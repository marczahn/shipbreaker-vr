using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using BBI.Unity.Game;
using HarmonyLib;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.Rendering;
using UnityEngine.UI;
using UnityEngine.XR;
using Object = UnityEngine.Object;

namespace ShipbreakerVR.Dev
{
    // The in-game checks, in the order they run. Most of them guard against a bug that already happened once
    // (docs/NOTES.md); VR stays on from xr.start until the end, then the runner restores the state from before.
    internal static class SelfTests
    {
        public static readonly SelfTest[] All =
        {
            new SelfTest { Name = "patches.applied", Body = PatchesApplied },
            new SelfTest { Name = "options.rows", Body = OptionRows },
            new SelfTest { Name = "xr.start", Body = XrStart, Timeout = 45f },
            new SelfTest { Name = "ui.overlay-canvases", NeedsVr = true, Body = OverlayCanvases },
            new SelfTest { Name = "stereo.eye-separation", NeedsVr = true, Body = EyeSeparation },
            new SelfTest { Name = "render.targets-shrink", NeedsVr = true, Body = RenderTargetsShrink },
            new SelfTest { Name = "render.resolution-change", NeedsVr = true, Body = ResolutionChange },
            new SelfTest { Name = "aim.screen-size", NeedsGameplay = true, NeedsVr = true, Body = AimScreenSize },
            new SelfTest { Name = "aim.follows-head", NeedsGameplay = true, NeedsVr = true, Body = AimFollowsHead },
            new SelfTest { Name = "options.navigation", Body = OptionsNavigation, Timeout = 60f },
            new SelfTest { Name = "xr.toggle-cycle", NeedsVr = true, Body = ToggleCycle, Timeout = 60f },
            new SelfTest { Name = "log.errors", Body = LogErrors },
        };

        private static readonly List<XRDisplaySubsystem> Displays = new List<XRDisplaySubsystem>();

        // Every patch class of the plugin has at least one applied patch, and every transpiler changed its method.
        // The plugin applies the classes one by one and only logs a failing one.
        private static IEnumerator PatchesApplied(TestContext t)
        {
            var assembly = t.Mod.Assembly;
            var classes = AccessTools.GetTypesFromAssembly(assembly).Where(c => c.GetCustomAttributes(typeof(HarmonyPatch), false).Length > 0).ToList();
            var applied = new HashSet<Type>();
            var transpiled = new List<System.Reflection.MethodBase>();
            foreach (var method in Harmony.GetAllPatchedMethods())
            {
                var info = Harmony.GetPatchInfo(method);
                if (info == null) continue;
                foreach (var patch in info.Prefixes.Concat(info.Postfixes).Concat(info.Transpilers).Concat(info.Finalizers))
                    if (patch.PatchMethod?.DeclaringType?.Assembly == assembly) applied.Add(patch.PatchMethod.DeclaringType);
                if (info.Transpilers.Any(p => p.PatchMethod?.DeclaringType?.Assembly == assembly)) transpiled.Add(method);
            }
            var missing = classes.Where(c => !applied.Contains(c)).Select(c => c.Name).ToList();
            t.Check(missing.Count == 0, $"not applied: {string.Join(", ", missing)}");
            foreach (var method in transpiled)
            {
                var original = PatchProcessor.GetOriginalInstructions(method);
                var current = PatchProcessor.GetCurrentInstructions(method);
                t.Check(!SameCode(original, current), $"transpiler changed nothing in {method.DeclaringType?.Name}.{method.Name}");
            }
            t.Note($"{classes.Count} patch classes applied, {transpiled.Count} method(s) transpiled");
            yield break;
        }

        // The VR rows exist in the video options and are all handled by the current plugin (no stale copies after a reload).
        private static IEnumerator OptionRows(TestContext t)
        {
            var screens = ModHooks.SceneObjects<VideoOptionsScreenController>().ToList();
            if (screens.Count == 0) t.Skip("the options screen is not loaded yet");
            var withRows = 0;
            foreach (var screen in screens)
            {
                var entries = screen.GetComponentsInChildren<OptionEntry>(true);
                var ours = entries.Where(e => e.name.StartsWith(ModHooks.ObjectPrefix)).ToList();
                // Awake (where the rows are added) has run for sure once the screen is visible.
                if (ours.Count == 0)
                {
                    t.Check(!screen.gameObject.activeInHierarchy, $"no VR rows in the visible options screen '{screen.name}'");
                    continue;
                }
                withRows++;
                var stale = ours.Where(e => !t.Mod.IsOurs(e)).Select(e => e.name).ToList();
                t.Check(stale.Count == 0, $"rows the plugin does not handle in '{screen.name}': {string.Join(", ", stale)}");
                var list = ours[0].transform.parent;
                var firstOurs = ours.Min(e => e.transform.GetSiblingIndex());
                var gameAfter = entries.Where(e => e.transform.parent == list && !ours.Contains(e) && e.transform.GetSiblingIndex() > firstOurs).Select(e => e.name).ToList();
                t.Check(gameAfter.Count == 0, $"game rows below the VR rows: {string.Join(", ", gameAfter)}");
                t.Note($"'{screen.name}': {ours.Count} VR rows");
            }
            if (withRows == 0) t.Skip("no VR rows yet; open the options once");
            yield break;
        }

