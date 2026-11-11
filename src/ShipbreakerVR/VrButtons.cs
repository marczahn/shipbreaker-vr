using System.Collections.Generic;
using BBI.Unity.Game;
using UnityEngine;

namespace ShipbreakerVR
{
    // Buttons pressed by touch (Raik, 0.29): the big black button of an air conditioner is pushed in with the hand, not
    // used with A. The tip of the hand (Tip, ahead of the controller along its pointing direction; no index finger yet)
    // going PressDepth into the button's face uses its control, as A would, once its control may be used again
    // (VrLever.Ready); out of it again, the next push may use it again. The button sinks in with the hand (Travel at most)
    // and springs back, and a press gives a short click. A button with no control (a broken air conditioner) sinks in and
    // clicks the same, and nothing else happens.
    //
    // Measured (VrInteractSurvey, 2026-11-07): the button is its own part 'Unlock_01' with the mesh
    // 'SM_AtmosphereRegulator_Mini_1Bx1xA_Button', under the air conditioner's part, whose control is the 'Interaction
    // Volume' next to it. Buttons are found as parts with a mesh named like a button on the part near the hand.
    internal static class VrButtons
    {
        // How far ahead of the controller's grip, along its pointing direction, the tip pushing buttons is, in m at world
        // scale 1: about the controller's front for now (Raik: pushing with the controller is enough to start with).
        private const float TipAhead = 0.03f;
        // Pushed this far into the face, the button is used; back out past Release, it may be used again.
        private const float PressDepth = 0.006f;
        private const float Release = 0.002f;
        // This near in front of the face, its control is offered to the game (Touch).
        private const float Approach = 0.05f;
        // How far the button sinks in at most.
        private const float Travel = 0.012f;
        // The tip counts on the face within this beyond its edges.
        private const float Beside = 0.01f;
        // Parts near the hand looked through this often, this far around the tip.
        private const float LookEvery = 0.15f;
        private const float LookAround = 0.25f;
        // A button is no larger than this.
        private const float Largest = 0.3f;
        private const float ClickAmplitude = 0.7f;
        private const float ClickLength = 0.03f;
        // After a press the game's hand would reach for the control: the hands stay with their controllers this long.
        private const float HandsKept = 1f;

        private class Button
        {
            public MeshFilter Filter;
            public InteractableObject Control;
            public Vector3 Rest;
            // The face's direction in the button's own axes (outwards, away from the middle of its part), its depth and size.
            public Vector3 Out;
            // Whether Out is known; else it is the side the tip first comes from.
            public bool OutKnown;
            public float Half;
            // Parts that sink with it: the light on its face ('Unlock_01'), where they rest.
            public readonly List<Transform> With = new List<Transform>();
            public readonly List<Vector3> WithRest = new List<Vector3>();
            public Vector3 Extents;
            public Vector3 Center;
            public float Sunk;
        }

        private class Tip
        {
            public readonly VrInput.Hand Input;
            public float LookAt;
            public readonly List<Button> Near = new List<Button>();
            // Per button the tip is in: armed again once out.
            public readonly HashSet<Button> In = new HashSet<Button>();
            // The button the tip pushes in this frame, if any (VrTouchSound).
            public Button Pushing;

            public Tip(VrInput.Hand input) => Input = input;
        }

        private static readonly Tip[] Tips = { new Tip(VrInput.Left), new Tip(VrInput.Right) };
        private static readonly Dictionary<Transform, List<Button>> ByPart = new Dictionary<Transform, List<Button>>();
        private static readonly HashSet<Button> Sinking = new HashSet<Button>();
        private static readonly Collider[] Found = new Collider[32];
        private static readonly List<Button> Done = new List<Button>();
        private static InteractableObject offered;
        private static float offeredUntil;
        private static float pressedAt = -10f;
        private static int logs;

        // The control a button press uses, offered to the game for a moment (VrInteractHeld), or null.
        public static InteractableObject Offered => Time.time < offeredUntil ? offered : null;

