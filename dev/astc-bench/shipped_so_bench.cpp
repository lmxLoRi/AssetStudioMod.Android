// Times the libTexture2DDecoderNative.so that is actually shipped in the APK, loaded with dlopen,
// against the same source compiled fresh at -O3. Same input files, same device, same harness.

#include <cstdio>
#include <cstdlib>
#include <cstdint>
#include <ctime>
#include <dlfcn.h>

#ifndef INPUT_DIR
#define INPUT_DIR "/tmp"
#endif

typedef int (*decode_fn)(const void *, int32_t, int32_t, int32_t, int32_t, void *);

static double now_ms(void)
{
    struct timespec ts;
    clock_gettime(CLOCK_MONOTONIC, &ts);
    return ts.tv_sec * 1000.0 + ts.tv_nsec / 1e6;
}

int main(int argc, char **argv)
{
    const char *lib = argc > 1 ? argv[1] : "/data/local/tmp/libTexture2DDecoderNative.so";
    void *h = dlopen(lib, RTLD_NOW);
    if (!h) { printf("dlopen failed: %s\n", dlerror()); return 1; }
    decode_fn fn = (decode_fn)dlsym(h, "DecodeASTC");
    if (!fn) { printf("dlsym DecodeASTC failed: %s\n", dlerror()); return 1; }
    printf("loaded %s\n", lib);

    struct { const char *label; int w, h, bw, bh; } cases[] = {
        { "ferris 4x4", 512, 512, 4, 4 },
        { "game 4x4 A", 2048, 1916, 4, 4 },
        { "game 4x4 B", 2048, 1872, 4, 4 },
        { "game 6x6 A", 943, 2048, 6, 6 },
        { "game 6x6 B", 1066, 2048, 6, 6 },
    };

    for (auto &c : cases)
    {
        char path[256];
        snprintf(path, sizeof path, INPUT_DIR "/astc-input-%d-%d-%dx%d.bin", c.w, c.h, c.bw, c.bh);
        FILE *f = fopen(path, "rb");
        if (!f) { printf("  missing %s\n", path); continue; }
        fseek(f, 0, SEEK_END); long bytes = ftell(f); fseek(f, 0, SEEK_SET);
        uint8_t *data = (uint8_t *)malloc(bytes);
        if (fread(data, 1, bytes, f) != (size_t)bytes) { printf("short read\n"); fclose(f); continue; }
        fclose(f);

        long bx = (c.w + c.bw - 1) / c.bw, by = (c.h + c.bh - 1) / c.bh;
        uint32_t *out = (uint32_t *)malloc((size_t)bx * c.bw * by * c.bh * 4);

        for (int i = 0; i < 3; i++) fn(data, c.w, c.h, c.bw, c.bh, out);

        int iters = 20;
        double t0 = now_ms();
        for (int i = 0; i < iters; i++) fn(data, c.w, c.h, c.bw, c.bh, out);
        double ms = (now_ms() - t0) / iters;

        unsigned long long sum = 0;
        for (long i = 0; i < bx * c.bw * by * c.bh; i++) sum = sum * 131 + out[i];

        printf("  %-15s %4dx%-5d %6.2f ms   %7.1f MPix/s   (checksum=0x%016llx)\n",
               c.label, c.w, c.h, ms, (double)c.w * c.h / 1e6 / (ms / 1000.0), sum);
        free(data); free(out);
    }
    return 0;
}
