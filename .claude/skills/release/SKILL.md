---
name: release
description: Build and deliver a ShipbreakerVR release ZIP (plugin, BepInEx, OpenXR files, all license texts) for this repo.
---

# Building a ShipbreakerVR release

The release is one ZIP laid out like the game folder; users extract it next to `Shipbreaker.exe`.

## Inputs
`tools/build-release.sh` takes all locations from environment variables. Find them in the current environment
(uploads, shared project folder, the user's device); if one is missing, ask the user where it is or to upload it.
- `MANAGED_DIR`: the game's `Shipbreaker_Data/Managed` folder (reference assemblies for the build).
- `BEPINEX_DIR`: unpacked BepInEx 5.4.23.3 with `winhttp.dll`, `doorstop_config.ini`, `.doorstop_version` and `BepInEx/core`.
- `DEPLOY_DIR`: output of the Unity helper project (`unity/ShipbreakerVRUnity`) with `plugin/`, `native/`, `subsys/`, `bundles/`.
- License files in the repo: `LICENSE` (the mod, MIT), `DISCLAIMER.md`, `licenses/`, `THIRD-PARTY-NOTICES.md`.
- Build tools: .NET SDK 8 and `zip`.

## Steps
1. Check the version: `Version` in `src/ShipbreakerVR/Plugin.cs` must be the release version. Bump it if needed and commit.
2. Check the licenses. Every bundled component needs its license text in `licenses/` and a row in `THIRD-PARTY-NOTICES.md`
   and `docs/INSTALL.md`:
   BepInEx 5 (MIT), UnityDoorstop 4.4.0 (LGPL-2.1, winhttp.dll, shipped unmodified), HarmonyX / MonoMod / Mono.Cecil (MIT),
   Unity OpenXR 1.2.8 / XR Management 4.2.1 / Input System 1.0.2 (Unity Companion License),
   Khronos OpenXR loader (Apache-2.0, openxr_loader.dll), XR settings from Raicuparta's ShipbreakerVR (MIT).
   If a component is added or upgraded, fetch its license from the upstream repo at the matching tag.
3. Build: `MANAGED_DIR=... BEPINEX_DIR=... DEPLOY_DIR=... tools/build-release.sh <version> <out dir>`.
   The script builds the plugin, copies all inputs and licenses and prints the ZIP path.
4. Verify: `unzip -l` the ZIP and compare its file list with the previous release's ZIP (from the GitHub release or
   wherever the user keeps it). Expect the plugin DLL, `BepInEx/plugins/ShipbreakerVR/licenses/` (the `licenses/`
   files plus `LICENSE` and `DISCLAIMER.md`), and `INSTALL.md`, `THIRD-PARTY-NOTICES.md`, `DISCLAIMER.md` at the root;
   nothing missing compared to the last release.
   Nothing from the game may be in it, unchanged or modified (assets, code, data; see "No game files" in `CLAUDE.md`):
   only the plugin, BepInEx, the Unity/OpenXR files from the helper project, the license texts and the docs. The script
   stops when a staged file is identical to one in the game's `Shipbreaker_Data`; a modified game file it cannot catch,
   so look at every new entry in the file list.
5. Release notes: write `docs/releases/<version>.md` (English, user-facing: New, Fixed, Install), based on
   `git log` since the previous tag. Commit and push with any script and license changes.
6. Deliver the ZIP and the notes to the user (check the copy's checksum if it goes through a network folder).
7. Publishing: a GitHub release needs the change on `main` (merge the PR only with the user's go) and a tag
   `alpha-v<version>` on it; the release title gets the same `alpha-` prefix (the project is alpha, older tags
   `v0.6.1`/`v0.7.0` predate this). If you cannot create releases or push tags, the user creates the release on GitHub with a new
   tag on `main`, the release notes as body and the ZIP as asset.
