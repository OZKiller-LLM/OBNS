# OpenBVE for Android — release notes

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
