using BBI.Unity.Game;
using HarmonyLib;
using UnityEngine;

namespace ShipbreakerVR
{
    // Steuerung/ZurWandDrehen, an experiment (Raik, 2026-11-08): the body model hangs upright under the head
    // (VrTorso) and has no collider of its own, so near the hull it sank into it, seen when looking down. Now, while the
    // hull lies within BodyLength below the head along the body, the whole player turns towards it: the legs swing to the
    // back and the view tips forward with them, as one would turn oneself onto a wall. The turn goes to the playspace like
    // the stick's (VrControls), around the head, until the body clears the hull.
    internal static class VrWallTurn
    {
        // How far below the head the body reaches, in meters at world scale 1 (eyes to about the knees), and from how
        // near on it turns at full speed.
        private const float BodyLength = 1.1f;
        private const float FullFrom = 0.6f;
        // How wide the body is looked for, in meters at world scale 1.
        private const float Width = 0.12f;
        // Degrees per second at full speed.
        private const float Speed = 45f;

        private static readonly AccessTools.FieldRef<GrabController, Rigidbody> PlayerBody =
            AccessTools.FieldRefAccess<GrabController, Rigidbody>("m_PlayerRigidbodyReference");
        private static readonly RaycastHit[] Hits = new RaycastHit[16];
        private static bool turning;
        private static int logs;

        // The turn for this frame, in the world (from VrControls, during gameplay).
        public static Quaternion Step(Quaternion playspace, float dt)
        {
            var vr = VrCamera.Instance;
            var camera = vr != null ? vr.GameCamera : null;
            var grab = GameRefs.Grab;
            var player = grab != null ? PlayerBody(grab) : null;
            if (!VrSettings.WallTurn.Value || camera == null || vr.FollowsHab || player == null) return Stop();

            var scale = VrCamera.WorldScale;
            var down = playspace * Vector3.down;
            var length = BodyLength * scale;
            var count = Physics.SphereCastNonAlloc(camera.transform.position, Width * scale, down, Hits, length,
                Physics.DefaultRaycastLayers, QueryTriggerInteraction.Ignore);
            var nearest = float.MaxValue;
            Collider hull = null;
            for (var i = 0; i < count; i++)
            {
                var hit = Hits[i];
                // Not the player's own colliders (body, tools), nor anything the sphere starts in (distance 0).
                if (hit.collider.attachedRigidbody == player || hit.collider.transform.IsChildOf(player.transform) || hit.distance <= 0f) continue;
                if (hit.distance >= nearest) continue;
                nearest = hit.distance;
                hull = hit.collider;
            }
            if (hull == null) return Stop();

            // Around the body's right, so the legs go back and the view forward, faster the deeper the body would be in.
            var heading = vr.HeadRotation * Vector3.forward;
            heading.y = 0f;
            if (heading.sqrMagnitude < 1e-4f) heading = vr.HeadRotation * Vector3.up;
            heading.y = 0f;
            if (heading.sqrMagnitude < 1e-4f) return Stop();
            var right = playspace * Vector3.Cross(Vector3.up, heading.normalized);
            var share = Mathf.Clamp01((length - nearest) / ((BodyLength - FullFrom) * scale));
            if (!turning && logs++ < 30)
                Plugin.Log.LogInfo($"Wall turn: '{hull.name}' {nearest / scale:0.00} m below the head along the body, turning towards it");
            turning = true;
            return Quaternion.AngleAxis(Speed * share * dt, right);
        }

        private static Quaternion Stop()
        {
            turning = false;
            return Quaternion.identity;
        }
    }
}
