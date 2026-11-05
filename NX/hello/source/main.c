// OBNS 1.31N: starts Mono with the C# hello compiled ahead of time, its output sent back to nxlink.
// From mono-nx's native/aot/source/main.c (MIT, Copyright (c) exelix11).
// Copyright (c) The OpenBVE Android port contributors.

#include "core.h"
#include <unistd.h>

#define STATIC_MONO_SYM(x) do {\
    extern void* x; \
    mono_aot_register_module(x);\
} while(0)

// Clocks for the C# side, which has no P/Invoke into clkrst of its own yet: 1 sets 1,785 MHz, 0 puts the clock back.
static u32 originalHz;
__attribute__((visibility("default"))) int obns_set_boost(int on)
{
    ClkrstSession cpu;
    if (R_FAILED(clkrstInitialize())) return -1;
    if (R_FAILED(clkrstOpenSession(&cpu, PcvModuleId_CpuBus, 3))) { clkrstExit(); return -2; }
    if (!originalHz) clkrstGetClockRate(&cpu, &originalHz);
    clkrstSetClockRate(&cpu, on ? 1785000000 : originalHz);
    u32 now = 0;
    clkrstGetClockRate(&cpu, &now);
    clkrstCloseSession(&cpu);
    clkrstExit();
    return (int)(now / 1000000);
}

int main(int argc, char *argv[])
{
    // Sent by nxlink -s: stdout and stderr go back to the PC.
    bool nxlink = R_SUCCEEDED(socketInitializeDefault()) && nxlinkStdio() >= 0;

    romfsInit();

    // Mono doesn't like : in paths: cd to the romfs so it loads the assemblies from there.
    chdir("romfs:/");

    if (!application_initialize("romfs:/aot_config.ini"))
        return 1;

    // Output from build.sh
    #include "mono_symbols.h"

    mono_jit_set_aot_mode(MONO_AOT_MODE_FULL);

    application_configure_mono();

    MonoDomain *domain = mono_jit_init("obns");
    if (!domain)
    {
        fatal_error("Failed to initialize mono domain\n");
        return 1;
    }

    MonoAssembly *assembly = mono_domain_assembly_open(domain, g_config.default_assembly);
    if (!assembly)
    {
        fatal_error("Failed to load assembly");
        return 1;
    }

    char *monoargs[] = {g_config.default_assembly};
    mono_jit_exec(domain, assembly, 1, monoargs);

    obns_set_boost(0);
    mono_jit_cleanup(domain);
    romfsExit();
    if (nxlink) socketExit();
    application_terminate();
    return 0;
}
