// OBNS 1.31N: deko3d's 2D drawing, included by obns_dk.c (it shares the device, the queue and the swapchain).
// Copyright (c) The OpenBVE Android port contributors. BSD-2-Clause, as OBNS.
//
// The menu, the loading screen and later the HUD (OBNS/SWITCH.md, steps 3 and 6). In the menu's units, 1280x720 from
// the top left, scaled to the framebuffer. Each call adds quads; obns_dk2d_end records the frame: the uploads first (the
// glyph atlas's changed rows, at most one new image a frame), then the quads in batches split where the image or the
// clip changes, and presents. One shader pair (shaders/draw2d.*, compiled by uam at build time): rounded rectangles and
// outlines from a distance function, text from an R8 glyph atlas, images from their own texture.

#include <stddef.h>
#include <stdlib.h>
#include <math.h>
#include "draw2d_vert_dksh.h"
#include "draw2d_frag_dksh.h"

typedef struct {
    const uint8_t *bitmap;
    int width, rows, pitch, left, top;
    float advance;
} ObnsGlyph;

// From obns_drawing.c: the console's shared fonts through FreeType, and the image decoders.
void *obns_font_open(const char *family, int bold, int italic, float pixels);
void obns_font_metrics(void *font, float *ascent, float *descent);
int obns_font_glyph(void *font, uint32_t codepoint, ObnsGlyph *out);
int obns_image_decode(const uint8_t *data, intptr_t length, int *w, int *h, void **pixels);
void obns_image_free(void *pixels);

typedef struct {
    float x, y, u, v;
    float localX, localY, halfWidth, halfHeight;
    float radius, thickness, mode;
    uint32_t color; // 0xAARRGGBB, read as BGRA
} Vertex2D;

#define UNITS_WIDTH 1280.0f
#define UNITS_HEIGHT 720.0f
#define ATLAS 2048
#define MAX_QUADS 16384
#define MAX_BATCHES 2048
#define IMAGES 8
#define FONTS 12
#define GLYPHS 8192
#define STAGING (8 * 1024 * 1024)
#define IMAGE_STAGING (4 * 1024 * 1024)
#define DATA_DESCRIPTORS 256
#define DATA_SAMPLERS (DATA_DESCRIPTORS + (1 + IMAGES) * 32)
#define DATA_VERTICES 4096
#define DATA_SIZE (DATA_VERTICES + MAX_QUADS * 6 * sizeof(Vertex2D))

typedef struct { int first, count, image; DkScissor scissor; } Batch;
typedef struct { char path[512]; DkMemBlock memory; DkImage image; int width, height, failed; uint64_t used; } Image2D;
typedef struct { float pixels; int bold; void *font; float ascent; } Font2D;
typedef struct { void *font; uint32_t codepoint; int16_t x, y, w, h, left, top; float advance; int used; } Glyph2D;

static int ready2d, failed2d;
static DkMemBlock codeMemory, dataMemory, atlasMemory, stagingMemory, uploadMemory;
static DkCmdBuf uploads;
static DkShader vertexShader, fragmentShader;
static DkImage atlas;
static uint8_t *atlasPixels;
static int atlasDirtyTop = ATLAS, atlasDirtyBottom, shelfX, shelfY, shelfHeight, atlasReset;
static Vertex2D *vertices;
static int vertexCount, batchCount, droppedQuads;
static Batch batches[MAX_BATCHES];
static DkScissor clip;
static Image2D images[IMAGES];
static int imagePending = -1;
static Font2D fonts[FONTS];
static Glyph2D glyphs[GLYPHS];
static float unitScale = 1.0f, ySign = 1.0f;
static uint32_t backgroundColor;

static int load_shader(DkShader *shader, const uint8_t *code, uint32_t size, uint32_t *offset)
{
    memcpy((uint8_t *)dkMemBlockGetCpuAddr(codeMemory) + *offset, code, size);
    DkShaderMaker maker;
    dkShaderMakerDefaults(&maker, codeMemory, *offset);
    dkShaderInitialize(shader, &maker);
    *offset = align_up(*offset + size, DK_SHADER_CODE_ALIGNMENT);
    return dkShaderIsValid(shader) ? 0 : 1;
}