        // A button was pressed just now: the game's hand would reach for its control (VrHands keeps them).
        public static bool JustPressed => Time.time - pressedAt < HandsKept;

        // From VrFrame, in LateUpdate.
        public static void LateUpdate()
        {
            var pushed = new Dictionary<Button, float>();
            if (VrXr.IsRunning && VrAim.Active)
                foreach (var tip in Tips) Touch(tip, pushed);
            else
                foreach (var tip in Tips) tip.Pushing = null;
            // Each button as far in as the deepest tip, back out when none.
            foreach (var each in pushed) Sinking.Add(each.Key);
            Done.Clear();
            foreach (var button in Sinking)
            {
                if (button.Filter == null)
                {
                    Done.Add(button);
                    continue;
                }
                pushed.TryGetValue(button, out var depth);
                button.Sunk = Mathf.Clamp(depth, 0f, Travel * VrCamera.WorldScale);
                var t = button.Filter.transform;
                t.localPosition = button.Rest;
                for (var i = 0; i < button.With.Count; i++)
                    if (button.With[i] != null) button.With[i].localPosition = button.WithRest[i];
                if (button.Sunk > 0f)
                {
                    var down = t.TransformDirection(button.Out).normalized * button.Sunk;
                    t.position -= down;
                    foreach (var with in button.With)
                        if (with != null) with.position -= down;
                }
                else Done.Add(button);
            }
            foreach (var button in Done) Sinking.Remove(button);
        }

        // The button that controller's tip pushes in, as its transform, or false.
        public static bool TryGetPushed(VrInput.Hand input, out Transform button)
        {
            button = null;
            foreach (var tip in Tips)
                if (tip.Input == input && tip.Pushing != null && tip.Pushing.Filter != null) button = tip.Pushing.Filter.transform;
            return button != null;
        }

        private static void Touch(Tip tip, Dictionary<Button, float> pushed)
        {
            tip.Pushing = null;
            if (VrClimb.TryGetHold(tip.Input, out _)) { tip.In.Clear(); return; }
            if (!tip.Input.TryGetGripPose(out var gripLocal, out _) || !tip.Input.TryGetAimRotation(out var aimLocal)) return;
            if (!VrToolModels.TrackingToWorld(gripLocal, aimLocal, out var grip, out var aim)) return;
            var at = grip + aim * Vector3.forward * (TipAhead * VrCamera.WorldScale);
            if (Time.time >= tip.LookAt)
            {
                tip.LookAt = Time.time + LookEvery;
                Look(tip, at);
            }
            foreach (var button in tip.Near)
            {
                if (button.Filter == null) continue;
                var t = button.Filter.transform;
                // In the button's own axes, from where it rests (not where it sank to).
                var rest = t.parent != null ? t.parent.TransformPoint(button.Rest) : button.Rest;
                var local = Quaternion.Inverse(t.rotation) * (at - rest);
                local = new Vector3(local.x / t.lossyScale.x, local.y / t.lossyScale.y, local.z / t.lossyScale.z) - button.Center;
                if (!button.OutKnown)
                {
                    if (Vector3.Dot(local, button.Out) < 0f) button.Out = -button.Out;
                    button.OutKnown = true;
                }
                var scale = Mathf.Abs(Vector3.Dot(t.lossyScale, Abs(button.Out)));
                var depth = (button.Half - Vector3.Dot(local, button.Out)) * scale;
                var across = local - button.Out * Vector3.Dot(local, button.Out);
                var side = Vector3.Scale(button.Extents, Vector3.one - Abs(button.Out)) + Vector3.one * (Beside * VrCamera.WorldScale / Mathf.Max(scale, 1e-4f));
                var onFace = Mathf.Abs(across.x) <= side.x && Mathf.Abs(across.y) <= side.y && Mathf.Abs(across.z) <= side.z;
                // Coming up to the button, its control is offered to the game already: the game takes a press only for what
                // it was offered before (with the other hand off the air conditioner, the press did nothing, Raik).
                if (onFace && depth > -Approach * VrCamera.WorldScale && button.Control != null && button.Control.isActiveAndEnabled)
                {
                    offered = button.Control;
                    offeredUntil = Mathf.Max(offeredUntil, Time.time + 0.3f);
                }
                if (!onFace || depth < -Release * VrCamera.WorldScale)
                {
                    tip.In.Remove(button);
                    continue;
                }
                if (depth <= 0f) continue;
                tip.Pushing = button;
                pushed[button] = Mathf.Max(pushed.TryGetValue(button, out var other) ? other : 0f, depth);
                if (depth < PressDepth * VrCamera.WorldScale || tip.In.Contains(button)) continue;
                tip.In.Add(button);
                Press(tip, button, depth);
            }
        }

