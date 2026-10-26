using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using BepInEx;
using UnityEngine;

namespace ShipbreakerVR
{
    // Shift+F8: saves the model of the tool in the hand (cutter or grapple) for judging and retexturing it: per renderer
    // its mesh, materials and textures go into the log, the textures as PNG files and, where the mesh can be read, the
    // mesh as an OBJ file plus a picture of its UV layout over the base color, the triangles colored by the side of the
    // tool they face (right red, left blue, top/bottom green, front/back gray, in the equipment point's axes). The log
    // also gives the texture resolution per side (texels per centimeter of the model), which says whether the right side
    // has less texture than the left. Files go to BepInEx\ShipbreakerVR-tools\<time>-<tool>.
    internal static class VrToolExport
    {
        private const int LayoutSize = 2048;

        public static void Update()
        {
            if (!Input.GetKeyDown(KeyCode.F8) || !(Input.GetKey(KeyCode.LeftShift) || Input.GetKey(KeyCode.RightShift))) return;
            try
            {
                Export();
            }
            catch (Exception e)
            {
                Plugin.Log.LogWarning($"Tool export failed: {e}");
            }
        }

        private static void Export()
        {
            var point = VrToolModels.EquipmentPoint;
            var equipment = VrToolModels.Equipped;
            if (point == null || equipment == BBI.Unity.Game.EquipmentController.Equipment.None)
            {
                Plugin.Log.LogInfo("Tool export: no tool in the hand");
                return;
            }
            var folder = System.IO.Path.Combine(Paths.BepInExRootPath, "ShipbreakerVR-tools", $"{DateTime.Now:yyyyMMdd-HHmmss}-{equipment}");
            Directory.CreateDirectory(folder);
            var text = new StringBuilder();
            text.AppendLine($"Tool export: {equipment} on '{VrDiagnostics.Path(point)}' into {folder}");
            var saved = new Dictionary<int, string>();
            var index = 0;
            foreach (var renderer in point.GetComponentsInChildren<Renderer>(true))
            {
                Mesh mesh = null;
                if (renderer is SkinnedMeshRenderer skinned) mesh = skinned.sharedMesh;
                else if (renderer is MeshRenderer)
                {
                    var filter = renderer.GetComponent<MeshFilter>();
                    if (filter != null) mesh = filter.sharedMesh;
                }
                var name = $"{index++:00}-{Safe(renderer.name)}";
                text.AppendLine($"  '{VrDiagnostics.Path(renderer.transform)}' {renderer.GetType().Name}, {(renderer.enabled && renderer.gameObject.activeInHierarchy ? "shown" : "hidden")}, layer {renderer.gameObject.layer}");
                if (mesh == null)
                {
                    text.AppendLine("    no mesh");
                    continue;
                }
                text.AppendLine($"    mesh '{mesh.name}': {mesh.vertexCount} vertices, {mesh.subMeshCount} submesh(es), readable {mesh.isReadable}, bounds {mesh.bounds.size}");
                var materials = renderer.sharedMaterials;
                for (var m = 0; m < materials.Length; m++) DescribeMaterial(text, folder, saved, m, materials[m]);
                if (!mesh.isReadable)
                {
                    text.AppendLine("    mesh not readable: no OBJ, no UV layout");
                    continue;
                }
                WriteObj(System.IO.Path.Combine(folder, name + ".obj"), mesh);
                for (var s = 0; s < mesh.subMeshCount; s++)
                {
                    var material = s < materials.Length ? materials[s] : null;
                    var baseMap = BaseMap(material);
                    var sides = Sides(mesh, s, renderer.transform, point, baseMap, out var layout);
                    text.AppendLine($"    submesh {s} ('{(material != null ? material.name : "-")}'): {sides}");
                    var file = System.IO.Path.Combine(folder, $"{name}-uv{s}.png");
                    SaveLayout(file, layout, baseMap);
                }
            }
            Plugin.Log.LogInfo(text.ToString());
        }

        private static void DescribeMaterial(StringBuilder text, string folder, Dictionary<int, string> saved, int slot, Material material)
        {
            if (material == null)
            {
                text.AppendLine($"    material {slot}: none");
                return;
            }
            text.AppendLine($"    material {slot} '{material.name}', shader '{(material.shader != null ? material.shader.name : "-")}', keywords {string.Join(" ", material.shaderKeywords)}");
            var values = new List<string>();
            foreach (var property in new[] { "_Metallic", "_Smoothness", "_NormalScale", "_DetailAlbedoScale", "_DetailNormalScale", "_DetailSmoothnessScale" })
                if (material.HasProperty(property)) values.Add($"{property} {material.GetFloat(property):0.###}");
            foreach (var property in new[] { "_BaseColor", "_EmissiveColor" })
                if (material.HasProperty(property)) values.Add($"{property} {material.GetColor(property)}");
            foreach (var property in new[] { "_BaseColorMap_ST", "_DetailMap_ST" })
                if (material.HasProperty(property)) values.Add($"{property} {material.GetVector(property)}");
            if (values.Count > 0) text.AppendLine($"      {string.Join(", ", values)}");
            foreach (var property in material.GetTexturePropertyNames())
            {
                var texture = material.GetTexture(property);
                if (texture == null) continue;
                var id = texture.GetInstanceID();
                if (!saved.TryGetValue(id, out var file))
                {
                    file = Safe(texture.name) + ".png";
                    saved[id] = SaveTexture(System.IO.Path.Combine(folder, file), texture, Linear(property)) ? file : "not saved";
                    file = saved[id];
                }
                var format = texture is Texture2D t2 ? $"{t2.format}, {t2.mipmapCount} mips" : texture.GetType().Name;
                text.AppendLine($"      {property}: '{texture.name}' {texture.width}x{texture.height} ({format}), filter {texture.filterMode}, aniso {texture.anisoLevel} -> {file}");
            }
        }

