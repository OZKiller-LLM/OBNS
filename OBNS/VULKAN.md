# Desktop Vulkan (OBNS 1.2)

The desktop build's Vulkan renderer: a design of its own for desktop GPUs, not the Android port's
(`Android/OpenBve.Vulkan`). The Android renderer is a GL ES emulation sized for phones (tile GPUs, small
memory budgets, ANGLE-shaped state); it does not draw upstream's legacy GL paths and would bring a phone's
limits to a desktop GPU. Here Vulkan draws the scene from the renderer's own data, in the way a desktop GPU
is good at, and leaves the rest of the screen to upstream's OpenGL.

## Shape: a hybrid with OpenGL

Upstream's frame (OBNS's copy of `NewRenderer.RenderScene`) is split:

| Part | Drawn by |
| --- | --- |
| Background (sky cylinder / background objects) | OpenGL, as upstream (drawn first, no depth) |
| World layer: opaque faces, static batches, transparent faces | **Vulkan** |
| Motion blur, particles, event cubes | OpenGL, as upstream |
| Overlay layer (3D cab, 2D panel) | OpenGL first; Vulkan in a later step |
| Touch areas, HUD, text, menus, loading screen | OpenGL, as upstream |

Vulkan draws the world layer into a colour image and a depth image whose memory OpenGL imports
(`GL_EXT_memory_object_win32`, `GL_EXT_semaphore_win32`; `_fd` on Linux). OpenGL then:

1. lays the colour over the background it drew, as premultiplied colour (`One, OneMinusSrcAlpha`);
2. writes Vulkan's depth into its own depth buffer (a full-screen pass writing `gl_FragDepth`), so the
   particles and event cubes drawn after are hidden by the scenery as before.

The composite is exact: Vulkan clears to (0, 0, 0, 0) and blends colour as upstream but alpha separately
(`One, OneMinusSrcAlpha`), so the image's alpha is the world's coverage. Normal blending gives
`world + background * (1 - coverage)`, as drawing over the background directly would; additive faces add
to colour and leave coverage alone, which is also what they do over the background directly.

Two semaphores order the APIs each frame: OpenGL signals that it is done with the images, Vulkan waits,
draws and signals, OpenGL waits before the composite. OpenGL keeps presenting (SwapBuffers), so vertical
sync, the window and full screen are upstream's.

Why the hybrid: the world layer is where the time goes (on the Tuen Ma Line, about 10,000 draws a frame in
the exterior view, CPU-bound in the GL driver); the rest is a few hundred draws through upstream code that
is large and changes often. macOS has no GL interop with MoltenVK, so it stays on OpenGL; moving the 2D
layer to Vulkan, for macOS, is a later step that the 3D renderer is built not to depend on.

## The world renderer

