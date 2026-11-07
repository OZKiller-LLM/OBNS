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

// The watchdog's way out of a game stuck for good (OBNS 1.31N, the drive benchmark): a fault on purpose, so Atmosphère's
// crash report records every thread's registers and stack - where the game stands natively, which its log cannot say.
EXPORT void obns_watchdog_crash(void)
{
    *(volatile u32 *)0 = 0x0B05DEADu;
}

// The native heap in use: malloc's, which the process's used memory can't show (libnx reserves it all up front).
EXPORT int64_t obns_native_heap_used(void)
{
    struct mallinfo info = mallinfo();
    return (int64_t)info.uordblks;
}

// Clocks. Loading: apm's FastLoad (mode 1: CPU 1,785 MHz, the GPU held low). Play, by the performance mode in Options
// (the user, 2026-11-06):
//   2 Normal    - the system's own clocks and fan control;
//   3 Turbo     - the highest clocks Nintendo gives games: CPU 1,785 MHz (its boost clock), GPU 768 MHz docked and
//                 460.8 MHz handheld, memory 1,600 MHz; the fan at full;
//   4 Overclock - each clock at the highest rate the console's clock table lists, up to the chip's ratings (the
//                 overclocking guides for the first model, researched 2026-11-06: past them "unstable" or "very
//                 dangerous"): plugged in, CPU 2,091 MHz, GPU 921.6 MHz (998.4 only with Horizon OC's undervolt 2), memory 1,600 MHz (its
//                 rating; with Horizon OC's kip, the highest listed up to 2,133 MHz - unstable memory clocks corrupt
//                 the system storage and the SD card, so its rate is chosen and tested in Horizon OC); on battery, where over
//                 8.6 W harms the battery, CPU 1,785 MHz and GPU 460.8 MHz. The stock table stops at CPU 1,785, GPU
//                 921.6 and memory 1,600 MHz; an overclocking module extending it raises them, never past these. Fan
//                 at full.
// Each target is snapped to the highest listed rate not above it. Mode 0 (and OBNS's end) puts every clock back as it
// was and gives the fan back to the system. Returns the CPU's MHz afterwards.
enum { CLOCKS = 3 };
static const PcvModuleId clockModules[CLOCKS] = { PcvModuleId_CpuBus, PcvModuleId_GPU, PcvModuleId_EMC };
static u32 originalHz[CLOCKS], setHz[CLOCKS];
static bool clocksTouched[CLOCKS];
static bool fanHeld;
static FanController fan;
static float originalFan = -1.0f;

// The highest listed rate not above `target`; `target` itself if the system lists none.
static u32 listed_rate(ClkrstSession *session, u32 target)
{
    u32 rates[64];
    s32 count = 0;
    PcvClockRatesListType type = PcvClockRatesListType_Invalid;
    if (R_FAILED(clkrstGetPossibleClockRates(session, rates, 64, &type, &count)) || count <= 0) return target;
    u32 best = 0, lowest = 0xFFFFFFFFu;
    for (s32 i = 0; i < count && i < 64; i++) {
        if (rates[i] < lowest) lowest = rates[i];
        if (rates[i] <= target && rates[i] > best) best = rates[i];
    }
    return best ? best : lowest;
}

// What a mode asks of a clock (the highest listed rate not above it is set); 0 leaves it to the system.
static bool on_battery(void)
{
    PsmChargerType charger = PsmChargerType_Unconnected;
    if (R_FAILED(psmInitialize())) return false;
    bool battery = R_SUCCEEDED(psmGetChargerType(&charger)) && charger == PsmChargerType_Unconnected;
    psmExit();
    return battery;
}

/*
 * Overclock's caps plugged in: CPU 2,091 and GPU 998.4 MHz, the Erista guide's ceilings (the user's choice; the guide wants
 * CPU undervolt 1 and GPU undervolt 2 under 950 mV for them, set in Horizon OC - without one the console lost its
 * network service, then OBNS's audio, 2026-11-07), memory 1,600 MHz (its rating: unstable memory corrupts the system
 * storage and the SD card). UserData/nx-overclock.ini - lines cpu_mhz=, gpu_mhz=, memory_mhz= - sets them otherwise,
 * never past the guide's ceilings: CPU 2,091, GPU 998.4, memory 2,133 MHz.
 */
// The user, 2026-11-07: "Allow the 2091 MHz / 998 MHz clocks" - the guide's ceilings plugged in, by default.
static u32 ocCpuHz = 2091000000u, ocGpuHz = 998400000u, ocMemoryHz = 1600000000u;

static void read_overclock_caps(void)
{
    static bool read;
    if (read) return;
    read = true;
    FILE *f = fopen("sdmc:/switch/OBNS/UserData/nx-overclock.ini", "r");
    if (!f) return;
    char line[128];
    while (fgets(line, sizeof line, f)) {
        unsigned mhz = 0;
        if (sscanf(line, " cpu_mhz = %u", &mhz) == 1) ocCpuHz = (mhz > 2091 ? 2091 : mhz) * 1000000u;
        else if (sscanf(line, " gpu_mhz = %u", &mhz) == 1) ocGpuHz = mhz >= 998 ? 998400000u : mhz * 1000000u;
        else if (sscanf(line, " memory_mhz = %u", &mhz) == 1) ocMemoryHz = (mhz > 2133 ? 2133 : mhz) * 1000000u;
    }
    fclose(f);
    printf("OBNS: overclock caps from nx-overclock.ini: CPU %u, GPU %u, memory %u MHz\n", ocCpuHz / 1000000u, ocGpuHz / 1000000u, ocMemoryHz / 1000000u);
}

