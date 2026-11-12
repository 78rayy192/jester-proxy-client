use crate::models::AppSettings;
use serde_yaml::{Mapping, Value};

pub fn merge(profile_yaml: &str, prepared: &AppSettings, secret: &str) -> Result<String, String> {
    let normalized = crate::normalize::to_clash_yaml(profile_yaml).unwrap_or_else(|_| profile_yaml.to_string());
    let mut doc = serde_yaml::from_str::<Value>(&normalized)
        .map_err(|e| format!("Не удалось прочитать профиль: {e}"))?;
    let map = doc.as_mapping_mut().ok_or("Профиль не является YAML-объектом")?;
    set(map, "mixed-port", num(prepared.mixed_port as i64));
    set(map, "socks-port", num(prepared.socks_port as i64));
    set(map, "allow-lan", Value::Bool(prepared.allow_lan));
    set(map, "bind-address", s(&bind_address(prepared)));
    set(map, "mode", s(&prepared.mode));
    let log_level = if prepared.silent_logs { "silent" } else { prepared.log_level.as_str() };
    set(map, "log-level", s(log_level));
    set(map, "ipv6", Value::Bool(prepared.ipv6));
    set(map, "unified-delay", Value::Bool(prepared.unified_delay));
    set(map, "tcp-concurrent", Value::Bool(prepared.tcp_concurrent));
    let (mut idle, mut interval) = if prepared.keep_alive { (10, 10) } else { (0, 0) };
    if prepared.traffic_padding && prepared.keep_alive {
        let seed = (std::time::SystemTime::now().duration_since(std::time::UNIX_EPOCH).map(|d| d.as_millis() as i32).unwrap_or(1)) ^ prepared.dns_pool_index.wrapping_mul(397);
        idle = 8 + (seed.unsigned_abs() % 11) as i64;
        interval = 8 + ((seed.wrapping_mul(3)).unsigned_abs() % 9) as i64;
    }
    set(map, "keep-alive-idle", num(idle));
    set(map, "keep-alive-interval", num(interval));
    let find = if prepared.per_app_fast_path && prepared.find_process_mode != "always" {
        "strict"
    } else {
        match prepared.find_process_mode.as_str() { "strict" | "always" => prepared.find_process_mode.as_str(), _ => "off" }
    };
    set(map, "find-process-mode", s(find));
    let fp = if prepared.random_fingerprint { "random" } else if prepared.global_client_fingerprint.trim().is_empty() { "chrome" } else { prepared.global_client_fingerprint.as_str() };
    set(map, "global-client-fingerprint", s(fp));
    set(map, "global-ua", s("Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/131.0.0.0 Safari/537.36"));
    let mut profile = Mapping::new();
    profile.insert(s("store-selected"), Value::Bool(prepared.store_selected));
    profile.insert(s("store-fake-ip"), Value::Bool(true));
    profile.insert(s("tracing"), Value::Bool(false));
    set(map, "profile", Value::Mapping(profile));
    if prepared.sniffer_enabled {
        let mut sniff = Mapping::new();
        sniff.insert(s("HTTP"), map_of(&[("ports", Value::Sequence(vec![num(80), num(443)]))]));
        sniff.insert(s("TLS"), map_of(&[("ports", Value::Sequence(vec![num(443), num(8443)]))]));
        let mut sniffer = Mapping::new();
        sniffer.insert(s("enable"), Value::Bool(true));
        sniffer.insert(s("parse-pure-ip"), Value::Bool(true));
        sniffer.insert(s("force-dns-mapping"), Value::Bool(true));
        sniffer.insert(s("override-destination"), Value::Bool(true));
        sniffer.insert(s("sniff"), Value::Mapping(sniff));
        sniffer.insert(s("skip-domain"), seq_str(&["Mijia Cloud", "+.push.apple.com", "+.apple.com", "+.microsoft.com", "+.windows.com"]));
        set(map, "sniffer", Value::Mapping(sniffer));
    } else {
        set(map, "sniffer", map_of(&[("enable", Value::Bool(false))]));
    }
    set(map, "external-controller", s(&format!("127.0.0.1:{}", prepared.external_port)));
    set(map, "secret", s(secret));
    remove(map, &["external-controller-tls", "external-controller-unix", "external-controller-pipe", "external-doh-server"]);
    let mtu = if prepared.mtu > 0 { prepared.mtu } else { 1400 };
    let mut tun = Mapping::new();
    tun.insert(s("enable"), Value::Bool(prepared.tun_enabled));
    tun.insert(s("stack"), s(if prepared.tun_stack.trim().is_empty() { "mixed" } else { &prepared.tun_stack }));
    tun.insert(s("auto-route"), Value::Bool(prepared.auto_route));
    tun.insert(s("auto-detect-interface"), Value::Bool(prepared.auto_detect_interface));
    tun.insert(s("auto-redirect"), Value::Bool(prepared.auto_redirect));
    tun.insert(s("strict-route"), Value::Bool(prepared.strict_route));
    tun.insert(s("mtu"), num(mtu as i64));
    tun.insert(s("endpoint-independent-nat"), Value::Bool(prepared.endpoint_independent_nat));
    tun.insert(s("dns-hijack"), if prepared.dns_hijack { seq_str(&["any:53", "tcp://any:53"]) } else { Value::Sequence(vec![]) });
    tun.insert(s("inet4-address"), seq_str(&[&prepared.inet4_address]));
    tun.insert(s("udp-timeout"), num(30));
    set(map, "tun", Value::Mapping(tun));
    if prepared.dns.enable {
        let via = prepared.dns_via_proxy;
        let nameservers = wrap_list(&prepared.dns.nameserver, via);
        let mut fallbacks = wrap_list(&prepared.dns.fallback, via);
        if prepared.dns_strict_no_fallback { fallbacks = Value::Sequence(vec![]); }
        let mode = if prepared.dns.enhanced_mode == "redir-host" { "redir-host" } else { "fake-ip" };
        let mut dns = Mapping::new();
        dns.insert(s("enable"), Value::Bool(true));
        dns.insert(s("ipv6"), Value::Bool(prepared.ipv6));
        dns.insert(s("prefer-h3"), Value::Bool(false));
        dns.insert(s("use-hosts"), Value::Bool(prepared.dns.use_hosts));
        dns.insert(s("enhanced-mode"), s(mode));
        dns.insert(s("fake-ip-range"), s(&prepared.dns.fake_ip_range));
        dns.insert(s("fake-ip-filter"), fake_filter(&prepared));
        dns.insert(s("default-nameserver"), seq_owned(&prepared.dns.default_nameserver));
        dns.insert(s("nameserver"), nameservers);
        dns.insert(s("fallback"), fallbacks);
        dns.insert(s("cache-algorithm"), s("arc"));
        dns.insert(s("proxy-server-nameserver"), seq_owned(&prepared.dns.default_nameserver));
        dns.insert(s("direct-nameserver"), seq_owned(&prepared.dns.default_nameserver));
        let mut filter = Mapping::new();
        filter.insert(s("geoip"), Value::Bool(true));
        filter.insert(s("geoip-code"), s("RU"));
        filter.insert(s("ipcidr"), seq_str(&["240.0.0.0/4", "0.0.0.0/32"]));
        dns.insert(s("fallback-filter"), Value::Mapping(filter));
        if prepared.dns_respect_rules || via { dns.insert(s("respect-rules"), Value::Bool(true)); }
        set(map, "dns", Value::Mapping(dns));
    }
    ensure_groups(map, &prepared);
    apply_rules(map, &prepared);
    serde_yaml::to_string(&doc).map_err(|e| e.to_string())
}

