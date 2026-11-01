# GPU support and default renderers (OBNS 1.2)

What each GPU family can run on Windows, Linux and macOS, and the renderer it should start on. This is the basis for
the defaults in `ObnsOptions.cs` and `VulkanDevice.cs`; the code doesn't read this file. Compiled 2026-10-31.

Every desktop renderer here is a world layer: OpenGL always draws the rest of the frame (`VULKAN.md`, `DIRECT3D.md`,
`METAL.md`). So a GPU needs OpenGL 3.3, the version the game asks for, and a working path to share images with OpenGL.
A GPU can have Vulkan or Direct3D and still end up on OpenGL because that path is missing.

## How the default is decided

1. **Use the GPU OpenGL is running on,** not the CPU model or the list of GPUs in the computer.
2. **Use the exceptions below** for that OS and GPU family, where they name one.
3. **Otherwise use the OS's normal default:** Vulkan on Windows and Linux, Metal on Apple silicon.
4. **If it won't start,** fall back to OpenGL. An explicit choice in the options always beats the default, except where
   a row says refused.

A Hackintosh is treated as an Intel Mac with the same GPU. Nothing needs to detect one.

Evidence: **measured** was run on our hardware; **documented** comes from vendors or the sources below; **decided** was
set with the user; **assumed** has none of these yet. The API columns describe the platform's support, not a test of
OBNS on that GPU.

## Windows (x86-64 and ARM64)

The Direct3D options exist only in 64-bit processes. Vulkan needs `GL_EXT_memory_object_win32`; Direct3D 9, 11 and 12
need `WGL_NV_DX_interop2`. Both are checked at start.

| GPU | Direct3D 12 level | Direct3D 11 level | OpenGL | Vulkan | Best default | Evidence for the default |
| --- | --- | --- | --- | --- | --- | --- |
| Intel Sandy Bridge and older | none | no | 3.1, below the 3.3 the game asks for | none | may not start | assumed |
| Intel Ivy Bridge | none | 11_0 | 4.0 | none | OpenGL | documented |
| Intel Haswell | 11_1 | 11_1 | 4.3 | none | OpenGL | documented |
| Intel Broadwell | 11_1 | 11_1 | 4.4 | none | OpenGL | documented |
| Intel Skylake and newer (HD, UHD, Iris, Xe, Arc) | 12_1 to 12_2 | yes | 4.6 | 1.3 to 1.4 | Vulkan | assumed |
| NVIDIA Fermi (400, 500) | 11_0 | 11_0 | yes | none | OpenGL | documented |
| NVIDIA Kepler (600, 700) | 11_0 | yes | yes | up to 1.2, core 1.0 path | Vulkan | assumed |
| NVIDIA Maxwell and newer | 11_0 (first Maxwell) to 12_2 | yes | yes | 1.3 or newer | Vulkan | measured on an RTX 2060 SUPER only |
| AMD TeraScale (HD 5000, 6000) | none | 11_0 | yes | none | OpenGL | documented |
| AMD GCN 1 to 3 (HD 7000, R7, R9; driver frozen at 22.6.1) | 11_1 to 12_0 | yes | yes | yes, version not confirmed | Vulkan | assumed |
| AMD Polaris, Vega, RDNA | 12_0 to 12_2 | yes | yes | yes | Vulkan | assumed |
| Qualcomm Adreno X (ARM64) | 12_1 or newer | yes | only through Microsoft's Compatibility Pack | 1.3 or newer | OpenGL | assumed |
| Lisuan LX 7G100 | 12_1 | yes | 4.6, through Mesa 22.0.4 | 1.3 | OpenGL, by fallback | measured |

Measured on the RTX 2060 SUPER (Tuen Ma Line, exterior view, shadows 2048), frame time: Vulkan 14.6 ms, Direct3D 12
15.7 ms, Direct3D 11 16.8 ms, Direct3D 9 27.4 ms, OpenGL 29 to 37 ms.

Measured on the Lisuan LX 7G100 with a Zhaoxin KX-7000 (2026-11-01; 12 GB, 32 GB RAM, Windows 11, 1920x1080, vsync off,
`OBNS/bench/bench.ps1`). All 14 runs drew with OpenGL, with no crashes or display driver events. Its OpenGL has no
extension with memory_object, semaphore or DX_interop in its name, so Vulkan and Direct3D 9, 11 and 12 all fell back at
start. Its Vulkan device does offer `VK_KHR_external_memory_win32`, so the missing part is on OpenGL's side.

