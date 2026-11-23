using BBI.Unity.Game;
using HarmonyLib;
using UnityEngine;

namespace ShipbreakerVR
{
    // What the grapple holds follows the grapple in the hand, not the head, as Marc found it did. The game's rope
    // (LaserRope) holds a grappled part where the rope pointed when it caught, in the player's body: the target is the
    // body's attach point plus the rope direction (stored in the body's space) times the rope length, and the part is
    // turned to keep its attitude to the body. In VR the body follows the head (VrBody), so the part swung with the gaze.
    // Now the body's place and turn in those two calculations are taken from the hand that holds the grapple (its aim
    // camera, VrAim.CameraTransformFor), as if the body were fixed to that hand from the moment the rope caught: the part
    // stays where it was then, and moving or turning the hand carries it along. GetPositionForceInfo and
    // GetRotationForceInfo compute their springs from that target; the postfixes shift the target by the difference,
    // which, the springs being linear, is the same as computing them from the hand. Only for the grapple's own rope
    // (GrapplingHook.SetHookPoint), only while that hand aims, and only where the game pulls the part to the player; a
    // part too heavy to move still pulls the player as before.
    internal static class VrGrappleHold
    {
        static VrGrappleHold() => VrSession.OnUnload("grapple hold", Release);

        private static readonly AccessTools.FieldRef<LaserRope, float> MaxSpring = AccessTools.FieldRefAccess<LaserRope, float>("mMaxSpring");
        private static readonly AccessTools.FieldRef<LaserRope, bool> Reorient = AccessTools.FieldRefAccess<LaserRope, bool>("m_ReorientObject");
        private static readonly System.Func<LaserRope, float> SpringPercent =
            AccessTools.MethodDelegate<System.Func<LaserRope, float>>(AccessTools.PropertyGetter(typeof(LaserRope), "SpringPercent"));
        private static readonly System.Func<LaserRope, float> RotationSpringPercent =
            AccessTools.MethodDelegate<System.Func<LaserRope, float>>(AccessTools.PropertyGetter(typeof(LaserRope), "RotationSpringPercent"));
        private static readonly System.Func<LaserRope, ForceMode> RotationForceMode =
            AccessTools.MethodDelegate<System.Func<LaserRope, ForceMode>>(AccessTools.PropertyGetter(typeof(LaserRope), "RotationForceMode"));

        // The grapple's rope, and the body's pose in the space of the hand that holds the grapple, taken when the rope
        // caught or when that hand started to aim or changed.
        private static LaserRope rope;
        private static Transform hand;
        private static Vector3 offsetPosition;
        private static Quaternion offsetRotation;
        // The body's pose the rope last got from here, and when: where to carry on from when the aim changes.
        private static Vector3 lastPosition;
        private static Quaternion lastRotation = Quaternion.identity;
        private static float lastAt = -10f;
        private const float Recent = 0.5f;
        // An aim the rope is about to take over, since when, and how long it waits for it to be where it belongs.
        private static Transform changingTo;
        private static float changingSince;
        private const float Settle = 0.15f;
        private static int logs;

        // From GrapplingHook.SetHookPoint, when the rope has caught something.
        public static void Caught(LaserRope caught)
        {
            rope = caught;
            hand = null;
            lastAt = -10f;
            changingTo = null;
            if (TryGetHand(out var aim)) Attach(aim, caught.StartAnchorData.Anchor.transform.position, caught.StartAnchorData.Anchor.rotation);
        }

        // The body's pose as the rope should see it now: fixed to the hand that holds the grapple. False when the game's
        // own pose applies (another rope, not the player's end, no hand aiming).
        private static bool TryGetBody(LaserRope instance, LaserRope.AnchorData anchor, out Vector3 position, out Quaternion rotation)
        {
            position = default;
            rotation = default;
            if (instance != rope || !instance.IsInitialized || anchor.Anchor == null || anchor.Anchor != instance.StartAnchorData.Anchor) return false;
            if (!TryGetHand(out var aim))
            {
                hand = null;
                return false;
            }
            if (aim != hand)
            {
                // The rope caught before the hand aimed, or the grapple's aim is another one now (it went to the other
                // hand, or a second tool was drawn and the grapple aims through the second tool's camera): carry on
                // from the pose the rope last had from here, so the part does not jump. Not from the old aim as it is
                // now: once a second tool is drawn, the game's aim camera is with the new tool, in the other hand, and
                // the part jumped by the way between the hands (Raik's list of 2026-11-21, point 10). Without a recent
                // pose (the hand did not aim for a while), from where the body is.
                var body = anchor.Anchor.transform;
                if (hand != null && Time.unscaledTime - lastAt < Recent)
                {
                    // The new aim is not where it will be at once: as a second tool is put away the grapple becomes
                    // the game's tool again and aims through the game's aim camera, which is still as the other tool
                    // left it for a moment. Taken over at once, the part shifted by a few degrees when the camera came
                    // to the grapple's hand (Raik, 2026-11-22, and the log: drawing the cutter was fine, putting it
                    // away was not). So the rope keeps the pose it last had for a moment, and takes the new aim then.
                    if (aim != changingTo)
                    {
                        changingTo = aim;
                        changingSince = Time.unscaledTime;
                    }
                    if (Time.unscaledTime - changingSince < Settle)
                    {
                        position = lastPosition;
                        rotation = lastRotation;
                        lastAt = Time.unscaledTime;
                        return true;
                    }
                    Attach(aim, lastPosition, lastRotation);
                }
                else Attach(aim, body.position, body.rotation);
            }
            changingTo = null;
            rotation = aim.rotation * offsetRotation;
            position = aim.position + aim.rotation * offsetPosition;
            lastPosition = position;
            lastRotation = rotation;
            lastAt = Time.unscaledTime;
            return true;
        }