static void write_descriptor(int slot, DkImage *image)
{
    DkImageView view;
    dkImageViewDefaults(&view, image);
    DkImageDescriptor descriptor;
    dkImageDescriptorInitialize(&descriptor, &view, false, false);
    memcpy((uint8_t *)dkMemBlockGetCpuAddr(dataMemory) + DATA_DESCRIPTORS + slot * 32, &descriptor, sizeof descriptor);
}

static int init2d(void)
{
    if (ready2d || failed2d)
        return ready2d;
    failed2d = 1;
    uint32_t codeSize = align_up(align_up(draw2d_vert_dksh_size, DK_SHADER_CODE_ALIGNMENT) +
        align_up(draw2d_frag_dksh_size, DK_SHADER_CODE_ALIGNMENT) + DK_SHADER_CODE_UNUSABLE_SIZE, DK_MEMBLOCK_ALIGNMENT);
    DkMemBlockMaker maker;
    dkMemBlockMakerDefaults(&maker, device, codeSize);
    maker.flags = DkMemBlockFlags_CpuUncached | DkMemBlockFlags_GpuCached | DkMemBlockFlags_Code;
    codeMemory = dkMemBlockCreate(&maker);
    uint32_t offset = 0;
    if (!codeMemory || load_shader(&vertexShader, draw2d_vert_dksh, draw2d_vert_dksh_size, &offset) ||
        load_shader(&fragmentShader, draw2d_frag_dksh, draw2d_frag_dksh_size, &offset)) {
        printf("deko3d 2D: the shaders did not load\n");
        return 0;
    }

    dkMemBlockMakerDefaults(&maker, device, align_up(DATA_SIZE, DK_MEMBLOCK_ALIGNMENT));
    dataMemory = dkMemBlockCreate(&maker);
    dkMemBlockMakerDefaults(&maker, device, STAGING);
    stagingMemory = dkMemBlockCreate(&maker);
    dkMemBlockMakerDefaults(&maker, device, COMMAND_MEMORY);
    uploadMemory = dkMemBlockCreate(&maker);
    DkCmdBufMaker commandsMaker;
    dkCmdBufMakerDefaults(&commandsMaker, device);
    uploads = dkCmdBufCreate(&commandsMaker);

    DkImageLayoutMaker layoutMaker;
    dkImageLayoutMakerDefaults(&layoutMaker, device);
    layoutMaker.format = DkImageFormat_R8_Unorm;
    layoutMaker.dimensions[0] = ATLAS;
    layoutMaker.dimensions[1] = ATLAS;
    DkImageLayout layout;
    dkImageLayoutInitialize(&layout, &layoutMaker);
    dkMemBlockMakerDefaults(&maker, device, align_up((uint32_t)dkImageLayoutGetSize(&layout), DK_MEMBLOCK_ALIGNMENT));
    maker.flags = DkMemBlockFlags_GpuCached | DkMemBlockFlags_Image;
    atlasMemory = dkMemBlockCreate(&maker);
    atlasPixels = calloc(ATLAS, ATLAS);
    if (!dataMemory || !stagingMemory || !uploadMemory || !uploads || !atlasMemory || !atlasPixels) {
        printf("deko3d 2D: out of memory\n");
        return 0;
    }

    dkImageInitialize(&atlas, &layout, atlasMemory, 0);
    // Every descriptor slot starts as the atlas, so nothing ever samples an empty one; slot 0 stays the atlas.
    for (int i = 0; i <= IMAGES; i++)
        write_descriptor(i, &atlas);
    DkSampler sampler;
    dkSamplerDefaults(&sampler);
    sampler.minFilter = DkFilter_Linear;
    sampler.magFilter = DkFilter_Linear;
    sampler.wrapMode[0] = sampler.wrapMode[1] = sampler.wrapMode[2] = DkWrapMode_ClampToEdge;
    DkSamplerDescriptor samplerDescriptor;
    dkSamplerDescriptorInitialize(&samplerDescriptor, &sampler);
    memcpy((uint8_t *)dkMemBlockGetCpuAddr(dataMemory) + DATA_SAMPLERS, &samplerDescriptor, sizeof samplerDescriptor);
    vertices = (Vertex2D *)((uint8_t *)dkMemBlockGetCpuAddr(dataMemory) + DATA_VERTICES);
    // The whole atlas is uploaded once (zeros), so its never-written parts read as nothing.
    atlasDirtyTop = 0;
    atlasDirtyBottom = ATLAS;
    ready2d = 1;
    failed2d = 0;
    printf("deko3d 2D: ready (shaders %u + %u bytes, atlas %dx%d)\n", (unsigned)draw2d_vert_dksh_size, (unsigned)draw2d_frag_dksh_size, ATLAS, ATLAS);
    return 1;
}

