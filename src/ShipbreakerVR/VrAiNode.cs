using System.Collections.Generic;
using BBI.Unity.Game;
using BepInEx.Configuration;
using Carbon.Audio;
using HarmonyLib;
using Unity.Collections;
using Unity.Entities;
using UnityEngine;
using UnityEngine.Rendering.HighDefinition;
using UnityEngine.AddressableAssets;
using UnityEngine.ResourceManagement.AsyncOperations;

namespace ShipbreakerVR
{
    // Collection (Raik 2026-11-15, in 0.32.0): the AI nodes of the ghost ships
    // ('SM_Props_AI_Node_...', structure parts fixed on walls and cargo) come off with the grapple, and by hand as a
    // first aid kit does (VrPullOff). Held in a hand and let go over the left shoulder they go into the backpack like the
    // things to collect (VrShoulderStow): removed as the game removes a completed thing (PendingDestruction on its entity,
    // InteractableObject.DestroyObjectOnComplete).
    // A small collection: there are three kinds (Regular, LargeRound, LargeWide). Each kind collected the first time
    // lies in the Hab, for good, each kind at a place of its own (Lay); one of a kind already there adds nothing, as
    // with the pink bunny. The first one collected also brings LYNX's warning (VrAiNodeMail). All of it is kept in the
    // mod's own config (section Experimente), not in the save, so the save never changes.
    // The game counts AI nodes for the ghost ship's objectives (salvaged correctly or not): one in the backpack is gone
    // and counts for none. With Experimente/KiKnotenStrafe (Raik 2026-11-15) the game also fines it as it fines a node
    // thrown into the processor: it is marked destroyed by the processor (SalvageableUtility.MarkStructurePartAsDestroyed,
    // as ProcessorVolume.HandleNegativeSalvage does), and the game's own salvage accounting (SalvageableSystem, the part's
    // SalvagedObjectIncorrectAction) does the rest, shown when the shift ends. LYNX's warning still says 0.00 credits.
    // Its entity is removed only a moment later (Settle), so the accounting has seen it first.
    internal static class VrAiNode
    {
        private const string Name = "SM_Props_AI_Node";
        // The node of a kind: 'SM_Props_AI_Node_LargeWide' comes from 'PRF_Props_AI_Node_LargeWide.prefab'.
        private const string PrefabFolder = "Assets/Content/Prefabs/Objects/GhostShip/";
        private const string Regular = "SM_Props_AI_Node_Regular";
        // Where the first lies, in the room's axes (VrHabSurvey: x, height over the floor, z): in front of the loadout
        // computer's keyboard, right of it and back from the edge (Raik 2026-11-15: first at 132.45 121.80 it lay on the
        // front edge, 3 cm above the top). The bench top has no collider; its height is where the keyboard and the trays
        // on it start (0.96 and 0.97 m over the floor). Resting on that, the node sank into it (Raik 2026-11-15): 3 cm more.
        private static readonly Vector2 Spot = new Vector2(132.57f, 121.76f);
        private const float BenchTop = 1.0f;
        // The large round one: on the middle of the white storage tower's top (Raik's pictures 2026-11-15, the look
        // measured on 'BC_Props_Storage_Tower_1x1xC'). The long one: standing on the floor in the gap between the black
        // hard crate and the barrels, leaning on the crate's side towards them (Raik looked down there, at 133.2 118.3).
        // Both measured on their models in the room (Measure): the survey's boxes are too large, as the Hab stands turned
        // (the large one floated beside the crate's middle, the long one stood off the crate).
        private const string WhiteCrate = "PRF_Hab_Prop_StorageTower";
        private const string BlackCrate = "PRF_Hab_Crate_Hard";
        private const string Floor = "PRF_Hab_Rev_03/SM_Hab_Interior_Floor";
        private const float LeanAngle = 14f;
        private const float LeanTurn = 20f;
        // Standing at the workbench (Workstation_Virtual_Camera, heading 42° in the room): ahead, towards the wall.
        private static readonly Vector3 TowardsWall = new Vector3(0.67f, 0f, 0.74f);
        // Each turned a little, so they do not look set down by a machine.
        private static readonly float[] Turn = { 8f, -14f, 25f };

        private const float ReachedStrength = 0.25f;
        private const float ReachedTime = 0.04f;
        private const float OverStrength = 0.08f;
        private const float OverTime = 0.1f;

        // The kinds collected, in order, comma separated.
        public static ConfigEntry<string> Collection;
        public static ConfigEntry<int> Count;
        // LYNX's warning in the Hab's inbox (VrAiNodeMail), for the first one: 0 none yet, 1 waiting unread, 2 read.
        public static ConfigEntry<int> Warning;
        public static ConfigEntry<long> WarningRead;
        public static ConfigEntry<bool> Fine;
        public static ConfigEntry<float> GrappleTime;
        public static ConfigEntry<string> LargePlace;
        public static ConfigEntry<float> BenchLight;

