# Nintendo Switch 1 homebrew (OBNS 1.32): research ahead of the work

Written 2026-11-04, before any code. The goal set by the user: TML's exterior view at a **stable 30 fps** on a Switch 1 (4 GB), as a
floor: more is fine, below it preferably never, so every frame within 33.3 ms (the worst frames and the garbage collector's pauses
count, not the mean), as a homebrew `.nro`
in its own folder under `/switch` on the SD card, run from hbmenu on custom firmware (Atmosphère), drawn by deko3d. Figures marked
*(verify)* are from secondary sources or are estimates, and need checking on the console.

**Switch 2** (the user, 2026-11-04): not before it has stable custom firmware; then it's considered, with enhanced performance.

## The machine

| What | Switch 1 (Tegra X1, "Erista" and "Mariko" alike at stock clocks) |
| --- | --- |
| CPU | 4 Cortex-A57 at 1,020 MHz in games, docked or not; 3 for the game (cores 0 to 2, mask 0x7), 1 kept by the OS (measured). A loading "boost mode" of 1,785 MHz: homebrew asks for it with `appletSetCpuBoostMode(ApmCpuBoostMode_FastLoad)`, which also holds the GPU at 76 MHz (measured) |
| GPU | Maxwell, 256 cores: 307.2 MHz handheld (about 157 GFLOPS), 768 MHz docked (about 393 GFLOPS); FP16 at twice the rate |
| Memory | 4 GB LPDDR4 shared by CPU and GPU: 25.6 GB/s docked, 21.3 GB/s handheld. A texture's CPU copy and GPU copy both count against the same 4 GB |
| Memory a homebrew gets | **title takeover** (hbmenu started by holding R while starting a game): 3,189 MiB for the whole process, code and GPU memory included. **Applet mode** (from the album): about 386 MiB, too little for anything here. Measured with `svcGetInfo` on firmware 20.5.0 (the user's console, 2026-11-05): 3,189 MiB, 3,136 MiB of it allocatable in 64 MiB blocks |
| Screen | 1280x720 handheld (touch), up to 1920x1080 docked |
| Storage | the SD card (FAT32 or exFAT). Read speed is that of a UHS-I card through the Switch's reader *(measure)*; TML's load takes 400 to 530 s on the Zhaoxin PC and 86 to 95 s on the MacBook Neo |

### The test console, measured (2026-11-05)

`NX/probe` (a libnx program, sent by nxlink) on the user's Switch: the first model (Icosa, Erista), firmware 20.5.0,
Atmosphère 1.12.1, started from hbmenu 3.6.1 by title takeover.

| What | Measured |
| --- | --- |
| Memory | 3,189 MiB for the process; 3,136 MiB allocated in 64 MiB blocks |
| Cores | threads ran on cores 0, 1 and 2; not 3 |
| Clocks (handheld, on battery) | CPU 1,020 MHz, GPU 307 MHz, memory 1,331 MHz |
| 1,785 MHz through clkrst | accepted; a fixed CPU loop 0.392 s at 1,020 MHz, 0.224 s at 1,785 (the clocks' ratio, 1.75); put back to 1,020 |
| apm FastLoad | accepted: CPU 1,785 MHz, GPU 76 MHz, memory 1,331 MHz; back to 1,020 and 307 after |
| SD card | 256 MiB written at 18.6 MiB/s, read back at 72.5 MiB/s (perhaps partly from the cache) |

For loading, FastLoad is Nintendo's own clock and leaves the GPU idle, which loading doesn't need; in play, clkrst at 1,785 MHz.
nxlink's broadcast found no console here: give it the address hbmenu shows (`nxlink -a <address>`).

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
1 GHz is several times slower a core than the A18 Pro; Mono's AOT code through LLVM measured within about 10% of C on the
console (NX/README.md), three times Mono's own code generator on struct maths: the same work would
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
  (Vulkan through `libdrm_nouveau`), in a devkitA64 container. Needs title takeover. New and untested beyond its first app. Its
  runtime toolchain is what's of use; its Mesa and NVK are left out (the renderer is deko3d alone).

So: **Mono full AOT**, with the interpreter as the fallback for code not known at build time, as Xamarin does on iOS. The Android
port is already the right shape for it: .NET for Android is Mono, ARM64, AOT-compiled (`RunAOTCompilation`), with OpenTK, OpenAL and most
of System.Drawing replaced (`Android/OpenBve.GLES`, `OpenBve.OpenAL`, `OpenBve.Compat`, the `*.Android` projects). 1.32 starts from
those projects, not from the desktop build; their GLES and Vulkan renderers stay on Android.

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

**A pure deko3d renderer** (the user, 2026-11-04): deko3d draws everything (the world, the cab, the HUD, the menus, the loading
screen), with no OpenGL anywhere and no fallback to it. Mesa's nouveau on the Switch (`switch-mesa`, OpenGL 4.3 and ES 3 through
EGL) is not used and not linked, which also keeps its code and memory out of the 3.2 GB; upstream's OpenGL calls are stubbed as in
1.3's alone modes.

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
- **In the menus, by the labels:** **A confirms, B cancels or goes back**, as Nintendo's own software has it (A on the right, B at the
  bottom), whatever the driving scheme's positions; the D-pad and left stick move, L and R change pages or tabs. This goes for the
  main menu, the in-game pause menu, the station list and every dialog.
