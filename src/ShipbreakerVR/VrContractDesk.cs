using System.Collections.Generic;
using BBI.Unity.Game;
using UnityEngine;
using UnityEngine.AddressableAssets;
using UnityEngine.ResourceManagement.AsyncOperations;

namespace ShipbreakerVR
{
    // The start of a new career at a desk (Raik, 2026-11-23). After the intro's lines of text the game shows the player's
    // apartment: a picture far behind ('3D Scene/Background_Main', a sprite 7.7 m from the front end's camera), a small
    // computer model right in front of that camera ('PRF_ApartmentComputer') and on it the contract as a world-space
    // canvas 22 cm wide ('UI/PRF_ContractScreen_WorldSpace', layer 10, which the front end's main camera draws itself).
    // Flat, the camera's 25 degrees make that fill the screen. In VR the rig stands at that camera, and the contract was
    // a tiny terminal somewhere in the room.
    // Here the player gets a copy of the station's desk instead, with its chair and the message terminal on it, standing
    // on the playspace's floor in front of them, and the contract lies on that terminal's screen, as large as fits. The
    // screens after it (the onboarding: video, pop-ups, cloning, the fees, the thumb print; screen-space canvases the mod
    // draws through its UI texture) lie on the same screen (VrUi asks TryGetScreen), as the menus on the Hab's monitors
    // do. Gone again once the apartment goes, or the flight's video plays.
    //
    // The desk is made of the game's own models, taken where the game has them: the prefab of the Hab's interior and
    // the chair's are loaded through Addressables (the keys are their paths in the game's catalog) and only read: for
    // each mesh of the desk group, the terminal on it and the chair a plain object with that mesh and its materials is
    // made, with none of the game's components. Nothing of them is stored or shipped. The prefabs are let go again when
    // the desk is.
    internal static class VrContractDesk
    {
        static VrContractDesk() => VrSession.OnUnload("contract desk", Release);

        private const string RoomKey = "Assets/Content/Art/Structures/Kitbash/Hab/Prefabs/PRF_Hab_Rev_03.prefab";
        private const string ChairKey = "Assets/Content/Art/Structures/Kitbash/Hab/Models/PRF_Props_Hab_Chair.prefab";
        // In the room's prefab: the desk's parts, the terminal on it, and the canvas that marks its screen.
        private const string DeskGroup = "GRP_Hab_Interior_Desk";
        private const string Terminal = "PRF_Computer_Terminal_Hab";
        private const string ScreenMark = "LYNX_Screensaver_Locked";
        // The chair is a prefab of its own, placed in the Hab's scene: where it stands in the room's prefab's axes
        // (from the scene 'Decoupled_Hab': its place and turn against 'PRF_Hab_Rev_03').
        private static readonly Vector3 ChairAt = new Vector3(-7.7402f, -0.0052f, -32.4935f);
        private static readonly Quaternion ChairTurn = new Quaternion(0f, 0.70711f, 0f, -0.70711f);
        // The floor's height in the room's prefab's axes (the chair stands on it).
        private const float FloorAt = 0f;
        // The terminal's screen, should its canvas not be in the prefab: its middle (the screensaver's place in the
        // scene), leaning back 38 degrees, 0.56 x 0.31 m (docs/NOTES.md, the Hab's monitors).
        private static readonly Vector3 ScreenAt = new Vector3(-8.328f, 1.2737f, -31.4625f);
        private static readonly Vector3 ScreenInto = new Vector3(-0.784f, -0.62f, 0f);
        private static readonly Vector3 ScreenUp = new Vector3(-0.62f, 0.784f, 0f);
        private static readonly Vector2 ScreenSize = new Vector2(0.56f, 0.31f);
        // Parts of the desk group further from the screen than this belong to the workbench, in meters.
        private const float DeskAround = 2.6f;
        // How far in front of the head the screen's middle is put, level, in meters; the canvases lie this far before
        // the glass.
        // Raik's first test (2026-11-23): 60 cm was a little too near.
        private const float Distance = 0.72f;
        private const float BeforeGlass = 0.01f;
        // The contract's canvas lies this far before the terminal's face. Measured on the terminal's mesh
        // ('SM_Computer_Terminal_Hab'): its front is one flat face right in the plane of its screensaver canvas; the
        // frame round it is the desk's own screen part, which stands out, so the glass looks set in. 3 mm before that
        // face the contract seemed to float on it (Raik's first test), 7 mm behind it it was inside the model and gone
        // (his second, with a picture). So: as close before the face as does not flicker with it.
        private const float ContractBefore = 0.001f;
        // The glass is higher than the terminal's own canvas (30 cm): the screen counts as this much higher (Raik:
        // "one centimeter higher"; the width fits as it is).
        private const float ScreenHigher = 0.01f;
        // The desk comes in front of the player again once the head has moved this far from where it was placed for
        // (level, in meters) and has been still (slower than StillBelow m/s) for StillFor seconds: Raik sat down again
        // after it had been placed, and the terminal was off to the left.
        private const float MovedFrom = 0.2f;
        private const float StillBelow = 0.1f;
        private const float StillFor = 0.6f;
        // The apartment's picture is stretched until it is this wide as seen from the head, in degrees (Raik: only
        // stretched, not moved).
        private const float PictureWide = 120f;
        private const float SearchEvery = 1f;

