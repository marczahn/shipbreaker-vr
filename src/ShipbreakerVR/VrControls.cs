using System.Collections.Generic;
using BBI.Unity.Game;
using HarmonyLib;
using UnityEngine;
using UnityEngine.XR;

namespace ShipbreakerVR
{
    // Flying with the left motion controller. Two schemes:
    // - Pavlov: stick forward/back thrusts along the direction the controller points, stick left/right rolls.
    // - Vtol: holding the trigger stores the controller pose as a reference; moving the controller away from it thrusts in
    //   that direction, turning it away from it rotates the body. Back at the reference (or trigger released) nothing happens.
    // Thrust goes through the game's own thrust axes (ThrustController reads them via LynxControls), so fuel, forces and
    // HUD stay the game's. Rotation is applied to the playspace in VrBody, so it needs "body follows head".
    internal static class VrControls
    {
        public enum Scheme { Aus, Pavlov, Vtol }

        private const float StickDeadzone = 0.15f;
        private const float RollSpeed = 90f;
        private const float VtolMoveDeadzone = 0.02f;
        private const float VtolMoveFull = 0.15f;
        private const float VtolTurnDeadzone = 8f;
        private const float VtolTurnFull = 45f;
        private const float VtolTurnSpeed = 90f;

        private static InputDevice left;
        private static bool loggedDevice;
        private static int updatedFrame = -1;

        // Desired thrust in world space, length 0..1, and the transform the game thrusts along.
        private static Vector3 thrust;
        private static Transform thrustFrame;

        // Rotation for this frame in world space, already scaled by the frame time.
        private static Quaternion turn = Quaternion.identity;

        private static bool vtolHeld;
        private static Vector3 vtolPosition;
        private static Quaternion vtolRotation;

        private static Scheme Active => VrXr.IsRunning && VrCamera.Instance != null ? Plugin.ControlScheme.Value : Scheme.Aus;

        private static void Update()
        {
            if (updatedFrame == Time.frameCount) return;
            updatedFrame = Time.frameCount;
            thrust = Vector3.zero;
            turn = Quaternion.identity;

            var scheme = Active;
            if (scheme == Scheme.Aus || !GetLeft()) { vtolHeld = false; return; }
            if (!left.TryGetFeatureValue(CommonUsages.devicePosition, out var position)
                || !left.TryGetFeatureValue(CommonUsages.deviceRotation, out var rotation)) { vtolHeld = false; return; }

            // Controller poses are in tracking space, i.e. relative to the rig whose rotation is the playspace.
            var playspace = VrCamera.Instance.Playspace;
            var dt = Time.deltaTime;

            if (scheme == Scheme.Pavlov)
            {
                left.TryGetFeatureValue(CommonUsages.primary2DAxis, out var stick);
                var forward = Deadzone(stick.y, StickDeadzone);
                var roll = Deadzone(stick.x, StickDeadzone);
                thrust = playspace * (rotation * Vector3.forward) * forward;
                if (roll != 0f)
                {
                    var view = playspace * VrCamera.Instance.HeadRotation * Vector3.forward;
                    turn = Quaternion.AngleAxis(-roll * RollSpeed * dt, view);
                }
                vtolHeld = false;
                return;
            }

            left.TryGetFeatureValue(CommonUsages.trigger, out var trigger);
            if (trigger < 0.5f) { vtolHeld = false; return; }
            if (!vtolHeld)
            {
                vtolHeld = true;
                vtolPosition = position;
                vtolRotation = rotation;
                return;
            }

            var offset = position - vtolPosition;
            var strength = Ramp(offset.magnitude, VtolMoveDeadzone, VtolMoveFull);
            if (strength > 0f) thrust = playspace * offset.normalized * strength;

            (rotation * Quaternion.Inverse(vtolRotation)).ToAngleAxis(out var angle, out var axis);
            if (angle > 180f) angle -= 360f;
            var rate = Ramp(Mathf.Abs(angle), VtolTurnDeadzone, VtolTurnFull) * Mathf.Sign(angle);
            if (rate != 0f && !float.IsInfinity(axis.x)) turn = Quaternion.AngleAxis(rate * VtolTurnSpeed * dt, playspace * axis);
        }

