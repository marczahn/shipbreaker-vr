using BBI.Unity.Game;
using BepInEx;
using BepInEx.Configuration;
using BepInEx.Logging;
using HarmonyLib;
using UnityEngine;

namespace ShipbreakerVR
{
    [BepInPlugin(Guid, Name, Version)]
    public class Plugin : BaseUnityPlugin
    {
        public const string Guid = "de.marczahn.shipbreakervr";
        public const string Name = "ShipbreakerVR";
        public const string Version = "0.15.0";

        internal static ManualLogSource Log;
        internal static ConfigEntry<bool> MultiPass;
        internal static ConfigEntry<bool> SeparateEyeTextures;
        internal static ConfigEntry<bool> StartVrAtLaunch;
        internal static ConfigEntry<bool> BodyFollowsHead;
        internal static ConfigEntry<bool> UiInVr;
        internal static ConfigEntry<float> HudDistance;
        internal static ConfigEntry<float> HudScale;
        internal static ConfigEntry<VrUi.Steadying> HudSteadying;
        internal static ConfigEntry<float> MenuDistance;
        internal static ConfigEntry<float> MenuWidth;
        internal static ConfigEntry<bool> FlipUi;
        internal static ConfigEntry<bool> MonitorLook;
        internal static ConfigEntry<VrHelmet.Mode> Helmet;
        internal static ConfigEntry<VrCamera.AntiAliasing> AntiAliasing;
        internal static ConfigEntry<float> ResolutionScale;
        internal static ConfigEntry<float> StationResolution;
        internal static ConfigEntry<float> WorldScale;
        internal static ConfigEntry<float> HeadHeight;
        internal static ConfigEntry<float> HeadSideways;
        internal static ConfigEntry<VrPerformance.Occlusion> AmbientOcclusion;
        internal static ConfigEntry<VrControls.Scheme> ControlScheme;
        internal static ConfigEntry<VrControls.Turning> TurnMode;
        internal static ConfigEntry<VrFlightGlobe.Mode> FlightGlobe;
        internal static ConfigEntry<float> FlightGlobeSize;
        internal static ConfigEntry<float> FlightGlobeX;
        internal static ConfigEntry<float> FlightGlobeY;
        internal static ConfigEntry<bool> AimWithHand;