        // The game's side: the contract scene's root and what of it is used.
        private static Transform scene;
        private static GameObject room3D;
        private static Transform ui;
        private static RectTransform contract;
        private static GameObject onboarding;
        private static Transform flight;
        private static float nextSearch;

        // The prefabs, while loading and loaded.
        private static AsyncOperationHandle<GameObject> roomLoad, chairLoad;
        private static bool loading, loadFailed;

        // The desk: its root, the screen in the root's axes, and whether it is shown.
        private static GameObject desk;
        private static Vector3 screenCenter;
        private static Quaternion screenTurn = Quaternion.identity;
        private static Vector2 screenSize;
        private static bool shown;
        private static string stateLogged;
        // Where the head was (in the playspace) when the desk was placed, where it was last frame, and since when it
        // is still.
        private static Vector3 placedFor, headBefore;
        private static float stillSince;

        // What is changed of the game's scene while the desk is shown, to put it back.
        private static bool uiMoved, pictureStretched;
        private static Vector3 uiPosition, uiScale, pictureScale;
        private static Quaternion uiRotation;
        private static Transform picture;
        private static Renderer computer;
        private static int logs;

        // The terminal's screen for the mod's UI texture (VrUi lays it there as on a monitor of the Hab): only while the
        // onboarding's screens are up. During the contract, which is a canvas in the room and lies on the screen itself
        // (Keep), the UI texture stays where menus are, so whatever it shows then cannot cover the contract.
        public static bool TryGetScreen(out Vector3 center, out Quaternion rotation, out Vector2 size)
        {
            if (onboarding != null && onboarding.activeInHierarchy) return Screen(out center, out rotation, out size);
            center = Vector3.zero;
            rotation = Quaternion.identity;
            size = Vector2.zero;
            return false;
        }

        // The terminal's screen while the desk is shown: its middle on the glass, its turn (forward into the screen) and
        // its size, in the world.
        private static bool Screen(out Vector3 center, out Quaternion rotation, out Vector2 size)
        {
            center = Vector3.zero;
            rotation = Quaternion.identity;
            size = Vector2.zero;
            if (!shown || desk == null) return false;
            var root = desk.transform;
            rotation = root.rotation * screenTurn;
            center = root.TransformPoint(screenCenter) + rotation * Vector3.forward * BeforeGlass;
            size = new Vector2(screenSize.x, screenSize.y + ScreenHigher) * root.lossyScale.x;
            return true;
        }

        // From VrFrame, in Update.
        public static void Update()
        {
            var vr = VrCamera.Instance;
            var state = GameSession.CurrentGameState;
            if (!VrXr.IsRunning || vr == null || vr.FollowsHab || state == GameSession.GameState.Gameplay || state == GameSession.GameState.Hab)
            {
                if (desk != null || loading || scene != null) Release();
                return;
            }
            if (scene == null && !Find())
            {
                if (desk != null || loading) Release();
                return;
            }
            var apartment = room3D != null && room3D.activeInHierarchy;
            var flying = Visible(flight);
            var wanted = apartment && !flying;
            Log($"apartment {(apartment ? "shown" : "off")}, contract {(Visible(contract) ? "shown" : "off")}, onboarding {(onboarding != null && onboarding.activeInHierarchy ? "shown" : "off")}, flight video {(flying ? "shown" : "off")}");
            if (!wanted)
            {
                Hide();
                return;
            }
            if (desk == null && !Build()) return;
            if (!shown) Show(vr);
            else Follow(vr);
            Keep();
        }

