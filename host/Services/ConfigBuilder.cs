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
        Set(doc, "tcp-concurrent", true); // always — faster dials
        Set(doc, "keep-alive-idle", settings.KeepAlive ? 10 : 0);
        Set(doc, "keep-alive-interval", settings.KeepAlive ? 10 : 0);
        Set(doc, "find-process-mode", stealth ? "off" : settings.FindProcessMode);

        var fp = settings.RandomFingerprint || stealth
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
        Set(doc, "secret", secret);
        Set(doc, "external-controller-cors", new Dictionary<object, object>
        {
            ["allow-origins"] = stealth
                ? new List<object> { "http://127.0.0.1:37841", "http://localhost:37841" }
                : new List<object>
                {
                    "http://127.0.0.1:37841", "http://localhost:37841",
                    "http://127.0.0.1:5173", "http://localhost:5173", "*"
                },
            ["allow-private-network"] = true
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
            ["endpoint-independent-nat"] = true,
            ["dns-hijack"] = settings.DnsHijack || stealth
                ? new List<object> { "any:53", "tcp://any:53" }
                : new List<object>(),
            ["inet4-address"] = new List<object> { settings.Inet4Address },
            ["udp-timeout"] = 30
        });

        if (settings.Dns.Enable)
        {
            var viaProxy = settings.DnsViaProxy || stealth;
            var nameservers = viaProxy
                ? settings.Dns.Nameserver.Select(WrapDnsViaProxy).Cast<object>().ToList()
                : settings.Dns.Nameserver.Cast<object>().ToList();
            var fallbacks = viaProxy
                ? settings.Dns.Fallback.Select(WrapDnsViaProxy).Cast<object>().ToList()
                : settings.Dns.Fallback.Cast<object>().ToList();

            var dns = new Dictionary<object, object>
            {
                ["enable"] = true,
                ["ipv6"] = false,
                ["prefer-h3"] = false,
                ["use-hosts"] = settings.Dns.UseHosts,
                ["enhanced-mode"] = "fake-ip",
                ["fake-ip-range"] = settings.Dns.FakeIpRange,
                ["fake-ip-filter"] = settings.Dns.FakeIpFilter.Cast<object>().ToList(),
                ["default-nameserver"] = settings.Dns.DefaultNameserver.Cast<object>().ToList(),
                ["nameserver"] = nameservers,
                ["fallback"] = fallbacks,
                ["cache-algorithm"] = "arc",
                ["proxy-server-nameserver"] = settings.Dns.DefaultNameserver.Cast<object>().ToList(),
                ["direct-nameserver"] = settings.Dns.DefaultNameserver.Cast<object>().ToList(),
                ["fallback-filter"] = new Dictionary<object, object>
                {
                    ["geoip"] = true,
                    ["geoip-code"] = "CN",
                    ["ipcidr"] = new List<object> { "240.0.0.0/4", "0.0.0.0/32" }
                }
            };
            if (settings.DnsRespectRules || stealth || viaProxy)
                dns["respect-rules"] = true;
            Set(doc, "dns", dns);
        }

        EnsureProxyGroup(doc, stealth);
        ApplySmartRouting(doc, settings);

        return Serializer.Serialize(doc);
    }

    /// <summary>Force DoH/DoT queries through PROXY outbound (mihomo #PROXY suffix).</summary>
    static string WrapDnsViaProxy(string server)
    {
        if (string.IsNullOrWhiteSpace(server)) return server;
        if (server.Contains('#', StringComparison.Ordinal)) return server;
        // Only wrap encrypted resolvers — plain IP stays for bootstrap paths
        if (server.StartsWith("https://", StringComparison.OrdinalIgnoreCase)
            || server.StartsWith("tls://", StringComparison.OrdinalIgnoreCase)
            || server.StartsWith("quic://", StringComparison.OrdinalIgnoreCase))
            return server + "#PROXY";
        return server;
    }

    static void EnsureProxyGroup(Dictionary<object, object> doc, bool stealth)
    {
        var proxies = GetList(doc, "proxies");
        var names = new List<string>();
        foreach (var item in proxies)
        {
            if (item is Dictionary<object, object> d && d.TryGetValue("name", out var n) && n != null)
                names.Add(n.ToString()!);
            else if (item is Dictionary<string, object> ds && ds.TryGetValue("name", out var ns) && ns != null)
                names.Add(ns.ToString()!);
        }

        var groups = GetList(doc, "proxy-groups");
        var hasProxy = groups.Any(g => NameOf(g).Equals("PROXY", StringComparison.OrdinalIgnoreCase));
        var hasAuto = groups.Any(g => NameOf(g).Equals("AUTO", StringComparison.OrdinalIgnoreCase));

        // Faster + quieter url-test: lazy (no dial until used), longer interval
        if (!hasAuto && names.Count > 0)
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
                ["proxies"] = names.Cast<object>().ToList()
            });
        }
        else
        {
            // Patch existing AUTO for speed/stealth
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

        if (!hasProxy && names.Count > 0)
        {
            groups.Insert(0, new Dictionary<object, object>
            {
                ["name"] = "PROXY",
                ["type"] = "select",
                ["lazy"] = true,
                ["proxies"] = names.Concat(["AUTO"]).Cast<object>().ToList()
            });
        }
        else
        {
            foreach (var g in groups)
            {
                if (!NameOf(g).Equals("PROXY", StringComparison.OrdinalIgnoreCase)) continue;
                if (g is Dictionary<object, object> d)
                    d["lazy"] = true;
            }
        }

        Set(doc, "proxy-groups", groups);
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

        // WebRTC / STUN / TURN — real IP leak vector
        if (settings.BlockStun || stealth)
        {
            rules.Add("DOMAIN-KEYWORD,stun,REJECT");
            rules.Add("DOMAIN-KEYWORD,turn.,REJECT");
            rules.Add("DOMAIN-SUFFIX,stun.cloudflare.com,REJECT");
            rules.Add("DOMAIN-SUFFIX,stun.l.google.com,REJECT");
            rules.Add("DOMAIN-SUFFIX,stun1.l.google.com,REJECT");
            rules.Add("DOMAIN-SUFFIX,stun2.l.google.com,REJECT");
            rules.Add("DOMAIN-SUFFIX,stun3.l.google.com,REJECT");
            rules.Add("DOMAIN-SUFFIX,stun4.l.google.com,REJECT");
            rules.Add("AND,((NETWORK,udp),(DST-PORT,3478)),REJECT");
            rules.Add("AND,((NETWORK,udp),(DST-PORT,5349)),REJECT");
        }

        // Anti-DPI: kill QUIC
        if (settings.BlockQuic || stealth)
            rules.Add("AND,((NETWORK,udp),(DST-PORT,443)),REJECT");

        // IP-check hosts always via PROXY — for leak test + hide real IP from those APIs
        rules.Add("DOMAIN-SUFFIX,ipify.org,PROXY");
        rules.Add("DOMAIN-SUFFIX,ifconfig.me,PROXY");
        rules.Add("DOMAIN-SUFFIX,ipinfo.io,PROXY");
        rules.Add("DOMAIN-SUFFIX,ip.sb,PROXY");
        rules.Add("DOMAIN,api.ipify.org,PROXY");
        rules.Add("DOMAIN,ifconfig.co,PROXY");

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

    static Dictionary<object, object> ProviderFile(string behavior, string fileName) => new()
    {
        ["type"] = "file",
        ["behavior"] = behavior,
        ["format"] = "text",
        ["path"] = "./ruleset/" + fileName
    };

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
}
