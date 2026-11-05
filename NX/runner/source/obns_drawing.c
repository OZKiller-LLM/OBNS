// OBNS 1.31N: the native half of the Switch's System.Drawing (NX/OpenBve.Compat.NX/Drawing/Native.cs).
// Copyright (c) The OpenBVE Android port contributors. BSD-2-Clause, as OBNS.
//
// Images: PNG through libpng's simplified API, JPEG through libjpeg-turbo, GIF (first frame) by the small LZW decoder
// below, all to unpremultiplied BGRA from malloc; PNG and JPEG written back. Text: FreeType over the console's shared
// fonts (pl service), the other shared fonts filling characters a font lacks, drawn through the Graphics transform.

#include <math.h>
#include <stdint.h>
#include <stdio.h>
#include <stdlib.h>
#include <string.h>
#include <strings.h>
#include <switch.h>
#include <png.h>
#include <turbojpeg.h>
#include <ft2build.h>
#include FT_FREETYPE_H
#include FT_SYNTHESIS_H

#define EXPORT __attribute__((visibility("default")))

// ---------------------------------------------------------------------------------------------------------------
// Images

static int decode_png(const uint8_t *data, size_t length, int *w, int *h, void **pixels)
{
    png_image image;
    memset(&image, 0, sizeof image);
    image.version = PNG_IMAGE_VERSION;
    if (!png_image_begin_read_from_memory(&image, data, length))
        return 2;
    image.format = PNG_FORMAT_BGRA;
    void *buffer = malloc(PNG_IMAGE_SIZE(image));
    if (!buffer) { png_image_free(&image); return 2; }
    if (!png_image_finish_read(&image, NULL, buffer, 0, NULL)) { free(buffer); return 2; }
    *w = (int)image.width;
    *h = (int)image.height;
    *pixels = buffer;
    return 0;
}

static int decode_jpeg(const uint8_t *data, size_t length, int *w, int *h, void **pixels)
{
    tjhandle tj = tjInitDecompress();
    if (!tj) return 2;
    int width, height, subsampling, colourspace;
    if (tjDecompressHeader3(tj, data, (unsigned long)length, &width, &height, &subsampling, &colourspace) != 0) { tjDestroy(tj); return 2; }
    void *buffer = malloc((size_t)width * height * 4);
    if (!buffer) { tjDestroy(tj); return 2; }
    // A truncated JPEG still gives what it has, as Skia's IncompleteInput did on Android.
    if (tjDecompress2(tj, data, (unsigned long)length, buffer, width, 0, height, TJPF_BGRA, TJFLAG_ACCURATEDCT) != 0 &&
        tjGetErrorCode(tj) == TJERR_FATAL) { free(buffer); tjDestroy(tj); return 2; }
    tjDestroy(tj);
    *w = width;
    *h = height;
    *pixels = buffer;
    return 0;
}

