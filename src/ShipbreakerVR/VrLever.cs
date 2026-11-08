using System.Collections.Generic;
using System.Reflection;
using BBI.Unity.Game;
using HarmonyLib;
using UnityEngine;

namespace ShipbreakerVR
{
    // Levers turned by hand (Raik, 0.29): a hand holding a lever control (the airlock's yellow lever) turns its handle with
    // the turn of the controller about the handle's axis. Turned all the way (EndAngle) it uses the control, as A did, once
    // the control may be used again (its asset's m_CompletionCooldown since mLastCompletionTime). Then it stays at the end
    // while the control is busy, also with the hand still on it, and turns back on its own once the control is done,
    // taking a hand still on it along (the body stays where it is, VrClimb.Pin); the controller has to come back to that
    // hand before it turns the lever again. No turning while it is busy (Raik). Turning a lever that will trigger hums
    // lightly, snapping in at the end gives one short hard knock. Let go before the end, it goes back at once.
    //
    // Measured on the airlock (VrInteractSurvey, 2026-11-07): the handle 'GRP_AirlockConsole_Handle' turns about its own
    // -X axis by 60° each time it is used, always from the same rest pose, played by the console's Animator. The hand's
    // target (m_HandTarget) sits on the handle, so the handle is found as the target's nearest parent named "...Handle".
    // The handle's pose is set after the Animator ran (LateUpdate), so it wins over the game's own turn.
    internal static class VrLever
    {
        // Further than the game's own 60°: more to feel in the turn (Raik).
        private const float EndAngle = 90f;
        private static readonly Vector3 LocalAxis = new Vector3(-1f, 0f, 0f);
        // Turned past this, the hand takes over the handle from the Animator.
        private const float TakeOver = 3f;
        // Turned back below this, the next full turn may trigger again.
        private const float ArmBelow = 15f;
        // Within this of the end it snaps in.
        private const float SnapWithin = 2f;
        // The hum: from this turn speed on, LightLeast to LightMost at HumFull degrees per second, a pulse every HumEvery.
        private const float HumFrom = 15f;
        private const float HumFull = 180f;
        private const float LightLeast = 0.08f;
        private const float LightMost = 0.22f;
        private const float HumEvery = 0.05f;
        private const float HumLength = 0.07f;
        private const float SnapAmplitude = 1f;
        private const float SnapLength = 0.035f;
        // Turned out of its rest, the handle holds this long, in seconds, before it follows the hand.
        private const float Unlatch = 0.05f;
        // Let go before the end, the handle goes back over this long.
        private const float BackTime = 0.6f;
        // Done with its work, a used lever goes back over this long, slowly as the game's own (Raik).
        private const float BusyBackTime = 1.2f;
        // A use the game did not take (no new mLastCompletionTime) lets the lever go back after this long.
        private const float NotTaken = 1f;
        // Back with the hand still on it while the controller stayed turned: the controller has to come back within this
        // of the lever before the lever turns with it again (Raik); a light knock says it caught it.
        private const float CatchWithin = 5f;
        private const float CatchAmplitude = 0.3f;
        private const float CatchLength = 0.04f;
        // The hand takes hold of the yellow grip only this near its middle line, in m at world scale 1 (the bar is about
        // 3 cm thick), and then sits right on it; further off, the lever is a surface as any other and does not turn (Raik).
        private const float GripReach = 0.085f;
        // The ends of the grip kept free of the hand's middle, as a share of its length.
        private const float GripEnds = 0.15f;
        // The hand on the grip sits this far further out from the console and up from the grip, in m at world scale 1,
        // so the fingers close around it instead of sinking into the console (Raik, from a screenshot).
        private const float GripOut = 0.01f;
        private const float GripUp = 0.01f;

        private class Held
        {
            public readonly VrInput.Hand Input;
            public InteractableObject Control;
            public Transform Handle;
            public Quaternion Rest;
            public Quaternion GripAtHold;
            public bool Engaged;
            public bool Armed;
            // Back from its use with the hand on it: waits for the controller to come back to it (Restart).
            public bool Catching;
            public float Angle;
            // Where the turn started: a lever taken hold of off its rest pose goes on from where it is.
            public float StartAngle;
            public float HumAt;
            public float UnlatchedAt = -1f;
            // Going back after letting go before the end: from that pose, since then.
            public Transform Returning;
            public Quaternion ReturnFrom;
            public Quaternion ReturnRest;
            public float ReturnAt;

            public Held(VrInput.Hand input) => Input = input;
        }

