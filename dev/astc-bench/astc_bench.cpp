// Is the ASTC decoder in Texture2DDecoderNative actually slow, or is ASTC just expensive?
//
// Worth knowing before porting 1148 lines of someone else's decoder. The phone measures ~27 MPix/s
// for ASTC 4x4; scalar code on a desktop core usually runs 3-4x a phone core, so a desktop figure
// near 100 MPix/s means the phone is where it should be and there is nothing to win.

#include <stdio.h>
#include <stdlib.h>
#include <stdint.h>
#include <string.h>
#include <time.h>

int decode_astc(const uint8_t *data, const long w, const long h, const int bw, const int bh, uint32_t *image);

static double now_ms(void)
{
    struct timespec ts;
    clock_gettime(CLOCK_MONOTONIC, &ts);
    return ts.tv_sec * 1000.0 + ts.tv_nsec / 1e6;
}

static void run(const char *label, int w, int h, int bw, int bh)
{
    // ASTC pads to whole blocks and every block is 16 bytes regardless of footprint.
    const int blocks_x = (w + bw - 1) / bw;
    const int blocks_y = (h + bh - 1) / bh;
    const long bytes = (long)blocks_x * blocks_y * 16;

    uint8_t *data = (uint8_t *)malloc(bytes);
    uint32_t *out = (uint32_t *)malloc((size_t)blocks_x * bw * blocks_y * bh * 4);

    // Random blocks rather than a captured texture: every mode and every bit pattern has to decode,
    // which makes this pessimistic rather than flattering. Noted because it is not a real mix.
    srand(1234);
    for (long i = 0; i < bytes; i++) data[i] = (uint8_t)(rand() & 0xff);

    for (int i = 0; i < 3; i++) decode_astc(data, w, h, bw, bh, out);

    const int iters = 20;
    double t0 = now_ms();
    for (int i = 0; i < iters; i++) decode_astc(data, w, h, bw, bh, out);
    double ms = (now_ms() - t0) / iters;

    double mpix = (double)w * h / 1e6;
    printf("  %-14s %4dx%-5d %6.2f ms   %7.1f MPix/s   %6.1f Mblocks/s   (out[0]=0x%08x)\n",
           label, w, h, ms, mpix / (ms / 1000.0),
           (double)(w / bw) * (h / bh) / (ms / 1000.0) / 1e6, out[0]);

    free(data);
    free(out);
}

int main(void)
{
    printf("decode_astc standalone, desktop %s\n", sizeof(void *) == 8 ? "x86_64" : "32-bit");
    run("ASTC 4x4", 1024, 1024, 4, 4);
    run("ASTC 6x6", 1020, 1020, 6, 6);
    run("ASTC 8x8", 1024, 1024, 8, 8);
    run("ASTC 4x4 big", 2048, 2048, 4, 4);
    return 0;
}
