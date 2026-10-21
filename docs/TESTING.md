# Testing

Restarting the game after every change does not scale. Testing is split into layers, from fast and narrow to slow and
complete:

| Layer | What | Runs where | Status |
|---|---|---|---|
| 1. Hot reload | Reload `ShipbreakerVR.dll` in the running game | Game | Ready (dev plugin), plugin-side teardown pending |
| 2. In-game self-test | Scripted checks with PASS/FAIL, also from the command line | Game | Ready (dev plugin) |
| 3. Patch check | Every Harmony patch finds its target, transpilers still match | Windows, no game start | Ready (`tools/PatchCheck`) |
| 4. Unit tests | Pure logic (memory estimate, row linking, value stepping) | Anywhere, also CI | Planned, needs code moved out of the plugin |

Layers 1–3 live **outside the plugin**: `src/ShipbreakerVR.Dev` (a separate BepInEx plugin) and `tools/`. They reach
into ShipbreakerVR only through reflection, so the plugin itself is unchanged. The release ZIP does not contain them
(`tools/build-release.sh` builds `src/ShipbreakerVR` only). What the plugin should change later to make reloading
clean is listed under [Plugin integration](#plugin-integration).

## Setup (once)
```
tools\dev-deploy.ps1 -GameDir "F:\SteamLibrary\steamapps\common\Hardspace Shipbreaker" -Setup
```
This builds the dev plugin into `BepInEx\plugins\ShipbreakerVR.Dev\` and moves the regular
`BepInEx\plugins\ShipbreakerVR\ShipbreakerVR.dll` aside (`.dev-parked`), because the dev plugin loads ShipbreakerVR from
`BepInEx\dev\` instead. The XR DLLs and the AssetBundle stay where they are. `-Restore` undoes all of this.

## Layer 1: hot reload
```
tools\dev-deploy.ps1 -GameDir "..."
```
builds ShipbreakerVR into `BepInEx\dev\`. The running game notices the new DLL within about a second and swaps the
plugin (or press **F6**). The log shows `Unloading ShipbreakerVR ...`, `Removed n Harmony patch(es) ...`,
`Loaded ShipbreakerVR ...`.

How it works (`HotReload.cs`): the DLL is read into memory, so the build can overwrite it at any time, and loaded under
a new assembly name (`ShipbreakerVR-dev3`), because Mono would otherwise return the already loaded copy. Old
assemblies cannot be unloaded, so their effects are undone instead:
1. If VR is on, it is switched off through the old plugin, which puts the game camera, canvases and helmet back.
2. The old plugin's GameObject, all components from its assembly and all objects named `ShipbreakerVR ...` (rig, UI
   pass, option rows) are destroyed.
3. Every Harmony patch whose method lives in the old assembly is removed.
4. The `xrmanager` AssetBundle is unloaded so the new plugin can load it again.
5. The new plugin loads, gets its option rows injected into existing options screens, and VR is switched on again if it
   was on.

Steps 1, 4 and 5 are workarounds in `ModHooks.cs` until the plugin can do this itself.

Limits:
- Each reload keeps the old assembly in memory (a few hundred KB). Restart the game now and then.
- Static state inside the game (e.g. a scroll list's height grown by the option rows) is not reset. After a reload the
  options list grows by the rows' height again.
- Re-initialising OpenXR with a freshly loaded settings asset has not been tried in the game yet. If VR does not come
  back after a reload, restart the game and note it here.
- Changes to the dev plugin itself, the XR DLLs or the AssetBundle still need a restart.

## Layer 2: in-game self-test
Press **F7** in the game, or let a script start the game and wait for the result:
```
tools\selftest.ps1 -GameDir "..."                               # all tests, waits for a loaded shift
tools\selftest.ps1 -GameDir "..." -Tests xr,stereo -NoWait      # selected tests, right away in the main menu
```
With `-NoWait` (and with F7) tests that need gameplay are skipped. Without it the test waits for a shift to be loaded
(up to `WaitForGameplay` seconds, config `de.marczahn.shipbreakervr.dev.cfg`); load one by hand. Command-line arguments
for the game: `--vr-selftest[=names]`, `--vr-selftest-quit`, `--vr-selftest-nowait`.

Results go to the log and to `BepInEx\selftest\results.txt`:
```
START	2026-10-21 14:03:11
PASS	patches.applied	15 patch classes applied, 1 method(s) transpiled | 0.0 s
FAIL	aim.screen-size	tools aim with a screen of 2688x2880, the game camera has 2560x1440 | 0.1 s
SKIP	options.navigation	open the Video tab of the options to run this
DONE	pass=9	fail=1	skip=2
```
A test also fails if ShipbreakerVR logged an error while it ran, or if a video memory error (`0x8007000e`) showed up.

| Test | Checks | Guards against |
|---|---|---|
| `patches.applied` | every patch class applied, every transpiler changed its method | a patch failing silently at start |
| `options.rows` | VR rows exist, are handled by the current plugin, sit below the game rows | missing or stale rows |
| `xr.start` | VR starts, display runs, VR camera renders | broken XR bootstrapping |
| `ui.overlay-canvases` | no Screen Space Overlay canvas left in VR | UI in one eye only |
| `stereo.eye-separation` | eye distance 50–80 mm × world scale; doubling the scale doubles it | world scale not reaching stereo |
| `render.targets-shrink` | HDRP render targets shrink to the eye size after VR start | wasted video memory |
| `render.resolution-change` | resolution goes down and back up after the slider rests | resolution changes not applied |
| `aim.screen-size` | tool screen size equals the game camera, centre ray along the view | cutter firing upwards |
| `aim.follows-head` | tools aim where the head looks (body follows head) | body and head drifting apart |
| `options.navigation` | walking down the list keeps the selection on each row and reaches all VR rows, and back up | selection jumping while scrolling, dead links |
| `xr.toggle-cycle` | VR off/on twice, the game camera takes over and hands back | black screen after VR off |
| `log.errors` | no plugin or video memory error since the game started | everything else |

The tests change settings (world scale, render resolution) only temporarily and put VR back in the state it was in.
New tests go into `SelfTests.cs`; a test is a coroutine that uses `t.Check`, `t.Skip`, `t.Until` and `t.OnCleanup`.

### Without a headset
OpenXR uses the system's active runtime. `-XrRuntimeJson` sets `XR_RUNTIME_JSON` for this run only, e.g. to the
Meta XR Simulator's runtime JSON, so the tests run without putting the headset on or starting Virtual Desktop.

### Steam
If the game restarts itself through Steam when started directly, the arguments are lost. A `steam_appid.txt` with the
game's Steam app ID in the game folder usually prevents that. `selftest.ps1` waits for the result file, not for the
process.

## Layer 3: patch check
```
dotnet build src\ShipbreakerVR -p:GameDir="..."
dotnet run --project tools\PatchCheck --property:GameDir="..." -- --game "..."
```
This loads the game's assemblies and the newest `src/ShipbreakerVR/bin/**/ShipbreakerVR.dll` (or `--plugin <dll>`)
into a plain .NET Framework process. For each patch class it resolves the target the way Harmony does, applies it, and
checks that each transpiler changes its method. Output is `OK`, `WARN` (target found, but the method cannot be compiled
outside Unity) or `FAIL`, and the exit code is 1 on any failure. Worth running after every game update. It does not run
in CI, because the game's DLLs must not be put into the repository.

## Plugin integration
To do once the current plugin work is merged, so that reloading no longer depends on `ModHooks`:
1. `Plugin.OnDestroy`: stop VR (`VrXr.Stop`), detach the VR camera, restore canvases and helmet, `harmony.UnpatchSelf()`.
2. `VrXr.LoadSettings`: reuse the XR settings if `XRGeneralSettings.Instance` already exists, instead of loading the
   AssetBundle a second time.
3. `VrOptions`: inject into option screens that already exist when the plugin starts. On teardown remove the rows,
   shrink the content again and restore the boundary row's navigation.
4. Reset the static state (`VrBody`, `VrOptions.Rows`, `VrCamera.maxResolutionScale`, log-once flags) when the plugin
   starts.
5. Then remove the matching steps from `ModHooks.cs`. Keep the generic cleanup in `HotReload.cs` (unpatching by
   assembly, destroying leftovers) as a safety net.

The self-test keeps using reflection (`Mod.cs`): a compile-time reference would bind to one specific assembly name,
and after a reload the plugin lives in a renamed copy.

## Layer 4: unit tests (planned)
Move the pure logic out of the MonoBehaviours into a `netstandard2.0` library without Unity or game references
(memory budget and resolution limit, row linking from a list of "interactable/ours" flags, list stepping and value
formatting, playspace resync decision), with xUnit tests and a GitHub Actions workflow. It is the only layer that can
run in CI. It needs changes in the plugin, so it waits for the current plugin work like the integration above.
