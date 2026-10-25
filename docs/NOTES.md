# Development notes

Findings about the game and decisions made while building the mod. Newest sections at the bottom.

## Game
- Hardspace: Shipbreaker 1.3.0.1263554, Unity 2020.3.35f1, Mono, HDRP 10.x
- Game logic lives in `BBI.Unity.Game.dll` (`Assembly-CSharp` is nearly empty)
- Input: InControl (`GameplayActions`, `LynxControls`)
- Camera: `LynxCameraController` (static `MainCamera`, `ScreenCenter`, `ScreenWidth/Height`), driven by Cinemachine
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
  front of the head; elsewhere it is a canvas fixed in the playspace, upright at eye height in the direction the head
  faces. It follows the eye height once the eyes are more than 30 cm above or below its center (standing up, sitting
  down). F4 (or B + Y on the controllers, `VrCamera.UpdateCenterCombo`) places it in front of the head again and, in menus, turns the playspace to the gaze. The VR tab row "Center view" (`VrOptions.ActionRow`, a toggle row
  without the toggle) does the same.
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

Loading the bay, the rig switches cameras twice behind the load screen: first to a `Main Camera` that `Plugin.Update`
finds while `LynxCameraController.MainCamera` is not set yet, then, from the `LynxCameraController.Start` postfix, to
the controller's main camera. `Retarget` used to switch the previously followed camera back on (as the game had it),
and the next line in every bay load read `Other cameras rendering next to 'Main Camera': 'Main Camera' (depth -1,
screen)`: a second world camera rendering into the headset next to the rig's black. Marc saw a view of the station
flash up about two thirds into the load screen. While VR runs the previous camera now stays off and joins the
cameras `SilenceOtherCameras` restores when VR stops (each as far as `GameWantsEnabled` says). Not yet confirmed in
the headset. A second effect, the view going dark for a blink at another point of the load, is still open; see the
load trace under Diagnostics.

## Settings menu
The VR rows are clones of real option rows (bool, slider, list) in their own "VR" tab. The game's logic for those
clones is intercepted via Harmony and redirected to the BepInEx config. Templates are taken from all tabs of the
options screen. Earlier the rows were appended to the Video tab (the Gameplay list does not scroll past its own rows);
that is still the fallback if the tab cannot be built.

