using System.Collections.Generic;
using BBI.Unity.Game;
using HarmonyLib;
using UnityEngine;
using UnityEngine.XR;

namespace ShipbreakerVR
{
    // Flying with the left motion controller. Two schemes:
    // - Pavlov ("point and grip"): stick forward/back thrusts along the direction the controller points. Holding the grip
    //   stores the controller orientation; twisting or tilting the hand away from it rolls, pitches or yaws the body.
    //   Stick left/right snap-turns, the trigger brakes.
    // - Vtol: holding the trigger stores the controller pose as a reference; moving the controller away from it thrusts in
    //   that direction, turning it away from it rotates the body. Back at the reference (or trigger released) nothing happens.
    //   Stick left/right snap-turns.
    // Thrust and brake go through the game's own actions (ThrustController reads them via LynxControls), so fuel, forces and
    // HUD stay the game's. Rotation is applied to the playspace in VrBody, so it needs "body follows head".
    internal static class VrControls
    {
        public enum Scheme { Aus, Pavlov, Vtol }

        private const float StickDeadzone = 0.15f;
        private const float SnapAngle = 30f;
        private const float SnapPress = 0.7f;
        private const float SnapRelease = 0.3f;
        private const float GripPress = 0.5f;
        private const float TriggerPress = 0.5f;
        private const float MoveDeadzone = 0.02f;
        private const float MoveFull = 0.15f;
        private const float TurnDeadzone = 8f;
        private const float TurnFull = 45f;
        private const float TurnSpeed = 45f;

        // The aim pose points where the controller points; the device (grip) pose runs along the handle, tilted upward.
        private static readonly InputFeatureUsage<Quaternion> PointerRotation = new InputFeatureUsage<Quaternion>("PointerRotation");

        private static InputDevice left;
        private static bool loggedDevice;
        private static float nextDeviceLog;
        private static int deviceLogs;
        private static int updatedFrame = -1;

        // Desired thrust in world space, length 0..1, and the transform the game thrusts along.
        private static Vector3 thrust;
        private static Transform thrustFrame;

        // Rotation for this frame in world space, already scaled by the frame time.
        private static Quaternion turn = Quaternion.identity;

        // Reference pose while the grip (Pavlov) or trigger (Vtol) is held.
        private static bool held;
        private static Vector3 heldPosition;
        private static Quaternion heldRotation;

        private static bool snapped;
        private static bool braking;

        private static Scheme Active => VrXr.IsRunning && VrCamera.Instance != null ? Plugin.ControlScheme.Value : Scheme.Aus;

        private static void Update()
        {
            if (updatedFrame == Time.frameCount) return;
            updatedFrame = Time.frameCount;
            thrust = Vector3.zero;
            turn = Quaternion.identity;
            braking = false;

            var scheme = Active;
            if (scheme == Scheme.Aus || !GetLeft()) { held = false; return; }
            if (!left.TryGetFeatureValue(CommonUsages.devicePosition, out var position)
                || !left.TryGetFeatureValue(CommonUsages.deviceRotation, out var rotation)) { held = false; return; }
            if (left.TryGetFeatureValue(PointerRotation, out var aim)) rotation = aim;

            // Controller poses are in tracking space, i.e. relative to the rig whose rotation is the playspace.
            var playspace = VrCamera.Instance.Playspace;
            left.TryGetFeatureValue(CommonUsages.primary2DAxis, out var stick);
            SnapTurn(stick.x, playspace);

            if (scheme == Scheme.Pavlov)
            {
                thrust = playspace * (rotation * Vector3.forward) * Deadzone(stick.y, StickDeadzone);
                left.TryGetFeatureValue(CommonUsages.trigger, out var trigger);
                braking = trigger >= TriggerPress;
                left.TryGetFeatureValue(CommonUsages.grip, out var grip);
                if (!Hold(grip >= GripPress, position, rotation)) return;
                TurnFromReference(rotation, playspace);
                return;
            }

            left.TryGetFeatureValue(CommonUsages.trigger, out var vtolTrigger);
            if (!Hold(vtolTrigger >= TriggerPress, position, rotation)) return;
            var offset = position - heldPosition;
            var strength = Ramp(offset.magnitude, MoveDeadzone, MoveFull);
            if (strength > 0f) thrust = playspace * offset.normalized * strength;
            TurnFromReference(rotation, playspace);
        }

        // Stores the pose when the button goes down; true while it stays down (from the frame after the press).
        private static bool Hold(bool pressed, Vector3 position, Quaternion rotation)
        {
            if (!pressed) { held = false; return false; }
            if (held) return true;
            held = true;
            heldPosition = position;
            heldRotation = rotation;
            return false;
        }

        // Turns the body the way the hand is turned away from the reference, faster the further it is turned.
        private static void TurnFromReference(Quaternion rotation, Quaternion playspace)
        {
            (rotation * Quaternion.Inverse(heldRotation)).ToAngleAxis(out var angle, out var axis);
            if (angle > 180f) angle -= 360f;
            var rate = Ramp(Mathf.Abs(angle), TurnDeadzone, TurnFull) * Mathf.Sign(angle);
            if (rate != 0f && !float.IsInfinity(axis.x)) turn = Quaternion.AngleAxis(rate * TurnSpeed * Time.deltaTime, playspace * axis) * turn;
        }

        // One fixed turn per stick flick around the playspace's up axis; the stick has to come back before the next one.
        private static void SnapTurn(float x, Quaternion playspace)
        {
            if (Mathf.Abs(x) < SnapRelease) { snapped = false; return; }
            if (snapped || Mathf.Abs(x) < SnapPress) return;
            snapped = true;
            turn = Quaternion.AngleAxis(Mathf.Sign(x) * SnapAngle, playspace * Vector3.up) * turn;
        }

        private static bool GetLeft()
        {
            if (!left.isValid) left = InputDevices.GetDeviceAtXRNode(XRNode.LeftHand);
            if (!left.isValid)
            {
                if (Time.unscaledTime >= nextDeviceLog && deviceLogs < 5)
                {
                    nextDeviceLog = Time.unscaledTime + 10f;
                    deviceLogs++;
                    var devices = new List<InputDevice>();
                    InputDevices.GetDevices(devices);
                    Plugin.Log.LogInfo($"No left controller yet. XR input devices: {string.Join(", ", devices.ConvertAll(d => $"'{d.name}' ({d.characteristics})"))}");
                }
                return false;
            }
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

        public static bool IsBraking()
        {
            Update();
            return braking;
        }

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

    // Presses the game's brake while the controller asks for it (both brake actions, so the double-input option works too).
    [HarmonyPatch(typeof(LynxControlExtensions), nameof(LynxControlExtensions.GetInputIsPressed))]
    internal static class BrakePatch
    {
        private static readonly LynxControls.ActionSetAndId Left = GameplayActions.GameplayActionSet.ThrustBrakeLeft;
        private static readonly LynxControls.ActionSetAndId Right = GameplayActions.GameplayActionSet.ThrustBrakeRight;

        private static void Postfix(LynxControls.ActionSetAndId actionSetAndId, ref bool __result)
        {
            if (__result || (!actionSetAndId.Equals(Left) && !actionSetAndId.Equals(Right))) return;
            if (VrControls.IsBraking()) __result = true;
        }
    }
}