        // The state of the game's scene, written once per change.
        private static void Log(string state)
        {
            if (state == stateLogged || logs >= 60) return;
            stateLogged = state;
            logs++;
            Plugin.Log.LogInfo($"Contract desk: {state}");
        }

        // The contract scene in the front end, looked for once a second: by the contract's own script, which sits on an
        // object that may be switched off.
        private static bool Find()
        {
            if (Time.unscaledTime < nextSearch) return false;
            nextSearch = Time.unscaledTime + SearchEvery;
            foreach (var check in Resources.FindObjectsOfTypeAll<ContractToggleCheck>())
            {
                if (check == null || !check.gameObject.scene.IsValid()) continue;
                var root = check.transform;
                while (root != null && root.name != "PRF_ContractScene") root = root.parent;
                if (root == null) continue;
                scene = root;
                var room = root.Find("3D Scene");
                room3D = room != null ? room.gameObject : null;
                ui = root.Find("UI");
                contract = ui != null ? ui.Find("PRF_ContractScreen_WorldSpace") as RectTransform : null;
                var later = root.Find("PRF_OnboardingScene");
                onboarding = later != null ? later.gameObject : null;
                flight = root.Find("PRF_CinematicIntroCanvas");
                picture = room != null ? room.Find("Background_Main") : null;
                var model = room != null ? room.Find("PRF_ApartmentComputer") : null;
                computer = model != null ? model.GetComponent<Renderer>() : null;
                Plugin.Log.LogInfo($"Contract desk: the contract scene is there (apartment {Name(room)}, contract {Name(contract)}, onboarding {Name(later)}, flight video {Name(flight)}, picture {Name(picture)}, computer {Name(model)})");
                return room3D != null;
            }
            return false;
        }

        private static string Name(Transform found) => found != null ? "found" : "MISSING";

        // A canvas the game shows: on, not shrunk away and not faded out (the game's views hide in those ways).
        private static bool Visible(Transform view)
        {
            if (view == null || !view.gameObject.activeInHierarchy) return false;
            var canvas = view.GetComponent<Canvas>();
            if (canvas != null && !canvas.enabled) return false;
            if (Mathf.Abs(view.lossyScale.x) < 1e-5f) return false;
            var group = view.GetComponent<CanvasGroup>();
            return group == null || group.alpha > 0.01f;
        }

        // The desk from the game's prefabs: loaded, then copied; false until it stands.
        private static bool Build()
        {
            if (loadFailed) return false;
            if (!loading)
            {
                loading = true;
                try
                {
                    roomLoad = Addressables.LoadAssetAsync<GameObject>(RoomKey);
                    chairLoad = Addressables.LoadAssetAsync<GameObject>(ChairKey);
                    Plugin.Log.LogInfo("Contract desk: loading the Hab's desk and chair");
                }
                catch (System.Exception e)
                {
                    loadFailed = true;
                    Plugin.Log.LogWarning($"Contract desk: the Hab's models cannot be loaded, the contract stays as the game has it: {e.Message}");
                }
                return false;
            }
            if (!roomLoad.IsDone || !chairLoad.IsDone) return false;
            if (roomLoad.Status != AsyncOperationStatus.Succeeded || roomLoad.Result == null)
            {
                loadFailed = true;
                Plugin.Log.LogWarning($"Contract desk: '{RoomKey}' did not load, the contract stays as the game has it");
                return false;
            }
            desk = new GameObject("ShipbreakerVR contract desk");
            desk.SetActive(false);
            var room = roomLoad.Result.transform;
            var toRoom = room.worldToLocalMatrix;
            ReadScreen(room, toRoom);
            var parts = 0;
            var group = Descendant(room, DeskGroup);
            if (group != null) parts += Copy(group, toRoom, true);
            var terminal = Descendant(room, Terminal);
            if (terminal != null) parts += Copy(terminal, toRoom, false);
            var chairs = 0;
            if (chairLoad.Status == AsyncOperationStatus.Succeeded && chairLoad.Result != null)
            {
                var chair = chairLoad.Result.transform;
                chairs = Copy(chair, Matrix4x4.TRS(ChairAt, ChairTurn, Vector3.one) * chair.worldToLocalMatrix, false);
            }
            Plugin.Log.LogInfo($"Contract desk: built from the Hab's models: {parts} part(s) of desk and terminal (desk group {Name(group)}, terminal {Name(terminal)}), {chairs} of the chair; "
                + $"screen {screenSize.x * 100f:0} x {screenSize.y * 100f:0} cm at {screenCenter.x:0.00} {screenCenter.y:0.00} {screenCenter.z:0.00} in the room's axes");
            if (parts > 0) return true;
            loadFailed = true;
            Object.Destroy(desk);
            desk = null;
            Plugin.Log.LogWarning("Contract desk: no part of the desk found in the Hab's prefab, the contract stays as the game has it");
            return false;
        }

