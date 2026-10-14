# OpenBVE for Android — release notes

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
ES. Measured on 20 phones (median frames per second, cab / exterior view; 60 and 120 are the screens' limits; East Rail with the
R-Train, and the HKRSC Tuen Ma Line pack with the SP1900):

| Phone | East Rail, OpenGL ES | East Rail, Vulkan | Tuen Ma Line, OpenGL ES | Tuen Ma Line, Vulkan |
|---|---|---|---|---|
| Nokia C31 | 19 / 4.7 | 22 / 12.4 | not run (storage) | not run (storage) |
| Galaxy A04s | 25 / 3.3 | 43 / 15 | 3.8 / 2.5 | 17 / 7.5 |
| Galaxy A05s (6 GB) | 20 / 14 | 39 / 19 | 7.8 / 8.6 | 22 / 11.5 |
| Galaxy A23 (two phones) | 19 / 14-15 | 39-40 / 19.5 | 13-14 / 6.5-7 | 19-20 / 10-11 |
| Galaxy A23 5G (SCG18, Japan) | 25 / 14.5 | 32 / 13.6 | 19 / 9.9 | 15 / 15 |
| Galaxy A14 5G | 42 / 15 | 59 / 32 | 15 / 6.3 | 25 / 11.6 |
| Galaxy Tab A9 | 53 / 16.5 | 52 / 30 | 21 / 10.6 | 36 / 20.5 |
| Galaxy Tab S5e | 20 / 15 | 32 / 18 | 13 / 6.8 | 17 / 12 |
| Galaxy A52 4G | 48 / 22 | 76 / 25 | 25 / 11 | 30 / 15 |
| Galaxy A36 | 87 / 24 | 107 / 43 | 33 / 16 | 45 / 24 |
| Galaxy A73 | 71 / 26 | 114 / 34 | 24 / 13 | 28-34 / 18-23 |
| Galaxy Z Flip4 | 95 / 29 | 113 / 44 | 32 / 13 | 38 / 22 |
| Galaxy Z Flip5 | 119 / 36 | 119 / 47.5 | 38 / 17 | 40 / 25.6 |
| Galaxy S20 FE 4G | 46 / 15 | 118 / 39 | 19 / 9.6 | 30 / closed (memory) |
| Galaxy A71 5G | 60 / 9.1 | 60 / 27 | 7.0 / 4.3 | 28 / 15 |
| Galaxy A35 | 114 / 35 | 120 / 59 | 35 / 15 | 49 / 25 |
| Galaxy A55 | 104 / 33 | 119 / 48 | 36 / 18 | 53 / 27 |
| Galaxy S24 Ultra | 120 / 52 | 120 / 64 | 48 / 23 | 62 / 34 |
| Galaxy S8 (no Vulkan 1.1) | 40 / 5.6 | OpenGL ES used | 6.2 / 3.8 | OpenGL ES used |

The cheapest phones gain the most: the A04s draws East Rail's exterior at 15 fps instead of 3, and
the Tuen Ma Line's cab at 17 instead of 4; the A71 the Tuen Ma Line at 28 / 15 instead of 7 / 4. The
Galaxy A23 5G's small GPU gains little (its Tuen Ma Line cab is slower). The Galaxy S8, without Vulkan 1.1, used OpenGL ES by
itself.

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
- On a phone with little free memory (a Galaxy S20 FE 4G, 5.3 GB, about 800 MB free with the game
  running), the Tuen Ma Line's exterior view can use more memory than the phone has free, and
  Android closes the game. OpenGL ES, whose memory Android can partly swap out, gets through
  there; choose it in Options for very heavy routes on such phones.

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
