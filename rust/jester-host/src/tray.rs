use crate::api::AppState;
use crate::{paths, profiles};
use std::sync::Arc;
use tray_icon::menu::{CheckMenuItem, Menu, MenuItem, PredefinedMenuItem, Submenu};

pub fn enable_dark_menus() {
    #[cfg(windows)]
    unsafe {
        use std::ffi::CString;
        use windows::core::w;
        use windows::Win32::System::LibraryLoader::{GetProcAddress, LoadLibraryW};
        let Ok(lib) = LoadLibraryW(w!("uxtheme.dll")) else { return };
        let set = GetProcAddress(lib, windows::core::PCSTR(135usize as _)).or_else(|| {
            CString::new("SetPreferredAppMode").ok().and_then(|name| GetProcAddress(lib, windows::core::PCSTR(name.as_ptr() as _)))
        });
        if let Some(proc) = set {
            let set: unsafe extern "system" fn(i32) -> i32 = std::mem::transmute(proc);
            set(2);
        }
        let flush = GetProcAddress(lib, windows::core::PCSTR(136usize as _)).or_else(|| {
            CString::new("FlushMenuThemes").ok().and_then(|name| GetProcAddress(lib, windows::core::PCSTR(name.as_ptr() as _)))
        });
        if let Some(proc) = flush {
            let flush: unsafe extern "system" fn() = std::mem::transmute(proc);
            flush();
        }
    }
}

pub fn signature(app: &AppState) -> String {
    let s = app.settings.current();
    let names = server_names(&s.active_profile_id);
    format!(
        "{}|{}|{}|{}|{}|{}",
        app.core.running(),
        s.mode,
        s.tun_enabled,
        s.system_proxy,
        s.last_selected_node,
        names.join("\n")
    )
}

pub fn build(app: &AppState) -> Menu {
    let s = app.settings.current();
    let running = app.core.running();
    let names = server_names(&s.active_profile_id);
    let current = if s.last_selected_node.is_empty() { names.first().cloned().unwrap_or_default() } else { s.last_selected_node.clone() };
    let status = if running {
        format!("Подключено  ·  {}", plain(&current))
    } else {
        "Не подключено".into()
    };
    let menu = Menu::new();
    let head = MenuItem::with_id("status", status, false, None);
    let power = MenuItem::with_id("power", if running { "Отключиться" } else { "Подключиться" }, true, None);
    let servers = Submenu::new("Сменить сервер", !names.is_empty());
    if names.is_empty() {
        let _ = servers.append(&MenuItem::with_id("srv-empty", "Нет серверов", false, None));
    } else {
        for name in names.iter().take(24) {
            let item = CheckMenuItem::with_id(format!("srv:{name}"), plain(name), true, name == &current, None);
            let _ = servers.append(&item);
        }
    }
    let transport = Submenu::new("Режим транспорта", true);
    let _ = transport.append(&CheckMenuItem::with_id("tr:tun", "Системный туннель", true, s.tun_enabled, None));
    let _ = transport.append(&CheckMenuItem::with_id("tr:proxy", "Системный прокси", true, s.system_proxy && !s.tun_enabled, None));
    let _ = transport.append(&CheckMenuItem::with_id("tr:local", "Локальный прокси", true, !s.tun_enabled && !s.system_proxy, None));
    let import = MenuItem::with_id("import", "Импорт из буфера обмена", true, None);
    let route = Submenu::new("Маршрутизация", true);
    let _ = route.append(&CheckMenuItem::with_id("mode:rule", "По правилам", true, s.mode == "rule", None));
    let _ = route.append(&CheckMenuItem::with_id("mode:global", "Глобальный", true, s.mode == "global", None));
    let _ = route.append(&CheckMenuItem::with_id("mode:direct", "Прямое соединение", true, s.mode == "direct", None));
    let show = MenuItem::with_id("show", "Показать окно", true, None);
    let quit = MenuItem::with_id("quit", "Выйти", true, None);
    let _ = menu.append_items(&[
        &head,
        &PredefinedMenuItem::separator(),
        &power,
        &servers,
        &transport,
        &import,
        &route,
        &PredefinedMenuItem::separator(),
        &show,
        &quit,
    ]);
    menu
}

