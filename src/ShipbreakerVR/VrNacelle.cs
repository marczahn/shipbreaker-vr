using System.Collections.Generic;
using BBI.Unity.Game;
using HarmonyLib;
using UnityEngine;

namespace ShipbreakerVR
{
    // The nacelle switch that cuts the fuel lines, worked by hand (Raik, 0.29), in the three steps of the game's own
    // animation: fold the handle down (the hand goes round its hinge with it, 90°), pull it down (the switch slides 16 cm,
    // no faster than PullMost, and the further the hand runs ahead of it the harder it shakes), fold it over (round on
    // another 90°). Then it uses the switch (InteractableObject.Complete, as holding A for 4 s did) and stays down.
    // Each step follows the controller directly, kept as near it as when taking hold: after a step the switch rests for
    // StepPause, then catches up with the controller (Raik: coming back to the handle was too fiddly, and the switch
    // lagged too far behind the hand). It moves only while the other hand holds on to
    // something fixed (Raik: it goes heavily); without, it stays, and goes on from the controller once braced again.
    // Let go before the end, it goes back to where it started. The hand holds free (VrClimb.SetFree): it pulls nothing,
    // the body stays where it is, it is not torn off however far the controller goes, and it turns about the grip.
    //
    // Measured (VrInteractSurvey, clip 'Switch_Flush_Turn_Off', 4.33 s, 2026-11-08): the handle
    // 'SM_Switch_Flush_AxCxA_Handle' turns about its own +X by 90° (0 to 1.44 s); the switch
    // 'SM_Switch_Flush_AxCxA_Switch' slides 16 cm down its parent's -Y (1.44 to 3.25 s); the handle turns on to 180°
    // (3.25 to 4.33 s). The game's hand goes to 'HandGrab' on the handle, 13.9 cm out along its +Y: the grip, which
    // runs along the hinge. The handle is L-shaped, so the middle of its mesh lies 10 to 20 cm off the grip (2026-11-08
    // log). Poses are set after the Animator ran (LateUpdate).
    internal static class VrNacelle
    {
        private const string TypeName = "Lever_Nacelle_InteractionTypeAsset";
        private static readonly Vector3 Axis = Vector3.right;
        private const float Fold = 90f;
        private const float Slide = 0.16039f;
        private const float SnapWithin = 2f;
        private const float SlideSnap = 0.004f;
        // The pull follows the hand at most this fast, in m/s at world scale 1 (the game's pull takes 1.8 s; Raik:
        // 0.2 m/s, once the switch kept near the controller).
        private const float PullMost = 0.2f;
        // After each step the switch rests this long before the next follows the hand.
        private const float StepPause = 0.15f;
        // The handle folds at most this fast, in degrees per second, catching up after a rest.
        private const float FoldMost = 400f;
        // The grip: this long along the hinge through 'HandGrab', taken hold of within GripReach of its middle line.
        private const float GripLength = 0.08f;
        private const float GripReach = 0.1f;
        // The shaking: from LagFrom ahead, Least to Most at LagFull, a pulse every Every.
        private const float LagFrom = 0.005f;
        private const float LagFull = 0.06f;
        private const float Least = 0.3f;
        private const float Most = 0.7f;
        private const float Every = 0.05f;
        private const float PulseLength = 0.07f;
        private const float HumLeast = 0.06f;
        private const float HumMost = 0.18f;
        private const float StepAmplitude = 0.8f;
        private const float StepLength = 0.035f;
        private const float BackTime = 0.6f;

        private enum Step { FoldDown, Pull, FoldOver, Done }

        private class Pose
        {
            public Transform Handle;
            public Transform Switch;
            public Quaternion HandleRest;
            public Vector3 HandleRestPlace;
            public Vector3 SwitchRest;
            public float Angle;
            public float Slid;

            public void Show(float angle, float slid)
            {
                if (Handle == null || Switch == null) return;
                Switch.localPosition = SwitchRest;
                Handle.localPosition = HandleRestPlace;
                Handle.localRotation = HandleRest * Quaternion.AngleAxis(angle, Axis);
                if (slid <= 0f) return;
                var down = Switch.parent.TransformDirection(Vector3.down).normalized * slid;
                Switch.position += down;
                // A handle on the switch goes with it already.
                if (!Handle.IsChildOf(Switch)) Handle.position += down;
            }

            public void Show() => Show(Angle, Slid);
        }

        private class Held
        {
            public readonly VrInput.Hand Input;
            public InteractableObject Control;
            public Pose Pose;
            public Step Step;
            // Where the controller was against the handle when taking hold (Rebase, once per hold): every step follows the
            // controller from there, so the switch stays as near the hand as it was then (Raik: it should follow sooner).
            public bool Based;
            public float PauseUntil;
            public float AngleFrom;
            public float AngleRef;
            // How far ahead of the hand on the handle the controller was along the pull when taking hold.
            public float PullOffset;
            // Where the hand holds the handle, in the handle's own axes.
            public Vector3 Reach;
            public float PulseAt;
            public bool BraceLogged;