static void close2d(void)
{
    if (!ready2d)
        return;
    for (int i = 0; i < IMAGES; i++)
        if (images[i].memory) dkMemBlockDestroy(images[i].memory);
    memset(images, 0, sizeof images);
    dkCmdBufDestroy(uploads);
    dkMemBlockDestroy(uploadMemory);
    dkMemBlockDestroy(stagingMemory);
    dkMemBlockDestroy(atlasMemory);
    dkMemBlockDestroy(dataMemory);
    dkMemBlockDestroy(codeMemory);
    free(atlasPixels);
    atlasPixels = NULL;
    memset(glyphs, 0, sizeof glyphs);
    memset(fonts, 0, sizeof fonts);
    ready2d = 0;
}

// The way Y points in clip space (-1: down, as Vulkan; +1: up, as OpenGL), should the first frame be upside down.
EXPORT void obns_dk2d_set_y(float sign) { ySign = sign < 0 ? -1.0f : 1.0f; }

static DkScissor whole_screen(void) { DkScissor s = { 0, 0, (uint32_t)width, (uint32_t)height }; return s; }

static void wait_frames3d(void); // obns_dk3d.inc

EXPORT int obns_dk2d_begin(uint32_t background)
{
    if (!device || !init2d())
        return 1;
    // The quads are written straight into the GPU's memory, which the frame before (still drawing, with frames in
    // flight) reads its own from.
    wait_frames3d();
    unitScale = width / UNITS_WIDTH;
    backgroundColor = background;
    vertexCount = batchCount = droppedQuads = 0;
    clip = whole_screen();
    if (atlasReset) {
        // The atlas filled up last frame: start it again (this frame's text is laid out anew).
        memset(glyphs, 0, sizeof glyphs);
        memset(atlasPixels, 0, ATLAS * ATLAS);
        shelfX = shelfY = shelfHeight = 0;
        atlasDirtyTop = 0;
        atlasDirtyBottom = ATLAS;
        atlasReset = 0;
    }
    return 0;
}

static void quad(float x, float y, float w, float h, float u0, float v0, float u1, float v1, float radius, float thickness, float mode, uint32_t color, int image)
{
    if (!ready2d || w <= 0 || h <= 0)
        return;
    if (vertexCount + 6 > MAX_QUADS * 6) { droppedQuads++; return; }
    Batch *last = batchCount ? &batches[batchCount - 1] : NULL;
    if (!last || last->image != image || memcmp(&last->scissor, &clip, sizeof clip) != 0) {
        if (batchCount == MAX_BATCHES) { droppedQuads++; return; }
        last = &batches[batchCount++];
        last->first = vertexCount;
        last->count = 0;
        last->image = image;
        last->scissor = clip;
    }
    float hw = w * 0.5f, hh = h * 0.5f;
    Vertex2D corners[4] = {
        { x,     y,     u0, v0, -hw, -hh, hw, hh, radius, thickness, mode, color },
        { x + w, y,     u1, v0,  hw, -hh, hw, hh, radius, thickness, mode, color },
        { x,     y + h, u0, v1, -hw,  hh, hw, hh, radius, thickness, mode, color },
        { x + w, y + h, u1, v1,  hw,  hh, hw, hh, radius, thickness, mode, color },
    };
    Vertex2D *v = vertices + vertexCount;
    v[0] = corners[0]; v[1] = corners[1]; v[2] = corners[2];
    v[3] = corners[2]; v[4] = corners[1]; v[5] = corners[3];
    vertexCount += 6;
    last->count += 6;
}

