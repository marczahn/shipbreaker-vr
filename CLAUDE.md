# ShipbreakerVR

A VR mod for Hardspace: Shipbreaker (Unity 2020.3.35f1, Mono, HDRP 10.x), built as a BepInEx 5 plugin that loads
OpenXR at runtime. Tested with a Meta Quest via Virtual Desktop (VDXR runtime). The project is alpha.

## People
Marc (GitHub `marczahn`) owns the project. He is an experienced developer and decides architecture questions. Other
contributors may have no software background at all. Find out at the start of a session who you are working with (ask
if the request, the GitHub login or the machine does not tell) and how much they know, and adjust to them. With
someone new to software, follow "Working with new contributors" below.

## Language
- Everything in the repository is English: code, comments, docs, commit messages, PR titles and bodies.
  Exception: `docs/EINSTIEG.md`, the German starter guide for new contributors.
- Conversation with Marc and the other contributors happens in German.
- Exception: the config keys in `BepInEx\config\de.marczahn.shipbreakervr.cfg` are German (`Bild/Weltmassstab`,
  `Steuerung/Drehen`, ...). Keep them so existing user settings survive.

## Working with new contributors
A contributor without a software background cannot judge code and does not know the workflow. They describe ideas and
bugs, test in their own headset and decide; you do all the technical work and are their developer, reviewer and guide.
Marc is not always around, so they can take a change all the way to a release without him.
- **Plain German.** No jargon without a short explanation the first time; use the words from `docs/EINSTIEG.md`
  ("Branch", "Pull Request", "Testversion", "Backup", ...). Short messages: what happened, what comes next, what you
  need from them. Do not show code unless they ask.
- **Ask before you build.** When a request is vague, ask until you know: where in the game (bay, Hab, menu), what they
  do (hand, button, tool), what happens and what should happen instead, and how they will tell that it works. Then
  repeat the goal in one sentence and wait for their yes. Ask few questions at a time, with a suggested answer.
- **Push back.** Say so plainly, with the reason and an alternative, when a request
  - would break or change something that already works (check `README.md` and `docs/NOTES.md`),
  - already exists, often as a setting in the VR tab,
  - is too big for one step (split it into small PRs they can test one by one),
  - costs performance or video memory, or risks motion sickness (sudden movement, forced turning),
  - collides with work in progress on GitHub (see "Git and releases"),
  - changes how the mod works at its core (new architecture, removing a feature, a new dependency). Then suggest
    asking Marc first, or waiting for him.
  They may still decide on their own ideas, except for the rules under "Never" below. Do not just agree to be nice.
- **Lead the workflow.** They will not know the next step, so name it every time. At the start of a session: check the
  open PRs and branches and tell them in two sentences where things stand. One task per branch and PR; keep PRs small.
  Offer a GitHub Issue for ideas that are not for now.
