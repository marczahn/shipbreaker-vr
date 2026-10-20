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
        public const string Version = "0.7.0";

        internal static ManualLogSource Log;
        internal static ConfigEntry<bool> MultiPass;
        internal static ConfigEntry<bool> SeparateEyeTextures;
        internal static ConfigEntry<bool> BodyFollowsHead;
        internal static ConfigEntry<bool> UiInVr;
        internal static ConfigEntry<float> HudDistance;
        internal static ConfigEntry<float> HudScale;
        internal static ConfigEntry<float> MenuDistance;
        internal static ConfigEntry<float> MenuWidth;
        internal static ConfigEntry<bool> FlipUi;
        internal static ConfigEntry<bool> ShowUiFrame;
        internal static ConfigEntry<VrHelmet.Mode> Helmet;
        internal static ConfigEntry<VrCamera.AntiAliasing> AntiAliasing;
        internal static ConfigEntry<float> ResolutionScale;
        internal static ConfigEntry<float> WorldScale;

        private void Awake()
        {
            Log = Logger;
            MultiPass = Config.Bind("Rendering", "MultiPass", true,
                "Render each eye in its own pass. Single Pass Instanced needs shader variants the game lacks (result: only one eye, distorted image).");
            SeparateEyeTextures = Config.Bind("Rendering", "SeparateEyeTextures", false,
                "A separate texture per eye instead of a Texture2DArray. Experimental, in case MultiPass alone is not enough.");
            BodyFollowsHead = Config.Bind("Steuerung", "KoerperFolgtKopf", true,
                "The body, and with it the tools' aim, turns with the head. Mouse and stick add extra rotation. Off: the head looks around freely, only the mouse turns the body.");
            UiInVr = Config.Bind("Anzeige", "UiImRaum", true,
                "Show HUD and menus on a canvas in the world instead of flat over the image.");
            HudDistance = Config.Bind("Anzeige", "HudAbstand", 1.5f, "Distance of the visor display in front of the eyes, in meters.");
            HudScale = Config.Bind("Anzeige", "VisierGroesse", 0.55f,
                "Size of the visor display relative to the game camera's field of view. 1 = markers sit exactly on objects, but the corners are outside the field of view. Smaller = everything in view, but markers are slightly offset.");
            MenuDistance = Config.Bind("Anzeige", "MenueAbstand", 2.0f, "Distance of the menu canvas in meters.");
            MenuWidth = Config.Bind("Anzeige", "MenueBreite", 2.6f, "Width of the menu canvas in meters.");
            ShowUiFrame = Config.Bind("Anzeige", "UiFlaecheZeigen", true,
                "Tint the UI canvas light blue so you can see where it is. For testing.");
            FlipUi = Config.Bind("Anzeige", "UiSpiegeln", false, "Flip the UI vertically in case it is upside down.");
            Helmet = Config.Bind("Anzeige", "Helm", VrHelmet.Mode.Kopf,
                "Kopf: the helmet follows the VR head. Aus: hide the helmet model. Spiel: the helmet stays on the body.");
            AntiAliasing = Config.Bind("Bild", "Kantenglaettung", VrCamera.AntiAliasing.SMAA,
                "SMAA: smooths edges, stays sharp. TAA: looks blurry in VR and makes objects jitter. FXAA: cheap and soft. Keine: none. Spiel: use the game's setting.");
            ResolutionScale = Config.Bind("Bild", "Aufloesung", 1.0f,
                "Render resolution per eye relative to the headset's default. Above 1 smooths further but costs significant performance.");
            WorldScale = Config.Bind("Bild", "Weltmassstab", 1.0f,
                "Size of the player relative to the world (0.5 to 3). Above 1 widens eye distance and head movement: distant objects keep more depth and their size is easier to judge, but the world looks smaller. Below 1 the opposite.");
            Log.LogInfo($"{Name} {Version} loaded. Unity {Application.unityVersion}, game {Application.version}");
            // Apply each patch separately so one failing patch doesn't take down all the others (and F3).
            var harmony = new Harmony(Guid);
            foreach (var type in AccessTools.GetTypesFromAssembly(typeof(Plugin).Assembly))
            {
                try { harmony.CreateClassProcessor(type).Patch(); }
                catch (System.Exception e) { Log.LogError($"Patch {type.Name} failed: {e}"); }
            }
        }

        private float nextCameraSearch;

        // The camera currently producing the image: active, no target texture, highest depth.
        private static Camera FindMenuCamera()
        {
            Camera best = null;
            foreach (var cam in Camera.allCameras)
            {
                if (cam.targetTexture != null || cam.CompareTag("UI_Camera") || cam.name.StartsWith("ShipbreakerVR")) continue;
                if (best == null || cam.depth > best.depth) best = cam;
            }
            return best;
        }

        private void Update()
        {
            if (Input.GetKeyDown(KeyCode.F3)) VrXr.Toggle();

            // The main menu has no LynxCameraController yet; the VR camera then attaches to the menu camera.
            if (VrXr.IsRunning && VrCamera.Instance == null && Time.unscaledTime >= nextCameraSearch)
            {
                nextCameraSearch = Time.unscaledTime + 1f;
                var cam = LynxCameraController.MainCamera != null ? LynxCameraController.MainCamera : FindMenuCamera();
                if (cam != null)
                {
                    Log.LogInfo($"VR camera attached to '{cam.name}'");
                    VrCamera.Attach(cam);
                }
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
            if (cam == null) return;
            if (VrCamera.Instance != null && VrCamera.Instance.GameCamera != cam) VrCamera.Instance.Detach();
            if (VrCamera.Instance == null) VrCamera.Attach(cam);
        }
    }
}
