using Jester.Host.Models;
using YamlDotNet.Serialization;

namespace Jester.Host.Services;

public static class ConfigBuilder
{
    static readonly IDeserializer Deserializer = new DeserializerBuilder()
        .IgnoreUnmatchedProperties()
        .Build();

    static readonly ISerializer Serializer = new SerializerBuilder()
        .ConfigureDefaultValuesHandling(DefaultValuesHandling.Preserve)
        .WithIndentedSequences()
        .Build();

    public static string Merge(string profileYaml, AppSettings settings, string secret)
    {
        Dictionary<object, object> doc;
        try
        {
            var yaml = SubscriptionNormalizer.ToClashYaml(profileYaml);
            doc = Deserializer.Deserialize<Dictionary<object, object>>(yaml)
                  ?? new Dictionary<object, object>();
        }
        catch (Exception ex)
        {
            throw new InvalidOperationException("Не удалось прочитать профиль: " + ex.Message, ex);
        }

        var stealth = settings.StealthMode;
        var dnsEnabled = settings.Dns.Enable || stealth;

        Set(doc, "mixed-port", settings.MixedPort);
        Set(doc, "socks-port", settings.SocksPort);
        // Stealth: never expose LAN listeners
        var allowLan = stealth ? false : settings.AllowLan;
        Set(doc, "allow-lan", allowLan);
        Set(doc, "bind-address", allowLan ? "*" : "127.0.0.1");
        Set(doc, "mode", settings.Mode);

        var logLevel = settings.LogLevel;
        if (settings.SilentLogs || stealth)
            logLevel = "silent";
        else if (logLevel is "info" or "debug")
            logLevel = "warning";
        Set(doc, "log-level", logLevel);

        Set(doc, "ipv6", stealth ? false : settings.Ipv6);
        Set(doc, "unified-delay", settings.UnifiedDelay);
        Set(doc, "tcp-concurrent", settings.TcpConcurrent);
        Set(doc, "disable-keep-alive", !settings.KeepAlive);
        var keepIdle = settings.KeepAlive ? 10 : 0;
        var keepInterval = settings.KeepAlive ? 10 : 0;
        if (settings.TrafficPadding && settings.KeepAlive)
        {
            var rnd = new Random(Environment.TickCount ^ settings.DnsPoolIndex);
            keepIdle = rnd.Next(8, 19);
            keepInterval = rnd.Next(8, 17);
        }
        Set(doc, "keep-alive-idle", keepIdle);
        Set(doc, "keep-alive-interval", keepInterval);
        var findProcess = settings.FindProcessMode;
        if (settings.PerAppFastPath) findProcess = "strict";
        else if (stealth) findProcess = "off";
        Set(doc, "find-process-mode", findProcess);

        var fp = settings.RandomFingerprint
            ? "random"
            : (string.IsNullOrWhiteSpace(settings.GlobalClientFingerprint) ? "chrome" : settings.GlobalClientFingerprint);
        Set(doc, "global-client-fingerprint", fp);
        Set(doc, "global-ua",
            "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/131.0.0.0 Safari/537.36");

        Set(doc, "profile", new Dictionary<object, object>
        {
            ["store-selected"] = settings.StoreSelected,
            ["store-fake-ip"] = true,
            ["tracing"] = false
        });

        // Sniff only what we need for routing — no QUIC
        if (settings.SnifferEnabled)
        {
            Set(doc, "sniffer", new Dictionary<object, object>
            {
                ["enable"] = true,
                ["parse-pure-ip"] = true,
                ["force-dns-mapping"] = true,
                ["override-destination"] = true,
                ["sniff"] = new Dictionary<object, object>
                {
                    ["HTTP"] = new Dictionary<object, object> { ["ports"] = new List<object> { 80, 443 } },
                    ["TLS"] = new Dictionary<object, object> { ["ports"] = new List<object> { 443, 8443 } }
                },
                ["skip-domain"] = new List<object>
                {
                    "Mijia Cloud", "+.push.apple.com", "+.apple.com", "+.microsoft.com", "+.windows.com"
                }
            });
        }
        else
        {
            Set(doc, "sniffer", new Dictionary<object, object> { ["enable"] = false });
        }

        Set(doc, "external-controller", $"127.0.0.1:{settings.ExternalPort}");
        // A subscription must not expose an additional controller listener.
        Remove(doc, "external-controller-tls", "external-controller-unix", "external-controller-pipe", "external-doh-server");
        Set(doc, "secret", secret);
        var origins = new List<object> { "http://127.0.0.1:37841", "http://localhost:37841" };
        var dev = Environment.GetEnvironmentVariable("JESTER_DEV");
        if (dev == "1" || string.Equals(dev, "true", StringComparison.OrdinalIgnoreCase))
            origins.AddRange(["http://127.0.0.1:5173", "http://localhost:5173"]);
        Set(doc, "external-controller-cors", new Dictionary<object, object>
        {
            ["allow-origins"] = origins,
            ["allow-private-network"] = false
        });

        var mtu = settings.Mtu > 0 ? settings.Mtu : 1400;
        if (stealth && mtu > 1280) mtu = 1280; // fewer fragments on filtered links

        Set(doc, "tun", new Dictionary<object, object>
        {
            ["enable"] = settings.TunEnabled,
            ["stack"] = string.IsNullOrWhiteSpace(settings.TunStack) ? "mixed" : settings.TunStack,
            ["auto-route"] = settings.AutoRoute,
            ["auto-detect-interface"] = settings.AutoDetectInterface,
            ["auto-redirect"] = settings.AutoRedirect,
            ["strict-route"] = stealth && settings.TunEnabled || settings.StrictRoute,
            ["mtu"] = mtu,
            ["endpoint-independent-nat"] = settings.EndpointIndependentNat,
            ["dns-hijack"] = dnsEnabled && (settings.DnsHijack || stealth)
                ? new List<object> { "any:53", "tcp://any:53" }
                : new List<object>(),
            ["inet4-address"] = new List<object> { settings.Inet4Address },
            ["udp-timeout"] = 30
        });

        if (dnsEnabled)
        {
            var viaProxy = settings.DnsViaProxy || stealth;
            var nameservers = viaProxy
                ? settings.Dns.Nameserver.Select(WrapDnsViaProxy).Cast<object>().ToList()
                : settings.Dns.Nameserver.Cast<object>().ToList();
            var fallbacks = viaProxy
                ? settings.Dns.Fallback.Select(WrapDnsViaProxy).Cast<object>().ToList()
                : settings.Dns.Fallback.Cast<object>().ToList();
            if (settings.DnsStrictNoFallback)
                fallbacks = [];

            var dns = new Dictionary<object, object>
            {
                ["enable"] = true,
                ["ipv6"] = !stealth && settings.Ipv6 && settings.Dns.Ipv6,
                ["prefer-h3"] = false,
                ["use-hosts"] = settings.Dns.UseHosts,
                ["enhanced-mode"] = settings.Dns.EnhancedMode,
                ["fake-ip-range"] = settings.Dns.FakeIpRange,
                ["fake-ip-filter"] = CriticalFakeIpFilter(settings),
                ["default-nameserver"] = settings.Dns.DefaultNameserver.Cast<object>().ToList(),
                ["nameserver"] = nameservers,
                ["fallback"] = fallbacks,
                ["cache-algorithm"] = "arc",
                ["proxy-server-nameserver"] = settings.Dns.DefaultNameserver.Cast<object>().ToList(),
                // DIRECT destinations still use the protected resolver when requested.
                // Bootstrap resolvers are reserved for resolving proxy/DNS server names.
                ["direct-nameserver"] = viaProxy ? nameservers : settings.Dns.DefaultNameserver.Cast<object>().ToList(),
                ["fallback-filter"] = new Dictionary<object, object>
                {
                    ["geoip"] = true,
                    // Product targets RF users — polluted resolvers return RU ISP poison, not CN.
                    ["geoip-code"] = "RU",
                    ["ipcidr"] = new List<object> { "240.0.0.0/4", "0.0.0.0/32" }
                }
            };
            if (settings.DnsRespectRules || stealth || viaProxy)
                dns["respect-rules"] = true;
            Set(doc, "dns", dns);
        }
        else
            Set(doc, "dns", new Dictionary<object, object> { ["enable"] = false });

        EnsureProxyGroup(doc, stealth, settings);
        ApplySmartRouting(doc, settings);

        return Serializer.Serialize(doc);
    }

