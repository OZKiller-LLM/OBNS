# Windows on ARM64 (OBNS 1.31)

This folder is the Windows on ARM64 build, as `Android/` is the Android port's: what only that build needs. 1.31 makes
the Windows program run natively on ARM64 Windows (Snapdragon X), and tunes it for Snapdragon's Oryon CPU and Adreno X
GPU. It starts from 1.3, so the whole game is drawn by Direct3D or Vulkan; OpenGL on these laptops is only Microsoft's
OpenGL Compatibility Pack (OpenGL over Direct3D 12), so the alone modes matter most here.

## Why a build of its own

The x64 program is AnyCPU for .NET Framework 4.8. On a Snapdragon X1-26-100 laptop with 4.8.1 installed, Windows still
ran it as x64 under emulation (`xtajit64se.dll` loaded, machine 0x8664), and changing only the config's
`supportedRuntime` to 4.8.1 didn't change that: the program itself has to target 4.8.1. So the ARM64 build's
`OpenBve.exe` targets 4.8.1 and is built for ARM64 only, and can't fall back to emulation. It needs Windows 11 (4.8.1
is there from 22H2).

## The folder

| File | What it is |
| --- | --- |
| `build-arm64.ps1` | Builds it: OBNS's desktop build (`OBNS/build-desktop.ps1`) in its own folder, `ARM64/build`, then the ARM64 DLLs beside the program. `-Package` zips it as `OpenBVE-<release>-OBNS-win-arm64.zip`, without the x64 DLLs and 32-bit copies. |
| `prepare-mirror.ps1` | The ARM64 build's changes to its own mirror of upstream: `OpenBve.exe` for 4.8.1 and ARM64, no 32-bit copies (LBAHeader), OBNS/shared by its full path. Upstream's files and the x64 build are untouched. |
| `dependencies/` | Native DLLs built for ARM64: OpenAL Soft (`dependencies/README.md` has its source and licence). |

The libraries and plugins stay AnyCPU for 4.8, which a 4.8.1 ARM64 process loads natively. Win32PluginProxy stays x86,
run by Windows' x86 emulation.

## In OBNS's shared code

