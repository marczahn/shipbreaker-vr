# Installing ShipbreakerVR (test build)

## Requirements
- Hardspace: Shipbreaker (Steam, version 1.3.x) on Windows
- A PC VR headset with an active OpenXR runtime (Meta Quest via Virtual Desktop, Quest Link/Air Link, SteamVR, …)

## Install
By installing you accept the [disclaimer](../DISCLAIMER.md). Back up your save games first.

1. In Steam: right-click Hardspace: Shipbreaker → Manage → Browse local files.
2. Extract the whole release ZIP into that folder (where `Shipbreaker.exe` is). Merge folders, overwrite files.
3. Start the game. The first start takes a bit longer while BepInEx sets itself up.

## Use
- Connect the headset and start the game; VR starts right away. **F3** toggles VR.
- **F4** recenters the view on the headset: position, and in menus also view direction and the menu canvas.
  Menus always appear at eye height, also when you look up or down.
  The row "Center view" at the top of the VR tab does the same.
- **F7** at one of the station's monitors switches its screen look. **F8** writes what is drawn around you into the log,
  for bug reports.
- The station between shifts has its own render resolution, the row "Station resolution" in the VR tab. If the view wobbles
  slightly at the monitors when you move your head, the graphics card misses the headset's frame rate there; lower it
  until that stops.
- Settings: in the game's options (tab "VR") or in
  `BepInEx\config\de.marczahn.shipbreakervr.cfg`.

### Flying with the controllers
Switch it in the options row "Controller flying" in the VR tab. Hold the left trigger, then:
- move the controller away from where it was when you pressed it to fly that way,
- tilt it sideways to roll; the further, the faster.

The left stick turns (left/right) and pitches (up/down), the further, the faster; it needs no trigger. The right stick
left/right snap-turns 30 degrees, Y brakes. Mouse, keyboard and gamepad keep working.

### Menus with the controllers
In menus both controllers act as one Xbox gamepad, each button where its counterpart sits, so the game shows gamepad
button prompts:
- sticks = sticks (left: navigate, right: scroll), right stick pressed = R3
- A/B/X/Y = A/B/X/Y (A confirms, B goes back)
- triggers = shoulder buttons (switch tabs), grips = triggers
- A + X together = Start: in gameplay it opens the pause menu, on loading screens it cycles the tips. In menus it is
  just A and X, since the game would read Start as "confirm".

There is no View button and no D-pad; the left menu button stays with Virtual Desktop. In gameplay only A + X
reaches the game.

## Uninstall
Delete `winhttp.dll` from the game folder (disables all BepInEx mods). For a full removal also delete `BepInEx\`,
`doorstop_config.ini`, `.doorstop_version`, `Shipbreaker_Data\Plugins\x86_64\UnityOpenXR.dll`,
`Shipbreaker_Data\Plugins\x86_64\openxr_loader.dll` and `Shipbreaker_Data\UnitySubsystems\UnityOpenXR\`,
or let Steam verify the game files.

When reporting problems, please attach `BepInEx\LogOutput.log`.

## Included third-party components
All license texts are in `BepInEx\plugins\ShipbreakerVR\licenses\`; `THIRD-PARTY-NOTICES.md` lists the sources.

| Component | Files | License |
|---|---|---|
| BepInEx 5.4.23.3 | `BepInEx\core\BepInEx*.dll` | MIT |
| HarmonyX, MonoMod, Mono.Cecil (shipped with BepInEx) | `BepInEx\core\0Harmony*.dll`, `MonoMod.*.dll`, `Mono.Cecil*.dll` | MIT |
| Unity Doorstop 4.4.0 (BepInEx loader) | `winhttp.dll` | LGPL-2.1 |
| Unity OpenXR 1.2.8, XR Management 4.2.1, Input System 1.0.2 | `Unity.XR.*.dll`, `Unity.InputSystem.dll`, `UnityOpenXR.dll` | Unity Companion License |
| Khronos OpenXR loader | `openxr_loader.dll` | Apache-2.0 |
| XR settings derived from ShipbreakerVR by Raicuparta | AssetBundle | MIT |

The LGPL-licensed `winhttp.dll` is shipped unmodified; its source code is available at
https://github.com/NeighTools/UnityDoorstop (tag v4.4.0).
