// A minimal 8-bit RGBA PNG writer over zlib, for the subset this app writes.
//
// Why it exists: ImageSharp's PNG encoder was 73% of the CPU in a texture export (882s of 8487
// files on a moto g200, measured in-process), and its deflate is managed scalar code. Android
// ships zlib (-lz), which is the same library libpng uses, so the whole encoder is written here
// rather than vendoring libpng: PNG is four chunks, and only IDAT needs a compression library.
//
// Filtering is Paeth on every row, which is what the ImageSharp encoder was configured to use, so
// output sizes stay in the same range instead of ballooning like a no-filter write would.

#include <stdio.h>
#include <stdlib.h>
#include <string.h>
#include <zlib.h>

static void put32(unsigned char *p, unsigned v)
{
    p[0] = (unsigned char)(v >> 24); p[1] = (unsigned char)(v >> 16);
    p[2] = (unsigned char)(v >> 8);  p[3] = (unsigned char)v;
}

static int write_chunk(FILE *f, const char *type, const unsigned char *data, unsigned len)
{
    unsigned char hdr[8], tail[4];
    put32(hdr, len);
    memcpy(hdr + 4, type, 4);
    if (fwrite(hdr, 1, 8, f) != 8) return -1;
    if (len && fwrite(data, 1, len, f) != len) return -1;

    uLong c = crc32(0L, Z_NULL, 0);
    c = crc32(c, (const Bytef *)type, 4);
    if (len) c = crc32(c, data, len);
    put32(tail, (unsigned)c);
    return fwrite(tail, 1, 4, f) == 4 ? 0 : -1;
}

static int paeth(int a, int b, int c)
{
    int p = a + b - c;
    int pa = p > a ? p - a : a - p;
    int pb = p > b ? p - b : b - p;
    int pc = p > c ? p - c : c - p;
    if (pa <= pb && pa <= pc) return a;
    return pb <= pc ? b : c;
}

// pixels: BGRA8, tightly packed, top row first. Returns 0 on success.
int png_write_bgra8(const char *path, const unsigned char *pixels, int w, int h, int level)
{
    if (!path || !pixels || w <= 0 || h <= 0) return -1;

    const size_t stride = (size_t)w * 4;
    const size_t rawLen = (stride + 1) * (size_t)h;
    unsigned char *raw = (unsigned char *)malloc(rawLen);
    if (!raw) return -2;

    // Two scratch rows are needed, not one. A PNG filter predicts each byte from the *original*
    // left/above/above-left bytes; reading them back out of the row being overwritten would feed
    // already-subtracted values into the predictor, which quietly ruins the compression (it made
    // the output 40% larger when this was written that way).
    unsigned char *cur = (unsigned char *)malloc(stride);
    unsigned char *prev = (unsigned char *)calloc(stride, 1); // row -1 is defined as zeroes
    if (!cur || !prev) { free(raw); free(cur); free(prev); return -2; }

    for (int y = 0; y < h; y++)
    {
        const unsigned char *src = pixels + (size_t)y * stride;
        unsigned char *dst = raw + (size_t)y * (stride + 1);
        dst[0] = 4; // Paeth

        for (size_t i = 0; i < stride; i += 4)
        {
            cur[i + 0] = src[i + 2]; // R
            cur[i + 1] = src[i + 1]; // G
            cur[i + 2] = src[i + 0]; // B
            cur[i + 3] = src[i + 3]; // A
        }

        for (size_t i = 0; i < stride; i++)
        {
            int left = i >= 4 ? cur[i - 4] : 0;
            int up = prev[i];
            int upleft = i >= 4 ? prev[i - 4] : 0;
            dst[i + 1] = (unsigned char)(cur[i] - paeth(left, up, upleft));
        }

        unsigned char *swap = prev; prev = cur; cur = swap; // this row becomes "above" for the next
    }
    free(cur);
    free(prev);

    uLongf compLen = compressBound((uLong)rawLen);
    unsigned char *comp = (unsigned char *)malloc(compLen);
    if (!comp) { free(raw); return -2; }

    if (compress2(comp, &compLen, raw, (uLong)rawLen, level) != Z_OK)
    {
        free(raw); free(comp); return -3;
    }
    free(raw);

    FILE *f = fopen(path, "wb");
    if (!f) { free(comp); return -4; }

    static const unsigned char sig[8] = { 0x89, 'P', 'N', 'G', 0x0D, 0x0A, 0x1A, 0x0A };
    unsigned char ihdr[13];
    put32(ihdr, (unsigned)w);
    put32(ihdr + 4, (unsigned)h);
    ihdr[8] = 8;   // bit depth
    ihdr[9] = 6;   // colour type: truecolour with alpha
    ihdr[10] = 0;  // deflate
    ihdr[11] = 0;  // adaptive filtering
    ihdr[12] = 0;  // no interlace

    int rc = 0;
    if (fwrite(sig, 1, 8, f) != 8) rc = -5;
    if (!rc) rc = write_chunk(f, "IHDR", ihdr, 13);
    if (!rc) rc = write_chunk(f, "IDAT", comp, (unsigned)compLen);
    if (!rc) rc = write_chunk(f, "IEND", NULL, 0);

    fclose(f);
    free(comp);
    return rc;
}
