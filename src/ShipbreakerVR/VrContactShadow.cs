using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Rendering;

namespace ShipbreakerVR
{
    // A shadow for a thing the mod sets down in the Hab (Raik 2026-11-16, pictures: the drill, the bottle and the tape on
    // the desk have shadows, the radio and the AI nodes none). The Hab's light is baked: its shadows are drawn into the
    // surfaces' light maps beforehand, so anything put there later casts none. This lays a thin see-through dark patch on
    // what the thing stands on (the game has decals switched off). It is a child of the thing, so it is there only while
    // the thing is, and goes with it. Two kinds:
    //  - Add: a soft patch under the thing's foot (the AI nodes).
    //  - AddFromLamp: the thing's own shape thrown by one lamp onto the surface it stands on, as the baked shadows around
    //    it are (the radio: Raik 2026-11-16, its shadow belongs behind it, away from the ceiling bay's lamps; the drill's
    //    baked shadow beside it, measured on his picture, is dark in its core with an edge of a few centimetres, sharp near
    //    the drill). Sharp where the thing touches, softer the farther out; cut off where the surface ends.
    internal static class VrContactShadow
    {
        // The foot patch: how dark right under the thing, and how far the soft edge reaches out, by the thing's height.
        private const float Dark = 0.6f;
        private const float EdgeBase = 0.03f;
        private const float EdgePerHeight = 0.12f;
        // Only the lower part makes the foot print and the softness.
        private const float FootHeight = 0.3f;
        // The thrown shadow: its darkness (the drill's core darkens the desk to about a third), its edge's softness near
        // the thing and at its far end, how much lighter it gets out there, and the texel size.
        private const float CastDark = 0.8f;
        private const float SoftNear = 0.006f;
        private const float SoftFar = 0.035f;
        private const float FarLighter = 0.35f;
        private const float CastTexel = 0.004f;
        private const int MaxTexels = 256;
        // A lamp lower than this above a point throws no useful shadow of it.
        private const float LampAbove = 0.05f;
        // The patch lies this far above the foot's lowest point, on the surface below without flickering into it.
        private const float Lift = 0.0015f;
        private const int Texels = 128;

        static VrContactShadow() => VrSession.OnUnload("contact shadows", Release);

        private static Material template;
        private static bool usesUnlit;
        // Each patch with the mesh, material and texture made for it: they go when the patch is gone (with the thing it
        // lies under, as the Hab is left), and all of them on unload. Until 0.32.0 they were made anew on every visit
        // to the Hab and never freed.
        private static readonly List<(GameObject Patch, Object[] Made)> Patches = new List<(GameObject, Object[])>();

        private static void Sweep(bool all)
        {
            for (var i = Patches.Count - 1; i >= 0; i--)
            {
                var (patch, made) = Patches[i];
                if (patch != null && !all) continue;
                foreach (var o in made) if (o != null) Object.Destroy(o);
                if (patch != null) Object.Destroy(patch);
                Patches.RemoveAt(i);
            }
        }

        // On unload (hot reload).
        private static void Release()
        {
            Sweep(true);
            if (template != null && !usesUnlit) Object.Destroy(template);
            template = null;
        }

        // ---- Under the foot ----

