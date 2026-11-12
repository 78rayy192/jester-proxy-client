use crate::paths;
use std::io::{Read, Write};
use std::sync::atomic::{AtomicBool, AtomicI32, Ordering};

pub struct Downloader {
    pub progress: AtomicI32,
    pub stage: std::sync::Mutex<String>,
    busy: AtomicBool,
}

impl Downloader {
    pub fn new() -> Self {
        Self { progress: AtomicI32::new(0), stage: std::sync::Mutex::new("idle".into()), busy: AtomicBool::new(false) }
    }

    pub fn busy(&self) -> bool { self.busy.load(Ordering::Relaxed) }
    pub fn progress(&self) -> i32 { self.progress.load(Ordering::Relaxed) }
    pub fn stage(&self) -> String { self.stage.lock().expect("stage").clone() }

    pub fn install(&self, channel: &str) -> Result<(), String> {
        if self.busy.swap(true, Ordering::AcqRel) { return Err("Загрузка уже идёт.".into()); }
        self.progress.store(0, Ordering::Relaxed);
        let result = (|| {
            paths::ensure();
            self.set_stage("release");
            let url = resolve(channel)?;
            let zip_path = paths::cores().join("mihomo.zip");
            self.set_stage("core");
            download_file(&url, &zip_path, &self.progress)?;
            extract_exe(&zip_path, &paths::mihomo_exe())?;
            let _ = std::fs::remove_file(&zip_path);
            if !paths::wintun_dll().exists() {
                self.set_stage("wintun");
                install_wintun()?;
            }
            self.progress.store(100, Ordering::Relaxed);
            self.set_stage("ready");
            Ok(())
        })();
        if result.is_err() { self.set_stage("error"); }
        self.busy.store(false, Ordering::Release);
        result
    }

    fn set_stage(&self, stage: &str) { *self.stage.lock().expect("stage") = stage.into(); }
}

fn resolve(channel: &str) -> Result<String, String> {
    let client = reqwest::blocking::Client::builder().user_agent("JesterProxyClient/0.2").timeout(std::time::Duration::from_secs(40)).build().map_err(|e| e.to_string())?;
    let doc: serde_json::Value = client.get("https://api.github.com/repos/MetaCubeX/mihomo/releases/latest").send().map_err(|e| e.to_string())?.error_for_status().map_err(|e| e.to_string())?.json().map_err(|e| e.to_string())?;
    let arch = "amd64";
    let mut best = None;
    let mut fallback = None;
    for asset in doc.get("assets").and_then(|a| a.as_array()).ok_or("Нет ассетов релиза")? {
        let name = asset.get("name").and_then(|v| v.as_str()).unwrap_or("");
        let url = asset.get("browser_download_url").and_then(|v| v.as_str()).unwrap_or("");
        if !name.to_ascii_lowercase().ends_with(".zip") || name.to_ascii_lowercase().contains("-go12") { continue; }
        if !name.to_ascii_lowercase().starts_with(&format!("mihomo-windows-{arch}")) { continue; }
        if matches!(channel, "v3" | "amd64-v3") && name.to_ascii_lowercase().contains(&format!("windows-{arch}-v3-")) { return Ok(url.into()); }
        if channel == "compatible" && name.to_ascii_lowercase().contains(&format!("windows-{arch}-compatible-")) { return Ok(url.into()); }
        if baseline(name, arch) { best = Some(url.to_string()); fallback = Some(url.to_string()); }
        if name.to_ascii_lowercase().contains(&format!("windows-{arch}-compatible-")) && fallback.is_none() { fallback = Some(url.to_string()); }
    }
    best.or(fallback).ok_or_else(|| "Не найден пакет mihomo для этой архитектуры.".into())
}

fn baseline(name: &str, arch: &str) -> bool {
    let prefix = format!("mihomo-windows-{arch}-v");
    let lower = name.to_ascii_lowercase();
    let Some(rest) = lower.strip_prefix(&prefix) else { return false };
    rest.chars().next().map(|c| c.is_ascii_digit()).unwrap_or(false) && rest.contains('.')
}

fn install_wintun() -> Result<(), String> {
    let zip_path = paths::cores().join("wintun.zip");
    download_file("https://www.wintun.net/builds/wintun-0.14.1.zip", &zip_path, &std::sync::atomic::AtomicI32::new(0))?;
    let file = std::fs::File::open(&zip_path).map_err(|e| e.to_string())?;
    let mut zip = zip::ZipArchive::new(file).map_err(|e| e.to_string())?;
    let needle = "bin/amd64/wintun.dll";
    let mut found = None;
    for i in 0..zip.len() {
        let name = zip.by_index(i).map(|e| e.name().replace('\\', "/")).unwrap_or_default();
        if name.to_ascii_lowercase().ends_with(needle) { found = Some(i); break; }
    }
    let idx = found.ok_or("wintun.dll не найден в архиве.")?;
    let mut entry = zip.by_index(idx).map_err(|e| e.to_string())?;
    let mut bytes = Vec::new();
    entry.read_to_end(&mut bytes).map_err(|e| e.to_string())?;
    std::fs::write(paths::wintun_dll(), bytes).map_err(|e| e.to_string())?;
    let _ = std::fs::remove_file(zip_path);
    Ok(())
}

fn download_file(url: &str, dest: &std::path::Path, progress: &std::sync::atomic::AtomicI32) -> Result<(), String> {
    let client = reqwest::blocking::Client::builder().timeout(std::time::Duration::from_secs(300)).build().map_err(|e| e.to_string())?;
    let mut res = client.get(url).send().map_err(|e| e.to_string())?.error_for_status().map_err(|e| e.to_string())?;
    let total = res.content_length().unwrap_or(0);
    let tmp = dest.with_extension("download");
    let mut file = std::fs::File::create(&tmp).map_err(|e| e.to_string())?;
    let mut buf = [0u8; 81920];
    let mut read_n = 0u64;
    loop {
        let n = res.read(&mut buf).map_err(|e| e.to_string())?;
        if n == 0 { break; }
        file.write_all(&buf[..n]).map_err(|e| e.to_string())?;
        read_n += n as u64;
        if total > 0 { progress.store(((read_n * 90) / total) as i32, std::sync::atomic::Ordering::Relaxed); }
    }
    drop(file);
    if read_n == 0 || (total > 0 && read_n != total) { return Err("Архив ядра загружен не полностью.".into()); }
    std::fs::rename(&tmp, dest).map_err(|e| e.to_string())
}

fn extract_exe(zip_path: &std::path::Path, dest: &std::path::Path) -> Result<(), String> {
    let file = std::fs::File::open(zip_path).map_err(|e| e.to_string())?;
    let mut zip = zip::ZipArchive::new(file).map_err(|e| e.to_string())?;
    let mut idx = None;
    for i in 0..zip.len() {
        let name = zip.by_index(i).map(|e| e.name().to_string()).unwrap_or_default();
        if name.to_ascii_lowercase().ends_with(".exe") { idx = Some(i); break; }
    }
    let idx = idx.ok_or("В архиве нет exe ядра.")?;
    let mut entry = zip.by_index(idx).map_err(|e| e.to_string())?;
    let mut bytes = Vec::new();
    entry.read_to_end(&mut bytes).map_err(|e| e.to_string())?;
    if bytes.len() < 64 || bytes[0] != b'M' || bytes[1] != b'Z' { return Err("В архиве нет корректного исполняемого файла Windows.".into()); }
    std::fs::write(dest, bytes).map_err(|e| e.to_string())
}
