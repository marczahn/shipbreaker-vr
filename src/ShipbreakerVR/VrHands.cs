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
    // The fingers are bent here too: the animator's grab pose barely moves them, so a hand that follows its controller
    // curls its finger bones itself (Curl), around a tool or a small part or into a fist while its grip is held, and
    // straightens them flat on a wall, a console or a large part. The animator is only given the open pose then (Fingers).
    internal static class VrHands
    {
        static VrHands() => VrSession.OnUnload("hands", Release);

        // How long an arm takes to move between the game's pose and the controller.
        private const float Blend = 0.2f;
        // How fast a hand that let go of a hold returns to its controller, in m/s at world scale 1.
        private const float HoldReturn = 2f;
        // How thick a hand is against walls, in meters.
        private const float HandRadius = 0.04f;
        // How long a hand takes to settle on its hold, and to come back to its controller after.
        private const float GripTime = 0.1f;
        // A flat hand on its hold: the wrist this far behind the middle of the palm, and the palm this far off the surface,
        // in meters at world scale 1.
        private const float PalmLength = 0.08f;
        private const float PalmThickness = 0.025f;
        // How long the fingers take to close or open.
        private const float CurlTime = 0.12f;
        // The bend of each finger joint (base, middle, tip) in degrees, and the thumb's over the palm: into a fist, and
        // around a handle (a tool, a small part).
        // The fist is loose, a tunnel the thumb closes over the middle finger's tip (ThumbOver), not a tight one (Raik).
        private static readonly float[] FistBend = { 52f, 66f, 44f };
        private static readonly float[] GripBend = { 55f, 70f, 45f };
        private const float FistThumb = 35f;
        // On a fist the thumb's tip goes this far off the middle finger's last joint, on the outside of the curled
        // fingers (where the palm faces), in meters at world scale 1: it lies over it.
        private const float ThumbOver = 0.015f;
        private const float GripThumb = 25f;
        // A flat hand is not stiff: each finger joint (base, middle, tip) stays bent this much towards the palm, in
        // degrees. The thumb lies in the palm's plane with the fingers, each of its bones this much towards the palm, about
        // 2 to 3 mm over its length (Raik).
        private static readonly float[] FlatBend = { 4f, 6f, 4f };
        private const float ThumbIn = 3f;
        // Over an edge a flat hand's fingers curl around it: each joint whose next bone finds no surface under it bends
        // this much (base, middle, tip, in degrees), over EdgeTime. Under a bone means within EdgeProbe of it towards the
        // palm, in meters at world scale 1.
        private static readonly float[] EdgeBend = { 30f, 35f, 25f };
        private const float EdgeTime = 0.15f;
        private const float EdgeProbe = 0.035f;
        // The points of a flat hand kept out of the surface (OutOfSurface), in the hand's axes from the wrist, in meters
        // at world scale 1: along the knuckles, and to either side.
        private static readonly float[] HandAlong = { 0f, 0.08f, 0.17f };
        private const float HandAcross = 0.04f;
        // How far outside the surface a ray for that starts.
        private const float SurfaceProbe = 0.2f;
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
            // The finger joints, index to little, each from the base to the tip (Phalanges), and the thumb's.
            public readonly Transform[] Joints = new Transform[12];
            // The thumb: its three humanoid bones, then the rig's further ones down to the last with a child (Setup).
            public readonly Transform[] Thumb = new Transform[5];
            // What the animation gave each joint, and what Curl set: a joint the animation leaves alone starts from its
            // own pose again, instead of bending further every frame.
            public readonly Quaternion[] Base = new Quaternion[17];
            public readonly Quaternion[] Set = new Quaternion[17];
            // The thumb's angles to the palm's plane once logged for this flat hold.
            public bool ThumbLogged;
            // How far the fingers are closed (0 open, 1 the full bend), and towards what.
            public float Closed;
            public bool Fist;
            // The hand held on to the control the game still works (UsesHeldControl).
            public bool UsedControl;
            // How far the fingers are straightened on a large hold (0 as animated, 1 straight).
            public float Flat;
            // How far each finger joint is curled over an edge (0 to 1), as Joints.
            public readonly float[] Edge = new float[12];
            // The hand on its hold (Grip): fixed on the thing held, turned (GripTurn) and placed (GripPoint, GripOffset)
            // as it took hold, flat on large ones; blended in and out over GripTime.
            public bool Gripping;
            public Transform GripAnchor;
            public Quaternion GripTurn;
            // After the hold slid along its surface (VrClimb.Slide): the turn it had, blended over to the new one.
            public int GripSlides;
            // The surface's outward direction on the hold, in the anchor's axes (for turning a flat hand along).
            public Vector3 GripNormal;
            public Quaternion GripTurnBefore;
            public float GripTurnAt = -1f;
            public Vector3 GripPoint;
            public Vector3 GripOffset;
            public bool GripFlat;
            public float GripBlend;
            public float Weight;
            public bool Driven;
            // The arm's own bone lengths, shoulder to elbow and elbow to wrist, from the skin's bind pose (KeepLength);
            // 0 until searched, -1 when not found.
            public float UpperLength;
            public float LowerLength;
            public bool LengthLogged;
            // The arm's helper bones (twist, stretch and track bones the skin also hangs on) and their places on their
            // parents in the bind pose (KeepLength).
            public Transform[] Helpers = new Transform[0];
            public Vector3[] HelperPlaces = new Vector3[0];
            public bool[] HelperLogged = new bool[0];
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
        private static InteractionController interaction;
        private static float nextInteractionSearch;
        private static int fingerLogs;
        private static int thumbLogs;
        private static int logs;

        // From VrFrame, right after VrToolModels.Unstretch and before anything poses the arms: both arms back to their
        // own bone lengths. The game's animations move the elbow and the wrist bone out: drawing the cutter stretched
        // the right arm by 81 % for a moment, folded at the elbow, and an arm left longer stayed bent where the real one
        // was straight (Raik). The mod's own stretch (VrToolModels.Reach) comes on top afterwards, as before.
        public static void KeepLength()
        {
            if (!Find()) return;
            KeepLength(LeftArm);
            KeepLength(RightArm);
        }

        private static void KeepLength(Arm arm)
        {
            if (arm.Upper == null || arm.Lower == null || arm.Wrist == null) return;
            if (arm.UpperLength < 0f) return;
            if (arm.UpperLength == 0f && !BindLengths(arm))
            {
                // Searched once: without a skin to measure the arm stays as the game has it.
                arm.UpperLength = -1f;
                Plugin.Log.LogWarning($"Hands: {arm.Name} arm's own length not found, left as the game has it");
                return;
            }
            var upper = Fit(arm.Lower, arm.Upper, arm.UpperLength);
            var lower = Fit(arm.Wrist, arm.Lower, arm.LowerLength);
            // The helper bones too: the main bones at their length, a twist bone the animation moved out still dragged the
            // forearm's skin long, the elbow out (Raik, after drawing the cutter).
            for (var i = 0; i < arm.Helpers.Length; i++)
            {
                var helper = arm.Helpers[i];
                if (helper == null) continue;
                var off = (helper.localPosition - arm.HelperPlaces[i]).magnitude;
                if (off < HelperSlack) continue;
                if (!arm.HelperLogged[i] && logs++ < 60)
                {
                    arm.HelperLogged[i] = true;
                    Plugin.Log.LogInfo($"Hands: {arm.Name} arm's '{helper.name}' put back, {off * 100f:0.0} cm off its place");
                }
                helper.localPosition = arm.HelperPlaces[i];
            }
            if ((upper || lower) && !arm.LengthLogged)
            {
                arm.LengthLogged = true;
                Plugin.Log.LogInfo($"Hands: {arm.Name} arm put back to its own length (upper {arm.UpperLength:0.000} m, lower {arm.LowerLength:0.000} m)");
            }
        }

        // The bone moved along its parent's way to it until it lies length (in the world) from it; true if it was off
        // by more than 1 %.
        private static bool Fit(Transform bone, Transform parent, float length)
        {
            var now = Vector3.Distance(bone.position, parent.position);
            if (now < 1e-4f || Mathf.Abs(now - length) <= 0.01f * length) return false;
            bone.localPosition *= length / now;
            return true;
        }

        // Helper bones count from this far off their place, in meters (the animation's own small moves stay).
        private const float HelperSlack = 0.005f;

        // The skin's twist, stretch and track bones of this arm (from the collarbone down, not the fingers), with their
        // places on their parents in the bind pose.
        private static void FindHelpers(Arm arm, Transform[] bones, Matrix4x4[] binds)
        {
            var root = arm.Upper.parent != null ? arm.Upper.parent : arm.Upper;
            var helpers = new System.Collections.Generic.List<Transform>();
            var places = new System.Collections.Generic.List<Vector3>();
            for (var i = 0; i < bones.Length && i < binds.Length; i++)
            {
                var bone = bones[i];
                if (bone == null || bone.parent == null || !bone.IsChildOf(root)) continue;
                var name = bone.name.ToLowerInvariant();
                // Twist and stretch bones only: a "track" bone (collar_shoulder_track) may follow the collarbone on purpose.
                if (!name.Contains("twist") && !name.Contains("stretch")) continue;
                var parent = System.Array.IndexOf(bones, bone.parent);
                if (parent < 0 || parent >= binds.Length) continue;
                helpers.Add(bone);
                places.Add((binds[parent] * binds[i].inverse).GetColumn(3));
            }
            arm.Helpers = helpers.ToArray();
            arm.HelperPlaces = places.ToArray();
            arm.HelperLogged = new bool[arm.Helpers.Length];
        }

        // The arm's bone lengths from the bind pose of the skin that uses its bones (the suit's arms, geo_arms).
        private static bool BindLengths(Arm arm)
        {
            foreach (var skin in grab.GetComponentsInChildren<SkinnedMeshRenderer>(true))
            {
                var bones = skin.bones;
                var binds = skin.sharedMesh != null ? skin.sharedMesh.bindposes : null;
                if (bones == null || binds == null) continue;
                int upper = System.Array.IndexOf(bones, arm.Upper), lower = System.Array.IndexOf(bones, arm.Lower), wrist = System.Array.IndexOf(bones, arm.Wrist);
                if (upper < 0 || lower < 0 || wrist < 0 || wrist >= binds.Length || upper >= binds.Length || lower >= binds.Length) continue;
                Vector3 At(int i) => binds[i].inverse.GetColumn(3);
                arm.UpperLength = Vector3.Distance(At(upper), At(lower)) * arm.Upper.lossyScale.x;
                arm.LowerLength = Vector3.Distance(At(lower), At(wrist)) * arm.Lower.lossyScale.x;
                FindHelpers(arm, bones, binds);
                Plugin.Log.LogInfo($"Hands: {arm.Name} arm's own length from '{skin.name}': upper {arm.UpperLength:0.000} m, lower {arm.LowerLength:0.000} m"
                    + $"; helper bones {(arm.Helpers.Length > 0 ? string.Join(", ", System.Array.ConvertAll(arm.Helpers, h => h.name)) : "none")}");
                return arm.UpperLength > 0.05f && arm.LowerLength > 0.05f;
            }
            return false;
        }

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
            // Every frame for both, so a hand let go of its control forgets it once the game is done with it.
            var leftControl = UsesHeldControl(LeftArm);
            var rightControl = UsesHeldControl(RightArm);
            Drive(LeftArm, active && (Idle(grab.LeftHand) || leftControl));
            var rightBusy = VrToolModels.PosesRightArm;
            Drive(RightArm, active && !rightBusy && (Idle(grab.RightHand) || rightControl
                || (grab.RightHand.CurrentHandState == HandGrab.HandState.Equipped && (equipment != EquipmentController.Equipment.DemoCharge || chargeLeft))));
            // The demo charge's arm is bent there; another bend on top would fight it.
            if (rightBusy) RightArm.Weight = 0f;
            Fingers(LeftArm, leftWasDriven);
            Fingers(RightArm, rightWasDriven);
            Pose(LeftArm, active && chargeLeft && Mirrored(out var hold) ? hold : Quaternion.identity);
            Pose(RightArm, Quaternion.identity);
            AtControl(LeftArm);
            AtControl(RightArm);
            Curl(LeftArm);
            Curl(RightArm);
            Charges(active && chargeLeft);
        }

        // A hand working a control is the game's, which reaches for the control's HandTarget from where its body is. In VR the
        // body is elsewhere, so the wrist is bent onto the target here.
        private static void AtControl(Arm arm)
        {
            if (arm.Driven || arm.Hand == null || arm.Hand.CurrentHandState != HandGrab.HandState.Success) return;
            if (arm.Upper == null || arm.Lower == null || arm.Wrist == null) return;
            if (interaction == null && Time.unscaledTime >= nextInteractionSearch)
            {
                nextInteractionSearch = Time.unscaledTime + 1f;
                interaction = Object.FindObjectOfType<InteractionController>();
            }
            var control = interaction != null ? interaction.CurrentInteractable : null;
            if (control == null || control.HandTarget == null) return;
            // Things that float loose, as cassettes and cables to collect, sit off their HandTarget in the hand: the hand goes
            // to the thing itself. Controls on the hull keep the place made for the hand.
            var target = VrClimb.Loose(control) ? control.GetComponentInParent<Rigidbody>().worldCenterOfMass : control.HandTarget.position;
            VrToolModels.Bend(arm.Upper, arm.Lower, arm.Wrist, target);
        }

        // The animator's grab pose, through the parameters HandGrab sets for grabbing and for holding a tool (PrepGrab
        // trigger and Grab on; Grab off and CancelGrab trigger): open while the hand follows its controller, as Curl bends
        // the fingers then. Handed back to the game, the hand gets the pose the game had set for it: closed on a tool or a grab, open otherwise. The game
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
                // Open for the animator: Curl bends the fingers.
                closed = false;
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

        // Bends the fingers of a hand that follows its controller: around its tool (not a demo charge, whose animation
        // holds it), around a small part that turns with the hand (VrClimb.HoldsFlat false) or into a fist while its
        // grip is held; straight on a wall, a console or a large part (Straighten), as animated with the grip let go.
        // Each joint turns about the hand's own thumb-side axis (Frame) towards the palm, which faces inwards: to the
        // left on the right hand, to the right on the left one. The thumb turns about the knuckles' axis over the palm.
        private static void Curl(Arm arm)
        {
            var hand = arm == LeftArm ? VrHand.Left : VrHand.Right;
            var tool = hand.HoldsTool && hand.Tool != EquipmentController.Equipment.DemoCharge;
            var grip = arm.Input.Grip.Down && hand.State != HandState.HiddenTool;
            var flat = arm.Driven && !tool && grip && VrClimb.HoldsFlat(arm.Input);
            var closing = arm.Driven && (tool || (grip && !flat));
            if (closing) arm.Fist = !tool && (hand.State != HandState.Holding || VrClimb.HoldsControl(arm.Input));
            arm.Closed = Mathf.MoveTowards(arm.Closed, closing ? 1f : 0f, Time.unscaledDeltaTime / CurlTime);
            arm.Flat = Mathf.MoveTowards(arm.Flat, flat ? 1f : 0f, Time.unscaledDeltaTime / CurlTime);
            if (!flat)
            {
                arm.ThumbLogged = false;
                System.Array.Clear(arm.Edge, 0, arm.Edge.Length);
            }
            var amount = arm.Closed * arm.Weight;
            var flatness = arm.Flat * arm.Weight;
            if ((amount <= 0f && flatness <= 0f) || !Frame(arm, out _, out var frame))
            {
                Open(arm);
                return;
            }
            var side = arm == LeftArm ? 1f : -1f;
            var across = frame * Vector3.up;
            var along = frame * Vector3.forward;
            if (amount <= 0f)
            {
                // Held on by the glove, as by a magnet: the fingers along the knuckles, barely bent, the thumb in
                // their plane instead of in the surface.
                for (var i = 0; i < arm.Joints.Length; i++)
                    Straighten(arm, i, across, Quaternion.AngleAxis(side * FlatBend[i % 3], across) * along, across, flatness);
                var palm = frame * Vector3.right * side;
                OverEdges(arm, palm, across, side, flatness);
                for (var i = 0; i < arm.Thumb.Length; i++) LiftThumb(arm, i, palm, flatness);
                LogThumb(arm, palm, flatness);
                return;
            }
            var bend = arm.Fist ? FistBend : GripBend;
            for (var i = 0; i < arm.Joints.Length; i++)
                Turn(arm, arm.Joints[i], i, Quaternion.AngleAxis(side * bend[i % 3] * amount, across));
            var thumb = (arm.Fist ? FistThumb : GripThumb) * amount;
            // The humanoid three only: the rig's further thumb bones keep their pose relative to them.
            for (var i = 0; i < 3; i++)
                Turn(arm, arm.Thumb[i], arm.Joints.Length + i, Quaternion.AngleAxis(-side * thumb, along));
            if (arm.Fist) ThumbOnMiddle(arm, frame * Vector3.right * side, amount);
        }

        // The thumb's base turned so that its tip lies over the middle finger's last joint, closing the fist's tunnel.
        private static void ThumbOnMiddle(Arm arm, Vector3 palm, float share)
        {
            var root = arm.Thumb[0];
            var last = arm.Thumb.LastOrDefault(j => j != null);
            var middle = arm.Joints[5];
            if (root == null || last == null || middle == null) return;
            var tip = last.childCount > 0 ? last.GetChild(0).position : last.position;
            var target = middle.position + palm * ThumbOver * VrCamera.WorldScale;
            var turn = Quaternion.FromToRotation(tip - root.position, target - root.position);
            root.rotation = Quaternion.Slerp(Quaternion.identity, turn, share) * root.rotation;
            arm.Set[arm.Joints.Length] = root.localRotation;
        }

        // The joints that are still as Curl left them back to the animation's pose.
        private static void Open(Arm arm)
        {
            for (var i = 0; i < arm.Base.Length; i++)
            {
                var joint = i < arm.Joints.Length ? arm.Joints[i] : arm.Thumb[i - arm.Joints.Length];
                if (joint != null && joint.localRotation == arm.Set[i]) joint.localRotation = arm.Base[i];
                arm.Set[i] = new Quaternion(0f, 0f, 0f, 0f);
            }
        }

        private static void Turn(Arm arm, Transform joint, int index, Quaternion by)
        {
            if (!Restore(arm, joint, index)) return;
            joint.rotation = by * joint.rotation;
            arm.Set[index] = joint.localRotation;
        }

        // A finger joint turned about the hand's thumb-side axis until the bone to its next joint (or, at the tip, to its
        // child) runs along the knuckles, by that share.
        private static void Straighten(Arm arm, int index, Vector3 across, Vector3 along, Vector3 axis, float share)
        {
            var joint = arm.Joints[index];
            if (!Restore(arm, joint, index)) return;
            var next = index % 3 < 2 ? arm.Joints[index + 1] : joint.childCount > 0 ? joint.GetChild(0) : null;
            if (next == null) return;
            var bone = Vector3.ProjectOnPlane(next.position - joint.position, across);
            if (bone.sqrMagnitude <= 1e-10f) return;
            var angle = Vector3.SignedAngle(bone, Vector3.ProjectOnPlane(along, across), axis);
            joint.rotation = Quaternion.AngleAxis(angle * share, axis) * joint.rotation;
            arm.Set[index] = joint.localRotation;
        }

        // Each finger, straight as Straighten left it, curls from the first joint whose next bone has no surface under it:
        // there it reaches over an edge. Found on the straight finger, so the curl does not change what is found.
        private static void OverEdges(Arm arm, Vector3 palm, Vector3 across, float side, float share)
        {
            var body = arm.Wrist.root;
            var probe = EdgeProbe * VrCamera.WorldScale;
            for (var finger = 0; finger < arm.Joints.Length / 3; finger++)
            {
                var over = false;
                for (var k = 0; k < 3; k++)
                {
                    var index = finger * 3 + k;
                    var joint = arm.Joints[index];
                    var next = k < 2 ? arm.Joints[index + 1] : joint != null && joint.childCount > 0 ? joint.GetChild(0) : null;
                    if (!over && next != null && !Supported(next.position, palm, probe, body)) over = true;
                    arm.Edge[index] = Mathf.MoveTowards(arm.Edge[index], over ? 1f : 0f, Time.unscaledDeltaTime / EdgeTime);
                }
                for (var k = 0; k < 3; k++)
                {
                    var index = finger * 3 + k;
                    var joint = arm.Joints[index];
                    if (joint == null || arm.Edge[index] <= 0f) continue;
                    joint.rotation = Quaternion.AngleAxis(side * EdgeBend[k] * arm.Edge[index] * share, across) * joint.rotation;
                    arm.Set[index] = joint.localRotation;
                }
            }
        }

        // Whether something solid lies within probe of that point towards the palm (or the point is in it).
        private static bool Supported(Vector3 point, Vector3 palm, float probe, Transform body)
        {
            var count = Physics.RaycastNonAlloc(point - palm * probe, palm, WallHits, 2f * probe, Physics.DefaultRaycastLayers, QueryTriggerInteraction.Ignore);
            for (var i = 0; i < count; i++)
                if (!WallHits[i].collider.transform.IsChildOf(body)) return true;
            return false;
        }

        // The last bone of a chain's tip, or null.
        private static Transform Tip(Transform[] chain)
        {
            var last = chain.LastOrDefault(j => j != null);
            return last != null && last.childCount > 0 ? last.GetChild(0) : null;
        }

        // Once per flat hold, the first few times: each thumb bone's angle to the palm's plane (negative towards the palm).
        private static void LogThumb(Arm arm, Vector3 palm, float share)
        {
            if (share < 1f || arm.ThumbLogged || thumbLogs >= 12) return;
            arm.ThumbLogged = true;
            thumbLogs++;
            var angles = new List<string>();
            var chain = arm.Thumb.Where(j => j != null).ToList();
            var tip = Tip(arm.Thumb);
            if (tip != null) chain.Add(tip);
            for (var i = 0; i + 1 < chain.Count; i++)
                angles.Add($"{chain[i].name} {Vector3.Angle(chain[i + 1].position - chain[i].position, palm) - 90f:0}°");
            Plugin.Log.LogInfo($"Hands: {arm.Name} flat thumb to the palm's plane: {string.Join(", ", angles)}");
        }

        // A thumb joint turned so that its bone runs ThumbIn from the palm's plane towards the palm: on a flat hand the
        // animation's thumb pointed into the wall.
        private static void LiftThumb(Arm arm, int index, Vector3 palm, float share)
        {
            var joint = arm.Thumb[index];
            var at = arm.Joints.Length + index;
            if (!Restore(arm, joint, at)) return;
            var next = index + 1 < arm.Thumb.Length && arm.Thumb[index + 1] != null ? arm.Thumb[index + 1]
                : joint.childCount > 0 ? joint.GetChild(0) : null;
            if (next == null) return;
            var bone = next.position - joint.position;
            var axis = Vector3.Cross(bone, palm);
            if (axis.sqrMagnitude <= 1e-12f) return;
            var into = 90f - Vector3.Angle(bone, palm) - ThumbIn;
            joint.rotation = Quaternion.AngleAxis(-into * share, axis.normalized) * joint.rotation;
            arm.Set[at] = joint.localRotation;
        }

        // The joint back to the animation's pose if it is still as it was set here, that pose kept as its base.
        private static bool Restore(Arm arm, Transform joint, int index)
        {
            if (joint == null) return false;
            if (joint.localRotation == arm.Set[index]) joint.localRotation = arm.Base[index];
            arm.Base[index] = joint.localRotation;
            arm.Set[index] = joint.localRotation;
            return true;
        }

        // A hand that holds on to a control and uses it: it stays where it holds, as a fist, instead of going back to the
        // game, whose animation bent it to the control's HandTarget and the hand flew off the lever (Raik). Let go while the
        // game still works the control (the airlock's lever turned by hand, VrLever), it stays with its controller until
        // the game is done, instead of jumping to the HandTarget to finish the game's animation there (Raik).
        private static bool UsesHeldControl(Arm arm)
        {
            if (arm.Hand == null || arm.Hand.CurrentHandState != HandGrab.HandState.Success) arm.UsedControl = false;
            else if (VrClimb.HoldsControl(arm.Input) || VrButtons.JustPressed) arm.UsedControl = true;
            return arm.UsedControl;
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
            // A hand holding the cutter or the grapple turns with the tool, not the controller: when the tool is held off by
            // something (VrToolCollide), the hand stays on its grip and turns with it instead of twisting the wrist, and
            // comes back with it.
            var vrHand = arm == LeftArm ? VrHand.Left : VrHand.Right;
            if (vrHand.HoldsTool && (vrHand.Tool == EquipmentController.Equipment.CuttingTool || vrHand.Tool == EquipmentController.Equipment.GrappleHook))
            {
                aimLocal = VrToolSteady.Apply(arm.Input, vrHand.Tool, aimLocal);
                // With weight the tool lags behind the controller, and the hand on its grip with it (VrToolWeight).
                gripLocal = VrToolWeight.Grip(arm.Input, vrHand.Tool, gripLocal);
            }
            if (!VrToolModels.TrackingToWorld(gripLocal + aimLocal * VrInput.WristBehindGrip, aimLocal, out var wrist, out var aim)) return;
            var held = VrClimb.TryGetHold(arm.Input, out var point);
            if (held && VrToolModels.TrackingToWorld(gripLocal, aimLocal, out var grip, out _))
                arm.OffController = point - grip;
            else
                arm.OffController = Vector3.MoveTowards(arm.OffController, Vector3.zero, HoldReturn * VrCamera.WorldScale * Time.unscaledDeltaTime);
            wrist += arm.OffController;
            wrist = OutOfWalls(arm, wrist);
            var aimed = aim * hold;
            var frame = Quaternion.LookRotation(aimed * Vector3.forward, aimed * Vector3.up);
            Grip(arm, held, point, ref wrist, ref frame);

            var animated = arm.Wrist.rotation;
            var target = Vector3.Lerp(arm.Wrist.position, wrist, weight);
            if (!arm.Measured) Measure(arm, wrist);
            var shortBy = VrToolModels.Reach(arm.Upper.parent, arm.Upper, arm.Lower, arm.Wrist, target, Elbow(arm));
            Count(arm, weight >= 1f ? shortBy : 0f);
            arm.Wrist.rotation = Quaternion.Slerp(animated, HandRotation(arm, frame, aimed, animated), weight);
        }

        // A hand that holds on stays fixed on the thing held, as Raik asked: it no longer turns with its controller, so its
        // fingers do not turn into the thing, and only the arm moves around it. On a wall, a hull or a large part
        // (VrClimb.HoldsFlat) it lies flat on the surface, the knuckles along the controller's aim; around a small part it
        // keeps the turn and place it took hold with. Letting go it goes back to its controller.
        private static void Grip(Arm arm, bool held, Vector3 point, ref Vector3 wrist, ref Quaternion frame)
        {
            if (held && VrClimb.TryGetGrip(arm.Input, out var anchor, out var normal))
            {
                var slides = VrClimb.Slides(arm.Input);
                if (!arm.Gripping || anchor != arm.GripAnchor || slides != arm.GripSlides)
                {
                    // Slid on the same hold: the hand turns over to its new place instead of snapping.
                    var slid = arm.Gripping && anchor == arm.GripAnchor;
                    // From the turn shown now, also in the middle of turning along (RollAlong).
                    arm.GripTurnBefore = ShownGripTurn(arm);
                    arm.GripTurnAt = slid ? Time.unscaledTime : -1f;
                    arm.GripSlides = slides;
                    arm.Gripping = true;
                    arm.GripAnchor = anchor;
                    var flat = VrClimb.HoldsFlat(arm.Input);
                    arm.GripFlat = flat;
                    var start = flat ? FlatFrame(arm, frame, normal) : frame;
                    var side = arm == LeftArm ? 1f : -1f;
                    arm.GripOffset = flat
                        ? new Vector3(-side * PalmThickness, 0f, -PalmLength) * VrCamera.WorldScale
                        : Quaternion.Inverse(start) * (wrist - point);
                    if (!flat) start = AroundBar(arm, true) * start;
                    arm.GripNormal = Quaternion.Inverse(anchor.rotation) * normal;
                    arm.GripTurn = Quaternion.Inverse(anchor.rotation) * start;
                    if (logs++ < 40) Plugin.Log.LogInfo($"Hands: {arm.Name} hand fixed on '{anchor.name}'{(flat ? ", flat on the surface" : "")}");
                }
                arm.GripPoint = anchor.InverseTransformPoint(point);
                arm.GripBlend = Mathf.MoveTowards(arm.GripBlend, 1f, Time.unscaledDeltaTime / GripTime);
            }
            else
            {
                arm.Gripping = false;
                arm.GripBlend = Mathf.MoveTowards(arm.GripBlend, 0f, Time.unscaledDeltaTime / GripTime);
            }
            if (arm.GripBlend <= 0f || arm.GripAnchor == null)
            {
                if (!arm.Gripping) arm.GripAnchor = null;
                return;
            }
            var turning = arm.GripTurnAt >= 0f && Time.unscaledTime - arm.GripTurnAt < SlideTurnTime;
            var gripTurn = ShownGripTurn(arm);
            // Flat on a surface while the body rolls (VTOL's tilt, the Shooter's trigger roll): the hand stays put and the
            // arm winds round the wrist. Past RollAlong degrees between the hand and where the controller would lay it, it
            // turns on the spot to the controller's way, over SlideTurnTime (Raik: it should slip after now and then).
            if (arm.Gripping && arm.GripFlat && !turning)
            {
                var along = FlatFrame(arm, frame, arm.GripAnchor.rotation * arm.GripNormal);
                if (Quaternion.Angle(arm.GripAnchor.rotation * gripTurn, along) > RollAlong)
                {
                    arm.GripTurnBefore = gripTurn;
                    arm.GripTurn = Quaternion.Inverse(arm.GripAnchor.rotation) * along;
                    arm.GripTurnAt = Time.unscaledTime;
                    if (rollLogs++ < 20) Plugin.Log.LogInfo($"Hands: {arm.Name} hand turns along on '{arm.GripAnchor.name}', the arm wound past {RollAlong:0}°");
                }
            }
            // Held free (a nacelle switch, VrClimb.HeldFree), the hand stays on its hold but turns with the controller.
            var turn = arm.Gripping && VrClimb.HeldFree(arm.Input) ? AroundBar(arm, false) * frame : arm.GripAnchor.rotation * gripTurn;
            var placed = arm.GripAnchor.TransformPoint(arm.GripPoint) + turn * arm.GripOffset;
            if (arm.GripFlat) placed = OutOfSurface(arm, placed, turn);
            wrist = Vector3.Lerp(wrist, placed, arm.GripBlend);
            frame = Quaternion.Slerp(frame, turn, arm.GripBlend);
        }

        // On a lever's grip (VrClimb.TryGetBar), the turn that lays the fist around it: the controller's grip axis, which
        // runs through the closed hand, onto the grip's direction, the least turn either way round. Turned about the hold
        // point, as the offset to the wrist turns with the hand.
        private static Quaternion AroundBar(Arm arm, bool log)
        {
            if (!VrClimb.TryGetBar(arm.Input, out var bar) || !arm.Input.TryGetGripPose(out _, out var grip)) return Quaternion.identity;
            var through = VrCamera.Instance.transform.parent.rotation * grip * Vector3.forward;
            if (Vector3.Dot(through, bar) < 0f) bar = -bar;
            if (log && barLogs++ < 30) Plugin.Log.LogInfo($"Hands: {arm.Name} fist turned {Vector3.Angle(through, bar):0}° around the lever's grip");
            return Quaternion.FromToRotation(through, bar);
        }

        private static int barLogs;

        // How long a hand takes to turn over to its hold after sliding along a surface (VrClimb.Slide), or turning along
        // it (RollAlong), in seconds.
        private const float SlideTurnTime = 0.35f;
        private const float RollAlong = 45f;

        // The hand's turn on its hold as shown now, on the way over to a new one after sliding or turning along.
        private static Quaternion ShownGripTurn(Arm arm) => arm.GripTurnAt < 0f ? arm.GripTurn
            : Quaternion.Slerp(arm.GripTurnBefore, arm.GripTurn, Mathf.Clamp01((Time.unscaledTime - arm.GripTurnAt) / SlideTurnTime));
        private static int rollLogs;

        // A flat hand placed on its hold, moved out along the palm's way as far as the deepest of its points (HandAlong,
        // HandAcross on the palm's side) went into anything: a ray from outside towards each point finds what is in front
        // of it. Only what has a collider counts; where a hull's visible skin lies outside its collider, the hand still
        // goes into the skin.
        private static Vector3 OutOfSurface(Arm arm, Vector3 wrist, Quaternion turn)
        {
            var side = arm == LeftArm ? 1f : -1f;
            var scale = VrCamera.WorldScale;
            var palm = turn * Vector3.right * side;
            var probe = SurfaceProbe * scale;
            var deepest = 0f;
            var body = arm.Wrist.root;
            foreach (var along in HandAlong)
                for (var across = -1; across <= 1; across += 2)
                {
                    var point = wrist + turn * new Vector3(side * PalmThickness, across * HandAcross, along) * scale;
                    var count = Physics.RaycastNonAlloc(point - palm * probe, palm, WallHits, probe, Physics.DefaultRaycastLayers, QueryTriggerInteraction.Ignore);
                    for (var i = 0; i < count; i++)
                    {
                        var hit = WallHits[i];
                        if (hit.distance <= 0f || hit.collider.transform.IsChildOf(body)) continue;
                        deepest = Mathf.Max(deepest, probe - hit.distance);
                    }
                }
            return wrist - palm * deepest;
        }

        // The hand's axes (Frame) lying flat on a surface: the palm on it (to the left of the right hand, to the right of
        // the left one), the knuckles along the surface where the controller points.
        private static Quaternion FlatFrame(Arm arm, Quaternion frame, Vector3 normal)
        {
            var forward = Vector3.ProjectOnPlane(frame * Vector3.forward, normal);
            if (forward.sqrMagnitude < 1e-4f) forward = Vector3.ProjectOnPlane(frame * Vector3.up, normal);
            forward.Normalize();
            var up = arm == LeftArm ? Vector3.Cross(normal, forward) : Vector3.Cross(forward, normal);
            return Quaternion.LookRotation(forward, up);
        }

        // Where the elbow points, in the body's axes (VrTorso.Frame): down, a little out to its side and back, as a relaxed
        // arm's does, whatever the game's animation gives it.
        private static readonly Vector3 ElbowRight = new Vector3(0.4f, -1f, -0.3f);

        private static Vector3? Elbow(Arm arm)
        {
            var camera = VrCamera.Instance != null ? VrCamera.Instance.GameCamera : null;
            if (camera == null) return null;
            var local = ElbowRight;
            if (arm == LeftArm) local.x = -local.x;
            return VrTorso.Frame(camera.transform.rotation) * local;
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

        // The hand's own axes (Frame) turned to wanted: usually the controller's aim, the knuckles (wrist to the middle
        // finger) along it, the thumb side (little finger to index finger) up, as when holding a Touch controller: the palm
        // then faces inwards on either hand; on a hold, as Grip has it. Without finger bones the hand keeps the angle to
        // the eye that the animation gave it, turned with aim.
        private static Quaternion HandRotation(Arm arm, Quaternion wanted, Quaternion aim, Quaternion animated)
        {
            if (Frame(arm, out _, out var current))
                return wanted * Quaternion.Inverse(current) * arm.Wrist.rotation;
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
                Plugin.Log.LogInfo($"Hands: {arm.Name} arm {Name(arm.Upper)} > {Name(arm.Lower)} > {Name(arm.Wrist)}, fingers {(arm.Index != null && arm.Middle != null && arm.Little != null ? "found" : "missing")}, {arm.Joints.Count(j => j != null)} finger joints, {arm.Thumb.Count(j => j != null)} thumb joints ({string.Join(" > ", arm.Thumb.Where(j => j != null).Select(Name))}, tip {Name(Tip(arm.Thumb))}), index {Name(arm.Joints[0])}");
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
            var first = (int)index;
            for (var i = 0; i < arm.Joints.Length; i++)
            {
                // Index, middle, ring and little, three each, follow each other in HumanBodyBones; the thumb comes before.
                arm.Joints[i] = animator != null ? animator.GetBoneTransform((HumanBodyBones)(first + i)) : null;
                arm.Set[i] = new Quaternion(0f, 0f, 0f, 0f);
            }
            for (var i = 0; i < arm.Thumb.Length; i++)
            {
                // Past the humanoid three, the rig's next bone down, while that one has a bone after it: the tip's
                // segment stayed bent up on a flat hand without it (Raik).
                var above = i > 0 ? arm.Thumb[i - 1] : null;
                arm.Thumb[i] = animator == null ? null
                    : i < 3 ? animator.GetBoneTransform((HumanBodyBones)(first - 3 + i))
                    : above != null && above.childCount > 0 && above.GetChild(0).childCount > 0 ? above.GetChild(0) : null;
                arm.Set[arm.Joints.Length + i] = new Quaternion(0f, 0f, 0f, 0f);
            }
            arm.Closed = arm.Flat = 0f;
            System.Array.Clear(arm.Edge, 0, arm.Edge.Length);
            arm.Weight = 0f;
            arm.Driven = false;
            arm.UpperLength = arm.LowerLength = 0f;
            arm.LengthLogged = false;
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
            {
                if (animator != null && arm.Driven && arm.Hand != null && Idle(arm.Hand)) animator.SetBool(arm.Grab, false);
                Open(arm);
                arm.Closed = arm.Flat = 0f;
            System.Array.Clear(arm.Edge, 0, arm.Edge.Length);
            }
            animator = null;
            grab = null;
            LeftArm.Weight = RightArm.Weight = 0f;
            LeftArm.Driven = RightArm.Driven = false;
            LeftArm.OffController = RightArm.OffController = Vector3.zero;
            foreach (var arm in new[] { LeftArm, RightArm })
            {
                arm.Gripping = false;
                arm.GripAnchor = null;
                arm.GripBlend = 0f;
            }
            Charges(false);
            chargePieces.Clear();
            chargesOn = null;
        }
    }
}
