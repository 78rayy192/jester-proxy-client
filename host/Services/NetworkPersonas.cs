using Jester.Host.Models;

namespace Jester.Host.Services;

/// <summary>Named network postures — MTU, TLS, DNS, rush/stealth bundles.</summary>
public static class NetworkPersonas
{
    public static readonly string[] DnsPool =
    [
        "https://1.1.1.1/dns-query",
        "https://1.0.0.1/dns-query",
        "https://dns.google/dns-query",
        "https://8.8.8.8/dns-query",
        "https://9.9.9.9/dns-query",
        "https://149.112.112.112/dns-query",
        "https://dns.quad9.net/dns-query",
        "https://security.cloudflare-dns.com/dns-query",
        "https://mozilla.cloudflare-dns.com/dns-query",
        "https://cloudflare-dns.com/dns-query"
    ];

    public static readonly string[] DnsFallbackPool =
    [
        "https://security.cloudflare-dns.com/dns-query",
        "tls://1.1.1.1",
        "tls://8.8.8.8",
        "tls://9.9.9.9",
        "https://dns.google/dns-query"
    ];

    /// <summary>Apply persona + DNS diversity onto a working settings copy used only for config build.</summary>
    public static AppSettings PrepareForBuild(AppSettings src, ProfileInfo? profile)
    {
        var json = System.Text.Json.JsonSerializer.Serialize(src);
        var s = System.Text.Json.JsonSerializer.Deserialize<AppSettings>(json) ?? new AppSettings();

        ApplyPersona(s);

        if (s.DnsDiversity)
            RotateDnsInto(s, advance: false);

        // Preserve the chosen MTU across reconnects. Random MTU changes are not
        // traffic padding and can introduce fragmentation on otherwise stable paths.

        if (profile != null)
        {
            if (!string.IsNullOrWhiteSpace(profile.FingerprintOverride))
            {
                s.RandomFingerprint = false;
                s.GlobalClientFingerprint = profile.FingerprintOverride.Trim();
            }
            if (profile.NameserverOverride is { Count: > 0 })
            {
                var resolvers = profile.NameserverOverride.Where(x => !string.IsNullOrWhiteSpace(x)).Select(x => x.Trim()).ToList();
                if (resolvers.Count > 0) s.Dns.Nameserver = resolvers;
            }
        }

        // Persist smart-direct domains into custom direct for this build
        if (s.SmartDirectMap && s.SmartDirectDomains.Count > 0)
        {
            var set = (s.CustomDirectRules ?? "")
                .Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .ToHashSet(StringComparer.OrdinalIgnoreCase);
            foreach (var d in s.SmartDirectDomains)
                if (!string.IsNullOrWhiteSpace(d)) set.Add(d.Trim());
            s.CustomDirectRules = string.Join('\n', set.OrderBy(x => x));
        }

        return s;
    }

    public static void ApplyPersona(AppSettings s)
    {
        switch ((s.NetworkPersona ?? "auto").Trim().ToLowerInvariant())
        {
            case "home":
                s.Mtu = s.Mtu > 0 ? s.Mtu : 1400;
                if (s.Mtu < 1280) s.Mtu = 1400;
                s.GlobalClientFingerprint = "chrome";
                s.RandomFingerprint = false;
                s.RfRushMode = true;
                s.PreferRushLane = true;
                s.BlockQuic = true;
                s.DnsViaProxy = true;
                break;
            case "mobile":
                s.Mtu = 1280;
                s.RandomFingerprint = true;
                s.RfRushMode = true;
                s.PreferRushLane = true;
                s.BlockQuic = true;
                s.DnsViaProxy = true;
                s.TcpConcurrent = true;
                s.HighPriority = true;
                break;
            case "corporate":
                s.Mtu = 1280;
                s.RandomFingerprint = false;
                s.GlobalClientFingerprint = "firefox";
                s.RfRushMode = false;
                s.PreferRushLane = false;
                s.StealthMode = true;
                s.BlockQuic = true;
                s.BlockStun = true;
                s.DnsViaProxy = true;
                s.DnsStrictNoFallback = true;
                s.SilentLogs = true;
                break;
            // auto — leave user toggles
        }
    }

    public static void RotateDnsInto(AppSettings s, bool advance)
    {
        var pool = DnsPool;
        var idx = (int)(Math.Abs((long)s.DnsPoolIndex) % pool.Length);
        if (advance)
        {
            idx = (idx + 2) % pool.Length;
            s.DnsPoolIndex = idx;
            s.LastDnsRotateAt = DateTimeOffset.UtcNow;
        }

        s.Dns.Nameserver =
        [
            pool[idx],
            pool[(idx + 1) % pool.Length],
            pool[(idx + 3) % pool.Length]
        ];
        if (s.DnsStrictNoFallback)
            s.Dns.Fallback = [];
        else
        {
            var f = Math.Abs(idx) % DnsFallbackPool.Length;
            s.Dns.Fallback =
            [
                DnsFallbackPool[f],
                DnsFallbackPool[(f + 1) % DnsFallbackPool.Length]
            ];
        }
        s.Dns.DefaultNameserver = ["1.1.1.1", "8.8.8.8", "9.9.9.9"];
    }
}
