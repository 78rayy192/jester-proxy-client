//! Решения, которые не зависят от окна и ядра.

#[derive(Clone, Copy, Debug, PartialEq, Eq)]
pub enum ExitStep {
    Adopt,
    Same,
    Pending,
    Confirmed,
}

pub fn next_exit(known: &Option<String>, pending: &Option<String>, seen: &str) -> (ExitStep, Option<String>, Option<String>) {
    if known.as_deref() == Some(seen) {
        return (ExitStep::Same, known.clone(), None);
    }
    if known.is_none() {
        return (ExitStep::Adopt, Some(seen.to_string()), None);
    }
    if pending.as_deref() == Some(seen) {
        return (ExitStep::Confirmed, Some(seen.to_string()), None);
    }
    (ExitStep::Pending, known.clone(), Some(seen.to_string()))
}

/// Два плохих замера подряд и отсутствие паузы после прошлой смены.
pub fn next_latency(streak: u32, measured: i32, budget: i32, cooling: bool) -> (u32, bool) {
    if measured <= 0 || measured <= budget {
        return (0, false);
    }
    let streak = streak.saturating_add(1);
    if streak >= 2 && !cooling {
        (0, true)
    } else {
        (streak, false)
    }
}

pub fn should_restore(new_config: &str, previous_good: Option<&str>) -> bool {
    match previous_good {
        Some(prev) if !prev.trim().is_empty() && prev != new_config => true,
        _ => false,
    }
}

pub fn domain_lines(content: &str) -> Vec<String> {
    content
        .lines()
        .map(str::trim)
        .filter(|line| !line.is_empty() && !line.to_ascii_lowercase().starts_with("process:"))
        .map(|line| line.to_string())
        .collect()
}

pub fn process_tokens(content: &str) -> Vec<String> {
    content
        .lines()
        .filter_map(|line| {
            let line = line.trim();
            let rest = line.strip_prefix("process:").or_else(|| line.strip_prefix("PROCESS:"))?;
            let name = rest.trim();
            if name.is_empty() { None } else { Some(name.to_string()) }
        })
        .collect()
}

pub fn process_rule_names(raw: &str) -> Vec<String> {
    let mut out = Vec::new();
    for token in raw.split([',', ';', '\n', '\r']) {
        let token = token.trim();
        if token.is_empty() || token.to_ascii_lowercase().starts_with("process:") { continue; }
        for name in expand_process(token) {
            if !out.iter().any(|have: &String| have.eq_ignore_ascii_case(&name)) {
                out.push(name);
            }
        }
    }
    out
}

fn expand_process(token: &str) -> Vec<String> {
    let key = token.trim().trim_end_matches(".exe").trim_end_matches(".EXE").to_ascii_lowercase();
    let linux: &[&str] = match key.as_str() {
        "chrome" | "google-chrome" | "google-chrome-stable" => &["chrome", "google-chrome", "google-chrome-stable"],
        "msedge" | "microsoft-edge" | "edge" => &["msedge", "microsoft-edge"],
        "firefox" => &["firefox"],
        "brave" | "brave-browser" => &["brave", "brave-browser"],
        "discord" => &["discord", "Discord"],
        "telegram" => &["telegram", "telegram-desktop"],
        "cursor" => &["cursor"],
        "code" => &["code", "code-oss"],
        _ => &[],
    };
    if cfg!(windows) {
        let exe = if token.to_ascii_lowercase().ends_with(".exe") {
            token.to_string()
        } else {
            format!("{token}.exe")
        };
        vec![exe]
    } else if linux.is_empty() {
        vec![key]
    } else {
        linux.iter().map(|s| (*s).to_string()).collect()
    }
}

pub fn tun_exclude_interfaces() -> Vec<&'static str> {
    if cfg!(windows) {
        vec![
            "Loopback",
            "vEthernet (WSL)",
            "vEthernet (WSL (Hyper-V firewall))",
            "vEthernet (Default Switch)",
            "DockerNAT",
            "Hyper-V Virtual Ethernet Adapter",
        ]
    } else {
        vec!["docker0", "podman0", "virbr0", "cni0", "flannel.1", "br-docker"]
    }
}

