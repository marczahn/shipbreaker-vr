# Development notes

Findings about the game and decisions made while building the mod. Newest sections at the bottom.

## Game
- Hardspace: Shipbreaker 1.3.0.1263554, Unity 2020.3.35f1, Mono, HDRP 10.x
- Game logic lives in `BBI.Unity.Game.dll` (`Assembly-CSharp` is nearly empty)
- Input: InControl (`GameplayActions`, `LynxControls`)
- Camera: `LynxCameraController` (static `MainCamera`, `ScreenCenter`, `ScreenWidth/Height`), driven by Cinemachine
- Decompile: `ilspycmd -p -o out -r <Managed> <Managed>/BBI.Unity.Game.dll` (needs `DOTNET_ROLL_FORWARD=Major` on .NET 8)
- Test setup: Meta Quest via Virtual Desktop (VDXR OpenXR runtime)

## XR bootstrapping
- The game ships without XR. OpenXR 1.2.8 + XR Management 4.2.1 are loaded at runtime: the XR settings come from an
  AssetBundle (`xrmanager`) built by the Unity helper project, the managed DLLs sit next to the plugin, and the native
  `UnityOpenXR.dll`/`openxr_loader.dll` plus `UnitySubsystemsManifest.json` go into `Shipbreaker_Data`.
