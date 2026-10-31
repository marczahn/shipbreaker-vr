using BBI.Unity.Game;
using HarmonyLib;
using UnityEngine;

namespace ShipbreakerVR
{
    // The player's body collides as one sphere, 32 cm around the head (the 'Player Collider' on the player). In the flat
    // game that keeps the camera off the walls; in VR it stops the head well before anything is near it, and airlocks are
    // a tight fit. While VR runs the sphere is smaller and sits lower, towards the legs, which have no collider: its
    // lowest point stays where it was, in front of and above the head there is less of it.
    internal static class VrHitbox
    {
        static VrHitbox() => VrSession.OnUnload("hitbox", Restore);

        // In meters of the game's world.
        private const float Radius = 0.22f;
        private const float TowardsLegs = 0.1f;
        // Back as well, so that it reaches 15 cm ahead of the head.
        private const float Backwards = 0.07f;

        private static readonly AccessTools.FieldRef<GrabController, Rigidbody> PlayerBody =
            AccessTools.FieldRefAccess<GrabController, Rigidbody>("m_PlayerRigidbodyReference");

        private static SphereCollider sphere;
        private static Rigidbody body;
        private static float gameRadius;
        private static Vector3 gameCenter;
        private static Vector3 gameCenterOfMass;

        // From VrFrame, each physics step: a new player comes with every scene.
        public static void FixedUpdate()
        {
            if (!VrXr.IsRunning)
            {
                Restore();
                return;
            }
            if (sphere != null) return;
            var grab = GameRefs.Grab;
            body = grab != null ? PlayerBody(grab) : null;
            if (body == null) return;
            foreach (var collider in body.GetComponentsInChildren<SphereCollider>(true))
                if (!collider.isTrigger) sphere = collider;
            if (sphere == null) return;
            gameRadius = sphere.radius;
            gameCenter = sphere.center;
            gameCenterOfMass = body.centerOfMass;
            sphere.radius = Radius;
            sphere.center = gameCenter + Vector3.down * TowardsLegs + Vector3.back * Backwards;
            // Without this the body would turn around the new center.
            body.centerOfMass = gameCenterOfMass;
            Plugin.Log.LogInfo($"Hitbox: sphere from {gameRadius:0.00} m to {Radius:0.00} m, {TowardsLegs:0.00} m towards the legs, {Backwards:0.00} m back");
        }

        private static void Restore()
        {
            if (sphere != null)
            {
                sphere.radius = gameRadius;
                sphere.center = gameCenter;
                if (body != null) body.centerOfMass = gameCenterOfMass;
            }
            sphere = null;
        }
    }
}