EXPORT void obns_dk2d_fill(float x, float y, float w, float h, uint32_t color, float radius)
{
    float most = fminf(w, h) * 0.5f;
    quad(x, y, w, h, 0, 0, 0, 0, radius < most ? radius : most, 0, 0, color, 0);
}

EXPORT void obns_dk2d_outline(float x, float y, float w, float h, uint32_t color, float thickness, float radius)
{
    float most = fminf(w, h) * 0.5f;
    quad(x, y, w, h, 0, 0, 0, 0, radius < most ? radius : most, thickness > 0 ? thickness : 1, 0, color, 0);
}

EXPORT void obns_dk2d_clip(float x, float y, float w, float h)
{
    float x0 = fmaxf(0, x * unitScale), y0 = fmaxf(0, y * unitScale);
    float x1 = fminf((float)width, (x + w) * unitScale), y1 = fminf((float)height, (y + h) * unitScale);
    DkScissor s = { (uint32_t)x0, (uint32_t)y0, (uint32_t)fmaxf(0, x1 - x0), (uint32_t)fmaxf(0, y1 - y0) };
    clip = s;
}

EXPORT void obns_dk2d_unclip(void) { clip = whole_screen(); }

// --- text ---

static Font2D *font_for(float pixels, int bold)
{
    int empty = -1;
    for (int i = 0; i < FONTS; i++) {
        if (fonts[i].font && fonts[i].pixels == pixels && fonts[i].bold == bold) return &fonts[i];
        if (!fonts[i].font && empty < 0) empty = i;
    }
    if (empty < 0) empty = 0; // more sizes than slots: the first is replaced (its glyphs stay cached under its old handle)
    void *font = obns_font_open(NULL, bold, 0, pixels);
    if (!font) return NULL;
    float ascent, descent;
    obns_font_metrics(font, &ascent, &descent);
    fonts[empty].font = font;
    fonts[empty].pixels = pixels;
    fonts[empty].bold = bold;
    fonts[empty].ascent = -ascent;
    return &fonts[empty];
}

static Glyph2D *glyph_for(Font2D *font, uint32_t codepoint)
{
    uint32_t hash = (uint32_t)(((uintptr_t)font->font >> 4) * 2654435761u) ^ (codepoint * 40503u);
    for (int probe = 0; probe < 32; probe++) {
        Glyph2D *g = &glyphs[(hash + probe) & (GLYPHS - 1)];
        if (g->used && g->font == font->font && g->codepoint == codepoint) return g;
        if (g->used) continue;
        ObnsGlyph rendered;
        if (!obns_font_glyph(font->font, codepoint, &rendered)) return NULL;
        int w = rendered.width, h = rendered.rows;
        if (shelfX + w + 1 > ATLAS) { shelfX = 0; shelfY += shelfHeight + 1; shelfHeight = 0; }
        if (shelfY + h + 1 > ATLAS) { atlasReset = 1; return NULL; }
        for (int row = 0; row < h; row++)
            memcpy(atlasPixels + (size_t)(shelfY + row) * ATLAS + shelfX, rendered.bitmap + (ptrdiff_t)row * rendered.pitch, (size_t)w);
        if (h > 0) {
            if (shelfY < atlasDirtyTop) atlasDirtyTop = shelfY;
            if (shelfY + h > atlasDirtyBottom) atlasDirtyBottom = shelfY + h;
        }
        g->used = 1;
        g->font = font->font;
        g->codepoint = codepoint;
        g->x = (int16_t)shelfX; g->y = (int16_t)shelfY; g->w = (int16_t)w; g->h = (int16_t)h;
        g->left = (int16_t)rendered.left; g->top = (int16_t)rendered.top;
        g->advance = rendered.advance;
        shelfX += w + 1;
        if (h > shelfHeight) shelfHeight = h;
        return g;
    }
    return NULL;
}

