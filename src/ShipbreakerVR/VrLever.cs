using System.Reflection;
using BBI.Unity.Game;
using HarmonyLib;
using UnityEngine;

namespace ShipbreakerVR
{
    // Levers turned by hand (Raik, 0.29): a hand holding a lever control (the airlock's yellow lever) turns its handle with
    // the turn of the controller about the handle's axis, any time, also while the airlock is still busy. Turned all the
    // way (EndAngle) it uses the control, as A does, but only once the control may be used again (its asset's
    // m_CompletionCooldown since mLastCompletionTime) and only after it was turned back near the start (ArmBelow): a
    // panicked back and forth during the cooldown does nothing. Turning a lever that will trigger hums lightly, snapping
    // in at the end gives one short hard knock; one that will not trigger glides without any.
    //
    // Measured on the airlock (VrInteractSurvey, 2026-11-07): the handle 'GRP_AirlockConsole_Handle' turns about its own
    // -X axis by 60° each time it is used, always from the same rest pose, played by the console's Animator. The hand's
    // target (m_HandTarget) sits on the handle, so the handle is found as the target's nearest parent named "...Handle".
    // The handle's pose is set after the Animator ran (LateUpdate), so the hand's turn wins while it holds it; let go, it
    // glides back to what the Animator shows.
    internal static class VrLever
    {
        private const float EndAngle = 60f;
        private static readonly Vector3 LocalAxis = new Vector3(-1f, 0f, 0f);
        // Turned past this, the hand takes over the handle from the Animator (A alone still plays the game's animation).
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
        // Let go, the handle goes back over this long.
        private const float BackTime = 0.25f;

        private class Held
        {
            public readonly VrInput.Hand Input;
            public InteractableObject Control;
            public Transform Handle;
            public Quaternion Rest;
            public Quaternion GripAtHold;
            public bool Engaged;
            public bool Armed;
            public float Angle;
            public float HumAt;
            // Going back after letting go: from that pose, since then.
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

        // From VrFrame, in LateUpdate after the Animators and before the hands are placed on their holds.
        public static void LateUpdate()
        {
            foreach (var held in Hands) Update(held);
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
            if (held.Handle == null) return;
            if (!held.Input.TryGetGripPose(out _, out var grip)) return;
            var world = VrCamera.Instance.transform.parent.rotation * grip;
            var axis = held.Handle.parent.rotation * held.Rest * LocalAxis;
            var twist = LeverMath.Twist(world * Quaternion.Inverse(held.GripAtHold), axis);
            if (!held.Engaged && Mathf.Abs(twist) < TakeOver) return;
            held.Engaged = true;
            var angle = Mathf.Clamp(twist, 0f, EndAngle);
            var speed = Mathf.Abs(angle - held.Angle) / Mathf.Max(Time.deltaTime, 1e-4f);
            held.Angle = angle;
            held.Handle.localRotation = held.Rest * Quaternion.AngleAxis(angle, LocalAxis);

            if (angle < ArmBelow) held.Armed = true;
            var ready = Ready(held.Control, out _);
            if (!held.Armed || !ready) return;
            if (angle >= EndAngle - SnapWithin)
            {
                held.Armed = false;
                held.Input.Buzz(SnapAmplitude, SnapLength);
                VrToolInput.PressInteract();
                if (logs++ < 40) Plugin.Log.LogInfo($"Lever: {held.Input.Name} hand turned '{held.Handle.name}' all the way, uses '{held.Control.name}'");
                return;
            }
            if (speed < HumFrom || Time.unscaledTime < held.HumAt) return;
            held.HumAt = Time.unscaledTime + HumEvery;
            held.Input.Buzz(Mathf.Lerp(LightLeast, LightMost, Mathf.InverseLerp(HumFrom, HumFull, speed)), HumLength);
        }

        private static void TakeHold(Held held, InteractableObject control)
        {
            held.Control = control;
            var handle = Handle(control);
            if (handle == null || !held.Input.TryGetGripPose(out _, out var grip)) return;
            // A lever still going back is this hand's now, from the rest pose it goes back to, not the pose it has on the way.
            var rest = handle.localRotation;
            foreach (var other in Hands)
                if (other.Returning == handle)
                {
                    rest = other.ReturnRest;
                    other.Returning = null;
                }
            held.Handle = handle;
            held.Rest = rest;
            held.GripAtHold = VrCamera.Instance.transform.parent.rotation * grip;
            held.Engaged = false;
            held.Armed = true;
            held.Angle = 0f;
            VrClimb.Reanchor(held.Input, handle);
            if (logs++ < 40)
            {
                Ready(control, out var wait);
                Plugin.Log.LogInfo($"Lever: {held.Input.Name} hand holds '{handle.name}' of '{control.name}' ({TypeName(control)}), "
                    + (wait > 0f ? $"usable again in {wait:0.0} s" : "usable"));
            }
        }

        private static void LetGo(Held held)
        {
            if (held.Handle != null && held.Engaged)
            {
                held.Returning = held.Handle;
                held.ReturnFrom = held.Handle.localRotation;
                held.ReturnRest = held.Rest;
                held.ReturnAt = Time.time;
            }
            held.Control = null;
            held.Handle = null;
            held.Engaged = false;
        }

        // Back to the rest pose it had when taken hold of; from then on the Animator shows it as before.
        private static void Return(Held held)
        {
            var handle = held.Returning;
            if (handle == null) return;
            var t = (Time.time - held.ReturnAt) / BackTime;
            if (t >= 1f)
            {
                held.Returning = null;
                return;
            }
            handle.localRotation = Quaternion.Slerp(held.ReturnFrom, held.ReturnRest, t * t * (3f - 2f * t));
        }

        // Whether using it now would do something: its cooldown since it was last used is over; how long until then.
        private static bool Ready(InteractableObject control, out float wait)
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
            if (logs++ < 40) Plugin.Log.LogInfo($"Lever: '{control.name}' is a lever without a handle found from its hand target '{(target != null ? target.name : "none")}'");
            return null;
        }

        private static object Data(InteractableObject control)
        {
            var asset = Asset?.GetValue(control);
            return asset == null ? null : AccessTools.Field(asset.GetType(), "m_Data")?.GetValue(asset);
        }

        private static string TypeName(InteractableObject control)
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
