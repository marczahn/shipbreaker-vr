using UnityEngine;

namespace ShipbreakerVR
{
    // Extended body physics (Raik 2026-11-26): what the hands throw pushes the body back, as much as it takes along
    // (momentum kept: the thrown thing's mass times its speed against the body, over the body's mass, 100 kg with this option, the
    // same the tool's line pulls with, VrClimb.BodyWeight). A grapple thrown forward pushes the body gently back, and its line
    // catching it at its end pulls the body forward again (VrLooseTools.Tighten), so the two about even out. While a
    // hand holds on to something the push goes into that hold, as the line's pull does.
    internal static class VrRecoil
    {
        private static int logs;

        public static bool On => VrSettings.BodyPhysics != null && VrSettings.BodyPhysics.Value;

        // Something of that mass (kg) let go at that speed against the body (world units per second).
        public static void Throw(Rigidbody player, float mass, Vector3 against, string what)
        {
            if (!On || player == null || player.isKinematic || mass <= 0f || against.sqrMagnitude < 1e-6f) return;
            var body = VrClimb.BodyWeight;
            var held = VrHand.Left.State == HandState.Holding || VrHand.Right.State == HandState.Holding;
            var kick = against * (-mass / body);
            if (!held) player.AddForce(kick, ForceMode.VelocityChange);
            if (logs++ < 40)
                Plugin.Log.LogInfo($"Recoil: {what} ({mass:0.0} kg) thrown at {against.magnitude / VrCamera.WorldScale:0.00} m/s, "
                    + (held ? "the hold takes it" : $"the body ({body:0} kg) goes back at {kick.magnitude / VrCamera.WorldScale:0.00} m/s"));
        }
    }
}
