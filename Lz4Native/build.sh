#!/usr/bin/env bash
# Rebuilds libLz4Native.so for the ABIs the app ships.
#
# Why this exists at all: K4os.Compression.LZ4, which AssetStudio uses on every platform, has no
# SIMD path for ARM64 and falls back to scalar managed code. Measured on a moto g200 it decompresses
# the 4.1 GB cache's 11.31 GB of blocks at ~207 MB/s; LZ4 should be several times that.
#
# -z max-page-size=16384 is not optional on Android 16: the platform is moving to 16 KB pages and a
# 4 KB-aligned .so will not load there. (libTexture2DDecoderNative.so still has this problem.)
#
# Usage: LZ4=/path/to/lz4/repo ./build.sh   (lz4.c/lz4.h are vendored next to this script)
set -euo pipefail

NDK=${ANDROID_NDK_HOME:-/tmp/opencode/ndk/android-ndk-r27c}
BIN="$NDK/toolchains/llvm/prebuilt/linux-x86_64/bin"
API=26   # matches SupportedOSPlatformVersion
HERE="$(cd "$(dirname "$0")" && pwd)"
OUT="$HERE/../AssetStudio.Android/jniLibs"

[ -x "$BIN/aarch64-linux-android${API}-clang" ] || { echo "no NDK at $NDK" >&2; exit 1; }

for pair in "aarch64-linux-android:arm64-v8a" "x86_64-linux-android:x86_64"; do
    triple=${pair%%:*}
    abi=${pair##*:}
    mkdir -p "$OUT/$abi"
    "$BIN/${triple}${API}-clang" -O3 -fPIC -shared -DNDEBUG \
        -Wl,-z,max-page-size=16384 -Wl,-z,common-page-size=16384 \
        -o "$OUT/$abi/libLz4Native.so" "$HERE/lz4.c"
    echo "built $OUT/$abi/libLz4Native.so"
done