static uint32_t next_char(const uint16_t *text, int length, int *i)
{
    uint32_t c = text[(*i)++];
    if (c >= 0xD800 && c < 0xDC00 && *i < length && text[*i] >= 0xDC00 && text[*i] < 0xE000)
        c = 0x10000 + ((c - 0xD800) << 10) + (text[(*i)++] - 0xDC00);
    return c;
}

// One line of text (UTF-16), its top at y, size units high. Glyphs are rendered at the framebuffer's pixel size.
EXPORT void obns_dk2d_text(const uint16_t *text, int length, float x, float y, float size, uint32_t color, int bold)
{
    if (!ready2d) return;
    Font2D *font = font_for(roundf(size * unitScale), bold);
    if (!font) return;
    float pen = x * unitScale, baseline = roundf(y * unitScale + font->ascent);
    for (int i = 0; i < length;) {
        Glyph2D *g = glyph_for(font, next_char(text, length, &i));
        if (!g) continue;
        if (g->w > 0 && g->h > 0) {
            float gx = roundf(pen) + g->left, gy = baseline - g->top;
            quad(gx / unitScale, gy / unitScale, g->w / unitScale, g->h / unitScale,
                 g->x / (float)ATLAS, g->y / (float)ATLAS, (g->x + g->w) / (float)ATLAS, (g->y + g->h) / (float)ATLAS, 0, 0, 1, color, 0);
        }
        pen += g->advance;
    }
}

EXPORT float obns_dk2d_text_width(const uint16_t *text, int length, float size, int bold)
{
    if (!ready2d) return 0;
    Font2D *font = font_for(roundf(size * unitScale), bold);
    if (!font) return 0;
    float pen = 0;
    for (int i = 0; i < length;) {
        Glyph2D *g = glyph_for(font, next_char(text, length, &i));
        if (g) pen += g->advance;
    }
    return pen / unitScale;
}

// --- images ---

static int load_image(int slot, const char *path)
{
    Image2D *image = &images[slot];
    memset(image, 0, sizeof *image);
    snprintf(image->path, sizeof image->path, "%s", path);
    image->failed = 1;
    FILE *file = fopen(path, "rb");
    if (!file) return 0;
    fseek(file, 0, SEEK_END);
    long length = ftell(file);
    fseek(file, 0, SEEK_SET);
    uint8_t *data = length > 0 && length < 64 * 1024 * 1024 ? malloc((size_t)length) : NULL;
    int read = data && fread(data, 1, (size_t)length, file) == (size_t)length;
    fclose(file);
    int w = 0, h = 0;
    void *pixels = NULL;
    if (!read || obns_image_decode(data, length, &w, &h, &pixels) != 0) { free(data); return 0; }
    free(data);
    // Halved until it fits the staging area (the menu's pictures are small; this bounds a large one).
    uint8_t *source = pixels;
    int step = 1;
    while ((size_t)(w / step) * (h / step) * 4 > IMAGE_STAGING) step *= 2;
    int sw = w / step, sh = h / step;
    if (sw < 1 || sh < 1) { obns_image_free(pixels); return 0; }
    uint8_t *staging = (uint8_t *)dkMemBlockGetCpuAddr(stagingMemory) + (STAGING - IMAGE_STAGING);
    for (int row = 0; row < sh; row++)
        for (int column = 0; column < sw; column++)
            memcpy(staging + ((size_t)row * sw + column) * 4, source + ((size_t)row * step * w + (size_t)column * step) * 4, 4);
    obns_image_free(pixels);

    DkImageLayoutMaker layoutMaker;
    dkImageLayoutMakerDefaults(&layoutMaker, device);
    layoutMaker.format = DkImageFormat_BGRA8_Unorm;
    layoutMaker.dimensions[0] = (uint32_t)sw;
    layoutMaker.dimensions[1] = (uint32_t)sh;
    DkImageLayout layout;
    dkImageLayoutInitialize(&layout, &layoutMaker);
    DkMemBlockMaker maker;
    dkMemBlockMakerDefaults(&maker, device, align_up((uint32_t)dkImageLayoutGetSize(&layout), DK_MEMBLOCK_ALIGNMENT));
    maker.flags = DkMemBlockFlags_GpuCached | DkMemBlockFlags_Image;
    QUEUE_LOCK();
    image->memory = dkMemBlockCreate(&maker);
    QUEUE_UNLOCK();
    if (!image->memory) return 0;
    dkImageInitialize(&image->image, &layout, image->memory, 0);
    image->width = sw;
    image->height = sh;
    image->failed = 0;
    write_descriptor(1 + slot, &image->image);
    imagePending = slot;
    return 1;
}

