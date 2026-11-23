using System.Collections.Generic;
using HarmonyLib;
using BBI.Unity.Game;
using UnityEngine;

namespace ShipbreakerVR
{
    // The kiosk at the start of a shift (the vending machine on the MasterJack, 'PRF_Kiosk_MasterJack'). It has no lever
    // and no switch to take hold of, so it stays the one device used with A or X from where one looks, with its prompt,
    // as in the flat game (Raik, 2026-11-14): VrInteractHeld leaves the game's own pick for it, VrLeverPrompt its prompt.
    //
    // Its housing collides as a convex mesh of its own ('SO_MasterJax_VendingMachineHousing2'): that
    // mesh's lines follow the machine, but physics takes their convex hull, which fills its front in, so a hand
    // stopped in the air before it. A static one is made not convex here the first time a hand comes near (VrHandShape),
    // so hands, rays and the body meet the machine as drawn. Only with a readable mesh; Unity cannot cook another.
    internal static class VrKiosk
    {
        private static readonly Dictionary<int, bool> Known = new Dictionary<int, bool>();
        private static readonly Dictionary<int, bool> KnownFixture = new Dictionary<int, bool>();
        private static readonly HashSet<int> Fitted = new HashSet<int>();
        // The housings made not convex here, convex again when the mod is unloaded (F3, a reload).
        private static readonly List<MeshCollider> Unconvexed = new List<MeshCollider>();
        private static int logs;

        static VrKiosk()
        {
            VrSession.OnUnload("kiosk shape", () =>
            {
                foreach (var each in Unconvexed)
                    if (each != null) each.convex = true;
                Unconvexed.Clear();
            });
            VrSession.OnUnload("kiosk platform", () =>
            {
                foreach (var (collider, shape, lowered) in Lowered)
                {
                    if (collider != null && collider.sharedMesh == lowered) collider.sharedMesh = shape;
                    if (lowered != null) Object.Destroy(lowered);
                }
                Lowered.Clear();
                foreach (var each in Added)
                    if (each != null) Object.Destroy(each);
                Added.Clear();
                Fitted.Clear();
            });
        }

        // Whether this interactable belongs to the kiosk.
        public static bool Is(InteractableObject thing)
        {
            if (thing == null) return false;
            var id = thing.GetInstanceID();
            if (Known.TryGetValue(id, out var known)) return known;
            var kiosk = Within(thing.transform);
            Known[id] = kiosk;
            if (kiosk) Plugin.Log.LogInfo($"Kiosk: '{Path(thing.transform)}' is used with A or X from the view, with its prompt");
            return kiosk;
        }

        // Test (Raik, 2026-11-15: the exit door to end the shift has no switch to take hold of either, so it could not be
        // used at all): whatever belongs to the MasterJack itself, not to the ship being salvaged, is used as the kiosk
        // is, with A or X from the view and its prompt. The ship's controls, collectibles and breakers all lie under its
        // 'HierarchyParent' or belong to one of its structure parts; a fixture of the MasterJack does neither and does
        // not float freely. Its levers stay turned by hand.
        public static bool UsedFromView(InteractableObject thing)
        {
            if (thing == null) return false;
            if (Is(thing)) return true;
            var id = thing.GetInstanceID();
            if (KnownFixture.TryGetValue(id, out var known)) return known;
            var fixture = Fixture(thing);
            KnownFixture[id] = fixture;
            if (fixtureLogs++ < 40) Plugin.Log.LogInfo($"Interaction: '{FullPath(thing.transform)}' ({VrLever.TypeName(thing)}) "
                + (fixture ? "belongs to the MasterJack: used with A or X from the view, with its prompt" : "belongs to the ship: used only by hand"));
            return fixture;
        }

        private static int fixtureLogs;

