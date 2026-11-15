#![cfg_attr(not(target_os = "android"), allow(dead_code))]

#[cfg(target_os = "android")]
mod api;
#[cfg(target_os = "android")]
mod android;
#[cfg(target_os = "android")]
mod config;
#[cfg(target_os = "android")]
mod core;
#[cfg(target_os = "android")]
mod download;
#[cfg(target_os = "android")]
mod flags;
#[cfg(target_os = "android")]
mod logic;
#[cfg(target_os = "android")]
mod models;
#[cfg(target_os = "android")]
mod normalize;
#[cfg(target_os = "android")]
mod paths;
#[cfg(target_os = "android")]
mod persona;
#[cfg(target_os = "android")]
mod profiles;
#[cfg(target_os = "android")]
mod rules;
#[cfg(target_os = "android")]
mod runtime;
#[cfg(target_os = "android")]
mod settings;
#[cfg(target_os = "android")]
mod sysproxy;

#[cfg(target_os = "android")]
mod entry {
    use crate::api::AppState;
    use std::sync::{Arc, Mutex};

    pub fn start() {
        crate::paths::ensure();
        let settings = Arc::new(crate::settings::Store::load());
        let core = Arc::new(crate::core::Core::new());
        let runtime = Arc::new(crate::runtime::Runtime::new(&settings.current()));
        let app = Arc::new(AppState {
            settings: settings.clone(),
            core: core.clone(),
            runtime: runtime.clone(),
            win: Mutex::new(None),
        });
        crate::core::watch_exit(core, settings, runtime);
        crate::api::guardian(app.clone());
        let rt = match tokio::runtime::Builder::new_multi_thread().enable_all().build() {
            Ok(rt) => rt,
            Err(err) => {
                eprintln!("jester: {err}");
                return;
            }
        };
        rt.block_on(crate::api::serve(app));
    }
}

#[cfg(target_os = "android")]
#[no_mangle]
pub extern "system" fn JNI_OnLoad(vm: *mut jni::sys::JavaVM, _reserved: *mut std::ffi::c_void) -> jni::sys::jint {
    match android::install(vm) {
        Ok(()) => {
            std::thread::Builder::new()
                .name("jester".into())
                .spawn(|| {
                    if let Err(err) = std::panic::catch_unwind(entry::start) {
                        eprintln!("jester panic: {err:?}");
                    }
                })
                .ok();
            jni::sys::JNI_VERSION_1_6
        }
        Err(err) => {
            eprintln!("jester: {err}");
            jni::sys::JNI_ERR
        }
    }
}

#[cfg(not(target_os = "android"))]
#[no_mangle]
pub extern "C" fn jester_desktop_stub() {}
