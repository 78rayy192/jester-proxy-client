namespace Jester.Host.Models;

public sealed class AppSettings
{
    public bool TunEnabled { get; set; } = true;
    public string TunStack { get; set; } = "mixed";
    public bool StrictRoute { get; set; }
    public bool AutoRoute { get; set; } = true;
    public bool AutoDetectInterface { get; set; } = true;
    public bool AutoRedirect { get; set; }
    public bool Ipv6 { get; set; }
    public bool EndpointIndependentNat { get; set; } = true;
    public int Mtu { get; set; } = 1400;
    public bool DnsHijack { get; set; } = true;
    public string Inet4Address { get; set; } = "172.19.0.1/30";

    public int MixedPort { get; set; } = 7890;
    public int SocksPort { get; set; } = 7891;
    public int RedirPort { get; set; }
    public int TproxyPort { get; set; }
    public int ExternalPort { get; set; } = 9090;
    public bool AllowLan { get; set; }
    public string BindAddress { get; set; } = "127.0.0.1";
    public string Mode { get; set; } = "rule";
    public string LogLevel { get; set; } = "warning";

    public bool SystemProxy { get; set; }
    public bool AutoStart { get; set; }
    public bool CloseToTray { get; set; } = true;
    public bool StartHidden { get; set; }
    public bool UnifiedDelay { get; set; } = true;
    public bool TcpConcurrent { get; set; } = true;
    public bool KeepAlive { get; set; } = true;
    public string FindProcessMode { get; set; } = "off";
    public string GlobalClientFingerprint { get; set; } = "chrome";
    public string LatencyUrl { get; set; } = "https://www.gstatic.com/generate_204";
    public int LatencyTimeoutMs { get; set; } = 5000;

    /// <summary>Blocked list (Re:filter) → PROXY, everything else → DIRECT.</summary>
    public bool RefilterEnabled { get; set; } = true;
    public bool RefilterUseIp { get; set; } = true;
    public string RefilterDomainsUrl { get; set; } =
        "https://github.com/1andrevich/Re-filter-lists/releases/latest/download/domains_all.lst";
    public string RefilterIpUrl { get; set; } =
        "https://github.com/1andrevich/Re-filter-lists/releases/latest/download/ipsum.lst";
    /// <summary>Extra domains through VPN, one per line.</summary>
    public string CustomProxyRules { get; set; } = "";
    /// <summary>Extra domains direct, one per line.</summary>
    public string CustomDirectRules { get; set; } = "";
    /// <summary>Final MATCH target: DIRECT or PROXY.</summary>
    public string FinalOutbound { get; set; } = "DIRECT";

    public DnsSettings Dns { get; set; } = new();
    public AppearanceSettings Appearance { get; set; } = new();
    public string Language { get; set; } = "ru";
    public string ActiveProfileId { get; set; } = "";
    public string CoreChannel { get; set; } = "v3";
    public bool SnifferEnabled { get; set; } = true;
    public bool StoreSelected { get; set; } = true;
    public bool HighPriority { get; set; } = true;

    /// <summary>Jester Shield: anti-QUIC, private DNS, tighter leaks, chrome fingerprint.</summary>
    public bool StealthMode { get; set; } = true;
    /// <summary>Block UDP/443 (QUIC) so DPI sees normal TLS via proxy.</summary>
    public bool BlockQuic { get; set; } = true;
    /// <summary>DNS follows routing rules (queries for blocked domains go via PROXY).</summary>
    public bool DnsRespectRules { get; set; } = true;
    /// <summary>Block STUN/TURN — closes WebRTC IP leaks.</summary>
    public bool BlockStun { get; set; } = true;
    /// <summary>Resolve DoH through PROXY (except bootstrap) — DNS invisible to ISP.</summary>
    public bool DnsViaProxy { get; set; } = true;
    /// <summary>Rotate TLS client fingerprint (random).</summary>
    public bool RandomFingerprint { get; set; } = true;
    /// <summary>log-level silent — no disk footprint from core logs.</summary>
    public bool SilentLogs { get; set; } = true;
    /// <summary>Binary .mrs rule-sets — much faster matching than text lists.</summary>
    public bool UseMrsRules { get; set; } = true;
    /// <summary>Hide window from screenshots / screen capture (WDA_EXCLUDEFROMCAPTURE).</summary>
    public bool HideFromCapture { get; set; } = true;

