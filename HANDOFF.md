# OpenBVE → Android: handoff notes

A working summary for whoever continues this port (human or AI). `PORTING.md` is the detailed
design record (brought up to date 2026-09-26); this file is the short "where things stand and how
to carry on".

Last updated 2026-09-26.

## Goal

Run OpenBVE 1.14.0.3 natively on Android, including trains whose safety systems are Win32 ATS
plugin DLLs.

Test phone: Samsung Galaxy A73 (SM-A736B, serial `R5CT41WNFSV`, Android 15, Adreno 642L,
2400×1080). Emulator AVD `OpenBVE_Test` (x86_64) exists but is rarely used now.

Test content (already on the phone under `/storage/emulated/0/Android/data/net.openbve/files/`,
extracted copies on the PC under `D:\TEMP`):

| Content | Phone path (under `files/`) | PC copy |
|---|---|---|
| MTR East Rail route EAL2023 | `Railway/Route/EAL2023/東鐵線南行 (羅湖→金鐘).csv` | `D:\TEMP\Railway` |
| MTR R-Train EMU 2023 (3D cab, single handle, DetailManager → OS_Ats1 + kcrwr_seltrac + TargetSpeed) | `Train/MTR R-Train EMU 2023` | `D:\TEMP\mtr` |
| KCR East Rail route | `Railway/Route/KCR East Rail/九廣東鐵南行 (羅湖→尖東) M184 ver2007.csv` | `D:\TEMP\kcr\Railway` |
| KCR East Rail MLR (PXSE Workshop) (2D panel, twin handle, DetailManager → kcrer_tbl_mlr = TBL with ATO) | `Train/KCR East Rail MLR (PXSE Workshop)` | `D:\TEMP\kcr\Train` |

Both trains' `train.txt` are UTF-16; this matters (see "Recently fixed").

## Ground rules the user set

- Ask before downloading third-party content.
- Don't unlock the phone, enter a PIN, or change system/security settings.
- Upstream sources stay unmodified where possible; patches are marked `ANDROID PORT PATCH`.
- Plugin DLLs from content are **not executed natively**. The user explicitly asked for the x86
  compatibility layer, which *interprets* them inside an emulated sandbox — that is the approved
  way they run.
- The user sometimes operates the phone during tests; unexpected key events in the log may be
  theirs.

## Layout

- `OpenBVE-1.14.0.3/source/` — upstream, compiled in place.
- `android/` — SDK-style shim projects (`*.Android.csproj`) compiling upstream folders for
  `net10.0-android36.0`, plus:
  - `OpenBve.GLES` — hand-written OpenTK-shaped GLES 3 binding, EGL/ANGLE selection.
  - `OpenBve.Android` — the app (activities, host, renderer glue, UI, native-plugin bridge).
  - `OpenBve.X86` — the x86/Win32 compatibility layer (net10.0 library): IA-32 interpreter
    (`Cpu*.cs`), paged memory, PE loader and Win32 process (`Win32Process.cs`), kernel32/msvcrt
    shims, the BVE ATS ABI (`AtsPlugin.cs`), and call recording/replay (`AtsTrace.cs`).
  - `OpenBve.X86.Tests` — desktop console harness for plugins (see "Plugin debugging").
- `android/Directory.Build.props` — `EnableDefaultCompileItems=false` for shim projects, `true`
  in `OpenBve.Android`, `OpenBve.X86`, `OpenBve.X86.Tests`.
- `android/OpenBve.Android/UpstreamFacade.cs` — `OpenBve.Program` (CurrentHost, Renderer,
  CurrentRoute, Sounds, TrainManager), `OpenBve.Interface.CurrentOptions`, and an abstract
  `OpenBve.TrainManager` so desktop files compile unchanged. Linked upstream desktop files:
  `FunctionScripts.cs`, `Game/AI/AI.cs`, `AI.SimpleHuman.cs`, `AI.PreTrain.cs`. Inside namespace
  `OpenBve`, `TrainManager` names that class — write `global::TrainManager.…` for the namespace.

## Build and install

