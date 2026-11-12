use crate::flags;
use base64::Engine;
use serde_json::Value as Json;
use serde_yaml::{Mapping, Value};
use std::collections::{HashMap, HashSet};

pub fn to_clash_yaml(raw: &str) -> Result<String, String> {
    let text = raw.trim().trim_start_matches('\u{feff}');
    if text.is_empty() { return Err("Пустая подписка.".into()); }
    if let Some(yaml) = try_xray(text)? { return Ok(yaml); }
    if looks_like_clash(text) { return Ok(decorate(text)); }
    if let Some(decoded) = try_b64(text) {
        if let Some(yaml) = try_xray(&decoded)? { return Ok(yaml); }
        if looks_like_clash(&decoded) { return Ok(decorate(&decoded)); }
        return from_uris(&decoded);
    }
    from_uris(text)
}

fn from_uris(text: &str) -> Result<String, String> {
    let uris = extract_uris(text);
    if uris.is_empty() {
        return Err("Формат не распознан. Укажите конфигурацию Clash или ссылку vless, vmess, trojan, ss, hysteria2.".into());
    }
    let mut proxies = Vec::new();
    let mut names = Vec::new();
    let mut used = HashSet::new();
    for uri in uris {
        let Some(mut proxy) = parse_share(&uri) else { continue };
        let old = proxy.get("name").and_then(Value::as_str).unwrap_or("node").to_string();
        let name = unique(&flags::apply(&old), &mut used);
        proxy.insert("name".into(), Value::String(name.clone()));
        names.push(name);
        proxies.push(Value::Mapping(proxy));
    }
    if proxies.is_empty() { return Err("В ссылке нет поддерживаемых серверов.".into()); }
    reject_notices(&names)?;
    Ok(dump_doc(proxies, names))
}

fn dump_doc(proxies: Vec<Value>, names: Vec<String>) -> String {
    let mut doc = Mapping::new();
    doc.insert("proxies".into(), Value::Sequence(proxies));
    let mut groups = Vec::new();
    let mut proxy_members: Vec<Value> = names.iter().cloned().map(Value::String).collect();
    proxy_members.push(Value::String("AUTO".into()));
    groups.push(map_val(&[("name", "PROXY".into()), ("type", "select".into()), ("proxies", Value::Sequence(proxy_members))]));
    groups.push(map_val(&[
        ("name", "AUTO".into()),
        ("type", "url-test".into()),
        ("url", "https://www.gstatic.com/generate_204".into()),
        ("interval", Value::Number(300.into())),
        ("tolerance", Value::Number(50.into())),
        ("proxies", Value::Sequence(names.into_iter().map(Value::String).collect())),
    ]));
    doc.insert("proxy-groups".into(), Value::Sequence(groups));
    doc.insert("rules".into(), Value::Sequence(vec![Value::String("MATCH,DIRECT".into())]));
    serde_yaml::to_string(&Value::Mapping(doc)).unwrap_or_default()
}

fn try_xray(text: &str) -> Result<Option<String>, String> {
    let trimmed = text.trim_start();
    if !trimmed.starts_with('{') && !trimmed.starts_with('[') { return Ok(None); }
    let Ok(json) = serde_json::from_str::<Json>(text) else { return Ok(None); };
    let configs: Vec<&Json> = match &json {
        Json::Array(items) => items.iter().filter(|i| i.get("outbounds").is_some()).collect(),
        Json::Object(_) if json.get("outbounds").is_some() => vec![&json],
        _ => return Ok(None),
    };
    if configs.is_empty() { return Ok(None); }
    let mut proxies = Vec::new();
    let mut names = Vec::new();
    let mut used = HashSet::new();
    for config in configs {
        let Some(outbound) = pick_outbound(config) else { continue };
        let mut remark = json_str(config, "remarks");
        if remark.is_empty() { remark = json_str(outbound, "tag"); }
        if remark.is_empty() { remark = "Сервер".into(); }
        let Some(mut proxy) = from_xray(&remark, outbound) else { continue };
        let old = proxy.get("name").and_then(Value::as_str).unwrap_or("Сервер").to_string();
        let name = unique(&flags::apply(&old), &mut used);
        proxy.insert("name".into(), Value::String(name.clone()));
        names.push(name);
        proxies.push(Value::Mapping(proxy));
    }
    if proxies.is_empty() { return Err("В ссылке нет поддерживаемых серверов.".into()); }
    reject_notices(&names)?;
    Ok(Some(dump_doc(proxies, names)))
}