            public Held(VrInput.Hand input) => Input = input;
        }

        // A switch let go of before the end, going back.
        private class Loose
        {
            public Pose Pose;
            public float At;
        }

        private static readonly Held[] Hands = { new Held(VrInput.Left), new Held(VrInput.Right) };
        private static readonly List<Loose> Let = new List<Loose>();
        // Used switches stay down (Raik): their end pose over whatever the Animator shows, as long as they exist.
        private static readonly Dictionary<InteractableObject, Pose> Used = new Dictionary<InteractableObject, Pose>();
        private static readonly Dictionary<InteractableObject, bool> Known = new Dictionary<InteractableObject, bool>();
        private static readonly System.Reflection.MethodInfo CompleteMethod = AccessTools.Method(typeof(InteractableObject), "Complete");
        private static int logs;
        private static int gripLogs;

        // A nacelle switch: its prompt is hidden and A does not use it.
        public static bool Is(InteractableObject control)
        {
            if (control == null) return false;
            if (!Known.TryGetValue(control, out var known))
            {
                var type = VrLever.TypeName(control);
                var parts = Parts(control, out var handle, out var slider, out var grab);
                known = type == TypeName && parts;
                Known[control] = known;
                if (type.Contains("Nacelle") && logs++ < 80)
                    Plugin.Log.LogInfo($"Nacelle: '{control.name}' ({type}): handle '{(handle != null ? handle.name : "none")}', switch '{(slider != null ? slider.name : "none")}', "
                        + $"grip '{(grab != null ? grab.name : "none")}'{(known ? "" : ", not worked by hand")}");
            }
            return known;
        }

        public static bool Holds => Hands[0].Control != null || Hands[1].Control != null;

        // Whether at, where a hand takes hold, is on the switch's grip: then where the hand sits on it and its direction
        // (VrClimb holds it there; further off the hand holds the hull as anywhere).
        public static bool OnGrip(InteractableObject control, Vector3 at, out Vector3 onGrip, out Vector3 axis)
        {
            onGrip = at;
            axis = Vector3.zero;
            if (!Parts(control, out var handle, out _, out var grab)) return false;
            axis = handle.TransformDirection(Axis).normalized;
            var half = GripLength * 0.5f * VrCamera.WorldScale;
            var t = Mathf.Clamp(Vector3.Dot(at - grab.position, axis), -half, half);
            onGrip = grab.position + axis * t;
            var off = (at - onGrip).magnitude;
            var on = off <= GripReach * VrCamera.WorldScale;
            if (gripLogs++ < 60)
                Plugin.Log.LogInfo($"Nacelle: hand takes hold {off * 100f:0.0} cm from the grip at '{grab.name}', " + (on ? "on the grip" : $"off it (within {GripReach * 100f:0.0} cm), as a surface"));
            return on;
        }

        // The grip as a line, and how near a hand has to be to take hold of it (the grip view, VrEdgeView).
        public static bool GripSegment(InteractableObject control, out Vector3 from, out Vector3 to, out float reach)
        {
            from = to = Vector3.zero;
            reach = GripReach * VrCamera.WorldScale;
            if (!Is(control) || !Parts(control, out var handle, out _, out var grab)) return false;
            var half = handle.TransformDirection(Axis).normalized * (GripLength * 0.5f * VrCamera.WorldScale);
            from = grab.position - half;
            to = grab.position + half;
            return true;
        }

        // From VrFrame, in LateUpdate after the Animators and before the hands are placed on their holds.
        public static void LateUpdate()
        {
            foreach (var held in Hands) Update(held);
            for (var i = Let.Count - 1; i >= 0; i--)
                if (!Settle(Let[i])) Let.RemoveAt(i);
            List<InteractableObject> gone = null;
            foreach (var used in Used)
            {
                if (used.Key == null || used.Value.Handle == null)
                {
                    (gone ??= new List<InteractableObject>()).Add(used.Key);
                    continue;
                }
                used.Value.Show();
            }
            if (gone != null) foreach (var key in gone) Used.Remove(key);
        }

