use std::path::PathBuf;

pub fn root() -> PathBuf {
    if let Some(home) = std::env::var_os("JESTER_HOME").filter(|p| !p.is_empty()) {
        return PathBuf::from(home);
    }
    #[cfg(windows)]
    {
        return std::env::var("APPDATA")
            .map(PathBuf::from)
            .unwrap_or_else(|_| PathBuf::from("."))
            .join("Jester");
    }
    #[cfg(not(windows))]
    {
        std::env::var_os("XDG_DATA_HOME")
            .map(PathBuf::from)
            .filter(|p| !p.as_os_str().is_empty())
            .or_else(|| std::env::var_os("HOME").map(|h| PathBuf::from(h).join(".local").join("share")))
            .unwrap_or_else(|| PathBuf::from("."))
            .join("jester")
    }
}

pub fn cores() -> PathBuf { root().join("cores") }
pub fn run() -> PathBuf { root().join("run") }
pub fn profiles() -> PathBuf { root().join("profiles") }
pub fn logs() -> PathBuf { root().join("logs") }
pub fn settings_file() -> PathBuf { root().join("settings.json") }
pub fn mihomo_candidates() -> Vec<PathBuf> {
    #[cfg(windows)]
    {
        vec![cores().join("mihomo.exe")]
    }
    #[cfg(target_os = "android")]
    {
        if let Some(bundled) = std::env::var_os("JESTER_MIHOMO") {
            let path = PathBuf::from(bundled);
            if path.is_file() {
                return vec![path];
            }
        }
        return vec![cores().join("mihomo")];
    }
    #[cfg(all(not(windows), not(target_os = "android")))]
    {
        let mut out = Vec::new();
        let downloaded = cores().join("mihomo");
        if downloaded.is_file() {
            out.push(downloaded);
        }
        if let Some(bundled) = std::env::var_os("JESTER_MIHOMO") {
            let path = PathBuf::from(bundled);
            if path.is_file() && !out.iter().any(|have| have == &path) {
                out.push(path);
            }
        }
        if out.is_empty() {
            out.push(cores().join("mihomo"));
        }
        out
    }
}

pub fn mihomo_exe() -> PathBuf {
    mihomo_candidates().into_iter().next().unwrap_or_else(|| cores().join(if cfg!(windows) { "mihomo.exe" } else { "mihomo" }))
}

pub fn core_present() -> bool {
    mihomo_candidates().iter().any(|path| path.is_file())
}
pub fn wintun_dll() -> PathBuf { cores().join("wintun.dll") }
pub fn runtime_config() -> PathBuf { run().join("config.yaml") }
pub fn core_log() -> PathBuf { logs().join("core.log") }
pub fn device_id_file() -> PathBuf { root().join("device-id") }
pub fn rules_dir() -> PathBuf { run().join("ruleset") }
pub fn sysproxy_state() -> PathBuf { root().join("sysproxy.on") }
pub fn instance_lock() -> PathBuf { root().join("instance.lock") }
pub fn updates() -> PathBuf { root().join("updates") }
pub fn good_config() -> PathBuf { run().join("config.good.yaml") }

pub fn ensure() {
    for dir in [root(), cores(), run(), profiles(), logs(), rules_dir(), updates()] {
        let _ = std::fs::create_dir_all(dir);
    }
}

pub fn ui_dir() -> PathBuf {
    if let Some(dir) = std::env::var_os("JESTER_UI") {
        let path = PathBuf::from(dir);
        if path.join("index.html").is_file() {
            return path;
        }
    }
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
    {
        unsafe { libc::geteuid() == 0 }
    }
}

pub fn can_elevate() -> bool {
    #[cfg(windows)]
    { true }
    #[cfg(not(windows))]
    { program_exists("pkexec") && program_exists("setpriv") }
}

pub fn program_exists(name: &str) -> bool {
    std::env::var_os("PATH").is_some_and(|path| {
        std::env::split_paths(&path).any(|dir| {
            let file = dir.join(name);
            file.is_file()
        })
    })
}
