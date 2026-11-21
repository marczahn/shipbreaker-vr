using System.Collections.Generic;
using BBI.Unity.Game;
using BepInEx.Configuration;
using Carbon.Audio;
using Unity.Collections;
using Unity.Entities;
using Unity.Transforms;
using UnityEngine;
using UnityEngine.AddressableAssets;
using UnityEngine.ResourceManagement.AsyncOperations;

namespace ShipbreakerVR
{
    // Collection (Raik 2026-11-15, "Sammelkampagne", in 0.32.0): a radio, collected as the AI nodes and the
    // game's pink bunny are. From ship level 2 on, a ship has it floating loose somewhere inside now and then
    // (Experimente/RadioChance); put in the backpack over the left shoulder, it stands in the Hab on the table left of
    // the message computer from then on (Experimente/RadioGesammelt, Experimente/RadioPlatz). Its model is Raik's own
    // (VrRadioModel). In the ship it is the mod's own loose body (no entity of the game's: the hands hold it as any loose
    // thing, the game's tools and the barge do not know it). Kept in the mod's config, not in the save.
    internal static class VrRadio
    {
        public static ConfigEntry<bool> Collected;
        public static ConfigEntry<string> Place;
        public static ConfigEntry<float> Chance;
        public static ConfigEntry<int> FromLevel;
        public static ConfigEntry<string> Rolled;

        // Its body: the mass of a full portable radio, kept a little from drifting as the game's loose things are.
        private const float Mass = 4f;
        private const float Drag = 0.05f;
        private const float AngularDrag = 0.1f;
        // The ship stands this long finished before the radio comes in (its physics on again, its rooms there).
        private const float SettleTime = 3f;
        // Free room around a place to float in, and tries before giving up.
        private const float Clearance = 0.35f;
        private const int Tries = 60;

        private static GameObject shown;

        // The radio standing in the Hab, while it does.
        public static Transform InHab => shown != null ? shown.transform : null;
        private static Transform shownIn;
        private static string shownPlace;
        private static bool warned;

        private static GameObject floating;
        private static string shipKey;
        private static float readySince = -1f;
        private static bool handled;

        public static void Bind()
        {
            if (Collected != null || VrSettings.File == null) return;
            Collected = VrSettings.Bind("Experimente", "RadioGesammelt", false,
                "Experiment: the radio was collected from a ship and stands in the Hab.");
            Place = VrSettings.Bind("Experimente", "RadioPlatz", "",
                "Experiment: where the radio stands in the Hab, 'x; y; z; turn' in the room's axes as a hand snapshot writes them (y above the floor), its bottom's middle, turn in degrees about up. Empty: not shown.");
            Chance = VrSettings.Bind("Experimente", "RadioChance", 0.1f,
                "Experiment: the chance that a ship from RadioAbStufe on has the radio floating inside (0 to 1), while it is not collected yet.");
            FromLevel = VrSettings.Bind("Experimente", "RadioAbStufe", 2,
                "Experiment: the lowest ship level (the job's ship class grade) the radio can be found in.");
            Rolled = VrSettings.Bind("Experimente", "RadioWurf", "",
                "Experiment: the last ship asked whether it has the radio and the answer, so loading it again does not ask anew.");
        }

        public static void Update()
        {
            Bind();
            if (Collected == null) return;
            Hab();
            Ship();
            if (floating != null && floating != scannableDone) Scannable(floating);
            if (floating != null && VrXr.IsRunning)
            {
                Check(VrInput.Left, Left);
                Check(VrInput.Right, Right);
            }
        }

        // ---- In the ship ----