        private static readonly Held[] Hands = { new Held(VrInput.Left), new Held(VrInput.Right) };
        private static readonly FieldInfo HandTarget = AccessTools.Field(typeof(InteractableObject), "m_HandTarget");
        private static readonly FieldInfo Asset = AccessTools.Field(typeof(InteractableObject), "m_Asset");
        private static readonly FieldInfo LastCompletion = AccessTools.Field(typeof(InteractableObject), "mLastCompletionTime");
        private static int logs;
        private static int gripLogs;
        // Each control's handle (null for any other control), found once.
        private static readonly Dictionary<InteractableObject, Transform> Handles = new Dictionary<InteractableObject, Transform>();

        // The lever used last, while its control is busy and while it goes back after: at its end pose since UsedAt, back
        // to Rest from BackAt on (negative until then).
        private static Transform used;
        private static InteractableObject usedControl;
        private static Quaternion usedRest;
        private static Quaternion usedEnd;
        private static float usedAt;
        private static float backAt = -1f;

        // A lever this mod turns by hand: its prompt is hidden (VrLeverPrompt) and A does not use it.
        public static bool Turnable(InteractableObject control) => control != null && FindHandle(control) != null;

        // Whether at, where a hand takes hold, is on the lever's grip: then where on its middle line the hand sits, and
        // the line's direction.
        public static bool OnGrip(InteractableObject control, Vector3 at, out Vector3 onGrip, out Vector3 axis)
        {
            onGrip = at;
            axis = Vector3.zero;
            var handle = FindHandle(control);
            var bar = Bar(handle);
            if (bar == null) return false;
            // Out: along the lever's turning axis, which stands out of the console.
            var outward = handle.rotation * LocalAxis;
            if (GripLine(bar, outward, at, GripReach, "Lever", out onGrip, out axis)) return true;
            // Also anywhere over the turning part, as in a cylinder about the turning axis as wide as the grip turns and
            // OverHeight out from the console (Raik: no need to reach right in).
            if (Vector3.Dot(outward, VrCamera.Instance.transform.position - handle.position) < 0f) outward = -outward;
            var from = handle.position;
            var height = Vector3.Dot(at - from, outward);
            var across = (Vector3.ProjectOnPlane(at - from, outward)).magnitude;
            GripSegment(control, out var end, out var other, out _);
            var radius = Mathf.Max(Vector3.ProjectOnPlane(end - from, outward).magnitude, Vector3.ProjectOnPlane(other - from, outward).magnitude) + OverWide * VrCamera.WorldScale;
            var over = height >= -OverBelow * VrCamera.WorldScale && height <= OverHeight * VrCamera.WorldScale && across <= radius;
            if (over && gripLogs++ < 60) Plugin.Log.LogInfo($"Lever: hand over the lever ({height * 100f:0} cm out, {across * 100f:0} cm from its axis), takes the grip");
            return over;
        }

        // Whether at is within reach of the middle line of a grip (a bar-like mesh, along its longest side, its ends kept
        // free): then where the hand sits on it, GripOut further out (outward, made across the grip, towards the head) and
        // GripUp up (across both, towards the top of the view), so the fingers close around it; and the line's direction.
        internal static bool GripLine(MeshFilter bar, Vector3 outward, Vector3 at, float reach, string what, out Vector3 onGrip, out Vector3 axis)
        {
            var bounds = bar.sharedMesh.bounds;
            var size = bounds.size;
            var along = size.x >= size.y && size.x >= size.z ? Vector3.right : size.y >= size.z ? Vector3.up : Vector3.forward;
            var half = Vector3.Dot(bounds.extents, along) * (1f - 2f * GripEnds);
            var from = bar.transform.TransformPoint(bounds.center - along * half);
            var to = bar.transform.TransformPoint(bounds.center + along * half);
            var line = to - from;
            var t = Mathf.Clamp01(Vector3.Dot(at - from, line) / Mathf.Max(line.sqrMagnitude, 1e-8f));
            onGrip = from + line * t;
            axis = line.normalized;
            var off = (at - onGrip).magnitude;
            var on = off <= reach * VrCamera.WorldScale;
            var head = VrCamera.Instance.transform;
            outward = Vector3.ProjectOnPlane(outward, axis);
            if (Vector3.Dot(outward, head.position - onGrip) < 0f) outward = -outward;
            var up = Vector3.Cross(axis, outward).normalized;
            if (Vector3.Dot(up, head.up) < 0f) up = -up;
            onGrip += (outward.normalized * GripOut + up * GripUp) * VrCamera.WorldScale;
            if (gripLogs++ < 60)
                Plugin.Log.LogInfo($"{what}: hand takes hold {off * 100f:0.0} cm from the middle of '{bar.name}' ({line.magnitude * 100f:0} cm of grip), "
                    + (on ? "on the grip" : $"off it (within {reach * 100f:0.0} cm), as a surface"));
            return on;
        }

