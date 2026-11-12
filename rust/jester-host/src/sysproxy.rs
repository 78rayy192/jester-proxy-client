use winreg::enums::*;
use winreg::RegKey;

const KEY: &str = r"Software\Microsoft\Windows\CurrentVersion\Internet Settings";

pub fn set(enable: bool, port: i32) -> Result<(), String> {
    if enable && !(1..=65535).contains(&port) { return Err("Некорректный порт прокси.".into()); }
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

pub fn is_enabled() -> bool {
    let hkcu = RegKey::predef(HKEY_CURRENT_USER);
    let Ok(key) = hkcu.open_subkey(KEY) else { return false };
    key.get_value::<u32, _>("ProxyEnable").unwrap_or(0) == 1
}

fn notify() {
    #[cfg(windows)]
    unsafe {
        use windows::Win32::Networking::WinInet::{InternetSetOptionW, INTERNET_OPTION_REFRESH, INTERNET_OPTION_SETTINGS_CHANGED};
        let _ = InternetSetOptionW(None, INTERNET_OPTION_SETTINGS_CHANGED, None, 0);
        let _ = InternetSetOptionW(None, INTERNET_OPTION_REFRESH, None, 0);
    }
}
