// OBNS 1.31N: block compression (BC1, BC3) of textures for the Switch's GPU, with their mipmap chains, for the
// compressed texture cache (NX/OpenBve.NX/Deko/CompressedTextures.cs). Written for OBNS; no third-party encoder.
//
// BC1: 4x4 texels in 8 bytes - two RGB565 endpoints and a 2-bit index per texel. With colour0 > colour1 four colours
// (the endpoints and two between); with colour0 <= colour1 three and transparent black (index 3), used for
// colour-keyed textures. BC3: an 8-byte alpha block (two 8-bit endpoints, 3-bit indices, eight levels) then a BC1
// colour block read as four colours. Endpoints: the block's principal colour axis (a few power iterations of its
// covariance), the extremes along it, pulled in by 1/16 of the range as stb_dxt and others do, then each texel takes
// the nearest palette entry.
//
// Simplified BSD License (BSD-2-Clause), Copyright (c) 2026, The OpenBVE Android port contributors.

#include <stdint.h>
#include <stdlib.h>
#include <string.h>

#define EXPORT __attribute__((visibility("default")))

enum { BC1_OPAQUE = 56, BC1_ALPHA = 57, BC3 = 59 };

static inline int clampi(int v, int lo, int hi) { return v < lo ? lo : v > hi ? hi : v; }

static uint16_t to565(const float c[3])
{
    int r = clampi((int)(c[0] * 31.0f / 255.0f + 0.5f), 0, 31);
    int g = clampi((int)(c[1] * 63.0f / 255.0f + 0.5f), 0, 63);
    int b = clampi((int)(c[2] * 31.0f / 255.0f + 0.5f), 0, 31);
    return (uint16_t)(r << 11 | g << 5 | b);
}

static void from565(uint16_t v, int c[3])
{
    int r = v >> 11 & 31, g = v >> 5 & 63, b = v & 31;
    c[0] = r << 3 | r >> 2;
    c[1] = g << 2 | g >> 4;
    c[2] = b << 3 | b >> 2;
}