The tab (`VrOptionsTab`): `UnifiedOptionsScreen` holds parallel arrays of screen controllers, Doozy `UIView`s and
`GenericTab` buttons and switches tabs by index (`ShowTab`, `Next/PreviousOptionTab`). On its first `OnEnable` the
video view is copied (controller and rows removed, `VrOptionsScreen` and the VR rows added, list height fitted, view
renamed so Doozy's lookups by category/name don't hit it; a `UIView` also shows and hides its child views by
its own category/name, so child views with the video name would be renamed too, though the video view has none), the last tab button is copied with its click redirected to
`ShowTab(new index)`, and both are appended to the arrays. `UIView.Show` does not hide other views and the game's way
of hiding the previous tab does not know the copy, so after every tab switch all other views are hidden.
The video tab's description field (`m_DisplayPerformanceDescription`) is not assigned in the game, so the VR view gets
its own text strip at the bottom (the list's viewport is shortened for it) showing the hint of the selected VR row
(`VrOptionsScreen.Update`, from the EventSystem selection, which the mouse pointer also sets). The first template
lookup searched under the `UnifiedOptionsScreen` object, which holds no rows; the templates come from the views'
container ("Options Body Container", a VerticalLayoutGroup that lays out the active views).

Empty VR page: the video view is copied before it was ever shown, so its `Visibility` still holds the default
`Visible`. `UIView.Show` returns early for a visible view and never enables its canvas, and its first `Start` would apply
`BehaviorAtStart` on top. The copy starts as `NotVisible` with `BehaviorAtStart = DoNothing`. While invisible but active
it also took space in the container's layout. A tab switch logs every view's state (active, visibility, canvas, alpha,
rect, list rows).
Labels, values and hints are German when the game's language (`LynxGameUserOptions.Language`) starts with "de",
otherwise English.

Keyboard/gamepad navigation (`UIInControlBridge`): Unity's navigation events are off; the selected row's
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

## Head offset
Configs `Bild/KopfHoehe` and `Bild/KopfSeitlich` (also in the VR tab, -10 to 10 cm each) shift the eyes against the
game camera after recentering. Height runs along the playspace's up axis, sideways (positive is right) along the head's
heading within the playspace, so the offset stays on the same side of the body when the player turns. Both are in the player's own centimeters and
scale with the world scale like the tracked head movement. With the helmet following the head, `VrHelmet` subtracts
the offset again, so the eyes move inside the helmet instead of taking it along. Background: Raik's head sat too low
in the game, which Marc could not reproduce; the cause is unknown, the offset lets each player correct it.

Their slider rows use whole steps (`SliderRow.Step = 1`): `Slider.wholeNumbers` for dragging, and the game's
`OptionEntrySlider` fields `m_ControllerSliderStep`/`mSpeed` set to the step with `m_AccelerateWhenHeld` off, because
by default the stick moves the value by a small fraction per tick and speeds up while held.

## Menu stick
In menus `VrMenuInput` passes only the stronger axis of the left stick on to InControl. The game navigates rows with
the stick's up/down, so a sideways push that was slightly tilted jumped to the next row while adjusting a slider.

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
Rotation from the controllers is applied in `VrBody`: to the playspace with body-follows-head, otherwise to the body
(`turn * gameTarget` in the redirected `MoveRotation`), which the camera follows.
Config `Steuerung/Controllersteuerung` (Aus/Vtol; the earlier "point and twist" Pavlov scheme was removed for now):
- Hold the left trigger; moving the controller away from where it was when pressed thrusts that way (2 cm deadzone,
  full at 15 cm), measured at an estimated wrist point 8 cm behind the grip pose: measured at the controller, tilting
  the hand to turn swung it past the deadzone and added thrust in a direction nobody asked for. Rotation uses the aim pose (`PointerRotation`; the `deviceRotation` grip pose runs along the handle).
  The hand's roll away from the reference (`eulerAngles.z`) rolls the body around the head's forward axis (8 degree
  deadzone, full 45 degrees/s at 45). Turning and pitching by twisting the hand worked, but holding the trigger and the
  hand angle at the same time was tiring, so both moved to the stick.
- Right stick (no trigger needed, gameplay only; in menus it works them as a gamepad): left/right turns around the head's up axis, up/down pitches around its right axis
  (stick up looks up), 15% deadzone, full 45 degrees/s at full deflection. It was the left stick until the flight
  globe; it moved to the right one so the left hand only flies (thrust, roll, brake) and the right hand only steers.
- Config `Steuerung/Drehen` (Fliessend/Snap30/Snap45/Snap90) makes the right stick's turn one step per flick (press at
  0.7, release below 0.3), around the head's up axis like the smooth turn (the playspace's up axis tilted the view after
  rolling). An earlier fixed 30 degree snap turn on the right stick was dropped when turning moved to the left stick. Y (left `secondaryButton`) presses the game's brake actions (`ThrustBrakeLeft/Right`); it used to be the
  left stick press. A postfix on
  `LynxControlExtensions.GetInputIsPressed` never ran because Mono inlines it, so a transpiler redirects the calls in
  `ThrustController.Update`.
The grip stays free for grabbing later. The left controller's name and features are logged once. OpenXR only reports
controllers because `VrXr` enables the Oculus Touch interaction profile, which the AssetBundle's settings carry disabled.