pub fn encode_dns_query(id: u16, host: &str) -> Vec<u8> {
    let mut packet = Vec::with_capacity(64);
    packet.extend_from_slice(&id.to_be_bytes());
    packet.extend_from_slice(&0x0100u16.to_be_bytes());
    packet.extend_from_slice(&1u16.to_be_bytes());
    packet.extend_from_slice(&[0, 0, 0, 0, 0, 0]);
    for label in host.split('.') {
        if label.is_empty() || label.len() > 63 { continue; }
        packet.push(label.len() as u8);
        packet.extend_from_slice(label.as_bytes());
    }
    packet.push(0);
    packet.extend_from_slice(&16u16.to_be_bytes());
    packet.extend_from_slice(&1u16.to_be_bytes());
    packet
}

pub fn parse_dns_txt(packet: &[u8]) -> Option<String> {
    if packet.len() < 12 { return None; }
    let questions = u16::from_be_bytes([packet[4], packet[5]]) as usize;
    let answers = u16::from_be_bytes([packet[6], packet[7]]) as usize;
    if answers == 0 { return None; }
    let mut index = 12usize;
    for _ in 0..questions {
        index = skip_name(packet, index)?;
        index = index.checked_add(4)?;
        if index > packet.len() { return None; }
    }
    for _ in 0..answers {
        index = skip_name(packet, index)?;
        if index + 10 > packet.len() { return None; }
        let kind = u16::from_be_bytes([packet[index], packet[index + 1]]);
        let len = u16::from_be_bytes([packet[index + 8], packet[index + 9]]) as usize;
        index += 10;
        if index + len > packet.len() { return None; }
        if kind == 16 {
            let text = read_txt(&packet[index..index + len]);
            if !text.is_empty() { return Some(text); }
        }
        index += len;
    }
    None
}

fn read_txt(rdata: &[u8]) -> String {
    let mut out = String::new();
    let mut index = 0;
    while index < rdata.len() {
        let len = rdata[index] as usize;
        index += 1;
        if index + len > rdata.len() { break; }
        out.push_str(&String::from_utf8_lossy(&rdata[index..index + len]));
        index += len;
    }
    out.trim_matches('"').trim().to_string()
}

fn skip_name(packet: &[u8], mut index: usize) -> Option<usize> {
    let mut hops = 0;
    loop {
        if index >= packet.len() || hops > 16 { return None; }
        let len = packet[index];
        if len == 0 { return Some(index + 1); }
        if len & 0xC0 == 0xC0 {
            if index + 1 >= packet.len() { return None; }
            return Some(index + 2);
        }
        index = index.checked_add(1 + len as usize)?;
        hops += 1;
    }
}

pub fn dns_matches(exit_ip: &str, dns_ip: &str) -> bool {
    !exit_ip.is_empty() && exit_ip.eq_ignore_ascii_case(dns_ip.trim())
}

pub fn release_asset<'a>(assets: &'a [serde_json::Value], os_name: &str) -> Option<(&'a str, &'a str)> {
    let mut fallback = None;
    for asset in assets {
        let Some(name) = asset.get("name").and_then(|v| v.as_str()) else { continue };
        let Some(url) = asset.get("browser_download_url").and_then(|v| v.as_str()) else { continue };
        if !trusted_download(url) { continue; }
        let lower = name.to_ascii_lowercase();
        if !lower.contains(os_name) { continue; }
        let arch = lower.contains("x64") || lower.contains("amd64") || lower.contains("x86_64");
        if arch { return Some((name, url)); }
        if fallback.is_none() { fallback = Some((name, url)); }
    }
    fallback
}

