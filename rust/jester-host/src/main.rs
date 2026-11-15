#![cfg_attr(windows, windows_subsystem = "windows")]

mod api;
mod tray;
mod config;
mod core;
mod download;
mod flags;
mod models;
mod normalize;
mod paths;
mod persona;
mod logic;
mod profiles;
mod rules;
mod runtime;
mod settings;
mod sysproxy;

use api::AppState;
use std::sync::{Arc, Mutex};
use std::time::{Duration, Instant};

fn main() {
    set_app_id();
    paths::ensure();
    settings::register_launcher();
    if !single_instance() {
        message("Jester уже запущен.");
        return;
    }
    let settings = Arc::new(settings::Store::load());
    let core = Arc::new(core::Core::new());
    let (tx, rx) = std::sync::mpsc::channel::<String>();
    let runtime = Arc::new(runtime::Runtime::new(&settings.current()));
    let app = Arc::new(AppState { settings: settings.clone(), core: core.clone(), runtime: runtime.clone(), win: Mutex::new(Some(tx)) });
    core::watch_exit(core.clone(), settings.clone(), runtime);
    api::guardian(app.clone());
    let server = app.clone();
    std::thread::spawn(move || {
        let rt = tokio::runtime::Builder::new_multi_thread().enable_all().build().expect("tokio");
        rt.block_on(api::serve(server));
    });
    for _ in 0..50 {
        if std::net::TcpStream::connect(("127.0.0.1", models::APP_PORT)).is_ok() { break; }
        std::thread::sleep(Duration::from_millis(40));
    }
    open_window(app, rx);
}

fn open_window(app: Arc<AppState>, rx: std::sync::mpsc::Receiver<String>) {
    use tao::event::{Event, WindowEvent};
    use tao::event_loop::{ControlFlow, EventLoopBuilder};
    use tao::window::WindowBuilder;
    use tray_icon::menu::MenuEvent;
    use tray_icon::{MouseButton, TrayIconBuilder, TrayIconEvent};
    use wry::WebViewBuilder;

    let mut event_loop_builder = EventLoopBuilder::<String>::with_user_event();
    #[cfg(target_os = "linux")]
    {
        use tao::platform::unix::EventLoopBuilderExtUnix;
        event_loop_builder.with_app_id("com.jester.Client");
    }
    let event_loop = event_loop_builder.build();
    let window_icon = load_rgba(64).and_then(|(pixels, w, h)| tao::window::Icon::from_rgba(pixels, w, h).ok());
    let mut builder = WindowBuilder::new()
        .with_title("Jester")
        .with_inner_size(tao::dpi::LogicalSize::new(1280.0, 820.0))
        .with_min_inner_size(tao::dpi::LogicalSize::new(980.0, 640.0))
        .with_decorations(false)
        .with_transparent(!cfg!(target_os = "linux"))
        .with_resizable(true);
    if let Some(icon) = window_icon { builder = builder.with_window_icon(Some(icon)); }
    let window = builder.build(&event_loop).expect("window");
    #[cfg(windows)]
    let hwnd = {
        use tao::platform::windows::WindowExtWindows;
        window.hwnd() as isize
    };
    #[cfg(windows)]
    enable_acrylic(hwnd);
    let tx = rx_sender(&app);
    #[cfg(not(windows))]
    let proxy = event_loop.create_proxy();
    let webview = WebViewBuilder::new().with_transparent(!cfg!(target_os = "linux"));
    #[cfg(windows)]
    let webview = {
        use wry::WebViewBuilderExtWindows;
        webview.with_default_context_menus(false)
    };
    let webview = webview
        .with_url(format!("http://127.0.0.1:{}/?v=26", models::APP_PORT))
        .with_ipc_handler(move |req| {
            let msg = req.body().trim().trim_matches(|c| c == '"' || c == '\'').to_string();
            if msg == "drag" {
                #[cfg(windows)]
                drag(hwnd);
                #[cfg(not(windows))]
                { let _ = proxy.send_event("drag".into()); }
            } else if !msg.is_empty() {
                let _ = tx.send(msg);
            }
        });
    #[cfg(windows)]
    let _webview = webview.build(&window).expect("webview");
    #[cfg(target_os = "linux")]
    let _webview = {
        use tao::platform::unix::WindowExtUnix;
        use wry::WebViewBuilderExtUnix;
        let vbox = window.default_vbox().expect("gtk box");
        let vbox = Box::leak(Box::new(vbox.clone()));
        webview.build_gtk(vbox).expect("webview")
    };
    #[cfg(not(any(windows, target_os = "linux")))]
    let _webview = webview.build(&window).expect("webview");

    tray::enable_dark_menus();
    let menu = tray::build(&app);
    let tray = std::panic::catch_unwind(std::panic::AssertUnwindSafe(|| {
        load_rgba(32).and_then(|(pixels, w, h)| tray_icon::Icon::from_rgba(pixels, w, h).ok()).and_then(|icon| {
            TrayIconBuilder::new()
                .with_menu(Box::new(menu))
                .with_tooltip("Jester")
                .with_icon(icon)
                .build()
                .ok()
        })
    })).ok().flatten();
    let tray_alive = tray.is_some();
    let mut menu_at = Instant::now();
    let mut menu_key = String::new();

    if tray_alive && app.settings.current().start_hidden { window.set_visible(false); }

    #[cfg(target_os = "linux")]
    let mut fitted = false;
    #[cfg(target_os = "linux")]
    let mut fit_tries = 0u8;
    event_loop.run(move |event, _, control_flow| {
        #[cfg(target_os = "linux")]
        if !fitted && fit_tries < 30 {
            fit_tries += 1;
            fitted = fit_jester_on_hyprland();
        }
        if let Event::UserEvent(cmd) = &event {
            if cmd == "drag" {
                let _ = window.drag_window();
                return;
            }
        }
        *control_flow = ControlFlow::WaitUntil(Instant::now() + Duration::from_millis(80));
        let _keep_tray = &tray;
        while let Ok(cmd) = rx.try_recv() { apply_cmd(&window, &app, cmd.as_str(), control_flow, tray_alive); }
        while let Ok(click) = TrayIconEvent::receiver().try_recv() {
            if let TrayIconEvent::DoubleClick { button: MouseButton::Left, .. } = click {
                window.set_visible(true);
                let _ = window.set_focus();
            }
        }
        if menu_at.elapsed() > Duration::from_millis(1200) {
            menu_at = Instant::now();
            let key = tray::signature(&app);
            if key != menu_key {
                menu_key = key;
                if let Some(icon) = tray.as_ref() { icon.set_menu(Some(Box::new(tray::build(&app)))); }
            }
        }
        while let Ok(menu_event) = MenuEvent::receiver().try_recv() {
            let id = menu_event.id.0.clone();
            if tray::handle(&app, &id) { app.core.stop(); *control_flow = ControlFlow::Exit; }
            if id == "show" { window.set_visible(true); let _ = window.set_focus(); }
        }
        if let Event::WindowEvent { event: WindowEvent::CloseRequested, .. } = event {
            apply_cmd(&window, &app, "close", control_flow, tray_alive);
        }
    });
}