| Part | Change |
| --- | --- |
| OpenGL stubs (`GLStub`) | The ARM64 stubs, as on Apple silicon, in Windows memory (VirtualAlloc, VirtualProtect, FlushInstructionCache). The alone modes and the options' checkbox are possible on ARM64 Windows. |
| Sound (`SoundManager/Sounds.cs`) | OpenAL Soft is looked for in `arm64\` in an ARM64 process (`x64\` and `x86\` as before). |
| Train plugins | An x64 plugin (BVE5's Ats64, BVE6) runs in the x86/x64 layer shared with the Android port, as on Linux and macOS: an ARM64 process can't load an x64 DLL. An x86 plugin keeps upstream's plugin proxy. |
| Log | "OBNS process:" says the process's architecture and whether Windows emulates it. |

## Results so far (Snapdragon X1-26-100, Adreno X1-45, driver 31.0.152.0)

Run as x64 under emulation (the x64 package), mackoy's Keisei (BVE5), exterior view: every renderer alone, and OpenGL
through the Compatibility Pack, keeps 60 fps (the display's rate) after one or two long frames at the start, 534 draws,
and draws correctly. Vulkan is Qualcomm's own driver (an ARM64X build serving x64 processes too), not Dozen. Direct3D 9
goes through Direct3D 9 on 12. Both trains' x86 plugins load through the proxy.

Natively (build 131d), every renderer, 60 s after loading, display at 60 Hz with vsync:

| Renderer | Keisei: load, first 10 s (fps / worst) | Then | Uchibo 3793F: load, first 10 s |
| --- | --- | --- | --- |
| Vulkan (default) | 5.2 s, 55.7 / 630 ms (emulated 8.4 s, 53.3 / 974 ms) | 60 fps, worst 18 to 20 ms | 18.2 s (emulated 27.9 s), 54.6 / 651 ms |
| Direct3D 12 | 7.6 s, 58.3 / 292 ms | 60 fps, worst 20 ms | 20.1 s, 57.4 / 324 ms |
| Direct3D 11 | 7.3 s, 57.0 / 482 ms | 60 fps, worst 20 ms | |
| Direct3D 9 (9 on 12) | 5.2 s, 57.2 / 416 ms | 60 fps, worst 19 ms | |
| OpenGL (OpenGL on 12) | 5.4 s, 52.9 / 581 ms | 60 fps, worst 20 ms | |

Loading is about 30 to 35% faster than under emulation and the first long frame 25 to 40% shorter; drawing is held by vsync
either way. Snapshots right on every renderer. Drivers: Qualcomm's ARM64 Vulkan, Direct3D 12 and 11; Direct3D 9 and
OpenGL through Microsoft's layers onto Direct3D 12.

The first native start, Keisei: "OBNS process: Arm64, native", Vulkan alone on Qualcomm's ARM64 driver, OpenAL
Soft for ARM64 loaded, loading 8.5 s, the first 10 s 55 fps with a worst frame of 0.7 s (emulated 53 fps, 0.9 to 1.0 s),
then 60 fps, and 785 MB of process memory against 908 MB emulated. Two x64-only libraries stopped the first native
builds: PI Engineering's PIEHid64Net (the RailDriver, left out on ARM64) and Tolk (screen readers: none on ARM64 yet).

Without vsync (131e, Uchibo 3793F, 1,337 draws, the clean windows): Direct3D 11 143.7 fps, Vulkan 140.9, OpenGL 128.5
(its first frame 2.5 s, nearly all in OpenGL on 12's set-up), Direct3D 12 exactly 120.0 in every window (its time is
the wait for the frame two back, not drawing: a pace to twice the display's rate, still to explain), Direct3D 9 108.9.
Uchibo is too light to choose the default; TML decides.

Vulkan's pipeline cache (131e, Keisei): 8 pipelines in 197 ms on the first start, 2 ms on the next; the first long frame
497 ms, then 317 ms. About 177 ms of that frame is outside the timed parts in both runs (likely the JIT compiling the
first frame's code, slower on ARM64), and "Vulkan start" (52 ms) and "touch and overlays" (43 ms) don't change.

Plugins (131d): Ats32 runs through the proxy under x86 emulation on both trains, and the E217's six-DLL x64 chain runs in
the plugin layer (`OBNS_ATS64=1`) with its TIMS monitor live and no measurable frame cost against the proxy. Keisei's
KqAts64.dll is a Debug build and stopped on `ucrtbased`; the layer now maps the Debug runtime (131f).

## To do

1. **The native build on the laptop:** each renderer; the x64 plugins in the layer; sound heard.
2. **The GPU list's default for Adreno X** from those numbers.
3. **Screen readers:** Tolk for ARM64, if the user wants it (LGPL, built from source like OpenAL Soft).

## Snapdragon tuning (after the native build's first numbers)

- **Unified memory.** Adreno X shares the system's LPDDR5X. Where Direct3D 12 reports cache-coherent UMA, or Vulkan
  has memory that is both device-local and host-visible, write vertices, indices and textures straight into it and skip
  the staging copy.
- **A tile-based GPU.** Adreno X can draw in tiles, so what is loaded from and stored to memory costs the most: depth
  discarded at the end of a frame (`DiscardResource`, `DiscardView`, Vulkan's `DONT_CARE`), clears instead of loads, and
  as few render target changes as possible (shadow map, world, 2D).
- **Bandwidth.** BC1/BC3 compressed textures, as planned for low-memory Android and the Switch, as an option here.
- **CPU.** Oryon cores are all equal, with no SMT: loading and the parallel updates use one thread a core (8 to 12).
- **Power.** Keep the game's own process out of Windows' power throttling (EcoQoS) while driving: a setting for this
  process only, never the system's.
- **Shaders.** Cache compiled pipelines between runs (Direct3D 12 pipeline libraries, a Vulkan pipeline cache), since
  Qualcomm's compiler is slower on first use.
- **The start.** The pipeline cache took 180 ms off the first frame. Most of what's left is untimed and probably the JIT:
  native images (NGen) would remove it, but installing them needs administrator rights, so only as an installer's
  optional step, the user's call.
