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

TML (131f, native, Vulkan alone, vsync on, exterior, the benchmark's TMLDN 20040 and HK-MTR-1141A-8cars, cache warm):
loading 39 s; 51.9 fps (45 once more scenery comes into view), worst frames 20 to 43 ms; 5,178 draws; held by the GPU's
world pass (15 ms of a 19 ms frame), not vsync; 2,952 MB of process memory. For comparison on the same scene: the M1's
Metal 43 fps, the MacBook Neo's 44, the Lisuan's Vulkan 62 to 70. The heavier TMLDN 20144 with the SP1900: loading
106 s, 40 to 45 fps, 7,944 draws, 5.3 GB of process memory, the texture and vertex pools 90% full.

East Rail (131f, the same settings; 東鐵線南行 羅湖→金鐘, M-Train A195-222(DN)): loading 29 s; 44 fps steady, worst frames
23 to 30 ms; 5,320 draws, again held by the world pass (19 ms of 21); 3,453 MB of process memory, the texture pool 92%
full. Its plugin is .NET and runs in the program itself, natively.

Direct3D 12's GPU time per part (131i, TML 20040, exterior, 960x600, vsync off): shadows off, 74.4 fps, the world 13.07
ms of 13.29; the depths discarded or not, no difference (74.4 against 74.9 fps). Shadows High (3 cascades of 2048):
33.7 fps, shadows 19.78 ms, the world 9.43, 29.47 a frame. The swap chain allows tearing, so the 120 fps on light routes
is something else.

The shadow cascades staggered (131j, the same scene; the nearest cascade drawn every frame, the farther two in turn, each
kept in the light space it was drawn in; `OBNS_SHADOW_STAGGER=0` turns it off). At High, 44.3 fps against 34.3 with
it off; shadows 12.81 ms against 19.63 (35% less), the world 9.38, 22.41 a frame; worst frames 45.6 ms against 36.9,
leaving out the snapshot frames. Driving at High, 48.7 to 49.8 fps while moving. Snapshots of the train at rest are
bit-identical across 30 s with it on and off, and nothing went back and forth while moving (sampled every 2 s, so not
frame by frame). Ultra (3 cascades of 4096): 29.4 fps, shadows 23.53 ms. Its HUD, missing in 131i, draws: 260 2D
quads, as at High, and the same HUD pixels. Process memory 2.65 to 2.84 GB, no exceptions.

The copies of a shadow caster's face drawn as one, with an instance each (131k, the same scene at High, staggered;
`OBNS_SHADOW_INSTANCES=0` turns it off): 48.5 fps against 44.9; shadow draw calls 3,173 and 4,186 on alternate
frames against 4,731 and 6,075; shadows 10.24 ms against 12.43, 20.43 a frame against 22.11; worst frames 29.6 ms
against 31.2. One pair of snapshots bit-identical; the other differs by 1 or 2 levels in scattered pixels over the
whole frame, the sky and HUD included, not in shadow-shaped patches. The world and cab's draws the same way (131l,
`OBNS_WORLD_INSTANCES=0`): East Rail's 5,320 draws in 4,146 calls on the RTX 2060. On the laptop (131l, TML at High):
5,178 draws in 4,674 calls; 50.7 fps against 49.0; the world 9.15 ms against 9.91, 19.54 a frame against 20.24; worst
frames 28.6 ms against 29.1; the snapshots alike (one bit-identical, the other one pixel tile 1 level apart). The faint
1 to 2 level difference over the whole frame comes with when a snapshot is taken, instances on or off.

So far on TML at High: 34.3 fps (131i) to 50.7 (131l), the GPU's frame from 29.5 ms to 19.5.

Vulkan's pipeline cache (131e, Keisei): 8 pipelines in 197 ms on the first start, 2 ms on the next; the first long frame
497 ms, then 317 ms. About 177 ms of that frame is outside the timed parts in both runs (likely the JIT compiling the
first frame's code, slower on ARM64), and "Vulkan start" (52 ms) and "touch and overlays" (43 ms) don't change.

Plugins (131d): Ats32 runs through the proxy under x86 emulation on both trains, and the E217's six-DLL x64 chain runs in
the plugin layer (`OBNS_ATS64=1`) with its TIMS monitor live and no measurable frame cost against the proxy. Keisei's
KqAts64.dll is a Debug build and stopped on `ucrtbased`; the layer now maps the Debug runtime (131f).

## To do

1. **Direct3D 12 alone is the default on Windows on ARM64** (decided from TML without vsync, 131f: Direct3D 12 75.7 fps,
   Vulkan 51.9, Direct3D 11 44.6; `OBNS/GPU_SUPPORT.md`). To check on the laptop: a fresh settings folder starts on it.
2. **Direct3D 12's 120 fps on light routes**, twice the display's rate, while Direct3D 11 and Vulkan pass 140.
3. **ATS braking** on a red signal or overspeed, through the proxy and the layer.
4. **NVDA running with the game:** Tolk and NVDA's ARM64 client load (`dependencies/`), and find no screen reader on
   the laptop.

Done: sound heard on the laptop by the user (131h); Tolk built for ARM64 and NVDA's ARM64 client added (131i).

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