```
cd C:\dev\OpenBVE-Android\android
dotnet build OpenBve.Android/OpenBve.Android.csproj -c Release -p:RuntimeIdentifier=android-arm64 -t:Install -p:AdbTarget="-s R5CT41WNFSV"
```

Release is AOT (~3–4 min). The SDK prints Chinese: check for `0 個錯誤`. **A failed build still
exits 0 through a pipe and the phone keeps the old APK** — confirm with
`adb -s R5CT41WNFSV shell "dumpsys package net.openbve | grep lastUpdateTime"`.
Harness: `dotnet build OpenBve.X86.Tests -c Release`.

Gotchas seen in this environment:
- Bash heredocs containing quotes/backslashes sometimes get mangled by the tool layer; write
  edit scripts to a file (scratchpad) and run them, or use a file-edit tool.
- adb cannot read files under very long paths (the scratchpad); copy to `D:\TEMP` first.
- Git Bash: prefix adb commands with device paths by `MSYS_NO_PATHCONV=1`.
- adb daemon restarts can make the device briefly "not found"/unauthorized.

## Run and test

Menu: `adb shell am start -n net.openbve/net.openbve.MenuActivity` (the user launches from here).
Game directly (Git Bash):

```
F=/storage/emulated/0/Android/data/net.openbve/files
MSYS_NO_PATHCONV=1 adb -s R5CT41WNFSV shell "am start -S -n net.openbve/net.openbve.GameActivity --es route_file '$F/Railway/Route/EAL2023/東鐵線南行 (羅湖→金鐘).csv' --es train_folder '$F/Train/MTR R-Train EMU 2023' --ei train_encoding 1200"
```

Extras: `--ei train_encoding 1200` (reproduces what the menu passes for these trains — always
include it when testing plugins), `--ez plugin_trace true` (record plugin calls, see below),
`--ez autodrive true` (demo driver), `--es graphics opengl|vulkan` (persists), `--ez skip_tfo true`,
`--ez merge_faces false`, `--ez merge_check true`.

Logs: `adb logcat -s OpenBVE`. Every 5 s: `scene:` (fps, track position, speed), `frame:`
(per-phase ms), `faces:`, `train:` (scripted/preceding trains, handles driver/actual, doors),
`memory:`, `sound:`. Plugin lines start `x86 DetailManager.dll:` (loads, KeyDown/KeyUp,
SetSignal, first 200 beacons, and `panel i=v` changes at most once a second).
Screenshots: `adb exec-out screencap -p > file.png` from Git Bash. Crash log on device:
`files/crash.log`.

Useful tap coordinates (phone, 2400×1080, in-game overlay):
- Top row: pause 104,80 · VIEW 266,80 · CAB 428,80 (toggles cab camera restriction — needed to
  pan a 2D panel freely) · ◀ 590,80 · ▶ 752,80 · RESET 914,80 · CAM 1076,80 (numpad).
- ATS row (y 574): S 114 · A1 276 · B1 438 · "ATS…" 601 expands A2…G; expanded: A2 601, B2 763,
  C1 925, C2 1087, D 1248, E 1411, F 1573, G 1735.
- Twin levers: POWER▲ 2004,744 · POWER▼ 2004,942 · BRAKE▲ 2244,744 · BRAKE▼ 2244,942.
- Unified (single-handle) lever: POWER▲ 2244,540 · N 2244,744 · BRAKE▼ 2244,942.
- DOORS LEFT 636,744 · DOORS RIGHT 636,946 · REV FWD 154,744 · HORN 395,744.
- Look around: hold-drag on the scene (`input motionevent DOWN/MOVE/UP` with a sleep; a quick
  `input swipe` is too short). adb cannot pinch.

## How to drive the test trains (verified on the phone)

**KCR MLR, TBL with ATO** (user's instructions + testing): reverser F (default), brake to N
(BRAKE▼ ×6), cycle the doors once (DOORS LEFT — this "kicks" the start), then D (SECURITY_D,
"2" on PC) = ATO/manual toggle, then S (Space) = start TBL. E ("3") acknowledges the awareness
light, otherwise EB. The dial's yellow band marks speeds *above* the permitted speed (plugin
sends Ats11 = 140 − permitted; panel angles are reversed) — this matches upstream's math.

