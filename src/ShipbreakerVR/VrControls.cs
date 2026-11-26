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
    // With the demo charges or their detonator in the left hand Y brakes as with an empty hand (Raik, 2026-11-18): the
    // two have places of their own on the body now, so the button no longer changes between them (VrToolInput), and a
    // charge held as a thing of its own has no use for it either.
    // Shooter, the other scheme (Steuerung/Controllersteuerung), as in VR shooters (Raik): the left stick thrusts forward,
    // back, left and right, as strongly as it is pushed, along the left controller: forward is where it points, so tilted
    // up or down it thrusts up or down. With the left trigger held, the stick's left and right roll instead of moving
    // sideways (Raik: the trigger is free there); forward and back still thrust. Turning, pitching, braking and the rest
    // as with VTOL.
    // Thrust and brake go through the game's own actions (ThrustController reads them via LynxControls), so fuel, forces and
    // HUD stay the game's. Rotation is applied in VrBody, to the playspace.
    internal static class VrControls
    {
        public enum Scheme { Aus, Vtol, Shooter }

        // Turning with the right stick: smoothly, or one fixed step per flick.
        public enum Turning { Fliessend, Snap30, Snap45, Snap90 }

        // What the right stick turns left and right around: the head's up, or the body's (the playspace's).
        public enum TurnAxis { Kopf, Koerper }

        private const float SnapPress = 0.7f;
        private const float SnapRelease = 0.3f;
        // Hand offset for thrust, in centimeters (VrSettings.ThrustDeadzone, VrSettings.ThrustFull): full thrust needs at least
        // MinThrustRange more than the deadzone, so the ramp between them never shrinks to a switch.
        public const float DeadzoneMax = 10f;
        public const float FullMax = 30f;
        public const float MinThrustRange = 5f;
        // Where the hand turns when it tilts to roll, in the grip pose's axes (meters): measured from Raik's controller's
        // velocity and angular velocity while rolling (a local test log), about 4 cm below and 3 cm behind the grip. Thrust
        // is measured there, so a tilt alone hardly moves it. The estimate before, 8 cm straight behind the grip, lay some
        // 6 cm off that axis: a 30 degree roll swung it 3 cm, past the deadzone, and the flight globe showed thrust.
        private static readonly Vector3 ThrustAnchor = new Vector3(0.01f, -0.04f, -0.03f);
        // What a tilt still moves the hand by itself (the arm going along, other hands), in meters per degree, and at most;
        // was 2.5 mm and 15 cm with the old anchor, and swallowed real thrust while tilted.
        private const float TiltShift = 0.001f;
        private const float TiltShiftMax = 0.05f;
        private const float TurnDeadzone = 12f;
        // Rolling: as fast per degree as turning, up to a hand tilted all the way.
        private const float RollFull = 90f;
        private const float RollSpeed = 100f;
        // Smooth turning with the right stick, in degrees per second at the game's look sensitivity 1 (Steuerung in the
        // game's own options, LynxControlsUserOptions.SensitivityXAxis / YAxis, 0 to 2): it follows that setting, as Raik
        // asked; at his 1.9 twice the 45 it was before.
        private const float TurnSpeed = 47.5f;
        private const float LeastSensitivity = 0.2f;
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

        // The reference turns with the body (Raik, 2026-11-22: turning round physically with the trigger held, the hand
        // goes round with the body, away from a reference that stayed in the room, and that gave thrust and turned it).
        // There is no tracker on the body, so its turn is told from what the head and the left controller do together
        // since the trigger went down: each one's turn about the up axis, and of the two the smaller where they turn the
        // same way (CommonTurn). Looking round with a still hand, or turning the wrist with a still head, is no turn of
        // the body. By that angle the reference goes round the neck, below the tracked head, and the reference pose for
        // rolling turns with it; while the body turns it also moves along with the neck. It does not follow the head
        // otherwise: with a deadzone of a centimetre or two every sway of the head would be thrust.
        // Experimente/SchubReferenzDrehtMit switches it off (the reference stays in the room, as before 0.32.1).
        private static BepInEx.Configuration.ConfigEntry<bool> referenceTurns;
        // Turning faster than this (degrees per second), the body counts as turning: the reference moves with the neck.
        private const float TurningFrom = 8f;
        private static Vector3 reference;
        private static Quaternion referenceRotation = Quaternion.identity;
        private static Quaternion headWas = Quaternion.identity, handWas = Quaternion.identity;
        private static float headTwist, handTwist, headTurn, handTurn, bodyTurn, mostTurn;
        private static Vector3 neckWas;
        private static int turnLogs;

        // The turn about the up axis in a rotation (its twist), in degrees, -180 to 180.
        internal static float TwistAboutUp(Quaternion rotation) =>
            VrTorso.Wrap(2f * Mathf.Atan2(rotation.y, rotation.w) * Mathf.Rad2Deg);

        // What two turns have in common: nothing if they go opposite ways, else the smaller one.
        internal static float CommonTurn(float one, float other)
        {
            if (one * other <= 0f) return 0f;
            return Mathf.Abs(one) < Mathf.Abs(other) ? one : other;
        }

        // A point turned about the up axis through a pivot, by degrees (as Quaternion.AngleAxis about up turns it).
        internal static Vector3 TurnedAbout(Vector3 point, Vector3 pivot, float degrees)
        {
            var angle = degrees * Mathf.Deg2Rad;
            var sin = Mathf.Sin(angle);
            var cos = Mathf.Cos(angle);
            var x = point.x - pivot.x;
            var z = point.z - pivot.z;
            return new Vector3(pivot.x + x * cos + z * sin, point.y, pivot.z - x * sin + z * cos);
        }

        // The trigger just went down: the reference is where the hand is, and the turns are counted from here.
        private static void StartReference(Quaternion head, Vector3 neck)
        {
            reference = heldPosition;
            referenceRotation = heldRotation;
            headWas = Quaternion.Inverse(head);
            handWas = Quaternion.Inverse(heldRotation);
            headTwist = handTwist = headTurn = handTurn = bodyTurn = mostTurn = 0f;
            neckWas = neck;
        }

        // While the trigger is held: the reference goes round with the body.
        private static void TurnReference(Quaternion head, Vector3 neck, Quaternion hand)
        {
            // Counted on from frame to frame, so a turn of more than half round does not jump.
            var twist = TwistAboutUp(head * headWas);
            headTurn += VrTorso.Wrap(twist - headTwist);
            headTwist = twist;
            twist = TwistAboutUp(hand * handWas);
            handTurn += VrTorso.Wrap(twist - handTwist);
            handTwist = twist;
            var turned = CommonTurn(headTurn, handTurn);
            var step = turned - bodyTurn;
            if (step != 0f)
            {
                var from = reference;
                if (Mathf.Abs(step) > TurningFrom * Time.unscaledDeltaTime) from += neck - neckWas;
                reference = TurnedAbout(from, neck, step);
                referenceRotation = Quaternion.AngleAxis(turned, Vector3.up) * heldRotation;
                bodyTurn = turned;
                if (Mathf.Abs(turned) > Mathf.Abs(mostTurn)) mostTurn = turned;
            }
            neckWas = neck;
        }

        // The trigger let go: said in the log when the reference went a good way round, to check it in the headset.
        private static void EndReference()
        {
            if (Mathf.Abs(mostTurn) < 15f || turnLogs >= 30) return;
            turnLogs++;
            Plugin.Log.LogInfo($"Controls: with the thrust held the body turned up to {mostTurn:0}° and the reference went round with it (at the end: head {headTurn:0}°, left controller {handTurn:0}°, taken as the body's {bodyTurn:0}°)");
        }
        // The hand this frame, for the flight globe.
        private static Vector3 handPosition;
        private static Quaternion handRotation;

        private static bool snapped;
        private static bool braking;
        // The left stick thrusting (Shooter): counts as holding the thrust, as the trigger does with VTOL.
        private static bool stickThrust;
        // Shooter, for the flight globe: the thrust in tracking space (0..1) and the roll as a hand tilt in degrees.
        private static Vector3 shooterThrust;
        private static float shooterRoll;
        // Rolling this frame and the one before: +1 left, -1 right, 0 none (VrTutorialRoll).
        private static int rollWay, rolledBefore;
        // Read in ECS systems that may run before or after VrControls.Update in a frame: the latest two frames either way.
        public static int Rolling => rollWay;
        public static int RolledBefore => rolledBefore;
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
            stickThrust = false;
            shooterThrust = Vector3.zero;
            shooterRoll = 0f;
            rolledBefore = rollWay;
            rollWay = 0;

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
                var charges = (VrHand.Left.HoldsTool && VrHand.Left.Tool == EquipmentController.Equipment.DemoCharge) || VrChargeByHand.Holds(left);
                if (!tool || charges) braking = left.Secondary.Down && (Allowed(GameplayActions.GameplayActionSet.ThrustBrakeLeft) || Allowed(GameplayActions.GameplayActionSet.ThrustBrakeRight));
                if (VrInput.Right.Valid) TurnFromStick(VrInput.Right.Stick, playspace);
                turn = VrFlightComfort.AlignStep(playspace, Time.deltaTime) * turn;
            }
            if (tool) { held = false; triggerFromTool = true; return; }
            if (Active == Scheme.Shooter)
            {
                held = false;
                // Only in gameplay: in the Hab the left stick walks (VrHabWalk), in the menus it moves the selection.
                if (GameSession.CurrentGameState != GameSession.GameState.Gameplay) return;
                if (!left.Trigger.Down) triggerFromTool = false;
                var rolling = left.Trigger.Down && !triggerFromTool;
                var stick = left.Stick;
                if (rolling)
                {
                    // Shown in the globe as the hand tilt that rolls as fast with VTOL.
                    var share = Ramp(Mathf.Abs(stick.x), StickDeadzone, 1f);
                    if (share > 0f) shooterRoll = -Mathf.Sign(stick.x) * Mathf.Lerp(TurnDeadzone, RollFull, share);
                    var roll = RollAllowed(ShooterRoll(stick.x) * Time.deltaTime);
                    if (roll != 0f) turn = Quaternion.AngleAxis(roll, playspace * VrCamera.Instance.HeadRotation * Vector3.forward) * turn;
                    rollWay = roll > 0f ? 1 : roll < 0f ? -1 : 0;
                    stick.x = 0f;
                }
                if (!left.TryGetAimRotation(out var pointing)) return;
                var local = ShooterThrust(stick, pointing);
                stickThrust = local != Vector3.zero;
                shooterThrust = local;
                thrust = playspace * local;
                return;
            }
            if (!left.TryGetGripPose(out var grip, out var rotation)) { held = false; return; }
            // Thrust follows the point the hand turns about (ThrustAnchor), not the controller: tilting the hand swings the
            // controller around it, and measured at the controller that swing alone moved it past the deadzone, adding
            // thrust nobody asked for.
            var position = grip + rotation * ThrustAnchor;
            left.TryGetAimRotation(out rotation);
            handPosition = position;
            handRotation = rotation;

            var trigger = left.Trigger.Down;
            if (!trigger) triggerFromTool = false;
            var head = VrCamera.Instance.HeadRotation;
            var neck = VrCamera.Instance.transform.localPosition + head * VrTorso.NeckFromEye;
            var wasHeld = held;
            if (!Hold(trigger && !triggerFromTool, position, rotation))
            {
                if (held) StartReference(head, neck);
                else if (wasHeld) EndReference();
                return;
            }
            if (referenceTurns == null && VrSettings.File != null)
                referenceTurns = VrSettings.Bind("Experimente", "SchubReferenzDrehtMit", true,
                    "Experiment: with VTOL, the point the thrust is measured from turns with your body when you turn round physically while holding the trigger (told from head and left controller turning together). Off: it stays where it was in the room.");
            if (referenceTurns != null && referenceTurns.Value) TurnReference(head, neck, rotation);
            var offset = position - reference;
            // The wrist is an estimate and the arm moves along when the hand tilts, so a tilt still shifts it: as far as a tilt
            // can do that, the offset does not count as thrust. Moving the hand further still does.
            var moved = ThrustOffset(offset.magnitude, Quaternion.Angle(referenceRotation, rotation));
            var strength = Ramp(moved, MoveDeadzone, FullOffset);
            if (strength > 0f) thrust = playspace * offset.normalized * strength;
            RollFromReference(rotation, playspace);
        }

        // Shooter: the thrust in tracking space for the left stick and the left controller's aim, length 0..1: none inside
        // the stick's deadzone, full at the stick's edge; stick up is the controller's forward, right its right.
        internal static Vector3 ShooterThrust(Vector2 stick, Quaternion aim)
        {
            var strength = Ramp(stick.magnitude, StickDeadzone, 1f);
            if (strength <= 0f) return Vector3.zero;
            var way = aim * new Vector3(stick.x, 0f, stick.y);
            return way.sqrMagnitude < 1e-8f ? Vector3.zero : way.normalized * strength;
        }

        // Shooter with the left trigger held: degrees per second to roll for the stick's left and right, as fast as VTOL's
        // roll at full; stick left rolls left, as a hand tilted left does with VTOL.
        internal static float ShooterRoll(float x) => -Ramp(Mathf.Abs(x), StickDeadzone, 1f) * Mathf.Sign(x) * RollSpeed;

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
            var hand = (Quaternion.Inverse(referenceRotation) * rotation).eulerAngles;
            var roll = RollAllowed(Rate(hand.z));
            rollWay = roll > 0f ? 1 : roll < 0f ? -1 : 0;
            if (roll != 0f) turn = Quaternion.AngleAxis(roll, playspace * VrCamera.Instance.HeadRotation * Vector3.forward) * turn;
        }

        // Right stick left/right turns around the body's up axis (smoothly or in snap steps), up/down pitches around its
        // right axis (stick up looks up, like the mouse), smoothly turning faster the further the stick is pushed.
        private static void TurnFromStick(Vector2 stick, Quaternion playspace)
        {
            var body = playspace * VrCamera.Instance.HeadRotation;
            var snap = SnapAngle(VrSettings.TurnMode.Value);
            var yaw = snap > 0f ? SnapTurn(stick.x, snap) : StickRate(stick.x, LynxControlsUserOptions.SensitivityXAxis);
            var pitch = -StickRate(stick.y, LynxControlsUserOptions.SensitivityYAxis);
            var up = VrSettings.TurnAround.Value == TurnAxis.Koerper ? playspace * Vector3.up : body * Vector3.up;
            if (yaw != 0f) turn = Quaternion.AngleAxis(yaw, up) * turn;
            if (pitch != 0f) turn = Quaternion.AngleAxis(pitch, body * Vector3.right) * turn;
        }

        private static float StickRate(float value, float sensitivity) =>
            Ramp(Mathf.Abs(value), StickDeadzone, 1f) * Mathf.Sign(value) * TurnSpeed * Mathf.Max(sensitivity, LeastSensitivity) * Time.deltaTime;

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

        public static bool Flying => Active != Scheme.Aus && GameSession.CurrentGameState == GameSession.GameState.Gameplay;

        // Holding the thrust: the trigger with VTOL, the left stick pushed with Shooter.
        public static bool Holding => held || stickThrust;

        // While the trigger is held: the hand's offset from the reference in tracking space (meters), and its rotation
        // relative to the reference, in the controller's own frame (z is the roll the controls read). With Shooter, while
        // the stick thrusts or rolls, the hand offset and tilt that would do the same with VTOL, so the flight globe shows
        // which way and how strongly (Raik).
        public static bool TryGetHand(out Vector3 offset, out Quaternion rotation)
        {
            if (!held && (shooterThrust != Vector3.zero || shooterRoll != 0f))
            {
                offset = shooterThrust == Vector3.zero ? Vector3.zero
                    : shooterThrust.normalized * Mathf.Lerp(DeadzoneShare, 1f, shooterThrust.magnitude) * FullOffset;
                rotation = Quaternion.AngleAxis(shooterRoll, Vector3.forward);
                return true;
            }
            offset = held ? handPosition - reference : Vector3.zero;
            rotation = held ? Quaternion.Inverse(referenceRotation) * handRotation : Quaternion.identity;
            return held;
        }

        public static void SetThrustFrame(Transform frame) => thrustFrame = frame;

        public static bool IsBraking() => braking;

        // Thrust along the game's thrust axes: x = right, y = up, z = forward of the thrust frame.
        // True while the controllers give thrust this frame (VTOL or Shooter).
        public static bool Thrusting => thrust != Vector3.zero;

        public static bool TryGetThrust(out Vector3 local)
        {
            local = Vector3.zero;
            if (thrust == Vector3.zero || thrustFrame == null) return false;
            local = thrustFrame.InverseTransformDirection(thrust);
            // What the game holds back now goes nowhere (the tutorial hands the directions out one by one).
            if (local.x > 0f && !Allowed(GameplayActions.GameplayActionSet.ThrustMoveRight, GameplayActions.GameplayActionSet.ThrustMoveLeftRightComposite)
                || local.x < 0f && !Allowed(GameplayActions.GameplayActionSet.ThrustMoveLeft, GameplayActions.GameplayActionSet.ThrustMoveLeftRightComposite)) local.x = 0f;
            if (local.y > 0f && !Allowed(GameplayActions.GameplayActionSet.ThrustMoveUp, GameplayActions.GameplayActionSet.ThrustMoveUpDownComposite)
                || local.y < 0f && !Allowed(GameplayActions.GameplayActionSet.ThrustMoveDown, GameplayActions.GameplayActionSet.ThrustMoveUpDownComposite)) local.y = 0f;
            if (local.z > 0f && !Allowed(GameplayActions.GameplayActionSet.ThrustMoveForward, GameplayActions.GameplayActionSet.ThrustMoveBackForwardComposite)
                || local.z < 0f && !Allowed(GameplayActions.GameplayActionSet.ThrustMoveBackward, GameplayActions.GameplayActionSet.ThrustMoveBackForwardComposite)) local.z = 0f;
            return local != Vector3.zero;
        }

        // Raik (2026-11-26): the tutorial hands out thrust, rolling and braking step by step, by blocking the game's
        // actions (ActionBlockerService); the controllers fly past the game's input, so they ask the same: a blocked
        // direction gives no thrust, a blocked roll no roll, a blocked brake no braking.
        internal static bool Allowed(GameplayActions.GameplayActionSet action, GameplayActions.GameplayActionSet? also = null)
        {
            var blocker = ActionBlockerService.Instance;
            if (blocker == null) return true;
            var blocked = blocker.IsActionBlocked(LynxControls.ActionSetAndId.FromEnum(action))
                || also.HasValue && blocker.IsActionBlocked(LynxControls.ActionSetAndId.FromEnum(also.Value));
            if (blocked && blockedLogs++ < 20) Plugin.Log.LogInfo($"Controls: the game holds {action} back now, the controllers leave it out");
            return !blocked;
        }

        private static int blockedLogs;

        // A roll this frame (degrees, + left), none in a way the game holds back.
        private static float RollAllowed(float roll) =>
            roll > 0f && !Allowed(GameplayActions.GameplayActionSet.RollBodyLeft) || roll < 0f && !Allowed(GameplayActions.GameplayActionSet.RollBodyRight) ? 0f : roll;
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
