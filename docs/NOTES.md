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
  `VrCanvases` re-parents them to the UI camera while VR runs.
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

## Roadmap
1. Inventory – done
2. Foundation (BepInEx, plugin, camera hook) – done
3. Seeing VR (stereo, head tracking) – working
4. Playing VR: controllers, tools in hand, bindings
5. Using VR: HUD as visor, menus with a pointer – in progress
6. Comfort and performance
