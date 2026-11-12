using BBI.Unity.Game;
using HarmonyLib;
using UnityEngine;

namespace ShipbreakerVR
{
    // Sound through the suit (Raik, 2026-11-11): what the player touches, the game lets be heard through the suit, also
    // where there is no air (AudioTouchTransferService: a touched part, its group and its body get a sound parameter set
    // to full). The game learns of a touch from its own hand's grab (GrabEvent), which the VR hands never send: whatever
    // a VR hand holds on to (VrClimb: a wall, the hull, a switch, a lever, a part, a thing to pull off) is told to it here
    // as the game's grab would tell it, and untold when the hand lets go or holds something else. So is a button the hand
    // pushes in (VrButtons, the air conditioner's), while it is pushed.
    internal static class VrTouchSound
    {
        private class Touch
        {
            public VrInput.Hand Input;
            public StructurePart Part;
            public GameObject On;
        }

        private static readonly Touch Left = new Touch { Input = VrInput.Left };
        private static readonly Touch Right = new Touch { Input = VrInput.Right };
        private static readonly System.Reflection.FieldInfo Service = AccessTools.Field(typeof(AudioTouchTransferService), "Instance");
        private static readonly System.Reflection.MethodInfo UpdateTouch = AccessTools.Method(typeof(AudioTouchTransferService), "UpdateTouch");
        private static readonly System.Type TouchType = AccessTools.Inner(typeof(AudioTouchTransferService), "TouchType");
        private static bool failed;
        private static int logs;

        // From VrFrame once the rig is placed, after the hands took hold or let go.
        public static void Update()
        {
            var on = VrXr.IsRunning && VrSettings.Climbing != null && VrSettings.Climbing.Value;
            Step(Left, on);
            Step(Right, on);
        }

        private static void Step(Touch touch, bool on)
        {
            StructurePart part = null;
            GameObject body = null;
            if (on)
            {
                var collider = VrClimb.HeldCollider(touch.Input);
                part = collider != null ? collider.GetComponentInParent<StructurePart>() : null;
                var rigidbody = collider != null ? collider.attachedRigidbody : null;
                body = rigidbody != null ? rigidbody.gameObject : null;
                if (collider == null && VrButtons.TryGetPushed(touch.Input, out var button))
                {
                    part = button.GetComponentInParent<StructurePart>();
                    rigidbody = button.GetComponentInParent<Rigidbody>();
                    body = rigidbody != null ? rigidbody.gameObject : null;
                }
                if (part == null || body == null) part = null;
            }
            if (part == touch.Part && body == touch.On) return;
            if (touch.Part != null) Tell(touch.Part, touch.On, false);
            touch.Part = null;
            touch.On = null;
            if (part == null || !Tell(part, body, true)) return;
            touch.Part = part;
            touch.On = body;
            if (logs++ < 40) Plugin.Log.LogInfo($"Touch sound: {touch.Input.Name} hand touches '{part.name}' on '{body.name}', heard through the suit");
        }

        // A touched part that left its body for one of its own (pulled off the wall, VrPullOff): told at once, before
        // sounds play on it, not only next frame.
        public static void Moved(StructurePart part, GameObject body)
        {
            if (part == null || body == null) return;
            foreach (var touch in new[] { Left, Right })
            {
                if (touch.Part != part || touch.On == body) continue;
                Tell(touch.Part, touch.On, false);
                touch.Part = null;
                touch.On = null;
                if (!Tell(part, body, true)) continue;
                touch.Part = part;
                touch.On = body;
                if (logs++ < 40) Plugin.Log.LogInfo($"Touch sound: '{part.name}' is a body of its own now, still touched by the {touch.Input.Name} hand");
            }
        }

        // Told to the game as its hand's grab would tell it (AudioTouchTransferService.UpdateTouch, a grab).
        private static bool Tell(StructurePart part, GameObject body, bool add)
        {
            if (failed || part == null || body == null) return false;
            try
            {
                var service = Service != null ? Service.GetValue(null) : null;
                if (service == null || UpdateTouch == null || TouchType == null) return false;
                UpdateTouch.Invoke(service, new object[] { part, body, System.Enum.ToObject(TouchType, 1), add });
                return true;
            }
            catch (System.Exception e)
            {
                failed = true;
                Plugin.Log.LogWarning($"Touch sound: telling the game of the touch failed, sounds stay as they are: {e.Message}");
                return false;
            }
        }
    }
}
