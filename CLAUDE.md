# ShipbreakerVR

A VR mod for Hardspace: Shipbreaker (Unity 2020.3.35f1, Mono, HDRP 10.x), built as a BepInEx 5 plugin that loads
OpenXR at runtime. Tested with a Meta Quest via Virtual Desktop (VDXR runtime). The project is alpha.

## Language
- Everything in the repository is English: code, comments, docs, commit messages, PR titles and bodies.
- Conversation with Marc happens in German.
- Exception: the config keys in `BepInEx\config\de.marczahn.shipbreakervr.cfg` are German (`Bild/Weltmassstab`,
  `KoerperFolgtKopf`, ...). Keep them so existing user settings survive.

## Layout
- `src/ShipbreakerVR/` – the plugin (C#, net48). One file per concern: `VrCamera` (rig, eye resolution, video memory),
  `VrBody` (body follows head, playspace), `VrUi` / `VrCanvases` (HUD visor and menu canvas), `VrOptions` /
  `VrOptionsNavigation` (rows in the game's options menu), `VrControls` (motion controller flying), `VrXr` (OpenXR
  setup), `VrHelmet`, `VrPostProcess`, `ScreenSizePatch`, `HdrpXrPatch`.
- `unity/ShipbreakerVRUnity/` – helper project that builds the `xrmanager` AssetBundle with the XR/OpenXR settings and
  collects the OpenXR DLLs (menu *ShipbreakerVR → Build for the game*).
- `docs/NOTES.md` – technical findings about the game and why the mod does what it does.
- `docs/JOURNAL.md` – the development journal (see below).
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
  the game"). Changes to the OpenXR setup (`VrXr.LoadSettings`) still need a restart, and so does a reload that misbehaves.
- Investigate from the logs and the game's behavior before changing things. State guesses as guesses.
- Video memory is tight (RTX 4070 Ti, 12 GB). Changes that allocate render targets need to consider it.

## Git and releases
- Work on a branch and open a PR; merge only when Marc says so. Rebase or merge `main` before testing when asked.
- Releases follow the release skill in `.claude/skills/release/`. Tags and GitHub releases are named `alpha-v<version>`.
  Sessions cannot push tags or create GitHub releases, so Marc publishes them by hand.

## Development journal
`docs/JOURNAL.md` tells the story of the mod's development for people who want to see how it came about, including
dead ends, wrong guesses and revised decisions. It is the counterpart to a developer diary: an experience report told by
Claude in the first person.

**Add an entry in the same PR** whenever the PR adds a feature, fixes a bug, or revises an earlier decision. Pure
documentation, release housekeeping and refactorings without visible effect do not need one.

How to write an entry:
- Append it at the bottom (chronological order). Start with an anchor and a heading:
  `<a id="short-slug"></a>` then `## YYYY-MM-DD · Title`. Use a date range if the work spanned several days.
- Written in English, in the first person from Claude's perspective ("I"). Refer to Marc and his brother Raik (tester
  and source of ideas) by name.
- Follow the style, tone and length of the existing entries: short, conversational, like a blog post, with short
  sentences and dry humor. The facts must stay accurate.
- Tell what was wanted, what went wrong, which attempts failed and why, and what finally worked. Mention when
  something took several rounds or when Marc's or Raik's observation or suggestion cracked the problem.
- When a change revises or builds on an earlier entry, link it: `[world scale](#world-scale)`.
- Leave out technical detail the story does not need; the facts belong in `docs/NOTES.md`.
- No direct quotes from Marc or Raik; paraphrase what they said or decided.
- Group related small problems by cause or theme instead of walking through each one separately.
- No personal paths, drive letters or user names from the test machine.