        private static bool Fixture(InteractableObject thing)
        {
            if (thing.StructurePartParent != null) return false;
            // A lever stays turned by hand wherever it is.
            if (VrLever.Turnable(thing) || VrNacelle.Is(thing) || VrLever.TypeName(thing).StartsWith("Lever")) return false;
            for (var t = thing.transform; t != null; t = t.parent)
                if (t.name.StartsWith("HierarchyParent", System.StringComparison.Ordinal)) return false;
            var collider = thing.GetComponent<Collider>();
            var body = collider != null ? collider.attachedRigidbody : thing.GetComponentInParent<Rigidbody>();
            return body == null || body.isKinematic;
        }

        private static string FullPath(Transform t)
        {
            var path = t.name;
            for (var up = t.parent; up != null; up = up.parent) path = up.name + "/" + path;
            return path;
        }

        private static bool Within(Transform at)
        {
            for (var t = at; t != null; t = t.parent)
                if (t.name.IndexOf("Kiosk", System.StringComparison.OrdinalIgnoreCase) >= 0
                    || t.name.IndexOf("VendingMachine", System.StringComparison.OrdinalIgnoreCase) >= 0) return true;
            return false;
        }

        // The platform the kiosk stands on (Raik, 2026-11-19, testing 0.31.5: the rails stop the hand where they are drawn,
        // but over the floor it hovers): it collides as a shape of its own, 'SO_MasterJax_Antenna6_Collision' on
        // 'SO_MasterJax_Antenna1', whose flat floors lie above the floors drawn (its mesh 'SO_MasterJax_Antenna4', the
        // same object's, compared in the game's files): the two decks beside the kiosk 10 cm (6.07 m in the platform's own
        // axes, drawn at 5.97), the kiosk's own deck 15 cm (5.375, drawn 5.23), the walkway above 11 to 12 cm (8.086 and
        // 8.101, drawn 7.98), an edge 6 cm (6.03). Its slopes lie on the drawn ones within 2 cm.
        // So once a hand comes near, those floors' points are put down where they are drawn, on a copy of the shape the
        // collider takes instead: hands, rays and the body all meet the floor as drawn. Back as it was on unloading.
        private const string PlatformShape = "SO_MasterJax_Antenna6_Collision";
        private static readonly (float From, float To)[] PlatformFloors =
        {
            (6.07f, 5.97f), (6.03f, 5.965f), (8.086f, 7.98f), (8.101f, 7.98f), (5.375f, 5.23f),
        };
        private const float FloorMatch = 0.004f;
        private static readonly List<(MeshCollider Collider, Mesh Shape, Mesh Lowered)> Lowered = new List<(MeshCollider, Mesh, Mesh)>();

        private static void LowerPlatform(MeshCollider collider)
        {
            // Each collider is looked at once: asking a mesh for its name makes a new string every time, and this is
            // asked for every shape that is not convex near a hand, every frame (bug list B6).
            var shape = collider.sharedMesh;
            if (shape == null || !Fitted.Add(collider.GetInstanceID()) || shape.name != PlatformShape) return;
            // On the MasterJack's kinematic body (Raik, 2026-11-20: both hands still above and below the floor; the log
            // showed it left as it was since this was made, as any body counted as moving): only a body physics moves
            // leaves it be.
            var body = collider.attachedRigidbody;
            if ((body != null && !body.isKinematic) || !shape.isReadable)
            {
                Plugin.Log.LogInfo($"Kiosk: platform '{Path(collider.transform)}' left as it is ({(shape.isReadable ? "it moves freely" : "its shape is not readable")})");
                return;
            }
            var vertices = shape.vertices;
            var moved = 0;
            for (var i = 0; i < vertices.Length; i++)
                foreach (var (from, to) in PlatformFloors)
                    if (Mathf.Abs(vertices[i].y - from) < FloorMatch)
                    {
                        vertices[i].y = to;
                        moved++;
                        break;
                    }
            if (moved == 0)
            {
                Plugin.Log.LogInfo($"Kiosk: platform '{Path(collider.transform)}' has no floor at the heights known, left as it is");
                return;
            }
            var lowered = Object.Instantiate(shape);
            lowered.name = shape.name + " (ShipbreakerVR: floors as drawn)";
            lowered.vertices = vertices;
            lowered.RecalculateBounds();
            lowered.RecalculateNormals();
            collider.sharedMesh = lowered;
            Lowered.Add((collider, shape, lowered));
            var solids = AddFloors(collider);
            Plugin.Log.LogInfo($"Kiosk: platform '{Path(collider.transform)}' collides with its floors where they are drawn ({moved} of {vertices.Length} points lowered 6 to 15 cm), {solids} solid floor(s) under them{(body != null ? " (on a kinematic body)" : "")}");
        }

