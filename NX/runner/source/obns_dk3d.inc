// OBNS 1.32: deko3d's 3D drawing for the renderer (NX/OpenBve.NX/Deko/DekoGL, the GL calls of upstream's renderer on
// deko3d, as Android's VulkanGL does them on Vulkan). Included by obns_dk.c: it shares the device, the queue and the
// swapchain. Copyright (c) The OpenBVE Android port contributors. BSD-2-Clause, as OBNS.
//
// What lives here: the memory blocks DekoGL suballocates, the textures (images by descriptor slot, uploads and mipmaps
// recorded before the frame), the samplers, the depth buffer, and the frame: begin (acquire, uploads first), clears,
// draws (one call each, a blittable Draw3D: the state applied where it changed), end (submit, present). C# keeps GL's
// objects and state; C only does what deko3d is needed for. The programs are Android's native Vulkan shaders, compiled
// by uam at build time (NX/build-nro.sh).

#include "default_vert_dksh.h"
#include "default_frag_dksh.h"
#include "rectangle_vert_dksh.h"
#include "rectangle_frag_dksh.h"
#include "text_vert_dksh.h"
#include "world_vert_dksh.h"
#include "world_frag_dksh.h"

#define TEXTURES3D 32768
#define SAMPLERS3D 256
#define FRAME_COMMANDS (24 * 1024 * 1024) // per frame in flight (a 10,900-draw frame records about 4 MB)
#define UPLOAD_COMMANDS (4 * 1024 * 1024)
#define UPLOADS_BEFORE_FLUSH 20000

// One draw, as DekoGL hands it over (Deko/DekoNative.cs's Draw3D: the same layout).
typedef struct {
    uint64_t vertexAddress[5];
    uint32_t vertexSize[5];
    uint32_t vertexStride[5];
    uint32_t attribute[5];      // DkVtxAttribSize | DkVtxAttribType << 8; 0: the attribute isn't read
    uint32_t attributeCount;    // 5 for the default program, 0 for the 2D ones
    uint64_t indexAddress;
    uint32_t indexFormat;       // DkIdxFormat + 1; 0: not indexed
    uint32_t primitive;         // DkPrimitive
    uint32_t first;
    uint32_t count;
    int32_t vertexOffset;
    uint64_t blockAddress;      // the program's uniform block: binding 0, both stages
    uint32_t blockSize;
    uint64_t matricesAddress;   // the animation matrices: binding 1, vertex stage
    uint32_t matricesSize;
    uint32_t program;           // 0 default, 1 rectangle, 2 text
    uint32_t texture;           // image descriptor slot
    uint32_t sampler;           // sampler descriptor slot
    uint32_t depthTest, depthWrite, depthCompare;     // DkCompareOp
    uint32_t blend, blendSource, blendDestination;    // DkBlendFactor
    uint32_t cull;              // DkFace
    int32_t viewport[4];        // x, y (from the top left), width, height
} Draw3D;

static int ready3d, failed3d, inFrame3d, frameSlot3d, uploadsPending;
static DkMemBlock code3d, descriptors3d, commands3dMemory, uploads3dMemory, depthMemory, clipMemory;
static DkCmdBuf commands3d, uploads3d;
static DkShader shaders3d[7]; // default vert, default frag, rectangle vert, rectangle frag, text vert, world vert, world frag
static DkImage textures3d[TEXTURES3D];
static DkImage depthImage;
static Draw3D last3d;
static int lastValid3d;
static int worldValid; // the world records' state is what is bound (see obns_dkw_draws)
static uint64_t frames3d, draws3d;

// Frames in flight (OBNS/SWITCH.md, step 4: the CPU and the GPU at once): the CPU records a frame while the GPU draws the
// one before. Each frame has its half of the command memory and a fence; the end of a frame waits for the one before it,
// so when it returns everything older is done (DekoGL frees what those frames used then, and writes the ring's other
// half). 0 (UserData/nx-serial-frames.txt) waits for each frame instead, as before.
static int framesInFlight = 1;
static int commandHalf;          // the half the frame being recorded uses
static DkFence frameFences[2];
static int fenceSignaled[2];

// Waits for every frame submitted so far: before the CPU writes memory a frame still drawing reads (the 2D layer's).
static void wait_frames3d(void)
{
    for (int i = 0; i < 2; i++) {
        if (fenceSignaled[i]) {
            dkFenceWait(&frameFences[i], -1);
            fenceSignaled[i] = 0;
        }
    }
}

EXPORT void obns_dk3d_frames_in_flight(int enabled)
{
    framesInFlight = enabled != 0;
}

#define DESCRIPTOR_IMAGES 0
#define DESCRIPTOR_SAMPLERS (TEXTURES3D * 32)

// A new upload list. It starts with a full barrier: with frames in flight the frame before may still be drawing when it
// runs, sampling an image an upload writes again (an animated texture's frames).
static void start_uploads(void)
{
    dkCmdBufClear(uploads3d);
    dkCmdBufAddMemory(uploads3d, uploads3dMemory, 0, UPLOAD_COMMANDS);
    dkCmdBufBarrier(uploads3d, DkBarrier_Full, DkInvalidateFlags_Image | DkInvalidateFlags_L2Cache);
}

