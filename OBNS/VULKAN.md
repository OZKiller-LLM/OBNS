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

- **One copy on the GPU.** Vertex buffers are shared: Vulkan allocates device memory in exportable blocks,
  which OpenGL imports once each (`GL_EXT_memory_object`), and upstream's vertex buffers are made over ranges
  of them (`glBufferStorageMemEXT`, in OBNS's copy of LibRender2's `VertexBufferObject.cs`); OpenGL writes and
  draws them as before, Vulkan reads the same memory, and each frame OpenGL's semaphore signal names the
  buffers it has written. Textures are not shared: OpenGL drawing with textures over imported memory cost
  2 ms or more a frame on the CPU (measured: it is the textures, not the buffers, and not the number of
  blocks). Vulkan keeps its own copy of each texture the world draws, made from upstream's decode
  (`TextureManager.ObnsDecoded` in OBNS's copy) and never uploaded to OpenGL, which keeps textures only for
  what it draws itself (the cab, the background, the HUD); animated textures are animated by OpenGL and their
  frames copied. Upstream's preloads go to Vulkan instead of OpenGL ("load textures in advance", and a
  train's exterior; its cab still to OpenGL): with both copies, the Tuen Ma Line's SP1900 (hundreds of
  destination display textures) filled the 8 GB card and the driver reset.
- **Meshes.** Upstream gives each face corner its own vertex and draws a face as triangles, a strip, quads, a
  quad strip or a polygon. Vulkan has no quads or polygons, so each mesh gets a triangle index list of Vulkan's
  own over the shared vertices (fans for quads and polygons), made when upstream makes or updates its VAO (a hook
  in OBNS's copy of `VertexArrayObject.cs`).
- **Textures.** RGBA8, converted as OpenGL keeps them (alpha 1 for textures used as opaque), mipmaps made on the
  GPU (image blits); in one bindless array (descriptor indexing) indexed by the draw. Made when first drawn, a
  few each frame (16 ms of the frame at most; one not made yet is drawn white for that frame), so a new view
  does not hold the frame for seconds: a train's textures are made as they come into view (Vulkan's memory
  cannot be paged out a texture at a time, as OpenGL's is, so textures never shown should not be made).
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

The renderer is chosen in the options (Options, display group: Renderer, OpenGL or Vulkan; Vulkan by default),
kept in `Settings/1.5.0/obns.cfg` beside upstream's `options.cfg`, and takes effect when a game starts. The
debug overlay's renderer line says which is drawing. Vulkan is used when the driver has Vulkan 1.3 with
descriptor indexing and external memory, and OpenGL has the memory object and semaphore extensions on the
same GPU (matched by device UUID); otherwise the world layer is drawn by OpenGL as before. The log says which, and why. While shadows or the wireframe or normals
debug views are on, OpenGL draws the world layer (frame by frame); a Vulkan frame that fails stops Vulkan for
the session, logged, and OpenGL draws the world from then on.

Switches, for testing and measuring (environment variables):

| Variable | Effect |
| --- | --- |
| `OBNS_VULKAN=0` / `1` | OpenGL / Vulkan for the world layer, whatever the options say |
| `OBNS_GL_FILTER=1` | the GL state filter on (`Graphics/GLStateFilter.cs`: skips GL sets that change nothing; off by default, as it costs more than it saves on the desktop) |
| `OBNS_FRAMESTATS=1` | where frame time goes, logged every 10 s |
| `OBNS_GPUSTATS=1` | with it, GPU timestamps (OpenGL's parts, Vulkan's frame) and a finer CPU split |
| `OBNS_SNAPSHOT_EVERY=<s>` | the frame saved (back buffer, before the swap) as `snapshot-N.png` beside the log |
| `OBNS_VULKAN_TINT=1` | Vulkan's image cleared to a faint green (shows where the background is) |
| `OBNS_VULKAN_DEBUG=1` | the shared images read back for the first frames, each texture made, the copies' counts, and a watchdog logging where each thread is when frames stop for 3 s |

## Code

In `OBNS/overrides/source/OpenBVE/Graphics/Vulkan/`, compiled into the desktop program by OBNS's
`OpenBve.csproj` (the shaders, `Shaders/*.spv`, embedded):

- `Vk.cs`, `Vk.Drawing.cs`: the Vulkan API, bound by hand (only what is used, no package dependencies, so it
  runs under .NET Framework and Mono alike); structure layouts checked against Silk.NET's.
- `VulkanDevice.cs`: instance and device, on the GPU OpenGL is on.
- `SharedTarget.cs`: the shared colour and depth images and the semaphores.
- `VulkanWorld.cs`: the frame (OpenGL hands over, Vulkan makes and submits the frame on its own thread, OpenGL
  composites) and OpenGL's side.
- `SharedHeap.cs`, `GpuMemory.cs`: memory shared with OpenGL (vertex buffers), Vulkan's own, and uploads.
- `VulkanTextures.cs`: Vulkan's own textures; `VulkanMeshes.cs`: the shared vertex buffers and the triangle
  indices.
- `VulkanScene.cs`: upstream's per-face drawing (`RenderFace`) as draw records, made on every core, pipelines,
  and the recording.

Elsewhere: OBNS's `NewRenderer.cs` gives the world's faces to it before the background and lays the frame over
it; `GameWindow.cs` sets it up and sends upstream's texture preloads to it; `Graphics/GpuStats.cs` measures;
`System/ObnsOptions.cs` and `UserInterface/formMain.Obns.cs` are the option; OBNS's copies of LibRender2's
`VertexBufferObject.cs`, `VertexArrayObject.cs` and `TextureManager.cs` have the hooks.

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
   frame) took 2.2 ms in the cab and 5.4 ms outside. Since then the gathering is done on every core (in chunks
   merged in order; what needs OpenGL, such as first texture uploads, done first on the main thread), and texture
   loads are skipped for loaded textures, with each face's texture place kept: 1.3 ms in the cab and 2.7 ms
   outside. On a loaded machine (another run, OpenGL at 36 ms a frame outside) the Vulkan world layer was a third
   faster outside (25 ms) and level in the cab. Frame times vary a lot between runs, so comparisons are made
   within one run (`OBNS_FRAMESTATS` also splits RenderScene into before, world and after).

3. Pipelined frames. Done: the world's faces are given to Vulkan before the background is drawn; what needs
   OpenGL is done on the main thread, then the frame is made (on every core) and submitted on a thread of its
   own while OpenGL draws the background, and laid over it where upstream draws the world. The frames then
   started and ended about 2 ms later with Vulkan on; GPU timestamps and a finer CPU split (`OBNS_GPUSTATS=1`)
   showed that was OpenGL drawing with textures over shared memory, now gone (see "One copy on the GPU").
   On the Tuen Ma Line, in one run: cab 9.8-10.2 ms a frame against OpenGL's 10.8-11.2, exterior 15.6-17
   against 19.4-22.9; Vulkan's world on the GPU takes 0.6 ms in the cab and 0.9 outside.
4. The overlay layer (cab).
5. Shadows.
6. Linux (`_fd` handles); later, the 2D layer for macOS.
