# OpenBVE 1.14.0.3 — Android port

Working notes for porting OpenBVE to run natively on Android (.NET for Android, no emulation
layer, no desktop VM).

## Layout

| Path | What it is |
|---|---|
| `OpenBVE-1.14.0.3/` | Upstream source, from the 1.14.0.3 release tag. Kept as close to pristine as possible — see *Upstream patches* below. |
| `android/` | The port. SDK-style projects that compile the upstream sources for Android. |
| `android/OpenBve.Compat/` | Compatibility layer: Android replacements for desktop-only BCL APIs. |
| `android/OpenBveApi.Android/` | The upstream `OpenBveApi` sources, built for Android. |

## Approach

Upstream is 43 non-SDK `.csproj` projects targeting .NET Framework 4.8, with Windows Forms,
desktop OpenGL and Win32 P/Invoke throughout. Rather than rewriting them, each port project is
a thin SDK-style "shim" that **compiles the upstream `.cs` files in place** and supplies
Android-appropriate references. Benefits: the upstream tree stays mergeable with future
OpenBVE releases, and the diff to review is small.

Where the upstream code calls an API Android does not have, the first choice is a
**compatibility shim** in `OpenBve.Compat` that presents the same namespace and signatures.
Editing upstream is the last resort.

## Build

Requires the .NET 10 SDK with the `android` workload, plus a JDK 17+ and the Android SDK.
`android/Directory.Build.props` points the build at Android Studio's bundled JBR, because a
Java 8 JRE sits ahead of the real JDKs on this machine's PATH.

```
dotnet build android/OpenBve.Android/OpenBve.Android.csproj -c Debug -t:Install
dotnet build android/OpenBve.Android/OpenBve.Android.csproj -c Release
adb install -r android/OpenBve.Android/bin/Release/net10.0-android36.0/net.openbve-Signed.apk
```

Use Release to judge speed: it is compiled ahead of time for arm64 and x86_64 (about 2 minutes
to build) and loads routes 2–4× faster than Debug. See *Loading time*.

### Release packaging

Builds for other people come from `android/release.ps1`, never from a development build:

```
androidelease.ps1 -PortRelease 1                  # dist.14.0.3-android.1\OpenBVE-1.14.0.3-android.1.apk
androidelease.ps1 -PortRelease 2 -Beta            # the beta testers' build, with the data log (…-beta.apk)
androidelease.ps1 -PortRelease 1 -Format both     # plus the App Bundle (.aab) for Google Play
```

- **Numbering**: `UpstreamVersion` (1.14.0.3) and `PortRelease` in the csproj give the version
  name `1.14.0.3-android.<n>` (`-beta` added for a beta) and the version code
  `11403 × 100 + n`, which rises with either; every published build needs a higher `-PortRelease`.
  `dist\RELEASES.txt` records each build made.
