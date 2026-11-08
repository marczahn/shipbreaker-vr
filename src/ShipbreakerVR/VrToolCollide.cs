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
        private const float MostArmPush = 0.25f;

        private static readonly AccessTools.FieldRef<GrabController, Rigidbody> PlayerBody =
            AccessTools.FieldRefAccess<GrabController, Rigidbody>("m_PlayerRigidbodyReference");
        private static readonly RaycastHit[] Hits = new RaycastHit[16];
        private static readonly Collider[] Overlaps = new Collider[16];

        private struct Box
        {
            // In the aim's axes, from the grip, turned as its part is: the part's own bounds, not a box around them
            // along the aim (which made the grapple's housing 60 cm tall).
            public Vector3 Center;
            public Vector3 Half;
            public Quaternion Turn;
        }

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
        private const float ArmRadius = 0.05f;
        private const float HandRadius = 0.045f;
        private const float Fingers = 0.08f;
        private static CapsuleCollider armProbe;
        private static bool armKnown;
        private static int armPieces;
        private static readonly Vector3[] ArmJoints = new Vector3[4];

        private static void OtherArm(VrInput.Hand hand)
        {
            armKnown = VrHands.TryGetArm(!hand.IsLeft, out ArmJoints[0], out ArmJoints[1], out ArmJoints[2], out var knuckles);
            var other = hand.IsLeft ? VrInput.Right : VrInput.Left;
            var along = knuckles - ArmJoints[2];
            armPieces = 2;
            if (armKnown && along.sqrMagnitude > 1e-6f && !VrToolSupport.Supports(other))
            {
                ArmJoints[3] = knuckles + along.normalized * (Fingers * VrCamera.WorldScale);
                armPieces = 3;
            }
        }

        private static float PieceRadius(int j) => (j == 2 ? HandRadius : ArmRadius) * VrCamera.WorldScale;

        // Whether a box of the tool lies in the other arm, and its way out of it.
        private static bool InArm(Vector3 center, Vector3 half, Quaternion turn, out Vector3 way, out float depth)
        {
            way = Vector3.zero;
            depth = 0f;
            if (!armKnown) return false;
            Probes();
            probe.size = half * 2f;
            probe.center = Vector3.zero;
            for (var j = 0; j < armPieces; j++)
            {
                var line = ArmJoints[j + 1] - ArmJoints[j];
                if (line.sqrMagnitude < 1e-6f) continue;
                var radius = PieceRadius(j);
                armProbe.radius = radius;
                armProbe.height = line.magnitude + 2f * radius;
                armProbe.center = Vector3.zero;
                if (Penetration(probe, center, turn, armProbe, ArmJoints[j] + line * 0.5f, Quaternion.FromToRotation(Vector3.up, line),
                    out var direction, out var distance) && direction.sqrMagnitude > 1e-8f)
                {
                    way = direction.normalized;
                    depth = distance;
                    return true;
                }
            }
            return false;
        }

        // Whether the tool's line (the ball along it, without a model) comes within reach of the other arm.
        private static bool LineInArm(Vector3 start, Vector3 end, float reach, out Vector3 at, out Vector3 way, out float depth)
        {
            at = way = Vector3.zero;
            depth = 0f;
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
            state.Running = true;
            var boxes = hand == VrHand.GameToolHand.Input ? Shape(tool) : null;
            var length = Length(pivot, desired);

            // The tool never jumps: it turns from where it was towards the controller in steps of Step, each one tested, and
            // stops before the first that goes into something, so it cannot pass through a thin wall between two frames.
            // Held off, it comes back over CatchUp, not at once, also once the way is free.
            var share = state.Blocked ? 1f - Mathf.Exp(-Time.unscaledDeltaTime / CatchUp) : 1f;
            // The hand's movement may have put the tool into something: turned out of it, a little each frame.
            if (!Free(pivot, previous, boxes, length, out var contact))
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
            HoldBack(state, rig, pivot, result, boxes, length, hand, tool);
            var blocked = Quaternion.Angle(result, desired) > 1f;
            if (blocked != state.Blocked && logs++ < 30)
                Plugin.Log.LogInfo($"Tool collision: {hand.Name} {tool} {(blocked ? $"held off, {Quaternion.Angle(result, desired):0}° from the controller" : "with the controller again")}");
            state.Blocked = blocked;
            state.Rotation = Quaternion.Inverse(rig.rotation) * result;
            state.Result = state.Rotation;
            return state.Result;
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

        // A box around each part of the game tool's model, in the aim's axes from the grip; null without a model.
        private static Box[] Shape(EquipmentController.Equipment tool)
        {
            if (!VrToolModels.TryGetFrame(out var model, out var mount, out var turn)) return null;
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
                boxes.Add(new Box { Center = center, Half = half * Shrink, Turn = partTurn });
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
                if (!Free(pivot, next, boxes, length, out _)) return last;
                last = next;
            }
            return last;
        }

        // From the grip to the muzzle along the tool.
        private static float Length(Vector3 pivot, Quaternion rotation)
        {
            if (!VrToolModels.Muzzle(pivot, rotation, out var muzzle, out _)) return Fallback * VrCamera.WorldScale;
            var along = Vector3.Dot(muzzle - pivot, rotation * Vector3.forward);
            return along > FromGrip * VrCamera.WorldScale ? along : Fallback * VrCamera.WorldScale;
        }

        // Whether the tool turned so is clear of everything but the player's own body (and the tool, which hangs under
        // it); what it went into if not.
        private static bool Free(Vector3 pivot, Quaternion rotation, Box[] boxes, float length, out Contact contact)
        {
            contact = default;
            var player = Player();
            if (boxes != null)
            {
                foreach (var box in boxes)
                {
                    var center = pivot + rotation * box.Center;
                    var count = Physics.OverlapBoxNonAlloc(center, box.Half, Overlaps, rotation * box.Turn, Physics.DefaultRaycastLayers, QueryTriggerInteraction.Ignore);
                    for (var i = 0; i < count; i++)
                    {
                        if (Own(Overlaps[i], player)) continue;
                        contact = new Contact { Thing = Overlaps[i], At = center };
                        var turn = rotation * box.Turn;
                        if (Separate(Overlaps[i], center, box.Half, turn, out var way))
                        {
                            contact.Separated = true;
                            contact.Out = way;
                        }
                        return false;
                    }
                    if (InArm(center, box.Half, rotation * box.Turn, out var outOfArm, out var armDepth))
                    {
                        contact = new Contact { Thing = armProbe, At = center, Separated = true, Out = outOfArm, Depth = armDepth };
                        return false;
                    }
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
                if (Own(hit.collider, player) || hit.distance >= nearest) continue;
                nearest = hit.distance;
                contact = new Contact { Thing = hit.collider, At = start + forward * Mathf.Max(hit.distance, distance * 0.5f) };
            }
            if (nearest == float.MaxValue && LineInArm(start, start + forward * distance, Radius * scale, out var armAt, out var armWay, out var lineDepth))
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
                if (Free(pivot, turned, boxes, length, out _)) return turned;
            }
            return turned;
        }
    }
}
