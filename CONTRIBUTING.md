# Developing ShipbreakerVR

A VR mod for **Hardspace: Shipbreaker** (Unity 2020.3, Mono, HDRP 10.x), built as a BepInEx 5 plugin that loads
OpenXR at runtime. Tested with Meta Quest via Virtual Desktop (VDXR runtime). For players, see the [README](README.md).

- How the game works and why the mod does what it does: [docs/NOTES.md](docs/NOTES.md)
- Release notes: [docs/releases/](docs/releases)

## Layout
- `src/ShipbreakerVR/` – the BepInEx plugin (C#, net48)
- `unity/ShipbreakerVRUnity/` – helper project (Unity 2020.3.35f1). Builds the `xrmanager` AssetBundle with the
  XR/OpenXR settings and collects the OpenXR DLLs (menu *ShipbreakerVR → Build for the game*).
  The XR settings are derived from [ShipbreakerVR by Raicuparta](licenses/ShipbreakerVr-Raicuparta-MIT.txt) (MIT).
- `tools/build-release.sh` – builds the release ZIP

## Building
```
dotnet build src/ShipbreakerVR -c Release -p:GameDir="C:\path\to\Hardspace Shipbreaker"
```
Expects BepInEx and the Unity XR DLLs in the game folder (`BepInEx\core`, `BepInEx\plugins\ShipbreakerVR`).
`GameDir` defaults to Steam's standard library folder; it can also be set as an environment variable. Without an
installed game, set the three folders the build reads instead:
```
dotnet build src/ShipbreakerVR -c Release -p:ManagedDir=<Shipbreaker_Data/Managed> -p:BepInExDir=<BepInEx/core> -p:XrDir=<folder with Unity.XR.*.dll>
```
Copy the resulting `ShipbreakerVR.dll` to `BepInEx\plugins\ShipbreakerVR\`, or with BepInEx's ScriptEngine to
`BepInEx\scripts\` (see [docs/NOTES.md](docs/NOTES.md), "Reloading without restarting the game").
The DLL's version comes from `Plugin.Version` in `Plugin.cs`.

## From the Unity helper project to a release
*ShipbreakerVR → Build for the game* in `unity/ShipbreakerVRUnity` writes `Output/` in the game's folder structure.
`tools/build-release.sh` reads the same files from a flatter `DEPLOY_DIR`, copied from `Output/` like this:

| `DEPLOY_DIR/` | from `Output/` | goes to (game folder) |
|---|---|---|
| `plugin/` | `BepInEx/plugins/ShipbreakerVR/*.dll` (`Unity.XR.*`, `Unity.InputSystem`) | `BepInEx/plugins/ShipbreakerVR/` |
| `native/` | `Shipbreaker_Data/Plugins/x86_64/` (`UnityOpenXR.dll`, `openxr_loader.dll`) | `Shipbreaker_Data/Plugins/x86_64/` |
| `subsys/` | `Shipbreaker_Data/UnitySubsystems/UnityOpenXR/UnitySubsystemsManifest.json` | `Shipbreaker_Data/UnitySubsystems/UnityOpenXR/` |
| `bundles/` | `AssetBundles/` (`xrmanager`, `xrmanager.manifest`) | `BepInEx/plugins/ShipbreakerVR/AssetBundles/` |

`plugin/` also serves as `XrDir` for building the plugin. License texts in the release come only from the repo
(`LICENSE`, `DISCLAIMER.md`, `licenses/`); the `licenses/` folder the Unity build writes also holds the license files of
the shipped Unity packages, for checking that `licenses/` still covers them when a package is upgraded. The steps
for a release are in `.claude/skills/release/SKILL.md`.

## Settings file
All settings are stored in `BepInEx\config\de.marczahn.shipbreakervr.cfg`. The keys are German
(`Bild/Weltmassstab`, `Steuerung/Drehen`, ...) and stay that way so existing user settings survive.

## License
MIT, see [LICENSE](LICENSE). Third-party components keep their own licenses, see [licenses/](licenses) and
[docs/INSTALL.md](docs/INSTALL.md#included-third-party-components).