        // Normal, mask and detail maps hold data, not colors; the base color is sRGB.
        private static bool Linear(string property) =>
            property.IndexOf("Normal", StringComparison.OrdinalIgnoreCase) >= 0
            || property.IndexOf("Mask", StringComparison.OrdinalIgnoreCase) >= 0
            || property.IndexOf("Detail", StringComparison.OrdinalIgnoreCase) >= 0
            || property.IndexOf("Bent", StringComparison.OrdinalIgnoreCase) >= 0;

        private static Texture BaseMap(Material material)
        {
            if (material == null) return null;
            foreach (var property in new[] { "_BaseColorMap", "_MainTex", "_BaseMap" })
                if (material.HasProperty(property) && material.GetTexture(property) != null) return material.GetTexture(property);
            return null;
        }

        // The game's textures are not readable from the CPU: copied through a render texture.
        private static bool SaveTexture(string file, Texture texture, bool linear)
        {
            if (texture.dimension != UnityEngine.Rendering.TextureDimension.Tex2D) return false;
            var copy = Read(texture, linear, texture.width, texture.height);
            if (copy == null) return false;
            File.WriteAllBytes(file, copy.EncodeToPNG());
            UnityEngine.Object.Destroy(copy);
            return true;
        }

        private static Texture2D Read(Texture texture, bool linear, int width, int height)
        {
            var target = RenderTexture.GetTemporary(width, height, 0, RenderTextureFormat.ARGB32,
                linear ? RenderTextureReadWrite.Linear : RenderTextureReadWrite.sRGB);
            var previous = RenderTexture.active;
            try
            {
                Graphics.Blit(texture, target);
                RenderTexture.active = target;
                var copy = new Texture2D(width, height, TextureFormat.RGBA32, false, linear);
                copy.ReadPixels(new Rect(0, 0, width, height), 0, 0);
                copy.Apply();
                return copy;
            }
            finally
            {
                RenderTexture.active = previous;
                RenderTexture.ReleaseTemporary(target);
            }
        }

        private enum Side { Right, Left, Up, Down, Front, Back }

        private static readonly Color[] SideColors =
        {
            new Color(1f, 0.25f, 0.2f), new Color(0.25f, 0.5f, 1f), new Color(0.3f, 1f, 0.3f),
            new Color(0.3f, 1f, 0.3f), new Color(0.7f, 0.7f, 0.7f), new Color(0.7f, 0.7f, 0.7f),
        };

        private struct Triangle
        {
            public Vector2 A, B, C;
            public Side Side;
        }

        // Per side of the tool (by the face normal in the equipment point's axes): model area, UV area and the texture
        // resolution that gives, plus the triangles for the layout picture.
        private static string Sides(Mesh mesh, int submesh, Transform from, Transform point, Texture baseMap, out List<Triangle> layout)
        {
            layout = new List<Triangle>();
            var vertices = mesh.vertices;
            var uvs = mesh.uv;
            var indices = mesh.GetTriangles(submesh);
            if (uvs.Length != vertices.Length) return "no UVs";
            var area = new double[6];
            var uvArea = new double[6];
            var count = new int[6];
            var toPoint = point.worldToLocalMatrix * from.localToWorldMatrix;
            for (var i = 0; i + 2 < indices.Length; i += 3)
            {
                var a = toPoint.MultiplyPoint3x4(vertices[indices[i]]);
                var b = toPoint.MultiplyPoint3x4(vertices[indices[i + 1]]);
                var c = toPoint.MultiplyPoint3x4(vertices[indices[i + 2]]);
                var cross = Vector3.Cross(b - a, c - a);
                var faceArea = cross.magnitude * 0.5;
                if (faceArea < 1e-12) continue;
                var side = SideOf(cross.normalized);
                var ua = uvs[indices[i]];
                var ub = uvs[indices[i + 1]];
                var uc = uvs[indices[i + 2]];
                area[(int)side] += faceArea;
                uvArea[(int)side] += Math.Abs((ub.x - ua.x) * (uc.y - ua.y) - (uc.x - ua.x) * (ub.y - ua.y)) * 0.5;
                count[(int)side]++;
                layout.Add(new Triangle { A = ua, B = ub, C = uc, Side = side });
            }
            var texels = baseMap != null ? (double)baseMap.width * baseMap.height : 0d;
            var parts = new List<string>();
            for (var s = 0; s < 6; s++)
            {
                if (count[s] == 0) continue;
                // In the equipment point's units (meters unless the point is scaled); compared between the sides.
                var density = area[s] > 0 && texels > 0 ? Math.Sqrt(uvArea[s] * texels / (area[s] * 1e4)) : 0d;
                parts.Add(string.Format(CultureInfo.InvariantCulture, "{0} {1} tris, {2:0.0} cm², {3:0.0}% of the texture, {4:0.0} texels/cm",
                    (Side)s, count[s], area[s] * 1e4, uvArea[s] * 100, density));
            }
            return string.Join("; ", parts);
        }

