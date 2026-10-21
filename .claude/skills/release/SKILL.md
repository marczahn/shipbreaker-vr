---
name: release
description: Build and deliver a ShipbreakerVR release ZIP (plugin, BepInEx, OpenXR files, all license texts) for this repo.
---

# Building a ShipbreakerVR release

The release is one ZIP laid out like the game folder; users extract it next to `Shipbreaker.exe`.

## Inputs
- Game reference assemblies (`Shipbreaker_Data/Managed`): stage them from Marc's device if not in the container
  (`F:/SteamLibrary/steamapps/common/Hardspace Shipbreaker/Shipbreaker_Data/Managed`), or reuse
  `/mnt/user-data/uploads/Hardspace Shipbreaker/Shipbreaker_Data/Managed` if it is still there.
- BepInEx 5.4.23.3: `/mnt/project-files/bepinex-5.4.23.3` (winhttp.dll, doorstop_config.ini, .doorstop_version, BepInEx/core).
- Unity helper project output: `/mnt/project-files/ShipbreakerVR/deploy` (plugin/, native/, subsys/, bundles/).
- License files in the repo: `LICENSE` (the mod, MIT), `DISCLAIMER.md`, `licenses/`, `THIRD-PARTY-NOTICES.md`.
- Build tools: `dotnet-sdk-8.0` (install from the Ubuntu apt archive if missing; dot.net downloads are blocked) and `zip`.

## Steps
1. Check the version: `Version` in `src/ShipbreakerVR/Plugin.cs` must be the release version. Bump it if needed and commit.
2. Check the licenses. Every bundled component needs its license text in `licenses/` and a row in `THIRD-PARTY-NOTICES.md`
   and `docs/INSTALL.md`:
   BepInEx 5 (MIT), UnityDoorstop 4.4.0 (LGPL-2.1, winhttp.dll, shipped unmodified), HarmonyX / MonoMod / Mono.Cecil (MIT),
   Unity OpenXR 1.2.8 / XR Management 4.2.1 / Input System 1.0.2 (Unity Companion License),
   Khronos OpenXR loader (Apache-2.0, openxr_loader.dll), XR settings from Raicuparta's ShipbreakerVR (MIT).
   If a component is added or upgraded, fetch its license from the upstream repo at the matching tag
   (raw.githubusercontent.com works; download.packages.unity.com is blocked).
3. Build: `MANAGED_DIR=<Managed dir> tools/build-release.sh <version> <scratch out dir>`.
   The script builds the plugin, copies all inputs and licenses and prints the ZIP path.
4. Verify: `unzip -l` the ZIP and diff its file list against the previous release in
   `/mnt/project-files/ShipbreakerVR/release/`. Expect the plugin DLL, `BepInEx/plugins/ShipbreakerVR/licenses/`
   (the `licenses/` files plus `LICENSE` and `DISCLAIMER.md`), and `INSTALL.md`, `THIRD-PARTY-NOTICES.md`,
   `DISCLAIMER.md` at the root; nothing missing compared to the last release.
5. Release notes: write `docs/releases/<version>.md` (English, user-facing: New, Fixed, Install), based on
   `git log` since the previous tag. Commit and push with any script and license changes.
6. Deliver: copy ZIP and notes to `/mnt/project-files/ShipbreakerVR/release/` (wait about 15 s for the network mount,
   check md5), then attach both in the reply.
7. Publishing: a GitHub release needs the change on `main` (merge the PR only with Marc's go). Session tools cannot
   create GitHub releases or push tags (the push is cut off), so Marc creates the release on GitHub with a new tag
   `v<version>` on `main`, the release notes as body and the ZIP as asset.
