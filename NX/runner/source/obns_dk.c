// OBNS 1.32: deko3d, the Switch's renderer (OBNS/SWITCH.md, step 3) - its first part: the device, the queue, the
// swapchain on the console's window, and frames made of rectangles (scissored clears, no shaders yet), enough for the
// loading screen's progress bar while the route loads. The world, the cab, the HUD and the menu follow, through the
// same device. Copyright (c) The OpenBVE Android port contributors. BSD-2-Clause, as OBNS.
//
// C# calls these as direct P/Invokes ("__Internal"). One thread draws at a time.

#include <stdint.h>
#include <stdio.h>
#include <string.h>
#include <switch.h>
#include <deko3d.h>

#define EXPORT __attribute__((visibility("default")))

#define FRAMEBUFFERS 3
#define COMMAND_MEMORY (64 * 1024)

static DkDevice device;
static DkQueue queue;
static DkMemBlock framebufferMemory, commandMemory;
static DkImage framebuffers[FRAMEBUFFERS];
static DkSwapchain swapchain;
static DkCmdBuf commands;
static int width, height;
static uint64_t frames;

// The queue is one: the loading screen's thread draws on it while the loading thread's DekoGL uploads textures.
static RMutex queueLock;
#define QUEUE_LOCK() rmutexLock(&queueLock)
#define QUEUE_UNLOCK() rmutexUnlock(&queueLock)

static void debug(void *userData, const char *context, DkResult result, const char *message)
{
    (void)userData;
    printf("deko3d: %s (%d): %s\n", context ? context : "", (int)result, message ? message : "");
}

static uint32_t align_up(uint32_t value, uint32_t alignment) { return (value + alignment - 1) & ~(alignment - 1); }

static void close2d(void); // obns_dk2d.inc
static void close3d(void); // obns_dk3d.inc

// Opens the device and the swapchain at width x height (1280x720 handheld; the console scales it docked until the
// renderer draws at 1080p there). 0 when it worked.
EXPORT int obns_dk_open(int w, int h)
{
    if (device)
        return 0;
    width = w;
    height = h;

    DkDeviceMaker deviceMaker;
    dkDeviceMakerDefaults(&deviceMaker);
    deviceMaker.cbDebug = debug;
    device = dkDeviceCreate(&deviceMaker);
    if (!device)
        return 1;

    DkImageLayoutMaker layoutMaker;
    dkImageLayoutMakerDefaults(&layoutMaker, device);
    layoutMaker.flags = DkImageFlags_UsageRender | DkImageFlags_UsagePresent | DkImageFlags_HwCompression;
    layoutMaker.format = DkImageFormat_RGBA8_Unorm;
    layoutMaker.dimensions[0] = (uint32_t)width;
    layoutMaker.dimensions[1] = (uint32_t)height;
    DkImageLayout layout;
    dkImageLayoutInitialize(&layout, &layoutMaker);
    uint32_t size = align_up((uint32_t)dkImageLayoutGetSize(&layout), dkImageLayoutGetAlignment(&layout));

    DkMemBlockMaker memoryMaker;
    dkMemBlockMakerDefaults(&memoryMaker, device, align_up(FRAMEBUFFERS * size, DK_MEMBLOCK_ALIGNMENT));
    memoryMaker.flags = DkMemBlockFlags_GpuCached | DkMemBlockFlags_Image;
    framebufferMemory = dkMemBlockCreate(&memoryMaker);
    if (!framebufferMemory)
        return 2;

    DkImage const *images[FRAMEBUFFERS];
    for (int i = 0; i < FRAMEBUFFERS; i++) {
        dkImageInitialize(&framebuffers[i], &layout, framebufferMemory, (uint32_t)i * size);
        images[i] = &framebuffers[i];
    }

    DkSwapchainMaker swapchainMaker;
    dkSwapchainMakerDefaults(&swapchainMaker, device, nwindowGetDefault(), images, FRAMEBUFFERS);
    swapchain = dkSwapchainCreate(&swapchainMaker);
    if (!swapchain)
        return 3;

    DkQueueMaker queueMaker;
    dkQueueMakerDefaults(&queueMaker, device);
    queueMaker.flags = DkQueueFlags_Graphics;
    queue = dkQueueCreate(&queueMaker);
    if (!queue)
        return 4;

    dkMemBlockMakerDefaults(&memoryMaker, device, COMMAND_MEMORY);
    commandMemory = dkMemBlockCreate(&memoryMaker);
    if (!commandMemory)
        return 5;

    DkCmdBufMaker commandsMaker;
    dkCmdBufMakerDefaults(&commandsMaker, device);
    commands = dkCmdBufCreate(&commandsMaker);
    if (!commands)
        return 6;

    printf("deko3d: open, %dx%d, %d framebuffers of %u KiB\n", width, height, FRAMEBUFFERS, size >> 10);
    return 0;
}

