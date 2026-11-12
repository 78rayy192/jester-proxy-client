use crate::core::Core;
use crate::models::{AppSettings, APP_PORT, APP_VERSION};
use crate::persona;
use crate::settings::Store;
use crate::{profiles, rules, sysproxy};
use axum::body::Body;
use axum::extract::State;
use axum::http::{header, HeaderValue, Method, Request, StatusCode};
use tower_http::set_header::SetResponseHeaderLayer;
use axum::response::Response;
use axum::routing::any;
use axum::Router;
use serde_json::{json, Value};
use std::sync::{Arc, Mutex};
use tower_http::cors::CorsLayer;
use tower_http::services::ServeDir;

pub struct AppState {
    pub settings: Arc<Store>,
    pub core: Arc<Core>,
    pub win: Mutex<Option<std::sync::mpsc::Sender<String>>>,
}

pub async fn serve(app: Arc<AppState>) {
    let ui = crate::paths::ui_dir();
    let router = Router::new()
        .route("/api/appearance/wallpaper", any(wallpaper))
        .route("/api/{*path}", any(dispatch))
        .route("/api", any(dispatch))
        .route("/clash/{*path}", any(clash))
        .route("/clash", any(clash))
        .fallback_service(ServeDir::new(ui).append_index_html_on_directories(true))
        .layer(SetResponseHeaderLayer::overriding(
            header::CACHE_CONTROL,
            HeaderValue::from_static("no-store"),
        ))
        .layer(CorsLayer::permissive())
        .with_state(app);
    let listener = match tokio::net::TcpListener::bind(("127.0.0.1", APP_PORT)).await {
        Ok(l) => l,
        Err(e) => {
            message(&format!("Не удалось открыть локальный порт {APP_PORT}.\n{e}"));
            return;
        }
    };
    let _ = axum::serve(listener, router).await;
}

async fn wallpaper(State(app): State<Arc<AppState>>) -> Response {
    let path = app.settings.current().appearance.background_image.unwrap_or_default();
    let file = std::path::PathBuf::from(&path);
    if path.is_empty() || !file.is_file() {
        return Response::builder().status(404).body(Body::empty()).unwrap();
    }
    let bytes = tokio::fs::read(&file).await.unwrap_or_default();
    let ctype = match file.extension().and_then(|e| e.to_str()).unwrap_or("").to_ascii_lowercase().as_str() {
        "png" => "image/png",
        "jpg" | "jpeg" => "image/jpeg",
        "webp" => "image/webp",
        "gif" => "image/gif",
        "bmp" => "image/bmp",
        _ => "application/octet-stream",
    };
    Response::builder().header(header::CONTENT_TYPE, ctype).body(Body::from(bytes)).unwrap()
}

async fn dispatch(State(app): State<Arc<AppState>>, req: Request<Body>) -> Response {
    let method = req.method().clone();
    let path = req.uri().path().to_string();
    let query = req.uri().query().unwrap_or("").to_string();
    if method == Method::OPTIONS { return Response::builder().status(204).body(Body::empty()).unwrap(); }
    let bytes = axum::body::to_bytes(req.into_body(), 8 * 1024 * 1024).await.unwrap_or_default();
    let body: Value = serde_json::from_slice(&bytes).unwrap_or(Value::Null);
    let result = tokio::task::spawn_blocking(move || handle(&app, &method, &path, &query, &body)).await;
    match result {
        Ok(Ok(v)) => json_ok(v),
        Ok(Err(e)) => json_err(e),
        Err(e) => json_err(e.to_string()),
    }
}

