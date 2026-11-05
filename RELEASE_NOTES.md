# OpenBVE for Android — release notes

## 1.31 (desktop 1.14.1.2-OBNS; Android unchanged, 1.14.1.2-android.6) — Windows on ARM64

### Windows on ARM64 (new)

- **A native ARM64 build** (`OpenBVE-1.14.1.2-OBNS-win-arm64.zip`, for Windows 11 on ARM64 with .NET Framework 4.8.1):
  no x64 emulation for the game itself. OpenAL Soft, Tolk and NVDA's client come built for ARM64. Joysticks are read
  without the RailDriver, whose library is x64 only.
- **Direct3D 12 alone is the default** there, measured on a Snapdragon X (Adreno X1-45): TML without vsync, Direct3D 12
  75.7 fps, Vulkan 51.9, Direct3D 11 44.6.
- **Train plugins:** 32-bit plugins run through upstream's proxy under Windows' x86 emulation. With `OBNS_ATS64=1`, a
  train's 64-bit plugins run in the plugin layer instead, including plugins built as Debug. The E217's ATS-P braked
  alike both ways (line maximum and red signals); Keisei's KqAts64 runs in the layer.
- **Tuned for the Snapdragon X:** the farther shadow cascades are drawn one a frame in turn, and copies of a face are drawn
  as one. TML at High went from 34 to 51 fps.

### Windows

- **Direct3D 12 without vsync** is no longer held to 1.5 or 2 times the display's rate in a window: the swap chain has three
  buffers in place of two. Uchibo on the Snapdragon X: 90 fps before, 197 now.
- **Direct3D 12 draws copies of a face as one** (instanced), on any GPU: East Rail at High on an RTX 2060, 5,320 draws in
  4,146 calls, and 8,995 shadow draws in 3,421.
- **`obns.cfg` names the renderer only when you chose it,** so a default renderer isn't written as if chosen.

### All desktop systems

- **BVE5 and BVE6 routes:** the speed limits of signal aspects (`Section.SetSpeedLimit`) were read as metres a second, 3.6
  times too high: Uchibo's 45 and 95 km/h aspects were 162 and 342 km/h. Android gets this in its next build.
- **Vulkan's pipelines are kept between starts** in the settings folder, so the first frames don't wait on the shader
  compiler: East Rail on an RTX 2060, 23 ms the first time, 2 ms after.
- **A screen reader library that can't be loaded** now means no screen reader, where it stopped the game as a route started.
- **For testing:** `OBNS_DRIVE_LOG=1` logs the train each second of game time (speed against the limits, handles, safety
  system lamps), and `OBNS_DRIVE_TEST=power` adds a driver that holds full power and resets the safety system after a stop.

### Known limitations

- **Windows on ARM64:** NVDA is found and spoken to, but its speech hasn't been heard by a tester. The RailDriver isn't
  supported. 32-bit plugins are emulated.
- **macOS:** the 1.3 app is still current; the 1.31 app will be built when a Mac is available.

## 1.3 (desktop 1.14.1.2-OBNS; Android unchanged, 1.14.1.2-android.6) — Renderers without OpenGL

### Windows (x64), macOS (Apple silicon) and Linux (x86-64)

- **The renderer draws the whole game alone:** Vulkan, Direct3D 12, 11 or 9.0c, or Metal draws everything, the HUD and menus too, with
  no OpenGL at all. It's on by default. It's faster where sharing images with OpenGL cost the most: the Lisuan 7G100, where every
  renderer used to fall back to OpenGL, runs TML at 34 to 68 fps against OpenGL's 23, and a MacBook Neo runs it at 47 fps against 28.
  Where the renderer chosen can't start, the next one is tried, then OpenGL. On Linux it's Vulkan alone, where Vulkan is the default
  renderer (older Intel GPUs without a discrete one stay on OpenGL).
- **OpenGL HUD fallback** (Options, under Renderer; off by default): the renderer draws only the world and the cab and OpenGL the rest,
  as in 1.2. For a computer where the renderer alone draws something wrong.
- **The debug views** (wireframe, event markers, normals) draw with the renderer alone too.
- Known: Direct3D 9 with shadows on draws the scene too dark on the Lisuan 7G100.

### Windows, Ubuntu and macOS

- **White scenery after a jump:** jumping to a station could leave much of the scenery white for the rest of the game, on every renderer except plain OpenGL. Textures whose decode had finished but which had gone out of view used up the places for new ones.
- **Back to the main menu with your own settings folder:** a copy started with `/filesystem=` (a portable or test copy) went back to the user's own settings after "Exit to main menu", as upstream's restart leaves that out. It's kept now.