        public static void Add(GameObject thing, Mesh mesh, Vector3 up, string what)
        {
            if (thing == null || mesh == null || Template() == null) return;
            // The thing's foot print on the surface: its points seen from above, in the turn that gives the smallest box.
            var points = World(thing, mesh, up, out var low, out var high);
            var cut = low + FootHeight;
            var foot = new List<Vector3>();
            foreach (var p in points) if (Vector3.Dot(p, up) <= cut) foot.Add(p);
            high = Mathf.Min(high, cut);
            var across = Vector3.Cross(up, Mathf.Abs(Vector3.Dot(up, Vector3.forward)) < 0.9f ? Vector3.forward : Vector3.right).normalized;
            var best = float.MaxValue;
            var bestTurn = 0f;
            var bestMin = Vector2.zero;
            var bestMax = Vector2.zero;
            for (var turn = 0f; turn < 180f; turn += 3f)
            {
                var x = Quaternion.AngleAxis(turn, up) * across;
                Extent(foot, x, Vector3.Cross(x, up), out var min, out var max);
                var area = (max.x - min.x) * (max.y - min.y);
                if (area >= best) continue;
                best = area;
                bestTurn = turn;
                bestMin = min;
                bestMax = max;
            }
            var ax = Quaternion.AngleAxis(bestTurn, up) * across;
            var az = Vector3.Cross(ax, up);
            var size = bestMax - bestMin;
            var mid = (bestMin + bestMax) * 0.5f;
            var edge = EdgeBase + EdgePerHeight * (high - low);
            var patch = new Vector2(size.x + 2f * edge, size.y + 2f * edge);
            Lay(thing, ax * mid.x + az * mid.y + up * (low + Lift), ax, az, patch, FootPatch(size, edge, patch));
            Plugin.Log.LogInfo($"Shadow: under {what}, {size.x:0.00} x {size.y:0.00} m, soft edge {edge:0.00} m");
        }

        // The patch's alpha: full darkness over the foot print, falling off softly over the edge around it.
        private static Texture2D FootPatch(Vector2 foot, float edge, Vector2 size)
        {
            var width = Texels;
            var height = Mathf.Clamp(Mathf.RoundToInt(Texels * size.y / Mathf.Max(0.01f, size.x)), 16, Texels * 2);
            var alpha = new float[width * height];
            var half = foot * 0.5f;
            // Rounded a little at the corners, as a real shadow's are.
            var round = Mathf.Min(edge, Mathf.Min(half.x, half.y));
            var inner = new Vector2(half.x - round * 0.5f, half.y - round * 0.5f);
            for (var y = 0; y < height; y++)
                for (var x = 0; x < width; x++)
                {
                    var p = new Vector2(((x + 0.5f) / width - 0.5f) * size.x, ((y + 0.5f) / height - 0.5f) * size.y);
                    var d = new Vector2(Mathf.Max(0f, Mathf.Abs(p.x) - inner.x), Mathf.Max(0f, Mathf.Abs(p.y) - inner.y)).magnitude - round * 0.5f;
                    alpha[y * width + x] = Dark * (1f - Smooth(Mathf.InverseLerp(-edge * 0.5f, edge, d)));
                }
            return Texture(alpha, width, height);
        }

        // ---- Thrown by a lamp ----