fn handle(app: &AppState, method: &Method, path: &str, query: &str, body: &Value) -> Result<Value, String> {
    let key = format!("{} {}", method.as_str(), path);
    let settings = app.settings.current();
    match key.as_str() {
        "GET /api/health" => Ok(json!({"ok": true, "name": "Jester"})),
        "GET /api/status" => Ok(serde_json::to_value(app.core.status(&settings)).unwrap()),
        "GET /api/settings" | "GET /api/settings/export" => Ok(serde_json::to_value(settings).unwrap()),
        "PUT /api/settings" => {
            let next: AppSettings = serde_json::from_value(body.clone()).map_err(|e| e.to_string())?;
            Ok(serde_json::to_value(app.settings.save(next)?).unwrap())
        }
        "GET /api/appearance" => Ok(serde_json::to_value(settings.appearance).unwrap()),
        "PUT /api/appearance" => {
            let appearance = serde_json::from_value(body.clone()).map_err(|e| e.to_string())?;
            let saved = app.settings.patch(|s| s.appearance = appearance)?;
            Ok(serde_json::to_value(saved.appearance).unwrap())
        }
        "POST /api/appearance/pick-wallpaper" => {
            let picked = rfd::FileDialog::new().set_title("Фон окна").add_filter("Изображения", &["jpg", "jpeg", "png", "webp", "gif", "bmp"]).pick_file();
            let Some(path) = picked else { return Ok(json!({"cancelled": true})); };
            let saved = app.settings.patch(|s| {
                s.appearance.background_image = Some(path.display().to_string());
                s.appearance.background_style = "image".into();
            })?;
            Ok(serde_json::to_value(saved.appearance).unwrap())
        }
        "GET /api/profiles" => Ok(serde_json::to_value(profiles::list()).unwrap()),
        "POST /api/profiles" => {
            let name = body.get("name").and_then(|v| v.as_str()).unwrap_or("Профиль");
            let kind = body.get("kind").and_then(|v| v.as_str()).unwrap_or("paste");
            let source = body.get("source").and_then(|v| v.as_str()).unwrap_or("");
            let payload = body.get("payload").and_then(|v| v.as_str()).unwrap_or("");
            let created = profiles::create(name, kind, source, payload)?;
            if app.settings.current().active_profile_id.is_empty() {
                let id = created.id.clone();
                app.settings.patch(|s| s.active_profile_id = id)?;
            }
            Ok(serde_json::to_value(created).unwrap())
        }
        "POST /api/profiles/update-all" => {
            let (updated, failed, errors) = profiles::update_all();
            app.settings.patch(|s| s.last_subscription_update_at = Some(chrono::Utc::now().to_rfc3339()))?;
            if app.core.running() && updated > 0 { let _ = app.core.restart(&app.settings.current()); }
            Ok(json!({"updated": updated, "failed": failed, "errors": errors}))
        }
        "GET /api/app/update" => Ok(check_update()),
        "POST /api/settings/import" => {
            let next: AppSettings = serde_json::from_value(body.clone()).map_err(|_| "Пустой файл настроек.".to_string())?;
            Ok(serde_json::to_value(app.settings.save(next)?).unwrap())
        }
        "POST /api/presets/speed" => Ok(serde_json::to_value(preset(app, "speed")?).unwrap()),
        "POST /api/presets/stability" => Ok(serde_json::to_value(preset(app, "stability")?).unwrap()),
        "POST /api/presets/protection" => Ok(serde_json::to_value(preset(app, "protection")?).unwrap()),
        "POST /api/persona/home" | "POST /api/persona/mobile" | "POST /api/persona/corporate" | "POST /api/persona/auto" => {
            let name = path.rsplit('/').next().unwrap_or("auto");
            let saved = app.settings.patch(|s| { s.network_persona = name.into(); s.performance_preset = "custom".into(); persona::apply(s); })?;
            Ok(serde_json::to_value(saved).unwrap())
        }
        "GET /api/guardian" => {
            let s = app.settings.current();
            Ok(json!({
                "message": s.guardian_message, "lane": s.adaptive_lane_state, "rttMs": s.last_measured_rtt_ms,
                "deadManArmed": s.dead_man_armed, "exitIp": s.last_known_exit_ip, "exitOrg": s.last_known_exit_org,
                "smartDirect": s.smart_direct_domains, "dnsPoolIndex": s.dns_pool_index
            }))
        }
        "POST /api/guardian/disarm" => {
            let _ = sysproxy::set(false, 0);
            app.settings.patch(|s| s.dead_man_armed = false)?;
            Ok(json!({"ok": true, "deadManArmed": false}))
        }
        "POST /api/guardian/warm" => { warm(app); Ok(json!({"ok": true})) }
        "POST /api/guardian/learn-direct" => learn_direct(app),
        "GET /api/logs" => Ok(json!({"text": app.core.log_tail(250)})),
        "POST /api/core/start" => Ok(serde_json::to_value(app.core.start(&app.settings.current())?).unwrap()),
        "POST /api/core/stop" => { app.core.stop(); Ok(serde_json::to_value(app.core.status(&app.settings.current())).unwrap()) }
        "POST /api/core/restart" => Ok(serde_json::to_value(app.core.restart(&app.settings.current())?).unwrap()),
        "POST /api/core/download" => {
            let channel = app.settings.current().core_channel;
            app.core.downloader.install(&channel)?;
            Ok(serde_json::to_value(app.core.status(&app.settings.current())).unwrap())
        }
        "POST /api/core/elevate" => {
            let ok = elevate();
            if ok {
                let tx = app.win.lock().expect("win").clone();
                std::thread::spawn(move || {
                    std::thread::sleep(std::time::Duration::from_millis(400));
                    if let Some(tx) = tx.as_ref() { let _ = tx.send("quit".into()); }
                });
            }
            Ok(json!({"ok": ok}))
        }
        "GET /api/sysproxy" => Ok(json!({"enable": settings.system_proxy, "windowsProxy": sysproxy::is_enabled(), "mixedPort": settings.mixed_port})),
        "POST /api/sysproxy" => {
            let enable = body.get("enable").and_then(|v| v.as_bool()).unwrap_or(false);
            if enable {
                let restart = app.core.running() && app.settings.current().tun_enabled;
                app.settings.patch(|s| { s.system_proxy = true; s.tun_enabled = false; })?;
                if restart { app.core.restart(&app.settings.current())?; }
                else { sysproxy::set(true, app.settings.current().mixed_port)?; }
            } else {
                sysproxy::set(false, 0)?;
                app.settings.patch(|s| s.system_proxy = false)?;
            }
            let s = app.settings.current();
            Ok(json!({"enable": s.system_proxy, "tunEnabled": s.tun_enabled, "windowsProxy": sysproxy::is_enabled(), "mixedPort": s.mixed_port}))
        }
        "POST /api/proxies/test" => {
            if !app.core.running() { return Err("Ядро не запущено.".into()); }
            let q = query_map(query);
            let url = q.get("url").cloned().unwrap_or(settings.latency_url);
            let timeout = q.get("timeout").and_then(|s| s.parse().ok()).unwrap_or(settings.latency_timeout_ms);
            Ok(test_delays(app, &url, timeout))
        }
        "GET /api/rules/status" => Ok(serde_json::to_value(rules::status(&settings)).unwrap()),
        "POST /api/rules/update" => Ok(serde_json::to_value(rules::update(&settings)?).unwrap()),
        "GET /api/leak-check" => leak(app),
        "POST /api/ghost/panic" => panic(app),
        "POST /api/ghost/rush" => rush(app),
        "POST /api/ghost/learn" => learn_proxy(app),
        "POST /api/ports" => {
            let cur = app.settings.current();
            let mixed = body.get("mixedPort").and_then(|v| v.as_i64()).unwrap_or(cur.mixed_port as i64) as i32;
            let socks = body.get("socksPort").and_then(|v| v.as_i64()).unwrap_or(cur.socks_port as i64) as i32;
            let external = body.get("externalPort").and_then(|v| v.as_i64()).unwrap_or(cur.external_port as i64) as i32;
            let running = app.core.running();
            app.settings.patch(|s| { s.mixed_port = mixed; s.socks_port = socks; s.external_port = external; })?;
            if running { app.core.restart(&app.settings.current())?; }
            Ok(serde_json::to_value(app.settings.current()).unwrap())
        }
        "POST /api/window/minimize" => { send(app, "minimize"); Ok(json!({"ok": true})) }
        "POST /api/window/maximize" => { send(app, "maximize"); Ok(json!({"ok": true})) }
        "POST /api/window/close" => { send(app, if app.settings.current().close_to_tray { "tray" } else { "close" }); Ok(json!({"ok": true})) }
        "POST /api/window/capture" => {
            if let Some(hide) = body.get("hide").and_then(|v| v.as_bool()) {
                app.settings.patch(|s| s.hide_from_capture = hide)?;
            }
            Ok(json!({"hide": app.settings.current().hide_from_capture || app.settings.current().stealth_mode}))
        }
        _ => profile_routes(app, method, path, body),
    }
}