**MTR R-Train, SelTrac ATO** (from `Train.txt` key list + replay experiments):
1. Close the doors.
2. Lever to full brake (BRAKE▼ repeatedly).
3. A2 (Delete) ×5: mode selector 5 Off → 0 Auto (panel index 70; moves only with brake applied).
   Off = emergency brake. 1 = Coded Manual (manual driving under SelTrac), 2 = FRM.
4. Wait for the TOD departure countdown (panel 34/35) to end; SelTrac then shows a target speed
   and "m to End of Authority".
5. Lever at **B8 (full service, not EB)**, press S (ATO start). Leave the lever at B8 — SelTrac
   drives. Stepping the lever through intermediate notches = driver braking = ATO cancelled.
Keys per `Train.txt`: E pantograph, F–I run number, B1/B2 destination, C1 coupler cover.

## Plugin debugging (x86 layer)

- Harness: `dotnet run --project OpenBve.X86.Tests -c Release --no-build -- <root> <dll> [mode]`
  where root is the folder above `Train` (e.g. `D:\TEMP\mtr`, `D:\TEMP\kcr`), dll e.g.
  `"D:/TEMP/mtr/MTR R-Train EMU 2023/plugin/DetailManager.dll"`. Modes: frame count (default
  drive), `probe` (which input releases traction), `ato` (TBL start), `horn` (horns/keys/doors
  robustness), `seltrac` (scripted SelTrac sequence), `replay <trace> [stopFrame] [script]`.
- Recording: launch with `--ez plugin_trace true`; the app writes `files/plugin-trace.bin`
  (flushed every 60 frames). Pull it (`adb pull …/plugin-trace.bin D:/TEMP/plugin-trace.bin`) and
  `replay` it: it reports any frame where the desktop emulator's outputs differ from the phone's
  (a determinism check of the layer) and prints a per-second summary. `replay <trace> 7000
  "brake:8,wait:1,key:S,wait:3"` replays to frame 7000 then runs a script (ops: key:X[:frames],
  down:X, up:X, power:n, brake:n, rev:n, open, close, horn:n, wait:seconds) — the fastest way to
  work out a plugin's procedure. Replaying into a single module DLL (e.g. `kcrwr_seltrac.dll`)
  shows which module holds the brake.
- Last check: 20,580 frames recorded on the phone replayed with **0 differences** — the layer
  behaves identically on ARM and x64.

## 2026-09-26: plugin compatibility, ANGLE, licensing (build 2026-09-26 04:39)

- **Plugin survey** (`OpenBve.X86.Tests survey D:/Documents/openBVE`): every 32-bit plugin named
  by an `ats.cfg` in the user's library, run for a simulated minute. After this round's fixes
  38 run, 16 are .NET (upstream loader), 3 fail as they would on Windows (corrupt DLL; missing INI;
  settings expected beside the host .exe), 45 `ats.cfg` name missing files. Fixes: ~70 more
  imports (`Win32Extras.cs`: pointer probes, locales table + GetLocaleInfo, date formats, INI
  writes and wide reads, VS2015+ UCRT/vcruntime140/msvcp140 start-up/exit/exception helpers,
  `_libm_sse2_*` in XMM registers, misc CRT), SSE2 shifts/PEXTRW/PINSRW, INI key/section trimming
  as Windows does (fixed a Japanese ATS that reported version 0).
- **Code page choice** (`NativePluginBridge.AnsiEncoding`): legacy train encodings are kept; for
  UTF-8/UTF-16 the train's legacy text files decide (KCR → 950, Japanese → 932), else UTF-8.
- **Diagnostics**: `Cpu.HostCallTrace`, harness `calls` mode (every Windows call with strings).
- **ANGLE**: no longer shipped. First use copies the device's `/system/lib64` ANGLE into
  `files/angle/` and loads it (GLES before EGL). `android/angle/` and `tools/Get-Angle.ps1` are
  deleted. Verified Vulkan on the A73.
