# Desktop Direct3D (OBNS 1.2)

Direct3D 11 (then 12 and 9.0c) as a renderer option on Windows x86-64 and ARM64, beside OpenGL and Vulkan;
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
   OpenGL's frame (a tinted clear first).
3. Meshes and textures split into shared logic and per-API storage; Direct3D 11's.
4. The scene's records shared; Direct3D 11's recording and HLSL shaders; the cab layer.
5. Shadows; the options' renderer list (OpenGL, Vulkan, Direct3D 11); measured on the Tuen Ma Line.

Then Direct3D 12 (closest to Vulkan: the records, and memory shared with OpenGL as Vulkan's is), Direct3D
9.0c (D3D9Ex, `WGL_NV_DX_interop`; no instancing of records, the slowest), and Metal (IOSurface to OpenGL).

## Switches

| Variable | Effect |
| --- | --- |
| `OBNS_D3D11_TEST=1` | at start, the Direct3D 11 device and its images' path to OpenGL checked and logged |