        private void Awake()
        {
            Log = Logger;
            MultiPass = Config.Bind("Rendering", "MultiPass", true,
                "Render each eye in its own pass. Single Pass Instanced needs shader variants the game lacks (result: only one eye, distorted image).");
            SeparateEyeTextures = Config.Bind("Rendering", "SeparateEyeTextures", false,
                "A separate texture per eye instead of a Texture2DArray. Experimental, in case MultiPass alone is not enough.");
            StartVrAtLaunch = Config.Bind("Rendering", "VrBeimStart", true,
                "Start VR as soon as the game launches, so the intro and loading screens already show in the headset. Off: start VR with F3.");
            BodyFollowsHead = Config.Bind("Steuerung", "KoerperFolgtKopf", true,
                "The body turns with the head (and with it the tools' aim, unless they aim with the hand, see ZielenMitHand). Mouse and stick add extra rotation. Off: the head looks around freely, only the mouse turns the body.");
            AmbientOcclusion = Config.Bind("Bild", "Umgebungsverdeckung", VrPerformance.Occlusion.Niedrig,
                "Ambient occlusion (shading in corners and creases) in VR. Niedrig: half resolution and few steps, as the game uses it in the bay. The station asks for full resolution and 16 steps, which at VR resolution costs so much GPU time that it stutters. Aus: off. Spiel: as the game sets it.");
            ControlScheme = Config.Bind("Steuerung", "Controllersteuerung", VrControls.Scheme.Vtol,
                "Flying with the motion controllers. Vtol: hold the left trigger and move the controller away from where it was when pressed to thrust that way, tilt it sideways to roll. Right stick: turn (left/right, see Drehen) and pitch (up/down). Y brakes. While the left hand holds a tool (ZielenMitHand) it works the tool instead. Aus: off.");
            TurnMode = Config.Bind("Steuerung", "Drehen", VrControls.Turning.Fliessend,
                "Turning with the right stick (controller flying). Fliessend: smooth, faster the further the stick is pushed. Snap30/Snap45/Snap90: one step of that many degrees per flick.");
            FlightGlobe = Config.Bind("Anzeige", "Flugkugel", VrFlightGlobe.Mode.Immer,
                "A small globe in the visor that shows where the left controller is relative to where the trigger was pressed (controller flying): its surface is full thrust, the small inner globe the deadzone. Immer: always in gameplay. BeimFliegen: only while the trigger is held. Aus: off.");
            FlightGlobeSize = Config.Bind("Anzeige", "FlugkugelGroesse", 0.06f, "Radius of the flight globe relative to the visor height.");
            FlightGlobeX = Config.Bind("Anzeige", "FlugkugelX", -0.159f, "Horizontal position of the flight globe on the visor, from -0.5 (left edge) to 0.5 (right edge).");
            FlightGlobeY = Config.Bind("Anzeige", "FlugkugelY", -0.38f, "Vertical position of the flight globe on the visor, from -0.5 (bottom edge) to 0.5 (top edge).");
            AimWithHand = Config.Bind("Steuerung", "ZielenMitHand", true,
                "The tools (cutter, grapple, scanner, demo charges), grabbing and interacting aim where the controller of the hand holding the tool points (the right one unless the left one drew it) instead of at the center of the view, from the muzzle of the tool in the hand. A laser shows where, its dot in the color of the game's crosshair, which is hidden from the center of the view meanwhile; with an empty hand there is no laser, and interacting aims at the center of the view, marked by the game's dot. The grip at the body draws a tool with either hand: right hip grapple, left hip demo charges, over the right shoulder cutter, right side of the helmet scanner; it stays in the hand while the grip is held, and reaching for it with the other hand takes it over. Trigger fires or grapples, A interacts (with the trigger held: throws), B with the trigger held reels in, B switches the tool mode (held: places a tether, held while pointing at nothing: recalls the tethers). In the left hand X and Y do what A and B do, and controller flying pauses until it lets go. The suit's hands follow the controllers.");
            VrToolFit.Bind(Config);
            UiInVr = Config.Bind("Anzeige", "UiImRaum", true,
                "Show HUD and menus on a canvas in the world instead of flat over the image.");
            HudDistance = Config.Bind("Anzeige", "HudAbstand", 1.5f, "Distance of the visor display in front of the eyes, in meters.");
            HudScale = Config.Bind("Anzeige", "VisierGroesse", 0.55f,
                "Size of the visor display relative to the game camera's field of view. 1 = markers sit exactly on objects, but the corners are outside the field of view. Smaller = everything in view, but markers are slightly offset.");
            HudSteadying = Config.Bind("Anzeige", "HudBeruhigung", VrUi.Steadying.Schwach,
                "Keeps the visor display steady instead of copying every small head movement. Schwach: ignores small tremor, follows the gaze quickly. Stark: stays put for larger movements and follows more slowly. Aus: fixed to the head. Turning the body (mouse, stick, controller) always applies at once.");
            MenuDistance = Config.Bind("Anzeige", "MenueAbstand", 2.0f, "Distance of the menu canvas in meters.");
            MenuWidth = Config.Bind("Anzeige", "MenueBreite", 2.6f, "Width of the menu canvas in meters.");
            FlipUi = Config.Bind("Anzeige", "UiSpiegeln", false, "Flip the UI vertically in case it is upside down.");
            MonitorLook = Config.Bind("Anzeige", "MonitorEffekt", true,
                "Screen look for the menus on the station's monitors: a little dimmer, fine scan lines, darker edges, a faint reflection on the glass and light on the frame. F7 switches it on and off.");
            Helmet = Config.Bind("Anzeige", "Helm", VrHelmet.Mode.Kopf,
                "Kopf: the helmet follows the VR head. Aus: hide the helmet model. Spiel: the helmet stays on the body.");
            AntiAliasing = Config.Bind("Bild", "Kantenglaettung", VrCamera.AntiAliasing.SMAA,
                "SMAA: smooths edges, stays sharp. TAA: looks blurry in VR and makes objects jitter. FXAA: cheap and soft. Keine: none. Spiel: use the game's setting.");
            ResolutionScale = Config.Bind("Bild", "Aufloesung", 1.0f,
                "Render resolution per eye relative to the headset's default (0.5 to 2). Above 1 smooths further but costs significant performance and video memory; it is limited to what should fit into the graphics card's memory. Applied one second after the last change.");
            StationResolution = Config.Bind("Bild", "AufloesungStation", 0.75f,
                "Render resolution per eye in the station between shifts (0.5 to 1), instead of Aufloesung. The station costs the graphics card more than the bay. If it misses the headset's frame rate, Virtual Desktop and other runtimes compute frames in between, which makes the view wobble close to the monitors; lower it until that stops (an RTX 4070 Ti at 2688x2880 per eye needed 0.7).");
            WorldScale = Config.Bind("Bild", "Weltmassstab", 1.0f,
                "Size of the player relative to the world (0.5 to 3). Above 1 widens eye distance and head movement: distant objects keep more depth and their size is easier to judge, but the world looks smaller. Below 1 the opposite.");
            HeadHeight = Config.Bind("Bild", "KopfHoehe", 0f,
                "Moves the eyes up (positive) or down (negative) against the game's head position, in centimeters (-10 to 10).");
            HeadSideways = Config.Bind("Bild", "KopfSeitlich", 0f,
                "Moves the eyes right (positive) or left (negative) against the game's head position, in centimeters (-10 to 10).");
            // Loaded again by ScriptEngine (F6) while the game runs: OpenXR and the Hab state carry over from the
            // previous instance, and VR must not be toggled off by the start-at-launch below.
            var reloaded = VrXr.Adopt();
            launched = reloaded;
            if (reloaded) VrHab.Adopt();
            Log.LogInfo($"{Name} {Version} loaded{(reloaded ? $" again (VR {(VrXr.IsRunning ? "running" : "off")})" : "")}. Unity {Application.unityVersion}, game {Application.version}");
            if (HeadHeight.Value != 0f || HeadSideways.Value != 0f) Log.LogInfo($"Head offset: {HeadHeight.Value:0} cm up, {HeadSideways.Value:0} cm right");
            // Apply each patch separately so one failing patch doesn't take down all the others (and F3).
            // ScriptEngine renames the assembly on every load; with that name in the ID, unloading an old instance
            // only removes its own patches.
            harmony = new Harmony($"{Guid}/{typeof(Plugin).Assembly.GetName().Name}");
            foreach (var type in AccessTools.GetTypesFromAssembly(typeof(Plugin).Assembly))
            {
                try { harmony.CreateClassProcessor(type).Patch(); }
                catch (System.Exception e) { Log.LogError($"Patch {type.Name} failed: {e}"); }
            }
            Application.quitting += OnQuitting;
        }

