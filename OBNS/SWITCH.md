# Nintendo Switch 1 homebrew (OBNS 1.31N): research ahead of the work

Written 2026-11-04, before any code. The goal set by the user: TML's exterior view at a **stable 30 fps** on a Switch 1 (4 GB), as a
floor: more is fine, below it preferably never, so every frame within 33.3 ms (the worst frames and the garbage collector's pauses
count, not the mean), as a homebrew `.nro`
in its own folder under `/switch` on the SD card, run from hbmenu on custom firmware (Atmosphère), drawn by deko3d. Figures marked
*(verify)* are from secondary sources or are estimates, and need checking on the console.

**Switch 2** (the user, 2026-11-04): not before it has stable custom firmware; then it's considered, with enhanced performance.

## The machine

| What | Switch 1 (Tegra X1, "Erista" and "Mariko" alike at stock clocks) |
| --- | --- |
| CPU | 4 Cortex-A57 at 1,020 MHz in games, docked or not; 3 for the game, 1 kept by the OS *(verify)*. A loading "boost mode" of 1,785 MHz exists for games *(verify how homebrew asks for it)* |
| GPU | Maxwell, 256 cores: 307.2 MHz handheld (about 157 GFLOPS), 768 MHz docked (about 393 GFLOPS); FP16 at twice the rate |
| Memory | 4 GB LPDDR4 shared by CPU and GPU: 25.6 GB/s docked, 21.3 GB/s handheld. A texture's CPU copy and GPU copy both count against the same 4 GB |
| Memory a homebrew gets | **title takeover** (hbmenu started by holding R while starting a game): 3,189 MiB for the whole process, code and GPU memory included. **Applet mode** (from the album): about 386 MiB, too little for anything here. Measured by one tester with `svcGetInfo` *(verify on current firmware)* |
| Screen | 1280x720 handheld (touch), up to 1920x1080 docked |
| Storage | the SD card (FAT32 or exFAT). Read speed is that of a UHS-I card through the Switch's reader *(measure)*; TML's load takes 400 to 530 s on the Zhaoxin PC and 86 to 95 s on the MacBook Neo |

### Clocks a homebrew can set

The CPU can be raised from inside the program, as RetroArch has done since 2018: its own overclock and underclock settings, restored
after docking or undocking and set back to the system's on losing focus. libnx has the clock service for it (`clkrst`, firmware 8.0.0
and later; `pcv` before), which sys-clk also uses as a background module. Community figures *(verify on the console)*:

| CPU / GPU | Erista (first model) | Mariko (later model, Lite, OLED) |
| --- | --- | --- |
| Stock in games | 1,020 MHz / 307 or 768 MHz | the same |
| 1,785 MHz CPU | the stock maximum (Nintendo's own loading "boost" clock); the board's 18 W limit is reached here without undervolting | "moderate", for most games |
| Higher | not without undervolting; the GPU's limit is 921 MHz; don't raise both at once on a first model | about 1,963 / 998 MHz called safe on good chips, 1,963 / 921 more generally; up to 2,397 / 1,305 unlockable, called unsafe |
| On battery | over about 8 W for long periods is said to wear the battery | the same |

What OBNS does with it (the user, 2026-11-04):

- **On power** (docked, or handheld with the charger in): **1,785 MHz set automatically from launch, while loading and in play**, with
  no option needed, on every model (the stock maximum,
  and the clock the console's own games load at). The 30 fps floor is measured here.
- **On battery:** stock 1,020 MHz, loading too, unless the options' toggle ("1,785 MHz on battery", off by default) says otherwise.
  The 30 fps floor isn't promised at stock clocks.
- Followed as it changes, at once, while loading or in play: docking or undocking, and the charger plugged in or pulled out, each
  switches between the two (to 1,785 MHz on power; to stock on battery). **The toggle overrides it:** on, the clock stays at 1,785 MHz
  whatever the charger and dock do; off, the switching above decides. libnx's `appletGetOperationMode`
  and `psmGetChargerType`, with the applet's notifications *(verify)*. Set back to stock on leaving or losing focus, as RetroArch does.
- Nothing above the stock maximum is offered: that is sys-clk's business, at the player's own risk.

For scale, TML in 1.3 on the MacBook Neo (Metal alone): 2.9 GB private memory, a 1.4 GB managed heap at load, 2.23 M vertices and 7.66 M
face vertices (about 375 MB), 7,900 to 8,000 draws a frame, and 12.5 ms of CPU a frame (train 3.4, render 8.5, world 0.7). An A57 at
1 GHz is several times slower a core than the A18 Pro, and Mono's AOT code is slower than its JIT's *(estimate)*: the same work would
take roughly 80 to 110 ms a frame at 1,020 MHz, about 10 fps, and 46 to 63 ms at 1,785 MHz. **30 fps needs the CPU's work cut to
about a third at stock clocks, or by about half at 1,785 MHz, and spread over the 3 cores; and the memory cut by about a quarter**,
before the GPU is the limit. **The 30 fps floor is to hold at 1,785 MHz** (the user, 2026-11-04; on power, and on battery with the
toggle on): the CPU's work cut by about half.

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
  are, as on Android). .NET train plugins added by a player would need Mono's interpreter, at its speed.
- **Trains' Windows ATS plugins** run as on Android, Linux and macOS: in the x86 and x64 compatibility layer (`shared/OpenBve.X86`, an
  interpreter with a PE loader and the Windows and C runtime calls plugins make) through the plugin bridge (`shared/NativePlugins`),
  with the managed stand-ins when a plugin can't start. It is plain C#: no code made at run time, no P/Invoke, no reflection emit, and
  its 4 GB address space is pages made as they're touched, so full AOT takes it as it is. Measured here (2026-11-04, .NET 10 on the
  PC, the layer's own test, standing at a station): TML's SP1900 DetailManager with kcrwr_seltrac, DGo and DGoN loaded, 1,745
  instructions and 0.19 to 0.33 ms a frame; the KCR's TBL set, 0.17 ms. Most of that is the calls' overhead, not the instructions.
  At several times slower on an A57 under AOT *(estimate)* that is about 2 to 4 ms of the 33 ms; to be measured driving, on the console.
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

