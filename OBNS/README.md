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
4. The Android port's features on the desktop: the native Vulkan renderer, per-frame updates on all
   cores, pipelined frames, then the Win32 plugin layer (x86) for macOS and Linux.

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

Shared libraries (moving here from `Android/` as each is taken up): the GL-on-Vulkan renderer core,
the x86 plugin layer, the BVE5 train plugin, the parallel-update helpers.
