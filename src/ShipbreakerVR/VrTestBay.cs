using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using BBI.Unity.Game;
using HarmonyLib;
using UnityEngine;
using UnityEngine.Rendering.HighDefinition;
using UnityEngine.VFX;

namespace ShipbreakerVR
{
    // Test mode: what the bay's furnaces and processors cost. A script switches their parts off one at a time and
    // names each stretch with 'stats', so the frame time lines of the stretches can be compared; 'bay feed' puts loose
    // parts into an acceptor to show the cost per accepted part. What the game does there is in docs/NOTES.md, "What
    // the furnaces cost". The commands (VrTestScript, example in tools/vr-tests/bay-cost.txt):
    //   bay survey                    logs the flow volumes and the bodies in them, the acceptors, the rigidbodies
    //                                 (kinematic, awake), the lights with shadows and the visual effects
    //   bay flow on|off [TEXT]        the pull towards furnaces and processors (the game's ForceVolume)
    //   bay triggers on|off [TEXT]    the acceptors' trigger boxes: nothing is accepted while they are off
    //   bay shadows on|off [TEXT]     the shadows of lights
    //   bay lights on|off [TEXT]      lights as a whole
    //   bay effects on|off [TEXT]     visual effects (VFX Graph): paused and not drawn
    //   bay feed N [TEXT]             moves up to N loose parts into the nearest acceptor. This changes the shift
    //                                 (the parts are salvaged or lost), so use a save that does not matter
    // TEXT narrows a command to objects whose path contains it (see 'bay survey' for names), * means all. Without it,
    // flow and triggers take all, shadows, lights and effects those with "Furnace" in their path, feed "Trigger_Furnace".
    // 'on' brings back everything the command switched off; so do the end of the script and unloading.
    internal static class VrTestBay
    {
        static VrTestBay() => VrSession.OnUnload("bay test", Restore);

        // Cells of the grid 'feed' lays parts out on, and the largest part that fits one.
        internal const float Cell = 1.5f;
        private const float Largest = 1.3f;
        private const int Listed = 12;

        // What each command switched off, as the steps that bring it back.
        private static readonly Dictionary<string, List<Action>> Undo = new Dictionary<string, List<Action>>();

        public static void Execute(string[] words)
        {
            if (words.Length < 2) throw new FormatException("bay needs survey, flow, triggers, shadows, lights, effects or feed");
            var what = words[1].ToLowerInvariant();
            switch (what)
            {
                case "survey":
                    Survey();
                    return;
                case "feed":
                    if (words.Length < 3 || !int.TryParse(words[2], out var count) || count < 1) throw new FormatException("bay feed needs a number of parts");
                    Feed(count, words.Length > 3 ? words[3] : "Trigger_Furnace");
                    return;
                case "flow":
                case "triggers":
                case "shadows":
                case "lights":
                case "effects":
                {
                    if (words.Length < 3) throw new FormatException($"bay {what} needs on or off");
                    var state = words[2].ToLowerInvariant();
                    if (state != "on" && state != "off") throw new FormatException($"'{words[2]}' is not on or off");
                    // Off starts from the game's state too, so a second 'off' with another TEXT replaces the first.
                    Restore(what);
                    if (state == "on") return;
                    var text = words.Length > 3 ? words[3] : what == "flow" || what == "triggers" ? "*" : "Furnace";
                    var undo = new List<Action>();
                    var names = SwitchOff(what, text, undo);
                    Undo[what] = undo;
                    Plugin.Log.LogInfo($"Bay {what} off ('{text}'): {names.Count} switched off{(names.Count > 0 ? ": " + string.Join(", ", names.Take(Listed).ToArray()) + (names.Count > Listed ? ", ..." : "") : "")}");
                    return;
                }
                default:
                    throw new FormatException($"unknown bay command '{words[1]}'");
            }
        }

        // Everything back as the game had it. Also from VrTestScript when a script ends.
        public static void Restore()
        {
            foreach (var what in Undo.Keys.ToList()) Restore(what);
        }

        private static void Restore(string what)
        {
            if (!Undo.TryGetValue(what, out var undo)) return;
            Undo.Remove(what);
            foreach (var step in undo)
            {
                try { step(); }
                catch (Exception e) { Plugin.Log.LogWarning($"Bay {what} on: {e.Message}"); }
            }
            Plugin.Log.LogInfo($"Bay {what} on again");
        }

