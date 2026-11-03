# Nintendo Switch 1 homebrew (OBNS 1.31N): research ahead of the work

Written 2026-11-04, before any code. The goal set by the user: TML's exterior view at 30 fps on a Switch 1 (4 GB), as a homebrew `.nro`
in its own folder under `/switch` on the SD card, run from hbmenu on custom firmware (Atmosphère), drawn by deko3d. Figures marked
*(verify)* are from secondary sources or are estimates, and need checking on the console.

## The machine

| What | Switch 1 (Tegra X1, "Erista" and "Mariko" alike at stock clocks) |
| --- | --- |
| CPU | 4 Cortex-A57 at 1,020 MHz in games, docked or not; 3 for the game, 1 kept by the OS *(verify)*. A loading "boost mode" of 1,785 MHz exists for games *(verify how homebrew asks for it)* |
| GPU | Maxwell, 256 cores: 307.2 MHz handheld (about 157 GFLOPS), 768 MHz docked (about 393 GFLOPS); FP16 at twice the rate |
| Memory | 4 GB LPDDR4 shared by CPU and GPU: 25.6 GB/s docked, 21.3 GB/s handheld. A texture's CPU copy and GPU copy both count against the same 4 GB |
| Memory a homebrew gets | **title takeover** (hbmenu started by holding R while starting a game): 3,189 MiB for the whole process, code and GPU memory included. **Applet mode** (from the album): about 386 MiB, too little for anything here. Measured by one tester with `svcGetInfo` *(verify on current firmware)* |
| Screen | 1280x720 handheld (touch), up to 1920x1080 docked |
| Storage | the SD card (FAT32 or exFAT). Read speed is that of a UHS-I card through the Switch's reader *(measure)*; TML's load takes 400 to 530 s on the Zhaoxin PC and 86 to 95 s on the MacBook Neo |

For scale, TML in 1.3 on the MacBook Neo (Metal alone): 2.9 GB private memory, a 1.4 GB managed heap at load, 2.23 M vertices and 7.66 M
face vertices (about 375 MB), 7,900 to 8,000 draws a frame, and 12.5 ms of CPU a frame (train 3.4, render 8.5, world 0.7). An A57 at
1 GHz is several times slower a core than the A18 Pro, and Mono's AOT code is slower than its JIT's *(estimate)*: the same work would
take roughly 80 to 110 ms a frame, about 10 fps. **30 fps needs the CPU's work cut to about a third and spread over the 3 cores, and
the memory cut by about a quarter**, before the GPU is the limit.

## Software

### Running C# there

No .NET runtime is official for homebrew. Two community routes, both on Mono:

- **mono-nx** (exelix11): Mono on libnx, running .NET 9 assemblies in **Mono's interpreter**. JIT doesn't work: Horizon enforces W^X,
  and Mono's JIT confuses the separate RW and RX addresses libnx's JIT API gives. P/Invoke works only into libraries linked in
  statically (`dlopen`/`dlsym` mapped to a table). Threads need cooperative suspension (no `pthread_kill`). Files, tasks and sockets work;
  TLS doesn't. Full AOT was made to work but needed trimming (branches out of range otherwise); a hello world was 60 MB, half of it ICU.
  The author calls it an experiment.
- **NroForge** (BennettNotFound, MIT): builds a .NET 9 program into an NRO with **Mono LLVM full AOT**, libnx 4.12, SDL3 and Mesa's NVK
  (Vulkan through `libdrm_nouveau`), in a devkitA64 container. Needs title takeover. New and untested beyond its first app.

So: **Mono full AOT**, with the interpreter as the fallback for code not known at build time, as Xamarin does on iOS. The Android
port is already the right shape for it: .NET for Android is Mono, ARM64, AOT-compiled (`RunAOTCompilation`), with OpenTK, OpenAL and most
of System.Drawing replaced (`Android/OpenBve.GLES`, `OpenBve.OpenAL`, `OpenBve.Compat`, the `*.Android` projects). 1.31N starts from
those projects, not from the desktop build.

What follows from it:

- **Plugins.** Upstream loads its route, object, sound and texture plugins by reflection. Under full AOT they must be in the build (they
  are, as on Android). Trains' native Windows ATS plugins can't run (as on Android); .NET train plugins added by a player would need
  the interpreter, at its speed.
- **Native libraries** are linked into the NRO; every P/Invoke target must be known at build time.
- **System.Drawing** (libgdiplus) is not there: images through the Android port's decoders or a portlib (libpng, libjpeg-turbo), text
  through FreeType (and HarfBuzz) or the console's own shared fonts (`pl` service: Nintendo's Latin, Chinese, Korean and symbol fonts)
  *(verify which suits the HUD best)*.
- **Size.** ICU data alone is about 30 MB; invariant globalization saves it if the game's text allows *(check the Chinese and Japanese
  route files)*.

### deko3d (the renderer to build)

devkitPro's low-level API for the Switch's GPU, made from what is known of Nintendo's NVN; C and C++ (`dk::`), in the `switch-dev`
pacman group. NVN itself is only in Nintendo's licensed SDK, so homebrew can't use or ship it: **deko3d is the way to the GPU**.

