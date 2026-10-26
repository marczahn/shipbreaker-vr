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
Copy the resulting `ShipbreakerVR.dll` to `BepInEx\plugins\ShipbreakerVR\`.

## Settings file
All settings are stored in `BepInEx\config\de.marczahn.shipbreakervr.cfg`. The keys are German
(`Bild/Weltmassstab`, `KoerperFolgtKopf`, ...) and stay that way so existing user settings survive.

## License
MIT, see [LICENSE](LICENSE). Third-party components keep their own licenses, see [licenses/](licenses) and
[docs/INSTALL.md](docs/INSTALL.md#included-third-party-components).