- **Licensing**: root `LICENSE` (BSD-2, holder "The OpenBVE Android port contributors"), header
  on all 83 port `.cs` files, `OpenBve.Android/Licenses/*.txt` packaged and shown in About →
  Licences. Test screenshots of third-party content deleted from `android/`.

## 2026-09-25 (all on the phone, build 2026-09-25 19:01)

Earlier in the session (documented only here):
- **x86 compatibility layer** runs Win32 ATS plugins (DetailManager + modules) inside an IA-32
  interpreter with a PE loader and kernel32/msvcrt shims; `NativePluginBridge` prefers it and
  falls back to the managed translations (DetailManager/OS_Ats1 re-implementations, pass-through)
  only if emulation fails. Cost ~0.2–0.3 ms/frame.
- **2D cab panels** (panel2.cfg) render: RenderCab's 2D branch ported; texture registration on
  the render thread no longer deadlocks (`AndroidHost.RegisterTexture`).
- **LED/DigitalGauge** updates: upstream patches `LibRender2/openGL/VertexArrayObject.cs`
  (`UpdateVAO`), `TrainManager/Car/CarBase.cs` (uses it), `OpenBveApi/.../AnimatedObject.cs`
  (unused-vertex loop). Not yet in PORTING.md's patch table.
- **Deferred key releases** (`AndroidControls`): a release in the same frame as its press is held
  to the next frame so plugins that poll key state see taps.
- **Camera numpad** (CAM button): desktop keypad layout (8/2 up/down, 4/6 left/right, 9/3
  forward/back, 5 reset, 7/1 POI, 0/. zoom, / * roll), placed beside whichever lever set shows.
- **Unified lever** for single-handle trains (POWER▲ / N / BRAKE▼ → Single* commands).
- **Preceding trains** (item 2 of the user's list): `Train.RunInterval` trains and RunInterval /
  PreTrain track-following objects driven by upstream's `SimpleHumanDriverAI`; `.PreTrain` as one
  invisible `BogusPretrainAI` train; upstream's fast-forward to the start time (minimalistic
  simulation, via `AndroidHost.SimulationState`); train/buffer collisions and disposal in
  `AndroidTrainManager.UpdateTrains`. Tested with a temporary RunInterval copy of the KCR route
  (since deleted): AI train ran its timetable, signals behind it went red. Fast-forward costs
  ~4 s at load, mostly the preceding train's exterior being introduced.

Fixed today:
- **Horns played once only** — release never called `Horn.Stop()` for primary/secondary, which
  is what resets `LoopStarted` (`AndroidControls.ControlUp`).
- **TBL / SelTrac "not working"** — launched from the menu, the train encoding is detected from
  `train.txt` = UTF-16; it was passed to the emulator as the Windows ANSI code page, so every path
  string was garbage, DetailManager's DllMain returned FALSE, and the trains silently fell back
  to translations. `NativePluginBridge.AnsiEncoding` maps UTF-16/32/7 to UTF-8.
- **Trees drawn inside carriages (exterior view)** — the world alpha pass only had upstream's
  "performance" branch while options are "quality"; now `AndroidRenderer.DrawAlphaFaces` serves
  world and cab with both branches. User reports rendering fine now.
- HUD: second line "next stop: <name> <distance>" (upstream DistNextStation2 logic);
  emergency-brake hint "S, then B1" only for the default ATS-Sx plugin.
- Removed my temporary test route `zz-test M184 RunInterval.csv` from the phone.

## 2026-09-26 (later): overlays, interface styles, timetable card, keyboard

- Upstream's HUD, messages, score, timetable texture and route map are linked and drawn
  (`AndroidRenderer.RenderScene` ends with `Overlays.Render`). Compat `Font` now falls back per
  character (`SKFontManager.MatchCharacter`) so CJK text is not tofu.
- Options → Advanced → *In-game interface*: Automatic (Desktop in Samsung DeX / desk dock) /
  Touch / Desktop. Touch hides the HUD and puts next stop, arr/dep times and messages in the
  information line; Desktop hides the touch controls.
