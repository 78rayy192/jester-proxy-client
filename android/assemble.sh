#!/bin/bash
set -euo pipefail
root="$(cd "$(dirname "$0")/.." && pwd)"
export ANDROID_HOME="${ANDROID_HOME:-$HOME/Android/Sdk}"
ndk_dir="$(find "$ANDROID_HOME/ndk" -mindepth 1 -maxdepth 1 -type d | sort | tail -n 1)"
export ANDROID_NDK_HOME="$ndk_dir"
export PATH="$ANDROID_HOME/cmdline-tools/latest/bin:$ANDROID_HOME/platform-tools:$PATH"

if [ ! -f "$root/android/gradle/wrapper/gradle-wrapper.jar" ]; then
  curl -fsSL -o "$root/android/gradle/wrapper/gradle-wrapper.jar" \
    https://github.com/gradle/gradle/raw/v8.11.1/gradle/wrapper/gradle-wrapper.jar
fi
chmod +x "$root/android/gradlew"

if [ ! -f "$root/android/app/src/main/res/drawable/jester_head.png" ]; then
  mkdir -p "$root/android/app/src/main/res/drawable"
  cp "$root/ui/public/jester-head.png" "$root/android/app/src/main/res/drawable/jester_head.png"
fi

echo "sdk.dir=$ANDROID_HOME" > "$root/android/local.properties"

cd "$root/ui"
if [ -f package-lock.json ]; then npm ci; else npm install; fi
npm run build
rm -rf "$root/android/app/src/main/assets/ui"
mkdir -p "$root/android/app/src/main/assets/ui"
cp -a "$root/ui/dist/." "$root/android/app/src/main/assets/ui/"

stage="$root/android/.mihomo"
rm -rf "$stage"
python3 - "$stage" <<'PY'
import gzip, json, sys, urllib.request
from pathlib import Path
out = Path(sys.argv[1])
abis = {
    "arm64-v8a": "arm64",
    "armeabi-v7a": "armv7",
    "x86_64": "amd64",
}
req = urllib.request.Request(
    "https://api.github.com/repos/MetaCubeX/mihomo/releases/latest",
    headers={"User-Agent": "JesterProxyClient/1.2", "Accept": "application/vnd.github+json"},
)
with urllib.request.urlopen(req, timeout=40) as res:
    doc = json.load(res)
assets = doc.get("assets") or []

def pick(arch: str) -> str:
    # arm64 ships as mihomo-android-arm64-v8-vX.Y.Z.gz (v8 is ARMv8, not a channel).
    best = None
    for asset in assets:
        name = (asset.get("name") or "").lower()
        url = asset.get("browser_download_url") or ""
        if not name.endswith(".gz") or name.endswith(".tar.gz"):
            continue
        if "-go12" in name or "-compatible-" in name or "-v3-" in name:
            continue
        if arch == "arm64":
            marker = "mihomo-android-arm64-v8-v"
        else:
            marker = f"mihomo-android-{arch}-v"
        if not name.startswith(marker):
            continue
        rest = name[len(marker):]
        if rest[:1].isdigit() and "." in rest:
            best = url
    if not best:
        raise SystemExit(f"no mihomo android build for {arch}")
    return best

for abi, arch in abis.items():
    dest = out / abi
    dest.mkdir(parents=True, exist_ok=True)
    target = dest / "libmihomo.so"
    url = pick(arch)
    print(f"mihomo {abi}: {url}")
    blob = urllib.request.urlopen(url, timeout=180).read()
    target.write_bytes(gzip.decompress(blob))
PY

ndk_bin="$ANDROID_NDK_HOME/toolchains/llvm/prebuilt/linux-x86_64/bin"
export PATH="$ndk_bin:$PATH"
export CARGO_TARGET_DIR="$root/rust/jester-host/target"
export TMPDIR="${TMPDIR:-$HOME/tmp}"
mkdir -p "$TMPDIR"

link_android() {
  local rust_target="$1" clang="$2"
  local key="${rust_target//-/_}"
  export "CC_${key}=$clang"
  export "CXX_${key}=${clang}++"
  export "AR_${key}=llvm-ar"
  local upper="${key^^}"
  export "CARGO_TARGET_${upper}_LINKER=$clang"
}

link_android aarch64-linux-android aarch64-linux-android26-clang
link_android armv7-linux-androideabi armv7a-linux-androideabi26-clang
link_android x86_64-linux-android x86_64-linux-android26-clang

cd "$root/rust/jester-host"
cargo build --release --lib --no-default-features --target aarch64-linux-android
cargo build --release --lib --no-default-features --target armv7-linux-androideabi
cargo build --release --lib --no-default-features --target x86_64-linux-android

mkdir -p \
  "$root/android/app/src/main/jniLibs/arm64-v8a" \
  "$root/android/app/src/main/jniLibs/armeabi-v7a" \
  "$root/android/app/src/main/jniLibs/x86_64"
cp -f "$CARGO_TARGET_DIR/aarch64-linux-android/release/libjester.so" "$root/android/app/src/main/jniLibs/arm64-v8a/libjester.so"
cp -f "$CARGO_TARGET_DIR/armv7-linux-androideabi/release/libjester.so" "$root/android/app/src/main/jniLibs/armeabi-v7a/libjester.so"
cp -f "$CARGO_TARGET_DIR/x86_64-linux-android/release/libjester.so" "$root/android/app/src/main/jniLibs/x86_64/libjester.so"
for abi in arm64-v8a armeabi-v7a x86_64; do
  cp -f "$stage/$abi/libmihomo.so" "$root/android/app/src/main/jniLibs/$abi/libmihomo.so"
done

cd "$root/android"
./gradlew assembleRelease
mkdir -p "$root/release"
cp -f "$root/android/app/build/outputs/apk/release/app-release.apk" "$root/release/Jester-android.apk"
echo "APK $root/release/Jester-android.apk"