fn ensure_groups(doc: &mut Mapping, settings: &AppSettings) {
    let proxies = get_seq(doc, "proxies");
    let mut names = Vec::new();
    let mut fast = Vec::new();
    for item in proxies {
        let name = name_of(&item);
        if name.is_empty() { continue; }
        if is_fast(&item) { fast.push(name.clone()); }
        names.push(name);
    }
    let mut groups = get_seq(doc, "proxy-groups");
    let has = |g: &[Value], n: &str| g.iter().any(|x| name_of(x).eq_ignore_ascii_case(n));
    let rush = settings.rf_rush_mode && !fast.is_empty();
    let udp_lane = settings.split_udp_policy && (!fast.is_empty() || !names.is_empty());
    let udp_members = if fast.is_empty() { names.clone() } else { fast.clone() };
    if !has(&groups, "AUTO") && !names.is_empty() {
        groups.push(url_test("AUTO", &names, if settings.stealth_mode { 600 } else { 300 }, 80, true));
    }
    if rush && !has(&groups, "RUSH") {
        groups.insert(0, url_test("RUSH", &fast, 90, 40, false));
    }
    if udp_lane && !has(&groups, "UDP-FAST") {
        groups.insert(0, url_test("UDP-FAST", &udp_members, 120, 50, true));
    }
    let mut children = Vec::new();
    if rush && settings.prefer_rush_lane { children.push("RUSH".to_string()); }
    children.push("AUTO".into());
    if rush && !settings.prefer_rush_lane { children.push("RUSH".into()); }
    children.extend(names.iter().cloned());
    if !has(&groups, "PROXY") && !names.is_empty() {
        let mut m = Mapping::new();
        m.insert(s("name"), s("PROXY"));
        m.insert(s("type"), s("select"));
        m.insert(s("lazy"), Value::Bool(true));
        m.insert(s("proxies"), seq_owned(&children));
        groups.insert(0, Value::Mapping(m));
    }
    set(doc, "proxy-groups", Value::Sequence(groups));
}