        // Nodes put in the backpack, removed once the game's salvage accounting had a moment to see them destroyed.
        private const float Settle = 1f;
        private static readonly List<(GameObject Thing, float At)> Settling = new List<(GameObject, float)>();

        private class Held
        {
            public Rigidbody Body;
            public bool Over;
            public float OverSince;
        }

        // Nodes off the ship on a body the mod gave them, until the game made them a loose object of their own (Separate).
        private class Loose
        {
            public Rigidbody Body;
            public StructurePart Part;
            public float AskedAt = -1f;
        }

        private static readonly List<Loose> Loosened = new List<Loose>();
        private const float SplitWait = 3f;

        private static readonly Held Left = new Held();
        private static readonly Held Right = new Held();

        private static readonly Dictionary<string, AsyncOperationHandle<GameObject>> Prefabs = new Dictionary<string, AsyncOperationHandle<GameObject>>();
        private static readonly List<GameObject> Shown = new List<GameObject>();
        private static Transform shownIn;
        private static string shownCollection;
        private static string shownRaw;
        private static int logs;

        public static bool Is(Component part) => part != null && part.name.StartsWith(Name);

        public static void Bind()
        {
            if (Collection != null || VrSettings.File == null) return;
            Collection = VrSettings.Bind("Experimente", "KiKnotenSammlung", "",
                "Experiment: the kinds of AI node on the Hab's workbench (their parts' names), in the order collected, comma separated. Empty clears the workbench.");
            Warning = VrSettings.Bind("Experimente", "KiKnotenAbmahnung", 0,
                "Experiment: LYNX's warning for taking an AI node home. 0 not sent, 1 waiting unread in the Hab's inbox, 2 read.");
            WarningRead = VrSettings.Bind("Experimente", "KiKnotenAbmahnungGelesen", 0L,
                "Experiment: when LYNX's warning was read (Unix seconds), its place among LYNX's read messages.");
            Count = VrSettings.Bind("Experimente", "KiKnotenImRucksack", 0,
                "Experiment: how many AI nodes went into the backpack, shown in the inventory list.");
            GrappleTime = VrSettings.Bind("Experimente", "KiKnotenGreiferSekunden", 6f,
                "Experiment: how long the grapple has to hold on to an AI node fixed on a wall or crate before it comes off (seconds).");
            Fine = VrSettings.Bind("Experimente", "KiKnotenStrafe", true,
                "Experiment: an AI node put in the backpack is fined by the game as one thrown into the processor (at the end of the shift).");
            LargePlace = VrSettings.Bind("Experimente", "KiKnotenPlatzGross", "",
                "Experiment: where the large round AI node rests in the Hab, 'x; y; z' in the room's axes as a hand snapshot writes them (y above the floor), its bottom's middle. Empty: on the white crate's top as measured.");
            BenchLight = VrSettings.Bind("Experimente", "KiKnotenAufhellung", 40f,
                "Experiment: a small light above the AI node on the workbench, in lumen, as the lamps above the bench light the bench (0: none).");
        }

        // A place written 'x; y; z' (decimal point or comma), in the room's axes with y above the floor, in the world.
        private static bool TryPlace(ConfigEntry<string> entry, Transform room, float floor, out Vector3 place) =>
            TryPlace(entry != null ? entry.Value : null, room, floor, out place, out _);

        // The same with an optional fourth number: a turn about the room's up, in degrees (0 when left out).
        internal static bool TryPlace(string text, Transform room, float floor, out Vector3 place, out float turn)
        {
            place = default;
            turn = 0f;
            var parts = text != null ? text.Split(';') : new string[0];
            if (parts.Length != 3 && parts.Length != 4) return false;
            var v = new float[4];
            for (var i = 0; i < parts.Length; i++)
                if (!float.TryParse(parts[i].Trim().Replace(',', '.'), System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out v[i])) return false;
            place = room.TransformPoint(new Vector3(v[0], v[1] + floor, v[2]));
            turn = v[3];
            return true;
        }

        public static List<string> Kinds()
        {
            var kinds = new List<string>();
            if (Collection == null) return kinds;
            foreach (var kind in Collection.Value.Split(','))
            {
                var k = kind.Trim();
                if (k.StartsWith(Name) && !kinds.Contains(k)) kinds.Add(k);
            }
            return kinds;
        }

