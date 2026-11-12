use crate::models::{AppSettings, CoreStatus, APP_VERSION};
use crate::paths;
use crate::{config, persona, profiles, rules, sysproxy};
use std::io::{BufRead, BufReader};
use std::process::{Child, Command, Stdio};
use std::sync::atomic::{AtomicBool, AtomicU32, Ordering};
use std::sync::{Arc, Mutex};
use std::os::windows::process::CommandExt;

const CREATE_NO_WINDOW: u32 = 0x08000000;

pub struct Core {
    pub secret: String,
    child: Mutex<Option<Child>>,
    stopping: AtomicBool,
    restarts: AtomicU32,
    pub downloader: crate::download::Downloader,
    log: Mutex<Vec<String>>,
}

impl Core {
    pub fn new() -> Self {
        let mut bytes = [0u8; 16];
        rand::RngCore::fill_bytes(&mut rand::rng(), &mut bytes);
        Self {
            secret: bytes.iter().map(|b| format!("{b:02x}")).collect(),
            child: Mutex::new(None),
            stopping: AtomicBool::new(false),
            restarts: AtomicU32::new(0),
            downloader: crate::download::Downloader::new(),
            log: Mutex::new(Vec::new()),
        }
    }

    pub fn running(&self) -> bool {
        let mut child = self.child.lock().expect("child");
        match child.as_mut() {
            Some(proc) => proc.try_wait().ok().flatten().is_none(),
            None => false,
        }
    }

    pub fn pid(&self) -> u32 {
        self.child.lock().expect("child").as_ref().map(|p| p.id()).unwrap_or(0)
    }

    pub fn status(&self, settings: &AppSettings) -> CoreStatus {
        let running = self.running();
        CoreStatus {
            running,
            core_installed: paths::mihomo_exe().exists(),
            wintun_installed: paths::wintun_dll().exists(),
            is_admin: paths::is_admin(),
            needs_admin: settings.tun_enabled && !paths::is_admin(),
            pid: if running { self.pid() } else { 0 },
            version: read_version(),
            app_version: APP_VERSION.into(),
            message: if self.downloader.busy() { self.downloader.stage() } else if running { "online".into() } else { "idle".into() },
            download_progress: self.downloader.progress(),
            active_profile_id: settings.active_profile_id.clone(),
            mode: settings.mode.clone(),
        }
    }

    pub fn start(&self, settings: &AppSettings) -> Result<CoreStatus, String> {
        if !paths::mihomo_exe().exists() { return Err("Ядро не установлено.".into()); }
        if settings.active_profile_id.trim().is_empty() { return Err("Сначала выберите профиль.".into()); }
        if settings.tun_enabled && !paths::is_admin() {
            let mut status = self.status(settings);
            status.message = "admin-required".into();
            status.needs_admin = true;
            return Ok(status);
        }
        let profile = profiles::get(&settings.active_profile_id).ok_or("Активный профиль не найден")?;
        let yaml = std::fs::read_to_string(profiles::config_path(&profile.id)?).map_err(|e| e.to_string())?;
        let _ = rules::ensure(settings);
        let effective = persona::prepare_profile(settings, Some(&profile));
        let _ = rules::ensure(&effective);
        let merged = config::merge(&yaml, &effective, &self.secret)?;
        paths::ensure();
        std::fs::write(paths::runtime_config(), merged).map_err(|e| e.to_string())?;
        self.stop_inner();
        copy_wintun();
        let mut cmd = Command::new(paths::mihomo_exe());
        cmd.current_dir(paths::run())
            .arg("-d").arg(paths::run())
            .arg("-f").arg(paths::runtime_config())
            .stdin(Stdio::null())
            .stdout(Stdio::piped())
            .stderr(Stdio::piped())
            .env("DISABLE_LOOPBACK_DETECTOR", "1")
            .creation_flags(CREATE_NO_WINDOW);
        let mut child = cmd.spawn().map_err(|e| format!("Не удалось запустить ядро: {e}"))?;
        if settings.high_priority { raise_priority(&child); }
        if let Some(out) = child.stdout.take() { pipe_log(out, self); }
        if let Some(err) = child.stderr.take() { pipe_log(err, self); }
        *self.child.lock().expect("child") = Some(child);
        if let Err(e) = wait_ready(settings.external_port, &self.secret) {
            self.stop_inner();
            return Err(e);
        }
        if settings.system_proxy && !settings.tun_enabled {
            let _ = sysproxy::set(true, settings.mixed_port);
        } else if !settings.dead_man_armed {
            let _ = sysproxy::set(false, 0);
        }
        self.restarts.store(0, Ordering::Relaxed);
        self.append("core started");
        Ok(self.status(settings))
    }

