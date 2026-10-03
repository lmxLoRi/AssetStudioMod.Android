// arm-software/astc-encoder (astcenc) against Texture2DDecoderNative/astc.cpp, on identical input.
//
// Same real ASTC payloads as the other two harnesses. Two checksums are printed, one for each byte
// order a u32 could be read from an RGBA byte buffer, so the comparison against the other decoders
// says something about the pixel layout rather than just differing.

#include <cstdio>
#include <cstdlib>
#include <cstring>
#include <cstdint>
#include <ctime>
#include "astcenc.h"

#ifndef INPUT_DIR
#define INPUT_DIR "/tmp"
#endif

static double now_ms(void)
{
    struct timespec ts;
    clock_gettime(CLOCK_MONOTONIC, &ts);
    return ts.tv_sec * 1000.0 + ts.tv_nsec / 1e6;
}

static void run(const char *label, int w, int h, int bw, int bh, astcenc_profile profile)
{
    char path[256];
    snprintf(path, sizeof path, INPUT_DIR "/astc-input-%d-%d-%dx%d.bin", w, h, bw, bh);
    FILE *f = fopen(path, "rb");
    if (!f) { printf("  missing %s\n", path); return; }
    fseek(f, 0, SEEK_END);
    long bytes = ftell(f);
    fseek(f, 0, SEEK_SET);
    uint8_t *data = (uint8_t *)malloc(bytes);
    if (fread(data, 1, bytes, f) != (size_t)bytes) { printf("  short read\n"); fclose(f); return; }
    fclose(f);

    astcenc_config config;
    astcenc_error err = astcenc_config_init(profile, bw, bh, 1, ASTCENC_PRE_MEDIUM,
                                            ASTCENC_FLG_DECOMPRESS_ONLY, &config);
    if (err != ASTCENC_SUCCESS) { printf("  config_init failed %d\n", err); return; }

    astcenc_context *ctx = nullptr;
    err = astcenc_context_alloc(&config, 1, &ctx, nullptr);
    if (err != ASTCENC_SUCCESS) { printf("  context_alloc failed %d\n", err); return; }

    uint8_t *out = (uint8_t *)malloc((size_t)w * h * 4);
    astcenc_image image;
    image.dim_x = w;
    image.dim_y = h;
    image.dim_z = 1;
    image.data_type = ASTCENC_TYPE_U8;
    void *slices[1] = { out };
    image.data = slices;

    astcenc_swizzle swz = { ASTCENC_SWZ_R, ASTCENC_SWZ_G, ASTCENC_SWZ_B, ASTCENC_SWZ_A };

    astcenc_error rc = ASTCENC_SUCCESS;
    for (int i = 0; i < 3; i++)
    {
        astcenc_decompress_reset(ctx);
        rc = astcenc_decompress_image(ctx, data, bytes, &image, &swz, 0);
    }
    if (rc != ASTCENC_SUCCESS) { printf("  decompress returned %d\n", rc); }

    const int iters = 20;
    double t0 = now_ms();
    for (int i = 0; i < iters; i++)
    {
        astcenc_decompress_reset(ctx);
        astcenc_decompress_image(ctx, data, bytes, &image, &swz, 0);
    }
    double ms = (now_ms() - t0) / iters;

    // RGBA in memory, read both ways round.
    uint64_t sumRgba = 0, sumAbgr = 0;
    for (long i = 0; i < (long)w * h; i++)
    {
        uint32_t rgba = (uint32_t)out[i * 4] | ((uint32_t)out[i * 4 + 1] << 8) |
                        ((uint32_t)out[i * 4 + 2] << 16) | ((uint32_t)out[i * 4 + 3] << 24);
        uint32_t abgr = (uint32_t)out[i * 4 + 3] | ((uint32_t)out[i * 4 + 2] << 8) |
                        ((uint32_t)out[i * 4 + 1] << 16) | ((uint32_t)out[i * 4] << 24);
        sumRgba = sumRgba * 131 + rgba;
        sumAbgr = sumAbgr * 131 + abgr;
    }

    { char op[256]; snprintf(op, sizeof op, INPUT_DIR "/astcenc-out-%dx%d.rgba", w, h); FILE* of = fopen(op, "wb"); if (of) { fwrite(out, 1, (size_t)w*h*4, of); fclose(of); } }
    double mpix = (double)w * h / 1e6;
    printf("  %-14s %4dx%-5d %6.2f ms   %7.1f MPix/s   (rgba=0x%016llx abgr=0x%016llx)  bw=%d bh=%d\n",
           label, w, h, ms, mpix / (ms / 1000.0),
           (unsigned long long)sumRgba, (unsigned long long)sumAbgr, bw, bh);

    free(data);
    free(out);
    astcenc_context_free(ctx);
}

int main(void)
{
    printf("astcenc (ARM-software), desktop x86_64\n");
    run("ferris 4x4", 512, 512, 4, 4, ASTCENC_PRF_LDR);
    run("game 4x4 A", 2048, 1916, 4, 4, ASTCENC_PRF_LDR);
    run("game 4x4 B", 2048, 1872, 4, 4, ASTCENC_PRF_LDR);
    run("game 6x6 A", 943, 2048, 6, 6, ASTCENC_PRF_LDR);
    run("game 6x6 B", 1066, 2048, 6, 6, ASTCENC_PRF_LDR);
    return 0;
}
