# OBNS

Features of the OpenBVE Android port brought to upstream's own platforms (Windows first; macOS and
Linux later), and the libraries the Android port and the desktop build share.

## Layout of the repository (from 1.2)

- `OpenBVE/` - upstream OpenBVE exactly as published (now 1.14.1.2). Never edited: a change to an
  upstream file is an OBNS or Android copy of it, compiled in its place.
- `Android/` - the Android release: what only Android needs (windowing, touch interface, menus,
  Android packaging).
- `OBNS/` - platform-neutral features, used by both, and the desktop build of upstream's program with
  them in it.

## Plan for 1.2

1. Android onto upstream 1.14.1.2, with every upstream feature (including the in-game commands
   Android never had: headlights, pantographs, breaker, sanders, gears, uncoupling, time
   acceleration, mute, head-out views, switch menu, axes, debug views), tested on phones.
2. The OBNS desktop build: upstream's program for Windows x64, built from `OpenBVE/` as it is.
3. BVE5 trains (vehicle, parameters, performance tables, panel, sound, motor noise) and BVE5 safety
   plugins, on Android and Windows.
4. The Android port's features on the desktop: per-frame updates on all cores, fewer draw calls,
   Vulkan with pipelined frames, then the Win32 plugin layer (x86) for macOS and Linux.

## The desktop build (Windows x64)

`OBNS/build-desktop.ps1` builds upstream's program without touching `OpenBVE/`:

1. `OpenBVE/` is mirrored into `OBNS/build/upstream/` (ignored by git; packages, `obj` and `bin`
   folders are kept between runs, so later builds are incremental);
2. `OBNS/overrides/` is laid over the mirror - each file there replaces upstream's at the same path,
   and starts with "OBNS: a copy of upstream's ...";
3. upstream's NuGet packages are restored (from nuget.org on the first build) and `OpenBVE.sln` is
   built: with Visual Studio's MSBuild if it has the C# compiler (the .NET desktop workload), else
   with the .NET SDK's (`dotnet msbuild`). Upstream targets .NET Framework 4.8, whose reference
   assemblies come from Windows' targeting pack or the restored packages.

```
powershell -ExecutionPolicy Bypass -File OBNS\build-desktop.ps1 [-Configuration Debug] [-Clean] [-Package]
```

