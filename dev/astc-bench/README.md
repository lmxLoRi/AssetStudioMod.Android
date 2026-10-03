# astc-bench

Is the ASTC decoder in `Texture2DDecoderNative/astc.cpp` actually slow, or is ASTC just
expensive? Asked because it is 118s of the export's CPU and porting a faster decoder is a
thousand-line job that should not start without evidence.

    g++ -O3 -o astc_bench astc_bench.cpp ../../Texture2DDecoderNative/astc.cpp -I../../Texture2DDecoderNative
    ./astc_bench

    decode_astc standalone, desktop x86_64
      ASTC 4x4       1024x1024   49.97 ms   21.0 MPix/s   1.3 Mblocks/s
      ASTC 6x6       1020x1020   36.19 ms   28.7 MPix/s   0.8 Mblocks/s
      ASTC 8x8       1024x1024   29.18 ms   35.9 MPix/s   0.6 Mblocks/s
      ASTC 4x4 big   2048x2048  201.45 ms   20.8 MPix/s   1.3 Mblocks/s

The phone measures 27 MPix/s for ASTC 4x4 in the app. So the decoder runs at desktop speed on
a phone, and the premise that motivated the port -- "a desktop core is 3-4x a phone core, so a
desktop figure near 100 MPix/s would mean the phone is just where it should be" -- does not
hold: there is no measured reference that this decoder is slow against. Porting stays off the
table until there is one.

Caveat: the input is random blocks rather than a captured texture. Every mode and bit pattern
has to decode, which makes this pessimistic rather than flattering, and it is not a real mix.
