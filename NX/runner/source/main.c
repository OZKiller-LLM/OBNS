// OBNS 1.31N: the Switch runner. Starts Mono with OBNS's C#, compiled ahead of time by LLVM and linked in.
// From mono-nx's native/aot/source/main.c (MIT, Copyright (c) exelix11).
// Copyright (c) The OpenBVE Android port contributors. BSD-2-Clause, as OBNS.
//
// The assemblies' metadata is read from sdmc:/switch/OBNS/managed (the NRO holds only code): the runner changes
// directory to sdmc:/switch/OBNS first, so paths without a device, which .NET on Unix needs ("sdmc:/" isn't rooted
// there), land on the SD card. Output goes to nxlink's console when sent by nxlink -s, and to UserData/runner.log.

#include "core.h"
#include <malloc.h>
#include <stdarg.h>
#include <sys/stat.h>
#include <unistd.h>

#define EXPORT __attribute__((visibility("default")))

// Mono's embedding entry for runtime properties (mono-private-unstable.h).
int monovm_initialize(int propertyCount, const char **propertyKeys, const char **propertyValues);

#define STATIC_MONO_SYM(x) do {\
    extern void* x; \
    mono_aot_register_module(x);\
} while(0)

// The process's memory in use (title takeover gives 3,189 MiB in all), for the C# side's load report.
EXPORT int64_t obns_memory_used(void)
{
    u64 used = 0;
    svcGetInfo(&used, InfoType_UsedMemorySize, CUR_PROCESS_HANDLE, 0);
    return (int64_t)used;
}

// The native heap in use: malloc's, which the process's used memory can't show (libnx reserves it all up front).
EXPORT int64_t obns_native_heap_used(void)
{
    struct mallinfo info = mallinfo();
    return (int64_t)info.uordblks;
}

// Clocks (the user, 2026-11-04): 0 stock, 1 apm's FastLoad (CPU 1,785 MHz, GPU held low: loading), 2 the CPU at 1,785
// MHz through clkrst (play on power), 3 everything at its highest (the user, 2026-11-06: "the CPU and GPUs ... at their
// max clocks, with the fan at full RPM"): the CPU, the GPU and the memory each at the highest rate the system lists for
// it - the console's own table, no clock beyond it - and the fan at full. For OBNS's run only: mode 0 puts back each
// clock as it was and gives the fan back to the system. Returns the CPU's MHz afterwards.
enum { CLOCKS = 3 };
static const PcvModuleId clockModules[CLOCKS] = { PcvModuleId_CpuBus, PcvModuleId_GPU, PcvModuleId_EMC };
static u32 originalHz[CLOCKS], highestHz[CLOCKS], setHz[CLOCKS];
static bool fanHeld;
static FanController fan;
static float originalFan = -1.0f;

// The highest rate the system lists for a clock (a list of rates, or a range's top); 0 if it gives none.
static u32 highest_rate(ClkrstSession *session)
{
    u32 rates[64];
    s32 count = 0;
    PcvClockRatesListType type = PcvClockRatesListType_Invalid;
    if (R_FAILED(clkrstGetPossibleClockRates(session, rates, 64, &type, &count)) || count <= 0) return 0;
    u32 top = 0;
    for (s32 i = 0; i < count && i < 64; i++) if (rates[i] > top) top = rates[i];
    return top;
}

// Sets the clocks for a mode (see above); the rates each clock ended at go to setHz.
static void apply_clocks(int mode, bool settle)
{
    if (!hosversionAtLeast(8, 0, 0) || R_FAILED(clkrstInitialize())) return;
    for (int i = 0; i < CLOCKS; i++) {
        // Only the CPU in modes 1 and 2; all three in 3; all three back in 0.
        if (i > 0 && (mode == 1 || mode == 2)) continue;
        ClkrstSession session;
        if (R_FAILED(clkrstOpenSession(&session, clockModules[i], 3))) continue;
        if (!originalHz[i]) clkrstGetClockRate(&session, &originalHz[i]);
        if (mode == 3 && !highestHz[i]) highestHz[i] = highest_rate(&session);
        if (mode == 3 && highestHz[i]) clkrstSetClockRate(&session, highestHz[i]);
        else if (mode == 2 && i == 0) clkrstSetClockRate(&session, 1785000000);
        else if (mode == 0 && originalHz[i]) clkrstSetClockRate(&session, originalHz[i]);
        if (settle) svcSleepThread(20000000);
        clkrstGetClockRate(&session, &setHz[i]);
        clkrstCloseSession(&session);
    }
    clkrstExit();
}

static void set_fan(bool full)
{
    if (full && !fanHeld) {
        if (R_FAILED(fanInitialize())) return;
        // The fan's device code (as fan-control homebrew opens it).
        if (R_FAILED(fanOpenController(&fan, 0x3D000001))) { fanExit(); return; }
        if (originalFan < 0) fanControllerGetRotationSpeedLevel(&fan, &originalFan);
        fanHeld = true;
    }
    if (!fanHeld) return;
    if (full) {
        fanControllerSetRotationSpeedLevel(&fan, 1.0f);
        return;
    }
    // Back to the level it had; the system's thermal control carries on from there.
    fanControllerSetRotationSpeedLevel(&fan, originalFan >= 0 ? originalFan : 0.5f);
    fanControllerClose(&fan);
    fanExit();
    fanHeld = false;
}

