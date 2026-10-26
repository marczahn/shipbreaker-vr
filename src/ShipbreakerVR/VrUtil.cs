using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Rendering;

namespace ShipbreakerVR
{
    // Small helpers several modules need, kept once.
    internal static class VrUtil
    {
        // A transform's path from the scene root, "root/.../name", for the log.
        public static string Path(Transform t) => t.parent == null ? t.name : Path(t.parent) + "/" + t.name;

        // A transform and its parents, "name < parent < ...", at most depth of them: shorter than Path where the
        // nearest parents tell more than the root.
        public static string Chain(Transform transform, int depth = 8)
        {
            var names = new List<string>();
            for (var t = transform; t != null && names.Count < depth; t = t.parent) names.Add(t.name);
            return string.Join(" < ", names);
        }

        // The alpha the canvas groups above a UI element multiply into it.
        public static float GroupAlpha(Transform t)
        {
            var alpha = 1f;
            for (; t != null; t = t.parent)
            {
                var group = t.GetComponent<CanvasGroup>();
                if (group == null || !group.enabled) continue;
                alpha *= group.alpha;
                if (group.ignoreParentGroups) break;
            }
            return alpha;
        }

        // UI/Default drawn over everything, like a display in the helmet.
        public static Material OverlayMaterial(Color color)
        {
            var material = new Material(Shader.Find("UI/Default")) { color = color };
            material.SetInt("unity_GUIZTestMode", (int)CompareFunction.Always);
            return material;
        }

        public static Material OverlayMaterial() => OverlayMaterial(Color.white);
    }
}
