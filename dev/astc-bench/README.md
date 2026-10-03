# astc-bench

Compares the ASTC decoder the app ships (`Texture2DDecoderNative/astc.cpp`) against
`arm-software/astc-encoder` (astcenc) and against the Rust `texture2ddecoder` crate, on identical
input, on the phone, with the same compiler.

## The input has to be the game's own data

Two wrong measurements happened here before the right one.

The first fed random bytes in. Random bytes are not valid ASTC: the Rust port panics and the C++
one segfaults, and the 21 MPix/s it reported first describes a path no real encoder emits.

The second used the Rust crate's own test vector, `ASTC_4x4.ktx2` -- a 512x512 image of a cartoon
crab. It reported 121-195 MPix/s and made the phone's in-app 27 MPix/s look like a six-fold
discrepancy. It is not: the game's textures are 2048x1916 and up, and they decode at 46-50 MPix/s.
A small, flat test image is 2.5x easier to decode than a real one, and comparing the two says
nothing.

Real payloads come from the app itself. `Texture2DConverter.RawTextureDump` is a dev hook that
hands over the compressed bytes before decoding, armed from the app with:

    adb shell am start -n com.aelurum.assetstudiomod/...MainActivity \
        -e action export -e path <dir> -e kind Texture -e dumpastc 1

It writes `files/astc-dump/NN_ASTC_RGB_<bx>x<by>_<w>x<h>.bin`, whose names carry everything the
harnesses need. Copy them into the input naming the harnesses use:

    cd .../astc-dump && for f in *.bin; do
      #  02_ASTC_RGB_4x4_2048x1916.bin  ->  astc-input-2048-1916-4x4.bin
      ...
    done

## Results, phone (moto g200), same input, same compiler

    input                ours        astcenc NEON   Rust
    ferris 512x512       121.1       64.7           85.7
    game 2048x1916        47.7       38.9
    game 2048x1872        45.8       36.7
    game  943x2048        46.6       31.6
    game 1066x2048        50.2       34.0

All in MPix/s. Ours is 1.2-1.9x faster than astcenc's NEON path and than the Rust port, on the
data that matters. `astc.cpp` is scalar C and beats both SIMD implementations, so there is nothing
to gain by swapping it and something to lose.

Outputs agree: ours and the Rust port are byte-identical, and astcenc differs from both only by
R/B being swapped (ours is BGRA, astcenc RGBA) plus +/-1 rounding on 3.24% of pixels, which is two
implementations of the same weight interpolation rounding differently. Neither is wrong.

## Harnesses

    shipped_so_bench.cpp   dlopen()s the libTexture2DDecoderNative.so from the APK and times the
                           exported DecodeASTC directly, so the comparison is against the binary
                           that actually ships rather than against a fresh rebuild of the source.
                           It measured the same as a fresh -O3 build (163 vs 168 MPix/s), which
                           rules out the shipped library having been built badly.
    astcenc_bench.cpp      links libastcenc-neon-static.a built with the NDK for arm64.
    astc_bench.cpp         our astc.cpp compiled directly.

Build for the phone:

    NDK=/path/to/android-ndk-r27c
    CXX=$NDK/toolchains/llvm/prebuilt/linux-x86_64/bin/aarch64-linux-android26-clang++
    $CXX -O3 -std=c++17 -DINPUT_DIR='"/data/local/tmp"' \
         -o bench-astcenc-arm64 astcenc_bench.cpp -I<astcenc>/Source <astcenc>/build-arm64/Source/libastcenc-neon-static.a
    $CXX -O2 -std=c++17 -DINPUT_DIR='"/data/local/tmp"' -o bench-shipped-arm64 shipped_so_bench.cpp -ldl

Push with the input files and `libTexture2DDecoderNative.so`, then:

    adb shell "LD_LIBRARY_PATH=/data/local/tmp /data/local/tmp/bench-shipped-arm64"

`-static` does not work with the NDK here (`executable's TLS segment is underaligned`); the
binaries link libc dynamically and libc++_shared.so has to be pushed alongside.