        // The grip as a line, and how near a hand has to be to take hold of it (the grip view, VrEdgeView).
        public static bool GripSegment(InteractableObject control, out Vector3 from, out Vector3 to, out float reach)
        {
            from = to = Vector3.zero;
            reach = GripReach * VrCamera.WorldScale;
            if (control == null || !Turnable(control)) return false;
            var bar = Bar(FindHandle(control));
            if (bar == null) return false;
            var bounds = bar.sharedMesh.bounds;
            var size = bounds.size;
            var along = size.x >= size.y && size.x >= size.z ? Vector3.right : size.y >= size.z ? Vector3.up : Vector3.forward;
            var half = Vector3.Dot(bounds.extents, along) * (1f - 2f * GripEnds);
            from = bar.transform.TransformPoint(bounds.center - along * half);
            to = bar.transform.TransformPoint(bounds.center + along * half);
            return true;
        }

        // Whether at lies over the lever's turning part (its handle and grip, OverMargin around them): nothing there is held
        // but the grip itself (Raik: no flat hand on the turning part, around it on the console it is fine).
        public static bool OverHandle(InteractableObject control, Vector3 at)
        {
            var handle = FindHandle(control);
            if (handle == null) return false;
            var found = false;
            var bounds = new Bounds();
            foreach (var renderer in handle.GetComponentsInChildren<Renderer>())
            {
                if (!found) bounds = renderer.bounds;
                else bounds.Encapsulate(renderer.bounds);
                found = true;
            }
            if (!found) return false;
            bounds.Expand(2f * OverMargin * VrCamera.WorldScale);
            return bounds.Contains(at);
        }

        private const float OverMargin = 0.04f;
        // The cylinder over the lever that takes its grip: this far out from the handle, below it, and wider than the grip
        // reaches round.
        private const float OverHeight = 0.15f;
        private const float OverBelow = 0.02f;
        private const float OverWide = 0.03f;

        // The yellow grip of a handle: its part named like a bar.
        private static MeshFilter Bar(Transform handle)
        {
            if (handle == null) return null;
            foreach (var filter in handle.GetComponentsInChildren<MeshFilter>())
                if (filter.name.Contains("HandleBar") && filter.sharedMesh != null) return filter;
            return null;
        }

        private static Transform FindHandle(InteractableObject control)
        {
            if (!Handles.TryGetValue(control, out var handle))
            {
                handle = Handle(control);
                Handles[control] = handle;
            }
            return handle;
        }

        // Whether a hand holds a lever it turns: then A does not use it (Raik: for now only the turn of the hand does).
        public static bool Holds => Hands[0].Handle != null || Hands[1].Handle != null;

        // From VrFrame, in LateUpdate after the Animators and before the hands are placed on their holds.
        public static void LateUpdate()
        {
            foreach (var held in Hands) Update(held);
            Used();
        }

        private static void Update(Held held)
        {
            var control = VrXr.IsRunning ? VrClimb.HeldControl(held.Input) : null;
            if (control != held.Control)
            {
                LetGo(held);
                if (control != null) TakeHold(held, control);
            }
            Return(held);
            // A used lever is the game's until it is back (Used).
            if (held.Handle == null || held.Handle == used) return;
            if (!held.Input.TryGetGripPose(out _, out var grip)) return;
            var world = VrCamera.Instance.transform.parent.rotation * grip;
            var axis = held.Handle.parent.rotation * held.Rest * LocalAxis;
            var twist = LeverMath.Twist(world * Quaternion.Inverse(held.GripAtHold), axis);
            if (held.Catching)
            {
                if (held.StartAngle + twist > CatchWithin) return;
                held.Catching = false;
                held.Input.Buzz(CatchAmplitude, CatchLength);
                if (logs++ < 200) Plugin.Log.LogInfo($"Lever: {held.Input.Name} controller is back on '{held.Handle.name}', turns it again");
            }
            if (!held.Engaged && Mathf.Abs(twist) < TakeOver) return;
            if (!held.Engaged)
            {
                // Coming out of its rest it unlatches, as out of a bayonet lock: a short hard knock, and it holds for
                // Unlatch before it follows the hand (Raik).
                held.Engaged = true;
                held.UnlatchedAt = Time.time;
                held.Input.Buzz(SnapAmplitude, SnapLength);
            }
            if (Time.time - held.UnlatchedAt < Unlatch) return;
            var angle = Mathf.Clamp(held.StartAngle + twist, 0f, EndAngle);
            var speed = Mathf.Abs(angle - held.Angle) / Mathf.Max(Time.deltaTime, 1e-4f);
            held.Angle = angle;
            held.Handle.localRotation = held.Rest * Quaternion.AngleAxis(angle, LocalAxis);

            if (angle < ArmBelow) held.Armed = true;
            if (!held.Armed || !Ready(held.Control, out _)) return;
            if (angle >= EndAngle - SnapWithin)
            {
                held.Armed = false;
                held.Input.Buzz(SnapAmplitude, SnapLength);
                VrToolInput.PressInteract();
                Use(held);
                return;
            }
            if (speed < HumFrom || Time.unscaledTime < held.HumAt) return;
            held.HumAt = Time.unscaledTime + HumEvery;
            held.Input.Buzz(Mathf.Lerp(LightLeast, LightMost, Mathf.InverseLerp(HumFrom, HumFull, speed)), HumLength);
        }