        // lamp: where the lamp is; surface: the top the thing stands on, as a box in the room's axes (the shadow ends at
        // its edges).
        public static void AddFromLamp(GameObject thing, Mesh mesh, Vector3 up, Vector3 lamp, Transform room, Bounds surface, string what)
        {
            if (thing == null || mesh == null || room == null || Template() == null) return;
            var points = World(thing, mesh, up, out var low, out _);
            var lampHeight = Vector3.Dot(lamp, up) - low;
            if (lampHeight < LampAbove * 2f)
            {
                Plugin.Log.LogWarning($"Shadow: the lamp is not above {what}, it gets only a patch under its foot");
                Add(thing, mesh, up, what);
                return;
            }
            // Each point thrown along the lamp's ray down onto the surface's plane.
            var onPlane = new Vector3[points.Length];
            var thrown = new bool[points.Length];
            for (var i = 0; i < points.Length; i++)
            {
                var above = Vector3.Dot(points[i], up) - low;
                thrown[i] = above < lampHeight - LampAbove;
                if (thrown[i]) onPlane[i] = lamp + (points[i] - lamp) * (lampHeight / (lampHeight - above));
            }
            // Lined up with the way the shadow falls: from under the lamp out through the thing.
            var center = thing.transform.position;
            var away = Vector3.ProjectOnPlane(center - lamp, up);
            if (away.sqrMagnitude < 1e-6f) away = Vector3.ProjectOnPlane(-thing.transform.forward, up);
            var ax = Vector3.Cross(up, away.normalized).normalized;
            var az = Vector3.Cross(ax, up);
            var flat = new List<Vector3>();
            for (var i = 0; i < points.Length; i++) if (thrown[i]) flat.Add(onPlane[i]);
            Extent(flat, ax, az, out var min, out var max);
            var margin = SoftFar * 2f + 0.01f;
            min -= new Vector2(margin, margin);
            max += new Vector2(margin, margin);
            var size = max - min;
            var texel = Mathf.Max(CastTexel, Mathf.Max(size.x, size.y) / MaxTexels);
            var width = Mathf.Clamp(Mathf.CeilToInt(size.x / texel), 8, MaxTexels);
            var height = Mathf.Clamp(Mathf.CeilToInt(size.y / texel), 8, MaxTexels);
            size = new Vector2(width * texel, height * texel);

            // The thing's shape on the plane: every triangle thrown, filled in.
            var mask = new float[width * height];
            var triangles = mesh.triangles;
            var grid = new Vector2[points.Length];
            for (var i = 0; i < points.Length; i++)
                if (thrown[i]) grid[i] = new Vector2((Vector3.Dot(onPlane[i], ax) - min.x) / texel, (Vector3.Dot(onPlane[i], az) - min.y) / texel);
            for (var t = 0; t + 2 < triangles.Length; t += 3)
            {
                int a = triangles[t], b = triangles[t + 1], c = triangles[t + 2];
                if (!thrown[a] || !thrown[b] || !thrown[c]) continue;
                Fill(mask, width, height, grid[a], grid[b], grid[c]);
            }
            // Its edge soft by how far out: sharp where the thing stands, soft at the far end, and lighter there.
            var near = Blur(mask, width, height, Mathf.Max(1, Mathf.RoundToInt(SoftNear / texel)));
            var far = Blur(mask, width, height, Mathf.Max(1, Mathf.RoundToInt(SoftFar / texel)));
            var foot = Vector3.Dot(center, az);
            var reach = Mathf.Max(0.01f, max.y - margin - foot);
            var alpha = new float[width * height];
            var origin = ax * min.x + az * min.y + up * (low + Lift);
            var inside = 0;
            for (var y = 0; y < height; y++)
            {
                var along = Mathf.Clamp01(((y + 0.5f) * texel + min.y - foot) / reach);
                for (var x = 0; x < width; x++)
                {
                    // Only on the surface: cut where its top ends (a little inside its box, which reaches to its rim).
                    var at = room.InverseTransformPoint(origin + ax * ((x + 0.5f) * texel) + az * ((y + 0.5f) * texel));
                    if (at.x < surface.min.x + SurfaceKept || at.x > surface.max.x - SurfaceKept
                        || at.z < surface.min.z + SurfaceKept || at.z > surface.max.z - SurfaceKept) continue;
                    var i = y * width + x;
                    alpha[i] = CastDark * (1f - FarLighter * along) * Mathf.Lerp(near[i], far[i], Smooth(along));
                    if (alpha[i] > 0.05f) inside++;
                }
            }
            Lay(thing, origin + ax * (size.x * 0.5f) + az * (size.y * 0.5f), ax, az, size, Texture(alpha, width, height));
            Plugin.Log.LogInfo($"Shadow: {what}'s shape thrown by the lamp {lampHeight:0.00} m above it, {size.x:0.00} x {size.y:0.00} m "
                + $"({width} x {height} texels, {inside} dark on the surface), reaching {reach:0.00} m out");
        }

        // The surface's box reaches to its rim; the shadow stays this far inside.
        private const float SurfaceKept = 0.015f;

        // One triangle filled into the mask (texel centres inside it).
        private static void Fill(float[] mask, int width, int height, Vector2 a, Vector2 b, Vector2 c)
        {
            var area = Cross(b - a, c - a);
            if (Mathf.Abs(area) < 1e-6f) return;
            var x0 = Mathf.Max(0, Mathf.FloorToInt(Mathf.Min(a.x, Mathf.Min(b.x, c.x))));
            var x1 = Mathf.Min(width - 1, Mathf.CeilToInt(Mathf.Max(a.x, Mathf.Max(b.x, c.x))));
            var y0 = Mathf.Max(0, Mathf.FloorToInt(Mathf.Min(a.y, Mathf.Min(b.y, c.y))));
            var y1 = Mathf.Min(height - 1, Mathf.CeilToInt(Mathf.Max(a.y, Mathf.Max(b.y, c.y))));
            for (var y = y0; y <= y1; y++)
                for (var x = x0; x <= x1; x++)
                {
                    var p = new Vector2(x + 0.5f, y + 0.5f);
                    var w0 = Cross(b - p, c - p) / area;
                    var w1 = Cross(c - p, a - p) / area;
                    if (w0 >= 0f && w1 >= 0f && w0 + w1 <= 1f) mask[y * width + x] = 1f;
                }
        }

