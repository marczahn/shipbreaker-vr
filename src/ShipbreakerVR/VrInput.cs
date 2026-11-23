using UnityEngine;
using UnityEngine.XR;

namespace ShipbreakerVR
{
    // The two motion controllers, read in one place. Once per frame (VrFrame, before the physics and the game's updates)
    // the buttons, triggers, grips and sticks are taken as a snapshot, with one hysteresis for the trigger and the grip
    // and press and release edges, so every module sees the same state for the whole frame: before, a grip resting
    // between 0.4 and 0.5 counted as let go for drawing tools and as held for climbing and the hands.
    // Poses are not part of the snapshot: the runtime updates them once more right before rendering, and the tool
    // models, the arms and the laser follow that latest pose. They are read live through the same devices
    // (TryGetGripPose, TryGetAimPose).
    internal static class VrInput
    {
        // Trigger and grip: pressed from PressAt, let go below ReleaseAt.
        public const float PressAt = 0.5f;
        public const float ReleaseAt = 0.4f;

        // The aim pose points where the controller points; the device (grip) pose runs along the handle, tilted upward.
        public static readonly InputFeatureUsage<Vector3> PointerPosition = new InputFeatureUsage<Vector3>("PointerPosition");
        public static readonly InputFeatureUsage<Quaternion> PointerRotation = new InputFeatureUsage<Quaternion>("PointerRotation");
        // The touch sensors of a controller (0.33.0; 'Oculus Touch Controller OpenXR' has them, not every controller or
        // runtime does): the index finger resting on the trigger, the thumb on A / X, on B / Y or on the stick. The
        // empty hand's fingers follow them (VrHands.Curl).
        public static readonly InputFeatureUsage<bool> TriggerTouch = new InputFeatureUsage<bool>("TriggerTouch");
        public static readonly InputFeatureUsage<bool> PrimaryTouch = new InputFeatureUsage<bool>("PrimaryTouch");
        public static readonly InputFeatureUsage<bool> SecondaryTouch = new InputFeatureUsage<bool>("SecondaryTouch");
        public static readonly InputFeatureUsage<bool> StickTouch = new InputFeatureUsage<bool>("Primary2DAxisTouch");
        // A trigger pulled or a stick moved this far has a finger on it, whatever its sensor tells.
        public const float FingerOnTrigger = 0.05f;
        private const float FingerOnStick = 0.1f;
        // A finger is off its sensor only after this long without a touch, in seconds: the sensors flicker.
        internal const float FingerOffAfter = 0.08f;
        // The grip's button closes the empty hand from SqueezeFrom (a finger resting on it) to SqueezeFull of its travel;
        // the trigger its index finger from FingerOnTrigger to PullFull.
        private const float SqueezeFrom = 0.03f;
        private const float SqueezeFull = 0.9f;
        private const float PullFull = 0.9f;

        // How far the grip's button closes the empty hand, and the trigger its index finger (0 to 1).
        internal static float Squeezed(float grip) => Mathf.InverseLerp(SqueezeFrom, SqueezeFull, grip);
        internal static float Pulled(float trigger) => Mathf.InverseLerp(FingerOnTrigger, PullFull, trigger);

        // The wrist relative to the grip pose (which runs along the handle): an estimate, about 8 cm behind the grip.
        public static readonly Vector3 WristBehindGrip = new Vector3(0f, 0f, -0.08f);

        public struct Button
        {
            public bool Down;
            // Went down or up since the previous frame.
            public bool Pressed;
            public bool Released;

            public void Set(bool down)
            {
                Pressed = down && !Down;
                Released = !down && Down;
                Down = down;
            }
        }

        public class Hand
        {
            public readonly XRNode Node;
            public readonly string Name;
            public InputDevice Device;
            // The controller is connected; and tracked, with a position.
            public bool Valid;
            public bool Tracked;
            public float TriggerValue;
            public float GripValue;
            public Vector2 Stick;
            public Button Trigger;
            public Button Grip;
            // A / X and B / Y.
            public Button Primary;
            public Button Secondary;
            public Button StickClick;
            // The fingers on the controller, by its touch sensors: the index finger on the trigger, the thumb on a
            // button or the stick. A sensor counts only once it has told of a touch (IndexSensed, ThumbSensed): a
            // controller or runtime that names a sensor but never sets it would show that finger off for good, and
            // without the sensors the fingers are on, as they were before 0.33.0.
            public bool IndexOn = true;
            public bool ThumbOn = true;
            public bool IndexSensed;
            public bool ThumbSensed;
            private float indexTouchedAt = -10f;
            private float thumbTouchedAt = -10f;

            // Raik (0.33.0): the index finger off the trigger is out straight. With the grip held on nothing it is stiff
            // (VrHands.Stiff): it pushes buttons and pushes the body off a wall.
            public bool IndexOut => Valid && !IndexOn;