        // Once a ship is finished spawning, new or from the save: asked once whether it has the radio (from its level on,
        // by chance), and if so the radio floats in one of its rooms. Gone with the ship.
        private static void Ship()
        {
            var service = ModuleService.Instance;
            var preview = service != null ? service.CurrentShipPreview : null;
            var key = preview != null ? $"{preview.Seed}:{preview.ShipName}" : null;
            if (key != shipKey)
            {
                if (floating != null) Object.Destroy(floating);
                floating = null;
                shipKey = key;
                readySince = -1f;
                handled = false;
            }
            if (preview == null || handled) return;
            LoadDriveLook();
            var ready = service.CurrentManifest != Entity.Null && !service.ShipSpawningInProgress && !service.ShipSpawningFromSaveInProgress
                && !service.ShipDespawningInProgress;
            if (!ready)
            {
                readySince = -1f;
                return;
            }
            if (readySince < 0f) readySince = Time.time;
            if (Time.time < readySince + SettleTime) return;
            handled = true;
            var grade = preview.ShipClassAsset != null ? preview.ShipClassAsset.ShipGrade : -1;
            if (Collected.Value)
            {
                Plugin.Log.LogInfo($"Radio: ship '{preview.ShipName}' (grade {grade}) ready, the radio is collected already");
                return;
            }
            bool has;
            if (Rolled.Value.StartsWith(key + "="))
                has = Rolled.Value.EndsWith("=1");
            else
            {
                has = grade >= FromLevel.Value && Random.value < Chance.Value;
                Rolled.Value = key + "=" + (has ? "1" : "0");
                VrSettings.File.Save();
            }
            Plugin.Log.LogInfo($"Radio: ship '{preview.ShipName}' grade {grade} ({(preview.ShipClassAsset != null ? preview.ShipClassAsset.name : "no class")}), "
                + (has ? "has the radio" : grade < FromLevel.Value ? $"below grade {FromLevel.Value}, no radio" : "no radio this time"));
            if (has) Spawn();
        }

        private static void Spawn()
        {
            var mesh = VrRadioModel.Mesh;
            var template = ShipTemplate(out var layer);
            var material = mesh != null ? VrRadioModel.MaterialFrom(template) : null;
            if (mesh == null || material == null)
            {
                Plugin.Log.LogWarning("Radio: no model or material for it, not put in the ship");
                return;
            }
            if (!FreePlace(out var at, out var room))
            {
                Plugin.Log.LogWarning("Radio: no free place in the ship's rooms found, not put in the ship");
                return;
            }
            var radio = new GameObject("ShipbreakerVR Radio (ship)");
            // Built while inactive, so the scanner's highlight (which wants its look before it wakes) can come on it first.
            radio.SetActive(false);
            radio.layer = layer;
            radio.transform.SetPositionAndRotation(at, Random.rotation);
            radio.AddComponent<MeshFilter>().sharedMesh = mesh;
            radio.AddComponent<MeshRenderer>().sharedMaterial = material;
            var box = radio.AddComponent<BoxCollider>();
            // The body only: the long antenna would make a box of mostly air to grab and bump into.
            box.center = VrRadioModel.Body.center;
            box.size = VrRadioModel.Body.size * 0.95f;
            var body = radio.AddComponent<Rigidbody>();
            body.mass = Mass;
            body.useGravity = false;
            body.drag = Drag;
            body.angularDrag = AngularDrag;
            body.interpolation = RigidbodyInterpolation.Interpolate;
            body.collisionDetectionMode = CollisionDetectionMode.ContinuousDynamic;
            floating = radio;
            Scannable(radio);
            radio.SetActive(true);
            body.angularVelocity = Random.insideUnitSphere * 0.3f;
            Plugin.Log.LogInfo($"Radio: floating in room '{room}' at {at}");
        }

        // ---- In the scanner ----