The program is `OBNS/build/upstream/bin_release/OpenBve.exe`: AnyCPU without "prefer 32-bit", as
upstream, so a 64-bit process on Windows x64 (32-bit train plugins run in upstream's
Win32PluginProxy). Upstream's tools are built beside it (Object Viewer, Route Viewer, the train
editors, ObjectBender, CarXmlConvertor). It is stamped 1.14.1.2-OBNS (OBNS's `AssemblyInfo.tt`), and
`-Package` zips it, without debug symbols, as `OBNS/build/dist/OpenBVE-1.14.1.2-OBNS-win-x64.zip`.

Run it from its own folder, as a double-click does: upstream starts Win32PluginProxy.exe by name.
For a test that leaves the installed OpenBVE's settings alone, upstream's `/filesystem=<file>` takes a
filesystem.cfg naming another settings folder (`version = 2`, `settings = <folder>`).

Tried (2026-10-21): East Rail with the R-Train loads and runs as a 64-bit process, its 32-bit
DetailManager.dll plugin through the proxy.

### OBNS's replacements for upstream files

| File | Change | Also used by |
|---|---|---|
| `source/OpenBVE/Properties/AssemblyInfo.tt` | the release stamped as 1.14.1.2-OBNS (upstream takes it from the git tag being built, which the mirror does not have) | - |
| `source/RouteManager2/Events/Transponder.cs` | a transponder's section reference past the last section counts as none (1.14.1.x keeps orphaned references, and the Tuen Ma Line threw at its first station) | Android (`RouteManager2.Android.csproj`) |
| `source/CarXMLConvertor/CarXMLConvertor.csproj`, `packages.config` | Windows Forms resources built preserialized with System.Resources.Extensions (as upstream's TrainEditor), so the .NET SDK's MSBuild builds it | - |
| `source/ObjectBender/ObjectBender.csproj`, `packages.config` | the same | - |

Shared libraries (moving here from `Android/` as each is taken up): the x86 plugin layer
(`shared/OpenBve.X86`, with its harness `shared/OpenBve.X86.Tests`) and plugin bridge
(`shared/NativePlugins`), the BVE5 train plugin, the parallel-update helpers, the static batcher.

## Backports to the desktop

Measured with `OBNS_FRAMESTATS=1`, which logs where a frame's time goes every 10 s (OBNS's copy of
`GameWindow.cs`): the animated world objects, the trains' objects, the scene's rendering, the buffer
swap (the wait for the GPU and vertical sync) and the rest. The benchmark is Android's heavy one:
the Tuen Ma Line (`TMLDN 20144.csv`) with the SP1900 (private pack, used from its local copy, never
committed), 30 s in the cab, then 30 s in the exterior view (F2), on a 12-thread PC at 60 Hz.

### 1. Animated objects on all cores (done)

`OBNS/shared/FrameWorkers.cs` and `ParallelUpdates.cs`, moved here from the Android port and compiled
by both (on the desktop into TrainManager). The desktop hooks are OBNS copies of upstream's
`TrainManager.cs` (every train's cars), `ObjectManager.cs` (the route's plain animated and keyframe
objects) and `Host.cs` (function scripts on per-thread stacks, shows and hides queued, a thread-safe
Random). `OBNS_PARALLEL_UPDATES=0` turns them off.

| Tuen Ma Line, steady state | Off | On |
|---|---|---|
| Cab: world objects | 2.68 ms | 1.10 ms |
| Exterior: world objects / train objects | 2.80 / 4.28 ms | 1.15 / 2.86 ms |
| Exterior: frame | 32.2 ms (31.0 fps) | 28.9 ms (34.6 fps) |
| Cab: frame (near the 60 Hz limit) | 17.5 ms | 17.3 ms |

### 2. Fewer draw calls (done)

The scene's rendering is now most of the frame (exterior: ~23 of 29 ms, ~10,000 draw calls), with the
swap under 0.5 ms: the time is the CPU submitting draw calls, not the GPU.

- **Runs of an object's faces as one call** (Android's `DrawRuns`): tried and dropped. On the desktop,
  upstream's in-game object optimisation already joins each object's faces per material when it
  loads (every face is a triangle list, and no two neighbouring faces of an object share a material:
  0 joinable of 3,212), so each call is already one object's material.