        private static bool GetLeft()
        {
            if (!left.isValid) left = InputDevices.GetDeviceAtXRNode(XRNode.LeftHand);
            if (!left.isValid) return false;
            if (!loggedDevice)
            {
                loggedDevice = true;
                var usages = new List<InputFeatureUsage>();
                left.TryGetFeatureUsages(usages);
                Plugin.Log.LogInfo($"Left controller: '{left.name}' ({left.characteristics}), features: {string.Join(", ", usages.ConvertAll(u => u.name))}");
            }
            return true;
        }

        private static float Deadzone(float value, float deadzone) =>
            Mathf.Abs(value) <= deadzone ? 0f : Mathf.Sign(value) * (Mathf.Abs(value) - deadzone) / (1f - deadzone);

        private static float Ramp(float value, float start, float full) => Mathf.Clamp01((value - start) / (full - start));

        // Rotation to apply to the playspace this frame (world space). Called by VrBody once per body update.
        public static Quaternion TakeTurn()
        {
            Update();
            var result = turn;
            turn = Quaternion.identity;
            return result;
        }

        public static void SetThrustFrame(Transform frame) => thrustFrame = frame;

        // Thrust along the game's thrust axes: x = right, y = up, z = forward of the thrust frame.
        public static bool TryGetThrust(out Vector3 local)
        {
            local = Vector3.zero;
            Update();
            if (thrust == Vector3.zero || thrustFrame == null) return false;
            local = thrustFrame.InverseTransformDirection(thrust);
            return true;
        }
    }

    // Remembers the transform the game thrusts along (the body, or a steering transform when docked).
    [HarmonyPatch(typeof(ThrustController), "Update")]
    internal static class ThrustControllerUpdatePatch
    {
        private static readonly AccessTools.FieldRef<ThrustController, Transform> Steering =
            AccessTools.FieldRefAccess<ThrustController, Transform>("mSteeringOrientationTransform");
        private static readonly AccessTools.FieldRef<ThrustController, Rigidbody> Body =
            AccessTools.FieldRefAccess<ThrustController, Rigidbody>("m_Rigidbody");

        private static void Prefix(ThrustController __instance)
        {
            var steering = Steering(__instance);
            var body = Body(__instance);
            VrControls.SetThrustFrame(steering != null ? steering : body != null ? body.transform : null);
        }
    }

    // Adds the controller thrust to the game's thrust axes.
    [HarmonyPatch(typeof(LynxControlExtensions), nameof(LynxControlExtensions.GetOneAxisInputControlValue))]
    internal static class ThrustAxisPatch
    {
        private static readonly LynxControls.ActionSetAndId LeftRight = GameplayActions.GameplayActionSet.ThrustMoveLeftRightComposite;
        private static readonly LynxControls.ActionSetAndId UpDown = GameplayActions.GameplayActionSet.ThrustMoveUpDownComposite;
        private static readonly LynxControls.ActionSetAndId BackForward = GameplayActions.GameplayActionSet.ThrustMoveBackForwardComposite;

        private static void Postfix(LynxControls.ActionSetAndId actionSetAndId, ref float __result)
        {
            if (!actionSetAndId.Equals(LeftRight) && !actionSetAndId.Equals(UpDown) && !actionSetAndId.Equals(BackForward)) return;
            if (!VrControls.TryGetThrust(out var local)) return;
            var value = actionSetAndId.Equals(LeftRight) ? local.x : actionSetAndId.Equals(UpDown) ? local.y : local.z;
            __result = Mathf.Clamp(__result + value, -1f, 1f);
        }
    }
}
