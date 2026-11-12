use crate::models::{AppSettings, ProfileInfo};

const DNS: &[&str] = &[
    "https://1.1.1.1/dns-query",
    "https://1.0.0.1/dns-query",
    "https://dns.google/dns-query",
    "https://8.8.8.8/dns-query",
    "https://9.9.9.9/dns-query",
    "https://149.112.112.112/dns-query",
    "https://dns.quad9.net/dns-query",
    "https://security.cloudflare-dns.com/dns-query",
    "https://mozilla.cloudflare-dns.com/dns-query",
    "https://cloudflare-dns.com/dns-query",
];

const FALLBACK: &[&str] = &[
    "https://security.cloudflare-dns.com/dns-query",
    "tls://1.1.1.1",
    "tls://8.8.8.8",
    "tls://9.9.9.9",
    "https://dns.google/dns-query",
];

pub fn prepare(src: &AppSettings) -> AppSettings {
    let mut s = src.clone();
    apply(&mut s);
    if s.dns_diversity { rotate(&mut s, false); }
    if s.smart_direct_map && !s.smart_direct_domains.is_empty() {
        let mut set: Vec<String> = s.custom_direct_rules.lines().map(|l| l.trim().to_string()).filter(|l| !l.is_empty()).collect();
        for d in &s.smart_direct_domains {
            if !d.trim().is_empty() && !set.iter().any(|x| x.eq_ignore_ascii_case(d.trim())) {
                set.push(d.trim().to_string());
            }
        }
        set.sort();
        s.custom_direct_rules = set.join("\n");
    }
    s
}

pub fn prepare_profile(src: &AppSettings, profile: Option<&ProfileInfo>) -> AppSettings {
    let mut s = prepare(src);
    if let Some(profile) = profile {
        if let Some(fp) = profile.fingerprint_override.as_deref().map(str::trim).filter(|s| !s.is_empty()) {
            s.random_fingerprint = false;
            s.global_client_fingerprint = fp.to_string();
        }
        if let Some(ns) = profile.nameserver_override.as_ref().filter(|n| !n.is_empty()) {
            s.dns.nameserver = ns.clone();
        }
    }
    s
}

pub fn apply(s: &mut AppSettings) {
    match s.network_persona.trim().to_ascii_lowercase().as_str() {
        "home" => {
            if s.mtu < 1280 { s.mtu = 1400; }
            s.global_client_fingerprint = "chrome".into();
            s.random_fingerprint = false;
            s.rf_rush_mode = true;
            s.prefer_rush_lane = true;
            s.block_quic = true;
            s.dns_via_proxy = true;
        }
        "mobile" => {
            s.mtu = 1280;
            s.random_fingerprint = true;
            s.rf_rush_mode = true;
            s.prefer_rush_lane = true;
            s.block_quic = true;
            s.dns_via_proxy = true;
            s.tcp_concurrent = true;
            s.high_priority = true;
        }
        "corporate" => {
            s.mtu = 1200;
            s.random_fingerprint = false;
            s.global_client_fingerprint = "firefox".into();
            s.rf_rush_mode = false;
            s.prefer_rush_lane = false;
            s.stealth_mode = true;
            s.block_quic = true;
            s.block_stun = true;
            s.dns_via_proxy = true;
            s.dns_strict_no_fallback = true;
            s.silent_logs = true;
        }
        _ => {}
    }
}

pub fn rotate(s: &mut AppSettings, advance: bool) {
    let mut idx = s.dns_pool_index.unsigned_abs() as usize % DNS.len();
    if advance {
        idx = (idx + 2) % DNS.len();
        s.dns_pool_index = idx as i32;
        s.last_dns_rotate_at = Some(chrono::Utc::now().to_rfc3339());
    }
    s.dns.nameserver = vec![DNS[idx].into(), DNS[(idx + 1) % DNS.len()].into(), DNS[(idx + 3) % DNS.len()].into()];
    if s.dns_strict_no_fallback {
        s.dns.fallback.clear();
    } else {
        let f = idx % FALLBACK.len();
        s.dns.fallback = vec![FALLBACK[f].into(), FALLBACK[(f + 1) % FALLBACK.len()].into()];
    }
    s.dns.default_nameserver = vec!["1.1.1.1".into(), "8.8.8.8".into(), "9.9.9.9".into()];
}