        // The game's data drives: the radio lights up in the scanner as they do (Raik 2026-11-16: "dieselbe Farbe wie die
        // Datenkassetten"). Their highlight look (HighlightComponentAsset: a scanner material for each scan mode) is in
        // their part's data (StructurePartAsset), from which the game puts it on them at run time. A HighlightComponent of
        // the game's own with it on the radio registers with the game's HighlightService, which swaps the radio's
        // material while the scanner is on, as for any of the ship's things.
        private const string DriveKey = "Assets/Content/Data/StructurePartAsset/Objects/Pickups/SP_Pickup_Data_Drive.asset";
        // The game's look for things to pick up (the scanner's "Aufheben"), taken when the data drive's own does not come.
        private const string PickupKey = "Assets/Content/Data/HighlightComponentAsset/HIGH_Pickup.asset";
        private const float LookWait = 5f;
        private static AsyncOperationHandle<StructurePartAsset> drive;
        private static AsyncOperationHandle<HighlightComponentAsset> pickup;
        private static bool driveAsked, driveFailed, pickupFailed, lookedLoaded;
        private static float askedAt;
        private static HighlightComponentAsset driveLook;
        private static GameObject scannableDone;

        private static void LoadDriveLook()
        {
            if (driveAsked) return;
            driveAsked = true;
            askedAt = Time.time;
            // Already in memory when the ship has a data drive or another pickup.
            foreach (var part in Resources.FindObjectsOfTypeAll<StructurePartAsset>())
                if (part != null && part.name == "SP_Pickup_Data_Drive" && part.Data != null && part.Data.HighlightAsset != null)
                {
                    Took(part.Data.HighlightAsset, "the data drive (loaded)");
                    return;
                }
            try { drive = LoadByPath<StructurePartAsset>(DriveKey); }
            catch (System.Exception e) { driveFailed = true; Plugin.Log.LogWarning($"Radio: the data drive's part did not load ({e.Message})"); }
            try { pickup = LoadByPath<HighlightComponentAsset>(PickupKey); }
            catch (System.Exception e) { pickupFailed = true; Plugin.Log.LogWarning($"Radio: the pickup look did not load ({e.Message})"); }
        }

        // The game's catalog keeps these under a key of their own (not their path, Raik's test 2026-11-16: "No Location
        // found for Key"); the location whose path is this one is looked up among all of its keys.
        private static AsyncOperationHandle<T> LoadByPath<T>(string path)
        {
            foreach (var locator in Addressables.ResourceLocators)
                foreach (var key in locator.Keys)
                {
                    if (!locator.Locate(key, typeof(T), out var locations)) continue;
                    foreach (var location in locations)
                        if (location.InternalId == path) return Addressables.LoadAssetAsync<T>(location);
                }
            throw new System.Exception("not in the catalog");
        }

        // Whether a load is over, and logs once how it failed.
        private static bool Finished<T>(AsyncOperationHandle<T> handle, ref bool failed, string what, out T result)
        {
            result = default;
            if (failed) return true;
            if (!handle.IsValid())
            {
                failed = true;
                Plugin.Log.LogWarning($"Radio: {what} has no load");
                return true;
            }
            if (!handle.IsDone) return false;
            if (handle.Status != AsyncOperationStatus.Succeeded || handle.Result == null)
            {
                failed = true;
                Plugin.Log.LogWarning($"Radio: {what} did not load ({handle.Status}{(handle.OperationException != null ? ": " + handle.OperationException.Message : "")})");
                return true;
            }
            result = handle.Result;
            return true;
        }

        private static HighlightComponentAsset DriveLook()
        {
            if (driveLook != null || !driveAsked) return driveLook;
            var driveOver = Finished(drive, ref driveFailed, "the data drive's part", out var part);
            if (part != null)
            {
                var look = part.Data != null ? part.Data.HighlightAsset : null;
                if (look != null) return Took(look, "the data drive");
                driveFailed = true;
                Plugin.Log.LogWarning("Radio: the data drive's part has no scanner look");
            }
            if (!driveOver && Time.time < askedAt + LookWait) return null;
            Finished(pickup, ref pickupFailed, "the pickup look", out var fallback);
            if (fallback != null) return Took(fallback, "things to pick up");
            if (!pickupFailed || lookedLoaded) return null;
            // Neither came: the pickup look as the ship's own pickups have it loaded, looked for once.
            lookedLoaded = true;
            foreach (var loaded in Resources.FindObjectsOfTypeAll<HighlightComponentAsset>())
                if (loaded != null && loaded.name == "HIGH_Pickup") return Took(loaded, "things to pick up (loaded)");
            Plugin.Log.LogWarning("Radio: no scanner look found, the radio is not in the scanner");
            return null;
        }

