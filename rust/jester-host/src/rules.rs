use crate::models::AppSettings;
use crate::paths;
use serde::Serialize;

#[derive(Serialize)]
#[serde(rename_all = "camelCase")]
pub struct RuleStatus {
    pub domains_path: String,
    pub ips_path: String,
    pub format: String,
    pub domains_count: i64,
    pub ips_count: i64,
    pub domains_bytes: i64,
    pub ips_bytes: i64,
}

pub fn ensure(settings: &AppSettings) -> Result<(), String> {
    paths::ensure();
    write_lines("custom-proxy.txt", &settings.custom_proxy_rules)?;
    write_lines("custom-direct.txt", &settings.custom_direct_rules)?;
    if !settings.refilter_enabled { return Ok(()); }
    if settings.use_mrs_rules {
        fetch_if_missing(&settings.refilter_domains_mrs_url, "refilter-domains.mrs")?;
        if settings.refilter_use_ip { fetch_if_missing(&settings.refilter_ip_mrs_url, "refilter-ipsum.mrs")?; }
    } else {
        fetch_if_missing(&settings.refilter_domains_url, "refilter-domains.txt")?;
        if settings.refilter_use_ip { fetch_if_missing(&settings.refilter_ip_url, "refilter-ipsum.txt")?; }
    }
    Ok(())
}

pub fn update(settings: &AppSettings) -> Result<RuleStatus, String> {
    paths::ensure();
    if settings.use_mrs_rules {
        fetch(&settings.refilter_domains_mrs_url, "refilter-domains.mrs")?;
        if settings.refilter_use_ip { fetch(&settings.refilter_ip_mrs_url, "refilter-ipsum.mrs")?; }
    } else {
        fetch(&settings.refilter_domains_url, "refilter-domains.txt")?;
        if settings.refilter_use_ip { fetch(&settings.refilter_ip_url, "refilter-ipsum.txt")?; }
    }
    write_lines("custom-proxy.txt", &settings.custom_proxy_rules)?;
    write_lines("custom-direct.txt", &settings.custom_direct_rules)?;
    Ok(status(settings))
}

pub fn status(settings: &AppSettings) -> RuleStatus {
    let mrs = settings.use_mrs_rules;
    let domains = paths::rules_dir().join(if mrs { "refilter-domains.mrs" } else { "refilter-domains.txt" });
    let ips = paths::rules_dir().join(if mrs { "refilter-ipsum.mrs" } else { "refilter-ipsum.txt" });
    RuleStatus {
        format: if mrs { "mrs" } else { "text" }.into(),
        domains_count: count(&domains, mrs),
        ips_count: count(&ips, mrs),
        domains_bytes: len(&domains),
        ips_bytes: len(&ips),
        domains_path: domains.display().to_string(),
        ips_path: ips.display().to_string(),
    }
}

fn count(path: &std::path::Path, mrs: bool) -> i64 {
    if mrs { return len(path) / 32; }
    std::fs::read_to_string(path).map(|t| t.lines().filter(|l| !l.trim().is_empty()).count() as i64).unwrap_or(0)
}

fn len(path: &std::path::Path) -> i64 {
    std::fs::metadata(path).map(|m| m.len() as i64).unwrap_or(0)
}

fn write_lines(name: &str, content: &str) -> Result<(), String> {
    let text = content.lines().map(str::trim).filter(|l| !l.is_empty()).collect::<Vec<_>>().join("\n");
    let path = paths::rules_dir().join(name);
    std::fs::write(path, text).map_err(|e| e.to_string())
}

fn fetch_if_missing(url: &str, name: &str) -> Result<(), String> {
    let path = paths::rules_dir().join(name);
    if path.exists() && len(&path) > 0 { return Ok(()); }
    fetch(url, name)
}

fn fetch(url: &str, name: &str) -> Result<(), String> {
    let client = reqwest::blocking::Client::builder().timeout(std::time::Duration::from_secs(90)).build().map_err(|e| e.to_string())?;
    let bytes = client.get(url).send().map_err(|e| e.to_string())?.error_for_status().map_err(|e| e.to_string())?.bytes().map_err(|e| e.to_string())?;
    std::fs::write(paths::rules_dir().join(name), bytes).map_err(|e| e.to_string())
}