| Bundle | Shadows | Load | Frame rate | Train | Memory |
| --- | --- | --- | --- | --- | --- |
| BVE6 Uchibo + E217 | Off | 60 to 62 s | 47 to 50 fps standing, 51 to 53 fps driving | moving | 0.6 GB |
| BVE6 Uchibo + E217 | High | 60 to 62 s | 38 to 39 fps standing, 42 to 43 fps driving | moving | 0.6 GB |
| East Rail Line 2023 | Off | 173 s | 57 to 60 fps | stationary | 0.6 GB |
| East Rail Line 2023 | High | 155 s | 50 fps | stationary | 0.6 GB |
| Tuen Ma Line | Off | 641 s | 23 fps once settled | stationary | 2.0 GB |
| Tuen Ma Line | High | 512 s | 17 fps once settled | stationary | 2.1 GB |

- **These figures may be low:** later the same day the same build ran Uchibo at 81.3 fps with shadows off and 64.9 fps on High
  (driving, 150 s). Swap time had fallen from about 4.5 ms to 0.4 ms a frame; the PC had rebooted in between, and the cause isn't
  known. The East Rail and Tuen Ma Line figures weren't taken again.
- **Stationary trains:** the bench's AI driver didn't move the two SelTrac trains, so their figures are at the first
  platform (the East Rail Line's Off run is almost certainly stationary, not proven). They aren't driving figures.
- **Tuen Ma Line warm-up:** frames start near 800 ms and take about 100 s after loading to settle, while video memory
  fills and GPU time stays level. That looks like CPU-side or upload work; the cause isn't established.
- **Bottleneck:** frame time looks CPU or driver bound, as GPU time is small (about 2.3 ms in a 22 ms frame on Uchibo with
  shadows High, 4.8 ms in 57 ms on the Tuen Ma Line).
- **Worst frames:** without warm-up and the bench's own 60 s snapshots, 38 ms on the East Rail Line and 80 to 114 ms on
  the Tuen Ma Line. These weren't worked out for Uchibo. The 100 ms and larger spikes in `summary.csv` are mostly those snapshots.

- **GPUs without Vulkan:** Direct3D 11 may beat OpenGL on them, but needs measuring before it could become the default.
  Reports on Intel's `WGL_NV_DX_interop2` are mixed, and some are old.
- **Adreno:** one user reports the Compatibility Pack replacing the native Vulkan driver with a layer over Direct3D 12.
  With no native OpenGL, the sharing path is unlikely to work. No such machine has been tested.

## Linux

Vulkan needs `GL_EXT_memory_object_fd`, and OpenGL and Vulkan must be on the same GPU (matched by UUID). The 1.2/1.3
path is used where the device has all it needs, otherwise the full core Vulkan 1.0 path. There is no Direct3D or Metal.

| GPU (Vulkan driver) | OpenGL | Vulkan | Best default | Evidence for the default |
| --- | --- | --- | --- | --- |
| Intel Sandy Bridge and older | 3.3 at most | none | OpenGL | documented |
| Intel Ivy Bridge, Bay Trail (hasvk) | 4.2 | 1.2, incomplete | OpenGL, Vulkan refused | decided |
| Intel Haswell (hasvk) | 4.6 | 1.2, incomplete | OpenGL, Vulkan refused | decided |
| Intel Broadwell, Cherry Trail (hasvk) | 4.6 | 1.3 | OpenGL, Vulkan selectable | measured on Broadwell: 47 fps against 14; Cherry Trail assumed |
| Intel Skylake and newer, Apollo and Gemini Lake (anv) | 4.6 | 1.3 to 1.4 | Vulkan | assumed |
| NVIDIA Fermi and older | yes | none | OpenGL | documented |
| NVIDIA Kepler and newer, NVIDIA's driver | yes | 1.2 on Kepler, newer after | Vulkan | assumed |
| NVIDIA Kepler and newer, Mesa's NVK | yes | 1.2 on Kepler, newer after | Vulkan if sharing works | assumed |
| AMD TeraScale and older | yes | none | OpenGL | documented |
| AMD GCN 1 and 1.1 on the `radeon` kernel driver | yes | none | OpenGL | documented |
| AMD GCN and newer on `amdgpu` (RADV; the default for GCN 1 and 1.1 from Linux 6.19) | yes | yes | Vulkan | assumed |
| AMD APUs from Broadwell's era or older (Kaveri, Kabini, Carrizo), no discrete GPU | yes | yes | OpenGL, Vulkan selectable | decided; unmeasured |
| Software (llvmpipe) | yes | never used | OpenGL | code |

Hasvk reports conformance 0.0.0.0 on Ivy Bridge and warns that its support is incomplete (one user's report on Mesa
26.2.5).

**Hybrid laptops:** what counts is the GPU OpenGL starts on, usually the Intel one unless launched with `prime-run` or
`DRI_PRIME=1`. The same laptop can take either row depending on how it's launched.

## macOS

Our builds have no Vulkan on macOS, because MoltenVK has no path to share images with OpenGL. Metal shares through
IOSurfaces. OpenGL is deprecated but present, at 4.1 on every GPU below that has Metal.

### Apple silicon

| GPU | Metal | OpenGL | Best default | Evidence for the default |
| --- | --- | --- | --- | --- |
| M1 and newer | Metal 3 | 4.1, itself on Metal | Metal | measured on an M1: 25.1 fps against 16.4 with shadows on High, 37.5 against 22.2 off |

### Intel Macs and Hackintoshes

macOS 26 Tahoe is the last release for Intel Macs, and most sources list four models: MacBook Pro 13-inch (2020),
MacBook Pro 16-inch (2019), Mac Pro (2019) and iMac (2020). The macOS ranges below are Dortania's for Hackintoshes; its
pages list Sequoia as the newest, so Tahoe isn't confirmed for them.

| GPU | Metal | macOS | Best default today |
| --- | --- | --- | --- |
| Intel HD 3000 and older | none | up to 10.13 | OpenGL (3.3) |
| Intel Ivy Bridge (HD 4000) | Metal 1 | 10.7 to 11 (Metal from 10.11) | OpenGL |
| Intel Haswell | Metal 1 | 10.8 to 12 (Metal from 10.11) | OpenGL |
| Intel Broadwell | Metal 1 | 10.10.2 to 12 (Metal from 10.11) | OpenGL |
| Intel Skylake | Metal 2 | 10.11.4 to 12 (to 15 with spoofing) | OpenGL |
| Intel Kaby Lake to Comet Lake (HD 630, UHD 630, Iris Plus) | Metal 2, Metal 3 from macOS 13 | 10.12.6 or 10.13.6 to 15 | OpenGL |
| Intel Ice Lake (Iris Plus G4, G7) | Metal 2, Metal 3 from macOS 13 | 10.15.4 to 15 | OpenGL |
| Intel GT1 (HD 510, 610, UHD 610) and Xe, Arc | unsupported on any macOS | none | not applicable |
| AMD HD 7000, 8000, R7, R9 | Metal 1 or 2 | 10.8 or 10.10 to 12 | OpenGL |
| AMD Polaris (RX 460 to 590) | Metal 2 or newer | 10.12.6 to 15 | OpenGL |
| AMD Vega, Navi 10, 14, 21, 23 | Metal 3 from macOS 13 | 10.13 to 11.4 upward, to 15 | OpenGL |
| AMD Navi 22, Vega APUs | only through unfinished kexts | not fully working | OpenGL |
| AMD Navi 24, Navi 3x (RX 7000), other APUs | unsupported | none | not applicable |
| NVIDIA Kepler | Metal 1 | up to 11 | OpenGL |
| NVIDIA Maxwell, Pascal | Metal 1 | up to 10.13, with NVIDIA's web driver | OpenGL |
| NVIDIA Volta, Turing and newer | unsupported or web driver only | none, or one 10.13 build for Volta | not applicable |

All of these defaults are assumed: OBNS has only been run on an M1, so no Intel or AMD GPU on macOS has been tested.

**Why OpenGL on all Intel Macs for now:** Metal has a path for these GPUs (managed memory, on the GPU OpenGL is on;
`METAL.md`), but it hasn't been run on any of them. So Metal stays blocked on them until it has been (decided
2026-10-31); `OBNS_METAL=1` tries it. Once measured, Metal is the likely default for the Metal 2 and newer rows, and
Metal 1 GPUs and NVIDIA's are to be judged on their own numbers.