        private static void Update(Held held)
        {
            var control = VrXr.IsRunning ? VrClimb.HeldControl(held.Input) : null;
            if (control != null && (!Is(control) || Used.ContainsKey(control))) control = null;
            if (control != held.Control)
            {
                LetGo(held);
                if (control != null) TakeHold(held, control);
            }
            if (held.Control == null || held.Step == Step.Done) return;
            // The handle where it was left, over what the Animator set this frame.
            held.Pose.Show();
            if (!held.Input.TryGetGripPose(out var gripLocal, out var gripTurn)) return;
            if (!VrToolModels.TrackingToWorld(gripLocal, gripTurn, out var grip, out _)) return;
            // It goes heavily: only with the other hand braced on something fixed (Raik).
            if (!VrClimb.Braced(held.Input))
            {
                if (!held.BraceLogged && logs++ < 80) Plugin.Log.LogInfo($"Nacelle: {held.Input.Name} hand without the other braced, '{held.Control.name}' does not move");
                held.BraceLogged = true;
                return;
            }
            held.BraceLogged = false;
            if (Time.time < held.PauseUntil) return;
            var angleAt = AngleAt(held, grip);
            if (!held.Based) Rebase(held, grip, angleAt);
            var pose = held.Pose;
            switch (held.Step)
            {
                case Step.FoldDown:
                {
                    var angle = Mathf.MoveTowards(pose.Angle, Mathf.Clamp(held.AngleFrom + Mathf.DeltaAngle(held.AngleRef, angleAt), 0f, Fold), FoldMost * Time.deltaTime);
                    Hum(held, angle);
                    pose.Angle = angle;
                    if (angle >= Fold - SnapWithin)
                    {
                        pose.Angle = Fold;
                        Next(held, Step.Pull, "folded down");
                    }
                    break;
                }
                case Step.Pull:
                {
                    var down = pose.Switch.parent.TransformDirection(Vector3.down).normalized;
                    var travel = pose.Switch.parent.TransformVector(Vector3.up * Slide).magnitude;
                    // Where the controller is against where the hand is on the handle now, less how far apart they were when
                    // taking hold: the switch catches up with the controller, it does not keep the gap it had after a rest.
                    var reach = pose.Slid + Vector3.Dot(grip - pose.Handle.TransformPoint(held.Reach), down) - held.PullOffset;
                    var before = pose.Slid;
                    pose.Slid = Mathf.MoveTowards(pose.Slid, Mathf.Clamp(reach, 0f, travel), PullMost * VrCamera.WorldScale * Time.deltaTime);
                    Shake(held, reach - pose.Slid, pose.Slid != before);
                    if (pose.Slid >= travel - SlideSnap * VrCamera.WorldScale)
                    {
                        pose.Slid = travel;
                        Next(held, Step.FoldOver, "pulled down");
                    }
                    break;
                }
                case Step.FoldOver:
                {
                    var angle = Mathf.MoveTowards(pose.Angle, Mathf.Clamp(held.AngleFrom + Mathf.DeltaAngle(held.AngleRef, angleAt), Fold, 2f * Fold), FoldMost * Time.deltaTime);
                    Hum(held, angle);
                    pose.Angle = angle;
                    if (angle >= 2f * Fold - SnapWithin)
                    {
                        pose.Angle = 2f * Fold;
                        Use(held);
                    }
                    break;
                }
            }
            pose.Show();
        }

        // A step done: a knock, a rest, then the next step from the controller as it is then.
        private static void Next(Held held, Step step, string what)
        {
            held.Input.Buzz(StepAmplitude, StepLength);
            held.Step = step;
            held.PauseUntil = Time.time + StepPause;
            if (logs++ < 80) Plugin.Log.LogInfo($"Nacelle: {held.Input.Name} hand {what}, '{held.Control.name}' rests {StepPause:0.0} s");
        }

        private static void Rebase(Held held, Vector3 grip, float angleAt)
        {
            held.Based = true;
            held.AngleFrom = held.Pose.Angle;
            held.AngleRef = angleAt;
            var pose = held.Pose;
            held.PullOffset = Vector3.Dot(grip - pose.Handle.TransformPoint(held.Reach), pose.Switch.parent.TransformDirection(Vector3.down).normalized);
        }

        // How far the controller is round the handle's hinge, in degrees from where the hand would be at its rest pose.
        private static float AngleAt(Held held, Vector3 grip)
        {
            var pose = held.Pose;
            var frame = pose.Handle.parent.rotation * pose.HandleRest;
            var axis = frame * Axis;
            var rest = frame * held.Reach;
            var to = grip - pose.Handle.position;
            return Vector3.SignedAngle(Vector3.ProjectOnPlane(rest, axis), Vector3.ProjectOnPlane(to, axis), axis);
        }

        private static void Hum(Held held, float angle)
        {
            var speed = Mathf.Abs(angle - held.Pose.Angle) / Mathf.Max(Time.deltaTime, 1e-4f);
            if (speed < 15f || Time.unscaledTime < held.PulseAt) return;
            held.PulseAt = Time.unscaledTime + Every;
            held.Input.Buzz(Mathf.Lerp(HumLeast, HumMost, Mathf.InverseLerp(15f, 180f, speed)), PulseLength);
        }