- **Keyboard and mouse stay supported** (the user, 2026-11-04): a USB keyboard and mouse in the dock's ports, or through a USB-C
  adapter handheld, read through libnx's HID (`obns_input_keyboard`, `obns_input_mouse`). **Built 2026-11-25, not yet on the
  console:** the keyboard drives with upstream's own bindings (`Game/KeyBindings.cs`: `UserData/1.5.0/controls.cfg` if there, a
  desktop's file works, else upstream's `Default.controls` built into the game; `Game/SwitchKeyboard.cs` maps the USB usage codes
  to upstream's key names); Escape (or Pause) pauses, Ctrl+T the route card; the menus take the arrows, Enter or Space, Escape or
  Backspace, Page Up and Page Down. The mouse (`Game/SwitchMouse.cs`, as Android's on the scene): a click on one of the cab's touch
  areas works it (`CabTouch`), a left drag elsewhere looks around, a right drag moves the camera, the wheel zooms; in the menus its
  left button is a touch; a pointer is drawn while it is in use. *(verify: the wheel's axis, the pointer's range)*
- **Upstream's train controllers stay supported** (the user, 2026-11-04), with the Switch's own as a priority:

  | Upstream plugin | Controllers | On the Switch |
  | --- | --- | --- |
  | `ZuikiInput` | **the Switch's Densha de GO! controllers**: HORI's One Handle controller for Nintendo Switch (`0f0d:00c1`), the ZUIKI Mascon (`33dd:0001` to `0005`), the ZUIKI Mascon Pro (`33dd:0006`, with HID output) | Licensed for the Switch, so the console's own controller service should take them as wired pads; how the lever reaches a homebrew there (an axis, as on the PC, or buttons) is the first thing to check on the console *(verify)*. Failing that, read raw over USB as below |
  | `DenshaDeGoInput` | PlayStation 1 and 2 controllers (Type II, Shinkansen, Ryojouhen, the TCPP and MTCP masters) through USB adapters or as USB devices (LibUsb) | Not licensed: raw USB through libnx's USB host service (`usbHs`), as the sys-con module does for other controllers *(verify)* |
  | `KatoInput` | Kato's EC-1 | the same |
  | `SanYingInput` | SanYing's controllers (USB joysticks) | the same |

  The plugins' C# runs as it is, given a back end for what they call on the PC (OpenTK's joystick, HidSharp, LibUsb) over libnx's HID
  and USB host services. Their settings dialogs are Windows Forms, so each needs a page in the console's menu instead.

  **Done first (2026-11-12, built, not yet tried on the console): the Switch's own, `NX/Game/MasconPad.cs`.** ZuikiInput's mapping
  lives in its Windows Forms settings, so the Switch has its own shell with upstream's logic: the lever's notches from upstream's
  thresholds on the left stick's Y (the console's controller service takes these controllers as wired pads), mapped as its default
  profile (B1 to B8, P1 to P5, the emergency position, air brakes in three steps); the Mascon Pro's reverser, horn pedal and ATS
  button. Which pad it is: the USB host service's list says whether one is plugged in (`obns_mascon_detect`), and the first
  full-key pad is then taken as it and left out of the game's own pad (`obns_mascon_attach`); its D-pad, A, B, + and − also work the
  pause menu. Options: "Densha de GO! controller on USB", on by default. To check on the console: the lever's direction and travel
  (upstream's thresholds were measured on the PC), and that the controller shows as a full-key pad. Not yet: the Mascon Pro's lamp
  (HID output), remapping its buttons, and the other plugins (PS1/PS2 controllers, Kato, SanYing: raw USB).
- **The in-game GUI is Android's** (the user, 2026-11-05): the HUD (`DriverHud.cs`), the pause menu, the station list, the
  timetable and the other overlays as the Android build draws them, rewritten for the Switch (drawn by deko3d), without the
  touch controls by default.
- **Touch controls** (`ControlOverlay.cs`'s buttons, and the cab's touch areas, `CabTouch.cs`): **off by default**; an options toggle
  turns them on in handheld mode. **Docked, they are off whatever the toggle says**: there is no touch screen in use.

## The work, in order (kept by the user, 2026-11-05)

1. **Runtime:** the Android port's projects built for libnx with Mono full AOT (NroForge's toolchain as the starting point), a console
   with nothing drawn: load TML, log the time and memory. This alone answers whether the load fits in 3.2 GB.
