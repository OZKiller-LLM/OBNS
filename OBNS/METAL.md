# Desktop Metal (OBNS 1.2)

Metal as a renderer option on macOS, in the place Direct3D has on Windows (`DIRECT3D.md`): a **world layer**, as
Vulkan's and Direct3D's are. Metal draws the world layer and the cab layer; OpenGL (macOS's OpenGL 4.1, itself on
Metal on Apple silicon) draws the rest and lays them over its frame. Chosen in the options as "Metal" (macOS only), saved
as `renderer = metal` in `obns.cfg`; where Metal cannot be used, OpenGL draws the world, as upstream.

## What changes from Direct3D 11

| | Direct3D 11 | Metal |
| --- | --- | --- |
| Images OpenGL sees | registered (`WGL_NV_DX_interop2`) | IOSurfaces: a Metal texture each (`newTextureWithDescriptor:iosurface:plane:`) and an OpenGL rectangle texture (`CGLTexImageIOSurface2D`); the composite reads rectangles (`WorldComposite(true)`) |
| Formats | RGBA8, R32F | BGRA8 (`'BGRA'`), R32F (`'L00f'`) |
| Ordering | lock and unlock | two sets of images, a frame each in turn: OpenGL fences its use of a set, which is waited for before Metal draws into it again; OpenGL waits for Metal's frame (its command buffer completed) before laying it over |
| Buffers, textures | written through the context | Apple silicon's unified memory: buffers written directly, textures by `replaceRegion` (levels made on every core), while Metal draws nothing |
| The draw's record | instance stream | its base instance (`[[base_instance]]`) |
| Shaders | `world.hlsl` | `Metal/Shaders/world.metal`, its port, compiled at the start (`newLibraryWithSource`) |
| States | state objects | 12 pipelines (blending, depth writes, layer) and the shadow pass's, made at the start; culling and the depth test set on the encoder |
| Shadow maps | texture array, comparison sampler | `depth2d_array`, a `constexpr` comparison sampler |
| Calls | COM tables | `objc_msgSend`, declared once per argument list (`Mtl.cs`); selectors looked up once, only on macOS |

## Steps

1. The device, the images' path to OpenGL (`OBNS_METAL_TEST=1`), the shaders, the world and cab layers with shadows,
   the options. Written on Windows (it builds there; nothing of it runs there); the first test on the Mac (M1, macOS 26,
   Mono 6.14) is build 13. **Done** (build 13): the self-test passed, the shaders and 12 pipelines made in 658 ms; at
   Tuen Mun's start view 80-92 fps (OpenGL 49-59), 4.3-4.5 GB process memory (OpenGL 6.7 GB); the snapshot the
   same as OpenGL's to 0.01 levels (a close-up view: the world, cab and shadows still to be compared).

## Switches

| Variable | Effect |
| --- | --- |
| `OBNS_METAL=1`, `=0` | the world layer drawn by Metal, or not, whatever the options say |
| `OBNS_METAL_TINT=1` | its world image cleared to a faint green, not to nothing |
| `OBNS_METAL_DEBUG=1`, `OBNS_METAL_WHITE=1`, `OBNS_METAL_NOMIPS=1` | as Direct3D 11's |
| `OBNS_METAL_TEST=1` | at start, the Metal device and its images' path to OpenGL (a BGRA and an R32F image) checked and logged |