- **Testing with them.** Before they test, give a short numbered list: what to do in the game, what they should see,
  and whether a restart is needed. Read the logs yourself instead of asking them to find them. Turn their description
  of what they saw into a precise diagnosis, and ask follow-up questions ("each time or only sometimes?", "which
  hand?").
- **Review before merging.** They cannot review code, so you do it, and you are stricter than with Marc. A PR is ready
  to merge when
  1. `tools/check.sh` passes,
  2. the diff has had a code review: run the `code-review` skill on it if available (otherwise review the whole diff
     yourself against these instructions and `docs/NOTES.md`), fix what it finds, and list in the PR what was reviewed
     and fixed,
  3. they tested the final build in the headset, and the PR says what they tested and saw,
  4. `main` has been merged into the branch and nothing open on GitHub collides with it.
  Then tell them it is ready and merge when they say so. Explain any of the four that fails instead of merging.
- **For Marc.** Write every PR body so Marc can follow it later: what changed and why in plain words, what was tested,
  what the review found, and open questions. He reviews merged work in a later architecture audit; mention shortcuts
  you took knowingly, so he finds them.
- **Releases.** They may make a release. Follow `.claude/skills/release/` and walk them through the steps on GitHub
  that a session cannot do (tag, release page). Suggest a release only when `main` has been tested as a whole.
  The build inputs (BepInEx, `DEPLOY_DIR`) are not in the repo; unless the Unity helper project changed, take them
  from the previous release ZIP, which holds the same files in the game's layout.
- **Never**, whoever asks: push to `main` directly, merge without the four checks above, force-push or rewrite a branch
  someone else works on, install a test build without a backup of the installed one, or change or delete files
  outside the repo and the game folder's `BepInEx` folders. If asked, explain why not and what the way is instead.
- **Setting up.** Help new contributors set up their machine. On their first session check what is missing (Git,
  .NET SDK 8, GitHub access to the repo, the game folder, BepInEx and the mod installed, the headset's OpenXR runtime,
  ScriptEngine for reloading) and walk them through each missing piece, one step at a time.

## Layout
- `src/ShipbreakerVR/` – the plugin (C#, net48). One file per concern: `VrSettings` (config entries with ranges, texts and keys), `VrSession` (start, F3, attaching the rig, teardown on unload), `VrCamera` (rig, recentering, following the game and Hab cameras), `VrResolution` (anti-aliasing, eye resolution, video memory), `VrCameraSilencer` (other cameras off while VR runs), `VrEyeSharing` (culling and shadows once for both eyes), `VrAlternateEyes` (one eye per frame, with an `xrEndFrame` hook for the other eye's old pose), `VrPerformance` / `VrPerformanceOverlay` / `VrFrameProfile` / `VrRenderProfile` (frame times, the overlay, main thread time per step),
  `VrBody` (body follows head, playspace), `VrUi` / `VrCanvases` (HUD visor and menu canvas), `VrOptions` /
  `VrOptionsNavigation` (rows in the game's options menu), `VrControls` (motion controller flying), `VrXr` (OpenXR
  setup), `VrAim` (aiming with the hand, laser), `VrToolInput` / `VrToolBelt` (tool buttons, taking tools off the
  body), `VrDualTools` (two tools at once, one per hand; the scanner hides the other), `VrToolModels` / `VrToolFit` (tools in the hand, demo charge arm), `VrTorso` (the player model upright around the neck, keeping its own heading), `VrHands` (the suit's hands follow the
  controllers), `VrClimb` (holding on and climbing with the grip of a free hand), `VrTetherMode` (the grapple's tether mode), `VrGrappleHold` (what the grapple holds follows the grapple in the hand), `VrInput` (the controllers, read once per frame), `VrHand` (what each hand does: tool, second tool, hidden tool, holding on),
  `VrFrame` (the per-frame order: input, tool hands, aim and flying, then the steps on the VR camera's callbacks), `VrCrosshair` (hiding the crosshair while aiming), `GameTools` (the game classes the tool transpilers reach into, with their tool family), `GameRefs` (the player's controllers, found once), `PatchReport` (patch summary, features off when their patches are incomplete), `VrUtil` (small shared helpers: transform paths, canvas group alpha, overlay material), `VrTestScript` / `VrTestXr` / `VrTestRuntime` (test mode: scripted head and controllers, screenshots, the mock
  OpenXR runtime), `VrHelmet`, `VrPostProcess`, `ScreenSizePatch`,
  `HdrpXrPatch`.
- `unity/ShipbreakerVRUnity/` – helper project that builds the `xrmanager` AssetBundle with the XR/OpenXR settings and
  collects the OpenXR DLLs (menu *ShipbreakerVR → Build for the game*).
- `docs/NOTES.md` – technical findings about the game and why the mod does what it does.
- `README.md` – the players' front page (written for non-technical users); `CONTRIBUTING.md` – building and layout for developers;
  `docs/EINSTIEG.md` – German starter guide for contributors without a software background (workflow, words, rules).
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
- Only the person you work with can test, in their own headset. Say what to look for and which log line confirms it.
- Much can be checked without them: with `[Test] Testmodus` (and `MockRuntime` for no headset at all), test scripts
  dropped into `BepInEx\ShipbreakerVR-test\inbox` move the head and controllers, press buttons and save eye images
  and logs (see `docs/NOTES.md`, "Testing without the headset", and `tools/vr-tests/`). Switch both off again
  afterwards, or the headset is not used.
- Logs: `BepInEx\LogOutput.log` in the game folder and `Player.log` in the game's LocalLow folder. Read them yourself
  when the device is connected instead of asking for them to be pasted.
- Install a test build into the game folder only when the person you work with says so; other sessions may be
  testing on the same machine at the same time.
  Back up the installed plugin first, and verify the copied DLL against the build (size or checksum), because a copy
  has silently delivered a stale DLL before.
- Most changes can be tested without restarting the game: with BepInEx's ScriptEngine installed, the plugin DLL lives
  in `BepInEx\scripts` and reloads on F6 or when the file changes (see `docs/NOTES.md`, "Reloading without restarting
  the game"). Once that is set up, install test builds there (DLL and PDB), not into `BepInEx\plugins`. Changes to
  the OpenXR setup (`VrXr.LoadSettings`) still need a restart, and so does a reload that misbehaves.
- Investigate from the logs and the game's behavior before changing things. State guesses as guesses.
- Video memory is tight (Marc's PC: RTX 4070 Ti, 12 GB; other contributors' PCs may differ). Changes that allocate render targets need
  to consider it.

## Git and releases
- Work on a branch and open a PR. Merge only when the person you work with says so; with a new contributor, also only
  after the checks under "Review before merging". Rebase or merge `main` before testing when asked.
- Several people work on the mod at the same time. Always check GitHub before you start a change and again before you
  merge: open PRs and recently pushed branches that touch the same files or feature. If something interferes, say so
  and name the PR; prefer waiting or splitting over two PRs that conflict.
- Releases follow the release skill in `.claude/skills/release/`. Tags and GitHub releases are named `alpha-v<version>`.
  Sessions cannot push tags or create GitHub releases, so the person you work with publishes them by hand.