fn apply_rules(doc: &mut Mapping, settings: &AppSettings) {
    let mut rules = vec![
        "GEOIP,PRIVATE,DIRECT,no-resolve",
        "IP-CIDR,127.0.0.0/8,DIRECT,no-resolve",
        "IP-CIDR,10.0.0.0/8,DIRECT,no-resolve",
        "IP-CIDR,172.16.0.0/12,DIRECT,no-resolve",
        "IP-CIDR,192.168.0.0/16,DIRECT,no-resolve",
        "IP-CIDR,169.254.0.0/16,DIRECT,no-resolve",
        "IP-CIDR,224.0.0.0/4,DIRECT,no-resolve",
        "IP-CIDR6,::1/128,DIRECT,no-resolve",
        "IP-CIDR6,fc00::/7,DIRECT,no-resolve",
        "IP-CIDR6,fe80::/10,DIRECT,no-resolve",
    ].into_iter().map(|s| s.to_string()).collect::<Vec<_>>();
    for d in ALWAYS {
        rules.push(format!("DOMAIN-SUFFIX,{d},PROXY"));
    }
    if settings.per_app_fast_path {
        for proc in settings.fast_path_processes.split([',', ';', '\n', '\r']) {
            let proc = proc.trim();
            if proc.is_empty() { continue; }
            let name = if proc.to_ascii_lowercase().ends_with(".exe") { proc.to_string() } else { format!("{proc}.exe") };
            rules.push(format!("PROCESS-NAME,{name},PROXY"));
        }
    }
    if settings.block_stun {
        for d in ["stun.cloudflare.com", "stun.l.google.com", "stun1.l.google.com", "stun2.l.google.com", "stun3.l.google.com", "stun4.l.google.com"] {
            rules.push(format!("DOMAIN-SUFFIX,{d},REJECT"));
        }
        rules.push("AND,((NETWORK,udp),(DST-PORT,3478)),REJECT".into());
        rules.push("AND,((NETWORK,udp),(DST-PORT,5349)),REJECT".into());
    }
    let has_udp = get_seq(doc, "proxy-groups").iter().any(|g| name_of(g).eq_ignore_ascii_case("UDP-FAST"));
    if settings.split_udp_policy && has_udp {
        for spec in port_specs(&settings.udp_fast_ports) {
            if spec == "3478" || spec == "5349" { continue; }
            rules.push(format!("AND,((NETWORK,udp),(DST-PORT,{spec})),UDP-FAST"));
        }
    }
    if settings.block_quic {
        rules.push("AND,((NETWORK,udp),(DST-PORT,443)),REJECT".into());
    }
    for d in ["ipify.org", "ifconfig.me", "ipinfo.io", "ip.sb"] {
        rules.push(format!("DOMAIN-SUFFIX,{d},PROXY"));
    }
    rules.push("DOMAIN,api.ipify.org,PROXY".into());
    rules.push("DOMAIN,ifconfig.co,PROXY".into());
    if settings.rf_rush_mode {
        for d in RUSH {
            rules.push(format!("DOMAIN-SUFFIX,{d},PROXY"));
        }
        rules.push("GEOIP,RU,DIRECT,no-resolve".into());
    }
    let mut providers = Mapping::new();
    providers.insert(s("jester-direct"), file_provider("domain", "custom-direct.txt"));
    providers.insert(s("jester-proxy"), file_provider("domain", "custom-proxy.txt"));
    rules.push("RULE-SET,jester-direct,DIRECT".into());
    rules.push("RULE-SET,jester-proxy,PROXY".into());
    if settings.refilter_enabled {
        if settings.use_mrs_rules {
            providers.insert(s("refilter-domains"), http_provider("domain", "mrs", &settings.refilter_domains_mrs_url, "refilter-domains.mrs"));
            rules.push("RULE-SET,refilter-domains,PROXY".into());
            if settings.refilter_use_ip {
                providers.insert(s("refilter-ipsum"), http_provider("ipcidr", "mrs", &settings.refilter_ip_mrs_url, "refilter-ipsum.mrs"));
                rules.push("RULE-SET,refilter-ipsum,PROXY,no-resolve".into());
            }
        } else {
            providers.insert(s("refilter-domains"), http_provider("domain", "text", &settings.refilter_domains_url, "refilter-domains.txt"));
            rules.push("RULE-SET,refilter-domains,PROXY".into());
            if settings.refilter_use_ip {
                providers.insert(s("refilter-ipsum"), http_provider("ipcidr", "text", &settings.refilter_ip_url, "refilter-ipsum.txt"));
                rules.push("RULE-SET,refilter-ipsum,PROXY,no-resolve".into());
            }
        }
    }
    let fin = if settings.final_outbound.eq_ignore_ascii_case("PROXY") { "PROXY" } else { "DIRECT" };
    rules.push(format!("MATCH,{fin}"));
    set(doc, "rule-providers", Value::Mapping(providers));
    set(doc, "rules", seq_owned(&rules));
}

