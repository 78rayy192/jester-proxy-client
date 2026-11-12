using System.Diagnostics;
using System.IO.Compression;
using System.Net.Http;
using System.Runtime.InteropServices;
using System.Text.Json;

namespace Jester.Host.Services;

public sealed class CoreDownloader
{
    static readonly HttpClient Http = CreateClient();
    public int Progress { get; private set; }
    public string Stage { get; private set; } = "idle";
    int _busy;
    public bool Busy => Volatile.Read(ref _busy) != 0;

    public async Task InstallAsync(string channel, CancellationToken ct = default)
    {
        if (Interlocked.CompareExchange(ref _busy, 1, 0) != 0)
            throw new InvalidOperationException("Загрузка уже идёт.");
        Progress = 0;
        try
        {
            AppPaths.Ensure();
            Stage = "release";
            var assetUrl = await ResolveMihomoUrl(channel, ct);
            var zipPath = Path.Combine(AppPaths.Cores, "mihomo.zip");
            Stage = "core";
            await DownloadFile(assetUrl, zipPath, ct);
            ExtractExe(zipPath, AppPaths.MihomoExe);
            TryDelete(zipPath);

            if (!File.Exists(AppPaths.WintunDll))
            {
                Stage = "wintun";
                await InstallWintun(ct);
            }

            Progress = 100;
            Stage = "ready";
        }
        catch (OperationCanceledException)
        {
            Stage = "cancelled";
            throw;
        }
        catch { Stage = "error"; throw; }
        finally { Volatile.Write(ref _busy, 0); }
    }

    async Task<string> ResolveMihomoUrl(string channel, CancellationToken ct)
    {
        using var req = new HttpRequestMessage(HttpMethod.Get,
            "https://api.github.com/repos/MetaCubeX/mihomo/releases/latest");
        using var res = await Http.SendAsync(req, ct);
        res.EnsureSuccessStatusCode();
        using var doc = JsonDocument.Parse(await res.Content.ReadAsStringAsync(ct));
        var assets = doc.RootElement.GetProperty("assets");
        var arch = RuntimeInformation.ProcessArchitecture == Architecture.Arm64 ? "arm64" : "amd64";
        string? best = null;
        string? fallback = null;
        foreach (var asset in assets.EnumerateArray())
        {
            var name = asset.GetProperty("name").GetString() ?? "";
            var url = asset.GetProperty("browser_download_url").GetString() ?? "";
            if (!name.EndsWith(".zip", StringComparison.OrdinalIgnoreCase)) continue;
            if (name.Contains("-go12", StringComparison.OrdinalIgnoreCase)) continue;
            if (!name.StartsWith($"mihomo-windows-{arch}", StringComparison.OrdinalIgnoreCase)) continue;

            if (channel is "v3" or "amd64-v3")
            {
                if (name.Contains($"windows-{arch}-v3-", StringComparison.OrdinalIgnoreCase)) return url;
            }
            else if (channel == "compatible")
            {
                if (name.Contains($"windows-{arch}-compatible-", StringComparison.OrdinalIgnoreCase)) return url;
            }
            else if (LooksLikeBaselineAmd64(name, arch))
            {
                best = url;
            }

            if (LooksLikeBaselineAmd64(name, arch)) fallback = url;
            if (name.Contains($"windows-{arch}-compatible-", StringComparison.OrdinalIgnoreCase) && fallback == null)
                fallback = url;
        }
        return best ?? fallback ?? throw new InvalidOperationException("Не найден пакет mihomo для этой архитектуры.");
    }

    static bool LooksLikeBaselineAmd64(string name, string arch)
    {
        var prefix = $"mihomo-windows-{arch}-v";
        if (!name.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) return false;
        var rest = name[prefix.Length..];
        return rest.Length > 0 && char.IsDigit(rest[0]) && rest.Contains('.');
    }

    async Task InstallWintun(CancellationToken ct)
    {
        var zipPath = Path.Combine(AppPaths.Cores, "wintun.zip");
        await DownloadFile("https://www.wintun.net/builds/wintun-0.14.1.zip", zipPath, ct);
        var arch = RuntimeInformation.ProcessArchitecture == Architecture.Arm64 ? "arm64" : "amd64";
        try
        {
            using var zip = ZipFile.OpenRead(zipPath);
            var entry = zip.Entries.FirstOrDefault(e =>
                e.FullName.Replace('\\', '/').EndsWith($"bin/{arch}/wintun.dll", StringComparison.OrdinalIgnoreCase))
                ?? throw new InvalidOperationException("wintun.dll не найден в архиве.");
            ExtractBinary(entry, AppPaths.WintunDll);
        }
        finally { TryDelete(zipPath); }
    }

    async Task DownloadFile(string url, string dest, CancellationToken ct)
    {
        var temp = dest + ".download";
        try
        {
            using var res = await Http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, ct);
            res.EnsureSuccessStatusCode();
            var total = res.Content.Headers.ContentLength ?? 0;
            await using var input = await res.Content.ReadAsStreamAsync(ct);
            long read = 0;
            await using (var output = File.Create(temp))
            {
                var buffer = new byte[81920];
                int n;
                while ((n = await input.ReadAsync(buffer, ct)) > 0)
                {
                    await output.WriteAsync(buffer.AsMemory(0, n), ct);
                    read += n;
                    if (total > 0) Progress = Math.Clamp((int)(read * 90 / total), 0, 90);
                }
            }
            if (read == 0 || total > 0 && read != total)
                throw new InvalidDataException("Архив ядра загружен не полностью.");
            ct.ThrowIfCancellationRequested();
            File.Move(temp, dest, true);
        }
        finally { TryDelete(temp); }
    }

    static void ExtractExe(string zipPath, string destExe)
    {
        using var zip = ZipFile.OpenRead(zipPath);
        var entry = zip.Entries.FirstOrDefault(e =>
            e.Name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
            ?? throw new InvalidOperationException("В архиве нет exe ядра.");
        ExtractBinary(entry, destExe);
    }

    static void ExtractBinary(ZipArchiveEntry entry, string destination)
    {
        var temp = destination + ".new";
        try
        {
            entry.ExtractToFile(temp, true);
            using (var file = File.OpenRead(temp))
                if (file.Length < 64 || file.ReadByte() != 'M' || file.ReadByte() != 'Z')
                    throw new InvalidDataException("В архиве нет корректного исполняемого файла Windows.");
            File.Move(temp, destination, true);
        }
        finally { TryDelete(temp); }
    }

    static void TryDelete(string path)
    {
        try { File.Delete(path); } catch { /* ignore */ }
    }

    static HttpClient CreateClient()
    {
        var http = new HttpClient { Timeout = TimeSpan.FromMinutes(5) };
        http.DefaultRequestHeaders.UserAgent.ParseAdd("JesterProxyClient/0.1");
        return http;
    }
}