        // Not convex, the platform is only a skin to physics (Raik, 2026-11-20, testing 0.31.5: the hand lay on the shape
        // or went through it, even below the platform drawn). Under its floors, inside what is drawn, solid shapes a hand
        // cannot go into (in the platform's own axes, from the meshes compared as above): the kiosk's deck, a slab 26 cm
        // thick (drawn 4.97 to 5.23 m) with six corners; the two decks beside it and the walkway above, the tops of solid
        // blocks (drawn at 5.97 and 7.98), 25 cm deep each.
        private const float FloorDepth = 0.25f;
        private static readonly (Vector3 Low, Vector3 High)[] FloorBlocks =
        {
            (new Vector3(-2.671f, 5.97f - FloorDepth, -9.058f), new Vector3(2.817f, 5.97f, -2.49f)),
            (new Vector3(-2.671f, 5.97f - FloorDepth, 2.49f), new Vector3(2.817f, 5.97f, 9.058f)),
            (new Vector3(-3.793f, 7.98f - FloorDepth, -2.442f), new Vector3(7.873f, 7.98f, 2.442f)),
        };
        private static readonly Vector2[] DeckCorners =
        {
            new Vector2(-12.416f, -1.169f), new Vector2(-7.72f, -3.88f), new Vector2(-5.695f, -2.711f),
            new Vector2(-5.696f, 2.713f), new Vector2(-7.72f, 3.881f), new Vector2(-12.416f, 1.169f),
        };
        private const float DeckBottom = 4.97f, DeckTop = 5.23f;
        private static readonly List<Object> Added = new List<Object>();

        private static int AddFloors(MeshCollider platform)
        {
            var holder = new GameObject("ShipbreakerVR platform floors") { layer = platform.gameObject.layer };
            holder.transform.SetParent(platform.transform, false);
            Added.Add(holder);
            foreach (var (low, high) in FloorBlocks)
            {
                var box = holder.AddComponent<BoxCollider>();
                box.center = (low + high) * 0.5f;
                box.size = high - low;
                box.sharedMaterial = platform.sharedMaterial;
            }
            var count = DeckCorners.Length;
            var points = new Vector3[count * 2];
            for (var i = 0; i < count; i++)
            {
                points[i] = new Vector3(DeckCorners[i].x, DeckTop, DeckCorners[i].y);
                points[count + i] = new Vector3(DeckCorners[i].x, DeckBottom, DeckCorners[i].y);
            }
            var triangles = new List<int>();
            for (var i = 1; i + 1 < count; i++)
            {
                triangles.AddRange(new[] { 0, i + 1, i });
                triangles.AddRange(new[] { count, count + i, count + i + 1 });
            }
            for (var i = 0; i < count; i++)
            {
                var j = (i + 1) % count;
                triangles.AddRange(new[] { i, j, count + j, i, count + j, count + i });
            }
            var mesh = new Mesh { name = "ShipbreakerVR kiosk deck", vertices = points, triangles = triangles.ToArray() };
            mesh.RecalculateBounds();
            Added.Add(mesh);
            var deck = holder.AddComponent<MeshCollider>();
            deck.sharedMesh = mesh;
            deck.convex = true;
            deck.sharedMaterial = platform.sharedMaterial;
            return FloorBlocks.Length + 1;
        }