        private static IEnumerator XrStart(TestContext t)
        {
            if (!t.Mod.VrRunning) t.Mod.ToggleVr();
            t.Check(t.Mod.VrRunning, "VR did not start; is an OpenXR runtime running (Virtual Desktop, SteamVR, XR simulator)? See the log");
            yield return t.Until(DisplayRunning, 20f, "the XR display");
            yield return t.Until(() => t.Mod.EyeCamera != null && t.Mod.EyeCamera.enabled, 10f, "the VR camera");
            t.Note($"{XRSettings.loadedDeviceName}, eye texture {XRSettings.eyeTextureWidth}x{XRSettings.eyeTextureHeight}");
        }

        // Overlay canvases would end up in one eye only; with the UI in the world they must all be on the UI camera.
        private static IEnumerator OverlayCanvases(TestContext t)
        {
            if (!t.Mod.Setting<bool>("UiInVr").Value) t.Skip("HUD and menus in the world are off");
            // The plugin scans for canvases every half second.
            yield return TestContext.Seconds(1.5f);
            var overlay = Object.FindObjectsOfType<Canvas>()
                .Where(c => c.isRootCanvas && c.isActiveAndEnabled && c.renderMode == RenderMode.ScreenSpaceOverlay)
                .Select(c => c.name).ToList();
            t.Check(overlay.Count == 0, $"overlay canvases left: {string.Join(", ", overlay)}");
        }

        // The eyes are an IPD apart, times the world scale, and doubling the world scale doubles their distance.
        private static IEnumerator EyeSeparation(TestContext t)
        {
            var camera = t.Mod.EyeCamera;
            t.Check(camera != null, "no VR camera");
            var setting = t.Mod.Setting<float>("WorldScale");
            var original = setting.Value;
            t.OnCleanup(() => setting.Value = original);
            var first = Mathf.Clamp(original, 0.5f, 3f);

            var separation = 0f;
            // Both eyes sit at the same spot until the first tracked pose arrives.
            yield return t.Until(() => (separation = Separation(camera)) > 0.001f, 15f, "tracked eye poses");
            var ipd = separation * 1000f / first;
            t.Check(ipd > 50f && ipd < 80f, $"eyes {separation * 1000f:0} mm apart at world scale {first:0.00}, i.e. an IPD of {ipd:0} mm");

            var second = first < 1.5f ? 2f : 1f;
            setting.Value = second;
            yield return TestContext.Frames(5);
            var scaled = 0f;
            yield return t.Until(() => (scaled = Separation(camera)) > 0.001f, 5f, "tracked eye poses");
            var ratio = scaled / separation;
            t.Check(Mathf.Abs(ratio / (second / first) - 1f) < 0.03f,
                $"world scale {first:0.00} -> {second:0.00} changed the eye distance by {ratio:0.00}x instead of {second / first:0.00}x");
            t.Note($"IPD {ipd:0} mm, world scale {second:0.00}: {scaled * 1000f:0} mm");
        }

        // HDRP's shared render targets only grow; the plugin shrinks them to the eye size two seconds after VR starts.
        private static IEnumerator RenderTargetsShrink(TestContext t)
        {
            yield return TestContext.Seconds(3f);
            int width = XRSettings.eyeTextureWidth, height = XRSettings.eyeTextureHeight;
            t.Check(width > 0 && height > 0, "no eye texture size");
            int maxWidth = RTHandles.maxWidth, maxHeight = RTHandles.maxHeight;
            t.Note($"eye {width}x{height}, HDRP targets {maxWidth}x{maxHeight}");
            t.Check((float)maxWidth * maxHeight < 1.05f * width * height, $"HDRP targets {maxWidth}x{maxHeight} stay larger than the eye size {width}x{height}");
        }

