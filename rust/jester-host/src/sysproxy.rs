#[cfg(not(windows))]
use crate::paths;

pub fn set(enable: bool, port: i32) -> Result<(), String> {
    #[cfg(target_os = "android")]
    {
        let _ = (enable, port);
        return Ok(());
    }
    #[cfg(not(target_os = "android"))]
    {
        if enable && !(1..=65535).contains(&port) { return Err("Некорректный порт прокси.".into()); }
        #[cfg(windows)]
        { return set_windows(enable, port); }
        #[cfg(not(windows))]
        { return set_linux(enable, port); }
    }
}

pub fn is_enabled() -> bool {
    #[cfg(target_os = "android")]
    { return false; }
    #[cfg(windows)]
    { windows_enabled() }
    #[cfg(all(not(windows), not(target_os = "android")))]
    { paths::sysproxy_state().exists() }
}

#[cfg(windows)]
fn set_windows(enable: bool, port: i32) -> Result<(), String> {
    use winreg::enums::*;
    use winreg::RegKey;
    const KEY: &str = r"Software\Microsoft\Windows\CurrentVersion\Internet Settings";
    let hkcu = RegKey::predef(HKEY_CURRENT_USER);
    let key = hkcu.open_subkey_with_flags(KEY, KEY_SET_VALUE).map_err(|e| e.to_string())?;
    if enable {
        let server = format!("127.0.0.1:{port}");
        let bypass = "localhost;127.*;10.*;192.168.*;172.16.*;172.17.*;172.18.*;172.19.*;<local>";
        key.set_value("ProxyEnable", &1u32).map_err(|e| e.to_string())?;
        key.set_value("ProxyServer", &server).map_err(|e| e.to_string())?;
        key.set_value("ProxyOverride", &bypass).map_err(|e| e.to_string())?;
        let _ = key.delete_value("AutoConfigURL");
    } else {
        key.set_value("ProxyEnable", &0u32).map_err(|e| e.to_string())?;
    }
    notify();
    Ok(())
}

#[cfg(windows)]
fn windows_enabled() -> bool {
    use winreg::enums::*;
    use winreg::RegKey;
    const KEY: &str = r"Software\Microsoft\Windows\CurrentVersion\Internet Settings";
    let hkcu = RegKey::predef(HKEY_CURRENT_USER);
    let Ok(key) = hkcu.open_subkey(KEY) else { return false };
    key.get_value::<u32, _>("ProxyEnable").unwrap_or(0) == 1
}

#[cfg(windows)]
fn notify() {
    unsafe {
        use windows::Win32::Networking::WinInet::{InternetSetOptionW, INTERNET_OPTION_REFRESH, INTERNET_OPTION_SETTINGS_CHANGED};
        let _ = InternetSetOptionW(None, INTERNET_OPTION_SETTINGS_CHANGED, None, 0);
        let _ = InternetSetOptionW(None, INTERNET_OPTION_REFRESH, None, 0);
    }
}

#[cfg(not(windows))]
fn set_linux(enable: bool, port: i32) -> Result<(), String> {
    if enable {
        gnome_proxy(true, port);
        desktop_schema_proxy(true, port);
        kde_proxy(true, port);
        xfce_proxy(true, port);
        write_environment(port);
        session_environment(true, port);
        std::fs::write(paths::sysproxy_state(), port.to_string()).map_err(|e| e.to_string())?;
    } else {
        gnome_proxy(false, 0);
        desktop_schema_proxy(false, 0);
        kde_proxy(false, 0);
        xfce_proxy(false, 0);
        clear_environment();
        session_environment(false, 0);
        let _ = std::fs::remove_file(paths::sysproxy_state());
    }
    Ok(())
}