fn pick_outbound(config: &Json) -> Option<&Json> {
    let outs = config.get("outbounds")?.as_array()?;
    let mut first = None;
    for outbound in outs {
        let proto = json_str(outbound, "protocol");
        if matches!(proto.as_str(), "freedom" | "blackhole" | "dns" | "loopback") { continue; }
        if json_str(outbound, "tag").eq_ignore_ascii_case("proxy") { return Some(outbound); }
        if first.is_none() { first = Some(outbound); }
    }
    first
}

fn from_xray(name: &str, outbound: &Json) -> Option<Mapping> {
    let proto = json_str(outbound, "protocol");
    let settings = outbound.get("settings")?;
    let stream = outbound.get("streamSettings");
    if proto == "vless" {
        let node = settings.get("vnext")?.as_array()?.first()?;
        let user = node.get("users")?.as_array()?.first()?;
        let port = json_int(node, "port");
        let mut proxy = base("vless", name, &json_str(node, "address"), if port > 0 { port } else { 443 });
        proxy.insert("uuid".into(), Value::String(json_str(user, "id")));
        let enc = json_str(user, "encryption");
        proxy.insert("encryption".into(), Value::String(if enc.is_empty() { "none".into() } else { enc }));
        apply_stream(&mut proxy, stream, &json_str(user, "flow"));
        return Some(proxy);
    }
    if proto == "hysteria" || proto == "hysteria2" {
        let mut version = json_int(settings, "version");
        if version == 0 {
            if let Some(hy) = stream.and_then(|s| s.get("hysteriaSettings")) {
                version = json_int(hy, "version");
            }
        }
        let port = json_int(settings, "port");
        let kind = if version == 1 { "hysteria" } else { "hysteria2" };
        let mut proxy = base(kind, name, &json_str(settings, "address"), if port > 0 { port } else { 443 });
        let mut auth = stream.and_then(|s| s.get("hysteriaSettings")).map(|h| json_str(h, "auth")).unwrap_or_default();
        if auth.is_empty() { auth = json_str(settings, "auth"); }
        if kind == "hysteria" { proxy.insert("auth-str".into(), Value::String(auth)); }
        else { proxy.insert("password".into(), Value::String(auth)); }
        if let Some(tls) = stream.and_then(|s| s.get("tlsSettings")) {
            let sni = json_str(tls, "serverName");
            if !sni.is_empty() { proxy.insert("sni".into(), Value::String(sni)); }
            let fp = json_str(tls, "fingerprint");
            if !fp.is_empty() { proxy.insert("client-fingerprint".into(), Value::String(fp)); }
            if tls.get("allowInsecure").and_then(|v| v.as_bool()) == Some(true) {
                proxy.insert("skip-cert-verify".into(), Value::Bool(true));
            }
        }
        return Some(proxy);
    }
    None
}

