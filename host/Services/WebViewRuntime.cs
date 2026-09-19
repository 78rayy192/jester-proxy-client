using System.Net.Http;
using Microsoft.Web.WebView2.Core;

namespace Jester.Host.Services;

/// <summary>
/// Finds a WebView2 runtime already on disk. A missing registry version
/// must not force a download — the binaries are often already installed.
/// </summary>
public static class WebViewRuntime
{
    const string BootstrapUrl = "https://go.microsoft.com/fwlink/p/?LinkId=2124703";

    public static string UserDataFolder
    {
        get
        {
            var dir = Path.Combine(AppPaths.Root, "webview2");
            Directory.CreateDirectory(dir);
            return dir;
        }
    }

    public static string? BundledFolder()
    {
        var dir = Path.Combine(AppContext.BaseDirectory, "webview2");
        return File.Exists(Path.Combine(dir, "msedgewebview2.exe")) ? dir : null;
    }

    public static string? FindInstalledFolder()
    {
        var bundled = BundledFolder();
        if (bundled != null) return bundled;

        string? best = null;
        Version bestVersion = new(0, 0);
        foreach (var root in InstallRoots())
        {
            if (!Directory.Exists(root)) continue;
            foreach (var dir in Directory.EnumerateDirectories(root))
            {
                if (!File.Exists(Path.Combine(dir, "msedgewebview2.exe"))) continue;
                if (!Version.TryParse(Path.GetFileName(dir), out var version)) version = new Version(0, 0);
                if (version < bestVersion) continue;
                bestVersion = version;
                best = dir;
            }
        }
        return best;
    }

    public static bool RegistryInstalled()
    {
        try
        {
            return !string.IsNullOrWhiteSpace(CoreWebView2Environment.GetAvailableBrowserVersionString());
        }
        catch
        {
            return false;
        }
    }

    /// <summary>Folder for CreateAsync, or null when the default installed runtime can be used.</summary>
    public static async Task<string?> EnsureAsync(Action<string>? status = null, CancellationToken ct = default)
    {
        var installed = FindInstalledFolder();
        if (installed != null || RegistryInstalled()) return installed;

        status?.Invoke("Инициализация графического движка");
        var dest = Path.Combine(Path.GetTempPath(), "JesterWebView2Setup.exe");
        using var handler = new HttpClientHandler { UseProxy = false };
        using var http = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(90) };
        using var res = await http.GetAsync(BootstrapUrl, HttpCompletionOption.ResponseHeadersRead, ct);
        res.EnsureSuccessStatusCode();
        var total = res.Content.Headers.ContentLength ?? 0;
        await using (var fs = File.Create(dest))
        await using (var src = await res.Content.ReadAsStreamAsync(ct))
        {
            var buf = new byte[81920];
            long read = 0;
            int n;
            while ((n = await src.ReadAsync(buf, ct)) > 0)
            {
                await fs.WriteAsync(buf.AsMemory(0, n), ct);
                read += n;
                status?.Invoke(total > 0
                    ? $"Загрузка компонента интерфейса — {Math.Clamp(read * 100 / total, 0, 100)}%"
                    : "Загрузка компонента интерфейса");
            }
        }

        status?.Invoke("Установка компонента интерфейса");
        var psi = new System.Diagnostics.ProcessStartInfo
        {
            FileName = dest,
            Arguments = "/silent /install",
            UseShellExecute = true
        };
        var proc = System.Diagnostics.Process.Start(psi)
            ?? throw new InvalidOperationException("Не удалось запустить установку компонента интерфейса.");
        using var installTimeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        installTimeout.CancelAfter(TimeSpan.FromMinutes(3));
        await proc.WaitForExitAsync(installTimeout.Token);

        return FindInstalledFolder();
    }

    static IEnumerable<string> InstallRoots()
    {
        yield return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), "Microsoft", "EdgeWebView", "Application");
        yield return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "Microsoft", "EdgeWebView", "Application");
        yield return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Microsoft", "EdgeWebView", "Application");
    }
}