- **One copy on the GPU.** Vulkan and OpenGL share the vertex buffers and textures: Vulkan allocates device
  memory in exportable blocks, which OpenGL imports once each (`GL_EXT_memory_object`), and upstream's vertex
  buffers and textures are made over ranges of them (`glBufferStorageMemEXT`, `glTexStorageMem2DEXT`, in OBNS's
  copies of LibRender2's `VertexBufferObject.cs` and `TextureManager.cs`). OpenGL writes them as upstream does
  and draws with them as before (the cab, the background, and the whole world when Vulkan is not drawing it);
  Vulkan reads the same memory. Separate copies were tried first: on the Tuen Ma Line OpenGL alone uses about
  5.5 GB of an 8 GB card, and a second copy brought the driver down. Each frame OpenGL's semaphore signal names
  the buffers and textures it has written since the last, which Vulkan then takes over.
- **Meshes.** Upstream gives each face corner its own vertex and draws a face as triangles, a strip, quads, a
  quad strip or a polygon. Vulkan has no quads or polygons, so each mesh gets a triangle index list of Vulkan's
  own over the shared vertices (fans for quads and polygons), made when upstream makes or updates its VAO (a hook
  in OBNS's copy of `VertexArrayObject.cs`).
- **Textures.** RGB8 textures are kept as RGBA8 whose alpha reads as 1, and one-channel ones as R8 read as grey,
  in both APIs (OpenGL's swizzle, Vulkan's view); all with full mipmap chains (OpenGL generates them). They are
  in one bindless array (descriptor indexing) indexed by the draw.
- **Per-draw data.** Storage buffers a frame hold each object's model-view, normal and texture matrices and
  animation matrices, and each draw's material colours and flags, brightness, opacity, texture, sampler and
  alpha test; the shader reads its record by `gl_InstanceIndex` (the draw's `firstInstance`). Draws are
  recorded as indirect draw commands, one `vkCmdDrawIndexedIndirect` per run of the same pipeline (blend
  mode, depth writes, culling) and buffer blocks: on the Tuen Ma Line about 4,000 draws in 130 calls in the
  cab, 8,000 in 400 outside.
- **Shaders.** GLSL 450 ports of upstream's `default.vert` / `default.frag` (lighting, fog, alpha test,
  animation matrices, MSTS alpha flag), compiled to SPIR-V with glslang and kept beside their sources.
- **Order.** Opaque faces in any order; colour-keyed faces; transparent faces in upstream's sorted order,
  as `RenderScene` draws them in both transparency modes.
- **Pipelined frames.** Two frames in flight: frame N+1's draw records are built while the GPU draws N.

Not in the first version: shadows (upstream's cascaded shadow maps). With shadows on, the world layer stays
on OpenGL until the Vulkan shadow pass exists.

## Fallback and switches

Vulkan is used when the driver has Vulkan 1.3 with descriptor indexing and external memory, and OpenGL has
the memory object and semaphore extensions on the same GPU (matched by device UUID); otherwise, or with
`OBNS_VULKAN=0`, the world layer is drawn by OpenGL as before (with the GL state filter,
`Graphics/GLStateFilter.cs`, which `OBNS_GL_FILTER=0` turns off). The log says which, and why.

For testing: `OBNS_VULKAN_TINT=1` clears Vulkan's image to a faint green (the background shows it);
`OBNS_VULKAN_DEBUG=1` logs what OpenGL reads back of the shared images for the first frames;
`OBNS_SNAPSHOT_EVERY=<seconds>` saves the frame (the back buffer, before the swap) as `snapshot-N.png`
beside the log, for comparing renderers; `OBNS_FRAMESTATS=1` logs where frame time goes.

## Code

In `OBNS/overrides/source/OpenBVE/Graphics/Vulkan/`, compiled into the desktop program by OBNS's
`OpenBve.csproj` (the shaders, `Shaders/*.spv`, embedded):

- `Vk.cs`: the Vulkan API, bound by hand (only what is used, no package dependencies, so it runs under
  .NET Framework and Mono alike); structure layouts checked against Silk.NET's.
- `VulkanDevice.cs`: instance and device, on the GPU OpenGL is on.
- `SharedTarget.cs`: the shared colour and depth images and the semaphores.
- `VulkanWorld.cs`: the frame (OpenGL hands over, Vulkan draws, OpenGL composites) and OpenGL's side.
- `SharedHeap.cs`, `GpuMemory.cs`: memory shared with OpenGL, Vulkan's own, and uploads.
- `VulkanTextures.cs`, `VulkanMeshes.cs`: the shared textures and vertex buffers, and the triangle indices.
- `VulkanScene.cs`: upstream's per-face drawing (`RenderFace`) as draw records, pipelines, and the recording.
- `Vk.Drawing.cs`: the rest of the API it uses.

OBNS's `NewRenderer.cs` calls it after the background; `GameWindow.cs` sets it up when the window loads.

## Steps

1. Device, shared images, semaphores; OpenGL composite of a cleared image (checks the interop). Done:
   on the Tuen Ma Line, OpenGL reads Vulkan's clear exactly and the scene is unchanged.
2. Meshes, textures, per-draw data, shaders: the world layer drawn by Vulkan; compared with OpenGL by
   screenshot and timed with `OBNS_FRAMESTATS=1`. Done; on the Tuen Ma Line (RTX 2060 SUPER, the camera
   moved forward past the cab door, frame times in ms):

   | View | OpenGL (with the state filter) | Vulkan world layer |
   | --- | --- | --- |
   | Cab | 9.6 | 11.7 |
   | Exterior | 18.5 | 19.5 |

   Vulkan submits the world in about 1.3 ms, but gathering the faces (upstream's per-face work, every face every
   frame) takes 2.2 ms in the cab and 5.4 ms outside: next, each face's draw record kept between frames (only the
   objects' matrices change), and the gathering moved off the main thread.
3. Pipelined frames.
4. The overlay layer (cab).
5. Shadows.
6. Linux (`_fd` handles); later, the 2D layer for macOS.
