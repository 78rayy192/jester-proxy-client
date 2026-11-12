using System.Net.Http;
using System.Reflection;
using System.Text.Json;

namespace Jester.Host.Services;

public static class AppUpdateChecker
{
    const string Repo = "78rayy192/jester-proxy-client";
    static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(20) };

    public static string CurrentVersion =>
        Assembly.GetExecutingAssembly().GetName().Version?.ToString(3) ?? "0.0.0";

    public static async Task<object> CheckAsync(CancellationToken ct = default)
    {
        using var req = new HttpRequestMessage(HttpMethod.Get, $"https://api.github.com/repos/{Repo}/releases/latest");
        req.Headers.TryAddWithoutValidation("User-Agent", "Jester/" + CurrentVersion);
        req.Headers.TryAddWithoutValidation("Accept", "application/vnd.github+json");
        using var res = await Http.SendAsync(req, ct);
        if (!res.IsSuccessStatusCode)
            return new { current = CurrentVersion, updateAvailable = false, message = "Не удалось проверить обновления." };

        using var doc = JsonDocument.Parse(await res.Content.ReadAsStringAsync(ct));
        var root = doc.RootElement;
        var tag = root.TryGetProperty("tag_name", out var t) ? (t.GetString() ?? "").TrimStart('v') : "";
        var url = root.TryGetProperty("html_url", out var u) ? u.GetString() ?? "" : "";
        var name = root.TryGetProperty("name", out var n) ? n.GetString() ?? tag : tag;
        string? zip = null;
        if (root.TryGetProperty("assets", out var assets) && assets.ValueKind == JsonValueKind.Array)
        {
            foreach (var a in assets.EnumerateArray())
            {
                var an = a.TryGetProperty("name", out var aname) ? aname.GetString() ?? "" : "";
                if (!an.EndsWith(".zip", StringComparison.OrdinalIgnoreCase)) continue;
                zip = a.TryGetProperty("browser_download_url", out var dl) ? dl.GetString() : null;
                break;
            }
        }

        var newer = IsNewer(tag, CurrentVersion);
        return new
        {
            current = CurrentVersion,
            latest = tag,
            name,
            updateAvailable = newer,
            releaseUrl = url,
            downloadUrl = zip,
            message = newer ? $"Доступна версия {tag}." : "Установлена актуальная версия."
        };
    }

    static bool IsNewer(string latest, string current)
    {
        if (!Version.TryParse(Normalize(latest), out var a)) return false;
        if (!Version.TryParse(Normalize(current), out var b)) return true;
        return a > b;
    }

    static string Normalize(string v)
    {
        v = v.Trim().TrimStart('v', 'V');
        var parts = v.Split('.', StringSplitOptions.RemoveEmptyEntries);
        while (parts.Length < 3) v += ".0";
        return string.Join('.', v.Split('.').Take(4));
    }
}
