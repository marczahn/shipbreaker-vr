using System.Collections.Generic;
using System.Linq;
using BBI.Unity.Game;
using HarmonyLib;
using UnityEngine;

namespace ShipbreakerVR
{
    // Steuerung/SchwereTeileSchieben (extended physics): cutter and grapple bump into things, as Raik asked. The tool stays
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
        // The steps a tool turns in, tested each, in degrees.
        private const float Step = 2f;
        // How far a tool is turned out of what it went into, each step and at most a frame, in degrees.
        private const float OutStep = 1f;
        private const float OutPerFrame = 3f;
        // A turn this far off the controller is given up: the tool goes back to the controller, through the obstacle.
        private const float MostOff = 120f;
        // A tool that was held off comes back to the controller over about this time, in seconds, not in one frame.
        private const float CatchUp = 0.12f;

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
        }

        private static readonly State Left = new State();
        private static readonly State Right = new State();
        private static int logs;

        // What a test found in the tool's way: the thing, and the tool's point that went into it.
        private struct Contact
        {
            public Collider Thing;
            public Vector3 At;
        }

        // The tool's turn in tracking space for the turn the controller (filtered) wants, wanted.
        public static Quaternion Apply(VrInput.Hand hand, EquipmentController.Equipment tool, Quaternion wanted)
        {
            var state = hand.IsLeft ? Left : Right;
            var rig = VrCamera.Instance != null ? VrCamera.Instance.transform.parent : null;
            if (VrSettings.BracedPush == null || !VrSettings.BracedPush.Value || rig == null || !hand.TryGetPosition(out var gripLocal)
                || (tool != EquipmentController.Equipment.CuttingTool && tool != EquipmentController.Equipment.GrappleHook))
            {
                state.Running = false;
                return wanted;
            }
            if (state.Running && state.Frame == Time.frameCount) return state.Result;
            state.Frame = Time.frameCount;
            var pivot = rig.TransformPoint(gripLocal);
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
            if (!Free(pivot, previous, boxes, length, out var contact)) previous = Out(pivot, previous, boxes, length, contact);
            var result = Towards(pivot, previous, desired, share, boxes, length);
            // Held off this far, it gives up the obstacle and comes back to the controller through it, still over CatchUp.
            if (Quaternion.Angle(result, desired) > MostOff) result = Quaternion.Slerp(result, desired, 1f - Mathf.Exp(-Time.unscaledDeltaTime / CatchUp));
            var blocked = Quaternion.Angle(result, desired) > 1f;
            if (blocked != state.Blocked && logs++ < 30)
                Plugin.Log.LogInfo($"Tool collision: {hand.Name} {tool} {(blocked ? $"held off, {Quaternion.Angle(result, desired):0}° from the controller" : "with the controller again")}");
            state.Blocked = blocked;
            state.Rotation = Quaternion.Inverse(rig.rotation) * result;
            state.Result = state.Rotation;
            return state.Result;
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

        // From a little ahead of the grip to the muzzle along the tool.
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
            var player = GameRefs.Grab != null ? PlayerBody(GameRefs.Grab) : null;
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
            return nearest == float.MaxValue;
        }

        private static bool Own(Collider collider, Rigidbody player) =>
            player != null && (collider.attachedRigidbody == player || collider.transform.IsChildOf(player.transform));

        // The tool turned about the grip so that the point of it that went into something moves away from it, step by
        // step until it is clear.
        private static Quaternion Out(Vector3 pivot, Quaternion rotation, Box[] boxes, float length, Contact contact)
        {
            if (contact.Thing == null) return rotation;
            var away = contact.At - contact.Thing.ClosestPoint(contact.At);
            // Inside a non-convex collider ClosestPoint gives the point itself: away from the thing's middle instead.
            if (away.sqrMagnitude < 1e-8f) away = contact.At - contact.Thing.bounds.center;
            var axis = Vector3.Cross(contact.At - pivot, away);
            if (axis.sqrMagnitude < 1e-10f) return rotation;
            // At most OutPerFrame a frame: the tool slides out along what it touches instead of jumping out of it.
            var step = Quaternion.AngleAxis(OutStep, axis.normalized);
            var turned = rotation;
            for (var angle = OutStep; angle <= OutPerFrame; angle += OutStep)
            {
                turned = step * turned;
                if (Free(pivot, turned, boxes, length, out _)) return turned;
            }
            return turned;
        }
    }
}
