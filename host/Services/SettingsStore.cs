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
                if (!json.Contains("rfRushMode", StringComparison.OrdinalIgnoreCase)) loaded.RfRushMode = true;
                if (!json.Contains("preferRushLane", StringComparison.OrdinalIgnoreCase)) loaded.PreferRushLane = true;
                if (loaded.Appearance != null) UpgradeLegacyAppearance(loaded.Appearance);
                lock (_gate) _current = Normalize(loaded);
            }
        }
        catch
        {
            // Preserve the unreadable file for recovery; a later explicit save
            // can replace it after the application has opened successfully.
            lock (_gate) _current = new();
        }
    }

    public AppSettings Save(AppSettings next)
    {
        lock (_gate)
        {
            // Preserve guardian/runtime fields so a stale UI PUT cannot wipe them.
            next.DeadManArmed = _current.DeadManArmed;
            next.SmartDirectHits = _current.SmartDirectHits;
            next.SmartDirectDomains = _current.SmartDirectDomains;
            next.DnsPoolIndex = _current.DnsPoolIndex;
            next.LastDnsRotateAt = _current.LastDnsRotateAt;
            next.LastKnownExitIp = _current.LastKnownExitIp;
            next.LastKnownExitOrg = _current.LastKnownExitOrg;
            next.AdaptiveLaneState = _current.AdaptiveLaneState;
            next.LastMeasuredRttMs = _current.LastMeasuredRttMs;
            next.GuardianMessage = _current.GuardianMessage;
            next.LastSubscriptionUpdateAt = next.LastSubscriptionUpdateAt ?? _current.LastSubscriptionUpdateAt;

            next = Normalize(Clone(next));
            if (next.AutoStart != _current.AutoStart) ApplyAutoStart(next.AutoStart);
            AtomicFile.WriteAllText(AppPaths.SettingsFile, JsonSerializer.Serialize(next, Json));
            _current = next;
            return Clone(_current);
        }
    }

    public AppSettings Patch(Action<AppSettings> mutate)
    {
        lock (_gate)
        {
            var next = Clone(_current);
            mutate(next);
            next = Normalize(next);
            if (next.AutoStart != _current.AutoStart) ApplyAutoStart(next.AutoStart);
            AtomicFile.WriteAllText(AppPaths.SettingsFile, JsonSerializer.Serialize(next, Json));
            _current = next;
            return Clone(_current);
        }
    }

    /// <summary>Allow guardian to set runtime fields that Save() otherwise preserves.</summary>
    public AppSettings PatchRuntime(Action<AppSettings> mutate) => Patch(mutate);

    internal static bool UpgradeLegacyAppearance(AppearanceSettings appearance)
    {
        // Upgrade only the former uncustomized palette. Keep personal themes,
        // wallpaper, typography, and layout choices intact.
        if (appearance.Theme != "dark"
            || !string.Equals(appearance.Background, "#090a0e", StringComparison.OrdinalIgnoreCase)
            || !string.Equals(appearance.Accent, "#e6c48a", StringComparison.OrdinalIgnoreCase)
            || !string.Equals(appearance.AccentMuted, "#a89068", StringComparison.OrdinalIgnoreCase)
            || !string.Equals(appearance.Surface, "#12131a", StringComparison.OrdinalIgnoreCase)
            || !string.Equals(appearance.Text, "#f7f3ec", StringComparison.OrdinalIgnoreCase)
            || !string.IsNullOrWhiteSpace(appearance.CustomCss)
            || !string.IsNullOrWhiteSpace(appearance.BackgroundImage)) return false;
        var liquid = new AppearanceSettings();
        appearance.Theme = liquid.Theme;
        appearance.Accent = liquid.Accent;
        appearance.AccentMuted = liquid.AccentMuted;
        appearance.Background = liquid.Background;
        appearance.Surface = liquid.Surface;
        appearance.Text = liquid.Text;
        appearance.Glass = liquid.Glass;
        appearance.GlassBlur = liquid.GlassBlur;
        if (appearance.Radius == 16) appearance.Radius = liquid.Radius;
        if (appearance.BackgroundStyle == "mesh") appearance.BackgroundStyle = liquid.BackgroundStyle;
        return true;
    }

    internal static AppSettings Normalize(AppSettings settings)
    {
        var ports = new[] { settings.MixedPort, settings.SocksPort, settings.ExternalPort };
        if (ports.Any(p => p is < 1 or > 65535 || p == JesterApi.Port) || ports.Distinct().Count() != ports.Length)
            throw new ArgumentException("Порты должны различаться, быть в диапазоне 1–65535 и не занимать порт приложения 37841.");
        settings.Dns ??= new();
        settings.Appearance ??= new();
        settings.Dns.Nameserver ??= new DnsSettings().Nameserver;
        settings.Dns.Fallback ??= [];
        settings.Dns.DefaultNameserver ??= new DnsSettings().DefaultNameserver;
        settings.Dns.FakeIpFilter ??= [];
        settings.FavoriteNodes ??= [];
        settings.SmartDirectDomains ??= [];
        settings.SmartDirectHits ??= new();
        settings.Mtu = Math.Clamp(settings.Mtu, 1280, 9000);
        settings.LatencyTimeoutMs = Math.Clamp(settings.LatencyTimeoutMs, 1000, 30000);
        settings.AutoUpdateIntervalHours = Math.Clamp(settings.AutoUpdateIntervalHours, 1, 168);
        settings.DnsRotateHours = Math.Clamp(settings.DnsRotateHours, 1, 168);
        settings.AdaptiveLaneIntervalSec = Math.Clamp(settings.AdaptiveLaneIntervalSec, 15, 3600);
        settings.ExitCheckIntervalSec = Math.Clamp(settings.ExitCheckIntervalSec, 30, 3600);
        settings.Appearance.Radius = Math.Clamp(settings.Appearance.Radius, 0, 28);
        settings.Appearance.GlassBlur = Math.Clamp(settings.Appearance.GlassBlur, 0, 6);
        settings.Appearance.GlassTintOpacity = double.IsFinite(settings.Appearance.GlassTintOpacity)
            ? Math.Clamp(settings.Appearance.GlassTintOpacity, 0.06, 0.28) : 0.1;
        var defaults = new AppearanceSettings();
        static string Color(string? value, string fallback) => value != null
            && System.Text.RegularExpressions.Regex.IsMatch(value, "\\A#[a-fA-F0-9]{6}\\z") ? value : fallback;
        var appearance = settings.Appearance;
        appearance.Background = Color(appearance.Background, defaults.Background);
        appearance.Text = Color(appearance.Text, defaults.Text);
        appearance.Accent = Color(appearance.Accent, defaults.Accent);
        appearance.AccentMuted = Color(appearance.AccentMuted, defaults.AccentMuted);
        appearance.Surface = Color(appearance.Surface, defaults.Surface);
        appearance.FontUi = string.IsNullOrWhiteSpace(appearance.FontUi) ? defaults.FontUi : appearance.FontUi;
        appearance.FontDisplay = string.IsNullOrWhiteSpace(appearance.FontDisplay) ? defaults.FontDisplay : appearance.FontDisplay;
        settings.Appearance.WindowOpacity = double.IsFinite(settings.Appearance.WindowOpacity)
            ? Math.Clamp(settings.Appearance.WindowOpacity, 0.92, 1) : 1;
        settings.Mode = settings.Mode is "rule" or "global" or "direct" ? settings.Mode : "rule";
        settings.LogLevel = settings.LogLevel is "silent" or "error" or "warning" or "info" or "debug" ? settings.LogLevel : "warning";
        settings.TunStack = settings.TunStack is "system" or "gvisor" or "mixed" ? settings.TunStack : "mixed";
        settings.Dns.EnhancedMode = settings.Dns.EnhancedMode is "fake-ip" or "redir-host" ? settings.Dns.EnhancedMode : "fake-ip";
        settings.Dns.FakeIpRange ??= "198.18.0.1/16";
        settings.Inet4Address ??= "172.19.0.1/30";
        settings.CoreChannel ??= "v3";
        settings.LatencyUrl = string.IsNullOrWhiteSpace(settings.LatencyUrl) ? "https://www.gstatic.com/generate_204" : settings.LatencyUrl;
        settings.ActiveProfileId ??= "";
        settings.FastPathProcesses ??= "";
        settings.UdpFastPorts ??= "";
        settings.CustomDirectRules ??= "";
        settings.CustomProxyRules ??= "";
        return settings;
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