        // The three kinds, as their prefabs are named. The part in a ship is called 'SM_Props_AI_Node_Regular' whatever
        // its kind (Raik 2026-11-15: large and long ones collected, all logged as Regular), so the kind is told by its
        // drawn mesh and size, compared with the three prefabs (KindOf).
        private static readonly string[] AllKinds = { Regular, "SM_Props_AI_Node_LargeRound", "SM_Props_AI_Node_LargeWide" };
        private static bool kindsLogged;

        // The drawn mesh of a node: its own, or of its children the one with the most vertices (the nearest LOD).
        private static MeshFilter Drawn(GameObject node)
        {
            var own = node.GetComponent<MeshFilter>();
            if (own != null && own.sharedMesh != null) return own;
            MeshFilter best = null;
            foreach (var f in node.GetComponentsInChildren<MeshFilter>(true))
                if (f.sharedMesh != null && (best == null || f.sharedMesh.vertexCount > best.sharedMesh.vertexCount)) best = f;
            return best;
        }

        // Its size, the diagonal of its mesh's box as scaled in the world: the same however it is turned.
        private static float Size(MeshFilter filter) => Vector3.Scale(filter.sharedMesh.bounds.size, filter.transform.lossyScale).magnitude;

        private static void Preload()
        {
            foreach (var kind in AllKinds)
            {
                if (Prefabs.TryGetValue(kind, out var handle) && handle.IsValid()) continue;
                try { Prefabs[kind] = Addressables.LoadAssetAsync<GameObject>(PrefabKey(kind)); }
                catch (System.Exception e) { Plugin.Log.LogWarning($"AI node: '{PrefabKey(kind)}' did not load ({e.Message})"); }
            }
        }

        // The kind whose prefab has the same mesh and the nearest size; without the prefabs loaded, Regular.
        private static string KindOf(GameObject thing, out string seen)
        {
            var filter = Drawn(thing);
            seen = filter != null ? $"mesh '{filter.sharedMesh.name}' {filter.sharedMesh.vertexCount} vertices, {Size(filter):0.00} m" : "no mesh";
            if (filter == null) return Regular;
            var size = Size(filter);
            string best = null;
            var bestScore = float.MaxValue;
            var known = new List<string>();
            foreach (var kind in AllKinds)
            {
                if (!Prefabs.TryGetValue(kind, out var handle) || !handle.IsValid() || !handle.IsDone || handle.Result == null) continue;
                var other = Drawn(handle.Result);
                if (other == null) continue;
                var otherSize = Size(other);
                known.Add($"{kind.Substring(Name.Length + 1)}: '{other.sharedMesh.name}' {other.sharedMesh.vertexCount} vertices, {otherSize:0.00} m");
                var score = Mathf.Abs(otherSize - size) + (other.sharedMesh.name == filter.sharedMesh.name ? 0f : 10f);
                if (score < bestScore)
                {
                    bestScore = score;
                    best = kind;
                }
            }
            if (!kindsLogged && known.Count > 0)
            {
                kindsLogged = true;
                Plugin.Log.LogInfo($"AI node: the kinds are {string.Join("; ", known)}");
            }
            return best ?? Regular;
        }

        private static string PrefabKey(string kind) => PrefabFolder + "PRF_" + kind.Substring(3) + ".prefab";

        // From VrFrame, once a frame, after the shoulder stow.
        public static void Update()
        {
            Bind();
            Remove();
            if (!VrXr.IsRunning) return;
            Preload();
            Bench();
            Grapple();
            if (VrSettings.ShoulderStow == null || !VrSettings.ShoulderStow.Value || VrSettings.Climbing == null || !VrSettings.Climbing.Value)
            {
                Left.Body = Right.Body = null;
                return;
            }
            Check(VrInput.Left, Left);
            Check(VrInput.Right, Right);
            Separate();
        }

        // The grapple on a node still fixed on a wall or crate: held on it long enough, it comes off (Raik 2026-11-15: at
        // once was far too quick, the cutter takes a while to dissolve one too). Off it comes the way the game lets a
        // part go whose joints broke (Separate): a loose object of its own, which the grapple then holds as any small
        // thing (GrapplingHook.OnHierarchyJointReparented), pulled in like the others. The grapple's hand buzzes, the
        // harder the nearer it is to coming off.
        private static StructurePart grappled;
        private static float grappledSince;