// An image file in the rectangle, its proportions kept: fitted inside it, or (cover) filling it with the overflow
// cropped, as Android's CenterCrop; tint multiplies it (0xAARRGGBB: 0x8CFFFFFF is 55% opaque). 0 while it isn't there
// yet (one loads a frame) or can't be read, when the caller draws a placeholder.
EXPORT int obns_dk2d_image_ex(const char *path, float x, float y, float w, float h, int cover, uint32_t tint)
{
    if (!ready2d || !path || !*path) return 0;
    int slot = -1, oldest = 0;
    for (int i = 0; i < IMAGES; i++) {
        if (images[i].path[0] && strcmp(images[i].path, path) == 0) { slot = i; break; }
        if (images[i].used < images[oldest].used) oldest = i;
    }
    if (slot < 0) {
        if (imagePending >= 0) return 0;
        // The least recently used goes (the GPU is idle between frames, so its memory can be let go of now).
        if (images[oldest].memory) {
            QUEUE_LOCK();
            dkMemBlockDestroy(images[oldest].memory);
            QUEUE_UNLOCK();
        }
        write_descriptor(1 + oldest, &atlas);
        load_image(oldest, path);
        slot = oldest;
    }
    Image2D *image = &images[slot];
    image->used = frames + 1;
    if (image->failed) return 0;
    if (cover) {
        // The part of the image with the rectangle's proportions, from its middle.
        float scale = fmaxf(w / image->width, h / image->height);
        float u = (w / scale) / image->width, v = (h / scale) / image->height;
        quad(x, y, w, h, (1 - u) * 0.5f, (1 - v) * 0.5f, (1 + u) * 0.5f, (1 + v) * 0.5f, 0, 0, 2, tint, 1 + slot);
        return 1;
    }
    float scale = fminf(w / image->width, h / image->height);
    float dw = image->width * scale, dh = image->height * scale;
    quad(x + (w - dw) * 0.5f, y + (h - dh) * 0.5f, dw, dh, 0, 0, 1, 1, 0, 0, 2, tint, 1 + slot);
    return 1;
}

EXPORT int obns_dk2d_image(const char *path, float x, float y, float w, float h)
{
    return obns_dk2d_image_ex(path, x, y, w, h, 0, 0xFFFFFFFF);
}

// --- the frame ---