- Single Pass Instanced shows only a distorted left eye (the game's shaders lack the stereo variants), so
  `OpenXRSettings.renderMode` is forced to MultiPass before the loader initializes.
- Patching an HDRP type by name with `[HarmonyPatch("Type", "Method")]` threw a `TypeLoadException` that aborted
  `PatchAll`; patches now use `TargetMethod()` + `AccessTools.TypeByName` and are applied class by class in a try/catch.

## Tool aiming
All tools raycast through `LynxCameraController.MainCamera.ScreenPointToRay(...)`:
`CuttingController` (595, 602, 621), `TetherController` (991), `Scanner` (237).
With XR enabled, `LynxCameraController.UpdateScreenDimensions` takes the eye texture size (e.g. 2688x2880) as
`ScreenWidth/ScreenCenter`, while the rays go through the flat camera (e.g. 2560x1440), so the cutter fired upwards.
`ScreenSizePatch` always uses the flat camera's pixel size.
Idea for controllers: an aiming camera on the hand that receives these calls.

## Body follows head
- The body rotates in `OrientationController.HandleAxisRotation` via `Rigidbody.MoveRotation(rotation * mouseDelta)`.
- A transpiler redirects that call to `VrBody.MoveRotation`: the mouse delta rotates the "playspace" P, and the body
  is set to P * head. Rotations from elsewhere (death spin, respawn) are detected and P is resynced.
- The VR rig is not a child of the game camera (that would double the rotation); it only follows its position.
- `HandleReorient` is skipped in VR; the reorient key levels P instead.

## UI
- The game renders its UI with a camera tagged `UI_Camera` inside HDRP custom passes (`HUDCustomPass`,
  `DrawUICameraCustomPass`) as a full-screen overlay. `CustomPass.Execute` is overloaded, so patches must name the
  `CustomPassContext` parameter type.
- Many menus and hints are Screen Space Overlay canvases; in VR Unity draws them into the left eye only.
  `VrCanvases` re-parents them to the UI camera while VR runs, also Screen Space Camera canvases on other cameras
  (the VR camera would otherwise see them in the world, in front of a camera it doesn't render with).
- `VrUi` renders the UI camera into its own RenderTexture once per frame and draws it per eye on a quad
  (`UI/Default`, ZTest Always, eye matrices via reflection from `HDCamera.xr`). In gameplay the quad is a visor in
  front of the head; elsewhere it is a fixed canvas in the playspace (F4 re-places it along the gaze and, in menus, turns the playspace to the gaze).
- The game switches its custom pass volumes off outside gameplay (e.g. pause), so the mod uses its own
  `CustomPassVolume` (AfterPostProcess) and only disables the game's UI passes for the VR camera.
- The main menu has no `LynxCameraController` and no UI camera: the VR camera attaches to the active menu camera and
  `VrUi` creates its own UI camera.
- TAA on the VR camera (MultiPass) looks washed out and makes objects jitter; SMAA is the default. The game itself
  uses no anti-aliasing.

## Helmet
`HelmetController.m_HelmetContainer` (under `Main Camera/Helmet_Rig`) follows the VR head, keeping its offset to the
game camera. It can also be hidden.

## Intro and loading screens
The VR rig lives in `DontDestroyOnLoad` and outlives scene changes. When the game camera is destroyed (scene teardown)
or `SceneLoader.LoadScreenVisible` is set, the VR camera culls everything and clears to black, so only the UI canvas
shows (menu placement). The next camera (`LynxCameraController.Start` or the highest enabled camera) takes over the
existing rig. `VrCanvases` restores and re-attaches the canvases when the UI camera changes. VR starts on the first
frame (`[Rendering] VrBeimStart`), so the launch intro video (`LynxVideoPlayer`, a RawImage on a canvas) is visible too.

The game's own custom post-processes (`DigitalDistortion` scanlines/CRT at shift start, RetroLookPro glitches) are
not XR-aware and fill the view with grey. `CustomPostProcessPatch` blits source to destination instead for the VR camera.

Level loads use `PRF_UI_LoadScreen` (core_assets_all, under `master_lazy_load/SceneLoader`): `LoadScreenCamera`
(depth 10, culling mask UI only, grey clear color, volume mask UI only) with the volume "Loadscreen Post" (CRT
aperture, VHS scanlines, vignette, bleed, bloom, film grain) and `LoadScreenCanvas` (Screen Space Camera). HDRP renders
every active game camera without a target texture into the headset (`XRSystem.SetupFrame` checks only that and
`xrRendering`), so this camera drew its grey background and non-XR scanlines over the VR image until the load
screen closed. While VR runs, `VrCamera` switches off UI-only cameras (their canvases are on the UI canvas anyway) and
`FindMenuCamera` never picks them. Our UI pass volume sits on layer 29 (unused by the game), which the VR camera
always adds to its volume mask; while the world is hidden the mask holds only that layer.

At 1.25x per eye (RTX 4070 Ti, 12 GB, 2688x2880 native) the level start ran out of video memory
(`Failed to create RenderTexture ... 0x8007000e` right after "Initializing Session 1", then a native crash). While the
world is hidden the eye resolution drops to 1.0x (native, still sharp; 0.5x made the load screen blurry) and the
shared HDRP targets shrink; afterwards `ApplyQuality` raises it again through the memory probe.

## Settings menu
The VR rows are clones of real option rows (bool, slider, list) appended to the Video tab. The game's logic for
those clones is intercepted via Harmony and redirected to the BepInEx config. Templates are taken from all tabs of
the options screen. The Gameplay tab was tried first, but its list does not scroll past its own rows, so the VR rows
hung invisible below it. The scroll content is grown by the added rows' height in case it has no ContentSizeFitter.

Keyboard/gamepad navigation (decompiled `UIInControlBridge`): Unity's navigation events are off; the selected row's
bridge reads the arrow keys from its serialized action set (FrontEnd or Paused). `LynxControls` loads only the set of
the current game state, so a bridge with the wrong set gets no input at all. With explicit navigation the bridge moves
only to an active, interactable target and never falls back to automatic search. Title-screen-only rows are
non-interactable in-game. `VrOptionsNavigation` therefore links only interactable rows (the last usable game row
leads down into the VR rows, the first VR row leads back up), recomputes that every 0.25 s while the list is visible,
gives the VR rows' bridges the action set of the game's rows, and logs the list and every selection change.

Selection jumping back up while scrolling by keyboard: Doozy's `UIButton` selects a row on pointer enter, and
`StandaloneInputModule` re-raycasts the mouse position every frame even when the mouse does not move. Keyboard
navigation scrolls the list under the resting cursor (in the pause menu it sits in the middle of the screen), so the
row sliding under it took the selection, one row further down each time. In lists with VR rows a pointer enter now
selects only if the pointer moved (`PointerEventData.delta`) or the mouse wheel was used in the last half second.

## World scale
Config `Bild/Weltmassstab` (also in the options menu) scales the VR rig. Unity derives the eye positions from the
camera's world transform, so the rig scale also scales eye separation and tracked head movement: above 1 distant
objects keep more stereo depth (hyperstereo) and sizes are easier to judge, but the world looks smaller. Visor,
menu canvas and helmet are scaled along so they keep their felt size. The log line "eye separation in the world"
shows whether the scale actually reaches the stereo rendering (about 63 mm times the scale is expected).

## Render resolution and video memory
`XRSettings.eyeTextureResolutionScale` reallocates every HDRP render target. At 2.0 (5376x5760 per eye) a 12 GB
RTX 4070 Ti ran out of memory (D3D11 error 0x8007000e in Player.log); after that even 1.0 failed and the image
smeared until the game was restarted. The plugin now estimates the limit from `SystemInfo.graphicsMemorySize`
(about 250 bytes per pixel and eye, half of the video memory as budget; 1.55 still failed on 12 GB), clamps the setting to it and applies a
change only after the slider has rested for a second. Before raising it, it allocates test textures of the new size; if the driver refuses one, the previous value stays.

HDRP's shared render targets never shrink: `RTHandleSystem.SetReferenceSize` keeps the largest width and the largest
height any camera rendered at, and the game never calls `ResetReferenceSize` (`HDRenderPipeline.ResetRTHandleReferenceSize`
is public but unused). A flat 3840x2160 view followed by VR at 2688x2880 therefore leaves every target at 3840x2880.
The plugin resets the shared targets to the eye size (or the screen size) two seconds after VR starts or stops and
one second after the render resolution goes down. A game crash during a mission load ran out of memory exactly when
the eye textures were recreated a second after VR started (1.0, then 0.95); a lower setting is now applied at VR start.

## Motion controller flying (proof of concept)
`ThrustController.Update` reads the thrust axes via `LynxControls.GetOneAxisInputControlValue` (`ThrustMove...Composite`)
and thrusts along its orientation transform (the body, or a steering transform). A postfix on that extension method adds
the controller thrust, converted into that transform's axes, so fuel, forces and the thrust HUD stay the game's.
Rotation from the controllers (roll, VTOL turns) is applied to the playspace in `VrBody`, so it needs body-follows-head.
Config `Steuerung/Controllersteuerung`, left controller only:
- Pavlov (point and twist): stick forward/back thrusts along the controller's aim pose (`PointerRotation`; the
  `deviceRotation` grip pose runs along the handle and pointed thrust upward). Holding the trigger stores the hand's
  orientation; twisting or tilting away from it rolls, pitches or yaws (8 degree deadzone, full 45 degrees/s at 45).
- Vtol: hold the trigger; moving the controller away from where it was when pressed thrusts that way (2 cm deadzone,
  full at 15 cm), turning it turns the body as above.
- Both: stick left/right snap-turns 30 degrees around the head's up axis (the playspace's up axis tilted the view after
  rolling). Pressing the stick presses the game's brake actions (`ThrustBrakeLeft/Right`). A postfix on
  `LynxControlExtensions.GetInputIsPressed` never ran because Mono inlines it, so a transpiler redirects the calls in
  `ThrustController.Update`.
