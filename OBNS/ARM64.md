# Windows on ARM64 (OBNS 1.31)

1.31 makes the Windows program run natively on ARM64 Windows (Snapdragon X), and tunes it for Snapdragon's Oryon CPU
and Adreno X GPU. It starts from 1.3, so the whole game is drawn by Direct3D or Vulkan; OpenGL on these laptops is
only Microsoft's OpenGL Compatibility Pack (OpenGL over Direct3D 12), so the alone modes matter most here.

## How the program runs there

- `OpenBve.exe` is AnyCPU and doesn't prefer 32-bit. With .NET Framework 4.8.1, which Windows 11 has had since 22H2,
  it runs as a native ARM64 process. With only 4.8, Windows runs it as x64 under emulation, and everything x64 applies.
- Native DLLs must match the process: an ARM64 process can't load x64 DLLs (only ARM64EC ones).

## Done

| Part | Change |
| --- | --- |
| OpenGL stubs (`GLStub`) | The ARM64 stubs, as on Apple silicon, in Windows memory (VirtualAlloc, VirtualProtect, FlushInstructionCache). The alone modes and the options' checkbox are now possible on ARM64 Windows. |
| Sound (`SoundManager/Sounds.cs`) | OpenAL Soft is looked for in `arm64\` in an ARM64 process (`x64\` and `x86\` as before). |
| Train plugins | An x64 plugin (BVE5's Ats64, BVE6) runs in the x86/x64 layer shared with the Android port, as on Linux and macOS. An x86 plugin keeps upstream's plugin proxy, which Windows runs under its x86 emulation. |

## To do

1. **OpenAL Soft for ARM64** (`arm64\OpenAL32.dll`). No release or CI build has one, so it is built from source: release
   1.25.2, unchanged, `cmake -A ARM64`, on the ARM64 laptop (the user's choice; this PC's Visual Studio has no ARM64
   compiler). It is LGPL, as upstream's x64 and x86 copies are: a separate DLL, shipped with `licenses\OpenALSoft.txt`
   and its source named by tag, so OBNS's BSD-2 code is unaffected. Without it the game runs silently.
2. **Packaging:** `build-desktop.ps1 -Package` to make an `-win-arm64` zip as well, with the `arm64\` folder. The
   screen-reader DLLs (Tolk and the rest) are x64 only and would be left out of it.
3. **On the laptop**, in this order:
   - it runs natively (the log says ARM64), with sound;
   - each renderer on TML and East Rail, alone and with the HUD fallback;
   - plugins: an x86 one (proxy), an x64 one (layer);
   - the GPU list's default for Adreno X from those numbers (expected: Direct3D 12 alone, Qualcomm's main driver).

## Snapdragon tuning (after the laptop's first numbers)

- **Unified memory.** Adreno X shares the system's LPDDR5X. Where Direct3D 12 reports cache-coherent UMA, or Vulkan
  has memory that is both device-local and host-visible, write vertices, indices and textures straight into it and skip
  the staging copy.
- **A tile-based GPU.** Adreno X can draw in tiles, so what is loaded from and stored to memory costs the most: depth
  discarded at the end of a frame (`DiscardResource`, `DiscardView`, Vulkan's `DONT_CARE`), clears instead of loads, and
  as few render target changes as possible (shadow map, world, 2D).
- **Bandwidth.** BC1/BC3 compressed textures, as planned for low-memory Android and the Switch, as an option here.
- **CPU.** Oryon cores are all equal, with no SMT: loading and the parallel updates use one thread a core (8 to 12).
- **Power.** Keep the game's own process out of Windows' power throttling (EcoQoS) while driving: a setting for this
  process only, never the system's.
- **Shaders.** Cache compiled pipelines between runs (Direct3D 12 pipeline libraries, a Vulkan pipeline cache), since
  Qualcomm's compiler is slower on first use.
