# Motion controller concept

Draft concept for playing Hardspace: Shipbreaker with VR motion controllers (Meta Quest Touch first, other OpenXR
controllers later). Status: proposal, nothing implemented yet. Open questions are collected at the end.

## Goals
- Play a full shift without keyboard or mouse: move, turn, use every tool, menus, Hab.
- Tools are aimed with the hand, not the head. Looking around and aiming are independent.
- Stay close to the game: reuse its input actions and tool logic, patch as little as possible.
- Comfortable by default (snap turn, no forced roll), with options for experienced players.
- Keyboard and mouse keep working alongside the controllers.

Non-goals for now: full-body/arm IK, physical grabbing of objects with the hands, two tools at the same time.

## Starting point (what the mod already does)
- The VR rig (`VrCamera`) has a playspace rotation; with body-follows-head, `VrBody` sets the body to
  `playspace * head` and the mouse delta turns the playspace. Turning with a stick fits directly into this.
- All tools aim through `LynxCameraController.MainCamera.ScreenPointToRay(ScreenCenter)`
  (`CuttingController`, `TetherController`, `Scanner`, see NOTES.md). Hand aiming means replacing these rays.
- The OpenXR settings in the `xrmanager` bundle already enable the **Oculus Touch Controller Profile**.
- The game reads input through InControl (`LynxControls`, `GameplayActions.GameplayActionSet`) and supports gamepads,
  so it already has controller bindings for every action.
- The UI is drawn on a quad by `VrUi` (visor in gameplay, fixed canvas in menus).

## Architecture

```
OpenXR runtime
   │  (UnityEngine.XR.InputDevices: poses, buttons, axes, haptics)
   ▼
VrInput ──────────────► VrInputDevice (virtual InControl device) ──► game actions (move, fire, scan, …)
   │                     │
   │                     └─ action overrides via Harmony where a gamepad binding is not enough
   ├──► VrHands (controller poses under the rig, tool model in hand)
   ├──► VrAim   (hand ray for the tools' raycasts, world reticle)
   ├──► VrBody  (snap/smooth turn on the playspace)
   ├──► VrPointer (laser pointer for menus on the VrUi quad)
   └──► VrHaptics (vibration on game events)
```

### VrInput – reading the controllers
- Poll `UnityEngine.XR.InputDevices` for the left/right controller once per frame (`CommonUsages.trigger`, `grip`,
  `primary2DAxis`, `primaryButton`, `secondaryButton`, `primary2DAxisClick`, `menuButton`, `devicePosition/Rotation`).
  This is the legacy XR input API from the engine module, so it does not depend on the game having the new Input
  System enabled.
- Store a small state struct per hand (values + pressed/released this frame, deadzones applied) that all other parts
  read. Re-fetch devices on `InputDevices.deviceConnected/Disconnected`.
- Risk to verify first: whether OpenXR 1.2.8 feeds the legacy API without the Input System backend in this game.
  Fallback: enable the Input System from the plugin (`Unity.InputSystem.dll` is already shipped) or read OpenXR
  actions directly.

### VrInputDevice – feeding the game's actions
Recommended path: register a virtual InControl `InputDevice` (subclass with `AddControl` / `UpdateWithValue`,
attached via `InputManager.AttachDevice`) that looks like a standard gamepad. The game's existing gamepad bindings
then apply to the controllers for free: movement, boost, tools, scan mode, menus, pause.

Where a gamepad binding does not fit (actions that must not come from the stick, or combos), add a small override
layer: a Harmony postfix on `LynxControls.GetInputIsPressed` / the value getters that ORs in the VR state for specific
`GameplayActionSet` entries. Keep that list short.

Alternative considered: patching every action read directly. More control, but many more patches and every binding
must be reimplemented. Only worth it if the virtual device does not work.

The look axes of the virtual device stay at zero: turning is done by the mod (see below), aiming by the hand.

### VrHands – controller poses and the tool in hand
- Two child objects of the rig (next to the head), each with a `TrackedPoseDriver`
  (`GenericXRController`, `LeftPose`/`RightPose`). Because they share the rig with the head, they are automatically
  correct relative to the view, including playspace turning and recentering.
- The tool view model (the first-person tool under the game camera) is re-parented to the tool hand with an
  adjustable grip offset. Its beam/muzzle points move with it as long as they are children of the model.
  The arms are hidden at first (no IK).
- Simple hand/controller models for the off hand (or none, just a small pointer dot) to start with.

### VrAim – aiming tools with the hand
- Transpile the `ScreenPointToRay` call sites in `CuttingController`, `TetherController` and `Scanner` to
  `VrAim.ScreenPointToRay(camera, position)`. With controllers active it returns the tool hand's aim ray, otherwise
  the original ray. The same pattern as `OrientationControllerAxisRotationPatch`.
- Use the controller's pointer pose (tilted ~ -40° from the grip pose on Touch, configurable) so the ray matches the
  natural pointing direction.
- First task of this step: an inventory of all other places where tools use `MainCamera`/`MainCameraTransform` for
  direction or origin (beam effects, demo charge placement, tether anchor, interaction prompts, range checks). Each
  either gets redirected or is accepted as head-based.
- Hide the game's center crosshair in VR and draw a world-space reticle at the hit point (plus an optional thin
  laser). The HUD texts about the aimed object stay on the visor.