        private static float Cross(Vector2 a, Vector2 b) => a.x * b.y - a.y * b.x;

        // A box blur done twice each way, near a gaussian of about the radius.
        private static float[] Blur(float[] source, int width, int height, int radius)
        {
            var a = (float[])source.Clone();
            var b = new float[a.Length];
            for (var pass = 0; pass < 2; pass++)
            {
                BlurLine(a, b, width, height, radius, true);
                BlurLine(b, a, width, height, radius, false);
            }
            return a;
        }

        private static void BlurLine(float[] from, float[] to, int width, int height, int radius, bool rows)
        {
            var lines = rows ? height : width;
            var length = rows ? width : height;
            for (var l = 0; l < lines; l++)
            {
                var sum = 0f;
                for (var k = -radius; k <= radius; k++) sum += Get(from, width, rows, l, Mathf.Clamp(k, 0, length - 1));
                for (var k = 0; k < length; k++)
                {
                    Set(to, width, rows, l, k, sum / (2 * radius + 1));
                    sum += Get(from, width, rows, l, Mathf.Min(length - 1, k + radius + 1)) - Get(from, width, rows, l, Mathf.Max(0, k - radius));
                }
            }
        }

        private static float Get(float[] a, int width, bool rows, int line, int k) => rows ? a[line * width + k] : a[k * width + line];

        private static void Set(float[] a, int width, bool rows, int line, int k, float v)
        {
            if (rows) a[line * width + k] = v;
            else a[k * width + line] = v;
        }

        // ---- Shared ----

        private static Vector3[] World(GameObject thing, Mesh mesh, Vector3 up, out float low, out float high)
        {
            var vertices = mesh.vertices;
            var points = new Vector3[vertices.Length];
            low = float.MaxValue;
            high = float.MinValue;
            for (var i = 0; i < vertices.Length; i++)
            {
                points[i] = thing.transform.TransformPoint(vertices[i]);
                var h = Vector3.Dot(points[i], up);
                low = Mathf.Min(low, h);
                high = Mathf.Max(high, h);
            }
            return points;
        }

        private static void Extent(IEnumerable<Vector3> points, Vector3 x, Vector3 z, out Vector2 min, out Vector2 max)
        {
            min = new Vector2(float.MaxValue, float.MaxValue);
            max = new Vector2(float.MinValue, float.MinValue);
            foreach (var p in points)
            {
                var q = new Vector2(Vector3.Dot(p, x), Vector3.Dot(p, z));
                min = Vector2.Min(min, q);
                max = Vector2.Max(max, q);
            }
        }

        private static float Smooth(float t) => t * t * (3f - 2f * t);

        private static Texture2D Texture(float[] alpha, int width, int height)
        {
            var texture = new Texture2D(width, height, TextureFormat.RGBA32, false) { name = "ShipbreakerVR Shadow", wrapMode = TextureWrapMode.Clamp };
            var pixels = new Color32[alpha.Length];
            for (var i = 0; i < alpha.Length; i++) pixels[i] = new Color32(0, 0, 0, (byte)Mathf.RoundToInt(255f * Mathf.Clamp01(alpha[i])));
            texture.SetPixels32(pixels);
            texture.Apply(false, true);
            return texture;
        }