        // A changed render resolution is applied after the slider rested for a second, down and back up again.
        private static IEnumerator ResolutionChange(TestContext t)
        {
            var setting = t.Mod.Setting<float>("ResolutionScale");
            var original = setting.Value;
            t.OnCleanup(() => setting.Value = original);
            var applied = XRSettings.eyeTextureResolutionScale;
            var lower = Mathf.Abs(applied - 0.8f) < 0.01f ? 0.7f : 0.8f;

            setting.Value = lower;
            yield return t.Until(() => Mathf.Abs(XRSettings.eyeTextureResolutionScale - lower) < 0.01f, 6f, $"render resolution {lower:0.00}x");
            var lowWidth = XRSettings.eyeTextureWidth;
            setting.Value = original;
            yield return t.Until(() => Mathf.Abs(XRSettings.eyeTextureResolutionScale - applied) < 0.01f, 8f, $"render resolution {applied:0.00}x again");
            t.Note($"{applied:0.00}x ({XRSettings.eyeTextureWidth} px) -> {lower:0.00}x ({lowWidth} px) -> back");
        }

        // Tools aim through the flat game camera. With XR on, the game used to take the eye texture size as screen
        // size, so a ray through the screen centre went off to the side and the cutter fired upwards.
        private static IEnumerator AimScreenSize(TestContext t)
        {
            yield return TestContext.Frames(3);
            var camera = LynxCameraController.MainCamera;
            t.Check(camera != null, "no game camera");
            t.Check(LynxCameraController.ScreenWidth == camera.pixelWidth && LynxCameraController.ScreenHeight == camera.pixelHeight,
                $"tools aim with a screen of {LynxCameraController.ScreenWidth}x{LynxCameraController.ScreenHeight}, the game camera has {camera.pixelWidth}x{camera.pixelHeight}");
            var ray = camera.ScreenPointToRay(LynxCameraController.ScreenCenter);
            var angle = Vector3.Angle(ray.direction, camera.transform.forward);
            t.Check(angle < 0.5f, $"a tool ray through the screen centre is {angle:0.0} degrees off the view direction");
            t.Note($"{camera.pixelWidth}x{camera.pixelHeight}, ray off by {angle:0.00} degrees");
        }

        // With body-follows-head the tools aim where the head looks.
        private static IEnumerator AimFollowsHead(TestContext t)
        {
            if (!t.Mod.Setting<bool>("BodyFollowsHead").Value) t.Skip("body follows head is off");
            yield return TestContext.Seconds(0.5f);
            var game = LynxCameraController.MainCameraTransform;
            var eye = t.Mod.EyeCamera;
            t.Check(game != null && eye != null, "game or VR camera missing");
            // The body follows one physics step late; take the best of a few frames.
            var best = float.MaxValue;
            for (var i = 0; i < 10; i++)
            {
                best = Mathf.Min(best, Vector3.Angle(game.forward, eye.transform.forward));
                yield return null;
            }
            t.Check(best < 3f, $"tools aim {best:0.0} degrees away from where the head looks");
            t.Note($"{best:0.00} degrees");
        }

        // Walks the visible options list downwards like the arrow keys: every row must keep the selection (it used
        // to jump back up while the list scrolled) and every usable VR row must be reachable, and the way back up too.
        private static IEnumerator OptionsNavigation(TestContext t)
        {
            var navigation = Object.FindObjectsOfType(t.Mod.Type("VrOptionsNavigation")).OfType<Component>().FirstOrDefault(c => c.gameObject.activeInHierarchy);
            if (navigation == null) t.Skip("open the Video tab of the options to run this");
            var events = EventSystem.current;
            t.Check(events != null, "no EventSystem");
            var previous = events.currentSelectedGameObject;
            t.OnCleanup(() =>
            {
                if (EventSystem.current != null) EventSystem.current.SetSelectedGameObject(previous);
            });
            // The plugin relinks the rows every quarter second.
            yield return TestContext.Seconds(0.5f);

            var rows = new List<KeyValuePair<Selectable, bool>>();
            foreach (Transform child in navigation.transform)
            {
                var entry = child.GetComponent<OptionEntry>();
                if (entry == null || !child.gameObject.activeInHierarchy) continue;
                var button = Traverse.Create(entry).Field("m_BackgroundButton").GetValue<Component>();
                var selectable = button != null ? button.GetComponent<Selectable>() : null;
                if (selectable != null && selectable.IsInteractable()) rows.Add(new KeyValuePair<Selectable, bool>(selectable, t.Mod.IsOurs(entry)));
            }
            var ours = rows.Where(r => r.Value).Select(r => r.Key).ToList();
            t.Check(ours.Count > 0, "no usable VR rows in the list");

            var visited = new List<Selectable>();
            var current = rows[0].Key;
            while (current != null && !visited.Contains(current) && visited.Count < 200)
            {
                events.SetSelectedGameObject(current.gameObject);
                // Selecting scrolls the list; give the game's scroll handling a few frames.
                yield return TestContext.Frames(4);
                var now = events.currentSelectedGameObject;
                t.Check(now == current.gameObject, $"selection jumped to '{Name(now)}' instead of staying on '{Name(current.gameObject)}'");
                visited.Add(current);
                current = Next(current, down: true);
            }
            var missed = ours.Where(s => !visited.Contains(s)).Select(s => Name(s.gameObject)).ToList();
            t.Check(missed.Count == 0, $"not reachable with the down key: {string.Join(", ", missed)}");

            if (rows.Any(r => !r.Value))
            {
                var up = ours[ours.Count - 1];
                for (var i = 0; up != null && ours.Contains(up) && i < 200; i++) up = Next(up, down: false);
                t.Check(up != null, "the up key leads nowhere from the first VR row");
            }
            t.Note($"{visited.Count} rows walked, {ours.Count} VR rows");
        }