    pub fn stop(&self) {
        self.stopping.store(true, Ordering::Relaxed);
        self.stop_inner();
        self.stopping.store(false, Ordering::Relaxed);
        let _ = sysproxy::set(false, 0);
        self.append("core stopped");
    }

    pub fn restart(&self, settings: &AppSettings) -> Result<CoreStatus, String> {
        self.stopping.store(true, Ordering::Relaxed);
        self.stop_inner();
        self.stopping.store(false, Ordering::Relaxed);
        std::thread::sleep(std::time::Duration::from_millis(250));
        self.start(settings)
    }

    fn stop_inner(&self) {
        if let Some(mut child) = self.child.lock().expect("child").take() {
            let _ = child.kill();
            let _ = child.wait();
        }
    }

    pub fn on_exit_unexpected(&self) -> bool {
        if self.stopping.load(Ordering::Relaxed) { return false; }
        self.append("core exited unexpectedly");
        true
    }

    pub fn note_restart(&self) -> u32 {
        self.restarts.fetch_add(1, Ordering::Relaxed) + 1
    }

    pub fn append(&self, line: &str) {
        let stamp = chrono::Local::now().format("%H:%M:%S");
        let mut log = self.log.lock().expect("log");
        log.push(format!("[{stamp}] {line}"));
        let extra = log.len().saturating_sub(400);
        if extra > 0 { log.drain(0..extra); }
        let _ = std::fs::OpenOptions::new().create(true).append(true).open(paths::core_log()).and_then(|mut f| {
            use std::io::Write;
            writeln!(f, "[{stamp}] {line}")
        });
    }

    pub fn log_tail(&self, lines: usize) -> String {
        if let Ok(text) = std::fs::read_to_string(paths::core_log()) {
            let all: Vec<&str> = text.lines().collect();
            let start = all.len().saturating_sub(lines);
            return all[start..].join("\n");
        }
        self.log.lock().expect("log").join("\n")
    }

    pub fn clash(&self, port: i32) -> reqwest::blocking::Client {
        let _ = port;
        reqwest::blocking::Client::builder().timeout(std::time::Duration::from_secs(20)).build().unwrap_or_else(|_| reqwest::blocking::Client::new())
    }

    pub fn select(&self, port: i32, group: &str, name: &str) {
        let client = self.clash(port);
        let url = format!("http://127.0.0.1:{port}/proxies/{}", urlencoding(group));
        let body = serde_json::json!({ "name": name }).to_string();
        let _ = client.put(url).header("Authorization", format!("Bearer {}", self.secret)).header("Content-Type", "application/json").body(body).send();
    }

    pub fn best_delay(&self, port: i32, group: &str, url: &str, timeout: i32) -> Option<(String, i32)> {
        let client = self.clash(port);
        let u = format!("http://127.0.0.1:{port}/group/{}/delay?url={}&timeout={timeout}", urlencoding(group), urlencoding(url));
        let res = client.get(u).header("Authorization", format!("Bearer {}", self.secret)).send().ok()?;
        if !res.status().is_success() { return None; }
        let doc: serde_json::Value = res.json().ok()?;
        let mut best: Option<(String, i32)> = None;
        if let Some(obj) = doc.as_object() {
            for (name, value) in obj {
                let Some(d) = value.as_i64() else { continue };
                if d <= 0 || d >= 10000 { continue; }
                if best.as_ref().map(|(_, cur)| d < *cur as i64).unwrap_or(true) {
                    best = Some((name.clone(), d as i32));
                }
            }
        }
        best
    }
}