- TT in the touch interface opens `TimetablePanel` (native timeline card). See PORTING.md.
- `KeyboardInput`: upstream key bindings plus a fixed controller layout; MISC_AI works.
- MAP opens the same card at a Map tab (plus Gradient), with live train dots. Upstream overlays are
  drawn on a virtual screen (`AndroidRenderer.OverlayScale`) so the desktop HUD is legible on a
  phone and lines up.
- Fixed on the way: route map never generated (now `LoadInformation` in the background), shim
  `GL.Color4` threw, compat `Graphics` drawing never reached the bitmap unless disposed.
- **Verified on the phone (build 2026-09-26 19:58):** touch info line (arr/dep, messages, CJK),
  timetable card collapsed/expanded on KCR and MTR, Map and Gradient tabs; forced Desktop
  (`--es interface_style desktop`, persists — reset with `auto`): no touch controls, HUD scaled,
  F8 route map/gradient, Ctrl+T timetable, 114 key bindings loaded. Not yet tried: a physical
  keyboard/controller, real Samsung DeX.

## 2026-09-26 (evening): TPWS, native map/gradient, host fixes

- **TPWS verified** (item 5): OS_Ats1 beacon 44003 = train stop at red → indicator 51 +
  emergency; S resets at a stand; B1 overrides (indicator 52). Harness mode `tpws`; on-phone
  procedure in `android/testcontent/tpws/README.md` (test route/train removed from the phone).
- Touch Map/Gradient tabs are native vector charts (`RouteViews.cs`); desktop keeps upstream's.
- Fixed: `AndroidHost.Trains` hid the base member (crash with forced-red departure signals);
  added missing host overrides AddScore, AddMarker/RemoveMarker, AddObjectForCustomTimeTable,
  CameraAtWorldEnd.
- Next, in the user's order: USB keyboard via hub with wireless ADB; USB controller likewise;
  DeX with wireless ADB. Wireless ADB: `adb pair`/`adb connect <ip:port>` from Developer options
  → Wireless debugging (the user does the pairing on the phone).

## 2026-09-27: keyboard, controllers, menu focus (ADB over Wi-Fi)

- Wireless ADB: use the SDK's adb (`%LOCALAPPDATA%\Android\Sdk\platform-toolsdb.exe`, 36.x);
  `C:\WINDOWSdb.exe` is 30.0.3 and cannot pair/connect reliably and restarts the server. The
  connect port changes when wireless debugging restarts: a port scan of the phone (30000-50000)
  finds it; the paired device also appears as `adb-R5CT41WNFSV-...._adb-tls-connect._tcp`.
- USB keyboard via hub: every binding tried works. AI button added above the ATS row.
- Wired Xbox 360 pad via an unpowered hub: works but drops off the bus (phone sourcing ~1.9 A);
  fixed stuck camera (stick return to zero, device-removed release). 8BitDo Pro 2 over Bluetooth:
  everything works, TBL start driven from the pad.
- Menus navigable by controller/keyboard (focus ring, pause menu focus); see PORTING.md.
- DeX (point 4) done on a second phone, Galaxy S24 Ultra SM-S928B at 192.168.0.121 (Ethernet,
  paired; serial adb-R5CX203WDTH-...). KCR content copied to it from D:\TEMP\kcr. Desktop mode
  detection by window display, live DeX ⇄ phone switching without reload; see PORTING.md
  "Desktop modes". Screen capture of the DeX display is blocked (secure display): ask the user.

## 2026-09-27 (later): cab touch, mouse, timetable scroll, test route

- Cab touch areas by tap / mouse (`CabTouch`, CPU projection instead of upstream's float-FBO
  picking); panel2 extended mode on by default (Options toggle); `SceneInput` shared by the
  overlay and the desktop view (drag look, wheel zoom). Verified on the A73 (test panel in
  `testcontent/touch`) and in DeX on the S24.
- Ctrl+Up/Down timetable scrolling; 50-station test route `testcontent/make_timetable50.py` →
  `Railway/Route/Timetable50.csv` (+ Uchibo objects, `Train/AndroidTest`), now on the A73.
- Test trains left on phones: `Train/zz-touch test train` on the S24 (delete when done).

