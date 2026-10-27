using System.Linq;
using UnityEngine;

namespace ShipbreakerVR
{
    // The player model hangs on the game's body, and VrBody turns that body with the whole head (heading, pitch and roll)
    // around the eye: the shoulders swung around the eye with every look, about 20 cm below it, while the controllers
    // stayed put, so the arms' reach changed with the head (issue #37). Here, after the animation and before the arms are
    // bent (VrFrame), the model is turned back upright around its neck: it keeps only a heading of its own in tracking
    // space, which follows the head's heading with a dead zone (Follow), as a body stays put while the head looks around.
    // Only the model turns: the body, the camera and the game's aiming stay as they are. The hips are turned, the root of
    // the humanoid skeleton, which the animator sets again every frame; the neck is turned back, so the head stays at the
    // eye as before.
    internal static class VrTorso
    {
        static VrTorso() => VrSession.OnUnload("torso", Release);

        // How long the model takes to turn between the game's pose and the upright one.
        private const float Blend = 0.3f;
        // How far the head turns away from the body before it takes the body along, in degrees.
        public const float DeadZone = 40f;
        // Beyond this angle the body slowly turns after the head, so that it faces the same way again after a real turn.
        public const float FollowFrom = 10f;
        // How fast it turns after it: the share of the angle beyond FollowFrom per second (exponential).
        public const float CatchUp = 1.5f;

        private static Animator animator;
        private static Transform hips;
        private static Transform neck;
        private static bool found;
        private static float weight;
        private static float heading;
        private static bool headingKnown;
        private static bool applied;
        private static bool measured;
        // The hips' local pose before the turn and after it: if nothing set them since (an animator that did not update),
        // the turn starts from the old pose instead of adding up.
        private static Vector3 animatedPosition, setPosition;
        private static Quaternion animatedRotation, setRotation;
        private static bool set;

        // The turn applied to the model this frame (identity when off): from the game camera's rotation to the frame the
        // model's head now has. Code that holds a bone relative to the eye uses Frame instead of the camera's rotation.
        public static Quaternion Correction { get; private set; } = Quaternion.identity;

        public static Quaternion Frame(Quaternion cameraRotation) => Correction * cameraRotation;

        // From VrFrame, first of the steps after the animation.
        public static void LateUpdate()
        {
            Correction = Quaternion.identity;
            var vr = VrCamera.Instance;
            var camera = vr != null ? vr.GameCamera : null;
            var active = VrSettings.UprightBody.Value && VrAim.Active && camera != null && !vr.FollowsHab && Find();
            weight = Mathf.MoveTowards(weight, active ? 1f : 0f, Time.unscaledDeltaTime / Blend);
            if (active != applied)
            {
                applied = active;
                Plugin.Log.LogInfo($"Torso: {(active ? "upright, turned around the neck" : "back to the game")}");
            }
            if (vr == null || camera == null) return;

            if (HeadingOf(vr.HeadRotation, out var head))
            {
                heading = headingKnown ? Follow(heading, head, Time.unscaledDeltaTime) : head;
                headingKnown = true;
            }
            if (weight <= 0f || !found || hips == null || neck == null)
            {
                Restore();
                return;
            }

            var wanted = vr.Playspace * Quaternion.AngleAxis(heading, Vector3.up);
            var turn = Quaternion.Slerp(Quaternion.identity, wanted * Quaternion.Inverse(camera.transform.rotation), weight);
            if (!measured) Measure(camera.transform);
            Restore();
            animatedPosition = hips.localPosition;
            animatedRotation = hips.localRotation;
            var pivot = neck.position;
            var neckRotation = neck.rotation;
            hips.SetPositionAndRotation(pivot + turn * (hips.position - pivot), turn * hips.rotation);
            // The neck and the head on it stay as the game had them, at the eye: only the body below turns.
            neck.rotation = neckRotation;
            setPosition = hips.localPosition;
            setRotation = hips.localRotation;
            set = true;
            Correction = turn;
        }

        // The heading the body keeps, in degrees around the tracking space's up axis: within DeadZone of the head's, and
        // beyond FollowFrom slowly turning after it. Plain numbers, for the tests.
        public static float Follow(float body, float head, float deltaTime)
        {
            var diff = Wrap(head - body);
            var sign = diff < 0f ? -1f : 1f;
            var off = System.Math.Abs(diff);
            if (off > DeadZone)
            {
                body += sign * (off - DeadZone);
                off = DeadZone;
            }
            var beyond = off - FollowFrom;
            if (beyond > 0f) body += sign * beyond * (1f - (float)System.Math.Exp(-CatchUp * deltaTime));
            return Wrap(body);
        }

        // An angle in (-180, 180].
        public static float Wrap(float angle)
        {
            angle %= 360f;
            if (angle > 180f) angle -= 360f;
            else if (angle <= -180f) angle += 360f;
            return angle;
        }

        // The head's heading in tracking space; looking straight down or up from where the top of the head points
        // (forwards looking down, backwards looking up). False when neither says (lying on the side).
        private static bool HeadingOf(Quaternion head, out float angle)
        {
            angle = 0f;
            var forward = head * Vector3.forward;
            var flat = new Vector3(forward.x, 0f, forward.z);
            if (flat.sqrMagnitude < 0.09f)
            {
                var up = head * Vector3.up;
                if (forward.y > 0f) up = -up;
                flat = new Vector3(up.x, 0f, up.z);
            }
            if (flat.sqrMagnitude < 0.01f) return false;
            angle = Mathf.Atan2(flat.x, flat.z) * Mathf.Rad2Deg;
            return true;
        }

        // Once: where the neck and the shoulders sit relative to the eye, in the camera's axes (x right, y up, z forward).
        private static void Measure(Transform eye)
        {
            measured = true;
            var left = animator.GetBoneTransform(HumanBodyBones.LeftUpperArm);
            var right = animator.GetBoneTransform(HumanBodyBones.RightUpperArm);
            string At(Transform t) => t == null ? "none" : $"{eye.InverseTransformPoint(t.position)}";
            Plugin.Log.LogInfo($"Torso: from the eye, neck '{neck.name}' at {At(neck)}, shoulders at {At(left)} and {At(right)}, hips '{hips.name}' at {At(hips)} m");
        }

        // The hips back to their pose before the turn, if nothing has set them since.
        private static void Restore()
        {
            if (set && hips != null && hips.localPosition == setPosition && hips.localRotation == setRotation)
            {
                hips.localPosition = animatedPosition;
                hips.localRotation = animatedRotation;
            }
            set = false;
        }

        private static bool Find()
        {
            if (found && animator != null) return true;
            found = false;
            var grab = GameRefs.Grab;
            if (grab == null) return false;
            animator = grab.GetComponentsInChildren<Animator>(true).FirstOrDefault(a => a != null && a.isHuman);
            if (animator == null) return false;
            hips = animator.GetBoneTransform(HumanBodyBones.Hips);
            neck = animator.GetBoneTransform(HumanBodyBones.Neck) ?? animator.GetBoneTransform(HumanBodyBones.Head);
            found = true;
            Plugin.Log.LogInfo($"Torso: hips {(hips != null ? $"'{hips.name}'" : "missing")}, neck {(neck != null ? $"'{neck.name}'" : "missing")}");
            return true;
        }

        // On unload (hot reload). The animator sets the hips again on its next update.
        public static void Release()
        {
            Restore();
            animator = null;
            hips = neck = null;
            found = false;
            weight = 0f;
            headingKnown = false;
            applied = false;
            measured = false;
            Correction = Quaternion.identity;
        }
    }
}
