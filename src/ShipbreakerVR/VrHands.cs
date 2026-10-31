using System.Collections.Generic;
using System.Linq;
using BBI.Unity.Game;
using UnityEngine;
using UnityEngine.XR;

namespace ShipbreakerVR
{
    // The game's two hands follow the controllers while the tools aim from the hand (VrAim), as Marc asked: the player
    // model has both arms, and the game has a hand of its own for each (GrabController.LeftHand/RightHand, with their own
    // targets, grab animations and grab actions). The arms come from the animation alone (the game has an IK component,
    // BBI.IKLimb, but not on the player). After the animation, before skinning (VrCamera.LateUpdate), each arm is bent
    // until the wrist sits at its controller, and the hand is turned so that its knuckles point where the controller
    // points, thumb side up. The tools are placed in the hand that holds them (VrToolModels, VrToolFit). The game itself
    // keeps the tool on its right hand; with the tool in the left hand the right one follows its controller empty.
    // A hand that is busy with something of the game's (grabbing, a gesture, damage) is left to the game. Demo charges
    // hang on the right hand's bone, so that arm is posed for them by VrToolModels.PoseArm. With the charge in the left
    // hand, the left arm follows its controller like any other, the right one its own, and the charge's pieces go over to
    // the left hand (Charges).
    // The fingers come from the animation too: a hand that follows its controller closes while its grip is held (around
    // the tool in the left hand, or into a fist), through the animator's own grab pose (Fingers).
    internal static class VrHands
    {
        static VrHands() => VrSession.OnUnload("hands", Release);

        // How long an arm takes to move between the game's pose and the controller.
        private const float Blend = 0.2f;
        // How fast a hand that let go of a hold returns to its controller, in m/s at world scale 1.
        private const float HoldReturn = 2f;
        // How thick a hand is against walls, in meters.
        private const float HandRadius = 0.04f;
        private static readonly RaycastHit[] WallHits = new RaycastHit[16];

        private class Arm
        {
            public readonly VrInput.Hand Input;
            public readonly string Name;
            public HandGrab Hand;
            public Transform Upper;
            public Transform Lower;
            public Transform Wrist;
            public Transform Index;
            public Transform Middle;
            public Transform Little;
            public float Weight;
            public bool Driven;
            // From the controller to where the game's hand sits: to its hold while it holds on (VrClimb), so the hand stays
            // there however far the real one has gone, then back to nothing.
            public Vector3 OffController;
            // The reach: measured once, then how often the wrist stayed short of the controller (Count).
            public bool Measured;
            public int Frames;
            public int Short;
            public float MostShort;
            public float CountSince;
            // The animator's grab parameters of this hand, as HandGrab sets them.
            public readonly string Prep;
            public readonly string Grab;
            public readonly string Cancel;

            public Arm(VrInput.Hand input, string side)
            {
                Input = input;
                Name = input.Name;
                Prep = side + "PrepGrab";
                Grab = side + "Grab";
                Cancel = side + "CancelGrab";
            }
        }

        private static readonly Arm LeftArm = new Arm(VrInput.Left, "L");
        private static readonly Arm RightArm = new Arm(VrInput.Right, "R");

        private static GrabController grab;
        private static Animator animator;
        private static int fingerLogs;
        private static int logs;

        // From VrCamera.LateUpdate, after VrToolModels.PoseArm: which arms follow their controllers, blended in and out
        // over Blend, and those posed.
        public static void LateUpdate()
        {
            if (!Find()) return;
            var leftWasDriven = LeftArm.Driven;
            var rightWasDriven = RightArm.Driven;
            var active = VrAim.Active;
            var equipment = grab.RightHand.CurrentEquipmentType;
            var chargeLeft = VrToolModels.ChargeInLeftHand;
            Drive(LeftArm, active && Idle(grab.LeftHand));
            var rightBusy = VrToolModels.PosesRightArm;
            Drive(RightArm, active && !rightBusy && (Idle(grab.RightHand)
                || (grab.RightHand.CurrentHandState == HandGrab.HandState.Equipped && (equipment != EquipmentController.Equipment.DemoCharge || chargeLeft))));
            // The demo charge's arm is bent there; another bend on top would fight it.
            if (rightBusy) RightArm.Weight = 0f;
            Fingers(LeftArm, leftWasDriven);
            Fingers(RightArm, rightWasDriven);
            Pose(LeftArm, active && chargeLeft && Mirrored(out var hold) ? hold : Quaternion.identity);
            Pose(RightArm, Quaternion.identity);
            Charges(active && chargeLeft);
        }