        private static void Press(Tip tip, Button button, float depth)
        {
            tip.Input.Buzz(ClickAmplitude, ClickLength);
            var control = button.Control;
            var ready = control != null && control.isActiveAndEnabled && VrLever.Ready(control, out _);
            if (ready)
            {
                offered = control;
                offeredUntil = Time.time + 0.3f;
                pressedAt = Time.time;
                VrToolInput.PressInteract();
            }
            if (logs++ < 60)
                Plugin.Log.LogInfo($"Buttons: {tip.Input.Name} hand pushes '{button.Filter.name}' {depth * 1000f:0} mm in, "
                    + (control == null ? "no control, only a click" : ready ? $"uses '{control.name}'" : $"'{control.name}' is busy"));
        }

        // The buttons on the parts around the tip.
        private static void Look(Tip tip, Vector3 at)
        {
            tip.Near.Clear();
            var count = Physics.OverlapSphereNonAlloc(at, LookAround * VrCamera.WorldScale, Found, ~0, QueryTriggerInteraction.Collide);
            for (var i = 0; i < count; i++)
            {
                var part = Found[i].GetComponentInParent<StructurePart>();
                if (part == null) continue;
                foreach (var button in Buttons(part.transform))
                    if (!tip.Near.Contains(button)) tip.Near.Add(button);
            }
        }

        private static List<Button> Buttons(Transform part)
        {
            if (ByPart.TryGetValue(part, out var list)) return list;
            list = new List<Button>();
            ByPart[part] = list;
            foreach (var filter in part.GetComponentsInChildren<MeshFilter>(true))
            {
                var mesh = filter.sharedMesh;
                // The air conditioner's black button is a part of its own, 'SM_AtmosphereRegulator_Mini_1Bx1xA_Button'
                // ('Unlock_01' on it is only its light, a flat quad).
                if (mesh == null || !(mesh.name.Contains("_Button") || filter.name.Contains("_Button"))) continue;
                var bounds = mesh.bounds;
                var size = Vector3.Scale(bounds.size, Abs(filter.transform.lossyScale));
                var center = filter.transform.TransformPoint(bounds.center);
                if (size.x > Largest || size.y > Largest || size.z > Largest)
                {
                    if (logs++ < 60) Plugin.Log.LogInfo($"Buttons: '{filter.name}' is too large for a button ({size.x * 100f:0} x {size.y * 100f:0} x {size.z * 100f:0} cm)");
                    continue;
                }
                var control = ControlNear(part, center, Device(mesh.name), out var controlPart);
                // It is pushed along its thinnest side, outwards away from the middle of the part its control is on.
                var local = bounds.size;
                var thin = local.x <= local.y && local.x <= local.z ? Vector3.right : local.y <= local.z ? Vector3.up : Vector3.forward;
                var body = controlPart != null ? controlPart.GetComponent<Renderer>() : null;
                if (body == filter.GetComponent<Renderer>()) body = null;
                if (body != null && Vector3.Dot(filter.transform.TransformDirection(thin), center - body.bounds.center) < 0f) thin = -thin;
                var button = new Button
                {
                    Filter = filter,
                    Control = control,
                    Rest = filter.transform.localPosition,
                    Out = thin,
                    OutKnown = body != null,
                    Half = Mathf.Abs(Vector3.Dot(bounds.extents, thin)),
                    Extents = bounds.extents,
                    Center = bounds.center,
                };
                // Its light sinks with it.
                var lights = controlPart != null ? controlPart : part;
                foreach (var light in lights.GetComponentsInChildren<MeshFilter>(true))
                    if (light.name.StartsWith("Unlock_") && (light.transform.position - center).magnitude < Largest)
                    {
                        button.With.Add(light.transform);
                        button.WithRest.Add(light.transform.localPosition);
                    }
                list.Add(button);
                if (logs++ < 60)
                    Plugin.Log.LogInfo($"Buttons: '{filter.name}' ({size.x * 100f:0.0} x {size.y * 100f:0.0} x {size.z * 100f:0.0} cm) on '{part.name}', "
                        + (control != null ? $"uses '{control.name}' on '{(controlPart != null ? controlPart.name : "?")}'" : "no control")
                        + $", {button.With.Count} light(s) with it, {(button.OutKnown ? "faces away from its part" : "faces where the hand comes from")}");
            }
            return list;
        }

