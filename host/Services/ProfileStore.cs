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

    public IReadOnlyList<ProfileInfo> List()
    {
        AppPaths.Ensure();
        var list = new List<ProfileInfo>();
        foreach (var dir in Directory.GetDirectories(AppPaths.Profiles))
        {
            var meta = Path.Combine(dir, "meta.json");
            if (!File.Exists(meta)) continue;
            try
            {
                var info = JsonSerializer.Deserialize<ProfileInfo>(File.ReadAllText(meta), Json);
                if (info != null) list.Add(info);
            }
            catch { /* skip broken profile */ }
        }
        return list.OrderByDescending(p => p.UpdatedAt).ToList();
    }

    public ProfileInfo? Get(string id)
    {
        var meta = MetaPath(id);
        if (!File.Exists(meta)) return null;
        return JsonSerializer.Deserialize<ProfileInfo>(File.ReadAllText(meta), Json);
    }

    public string ConfigPath(string id) => Path.Combine(AppPaths.Profiles, id, "config.yaml");

    public async Task<ProfileInfo> CreateAsync(CreateProfileRequest req, CancellationToken ct = default)
    {
        var id = Guid.NewGuid().ToString("n")[..12];
        var dir = Path.Combine(AppPaths.Profiles, id);
        Directory.CreateDirectory(dir);

        var body = req.Kind == "subscription"
            ? await DownloadAsync(req.Source, ct)
            : req.Payload;

        if (string.IsNullOrWhiteSpace(body))
            throw new InvalidOperationException("Пустой профиль: нет YAML/ссылки.");

        body = SubscriptionNormalizer.ToClashYaml(body);

        var configPath = Path.Combine(dir, "config.yaml");
        await File.WriteAllTextAsync(configPath, body, ct);

        var info = new ProfileInfo
        {
            Id = id,
            Name = string.IsNullOrWhiteSpace(req.Name) ? "Профиль" : req.Name.Trim(),
            Kind = req.Kind,
            Source = req.Source ?? "",
            UpdatedAt = DateTimeOffset.UtcNow,
            Bytes = new FileInfo(configPath).Length
        };
        await File.WriteAllTextAsync(MetaPath(id), JsonSerializer.Serialize(info, Json), ct);
        return info;
    }

    public async Task<ProfileInfo> UpdateSubscriptionAsync(string id, CancellationToken ct = default)
    {
        var info = Get(id) ?? throw new FileNotFoundException("Профиль не найден", id);
        if (info.Kind != "subscription" || string.IsNullOrWhiteSpace(info.Source))
            throw new InvalidOperationException("Это не подписка.");

        var body = await DownloadAsync(info.Source, ct);
        body = SubscriptionNormalizer.ToClashYaml(body);
        await File.WriteAllTextAsync(ConfigPath(id), body, ct);
        info.UpdatedAt = DateTimeOffset.UtcNow;
        info.Bytes = new FileInfo(ConfigPath(id)).Length;
        await File.WriteAllTextAsync(MetaPath(id), JsonSerializer.Serialize(info, Json), ct);
        return info;
    }

    public ProfileInfo Rename(string id, string name)
    {
        var info = Get(id) ?? throw new FileNotFoundException("Профиль не найден", id);
        info.Name = name.Trim();
        File.WriteAllText(MetaPath(id), JsonSerializer.Serialize(info, Json));
        return info;
    }

    public void Delete(string id)
    {
        var dir = Path.Combine(AppPaths.Profiles, id);
        if (Directory.Exists(dir)) Directory.Delete(dir, true);
    }

    static string MetaPath(string id) => Path.Combine(AppPaths.Profiles, id, "meta.json");

    static async Task<string> DownloadAsync(string url, CancellationToken ct)
    {
        url = UnwrapInstallLink(url);
        using var req = new HttpRequestMessage(HttpMethod.Get, url);
        req.Headers.TryAddWithoutValidation("User-Agent", "clash.meta/v1.19.30");
        req.Headers.TryAddWithoutValidation("Accept", "*/*");
        using var res = await Http.SendAsync(req, ct);
        res.EnsureSuccessStatusCode();
        var body = await res.Content.ReadAsStringAsync(ct);
        if (string.IsNullOrWhiteSpace(body))
            throw new InvalidOperationException("Подписка вернула пустой ответ.");
        return body;
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
        var handler = new HttpClientHandler
        {
            ServerCertificateCustomValidationCallback = HttpClientHandler.DangerousAcceptAnyServerCertificateValidator
        };
        var http = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(30) };
        http.DefaultRequestHeaders.UserAgent.ParseAdd("clash.meta/v1.19.30");
        return http;
    }
}