On Macs with two GPUs, Metal's default device may not be the GPU OpenGL is on, which would mean copies between them.

## What the code does

- **Windows:** Vulkan first, falling back to OpenGL.
- **Linux, in the options:** OpenGL if Vulkan lists a GPU in the old rows above and no discrete GPU, otherwise Vulkan.
- **Linux, when a game starts:** also OpenGL if OpenGL's own renderer names a GPU in the old rows, unless Vulkan was
  chosen. This is what catches hybrid laptops, and starting on the discrete GPU still gives Vulkan.
- **macOS:** Metal on Apple's GPUs (`MTLGPUFamilyApple7`), otherwise OpenGL with Metal not offered.
- **All:** the options record whether a renderer was chosen. Below the choice they say what drew the last game, and why
  if it isn't what was asked for. With nothing chosen, the choice itself shows what drew the last game.

A settings file from before this on macOS says `renderer = opengl` whether or not anyone chose it, because the old menu
saved it unasked. It counts as not chosen, so that Mac moves to the default.

## Sources

- Intel: [Intel Graphics Technology](https://en.wikipedia.org/wiki/Intel_Graphics_Technology), [Skylake's first Windows Vulkan driver](https://www.pcgamer.com/intel-launches-full-support-for-vulkan-api), [HASVK split](https://www.phoronix.com/news/Intel-ANV-HASVK-Split-Merged), [hasvk on Mesa 26.2.5](https://github.com/horizon-rust/horizon/issues/4059)
- Direct3D levels: [Feature levels in Direct3D](https://en.wikipedia.org/wiki/Feature_levels_in_Direct3D)
- NVIDIA: [Fermi without Vulkan](https://wccftech.com/nvidia-fermi-geforce-400-500-vulkan-api-support/amp/), [NVK](https://docs.mesa3d.org/drivers/nvk.html)
- AMD: [GCN 1 to 3 legacy](https://www.anandtech.com/show/16775), [Polaris and Vega support](https://tomshardware.com/pc-components/gpus/its-curtains-for-polaris-and-vega-as-amd-reduces-driver-support), [TeraScale](https://discussion.fedoraproject.org/t/no-terascale-and-earlier-gpus-cant-run-wayland-because-almost-all-of-them-dont-even-support-vulkan-p-s-a/198552), [amdgpu for GCN 1 and 1.1](https://www.tomshardware.com/pc-components/gpus/legacy-amd-gpus-receive-30-percent-performance-boost-thanks-to-latest-linux-kernel-update-finally-drops-obsolete-radeon-graphics-driver-for-gcn-1-0-and-gcn-1-1-cards-after-more-than-two-decades)
- Windows on Arm: [Adreno X1](https://at-web1.www.anandtech.com/show/21445/qualcomm-snapdragon-x-architecture-deep-dive/3), [Compatibility Pack report](https://github.com/xemu-project/xemu/issues/1878)
- Sharing on Intel: [`WGL_NV_DX_interop2` report](https://community.intel.com/t5/Developing-Games-on-Intel/WGL-NV-DX-interop2-not-able-to-share-textures-on-Intel-Graphics/m-p/1145549), [memory object report](https://community.intel.com/t5/Developing-Games-on-Intel/ImportMemoryWin32HandleEXT-unsupported/m-p/1697591)
- Metal: [Metal (API)](https://en.wikipedia.org/wiki/Metal_(API)), [feature set tables](https://developer.apple.com/metal/feature-sets), [storage modes](https://developer.apple.com/documentation/metal/setting-resource-storage-modes), [Intel and AMD storage](https://developer.apple.com/documentation/metal/choosing-a-resource-storage-mode-for-intel-and-amd-gpus)
- macOS: [Mac OpenGL table](https://support.apple.com/en-au/101525), [macOS Tahoe's Intel Macs](https://www.macworld.com/article/2808358/if-you-own-an-intel-mac-you-have-12-months-to-upgrade-to-a-new-model.html)
- Hackintosh: Dortania's [Intel](https://dortania.github.io/GPU-Buyers-Guide/modern-gpus/intel-gpu.html), [AMD](https://dortania.github.io/GPU-Buyers-Guide/modern-gpus/amd-gpu.html) and [NVIDIA](https://dortania.github.io/GPU-Buyers-Guide/modern-gpus/nvidia-gpu.html) guides
