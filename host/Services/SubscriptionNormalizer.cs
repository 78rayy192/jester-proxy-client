using System.Collections;
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

    static readonly IDeserializer YamlIn = new DeserializerBuilder()
        .IgnoreUnmatchedProperties()
        .Build();

    public static string ToClashYaml(string raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
            throw new InvalidOperationException("Пустая подписка.");

        var text = raw.Trim().TrimStart('\uFEFF');
        var fromJson = TryXrayJson(text);
        if (fromJson != null) return fromJson;
        if (LooksLikeClash(text)) return DecorateYaml(text);

        var decoded = TryBase64(text);
        if (decoded != null)
        {
            fromJson = TryXrayJson(decoded);
            if (fromJson != null) return fromJson;
            if (LooksLikeClash(decoded)) return DecorateYaml(decoded);
            text = decoded;
        }

        var uris = ExtractUris(text);
        if (uris.Count == 0)
            throw new InvalidOperationException(
                "Формат не распознан. Укажите конфигурацию Clash или ссылку vless, vmess, trojan, ss, hysteria2.");

        var proxies = new List<Dictionary<string, object>>();
        var names = new List<string>();
        var used = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var uri in uris)
        {
            Dictionary<string, object>? proxy;
            try { proxy = ParseShare(uri); }
            catch { continue; }
            if (proxy == null) continue;
            var name = Unique(ServerFlags.Apply(proxy["name"].ToString() ?? "node"), used);
            proxy["name"] = name;
            proxies.Add(proxy);
            names.Add(name);
        }

        if (proxies.Count == 0)
            throw new InvalidOperationException("В ссылке нет поддерживаемых серверов.");
        RejectNotices(names);

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

    static string? TryXrayJson(string text)
    {
        var trimmed = text.TrimStart();
        if (trimmed.Length == 0 || trimmed[0] is not ('[' or '{')) return null;
        try
        {
            using var doc = JsonDocument.Parse(text);
            var configs = new List<JsonElement>();
            if (doc.RootElement.ValueKind == JsonValueKind.Array)
            {
                foreach (var item in doc.RootElement.EnumerateArray())
                {
                    if (item.ValueKind == JsonValueKind.Object && item.TryGetProperty("outbounds", out _))
                        configs.Add(item.Clone());
                }
                if (configs.Count == 0) return null;
            }
            else if (doc.RootElement.ValueKind == JsonValueKind.Object && doc.RootElement.TryGetProperty("outbounds", out _))
            {
                configs.Add(doc.RootElement.Clone());
            }
            else return null;

            var proxies = new List<Dictionary<string, object>>();
            var names = new List<string>();
            var used = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var config in configs)
            {
                var outbound = PickProxyOutbound(config);
                if (outbound == null) continue;
                var remark = JsonStr(config, "remarks");
                if (remark.Length == 0) remark = JsonStr(outbound.Value, "tag");
                Dictionary<string, object>? proxy;
                try { proxy = FromXrayOutbound(remark.Length == 0 ? "Сервер" : remark, outbound.Value); }
                catch { continue; }
                if (proxy == null) continue;
                var name = Unique(ServerFlags.Apply(proxy["name"].ToString() ?? "Сервер"), used);
                proxy["name"] = name;
                proxies.Add(proxy);
                names.Add(name);
            }
            if (proxies.Count == 0)
                throw new InvalidOperationException("В ссылке нет поддерживаемых серверов.");
            RejectNotices(names);
            return Yaml.Serialize(new Dictionary<string, object>
            {
                ["proxies"] = proxies,
                ["proxy-groups"] = new List<object>
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
                },
                ["rules"] = new List<object> { "MATCH,DIRECT" }
            });
        }
        catch (InvalidOperationException) { throw; }
        catch { return null; }
    }

    static JsonElement? PickProxyOutbound(JsonElement config)
    {
        if (!config.TryGetProperty("outbounds", out var outs) || outs.ValueKind != JsonValueKind.Array)
            return null;
        JsonElement? first = null;
        foreach (var outbound in outs.EnumerateArray())
        {
            var proto = JsonStr(outbound, "protocol");
            if (proto is "freedom" or "blackhole" or "dns" or "loopback") continue;
            if (JsonStr(outbound, "tag").Equals("proxy", StringComparison.OrdinalIgnoreCase))
                return outbound.Clone();
            first ??= outbound.Clone();
        }
        return first;
    }

    static Dictionary<string, object>? FromXrayOutbound(string name, JsonElement outbound)
    {
        var proto = JsonStr(outbound, "protocol");
        if (!outbound.TryGetProperty("settings", out var settings)) return null;
        var stream = outbound.TryGetProperty("streamSettings", out var ss) ? ss : default;

        if (proto == "vless")
        {
            if (!settings.TryGetProperty("vnext", out var vnext) || vnext.GetArrayLength() == 0) return null;
            var node = vnext[0];
            if (!node.TryGetProperty("users", out var users) || users.GetArrayLength() == 0) return null;
            var user = users[0];
            var port = JsonInt(node, "port");
            var proxy = Base("vless", name, JsonStr(node, "address"), port > 0 ? port : 443);
            proxy["uuid"] = JsonStr(user, "id");
            proxy["encryption"] = JsonStr(user, "encryption") is { Length: > 0 } enc ? enc : "none";
            ApplyStream(proxy, stream, JsonStr(user, "flow"));
            return proxy;
        }

        if (proto is "hysteria" or "hysteria2")
        {
            var version = JsonInt(settings, "version");
            if (version == 0 && stream.ValueKind == JsonValueKind.Object
                && stream.TryGetProperty("hysteriaSettings", out var hyVer))
                version = JsonInt(hyVer, "version");
            var port = JsonInt(settings, "port");
            var kind = version == 1 ? "hysteria" : "hysteria2";
            var proxy = Base(kind, name, JsonStr(settings, "address"), port > 0 ? port : 443);
            var auth = "";
            if (stream.ValueKind == JsonValueKind.Object && stream.TryGetProperty("hysteriaSettings", out var hy))
                auth = JsonStr(hy, "auth");
            if (auth.Length == 0) auth = JsonStr(settings, "auth");
            if (kind == "hysteria") proxy["auth-str"] = auth;
            else proxy["password"] = auth;
            if (stream.ValueKind == JsonValueKind.Object && stream.TryGetProperty("tlsSettings", out var tls))
            {
                var sni = JsonStr(tls, "serverName");
                if (sni.Length > 0) proxy["sni"] = sni;
                var fp = JsonStr(tls, "fingerprint");
                if (fp.Length > 0) proxy["client-fingerprint"] = fp;
                if (tls.TryGetProperty("alpn", out var alpn) && alpn.ValueKind == JsonValueKind.Array)
                {
                    var list = alpn.EnumerateArray().Select(x => x.GetString()).Where(x => !string.IsNullOrWhiteSpace(x)).Cast<object>().ToList();
                    if (list.Count > 0) proxy["alpn"] = list;
                }
            }
            if (LooksLikeIp(proxy["server"].ToString())) proxy["skip-cert-verify"] = true;
            return proxy;
        }

        return null;
    }

    static void ApplyStream(Dictionary<string, object> proxy, JsonElement stream, string flow)
    {
        if (stream.ValueKind != JsonValueKind.Object) return;
        var network = JsonStr(stream, "network");
        if (network.Length == 0) network = "tcp";
        proxy["network"] = network is "ws" or "grpc" or "h2" or "http" ? network : "tcp";
        if ((network is "tcp" or "") && flow.Length > 0) proxy["flow"] = flow;

        var security = JsonStr(stream, "security");
        if (security is "reality" or "tls")
        {
            proxy["tls"] = true;
            var tlsName = security == "reality" ? "realitySettings" : "tlsSettings";
            if (stream.TryGetProperty(tlsName, out var tls))
            {
                var sni = JsonStr(tls, "serverName");
                if (sni.Length > 0) proxy["servername"] = sni;
                var fp = JsonStr(tls, "fingerprint");
                if (fp.Length > 0) proxy["client-fingerprint"] = fp;
                if (security == "reality")
                {
                    var reality = new Dictionary<string, object>();
                    var key = JsonStr(tls, "publicKey");
                    if (key.Length > 0) reality["public-key"] = key;
                    var sid = JsonStr(tls, "shortId");
                    if (sid.Length > 0) reality["short-id"] = sid;
                    var spider = JsonStr(tls, "spiderX");
                    if (spider.Length > 0) reality["spider-x"] = spider;
                    proxy["reality-opts"] = reality;
                }
            }
        }

        if (network == "grpc" && stream.TryGetProperty("grpcSettings", out var grpc))
        {
            var service = JsonStr(grpc, "serviceName");
            if (service.Length > 0)
                proxy["grpc-opts"] = new Dictionary<string, object> { ["grpc-service-name"] = service };
        }

        if (network == "ws" && stream.TryGetProperty("wsSettings", out var ws))
        {
            var opts = new Dictionary<string, object>();
            var path = JsonStr(ws, "path");
            if (path.Length > 0) opts["path"] = path;
            var host = JsonStr(ws, "host");
            if (host.Length == 0 && ws.TryGetProperty("headers", out var headers) && headers.ValueKind == JsonValueKind.Object)
            {
                foreach (var header in headers.EnumerateObject())
                {
                    if (header.Name.Equals("Host", StringComparison.OrdinalIgnoreCase) && header.Value.ValueKind == JsonValueKind.String)
                        host = header.Value.GetString() ?? "";
                }
            }
            if (host.Length > 0)
                opts["headers"] = new Dictionary<string, object> { ["Host"] = host };
            proxy["ws-opts"] = opts;
        }

        if (LooksLikeIp(proxy["server"].ToString())) proxy["skip-cert-verify"] = true;
    }

    static string JsonStr(JsonElement el, string key)
    {
        if (el.ValueKind != JsonValueKind.Object || !el.TryGetProperty(key, out var value)) return "";
        return value.ValueKind switch
        {
            JsonValueKind.String => (value.GetString() ?? "").Trim(),
            JsonValueKind.Number => value.ToString(),
            _ => ""
        };
    }

    static int JsonInt(JsonElement el, string key)
    {
        if (el.ValueKind != JsonValueKind.Object || !el.TryGetProperty(key, out var value)) return 0;
        if (value.ValueKind == JsonValueKind.Number && value.TryGetInt32(out var n)) return n;
        return value.ValueKind == JsonValueKind.String && int.TryParse(value.GetString(), out var p) ? p : 0;
    }

    static void RejectNotices(IReadOnlyList<string> names)
    {
        if (names.Count == 0 || names.Any(n => !ServerFlags.IsNotice(n))) return;
        throw new InvalidOperationException(
            "Поставщик не отдал список серверов. Если указан лимит устройств, удалите неиспользуемое устройство в боте и обновите подписку.");
    }

    static string DecorateYaml(string yaml)
    {
        Dictionary<object, object>? doc;
        try { doc = YamlIn.Deserialize<Dictionary<object, object>>(yaml); }
        catch { return yaml; }
        if (doc == null || !doc.TryGetValue("proxies", out var raw) || raw is not IList list) return yaml;

        var map = new Dictionary<string, string>(StringComparer.Ordinal);
        var names = new List<string>();
        foreach (var item in list)
        {
            if (item is not IDictionary<object, object> proxy) continue;
            if (!proxy.TryGetValue("name", out var current) || current is not string old || old.Length == 0) continue;
            var next = ServerFlags.Apply(old);
            names.Add(next);
            if (next == old) continue;
            proxy["name"] = next;
            map[old] = next;
        }
        RejectNotices(names);
        if (map.Count > 0) Rewrite(doc, map);
        return Yaml.Serialize(doc);
    }

    static void Rewrite(object node, Dictionary<string, string> map)
    {
        if (node is IDictionary<object, object> dict)
        {
            foreach (var key in dict.Keys.ToList())
            {
                if (dict[key] is string s && map.TryGetValue(s, out var next) && !string.Equals(key as string, "name", StringComparison.Ordinal))
                    dict[key] = next;
                else Rewrite(dict[key]!, map);
            }
        }
        else if (node is IList list)
        {
            for (var i = 0; i < list.Count; i++)
            {
                if (list[i] is string s && map.TryGetValue(s, out var next)) list[i] = next;
                else if (list[i] != null) Rewrite(list[i]!, map);
            }
        }
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
            "tuic" => ParseTuic(uri),
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

    static Dictionary<string, object> ParseTuic(Uri uri)
    {
        var q = Query(uri);
        var proxy = Base("tuic", Name(uri, "TUIC"), uri.Host, uri.Port > 0 ? uri.Port : 443);
        var user = Uri.UnescapeDataString(uri.UserInfo);
        var split = user.Split(':', 2);
        proxy["uuid"] = split[0];
        if (split.Length == 2) proxy["password"] = split[1];
        if (q.Get("sni") is { Length: > 0 } sni) proxy["sni"] = sni;
        if (q.Get("congestion_control") is { Length: > 0 } cc) proxy["congestion-controller"] = cc;
        if (q.Get("udp_relay_mode") is { Length: > 0 } relay) proxy["udp-relay-mode"] = relay;
        if (q.Get("alpn") is { Length: > 0 } alpn)
            proxy["alpn"] = alpn.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Cast<object>().ToList();
        if (q.Get("insecure") is "1" or "true" || LooksLikeIp(uri.Host)) proxy["skip-cert-verify"] = true;
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
