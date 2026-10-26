using System.Collections.Generic;
using BBI.Unity.Game;
using HarmonyLib;
using UnityEngine;
using UnityEngine.XR;

namespace ShipbreakerVR
{
    // Flying with the motion controllers (VTOL): holding the left trigger stores the left controller pose as a reference.
    // Moving the controller away from it thrusts in that direction; tilting it sideways rolls, around the body's own axis
    // and faster the further the hand is tilted. Back at the reference (or trigger released) nothing happens. In gameplay
    // the right stick turns (left/right; smoothly or in snap steps, see Turning) and pitches (up/down), faster the further
    // it is pushed, and Y brakes. The grips stay free for grabbing. While the left hand holds a tool its controller works
    // the tool (VrToolInput), so there is no flying and no braking then; the right stick still turns.
    // Thrust and brake go through the game's own actions (ThrustController reads them via LynxControls), so fuel, forces and
    // HUD stay the game's. Rotation is applied in VrBody: to the playspace with "body follows head", to the body without.
    internal static class VrControls
    {
        public enum Scheme { Aus, Vtol }

        // Turning with the right stick: smoothly, or one fixed step per flick.
        public enum Turning { Fliessend, Snap30, Snap45, Snap90 }

        private const float SnapPress = 0.7f;
        private const float SnapRelease = 0.3f;
        private const float TriggerPress = 0.5f;
        // Hand offset for thrust, in centimeters (Plugin.ThrustDeadzone, Plugin.ThrustFull): full thrust needs at least
        // MinThrustRange more than the deadzone, so the ramp between them never shrinks to a switch.
        public const float DeadzoneMax = 10f;
        public const float FullMax = 30f;
        public const float MinThrustRange = 5f;
        // The wrist relative to the grip pose (which runs along the handle): an estimate, about 8 cm behind the grip.
        private static readonly Vector3 WristOffset = new Vector3(0f, 0f, -0.08f);
        private const float TurnDeadzone = 8f;
        private const float TurnFull = 45f;
        private const float TurnSpeed = 45f;
        private const float StickDeadzone = 0.15f;

        // The aim pose points where the controller points; the device (grip) pose runs along the handle, tilted upward.
        private static readonly InputFeatureUsage<Quaternion> PointerRotation = new InputFeatureUsage<Quaternion>("PointerRotation");

        private static InputDevice left;
        private static InputDevice right;
        private static bool loggedDevice;
        private static float nextDeviceLog;
        private static int deviceLogs;
        private static int updatedFrame = -1;

        // Desired thrust in world space, length 0..1, and the transform the game thrusts along.
        private static Vector3 thrust;
        private static Transform thrustFrame;

        // Rotation for this frame in world space, already scaled by the frame time.
        private static Quaternion turn = Quaternion.identity;

        // Reference pose while the trigger is held.
        private static bool held;
        private static Vector3 heldPosition;
        private static Quaternion heldRotation;
        // The hand this frame, for the flight globe.
        private static Vector3 handPosition;
        private static Quaternion handRotation;

        private static bool snapped;
        private static bool braking;
        // A left trigger still held from working a tool with that hand flies only after it was let go.
        private static bool triggerFromTool;

        private static Scheme Active => VrXr.IsRunning && VrCamera.Instance != null ? Plugin.ControlScheme.Value : Scheme.Aus;