- **Signing**: with the release key (`%USERPROFILE%\.openbve\openbve-release.jks`, alias `openbve`,
  or `-KeyStore`/`OPENBVE_KEYSTORE`); the password is asked for or read from
  `OPENBVE_KEYSTORE_PASS`/`OPENBVE_KEY_PASS`, and reaches MSBuild only through the environment
  (`AndroidSigningStorePass=env:…`). The key is made once by the maintainer (keytool command in
  the script's help) and must be kept: Android updates an app only from the same key. Builds so
  far were signed with the SDK debug key, so moving to the release key needs one uninstall
  (which deletes `Android/data/net.openbve`, where the content folders are). `-DebugKey` tries the
  packaging without the key; its output is marked `-debugkey` and says it is not for distribution.
- **Output**: `dist\<version>\` with the APK (universal: arm64-v8a and x86_64, about 51 MB;
  `-Arm64Only` for phones only), optionally the .aab, `SHA256SUMS.txt`, `LICENSE.txt` and a
  `README.txt` for installers (installing, updating, the key conflict, the beta log, and the
  LGPL notice with the source of OpenAL Soft). The script checks the signature (apksigner or
  jarsigner) and reads the version back from the package.
- The in-app version (menu rail, About, crash logs, beta log) is the package's version name.

## Status

| Component | State |
|---|---|
| `OpenBve.Compat` | **Builds and runs on device.** `System.Drawing` (Skia-backed) and `System.Windows.Forms` shims. |
| `OpenBveApi` | **Builds and runs on device** (`net10.0-android36.0`), verified by the smoke test below. |
| `OpenBve.GLES` | **Builds, and runs on device.** The OpenTK 3 API surface bound to OpenGL ES 3. |
| Shaders | **All 5 shader programs compile and link on device**, translated to GLSL ES 3.00 at upload time. |
| `LibRender2` | **Builds** against the GLES shim, with **no upstream patches**. Not yet drawing: needs an on-screen surface and a render loop. |
| `OpenBve.OpenAL` | **Builds, and plays audio on device.** The OpenTK 3 OpenAL surface bound to OpenAL Soft. |
| `SoundManager` | **Builds.** No upstream patches. |
| `RouteManager2` | **Builds.** No upstream patches. |
| `TrainManager` | **Builds**, with the Win32 plugin proxy excluded (see below). |
| `Formats.Msts` | **Builds.** No upstream patches. |
| Content plugins — all 22 (`Route.CsvRw`, `Route.Bve5`, `Route.Mechanik`, `Object.*`, `Texture.*`, `Sound.*`, `Train.OpenBve`, `Train.MsTs`, `Formats.*`, `AssimpParser`, `OpenBveAts`) | **All build.** Every route, object, texture, sound and train format OpenBVE supports. |
| `OpenBVE` main app | Replaced by the Android front end below; its Windows Forms menu is re-created as native Android views. |
| `OpenBve.Android` (front end) | **Loads a route and a train, runs the simulation, and renders from the driver's cab on device.** Activity, `SurfaceView`, EGL context, render thread, host (textures, objects, animated world objects, nearest-train lookup), options, renderer with background, fog and the world layer, train manager, train session, plugin registry and route loader. Touch driving controls, sound (OpenAL Soft), and an OpenGL ES or Vulkan (ANGLE) backend. Scripted trains (track following objects). A main menu following the Windows one: route and train selection with previews and an in-built file finder, package management (OpenBVE packages and plain archives), options including the graphics backend, in OpenBVE's 28 languages. 2D cab panels and preceding trains still to come. |
| Route/Object Viewer, Train Editor, ObjectBender, input-device plugins | **Out of scope.** Windows Forms applications. |

**33 assemblies build for `net10.0-android36.0`** — every library OpenBVE needs to load and run a
route, minus the front end.

Third-party dependencies restore and build unchanged from NuGet: NAudio, NVorbis, NLayer,
ANTLR and `Bve5_Parsing` among them. Several resolve as .NET Framework assets (NU1701) but are
pure managed code and load fine; the smoke test is the check that matters.

## Smoke test

`android/OpenBve.SmokeTest` is an Android app that runs `OpenBveApi` on a device and reports
what worked, to logcat under the tag `OPENBVE_SMOKE` and on screen. It is the gate before more
layers are stacked on top: compiling proves nothing about Mono's behaviour at runtime.

It currently covers the precise timer (whose `kernel32` P/Invoke must fall back to a stopwatch),
a PNG encode/decode/`BitmapOrigin` round trip that checks pixels arrive as RGBA with alpha
intact, text overlay rendering through the Skia `Graphics`/`Font` shims, loading the embedded
translation, case-insensitive path resolution, compiling and linking all five of OpenBVE's
shader programs through the GLES shim on an offscreen EGL pbuffer context, and playing a tone
through OpenAL Soft with the same setup calls `SoundManager` makes.

The shader test earns its keep: it exercises the P/Invoke bindings, the GLSL translation and the
driver in one go, and every shader problem below was found by it rather than by reading specs.

```
dotnet build android/OpenBve.SmokeTest/OpenBve.SmokeTest.csproj -t:Install
adb shell am start -n net.openbve.smoketest/crc64ff8d715676c236fc.MainActivity
adb logcat -d -s OPENBVE_SMOKE:V
```

Last run on the `Medium_Phone_API_36.1` emulator (Android 16, x86_64): **7 passed, 0 failed**,
with all 5 shader programs compiled and linked on OpenGL ES 3.1.

Note that the emulator's GL is a translator onto desktop GL, so it is more forgiving than a
phone's real driver in places. Shader compilation is the exception — it runs the same ESSL
front end — but rendering results still need checking on real hardware.

## Compatibility layer

### `System.Drawing` → SkiaSharp

`System.Drawing.Primitives` (`Color`, `Point`, `Size`, `Rectangle`) ships with the Android BCL, so
only the `System.Drawing.Common` types are re-implemented: `Bitmap`, `Image`, `Graphics`, `Font`,
`Brush`/`SolidBrush`, `ColorPalette`, and `System.Drawing.Imaging.{PixelFormat, ImageLockMode,
BitmapData, ImageFormat}`.

Two details matter for texture fidelity:

- Bitmaps are held as **unpremultiplied BGRA8888**, which is the memory layout OpenBVE's texture
  pipeline expects from `Format32bppArgb`. `BitmapOrigin.GetTexture` reads those bytes directly
  and swaps to RGBA.
- Skia cannot draw onto an unpremultiplied surface, so `Graphics` renders to a premultiplied
  working copy and converts it back on `Flush`/`Dispose`.

### `System.Windows.Forms` → logging and stubs

`MessageBox` writes to logcat and calls an optional handler the Android front end can install;
it answers dialogs with the non-destructive default, since there is no user to ask on a
background thread. `Application.StartupPath` is settable and is pointed at the app's private
files directory during startup. `ComboBox`/`BindingSource` exist only so the upstream
`Translations.ListLanguages` helpers compile — the Android menu reads the language list directly.

## The front end

`android/OpenBve.Android` replaces upstream's Windows Forms and OpenTK windowing entirely:

| Piece | What it does |
|---|---|
| `MenuActivity` | The launcher: the main menu (Start new game, Package Management, Options, About). See *The main menu*. |
| `GameActivity` | The game, in its own `:game` process: the surface, driving controls, loading screen. |
| `GameView` | A `SurfaceView` whose render thread owns the EGL context for its lifetime, because LibRender2 assumes GL happens on one consistent thread. It tells the shim which thread that is through `AndroidGraphicsContext`. |
| `AndroidHost` | The `HostInterface`. Messages go to logcat; texture loading routes through the renderer's `TextureManager`, mirroring the desktop host. |
| `AndroidRenderer` | A concrete `BaseRenderer`. The base class carries the whole implementation and declares no abstract members. |
| `AndroidOptions` | `BaseOptions` with mobile defaults: 400 m viewing distance, no anisotropic filtering, no shadow cascades. |
| `AndroidFileSystem` | Extracts packaged data (languages, flags) to the app's files directory on first run and points `FileSystem` at Android paths. Route and train content (Railway, Train, packages' other folders) lives in **Documents/OpenBVE on the SD card**, or in the phone's own Documents/OpenBVE when no SD card is mounted; both need all-files access, and until it is granted (or if the folder cannot be made) the app's own Android/data folder is used, as in earlier builds. Granting access in the menu moves the folders at once (`UpdateContentFolder`); the file finder keeps offering the old app folder while it holds content. |

Shaders are **not** shipped as Android assets: `AbstractShader` loads them from embedded
resources named `LibRender2.<name>.<ext>`, so `LibRender2.Android` embeds them under those exact
logical names.

### Plugin registration

Upstream discovers content plugins by scanning a `Plugins` folder for assemblies and reflecting
over each one. An APK has no such folder, so `AndroidPlugins` names the 19 plugin assemblies
explicitly, loads each by name and reflects over its types exactly as upstream does, then calls
the same `ContentLoadingPlugin.Load`.

**This means the managed linker must not trim them.** Nothing references these assemblies
statically, so a release build with linking enabled will strip them and plugin registration will
silently find nothing. Debug builds do not link, which is why it works today; release packaging
needs `TrimmerRootAssembly` entries or equivalent.

### Content layout

Route and train content goes in external storage, where a file manager can reach it:

```
/storage/emulated/0/Android/data/net.openbve/files/
    Railway/Route/<route>.csv     Railway/Object/    Railway/Sound/
    Train/
```

Routes reference objects and sounds relative to **sibling folders of the Route folder**, not
relative to the route file, so the loader derives `Railway/Object` and `Railway/Sound` from the
route's parent.

`android/testcontent/` holds the test route. It is built entirely from OpenBVE's own
open-source **Uchibo** compatibility objects (`assets/Compatibility/Uchibo`, which ship with
OpenBVE), so it can be redistributed with the port. It exercises textured objects, a background
image, a ground object, ballast, a second track, a walled cutting, a station platform and roof,
signals, curves and gradients — 285 static objects, loading in about 1.4 s.

It also exercises case-insensitive file lookup on Android's case-sensitive storage, twice:
Uchibo's own objects load `Tie.png` and `Grass.png`, which exist on disk only as `tie.png` and
`grass.png` (Windows hides this), and the route deliberately references its ground as
`UCHIBO\grass.CSV`. All resolve.

Two things in the test route that look like bugs but are content: `Track.Height` must lower the
ground below the rail, or it hides Uchibo's ballast, which sits 14 cm under rail level; and
Uchibo's `WallR.csv` is not a wall on the right but a raised trackbed profile spanning both
sides. The handedness check that matters — the second track at X = -3.8 appears on the left — is
correct.

### Loading a route and drawing it

The sequence mirrors upstream's `Program` startup and `Loading` path:

1. Construct the renderer and **train manager** — a full (non-preview) route load places scripted
   trains through the train manager, so passing none is a null dereference.
2. **Register plugins, then `Initialize` the renderer** — `Initialize` loads menu textures through
   the texture plugins.
3. Create a `CurrentRoute` and hand it to the host **and** to `TrainManagerBase.CurrentRoute`
   before parsing: objects are created during the parse against its disposal mode and block
   length, and the route plugins cast the `ref object route` argument to it rather than creating one.
4. `LoadRoute(..., PreviewOnly: false)`. Objects arrive through the host's `CreateStaticObject`,
   which registers them with the renderer.
5. `PrepareScene`: field of view, viewing distance, a camera track follower, then
   `InitializeVisibility` to build the VAOs and show what is in range.
6. Each frame: move the camera along the track, `UpdateVisibility`, `RenderScene`.

`AndroidRenderer.RenderScene` is a trimmed port of upstream's `NewRenderer.RenderScene`: the view
and light transforms, the scenery viewport, the route background, fog (interpolated along the
track exactly as upstream does), opaque faces, then alpha faces. Shadows, motion blur, particles
and the cab layer are not yet ported.

The host also owns the **animated world object** registry (signals, animated scenery), which
upstream keeps in OpenBVE's `ObjectManager`. Signals are created during the load and registered
there; their per-frame update (aspect changes, animation) is not yet wired, since it wants a
player train.

### Trains

`AndroidTrainSession` ports the player-train parts of upstream's `Loading` and `GameWindow`
sequences:

1. Create a `TrainBase` as the player train, hand it to the train manager and the host, and load
   it through the first train plugin that accepts the folder.
2. Find the player's first station stop and start the clock at its arrival (or departure less
   stop time).
3. `Initialize` the train, enter the first signalling section, nudge each car back and forth once
   so its followers settle (as upstream does), then move the cars to the stop position and enter
   the section they are now in.
4. Put the camera in the driver's car.

Each frame: advance the route clock, `UpdateTrains` (a port of upstream's, including the final
world-coordinate and suspension pass), re-evaluate the signalling sections once a second, update
the animated world objects so signals change aspect, then place the camera at the driver's
position with `CarBase.UpdateCamera`.

`AndroidTrainManager.UpdateTrains` omits developer mode, collision detection and train disposal.
The camera is upstream's interior view without look-ahead or head movement. **Not yet ported:**
preceding and bogus AI trains, the score, and fast-forwarding to the start time.

**The safety plugin is required, not optional.** The power and brake handles only reach the
motors through the plugin's safety state, so with no plugin the actual power notch stays at 0
whatever the driver does. As upstream does, the session loads the train's own `ats.cfg` plugin
and falls back to OpenBVE's default (`OpenBveAts`, shipped as the data asset
`Data/Plugins/OpenBveAts.dll`). Native Win32 plugins are refused as Windows-only and also fall
back to the default.

**`HostInterface.InGameTime` must be the route clock.** Handle changes are scheduled against it
(`PowerHandle.Update` applies a delayed notch when `InGameTime` passes its time), and so are
spring returns and plugin timing. The base class returns a constant 0, which left every delayed
power change pending forever: the MTR train sat with power 2 requested and 0 applied. The Android
host returns `CurrentRoute.SecondsSinceMidnight`, as upstream's does.

### The main menu

`MenuActivity` is the launcher, the Android counterpart of the Windows main form: sections down
the left (Start new game, Package Management, Options, About) and the chosen one on the right.
Every label that the Windows form has comes from OpenBVE's own translation files through
`Menu.T(group, key, fallback)`, so the menu is in all 28 languages OpenBVE ships. The default is
the device's language if OpenBVE has it. Labels that exist only on Android (the file finder, storage
access, the graphics backend) live in an `android` group the translation files don't have yet,
so for now they show the English fallback in every language.

**Two processes.** The game (`GameActivity`) runs in its own `:game` process, and the process
ends with the game. OpenBVE keeps a lot of state in statics (current route, player train,
sound sources, the plugins' own references) and the GL bindings attach to a library once per
process. A fresh process per game gives the clean start upstream gets from a fresh launch.
It also lets an Options change of graphics backend apply to the next game without restarting
the app, and keeps a crash in the simulation from taking the menu with it. Settings are shared
through `AndroidSettings` (shared preferences, re-read across processes); the chosen route
and train travel as intent extras (`LaunchParameters`). With target SDK 36, Back reaches the game
through an `OnBackInvokedCallback`, which shows upstream's "Exit to main menu" dialog.

**Start new game** follows the Windows Start tab: choose a route, then a train, then Start.

- Each has *Browse manually* (the in-built file finder) and *Recently used* (the last ten).
- Selecting a route previews it the way upstream does, by parsing it with the route plugin in
  preview mode. That gives the description, the route's image (or one beside the route file
  with the same name) and the train the route suggests. The menu hosts the plugins for this
  without a GL context; a preview load draws nothing.
- *Use the train suggested by the route* finds that train with a port of upstream's
  `GetDefaultTrainFolder`, plus the app's own Train folder. Choosing a train by hand clears it,
  as on Windows.
- Train previews use the train plugin's `GetDescription` and `GetImage`, with the description
  file's encoding detected as upstream does.
- The route list follows upstream's rules: `.csv` files that contain track commands, `.rw`, and
  `.dat`/`.txt` files a plugin accepts. So a route's `$Include` fragments stay out of the list.

A loading screen shows the route's picture and a progress bar fed by the plugins'
`CurrentProgress` (route first, then train, as upstream's). Back during loading cancels.

**The in-built file finder** (`FileBrowser`) browses the device's storage for route files,
train folders or archives, and content is used where it lies: nothing has to be imported first.
*Places* offers the app's own folders, internal storage, Downloads and any SD card. The app's
own external folder (`Android/data/net.openbve/files`) can always be read, and is reachable from
a PC over USB. Anywhere else needs Android's "all files access"
(`MANAGE_EXTERNAL_STORAGE`, or the storage permissions before Android 11). The user grants it on
a system page that the finder and Options link to; the app only asks.

**Package Management** follows the Windows procedure using upstream's own package code
(`OpenBveApi/Packages`, now compiled into the Android build again):

1. read the package (`Manipulation.ReadPackage`: `package.xml`, image);
2. show its details;
3. check dependencies and recommendations against the package database;
4. compare versions with any installed copy (newer / older / same);
5. extract to the route, train or other installation directory by package type;
6. record it in `packages.xml` and write its file list;
7. uninstall later through `Manipulation.UninstallPackage`, which also cleans up empty folders.

The dialogs use upstream's wording. Archives can come from the file finder or from the system
file picker; a picked document is copied into the cache first, because the archive code needs
a seekable file.

**Plain archives** (no `package.xml`) are most BVE content, and the Windows installer rejects
them because users unpack them by hand. There is no Explorer for that on a phone, so they are
accepted too, and placed by their layout:

- `Railway/...` goes into the Railway folder, and top-level `Route`/`Object`/`Sound` folders
  also count as Railway content;
- `Train/...` goes into the Train folder;
- a folder holding `train.dat` goes into the Train folder, wrapped in a folder named after the
  archive if `train.dat` is at the root;
- bare route files get their own folder under `Railway/Route`;
- anything else goes to Other;
- a single wrapper folder around everything is stripped.

Entries that would escape the installation folders are refused. A made-up package entry goes
into the same database and file list, so uninstalling works the same way for both kinds.

**File-name encoding of plain archives.** Zip files made on Windows without the UTF-8 flag store
names in the maker's ANSI code page, with nothing to say which one. Decoding them:

- The raw name bytes are checked in order: valid UTF-8; then the text detector, but only if it is
  confident and names a multi-byte encoding (it calls a few Big5 bytes Latin-1); then the code
  pages the app language and device language suggest (Big5 for zh-HK/TW, GBK for zh-CN,
  Shift-JIS, EUC-KR); then the rest. The first that decodes every name cleanly wins.
- A dropdown overrides the choice, as the Windows Start page's encoding list does, with a preview
  of a few names.
- Zips are read with .NET's `System.IO.Compression`, which honours an explicit name encoding.
  SharpCompress 0.50 decodes unflagged names as CP437 whatever `ArchiveEncoding` says.
- Other formats (7z, RAR, tar) go through SharpCompress; 7z and RAR5 store Unicode names anyway.

Verified on the emulator:

- A Big5-named plain train zip (built with .NET's `ZipArchive`, no UTF-8 flag, like Windows' own
  zip tool) was detected as Big5, installed as `Train/測試列車`, listed at once in the Start page's
  trains, and uninstalled cleanly.
- A route package with `package.xml` installed to `Railway/Route/PackageTest`; reinstalling it
  gave upstream's "same version" dialog.
- Picking the MTR route previewed it with its Big5 description, its image and the suggested
  train. Start ran it in the game process, and a Vulkan choice made in Options applied to that
  game.

Test archives and the scripts that make them are in `android/testcontent/archives`.

**Options** covers what applies on a phone, under the Windows headings: language, interpolation
mode, viewing distance, number of sounds, the installation folders, storage access, and
**Advanced Options → Graphics backend** (OpenGL ES / Vulkan (ANGLE)). The dropdown marks Vulkan
unavailable when ANGLE can't be loaded or the device reports no Vulkan, and says the change
applies from the next game. Every change is saved immediately.

**Not yet:** the route map and gradient profile (upstream draws them with System.Drawing into
the Start tab), per-route encoding overrides on the Start page, the Review and Controls
sections, package creation, and translations for the Android-only labels and the in-game
control buttons.

### Driving controls

`ControlOverlay` puts plain Android views over the cab view: power ▲/▼ and brake ▲/▼ at the
bottom right, the emergency brake top right, reverser, horns and doors bottom left, the driving
information in a bar at the bottom between them, and the in-game messages on a card at the top. Plain views rather than GL: they get platform touch handling and
multi-touch for free and cost the renderer nothing. Every button reports press *and* release,
because upstream's controls use both (held notching, the music horn, door buttons that latch
until released).

Touches arrive on the UI thread; `AndroidControls` queues them and applies them on the render
thread at the start of each frame, as a port of the train-control part of upstream's
`MainLoop.ProcessDigitalControl`: safety systems, traction components, then
`CabHandles.ControlDown/ControlUp` — which holds all of upstream's handle logic (single handle, air
brake, hold brake) so none of it is duplicated — then the safety plugin's key for security
commands, then horns and doors. A plugin's `BlockingInput` blocks driver input, as upstream. On a
single-handle train the power and brake buttons send `SinglePower`/`SingleBrake`.

Verified on the emulator with the MTR train: brake ▼ ×6 released the brake, power ▲ ×2 gave P2
and the train moved off; emergency cut power and applied brake 8; doors left opened (state 1.0)
once stopped.

`DemoDriver` remains for unattended tests (`--ez autodrive true` on `am start`): it releases the
emergency brake, selects forward, and holds a cruising speed with half power and the service brake.

Since then: a **unified lever** (POWER▲ / N / BRAKE▼, the `Single*` commands) replaces the two
lever columns for single-handle trains; **CAM** opens a camera numpad laid out like the desktop's
keypad keys, placed beside whichever lever set shows; a release arriving in the same frame as its
press is held to the next frame so plugins that poll key state see taps; and every horn is stopped
on release as upstream does (which is what resets a play-once horn). The information line also
shows the next stop and its distance (upstream's HUD rule).

The activity is full screen and immersive; the system bars come back with a swipe from the edge.

### Interface styles: touch and desktop

Options → Advanced → *In-game interface* picks **Automatic**, **Touch** or **Desktop**
(`AndroidSettings.UseDesktopInterface`). Automatic is Desktop only when the app runs as a desktop:
Samsung DeX (`Configuration.semDesktopModeEnabled`, read by reflection) or a display Android
reports as a desk dock (`UiMode.TypeDesk`). The choice is made when a game starts.

- **Touch:** `ControlOverlay` is shown; upstream's HUD elements are cleared (`HUD.CurrentHudElements`
  empty) and the overlay carries what the HUD would say. `DriverInfoBar`, at the bottom in the
  gap between the left and right buttons (wherever they, the numpad or an expanded card leave
  it, `ControlOverlay.PlaceHud`): the speed (red above the limit), the limit as a round sign
  (red at a signal at danger), the notches (from the train's own descriptions) in a chip coloured
  neutral/power/brake/emergency, the reverser, the clock, the next stop with its distance and
  timetabled arrival/departure, and - only while it happens - an amber strip saying what the
  safety system is doing to the handles. Up to three in-game messages
  (`MessageManager.TextualMessages`) go on a dark card under the top row, each in its route-given
  colour, with the camera view's name for two seconds after it changes. The data comes from
  `AndroidTrainSession.Status()` (a `DriverStatus` record) five times a second. **TT** opens the
  timetable card (below) instead of upstream's texture, and the route's custom timetable is not
  opened at the start.
- **Desktop:** no touch controls; upstream's HUD, messages, timetable texture (Ctrl+T) and route
  map (F8/F9) are drawn by the linked `Overlays` classes, and the game is driven from the keyboard
  or a controller.

### TPWS (OS_Ats1)

The R-Train's `OS_Ats1.dll` (Oskari Saarekas, 2004; `system=3`) implements TPWS as a **train stop
only** (`_tpwsTrainstop`, `_tpwsRelease`, a startup test; no overspeed sensor). Its beacon is
**44003**: passed at a red signal it lights `tpwsindicator` (51) and applies the emergency brake;
at a stand `tpwsresetkey` (0 = S) releases it, and `tpwsoverridekey` (3 = B1) lights
`tpwsindicator2` (52) and lets the next red be passed once. Verified in `OpenBve.X86.Tests tpws`
(types 44000–44010 scanned, pairs for an OSS, override-first) and on the phone through
DetailManager on a test copy of EAL2023 (`android/testcontent/tpws/README.md`). Beacons must name
section 1 (the signal ahead): for the section it is in, a train is sent the aspect that section
would show without it (upstream's `GetPluginSignal`).

Found on the way: `AndroidHost` declared its own `Trains` list, hiding `HostInterface.Trains`, so
upstream code asking the host for trains got null - a station holding its departure signal red
crashed the game at start. It is now `TrainList`, with `Trains` a real override. The audit of
upstream `Host` overrides also added `AddScore`, `AddMarker`/`RemoveMarker`,
`AddObjectForCustomTimeTable` and `CameraAtWorldEnd`; still absent (no effect on this port yet):
`AddBlackBoxEntry`, `PlayMicSound`, `AddTrain`.

### Backend frame rates (2026-09-27)

| Scene | Phone | OpenGL ES | Vulkan (ANGLE) |
|---|---|---|---|
| KCR Lo Wu, cab, standing | A73 | 78 fps | 72 fps |
| KCR Lo Wu, cab, standing | S24 Ultra (120 Hz screen / 60 Hz DeX) | capped | capped |
| MTR EAL, R-Train on ATO, exterior, leaving Lo Wu | A73 | 11.5 → 5.8 fps | 13.6 → 9.3 fps |
| MTR EAL, R-Train on ATO, exterior, leaving Lo Wu | S24 Ultra (phone screen) | 19.0 → 11.4 fps | 18.5 → 12.2 fps |
| MTR Tuen Ma Line (HKRSC, TMLDN 20144), SP1900, from the SD card (2026-09-29) | A73 | cab 10–15 fps, exterior 2–5 fps; load ~10 min | not measured |

Light scenes favour the system GL driver slightly (ANGLE's translation costs CPU); the heavy,
transparency-laden exterior view favours ANGLE's Vulkan path, markedly on the A73's Adreno 642L
(alpha faces 38 ms against 106 ms). Both remain CPU-bound on draw calls in dense scenes. The MTR
run is scripted (`mtr_ato_bench.sh` in the session scratchpad, reproducible from the steps:
close the open doors, Q ×10, Delete ×5, wait for departure, Space, F2).

The Tuen Ma Line pack is the heavy end: about 31,000 object files (4.3 GB) across three object
folders, a 10-car train with a 3D cab, scripted trains. It loads, and runs, but at those rates it
is not playable on a mid-range phone; version 1.0 ships with that as a known limitation. Its
load time includes reading ~31,000 small files through Android's FUSE layer from an exFAT SD card
(the push alone ran at 3.6–10 MB/s). The work for heavy routes: per-object draw calls (batching
static geometry), the alpha pass, texture decode during loading, and file access on the card.

### Beta data log

A beta build (`-p:BetaLog=true`, defining `BETA_LOG`; `release.ps1 -Beta`) writes one plain-text
file per process - `OpenBVE_beta_<date>_<time>_game.txt` (and `_menu.txt`) - to
**Documents/OpenBVE**, the same folder as the content: the SD card's, or the phone's. With
all-files access it is written directly; without, through MediaStore (no permission from
Android 10, SD card volume preferred); failing both, the app's own folder, which the log names.
`BetaLog.cs` lays it out to be read: a header of labelled sections - session (start, time zone,
file), app version, device (model, chip, CPU types, Android version/build, RAM total/free, screen
size/dpi/refresh rate, content storage free, battery, thermal state, app/system language),
settings (graphics backend, interface style, viewing distance, sound sources, texture filtering,
touch-button options), external input devices by kind and USB vendor/product number - then a
time-stamped log. At game start a Game section (route and train with encodings, safety plugin,
backend chosen and in use with the GL driver string, game mode, load time, content error count,
load summary); every 30 s a frame-rate entry (average, worst second, longest frame, frames over
50 ms, position/speed/view/size, the frame's cost by phase, scene size and draw calls, memory,
thermal state); game-mode switches; every warning and error marked (the game's, other libraries',
crash traces - also caught through the unhandled-exception handlers and the render loop's crash
path - and load failures); a session summary whenever the game ends or leaves the screen. The
game's lines come from the process's own logcat (`OpenBVE:I *:W`); its 5-second diagnostics are
folded into the 30-second entries, and known Android/.NET boilerplate warnings are dropped.
Nothing personal: no location, accounts, contacts, phone number, serial or advertising IDs,
installed apps, or names of paired devices (the game's own "input device added" line logs kind
and vendor/product instead of the name). In a normal build every call compiles to nothing.

### Touch button size, opacity and hiding

Options → Touch button size (small 80 %, normal, large 125 %) scales every measurement of the
touch overlay - all go through `ControlOverlay.Dp`, so the timetable card's margins keep in step
with the button rows - and the button text; Touch button opacity (solid, translucent 60 %, faint
30 %) fades the buttons, a pressed one brightening rather than dimming when faded. The size is
capped at what the screen fits (`ControlOverlay.FittingScale`: about 680 × 370 dp needed at
scale 1), so Large is 1.11× on a Galaxy A73 (914 × 411 dp) and a small screen shrinks Normal;
the top row's buttons are a little smaller than the rest, to stay clear of the emergency brake. The pause
button never goes below 75 %. The pause menu's "Hide touch buttons" leaves only pause, the
information line and the card; the choice is kept (`AndroidSettings.GetTouchButtonsHidden`) and
the same item shows them again. For tests: `--ei touch_button_size 0|1|2`,
`--ei touch_button_opacity 0|1|2`, `--ez touch_buttons_hidden true|false`.

### The port's own labels in other languages

Labels only the port has (the "android" group of `Menu.T`) are not in upstream's .xlf files, which
stay as upstream ships them. `AndroidStrings` reads them from `Data/Languages/android/<code>.txt`
(from `OpenBve.Android/Languages`, packaged beside the .xlf files; upstream's loader reads only
the folder's top level), one `key = text` per line, `
` for a line break and `{0}` where the game
fills in a value (the timetable's "in {0}", "late {0}", so a language may put the time first). A
language without its own file borrows one of the same language (de-CH takes de-DE, pt-PT pt-BR);
a missing key falls back to the English in the code. `en-US.txt` lists every key for translators.
Drafts exist for zh-HK, zh-TW, zh-CN, ja-JP, ko-KR, de-DE, fr-FR, es-ES, it-IT, nl-NL, pt-BR, ru-RU
and pl-PL; they want checking by native speakers. When adding a label, add its key to en-US.txt.

### App icon

The classic (older generation) OpenBVE logo, `android/artwork/openbve_logo_classic.png`, made
into launcher icons by `android/artwork/make_icons.ps1` (Windows' own System.Drawing, nothing to
install). The logo has its own rounded frame, which a launcher's mask would show as a second
outline inside its shape, so from Android 8 the adaptive icon (`mipmap-anydpi-v26/ic_launcher.xml`)
is a single full-bleed layer: the artwork without the frame in the 66 dp safe zone, carried on to
the layer's edges with its nearest edge colours, and the launcher's circle or squircle gives the
rounded edge. Android 7 gets the logo as it is, frame and transparent corners included.

### Controls page

Main menu → Controls lists every command with its keyboard keys or controller buttons (tabs);
selecting one waits for a key (with modifiers) or button to add, Clear removes them, and Reset
restores the defaults. Only digital commands are listed: the analog axis commands belong to
sticks and levers. Keyboard bindings are upstream's, in `Settings/1.5.0/controls.cfg` in its
own format (key names spelt as `OpenBveApi.Input.Key`); controller buttons, which upstream binds
per device and axis, are in `Settings/android/pad.cfg` (`COMMAND, pad, ButtonA`), the built-in
layout being the default (`ControlBindings`). `KeyboardInput` builds its tables from the same
model; changes apply from the next game. From the pause menu, Customize controls opens the same page over the game with a Back
button; leaving it (the button, Back, Escape or B) makes the game read the bindings again, so the
changes apply at once (`PauseMenu.ControlsClosed`).

### Desktop modes (Samsung DeX and others) and live switching

`AndroidSettings.IsDesktopMode` decides Automatic: the game's **window on a display other than the
built-in one** (Samsung DeX - One UI 7 runs it on a virtual "Desktop" display -, Motorola Ready
For, Huawei/Honor PC mode, Android's connected-display desktop), else desk UI mode or Samsung's
`semDesktopModeEnabled` - but on the built-in screen only if it is tablet-sized (sw ≥ 600 dp):
after a window leaves DeX, Samsung keeps reporting desk mode and the DeX flag for a while. The
window's own display (`DecorView.Display`) is asked first, as the activity's lags behind a move.
Every decision is logged with its signals ("interface: …", "re-check: …").

The game activity handles display, size, density, UI-mode and keyboard changes itself, so moving
between the phone and a monitor never recreates it. On a move:
- the SurfaceView's surface is destroyed and recreated; the render thread **detaches the EGL window
  surface, keeps the context** (route, train, textures, sounds), waits, and attaches the context
  to the new window (`IRenderSurface.DetachWindow/AttachWindow`, EGL and ANGLE). Sounds pause and
  simulated time stands still meanwhile. This also means going to the background and back no
  longer reloads the game;
- aspect ratio, field of view and the overlay scale follow the new surface size and density;
- the interface is re-evaluated on each configuration change, 1.5 s later, when the window comes
  back, and when a display is added or removed (Samsung's reports settle late); switching loads
  or clears upstream's HUD on the render thread and shows or hides the touch controls;
- the touch controls, pause menu (even while open - the move pauses the game) and status panel
  are rebuilt for the new density, as Android views are sized in pixels when built.

Verified on a Galaxy S24 Ultra (One UI 7, HDMI monitor, USB keyboard and mouse, ADB over
Ethernet): desktop interface chosen automatically in DeX, HUD and keyboard fine, window resize,
and DeX ⇄ phone switching in about a second with the train kept where it was.

### Cab touch areas, mouse, and the scene

The cab's touch areas (panel.xml / panel.animated touch elements, and panel2.cfg handle indicators
in upstream's "panel2 extended mode", which Options → *Touch the cab panel's handles* turns on
and which defaults to on here) work by tap in the touch interface and by mouse click or touch in
the desktop one. Upstream picks them by drawing them into an R32F texture and reading the pixel
back, which GLES 3 does not guarantee; `CabTouch` instead projects the areas each frame with the
matrices the cab was drawn with (captured in `AndroidRenderer.RenderCab`) and hit-tests on the UI
thread, so a touch is known at once to be on a control or to turn the camera. Press and release
follow upstream's `TouchCheck`/`LeaveCheck`: commands down and up (indices into the controls
`LoadTrain` returns, kept as `AndroidTrainSession.TouchControls`), the plugin's `TouchEvent`,
panel-screen jumps and touch sounds. A fingertip counts within 10 dp of an area, a mouse within
2 px. Areas and their screen bounds are logged ("cab touch area …").

`SceneInput` holds the scene gestures for both interfaces: the touch overlay passes it the touches
its buttons miss, and the game view uses it in the desktop interface (drag to look around, two
fingers to move, pinch or the mouse wheel to zoom). Verified on the A73 with a test panel
(`android/testcontent/touch`: two large digital-number handle indicators added to the KCR
panel2.cfg): taps in the touch interface notched the brake up, taps in the desktop interface
released the brake and added power. The overlay's buttons still take precedence where they cover
a touch area.

Also: Ctrl+Up / Ctrl+Down scroll the desktop timetable (upstream's TIMETABLE_UP/DOWN, 250 px/s,
clamped to the picture's height on the overlays' virtual screen). Verified with
`testcontent/Railway/Route/Timetable50.csv` (50 stations, generated by
`testcontent/make_timetable50.py` from the Uchibo objects, driven with `Train/AndroidTest`): the
timetable scrolls until Station 50 reaches the bottom and stops there. The same route shows the
touch gradient chart's speed-limit line.

In DeX (S24 Ultra, mouse and keyboard) the mouse works the cab touch areas, left-drag looks
around and the wheel zooms; the pause menu's text is right after a move to the phone. The game
declares a 1280 x 720 dp default size for desktop windows (`[Layout]`); DeX reopened it
maximised, having remembered the player's last size.

### Route card: timetable, map and gradient (touch interface)

`TimetablePanel` is a native card top left, with three tabs; **TT** opens it at Timetable and
**MAP** at Map. Collapsed it reaches down to the reverser/horn/door buttons (the ATS keys move right
of it while it is open); ▼ expands it over those too. The **Map** and **Gradient** tabs are drawn
natively from the track (`RouteViews.cs`, data from `AndroidTrainSession.GetRouteGeometry` /
`GetRouteLive`), not upstream's 500 px pictures: the map is the track polyline (travelled part in
the accent colour) with station dots and name chips placed where they fit, every train, a scale
bar, pinch zoom and drag, ◎ to follow the train and ALL for the whole line; the gradient chart
shows a window of track (¼ behind, ¾ ahead, pinch to change 0.5–60 km) with the filled profile,
per-mille labels per constant-pitch stretch, stations, speed limits as a step line where the
route has `.Limit`s, and the train with its gradient and height. The desktop interface keeps
upstream's `RouteInfoOverlay` (F8), enlarged with the rest of the overlays.

The **Timetable** tab It lists every named, non-dummy station in route order (upstream's generated timetable's
rule) as a timeline: stops made are filled, the next stop is ringed and highlighted (green when
the train is standing there), passing points are small dots marked "pass". Each row has arrival and
departure; a large strip at the top gives the next stop's distance and "in m:ss" / "late m:ss"
against the timetable (or "departs … overdue" while standing). When the current section has a
custom timetable image on disk, ▦ swaps the list for it. The data comes from
`AndroidTrainSession.GetTimetable()` once a second while the card is open; the information line
moves right of the card while it is open.

### Upstream overlays at phone density

Upstream's overlays are laid out in pixels for a 96 dpi monitor. `AndroidRenderer` draws them on a
virtual screen `OverlayScale` times smaller than the real one (the orthographic projection comes
from `Screen.Width/Height`, so it stretches everything uniformly): `OverlayScale` is the display
density, capped so the virtual screen keeps 600 px of height (≈1.8 on the test phone, ≈1–1.5 on a
DeX monitor). DPI-scaling only the fonts, as upstream does on Windows, left text spilling out of
the HUD's boxes.

Two compat fixes came out of this: `GL.Color4` is now a no-op instead of throwing (the route map
and marker overlays call it before a shader-coloured `Rectangle.Draw` when the options don't claim
a forwards-compatible context, and claiming one would change greyscale texture uploads), and a
compat `Bitmap` now flushes its open `Graphics` before its pixels are read (the working-copy design
meant upstream's illustrations, which never dispose their `Graphics`, came out blank).

### Keyboard and controllers

`KeyboardInput` reads upstream's bindings: the player's `Settings/1.5.0/controls.cfg` if present,
else `Data/Controls/Default.controls`, keyed by OpenTK key name and modifier (Shift 1, Ctrl 2,
Alt 4). Android key codes are mapped to those names; OpenTK's aliases (LBracket, KeypadAdd, …)
are normalised. A key sends every command bound to it, *exactly* (the on-screen levers' mapping of
power/brake to `Single*` is skipped, since the bindings carry both sets and `CabHandles` ignores
the set that does not apply), and releases them all when it comes up; auto-repeat is ignored.
Escape/Pause/Ctrl+Q open the pause menu, Ctrl+T is the timetable (card or texture by interface),
and MISC_AI (Ctrl+A) puts upstream's `SimpleHumanDriverAI` on the player train, as upstream.

In the touch interface an **AI** button above the safety keys sends the same MISC_AI command and
turns green ("AI ●") while the AI drives. Verified 2026-09-27 with a Logitech USB keyboard on the
phone (through a USB hub, ADB over Wi-Fi): every binding tried sent its command.

Controllers have a fixed layout (upstream ships no joystick bindings): R2/L2 notch power/brake up,
R1/L1 back (`SingleNeutral` on a single handle), R3 emergency, A = ATS S, B = horn, X/Y = doors
left/right, D-pad up/down = reverser, left/right = previous/next view point, right stick looks
around, left stick moves the camera, L3 = cab view, Start = pause, Select = timetable. Hats and
analog triggers are turned into the same presses.

Tested 2026-09-27 with a wired Xbox 360 pad (045E:028E: D-pad as hat axes, triggers as axes) on
the phone through an unpowered USB hub: triggers, bumpers, stick clicks and the D-pad reverser all
sent their commands. The pad dropped off the bus more than once - the phone was sourcing up to
1.9 A with its voltage sagging - which exposed two faults, both fixed: a stick released from just
past the dead zone could leave a tiny strength held (the "skip small changes" filter swallowed the
return to zero), and a device unplugged mid-press never sent its releases (the activity now
listens for removed input devices and releases everything held). Controllers want a powered hub.
Over Bluetooth an 8BitDo Pro 2 (same layout: hat D-pad, analog triggers) worked throughout: every
button sent its command, including A (= S) into the KCR TBL plugin, and the TBL start was driven
from the pad (D from the touch ATS row). B in the pause menu resumes (Android turns an unhandled
B into Back); held buttons' auto-repeat is ignored as intended.

**Menus by controller or keyboard.** Android's key map already turns a pad's D-pad into focus
moves, A (and Start) into DPAD_CENTER and B into Back, so the menus only needed visible focus:
`Ui.EnableFocusRing` (main menu and game) outlines the focused view - or, for a list, its chosen
row via the list's selector - and only outside touch mode, so touch use never shows it. The pause
menu focuses its first item (or the suggested station) on every page, and when Start or Escape
opens it, takes focus explicitly (those keys, unlike the D-pad, do not leave touch mode); its
backdrop and the start-up summary panel no longer take focus. Verified on the phone with gamepad
key events: main menu → route list → A selects a route; Start → pause menu → D-pad → A into the
station list (suggested station focused) → B, B back to the game.

The test train (`android/testcontent/Train/AndroidTest`) is a two-car EMU defined only by
`train.dat` — physics, no exterior, panel or sounds. **Its `train.dat` carries no comments:** the
parser strips `;` comments but still reads each section's fields by line position, so a comment
line shifts every value after it. That silently produced a driver car index of 9000 and an
invalid notch count on the first attempt; the field layout is documented in a README beside it.

On the emulator the test train starts at the platform (775 m), reaches 55 km/h in about a minute
and holds it, and the signalling section advances 2 → 3 → 4 as it passes the section boundaries.
The whole simulation step costs about 0.5–0.7 ms per frame.

### Animation functions

Every animated element — door leaves, cab needles and displays, signal aspects, level crossings —
is driven by function scripts, and `FunctionScript.ExecuteScript` does not evaluate them itself:
it calls `HostInterface.ExecuteFunctionScript`, whose base implementation is empty. The desktop
host overrides it with `OpenBVE/Game/ObjectManager/AnimatedObjects/FunctionScripts.cs`. Until
`AndroidHost` did the same, every function stayed at zero and nothing animated moved.

That evaluator is compiled into the app unchanged (linked in `OpenBve.Android.csproj`), against
`UpstreamFacade.cs`: an `OpenBve.Program` holding the host, renderer, route and sounds, and an
abstract `OpenBve.TrainManager : TrainManagerBase` so its `TrainManager.PlayerTrain` resolves to
the shared static. `Program.CurrentRoute` is set before the route is parsed, because signals are
first evaluated during the parse and a function that throws once is disabled for good. Inside
namespace `OpenBve`, `TrainManager` now names that class, as on desktop, so Android code that
needs the namespace in a qualified name writes `global::TrainManager.…`.

### Texture streaming

Upstream decodes an image the first time a face needs it, on the render thread, and waits. A
PNG takes ~13 ms to decode on the test phone, so a view bringing a few hundred new textures at
once (the whole train from outside) froze the simulation for several seconds. Once driving has
started, `AndroidHost.LoadTexture` now decodes such textures on worker threads while their faces
draw untextured for a moment, and uploads them within a time budget (6 ms per frame). The worker
also does the per-pixel work TextureManager would otherwise do at upload time: the transparency
scan (cached on the texture) and, for opaque images, the RGBA→RGB copy (TextureManager's RGB
branch uploads the same pixels; it sets the unpack alignment explicitly either way).

At most 16 textures are decoding or waiting at once: without that cap, all of them decoded at
once and waited in memory, and resident memory went from 550 MB to 1.15 GB. With it, the
exterior view fills in within about 5 s with memory under 700 MB. Loading is unchanged (all
synchronous, so the first frame is complete), and the cab layer is never streamed — its
displays switch between many small images, which would flash blank while decoding.

### Load errors on the MTR route

The route reports ~1,100 errors at load. Checked against the archives, none come from the port:
330 name an object folder `mtrnsl` that `Route.zip` does not contain at all (station fittings,
platform screen doors, PIDS), most of the rest are knock-on "object not loaded" errors from the
same missing objects, and the remainder are files absent from the archive at the referenced
path (a Windows install would report them too). Only four names exist in a different letter
case, and all four are in other folders or inside `mtrnsl`.

### Camera and view modes

`AndroidCamera` is a port of upstream's `World.UpdateAbsoluteCamera` and
`InitializeCameraRestriction`, the camera cases of `MainLoop.ProcessDigitalControl` /
`ProcessAnalogControl`, `MainLoop.SaveCameraSettings` / `RestoreCameraSettings`, and the camera
part of `GameWindow.OnRenderFrame`. All five view modes work — cab, cab with look-ahead, exterior,
track and the two fly-bys — with look-ahead, driver body and head movement, the cab's own view
direction, the camera restriction box, per-mode saved alignments, points of interest and the
glide between cars in the exterior view.

One omission: the animated transition *between* interior and exterior (upstream's
`ModeTransitionTimer`), which replaces the camera's own motion for its duration and reads as lag
on a touch screen. `ModeTransitionTimer` is left at 1.0, which is upstream's "no transition" state.

Upstream drives the camera through digital commands (view modes, reset, points of interest) and
analog ones (move, rotate, zoom) whose direction is re-applied every frame while held and cleared
after each frame. The overlay keeps that shape: `VIEW`, `CAB`, `◀`/`▶` and `RESET` are digital
commands, while dragging one finger on the scene holds `CameraRotate*`, two fingers hold
`CameraMove*` and pinching holds `CameraZoomIn`/`Out`, each with a strength that grows with the
distance dragged, exactly as a joystick axis would supply it.

### The pause menu

`PauseMenu` carries the items of upstream's `Menu.SingleMenu` `MenuType.Top`: resume, jump to
station, exit to the main menu, customise controls, quit — with the same confirmation questions,
the same translated strings, and the station list built the same way (stations where the player
train stops, the first one after the last stop preselected). Jumping ports upstream's chain:
`TrainBase.Jump`, `TrainManagerBase.JumpTFO`, and `AndroidHost.ProcessJump` for
`ObjectManager.ProcessJump` (resetting track-following objects and re-railing the train).

Back opens it and steps back through it, as Escape does upstream; leaving the app pauses too.
While it is open the simulation is not stepped, sounds are paused (`AndroidSounds.SetPaused`), and
the scene is still drawn so the camera can be moved around a standing train.

Customising controls is the one item that cannot be ported as-is: there are no keys to rebind, so
it lists what each on-screen control does instead.

### Safety systems and the driver

The overlay carries the ATS keys (`S`, `A1`, `B1`, and `A2`–`G` behind `ATS…`). They are not
optional: OpenBVE's default plugin is ATS-Sx, whose alarm rings at a signal and applies the
emergency brake five seconds later if it is not acknowledged, and holds it until `B1` resets it.
Without those keys a train fitted with the default plugin stops soon after leaving a platform and
can never be released — with the simulation, the buttons and the clock all still running, which
looks exactly like a hang and was reported as one.

For the same reason the driving-information line now names what the safety system is doing when
it is not what the driver asked for: `ATS EMERGENCY BRAKE`, `ATS BRAKING`, `ATS POWER CUT` or
`POWER CUT: DOORS OPEN`. On desktop the cab panel shows this; few trains' panels are legible on a
phone, and none are in the exterior views.

### Win32 train plugins: the x86 compatibility layer

Upstream runs a train's Win32 (legacy BVE) ATS plugin in-process on 32-bit Windows or through a
WCF proxy to a Windows `.exe`. Neither exists here. `OpenBve.X86` runs the plugin DLL itself, in
an emulator, frame by frame — every call the train makes (Elapse, keys, handles, doors, horn,
signals, beacons) executes the plugin's own x86 code, so its signalling and cab indices behave
exactly as on Windows. Nothing is translated or converted ahead of time.

- **CPU** (`Cpu*.cs`): an IA-32 interpreter — integer instructions, x87 (as doubles), the SSE/SSE2
  subset compilers emit (scalar and packed float, integer logic, shifts, shuffles, PEXTRW/PINSRW),
  16-bit addressing for MSVC's SEH prologue. ~2,000 instructions per frame for a typical plugin
  stack (0.2–0.3 ms on the phone).
- **Process** (`Win32Process.cs`): paged 32-bit address space, PE loader (relocations, imports,
  exports, DllMain, TLS), heap, and a file system confined to one folder presented as `C:\` — the
  folder above the train's (normally the one holding `Train`), matched case-insensitively.
- **Windows and C runtime** (`Kernel32.cs`, `Msvcrt.cs`, `Win32Extras.cs`): the kernel32, msvcrt,
  UCRT (`api-ms-win-crt-*`), vcruntime140/msvcp140, user32, advapi32 and winmm functions plugins
  import, including INI files (read and write, with Windows' trimming), atoms (DetailManager
  shares memory through them), locales (a table of the locales BVE content is written for:
  English, Japanese, Chinese, Korean, Indonesian, German, French, Polish), date formatting and
  the start-up/shut-down tables of VS2015+ runtimes. An import that is not provided is bound to a
  stub that stops *only that plugin*, with the function's name, if it is ever called.
- **ATS ABI** (`AtsPlugin.cs`): the BVE ATS exports, `__stdcall`, structures by value, Elapse's
  hidden return pointer. `EmulatedWin32Plugin` sits where upstream's `Win32Plugin` does and
  mirrors it (version check, spec, sound instructions).
- **Code page.** The emulated Windows' ANSI code page matters: plugins convert their own paths and
  settings with it, and one written on Japanese Windows assumes 932. `NativePluginBridge.AnsiEncoding`
  keeps a legacy train encoding; for UTF-8/UTF-16 (the menu reads the train's `train.txt`, often
  UTF-16) it lets the train's own legacy-encoded text files vote, falling back to UTF-8.

`NativePluginBridge` prefers the layer and falls back to the managed translations
(`DetailManager`/`OS_Ats1` re-implementations, else an inert pass-through) only if emulation fails.

**Tools** (`OpenBve.X86.Tests`, a desktop console app): `survey <folder>` runs every plugin named by
an `ats.cfg` under a folder for a simulated minute; `calls` prints every Windows call a plugin
makes; `--ez plugin_trace true` on the phone records every plugin call to `plugin-trace.bin`, and
`replay` runs it on the desktop, reports any frame whose outputs differ (a determinism check of the
layer across ARM and x64: 20,580 frames, 0 differences) and can continue from any recorded frame
with scripted inputs — which is how the SelTrac start procedure was found.

**Compatibility survey** (the user's library, 482 trains with `ats.cfg`, distinct plugin files):

| Result | Count | Notes |
|---|---|---|
| Runs | 38 | Japan 16 (ATS-Sn/P/Ps/ATC/TASC, JR West swp2, Tokyu, Tokyo Metro...), Hong Kong 4 (TBL, SelTrac ×2, LRV), UK 2 + OS_Ats1 ×3 (TPWS/AWS), Indonesia 2, AI-train folder 8, others 3 |
| .NET plugins | 16 | Not the layer's job: upstream's own loader, if they avoid Windows-only APIs |
| Would fail on Windows too | 3 | a corrupt DLL; a plugin whose own INI is missing; one that looks for its settings beside the host `.exe` |
| Missing plugin file | 45 | `ats.cfg` names a file the train does not contain |

So: not *any* plugin — 64-bit native plugins and plugins needing Windows GUI, DirectX, sockets or
threads are out of reach, and a plugin that throws a C++ exception it expects to catch will stop
(Windows structured exception handling is not emulated) — but every 32-bit plugin in this
library that works on Windows works here.

### Track following objects (scripted trains)

`TrackFollowingObjectParser` ports upstream's parser of the same name (the desktop app's
`Parsers/Script`), with its `Program.*` and `Loading.CurrentTrainFolder` references taken from
the host; the host now implements `ParseTrackFollowingObject` with it. The route plugin calls it
while loading and adds each train to the train manager's `TFOs`, then the session initialises
them alongside the player train, as upstream's `GameWindow` does. They run on upstream's own
`ScriptedTrain` and `TrackFollowingObjectAI`, unchanged: appear at their time and position, run
their stops and points, and leave.

- The `RunInterval` / `PreTrain` kind is read too: a preceding train on upstream's
  `SimpleHumanDriverAI`, moved into the train list by the session (see *Preceding trains*).
- **Deliberate difference:** upstream keeps the train under construction in a static field that
  is never reset, so a file with neither a `Definition` nor a `RunInterval` silently reuses the
  previous file's train. The port starts empty each time and reports such a file.
- Train folders are matched case-insensitively by upstream's path helpers, which this content
  needs: its TFO files name `...\Train\KTT` for a folder called `Ktt`.

On the MTR route 16 scripted trains load (the route selects 16 of the 47 files) and appear on
schedule. They cost simulation time even before they appear, because upstream positions every
TFO's cars each frame: the final world-coordinate pass is now run one train per task with
`Parallel.For`, as upstream does, which took the train step from ~7 ms to ~4 ms on the emulator.
`UpdateTrainObjects` adds ~4 ms more, also as upstream.

### Preceding trains

Upstream's desktop AI files (`Game/AI/AI.cs`, `AI.SimpleHuman.cs`, `AI.PreTrain.cs`) are compiled
unchanged against `UpstreamFacade.cs` (`Program.TrainManager`, `Interface.CurrentOptions`).
`AndroidTrainSession` sets the traffic up as upstream's `Loading` and `GameWindow` do:
`Train.RunInterval` trains (copies of the player's train, `SimpleHumanDriverAI`), RunInterval /
PreTrain track-following objects, and `.PreTrain` as one invisible `BogusPretrainAI` train (upstream
loads one per instruction; all follow the same positions). Placement, the start-time fast-forward
(`AndroidHost.SimulationState` reports `MinimalisticSimulation` meanwhile, so trains wait for
their timetable and collisions are off) and per-frame train/buffer collisions and disposal
(`AndroidTrainManager.UpdateTrains`) follow upstream. The fast-forward skips the world-coordinate
pass, which nothing needs until the first frame.

#### A probable upstream bug

`ClosestTrain(Vector3)`, which signals and animated objects use to find the train they react to,
measures distance in the X/Y plane. OpenBVE's world space has Y up and Z along the track, so this
ignores separation along the track entirely. The Android host ports it faithfully so behaviour
matches; it rarely matters with a single player train, but is worth reporting upstream.

### Transparency

Both the world and the cab draw transparent faces through `AndroidRenderer.DrawAlphaFaces`, in the
options' transparency mode as upstream: in *quality* mode (the default here as on desktop) the
fully opaque texels of partly transparent faces are drawn first with depth writes, then the rest
blended. The world pass once had only the performance branch; with quality mode's classification
that let trees sorted after a carriage's (colour-keyed) interior draw over it in the exterior view.

### Performance

`FrameProfile` charges each phase of the frame to a name, and the render loop logs the breakdown
every five seconds. On the emulator, with ~200 visible faces of the test route:

| Phase | Time per frame |
|---|---|
| Camera placement, setup, face lists | ~0.5 ms |
| Background | ~0.2 ms |
| All opaque and alpha faces | ~1.5–2.5 ms |
| **Blocked in `eglSwapBuffers`** | **~26–116 ms** |

OpenBVE's own work is flat at about 2 ms per frame. The measured frame rate varies between
runs — steady at 34–36 fps in some, dipping to 8 fps in others — and the variation sits entirely
in the swap: the emulator's graphics composer service at ~75% of a core, and the emulated device
with 1.9 GB of its 2 GB in use and about 1 GB swapped. **Emulator frame rates are not
meaningful for this port**; real hardware is needed before drawing conclusions.

`AndroidRenderer.DiagnoseGlErrors` enables per-phase `glGetError` checkpoints for tracking down
GL errors. It is off by default, since `glGetError` is itself a synchronous round trip.

With the MTR East Rail route and R-Train (9 cars, 3D cab, ~14,400 static objects, ~710 visible
faces) on the `OpenBVE_Test` AVD, both backends hold **57–60 fps** once running, at about 17 ms
per frame of which OpenBVE's own work is about 15 ms (setup ~5 ms, opaque faces ~5 ms,
simulation ~2.5 ms). The route takes about 100 s to load on the emulator.

### Loading time

`LoadProfile` times every host callback during a load: textures (decode, upload, register),
objects (load, create), sounds and scripted trains. Everything else is charged to the caller
(during a route load, the route parser), and the breakdown is logged as `load profile, route:`,
`scene:` and `train:`. Measured on the MTR route with its 9-car train and 16 scripted trains, on
the `OpenBVE_Test` emulator:

| Step | Launch to driving | Route | Train |
|---|---|---|---|
| Debug build (JIT) | ~230 s | 142 s | 46 s |
| **Release build**: full AOT (`RunAOTCompilation`, not only the profiled startup path) | 135 s | 90 s | 46 s |
| Host caches for texture and sound registration, and for file existence | 115 s | 72 s | 40 s |
| Native PNG decoding (upstream's `gdiplus` option, on by default here) | 92 s | 52 s | 24 s |
| No full decode for the texture format probe | **57 s** | **34 s** | **13 s** |

What each step fixes:

- **AOT.** Loading is long, CPU-bound managed code, and under the JIT it all runs cold. NAudio's
  .NET Framework build references the real `System.Windows.Forms` from WinForms-based playback
  classes the plugins never use; the AOT compiler cannot load those methods, so NAudio alone is
  left to the JIT (`OpenBveExcludeFromAot`, which filters the SDK's `_AndroidAotInputs`). Trimming
  is `partial`: the plugins are found by reflection and must not be trimmed.
- **Registration caches.** Upstream's `TextureManager.RegisterTexture` checks the file exists,
  then scans every registered texture with a case-insensitive compare. That is quadratic, and on
  Android every existence check is a round trip through the shared-storage FUSE layer. The route
  registers 36,000 textures and 173,000 sounds, almost all repeats. The host remembers each
  path's result, matching exactly as upstream's scan does, so it returns the same handles.
- **Native PNG decoding.** The plugin's managed PNG decoder took ~18 ms a texture. Upstream's
  `gdiplus` option sends PNGs through System.Drawing, which here is the Skia shim (native libpng).
- **Format probe.** Upstream's `Texture(path)` constructor decodes the whole image to read its
  `PixelFormat`, discards the pixels, and decodes again when the texture is first drawn; the
  upload uses the second decode's format. With native decoding, the BMP/GIF/JPEG/PNG/TIFF plugin
  always yields RGBA, so the host answers the probe for those formats without decoding. The value
  is the same, so nothing downstream changes. Other formats keep the real probe.

As upstream does, route textures decode when first drawn, and the player train's are preloaded
at the start of play (`TrainBase.PreloadTextures`, called where upstream's GameWindow calls it).

**What remains:** 16 s inside the train plugin for the 16 scripted trains, about 1 s each even
with their objects, textures and sounds counted separately. That is probably vertex-buffer
creation through the emulator's GL translator, and needs measuring on real hardware before
optimising. Object parsing takes 7.7 s and the route parser 3.6 s. The Release APK is 52 MB for
arm64 and x86_64 together and is signed with the debug key; distribution needs a release key.

### Graphics backend: OpenGL ES or Vulkan

The renderer can run on either the device's OpenGL ES driver or on **Vulkan, through ANGLE**.

LibRender2 is written against OpenGL throughout — buffers, VAOs, GLSL shaders, framebuffers,
state — so a native Vulkan renderer would be a second renderer, not a port, and would fork
upstream. ANGLE implements OpenGL ES on top of Vulkan; it is what Android itself uses to run GLES
apps on Vulkan drivers. With the Vulkan backend the renderer issues exactly the same GLES calls,
and ANGLE turns them into Vulkan command buffers.

How it is wired:

- **`GraphicsLibraries.Select`** (`OpenBve.GLES/GraphicsBackend.cs`) runs before the first GL
  call. For Vulkan it loads ANGLE's `libEGL_angle.so` and `libGLESv2_angle.so` and installs a
  `DllImport` resolver on the GLES shim assembly that sends `libGLESv3.so` and `libEGL.so` to
  them. No call site changes. If ANGLE cannot be loaded it falls back to OpenGL ES and logs why.
- **`AngleSurface`** creates the display, context and window surface through native EGL
  (`NativeEgl`), since Android's Java `EGL14` always talks to the system driver. It asks for the
  display with `EGL_ANGLE_platform_angle` and `EGL_PLATFORM_ANGLE_TYPE_VULKAN_ANGLE`, so ANGLE
  uses Vulkan explicitly. The OpenGL path keeps the Java `EglSurface`; both implement
  `IRenderSurface`.
- **The choice is fixed per process.** A P/Invoke binds to its library on first call and stays
  bound. Since each game runs in its own process (see *The main menu*), a change applies from
  the next game.
- **The setting** is the shared preference `graphics_backend` (`AndroidSettings`), chosen in
  **Options → Advanced Options → Graphics backend**. For tests it can also be set from the
  command line; the value persists:

  ```
  adb shell am start -S -n net.openbve/net.openbve.GameActivity --es graphics vulkan
  adb shell am start -S -n net.openbve/net.openbve.GameActivity --es graphics opengl
  ```

- **ANGLE is not shipped: the app uses the device's own.** Android 14+ ships ANGLE in
  `/system/lib64`, but an app's linker namespace may not load from there
  (`permitted_path=/data:/mnt/expand:...`). It may *read* the files, though, and load libraries
  from its own internal storage. So on first use `GraphicsLibraries.EnsurePrivateCopy` copies
  `libEGL_angle.so` and `libGLESv2_angle.so` from the first system folder that has them
  (`/system/lib64`, `/system_ext/lib64`, `/vendor/lib64/egl`, `/vendor/lib64`) into
  `files/angle/` (refreshed when the system's copy changes), and loads them from there —
  `libGLESv2_angle.so` first, because `libEGL_angle.so` names it as a dependency by bare name,
  which the linker can only satisfy from an already-loaded library of that name. Nothing is
  downloaded or redistributed; a device without system ANGLE falls back to OpenGL ES. A build may
  still bundle an ANGLE built from source as `AndroidNativeLibrary` items: the bare library name
  is tried first. Verified on the Galaxy A73: `ANGLE from .../files/angle/libEGL_angle.so (the
  device's own, copied from /system/lib64)`, `Vulkan 1.1.128 (Adreno (TM) 642L)`.

Verified on the emulator: ANGLE reports `Vulkan 1.4.0 (NVIDIA Goldfish GFXStream (NVIDIA GeForce
RTX 2060 SUPER))`, all shaders compile, and the MTR cab view renders identically to the OpenGL ES
path at the same frame rate.

The dropdown marks Vulkan unavailable when ANGLE cannot be loaded for the device's processor
(`GraphicsLibraries.IsAngleAvailable`) or the device reports no Vulkan
(`android.hardware.vulkan.version`). Choosing it anyway still falls back to OpenGL ES at start.
Verified: choosing Vulkan in Options and starting a game from the menu gave `ANGLE (NVIDIA,
Vulkan 1.4.0 ...)` in that game with no app restart.

### Data assets

About 11 MB of upstream's `assets` folder is packaged and extracted once per installed version
(guarded by a `.extracted-<versionCode>` marker): Compatibility, Controls, Cursors, Flags,
In-game, Languages and Menu. A full route load needs `Compatibility/Signals/Japanese.xml` for the
default signal set, even for a route with no signals.

### Known upstream data quirk

Loading any route logs `File null.csv was not found in Key Path in Section Object`. This comes
from `Compatibility/Misc.xml`, which gives `null.csv` a path relative to the XML's own folder
where the file does not exist — it lives in `Compatibility/Misc/`. The resolution is platform
independent, so desktop builds log the same thing; it is not a case-sensitivity issue, and it is
harmless because null objects are placeholders.

## Audio

`android/OpenBve.OpenAL` presents OpenTK 3's `AL`/`Alc` classes and their enums, bound to
OpenAL Soft. Android ships no system OpenAL, and no NDK is installed on this machine to build
one — but **MonoGame.Library.OpenAL** publishes prebuilt `libopenal.so` for all four Android
ABIs (arm, arm64, x64, x86), so the app project references that package and the native library
is packaged into the APK. OpenAL Soft is LGPL and is linked dynamically as a separate `.so`,
which is what that licence expects.

Microphone capture (`AudioCapture`) is **not** implemented: it would need the `RECORD_AUDIO`
permission, and OpenBVE uses it only for the optional microphone-driven horn. Upstream
constructs it inside a try/catch and carries on without a microphone, so throwing disables the
feature cleanly rather than demanding a permission the user has no reason to grant.

**Playback in the game.** `AndroidSounds` is upstream's `SoundsBase` (buffers, sources, the
background loader thread, device setup) plus a port of the desktop app's `UpdateInverseModel`:
listener at the camera, distance-based gain, only the loudest `SoundNumber` (16) sources
playing, speed of sound from the route's atmosphere. It is created before the route loads,
because routes and trains register their sounds while parsing, and updated after each frame's
render. `AndroidHost.Sounds.cs` ports upstream's host sound methods (decode through the sound
plugins, play and stop through the manager).

