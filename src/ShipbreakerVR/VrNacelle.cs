using System.Collections.Generic;
using BBI.Unity.Game;
using HarmonyLib;
using UnityEngine;

namespace ShipbreakerVR
{
    // The nacelle switch that cuts the fuel lines, worked by hand (Raik, 0.29), in the three steps of the game's own
    // animation: fold the handle down (the hand goes round its hinge with it, 90°), pull it down (the switch slides 16 cm,
    // no faster than PullMost: it goes heavily, and the further the hand runs ahead of it the harder it shakes), fold it
    // over (another 90°). Then it uses the switch (InteractableObject.Complete, as holding A for 4 s did). A hand that came
    // off the handle in a step has to come back to it (CatchReach) before the next step follows it: a fiddly job on
    // purpose. Let go before the end, it goes back to where it started. The hand holds free (VrClimb.SetFree): it pulls
    // nothing, the body stays where it is, it is not torn off however far the controller goes, and it turns freely on the
    // handle (Raik: the wrist turns as the switch goes down, without moving it).
    //
    // Measured (VrInteractSurvey, clip 'Switch_Flush_Turn_Off', 4.33 s, 2026-11-08): the handle
    // 'SM_Switch_Flush_AxCxA_Handle' under the control turns about its own +X by 90° (0 to 1.44 s); the switch
    // 'SM_Switch_Flush_AxCxA_Switch' beside the control slides 16 cm down its parent's -Y (1.44 to 3.25 s); the handle
    // turns on to 180° (3.25 to 4.33 s). Poses are set after the Animator ran (LateUpdate).
    internal static class VrNacelle
    {
        private const string TypeName = "Lever_Nacelle_InteractionTypeAsset";
        private static readonly Vector3 Axis = Vector3.right;
        private const float Fold = 90f;
        private const float Slide = 0.16039f;
        private const float SnapWithin = 2f;
        private const float SlideSnap = 0.004f;
        // The pull follows the hand at most this fast, in m/s at world scale 1 (the game's pull takes 1.8 s).
        private const float PullMost = 0.12f;
        // The hand back within this of the handle, the next step follows it.
        // The controller back within this of where the hand holds the handle, the next step follows it (m at world scale 1).
        private const float CatchReach = 0.08f;
        // The shaking: from LagFrom ahead, Least to Most at LagFull, a pulse every Every.
        private const float LagFrom = 0.005f;
        private const float LagFull = 0.06f;
        private const float Least = 0.1f;
        private const float Most = 0.6f;
        private const float Every = 0.05f;
        private const float PulseLength = 0.07f;
        private const float HumLeast = 0.06f;
        private const float HumMost = 0.18f;
        private const float StepAmplitude = 0.8f;
        private const float StepLength = 0.035f;
        private const float BackTime = 0.6f;
        // Used, its pose is kept until the game's own animation has played to its end pose.
        private const float UsedHold = 5f;

        private enum Step { FoldDown, Pull, FoldOver, Done }

        private class Held
        {
            public readonly VrInput.Hand Input;
            public InteractableObject Control;
            public Transform Handle;
            public Transform Switch;
            public Quaternion HandleRest;
            public Vector3 HandleRestPlace;
            public Vector3 SwitchRest;
            public Step Step;
            public bool Caught;
            public Vector3 PullFrom;
            // Where the hand holds the handle, in the handle's own axes.
            public Vector3 Reach;
            public float SlideFrom;
            public float Angle;
            public float Slid;
            public float PulseAt;

            public Held(VrInput.Hand input) => Input = input;
        }

        // A switch let go of: going back from its pose, or kept at its used pose.
        private class Loose
        {
            public Transform Handle;
            public Transform Switch;
            public Quaternion HandleRest;
            public Vector3 HandleRestPlace;
            public Vector3 SwitchRest;
            public float Angle;
            public float Slid;
            public float At;
            public bool Used;
        }

        private static readonly Held[] Hands = { new Held(VrInput.Left), new Held(VrInput.Right) };
        private static readonly List<Loose> Let = new List<Loose>();
        private static readonly Dictionary<InteractableObject, bool> Known = new Dictionary<InteractableObject, bool>();
        private static readonly System.Reflection.MethodInfo CompleteMethod = AccessTools.Method(typeof(InteractableObject), "Complete");
        private static int logs;