            // Which fingers are on the controller at that time: by the sensors (touched), and by what only a finger on
            // it can do (used: the trigger pulled, a button pressed, the stick moved).
            internal void Fingers(float now, bool indexTouched, bool indexUsed, bool thumbTouched, bool thumbUsed)
            {
                if (indexTouched && !IndexSensed)
                {
                    IndexSensed = true;
                    Plugin.Log?.LogInfo($"Input: the {Name} controller's trigger tells where the index finger is");
                }
                if (thumbTouched && !ThumbSensed)
                {
                    ThumbSensed = true;
                    Plugin.Log?.LogInfo($"Input: the {Name} controller's buttons and stick tell where the thumb is");
                }
                if (indexTouched || indexUsed) indexTouchedAt = now;
                if (thumbTouched || thumbUsed) thumbTouchedAt = now;
                IndexOn = !IndexSensed || now - indexTouchedAt < FingerOffAfter;
                ThumbOn = !ThumbSensed || now - thumbTouchedAt < FingerOffAfter;
            }

            public Hand(XRNode node, string name)
            {
                Node = node;
                Name = name;
            }

            public bool IsLeft => Node == XRNode.LeftHand;

            public Hand Other => IsLeft ? Right : Left;

            // The device, looked up again while it is missing (switched off, not yet connected).
            public bool Get()
            {
                if (!Device.isValid) Device = InputDevices.GetDeviceAtXRNode(Node);
                return Device.isValid;
            }

            // The grip pose's position in tracking space, read now.
            public bool TryGetPosition(out Vector3 position)
            {
                position = Vector3.zero;
                if (!Get() || !Device.TryGetFeatureValue(CommonUsages.devicePosition, out position)) return false;
                Steady(ref position);
                return true;
            }

            // The grip pose in tracking space, read now.
            public bool TryGetGripPose(out Vector3 position, out Quaternion rotation)
            {
                rotation = Quaternion.identity;
                position = Vector3.zero;
                if (!Get() || !Device.TryGetFeatureValue(CommonUsages.devicePosition, out position)
                    || !Device.TryGetFeatureValue(CommonUsages.deviceRotation, out rotation)) return false;
                Steady(ref position, ref rotation);
                return true;
            }

            // The aim pose (or the grip pose without one) of a tracked controller in tracking space, read now.
            public bool TryGetAimPose(out Vector3 position, out Quaternion rotation, out bool pointer)
            {
                position = Vector3.zero;
                rotation = Quaternion.identity;
                pointer = false;
                if (!Get()) return false;
                if (Device.TryGetFeatureValue(CommonUsages.isTracked, out var tracked) && !tracked) return false;
                pointer = Device.TryGetFeatureValue(PointerPosition, out position) && Device.TryGetFeatureValue(PointerRotation, out rotation);
                if (pointer) Steady(ref position, ref rotation);
                else if (!TryGetGripPose(out position, out rotation)) return false;
                rotation *= Tilt;
                return true;
            }

            // Where the controller points: the aim pose's rotation, or the grip pose's without one.
            public bool TryGetAimRotation(out Quaternion rotation)
            {
                rotation = Quaternion.identity;
                if (Get() && Device.TryGetFeatureValue(PointerRotation, out rotation))
                {
                    var unused = Vector3.zero;
                    Steady(ref unused, ref rotation, false);
                }
                else if (!TryGetGripPose(out _, out rotation)) return false;
                rotation *= Tilt;
                return true;
            }

            // A hand that holds a demo charge or the charges' detonator is steadied (Raik, 2026-11-18: "smooth the
            // movement very slightly, it trembles a little too much with the hand's natural unrest"): the controller's
            // pose is filtered, the tremor within SteadyWithin and SteadyAngle taken out and the rest followed with a
            // short lag (as VrToolSteady does for the turn of the cutter and the grapple). Worked out once a frame from
            // the grip pose; every pose read in that frame (grip, aim) is moved rigidly by the same step, so hand,
            // charge and aim stay together.
            private const float SteadyWithin = 0.004f;
            private const float SteadyAngle = 1.2f;
            private const float SteadyLag = 0.05f;
            // Raik (2026-11-23, for the trailer's videos: "the hands tremble in front of the face, that can hardly be
            // avoided; a slight, really slight smoothing"): every controller's pose is smoothed a little
            // (Experimente/HandGlaettung, 1 by default, 0 for none): the tremor within SmoothWithin and SmoothAngle
            // times that setting is followed slowly, a real movement the faster the further the pose is behind
            // (VrToolSteady.Follow), so a hand moved at 1 m/s lags under a centimeter. The same filter and the same
            // rigid step for every pose read as for the charges, whose stronger steadying goes first.
            // NOTE (Raik): made for the videos; it may be taken out again before 0.33.0 is released. Ask him.
            private const float SmoothWithin = 0.002f;
            private const float SmoothAngle = 0.8f;
            private const float SmoothLag = 0.04f;
            private int steadyFrame = -1;
            private bool steadied;
            private Vector3 steadyRaw, steadyAt;
            private Quaternion steadyTurn = Quaternion.identity, steadyBy = Quaternion.identity;