fn pipe_log(pipe: impl std::io::Read + Send + 'static, core: &Core) {
    let _ = core;
    std::thread::spawn(move || {
        let reader = BufReader::new(pipe);
        for line in reader.lines().map_while(Result::ok) {
            let _ = std::fs::OpenOptions::new().create(true).append(true).open(paths::core_log()).and_then(|mut f| {
                use std::io::Write;
                writeln!(f, "{line}")
            });
        }
    });
}

fn wait_ready(port: i32, secret: &str) -> Result<(), String> {
    let client = reqwest::blocking::Client::builder().timeout(std::time::Duration::from_millis(800)).build().map_err(|e| e.to_string())?;
    for _ in 0..25 {
        if let Ok(res) = client.get(format!("http://127.0.0.1:{port}/version")).header("Authorization", format!("Bearer {secret}")).send() {
            if res.status().is_success() { return Ok(()); }
        }
        std::thread::sleep(std::time::Duration::from_millis(200));
    }
    Err("Ядро не ответило на локальный API.".into())
}

fn read_version() -> String {
    if !paths::mihomo_exe().exists() { return String::new(); }
    let output = Command::new(paths::mihomo_exe()).arg("-v").creation_flags(CREATE_NO_WINDOW).output();
    output.ok().map(|o| String::from_utf8_lossy(&o.stdout).lines().next().unwrap_or("").trim().to_string()).unwrap_or_default()
}

fn raise_priority(child: &Child) {
    use std::os::windows::io::AsRawHandle;
    use windows::Win32::Foundation::HANDLE;
    use windows::Win32::System::Threading::{SetPriorityClass, ABOVE_NORMAL_PRIORITY_CLASS};
    unsafe { let _ = SetPriorityClass(HANDLE(child.as_raw_handle()), ABOVE_NORMAL_PRIORITY_CLASS); }
}

fn copy_wintun() {
    let src = paths::wintun_dll();
    let dst = paths::run().join("wintun.dll");
    if src.exists() { let _ = std::fs::copy(src, dst); }
}

fn urlencoding(s: &str) -> String {
    let mut out = String::new();
    for b in s.bytes() {
        if b.is_ascii_alphanumeric() || matches!(b, b'-' | b'_' | b'.' | b'~') { out.push(b as char); }
        else { out.push_str(&format!("%{b:02X}")); }
    }
    out
}

pub fn watch_exit(core: Arc<Core>, settings: Arc<crate::settings::Store>) {
    std::thread::spawn(move || loop {
        std::thread::sleep(std::time::Duration::from_secs(2));
        let exited = {
            let mut child = core.child.lock().expect("child");
            if let Some(proc) = child.as_mut() {
                matches!(proc.try_wait(), Ok(Some(_)))
            } else { false }
        };
        if !exited { continue; }
        {
            let mut child = core.child.lock().expect("child");
            if let Some(mut proc) = child.take() { let _ = proc.wait(); }
        }
        if !core.on_exit_unexpected() { continue; }
        let current = settings.current();
        if current.dead_man_switch { let _ = sysproxy::set(true, 1); settings.patch(|s| s.dead_man_armed = true).ok(); }
        if !current.auto_restart_core { continue; }
        let n = core.note_restart();
        if n > 3 { core.append("auto-restart aborted after 3 attempts"); continue; }
        std::thread::sleep(std::time::Duration::from_millis(1500 * n as u64));
        match core.start(&settings.current()) {
            Ok(_) => { core.append("auto-restart succeeded"); let _ = settings.patch(|s| s.dead_man_armed = false); }
            Err(e) => core.append(&format!("auto-restart failed: {e}")),
        }
    });
}
