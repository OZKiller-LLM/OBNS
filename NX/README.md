# Nintendo Switch (OBNS 1.32)

This folder is the Switch build, as `Android/` is the Android port's and `ARM64/` the Windows on ARM64 build's: what only
the Switch needs. The plan, the research and the user's decisions are in `OBNS/SWITCH.md`; this file is how it's built
and what has been measured.

The target: a homebrew `.nro` in `sdmc:/switch/OBNS/`, started from hbmenu by title takeover on custom firmware
(Atmosphère), C# compiled ahead of time by Mono, everything drawn by deko3d, TML's exterior view at 30 fps or more
in every frame.

## The folder

| Path | What it is |
| --- | --- |
| `probe/` | A small libnx program reporting the console: memory by launch mode, model and versions, clocks (clkrst and apm FastLoad), cores, the SD card's speed. Results in `OBNS/SWITCH.md` |
| `hello/` | The first C# on the console: mono-nx's AOT example in Release (`build.sh` on Linux). Times C# against the probe's C, the garbage collector and three threads |
| `build-mono.sh` | Builds Mono for libnx on Linux, with the patches: the runtime and libraries in Release, the AOT offsets, and the PC's AOT cross-compiler with LLVM |
| `patches/` | Changes to mono-nx's runtime fork (`dotnet_runtime`, branch `libnx`, at 289cdaa): `Console` in sockets logging made Debug-only (Release doesn't reference System.Console), NroForge's libnx TLS clean-up (MIT), without which a managed thread ending trips an assertion in `unregister_thread` and the console crashes, NroForge's `-fexceptions` for `llvm-runtime.cpp` (unused on ARM64 so far), the code page encodings' `WindowsCodePage`, the fake mmap's aligned allocations (sgen's 32 MB nursery), and System.Linq.Expressions' thunks for 3 to 8 parameters (`dynamic` without Reflection.Emit) |
| `Switch.targets` | How the Android port's projects build for the Switch: Compat's Switch backend, the game project as a library without Android's views, `Game/` compiled in |
| `Game/` | The game's Switch-only parts: the menu (`Menu/`, drawn through `IMenuCanvas`), its settings (`SwitchSettings.cs`, `UserData/options-nx.cfg`), the Menu, LoadProgress and Speech stand-ins |
| `OpenBve.NX/` | The entry program: the menu, the load (logged with its time and memory), the loading screen, and the frames; `Deko/` is DekoGL, the renderer's GL calls on deko3d |
| `runner/` | The native host linked into the NRO: Mono's start-up, deko3d (`obns_dk.c`: the device and swapchain, `obns_dk2d.inc` 2D drawing, `obns_dk3d.inc` the renderer's 3D), input, images and fonts, `shaders/` (GLSL for `uam`) |
| `build-nro.sh` | The NRO on Linux: the C# built, trimmed and compiled ahead of time with LLVM, the shaders compiled by `uam` (the 2D ones and Android's native Vulkan ones made deko3d's), linked with `runner/` |
| `MenuPreview/` | The menu drawn into PNG files on Windows (`preview.ps1`), for its layout without the console |

## Building

### On Windows: native programs

devkitPro's Windows installer (`C:\devkitPro`, with libnx, deko3d and uam) builds the native parts. From devkitPro's
MSYS2 shell, in a program's folder:

```
make
```

The Switch portlibs (OpenAL Soft, libvorbis, FLAC, mpg123, FreeType, HarfBuzz, libpng, libjpeg-turbo, zlib, bzip2)
come from devkitPro's pacman (`pacman -S switch-openal-soft ...`). OpenAL Soft brings SDL2 and Mesa as dependencies;
they're not linked, as the renderer is deko3d alone.

### On Linux (WSL's Ubuntu): Mono for the Switch

