# Desktop Direct3D (OBNS 1.2)

Direct3D 11, 12 and 9.0c as renderer options on Windows x86-64 and ARM64, beside OpenGL and Vulkan;
Metal takes the same place on macOS. Decided with the user (2026-10-30): each first as a **world layer**, as
Vulkan is (`VULKAN.md`): the API draws the world layer and the cab layer, OpenGL draws the rest and lays
them over its frame. Full renderers without OpenGL (upstream's 2D drawing, menus and HUD moved over too)
are a later project. Direct3D 11 is the first.

## Direct3D 11: what changes from Vulkan

| | Vulkan | Direct3D 11 |
| --- | --- | --- |
| Images OpenGL sees | Vulkan memory imported (`GL_EXT_memory_object_win32`) | Direct3D textures registered (`WGL_NV_DX_interop2`) |
| Ordering | two semaphores a frame | `wglDXLockObjectsNV` / `wglDXUnlockObjectsNV` around OpenGL's use |
| Depth for the composite | the depth image | an R32F colour target holding the depth (the composite reads `.r` either way) |
| Vertex buffers | shared: OpenGL's buffers live in Vulkan memory | Direct3D keeps its own copy, written through the same hook (`ObnsSharing.WriteVertices`); OpenGL keeps its own buffers |
| Textures | one bindless array, or pages (no descriptor indexing) | one bound per draw: the paged draw keys with a page of one |
| Draws | `vkCmdDrawIndexedIndirect` per run | `DrawIndexedInstanced` per draw, the draw's record found by an instance stream (`StartInstanceLocation`) |
| Per-draw data | storage buffers | structured buffers (`StructuredBuffer<T>`, same layouts) |
| Shaders | GLSL 450 to SPIR-V | HLSL (shader model 5.0), compiled at start by Windows' own `d3dcompiler_47.dll` |
| Calls | `vulkan-1.dll` P/Invoke | COM tables through function pointers (`Dx.cs`; slots from the SDK's IDL) |

The device is made on the adapter OpenGL draws on (by LUID, `GL_DEVICE_LUID_EXT`), at feature level 11.1 or
11.0. Without `WGL_NV_DX_interop2` (or if the self-test fails) OpenGL draws the world, as without Vulkan.

## Shared code

Most of the Vulkan layer is not about Vulkan: which faces are drawn and in what order (`VulkanScene`'s
records, chunks, keys and sorting), the meshes' triangle indices (quads and polygons as fans), the textures'
decoding, budgets and white stand-in, the composite into OpenGL. These are to become the world layer's own
classes, each API giving only where bytes live and how a frame is recorded, rather than a second copy of
about 3,000 lines:

- `WorldLayer`: what `NewRenderer` and `GameWindow` call (start, finish, cab composite, faces, preloads),
  whichever API draws.
- meshes: the triangulation and bookkeeping shared; storage per API.
- textures: decoding, budgets and the white texture shared; images per API.
- scene: records and order shared; recording per API.

## Steps

1. The device on OpenGL's GPU and the images' path to OpenGL, checked by `OBNS_D3D11_TEST=1`
   (Direct3D clears an image, OpenGL reads it back). **Done** (5cecf2b): RTX 2060 SUPER, feature level 11.1.
2. `WorldLayer`, and the composite shared by both layers; the Direct3D 11 frame's images, laid over
   OpenGL's frame (a tinted clear first). **Done** (a4d1ba7, 522f28e, and the layer by `OBNS_D3D11=1`):
   OpenGL's background with Direct3D's cleared images over it, 94 fps on the Tuen Ma Line.
3. Meshes and textures split into shared logic and per-API storage; Direct3D 11's. **Done**: `WorldMeshes`,
   `WorldTextures`, `WorldHooks` and `WorldMemory` shared; Vulkan unchanged (snapshots on the Tuen Ma Line);
   Direct3D 11 copies the vertex buffers (`ObnsSharedMemory.Mirror`: OpenGL keeps its own) and makes its textures
   (levels by `GenerateMips`): on the Tuen Ma Line about 14,800 meshes, 448 MB of vertices, the cab's 238 textures.
4. The scene's records shared; Direct3D 11's recording and HLSL shaders; the cab layer. **Done**: `WorldScene`
   shared (Vulkan unchanged by snapshot); `D3D11Scene` draws each record with `DrawIndexedInstanced`, bound per
   draw only where the pipeline, texture, sampler or buffer block changes (the draws are sorted by them);
   `Shaders/world.hlsl` is the Vulkan shaders' port. On the Tuen Ma Line at Tuen Mun: the same picture as Vulkan's
   but for the shadows, 7.8 ms a frame (Vulkan 6.8 ms), about 4,100 draws with about 1,300 changes.
5. Shadows; the options' renderer list (OpenGL, Vulkan, Direct3D 11); measured on the Tuen Ma Line. **Done**:
   the shadow maps one array of depth (a layer per cascade) read through a comparison sampler, the pass and the
   cascades' sampling the Vulkan shaders' port; Direct3D 11 in the options (64-bit Windows; where it cannot be
   used, Vulkan is tried, then OpenGL). Snapshots at Tuen Mun differ from Vulkan's by 1.4 levels on average
   (Vulkan's own runs by 0.8). Seven minutes of the AI driving from Tuen Mun: 8.7 ms a frame, the worst frame of
   each 10 s 18 ms (median); Vulkan 7.0 and 19 ms (RTX 2060 SUPER).

6. Direct3D 12 (below). **Done**: in the options as "Direct3D 12" (64-bit Windows; where it cannot be used,
   Direct3D 11 is tried, then Vulkan, then OpenGL; the Direct3D entries are listed on Windows only, not in
   any 64-bit program as before). Snapshots at Tuen Mun differ from Vulkan's by 1.0 levels on
   average. Seven minutes of the AI driving from Tuen Mun: 8.0 ms a frame, the worst frame of each 10 s 21 ms
   (median); its 50 pipeline objects made at the start in 11 ms (RTX 2060 SUPER).

7. Direct3D 9.0c (below). **Done**: in the options as "Direct3D 9.0c" (64-bit Windows; where it cannot be
   used, Vulkan is tried, then OpenGL). From the exterior view (a camera that does not depend on timing) at Tuen Mun,
   its picture differs from Vulkan's by 0.71 levels on average (Direct3D 12's by 0.31, Vulkan's own runs by 0.04):
   texture sampling's rounding, nothing missing or misplaced. Seven minutes of the AI driving from Tuen Mun:
   14.7 ms a frame, the worst frame of each 10 s 26 ms (median), the slowest as expected: 11.5 ms of it the CPU
   issuing about 4,650 draws and 4,450 shadow casters a cascade, each its own call with its record's index set.

Then Metal (IOSurface to OpenGL). Later, for Direct3D 12: its records made and its commands recorded on a thread of their own while
OpenGL draws the background (as Vulkan's are; it is the main thread's now, as Direct3D 11's), and the vertex
buffers shared with OpenGL rather than copied.

## Direct3D 12: what changes from Direct3D 11

| | Direct3D 11 | Direct3D 12 |
| --- | --- | --- |
| Images OpenGL sees | registered (`WGL_NV_DX_interop2`) | Direct3D's memory imported, as Vulkan's (`GL_EXT_memory_object_win32`, `GL_HANDLE_TYPE_D3D12_RESOURCE_EXT`) |
| Ordering | `wglDXLockObjectsNV` / `wglDXUnlockObjectsNV` | one fence both share (`GL_EXT_semaphore_win32`, `GL_HANDLE_TYPE_D3D12_FENCE_EXT`): OpenGL signals it is done with the images, the queue waits, draws and signals, OpenGL waits |
| Frames | drawn as they are given | two in flight: each has its commands and its records' buffers, used again once the GPU is done with them |
| Uploads | `UpdateSubresource` | written into mapped upload memory (64 MB blocks, 512 MB at most before the CPU waits), copied by a list sent before the frame's |
| Mipmap levels | `GenerateMips` | made on every core as the pixels are written (the same box filter) |
| States | state objects, set separately | 48 pipeline objects (and the shadow pass's two), made at the start |
| Bindings | slots | a root signature: the layer as constants, the records as root views, the texture, shadow maps and sampler by tables (one shader-visible heap, a view per texture at its index) |
| Shaders | `world.hlsl`, shader model 5.0 | the same |

The device is made on OpenGL's adapter (by LUID), at feature level 11.0 or later; any Direct3D 12 GPU will do
(the draws bind one texture each, so resource binding tier 1 is enough). It needs OpenGL's
`GL_EXT_memory_object_win32` and `GL_EXT_semaphore_win32`, as Vulkan's layer does; without them (or if the
self-test fails) Direct3D 11 is tried. The structures' sizes are checked against `d3d12.h`'s at the start.

## Direct3D 9.0c: what changes from Direct3D 11

Shader model 3.0 has no structured buffers, integers, texture arrays, instance stream or `SV_Position.z` in the
pixel shader, so `Direct3D9/Shaders/world9.hlsl` is `world.hlsl` rewritten for it:

| | Direct3D 11 | Direct3D 9.0c |
| --- | --- | --- |
| Device | Direct3D 11 | Direct3D 9Ex, with a hidden window of its own (it presents nothing) |
| Images OpenGL sees | `WGL_NV_DX_interop2` | `WGL_NV_DX_interop`, the textures made with share handles (`wglDXSetResourceShareHandleNV`) |
| Records | structured buffers | float4 texels of one A32B32G32R32F texture (written whole each frame), read in both stages by the draw's index (vertex texture fetch); the frame's record in constant registers; integers as floats |
| The draw's record | instance stream | a vertex shader constant set per draw (passed on to the pixel shader) |
| Matrix chains | `int3` | the three integers read as four bytes each (`UBYTE4`) and taken apart as upstream does |
| States | state objects | render states, set where the pipeline key changes |
| Depth as a colour | a second target, its writes masked | a second target bound only for draws that write depth |
| Shadow maps | a texture array, comparison sampler | the quarters of one R32F atlas, the 2x2 filtering of a comparison sampler done in the shader |
| Pixel centres | at .5 | at whole coordinates: positions moved half a pixel |
| Depth buffers | 32-bit float | the most precise that can be made (24-bit on the RTX 2060 SUPER) |
| Textures | `GenerateMips` | A8R8G8B8 (written as BGRA), levels made on every core, copied from system memory (`UpdateTexture`) |

It needs vertex texture fetch of A32B32G32R32F and R32F render targets (any Direct3D 10-class GPU).

## Switches

| Variable | Effect |
| --- | --- |
| `OBNS_D3D11=1`, `=0` | the world layer drawn by Direct3D 11, or not, whatever the options say |
| `OBNS_D3D11_TINT=1` | its world image cleared to a faint green, not to nothing (to show it arrives) |
| `OBNS_D3D11_DEBUG=1` | each texture made, and the copies' counts every 5 seconds, logged |
| `OBNS_D3D11_WHITE=1`, `OBNS_D3D11_NOMIPS=1` | no textures (all white); textures without mipmaps (as Vulkan's) |
| `OBNS_D3D11_TEST=1` | at start, the Direct3D 11 device and its images' path to OpenGL checked and logged |
| `OBNS_D3D12=1`, `=0` | the world layer drawn by Direct3D 12, or not, whatever the options say |
| `OBNS_D3D12_TINT=1` | its world image cleared to a faint green, not to nothing |
| `OBNS_D3D12_DEBUG=1` | each texture made logged; a frame whose start takes 10 ms or more logged with its steps' times |
| `OBNS_D3D12_WHITE=1`, `OBNS_D3D12_NOMIPS=1` | no textures (all white); textures without mipmaps |
| `OBNS_D3D12_TEST=1` | at start, the Direct3D 12 device and its images' path to OpenGL checked and logged |
| `OBNS_D3D9=1`, `=0` | the world layer drawn by Direct3D 9.0c, or not, whatever the options say |
| `OBNS_D3D9_TINT=1`, `OBNS_D3D9_DEBUG=1`, `OBNS_D3D9_WHITE=1`, `OBNS_D3D9_NOMIPS=1` | as Direct3D 11's |
| `OBNS_D3D9_TEST=1` | at start, the Direct3D 9 device and its images' path to OpenGL (a colour and an R32F texture) checked and logged |