On the emulator with the MTR train: 47 buffers registered, loaded on demand; 16 of 19 sources
playing (the cap) once moving; about 0.1 ms per frame. The emulator was started with `-no-audio`,
so this confirms OpenAL Soft opened a device and is mixing — it has not been *heard*. The train's
`sound.cfg` references `sound\Flange1.wav`, which the package does not contain (only
`flange0.wav`), so those errors are the content's own.

## Exclusions in `OpenBveApi.Android`

| Excluded | Why |
|---|---|
| `Properties/AssemblyInfo.cs` | Duplicates the SDK's generated assembly info. |
| `Resource.Designer.cs`, `Resource.resx` | WinForms resource class. The `.resx` holds `System.Drawing.Icon`/`Color` entries and a BinaryFormatter-serialised bitmap, none of which survive on .NET 10. Replaced by `Resource.Android.cs`, which embeds `assets/Languages/en-US.xlf` — the only member the simulation uses. |
| `FunctionScripts/CSAnimationScript.cs` | CS-Script compiles C# at runtime; unavailable on Android under AOT. Replaced by `CSAnimationScript.Android.cs`, a stub whose constructor throws, so upstream's existing try/catch reports the object as unsupported and route loading continues. `.animated` files, the common case, are unaffected. |
| `System/Interop.cs` | WCF service contracts for the 32-bit Win32 ATS plugin proxy. Windows-only by definition. |
| `Texture.cs`, `Sound.cs` | Dead files, present in the tree but not built upstream. Their stale `OpenBveApi.Texture`/`OpenBveApi.Sound` namespaces shadow the real `Texture` and `Sound` types. |

