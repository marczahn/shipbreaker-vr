# Third-party components

The release ZIP bundles the following components. Their license texts are in `licenses/` (in the ZIP:
`BepInEx\plugins\ShipbreakerVR\licenses\`).

| Component | Files in the release | License | Source |
|---|---|---|---|
| BepInEx 5.4.23.3 | `BepInEx\core\BepInEx*.dll` | MIT (`BepInEx-MIT.txt`) | https://github.com/BepInEx/BepInEx |
| UnityDoorstop 4.4.0 | `winhttp.dll`, `doorstop_config.ini`, `.doorstop_version` | LGPL-2.1 (`UnityDoorstop-LGPL-2.1.txt`) | https://github.com/NeighTools/UnityDoorstop/tree/v4.4.0 |
| HarmonyX | `BepInEx\core\0Harmony*.dll`, `HarmonyXInterop.dll` | MIT (`HarmonyX-MIT.txt`) | https://github.com/BepInEx/HarmonyX |
| MonoMod | `BepInEx\core\MonoMod.*.dll` | MIT (`MonoMod-MIT.txt`) | https://github.com/MonoMod/MonoMod |
| Mono.Cecil | `BepInEx\core\Mono.Cecil*.dll` | MIT (`Mono.Cecil-MIT.txt`) | https://github.com/jbevain/cecil |
| Unity OpenXR Plugin 1.2.8, XR Plugin Management 4.2.1, Input System 1.0.2 | `BepInEx\plugins\ShipbreakerVR\Unity.*.dll`, `Shipbreaker_Data\Plugins\x86_64\UnityOpenXR.dll`, `Shipbreaker_Data\UnitySubsystems\UnityOpenXR\UnitySubsystemsManifest.json` | Unity Companion License (`Unity-Companion-License.md`) | Unity Package Manager |
| Khronos OpenXR loader (shipped with the Unity OpenXR Plugin) | `Shipbreaker_Data\Plugins\x86_64\openxr_loader.dll` | Apache-2.0 (`OpenXR-SDK-Apache-2.0.txt`) | https://github.com/KhronosGroup/OpenXR-SDK |
| XR settings derived from ShipbreakerVR by Raicuparta | `BepInEx\plugins\ShipbreakerVR\AssetBundles\xrmanager` | MIT (`ShipbreakerVr-Raicuparta-MIT.txt`) | ShipbreakerVR by Raicuparta (2022) |

UnityDoorstop is used unmodified as a separate library; its source is available at the link above.

## Assets made for the mod

| Asset | Files in the release | Origin |
|---|---|---|
| The new body (suit, helmet, visor and their textures) | `BepInEx\plugins\ShipbreakerVR\Anzug\*` | Model and textures made by Raik for this mod (generated in Meshy from a text prompt) and edited with the mod's own scripts. Its binding to the skeleton (weights and joints of its own) is computed from this model alone; it holds no mesh, weight or joint position from the game's files, only the game's bone names, by which the mod finds the bones at run time. |
| The radio in the station (model and texture) | inside `BepInEx\scripts\ShipbreakerVR.dll` (`Models/Radio.obj`, `Models/Radio.png`) | Model and texture made by Raik for this mod (generated in Meshy from a text prompt), the speaker grille repainted with the mod's own scripts. The music it plays is the game's own, played by the game's sound engine at run time; nothing of it is in the mod. |