fn profile_routes(app: &AppState, method: &Method, path: &str, body: &Value) -> Result<Value, String> {
    let rest = path.strip_prefix("/api/profiles/").ok_or("not found")?;
    if method == Method::POST {
        if let Some(id) = rest.strip_suffix("/activate") {
        if profiles::get(id).is_none() { return Err("not found".into()); }
        app.settings.patch(|s| s.active_profile_id = id.to_string())?;
        if app.core.running() { let _ = app.core.restart(&app.settings.current()); }
        return Ok(serde_json::to_value(app.settings.current()).unwrap());
        }
    }
    if method == Method::POST {
        if let Some(id) = rest.strip_suffix("/update") {
        let updated = profiles::update_subscription(id)?;
        if app.core.running() && app.settings.current().active_profile_id == id { let _ = app.core.restart(&app.settings.current()); }
        return Ok(serde_json::to_value(updated).unwrap());
        }
    }
    if method == Method::DELETE {
        let active = app.settings.current().active_profile_id == rest;
        profiles::delete(rest)?;
        if active { app.settings.patch(|s| s.active_profile_id.clear())?; }
        return Ok(json!({"ok": true}));
    }
    if method == Method::PUT {
        let name = body.get("name").and_then(|v| v.as_str());
        let fp = if body.get("fingerprintOverride").is_some() {
            Some(body.get("fingerprintOverride").and_then(|v| v.as_str()).map(|s| s.to_string()))
        } else { None };
        let ns = if let Some(v) = body.get("nameserverOverride") {
            Some(match v {
                Value::String(text) => Some(text.lines().map(|l| l.trim().to_string()).filter(|l| !l.is_empty()).collect()),
                Value::Array(arr) => Some(arr.iter().filter_map(|x| x.as_str().map(|s| s.to_string())).filter(|s| !s.is_empty()).collect()),
                Value::Null => None,
                _ => None,
            })
        } else { None };
        return Ok(serde_json::to_value(profiles::patch(rest, name, fp, ns)?).unwrap());
    }
    Err("not found".into())
}