## Controls and HUD (the user, 2026-11-04: as the Android branch)

- **The controller scheme is Android's** (`Android/OpenBve.Android/ControlBindings.cs`, its built-in layout and `pad.cfg` for
  changes), kept by the buttons' **positions**, as Nintendo's labels are the other way round from the standard pad's Android names:

  | Android (standard pad) | Switch | Command |
  | --- | --- | --- |
  | RT / RB | ZR / R | power up (and one-handle power) / power down (and neutral) |
  | LT / LB | ZL / L | brake up (and one-handle brake) / brake down (and neutral) |
  | A (bottom) | B (bottom) | ATS confirm (security S) |
  | B (right) | A (right) | horn |
  | X (left) | Y (left) | left doors |
  | Y (top) | X (top) | right doors |
  | Start / Select | + / − | pause / timetable |
  | D-pad up / down | D-pad up / down | reverser forward / backward |
  | D-pad left / right | D-pad left / right | previous / next camera point |

  Joy-Cons held together, the Pro Controller and the handheld's own Joy-Cons are one pad; a single Joy-Con held sideways has too few
  buttons for the whole scheme *(decide later)*. Remapping as on Android, kept in the settings folder.
- **The HUD is Android's** (`DriverHud.cs`), without the touch controls by default.
- **Touch controls** (`ControlOverlay.cs`'s buttons, and the cab's touch areas, `CabTouch.cs`): **off by default**; an options toggle
  turns them on in handheld mode. **Docked, they are off whatever the toggle says**: there is no touch screen in use.

## The work, in order (a proposal)

1. **Runtime:** the Android port's projects built for libnx with Mono full AOT (NroForge's toolchain as the starting point), a console
   with nothing drawn: load TML, log the time and memory. This alone answers whether the load fits in 3.2 GB.
2. **Memory to fit:** compressed textures (ASTC or BC) with a cache on the SD card; no CPU copy kept after upload (shared memory counts
   twice); meshes shared and trimmed; the managed heap's census from `OBNS_MEMSTATS` down.
3. **deko3d whole-game renderer:** the Vulkan alone path's structure, with texture pages of 32, shaders compiled by `uam` at build
   time, the 2D part and the HUD in the same pass (no OpenGL anywhere).
4. **CPU to 33 ms in the worst frame, not the mean:** textures and meshes made off the frame's thread, the garbage collector kept from
   long pauses (fewer allocations a frame), the ATS plugin layer's cost measured driving; fewer draws (static scenery merged by texture page, indirect draws, culling on the GPU), the work over 3 cores,
   the hot loops checked under AOT. 1,785 MHz on power; stock on battery unless the toggle is on.
5. **The console's ways:** the controls and HUD above, sleep and resume, the HOME button. Resolution (the user, 2026-11-04):
   **1280x720 in handheld mode** (all three models' screens: 6.2" LCD, 7.0" OLED, 5.5" Lite), **up to 1920x1080 docked**, switched as
   the console is docked or undocked; 720p rendering scaled up stays possible docked if 1080p can't hold the 30 fps floor.
6. **The menu, redesigned for the console** (the user's instructions to come): laid out at 1280x720 and scaled to 1080p docked; every part reachable with buttons alone
   (no touch docked), touch as well in handheld; text readable on the Lite's 5.5" screen (about 18 to 20 px at 720p at least), touch
   targets of 60 to 80 px, about 5% kept clear of the edges for TVs.

## Sources

- deko3d: [repository](https://github.com/devkitPro/deko3d), [primer](https://www.github.com/devkitPro/deko3d/blob/master/Primer.md),
  [header](https://raw.githubusercontent.com/devkitPro/deko3d/master/include/deko3d.h); uam: [repository](https://github.com/devkitPro/uam)
- C# on the Switch: [mono-nx](https://github.com/exelix11/mono-nx) and its [write-up](https://github.com/exelix11/mono-nx/blob/master/notes/writeup.md);
  [NroForge](https://github.com/BennettNotFound/NroForge)
- Memory by launch mode: [a tester's measurements](https://gist.github.com/uyjulian/df5e5c44a8304ba17536609f0358ac0a);
  [applet and title mode](https://tabrnicus.github.io/Switch-CFW-Guide/guide/homebrews)
- Hardware: [Digital Foundry's clocks, via GamesRadar](https://www.gamesradar.com/report-says-the-nintendo-switch-runs-slower-than-we-thought-but-its-not-all-bad-news),
  [Chips and Cheese on the GPU](https://chipsandcheese.com/p/nintendo-switchs-igpu-maxwell-nerfed-edition)
- Clocks: [RetroArch's own overclocking (November 2018)](https://www.libretro.com/index.php/libretroretroarch-progress-reportupdate-november-2018/),
  [libnx's clkrst service](https://github.com/switchbrew/libnx/pull/266/files), [sys-clk](https://github.com/retronx-team/sys-clk/),
  [Horizon OC on Erista](https://horizon-oc.github.io/erista.html), [Switch OC Suite](https://gamebrew.org/wiki/Switch_OC_Suite)
- Packages: [devkitPro's Switch packages](https://github.com/devkitPro/pacman-packages/tree/master/switch);
  [switch-mesa's OpenGL 4.3](https://devkitpro.org/viewtopic.php?p=16122)