// The colour block for 16 RGBA texels. `cutout`: texels with alpha under 128 become transparent (three-colour mode)
// when any are; otherwise four colours. `force4`: always four colours (BC3's colour block); its endpoints then come from
// the texels that can be seen (alpha above 0), every texel still taking the nearest of the four.
static void color_block(const uint8_t *texels, uint8_t *out, int cutout, int force4)
{
    int used[16], n = 0, transparent = 0;
    for (int i = 0; i < 16; i++) {
        if (force4 ? texels[i * 4 + 3] == 0 : cutout && texels[i * 4 + 3] < 128) transparent = 1;
        else used[n++] = i;
    }
    if (n == 0 && force4) {
        // Nothing seen in the block: its colours from all of it (spread in from the edges, see bleed()).
        for (int i = 0; i < 16; i++) used[i] = i;
        n = 16;
        transparent = 0;
    }
    if (n == 0) {
        // All transparent: three-colour mode, every index 3.
        out[0] = 0; out[1] = 0; out[2] = 0; out[3] = 0;
        out[4] = out[5] = out[6] = out[7] = 0xFF;
        return;
    }

    float mean[3] = { 0, 0, 0 };
    for (int k = 0; k < n; k++)
        for (int c = 0; c < 3; c++) mean[c] += texels[used[k] * 4 + c];
    for (int c = 0; c < 3; c++) mean[c] /= (float)n;
    float cov[6] = { 0, 0, 0, 0, 0, 0 };
    for (int k = 0; k < n; k++) {
        float r = texels[used[k] * 4] - mean[0], g = texels[used[k] * 4 + 1] - mean[1], b = texels[used[k] * 4 + 2] - mean[2];
        cov[0] += r * r; cov[1] += r * g; cov[2] += r * b; cov[3] += g * g; cov[4] += g * b; cov[5] += b * b;
    }
    float axis[3] = { 0.9f, 1.0f, 0.7f };
    for (int it = 0; it < 4; it++) {
        float x = cov[0] * axis[0] + cov[1] * axis[1] + cov[2] * axis[2];
        float y = cov[1] * axis[0] + cov[3] * axis[1] + cov[4] * axis[2];
        float z = cov[2] * axis[0] + cov[4] * axis[1] + cov[5] * axis[2];
        float m = x * x + y * y + z * z;
        if (m < 1e-6f) break;
        float inv = 1.0f / __builtin_sqrtf(m);
        axis[0] = x * inv; axis[1] = y * inv; axis[2] = z * inv;
    }
    float lo = 1e9f, hi = -1e9f;
    for (int k = 0; k < n; k++) {
        const uint8_t *t = texels + used[k] * 4;
        float d = (t[0] - mean[0]) * axis[0] + (t[1] - mean[1]) * axis[1] + (t[2] - mean[2]) * axis[2];
        if (d < lo) lo = d;
        if (d > hi) hi = d;
    }
    float inset = (hi - lo) / 16.0f;
    lo += inset; hi -= inset;
    float a[3], b[3];
    for (int c = 0; c < 3; c++) { a[c] = mean[c] + axis[c] * hi; b[c] = mean[c] + axis[c] * lo; }
    uint16_t c0 = to565(a), c1 = to565(b);

    int three = cutout && transparent && !force4;
    if (three) { if (c0 > c1) { uint16_t s = c0; c0 = c1; c1 = s; } }
    else {
        if (c0 < c1) { uint16_t s = c0; c0 = c1; c1 = s; }
        else if (c0 == c1) {
            // One colour: every index 0 (four-colour mode needs c0 > c1; with them equal it reads as three-colour, where
            // index 0 is still c0).
            out[0] = (uint8_t)c0; out[1] = (uint8_t)(c0 >> 8); out[2] = (uint8_t)c1; out[3] = (uint8_t)(c1 >> 8);
            out[4] = out[5] = out[6] = out[7] = 0;
            if (transparent && !force4) {
                uint32_t indices = 0;
                for (int i = 0; i < 16; i++) if (texels[i * 4 + 3] < 128) indices |= 3u << (2 * i);
                out[4] = (uint8_t)indices; out[5] = (uint8_t)(indices >> 8); out[6] = (uint8_t)(indices >> 16); out[7] = (uint8_t)(indices >> 24);
            }
            return;
        }
    }

    int p[4][3];
    from565(c0, p[0]);
    from565(c1, p[1]);
    int colours;
    if (three) {
        for (int c = 0; c < 3; c++) p[2][c] = (p[0][c] + p[1][c]) / 2;
        colours = 3;
    } else {
        for (int c = 0; c < 3; c++) { p[2][c] = (2 * p[0][c] + p[1][c]) / 3; p[3][c] = (p[0][c] + 2 * p[1][c]) / 3; }
        colours = 4;
    }

    uint32_t indices = 0;
    for (int i = 0; i < 16; i++) {
        const uint8_t *t = texels + i * 4;
        int best = 0;
        if (three && cutout && t[3] < 128) best = 3;
        else {
            int bestD = 1 << 30;
            for (int j = 0; j < colours; j++) {
                int dr = t[0] - p[j][0], dg = t[1] - p[j][1], db = t[2] - p[j][2];
                int d = dr * dr + dg * dg + db * db;
                if (d < bestD) { bestD = d; best = j; }
            }
        }
        indices |= (uint32_t)best << (2 * i);
    }
    out[0] = (uint8_t)c0; out[1] = (uint8_t)(c0 >> 8); out[2] = (uint8_t)c1; out[3] = (uint8_t)(c1 >> 8);
    out[4] = (uint8_t)indices; out[5] = (uint8_t)(indices >> 8); out[6] = (uint8_t)(indices >> 16); out[7] = (uint8_t)(indices >> 24);
}

// BC3's alpha block: eight levels between the block's least and greatest alpha.
static void alpha_block(const uint8_t *texels, uint8_t *out)
{
    int lo = 255, hi = 0;
    for (int i = 0; i < 16; i++) {
        int a = texels[i * 4 + 3];
        if (a < lo) lo = a;
        if (a > hi) hi = a;
    }
    out[0] = (uint8_t)hi;
    out[1] = (uint8_t)lo;
    int level[8];
    level[0] = hi; level[1] = lo;
    for (int j = 1; j < 7; j++) level[j + 1] = ((7 - j) * hi + j * lo) / 7;
    uint64_t bits = 0;
    for (int i = 0; i < 16; i++) {
        int a = texels[i * 4 + 3], best = 0, bestD = 1 << 30;
        if (hi != lo)
            for (int j = 0; j < 8; j++) {
                int d = abs(a - level[j]);
                if (d < bestD) { bestD = d; best = j; }
            }
        bits |= (uint64_t)best << (3 * i);
    }
    for (int k = 0; k < 6; k++) out[2 + k] = (uint8_t)(bits >> (8 * k));
}