static int init3d(void)
{
    if (ready3d || failed3d)
        return ready3d;
    failed3d = 1;
    if (!device) {
        printf("deko3d 3D: no device\n");
        return 0;
    }

    const uint8_t *code[7] = { default_vert_dksh, default_frag_dksh, rectangle_vert_dksh, rectangle_frag_dksh, text_vert_dksh, world_vert_dksh, world_frag_dksh };
    uint32_t sizes[7] = { default_vert_dksh_size, default_frag_dksh_size, rectangle_vert_dksh_size, rectangle_frag_dksh_size, text_vert_dksh_size,
                          world_vert_dksh_size, world_frag_dksh_size };
    uint32_t codeSize = DK_SHADER_CODE_UNUSABLE_SIZE;
    for (int i = 0; i < 7; i++)
        codeSize += align_up(sizes[i], DK_SHADER_CODE_ALIGNMENT);
    DkMemBlockMaker maker;
    dkMemBlockMakerDefaults(&maker, device, align_up(codeSize, DK_MEMBLOCK_ALIGNMENT));
    maker.flags = DkMemBlockFlags_CpuUncached | DkMemBlockFlags_GpuCached | DkMemBlockFlags_Code;
    code3d = dkMemBlockCreate(&maker);
    if (!code3d) return 0;
    uint32_t offset = 0;
    for (int i = 0; i < 7; i++) {
        memcpy((uint8_t *)dkMemBlockGetCpuAddr(code3d) + offset, code[i], sizes[i]);
        DkShaderMaker shaderMaker;
        dkShaderMakerDefaults(&shaderMaker, code3d, offset);
        dkShaderInitialize(&shaders3d[i], &shaderMaker);
        if (!dkShaderIsValid(&shaders3d[i])) {
            printf("deko3d 3D: shader %d did not load\n", i);
            return 0;
        }
        offset = align_up(offset + sizes[i], DK_SHADER_CODE_ALIGNMENT);
    }

    dkMemBlockMakerDefaults(&maker, device, align_up(DESCRIPTOR_SAMPLERS + SAMPLERS3D * 32, DK_MEMBLOCK_ALIGNMENT));
    descriptors3d = dkMemBlockCreate(&maker);
    dkMemBlockMakerDefaults(&maker, device, 2 * FRAME_COMMANDS);
    commands3dMemory = dkMemBlockCreate(&maker);
    dkMemBlockMakerDefaults(&maker, device, UPLOAD_COMMANDS);
    uploads3dMemory = dkMemBlockCreate(&maker);
    dkMemBlockMakerDefaults(&maker, device, DK_MEMBLOCK_ALIGNMENT);
    clipMemory = dkMemBlockCreate(&maker);
    DkCmdBufMaker commandsMaker;
    dkCmdBufMakerDefaults(&commandsMaker, device);
    commands3d = dkCmdBufCreate(&commandsMaker);
    uploads3d = dkCmdBufCreate(&commandsMaker);

    DkImageLayoutMaker layoutMaker;
    dkImageLayoutMakerDefaults(&layoutMaker, device);
    layoutMaker.flags = DkImageFlags_UsageRender | DkImageFlags_HwCompression;
    layoutMaker.format = DkImageFormat_Z24S8;
    layoutMaker.dimensions[0] = (uint32_t)width;
    layoutMaker.dimensions[1] = (uint32_t)height;
    DkImageLayout layout;
    dkImageLayoutInitialize(&layout, &layoutMaker);
    dkMemBlockMakerDefaults(&maker, device, align_up((uint32_t)dkImageLayoutGetSize(&layout), DK_MEMBLOCK_ALIGNMENT));
    maker.flags = DkMemBlockFlags_GpuCached | DkMemBlockFlags_Image;
    depthMemory = dkMemBlockCreate(&maker);
    if (!descriptors3d || !commands3dMemory || !uploads3dMemory || !clipMemory || !commands3d || !uploads3d || !depthMemory) {
        printf("deko3d 3D: out of memory\n");
        return 0;
    }
    dkImageInitialize(&depthImage, &layout, depthMemory, 0);
    memset(dkMemBlockGetCpuAddr(descriptors3d), 0, DESCRIPTOR_SAMPLERS + SAMPLERS3D * 32);
    // GL's clip space is deko3d's as it stands (its +1 at the top of the screen: measured on the console, 2026-11-06).
    float clip[4] = { 1.0f, 0.0f, 0.0f, 1.0f };
    memcpy(dkMemBlockGetCpuAddr(clipMemory), clip, sizeof clip);
    start_uploads();
    ready3d = 1;
    failed3d = 0;
    printf("deko3d 3D: ready (%d texture slots, depth %dx%d)\n", TEXTURES3D, width, height);
    return 1;
}

// The clip transform (x' = X*x + Y*y, y' = Z*x + W*y), should the first frames show the world upside down.
EXPORT void obns_dk3d_set_clip(float x, float y, float z, float w)
{
    if (!init3d()) return;
    float clip[4] = { x, y, z, w };
    memcpy(dkMemBlockGetCpuAddr(clipMemory), clip, sizeof clip);
}

// --- memory ---