        private static List<string> SwitchOff(string what, string text, List<Action> undo)
        {
            var names = new List<string>();
            switch (what)
            {
                case "flow":
                    foreach (var volume in UnityEngine.Object.FindObjectsOfType<ForceVolume>())
                    {
                        if (!Matches(volume, text)) continue;
                        var changed = Off(volume, undo);
                        foreach (var collider in volume.GetComponents<Collider>()) changed |= Off(collider, undo);
                        // A trigger that is switched off sends no exit, so the volume would go on pulling the bodies
                        // it held, wherever they are by then, once it is back on.
                        Bodies(volume)?.Clear();
                        if (changed) names.Add(volume.name);
                    }
                    // Back on, a trigger only notices bodies that are awake.
                    undo.Add(() =>
                    {
                        foreach (var body in UnityEngine.Object.FindObjectsOfType<Rigidbody>())
                            if (!body.isKinematic) body.WakeUp();
                    });
                    break;
                case "triggers":
                    // Only the colliders: the acceptors' coroutines, which destroy the parts already accepted, go on.
                    foreach (var acceptor in UnityEngine.Object.FindObjectsOfType<SalvageAcceptorVolumeBase>()) TriggerOff(acceptor, text, undo, names);
                    foreach (var smoke in UnityEngine.Object.FindObjectsOfType<PreFurnaceVolume>()) TriggerOff(smoke, text, undo, names);
                    break;
                case "shadows":
                    foreach (var light in UnityEngine.Object.FindObjectsOfType<Light>())
                    {
                        if (!light.enabled || light.shadows == LightShadows.None || !Matches(light, text)) continue;
                        var before = light.shadows;
                        // HDRP renders a light's shadow map only while this is not None.
                        light.shadows = LightShadows.None;
                        undo.Add(() => { if (light != null) light.shadows = before; });
                        names.Add(light.name);
                    }
                    break;
                case "lights":
                    foreach (var light in UnityEngine.Object.FindObjectsOfType<Light>())
                        if (Matches(light, text) && Off(light, undo)) names.Add(light.name);
                    break;
                case "effects":
                    // Paused and not drawn, not disabled: a disabled effect comes back empty, and the furnaces' gas
                    // clouds then stayed empty (nothing sends them their start event a second time).
                    foreach (var effect in UnityEngine.Object.FindObjectsOfType<VisualEffect>())
                    {
                        if (!effect.enabled || effect.pause || !Matches(effect, text)) continue;
                        var renderer = effect.GetComponent<Renderer>();
                        var drawn = renderer != null && renderer.enabled;
                        effect.pause = true;
                        if (drawn) renderer.enabled = false;
                        undo.Add(() =>
                        {
                            if (effect != null) effect.pause = false;
                            if (drawn && renderer != null) renderer.enabled = true;
                        });
                        names.Add(effect.name);
                    }
                    break;
            }
            return names;
        }

        private static void TriggerOff(Component volume, string text, List<Action> undo, List<string> names)
        {
            if (!Matches(volume, text)) return;
            var changed = false;
            foreach (var collider in volume.GetComponents<Collider>()) changed |= Off(collider, undo);
            if (changed && !names.Contains(volume.name)) names.Add(volume.name);
        }

        private static bool Off(Behaviour behaviour, List<Action> undo)
        {
            if (!behaviour.enabled) return false;
            behaviour.enabled = false;
            undo.Add(() => { if (behaviour != null) behaviour.enabled = true; });
            return true;
        }

        private static bool Off(Collider collider, List<Action> undo)
        {
            if (!collider.enabled) return false;
            collider.enabled = false;
            undo.Add(() => { if (collider != null) collider.enabled = true; });
            return true;
        }

        private static bool Matches(Component component, string text) =>
            text == "*" || VrUtil.Path(component.transform).IndexOf(text, StringComparison.OrdinalIgnoreCase) >= 0;

        // The bodies a flow volume pulls each physics step.
        private static List<Rigidbody> Bodies(ForceVolume volume) => Traverse.Create(volume).Field<List<Rigidbody>>("mAffectedRigidbodies").Value;