static int block_bytes(int format) { return format == BC3 ? 16 : 8; }

static size_t level_size(int w, int h, int format)
{
    return (size_t)((w + 3) / 4) * (size_t)((h + 3) / 4) * (size_t)block_bytes(format);
}

// One level's blocks, row by row, from tightly packed RGBA.
static void compress_level(const uint8_t *rgba, int w, int h, int format, uint8_t *out)
{
    uint8_t texels[64];
    for (int by = 0; by < (h + 3) / 4; by++)
        for (int bx = 0; bx < (w + 3) / 4; bx++) {
            for (int y = 0; y < 4; y++)
                for (int x = 0; x < 4; x++) {
                    // Past the edge: the nearest texel inside.
                    int sx = clampi(bx * 4 + x, 0, w - 1), sy = clampi(by * 4 + y, 0, h - 1);
                    memcpy(texels + (y * 4 + x) * 4, rgba + ((size_t)sy * w + sx) * 4, 4);
                }
            if (format == BC3) {
                alpha_block(texels, out);
                color_block(texels, out + 8, 0, 1);
                out += 16;
            } else {
                color_block(texels, out, format == BC1_ALPHA, 0);
                out += 8;
            }
        }
}

// Half size, each texel the average of four; colours weighted by alpha, so transparent texels' colours (a colour key's)
// don't bleed into the edges. `binary`: alpha kept to 0 or 255 (a cut-out's, as GL's one-bit formats make its mipmaps).
static void halve(const uint8_t *src, int w, int h, uint8_t *dst, int nw, int nh, int binary)
{
    for (int y = 0; y < nh; y++)
        for (int x = 0; x < nw; x++) {
            int x0 = clampi(2 * x, 0, w - 1), x1 = clampi(2 * x + 1, 0, w - 1), y0 = clampi(2 * y, 0, h - 1), y1 = clampi(2 * y + 1, 0, h - 1);
            const uint8_t *q[4] = { src + ((size_t)y0 * w + x0) * 4, src + ((size_t)y0 * w + x1) * 4, src + ((size_t)y1 * w + x0) * 4, src + ((size_t)y1 * w + x1) * 4 };
            int alpha = q[0][3] + q[1][3] + q[2][3] + q[3][3];
            uint8_t *o = dst + ((size_t)y * nw + x) * 4;
            for (int c = 0; c < 3; c++) {
                int sum = alpha > 0 ? q[0][c] * q[0][3] + q[1][c] * q[1][3] + q[2][c] * q[2][3] + q[3][c] * q[3][3] : q[0][c] + q[1][c] + q[2][c] + q[3][c];
                o[c] = (uint8_t)(alpha > 0 ? (sum + alpha / 2) / alpha : (sum + 2) / 4);
            }
            o[3] = (uint8_t)((alpha + 2) / 4);
            if (binary) o[3] = o[3] >= 128 ? 255 : 0;
        }
}

/*
 * The colours of a cut-out's transparent texels (a colour key's, or black) spread in from the texels that can be seen,
 * a texel at a time (up to `passes`), the rest given the image's average: filtering at the edges then blends in the
 * leaves' own colours instead of dark ones (OBNS 1.31N: dark fringes and specks round the trees on the Switch, from BC1's
 * transparent black, until 2026-11-07). Alpha is left as it is.
 */