// A memory block for DekoGL to suballocate: image 0, buffers (the CPU writes, uncached: write-combined), 1, images.
EXPORT void *obns_dk3d_memory(uint32_t size, int image, void **cpu, uint64_t *gpu)
{
    if (!device) return NULL;
    DkMemBlockMaker maker;
    dkMemBlockMakerDefaults(&maker, device, align_up(size, DK_MEMBLOCK_ALIGNMENT));
    if (image) maker.flags = DkMemBlockFlags_GpuCached | DkMemBlockFlags_Image;
    QUEUE_LOCK();
    DkMemBlock block = dkMemBlockCreate(&maker);
    QUEUE_UNLOCK();
    if (!block) return NULL;
    *cpu = image ? NULL : dkMemBlockGetCpuAddr(block);
    *gpu = dkMemBlockGetGpuAddr(block);
    return block;
}

EXPORT void obns_dk3d_memory_free(void *block)
{
    if (!block) return;
    QUEUE_LOCK();
    dkMemBlockDestroy((DkMemBlock)block);
    QUEUE_UNLOCK();
}

// --- textures ---

// Always waited for: the list's command memory is recorded into again straight after, and the GPU must be done reading
// it (a mid-frame flush that didn't wait let new car textures overwrite uploads still running - noise, 2026-11-06).
static void flush_uploads(int wait)
{
    (void)wait;
    if (!uploadsPending) return;
    QUEUE_LOCK();
    dkQueueSubmitCommands(queue, dkCmdBufFinishList(uploads3d));
    dkQueueWaitIdle(queue);
    QUEUE_UNLOCK();
    start_uploads();
    uploadsPending = 0;
}

// Uploads recorded so far, run now, and everything waited for (frames in flight included): their staging, and whatever
// the frames used, can then be reused.
EXPORT void obns_dk3d_flush_uploads(void)
{
    if (!ready3d) return;
    flush_uploads(1);
    wait_frames3d();
    QUEUE_LOCK();
    dkQueueWaitIdle(queue);
    QUEUE_UNLOCK();
}

// How much memory, and at what alignment, an image of the size, levels and format needs.
EXPORT int obns_dk3d_image_size(int w, int h, int levels, int format, uint64_t *size, uint32_t *alignment)
{
    if (!init3d()) return 1;
    DkImageLayoutMaker maker;
    dkImageLayoutMakerDefaults(&maker, device);
    maker.format = (DkImageFormat)format;
    maker.dimensions[0] = (uint32_t)w;
    maker.dimensions[1] = (uint32_t)h;
    maker.mipLevels = (uint32_t)levels;
    DkImageLayout layout;
    dkImageLayoutInitialize(&layout, &maker);
    *size = dkImageLayoutGetSize(&layout);
    *alignment = dkImageLayoutGetAlignment(&layout);
    return 0;
}

// The texture in descriptor slot `slot`: an image of the size, levels and format at the memory, read through the swizzle
// (DkImageSwizzle for red, green, blue and alpha).
EXPORT int obns_dk3d_image_init(int slot, int w, int h, int levels, int format, void *block, uint32_t offset, const uint8_t *swizzle)
{
    if (!init3d() || slot < 0 || slot >= TEXTURES3D) return 1;
    DkImageLayoutMaker maker;
    dkImageLayoutMakerDefaults(&maker, device);
    maker.format = (DkImageFormat)format;
    maker.dimensions[0] = (uint32_t)w;
    maker.dimensions[1] = (uint32_t)h;
    maker.mipLevels = (uint32_t)levels;
    DkImageLayout layout;
    dkImageLayoutInitialize(&layout, &maker);
    dkImageInitialize(&textures3d[slot], &layout, (DkMemBlock)block, offset);
    DkImageView view;
    dkImageViewDefaults(&view, &textures3d[slot]);
    for (int i = 0; i < 4; i++)
        view.swizzle[i] = (DkImageSwizzle)swizzle[i];
    DkImageDescriptor descriptor;
    dkImageDescriptorInitialize(&descriptor, &view, false, false);
    memcpy((uint8_t *)dkMemBlockGetCpuAddr(descriptors3d) + DESCRIPTOR_IMAGES + (size_t)slot * 32, &descriptor, sizeof descriptor);
    return 0;
}

// Level 0 of the texture from tightly packed pixels at the GPU address: recorded with the uploads, run before the next
// frame's drawing (or at obns_dk3d_flush_uploads).
EXPORT void obns_dk3d_image_upload(int slot, uint64_t source, int w, int h)
{
    if (!init3d() || slot < 0 || slot >= TEXTURES3D) return;
    if (uploadsPending >= UPLOADS_BEFORE_FLUSH) flush_uploads(!inFrame3d);
    DkImageView view;
    dkImageViewDefaults(&view, &textures3d[slot]);
    DkCopyBuf copy = { source, 0, 0 };
    DkImageRect rect = { 0, 0, 0, (uint32_t)w, (uint32_t)h, 1 };
    dkCmdBufCopyBufferToImage(uploads3d, &copy, &view, &rect, 0);
    uploadsPending++;
}