        // The control a button uses: an active one on its own part, else the nearest on the parts around (the air
        // conditioner's button is a part next to its box, which has the control); never a lever turned by hand. Around, not
        // a control of another device: a broken air conditioner has none, and its button used the wall terminal next to it
        // ('SM_Computer_WallTerminal_B0xBxA'), whose use moved the player in front of it (Raik, 2026-11-08). Told apart only
        // where both names follow the 'SM_<Device>_' naming; any other control around is taken as before.
        private static InteractableObject ControlNear(Transform part, Vector3 center, string device, out Transform controlPart)
        {
            InteractableObject best = null;
            var bestDistance = float.MaxValue;
            var around = false;
            void Consider(InteractableObject each)
            {
                if (each == null || !each.isActiveAndEnabled || VrLever.Turnable(each)) return;
                if (around)
                {
                    var eachPart = each.GetComponentInParent<StructurePart>();
                    if (eachPart != null && OtherDevice(device, eachPart.name))
                    {
                        if (logs++ < 60) Plugin.Log.LogInfo($"Buttons: '{each.name}' on '{eachPart.name}' belongs to another device than '{device}', left out");
                        return;
                    }
                }
                var distance = (each.transform.position - center).sqrMagnitude;
                // The powered one before 'Unpowered Interaction Volume' at the same place.
                if (each.name.Contains("Unpowered")) distance += 1f;
                if (distance >= bestDistance) return;
                best = each;
                bestDistance = distance;
            }
            foreach (var each in part.GetComponentsInChildren<InteractableObject>(true)) Consider(each);
            if (best == null)
            {
                around = true;
                var count = Physics.OverlapSphereNonAlloc(center, ControlAround * VrCamera.WorldScale, Found, ~0, QueryTriggerInteraction.Collide);
                for (var i = 0; i < count; i++) Consider(Found[i].GetComponentInParent<InteractableObject>());
            }
            var owner = best != null ? best.GetComponentInParent<StructurePart>() : null;
            controlPart = owner != null ? owner.transform : null;
            return best;
        }

        private const float ControlAround = 0.6f;

        // Whether a part's name names another device than device, both in the 'SM_<Device>_' naming.
        public static bool OtherDevice(string device, string part) =>
            device.StartsWith("SM_") && part.StartsWith("SM_") && Device(part) != device;

        // The device a button's mesh belongs to: its first two words, 'SM_AtmosphereRegulator' for the air conditioner's
        // 'SM_AtmosphereRegulator_Mini_1Bx1xA_Button' (its box is 'SM_AtmosphereRegulator_Mini_1Bx1xA1_Box').
        public static string Device(string mesh)
        {
            var first = mesh.IndexOf('_');
            var second = first >= 0 ? mesh.IndexOf('_', first + 1) : -1;
            return second > 0 ? mesh.Substring(0, second) : mesh;
        }

        private static Vector3 Abs(Vector3 v) => new Vector3(Mathf.Abs(v.x), Mathf.Abs(v.y), Mathf.Abs(v.z));
    }
}
