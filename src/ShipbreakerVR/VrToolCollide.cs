using System.Collections.Generic;
using System.Linq;
using BBI.Unity.Game;
using HarmonyLib;
using UnityEngine;

namespace ShipbreakerVR
{
    // Steuerung/ErweiterteWerkzeugphysik (extended tool physics): cutter and grapple bump into things, as Raik asked. The tool stays
    // in the hand and turns about the grip instead of going through what is in its way: swung against a bar it stops
    // there and is turned aside as the hand goes on, and pulled past it, it follows the controller again, catching up
    // over CatchUp. The tool's shape is a box around each part of its model (Shape): the grapple's housing and its arms,
    // not a line along it, as it is far from symmetric; without a model in the hand (the second tool) a thin ball along
    // the tool from the hand to its muzzle (VrToolModels.Muzzle). A turn that would put the shape into something goes
    // only as far as it is free; a tool that the moving hand has pushed into something is turned out of it, away from
    // what it touches. The tool's turn is the aim's (VrToolSteady), so the laser shows where the tool points, and the
    // hand holding it turns with it (VrHands.Pose), so the wrist never twists against the tool.
    internal static class VrToolCollide
    {
        // The ball, of this radius, from this far ahead of the grip to the muzzle, in meters at world scale 1.
        private const float Radius = 0.04f;
        private const float FromGrip = 0.1f;
        private const float Fallback = 0.4f;
        // The model's parts: boxes smaller than Smallest (half size, meters) left out, at most MostBoxes of the largest,
        // each shrunk to Shrink of its size so the tool does not catch on what it only grazes.
        private const float Smallest = 0.01f;
        private const int MostBoxes = 16;
        private const float Shrink = 0.85f;
        // Friction: a tool pressed against something within Hold of straight on stays where it is (Raik: 15°); further off
        // it slides along it, slowly at first (SlideSlow, degrees per second) and the faster the more slanted, up to
        // SlideFast from SlideFull on.
        private const float Hold = 15f;
        private const float SlideFull = 45f;
        private const float SlideSlow = 15f;
        private const float SlideFast = 270f;
        // A tool thrust in along itself is held back at what it meets, with the hand on it, instead of sinking in: up to
        // MostBack, found in BackStep steps and then to BackFine, in meters at world scale 1. Further in, it goes through.
        private const float MostBack = 0.2f;
        private const float BackStep = 0.02f;
        private const float BackFine = 0.005f;
        // The steps a tool turns in, tested each, in degrees.
        private const float Step = 2f;
        // How far a tool is turned out of what it went into, each step, in degrees.
        private const float OutStep = 1f;
        // A turn this far off the controller is given up: the tool goes back to the controller, through the obstacle.
        private const float MostOff = 120f;
        // A tool that was held off comes back to the controller over about this time, in seconds, not in one frame.
        private const float CatchUp = 0.12f;
        // Out of the other arm: in up to ArmTries pushes, each this much past its surface, at most MostArmPush in all, in
        // meters at world scale 1; deeper, the tool goes through rather than drag the hand far off the controller.
        private const int ArmTries = 4;
        private const float ArmGap = 0.003f;
        // Test (Raik, 2026-11-14: the physics went wild once a hand was through the tool): no further than this, no jump.
        private const float MostArmPush = 0.06f;

        private static readonly AccessTools.FieldRef<GrabController, Rigidbody> PlayerBody =
            AccessTools.FieldRefAccess<GrabController, Rigidbody>("m_PlayerRigidbodyReference");
        private static readonly RaycastHit[] Hits = new RaycastHit[16];
        private static readonly Collider[] Overlaps = new Collider[16];

        internal struct Box
        {
            // In the aim's axes, from the grip, turned as its part is: the part's own bounds, not a box around them
            // along the aim (which made the grapple's housing 60 cm tall).
            public Vector3 Center;
            public Vector3 Half;
            public Quaternion Turn;
            // Experiment (Raik, 2026-11-17, VrLooseTools.Exact): the part's shape as its model shows it (VrToolShape),
            // with the part's own origin and turn in the aim's axes. The box, at its full size (Full), then only finds
            // what is near; the pieces decide whether the tool is in it.
            public Vector3 Full;
            public Vector3 Origin;
            public Quaternion PartTurn;
            public VrToolShape.Part Part;
        }

        // A tool's piece may sink this far into something before it counts as in it, in meters at world scale 1, so the
        // tool does not catch on what it only grazes (what shrinking the boxes was for).
        private const float Graze = 0.004f;

        // The game tool's shape, built once per tool model once it is in the hand.
        private static Transform shapeOf;
        private static EquipmentController.Equipment shapeTool;
        private static Box[] shape;
        // Built again now and then: the cutter gets its mirrored side (VrToolMirror) after it is drawn.
        private const float ShapeAgain = 2f;
        private static float shapeAt;
        private static readonly HashSet<EquipmentController.Equipment> shapeLogged = new HashSet<EquipmentController.Equipment>();

        private class State
        {
            public bool Running;
            public int Frame;
            public Quaternion Rotation;
            public Quaternion Result;
            // Off the controller last frame: blocked, or catching up after it.
            public bool Blocked;
            // How far the tool and the hand are held back from the controller along the tool, in tracking space.
            public Vector3 Back;
            public bool HeldBack;
            public bool ArmPushed;
            public bool ArmTurned;
            // How far the other arm turned the tool aside last frame, on top of where it would be without the arm.
            public Quaternion ArmTurn = Quaternion.identity;
            // The grip last frame, in tracking space.
            public Vector3 Pivot;
        }

        private static readonly State Left = new State();
        private static readonly State Right = new State();
        private static int logs;

        // What a test found in the tool's way: the thing, and the tool's point that went into it; for a box of the tool's
        // shape also the way out of it, from the physics engine (Separated).
        private struct Contact
        {
            public Collider Thing;
            public Vector3 At;
            public bool Separated;
            public Vector3 Out;
            // How deep in, for the other arm: the hand and tool are pushed out of it by that much (HoldBack).
            public float Depth;
        }

        // A box collider never in the scene's physics (disabled), only for Physics.ComputePenetration: the way out of what
        // a box of the tool's shape went into. From the box's middle instead (Away), the grapple's tall housing, whose
        // middle lies above the tool's line, gave a slanted way out of a wall met head on, and the tool hopped up or down
        // along it (Raik: the grapple jumps when thrust straight at something).
        private static BoxCollider probe;
        private static bool probeLogged;

        private static void Probes()
        {
            if (probe != null && armProbe != null) return;
            var holder = new GameObject("ShipbreakerVR tool probe") { hideFlags = HideFlags.HideAndDontSave };
            Object.DontDestroyOnLoad(holder);
            probe = holder.AddComponent<BoxCollider>();
            probe.isTrigger = true;
            probe.enabled = false;
            armProbe = holder.AddComponent<CapsuleCollider>();
            armProbe.isTrigger = true;
            armProbe.enabled = false;
            armProbe.direction = 1;
        }

