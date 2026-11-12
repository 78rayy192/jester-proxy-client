using System.Text;
using System.Text.Json;
using Jester.Host.Models;
using Jester.Host.Services;
using YamlDotNet.Serialization;

namespace Jester.Regression;

internal static class ConfigRegression
{
    const string Profile = "proxies:\n  - name: Test\n    type: trojan\n    server: 203.0.113.10\n    port: 443\n    password: test\n    sni: example.com\n";
    static readonly IDeserializer Yaml = new DeserializerBuilder().Build();
    static Dictionary<object, object> Read(string yaml) => Yaml.Deserialize<Dictionary<object, object>>(yaml);
    static Dictionary<object, object> Map(Dictionary<object, object> source, string key) => (Dictionary<object, object>)source[key];
    static List<object> List(Dictionary<object, object> source, string key) => (List<object>)source[key];
    static string Text(object value) => value.ToString() ?? "";
    static string B64(string value) => Convert.ToBase64String(Encoding.UTF8.GetBytes(value));
    static Dictionary<object, object> Node(string share) => (Dictionary<object, object>)List(Read(SubscriptionNormalizer.ToClashYaml(share)), "proxies")[0];

    public static void Register(RegressionSuite suite)
    {
        suite.Case("TLS verification stays enabled for IP endpoints", () =>
        {
            string[] shares =
            [
                "vless://id@203.0.113.10:443?security=tls&sni=example.com#Test",
                "trojan://password@203.0.113.10:443?sni=example.com#Test",
                "hysteria2://password@203.0.113.10:443?sni=example.com#Test",
                "tuic://id:password@203.0.113.10:443?sni=example.com#Test"
            ];
            foreach (var share in shares)
            {
                Check.True(!Node(share).ContainsKey("skip-cert-verify"), "IP address silently disabled TLS verification");
                Check.Equal("true", Text(Node(share.Replace("#Test", "&insecure=1#Test"))["skip-cert-verify"]).ToLowerInvariant());
            }
        });
        suite.Case("Xray TLS verification follows explicit allowInsecure", () =>
        {
            string Json(bool insecure) => JsonSerializer.Serialize(new
            {
                outbounds = new[] { new { protocol = "vless", settings = new { vnext = new[] { new { address = "203.0.113.10", port = 443, users = new[] { new { id = "id" } } } } },
                    streamSettings = new { security = "tls", tlsSettings = new { serverName = "example.com", allowInsecure = insecure } } } }
            });
            Check.True(!Node(Json(false)).ContainsKey("skip-cert-verify"), "Xray IP import disabled verification");
            Check.Equal("true", Text(Node(Json(true))["skip-cert-verify"]).ToLowerInvariant());
        });
        suite.Case("VMess base64 JSON preserves websocket transport", () =>
        {
            var json = JsonSerializer.Serialize(new { v = "2", ps = "Test", add = "example.com", port = "443", id = "id", aid = "0", net = "ws", tls = "tls", path = "/socket", host = "front.example.com" });
            var node = Node("vmess://" + B64(json).TrimEnd('=').Replace('+', '-').Replace('/', '_'));
            Check.Equal("vmess", Text(node["type"]));
            Check.Equal("/socket", Text(Map(node, "ws-opts")["path"]));
            Check.Equal("front.example.com", Text(Map(Map(node, "ws-opts"), "headers")["Host"]));
        });
        suite.Case("Shadowsocks imports SIP002 and legacy credentials", () =>
        {
            var node = Node("ss://" + B64("aes-128-gcm:secret") + "@example.com:8388#Test");
            Check.Equal("secret", Text(node["password"]));
            Check.Equal("aes-128-gcm", Text(node["cipher"]));
            node = Node("ss://" + B64("aes-128-gcm:secret@example.com:8388") + "#Test");
            Check.Equal("8388", Text(node["port"]));
            Check.Equal("secret", Text(node["password"]));
        });
        suite.Case("Trojan websocket settings survive import", () =>
        {
            var node = Node("trojan://password@example.com:443?type=ws&path=%2Fsocket&host=front.example.com");
            Check.Equal("/socket", Text(Map(node, "ws-opts")["path"]));
        });
        suite.Case("Flag decoration updates references without rewriting credentials", () =>
        {
            var yaml = "proxies:\n  - name: Germany\n    type: trojan\n    server: Germany\n    password: Germany\n    port: 443\nproxy-groups:\n  - name: PROXY\n    type: select\n    proxies: [Germany]\nrules:\n  - DOMAIN,example.com,Germany\n  - IP-CIDR,1.1.1.1/32,Germany,no-resolve\n";
            var doc = Read(SubscriptionNormalizer.ToClashYaml(yaml));
            var node = (Dictionary<object, object>)List(doc, "proxies")[0];
            var decorated = Text(node["name"]);
            Check.True(decorated != "Germany", "Flag was not applied");
            Check.Equal("Germany", Text(node["password"]), "Credential was rewritten");
            Check.Equal("Germany", Text(node["server"]), "Server was rewritten");
            Check.Equal(decorated, Text(List((Dictionary<object, object>)List(doc, "proxy-groups")[0], "proxies")[0]));
            Check.Equal("DOMAIN,example.com," + decorated, Text(List(doc, "rules")[0]));
            Check.Equal("IP-CIDR,1.1.1.1/32," + decorated + ",no-resolve", Text(List(doc, "rules")[1]));
        });
        suite.Case("DNS proxy policy covers plain DNS and existing routing suffixes", () =>
        {
            var settings = new AppSettings();
            settings.Dns.Nameserver = ["1.1.1.1", "https://dns.google/dns-query#DIRECT&disable-qtype-65=true", "2001:4860:4860::8888"];
            var doc = Read(ConfigBuilder.Merge(Profile, settings, "test-secret"));
            var dns = Map(doc, "dns");
            var names = List(dns, "nameserver").Select(Text).ToList();
            Check.Equal("1.1.1.1#PROXY", names[0]);
            Check.Equal("https://dns.google/dns-query#PROXY&disable-qtype-65=true", names[1]);
            Check.Equal("[2001:4860:4860::8888]#PROXY", names[2]);
            Check.True(List(dns, "direct-nameserver").Select(Text).SequenceEqual(names), "Direct destination DNS escaped proxy policy");
        });
        suite.Case("Provider-only profiles receive valid application proxy groups", () =>
        {
            const string yaml = "proxy-providers:\n  Subscription:\n    type: http\n    url: https://example.com/subscription\n    path: ./provider.yaml\n";
            var doc = Read(ConfigBuilder.Merge(yaml, new AppSettings(), "secret"));
            var groups = List(doc, "proxy-groups").Cast<Dictionary<object, object>>().ToList();
            Check.True(groups.Any(g => Text(g["name"]) == "PROXY"), "Application PROXY group was not generated");
            var auto = groups.Single(g => Text(g["name"]) == "AUTO");
            Check.Equal("Subscription", Text(List(auto, "use")[0]));
        });
        suite.Case("DNS disable overrides imported DNS and disables hijack", () =>
        {
            var settings = new AppSettings { StealthMode = false };
            settings.Dns.Enable = false;
            var doc = Read(ConfigBuilder.Merge(Profile + "dns:\n  enable: true\n  nameserver: [8.8.8.8]\n", settings, "secret"));
            Check.Equal("false", Text(Map(doc, "dns")["enable"]).ToLowerInvariant());
            Check.Equal(0, List(Map(doc, "tun"), "dns-hijack").Count);
        });
        suite.Case("Shield retains DNS protection if DNS toggle is disabled", () =>
        {
            var settings = new AppSettings();
            settings.Dns.Enable = false;
            var doc = Read(ConfigBuilder.Merge(Profile, settings, "secret"));
            Check.Equal("true", Text(Map(doc, "dns")["enable"]).ToLowerInvariant());
            Check.True(List(Map(doc, "tun"), "dns-hijack").Count > 0, "Shield has no DNS hijack");
        });
        suite.Case("QUIC and STUN blocks precede browser routing", () =>
        {
            var rules = List(Read(ConfigBuilder.Merge(Profile, new AppSettings(), "secret")), "rules").Select(Text).ToList();
            var browser = rules.IndexOf("PROCESS-NAME,chrome.exe,PROXY");
            Check.True(browser > 0, "Browser process rule missing");
            Check.True(rules.IndexOf("AND,((NETWORK,udp),(DST-PORT,3478)),REJECT") < browser, "STUN bypassed process routing");
            var quic = rules.IndexOf("AND,((NETWORK,udp),(DST-PORT,443)),REJECT");
            Check.True(quic >= 0 && quic < browser, "QUIC block was missing or bypassed");
        });
        suite.Case("Subscriptions cannot add public controller listeners or wildcard CORS", () =>
        {
            var doc = Read(ConfigBuilder.Merge(Profile + "external-controller-tls: 0.0.0.0:9443\nexternal-controller-unix: /tmp/control.sock\n", new AppSettings { StealthMode = false }, "secret"));
            Check.True(!doc.ContainsKey("external-controller-tls") && !doc.ContainsKey("external-controller-unix"), "Extra controller retained");
            Check.Equal("127.0.0.1:9090", Text(doc["external-controller"]));
            Check.True(!List(Map(doc, "external-controller-cors"), "allow-origins").Select(Text).Contains("*"), "Wildcard controller CORS");
        });
        suite.Case("MTU and explicit keepalive settings survive persona preparation", () =>
        {
            var source = new AppSettings { Mtu = 1360, TrafficPadding = true, KeepAlive = false, DnsDiversity = false };
            var prepared = NetworkPersonas.PrepareForBuild(source, null);
            Check.Equal(1360, prepared.Mtu);
            Check.Equal(false, prepared.KeepAlive);
            var doc = Read(ConfigBuilder.Merge(Profile, prepared, "secret"));
            Check.Equal("true", Text(doc["disable-keep-alive"]).ToLowerInvariant());
            Check.Equal(1360, source.Mtu, "Original settings mutated");
        });
        suite.Case("Profile fingerprint override survives Shield", () =>
        {
            var settings = NetworkPersonas.PrepareForBuild(new AppSettings(), new ProfileInfo { FingerprintOverride = "firefox" });
            var doc = Read(ConfigBuilder.Merge(Profile, settings, "secret"));
            Check.Equal("firefox", Text(doc["global-client-fingerprint"]));
        });
        suite.Case("DNS pool handles minimum integer index", () =>
        {
            var settings = new AppSettings { DnsPoolIndex = int.MinValue };
            NetworkPersonas.RotateDnsInto(settings, false);
            Check.Equal(3, settings.Dns.Nameserver.Count);
        });
    }
}
