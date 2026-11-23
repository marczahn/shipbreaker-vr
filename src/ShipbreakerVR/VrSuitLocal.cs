using System.Collections.Generic;
using System.Globalization;
using System.IO;
using BepInEx;
using UnityEngine;
using UnityEngine.Rendering.HighDefinition;

namespace ShipbreakerVR
{
    // Raik's Meshy suit (Neuer Anzug mit Armen, T pose: its body with the game's own arms) as the player's body in a
    // shift, the new body of 0.31.5, still a prototype (Raik, 2026-11-20). Its texture carries no LYNX logo; the earlier
    // suits that did are gone.
    //
    // Read from BepInEx\plugins\ShipbreakerVR\Anzug (the release), or from BepInEx\ShipbreakerVR-suit-local\Originalarme
    // where that is there (Raik's own test files go first); without the files nothing happens:
    //   Anzug-Koerper.suit, Anzug-Helm.suit  fitted onto the game's body (geo_suit) in Blender: knees, crotch and
    //   Anzug-Visier.suit                    shoulders at its heights, its skin weights carried over, in its mesh space;
    //                                        the visor apart from the helmet's shell, for its own polished material
    //   Anzug-Farbe.png, Anzug-Normal.png    colour (the gaps between the islands filled) and normal map (4096, the
    //                                        ripstop fabric baked in on the cloth)
    //   Anzug-Maske.png                      mask map (metal, occlusion, smoothness), see Material
    // The body draws instead of geo_suit's visible part (geo_suit stays and only stops drawing, so the game keeps
    // switching it as before and the suit follows). The helmet hangs on the head bone and, for the player wearing it,
    // only casts its shadow (the camera sits inside it); in multiplayer others would see it follow the head.
    internal static class VrSuitLocal
    {
        static VrSuitLocal()
        {
            VrSession.OnUnload("local suit", Release);
            // Switched in the VR options (Graphics): the suit is built again from the other folder.
            if (VrSettings.SuitQuality != null) VrSettings.SuitQuality.SettingChanged += (_, __) => Release();
        }

        // Raik 2026-11-20: only this suit is left; the earlier ones (Neuer Anzug 2 and 3 with the LYNX logo, and the one
        // with sleeves of its own on the game's gloves) are gone, with the shoulders that followed the arms. Aus: the
        // game's own body.
        // Originalarme (Raik, 2026-11-19): the body of the T-pose model without its arms, rigid at the chest, and the
        // game's arms as they are: their round cap at the shoulder sits in the body's side like a ball joint, so nothing
        // of the suit follows the arms (no Shoulders.Follow). Knees, feet and toes have joints of their own in the file.
        public enum Quality { Aus, Originalarme }

        private static bool ArmsAsTheyAre => Folder != null;

        // The folder with the suit's files, or null: none there, or the suit off (Aus).
        private static string Folder
        {
            get
            {
                if (VrSettings.SuitQuality == null || VrSettings.SuitQuality.Value == Quality.Aus) return null;
                var local = Path.Combine(Paths.BepInExRootPath, "ShipbreakerVR-suit-local", Quality.Originalarme.ToString());
                if (File.Exists(Path.Combine(local, "Anzug-Koerper.suit"))) return local;
                var shipped = Path.Combine(Paths.PluginPath, "ShipbreakerVR", "Anzug");
                return File.Exists(Path.Combine(shipped, "Anzug-Koerper.suit")) ? shipped : null;
            }
        }

        // The mannequin beside the kiosk and the mirror across from it: Raik's local test tools, not in a release.
        internal static bool Showroom;
        private const string BodyName = "geo_suit";

        private static bool failed;
        private static bool checkedFiles;
        private static Mesh bodyMesh;
        private static Mesh helmetMesh;
        private static Mesh visorMesh;
        private static Texture2D colour;
        private static Texture2D normal;
        private static Texture2D mask;
        private static Material material;
        private static Material visorMaterial;
        private static SkinnedMeshRenderer original;
        private static SkinnedMeshRenderer body;
        private static SkinnedMeshRenderer helmet;
        private static SkinnedMeshRenderer visor;
        private static float nextSearch;
        // geo_suit's bones and the arm bones it lacks (Build)
        private static Transform[] suitBones;
        // The body the meshes were built on (Build): their bones, the joints of the suit's own and the arms' caps are
        // that body's. A later shift brings a new player with new bones, and the suit is built again on them; kept, it
        // hung on the first shift's destroyed bones and did not draw (0.31.5: no suit from the second shift on).
        private static SkinnedMeshRenderer builtFor;
        // Originalarme: the game's arms draw a copy of their mesh with plain caps at the shoulders (PlainCaps)
        private static Mesh capsMesh;
        private static Mesh armsOwnMesh;
        private static SkinnedMeshRenderer cappedArms;

        public static void Update()
        {
            if (!VrXr.IsRunning)
            {
                Undress();
                return;
            }
            if (failed) return;
            if (!checkedFiles)
            {
                checkedFiles = true;
                if (Folder == null)
                {
                    failed = true;
                    return;
                }
            }
            if (body != null && original != null)
            {
                // The game shows and hides its body (geo_suit) itself; the suit does the same.
                original.forceRenderingOff = true;
                body.enabled = original.enabled;
                if (helmet != null) helmet.enabled = original.enabled;
                if (visor != null) visor.enabled = original.enabled;
                if (Showroom) Mannequin.Update(original);
                Backpack.Update(original, Showroom ? Mannequin.Root : null);
                return;
            }
            if (Time.unscaledTime < nextSearch) return;
            nextSearch = Time.unscaledTime + 1f;
            foreach (var found in Object.FindObjectsOfType<SkinnedMeshRenderer>())
            {
                if (found.name != BodyName || found.sharedMesh == null) continue;
                try
                {
                    if (bodyMesh != null && builtFor != found)
                    {
                        Plugin.Log.LogInfo("Local suit: a new body, the suit is built again on its bones");
                        Forget();
                    }
                    if (bodyMesh == null) Build(found);
                    Dress(found);
                }
                catch (System.Exception e)
                {
                    failed = true;
                    Undress();
                    Plugin.Log.LogWarning($"Local suit: {e}");
                }
                return;
            }
        }

        private static void Dress(SkinnedMeshRenderer found)
        {
            Undress();
            OwnJoints.Dress(found);
            body = Part(found, "ShipbreakerVR Local Suit", bodyMesh, Material(found));
            body.shadowCastingMode = found.shadowCastingMode;
            if (helmetMesh != null)
            {
                helmet = Part(found, "ShipbreakerVR Local Suit Helmet", helmetMesh, Material(found));
                helmet.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.ShadowsOnly;
            }
            if (visorMesh != null)
            {
                visor = Part(found, "ShipbreakerVR Local Suit Visor", visorMesh, VisorMaterial(found));
                visor.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.ShadowsOnly;
            }
            found.forceRenderingOff = true;
            original = found;
            if (capsMesh != null) ShowPlainCaps(found);
            Plugin.Log.LogInfo($"Local suit: dressed '{VrUtil.Path(found.transform)}' (enabled {found.enabled}, layer {found.gameObject.layer})");
        }

        private static SkinnedMeshRenderer Part(SkinnedMeshRenderer found, string name, Mesh mesh, Material look)
        {
            var go = new GameObject(name) { layer = found.gameObject.layer };
            go.transform.SetParent(found.transform.parent, false);
            go.transform.localPosition = found.transform.localPosition;
            go.transform.localRotation = found.transform.localRotation;
            go.transform.localScale = found.transform.localScale;
            var renderer = go.AddComponent<SkinnedMeshRenderer>();
            renderer.sharedMesh = mesh;
            renderer.bones = OwnJoints.Bones ?? suitBones ?? found.bones;
            renderer.rootBone = found.rootBone;
            renderer.sharedMaterial = look;
            renderer.renderingLayerMask = found.renderingLayerMask;
            renderer.updateWhenOffscreen = true;
            renderer.enabled = found.enabled;
            return renderer;
        }

