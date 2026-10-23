using System.Reflection;
using BBI.Unity.Game;
using HarmonyLib;
using UnityEngine;

namespace ShipbreakerVR
{
    // The bay's play area is a set of nodes (PlayableArea.PlayableNodes) with three radii around each: inside the warning
    // radius (100 m by default) the player is safe, up to the danger radius (125 m) the visor warns, beyond it a
    // countdown starts and then the suit takes damage. Separately, PlayableAreaSystem marks every object further than
    // the object radius (130 m) from all nodes as out of bounds and destroys it after a delay. In VR the bay's structure
    // invites flying around it, so while VR runs all three radii are lifted, objects included, so parts the player
    // carries along stay in bounds as well. Without VR the game's radii apply again.
    internal static class VrPlayArea
    {
        private const float Unlimited = 1e7f;

        private static readonly FieldInfo WarningField = AccessTools.Field(typeof(PlayableArea), "m_WarningRadius");
        private static readonly FieldInfo DangerField = AccessTools.Field(typeof(PlayableArea), "m_DangerRadius");
        private static readonly FieldInfo ObjectField = AccessTools.Field(typeof(PlayableArea), "m_ObjectDangerRadius");

        private static PlayableArea area;
        private static float warning, danger, objects;
        private static bool lifted;

        public static void Adopt(PlayableArea instance)
        {
            area = instance;
            warning = instance.WarningRadius;
            danger = instance.DangerRadius;
            objects = instance.ObjectDangerRadius;
            lifted = false;
            var nodes = instance.PlayableNodes;
            var bounds = new Bounds();
            var first = true;
            foreach (var node in nodes)
            {
                if (node == null) continue;
                if (first) bounds = new Bounds(node.position, Vector3.zero);
                else bounds.Encapsulate(node.position);
                first = false;
            }
            Plugin.Log.LogInfo($"Play area '{instance.name}': {nodes.Count} nodes spanning {bounds.size.x:0} x {bounds.size.y:0} x {bounds.size.z:0} m " +
                               $"around {bounds.center}, radii safe {warning:0} m, warning {danger:0} m, objects {objects:0} m");
            Update();
        }

        public static void Update()
        {
            if (area == null || lifted == VrXr.IsRunning) return;
            lifted = VrXr.IsRunning;
            WarningField?.SetValue(area, lifted ? Unlimited : warning);
            DangerField?.SetValue(area, lifted ? Unlimited : danger);
            ObjectField?.SetValue(area, lifted ? Unlimited : objects);
            Plugin.Log.LogInfo(lifted ? "Play area: unlimited while VR runs" : "Play area: the game's radii");
        }
    }

    [HarmonyPatch(typeof(PlayableArea), "Awake")]
    internal static class PlayableAreaAwakePatch
    {
        private static void Postfix(PlayableArea __instance) => VrPlayArea.Adopt(__instance);
    }
}