        // Closes the hand while its grip is held, as long as it follows its controller: the same parameters HandGrab sets
        // for grabbing and for holding a tool (PrepGrab trigger and Grab on; Grab off and CancelGrab trigger). Handed back
        // to the game, the hand gets the pose the game had set for it: closed on a tool or a grab, open otherwise. The game
        // sets these only when its hand changes state, so a hand taken over keeps what is set here until then.
        private static void Fingers(Arm arm, bool wasDriven)
        {
            bool closed;
            if (arm.Driven)
            {
                // The game's right hand with the charge in the left one: its fingers stay with the charge's animation, also
                // around a second tool (VrDualTools). The grab pose set here would take the arm's animator out of the
                // charge's states, and the charge vanished from the other hand.
                if (arm.Hand.CurrentEquipmentType == EquipmentController.Equipment.DemoCharge) return;
                closed = arm.Input.Grip.Down;
            }
            else if (wasDriven)
            {
                var state = arm.Hand.CurrentHandState;
                closed = state == HandGrab.HandState.Success
                    || (state == HandGrab.HandState.Equipped && arm.Hand.CurrentEquipmentType != EquipmentController.Equipment.DemoCharge);
            }
            else return;
            if (animator == null || animator.GetBool(arm.Grab) == closed) return;
            animator.ResetTrigger(closed ? arm.Cancel : arm.Prep);
            animator.SetTrigger(closed ? arm.Prep : arm.Cancel);
            animator.SetBool(arm.Grab, closed);
            if (fingerLogs++ < 20) Plugin.Log.LogInfo($"Hands: {arm.Name} hand {(closed ? "closes" : "opens")}{(arm.Driven ? "" : " (back to the game)")}");
        }

        // Not grabbing, gesturing, hurt or holding a tool.
        private static bool Idle(HandGrab hand)
        {
            var state = hand.CurrentHandState;
            return state == HandGrab.HandState.None || state == HandGrab.HandState.Resting || state == HandGrab.HandState.Leading;
        }

        private static void Drive(Arm arm, bool driven)
        {
            arm.Weight = Mathf.MoveTowards(arm.Weight, driven ? 1f : 0f, Time.unscaledDeltaTime / Blend);
            if (driven == arm.Driven) return;
            arm.Driven = driven;
            if (logs++ < 40)
                Plugin.Log.LogInfo($"Hands: {arm.Name} arm {(driven ? "follows the controller" : "back to the game")} (hand {arm.Hand.CurrentHandState}, {grab.RightHand.CurrentEquipmentType} on the right hand)");
        }

        // The wrist to the controller, the hand along it, turned against it by hold.
        private static void Pose(Arm arm, Quaternion hold)
        {
            var weight = arm.Weight;
            if (weight <= 0f || arm.Wrist == null || arm.Upper == null || arm.Lower == null) return;
            if (!arm.Input.TryGetPosition(out var gripLocal) || !arm.Input.TryGetAimRotation(out var aimLocal)) return;
            if (!VrToolModels.TrackingToWorld(gripLocal + aimLocal * VrInput.WristBehindGrip, aimLocal, out var wrist, out var aim)) return;
            if (VrClimb.TryGetHold(arm.Input, out var point) && VrToolModels.TrackingToWorld(gripLocal, aimLocal, out var grip, out _))
                arm.OffController = point - grip;
            else
                arm.OffController = Vector3.MoveTowards(arm.OffController, Vector3.zero, HoldReturn * VrCamera.WorldScale * Time.unscaledDeltaTime);
            wrist += arm.OffController;
            wrist = OutOfWalls(arm, wrist);

            var animated = arm.Wrist.rotation;
            var target = Vector3.Lerp(arm.Wrist.position, wrist, weight);
            if (!arm.Measured) Measure(arm, wrist);
            var shortBy = VrToolModels.Reach(arm.Upper.parent, arm.Upper, arm.Lower, arm.Wrist, target);
            Count(arm, weight >= 1f ? shortBy : 0f);
            arm.Wrist.rotation = Quaternion.Slerp(animated, HandRotation(arm, aim * hold, animated), weight);
        }