The grip stays free for grabbing later. The left controller's name and features are logged once. OpenXR only reports
controllers because `VrXr` enables the Oculus Touch interaction profile, which the AssetBundle's settings carry disabled.

## Hab (the station between shifts)
The Hab is an extra scene (`Hab Container`, loaded additively next to the bay after a shift or at the start of a
career session). It has its own camera, `PRF_HAB_Camera_Navigation/Unity_Camera` (depth 2, all layers, sky clear,
`xrRendering` on), driven by `Hab3DController.CinemachineBrain`, which blends between about 20 virtual cameras (bed,
kitchen, terminal, suit, ...), each with Cinemachine Perlin noise as camera sway. On `GameState.Hab`,
`LynxCameraController.OnGameStateChangedEvent` switches the bay's main camera off (on again in `Gameplay`).

The VR rig used to stay on the main camera. In the Hab the headset then got two full stereo HDRP renders per frame:
our camera drew the whole bay with the ship from the player's position (invisible), then the Hab camera drew the Hab
on top. That doubled GPU time and render targets (at 1.25x the log showed `Failed to create RenderTexture ...
0x8007000e` after re-entering VR in the Hab), the visible image came from a camera without head tracking (the room
stuck to the head and swayed with the noise), and it painted over our UI pass, so the prompts of
`PRF_HAB_3DNav_UI_Canvas` (an overlay canvas: R, Tab, location markers) never showed.