## Licensing

The port is under the Simplified BSD License (`LICENSE` at the repository root; every source file
under `android/` carries the header), like OpenBVE's newer code (its original code is public
domain). BSD-2 and the bundled libraries' own licences require binaries to carry their notices:
`OpenBve.Android/Licenses/*.txt` is packaged as `Assets/Licenses` and shown under About →
Licences (OpenBVE, this port, NAudio, NAudio.Vorbis, NVorbis, NLayer, SharpCompress, DotNetZip,
Ude, OpenAL Soft and its packaging, Bve5_Parsing, ANTLR 4, CoreFX, cursors, SkiaSharp/Skia,
System.Text.Encoding.CodePages, Microsoft.Bcl.AsyncInterfaces, the .NET runtime). ANGLE is not
shipped (see *Graphics backend*), so needs no notice. The copyright line reads "The OpenBVE
Android port contributors"; change it in one place per file if a named holder is wanted.

## Upstream patches

Kept to a minimum, each guarded so desktop builds are byte-for-byte unaffected. `OPENBVE_ANDROID`
is defined only by the Android projects.

| File | Change |
|---|---|
| `OpenBveApi/System/FileSystem.cs` | `Directory.GetAccessControl` (a Windows ACL write test) guarded out under `OPENBVE_ANDROID`. It has no .NET 10 equivalent taking a path. |
| `assets/Shaders/default.vert`, `default.frag`, `rectangle.frag` | Integer literals made explicitly float (`1 *` → `1.0 *`, `== 513` → `== 513.0`) and `1.0 / textureSize(...)` wrapped as `1.0 / vec2(textureSize(...))`. GLSL ES has no implicit int-to-float or ivec2-to-vec2 conversion. **Unconditional — GLSL has no preprocessor guard for this — but a no-op on desktop**, where the compiler performs exactly these conversions silently. Worth sending upstream as a portability fix. |
| `Plugins/Texture.BmpGifJpegPngTiff/PNG/PngDecoder.cs` | The IDAT scanline read now loops until the buffer is full (`ReadFully`). **An upstream bug worth reporting:** `Stream.Read` may legitimately return fewer bytes than requested, and modern .NET's `DeflateStream` does, so a single call reported "insufficient data" on every PNG. .NET Framework's and Mono's implementations happened to fill the buffer, which is why it never showed. Unconditional: correct on every runtime, and unchanged in behaviour wherever `Read` already filled the buffer. |
| `OpenBveApi/System/Hosts/HostInterface.cs` | The two **named** `EventWaitHandle` fields for the Win32 plugin proxy guarded out under `OPENBVE_ANDROID`. Named synchronisation primitives do not exist on Android, and being static fields they run in the type initializer — so merely constructing a host threw before anything else could happen. Used only by the two excluded proxy files. |
| `OpenBveApi/System/FileSystem.cs` | `Assembly.GetEntryAssembly().Location` (7 uses) routed through new `EntryAssemblyLocation`/`EntryAssemblyFolder` helpers that fall back to `AppContext.BaseDirectory`. Android has no managed entry assembly, so this was a null dereference. Unconditional, and unchanged on desktop where the entry assembly always exists. |
| `LibRender2/BaseRenderer.cs` | The ReShade detection — which looks for `opengl32.dll` next to the executable — guarded out under `OPENBVE_ANDROID`. Same null entry assembly, and the DLL is Windows-only anyway. |
| `AssimpParser/Wavefront/ObjFileMtlImporter.cs` | Removed `using System.Runtime.Remoting.Channels;`. Unconditional, but the namespace is unused by the file (it appears nowhere else in it) and does not exist outside .NET Framework. Deleting it has no effect on desktop. |
| `TrainManager/SafetySystems/Plugin/Plugin.Functions.cs` | The `HostPlatform.MicrosoftWindows` branch, which constructs the WCF `ProxyPlugin`, guarded out under `OPENBVE_ANDROID`. Android falls through to the existing `default` case, which already refuses Win32 plugins with a sensible message. `ProxyPlugin.cs` itself is excluded from the Android build. |
| `OpenBveApi/Interface/Translations/LanguageFile.cs` | `LoadEmbeddedLanguage` builds the language from a `MemoryStream` under `OPENBVE_ANDROID`. **This is an upstream bug worth reporting:** `Resource.en_US` is a `ResXFileRef` holding the *contents* of `en-US.xlf`, but it is passed to `NewLanguage(string)`, which opens its argument as a file path. The embedded fallback therefore throws on every platform. Desktop rarely notices because the language folder normally exists; Android reaches it on every launch. |
| `LibRender2/openGL/VertexArrayObject.cs`, `TrainManager/Car/CarBase.cs` | New `VAOExtensions.UpdateVAO`, used by CarBase for elements with an LED function. `CreateVAO` returns early for a mesh that already has a VAO, so a 2D panel DigitalGauge whose vertices change each frame never reached the GPU after the first. Unconditional; on desktop the same bug would apply to any renderer that uploads once. |
| `OpenBveApi/Objects/ObjectTypes/AnimatedObject/AnimatedObject.cs` | The loop collapsing unused LED vertices advanced `v` but wrote index `j`, leaving all but one at a previous frame's position. **Upstream bug.** |
| `LibRender2/Textures/TextureManager.cs`, `LibRender2/Objects/ObjectLibrary.cs` | `textureCache` changed from `Dictionary<TextureOrigin, Texture>` to `Dictionary<TextureOrigin, TextureTransparencyType>`, and its two uses updated. Only the transparency type is ever read back from it; keeping the whole decoded texture held 1.25 GB of pixel data for the MTR route on the phone, which the low-memory killer ended the game over. Unconditional, and a straight win on desktop too. |

