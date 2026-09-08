using System.Text;
using System.Text.Json;
using YamlDotNet.Serialization;

namespace Jester.Host.Services;

public static class SubscriptionNormalizer
{
    static readonly ISerializer Yaml = new SerializerBuilder()
        .ConfigureDefaultValuesHandling(DefaultValuesHandling.OmitNull)
        .WithIndentedSequences()
        .Build();

    public static string ToClashYaml(string raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
            throw new InvalidOperationException("Пустая подписка.");

        var text = raw.Trim().TrimStart('\uFEFF');
        if (LooksLikeClash(text)) return text;

        var decoded = TryBase64(text);
        if (decoded != null)
        {
            if (LooksLikeClash(decoded)) return decoded;
            text = decoded;
        }

        var uris = ExtractUris(text);
        if (uris.Count == 0)
            throw new InvalidOperationException(
                "Формат не распознан. Вставьте Clash YAML или ссылку Happ/v2ray (vless, vmess, trojan, ss, hysteria2).");

        var proxies = new List<Dictionary<string, object>>();
        var names = new List<string>();
        var used = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var uri in uris)
        {
            Dictionary<string, object>? proxy;
            try { proxy = ParseShare(uri); }
            catch { continue; }
            if (proxy == null) continue;
            var name = Unique(proxy["name"].ToString() ?? "node", used);
            proxy["name"] = name;
            proxies.Add(proxy);
            names.Add(name);
        }

        if (proxies.Count == 0)
            throw new InvalidOperationException("В ссылке нет узлов, которые умеет mihomo (vless / vmess / trojan / ss / hysteria2).");

        var groups = new List<object>
        {
            new Dictionary<string, object>
            {
                ["name"] = "PROXY",
                ["type"] = "select",
                ["proxies"] = names.Concat(["AUTO"]).Cast<object>().ToList()
            },
            new Dictionary<string, object>
            {
                ["name"] = "AUTO",
                ["type"] = "url-test",
                ["url"] = "https://www.gstatic.com/generate_204",
                ["interval"] = 300,
                ["tolerance"] = 50,
                ["proxies"] = names.Cast<object>().ToList()
            }
        };