Now a postfix on `OnGameStateChangedEvent` records whether the game has the main camera off (`VrHab.MainCameraOff`).
While it is off and a `Hab3DController` exists, the rig follows the Hab camera and switches it off like any game
camera; switching back never re-enables the main camera while the game wants it off. The playspace follows only the
Hab camera's heading (its pitch would tilt the room), the Perlin noise amplitude of the Hab's virtual cameras is set
to 0 while VR runs (the game itself toggles their enabled flag), and the helmet only follows the head while the rig
is on the main camera (it would otherwise come along from the bay). The Hab's overlay and screen-space canvases
(3D navigation prompts, the terminal `Hab ScreenStack` at 0.25 m) go onto the UI canvas like other menus.

The Hab scene is loaded while the load screen still shows (a career load starts in the Hab), and everything in it is
active, so `Unity_Camera` already renders then. In the flat game the load screen camera (depth 10) covers it. In VR
that camera is off and the Hab camera (depth 2) drew the Hab over our black image and the UI pass, without head
tracking: the headset seemed frozen while the game waited for a key. `VrCamera` therefore switches the Hab camera off
like the UI-only cameras whenever the rig follows another camera. `GameState.Hab` is posted on that key press and
the load screen hides afterwards (`SceneLoader.EndProcess`), so the Hab camera is checked every frame and the rig is
already on it when the Hab appears.

Cinemachine applies each virtual camera's lens to `Unity_Camera` every frame. Most Hab views have a far clip plane of
500 m, the window view 5000 m (the ship and the dock below), the comms view 5000 m and the Hab screen 2555 m. The VR
camera copied it only when attaching, so the window view was cut off at 500 m: the ship showed only in part, and only
near the edge of the view (the far plane is a distance along the view direction). The far clip plane is now copied
every frame.

Three views are "navigable screens" on a monitor: the one above the bed (`Entry` -> `WakeUpMonitor`, the good morning
screen after waking up), the terminal (`Comms` -> `HabScreen`) and the workbench (`Workstation` ->
`Workstation_Screen`). Their virtual cameras sit 0.35 to 0.45 m in front of a `PRF_Computer_Terminal_Hab`, and
`Hab3DLocationNavigation.Navigate` sets the trigger `FadeOut` on the monitor's `LYNX_Screensaver` animator: the screen
in the room goes dark and its content comes as the `Hab ScreenStack` overlay over the whole picture. (`Exit` is a
navigable screen too, without a monitor.)

In VR that overlay was on the menu canvas (about 66 degrees wide, 2.4 m away and drawn over everything) while the rig
followed the virtual camera to 0.35 m in front of the monitor: the room showed around the canvas, and the eyes got the
canvas at 2.4 m and the monitor at 0.35 m at once. Now the canvas lies on the monitor. Each terminal prefab has a world
space canvas for its "locked" screensaver (`LYNX_Screensaver_Locked/Canvas`, inactive, 0.56 x 0.31 m) that covers
exactly its screen; `VrHab` maps the virtual cameras of every `NavigationOption` with a `ScreenAnimator`
(`CameraToGoTo`, `CameraToSwapToAfterBlend`, from `m_NavigationOptions` and on `Navigate`) to that canvas. While one of
them is active, `VrUi` draws the UI texture on the canvas's corners (as large as fits at the texture's aspect) and the
rig puts the head 0.7 m in front of the screen along its normal (about 43 degrees wide), with the heading of the
virtual camera. The monitor above the bed faces down onto the pillow; pulled back, the head would be in the mattress,
so there the head stays where the game's camera is (0.44 m). The rig and the canvas change spot only once the view is
black, like any other Hab move. The game's `FadeOut` of the monitor stays, as in the flat game the overlay is over the
dark monitor; a postfix on `Navigate` resets it only for a screen whose monitor canvas was not found, where the overlay
is on the menu canvas in front of the player and the dark monitor would otherwise stay in the room as a black
rectangle.