// One frame: the background colour, then each rectangle (x, y, width, height in pixels from the top left; red, green,
// blue from 0 to 1: 7 floats each), presented. Waits for the GPU before returning, which the loading screen's 30 frames
// a second can afford; the renderer proper keeps frames in flight.
EXPORT int obns_dk_frame_rects(float red, float green, float blue, const float *rects, int count)
{
    if (!device || dkQueueIsInErrorState(queue))
        return 1;

    QUEUE_LOCK();
    int slot = dkQueueAcquireImage(queue, swapchain);
    dkCmdBufClear(commands);
    dkCmdBufAddMemory(commands, commandMemory, 0, COMMAND_MEMORY);

    DkImageView view;
    dkImageViewDefaults(&view, &framebuffers[slot]);
    dkCmdBufBindRenderTarget(commands, &view, NULL);

    DkScissor whole = { 0, 0, (uint32_t)width, (uint32_t)height };
    dkCmdBufSetScissors(commands, 0, &whole, 1);
    dkCmdBufClearColorFloat(commands, 0, DkColorMask_RGBA, red, green, blue, 1.0f);

    for (int i = 0; i < count; i++) {
        const float *r = rects + i * 7;
        int x0 = (int)(r[0] + 0.5f), y0 = (int)(r[1] + 0.5f), x1 = (int)(r[0] + r[2] + 0.5f), y1 = (int)(r[1] + r[3] + 0.5f);
        if (x0 < 0) x0 = 0;
        if (y0 < 0) y0 = 0;
        if (x1 > width) x1 = width;
        if (y1 > height) y1 = height;
        if (x1 <= x0 || y1 <= y0)
            continue;
        DkScissor scissor = { (uint32_t)x0, (uint32_t)y0, (uint32_t)(x1 - x0), (uint32_t)(y1 - y0) };
        dkCmdBufSetScissors(commands, 0, &scissor, 1);
        dkCmdBufClearColorFloat(commands, 0, DkColorMask_RGBA, r[4], r[5], r[6], 1.0f);
    }

    dkQueueSubmitCommands(queue, dkCmdBufFinishList(commands));
    dkQueuePresentImage(queue, swapchain, slot);
    dkQueueWaitIdle(queue);
    QUEUE_UNLOCK();
    if (frames++ == 0)
        printf("deko3d: first frame presented\n");
    return dkQueueIsInErrorState(queue) ? 2 : 0;
}

// Closes everything, the GPU idle first.
EXPORT void obns_dk_close(void)
{
    if (!device)
        return;
    QUEUE_LOCK();
    dkQueueWaitIdle(queue);
    close2d();
    close3d();
    dkCmdBufDestroy(commands);
    dkMemBlockDestroy(commandMemory);
    dkQueueDestroy(queue);
    dkSwapchainDestroy(swapchain);
    dkMemBlockDestroy(framebufferMemory);
    dkDeviceDestroy(device);
    device = NULL;
    QUEUE_UNLOCK();
    printf("deko3d: closed after %llu frames\n", (unsigned long long)frames);
}

// The 2D drawing, sharing this file's device, queue and swapchain.
#include "obns_dk2d.inc"

// The renderer's 3D drawing (DekoGL), sharing the same.
#include "obns_dk3d.inc"
