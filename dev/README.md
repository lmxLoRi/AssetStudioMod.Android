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

The win is not the language: compiling the *same* C zlib through Rust's flate2 zlib backend
measures the same as calling it from C. It is `fdeflate`, which the `png` crate uses for
`Compression::Fast` (png/src/encoder.rs, "Compression::Fast => fdeflate::Compressor::new").
fdeflate is a deflate implementation written specifically for fast PNG encoding, and there is
nothing like it in the current C path -- zlib level 1 is a general-purpose deflate.

Note also that `brotli`, `zstd` and `lz4` are all available as Rust crates with C-comparable
performance, so there is no penalty for the boundary; the crate is the point.

Caveats: desktop x86_64, and the two smaller images favour fdeflate on size while the 2048x2048
is 7% larger. The next step is cross-compiling the encoder for aarch64-linux-android and calling
it from the app, which means `rustup target add aarch64-linux-android` and the NDK as linker.

Run:

    cd rust-png-bench && cargo build --release
    ./target/release/rust-png-bench dump <png...>       # writes <png>.rgba + .dims
    ./target/release/rust-png-bench bench <png...>
    cd ../c-png-bench && gcc -O3 -o harness harness.c ../../PngNative/png_write.c -lz
    ./harness <png...>