fn preset(app: &AppState, name: &str) -> Result<AppSettings, String> {
    app.settings.patch(|s| {
        s.performance_preset = name.into();
        match name {
            "speed" => {
                s.rf_rush_mode = true; s.prefer_rush_lane = true; s.tcp_concurrent = true; s.unified_delay = true;
                s.high_priority = true; s.stealth_mode = false; s.silent_logs = false; s.log_level = "warning".into();
                s.strict_route = false; s.adaptive_lane = true; s.warm_start = true; s.split_udp_policy = true;
                s.per_app_fast_path = true; s.network_persona = "home".into();
            }
            "stability" => {
                s.rf_rush_mode = false; s.prefer_rush_lane = false; s.tcp_concurrent = true; s.unified_delay = true;
                s.high_priority = false; s.stealth_mode = true; s.block_quic = true; s.block_stun = true;
                s.dns_via_proxy = true; s.silent_logs = true; s.strict_route = false; s.adaptive_lane = false;
                s.latency_budget_enabled = true; s.latency_budget_ms = 320; s.network_persona = "auto".into();
            }
            "protection" => {
                s.rf_rush_mode = true; s.prefer_rush_lane = true; s.stealth_mode = true; s.block_quic = true;
                s.block_stun = true; s.dns_via_proxy = true; s.dns_respect_rules = true; s.random_fingerprint = true;
                s.silent_logs = true; s.hide_from_capture = true; s.strict_route = true; s.use_mrs_rules = true;
                s.traffic_padding = true; s.dns_diversity = true; s.dns_strict_no_fallback = true;
                s.exit_consistency_check = true; s.dead_man_switch = true; s.network_persona = "corporate".into();
            }
            _ => {}
        }
    })
}

fn warm(app: &AppState) {
    if !app.core.running() { return; }
    let s = app.settings.current();
    for group in ["RUSH", "AUTO", "UDP-FAST"] {
        let _ = app.core.best_delay(s.external_port, group, &s.latency_url, s.latency_timeout_ms.clamp(2000, 8000));
    }
    if s.prefer_rush_lane && s.rf_rush_mode { app.core.select(s.external_port, "PROXY", "RUSH"); }
}

fn rush(app: &AppState) -> Result<Value, String> {
    app.settings.patch(|s| { s.rf_rush_mode = true; s.prefer_rush_lane = true; s.tcp_concurrent = true; s.unified_delay = true; })?;
    if !crate::paths::mihomo_exe().exists() || app.settings.current().active_profile_id.is_empty() {
        return Ok(json!({"enabled": true, "message": "Ускоренный канал включён. Загрузите ядро, выберите профиль и запустите подключение."}));
    }
    if app.core.running() { app.core.restart(&app.settings.current())?; } else {
        return Ok(json!({"enabled": true, "message": "Ускоренный канал включён. Запустите ядро."}));
    }
    let s = app.settings.current();
    app.core.select(s.external_port, "PROXY", "RUSH");
    let mut node = None;
    let mut delay = 0;
    for group in ["RUSH", "AUTO", "PROXY"] {
        if let Some((name, d)) = app.core.best_delay(s.external_port, group, &s.latency_url, s.latency_timeout_ms.clamp(2000, 8000)) {
            node = Some(name); delay = d; break;
        }
    }
    if let Some(name) = &node {
        app.core.select(s.external_port, "RUSH", name);
        app.core.select(s.external_port, "PROXY", name);
        Ok(json!({"enabled": true, "lane": "RUSH", "node": name, "delayMs": delay, "message": format!("Ускоренный канал активен. Выбран сервер {name}, задержка {delay} мс.")}))
    } else {
        Ok(json!({"enabled": true, "lane": "RUSH", "message": "Ускоренный канал активен. Серверы ещё проверяются."}))
    }
}