## Known blockers

- **Native Win32 ATS plugins** run in the x86 compatibility layer (see *Win32 train plugins*);
  64-bit native plugins and ones needing Windows GUI, DirectX, sockets or threads cannot.
- **Managed train plugins** should load, provided they avoid Windows APIs.
- **CS-Script animation scripts** are unavailable, as above.
- **Case-sensitive file systems.** Android storage is case-sensitive (except FAT/exFAT SD cards);
  upstream's case-insensitive path lookup (`OpenBveApi/System/Path.cs`) handles the routes tried
  so far.
- **Heavy routes** (e.g. the Tuen Ma Line pack) load slowly and run at single-digit frame rates
  in the exterior view on a mid-range phone; see *Backend frame rates*.

## Next steps

A textured route loads, a train runs on it under simulation, and the view is from the driver's
cab. What remains:

**Version 1.0 (2026-09-29)** is released (`1.14.0.3-android.1`, signed with the project's release
key): everything below except the last item is done.

1. ~~Touch controls~~, ~~sound~~, ~~2D cab panels~~, ~~track following objects~~,
   ~~preceding trains~~, ~~Win32 plugins~~ (x86 layer), ~~the menu~~ — done.
2. ~~On-screen messages, the timetable overlay, the route map and gradient display, and the
   score~~, ~~keyboard and game-controller input~~, ~~timetable scrolling~~, ~~the Controls
   editor~~ (menu and pause menu) — done.