On a monitor the canvas gets a screen look (`[Anzeige] MonitorEffekt`, F7 at a monitor switches it), since our pass
runs after post-processing and the flat UI looked pasted onto the room: the UI texture slightly dimmer and cooler,
128 fine scan lines and edges darkened over the outer 10 % (one black texture), a faint sheen from the upper left
(white), and light around the screen over 12 % of its height that the game's bloom would give a lit screen. All of it
is drawn with `UI/Default` and textures made at runtime for the canvas's aspect; the plugin has no shaders of its own
(an AssetBundle with shaders would need the Unity editor).

Lying in bed (`Entry` after waking up and `WakeUpMonitor`) the Hab camera looks up at the ceiling (57 and 63 degrees).
With only its heading the player looked straight ahead at the wall while the monitor with the morning menu was
overhead. For a Hab camera looking up more than 30 degrees the playspace now takes its pitch too, as if lying on one's
back; the change happens with the cut, inside the fade.

After loading into the Hab (and when waking up), a black area about the size of the menu canvas hung in the room
in VR, as if a piece of the world were missing, and was gone a few seconds later. Most likely it was the game's
full-screen fades to black (`FadeScreen`, `HAB_General_Fade`, `WakeUpFade`, `QuitFromHabFade`; the dump 6 s after
entering showed them done and nothing else dark around the player), which in VR land on the UI texture like any other
canvas. On the visor in gameplay that covers the view as meant; on the menu canvas fixed in the room it is a black
rectangle with the room around it. In menu mode `VrCanvases` puts these canvases on our pass layer (which neither the UI
camera nor the VR camera draws) and reports how dark they would make the screen; `VrUi` darkens the whole view by that
under the canvas, so subtitles and prompts stay on top as in the flat game. `VrCanvases` also scans inactive canvases
now: a canvas the game switches on later used to show for up to half a second where the game put it, which for the
Hab's fades is 25 cm in front of the Hab camera, in the room in front of the VR camera.

The prompt to answer a call at the terminal (`Comms_UI/Comms Interact Button`) never showed in VR. Its
`UITrackTransform` sets the prompt's world position to `WorldToScreenPoint` of the spot it tracks, which only works on
an overlay canvas (one unit per pixel). On the canvas attached to the UI camera the prompt landed far off it. A postfix
turns the screen point into the matching point on the canvas (`RectTransformUtility.ScreenPointToWorldPointInRectangle`
with the UI camera). The other Hab prompts are placed by `Hab3DLocationNavigation` through `localPosition` scaled to the
canvas and work as they are.

## Performance in the Hab
The Hab stuttered in VR while the bay ran smoothly at the same settings (Ultra, 1.25x, 3360x3600 per eye). What the
scene data shows (`scenes-gameplay_scenes_all`, Hab scene CAB-11cf266b; profile in `vfx_assets_all`):
- Ambient occlusion makes the difference. The bay's global `BaseSessionPostprocessProfile` uses HDRP's Low level (half
  resolution, 4 steps). The Hab's local volume `HAB_PostprocessProfile2` (priority 15) sets a custom level: full
  resolution, 16 steps, up to 50 pixels radius. Per eye that is roughly 30 times the work, at 2 x 12 million pixels.
- Lights are not the cause: 45 lights, but almost all are baked; only two realtime lights cast shadows (512 and 1024).
- The single reflection probe is baked. The space truck viewer (`Root/CameraRig/Main Camera`, `SpaceTruck3DHABController`)
  renders into a texture every frame, in the flat game as well.
- The Hab camera has no custom frame settings, so it uses the HDRP asset's defaults; the bay's main camera switches
  off contact shadows, subsurface scattering, transmission, depth of field and a few others. None of those have a
  volume override in the Hab, so they should cost little.