        // A nacelle switch: its prompt is hidden and A does not use it.
        public static bool Is(InteractableObject control)
        {
            if (control == null) return false;
            if (!Known.TryGetValue(control, out var known))
            {
                var type = VrLever.TypeName(control);
                var parts = Parts(control, out var handle, out var slider);
                known = type == TypeName && parts;
                Known[control] = known;
                if (type.Contains("Nacelle") && logs++ < 80)
                    Plugin.Log.LogInfo($"Nacelle: '{control.name}' ({type}) under '{(control.transform.parent != null ? control.transform.parent.name : "none")}': "
                        + $"handle '{(handle != null ? handle.name + " under '" + handle.parent.name + "'" : "none")}', switch '{(slider != null ? slider.name : "none")}'{(known ? "" : ", not worked by hand")}");
            }
            return known;
        }

        // Whether at, where a hand takes hold, is on the switch's handle: then where the hand sits on it and its direction
        // (VrClimb holds it there; further off the hand holds the hull as anywhere, Raik: the grab was too wide).
        public static bool OnGrip(InteractableObject control, Vector3 at, out Vector3 onGrip, out Vector3 axis)
        {
            onGrip = at;
            axis = Vector3.zero;
            if (!Parts(control, out var handle, out _)) return false;
            var bar = handle.GetComponent<MeshFilter>();
            if (bar == null || bar.sharedMesh == null) return false;
            // Out: away from the middle of the housing.
            var housing = control.transform.parent != null ? control.transform.parent.GetComponent<Renderer>() : null;
            var middle = housing != null ? housing.bounds.center : control.transform.parent.position;
            return VrLever.GripLine(bar, bar.transform.TransformPoint(bar.sharedMesh.bounds.center) - middle, at, GripReach, "Nacelle", out onGrip, out axis);
        }

        private const float GripReach = 0.06f;

        public static bool Holds => Hands[0].Control != null || Hands[1].Control != null;

        // From VrFrame, in LateUpdate after the Animators and before the hands are placed on their holds.
        public static void LateUpdate()
        {
            foreach (var held in Hands) Update(held);
            for (var i = Let.Count - 1; i >= 0; i--)
                if (!Settle(Let[i])) Let.RemoveAt(i);
        }

        private static void Update(Held held)
        {
            var control = VrXr.IsRunning ? VrClimb.HeldControl(held.Input) : null;
            if (control != null && !Is(control)) control = null;
            if (control != held.Control)
            {
                LetGo(held);
                if (control != null) TakeHold(held, control);
            }
            if (held.Control == null) return;
            // Used: kept at its end pose while the game's animation plays to it.
            if (held.Step == Step.Done)
            {
                Pose(held.Handle, held.Switch, held.HandleRest, held.HandleRestPlace, held.SwitchRest, held.Angle, held.Slid);
                return;
            }
            if (!held.Input.TryGetGripPose(out var gripLocal, out var gripTurn)) return;
            if (!VrToolModels.TrackingToWorld(gripLocal, gripTurn, out var grip, out _)) return;
            // The handle where it was left, over what the Animator set this frame.
            Pose(held.Handle, held.Switch, held.HandleRest, held.HandleRestPlace, held.SwitchRest, held.Angle, held.Slid);
            var hand = held.Handle.TransformPoint(held.Reach);
            // It goes heavily: only with the other hand braced on something fixed (Raik). Without, it stays where it is
            // and the hand has to come back to it once braced.
            if (!VrClimb.Braced(held.Input))
            {
                if (held.Caught && logs++ < 80) Plugin.Log.LogInfo($"Nacelle: {held.Input.Name} hand without the other braced, '{held.Control.name}' does not move");
                held.Caught = false;
                Pose(held.Handle, held.Switch, held.HandleRest, held.HandleRestPlace, held.SwitchRest, held.Angle, held.Slid);
                return;
            }
            var near = (grip - hand).magnitude <= CatchReach * VrCamera.WorldScale;
            var angleAt = AngleAt(held, grip);
            var down = held.Switch.parent.TransformDirection(Vector3.down).normalized;
            var travel = held.Switch.parent.TransformVector(Vector3.up * Slide).magnitude;
            switch (held.Step)
            {
                case Step.FoldDown:
                {
                    if (!held.Caught)
                    {
                        if (!near) break;
                        Catch(held, grip, "on the handle, braced");
                    }
                    var angle = Mathf.Clamp(angleAt, 0f, Fold);
                    Hum(held, angle);
                    held.Angle = angle;
                    if (angle >= Fold - SnapWithin) Next(held, Step.Pull, "folded down");
                    break;
                }
                case Step.Pull:
                {
                    // Folding out, the hand comes off the handle: it has to come back to it first.
                    if (!held.Caught)
                    {
                        if (!near) break;
                        Catch(held, grip, "on the folded handle");
                    }
                    var wanted = Mathf.Clamp(held.SlideFrom + Vector3.Dot(grip - held.PullFrom, down), 0f, travel);
                    var most = PullMost * VrCamera.WorldScale * Time.deltaTime;
                    held.Slid = Mathf.MoveTowards(held.Slid, wanted, most);
                    Shake(held, held.SlideFrom + Vector3.Dot(grip - held.PullFrom, down) - held.Slid);
                    if (held.Slid >= travel - SlideSnap * VrCamera.WorldScale)
                    {
                        held.Slid = travel;
                        Next(held, Step.FoldOver, "pulled down");
                    }
                    break;
                }
                case Step.FoldOver:
                {
                    // Pulled on past the end: the hand has to come back to the handle first.
                    if (!held.Caught)
                    {
                        if (!near) break;
                        Catch(held, grip, "back at the pulled handle");
                    }
                    var angle = Mathf.Clamp(angleAt < -90f ? angleAt + 360f : angleAt, Fold, 2f * Fold);
                    Hum(held, angle);
                    held.Angle = angle;
                    if (angle >= 2f * Fold - SnapWithin)
                    {
                        held.Angle = 2f * Fold;
                        Use(held);
                    }
                    break;
                }
            }
            Pose(held.Handle, held.Switch, held.HandleRest, held.HandleRestPlace, held.SwitchRest, held.Angle, held.Slid);
        }

