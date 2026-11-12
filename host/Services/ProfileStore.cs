using System.Net.Http;
using System.Text.Json;
using Jester.Host.Models;

namespace Jester.Host.Services;

public sealed class ProfileStore
{
    static readonly JsonSerializerOptions Json = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true
    };

    static readonly HttpClient Http = CreateClient();
    const long MaxSubscriptionBytes = 8 * 1024 * 1024;
    // Bounded striped locks coordinate API edits, auto-refresh, and deletion.
    // Downloads release the metadata lock so renames stay responsive.
    static readonly object[] MetadataGates = Enumerable.Range(0, 32).Select(_ => new object()).ToArray();
    static readonly SemaphoreSlim[] UpdateGates = Enumerable.Range(0, 32).Select(_ => new SemaphoreSlim(1, 1)).ToArray();

    public IReadOnlyList<ProfileInfo> List()
    {
        AppPaths.Ensure();
        var list = new List<ProfileInfo>();
        foreach (var dir in Directory.GetDirectories(AppPaths.Profiles))
        {
            var id = Path.GetFileName(dir);
            if (!IsValidId(id)) continue;
            var info = Get(id);
            if (info != null) list.Add(info);
        }
        return list.OrderByDescending(p => p.UpdatedAt).ToList();
    }

    public ProfileInfo? Get(string id)
    {
        if (!IsValidId(id)) return null;
        lock (MetadataGate(id))
        {
            var meta = MetaPath(id);
            if (!File.Exists(meta)) return null;
            try { return ReadMetadata(id, File.ReadAllText(meta)); }
            catch (IOException) { return null; }
            catch (UnauthorizedAccessException) { return null; }
        }
    }

    public string ConfigPath(string id)
    {
        EnsureValidId(id);
        return Path.Combine(AppPaths.Profiles, id, "config.yaml");
    }

    public async Task<ProfileInfo> CreateAsync(CreateProfileRequest req, CancellationToken ct = default)
    {
        var id = Guid.NewGuid().ToString("n")[..12];
        var dir = Path.Combine(AppPaths.Profiles, id);

        var info = new ProfileInfo
        {
            Id = id,
            Name = string.IsNullOrWhiteSpace(req.Name) ? "Профиль" : req.Name.Trim(),
            Kind = req.Kind == "subscription" ? "subscription" : "paste",
            Source = req.Source?.Trim() ?? "",
            UpdatedAt = DateTimeOffset.UtcNow
        };

        string body;
        if (info.Kind == "subscription")
        {
            var dl = await DownloadAsync(info.Source, ct);
            body = dl.Body;
            ApplyUserInfo(info, dl);
        }
        else body = req.Payload ?? "";

        if (string.IsNullOrWhiteSpace(body))
            throw new InvalidOperationException("Пустой профиль: нет YAML/ссылки.");

        body = SubscriptionNormalizer.ToClashYaml(body);

        ct.ThrowIfCancellationRequested();
        lock (MetadataGate(id))
        {
            ct.ThrowIfCancellationRequested();
            Directory.CreateDirectory(dir);
            var configPath = Path.Combine(dir, "config.yaml");
            AtomicFile.WriteAllText(configPath, body);
            info.Bytes = new FileInfo(configPath).Length;
            AtomicFile.WriteAllText(MetaPath(id), JsonSerializer.Serialize(info, Json));
        }
        return info;
    }

    public async Task<ProfileInfo> UpdateSubscriptionAsync(string id, CancellationToken ct = default)
    {
        EnsureValidId(id);
        var updateGate = UpdateGates[LockSlot(id)];
        await updateGate.WaitAsync(ct);
        try
        {
            var original = Get(id) ?? throw new FileNotFoundException("Профиль не найден", id);
            if (original.Kind != "subscription" || string.IsNullOrWhiteSpace(original.Source))
                throw new InvalidOperationException("Это не подписка.");

            var dl = await DownloadAsync(original.Source, ct);
            var body = SubscriptionNormalizer.ToClashYaml(dl.Body);
            ct.ThrowIfCancellationRequested();
            lock (MetadataGate(id))
            {
                ct.ThrowIfCancellationRequested();
                // Re-read after the network await: keep name, fingerprint, and DNS
                // changes made while the subscription was downloading.
                var current = Get(id) ?? throw new FileNotFoundException("Профиль удалён во время обновления", id);
                if (current.Kind != original.Kind || current.Source != original.Source)
                    throw new InvalidOperationException("Источник профиля изменился во время обновления. Повторите обновление.");
                AtomicFile.WriteAllText(ConfigPath(id), body);
                ApplyUserInfo(current, dl);
                current.UpdatedAt = DateTimeOffset.UtcNow;
                current.Bytes = new FileInfo(ConfigPath(id)).Length;
                AtomicFile.WriteAllText(MetaPath(id), JsonSerializer.Serialize(current, Json));
                return current;
            }
        }
        finally { updateGate.Release(); }
    }

    public async Task<(int Updated, int Failed, List<string> Errors)> UpdateAllSubscriptionsAsync(CancellationToken ct = default)
    {
        var updated = 0;
        var failed = 0;
        var errors = new List<string>();
        foreach (var p in List().Where(x => x.Kind == "subscription" && !string.IsNullOrWhiteSpace(x.Source)))
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                await UpdateSubscriptionAsync(p.Id, ct);
                updated++;
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
            catch (Exception ex)
            {
                failed++;
                errors.Add($"{p.Name}: {ex.Message}");
            }
        }
        return (updated, failed, errors);
    }

    public ProfileInfo Rename(string id, string name)
    {
        return Patch(id, info => info.Name = string.IsNullOrWhiteSpace(name) ? "Профиль" : name.Trim());
    }

    public ProfileInfo Patch(string id, Action<ProfileInfo> mutate)
    {
        EnsureValidId(id);
        lock (MetadataGate(id))
        {
            var info = Get(id) ?? throw new FileNotFoundException("Профиль не найден", id);
            mutate(info);
            info.Id = id;
            AtomicFile.WriteAllText(MetaPath(id), JsonSerializer.Serialize(info, Json));
            return info;
        }
    }

    public void Delete(string id)
    {
        EnsureValidId(id);
        var dir = Path.Combine(AppPaths.Profiles, id);
        var root = Path.GetFullPath(AppPaths.Profiles);
        var full = Path.GetFullPath(dir);
        if (!full.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)
            && !string.Equals(full, root, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("Некорректный id профиля.");
        lock (MetadataGate(id))
            if (Directory.Exists(dir)) Directory.Delete(dir, true);
    }

    public static bool IsValidId(string? id) =>
        id is { Length: 12 } && id.All(c => c is >= 'a' and <= 'f' or >= '0' and <= '9');

    static void EnsureValidId(string id)
    {
        if (!IsValidId(id)) throw new InvalidOperationException("Некорректный id профиля.");
    }

    static int LockSlot(string id) => (id[0] * 31 + id[^1]) % MetadataGates.Length;
    static object MetadataGate(string id) => MetadataGates[LockSlot(id)];

    internal static ProfileInfo? ReadMetadata(string id, string json)
    {
        if (!IsValidId(id)) return null;
        try
        {
            var info = JsonSerializer.Deserialize<ProfileInfo>(json, Json);
            if (info == null || !string.Equals(info.Id, id, StringComparison.Ordinal)) return null;
            info.Name = string.IsNullOrWhiteSpace(info.Name) ? "Профиль" : info.Name.Trim();
            info.Source ??= "";
            info.Kind = info.Kind == "subscription" ? "subscription" : "paste";
            return info;
        }
        catch (JsonException) { return null; }
    }

    static string MetaPath(string id)
    {
        EnsureValidId(id);
        return Path.Combine(AppPaths.Profiles, id, "meta.json");
    }

    static void ApplyUserInfo(ProfileInfo info, DownloadResult dl)
    {
        info.Upload = dl.Upload;
        info.Download = dl.Download;
        info.Total = dl.Total;
        info.ExpireUnix = dl.ExpireUnix;
        info.HwidLimited = dl.HwidLimited;
        info.UpdateIntervalHours = dl.UpdateIntervalHours;
    }

    static async Task<DownloadResult> DownloadAsync(string url, CancellationToken ct)
    {
        url = UnwrapInstallLink(url);
        if (!Uri.TryCreate(url, UriKind.Absolute, out var address) || address.Scheme is not ("http" or "https"))
            throw new InvalidOperationException("Укажите HTTP(S)-ссылку подписки.");
        using var req = new HttpRequestMessage(HttpMethod.Get, url);
        req.Headers.TryAddWithoutValidation("User-Agent", "Happ/3.13.0/Windows");
        req.Headers.TryAddWithoutValidation("Accept", "*/*");
        req.Headers.TryAddWithoutValidation("x-hwid", DeviceId.Current());
        req.Headers.TryAddWithoutValidation("x-device-os", "Windows");
        req.Headers.TryAddWithoutValidation("x-ver-os", Environment.OSVersion.Version.ToString());
        req.Headers.TryAddWithoutValidation("x-device-model", "Jester");
        using var res = await Http.SendAsync(req, ct);
        res.EnsureSuccessStatusCode();
        if (HeaderIs(res, "x-hwid-max-devices-reached"))
            throw new InvalidOperationException("Достигнут лимит устройств поставщика. Удалите неиспользуемое устройство в боте и обновите подписку.");

        var result = new DownloadResult
        {
            HwidLimited = HeaderIs(res, "x-hwid-limit") || HeaderIs(res, "x-hwid-not-supported")
        };
        if (TryHeader(res, "subscription-userinfo", out var userinfo))
            ParseUserInfo(userinfo, result);
        if (TryHeader(res, "profile-update-interval", out var interval)
            && int.TryParse(interval, out var hours) && hours > 0)
            result.UpdateIntervalHours = hours;

        result.Body = await res.Content.ReadAsStringAsync(ct);
        if (string.IsNullOrWhiteSpace(result.Body))
            throw new InvalidOperationException("Подписка вернула пустой ответ.");
        return result;
    }

    static void ParseUserInfo(string raw, DownloadResult into)
    {
        foreach (var part in raw.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var kv = part.Split('=', 2);
            if (kv.Length != 2) continue;
            var key = kv[0].Trim().ToLowerInvariant();
            if (!long.TryParse(kv[1].Trim(), out var n)) continue;
            switch (key)
            {
                case "upload": into.Upload = n; break;
                case "download": into.Download = n; break;
                case "total": into.Total = n; break;
                case "expire": into.ExpireUnix = n; break;
            }
        }
    }

    static string UnwrapInstallLink(string url)
    {
        url = url.Trim();
        foreach (var prefix in new[] { "happ://", "hiddify://", "clash://install-config?url=" })
        {
            if (!url.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) continue;
            var rest = url[prefix.Length..];
            if (rest.StartsWith("install-config?url=", StringComparison.OrdinalIgnoreCase))
                rest = rest["install-config?url=".Length..];
            rest = Uri.UnescapeDataString(rest);
            if (rest.StartsWith("http", StringComparison.OrdinalIgnoreCase)) return rest;
        }
        return url;
    }

    static HttpClient CreateClient()
    {
        var http = new HttpClient
        {
            Timeout = TimeSpan.FromSeconds(30),
            MaxResponseContentBufferSize = MaxSubscriptionBytes
        };
        return http;
    }

    static bool HeaderIs(HttpResponseMessage res, string name) =>
        TryHeader(res, name, out var v) && v.Equals("true", StringComparison.OrdinalIgnoreCase);

    static bool TryHeader(HttpResponseMessage res, string name, out string value)
    {
        if (res.Headers.TryGetValues(name, out var values))
        {
            value = values.FirstOrDefault() ?? "";
            return value.Length > 0;
        }
        if (res.Content.Headers.TryGetValues(name, out values))
        {
            value = values.FirstOrDefault() ?? "";
            return value.Length > 0;
        }
        value = "";
        return false;
    }

    sealed class DownloadResult
    {
        public string Body { get; set; } = "";
        public long Upload { get; set; }
        public long Download { get; set; }
        public long Total { get; set; }
        public long ExpireUnix { get; set; }
        public bool HwidLimited { get; set; }
        public int UpdateIntervalHours { get; set; }
    }
}