        private static readonly Vector3[] corners = new Vector3[4];

        // The terminal's screen in the room's axes: the rectangle of the canvas that shows its screensaver, or the
        // measured one without it.
        private static void ReadScreen(Transform room, Matrix4x4 toRoom)
        {
            screenCenter = ScreenAt;
            screenTurn = Quaternion.LookRotation(ScreenInto.normalized, ScreenUp.normalized);
            screenSize = ScreenSize;
            var terminal = Descendant(room, Terminal);
            var mark = terminal != null ? Descendant(terminal, ScreenMark) : null;
            var canvas = mark != null ? mark.GetComponentInChildren<Canvas>(true) : null;
            var rect = canvas != null ? canvas.transform as RectTransform : null;
            if (rect == null)
            {
                Plugin.Log.LogInfo($"Contract desk: the terminal's screen canvas is not in the prefab (terminal {Name(terminal)}, screensaver {Name(mark)}), taking the measured screen");
                return;
            }
            rect.GetLocalCorners(corners);
            var toRoot = toRoom * rect.localToWorldMatrix;
            var bottomLeft = toRoot.MultiplyPoint3x4(corners[0]);
            var topLeft = toRoot.MultiplyPoint3x4(corners[1]);
            var topRight = toRoot.MultiplyPoint3x4(corners[2]);
            var bottomRight = toRoot.MultiplyPoint3x4(corners[3]);
            var across = bottomRight - bottomLeft;
            var up = topLeft - bottomLeft;
            if (across.sqrMagnitude < 1e-4f || up.sqrMagnitude < 1e-4f)
            {
                Plugin.Log.LogInfo($"Contract desk: the terminal's screen canvas has no size in the prefab ({across.magnitude:0.000} x {up.magnitude:0.000} m), taking the measured screen");
                return;
            }
            screenCenter = (bottomLeft + topRight) * 0.5f;
            screenTurn = Quaternion.LookRotation(Vector3.Cross(across, up), up);
            screenSize = new Vector2(across.magnitude, up.magnitude);
        }

        // A plain copy of every mesh under that part of a prefab, placed by that matrix in the desk's axes; how many.
        private static int Copy(Transform source, Matrix4x4 toDesk, bool nearScreenOnly)
        {
            var made = 0;
            foreach (var filter in source.GetComponentsInChildren<MeshFilter>(true))
            {
                var renderer = filter.GetComponent<MeshRenderer>();
                if (filter.sharedMesh == null || renderer == null || !renderer.enabled || !OnWithin(filter.transform, source) || LowerDetail(renderer)) continue;
                var matrix = toDesk * filter.transform.localToWorldMatrix;
                Vector3 at = matrix.GetColumn(3);
                if (nearScreenOnly && Vector3.Distance(at, screenCenter) > DeskAround) continue;
                var copy = new GameObject(filter.name);
                copy.transform.SetParent(desk.transform, false);
                copy.transform.localPosition = at;
                copy.transform.localRotation = matrix.rotation;
                copy.transform.localScale = matrix.lossyScale;
                copy.AddComponent<MeshFilter>().sharedMesh = filter.sharedMesh;
                var drawn = copy.AddComponent<MeshRenderer>();
                drawn.sharedMaterials = renderer.sharedMaterials;
                drawn.shadowCastingMode = renderer.shadowCastingMode;
                made++;
            }
            return made;
        }

