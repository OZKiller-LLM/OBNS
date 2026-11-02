# Renderers without OpenGL (OBNS 1.3)

The plan. Today Vulkan, Direct3D 9, 11 and 12 and Metal each draw the world and the cab, and OpenGL draws the rest of the
frame, lays their images over it and presents it (`VULKAN.md`, `DIRECT3D.md`, `METAL.md`). So every one of them still needs a
working OpenGL driver and a way to share images with it. That sharing is what fails or costs most on some GPUs: the Lisuan
7G100 has no working path, and Direct3D 11 there can only read frames back at 2 to 3 times OpenGL's cost.

In 1.3 a chosen renderer draws the whole game frame, owns the swap chain and needs no OpenGL context.

## What OpenGL still does in a game frame

| Part | Where | Size |
| --- | --- | --- |
| Window, context, present, vertical sync | `OpenBVEGame : GameWindow` (OpenTK) | one class, but everything assumes its context |
| Background (sky cylinder, background objects) | `LibRender2/Backgrounds` | small |
| Motion blur, particles, event cubes | `LibRender2`, `OpenBVE/Graphics` | small |
| Touch areas | `LibRender2/Overlays` | small, has its own framebuffer |
| HUD, text, messages, in-game menu, loading screen | `Rectangle`, `OpenGlString`, `Picturebox`, overlays | the many callers, a few primitives |
| Animated textures | `TextureManager` | the layers copy their frames from OpenGL today |
| Vertex buffers | `VertexBufferObject` | Vulkan shares OpenGL's; the others keep copies |

Not part of this: Object Viewer, Route Viewer and Train Editor stay on OpenGL. The menus before the game (Windows Forms on
Windows, the native menu on macOS, the OpenGL host on Ubuntu) are separate and stay as they are.

## How

1. **One drawing interface for the 2D frame.** Rectangles, text, the loading screen's clear and the blend and alpha state go
   through `IFrameDrawing` (`LibRender2/Frame`), with OpenGL as its first implementation. Upstream's callers don't change:
   OBNS's copies of the primitives call it. Nothing should look different, and this step can be compared frame by frame
   against 1.22. The background, particles, event cubes and motion blur are 3D or full-frame, not quads: they join the
   world layer in step 3.
2. **Textures without OpenGL.** Decoding and transparency are already separate from upload. HUD and animated textures go
   through the layer's own texture path.
3. **Vulkan draws it all, into its own image.** It still presents through OpenGL at this step, so it can be compared with
   the hybrid on the same machine.
4. **A window with no context.** Vulkan gets its own surface and swap chain, with vertical sync and full screen. The game
   runs with no OpenGL context. The Android port's `VulkanSurface` shows the shape, not the code: its renderer is a different design.
5. **Direct3D 12 and 11, then Metal, then Direct3D 9.** The same frame and interface on each. Metal is what makes macOS free of
   its deprecated OpenGL, so it may matter more than its place here suggests.
6. **The Lisuan.** Test Direct3D 11 and 12 there with no interop. The parked readback patch is decided then.

## Rules

- OpenGL stays as a renderer and as the fallback: where a full renderer can't start, the game says why and draws with OpenGL.
- `OpenBVE/` stays untouched; OBNS's copies and new files go in `OBNS/overrides`.
- Each step is compared against 1.22 by snapshot (`OBNS_SNAPSHOT_EVERY`), on Uchibo, the Kwun Tong Line and a CSV route, in cab and exterior views.
- Android has its own renderers, and isn't part of this.

## Status

Step 1 done on Windows x64. Against the 1.22 release, M-Train on the Kwun Tong Line: the paused in-game menu is byte-identical
under OpenGL and Vulkan; the Vulkan cab differs only in the countdown message. The other frames differ by a scatter of edge
pixels that two runs of 1.22 also show. Load time 30.1 s against 28.8 s (three runs each, ranges overlap). The loading screen,
Object Viewer and Route Viewer start and draw. Train Editor and Linux not run yet.

macOS (M1, TML, exterior view, against 1.22): the same under OpenGL and Metal in the world, HUD, loading screen, in-game menu and
native menu pictures; load time and frame rate within 1.22's own spread; no new log errors. Cab view, driving and audio not covered.

With OpenGL drawing the world, faces and HUD elements are blank for about 20 s after loading on this route (17 to 22 s, in 1.22
as well), while worker threads decode what is in view, and the frame then shows what Vulkan shows. `OBNS_ASYNC_DECODE=0`
decodes on the main thread instead. A finished decode no longer holds one of the decode places until its texture is asked for
again: that could stop decoding altogether, from reading the code; no run has shown it happening, and the time above is unchanged.

Comparisons use `OBNS_SNAPSHOT_EVERY`: `PrintWindow` starts returning black some seconds into a run, and pristine upstream's
window has only come out black through it.
