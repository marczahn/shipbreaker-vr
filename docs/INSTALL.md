# Installing ShipbreakerVR (test build)

## Requirements
- Hardspace: Shipbreaker (Steam, version 1.3.x) on Windows
- A PC VR headset with an active OpenXR runtime (Meta Quest via Virtual Desktop, Quest Link/Air Link, SteamVR, …)

## Install
1. In Steam: right-click Hardspace: Shipbreaker → Manage → Browse local files.
2. Extract the whole release ZIP into that folder (where `Shipbreaker.exe` is). Merge folders, overwrite files.
3. Start the game. The first start takes a bit longer while BepInEx sets itself up.

## Use
- Connect the headset, start the game, press **F3** to toggle VR.
- **F4** recenters the view (and the menu canvas).
- Settings: in the game's options (Video tab, rows starting with "VR:" at the bottom) or in
  `BepInEx\config\de.marczahn.shipbreakervr.cfg`.

## Uninstall
Delete `winhttp.dll` from the game folder (disables all BepInEx mods). For a full removal also delete `BepInEx\`,
`doorstop_config.ini`, `.doorstop_version`, `Shipbreaker_Data\Plugins\x86_64\UnityOpenXR.dll`,
`Shipbreaker_Data\Plugins\x86_64\openxr_loader.dll` and `Shipbreaker_Data\UnitySubsystems\UnityOpenXR\`,
or let Steam verify the game files.

When reporting problems, please attach `BepInEx\LogOutput.log`.

## Included third-party components
BepInEx 5.4.23.3 (LGPL-2.1), Unity OpenXR / XR Management / Input System (Unity Companion License),
XR settings derived from ShipbreakerVR by Raicuparta (MIT, see `LICENSE-ShipbreakerVr.txt`).
