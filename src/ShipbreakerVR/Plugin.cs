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
        public const string Version = "0.5.1";

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

        private void Awake()
        {
            Log = Logger;
            MultiPass = Config.Bind("Rendering", "MultiPass", true,
                "Jedes Auge in einem eigenen Durchgang rendern. Single Pass Instanced braucht Shader-Varianten, die dem Spiel fehlen (Folge: nur ein Auge, verzerrtes Bild).");
            SeparateEyeTextures = Config.Bind("Rendering", "SeparateEyeTextures", false,
                "Eine eigene Textur pro Auge statt eines Texture2DArray. Nur zum Ausprobieren, falls MultiPass allein nicht reicht.");
            BodyFollowsHead = Config.Bind("Steuerung", "KoerperFolgtKopf", true,
                "Die Figur und damit die Zielrichtung der Werkzeuge drehen sich mit dem Kopf. Maus und Stick drehen zusätzlich. Aus: Kopf schaut frei, nur die Maus dreht die Figur.");
            UiInVr = Config.Bind("Anzeige", "UiImRaum", true,
                "HUD und Menüs auf einer Leinwand im Raum zeigen statt flach über das Bild gelegt.");
            HudDistance = Config.Bind("Anzeige", "HudAbstand", 1.5f, "Abstand des Visier-Displays vor den Augen in Metern.");
            HudScale = Config.Bind("Anzeige", "VisierGroesse", 0.55f,
                "Größe des Visier-Displays relativ zum Blickwinkel der Spielkamera. 1 = Markierungen liegen genau auf den Objekten, aber die Ecken sind außerhalb des Sichtfelds. Kleiner = alles im Blick, Markierungen sitzen dafür etwas versetzt.");
            MenuDistance = Config.Bind("Anzeige", "MenueAbstand", 2.0f, "Abstand der Menü-Leinwand in Metern.");
            MenuWidth = Config.Bind("Anzeige", "MenueBreite", 2.6f, "Breite der Menü-Leinwand in Metern.");
            ShowUiFrame = Config.Bind("Anzeige", "UiFlaecheZeigen", true,
                "Die UI-Leinwand leicht blau einfärben, damit man sieht, wo sie liegt. Zum Testen.");
            FlipUi = Config.Bind("Anzeige", "UiSpiegeln", false, "Die UI senkrecht spiegeln, falls sie auf dem Kopf steht.");
            Helmet = Config.Bind("Anzeige", "Helm", VrHelmet.Mode.Kopf,
                "Kopf: Der Helm folgt dem VR-Kopf. Aus: Helm-Modell ausblenden. Spiel: Helm bleibt an der Figur.");
            AntiAliasing = Config.Bind("Bild", "Kantenglaettung", VrCamera.AntiAliasing.SMAA,
                "SMAA: glättet Kanten, bleibt scharf. TAA: wirkt in VR verwaschen und lässt Objekte zittern. FXAA: billig und unscharf. Keine. Spiel: Einstellung des Spiels.");
            ResolutionScale = Config.Bind("Bild", "Aufloesung", 1.0f,
                "Renderauflösung pro Auge relativ zur Vorgabe des Headsets. Über 1 glättet weiter, kostet aber deutlich Leistung.");
            Log.LogInfo($"{Name} {Version} geladen. Unity {Application.unityVersion}, Spiel {Application.version}");
            // Jeden Patch einzeln anwenden, damit ein fehlschlagender Patch nicht alle anderen (und F3) mitreißt.
            var harmony = new Harmony(Guid);
            foreach (var type in AccessTools.GetTypesFromAssembly(typeof(Plugin).Assembly))
            {
                try { harmony.CreateClassProcessor(type).Patch(); }
                catch (System.Exception e) { Log.LogError($"Patch {type.Name} fehlgeschlagen: {e}"); }
            }
        }

        private void Update()
        {
            if (Input.GetKeyDown(KeyCode.F3)) VrXr.Toggle();
        }
    }

    // Prüft, dass wir die Spielkamera finden: Alle Werkzeuge zielen über LynxCameraController.MainCamera.
    [HarmonyPatch(typeof(LynxCameraController), "Start")]
    internal static class LynxCameraControllerStartPatch
    {
        private static void Postfix()
        {
            var cam = LynxCameraController.MainCamera;
            Plugin.Log.LogInfo(cam == null
                ? "LynxCameraController gestartet, aber MainCamera ist null"
                : $"Spielkamera gefunden: '{cam.name}', FOV {cam.fieldOfView}, Auflösung {LynxCameraController.ScreenWidth}x{LynxCameraController.ScreenHeight}");
            if (cam != null && VrCamera.Instance == null) VrCamera.Attach(cam);
        }
    }
}