        var doc = new Dictionary<string, object>
        {
            ["proxies"] = proxies,
            ["proxy-groups"] = groups,
            ["rules"] = new List<object> { "MATCH,DIRECT" }
        };
        return Yaml.Serialize(doc);
    }

    static bool LooksLikeClash(string text)
    {
        return text.Contains("proxies:", StringComparison.OrdinalIgnoreCase)
            || text.Contains("proxy-groups:", StringComparison.OrdinalIgnoreCase)
            || text.Contains("mixed-port:", StringComparison.OrdinalIgnoreCase);
    }

    static List<string> ExtractUris(string text)
    {
        var list = new List<string>();
        foreach (var line in text.Replace("\r", "").Split('\n'))
        {
            var s = line.Trim();
            if (s.Length == 0 || s.StartsWith('#')) continue;
            if (s.Contains("://", StringComparison.Ordinal)) list.Add(s);
        }
        return list;
    }

    static string? TryBase64(string text)
    {
        var compact = text.Replace("\r", "").Replace("\n", "").Replace(" ", "");
        if (compact.Length < 16 || compact.Contains("://")) return null;
        try
        {
            var pad = compact.Length % 4;
            if (pad > 0) compact += new string('=', 4 - pad);
            var bytes = Convert.FromBase64String(compact);
            var s = Encoding.UTF8.GetString(bytes);
            return s.Contains("://") || LooksLikeClash(s) ? s : null;
        }
        catch { return null; }
    }

    static Dictionary<string, object>? ParseShare(string raw)
    {
        if (raw.StartsWith("vmess://", StringComparison.OrdinalIgnoreCase))
            return ParseVmess(raw);
        if (!Uri.TryCreate(raw, UriKind.Absolute, out var uri)) return null;
        var scheme = uri.Scheme.ToLowerInvariant();
        return scheme switch
        {
            "vless" => ParseVless(uri),
            "trojan" => ParseTrojan(uri),
            "ss" => ParseShadowsocks(uri),
            "hysteria2" or "hy2" => ParseHysteria2(uri),
            _ => null
        };
    }

    static Dictionary<string, object> ParseVless(Uri uri)
    {
        var q = Query(uri);
        var name = Name(uri, "VLESS");
        var security = q.Get("security") ?? "";
        var type = q.Get("type") ?? "tcp";
        var proxy = Base("vless", name, uri.Host, uri.Port > 0 ? uri.Port : 443);
        proxy["uuid"] = Uri.UnescapeDataString(uri.UserInfo);
        proxy["encryption"] = q.Get("encryption") ?? "none";
        proxy["network"] = type is "xhttp" or "httpupgrade" or "ws" or "grpc" or "h2" or "http" ? type : "tcp";
        if (!string.IsNullOrWhiteSpace(q.Get("flow"))) proxy["flow"] = q.Get("flow")!;
        if (!string.IsNullOrWhiteSpace(q.Get("fp"))) proxy["client-fingerprint"] = q.Get("fp")!;
        var tls = security is "tls" or "reality" || q.Get("sni") != null;
        if (tls) proxy["tls"] = true;
        if (!string.IsNullOrWhiteSpace(q.Get("sni"))) proxy["servername"] = q.Get("sni")!;
        if (q.Get("insecure") is "1" or "true" || LooksLikeIp(uri.Host)) proxy["skip-cert-verify"] = true;
        if (security == "reality")
        {
            proxy["tls"] = true;
            var reality = new Dictionary<string, object>();
            if (q.Get("pbk") is { Length: > 0 } pbk) reality["public-key"] = pbk;
            if (q.Get("sid") is { Length: > 0 } sid) reality["short-id"] = sid;
            proxy["reality-opts"] = reality;
        }
        if (type is "ws")
        {
            var opts = new Dictionary<string, object>();
            if (q.Get("path") is { Length: > 0 } path) opts["path"] = path;
            if (q.Get("host") is { Length: > 0 } host) opts["headers"] = new Dictionary<string, object> { ["Host"] = host };
            proxy["ws-opts"] = opts;
        }
        if (type == "xhttp")
        {
            var opts = new Dictionary<string, object>();
            if (q.Get("path") is { Length: > 0 } path) opts["path"] = path;
            if (q.Get("host") is { Length: > 0 } host) opts["host"] = host;
            if (q.Get("mode") is { Length: > 0 } mode) opts["mode"] = mode;
            proxy["xhttp-opts"] = opts;
        }
        if (type == "grpc" && q.Get("serviceName") is { Length: > 0 } svc)
            proxy["grpc-opts"] = new Dictionary<string, object> { ["grpc-service-name"] = svc };
        return proxy;
    }

    static Dictionary<string, object> ParseHysteria2(Uri uri)
    {
        var q = Query(uri);
        var proxy = Base("hysteria2", Name(uri, "Hysteria2"), uri.Host, uri.Port > 0 ? uri.Port : 443);
        proxy["password"] = Uri.UnescapeDataString(uri.UserInfo);
        if (q.Get("sni") is { Length: > 0 } sni) proxy["sni"] = sni;
        if (q.Get("obfs") is { Length: > 0 } obfs) proxy["obfs"] = obfs;
        if (q.Get("obfs-password") is { Length: > 0 } op) proxy["obfs-password"] = op;
        if (q.Get("alpn") is { Length: > 0 } alpn)
            proxy["alpn"] = alpn.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Cast<object>().ToList();
        if (q.Get("insecure") is "1" or "true" || LooksLikeIp(uri.Host) || LooksLikeIp(q.Get("sni")))
            proxy["skip-cert-verify"] = true;
        if (q.Get("fp") is { Length: > 0 } fp) proxy["client-fingerprint"] = fp;
        return proxy;
    }

    static Dictionary<string, object> ParseTrojan(Uri uri)
    {
        var q = Query(uri);
        var proxy = Base("trojan", Name(uri, "Trojan"), uri.Host, uri.Port > 0 ? uri.Port : 443);
        proxy["password"] = Uri.UnescapeDataString(uri.UserInfo);
        proxy["tls"] = true;
        if (q.Get("sni") is { Length: > 0 } sni) proxy["sni"] = sni;
        if (q.Get("fp") is { Length: > 0 } fp) proxy["client-fingerprint"] = fp;
        var type = q.Get("type") ?? "tcp";
        proxy["network"] = type;
        if (q.Get("insecure") is "1" or "true" || LooksLikeIp(uri.Host)) proxy["skip-cert-verify"] = true;
        return proxy;
    }

    static Dictionary<string, object>? ParseShadowsocks(Uri uri)
    {
        var name = Name(uri, "SS");
        string userinfo;
        try { userinfo = Uri.UnescapeDataString(uri.UserInfo); }
        catch { return null; }
        if (!userinfo.Contains(':'))
        {
            var decoded = TryBase64(userinfo);
            if (decoded != null) userinfo = decoded;
        }
        var split = userinfo.Split(':', 2);
        if (split.Length != 2) return null;
        var proxy = Base("ss", name, uri.Host, uri.Port > 0 ? uri.Port : 443);
        proxy["cipher"] = split[0];
        proxy["password"] = split[1];
        return proxy;
    }

    static Dictionary<string, object>? ParseVmess(string raw)
    {
        var payload = raw["vmess://".Length..];
        var json = TryBase64(payload);
        if (json == null) return null;
        using var doc = JsonDocument.Parse(json);
        var r = doc.RootElement;
        string S(string k) => r.TryGetProperty(k, out var v) ? v.ToString() : "";
        var port = int.TryParse(S("port"), out var p) ? p : 443;
        var proxy = Base("vmess", string.IsNullOrWhiteSpace(S("ps")) ? "VMess" : S("ps"), S("add"), port);
        proxy["uuid"] = S("id");
        proxy["alterId"] = int.TryParse(S("aid"), out var aid) ? aid : 0;
        proxy["cipher"] = string.IsNullOrWhiteSpace(S("scy")) ? "auto" : S("scy");
        var net = S("net");
        if (!string.IsNullOrWhiteSpace(net)) proxy["network"] = net;
        if (S("tls") is "tls" or "reality") proxy["tls"] = true;
        if (!string.IsNullOrWhiteSpace(S("sni"))) proxy["servername"] = S("sni");
        return proxy;
    }

    static Dictionary<string, object> Base(string type, string name, string server, int port) => new()
    {
        ["name"] = name,
        ["type"] = type,
        ["server"] = server,
        ["port"] = port,
        ["udp"] = true
    };

    static Dictionary<string, string> Query(Uri uri)
    {
        var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var q = uri.Query.TrimStart('?');
        if (q.Length == 0) return map;
        foreach (var part in q.Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            var kv = part.Split('=', 2);
            var key = Uri.UnescapeDataString(kv[0]);
            var val = kv.Length > 1 ? Uri.UnescapeDataString(kv[1].Replace("+", "%20")) : "";
            map[key] = val;
        }
        return map;
    }

    static string Name(Uri uri, string fallback)
    {
        var n = Uri.UnescapeDataString(uri.Fragment.TrimStart('#'));
        return string.IsNullOrWhiteSpace(n) ? fallback : n;
    }

    static string Unique(string name, HashSet<string> used)
    {
        var n = name.Trim();
        if (n.Length == 0) n = "node";
        var baseName = n;
        var i = 2;
        while (!used.Add(n)) n = $"{baseName} {i++}";
        return n;
    }

    static bool LooksLikeIp(string? host) =>
        !string.IsNullOrWhiteSpace(host) && (System.Net.IPAddress.TryParse(host, out _) || host.All(c => char.IsDigit(c) || c == '.'));
}

file static class QueryGet
{
    public static string? Get(this Dictionary<string, string> q, string key) =>
        q.TryGetValue(key, out var v) && v.Length > 0 ? v : null;
}