        // The hand ahead of the switch: the further, the harder it shakes.
        // Moving at all, it shakes at least Least (Raik: more from the start of the pull).
        private static void Shake(Held held, float ahead, bool moving)
        {
            ahead /= VrCamera.WorldScale;
            if ((!moving && ahead < LagFrom) || Time.unscaledTime < held.PulseAt) return;
            held.PulseAt = Time.unscaledTime + Every;
            held.Input.Buzz(Mathf.Lerp(Least, Most, Mathf.InverseLerp(LagFrom, LagFull, ahead)), PulseLength);
        }

        private static void Use(Held held)
        {
            held.Input.Buzz(1f, 0.05f);
            held.Step = Step.Done;
            Used[held.Control] = held.Pose;
            try
            {
                CompleteMethod?.Invoke(held.Control, null);
                if (logs++ < 80) Plugin.Log.LogInfo($"Nacelle: {held.Input.Name} hand folded '{held.Control.name}' over, uses it; it stays down");
            }
            catch (System.Exception e)
            {
                Plugin.Log.LogWarning($"Nacelle: using '{held.Control.name}' failed: {e.InnerException?.Message ?? e.Message}");
            }
        }

        private static void TakeHold(Held held, InteractableObject control)
        {
            if (!control.IsInteractable || !Parts(control, out var handle, out var slider, out _))
            {
                if (logs++ < 80) Plugin.Log.LogInfo($"Nacelle: '{control.name}' held, but cannot be used now");
                return;
            }
            // A switch still going back is this hand's again, from where it is.
            Loose from = null;
            foreach (var loose in Let)
                if (loose.Pose.Handle == handle) from = loose;
            if (from != null) Let.Remove(from);
            var pose = from != null ? from.Pose : new Pose
            {
                Handle = handle,
                Switch = slider,
                HandleRest = handle.localRotation,
                HandleRestPlace = handle.localPosition,
                SwitchRest = slider.localPosition,
            };
            held.Control = control;
            held.Pose = pose;
            held.Step = pose.Slid > 0f ? (pose.Angle > Fold ? Step.FoldOver : Step.Pull) : Step.FoldDown;
            held.Based = false;
            held.PauseUntil = 0f;
            held.BraceLogged = false;
            pose.Show();
            if (VrClimb.TryGetSurface(held.Input, out var onHandle)) held.Reach = handle.InverseTransformPoint(onHandle);
            VrClimb.Reanchor(held.Input, handle);
            VrClimb.SetFree(held.Input, true);
            if (logs++ < 80) Plugin.Log.LogInfo($"Nacelle: {held.Input.Name} hand holds '{handle.name}' of '{control.name}', step {held.Step}");
        }

        private static void LetGo(Held held)
        {
            if (held.Control != null)
            {
                VrClimb.SetFree(held.Input, false);
                if (held.Step != Step.Done) Let.Add(new Loose { Pose = held.Pose, At = Time.time });
                if (logs++ < 80) Plugin.Log.LogInfo($"Nacelle: {held.Input.Name} hand lets go of '{held.Control.name}' at step {held.Step}, {held.Pose.Angle:0}°, {held.Pose.Slid * 100f:0.0} cm");
            }
            held.Control = null;
            held.Pose = null;
        }

        // Let go before the end: back to its start, then the Animator shows it again.
        private static bool Settle(Loose loose)
        {
            var pose = loose.Pose;
            if (pose.Handle == null || pose.Switch == null) return false;
            var t = (Time.time - loose.At) / BackTime;
            if (t >= 1f)
            {
                pose.Angle = 0f;
                pose.Slid = 0f;
                pose.Show();
                return false;
            }
            var back = 1f - t * t * (3f - 2f * t);
            pose.Show(pose.Angle * back, pose.Slid * back);
            return true;
        }

        // Its handle, its switch and the grip on the handle ('HandGrab', where the game's hand goes).
        private static bool Parts(InteractableObject control, out Transform handle, out Transform slider, out Transform grab)
        {
            handle = null;
            slider = null;
            grab = null;
            var parent = control.transform.parent;
            if (parent == null) return false;
            foreach (var child in parent.GetComponentsInChildren<Transform>(true))
            {
                // The first handle with a visible mesh.
                if (child.name.EndsWith("_Handle") && (handle == null || (handle.GetComponent<Renderer>() == null && child.GetComponent<Renderer>() != null))) handle = child;
                if (child.name.EndsWith("_Switch") && slider == null) slider = child;
            }
            grab = control.HandTarget;
            return handle != null && slider != null && grab != null;
        }
    }
}