pub fn handle(app: &Arc<AppState>, id: &str) -> bool {
    if id == "quit" { return true; }
    if id == "power" {
        let settings = app.settings.current();
        if app.core.running() { app.core.stop(); }
        else { let _ = app.core.start(&settings); }
    } else if let Some(name) = id.strip_prefix("srv:") {
        let name = name.to_string();
        let _ = app.settings.patch(|s| s.last_selected_node = name.clone());
        let settings = app.settings.current();
        if app.core.running() { app.core.select(settings.external_port, "PROXY", &name); }
    } else if let Some(mode) = id.strip_prefix("tr:") {
        let mode = mode.to_string();
        let _ = app.settings.patch(|s| {
            s.tun_enabled = mode == "tun";
            s.system_proxy = mode == "proxy";
        });
        restart_if_needed(app);
    } else if let Some(mode) = id.strip_prefix("mode:") {
        let mode = mode.to_string();
        let _ = app.settings.patch(|s| s.mode = mode);
        restart_if_needed(app);
    } else if id == "import" {
        let app = app.clone();
        std::thread::spawn(move || import_clipboard(&app));
    }
    false
}

fn restart_if_needed(app: &AppState) {
    if !app.core.running() { return; }
    let settings = app.settings.current();
    let _ = app.core.restart(&settings);
}

fn import_clipboard(app: &AppState) {
    let text = clipboard_text().unwrap_or_default();
    let text = text.trim().to_string();
    if text.is_empty() { notice("Буфер обмена пуст."); return; }
    let result = if text.starts_with("http://") || text.starts_with("https://") {
        profiles::create("Подписка", "subscription", &text, "")
    } else {
        profiles::create("Импорт", "paste", "", &text)
    };
    match result {
        Ok(info) => {
            let _ = app.settings.patch(|s| s.active_profile_id = info.id.clone());
            if app.core.running() {
                let _ = app.core.restart(&app.settings.current());
            }
            notice("Профиль импортирован.");
        }
        Err(err) => notice(&err),
    }
}

fn server_names(profile_id: &str) -> Vec<String> {
    let path = profiles::config_path(profile_id).unwrap_or_else(|_| paths::runtime_config());
    let Ok(text) = std::fs::read_to_string(path) else { return Vec::new() };
    let Ok(doc) = serde_yaml::from_str::<serde_yaml::Value>(&text) else { return Vec::new() };
    doc.get("proxies").and_then(|v| v.as_sequence()).map(|list| {
        list.iter().filter_map(|item| item.get("name").and_then(|n| n.as_str()).map(|s| s.to_string())).filter(|n| !n.is_empty()).collect()
    }).unwrap_or_default()
}

fn plain(name: &str) -> String {
    let mut out = String::new();
    for ch in name.chars() {
        let code = ch as u32;
        if (0x1F1E6..=0x1F1FF).contains(&code) { continue; }
        if ch == '&' { out.push_str("&&"); } else { out.push(ch); }
    }
    out.trim().trim_start_matches(|c: char| "•·∙●⟳↻↺-|".contains(c) || c.is_whitespace()).trim().to_string()
}

fn clipboard_text() -> Option<String> {
    #[cfg(windows)]
    unsafe {
        use std::os::windows::ffi::OsStringExt;
        use windows::Win32::Foundation::HGLOBAL;
        use windows::Win32::System::DataExchange::{CloseClipboard, GetClipboardData, OpenClipboard};
        use windows::Win32::System::Memory::{GlobalLock, GlobalUnlock};
        if OpenClipboard(None).is_err() { return None; }
        let handle = GetClipboardData(13).ok();
        let text = handle.and_then(|handle| {
            let ptr = GlobalLock(HGLOBAL(handle.0));
            if ptr.is_null() { return None; }
            let mut len = 0usize;
            while *((ptr as *const u16).add(len)) != 0 { len += 1; }
            let slice = std::slice::from_raw_parts(ptr as *const u16, len);
            let text = std::ffi::OsString::from_wide(slice).to_string_lossy().to_string();
            let _ = GlobalUnlock(HGLOBAL(handle.0));
            Some(text)
        });
        let _ = CloseClipboard();
        return text.filter(|s| !s.trim().is_empty());
    }
    #[cfg(not(windows))]
    { None }
}

fn notice(text: &str) {
    #[cfg(windows)]
    unsafe {
        use std::os::windows::ffi::OsStrExt;
        use windows::core::PCWSTR;
        use windows::Win32::UI::WindowsAndMessaging::{MessageBoxW, MB_OK};
        let wide: Vec<u16> = std::ffi::OsStr::new(text).encode_wide().chain(std::iter::once(0)).collect();
        let title: Vec<u16> = "Jester".encode_utf16().chain(std::iter::once(0)).collect();
        let _ = MessageBoxW(None, PCWSTR(wide.as_ptr()), PCWSTR(title.as_ptr()), MB_OK);
    }
}
