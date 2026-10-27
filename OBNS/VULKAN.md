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

- **Meshes.** Upstream gives each face corner its own vertex and draws a face as triangles, a strip,
  quads, a quad strip or a polygon. Vulkan has no quads or polygons, so each mesh gets its own triangle
  index list over the same vertices (fans for quads and polygons). A mesh's buffers are made when
  upstream makes its VAO (a hook in OBNS's copy of LibRender2's `VertexArrayObject.cs`), and remade when a
  dynamic mesh is updated.
- **Textures.** Made when upstream uploads a texture (a hook in OBNS's copy of the texture manager), with
  mipmaps, in one bindless array (descriptor indexing) indexed by the draw.
- **Per-draw data.** One storage buffer a frame holds each draw's model-view and texture matrices, its
  normal matrix, material colours and flags, brightness, opacity and texture index; the shader reads its
  record by `gl_InstanceIndex` (the draw's `firstInstance`). A draw is then only `vkCmdDrawIndexed`, and
  draws are grouped by pipeline (blend mode, alpha test, culling, depth writes).
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
`OpenBve.csproj`:

- `Vk.cs`: the Vulkan API, bound by hand (only what is used, no package dependencies, so it runs under
  .NET Framework and Mono alike); structure layouts checked against Silk.NET's.
- `VulkanDevice.cs`: instance and device, on the GPU OpenGL is on.
- `SharedTarget.cs`: the shared colour and depth images and the semaphores.
- `VulkanWorld.cs`: the frame (OpenGL hands over, Vulkan draws, OpenGL composites) and OpenGL's side.

OBNS's `NewRenderer.cs` calls it after the background; `GameWindow.cs` sets it up when the window loads.

## Steps

1. Device, shared images, semaphores; OpenGL composite of a cleared image (checks the interop). Done:
   on the Tuen Ma Line, OpenGL reads Vulkan's clear exactly and the scene is unchanged.
2. Meshes, textures, per-draw data, shaders: the world layer drawn by Vulkan; compared with OpenGL by
   screenshot and timed with `OBNS_FRAMESTATS=1`.
3. Pipelined frames.
4. The overlay layer (cab).
5. Shadows.
6. Linux (`_fd` handles); later, the 2D layer for macOS.
