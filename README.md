# ShipbreakerVR

A VR mod for **Hardspace: Shipbreaker** (Unity 2020.3, HDRP), built on BepInEx 5 and OpenXR.
Tested with Meta Quest via Virtual Desktop. Early test build.

> Unofficial fan project, use at your own risk. See the [disclaimer](DISCLAIMER.md) (liability, VR health notes).

## Status
- Stereo rendering (MultiPass) and head tracking
- Body and tools follow the head; mouse/stick turning is added on top
- HUD as a visor in front of the eyes, menus on a canvas in the room, helmet follows the head
- VR settings inside the game's options (Video tab, rows starting with "VR:" at the bottom)
- Not yet: motion controllers, laser pointer for menus, comfort options

Notes on how the game works and why the mod does what it does: [docs/NOTES.md](docs/NOTES.md).

## Usage
- **F3** toggles VR, **F4** recenters the view
- Settings: in-game options or `BepInEx\config\de.marczahn.shipbreakervr.cfg`

Installing a release: [docs/INSTALL.md](docs/INSTALL.md).

## Layout
- `src/ShipbreakerVR/` – the BepInEx plugin (C#, net48)
- `unity/ShipbreakerVRUnity/` – helper project (Unity 2020.3.35f1). Builds the `xrmanager` AssetBundle with the
  XR/OpenXR settings and collects the OpenXR DLLs (menu *ShipbreakerVR → Build for the game*).
  The XR settings are derived from [ShipbreakerVR by Raicuparta](licenses/ShipbreakerVr-Raicuparta-MIT.txt) (MIT).

## Building
```
dotnet build src/ShipbreakerVR -c Release -p:GameDir="C:\path\to\Hardspace Shipbreaker"
```
Expects BepInEx and the Unity XR DLLs in the game folder (`BepInEx\core`, `BepInEx\plugins\ShipbreakerVR`).
Copy the resulting `ShipbreakerVR.dll` to `BepInEx\plugins\ShipbreakerVR\`.

## License
MIT, see [LICENSE](LICENSE). Third-party components keep their own licenses, see [licenses/](licenses) and [docs/INSTALL.md](docs/INSTALL.md).
Use at your own risk, see the [disclaimer](DISCLAIMER.md).
