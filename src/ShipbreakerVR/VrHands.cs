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
        // Test (Raik's photos, 2026-11-13: the closed hand on a tool looked too stiff): the cutter and the grapple held as a
        // handle in the hand, each finger joint (index, middle, ring, little; base, middle, tip) this far from the knuckles'
        // line towards the palm, in degrees, wrapped round it: the index finger a little more open, each further finger a
        // little more curled. The thumb lies over the index finger's middle bone (ThumbOnIndex), ThumbOver out from it.
        private static readonly float[] ToolBend = { 62f, 145f, 185f, 68f, 155f, 195f, 74f, 162f, 205f, 80f, 170f, 215f };
        // A flat hand is not stiff: each finger joint (base, middle, tip) stays bent this much towards the palm, in
        // degrees. The thumb lies in the palm's plane with the fingers, each of its bones this much towards the palm, about
        // 2 to 3 mm over its length (Raik).
        private static readonly float[] FlatBend = { 4f, 6f, 4f };
        private const float ThumbIn = 3f;
        // Test (Raik's photos, 2026-11-13): holding on to a wall the hand is not quite flat. It stands ClawLift further off
        // the surface, and each finger bends (base, middle, tip, in degrees, at most ClawBend) until its tip, TipRadius
        // thick, touches the surface under it; the thumb leans ClawThumb more towards it. As if trying to grip the wall.
        private const float ClawLift = 0.01f;
        private static readonly float[] ClawBend = { 12f, 28f, 12f };
        private const float TipRadius = 0.008f;
        private const float ClawThumb = 12f;
        // Test (Raik, 2026-11-13): an empty hand pushed against a surface without holding on lies flat on it, as on a
        // hold, and its fingers curl over edges. Pushed: the controller's wrist PressAt or less off the surface (the
        // palm would be PalmThickness off it), the palm facing it by PressFacing (cosine) at least; let go again
        // PressRelease further out.
        private const float PressFacing = 0.5f;
        // Test (Raik, 2026-11-13): an empty hand whose palm comes within FlatFrom of a surface it faces lies flat on it
        // (0: from touching on, so a hand folding over goes on to lie flat without a jump; 0.9 cm jumped),
        // and lets go again FlatRelease further out. Coming at a surface fingers first, the fingers bend back at the
        // knuckles as far as they need to, at most MostBack degrees; from there on the hand folds over onto the surface
        // (Fold) until it lies flat. The wall is looked for up to WallReach from the wrist.
        private const float FlatFrom = 0f;
        private const float FlatRelease = 0.01f;
        private const float MostBack = 30f;
        private const float WallReach = 0.25f;
        // The wrist's own radius, the arm stopping at walls (OutOfWalls): the hand's shape does the rest (Touch).
        private const float WristRadius = 0.02f;
        // A tip pressing on a held wall goes this far into it at most (Claw).
        private const float ClawPress = 0.002f;
        // Test (0.30.0, Raik's photos of his hand): an empty hand is loose, not as the game animates it. Each finger joint
        // (index, middle, ring, little; base, middle, tip) bent this far from the knuckles' line towards the palm, in
        // degrees: the index finger nearly straight, each further finger a little more curled, the little one the most.
        private static readonly float[] IdleBend = { 8f, 18f, 28f, 12f, 26f, 38f, 15f, 32f, 45f, 20f, 40f, 54f };
        // Test (Raik): the game's fingers are long, the index finger the longest. All fingers this much of their length,
        // the index finger at most as long as the ring finger, and this much shorter again.
        private const float FingerLength = 0.85f;
        private const float IndexShorter = 0.97f;
        // Test (Raik, 2026-11-13): the real controller sat at the game hand's palm, so the own hand felt nearly outside
        // the game's. An empty or grabbing hand sits this far away from where its palm faces (the hands, palms facing each
        // other, 1 cm further apart each), in meters (tracking space); a hand on the cutter or the grapple stays as fitted
        // to the tool.
        private const float PalmShift = 0.01f;
        // The thumb's free part (from its first joint out of the palm); its base in the palm keeps its size, so the thumb
        // sits where it did and the ball of the thumb keeps its shape (Raik: scaling the base kinked it and set it low).
        private const float ThumbLength = 0.94f;
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
            public float IndexScale;
            public int ThumbBendLogs;
            public bool IdleLogged;
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
            // How far the fingers press their tips on a held surface (0 to 1, ClawBend).
            public float Claw;
            // The surface the empty hand met last frame (OutOfWalls), how far the controller's wrist was off it, and
            // whether and how far the hand lies flat on it, pushed (Press).
            public bool WallMet;
            public Vector3 WallPoint;
            public Vector3 WallNormal;
            public float WallDepth;
            public bool Pressed;
            public float Press;
            // The hand's shape in its own axes (Frame), as last posed before bending back: each finger's knuckle and tip
            // (index to little), for Touch to try the hand against a wall before it is posed.
            public readonly Vector3[] ShapeKnuckle = new Vector3[4];
            public readonly Vector3[] ShapeTip = new Vector3[4];
            public bool ShapeKnown;
            // How far the fingers are bent back at the knuckles (Touch), and how far the hand is folded over (0 to 1).
            public float Back;
            public float Folded;
            public float FoldedBefore;
            public bool ClawLogged;
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

        // An arm as drawn last, whatever its hand does: shoulder, elbow, wrist and the middle finger's base (VrArmPush,
        // VrToolCollide).
        public static bool TryGetArm(bool left, out Vector3 shoulder, out Vector3 elbow, out Vector3 wrist, out Vector3 knuckles)
        {
            var arm = left ? LeftArm : RightArm;
            shoulder = elbow = wrist = knuckles = Vector3.zero;
            if (arm.Upper == null || arm.Lower == null || arm.Wrist == null) return false;
            shoulder = arm.Upper.position;
            elbow = arm.Lower.position;
            wrist = arm.Wrist.position;
            knuckles = arm.Middle != null ? arm.Middle.position : wrist;
            return true;
        }

        // From VrCamera.LateUpdate, after VrToolModels.PoseArm: which arms follow their controllers, blended in and out
        // over Blend, and those posed.
        public static void LateUpdate()
        {
            if (!Find()) return;
            Shorten(LeftArm);
            Shorten(RightArm);
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
            MirrorThumb(active && !VrHand.Right.HoldsTool);
            ReadChargeFingers(active && chargeLeft);
            Curl(LeftArm);
            Curl(RightArm);
            MirrorChargeFingers();
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
            var held = grip && VrClimb.HoldsFlat(arm.Input);
            var flat = arm.Driven && !tool && (held || (!grip && arm.Pressed));
            arm.Claw = Mathf.MoveTowards(arm.Claw, flat && held ? 1f : 0f, Time.unscaledDeltaTime / CurlTime);
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
            // The loose hand fades out as the hand closes, so a grip still starts from the animation's pose.
            var loose = arm.Weight * (1f - arm.Closed);
            if ((amount <= 0f && flatness <= 0f && loose <= 0f) || !Frame(arm, out _, out var frame))
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
                // their plane instead of in the surface. Empty, loose (IdleBend), and from there to flat.
                for (var i = 0; i < arm.Joints.Length; i++)
                {
                    var toward = Mathf.Lerp(IdleBend[i], FlatBend[i % 3], arm.Flat);
                    Straighten(arm, i, across, Quaternion.AngleAxis(side * toward, across) * along, across, arm.Weight);
                    InLine(arm, i, across, arm.Weight);
                }
                Remember(arm);
                if (flatness <= 0f)
                {
                    BendBack(arm, across, side);
                    ThumbInLine(arm, frame * Vector3.right * side, arm.Weight, false);
                    LogIdle(arm);
                    return;
                }
                var palm = frame * Vector3.right * side;
                OverEdges(arm, palm, across, side, flatness);
                Claw(arm, palm, across, side, flatness * arm.Claw);
                for (var i = 0; i < arm.Thumb.Length; i++) LiftThumb(arm, i, palm, flatness, ThumbIn + ClawThumb * arm.Claw);
                ThumbInLine(arm, palm, flatness, true);
                LogThumb(arm, palm, flatness);
                return;
            }
            var bend = arm.Fist ? FistBend : GripBend;
            for (var i = 0; i < arm.Joints.Length; i++)
            {
                // A tool: wrapped round its handle, from the animation's pose (ToolBend).
                if (tool)
                {
                    Straighten(arm, i, across, Quaternion.AngleAxis(side * ToolBend[i], across) * along, across, amount);
                    InLine(arm, i, across, amount);
                    continue;
                }
                // Closing from the loose hand: the grip on top of what is left of it.
                if (loose > 0f)
                {
                    Straighten(arm, i, across, Quaternion.AngleAxis(side * IdleBend[i], across) * along, across, loose);
                    InLine(arm, i, across, loose);
                }
                else if (!Restore(arm, arm.Joints[i], i)) continue;
                var joint = arm.Joints[i];
                if (joint == null) continue;
                joint.rotation = Quaternion.AngleAxis(side * bend[i % 3] * amount, across) * joint.rotation;
                arm.Set[i] = joint.localRotation;
            }
            var thumb = (arm.Fist ? FistThumb : GripThumb) * amount;
            // The humanoid three only: the rig's further thumb bones keep their pose relative to them.
            for (var i = 0; i < 3; i++)
                Turn(arm, arm.Thumb[i], arm.Joints.Length + i, Quaternion.AngleAxis(-side * thumb, along));
            if (arm.Fist) ThumbOnMiddle(arm, frame * Vector3.right * side, amount);
            else if (tool) ThumbOnIndex(arm, frame * Vector3.right * side, amount);
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

        // On a tool, the thumb's base turned so that its tip lies over the middle of the index finger's middle bone, out
        // from it by ThumbOver, as when holding a handle (Raik's photos).
        private static void ThumbOnIndex(Arm arm, Vector3 palm, float share)
        {
            var root = arm.Thumb[0];
            var last = arm.Thumb.LastOrDefault(j => j != null);
            var from = arm.Joints[1];
            var to = arm.Joints[2];
            if (root == null || last == null || from == null || to == null) return;
            var tip = last.childCount > 0 ? last.GetChild(0).position : last.position;
            var target = (from.position + to.position) * 0.5f + palm * ThumbOver * VrCamera.WorldScale;
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

        // Test (Raik): seen from above a finger runs straight, not in an arc. Its middle and last bone turned sideways (towards
        // or away from the thumb side) until they lean as far to that side as the bone before them, by that share.
        private static void InLine(Arm arm, int index, Vector3 across, float share)
        {
            if (index % 3 == 0 || share <= 0f) return;
            var joint = arm.Joints[index];
            var before = arm.Joints[index - 1];
            if (joint == null || before == null) return;
            var next = index % 3 < 2 ? arm.Joints[index + 1] : joint.childCount > 0 ? joint.GetChild(0) : null;
            if (next == null) return;
            var bone = next.position - joint.position;
            var last = joint.position - before.position;
            if (bone.sqrMagnitude <= 1e-10f || last.sqrMagnitude <= 1e-10f) return;
            var axis = Vector3.Cross(bone, across);
            if (axis.sqrMagnitude <= 1e-10f) return;
            var lean = Mathf.Asin(Mathf.Clamp(Vector3.Dot(bone.normalized, across), -1f, 1f));
            var lastLean = Mathf.Asin(Mathf.Clamp(Vector3.Dot(last.normalized, across), -1f, 1f));
            joint.rotation = Quaternion.AngleAxis(-(lean - lastLean) * Mathf.Rad2Deg * share, axis.normalized) * joint.rotation;
            arm.Set[index] = joint.localRotation;
        }

        // Each finger, straight as Straighten left it, curls from the first joint whose next bone has no surface under it:
        // there it reaches over an edge. Found on the straight finger, so the curl does not change what is found.
        private static void OverEdges(Arm arm, Vector3 palm, Vector3 across, float side, float share)
        {
            var body = arm.Wrist.root;
            var probe = EdgeProbe * VrCamera.WorldScale;
            // Edges are looked for only once the hand lies on the surface: while it still goes there (GripBlend, Press),
            // every finger found nothing under it and curled (the log, 2026-11-13: all four at once on each hold).
            var arrived = arm.Pressed ? arm.Press >= 1f : arm.GripBlend >= 1f;
            for (var finger = 0; finger < arm.Joints.Length / 3; finger++)
            {
                var over = false;
                for (var k = 0; k < 3; k++)
                {
                    var index = finger * 3 + k;
                    var joint = arm.Joints[index];
                    var next = k < 2 ? arm.Joints[index + 1] : joint != null && joint.childCount > 0 ? joint.GetChild(0) : null;
                    if (!over && arrived && next != null && !Supported(next.position, palm, probe, body))
                    {
                        over = true;
                        if (arm.Edge[index] <= 0f && edgeLogs++ < 40)
                            Plugin.Log.LogInfo($"Hands: {arm.Name} {joint?.name} reaches over an edge ({(arm.Pressed ? "pressed" : "held")})");
                    }
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

        // Each finger with the surface under it (not curled over an edge) bends by ClawBend, scaled down until its tip's
        // capsule (VrHandShape) presses at most ClawPress into the surface, so it adapts to how far off it the hand is.
        // The first time per hold, each finger's share is logged (Raik: the ring finger seemed pressed down too often).
        private static void Claw(Arm arm, Vector3 palm, Vector3 across, float side, float share)
        {
            if (share <= 0f)
            {
                arm.ClawLogged = false;
                return;
            }
            VrHandShape.Gather(arm.Wrist.position, WallReach * VrCamera.WorldScale, arm.Wrist.root);
            var log = share >= 1f && !arm.ClawLogged && clawLogs < 30 ? new System.Text.StringBuilder() : null;
            var saved = new Quaternion[3];
            for (var finger = 0; finger < arm.Joints.Length / 3; finger++)
            {
                var first = finger * 3;
                var edge = Mathf.Max(arm.Edge[first], Mathf.Max(arm.Edge[first + 1], arm.Edge[first + 2]));
                var weight = share * (1f - edge);
                if (weight <= 0f || !Chain(arm, finger)) continue;
                for (var k = 0; k < 3; k++) saved[k] = arm.Joints[first + k].localRotation;
                var reach = 1f;
                if (TipIn(arm, finger, saved, across, side, weight, 1f) > ClawPress * VrCamera.WorldScale)
                {
                    var low = 0f;
                    for (var step = 0; step < 8; step++)
                    {
                        var middle = (low + reach) * 0.5f;
                        if (TipIn(arm, finger, saved, across, side, weight, middle) > ClawPress * VrCamera.WorldScale) reach = middle; else low = middle;
                    }
                    reach = low;
                }
                ClawFinger(arm, finger, saved, across, side, weight * reach);
                for (var k = 0; k < 3; k++) arm.Set[first + k] = arm.Joints[first + k].localRotation;
                if (log != null) log.Append($" {arm.Joints[first].name} {reach:0.00}{(edge > 0f ? " (edge)" : "")} {Under(arm, arm.Joints[first].position, palm)}");
            }
            if (log != null)
            {
                arm.ClawLogged = true;
                clawLogs++;
                Plugin.Log.LogInfo($"Hands: {arm.Name} fingertips on the wall, each finger's share of ClawBend:{log}");
            }
        }

        private static int clawLogs;

        // For the log: how far the surface lies under that point towards the palm, in cm, or '-' within 10 cm none.
        private static string Under(Arm arm, Vector3 point, Vector3 palm)
        {
            var count = Physics.RaycastNonAlloc(point - palm * 0.02f * VrCamera.WorldScale, palm, WallHits, 0.12f * VrCamera.WorldScale, Physics.DefaultRaycastLayers, QueryTriggerInteraction.Ignore);
            var nearest = float.MaxValue;
            for (var i = 0; i < count; i++)
                if (!WallHits[i].collider.transform.IsChildOf(arm.Wrist.root)) nearest = Mathf.Min(nearest, WallHits[i].distance);
            return nearest == float.MaxValue ? "(-)" : $"({(nearest - 0.02f * VrCamera.WorldScale) * 100f:0.0} cm)";
        }

        private static bool Chain(Arm arm, int finger)
        {
            var last = arm.Joints[finger * 3 + 2];
            return arm.Joints[finger * 3] != null && arm.Joints[finger * 3 + 1] != null && last != null && last.childCount > 0;
        }

        private static void ClawFinger(Arm arm, int finger, Quaternion[] saved, Vector3 across, float side, float share)
        {
            for (var k = 0; k < 3; k++) arm.Joints[finger * 3 + k].localRotation = saved[k];
            for (var k = 0; k < 3; k++)
            {
                var joint = arm.Joints[finger * 3 + k];
                joint.rotation = Quaternion.AngleAxis(side * ClawBend[k] * share, across) * joint.rotation;
            }
        }

        // How deep the finger's tip bone is in the surface with ClawBend by that share.
        private static float TipIn(Arm arm, int finger, Quaternion[] saved, Vector3 across, float side, float weight, float reach)
        {
            ClawFinger(arm, finger, saved, across, side, weight * reach);
            var last = arm.Joints[finger * 3 + 2];
            var depth = VrHandShape.Capsule(last.position, last.GetChild(0).position, VrHandShape.FingerRadius[finger] * VrCamera.WorldScale, out _);
            for (var k = 0; k < 3; k++) arm.Joints[finger * 3 + k].localRotation = saved[k];
            return depth;
        }

        // The hand's shape in its own axes for Touch, from the fingers as straightened, before they bend back.
        private static void Remember(Arm arm)
        {
            if (!Frame(arm, out var wrist, out var frame)) return;
            var back = Quaternion.Inverse(frame);
            for (var finger = 0; finger < 4; finger++)
            {
                if (!Chain(arm, finger)) return;
                arm.ShapeKnuckle[finger] = back * (arm.Joints[finger * 3].position - wrist);
                arm.ShapeTip[finger] = back * (arm.Joints[finger * 3 + 2].GetChild(0).position - wrist);
            }
            arm.ShapeKnown = true;
        }

        // The loose empty hand's fingers bent back at the knuckles: all as far as Touch found, then each finger that is
        // still in something (its bones' capsules, VrHandShape) further, as far as it needs, up to MostBack in all.
        private static void BendBack(Arm arm, Vector3 across, float side)
        {
            var state = (arm == LeftArm ? VrHand.Left : VrHand.Right).State;
            if (state != HandState.Empty || !arm.Driven || arm.Weight <= 0f) return;
            VrHandShape.Gather(arm.Wrist.position, WallReach * VrCamera.WorldScale, arm.Wrist.root);
            for (var finger = 0; finger < 4; finger++)
            {
                if (!Chain(arm, finger)) continue;
                var root = arm.Joints[finger * 3];
                if (arm.Back > 0f) root.rotation = Quaternion.AngleAxis(-side * arm.Back * arm.Weight, across) * root.rotation;
                var room = MostBack - arm.Back;
                if (room > 0.5f && FingerIn(arm, finger))
                {
                    var start = root.localRotation;
                    var least = 0f;
                    var most = room;
                    for (var step = 0; step < 6; step++)
                    {
                        var middle = (least + most) * 0.5f;
                        root.localRotation = start;
                        root.rotation = Quaternion.AngleAxis(-side * middle, across) * root.rotation;
                        if (FingerIn(arm, finger)) least = middle; else most = middle;
                    }
                    root.localRotation = start;
                    root.rotation = Quaternion.AngleAxis(-side * most * arm.Weight, across) * root.rotation;
                    if (backLogs++ < 20) Plugin.Log.LogInfo($"Hands: {arm.Name} {root.name} bent back {arm.Back + most:0}° at a wall");
                }
                arm.Set[finger * 3] = root.localRotation;
            }
        }

        private static int backLogs;

        // Whether any bone of the finger, as a capsule, is in something.
        private static bool FingerIn(Arm arm, int finger)
        {
            var radius = VrHandShape.FingerRadius[finger] * VrCamera.WorldScale;
            for (var k = 0; k < 3; k++)
            {
                var joint = arm.Joints[finger * 3 + k];
                var next = k < 2 ? arm.Joints[finger * 3 + k + 1] : joint.GetChild(0);
                if (VrHandShape.Capsule(joint.position, next.position, radius, out _) > 0.001f) return true;
            }
            return false;
        }

        private static int edgeLogs;

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
        private static void LiftThumb(Arm arm, int index, Vector3 palm, float share, float lean)
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
            var into = 90f - Vector3.Angle(bone, palm) - lean;
            joint.rotation = Quaternion.AngleAxis(-into * share, axis.normalized) * joint.rotation;
            arm.Set[at] = joint.localRotation;
        }

        // Test (Raik, 2026-11-13: the right thumb kinked, the left not, both lying in the palm's plane alike): the right hand's
        // animation (it carries the tools) bends the thumb's outer bones sideways in that plane. Each bone after the base
        // turned about the palm's normal until it runs on as the one before it, seen from above, by that share. The first
        // few times per hand the bends found are logged, so left and right compare.
        private static void ThumbInLine(Arm arm, Vector3 palm, float share, bool lifted)
        {
            if (share <= 0f) return;
            var found = new System.Text.StringBuilder();
            for (var index = 1; index < arm.Thumb.Length; index++)
            {
                var joint = arm.Thumb[index];
                var before = arm.Thumb[index - 1];
                if (joint == null || before == null) continue;
                var next = index + 1 < arm.Thumb.Length && arm.Thumb[index + 1] != null ? arm.Thumb[index + 1]
                    : joint.childCount > 0 ? joint.GetChild(0) : null;
                if (next == null) continue;
                var bone = Vector3.ProjectOnPlane(next.position - joint.position, palm);
                var last = Vector3.ProjectOnPlane(joint.position - before.position, palm);
                if (bone.sqrMagnitude <= 1e-12f || last.sqrMagnitude <= 1e-12f) continue;
                var at = arm.Joints.Length + index;
                // On top of LiftThumb this frame, or else from the animation's pose.
                if (!lifted && !Restore(arm, joint, at)) continue;
                var bend = Vector3.SignedAngle(last, bone, palm);
                found.Append($" {joint.name} {bend:0}°");
                joint.rotation = Quaternion.AngleAxis(-bend * share, palm) * joint.rotation;
                arm.Set[at] = joint.localRotation;
            }
            if (arm.ThumbBendLogs >= 4 || found.Length == 0) return;
            arm.ThumbBendLogs++;
            Plugin.Log.LogInfo($"Hands: {arm.Name} thumb bent sideways in the palm's plane:{found}");
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
            if (!vrHand.HoldsTool) gripLocal += aimLocal * new Vector3(arm == LeftArm ? -PalmShift : PalmShift, 0f, 0f);
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
            Touch(arm, ref wrist, ref frame);
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
                        ? new Vector3(-side * (PalmThickness + ClawLift), 0f, -PalmLength) * VrCamera.WorldScale
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
            arm.WallMet = false;
            if (state != HandState.Empty) return wrist;
            var shoulder = arm.Upper.position;
            var way = wrist - shoulder;
            var length = way.magnitude;
            if (length < 0.01f) return wrist;
            var direction = way / length;
            var count = Physics.SphereCastNonAlloc(shoulder, WristRadius, direction, WallHits, length, Physics.DefaultRaycastLayers, QueryTriggerInteraction.Ignore);
            var free = length;
            var body = arm.Wrist.root;
            var first = -1;
            for (var i = 0; i < count; i++)
            {
                var hit = WallHits[i];
                // Hits from where the cast starts inside something have no place on the way.
                if (hit.distance > 0f && hit.distance < free && !hit.collider.transform.IsChildOf(body))
                {
                    free = hit.distance;
                    first = i;
                }
            }
            if (first < 0) return wrist;
            arm.WallMet = true;
            arm.WallPoint = WallHits[first].point;
            arm.WallNormal = WallHits[first].normal;
            arm.WallDepth = Vector3.Dot(wrist - WallHits[first].point, WallHits[first].normal);
            // The hand slides along the surface instead of back along the arm (Raik, 2026-11-13: looking at a wall at a
            // slant, the hand was pushed towards the body, though the controller was out in front): out of it along its
            // normal, to WristRadius off its plane. Only if the way there is free, else back along the arm as before.
            var wall = WallHits[first];
            var into = WristRadius - Vector3.Dot(wrist - wall.point, wall.normal);
            if (into > 0f)
            {
                var slid = wrist + wall.normal * into;
                var toSlid = slid - shoulder;
                var reach = toSlid.magnitude;
                if (reach > 0.01f)
                {
                    var blocked = false;
                    var again = Physics.SphereCastNonAlloc(shoulder, WristRadius * 0.9f, toSlid / reach, WallHits, reach, Physics.DefaultRaycastLayers, QueryTriggerInteraction.Ignore);
                    for (var i = 0; i < again && !blocked; i++)
                        blocked = WallHits[i].distance > 0f && WallHits[i].distance < reach - 0.005f && !WallHits[i].collider.transform.IsChildOf(body);
                    if (!blocked)
                    {
                        if (slideLogs++ < 10) Plugin.Log.LogInfo($"Hands: {arm.Name} hand slides along '{wall.collider.name}', {Vector3.Distance(slid, wrist) * 100f:0.0} cm off the controller (along the arm it would be {(length - free) * 100f:0.0} cm)");
                        return slid;
                    }
                }
            }
            return shoulder + direction * free;
        }

        private static int pressLogs;

        // An empty hand at a surface (Raik, 2026-11-13). Its palm within FlatFrom of a surface it faces: it turns flat onto it,
        // the knuckles along the controller's aim (FlatFrame), and lies on it, the wrist PalmThickness off it; Curl
        // straightens its fingers and curls them over edges as on a hold. Fingers first into it: they bend back (Back), then
        // the hand folds over (Fold). Its back or side against it: the hand stops there. Then the palm's box is pushed out
        // of anything it is still in (a corner, an edge). Flat blended in and out over CurlTime.
        private static void Touch(Arm arm, ref Vector3 wrist, ref Quaternion frame)
        {
            var side = arm == LeftArm ? 1f : -1f;
            var state = (arm == LeftArm ? VrHand.Left : VrHand.Right).State;
            var scale = VrCamera.WorldScale;
            arm.Back = 0f;
            arm.Folded = 0f;
            var pressed = false;
            var empty = state == HandState.Empty && arm.Driven && arm.Wrist != null;
            if (empty) VrHandShape.Gather(wrist, WallReach * scale, arm.Wrist.root);
            if (empty && arm.ShapeKnown && Wall(arm, wrist, frame, side, out var point, out var normal))
            {
                var facing = Vector3.Dot(frame * Vector3.right * side, -normal);
                var gap = Vector3.Dot(wrist + frame * (new Vector3(side * PalmThickness, 0f, PalmLength) * scale) - point, normal);
                pressed = arm.Pressed && facing > PressFacing - 0.15f && gap < (FlatFrom + FlatRelease) * scale;
                if (!pressed && facing > 0f)
                {
                    pressed = Fold(arm, wrist, ref frame, side, point, normal);
                    if (!pressed && arm.Folded <= 0f && facing > PressFacing && gap < FlatFrom * scale) pressed = true;
                    // Folded over until it can fold no further: lying flat goes on from as far as it was folded.
                    if (pressed) arm.Press = Mathf.Max(arm.Press, arm.FoldedBefore);
                }
                else if (!pressed)
                {
                    var least = Mathf.Min(Tips(arm, wrist, frame, side, 0f, point, normal), Rest(arm, wrist, frame, side, point, normal));
                    least = Mathf.Min(least, Vector3.Dot(wrist + frame * (new Vector3(-side * 0.012f, 0f, PalmLength * 0.5f) * scale) - point, normal));
                    if (least < 0f) wrist -= normal * least;
                }
                if (pressed)
                {
                    arm.WallPoint = point;
                    arm.WallNormal = normal;
                }
            }
            if (pressed && !arm.Pressed && pressLogs++ < 20) Plugin.Log.LogInfo($"Hands: {arm.Name} hand lies flat on a surface");
            if (arm.Folded > 0f && foldLogs++ < 20) Plugin.Log.LogInfo($"Hands: {arm.Name} hand folds over onto a surface, {arm.Folded * 100f:0}% of the way, fingers {arm.Back:0}° back");
            arm.Pressed = pressed;
            arm.FoldedBefore = arm.Folded;
            arm.Press = Mathf.MoveTowards(arm.Press, pressed ? 1f : 0f, Time.unscaledDeltaTime / CurlTime);
            if (arm.Press > 0f)
            {
                frame = Quaternion.Slerp(frame, FlatFrame(arm, frame, arm.WallNormal), arm.Press);
                var off = Vector3.Dot(wrist - arm.WallPoint, arm.WallNormal);
                wrist += arm.WallNormal * ((PalmThickness * scale - off) * arm.Press);
            }
            if (empty && arm.Press < 1f) PalmOut(arm, ref wrist, frame, side);
        }

        private static int foldLogs;
        private static int palmLogs;

        // The nearest surface the hand may meet: looked for from the wrist along the fingers, towards the palm, between
        // both, and towards the back and the side of the hand; the one the hand comes closest to (Tips, Rest) is taken.
        private static bool Wall(Arm arm, Vector3 wrist, Quaternion frame, float side, out Vector3 point, out Vector3 normal)
        {
            point = Vector3.zero;
            normal = Vector3.zero;
            var best = float.MaxValue;
            var body = arm.Wrist.root;
            var reach = WallReach * VrCamera.WorldScale;
            var palm = frame * Vector3.right * side;
            var along = frame * Vector3.forward;
            for (var w = 0; w < 5; w++)
            {
                var way = w == 0 ? along : w == 1 ? palm : w == 2 ? (along + palm).normalized : w == 3 ? -palm : (along - palm).normalized;
                var count = Physics.RaycastNonAlloc(wrist, way, WallHits, reach, Physics.DefaultRaycastLayers, QueryTriggerInteraction.Ignore);
                for (var i = 0; i < count; i++)
                {
                    var hit = WallHits[i];
                    if (hit.distance <= 0f || Vector3.Dot(hit.normal, way) >= 0f || !VrHandShape.Stops(hit.collider, body)) continue;
                    var least = Mathf.Min(Tips(arm, wrist, frame, side, 0f, hit.point, hit.normal), Rest(arm, wrist, frame, side, hit.point, hit.normal));
                    if (least < best)
                    {
                        best = least;
                        point = hit.point;
                        normal = hit.normal;
                    }
                }
            }
            return best < (FlatFrom + FlatRelease + 0.03f) * VrCamera.WorldScale;
        }

        // The fingers' tips' least distance off a wall, less their radius, with the fingers bent back at the knuckles.
        private static float Tips(Arm arm, Vector3 wrist, Quaternion frame, float side, float back, Vector3 point, Vector3 normal)
        {
            var bend = Quaternion.AngleAxis(-side * back, Vector3.up);
            var least = float.MaxValue;
            for (var i = 0; i < 4; i++)
            {
                var tip = arm.ShapeKnuckle[i] + bend * (arm.ShapeTip[i] - arm.ShapeKnuckle[i]);
                least = Mathf.Min(least, Vector3.Dot(wrist + frame * tip - point, normal) - VrHandShape.FingerRadius[i] * VrCamera.WorldScale);
            }
            return least;
        }

        // The rest of the hand's least distance off a wall: the knuckles, less the fingers' radius, the palm's middle and
        // its heel.
        private static float Rest(Arm arm, Vector3 wrist, Quaternion frame, float side, Vector3 point, Vector3 normal)
        {
            var scale = VrCamera.WorldScale;
            var least = float.MaxValue;
            for (var i = 0; i < 4; i++)
                least = Mathf.Min(least, Vector3.Dot(wrist + frame * arm.ShapeKnuckle[i] - point, normal) - VrHandShape.FingerRadius[i] * scale);
            least = Mathf.Min(least, Vector3.Dot(wrist + frame * (new Vector3(side * PalmThickness, 0f, PalmLength) * scale) - point, normal));
            least = Mathf.Min(least, Vector3.Dot(wrist + frame * (new Vector3(side * PalmThickness, 0f, 0.02f) * scale) - point, normal));
            return least;
        }

        // Fingers first at a wall the palm faces: bent back as far as needed (at most MostBack); if that is not enough, the
        // hand turned from the controller's way towards lying flat (FlatFrame) as far as it must, the fingers again bent
        // back as needed. True when even that does not keep it out: then it lies flat (Touch).
        private static bool Fold(Arm arm, Vector3 wrist, ref Quaternion frame, float side, Vector3 point, Vector3 normal)
        {
            if (Mathf.Min(Tips(arm, wrist, frame, side, 0f, point, normal), Rest(arm, wrist, frame, side, point, normal)) >= 0f) return false;
            var flat = FlatFrame(arm, frame, normal);
            var found = -1f;
            if (Fits(arm, wrist, frame, side, point, normal)) found = 0f;
            else
            {
                var before = 0f;
                for (var k = 1; k <= 20 && found < 0f; k++)
                {
                    var at = k / 20f;
                    if (!Fits(arm, wrist, Quaternion.Slerp(frame, flat, at), side, point, normal))
                    {
                        before = at;
                        continue;
                    }
                    var low = before;
                    var high = at;
                    for (var step = 0; step < 6; step++)
                    {
                        var middle = (low + high) * 0.5f;
                        if (Fits(arm, wrist, Quaternion.Slerp(frame, flat, middle), side, point, normal)) high = middle; else low = middle;
                    }
                    found = high;
                }
            }
            if (found < 0f) return true;
            frame = Quaternion.Slerp(frame, flat, found);
            arm.Folded = found;
            if (Tips(arm, wrist, frame, side, 0f, point, normal) >= 0f) return false;
            var least = 0f;
            var most = MostBack;
            for (var step = 0; step < 8; step++)
            {
                var middle = (least + most) * 0.5f;
                if (Tips(arm, wrist, frame, side, middle, point, normal) >= 0f) most = middle; else least = middle;
            }
            arm.Back = most;
            return false;
        }

        private static bool Fits(Arm arm, Vector3 wrist, Quaternion frame, float side, Vector3 point, Vector3 normal)
            => Rest(arm, wrist, frame, side, point, normal) >= 0f && Tips(arm, wrist, frame, side, MostBack, point, normal) >= 0f;

        // The palm as a box from the wrist to the knuckles, as wide as the knuckles and as thick as the palm, pushed out of
        // what it is still in (a corner or an edge the surface found by Wall does not cover), in up to three steps.
        private static void PalmOut(Arm arm, ref Vector3 wrist, Quaternion frame, float side)
        {
            if (!arm.ShapeKnown) return;
            var scale = VrCamera.WorldScale;
            var length = arm.ShapeKnuckle[1].z;
            var width = Mathf.Abs(arm.ShapeKnuckle[0].y - arm.ShapeKnuckle[3].y) + 2f * VrHandShape.FingerRadius[1] * scale;
            if (length <= 0.01f * scale) return;
            var palmSide = PalmThickness * 0.9f * scale;
            var backSide = 0.01f * scale;
            var middle = new Vector3(side * (palmSide - backSide) * 0.5f, (arm.ShapeKnuckle[0].y + arm.ShapeKnuckle[3].y) * 0.5f, length * 0.5f);
            var half = new Vector3((palmSide + backSide) * 0.5f, width * 0.5f, length * 0.5f);
            for (var step = 0; step < 3; step++)
            {
                var depth = VrHandShape.Box(wrist + frame * middle, frame, half, out var way);
                if (depth <= 0.0005f) return;
                wrist += way * depth;
                if (palmLogs++ < 20) Plugin.Log.LogInfo($"Hands: {arm.Name} palm pushed {depth * 100f:0.0} cm out of something");
            }
        }

        private static int slideLogs;

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

        // Test (0.30.0, Raik): the hand holding the demo charges keeps the game's own finger pose. With the charge in the
        // left hand, that hand's fingers take the right hand's animated pose instead of Curl's grip, mirrored in the hands'
        // own axes (Frame): each bone points as the right one does, from the knuckles to the tips. Read before Curl bends
        // the right hand (around a second tool).
        private static readonly Vector3[] chargeBones = new Vector3[17];
        private static bool chargeBonesRead;
        private static bool chargeFingersLogged;

        private static void ReadChargeFingers(bool left)
        {
            chargeBonesRead = false;
            if (!left || !Frame(RightArm, out _, out var frame)) return;
            var toRight = Quaternion.Inverse(frame);
            for (var i = 0; i < chargeBones.Length; i++)
            {
                var bone = Bone(RightArm, i);
                chargeBones[i] = bone.sqrMagnitude > 1e-10f ? toRight * bone : Vector3.zero;
            }
            chargeBonesRead = true;
        }

        private static void MirrorChargeFingers()
        {
            if (!chargeBonesRead || LeftArm.Weight <= 0f || !Frame(LeftArm, out _, out var frame)) return;
            var turned = 0;
            // Root to tip within each finger, as the arrays run: a bone's direction depends only on its own joint's turn.
            for (var i = 0; i < chargeBones.Length; i++)
            {
                var want = chargeBones[i];
                if (want == Vector3.zero) continue;
                var joint = Joint(LeftArm, i);
                if (!Restore(LeftArm, joint, i)) continue;
                var bone = Bone(LeftArm, i);
                if (bone.sqrMagnitude <= 1e-10f) continue;
                want.x = -want.x;
                var turn = Quaternion.FromToRotation(bone, frame * want);
                joint.rotation = Quaternion.Slerp(Quaternion.identity, turn, LeftArm.Weight) * joint.rotation;
                LeftArm.Set[i] = joint.localRotation;
                turned++;
            }
            if (chargeFingersLogged) return;
            chargeFingersLogged = true;
            Plugin.Log.LogInfo($"Hands: left hand takes the demo charge's finger pose from the right hand ({turned} joints)");
        }

        private static Transform Joint(Arm arm, int index) =>
            index < arm.Joints.Length ? arm.Joints[index] : arm.Thumb[index - arm.Joints.Length];

        // From a finger joint to the next one, or at the tip to its child.
        private static Vector3 Bone(Arm arm, int index)
        {
            var joint = Joint(arm, index);
            if (joint == null) return Vector3.zero;
            Transform next = null;
            if (index < arm.Joints.Length)
            {
                if (index % 3 < 2) next = arm.Joints[index + 1];
            }
            else
            {
                var thumb = index - arm.Joints.Length;
                if (thumb + 1 < arm.Thumb.Length) next = arm.Thumb[thumb + 1];
            }
            if (next == null && joint.childCount > 0) next = joint.GetChild(0);
            return next != null ? next.position - joint.position : Vector3.zero;
        }

        // Test (Raik): shorter fingers. Each finger's base joint (and the thumb's) scaled as a whole, so the finger keeps its
        // shape and only gets smaller; the palm stays as it is. The index finger to at most the ring finger's length,
        // measured once from the game's skeleton, and IndexShorter more. Every frame, in case anything sets the scale back.
        private static void Shorten(Arm arm)
        {
            if (arm.IndexScale <= 0f)
            {
                var index = Length(arm, 0);
                var ring = Length(arm, 6);
                if (index <= 0f || ring <= 0f) return;
                arm.IndexScale = FingerLength * Mathf.Min(1f, ring / index) * IndexShorter;
                Plugin.Log.LogInfo($"Hands: {arm.Name} fingers to {FingerLength:0.00} of their length, the index finger ({index:0.000} m, ring finger {ring:0.000} m) to {arm.IndexScale:0.00}");
            }
            for (var finger = 0; finger < arm.Joints.Length / 3; finger++)
            {
                var root = arm.Joints[finger * 3];
                if (root != null) root.localScale = Vector3.one * (finger == 0 ? arm.IndexScale : FingerLength);
            }
            if (arm.Thumb[0] != null) arm.Thumb[0].localScale = Vector3.one;
            if (arm.Thumb[1] != null) arm.Thumb[1].localScale = Vector3.one * ThumbLength;
        }

        // A finger's length from its base joint to the tip of its last bone, as the skeleton has it.
        private static float Length(Arm arm, int first)
        {
            var length = 0f;
            for (var k = 0; k < 3; k++)
            {
                var joint = arm.Joints[first + k];
                if (joint == null) return 0f;
                var next = k < 2 ? arm.Joints[first + k + 1] : joint.childCount > 0 ? joint.GetChild(0) : null;
                if (next == null) return k == 2 ? length : 0f;
                length += Vector3.Distance(joint.position, next.position);
            }
            return length;
        }

        private static void LogIdle(Arm arm)
        {
            if (arm.IdleLogged) return;
            arm.IdleLogged = true;
            Plugin.Log.LogInfo($"Hands: {arm.Name} hand loose (IdleBend)");
        }

        // Test (Raik, 2026-11-13): the right thumb showed a bend in its middle the left one has not, though both lie alike
        // (snapshot: their bones' bends and angles to the palm match within a few degrees), so it twists about its own
        // bones. The right thumb takes the left one's pose as animated, mirrored in the hands' own axes (Frame), before
        // anything bends the fingers. Each bone's axes are matched through the rig's bind pose (the skinned mesh's
        // bindposes), where both thumbs are taken as mirror images: so the right thumb turns as the left one, not only
        // points the same way. Only while both arms follow their controllers and the right hand holds no tool.
        private static readonly Quaternion[] thumbMatch = new Quaternion[3];
        private static bool thumbMatchKnown;
        private static bool thumbMatchTried;

        private static void MirrorThumb(bool on)
        {
            var share = on ? Mathf.Min(LeftArm.Weight, RightArm.Weight) : 0f;
            if (share <= 0f) return;
            if (!thumbMatchTried)
            {
                thumbMatchTried = true;
                thumbMatchKnown = MatchThumbs();
            }
            if (!thumbMatchKnown || !Frame(LeftArm, out _, out var left) || !Frame(RightArm, out _, out var right)) return;
            var toLeft = Quaternion.Inverse(left);
            for (var i = 0; i < thumbMatch.Length; i++)
            {
                var from = LeftArm.Thumb[i];
                var to = RightArm.Thumb[i];
                if (from == null || to == null) continue;
                var wanted = right * Mirror(toLeft * from.rotation) * thumbMatch[i];
                to.rotation = Quaternion.Slerp(to.rotation, wanted, share);
            }
        }

        // A rotation in a hand's own axes as on the other hand: reflected through the plane of knuckles and thumb side.
        private static Quaternion Mirror(Quaternion q) => new Quaternion(q.x, -q.y, -q.z, q.w);

        // For each thumb bone, how the right one's axes sit against the mirrored left one's in the bind pose.
        private static bool MatchThumbs()
        {
            if (!BindFrame(LeftArm, out var leftMesh, out var leftFrame) || !BindFrame(RightArm, out var rightMesh, out var rightFrame))
            {
                Plugin.Log.LogInfo("Hands: thumb mirror: no bind pose found for both hands, right thumb stays as animated");
                return false;
            }
            var log = new System.Text.StringBuilder();
            for (var i = 0; i < thumbMatch.Length; i++)
            {
                if (!BindRotation(leftMesh, LeftArm.Thumb[i], out var l) || !BindRotation(rightMesh, RightArm.Thumb[i], out var r)) return false;
                var leftInHand = Quaternion.Inverse(leftFrame) * l;
                var rightInHand = Quaternion.Inverse(rightFrame) * r;
                thumbMatch[i] = Quaternion.Inverse(Mirror(leftInHand)) * rightInHand;
                thumbMatch[i].ToAngleAxis(out var angle, out var axis);
                log.Append($" {RightArm.Thumb[i].name} {angle:0}° about {axis}");
            }
            Plugin.Log.LogInfo($"Hands: thumb mirror: right thumb bones against the mirrored left ones in the bind pose:{log}");
            return true;
        }

        // The hand's own axes (as Frame) in the bind pose of the skinned mesh that carries its thumb.
        private static bool BindFrame(Arm arm, out SkinnedMeshRenderer mesh, out Quaternion frame)
        {
            frame = Quaternion.identity;
            mesh = null;
            if (arm.Thumb[0] == null || arm.Wrist == null) return false;
            foreach (var candidate in arm.Wrist.root.GetComponentsInChildren<SkinnedMeshRenderer>(true))
                if (candidate.bones != null && System.Array.IndexOf(candidate.bones, arm.Thumb[0]) >= 0
                    && System.Array.IndexOf(candidate.bones, arm.Wrist) >= 0 && System.Array.IndexOf(candidate.bones, arm.Index) >= 0
                    && System.Array.IndexOf(candidate.bones, arm.Middle) >= 0 && System.Array.IndexOf(candidate.bones, arm.Little) >= 0)
                {
                    mesh = candidate;
                    break;
                }
            if (mesh == null) return false;
            if (!BindPosition(mesh, arm.Wrist, out var wrist) || !BindPosition(mesh, arm.Index, out var index)
                || !BindPosition(mesh, arm.Middle, out var middle) || !BindPosition(mesh, arm.Little, out var little)) return false;
            var knuckles = middle - wrist;
            var thumbSide = index - little;
            if (knuckles.sqrMagnitude <= 1e-10f || thumbSide.sqrMagnitude <= 1e-10f) return false;
            frame = Quaternion.LookRotation(knuckles, thumbSide);
            return true;
        }

        private static bool BindPosition(SkinnedMeshRenderer mesh, Transform bone, out Vector3 position)
        {
            position = Vector3.zero;
            var i = System.Array.IndexOf(mesh.bones, bone);
            var poses = mesh.sharedMesh != null ? mesh.sharedMesh.bindposes : null;
            if (i < 0 || poses == null || i >= poses.Length) return false;
            position = poses[i].inverse.GetColumn(3);
            return true;
        }

        private static bool BindRotation(SkinnedMeshRenderer mesh, Transform bone, out Quaternion rotation)
        {
            rotation = Quaternion.identity;
            var i = bone == null ? -1 : System.Array.IndexOf(mesh.bones, bone);
            var poses = mesh.sharedMesh != null ? mesh.sharedMesh.bindposes : null;
            if (i < 0 || poses == null || i >= poses.Length) return false;
            rotation = poses[i].inverse.rotation;
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