        // Each step goes on from where the last one left the controller, once it is back at the handle (Catch).
        private static void Next(Held held, Step step, string what)
        {
            held.Input.Buzz(StepAmplitude, StepLength);
            held.Step = step;
            held.Caught = false;
            if (logs++ < 80) Plugin.Log.LogInfo($"Nacelle: {held.Input.Name} hand {what}, '{held.Control.name}' waits for the hand");
        }

        private static void Catch(Held held, Vector3 grip, string where)
        {
            held.Caught = true;
            held.PullFrom = grip;
            held.SlideFrom = held.Slid;
            if (logs++ < 80) Plugin.Log.LogInfo($"Nacelle: {held.Input.Name} hand is {where}, goes on");
        }

        // How far the controller is round the handle's hinge, from where the hand would be at its rest pose, in degrees:
        // the handle goes with the hand on its way round (Raik: following the switch's own movement, not turning the
        // controller).
        private static float AngleAt(Held held, Vector3 grip)
        {
            var frame = held.Handle.parent.rotation * held.HandleRest;
            var axis = frame * Axis;
            var rest = frame * held.Reach;
            var to = grip - held.Handle.position;
            return Vector3.SignedAngle(Vector3.ProjectOnPlane(rest, axis), Vector3.ProjectOnPlane(to, axis), axis);
        }

        private static void Hum(Held held, float angle)
        {
            var speed = Mathf.Abs(angle - held.Angle) / Mathf.Max(Time.deltaTime, 1e-4f);
            if (speed < 15f || Time.unscaledTime < held.PulseAt) return;
            held.PulseAt = Time.unscaledTime + Every;
            held.Input.Buzz(Mathf.Lerp(HumLeast, HumMost, Mathf.InverseLerp(15f, 180f, speed)), PulseLength);
        }

        // The hand ahead of the switch: the further, the harder it shakes.
        private static void Shake(Held held, float ahead)
        {
            ahead /= VrCamera.WorldScale;
            if (ahead < LagFrom || Time.unscaledTime < held.PulseAt) return;
            held.PulseAt = Time.unscaledTime + Every;
            held.Input.Buzz(Mathf.Lerp(Least, Most, Mathf.InverseLerp(LagFrom, LagFull, ahead)), PulseLength);
        }

        private static void Use(Held held)
        {
            held.Input.Buzz(1f, 0.05f);
            held.Step = Step.Done;
            try
            {
                CompleteMethod?.Invoke(held.Control, null);
                if (logs++ < 80) Plugin.Log.LogInfo($"Nacelle: {held.Input.Name} hand folded '{held.Control.name}' over, uses it");
            }
            catch (System.Exception e)
            {
                Plugin.Log.LogWarning($"Nacelle: using '{held.Control.name}' failed: {e.InnerException?.Message ?? e.Message}");
            }
        }