fn apply_stream(proxy: &mut Mapping, stream: Option<&Json>, flow: &str) {
    let Some(stream) = stream else { return };
    if !stream.is_object() { return; }
    let mut network = json_str(stream, "network");
    if network.is_empty() { network = "tcp".into(); }
    proxy.insert("network".into(), Value::String(if matches!(network.as_str(), "ws" | "grpc" | "h2" | "http") { network.clone() } else { "tcp".into() }));
    if (network == "tcp" || network.is_empty()) && !flow.is_empty() {
        proxy.insert("flow".into(), Value::String(flow.into()));
    }
    let security = json_str(stream, "security");
    if security == "reality" || security == "tls" {
        proxy.insert("tls".into(), Value::Bool(true));
        let tls_name = if security == "reality" { "realitySettings" } else { "tlsSettings" };
        if let Some(tls) = stream.get(tls_name) {
            let sni = json_str(tls, "serverName");
            if !sni.is_empty() { proxy.insert("servername".into(), Value::String(sni)); }
            let fp = json_str(tls, "fingerprint");
            if !fp.is_empty() { proxy.insert("client-fingerprint".into(), Value::String(fp)); }
            if tls.get("allowInsecure").and_then(|v| v.as_bool()) == Some(true) {
                proxy.insert("skip-cert-verify".into(), Value::Bool(true));
            }
            if security == "reality" {
                let mut reality = Mapping::new();
                let key = json_str(tls, "publicKey");
                if !key.is_empty() { reality.insert("public-key".into(), Value::String(key)); }
                let sid = json_str(tls, "shortId");
                if !sid.is_empty() { reality.insert("short-id".into(), Value::String(sid)); }
                let spider = json_str(tls, "spiderX");
                if !spider.is_empty() { reality.insert("spider-x".into(), Value::String(spider)); }
                proxy.insert("reality-opts".into(), Value::Mapping(reality));
            }
        }
    }
    if network == "grpc" {
        if let Some(grpc) = stream.get("grpcSettings") {
            let service = json_str(grpc, "serviceName");
            if !service.is_empty() {
                let mut opts = Mapping::new();
                opts.insert("grpc-service-name".into(), Value::String(service));
                proxy.insert("grpc-opts".into(), Value::Mapping(opts));
            }
        }
    }
    if network == "ws" {
        if let Some(ws) = stream.get("wsSettings") {
            let mut opts = Mapping::new();
            let path = json_str(ws, "path");
            if !path.is_empty() { opts.insert("path".into(), Value::String(path)); }
            let mut host = json_str(ws, "host");
            if host.is_empty() {
                if let Some(headers) = ws.get("headers").and_then(|h| h.as_object()) {
                    for (k, v) in headers {
                        if k.eq_ignore_ascii_case("host") {
                            host = v.as_str().unwrap_or("").to_string();
                        }
                    }
                }
            }
            if !host.is_empty() {
                let mut headers = Mapping::new();
                headers.insert("Host".into(), Value::String(host));
                opts.insert("headers".into(), Value::Mapping(headers));
            }
            proxy.insert("ws-opts".into(), Value::Mapping(opts));
        }
    }
}

fn parse_share(raw: &str) -> Option<Mapping> {
    if raw.to_ascii_lowercase().starts_with("vmess://") { return parse_vmess(raw); }
    let mut raw = raw.to_string();
    if raw.to_ascii_lowercase().starts_with("ss://") {
        let body = &raw[5..];
        let end = body.find(['#', '?']).unwrap_or(body.len());
        let authority = &body[..end];
        if !authority.contains('@') {
            let decoded = try_b64(authority.trim_end_matches('/'))?;
            let sep = decoded.rfind('@')?;
            if sep == 0 { return None; }
            raw = format!("ss://{}{}{}", urlencoding(&decoded[..sep]), &decoded[sep..], &body[end..]);
        }
    }
    let uri = url::Url::parse(&raw).ok()?;
    match uri.scheme() {
        "vless" => Some(parse_vless(&uri)),
        "trojan" => Some(parse_trojan(&uri)),
        "ss" => parse_ss(&uri),
        "hysteria2" | "hy2" => Some(parse_hy2(&uri)),
        "tuic" => Some(parse_tuic(&uri)),
        _ => None,
    }
}