        private static void Grapple()
        {
            var hook = GameRefs.Grapple;
            var part = hook != null ? hook.GrappledStructurePart : null;
            var body = hook != null ? hook.GrappledRigidbody : null;
            if (!Is(part) || body == null || body.transform == part.transform || OwnObject(body, part) || !part.gameObject.activeInHierarchy)
            {
                grappled = null;
                return;
            }
            if (part != grappled)
            {
                grappled = part;
                grappledSince = Time.time;
                if (logs++ < 40) Plugin.Log.LogInfo($"AI node: grapple on '{part.name}' on '{body.name}', comes off in {GrappleTime.Value:0.0} s");
            }
            var held = Time.time - grappledSince;
            var hand = VrHand.GameToolHand.Input;
            if (held < GrappleTime.Value)
            {
                hand.Buzz(0.05f + 0.35f * held / Mathf.Max(0.1f, GrappleTime.Value), 0.05f);
                return;
            }
            grappled = null;
            var root = part.transform;
            var head = VrCamera.Instance != null ? VrCamera.Instance.transform.position : root.position + root.forward;
            var normal = (head - root.position).sqrMagnitude > 1e-6f ? (head - root.position).normalized : root.forward;
            VrPullOff.Break(root, body, hook.GrappledRigidbodyHitPoint, normal);
            hand.Buzz(0.9f, 0.12f);
            if (SplitOff(part))
                Plugin.Log.LogInfo($"AI node: '{part.name}' comes off '{body.name}' after {held:0.0} s on the grapple");
        }

        // Its joints to the ship removed: the game makes it a loose object of its own, in a hierarchy parent of its own.
        private static bool SplitOff(StructurePart part)
        {
            var blueprint = part.EntityBlueprintComponent;
            if (!EntityBlueprintComponent.IsValid(blueprint)) return false;
            var commands = new EntityCommandBuffer(Allocator.TempJob);
            try
            {
                Main.EventSystem.Post(RemoveHierarchyJointsEvent.GetEvent(part, commands));
                commands.Playback(blueprint.EntityManager);
                return true;
            }
            catch (System.Exception e)
            {
                Plugin.Log.LogWarning($"AI node: separating '{part.name}' from its ship failed: {e.Message}");
                return false;
            }
            finally { commands.Dispose(); }
        }

        // A node's body: its own (the mod's, just off the ship) or the game's loose object holding it alone.
        private static StructurePart NodeOf(Rigidbody body)
        {
            if (body == null || !body.gameObject.activeInHierarchy) return null;
            var part = body.GetComponent<StructurePart>();
            if (Is(part)) return part;
            var parts = body.GetComponentsInChildren<StructurePart>();
            return parts.Length == 1 && Is(parts[0]) ? parts[0] : null;
        }

        // The game's own loose object of this node alone.
        private static bool OwnObject(Rigidbody body, StructurePart part) =>
            part.transform.parent == body.transform && body.GetComponentsInChildren<StructurePart>().Length == 1;

        private static void Track(Rigidbody body, StructurePart part)
        {
            foreach (var loose in Loosened) if (loose.Body == body) return;
            Loosened.Add(new Loose { Body = body, Part = part });
        }

        // Off the ship, the node still belongs to it for the game: its place in the ship's structure (the graph of
        // parts joined) and the ship's hierarchy (HierarchyMember). Thrown into the barge, the whole ship lit up red
        // (Raik 2026-11-15). Pulled off by hand, once no hand holds it any more, its joints in the structure are removed as when the game
        // breaks a part's joints (RemoveHierarchyJointsEvent, BreakableJointComponent): the game makes it a loose object
        // of its own, in a hierarchy parent of its own, which a grapple on it follows (GrapplingHook
        // .OnHierarchyJointReparented). The mod's body on it then gives way to the parent's, moving on as it moved.
        private static void Separate()
        {
            for (var i = Loosened.Count - 1; i >= 0; i--)
            {
                var loose = Loosened[i];
                if (loose.Body == null || loose.Part == null)
                {
                    Loosened.RemoveAt(i);
                    continue;
                }
                if (loose.AskedAt < 0f)
                {
                    if (VrClimb.HeldBody(VrInput.Left) == loose.Body || VrClimb.HeldBody(VrInput.Right) == loose.Body || VrPullOff.Busy(loose.Body)) continue;
                    if (!SplitOff(loose.Part))
                    {
                        Loosened.RemoveAt(i);
                        continue;
                    }
                    loose.AskedAt = Time.time;
                    if (logs++ < 40) Plugin.Log.LogInfo($"AI node: '{loose.Part.name}' let go, its joints to the ship removed");
                    continue;
                }
                var parent = loose.Part.transform.parent;
                var parentBody = parent != null ? parent.GetComponent<Rigidbody>() : null;
                if (parentBody != null && parentBody != loose.Body)
                {
                    parentBody.velocity = loose.Body.velocity;
                    parentBody.angularVelocity = loose.Body.angularVelocity;
                    Object.Destroy(loose.Body);
                    Loosened.RemoveAt(i);
                    if (logs++ < 40)
                        Plugin.Log.LogInfo($"AI node: '{loose.Part.name}' is a loose object of its own now, in '{parent.name}' ({parentBody.mass:0.0} kg), after {Time.time - loose.AskedAt:0.00} s");
                    continue;
                }
                if (Time.time > loose.AskedAt + SplitWait)
                {
                    Plugin.Log.LogWarning($"AI node: '{loose.Part.name}' got no hierarchy of its own from the game within {SplitWait:0} s, stays on the mod's body");
                    Loosened.RemoveAt(i);
                }
            }
        }