3. ~~Desktop modes (DeX), live display switching~~, ~~the information bar~~, ~~touch button
   options~~, ~~SD card storage~~ — done.
4. ~~Translations for the Android-only labels~~ (drafts, to be checked by native speakers),
   ~~release packaging~~, ~~the beta data log~~ — done.
5. **Performance in dense scenery and heavy routes** (per-object draw calls, the alpha pass, load
   time) — the main work after 1.0.

### Testing on real hardware

Everything so far has run on the `Medium_Phone_API_36.1` emulator, whose GL is a translator onto
desktop GL and is more forgiving than a phone driver. Shader compilation is the exception, but
rendering behaviour and performance both need checking on a physical device.

## The renderer: inventory and GL decision

`LibRender2` is written against OpenTK 3 and desktop OpenGL, with GLSL 1.50/4.10 shaders.
Android gives OpenGL ES 3. Measured surface:

- **97 distinct GL entry points, 529 call sites.**
- **8 shader files, ~25 KB total** (`assets/Shaders/`): 4 at `#version 150 core`, 4 at
  `#version 410 core`. All need converting to `#version 300 es` with precision qualifiers.
- **~35 OpenTK enum types** are referenced, but only a fraction of each enum's members.

### Legacy OpenGL is mostly dead code

The calls with no GLES equivalent — immediate mode (`GL.Begin`/`End`/`Vertex2`/`TexCoord2`/
`Color4`), the matrix stack (`MatrixMode`/`PushMatrix`/`PopMatrix`/`LoadMatrix`/`Ortho`) and
`PolygonMode` — are ~96 call sites concentrated in four files: `Primitives/Rectangle.cs`,
`Text/OpenGlString.cs`, `MotionBlur/MotionBlur.cs` and `Overlays/RailPath.cs`.