| Area | deko3d | For OBNS |
| --- | --- | --- |
| Model | Vulkan-like: memory blocks, command buffers and lists, queues, fences, barriers, images, swapchain | The Vulkan alone path (`VulkanWorld`, `VulkanFrameDrawing`) is the pattern to follow |
| Memory blocks | CPU uncached (default) or cached, GPU cached; few big blocks, suballocated; the CPU cache can be flushed but not invalidated | GPU-to-CPU reads (snapshots) want a cached block flushed by the GPU side, or a readback copy; the Vulkan snapshot lesson (half a second from uncached memory) applies |
| Shaders | **GLSL compiled on the PC** by `uam` (Mesa 19's GLSL front end and nouveau's Maxwell back end) into `.dksh`; nothing compiles on the console | Fine: OBNS's shaders are fixed. Bindings must be explicit and there are no loose uniforms (push constants become a UBO). No transform feedback, no subroutines |
| Limits a stage | 16 UBOs (64 KB each), 16 SSBOs, **32 textures**, 8 images; 8 render targets; 32 vertex attributes, 16 vertex buffers | 32 textures a stage is the core Vulkan 1.0 tier's case: the **texture pages** (`OBNS_VULKAN_PAGED`) carry over. Bindless through handles in descriptor sets may be possible (`dkMakeTextureHandle`, image and sampler descriptor sets of any size), but isn't documented *(verify)* |
| Conventions | depth 0 to 1, upper-left origin, Y up by default; flags change them | Match the Vulkan path's conventions with the device flags |
| Formats | BC1 to BC7, ETC2, **ASTC 4x4 to 12x12**, sRGB variants | Compressed textures cut the memory most: decode once on the PC or on first load, keep a cache on the SD card |
| Present | `dkSwapchain` on the `NWindow`, a swap interval, any number of images | As the alone modes' present |
| Other | compute queues, indirect draws, tiled cache control, multisampling | GPU-driven culling and indirect draws to cut the CPU's 7,900 draws |
| Errors | a GPU fault puts the queue in an error state; the OS may end the process seconds later | Needs the validation the Vulkan path got here, done on PC first where possible |

A fallback exists: Mesa's nouveau on the Switch (`switch-mesa`) gives OpenGL 4.3 core and OpenGL ES 2 and 3 through EGL. The Android
port's GLES renderer could run on it first, as the baseline and as the fallback; it is slow and old, so not the 30 fps path.

### The rest

| Need | On the Switch |
| --- | --- |
| Window, input | libnx directly (`NWindow`, `pad`, touch), or SDL2 (`switch-sdl2`; no SDL3 package). Joy-Cons, Pro Controller, touch screen handheld: the Android port's touch overlay fits |
| Sound | OpenAL Soft (`switch-openal-soft`), libvorbis, FLAC, mpg123 are packaged |
| Fonts | FreeType, HarfBuzz packaged; the console's shared fonts |
| Images | libpng, libjpeg-turbo, giflib packaged |
| Files | `sdmc:/` paths; the SD card is case-insensitive, with long Unicode names (TML's and the Chinese route folders' names) *(verify exFAT and FAT32 both)* |

## Packaging (as the user set it)

```
sdmc:/switch/OBNS/OBNS.nro      the program: runtime, AOT code, native libraries, icon and title (NACP)
sdmc:/switch/OBNS/Data/         OpenBVE's data folder (languages, fonts if not the console's, shaders' .dksh)
sdmc:/switch/OBNS/UserData/     settings and logs (as /filesystem=)
sdmc:/switch/OBNS/LegacyContent/ routes and trains, or a path chosen in the options
```

hbloader maps the NRO into memory, so the NRO is kept to code, with the content beside it. Started from hbmenu with title takeover; the
program should say so and stop if it finds itself in applet mode's 386 MiB.

## The work, in order (a proposal)

1. **Runtime:** the Android port's projects built for libnx with Mono full AOT (NroForge's toolchain as the starting point), a console
   with nothing drawn: load TML, log the time and memory. This alone answers whether the load fits in 3.2 GB.
2. **Memory to fit:** compressed textures (ASTC or BC) with a cache on the SD card; no CPU copy kept after upload (shared memory counts
   twice); meshes shared and trimmed; the managed heap's census from `OBNS_MEMSTATS` down.
3. **deko3d whole-game renderer:** the Vulkan alone path's structure, with texture pages of 32, shaders compiled by `uam` at build
   time, the 2D part and the HUD in the same pass (no OpenGL anywhere).
4. **CPU to 33 ms:** fewer draws (static scenery merged by texture page, indirect draws, culling on the GPU), the work over 3 cores,
   the hot loops checked under AOT. Loading under the CPU boost mode if homebrew can use it.
5. **The console's ways:** input mapping, touch for the cab, docked and handheld resolution (720p rendering docked if needed), sleep and
   resume, the HOME button.

## Sources

- deko3d: [repository](https://github.com/devkitPro/deko3d), [primer](https://www.github.com/devkitPro/deko3d/blob/master/Primer.md),
  [header](https://raw.githubusercontent.com/devkitPro/deko3d/master/include/deko3d.h); uam: [repository](https://github.com/devkitPro/uam)
- C# on the Switch: [mono-nx](https://github.com/exelix11/mono-nx) and its [write-up](https://github.com/exelix11/mono-nx/blob/master/notes/writeup.md);
  [NroForge](https://github.com/BennettNotFound/NroForge)
- Memory by launch mode: [a tester's measurements](https://gist.github.com/uyjulian/df5e5c44a8304ba17536609f0358ac0a);
  [applet and title mode](https://tabrnicus.github.io/Switch-CFW-Guide/guide/homebrews)
- Hardware: [Digital Foundry's clocks, via GamesRadar](https://www.gamesradar.com/report-says-the-nintendo-switch-runs-slower-than-we-thought-but-its-not-all-bad-news),
  [Chips and Cheese on the GPU](https://chipsandcheese.com/p/nintendo-switchs-igpu-maxwell-nerfed-edition)
- Packages: [devkitPro's Switch packages](https://github.com/devkitPro/pacman-packages/tree/master/switch);
  [switch-mesa's OpenGL 4.3](https://devkitpro.org/viewtopic.php?p=16122)
