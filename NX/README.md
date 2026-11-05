# Nintendo Switch (OBNS 1.31N)

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
