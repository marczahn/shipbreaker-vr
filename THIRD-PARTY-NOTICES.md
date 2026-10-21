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
| XR settings derived from ShipbreakerVR by Raicuparta | `BepInEx\plugins\ShipbreakerVR\AssetBundles\xrmanager` | MIT (`ShipbreakerVR-Raicuparta-MIT.txt`) | ShipbreakerVR by Raicuparta (2022) |

UnityDoorstop is used unmodified as a separate library; its source is available at the link above.