        private static void TakeHold(Held held, InteractableObject control)
        {
            if (!control.IsInteractable || !Parts(control, out var handle, out var slider))
            {
                if (logs++ < 80) Plugin.Log.LogInfo($"Nacelle: '{control.name}' held, but cannot be used now");
                return;
            }
            // A switch still going back is this hand's again, from where it is.
            Loose from = null;
            foreach (var loose in Let)
                if (loose.Handle == handle && !loose.Used) from = loose;
            if (from != null) Let.Remove(from);
            held.Control = control;
            held.Handle = handle;
            held.Switch = slider;
            held.HandleRest = from != null ? from.HandleRest : handle.localRotation;
            held.HandleRestPlace = from != null ? from.HandleRestPlace : handle.localPosition;
            held.SwitchRest = from != null ? from.SwitchRest : slider.localPosition;
            held.Angle = from != null ? from.Angle : 0f;
            held.Slid = from != null ? from.Slid : 0f;
            held.Step = held.Slid > 0f ? Step.Pull : Step.FoldDown;
            held.Caught = false;
            Pose(handle, slider, held.HandleRest, held.HandleRestPlace, held.SwitchRest, held.Angle, held.Slid);
            if (held.Input.TryGetGripPose(out var gripLocal, out var gripTurn) && VrToolModels.TrackingToWorld(gripLocal, gripTurn, out var grip, out _))
            {
                held.PullFrom = grip;
                held.Reach = handle.InverseTransformPoint(grip);
            }
            if (VrClimb.TryGetSurface(held.Input, out var onHandle)) held.Reach = handle.InverseTransformPoint(onHandle);
            held.SlideFrom = held.Slid;
            // Taken hold of at the first step, it turns with the hand at once.
            held.Caught = held.Step == Step.FoldDown;
            VrClimb.Reanchor(held.Input, handle);
            VrClimb.SetFree(held.Input, true);
            if (logs++ < 80) Plugin.Log.LogInfo($"Nacelle: {held.Input.Name} hand holds '{handle.name}' of '{control.name}', step {held.Step}");
        }

        private static void LetGo(Held held)
        {
            if (held.Control != null)
            {
                VrClimb.SetFree(held.Input, false);
                Let.Add(new Loose
                {
                    Handle = held.Handle,
                    Switch = held.Switch,
                    HandleRest = held.HandleRest,
                    HandleRestPlace = held.HandleRestPlace,
                    SwitchRest = held.SwitchRest,
                    Angle = held.Angle,
                    Slid = held.Slid,
                    At = Time.time,
                    Used = held.Step == Step.Done,
                });
                if (logs++ < 80) Plugin.Log.LogInfo($"Nacelle: {held.Input.Name} hand lets go of '{held.Control.name}' at step {held.Step}, {held.Angle:0}°, {held.Slid * 100f:0.0} cm");
            }
            held.Control = null;
            held.Handle = null;
            held.Switch = null;
        }

        // Let go: a used switch kept at its pose until the game's animation is there too; any other back to its start.
        private static bool Settle(Loose loose)
        {
            if (loose.Handle == null || loose.Switch == null) return false;
            var t = (Time.time - loose.At) / (loose.Used ? UsedHold : BackTime);
            if (t >= 1f)
            {
                if (!loose.Used) Pose(loose.Handle, loose.Switch, loose.HandleRest, loose.HandleRestPlace, loose.SwitchRest, 0f, 0f);
                return false;
            }
            var back = loose.Used ? 1f : 1f - t * t * (3f - 2f * t);
            Pose(loose.Handle, loose.Switch, loose.HandleRest, loose.HandleRestPlace, loose.SwitchRest, loose.Angle * back, loose.Slid * back);
            return true;
        }

        // The handle folded by angle and, with the switch, slid down by slid (world length).
        private static void Pose(Transform handle, Transform slider, Quaternion handleRest, Vector3 handleRestPlace, Vector3 switchRest, float angle, float slid)
        {
            slider.localPosition = switchRest;
            handle.localPosition = handleRestPlace;
            handle.localRotation = handleRest * Quaternion.AngleAxis(angle, Axis);
            if (slid <= 0f) return;
            var down = slider.parent.TransformDirection(Vector3.down).normalized * slid;
            slider.position += down;
            // A handle on the switch goes with it already.
            if (!handle.IsChildOf(slider)) handle.position += down;
        }

        // Its handle and its switch.
        private static bool Parts(InteractableObject control, out Transform handle, out Transform slider)
        {
            handle = null;
            slider = null;
            // Both somewhere under the switch's housing (the survey's list does not show the handle's real parent).
            var parent = control.transform.parent;
            if (parent == null) return false;
            foreach (var child in parent.GetComponentsInChildren<Transform>(true))
            {
                if (child.name.EndsWith("_Handle")) handle = child;
                if (child.name.EndsWith("_Switch")) slider = child;
            }
            return handle != null && slider != null;
        }
    }
}
