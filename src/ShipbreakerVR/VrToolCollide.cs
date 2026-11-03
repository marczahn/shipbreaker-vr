using BBI.Unity.Game;
using HarmonyLib;
using UnityEngine;

namespace ShipbreakerVR
{
    // Steuerung/SchwereTeileSchieben (extended physics): cutter and grapple bump into things, as Raik asked. The tool stays
    // in the hand and turns about the grip instead of going through what is in its way: swung against a bar it stops
    // there and is turned aside as the hand goes on, and pulled past it, it follows the controller again. A thin ball is
    // swept along the tool from the hand to its muzzle (VrToolModels.Muzzle). A turn that would sweep it into something
    // goes only as far as it is free; a tool that the moving hand has pushed into something is turned out of it, away from
    // the surface it touches. The tool's turn is the aim's (VrToolSteady), so the laser shows where the tool points.
    internal static class VrToolCollide
    {
        // The tool as a ball of this radius, from this far ahead of the grip to its muzzle, in meters at world scale 1.
        private const float Radius = 0.04f;
        private const float FromGrip = 0.1f;
        private const float Fallback = 0.4f;
        // How far a tool is turned out of what it went into, each step and at most, in degrees.
        private const float OutStep = 4f;
        private const float MostOut = 80f;
        // A turn this far off the controller is given up: the tool goes back to the controller.
        private const float MostOff = 120f;
        // A tool that was held off comes back to the controller over about this time, in seconds, not in one frame.
        private const float CatchUp = 0.12f;

        private static readonly AccessTools.FieldRef<GrabController, Rigidbody> PlayerBody =
            AccessTools.FieldRefAccess<GrabController, Rigidbody>("m_PlayerRigidbodyReference");
        private static readonly RaycastHit[] Hits = new RaycastHit[16];

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
            var length = Length(pivot, desired);

            // Free of the obstacle it was held off by, the tool follows the controller with a short lag, not at once.
            var most = state.Blocked ? 1f - Mathf.Exp(-Time.unscaledDeltaTime / CatchUp) : 1f;
            Quaternion result;
            if (most >= 1f && Free(pivot, desired, length, out _)) result = desired;
            else
            {
                // The hand's movement may have put the last turn into something: turned out of it first.
                if (!Free(pivot, previous, length, out var hit)) previous = Out(pivot, previous, length, hit);
                // Then as far towards the controller as it is free, at most the catch-up's share.
                var from = 0f;
                var to = most;
                if (Free(pivot, Quaternion.Slerp(previous, desired, to), length, out _)) from = to;
                else
                {
                    for (var i = 0; i < 7; i++)
                    {
                        var middle = (from + to) * 0.5f;
                        if (Free(pivot, Quaternion.Slerp(previous, desired, middle), length, out _)) from = middle;
                        else to = middle;
                    }
                }
                result = Quaternion.Slerp(previous, desired, from);
                if (Quaternion.Angle(result, desired) > MostOff) result = desired;
            }
            var blocked = Quaternion.Angle(result, desired) > 1f;
            if (blocked != state.Blocked && logs++ < 30)
                Plugin.Log.LogInfo($"Tool collision: {hand.Name} {tool} {(blocked ? $"held off, {Quaternion.Angle(result, desired):0}° from the controller" : "with the controller again")}");
            state.Blocked = blocked;
            state.Rotation = Quaternion.Inverse(rig.rotation) * result;
            state.Result = state.Rotation;
            return state.Result;
        }

        // From a little ahead of the grip to the muzzle along the tool.
        private static float Length(Vector3 pivot, Quaternion rotation)
        {
            if (!VrToolModels.Muzzle(pivot, rotation, out var muzzle, out _)) return Fallback * VrCamera.WorldScale;
            var along = Vector3.Dot(muzzle - pivot, rotation * Vector3.forward);
            return along > FromGrip * VrCamera.WorldScale ? along : Fallback * VrCamera.WorldScale;
        }

        // Whether the tool turned so is clear of everything but the player's own body; the first thing in its way if not.
        private static bool Free(Vector3 pivot, Quaternion rotation, float length, out RaycastHit first)
        {
            first = default;
            var scale = VrCamera.WorldScale;
            var forward = rotation * Vector3.forward;
            var start = pivot + forward * FromGrip * scale;
            var distance = length - FromGrip * scale;
            if (distance <= 0f) return true;
            var count = Physics.SphereCastNonAlloc(start, Radius * scale, forward, Hits, distance, Physics.DefaultRaycastLayers, QueryTriggerInteraction.Ignore);
            var player = GameRefs.Grab != null ? PlayerBody(GameRefs.Grab) : null;
            var nearest = float.MaxValue;
            for (var i = 0; i < count; i++)
            {
                var hit = Hits[i];
                // What the ball starts in counts too (distance 0): the tool lies in it.
                var body = hit.collider.attachedRigidbody;
                if (player != null && (body == player || hit.collider.transform.IsChildOf(player.transform))) continue;
                if (hit.distance >= nearest) continue;
                nearest = hit.distance;
                first = hit;
            }
            return nearest == float.MaxValue;
        }

        // The tool turned about the grip away from the surface it lies in, step by step until it is clear.
        private static Quaternion Out(Vector3 pivot, Quaternion rotation, float length, RaycastHit hit)
        {
            var forward = rotation * Vector3.forward;
            // A ball that starts inside something gets no normal from the cast: away from the collider's nearest point to
            // the middle of the tool instead.
            var middle = pivot + forward * length * 0.5f;
            var normal = hit.distance > 0f ? hit.normal : (middle - hit.collider.ClosestPoint(middle)).normalized;
            var axis = Vector3.Cross(forward, normal);
            if (axis.sqrMagnitude < 1e-6f) return rotation;
            var step = Quaternion.AngleAxis(OutStep, axis.normalized);
            var turned = rotation;
            for (var angle = OutStep; angle <= MostOut; angle += OutStep)
            {
                turned = step * turned;
                if (Free(pivot, turned, length, out _)) return turned;
            }
            return rotation;
        }
    }
}