Crucially these sit behind `if (Shader != null)` fallbacks: upstream already has shader-based
paths for rectangles and text (hence `rectangle.vert`, `text.vert`), and only falls back to
immediate mode when shader creation fails on ancient GPUs. On Android shaders always exist, so
**those branches are unreachable** and the shim can leave them as stubs that throw. Motion blur
is an optional effect and can be disabled; `RailPath`/`Marker` are Route Viewer overlays, which
are out of scope.

This is the single biggest de-risking finding so far: the renderer is essentially a modern
GL 3.x renderer with a legacy fallback, not a legacy renderer.

### Decision: a hand-written OpenTK-shaped GLES shim

Tested and rejected: **OpenTK 3.3.3** (the version upstream targets) resolves its GL entry
points through OpenTK's own desktop platform layer — `GraphicsContext.CurrentContext` backed by
X11/WGL — and the NuGet build exposes no `LoadBindings`/`IBindingsContext` hook to redirect it at
an EGL context. It restores on Android only as a .NET Framework asset (NU1701) and cannot bind.

Remaining options:

- **A hand-written `OpenTK.Graphics.OpenGL` shim over GLES 3** (P/Invoke straight to
  `libGLESv3.so`, whose core ES 3 symbols are exported directly, so no `eglGetProcAddress`
  dance is needed for them). Presents exactly the OpenTK 3 API shape upstream expects, so
  `LibRender2` compiles with **no upstream patches**, and the compiler drives the shim to
  completeness — every missing overload is a build error, not a runtime surprise. Roughly 97
  functions plus the used members of ~35 enums. **Chosen.**