static void bleed(uint8_t *rgba, int w, int h, int passes)
{
    size_t count = (size_t)w * h;
    uint8_t *known = malloc(count), *next = malloc(count);
    if (!known || !next) { free(known); free(next); return; }
    long sum[3] = { 0, 0, 0 }, seen = 0;
    for (size_t i = 0; i < count; i++) {
        known[i] = rgba[i * 4 + 3] != 0;
        if (known[i]) { for (int c = 0; c < 3; c++) sum[c] += rgba[i * 4 + c]; seen++; }
    }
    if (seen == 0 || seen == (long)count) { free(known); free(next); return; }
    for (int pass = 0; pass < passes; pass++) {
        int changed = 0;
        memcpy(next, known, count);
        for (int y = 0; y < h; y++)
            for (int x = 0; x < w; x++) {
                size_t i = (size_t)y * w + x;
                if (known[i]) continue;
                int s[3] = { 0, 0, 0 }, k = 0;
                for (int dy = -1; dy <= 1; dy++)
                    for (int dx = -1; dx <= 1; dx++) {
                        int nx = x + dx, ny = y + dy;
                        if ((dx | dy) == 0 || nx < 0 || ny < 0 || nx >= w || ny >= h) continue;
                        size_t j = (size_t)ny * w + nx;
                        if (!known[j]) continue;
                        for (int c = 0; c < 3; c++) s[c] += rgba[j * 4 + c];
                        k++;
                    }
                if (k) { for (int c = 0; c < 3; c++) rgba[i * 4 + c] = (uint8_t)((s[c] + k / 2) / k); next[i] = 1; changed = 1; }
            }
        memcpy(known, next, count);
        if (!changed) break;
    }
    for (size_t i = 0; i < count; i++)
        if (!known[i]) for (int c = 0; c < 3; c++) rgba[i * 4 + c] = (uint8_t)(sum[c] / seen);
    free(known);
    free(next);
}

// The mipmap chain's level count for a size (down to 1x1).
EXPORT int obns_bc_levels(int w, int h)
{
    int levels = 1, m = w > h ? w : h;
    while (m > 1) { m >>= 1; levels++; }
    return levels;
}

// Bytes for `levels` levels in the format, each level's blocks padded to 256 bytes (the copies' alignment).
EXPORT int64_t obns_bc_size(int w, int h, int format, int levels)
{
    int64_t total = 0;
    for (int l = 0; l < levels; l++) {
        total += (int64_t)((level_size(w, h, format) + 255) & ~(size_t)255);
        w = w > 1 ? w / 2 : 1;
        h = h > 1 ? h / 2 : 1;
    }
    return total;
}

// Compresses tightly packed RGBA and its mipmaps into `out` (obns_bc_size bytes): each level padded to 256 bytes.
// `format` | OBNS_BC_CUTOUT (BC3 only): a cut-out's (alpha 0 or 255) - its hidden colours spread in from the edges, its
// mipmaps' alpha kept to 0 or 255. Returns 0, or 1 for a format it doesn't make, 2 for no memory.
#define OBNS_BC_CUTOUT 0x100
EXPORT int obns_bc_compress(const uint8_t *rgba, int w, int h, int format, int levels, uint8_t *out)
{
    int cutout = format & OBNS_BC_CUTOUT;
    format &= 0xFF;
    if (format != BC1_OPAQUE && format != BC1_ALPHA && format != BC3) return 1;
    if (cutout && format != BC3) return 1;
    uint8_t *a = NULL, *b = NULL, *own = NULL;
    const uint8_t *level = rgba;
    if (format == BC3) {
        // A copy whose hidden colours are spread in (the caller's is left as it is).
        own = malloc((size_t)w * h * 4);
        if (!own) return 2;
        memcpy(own, rgba, (size_t)w * h * 4);
        bleed(own, w, h, 16);
        level = own;
    }
    for (int l = 0; l < levels; l++) {
        size_t size = level_size(w, h, format);
        compress_level(level, w, h, format, out);
        memset(out + size, 0, ((size + 255) & ~(size_t)255) - size);
        out += (size + 255) & ~(size_t)255;
        if (l + 1 == levels) break;
        int nw = w > 1 ? w / 2 : 1, nh = h > 1 ? h / 2 : 1;
        uint8_t *next = level == a || level == rgba || level == own ? (b ? b : (b = malloc((size_t)nw * nh * 4))) : (a ? a : (a = malloc((size_t)nw * nh * 4)));
        if (!next) { free(a); free(b); free(own); return 2; }
        halve(level, w, h, next, nw, nh, cutout);
        level = next;
        w = nw;
        h = nh;
    }
    free(a);
    free(b);
    free(own);
    return 0;
}
