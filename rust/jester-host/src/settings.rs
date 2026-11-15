use crate::models::{AppearanceSettings, AppSettings, DnsSettings, APP_PORT};
use crate::paths;
use std::sync::Mutex;

pub struct Store {
    inner: Mutex<AppSettings>,
}

impl Store {
    pub fn load() -> Self {
        paths::ensure();
        let current = std::fs::read_to_string(paths::settings_file())
            .ok()
            .and_then(|text| serde_json::from_str::<AppSettings>(&text).ok())
            .map(|mut s| {
                upgrade_legacy(&mut s.appearance);
                let _ = normalize(&mut s);
                s
            })
            .unwrap_or_default();
        let store = Self { inner: Mutex::new(current.clone()) };
        if !paths::settings_file().exists() {
            let _ = store.save(current);
        }
        store
    }

    pub fn current(&self) -> AppSettings {
        self.inner.lock().expect("settings").clone()
    }

    pub fn save(&self, mut next: AppSettings) -> Result<AppSettings, String> {
        let mut cur = self.inner.lock().expect("settings");
        next.dead_man_armed = cur.dead_man_armed;
        next.smart_direct_hits = cur.smart_direct_hits.clone();
        next.smart_direct_domains = cur.smart_direct_domains.clone();
        next.dns_pool_index = cur.dns_pool_index;
        next.last_dns_rotate_at = cur.last_dns_rotate_at.clone();
        next.last_known_exit_ip = cur.last_known_exit_ip.clone();
        next.last_known_exit_org = cur.last_known_exit_org.clone();
        next.adaptive_lane_state = cur.adaptive_lane_state.clone();
        next.last_measured_rtt_ms = cur.last_measured_rtt_ms;
        next.guardian_message = cur.guardian_message.clone();
        if next.last_subscription_update_at.is_none() {
            next.last_subscription_update_at = cur.last_subscription_update_at.clone();
        }
        normalize(&mut next)?;
        if next.auto_start != cur.auto_start {
            apply_autostart(next.auto_start);
        }
        write(&next)?;
        *cur = next.clone();
        Ok(next)
    }

    pub fn patch<F: FnOnce(&mut AppSettings)>(&self, mutate: F) -> Result<AppSettings, String> {
        let mut cur = self.inner.lock().expect("settings");
        let mut next = cur.clone();
        mutate(&mut next);
        normalize(&mut next)?;
        if next.auto_start != cur.auto_start {
            apply_autostart(next.auto_start);
        }
        write(&next)?;
        *cur = next.clone();
        Ok(next)
    }
}

fn write(settings: &AppSettings) -> Result<(), String> {
    paths::ensure();
    let text = serde_json::to_string_pretty(settings).map_err(|e| e.to_string())?;
    let path = paths::settings_file();
    let tmp = path.with_extension("json.tmp");
    std::fs::write(&tmp, text).map_err(|e| e.to_string())?;
    std::fs::rename(&tmp, &path).map_err(|e| e.to_string())
}

pub fn normalize(s: &mut AppSettings) -> Result<(), String> {
    let ports = [s.mixed_port, s.socks_port, s.external_port];
    if ports.iter().any(|p| *p < 1 || *p > 65535 || *p == APP_PORT as i32)
        || ports[0] == ports[1]
        || ports[0] == ports[2]
        || ports[1] == ports[2]
    {
        return Err("Порты должны различаться, быть в диапазоне 1–65535 и не занимать порт приложения 37841.".into());
    }
    s.mtu = s.mtu.clamp(1280, 9000);
    s.latency_timeout_ms = s.latency_timeout_ms.clamp(1000, 30000);
    s.latency_budget_ms = s.latency_budget_ms.clamp(80, 2000);
    if !matches!(s.exit_check_action.as_str(), "switch" | "warn" | "panic") { s.exit_check_action = "switch".into(); }
    if !matches!(s.latency_budget_action.as_str(), "switch" | "warn") { s.latency_budget_action = "switch".into(); }
    s.auto_update_interval_hours = s.auto_update_interval_hours.clamp(1, 168);
    s.dns_rotate_hours = s.dns_rotate_hours.clamp(1, 168);
    s.adaptive_lane_interval_sec = s.adaptive_lane_interval_sec.clamp(15, 3600);
    s.exit_check_interval_sec = s.exit_check_interval_sec.clamp(30, 3600);
    let defaults = AppearanceSettings::default();
    let a = &mut s.appearance;
    a.radius = a.radius.clamp(0, 28);
    a.glass_blur = a.glass_blur.clamp(0, 6);
    if !a.glass_tint_opacity.is_finite() { a.glass_tint_opacity = 0.1; }
    a.glass_tint_opacity = a.glass_tint_opacity.clamp(0.06, 0.28);
    a.background = color_or(&a.background, &defaults.background);
    a.text = color_or(&a.text, &defaults.text);
    a.accent = color_or(&a.accent, &defaults.accent);
    a.accent_muted = color_or(&a.accent_muted, &defaults.accent_muted);
    a.surface = color_or(&a.surface, &defaults.surface);
    if a.font_ui.trim().is_empty() { a.font_ui = defaults.font_ui.clone(); }
    if a.font_display.trim().is_empty() { a.font_display = defaults.font_display.clone(); }
    if !a.window_opacity.is_finite() { a.window_opacity = 1.0; }
    a.window_opacity = a.window_opacity.clamp(0.92, 1.0);
    if !matches!(s.mode.as_str(), "rule" | "global" | "direct") { s.mode = "rule".into(); }
    if !matches!(s.log_level.as_str(), "silent" | "error" | "warning" | "info" | "debug") {
        s.log_level = "warning".into();
    }
    if !matches!(s.tun_stack.as_str(), "system" | "gvisor" | "mixed") { s.tun_stack = "mixed".into(); }
    if !matches!(s.dns.enhanced_mode.as_str(), "fake-ip" | "redir-host") {
        s.dns.enhanced_mode = "fake-ip".into();
    }
    if s.dns.fake_ip_range.is_empty() { s.dns.fake_ip_range = DnsSettings::default().fake_ip_range; }
    if s.inet4_address.is_empty() { s.inet4_address = "172.19.0.1/30".into(); }
    if s.core_channel.is_empty() { s.core_channel = "v3".into(); }
    if s.latency_url.trim().is_empty() { s.latency_url = "https://www.gstatic.com/generate_204".into(); }
    Ok(())
}