        private static void Check(VrInput.Hand hand, Held held)
        {
            var body = VrClimb.HeldBody(hand);
            var node = NodeOf(body);
            if (node != null)
            {
                // Pulled off by hand (VrPullOff): on the mod's body, to be separated from its ship once let go.
                if (body.transform == node.transform) Track(body, node);
                var wasOver = held.Body == body && held.Over;
                held.Body = body;
                held.Over = VrToolBelt.HeadLocal(hand, out var local) && VrToolBelt.OverLeftShoulder(local);
                if (held.Over && !wasOver)
                {
                    held.OverSince = Time.time;
                    hand.Buzz(ReachedStrength, ReachedTime);
                }
                else if (held.Over && Time.time >= held.OverSince + ReachedTime) hand.Buzz(OverStrength, OverTime);
                return;
            }
            var was = held.Body;
            var over = held.Over;
            held.Body = null;
            held.Over = false;
            if (was == null || hand.Grip.Down || !was.gameObject.activeInHierarchy) return;
            if (VrClimb.HeldBody(hand.IsLeft ? VrInput.Right : VrInput.Left) == was) return;
            if (!over && !(VrToolBelt.HeadLocal(hand, out var at) && VrToolBelt.OverLeftShoulder(at))) return;
            Stow(hand, was);
        }

        private static void Stow(VrInput.Hand hand, Rigidbody body)
        {
            var part = NodeOf(body);
            var thing = part != null ? part.gameObject : body.gameObject;
            var name = thing.name;
            var head = VrCamera.Instance != null ? VrCamera.Instance.transform : null;
            if (head != null)
                AudioViewSystem.RequestOneShotSFX(new WwiseShortID(hand.IsLeft ? VrShoulderStow.GrabSoundLeft : VrShoulderStow.GrabSoundRight), head);
            var fined = false;
            if (EntityBlueprintComponent.TryGetEntityBlueprint(thing, out var blueprint))
            {
                if (Fine.Value) fined = MarkDestroyed(blueprint);
                if (fined) Settling.Add((thing, Time.time + Settle));
                else blueprint.EntityManager.AddComponent<PendingDestruction>(blueprint.Entity);
            }
            thing.SetActive(false);
            if (body.gameObject != thing) body.gameObject.SetActive(false);
            ((Carbon.Core.Events.IEventBroadcaster)Main.EventSystem).Post(MasterSFXEvent.GetEvent(new WwiseShortID(VrShoulderStow.RattleSound)));
            hand.Buzz(1f, 0.15f);
            Count.Value++;
            var kind = KindOf(thing, out var seen);
            var kinds = Kinds();
            var added = !kinds.Contains(kind);
            if (added)
            {
                kinds.Add(kind);
                Collection.Value = string.Join(",", kinds);
            }
            // Only the very first one brings LYNX's warning (Raik: three at once would be too much).
            if (Warning.Value == 0) Warning.Value = 1;
            VrSettings.File.Save();
            VrAiNodeInventory.Changed();
            Plugin.Log.LogInfo($"AI node: '{name}' ({seen}, a {kind.Substring(Name.Length + 1)}) put in the backpack by the {hand.Name} hand, "
                + (added ? $"a new kind, {kinds.Count} of 3 on the workbench" : $"its kind lies on the workbench already ({kinds.Count} of 3)")
                + (blueprint != null ? "" : " (no entity found, only hidden)") + (fined ? ", fined as thrown into the processor" : ""));
        }

        // As the processor marks a part that does not belong there: destroyed, for the game to fine.
        private static bool MarkDestroyed(EntityBlueprintComponent blueprint)
        {
            var manager = blueprint.EntityManager;
            var entity = blueprint.Entity;
            if (!manager.Exists(entity)) return false;
            var commands = new EntityCommandBuffer(Allocator.Temp);
            try
            {
                // The part alone, as the processor does with one that does not belong there (ProcessorVolume.
                // HandleNegativeSalvage): by the processor it is destroyed, never salvaged. By the furnace a part the game
                // lets be salvaged by destroying (CanBeDestroySalvaged) counts as salvaged, and was paid (Raik 2026-11-15).
                SalvageableUtility.MarkStructurePartAsDestroyed(commands, manager, entity, SalvagedBy.Processor);
                commands.Playback(manager);
                return true;
            }
            catch (System.Exception e)
            {
                Plugin.Log.LogWarning($"AI node: marking '{blueprint.name}' destroyed failed: {e.Message}");
                return false;
            }
            finally { commands.Dispose(); }
        }

