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

RUSTFLAGS="-C link-arg=-Wl,-z,max-page-size=16384 -C link-arg=-Wl,-z,common-page-size=16384"

for pair in "aarch64-linux-android:arm64-v8a" "x86_64-linux-android:x86_64"; do
    triple=${pair%%:*}; abi=${pair##*:}
    env "CARGO_TARGET_$(echo "$triple" | tr 'a-z-' 'A-Z_')_LINKER=$BIN/${triple}${API}-clang" \
        RUSTFLAGS="$RUSTFLAGS" \
        cargo build --release --manifest-path "$HERE/Cargo.toml" --target "$triple"
    mkdir -p "$OUT/$abi"
    cp "$HERE/target/$triple/release/libRustPng.so" "$OUT/$abi/"
    echo "built $OUT/$abi/libRustPng.so"
done