`VrPerformance` adds a global volume on our pass layer (priority 1000, only the VR camera has the layer in its mask)
that sets the occlusion quality back to Low (`[Bild] Umgebungsverdeckung`, also in the options menu: Low, Off, Game
default). It also logs frame times every 30 s and whenever the place or a setting changes, and once per camera switch
which other cameras render, to compare bay and Hab from one Player.log.

With the occlusion at Low the Hab still ran at a steady 22.2 ms per frame (45 fps), and `XRDevice.refreshRate` read
45 Hz instead of 90 Hz: Virtual Desktop's Synchronous Spacewarp halves the rate when the app misses 90 fps and
synthesizes every second frame. During the Hab's fast Cinemachine moves those synthesized frames smear sideways
(ghosting). Entering the Hab at 1.25x also failed several render target allocations every time, because the bay stays
loaded next to the Hab. The resolution in the Hab is therefore capped at 1.0x (native), and the rig moves onto the
Hab camera in `VrCamera.LateUpdate` as well, since the game can post `GameState.Hab` and hide the load screen in the
same frame after `Plugin.Update` ran (the bay then showed for a frame and the resolution went up for it).

At 1.0x the Hab still did not reach 90 fps, so in VR its camera moves are replaced by fades. The Hab brain blends with
`EaseInOut` 1 s by default plus a `CinemachineBlenderSettings` asset. While the rig follows the Hab camera in VR,
`VrHab.CutBlends` sets the default blend to `Cut` and removes the custom blends (both restored when VR stops or the rig
leaves the Hab), so the flat game keeps its moves. When the brain's `ActiveVirtualCamera` changes, the rig stays on
the old spot while the view fades to black (0.15 s), jumps while black (0.05 s) and fades in on the new spot (0.25 s).
The game's own follow-up switch (`CameraToSwapToAfterBlend`, about 0.1 s after the cut) lands inside the black.
`Hab3DController` only waits for `!IsBlending`, so cuts don't change its logic. The black is drawn in our pass after
the UI, as a quad with identity matrices.

Close to a monitor (0.7 m) the synthesized frames show: nodding the head, the screen and its frame seem to move back
and forth slightly (Player.log of 22.10.: the Hab at 22.4 ms per frame, headset at 45 Hz). The space truck viewer
(`SpaceTruck3DHABController`, camera `Root/CameraRig/Main Camera`) renders into a 960x540 texture every frame, an extra
HDRP camera with its own culling, shadows and lighting, while only the `TruckImage` of the truck screen in the
`Hab ScreenStack` shows it. In VR in the Hab `VrPerformance` now switches that camera off while none of the raw images
showing its texture is drawn (inactive, on a disabled canvas or under a canvas group at alpha 0). The frame time line
also gives the main thread's time from `Update` to the end of rendering and, where Unity's frame timing is enabled,
CPU and GPU time, to tell whether the processor or the graphics card holds the Hab at 45 fps.

## Diagnostics
F8 (`VrDiagnostics`) is for things only seen in the headset. It writes into the log: game state, head pose, the camera
the rig follows, where the UI canvas hangs, every active root canvas (render mode, camera, layer, whether `VrCanvases`
attached it, direction and distance from the head for the non-overlay ones, and its dark, large or specially shaded
graphics, and its visible texts with their position on the canvas, so a prompt that is off the canvas or covered
shows up), and the visible renderers within 4 m that reach above eye height. It also runs on its own 6 s after the rig
enters the Hab, for things that show right after waking up. Directions are relative to the gaze: "ahead 10, up 60"
is 10 degrees right of where the head points and 60 degrees above the horizon. It also saves the UI texture and the
game window as PNG files to `BepInEx\ShipbreakerVR-F8`.

## Roadmap
1. Inventory – done
2. Foundation (BepInEx, plugin, camera hook) – done
3. Seeing VR (stereo, head tracking) – working
4. Playing VR: controllers, tools in hand, bindings
5. Using VR: HUD as visor, menus with a pointer – in progress
6. Comfort and performance