// GIF: the first image, interlaced or not, its transparent colour (from a graphic control extension) made transparent.
static int decode_gif(const uint8_t *d, size_t n, int *w, int *h, void **pixels)
{
    if (n < 13) return 2;
    int width = d[6] | (d[7] << 8), height = d[8] | (d[9] << 8);
    size_t p = 13;
    uint8_t global[768];
    int globalSize = 0;
    if (d[10] & 0x80) { globalSize = 2 << (d[10] & 7); if (p + globalSize * 3 > n) return 2; memcpy(global, d + p, globalSize * 3); p += globalSize * 3; }
    int transparent = -1;
    while (p < n) {
        uint8_t block = d[p++];
        if (block == 0x21) {
            if (p >= n) return 2;
            uint8_t label = d[p++];
            if (label == 0xF9 && p + 5 < n && (d[p + 1] & 1)) transparent = d[p + 4];
            while (p < n && d[p]) p += d[p] + 1;
            p++;
            continue;
        }
        if (block != 0x2C) return 2;
        if (p + 9 > n) return 2;
        int left = d[p] | (d[p + 1] << 8), top = d[p + 2] | (d[p + 3] << 8);
        int iw = d[p + 4] | (d[p + 5] << 8), ih = d[p + 6] | (d[p + 7] << 8);
        uint8_t flags = d[p + 8];
        p += 9;
        const uint8_t *palette = global;
        int paletteSize = globalSize;
        uint8_t local[768];
        if (flags & 0x80) { paletteSize = 2 << (flags & 7); if (p + paletteSize * 3 > n) return 2; memcpy(local, d + p, paletteSize * 3); p += paletteSize * 3; palette = local; }
        if (p >= n) return 2;
        int minCode = d[p++];
        if (minCode < 2 || minCode > 11) return 2;

        // The image data's sub-blocks joined.
        size_t dataLength = 0, q = p;
        while (q < n && d[q]) { dataLength += d[q]; q += d[q] + 1; }
        uint8_t *lzw = malloc(dataLength + 4);
        uint8_t *indices = malloc((size_t)iw * ih + 1);
        uint32_t *out = calloc((size_t)width * height, 4);
        if (!lzw || !indices || !out) { free(lzw); free(indices); free(out); return 2; }
        size_t o = 0;
        while (p < n && d[p]) { memcpy(lzw + o, d + p + 1, d[p]); o += d[p]; p += d[p] + 1; }

        // LZW, 12-bit codes at most.
        static uint16_t prefix[4096];
        static uint8_t suffix[4096], stack[4097];
        int clear = 1 << minCode, end = clear + 1, next = clear + 2, size = minCode + 1, old = -1;
        uint8_t first = 0;
        size_t bit = 0, count = 0, total = (size_t)iw * ih;
        for (int i = 0; i < clear; i++) { prefix[i] = 0; suffix[i] = (uint8_t)i; }
        while (count < total && (bit + size) <= dataLength * 8) {
            int code = 0;
            for (int b = 0; b < size; b++, bit++) code |= ((lzw[bit >> 3] >> (bit & 7)) & 1) << b;
            if (code == clear) { size = minCode + 1; next = clear + 2; old = -1; continue; }
            if (code == end) break;
            int sp = 0, c = code;
            if (old < 0) { if (code >= clear) break; indices[count++] = (uint8_t)code; old = code; first = (uint8_t)code; continue; }
            if (code >= next) { stack[sp++] = first; c = old; }
            while (c >= clear && sp < 4096) { stack[sp++] = suffix[c]; c = prefix[c]; }
            stack[sp++] = (uint8_t)c;
            first = (uint8_t)c;
            while (sp > 0 && count < total) indices[count++] = stack[--sp];
            if (next < 4096) { prefix[next] = (uint16_t)old; suffix[next] = first; next++; if (next == (1 << size) && size < 12) size++; }
            old = code;
        }

        // Rows in order, or in the four interlace passes.
        int row = 0;
        for (int y = 0; y < ih; y++) {
            int ty;
            if (flags & 0x40) {
                static const int start[4] = {0, 4, 2, 1}, step[4] = {8, 8, 4, 2};
                int r = y, pass = 0;
                for (pass = 0; pass < 4; pass++) { int rows = (ih - start[pass] + step[pass] - 1) / step[pass]; if (r < rows) break; r -= rows; }
                ty = start[pass] + r * step[pass];
            } else ty = row++;
            for (int x = 0; x < iw; x++) {
                int dx = left + x, dy = top + ty;
                if (dx >= width || dy >= height || (size_t)y * iw + x >= count) continue;
                int index = indices[(size_t)y * iw + x];
                if (index == transparent || index >= paletteSize) continue;
                const uint8_t *c = palette + index * 3;
                out[(size_t)dy * width + dx] = 0xFF000000u | (c[0] << 16) | (c[1] << 8) | c[2];
            }
        }
        free(lzw);
        free(indices);
        *w = width;
        *h = height;
        *pixels = out;
        return 0;
    }
    return 2;
}