- **OpenTK 4/5** with a custom `IBindingsContext` over `eglGetProcAddress` does work on Android,
  but its namespaces and math types moved (`OpenTK.Mathematics.Vector3`, not `OpenTK.Vector3`)
  and its enum and overload names drift from OpenTK 3. That means patching upstream at a large
  number of call sites — exactly what this port is structured to avoid.
- **Silk.NET** or **`Android.Opengl.GLES30`**: both would mean rewriting every call site.

`GL.ProgramUniform*` (49 call sites) is GLES **3.1**, not 3.0. Rather than raising the minimum
device requirement, the shim emulates it on 3.0 by binding the target program only if it is not
already current, setting the uniform and restoring. The current program is **tracked in the
shim**, never queried: the first version called `glGetIntegerv(GL_CURRENT_PROGRAM)` per uniform,
a synchronous round trip that stalls the pipeline, with around fifteen uniforms set per face.
OpenBVE activates a shader before setting its uniforms, so in practice the emulation now issues no
extra GL calls at all. `GL.ResetStateCache()` must be called whenever a new context is made
current.

**Depth texture types.** OpenBVE allocates depth textures with type `FLOAT`. Desktop GL accepts
any transfer type; GLES 3 ties the type to the sized format (`DEPTH_COMPONENT16` takes
`UNSIGNED_SHORT`/`UNSIGNED_INT`, `DEPTH_COMPONENT24` takes `UNSIGNED_INT`) and raises
`GL_INVALID_OPERATION` otherwise. When no data is uploaded the type describes a transfer that
never happens, so `TexImage2D` corrects it. This surfaced only once the background was drawn:
LibRender2's debug build checks `glGetError` at the top of `ResetShader`, which reported an error
left over from `Initialize` several frames earlier.

### What the shim covers

`android/OpenBve.GLES` provides, in the namespaces upstream expects:

- `OpenTK.Graphics.OpenGL.GL` — the ~97 entry points, P/Invoked to `libGLESv3.so`.
- The used members of ~35 GL enums, with their real token values.
- `OpenTK.Vector2/3/4` and `OpenTK.Matrix4`, laid out to go straight into `glUniformMatrix4fv`
  and `glBufferData`.
- `OpenTK.GameWindow`, `DisplayDevice`, `MouseCursor`, `GraphicsMode`, `OpenTK.Input` — stubs.
  The Android front end owns the real surface, and tells `AndroidGraphicsContext` which thread
  the EGL context is current on so the renderer's render-thread checks answer correctly.

Calls with no ES equivalent throw `NotSupportedException` rather than failing silently, so if a
legacy path ever *is* reached the stack trace says exactly which one.

Three behaviours are deliberately dropped, all of which GLES would reject with
`GL_INVALID_ENUM`: the texture border colour (so `ClampToBorder` behaves as `ClampToEdge`), the
combined RGBA swizzle, and the desktop-only hints.

**Quads and polygons are drawn as triangles.** They originally threw, on the assumption that the
object optimiser triangulates every mesh first. It does not: a face whose type is never set is
drawn as `GL_POLYGON` (`BaseRenderer.RenderFace`), and the optimiser converts only some faces. On
the MTR route the first such face comes into view about 20 s after leaving Lo Wu (4,610 m); the
exception killed the render thread, which froze the picture and the simulation while the
on-screen buttons — on the UI thread — kept answering. It was reported as "the train randomly
stops shortly after leaving the platform". The shim now draws `GL_POLYGON` as a triangle fan,
`GL_QUAD_STRIP` as a triangle strip, and `GL_QUADS` as one fan per quad; each is exact for the
convex faces OpenBVE produces, and winding (so culling) is preserved. Verified on the phone:
the train now runs through that point and on.

Two safety nets came out of that bug and stay: a render-thread failure after loading now shows a
dialog and writes `Android/data/net.openbve/files/crash.log` (the phone is seldom attached to a
computer when it happens), and a watchdog on the UI thread reports a frame stuck in one step for
over five seconds, naming the step.

**Draw-call cost.** In dense stretches of the MTR route (~9,000 visible faces) the phone managed
17.5 fps, with 34 ms of the frame in drawing opaque faces. Native GLES and Vulkan-through-ANGLE
measured the same, so the cost was on the CPU: upstream draws every face with its own uniforms,
state and draw call. Two changes, measured at the same spot (5,393 m, 8,313 opaque faces):

| | Opaque faces | Frame rate |
|---|---|---|
| Before | 34.4 ms | 17.5 fps |
| + redundant-state elimination in the shim | 27.3 ms | 20.8 fps |
| + merged draw calls | 18.3 ms | 25.8 fps |

*Redundant-state elimination* (`GL.cs`): capabilities, blend/depth/cull state and uniform values
are cached, and a set to the value already in place is skipped. GL state persists until
changed and this shim is the renderer's only binding, so this is exact. Link and delete forget a
program's uniforms; a new context forgets everything.

*Merged draw calls* (`AndroidRenderer.DrawOpaqueFaces`): upstream inserts each face of a newly
shown object before the object's previous face, so an object's faces sit in the list in reverse
index order, and consecutive same-material faces usually cover adjacent index ranges — 70% of
faces continue such a run. A run (same object state, material and flags, triangle lists, reverse-
contiguous indices, no glow) is drawn as one face spanning the range, through upstream's own
`RenderFace`. It halves the opaque draw calls. The only theoretical difference is rasterisation
order inside a run, which matters only for exactly overlapping same-material faces of one object;
a test mode (`--ez merge_check true`) draws the same frame both ways and diffs the framebuffers,
and through the dense stretch it reported **0 of ~1.37 million pixels different** in every check.
`--ez merge_faces false` turns merging off.

The remaining cost there is mostly the 2,000 polygon faces (drawn as fans, so not mergeable) and
per-object matrix work.

**Result: `LibRender2` compiles for Android with no upstream patches** — the 30k-line renderer
needed 19 shim additions across two builds, not source changes.

### Shaders: translated, not forked

`OpenBve.GLES.ShaderTranslator` rewrites each shader as `GL.ShaderSource` hands it to the
driver, so `assets/Shaders/` stays the single source of truth and future upstream shader changes
carry over without a merge. It does two things:

1. Emits `#version 300 es` as the **first** line, dropping the original directive.
2. Injects default precisions for `float`, `int`, `sampler2D` and `sampler2DShadow`.

Both matter more than they look. Every OpenBVE shader opens with a 24-line licence header before
its `#version`; desktop GL tolerates that, but an ES driver that does not see the directive first
**silently compiles as GLSL ES 1.00** — where `sampler2DShadow` is a reserved word and there are
no implicit conversions. The failures then surface far from the cause. And a fragment shader with
no declared float precision is a hard error in ESSL, while samplers otherwise default to `lowp`,
which is not enough precision for the shadow cascade depth comparisons.
