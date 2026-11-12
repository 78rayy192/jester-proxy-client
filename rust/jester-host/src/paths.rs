use std::path::PathBuf;

pub fn root() -> PathBuf {
    std::env::var("APPDATA")
        .map(PathBuf::from)
        .unwrap_or_else(|_| PathBuf::from("."))
        .join("Jester")
}

pub fn cores() -> PathBuf { root().join("cores") }
pub fn run() -> PathBuf { root().join("run") }
pub fn profiles() -> PathBuf { root().join("profiles") }
pub fn logs() -> PathBuf { root().join("logs") }
pub fn settings_file() -> PathBuf { root().join("settings.json") }
pub fn mihomo_exe() -> PathBuf { cores().join("mihomo.exe") }
pub fn wintun_dll() -> PathBuf { cores().join("wintun.dll") }
pub fn runtime_config() -> PathBuf { run().join("config.yaml") }
pub fn core_log() -> PathBuf { logs().join("core.log") }
pub fn device_id_file() -> PathBuf { root().join("device-id") }
pub fn rules_dir() -> PathBuf { run().join("ruleset") }

pub fn ensure() {
    for dir in [root(), cores(), run(), profiles(), logs(), rules_dir()] {
        let _ = std::fs::create_dir_all(dir);
    }
}

pub fn ui_dir() -> PathBuf {
    if let Ok(exe) = std::env::current_exe() {
        if let Some(dir) = exe.parent() {
            let beside = dir.join("ui");
            if beside.join("index.html").exists() {
                return beside;
            }
        }
    }
    PathBuf::from(env!("CARGO_MANIFEST_DIR")).join("../../ui/dist")
}

pub fn is_admin() -> bool {
    #[cfg(windows)]
    {
        unsafe { windows::Win32::UI::Shell::IsUserAnAdmin().as_bool() }
    }
    #[cfg(not(windows))]
    { false }
}
