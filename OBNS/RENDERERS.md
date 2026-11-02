# Renderers without OpenGL (OBNS 1.3)

Up to 1.22, Vulkan, Direct3D 9, 11 and 12 and Metal each draw the world and the cab, and OpenGL draws the rest of the frame, lays
their images over it and presents it (`VULKAN.md`, `DIRECT3D.md`, `METAL.md`). So every one of them still needs a working OpenGL
driver and a way to share images with it. That sharing is what fails or costs most on some GPUs: the Lisuan 7G100 has no working
path, and Direct3D 11 there can only read frames back at 2 to 3 times OpenGL's cost.

In 1.3 a chosen renderer draws the whole game frame, owns the swap chain and needs no OpenGL context.

## The game on Vulkan alone (`OBNS_VULKAN_FULL=1`)

Windows x64, off by default so far. Everything below is in that mode unless it says otherwise.

- **The frame.** One image: the sky and background objects, the world, motion blur, particles, then the cab with its own depth, then
  the HUD, text and menus, as upstream draws them into its framebuffer. `VulkanWorld.RecordWhole` and `Present`.
- **Presenting.** A swap chain on the window (`VulkanPresenter`), which replaces `SwapBuffers`. FIFO with vertical sync on, immediate
  without it. Made again when the window changes size.
- **No context.** OpenTK's window always makes one, so its platform factory is wrapped: all is passed on, but a context asked for is
  OpenTK's own dummy (`NoGLContext`). The system reports no current context and no pixel format on the window.
- **Upstream's OpenGL calls.** Hundreds remain that no longer draw anything. OpenTK calls through a table, so the table points at machine
  code that does nothing (`GLStub`); a few have answers upstream depends on (a new object's name, a shader's status). Upstream's code
  otherwise runs as it is, which keeps this small and lets an upstream update come in as before.
- **Falling back.** Whether a Vulkan device can be had is checked before the window is made (`VulkanDevice.ProbeFull`). If not, OpenGL
  draws as usual. There is no falling back once the window is made, as it has no OpenGL.
- **Textures.** Through `WorldTextures`, as the world's are. Upstream's texture loading still runs, with its uploads stubbed, so a texture
  used in 2D is decoded twice. That's the next thing to trim.
- **Touch areas.** Picked from their faces, with no framebuffer to read (`Touch.Pick`). `OBNS_TOUCH_CHECK=1` compares it with OpenGL's
  framebuffer in the hybrid mode.
- **The sky.** A third layer in `WorldScene` (`SkyLayer`, `WorldSky`), so the other APIs can use it too. `OBNS_VULKAN_SKY=1` turns it on in the
  hybrid mode.
- **The 2D part.** `IFrameDrawing`, with OpenGL as one implementation and `VulkanFrameDrawing` as the other. Text's first pass darkens
  what is under it, so it can't be a layer laid over: it draws on the frame itself. `OBNS_VULKAN_2D=1` turns it on in the hybrid mode.
  Upstream draws rectangles as a strip of six vertices with culling on, which leaves each half covered once: a list of two triangles here.

## Tested

RTX 2060 SUPER, Windows, against the hybrid (1.22's way), with the game's own frames (`OBNS_SNAPSHOT_EVERY`; `PrintWindow` starts returning
black some seconds into a run).

| What | Result |
| --- | --- |
| East Rail with M-Train, cab with sky and HUD | 3 to 5 pixels of about 576,000 differ by more than 2 levels; about 5,600 differ by 1 or 2 |
| The same, with the sky alone on Vulkan | 4 to 5 by more than 2, 281 to 452 in all |
| HUD frame with the 2D part alone on Vulkan | byte-identical |
| In-game menu with the 2D part alone on Vulkan | the same 6,614 pixels as two OpenGL runs against each other |
| Particles (`OBNS_PARTICLE_TEST=1`, fixed places) | 3 to 4 by more than 2, as without them |
| Touch picking, three 2D-panel trains | 62,964 points, 3,520 on touch areas at most: 0 differ from OpenGL's framebuffer |
| Frame cost, vertical sync off | 2.64 to 2.85 ms against 2.75 to 2.83 ms |
| Loading | 24 s both ways |
| Resizing, minimizing, views, menu, driving, two routes | no errors, pictures right |
| Shadows on | 3 to 11 pixels by more than 2, about 5,600 in all |
| No Vulkan driver | OpenGL draws, as usual |
| Jump to a station | scenery right (the same bug in 1.22 is fixed, for every API) |
| 30 minutes running | memory flat at 2,796 MB, 120 fps, 18 to 23 ms worst frame |
| TML pack | load, memory and frame times as the hybrid (numbers only) |
| Motion blur (`OBNS_BLUR_SPEED=s` forces it on, camera turning) | blurs as the hybrid does while moving. Stopped, it comes back crisp; the hybrid stays slightly soft, with faint ghost lines |
| The other renderers, against 1.22 | Vulkan, OpenGL, Direct3D 12 and 11: 0 pixels; Direct3D 9: 1 pixel by 2 or less |

Step 1 on the M1 Mac (OpenGL drawing the 2D part, under OpenGL and Metal) matches 1.22 in the world, HUD, loading screen, in-game menu and
native menu, with load time and frame rate within 1.22's own spread.

## Not done

- **Event cubes and the rail path overlay:** debug views, not drawn in this mode. Nor are the wireframe and normals views.
- **Particles' fog:** upstream's shader still has the world's fog on when it draws them, so they fade with distance on a foggy route. Not done here.
- **Linux:** `GLStub`'s machine code is for Windows' calling convention, and the surface is Win32's.
- **The older Vulkan tiers:** this needs bindless textures and dynamic rendering, so not the core 1.0 tier.
- **Full screen:** not run, as it changes the display mode.
- **Direct3D 12, 11, 9 and Metal:** the same frame and `IFrameDrawing` on each is still to do. `WorldSky` and the layers are shared already.
- **The options:** no entry yet; it's the switch alone.
- **The Lisuan:** not run there. It's what this is for: no shared images, and no OpenGL.

## Rules

- OpenGL stays as a renderer and as the fallback.
- `OpenBVE/` stays untouched; OBNS's copies and new files go in `OBNS/overrides`.
- Object Viewer, Route Viewer and Train Editor stay on OpenGL. The menus before the game are separate and stay as they are.
- Android has its own renderers, and isn't part of this.
