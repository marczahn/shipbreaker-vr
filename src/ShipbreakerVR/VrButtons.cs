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
        // How far the button sinks in at most.
        private const float Travel = 0.008f;
        // The tip counts on the face within this beyond its edges.
        private const float Beside = 0.01f;
        // Parts near the hand looked through this often, this far around the tip.
        private const float LookEvery = 0.15f;
        private const float LookAround = 0.25f;
        // A button is no larger than this.
        private const float Largest = 0.2f;
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
            public float Half;
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
                if (button.Sunk > 0f) t.position -= t.TransformDirection(button.Out).normalized * button.Sunk;
                else Done.Add(button);
            }
            foreach (var button in Done) Sinking.Remove(button);
        }

        private static void Touch(Tip tip, Dictionary<Button, float> pushed)
        {
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
                var scale = Mathf.Abs(Vector3.Dot(t.lossyScale, Abs(button.Out)));
                var depth = (button.Half - Vector3.Dot(local, button.Out)) * scale;
                var across = local - button.Out * Vector3.Dot(local, button.Out);
                var side = Vector3.Scale(button.Extents, Vector3.one - Abs(button.Out)) + Vector3.one * (Beside * VrCamera.WorldScale / Mathf.Max(scale, 1e-4f));
                var onFace = Mathf.Abs(across.x) <= side.x && Mathf.Abs(across.y) <= side.y && Mathf.Abs(across.z) <= side.z;
                if (!onFace || depth < -Release * VrCamera.WorldScale)
                {
                    tip.In.Remove(button);
                    continue;
                }
                if (depth <= 0f) continue;
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
            InteractableObject control = null;
            // The powered one first: the part also has an 'Unpowered Interaction Volume'.
            foreach (var each in part.GetComponentsInChildren<InteractableObject>(true))
                if (each.isActiveAndEnabled && (control == null || control.name.Contains("Unpowered"))) control = each;
            foreach (var filter in part.GetComponentsInChildren<MeshFilter>(true))
            {
                var mesh = filter.sharedMesh;
                if (logs < 60 && part.name.Contains("AtmosphereRegulator"))
                    Plugin.Log.LogInfo($"Buttons: '{part.name}' has '{filter.name}' ({(mesh != null ? mesh.name : "no mesh")})");
                // The air conditioner's button is its part 'Unlock_01'.
                if (mesh == null || !(mesh.name.Contains("_Button") || filter.name.Contains("_Button") || filter.name.StartsWith("Unlock_"))) continue;
                var bounds = mesh.bounds;
                var size = Vector3.Scale(bounds.size, Abs(filter.transform.lossyScale));
                if (size.x > Largest || size.y > Largest || size.z > Largest) continue;
                // It is pushed along its thinnest side.
                var local = bounds.size;
                var thin = local.x <= local.y && local.x <= local.z ? Vector3.right : local.y <= local.z ? Vector3.up : Vector3.forward;
                // Outwards: away from the middle of the part it sits on.
                var body = part.GetComponent<Renderer>();
                var middle = body != null ? body.bounds.center : part.position;
                if (Vector3.Dot(filter.transform.TransformDirection(thin), filter.transform.TransformPoint(bounds.center) - middle) < 0f) thin = -thin;
                list.Add(new Button
                {
                    Filter = filter,
                    Control = control,
                    Rest = filter.transform.localPosition,
                    Out = thin,
                    Half = Vector3.Dot(bounds.extents, thin),
                    Extents = bounds.extents,
                    Center = bounds.center,
                });
                if (logs++ < 60)
                    Plugin.Log.LogInfo($"Buttons: '{filter.name}' ({mesh.name}, {size.x * 100f:0.0} x {size.y * 100f:0.0} x {size.z * 100f:0.0} cm) on '{part.name}', "
                        + (control != null ? $"uses '{control.name}'" : "no control"));
            }
            return list;
        }

        private static Vector3 Abs(Vector3 v) => new Vector3(Mathf.Abs(v.x), Mathf.Abs(v.y), Mathf.Abs(v.z));
    }
}