        // The nodes fined a moment ago: now removed as a completed thing is.
        private static void Remove()
        {
            for (var i = Settling.Count - 1; i >= 0; i--)
            {
                var (thing, at) = Settling[i];
                if (thing != null && Time.time < at) continue;
                Settling.RemoveAt(i);
                if (thing != null && EntityBlueprintComponent.TryGetEntityBlueprint(thing, out var blueprint) && blueprint.EntityManager.Exists(blueprint.Entity))
                    blueprint.EntityManager.AddComponent<PendingDestruction>(blueprint.Entity);
            }
        }

        // In the Hab: the kinds collected lie on the workbench, plain copies of the nodes' meshes, nothing of the game's logic.
        private static void Bench()
        {
            var room = VrHab.Room;
            // Nothing changed: nothing to do, without building lists each frame.
            if (room != null && shownIn == room && Shown.Count > 0 && Collection.Value == shownRaw && Shown.TrueForAll(o => o != null)) return;
            var kinds = room != null ? Kinds() : new List<string>();
            var collection = string.Join(",", kinds);
            if (kinds.Count == 0)
            {
                Clear();
                return;
            }
            if (Shown.Count > 0 && shownIn == room && shownCollection == collection && Shown.TrueForAll(o => o != null)) return;
            // Every kind's prefab loaded first, then all are laid out at once.
            var ready = true;
            foreach (var kind in kinds)
            {
                if (!Prefabs.TryGetValue(kind, out var handle) || !handle.IsValid())
                {
                    try { Prefabs[kind] = Addressables.LoadAssetAsync<GameObject>(PrefabKey(kind)); }
                    catch (System.Exception e)
                    {
                        Drop(kind, e.Message);
                        return;
                    }
                    ready = false;
                    continue;
                }
                if (!handle.IsDone)
                {
                    ready = false;
                    continue;
                }
                if (handle.Status != AsyncOperationStatus.Succeeded || handle.Result == null)
                {
                    Drop(kind, handle.Status.ToString());
                    return;
                }
            }
            if (!ready) return;
            Clear();
            Lay(room, kinds);
            shownIn = room;
            shownCollection = collection;
            shownRaw = Collection.Value;
        }

        private static void Clear()
        {
            foreach (var shown in Shown) if (shown != null) Object.Destroy(shown);
            Shown.Clear();
            shownCollection = null;
        }

        // A kind that does not load leaves the collection, so the others still lie there.
        private static void Drop(string kind, string why)
        {
            Plugin.Log.LogWarning($"AI node: '{PrefabKey(kind)}' did not load ({why}), left off the workbench");
            Prefabs.Remove(kind);
            var kinds = Kinds();
            kinds.Remove(kind);
            Collection.Value = string.Join(",", kinds);
        }

