#!/usr/bin/env bash
# Builds libPngNative.so for the ABIs the app ships.
#
# Links the platform zlib (-lz), which is one of the few libraries the NDK guarantees. libdeflate
# was tried and removed: it measured 79% slower than zlib on this workload, see png_write.c.
set -euo pipefail

NDK=${ANDROID_NDK_HOME:-/tmp/opencode/ndk/android-ndk-r27c}
BIN="$NDK/toolchains/llvm/prebuilt/linux-x86_64/bin"
API=26
HERE="$(cd "$(dirname "$0")" && pwd)"
OUT="$HERE/../AssetStudio.Android/jniLibs"

[ -x "$BIN/aarch64-linux-android${API}-clang" ] || { echo "no NDK at $NDK" >&2; exit 1; }

for pair in "aarch64-linux-android:arm64-v8a" "x86_64-linux-android:x86_64"; do
    triple=${pair%%:*}; abi=${pair##*:}
    mkdir -p "$OUT/$abi"
    "$BIN/${triple}${API}-clang" -O3 -fPIC -shared -DNDEBUG \
        -Wl,-z,max-page-size=16384 -Wl,-z,common-page-size=16384 \
        -o "$OUT/$abi/libPngNative.so" "$HERE/png_write.c" -lz
    echo "built $OUT/$abi/libPngNative.so"
done