#[cfg(not(windows))]
fn gnome_proxy(enable: bool, port: i32) {
    if !paths::program_exists("gsettings") { return; }
    let probe = std::process::Command::new("gsettings")
        .args(["get", "org.gnome.system.proxy", "mode"])
        .output();
    if !probe.map(|o| o.status.success()).unwrap_or(false) { return; }
    if !enable {
        let _ = std::process::Command::new("gsettings").args(["set", "org.gnome.system.proxy", "mode", "none"]).status();
        return;
    }
    let port_s = port.to_string();
    let sets = [
        ("org.gnome.system.proxy", "mode", "manual"),
        ("org.gnome.system.proxy.http", "host", "127.0.0.1"),
        ("org.gnome.system.proxy.https", "host", "127.0.0.1"),
        ("org.gnome.system.proxy.socks", "host", "127.0.0.1"),
    ];
    for (schema, key, value) in sets {
        let _ = std::process::Command::new("gsettings").args(["set", schema, key, value]).status();
    }
    for schema in ["org.gnome.system.proxy.http", "org.gnome.system.proxy.https", "org.gnome.system.proxy.socks"] {
        let _ = std::process::Command::new("gsettings").args(["set", schema, "port", &port_s]).status();
    }
    let _ = std::process::Command::new("gsettings").args(["set", "org.gnome.system.proxy", "ignore-hosts", "['localhost', '127.0.0.0/8', '::1', '10.0.0.0/8', '192.168.0.0/16', '172.16.0.0/12']"]).status();
}

#[cfg(not(windows))]
fn desktop_schema_proxy(enable: bool, port: i32) {
    for schema in ["org.cinnamon.desktop.proxy", "org.mate.system.proxy"] {
        apply_gsettings_schema(schema, enable, port);
    }
}

#[cfg(not(windows))]
fn apply_gsettings_schema(schema: &str, enable: bool, port: i32) {
    if !paths::program_exists("gsettings") { return; }
    let probe = std::process::Command::new("gsettings").args(["get", schema, "mode"]).output();
    if !probe.map(|o| o.status.success()).unwrap_or(false) { return; }
    if !enable {
        let _ = std::process::Command::new("gsettings").args(["set", schema, "mode", "none"]).status();
        return;
    }
    let port_s = port.to_string();
    let _ = std::process::Command::new("gsettings").args(["set", schema, "mode", "manual"]).status();
    for sub in ["http", "https", "socks"] {
        let child = format!("{schema}.{sub}");
        let _ = std::process::Command::new("gsettings").args(["set", &child, "host", "127.0.0.1"]).status();
        let _ = std::process::Command::new("gsettings").args(["set", &child, "port", &port_s]).status();
    }
}

#[cfg(not(windows))]
fn kde_proxy(enable: bool, port: i32) {
    let tool = if paths::program_exists("kwriteconfig6") {
        "kwriteconfig6"
    } else if paths::program_exists("kwriteconfig5") {
        "kwriteconfig5"
    } else {
        return;
    };
    let write = |key: &str, value: &str| {
        let _ = std::process::Command::new(tool)
            .args(["--file", "kioslaverc", "--group", "Proxy Settings", "--key", key, value])
            .stdin(std::process::Stdio::null())
            .stdout(std::process::Stdio::null())
            .stderr(std::process::Stdio::null())
            .status();
    };
    if !enable {
        write("ProxyType", "0");
    } else {
        let http = format!("http://127.0.0.1:{port}");
        let socks = format!("socks://127.0.0.1:{port}");
        write("ProxyType", "1");
        write("httpProxy", &http);
        write("httpsProxy", &http);
        write("ftpProxy", &http);
        write("socksProxy", &socks);
        write("NoProxyFor", "localhost,127.0.0.1,::1,10.0.0.0/8,192.168.0.0/16,172.16.0.0/12");
    }
    if paths::program_exists("dbus-send") {
        let _ = std::process::Command::new("dbus-send")
            .args(["--type=signal", "/KIO/Scheduler", "org.kde.KIO.Scheduler.reparseSlaveConfiguration", "string:"])
            .stdin(std::process::Stdio::null())
            .stdout(std::process::Stdio::null())
            .stderr(std::process::Stdio::null())
            .status();
    }
}