        // Turned all the way: it stays at the end while its control works, with the hands on it, whose bodies keep where
        // they are while it goes back later.
        private static void Use(Held held)
        {
            used = held.Handle;
            usedControl = held.Control;
            usedRest = held.Rest;
            usedEnd = held.Rest * Quaternion.AngleAxis(EndAngle, LocalAxis);
            usedAt = Time.time;
            backAt = -1f;
            used.localRotation = usedEnd;
            foreach (var each in Hands)
                if (each.Handle == used) VrClimb.Pin(each.Input, used.parent);
            if (logs++ < 200) Plugin.Log.LogInfo($"Lever: {held.Input.Name} hand turned '{used.name}' all the way, uses '{usedControl.name}'");
        }

        // The used lever at its end until its control is done (or the game did not take the use), then back to its rest
        // pose, a hand still on it along; that hand turns it from there again.
        private static void Used()
        {
            if (used == null) return;
            if (backAt < 0f)
            {
                var last = LastCompletion?.GetValue(usedControl) is float l ? l : 0f;
                var taken = last >= usedAt - 0.1f;
                var done = taken ? Time.time >= last + Cooldown(usedControl) : Time.time >= usedAt + NotTaken;
                if (!done)
                {
                    used.localRotation = usedEnd;
                    return;
                }
                backAt = Time.time;
                if (logs++ < 200) Plugin.Log.LogInfo($"Lever: '{used.name}' goes back, {(taken ? "its control is done" : "the game did not take the use")}");
            }
            var t = (Time.time - backAt) / BusyBackTime;
            if (t < 1f)
            {
                used.localRotation = Quaternion.Slerp(usedEnd, usedRest, t * t * (3f - 2f * t));
                return;
            }
            used.localRotation = usedRest;
            foreach (var each in Hands)
                if (each.Handle == used) Restart(each);
            if (logs++ < 200) Plugin.Log.LogInfo($"Lever: '{used.name}' is back at its rest pose");
            used = null;
            usedControl = null;
        }

        // A hand still on a lever that went back: the hand went with it, the controller may not have. The lever turns with
        // the controller again once the controller is back where it held the lever at rest (Catching), the same reference
        // as before (GripAtHold, StartAngle), so the lever never jumps to the controller.
        private static void Restart(Held held)
        {
            VrClimb.Pin(held.Input, null);
            held.Rest = usedRest;
            held.Angle = 0f;
            held.Engaged = true;
            held.Armed = true;
            held.Catching = true;
        }

        private static void TakeHold(Held held, InteractableObject control)
        {
            held.Control = control;
            var handle = FindHandle(control);
            if (handle == null || !held.Input.TryGetGripPose(out _, out var grip)) return;
            // A lever off its rest pose (used, or going back) is taken where it is, from the rest pose it goes back to.
            var rest = handle.localRotation;
            if (handle == used) rest = usedRest;
            foreach (var other in Hands)
                if (other.Returning == handle)
                {
                    rest = other.ReturnRest;
                    other.Returning = null;
                }
            var start = Mathf.Clamp(LeverMath.Twist(Quaternion.Inverse(rest) * handle.localRotation, LocalAxis), 0f, EndAngle);
            held.Handle = handle;
            held.Rest = rest;
            held.GripAtHold = VrCamera.Instance.transform.parent.rotation * grip;
            held.StartAngle = start;
            held.Catching = false;
            held.Engaged = start > 0f;
            held.Armed = start < ArmBelow;
            held.Angle = start;
            // The hand took hold on the grip where it is now (VrClimb, OnGrip) and turns with it from there.
            VrClimb.Reanchor(held.Input, handle);
            // The hand goes round with the lever, the body does not: turned from afar, the hold went round the lever and
            // the body after it, and flew on after letting go (Raik, 0.6 m/s). It turns with the lever, not the controller.
            VrClimb.SetFree(held.Input, true, false);
            if (handle == used) VrClimb.Pin(held.Input, handle.parent);
            if (logs++ < 200)
            {
                Ready(control, out var wait);
                Plugin.Log.LogInfo($"Lever: {held.Input.Name} hand holds '{handle.name}' of '{control.name}' ({TypeName(control)}), "
                    + (wait > 0f ? $"usable again in {wait:0.0} s" : "usable") + (start > 0f ? $", at {start:0}°" : "")
                    + (handle == used ? ", busy" : ""));
            }
        }

