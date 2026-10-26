# ShipbreakerVR

A VR mod for Hardspace: Shipbreaker (Unity 2020.3.35f1, Mono, HDRP 10.x), built as a BepInEx 5 plugin that loads
OpenXR at runtime. Tested with a Meta Quest via Virtual Desktop (VDXR runtime). The project is alpha.

## Language
- Everything in the repository is English: code, comments, docs, commit messages, PR titles and bodies.
- Conversation with Marc happens in German.
- Exception: the config keys in `BepInEx\config\de.marczahn.shipbreakervr.cfg` are German (`Bild/Weltmassstab`,
  `Steuerung/Drehen`, ...). Keep them so existing user settings survive.

## Layout
- `src/ShipbreakerVR/` – the plugin (C#, net48). One file per concern: `VrCamera` (rig, eye resolution, video memory),
  `VrBody` (body follows head, playspace), `VrUi` / `VrCanvases` (HUD visor and menu canvas), `VrOptions` /
  `VrOptionsNavigation` (rows in the game's options menu), `VrControls` (motion controller flying), `VrXr` (OpenXR
  setup), `VrAim` (aiming with the hand, laser), `VrToolInput` / `VrToolBelt` (tool buttons, taking tools off the
  body), `VrDualTools` (two tools at once, one per hand; the scanner hides the other), `VrToolModels` / `VrToolFit` (tools in the hand, demo charge arm), `VrHands` (the suit's hands follow the
  controllers), `VrClimb` (holding on and climbing with the grip of a free hand), `VrInput` (the controllers, read once per frame), `VrHand` (what each hand does: tool, second tool, hidden tool, holding on),
  `VrFrame` (the per-frame order of input, tool hands, aim and flying), `VrCrosshair` (hiding the crosshair while aiming), `VrHelmet`, `VrPostProcess`, `ScreenSizePatch`,
  `HdrpXrPatch`.
- `unity/ShipbreakerVRUnity/` – helper project that builds the `xrmanager` AssetBundle with the XR/OpenXR settings and
  collects the OpenXR DLLs (menu *ShipbreakerVR → Build for the game*).
- `docs/NOTES.md` – technical findings about the game and why the mod does what it does.
- `README.md` – the players' front page (written for non-technical users); `CONTRIBUTING.md` – building and layout for developers.
- `docs/INSTALL.md` – user install guide; `docs/releases/<version>.md` – release notes.
- `tools/build-release.sh` and `.claude/skills/release/` – building a release ZIP.
- `licenses/`, `THIRD-PARTY-NOTICES.md`, `LICENSE` (MIT), `DISCLAIMER.md`.

## Building
```
dotnet build src/ShipbreakerVR -c Release -p:GameDir="<game folder>"
```
Reference assemblies come from the game (`Shipbreaker_Data/Managed`), BepInEx (`BepInEx/core`) and the Unity XR DLLs.
The game logic lives in `BBI.Unity.Game.dll`, `Assembly-CSharp` is nearly empty.

## Harmony patches
- Apply patches class by class in a try/catch, so one failing patch does not abort the plugin start.
- Resolve game and HDRP types with `TargetMethod()` + `AccessTools.TypeByName`, not `[HarmonyPatch("Type", "Method")]`.
- Name parameter types for overloaded methods (e.g. `CustomPass.Execute(CustomPassContext)`).
- Small game methods may be inlined by Mono, so a prefix/postfix on them never runs; patch the caller with a transpiler.

## Testing workflow
- Only Marc can test, in the headset. Say what to look for and which log line confirms it.
- Logs: `BepInEx\LogOutput.log` in the game folder and `Player.log` in the game's LocalLow folder. Read them yourself
  when the device is connected instead of asking Marc to paste them.
- Install a test build into the game folder only when Marc says so; other threads may be testing at the same time.
  Back up the installed plugin first, and verify the copied DLL against the build (size or checksum), because a copy
  has silently delivered a stale DLL before.
- Most changes can be tested without restarting the game: with BepInEx's ScriptEngine installed, the plugin DLL lives
  in `BepInEx\scripts` and reloads on F6 or when the file changes (see `docs/NOTES.md`, "Reloading without restarting
  the game"). Once that is set up, install test builds there (DLL and PDB), not into `BepInEx\plugins`. Changes to
  the OpenXR setup (`VrXr.LoadSettings`) still need a restart, and so does a reload that misbehaves.
- Investigate from the logs and the game's behavior before changing things. State guesses as guesses.
- Video memory is tight (RTX 4070 Ti, 12 GB). Changes that allocate render targets need to consider it.

## Git and releases
- Work on a branch and open a PR; merge only when Marc says so. Rebase or merge `main` before testing when asked.
- Releases follow the release skill in `.claude/skills/release/`. Tags and GitHub releases are named `alpha-v<version>`.
  Sessions cannot push tags or create GitHub releases, so Marc publishes them by hand.