// One level of the texture from tightly packed data at `source` (a compressed image's levels, made beforehand: the 2D
// engine's blits can't write block-compressed formats).
EXPORT void obns_dk3d_image_upload_level(int slot, uint64_t source, int level, int w, int h)
{
    if (!init3d() || slot < 0 || slot >= TEXTURES3D) return;
    if (uploadsPending >= UPLOADS_BEFORE_FLUSH) flush_uploads(!inFrame3d);
    DkImageView view;
    dkImageViewDefaults(&view, &textures3d[slot]);
    view.mipLevelOffset = (uint8_t)level;
    view.mipLevelCount = 1;
    DkCopyBuf copy = { source, 0, 0 };
    DkImageRect rect = { 0, 0, 0, (uint32_t)w, (uint32_t)h, 1 };
    dkCmdBufCopyBufferToImage(uploads3d, &copy, &view, &rect, 0);
    uploadsPending++;
}

// The texture's mipmaps from level 0, each level blitted (filtered) from the one above.
EXPORT void obns_dk3d_image_mipmaps(int slot, int w, int h, int levels)
{
    if (!init3d() || slot < 0 || slot >= TEXTURES3D) return;
    for (int level = 1; level < levels; level++) {
        int nw = w > 1 ? w / 2 : 1, nh = h > 1 ? h / 2 : 1;
        DkImageView source, target;
        dkImageViewDefaults(&source, &textures3d[slot]);
        dkImageViewDefaults(&target, &textures3d[slot]);
        source.mipLevelOffset = (uint8_t)(level - 1);
        source.mipLevelCount = 1;
        target.mipLevelOffset = (uint8_t)level;
        target.mipLevelCount = 1;
        DkImageRect from = { 0, 0, 0, (uint32_t)w, (uint32_t)h, 1 };
        DkImageRect to = { 0, 0, 0, (uint32_t)nw, (uint32_t)nh, 1 };
        // Each level reads the one just written - by the copy for level 0, which a fragments barrier doesn't wait for.
        dkCmdBufBarrier(uploads3d, DkBarrier_Full, DkInvalidateFlags_Image | DkInvalidateFlags_L2Cache);
        dkCmdBufBlitImage(uploads3d, &source, &from, &target, &to, DkBlitFlag_FilterLinear, 0);
        w = nw;
        h = nh;
        uploadsPending++;
    }
}

// A sampler in its descriptor slot: DkFilter min and mag, DkMipFilter, DkWrapMode for s and t, the anisotropy.
EXPORT void obns_dk3d_sampler(int slot, int minFilter, int magFilter, int mipFilter, int wrapS, int wrapT, float anisotropy)
{
    if (!init3d() || slot < 0 || slot >= SAMPLERS3D) return;
    DkSampler sampler;
    dkSamplerDefaults(&sampler);
    sampler.minFilter = (DkFilter)minFilter;
    sampler.magFilter = (DkFilter)magFilter;
    sampler.mipFilter = (DkMipFilter)mipFilter;
    sampler.wrapMode[0] = (DkWrapMode)wrapS;
    sampler.wrapMode[1] = (DkWrapMode)wrapT;
    sampler.wrapMode[2] = DkWrapMode_Repeat;
    sampler.maxAnisotropy = anisotropy > 1 ? anisotropy : 1;
    // GL's non-mipmap minification filters read level 0 only.
    if (mipFilter == DkMipFilter_None) sampler.lodClampMax = 0.0f;
    DkSamplerDescriptor descriptor;
    dkSamplerDescriptorInitialize(&descriptor, &sampler);
    memcpy((uint8_t *)dkMemBlockGetCpuAddr(descriptors3d) + DESCRIPTOR_SAMPLERS + (size_t)slot * 32, &descriptor, sizeof descriptor);
}

// --- the frame ---

// Begins a frame: the uploads go first, then the swapchain image is acquired and bound with the depth buffer.
EXPORT int obns_dk3d_begin(void)
{
    if (!init3d() || dkQueueIsInErrorState(queue)) return 1;
    if (inFrame3d) return 0;
    QUEUE_LOCK();
    flush_uploads(0);
    frameSlot3d = dkQueueAcquireImage(queue, swapchain);
    QUEUE_UNLOCK();
    dkCmdBufClear(commands3d);
    // The half the frame two before used: its fence was waited for at the end of the frame before this one.
    if (fenceSignaled[commandHalf]) {
        dkFenceWait(&frameFences[commandHalf], -1);
        fenceSignaled[commandHalf] = 0;
    }
    dkCmdBufAddMemory(commands3d, commands3dMemory, (uint32_t)commandHalf * FRAME_COMMANDS, FRAME_COMMANDS);
    // The uploads' images and the descriptors the CPU wrote, seen by the drawing.
    dkCmdBufBarrier(commands3d, DkBarrier_Full, DkInvalidateFlags_Image | DkInvalidateFlags_Descriptors | DkInvalidateFlags_L2Cache);
    DkImageView color, depth;
    dkImageViewDefaults(&color, &framebuffers[frameSlot3d]);
    dkImageViewDefaults(&depth, &depthImage);
    dkCmdBufBindRenderTarget(commands3d, &color, &depth);
    DkScissor all = { 0, 0, (uint32_t)width, (uint32_t)height };
    dkCmdBufSetScissors(commands3d, 0, &all, 1);
    DkViewport viewport = { 0, 0, (float)width, (float)height, 0.0f, 1.0f };
    dkCmdBufSetViewports(commands3d, 0, &viewport, 1);
    DkGpuAddr descriptors = dkMemBlockGetGpuAddr(descriptors3d);
    dkCmdBufBindImageDescriptorSet(commands3d, descriptors + DESCRIPTOR_IMAGES, TEXTURES3D);
    dkCmdBufBindSamplerDescriptorSet(commands3d, descriptors + DESCRIPTOR_SAMPLERS, SAMPLERS3D);
    DkGpuAddr clip = dkMemBlockGetGpuAddr(clipMemory);
    dkCmdBufBindUniformBuffer(commands3d, DkStage_Vertex, 2, clip, DK_UNIFORM_BUF_ALIGNMENT);
    lastValid3d = 0;
    worldValid = 0;
    inFrame3d = 1;
    return 0;
}