fn parse_vless(uri: &url::Url) -> Mapping {
    let q = query(uri);
    let name = name(uri, "VLESS");
    let security = q.get("security").cloned().unwrap_or_default();
    let typ = q.get("type").cloned().unwrap_or_else(|| "tcp".into());
    let mut proxy = base("vless", &name, uri.host_str().unwrap_or(""), uri.port().unwrap_or(443) as i64);
    proxy.insert("uuid".into(), Value::String(urlencoding_decode(uri.username())));
    proxy.insert("encryption".into(), Value::String(q.get("encryption").cloned().unwrap_or_else(|| "none".into())));
    let network = if matches!(typ.as_str(), "xhttp" | "httpupgrade" | "ws" | "grpc" | "h2" | "http") { typ.clone() } else { "tcp".into() };
    proxy.insert("network".into(), Value::String(network));
    if let Some(flow) = q.get("flow").filter(|s| !s.is_empty()) { proxy.insert("flow".into(), Value::String(flow.clone())); }
    if let Some(fp) = q.get("fp").filter(|s| !s.is_empty()) { proxy.insert("client-fingerprint".into(), Value::String(fp.clone())); }
    let tls = security == "tls" || security == "reality" || q.contains_key("sni");
    if tls { proxy.insert("tls".into(), Value::Bool(true)); }
    if let Some(sni) = q.get("sni").filter(|s| !s.is_empty()) { proxy.insert("servername".into(), Value::String(sni.clone())); }
    if insecure(&q) { proxy.insert("skip-cert-verify".into(), Value::Bool(true)); }
    if security == "reality" {
        proxy.insert("tls".into(), Value::Bool(true));
        let mut reality = Mapping::new();
        if let Some(pbk) = q.get("pbk").filter(|s| !s.is_empty()) { reality.insert("public-key".into(), Value::String(pbk.clone())); }
        if let Some(sid) = q.get("sid").filter(|s| !s.is_empty()) { reality.insert("short-id".into(), Value::String(sid.clone())); }
        proxy.insert("reality-opts".into(), Value::Mapping(reality));
    }
    if typ == "ws" {
        let mut opts = Mapping::new();
        if let Some(path) = q.get("path").filter(|s| !s.is_empty()) { opts.insert("path".into(), Value::String(path.clone())); }
        if let Some(host) = q.get("host").filter(|s| !s.is_empty()) {
            let mut headers = Mapping::new();
            headers.insert("Host".into(), Value::String(host.clone()));
            opts.insert("headers".into(), Value::Mapping(headers));
        }
        proxy.insert("ws-opts".into(), Value::Mapping(opts));
    }
    if typ == "xhttp" {
        let mut opts = Mapping::new();
        if let Some(path) = q.get("path").filter(|s| !s.is_empty()) { opts.insert("path".into(), Value::String(path.clone())); }
        if let Some(host) = q.get("host").filter(|s| !s.is_empty()) { opts.insert("host".into(), Value::String(host.clone())); }
        if let Some(mode) = q.get("mode").filter(|s| !s.is_empty()) { opts.insert("mode".into(), Value::String(mode.clone())); }
        proxy.insert("xhttp-opts".into(), Value::Mapping(opts));
    }
    if typ == "grpc" {
        if let Some(svc) = q.get("serviceName").filter(|s| !s.is_empty()) {
            let mut opts = Mapping::new();
            opts.insert("grpc-service-name".into(), Value::String(svc.clone()));
            proxy.insert("grpc-opts".into(), Value::Mapping(opts));
        }
    }
    proxy
}

fn parse_hy2(uri: &url::Url) -> Mapping {
    let q = query(uri);
    let mut proxy = base("hysteria2", &name(uri, "Hysteria2"), uri.host_str().unwrap_or(""), uri.port().unwrap_or(443) as i64);
    proxy.insert("password".into(), Value::String(urlencoding_decode(uri.username())));
    if let Some(sni) = q.get("sni").filter(|s| !s.is_empty()) { proxy.insert("sni".into(), Value::String(sni.clone())); }
    if let Some(obfs) = q.get("obfs").filter(|s| !s.is_empty()) { proxy.insert("obfs".into(), Value::String(obfs.clone())); }
    if let Some(op) = q.get("obfs-password").filter(|s| !s.is_empty()) { proxy.insert("obfs-password".into(), Value::String(op.clone())); }
    if let Some(fp) = q.get("fp").filter(|s| !s.is_empty()) { proxy.insert("client-fingerprint".into(), Value::String(fp.clone())); }
    if insecure(&q) { proxy.insert("skip-cert-verify".into(), Value::Bool(true)); }
    proxy
}