### macOS

- **No wait for the microphone at start:** the game opened the microphone as it started, so on a Mac where it hadn't been allowed yet, it waited on macOS's question before any window opened. It's opened when a route first plays a microphone sound now.

### Windows

- **OGG sounds** load. The 1.2, 1.21 and 1.22 zips shipped a version of NAudio.Core that the OGG plugin can't use.

## 1.22 (1.14.1.2-android.6; desktop 1.14.1.2-OBNS) — BVE6 cabs and sounds, and faster loading

### All platforms

- **BVE6 cabs:** a needle's `Tilt` makes it follow a gauge printed at an angle, staying in its own layer, and `StopPin` stops it at its limits. `Perspective` is read but not applied.
- **BVE5 and BVE6 sounds:** the vehicle sound file's `[Joint]` and `[AirSpring]` now load, and a route's `JointNoise.Play(index)` picks the point sound. `LeftApply` and `RightApply` aren't played, because OpenBVE has one suspension sound a side.
- **Faster loading:** route objects are shared out one at a time between worker threads, and a train's models load on workers before the parsers need them. Models come out the same.

### Windows, Ubuntu and macOS

- **Cab textures** decode on worker threads during the last stage of loading, on every renderer except plain OpenGL.

### Windows

- **Images that aren't 32-bit** decode about twice as fast, with the same pixels.
- One train and route on a test PC: 42 s in 1.21, about 24 s now, loaded a second time with the disk cache warm.

### Android, Ubuntu and macOS

- **The plugin layer's log** says x64 when it's running a 64-bit plugin.

### Known limitations

- **BVE6 content** has only been tested with one route and train, Uchibo and the E217.
- Otherwise as in 1.21, apart from its log wording.

## 1.21 (1.14.1.2-android.5; desktop 1.14.1.2-OBNS) — 64-bit train plugins

### All platforms

- **Heavy models load much faster:** CSV and B3D models split into thousands of mesh blocks no longer slow loading so sharply. One
  train's cab went from about 125 s to under 6 s on a Windows test PC, and that train and route from 162 s to 42 s. Models come out the same.
- **BVE5 trains with a `train.xml`:** a `<Car>` that only sets something like a length or object no longer replaces the car's BVE5
  traction. Setting `MotorCar` to false, or giving acceleration curves, still replaces it. `extensions.cfg` works as before.

### Android, Ubuntu and macOS

- **64-bit train plugins:** a train with only an `Ats64` plugin now runs it in the plugin layer. Where a train has both, `Ats32` is still
  used. Checked with the E217's six plugins on Android and macOS. Not yet run on Ubuntu.

### Windows

- **32-bit plugins from a download:** a train plugin no longer brings up a Windows security prompt when the program came from a downloaded zip.

### macOS

- **Input plugin settings:** the settings windows of the five input plugins included with the game (KATO, SanYing, Densha de GO, ZUIKI and
  the default display plugin) now open and save.
- **Menu:** links are gold as on Windows, a dialog opened from a dialog returns to it when closed, and a long title on a tall button wraps.

### Known limitations

- **macOS input plugins:** third-party plugins' settings windows still can't open.
- **Plugin log wording:** the log says "x86" for the plugin layer even when it's running a 64-bit plugin.
- **64-bit plugins that start another program,** such as `DetailManager_.dll`, can't run in the plugin layer.
- **BVE5 plugins that read BVE5's memory** (AtsEx and the like) can't work outside BVE5.
- **`Track[...].Position(x, y, radius)`:** earlier notes, including 1.21's as first published, said the parser rejects this. It doesn't: all its forms load.
- **macOS, first opening** still needs Open Anyway (see 1.2), and **Metal** is still off on Intel, AMD and NVIDIA Macs.

## 1.2 (1.14.1.2-android.4; desktop 1.14.1.2-OBNS) — BVE5 trains, and Windows, Ubuntu and macOS

### Android

- **Upstream OpenBVE 1.14.1.2,** with the in-game commands Android didn't have: headlights, pantographs, breaker, sanders,
  gears, uncoupling, time acceleration, mute, head-out views, the switch menu, axes and debug views. Motion blur still needs
  OpenGL ES or Vulkan through ANGLE.
- **BVE5 trains and their safety plugins:** vehicle, parameters, performance tables, panel, sound and motor noise files.
  32-bit plugins run in the x86 layer.