EXPORT int obns_image_decode(const uint8_t *data, intptr_t length, int *w, int *h, void **pixels)
{
    *pixels = NULL;
    if (length >= 8 && memcmp(data, "\x89PNG", 4) == 0) return decode_png(data, (size_t)length, w, h, pixels);
    if (length >= 3 && data[0] == 0xFF && data[1] == 0xD8) return decode_jpeg(data, (size_t)length, w, h, pixels);
    if (length >= 6 && memcmp(data, "GIF8", 4) == 0) return decode_gif(data, (size_t)length, w, h, pixels);
    return 1;
}

EXPORT void obns_image_free(void *pixels)
{
    free(pixels);
}

EXPORT int obns_image_save(const char *path, int format, const void *pixels, int width, int height, int stride)
{
    if (format == 1) {
        tjhandle tj = tjInitCompress();
        if (!tj) return 1;
        unsigned char *jpeg = NULL;
        unsigned long size = 0;
        int rc = tjCompress2(tj, pixels, width, stride, height, TJPF_BGRA, &jpeg, &size, TJSAMP_420, 90, 0);
        tjDestroy(tj);
        if (rc != 0) return 1;
        FILE *f = fopen(path, "wb");
        if (!f) { tjFree(jpeg); return 1; }
        size_t written = fwrite(jpeg, 1, size, f);
        fclose(f);
        tjFree(jpeg);
        return written == size ? 0 : 1;
    }
    png_image image;
    memset(&image, 0, sizeof image);
    image.version = PNG_IMAGE_VERSION;
    image.width = (png_uint_32)width;
    image.height = (png_uint_32)height;
    image.format = PNG_FORMAT_BGRA;
    return png_image_write_to_file(&image, path, 0, pixels, stride, NULL) ? 0 : 1;
}

// ---------------------------------------------------------------------------------------------------------------
// Fonts

enum { FACES = 6 };
static const PlSharedFontType faceTypes[FACES] = {
    PlSharedFontType_Standard, PlSharedFontType_ChineseSimplified, PlSharedFontType_ExtChineseSimplified,
    PlSharedFontType_ChineseTraditional, PlSharedFontType_KO, PlSharedFontType_NintendoExt };

static FT_Library library;
static PlFontData fontData[FACES];
static bool fontsReady, fontsTried;
static Mutex fontMutex;

typedef struct {
    FT_Face faces[FACES];   // the face asked for first, then the others in the order above
    int order[FACES];
    float pixels;
    bool bold, italic;
} ObnsFont;

static bool fonts_init(void)
{
    if (fontsTried) return fontsReady;
    fontsTried = true;
    if (R_FAILED(plInitialize(PlServiceType_User))) return false;
    for (int i = 0; i < FACES; i++)
        if (R_FAILED(plGetSharedFontByType(&fontData[i], faceTypes[i]))) fontData[i].address = NULL;
    fontsReady = FT_Init_FreeType(&library) == 0 && fontData[0].address != NULL;
    return fontsReady;
}

// Whether a name contains a word, ignoring case.
static bool contains(const char *name, const char *word)
{
    size_t n = strlen(word);
    for (; *name; name++) if (strncasecmp(name, word, n) == 0) return true;
    return false;
}

EXPORT void *obns_font_open(const char *family, int bold, int italic, float pixels)
{
    mutexLock(&fontMutex);
    if (!fonts_init()) { mutexUnlock(&fontMutex); return NULL; }
    ObnsFont *font = calloc(1, sizeof *font);
    font->pixels = pixels > 0 ? pixels : 12;
    font->bold = bold != 0;
    font->italic = italic != 0;
    // The family asked for first: the Chinese and Korean ones by name, anything else the standard font.
    int primary = 0;
    if (family) {
        if (contains(family, "traditional") || contains(family, " TC") || contains(family, "JhengHei") || contains(family, "MingLiU")) primary = 3;
        else if (contains(family, "chinese") || contains(family, "simplified") || contains(family, " SC") || contains(family, "YaHei") || contains(family, "SimSun") || contains(family, "SimHei")) primary = 1;
        else if (contains(family, "korean") || contains(family, "Gothic") || contains(family, "Malgun") || contains(family, "Gulim")) primary = 4;
    }
    font->order[0] = primary;
    for (int i = 0, k = 1; i < FACES; i++) if (i != primary) font->order[k++] = i;
    mutexUnlock(&fontMutex);
    return font;
}