        private static bool TryGetHand(out Transform aim)
        {
            aim = VrAim.Active ? VrAim.CameraTransformFor((int)GameTools.Family.Grapple) : null;
            return aim != null && aim != LynxCameraController.MainCameraTransform;
        }

        private static void Attach(Transform aim, Vector3 bodyPosition, Quaternion bodyRotation)
        {
            hand = aim;
            var inverse = Quaternion.Inverse(aim.rotation);
            offsetPosition = inverse * (bodyPosition - aim.position);
            offsetRotation = inverse * bodyRotation;
            if (logs++ < 20) Plugin.Log.LogInfo($"Grapple hold: '{rope.EndAnchorData.Anchor?.name}' follows '{aim.name}' (rope {rope.TotalLength:0.0} m)");
        }

        // LaserRope.GetPositionForceInfo: where the part is pulled to, and the spring towards it.
        public static void ShiftPosition(LaserRope instance, LaserRope.AnchorData connected, float forceMod, ref Vector3 displacement, ref Vector3 force)
        {
            var none = instance.State == LaserRope.RopeState.None;
            if (!((connected.IsManipulator && (forceMod == 1f || none)) || (none && forceMod == 0f))) return;
            if (!TryGetBody(instance, connected, out var position, out var rotation)) return;
            var game = connected.WorldAttachPoint + connected.WorldRopeDirection * instance.TotalLength;
            var target = position + rotation * connected.LocalAttachPoint + rotation * connected.LocalRopeDirection * instance.TotalLength;
            var shift = target - game;
            displacement += shift;
            force += MaxSpring(instance) * SpringPercent(instance) * shift;
        }

        // LaserRope.GetRotationForceInfo: the attitude the part is turned to, and the spring towards it.
        public static void ShiftRotation(LaserRope instance, LaserRope.AnchorData forceAnchor, LaserRope.AnchorData connected, float forceMod, ref Vector3 desiredRotationDir, ref Vector3 force)
        {
            if (forceMod == 0f || forceAnchor.Anchor == connected.Anchor || !connected.IsManipulator) return;
            if (!TryGetBody(instance, connected, out _, out var rotation)) return;
            Vector3 game, wanted;
            if (Reorient(instance))
            {
                var body = connected.Anchor.transform;
                var normal = forceAnchor.WorldAttachNormal.normalized;
                var up = forceAnchor.WorldAttachUp.normalized;
                game = Vector3.Cross(body.forward.normalized, normal) + Vector3.Cross(-body.up, up);
                wanted = Vector3.Cross(rotation * Vector3.forward, normal) + Vector3.Cross(-(rotation * Vector3.up), up);
            }
            else
            {
                var forward = forceAnchor.WorldAnchorForward.normalized;
                var up = forceAnchor.WorldAnchorUp.normalized;
                game = Vector3.Cross(-connected.WorldAttachForward.normalized, forward) + Vector3.Cross(-connected.WorldAttachUp, up);
                wanted = Vector3.Cross(-(rotation * connected.LocalAttachForward).normalized, forward) + Vector3.Cross(-(rotation * connected.LocalAttachUp), up);
            }
            var shift = wanted - game;
            desiredRotationDir += shift;
            var extra = MaxSpring(instance) * RotationSpringPercent(instance) * shift;
            var mode = RotationForceMode(instance);
            if (mode == ForceMode.Force || mode == ForceMode.Impulse)
            {
                var body = forceAnchor.Anchor;
                var tensor = body.rotation * body.inertiaTensorRotation;
                extra = tensor * Vector3.Scale(body.inertiaTensor, Quaternion.Inverse(tensor) * extra);
            }
            force += extra;
        }

        private static void Release()
        {
            rope = null;
            hand = null;
            logs = 0;
        }
    }

    [HarmonyPatch(typeof(GrapplingHook), "SetHookPoint")]
    internal static class GrappleHookPointPatch
    {
        private static readonly AccessTools.FieldRef<GrapplingHook, LaserRope> Rope =
            AccessTools.FieldRefAccess<GrapplingHook, LaserRope>("m_GrapplingRope");

        private static void Postfix(GrapplingHook __instance)
        {
            var rope = Rope(__instance);
            if (rope != null && rope.IsInitialized) VrGrappleHold.Caught(rope);
        }
    }

    [HarmonyPatch(typeof(LaserRope), "GetPositionForceInfo")]
    internal static class GrappleHoldPositionPatch
    {
        private static void Postfix(LaserRope __instance, LaserRope.AnchorData connectedAnchor, float forceMod, ref Vector3 displacement, ref Vector3 force) =>
            VrGrappleHold.ShiftPosition(__instance, connectedAnchor, forceMod, ref displacement, ref force);
    }

    [HarmonyPatch(typeof(LaserRope), "GetRotationForceInfo")]
    internal static class GrappleHoldRotationPatch
    {
        private static void Postfix(LaserRope __instance, LaserRope.AnchorData forceAnchor, LaserRope.AnchorData connectedAnchor, float forceMod, ref Vector3 desiredRotationDir, ref Vector3 force) =>
            VrGrappleHold.ShiftRotation(__instance, forceAnchor, connectedAnchor, forceMod, ref desiredRotationDir, ref force);
    }
}
