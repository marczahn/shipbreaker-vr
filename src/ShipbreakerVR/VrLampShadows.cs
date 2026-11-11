using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Rendering.HighDefinition;

namespace ShipbreakerVR
{
    // Bild/LampenschattenNurNah (Raik, 2026-11-10, off by default): in the large freighters the GPU was the limit, and
    // the lamps' shadows cost up to 1.5 ms there (about 70 lamps with shadows, 15 of them lighting the view from within
    // 10 m; measured with all shadows switched off in turn). With the option on, the shadows of the lamps (all lights but
    // the sun, whose shadows stay out to the game's 500 m) fade out by Near from the camera, where HDRP stops rendering
    // them; the lamps themselves still shine. Lights are looked through every Every seconds, as ships bring new ones; each
    // gets its own distance back when the option goes off or VR stops.
    internal static class VrLampShadows
    {
        static VrLampShadows() => VrSession.OnUnload("lamp shadows", RestoreAll);

        private const float Near = 20f;
        private const float Every = 2f;

        private static readonly Dictionary<HDAdditionalLightData, float> Changed = new Dictionary<HDAdditionalLightData, float>();
        private static readonly List<HDAdditionalLightData> Gone = new List<HDAdditionalLightData>();
        private static float next;
        private static bool logged;

        // From VrFrame, once a frame.
        public static void Update()
        {
            if (!VrSettings.NearLampShadows.Value || !VrXr.IsRunning)
            {
                if (Changed.Count > 0) RestoreAll();
                return;
            }
            if (Time.unscaledTime < next) return;
            next = Time.unscaledTime + Every;
            Gone.Clear();
            foreach (var data in Changed.Keys)
                if (data == null) Gone.Add(data);
            foreach (var data in Gone) Changed.Remove(data);
            var near = Near * VrCamera.WorldScale;
            var added = 0;
            foreach (var data in Object.FindObjectsOfType<HDAdditionalLightData>())
            {
                if (Changed.ContainsKey(data)) continue;
                var light = data.GetComponent<Light>();
                if (light == null || light.type == LightType.Directional || light.shadows == LightShadows.None) continue;
                if (data.shadowFadeDistance <= near) continue;
                Changed[data] = data.shadowFadeDistance;
                data.shadowFadeDistance = near;
                added++;
            }
            if (added > 0 && !logged)
            {
                logged = true;
                Plugin.Log.LogInfo($"Lamp shadows: {added} lamp(s) cast shadows only within {Near:0} m now");
            }
        }

        private static void RestoreAll()
        {
            foreach (var pair in Changed)
                if (pair.Key != null) pair.Key.shadowFadeDistance = pair.Value;
            Changed.Clear();
            logged = false;
        }
    }
}