        private static Side SideOf(Vector3 normal)
        {
            var x = Mathf.Abs(normal.x);
            var y = Mathf.Abs(normal.y);
            var z = Mathf.Abs(normal.z);
            if (x >= y && x >= z) return normal.x > 0 ? Side.Right : Side.Left;
            if (y >= z) return normal.y > 0 ? Side.Up : Side.Down;
            return normal.z > 0 ? Side.Front : Side.Back;
        }

        // The UV triangles as outlines over the darkened base color (UVs wrapped into 0..1).
        private static void SaveLayout(string file, List<Triangle> layout, Texture baseMap)
        {
            var size = LayoutSize;
            Color[] pixels;
            if (baseMap != null)
            {
                var background = Read(baseMap, false, size, size);
                pixels = background.GetPixels();
                UnityEngine.Object.Destroy(background);
                for (var i = 0; i < pixels.Length; i++) pixels[i] = pixels[i] * 0.45f;
            }
            else
            {
                pixels = new Color[size * size];
                for (var i = 0; i < pixels.Length; i++) pixels[i] = Color.black;
            }
            foreach (var t in layout)
            {
                var color = SideColors[(int)t.Side];
                Line(pixels, size, t.A, t.B, color);
                Line(pixels, size, t.B, t.C, color);
                Line(pixels, size, t.C, t.A, color);
            }
            var image = new Texture2D(size, size, TextureFormat.RGBA32, false);
            image.SetPixels(pixels);
            image.Apply();
            File.WriteAllBytes(file, image.EncodeToPNG());
            UnityEngine.Object.Destroy(image);
        }

        private static void Line(Color[] pixels, int size, Vector2 from, Vector2 to, Color color)
        {
            var a = Wrap(from) * (size - 1);
            var b = a + (to - from) * (size - 1);
            var steps = Mathf.Clamp(Mathf.CeilToInt(Vector2.Distance(a, b)), 1, size * 4);
            for (var i = 0; i <= steps; i++)
            {
                var p = Vector2.Lerp(a, b, i / (float)steps);
                var x = ((int)p.x % size + size) % size;
                var y = ((int)p.y % size + size) % size;
                pixels[y * size + x] = color;
            }
        }

        private static Vector2 Wrap(Vector2 uv) => new Vector2(uv.x - Mathf.Floor(uv.x), uv.y - Mathf.Floor(uv.y));

        // Wavefront OBJ in the mesh's own space, one group per submesh; X is mirrored, as Unity is left-handed.
        private static void WriteObj(string file, Mesh mesh)
        {
            var text = new StringBuilder();
            var culture = CultureInfo.InvariantCulture;
            text.AppendLine($"# {mesh.name}, exported by ShipbreakerVR");
            foreach (var v in mesh.vertices) text.AppendLine(string.Format(culture, "v {0} {1} {2}", -v.x, v.y, v.z));
            var uvs = mesh.uv;
            foreach (var uv in uvs) text.AppendLine(string.Format(culture, "vt {0} {1}", uv.x, uv.y));
            var normals = mesh.normals;
            foreach (var n in normals) text.AppendLine(string.Format(culture, "vn {0} {1} {2}", -n.x, n.y, n.z));
            var hasUv = uvs.Length == mesh.vertexCount;
            var hasNormal = normals.Length == mesh.vertexCount;
            for (var s = 0; s < mesh.subMeshCount; s++)
            {
                text.AppendLine($"g submesh{s}");
                text.AppendLine($"usemtl submesh{s}");
                var indices = mesh.GetTriangles(s);
                // Mirroring X flips the winding, so the order is reversed.
                for (var i = 0; i + 2 < indices.Length; i += 3)
                    text.AppendLine($"f {Corner(indices[i + 2], hasUv, hasNormal)} {Corner(indices[i + 1], hasUv, hasNormal)} {Corner(indices[i], hasUv, hasNormal)}");
            }
            File.WriteAllText(file, text.ToString());
        }

        private static string Corner(int index, bool uv, bool normal)
        {
            var i = index + 1;
            if (uv && normal) return $"{i}/{i}/{i}";
            if (uv) return $"{i}/{i}";
            if (normal) return $"{i}//{i}";
            return i.ToString();
        }

        private static string Safe(string name)
        {
            foreach (var c in System.IO.Path.GetInvalidFileNameChars()) name = name.Replace(c, '_');
            return name.Length > 0 ? name : "unnamed";
        }
    }
}
