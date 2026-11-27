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
            if (VrSuitPower.Off)
            {
                VrInput.Left.Buzz(0.2f, 0.15f);
                Plugin.Log.LogInfo("Flight: no straightening up, the suit is shut down");
                return;
            }
            aligning = true;
            VrInput.Left.Buzz(0.5f, 0.06f);
            Plugin.Log.LogInfo("Flight: straightening up");
        }

        // Straightening up under way ends where it is (the suit shut down, VrSuitPower).
        public static void StopAligning(string why)
        {
            if (!aligning) return;
            aligning = false;
            Plugin.Log.LogInfo($"Flight: straightening up stops, {why}");
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
            // Realistic: the suit's thrusters turn one upright, on its fuel (AlignFuelPatch); with the tank empty, not.
            if (VrSettings.ThrustMode.Value == Thrust.Realistisch)
            {
                if (AlignFuelPatch.Empty)
                {
                    aligning = false;
                    VrInput.Left.Buzz(0.2f, 0.15f);
                    Plugin.Log.LogInfo($"Flight: no fuel left to straighten up, {angle:0.0} degrees off");
                    return Quaternion.identity;
                }
                AlignFuelPatch.Turned += Mathf.Min(angle, AlignSpeed * dt) / AlignSpeed;
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

    // Steuerung/Schubmodus Einfach: without the trigger the body brakes to a stop by itself, on the suit's fuel.
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

    // Steuerung/Schubmodus Realistisch: the game's own brake stays off, so one keeps one's speed.
    [HarmonyPatch(typeof(PlayerMotion), "HandleAutoBrake")]
    internal static class NoAutoBrakePatch
    {
        private static bool Prefix() => VrSettings.ThrustMode.Value != VrFlightComfort.Thrust.Realistisch;
    }

    // Raik (2026-11-20): with Realistic, straightening up costs fuel as the maneuvering thrusters do: while it turns, as
    // much as thrusting sideways at full (the game's DrainPerSecond across), and the tank does not fill meanwhile. As the
    // game, nothing while docked, with its drain switched off, or in the career before the tutorial is done. Taken
    // through the game's own Charge, so the visor's gauge and its warnings follow.
    [HarmonyPatch(typeof(ThrustController), "Update")]
    internal static class AlignFuelPatch
    {
        // Seconds of turning at AlignSpeed since the game last took fuel; and whether the tank was empty then.
        public static float Turned;
        public static bool Empty;

        private static readonly AccessTools.FieldRef<ThrustController, IThrusterData> Data =
            AccessTools.FieldRefAccess<ThrustController, IThrusterData>("mData");
        private static readonly AccessTools.FieldRef<ThrustController, Transform> Steering =
            AccessTools.FieldRefAccess<ThrustController, Transform>("mSteeringOrientationTransform");
        private static readonly AccessTools.FieldRef<ThrustController, bool> NoDrain =
            AccessTools.FieldRefAccess<ThrustController, bool>("mIgnoreThrusterDrain");
        private static readonly System.Reflection.MethodInfo SetCharge = AccessTools.PropertySetter(typeof(ThrustController), "Charge");
        private static bool logged;

        private static void Prefix(ThrustController __instance, out float __state) => __state = __instance.Charge;

        private static void Postfix(ThrustController __instance, float __state)
        {
            Empty = __instance.Charge <= 0f;
            var turned = Turned;
            Turned = 0f;
            if (turned <= 0f || VrSettings.ThrustMode.Value != VrFlightComfort.Thrust.Realistisch) return;
            if (Steering(__instance) != null || NoDrain(__instance) || SetCharge == null) return;
            if (GameSession.CurrentSessionType == GameSession.SessionType.Career
                && (PlayerProfileService.Instance?.Profile == null || !PlayerProfileService.Instance.Profile.TutorialCompleted)) return;
            var drain = Data(__instance).DrainPerSecond.PositiveX * turned;
            var charge = Mathf.Max(Mathf.Min(__instance.Charge, __state) - drain, 0f);
            SetCharge.Invoke(__instance, new object[] { charge });
            Empty = charge <= 0f;
            if (logged) return;
            logged = true;
            Plugin.Log.LogInfo($"Flight: straightening up takes fuel as thrusting sideways does, {Data(__instance).DrainPerSecond.PositiveX:0.##} per second of turning at {__instance.Charge:0.#} of {__instance.MaxCharge:0.#}");
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
}