// A face of this font at its size, opened the first time it's needed.
static FT_Face face_at(ObnsFont *font, int slot)
{
    int index = font->order[slot];
    if (!font->faces[index] && fontData[index].address) {
        mutexLock(&fontMutex);
        if (FT_New_Memory_Face(library, fontData[index].address, fontData[index].size, 0, &font->faces[index]) == 0)
            FT_Set_Pixel_Sizes(font->faces[index], 0, (FT_UInt)lroundf(font->pixels));
        else font->faces[index] = NULL;
        mutexUnlock(&fontMutex);
    }
    return font->faces[index];
}

// The face with a glyph for a character: the font's own, else the first other shared font that has it.
static FT_Face face_for(ObnsFont *font, uint32_t codepoint, FT_UInt *glyph)
{
    FT_Face fallback = NULL;
    for (int slot = 0; slot < FACES; slot++) {
        FT_Face face = face_at(font, slot);
        if (!face) continue;
        if (!fallback) fallback = face;
        FT_UInt g = FT_Get_Char_Index(face, codepoint);
        if (g) { *glyph = g; return face; }
    }
    *glyph = 0;
    return fallback;
}

static uint32_t next_codepoint(const uint16_t *text, int length, int *i)
{
    uint32_t c = text[(*i)++];
    if (c >= 0xD800 && c < 0xDC00 && *i < length && text[*i] >= 0xDC00 && text[*i] < 0xE000)
        c = 0x10000 + ((c - 0xD800) << 10) + (text[(*i)++] - 0xDC00);
    return c;
}

static FT_Error load_glyph(ObnsFont *font, FT_Face face, FT_UInt glyph, bool render)
{
    FT_Error e = FT_Load_Glyph(face, glyph, FT_LOAD_DEFAULT | FT_LOAD_NO_BITMAP);
    if (e) return e;
    if (font->italic) FT_GlyphSlot_Oblique(face->glyph);
    if (font->bold) FT_GlyphSlot_Embolden(face->glyph);
    return render ? FT_Render_Glyph(face->glyph, FT_RENDER_MODE_NORMAL) : 0;
}

EXPORT void obns_font_metrics(ObnsFont *font, float *ascent, float *descent)
{
    FT_Face face = font ? face_at(font, 0) : NULL;
    if (!face) { *ascent = -12; *descent = 3; return; }
    *ascent = -(float)face->size->metrics.ascender / 64.0f;
    *descent = -(float)face->size->metrics.descender / 64.0f;
}

EXPORT float obns_font_measure(ObnsFont *font, const uint16_t *text, int length)
{
    if (!font) return 0;
    float width = 0;
    for (int i = 0; i < length;) {
        uint32_t c = next_codepoint(text, length, &i);
        FT_UInt glyph;
        FT_Face face = face_for(font, c, &glyph);
        if (!face) continue;
        FT_Set_Transform(face, NULL, NULL);
        if (load_glyph(font, face, glyph, false) == 0) width += (float)face->glyph->advance.x / 64.0f;
    }
    return width;
}