        private static void Survey()
        {
            var text = new StringBuilder();
            var flows = UnityEngine.Object.FindObjectsOfType<ForceVolume>();
            var inside = 0;
            foreach (var flow in flows)
            {
                var count = Bodies(flow)?.Count(body => body != null) ?? 0;
                if (count == 0) continue;
                inside += count;
                text.Append($", {flow.name} {count}");
            }
            Plugin.Log.LogInfo($"Bay survey: {flows.Length} flow volumes ({flows.Count(flow => flow.enabled)} on) pulling {inside} bodies{text}");

            text.Length = 0;
            foreach (var acceptor in UnityEngine.Object.FindObjectsOfType<SalvageAcceptorVolumeBase>())
            {
                var fields = Traverse.Create(acceptor);
                var parts = fields.Field<HashSet<StructurePart>>("mProcessedParts").Value?.Count ?? 0;
                var groups = fields.Field<HashSet<StructureGroup>>("mProcessedGroups").Value?.Count ?? 0;
                var collider = acceptor.GetComponent<Collider>();
                text.Append($"{(text.Length > 0 ? ", " : "")}{acceptor.GetType().Name} '{acceptor.name}'{(collider != null && collider.enabled ? "" : " (trigger off)")} holds {parts} parts and {groups} groups");
            }
            Plugin.Log.LogInfo($"Bay survey: acceptors: {(text.Length > 0 ? text.ToString() : "none")}; {UnityEngine.Object.FindObjectsOfType<PreFurnaceVolume>().Length} pre-furnace volumes");

            var bodies = UnityEngine.Object.FindObjectsOfType<Rigidbody>();
            var kinematic = 0;
            var awake = 0;
            foreach (var body in bodies)
            {
                if (body.isKinematic) kinematic++;
                else if (!body.IsSleeping()) awake++;
            }
            var colliders = UnityEngine.Object.FindObjectsOfType<Collider>();
            // Parts an acceptor took are on this layer until they are destroyed.
            var acceptedLayer = LayerMask.NameToLayer("Surface_NoAttach");
            Plugin.Log.LogInfo($"Bay survey: {bodies.Length} rigidbodies ({kinematic} kinematic, {awake} awake), {colliders.Length} colliders ({colliders.Count(c => c.isTrigger)} triggers, {colliders.Count(c => c.gameObject.layer == acceptedLayer)} on the accepted parts' layer)");

            var eye = VrCamera.Instance != null ? VrCamera.Instance.Camera.transform.position : (Vector3?)null;
            var lights = UnityEngine.Object.FindObjectsOfType<Light>().Where(l => l.enabled && l.shadows != LightShadows.None)
                .OrderByDescending(l => l.type == LightType.Directional ? float.MaxValue : l.range).ToList();
            Plugin.Log.LogInfo($"Bay survey: {lights.Count} lights with shadows{(lights.Count > Listed ? $", the {Listed} that reach furthest" : "")}:");
            foreach (var light in lights.Take(Listed))
            {
                var data = light.GetComponent<HDAdditionalLightData>();
                var away = eye.HasValue ? $", {Vector3.Distance(eye.Value, light.transform.position):0} m away" : "";
                var shadows = data != null ? $", shadows to {data.shadowFadeDistance:0} m, updated {data.shadowUpdateMode}" : "";
                Plugin.Log.LogInfo($"  {light.type} '{VrUtil.Path(light.transform)}': range {light.range:0} m, intensity {light.intensity:0}{away}{shadows}");
            }

            var effects = UnityEngine.Object.FindObjectsOfType<VisualEffect>().Where(e => e.enabled && !e.pause).OrderByDescending(e => e.aliveParticleCount).ToList();
            text.Length = 0;
            foreach (var effect in effects.Take(Listed)) text.Append($", '{effect.name}' {effect.aliveParticleCount}");
            Plugin.Log.LogInfo($"Bay survey: {effects.Count} visual effects with {effects.Sum(e => (long)e.aliveParticleCount)} particles{text}");
        }