        private static void Lay(Transform room, List<string> kinds)
        {
            if (!VrHab.TryRoomFloor(out var floor)) floor = 0f;
            var up = room.up;
            var wall = Vector3.ProjectOnPlane(room.TransformDirection(TowardsWall), up).normalized;
            var spot = room.TransformPoint(new Vector3(Spot.x, floor + BenchTop, Spot.y));
            var tools = GameObject.Find("SM_Hab_Prop_WorkbenchTool_Scissors");
            // On the white crate's top, in its middle; without it, on the bench.
            var crate = Measure(room, WhiteCrate, out var white)
                ? room.TransformPoint(new Vector3(white.center.x, white.max.y, white.center.z))
                : spot;
            // Shown by hand (Raik 2026-11-15: the crate's measured top lay below its drawn one, the node inside it).
            if (TryPlace(LargePlace, room, floor, out var placed)) crate = placed;
            // A measuring gone wrong must not put a node out of the room (Raik 2026-11-15: two of three not to be seen).
            if (Vector3.Distance(crate, spot) > FarOff)
            {
                Plugin.Log.LogWarning($"AI node: the white crate measured {Vector3.Distance(crate, spot):0.0} m from the bench, the large one lies on the bench");
                crate = spot;
            }
            var text = new List<string>();
            for (var i = 0; i < kinds.Count && i < 3; i++)
            {
                var kind = kinds[i];
                var node = Make(room, Prefabs[kind].Result, kind, tools);
                if (node == null) continue;
                var drawn = node.GetComponent<MeshRenderer>();
                var mesh = node.GetComponent<MeshFilter>().sharedMesh;
                // Lying on its back, its longer side along the bench.
                var lying = Quaternion.LookRotation(up, wall);
                if (mesh.bounds.size.y > mesh.bounds.size.x) lying *= Quaternion.Euler(0f, 0f, 90f);
                var slot = System.Array.IndexOf(AllKinds, kind);
                lying = Quaternion.AngleAxis(Turn[Mathf.Max(0, slot)], up) * lying;
                // Each kind has a place of its own (Raik 2026-11-15, pictures): the regular one on the workbench, the large
                // round one on the white storage tower, the long one standing on the floor, leaning on the black crate's
                // side towards the barrels.
                if (kind == AllKinds[2])
                {
                    Lean(room, floor, node, mesh);
                    if (Vector3.Distance(node.transform.position, spot) > FarOff)
                    {
                        Plugin.Log.LogWarning($"AI node: the long one measured {Vector3.Distance(node.transform.position, spot):0.0} m from the bench, it lies on the bench");
                        node.transform.rotation = lying;
                        node.transform.position = spot;
                        var low = Lowest(node, mesh, up);
                        node.transform.position += up * (Vector3.Dot(spot, up) - low);
                    }
                    Shown.Add(node);
                    VrContactShadow.Add(node, mesh, up, "the long AI node");
                    var at = room.InverseTransformPoint(node.transform.position);
                    text.Add($"'{kind}' leaning at ({at.x:0.00}, {at.y - floor:0.00}, {at.z:0.00})");
                    continue;
                }
                var center = kind == AllKinds[1] ? crate : spot;
                node.transform.rotation = lying;
                node.transform.position = center;
                // Its lowest point where it should rest.
                var restOn = Vector3.Dot(center, up);
                var bottom = Lowest(node, mesh, up);
                node.transform.position += up * (restOn - bottom);
                Shown.Add(node);
                // Not under the large round one: it lies flat on the crate and throws no shadow worth one, and the
                // patch's corners showed square around its round foot (Raik 2026-11-16).
                if (kind != AllKinds[1]) VrContactShadow.Add(node, mesh, up, "the AI node on the bench");
                if (kind == AllKinds[0]) Brighten(node, up);
                var local = room.InverseTransformPoint(node.transform.position);
                text.Add($"'{kinds[i]}' at ({local.x:0.00}, {local.y - floor:0.00}, {local.z:0.00}), {drawn.bounds.size.x:0.00} x {drawn.bounds.size.y:0.00} x {drawn.bounds.size.z:0.00} m");
            }
            Plugin.Log.LogInfo($"AI node: in the Hab {string.Join("; ", text)}");
        }

        // The node on the workbench a little brighter (Raik 2026-11-16, pictures: the bench, lit by the lamps above it,
        // is brighter than the node, which only takes the room's baked light). A small light above it, no shadows, its
        // reach not much beyond the node.
        private const float BenchLightHeight = 0.3f;
        private const float BenchLightRange = 0.6f;

        private static void Brighten(GameObject node, Vector3 up)
        {
            if (BenchLight == null || BenchLight.Value <= 0f) return;
            var lamp = new GameObject("ShipbreakerVR AI Node light");
            lamp.transform.SetParent(node.transform, false);
            lamp.transform.position = node.GetComponent<MeshRenderer>().bounds.center + up * BenchLightHeight;
            var data = lamp.AddHDLight(HDLightTypeAndShape.Point);
            data.SetIntensity(BenchLight.Value, LightUnit.Lumen);
            data.SetRange(BenchLightRange);
            data.SetColor(new Color(1f, 0.96f, 0.9f));
            data.EnableShadows(false);
            // Not in the Hab's haze: lit, it showed as a bright point in the air (Raik 2026-11-16).
            data.volumetricDimmer = 0f;
            Plugin.Log.LogInfo($"AI node: the one on the bench lit by a small light of {BenchLight.Value:0} lumen");
        }