        private static HighlightComponentAsset Took(HighlightComponentAsset look, string whose)
        {
            driveLook = look;
            var modes = look.Data.ScanModeMaterials;
            Plugin.Log.LogInfo($"Radio: scanner look of {whose} '{look.name}', scannable {look.Data.IsScannable}, "
                + $"{(modes != null ? modes.Count : 0)} scan mode material(s)");
            return look;
        }

        // The highlight on the radio; on an inactive radio before it first wakes, else switched off and on for it to wake
        // with its look (a HighlightComponent without one destroys itself on waking).
        private static void Scannable(GameObject radio)
        {
            if (radio == null || radio == scannableDone) return;
            var look = DriveLook();
            if (look == null) return;
            scannableDone = radio;
            var active = radio.activeSelf;
            Vector3 velocity = default, spin = default;
            var body = radio.GetComponent<Rigidbody>();
            if (active && body != null)
            {
                velocity = body.velocity;
                spin = body.angularVelocity;
            }
            if (active) radio.SetActive(false);
            radio.AddComponent<HighlightComponent>().HighlightAsset = look;
            if (active)
            {
                radio.SetActive(true);
                if (body != null)
                {
                    body.velocity = velocity;
                    body.angularVelocity = spin;
                }
            }
            Plugin.Log.LogInfo("Radio: shows in the scanner as the data drives do");
        }

        // A lit material and the layer of a loose thing of the ship's own.
        private static Material ShipTemplate(out int layer)
        {
            layer = 0;
            foreach (var part in Object.FindObjectsOfType<StructurePart>())
            {
                var body = part.GetComponentInParent<Rigidbody>();
                var renderer = part.GetComponentInChildren<MeshRenderer>();
                if (body == null || body.isKinematic || renderer == null || renderer.sharedMaterial == null) continue;
                layer = body.gameObject.layer;
                return renderer.sharedMaterial;
            }
            return null;
        }

        // A random point in a random room of the ship (its volumes as the game keeps them, moved with the room), with
        // nothing within Clearance of it.
        private static bool FreePlace(out Vector3 at, out string room)
        {
            at = default;
            room = null;
            var world = World.DefaultGameObjectInjectionWorld;
            if (world == null) return false;
            var manager = world.EntityManager;
            var volumes = new List<(Matrix4x4 Matrix, string Room)>();
            var query = manager.CreateEntityQuery(ComponentType.ReadOnly<RoomComponent>(), ComponentType.ReadOnly<LocalToWorld>());
            using (var rooms = query.ToEntityArray(Allocator.Temp))
            {
                foreach (var entity in rooms)
                {
                    if (!manager.HasComponent<RoomVolumeIncludeData>(entity)) continue;
                    var data = manager.GetComponentData<RoomComponent>(entity);
                    if (data.ExternallyExposed) continue;
                    Matrix4x4 toWorld = manager.GetComponentData<LocalToWorld>(entity).Value;
                    var name = "room " + entity.Index;
                    foreach (var volume in manager.GetBuffer<RoomVolumeIncludeData>(entity))
                        volumes.Add((toWorld * (Matrix4x4)volume.LocalMatrix, name));
                }
            }
            query.Dispose();
            if (volumes.Count == 0) return false;
            for (var i = 0; i < Tries; i++)
            {
                var (matrix, name) = volumes[Random.Range(0, volumes.Count)];
                var p = matrix.MultiplyPoint3x4(new Vector3(Random.value - 0.5f, Random.value - 0.5f, Random.value - 0.5f) * 0.8f);
                if (Physics.CheckSphere(p, Clearance, ~0, QueryTriggerInteraction.Ignore)) continue;
                at = p;
                room = name;
                return true;
            }
            return false;
        }

