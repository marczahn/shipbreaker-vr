using System.Collections.Generic;
using BBI.Unity.Game;
using UnityEngine;
using UnityEngine.AddressableAssets;
using UnityEngine.EventSystems;
using UnityEngine.ResourceManagement.AsyncOperations;
using UnityEngine.UI;

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
        // The apartment's picture stands straight behind the desk, as far from the head as the game has it from its
        // camera, its middle at the head's height, and is stretched until it is this wide as seen from the head, in
        // degrees. Raik's tests: first only stretched where it was (120 degrees), but the desk goes where the head
        // looks, so the picture stood askew behind it, and it was a little too large.
        private const float PictureWide = 100f;
        // The contract's canvas and what lies on it (the mail, the news, the contract's window, its pop-up) are layered
        // in depth, in meters whatever the canvas' scale: fine for the game's one camera, but in VR the contract's
        // window floated centimeters before the glass (Raik's picture of 2026-11-24). Their depth is pressed flat to
        // this share.
        private const float Flat = 0.02f;
        private const float SearchEvery = 1f;

        // The game's side: the contract scene's root and what of it is used.
        private static Transform scene;
        private static GameObject room3D;
        private static Transform ui;
        private static RectTransform contract;
        private static GameObject onboarding;
        private static Transform flight;
        private static float nextSearch;
        // What darkened the apartment as it came (Raik 2026-11-24: "a blue background when you come into the room, it
        // stays too long"): the dark background of the intro's text, and the scene's fade from black ('Fade Screen',
        // VrCanvases.FadeNames). On the menu canvas fixed in the room both were a dark rectangle with the room around
        // it. The background is switched off while the desk is shown; the fade from black is not darkened in VR once
        // the desk is there (SkipsFade), until the game's fade has run out or for FadeInAtMost seconds. A fade to black
        // later (the contract done) darkens the view as it should.
        private static Graphic introBackground;
        private static bool introBackgroundWas, introHidden;
        private static Transform fadeScreen;
        private static bool fadeInSkipped;
        private static float fadeInSince;
        private const float FadeInAtMost = 8f;
        // The contract's account name, typed with VrDeskKeyboard.
        private static TMPro.TMP_InputField nameField;
        // Stepping on through the contract (Advance): what was selected last frame and whether it was ticked, the
        // button listened to, whether it was clicked, and what to step on from, and when.
        private static GameObject selectedBefore, stepFrom;
        private static bool tickedBefore, clicked;
        private static Button listening;
        private static float stepAt;
        private const float StepAfter = 0.15f;

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
        // The shader the desk's parts are drawn with, for the mod's own things at the desk (VrDeskKeyboard's pens).
        internal static Shader DeskShader { get; private set; }
        // Where the head was (in the playspace) when the desk was placed, where it was last frame, and since when it
        // is still.
        private static Vector3 placedFor, headBefore;
        private static float stillSince;

        // What is changed of the game's scene while the desk is shown, to put it back.
        private static bool uiMoved, pictureStretched;
        private static Vector3 uiPosition, uiScale, pictureScale, picturePosition;
        private static Quaternion uiRotation, pictureRotation;
        private static float pictureWidth, pictureFar;
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
            VrContractSubmit.Update();
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
            FadeIn();
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
                nameField = HarmonyLib.Traverse.Create(check).Field("m_RequiredInputField").GetValue<TMPro.TMP_InputField>();
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
                var background = root.Find("PRF_IntroCanvas/Image - Background");
                introBackground = background != null ? background.GetComponent<Graphic>() : null;
                fadeScreen = root.Find("Fade Screen");
                Plugin.Log.LogInfo($"Contract desk: the contract scene is there (apartment {Name(room)}, contract {Name(contract)}, onboarding {Name(later)}, flight video {Name(flight)}, picture {Name(picture)}, computer {Name(model)})");
                return room3D != null;
            }
            return false;
        }

        private static string Name(Transform found) => found != null ? "found" : "MISSING";

        // While a new career starts in the front end: the apartment or the flight video is there. VrRadio waits with
        // reading its model until then (2.7 MB of texture at once, a halt of most of a second when the account's name
        // was confirmed, Raik 2026-11-24), for the loading screen that follows.
        // Whether the desk stands in the apartment now.
        internal static bool Shown => shown;

        internal static bool CareerStarting => scene != null && ((room3D != null && room3D.activeInHierarchy) || Visible(flight));

        // For VrCanvases.FadeAlpha: the scene's fade from black, not darkening the view while the desk has come.
        internal static bool SkipsFade(Canvas canvas) => fadeInSkipped && shown && fadeScreen != null && canvas != null && canvas.transform == fadeScreen;

        // How dark a part of the scene makes what lies behind it, 0 to 1 (as VrCanvases.FadeAlpha counts it).
        private static readonly List<Graphic> graphics = new List<Graphic>();

        private static float Darkness(Transform part)
        {
            if (part == null || !part.gameObject.activeInHierarchy) return 0f;
            var darkest = 0f;
            part.GetComponentsInChildren(graphics);
            foreach (var graphic in graphics)
            {
                if (!graphic.enabled) continue;
                var c = graphic.color;
                var darkness = 1f - Mathf.Max(c.r, Mathf.Max(c.g, c.b));
                darkest = Mathf.Max(darkest, c.a * darkness * graphic.canvasRenderer.GetAlpha() * VrUtil.GroupAlpha(graphic.transform));
            }
            return darkest;
        }

        // While shown: the scene's fade from black let run out without darkening the view.
        private static void FadeIn()
        {
            if (!fadeInSkipped) return;
            var dark = Darkness(fadeScreen);
            var since = Time.unscaledTime - fadeInSince;
            if (dark > 0.02f && since < FadeInAtMost) return;
            fadeInSkipped = false;
            Plugin.Log.LogInfo($"Contract desk: the scene's fade from black is over after {since:0.0} s ({(dark > 0.02f ? $"still {dark:0.00} dark, the view darkens with it again" : "run out")})");
        }

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
                if (DeskShader == null && renderer.sharedMaterial != null) DeskShader = renderer.sharedMaterial.shader;
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
            var intro = introBackground != null ? Darkness(introBackground.transform) : 0f;
            var fade = Darkness(fadeScreen);
            Plugin.Log.LogInfo($"Contract desk: as the desk comes, the intro's background is {(introBackground == null ? "not found" : $"{intro:0.00} dark")}, the scene's fade {(fadeScreen == null ? "not found" : $"{fade:0.00} dark")}");
            if (introBackground != null && !introHidden)
            {
                introHidden = true;
                introBackgroundWas = introBackground.enabled;
                introBackground.enabled = false;
            }
            fadeInSkipped = fadeScreen != null;
            fadeInSince = Time.unscaledTime;
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
            Picture(head.position, ahead, up, scale);
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

        // The apartment's picture straight behind the desk: ahead of the head as far as it was from the game's camera,
        // its middle at the head's height, facing the player, as wide as PictureWide from there.
        private static void Picture(Vector3 from, Vector3 ahead, Vector3 up, float scale)
        {
            var sprite = picture != null ? picture.GetComponent<SpriteRenderer>() : null;
            if (sprite == null) return;
            if (!pictureStretched)
            {
                var camera = Camera.main != null ? Camera.main.transform.position : from;
                pictureWidth = sprite.bounds.size.x;
                pictureFar = Vector3.Distance(camera, sprite.bounds.center);
                if (pictureWidth <= 0.01f || pictureFar <= 0.01f) return;
                pictureStretched = true;
                pictureScale = picture.localScale;
                picturePosition = picture.position;
                pictureRotation = picture.rotation;
            }
            var factor = 2f * pictureFar * Mathf.Tan(PictureWide * 0.5f * Mathf.Deg2Rad) / pictureWidth;
            picture.localScale = new Vector3(pictureScale.x * factor, pictureScale.y * factor, pictureScale.z);
            picture.rotation = Quaternion.LookRotation(ahead, up);
            // The sprite's middle may lie off its transform.
            picture.position += from + ahead * pictureFar - sprite.bounds.center;
            if (logs++ < 60)
                Plugin.Log.LogInfo($"Contract desk: the apartment's picture ({pictureWidth:0.0} m wide, {pictureFar:0.0} m from the game's camera) stands behind the desk, stretched {factor:0.0} times");
        }

        // Every frame while shown: the contract's canvas on the terminal's screen, as large as fits. Its parent ('UI')
        // is moved, so what the game does with the canvas itself (its views fade and scale) stays as it is; the canvas
        // counts as it rests in its parent, at full size.
        private static void Keep()
        {
            if (ui == null || contract == null || !Screen(out var center, out var rotation, out var size)) return;
            VrDeskKeyboard.Update(Visible(contract) ? nameField : null, center - rotation * Vector3.forward * BeforeGlass, rotation, size, desk.transform.lossyScale.x);
            Advance();
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
            Depths();
            var fit = Mathf.Min(size.x / rect.width, size.y / rect.height);
            var parentScale = ui.parent != null ? ui.parent.lossyScale : Vector3.one;
            ui.rotation = rotation * Quaternion.Inverse(contract.localRotation);
            ui.localScale = new Vector3(fit / Mathf.Max(Mathf.Abs(parentScale.x), 1e-6f), fit / Mathf.Max(Mathf.Abs(parentScale.y), 1e-6f), uiScale.z * Flat);
            // The middle of the canvas' rectangle, as it rests in its parent, onto the screen's middle.
            var resting = contract.localPosition + contract.localRotation * (Vector3)rect.center;
            ui.position = center - ui.rotation * Vector3.Scale(resting, ui.lossyScale);
        }

        // Raik (2026-11-23): "whenever a tick is set or a button pressed, it should go on to the next row, the next
        // switch to choose or fill in, so one can simply click through". A toggle of the contract that was just ticked,
        // or a button of it that was just clicked: if the game leaves the selection on it, the next thing below it (or
        // to its right) is selected a moment later, past the other choices of the same group of toggles.
        private static void Advance()
        {
            var system = EventSystem.current;
            var selected = system != null ? system.currentSelectedGameObject : null;
            // The name confirmed and the game's work waiting for the dark (VrContractSubmit): the next page comes
            // with it, and the game selects on it itself.
            if (VrContractSubmit.Busy)
            {
                stepFrom = null;
                clicked = false;
                return;
            }
            if (stepFrom != null && Time.unscaledTime >= stepAt)
            {
                var from = stepFrom;
                stepFrom = null;
                if (selected == from && from.activeInHierarchy)
                {
                    var next = Next(from.GetComponent<Selectable>());
                    if (next != null)
                    {
                        system.SetSelectedGameObject(next.gameObject);
                        if (logs++ < 60) Plugin.Log.LogInfo($"Contract desk: on from '{from.name}' to '{next.name}'");
                        return;
                    }
                }
            }
            if (selected != selectedBefore)
            {
                if (listening != null) listening.onClick.RemoveListener(Clicked);
                listening = null;
                clicked = false;
                selectedBefore = selected;
                if (selected == null || contract == null || !selected.transform.IsChildOf(contract)) return;
                var toggle = selected.GetComponent<Toggle>();
                tickedBefore = toggle != null && toggle.isOn;
                listening = selected.GetComponent<Button>();
                if (listening != null) listening.onClick.AddListener(Clicked);
                return;
            }
            if (selected == null || contract == null || !selected.transform.IsChildOf(contract)) return;
            var ticked = selected.GetComponent<Toggle>();
            var done = clicked || (ticked != null && ticked.isOn && !tickedBefore);
            if (ticked != null) tickedBefore = ticked.isOn;
            clicked = false;
            if (!done) return;
            stepFrom = selected;
            stepAt = Time.unscaledTime + StepAfter;
        }

        private static void Clicked() => clicked = true;

        // What is selected after that: below it, else to its right; not another choice of the same group.
        private static Selectable Next(Selectable from)
        {
            if (from == null) return null;
            var toggle = from as Toggle;
            var group = toggle != null ? toggle.group : null;
            var each = from;
            for (var i = 0; i < 8; i++)
            {
                var next = each.FindSelectableOnDown() ?? each.FindSelectableOnRight();
                if (next == null || next == from) return null;
                var other = next as Toggle;
                if (group == null || other == null || other.group != group) return next;
                each = next;
            }
            return null;
        }

        // For the log, while something of the contract lies off its canvas in depth: what, and how far (in the canvas'
        // own depth, meters before it was pressed flat). A few times, when the deepest changes.
        private static float depthLogged;
        private static int depthLogs;

        private static void Depths()
        {
            if (depthLogs >= 8) return;
            var deepest = 0f;
            Transform which = null;
            foreach (var each in contract.GetComponentsInChildren<Transform>(false))
            {
                if (each == contract) continue;
                var depth = contract.InverseTransformPoint(each.position).z;
                if (Mathf.Abs(depth) <= Mathf.Abs(deepest)) continue;
                deepest = depth;
                which = each;
            }
            if (which == null || Mathf.Abs(deepest - depthLogged) < 0.002f) return;
            depthLogged = deepest;
            depthLogs++;
            Plugin.Log.LogInfo($"Contract desk: '{which.name}' lies {deepest * 100f:0.0} cm off the contract's canvas in depth (pressed flat to {Flat * 100f:0} % of that)");
        }

        private static void Hide()
        {
            if (!shown) return;
            shown = false;
            VrDeskKeyboard.Hide();
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
            if (pictureStretched && picture != null)
            {
                picture.localScale = pictureScale;
                picture.position = picturePosition;
                picture.rotation = pictureRotation;
            }
            pictureStretched = false;
            if (computer != null) computer.enabled = true;
            if (introHidden && introBackground != null) introBackground.enabled = introBackgroundWas;
            introHidden = false;
            fadeInSkipped = false;
            if (listening != null) listening.onClick.RemoveListener(Clicked);
            listening = null;
            selectedBefore = stepFrom = null;
            clicked = false;
        }

        // On unload, and when the front end is left: the desk gone, the prefabs let go.
        public static void Release()
        {
            shown = false;
            VrDeskKeyboard.Hide();
            nameField = null;
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
            DeskShader = null;
            scene = null;
            room3D = null;
            ui = null;
            contract = null;
            onboarding = null;
            flight = null;
            picture = null;
            computer = null;
            introBackground = null;
            fadeScreen = null;
            stateLogged = null;
        }
    }
}
