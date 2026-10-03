// Baseline: the encoder the app actually ships, driven from the same raw pixels the Rust
// benchmark reads. Reads <file>.rgba and <file>.dims, writes /dev/null, prints ms and bytes.
#include <stdio.h>
#include <stdlib.h>
#include <time.h>
#include <string.h>

int png_write_bgra8(const char *path, const unsigned char *pixels, int w, int h, int level);

static double now_ms(void) {
    struct timespec ts;
    clock_gettime(CLOCK_MONOTONIC, &ts);
    return ts.tv_sec * 1000.0 + ts.tv_nsec / 1e6;
}

int main(int argc, char **argv) {
    printf("deflate backend: C zlib (the shipping encoder)\n");
    for (int a = 1; a < argc; a++) {
        char dims[512], raw[512];
        snprintf(dims, sizeof dims, "%s.dims", argv[a]);
        snprintf(raw, sizeof raw, "%s.rgba", argv[a]);

        FILE *f = fopen(dims, "r");
        if (!f) { printf("  no %s\n", dims); continue; }
        int w = 0, h = 0;
        if (fscanf(f, "%d %d", &w, &h) != 2) { fclose(f); continue; }
        fclose(f);

        long len = (long)w * h * 4;
        unsigned char *px = malloc(len);
        f = fopen(raw, "rb");
        if (!f || fread(px, 1, len, f) != (size_t)len) { printf("  cannot read %s\n", raw); free(px); if (f) fclose(f); continue; }
        fclose(f);

        int iters = (w * h > 2000000) ? 3 : 10;
        int level = 1;

        png_write_bgra8("/dev/null", px, w, h, level); // warm-up
        png_write_bgra8("/dev/null", px, w, h, level);

        // size: write for real once, so the ratio can be compared and not just the time
        char tmp[] = "/tmp/c-png-bench-out.png";
        png_write_bgra8(tmp, px, w, h, level);
        FILE *sz = fopen(tmp, "rb");
        long bytes = 0;
        if (sz) { fseek(sz, 0, SEEK_END); bytes = ftell(sz); fclose(sz); }

        double t0 = now_ms();
        for (int i = 0; i < iters; i++) png_write_bgra8("/dev/null", px, w, h, level);
        double ms = (now_ms() - t0) / iters;

        printf("  %-38s %5dx%-5d fast(Paeth,L1) %7.1f ms %6ld KB\n",
               strrchr(argv[a], '/') ? strrchr(argv[a], '/') + 1 : argv[a], w, h, ms, bytes / 1024);
        free(px);
    }
    return 0;
}