2. **Memory to fit: every measure below is on by default** (the user, 2026-11-04: "we need to squeeze its performance as much as
   possible"); the options can turn the texture size limit and compression off for a lighter route. The Android port's measures:
   - the **texture size limit** (`TextureSizeLimit.cs`): textures halved until neither side is over the limit; Android sets 1,024 px
     on phones of 4.5 GB or less (the Switch's case) and 2,048 px up to 6.5 GB, and the options can change it;
   - vertex copies released once uploaded (`VertexCopyReleaser.cs`), meshes shared (`MeshSharing.cs`), the transparency answers kept
     on disk (`TextureClassifier.cs`), and the census of what the GPU holds (`TextureCensus.cs`: on a phone, TML's graphics were 2.6 of
     the game's 3.4 GB, some textures uploaded once a wrap mode).

   And, beyond Android's, also on by default: **GPU-compressed textures**, which the Switch's GPU reads natively, 4 to 8 times smaller
   than RGBA8 and lighter on its 25.6 GB/s of bandwidth too. BC1 for opaque textures and BC3 for those with alpha, made on first load
   by a fast encoder (ASTC packs better but is slow to encode on an A57, so only from a PC tool, if at all) and kept on the SD card
   beside the transparency answers, so later loads read them as they are. **No CPU copy of a texture kept after upload** (shared memory
   counts twice). The managed heap trimmed from `OBNS_MEMSTATS`'s census down. The cost: textures at most 1,024 px and slightly
   blockier from BC; a first load of a route longer than later ones.
3. **The pure deko3d renderer:** the Vulkan alone path's structure, with texture pages of 32, shaders compiled by `uam` at build
   time, the 2D part, the HUD and the menus drawn by it too; no OpenGL, Mesa or fallback.
4. **CPU to 33 ms in the worst frame, not the mean:** textures and meshes made off the frame's thread, the garbage collector kept from
   long pauses (fewer allocations a frame), the ATS plugin layer's cost measured driving; fewer draws (static scenery merged by texture page, indirect draws, culling on the GPU), the work over 3 cores,
   the hot loops checked under AOT. 1,785 MHz on power; stock on battery unless the toggle is on.
5. **The console's ways:** the controls and HUD above, sleep and resume, the HOME button. Resolution (the user, 2026-11-04):
   **1280x720 in handheld mode** (all three models' screens: 6.2" LCD, 7.0" OLED, 5.5" Lite), **up to 1920x1080 docked**, switched as
   the console is docked or undocked; 720p rendering scaled up stays possible docked if 1080p can't hold the 30 fps floor.
6. **The menu, redesigned for the console.** The user, 2026-11-05: the Android build's more modern menu, rewritten for the
   Switch (drawn by deko3d, in the runner's code rather than Android's views), with the Options entries that don't apply to
   the Switch left out (for example renderer and graphics-API choices, Android storage and permission items, phone-only
   touch settings, desktop window modes). The layout: laid out at 1280x720 and scaled to 1080p docked; every part reachable with buttons alone
   (no touch docked), touch as well in handheld; text readable on the Lite's 5.5" screen (about 18 to 20 px at 720p at least), touch
   targets of 60 to 80 px, about 5% kept clear of the edges for TVs.

## Progress (2026-11-06)

**Step 1, measured on the console** (TML, TMLDN 20040 TT1-WKS2, handheld at the 1,785 MHz loading clock):

| Part | Time | Memory |
| --- | --- | --- |
| Start-up, file system, translations, 19 plugins | 1.5 s | native 85 MiB |
| Route (14,572 static objects) | 680 s, of it ~590 s loading objects | managed 594 to 670 MiB, native about 105 to 125 MiB |
| Scene preparation (without a GPU: every buffer's creation failed and was logged) | 120 to 145 s | 401 MiB managed after a collection |
| Train (DetailManager in the x86 layer, 1,560 textures classified) | placed; its fast-forward never finished with upstream's `dynamic` camera (fixed since) | |

- The object parsers are the route's time: 141 ms a file against 6 ms to read it (300 files sampled). Not the collector (its pauses
  sum to 0.0 s), not the SD card, nor exceptions any more (39,786 `DirectoryNotFoundException`s a load, now 19, made no difference).
  About 7.5 times the MacBook Neo's 86 to 95 s, as an A57 at 1,785 MHz on 3 cores under Mono AOT can be expected to be. The ways
  down are a parsed-object cache on the SD card and the parsers' hot spots under AOT (steps 2 and 4).
- Runtime fixes on the way: `dynamic` call sites need no Reflection.Emit (mono-nx's System.Linq.Expressions given thunks for 3 to
  8 parameters), the cab's camera read without `dynamic`, missing folders remembered while loading, sgen's nursery at 32 MB (mono-nx's
  aligned allocation fixed), the console kept from sleeping while it loads.

**Step 3, begun:** deko3d opens on the console and draws (the loading bar, 2026-11-06). Built since, not yet on the console: the 2D
layer (rounded rectangles, text from the console's shared fonts, images; `NX/runner/source/obns_dk2d.inc`), the loading screen as
Android's, and the renderer on deko3d (`NX/OpenBve.NX/Deko`, `obns_dk3d.inc`), the alone way below.

**The alone way** (the user, 2026-11-06: "go for alone mode for less rendering cost"). The world and the cab - nearly all of a
frame's draws - don't go through GL calls: Android's renderer works out each face's state as upstream's RenderFace does (its native
faces, `AndroidRenderer.DrawFace`, now behind `INativeFaceDevice`, which Android's VulkanGL also implements) and hands it to the
world layer (`Deko/DekoGL.World.cs`), which keeps it as a 192-byte record and has the native side record them in batches
(`obns_dkw_draws`) with its own program (`shaders/world.*`), the uniforms split by how often they change: the frame's (projection,
lights, fog) written when they change, the object's (its matrices) once for all of its faces, the draw's (material, brightness,
opacity, alpha test) pushed inline. No GL uniform call, no GL draw, no P/Invoke per face. DekoGL's GL side stays for what the
renderer makes once (meshes and textures as it loads, which the records then use) and for the few draws left on it: the
background, particles, upstream's overlays. Not yet: shadows (frames in flight since, step 4).

*Next, after the first run on the console* (the user, 2026-11-06): no GL at all, as the desktop's full alone mode - the meshes
and textures written straight into deko3d's memory by Switch versions of LibRender2's upload code (in `NX/`), the background
and particles as world records, upstream's overlays replaced by the Switch's own interface, GL's calls stubbed, DekoGL removed.
Until the world draws right on the console, DekoGL's GL side stays as the proven way meshes and textures reach deko3d.

**#11, the GL-free renderer, in stages** (the user, 2026-11-12: started again, after the 2026-11-07 list's other items).
What still passes through GL in a frame was about 0.2 ms in the last console logs (setup, background, overlays), and the
faces' look-ups of their mesh and texture by GL name are part of the 11 ms of face recording; so the gain is about a
millisecond a frame, plus a simpler path for shadows. Each stage leaves the Switch build working:

1. **Meshes:** NX's own `VertexArrayObject`, `VertexBufferObject` and `IndexBufferObject` (Switch.targets swaps upstream's
   in LibRender2.Android) write vertices and indices straight into deko3d's memory and keep the GPU addresses and the
   vertex layout on the object; a face takes them from its mesh, with no look-up by name.
2. **Textures:** the upload in TextureManager (and CompressedTextures' BC images) makes the deko3d image and its slot
   directly, the slot kept with the texture.
3. **Uniforms:** the default shader's values (the frame block, the face's material) set on a Switch shader object, not
   through GL uniform locations.
4. **The rest:** the background and particles as world records; whatever of upstream's overlays the Switch still draws,
   by its own 2D layer.
5. **No GL:** GL.Device a stub that logs any call once; DekoGL's GL side removed.

Done 2026-11-12, built (managed and NRO), not yet run on the console: stage 1 (NX/LibRender2, `Deko/DekoGL.Meshes.cs`; the
vertex and index copies let go once uploaded, so VertexCopyReleaser's 0.6 ms a frame is off; `UserData/nx-gl-meshes.txt` keeps
the old way), and stage 2's part that counts per frame: textures found by name through an array, not a dictionary. The
upload itself still goes through DekoGL's GL calls (once per texture, at load or when streamed).

**Step 4, begun** (the user, 2026-11-06: "the frame rates are still very low"; the first runs gave 2 to 5 fps on TML at
10,900 draws a frame, the GPU held at 76 MHz by the loading clock - 128 ms a frame waiting for it - and about 160 ms of CPU in
RenderScene). Done since, not yet on the console:

- The performance mode in Options (Normal, Turbo - the default - and Overclock), the fan at full in the last two; play no longer
  runs at FastLoad's GPU clock.
- The trains handed to the static batcher, as Android's GameView does: on the Switch it never had them, so every face of the
  cars' still parts was a draw of its own.
- Two frames in flight: the native end of a frame waits only for the frame before it; command memory and the uniform ring in two
  halves; what a frame used freed a frame later; the 2D layer waits before it rewrites its quads; `UserData/nx-serial-frames.txt`
  turns it off for comparisons.
- Less CPU per face: the alpha sort without LINQ, the batch filter's lookups once per object by reference, textures on the GPU
  without the host's layers, colour-key checks by texture, the frame's face lists kept from frame to frame.
- The log gives DekoGL's recording and texture-making time apart from the rest, and the batcher's summary every 30 s.
- Loading (steps 2 and 4): parsed objects kept on the SD card (`ObjectDiskCache`, `UserData/ObjectCache`, one pack and index
  per route) and read back on the next load of the route instead of parsed - the parsers were ~590 s of TML's 680 s. Keyed by
  file, size, date, encoding, optimisation settings and the parsers' assemblies; `UserData/nx-no-object-cache.txt` keeps it off.
  The first load is as before; the gain is from the second on.

**Step 5, sleep and resume** (the user, 2026-11-12): nothing took the system's applet messages before. `runner/source/obns_applet.c`
takes them (appletMainLoop, with a hook), once a frame in the menu, the loading screen and the game (`OpenBve.NX/Applet.cs`). The
game pauses (its pause menu) when it loses the focus - the HOME menu, an applet over it - or wakes from sleep, and leaves the time
away out of the simulation and the frame statistics (the system suspends an application out of focus, so the first frame after
would have run minutes at once); the watchdog doesn't count it as a stuck frame. Docking is logged; the resolution still follows
the mode at start. Built, not yet tried on the console.

**Step 6, begun:** the menu in `NX/Game/Menu` (Android's, for buttons: rail, Start, Options, pickers, hints), drawn through
`IMenuCanvas`; `NX/MenuPreview` draws it into PNG files on Windows. A first pass: once the 2D and 3D rendering are done, its GUI and
UX are to match Android's, with only the changes noted above (the user, 2026-11-06).

**Step 6, Android's remaining pages and cards** (the user, 2026-11-12; built and drawn by `NX/MenuPreview`, not yet on the console;
compared with Android's code, as there are no Android screenshots to compare with):
- the switch map (`Hud/SwitchMapCard.cs`, Android's SwitchMapPanel): from the pause menu's "Select a Switch To Change" (routes
  with switches) or SWITCH_MENU; up and down choose, A changes, L and R zoom, Y follows the train, B closes; the game goes on;
- the route card's Map and Gradient tabs (`Hud/TimetableCard.Route.cs`, Android's TimetablePanel and RouteViews): − now goes
  Timetable, Map, Gradient, hidden; following the train (no panning without touch while driving);
- Controls (`Menu/ControlsPage.Switch.cs`, `PadBindings.cs`; Android's ControlsPage, its controller tab): every command and its
  buttons, A then a button to add, X to clear, Reset; kept in `UserData/pad-nx.cfg`; + and − stay pause and timetable; also from
  the pause menu's "Customize controls";
- Package Management (`Menu/PackagesPage.Switch.cs` on Android's PackageInstaller, now in the Switch build): archives put in
  `/switch/OBNS/Packages` (no file picker on the console), their details and upstream's checks, the install with its progress;
  the installed packages to uninstall;
- the 2D layer draws lines (`obns_dk2d_line`), for the maps.
Done since (2026-11-25, built, not yet on the console): Controls' keyboard list (a row switches between the controller's buttons
and a USB keyboard's keys; A then the key with Shift, Ctrl or Alt, X clears, Reset; `KeyBindings.cs`, kept in
`UserData/1.5.0/controls.cfg` as upstream's), the route card's panning and zooming (the right stick on the Map and Gradient tabs,
a USB mouse dragged and wheeled over them; following the train again when the tab changes), and Android's encoding choice for plain
archives on Package Management.

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
