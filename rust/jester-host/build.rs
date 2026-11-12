use std::env;
use std::fs;
use std::path::PathBuf;

fn main() {
    let manifest = PathBuf::from(env::var("CARGO_MANIFEST_DIR").unwrap());
    let src = manifest.join("assets").join("WebView2Loader.dll");
    println!("cargo:rerun-if-changed={}", src.display());
    if !src.exists() {
        return;
    }
    let target = env::var("CARGO_TARGET_DIR")
        .map(PathBuf::from)
        .unwrap_or_else(|_| manifest.join("target"));
    let profile = env::var("PROFILE").unwrap_or_else(|_| "debug".into());
    let dest_dir = target.join(profile);
    let _ = fs::create_dir_all(&dest_dir);
    let _ = fs::copy(&src, dest_dir.join("WebView2Loader.dll"));

    let icon = manifest.join("assets").join("jester.ico");
    println!("cargo:rerun-if-changed={}", icon.display());
    if icon.exists() {
        embed_icon(&icon);
    }
}

fn embed_icon(icon: &std::path::Path) {
    let out = PathBuf::from(r"C:\Users\PC-Admin\AppData\Local\Temp\jester-winres");
    let _ = fs::create_dir_all(&out);
    let icon_copy = out.join("jester.ico");
    fs::copy(icon, &icon_copy).expect("copy icon");
    let rc_path = out.join("resource.rc");
    let icon_rc = icon_copy.display().to_string().replace('\\', "\\\\");
    fs::write(
        &rc_path,
        format!(
            "1 ICON \"{icon_rc}\"\n\
1 VERSIONINFO\n\
FILEVERSION 1,0,0,0\n\
PRODUCTVERSION 1,0,0,0\n\
BEGIN\n\
  BLOCK \"StringFileInfo\"\n\
  BEGIN\n\
    BLOCK \"040904B0\"\n\
    BEGIN\n\
      VALUE \"FileDescription\", \"Jester\"\n\
      VALUE \"ProductName\", \"Jester\"\n\
      VALUE \"OriginalFilename\", \"jester.exe\"\n\
      VALUE \"FileVersion\", \"1.0.0\"\n\
    END\n\
  END\n\
  BLOCK \"VarFileInfo\"\n\
  BEGIN\n\
    VALUE \"Translation\", 0x409, 1200\n\
  END\n\
END\n"
        ),
    )
    .expect("write resource");
    let obj = out.join("resource.o");
    let windres = std::process::Command::new("windres")
        .arg(&rc_path)
        .arg(&obj)
        .status()
        .expect("windres");
    if !windres.success() {
        panic!("windres failed");
    }
    println!("cargo:rustc-link-arg-bins={}", obj.display());
}
