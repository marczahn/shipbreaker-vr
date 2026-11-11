using System.Collections.Generic;
using UnityEngine;

namespace ShipbreakerVR
{
    // Bild/TruemmerWeiterSehen (Raik, 2026-11-08, off by default): the small floating debris ('PRF_MeshParticles_...',
    // loose 1 kg nuts and bolts on the Ignore Raycast layer) showed only within about 2 m. The game has no code of its own
    // for that; their LOD groups drop them once they get small on screen. With the option on, the debris near the head
    // (checked every Every seconds within Near) is drawn out to Range: its last level's switch is lowered to the screen
    // height the debris has there. Off again, each gets its own levels back.
    internal static class VrDebrisRange
    {
        static VrDebrisRange() => VrSession.OnUnload("debris range", RestoreAll);

        private const string Prefix = "PRF_MeshParticles_";
        // How far the debris is drawn, how near it is looked for and how often, in meters at world scale 1 and seconds.
        private const float Range = 5f;
        private const float Near = 8f;
        private const float Every = 0.5f;
        private const int IgnoreRaycastLayer = 1 << 2;

        private static readonly Collider[] Found = new Collider[256];
        private static readonly Dictionary<LODGroup, LOD[]> Changed = new Dictionary<LODGroup, LOD[]>();
        private static readonly List<LODGroup> Gone = new List<LODGroup>();
        private static float next;
        private static int logs;
        private static int missLogs;

        // From VrFrame, once a frame.
        public static void Update()
        {
            if (!VrSettings.DebrisRange.Value || !VrXr.IsRunning)
            {
                if (Changed.Count > 0) RestoreAll();
                return;
            }
            if (Time.unscaledTime < next) return;
            next = Time.unscaledTime + Every;
            var vr = VrCamera.Instance;
            var eye = vr != null ? vr.Camera : null;
            var head = vr != null ? vr.GameCamera : null;
            if (eye == null || head == null) return;
            Forget();
            var scale = VrCamera.WorldScale;
            var count = Physics.OverlapSphereNonAlloc(head.transform.position, Near * scale, Found, IgnoreRaycastLayer, QueryTriggerInteraction.Ignore);
            // The screen height an object of size 1 has at Range: size / (2 Range tan(fov / 2)), LOD bias included.
            var perSize = QualitySettings.lodBias / (2f * Range * scale * Mathf.Tan(eye.fieldOfView * 0.5f * Mathf.Deg2Rad));
            for (var i = 0; i < count; i++)
            {
                var root = Found[i].attachedRigidbody != null ? Found[i].attachedRigidbody.transform : Found[i].transform;
                if (!root.name.StartsWith(Prefix)) continue;
                var group = root.GetComponentInChildren<LODGroup>();
                if (group == null)
                {
                    if (missLogs++ < 5) Plugin.Log.LogInfo($"Debris range: '{root.name}' has no LOD group, left as it is");
                    continue;
                }
                if (Changed.ContainsKey(group)) continue;
                var lods = group.GetLODs();
                if (lods.Length == 0) continue;
                var size = group.size * MaxScale(group.transform);
                var wanted = size * perSize;
                var last = lods.Length - 1;
                if (lods[last].screenRelativeTransitionHeight <= wanted) continue;
                Changed[group] = (LOD[])lods.Clone();
                var changed = (LOD[])lods.Clone();
                changed[last].screenRelativeTransitionHeight = wanted;
                for (var j = last - 1; j >= 0; j--)
                    changed[j].screenRelativeTransitionHeight = Mathf.Max(changed[j].screenRelativeTransitionHeight, changed[j + 1].screenRelativeTransitionHeight * 1.01f);
                group.SetLODs(changed);
                if (logs++ < 10)
                    Plugin.Log.LogInfo($"Debris range: '{root.name}' ({lods.Length} level(s), size {size:0.000} m) drawn down to {wanted:0.0000} of the screen height instead of {lods[last].screenRelativeTransitionHeight:0.0000}, out to {Range:0} m");
            }
        }

        // Debris that is gone (collected, destroyed).
        private static void Forget()
        {
            Gone.Clear();
            foreach (var group in Changed.Keys)
                if (group == null) Gone.Add(group);
            foreach (var group in Gone) Changed.Remove(group);
        }

        private static void RestoreAll()
        {
            foreach (var pair in Changed)
                if (pair.Key != null) pair.Key.SetLODs(pair.Value);
            Changed.Clear();
        }

        private static float MaxScale(Transform t)
        {
            var s = t.lossyScale;
            return Mathf.Max(Mathf.Abs(s.x), Mathf.Abs(s.y), Mathf.Abs(s.z));
        }
    }
}
