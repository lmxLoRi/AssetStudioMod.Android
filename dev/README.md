# dev

Experiments, not shipped code.

## rust-png-bench / c-png-bench

Does Rust encode PNG faster than the C path the app ships? Yes, about 6.5x, and not for
the reason the question implied.

Both harnesses encode the *same* raw pixels (the `.rgba` dumps made by
`rust-png-bench dump`) with Paeth filtering, and the comparison is interleaved and repeated
to keep machine state out of it:

    image        C zlib (shipping)        Rust png crate
    860x730       63 ms   859 KB          10.2 ms   779 KB    6.2x
    1080x800      78 ms  1015 KB          10.3 ms   973 KB    7.6x
    2048x2048    433 ms  5801 KB          63.0 ms  6220 KB    6.9x

What is demonstrated is that the *deflate implementation* is the difference, not the language:
`fdeflate` is what the `png` crate uses for `Compression::Fast`
(png/src/encoder.rs, "Compression::Fast => fdeflate::Compressor::new"), and it is a deflate
written specifically for fast PNG encoding. zlib level 1 is a general-purpose deflate.

The `czlib` feature here does **not** test "Rust calling C zlib", because the Fast path is
hardcoded to fdeflate and never consults flate2: it only changes the non-Fast path. So there is
no measurement here of what Rust plus a general C deflate would cost, and none is claimed.

Whether a general C deflate can match fdeflate at this level is open. libdeflate level 1 was
measured slower than zlib *on the phone* in the shipping app; zlib-ng was not tried.

Note also that `brotli`, `zstd` and `lz4` are all available as Rust crates with C-comparable
performance, so there is no penalty for the boundary; the crate is the point.

Caveats: desktop x86_64, not the phone. The two smaller images also get *smaller* from fdeflate
while the 2048x2048 grows 7%. One C reading of 33 ms was machine noise; the stable value is 63 ms
and that is what is quoted.

The next step is cross-compiling the encoder for aarch64-linux-android and calling it from the app
the way libLz4Native is called, which means `rustup target add aarch64-linux-android` plus the NDK
as linker. Until that is measured on the phone, 6.5x here is a promise about a desktop.

Run:

    cd rust-png-bench && cargo build --release
    ./target/release/rust-png-bench dump <png...>       # writes <png>.rgba + .dims
    ./target/release/rust-png-bench bench <png...>
    cd ../c-png-bench && gcc -O3 -o harness harness.c ../../PngNative/png_write.c -lz
    ./harness <png...>
