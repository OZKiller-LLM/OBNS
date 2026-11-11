// OBNS 1.32: the sound output without SDL. OpenAL Soft mixes into a loopback device, and a thread of OBNS's own hands
// the mix to audout, never waiting on it for long.
//
// Why: OpenAL Soft's SDL2 backend left the sound silent for good partway through the Tuen Ma Line drive benchmark
// (2026-11-07/08), SDL's audio thread waiting in SWITCHAUDIO_PlayDevice for a buffer that never came back - and closing
// the device then joined that thread, which froze the game. Here every wait has a timeout: when no buffer comes back for
// half a second the output is stopped and started again, and the mix carries on.
//
// The game opens its device through OpenTK (alcOpenDevice, alcCreateContext, alcCloseDevice); the runner is linked with
// --wrap for those three, so the C# side is unchanged: it gets the loopback device and a context on it.
//
// Simplified BSD License (BSD-2-Clause), Copyright (c) 2026, The OpenBVE Android port contributors.

#include <switch.h>
#include <malloc.h>
#include <stdio.h>
#include <string.h>

#define AL_ALEXT_PROTOTYPES
#include <AL/al.h>
#include <AL/alc.h>
#include <AL/alext.h>

#define EXPORT __attribute__((visibility("default")))

ALCdevice *__real_alcOpenDevice(const ALCchar *name);
ALCcontext *__real_alcCreateContext(ALCdevice *device, const ALCint *attributes);
ALCboolean __real_alcCloseDevice(ALCdevice *device);

enum {
    RATE = 48000,
    CHANNELS = 2,
    FRAMES = 960,                       // 20 ms a buffer
    BUFFERS = 4,                        // 80 ms queued at most
    STALL_MS = 500,                     // no buffer back this long: the output is started again
};

static ALCdevice *loopback;
static Thread outputThread;
static volatile bool outputRunning, threadStarted;
static AudioOutBuffer buffers[BUFFERS];
static void *bufferData[BUFFERS];
static volatile int restarts, timeouts;
static volatile u64 buffersPlayed;

static void render(AudioOutBuffer *buffer)
{
    alcRenderSamplesSOFT(loopback, buffer->buffer, FRAMES);
    armDCacheFlush(buffer->buffer, buffer->buffer_size);
}

static bool start_output(void)
{
    if (R_FAILED(audoutInitialize())) return false;
    if (R_FAILED(audoutStartAudioOut())) {
        audoutExit();
        return false;
    }

    for (int i = 0; i < BUFFERS; i++) {
        render(&buffers[i]);
        audoutAppendAudioOutBuffer(&buffers[i]);
    }

    return true;
}

static void stop_output(void)
{
    audoutStopAudioOut();
    audoutExit();
}

static void output_main(void *argument)
{
    (void)argument;
    bool open = start_output();
    if (!open) printf("OBNS: audio: audout would not start - no sound\n");
    u64 lastBack = armGetSystemTick();
    while (outputRunning) {
        if (!open) {
            svcSleepThread(500000000ull);
            open = start_output();
            lastBack = armGetSystemTick();
            continue;
        }

        AudioOutBuffer *released = NULL;
        u32 count = 0;
        // A tenth of a second at a time, so a stalled output is seen and the thread can always be stopped.
        Result rc = audoutWaitPlayFinish(&released, &count, 100000000ull);
        if (R_SUCCEEDED(rc)) {
            while (released != NULL && count > 0) {
                render(released);
                audoutAppendAudioOutBuffer(released);
                buffersPlayed++;
                released = NULL;
                count = 0;
                if (R_FAILED(audoutGetReleasedAudioOutBuffer(&released, &count))) break;
            }

            lastBack = armGetSystemTick();
            continue;
        }

        timeouts++;
        if (armTicksToNs(armGetSystemTick() - lastBack) >= (u64)STALL_MS * 1000000ull) {
            // Nothing back for half a second: the output started again, with fresh mixes queued.
            restarts++;
            if (restarts <= 20 || restarts % 100 == 0)
                printf("OBNS: audio: no buffer back for %d ms - the output started again (%d so far)\n", STALL_MS, restarts);
            stop_output();
            open = start_output();
            lastBack = armGetSystemTick();
        }
    }

    if (open) stop_output();
}