fn bind_address(settings: &AppSettings) -> String {
    if !settings.allow_lan { return "127.0.0.1".into(); }
    let raw = settings.bind_address.trim();
    if raw.is_empty() || raw == "127.0.0.1" || raw.eq_ignore_ascii_case("localhost") { "*".into() } else { raw.into() }
}

fn url_test(name: &str, proxies: &[String], interval: i64, tolerance: i64, lazy: bool) -> Value {
    let mut m = Mapping::new();
    m.insert(s("name"), s(name));
    m.insert(s("type"), s("url-test"));
    m.insert(s("url"), s("https://www.gstatic.com/generate_204"));
    m.insert(s("interval"), num(interval));
    m.insert(s("tolerance"), num(tolerance));
    m.insert(s("lazy"), Value::Bool(lazy));
    m.insert(s("expected-status"), s("204"));
    m.insert(s("proxies"), seq_owned(proxies));
    Value::Mapping(m)
}

fn file_provider(behavior: &str, file: &str) -> Value {
    map_of(&[
        ("type", s("file")),
        ("behavior", s(behavior)),
        ("format", s("text")),
        ("path", s(&format!("./ruleset/{file}"))),
    ])
}

fn http_provider(behavior: &str, format: &str, url: &str, file: &str) -> Value {
    map_of(&[
        ("type", s("http")),
        ("behavior", s(behavior)),
        ("format", s(format)),
        ("url", s(url)),
        ("path", s(&format!("./ruleset/{file}"))),
        ("interval", num(86400)),
        ("proxy", s("DIRECT")),
    ])
}

fn fake_filter(settings: &AppSettings) -> Value {
    let mut list = settings.dns.fake_ip_filter.clone();
    for extra in ["+.cursor.com", "+.cursor.sh", "+.cursorapi.com", "+.github.com", "+.githubusercontent.com", "+.githubassets.com", "+.ghcr.io"] {
        if !list.iter().any(|x| x.eq_ignore_ascii_case(extra)) { list.push(extra.into()); }
    }
    seq_owned(&list)
}