    /// <summary>
    /// РФ Rush: SPEED-lane (Hysteria2/TUIC/VLESS), GEOIP RU→DIRECT, emergency blocked domains.
    /// Built for Russian DPI — uncommon as a one-toggle product mode.
    /// </summary>
    public bool RfRushMode { get; set; } = true;
    /// <summary>After rush enable, prefer RUSH url-test group as default PROXY pick.</summary>
    public bool PreferRushLane { get; set; } = true;

    public string RefilterDomainsMrsUrl { get; set; } =
        "https://github.com/legiz-ru/mihomo-rule-sets/raw/main/re-filter/domain-rule.mrs";
    public string RefilterIpMrsUrl { get; set; } =
        "https://github.com/legiz-ru/mihomo-rule-sets/raw/main/re-filter/ip-rule.mrs";

    public bool AutoUpdateSubscriptions { get; set; } = true;
    public int AutoUpdateIntervalHours { get; set; } = 6;
    public DateTimeOffset? LastSubscriptionUpdateAt { get; set; }
    public List<string> FavoriteNodes { get; set; } = [];
    public string LastSelectedNode { get; set; } = "";
    public bool FirstRunCompleted { get; set; }
    public bool ConnectionHighlight { get; set; } = true;
    public bool AutoRestartCore { get; set; } = true;
    /// <summary>speed | stability | protection | custom</summary>
    public string PerformancePreset { get; set; } = "custom";

    /// <summary>auto | home | mobile | corporate</summary>
    public string NetworkPersona { get; set; } = "auto";

    /// <summary>Switch RUSH↔AUTO by live RTT.</summary>
    public bool AdaptiveLane { get; set; } = true;
    public int AdaptiveLaneBadRttMs { get; set; } = 280;
    public int AdaptiveLaneGoodRttMs { get; set; } = 140;
    public int AdaptiveLaneIntervalSec { get; set; } = 45;

    /// <summary>Selected processes always take PROXY/RUSH.</summary>
    public bool PerAppFastPath { get; set; } = true;
    public string FastPathProcesses { get; set; } =
        "chrome.exe\nmsedge.exe\nfirefox.exe\nbrave.exe\ndiscord.exe\nTelegram.exe\nCursor.exe\nCode.exe";

    public bool WarmStart { get; set; } = true;

    /// <summary>Game/VoIP UDP ports → UDP-FAST group; other UDP via PROXY.</summary>
    public bool SplitUdpPolicy { get; set; } = true;
    public string UdpFastPorts { get; set; } = "3074,5000-5020,27015-27050,27000-27100,3659,7000-7500";

    /// <summary>Jitter keep-alive / MTU to reduce identical client fingerprint.</summary>
    public bool TrafficPadding { get; set; } = true;

    public bool DnsDiversity { get; set; } = true;
    public bool DnsStrictNoFallback { get; set; }
    public int DnsRotateHours { get; set; } = 12;
    public int DnsPoolIndex { get; set; }
    public DateTimeOffset? LastDnsRotateAt { get; set; }

    public bool ExitConsistencyCheck { get; set; } = true;
    public int ExitCheckIntervalSec { get; set; } = 120;
    /// <summary>switch | panic | warn</summary>
    public string ExitCheckAction { get; set; } = "switch";
    public string? LastKnownExitIp { get; set; }
    public string? LastKnownExitOrg { get; set; }

    /// <summary>If core dies, fail-closed until restart succeeds or user disarms.</summary>
    public bool DeadManSwitch { get; set; } = true;
    public bool DeadManArmed { get; set; }

    public bool SmartDirectMap { get; set; } = true;
    public int SmartDirectMinHits { get; set; } = 5;
    public Dictionary<string, int> SmartDirectHits { get; set; } = new();
    public List<string> SmartDirectDomains { get; set; } = [];