        // ---- Collected over the shoulder, as the AI nodes are ----

        private sealed class Held
        {
            public bool Over;
            public float OverSince;
            public bool Holding;
        }

        private static readonly Held Left = new Held();
        private static readonly Held Right = new Held();

        private static void Check(VrInput.Hand hand, Held held)
        {
            var body = floating != null ? floating.GetComponent<Rigidbody>() : null;
            if (body == null) return;
            if (VrClimb.HeldBody(hand) == body)
            {
                var wasOver = held.Holding && held.Over;
                held.Holding = true;
                held.Over = VrToolBelt.HeadLocal(hand, out var local) && VrToolBelt.OverLeftShoulder(local);
                if (held.Over && !wasOver)
                {
                    held.OverSince = Time.time;
                    hand.Buzz(0.6f, 0.08f);
                }
                else if (held.Over && Time.time >= held.OverSince + 0.08f) hand.Buzz(0.15f, 0.05f);
                return;
            }
            var was = held.Holding;
            var over = held.Over;
            held.Holding = false;
            held.Over = false;
            if (!was || hand.Grip.Down) return;
            if (VrClimb.HeldBody(hand.IsLeft ? VrInput.Right : VrInput.Left) == body) return;
            if (!over && !(VrToolBelt.HeadLocal(hand, out var at) && VrToolBelt.OverLeftShoulder(at))) return;
            Stow(hand);
        }

        private static void Stow(VrInput.Hand hand)
        {
            var head = VrCamera.Instance != null ? VrCamera.Instance.transform : null;
            if (head != null)
                AudioViewSystem.RequestOneShotSFX(new WwiseShortID(hand.IsLeft ? VrShoulderStow.GrabSoundLeft : VrShoulderStow.GrabSoundRight), head);
            ((Carbon.Core.Events.IEventBroadcaster)Main.EventSystem).Post(MasterSFXEvent.GetEvent(new WwiseShortID(VrShoulderStow.RattleSound)));
            hand.Buzz(1f, 0.15f);
            Object.Destroy(floating);
            floating = null;
            Collected.Value = true;
            VrSettings.File.Save();
            Plugin.Log.LogInfo($"Radio: put in the backpack by the {hand.Name} hand, it stands in the Hab from now on");
        }

        // ---- In the Hab ----