fn parse_tuic(uri: &url::Url) -> Mapping {
    let q = query(uri);
    let mut proxy = base("tuic", &name(uri, "TUIC"), uri.host_str().unwrap_or(""), uri.port().unwrap_or(443) as i64);
    let user = urlencoding_decode(uri.username());
    let mut parts = user.splitn(2, ':');
    proxy.insert("uuid".into(), Value::String(parts.next().unwrap_or("").into()));
    if let Some(pass) = parts.next() { proxy.insert("password".into(), Value::String(pass.into())); }
    if let Some(sni) = q.get("sni").filter(|s| !s.is_empty()) { proxy.insert("sni".into(), Value::String(sni.clone())); }
    if let Some(cc) = q.get("congestion_control").filter(|s| !s.is_empty()) { proxy.insert("congestion-controller".into(), Value::String(cc.clone())); }
    if let Some(relay) = q.get("udp_relay_mode").filter(|s| !s.is_empty()) { proxy.insert("udp-relay-mode".into(), Value::String(relay.clone())); }
    if insecure(&q) { proxy.insert("skip-cert-verify".into(), Value::Bool(true)); }
    proxy
}

fn parse_trojan(uri: &url::Url) -> Mapping {
    let q = query(uri);
    let mut proxy = base("trojan", &name(uri, "Trojan"), uri.host_str().unwrap_or(""), uri.port().unwrap_or(443) as i64);
    proxy.insert("password".into(), Value::String(urlencoding_decode(uri.username())));
    proxy.insert("tls".into(), Value::Bool(true));
    if let Some(sni) = q.get("sni").filter(|s| !s.is_empty()) { proxy.insert("sni".into(), Value::String(sni.clone())); }
    if let Some(fp) = q.get("fp").filter(|s| !s.is_empty()) { proxy.insert("client-fingerprint".into(), Value::String(fp.clone())); }
    let typ = q.get("type").cloned().unwrap_or_else(|| "tcp".into());
    proxy.insert("network".into(), Value::String(typ.clone()));
    if typ == "ws" {
        let mut opts = Mapping::new();
        if let Some(path) = q.get("path").filter(|s| !s.is_empty()) { opts.insert("path".into(), Value::String(path.clone())); }
        proxy.insert("ws-opts".into(), Value::Mapping(opts));
    }
    if insecure(&q) { proxy.insert("skip-cert-verify".into(), Value::Bool(true)); }
    proxy
}

fn parse_ss(uri: &url::Url) -> Option<Mapping> {
    let name = name(uri, "SS");
    let mut userinfo = urlencoding_decode(uri.username());
    if let Some(pass) = uri.password() {
        userinfo = format!("{userinfo}:{}", urlencoding_decode(pass));
    }
    if !userinfo.contains(':') {
        userinfo = try_b64(&userinfo)?;
    }
    let (cipher, password) = userinfo.split_once(':')?;
    let mut proxy = base("ss", &name, uri.host_str().unwrap_or(""), uri.port().unwrap_or(443) as i64);
    proxy.insert("cipher".into(), Value::String(cipher.into()));
    proxy.insert("password".into(), Value::String(password.into()));
    Some(proxy)
}

fn parse_vmess(raw: &str) -> Option<Mapping> {
    let json = try_b64(&raw["vmess://".len()..])?;
    let doc: Json = serde_json::from_str(&json).ok()?;
    let s = |k: &str| doc.get(k).map(|v| v.as_str().unwrap_or(&v.to_string()).to_string()).unwrap_or_default();
    let port = s("port").parse::<i64>().unwrap_or(443);
    let ps = s("ps");
    let mut proxy = base("vmess", if ps.trim().is_empty() { "VMess" } else { &ps }, &s("add"), port);
    proxy.insert("uuid".into(), Value::String(s("id")));
    proxy.insert("alterId".into(), Value::Number(s("aid").parse::<i64>().unwrap_or(0).into()));
    let scy = s("scy");
    proxy.insert("cipher".into(), Value::String(if scy.trim().is_empty() { "auto".into() } else { scy }));
    let net = s("net");
    if !net.trim().is_empty() { proxy.insert("network".into(), Value::String(net.clone())); }
    if matches!(s("tls").as_str(), "tls" | "reality") { proxy.insert("tls".into(), Value::Bool(true)); }
    if !s("sni").is_empty() { proxy.insert("servername".into(), Value::String(s("sni"))); }
    if net == "ws" {
        let mut opts = Mapping::new();
        if !s("path").is_empty() { opts.insert("path".into(), Value::String(s("path"))); }
        if !s("host").is_empty() {
            let mut headers = Mapping::new();
            headers.insert("Host".into(), Value::String(s("host")));
            opts.insert("headers".into(), Value::Mapping(headers));
        }
        proxy.insert("ws-opts".into(), Value::Mapping(opts));
    }
    if net == "grpc" && !s("path").is_empty() {
        let mut opts = Mapping::new();
        opts.insert("grpc-service-name".into(), Value::String(s("path")));
        proxy.insert("grpc-opts".into(), Value::Mapping(opts));
    }
    if !s("fp").is_empty() { proxy.insert("client-fingerprint".into(), Value::String(s("fp"))); }
    Some(proxy)
}

