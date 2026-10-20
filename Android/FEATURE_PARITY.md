# Feature parity with upstream OpenBVE (1.14.1.2)

What upstream's program does that the Android port must also do. The libraries (OpenBveApi,
LibRender2, TrainManager, RouteManager2, the plugins) are upstream's own code, compiled for
Android; the gaps are where Android replaces upstream's main program (`OpenBVE/source/OpenBVE`)
with its own: the camera (`AndroidCamera` for World.cs), the controls (`AndroidControls` for
ProcessControls), the renderer (`AndroidRenderer` for NewRenderer), the host, the sound update,
the menus.

## Done in 1.2

| Feature | Upstream | Android |
|---|---|---|
| Interior view direction as yaw/pitch/roll | World.cs (1.14.1.x) | AndroidCamera |
| Glide from the exterior view into the cab | World.cs, GameWindow.cs | AndroidCamera (on by default, 0.4 s) |
| Options: camera transitions (interior, exterior, duration), time acceleration factor | Options (camera options, advanced) | Android Options page, upstream's strings |
| Head-out views (left, right) | CAMERA_HEADOUT_LEFT/RIGHT | AndroidCamera |
| Cab view without the panel | CAMERA_INTERIOR_NOPANEL | AndroidCamera |
| Time acceleration | MISC_TIMEFACTOR | AndroidControls, AndroidTrainSession (camera in real time) |
| Mute | MISC_MUTE | AndroidControls |
| Back-face culling toggle | MISC_BACKFACE | AndroidControls |
| Headlights, pantographs, main breaker, sanders, gears, uncoupling, constant speed, contact the signaller | ProcessControls.Digital | AndroidControls |
| MSTS horn also rings the bell | 1.14.1.1 | AndroidControls |
| Route microphone sounds | Track.MicSound, PLAY_MIC_SOUNDS | AudioCapture on OpenAL Soft (OpenSL ES), RECORD_AUDIO asked when first switched on |
| Developer overlays | MISC_INTERFACE, DEBUG_ATS, DEBUG_BRAKE | upstream's Overlays.Debug.cs compiled in |
| Accessibility read-outs and screen reader | ACCESSIBILITY_*, Tolk | Android text-to-speech while TalkBack (or another screen reader) runs |
| Route map in the game's font | Loading.cs (1.14.1.2) | AndroidRouteLoader |
| Wireframe view | DEBUG_WIREFRAME (glPolygonMode) | GL ES: each polygon drawn as its edge loop; Vulkan: line loops through a scratch index buffer |
| Normals view | DEBUG_NORMALS | the native face path steps aside for upstream's RenderFace |
| Touch areas view | DEBUG_TOUCH_MODE | CabTouch's areas, drawn after the cab |
| Event markers | SHOW_EVENTS (Events.cs, cubes) | upstream's Events.cs compiled in, drawn by AndroidRenderer |
| Particle effects (MSTS exhaust, steam) | TrainBase.UpdateParticleSources | AndroidRenderer, after the alpha faces |
| Switch change map | SWITCH_MENU (SwitchChangeDialog) | SwitchMapPanel: the touch interface's own map of every track near the train, the way ahead highlighted, switches to tap and change (SW button, or Ctrl+F9) |

## Still to do

| Feature | Upstream | Notes |
|---|---|---|
| Shadows | cascaded shadow maps (CascadedShadowMap) | off-screen depth targets on GL ES and Vulkan |
| Motion blur | MotionBlur.RenderFullscreen | off-screen colour target |
| Controller axes | POWER_HALFAXIS, BRAKE_HALFAXIS, SINGLE_FULLAXIS, REVERSER_FULLAXIS | Android controllers' analog sticks/triggers |

## Not applicable on Android

- RailDriver desk (a USB device upstream drives through its Windows driver).
- The viewers and editors (Object Viewer, Route Viewer, Train Editor).