// glClear: colour (mask & 1) and depth (mask & 2), over the whole target as GL's clear with no scissor.
EXPORT void obns_dk3d_clear(int mask, float r, float g, float b, float a)
{
    if (!inFrame3d) return;
    DkScissor all = { 0, 0, (uint32_t)width, (uint32_t)height };
    dkCmdBufSetScissors(commands3d, 0, &all, 1);
    if (mask & 1) dkCmdBufClearColorFloat(commands3d, 0, DkColorMask_RGBA, r, g, b, a);
    if (mask & 2) dkCmdBufClearDepthStencil(commands3d, true, 1.0f, 0xFF, 0);
    // The scissor is the whole target now: the next draw sets its own again.
    lastValid3d = 0;
    worldValid = 0;
}

static void apply_state(const Draw3D *d)
{
    const Draw3D *l = lastValid3d ? &last3d : NULL;
    if (!l || l->program != d->program) {
        DkShader const *bound[2];
        bound[0] = &shaders3d[d->program == 0 ? 0 : d->program == 1 ? 2 : 4];
        bound[1] = &shaders3d[d->program == 0 ? 1 : 3];
        dkCmdBufBindShaders(commands3d, DkStageFlag_GraphicsMask, bound, 2);
    }
    if (!l || l->cull != d->cull) {
        DkRasterizerState rasterizer;
        dkRasterizerStateDefaults(&rasterizer);
        rasterizer.cullMode = (DkFace)d->cull;
        rasterizer.frontFace = DkFrontFace_CCW;
        dkCmdBufBindRasterizerState(commands3d, &rasterizer);
    }
    if (!l || l->blend != d->blend) {
        DkColorState color;
        dkColorStateDefaults(&color);
        dkColorStateSetBlendEnable(&color, 0, d->blend != 0);
        dkCmdBufBindColorState(commands3d, &color);
        if (!l) {
            DkColorWriteState write;
            dkColorWriteStateDefaults(&write);
            dkCmdBufBindColorWriteState(commands3d, &write);
        }
    }
    if (!l || l->blendSource != d->blendSource || l->blendDestination != d->blendDestination) {
        DkBlendState blend;
        dkBlendStateDefaults(&blend);
        dkBlendStateSetFactors(&blend, (DkBlendFactor)d->blendSource, (DkBlendFactor)d->blendDestination, (DkBlendFactor)d->blendSource, (DkBlendFactor)d->blendDestination);
        dkCmdBufBindBlendState(commands3d, 0, &blend);
    }
    if (!l || l->depthTest != d->depthTest || l->depthWrite != d->depthWrite || l->depthCompare != d->depthCompare) {
        DkDepthStencilState depth;
        dkDepthStencilStateDefaults(&depth);
        depth.depthTestEnable = d->depthTest != 0;
        depth.depthWriteEnable = d->depthWrite != 0;
        depth.depthCompareOp = (DkCompareOp)d->depthCompare;
        dkCmdBufBindDepthStencilState(commands3d, &depth);
    }
    if (!l || memcmp(l->viewport, d->viewport, sizeof d->viewport) != 0) {
        DkViewport viewport = { (float)d->viewport[0], (float)d->viewport[1], (float)d->viewport[2], (float)d->viewport[3], 0.0f, 1.0f };
        dkCmdBufSetViewports(commands3d, 0, &viewport, 1);
        DkScissor scissor = { (uint32_t)(d->viewport[0] > 0 ? d->viewport[0] : 0), (uint32_t)(d->viewport[1] > 0 ? d->viewport[1] : 0),
                              (uint32_t)d->viewport[2], (uint32_t)d->viewport[3] };
        dkCmdBufSetScissors(commands3d, 0, &scissor, 1);
    }
    if (!l || l->attributeCount != d->attributeCount || memcmp(l->attribute, d->attribute, sizeof d->attribute) != 0) {
        DkVtxAttribState attributes[5];
        int n = (int)d->attributeCount;
        for (int i = 0; i < n; i++) {
            uint32_t a = d->attribute[i];
            DkVtxAttribState s = { .bufferId = (uint32_t)i, .isFixed = 0, .offset = 0,
                .size = (DkVtxAttribSize)(a & 0xFF), .type = (DkVtxAttribType)((a >> 8) & 0xFF), .isBgra = 0 };
            attributes[i] = s;
        }
        dkCmdBufBindVtxAttribState(commands3d, attributes, (uint32_t)n);
    }
    if (d->attributeCount && (!l || l->attributeCount != d->attributeCount || memcmp(l->vertexStride, d->vertexStride, sizeof d->vertexStride) != 0)) {
        DkVtxBufferState buffers[5];
        for (uint32_t i = 0; i < d->attributeCount; i++) {
            buffers[i].stride = d->vertexStride[i];
            buffers[i].divisor = 0;
        }
        dkCmdBufBindVtxBufferState(commands3d, buffers, d->attributeCount);
    }
    if (d->attributeCount && (!l || memcmp(l->vertexAddress, d->vertexAddress, sizeof d->vertexAddress) != 0 || memcmp(l->vertexSize, d->vertexSize, sizeof d->vertexSize) != 0)) {
        DkBufExtents extents[5];
        for (uint32_t i = 0; i < d->attributeCount; i++) {
            extents[i].addr = d->vertexAddress[i];
            extents[i].size = d->vertexSize[i];
        }
        dkCmdBufBindVtxBuffers(commands3d, 0, extents, d->attributeCount);
    }
    if (!l || l->blockAddress != d->blockAddress || l->blockSize != d->blockSize) {
        uint32_t size = align_up(d->blockSize, DK_UNIFORM_BUF_ALIGNMENT);
        dkCmdBufBindUniformBuffer(commands3d, DkStage_Vertex, 0, d->blockAddress, size);
        dkCmdBufBindUniformBuffer(commands3d, DkStage_Fragment, 0, d->blockAddress, size);
    }
    if (d->matricesSize && (!l || l->matricesAddress != d->matricesAddress || l->matricesSize != d->matricesSize))
        dkCmdBufBindUniformBuffer(commands3d, DkStage_Vertex, 1, d->matricesAddress, align_up(d->matricesSize, DK_UNIFORM_BUF_ALIGNMENT));
    if (!l || l->texture != d->texture || l->sampler != d->sampler)
        dkCmdBufBindTexture(commands3d, DkStage_Fragment, 0, dkMakeTextureHandle(d->texture, d->sampler));
    last3d = *d;
    lastValid3d = 1;
}