fn decorate(yaml: &str) -> String {
    let Ok(mut doc) = serde_yaml::from_str::<Value>(yaml) else { return yaml.to_string(); };
    let Some(list) = doc.get_mut("proxies").and_then(|v| v.as_sequence_mut()) else { return yaml.to_string(); };
    let mut map = HashMap::new();
    let mut names = Vec::new();
    for item in list.iter_mut() {
        let Some(proxy) = item.as_mapping_mut() else { continue };
        let Some(old) = proxy.get("name").and_then(Value::as_str).map(|s| s.to_string()) else { continue };
        if old.is_empty() { continue; }
        let next = flags::apply(&old);
        names.push(next.clone());
        if next != old {
            proxy.insert("name".into(), Value::String(next.clone()));
            map.insert(old, next);
        }
    }
    if reject_notices(&names).is_err() { return yaml.to_string(); }
    if !map.is_empty() { rewrite(&mut doc, &map); }
    serde_yaml::to_string(&doc).unwrap_or_else(|_| yaml.to_string())
}

fn rewrite(node: &mut Value, map: &HashMap<String, String>) {
    match node {
        Value::Sequence(list) => { for item in list { rewrite(item, map); } }
        Value::Mapping(dict) => {
            let keys: Vec<Value> = dict.keys().cloned().collect();
            for key in keys {
                let field = key.as_str().unwrap_or("");
                if let Some(val) = dict.get_mut(&key) {
                    if (field == "dialer-proxy" || field == "proxy") {
                        if let Some(s) = val.as_str() {
                            if let Some(next) = map.get(s) { *val = Value::String(next.clone()); }
                        }
                    } else if field == "rules" {
                        if let Some(rules) = val.as_sequence_mut() { rewrite_rules(rules, map); }
                    } else {
                        rewrite(val, map);
                    }
                }
            }
        }
        _ => {}
    }
}

fn rewrite_rules(rules: &mut Vec<Value>, map: &HashMap<String, String>) {
    for rule in rules {
        let Some(text) = rule.as_str().map(|s| s.to_string()) else { continue };
        if text.to_ascii_lowercase().starts_with("sub-rule,") { continue; }
        let (body, suffix) = if text.to_ascii_lowercase().ends_with(",no-resolve") {
            (text.trim_end_matches(",no-resolve").to_string(), ",no-resolve")
        } else { (text.clone(), "") };
        if let Some(sep) = body.rfind(',') {
            if let Some(next) = map.get(&body[sep + 1..]) {
                *rule = Value::String(format!("{}{next}{suffix}", &body[..=sep]));
            }
        }
    }
}

fn looks_like_clash(text: &str) -> bool {
    let t = text.to_ascii_lowercase();
    t.contains("proxies:") || t.contains("proxy-providers:") || t.contains("proxy-groups:") || t.contains("mixed-port:")
}

fn extract_uris(text: &str) -> Vec<String> {
    text.replace('\r', "").lines().filter_map(|line| {
        let s = line.trim();
        if s.is_empty() || s.starts_with('#') || !s.contains("://") { None } else { Some(s.to_string()) }
    }).collect()
}