        // The kiosk's convex housing made not convex, once; the platform's floors lowered (LowerPlatform).
        public static void Fit(Collider collider)
        {
            if (!(collider is MeshCollider mesh)) return;
            if (!mesh.convex)
            {
                LowerPlatform(mesh);
                return;
            }
            if (!Fitted.Add(mesh.GetInstanceID())) return;
            if (!Within(mesh.transform)) return;
            var body = mesh.attachedRigidbody;
            var shape = mesh.sharedMesh;
            if (body != null && !body.isKinematic)
            {
                Log($"Kiosk: '{Path(mesh.transform)}' left convex, it moves freely");
                return;
            }
            if (shape == null || !shape.isReadable)
            {
                Log($"Kiosk: '{Path(mesh.transform)}' left convex, its mesh '{(shape != null ? shape.name : "-")}' is not readable");
                return;
            }
            var before = mesh.bounds.size;
            mesh.convex = false;
            Unconvexed.Add(mesh);
            Log($"Kiosk: '{Path(mesh.transform)}' collides as its mesh '{shape.name}' ({shape.vertexCount} vertices) instead of its convex hull, bounds {before * 100f} cm");
            StartField(mesh, shape);
        }

        // Test (Raik, 2026-11-14: a hand laid on the kiosk sank through it): not convex, the housing is only a skin to
        // physics, and a palm through it touches nothing any more. Its inside as a field (VrShapeField, as the tool's),
        // worked out once on a worker thread from the mesh, tells a hand in it from one in front of it and the way out;
        // how deep exactly, a ray from outside back onto the mesh along that way. In the housing's own axes, scaled.
        private sealed class Shape
        {
            public MeshCollider Collider;
            public volatile VrShapeField Field;
            public volatile string Failed;
            public float Started;
            public bool Logged;
        }

        private static readonly Dictionary<int, Shape> Shapes = new Dictionary<int, Shape>();
        private const float FieldMargin = 0.06f;
        private const int MostCells = 118;

        private static void StartField(MeshCollider collider, Mesh mesh)
        {
            var scale = collider.transform.lossyScale;
            var vertices = mesh.vertices;
            var triangles = mesh.triangles;
            if (triangles.Length < 3) return;
            var corners = new List<Vector3>(triangles.Length);
            foreach (var t in triangles) corners.Add(Vector3.Scale(vertices[t], scale));
            var bounds = new Bounds(corners[0], Vector3.zero);
            foreach (var p in corners) bounds.Encapsulate(p);
            var longest = Mathf.Max(bounds.size.x, bounds.size.y, bounds.size.z) + 2f * FieldMargin;
            var cell = Mathf.Max(0.02f, longest / MostCells);
            var entry = new Shape { Collider = collider, Started = Time.realtimeSinceStartup };
            Shapes[collider.GetInstanceID()] = entry;
            System.Threading.Tasks.Task.Run(() =>
            {
                try { entry.Field = new VrShapeField(corners, cell, FieldMargin, float.MinValue); }
                catch (System.Exception e) { entry.Failed = e.Message; }
            });
        }

        // Whether hands meet this collider by its field (Distance).
        public static bool HasField(Collider collider) => Shapes.Count > 0 && collider != null && Shapes.ContainsKey(collider.GetInstanceID());