        private Harmony harmony;
        private bool quitting;

        private void OnQuitting() => quitting = true;

        // ScriptEngine destroys the plugin before loading the new build. Everything the mod changed in the game goes
        // back to how the game had it, as when VR stops, so the new instance starts from the game's own state. OpenXR
        // keeps running; the new instance takes it over (VrXr.Adopt).
        private void OnDestroy()
        {
            Application.quitting -= OnQuitting;
            if (quitting) return;
            Log.LogInfo($"{Name} unloading");
            Unload("options tab", VrOptionsTab.RemoveAll);
            Unload("menu control", VrMenuInput.Detach);
            Unload("VR camera", VrCamera.Unload);
            Unload("frame statistics", VrPerformance.Detach);
            Unload("flight globe", VrFlightGlobe.Release);
            Unload("tool models", VrToolModels.Release);
            Unload("hands", VrHands.Release);
            Unload("crosshair", VrCrosshair.Release);
            Unload("aim camera", VrAim.Release);
            Unload("patches", () => harmony?.UnpatchSelf());
            Log.LogInfo($"{Name} unloaded");
            BepInEx.Logging.Logger.Sources.Remove(Log);
        }

        private static void Unload(string what, System.Action action)
        {
            try { action(); }
            catch (System.Exception e) { Log.LogError($"Unloading the {what} failed: {e}"); }
        }