// One draw: the state where it changed, then the draw.
EXPORT void obns_dk3d_draw(const Draw3D *d)
{
    if (!inFrame3d || d->count == 0) return;
    worldValid = 0;
    apply_state(d);
    if (d->indexFormat) {
        dkCmdBufBindIdxBuffer(commands3d, (DkIdxFormat)(d->indexFormat - 1), d->indexAddress);
        dkCmdBufDrawIndexed(commands3d, (DkPrimitive)d->primitive, d->count, 1, d->first, d->vertexOffset, 0);
    } else {
        dkCmdBufDraw(commands3d, (DkPrimitive)d->primitive, d->count, 1, d->first, 0);
    }
    draws3d++;
}

// --- the world's records (OBNS/SWITCH.md, step 3, the alone way): the faces of the world and the cab handed over as
// compact records in batches rather than as GL calls (NX/OpenBve.NX/Deko/DekoGL.World.cs; shaders/world.*) ---

// One face, as DekoGL.World hands it over (the same layout as its WorldDraw).
typedef struct {
    uint64_t vertexAddress;     // the mesh's interleaved vertex buffer
    uint32_t vertexSize;
    uint32_t stride;
    uint64_t indexAddress;
    uint32_t indexFormat;       // DkIdxFormat + 1
    uint32_t primitive;         // DkPrimitive (GL's modes, quads and polygons too)
    uint32_t first;
    uint32_t count;
    uint64_t frameAddress;      // FrameBlock (binding 0): projection, lights, fog
    uint64_t objectAddress;     // ObjectBlock (binding 3): the object's matrices
    uint64_t matricesAddress;   // the animation matrices (binding 1)
    uint32_t layout;            // a vertex layout registered with obns_dkw_layout
    uint32_t texture;
    uint32_t sampler;
    uint32_t state;             // depth test, depth write << 1, compare << 2 (4 bits), blend << 6, source << 7 (4), destination << 11 (4), cull << 15 (2)
    int32_t viewport[4];
    float draw[24];             // DrawBlock (binding 4), pushed with the draw: material, flags, brightness, opacity, alpha test
} WorldDraw;

#define WORLD_LAYOUTS 64
#define WORLD_ATTRIBUTES 5
#define DRAW_BLOCK_SIZE 96
static DkVtxAttribState worldLayouts[WORLD_LAYOUTS][WORLD_ATTRIBUTES];
static DkMemBlock worldDrawMemory, worldConstantMemory;
static WorldDraw lastWorld;

static int initWorld(void)
{
    if (worldDrawMemory) return 1;
    DkMemBlockMaker maker;
    dkMemBlockMakerDefaults(&maker, device, DK_MEMBLOCK_ALIGNMENT);
    worldDrawMemory = dkMemBlockCreate(&maker);
    worldConstantMemory = dkMemBlockCreate(&maker);
    if (!worldDrawMemory || !worldConstantMemory) return 0;
    // The constant attributes, as GL reads an attribute without an array: (0, 0, 0, 1) as floats, zeros as integers.
    float *constant = (float *)dkMemBlockGetCpuAddr(worldConstantMemory);
    memset(constant, 0, 64);
    constant[3] = 1.0f;
    return 1;
}

