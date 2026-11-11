using System.Collections.Generic;
using BBI.Unity.Game;
using HarmonyLib;
using UnityEngine;

namespace ShipbreakerVR
{
    // Collectables (spare parts such as circuit boards, filters and cables, cassettes) for the cutter and the grapple
    // (Raik, 2026-11-08): their solid colliders are on the Ignore Raycast layer, so the cutter's and the grapple's rays
    // went through them. In VR, the solid ones near the head are put on a layer both tools aim at (in the grapple's valid
    // and base masks and the cutter's ray and surface masks); their 'Interaction' trigger stays where it is, so taking
    // them still works as before. Off VR, each goes back to its own layer.
    internal static class VrPickupLayer
    {
        static VrPickupLayer() => VrSession.OnUnload("pickup layer", RestoreAll);

        private const int IgnoreRaycast = 2;
        private const float Near = 40f;
        private const float Every = 1f;

        private static readonly AccessTools.FieldRef<CuttingToolController, CuttingController> CuttingControllerOf =
            AccessTools.FieldRefAccess<CuttingToolController, CuttingController>("m_CuttingController");
        private static readonly AccessTools.FieldRef<CuttingController, ICutterData> CutterData =
            AccessTools.FieldRefAccess<CuttingController, ICutterData>("mData");

        private static readonly Collider[] Found = new Collider[1024];
        private static readonly Dictionary<Collider, int> Moved = new Dictionary<Collider, int>();
        private static readonly List<Collider> Gone = new List<Collider>();
        private static int layer = -1;
        private static float next;
        private static int logs;

        // From VrFrame, once a frame.
        public static void Update()
        {
            if (!VrXr.IsRunning)
            {
                if (Moved.Count > 0) RestoreAll();
                return;
            }
            if (Time.unscaledTime < next) return;
            next = Time.unscaledTime + Every;
            if (layer < 0 && !FindLayer()) return;
            if (layer > 31) return;
            var head = VrCamera.Instance != null ? VrCamera.Instance.GameCamera : null;
            if (head == null) return;
            Forget();
            var count = Physics.OverlapSphereNonAlloc(head.transform.position, Near * VrCamera.WorldScale, Found, 1 << IgnoreRaycast, QueryTriggerInteraction.Ignore);
            for (var i = 0; i < count; i++)
            {
                var collider = Found[i];
                if (collider.isTrigger || collider.attachedRigidbody == null || collider.name.StartsWith("PRF_")) continue;
                var part = collider.GetComponentInParent<StructurePart>();
                if (part == null) continue;
                Moved[collider] = collider.gameObject.layer;
                collider.gameObject.layer = layer;
                if (logs++ < 20)
                    Plugin.Log.LogInfo($"Pickup layer: '{collider.name}' on '{part.name}' ({collider.attachedRigidbody.mass:0.0} kg) now on layer {layer} '{LayerMask.LayerToName(layer)}' for the cutter and the grapple");
            }
        }

        // A layer the grapple and the cutter both aim at; the cutter's masks only once it is known. Of those, the one most
        // like Ignore Raycast for the game's own use: not in the mask its interaction rays look for (so a collectable's
        // solid body never stands before its 'Interaction' trigger), then colliding with the same layers (so it bumps into
        // what it did before), then the lowest. None at all: int.MaxValue, and nothing is moved.
        private static bool FindLayer()
        {
            var settings = Main.Instance != null && Main.Instance.MainSettings != null ? Main.Instance.MainSettings.RaycastSettings : null;
            if (settings == null) return false;
            var mask = settings.GrappleValidLayerMask.value & settings.BaseLayerMask.value;
            var tool = GameRefs.Cutter;
            var cutting = tool != null ? CuttingControllerOf(tool) : null;
            var data = cutting != null ? CutterData(cutting) : null;
            if (data == null) return false;
            mask &= data.RaycastLayerMask.value & data.ValidSurfaceLayerMask.value;
            mask &= ~(1 << IgnoreRaycast);
            var interact = settings.InteractValidLayerMask.value;
            var best = -1;
            var bestScore = int.MinValue;
            for (var i = 0; i < 32; i++)
            {
                if ((mask & (1 << i)) == 0) continue;
                var score = (interact & (1 << i)) == 0 ? 100 : 0;
                for (var k = 0; k < 32; k++)
                    if (Physics.GetIgnoreLayerCollision(IgnoreRaycast, k) == Physics.GetIgnoreLayerCollision(i, k)) score++;
                if (score > bestScore) { best = i; bestScore = score; }
            }
            if (best >= 0)
            {
                layer = best;
                var same = 0;
                for (var k = 0; k < 32; k++)
                    if (Physics.GetIgnoreLayerCollision(IgnoreRaycast, k) == Physics.GetIgnoreLayerCollision(best, k)) same++;
                Plugin.Log.LogInfo($"Pickup layer: collectables go on layer {best} '{LayerMask.LayerToName(best)}' ({((interact & (1 << best)) == 0 ? "not" : "also")} in the interaction mask {interact:X}, "
                    + $"collides like Ignore Raycast with {same} of 32 layers; grapple valid {settings.GrappleValidLayerMask.value:X}, base {settings.BaseLayerMask.value:X}, "
                    + $"cutter ray {data.RaycastLayerMask.value:X}, surface {data.ValidSurfaceLayerMask.value:X})");
                return true;
            }
            if (logs++ < 20) Plugin.Log.LogInfo("Pickup layer: no layer that both the grapple and the cutter aim at");
            layer = int.MaxValue;
            return false;
        }

        private static void Forget()
        {
            Gone.Clear();
            foreach (var collider in Moved.Keys)
                if (collider == null) Gone.Add(collider);
            foreach (var collider in Gone) Moved.Remove(collider);
        }

        private static void RestoreAll()
        {
            foreach (var pair in Moved)
                if (pair.Key != null) pair.Key.gameObject.layer = pair.Value;
            Moved.Clear();
        }
    }
}