#[cfg(not(windows))]
fn xfce_proxy(enable: bool, port: i32) {
    if !paths::program_exists("xfconf-query") { return; }
    let pairs = [
        ("/environment/http_proxy", format!("http://127.0.0.1:{port}")),
        ("/environment/https_proxy", format!("http://127.0.0.1:{port}")),
        ("/environment/ALL_PROXY", format!("socks5://127.0.0.1:{port}")),
        ("/environment/NO_PROXY", "localhost,127.0.0.1,::1".to_string()),
    ];
    for (key, value) in pairs {
        let mut cmd = std::process::Command::new("xfconf-query");
        cmd.args(["-c", "xfce4-session", "-n", "-p", key, "-t", "string"]);
        if enable { cmd.args(["-s", &value]); } else { cmd.arg("-r"); }
        cmd.stdin(std::process::Stdio::null()).stdout(std::process::Stdio::null()).stderr(std::process::Stdio::null());
        let _ = cmd.status();
    }
}

#[cfg(not(windows))]
fn environment_file() -> Option<std::path::PathBuf> {
    let home = std::env::var_os("HOME")?;
    Some(std::path::PathBuf::from(home).join(".config").join("environment.d").join("jester-proxy.conf"))
}

#[cfg(not(windows))]
fn write_environment(port: i32) {
    let Some(path) = environment_file() else { return };
    if let Some(dir) = path.parent() { let _ = std::fs::create_dir_all(dir); }
    let body = format!(
        "http_proxy=http://127.0.0.1:{port}\nhttps_proxy=http://127.0.0.1:{port}\nHTTP_PROXY=http://127.0.0.1:{port}\nHTTPS_PROXY=http://127.0.0.1:{port}\nALL_PROXY=socks5://127.0.0.1:{port}\nNO_PROXY=localhost,127.0.0.1,::1\n"
    );
    let _ = std::fs::write(path, body);
}

#[cfg(not(windows))]
fn clear_environment() {
    if let Some(path) = environment_file() { let _ = std::fs::remove_file(path); }
}

#[cfg(not(windows))]
fn session_environment(enable: bool, port: i32) {
    if !paths::program_exists("systemctl") { return; }
    let names = ["http_proxy", "https_proxy", "HTTP_PROXY", "HTTPS_PROXY", "ALL_PROXY", "NO_PROXY", "all_proxy", "no_proxy"];
    if !enable {
        let mut cmd = std::process::Command::new("systemctl");
        cmd.args(["--user", "unset-environment"]);
        cmd.args(names);
        let _ = cmd.status();
        let _ = std::process::Command::new("dbus-update-activation-environment").args(["--systemd", "http_proxy", "https_proxy", "ALL_PROXY", "NO_PROXY"]).status();
        return;
    }
    let values = [
        format!("http_proxy=http://127.0.0.1:{port}"),
        format!("https_proxy=http://127.0.0.1:{port}"),
        format!("HTTP_PROXY=http://127.0.0.1:{port}"),
        format!("HTTPS_PROXY=http://127.0.0.1:{port}"),
        format!("ALL_PROXY=socks5://127.0.0.1:{port}"),
        format!("all_proxy=socks5://127.0.0.1:{port}"),
        "NO_PROXY=localhost,127.0.0.1,::1".into(),
        "no_proxy=localhost,127.0.0.1,::1".into(),
    ];
    let mut cmd = std::process::Command::new("systemctl");
    cmd.args(["--user", "set-environment"]);
    for value in &values { cmd.arg(value); }
    let _ = cmd.status();
    let _ = std::process::Command::new("dbus-update-activation-environment")
        .arg("--systemd")
        .args(values.iter().map(String::as_str))
        .status();
}

pub fn desktop_notice(text: &str) {
    let text = text.trim();
    if text.is_empty() { return; }
    #[cfg(not(windows))]
    if paths::program_exists("notify-send") {
        let _ = std::process::Command::new("notify-send")
            .args(["-a", "Jester", "-i", "network-vpn", "Jester", text])
            .stdin(std::process::Stdio::null())
            .stdout(std::process::Stdio::null())
            .stderr(std::process::Stdio::null())
            .status();
    }
    #[cfg(windows)]
    {
        let _ = text;
    }
}