        // Physics.ComputePenetration finds nothing for a disabled collider (the log said 'found none' at walls, and the
        // grapple went through the other arm, Raik 2026-11-08): the probes are on just for the call, as triggers, with no
        // physics step in between, so nothing ever touches them.
        private static bool Penetration(Collider a, Vector3 aAt, Quaternion aTurn, Collider b, Vector3 bAt, Quaternion bTurn, out Vector3 direction, out float distance)
        {
            var aWas = a.enabled;
            var bWas = b.enabled;
            a.enabled = true;
            b.enabled = true;
            var found = Physics.ComputePenetration(a, aAt, aTurn, b, bAt, bTurn, out direction, out distance);
            a.enabled = aWas;
            b.enabled = bWas;
            return found;
        }

        // The other arm, which the tool does not pass through and cannot push aside (Raik, 2026-11-08): its upper arm and
        // forearm as drawn (VrHands), set for each tool's turn (Apply), and its hand to the finger tips while it does not
        // hold the tool as a second hand (VrToolSupport).
        // Test (Raik, 2026-11-14): a little thinner than the forearm VrHands keeps out of the tool (ForearmRadius).
        private const float ArmRadius = 0.035f;
        private const float HandRadius = 0.012f;
        private const float Fingers = 0.08f;
        private static CapsuleCollider armProbe;
        internal static bool armKnown;
        internal static int armPieces;
        internal static readonly Vector3[] ArmJoints = new Vector3[4];
        // How fast each joint moves, from where it was the frame before (per arm, as the tool may change hands).
        private static readonly Vector3[] JointSpeed = new Vector3[4];
        private static readonly Vector3[][] JointsBefore = { new Vector3[4], new Vector3[4] };
        private static readonly int[] JointsFrame = { -10, -10 };

        // Raik, 0.30.1: the own body and the tool do not meet for now. The tool passes through the other arm and hand
        // (OtherArm), and the free hand and forearm pass through the tool (WorldTool: VrHands, VrHandShape). The tool still
        // meets the world, and a second hand still takes hold of it (VrToolSupport, Distance).
        internal static readonly bool BodyMeetsTool = false;

        private static void OtherArm(VrInput.Hand hand)
        {
            if (!BodyMeetsTool)
            {
                armKnown = false;
                return;
            }
            armKnown = VrHands.TryGetArm(!hand.IsLeft, out ArmJoints[0], out ArmJoints[1], out ArmJoints[2], out var knuckles);
            var along = knuckles - ArmJoints[2];
            // Test (Raik, 2026-11-14): the hand as a thin line from the wrist to the knuckles, inside its palm, so a hand lying
            // on the tool (VrHands.PalmOut) does not touch it here and the two never push each other about; the fingers
            // bend out of the tool themselves. Not while it holds the tool as a second hand.
            armPieces = 2;
            ArmJoints[3] = knuckles;
            var other = hand.IsLeft ? VrInput.Right : VrInput.Left;
            if (armKnown && along.sqrMagnitude > 1e-6f && !VrToolSupport.Supports(other)) armPieces = 3;
            var side = hand.IsLeft ? 1 : 0;
            var before = JointsBefore[side];
            var dt = Time.unscaledDeltaTime;
            var known = armKnown && JointsFrame[side] == Time.frameCount - 1 && dt > 1e-4f;
            for (var j = 0; j < 4; j++)
            {
                JointSpeed[j] = known ? (ArmJoints[j] - before[j]) / dt : Vector3.zero;
                before[j] = ArmJoints[j];
            }
            JointsFrame[side] = armKnown ? Time.frameCount : -10;
        }

        // The point on a to b nearest to p, and how far along it lies (0 to 1).
        private static Vector3 OnSegment(Vector3 a, Vector3 b, Vector3 p, out float t)
        {
            var line = b - a;
            t = Mathf.Clamp01(Vector3.Dot(p - a, line) / Mathf.Max(line.sqrMagnitude, 1e-8f));
            return a + line * t;
        }

        internal static float PieceRadius(int j) => (j == 2 ? HandRadius : ArmRadius) * VrCamera.WorldScale;

        // Whether the tool's line (the ball along it, without a model) comes within reach of the other arm.
        private static bool LineInArm(Vector3 start, Vector3 end, float reach, out Vector3 at, out Vector3 way, out float depth) =>
            LineInArm(start, end, reach, out at, out way, out depth, out _, out _);

        private static bool LineInArm(Vector3 start, Vector3 end, float reach, out Vector3 at, out Vector3 way, out float depth, out int piece, out float along)
        {
            at = way = Vector3.zero;
            depth = 0f;
            piece = 0;
            along = 0f;
            if (!armKnown) return false;
            for (var j = 0; j < armPieces; j++)
            {
                var radius = PieceRadius(j) + reach;
                VrUtil.ClosestOnSegments(start, end, ArmJoints[j], ArmJoints[j + 1], out var onTool, out var onArm);
                var gap = onTool - onArm;
                if (gap.magnitude >= radius) continue;
                at = onTool;
                way = gap.sqrMagnitude > 1e-8f ? gap.normalized : Vector3.up;
                depth = radius - gap.magnitude;
                piece = j;
                OnSegment(ArmJoints[j], ArmJoints[j + 1], onArm, out along);
                return true;
            }
            return false;
        }