fn color_or(value: &str, fallback: &str) -> String {
    let ok = value.len() == 7
        && value.as_bytes()[0] == b'#'
        && value.as_bytes()[1..].iter().all(|c| c.is_ascii_hexdigit());
    if ok { value.to_string() } else { fallback.to_string() }
}

fn upgrade_legacy(a: &mut AppearanceSettings) {
    if a.theme != "dark"
        || !a.background.eq_ignore_ascii_case("#090a0e")
        || !a.accent.eq_ignore_ascii_case("#e6c48a")
        || !a.accent_muted.eq_ignore_ascii_case("#a89068")
        || !a.surface.eq_ignore_ascii_case("#12131a")
        || !a.text.eq_ignore_ascii_case("#f7f3ec")
        || !a.custom_css.trim().is_empty()
        || a.background_image.as_deref().map(|s| !s.trim().is_empty()).unwrap_or(false)
    {
        return;
    }
    let liquid = AppearanceSettings::default();
    a.theme = liquid.theme;
    a.accent = liquid.accent;
    a.accent_muted = liquid.accent_muted;
    a.background = liquid.background;
    a.surface = liquid.surface;
    a.text = liquid.text;
    a.glass = liquid.glass;
    a.glass_blur = liquid.glass_blur;
    if a.radius == 16 { a.radius = liquid.radius; }
}

fn apply_autostart(enable: bool) {
    #[cfg(windows)]
    apply_autostart_windows(enable);
    #[cfg(not(windows))]
    apply_autostart_linux(enable);
}

#[cfg(windows)]
fn apply_autostart_windows(enable: bool) {
    let hkcu = winreg::RegKey::predef(winreg::enums::HKEY_CURRENT_USER);
    let Ok(key) = hkcu.open_subkey_with_flags(
        r"Software\Microsoft\Windows\CurrentVersion\Run",
        winreg::enums::KEY_SET_VALUE,
    ) else { return };
    const NAME: &str = "JesterProxy";
    if enable {
        if let Ok(path) = std::env::current_exe() {
            let _ = key.set_value(NAME, &format!("\"{}\"", path.display()));
        }
    } else {
        let _ = key.delete_value(NAME);
    }
}

pub fn register_launcher() {
    #[cfg(not(windows))]
    register_launcher_linux();
}

#[cfg(not(windows))]
fn register_launcher_linux() {
    let Ok(exe) = std::env::current_exe() else { return };
    let Some(home) = std::env::var_os("HOME") else { return };
    let home = std::path::PathBuf::from(home);
    let apps = home.join(".local").join("share").join("applications");
    let icon_dir = home.join(".local").join("share").join("icons").join("hicolor").join("256x256").join("apps");
    let _ = std::fs::create_dir_all(&apps);
    let _ = std::fs::create_dir_all(&icon_dir);
    let icon_file = icon_dir.join("jester.png");
    if let Some(src) = launcher_icon() {
        let _ = std::fs::copy(src, &icon_file);
    }
    let icon = if icon_file.exists() { icon_file.display().to_string() } else { "jester".into() };
    let body = format!(
        "[Desktop Entry]\nType=Application\nVersion=1.1\nName=Jester\nGenericName=Прокси\nComment=Защищённое подключение\nExec=\"{}\"\nIcon={icon}\nTerminal=false\nCategories=Network;\nStartupNotify=true\nStartupWMClass=jester\n",
        exe.display()
    );
    let _ = std::fs::write(apps.join("jester.desktop"), body);
    if crate::paths::program_exists("update-desktop-database") {
        let _ = std::process::Command::new("update-desktop-database").arg(&apps).status();
    }
}