- **Static scenery and trains' still parts batched** (done): `OBNS/shared/StaticBatcher.cs`, moved here
  from the Android port and compiled by both, each renderer giving it what differs through
  `IStaticBatchHost` (Android's `AndroidRenderer`; on the desktop an OBNS copy of upstream's
  `NewRenderer.cs`, with OBNS's `OpenBve.csproj` compiling the shared file). The objects of a stretch
  of track, or a car's parts that never move relative to it, are merged per material into one mesh
  drawn with one call; the batched train parts also leave the per-frame update. On the Tuen Ma Line:
  28,032 of 29,448 scenery objects in 2,825 groups, 3,951 still train parts in 283 groups.
  `OBNS_STATIC_BATCHES=0` and `OBNS_DETACH_PARTS=0` turn them off.

| Tuen Ma Line, steady state, parallel updates on | Batching off | On |
|---|---|---|
| Cab: render / frame | 12.7 / 17.6 ms (57 fps) | 9.95 / 16.7 ms (59.8 fps: the 60 Hz limit) |
| Cab: train objects | 0.68 ms | 0.33 ms |
| Exterior: render / frame | 24.2 / 30.6 ms (32.7 fps) | 21.1 / 27.0 ms (37.1 fps) |

Less than on phones, where a draw call costs far more; the exterior's remaining rendering is next
for Vulkan (3), which makes each call cheaper.

### 3. Vulkan for the world and the cab, with pipelined frames (done)

Not Android's renderer: on phones `OpenBve.Vulkan` (`VulkanGL`) carries out GL ES calls with Vulkan,
which does not suit desktop drivers. The desktop's is a hybrid, designed for them (`OBNS/VULKAN.md`):
Vulkan draws the world layer and the cab layer - upstream's scenery, trains, cab and panel, as draw
records made on every core and drawn with bindless textures and indirect draws - into images OpenGL
shares, and OpenGL draws the background, particles, the overlays and the menus as upstream does,
laying Vulkan's world image over the background and its cab image over the particles. Each frame is made and submitted on a thread of its own while OpenGL draws the
background. The vertex buffers are shared between the two; textures are Vulkan's own, with upstream's
preloads sent to it, so there is one copy of each on the GPU.

The renderer is chosen in the options (display: Renderer, OpenGL or Vulkan; Vulkan by default) and
the debug overlay names it. OpenGL draws the world when the GPU or driver cannot share images with
Vulkan, while the wireframe and normals views are on, and for the rest of the session if a
Vulkan frame fails; `OBNS_VULKAN=0` or `1` overrides the option.

| Tuen Ma Line, steady state (each view's first 10 s left out), a loaded machine, runs interleaved | OpenGL | Vulkan |
|---|---|---|
| Cab: frame | 13.4-20.0 ms | 8.5-11.8 ms |
| Exterior: frame | 25.1-30.1 ms | 14.0-18.4 ms |
| Exterior: the world layer on the main thread | 17.7-21.5 ms (drawing) | 7.1-9.0 ms (gathering the faces, handing over, compositing) |

Vulkan's world takes 0.6 ms of GPU time in the cab and 0.9 outside. Most of what remains on the main
thread is upstream's per-face work, gathering every visible face each frame. In one of nine Vulkan runs the
frames were 44-300 ms throughout; the other eight were as above, and on this machine, under load from
other programs, that one is taken for now as the load.

Also tried: a GL state filter (from the Android port's GL layer, `Graphics/GLStateFilter.cs`) skipping
GL sets that change nothing. On the desktop it costs more than it saves - each call through it goes from
OpenTK into managed code and out again - so it is off unless `OBNS_GL_FILTER=1`.

### 4. The x86 plugin layer for macOS and Linux

Upstream runs a 32-bit Win32 train plugin only on Windows (directly in a 32-bit process, else through
Win32PluginProxy) and under 32-bit WINE; anywhere else it refuses the plugin and the train gets the
default ATS-Sx. The desktop build now runs such a plugin as Android does: in the x86 layer, an
IA-32 interpreter with a PE loader and the Windows and C runtime calls plugins make
(`shared/OpenBve.X86`), through the Android port's plugin bridge (`shared/NativePlugins`). If the plugin
cannot start there, a managed translation stands in (DetailManager's module list, OS_ATS from its
configuration), else an inert one, and the driver is told; a train without one never drives into the
ATS-Sx brake its real system would not have applied.

- Where: on Linux, macOS and FreeBSD, and in the 64-bit program under WINE (which has no proxy). On
  Windows the proxy stays, unless `OBNS_X86_PLUGINS=1` sends 32-bit plugins to the layer too, for
  comparing the two.
- How: OBNS's copy of `Plugin.Functions.cs` (`UseX86Layer`) hands the plugin to
  `NativePluginBridge.TryRun`; the desktop's TrainManager compiles the shared files in (C# 12; the
  few runtime functions .NET Framework 4.8 lacks are in `OpenBve.X86/Compat.cs`). Android builds the
  same files for net10.0, with its own way into the bridge (`NativePluginBridge.Android.cs`).
- Log: what was installed goes to the log file; `OBNS_X86_DEBUG=1` adds every key, signal, beacon
  and panel change; `OBNS_PLUGIN_TRACE=<folder>` (or `1`, for `plugin-trace` in the settings
  folder) records the calls (`plugin-trace.bin`) for `OpenBve.X86.Tests <root> <dll> replay <trace>`.
- Running it (2026-10-29): Linux needs Mono (`mono-complete`; Ubuntu 22.04's 6.8 works) with OpenAL and
  SDL2. macOS needs Mono, `mono-libgdiplus` and SDL2 (Homebrew; arm64 on Apple silicon), and on Apple
  silicon `preferNativeBackend = false` under `[display]` in options.cfg: OpenTK 3's native Cocoa
  backend crashes there (a variadic objc_msgSend called as a fixed one), its SDL2 backend works. Start
  it as `DYLD_FALLBACK_LIBRARY_PATH=/opt/homebrew/lib mono OpenBve.exe`.
- Checked on Linux (Ubuntu 22.04, Mono 6.8, Intel Iris 6100): the Keisei 3500's KqAts.dll runs in the
  layer and drives; the Tuen Ma Line's DetailManager runs OS_Ats1 and kcrwr_seltrac in it. For the E217's
  UCRT DetailManager and modules the layer gained the C runtime's text mode, the bounds-checked string
  functions, `_wstat64i32`, `_wsplitpath_s`, `mbstowcs` and the Universal CRT's printf entry points, and
  for its TIMS_MON_Disp.dll (C++ with file streams) a shim of Microsoft's C++ runtime (`Msvcp.cs`:
  basic_streambuf, basic_ios, basic_istream, the char codecvt, locales, call_once), laid out as Visual C++
  lays them out, as the plugin's own inline template code reads them. All five modules run in the layer
  (checked in `OpenBve.X86.Tests`; `calls` now also presses each ATS key).

Checked (2026-10-29), on Windows with `OBNS_X86_PLUGINS=1`: the Tuen Ma Line's SP1900 runs its
DetailManager.dll with both modules (OS_Ats1, kcrwr_seltrac) in the layer; the TBL display and panel
work, keys reach it, and a frame costs the same as with the proxy (9.2 against 9.3 ms). The plugin
survey (`OpenBve.X86.Tests survey`, every 32-bit plugin in a library for a simulated minute) gives the
same results before and after the layer was made to build for .NET Framework. Not yet run on Linux or
macOS themselves.

### 5. The main menu on macOS: formMain drawn with OpenGL

Upstream shows only a small in-window menu on 64-bit macOS, where Windows Forms cannot show a window. OBNS shows
formMain itself there - every page, control and layout of the Windows menu - with its controls drawn by OpenGL in a
window of its own (`OBNS_GLFORMS=1` shows it on Windows, for testing). See [GLFORMS.md](GLFORMS.md).
Checked on an M1 Mac (2026-10-29, Mono 6.14, SDL2 backend): it opens; the route browser, a BVE5 scenario's
details, the options and the language list work. The language list is sorted by code, as Windows lists the
folder; the renderer choice shows OpenGL only away from Windows (Vulkan draws only there so far).

### 6. Texture memory

Upstream decodes every texture when a route registers it (to learn its format) and keeps that decode on the
handle, and in the texture cache, until it is uploaded; "released" decodes kept their pixels too, through the
decode's ByteArrayOrigin. The Tuen Ma Line registers 9,672 textures: 7.5 GB of pixels held, 8.7 GB of managed
heap, 13-15 GB private on Windows, and more than Linux's 16 GB machines could load. OBNS lets the register-time
decode's pixels go at once, keeping it for its transparency (as the Android port does), and its releases free
the pixels for good (`Texture.ObnsDropPixels`, in OBNS's copies of OpenBveApi's texture files); a texture is
decoded again from its file when uploaded. On the Tuen Ma Line: 1.1 GB live managed heap, 3.9 GB private when
loaded (5.5 GB driving), loading as fast as before. `OBNS_MEMSTATS=1` logs a census of what a loaded route holds
(`2`: after a full collection).
