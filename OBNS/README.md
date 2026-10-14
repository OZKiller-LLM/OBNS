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

Shared libraries (moving here from `Android/` as each is taken up): the GL-on-Vulkan renderer core,
the x86 plugin layer, the BVE5 train plugin, the parallel-update helpers.