static inline void blend(uint8_t *d, uint32_t argb, float coverage)
{
    float alpha = ((argb >> 24) & 0xFF) / 255.0f * coverage;
    if (alpha <= 0) return;
    uint8_t b = argb & 0xFF, g = (argb >> 8) & 0xFF, r = (argb >> 16) & 0xFF;
    if (alpha >= 0.999f) { d[0] = b; d[1] = g; d[2] = r; d[3] = 255; return; }
    float da = d[3] / 255.0f, outA = alpha + da * (1 - alpha), keep = da * (1 - alpha);
    if (outA <= 0) { d[0] = d[1] = d[2] = d[3] = 0; return; }
    d[0] = (uint8_t)fminf(255, (b * alpha + d[0] * keep) / outA + 0.5f);
    d[1] = (uint8_t)fminf(255, (g * alpha + d[1] * keep) / outA + 0.5f);
    d[2] = (uint8_t)fminf(255, (r * alpha + d[2] * keep) / outA + 0.5f);
    d[3] = (uint8_t)fminf(255, outA * 255 + 0.5f);
}

// Each glyph is placed at its pen position mapped through the matrix, and drawn through the matrix's linear part.
// FreeType's y goes up, the bitmap's down: the device point of a glyph point (gx, gy up) is (gx m11 - gy m21,
// gx m12 - gy m22), which in FreeType's terms is the matrix (m11, -m21; -m12, m22).
EXPORT void obns_font_draw(ObnsFont *font, const uint16_t *text, int length, float x, float y, uint32_t argb, const float *m,
                           uint8_t *pixels, int width, int height, int stride, int clipLeft, int clipTop, int clipRight, int clipBottom)
{
    if (!font) return;
    if (clipLeft < 0) clipLeft = 0;
    if (clipTop < 0) clipTop = 0;
    if (clipRight > width) clipRight = width;
    if (clipBottom > height) clipBottom = height;
    FT_Matrix matrix = { (FT_Fixed)lroundf(m[0] * 65536.0f), (FT_Fixed)lroundf(-m[2] * 65536.0f),
                         (FT_Fixed)lroundf(-m[1] * 65536.0f), (FT_Fixed)lroundf(m[3] * 65536.0f) };
    float pen = x;
    for (int i = 0; i < length;) {
        uint32_t c = next_codepoint(text, length, &i);
        FT_UInt glyph;
        FT_Face face = face_for(font, c, &glyph);
        if (!face) continue;
        FT_Set_Transform(face, NULL, NULL);
        if (load_glyph(font, face, glyph, false) != 0) continue;
        float advance = (float)face->glyph->advance.x / 64.0f;
        float ox = pen * m[0] + y * m[2] + m[4], oy = pen * m[1] + y * m[3] + m[5];
        FT_Vector delta = { (FT_Pos)lroundf((ox - floorf(ox)) * 64.0f), (FT_Pos)lroundf(-(oy - floorf(oy)) * 64.0f) };
        FT_Set_Transform(face, &matrix, &delta);
        if (load_glyph(font, face, glyph, true) == 0) {
            FT_Bitmap *bitmap = &face->glyph->bitmap;
            int left = (int)floorf(ox) + face->glyph->bitmap_left, top = (int)floorf(oy) - face->glyph->bitmap_top;
            for (unsigned row = 0; row < bitmap->rows; row++) {
                int py = top + (int)row;
                if (py < clipTop || py >= clipBottom) continue;
                const uint8_t *src = bitmap->buffer + row * bitmap->pitch;
                for (unsigned col = 0; col < bitmap->width; col++) {
                    int px = left + (int)col;
                    if (px < clipLeft || px >= clipRight || !src[col]) continue;
                    blend(pixels + (size_t)py * stride + px * 4, argb, src[col] / 255.0f);
                }
            }
        }
        FT_Set_Transform(face, NULL, NULL);
        pen += advance;
    }
}

EXPORT void obns_font_close(ObnsFont *font)
{
    if (!font) return;
    mutexLock(&fontMutex);
    for (int i = 0; i < FACES; i++) if (font->faces[i]) FT_Done_Face(font->faces[i]);
    mutexUnlock(&fontMutex);
    free(font);
}
