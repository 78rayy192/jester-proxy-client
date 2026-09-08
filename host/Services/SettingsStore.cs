using System.Text.Json;
using Jester.Host.Models;
using Microsoft.Win32;

namespace Jester.Host.Services;

public sealed class SettingsStore
{
    static readonly JsonSerializerOptions Json = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true
    };

    readonly object _gate = new();
    AppSettings _current = new();

    public AppSettings Current
    {
        get { lock (_gate) return Clone(_current); }
    }

    public void Load()
    {
        AppPaths.Ensure();
        if (!File.Exists(AppPaths.SettingsFile))
        {
            Save(_current);
            return;
        }

        try
        {
            var json = File.ReadAllText(AppPaths.SettingsFile);
            var loaded = JsonSerializer.Deserialize<AppSettings>(json, Json);
            if (loaded != null)
            {
                if (!json.Contains("snifferEnabled", StringComparison.OrdinalIgnoreCase)) loaded.SnifferEnabled = true;
                if (!json.Contains("storeSelected", StringComparison.OrdinalIgnoreCase)) loaded.StoreSelected = true;
                if (!json.Contains("highPriority", StringComparison.OrdinalIgnoreCase)) loaded.HighPriority = true;
                if (!json.Contains("refilterEnabled", StringComparison.OrdinalIgnoreCase)) loaded.RefilterEnabled = true;
                if (!json.Contains("refilterUseIp", StringComparison.OrdinalIgnoreCase)) loaded.RefilterUseIp = true;
                if (!json.Contains("finalOutbound", StringComparison.OrdinalIgnoreCase)) loaded.FinalOutbound = "DIRECT";
                if (!json.Contains("\"mtu\"", StringComparison.OrdinalIgnoreCase)) loaded.Mtu = 1400;
                if (!json.Contains("strictRoute", StringComparison.OrdinalIgnoreCase)) loaded.StrictRoute = false;
                if (!json.Contains("autoRedirect", StringComparison.OrdinalIgnoreCase)) loaded.AutoRedirect = false;
                if (!json.Contains("endpointIndependentNat", StringComparison.OrdinalIgnoreCase)) loaded.EndpointIndependentNat = true;
                if (string.IsNullOrWhiteSpace(loaded.RefilterDomainsUrl))
                    loaded.RefilterDomainsUrl = "https://github.com/1andrevich/Re-filter-lists/releases/latest/download/domains_all.lst";
                if (string.IsNullOrWhiteSpace(loaded.RefilterIpUrl))
                    loaded.RefilterIpUrl = "https://github.com/1andrevich/Re-filter-lists/releases/latest/download/ipsum.lst";
                if (!json.Contains("stealthMode", StringComparison.OrdinalIgnoreCase)) loaded.StealthMode = true;
                if (!json.Contains("blockQuic", StringComparison.OrdinalIgnoreCase)) loaded.BlockQuic = true;
                if (!json.Contains("dnsRespectRules", StringComparison.OrdinalIgnoreCase)) loaded.DnsRespectRules = true;
                if (!json.Contains("blockStun", StringComparison.OrdinalIgnoreCase)) loaded.BlockStun = true;
                if (!json.Contains("dnsViaProxy", StringComparison.OrdinalIgnoreCase)) loaded.DnsViaProxy = true;
                if (!json.Contains("randomFingerprint", StringComparison.OrdinalIgnoreCase)) loaded.RandomFingerprint = true;
                if (!json.Contains("silentLogs", StringComparison.OrdinalIgnoreCase)) loaded.SilentLogs = true;
                if (!json.Contains("useMrsRules", StringComparison.OrdinalIgnoreCase)) loaded.UseMrsRules = true;
                if (!json.Contains("hideFromCapture", StringComparison.OrdinalIgnoreCase)) loaded.HideFromCapture = true;
                lock (_gate) _current = loaded;
            }
        }
        catch
        {
            Save(_current);
        }
    }

    public AppSettings Save(AppSettings next)
    {
        lock (_gate)
        {
            _current = Clone(next);
            File.WriteAllText(AppPaths.SettingsFile, JsonSerializer.Serialize(_current, Json));
            ApplyAutoStart(_current.AutoStart);
            return Clone(_current);
        }
    }

    public AppSettings Patch(Action<AppSettings> mutate)
    {
        var copy = Current;
        mutate(copy);
        return Save(copy);
    }

    static AppSettings Clone(AppSettings s) =>
        JsonSerializer.Deserialize<AppSettings>(JsonSerializer.Serialize(s, Json), Json) ?? new();

    static void ApplyAutoStart(bool enable)
    {
        using var key = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Run", true);
        if (key == null) return;
        const string name = "JesterProxy";
        if (enable && Environment.ProcessPath is string path)
            key.SetValue(name, $"\"{path}\"");
        else
            key.DeleteValue(name, false);
    }
}