        private float nextCameraSearch;
        private bool launched;

        // The camera currently producing the image: active, no target texture, highest depth. Not one that only draws
        // canvases, like the load screen camera: its volume layers hid our UI pass and it shows no world anyway.
        private static Camera FindMenuCamera()
        {
            Camera best = null;
            foreach (var cam in Camera.allCameras)
            {
                if (cam.targetTexture != null || cam.CompareTag("UI_Camera") || cam.name.StartsWith("ShipbreakerVR") || VrCamera.IsUiOnly(cam)) continue;
                // Only while the game shows the Hab (VrHab.Camera); a leftover Hab camera during a load is not a menu.
                if (VrHab.IsHabCamera(cam)) continue;
                if (best == null || cam.depth > best.depth) best = cam;
            }
            return best;
        }

        private void Update()
        {
            if (!launched)
            {
                launched = true;
                if (StartVrAtLaunch.Value) VrXr.Toggle();
            }
            if (Input.GetKeyDown(KeyCode.F3)) VrXr.Toggle();
            VrMenuInput.EnsureAttached();

            // The main menu has no LynxCameraController yet; the VR camera then attaches to the menu camera.
            // Without any camera (intro, loading screen) the rig still exists and shows the UI on black.
            // In the Hab the game shows the station through its own camera; back in gameplay the main camera takes over again.
            var vr = VrCamera.Instance;
            if (!VrXr.IsRunning) return;
            // The rig moves onto the Hab camera by itself (VrCamera.LateUpdate); here only when VR starts in the Hab.
            var hab = VrHab.Camera;
            if (hab != null)
            {
                if (vr == null)
                {
                    Log.LogInfo($"VR camera attached to the Hab camera '{hab.name}'");
                    VrCamera.Attach(hab);
                }
            }
            else if (Time.unscaledTime >= nextCameraSearch && (vr == null || vr.GameCamera == null || vr.FollowsHab))
            {
                nextCameraSearch = Time.unscaledTime + 0.25f;
                var cam = LynxCameraController.MainCamera != null ? LynxCameraController.MainCamera : FindMenuCamera();
                if (cam != null) Log.LogInfo($"VR camera attached to '{cam.name}'");
                if (cam != null || vr == null || vr.GameCamera != null) VrCamera.Attach(cam);
            }
        }
    }

    // Checks that we find the game camera: all tools aim via LynxCameraController.MainCamera.
    [HarmonyPatch(typeof(LynxCameraController), "Start")]
    internal static class LynxCameraControllerStartPatch
    {
        private static void Postfix()
        {
            var cam = LynxCameraController.MainCamera;
            Plugin.Log.LogInfo(cam == null
                ? "LynxCameraController started, but MainCamera is null"
                : $"Game camera found: '{cam.name}', FOV {cam.fieldOfView}, resolution {LynxCameraController.ScreenWidth}x{LynxCameraController.ScreenHeight}");
            // A new main camera starts switched on; the game turns it off again when it shows the Hab.
            VrHab.MainCameraOff = false;
            if (cam == null) return;
            if (VrCamera.Instance == null || VrCamera.Instance.GameCamera != cam) VrCamera.Attach(cam);
        }
    }
}