        // Switched on from that part of the prefab down (a prefab is not in a scene, so activeInHierarchy does not tell).
        private static bool OnWithin(Transform part, Transform top)
        {
            for (var each = part; each != null; each = each.parent)
            {
                if (!each.gameObject.activeSelf) return false;
                if (each == top) break;
            }
            return true;
        }

        // A renderer of a coarser level of detail: only the finest of a group is copied.
        private static bool LowerDetail(Renderer renderer)
        {
            var group = renderer.GetComponentInParent<LODGroup>();
            if (group == null) return false;
            var levels = group.GetLODs();
            return levels.Length > 0 && levels[0].renderers != null && System.Array.IndexOf(levels[0].renderers, renderer) < 0;
        }

        private static Transform Descendant(Transform top, string name)
        {
            if (top.name == name) return top;
            for (var i = 0; i < top.childCount; i++)
            {
                var found = Descendant(top.GetChild(i), name);
                if (found != null) return found;
            }
            return null;
        }

        // The desk put in front of the player: on the playspace's floor, its screen's middle Distance ahead of the head,
        // level, facing them. Ahead is where the head looks, level (Raik: "it must stand straight in front of me, the
        // panel centered"; by where the playspace faces it was off to the side for a head turned a little).
        private static void Show(VrCamera vr)
        {
            Place(vr);
            desk.SetActive(true);
            shown = true;
            if (computer != null) computer.enabled = false;
            Stretch(vr.transform.position);
        }

        private static void Place(VrCamera vr)
        {
            var head = vr.transform;
            var rig = head.parent != null ? head.parent : head;
            var up = rig.up;
            var scale = rig.lossyScale.x;
            var ahead = Vector3.ProjectOnPlane(head.forward, up);
            if (ahead.sqrMagnitude < 0.01f) ahead = Vector3.ProjectOnPlane(rig.forward, up);
            ahead.Normalize();
            // The screen's side towards the viewer, level, in the desk's axes.
            var towards = Vector3.ProjectOnPlane(-(screenTurn * Vector3.forward), Vector3.up);
            if (towards.sqrMagnitude < 1e-4f) towards = Vector3.right;
            var root = desk.transform;
            root.rotation = Quaternion.LookRotation(-ahead, up) * Quaternion.Inverse(Quaternion.LookRotation(towards.normalized, Vector3.up));
            root.localScale = Vector3.one * scale;
            var floor = head.parent != null ? rig.TransformPoint(new Vector3(head.localPosition.x, 0f, head.localPosition.z)) : head.position - up * 1.6f * scale;
            var target = floor + ahead * (Distance * scale) + up * ((screenCenter.y - FloorAt) * scale);
            root.position = target - root.rotation * (screenCenter * scale);
            placedFor = headBefore = head.localPosition;
            stillSince = Time.unscaledTime;
            if (logs++ < 60)
                Plugin.Log.LogInfo($"Contract desk: placed, the screen {Distance * 100f:0} cm ahead of the head and {(screenCenter.y - FloorAt) * 100f:0} cm above the floor (the head is {Vector3.Dot(head.position - floor, up) / scale * 100f:0} cm above it)");
        }

        // The head moved away from where the desk was placed for, and has come to rest: the desk is placed again.
        private static void Follow(VrCamera vr)
        {
            var at = vr.transform.localPosition;
            var dt = Time.unscaledDeltaTime;
            var step = at - headBefore;
            headBefore = at;
            step.y = 0f;
            if (dt <= 0f || step.magnitude / dt > StillBelow) stillSince = Time.unscaledTime;
            var moved = at - placedFor;
            moved.y = 0f;
            if (moved.magnitude < MovedFrom || Time.unscaledTime - stillSince < StillFor) return;
            Place(vr);
        }