## 2026-09-27 (evening): Vulkan report, benchmarks, beta log, Controls page

- Vulkan: the renderer runs entirely on ANGLE's Vulkan backend when selected (no direct system-GL
  calls); Android's own UI views stay on HWUI (Skia OpenGL). Benchmarks in PORTING.md.
- Beta data log: build with `-p:BetaLog=true` ONLY when the user asks for a tester build; logs go
  to Documents/OpenBVE. See PORTING.md "Beta data log".
- Controls page in the main menu (keyboard + controller rebinding); not yet tried on a device
  (both phones were locked).
- The S24 now also has the MTR EAL2023 route and R-Train (copied from D:\TEMP and the A73).

## 2026-09-27 (later): touch button options, translations, icon

- Options: touch button size / opacity; pause menu: hide / show touch buttons.
- Android-only labels translated (drafts, 13 languages) through `AndroidStrings`; per-key,
  pad-axis and cab-touch-area log lines moved to Debug level.
- App icon from the user's classic logo (android/artwork).
- Tested on the A73 over the user's VPN (100.108.27.76, wireless-debugging port found by scan):
  Controls page (key and pad binding, saved across restarts, reset), zh-HK labels, touch button
  size/opacity/hide. Fixed from it: Large overflowed (fit cap), top row compacted, analog commands
  removed from the Controls lists. Icon confirmed by the user on the launcher.
- Source zip refreshed: C:\dev\OpenBVE-Android-source-2026-09-27.zip (16.2 MB, same exclusions
  as before; dist/ and keys never included). Dense-scenery performance work paused until the
  user is home. The A73 is still set to the Vulkan backend from the benchmarks.

## 2026-09-27: release packaging

- `android/release.ps1` (see PORTING.md "Release packaging"): version 1.14.0.3-android.<n>,
  code 1140300+n, release-key signing via environment passwords, APK/AAB, dist\<version>\ with
  checksums, licence and read-me. Tried end to end with `-DebugKey` (APK and AAB verified), trial
  output deleted. No release key exists yet: the user makes it (keytool command in the script's
  help); do not make or hold one for them. Beta builds only when the user says.

## VERSION 1.0 DONE (2026-09-29)