            private void Steady(ref Vector3 position)
            {
                var none = Quaternion.identity;
                Steady(ref position, ref none);
            }

            private void Steady(ref Vector3 position, ref Quaternion rotation, bool place = true)
            {
                if (steadyFrame != Time.frameCount)
                {
                    steadyFrame = Time.frameCount;
                    var at = Vector3.zero;
                    var turn = Quaternion.identity;
                    var charge = VrXr.IsRunning && VrChargeByHand.Steadies(this);
                    var smooth = VrXr.IsRunning && VrSettings.HandSmoothing != null ? VrSettings.HandSmoothing.Value : 0f;
                    var wanted = (charge || smooth > 0f)
                        && Device.TryGetFeatureValue(CommonUsages.devicePosition, out at) && Device.TryGetFeatureValue(CommonUsages.deviceRotation, out turn);
                    if (!wanted) steadied = false;
                    else
                    {
                        if (!steadied)
                        {
                            steadyAt = at;
                            steadyTurn = turn;
                        }
                        else if (charge)
                        {
                            var follow = 1f - Mathf.Exp(-Time.unscaledDeltaTime / SteadyLag);
                            steadyAt = Vector3.Lerp(steadyAt, at, VrToolSteady.Share((at - steadyAt).magnitude, SteadyWithin) * follow);
                            steadyTurn = Quaternion.Slerp(steadyTurn, turn, VrToolSteady.Share(Quaternion.Angle(steadyTurn, turn), SteadyAngle) * follow);
                        }
                        else
                        {
                            var dt = Time.unscaledDeltaTime;
                            steadyAt = Vector3.Lerp(steadyAt, at, VrToolSteady.Follow((at - steadyAt).magnitude, SmoothWithin * smooth, SmoothLag, dt));
                            steadyTurn = Quaternion.Slerp(steadyTurn, turn, VrToolSteady.Follow(Quaternion.Angle(steadyTurn, turn), SmoothAngle * smooth, SmoothLag, dt));
                        }
                        steadied = true;
                        steadyRaw = at;
                        steadyBy = steadyTurn * Quaternion.Inverse(turn);
                    }
                }
                if (!steadied) return;
                if (place) position = steadyAt + steadyBy * (position - steadyRaw);
                rotation = steadyBy * rotation;
            }

            // Steuerung/HandNeigung: hands, tools and beams tipped forward (positive) or back against the controller.
            private static Quaternion Tilt => Quaternion.AngleAxis(VrSettings.HandTilt.Value, Vector3.right);

            public void Buzz(float amplitude, float seconds)
            {
                if (Device.isValid) Device.SendHapticImpulse(0, amplitude, seconds);
            }

            public void Read()
            {
                Valid = VrXr.IsRunning && Get();
                Tracked = Valid && (!Device.TryGetFeatureValue(CommonUsages.isTracked, out var tracked) || tracked)
                    && Device.TryGetFeatureValue(CommonUsages.devicePosition, out _);
                TriggerValue = GripValue = 0f;
                Stick = Vector2.zero;
                bool primary = false, secondary = false, stickClick = false;
                bool triggerTouch = false, primaryTouch = false, secondaryTouch = false, stickTouch = false;
                if (Valid)
                {
                    Device.TryGetFeatureValue(CommonUsages.trigger, out TriggerValue);
                    Device.TryGetFeatureValue(CommonUsages.grip, out GripValue);
                    Device.TryGetFeatureValue(CommonUsages.primary2DAxis, out Stick);
                    Device.TryGetFeatureValue(CommonUsages.primaryButton, out primary);
                    Device.TryGetFeatureValue(CommonUsages.secondaryButton, out secondary);
                    Device.TryGetFeatureValue(CommonUsages.primary2DAxisClick, out stickClick);
                    Device.TryGetFeatureValue(TriggerTouch, out triggerTouch);
                    Device.TryGetFeatureValue(PrimaryTouch, out primaryTouch);
                    Device.TryGetFeatureValue(SecondaryTouch, out secondaryTouch);
                    Device.TryGetFeatureValue(StickTouch, out stickTouch);
                }
                Fingers(Time.unscaledTime, triggerTouch, TriggerValue > FingerOnTrigger,
                    primaryTouch || secondaryTouch || stickTouch, primary || secondary || stickClick || Stick.magnitude > FingerOnStick);
                Trigger.Set(TriggerValue >= (Trigger.Down ? ReleaseAt : PressAt));
                Grip.Set(GripValue >= (Grip.Down ? ReleaseAt : PressAt));
                Primary.Set(primary);
                Secondary.Set(secondary);
                StickClick.Set(stickClick);
            }
        }

        public static readonly Hand Left = new Hand(XRNode.LeftHand, "left");
        public static readonly Hand Right = new Hand(XRNode.RightHand, "right");

        public static Hand Of(XRNode node) => node == XRNode.LeftHand ? Left : Right;

        // From VrFrame, once per frame. Without VR nothing is pressed.
        public static void Update()
        {
            Left.Read();
            Right.Read();
        }
    }
}
