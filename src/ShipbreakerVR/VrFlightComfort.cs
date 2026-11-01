using BBI.Unity.Game;
using HarmonyLib;
using UnityEngine;

namespace ShipbreakerVR
{
    // Help for those who get lost or sick in free flight: straightening up, and a brake that needs no thought.
    internal static class VrFlightComfort
    {
        // Degrees per second of the turn back to upright. Slow enough to follow, fast enough to be over soon.
        private const float AlignSpeed = 90f;
        private const float AlignDone = 0.5f;

        private static bool aligning;

        // Holding the left stick in: upright to the world's up, keeping the heading.
        public static void StartAligning()
        {
            aligning = true;
            VrInput.Left.Buzz(0.5f, 0.06f);
            Plugin.Log.LogInfo("Flight: straightening up");
        }

        // The turn for this frame, in the world.
        public static Quaternion AlignStep(Quaternion playspace, float dt)
        {
            if (!aligning) return Quaternion.identity;
            var up = playspace * Vector3.up;
            var angle = Vector3.Angle(up, Vector3.up);
            if (angle < AlignDone)
            {
                aligning = false;
                return Quaternion.identity;
            }
            return Quaternion.RotateTowards(Quaternion.identity, Quaternion.FromToRotation(up, Vector3.up), AlignSpeed * dt);
        }
    }

    internal static class SimpleThrust
    {
        // Of the game's own brake.
        public const float Share = 1f / 3f;

        // A speed after a step of braking: down to zero, not past it.
        public static float Slowed(float speed, float braking, float dt) => Mathf.Max(speed - braking * dt, 0f);
    }

    // Steuerung/EinfacherSchub: without the trigger the body brakes to a stop by itself, on the suit's fuel.
    [HarmonyPatch(typeof(ThrustController), "FixedUpdate")]
    internal static class SimpleThrustPatch
    {
        private const float Stopped = 0.01f;

        public static bool Brakes(Rigidbody body, GrabController grab, GrapplingHook grapple, float charge)
        {
            if (!VrSettings.SimpleThrust.Value || !VrControls.Flying || VrControls.Holding || VrControls.IsBraking()) return false;
            if (body.isKinematic || charge <= 0f || body.velocity.magnitude < Stopped) return false;
            if (grapple.IsSwinging || grapple.GrappledRigidbody != null || grab.IsGrabPreventingThrust) return false;
            return VrHand.Left.State != HandState.Holding && VrHand.Right.State != HandState.Holding;
        }

        private static void Postfix(Rigidbody ___m_Rigidbody, GrabController ___m_GrabController, GrapplingHook ___m_GrapplingHook, IThrusterData ___mData, ref float ___mCharge)
        {
            if (!Brakes(___m_Rigidbody, ___m_GrabController, ___m_GrapplingHook, ___mCharge)) return;
            var velocity = ___m_Rigidbody.velocity;
            var speed = velocity.magnitude;
            var dt = Time.fixedDeltaTime;
            ___m_Rigidbody.velocity = velocity * (SimpleThrust.Slowed(speed, ___mData.BrakingForce * SimpleThrust.Share, dt) / speed);
            ___mCharge = Mathf.Max(___mCharge - ___mData.BrakingDrainPerSecond * SimpleThrust.Share * dt, 0f);
        }
    }

    // The game sees no thrust and no brake there and would fill the tank meanwhile, which would make braking pay.
    [HarmonyPatch(typeof(ThrustController), "Update")]
    internal static class SimpleThrustChargePatch
    {
        private static void Prefix(float ___mCharge, out float __state) => __state = ___mCharge;

        private static void Postfix(Rigidbody ___m_Rigidbody, GrabController ___m_GrabController, GrapplingHook ___m_GrapplingHook, ref float ___mCharge, float __state)
        {
            if (___mCharge > __state && SimpleThrustPatch.Brakes(___m_Rigidbody, ___m_GrabController, ___m_GrapplingHook, __state)) ___mCharge = __state;
        }
    }

    // Steuerung/MinenPerHand: the cut goes by how the hand holds the charge instead of A.
    internal static class ChargeHold
    {
        private const float Upright = 45f;
        private const float Margin = 8f;

        // tilt: 0 with the hand level, 90 on edge. Past the middle only by a margin, so it does not flicker there.
        public static bool OnEdge(float tilt, bool wasOnEdge) => tilt > (wasOnEdge ? Upright - Margin : Upright + Margin);

        public static float Tilt(Quaternion hand, Quaternion head) =>
            Mathf.Abs(90f - Vector3.Angle(hand * Vector3.right, head * Vector3.up));
    }

    [HarmonyPatch(typeof(DemoChargeController), "UpdateTargeting")]
    internal static class ChargeHoldPatch
    {
        private static readonly Quaternion Vertical = Quaternion.Euler(0f, 0f, 90f);

        private static void Prefix(ref bool ___mHorizontalCutNext, ref Quaternion ___mCutRotation)
        {
            if (!VrSettings.ChargeByHand.Value || !VrAim.Active || VrCamera.Instance == null) return;
            var hand = VrToolModels.ChargeInLeftHand ? VrInput.Left : VrInput.Right;
            if (!hand.TryGetAimRotation(out var rotation)) return;
            var wasOnEdge = ___mHorizontalCutNext == false;
            var onEdge = ChargeHold.OnEdge(ChargeHold.Tilt(rotation, VrCamera.Instance.HeadRotation), wasOnEdge);
            if (onEdge == wasOnEdge) return;
            ___mHorizontalCutNext = !onEdge;
            ___mCutRotation = onEdge ? Vertical : Quaternion.identity;
            ((Carbon.Core.Events.IEventBroadcaster)Main.EventSystem).Post(DemoChargeRotationChangedEvent.GetEvent(___mCutRotation));
        }
    }

    [HarmonyPatch(typeof(DemoChargeController), "ToggleCutRotation")]
    internal static class ChargeTogglePatch
    {
        private static bool Prefix() => !VrSettings.ChargeByHand.Value;
    }
}
