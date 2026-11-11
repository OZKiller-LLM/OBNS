// OBNS 1.32: a probe of the Switch it runs on, for OBNS/SWITCH.md's "verify" items.
// Copyright (c) The OpenBVE Android port contributors. Licensed as OBNS.
//
// Reports the launch mode and the memory the process gets, the model, firmware and Atmosphère versions, docked or
// handheld, the charger and battery, the CPU, GPU and memory clocks, a CPU loop's time at the clock found and at 1,785 MHz
// (set through clkrst, then put back), the official loading boost (apm FastLoad), the cores a thread can run on, the largest
// allocation, and the SD card's write and read speed (a 256 MiB file in its own folder, deleted after).
//
// Output: to nxlink's console when sent with `nxlink -s`, otherwise on screen; always to sdmc:/switch/OBNS-probe/probe.log.

#include <stdarg.h>
#include <stdio.h>
#include <stdlib.h>
#include <string.h>
#include <unistd.h>
#include <sys/stat.h>
#include <switch.h>

static FILE *logFile;
static bool viaNxlink;

static void say(const char *format, ...)
{
    va_list args;
    va_start(args, format);
    vprintf(format, args);
    va_end(args);
    if (logFile) {
        va_start(args, format);
        vfprintf(logFile, format, args);
        va_end(args);
        fflush(logFile);
    }
    if (!viaNxlink) consoleUpdate(NULL);
}

static double seconds(u64 ticks) { return (double)armTicksToNs(ticks) / 1e9; }

// A fixed amount of integer and floating-point work, for timing one core at a clock.
static volatile double sink;
static double cpuLoop(void)
{
    u64 start = armGetSystemTick();
    double a = 1.0;
    u64 h = 1469598103934665603ULL;
    for (u32 i = 0; i < 40000000; i++) {
        h = (h ^ i) * 1099511628211ULL;
        a = a * 1.0000001 + (double)(h & 0xFF) * 1e-9;
    }
    sink = a + (double)h;
    return seconds(armGetSystemTick() - start);
}

static const char *hardwareName(u64 type)
{
    switch (type) {
        case 0: return "Icosa (first model, Erista)";
        case 1: return "Copper (unreleased)";
        case 2: return "Hoag (Lite, Mariko)";
        case 3: return "Iowa (later model, Mariko)";
        case 4: return "Calcio (unreleased)";
        case 5: return "Aula (OLED, Mariko)";
        default: return "unknown";
    }
}

static u32 clockOf(PcvModuleId module)
{
    ClkrstSession session;
    u32 hz = 0;
    if (R_SUCCEEDED(clkrstOpenSession(&session, module, 3))) {
        clkrstGetClockRate(&session, &hz);
        clkrstCloseSession(&session);
    }
    return hz;
}

static void sayClocks(const char *when)
{
    say("Clocks %s: CPU %u MHz, GPU %u MHz, memory %u MHz\n", when, clockOf(PcvModuleId_CpuBus) / 1000000,
        clockOf(PcvModuleId_GPU) / 1000000, clockOf(PcvModuleId_EMC) / 1000000);
}

static volatile u32 coreRan[4];
static void onCore(void *arg) { coreRan[(uintptr_t)arg] = 1 + svcGetCurrentProcessorNumber(); }

static void sdSpeed(void)
{
    const size_t chunk = 1 << 20, total = 256;
    char *buffer = malloc(chunk);
    if (!buffer) { say("SD speed: no buffer\n"); return; }
    for (size_t i = 0; i < chunk; i++) buffer[i] = (char)(i * 31);
    const char *path = "sdmc:/switch/OBNS-probe/speed.bin";

    FILE *f = fopen(path, "wb");
    if (!f) { say("SD speed: can't create %s\n", path); free(buffer); return; }
    u64 start = armGetSystemTick();
    size_t written = 0;
    for (size_t i = 0; i < total; i++) written += fwrite(buffer, 1, chunk, f);
    fclose(f);
    double w = seconds(armGetSystemTick() - start);

    f = fopen(path, "rb");
    size_t read = 0;
    start = armGetSystemTick();
    if (f) { size_t n; while ((n = fread(buffer, 1, chunk, f)) > 0) read += n; fclose(f); }
    double r = seconds(armGetSystemTick() - start);
    remove(path);
    free(buffer);
    say("SD card: wrote %zu MiB in %.2f s (%.1f MiB/s), read %zu MiB in %.2f s (%.1f MiB/s); file deleted\n",
        written >> 20, w, (written >> 20) / w, read >> 20, r, (read >> 20) / r);
    say("  (the read may come partly from the file system's cache: a first read of a route is the real test)\n");
}

