#!/usr/bin/env bash
# Cross-compiles the Rust PNG encoder for the ABIs the app ships.
#
# Needs: rustup target add aarch64-linux-android, and the NDK as the linker. The page-size link
# args matter: without them the .so is 4 KB aligned and will not load on Android 16's 16 KB pages.
set -euo pipefail

NDK=${ANDROID_NDK_HOME:-/tmp/opencode/ndk/android-ndk-r27c}
BIN="$NDK/toolchains/llvm/prebuilt/linux-x86_64/bin"
API=26
HERE="$(cd "$(dirname "$0")" && pwd)"
OUT="$HERE/../../AssetStudio.Android/jniLibs"

[ -x "$BIN/aarch64-linux-android${API}-clang" ] || { echo "no NDK at $NDK" >&2; exit 1; }

CARGO_TARGET_AARCH64_LINUX_ANDROID_LINKER="$BIN/aarch64-linux-android${API}-clang" \
RUSTFLAGS="-C link-arg=-Wl,-z,max-page-size=16384 -C link-arg=-Wl,-z,common-page-size=16384" \
    cargo build --release --manifest-path "$HERE/Cargo.toml" --target aarch64-linux-android

mkdir -p "$OUT/arm64-v8a"
cp "$HERE/target/aarch64-linux-android/release/libRustPng.so" "$OUT/arm64-v8a/"
echo "built $OUT/arm64-v8a/libRustPng.so"