        private static bool Separate(Collider thing, Vector3 center, Vector3 half, Quaternion turn, out Vector3 way)
        {
            way = Vector3.zero;
            Probes();
            probe.size = half * 2f;
            probe.center = Vector3.zero;
            var separated = Penetration(probe, center, turn, thing, thing.transform.position, thing.transform.rotation, out var direction, out var distance);
            if (!probeLogged)
            {
                probeLogged = true;
                Plugin.Log.LogInfo($"Tool collision: way out from the physics engine {(separated ? $"works ({distance * 100f:0.0} cm)" : "found none, from the box's middle instead")}");
            }
            if (!separated || direction.sqrMagnitude < 1e-8f) return false;
            way = direction.normalized;
            return true;
        }

        // The tool's turn in tracking space for the turn the controller (filtered) wants, wanted.
        public static Quaternion Apply(VrInput.Hand hand, EquipmentController.Equipment tool, Quaternion wanted)
        {
            var state = hand.IsLeft ? Left : Right;
            var rig = VrCamera.Instance != null ? VrCamera.Instance.transform.parent : null;
            if (VrSettings.ToolPhysics == null || !VrSettings.ToolPhysics.Value || rig == null || !hand.TryGetPosition(out var gripLocal)
                || (tool != EquipmentController.Equipment.CuttingTool && tool != EquipmentController.Equipment.GrappleHook))
            {
                state.Running = false;
                return wanted;
            }
            // Not run last frame: the tool was put away, it starts afresh at the controller (as VrToolWeight).
            if (state.Running && state.Frame < Time.frameCount - 1) state.Running = false;
            if (state.Running && state.Frame == Time.frameCount) return state.Result;
            state.Frame = Time.frameCount;
            state.Back = Vector3.zero;
            OtherArm(hand);
            var pivot = rig.TransformPoint(VrToolWeight.Spring(hand, tool, gripLocal));
            var desired = rig.rotation * wanted;
            var previous = state.Running ? rig.rotation * state.Rotation : desired;
            var lastTurn = previous;
            var lastPivot = state.Running ? rig.TransformPoint(state.Pivot) : pivot;
            if (!state.Running) state.ArmTurn = Quaternion.identity;
            state.Running = true;
            var boxes = hand == VrHand.GameToolHand.Input ? Shape(tool) : null;
            var length = ToolLength(pivot, desired);

            // The tool never jumps: it turns from where it was towards the controller in steps of Step, each one tested, and
            // stops before the first that goes into something, so it cannot pass through a thin wall between two frames.
            // Held off, it comes back over CatchUp, not at once, also once the way is free.
            var share = state.Blocked ? 1f - Mathf.Exp(-Time.unscaledDeltaTime / CatchUp) : 1f;
            // The hand's movement may have put the tool into something: turned out of it, a little each frame.
            // The other arm is not an obstacle here but pushes the tool about the wrist (ByArm, after the turn).
            if (!Free(pivot, previous, boxes, length, out var contact, false))
            {
                var away = Away(contact);
                // Pressed nearly straight against it, the tip holds there (friction); at a slant it slides out along it, the
                // faster the more slanted. Pushing the body back off a hard press, and pushing heavy parts with the tool,
                // were tried and left out (Raik: not yet right).
                var tilt = Vector3.Angle(previous * Vector3.forward, -away);
                if (tilt > Hold)
                {
                    var speed = Mathf.Lerp(SlideSlow, SlideFast, Mathf.InverseLerp(Hold, SlideFull, tilt));
                    previous = Out(pivot, previous, boxes, length, contact.At, away, speed * Time.unscaledDeltaTime);
                }
            }
            var result = Towards(pivot, previous, desired, share, boxes, length);
            // Held off this far, it gives up the obstacle and comes back to the controller through it, still over CatchUp.
            if (Quaternion.Angle(result, desired) > MostOff) result = Quaternion.Slerp(result, desired, 1f - Mathf.Exp(-Time.unscaledDeltaTime / CatchUp));
            // Held off by the world only: the other arm pushes it aside each frame anew, and it turns freely meanwhile.
            var blocked = Quaternion.Angle(result, desired) > 1f;
            // Test (Raik, 2026-11-14): the other arm and hand neither turn nor push the tool; they only stop it. Turning it
            // stops at them (Towards), and moved into them it stays at their surface, sliding along (HoldBack), the hand
            // on it held back with it. ByArm is left out for now.
            HoldBack(state, rig, pivot, result, boxes, length, hand, tool);
            state.Pivot = rig.InverseTransformPoint(pivot);
            if (blocked != state.Blocked && logs++ < 30)
                Plugin.Log.LogInfo($"Tool collision: {hand.Name} {tool} {(blocked ? $"held off, {Quaternion.Angle(result, desired):0}° from the controller" : "with the controller again")}");
            state.Blocked = blocked;
            state.Rotation = Quaternion.Inverse(rig.rotation) * result;
            state.Result = state.Rotation;
            return state.Result;
        }

        // The other arm pushes the tool about the wrist (Raik, 2026-11-08): the tool keeps turning with the controller, and
        // where the arm is in its way the arm turns it aside, the way the arm moves against the tool (or the tool against
        // the arm), not the shortest way out, which tipped the grapple up onto a hand pressing its lower front corner.
        // Without a move between them, the shortest way out, across the lever only. Near the grip, where turning does
        // little, HoldBack pushes tool and hand aside instead.
        private const float LeastPush = 0.05f;
        private const float ShortestLever = 0.08f;
        private const float MostArmTurn = 60f;
        private const int ArmTurnSteps = 8;
        // At most this fast, in degrees per second (a little over 6° a frame at 90 fps), so a sudden contact does not
        // throw the tool round in one frame; what is left HoldBack pushes aside. Let go over ArmLetGo seconds.
        private const float ArmTurnRate = 540f;
        private const float ArmLetGo = 0.08f;

        // Where the other arm last met the tool and the way the tool was turned (world), for a local view of the shape.
        internal static Vector3 ContactAt, ContactWay;
        internal static int ContactFrame = -10;

        // For a local view of the shapes (VrToolShapeView): the boxes of the tool in the hand, the ball along it, its length.
        internal static Box[] ViewBoxes(EquipmentController.Equipment tool) => Shape(tool);
        internal const float ViewRadius = Radius;
        internal const float ViewFromGrip = FromGrip;
        internal static float ViewLength(Vector3 pivot, Quaternion rotation) => ToolLength(pivot, rotation);

        // The tool hand's grip and the tool's turn as last worked out (world), for a local view of the shape.
        internal static bool ToolPose(out Vector3 pivot, out Quaternion rotation)
        {
            pivot = Vector3.zero;
            rotation = Quaternion.identity;
            var hand = VrHand.GameToolHand.Input;
            var rig = VrCamera.Instance != null ? VrCamera.Instance.transform.parent : null;
            if (hand == null || rig == null) return false;
            var state = hand.IsLeft ? Left : Right;
            if (!state.Running || state.Frame < Time.frameCount - 1) return false;
            pivot = rig.TransformPoint(state.Pivot);
            rotation = rig.rotation * state.Result;
            return true;
        }

        private static Quaternion ByArm(State state, Vector3 pivot, Quaternion rotation, Vector3 lastPivot, Quaternion lastTurn,
            Box[] boxes, float length, VrInput.Hand hand, EquipmentController.Equipment tool)
        {
            var scale = VrCamera.WorldScale;
            if (!ArmContact(pivot, rotation, boxes, length, out var armAt, out var piece, out var along, out var way, out var depth))
            {
                ReportTurn(state, false, hand, tool, 0f);
                return rotation;
            }
            ContactAt = armAt;
            ContactWay = way;
            ContactFrame = Time.frameCount;
            var lever = armAt - pivot;
            if (lever.magnitude < ShortestLever * scale) return rotation;
            var dt = Mathf.Max(Time.unscaledDeltaTime, 1e-4f);
            var armSpeed = Vector3.Lerp(JointSpeed[piece], JointSpeed[piece + 1], along);
            // Where the tool's point at the arm was last frame, carried by the tool's last pose.
            var before = lastPivot + lastTurn * (Quaternion.Inverse(rotation) * lever);
            var toolSpeed = (armAt - before) / dt;
            var push = Vector3.ProjectOnPlane(armSpeed - toolSpeed, lever);
            if (push.magnitude < LeastPush * scale) push = Vector3.ProjectOnPlane(way, lever);
            if (push.sqrMagnitude < 1e-8f) return rotation;
            var axis = Vector3.Cross(lever, push).normalized;
            var turned = 0f;
            var most = Mathf.Min(MostArmTurn, ArmTurnRate * dt);
            for (var i = 0; i < ArmTurnSteps && turned < most; i++)
            {
                var step = Mathf.Clamp(Mathf.Atan2(depth + ArmGap * scale, lever.magnitude) * Mathf.Rad2Deg, 0.1f, most - turned);
                rotation = Quaternion.AngleAxis(step, axis) * rotation;
                turned += step;
                if (!ArmContact(pivot, rotation, boxes, length, out _, out _, out _, out _, out depth)) break;
            }
            ReportTurn(state, true, hand, tool, turned);
            return rotation;
        }

        // Where the tool lies in the other arm: the point on the arm's line, which piece and how far along it, the tool's
        // shortest way out and how deep.
        private static bool ArmContact(Vector3 pivot, Quaternion rotation, Box[] boxes, float length, out Vector3 armAt, out int piece,
            out float along, out Vector3 way, out float depth)
        {
            armAt = way = Vector3.zero;
            piece = 0;
            along = depth = 0f;
            if (!armKnown) return false;
            // The grapple in the hand with its model: its housing and its four arms (GrappleShape).
            if (boxes != null && shapeTool == EquipmentController.Equipment.GrappleHook && GrappleShape(out var housing, out var arms))
                return InGrapple(pivot, rotation, housing, arms, out armAt, out piece, out along, out way, out depth);
            // Otherwise the ball along it, as the cutter (Raik: the cutter's way felt right).
            var scale = VrCamera.WorldScale;
            var forward = rotation * Vector3.forward;
            var start = pivot + forward * FromGrip * scale;
            var distance = length - FromGrip * scale;
            if (distance <= 0f) return false;
            return LineInArm(start, start + forward * distance, Radius * scale, out armAt, out way, out depth, out piece, out along);
        }

        // The grapple against the other arm (Raik, 2026-11-10: the ball along it did not fit the grapple): its housing as a
        // box in the aim's axes ('Main_1' with its spool 'Spool_1'), without the narrow ring under it, and its four arms
        // ('Teeth_1' to 'Teeth_4') each as a capsule along its longest side, read anew each time as they open and close.
        // The ring is found from the housing's mesh, where it is readable: the bottom rows where the housing is narrower
        // than RingNarrow of its widest are left out.
        private const string GrapplePart = "GB_GrappleTool_";
        private static readonly string[] HousingParts = { "Main_1", "Spool_1" };
        private const string ArmPart = "Teeth_";
        private const float RingNarrow = 0.5f;
        private const int RingRows = 24;
        private const int ArmSamples = 12;

        internal struct Capsule
        {
            public Vector3 A, B;
            public float Radius;
        }

        // The housing as it is (Raik, 2026-11-11: the box around it, 27 x 39 x 59 cm, was larger than the grapple, which
        // turned away from an arm that did not touch it): its own shape (VrShapeField) in cells of FieldCell, known out to
        // FieldMargin, in meters at world scale 1. The box only where the mesh cannot be read.
        private const float FieldCell = 0.015f;
        private const float FieldMargin = 0.08f;
        internal static VrShapeField grappleField;
        private static Transform grappleOf;
        private static Bounds grappleHousing;
        private static readonly List<Transform> GrappleArms = new List<Transform>();
        internal static readonly Capsule[] ArmCapsules = new Capsule[4];
        internal static int armCapsules;

        private static bool GrappleShape(out Bounds housing, out Capsule[] arms)
        {
            housing = default;
            arms = ArmCapsules;
            if (!VrToolModels.TryGetFrame(out var model, out var mount, out var turn) || !Settled(model, out var root)) return false;
            // Built again once the tool sits differently in the hand (built while it still glided in from the hip, the
            // housing lay 1.5 m below the grip, Raik 2026-11-10).
            if (model != grappleOf || root != grappleRoot || Vector3.Distance(root.localPosition, grappleAt) > 0.01f
                || Quaternion.Angle(root.localRotation, grappleTurn) > 2f)
            {
                if (!BuildGrapple(model, mount, turn)) return false;
                grappleRoot = root;
                grappleAt = root.localPosition;
                grappleTurn = root.localRotation;
            }
            housing = grappleHousing;
            armCapsules = 0;
            var scale = model.lossyScale.x;
            foreach (var part in GrappleArms)
            {
                if (part == null || armCapsules >= ArmCapsules.Length) continue;
                var filter = part.GetComponent<MeshFilter>();
                if (filter == null || filter.sharedMesh == null) continue;
                var bounds = filter.sharedMesh.bounds;
                var center = ToAim(model, mount, turn, scale, part.TransformPoint(bounds.center));
                var partTurn = turn * (Quaternion.Inverse(model.rotation) * part.rotation);
                var half = Vector3.Scale(bounds.extents, part.lossyScale);
                var axis = half.x >= half.y && half.x >= half.z ? 0 : half.y >= half.z ? 1 : 2;
                var along = axis == 0 ? Vector3.right : axis == 1 ? Vector3.up : Vector3.forward;
                var radius = Mathf.Max(0.01f * VrCamera.WorldScale, (half.x + half.y + half.z - half[axis]) * 0.5f);
                var reach = Mathf.Max(half[axis] - radius, 0f);
                ArmCapsules[armCapsules++] = new Capsule { A = center - partTurn * along * reach, B = center + partTurn * along * reach, Radius = radius };
            }
            return true;
        }

        // Whether the tool under the model point has sat still in the hand for SettledFor seconds: drawn, it glides in
        // from the belt first, and a shape built meanwhile lies wherever it was.
        private const float SettledFor = 0.25f;
        private static Transform settledRoot, grappleRoot;
        private static Vector3 settledAt, grappleAt;
        private static Quaternion settledTurn, grappleTurn;
        private static float settledSince;

        private static bool Settled(Transform model, out Transform root)
        {
            root = null;
            foreach (Transform child in model)
                if (child.gameObject.activeInHierarchy && child.GetComponentInChildren<MeshRenderer>() != null) { root = child; break; }
            if (root == null) return false;
            var now = Time.unscaledTime;
            if (root != settledRoot || Vector3.Distance(root.localPosition, settledAt) > 0.002f || Quaternion.Angle(root.localRotation, settledTurn) > 0.5f)
            {
                settledRoot = root;
                settledAt = root.localPosition;
                settledTurn = root.localRotation;
                settledSince = now;
            }
            return now - settledSince >= SettledFor;
        }

        // A point of the model, in the aim's axes from the grip.
        private static Vector3 ToAim(Transform model, Vector3 mount, Quaternion turn, float scale, Vector3 world) =>
            mount + turn * (model.InverseTransformPoint(world) * scale);

        private static bool BuildGrapple(Transform model, Vector3 mount, Quaternion turn)
        {
            grappleOf = model;
            GrappleArms.Clear();
            var scale = model.lossyScale.x;
            var points = new List<Vector3>();
            var corners = new List<Vector3>();
            grappleField = null;
            var readable = true;
            var parts = new List<string>();
            foreach (var renderer in model.GetComponentsInChildren<MeshRenderer>(true))
            {
                var name = renderer.name;
                if (!name.StartsWith(GrapplePart)) continue;
                var filter = renderer.GetComponent<MeshFilter>();
                if (filter == null || filter.sharedMesh == null) continue;
                var mesh = filter.sharedMesh;
                var part = renderer.transform;
                parts.Add($"{name.Substring(GrapplePart.Length)} {Vector3.Scale(mesh.bounds.size, part.lossyScale) * 100f:F0} cm at {ToAim(model, mount, turn, scale, part.TransformPoint(mesh.bounds.center)) * 100f:F0}");
                if (name.Contains(ArmPart)) { GrappleArms.Add(part); continue; }
                if (!HousingParts.Any(h => name.EndsWith(h))) continue;
                if (mesh.isReadable)
                {
                    var start = points.Count;
                    foreach (var vertex in mesh.vertices) points.Add(ToAim(model, mount, turn, scale, part.TransformPoint(vertex)));
                    foreach (var index in mesh.triangles) corners.Add(points[start + index]);
                }
                else
                {
                    readable = false;
                    var b = mesh.bounds;
                    for (var i = 0; i < 8; i++)
                        points.Add(ToAim(model, mount, turn, scale, part.TransformPoint(b.center + Vector3.Scale(b.extents, new Vector3((i & 1) * 2 - 1, (i & 2) - 1, (i & 4) / 2 - 1)))));
                }
            }
            if (points.Count == 0)
            {
                Plugin.Log.LogInfo("Tool collision: grapple housing not found, the ball along it instead");
                return false;
            }
            var bounds = new Bounds(points[0], Vector3.zero);
            foreach (var p in points) bounds.Encapsulate(p);
            var full = bounds;
            // The ring under the housing: the bottom rows narrower than RingNarrow of the widest row.
            if (readable)
            {
                var rows = new float[RingRows];
                var row = bounds.size.y / RingRows;
                var rowMin = new float[RingRows];
                var rowMax = new float[RingRows];
                for (var i = 0; i < RingRows; i++) { rowMin[i] = float.MaxValue; rowMax[i] = float.MinValue; }
                foreach (var p in points)
                {
                    var i = Mathf.Clamp((int)((p.y - bounds.min.y) / Mathf.Max(row, 1e-6f)), 0, RingRows - 1);
                    rowMin[i] = Mathf.Min(rowMin[i], p.x);
                    rowMax[i] = Mathf.Max(rowMax[i], p.x);
                }
                var widest = 0f;
                for (var i = 0; i < RingRows; i++)
                {
                    rows[i] = rowMax[i] > rowMin[i] ? rowMax[i] - rowMin[i] : 0f;
                    widest = Mathf.Max(widest, rows[i]);
                }
                var bottom = 0;
                while (bottom < RingRows / 2 && rows[bottom] < widest * RingNarrow) bottom++;
                if (bottom > 0)
                {
                    var min = bounds.min;
                    min.y += bottom * row;
                    bounds.SetMinMax(min, bounds.max);
                }
            }
            grappleHousing = bounds;
            var fieldNote = "";
            if (readable && corners.Count >= 3)
            {
                var watch = System.Diagnostics.Stopwatch.StartNew();
                var field = new VrShapeField(corners, FieldCell * VrCamera.WorldScale, FieldMargin * VrCamera.WorldScale, bounds.min.y);
                if (field.SolidCells > 0)
                {
                    grappleField = field;
                    fieldNote = $"; its own shape {field.SolidBounds.size * 100f:F0} cm, {field.SolidCells} cells of {FieldCell * 100f:0.#} cm, built in {watch.ElapsedMilliseconds} ms";
                }
            }
            if (logs++ < 30) Plugin.Log.LogInfo($"Tool collision: grapple housing {bounds.size * 100f:F0} cm at {bounds.center * 100f:F0} cm from the grip "
                + $"({(readable ? $"mesh read, {full.size.y * 100f - bounds.size.y * 100f:0} cm of ring left out below" : "mesh not readable, the whole height kept")}{fieldNote}), "
                + $"{GrappleArms.Count} arm(s); parts: {string.Join("; ", parts)}");
            return true;
        }

        // The deepest place where the other arm goes into the grapple's housing or one of its arms.
        private static bool InGrapple(Vector3 pivot, Quaternion rotation, Bounds housing, Capsule[] arms, out Vector3 armAt, out int piece,
            out float along, out Vector3 way, out float depth)
        {
            armAt = way = Vector3.zero;
            piece = 0;
            along = depth = 0f;
            var back = Quaternion.Inverse(rotation);
            var found = false;
            for (var j = 0; j < armPieces; j++)
            {
                var a = back * (ArmJoints[j] - pivot);
                var b = back * (ArmJoints[j + 1] - pivot);
                var radius = PieceRadius(j);
                // The housing: the arm's line, sampled, against the box.
                for (var s = 0; s <= ArmSamples; s++)
                {
                    var t = s / (float)ArmSamples;
                    var p = Vector3.Lerp(a, b, t);
                    if (grappleField != null)
                    {
                        var distance = grappleField.Distance(p);
                        if (distance == float.MaxValue || radius - distance <= depth) continue;
                        var outward = grappleField.Out(p);
                        if (outward == Vector3.zero) continue;
                        found = true;
                        depth = radius - distance;
                        // The field's way points off the housing; the tool goes the other way.
                        way = rotation * -outward;
                        armAt = pivot + rotation * p;
                        piece = j;
                        along = t;
                        continue;
                    }
                    var q = Vector3.Max(housing.min, Vector3.Min(housing.max, p));
                    var gap = p - q;
                    float deep;
                    Vector3 out_;
                    if (gap.sqrMagnitude > 1e-10f)
                    {
                        deep = radius - gap.magnitude;
                        out_ = -gap.normalized;
                    }
                    else
                    {
                        // Inside: out through the nearest side, the tool going the other way.
                        var toMax = housing.max - p;
                        var toMin = p - housing.min;
                        var least = toMax.x; out_ = Vector3.left;
                        if (toMin.x < least) { least = toMin.x; out_ = Vector3.right; }
                        if (toMax.y < least) { least = toMax.y; out_ = Vector3.down; }
                        if (toMin.y < least) { least = toMin.y; out_ = Vector3.up; }
                        if (toMax.z < least) { least = toMax.z; out_ = Vector3.back; }
                        if (toMin.z < least) { least = toMin.z; out_ = Vector3.forward; }
                        deep = radius + least;
                    }
                    if (deep <= depth) continue;
                    found = true;
                    depth = deep;
                    way = rotation * out_;
                    armAt = pivot + rotation * p;
                    piece = j;
                    along = t;
                }
                // The arms: capsule against capsule.
                for (var k = 0; k < armCapsules; k++)
                {
                    VrUtil.ClosestOnSegments(arms[k].A, arms[k].B, a, b, out var onTool, out var onArm);
                    var gap = onTool - onArm;
                    var deep = radius + arms[k].Radius - gap.magnitude;
                    if (deep <= depth) continue;
                    found = true;
                    depth = deep;
                    way = rotation * (gap.sqrMagnitude > 1e-10f ? gap.normalized : Vector3.up);
                    armAt = pivot + rotation * onArm;
                    piece = j;
                    OnSegment(a, b, onArm, out along);
                }
            }
            return found;
        }

        private static void ReportTurn(State state, bool turned, VrInput.Hand hand, EquipmentController.Equipment tool, float angle)
        {
            if (turned != state.ArmTurned && logs++ < 40)
                Plugin.Log.LogInfo($"Tool collision: {hand.Name} {tool} {(turned ? $"turned {angle:0}° aside by the other arm" : "free of the other arm")}");
            state.ArmTurned = turned;
        }

        // How far the tool and the hand go back with the tool's own turn this frame, in tracking space; zero when free or
        // without extended tool physics. Once a frame, after Apply (VrToolWeight.Grip runs it first).
        public static Vector3 Back(VrInput.Hand hand)
        {
            var state = hand.IsLeft ? Left : Right;
            return state.Running && state.Frame == Time.frameCount ? state.Back : Vector3.zero;
        }

        // A tool still in something once turned as far as it may: thrust in along itself. Moved back along itself, with the
        // hand, to where it is clear, so it stops at the surface instead of sinking in.
        private static void HoldBack(State state, Transform rig, Vector3 pivot, Quaternion rotation, Box[] boxes, float length,
            VrInput.Hand hand, EquipmentController.Equipment tool)
        {
            if (Free(pivot, rotation, boxes, length, out var contact))
            {
                Report(state, false, hand, tool, 0f);
                ReportArm(state, false, hand, tool, 0f);
                return;
            }
            var scale = VrCamera.WorldScale;
            // In the other arm, which turning could not get it out of (the hand moved it in sideways, or the arm came to
            // it): tool and hand are pushed out of the arm, the way its surface faces, as the arm comes first (Raik).
            if (contact.Thing == armProbe)
            {
                var push = Vector3.zero;
                for (var i = 0; i < ArmTries && contact.Thing == armProbe; i++)
                {
                    push += contact.Out * (contact.Depth + ArmGap * scale);
                    if (push.magnitude > MostArmPush * scale) break;
                    ContactAt = contact.At;
                    ContactWay = contact.Out;
                    ContactFrame = Time.frameCount;
                    if (Free(pivot + push, rotation, boxes, length, out contact))
                    {
                        state.Back = rig.InverseTransformVector(push);
                        ReportArm(state, true, hand, tool, push.magnitude);
                        Report(state, false, hand, tool, 0f);
                        return;
                    }
                }
            }
            ReportArm(state, false, hand, tool, 0f);
            var back = rotation * Vector3.back;
            var clear = -1f;
            for (var d = BackStep; d <= MostBack + 1e-4f; d += BackStep)
                if (Free(pivot + back * d * scale, rotation, boxes, length, out _)) { clear = d; break; }
            if (clear < 0f)
            {
                // Too deep: let it through rather than drag the hand far off the controller.
                Report(state, false, hand, tool, 0f);
                return;
            }
            for (var d = clear - BackStep + BackFine; d < clear - 1e-4f; d += BackFine)
                if (Free(pivot + back * d * scale, rotation, boxes, length, out _)) { clear = d; break; }
            state.Back = rig.InverseTransformVector(back * clear * scale);
            Report(state, true, hand, tool, clear);
        }

        private static void ReportArm(State state, bool held, VrInput.Hand hand, EquipmentController.Equipment tool, float distance)
        {
            if (held != state.ArmPushed && logs++ < 30)
                Plugin.Log.LogInfo($"Tool collision: {hand.Name} {tool} {(held ? $"pushed {distance * 100f:0} cm out of the other arm" : "clear of the other arm")}");
            state.ArmPushed = held;
        }

        private static void Report(State state, bool held, VrInput.Hand hand, EquipmentController.Equipment tool, float distance)
        {
            if (held != state.HeldBack && logs++ < 30)
                Plugin.Log.LogInfo($"Tool collision: {hand.Name} {tool} {(held ? $"held back {distance * 100f:0} cm at what it was thrust into" : "no longer held back")}");
            state.HeldBack = held;
        }

        // How far a point in the world lies from the shape of the tool in that hand, as it is held this frame (the boxes of
        // its model, or the ball along it), in meters; MaxValue without a tool. For a second hand taking hold of the tool
        // (VrToolSupport) and its fingers closing on it.
        public static float Distance(VrInput.Hand hand, EquipmentController.Equipment tool, Vector3 point)
        {
            var rig = VrCamera.Instance != null ? VrCamera.Instance.transform.parent : null;
            if (rig == null || (tool != EquipmentController.Equipment.CuttingTool && tool != EquipmentController.Equipment.GrappleHook)
                || !hand.TryGetPosition(out var gripLocal) || !hand.TryGetAimRotation(out var aimLocal)) return float.MaxValue;
            var turned = VrToolSteady.Apply(hand, tool, aimLocal);
            var pivot = rig.TransformPoint(VrToolWeight.Grip(hand, tool, gripLocal));
            var rotation = rig.rotation * turned;
            var boxes = hand == VrHand.GameToolHand.Input ? Shape(tool) : null;
            if (boxes != null)
            {
                var nearest = float.MaxValue;
                foreach (var box in boxes)
                {
                    var local = Quaternion.Inverse(rotation * box.Turn) * (point - (pivot + rotation * box.Center));
                    var outside = new Vector3(Mathf.Max(Mathf.Abs(local.x) - box.Half.x, 0f), Mathf.Max(Mathf.Abs(local.y) - box.Half.y, 0f),
                        Mathf.Max(Mathf.Abs(local.z) - box.Half.z, 0f));
                    nearest = Mathf.Min(nearest, outside.magnitude);
                }
                return nearest;
            }
            var scale = VrCamera.WorldScale;
            var forward = rotation * Vector3.forward;
            var from = pivot + forward * FromGrip * scale;
            var to = pivot + forward * ToolLength(pivot, rotation);
            var along = Vector3.Project(point - from, to - from);
            var share = Vector3.Dot(along, to - from) / Mathf.Max((to - from).sqrMagnitude, 1e-8f);
            var closest = from + (to - from) * Mathf.Clamp01(share);
            return Mathf.Max(Vector3.Distance(point, closest) - Radius * scale, 0f);
        }

        // Test (Raik, 2026-11-14): the tool in the game tool hand as the other hand feels it (VrHandShape), as held this
        // frame. The grapple by its own shape (grappleField, cells of 1.5 cm, and its four arms as capsules): the boxes
        // round its parts were far larger than it, the housing's 22 x 49 x 59 cm with the empty ring, and pushed a hand
        // 5 to 9 cm off a tool it did not touch. Other tools by the boxes of their parts.
        private static int worldFrame = -1;
        private static bool worldKnown, worldField;
        private static Vector3 worldPivot;
        private static Quaternion worldTurn;
        private static Box[] worldBoxes;
        private static float worldReach;

        private static bool WorldTool()
        {
            if (worldFrame == Time.frameCount) return worldKnown;
            worldFrame = Time.frameCount;
            worldKnown = false;
            if (!BodyMeetsTool) return false;
            var hand = VrHand.GameToolHand;
            var tool = VrToolModels.Equipped;
            var rig = VrCamera.Instance != null ? VrCamera.Instance.transform.parent : null;
            if (rig == null || hand == null || !hand.HoldsTool || (tool != EquipmentController.Equipment.CuttingTool && tool != EquipmentController.Equipment.GrappleHook)
                || !hand.Input.TryGetPosition(out var gripLocal) || !hand.Input.TryGetAimRotation(out var aimLocal)) return false;
            worldBoxes = Shape(tool);
            if (worldBoxes == null) return false;
            worldPivot = rig.TransformPoint(VrToolWeight.Grip(hand.Input, tool, gripLocal));
            worldTurn = rig.rotation * VrToolSteady.Apply(hand.Input, tool, aimLocal);
            worldField = tool == EquipmentController.Equipment.GrappleHook && GrappleShape(out _, out _) && grappleField != null;
            worldReach = 0f;
            foreach (var box in worldBoxes) worldReach = Mathf.Max(worldReach, box.Center.magnitude + box.Half.magnitude);
            worldKnown = true;
            return true;
        }

        // The tool's turn as held this frame (world), for a way kept in its own axes (VrHands.StayOutOfTool).
        internal static bool ToolTurn(out Quaternion turn)
        {
            turn = Quaternion.identity;
            if (!WorldTool()) return false;
            turn = worldTurn;
            return true;
        }

        // Whether the tool lies within reach of that point.
        internal static bool ToolNear(Vector3 center, float reach) => WorldTool() && (center - worldPivot).magnitude < reach + worldReach;

        // How far a point lies outside the tool (less than 0 inside it, MaxValue far off), and the way out of it.
        internal static float WorldDistance(Vector3 point, out Vector3 way)
        {
            way = Vector3.zero;
            if (!WorldTool()) return float.MaxValue;
            var local = Quaternion.Inverse(worldTurn) * (point - worldPivot);
            var nearest = float.MaxValue;
            if (worldField)
            {
                var distance = grappleField.Distance(local);
                if (distance != float.MaxValue)
                {
                    var outward = grappleField.Out(local);
                    if (outward != Vector3.zero)
                    {
                        nearest = distance;
                        way = worldTurn * outward;
                    }
                }
                for (var k = 0; k < armCapsules; k++)
                {
                    var on = OnSegment(ArmCapsules[k].A, ArmCapsules[k].B, local, out _);
                    var gap = local - on;
                    var off = gap.magnitude - ArmCapsules[k].Radius;
                    if (off >= nearest) continue;
                    nearest = off;
                    way = worldTurn * (gap.sqrMagnitude > 1e-10f ? gap.normalized : Vector3.up);
                }
                return nearest;
            }
            foreach (var box in worldBoxes)
            {
                var inBox = Quaternion.Inverse(box.Turn) * (local - box.Center);
                var half = box.Half;
                var q = new Vector3(Mathf.Abs(inBox.x) - half.x, Mathf.Abs(inBox.y) - half.y, Mathf.Abs(inBox.z) - half.z);
                var outside = new Vector3(Mathf.Max(q.x, 0f), Mathf.Max(q.y, 0f), Mathf.Max(q.z, 0f));
                float distance;
                Vector3 boxWay;
                if (outside.sqrMagnitude > 0f)
                {
                    distance = outside.magnitude;
                    boxWay = new Vector3(outside.x * Mathf.Sign(inBox.x), outside.y * Mathf.Sign(inBox.y), outside.z * Mathf.Sign(inBox.z)) / distance;
                }
                else
                {
                    // Inside: out through the nearest face.
                    distance = Mathf.Max(q.x, Mathf.Max(q.y, q.z));
                    boxWay = q.x >= q.y && q.x >= q.z ? new Vector3(Mathf.Sign(inBox.x), 0f, 0f)
                        : q.y >= q.z ? new Vector3(0f, Mathf.Sign(inBox.y), 0f) : new Vector3(0f, 0f, Mathf.Sign(inBox.z));
                }
                if (distance >= nearest) continue;
                nearest = distance;
                way = worldTurn * (box.Turn * boxWay);
            }
            return nearest;
        }

        // A box around each part of the game tool's model, in the aim's axes from the grip; null without a model.
        private static Box[] Shape(EquipmentController.Equipment tool)
        {
            if (!VrToolModels.TryGetFrame(out var model, out var mount, out var turn)) return null;
            // Not while the tool still glides into the hand: the boxes would lie where it was (the ball meanwhile).
            if (!Settled(model, out _)) return null;
            if (model == shapeOf && tool == shapeTool && Time.unscaledTime - shapeAt < ShapeAgain) return shape;
            shapeAt = Time.unscaledTime;
            var scale = model.lossyScale.x;
            var boxes = new List<Box>();
            foreach (var renderer in model.GetComponentsInChildren<MeshRenderer>())
            {
                var filter = renderer.GetComponent<MeshFilter>();
                if (!renderer.enabled || filter == null || filter.sharedMesh == null) continue;
                var bounds = filter.sharedMesh.bounds;
                var part = renderer.transform;
                var center = mount + turn * (model.InverseTransformPoint(part.TransformPoint(bounds.center)) * scale);
                var partTurn = turn * (Quaternion.Inverse(model.rotation) * part.rotation);
                var half = Vector3.Scale(bounds.extents, part.lossyScale);
                if (Mathf.Max(half.x, half.y, half.z) < Smallest * VrCamera.WorldScale) continue;
                var box = new Box { Center = center, Half = half * Shrink, Turn = partTurn };
                if (VrLooseTools.Exact)
                {
                    box.Full = new Vector3(Mathf.Abs(half.x), Mathf.Abs(half.y), Mathf.Abs(half.z));
                    box.Origin = mount + turn * (model.InverseTransformPoint(part.position) * scale);
                    box.PartTurn = partTurn;
                    box.Part = VrToolShape.For(filter.sharedMesh, part.lossyScale);
                }
                boxes.Add(box);
            }
            shapeOf = model;
            shapeTool = tool;
            shape = boxes.OrderByDescending(b => b.Half.x * b.Half.y * b.Half.z).Take(MostBoxes).ToArray();
            if (shape.Length == 0) shape = null;
            if (shapeLogged.Add(tool)) Plugin.Log.LogInfo($"Tool collision: {tool} shaped by {(shape != null ? shape.Length : 0)} box(es) of its model" + (shape != null
                ? ": " + string.Join(", ", shape.Select(b => $"{b.Half.x * 2f:0.00}x{b.Half.y * 2f:0.00}x{b.Half.z * 2f:0.00} at {b.Center.z:0.00}")) : ", the ball instead"));
            return shape;
        }

        // From from towards to by share of the way, in tested steps; the last step that was free.
        private static Quaternion Towards(Vector3 pivot, Quaternion from, Quaternion to, float share, Box[] boxes, float length)
        {
            var angle = Quaternion.Angle(from, to);
            if (angle < 0.01f) return to;
            var go = angle * share;
            var steps = Mathf.Max(1, Mathf.CeilToInt(go / Step));
            var last = from;
            for (var i = 1; i <= steps; i++)
            {
                var next = Quaternion.Slerp(from, to, go * i / steps / angle);
                if (!Free(pivot, next, boxes, length, out _, false)) return last;
                last = next;
            }
            return last;
        }

        // From the grip to the muzzle along the tool.
        private static float ToolLength(Vector3 pivot, Quaternion rotation)
        {
            if (!VrToolModels.Muzzle(pivot, rotation, out var muzzle, out _)) return Fallback * VrCamera.WorldScale;
            var along = Vector3.Dot(muzzle - pivot, rotation * Vector3.forward);
            return along > FromGrip * VrCamera.WorldScale ? along : Fallback * VrCamera.WorldScale;
        }

        // Whether the tool turned so is clear of everything but the player's own body (and the tool, which hangs under
        // it); what it went into if not.
        private static bool Free(Vector3 pivot, Quaternion rotation, Box[] boxes, float length, out Contact contact, bool arm = true)
        {
            contact = default;
            var player = Player();
            if (boxes != null)
            {
                foreach (var box in boxes)
                {
                    var center = pivot + rotation * box.Center;
                    var count = Physics.OverlapBoxNonAlloc(center, box.Part != null ? box.Full : box.Half, Overlaps, rotation * box.Turn, Physics.DefaultRaycastLayers, QueryTriggerInteraction.Ignore);
                    for (var i = 0; i < count; i++)
                    {
                        // A tool afloat (VrLooseTools) is no obstacle to the one in the hand: it is taken, not pushed.
                        if (Own(Overlaps[i], player) || VrLooseTools.Owns(Overlaps[i])) continue;
                        if (box.Part != null)
                        {
                            // Near it: in it only if one of the part's pieces is.
                            if (!VrToolShape.Touches(box.Part, pivot + rotation * box.Origin, rotation * box.PartTurn, Overlaps[i], Graze * VrCamera.WorldScale,
                                out var pieceWay, out _, out var pieceAt)) continue;
                            contact = new Contact { Thing = Overlaps[i], At = pieceAt, Separated = true, Out = pieceWay };
                            return false;
                        }
                        contact = new Contact { Thing = Overlaps[i], At = center };
                        var turn = rotation * box.Turn;
                        if (Separate(Overlaps[i], center, box.Half, turn, out var way))
                        {
                            contact.Separated = true;
                            contact.Out = way;
                        }
                        return false;
                    }
                }
                if (arm && ArmContact(pivot, rotation, boxes, length, out var boxArmAt, out _, out _, out var boxArmWay, out var boxArmDepth))
                {
                    contact = new Contact { Thing = armProbe, At = boxArmAt, Separated = true, Out = boxArmWay, Depth = boxArmDepth };
                    return false;
                }
                return true;
            }
            var scale = VrCamera.WorldScale;
            var forward = rotation * Vector3.forward;
            var start = pivot + forward * FromGrip * scale;
            var distance = length - FromGrip * scale;
            if (distance <= 0f) return true;
            var hits = Physics.SphereCastNonAlloc(start, Radius * scale, forward, Hits, distance, Physics.DefaultRaycastLayers, QueryTriggerInteraction.Ignore);
            var nearest = float.MaxValue;
            for (var i = 0; i < hits; i++)
            {
                var hit = Hits[i];
                // What the ball starts in counts too (distance 0): the tool lies in it.
                if (Own(hit.collider, player) || VrLooseTools.Owns(hit.collider) || hit.distance >= nearest) continue;
                nearest = hit.distance;
                contact = new Contact { Thing = hit.collider, At = start + forward * Mathf.Max(hit.distance, distance * 0.5f) };
            }
            if (arm && nearest == float.MaxValue && LineInArm(start, start + forward * distance, Radius * scale, out var armAt, out var armWay, out var lineDepth))
            {
                contact = new Contact { Thing = armProbe, At = armAt, Separated = true, Out = armWay, Depth = lineDepth };
                return false;
            }
            return nearest == float.MaxValue;
        }

        // The player's body, which the tools never bump into.
        public static Rigidbody Player() => GameRefs.Grab != null ? PlayerBody(GameRefs.Grab) : null;

        public static bool Own(Collider collider, Rigidbody player) =>
            player != null && (collider.attachedRigidbody == player || collider.transform.IsChildOf(player.transform));

        // Out of what the tool went into, from its point that went in (a unit direction; zero without a contact).
        private static Vector3 Away(Contact contact)
        {
            if (contact.Thing == null) return Vector3.zero;
            if (contact.Separated) return contact.Out;
            var away = contact.At - VrUtil.ClosestPoint(contact.Thing, contact.At);
            // Inside a non-convex collider ClosestPoint gives the point itself: away from the thing's middle instead.
            if (away.sqrMagnitude < 1e-8f) away = contact.At - contact.Thing.bounds.center;
            return away.sqrMagnitude < 1e-10f ? Vector3.zero : away.normalized;
        }

        // The tool turned about the grip so that the point of it that went into something moves away from it, step by
        // step until it is clear.
        private static Quaternion Out(Vector3 pivot, Quaternion rotation, Box[] boxes, float length, Vector3 at, Vector3 away, float most)
        {
            var axis = Vector3.Cross(at - pivot, away);
            if (axis.sqrMagnitude < 1e-10f || most <= 0f) return rotation;
            // At most most this frame: the tool slides out along what it touches instead of jumping out of it.
            var turned = rotation;
            for (var angle = 0f; angle < most - 1e-4f;)
            {
                var step = Mathf.Min(OutStep, most - angle);
                angle += step;
                turned = Quaternion.AngleAxis(step, axis.normalized) * turned;
                if (Free(pivot, turned, boxes, length, out _, false)) return turned;
            }
            return turned;
        }
    }
}