fn port_specs(raw: &str) -> Vec<String> {
    raw.split([',', ' ', ';', '\n', '\r']).filter_map(|part| {
        let part = part.trim();
        if part.is_empty() { return None; }
        if let Some((a, b)) = part.split_once('-') {
            let a = a.parse::<i32>().ok()?;
            let b = b.parse::<i32>().ok()?;
            if a > 0 && b >= a && b <= 65535 { return Some(format!("{a}-{b}")); }
            return None;
        }
        let p = part.parse::<i32>().ok()?;
        if (1..=65535).contains(&p) { Some(part.to_string()) } else { None }
    }).collect()
}

fn wrap_list(items: &[String], via: bool) -> Value {
    Value::Sequence(items.iter().map(|item| s(&wrap_dns(item, via))).collect())
}

fn wrap_dns(server: &str, via: bool) -> String {
    if !via || server.contains('#') { return server.to_string(); }
    let lower = server.to_ascii_lowercase();
    if lower.starts_with("https://") || lower.starts_with("tls://") || lower.starts_with("quic://") {
        format!("{server}#PROXY")
    } else { server.to_string() }
}

fn is_fast(item: &Value) -> bool {
    let t = item.as_mapping().and_then(|m| m.get("type")).and_then(Value::as_str).unwrap_or("");
    matches!(t.to_ascii_lowercase().as_str(), "hysteria2" | "hysteria" | "tuic" | "vless" | "trojan" | "wireguard")
}

fn name_of(item: &Value) -> String {
    item.as_mapping().and_then(|m| m.get("name")).and_then(Value::as_str).unwrap_or("").to_string()
}

fn get_seq(doc: &Mapping, key: &str) -> Vec<Value> {
    doc.iter().find(|(k, _)| k.as_str().map(|s| s.eq_ignore_ascii_case(key)).unwrap_or(false))
        .and_then(|(_, v)| v.as_sequence().cloned())
        .unwrap_or_default()
}

fn set(doc: &mut Mapping, key: &str, value: Value) {
    let old: Vec<Value> = doc.keys().filter(|k| k.as_str().map(|s| s.eq_ignore_ascii_case(key)).unwrap_or(false)).cloned().collect();
    for k in old { doc.remove(&k); }
    doc.insert(s(key), value);
}

fn remove(doc: &mut Mapping, keys: &[&str]) {
    let old: Vec<Value> = doc.keys().filter(|k| k.as_str().map(|s| keys.iter().any(|x| s.eq_ignore_ascii_case(x))).unwrap_or(false)).cloned().collect();
    for k in old { doc.remove(&k); }
}

fn s(v: &str) -> Value { Value::String(v.to_string()) }
fn num(v: i64) -> Value { Value::Number(v.into()) }
fn seq_str(items: &[&str]) -> Value { Value::Sequence(items.iter().map(|x| s(x)).collect()) }
fn seq_owned(items: &[String]) -> Value { Value::Sequence(items.iter().cloned().map(Value::String).collect()) }
fn map_of(pairs: &[(&str, Value)]) -> Value {
    let mut m = Mapping::new();
    for (k, v) in pairs { m.insert(s(k), v.clone()); }
    Value::Mapping(m)
}

const ALWAYS: &[&str] = &[
    "cursor.com", "cursor.sh", "cursorapi.com", "todesktop.com",
    "github.com", "githubusercontent.com", "githubassets.com", "github.io",
    "ghcr.io", "github.dev", "githubcopilot.com",
    "vscode.dev", "vscode-cdn.net", "visualstudio.com", "gitlab.com", "bitbucket.org",
];

const RUSH: &[&str] = &[
    "youtube.com", "youtu.be", "googlevideo.com", "ytimg.com", "ggpht.com",
    "instagram.com", "cdninstagram.com", "facebook.com", "fbcdn.net", "meta.com",
    "twitter.com", "x.com", "twimg.com", "t.co", "tiktok.com", "tiktokv.com",
    "discord.com", "discord.gg", "discordapp.com", "discord.media",
    "telegram.org", "t.me", "telegra.ph", "signal.org",
    "openai.com", "chatgpt.com", "oaistatic.com", "anthropic.com", "claude.ai",
    "googleapis.com", "perplexity.ai", "grok.com", "x.ai", "notion.so",
    "github.com", "githubusercontent.com", "npmjs.com", "reddit.com", "twitch.tv",
    "spotify.com", "netflix.com", "google.com", "gstatic.com", "wikipedia.org",
    "play.google.com", "apple.com", "icloud.com", "proton.me", "mullvad.net",
];