ALCdevice *__wrap_alcOpenDevice(const ALCchar *name)
{
    if (loopback == NULL) {
        for (int i = 0; i < BUFFERS; i++) {
            size_t size = (FRAMES * CHANNELS * sizeof(s16) + 0xFFF) & ~(size_t)0xFFF;
            bufferData[i] = memalign(0x1000, size);
            if (bufferData[i] == NULL) return __real_alcOpenDevice(name);
            memset(bufferData[i], 0, size);
            buffers[i].next = NULL;
            buffers[i].buffer = bufferData[i];
            buffers[i].buffer_size = size;
            buffers[i].data_size = FRAMES * CHANNELS * sizeof(s16);
            buffers[i].data_offset = 0;
        }

        loopback = alcLoopbackOpenDeviceSOFT(NULL);
        if (loopback == NULL) {
            printf("OBNS: audio: no loopback device - OpenAL's own output (SDL) instead\n");
            return __real_alcOpenDevice(name);
        }

        if (!alcIsRenderFormatSupportedSOFT(loopback, RATE, ALC_STEREO_SOFT, ALC_SHORT_SOFT)) {
            printf("OBNS: audio: the loopback device can't render 48 kHz stereo 16-bit - OpenAL's own output (SDL) instead\n");
            __real_alcCloseDevice(loopback);
            loopback = NULL;
            return __real_alcOpenDevice(name);
        }
    }

    return loopback;
}

ALCcontext *__wrap_alcCreateContext(ALCdevice *device, const ALCint *attributes)
{
    if (device == NULL || device != loopback) return __real_alcCreateContext(device, attributes);
    (void)attributes;
    const ALCint format[] = {
        ALC_FORMAT_CHANNELS_SOFT, ALC_STEREO_SOFT,
        ALC_FORMAT_TYPE_SOFT, ALC_SHORT_SOFT,
        ALC_FREQUENCY, RATE,
        0
    };
    ALCcontext *context = __real_alcCreateContext(device, format);
    if (context != NULL && !threadStarted) {
        outputRunning = true;
        // Above the game's threads (0x2C): OpenAL's calls on the game's thread wait for a mix in progress to end, which
        // a thread below it on the same core would never get to finish.
        if (R_SUCCEEDED(threadCreate(&outputThread, output_main, NULL, NULL, 0x8000, 0x2B, -2)) && R_SUCCEEDED(threadStart(&outputThread))) {
            threadStarted = true;
            printf("OBNS: audio: OpenAL's loopback device to audout, %d Hz stereo, %d buffers of %d ms\n", RATE, BUFFERS, FRAMES * 1000 / RATE);
        } else {
            outputRunning = false;
            printf("OBNS: audio: the output thread would not start - no sound\n");
        }
    }

    return context;
}

ALCboolean __wrap_alcCloseDevice(ALCdevice *device)
{
    if (device == NULL || device != loopback) return __real_alcCloseDevice(device);
    if (threadStarted) {
        // The thread waits a tenth of a second at most, so this join always ends.
        outputRunning = false;
        threadWaitForExit(&outputThread);
        threadClose(&outputThread);
        threadStarted = false;
    }

    loopback = NULL;
    return __real_alcCloseDevice(device);
}

// For the frame log: buffers played, waits that timed out, restarts of the output.
EXPORT void obns_audio_stats(long long *played, int *timedOut, int *restarted)
{
    *played = (long long)buffersPlayed;
    *timedOut = timeouts;
    *restarted = restarts;
}
