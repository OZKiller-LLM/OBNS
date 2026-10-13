# Device benchmarks and plugin checks

Bash scripts (Git Bash on Windows) that drive the Android port on real phones over adb, USB or
wireless debugging. They use the SDK's adb (`%LOCALAPPDATA%\Android\Sdk\platform-tools\adb.exe`).
Results go to `results/`, which git ignores.

## Benchmarks

| Script | What it does |
|---|---|
| `run_all.sh [apk]` | **1.1: every connected phone at once** (USB and wireless): `content_base.sh`, `prepare_device.sh`, `core11_suite.sh`, `plugin_regression.sh`, in parallel. One file per phone, `results/all_<model>_<serial>.txt`, and `results/all_summary.txt`. `SKIP_PREPARE=1` to skip installing and copying, `ONLY="serial ..."` for some phones. |
| `content_base.sh <serial>` | Where a phone keeps the content, **SD card first**: `Documents/OpenBVE` on the card where the app has "all files" access (not granted by the script), else the app's own folder on the card, else the app's folder on the phone. A Tuen Ma Line already merged into that folder is used where it is. |
| `core11_suite.sh` | 1.1: native Vulkan (default) on the light route, East Rail and the Tuen Ma Line, with OpenGL ES (the fallback) on East Rail and the Tuen Ma Line. |
| `run_device.sh <serial> <name> [skip_prepare]` | Everything for one phone: `prepare_device.sh`, then `core6_suite.sh`. Results in `results/core6_<name>.txt`. |
| `run_model.sh <model prefix> <name>` | The same, finding the phone by model (`SM-A366` = Galaxy A36 5G). |
| `prepare_device.sh <serial> [apk]` | Installs the test build (`install -r`; stops if the installed app has another signature, uninstalling nothing) and copies the content to `BASE` (default: the app's own folder; `TML` for the Tuen Ma Line, default `BASE/TML`). |
| `core6_suite.sh` | Light route (GL), East Rail + R-Train (GL and Vulkan via ANGLE), Tuen Ma Line (GL). |
| `bench_device.sh` | One run: device and SoC, cool-down to thermal status 0-1, load time and profile, ~35 s cab and ~35 s exterior fps (5-second samples), memory, heat after. The cab camera is first moved forward for one second (Keypad 9), since it often starts behind the cab-to-saloon door or a barrier (as upstream places it). `EXTRA="--ez detach_parts false"` etc. adds intent extras. |

Content: the light route and its train come from `android/testcontent`, East Rail and the R-Train
from `content/` (third-party, not in git). The Tuen Ma Line pack is private (HKRSC terms): it is
copied only when `TML_SOURCE` points at a local copy, and must never be committed or packaged.
`NO_TML=1` leaves it out (phones with little storage or RAM: it needs more than 4 GB).

```bash
TML_SOURCE="D:/path/to/TML" bash android/tools/benchmark/run_all.sh
```

```bash
TML_SOURCE="D:/path/to/TML" bash android/tools/benchmark/run_model.sh SM-A366 A36
```

Things learnt the hard way:

- Build the APK first (`dotnet build ... -c Release -p:RuntimeIdentifier=android-arm64`).
- Copy content over USB (~40 MB/s); benchmark over wireless if ports are short. Wireless
  debugging changes port on every reconnect: use the mDNS name (`adb mdns services`,
  `adb-<serial>-xxxx._adb-tls-connect._tcp`) as the serial, or scan for the port.
- Keep phones awake and charging, and check `dumpsys thermalservice` - a warm phone (status 2)
  halves the exterior fps. Some phones report no skin temperature, or a stuck one.
- `adb push` folders on Android 14 (Galaxy A35) were unwritable by the app and made it crash on
  start (fixed in the app; the scripts create folders with `mkdir` first).
- Some phones keep only 256 KiB of log, which other apps fill in seconds: the scripts read and
  clear the log every few seconds.
- Stopping a background run does not always stop its children: check `ps -ef | grep bench` before
  starting another run on the same phone, or the two will restart the game over each other.
- Never edit a script while a run is using it: bash reads scripts as it goes.

## Plugin regression

`plugin_regression.sh <serial>` checks the train plugins known to work: MTR SelTrac + OS_Ats1 and
KCR TBL (Win32, in the x86 layer; ATO must depart) and Barcelona's FCMB (.NET Framework; must
load). Checks whose content is not on the phone are skipped. Run it after any change to plugin
loading, the x86 layer, trimming or input.