fn rx_sender(app: &AppState) -> std::sync::mpsc::Sender<String> {
    app.win.lock().expect("win").clone().unwrap_or_else(|| std::sync::mpsc::channel().0)
}

fn apply_cmd(window: &tao::window::Window, app: &AppState, cmd: &str, control_flow: &mut tao::event_loop::ControlFlow, tray_alive: bool) {
    match cmd {
        "minimize" => window.set_minimized(true),
        "maximize" => toggle_maximize(window),
        "tray" | "close" => {
            if tray_alive && app.settings.current().close_to_tray { window.set_visible(false); }
            else { app.core.stop(); *control_flow = tao::event_loop::ControlFlow::Exit; }
        }
        "show" => { window.set_visible(true); let _ = window.set_focus(); }
        "quit" => { app.core.stop(); *control_flow = tao::event_loop::ControlFlow::Exit; }
        _ => {}
    }
}

fn toggle_maximize(window: &tao::window::Window) {
    #[cfg(target_os = "linux")]
    if let Some(info) = jester_hypr_window() {
        if info.floating {
            hypr_dispatch(&format!(r#"hl.dsp.window.float({{action="toggle", window="address:{}"}})"#, info.address));
        } else {
            place_hypr_window(&info.address);
        }
        return;
    }
    window.set_maximized(!window.is_maximized());
}

#[cfg(target_os = "linux")]
fn fit_jester_on_hyprland() -> bool {
    if std::env::var_os("HYPRLAND_INSTANCE_SIGNATURE").is_none() || !paths::program_exists("hyprctl") {
        return true;
    }
    let Some(info) = jester_hypr_window() else { return false };
    if !info.floating { place_hypr_window(&info.address); }
    true
}

#[cfg(target_os = "linux")]
struct HyprWin { address: String, floating: bool }

#[cfg(target_os = "linux")]
fn jester_hypr_window() -> Option<HyprWin> {
    let out = std::process::Command::new("hyprctl").args(["clients", "-j"]).output().ok()?;
    let list: Vec<serde_json::Value> = serde_json::from_slice(&out.stdout).ok()?;
    list.into_iter().find_map(|client| {
        if client.get("class").and_then(|v| v.as_str()) != Some("jester") { return None; }
        let address = client.get("address").and_then(|v| v.as_str())?.to_string();
        let floating = client.get("floating").and_then(|v| v.as_bool()).unwrap_or(false);
        Some(HyprWin { address, floating })
    })
}

#[cfg(target_os = "linux")]
fn place_hypr_window(address: &str) {
    let window = format!("address:{address}");
    hypr_dispatch(&format!(r#"hl.dsp.window.float({{action="toggle", window="{window}"}})"#));
    hypr_dispatch(&format!(r#"hl.dsp.window.resize({{x=1280, y=820, exact=true, window="{window}"}})"#));
    hypr_dispatch(&format!(r#"hl.dsp.window.center({{window="{window}"}})"#));
}

#[cfg(target_os = "linux")]
fn hypr_dispatch(expr: &str) {
    let _ = std::process::Command::new("hyprctl")
        .args(["dispatch", expr])
        .stdout(std::process::Stdio::null())
        .stderr(std::process::Stdio::null())
        .status();
}

fn load_rgba(size: u32) -> Option<(Vec<u8>, u32, u32)> {
    let path = std::path::PathBuf::from(env!("CARGO_MANIFEST_DIR")).join("../../ui/public/jester-head.png");
    let img = image::open(path).ok()?;
    let img = img.resize_exact(size, size, image::imageops::FilterType::Lanczos3).into_rgba8();
    Some((img.into_raw(), size, size))
}

#[cfg(windows)]
fn enable_acrylic(hwnd: isize) {
    unsafe {
        use windows::Win32::Foundation::HWND;
        use windows::Win32::Graphics::Dwm::{DwmSetWindowAttribute, DWMWINDOWATTRIBUTE};
        let hwnd = HWND(hwnd as *mut _);
        let dark: i32 = 1;
        let _ = DwmSetWindowAttribute(hwnd, DWMWINDOWATTRIBUTE(20), &dark as *const _ as *const _, 4);
        let acrylic: i32 = 3;
        let _ = DwmSetWindowAttribute(hwnd, DWMWINDOWATTRIBUTE(38), &acrylic as *const _ as *const _, 4);
    }
}

#[cfg(windows)]
fn drag(hwnd: isize) {
    unsafe {
        use windows::Win32::Foundation::HWND;
        use windows::Win32::UI::Input::KeyboardAndMouse::ReleaseCapture;
        use windows::Win32::UI::WindowsAndMessaging::{SendMessageW, HTCAPTION, WM_NCLBUTTONDOWN};
        let _ = ReleaseCapture();
        let _ = SendMessageW(
            HWND(hwnd as *mut _),
            WM_NCLBUTTONDOWN,
            Some(windows::Win32::Foundation::WPARAM(HTCAPTION as usize)),
            Some(windows::Win32::Foundation::LPARAM(0)),
        );
    }
}

fn set_app_id() {
    #[cfg(windows)]
    unsafe {
        use windows::core::w;
        use windows::Win32::UI::Shell::SetCurrentProcessExplicitAppUserModelID;
        let _ = SetCurrentProcessExplicitAppUserModelID(w!("Jester.Proxy.Client"));
    }
}

fn single_instance() -> bool {
    #[cfg(windows)]
    unsafe {
        use windows::core::w;
        use windows::Win32::Foundation::{CloseHandle, GetLastError};
        use windows::Win32::System::Threading::CreateMutexW;
        let elevated = std::env::args().any(|arg| arg == "--elevated");
        let attempts = if elevated { 40 } else { 1 };
        for _ in 0..attempts {
            let handle = CreateMutexW(None, true, w!("Local\\JesterProxyClient.SingleInstance"));
            let busy = GetLastError().0 == 183;
            if !busy {
                if let Ok(handle) = handle { std::mem::forget(handle); }
                return true;
            }
            if let Ok(handle) = handle { let _ = CloseHandle(handle); }
            if attempts == 1 { break; }
            std::thread::sleep(Duration::from_millis(100));
        }
        false
    }
    #[cfg(not(windows))]
    {
        use std::os::unix::io::AsRawFd;
        let Ok(file) = std::fs::OpenOptions::new().create(true).read(true).write(true).open(paths::instance_lock()) else {
            return true;
        };
        let fd = file.as_raw_fd();
        let elevated = std::env::args().any(|arg| arg == "--elevated");
        let attempts = if elevated { 40 } else { 1 };
        for _ in 0..attempts {
            if unsafe { libc::flock(fd, libc::LOCK_EX | libc::LOCK_NB) } == 0 {
                std::mem::forget(file);
                return true;
            }
            if attempts == 1 { break; }
            std::thread::sleep(Duration::from_millis(100));
        }
        false
    }
}

fn message(text: &str) {
    #[cfg(windows)]
    unsafe {
        use std::os::windows::ffi::OsStrExt;
        use windows::core::PCWSTR;
        use windows::Win32::UI::WindowsAndMessaging::{MessageBoxW, MB_ICONINFORMATION, MB_OK};
        let wide: Vec<u16> = std::ffi::OsStr::new(text).encode_wide().chain(std::iter::once(0)).collect();
        let title: Vec<u16> = "Jester".encode_utf16().chain(std::iter::once(0)).collect();
        let _ = MessageBoxW(None, PCWSTR(wide.as_ptr()), PCWSTR(title.as_ptr()), MB_OK | MB_ICONINFORMATION);
    }
    #[cfg(not(windows))]
    {
        let _ = std::process::Command::new("notify-send").args(["Jester", text]).status();
        eprintln!("Jester: {text}");
    }
}
