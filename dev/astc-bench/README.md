# astc-bench

Compares the ASTC decoder the app ships (`Texture2DDecoderNative/astc.cpp`) against the Rust
`texture2ddecoder` crate, on identical input.

## The input has to be real ASTC data

The first version of this fed random bytes in. That is not valid input: the Rust port panics
(`bitreader.rs`, index out of range) and the C++ one segfaults, and before it did, it reported
21 MPix/s -- a number that says nothing about decoding a real texture, because the blocks that
survive take a path a real encoder never emits. Real payloads come out six times faster.

Real payloads are taken from the Rust crate's own test vectors:

    python3 - <<'PY'
    import struct, os
    for name in ["ASTC_4x4", "ASTC_6x6", "ASTC_8x8"]:
        d = open(f"/tmp/t2d-rs/resources/tests/textures/{name}.ktx2", "rb").read()
        w, h = struct.unpack_from("<2I", d, 20)
        off, ln, _ = struct.unpack_from("<3Q", d, 80)
        open(f"/tmp/astc-input-{w}-{h}-{name.split('_')[1]}.bin", "wb").write(d[off:off+ln])
    PY

Both harnesses then read the same files, which matters because ASTC decode time depends on which
block modes the data happens to use.

## Results, desktop x86_64, 512x512 real textures

    C++ astc.cpp              Rust texture2ddecoder
    ASTC 4x4   129.4 MPix/s   85.7 MPix/s     C++ 1.51x faster
    ASTC 6x6   143.6 MPix/s  112.7 MPix/s     C++ 1.27x faster
    ASTC 8x8   148.0 MPix/s  115.0 MPix/s     C++ 1.29x faster
    checksums: identical for all three

So the crate is a faithful port -- byte-identical output -- that is 1.3 to 1.5x slower. It is not
an upgrade, and neither is `latias94/unity-asset`, whose decode crate depends on this same
`texture2ddecoder` (`unity-asset-decode/Cargo.toml`, feature `texture-advanced`).

This also corrects the conclusion recorded in 13ce65f. That commit said the decoder runs at desktop
speed on a phone, so there was nothing to win. That was measured on random blocks at 21 MPix/s.
On real data the desktop does 129-148 MPix/s against the phone's 27 MPix/s, a ratio of about 4.8x,
which is an ordinary desktop-to-phone gap. The direction of the answer is the same and now has a
sound basis -- the phone is where it should be, and the Rust alternative is slower -- but the
reason first given for it was not.

## Running

    /tmp/t2d-bench/target/release/t2d-bench          # Rust, path dependency on the cloned crate
    ./astc_bench                                     # C++, built as below
    g++ -O3 -o astc_bench astc_bench.cpp ../../Texture2DDecoderNative/astc.cpp -I../../Texture2DDecoderNative