        // VR off and on again, twice: the game camera takes over and hands back each time.
        private static IEnumerator ToggleCycle(TestContext t)
        {
            for (var cycle = 1; cycle <= 2; cycle++)
            {
                t.Mod.ToggleVr();
                t.Check(!t.Mod.VrRunning, $"VR did not stop (cycle {cycle})");
                yield return TestContext.Seconds(1f);
                var eye = t.Mod.EyeCamera;
                t.Check(eye == null || !eye.enabled, $"the VR camera still renders after VR stopped (cycle {cycle})");
                if (TestContext.InGameplay) t.Check(LynxCameraController.MainCamera.enabled, $"the game camera stays off after VR stopped (cycle {cycle})");

                t.Mod.ToggleVr();
                t.Check(t.Mod.VrRunning, $"VR did not start again (cycle {cycle})");
                yield return t.Until(() => DisplayRunning() && t.Mod.EyeCamera != null && t.Mod.EyeCamera.enabled, 15f, $"VR rendering again (cycle {cycle})");
                yield return TestContext.Frames(2);
                if (TestContext.InGameplay) t.Check(!LynxCameraController.MainCamera.enabled, $"the game camera still renders in VR (cycle {cycle})");
            }
        }

        private static IEnumerator LogErrors(TestContext t)
        {
            var errors = LogWatch.Since(0);
            t.Check(errors.Count == 0, $"{errors.Count} error(s) since the game started: {string.Join(" || ", errors.Take(3))}");
            yield break;
        }

        private static bool DisplayRunning()
        {
            SubsystemManager.GetSubsystems(Displays);
            return Displays.Any(d => d.running);
        }

        // Distance between the eyes in the world, from the view matrices HDRP renders with (as the plugin logs it).
        private static float Separation(Camera camera)
        {
            SubsystemManager.GetSubsystems(Displays);
            if (Displays.Count == 0) return 0f;
            var display = Displays[0];
            Vector3? first = null;
            for (var p = 0; p < display.GetRenderPassCount(); p++)
            {
                display.GetRenderPass(p, out var pass);
                for (var i = 0; i < pass.GetRenderParameterCount(); i++)
                {
                    pass.GetRenderParameter(camera, i, out var parameter);
                    Vector3 eye = parameter.view.inverse.GetColumn(3);
                    if (first == null) first = eye;
                    else return Vector3.Distance(first.Value, eye);
                }
            }
            return 0f;
        }

        // Where the game's input bridge would move: explicit links only to an active, interactable target.
        private static Selectable Next(Selectable from, bool down)
        {
            var navigation = from.navigation;
            var target = navigation.mode == Navigation.Mode.Explicit
                ? (down ? navigation.selectOnDown : navigation.selectOnUp)
                : (down ? from.FindSelectableOnDown() : from.FindSelectableOnUp());
            return target != null && target.isActiveAndEnabled && target.IsInteractable() ? target : null;
        }

        private static bool SameCode(List<CodeInstruction> a, List<CodeInstruction> b) =>
            a.Count == b.Count && a.Zip(b, (x, y) => x.opcode == y.opcode && Equals(x.operand, y.operand)).All(same => same);

        private static string Name(GameObject go)
        {
            if (go == null) return "-";
            var entry = go.GetComponentInParent<OptionEntry>();
            return entry != null ? entry.name : go.name;
        }
    }
}