pub fn trusted_download(url: &str) -> bool {
    let Ok(parsed) = url::Url::parse(url) else { return false };
    if parsed.scheme() != "https" { return false; }
    matches!(parsed.host_str(), Some("github.com") | Some("objects.githubusercontent.com") | Some("release-assets.githubusercontent.com") | Some("github-releases.githubusercontent.com"))
        || parsed.host_str().unwrap_or("").ends_with(".githubusercontent.com")
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn exit_needs_two_sightings() {
        let (step, known, pending) = next_exit(&None, &None, "1.1.1.1");
        assert_eq!(step, ExitStep::Adopt);
        let (step, known, pending) = next_exit(&known, &pending, "2.2.2.2");
        assert_eq!(step, ExitStep::Pending);
        assert_eq!(pending.as_deref(), Some("2.2.2.2"));
        let (step, known, _) = next_exit(&known, &pending, "2.2.2.2");
        assert_eq!(step, ExitStep::Confirmed);
        assert_eq!(known.as_deref(), Some("2.2.2.2"));
        let (step, _, _) = next_exit(&known, &None, "2.2.2.2");
        assert_eq!(step, ExitStep::Same);
    }

    #[test]
    fn latency_waits_for_second_sample_and_cooldown() {
        let (streak, switch) = next_latency(0, 400, 250, false);
        assert_eq!((streak, switch), (1, false));
        let (streak, switch) = next_latency(streak, 400, 250, false);
        assert_eq!((streak, switch), (0, true));
        let (_, switch) = next_latency(1, 400, 250, true);
        assert!(!switch);
        let (streak, switch) = next_latency(2, 100, 250, false);
        assert_eq!((streak, switch), (0, false));
    }

    #[test]
    fn restores_only_a_different_good_config() {
        assert!(!should_restore("new", None));
        assert!(!should_restore("same", Some("same")));
        assert!(should_restore("new", Some("old")));
    }

    #[test]
    fn splits_domains_and_processes() {
        let raw = "youtube.com\nprocess:firefox\n  bank.ru  \n";
        assert_eq!(domain_lines(raw), vec!["youtube.com".to_string(), "bank.ru".to_string()]);
        assert_eq!(process_tokens(raw), vec!["firefox".to_string()]);
    }

    #[test]
    fn process_names_match_the_host() {
        let names = process_rule_names("chrome.exe\ntelegram");
        if cfg!(windows) {
            assert!(names.iter().any(|n| n.eq_ignore_ascii_case("chrome.exe")));
            assert!(names.iter().any(|n| n.eq_ignore_ascii_case("telegram.exe")));
        } else {
            assert!(names.iter().any(|n| n == "google-chrome"));
            assert!(names.iter().any(|n| n == "telegram-desktop"));
            assert!(!names.iter().any(|n| n.ends_with(".exe")));
        }
    }

    #[test]
    fn dns_txt_roundtrip_shape() {
        let query = encode_dns_query(0x1e57, "whoami.cloudflare");
        assert_eq!(&query[0..2], &0x1e57u16.to_be_bytes());
        assert!(query.windows(b"cloudflare".len()).any(|w| w == b"cloudflare"));
        let mut packet = query.clone();
        packet[2] = 0x81;
        packet[3] = 0x80;
        packet[6] = 0;
        packet[7] = 1;
        packet.extend_from_slice(&[0xC0, 12]);
        packet.extend_from_slice(&16u16.to_be_bytes());
        packet.extend_from_slice(&1u16.to_be_bytes());
        packet.extend_from_slice(&60u32.to_be_bytes());
        let txt = b"\"203.0.113.8\"";
        packet.extend_from_slice(&((txt.len() + 1) as u16).to_be_bytes());
        packet.push(txt.len() as u8);
        packet.extend_from_slice(txt);
        assert_eq!(parse_dns_txt(&packet).as_deref(), Some("203.0.113.8"));
        assert!(dns_matches("203.0.113.8", "203.0.113.8"));
        assert!(!dns_matches("203.0.113.8", "198.51.100.4"));
    }

    #[test]
    fn picks_a_github_asset_for_this_os() {
        let os = if cfg!(windows) { "windows" } else { "linux" };
        let assets = vec![
            serde_json::json!({"name": "Jester-linux-x64.tar.gz", "browser_download_url": "https://github.com/acme/releases/download/v1/Jester-linux-x64.tar.gz"}),
            serde_json::json!({"name": "Jester-windows-x64.zip", "browser_download_url": "https://github.com/acme/releases/download/v1/Jester-windows-x64.zip"}),
            serde_json::json!({"name": "notes.txt", "browser_download_url": "http://evil.example/x"}),
        ];
        let (name, _) = release_asset(&assets, os).unwrap();
        assert!(name.to_ascii_lowercase().contains(os));
        assert!(!trusted_download("http://github.com/acme/x"));
        assert!(!trusted_download("https://example.com/jester.zip"));
    }
}