        // Loose parts onto a grid inside the acceptor's trigger box, so they are accepted in the next physics step and
        // the flow behind it carries them off. Loose: an own moving rigidbody with a part of a ship on it, no joint to
        // anything else, small enough for a cell, and not the player, in the barge or already accepted.
        private static void Feed(int count, string text)
        {
            var eye = VrCamera.Instance != null ? VrCamera.Instance.Camera.transform.position : Vector3.zero;
            BoxCollider box = null;
            foreach (var acceptor in UnityEngine.Object.FindObjectsOfType<SalvageAcceptorVolumeBase>())
            {
                var own = acceptor.GetComponent<BoxCollider>();
                if (own == null || !own.enabled || !Matches(acceptor, text)) continue;
                if (box == null || (own.bounds.center - eye).sqrMagnitude < (box.bounds.center - eye).sqrMagnitude) box = own;
            }
            if (box == null) throw new FormatException($"no acceptor with '{text}' in its path and a trigger box that is on");

            // The grid spans the box's two wide sides, in the middle of its depth.
            var scale = box.transform.lossyScale;
            var size = new Vector3(Mathf.Abs(box.size.x * scale.x), Mathf.Abs(box.size.y * scale.y), Mathf.Abs(box.size.z * scale.z));
            if (!Grid(size, out var a, out var b, out var columns, out var rows)) throw new FormatException($"'{box.name}' is too small for parts");

            var jointed = new HashSet<Rigidbody>();
            foreach (var joint in UnityEngine.Object.FindObjectsOfType<Joint>())
            {
                var own = joint.GetComponent<Rigidbody>();
                if (own != null) jointed.Add(own);
                if (joint.connectedBody != null) jointed.Add(joint.connectedBody);
            }
            var skipped = LayerMask.GetMask("Ignore Raycast", "Surface_NoAttach", "Gameplay_Player", "Barge_Salvaged", "Static");
            var loose = new List<(Rigidbody Body, Vector3 Center)>();
            var moving = 0;
            foreach (var body in UnityEngine.Object.FindObjectsOfType<Rigidbody>())
            {
                if (body.isKinematic) continue;
                moving++;
                if (jointed.Contains(body) || body.GetComponentInChildren<StructurePart>() == null) continue;
                if (!TryBounds(body, skipped, out var bounds)) continue;
                if (Mathf.Max(bounds.size.x, Mathf.Max(bounds.size.y, bounds.size.z)) > Largest) continue;
                loose.Add((body, bounds.center));
            }
            // The nearest first, so the same script takes the same parts.
            var target = box.bounds.center;
            loose.Sort((x, y) => (x.Center - target).sqrMagnitude.CompareTo((y.Center - target).sqrMagnitude));

            var fed = Mathf.Min(count, Mathf.Min(loose.Count, columns * rows));
            for (var i = 0; i < fed; i++)
            {
                var local = box.center;
                local[a] += Share(i % columns, columns) * box.size[a];
                local[b] += Share(i / columns, rows) * box.size[b];
                var (body, center) = loose[i];
                body.position += box.transform.TransformPoint(local) - center;
                body.velocity = Vector3.zero;
                body.angularVelocity = Vector3.zero;
                body.WakeUp();
            }
            Plugin.Log.LogInfo($"Bay feed: {fed} of {count} parts moved into '{VrUtil.Path(box.transform)}' ({loose.Count} loose parts up to {Largest:0.0} m among {moving} moving rigidbodies, {columns * rows} cells)");
        }

        // The grid for a box of this size (meters along its own axes): which two axes it spans, the two wide ones, and
        // how many cells fit along each. False when not even one fits.
        internal static bool Grid(Vector3 size, out int a, out int b, out int columns, out int rows)
        {
            var thin = size.x <= size.y && size.x <= size.z ? 0 : size.y <= size.z ? 1 : 2;
            a = (thin + 1) % 3;
            b = (thin + 2) % 3;
            columns = Mathf.FloorToInt(size[a] / Cell);
            rows = Mathf.FloorToInt(size[b] / Cell);
            return columns >= 1 && rows >= 1;
        }

        // The middle of cell index of cells along a side, as a share of the side from its middle (-0.5 to 0.5).
        internal static float Share(int index, int cells) => (index + 0.5f) / cells - 0.5f;

        // The bounds of the body's own solid colliders; false without any, or with one on a layer to leave alone.
        private static bool TryBounds(Rigidbody body, int skippedLayers, out Bounds bounds)
        {
            bounds = default;
            var found = false;
            foreach (var collider in body.GetComponentsInChildren<Collider>())
            {
                if (!collider.enabled || collider.isTrigger || collider.attachedRigidbody != body) continue;
                if ((1 << collider.gameObject.layer & skippedLayers) != 0) return false;
                if (found) bounds.Encapsulate(collider.bounds);
                else bounds = collider.bounds;
                found = true;
            }
            return found && (1 << body.gameObject.layer & skippedLayers) == 0;
        }
    }
}