- Fallback idea from NOTES.md: a hidden aiming camera on the hand that becomes `MainCamera` for the tools. Riskier,
  because the body and Cinemachine also read `MainCamera`.

### Turning (VrBody)
- Right stick X turns the playspace directly in `VrBody` (not via the game's look input, which has acceleration and
  smoothing): snap turn (default 30°, options 15/45/90) or smooth turn (degrees per second).
- Turning happens around the body's current up axis, so it works in zero-G at any orientation.
- Optional pitch snap (modifier + right stick Y) for players who don't want to look straight up in the chair.
- Roll stays off by default (strongest motion sickness trigger); "Reorient" already levels the playspace.

### Movement direction
The game interprets movement input in the body frame, and with body-follows-head the body points where the head
looks. Two options:
- **Head** (default): thrust follows the view, no extra work.
- **Hand**: thrust follows the movement hand. The mod rotates the stick vector from the hand frame into the body frame
  before feeding it to the virtual device. Lets you look around while flying straight.

### VrPointer – menus
- In menus (canvas mode) and on in-game screens a laser comes out of the hand that last pulled its trigger.
- Ray vs. the `VrUi` quad → UV → pixel on the UI camera → fed to the Unity EventSystem by setting
  `BaseInputModule.inputOverride` to a custom `BaseInput` (mouse position, mouse button = trigger, scroll = stick Y).
- To verify: which input module the game uses (standard or InControl's), and whether it reads `Input.mousePosition`
  directly. Fallback: patch the module's pointer processing.
- Gamepad navigation via the virtual device keeps working as a backup (sticks/A/B).

### VrHaptics
`InputDevice.SendHapticImpulse(0, amplitude, duration)` on the tool hand unless stated otherwise:
- cutter firing (light continuous), cut finished (short pulse), cutting a dangerous part (stronger)
- grapple grab/release, tether attached/snapped
- demo charge placed / detonated (both hands)
- damage, hull breach, explosions nearby (both hands, scaled with intensity)
- low oxygen / fuel warnings (off hand)
Hooks are Harmony postfixes on the relevant game methods, found during the tool inventory. Global strength option.

## Default mapping (Meta Quest Touch, right-handed)

Draft. The final list depends on the actual `GameplayActionSet` entries (step 0); names below describe the function.

| Input | Right hand (tool hand) | Left hand (movement hand) |
|---|---|---|
| Trigger | Primary fire (cut, grapple) | Brake / stop (hold) |
| Grip | Secondary fire (e.g. push/throw) | Modifier layer (hold) |
| Stick X | Snap/smooth turn | Strafe left/right |
| Stick Y | Thrust up/down | Thrust forward/back |
| Stick click | Tool selection (hold, tilt, release) | Boost |
| A / X | Tool mode (e.g. splitsaw orientation, tether mode) | Interact |
| B / Y | Reorient (hold) | Scan mode |
| Menu | – | Pause menu |

Modifier layer (left grip held):

| Input | Action |
|---|---|
| Right stick X | Roll (only if enabled) |
| Right stick Y | Pitch snap (only if enabled) |
| A | Retract tethers / tool-specific second mode |
| B | Flashlight |
| X / Y | Previous / next tool |
| Left stick click | Recenter (F4) |

Left-handed mode mirrors the table. Tool selection opens a small radial menu on the tool hand; a wrist panel on the
off hand (tools, O2/fuel, time) is a later option.

## VR-native extras (after the basics work)
- **Splitsaw orientation from the wrist**: the cutter's horizontal/vertical cut plane follows the controller roll
  instead of a mode button.
- **Grapple throw by gesture**: release velocity of the hand scales/directs the throw.
- **Off-hand grapple**: quick grapple on the off hand while the cutter stays in the tool hand (needs the game to
  allow two active tools – investigate).
- **Comfort vignette** while thrusting or turning smoothly.
- More controller profiles (Index, Vive, WMR) enabled in the `xrmanager` bundle, with per-profile default mappings.

## Settings (VR rows in the Gameplay tab and the config file)
Controllers on/off · dominant hand · movement direction (head/hand) · turn mode (snap/smooth) · snap angle ·
smooth turn speed · stick deadzone · pitch snap on/off · roll on/off · aim angle offset · laser visible ·
haptics strength · vignette on/off.

## Implementation steps
0. **Spike / inventory**
   - Read both controllers in game and log poses/buttons (proves the input path).
   - Decompile and list `GameplayActionSet` and the gamepad bindings in `GameplayActions`; check how InControl
     devices are attached and which UI input module is used.
   - Find all `MainCamera`/`MainCameraTransform` uses in tool code.
1. **Controllers as a gamepad**: `VrInput` + `VrInputDevice`, snap turn in `VrBody`. Aiming still with the head.
   Already playable without keyboard.
2. **Hand aiming**: `VrHands`, `VrAim` transpilers, tool model in hand, world reticle.
3. **Menus with a laser pointer**: `VrPointer`.
4. **Feel**: haptics, tool radial, settings rows, left-handed mode.
5. **VR-native extras and comfort** from the list above.

## Open questions
- Default turn: snap 30° or smooth? Should pitch snap be on by default in zero-G?
- Movement direction default: head or hand?
- Brake on the left trigger vs. interact on the left trigger (interact is frequent; brake is held).
- Should the hands/arms be visible early (simple controller models) or only the tool?
- How should tool selection feel: radial on the stick, wrist panel, or reaching to "holsters" on the body?