        // The keywords a template may have: plain opaque HDRP/Lit with maps on the first UV set, nothing else.
        private static readonly HashSet<string> Allowed = new HashSet<string>
        {
            "_NORMALMAP", "_NORMALMAP_TANGENT_SPACE", "_MASKMAP", "_DISABLE_SSR", "_DISABLE_SSR_TRANSPARENT",
            "_DISABLE_DECALS", "_EMISSIVE_COLOR_MAP",
        };

        // The suit's material (Raik, 2026-11-16: textures blurred, belts melted into the cloth). The body's own material
        // (M_PlayerSuit001) has no normal or mask map, so its shader variant ignores them: the suit takes a copy of a
        // loaded HDRP/Lit material that uses normal and mask map, so that variant is in the game, and puts its own maps
        // in: colour, normal (the fabric baked in) and mask (metal, occlusion, smoothness). No material of the game uses
        // a detail map with HDRP/Lit (all bundles searched 2026-11-16), so that variant is not in the build and the fabric
        // went into the normal map. Without such a material the body's own one, its keywords switched on. The maps are
        // not compressed and filtered sharply at a slant.
        private static Material Material(SkinnedMeshRenderer found)
        {
            if (material != null) return material;
            colour = Image("Anzug-Farbe.png", false);
            normal = Image("Anzug-Normal.png", true);
            mask = Image("Anzug-Maske.png", true);
            var template = Template(out var candidates);
            var own = template == null;
            if (own) template = found.sharedMaterial;
            material = new Material(template) { name = "ShipbreakerVR local suit", hideFlags = HideFlags.HideAndDontSave };
            if (own)
                foreach (var keyword in new[] { "_NORMALMAP", "_NORMALMAP_TANGENT_SPACE", "_MASKMAP" })
                    material.EnableKeyword(keyword);
            Set(material, "_BaseColorMap", colour);
            Set(material, "_NormalMap", normal);
            Set(material, "_MaskMap", mask);
            if (material.HasProperty("_BaseColorMap"))
            {
                material.SetTextureScale("_BaseColorMap", Vector2.one);
                material.SetTextureOffset("_BaseColorMap", Vector2.zero);
            }
            foreach (var value in new[]
            {
                ("_NormalScale", 1f), ("_MetallicRemapMin", 0f), ("_MetallicRemapMax", 1f), ("_SmoothnessRemapMin", 0f),
                ("_SmoothnessRemapMax", 1f), ("_AORemapMin", 0f), ("_AORemapMax", 1f), ("_Metallic", 0f), ("_Smoothness", 0.5f),
                ("_UVBase", 0f), ("_EmissiveIntensity", 0f),
            })
                if (material.HasProperty(value.Item1)) material.SetFloat(value.Item1, value.Item2);
            if (material.HasProperty("_UVMappingMask")) material.SetVector("_UVMappingMask", new Vector4(1f, 0f, 0f, 0f));
            if (material.HasProperty("_BaseColor")) material.SetColor("_BaseColor", Color.white);
            if (material.HasProperty("_EmissiveColor")) material.SetColor("_EmissiveColor", Color.black);
            if (material.HasProperty("_EmissiveColorMap")) material.SetTexture("_EmissiveColorMap", null);
            Plugin.Log.LogInfo($"Local suit: material from '{template.name}' ({template.shader.name}, {(own ? "the body's own, keywords switched on" : $"one of {candidates} loaded with normal and mask map")}), keywords [{string.Join(" ", material.shaderKeywords)}], colour {Size(colour)}, normal {Size(normal)}, mask {Size(mask)}");
            return material;
        }

        // The visor (Raik, 2026-11-16: "matt", it should shine red-gold): a copy of the suit's material without maps
        // (white mask: metallic and smoothness are their remaps' upper ends), mostly metal and polished. A polished
        // metal shows what it reflects; on the mannequin a reflection probe of its own gives it the bay.
        // Raik, 2026-11-19: all of the glass a mirror of gold. The glass is all of it now (the suit's file); the colour
        // went from red-gold (1, 0.56, 0.24) to gold, a little more of a mirror.
        private static readonly Color VisorColour = new Color(1f, 0.70f, 0.28f);
        private const float VisorMetallic = 0.95f;
        private const float VisorSmoothness = 0.97f;

        private static Material VisorMaterial(SkinnedMeshRenderer found)
        {
            if (visorMaterial != null) return visorMaterial;
            visorMaterial = new Material(Material(found)) { name = "ShipbreakerVR local suit visor", hideFlags = HideFlags.HideAndDontSave };
            Set(visorMaterial, "_BaseColorMap", null);
            Set(visorMaterial, "_NormalMap", null);
            Set(visorMaterial, "_MaskMap", null);
            foreach (var value in new[]
            {
                ("_MetallicRemapMin", VisorMetallic), ("_MetallicRemapMax", VisorMetallic), ("_Metallic", VisorMetallic),
                ("_SmoothnessRemapMin", VisorSmoothness), ("_SmoothnessRemapMax", VisorSmoothness), ("_Smoothness", VisorSmoothness),
            })
                if (visorMaterial.HasProperty(value.Item1)) visorMaterial.SetFloat(value.Item1, value.Item2);
            if (visorMaterial.HasProperty("_BaseColor")) visorMaterial.SetColor("_BaseColor", VisorColour);
            Plugin.Log.LogInfo($"Local suit: visor material gold, metallic {VisorMetallic}, smoothness {VisorSmoothness}, keywords [{string.Join(" ", visorMaterial.shaderKeywords)}]");
            return visorMaterial;
        }

        // A loaded HDRP/Lit material that draws as the suit needs: opaque, one-sided, standard, maps on the first UV
        // set, normal and mask map, preferably with screen-space reflections (for the polished visor) and without an
        // emissive map; null when there is none.
        private static Material Template(out int candidates)
        {
            Material best = null;
            var bestScore = -1;
            candidates = 0;
            foreach (var m in Resources.FindObjectsOfTypeAll<Material>())
            {
                if (m == null || m.shader == null || m.shader.name != "HDRP/Lit") continue;
                var keywords = m.shaderKeywords;
                if (System.Array.IndexOf(keywords, "_NORMALMAP") < 0 || System.Array.IndexOf(keywords, "_MASKMAP") < 0
                    || System.Array.IndexOf(keywords, "_NORMALMAP_TANGENT_SPACE") < 0) continue;
                var plain = true;
                foreach (var k in keywords)
                    if (!Allowed.Contains(k)) plain = false;
                if (!plain || m.renderQueue >= 2450) continue;
                if (m.HasProperty("_SurfaceType") && m.GetFloat("_SurfaceType") > 0.5f) continue;
                if (m.HasProperty("_DoubleSidedEnable") && m.GetFloat("_DoubleSidedEnable") > 0.5f) continue;
                if (m.HasProperty("_MaterialID") && Mathf.Abs(m.GetFloat("_MaterialID") - 1f) > 0.01f) continue;
                candidates++;
                var score = System.Array.IndexOf(keywords, "_DISABLE_SSR") < 0 ? 2 : 0;
                if (System.Array.IndexOf(keywords, "_EMISSIVE_COLOR_MAP") < 0) score++;
                if (score > bestScore)
                {
                    best = m;
                    bestScore = score;
                }
            }
            return best;
        }

        private static void Set(Material m, string property, Texture texture)
        {
            if (m.HasProperty(property)) m.SetTexture(property, texture);
        }

        private static string Size(Texture2D t) => t == null ? "none" : $"{t.width}x{t.height}";

        private static Texture2D Image(string file, bool linear)
        {
            var path = Path.Combine(Folder, file);
            if (!File.Exists(path)) return null;
            var t = new Texture2D(2, 2, TextureFormat.RGBA32, true, linear) { name = "ShipbreakerVR local suit " + file, hideFlags = HideFlags.HideAndDontSave };
            if (!ImageConversion.LoadImage(t, File.ReadAllBytes(path), false))
            {
                Object.Destroy(t);
                return null;
            }
            t.wrapMode = TextureWrapMode.Clamp;
            t.filterMode = FilterMode.Trilinear;
            t.anisoLevel = 16;
            t.Apply(true, true);
            return t;
        }

        // Local look from outside (Raik, 2026-11-16): a still copy of the suit with its helmet, standing in its bind pose
        // beside the kiosk on the MasterJack ('PRF_Kiosk_MasterJack'), to see its shapes and textures. It stands 1.2 m to
        // the side of the kiosk, seen from where the player is when it is placed, faces the player, its feet on the
        // nearest thing a ray straight down finds there (the platform), else level with the kiosk's foot. Hung on the
        // kiosk, so it moves with it.
        private static class Mannequin
        {
            private const string KioskName = "PRF_Kiosk_MasterJack";
            private const float Aside = 1.2f;
            private static GameObject placed;
            public static Transform Root => placed != null ? placed.transform : null;
            private static float nextTry;
            private static HDAdditionalReflectionData probe;
            private static float probeAgainAt;

            public static void Update(SkinnedMeshRenderer found)
            {
                if (placed != null)
                {
                    // Once more when the bay has surely finished loading.
                    if (probe != null && probeAgainAt > 0f && Time.unscaledTime > probeAgainAt)
                    {
                        probeAgainAt = 0f;
                        probe.RequestRenderNextUpdate();
                    }
                    return;
                }
                if (Time.unscaledTime < nextTry) return;
                nextTry = Time.unscaledTime + 2f;
                var head = VrCamera.Instance != null && VrCamera.Instance.Camera != null ? VrCamera.Instance.Camera.transform : null;
                if (head == null) return;
                Transform kiosk = null;
                foreach (var t in Object.FindObjectsOfType<Transform>())
                    if (t.name.StartsWith(KioskName, System.StringComparison.Ordinal))
                    {
                        kiosk = t;
                        break;
                    }
                if (kiosk == null) return;
                var up = kiosk.up;
                var toPlayer = Vector3.ProjectOnPlane(head.position - kiosk.position, up);
                if (toPlayer.sqrMagnitude < 0.01f) toPlayer = Vector3.ProjectOnPlane(kiosk.forward, up);
                toPlayer.Normalize();
                var side = Vector3.Cross(up, toPlayer).normalized;
                var feet = kiosk.position + side * Aside + toPlayer * 0.3f;
                var floor = "nothing, level with the kiosk's foot";
                var from = feet + up * 1.5f;
                var nearest = float.MaxValue;
                foreach (var hit in Physics.RaycastAll(from, -up, 4f, ~0, QueryTriggerInteraction.Ignore))
                {
                    if (hit.distance >= nearest || hit.collider.GetComponentInParent<CharacterController>() != null) continue;
                    nearest = hit.distance;
                    feet = hit.point;
                    floor = $"'{VrUtil.Path(hit.collider.transform)}'";
                }
                placed = new GameObject("ShipbreakerVR Local Suit Mannequin") { layer = kiosk.gameObject.layer };
                // The suit's mesh space: y up, +z its front, its soles 5.5 cm below zero.
                placed.transform.SetPositionAndRotation(feet + up * 0.055f, Quaternion.LookRotation(toPlayer, up));
                placed.transform.SetParent(kiosk, true);
                var standIns = new Dictionary<string, Transform>();
                var arms = Arms(placed.transform, found, standIns);
                Body(placed.transform, found, standIns);
                if (helmetMesh != null) Part(placed.transform, helmetMesh, found, Material(found));
                if (visorMesh != null) Part(placed.transform, visorMesh, found, VisorMaterial(found));
                var reflection = Probe(placed.transform);
                Plugin.Log.LogInfo($"Local suit: mannequin beside '{VrUtil.Path(kiosk)}' (kiosk at {kiosk.position}, up {up}) at {feet}, feet on {floor}, facing the player; {arms}; {reflection}");
                Mirror.Place(kiosk, kiosk.position - side * Aside + toPlayer * 0.3f, up, head.position);
            }

            // A reflection probe of the mannequin's own, 60 cm in front of its visor, rendered once on demand (and once
            // more after a few seconds): the polished visor and the metal show the bay around it instead of the dark of
            // space. Its influence, a box of 3 x 4 x 3 m, holds the whole mannequin.
            private static string Probe(Transform parent)
            {
                try
                {
                    var go = new GameObject("ShipbreakerVR Local Suit Mannequin Reflection") { layer = parent.gameObject.layer };
                    go.transform.SetParent(parent, false);
                    go.transform.localPosition = new Vector3(0f, 1.65f, 0.6f);
                    if (go.GetComponent<ReflectionProbe>() == null) go.AddComponent<ReflectionProbe>();
                    probe = go.GetComponent<HDAdditionalReflectionData>();
                    if (probe == null) probe = go.AddComponent<HDAdditionalReflectionData>();
                    probe.mode = ProbeSettings.Mode.Realtime;
                    probe.realtimeMode = ProbeSettings.RealtimeMode.OnDemand;
                    probe.influenceVolume.shape = InfluenceShape.Box;
                    probe.influenceVolume.boxSize = new Vector3(3f, 4f, 3f);
                    probe.RequestRenderNextUpdate();
                    probeAgainAt = Time.unscaledTime + 5f;
                    return "reflection probe in front of the visor, rendered on demand";
                }
                catch (System.Exception e)
                {
                    probe = null;
                    return $"no reflection probe ({e.Message})";
                }
            }

            // The game's own arms (geo_arms, the same mesh space as geo_suit) on the mannequin in a T pose (Raik,
            // 2026-11-16: to see how they meet the suit's shoulders). Each of their bones gets a stand-in at its place in
            // the bind pose; each arm's bones are turned about its shoulder so that shoulder to hand points straight out.
            private static string Arms(Transform parent, SkinnedMeshRenderer found, Dictionary<string, Transform> standInsByName)
            {
                var source = FindArms(found);
                if (source == null) return "no arms found (geo_arms)";
                var mesh = source.sharedMesh;
                var binds = mesh.bindposes;
                var bones = source.bones;
                if (bones.Length != binds.Length) return $"arms left out: {bones.Length} bones, {binds.Length} bind poses";
                var at = new Dictionary<string, Vector3>();
                for (var i = 0; i < bones.Length; i++)
                    if (bones[i] != null) at[bones[i].name] = binds[i].inverse.GetColumn(3);
                Matrix4x4 Turn(string side, Vector3 outward)
                {
                    if (!at.TryGetValue($"rig_{side}_shoulder", out var shoulder) || !at.TryGetValue($"rig_{side}_hand", out var hand)) return Matrix4x4.identity;
                    var turn = Quaternion.FromToRotation(hand - shoulder, outward);
                    return Matrix4x4.Translate(shoulder) * Matrix4x4.Rotate(turn) * Matrix4x4.Translate(-shoulder);
                }
                var left = Turn("left", Vector3.left);
                var right = Turn("right", Vector3.right);
                var root = new GameObject("ShipbreakerVR Local Suit Mannequin Arms") { layer = parent.gameObject.layer };
                root.transform.SetParent(parent, false);
                var standIns = new Transform[bones.Length];
                for (var i = 0; i < bones.Length; i++)
                {
                    var name = bones[i] != null ? bones[i].name : "bone " + i;
                    var m = binds[i].inverse;
                    if (name.Contains("_left_")) m = left * m;
                    else if (name.Contains("_right_")) m = right * m;
                    var bone = new GameObject(name).transform;
                    bone.SetParent(root.transform, false);
                    bone.localPosition = m.GetColumn(3);
                    bone.localRotation = m.rotation;
                    bone.localScale = m.lossyScale;
                    standIns[i] = bone;
                    if (bones[i] != null) standInsByName[bones[i].name] = bone;
                }
                var skin = new GameObject("geo_arms") { layer = parent.gameObject.layer };
                skin.transform.SetParent(root.transform, false);
                var renderer = skin.AddComponent<SkinnedMeshRenderer>();
                renderer.sharedMesh = capsMesh != null ? capsMesh : mesh;
                renderer.bones = standIns;
                renderer.rootBone = root.transform;
                renderer.sharedMaterials = source.sharedMaterials;
                renderer.renderingLayerMask = source.renderingLayerMask;
                renderer.updateWhenOffscreen = true;
                return $"arms from '{VrUtil.Path(source.transform)}' in a T pose, {bones.Length} bones, materials {string.Join(", ", System.Array.ConvertAll(source.sharedMaterials, m => m != null ? m.name + " (" + m.shader.name + ")" : "-"))}";
            }

            // The body skinned on stand-ins: the arms' own where they have them (their T pose), the rest in the bind
            // pose; so its shoulders follow the arms as on the player.
            private static void Body(Transform parent, SkinnedMeshRenderer found, Dictionary<string, Transform> standIns)
            {
                var root = new GameObject("ShipbreakerVR Local Suit Mannequin Bones") { layer = parent.gameObject.layer };
                root.transform.SetParent(parent, false);
                var source = suitBones ?? found.bones;
                var binds = bodyMesh.bindposes;
                var bones = new Transform[source.Length];
                for (var i = 0; i < source.Length; i++)
                {
                    if (source[i] != null && standIns.TryGetValue(source[i].name, out var standIn))
                    {
                        bones[i] = standIn;
                        continue;
                    }
                    var m = binds[i].inverse;
                    var bone = new GameObject(source[i] != null ? source[i].name : "bone " + i).transform;
                    bone.SetParent(root.transform, false);
                    bone.localPosition = m.GetColumn(3);
                    bone.localRotation = m.rotation;
                    bone.localScale = m.lossyScale;
                    bones[i] = bone;
                }
                var go = new GameObject(bodyMesh.name) { layer = parent.gameObject.layer };
                go.transform.SetParent(parent, false);
                var renderer = go.AddComponent<SkinnedMeshRenderer>();
                renderer.sharedMesh = bodyMesh;
                renderer.bones = bones;
                renderer.rootBone = root.transform;
                renderer.sharedMaterial = Material(found);
                renderer.renderingLayerMask = found.renderingLayerMask;
                renderer.updateWhenOffscreen = true;
            }

            private static void Part(Transform parent, Mesh mesh, SkinnedMeshRenderer found, Material look)
            {
                var go = new GameObject(mesh.name) { layer = parent.gameObject.layer };
                go.transform.SetParent(parent, false);
                go.AddComponent<MeshFilter>().sharedMesh = mesh;
                var renderer = go.AddComponent<MeshRenderer>();
                renderer.sharedMaterial = look;
                renderer.renderingLayerMask = found.renderingLayerMask;
            }

            public static void Remove()
            {
                Mirror.Remove();
                if (placed != null) Object.Destroy(placed);
                placed = null;
                probe = null;
                probeAgainAt = 0f;
                nextTry = 0f;
            }
        }

        // The standard suit's backpack (Raik, 2026-11-16), the one on the suit in the Hab: its mesh and materials taken
        // from the game's prefab at run time (Addressables, the catalog has it on its own), never from a file of ours.
        // It hangs on the chest bone (rig_spine3) as on the Hab's suit: its centre 1.31 m up in the suit's mesh space
        // (the Hab's 1.34 m above the floor, our soles 5.5 cm below zero, the suit a little taller), its front 3 cm into
        // the suit's back, which is measured on the suit itself (the rearmost point within 12 cm of the middle, 25 cm
        // above and below the centre). On the mannequin only (a local test tool): on the player it sat under the
        // game's own thruster pack, which VrBackpack keeps on the chest since 0.31.0, so the player wore two packs one
        // inside the other (Raik's screenshot of 2026-11-22). The thruster pack is the one a shift has, and stays.
        private static class Backpack
        {
            private const string Key = "Assets/Content/Art/Structures/Kitbash/Hab/Models/PRF_Shipbreaker_BodysuitMesh_Backpack.prefab";
            private const float CentreHeight = 1.31f;
            private const float IntoBack = 0.03f;
            // The mesh's side that lies on the back faces its -z (Raik's test 2026-11-16: it hung the wrong way round),
            // so it is turned half round about the vertical.
            private static readonly Quaternion Turn = Quaternion.Euler(0f, 180f, 0f);
            private static Matrix4x4 Placement => Matrix4x4.TRS(place, Turn, Vector3.one);

            private static UnityEngine.ResourceManagement.AsyncOperations.AsyncOperationHandle<GameObject> handle;
            private static bool asked;
            private static bool failed;
            private static Mesh mesh;
            private static Material[] materials;
            private static Vector3 place;
            private static GameObject onMannequin;

            public static void Update(SkinnedMeshRenderer found, Transform mannequin)
            {
                // Without a mannequin nothing wears it, and its prefab is not loaded.
                if (failed || mannequin == null) return;
                if (mesh == null && !Ready()) return;
                if (onMannequin == null) onMannequin = Make(mannequin, Placement, found, "mannequin");
            }

            private static bool Ready()
            {
                if (!asked)
                {
                    asked = true;
                    try { handle = LoadByPath(Key); }
                    catch (System.Exception e) { return Fail($"not loaded ({e.Message})"); }
                }
                if (!handle.IsValid()) return Fail("has no load");
                if (!handle.IsDone) return false;
                if (handle.Status != UnityEngine.ResourceManagement.AsyncOperations.AsyncOperationStatus.Succeeded || handle.Result == null)
                    return Fail($"did not load ({handle.Status}{(handle.OperationException != null ? ": " + handle.OperationException.Message : "")})");
                var filter = handle.Result.GetComponentInChildren<MeshFilter>(true);
                var renderer = filter != null ? filter.GetComponent<MeshRenderer>() : null;
                if (filter == null || filter.sharedMesh == null || renderer == null) return Fail("has no mesh");
                mesh = filter.sharedMesh;
                materials = renderer.sharedMaterials;
                var back = 0f;
                var any = false;
                foreach (var v in bodyMesh.vertices)
                    if (Mathf.Abs(v.x) < 0.12f && Mathf.Abs(v.y - CentreHeight) < 0.25f && (!any || v.z < back))
                    {
                        back = v.z;
                        any = true;
                    }
                if (!any) back = -0.2f;
                place = new Vector3(0f, CentreHeight, back + IntoBack - mesh.bounds.extents.z) - Turn * mesh.bounds.center;
                Plugin.Log.LogInfo($"Local suit: backpack '{mesh.name}' ({mesh.bounds.size}, materials {string.Join(", ", System.Array.ConvertAll(materials, m => m != null ? m.name + " (" + m.shader.name + ")" : "-"))}), the suit's back at z {back:0.000}, placed at {place}");
                return true;
            }

            private static bool Fail(string why)
            {
                failed = true;
                Plugin.Log.LogWarning($"Local suit: the backpack {why}");
                return false;
            }

            private static GameObject Make(Transform parent, Matrix4x4 local, SkinnedMeshRenderer found, string who)
            {
                var go = new GameObject("ShipbreakerVR Local Suit Backpack") { layer = parent.gameObject.layer };
                go.transform.SetParent(parent, false);
                go.transform.localPosition = local.GetColumn(3);
                go.transform.localRotation = local.rotation;
                go.transform.localScale = local.lossyScale;
                go.AddComponent<MeshFilter>().sharedMesh = mesh;
                var renderer = go.AddComponent<MeshRenderer>();
                renderer.sharedMaterials = materials;
                renderer.renderingLayerMask = found.renderingLayerMask;
                Plugin.Log.LogInfo($"Local suit: backpack on the {who} ('{VrUtil.Path(parent)}')");
                return go;
            }

            // The catalog keeps its assets under keys of their own, not their paths (as the radio found).
            private static UnityEngine.ResourceManagement.AsyncOperations.AsyncOperationHandle<GameObject> LoadByPath(string path)
            {
                foreach (var locator in UnityEngine.AddressableAssets.Addressables.ResourceLocators)
                    foreach (var key in locator.Keys)
                    {
                        if (!locator.Locate(key, typeof(GameObject), out var locations)) continue;
                        foreach (var location in locations)
                            if (location.InternalId == path) return UnityEngine.AddressableAssets.Addressables.LoadAssetAsync<GameObject>(location);
                    }
                throw new System.Exception("not in the catalog");
            }

            public static void Remove()
            {
                if (onMannequin != null) Object.Destroy(onMannequin);
                onMannequin = null;
            }

            public static void Release()
            {
                Remove();
                if (handle.IsValid()) UnityEngine.AddressableAssets.Addressables.Release(handle);
                handle = default;
                mesh = null;
                materials = null;
                asked = failed = false;
            }
        }

        // The game's arms (geo_arms), beside geo_suit and on the same bones.
        private static SkinnedMeshRenderer FindArms(SkinnedMeshRenderer found)
        {
            if (found.transform.parent != null)
                foreach (var r in found.transform.parent.GetComponentsInChildren<SkinnedMeshRenderer>(true))
                    if (r.name == "geo_arms" && r.sharedMesh != null) return r;
            return null;
        }

        // After the game has posed the player (end of VrFrame's late steps): the suit's own joints follow.
        public static void LateUpdate()
        {
            if (body != null) OwnJoints.Update();
            if (Showroom) Mirror.Update();
        }

        // Raik 2026-11-17: a large mirror (1.5 m wide, 2.5 m high) on the kiosk's other side from the mannequin, to watch
        // the suit move on himself. A camera of its own at the head's mirror image looks through the mirror's frame into
        // the bay: its picture's edges are the frame's (an off-centre frustum), its near plane the mirror's plane (what
        // lies behind the mirror stays out). One picture from between the eyes, so the mirror shows it flat like a
        // screen. While it renders, the helmet is drawn too (on the player it only casts its shadow). Drawn only while
        // the head is in front of it and within 12 m.
        private static class Mirror
        {
            private const float Width = 1.5f;
            private const float Height = 2.5f;
            private const float Range = 12f;
            private static GameObject placed;
            private static Transform surface;
            private static Camera camera;
            private static RenderTexture picture;
            private static Material look;
            private static bool hooked;

            public static void Place(Transform kiosk, Vector3 at, Vector3 up, Vector3 head)
            {
                Remove();
                try
                {
                    var foot = at;
                    var nearest = float.MaxValue;
                    foreach (var hit in Physics.RaycastAll(at + up * 1.5f, -up, 4f, ~0, QueryTriggerInteraction.Ignore))
                    {
                        if (hit.distance >= nearest || hit.collider.GetComponentInParent<CharacterController>() != null) continue;
                        nearest = hit.distance;
                        foot = hit.point;
                    }
                    var facing = Vector3.ProjectOnPlane(head - foot, up);
                    if (facing.sqrMagnitude < 0.01f) facing = Vector3.ProjectOnPlane(kiosk.forward, up);
                    facing.Normalize();
                    placed = new GameObject("ShipbreakerVR Local Mirror") { layer = kiosk.gameObject.layer };
                    placed.transform.SetPositionAndRotation(foot + up * (0.02f + Height / 2f), Quaternion.LookRotation(-facing, up));
                    placed.transform.SetParent(kiosk, true);
                    // Unity's quad shows its face against its +z, to the player; its picture's left and right swapped (the
                    // camera behind the mirror sees the bay the other way round)
                    var quad = GameObject.CreatePrimitive(PrimitiveType.Quad);
                    Object.Destroy(quad.GetComponent<Collider>());
                    quad.name = "ShipbreakerVR Local Mirror Surface";
                    quad.layer = placed.layer;
                    quad.transform.SetParent(placed.transform, false);
                    quad.transform.localScale = new Vector3(Width, Height, 1f);
                    var filter = quad.GetComponent<MeshFilter>();
                    var mesh = Object.Instantiate(filter.sharedMesh);
                    mesh.hideFlags = HideFlags.HideAndDontSave;
                    var uv = mesh.uv;
                    for (var i = 0; i < uv.Length; i++) uv[i] = new Vector2(1f - uv[i].x, uv[i].y);
                    mesh.uv = uv;
                    filter.sharedMesh = mesh;
                    surface = quad.transform;
                    picture = new RenderTexture(900, 1500, 24, RenderTextureFormat.ARGBHalf) { name = "ShipbreakerVR local mirror" };
                    picture.Create();
                    var renderer = quad.GetComponent<MeshRenderer>();
                    string how;
                    renderer.sharedMaterial = look = Look(out how);
                    renderer.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
                    var eye = new GameObject("ShipbreakerVR Local Mirror Camera");
                    eye.transform.SetParent(placed.transform, false);
                    camera = eye.AddComponent<Camera>();
                    camera.targetTexture = picture;
                    camera.stereoTargetEye = StereoTargetEyeMask.None;
                    var hd = eye.AddComponent<HDAdditionalCameraData>();
                    hd.xrRendering = false;
                    hd.clearColorMode = HDAdditionalCameraData.ClearColorMode.Sky;
                    camera.enabled = false;
                    if (!hooked)
                    {
                        hooked = true;
                        UnityEngine.Rendering.RenderPipelineManager.beginCameraRendering += (_, c) => { if (c == camera) ShowHelmet(true); };
                        UnityEngine.Rendering.RenderPipelineManager.endCameraRendering += (_, c) => { if (c == camera) ShowHelmet(false); };
                    }
                    Plugin.Log.LogInfo($"Local suit: mirror {Width} x {Height} m beside '{VrUtil.Path(kiosk)}' at {foot}, facing {facing}; {how}");
                }
                catch (System.Exception e)
                {
                    Plugin.Log.LogWarning($"Local suit: no mirror ({e})");
                    Remove();
                }
            }

            private static void ShowHelmet(bool show)
            {
                var cast = show ? UnityEngine.Rendering.ShadowCastingMode.On : UnityEngine.Rendering.ShadowCastingMode.ShadowsOnly;
                if (helmet != null) helmet.shadowCastingMode = cast;
                if (visor != null) visor.shadowCastingMode = cast;
            }

            // Unlit, the picture as it is: a loaded HDRP/Unlit material copied if there is one (its variant is in the
            // build), else the shader itself, else UI/Default.
            private static Material Look(out string how)
            {
                foreach (var m in Resources.FindObjectsOfTypeAll<Material>())
                {
                    if (m == null || m.shader == null || m.shader.name != "HDRP/Unlit") continue;
                    var copy = new Material(m) { name = "ShipbreakerVR local mirror", hideFlags = HideFlags.HideAndDontSave };
                    foreach (var k in copy.shaderKeywords) copy.DisableKeyword(k);
                    copy.SetTexture("_UnlitColorMap", picture);
                    copy.SetTextureScale("_UnlitColorMap", Vector2.one);
                    copy.SetTextureOffset("_UnlitColorMap", Vector2.zero);
                    copy.SetColor("_UnlitColor", Color.white);
                    how = $"HDRP/Unlit from '{m.name}'";
                    return copy;
                }
                var shader = Shader.Find("HDRP/Unlit");
                if (shader != null)
                {
                    var made = new Material(shader) { name = "ShipbreakerVR local mirror", hideFlags = HideFlags.HideAndDontSave };
                    made.SetTexture("_UnlitColorMap", picture);
                    made.SetColor("_UnlitColor", Color.white);
                    how = "HDRP/Unlit (no loaded material to copy)";
                    return made;
                }
                var ui = new Material(Shader.Find("UI/Default")) { name = "ShipbreakerVR local mirror", mainTexture = picture, hideFlags = HideFlags.HideAndDontSave };
                how = "UI/Default (no HDRP/Unlit)";
                return ui;
            }

            public static void Update()
            {
                if (camera == null || surface == null) return;
                var head = VrCamera.Instance != null && VrCamera.Instance.Camera != null ? VrCamera.Instance.Camera.transform : null;
                if (head == null) return;
                var centre = surface.position;
                var normal = -surface.forward;    // to the player
                var right = surface.right;
                var up = surface.up;
                var eye = head.position;
                var ahead = Vector3.Dot(eye - centre, normal);
                camera.enabled = ahead > 0.05f && ahead < Range;
                if (!camera.enabled) return;
                var mirrored = eye - 2f * ahead * normal;
                // behind the mirror, looking through it into the bay: its right is the mirror's left
                camera.transform.SetPositionAndRotation(mirrored, Quaternion.LookRotation(normal, up));
                var camRight = -right;
                var toCentre = centre - mirrored;
                var near = ahead;
                var l = Vector3.Dot(toCentre, camRight) - Width / 2f;
                var r = l + Width;
                var b = Vector3.Dot(toCentre, up) - Height / 2f;
                var t = b + Height;
                const float far = 500f;
                camera.nearClipPlane = near;
                camera.farClipPlane = far;
                var cullingMask = VrCamera.Instance.Camera.cullingMask;
                camera.cullingMask = cullingMask;
                var p = new Matrix4x4();
                p[0, 0] = 2f * near / (r - l);
                p[0, 2] = (r + l) / (r - l);
                p[1, 1] = 2f * near / (t - b);
                p[1, 2] = (t + b) / (t - b);
                p[2, 2] = -(far + near) / (far - near);
                p[2, 3] = -2f * far * near / (far - near);
                p[3, 2] = -1f;
                camera.projectionMatrix = p;
            }

            public static void Remove()
            {
                if (camera != null) camera.targetTexture = null;
                if (placed != null) Object.Destroy(placed);
                if (picture != null) picture.Release();
                if (picture != null) Object.Destroy(picture);
                if (look != null) Object.Destroy(look);
                placed = null;
                surface = null;
                camera = null;
                picture = null;
                look = null;
            }
        }

        // Raik 2026-11-17, the suit keeps the model's own proportions: it is only moved so its shoulders sit on the
        // game's, and below the chest it bends at joints of its own (pelvis, spine, hips, knees, feet; the .suit file's
        // "joints"). Each is a bone of its own at the suit's joint: it turns as the game's bone does, and where it sits
        // follows from the chest (the game's spine3) down, so the pieces meet at the suit's joints. Chest, arms, neck
        // and head stay on the game's bones.
        // Raik 2026-11-17 (Prototyp 2): the game's skeleton sits too low for such a suit. Joints the game does not have
        // ("suit_..." in the .suit file, with the bone they sit on, the bone they turn as and how far): the head higher
        // on the chest, the collarbones at the neck, a shoulder joint on the sleeve's upper edge and a shoulder pad that
        // turns part of the way. Until the joints are in place such a bone stands in as the chest (spine3).
        internal struct JointSpec
        {
            public string Name;
            public Vector3 Rest;
            public string From;
            public string Turn;
            public float Fraction;
        }

        private static class OwnJoints
        {
            private class Joint
            {
                public int Index;
                public Vector3 Rest;
                public int From;
                public bool Up;
                public Transform Bone;
                public Vector3 Now;
                // a joint the game lacks: turns as `Turn`, the part `Fraction` of the way from the turn of `From`
                public bool Own;
                public string Name;
                public int Turn;
                public float Fraction;
                public Quaternion Q;
                public Matrix4x4 Lin;
            }

            // up the spine each joint follows from the one above it, the legs from the one they hang on
            private static readonly string[,] UpFrom = { { "rig_spine2", "rig_spine3" }, { "rig_spine1", "rig_spine2" }, { "rig_pelvis", "rig_spine1" } };

            private static List<Joint> joints;
            private static Dictionary<int, Joint> byIndex;
            private static Transform[] game;
            private static Matrix4x4[] gameBinds;
            private static Vector3[] gameRest;
            private static Quaternion[] gameTurn;
            private static GameObject root;
            public static Transform[] Bones { get; private set; }

            // The bind poses with the suit's joints in place of the game's (same turn, the suit's position).
            public static Matrix4x4[] Prepare(Dictionary<string, Vector3> read, List<JointSpec> specs, Transform[] bones, Dictionary<string, int> names, Matrix4x4[] binds)
            {
                game = bones;
                gameBinds = binds;
                gameRest = new Vector3[binds.Length];
                gameTurn = new Quaternion[binds.Length];
                for (var i = 0; i < binds.Length; i++)
                {
                    gameRest[i] = binds[i].inverse.GetColumn(3);
                    gameTurn[i] = binds[i].inverse.rotation;
                }
                joints = new List<Joint>();
                byIndex = new Dictionary<int, Joint>();
                for (var k = 0; k < UpFrom.GetLength(0); k++)
                    if (read.TryGetValue(UpFrom[k, 0], out var at) && names.TryGetValue(UpFrom[k, 0], out var i) && names.TryGetValue(UpFrom[k, 1], out var from))
                        Add(new Joint { Index = i, Rest = at, From = from, Up = true });
                var down = new List<Joint>();
                foreach (var pair in read)
                {
                    if (!names.TryGetValue(pair.Key, out var i) || byIndex.ContainsKey(i) || bones[i] == null) continue;
                    var parent = bones[i].parent;
                    while (parent != null && !names.ContainsKey(parent.name)) parent = parent.parent;
                    if (parent == null) continue;
                    down.Add(new Joint { Index = i, Rest = pair.Value, From = names[parent.name] });
                }
                down.Sort((a, b) => Depth(game[a.Index]).CompareTo(Depth(game[b.Index])));
                foreach (var j in down) Add(j);
                var extra = new List<string>();
                if (specs != null)
                    foreach (var s in specs)
                    {
                        if (!names.TryGetValue(s.Name, out var i) || !names.TryGetValue(s.From, out var from) || !names.TryGetValue(s.Turn, out var turn))
                        {
                            Plugin.Log.LogWarning($"Local suit: joint '{s.Name}' left out, a bone of '{s.From}', '{s.Turn}' is missing");
                            continue;
                        }
                        Add(new Joint { Index = i, Rest = s.Rest, From = from, Turn = turn, Fraction = s.Fraction, Own = true, Name = s.Name });
                        extra.Add($"{s.Name} ({s.Fraction:0.##} of {s.Turn})");
                    }
                var result = (Matrix4x4[])binds.Clone();
                var furthest = 0f;
                foreach (var j in joints)
                {
                    if (j.Own)
                    {
                        result[j.Index] = Matrix4x4.Translate(-j.Rest);
                        continue;
                    }
                    var r = binds[j.Index].inverse;
                    result[j.Index] = Matrix4x4.TRS(j.Rest, r.rotation, r.lossyScale).inverse;
                    furthest = Mathf.Max(furthest, Vector3.Distance(j.Rest, gameRest[j.Index]));
                }
                Plugin.Log.LogInfo($"Local suit: {joints.Count - extra.Count} joints of its own (pelvis, spine, legs), up to {furthest * 100f:F0} cm from the game's; "
                    + $"{extra.Count} the game lacks: {string.Join(", ", extra)}");
                return result;
            }

            // how far a bone has turned from the bind pose, in the world
            private static Quaternion TurnOf(int i)
            {
                if (byIndex.TryGetValue(i, out var j) && j.Own) return j.Q;
                return game[i].rotation * Quaternion.Inverse(gameTurn[i]);
            }

            private static void Add(Joint j)
            {
                joints.Add(j);
                byIndex[j.Index] = j;
            }

            private static int Depth(Transform t)
            {
                var d = 0;
                for (; t != null; t = t.parent) d++;
                return d;
            }

            public static void Dress(SkinnedMeshRenderer found)
            {
                Bones = null;
                if (joints == null || joints.Count == 0 || game == null) return;
                root = new GameObject("ShipbreakerVR Local Suit Joints");
                root.transform.SetParent(found.transform.parent, false);
                Bones = (Transform[])game.Clone();
                foreach (var j in joints)
                {
                    j.Bone = new GameObject(j.Own ? j.Name : game[j.Index].name + " (suit)").transform;
                    j.Bone.SetParent(root.transform, false);
                    Bones[j.Index] = j.Bone;
                }
                Update();
            }

            // mesh space to world: the suit's own joints about their own point, the game's bones as the game has them
            private static Matrix4x4 Deform(int i)
            {
                var m = game[i].localToWorldMatrix * gameBinds[i];
                if (!byIndex.TryGetValue(i, out var j)) return m;
                return Matrix4x4.Translate(j.Now) * (j.Own ? j.Lin : Linear(m)) * Matrix4x4.Translate(-j.Rest);
            }

            private static Matrix4x4 Linear(Matrix4x4 m)
            {
                m.SetColumn(3, new Vector4(0f, 0f, 0f, 1f));
                return m;
            }

            private static Vector3 RestOf(int i) => byIndex.TryGetValue(i, out var j) ? j.Rest : gameRest[i];

            public static void Update()
            {
                if (root == null) return;
                var scale = root.transform.lossyScale;
                foreach (var j in joints)
                {
                    var g = game[j.Index];
                    if (g == null || game[j.From] == null) continue;
                    if (j.Own)
                    {
                        // sits on its bone, turns part of the way from that bone's turn to the other's
                        if (game[j.Turn] == null) continue;
                        var on = Deform(j.From);
                        j.Now = on.MultiplyPoint3x4(j.Rest);
                        j.Q = Quaternion.Slerp(TurnOf(j.From), TurnOf(j.Turn), j.Fraction);
                        var size = on.lossyScale;
                        j.Lin = Matrix4x4.TRS(Vector3.zero, j.Q, size);
                        j.Bone.SetPositionAndRotation(j.Now, j.Q);
                        j.Bone.localScale = new Vector3(size.x / scale.x, size.y / scale.y, size.z / scale.z);
                        continue;
                    }
                    if (j.Up)
                    {
                        // the joint above stays where it is; this bone's piece reaches up to it, turned as the game's
                        var above = Deform(j.From).MultiplyPoint3x4(RestOf(j.From));
                        var turn = Linear(g.localToWorldMatrix * gameBinds[j.Index]);
                        j.Now = above - turn.MultiplyVector(RestOf(j.From) - j.Rest);
                    }
                    else j.Now = Deform(j.From).MultiplyPoint3x4(j.Rest);
                    j.Bone.SetPositionAndRotation(j.Now, g.rotation);
                    var s = g.lossyScale;
                    j.Bone.localScale = new Vector3(s.x / scale.x, s.y / scale.y, s.z / scale.z);
                }
            }

            public static void Remove()
            {
                if (root != null) Object.Destroy(root);
                root = null;
                Bones = null;
            }

            public static void Clear()
            {
                Remove();
                joints = null;
                byIndex = null;
                game = null;
                gameBinds = null;
                gameRest = null;
            }
        }

        // The bone names a .suit file uses (its header).
        private static List<string> BoneNames(string path)
        {
            var result = new List<string>();
            using (var reader = new StreamReader(path))
            {
                reader.ReadLine();
                var head = reader.ReadLine();
                if (head == null || !head.StartsWith("bones ")) return result;
                var n = int.Parse(head.Substring(6), CultureInfo.InvariantCulture);
                for (var i = 0; i < n; i++) result.Add(reader.ReadLine().Trim());
            }
            return result;
        }

        // The game's arms end at the shoulder in a round cap that the game's own body hides. Its faces have no place of
        // their own in the arms' texture (their UVs run across the whole picture), so beside a slimmer body they show the
        // wrist device and the buckles (Raik, 2026-11-19: the "arm balls" should be plain dark grey like the sleeves).
        // A copy of the arms' mesh in which the corners of those faces take their UV from the nearest corners of the
        // sleeve: the caps then show the sleeve's cloth. Those faces: pieces of the mesh that hang together (it is
        // split along its UV seams) and lie wholly within CapLength of the shoulder along the upper arm, 21 a side in
        // the game's mesh (measured 2026-11-19). Nothing of the game's files changes; the arms get their mesh back
        // when the suit comes off.
        private const float CapLength = 0.14f;
        private const float CapReach = 0.25f;
        private const float CapDrawnIn = 0.75f;

        private static Mesh PlainCaps(Mesh source, Transform[] bones)
        {
            if (!source.isReadable)
            {
                Plugin.Log.LogInfo("Local suit: the arms' mesh cannot be read, their caps stay as they are");
                return null;
            }
            var vertices = source.vertices;
            var uvs = source.uv;
            var binds = source.bindposes;
            if (uvs.Length != vertices.Length || bones.Length != binds.Length) return null;
            var at = new Dictionary<string, Vector3>();
            for (var i = 0; i < bones.Length; i++)
                if (bones[i] != null) at[bones[i].name] = binds[i].inverse.GetColumn(3);
            var piece = new int[vertices.Length];
            for (var i = 0; i < piece.Length; i++) piece[i] = i;
            int Find(int a)
            {
                while (piece[a] != a)
                {
                    piece[a] = piece[piece[a]];
                    a = piece[a];
                }
                return a;
            }
            var subMesh = new int[vertices.Length];
            for (var s = 0; s < source.subMeshCount; s++)
            {
                var tris = source.GetTriangles(s);
                for (var i = 0; i < tris.Length; i += 3)
                {
                    piece[Find(tris[i + 1])] = Find(tris[i]);
                    piece[Find(tris[i + 2])] = Find(tris[i]);
                    subMesh[tris[i]] = subMesh[tris[i + 1]] = subMesh[tris[i + 2]] = s;
                }
            }
            var plain = (Vector2[])uvs.Clone();
            var changed = 0;
            var caps = 0;
            foreach (var side in new[] { "left", "right" })
            {
                if (!at.TryGetValue($"rig_{side}_shoulder", out var shoulder) || !at.TryGetValue($"rig_{side}_elbow", out var elbow)) continue;
                var along = (elbow - shoulder).normalized;
                // a piece with a corner beyond the cap, or away from this shoulder, is not a cap
                var beyond = new HashSet<int>();
                for (var v = 0; v < vertices.Length; v++)
                {
                    var d = vertices[v] - shoulder;
                    if (d.sqrMagnitude > CapReach * CapReach || Vector3.Dot(d, along) > CapLength) beyond.Add(Find(v));
                }
                var sleeve = new List<int>();
                for (var v = 0; v < vertices.Length; v++)
                    if (beyond.Contains(Find(v)) && (vertices[v] - shoulder).sqrMagnitude <= CapReach * CapReach) sleeve.Add(v);
                if (sleeve.Count == 0) continue;
                var pieces = new HashSet<int>();
                var takes = new Dictionary<int, int>();
                for (var v = 0; v < vertices.Length; v++)
                {
                    var root = Find(v);
                    if (beyond.Contains(root)) continue;
                    var best = -1;
                    var bestDistance = float.MaxValue;
                    foreach (var from in sleeve)
                    {
                        if (subMesh[from] != subMesh[v]) continue;
                        var distance = (vertices[from] - vertices[v]).sqrMagnitude;
                        if (distance < bestDistance)
                        {
                            bestDistance = distance;
                            best = from;
                        }
                    }
                    if (best < 0) continue;
                    takes[v] = best;
                    pieces.Add(root);
                }
                if (takes.Count == 0) continue;
                // Those sleeve corners lie on the edge of the sleeve's piece of the texture, with other parts right
                // beside it. All are drawn in towards their middle, which lies inside the piece: plain dark grey
                // cloth in the game's texture (looked at 2026-11-19), a little of its folds left. Not all the way, or so
                // few texels cover the cap that the texture's blocks show.
                var middle = Vector2.zero;
                var edge = new HashSet<int>(takes.Values);
                foreach (var from in edge) middle += uvs[from];
                middle /= edge.Count;
                foreach (var take in takes) plain[take.Key] = Vector2.Lerp(uvs[take.Value], middle, CapDrawnIn);
                changed += takes.Count;
                caps += pieces.Count;
            }
            if (changed == 0)
            {
                Plugin.Log.LogInfo("Local suit: no caps found on the game's arms, they stay as they are");
                return null;
            }
            var copy = Object.Instantiate(source);
            copy.name = "ShipbreakerVR local suit arms";
            copy.hideFlags = HideFlags.HideAndDontSave;
            copy.uv = plain;
            Plugin.Log.LogInfo($"Local suit: the caps of the game's arms take the sleeves' cloth, {changed} corners in {caps} pieces");
            return copy;
        }

        private static void ShowPlainCaps(SkinnedMeshRenderer found)
        {
            cappedArms = FindArms(found);
            if (cappedArms == null) return;
            armsOwnMesh = cappedArms.sharedMesh;
            cappedArms.sharedMesh = capsMesh;
        }

        private static void Undress()
        {
            Backpack.Remove();
            Mannequin.Remove();
            OwnJoints.Remove();
            if (cappedArms != null && armsOwnMesh != null) cappedArms.sharedMesh = armsOwnMesh;
            cappedArms = null;
            armsOwnMesh = null;
            if (original != null) original.forceRenderingOff = false;
            original = null;
            if (body != null) Object.Destroy(body.gameObject);
            if (helmet != null) Object.Destroy(helmet.gameObject);
            if (visor != null) Object.Destroy(visor.gameObject);
            body = helmet = visor = null;
        }

        private static void Build(SkinnedMeshRenderer target)
        {
            // geo_suit's bones, then those of the game's arms it lacks (forearm twist, hand, fingers), for a suit with
            // sleeves of its own; their bind poses from geo_arms (the same mesh space)
            var names = new Dictionary<string, int>();
            var bones = new List<Transform>(target.bones);
            var binds = new List<Matrix4x4>(target.sharedMesh.bindposes);
            for (var i = 0; i < bones.Count; i++)
                if (bones[i] != null) names[bones[i].name] = i;
            var arms = FindArms(target);
            if (arms != null)
            {
                var armBones = arms.bones;
                var armBinds = arms.sharedMesh.bindposes;
                for (var i = 0; i < armBones.Length && i < armBinds.Length; i++)
                    if (armBones[i] != null && !names.ContainsKey(armBones[i].name))
                    {
                        names[armBones[i].name] = bones.Count;
                        bones.Add(armBones[i]);
                        binds.Add(armBinds[i]);
                    }
            }
            var bodyFile = Path.Combine(Folder, "Anzug-Koerper.suit");
            // the suit's joints the game lacks ("suit_..."): the chest stands in for them until OwnJoints places them
            if (names.TryGetValue("rig_spine3", out var chest))
                foreach (var name in BoneNames(bodyFile))
                    if (name.StartsWith("suit_") && !names.ContainsKey(name))
                    {
                        names[name] = bones.Count;
                        bones.Add(bones[chest]);
                        binds.Add(binds[chest]);
                    }
            suitBones = bones.ToArray();
            var bindposes = binds.ToArray();
            var joints = new Dictionary<string, Vector3>();
            var specs = new List<JointSpec>();
            bodyMesh = Read(bodyFile, names, bindposes, joints, specs);
            if (joints.Count > 0 || specs.Count > 0)
            {
                bindposes = OwnJoints.Prepare(joints, specs, suitBones, names, bindposes);
                bodyMesh.bindposes = bindposes;
            }
            if (ArmsAsTheyAre)
            {
                Plugin.Log.LogInfo("Local suit: the body stays rigid at the shoulders, the game's arms as they are");
                if (arms != null) capsMesh = PlainCaps(arms.sharedMesh, arms.bones);
            }
            var helmetFile = Path.Combine(Folder, "Anzug-Helm.suit");
            if (File.Exists(helmetFile)) helmetMesh = Read(helmetFile, names, bindposes);
            var visorFile = Path.Combine(Folder, "Anzug-Visier.suit");
            if (File.Exists(visorFile)) visorMesh = Read(visorFile, names, bindposes);
            builtFor = target;
            Plugin.Log.LogInfo($"Local suit: from '{Path.GetFileName(Folder)}', body {bodyMesh.vertexCount} vertices, helmet {(helmetMesh != null ? helmetMesh.vertexCount : 0)}, visor {(visorMesh != null ? visorMesh.vertexCount : 0)}, bounds {bodyMesh.bounds.min} to {bodyMesh.bounds.max}");
        }

        // A .suit file from tools of this test (Blender): a header line, "bones n" and their names, "verts n" and per
        // vertex position, normal, uv and four (bone, weight), "tris n" and the triangles; all in the body's mesh space.
        // Optionally after the triangles: "joints n" and per line a bone's name and the suit's own joint for it (OwnJoints).
        // A joint's line may go on with the bone it sits on, the bone it turns as and how far (a joint the game lacks).
        internal static Mesh Read(string path, Dictionary<string, int> boneIndex, Matrix4x4[] bindposes, Dictionary<string, Vector3> joints = null,
            List<JointSpec> specs = null)
        {
            var lines = File.ReadAllLines(path);
            var at = 0;
            if (!lines[at++].StartsWith("SHIPBREAKERVR-SUIT 1")) throw new InvalidDataException($"{path}: not a suit file");
            float F(string s) => float.Parse(s, CultureInfo.InvariantCulture);
            int Count(string what)
            {
                var p = lines[at++].Split(' ');
                if (p[0] != what) throw new InvalidDataException($"{path}: '{what}' expected, '{p[0]}' found");
                return int.Parse(p[1], CultureInfo.InvariantCulture);
            }
            var boneCount = Count("bones");
            var map = new int[boneCount];
            for (var i = 0; i < boneCount; i++)
            {
                var name = lines[at++].Trim();
                if (!boneIndex.TryGetValue(name, out map[i])) throw new InvalidDataException($"{path}: the body has no bone '{name}'");
            }
            var n = Count("verts");
            var positions = new List<Vector3>(n);
            var normals = new List<Vector3>(n);
            var uvs = new List<Vector2>(n);
            var weights = new BoneWeight[n];
            for (var i = 0; i < n; i++)
            {
                var p = lines[at++].Split(' ');
                positions.Add(new Vector3(F(p[0]), F(p[1]), F(p[2])));
                normals.Add(new Vector3(F(p[3]), F(p[4]), F(p[5])));
                uvs.Add(new Vector2(F(p[6]), F(p[7])));
                weights[i] = new BoneWeight
                {
                    boneIndex0 = map[int.Parse(p[8])], weight0 = F(p[9]),
                    boneIndex1 = map[int.Parse(p[10])], weight1 = F(p[11]),
                    boneIndex2 = map[int.Parse(p[12])], weight2 = F(p[13]),
                    boneIndex3 = map[int.Parse(p[14])], weight3 = F(p[15]),
                };
            }
            var t = Count("tris");
            var triangles = new int[t * 3];
            for (var i = 0; i < t; i++)
            {
                var p = lines[at++].Split(' ');
                triangles[3 * i] = int.Parse(p[0]);
                triangles[3 * i + 1] = int.Parse(p[1]);
                triangles[3 * i + 2] = int.Parse(p[2]);
            }
            if (joints != null && at < lines.Length && lines[at].StartsWith("joints "))
            {
                var j = Count("joints");
                for (var i = 0; i < j; i++)
                {
                    var p = lines[at++].Split(' ');
                    var where = new Vector3(F(p[1]), F(p[2]), F(p[3]));
                    if (p.Length >= 7)
                    {
                        if (specs != null) specs.Add(new JointSpec { Name = p[0], Rest = where, From = p[4], Turn = p[5], Fraction = F(p[6]) });
                    }
                    else joints[p[0]] = where;
                }
            }
            var mesh =new Mesh { name = "ShipbreakerVR local suit " + Path.GetFileNameWithoutExtension(path), hideFlags = HideFlags.HideAndDontSave };
            if (n > 65000) mesh.indexFormat = UnityEngine.Rendering.IndexFormat.UInt32;
            mesh.SetVertices(positions);
            mesh.SetNormals(normals);
            mesh.SetUVs(0, uvs);
            mesh.SetTriangles(triangles, 0);
            mesh.boneWeights = weights;
            mesh.bindposes = bindposes;
            mesh.RecalculateTangents();
            mesh.RecalculateBounds();
            return mesh;
        }

        // What was built on one body's bones goes; the materials and their textures do not depend on the body and stay.
        private static void Forget()
        {
            Undress();
            foreach (var o in new Object[] { bodyMesh, helmetMesh, visorMesh, capsMesh })
                if (o != null) Object.Destroy(o);
            bodyMesh = helmetMesh = visorMesh = capsMesh = null;
            OwnJoints.Clear();
            suitBones = null;
            builtFor = null;
        }

        private static void Release()
        {
            Forget();
            Backpack.Release();
            foreach (var o in new Object[] { material, visorMaterial, colour, normal, mask })
                if (o != null) Object.Destroy(o);
            material = visorMaterial = null;
            colour = normal = mask = null;
            failed = checkedFiles = false;
        }
    }
}