#[cfg(not(windows))]
fn launcher_icon() -> Option<std::path::PathBuf> {
    let candidates = [
        crate::paths::ui_dir().join("jester-head.png"),
        std::path::PathBuf::from(env!("CARGO_MANIFEST_DIR")).join("../../ui/public/jester-head.png"),
        std::path::PathBuf::from(env!("CARGO_MANIFEST_DIR")).join("../../ui/dist/jester-head.png"),
    ];
    candidates.into_iter().find(|path| path.is_file())
}

#[cfg(not(windows))]
fn apply_autostart_linux(enable: bool) {
    let Ok(exe) = std::env::current_exe() else { return };
    write_desktop_entry(enable, &exe);
    write_hypr_autostart(enable, &exe);
    write_user_service(enable, &exe);
}

#[cfg(not(windows))]
fn write_desktop_entry(enable: bool, exe: &std::path::Path) {
    let Some(home) = std::env::var_os("HOME") else { return };
    let dir = std::path::PathBuf::from(home).join(".config").join("autostart");
    let path = dir.join("jester.desktop");
    if !enable {
        let _ = std::fs::remove_file(path);
        return;
    }
    let _ = std::fs::create_dir_all(&dir);
    let body = format!(
        "[Desktop Entry]\nType=Application\nName=Jester\nComment=Jester proxy client\nExec=\"{}\"\nTerminal=false\nCategories=Network;\nStartupNotify=false\nX-GNOME-Autostart-enabled=true\n",
        exe.display()
    );
    let _ = std::fs::write(path, body);
}

#[cfg(not(windows))]
fn write_user_service(enable: bool, exe: &std::path::Path) {
    let Some(home) = std::env::var_os("HOME") else { return };
    let dir = std::path::PathBuf::from(home).join(".config").join("systemd").join("user");
    let path = dir.join("jester.service");
    if !enable {
        if crate::paths::program_exists("systemctl") {
            let _ = std::process::Command::new("systemctl").args(["--user", "disable", "jester.service"]).status();
        }
        let _ = std::fs::remove_file(&path);
        if crate::paths::program_exists("systemctl") {
            let _ = std::process::Command::new("systemctl").args(["--user", "daemon-reload"]).status();
        }
        return;
    }
    let _ = std::fs::create_dir_all(&dir);
    let body = format!(
        "[Unit]\nDescription=Jester\nAfter=graphical-session.target\n\n[Service]\nType=simple\nExecStart=\"{}\"\nRestart=on-failure\nRestartSec=3\n\n[Install]\nWantedBy=graphical-session.target\n",
        exe.display()
    );
    let _ = std::fs::write(&path, body);
    if crate::paths::program_exists("systemctl") {
        let _ = std::process::Command::new("systemctl").args(["--user", "daemon-reload"]).status();
        let _ = std::process::Command::new("systemctl").args(["--user", "enable", "jester.service"]).status();
    }
}

#[cfg(not(windows))]
fn write_hypr_autostart(enable: bool, exe: &std::path::Path) {
    let Some(home) = std::env::var_os("HOME") else { return };
    let path = std::path::PathBuf::from(home).join(".config").join("hypr").join("modules").join("autostart.lua");
    let Ok(text) = std::fs::read_to_string(&path) else { return };
    if !text.contains("hl.exec_cmd") { return; }
    let cleaned = strip_hypr_block(&text);
    let next = if enable {
        let block = format!("    -- jester-autostart: managed by Jester, do not edit\n    hl.exec_cmd([[{}]])\n", exe.display());
        insert_before_last_end(&cleaned, &block)
    } else {
        cleaned
    };
    if next != text { let _ = std::fs::write(path, next); }
}

#[cfg(not(windows))]
fn strip_hypr_block(text: &str) -> String {
    let mut out = String::new();
    let mut skip_exec = false;
    for line in text.lines() {
        if line.contains("jester-autostart:") {
            skip_exec = true;
            continue;
        }
        if skip_exec {
            skip_exec = false;
            if line.contains("hl.exec_cmd") { continue; }
        }
        out.push_str(line);
        out.push('\n');
    }
    out
}

#[cfg(not(windows))]
fn insert_before_last_end(text: &str, block: &str) -> String {
    let Some(idx) = text.rfind("\nend)") else { return format!("{text}{block}"); };
    let mut next = String::with_capacity(text.len() + block.len());
    next.push_str(&text[..idx + 1]);
    next.push_str(block);
    next.push_str(&text[idx + 1..]);
    next
}