        private static void LetGo(Held held)
        {
            if (held.Handle != null)
            {
                // A used lever stays at its end and goes back on its own (Used).
                if (held.Handle != used && held.Engaged)
                {
                    held.Returning = held.Handle;
                    held.ReturnFrom = held.Handle.localRotation;
                    held.ReturnRest = held.Rest;
                    held.ReturnAt = Time.time;
                }
                if (logs++ < 200)
                    Plugin.Log.LogInfo($"Lever: {held.Input.Name} hand lets go of '{held.Handle.name}' at {held.Angle:0}°, "
                        + (held.Handle == used ? "stays there while it is busy" : held.Engaged ? "goes back" : "not turned"));
            }
            held.Control = null;
            held.Handle = null;
            held.Engaged = false;
        }

        // Let go before the end: back to the rest pose it had when taken hold of; from then on the Animator shows it.
        private static void Return(Held held)
        {
            var handle = held.Returning;
            if (handle == null) return;
            var t = (Time.time - held.ReturnAt) / BackTime;
            if (t >= 1f)
            {
                handle.localRotation = held.ReturnRest;
                held.Returning = null;
                return;
            }
            handle.localRotation = Quaternion.Slerp(held.ReturnFrom, held.ReturnRest, t * t * (3f - 2f * t));
        }

        // Whether using it now would do something: its cooldown since it was last used is over; how long until then.
        internal static bool Ready(InteractableObject control, out float wait)
        {
            wait = 0f;
            var last = LastCompletion?.GetValue(control) is float l ? l : 0f;
            if (last <= 0f) return true;
            wait = last + Cooldown(control) - Time.time;
            return wait <= 0f;
        }

        // The control's turning part: the nearest parent of its hand target named like a handle, only for plain levers
        // (the nacelle lever is held for seconds, not turned).
        private static Transform Handle(InteractableObject control)
        {
            if (TypeName(control) != "Lever_InteractionTypeAsset") return null;
            var target = HandTarget?.GetValue(control) as Transform;
            for (var t = target; t != null && t != control.transform.root; t = t.parent)
                if (t.name.EndsWith("_Handle")) return t;
            if (logs++ < 200) Plugin.Log.LogInfo($"Lever: '{control.name}' is a lever without a handle found from its hand target '{(target != null ? target.name : "none")}'");
            return null;
        }

        private static object Data(InteractableObject control)
        {
            var asset = Asset?.GetValue(control);
            return asset == null ? null : AccessTools.Field(asset.GetType(), "m_Data")?.GetValue(asset);
        }

        internal static string TypeName(InteractableObject control)
        {
            var data = Data(control);
            var type = data == null ? null : AccessTools.Field(data.GetType(), "m_InteractionType")?.GetValue(data) as Object;
            return type != null ? type.name : "";
        }

        private static float Cooldown(InteractableObject control)
        {
            var data = Data(control);
            return data != null && AccessTools.Field(data.GetType(), "m_CompletionCooldown")?.GetValue(data) is float f ? f : 0f;
        }
    }

    // Outside VrLever, so the tests run it without the game.
    internal static class LeverMath
    {
        // How far a turn goes about an axis (unit length), in degrees from -180 to 180: its twist about it, without the
        // swing of the axis itself.
        public static float Twist(Quaternion turn, Vector3 axis)
        {
            var along = turn.x * axis.x + turn.y * axis.y + turn.z * axis.z;
            var angle = 2f * Mathf.Atan2(along, turn.w) * Mathf.Rad2Deg;
            if (angle > 180f) angle -= 360f;
            if (angle < -180f) angle += 360f;
            return angle;
        }
    }
}