        // The collected radio on its place, a plain copy of the model, nothing of the game's logic.
        private static void Hab()
        {
            var room = VrHab.Room;
            var want = room != null && Collected.Value;
            if (want && shown != null && shownIn == room && shownPlace == Place.Value) return;
            if (shown != null) Object.Destroy(shown);
            shown = null;
            shownIn = null;
            shownPlace = null;
            if (!want) return;
            if (!VrHab.TryRoomFloor(out var floor)) floor = 0f;
            if (!VrAiNode.TryPlace(Place.Value, room, floor, out var at, out var turn))
            {
                if (!warned) Plugin.Log.LogWarning("Radio: collected, but RadioPlatz is not set, so it is not shown in the Hab");
                warned = true;
                return;
            }
            var asked = room.InverseTransformPoint(at);
            var onTable = TryTableTop(room, asked, floor, out var top, out var table, out var tableBox);
            if (onTable)
            {
                at = room.TransformPoint(new Vector3(asked.x, top + floor + OnTable, asked.z));
                Plugin.Log.LogInfo($"Radio: set on '{table}', its top at {top:0.00} (asked for {asked.y - floor:0.00})");
            }
            else Plugin.Log.LogWarning($"Radio: no table found below ({asked.x:0.00}, {asked.y - floor:0.00}, {asked.z:0.00}), set where asked");
            var mesh = VrRadioModel.Mesh;
            var template = HabTemplate();
            var material = mesh != null ? VrRadioModel.MaterialFrom(template != null ? template.sharedMaterial : null) : null;
            if (mesh == null || material == null) return;
            var radio = new GameObject("ShipbreakerVR Radio");
            radio.transform.SetParent(room, false);
            radio.layer = template.gameObject.layer;
            radio.AddComponent<MeshFilter>().sharedMesh = mesh;
            radio.AddComponent<MeshRenderer>().sharedMaterial = material;
            radio.transform.position = at;
            radio.transform.localRotation = Quaternion.AngleAxis(turn, Vector3.up);
            // Its shadow thrown by a light straight in front of it, high up (Raik 2026-11-16: it faces the ceiling bay's
            // lamps, its shadow belongs straight behind it, towards the window): its own shape on the desk, about a
            // quarter of a metre out behind it, up to the table's edge. The bay's real strongest lamp would throw it a
            // metre long along the desk.
            var lamp = at + radio.transform.forward * ShadowLampAhead + room.up * ShadowLampHigh;
            if (onTable) VrContactShadow.AddFromLamp(radio, mesh, room.up, lamp, room, tableBox, "the radio");
            else VrContactShadow.Add(radio, mesh, room.up, "the radio");
            shown = radio;
            shownIn = room;
            shownPlace = Place.Value;
            var local = room.InverseTransformPoint(at);
            Plugin.Log.LogInfo($"Radio: in the Hab at ({local.x:0.00}, {local.y - floor:0.00}, {local.z:0.00}), turned {turn:0}°");
        }

        // The table under the radio's middle (Raik 2026-11-16, pictures: it floated a little above the table, set at the
        // height of a controller's tip held on it). The highest top of the Hab's models there, between knee height and
        // a little above the place asked for; walls and the window reach higher and are left out by that.
        private const float TableLow = 0.6f;
        private const float TableAbove = 0.1f;
        private const float OnTable = 0.002f;
        private const float TableSize = 0.5f;
        // Where the light throwing the radio's shadow is: this far ahead of its front and this high above the desk.
        private const float ShadowLampAhead = 0.8f;
        private const float ShadowLampHigh = 1.5f;

        private static bool TryTableTop(Transform room, Vector3 asked, float floor, out float top, out string table, out Bounds tableBox)
        {
            top = float.MinValue;
            table = null;
            tableBox = default;
            foreach (var renderer in room.GetComponentsInChildren<MeshRenderer>())
            {
                if (renderer == null || !renderer.enabled || renderer.name.StartsWith("ShipbreakerVR")) continue;
                var box = default(Bounds);
                var any = false;
                if (!VrModelBox.Add(renderer, room, ref box, ref any) || !any) continue;
                if (asked.x < box.min.x || asked.x > box.max.x || asked.z < box.min.z || asked.z > box.max.z) continue;
                // Only something as large as a table: the multitool lying there was taken once (Raik 2026-11-16, the
                // radio stood on it, 11 cm above the desk's top).
                if (box.size.x < TableSize || box.size.z < TableSize) continue;
                var y = box.max.y - floor;
                if (y < TableLow || y > asked.y - floor + TableAbove || y <= top) continue;
                top = y;
                tableBox = box;
                table = renderer.transform.parent != null ? renderer.transform.parent.name + "/" + renderer.name : renderer.name;
            }
            return table != null;
        }

        // A lit material of the Hab's own things, as the AI nodes' copies use theirs.
        private static MeshRenderer HabTemplate()
        {
            var tools = GameObject.Find("SM_Hab_Prop_WorkbenchTool_Scissors");
            return tools != null ? tools.GetComponent<MeshRenderer>() : null;
        }
    }
}