        // The apartment's picture as wide as PictureWide from the head, where it is; never smaller than it was.
        private static void Stretch(Vector3 from)
        {
            var sprite = picture != null ? picture.GetComponent<SpriteRenderer>() : null;
            if (sprite == null || pictureStretched) return;
            var width = sprite.bounds.size.x;
            var far = Vector3.Distance(from, sprite.bounds.center);
            if (width <= 0.01f || far <= 0.01f) return;
            var factor = 2f * far * Mathf.Tan(PictureWide * 0.5f * Mathf.Deg2Rad) / width;
            if (factor <= 1f) return;
            pictureScale = picture.localScale;
            pictureStretched = true;
            picture.localScale = new Vector3(pictureScale.x * factor, pictureScale.y * factor, pictureScale.z);
            Plugin.Log.LogInfo($"Contract desk: the apartment's picture ({width:0.0} m wide, {far:0.0} m away) is stretched {factor:0.0} times");
        }

        // Every frame while shown: the contract's canvas on the terminal's screen, as large as fits. Its parent ('UI')
        // is moved, so what the game does with the canvas itself (its views fade and scale) stays as it is; the canvas
        // counts as it rests in its parent, at full size.
        private static void Keep()
        {
            if (ui == null || contract == null || !Screen(out var center, out var rotation, out var size)) return;
            // On the terminal's face: Screen gives the place BeforeGlass behind it.
            center -= rotation * Vector3.forward * (BeforeGlass + ContractBefore);
            if (!uiMoved)
            {
                uiMoved = true;
                uiPosition = ui.localPosition;
                uiRotation = ui.localRotation;
                uiScale = ui.localScale;
                Plugin.Log.LogInfo($"Contract desk: the contract ({contract.rect.width:0} x {contract.rect.height:0}) goes onto the screen ({size.x * 100f:0} x {size.y * 100f:0} cm)");
            }
            var rect = contract.rect;
            if (rect.width <= 1f || rect.height <= 1f) return;
            var fit = Mathf.Min(size.x / rect.width, size.y / rect.height);
            var parentScale = ui.parent != null ? ui.parent.lossyScale : Vector3.one;
            ui.rotation = rotation * Quaternion.Inverse(contract.localRotation);
            ui.localScale = new Vector3(fit / Mathf.Max(Mathf.Abs(parentScale.x), 1e-6f), fit / Mathf.Max(Mathf.Abs(parentScale.y), 1e-6f), uiScale.z);
            // The middle of the canvas' rectangle, as it rests in its parent, onto the screen's middle.
            var resting = contract.localPosition + contract.localRotation * (Vector3)rect.center;
            ui.position = center - ui.rotation * Vector3.Scale(resting, ui.lossyScale);
        }

        private static void Hide()
        {
            if (!shown) return;
            shown = false;
            if (desk != null) desk.SetActive(false);
            Restore();
            Plugin.Log.LogInfo("Contract desk: hidden");
        }

        // The game's scene as it was.
        private static void Restore()
        {
            if (uiMoved && ui != null)
            {
                ui.localPosition = uiPosition;
                ui.localRotation = uiRotation;
                ui.localScale = uiScale;
            }
            uiMoved = false;
            if (pictureStretched && picture != null) picture.localScale = pictureScale;
            pictureStretched = false;
            if (computer != null) computer.enabled = true;
        }

        // On unload, and when the front end is left: the desk gone, the prefabs let go.
        public static void Release()
        {
            shown = false;
            Restore();
            if (desk != null) Object.Destroy(desk);
            desk = null;
            if (loading)
            {
                try
                {
                    if (roomLoad.IsValid()) Addressables.Release(roomLoad);
                    if (chairLoad.IsValid()) Addressables.Release(chairLoad);
                }
                catch (System.Exception e)
                {
                    Plugin.Log.LogInfo($"Contract desk: letting the Hab's models go failed: {e.Message}");
                }
            }
            loading = false;
            loadFailed = false;
            scene = null;
            room3D = null;
            ui = null;
            contract = null;
            onboarding = null;
            flight = null;
            picture = null;
            computer = null;
            stateLogged = null;
        }
    }
}
