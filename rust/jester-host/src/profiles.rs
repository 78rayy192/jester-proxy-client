use crate::models::ProfileInfo;
use crate::paths;
use std::collections::HashMap;

pub struct DownloadMeta {
    pub body: String,
    pub upload: i64,
    pub download: i64,
    pub total: i64,
    pub expire_unix: i64,
    pub hwid_limited: bool,
    pub update_interval_hours: i32,
}

pub fn list() -> Vec<ProfileInfo> {
    paths::ensure();
    let mut out = Vec::new();
    let Ok(dirs) = std::fs::read_dir(paths::profiles()) else { return out };
    for entry in dirs.flatten() {
        let id = entry.file_name().to_string_lossy().to_string();
        if let Some(info) = get(&id) { out.push(info); }
    }
    out.sort_by(|a, b| b.updated_at.cmp(&a.updated_at));
    out
}

pub fn get(id: &str) -> Option<ProfileInfo> {
    if !valid_id(id) { return None; }
    let text = std::fs::read_to_string(meta(id)).ok()?;
    let mut info: ProfileInfo = serde_json::from_str(&text).ok()?;
    if info.id != id { return None; }
    if info.name.trim().is_empty() { info.name = "Профиль".into(); }
    if info.kind != "subscription" { info.kind = "paste".into(); }
    Some(info)
}

pub fn config_path(id: &str) -> Result<std::path::PathBuf, String> {
    if !valid_id(id) { return Err("Некорректный id профиля.".into()); }
    Ok(paths::profiles().join(id).join("config.yaml"))
}

pub fn valid_id(id: &str) -> bool {
    id.len() == 12 && id.bytes().all(|c| c.is_ascii_hexdigit())
}

pub fn create(name: &str, kind: &str, source: &str, payload: &str) -> Result<ProfileInfo, String> {
    let id = uuid_hex();
    let kind = if kind == "subscription" { "subscription" } else { "paste" };
    let mut info = ProfileInfo {
        id: id.clone(),
        name: if name.trim().is_empty() { "Профиль".into() } else { name.trim().to_string() },
        kind: kind.into(),
        source: source.trim().to_string(),
        updated_at: chrono::Utc::now().to_rfc3339(),
        ..ProfileInfo::default()
    };
    let body = if kind == "subscription" {
        let dl = download(&info.source)?;
        apply_user(&mut info, &dl);
        dl.body
    } else {
        payload.to_string()
    };
    if body.trim().is_empty() { return Err("Пустой профиль: нет YAML/ссылки.".into()); }
    let yaml = crate::normalize::to_clash_yaml(&body)?;
    write_profile(&mut info, &yaml)?;
    Ok(info)
}

pub fn update_subscription(id: &str) -> Result<ProfileInfo, String> {
    let mut info = get(id).ok_or("Профиль не найден")?;
    if info.kind != "subscription" || info.source.trim().is_empty() { return Err("Это не подписка.".into()); }
    let dl = download(&info.source)?;
    let yaml = crate::normalize::to_clash_yaml(&dl.body)?;
    apply_user(&mut info, &dl);
    info.updated_at = chrono::Utc::now().to_rfc3339();
    write_profile(&mut info, &yaml)?;
    Ok(info)
}

pub fn update_all() -> (i32, i32, Vec<String>) {
    let mut updated = 0;
    let mut failed = 0;
    let mut errors = Vec::new();
    for p in list().into_iter().filter(|p| p.kind == "subscription" && !p.source.trim().is_empty()) {
        match update_subscription(&p.id) {
            Ok(_) => updated += 1,
            Err(e) => { failed += 1; errors.push(format!("{}: {e}", p.name)); }
        }
    }
    (updated, failed, errors)
}

pub fn patch(id: &str, name: Option<&str>, fingerprint: Option<Option<String>>, nameservers: Option<Option<Vec<String>>>) -> Result<ProfileInfo, String> {
    let mut info = get(id).ok_or("Профиль не найден")?;
    if let Some(name) = name {
        if !name.trim().is_empty() { info.name = name.trim().to_string(); }
    }
    if let Some(fp) = fingerprint { info.fingerprint_override = fp.filter(|s| !s.trim().is_empty()); }
    if let Some(ns) = nameservers { info.nameserver_override = ns.filter(|v| !v.is_empty()); }
    info.id = id.to_string();
    let text = serde_json::to_string_pretty(&info).map_err(|e| e.to_string())?;
    atomic(&meta(id), &text)?;
    Ok(info)
}

pub fn delete(id: &str) -> Result<(), String> {
    if !valid_id(id) { return Err("Некорректный id профиля.".into()); }
    let dir = paths::profiles().join(id);
    if dir.exists() { std::fs::remove_dir_all(dir).map_err(|e| e.to_string())?; }
    Ok(())
}

fn write_profile(info: &mut ProfileInfo, yaml: &str) -> Result<(), String> {
    let dir = paths::profiles().join(&info.id);
    std::fs::create_dir_all(&dir).map_err(|e| e.to_string())?;
    let cfg = dir.join("config.yaml");
    atomic(&cfg, yaml)?;
    info.bytes = std::fs::metadata(&cfg).map(|m| m.len() as i64).unwrap_or(0);
    atomic(&meta(&info.id), &serde_json::to_string_pretty(info).map_err(|e| e.to_string())?)
}