int main(int argc, char **argv)
{
    viaNxlink = R_SUCCEEDED(socketInitializeDefault()) && nxlinkStdio() >= 0;
    if (!viaNxlink) consoleInit(NULL);
    mkdir("sdmc:/switch/OBNS-probe", 0777);
    logFile = fopen("sdmc:/switch/OBNS-probe/probe.log", "w");

    say("OBNS Switch probe (1.32)\n\n");

    AppletType type = appletGetAppletType();
    say("Launch: %s (applet type %d)\n",
        type == AppletType_Application || type == AppletType_SystemApplication ? "title takeover (full memory)"
        : "applet mode (about 386 MiB; start hbmenu by holding R while starting a game)", (int)type);

    u64 total = 0, used = 0, cores = 0;
    svcGetInfo(&total, InfoType_TotalMemorySize, CUR_PROCESS_HANDLE, 0);
    svcGetInfo(&used, InfoType_UsedMemorySize, CUR_PROCESS_HANDLE, 0);
    svcGetInfo(&cores, InfoType_CoreMask, CUR_PROCESS_HANDLE, 0);
    say("Process memory: %lu MiB in all, %lu MiB used at start; core mask 0x%lx\n", total >> 20, used >> 20, cores);

    u64 hw = 0, ams = 0;
    if (R_SUCCEEDED(splInitialize())) {
        if (R_SUCCEEDED(splGetConfig(SplConfigItem_HardwareType, &hw))) say("Model: %s\n", hardwareName(hw));
        if (R_SUCCEEDED(splGetConfig((SplConfigItem)65000, &ams)))
            say("Atmosphere %lu.%lu.%lu\n", (ams >> 56) & 0xFF, (ams >> 48) & 0xFF, (ams >> 40) & 0xFF);
        splExit();
    } else say("Model: spl not available\n");
    say("Firmware %u.%u.%u\n", HOSVER_MAJOR(hosversionGet()), HOSVER_MINOR(hosversionGet()), HOSVER_MICRO(hosversionGet()));

    say("Mode: %s\n", appletGetOperationMode() == AppletOperationMode_Console ? "docked" : "handheld");
    if (R_SUCCEEDED(psmInitialize())) {
        u32 percent = 0;
        PsmChargerType charger = 0;
        psmGetBatteryChargePercentage(&percent);
        psmGetChargerType(&charger);
        say("Battery %u%%, charger: %s\n", percent,
            charger == PsmChargerType_Unconnected ? "none" : charger == PsmChargerType_EnoughPower ? "full power"
            : charger == PsmChargerType_LowPower ? "low power" : "not supported");
        psmExit();
    }

    // Threads on each core: the game should have three; the fourth is said to be the system's.
    Thread threads[4];
    for (int c = 0; c < 4; c++) {
        coreRan[c] = 0;
        if (R_SUCCEEDED(threadCreate(&threads[c], onCore, (void *)(uintptr_t)c, NULL, 0x4000, 0x2C, c))) {
            threadStart(&threads[c]);
            threadWaitForExit(&threads[c]);
            threadClose(&threads[c]);
        }
    }
    say("Threads ran on cores: %s%s%s%s\n", coreRan[0] ? "0 " : "", coreRan[1] ? "1 " : "", coreRan[2] ? "2 " : "",
        coreRan[3] ? "3" : "(not 3)");

    // The largest single allocation, then how much can be held in 64 MiB blocks.
    size_t largest = 0;
    for (size_t size = (size_t)4096 << 20; size >= (1 << 20); size -= (16 << 20)) {
        void *p = malloc(size);
        if (p) { largest = size; free(p); break; }
    }
    void *blocks[128];
    int held = 0;
    while (held < 128 && (blocks[held] = malloc(64 << 20)) != NULL) held++;
    for (int i = 0; i < held; i++) free(blocks[i]);
    say("Heap: largest block %zu MiB; %d MiB held in 64 MiB blocks\n", largest >> 20, held * 64);

    // Clocks: as found, then 1,785 MHz through clkrst, then the official loading boost; put back after.
    bool clkrst = hosversionAtLeast(8, 0, 0) && R_SUCCEEDED(clkrstInitialize());
    if (clkrst) {
        sayClocks("as found");
        u32 original = clockOf(PcvModuleId_CpuBus);
        say("CPU loop at %u MHz: %.3f s\n", original / 1000000, cpuLoop());

        ClkrstSession cpu;
        if (R_SUCCEEDED(clkrstOpenSession(&cpu, PcvModuleId_CpuBus, 3))) {
            Result rc = clkrstSetClockRate(&cpu, 1785000000);
            u32 now = 0;
            clkrstGetClockRate(&cpu, &now);
            say("clkrst to 1,785 MHz: %s, now %u MHz\n", R_SUCCEEDED(rc) ? "accepted" : "refused", now / 1000000);
            say("CPU loop at %u MHz: %.3f s\n", now / 1000000, cpuLoop());
            clkrstSetClockRate(&cpu, original);
            clkrstGetClockRate(&cpu, &now);
            say("Put back: %u MHz\n", now / 1000000);
            clkrstCloseSession(&cpu);
        }

        Result rc = appletSetCpuBoostMode(ApmCpuBoostMode_FastLoad);
        say("apm FastLoad boost: %s\n", R_SUCCEEDED(rc) ? "accepted" : "refused");
        if (R_SUCCEEDED(rc)) {
            svcSleepThread(100000000);
            sayClocks("in FastLoad");
            say("CPU loop in FastLoad: %.3f s\n", cpuLoop());
            appletSetCpuBoostMode(ApmCpuBoostMode_Normal);
            svcSleepThread(100000000);
            sayClocks("after FastLoad");
        }
        clkrstExit();
    } else say("clkrst not available (firmware before 8.0.0?)\n");

    sdSpeed();

    say("\nDone. Log: sdmc:/switch/OBNS-probe/probe.log\n");
    if (logFile) fclose(logFile);
    logFile = NULL;

    if (viaNxlink) {
        socketExit();
    } else {
        say("Press + to exit.\n");
        padConfigureInput(1, HidNpadStyleSet_NpadStandard);
        PadState pad;
        padInitializeDefault(&pad);
        while (appletMainLoop()) {
            padUpdate(&pad);
            if (padGetButtonsDown(&pad) & HidNpadButton_Plus) break;
            consoleUpdate(NULL);
        }
        consoleExit(NULL);
    }
    return 0;
}
