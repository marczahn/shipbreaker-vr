using System.Collections.Generic;
using BBI.Unity.Game;
using HarmonyLib;
using UnityEngine;

namespace ShipbreakerVR
{
    // Three dots circling where the Stinger's beam ends, while a part is within its reach: its reach is far shorter than
    // the laser's, so the laser alone does not tell whether a shot would land.
    internal static class VrStingerDots
    {
        private const float Radius = 0.035f;
        private const float TurnsPerSecond = 0.8f;
        private const int Dots = 3;

        private static readonly AccessTools.FieldRef<CuttingToolController, ScalpelController> StingerOf =
            AccessTools.FieldRefAccess<CuttingToolController, ScalpelController>("m_ScalpelController");

        public static void Add(List<Vector3> points, Vector3 from)
        {
            var tool = GameRefs.Cutter;
            if (tool == null || tool.CurrentMode != CuttingToolController.CutterMode.Scalpel) return;
            var stinger = StingerOf(tool);
            var target = stinger != null ? stinger.Target : null;
            if (target == null || target.Part == null) return;
            var way = (target.Position - from).normalized;
            var across = Vector3.Cross(way, Mathf.Abs(way.y) < 0.9f ? Vector3.up : Vector3.right).normalized;
            var radius = Radius * VrCamera.WorldScale;
            for (var i = 0; i < Dots; i++)
            {
                var angle = 360f * (Time.unscaledTime * TurnsPerSecond + (float)i / Dots);
                points.Add(target.Position + Quaternion.AngleAxis(angle, way) * across * radius);
            }
        }
    }
}