- Released build: `dist.14.0.3-android.1\` (APK + AAB, code 1140301), built by the user with
  `release.ps1 -PortRelease 1 -Format both`, signed with the user's release key
  (`%USERPROFILE%\.openbve\openbve-release.jks`, alias openbve, SHA-256 45:B8:92:0B:...:5B:D9).
  The key and its password are the user's: never copy, move or hold them.
- Every later build that is handed out (release or beta) must use this key and a higher
  `-PortRelease` (next: 2). Betas from now on: `release.ps1 -PortRelease <n> -Beta` (release key),
  not -DebugKey. Testers holding the debug-key beta uninstall it once before installing a
  release-signed build (Documents/OpenBVE content survives).
- release.ps1 now checks the password against the key before building (a wrong one used to fail
  only at signing, "java.exe exited with code 2").
- Next work (post-1.0): performance for dense scenery / heavy routes; then the smaller open items.

## 2026-09-29 (late): beta built for testers

- `dist.14.0.3-android.1-beta-debugkey\` (APK 51 MB, README, RELEASE_NOTES, LICENSE,
  SHA256SUMS), built by the user's instruction with `release.ps1 -PortRelease 1 -Beta -DebugKey`
  (no release key yet: testers uninstall it before the release-signed build; Documents/OpenBVE
  content survives). Installed on the A73 (same key as dev builds).
- Beta log reworked to the user's spec (PORTING.md "Beta data log"): Documents/OpenBVE on SD or
  phone, readable sections, device/settings/game facts, 30-s fps entries with frame cost, marked
  warnings/errors, noise filtered, nothing personal. Verified on the A73.
- Translations: the user said leave them as they are for now.

## 2026-09-29: VERSION 1.0 MILESTONE

The user declared the current state the **1.0 initial release** milestone. Tuen Ma Line (HKRSC
pack, SD card) loaded after ~10 min and ran at 10-15 fps cab / 2-5 fps exterior on the A73
(OpenGL ES) - heavy routes are a known limitation of 1.0 (RELEASE_NOTES.md, PORTING.md
"Backend frame rates"). Release notes: RELEASE_NOTES.md (copied into dist by release.ps1).
Still needed to publish: the user's release key; beta build only on the user's word. After 1.0:
performance for dense scenery / heavy routes (draw-call batching of static geometry, alpha pass,
load time incl. SD-card file access), then the smaller open items below.

## 2026-09-29: tested on the A73 (USB)

- SD storage works: content folder /storage/6234-3630/Documents/OpenBVE (the user had already put
  Tohoku Shinkansen content there). Internal storage has ~4 GB free - keep content on the card.
- Information bar: fixed on device - placed by hand (FrameLayout's centring with margins was off),
  width from `DriverInfoBar.FitTo` (text widths, never measuring live views mid-layout, which
  corrupted the limit sign), refit on each status; drops "km/h", then the clock, when narrow
  (numpad open). Verified wide and narrow, power/EMG chip colours.
- In-game Controls page verified: opens full-screen, binding applies at once after Back
  ("read 115 key bindings ..." then X -> PowerIncrease). Controls reset to defaults afterwards.
- TML pack pushed to the SD card (see below); benchmark script: scratchpad tml_bench.sh
  (copy of mtr_ato_bench.sh with the SD paths, startup wait 300 s).
- First TML run (OpenGL ES) gave no data: the backend line appeared, but no "train:" state line
  followed, so the doors/ATO steps and fps capture ran blind. Next: launch it by hand and read
  logcat (load errors? load longer than the wait? crash?), then rerun OpenGL and Vulkan.

## 2026-09-28: CURRENT STATE - resume here

0. **Content storage (release behaviour, user's instruction):** routes/trains default to
   `<SD card>/Documents/OpenBVE` (Railway, Train), fallback `<phone>/Documents/OpenBVE` when no SD
   card; the app's own Android/data folder only without all-files access
   (`AndroidFileSystem.ContentFolder`, `UpdateContentFolder` on menu resume, file finder place
   "App folder (earlier content)" = key `place_app_own`, untranslated so far). Built, untested.
   On the A73 the existing content is still in Android/data/net.openbve/files; the TML pack
   should go to its SD card's Documents/OpenBVE.

Built (Release, 0 errors) but NOT yet installed or tried on a phone:
1. **Pause menu → Customize controls** now opens the real Controls page full-screen over the game
   (`PauseMenu.ShowControls`/`CloseControls`, `ControlsPage(activity, back)`), replacing the old
   static placeholder. Leaving it raises `PauseMenu.ControlsClosed`; GameActivity then drops its
   `KeyboardInput` so the bindings are re-read at once. The help sentence was split into
   `controls_help` + `controls_apply_next` / `controls_apply_now` in all 14 language files.
2. **New touch information bar** (`DriverHud.cs`, `DriverInfoBar`), per the user's screenshot: at
   the bottom, centred in the gap between the door buttons and the levers (`ControlOverlay.PlaceHud`,
   via ViewTreeObserver.GlobalLayout; any visible child in the bar's band narrows the gap). Speed
   (red over limit), round limit sign, handle chip coloured by state, reverser chip, clock; next
   stop line; amber safety-warning strip. Messages moved to a dark card at the top (`info`
   TextView, route colours, camera-mode name shown 2 s after a change). Data: new
   `AndroidTrainSession.Status()` → `DriverStatus` record; `GameView.InfoChanged` now carries it;
   `ControlOverlay.SetStatus` replaced `SetInfo`. `DriverInfo()` string kept for logs.
   To check on device: overlap at Normal/Large, timetable card expanded, numpad open, hidden mode.

Next, after installing and checking those (A73 first):
3. **Dense-scenery test with the user's TML pack** at D:\Documents\TEMP\TML_LicensingFix   (already extracted: Railway\, Train\). Route e.g. `Railway/Route/TMLDN 20144.csv` (daytime;
   also Rain/Evening/Night variants; "Dengo" variants use the Dengo panel trains); it includes
   TMLTFO2025\*.csv and uses all three object folders (mtrtml2021 1.2 GB, mtrmol2021 1.0 GB,
   mtrwrl2025 2.1 GB) plus Railway/Sound; route asks for "MTR TML SP1900 EMU" → use
   `Train/MTR TML SP1900 EMU ver2022` (251 MB). ~4.8 GB to push to
   /storage/emulated/0/Android/data/net.openbve/files/{Railway,Train}; check free space first.
   Plugin: DetailManager + OS_Ats1 + kcrwr_seltrac.dll (SelTrac; start procedure in memory
   notes; use `--ei train_encoding 1200`). Measure fps (OpenGL vs Vulkan, cab and exterior) like the
   MTR EAL benchmark, then work on dense-scenery performance (CPU draw-call bound).
   Copyright: HKRSC pack, private testing only - never put it in the repo, zips or dist.
   **Storage: the A73's internal storage is low - put the TML route AND train on its external SD
   card, not in Android/data on internal storage.** Find the card with `adb shell ls /storage`
   (a XXXX-XXXX volume). Keep the Railway tree together (the route finds Object/ and Sound/ as
   siblings of Route/), e.g. /storage/XXXX-XXXX/OpenBVE/Railway/... and
   /storage/XXXX-XXXX/OpenBVE/Train/MTR TML SP1900 EMU ver2022. Check the card's free space and that
   adb can write there; if the root is not writable, use the app's own folder on the card
   (/storage/XXXX-XXXX/Android/data/net.openbve/files, which exists once the app calls
   GetExternalFilesDirs - the app currently uses only the primary one). Reading from the card
   needs the app's all-files access (Options/Start page shows whether it is granted); the file
   finder has an "SD card" place. Launch with --es route_file / train_folder pointing at the card.

Device notes: A73 at home 192.168.0.120, away via the user's VPN 100.108.27.76; S24 192.168.0.121.
Wireless-debugging port changes: scan 30000-50000 and `adb connect` (several may be open; only
one connects). Use the SDK adb and `MSYS_NO_PATHCONV=1` for /storage paths. Both phones need the
user to enable wireless debugging / unlock; never unlock them. The A73 is still on the Vulkan
backend from the benchmarks (user may switch back to OpenGL ES).

Standing rules: ask before downloading third-party content; don't change phone system/security
settings; prompt the user to connect the phone when needed; keep upstream unmodified where
possible (ANDROID PORT PATCH markers, listed in PORTING.md); beta build (`release.ps1 -Beta`)
only when the user says; no release key exists yet - the user makes it (see release.ps1 help).

## Open items (in the user's order)

1. ~~2D cab panels~~ — done.
2. ~~AI / preceding trains~~ — done (see above); not yet tried on a route that ships RunInterval.
3. **On-screen messages, timetable overlay, route map & gradient display, score** — implemented;
   verify on the phone (info-line messages, arr/dep, timetable card, MAP, CJK, desktop HUD size).
4. **Keyboard and game-controller input** — done and verified (keyboard, USB and Bluetooth
   controllers, DeX). Timetable scroll keys and the Controls editor (main menu) are done; the
   in-game Controls page (pause menu) is built but not yet tried on a phone.
5. **TPWS and SelTrac** — both verified (SelTrac procedure above; TPWS 2026-09-26 evening).

Also:
- Licensing is done; if the user names a copyright holder, replace "The OpenBVE Android port
  contributors" in `LICENSE`, the file headers and `Licenses/OpenBVE for Android.txt`.
- Plugins the layer cannot run: 64-bit native, ones needing GUI/DirectX/sockets/threads, and ones
  relying on Windows SEH/C++ exceptions they catch themselves (not emulated).
- ~~Hide/shrink option for the touch buttons~~ — done (size, opacity, hide).
- Performance in dense areas (~26 fps worst case): polygon fans, per-object matrices, alpha pass
  (now two passes in quality mode — re-measure).
- `DemoDriver` (`autodrive`) could be replaced by SimpleHumanDriverAI on the player train.