static u32 target_rate(int mode, int clock)
{
    bool docked = appletGetOperationMode() == AppletOperationMode_Console;
    /*
     * The user's clocks, 2026-11-07. Turbo: on battery CPU 1,581 and GPU 460.8 MHz (Horizon OC's Erista handheld caps),
     * plugged in or docked CPU 1,785 and GPU 921.6 MHz. Overclock: CPU 2,091 and GPU 998.4 MHz whatever the power source
     * (the ocCpuHz/ocGpuHz caps, nx-overclock.ini; on battery past the Erista guide's 8.6 W advice, the user's choice -
     * where Horizon OC or the system holds a clock lower, the frame log's live clocks show it). Memory 1,600 MHz.
     */
    if (mode == 4) {
        read_overclock_caps();
        if (clock == 0) return ocCpuHz;
        if (clock == 1) return ocGpuHz;
        return ocMemoryHz;
    }
    if (mode == 3) {
        bool powered = docked || !on_battery();
        if (clock == 0) return powered ? 1785000000u : 1581000000u;
        if (clock == 1) return powered ? 921600000u : 460800000u;
        return 1600000000u;
    }
    return 0;
}

// Once: the rates each clock's table lists, in the log - what an overclocking module has unlocked shows there.
static void log_rates(void)
{
    static bool logged;
    if (logged) return;
    logged = true;
    static const char *names[CLOCKS] = { "CPU", "GPU", "memory" };
    for (int i = 0; i < CLOCKS; i++) {
        ClkrstSession session;
        if (R_FAILED(clkrstOpenSession(&session, clockModules[i], 3))) continue;
        u32 rates[64];
        s32 count = 0;
        PcvClockRatesListType type = PcvClockRatesListType_Invalid;
        if (R_SUCCEEDED(clkrstGetPossibleClockRates(&session, rates, 64, &type, &count)) && count > 0) {
            printf("OBNS: clocks: %s lists", names[i]);
            for (s32 r = 0; r < count && r < 64; r++) printf(" %u", rates[r] / 1000000u);
            printf(" MHz\n");
        }
        clkrstCloseSession(&session);
    }
}

static void apply_clocks(int mode, bool settle)
{
    if (!hosversionAtLeast(8, 0, 0) || R_FAILED(clkrstInitialize())) return;
    log_rates();
    for (int i = 0; i < CLOCKS; i++) {
        u32 target = target_rate(mode, i);
        // Clocks no mode has set are left alone, even when putting back.
        if (!target && !clocksTouched[i]) continue;
        ClkrstSession session;
        if (R_FAILED(clkrstOpenSession(&session, clockModules[i], 3))) continue;
        if (!clocksTouched[i]) {
            clkrstGetClockRate(&session, &originalHz[i]);
            clocksTouched[i] = true;
        }
        if (target) clkrstSetClockRate(&session, listed_rate(&session, target));
        else if (originalHz[i]) {
            clkrstSetClockRate(&session, originalHz[i]);
            clocksTouched[i] = false;
        }
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

static int heldMode;

EXPORT int obns_set_cpu_boost(int mode)
{
    appletSetCpuBoostMode(mode == 1 ? ApmCpuBoostMode_FastLoad : ApmCpuBoostMode_Normal);
    heldMode = mode;
    apply_clocks(mode, true);
    set_fan(mode == 3 || mode == 4);
    if (!clocksTouched[0] && hosversionAtLeast(8, 0, 0) && R_SUCCEEDED(clkrstInitialize())) {
        // The CPU's clock as the system has it, for the log.
        ClkrstSession cpu;
        if (R_SUCCEEDED(clkrstOpenSession(&cpu, PcvModuleId_CpuBus, 3))) {
            clkrstGetClockRate(&cpu, &setHz[0]);
            clkrstCloseSession(&cpu);
        }
        clkrstExit();
    }
    return (int)(setHz[0] / 1000000);
}

// Turbo and Overclock held: the system sets its own clocks again on docking and undocking (and Turbo's GPU clock
// follows the mode), its thermal control its own fan level, so the game's loop calls this every second. Quick (no
// settling). Returns the GPU's MHz (0 in Normal, the system's).
EXPORT int obns_hold_clocks(void)
{
    if (heldMode != 3 && heldMode != 4) return 0;
    apply_clocks(heldMode, false);
    set_fan(true);
    return (int)(setHz[1] / 1000000);
}

// The clocks as last set, in MHz: CPU, GPU, memory (0 for one left to the system).
EXPORT void obns_clock_rates(int *cpu, int *gpu, int *memory)
{
    *cpu = (int)(setHz[0] / 1000000);
    *gpu = clocksTouched[1] ? (int)(setHz[1] / 1000000) : 0;
    *memory = clocksTouched[2] ? (int)(setHz[2] / 1000000) : 0;
}

// The clocks as they are now, read back from the system (what another program may have set shows here), in MHz.
EXPORT void obns_clock_now(int *cpu, int *gpu, int *memory)
{
    int *out[CLOCKS] = { cpu, gpu, memory };
    for (int i = 0; i < CLOCKS; i++) *out[i] = 0;
    if (!hosversionAtLeast(8, 0, 0) || R_FAILED(clkrstInitialize())) return;
    for (int i = 0; i < CLOCKS; i++) {
        ClkrstSession session;
        if (R_FAILED(clkrstOpenSession(&session, clockModules[i], 3))) continue;
        u32 hz = 0;
        if (R_SUCCEEDED(clkrstGetClockRate(&session, &hz))) *out[i] = (int)(hz / 1000000u);
        clkrstCloseSession(&session);
    }
    clkrstExit();
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