fn apply_user(info: &mut ProfileInfo, dl: &DownloadMeta) {
    info.upload = dl.upload;
    info.download = dl.download;
    info.total = dl.total;
    info.expire_unix = dl.expire_unix;
    info.hwid_limited = dl.hwid_limited;
    info.update_interval_hours = dl.update_interval_hours;
}

pub fn device_id() -> String {
    paths::ensure();
    if let Ok(existing) = std::fs::read_to_string(paths::device_id_file()) {
        let existing = existing.trim();
        if existing.len() >= 10 && existing.len() <= 64 && existing.chars().all(|c| c.is_ascii_alphanumeric() || c == '=' || c == '-') {
            return existing.to_string();
        }
    }
    let id = uuid_hex() + &uuid_hex();
    let _ = std::fs::write(paths::device_id_file(), &id);
    id
}

fn download(url: &str) -> Result<DownloadMeta, String> {
    let url = unwrap(url);
    if !(url.starts_with("http://") || url.starts_with("https://")) {
        return Err("Укажите HTTP(S)-ссылку подписки.".into());
    }
    let client = reqwest::blocking::Client::builder().timeout(std::time::Duration::from_secs(45)).build().map_err(|e| e.to_string())?;
    let res = client.get(&url)
        .header("User-Agent", "Happ/3.13.0/Windows")
        .header("Accept", "*/*")
        .header("x-hwid", device_id())
        .header("x-device-os", "Windows")
        .header("x-ver-os", "10.0")
        .header("x-device-model", "Jester")
        .send()
        .map_err(|e| e.to_string())?;
    if !res.status().is_success() { return Err(format!("Подписка ответила {}", res.status())); }
    let headers: HashMap<String, String> = res.headers().iter().map(|(k, v)| (k.as_str().to_ascii_lowercase(), v.to_str().unwrap_or("").to_string())).collect();
    if headers.get("x-hwid-max-devices-reached").map(|s| s == "1" || s.eq_ignore_ascii_case("true")).unwrap_or(false) {
        return Err("Достигнут лимит устройств поставщика. Удалите неиспользуемое устройство в боте и обновите подписку.".into());
    }
    let mut meta = DownloadMeta { body: String::new(), upload: 0, download: 0, total: 0, expire_unix: 0, hwid_limited: false, update_interval_hours: 0 };
    meta.hwid_limited = headers.contains_key("x-hwid-limit") || headers.contains_key("x-hwid-not-supported");
    if let Some(info) = headers.get("subscription-userinfo") { parse_userinfo(info, &mut meta); }
    if let Some(hours) = headers.get("profile-update-interval").and_then(|s| s.parse::<i32>().ok()) {
        if hours > 0 { meta.update_interval_hours = hours; }
    }
    meta.body = res.text().map_err(|e| e.to_string())?;
    if meta.body.trim().is_empty() { return Err("Подписка вернула пустой ответ.".into()); }
    Ok(meta)
}

fn parse_userinfo(raw: &str, into: &mut DownloadMeta) {
    for part in raw.split(';') {
        let Some((k, v)) = part.split_once('=') else { continue };
        let n = v.trim().parse::<i64>().unwrap_or(0);
        match k.trim() {
            "upload" => into.upload = n,
            "download" => into.download = n,
            "total" => into.total = n,
            "expire" => into.expire_unix = n,
            _ => {}
        }
    }
}

fn unwrap(url: &str) -> String {
    let url = url.trim();
    if let Some(rest) = url.strip_prefix("happ://add/") { return urlencoding_simple(rest); }
    url.to_string()
}

fn urlencoding_simple(s: &str) -> String {
    let bytes = s.as_bytes();
    let mut out = Vec::new();
    let mut i = 0;
    while i < bytes.len() {
        if bytes[i] == b'%' && i + 2 < bytes.len() {
            if let Ok(v) = u8::from_str_radix(std::str::from_utf8(&bytes[i + 1..i + 3]).unwrap_or(""), 16) {
                out.push(v);
                i += 3;
                continue;
            }
        }
        out.push(bytes[i]);
        i += 1;
    }
    String::from_utf8(out).unwrap_or_else(|_| s.to_string())
}

fn uuid_hex() -> String {
    use rand::RngCore;
    let mut b = [0u8; 6];
    rand::rng().fill_bytes(&mut b);
    b.iter().map(|x| format!("{x:02x}")).collect()
}

fn meta(id: &str) -> std::path::PathBuf { paths::profiles().join(id).join("meta.json") }

fn atomic(path: &std::path::Path, text: &str) -> Result<(), String> {
    if let Some(parent) = path.parent() { std::fs::create_dir_all(parent).map_err(|e| e.to_string())?; }
    let tmp = path.with_extension("tmp");
    std::fs::write(&tmp, text).map_err(|e| e.to_string())?;
    std::fs::rename(&tmp, path).map_err(|e| e.to_string())
}