- **BVE5 routes:** `rand(x)` with a fractional x works, so the Uchibo line gains its missing Kominato train.
- **BVE5 panels:** the `am` subject (the ammeter) and colours written without `#` now work.

### Desktop (new)

The same train and plugin support, on upstream's own program and menu, as a build separate from upstream's:

| | Windows x64 | Ubuntu x86-64 | macOS |
|---|---|---|---|
| File | `OpenBVE-1.14.1.2-OBNS-win-x64.zip` | `OpenBVE-1.14.1.2-OBNS.deb` | `OpenBVE-1.14.1.2-OBNS.dmg` |
| Renderers | OpenGL, Vulkan, Direct3D 9, 11 and 12 | OpenGL, Vulkan | OpenGL, Metal (Apple silicon) |
| Needs | .NET Framework 4.8 | Mono and the rest, installed by the package | Mono, `mono-libgdiplus` and SDL2 (`brew install mono mono-libgdiplus sdl2`) |

- **Renderers:** each one draws the world while OpenGL draws the rest of the frame, so a GPU also needs a way to share images
  with OpenGL. Without one the game falls back to OpenGL and says why in the options. `OBNS/GPU_SUPPORT.md` lists the defaults.
- **Plugins on Ubuntu and macOS:** 32-bit Windows train plugins run in the x86 layer.
- **macOS menu:** the Windows menu's pages and layout, with the Mac's own controls.
- **Performance:** per-frame updates on all cores and fewer draw calls, with shadows on every renderer.

### Known limitations

- **macOS input plugins:** their settings windows can't open, so their settings can't be changed in the program. To be fixed in 1.2.1.
- **64-bit train plugins:** a train with only an `Ats64` plugin has no working safety plugin on Android, and probably none on
  Ubuntu or macOS, which haven't been tested with one. To be fixed in 1.2.1.
- **BVE5 plugins that read BVE5's memory** (AtsEx and the like) can't work outside BVE5.
- **Some BVE5 map 2.02 statements** are rejected by the parser, such as `Track[...].Position(x, y, radius)`; such routes load without them.
- **macOS, first opening:** the app has no Developer ID, so macOS refuses to open it ("Apple could not verify..."). Choose Done,
  then System Settings > Privacy & Security > Open Anyway. On macOS 15 and later, right-click > Open no longer gets past this.
- **macOS, Metal:** off on Intel, AMD and NVIDIA Macs, which haven't been tested, and they use OpenGL.
- **Package creation in the macOS menu and RailDriver calibration** haven't been tested through to the end.

## 1.1 (1.14.0.3-android.3) — native Vulkan renderer

### Native Vulkan renderer, now the default

The game is drawn with Vulkan directly - no OpenGL ES, no ANGLE - with much less work per object,
the drawing recorded on several processor cores, and each frame recorded and shown while the next
is already being prepared. Compiled graphics pipelines are kept between games, so new kinds of
objects no longer stutter the first time they appear. On phones with Vulkan 1.3 it uses 1.3's
dynamic rendering.