    /// <summary>Force application DNS through PROXY; retain resolver parameters, never a DIRECT/interface override.</summary>
    static string WrapDnsViaProxy(string server)
    {
        var parts = server.Trim().Split('#', 2);
        var address = parts[0];
        if (address.Equals("system", StringComparison.OrdinalIgnoreCase)
            || address.StartsWith("system://", StringComparison.OrdinalIgnoreCase)
            || address.StartsWith("dhcp://", StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("Для DNS через прокси укажите адрес DNS-сервера вместо system/DHCP.");
        if (System.Net.IPAddress.TryParse(address, out var ip)
            && ip.AddressFamily == System.Net.Sockets.AddressFamily.InterNetworkV6)
            address = "[" + address + "]";
        var options = parts.Length == 2
            ? parts[1].Split('&', StringSplitOptions.RemoveEmptyEntries).Where(p => p.Contains('='))
            : [];
        return address + "#PROXY" + string.Concat(options.Select(p => "&" + p));
    }

    static void EnsureProxyGroup(Dictionary<object, object> doc, bool stealth, AppSettings settings)
    {
        var proxies = GetList(doc, "proxies");
        var names = new List<string>();
        var fastNames = new List<string>();
        foreach (var item in proxies)
        {
            var name = ProxyName(item);
            if (string.IsNullOrEmpty(name)) continue;
            names.Add(name);
            if (IsFastProtocol(item)) fastNames.Add(name);
        }

        var groups = GetList(doc, "proxy-groups");
        var providerNames = doc.TryGetValue("proxy-providers", out var rawProviders)
            && rawProviders is Dictionary<object, object> proxyProviders
            ? proxyProviders.Keys.Select(k => k.ToString() ?? "").Where(k => k.Length > 0).ToList()
            : [];
        var hasMembers = names.Count > 0 || providerNames.Count > 0;
        var hasProxy = groups.Any(g => NameOf(g).Equals("PROXY", StringComparison.OrdinalIgnoreCase));
        var hasAuto = groups.Any(g => NameOf(g).Equals("AUTO", StringComparison.OrdinalIgnoreCase));
        var hasRush = groups.Any(g => NameOf(g).Equals("RUSH", StringComparison.OrdinalIgnoreCase));
        var hasUdpFast = groups.Any(g => NameOf(g).Equals("UDP-FAST", StringComparison.OrdinalIgnoreCase));
        var rush = settings.RfRushMode && fastNames.Count > 0;
        var udpLane = settings.SplitUdpPolicy && (fastNames.Count > 0 || names.Count > 0);
        var udpMembers = fastNames.Count > 0 ? fastNames : names;

        // Faster + quieter url-test: lazy (no dial until used), longer interval
        if (!hasAuto && hasMembers)
        {
            groups.Add(new Dictionary<object, object>
            {
                ["name"] = "AUTO",
                ["type"] = "url-test",
                ["url"] = "https://www.gstatic.com/generate_204",
                ["interval"] = stealth ? 600 : 300,
                ["tolerance"] = 80,
                ["lazy"] = true,
                ["expected-status"] = "204",
                ["proxies"] = names.Cast<object>().ToList(),
                ["use"] = providerNames.Cast<object>().ToList()
            });
        }
        else
        {
            foreach (var g in groups)
            {
                if (!NameOf(g).Equals("AUTO", StringComparison.OrdinalIgnoreCase)) continue;
                if (g is Dictionary<object, object> d)
                {
                    d["lazy"] = true;
                    d["interval"] = stealth ? 600 : 300;
                    d["tolerance"] = 80;
                }
            }
        }

        // РФ Rush SPEED lane — only Hysteria2 / TUIC / VLESS / Trojan (snappy UDP/TLS transports)
        if (rush && !hasRush)
        {
            groups.Insert(0, new Dictionary<object, object>
            {
                ["name"] = "RUSH",
                ["type"] = "url-test",
                ["url"] = "https://www.gstatic.com/generate_204",
                ["interval"] = 90,
                ["tolerance"] = 40,
                ["lazy"] = false,
                ["expected-status"] = "204",
                ["proxies"] = fastNames.Cast<object>().ToList()
            });
        }
        else if (rush)
        {
            foreach (var g in groups)
            {
                if (!NameOf(g).Equals("RUSH", StringComparison.OrdinalIgnoreCase)) continue;
                if (g is Dictionary<object, object> d)
                {
                    d["proxies"] = fastNames.Cast<object>().ToList();
                    d["interval"] = 90;
                    d["tolerance"] = 40;
                    d["lazy"] = false;
                }
            }
        }

        if (udpLane && !hasUdpFast)
        {
            groups.Insert(0, new Dictionary<object, object>
            {
                ["name"] = "UDP-FAST",
                ["type"] = "url-test",
                ["url"] = "https://www.gstatic.com/generate_204",
                ["interval"] = 120,
                ["tolerance"] = 50,
                ["lazy"] = true,
                ["expected-status"] = "204",
                ["proxies"] = udpMembers.Cast<object>().ToList()
            });
        }
        else if (udpLane)
        {
            foreach (var g in groups)
            {
                if (!NameOf(g).Equals("UDP-FAST", StringComparison.OrdinalIgnoreCase)) continue;
                if (g is Dictionary<object, object> d)
                {
                    d["proxies"] = udpMembers.Cast<object>().ToList();
                    d["interval"] = 120;
                    d["tolerance"] = 50;
                }
            }
        }

        var proxyChildren = new List<string>();
        if (rush && settings.PreferRushLane) proxyChildren.Add("RUSH");
        proxyChildren.Add("AUTO");
        if (rush && !settings.PreferRushLane) proxyChildren.Add("RUSH");
        proxyChildren.AddRange(names);

        if (!hasProxy && hasMembers)
        {
            groups.Insert(0, new Dictionary<object, object>
            {
                ["name"] = "PROXY",
                ["type"] = "select",
                ["lazy"] = true,
                ["proxies"] = proxyChildren.Distinct(StringComparer.OrdinalIgnoreCase).Cast<object>().ToList()
            });
        }
        else
        {
            foreach (var g in groups)
            {
                if (!NameOf(g).Equals("PROXY", StringComparison.OrdinalIgnoreCase)) continue;
                if (g is Dictionary<object, object> d)
                {
                    d["lazy"] = true;
                    if (rush)
                    {
                        var existing = new List<string>();
                        if (d.TryGetValue("proxies", out var pl) && pl is IEnumerable<object> en)
                        {
                            foreach (var x in en)
                            {
                                var s = x?.ToString();
                                if (!string.IsNullOrEmpty(s)) existing.Add(s);
                            }
                        }
                        var merged = proxyChildren.Concat(existing)
                            .Distinct(StringComparer.OrdinalIgnoreCase)
                            .ToList();
                        d["proxies"] = merged.Cast<object>().ToList();
                    }
                }
            }
        }

        Set(doc, "proxy-groups", groups);
    }

    static string ProxyName(object item)
    {
        if (item is Dictionary<object, object> d && d.TryGetValue("name", out var n) && n != null)
            return n.ToString() ?? "";
        if (item is Dictionary<string, object> ds && ds.TryGetValue("name", out var ns) && ns != null)
            return ns.ToString() ?? "";
        return "";
    }

    static string ProxyType(object item)
    {
        if (item is Dictionary<object, object> d && d.TryGetValue("type", out var t) && t != null)
            return t.ToString() ?? "";
        if (item is Dictionary<string, object> ds && ds.TryGetValue("type", out var ts) && ts != null)
            return ts.ToString() ?? "";
        return "";
    }

    static bool IsFastProtocol(object item)
    {
        var type = ProxyType(item);
        return type.Equals("hysteria2", StringComparison.OrdinalIgnoreCase)
            || type.Equals("hysteria", StringComparison.OrdinalIgnoreCase)
            || type.Equals("tuic", StringComparison.OrdinalIgnoreCase)
            || type.Equals("vless", StringComparison.OrdinalIgnoreCase)
            || type.Equals("trojan", StringComparison.OrdinalIgnoreCase)
            || type.Equals("wireguard", StringComparison.OrdinalIgnoreCase);
    }

    static void ApplySmartRouting(Dictionary<object, object> doc, AppSettings settings)
    {
        var stealth = settings.StealthMode;
        var providers = new Dictionary<object, object>();
        var rules = new List<object>
        {
            "GEOIP,PRIVATE,DIRECT,no-resolve",
            "IP-CIDR,127.0.0.0/8,DIRECT,no-resolve",
            "IP-CIDR,10.0.0.0/8,DIRECT,no-resolve",
            "IP-CIDR,172.16.0.0/12,DIRECT,no-resolve",
            "IP-CIDR,192.168.0.0/16,DIRECT,no-resolve",
            "IP-CIDR,169.254.0.0/16,DIRECT,no-resolve",
            "IP-CIDR,224.0.0.0/4,DIRECT,no-resolve",
            "IP-CIDR6,::1/128,DIRECT,no-resolve",
            "IP-CIDR6,fc00::/7,DIRECT,no-resolve",
            "IP-CIDR6,fe80::/10,DIRECT,no-resolve"
        };

        // WebRTC / STUN / TURN — real IP leak vector (before UDP-FAST so 3478 never hairpins to a node)
        if (settings.BlockStun || stealth)
        {
            rules.Add("DOMAIN-SUFFIX,stun.cloudflare.com,REJECT");
            rules.Add("DOMAIN-SUFFIX,stun.l.google.com,REJECT");
            rules.Add("DOMAIN-SUFFIX,stun1.l.google.com,REJECT");
            rules.Add("DOMAIN-SUFFIX,stun2.l.google.com,REJECT");
            rules.Add("DOMAIN-SUFFIX,stun3.l.google.com,REJECT");
            rules.Add("DOMAIN-SUFFIX,stun4.l.google.com,REJECT");
            rules.Add("AND,((NETWORK,udp),(DST-PORT,3478)),REJECT");
            rules.Add("AND,((NETWORK,udp),(DST-PORT,5349)),REJECT");
        }

        // These controls must precede process and domain allow-rules.
        // Rejecting HTTP/3 makes browsers retry over TLS/TCP through the selected proxy.
        if (settings.BlockQuic || stealth)
            rules.Add("AND,((NETWORK,udp),(DST-PORT,443)),REJECT");

        foreach (var rule in AlwaysProxyRules())
            rules.Add(rule);

        if (settings.PerAppFastPath)
        {
            foreach (var proc in (settings.FastPathProcesses ?? "")
                         .Split(['\r', '\n', ',', ';'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            {
                var name = proc.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) ? proc : proc + ".exe";
                rules.Add($"PROCESS-NAME,{name},PROXY");
            }
        }

        // Split UDP: game/VoIP ports → UDP-FAST (only if group exists)
        var hasUdpFast = GetList(doc, "proxy-groups")
            .Any(g => NameOf(g).Equals("UDP-FAST", StringComparison.OrdinalIgnoreCase));
        if (settings.SplitUdpPolicy && hasUdpFast)
        {
            foreach (var portSpec in ParsePortSpecs(settings.UdpFastPorts))
            {
                if (portSpec is "3478" or "5349") continue;
                rules.Add($"AND,((NETWORK,udp),(DST-PORT,{portSpec})),UDP-FAST");
            }
        }

        // IP-check hosts always via PROXY — for leak test + hide real IP from those APIs
        rules.Add("DOMAIN-SUFFIX,ipify.org,PROXY");
        rules.Add("DOMAIN-SUFFIX,ifconfig.me,PROXY");
        rules.Add("DOMAIN-SUFFIX,ipinfo.io,PROXY");
        rules.Add("DOMAIN-SUFFIX,ip.sb,PROXY");
        rules.Add("DOMAIN,api.ipify.org,PROXY");
        rules.Add("DOMAIN,ifconfig.co,PROXY");

        // РФ Rush: Russian destinations stay DIRECT (local speed), blocked/foreign packs via PROXY
        if (settings.RfRushMode)
        {
            foreach (var rule in RfEmergencyProxyRules())
                rules.Add(rule);
            // Local RU networks — don't hairpin through VPN (huge latency win)
            rules.Add("GEOIP,RU,DIRECT,no-resolve");
        }

        // Custom lists
        providers["jester-direct"] = ProviderFile("domain", RuleListService.CustomDirectFile);
        providers["jester-proxy"] = ProviderFile("domain", RuleListService.CustomProxyFile);
        rules.Add("RULE-SET,jester-direct,DIRECT");
        rules.Add("RULE-SET,jester-proxy,PROXY");

        if (settings.RefilterEnabled)
        {
            if (settings.UseMrsRules)
            {
                providers["refilter-domains"] = new Dictionary<object, object>
                {
                    ["type"] = "http",
                    ["behavior"] = "domain",
                    ["format"] = "mrs",
                    ["url"] = settings.RefilterDomainsMrsUrl,
                    ["path"] = "./ruleset/" + RuleListService.DomainsMrsFile,
                    ["interval"] = 86400,
                    ["proxy"] = "DIRECT"
                };
                rules.Add("RULE-SET,refilter-domains,PROXY");

                if (settings.RefilterUseIp)
                {
                    providers["refilter-ipsum"] = new Dictionary<object, object>
                    {
                        ["type"] = "http",
                        ["behavior"] = "ipcidr",
                        ["format"] = "mrs",
                        ["url"] = settings.RefilterIpMrsUrl,
                        ["path"] = "./ruleset/" + RuleListService.IpsMrsFile,
                        ["interval"] = 86400,
                        ["proxy"] = "DIRECT"
                    };
                    rules.Add("RULE-SET,refilter-ipsum,PROXY,no-resolve");
                }
            }
            else
            {
                providers["refilter-domains"] = new Dictionary<object, object>
                {
                    ["type"] = "http",
                    ["behavior"] = "domain",
                    ["format"] = "text",
                    ["url"] = settings.RefilterDomainsUrl,
                    ["path"] = "./ruleset/" + RuleListService.DomainsFile,
                    ["interval"] = 86400,
                    ["proxy"] = "DIRECT"
                };
                rules.Add("RULE-SET,refilter-domains,PROXY");

                if (settings.RefilterUseIp)
                {
                    providers["refilter-ipsum"] = new Dictionary<object, object>
                    {
                        ["type"] = "http",
                        ["behavior"] = "ipcidr",
                        ["format"] = "text",
                        ["url"] = settings.RefilterIpUrl,
                        ["path"] = "./ruleset/" + RuleListService.IpsFile,
                        ["interval"] = 86400,
                        ["proxy"] = "DIRECT"
                    };
                    rules.Add("RULE-SET,refilter-ipsum,PROXY,no-resolve");
                }
            }
        }

        var final = string.Equals(settings.FinalOutbound, "PROXY", StringComparison.OrdinalIgnoreCase)
            ? "PROXY"
            : "DIRECT";
        rules.Add("MATCH," + final);

        Set(doc, "rule-providers", providers);
        Set(doc, "rules", rules);
    }

    static IEnumerable<string> ParsePortSpecs(string? raw)
    {
        foreach (var part in (raw ?? "")
                     .Split([',', ' ', ';', '\n', '\r'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            if (part.Contains('-', StringComparison.Ordinal))
            {
                var bits = part.Split('-', 2, StringSplitOptions.TrimEntries);
                if (int.TryParse(bits[0], out var a) && int.TryParse(bits[1], out var b) && a > 0 && b >= a && b <= 65535)
                    yield return a + "-" + b;
            }
            else if (int.TryParse(part, out var p) && p > 0 && p <= 65535)
                yield return part;
        }
    }

    static Dictionary<object, object> ProviderFile(string behavior, string fileName) => new()
    {
        ["type"] = "file",
        ["behavior"] = behavior,
        ["format"] = "text",
        ["path"] = "./ruleset/" + fileName
    };

    /// <summary>Always via VPN — independent of Rush. Cursor/GitHub die if they fall to DIRECT or QUIC-reject.</summary>
    static IEnumerable<string> AlwaysProxyRules()
    {
        string[] domains =
        [
            "cursor.com", "cursor.sh", "cursorapi.com", "todesktop.com",
            "github.com", "githubusercontent.com", "githubassets.com", "github.io",
            "ghcr.io", "github.dev", "githubcopilot.com",
            "vscode.dev", "vscode-cdn.net", "visualstudio.com",
            "gitlab.com", "bitbucket.org"
        ];
        foreach (var d in domains)
            yield return "DOMAIN-SUFFIX," + d + ",PROXY";
    }

    static List<object> CriticalFakeIpFilter(AppSettings settings)
    {
        var list = settings.Dns.FakeIpFilter.Cast<object>().ToList();
        string[] extra =
        [
            "+.cursor.com", "+.cursor.sh", "+.cursorapi.com",
            "+.github.com", "+.githubusercontent.com", "+.githubassets.com", "+.ghcr.io"
        ];
        foreach (var d in extra)
        {
            if (!list.Any(x => string.Equals(x?.ToString(), d, StringComparison.OrdinalIgnoreCase)))
                list.Add(d);
        }
        return list;
    }

    /// <summary>Hard-coded RF emergency pack — works even before Re:filter download finishes.</summary>
    static IEnumerable<string> RfEmergencyProxyRules()
    {
        string[] domains =
        [
            // Social / media
            "youtube.com", "youtu.be", "googlevideo.com", "ytimg.com", "ggpht.com",
            "instagram.com", "cdninstagram.com", "facebook.com", "fbcdn.net", "meta.com",
            "twitter.com", "x.com", "twimg.com", "t.co",
            "tiktok.com", "tiktokv.com", "musical.ly",
            "discord.com", "discord.gg", "discordapp.com", "discord.media", "discordapp.net",
            "linkedin.com", "licdn.com",
            // Messengers / calls
            "telegram.org", "t.me", "telegra.ph", "cdn-telegram.org",
            "signal.org", "whispersystems.org",
            // AI / cloud
            "openai.com", "chatgpt.com", "oaistatic.com", "oaiusercontent.com",
            "anthropic.com", "claude.ai",
            "gemini.google.com", "aistudio.google.com", "googleapis.com",
            "perplexity.ai", "grok.com", "x.ai",
            "notion.so", "notion.site",
            // Dev / media
            "github.com", "githubusercontent.com", "githubassets.com", "githubcopilot.com",
            "gitlab.com", "npmjs.com", "pypi.org", "docker.com", "medium.com",
            "reddit.com", "redd.it", "redditmedia.com", "redditstatic.com",
            "twitch.tv", "ttvnw.net", "jtvnw.net",
            "spotify.com", "scdn.co", "spotifycdn.com",
            "netflix.com", "nflxvideo.net", "nflximg.net",
            // Google / CDN often throttled
            "google.com", "gstatic.com", "googleusercontent.com", "google.com.ua",
            "cloudflare.com", "cloudflare-dns.com",
            "wikipedia.org", "wikimedia.org",
            // VPN-ish helpers / stores
            "play.google.com", "apple.com", "icloud.com",
            "proton.me", "protonmail.com", "mullvad.net"
        ];

        foreach (var d in domains)
            yield return "DOMAIN-SUFFIX," + d + ",PROXY";
    }

    static List<object> GetList(Dictionary<object, object> doc, string key)
    {
        foreach (var k in doc.Keys.ToList())
        {
            if (!string.Equals(k?.ToString(), key, StringComparison.OrdinalIgnoreCase)) continue;
            if (doc[k!] is List<object> list) return list;
            if (doc[k!] is IEnumerable<object> en) return en.ToList();
        }
        var created = new List<object>();
        doc[key] = created;
        return created;
    }

    static string NameOf(object? group)
    {
        if (group is Dictionary<object, object> d && d.TryGetValue("name", out var n) && n != null)
            return n.ToString() ?? "";
        if (group is Dictionary<string, object> ds && ds.TryGetValue("name", out var ns) && ns != null)
            return ns.ToString() ?? "";
        return "";
    }

    static void Set(Dictionary<object, object> doc, string key, object value)
    {
        object? existing = null;
        foreach (var k in doc.Keys.ToList())
        {
            if (string.Equals(k?.ToString(), key, StringComparison.OrdinalIgnoreCase))
            {
                existing = k;
                break;
            }
        }
        if (existing != null) doc.Remove(existing);
        doc[key] = value;
    }

    static void Remove(Dictionary<object, object> doc, params string[] keys)
    {
        foreach (var key in doc.Keys.Where(k => keys.Contains(k?.ToString(), StringComparer.OrdinalIgnoreCase)).ToList())
            doc.Remove(key);
    }
}
