# ShipbreakerVR

A VR mod for Hardspace: Shipbreaker (Unity 2020.3.35f1, Mono, HDRP 10.x), built as a BepInEx 5 plugin that loads
OpenXR at runtime. Tested with a Meta Quest via Virtual Desktop (VDXR runtime). The project is alpha.

## Language
- Everything in the repository is English: code, comments, docs, commit messages, PR titles and bodies.
- Conversation with Marc happens in German.
- Exception: the config keys in `BepInEx\config\de.marczahn.shipbreakervr.cfg` are German (`Bild/Weltmassstab`,
  `Steuerung/Drehen`, ...). Keep them so existing user settings survive.

## Layout
- `src/ShipbreakerVR/` – the plugin (C#, net48). One file per concern: `VrSettings` (config entries with ranges, texts and keys), `VrSession` (start, F3, attaching the rig, teardown on unload), `VrCamera` (rig, recentering, following the game and Hab cameras), `VrResolution` (anti-aliasing, eye resolution, video memory), `VrCameraSilencer` (other cameras off while VR runs),
  `VrBody` (body follows head, playspace), `VrUi` / `VrCanvases` (HUD visor and menu canvas), `VrOptions` /
  `VrOptionsNavigation` (rows in the game's options menu), `VrControls` (motion controller flying), `VrXr` (OpenXR
  setup), `VrAim` (aiming with the hand, laser), `VrToolInput` / `VrToolBelt` (tool buttons, taking tools off the
  body), `VrDualTools` (two tools at once, one per hand; the scanner hides the other), `VrToolModels` / `VrToolFit` (tools in the hand, demo charge arm), `VrHands` (the suit's hands follow the
  controllers), `VrClimb` (holding on and climbing with the grip of a free hand), `VrTetherMode` (the grapple's tether mode), `VrInput` (the controllers, read once per frame), `VrHand` (what each hand does: tool, second tool, hidden tool, holding on),
  `VrFrame` (the per-frame order: input, tool hands, aim and flying, then the steps on the VR camera's callbacks), `VrCrosshair` (hiding the crosshair while aiming), `GameTools` (the game classes the tool transpilers reach into, with their tool family), `GameRefs` (the player's controllers, found once), `PatchReport` (patch summary, features off when their patches are incomplete), `VrUtil` (small shared helpers: transform paths, canvas group alpha, overlay material), `VrHelmet`, `VrPostProcess`, `ScreenSizePatch`,
  `HdrpXrPatch`.
- `unity/ShipbreakerVRUnity/` – helper project that builds the `xrmanager` AssetBundle with the XR/OpenXR settings and
  collects the OpenXR DLLs (menu *ShipbreakerVR → Build for the game*).
- `docs/NOTES.md` – technical findings about the game and why the mod does what it does.
- `README.md` – the players' front page (written for non-technical users); `CONTRIBUTING.md` – building and layout for developers.
- `docs/INSTALL.md` – user install guide; `docs/releases/<version>.md` – release notes.
- `tests/ShipbreakerVR.Tests/` – tests for the logic that runs without the engine; `tools/check.sh` builds and runs them.
- `tools/build-release.sh` and `.claude/skills/release/` – building a release ZIP.
- `tools/control-images.py` – draws the control overviews in `docs/images` (flying, hands, tool buttons, cutter,
  demo charges, grapple, menus). When a control changes, update its texts there, in `README.md`, in `docs/INSTALL.md` and in the setting's
  description (`VrSettings`), then run it.
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
- Run `tools/check.sh` (build with warnings as errors, then the tests in `tests/ShipbreakerVR.Tests`) before every PR,
  with the same folder properties as the build. The tests need the game's whole `Managed` folder (see
  `CONTRIBUTING.md`, "Checks"). Add a test when you change logic that runs without the engine.
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
