using BBI.Unity.Game;
using HarmonyLib;
using UnityEngine;

namespace ShipbreakerVR
{
    // Help for those who get lost or sick in free flight: straightening up, and a brake that needs no thought.
    internal static class VrFlightComfort
    {
        // What happens without thrust: braking by oneself (SimpleThrustPatch), the game's auto brake, or nothing (NoAutoBrakePatch).
        public enum Thrust { Einfach, Standard, Realistisch }

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

        // The last small turn, so the view itself ends level. Looking clearly up or down stays as it is.
        private static Quaternion GazeLevel(Quaternion playspace)
        {
            var gaze = playspace * VrCamera.Instance.HeadRotation * Vector3.forward;
            var pitch = FlightGaze.Pitch(gaze);
            Plugin.Log.LogInfo($"Flight: upright, body {Vector3.Angle(playspace * Vector3.up, Vector3.up):0.0}, gaze {pitch:0.0} degrees off");
            if (Mathf.Abs(pitch) > FlightGaze.Trim) return Quaternion.identity;
            return Quaternion.FromToRotation(gaze, Vector3.ProjectOnPlane(gaze, Vector3.up).normalized);
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
                return GazeLevel(playspace);
            }
            return Quaternion.RotateTowards(Quaternion.identity, Quaternion.FromToRotation(up, Vector3.up), AlignSpeed * dt);
        }
    }

    internal static class FlightGaze
    {
        // A head held a little off level at the end of straightening up left the view that far off the horizon.
        public const float Trim = 5f;

        // Degrees the gaze lies above the horizon, up being the world's.
        public static float Pitch(Vector3 gaze) => 90f - Vector3.Angle(gaze, Vector3.up);
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
            if (VrSettings.ThrustMode.Value != VrFlightComfort.Thrust.Einfach || !VrControls.Flying || VrControls.Holding || VrControls.IsBraking()) return false;
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
    // Steuerung/Schubmodus Realistisch: the game's own brake stays off, so one keeps one's speed.
    [HarmonyPatch(typeof(PlayerMotion), "HandleAutoBrake")]
    internal static class NoAutoBrakePatch
    {
        private static bool Prefix() => VrSettings.ThrustMode.Value != VrFlightComfort.Thrust.Realistisch;
    }

    [HarmonyPatch(typeof(ThrustController), "Update")]
    internal static class SimpleThrustChargePatch
    {
        private static void Prefix(float ___mCharge, out float __state) => __state = ___mCharge;

        private static void Postfix(Rigidbody ___m_Rigidbody, GrabController ___m_GrabController, GrapplingHook ___m_GrapplingHook, ref float ___mCharge, float __state)
        {
            if (___mCharge > __state && SimpleThrustPatch.Brakes(___m_Rigidbody, ___m_GrabController, ___m_GrapplingHook, __state)) ___mCharge = __state;
        }
    }
}