        // An empty hand stops at what is between the shoulder and the controller instead of going through it. Only the
        // model: nothing pushes against it. A tool stays with its controller, so the hand holding one does too.
        private static Vector3 OutOfWalls(Arm arm, Vector3 wrist)
        {
            var state = (arm == LeftArm ? VrHand.Left : VrHand.Right).State;
            if (state != HandState.Empty) return wrist;
            var shoulder = arm.Upper.position;
            var way = wrist - shoulder;
            var length = way.magnitude;
            if (length < 0.01f) return wrist;
            var direction = way / length;
            var count = Physics.SphereCastNonAlloc(shoulder, HandRadius, direction, WallHits, length, Physics.DefaultRaycastLayers, QueryTriggerInteraction.Ignore);
            var free = length;
            var body = arm.Wrist.root;
            for (var i = 0; i < count; i++)
            {
                var hit = WallHits[i];
                // Hits from where the cast starts inside something have no place on the way.
                if (hit.distance > 0f && hit.distance < free && !hit.collider.transform.IsChildOf(body)) free = hit.distance;
            }
            return shoulder + direction * free;
        }

        // Every this many seconds while an arm follows its controller, how often its wrist stayed short of it.
        private const float CountPeriod = 10f;
        // Shorter than this does not count (the solve leaves 1 mm).
        private const float ShortFrom = 0.01f;

        // Once per arm: its lengths and where the shoulder sits relative to the eye, in the camera's axes.
        private static void Measure(Arm arm, Vector3 wrist)
        {
            arm.Measured = true;
            var camera = VrCamera.Instance != null ? VrCamera.Instance.GameCamera : null;
            if (camera == null) return;
            var eye = camera.transform;
            var upper = Vector3.Distance(arm.Upper.position, arm.Lower.position);
            var lower = Vector3.Distance(arm.Lower.position, arm.Wrist.position);
            var collar = arm.Upper.parent;
            Plugin.Log.LogInfo($"Hands: {arm.Name} arm upper {upper:0.000} m, lower {lower:0.000} m, reach {upper + lower:0.000} m; "
                + $"shoulder at {eye.InverseTransformPoint(arm.Upper.position)} from the eye, collarbone '{(collar != null ? collar.name : "none")}' "
                + $"{(collar != null ? Vector3.Distance(collar.position, arm.Upper.position).ToString("0.000") : "-")} m; "
                + $"controller wrist {Vector3.Distance(arm.Upper.position, wrist):0.000} m from the shoulder (world scale {VrCamera.WorldScale:0.00})");
        }