fn learn_proxy(app: &AppState) -> Result<Value, String> {
    if !app.core.running() { return Ok(json!({"ok": false, "message": "Сначала запустите ядро."})); }
    let s = app.settings.current();
    let client = app.core.clash(s.external_port);
    let res = client.get(format!("http://127.0.0.1:{}/connections", s.external_port)).header("Authorization", format!("Bearer {}", app.core.secret)).send().map_err(|e| e.to_string())?;
    let doc: Value = res.json().map_err(|e| e.to_string())?;
    let mut found = Vec::new();
    if let Some(conns) = doc.get("connections").and_then(|v| v.as_array()) {
        for c in conns {
            let host = c.pointer("/metadata/host").and_then(|v| v.as_str()).unwrap_or("");
            let chain = c.get("chains").and_then(|v| v.as_array()).and_then(|a| a.first()).and_then(|v| v.as_str()).unwrap_or("");
            if !chain.eq_ignore_ascii_case("DIRECT") || host.is_empty() { continue; }
            if host.ends_with(".ru") || host.ends_with(".su") { continue; }
            if let Some(root) = root_domain(host) { if !found.iter().any(|d: &String| d.eq_ignore_ascii_case(&root)) { found.push(root); } }
        }
    }
    if found.is_empty() { return Ok(json!({"ok": false, "message": "Подходящих доменов для сохранения маршрута не найдено."})); }
    let mut added = Vec::new();
    app.settings.patch(|s| {
        let mut set: Vec<String> = s.custom_proxy_rules.lines().map(|l| l.trim().to_string()).filter(|l| !l.is_empty()).collect();
        for d in &found {
            if !set.iter().any(|x| x.eq_ignore_ascii_case(d)) { set.push(d.clone()); added.push(d.clone()); }
        }
        set.sort();
        s.custom_proxy_rules = set.join("\n");
    })?;
    let _ = rules::ensure(&app.settings.current());
    if app.core.running() { let _ = app.core.restart(&app.settings.current()); }
    Ok(json!({"ok": true, "added": added.len(), "domains": added, "message": format!("Сохранено доменов: {}.", added.len())}))
}

fn learn_direct(app: &AppState) -> Result<Value, String> {
    if !app.core.running() { return Ok(json!({"ok": false, "message": "Ядро не запущено."})); }
    let s = app.settings.current();
    let client = app.core.clash(s.external_port);
    let res = client.get(format!("http://127.0.0.1:{}/connections", s.external_port)).header("Authorization", format!("Bearer {}", app.core.secret)).send().map_err(|e| e.to_string())?;
    let doc: Value = res.json().map_err(|e| e.to_string())?;
    let mut found = Vec::new();
    if let Some(conns) = doc.get("connections").and_then(|v| v.as_array()) {
        for c in conns {
            let host = c.pointer("/metadata/host").and_then(|v| v.as_str()).unwrap_or("");
            let chain = c.get("chains").and_then(|v| v.as_array()).and_then(|a| a.first()).and_then(|v| v.as_str()).unwrap_or("");
            if !chain.eq_ignore_ascii_case("DIRECT") { continue; }
            if !(host.ends_with(".ru") || host.ends_with(".su")) { continue; }
            if let Some(root) = root_domain(host) { if !found.iter().any(|d: &String| d.eq_ignore_ascii_case(&root)) { found.push(root); } }
        }
    }
    if found.is_empty() { return Ok(json!({"ok": false, "message": "Нет подходящих DIRECT-доменов.", "domains": []})); }
    let mut added = Vec::new();
    app.settings.patch(|s| {
        for d in &found {
            if !s.smart_direct_domains.iter().any(|x| x.eq_ignore_ascii_case(d)) {
                s.smart_direct_domains.push(d.clone());
                added.push(d.clone());
            }
        }
        if !added.is_empty() {
            let mut set: Vec<String> = s.custom_direct_rules.lines().map(|l| l.trim().to_string()).filter(|l| !l.is_empty()).collect();
            for d in &added { if !set.iter().any(|x| x.eq_ignore_ascii_case(d)) { set.push(d.clone()); } }
            set.sort();
            s.custom_direct_rules = set.join("\n");
        }
    })?;
    let _ = rules::ensure(&app.settings.current());
    if app.core.running() && !added.is_empty() { let _ = app.core.restart(&app.settings.current()); }
    Ok(json!({"ok": true, "message": format!("Добавлено в Smart Direct: {}", added.len()), "domains": added}))
}

fn leak(app: &AppState) -> Result<Value, String> {
    let s = app.settings.current();
    let mut exit_ip = None;
    if app.core.running() {
        let proxy = reqwest::Proxy::http(format!("http://127.0.0.1:{}", s.mixed_port)).ok();
        if let Some(proxy) = proxy {
            if let Ok(client) = reqwest::blocking::Client::builder().proxy(proxy).timeout(std::time::Duration::from_secs(12)).build() {
                exit_ip = client.get("https://api.ipify.org").send().ok().and_then(|r| r.text().ok()).map(|t| t.trim().to_string());
            }
        }
    }
    Ok(json!({
        "exitIp": exit_ip,
        "realIp": Value::Null,
        "masked": exit_ip.is_some(),
        "score": if app.core.running() { 80 } else { 40 },
        "message": if exit_ip.is_some() { "Адрес выхода получен." } else { "Не удалось определить адрес выхода." },
        "checks": []
    }))
}