OpenGL ES is used instead, by itself, on phones without Vulkan 1.1, and on a phone where Vulkan
failed to start (it tries again after a system update, or when chosen again). **Options → Advanced
Options → Graphics backend** chooses OpenGL ES, Vulkan through ANGLE, or native Vulkan. It should
look exactly as with OpenGL ES; if anything looks different (missing or black objects, wrong
colours or transparency), please report the route, the train and a screenshot, and choose OpenGL
ES. Measured on 20 phones (median frames per second, cab / exterior view; 60 and 120 are the
screens' limits; East Rail with the R-Train, and the HKRSC Tuen Ma Line pack with the SP1900):

| Phone | East Rail, OpenGL ES | East Rail, Vulkan | Tuen Ma Line, OpenGL ES | Tuen Ma Line, Vulkan |
|---|---|---|---|---|
| Nokia C31<br>Unisoc SC9863A | 19 / 4.7 | 22 / 12.4 | not run (storage) | not run (storage) |
| Galaxy A04s<br>Exynos 850 | 25 / 3.3 | 43 / 15 | 3.8 / 2.5 | 17 / 7.5 |
| Galaxy A05s (6 GB)<br>Snapdragon 680 | 20 / 14 | 39 / 19 | 7.8 / 8.6 | 22 / 11.5 |
| Galaxy A23 (two phones)<br>Snapdragon 680 | 19 / 14-15 | 39-40 / 19.5 | 13-14 / 6.5-7 | 19-20 / 10-11 |
| Galaxy A23 5G (SCG18, Japan)<br>Dimensity 700 | 25 / 14.5 | 32 / 13.6 | 19 / 9.9 | 15 / 15 |
| Galaxy A14 5G<br>Dimensity 700 | 42 / 15 | 59 / 32 | 15 / 6.3 | 25 / 11.6 |
| Galaxy Tab A9<br>Helio G99 | 53 / 16.5 | 52 / 30 | 21 / 10.6 | 36 / 20.5 |
| Galaxy Tab S5e<br>Snapdragon 670 | 20 / 15 | 32 / 18 | 13 / 6.8 | 17 / 12 |
| Galaxy A52 4G<br>Snapdragon 720G | 48 / 22 | 76 / 25 | 25 / 11 | 30 / 15 |
| Galaxy A36<br>Snapdragon 6 Gen 3 | 87 / 24 | 107 / 43 | 33 / 16 | 45 / 24 |
| Galaxy A73<br>Snapdragon 778G | 71 / 26 | 114 / 34 | 24 / 13 | 28-34 / 18-23 |
| Galaxy Z Flip4<br>Snapdragon 8+ Gen 1 | 95 / 29 | 113 / 44 | 32 / 13 | 38 / 22 |
| Galaxy Z Flip5<br>Snapdragon 8 Gen 2 | 119 / 36 | 119 / 47.5 | 38 / 17 | 40 / 25.6 |
| Galaxy S20 FE 4G<br>Exynos 990 | 46 / 15 | 118 / 39 | 19 / 9.6 | 30 / 17 * |
| Galaxy A71 5G<br>Exynos 980 | 60 / 9.1 | 60 / 27 | 7.0 / 4.3 | 28 / 15 |
| Galaxy A35<br>Exynos 1380 | 114 / 35 | 120 / 59 | 35 / 15 | 49 / 25 |
| Galaxy A55<br>Exynos 1480 | 104 / 33 | 119 / 48 | 36 / 18 | 53 / 27 |
| Galaxy S24 Ultra<br>Snapdragon 8 Gen 3 | 120 / 52 | 120 / 64 | 48 / 23 | 62 / 34 |
| Galaxy S8 (no Vulkan 1.1)<br>Exynos 8895 | 40 / 5.6 | OpenGL ES used | 6.2 / 3.8 | OpenGL ES used |

\* With **Smaller textures with native Vulkan** (below). Without it, the S20 FE 4G ran out of memory
in the Tuen Ma Line's exterior view.

The cheapest phones gain the most: the A04s draws East Rail's exterior at 15 fps instead of 3, and
the Tuen Ma Line's cab at 17 instead of 4; the A71 the Tuen Ma Line at 28 / 15 instead of 7 / 4. The
Galaxy A23 5G (Japanese model) gains little - its Tuen Ma Line cab is slower - though the A14 5G,
with the same chip, gains as much as the others. The Galaxy S8, without Vulkan 1.1, used OpenGL ES
by itself.

### Smaller textures with native Vulkan

**Options → Quality → Smaller textures with native Vulkan**, on by default. On phones with about
6 GB of RAM or less, native Vulkan keeps textures to 1024 px and 16-bit colour (where the largest
texture size and texture colours are automatic): Vulkan's texture memory cannot be swapped out as
OpenGL ES's partly can, and a Galaxy S20 FE 4G was closed by Android for lack of memory in the Tuen
Ma Line's exterior view. With it, the S20 FE runs it at 30 / 17 fps (OpenGL ES: 19 / 9.6). Turn it
off for sharper textures on lighter routes.

### Game mode on the Start page

**Mode: Arcade / Normal / Expert** is beside the Start button, as on the desktop's Start page; every
game used to run in Normal.

### Other changes

- Animated objects and the trains' cars are updated on all processor cores each frame.
- **Safety system buttons can be held**: keep a button pressed for over half a second and it stays
  pressed when you let go (shown with "●"); tap it again to release it - for dead-man switches
  such as Barcelona Metro L2/L3's. All the safety buttons (A2-G, H-P) are in the ATS... menu, in
  two rows.
- Train plugin gimmicks that set panel values outside a frame now work (e.g. the MTR R-Train's
  pantograph and LED destination displays through OS_Ats1).

### Known limitations of the native Vulkan renderer

- Shadows and motion blur are not drawn (the Android port does not use them yet either).

## 1.01 (1.14.0.3-android.2) — performance

Heavy routes are now playable, including on 4 GB phones. Measured on the HKRSC Tuen Ma Line pack
(36 scripted trains, ~31,000 object files):

| Phone | 1.0 load | 1.01 first load | 1.01 repeat load | Cab / exterior fps |
|---|---|---|---|---|
| Galaxy A73 | ~20 min | 101 s | 69 s | 29-31 / 13-17 |
| Galaxy A05s (4 GB) | could not run | 118 s | 86 s | 8-10 / 7-9 |
| Galaxy A05s (6 GB) | - | 110 s | 84 s | 8 / 7-8 |

### Faster loading

- Object files are read and prepared on all processor cores; scripted trains load in parallel.
- File checks, texture registration and object caching no longer slow down on large routes.
- Textures' transparency is remembered between loads, so loading a route again is faster.

### Faster frames

- Still scenery and still train parts are drawn in batches; trains' parts outside the view are
  skipped; faster compiled code (LLVM).
- New option **Quality → Transparency: Sharp / Smooth** (upstream's): Sharp is faster on weak GPUs.

### Less memory

- Identical meshes of trains and animated scenery are shared, instead of one copy per car.
- **Largest texture size** (Options): automatic 1024 px on phones with 4 GB of RAM or less and
  2048 px on 6 GB phones; or choose 2048/1024/768/512 px or no limit.
- **Texture colours** (Options): 16-bit textures need half the memory; automatic on phones with
  4 GB of RAM or less.
- Uploaded vertex data is no longer kept twice; large images are reduced before they reach the
  app's memory.

### Fixes

- .NET train plugins built for .NET Framework (e.g. Barcelona Metro L3's FCMB) load again.
- PowerVR GPUs (e.g. Nokia C31) drew only the sky: fixed.
- The app no longer crashes on start when a content folder cannot be created.
- Sounds paused before they had started no longer flood the log with errors; the desktop HUD's
  frame counter works; no false "stopped responding" message while in the background.

### Known limitations

- Vulkan is still OpenGL ES through the device's ANGLE (Android 14 and later); a native Vulkan
  renderer is planned after 1.01.
- On the weakest GPUs, dense routes remain slow in the exterior view.

## 1.0 (1.14.0.3-android.1) — first release

OpenBVE 1.14.0.3, the free train simulator, running natively on Android 7.0 or later (OpenGL ES
3.0; arm64 phones, plus x86_64 for Chromebooks and emulators).

### What it does

- **Routes and trains** in OpenBVE's formats (CSV/RW routes, BVE5, Mechanik; b3d/csv/x/obj/
  animated objects; train.dat/xml trains, 2D panels and 3D cabs, sounds), loaded from
  **Documents/OpenBVE** on the SD card, or on the phone when it has no SD card.
- **Train safety plugins**: the built-in ATS/ATC, managed .NET plugins, and 32-bit Windows plugin
  DLLs through a built-in compatibility layer (tested: KCR TBL, MTR SelTrac, OS_Ats1 TPWS, and
  a survey of other countries' plugins).
- **Scripted and preceding trains**, signals, stations, timetables, score and in-game messages.
- **Touch driving**: power, brake, reverser, horns, doors, emergency brake, safety-system keys,
  camera views and a camera keypad, AI driver; touch the cab's own levers and switches; an
  information bar (speed, limit, notches, next stop, safety warnings); a timetable card with a
  route map and gradient profile. Button size, opacity and hiding are adjustable.
- **Keyboard and game controllers**, with a Controls page (main menu and pause menu) to change
  every key and button; menus work with a controller.
- **Desktop mode**: in Samsung DeX (and other Android desktop modes) the app switches to the
  desktop interface — upstream's HUD, keyboard and controller — and follows the window between
  the phone and an external display.
- **Graphics**: OpenGL ES, or Vulkan through the device's ANGLE (Options).
- **Main menu** as on Windows: route and train selection with previews, a file finder, package
  installation, options, in OpenBVE's 28 languages (the Android-only labels in 13 of them, as
  first drafts).

### Known limitations

- **Heavy routes are not yet playable.** A route on the scale of the HKRSC Tuen Ma Line pack
  (~31,000 object files, 4.3 GB) takes about 10 minutes to load on a Galaxy A73 and runs at
  10–15 fps in the cab and 2–5 fps in the exterior view. Typical routes run at playable rates;
  dense scenery still costs frame rate (the main work for the next versions).
- Windows plugins that are 64-bit, or need a Windows GUI, DirectX, networking or extra threads,
  cannot run; CS-Script animation scripts are unavailable.
- Translations of the Android-only labels are drafts awaiting native speakers.
- Uninstalling deletes the app's own folder (Android/data/net.openbve); content in
  Documents/OpenBVE is kept.
