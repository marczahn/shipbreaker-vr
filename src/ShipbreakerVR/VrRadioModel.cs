using System.Collections.Generic;
using System.Globalization;
using UnityEngine;

namespace ShipbreakerVR
{
    // Collection (Raik 2026-11-15, "Sammelkampagne", in 0.32.0): the radio's model, Raik's own (made with his
    // Meshy subscription, the lettering made up). Its OBJ and colour texture are built into the DLL (Models/Radio.obj,
    // Models/Radio.png) and turned into a Unity mesh and material at run time, once.
    internal static class VrRadioModel
    {
        // The OBJ's units against metres: the cassette in its texture makes it 38 to 48 cm wide (Raik: "das sollte mit der
        // Größe helfen"), 0.45 makes it about 43 cm wide, 36 cm high with its handle, 16 cm deep.
        public const float Scale = 0.45f;
        // The antenna, 30 cm long and tilted 7° out (Raik 2026-11-16), reaches far above the rest: the body is everything
        // below this height in the OBJ's units (the handle's top is at 0.36, the old short antenna's at 0.405).
        private const float BodyTop = 0.41f;

        // The radio without its antenna, in the mesh's axes: for its collider and where it is centred.
        public static Bounds Body { get; private set; }

        private static Mesh mesh;
        private static Texture2D texture;
        private static bool failed;

        // The mesh: y up, its front towards +z, its bottom's middle at its origin, in metres.
        public static Mesh Mesh
        {
            get
            {
                if (mesh == null && !failed) Load();
                return mesh;
            }
        }

        // A material for it, made from one of the scene's lit materials (so it is lit as the things around it are), with
        // the radio's colours and no maps of the template's own.
        public static Material MaterialFrom(Material template)
        {
            var lit = LitTemplate();
            if (lit != null) template = lit;
            if (template == null || (texture == null && (failed || !LoadTexture()))) return null;
            var material = new Material(template) { name = "ShipbreakerVR Radio" };
            foreach (var map in new[] { "_NormalMap", "_MaskMap", "_DetailMap", "_EmissiveColorMap", "_BentNormalMap", "_HeightMap", "_CoatMaskMap" })
                if (material.HasProperty(map)) material.SetTexture(map, null);
            foreach (var keyword in new[] { "_NORMALMAP", "_MASKMAP", "_DETAIL_MAP", "_EMISSIVE_COLOR_MAP", "_BENTNORMALMAP", "_HEIGHTMAP", "_NORMALMAP_TANGENT_SPACE" })
                material.DisableKeyword(keyword);
            if (material.HasProperty("_BaseColorMap")) material.SetTexture("_BaseColorMap", texture);
            if (material.HasProperty("_MainTex")) material.SetTexture("_MainTex", texture);
            if (material.HasProperty("_BaseColor")) material.SetColor("_BaseColor", Color.white);
            if (material.HasProperty("_Metallic")) material.SetFloat("_Metallic", 0f);
            if (material.HasProperty("_Smoothness")) material.SetFloat("_Smoothness", 0.35f);
            if (material.HasProperty("_EmissiveColor")) material.SetColor("_EmissiveColor", Color.black);
            return material;
        }

        // A material of the plain HDRP lit shader from among those loaded, one with no wire of its own: the ship's parts
        // draw with a shader of the game's that has its own wire (blue edges, Raik 2026-11-16) and reads its colour map
        // otherwise (the radio came out green). Null when none is loaded, and the caller's template is used.
        private const string LitShader = "HDRP/Lit";
        private static Material lit;
        private static bool litLooked;

        public static Material LitTemplate()
        {
            if (lit != null || litLooked) return lit;
            litLooked = true;
            foreach (var material in Resources.FindObjectsOfTypeAll<Material>())
            {
                if (material == null || material.shader == null || material.shader.name != LitShader) continue;
                if (material.HasProperty("_V_WIRE_Color")) continue;
                // Nothing glowing, see-through or painted on (the first one found was an airlock's blue glow).
                var name = material.name.ToLowerInvariant();
                if (name.Contains("emissive") || name.Contains("glow") || name.Contains("glass") || name.Contains("decal")
                    || name.Contains("transparent") || name.Contains("light")) continue;
                // None of the mod's own: the local suit's was taken once, and is not there in a release.
                if (name.StartsWith("shipbreakervr")) continue;
                // Only a solid one: a see-through material draws the radio's back through its front (Raik 2026-11-16).
                if (!Opaque(material)) continue;
                lit = material;
                if (material.GetTexture("_BaseColorMap") != null) break;
            }
            if (lit == null)
            {
                var shader = Shader.Find(LitShader);
                if (shader != null) lit = new Material(shader) { name = "ShipbreakerVR Radio lit" };
            }
            Plugin.Log.LogInfo(lit != null ? $"Radio: drawn with shader '{lit.shader.name}' from material '{lit.name}' (queue {lit.renderQueue}, opaque {Opaque(lit)})" : $"Radio: no solid '{LitShader}' material found, the template's own shader is used");
            return lit;
        }

