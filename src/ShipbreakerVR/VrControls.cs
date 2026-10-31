using System.Collections.Generic;
using System.Reflection;
using System.Reflection.Emit;
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
    // it is pushed, and Y brakes. The grips stay free for holding on (VrClimb). While the left hand holds a tool its controller works
    // the tool (VrToolInput), so there is no flying and no braking then; the right stick still turns.
    // Thrust and brake go through the game's own actions (ThrustController reads them via LynxControls), so fuel, forces and
    // HUD stay the game's. Rotation is applied in VrBody, to the playspace.
    internal static class VrControls
    {
        public enum Scheme { Aus, Vtol }

        // Turning with the right stick: smoothly, or one fixed step per flick.
        public enum Turning { Fliessend, Snap30, Snap45, Snap90 }

        private const float SnapPress = 0.7f;
        private const float SnapRelease = 0.3f;
        // Hand offset for thrust, in centimeters (VrSettings.ThrustDeadzone, VrSettings.ThrustFull): full thrust needs at least
        // MinThrustRange more than the deadzone, so the ramp between them never shrinks to a switch.
        public const float DeadzoneMax = 10f;
        public const float FullMax = 30f;
        public const float MinThrustRange = 5f;
        // How far a tilted hand moves without meaning to, in meters per degree, and at most.
        private const float TiltShift = 0.0025f;
        private const float TiltShiftMax = 0.15f;
        private const float TurnDeadzone = 8f;
        // Rolling: as fast per degree as turning, up to a hand tilted all the way.
        private const float RollFull = 90f;
        private const float RollSpeed = 100f;
        private const float TurnSpeed = 45f;
        private const float StickDeadzone = 0.15f;

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

        private static Scheme Active => VrXr.IsRunning && VrCamera.Instance != null ? VrSettings.ControlScheme.Value : Scheme.Aus;

        // From VrFrame, once per frame, after VrToolInput (whether the left hand holds a tool).
        public static void Update()
        {
            if (updatedFrame == Time.frameCount) return;
            updatedFrame = Time.frameCount;
            thrust = Vector3.zero;
            turn = Quaternion.identity;
            braking = false;

            if (Active == Scheme.Aus) { held = false; return; }
            // Controller poses are in tracking space, i.e. relative to the rig whose rotation is the playspace.
            var playspace = VrCamera.Instance.Playspace;

            var left = VrInput.Left;
            if (!LeftConnected()) { held = false; return; }
            // Holding on with the left hand rests flying as a tool in it does (VrClimb).
            var tool = VrHand.Left.HoldsTool || VrHand.Left.State == HandState.Holding;
            // Outside gameplay the sticks and buttons work the menus as a gamepad; turning there would swing the menus.
            if (GameSession.CurrentGameState == GameSession.GameState.Gameplay)
            {
                if (!tool) braking = left.Secondary.Down;
                if (VrInput.Right.Valid) TurnFromStick(VrInput.Right.Stick, playspace);
            }
            if (tool) { held = false; triggerFromTool = true; return; }
            if (!left.TryGetGripPose(out var grip, out var rotation)) { held = false; return; }
            // Thrust follows the wrist, not the controller: tilting the hand swings the controller around the wrist, and
            // measured at the controller that swing alone moved it past the deadzone, adding thrust nobody asked for.
            var position = grip + rotation * VrInput.WristBehindGrip;
            left.TryGetAimRotation(out rotation);
            handPosition = position;
            handRotation = rotation;

            var trigger = left.Trigger.Down;
            if (!trigger) triggerFromTool = false;
            if (!Hold(trigger && !triggerFromTool, position, rotation)) return;
            var offset = position - heldPosition;
            // The wrist is an estimate and the arm moves along when the hand tilts, so a tilt still shifts it: as far as a tilt
            // can do that, the offset does not count as thrust. Moving the hand further still does.
            var moved = ThrustOffset(offset.magnitude, Quaternion.Angle(heldRotation, rotation));
            var strength = Ramp(moved, MoveDeadzone, FullOffset);
            if (strength > 0f) thrust = playspace * offset.normalized * strength;
            RollFromReference(rotation, playspace);
        }

        // The offset left for thrust after what a tilt of that many degrees moves the hand by itself, in meters.
        internal static float ThrustOffset(float offset, float tilt) => Mathf.Max(offset - Mathf.Min(tilt * TiltShift, TiltShiftMax), 0f);

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
            var snap = SnapAngle(VrSettings.TurnMode.Value);
            var yaw = snap > 0f ? SnapTurn(stick.x, snap) : StickRate(stick.x);
            var pitch = -StickRate(stick.y);
            if (yaw != 0f) turn = Quaternion.AngleAxis(yaw, body * Vector3.up) * turn;
            if (pitch != 0f) turn = Quaternion.AngleAxis(pitch, body * Vector3.right) * turn;
        }

        private static float StickRate(float value) => Ramp(Mathf.Abs(value), StickDeadzone, 1f) * Mathf.Sign(value) * TurnSpeed * Time.deltaTime;

        // Degrees to roll this frame for a hand angle (0..360 from eulerAngles): none inside the deadzone, full speed from
        // RollFull on.
        private static float Rate(float angle)
        {
            if (angle > 180f) angle -= 360f;
            return Ramp(Mathf.Abs(angle), TurnDeadzone, RollFull) * Mathf.Sign(angle) * RollSpeed * Time.deltaTime;
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

        private static bool LeftConnected()
        {
            var left = VrInput.Left.Device;
            if (!VrInput.Left.Valid)
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

        private static float Ramp(float value, float start, float full) => Mathf.Clamp01((value - start) / (full - start));

        // Rotation to apply to the playspace (or the body) this frame, in world space. Called by VrBody once per body update.
        public static Quaternion TakeTurn()
        {
            var result = turn;
            turn = Quaternion.identity;
            return result;
        }

        // The hand offset (meters) where thrust starts and where it is full; also used by the flight globe (VrFlightGlobe).
        // The settings' ranges hold even for a hand-edited config file (VrSettings); the gap between them is kept here too.
        private static float MoveDeadzone => VrSettings.ThrustDeadzone.Value / 100f;
        public static float FullOffset => Mathf.Max(VrSettings.ThrustFull.Value, VrSettings.ThrustDeadzone.Value + MinThrustRange) / 100f;
        public static float DeadzoneShare => MoveDeadzone / FullOffset;

        // Keeps full thrust at least MinThrustRange beyond the deadzone: whichever value was just changed wins and pushes
        // the other one along.
        public static void KeepThrustRange(bool deadzoneChanged)
        {
            var deadzone = VrSettings.ThrustDeadzone;
            var full = VrSettings.ThrustFull;
            if (full.Value - deadzone.Value >= MinThrustRange - 0.001f) return;
            if (deadzoneChanged) full.Value = Mathf.Min(deadzone.Value + MinThrustRange, FullMax);
            else deadzone.Value = Mathf.Max(full.Value - MinThrustRange, 0f);
        }

        public static bool Flying => Active == Scheme.Vtol && GameSession.CurrentGameState == GameSession.GameState.Gameplay;

        public static bool Holding => held;

        // While the trigger is held: the hand's offset from the reference in tracking space (meters), and its rotation
        // relative to the reference, in the controller's own frame (z is the roll the controls read).
        public static bool TryGetHand(out Vector3 offset, out Quaternion rotation)
        {
            offset = held ? handPosition - heldPosition : Vector3.zero;
            rotation = held ? Quaternion.Inverse(heldRotation) * handRotation : Quaternion.identity;
            return held;
        }

        public static void SetThrustFrame(Transform frame) => thrustFrame = frame;

        public static bool IsBraking() => braking;

        // Thrust along the game's thrust axes: x = right, y = up, z = forward of the thrust frame.
        public static bool TryGetThrust(out Vector3 local)
        {
            local = Vector3.zero;
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

        private static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> instructions, MethodBase original)
        {
            var target = AccessTools.Method(typeof(LynxControlExtensions), nameof(LynxControlExtensions.GetInputIsPressed));
            var replacement = AccessTools.Method(typeof(BrakePatch), nameof(GetInputIsPressed));
            var replaced = 0;
            foreach (var instruction in instructions)
            {
                if (instruction.Calls(target))
                {
                    instruction.operand = replacement;
                    replaced++;
                }
                yield return instruction;
            }
            PatchReport.Count("Braking", original, replaced, "input call(s)");
        }

        private static bool GetInputIsPressed(LynxControls controls, LynxControls.ActionSetAndId actionSetAndId)
        {
            if (controls.GetInputIsPressed(actionSetAndId)) return true;
            return (actionSetAndId.Equals(Left) || actionSetAndId.Equals(Right)) && VrControls.IsBraking();
        }
    }

    // Keeps thrust and brake working while the grapple holds something heavy. The game skips both then
    // (ThrustController.Update checks GrapplingHook.IsSwinging) and lets the player swing around the anchor with the swing
    // axes instead, which the controller does not feed: moving on you drifted on without braking, standing still you could
    // not get going. While flying with the controller the check is answered with false; the rope keeps pulling.
    [HarmonyPatch(typeof(ThrustController), "Update")]
    internal static class GrappleThrustPatch
    {
        private static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> instructions, MethodBase original)
        {
            var target = AccessTools.PropertyGetter(typeof(GrapplingHook), nameof(GrapplingHook.IsSwinging));
            var replacement = AccessTools.Method(typeof(GrappleThrustPatch), nameof(IsSwinging));
            var replaced = 0;
            foreach (var instruction in instructions)
            {
                if (instruction.Calls(target))
                {
                    instruction.opcode = OpCodes.Call;
                    instruction.operand = replacement;
                    replaced++;
                }
                yield return instruction;
            }
            PatchReport.Count("Grapple swing", original, replaced, "grapple swing check(s)");
        }

        private static bool IsSwinging(GrapplingHook hook) => !VrControls.Flying && hook.IsSwinging;
    }

    // Snaps the rope when the player flies further from something heavy on the grapple than the grapple reaches, since
    // GrappleThrustPatch lets them fly against the rope. The game's own snap (TriggerRopeBreak with OutOfBounds, as when a
    // grappled object leaves the view too long) plays the break effects and sound.
    [HarmonyPatch(typeof(GrapplingHook), "Update")]
    internal static class GrappleSnapPatch
    {
        private static readonly AccessTools.FieldRef<GrapplingHook, LaserRope> Rope =
            AccessTools.FieldRefAccess<GrapplingHook, LaserRope>("m_GrapplingRope");
        private static readonly MethodInfo Break = AccessTools.Method(typeof(GrapplingHook), "TriggerRopeBreak");

        private static void Postfix(GrapplingHook __instance)
        {
            if (!VrControls.Flying || !__instance.IsGrapplingHeavy) return;
            var rope = Rope(__instance);
            if (rope == null || !rope.IsInitialized) return;
            var distance = Vector3.Distance(rope.StartPoint, rope.EndPoint);
            if (distance <= __instance.MaxDistance) return;
            Plugin.Log.LogInfo($"Grapple: rope snapped at {distance:0.0} m (grapple reach {__instance.MaxDistance:0.0} m)");
            Break.Invoke(__instance, new object[] { LaserRope.RopeReleaseType.OutOfBounds, rope.VisualEndPoint });
        }
    }
}
