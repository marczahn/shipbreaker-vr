using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Rendering;

namespace ShipbreakerVR
{
    // Small helpers several modules need, kept once.
    internal static class VrUtil
    {
        // A transform's path from the scene root, "root/.../name", for the log.
        // The point of a collider nearest to point (point itself inside it). Collider.ClosestPoint does not take a mesh
        // that is not convex, as most of a ship's are (Unity warns on every call): its bounds' nearest point then.
        public static Vector3 ClosestPoint(Collider collider, Vector3 point) =>
            collider is MeshCollider mesh && !mesh.convex ? collider.bounds.ClosestPoint(point) : collider.ClosestPoint(point);

        // The nearest points of two segments, a0-a1 and b0-b1, one on each.
        public static void ClosestOnSegments(Vector3 a0, Vector3 a1, Vector3 b0, Vector3 b1, out Vector3 onA, out Vector3 onB)
        {
            var d1 = a1 - a0;
            var d2 = b1 - b0;
            var r = a0 - b0;
            var a = Vector3.Dot(d1, d1);
            var e = Vector3.Dot(d2, d2);
            var f = Vector3.Dot(d2, r);
            float s, t;
            if (a < 1e-9f && e < 1e-9f) { s = t = 0f; }
            else if (a < 1e-9f) { s = 0f; t = Mathf.Clamp01(f / e); }
            else
            {
                var c = Vector3.Dot(d1, r);
                if (e < 1e-9f) { t = 0f; s = Mathf.Clamp01(-c / a); }
                else
                {
                    var b = Vector3.Dot(d1, d2);
                    var denom = a * e - b * b;
                    s = denom > 1e-9f ? Mathf.Clamp01((b * f - c * e) / denom) : 0f;
                    t = (b * s + f) / e;
                    if (t < 0f) { t = 0f; s = Mathf.Clamp01(-c / a); }
                    else if (t > 1f) { t = 1f; s = Mathf.Clamp01((b - c) / a); }
                }
            }
            onA = a0 + d1 * s;
            onB = b0 + d2 * t;
        }

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

        // UI/Default hidden by what lies in front of it: for things in the world drawn after the scene, with its depth
        // buffer bound.
        public static Material DepthTestedMaterial(Color color)
        {
            var material = new Material(Shader.Find("UI/Default")) { color = color };
            material.SetInt("unity_GUIZTestMode", (int)CompareFunction.LessEqual);
            return material;
        }
    }
}