fn panic(app: &AppState) -> Result<Value, String> {
    app.settings.patch(|s| { s.hide_from_capture = true; s.close_to_tray = true; s.system_proxy = false; })?;
    app.core.stop();
    let _ = std::fs::write(crate::paths::core_log(), "");
    let _ = std::fs::remove_file(crate::paths::runtime_config());
    send(app, "tray");
    Ok(json!({"ok": true, "message": "Подключение остановлено. Системный прокси сброшен. Окно свёрнуто в область уведомлений."}))
}

fn test_delays(app: &AppState, url: &str, timeout: i32) -> Value {
    let s = app.settings.current();
    let mut map = serde_json::Map::new();
    for group in ["RUSH", "AUTO", "PROXY"] {
        if let Some((name, delay)) = app.core.best_delay(s.external_port, group, url, timeout) {
            map.insert(name, json!(delay));
        }
    }
    Value::Object(map)
}

fn check_update() -> Value {
    let client = reqwest::blocking::Client::builder().timeout(std::time::Duration::from_secs(20)).build();
    let Ok(client) = client else { return json!({"current": APP_VERSION, "updateAvailable": false, "message": "Не удалось проверить обновления."}); };
    let Ok(res) = client.get("https://api.github.com/repos/78rayy192/jester-proxy-client/releases/latest").header("User-Agent", format!("Jester/{APP_VERSION}")).header("Accept", "application/vnd.github+json").send() else {
        return json!({"current": APP_VERSION, "updateAvailable": false, "message": "Не удалось проверить обновления."});
    };
    if !res.status().is_success() { return json!({"current": APP_VERSION, "updateAvailable": false, "message": "Не удалось проверить обновления."}); }
    let Ok(doc) = res.json::<Value>() else { return json!({"current": APP_VERSION, "updateAvailable": false, "message": "Не удалось проверить обновления."}); };
    let tag = doc.get("tag_name").and_then(|v| v.as_str()).unwrap_or("").trim_start_matches('v').to_string();
    let url = doc.get("html_url").and_then(|v| v.as_str()).unwrap_or("");
    let newer = tag.split('.').filter_map(|p| p.parse::<u32>().ok()).gt(APP_VERSION.split('.').filter_map(|p| p.parse::<u32>().ok()));
    json!({"current": APP_VERSION, "latest": tag, "updateAvailable": newer, "releaseUrl": url, "message": if newer { format!("Доступна версия {tag}.") } else { "Установлена актуальная версия.".into() }})
}

fn root_domain(host: &str) -> Option<String> {
    let host = host.trim().trim_end_matches('.').to_lowercase();
    if host.parse::<std::net::IpAddr>().is_ok() { return None; }
    let parts: Vec<&str> = host.split('.').filter(|p| !p.is_empty()).collect();
    if parts.len() < 2 { return None; }
    Some(format!("{}.{}", parts[parts.len() - 2], parts[parts.len() - 1]))
}

fn query_map(q: &str) -> std::collections::HashMap<String, String> {
    url::form_urlencoded::parse(q.as_bytes()).map(|(k, v)| (k.into_owned(), v.into_owned())).collect()
}

fn send(app: &AppState, cmd: &str) {
    if let Some(tx) = app.win.lock().expect("win").as_ref() { let _ = tx.send(cmd.to_string()); }
}

fn elevate() -> bool {
    let Ok(exe) = std::env::current_exe() else { return false };
    #[cfg(windows)]
    unsafe {
        use std::os::windows::ffi::OsStrExt;
        use windows::core::PCWSTR;
        use windows::Win32::UI::Shell::ShellExecuteW;
        use windows::Win32::UI::WindowsAndMessaging::SW_SHOWNORMAL;
        let wide: Vec<u16> = exe.as_os_str().encode_wide().chain(std::iter::once(0)).collect();
        let verb: Vec<u16> = "runas".encode_utf16().chain(std::iter::once(0)).collect();
        let params: Vec<u16> = "--elevated".encode_utf16().chain(std::iter::once(0)).collect();
        let dir = exe.parent().unwrap_or(std::path::Path::new("."));
        let dir_w: Vec<u16> = dir.as_os_str().encode_wide().chain(std::iter::once(0)).collect();
        let code = ShellExecuteW(
            None,
            PCWSTR(verb.as_ptr()),
            PCWSTR(wide.as_ptr()),
            PCWSTR(params.as_ptr()),
            PCWSTR(dir_w.as_ptr()),
            SW_SHOWNORMAL,
        );
        return code.0 as isize > 32;
    }
    #[cfg(not(windows))]
    { false }
}