        // How far a point lies from the kiosk's housing (negative inside) and the way out of it; false where that is not
        // known (no field yet, or the point off it).
        public static bool Distance(Collider collider, Vector3 point, out float distance, out Vector3 way)
        {
            distance = float.MaxValue;
            way = Vector3.zero;
            if (!Shapes.TryGetValue(collider.GetInstanceID(), out var entry) || entry.Collider == null) return false;
            var field = entry.Field;
            if (field == null)
            {
                if (entry.Failed != null && !entry.Logged)
                {
                    entry.Logged = true;
                    Log($"Kiosk: no field for '{Path(entry.Collider.transform)}': {entry.Failed}");
                }
                return false;
            }
            if (!entry.Logged)
            {
                entry.Logged = true;
                var size = field.Size;
                Log($"Kiosk: field for '{Path(entry.Collider.transform)}' ready after {(Time.realtimeSinceStartup - entry.Started) * 1000f:0} ms, {size.x}x{size.y}x{size.z} cells of {field.Cell * 100f:0.0} cm, {field.SolidCells} solid, solid bounds {field.SolidBounds.size * 100f} cm");
            }
            var t = entry.Collider.transform;
            var local = Quaternion.Inverse(t.rotation) * (point - t.position);
            var coarse = field.Distance(local);
            if (coarse == float.MaxValue) return false;
            var outward = field.Out(local);
            if (outward == Vector3.zero) return false;
            way = t.rotation * outward;
            distance = coarse;
            // Far off it, the field is enough; near it or in it, the mesh itself along that way.
            var cell = field.Cell;
            if (coarse > 2f * cell) return true;
            var reach = Mathf.Abs(coarse) + 2f * cell + 0.02f;
            if (entry.Collider.Raycast(new Ray(point + way * reach, -way), out var hit, 2f * reach))
            {
                distance = hit.distance - reach;
                if (Vector3.Dot(hit.normal, way) > 0.3f) way = hit.normal;
            }
            return true;
        }

        private static void Log(string text)
        {
            if (logs++ < 10) Plugin.Log.LogInfo(text);
        }

        private static string Path(Transform t) => t.parent != null ? $"{t.parent.name}/{t.name}" : t.name;
    }

    // Test (Raik, 2026-11-14): the kiosk's prompt shows and A uses it from 1.2 m further away. The game finds what to
    // offer with rays as long as m_MaxRaycastRange (InteractRaycastSystem.SetDistances, set once in Awake) and keeps
    // a hit only as far from the camera as mMaxInteractionDistanceSqr: m_MaxInteractionDistance squared, compared
    // with the plain distance (as the game has it), so that is the reach in meters. Both are longer only while
    // VrInteractHeld offers nothing else from afar (aiming with the hand, holding on), so it is the kiosk's alone.
    [HarmonyPatch(typeof(InteractionController), "UpdateClosestInteractable")]
    internal static class VrKioskReach
    {
        private const float Further = 1.2f;

        private static readonly AccessTools.FieldRef<InteractionController, float> Reach =
            AccessTools.FieldRefAccess<InteractionController, float>("mMaxInteractionDistanceSqr");
        private static readonly AccessTools.FieldRef<InteractionController, float> RayRange =
            AccessTools.FieldRefAccess<InteractionController, float>("m_MaxRaycastRange");
        private static readonly AccessTools.FieldRef<InteractionController, object> Rays =
            AccessTools.FieldRefAccess<InteractionController, object>("mRaycastSystem");

        private static InteractionController known;
        private static float gameReach;
        private static bool longer;

        static VrKioskReach()
        {
            VrSession.OnUnload("kiosk reach", () =>
            {
                if (known != null && longer) Set(known, false);
            });
        }

        private static void Prefix(InteractionController __instance)
        {
            if (__instance != known)
            {
                known = __instance;
                gameReach = Reach(__instance);
                longer = false;
            }
            var want = VrXr.IsRunning && VrAim.Active && VrSettings.Climbing != null && VrSettings.Climbing.Value;
            if (want == longer) return;
            Set(__instance, want);
        }

        private static void Set(InteractionController __instance, bool want)
        {
            longer = want;
            Reach(__instance) = gameReach + (want ? Further : 0f);
            var range = RayRange(__instance) + (want ? Further : 0f);
            var rays = Rays(__instance);
            var set = rays != null ? AccessTools.Method(rays.GetType(), "SetDistances", new[] { typeof(float), typeof(float) }) : null;
            set?.Invoke(rays, new object[] { 0f, range });
            Plugin.Log.LogInfo($"Kiosk: reach {(want ? "longer" : "as the game's")}: prompts within {Reach(__instance):0.00} m (game {gameReach:0.00} m), rays {range:0.00} m{(set == null ? ", ray length not found" : "")}");
        }
    }
}