        // The long one upright on the floor, its back leaning on the black crate's side (Raik 2026-11-15: pictures looking
        // down between the crate and the wall's pipe): its dome facing away from the crate and a little round to the room.
        private static void Lean(Transform room, float floor, GameObject node, Mesh mesh)
        {
            // In the room's axes: the crate's side towards the barrels (its largest z), the node standing in front of its
            // middle, on the floor's own top where it is measured.
            if (!Measure(room, BlackCrate, out var crate))
            {
                Plugin.Log.LogWarning($"AI node: '{BlackCrate}' not found, the long one stands by the bench");
                crate = new Bounds(new Vector3(Spot.x - 0.5f, floor + 0.5f, Spot.y - 0.5f), Vector3.one * 0.6f);
            }
            var ground = floor;
            if (Measure(room, Floor, out var floorBox) && Mathf.Abs(floorBox.max.y - floor) < 0.15f) ground = floorBox.max.y;
            var away = Quaternion.AngleAxis(-LeanTurn, Vector3.up) * Vector3.forward;
            var upright = Quaternion.LookRotation(away, Vector3.up);
            if (mesh.bounds.size.x > mesh.bounds.size.y) upright *= Quaternion.Euler(0f, 0f, 90f);
            upright = Quaternion.AngleAxis(LeanAngle, Vector3.Cross(away, Vector3.up)) * upright;
            node.transform.localRotation = upright;
            node.transform.localPosition = new Vector3(crate.center.x, ground + 0.5f, crate.max.z + 0.2f);
            // Its box in the room's axes: lowest point on the floor, nearest point to the crate on its side, farthest one
            // towards the kitchen's wall behind the crate.
            var low = float.MaxValue;
            var near = float.MaxValue;
            var far = float.MinValue;
            var b = mesh.bounds;
            for (var c = 0; c < 8; c++)
            {
                var corner = new Vector3((c & 1) == 0 ? b.min.x : b.max.x, (c & 2) == 0 ? b.min.y : b.max.y, (c & 4) == 0 ? b.min.z : b.max.z);
                var p = room.InverseTransformPoint(node.transform.TransformPoint(corner));
                low = Mathf.Min(low, p.y);
                near = Mathf.Min(near, p.z);
                far = Mathf.Max(far, p.x);
            }
            // Into the corner of crate and wall (Raik 2026-11-15: still a gap to the crate, and leaning as it does it
            // belongs in the corner, on crate and wall both). Its box's corners stand off its rounded model, so it goes
            // a little into the box on both sides.
            var wall = Measure(room, KitchenWall, out var wallBox) && Mathf.Abs(wallBox.min.x - crate.max.x) < 0.3f ? wallBox.min.x : crate.max.x;
            node.transform.localPosition += new Vector3(wall + WallSnug - far, ground - low, crate.max.z - CrateSnug - near);
        }

        // How far the long one's box goes into the black crate's and the wall's (its rounded model then touches them).
        private const float CrateSnug = 0.025f;
        private const float WallSnug = 0.01f;
        private const string KitchenWall = "PRF_Hab_Rev_03/PRF_Hab_Interior_Kitchen/SM_Hab_Interior_Kitchen_Wall";

        // A thing's drawn box in the room's axes (its models' own boxes turned into the room, all of its parts), by its
        // path under the room. False where there is none.
        private static bool Measure(Transform room, string path, out Bounds box)
        {
            box = default;
            var thing = room.Find(path);
            if (thing == null) return false;
            var any = false;
            foreach (var renderer in thing.GetComponentsInChildren<MeshRenderer>())
                if (renderer.enabled) VrModelBox.Add(renderer, room, ref box, ref any);
            if (any && measureLogs++ < 6)
                Plugin.Log.LogInfo($"AI node: '{path}' measured from ({box.min.x:0.00}, {box.min.y:0.00}, {box.min.z:0.00}) to ({box.max.x:0.00}, {box.max.y:0.00}, {box.max.z:0.00}) in the room");
            return any;
        }

        private static int measureLogs;

        // Farther than this from the workbench, a place is measured wrong: the Hab's room is a few metres across.
        private const float FarOff = 8f;

        private static GameObject Make(Transform room, GameObject source, string kind, GameObject tools)
        {
            var filter = Drawn(source);
            var renderer = filter != null ? filter.GetComponent<MeshRenderer>() : null;
            if (filter == null || renderer == null)
            {
                Plugin.Log.LogWarning($"AI node: '{kind}' has no mesh, left off the workbench");
                return null;
            }
            var node = new GameObject("ShipbreakerVR AI Node " + kind.Substring(Name.Length + 1));
            node.transform.SetParent(room, false);
            node.layer = tools != null ? tools.layer : renderer.gameObject.layer;
            node.AddComponent<MeshFilter>().sharedMesh = filter.sharedMesh;
            node.AddComponent<MeshRenderer>().sharedMaterials = renderer.sharedMaterials;
            node.transform.localScale = filter.transform.lossyScale;
            return node;
        }

        // The model's lowest point along up: its own box's corners, not the world box, which stands taller where the node
        // lies turned (Raik 2026-11-15: the large one a few centimetres above the white crate).
        private static float Lowest(GameObject node, Mesh mesh, Vector3 up)
        {
            var b = mesh.bounds;
            var low = float.MaxValue;
            for (var c = 0; c < 8; c++)
            {
                var corner = new Vector3((c & 1) == 0 ? b.min.x : b.max.x, (c & 2) == 0 ? b.min.y : b.max.y, (c & 4) == 0 ? b.min.z : b.max.z);
                low = Mathf.Min(low, Vector3.Dot(node.transform.TransformPoint(corner), up));
            }
            return low;
        }

        // The point moved up or down to the bench top.
        private static Vector3 Flat(Vector3 point, Vector3 up, float height) => point + up * (height - Vector3.Dot(point, up));
    }

}