        // A solid surface of HDRP's lit shader: opaque, in the opaque queue, not cut out by alpha, one-sided.
        private static bool Opaque(Material material)
        {
            if (material.renderQueue >= 2450) return false;
            if (material.IsKeywordEnabled("_SURFACE_TYPE_TRANSPARENT") || material.IsKeywordEnabled("_ALPHATEST_ON")) return false;
            if (material.HasProperty("_SurfaceType") && material.GetFloat("_SurfaceType") > 0.5f) return false;
            if (material.HasProperty("_AlphaCutoffEnable") && material.GetFloat("_AlphaCutoffEnable") > 0.5f) return false;
            if (material.HasProperty("_DoubleSidedEnable") && material.GetFloat("_DoubleSidedEnable") > 0.5f) return false;
            return true;
        }

        private static byte[] Resource(string name)
        {
            using (var stream = typeof(VrRadioModel).Assembly.GetManifestResourceStream("Models/" + name))
            {
                if (stream == null) return null;
                var bytes = new byte[stream.Length];
                var read = 0;
                while (read < bytes.Length)
                {
                    var n = stream.Read(bytes, read, bytes.Length - read);
                    if (n <= 0) break;
                    read += n;
                }
                return bytes;
            }
        }

        private static bool LoadTexture()
        {
            var png = Resource("Radio.png");
            if (png == null)
            {
                failed = true;
                Plugin.Log.LogError("Radio: its texture is not in the DLL");
                return false;
            }
            texture = new Texture2D(2, 2, TextureFormat.RGB24, true) { name = "ShipbreakerVR Radio" };
            if (!ImageConversion.LoadImage(texture, png, true))
            {
                failed = true;
                Plugin.Log.LogError("Radio: its texture did not load");
                return false;
            }
            return true;
        }

        private static void Load()
        {
            var bytes = Resource("Radio.obj");
            if (bytes == null)
            {
                failed = true;
                Plugin.Log.LogError("Radio: its model is not in the DLL");
                return;
            }
            mesh = Parse(System.Text.Encoding.UTF8.GetString(bytes));
            if (mesh == null) failed = true;
            else Plugin.Log.LogInfo($"Radio: model of {mesh.vertexCount} vertices, {mesh.bounds.size.x:0.00} x {mesh.bounds.size.y:0.00} x {mesh.bounds.size.z:0.00} m");
        }

        // An OBJ of triangles (v, vt, vn, f a/b/c) as a Unity mesh: one vertex for each used combination, x mirrored for
        // Unity's left-handed axes (so the triangles turned the other way round), scaled, its bottom's middle at zero.
        internal static Mesh Parse(string obj)
        {
            var v = new List<Vector3>();
            var vt = new List<Vector2>();
            var vn = new List<Vector3>();
            var verts = new List<Vector3>();
            var uvs = new List<Vector2>();
            var normals = new List<Vector3>();
            var tris = new List<int>();
            var index = new Dictionary<string, int>();
            float F(string s) => float.Parse(s, CultureInfo.InvariantCulture);
            foreach (var raw in obj.Split('\n'))
            {
                var line = raw.Trim();
                if (line.Length < 2) continue;
                var p = line.Split((char[])null, System.StringSplitOptions.RemoveEmptyEntries);
                switch (p[0])
                {
                    case "v": v.Add(new Vector3(-F(p[1]), F(p[2]), F(p[3])) * Scale); break;
                    case "vt": vt.Add(new Vector2(F(p[1]), F(p[2]))); break;
                    case "vn": vn.Add(new Vector3(-F(p[1]), F(p[2]), F(p[3]))); break;
                    case "f":
                        var face = new List<int>();
                        for (var i = 1; i < p.Length; i++)
                        {
                            if (!index.TryGetValue(p[i], out var at))
                            {
                                var parts = p[i].Split('/');
                                at = verts.Count;
                                verts.Add(v[int.Parse(parts[0]) - 1]);
                                uvs.Add(parts.Length > 1 && parts[1].Length > 0 ? vt[int.Parse(parts[1]) - 1] : Vector2.zero);
                                normals.Add(parts.Length > 2 && parts[2].Length > 0 ? vn[int.Parse(parts[2]) - 1] : Vector3.zero);
                                index[p[i]] = at;
                            }
                            face.Add(at);
                        }
                        // A fan, wound the other way round for the mirrored x.
                        for (var i = 1; i + 1 < face.Count; i++)
                        {
                            tris.Add(face[0]);
                            tris.Add(face[i + 1]);
                            tris.Add(face[i]);
                        }
                        break;
                }
            }
            if (verts.Count == 0 || tris.Count == 0) return null;
            var lo = Vector3.one * float.MaxValue;
            var hi = Vector3.one * float.MinValue;
            foreach (var p in verts)
            {
                if (p.y > BodyTop * Scale) continue;
                lo = Vector3.Min(lo, p);
                hi = Vector3.Max(hi, p);
            }
            if (lo.x > hi.x) return null;
            var shift = new Vector3((lo.x + hi.x) / 2f, lo.y, (lo.z + hi.z) / 2f);
            for (var i = 0; i < verts.Count; i++) verts[i] -= shift;
            Body = new Bounds((lo + hi) / 2f - shift, hi - lo);
            var result = new Mesh { name = "ShipbreakerVR Radio" };
            if (verts.Count > 65000) result.indexFormat = UnityEngine.Rendering.IndexFormat.UInt32;
            result.SetVertices(verts);
            result.SetUVs(0, uvs);
            result.SetTriangles(tris, 0);
            if (vn.Count > 0) result.SetNormals(normals);
            else result.RecalculateNormals();
            result.RecalculateTangents();
            result.RecalculateBounds();
            return result;
        }
    }
}
