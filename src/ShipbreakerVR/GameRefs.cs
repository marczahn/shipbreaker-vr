using BBI.Unity.Game;
using UnityEngine;

namespace ShipbreakerVR
{
    // The player's controllers that several modules need, found once and shared. Before, VrToolInput, VrDualTools,
    // VrClimb, VrHands and VrToolModels each searched the scene for the same objects with their own cache and retry
    // timer. A reference whose object was destroyed (a scene change) counts as missing and is searched again, at most
    // once a second per kind, so a missing one (main menu, loading) costs no search every frame.
    internal static class GameRefs
    {
        private static EquipmentController equipment;
        private static GrabController grab;
        private static CuttingToolController cutter;
        private static GunController gun;
        private static float nextEquipment, nextGrab, nextCutter, nextGun;

        public static EquipmentController Equipment => Find(ref equipment, ref nextEquipment);
        public static GrabController Grab => Find(ref grab, ref nextGrab);
        public static CuttingToolController Cutter => Find(ref cutter, ref nextCutter);
        public static GunController Gun => Find(ref gun, ref nextGun);

        private static T Find<T>(ref T cached, ref float next) where T : Object
        {
            if (cached != null || Time.unscaledTime < next) return cached;
            next = Time.unscaledTime + 1f;
            cached = Object.FindObjectOfType<T>();
            return cached;
        }
    }
}