        private static void Count(Arm arm, float shortBy)
        {
            arm.Frames++;
            if (shortBy > ShortFrom)
            {
                arm.Short++;
                arm.MostShort = Mathf.Max(arm.MostShort, shortBy);
            }
            if (Time.unscaledTime - arm.CountSince < CountPeriod) return;
            if (arm.CountSince > 0f && arm.Short > 0)
                Plugin.Log.LogInfo($"Hands: {arm.Name} hand short of the controller in {100f * arm.Short / arm.Frames:0}% of {arm.Frames} frames, by up to {arm.MostShort:0.000} m (torso {(VrTorso.Correction == Quaternion.identity ? "as the game's" : "upright")})");
            arm.CountSince = Time.unscaledTime;
            arm.Frames = arm.Short = 0;
            arm.MostShort = 0f;
        }

        // With the charge in the left hand, that hand takes over how the animation turns the game's right hand, mirrored:
        // relative to the controller as the right hand is to the eye, as VrToolModels.PoseArm holds it in the right hand.
        // So the charge sits in the left hand as in the right one, and the hand moves with the charge's animations,
        // throwing included. Read before the right arm is posed, in the hand's own axes (Frame), and mirrored there.
        private static bool Mirrored(out Quaternion hold)
        {
            hold = Quaternion.identity;
            var camera = VrCamera.Instance != null ? VrCamera.Instance.GameCamera : null;
            if (camera == null || !Frame(RightArm, out _, out var right)) return false;
            var relative = Quaternion.Inverse(VrTorso.Frame(camera.transform.rotation)) * right;
            hold = new Quaternion(relative.x, -relative.y, -relative.z, relative.w);
            return true;
        }

        // The hand's knuckles (wrist to the middle finger) along the controller's aim, the thumb side (little finger to
        // index finger) up, as when holding a Touch controller: the palm then faces inwards on either hand. Without
        // finger bones the hand keeps the angle to the eye that the animation gave it.
        private static Quaternion HandRotation(Arm arm, Quaternion aim, Quaternion animated)
        {
            if (Frame(arm, out _, out var current))
            {
                var wanted = Quaternion.LookRotation(aim * Vector3.forward, aim * Vector3.up);
                return wanted * Quaternion.Inverse(current) * arm.Wrist.rotation;
            }
            var camera = VrCamera.Instance != null ? VrCamera.Instance.GameCamera : null;
            return camera == null ? animated : aim * (Quaternion.Inverse(VrTorso.Frame(camera.transform.rotation)) * animated);
        }

        // The hand's own axes: forward along the knuckles (wrist to middle finger), up towards the thumb side (little to
        // index finger), from the wrist. The same on either hand, so a pose relative to one hand mirrors onto the other by
        // negating x (the cross product of the two flips with the mirror).
        private static bool Frame(Arm arm, out Vector3 wrist, out Quaternion frame)
        {
            wrist = Vector3.zero;
            frame = Quaternion.identity;
            if (arm.Wrist == null || arm.Index == null || arm.Middle == null || arm.Little == null) return false;
            var knuckles = arm.Middle.position - arm.Wrist.position;
            var thumbSide = arm.Index.position - arm.Little.position;
            if (knuckles.sqrMagnitude <= 1e-8f || thumbSide.sqrMagnitude <= 1e-8f) return false;
            wrist = arm.Wrist.position;
            frame = Quaternion.LookRotation(knuckles, thumbSide);
            return true;
        }

        private struct Moved
        {
            public Vector3 Position;
            public Quaternion Rotation;
            public Vector3 SetPosition;
            public Quaternion SetRotation;
        }

        private static readonly List<Transform> chargePieces = new List<Transform>();
        private static readonly Dictionary<Transform, Moved> moved = new Dictionary<Transform, Moved>();
        private static Transform chargesOn;
        private static float nextChargeSearch;
        private static int chargeLogs;

        // The demo charge and the detonator (PRF_Demo_Charge, PRF_Tool_ExplosiveChargeDetonator) hang on the bone of the
        // game's right hand, which its animation holds them with. With the charge in the left hand they are moved to the
        // left hand every frame, after both arms are posed: mirrored, relative to the hands' own axes (Frame), so the left
        // hand holds them as the right one would. Each piece starts from the animation's pose, or the one it had before
        // being moved, if nothing has set it since; it gets that pose back when the charge leaves the left hand.
        private static void Charges(bool left)
        {
            if (!left || !Frame(RightArm, out var rightWrist, out var rightFrame) || !Frame(LeftArm, out var leftWrist, out var leftFrame))
            {
                if (moved.Count == 0) return;
                foreach (var entry in moved)
                    if (entry.Key != null) SetLocal(entry.Key, entry.Value.Position, entry.Value.Rotation);
                moved.Clear();
                return;
            }
            if (chargesOn != RightArm.Wrist || (chargePieces.Count == 0 && Time.unscaledTime >= nextChargeSearch))
            {
                chargesOn = RightArm.Wrist;
                nextChargeSearch = Time.unscaledTime + 2f;
                chargePieces.Clear();
                foreach (Transform child in chargesOn)
                    if (child.name.IndexOf("Charge", System.StringComparison.OrdinalIgnoreCase) >= 0) chargePieces.Add(child);
                Plugin.Log.LogInfo($"Hands: demo charge pieces on the right hand: {string.Join(", ", chargePieces.Select(p => $"'{p.name}'"))}");
            }
            var toRight = Quaternion.Inverse(rightFrame);
            foreach (var piece in chargePieces)
            {
                if (piece == null) continue;
                if (moved.TryGetValue(piece, out var last) && piece.localPosition == last.SetPosition && piece.localRotation == last.SetRotation)
                    SetLocal(piece, last.Position, last.Rotation);
                var original = new Moved { Position = piece.localPosition, Rotation = piece.localRotation };
                var position = toRight * (piece.position - rightWrist);
                var rotation = toRight * piece.rotation;
                // Mirrored in the hand's axes, and the piece turned over its own x so that the result is a rotation again.
                position.x = -position.x;
                rotation = new Quaternion(rotation.x, -rotation.y, -rotation.z, rotation.w);
                piece.SetPositionAndRotation(leftWrist + leftFrame * position, leftFrame * rotation);
                original.SetPosition = piece.localPosition;
                original.SetRotation = piece.localRotation;
                moved[piece] = original;
            }
            if (chargeLogs++ == 0)
                Plugin.Log.LogInfo($"Hands: demo charge pieces moved to the left hand ({chargePieces.Count})");
        }

        private static bool Find()
        {
            if (grab != null) return true;
            grab = GameRefs.Grab;
            if (grab == null) return false;
            animator = grab.GetComponentsInChildren<Animator>(true).FirstOrDefault(a => a != null && a.isHuman);
            Setup(LeftArm, grab.LeftHand, animator, HumanBodyBones.LeftUpperArm, HumanBodyBones.LeftLowerArm, HumanBodyBones.LeftHand,
                HumanBodyBones.LeftIndexProximal, HumanBodyBones.LeftMiddleProximal, HumanBodyBones.LeftLittleProximal);
            Setup(RightArm, grab.RightHand, animator, HumanBodyBones.RightUpperArm, HumanBodyBones.RightLowerArm, HumanBodyBones.RightHand,
                HumanBodyBones.RightIndexProximal, HumanBodyBones.RightMiddleProximal, HumanBodyBones.RightLittleProximal);
            foreach (var arm in new[] { LeftArm, RightArm })
                Plugin.Log.LogInfo($"Hands: {arm.Name} arm {Name(arm.Upper)} > {Name(arm.Lower)} > {Name(arm.Wrist)}, fingers {(arm.Index != null && arm.Middle != null && arm.Little != null ? "found" : "missing")}");
            return true;
        }

        private static void Setup(Arm arm, HandGrab hand, Animator animator,
            HumanBodyBones upper, HumanBodyBones lower, HumanBodyBones wrist, HumanBodyBones index, HumanBodyBones middle, HumanBodyBones little)
        {
            arm.Hand = hand;
            arm.Upper = animator != null ? animator.GetBoneTransform(upper) : null;
            arm.Lower = animator != null ? animator.GetBoneTransform(lower) : null;
            arm.Wrist = animator != null ? animator.GetBoneTransform(wrist) : null;
            arm.Index = animator != null ? animator.GetBoneTransform(index) : null;
            arm.Middle = animator != null ? animator.GetBoneTransform(middle) : null;
            arm.Little = animator != null ? animator.GetBoneTransform(little) : null;
            arm.Weight = 0f;
            arm.Driven = false;
            arm.Measured = false;
            arm.CountSince = 0f;
            arm.Frames = arm.Short = 0;
            arm.MostShort = 0f;
        }

        private static void SetLocal(Transform transform, Vector3 position, Quaternion rotation)
        {
            transform.localPosition = position;
            transform.localRotation = rotation;
        }

        private static string Name(Transform transform) => transform == null ? "none" : $"'{transform.name}'";

        // On unload (hot reload).
        public static void Release()
        {
            foreach (var arm in new[] { LeftArm, RightArm })
                if (animator != null && arm.Driven && arm.Hand != null && Idle(arm.Hand)) animator.SetBool(arm.Grab, false);
            animator = null;
            grab = null;
            LeftArm.Weight = RightArm.Weight = 0f;
            LeftArm.Driven = RightArm.Driven = false;
            LeftArm.OffController = RightArm.OffController = Vector3.zero;
            Charges(false);
            chargePieces.Clear();
            chargesOn = null;
        }
    }
}
