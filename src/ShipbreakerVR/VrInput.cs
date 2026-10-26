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
                return Get() && Device.TryGetFeatureValue(CommonUsages.devicePosition, out position);
            }

            // The grip pose in tracking space, read now.
            public bool TryGetGripPose(out Vector3 position, out Quaternion rotation)
            {
                rotation = Quaternion.identity;
                position = Vector3.zero;
                return Get() && Device.TryGetFeatureValue(CommonUsages.devicePosition, out position)
                    && Device.TryGetFeatureValue(CommonUsages.deviceRotation, out rotation);
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
                return pointer || TryGetGripPose(out position, out rotation);
            }

            // Where the controller points: the aim pose's rotation, or the grip pose's without one.
            public bool TryGetAimRotation(out Quaternion rotation) =>
                (Get() && Device.TryGetFeatureValue(PointerRotation, out rotation)) || TryGetGripPose(out _, out rotation);

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
                if (Valid)
                {
                    Device.TryGetFeatureValue(CommonUsages.trigger, out TriggerValue);
                    Device.TryGetFeatureValue(CommonUsages.grip, out GripValue);
                    Device.TryGetFeatureValue(CommonUsages.primary2DAxis, out Stick);
                    Device.TryGetFeatureValue(CommonUsages.primaryButton, out primary);
                    Device.TryGetFeatureValue(CommonUsages.secondaryButton, out secondary);
                    Device.TryGetFeatureValue(CommonUsages.primary2DAxisClick, out stickClick);
                }
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