// The uploads: the atlas's changed rows and the new image, submitted ahead of the drawing that reads them. The queue's
// lock is held by the caller.
static void submit_uploads2d(void)
{
    dkCmdBufClear(uploads);
    dkCmdBufAddMemory(uploads, uploadMemory, 0, COMMAND_MEMORY);
    int uploaded = 0;
    if (atlasDirtyBottom > atlasDirtyTop) {
        int rows = atlasDirtyBottom - atlasDirtyTop;
        memcpy(dkMemBlockGetCpuAddr(stagingMemory), atlasPixels + (size_t)atlasDirtyTop * ATLAS, (size_t)rows * ATLAS);
        DkImageView view;
        dkImageViewDefaults(&view, &atlas);
        DkCopyBuf source = { dkMemBlockGetGpuAddr(stagingMemory), 0, 0 };
        DkImageRect rect = { 0, (uint32_t)atlasDirtyTop, 0, ATLAS, (uint32_t)rows, 1 };
        dkCmdBufCopyBufferToImage(uploads, &source, &view, &rect, 0);
        atlasDirtyTop = ATLAS;
        atlasDirtyBottom = 0;
        uploaded = 1;
    }
    if (imagePending >= 0) {
        Image2D *image = &images[imagePending];
        DkImageView view;
        dkImageViewDefaults(&view, &image->image);
        DkCopyBuf source = { dkMemBlockGetGpuAddr(stagingMemory) + (STAGING - IMAGE_STAGING), 0, 0 };
        DkImageRect rect = { 0, 0, 0, (uint32_t)image->width, (uint32_t)image->height, 1 };
        dkCmdBufCopyBufferToImage(uploads, &source, &view, &rect, 0);
        imagePending = -1;
        uploaded = 1;
    }
    if (uploaded)
        dkQueueSubmitCommands(queue, dkCmdBufFinishList(uploads));
}

// The frame's quads into a command list whose render target is bound: the 2D state, then the batches.
static void record2d(DkCmdBuf cmd)
{
    // What the uploads wrote, and the descriptors the CPU wrote, seen by the drawing.
    dkCmdBufBarrier(cmd, DkBarrier_Full, DkInvalidateFlags_Image | DkInvalidateFlags_Descriptors | DkInvalidateFlags_L2Cache);
    DkViewport viewport = { 0, 0, (float)width, (float)height, 0.0f, 1.0f };
    dkCmdBufSetViewports(cmd, 0, &viewport, 1);
    DkShader const *shaders[] = { &vertexShader, &fragmentShader };
    dkCmdBufBindShaders(cmd, DkStageFlag_GraphicsMask, shaders, 2);
    DkRasterizerState rasterizer;
    dkRasterizerStateDefaults(&rasterizer);
    rasterizer.cullMode = DkFace_None;
    dkCmdBufBindRasterizerState(cmd, &rasterizer);
    DkColorState color;
    dkColorStateDefaults(&color);
    dkColorStateSetBlendEnable(&color, 0, true);
    dkCmdBufBindColorState(cmd, &color);
    DkColorWriteState colorWrite;
    dkColorWriteStateDefaults(&colorWrite);
    dkCmdBufBindColorWriteState(cmd, &colorWrite);
    DkBlendState blend;
    dkBlendStateDefaults(&blend);
    dkBlendStateSetFactors(&blend, DkBlendFactor_SrcAlpha, DkBlendFactor_InvSrcAlpha, DkBlendFactor_One, DkBlendFactor_InvSrcAlpha);
    dkCmdBufBindBlendState(cmd, 0, &blend);
    DkDepthStencilState depth;
    dkDepthStencilStateDefaults(&depth);
    depth.depthTestEnable = false;
    depth.depthWriteEnable = false;
    dkCmdBufBindDepthStencilState(cmd, &depth);

    DkVtxAttribState attributes[] = {
        { .bufferId = 0, .isFixed = 0, .offset = offsetof(Vertex2D, x), .size = DkVtxAttribSize_2x32, .type = DkVtxAttribType_Float, .isBgra = 0 },
        { .bufferId = 0, .isFixed = 0, .offset = offsetof(Vertex2D, u), .size = DkVtxAttribSize_2x32, .type = DkVtxAttribType_Float, .isBgra = 0 },
        { .bufferId = 0, .isFixed = 0, .offset = offsetof(Vertex2D, localX), .size = DkVtxAttribSize_4x32, .type = DkVtxAttribType_Float, .isBgra = 0 },
        { .bufferId = 0, .isFixed = 0, .offset = offsetof(Vertex2D, radius), .size = DkVtxAttribSize_3x32, .type = DkVtxAttribType_Float, .isBgra = 0 },
        { .bufferId = 0, .isFixed = 0, .offset = offsetof(Vertex2D, color), .size = DkVtxAttribSize_4x8, .type = DkVtxAttribType_Unorm, .isBgra = 1 },
    };
    dkCmdBufBindVtxAttribState(cmd, attributes, 5);
    DkVtxBufferState buffer = { sizeof(Vertex2D), 0 };
    dkCmdBufBindVtxBufferState(cmd, &buffer, 1);
    DkGpuAddr data = dkMemBlockGetGpuAddr(dataMemory);
    dkCmdBufBindVtxBuffer(cmd, 0, data + DATA_VERTICES, (uint32_t)(vertexCount * sizeof(Vertex2D)) + 1);

    float transform[4] = { 2.0f / UNITS_WIDTH, 2.0f / UNITS_HEIGHT, ySign, 0.0f };
    memcpy(dkMemBlockGetCpuAddr(dataMemory), transform, sizeof transform);
    dkCmdBufBindUniformBuffer(cmd, DkStage_Vertex, 0, data, 256);
    dkCmdBufBindImageDescriptorSet(cmd, data + DATA_DESCRIPTORS, 1 + IMAGES);
    dkCmdBufBindSamplerDescriptorSet(cmd, data + DATA_SAMPLERS, 1);
    dkCmdBufBindTexture(cmd, DkStage_Fragment, 0, dkMakeTextureHandle(0, 0));

    int boundImage = -1;
    for (int i = 0; i < batchCount; i++) {
        Batch *b = &batches[i];
        if (b->count == 0 || b->scissor.width == 0 || b->scissor.height == 0) continue;
        dkCmdBufSetScissors(cmd, 0, &b->scissor, 1);
        if (b->image != boundImage) {
            dkCmdBufBindTexture(cmd, DkStage_Fragment, 1, dkMakeTextureHandle((uint32_t)b->image, 0));
            boundImage = b->image;
        }
        dkCmdBufDraw(cmd, DkPrimitive_Triangles, (uint32_t)b->count, 1, (uint32_t)b->first, 0);
    }

    DkScissor all = whole_screen();
    dkCmdBufSetScissors(cmd, 0, &all, 1);
    if (droppedQuads)
        printf("deko3d 2D: %d quads over the frame's limit were left out\n", droppedQuads);
}