// A vertex layout: for each of the world shader's five attributes, where it sits in the mesh's vertex (offset), its format
// (DkVtxAttribSize | DkVtxAttribType << 8) and whether it comes from the mesh (buffer 0) or the constant (buffer 1).
EXPORT int obns_dkw_layout(int id, const uint32_t *offsets, const uint32_t *formats, const uint32_t *buffers)
{
    if (id < 0 || id >= WORLD_LAYOUTS) return 1;
    for (int i = 0; i < WORLD_ATTRIBUTES; i++) {
        DkVtxAttribState a = { .bufferId = buffers[i], .isFixed = 0, .offset = offsets[i],
            .size = (DkVtxAttribSize)(formats[i] & 0xFF), .type = (DkVtxAttribType)((formats[i] >> 8) & 0xFF), .isBgra = 0 };
        worldLayouts[id][i] = a;
    }
    return 0;
}

static void world_state(uint32_t state, uint32_t last, int all)
{
    if (all || ((state ^ last) & (3u << 15))) {
        DkRasterizerState rasterizer;
        dkRasterizerStateDefaults(&rasterizer);
        rasterizer.cullMode = (DkFace)((state >> 15) & 3);
        rasterizer.frontFace = DkFrontFace_CCW;
        dkCmdBufBindRasterizerState(commands3d, &rasterizer);
    }
    if (all || ((state ^ last) & (1u << 6))) {
        DkColorState color;
        dkColorStateDefaults(&color);
        dkColorStateSetBlendEnable(&color, 0, ((state >> 6) & 1) != 0);
        dkCmdBufBindColorState(commands3d, &color);
    }
    if (all || ((state ^ last) & (0xFFu << 7))) {
        DkBlendState blend;
        dkBlendStateDefaults(&blend);
        DkBlendFactor source = (DkBlendFactor)((state >> 7) & 15), destination = (DkBlendFactor)((state >> 11) & 15);
        dkBlendStateSetFactors(&blend, source, destination, source, destination);
        dkCmdBufBindBlendState(commands3d, 0, &blend);
    }
    if (all || ((state ^ last) & 0x3Fu)) {
        DkDepthStencilState depth;
        dkDepthStencilStateDefaults(&depth);
        depth.depthTestEnable = (state & 1) != 0;
        depth.depthWriteEnable = ((state >> 1) & 1) != 0;
        depth.depthCompareOp = (DkCompareOp)((state >> 2) & 15);
        dkCmdBufBindDepthStencilState(commands3d, &depth);
    }
}

// Records a batch of the world's faces into the frame: the world program, then for each face only what differs from the
// one before it - its blocks' addresses, vertices, indices, texture, state, viewport - its material pushed, and the draw.
EXPORT int obns_dkw_draws(const WorldDraw *draws, int count)
{
    if (!inFrame3d || !initWorld()) return 1;
    DkGpuAddr drawBlock = dkMemBlockGetGpuAddr(worldDrawMemory);
    for (int i = 0; i < count; i++) {
        const WorldDraw *d = &draws[i];
        if (d->count == 0) continue;
        const WorldDraw *l = worldValid ? &lastWorld : NULL;
        if (!l) {
            DkShader const *bound[2] = { &shaders3d[5], &shaders3d[6] };
            dkCmdBufBindShaders(commands3d, DkStageFlag_GraphicsMask, bound, 2);
            dkCmdBufBindUniformBuffer(commands3d, DkStage_Vertex, 2, dkMemBlockGetGpuAddr(clipMemory), DK_UNIFORM_BUF_ALIGNMENT);
            dkCmdBufBindUniformBuffer(commands3d, DkStage_Vertex, 4, drawBlock, DK_UNIFORM_BUF_ALIGNMENT);
            dkCmdBufBindUniformBuffer(commands3d, DkStage_Fragment, 4, drawBlock, DK_UNIFORM_BUF_ALIGNMENT);
            DkColorWriteState write;
            dkColorWriteStateDefaults(&write);
            dkCmdBufBindColorWriteState(commands3d, &write);
        }
        if (!l || l->frameAddress != d->frameAddress) {
            dkCmdBufBindUniformBuffer(commands3d, DkStage_Vertex, 0, d->frameAddress, DK_UNIFORM_BUF_ALIGNMENT);
            dkCmdBufBindUniformBuffer(commands3d, DkStage_Fragment, 0, d->frameAddress, DK_UNIFORM_BUF_ALIGNMENT);
        }
        if (!l || l->objectAddress != d->objectAddress)
            dkCmdBufBindUniformBuffer(commands3d, DkStage_Vertex, 3, d->objectAddress, DK_UNIFORM_BUF_ALIGNMENT);
        if (!l || l->matricesAddress != d->matricesAddress)
            dkCmdBufBindUniformBuffer(commands3d, DkStage_Vertex, 1, d->matricesAddress, 128 * 64);
        if (!l || l->layout != d->layout)
            dkCmdBufBindVtxAttribState(commands3d, worldLayouts[d->layout < WORLD_LAYOUTS ? d->layout : 0], WORLD_ATTRIBUTES);
        if (!l || l->stride != d->stride) {
            DkVtxBufferState buffers[2] = { { d->stride, 0 }, { 0, 0 } };
            dkCmdBufBindVtxBufferState(commands3d, buffers, 2);
        }
        if (!l || l->vertexAddress != d->vertexAddress || l->vertexSize != d->vertexSize) {
            DkBufExtents extents[2] = { { d->vertexAddress, d->vertexSize }, { dkMemBlockGetGpuAddr(worldConstantMemory), 64 } };
            dkCmdBufBindVtxBuffers(commands3d, 0, extents, 2);
        }
        if (!l || l->texture != d->texture || l->sampler != d->sampler)
            dkCmdBufBindTexture(commands3d, DkStage_Fragment, 0, dkMakeTextureHandle(d->texture, d->sampler));
        world_state(d->state, l ? l->state : 0, !l);
        if (!l || memcmp(l->viewport, d->viewport, sizeof d->viewport) != 0) {
            DkViewport viewport = { (float)d->viewport[0], (float)d->viewport[1], (float)d->viewport[2], (float)d->viewport[3], 0.0f, 1.0f };
            dkCmdBufSetViewports(commands3d, 0, &viewport, 1);
            DkScissor scissor = { (uint32_t)(d->viewport[0] > 0 ? d->viewport[0] : 0), (uint32_t)(d->viewport[1] > 0 ? d->viewport[1] : 0),
                                  (uint32_t)d->viewport[2], (uint32_t)d->viewport[3] };
            dkCmdBufSetScissors(commands3d, 0, &scissor, 1);
        }
        dkCmdBufPushConstants(commands3d, drawBlock, DK_UNIFORM_BUF_ALIGNMENT, 0, DRAW_BLOCK_SIZE, d->draw);
        if (!l || l->indexAddress != d->indexAddress || l->indexFormat != d->indexFormat)
            dkCmdBufBindIdxBuffer(commands3d, (DkIdxFormat)(d->indexFormat - 1), d->indexAddress);
        dkCmdBufDrawIndexed(commands3d, (DkPrimitive)d->primitive, d->count, 1, d->first, 0, 0);
        lastWorld = *d;
        worldValid = 1;
        draws3d++;
    }
    // A GL-path draw after these binds its own state again.
    lastValid3d = 0;
    return 0;
}