EXPORT int obns_set_cpu_boost(int mode)
{
    appletSetCpuBoostMode(mode == 1 ? ApmCpuBoostMode_FastLoad : ApmCpuBoostMode_Normal);
    apply_clocks(mode, true);
    set_fan(mode == 3);
    return (int)(setHz[0] / 1000000);
}

// Mode 3 held: the system sets its own clocks again on docking and undocking, and its thermal control its own fan
// level, so the game's loop calls this every few seconds. Quick (no settling). Returns the GPU's MHz.
EXPORT int obns_hold_max_clocks(void)
{
    apply_clocks(3, false);
    set_fan(true);
    return (int)(setHz[1] / 1000000);
}

// The clocks as last set, in MHz: CPU, GPU, memory.
EXPORT void obns_clock_rates(int *cpu, int *gpu, int *memory)
{
    *cpu = (int)(setHz[0] / 1000000);
    *gpu = (int)(setHz[1] / 1000000);
    *memory = (int)(setHz[2] / 1000000);
}

// An exception nothing caught: printed (to nxlink, or UserData/runner.log) before mono-nx's error screen, which alone
// shows only the first line unless its details are opened.
static void obns_unhandled(MonoObject *exc, void *user_data)
{
    MonoString *text = mono_object_to_string(exc, NULL);
    char *utf8 = text ? mono_string_to_utf8(text) : NULL;
    printf("--- OBNS: unhandled exception ---\n%s\n", utf8 ? utf8 : "(no text)");
    fflush(stdout);
    fatal_error(utf8 ? utf8 : "unhandled exception");
    if (utf8) mono_free(utf8);
}

int main(int argc, char *argv[])
{
    bool nxlink = R_SUCCEEDED(socketInitializeDefault()) && nxlinkStdio() >= 0;

    mkdir("sdmc:/switch/OBNS", 0777);
    mkdir("sdmc:/switch/OBNS/UserData", 0777);
    chdir("sdmc:/switch/OBNS/");
    if (!nxlink) {
        // Without nxlink, the runner's own messages (Mono's start, a fatal error) go to a file.
        freopen("sdmc:/switch/OBNS/UserData/runner.log", "w", stdout);
        freopen("sdmc:/switch/OBNS/UserData/runner.log", "a", stderr);
    }
    printf("OBNS runner (1.31N): %s\n", nxlink ? "output to nxlink" : "output to UserData/runner.log");
    // No auto-sleep while OBNS runs, as games during long loads and video: the console slept through a TML load
    // with nothing on the screen yet, suspending it and dropping nxlink (2026-11-06). For this app only; the
    // console's own sleep setting is untouched, and the system restores it when the app exits.
    appletSetAutoSleepDisabled(true);

    // Invariant globalisation: ICU's 30 MB of data left out. OpenBVE parses with the invariant culture.
    setenv("DOTNET_SYSTEM_GLOBALIZATION_INVARIANT", "1", 1);
    // What a Unix desktop has and the Switch doesn't: a home, for .NET's special folders (upstream's FileSystem
    // builds its user data folder from ApplicationData, and an empty one throws).
    // The garbage collector's nursery at sgen's largest (32 MB, the default 4): TML's load ran 1,450 minor
    // collections, each scanning a heap of up to 700 MB for references into the nursery (2026-11-06, 669 s).
    // A value already in the environment wins, for comparing settings.
    setenv("MONO_GC_PARAMS", "nursery-size=32m", 0);
    setenv("HOME", "/switch/OBNS/UserData", 1);
    setenv("XDG_CONFIG_HOME", "/switch/OBNS/UserData", 1);
    setenv("XDG_DATA_HOME", "/switch/OBNS/UserData", 1);

    if (!application_initialize("sdmc:/switch/OBNS/managed/obns.ini"))
        return 1;

    // From NX/build-nro.sh: every assembly compiled ahead of time, registered with Mono.
    #include "mono_symbols.h"

    mono_jit_set_aot_mode(MONO_AOT_MODE_FULL);
    application_configure_mono();

    // AppContext.BaseDirectory, which the Android port's FileSystem falls back on when Assembly.Location is empty (as
    // it is here: mono-nx's images have no file name). A host sets it; mono-nx doesn't.
    const char *keys[] = { "APP_CONTEXT_BASE_DIRECTORY" };
    const char *values[] = { "/switch/OBNS/managed/" };
    monovm_initialize(1, keys, values);

    MonoDomain *domain = mono_jit_init("obns");
    if (!domain) {
        fatal_error("Mono could not start (is /switch/OBNS/managed on the SD card?)");
        return 1;
    }
    mono_install_unhandled_exception_hook(obns_unhandled, NULL);

    MonoAssembly *assembly = mono_domain_assembly_open(domain, g_config.default_assembly);
    if (!assembly) {
        fatal_error("OpenBve.NX.dll could not be loaded from /switch/OBNS/managed");
        return 1;
    }

    char *monoargs[] = {g_config.default_assembly};
    int result = mono_jit_exec(domain, assembly, 1, monoargs);
    printf("OBNS ended (%d)\n", result);

    obns_set_cpu_boost(0);
    appletSetAutoSleepDisabled(false);
    mono_jit_cleanup(domain);
    if (nxlink) socketExit();
    application_terminate();
    return 0;
}