async fn clash(State(app): State<Arc<AppState>>, req: Request<Body>) -> Response {
    let settings = app.settings.current();
    let path = req.uri().path().trim_start_matches("/clash");
    let path = if path.is_empty() { "/" } else { path };
    let qs = req.uri().query().map(|q| format!("?{q}")).unwrap_or_default();
    let url = format!("http://127.0.0.1:{}{path}{qs}", settings.external_port);
    let method = req.method().clone();
    let content_type = req.headers().get(header::CONTENT_TYPE).cloned();
    let bytes = axum::body::to_bytes(req.into_body(), 2 * 1024 * 1024).await.unwrap_or_default();
    let secret = app.core.secret.clone();
    let client = match reqwest::Client::builder().build() {
        Ok(c) => c,
        Err(_) => return Response::builder().status(502).body(Body::from("Ядро не запущено.")).unwrap(),
    };
    let mut builder = client.request(method, url).header("Authorization", format!("Bearer {secret}"));
    if let Some(ct) = content_type { builder = builder.header(header::CONTENT_TYPE, ct); }
    if !bytes.is_empty() { builder = builder.body(bytes); }
    match builder.send().await {
        Ok(res) => {
            let status = res.status();
            let ctype = res.headers().get(header::CONTENT_TYPE).cloned();
            let stream = res.bytes_stream();
            let mut response = Response::builder().status(status.as_u16());
            if let Some(ct) = ctype {
                if let Ok(v) = HeaderValue::from_bytes(ct.as_bytes()) { response = response.header(header::CONTENT_TYPE, v); }
            }
            response.body(Body::from_stream(stream)).unwrap_or_else(|_| Response::builder().status(502).body(Body::from("bad gateway")).unwrap())
        }
        Err(_) => Response::builder().status(502).body(Body::from("Ядро не запущено.")).unwrap(),
    }
}

fn json_ok(v: Value) -> Response {
    Response::builder().status(StatusCode::OK).header(header::CONTENT_TYPE, "application/json").body(Body::from(v.to_string())).unwrap()
}

fn json_err(e: String) -> Response {
    let code = if e == "not found" { StatusCode::NOT_FOUND } else { StatusCode::BAD_REQUEST };
    Response::builder().status(code).header(header::CONTENT_TYPE, "application/json").body(Body::from(json!({"error": e}).to_string())).unwrap()
}

fn message(text: &str) {
    #[cfg(windows)]
    unsafe {
        use std::os::windows::ffi::OsStrExt;
        use windows::core::PCWSTR;
        use windows::Win32::UI::WindowsAndMessaging::{MessageBoxW, MB_ICONERROR, MB_OK};
        let wide: Vec<u16> = std::ffi::OsStr::new(text).encode_wide().chain(std::iter::once(0)).collect();
        let title: Vec<u16> = "Jester".encode_utf16().chain(std::iter::once(0)).collect();
        let _ = MessageBoxW(None, PCWSTR(wide.as_ptr()), PCWSTR(title.as_ptr()), MB_OK | MB_ICONERROR);
    }
    #[cfg(not(windows))]
    eprintln!("{text}");
}