        // A thin flat patch lying on the surface, its middle at center, size along ax and az, built in the thing's own
        // axes (so its scale does not matter), drawn see-through black: what is under it shows darker. The texture's
        // rows run along az.
        private static void Lay(GameObject thing, Vector3 center, Vector3 ax, Vector3 az, Vector2 size, Texture2D texture)
        {
            var corners = new Vector3[4];
            for (var c = 0; c < 4; c++)
            {
                var sx = (c == 1 || c == 2) ? 0.5f : -0.5f;
                var sz = c >= 2 ? 0.5f : -0.5f;
                corners[c] = thing.transform.InverseTransformPoint(center + ax * (sx * size.x) + az * (sz * size.y));
            }
            Sweep(false);
            var quad = new Mesh { name = "ShipbreakerVR Shadow" };
            quad.vertices = corners;
            quad.uv = new[] { new Vector2(0f, 0f), new Vector2(1f, 0f), new Vector2(1f, 1f), new Vector2(0f, 1f) };
            // Facing up: seen from above, Unity's front faces run clockwise; with az = ax x up the corners 0, 1, 2, 3 run
            // anticlockwise there.
            var up = Vector3.Cross(az, ax);
            var anticlockwise = Vector3.Dot(Vector3.Cross(ax, az), up) < 0f;
            quad.triangles = anticlockwise ? new[] { 0, 2, 1, 0, 3, 2 } : new[] { 0, 1, 2, 0, 2, 3 };
            quad.RecalculateBounds();
            var shadow = new GameObject("ShipbreakerVR Shadow");
            shadow.layer = thing.layer;
            shadow.transform.SetParent(thing.transform, false);
            shadow.AddComponent<MeshFilter>().sharedMesh = quad;
            var drawn = shadow.AddComponent<MeshRenderer>();
            drawn.shadowCastingMode = ShadowCastingMode.Off;
            drawn.receiveShadows = false;
            drawn.lightProbeUsage = LightProbeUsage.Off;
            drawn.reflectionProbeUsage = ReflectionProbeUsage.Off;
            var material = new Material(Template()) { name = "ShipbreakerVR Shadow" };
            if (usesUnlit)
            {
                material.SetTexture("_UnlitColorMap", texture);
                material.SetTextureScale("_UnlitColorMap", Vector2.one);
                material.SetTextureOffset("_UnlitColorMap", Vector2.zero);
                material.SetColor("_UnlitColor", Color.white);
                if (material.HasProperty("_EmissiveColor")) material.SetColor("_EmissiveColor", Color.black);
                if (material.HasProperty("_EmissiveColorMap")) material.SetTexture("_EmissiveColorMap", null);
            }
            else
            {
                material.mainTexture = texture;
                material.color = Color.white;
                material.SetInt("unity_GUIZTestMode", (int)CompareFunction.LessEqual);
            }
            drawn.sharedMaterial = material;
            Patches.Add((shadow, new Object[] { quad, material, texture }));
        }

        // A see-through material that draws the patch as it is (black, its alpha the darkness): one of the game's own
        // transparent unlit HDRP materials blending by alpha, so the build has its shader; else the UI shader, which HDRP
        // also draws.
        // Looked for again whenever the one found before is gone: it belongs to a scene's assets and is unloaded with
        // them (0.32.0 looked once, and things set down later in the game got no shadow).
        private static Material Template()
        {
            if (template != null) return template;
            usesUnlit = false;
            foreach (var material in Resources.FindObjectsOfTypeAll<Material>())
            {
                if (material == null || material.shader == null || material.shader.name != "HDRP/Unlit") continue;
                if (!material.IsKeywordEnabled("_SURFACE_TYPE_TRANSPARENT")) continue;
                if (material.HasProperty("_BlendMode") && Mathf.RoundToInt(material.GetFloat("_BlendMode")) != 0) continue;
                if (material.IsKeywordEnabled("_ALPHATEST_ON") || !material.HasProperty("_UnlitColorMap")) continue;
                template = material;
                usesUnlit = true;
                if (material.GetTexture("_UnlitColorMap") != null) break;
            }
            if (template == null)
            {
                var shader = Shader.Find("UI/Default");
                if (shader != null) template = new Material(shader) { name = "ShipbreakerVR Shadow ui" };
            }
            if (template == null) Plugin.Log.LogWarning("Shadow: no see-through material found, things set down get no shadow");
            else Plugin.Log.LogInfo($"Shadow: drawn with '{template.name}' ({template.shader.name}, queue {template.renderQueue})");
            return template;
        }
    }
}