    public bool LatencyBudgetEnabled { get; set; } = true;
    public int LatencyBudgetMs { get; set; } = 250;
    /// <summary>switch | warn</summary>
    public string LatencyBudgetAction { get; set; } = "switch";

    /// <summary>Runtime telemetry for UI (not critical if lost).</summary>
    public string AdaptiveLaneState { get; set; } = "idle";
    public int LastMeasuredRttMs { get; set; }
    public string GuardianMessage { get; set; } = "";
}

public sealed class DnsSettings
{
    public bool Enable { get; set; } = true;
    public bool Ipv6 { get; set; }
    public bool UseHosts { get; set; } = true;
    public string EnhancedMode { get; set; } = "fake-ip";
    public string FakeIpRange { get; set; } = "198.18.0.1/16";
    public List<string> Nameserver { get; set; } =
    [
        "https://1.1.1.1/dns-query",
        "https://dns.google/dns-query"
    ];
    public List<string> Fallback { get; set; } =
    [
        "https://security.cloudflare-dns.com/dns-query",
        "tls://1.1.1.1"
    ];
    public List<string> DefaultNameserver { get; set; } = ["1.1.1.1", "8.8.8.8"];
    public List<string> FakeIpFilter { get; set; } =
    [
        "*.lan", "*.local", "localhost.ptlogin2.qq.com", "+.stun.*.*", "+.stun.*.*.*",
        "+.xboxlive.com", "lens.l.google.com"
    ];
}

public sealed class AppearanceSettings
{
    public string Theme { get; set; } = "liquid";
    public string Accent { get; set; } = "#b8d4ff";
    public string AccentMuted { get; set; } = "#7d9cce";
    public string Background { get; set; } = "#0b101b";
    public string Surface { get; set; } = "#182235";
    public string Text { get; set; } = "#f2f6ff";
    public string BackgroundStyle { get; set; } = "mesh";
    public string? BackgroundImage { get; set; }
    public bool Glass { get; set; } = true;
    public int GlassBlur { get; set; } = 6;
    public double GlassTintOpacity { get; set; } = 0.1;
    public int Radius { get; set; } = 22;
    public double WindowOpacity { get; set; } = 1;
    public string FontUi { get; set; } = "Segoe UI";
    public string FontDisplay { get; set; } = "Segoe UI";
    public string Sidebar { get; set; } = "full";
    public bool Animations { get; set; } = true;
    public bool TrafficGraph { get; set; } = true;
    public string CustomCss { get; set; } = "";
}

public sealed class ProfileInfo
{
    public string Id { get; set; } = "";
    public string Name { get; set; } = "";
    public string Kind { get; set; } = "paste";
    public string Source { get; set; } = "";
    public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.UtcNow;
    public long Bytes { get; set; }
    public long Upload { get; set; }
    public long Download { get; set; }
    public long Total { get; set; }
    public long ExpireUnix { get; set; }
    public bool HwidLimited { get; set; }
    public int UpdateIntervalHours { get; set; }
    /// <summary>Session isolation: override TLS fingerprint for this profile.</summary>
    public string? FingerprintOverride { get; set; }
    /// <summary>Session isolation: override DoH nameservers (one per line / list).</summary>
    public List<string>? NameserverOverride { get; set; }
}

public sealed record CoreStatus
{
    public bool Running { get; set; }
    public bool CoreInstalled { get; set; }
    public bool WintunInstalled { get; set; }
    public bool IsAdmin { get; set; }
    public bool NeedsAdmin { get; set; }
    public int Pid { get; set; }
    public string Version { get; set; } = "";
    public string AppVersion { get; set; } = "";
    public string Message { get; set; } = "";
    public int DownloadProgress { get; set; }
    public string ActiveProfileId { get; set; } = "";
    public string Mode { get; set; } = "rule";
}

public sealed class CreateProfileRequest
{
    public string Name { get; set; } = "Профиль";
    public string Kind { get; set; } = "paste";
    public string Payload { get; set; } = "";
    public string Source { get; set; } = "";
}