The .NET runtime for libnx (mono-nx's fork of dotnet/runtime, branch `libnx`) builds only on Linux.

- **devkitPro for Linux:** devkitPro's servers answer only their own pacman (`wget` and `curl` get 403), so the Linux
  packages are downloaded by the Windows pacman with a configuration of their own (`D:\dev\tools\dkp-linux`: the
  `dkp-libs` and `dkp-linux` repositories, `Architecture = x86_64`, its own root and cache, the Windows keyring; packages
  checked against devkitPro's signatures) and unpacked in Ubuntu into `~/devkitpro-root`, with
  `DEVKITPRO=~/devkitpro-root/opt/devkitpro`. No root needed. devkitA64 r30 (GCC 16.1), newer than the GCC 15.2
  mono-nx and NroForge were built with.
- **Ubuntu's own packages:** build-essential, cmake, clang, libclang-dev, libicu-dev, libkrb5-dev, liblttng-ust-dev,
  gettext, llvm, lldb, liblldb-dev, ninja-build, python3.
- **Sources** (in `~/switch-build`): mono-nx at 285b277 and its runtime fork at 289cdaa, the revisions NroForge's
  toolchain was made from. Clone them in Ubuntu: a copy from a Windows clone has Windows line endings, and its
  scripts don't run.
- **Order:** ICU 77.1 for libnx (mono-nx's `icu/build_icu.sh`), then Mono (mono-nx's `build_mono.sh`, in Release rather
  than its Debug): the runtime and libraries for libnx, the AOT offsets, then the PC's AOT cross-compiler.

## Sending to the console

hbmenu 3.6.1's netloader (Y in hbmenu): nxlink's broadcast doesn't find the console here, so give it the address hbmenu
shows:

```
nxlink -a <address> -s program.nro
```

`-s` keeps nxlink listening, so the program's output comes back to the PC. If the netloader refuses the connection
although it shows "waiting for connection", leave it (B) and start it again (Y) once the network is up.

## The test console

The user's Switch: the first model (Erista), firmware 20.5.0, Atmosphère 1.12.1, hbmenu 3.6.1, docked with Ethernet.

## Measured

`hello` on the test console (2026-11-05), Mono's standard full AOT (not LLVM), Release runtime with the TLS fix:

| What | Result |
| --- | --- |
| Runtime | .NET 9.0.3 on libnx, Arm64, 3 processors; invariant culture; formatting right |
| Heaps | mono-nx splits the memory in two: 1,582 MB for libnx (C, GPU) and 1,580 MB for Mono's. OBNS will want its own split |
| The probe's CPU loop in C# | 0.767 s at 1,020 MHz (C 0.392), 0.410 s at 1,785 (C 0.224): about 1.9 times C's time |
| A vector loop over structs | 0.281 s at 1,020 MHz, 0.161 s at 1,785 |
| Garbage collector | 0.75 ms of allocation a frame for 360 frames, the worst 1.20 ms, with 104 MiB held (plain arrays, nothing to trace); 35 minor and 22 major collections |
| Threads | the loop over 1, 2 and 3 threads: 0.544, 0.273, 0.187 s (2.9 times on three) |

The first run, before the TLS fix, crashed the console when its first worker thread ended.

With LLVM (Mono's AOT cross-compiler with Microsoft's Mono LLVM 19, `opt` at O2, `llc` with position-independent code), the
same hello on the same console:

| What | Mono's code generator | LLVM | C |
| --- | --- | --- | --- |
| CPU loop, 1,020 MHz | 0.767 s | 0.431 s | 0.392 s |
| CPU loop, 1,785 MHz | 0.410 s | 0.247 s | 0.224 s |
| Vector loop, 1,020 / 1,785 MHz | 0.281 / 0.161 s | 0.097 / 0.059 s | |
| Allocation a frame, worst frame | 0.75, 1.20 ms | 0.53, 0.85 ms | |
| Loop over 1, 2, 3 threads (1,785 MHz) | 0.544, 0.273, 0.187 s | 0.247, 0.124, 0.083 s | |

C# under LLVM is within about 10% of C on the loop and three times faster than Mono's own code on structs; three threads
give three times the speed. OBNS is built with LLVM.