### Flight globe
`VrFlightGlobe` draws a small globe at the bottom of the visor (config `Anzeige/Flugkugel`: Immer/BeimFliegen/Aus;
`FlugkugelGroesse` = radius relative to the visor height, `FlugkugelX/Y` = position on the visor from -0.5 to 0.5). The
defaults (radius 0.06, X -0.159, Y -0.38) put it in the gap between the scanner/helmet lamp keys and the hull/suit bars,
its lowest point on the baseline of the lower row, measured on an F8 dump of the gameplay HUD (2560x1440: the gap runs
from x 774 to 971, the baseline is at y 1354). Its radius is the full-thrust offset (15 cm), a small inner globe the
deadzone (2 cm). A 3D cross (the hand's axes; the upper arm longer and capped so up stays clear at any roll) sits at the
hand's offset from the reference (tracking space) and turns by the hand's rotation relative to the reference in the
controller's own frame, the same rotation whose `z` is the roll. Everything is one light HUD color; while the trigger is
held, the cross and the line from the center to it take the orange of the game's gauge bars (255, 139, 0), the line
faint at the deadzone and solid at full thrust. The globe is fixed to the visor (Marc wanted a steady instrument), its
axes along and across the view; the offset (tracking space) is turned into the head's frame
(`Inverse(HeadRotation)`). Shown in raw tracking axes it was off by the head's yaw against the tracking space's
forward: facing 90 degrees away from it, the cross moved 90 degrees off. A first version kept the globe aligned with the playspace, so it swung around when the head turned; a 10 degree
tilt towards the eyes was tried and dropped. After the trigger is released the cross eases back to the center. Drawn per
eye right after the visor quad, with `Hidden/Internal-Colored` (vertex colors, `_Cull` Back, `_ZTest` Always;
`UI/Default` without culling as fallback) on meshes built at runtime. The first draw logs `Flight globe drawn (<shader>,
radius ...)`.

## Menu control with the motion controllers
The game's menus are fully usable with a gamepad. Each selected control has a `UIInControlBridge` that reads the
navigation composite of the action set for the current game state (`FrontEnd` in the main menu and the Hab, `Paused` in
the pause menu, `NIS` in cutscenes; `LynxControls.AdjustControlsForGameState` loads only that one). Default gamepad
bindings (`LynxConfigurableControlOptions`): Confirm Action1, Decline Action2, Navigate left stick and D-pad, Scroll
right stick, tabs bumpers, Start/Pause `GenericStart` (= Menu, Start or Options), Back `GenericBack` (= View/Back).
Actions read `InputManager.ActiveDevice`, the device with the most recent input. InControl runs in manual mode and is
updated from `LynxControls.Update`.
`VrMenuInput` is an InControl `InputDevice` (class Controller, style XboxOne, so the prompts show Xbox glyphs) attached
with `InputManager.AttachDevice` once InControl is set up. In its `Update` it reads both XR controllers and maps them
by place: sticks = sticks, A/B/X/Y = A/B/X/Y, triggers = bumpers, grips = analog triggers, right stick pressed =
R3. A + X together is Menu (Start), only in `Gameplay` and `LoadingInProgress` (it cycles the loading tips; A and X then
send nothing else): the left menu button belongs to Virtual Desktop, the left stick press (the first choice) was too easy
to hit while steering with the left stick, and in menus the Doozy button bridges read Start like Confirm on the
selected button. No View button (Back; `BackButtonService` treats Decline/B the same, so B covers Esc in menus) and no D-pad. In
`Gameplay` it reports only A + X, which opens the pause menu; everything else stays with controller flying. A press held across a game state change is ignored until released, so the
press that opens the pause menu does not close it again. B + Y together centers the view (`VrCamera.CenterComboHeld`,
from the press until both are up again): then B and Y send nothing and Y does not brake. Pressed buttons are logged as `Menu control: ...`.
InControl makes a device active only when its last input is strictly newer than the active one's. An "Unknown
Device" (probably a joystick exposed by the runtime) had input every frame and kept the role, so the first version's
presses never reached the game. A postfix on `InputManager.UpdateActiveDevice` gives our device the role while it has
input. The device list is logged on attach and on the first presses.

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
every frame, and in the Hab it is at least the farthest of the Hab's virtual cameras (5000 m): the game sets each
view's far plane for what its camera looks at, but in VR one looks out of the window from anywhere. At the terminal
(2555 m) parts of the earth outside were cut off depending on the head.

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
them is active, `VrUi` draws the UI texture on the canvas's corners (as large as fits at the texture's aspect), moved
1 cm along the normal onto the glass (the screensaver's canvas floats about that far in front of it), and the rig puts
the head 0.7 m in front of the screen along its normal (about 43 degrees wide), with the heading of the virtual
camera. The monitor above the bed faces down onto the pillow (`WakeUpMonitor` sits 0.44 m below it): there the rig
stays where it was (`Entry` after waking up, about 1 m away), without a fade going in or back out; only if the Hab
opens right at that monitor does the head go where the game's camera is. Otherwise the rig and the canvas change spot
only once the view is black, like any other Hab move. The Hab's 3D waypoint markers (`PRF_3DHAB_WaypointMarkers`, the
green arrows pointing at the terminal) stuck into the monitor from 0.7 m; in the flat game the overlay covers them, so
in VR their renderers are hidden (`forceRenderingOff`) while the rig is at a monitor. The game's `FadeOut` of the monitor stays, as in the flat game the overlay is over the
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
showing its texture is drawn (inactive, on a disabled canvas or under a canvas group at alpha 0). That alone did not
get the Hab to 90 fps. The frame time line now also gives the main thread's time from `Update` until rendering starts
(game logic), the time rendering takes on the main thread (including the wait for the headset's next frame) and, if
the runtime reports it, the app's GPU time (`XRDisplaySubsystem.TryGetAppGPUTimeLastFrame`). Unity's
`FrameTimingManager` reports nothing in this build, and a main thread time from `Update` to the end of rendering
came out at the full frame time, because the wait for the headset falls inside it.

The next log (22.10., Hab at 1.0x and 0.75x) settled it. The runtime reports no GPU time either, and the wait for the
headset falls between `Update` and the start of rendering: at 45 Hz "game logic" read about 15 ms, at 90 Hz 3.5 to
3.9 ms, with rendering on the main thread at 6 ms in both. At 0.75x the Hab ran at 90 Hz (11.2 ms per frame, slowest
5 % from 12.3 ms) and the wobble at the monitors was gone; at 1.0x it fell back to 45 Hz. So the GPU sets the limit,
and the main thread (about 10 ms at 90 Hz) has little room left.

The frame settings the game gives the bay's main camera (`GameCameras/Main Camera`, override mask in the scene data)
switch off, among others, contact shadows, shadowmask, transmission, subsurface scattering, depth of field and sky
reflection; the Hab camera uses the HDRP defaults. Most of those cost little where nothing uses them, and shadowmask
and sky reflection change the Hab's baked lighting, so they were left alone.

Instead the Hab has its own render resolution (`[Bild] AufloesungStation`, in the options menu as "Station
resolution", 0.75 by default, at most 1.0x), independent of the one for everywhere else (`[Bild] Aufloesung`).
A first version lowered it automatically by 0.05 while the median frame time was above 1.3 headset intervals. It
stopped at 0.8x, where the view still wobbled: the Hab ran at 90 Hz on average (11.4 ms per frame) but missed about
3 % of the frames, and each of those is synthesized. At 0.7x it was steady. Rather than tuning a threshold that
depends on the runtime and the scene, the setting is now set by hand. The frame time line gives the share of missed
frames (1.4 to 4 intervals of the highest refresh rate seen, the headset's own; longer ones are hitches) to help with
that.

## Play area in the bay

The bay's boundary is `PlayableArea` (`GameSession.PlayableArea`, in the bay scene `PRF_Playable_Area`): node
transforms and three radii, class defaults 100/125/130 m; the bay sets 40/55/55 m around 83 nodes spanning
96 x 94 x 176 m. `PlayableAreaUIController.Update` checks the player against the first two every frame (within 40 m
of any node: safe; up to 55 m from the nearest: visor warning; beyond: a 10 s countdown, then damage).
`PlayableAreaSystem` gives every object further than the third radius from all nodes an `OutOfBounds` component, and
`OutOfBoundsSystem` destroys it after `SalvageSettings.OutOfBoundsDestructionDelay`. Nothing else in the game's
assemblies reads the radii.

Lifting all three while VR runs (PR #24, a postfix on `PlayableArea.Awake` writing the private fields) worked, but the
test run missed frames and ran out of video memory (`Failed to create RenderTexture ... 0x8007000e` right after the
session started), with about 50,000 more loaded objects at the bay load than the comparison run. Those were counted
before the boundary could change anything, so a different ship is the likelier cause, but it was not settled. Marc
parked the change for a later performance audit; the PR is closed, its commits stay under `refs/pull/24/head`.

## Diagnostics
F8 (`VrDiagnostics`) is for things only seen in the headset. It writes into the log: game state, head pose, the camera
the rig follows, where the UI canvas hangs, every active root canvas (render mode, camera, layer, whether `VrCanvases`
attached it, direction and distance from the head for the non-overlay ones, and its dark, large or specially shaded
graphics, and its visible texts with their position on the canvas, so a prompt that is off the canvas or covered
shows up), and the visible renderers within 4 m that reach above eye height. It also runs on its own 6 s after the rig
enters the Hab, for things that show right after waking up. Directions are relative to the gaze: "ahead 10, up 60"
is 10 degrees right of where the head points and 60 degrees above the horizon. It also saves the UI texture and the
game window as PNG files to `BepInEx\ShipbreakerVR-F8`.

`VrLoadTrace` covers what happens too fast to see. While the rig hides the world (load screens, intro) and for 3 s
after, it logs every frame in which the picture changes (`Load trace frame N: ...`): the cameras that rendered (from
`RenderPipelineManager.beginCameraRendering`, the rig's own as `VR`, others with their path and depth), whether our UI
pass drew the canvas, the eye texture size, whether the load screen is on, whether the world is hidden and which
camera the rig follows. Frames more than 50 ms after the previous one get a line of their own. At most 400 lines per
load.

## Reloading without restarting the game
For development the plugin can be reloaded while the game runs, with BepInEx's ScriptEngine (BepInEx.Debug, release
r11: `ScriptEngine.dll` into `BepInEx\plugins`). The plugin DLL and its PDB then live in `BepInEx\scripts` instead of
`BepInEx\plugins\ShipbreakerVR` (both at once and ScriptEngine refuses the second copy). ScriptEngine reads the
assembly with symbols, and Mono.Cecil most likely throws without the PDB, so it goes along. The OpenXR DLLs and the
AssetBundle stay where they are. Settings in `BepInEx\config\com.bepis.bepinex.scriptengine.cfg`: `LoadOnStart = true`
(otherwise the mod only loads with the first F6) and, for reloading on every copy of a new DLL,
`EnableFileSystemWatcher = true` (reload `AutoReloadDelay` seconds after the file changed). F6 reloads by hand.

How a reload goes: ScriptEngine destroys its object with the plugin on it, then a frame later loads the DLL from memory
under a new assembly name (`ShipbreakerVR-<ticks>`) and adds the new plugin.
- `Plugin.OnDestroy` hands everything back to the game, as stopping VR does: the VR tab and the VR rows leave the
  options screen (`VrOptionsTab.RemoveAll`), the InControl device is detached, the game cameras and the silenced ones are
  switched on as the game wants them, the Hab's sway and blends come back, the truck viewer renders again, the canvases
  return to their render modes and layers, the helmet goes back onto the body, the rig is destroyed right away (with
  the UI pass and the quality volume), the render pipeline events are removed, and the Harmony patches are undone. Not
  when the game quits.
- The Harmony ID contains the assembly name, so the old instance's `UnpatchSelf` only removes its own patches.
- OpenXR keeps running. The AssetBundle with the XR settings cannot be loaded twice, and `XRGeneralSettings.Instance`
  survives the reload, so the new instance takes both over (`VrXr.Adopt`) and does not toggle VR at launch (that would
  stop it). Its rig then starts like at VR start: recentered on the current head pose.
- `VrHab.MainCameraOff` came from game state events that are long past; after the old instance gave the main camera
  back to the game, its enabled flag tells whether the game shows the Hab (`VrHab.Adopt`).

What a reload cannot change: the OpenXR setup (`VrXr.LoadSettings`: render mode, controller profile), which runs once
per game session; that needs a restart. Reload with the options menu closed; an options screen open on
the VR tab falls back to the first tab. Mono never unloads assemblies, so every reload leaves the old code in memory
(about 140 KB each), which is harmless for a test session.

## Performance audit: work done twice per eye
The bay ran at 45 Hz at 1.05x (22-24 ms per frame), and at 90 Hz the main thread already used about 10 of the 11.1 ms
(NOTES above, "Performance in the Hab"). So both the GPU and the CPU are close to the limit on an i7-7700K with an
RTX 4070 Ti. An audit against the HDRP 10.9.0 source (the game ships some HDRP 10.x) found
these points. None of them was measured in the headset yet; the log lines below are there to measure them.

What MultiPass does per eye (HDRP `HDRenderPipeline.Render`, `XRSystem.CreateLayoutFromXrSdk`): every eye is its own
`XRPass`, its own `HDCamera` (history, exposure, volume stack) and its own render request. Most of that work depends on
the view and cannot be shared (G-buffer, lighting, SSAO, SSR, volumetrics, post-processing). Two things don't:
- Culling. HDRP reuses the first eye's culling results for the second eye only if the runtime gives both passes the
  same culling pass (`cullingPassId`). Otherwise it culls the scene, every light's shadow casters, decals and probes
  twice. `VrEyeSharing` logs what the runtime does (`XR culling passes from the runtime: ...`) and, with
  `[Rendering] AugenTeilen`, gives the first eye a culling volume that holds both eyes (per plane the looser of the two,
  5 cm out) and the second eye the first eye's culling pass id. HDRP's own branch then shares the results.
- Shadow maps. `HDShadowManager.RenderShadows` runs in every render request and redraws the punctual, cascade and
  area atlases. The atlases are persistent RTHandles imported into the render graph, and with shared culling the
  second eye's shadow requests (from the culling results and the camera transform both eyes share) are the same as
  the first eye's. With `AugenTeilen` a prefix on `HDShadowAtlas.RenderShadows` skips the second eye for atlases the
  first eye rendered this frame, except blurred ones (EVSM, IM) and the cached atlases. The second eye still builds its
  own shadow data, so sampling uses its own camera-relative matrices. F10 switches it while the game runs; the frame
  time line names the state, so before and after land in separate lines.

Single Pass Instanced stays out of reach: HDRP issues every draw twice with `STEREO_INSTANCING_ON`, and a shader
without that variant draws both instances into the left eye. The game was built without XR, so none of its shaders,
HDRP's own included, has those variants, and there is no per-material fallback.

Texture array slices. HDRP allocates every screen-sized target with `TextureXR.slices` slices. `XRSystem`'s constructor
sets it to 2 whenever an XR display exists when the pipeline is created, and nothing lowers it. In MultiPass only
slice 0 is used. The pipeline is normally created before the plugin starts OpenXR, but one recreated afterwards (for
example by a quality level with another asset) would carry a second slice in every target, roughly doubling HDRP's
render target memory. `XrMaxViewsPatch` makes `GetMaxViews` return 1 with MultiPass, and if the slices are 2 anyway,
`HdrpXrSettings.FixSlices` sets them to 1 while the world is hidden (`HDRP render targets had 2 texture array slices`).
`Render info: ... texture array slices N` tells which case it is.

XR settings in the HDRP asset (`HdrpXrSettings.Update`): the occlusion mesh (the hidden area outside the lenses) is
only drawn if the asset's `xrSettings.occlusionMesh` is on and the runtime supplies one; the mod switches the flag on.
Camera jitter (`xrSettings.cameraJitter`) is off by default, and in XR HDRP then leaves the jitter out of TAA, which
only blurs: the washed-out TAA from the UI section. With TAA selected the mod switches the jitter on.

Effects that are wrong in a headset (motion blur, depth of field, lens distortion, chromatic aberration, Panini
projection) are switched off for the VR camera through the quality volume (`[Bild] VrUnpassendeEffekteAus`). The Hab
camera has no custom frame settings, so depth of field is allowed there; at full resolution per eye it is among the
most expensive effects, if a volume uses it.

Measuring. Neither the runtime nor `FrameTimingManager` reports GPU time, and HDRP's profiling samplers are compiled out
of release players. The mod opens a `CustomSampler` in a BeforeRendering pass and closes it after post-processing
(`GPU timer: ...` says once whether Unity's recorder works in this player; if it does, the frame time line gets
`GPU both eyes`). The frame time line also gives how often HDRP culls per frame and how long it takes (Harmony timer on
`HDRenderPipeline.TryCull`), physics steps per frame, garbage collections and managed memory. `Render info` lines, 3 s
after the rig follows a new camera, list the frame settings in effect, the active post effects, the occlusion mesh,
realtime shadow lights and planar probes, the asset's settings (as JSON) and once the timing settings (target frame
rate, fixed step, incremental GC).

The mod's own main thread cost is small (estimated 0.2-0.5 ms per frame). The largest item was `VrCanvases` scanning
every canvas including prefabs (`Resources.FindObjectsOfTypeAll`) twice a second and allocating an array per inactive
canvas; it now scans scene objects only and walks the parents.

Ideas not done yet, for after the measurements:
- HDRP's software dynamic resolution (`DynamicResolutionHandler`, XR-aware in 10.9, upscaling with CAS or Catmull-Rom)
  instead of `eyeTextureResolutionScale` below the maximum: no reallocation and no video memory spike per change, so
  the bay and the Hab, or an automatic controller, could change the resolution from one frame to the next. `VrUi` would
  have to draw with the final viewport instead of `actualWidth/actualHeight`.
- The desktop mirror is blitted every frame (`XRSystem.RenderMirrorView`, 0.1-0.3 ms); it could be switched off with
  `SetPreferredMirrorBlitMode`, at the cost of the game window (and the F8 window dump) staying black.
- Our AfterPostProcess pass makes HDRP render post-processing into an intermediate target and copy it once more per eye
  (`HDUtils.PostProcessIsFinalPass`).
- Passes that run per frame setting even when nothing uses them (rough distortion and refraction color pyramids,
  low-res transparency, Stop NaNs) could be switched off in the VR camera's frame settings once the render info shows
  they are on and a scan shows no material needs them.
- Incremental GC (`gc-max-time-slice` in `boot.config`) if the garbage collections show up as hitches.

## Roadmap
1. Inventory – done
2. Foundation (BepInEx, plugin, camera hook) – done
3. Seeing VR (stereo, head tracking) – working
4. Playing VR: controllers, tools in hand, bindings
5. Using VR: HUD as visor, menus with the right controller (no pointer yet) – in progress
6. Comfort and performance