// A frame of its own (the menu, the loading screen): cleared to the background, the quads, presented.
EXPORT int obns_dk2d_end(void)
{
    if (!ready2d || dkQueueIsInErrorState(queue))
        return 1;

    QUEUE_LOCK();
    submit_uploads2d();
    int slot = dkQueueAcquireImage(queue, swapchain);
    dkCmdBufClear(commands);
    dkCmdBufAddMemory(commands, commandMemory, 0, COMMAND_MEMORY);
    DkImageView target;
    dkImageViewDefaults(&target, &framebuffers[slot]);
    dkCmdBufBindRenderTarget(commands, &target, NULL);
    DkScissor all = whole_screen();
    dkCmdBufSetScissors(commands, 0, &all, 1);
    dkCmdBufClearColorFloat(commands, 0, DkColorMask_RGBA, ((backgroundColor >> 16) & 255) / 255.0f,
        ((backgroundColor >> 8) & 255) / 255.0f, (backgroundColor & 255) / 255.0f, 1.0f);
    record2d(commands);
    dkQueueSubmitCommands(queue, dkCmdBufFinishList(commands));
    dkQueuePresentImage(queue, swapchain, slot);
    dkQueueWaitIdle(queue);
    QUEUE_UNLOCK();
    if (frames++ == 0)
        printf("deko3d: first frame presented\n");
    return dkQueueIsInErrorState(queue) ? 2 : 0;
}
