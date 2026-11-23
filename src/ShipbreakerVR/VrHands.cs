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
        // Raik (2026-11-14, a picture of the hand on the grapple): the fingers closed parallel, all bent alike, not fanning
        // out more from the index finger down to the little one.
        private static readonly float[] ToolBend = { 68f, 155f, 195f, 68f, 155f, 195f, 68f, 155f, 195f, 68f, 155f, 195f };
        // Raik (0.33.0): the empty hand that holds nothing follows the fingers on the controller (VrInput: the grip's and
        // the trigger's travel, the touch sensors of the trigger, the buttons and the stick), always, with no setting:
        //   the grip   closes the hand into its fist as far as it is pressed (VrInput.Squeezed), from the loose hand on
        //   the index finger  off the trigger it is out straight (PointBend: each joint's bone to the knuckles' line, as
        //              ToolBend, in degrees); on it, it closes with the others, and with the trigger as far as that is pulled
        //   the thumb  off the buttons and the stick it is out, and stands up out of the fist ("thumbs up")
        // So an open hand, a fist, pointing and a thumbs up are the player's own. The index finger out of a loose hand is
        // soft, as every finger of it (BendBack, KeepOut). Out of the fist, the grip held on nothing, it is stiff (Stiff).
        // A controller without the sensors keeps its fingers on: the fist as it was, only as far as the grip is pressed.
        private static readonly float[] PointBend = { 3f, 6f, 9f };
        // The loose hand's thumb, off the buttons and the stick: its base this far out, away from the palm, in degrees.
        private const float ThumbAway = 22f;
        // Thumbs up: where the thumb's tip goes from its base, in the hand's axes (Frame): up the thumb's side, a little
        // to the back of the hand and ahead.
        private const float ThumbUpBack = 0.15f;
        private const float ThumbUpAhead = 0.2f;
        // Raik (2026-11-14): on a tool the thumb lies a little above the index finger, this far towards the thumb's side.
        private const float ThumbAbove = 0.01f;
        // A second hand on a tool (VrToolSupport) closes each finger in WrapSteps until one of its bones comes within
        // FingerRadius of the tool's shape: around the grapple's housing or the cutter's body wherever it took hold.
        private const int WrapSteps = 8;
        private const float FingerRadius = 0.009f;
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
        // (Fold) until it lies flat. Raik (2026-11-14): the palm keeps following the controller while the fingers, the
        // softest part, take the movement; only once they are back KnuckleBackAtMost does the palm fold over too. The wall is looked for up to WallReach from the wrist.
        private const float FlatFrom = 0f;
        // Test (Raik, 2026-11-14): a flat hand follows what lies under its palm. PalmGrid points over the palm (from the
        // heel to the knuckles, from the index to the little finger) look for a surface from PalmLook behind the palm out
        // to PalmFind in front of it; the hand lies on the average of what they find, each weighed less the further it lies,
        // so over an edge it turns over as the palm goes over it. The turn follows within about SurfaceTurnTime.
        private const int PalmGrid = 3;
        private const float PalmLook = 0.03f;
        private const float PalmFind = 0.04f;
        private const float SurfaceTurnTime = 0.04f;
        // A surface the hand would be deeper in than IntoAtMost is a plane drawn on past its edge, not one it touches
        // (the log, 2026-11-14: hands thrown 20 cm by it); at its back or side the hand is pushed out at most BackPushAtMost.
        private const float IntoAtMost = 0.1f;
        // How long a fold, a bend back or a lift takes to go back once nothing needs it any more.
        private const float RelaxTime = 0.15f;
        // Test (2026-11-14, faces meeting at a ship's nose): another face must be this much closer to take over, and the
        // way the hand folds turns to it within about FoldTurnTime.
        private const float KeepFace = 0.006f;
        private const float FoldTurnTime = 0.06f;
        // Raik (2026-11-14): fingers bend back towards the back of the hand at most this far at the knuckle, in all; a hand
        // turned further against a surface is pushed off it instead (Touch), at most by PushOffAtMost.
        private const float PushOffAtMost = 0.06f;
        private const float KnuckleBackAtMost = 80f;
        private const float BackPushAtMost = 0.04f;
        private const float FlatRelease = 0.003f;
        private const float MostBack = KnuckleBackAtMost;
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
        private static RaycastHit[] WallHits = new RaycastHit[16];
        private const int MostWallHits = 256;

        // The arm's sweep from the shoulder to the hand: what it meets is in WallHits. A sweep that fills the room for
        // its hits is asked again with twice the room (the same as VrHandShape.Gather, bug list B5): the query returns
        // any of the hits, not the nearest, so with more debris along the arm than there was room for, the wall could be
        // the one left out.
        private static int Sweep(Vector3 from, float radius, Vector3 direction, float length)
        {
            var count = Physics.SphereCastNonAlloc(from, radius, direction, WallHits, length, Physics.DefaultRaycastLayers, QueryTriggerInteraction.Ignore);
            while (count == WallHits.Length && WallHits.Length < MostWallHits)
            {
                WallHits = new RaycastHit[WallHits.Length * 2];
                count = Physics.SphereCastNonAlloc(from, radius, direction, WallHits, length, Physics.DefaultRaycastLayers, QueryTriggerInteraction.Ignore);
            }
            return count;
        }

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
            // How far the open hand's fingers are straight at a button (VrButtons.AtButton).
            public float Straight;
            // The empty hand's own fingers (0.33.0): how far the index finger is out straight, off the trigger, how far
            // the trigger closes it, how far the thumb is out, off its buttons and the stick (each 0 to 1), and whether
            // the index finger is stiff: out of the fist, the grip held on nothing (Stiff).
            public float IndexOut;
            public float IndexPull;
            public float ThumbUp;
            public bool Pointing;
            // What the stiff index finger met this frame (Stiff): how far it holds the hand back from the controller, out
            // of what it met; where its tip is; what it met (null at a button); and the frame that was.
            public Vector3 Poke;
            public Vector3 PokeAt;
            public Collider PokeThing;
            public int PokeFrame = -1;
            public bool Poking;
            public bool PointingBefore;
            // The fingertips (index to little) as Curl left them last frame, for pushing buttons (VrButtons), and whether known.
            public readonly Vector3[] TipWorld = new Vector3[4];
            public bool TipsKnown;
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
            // The surface the flat hand lies on (Touch, PalmSurface): its own, not OutOfWalls', turned over smoothly as the
            // palm passes onto another surface.
            public Vector3 FlatPoint;
            public Vector3 FlatNormal;
            // Test (Raik, 2026-11-14: jumps, fingers flipping): what the hand shows of folding over, bending back and lifting
            // its fingers. Each goes up at once (so nothing goes into the wall) but back down only over RelaxTime, so a
            // contact found in one frame and not the next no longer flips the hand.
            public float FoldShown;
            public Vector3 FoldNormal;
            // The face the hand met last frame (Wall), kept unless another is clearly closer.
            public Collider TouchCollider;
            public Vector3 TouchNormal;
            // How far the empty hand is pushed off a surface so its fingers need not bend back past KnuckleBackAtMost.
            public float PushShown;
            public Vector3 PushNormal;
            // What Touch makes of the controller's hand as shown (Ease): the wrist's shift and the hand's turn in the
            // controller's axes, and how fast each changes.
            public Vector3 EaseShift, EaseShiftSpeed;
            public Quaternion EaseTurn = Quaternion.identity;
            public float EaseTurnSpeed;
            public float BackShown;
            public readonly float[] BendShown = new float[4];
            public readonly float[] LiftShown = new float[4];
            // The back of the hand on something (BendBack): how far each finger is folded in towards the palm, whether
            // they fold in or bend back as before, when a finger's back last touched, and where each folded finger's tip
            // rests (held there as by friction) and when that finger was last near something.
            public readonly float[] TuckShown = new float[4];
            // How far the empty hand is held back because a finger stays in something (FingerPush).
            public Vector3 FingerPush;
            public bool FoldIn = true;
            public float BackTouchAt = -10f;
            public readonly Vector3[] TuckAnchor = new Vector3[4];
            public readonly bool[] TuckHas = new bool[4];
            public readonly float[] TuckNearAt = new float[4];
            public readonly float[] UnbendShown = new float[4];
            public float FingerLogAt;
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
            // How far each finger joint (index to little; base, middle, last) and the thumb are closed around a tool held
            // as a second hand, in degrees (Wrap).
            public readonly float[] Wrap = new float[12];
            public float WrapThumb;
            // The palm folding in under the knuckles (PalmFold): each finger's base joint where the animation had it and
            // where it was set here, and how far the palm is folded now, in degrees (the front of its shape, PalmBoxes).
            public readonly Vector3[] FoldBase = new Vector3[4];
            public readonly Vector3[] FoldSet = new Vector3[4];
            public float PalmFold;
            // The side of the tool in the other hand the palm came against it from, in the tool's own axes (zero when
            // not at it), and when it last was (StayOutOfTool).
            public Vector3 ToolSide;
            public float ToolSideAt;
            // The same for the other arm, in its forearm's axes (StayOutOfArm).
            public Vector3 ArmSide;
            public float ArmSideAt;
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
        // Local test view (VrToolShapeView): the hand's collision shape as drawn now, as KeepOut and PalmOut test it: each
        // finger bone and thumb bone as a capsule (from, to, radius) and the palm's box (PalmOut). False without a hand.
        public static bool TryGetHandShape(bool left, List<Vector3> from, List<Vector3> to, List<float> radius, List<Vector3> boxCenter, List<Quaternion> boxTurn, List<Vector3> boxHalf)
        {
            var arm = left ? LeftArm : RightArm;
            boxCenter.Clear();
            boxTurn.Clear();
            boxHalf.Clear();
            if (arm.Wrist == null) return false;
            var scale = VrCamera.WorldScale;
            for (var finger = 0; finger < 4; finger++)
            {
                if (!Chain(arm, finger)) continue;
                for (var k = 0; k < 3; k++)
                {
                    var joint = arm.Joints[finger * 3 + k];
                    var next = k < 2 ? arm.Joints[finger * 3 + k + 1] : joint.GetChild(0);
                    from.Add(joint.position);
                    to.Add(next.position);
                    radius.Add(VrHandShape.FingerRadius[finger] * scale);
                }
            }
            for (var k = 0; k < arm.Thumb.Length; k++)
            {
                var joint = arm.Thumb[k];
                if (joint == null) break;
                var next = k + 1 < arm.Thumb.Length && arm.Thumb[k + 1] != null ? arm.Thumb[k + 1] : joint.childCount > 0 ? joint.GetChild(0) : null;
                if (next == null) break;
                from.Add(joint.position);
                to.Add(next.position);
                radius.Add(VrHandShape.ThumbRadius * scale);
            }
            if (!Frame(arm, out var wrist, out var frame)) return true;
            var boxes = PalmBoxes(arm, left ? 1f : -1f);
            for (var b = 0; b < boxes; b++)
            {
                boxCenter.Add(wrist + frame * PalmCenter[b]);
                boxTurn.Add(frame * PalmTurn[b]);
                boxHalf.Add(PalmHalf[b]);
            }
            return true;
        }

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
            // The knuckles back where the animation has them before anything reads the hand's axes from them (Frame).
            RestoreFold(LeftArm);
            RestoreFold(RightArm);
            var leftControl = UsesHeldControl(LeftArm);
            var rightControl = UsesHeldControl(RightArm);
            Drive(LeftArm, active && (Idle(grab.LeftHand) || leftControl));
            var rightBusy = VrToolModels.PosesRightArm;
            Drive(RightArm, active && !rightBusy && (Idle(grab.RightHand) || rightControl
                || (grab.RightHand.CurrentHandState == HandGrab.HandState.Equipped
                    && (equipment != EquipmentController.Equipment.DemoCharge || chargeLeft || VrChargeByHand.Decoupled))));
            // The demo charge's arm is bent there; another bend on top would fight it.
            if (rightBusy) RightArm.Weight = 0f;
            Fingers(LeftArm, leftWasDriven);
            Fingers(RightArm, rightWasDriven);
            Pose(LeftArm, active && chargeLeft && !VrChargeByHand.Decoupled && Mirrored(out var hold) ? hold : Quaternion.identity);
            Pose(RightArm, Quaternion.identity);
            AtControl(LeftArm);
            AtControl(RightArm);
            MirrorThumb(active && !VrHand.Right.HoldsTool);
            var detonator = active && equipment == EquipmentController.Equipment.DemoCharge
                && grab.RightHand.CurrentHandState == HandGrab.HandState.Equipped && VrChargeByHand.DetonatorOut;
            ReadChargeFingers(active && chargeLeft);
            Curl(LeftArm);
            Curl(RightArm);
            KeepTips(LeftArm);
            KeepTips(RightArm);
            MirrorChargeFingers();
            RestoreDetonator();
            RestoreLever();
            Charges(active && chargeLeft);
            ChargeInHand(active && VrChargeByHand.Decoupled && equipment == EquipmentController.Equipment.DemoCharge
                && grab.RightHand.CurrentHandState == HandGrab.HandState.Equipped);
            FitDetonator(detonator);
            PressDetonator(detonator);
        }

        // The detonator's red lever gives way under the trigger (Raik, 2026-11-18: "pressed in a few millimeters or a
        // centimeter, to see the detonating"): the lever (the prop's part '..._Trigger') swings towards the grip (its
        // part '..._Body') as far as the trigger of the hand that holds it is pulled, and that hand's fingers, which lie
        // round the lever, close with it. It turns about its round head at the top, its hinge (Raik, of his pictures
        // before and after: moved as a whole, the head went into the grip too): by up to LeverTurn degrees, the far
        // end into the grip. The hinge is the middle of the lever's head, found once from the mesh: the end of the
        // lever towards the body's middle, as wide across as the lever is there. The lever is put back before the
        // next frame's posing if nothing has set it since; the fingers through their own bookkeeping (Base, Set).
        private const float LeverTurn = 12f;
        // The head is looked for within HeadSlice of the lever's end; HeadRadius if the mesh cannot be read (meters).
        private const float HeadSlice = 0.02f;
        private const float HeadRadius = 0.013f;
        // Degrees more at the knuckle, the middle and the last joint of each finger, with the trigger pulled through.
        private static readonly float[] SqueezeBend = { 3f, 10f, 7f };
        // The game's own hold has the finger tips in the lever (Raik's screenshot of 2026-11-18 18:32, "from the start,
        // nothing to do with you"): with the detonator in the hand the fingers are that much straighter, in degrees at
        // the knuckle, the middle and the last joint, so their tips lie on the lever's front.
        private static readonly float[] LeverReach = { 0f, 6f, 12f };
        private static Transform leverPart;
        // In the lever's own axes: its hinge and the axis it turns about (towards the grip for a positive angle).
        private static Vector3 leverHinge, leverAxis;
        private static Vector3 leverRest, leverSetAt;
        private static Quaternion leverRestTurn, leverSetTurn;
        private static bool leverLooked, leverSet;

        private static void RestoreLever()
        {
            if (!leverSet) return;
            leverSet = false;
            if (leverPart != null && leverPart.localPosition == leverSetAt && leverPart.localRotation == leverSetTurn)
                SetLocal(leverPart, leverRest, leverRestTurn);
        }

        // The lever's hinge and axis from its mesh, in the lever's own axes; way: towards the grip, in the world.
        private static void FindHinge(MeshFilter lever, MeshFilter body, Vector3 way)
        {
            var frame = lever.transform;
            var bounds = lever.sharedMesh.bounds;
            var size = bounds.size;
            var length = size.x >= size.y && size.x >= size.z ? Vector3.right : size.y >= size.z ? Vector3.up : Vector3.forward;
            // The head is at the end towards the body's middle (the body's box sits above the grip).
            var toBody = frame.InverseTransformPoint(body.transform.TransformPoint(body.sharedMesh.bounds.center)) - bounds.center;
            var head = Vector3.Dot(toBody, length) >= 0f ? length : -length;
            var toGrip = frame.InverseTransformDirection(way).normalized;
            var flat = Vector3.Cross(head, toGrip).normalized;
            var meters = Mathf.Max(Mathf.Abs(frame.lossyScale.x), 1e-4f) / VrCamera.WorldScale;
            var top = Vector3.Dot(bounds.center, head) + Mathf.Abs(Vector3.Dot(bounds.extents, length));
            var radius = HeadRadius / meters;
            var across = Vector3.Dot(bounds.center, toGrip);
            var read = 0;
            try
            {
                var points = lever.sharedMesh.vertices;
                if (points != null && points.Length > 0)
                {
                    top = float.MinValue;
                    foreach (var point in points) top = Mathf.Max(top, Vector3.Dot(point, head));
                    float least = float.MaxValue, most = float.MinValue;
                    foreach (var point in points)
                    {
                        if (Vector3.Dot(point, head) < top - HeadSlice / meters) continue;
                        var at = Vector3.Dot(point, toGrip);
                        least = Mathf.Min(least, at);
                        most = Mathf.Max(most, at);
                        read++;
                    }
                    if (read > 0 && most > least)
                    {
                        radius = (most - least) * 0.5f;
                        across = (most + least) * 0.5f;
                    }
                }
            }
            catch (System.Exception)
            {
                read = 0;
            }
            leverHinge = head * (top - radius) + toGrip * across + flat * Vector3.Dot(bounds.center, flat);
            // From the hinge down the lever, turned towards the grip.
            leverAxis = Vector3.Cross(-head, toGrip).normalized;
            Plugin.Log.LogInfo($"Hands: the detonator's lever '{lever.name}' turns about its head under the trigger, up to {LeverTurn:0}°: hinge {radius * meters * 1000f:0} mm below its end "
                + $"({(read > 0 ? $"the head {radius * meters * 2000f:0} mm across, from {read} points of the mesh" : "the mesh not readable, a head of the usual size")}), "
                + $"the lever {Mathf.Abs(Vector3.Dot(size, length)) * meters * 100f:0.0} cm long, axis ({leverAxis.x:0.00}, {leverAxis.y:0.00}, {leverAxis.z:0.00}) in its own axes");
        }

        // The index and the middle finger of the hand that holds the detonator, which lie on its lever: straighter by
        // LeverReach, so their tips lie on its front, and closed with the trigger by SqueezeBend.
        private static void Squeeze(Arm arm, bool left, float pulled)
        {
            if (!Frame(arm, out _, out var frame)) return;
            // Towards the palm, about the hand's thumb-side axis, as Curl bends them; the other two fingers lie round the grip.
            var across = frame * Vector3.up;
            var side = left ? 1f : -1f;
            for (var i = 0; i < arm.Joints.Length && i < 6; i++)
            {
                var joint = arm.Joints[i];
                var bend = SqueezeBend[i % 3] * pulled - LeverReach[i % 3];
                if (joint == null || Mathf.Abs(bend) < 0.01f) continue;
                // Not set this frame (the game's own pose): that pose is its base from here.
                if (joint.localRotation != arm.Set[i]) arm.Base[i] = joint.localRotation;
                joint.rotation = Quaternion.AngleAxis(side * bend, across) * joint.rotation;
                arm.Set[i] = joint.localRotation;
            }
        }

        private static void PressDetonator(bool on)
        {
            if (!on || detonatorProp == null) return;
            var left = VrHand.GameToolHand.IsLeft;
            var arm = left ? LeftArm : RightArm;
            var pulled = Mathf.Clamp01(arm.Input.TriggerValue);
            Squeeze(arm, left, pulled);
            if (pulled <= 0.01f) return;
            if (!leverLooked)
            {
                leverLooked = true;
                MeshFilter lever = null, body = null;
                foreach (var filter in detonatorProp.GetComponentsInChildren<MeshFilter>(true))
                {
                    if (filter.sharedMesh == null) continue;
                    if (filter.name.EndsWith("_Trigger", System.StringComparison.Ordinal)) lever = filter;
                    else if (filter.name.EndsWith("_Body", System.StringComparison.Ordinal)) body = filter;
                }
                if (lever != null && body != null && lever.transform.parent != null)
                {
                    // Towards the body's middle, across the lever's own length.
                    var size = lever.sharedMesh.bounds.size;
                    var length = size.x >= size.y && size.x >= size.z ? Vector3.right : size.y >= size.z ? Vector3.up : Vector3.forward;
                    var along = lever.transform.TransformDirection(length).normalized;
                    var way = body.transform.TransformPoint(body.sharedMesh.bounds.center) - lever.transform.TransformPoint(lever.sharedMesh.bounds.center);
                    way -= Vector3.Project(way, along);
                    if (way.sqrMagnitude > 1e-8f)
                    {
                        leverPart = lever.transform;
                        FindHinge(lever, body, way);
                    }
                }
                if (leverPart == null)
                    Plugin.Log.LogInfo($"Hands: no lever to press found on the detonator ({(lever == null ? "no part '..._Trigger'" : body == null ? "no part '..._Body'" : "the two lie in line")})");
            }
            if (leverPart == null || leverPart.parent == null) return;
            leverRest = leverPart.localPosition;
            leverRestTurn = leverPart.localRotation;
            // About the hinge, which stays where it is.
            var hinge = leverPart.TransformPoint(leverHinge);
            var axis = leverPart.TransformDirection(leverAxis);
            var turn = Quaternion.AngleAxis(LeverTurn * pulled, axis);
            leverPart.SetPositionAndRotation(hinge + turn * (leverPart.position - hinge), turn * leverPart.rotation);
            leverSetAt = leverPart.localPosition;
            leverSetTurn = leverPart.localRotation;
            leverSet = true;
        }

        // The charges' detonator ('PRF_Tool_ExplosiveChargeDetonator' on the game's right hand, moved to the left hand
        // by Charges) sits in the hand that holds it as it is fitted (VrToolFit.Detonator: dragged by the other hand
        // while the tools are fitted, kept in ZuenderHaltung), in that hand's own axes (Frame). Set after Charges each
        // frame and taken back before it the next, so Charges finds the piece as it left it.
        private const string DetonatorProp = "PRF_Tool_ExplosiveChargeDetonator";
        private static Transform detonatorProp;
        private static Moved detonatorMoved;
        private static bool detonatorSet;

        private static void RestoreDetonator()
        {
            if (!detonatorSet) return;
            detonatorSet = false;
            if (detonatorProp != null && detonatorProp.localPosition == detonatorMoved.SetPosition && detonatorProp.localRotation == detonatorMoved.SetRotation)
                SetLocal(detonatorProp, detonatorMoved.Position, detonatorMoved.Rotation);
        }

        // Where the detonator lies in the game's right hand as the game has it, in that hand's own axes from the wrist
        // (meters at world scale 1): for VrBeltGrapple, which shows it at the hip turned as a hand there would hold it.
        private static Vector3 detonatorRestAt;
        private static Quaternion detonatorRestTurn = Quaternion.identity;
        private static bool detonatorRestKnown;

        internal static bool DetonatorRest(out Vector3 at, out Quaternion turn)
        {
            at = detonatorRestAt;
            turn = detonatorRestTurn;
            return detonatorRestKnown;
        }

        private static void FitDetonator(bool on)
        {
            if (detonatorProp == null && RightArm.Wrist != null)
                foreach (Transform child in RightArm.Wrist)
                    if (child.name.StartsWith(DetonatorProp, System.StringComparison.Ordinal)) detonatorProp = child;
            // Read while nothing has moved it (not out, so neither on the left hand nor fitted).
            if (!detonatorRestKnown && !on && detonatorProp != null && moved.Count == 0 && Frame(RightArm, out var restWrist, out var restFrame))
            {
                detonatorRestAt = Quaternion.Inverse(restFrame) * (detonatorProp.position - restWrist) / VrCamera.WorldScale;
                detonatorRestTurn = Quaternion.Inverse(restFrame) * detonatorProp.rotation;
                detonatorRestKnown = true;
            }
            var left = VrHand.GameToolHand.IsLeft;
            if (!on || detonatorProp == null || !Frame(left ? LeftArm : RightArm, out var wrist, out var frame))
            {
                VrToolFit.DetonatorIdle();
                return;
            }
            var scale = VrCamera.WorldScale;
            var at = Quaternion.Inverse(frame) * (detonatorProp.position - wrist) / scale;
            var turn = Quaternion.Inverse(frame) * detonatorProp.rotation;
            if (left) VrToolFit.Mirror(ref at, ref turn);
            if (!VrToolFit.Detonator(left, wrist, frame, ref at, ref turn)) return;
            if (left) VrToolFit.Mirror(ref at, ref turn);
            var original = new Moved { Position = detonatorProp.localPosition, Rotation = detonatorProp.localRotation };
            detonatorProp.SetPositionAndRotation(wrist + frame * (at * scale), frame * turn);
            original.SetPosition = detonatorProp.localPosition;
            original.SetRotation = detonatorProp.localRotation;
            detonatorMoved = original;
            detonatorSet = true;
        }

        // With the hands following their controllers (VrChargeByHand.Decoupled): the charge itself ('PRF_Demo_Charge' on
        // the game's right hand) is set in the hand that holds the charges exactly as a charge taken out of the stock by
        // hand lies against that controller (VrChargeByHand.HoldInTracking, Raik's screenshot of 2026-11-18: upright like
        // a slab, its face inwards, its back outwards, its top to the thumb's side). Its own pose under the hand comes
        // back when it is no longer set, if nothing has set it since. The detonator stays on the hand.
        private const string ChargeProp = "PRF_Demo_Charge";
        private static Transform chargeProp, chargeMesh;
        private static Moved chargeMoved;
        private static bool chargeSet;

        private static bool IsChargeProp(Transform piece) => piece != null && piece.name.StartsWith(ChargeProp, System.StringComparison.Ordinal);

        private static void ChargeInHand(bool on)
        {
            if (chargeProp == null && RightArm.Wrist != null)
            {
                foreach (Transform child in RightArm.Wrist)
                    if (IsChargeProp(child)) chargeProp = child;
                var filter = chargeProp != null ? chargeProp.GetComponentInChildren<MeshFilter>(true) : null;
                chargeMesh = filter != null ? filter.transform : null;
            }
            if (chargeProp == null || chargeMesh == null) return;
            if (chargeSet && chargeProp.localPosition == chargeMoved.SetPosition && chargeProp.localRotation == chargeMoved.SetRotation)
                SetLocal(chargeProp, chargeMoved.Position, chargeMoved.Rotation);
            chargeSet = false;
            var left = VrHand.GameToolHand.IsLeft;
            // As a charge taken out by hand lies against its controller (VrChargeByHand.HoldInTracking; Raik, 2026-11-19:
            // set against the drawn hand's wrist, it sat off the hand with Automatisch).
            if (!on || !VrChargeByHand.HoldInTracking(left, out var meshLocal, out var turnLocal)
                || !VrToolModels.TrackingToWorld(meshLocal, turnLocal, out var meshAt, out var meshTurn)) return;
            var original = new Moved { Position = chargeProp.localPosition, Rotation = chargeProp.localRotation };
            chargeProp.rotation = meshTurn * Quaternion.Inverse(Quaternion.Inverse(chargeProp.rotation) * chargeMesh.rotation);
            chargeProp.position += meshAt - chargeMesh.position;
            original.SetPosition = chargeProp.localPosition;
            original.SetRotation = chargeProp.localRotation;
            chargeMoved = original;
            chargeSet = true;
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
            RestoreFold(arm);
            var hand = arm == LeftArm ? VrHand.Left : VrHand.Right;
            // The right hand with the game's charges or their detonator keeps the fingers as the game's animation has
            // them round it, which is the hold the left hand takes over mirrored (MirrorChargeFingers). With the charges
            // put on by hand the right hand follows its controller, and its grip closed it to the empty hand's fist
            // instead (Raik, 2026-11-18: the right hand held the detonator in another shape than the left).
            if (arm == RightArm && hand.HoldsTool && hand.Tool == EquipmentController.Equipment.DemoCharge)
            {
                arm.Closed = arm.Flat = arm.Claw = arm.Straight = 0f;
                Open(arm);
                return;
            }
            var tool = hand.HoldsTool && hand.Tool != EquipmentController.Equipment.DemoCharge;
            var grip = arm.Input.Grip.Down && hand.State != HandState.HiddenTool;
            var held = grip && VrClimb.HoldsFlat(arm.Input);
            var flat = arm.Driven && !tool && (held || (!grip && arm.Pressed));
            arm.Claw = Mathf.MoveTowards(arm.Claw, flat && held ? 1f : 0f, Time.unscaledDeltaTime / CurlTime);
            var support = arm.Driven && VrToolSupport.Supports(arm.Input);
            // The player's own hand (0.33.0): empty, holding nothing, its fingers as they are on the controller.
            var free = arm.Driven && !tool && !support && hand.State == HandState.Empty;
            var step = Time.unscaledDeltaTime / CurlTime;
            arm.IndexOut = Mathf.MoveTowards(arm.IndexOut, free && arm.Input.IndexOut ? 1f : 0f, step);
            arm.IndexPull = Mathf.MoveTowards(arm.IndexPull, free ? VrInput.Pulled(arm.Input.TriggerValue) : 0f, step);
            arm.ThumbUp = Mathf.MoveTowards(arm.ThumbUp, free && !arm.Input.ThumbOn ? 1f : 0f, step);
            arm.Pointing = free && grip && arm.Input.IndexOut;
            if (arm.Pointing && !arm.PointingBefore && pointLogs++ < 10)
                Plugin.Log.LogInfo($"Hands: {arm.Name} index finger out of the fist, off the trigger with the grip held on nothing: stiff");
            arm.PointingBefore = arm.Pointing;
            var atButton = free && (!grip || arm.Pointing) && VrButtons.AtButton(arm.Input);
            arm.Straight = Mathf.MoveTowards(arm.Straight, atButton && !arm.Pointing ? 1f : 0f, step);
            // Not on a surface it lies flat on, and not right at a button, where the open hand's fingers are straight.
            var squeeze = free && !flat && (!atButton || arm.Pointing) ? VrInput.Squeezed(arm.Input.GripValue) : 0f;
            var closing = arm.Driven && (tool || support || (grip && !flat) || squeeze > 0f);
            if (closing) arm.Fist = !tool && !support && (hand.State != HandState.Holding || VrClimb.HoldsControl(arm.Input));
            if (!support)
            {
                System.Array.Clear(arm.Wrap, 0, arm.Wrap.Length);
                arm.WrapThumb = 0f;
            }
            // Raik (0.33.0): the fist of a hand that holds nothing closes as far as the grip is pressed.
            arm.Closed = Mathf.MoveTowards(arm.Closed, free ? squeeze : closing ? 1f : 0f, step);
            arm.Flat = Mathf.MoveTowards(arm.Flat, flat ? 1f : 0f, step);
            if (!flat)
            {
                arm.ThumbLogged = false;
                System.Array.Clear(arm.Edge, 0, arm.Edge.Length);
            }
            var amount = arm.Closed * arm.Weight;
            var flatness = arm.Flat * arm.Weight;
            // The loose hand fades out as the hand closes, so a grip still starts from the animation's pose.
            var loose = arm.Weight * (1f - arm.Closed);
            if ((amount <= 0f && flatness <= 0f && loose <= 0f) || !Frame(arm, out var wristAt, out var frame))
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
                    // The index finger: out straight off the trigger, closing as far as the trigger is pulled.
                    var idle = i < 3 ? Mathf.Lerp(Mathf.Lerp(IdleBend[i], ToolBend[i], arm.IndexPull), PointBend[i], arm.IndexOut) : IdleBend[i];
                    // Raik (0.30.1): right at a button the fingers are straight and push it in, bent back by nothing.
                    var toward = Mathf.Lerp(Mathf.Lerp(idle, FlatBend[i % 3], arm.Flat), FlatBend[i % 3], arm.Straight);
                    Straighten(arm, i, across, Quaternion.AngleAxis(side * toward, across) * along, across, arm.Weight);
                    InLine(arm, i, across, arm.Weight);
                }
                Remember(arm);
                if (flatness <= 0f)
                {
                    if (!atButton) BendBack(arm, across, side);
                    // The thumb off its buttons and the stick: out, away from the palm.
                    Turn(arm, arm.Thumb[0], arm.Joints.Length, Quaternion.AngleAxis(side * ThumbAway * arm.ThumbUp * arm.Weight, along));
                    ThumbInLine(arm, frame * Vector3.right * side, arm.Weight, false);
                    if (!atButton) KeepOut(arm, across, side, arm.Weight);
                    else arm.FingerPush = Vector3.zero;
                    LogFingers(arm, across, side);
                    LogIdle(arm);
                    return;
                }
                var palm = frame * Vector3.right * side;
                OverEdges(arm, palm, across, side, flatness);
                Claw(arm, palm, across, side, flatness * arm.Claw);
                for (var i = 0; i < arm.Thumb.Length; i++) LiftThumb(arm, i, palm, flatness, ThumbIn + ClawThumb * arm.Claw);
                ThumbInLine(arm, palm, flatness, true);
                KeepOut(arm, across, side, flatness);
                LogFingers(arm, across, side);
                LogThumb(arm, palm, flatness);
                return;
            }
            var bend = arm.Fist ? FistBend : GripBend;
            // Raik (2026-11-14): the empty hand's fist, the grip pressed on nothing, closes as on a tool for now: fingers
            // parallel (ToolBend), the thumb above the index finger (ThumbOnIndex).
            var handle = tool || (arm.Fist && hand.State == HandState.Empty);
            // The player's own fist (0.33.0): from the loose hand (IdleBend) to the fist (ToolBend) as far as the grip is
            // pressed, so it is one pose all the way; the index finger closes with the trigger too, or is out straight.
            var own = handle && free;
            var point = own ? arm.IndexOut : 0f;
            var up = own ? arm.ThumbUp : 0f;
            if (support) Wrap(arm, side, across, amount);
            else
            {
                for (var i = 0; i < arm.Joints.Length; i++)
                {
                    if (own)
                    {
                        var closed = i < 3 ? Mathf.Max(arm.Closed, arm.IndexPull) : arm.Closed;
                        var to = Mathf.Lerp(IdleBend[i], ToolBend[i], closed);
                        if (i < 3) to = Mathf.Lerp(to, PointBend[i], point);
                        Straighten(arm, i, across, Quaternion.AngleAxis(side * to, across) * along, across, arm.Weight);
                        Parallel(arm, i, across, i < 3 ? amount * (1f - point) : amount);
                        InLine(arm, i, across, arm.Weight);
                        continue;
                    }
                    // A tool: wrapped round its handle, from the animation's pose (ToolBend).
                    if (handle)
                    {
                        Straighten(arm, i, across, Quaternion.AngleAxis(side * ToolBend[i], across) * along, across, amount);
                        Parallel(arm, i, across, amount);
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
            }
            if (!support && (handle || arm.Fist)) PalmFold(arm, frame, side, amount, point);
            var thumb = (arm.Fist && !handle ? FistThumb : GripThumb) * amount * (1f - up);
            // The humanoid three only: the rig's further thumb bones keep their pose relative to them.
            // A second hand on a tool has closed its thumb on it already (Wrap).
            for (var i = 0; i < 3 && !support; i++)
            {
                // The loose hand's thumb out (ThumbAway) goes over into the fist's as the hand closes.
                var away = own && i == 0 ? ThumbAway * up * (1f - arm.Closed) * arm.Weight : 0f;
                Turn(arm, arm.Thumb[i], arm.Joints.Length + i, Quaternion.AngleAxis(side * (away - thumb), along));
            }
            var inward = frame * Vector3.right * side;
            // As the loose hand's thumb, in line, as far as the hand is still open or the thumb is up.
            if (own) ThumbInLine(arm, inward, arm.Weight * Mathf.Max(1f - arm.Closed, up), true);
            if (handle)
            {
                // Pointing, the thumb lies over the middle finger, as on a fist.
                ThumbOnIndex(arm, inward, amount * (1f - up) * (1f - point));
                if (point > 0f) ThumbOnMiddle(arm, inward, amount * (1f - up) * point);
            }
            else if (arm.Fist) ThumbOnMiddle(arm, inward, amount);
            if (up > 0f) ThumbsUp(arm, frame, side, amount * up);
            // The stiff finger's shape for Touch: as it is now, out of the fist.
            if (own && arm.Pointing && Chain(arm, 0))
            {
                var back = Quaternion.Inverse(frame);
                arm.ShapeKnuckle[0] = back * (arm.Joints[0].position - wristAt);
                arm.ShapeTip[0] = back * (arm.Joints[2].GetChild(0).position - wristAt);
            }
        }

        // Raik (0.33.0): the thumb off its buttons and the stick stands up out of the fist: its base turned so that its
        // tip is out along the thumb's side of the hand (ThumbUpBack, ThumbUpAhead), by that share.
        private static void ThumbsUp(Arm arm, Quaternion frame, float side, float share)
        {
            var root = arm.Thumb[0];
            var tip = Tip(arm.Thumb);
            if (share <= 0f || root == null || tip == null) return;
            var way = frame * new Vector3(-side * ThumbUpBack, 1f, ThumbUpAhead);
            var turn = Quaternion.FromToRotation(tip.position - root.position, way);
            root.rotation = Quaternion.Slerp(Quaternion.identity, turn, share) * root.rotation;
            arm.Set[arm.Joints.Length] = root.localRotation;
        }

        // The fingertips as Curl left them, while the hand is the controller's and holds nothing (VrButtons).
        private static void KeepTips(Arm arm)
        {
            var hand = arm == LeftArm ? VrHand.Left : VrHand.Right;
            arm.TipsKnown = arm.Driven && arm.Weight >= 1f && hand.State == HandState.Empty && !VrClimb.TryGetHold(arm.Input, out _);
            if (!arm.TipsKnown) return;
            for (var finger = 0; finger < 4; finger++)
            {
                var last = finger * 3 + 2 < arm.Joints.Length ? arm.Joints[finger * 3 + 2] : null;
                if (last == null || last.childCount == 0)
                {
                    arm.TipsKnown = false;
                    return;
                }
                arm.TipWorld[finger] = last.GetChild(0).position;
            }
        }

        // That controller's hand's fingertips (index to little), as shown last frame, while it holds nothing.
        public static bool TryGetFingerTips(VrInput.Hand input, out Vector3[] tips)
        {
            var arm = input == VrInput.Left ? LeftArm : RightArm;
            tips = arm != null && arm.TipsKnown ? arm.TipWorld : null;
            return tips != null;
        }

        // The other hand's tool is in the way of a hand that does not hold it itself (VrHandShape.Gather).
        private static bool SeesTool(Arm arm) => !(arm == LeftArm ? VrHand.Left : VrHand.Right).HoldsTool;

        // The other arm, in the way of this hand (VrHandShape.Limb); not for a hand that supports a tool held in the other
        // hand, which lies right at its wrist.
        private static VrHandShape.Limb OtherLimb(Arm arm)
        {
            if (VrToolSupport.Supports(arm.Input)) return default;
            var other = arm == LeftArm ? RightArm : LeftArm;
            return Limb(other);
        }

        private static VrHandShape.Limb Limb(Arm arm)
        {
            if (arm.Upper == null || arm.Lower == null || arm.Wrist == null) return default;
            return new VrHandShape.Limb
            {
                Known = true,
                Shoulder = arm.Upper.position,
                Elbow = arm.Lower.position,
                Wrist = arm.Wrist.position,
                Turn = arm.Lower.rotation,
            };
        }

        // Test (Raik, 2026-11-14: wherever a hand takes hold of the tool, a pose that makes sense): each finger of a second
        // hand on a tool wraps round the tool's shape joint by joint, as a real finger does: its base joint closes until a
        // bone touches the tool, then its middle joint, then its last, each up to WrapMost. Where a bone meets nothing the
        // finger curls on round it. Found by halving, so the finger lies on the shape; each joint goes there over CurlTime.
        // The thumb closes the same way from the other side.
        private static readonly float[] WrapMost = { 85f, 100f, 75f };
        private const float ThumbWrapMost = 60f;
        private static int wrapLogs;

        private static void Wrap(Arm arm, float side, Vector3 across, float amount)
        {
            var speed = Time.unscaledDeltaTime / CurlTime;
            for (var finger = 0; finger < arm.Joints.Length / 3; finger++)
            {
                if (!Chain(arm, finger)) continue;
                for (var k = 0; k < 3; k++) Turn(arm, arm.Joints[finger * 3 + k], finger * 3 + k, Quaternion.identity);
                for (var k = 0; k < 3; k++)
                {
                    var index = finger * 3 + k;
                    var joint = arm.Joints[index];
                    var from = k;
                    var most = WrapMost[k] * amount;
                    var reach = Closest(most, angle =>
                    {
                        Turn(arm, joint, index, Quaternion.AngleAxis(side * angle, across));
                        return Touches(arm, finger, from);
                    });
                    arm.Wrap[index] = Mathf.MoveTowards(arm.Wrap[index], reach, WrapMost[k] * speed);
                    Turn(arm, joint, index, Quaternion.AngleAxis(side * arm.Wrap[index], across));
                }
            }
            // The thumb: its bones turned together about the hand's length until one touches the tool.
            var along = Frame(arm, out _, out var frame) ? frame * Vector3.forward : Vector3.forward;
            var thumb = Closest(ThumbWrapMost * amount, angle =>
            {
                for (var i = 0; i < 3; i++) Turn(arm, arm.Thumb[i], arm.Joints.Length + i, Quaternion.AngleAxis(-side * angle, along));
                return ThumbTouches(arm);
            });
            arm.WrapThumb = Mathf.MoveTowards(arm.WrapThumb, thumb, ThumbWrapMost * speed);
            for (var i = 0; i < 3; i++) Turn(arm, arm.Thumb[i], arm.Joints.Length + i, Quaternion.AngleAxis(-side * arm.WrapThumb, along));
            if (wrapLogs++ < 20)
                Plugin.Log.LogInfo($"Hands: {arm.Name} hand wraps round the tool, joints (base/middle/last) I {arm.Wrap[0]:0}/{arm.Wrap[1]:0}/{arm.Wrap[2]:0} M {arm.Wrap[3]:0}/{arm.Wrap[4]:0}/{arm.Wrap[5]:0} "
                    + $"R {arm.Wrap[6]:0}/{arm.Wrap[7]:0}/{arm.Wrap[8]:0} L {arm.Wrap[9]:0}/{arm.Wrap[10]:0}/{arm.Wrap[11]:0}, thumb {arm.WrapThumb:0}°");
        }

        // The greatest value in 0 to most for which touching is false, by halving (0 when it touches already).
        private static float Closest(float most, System.Func<float, bool> touching)
        {
            if (most <= 0f || !touching(most)) return Mathf.Max(most, 0f);
            if (touching(0f)) return 0f;
            var least = 0f;
            for (var step = 0; step < 7; step++)
            {
                var middle = (least + most) * 0.5f;
                if (touching(middle)) most = middle; else least = middle;
            }
            return least;
        }

        // Whether one of the thumb's bones reaches the tool.
        private static bool ThumbTouches(Arm arm)
        {
            var radius = VrHandShape.ThumbRadius * VrCamera.WorldScale;
            for (var k = 0; k < arm.Thumb.Length; k++)
            {
                var joint = arm.Thumb[k];
                if (joint == null) break;
                var next = k + 1 < arm.Thumb.Length && arm.Thumb[k + 1] != null ? arm.Thumb[k + 1] : joint.childCount > 0 ? joint.GetChild(0) : null;
                if (next == null) break;
                if (VrToolSupport.Distance(arm.Input, Vector3.Lerp(joint.position, next.position, 0.5f)) <= radius
                    || VrToolSupport.Distance(arm.Input, next.position) <= radius) return true;
            }
            return false;
        }

        // Whether a bone of that finger, from that one on to its tip, reaches the tool.
        private static bool Touches(Arm arm, int finger, int from = 0)
        {
            var radius = FingerRadius * VrCamera.WorldScale;
            for (var k = from; k < 3; k++)
            {
                var joint = arm.Joints[finger * 3 + k];
                if (joint == null) continue;
                var next = k < 2 ? arm.Joints[finger * 3 + k + 1] : joint.childCount > 0 ? joint.GetChild(0) : null;
                if (next == null) continue;
                // The bone's middle and its end.
                if (VrToolSupport.Distance(arm.Input, Vector3.Lerp(joint.position, next.position, 0.5f)) <= radius
                    || VrToolSupport.Distance(arm.Input, next.position) <= radius) return true;
            }
            return false;
        }

        // The thumb's base turned so that its tip lies over the middle finger's last joint, closing the fist's tunnel.
        private static void ThumbOnMiddle(Arm arm, Vector3 palm, float share)
        {
            var root = arm.Thumb[0];
            var last = arm.Thumb.LastOrDefault(j => j != null);
            var middle = arm.Joints[5];
            if (share <= 0f || root == null || last == null || middle == null) return;
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
            if (share <= 0f || root == null || last == null || from == null || to == null) return;
            var tip = last.childCount > 0 ? last.GetChild(0).position : last.position;
            var target = (from.position + to.position) * 0.5f + palm * ThumbOver * VrCamera.WorldScale;
            // Above the index finger: from the little finger's knuckle towards the index finger's.
            var little = arm.Joints.Length >= 12 ? arm.Joints[9] : null;
            if (little != null && (arm.Joints[0].position - little.position).sqrMagnitude > 1e-6f)
                target += (arm.Joints[0].position - little.position).normalized * ThumbAbove * VrCamera.WorldScale;
            var turn = Quaternion.FromToRotation(tip - root.position, target - root.position);
            root.rotation = Quaternion.Slerp(Quaternion.identity, turn, share) * root.rotation;
            arm.Set[arm.Joints.Length] = root.localRotation;
        }

        // The joints that are still as Curl left them back to the animation's pose.
        private static void Open(Arm arm)
        {
            RestoreFold(arm);
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

        // Raik (2026-11-14, his own hand): closing a fist, the palm folds in under the knuckles too; on its back the hand
        // bends at the knuckles, on its inside at a crease CreaseShare of the palm's length behind them. The game's hand
        // has no bones between wrist and fingers, so each finger's base joint is turned about that crease instead of about
        // itself, by FoldAt degrees (index to little, the little finger most, as a hand cups) times the share closed: the
        // knuckle moves in towards the palm and the glove round it follows. Half of that turn is taken back at the joint,
        // so the fingers keep about the pose they had. The palm's shape folds its front the same way (PalmBoxes).
        private const float CreaseShare = 0.15f;
        private static readonly float[] FoldAt = { 8f, 10f, 13f, 16f };
        private static int foldPalmLogs;

        // The index finger out of the fist (pointing, 0 to 1) keeps its knuckle where it is.
        private static void PalmFold(Arm arm, Quaternion frame, float side, float share, float pointing = 0f)
        {
            if (share <= 0f) return;
            var scale = VrCamera.WorldScale;
            var across = frame * Vector3.up;
            var along = frame * Vector3.forward;
            var palm = frame * Vector3.right * side;
            var total = 0f;
            for (var finger = 0; finger < 4; finger++)
            {
                var index = finger * 3;
                var joint = arm.Joints[index];
                if (joint == null) continue;
                var angle = FoldAt[finger] * share * (finger == 0 ? 1f - pointing : 1f);
                var pivot = joint.position - along * (CreaseShare * PalmLength * scale) + palm * (PalmThickness * 0.5f * scale);
                var turn = Quaternion.AngleAxis(side * angle, across);
                joint.position = pivot + turn * (joint.position - pivot);
                joint.rotation = Quaternion.AngleAxis(side * angle * 0.5f, across) * joint.rotation;
                arm.FoldSet[finger] = joint.localPosition;
                arm.Set[index] = joint.localRotation;
                total += angle;
            }
            arm.PalmFold = total / 4f;
            if (foldPalmLogs++ < 10) Plugin.Log.LogInfo($"Hands: {arm.Name} palm folds in under the knuckles, {arm.PalmFold:0}° on average");
        }

        // The finger's base joints back where the animation had them, if they are still where PalmFold put them.
        private static void RestoreFold(Arm arm)
        {
            arm.PalmFold = 0f;
            for (var finger = 0; finger < 4; finger++)
            {
                var joint = finger * 3 < arm.Joints.Length ? arm.Joints[finger * 3] : null;
                if (joint == null) continue;
                if (arm.FoldSet[finger] != Vector3.zero && joint.localPosition == arm.FoldSet[finger]) joint.localPosition = arm.FoldBase[finger];
                arm.FoldBase[finger] = joint.localPosition;
                arm.FoldSet[finger] = Vector3.zero;
            }
        }

        // Test (Raik, 2026-11-14): the palm's shape in three boxes, in the hand's axes from the wrist (middle, turn, half
        // size): the main one from the wrist to CreaseShare before the knuckles; beyond it the back of the hand on to the
        // knuckles, and the palm's front under them, folded in about the crease by PalmFold. Up to three, none without the
        // hand's shape known.
        private static readonly Vector3[] PalmCenter = new Vector3[3];
        private static readonly Quaternion[] PalmTurn = new Quaternion[3];
        private static readonly Vector3[] PalmHalf = new Vector3[3];

        private static int PalmBoxes(Arm arm, float side)
        {
            if (!arm.ShapeKnown) return 0;
            var scale = VrCamera.WorldScale;
            var length = arm.ShapeKnuckle[1].z;
            if (length <= 0.01f * scale) return 0;
            var width = Mathf.Abs(arm.ShapeKnuckle[0].y - arm.ShapeKnuckle[3].y) + 2f * VrHandShape.FingerRadius[1] * scale;
            var middle = (arm.ShapeKnuckle[0].y + arm.ShapeKnuckle[3].y) * 0.5f;
            var palmSide = PalmThickness * 0.9f * scale;
            var backSide = 0.01f * scale;
            var crease = (1f - CreaseShare) * length;
            var front = CreaseShare * length;
            PalmCenter[0] = new Vector3(side * (palmSide - backSide) * 0.5f, middle, crease * 0.5f);
            PalmTurn[0] = Quaternion.identity;
            PalmHalf[0] = new Vector3((palmSide + backSide) * 0.5f, width * 0.5f, crease * 0.5f);
            PalmCenter[1] = new Vector3(-side * backSide * 0.5f, middle, crease + front * 0.5f);
            PalmTurn[1] = Quaternion.identity;
            PalmHalf[1] = new Vector3(backSide * 0.5f, width * 0.5f, front * 0.5f);
            var hinge = new Vector3(0f, middle, crease);
            var fold = Quaternion.AngleAxis(side * arm.PalmFold, Vector3.up);
            PalmCenter[2] = hinge + fold * (new Vector3(side * palmSide * 0.5f, middle, crease + front * 0.5f) - hinge);
            PalmTurn[2] = fold;
            PalmHalf[2] = new Vector3(palmSide * 0.5f, width * 0.5f, front * 0.5f);
            return 3;
        }

        // Raik (2026-11-14, picture: the little finger still bent out towards the hand's edge): Straighten bends a finger
        // only in its own plane, so it kept the animation's spread sideways. On a handle each finger's base bone is
        // turned sideways until it leans neither towards the thumb nor away (InLine lines up the rest), by that share:
        // the fingers lie side by side, parallel.
        private static void Parallel(Arm arm, int index, Vector3 across, float share)
        {
            if (index % 3 != 0 || share <= 0f) return;
            var joint = arm.Joints[index];
            var next = arm.Joints[index + 1];
            if (joint == null || next == null) return;
            var bone = next.position - joint.position;
            if (bone.sqrMagnitude <= 1e-10f) return;
            var axis = Vector3.Cross(bone, across);
            if (axis.sqrMagnitude <= 1e-10f) return;
            var lean = Mathf.Asin(Mathf.Clamp(Vector3.Dot(bone.normalized, across), -1f, 1f)) * Mathf.Rad2Deg;
            joint.rotation = Quaternion.AngleAxis(-lean * share, axis.normalized) * joint.rotation;
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
            VrHandShape.Gather(arm.Wrist.position, WallReach * VrCamera.WorldScale, body, SeesTool(arm), OtherLimb(arm));
            var saved = new Quaternion[3];
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
                // Test (Raik, 2026-11-14: the ring finger went into the held wall, curled over an edge that was none): the
                // curl only as far as the finger stays out of what it lies on, ClawPress at most.
                var first = finger * 3;
                if (arm.Edge[first] <= 0f && arm.Edge[first + 1] <= 0f && arm.Edge[first + 2] <= 0f) continue;
                if (!Chain(arm, finger)) continue;
                for (var k = 0; k < 3; k++) saved[k] = arm.Joints[first + k].localRotation;
                var reach = 1f;
                if (EdgeCurl(arm, finger, saved, across, side, share, 1f))
                {
                    var low = 0f;
                    for (var step = 0; step < 6; step++)
                    {
                        var middle = (low + reach) * 0.5f;
                        if (EdgeCurl(arm, finger, saved, across, side, share, middle)) reach = middle; else low = middle;
                    }
                    reach = low;
                    if (curlLogs++ < 20) Plugin.Log.LogInfo($"Hands: {arm.Name} {arm.Joints[first].name} curls over an edge only {reach * 100f:0}%, the surface is under it");
                }
                EdgeCurl(arm, finger, saved, across, side, share, reach);
                for (var k = 0; k < 3; k++) arm.Set[first + k] = arm.Joints[first + k].localRotation;
            }
        }

        private static int curlLogs;

        // The finger curled over an edge by that share of its Edge, from saved; true when it is then in something.
        private static bool EdgeCurl(Arm arm, int finger, Quaternion[] saved, Vector3 across, float side, float share, float reach)
        {
            for (var k = 0; k < 3; k++) arm.Joints[finger * 3 + k].localRotation = saved[k];
            for (var k = 0; k < 3; k++)
            {
                var index = finger * 3 + k;
                if (arm.Edge[index] <= 0f) continue;
                var joint = arm.Joints[index];
                joint.rotation = Quaternion.AngleAxis(side * EdgeBend[k] * arm.Edge[index] * share * reach, across) * joint.rotation;
            }
            return FingerIn(arm, finger, ClawPress * VrCamera.WorldScale);
        }

        // Test (Raik, 2026-11-14): a finger of the flat hand still in the surface (the ring finger on a held wall) bends
        // back at its knuckle until it is out, ClawPress at most, up to MostBack.
        // Test (Raik, 2026-11-14): the hand as drawn is its collision shape, each finger and thumb bone a capsule to the
        // visible tip; at the end of the pose none may be more than PressInAtMost in a surface, as a suit glove pressed
        // on it. A finger still in bends back at its knuckle (up to MostLift), then unbends its middle and last joint
        // (up to MostUnbend each); the thumb turns out of it the way out. What is left is logged.
        private const float PressInAtMost = 0.0015f;
        private const float MostLift = 45f;
        private const float MostUnbend = 60f;

        private static void KeepOut(Arm arm, Vector3 across, float side, float share)
        {
            // Also on a hold (the ring finger on a held wall): only the hand that follows its controller.
            if (share <= 0f || !arm.Driven) return;
            var allowed = PressInAtMost * VrCamera.WorldScale;
            VrHandShape.Gather(arm.Wrist.position, WallReach * VrCamera.WorldScale, arm.Wrist.root, SeesTool(arm), OtherLimb(arm));
            for (var finger = 0; finger < 4; finger++)
            {
                if (!Chain(arm, finger)) continue;
                var root = arm.Joints[finger * 3];
                var start = root.localRotation;
                var inside = FingerIn(arm, finger, allowed);
                // The knuckle bends back at most KnuckleBackAtMost in all (Raik): what it is bent back already counts.
                var mostLift = Mathf.Clamp(Mathf.Min(MostLift, KnuckleBackAtMost + KnuckleBend(arm, finger, across, side)), 0f, MostLift);
                var lift = inside ? Search(mostLift, angle =>
                {
                    root.localRotation = start;
                    root.rotation = Quaternion.AngleAxis(-side * angle, across) * root.rotation;
                    return FingerIn(arm, finger, allowed);
                }) : 0f;
                lift = Mathf.Min(mostLift, Mathf.Max(lift, arm.LiftShown[finger] - Time.unscaledDeltaTime * MostLift / RelaxTime));
                arm.LiftShown[finger] = lift;
                root.localRotation = start;
                if (lift > 0f) root.rotation = Quaternion.AngleAxis(-side * lift * share, across) * root.rotation;
                arm.Set[finger * 3] = root.localRotation;
                var unbend = 0f;
                // Middle and last joint unbend at most until they are straight, never bent backwards (the picture,
                // 2026-11-14: fingers overstretched).
                var mostUnbend = Mathf.Clamp(Mathf.Min(JointBend(arm, finger * 3 + 1, across, side), JointBend(arm, finger * 3 + 2, across, side)), 0f, MostUnbend);
                var stillIn = inside && FingerIn(arm, finger, allowed);
                if (stillIn || arm.UnbendShown[finger] > 0f)
                {
                    var middleJoint = arm.Joints[finger * 3 + 1];
                    var lastJoint = arm.Joints[finger * 3 + 2];
                    var middleStart = middleJoint.localRotation;
                    var lastStart = lastJoint.localRotation;
                    if (stillIn) unbend = Search(mostUnbend, angle =>
                    {
                        middleJoint.localRotation = middleStart;
                        lastJoint.localRotation = lastStart;
                        middleJoint.rotation = Quaternion.AngleAxis(-side * angle, across) * middleJoint.rotation;
                        lastJoint.rotation = Quaternion.AngleAxis(-side * angle, across) * lastJoint.rotation;
                        return FingerIn(arm, finger, allowed);
                    });
                    unbend = Mathf.Min(mostUnbend, Mathf.Max(unbend, arm.UnbendShown[finger] - Time.unscaledDeltaTime * MostUnbend / RelaxTime));
                    middleJoint.localRotation = middleStart;
                    lastJoint.localRotation = lastStart;
                    middleJoint.rotation = Quaternion.AngleAxis(-side * unbend * share, across) * middleJoint.rotation;
                    lastJoint.rotation = Quaternion.AngleAxis(-side * unbend * share, across) * lastJoint.rotation;
                    arm.Set[finger * 3 + 1] = middleJoint.localRotation;
                    arm.Set[finger * 3 + 2] = lastJoint.localRotation;
                }
                arm.UnbendShown[finger] = unbend;
                if (!inside) continue;
                if (keepLogs++ < 30)
                    Plugin.Log.LogInfo($"Hands: {arm.Name} {root.name} kept out of the surface: lifted {lift:0}°, unbent {unbend:0}°, still {FingerDepth(arm, finger) * 1000f / VrCamera.WorldScale:0.0} mm in");
            }
            ThumbOut(arm, allowed);
            FingerPush(arm, allowed);
        }

        // Test (Raik, 2026-11-14: with the open hand the fingers still slipped into a wall where faces meet, and drawing
        // the hand up along it they stayed in): bent back as far as they go (MostLift) and straight, a finger can still
        // lie in a face ahead of the palm. Then the empty hand is held back, the next frame on, along the way out of the
        // deepest finger, until none is in more than allowed; with none in, it comes back towards its controller at
        // FingerPushBack m/s, so it settles at the surface. At most FingerPushMost.
        private const float FingerPushMost = 0.08f;
        private const float FingerPushBack = 0.05f;
        private static int fingerPushLogs;

        private static void FingerPush(Arm arm, float allowed)
        {
            var state = (arm == LeftArm ? VrHand.Left : VrHand.Right).State;
            if (state != HandState.Empty)
            {
                arm.FingerPush = Vector3.zero;
                return;
            }
            var scale = VrCamera.WorldScale;
            var deepest = 0f;
            var way = Vector3.zero;
            for (var finger = 0; finger < 4; finger++)
            {
                if (!Chain(arm, finger)) continue;
                var depth = FingerWay(arm, finger, out var fingerWay);
                if (depth <= deepest) continue;
                deepest = depth;
                way = fingerWay;
            }
            if (deepest > allowed && way != Vector3.zero)
            {
                var before = arm.FingerPush.magnitude;
                arm.FingerPush = Vector3.ClampMagnitude(arm.FingerPush + way * (deepest - allowed), FingerPushMost * scale);
                if (before <= 0f && fingerPushLogs++ < 30)
                    Plugin.Log.LogInfo($"Hands: {arm.Name} hand held back, a finger {deepest * 1000f / scale:0.0} mm in a surface");
            }
            else arm.FingerPush = Vector3.MoveTowards(arm.FingerPush, Vector3.zero, FingerPushBack * scale * Time.unscaledDeltaTime);
        }

        // How far a finger joint's bone is bent towards the palm from the bone before it, in degrees (negative: backwards).
        private static float JointBend(Arm arm, int index, Vector3 across, float side)
        {
            var joint = arm.Joints[index];
            var before = arm.Joints[index - 1];
            var next = index % 3 < 2 ? arm.Joints[index + 1] : joint.childCount > 0 ? joint.GetChild(0) : null;
            if (joint == null || before == null || next == null) return 0f;
            return side * Vector3.SignedAngle(joint.position - before.position, next.position - joint.position, across);
        }

        // How far a finger's first bone is bent towards the palm from the knuckles' line, in degrees (negative: backwards).
        private static float KnuckleBend(Arm arm, int finger, Vector3 across, float side)
        {
            if (!Frame(arm, out _, out var frame)) return 0f;
            var root = arm.Joints[finger * 3];
            var next = arm.Joints[finger * 3 + 1];
            if (root == null || next == null) return 0f;
            return side * Vector3.SignedAngle(frame * Vector3.forward, next.position - root.position, across);
        }

        // Local test tool (Raik, 2026-11-14: fingers overstretched): while a finger of the empty hand is bent back more
        // than 30 degrees at a joint, every quarter second each finger's bends (knuckle, middle, last; negative backwards)
        // and what bent it.
        private static int fingerPoseLogs;

        private static void LogFingers(Arm arm, Vector3 across, float side)
        {
            if (fingerPoseLogs >= 300 || Time.unscaledTime - arm.FingerLogAt < 0.25f) return;
            var worst = 0f;
            var b = new System.Text.StringBuilder();
            for (var finger = 0; finger < 4; finger++)
            {
                if (!Chain(arm, finger)) continue;
                var knuckle = KnuckleBend(arm, finger, across, side);
                var middle = JointBend(arm, finger * 3 + 1, across, side);
                var last = JointBend(arm, finger * 3 + 2, across, side);
                worst = Mathf.Min(worst, Mathf.Min(knuckle, Mathf.Min(middle, last)));
                b.Append($" {"IMRL"[finger]} {knuckle:0}/{middle:0}/{last:0} (bent {arm.BendShown[finger]:0}, lifted {arm.LiftShown[finger]:0}, unbent {arm.UnbendShown[finger]:0}, {FingerDepth(arm, finger) * 1000f / VrCamera.WorldScale:0.0} mm in);");
            }
            if (worst > -30f) return;
            arm.FingerLogAt = Time.unscaledTime;
            fingerPoseLogs++;
            Plugin.Log.LogInfo($"Finger pose: {arm.Name} knuckle/middle/last in degrees:{b} hand back {arm.Back:0}°, folded {arm.Folded:0.00}, flat {arm.Press:0.00}");
        }

        // The least value in 0 to most for which inside is false (most when it stays true), by halving.
        private static float Search(float most, System.Func<float, bool> inside)
        {
            if (!inside(most))
            {
                var least = 0f;
                for (var step = 0; step < 6; step++)
                {
                    var middle = (least + most) * 0.5f;
                    if (inside(middle)) least = middle; else most = middle;
                }
            }
            return most;
        }

        // How deep the finger's deepest bone is in something.
        private static float FingerDepth(Arm arm, int finger)
        {
            var radius = VrHandShape.FingerRadius[finger] * VrCamera.WorldScale;
            var deepest = 0f;
            for (var k = 0; k < 3; k++)
            {
                var joint = arm.Joints[finger * 3 + k];
                var next = k < 2 ? arm.Joints[finger * 3 + k + 1] : joint.GetChild(0);
                deepest = Mathf.Max(deepest, VrHandShape.Capsule(joint.position, next.position, radius, out _));
            }
            return deepest;
        }

        // The thumb's bones as capsules: while one is in something deeper than allowed, the thumb's base turns so that
        // that bone's end moves out the way out, three times at most.
        private static void ThumbOut(Arm arm, float allowed)
        {
            var root = arm.Thumb[0];
            if (root == null) return;
            var radius = VrHandShape.ThumbRadius * VrCamera.WorldScale;
            for (var round = 0; round < 3; round++)
            {
                var deepest = 0f;
                var way = Vector3.zero;
                var end = Vector3.zero;
                for (var k = 0; k < arm.Thumb.Length; k++)
                {
                    var joint = arm.Thumb[k];
                    if (joint == null) break;
                    var next = k + 1 < arm.Thumb.Length && arm.Thumb[k + 1] != null ? arm.Thumb[k + 1] : joint.childCount > 0 ? joint.GetChild(0) : null;
                    if (next == null) break;
                    var depth = VrHandShape.Capsule(joint.position, next.position, radius, out var out_);
                    if (depth > deepest)
                    {
                        deepest = depth;
                        way = out_;
                        end = next.position;
                    }
                }
                if (deepest <= allowed) return;
                var turn = Quaternion.FromToRotation(end - root.position, end + way * (deepest - allowed * 0.5f) - root.position);
                root.rotation = turn * root.rotation;
                arm.Set[arm.Joints.Length] = root.localRotation;
                if (thumbOutLogs++ < 20) Plugin.Log.LogInfo($"Hands: {arm.Name} thumb turned {Quaternion.Angle(Quaternion.identity, turn):0}° out of a surface it was {deepest * 1000f / VrCamera.WorldScale:0.0} mm in");
            }
        }

        private static int thumbOutLogs;

        private static int keepLogs;

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
            VrHandShape.Gather(arm.Wrist.position, WallReach * VrCamera.WorldScale, arm.Wrist.root, SeesTool(arm), OtherLimb(arm));
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

        // The loose empty hand's fingers bent back at the knuckles: each finger that is in something (its bones' capsules
        // against the real shapes, VrHandShape) as far as it needs, up to MostBack. Test (Raik, 2026-11-14: fingers lift
        // as the hand comes near, though the shapes are right): no longer all together as far as Touch found, which
        // reckons with the surface as a plane drawn on past its edges and lifted every finger as far as the one nearest it.
        // Touch still uses that to tell when the hand folds over.
        // Test (Raik, 2026-11-14): a finger touching something with its back (the back of the hand on a hull) folds in
        // towards the palm at its knuckle, long and straight, as far as it has to, up to MostFold, instead of bending back
        // into it.
        // Test (Raik, 2026-11-14: the fingers swung over as soon as the palm was raised a little; they should only once
        // the palm goes over them, dragging them along): no longer by which way the hand moves. A folded finger's tip
        // rests where it touched (TuckAnchor), as held there by friction: it slides along while the finger is pressed
        // further in, and is dragged after the knuckle when that goes further off than the finger is long. The finger
        // keeps pointing at that spot as the hand turns or lifts. Only when the knuckles have passed over it, the spot
        // FlipAt degrees on the back of the hand's side, the fingers swing over and bend back as before, until BackForget
        // seconds pass without a finger's back touching. A tip no longer within NearGap of anything for BackForget
        // seconds lets go of its spot and the finger relaxes.
        private const float MostFold = 90f;
        private const float FlipAt = 5f;
        private const float NearGap = 0.008f;
        private const float BackForget = 0.3f;
        // A finger touches with its back when the way out of what it is in points this much towards the palm.
        private const float BackSide = 0.2f;
        private static int tuckLogs;

        // Whether some bone of the finger is within gap of something.
        private static bool FingerNear(Arm arm, int finger, float gap)
        {
            var radius = VrHandShape.FingerRadius[finger] * VrCamera.WorldScale + gap;
            for (var k = 0; k < 3; k++)
            {
                var joint = arm.Joints[finger * 3 + k];
                var next = k < 2 ? arm.Joints[finger * 3 + k + 1] : joint.GetChild(0);
                if (VrHandShape.Capsule(joint.position, next.position, radius, out _) > 0.001f) return true;
            }
            return false;
        }

        // The finger's length from its knuckle to its tip, along its bones.
        private static float FingerReach(Arm arm, int finger)
        {
            var length = 0f;
            for (var k = 0; k < 3; k++)
            {
                var joint = arm.Joints[finger * 3 + k];
                var next = k < 2 ? arm.Joints[finger * 3 + k + 1] : joint.GetChild(0);
                length += Vector3.Distance(joint.position, next.position);
            }
            return length;
        }

        // How deep the finger's deepest bone is in something, and the way out of it there.
        private static float FingerWay(Arm arm, int finger, out Vector3 way)
        {
            way = Vector3.zero;
            var deepest = 0f;
            var radius = VrHandShape.FingerRadius[finger] * VrCamera.WorldScale;
            for (var k = 0; k < 3; k++)
            {
                var joint = arm.Joints[finger * 3 + k];
                var next = k < 2 ? arm.Joints[finger * 3 + k + 1] : joint.GetChild(0);
                var depth = VrHandShape.Capsule(joint.position, next.position, radius, out var boneWay);
                if (depth <= deepest) continue;
                deepest = depth;
                way = boneWay;
            }
            return deepest;
        }

        // The finger folded in by that many degrees at its knuckle (towards the palm), its middle and last joint
        // straightened as much as it is folded in, from start.
        private static void Fold(Arm arm, int finger, Quaternion start, float angle, Vector3 across, float side)
        {
            var root = arm.Joints[finger * 3];
            root.localRotation = start;
            root.rotation = Quaternion.AngleAxis(side * angle, across) * root.rotation;
            var straight = Mathf.Clamp01(angle / 20f);
            for (var k = 1; k < 3; k++)
            {
                var joint = arm.Joints[finger * 3 + k];
                var bend = JointBend(arm, finger * 3 + k, across, side);
                if (bend > 0f) joint.rotation = Quaternion.AngleAxis(-side * bend * straight, across) * joint.rotation;
            }
        }

        private static void BendBack(Arm arm, Vector3 across, float side)
        {
            var state = (arm == LeftArm ? VrHand.Left : VrHand.Right).State;
            if (state != HandState.Empty || !arm.Driven || arm.Weight <= 0f) return;
            VrHandShape.Gather(arm.Wrist.position, WallReach * VrCamera.WorldScale, arm.Wrist.root, SeesTool(arm), OtherLimb(arm));
            var framed = Frame(arm, out _, out var handFrame);
            var palm = framed ? handFrame * Vector3.right * side : Vector3.zero;
            var now = Time.unscaledTime;
            if (!arm.FoldIn && now - arm.BackTouchAt > BackForget)
            {
                arm.FoldIn = true;
                if (tuckLogs++ < 30) Plugin.Log.LogInfo($"Hands: {arm.Name} fingers fold in again on their backs");
            }
            for (var finger = 0; finger < 4; finger++)
            {
                if (!Chain(arm, finger)) continue;
                var root = arm.Joints[finger * 3];
                var room = MostBack;
                var need = 0f;
                var start = root.localRotation;
                var middleStart = arm.Joints[finger * 3 + 1].localRotation;
                var lastStart = arm.Joints[finger * 3 + 2].localRotation;
                var onBack = framed && FingerWay(arm, finger, out var fingerWay) > 0.001f && Vector3.Dot(fingerWay, palm) > BackSide;
                if (onBack) arm.BackTouchAt = now;
                var fold = 0f;
                var outAt = 0f;
                var held = 0f;
                if (framed && arm.FoldIn)
                {
                    var bentAt = KnuckleBend(arm, finger, across, side);
                    if (onBack)
                    {
                        // As little folded in as gets the finger out.
                        var most = MostFold;
                        for (var step = 0; step < 6; step++)
                        {
                            var middle = (outAt + most) * 0.5f;
                            Fold(arm, finger, start, middle, across, side);
                            if (FingerIn(arm, finger)) outAt = middle; else most = middle;
                            arm.Joints[finger * 3 + 1].localRotation = middleStart;
                            arm.Joints[finger * 3 + 2].localRotation = lastStart;
                        }
                        root.localRotation = start;
                        outAt = most;
                    }
                    if (arm.TuckHas[finger] && now - arm.TuckNearAt[finger] > BackForget)
                    {
                        arm.TuckHas[finger] = false;
                        if (tuckLogs++ < 30) Plugin.Log.LogInfo($"Hands: {arm.Name} {root.name} lets go of where its tip rested, nothing near");
                    }
                    if (arm.TuckHas[finger])
                    {
                        // Pointing at where its tip rests, that dragged along when out of the finger's reach.
                        var knuckle = root.position;
                        var length = FingerReach(arm, finger);
                        var toAnchor = arm.TuckAnchor[finger] - knuckle;
                        if (toAnchor.magnitude > length)
                        {
                            toAnchor = toAnchor.normalized * length;
                            arm.TuckAnchor[finger] = knuckle + toAnchor;
                        }
                        var aim = side * Vector3.SignedAngle(handFrame * Vector3.forward, Vector3.ProjectOnPlane(toAnchor, across), across);
                        if (aim < -FlipAt)
                        {
                            // The palm went over the fingers: they swing over.
                            arm.FoldIn = false;
                            for (var each = 0; each < 4; each++) arm.TuckHas[each] = false;
                            outAt = 0f;
                            if (tuckLogs++ < 30) Plugin.Log.LogInfo($"Hands: {arm.Name} knuckles passed over {root.name}'s tip ({aim:0}°), the fingers swing over and bend back");
                        }
                        else held = Mathf.Max(0f, aim - bentAt);
                    }
                    if (arm.FoldIn) fold = Mathf.Min(Mathf.Max(outAt, held), MostFold);
                }
                var folded = Mathf.Clamp(Mathf.Max(fold, arm.TuckShown[finger] - Time.unscaledDeltaTime * MostFold / RelaxTime), 0f, MostFold);
                arm.TuckShown[finger] = folded;
                if (folded > 0f)
                {
                    // Folded in, or still swinging over: no bending back meanwhile.
                    Fold(arm, finger, start, folded * arm.Weight, across, side);
                    arm.Set[finger * 3] = root.localRotation;
                    arm.Set[finger * 3 + 1] = arm.Joints[finger * 3 + 1].localRotation;
                    arm.Set[finger * 3 + 2] = arm.Joints[finger * 3 + 2].localRotation;
                    arm.BendShown[finger] = 0f;
                    if (arm.FoldIn && fold > 0f)
                    {
                        // Pressed in (no further folded than getting out takes): the tip rests, or slides on, where it is now.
                        var fresh = !arm.TuckHas[finger];
                        if ((fresh || outAt >= held) && fold >= folded - 0.01f)
                        {
                            arm.TuckAnchor[finger] = arm.Joints[finger * 3 + 2].GetChild(0).position;
                            arm.TuckHas[finger] = true;
                            if (fresh) arm.TuckNearAt[finger] = now;
                            if (fresh && tuckLogs++ < 30) Plugin.Log.LogInfo($"Hands: {arm.Name} {root.name} folded in {fold:0}° on its back, its tip resting there");
                        }
                        if (arm.TuckHas[finger] && FingerNear(arm, finger, NearGap * VrCamera.WorldScale)) arm.TuckNearAt[finger] = now;
                    }
                    continue;
                }
                if (room > 0.5f && FingerIn(arm, finger))
                {
                    var least = 0f;
                    var most = room;
                    for (var step = 0; step < 6; step++)
                    {
                        var middle = (least + most) * 0.5f;
                        root.localRotation = start;
                        root.rotation = Quaternion.AngleAxis(-side * middle, across) * root.rotation;
                        if (FingerIn(arm, finger)) least = middle; else most = middle;
                    }
                    need = most;
                    if (backLogs++ < 20) Plugin.Log.LogInfo($"Hands: {arm.Name} {root.name} bent back {most:0}° at a wall");
                }
                var shown = Mathf.Clamp(Mathf.Max(need, arm.BendShown[finger] - Time.unscaledDeltaTime * MostBack / RelaxTime), 0f, Mathf.Max(0f, room));
                arm.BendShown[finger] = shown;
                root.localRotation = start;
                if (shown > 0f) root.rotation = Quaternion.AngleAxis(-side * shown * arm.Weight, across) * root.rotation;
                arm.Set[finger * 3] = root.localRotation;
            }
        }

        private static int backLogs;

        // Whether any bone of the finger, as a capsule, is in something.
        private static bool FingerIn(Arm arm, int finger) => FingerIn(arm, finger, 0.001f);

        private static bool FingerIn(Arm arm, int finger, float allowed)
        {
            var radius = VrHandShape.FingerRadius[finger] * VrCamera.WorldScale;
            for (var k = 0; k < 3; k++)
            {
                var joint = arm.Joints[finger * 3 + k];
                var next = k < 2 ? arm.Joints[finger * 3 + k + 1] : joint.GetChild(0);
                if (VrHandShape.Capsule(joint.position, next.position, radius, out _) > allowed) return true;
            }
            return false;
        }

        private static int edgeLogs;

        // Whether something solid lies within probe of that point towards the palm (or the point is in it).
        private static bool Supported(Vector3 point, Vector3 palm, float probe, Transform body)
        {
            var count = Physics.RaycastNonAlloc(point - palm * probe, palm, WallHits, 2f * probe, Physics.DefaultRaycastLayers, QueryTriggerInteraction.Ignore);
            var found = false;
            for (var i = 0; i < count; i++)
                if (!WallHits[i].collider.transform.IsChildOf(body) && !VrLooseTools.HandsPass(WallHits[i].collider)) found = true;
            return found;
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
            // A second hand on a tool stays where it took hold of it and moves with it (VrToolSupport).
            if (VrToolSupport.TryGetPose(arm.Input, out var onTool, out var onToolAim))
            {
                gripLocal = onTool;
                aimLocal = onToolAim;
            }
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
            var aimed = aim * hold;
            var frame = Quaternion.LookRotation(aimed * Vector3.forward, aimed * Vector3.up);
            wrist = OutOfWalls(arm, wrist);
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
            var free = arm.Gripping && VrClimb.HeldFree(arm.Input);
            var turn = free ? AroundBar(arm, false) * frame : arm.GripAnchor.rotation * gripTurn;
            var placed = arm.GripAnchor.TransformPoint(arm.GripPoint) + turn * arm.GripOffset;
            if (free) placed += SwitchHandShift(placed);
            if (arm.GripFlat) placed = HeldOut(arm, OutOfSurface(arm, placed, turn), turn);
            wrist = Vector3.Lerp(wrist, placed, arm.GripBlend);
            frame = Quaternion.Slerp(frame, turn, arm.GripBlend);
        }

        // Test (Raik, 2026-11-14: the hand on the nacelle switch's grip sat a little low, not quite as a hand would hold
        // it): drawn SwitchHandUp higher and SwitchHandBack level towards the head, in the body's upright.
        private const float SwitchHandUp = 0.01f;
        private const float SwitchHandBack = 0.01f;

        private static Vector3 SwitchHandShift(Vector3 placed)
        {
            var camera = VrCamera.Instance != null ? VrCamera.Instance.transform : null;
            if (camera == null || camera.parent == null) return Vector3.zero;
            var up = camera.parent.up;
            var back = Vector3.ProjectOnPlane(camera.position - placed, up);
            back = back.sqrMagnitude > 1e-6f ? back.normalized : Vector3.zero;
            return (up * SwitchHandUp + back * SwitchHandBack) * VrCamera.WorldScale;
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
                        if (hit.distance <= 0f || hit.collider.transform.IsChildOf(body) || VrLooseTools.HandsPass(hit.collider)) continue;
                        deepest = Mathf.Max(deepest, probe - hit.distance);
                    }
                }
            return wrist - palm * deepest;
        }

        // Test (Raik, 2026-11-14: holding the nose where four faces meet, the hand sat 5 to 11 cm in the part, the
        // fingers straight and lifted as far as they go): OutOfSurface only looks along the palm's way, so what lies
        // beside the hand (the faces of a corner, the next part) was never felt. A flat hand on its hold is pushed out of
        // everything by the palm's boxes as the empty hand is (PalmOut), in up to three steps; not out of the tool in
        // the other hand or the other arm, which keep their own sides.
        private static int heldOutLogs;

        private static Vector3 HeldOut(Arm arm, Vector3 wrist, Quaternion turn)
        {
            var side = arm == LeftArm ? 1f : -1f;
            var boxes = PalmBoxes(arm, side);
            if (boxes == 0) return wrist;
            VrHandShape.Gather(wrist, WallReach * VrCamera.WorldScale, arm.Wrist.root, SeesTool(arm), OtherLimb(arm));
            var moved = Vector3.zero;
            for (var step = 0; step < 3; step++)
            {
                var depth = 0f;
                var way = Vector3.zero;
                var other = false;
                for (var b = 0; b < boxes; b++)
                {
                    var deep = VrHandShape.Box(wrist + turn * PalmCenter[b], turn * PalmTurn[b], PalmHalf[b], out var boxWay);
                    if (deep <= depth) continue;
                    depth = deep;
                    way = boxWay;
                    other = VrHandShape.FromTool || VrHandShape.FromArm;
                }
                if (depth <= 0.0005f || other) break;
                wrist += way * depth;
                moved += way * depth;
            }
            if (moved != Vector3.zero && heldOutLogs++ < 30)
                Plugin.Log.LogInfo($"Hands: {arm.Name} hand on its hold pushed {moved.magnitude * 100f / VrCamera.WorldScale:0.0} cm out of what lies beside it");
            return wrist;
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
            var count = Sweep(shoulder, WristRadius, direction, length);
            var free = length;
            var body = arm.Wrist.root;
            var first = -1;
            for (var i = 0; i < count; i++)
            {
                var hit = WallHits[i];
                // Hits from where the cast starts inside something have no place on the way.
                if (hit.distance > 0f && hit.distance < free && !hit.collider.transform.IsChildOf(body) && !VrLooseTools.HandsPass(hit.collider))
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
                    var again = Sweep(shoulder, WristRadius * 0.9f, toSlid / reach, reach);
                    for (var i = 0; i < again && !blocked; i++)
                        blocked = WallHits[i].distance > 0f && WallHits[i].distance < reach - 0.005f && !WallHits[i].collider.transform.IsChildOf(body)
                            && !VrLooseTools.HandsPass(WallHits[i].collider);
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
            var fromWrist = wrist;
            var fromFrame = frame;
            arm.Back = 0f;
            arm.Folded = 0f;
            var pressed = false;
            var empty = state == HandState.Empty && arm.Driven && arm.Wrist != null;
            if (empty) VrHandShape.Gather(wrist, WallReach * scale, arm.Wrist.root, SeesTool(arm), OtherLimb(arm));
            // Right at a button: nothing bends back or folds over (AtButton). Nor for the stiff index finger (Stiff).
            var stiff = empty && arm.ShapeKnown && arm.Pointing;
            if (stiff || (empty && arm.ShapeKnown && VrButtons.AtButton(arm.Input) && !arm.Input.Grip.Down))
            {
                if (stiff) Stiff(arm, ref wrist, frame, side);
                else AtButton(arm, ref wrist, frame, side);
                Ease(arm, fromWrist, fromFrame, ref wrist, ref frame, true);
                return;
            }
            arm.Poking = false;
            var stays = false;
            var pushNeed = 0f;
            if (empty && arm.ShapeKnown && arm.Pressed && PalmSurface(arm, wrist, frame, side, out var under, out var underNormal))
            {
                // Lying flat: on what lies under the palm, while the controller's palm still faces it and is near it.
                var facing = Vector3.Dot(frame * Vector3.right * side, -underNormal);
                var gap = Vector3.Dot(wrist + frame * (new Vector3(side * PalmThickness, 0f, PalmLength) * scale) - under, underNormal);
                // Raik (2026-11-14: still pulled onto surfaces): no margin to let go any more, only the collision. It stays
                // flat while the controller's palm faces the surface and is in it; out of it, the hand is the controller's
                // again, folded only as far as its shape meets the surface (Fold), eased (Ease).
                stays = facing > PressFacing && gap < FlatFrom * scale;
                if (stays)
                {
                    pressed = true;
                    arm.FlatNormal = Vector3.Slerp(arm.FlatNormal, underNormal, 1f - Mathf.Exp(-Time.unscaledDeltaTime / SurfaceTurnTime)).normalized;
                    arm.FlatPoint = under;
                }
            }
            if (!stays && empty && arm.ShapeKnown && Wall(arm, wrist, frame, side, out var point, out var normal))
            {
                var facing = Vector3.Dot(frame * Vector3.right * side, -normal);
                var gap = Vector3.Dot(wrist + frame * (new Vector3(side * PalmThickness, 0f, PalmLength) * scale) - point, normal);
                pressed = arm.Pressed && facing > PressFacing && gap < FlatFrom * scale;
                if (!pressed && facing > 0f)
                {
                    pressed = Fold(arm, wrist, ref frame, side, point, normal);
                    // Raik (2026-11-14): no pull onto the surface any more for the empty hand; it lies flat only once it
                    // has folded over all the way (Fold), the fingers and the hand's shape do the rest.
                    // Folded over until it can fold no further: lying flat goes on from as far as it was folded.
                    if (pressed) arm.Press = Mathf.Max(arm.Press, arm.FoldedBefore);
                }
                else if (!pressed)
                {
                    var least = Mathf.Min(Tips(arm, wrist, frame, side, 0f, point, normal), Rest(arm, wrist, frame, side, point, normal));
                    least = Mathf.Min(least, Vector3.Dot(wrist + frame * (new Vector3(-side * 0.012f, 0f, PalmLength * 0.5f) * scale) - point, normal));
                    if (least < 0f && least > -BackPushAtMost * scale) wrist -= normal * least;
                }
                if (pressed)
                {
                    // From the surface found, at once when the hand did not lie flat before.
                    arm.FlatPoint = point;
                    arm.FlatNormal = arm.Press > 0f ? Vector3.Slerp(arm.FlatNormal, normal, 1f - Mathf.Exp(-Time.unscaledDeltaTime / SurfaceTurnTime)).normalized : normal;
                }
                else if (facing > 0f)
                {
                    // Raik (2026-11-14): turned so far against the surface that the fingers would bend back past
                    // KnuckleBackAtMost, the hand is pushed off it until they need no more.
                    var least = Mathf.Min(Tips(arm, wrist, frame, side, KnuckleBackAtMost, point, normal), Rest(arm, wrist, frame, side, point, normal));
                    if (least < 0f)
                    {
                        pushNeed = Mathf.Min(-least, PushOffAtMost * scale);
                        arm.PushNormal = normal;
                        if (pushLogs++ < 20) Plugin.Log.LogInfo($"Hands: {arm.Name} hand pushed {pushNeed * 100f:0.0} cm off a surface, its fingers would bend back past {KnuckleBackAtMost:0}°");
                    }
                }
            }
            // The push goes up at once and back over RelaxTime.
            arm.PushShown = empty && !pressed ? Mathf.Max(pushNeed, arm.PushShown - Time.unscaledDeltaTime * PushOffAtMost * scale / RelaxTime) : 0f;
            if (arm.PushShown > 0f) wrist += arm.PushNormal * arm.PushShown;
            if (!empty || pressed)
            {
                arm.FoldShown = arm.Folded;
                if (!empty) arm.BackShown = 0f;
            }
            else if (arm.Folded <= 0f && arm.FoldShown > 0f)
            {
                // Nothing to fold over onto this frame: the hand turns back to the controller over RelaxTime.
                arm.FoldShown = Mathf.Max(0f, arm.FoldShown - Time.unscaledDeltaTime / RelaxTime);
                if (arm.FoldShown > 0f) frame = Quaternion.Slerp(frame, FlatFrame(arm, frame, arm.FoldNormal), arm.FoldShown);
                arm.Folded = arm.FoldShown;
            }
            if (empty && !pressed)
            {
                arm.Back = Mathf.Max(arm.Back, arm.BackShown - Time.unscaledDeltaTime * MostBack / RelaxTime);
                arm.BackShown = arm.Back;
            }
            if (pressed && !arm.Pressed && pressLogs++ < 20) Plugin.Log.LogInfo($"Hands: {arm.Name} hand lies flat on a surface");
            if (arm.Folded > 0f && foldLogs++ < 20) Plugin.Log.LogInfo($"Hands: {arm.Name} hand folds over onto a surface, {arm.Folded * 100f:0}% of the way, fingers {arm.Back:0}° back");
            arm.Pressed = pressed;
            arm.FoldedBefore = arm.Folded;
            // Lying flat comes and goes with the collision, at once (Ease keeps it from jumping), not over CurlTime.
            arm.Press = pressed ? 1f : 0f;
            if (arm.Press > 0f)
            {
                frame = Quaternion.Slerp(frame, FlatFrame(arm, frame, arm.FlatNormal), arm.Press);
                var off = Vector3.Dot(wrist - arm.FlatPoint, arm.FlatNormal);
                wrist += arm.FlatNormal * ((PalmThickness * scale - off) * arm.Press);
            }
            if (empty && arm.Press < 1f) PalmOut(arm, ref wrist, frame, side);
            if (empty) wrist += arm.FingerPush;
            else arm.FingerPush = Vector3.zero;
            Ease(arm, fromWrist, fromFrame, ref wrist, ref frame, empty);
        }

        // Raik (0.30.1): right at a button the fingers stay straight and push it in; the hand neither lies flat nor folds
        // over, only the palm is kept out of things.
        private static void AtButton(Arm arm, ref Vector3 wrist, Quaternion frame, float side)
        {
            arm.Pressed = false;
            arm.Press = 0f;
            arm.Folded = arm.FoldShown = arm.FoldedBefore = 0f;
            arm.Back = arm.BackShown = 0f;
            arm.PushShown = 0f;
            arm.FingerPush = Vector3.zero;
            PalmOut(arm, ref wrist, frame, side);
        }

        // Raik (0.33.0): the index finger out of the fist, the grip held on nothing, is stiff, a finger held firm: it
        // neither bends back nor lets the hand fold over. Where it meets something (the straight finger's capsule,
        // VrHandShape) the whole hand stops, held back along the way out of it, however far the controller goes on;
        // how far that is, is the push on the body (VrClimb.Poke, through TryGetPoke), and the controller hums with it.
        // Right at a button the finger pushes it in instead, and is held back only once the button is all the way in
        // (VrButtons.Travel past its face at rest, VrButtons.TryGetFace).
        // The hum of the finger under load: from PokeHum at a touch to PokeHumMost with the controller PokeHumAt further in.
        private const float PokeHum = 0.05f;
        private const float PokeHumMost = 0.35f;
        private const float PokeHumAt = 0.1f;
        private static int stiffLogs;
        private static int pointLogs;

        private static void Stiff(Arm arm, ref Vector3 wrist, Quaternion frame, float side)
        {
            var scale = VrCamera.WorldScale;
            arm.Pressed = false;
            arm.Press = 0f;
            arm.Folded = arm.FoldShown = arm.FoldedBefore = 0f;
            arm.Back = arm.BackShown = 0f;
            arm.PushShown = 0f;
            arm.FingerPush = Vector3.zero;
            var from = wrist;
            Collider thing = null;
            if (VrButtons.AtButton(arm.Input) && VrButtons.TryGetFace(arm.Input, out var face, out var outward))
            {
                var deep = Vector3.Dot(face - (wrist + frame * arm.ShapeTip[0]), outward) - VrButtons.Travel * scale;
                if (deep > 0f) wrist += outward * deep;
            }
            else
            {
                for (var step = 0; step < 3; step++)
                {
                    var depth = VrHandShape.Capsule(wrist + frame * arm.ShapeKnuckle[0], wrist + frame * arm.ShapeTip[0],
                        VrHandShape.FingerRadius[0] * scale, out var way);
                    if (depth <= 0.0005f * scale || VrHandShape.FromTool || VrHandShape.FromArm) break;
                    wrist += way * depth;
                    if (VrHandShape.Met != null) thing = VrHandShape.Met;
                }
            }
            arm.Poke = wrist - from;
            arm.PokeAt = wrist + frame * arm.ShapeTip[0];
            arm.PokeThing = thing;
            arm.PokeFrame = Time.frameCount;
            var held = arm.Poke.magnitude / scale;
            if (held > 0f)
            {
                arm.Input.Buzz(Mathf.Lerp(PokeHum, PokeHumMost, Mathf.Clamp01(held / PokeHumAt)), 0.02f);
                if (!arm.Poking && stiffLogs++ < 20)
                    Plugin.Log.LogInfo($"Hands: {arm.Name} stiff index finger meets {(thing != null ? $"'{thing.name}'" : "a button all the way in")}, the hand held back {held * 100f:0.0} cm");
            }
            arm.Poking = held > 0f;
            PalmOut(arm, ref wrist, frame, side);
        }

        // What that controller's stiff index finger pushes against this frame (Stiff): how far it holds the hand back from
        // the controller, the way out of what it met; where its tip is; and what it met, null at a button.
        public static bool TryGetPoke(VrInput.Hand input, out Vector3 back, out Vector3 at, out Collider thing)
        {
            var arm = input == VrInput.Left ? LeftArm : RightArm;
            back = arm.Poke;
            at = arm.PokeAt;
            thing = arm.PokeThing;
            return Time.frameCount - arm.PokeFrame <= 1 && back.sqrMagnitude > 0f;
        }

        // Test (Raik, 2026-11-14: the hand still jumps where several faces meet): what Touch makes of the controller's hand
        // (folded, lying flat, the palm pushed out) is shown speeding up and slowing down towards it, within about
        // EaseTime, never in one frame. It may lag a few millimetres into a surface meanwhile. A hand that holds or works
        // something takes it at once.
        private const float EaseTime = 0.04f;

        private static void Ease(Arm arm, Vector3 fromWrist, Quaternion fromFrame, ref Vector3 wrist, ref Quaternion frame, bool empty)
        {
            var back = Quaternion.Inverse(fromFrame);
            var shift = back * (wrist - fromWrist);
            var turn = back * frame;
            var dt = Time.unscaledDeltaTime;
            if (!empty || dt <= 0f)
            {
                arm.EaseShift = shift;
                arm.EaseShiftSpeed = Vector3.zero;
                arm.EaseTurn = turn;
                arm.EaseTurnSpeed = 0f;
                return;
            }
            arm.EaseShift = Vector3.SmoothDamp(arm.EaseShift, shift, ref arm.EaseShiftSpeed, EaseTime, Mathf.Infinity, dt);
            var apart = Quaternion.Angle(arm.EaseTurn, turn);
            if (apart > 0.01f)
            {
                var left = Mathf.SmoothDamp(apart, 0f, ref arm.EaseTurnSpeed, EaseTime, Mathf.Infinity, dt);
                arm.EaseTurn = Quaternion.Slerp(turn, arm.EaseTurn, left / apart);
            }
            else
            {
                arm.EaseTurn = turn;
                arm.EaseTurnSpeed = 0f;
            }
            wrist = fromWrist + fromFrame * arm.EaseShift;
            frame = fromFrame * arm.EaseTurn;
        }

        private static int foldLogs;
        private static int pushLogs;
        private static int palmLogs;

        // The nearest surface the hand may meet: looked for from the wrist along the fingers, towards the palm, between
        // both, and towards the back and the side of the hand; the one the hand comes closest to (Tips, Rest) is taken.
        private static bool Wall(Arm arm, Vector3 wrist, Quaternion frame, float side, out Vector3 point, out Vector3 normal)
        {
            point = Vector3.zero;
            normal = Vector3.zero;
            var best = float.MaxValue;
            Collider chosen = null;
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
                    if (least < -IntoAtMost * VrCamera.WorldScale) continue;
                    // Where several faces meet (the log, 2026-11-14: four at a ship's nose), the face met last frame
                    // wins unless another is clearly closer, so the hand does not flip between them every frame.
                    var rank = least - (hit.collider == arm.TouchCollider && Vector3.Angle(hit.normal, arm.TouchNormal) < 5f ? KeepFace * VrCamera.WorldScale : 0f);
                    if (rank < best)
                    {
                        best = rank;
                        point = hit.point;
                        normal = hit.normal;
                        chosen = hit.collider;
                    }
                }
            }
            arm.TouchCollider = chosen;
            arm.TouchNormal = normal;
            return best < (FlatFrom + FlatRelease + 0.03f) * VrCamera.WorldScale;
        }

        private static readonly RaycastHit[] PalmHits = new RaycastHit[16];
        private static int roundLogs;
        // How far below the surface a point past an edge looks back for the face the edge turns down to.
        private const float EdgeDown = 0.004f;

        // What lies under the flat hand's palm (Test, Raik 2026-11-14): PalmGrid by PalmGrid points over the palm, as the
        // hand lies now (FlatNormal), each looking from PalmLook behind the palm towards it for the nearest surface out to
        // PalmFind in front of it. Their points and outward directions averaged, each weighed less the further in front of
        // the palm it lies. False when none finds one.
        private static bool PalmSurface(Arm arm, Vector3 wrist, Quaternion frame, float side, out Vector3 point, out Vector3 normal)
        {
            point = Vector3.zero;
            normal = Vector3.zero;
            if (arm.FlatNormal.sqrMagnitude < 0.5f) return false;
            var scale = VrCamera.WorldScale;
            var flat = FlatFrame(arm, frame, arm.FlatNormal);
            var palm = -arm.FlatNormal;
            var body = arm.Wrist.root;
            var length = Mathf.Max(arm.ShapeKnuckle[1].z, 0.05f * scale);
            var across0 = arm.ShapeKnuckle[0].y;
            var across3 = arm.ShapeKnuckle[3].y;
            // The palm's plane: the flat wrist lies PalmThickness off the surface.
            var plane = arm.FlatPoint + arm.FlatNormal * (PalmThickness * scale);
            var onPlane = wrist - arm.FlatNormal * Vector3.Dot(wrist - plane, arm.FlatNormal);
            var look = PalmLook * scale;
            var reach = look + (PalmThickness + PalmFind) * scale;
            var total = 0f;
            var rounded = 0;
            var middle = onPlane + flat * new Vector3(0f, (across0 + across3) * 0.5f, 0.6f * length);
            for (var a = 0; a < PalmGrid; a++)
                for (var b = 0; b < PalmGrid; b++)
                {
                    var along = Mathf.Lerp(0.2f, 1f, a / (PalmGrid - 1f)) * length;
                    var across = Mathf.Lerp(across0, across3, b / (PalmGrid - 1f));
                    var start = onPlane + flat * new Vector3(0f, across, along) - palm * look;
                    var count = Physics.RaycastNonAlloc(start, palm, PalmHits, reach, Physics.DefaultRaycastLayers, QueryTriggerInteraction.Ignore);
                    var nearest = -1;
                    for (var i = 0; i < count; i++)
                    {
                        var hit = PalmHits[i];
                        if (hit.distance <= 0f || Vector3.Dot(hit.normal, palm) >= 0f || !VrHandShape.Stops(hit.collider, body)) continue;
                        if (nearest < 0 || hit.distance < PalmHits[nearest].distance) nearest = i;
                    }
                    if (nearest < 0)
                    {
                        // Test (Raik, 2026-11-14): past an edge of 90 degrees the point finds nothing in front of the palm.
                        // Just below where the surface was, it looks back towards the palm's middle for the face the edge
                        // turns down to; that counts as fully as a surface under the palm, so the palm rounds over the edge
                        // by how much of it is past the edge, a few millimetres into the corner.
                        var depth = (PalmThickness + EdgeDown) * scale;
                        var from = start + palm * (look + depth);
                        var to = middle + palm * depth;
                        var back = to - from;
                        var far = back.magnitude;
                        if (far < 0.005f * scale) continue;
                        back /= far;
                        var backCount = Physics.RaycastNonAlloc(from, back, PalmHits, far + 0.02f * scale, Physics.DefaultRaycastLayers, QueryTriggerInteraction.Ignore);
                        for (var i = 0; i < backCount; i++)
                        {
                            var hit = PalmHits[i];
                            if (hit.distance <= 0f || Vector3.Dot(hit.normal, back) >= 0f || !VrHandShape.Stops(hit.collider, body)) continue;
                            if (nearest < 0 || hit.distance < PalmHits[nearest].distance) nearest = i;
                        }
                        if (nearest < 0) continue;
                        var face = PalmHits[nearest];
                        point += face.point;
                        normal += face.normal;
                        total += 1f;
                        rounded++;
                        continue;
                    }
                    var found = PalmHits[nearest];
                    var beyond = found.distance - look - PalmThickness * scale;
                    var weight = Mathf.Clamp01(1f - beyond / (PalmFind * scale));
                    if (weight <= 0f) continue;
                    point += found.point * weight;
                    normal += found.normal * weight;
                    total += weight;
                }
            if (total <= 0f || normal.sqrMagnitude < 1e-6f) return false;
            point /= total;
            normal.Normalize();
            if (rounded > 0 && roundLogs++ < 20)
                Plugin.Log.LogInfo($"Hands: {arm.Name} palm rounds over an edge, {rounded} of {PalmGrid * PalmGrid} points past it, turned {Vector3.Angle(normal, arm.FlatNormal):0}° from before");
            return true;
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
            // The way it folds over turns to a new face within about FoldTurnTime, not in one frame.
            var toward = arm.FoldShown > 0f && arm.FoldNormal.sqrMagnitude > 0.5f
                ? Vector3.Slerp(arm.FoldNormal, normal, 1f - Mathf.Exp(-Time.unscaledDeltaTime / FoldTurnTime)).normalized : normal;
            var flat = FlatFrame(arm, frame, toward);
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
            found = Mathf.Max(found, arm.FoldShown - Time.unscaledDeltaTime / RelaxTime);
            arm.FoldShown = found;
            arm.FoldNormal = toward;
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
        // Test (Raik, 2026-11-14): out of the tool in the other hand as out of a wall, the hand staying on its surface off
        // the controller and sliding along it; the tool does not move for it. Deeper in than MostIntoTool (meters at
        // world scale 1) the hand goes through instead of being pushed out the far side.
        private const float MostIntoTool = 0.1f;

        private static void PalmOut(Arm arm, ref Vector3 wrist, Quaternion frame, float side)
        {
            var boxes = PalmBoxes(arm, side);
            if (boxes == 0) return;
            StayOutOfTool(arm, ref wrist, frame, boxes);
            StayOutOfArm(arm, ref wrist, frame, boxes);
            for (var step = 0; step < 3; step++)
            {
                var depth = 0f;
                var way = Vector3.zero;
                var fromTool = false;
                for (var b = 0; b < boxes; b++)
                {
                    var deep = VrHandShape.Box(wrist + frame * PalmCenter[b], frame * PalmTurn[b], PalmHalf[b], out var boxWay);
                    if (deep <= depth) continue;
                    depth = deep;
                    way = boxWay;
                    fromTool = VrHandShape.FromTool || VrHandShape.FromArm;
                }
                if (depth <= 0.0005f) break;
                // The tool is StayOutOfTool's, the other arm StayOutOfArm's: pushed out of them here, the hand could go
                // through them.
                if (fromTool) break;
                wrist += way * depth;
                if (palmLogs++ < 20) Plugin.Log.LogInfo($"Hands: {arm.Name} palm pushed {depth * 100f:0.0} cm out of {(fromTool ? "the tool in the other hand" : "something")}");
            }
            ForearmOutOfTool(arm, ref wrist);
        }

        // Test (Raik, 2026-11-14): the forearm does not go into the tool in the other hand either: the hand is held back
        // until it is out, a little further than the tool keeps from it (VrToolCollide.PieceRadius), so the two do not
        // push each other back and forth. Points along it move with the wrist by how far along they lie.
        private const float ForearmRadius = 0.04f;
        // Not up to the wrist: the palm lies on the tool (PalmOut), and the forearm near it would lift it off.
        private static readonly float[] ForearmAt = { 0.25f, 0.45f, 0.65f };

        private static void ForearmOutOfTool(Arm arm, ref Vector3 wrist)
        {
            if (arm.Lower == null) return;
            var scale = VrCamera.WorldScale;
            var elbow = arm.Lower.position;
            var moved = Vector3.zero;
            for (var step = 0; step < 2; step++)
            {
                var need = 0f;
                var way = Vector3.zero;
                foreach (var t in ForearmAt)
                {
                    var depth = ForearmRadius * scale - VrToolCollide.WorldDistance(Vector3.Lerp(elbow, wrist, t), out var outward);
                    if (depth <= 0f || depth / t <= need) continue;
                    need = depth / t;
                    way = outward;
                }
                if (need <= 0.0005f || need > MostIntoTool * scale) break;
                wrist += way * need;
                moved += way * need;
            }
            if (moved != Vector3.zero && forearmLogs++ < 20)
                Plugin.Log.LogInfo($"Hands: {arm.Name} forearm kept out of the tool in the other hand, the hand held back {moved.magnitude * 100f / scale:0.0} cm");
        }

        private static int forearmLogs;

        // Test (Raik, 2026-11-14: a hand pushed through the tool, and its physics went wild): the palm never goes through
        // the tool in the other hand. It stays on the side it came against it from, kept in the tool's own axes: pushed
        // out the shortest way while that is still this side, else (past the tool's middle, or deeper than MostIntoTool)
        // marched back along that side's way until it is out. The controller may go through; the hand stays at the
        // surface and slides along it. The side is forgotten ForgetToolSide seconds after the palm left the tool.
        private const float ForgetToolSide = 0.3f;
        private const float StayStep = 0.01f;
        private const float StayFar = 0.5f;
        private static int stayLogs;

        private static void StayOutOfTool(Arm arm, ref Vector3 wrist, Quaternion frame, int boxes)
        {
            var now = Time.unscaledTime;
            if (!VrToolCollide.ToolTurn(out var toolTurn))
            {
                arm.ToolSide = Vector3.zero;
                return;
            }
            var depth = PalmInTool(wrist, frame, boxes, out var way);
            if (depth <= 0.0005f)
            {
                if (now - arm.ToolSideAt > ForgetToolSide) arm.ToolSide = Vector3.zero;
                return;
            }
            arm.ToolSideAt = now;
            var scale = VrCamera.WorldScale;
            if (arm.ToolSide == Vector3.zero) arm.ToolSide = Quaternion.Inverse(toolTurn) * way;
            var kept = toolTurn * arm.ToolSide;
            if (Vector3.Dot(way, kept) > 0.3f && depth <= MostIntoTool * scale)
            {
                // Still this side: out the shortest way, and the side follows the surface as the hand slides round it.
                wrist += way * depth;
                arm.ToolSide = Quaternion.Inverse(toolTurn) * way;
                if (PalmInTool(wrist, frame, boxes, out var again) > 0.0005f) wrist += again * PalmInTool(wrist, frame, boxes, out _);
                return;
            }
            // Through it, or past its middle: back along the kept side's way until the palm is out.
            var least = 0f;
            var most = -1f;
            for (var d = StayStep; d <= StayFar + 1e-4f; d += StayStep)
            {
                if (PalmInTool(wrist + kept * d * scale, frame, boxes, out _) <= 0.0005f) { most = d; break; }
                least = d;
            }
            if (most < 0f) return;
            for (var i = 0; i < 4; i++)
            {
                var middle = (least + most) * 0.5f;
                if (PalmInTool(wrist + kept * middle * scale, frame, boxes, out _) <= 0.0005f) most = middle; else least = middle;
            }
            wrist += kept * most * scale;
            if (stayLogs++ < 20) Plugin.Log.LogInfo($"Hands: {arm.Name} palm kept on its side of the tool in the other hand, {most * 100f:0.0} cm off the controller");
        }

        // Test (Raik, 2026-11-14): the palm stays on its side of the other arm as of the tool (StayOutOfTool), kept in the
        // other forearm's axes, so it slides over the arm and never comes out on its far side.
        private static int armStayLogs;

        private static void StayOutOfArm(Arm arm, ref Vector3 wrist, Quaternion frame, int boxes)
        {
            var now = Time.unscaledTime;
            var limb = OtherLimb(arm);
            if (!limb.Known)
            {
                arm.ArmSide = Vector3.zero;
                return;
            }
            var depth = PalmInArm(limb, wrist, frame, boxes, out var way);
            if (depth <= 0.0005f)
            {
                if (now - arm.ArmSideAt > ForgetToolSide) arm.ArmSide = Vector3.zero;
                return;
            }
            arm.ArmSideAt = now;
            var scale = VrCamera.WorldScale;
            if (arm.ArmSide == Vector3.zero) arm.ArmSide = Quaternion.Inverse(limb.Turn) * way;
            var kept = limb.Turn * arm.ArmSide;
            if (Vector3.Dot(way, kept) > 0.3f)
            {
                wrist += way * depth;
                arm.ArmSide = Quaternion.Inverse(limb.Turn) * way;
                var again = PalmInArm(limb, wrist, frame, boxes, out var againWay);
                if (again > 0.0005f) wrist += againWay * again;
                return;
            }
            var least = 0f;
            var most = -1f;
            for (var d = StayStep; d <= StayFar + 1e-4f; d += StayStep)
            {
                if (PalmInArm(limb, wrist + kept * d * scale, frame, boxes, out _) <= 0.0005f) { most = d; break; }
                least = d;
            }
            if (most < 0f) return;
            for (var i = 0; i < 4; i++)
            {
                var middle = (least + most) * 0.5f;
                if (PalmInArm(limb, wrist + kept * middle * scale, frame, boxes, out _) <= 0.0005f) most = middle; else least = middle;
            }
            wrist += kept * most * scale;
            if (armStayLogs++ < 20) Plugin.Log.LogInfo($"Hands: {arm.Name} palm kept on its side of the other arm, {most * 100f:0.0} cm off the controller");
        }

        private static float PalmInArm(VrHandShape.Limb limb, Vector3 wrist, Quaternion frame, int boxes, out Vector3 way)
        {
            way = Vector3.zero;
            var deepest = 0f;
            for (var b = 0; b < boxes; b++)
            {
                var center = wrist + frame * PalmCenter[b];
                var turn = frame * PalmTurn[b];
                var half = PalmHalf[b];
                for (var x = -1; x <= 1; x++)
                    for (var y = -1; y <= 1; y++)
                        for (var z = -1; z <= 1; z++)
                        {
                            var depth = -VrHandShape.ArmDistance(limb, center + turn * Vector3.Scale(half, new Vector3(x, y, z)), out var outward);
                            if (depth <= deepest) continue;
                            deepest = depth;
                            way = outward;
                        }
            }
            return deepest;
        }

        // How deep the palm's boxes (PalmBoxes, as last worked out) lie in the tool in the other hand, and the way out.
        private static float PalmInTool(Vector3 wrist, Quaternion frame, int boxes, out Vector3 way)
        {
            way = Vector3.zero;
            var deepest = 0f;
            for (var b = 0; b < boxes; b++)
            {
                var center = wrist + frame * PalmCenter[b];
                var turn = frame * PalmTurn[b];
                var half = PalmHalf[b];
                for (var x = -1; x <= 1; x++)
                    for (var y = -1; y <= 1; y++)
                        for (var z = -1; z <= 1; z++)
                        {
                            var depth = -VrToolCollide.WorldDistance(center + turn * Vector3.Scale(half, new Vector3(x, y, z)), out var outward);
                            if (depth <= deepest) continue;
                            deepest = depth;
                            way = outward;
                        }
            }
            return deepest;
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
                // The charge itself is set in front of the controller instead (ChargeInHand).
                if (VrChargeByHand.Decoupled && IsChargeProp(piece))
                {
                    if (moved.TryGetValue(piece, out var set))
                    {
                        if (piece.localPosition == set.SetPosition && piece.localRotation == set.SetRotation) SetLocal(piece, set.Position, set.Rotation);
                        moved.Remove(piece);
                    }
                    continue;
                }
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