        private static void Update()
        {
            if (updatedFrame == Time.frameCount) return;
            updatedFrame = Time.frameCount;
            thrust = Vector3.zero;
            turn = Quaternion.identity;
            braking = false;

            if (Active == Scheme.Aus) { held = false; return; }
            // Controller poses are in tracking space, i.e. relative to the rig whose rotation is the playspace.
            var playspace = VrCamera.Instance.Playspace;

            if (!GetLeft()) { held = false; return; }
            var tool = VrToolInput.LeftHoldsTool;
            // Outside gameplay the sticks and buttons work the menus as a gamepad; turning there would swing the menus.
            if (GameSession.CurrentGameState == GameSession.GameState.Gameplay)
            {
                if (!tool) left.TryGetFeatureValue(CommonUsages.secondaryButton, out braking);
                if (GetRight() && right.TryGetFeatureValue(CommonUsages.primary2DAxis, out var rightStick)) TurnFromStick(rightStick, playspace);
            }
            if (tool) { held = false; triggerFromTool = true; return; }
            if (!left.TryGetFeatureValue(CommonUsages.devicePosition, out var grip)
                || !left.TryGetFeatureValue(CommonUsages.deviceRotation, out var rotation)) { held = false; return; }
            // Thrust follows the wrist, not the controller: tilting the hand swings the controller around the wrist, and
            // measured at the controller that swing alone moved it past the deadzone, adding thrust nobody asked for.
            var position = grip + rotation * WristOffset;
            if (left.TryGetFeatureValue(PointerRotation, out var aim)) rotation = aim;
            left.TryGetFeatureValue(CommonUsages.trigger, out var trigger);
            handPosition = position;
            handRotation = rotation;

            if (trigger < TriggerPress) triggerFromTool = false;
            if (!Hold(trigger >= TriggerPress && !triggerFromTool, position, rotation)) return;
            var offset = position - heldPosition;
            var strength = Ramp(offset.magnitude, MoveDeadzone, FullOffset);
            if (strength > 0f) thrust = playspace * offset.normalized * strength;
            RollFromReference(rotation, playspace);
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

        // Rolls the body around its forward axis by the hand's roll away from the reference (around the controller's
        // forward axis). Unity's signs line up: hand tilted left rolls the body left.
        private static void RollFromReference(Quaternion rotation, Quaternion playspace)
        {
            var hand = (Quaternion.Inverse(heldRotation) * rotation).eulerAngles;
            var roll = Rate(hand.z);
            if (roll != 0f) turn = Quaternion.AngleAxis(roll, playspace * VrCamera.Instance.HeadRotation * Vector3.forward) * turn;
        }

        // Right stick left/right turns around the body's up axis (smoothly or in snap steps), up/down pitches around its
        // right axis (stick up looks up, like the mouse), smoothly turning faster the further the stick is pushed.
        private static void TurnFromStick(Vector2 stick, Quaternion playspace)
        {
            var body = playspace * VrCamera.Instance.HeadRotation;
            var snap = SnapAngle(Plugin.TurnMode.Value);
            var yaw = snap > 0f ? SnapTurn(stick.x, snap) : StickRate(stick.x);
            var pitch = -StickRate(stick.y);
            if (yaw != 0f) turn = Quaternion.AngleAxis(yaw, body * Vector3.up) * turn;
            if (pitch != 0f) turn = Quaternion.AngleAxis(pitch, body * Vector3.right) * turn;
        }

        private static float StickRate(float value) => Ramp(Mathf.Abs(value), StickDeadzone, 1f) * Mathf.Sign(value) * TurnSpeed * Time.deltaTime;

        // Degrees to turn this frame for a hand angle (0..360 from eulerAngles): none inside the deadzone, full speed from
        // TurnFull on.
        private static float Rate(float angle)
        {
            if (angle > 180f) angle -= 360f;
            return Ramp(Mathf.Abs(angle), TurnDeadzone, TurnFull) * Mathf.Sign(angle) * TurnSpeed * Time.deltaTime;
        }

        private static float SnapAngle(Turning mode) =>
            mode == Turning.Snap30 ? 30f : mode == Turning.Snap45 ? 45f : mode == Turning.Snap90 ? 90f : 0f;

        // Degrees for one fixed turn per stick flick; the stick has to come back before the next one.
        private static float SnapTurn(float x, float angle)
        {
            if (Mathf.Abs(x) < SnapRelease) { snapped = false; return 0f; }
            if (snapped || Mathf.Abs(x) < SnapPress) return 0f;
            snapped = true;
            return Mathf.Sign(x) * angle;
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

        private static bool GetRight()
        {
            if (!right.isValid) right = InputDevices.GetDeviceAtXRNode(XRNode.RightHand);
            return right.isValid;
        }

        private static float Ramp(float value, float start, float full) => Mathf.Clamp01((value - start) / (full - start));

        // Rotation to apply to the playspace (or the body) this frame, in world space. Called by VrBody once per body update.
        public static Quaternion TakeTurn()
        {
            Update();
            var result = turn;
            turn = Quaternion.identity;
            return result;
        }

        // The hand offset (meters) where thrust starts and where it is full; also used by the flight globe (VrFlightGlobe).
        // Clamped here as well, in case the config file was edited by hand.
        private static float MoveDeadzone => Mathf.Clamp(Plugin.ThrustDeadzone.Value, 0f, DeadzoneMax) / 100f;
        public static float FullOffset => Mathf.Clamp(Plugin.ThrustFull.Value, Mathf.Clamp(Plugin.ThrustDeadzone.Value, 0f, DeadzoneMax) + MinThrustRange, FullMax) / 100f;
        public static float DeadzoneShare => MoveDeadzone / FullOffset;

        // Keeps full thrust at least MinThrustRange beyond the deadzone: whichever value was just changed wins and pushes
        // the other one along.
        public static void KeepThrustRange(bool deadzoneChanged)
        {
            var deadzone = Plugin.ThrustDeadzone;
            var full = Plugin.ThrustFull;
            if (full.Value - deadzone.Value >= MinThrustRange - 0.001f) return;
            if (deadzoneChanged) full.Value = Mathf.Min(deadzone.Value + MinThrustRange, FullMax);
            else deadzone.Value = Mathf.Max(full.Value - MinThrustRange, 0f);
            VrOptions.Refresh(deadzoneChanged ? full : deadzone);
        }

        public static bool Flying => Active == Scheme.Vtol && GameSession.CurrentGameState == GameSession.GameState.Gameplay;

        public static bool Holding
        {
            get { Update(); return held; }
        }

        // While the trigger is held: the hand's offset from the reference in tracking space (meters), and its rotation
        // relative to the reference, in the controller's own frame (z is the roll the controls read).
        public static bool TryGetHand(out Vector3 offset, out Quaternion rotation)
        {
            Update();
            offset = held ? handPosition - heldPosition : Vector3.zero;
            rotation = held ? Quaternion.Inverse(heldRotation) * handRotation : Quaternion.identity;
            return held;
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
    // A postfix on LynxControlExtensions.GetInputIsPressed never ran: the method is small enough for Mono to inline it into
    // its callers, so the call in ThrustController.Update is redirected instead.
    [HarmonyPatch(typeof(ThrustController), "Update")]
    internal static class BrakePatch
    {
        private static readonly LynxControls.ActionSetAndId Left = GameplayActions.GameplayActionSet.ThrustBrakeLeft;
        private static readonly LynxControls.ActionSetAndId Right = GameplayActions.GameplayActionSet.ThrustBrakeRight;

        private static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> instructions)
        {
            var original = AccessTools.Method(typeof(LynxControlExtensions), nameof(LynxControlExtensions.GetInputIsPressed));
            var replacement = AccessTools.Method(typeof(BrakePatch), nameof(GetInputIsPressed));
            var replaced = 0;
            foreach (var instruction in instructions)
            {
                if (instruction.Calls(original))
                {
                    instruction.operand = replacement;
                    replaced++;
                }
                yield return instruction;
            }
            Plugin.Log.LogInfo($"ThrustController: {replaced} input call(s) redirected for braking");
        }

        private static bool GetInputIsPressed(LynxControls controls, LynxControls.ActionSetAndId actionSetAndId)
        {
            if (controls.GetInputIsPressed(actionSetAndId)) return true;
            return (actionSetAndId.Equals(Left) || actionSetAndId.Equals(Right)) && VrControls.IsBraking();
        }
    }
}
