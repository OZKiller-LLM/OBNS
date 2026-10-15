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

## Still to do

| Feature | Upstream | Notes |
|---|---|---|
| Wireframe view | DEBUG_WIREFRAME (glPolygonMode) | GL ES has no polygon mode: lines drawn from the triangles (GL ES), polygon mode or line lists (Vulkan) |
| Normals view | DEBUG_NORMALS | upstream's RenderFace draws them; Android's native face path must step aside |
| Touch areas view | DEBUG_TOUCH_MODE | Android's touch areas are CabTouch |
| Event markers | SHOW_EVENTS (Events.cs, cubes) | port to AndroidRenderer |
| Switch change map | SWITCH_MENU (SwitchChangeDialog) | needs an Android screen for it |
| Particle effects (MSTS exhaust, steam) | TrainBase.UpdateParticleSources each frame | not called on Android yet |
| Shadows | cascaded shadow maps (CascadedShadowMap) | off-screen depth targets on GL ES and Vulkan |
| Motion blur | MotionBlur.RenderFullscreen | off-screen colour target |
| Controller axes | POWER_HALFAXIS, BRAKE_HALFAXIS, SINGLE_FULLAXIS, REVERSER_FULLAXIS | Android controllers' analog sticks/triggers |
| Options: time acceleration factor, camera transitions | options.cfg | Android Options page |

## Not applicable on Android

- RailDriver desk (a USB device upstream drives through its Windows driver).
- The viewers and editors (Object Viewer, Route Viewer, Train Editor).
