use crate::models::AppSettings;
use crate::paths;
use jni::objects::{GlobalRef, JValue};
use jni::JavaVM;
use std::sync::atomic::{AtomicI32, Ordering};
use std::sync::OnceLock;

static BRIDGE: OnceLock<Bridge> = OnceLock::new();
static TUN_FD: AtomicI32 = AtomicI32::new(-1);

struct Bridge {
    vm: JavaVM,
    class: GlobalRef,
}

pub fn install(vm: *mut jni::sys::JavaVM) -> Result<(), String> {
    let vm = unsafe { JavaVM::from_raw(vm) }.map_err(|e| e.to_string())?;
    let mut env = vm.get_env().map_err(|e| e.to_string())?;
    let class = env.find_class("com/jester/client/JesterVpn").map_err(|e| e.to_string())?;
    let class = env.new_global_ref(class).map_err(|e| e.to_string())?;
    BRIDGE.set(Bridge { vm, class }).map_err(|_| "Мост VPN уже создан.".to_string())
}

pub fn tun_fd() -> i32 {
    TUN_FD.load(Ordering::SeqCst)
}

pub fn prepare(settings: &AppSettings) -> Result<(), String> {
    let previous = TUN_FD.swap(-1, Ordering::SeqCst);
    if previous >= 0 {
        unsafe { libc::close(previous); }
    }
    paths::ensure();
    let (address, prefix) = split_cidr(&settings.inet4_address);
    let mtu = if settings.mtu > 0 { settings.mtu.clamp(1280, 9000) } else { 1400 };
    let spec = serde_json::json!({
        "address": address,
        "prefix": prefix,
        "dns": "172.19.0.2",
        "mtu": mtu,
    });
    std::fs::write(paths::run().join("vpn.json"), spec.to_string()).map_err(|e| e.to_string())?;
    let raw = call_int("establish")?;
    if raw == -2 {
        return Err("Разрешение VPN не выдано.".into());
    }
    if raw < 0 {
        return Err("Не удалось создать VPN.".into());
    }
    let fd = inherit_fd(raw);
    let previous = TUN_FD.swap(fd, Ordering::SeqCst);
    if previous >= 0 && previous != fd {
        unsafe { libc::close(previous); }
    }
    Ok(())
}

pub fn release() {
    let fd = TUN_FD.swap(-1, Ordering::SeqCst);
    if fd >= 0 {
        unsafe { libc::close(fd); }
    }
    let _ = call_void("stop");
}

fn inherit_fd(fd: i32) -> i32 {
    unsafe {
        let dup = libc::fcntl(fd, libc::F_DUPFD, 100);
        if dup >= 0 {
            libc::close(fd);
            return dup;
        }
        let flags = libc::fcntl(fd, libc::F_GETFD);
        if flags >= 0 {
            libc::fcntl(fd, libc::F_SETFD, flags & !libc::FD_CLOEXEC);
        }
    }
    fd
}

fn split_cidr(value: &str) -> (String, i32) {
    let value = value.trim();
    let value = if value.is_empty() { "172.19.0.1/30" } else { value };
    let mut parts = value.split('/');
    let address = parts.next().unwrap_or("172.19.0.1").trim();
    let address = if address.is_empty() { "172.19.0.1" } else { address };
    let prefix = parts.next().and_then(|p| p.trim().parse::<i32>().ok()).unwrap_or(30).clamp(1, 32);
    (address.to_string(), prefix)
}

fn call_int(name: &str) -> Result<i32, String> {
    let bridge = BRIDGE.get().ok_or("Служба VPN не готова.")?;
    let mut env = bridge.vm.attach_current_thread().map_err(|e| e.to_string())?;
    let value = env.call_static_method(&bridge.class, name, "()I", &[] as &[JValue]).map_err(|e| e.to_string())?;
    value.i().map_err(|e| e.to_string())
}

fn call_void(name: &str) -> Result<(), String> {
    let bridge = BRIDGE.get().ok_or("Служба VPN не готова.")?;
    let mut env = bridge.vm.attach_current_thread().map_err(|e| e.to_string())?;
    env.call_static_method(&bridge.class, name, "()V", &[] as &[JValue]).map_err(|e| e.to_string())?;
    Ok(())
}