pub fn guardian(app: Arc<AppState>) {
    std::thread::spawn(move || {
        let mut was_running = false;
        let mut lane_at = std::time::Instant::now() - std::time::Duration::from_secs(3600);
        let mut exit_at = std::time::Instant::now() - std::time::Duration::from_secs(3600);
        loop {
            std::thread::sleep(std::time::Duration::from_secs(5));
            let running = app.core.running();
            if running && !was_running && app.settings.current().warm_start { warm(&app); }
            was_running = running;
            if !running { continue; }
            let mut restart = false;
            let s = app.settings.current();
            if s.auto_update_subscriptions && hours_since(&s.last_subscription_update_at) >= s.auto_update_interval_hours as i64 {
                if s.last_subscription_update_at.is_none() {
                    let _ = app.settings.patch(|x| x.last_subscription_update_at = Some(chrono::Utc::now().to_rfc3339()));
                } else {
                    let (updated, _, _) = profiles::update_all();
                    let _ = app.settings.patch(|x| x.last_subscription_update_at = Some(chrono::Utc::now().to_rfc3339()));
                    if updated > 0 { restart = true; }
                }
            }
            let s = app.settings.current();
            if s.dns_diversity && hours_since(&s.last_dns_rotate_at) >= s.dns_rotate_hours as i64 {
                if s.last_dns_rotate_at.is_none() {
                    let _ = app.settings.patch(|x| x.last_dns_rotate_at = Some(chrono::Utc::now().to_rfc3339()));
                } else {
                    let _ = app.settings.patch(|x| persona::rotate(x, true));
                    restart = true;
                }
            }
            if restart && app.core.running() {
                let _ = app.core.restart(&app.settings.current());
                continue;
            }
            let s = app.settings.current();
            let lane_every = s.adaptive_lane_interval_sec.clamp(15, 3600) as u64;
            if lane_at.elapsed() >= std::time::Duration::from_secs(lane_every) && (s.adaptive_lane || s.latency_budget_enabled) {
                lane_at = std::time::Instant::now();
                let timeout = s.latency_timeout_ms.clamp(2000, 8000);
                let rush = if s.adaptive_lane && s.rf_rush_mode { app.core.best_delay(s.external_port, "RUSH", &s.latency_url, timeout).map(|(_, d)| d).unwrap_or(-1) } else { -1 };
                let auto = app.core.best_delay(s.external_port, "AUTO", &s.latency_url, timeout).map(|(_, d)| d).unwrap_or(-1);
                let measured = if rush > 0 { rush } else { auto };
                if s.adaptive_lane && s.rf_rush_mode {
                    let pick = if rush > 0 && rush <= s.adaptive_lane_bad_rtt_ms { "RUSH" } else { "AUTO" };
                    let _ = app.settings.patch(|x| { if measured > 0 { x.last_measured_rtt_ms = measured; } x.adaptive_lane_state = pick.into(); });
                    app.core.select(s.external_port, "PROXY", pick);
                }
                if s.latency_budget_enabled && measured > s.latency_budget_ms {
                    if s.latency_budget_action == "warn" {
                        let _ = app.settings.patch(|x| x.guardian_message = format!("Задержка {measured} мс выше порога {} мс.", s.latency_budget_ms));
                    } else if let Some((name, delay)) = app.core.best_delay(s.external_port, "AUTO", &s.latency_url, timeout) {
                        app.core.select(s.external_port, "AUTO", &name);
                        app.core.select(s.external_port, "PROXY", &name);
                        let _ = app.settings.patch(|x| {
                            x.last_measured_rtt_ms = delay;
                            x.guardian_message = format!("Задержка выше порога. Выбран {name}, {delay} мс.");
                        });
                    }
                }
            }
            let s = app.settings.current();
            let exit_every = s.exit_check_interval_sec.clamp(30, 3600) as u64;
            if s.exit_consistency_check && exit_at.elapsed() >= std::time::Duration::from_secs(exit_every) {
                exit_at = std::time::Instant::now();
                if let Some(ip) = probe_exit(&s) {
                    let prev = s.last_known_exit_ip.clone();
                    let _ = app.settings.patch(|x| x.last_known_exit_ip = Some(ip.clone()));
                    if let Some(prev) = prev.filter(|p| p != &ip) {
                        match s.exit_check_action.as_str() {
                            "warn" => { let _ = app.settings.patch(|x| x.guardian_message = format!("Адрес выхода сменился: {prev} → {ip}")); }
                            "panic" => { let _ = panic(&app); }
                            _ => {
                                if let Some((name, _)) = app.core.best_delay(s.external_port, "AUTO", &s.latency_url, s.latency_timeout_ms.clamp(2000, 8000)) {
                                    app.core.select(s.external_port, "PROXY", &name);
                                }
                                let _ = app.settings.patch(|x| x.guardian_message = format!("Адрес выхода сменился на {ip}. Узел переключён."));
                            }
                        }
                    }
                }
            }
        }
    });
}

fn hours_since(stamp: &Option<String>) -> i64 {
    match stamp.as_deref().and_then(|t| chrono::DateTime::parse_from_rfc3339(t).ok()) {
        Some(t) => chrono::Utc::now().signed_duration_since(t.with_timezone(&chrono::Utc)).num_hours(),
        None => i64::MAX,
    }
}

fn probe_exit(settings: &crate::models::AppSettings) -> Option<String> {
    let proxy = reqwest::Proxy::http(format!("http://127.0.0.1:{}", settings.mixed_port)).ok()?;
    let client = reqwest::blocking::Client::builder().proxy(proxy).timeout(std::time::Duration::from_secs(8)).build().ok()?;
    let text = client.get("https://api.ipify.org").send().ok()?.text().ok()?;
    let ip = text.trim();
    if ip.is_empty() || ip.len() > 64 || !ip.chars().all(|c| c.is_ascii_hexdigit() || c == '.' || c == ':') { return None; }
    Some(ip.to_string())
}