// The 2D layer's quads (obns_dk2d_begin and the drawing calls) over the 3D frame being recorded: the in-game interface,
// drawn after the scene and before the present. 1 if there is no 3D frame.
EXPORT int obns_dk2d_overlay_end(void)
{
    if (!inFrame3d || !ready2d) return 1;
    QUEUE_LOCK();
    submit_uploads2d();
    QUEUE_UNLOCK();
    record2d(commands3d);
    // The 3D bindings back, should more be drawn after: its state is set again at the next draw.
    lastValid3d = 0;
    worldValid = 0;
    DkGpuAddr descriptors = dkMemBlockGetGpuAddr(descriptors3d);
    dkCmdBufBindImageDescriptorSet(commands3d, descriptors + DESCRIPTOR_IMAGES, TEXTURES3D);
    dkCmdBufBindSamplerDescriptorSet(commands3d, descriptors + DESCRIPTOR_SAMPLERS, SAMPLERS3D);
    dkCmdBufBindUniformBuffer(commands3d, DkStage_Vertex, 2, dkMemBlockGetGpuAddr(clipMemory), DK_UNIFORM_BUF_ALIGNMENT);
    return 0;
}

// Ends the frame: submitted, presented, and (for now) waited for, so everything it used is free again.
EXPORT int obns_dk3d_end(void)
{
    if (!inFrame3d) return 1;
    inFrame3d = 0;
    QUEUE_LOCK();
    // Textures made during the frame: their uploads run first (the frame's barrier at its start waits for them).
    flush_uploads(0);
    dkQueueSubmitCommands(queue, dkCmdBufFinishList(commands3d));
    if (framesInFlight)
        dkQueueSignalFence(queue, &frameFences[commandHalf], false);
    dkQueuePresentImage(queue, swapchain, frameSlot3d);
    if (!framesInFlight)
        dkQueueWaitIdle(queue);
    QUEUE_UNLOCK();
    if (framesInFlight) {
        fenceSignaled[commandHalf] = 1;
        // The frame before this one, done: from here on only this frame may still be drawing.
        int before = commandHalf ^ 1;
        if (fenceSignaled[before]) {
            dkFenceWait(&frameFences[before], -1);
            fenceSignaled[before] = 0;
        }
        commandHalf = before;
    }
    if (frames3d++ == 0)
        printf("deko3d 3D: first frame presented, %llu draws\n", (unsigned long long)draws3d);
    return dkQueueIsInErrorState(queue) ? 2 : 0;
}

static void close3d(void)
{
    if (!ready3d) return;
    wait_frames3d();
    dkQueueWaitIdle(queue);
    dkCmdBufDestroy(commands3d);
    dkCmdBufDestroy(uploads3d);
    dkMemBlockDestroy(commands3dMemory);
    dkMemBlockDestroy(uploads3dMemory);
    dkMemBlockDestroy(descriptors3d);
    dkMemBlockDestroy(depthMemory);
    dkMemBlockDestroy(clipMemory);
    dkMemBlockDestroy(code3d);
    ready3d = 0;
}