fn try_b64(text: &str) -> Option<String> {
    let mut compact = text.trim().replace(['\r', '\n', ' '], "").replace('-', "+").replace('_', "/");
    if compact.is_empty() || compact.contains("://") { return None; }
    let pad = compact.len() % 4;
    if pad > 0 { compact.push_str(&"=".repeat(4 - pad)); }
    let bytes = base64::engine::general_purpose::STANDARD.decode(compact).ok()?;
    String::from_utf8(bytes).ok()
}

fn reject_notices(names: &[String]) -> Result<(), String> {
    if !names.is_empty() && names.iter().all(|n| flags::is_notice(n)) {
        return Err("Поставщик не отдал список серверов. Если указан лимит устройств, удалите неиспользуемое устройство в боте и обновите подписку.".into());
    }
    Ok(())
}

fn unique(name: &str, used: &mut HashSet<String>) -> String {
    let mut n = name.to_string();
    let mut i = 2;
    while !used.insert(n.to_lowercase()) {
        n = format!("{name} {i}");
        i += 1;
    }
    n
}

fn base(kind: &str, name: &str, server: &str, port: i64) -> Mapping {
    let mut m = Mapping::new();
    m.insert("name".into(), Value::String(name.into()));
    m.insert("type".into(), Value::String(kind.into()));
    m.insert("server".into(), Value::String(server.into()));
    m.insert("port".into(), Value::Number(port.into()));
    m
}

fn query(uri: &url::Url) -> HashMap<String, String> {
    uri.query_pairs().map(|(k, v)| (k.into_owned(), v.into_owned())).collect()
}

fn name(uri: &url::Url, fallback: &str) -> String {
    let frag = uri.fragment().unwrap_or("").trim();
    if frag.is_empty() { fallback.into() } else { urlencoding_decode(frag) }
}

fn insecure(q: &HashMap<String, String>) -> bool {
    matches!(q.get("allowInsecure").map(|s| s.as_str()), Some("1") | Some("true"))
        || matches!(q.get("insecure").map(|s| s.as_str()), Some("1") | Some("true"))
}

fn json_str(el: &Json, key: &str) -> String {
    match el.get(key) {
        Some(Json::String(s)) => s.trim().to_string(),
        Some(Json::Number(n)) => n.to_string(),
        _ => String::new(),
    }
}

fn json_int(el: &Json, key: &str) -> i64 {
    match el.get(key) {
        Some(Json::Number(n)) => n.as_i64().unwrap_or(0),
        Some(Json::String(s)) => s.parse().unwrap_or(0),
        _ => 0,
    }
}

fn urlencoding_decode(s: &str) -> String {
    urlencoding_decode_inner(s).unwrap_or_else(|| s.to_string())
}

fn urlencoding_decode_inner(s: &str) -> Option<String> {
    let bytes = s.as_bytes();
    let mut out = Vec::new();
    let mut i = 0;
    while i < bytes.len() {
        match bytes[i] {
            b'%' if i + 2 < bytes.len() => {
                let h = std::str::from_utf8(&bytes[i + 1..i + 3]).ok()?;
                out.push(u8::from_str_radix(h, 16).ok()?);
                i += 3;
            }
            b'+' => { out.push(b' '); i += 1; }
            c => { out.push(c); i += 1; }
        }
    }
    String::from_utf8(out).ok()
}

fn urlencoding(s: &str) -> String {
    let mut out = String::new();
    for b in s.bytes() {
        if b.is_ascii_alphanumeric() || matches!(b, b'-' | b'_' | b'.' | b'~') { out.push(b as char); }
        else { out.push_str(&format!("%{b:02X}")); }
    }
    out
}

fn map_val(pairs: &[(&str, Value)]) -> Value {
    let mut m = Mapping::new();
    for (k, v) in pairs { m.insert((*k).into(), v.clone()); }
    Value::Mapping(m)
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn vless_reality() {
        let yaml = to_clash_yaml("vless://11111111-1111-1111-1111-111111111111@example.com:443?security=reality&type=tcp&sni=www.microsoft.com&fp=chrome&pbk=abc&sid=def#Amsterdam").unwrap();
        assert!(yaml.contains("vless"));
        assert!(yaml.contains("reality-opts"));
        assert!(yaml.contains("example.com"));
    }
}
