# Renderers without OpenGL (OBNS 1.3)

Up to 1.22, Vulkan, Direct3D 9, 11 and 12 and Metal each draw the world and the cab, and OpenGL draws the rest of the frame, lays
their images over it and presents it (`VULKAN.md`, `DIRECT3D.md`, `METAL.md`). So every one of them still needs a working OpenGL
driver and a way to share images with it. That sharing is what fails or costs most on some GPUs: the Lisuan 7G100 has no working
path, and Direct3D 11 there can only read frames back at 2 to 3 times OpenGL's cost.

In 1.3 a chosen renderer draws the whole game frame, owns the swap chain and needs no OpenGL context.

## The game on one API alone

`OBNS_VULKAN_FULL=1`, `OBNS_D3D12_FULL=1`, `OBNS_D3D11_FULL=1` or `OBNS_D3D9_FULL=1` on Windows x64; `OBNS_METAL_FULL=1` on Macs with
Apple silicon. Off by default so far.
Everything below is on Vulkan unless it says otherwise, and is the same on Direct3D, with its own frame image and swap chain
(`D3D11World`, `D3D12World`, `D3D9World`, each with its `FrameDrawing`). What is the same whichever API it is lives in `WholeFrame.cs`.

- **The frame.** One image: the sky and background objects, the world, motion blur, particles, then the cab with its own depth, then
  the HUD, text and menus, as upstream draws them into its framebuffer. `VulkanWorld.RecordWhole` and `Present`.
- **Presenting.** A swap chain on the window (`VulkanPresenter`), which replaces `SwapBuffers`. FIFO with vertical sync on, immediate
  without it. Made again when the window changes size. Direct3D 11 and 12 use a flip swap chain with tearing allowed: without tearing it can
  show only as many frames a refresh as it has buffers to spare, however fast the frames are made. Direct3D 9 uses an additional swap chain.
- **No context.** OpenTK's window always makes one, so its platform factory is wrapped: all is passed on, but a context asked for is
  OpenTK's own dummy (`NoGLContext`). The system reports no current context and no pixel format on the window.
- **Upstream's OpenGL calls.** Hundreds remain that no longer draw anything. OpenTK calls through a table, so the table points at machine
  code that does nothing (`GLStub`); a few have answers upstream depends on (a new object's name, a shader's status). Upstream's code
  otherwise runs as it is, which keeps this small and lets an upstream update come in as before.
- **Falling back.** Whether a device can be had is checked before the window is made (`WholeGame.Probe`). If not, OpenGL draws as usual.
  There is no falling back once the window is made, as it has no OpenGL: a frame that fails stops the game with the reason.
- **Textures.** Through `WorldTextures`, as the world's are. Upstream's texture loading still runs, with its uploads stubbed, so a texture
  used in 2D is decoded twice. That's the next thing to trim.
- **Touch areas.** Picked from their faces, with no framebuffer to read (`Touch.Pick`). `OBNS_TOUCH_CHECK=1` compares it with OpenGL's
  framebuffer in the hybrid mode.
- **The sky.** A third layer in `WorldScene` (`SkyLayer`, `WorldSky`), so the other APIs can use it too. `OBNS_VULKAN_SKY=1` turns it on in the
  hybrid mode.
- **The 2D part.** `IFrameDrawing`, with OpenGL as one implementation and `VulkanFrameDrawing` as the other. Text's first pass darkens
  what is under it, so it can't be a layer laid over: it draws on the frame itself. `OBNS_VULKAN_2D=1` turns it on in the hybrid mode.
  Upstream draws rectangles as a strip of six vertices with culling on, which leaves each half covered once: a list of two triangles here.
- **Particles.** Square to the view, so each is one rectangle at one depth, tested against the world's. The world's fog is on for them as it is
  in upstream, taken at each one's middle.
- **Direct3D 9.** Shader model 3: six plain vertices a quad, and no depth clamp, so a background's depth is set in the vertex shader.
- **Metal.** A `CAMetalLayer` in place of what the window's view draws; the window is SDL2's on Apple silicon, so its `NSWindow` comes from
  `SDL_GetWindowWMInfo`. `GLStub` has ARM64 stubs, written to ordinary memory that is then made executable: memory made for a JIT
  would need the thread's protection lifted while writing, which stops the runtime's own compiled code too (the first build crashed so).
  SDL2 still loads the OpenGL framework for its window, but no context is made. One frame at a time, as the world layer is.

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
| The other renderers, against 1.22 | Vulkan, OpenGL, Direct3D 12, 11 and 9: 0 pixels |

The same machine and route for Direct3D alone, against each one's hybrid:

| What | Direct3D 11 | Direct3D 12 | Direct3D 9 |
| --- | --- | --- | --- |
| Pixels of a matched frame by more than 2 levels | 5 to 13 | 3 to 8 | 5 to 7 |
| By 1 or 2 levels (mipmaps: OpenGL's against Direct3D 12's and 9's own, in the sky, most likely) | about 4,600 | about 24,000 | about 24,000 |
| Frame cost, vertical sync off | 2.20 to 2.36 ms against 3.28 to 3.70 ms | 2.52 to 2.69 ms against 3.01 to 3.44 ms | 3.28 to 3.54 ms against 3.47 to 3.59 ms |
| Particles, fixed places | 3 to 14 by more than 2 | 5 to 7 | 4 to 7 |
| Particles in fog (fog to 850 m, particles to 541 m), against the hybrid | 0 by more than 2 | 0 | 0 |
| Resizing, minimizing, views, menu | no errors, pictures right | the same | the same |
| Jump to a station | 0.1 to 0.3% white | the same | the same |
| Motion blur | as the hybrid while moving, by eye and by sharpness | the same | the same |
| No usable Direct3D (a copy of the build with DLLs lacking its functions) | OpenGL draws, as usual | the same | the same |
| Ten minutes running | memory flat at 2.09 GB, 118 to 120 fps | memory flat, 2.3 to 3.3 GB between runs (the hybrid 2.6 to 2.7 GB); 120 fps in two later runs of 7 minutes | memory flat at 2.13 GB, 118 to 120 fps |

Vulkan's frame cost in the same comparison: 2.92 to 3.57 ms against 2.89 to 3.44 ms. Particles in fog on Vulkan: 0 by more than 2.

Step 1 on the M1 Mac (OpenGL drawing the 2D part, under OpenGL and Metal) matches 1.22 in the world, HUD, loading screen, in-game menu and
native menu, with load time and frame rate within 1.22's own spread.

On the M1 Mac (macOS 26, Mono 6.14, SDL2), TML, exterior at the start, against the Metal world layer of the same build (numbers only):

| What | Result |
| --- | --- |
| Start | no OpenGL context; no errors in five runs; loading 68 to 85 s against 77 to 84 s |
| Frame rate, snapshots on in both | 37.0 fps against 24.5 fps with vertical sync on; 33.2 against 27.1 fps with it off (one run each) |
| The window, captured | 0.05% of pixels by more than 2 levels, within the two modes' own run-to-run spread |
| The game's snapshots | 0.43 to 0.69% by more than 2: a 1-pixel outline at the top left, the HUD's boxes at the bottom, a near advert's moiré. Not in the window captures, so most likely in how the snapshot is taken; to be looked at |
| Colours | the same by eye and by mean (42.5/47.6/51.4 against 42.5/47.6/51.3) |
| In-game menu, resizing, minimizing, cab view | right |
| From the native main menu | starts and draws right |
| Five minutes of driving from Tuen Mun | 38.4 fps moving against 29.3, nothing missing; memory about 3.7 to 3.9 GB against 3.8 to 4.5 GB |
| Its fallback (`OBNS_METAL_STUB_REFUSE=1`) | OpenGL draws, with the Metal world layer, as usual |
| The snapshots' difference | the hybrid's own snapshots are what differ (an outline and darker HUD boxes not on its screen); the screens agree |

## Not done

- **Event cubes and the rail path overlay:** debug views, not drawn in this mode. Nor are the wireframe and normals views.
- **Linux:** `GLStub`'s machine code is for Windows' calling convention, and the surface is Win32's.
- **Broadwell:** the core Vulkan 1.0 tier draws the whole game too (render passes, texture pages, the shaders built for Vulkan 1.0), but it
  has only been run here, forced (`OBNS_VULKAN_TIER=1.0`, and `OBNS_VULKAN_PAGED=16` for pages to be switched): the frames match the
  1.3 path's but for single pixels of the world, the frame cost is the same, and the validation layer reports nothing. Broadwell's
  GPUs are on Linux, which this mode doesn't run on yet.
- **Full screen:** not run, as it changes the display mode.
- **Metal:** Intel Macs aren't offered.
- **Metal's speed (on hold, the user's call, 2026-11-03):** the GPU's time per frame in the log, then frames in flight (the CPU and GPU
  each waits for the other now), encoding draws natively rather than call by call from Mono, memoryless depth and fewer passes, ASTC textures.
- **The options:** no entry yet; it's the switch alone.
- **The Lisuan:** not run there. It's what this is for: no shared images, and no OpenGL. Direct3D 11 alone is the one to try first.

## Rules

- OpenGL stays as a renderer and as the fallback.
- `OpenBVE/` stays untouched; OBNS's copies and new files go in `OBNS/overrides`.
- Object Viewer, Route Viewer and Train Editor stay on OpenGL. The menus before the game are separate and stay as they are.
- Android has its own renderers, and isn't part of this.
