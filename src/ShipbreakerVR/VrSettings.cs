using System.Collections.Generic;
using System.Linq;
using BepInEx.Configuration;
using UnityEngine;

namespace ShipbreakerVR
{
    // The settings in BepInEx\config\de.marczahn.shipbreakervr.cfg, bound in one place with their ranges and texts.
    // A setting with a row in the VR options (VrOptions) is described once, by its hint in English and German: the
    // menu shows the hint in the game's language, and the .cfg gets the English one, for a list setting with what its
    // values are called in the menu, plus a note for what only the file needs (units). Before, each setting was
    // described three times (.cfg, English and German hint), and the texts drifted apart. The ranges hold for the .cfg
    // too: BepInEx clamps a value edited there when it loads it, and the sliders take their ends from them.
    // The keys stay German so existing user settings survive (CLAUDE.md).
    internal static class VrSettings
    {
        public static ConfigFile File { get; private set; }

        public static ConfigEntry<bool> MultiPass;
        public static ConfigEntry<bool> SeparateEyeTextures;
        public static ConfigEntry<bool> StartVrAtLaunch;
        public static ConfigEntry<bool> EyeSharing;
        public static ConfigEntry<bool> AlternateEyes;
        public static ConfigEntry<bool> NoVrUnfitEffects;
        public static ConfigEntry<bool> PerformanceOverlay;
        public static ConfigEntry<bool> UiInVr;
        public static ConfigEntry<float> HudDistance;
        public static ConfigEntry<float> HudScale;
        public static ConfigEntry<VrUi.Steadying> HudSteadying;
        public static ConfigEntry<float> MenuDistance;
        public static ConfigEntry<float> MenuWidth;
        public static ConfigEntry<bool> FlipUi;
        public static ConfigEntry<bool> MonitorLook;
        public static ConfigEntry<bool> FreeWalking;
        public static ConfigEntry<VrHelmet.Mode> Helmet;
        public static ConfigEntry<VrResolution.AntiAliasing> AntiAliasing;
        public static ConfigEntry<float> ResolutionScale;
        public static ConfigEntry<float> StationResolution;
        public static ConfigEntry<float> WorldScale;
        public static ConfigEntry<float> HeadHeight;
        public static ConfigEntry<float> HeadSideways;
        public static ConfigEntry<VrPerformance.Occlusion> AmbientOcclusion;
        public static ConfigEntry<VrControls.Scheme> ControlScheme;
        public static ConfigEntry<VrControls.Turning> TurnMode;
        public static ConfigEntry<float> ThrustDeadzone;
        public static ConfigEntry<float> ThrustFull;
        public static ConfigEntry<VrFlightGlobe.Mode> FlightGlobe;
        public static ConfigEntry<float> FlightGlobeSize;
        public static ConfigEntry<float> FlightGlobeX;
        public static ConfigEntry<float> FlightGlobeY;
        public static ConfigEntry<bool> AimWithHand;
        public static ConfigEntry<bool> MirrorCutter;
        public static ConfigEntry<bool> TwoTools;
        public static ConfigEntry<bool> Climbing;
        public static ConfigEntry<bool> UprightBody;
        public static ConfigEntry<bool> Diagnostics;
        public static ConfigEntry<bool> ToolExport;
        public static ConfigEntry<bool> TestMode;
        public static ConfigEntry<bool> MockRuntime;
        public static ConfigEntry<KeyboardShortcut> ToggleVrKey;
        public static ConfigEntry<KeyboardShortcut> CenterKey;
        public static ConfigEntry<KeyboardShortcut> MonitorLookKey;
        public static ConfigEntry<KeyboardShortcut> DiagnosticsKey;
        public static ConfigEntry<KeyboardShortcut> ToolExportKey;
        public static ConfigEntry<KeyboardShortcut> EyeSharingKey;
        public static ConfigEntry<KeyboardShortcut> AlternateEyesKey;

        // What the VR options show for a setting: its hint, and for a list the names of its values in enum order.
        private sealed class Texts
        {
            public string HintEn, HintDe;
            public string[] NamesEn, NamesDe;
        }

        private static readonly Dictionary<ConfigEntryBase, Texts> Shown = new Dictionary<ConfigEntryBase, Texts>();

        // The hint for a setting's option row, in the game's language; null for a setting without one.
        public static string Hint(ConfigEntryBase entry) =>
            entry != null && Shown.TryGetValue(entry, out var t) ? VrOptions.L(t.HintEn, t.HintDe) : null;

        // The names of a list setting's values in the game's language, in the order of its enum.
        public static string[] Names(ConfigEntryBase entry) =>
            entry != null && Shown.TryGetValue(entry, out var t) && t.NamesEn != null ? VrOptions.L(t.NamesEn, t.NamesDe) : new string[0];

        // The ends of a number setting's range.
        public static (float Min, float Max) Range(ConfigEntry<float> entry) =>
            entry.Description.AcceptableValues is AcceptableValueRange<float> r ? (r.MinValue, r.MaxValue) : (0f, 1f);

        public static void Bind(ConfigFile config)
        {
            File = config;
            MultiPass = Bind("Rendering", "MultiPass", true,
                "Render each eye in its own pass. Single Pass Instanced needs shader variants the game lacks (result: only one eye, distorted image).");
            SeparateEyeTextures = Bind("Rendering", "SeparateEyeTextures", false,
                "A separate texture per eye instead of a Texture2DArray. Experimental, in case MultiPass alone is not enough.");
            StartVrAtLaunch = Bind("Rendering", "VrBeimStart", true,
                "Start VR as soon as the game launches, so the intro and loading screens already show in the headset. Off: start VR with Tasten/VrAnAus (F3).");
            EyeSharing = Bind("Rendering", "AugenTeilen", true,
                ("Works out what is in view and draws the shadows once for both eyes instead of once per eye. Saves processor time, most in the bay. Switch it off if objects appear late or are missing at the edge of one eye, or if shadows flicker or differ between the eyes.",
                    "Ermittelt einmal für beide Augen, was im Blickfeld ist, und zeichnet die Schatten nur einmal statt für jedes Auge. Spart Prozessorzeit, am meisten im Dock. Ausschalten, wenn am Rand eines Auges Objekte zu spät erscheinen oder fehlen, oder wenn Schatten flackern oder zwischen den Augen verschieden aussehen."),
                "Tasten/AugenTeilen (F10) switches it while the game runs.");
            AlternateEyes = Bind("Rendering", "AugenAbwechselnd", true,
                ("Draws only one eye per frame, the left and the right in turn; the other eye shows its last image, turned to where you look now. Saves about half of the rendering work on the processor. Fast-moving things (tools, hands, flying parts) can flicker slightly, because the eyes see them at different moments.",
                    "Zeichnet pro Bild nur ein Auge, abwechselnd links und rechts; das andere Auge zeigt sein letztes Bild, gedreht auf die aktuelle Blickrichtung. Spart etwa die Hälfte der Render-Arbeit des Prozessors. Schnell bewegte Dinge (Werkzeug, Hände, fliegende Teile) können leicht flimmern, weil die Augen sie zu verschiedenen Zeitpunkten sehen."),
                "Switch it off if the flicker bothers you or one eye looks wrong. Tasten/AugenAbwechselnd (F11) switches it while the game runs.");
            NoVrUnfitEffects = Bind("Bild", "VrUnpassendeEffekteAus", true,
                "Switch off effects that are wrong in a headset: motion blur, depth of field, lens distortion, chromatic aberration and Panini projection.");

            AmbientOcclusion = Bind("Bild", "Umgebungsverdeckung", VrPerformance.Occlusion.Niedrig,
                ("Shading in corners and creases. Low looks nearly the same and saves a lot of performance; the game default can stutter on the station. Off for the most performance.",
                    "Schattierung in Ecken und Fugen. Niedrig sieht fast gleich aus und spart viel Leistung; der Spielstandard kann auf der Station ruckeln. Aus für die meiste Leistung."),
                (new[] { "Low", "Off", "Game default" }, new[] { "Niedrig", "Aus", "Spielstandard" }),
                "Low is half resolution and few steps, as the game uses it in the bay; the station asks for full resolution and 16 steps, which costs a lot of GPU time at VR resolution.");

            ControlScheme = Bind("Steuerung", "Controllersteuerung", VrControls.Scheme.Vtol,
                ("Hold the left trigger: moving the controller thrusts that way, tilting it sideways rolls. Right stick: turn and pitch. Y: brake. Off if you fly with mouse and keyboard or a gamepad.",
                    "Linken Trigger halten: Controller verschieben gibt Schub in diese Richtung, seitlich Kippen rollt. Rechter Stick: drehen und neigen. Y: Bremse. Aus, wenn du mit Maus und Tastatur oder Gamepad fliegst."),
                (new[] { "Off", "VTOL (trigger)" }, new[] { "Aus", "VTOL (Trigger)" }),
                "While the left hand holds a tool or holds on, it works that instead.");
            TurnMode = Bind("Steuerung", "Drehen", VrControls.Turning.Fliessend,
                ("How the right stick turns you when flying with the controllers: smoothly, or in steps of 30, 45 or 90 degrees per flick. Steps are easier on the stomach.",
                    "Wie der rechte Stick dich beim Fliegen mit den Controllern dreht: fließend oder in Schritten von 30, 45 oder 90 Grad pro Antippen. Schritte sind magenfreundlicher."),
                (new[] { "Smooth", "30°", "45°", "90°" }, new[] { "Fließend", "30°", "45°", "90°" }));
            ThrustDeadzone = Bind("Steuerung", "SchubTotzone", 2f,
                ("How far you move the left controller from where you pressed the trigger before thrust starts. Raise it if you drift without meaning to.",
                    "Wie weit du den linken Controller von dort, wo du den Trigger gedrückt hast, bewegen musst, bevor Schub einsetzt. Erhöhen, wenn du ungewollt driftest."),
                "In centimeters.", Between(0f, VrControls.DeadzoneMax));
            ThrustFull = Bind("Steuerung", "SchubVoll", 10f,
                ("How far you move the left controller for full thrust. Shorter needs less arm movement, longer gives finer control. Always at least 5 cm beyond the deadzone.",
                    "Wie weit du den linken Controller für vollen Schub bewegst. Kürzer braucht weniger Armbewegung, länger erlaubt feinere Kontrolle. Immer mindestens 5 cm mehr als die Totzone."),
                "In centimeters.", Between(VrControls.MinThrustRange, VrControls.FullMax));
            ThrustDeadzone.SettingChanged += (_, __) => VrControls.KeepThrustRange(true);
            ThrustFull.SettingChanged += (_, __) => VrControls.KeepThrustRange(false);
            VrControls.KeepThrustRange(false);

            FlightGlobe = Bind("Anzeige", "Flugkugel", VrFlightGlobe.Mode.Immer,
                ("A small globe in the visor shows where your left controller is while you hold the trigger: the center is where you pressed it, the surface full thrust. Helps to find the zero point again.",
                    "Eine kleine Kugel im Visier zeigt, wo dein linker Controller ist, während du den Trigger hältst: die Mitte ist, wo du gedrückt hast, die Oberfläche voller Schub. Hilft, den Nullpunkt wiederzufinden."),
                (new[] { "Off", "Always", "While flying" }, new[] { "Aus", "Immer", "Beim Fliegen" }),
                "The small inner globe is the deadzone.");
            FlightGlobeSize = Bind("Anzeige", "FlugkugelGroesse", 0.06f, "Radius of the flight globe relative to the visor height.", Between(0.02f, 0.2f));
            FlightGlobeX = Bind("Anzeige", "FlugkugelX", -0.159f, "Horizontal position of the flight globe on the visor, from -0.5 (left edge) to 0.5 (right edge).", Between(-0.5f, 0.5f));
            FlightGlobeY = Bind("Anzeige", "FlugkugelY", -0.38f, "Vertical position of the flight globe on the visor, from -0.5 (bottom edge) to 0.5 (top edge).", Between(-0.5f, 0.5f));

            AimWithHand = Bind("Steuerung", "ZielenMitHand", true,
                ("Tools, grabbing and interacting aim where the controller with the tool points, from the tool's muzzle; a laser shows where, and the crosshair leaves the middle of the view. With an empty hand: no laser, A uses what is in the middle of the view. Grip at the body draws a tool with either hand and holds it while held: right hip grapple, left hip demo charges, over the right shoulder cutter, right side of the helmet scanner; the left hand at the left side of the helmet switches the flashlight. Trigger: fire or grapple, A: use (with the trigger held: throw; on the cutter: horizontal or vertical cuts), B with the trigger held: reel in, B: tool mode (on the grapple: grappling or placing tethers, however long it is held; in the tether mode an orange ring shows where it points, the trigger places a tether, A removes the tether pointed at, A held recalls them all and B switches back), B held on the other tools: tether. A bright dot marks where the grapple's or cutter's laser hits; on the Splitsaw small dots show where the cut goes. In the left hand X and Y work as A and B, and flying pauses. The suit's hands follow the controllers. Off: aim at the center of your view.",
                    "Werkzeuge, Greifen und Benutzen zielen dorthin, wohin der Controller mit dem Werkzeug zeigt, vom Lauf des Werkzeugs aus; ein Laser zeigt die Stelle, und das Fadenkreuz verschwindet aus der Blickmitte. Mit leerer Hand: kein Laser, A benutzt, was in der Blickmitte ist. Grip am Körper zieht mit jeder Hand ein Werkzeug und hält es, solange Grip gedrückt bleibt: rechte Hüfte Greifhaken, linke Hüfte Sprengladungen, über die rechte Schulter Cutter, rechte Helmseite Scanner; die linke Hand an der linken Helmseite schaltet die Taschenlampe. Trigger: feuern oder greifen, A: benutzen (mit Trigger: werfen; beim Cutter: waagrechte oder senkrechte Schnitte), B mit Trigger: einholen, B: Werkzeugmodus (beim Greifhaken: greifen oder Tethers setzen, egal wie lange gedrückt; im Tether-Modus zeigt ein oranger Ring die Stelle, der Trigger setzt einen Tether, A entfernt den anvisierten Tether, A halten holt alle zurück und B wechselt zurück), B halten bei den anderen Werkzeugen: Tether. Ein heller Punkt zeigt, wo der Laser von Greifhaken oder Cutter auftrifft; beim Splitsaw zeigen kleine Punkte, wo der Schnitt verläuft. In der linken Hand wirken X und Y wie A und B, und das Fliegen ruht. Die Hände des Anzugs folgen den Controllern. Aus: Ziel ist die Blickmitte."),
                "On the cutter and demo charges, B held while pointing at nothing recalls the tethers. Reaching for a tool in one hand with the other hand takes it over.");
            TwoTools = Bind("Steuerung", "ZweiWerkzeuge", true,
                ("Two of cutter, grapple and demo charges can be held at once, one in each hand: while one hand holds one of them, draw another with the other hand. Each hand's trigger and buttons work its own tool. The scanner hides the other hand's tool while it is out; keep that hand's grip and the tool comes back after the scan.",
                    "Zwei von Cutter, Greifhaken und Sprengladungen lassen sich gleichzeitig halten, je eins pro Hand: Während eine Hand eins davon hält, zieht die andere Hand ein weiteres. Trigger und Tasten jeder Hand bedienen ihr eigenes Werkzeug. Der Scanner blendet das Werkzeug der anderen Hand aus; hältst du dort den Grip, kommt es nach dem Scan zurück."),
                "Needs ZielenMitHand.");
            Climbing = Bind("Steuerung", "Festhalten", true,
                ("Grip with a hand that holds no tool, close to a surface, holds on there; moving the hand pulls you along it, hand over hand with both. A light part comes to the hand instead, the station and fixed hulls stay put. Letting go keeps your speed, so you can push off. While the left hand holds on, flying rests. At a control (airlock console, switch) the grip holds on too; A or X works it.",
                    "Grip mit einer Hand ohne Werkzeug, nah an einer Oberfläche, hält dich dort fest; bewegst du die Hand, ziehst du dich daran entlang, mit beiden Händen Hand über Hand. Ein leichtes Teil kommt stattdessen zur Hand, Station und feste Rümpfe bleiben, wo sie sind. Loslassen behält deinen Schwung, so kannst du dich abstoßen. Solange die linke Hand festhält, ruht das Fliegen. An einem Bedienelement (Schleusenkonsole, Schalter) hält der Grip dich ebenfalls fest; bedient wird es mit A oder X."),
                "How far you and a light part move depends on both masses.");

            UprightBody = Bind("Steuerung", "OberkoerperAufrecht", true,
                ("Your body stays upright and faces your hands while you look around, and follows your head when you lean, so the hands reach the controllers the same way wherever you look. Off: the body turns with every look and stays put when you lean, as before.",
                    "Dein Körper bleibt aufrecht und zu deinen Händen gewandt, während du dich umschaust, und geht mit, wenn du dich vorbeugst: Die Hände reichen gleich weit, egal wohin du schaust. Aus: Der Körper dreht sich wie bisher mit jedem Blick mit und bleibt stehen, wenn du dich vorbeugst."));

            Diagnostics = Bind("Debug", "Diagnose", false,
                "For finding problems: Tasten/Diagnose (F8, or B and Y together) writes what is drawn around the player into the log and saves screenshots next to it (also on its own 6 s after entering the Hab), load screens are logged frame by frame, and frame times are logged every 30 s.");
            ToolExport = Bind("Debug", "WerkzeugExport", false,
                "Tasten/WerkzeugExport (Shift+F8) saves the model of the tool in the hand (meshes, textures, UV layouts) to BepInEx\\ShipbreakerVR-tools, for retexturing it.");
            TestMode = Bind("Test", "Testmodus", false,
                "For development: test scripts dropped into BepInEx\\ShipbreakerVR-test\\inbox move the head and the controllers and press their buttons, and save screenshots and logs to ...\\results (see VrTestScript). Needs a restart to switch on.");
            MockRuntime = Bind("Test", "MockRuntime", false,
                "For development, with Testmodus: start VR with Unity's mock OpenXR runtime (BepInEx\\plugins\\ShipbreakerVR\\MockRuntime\\mock_runtime.dll) instead of the headset's, so VR runs without a headset. Needs a restart.");
            MirrorCutter = Bind("Bild", "CutterSpiegeln", true,
                "Copy the detailed left side of the cutter's model onto its plain right side, which the flat game never showed.");
            VrToolFit.Bind();

            UiInVr = Bind("Anzeige", "UiImRaum", true,
                ("Shows the HUD and the menus on a surface in the room instead of flat over the image. Leave it on; off is only for troubleshooting.",
                    "Zeigt HUD und Menüs auf einer Fläche im Raum statt flach über dem Bild. An lassen; aus nur zur Fehlersuche."));
            HudDistance = Bind("Anzeige", "HudAbstand", 1.5f,
                ("How far in front of your eyes the visor display floats. Change it if reading the display or switching focus between it and the world is uncomfortable.",
                    "Wie weit die Visieranzeige vor den Augen schwebt. Anpassen, wenn das Lesen der Anzeige oder der Blickwechsel zwischen ihr und der Welt unangenehm ist."),
                "In meters.", Between(0.5f, 3f));
            HudScale = Bind("Anzeige", "VisierGroesse", 0.55f,
                ("Size of the visor display. At 1 markers sit exactly on their objects, but the corners are outside your view. Smaller: everything in view, markers slightly offset.",
                    "Größe der Visieranzeige. Bei 1 sitzen Markierungen genau auf den Objekten, die Ecken liegen aber außerhalb des Blickfelds. Kleiner: alles im Blick, Markierungen leicht versetzt."),
                "Relative to the game camera's field of view.", Between(0.3f, 1f));
            HudSteadying = Bind("Anzeige", "HudBeruhigung", VrUi.Steadying.Schwach,
                ("Keeps the visor display still instead of copying every small head movement. Light follows your gaze quickly, Strong stays put longer. Off: fixed to the head.",
                    "Hält die Visieranzeige ruhig, statt jede kleine Kopfbewegung mitzumachen. Leicht folgt dem Blick schnell, Stark bleibt länger stehen. Aus: fest am Kopf."),
                (new[] { "Off", "Light", "Strong" }, new[] { "Aus", "Leicht", "Stark" }),
                "Turning the body (mouse, stick, controller) always applies at once.");
            MenuDistance = Bind("Anzeige", "MenueAbstand", 2.0f,
                ("How far away the menus appear. Together with the width it sets how large the text looks; adjust until it reads comfortably.",
                    "Wie weit entfernt die Menüs erscheinen. Bestimmt zusammen mit der Breite, wie groß der Text wirkt; anpassen, bis er sich bequem lesen lässt."),
                "In meters.", Between(1f, 4f));
            MenuWidth = Bind("Anzeige", "MenueBreite", 2.6f,
                ("Width of the menus. Wider makes the text larger but needs more head movement to see everything.",
                    "Breite der Menüs. Breiter macht den Text größer, braucht aber mehr Kopfbewegung, um alles zu sehen."),
                "In meters.", Between(1f, 4f));
            PerformanceOverlay = Bind("Anzeige", "Leistungsanzeige", false,
                ("Shows the frame rate in the upper left of the visor and menus: frames per second, the headset's rate, the share of frames that missed it, the main thread's time for game logic and for preparing the render, and the settings that matter for it.",
                    "Zeigt links oben in Visier und Menüs die Bildrate: Bilder pro Sekunde, die Rate des Headsets, den Anteil der Bilder, die sie verpasst haben, die Zeit des Hauptthreads für Spiellogik und für das Vorbereiten des Renderns, und die Einstellungen, die sie beeinflussen."),
                "While it is on, frame times are also logged every 30 s, as with Debug/Diagnose.");
            FlipUi = Bind("Anzeige", "UiSpiegeln", false, "Flip the UI vertically in case it is upside down.");
            FreeWalking = Bind("Station", "FreiGehen", false,
                "Walk through the station instead of hopping between its spots. Sitting on the bed, A stands up and B sits down again; the left stick walks, the right stick turns in steps, and the triggers change the poster you look at. Walking with a stick can make you feel sick.");
            MonitorLook = Bind("Anzeige", "MonitorEffekt", true,
                "Screen look for the menus on the station's monitors: a little dimmer, fine scan lines, darker edges, a faint reflection on the glass and light on the frame. Tasten/MonitorEffektUmschalten (F7) switches it on and off.");
            Helmet = Bind("Anzeige", "Helm", VrHelmet.Mode.Kopf,
                ("Follows head: the helmet frame moves with the headset. Hidden: a clear view without the frame. Game default: the helmet stays on the body.",
                    "Folgt dem Kopf: der Helmrahmen bewegt sich mit dem Headset. Ausgeblendet: freie Sicht ohne Rahmen. Spielstandard: der Helm bleibt am Körper."),
                (new[] { "Follows head", "Hidden", "Game default" }, new[] { "Folgt dem Kopf", "Ausgeblendet", "Spielstandard" }));

            AntiAliasing = Bind("Bild", "Kantenglaettung", VrResolution.AntiAliasing.SMAA,
                ("Smooths jagged edges. SMAA stays sharp and is the best choice in VR. TAA looks blurry in VR and makes objects jitter, FXAA is cheap but soft.",
                    "Glättet treppige Kanten. SMAA bleibt scharf und ist in VR die beste Wahl. TAA wirkt in VR unscharf und lässt Objekte zittern, FXAA ist günstig, aber weich."),
                (new[] { "TAA", "SMAA", "FXAA", "None", "Game default" }, new[] { "TAA", "SMAA", "FXAA", "Keine", "Spielstandard" }));
            ResolutionScale = Bind("Bild", "Aufloesung", 1.0f,
                ("Resolution per eye relative to the headset's default. Higher is sharper but costs a lot of performance and video memory. Lower it if the game stutters.",
                    "Auflösung pro Auge im Verhältnis zum Standard des Headsets. Höher ist schärfer, kostet aber viel Leistung und Grafikspeicher. Senken, wenn das Spiel ruckelt."),
                "It is limited to what should fit into the graphics card's memory, and applied one second after the last change.",
                Between(VrResolution.ResolutionScaleMin, VrResolution.ResolutionScaleMax));
            StationResolution = Bind("Bild", "AufloesungStation", 0.75f,
                ("Own resolution for the station between shifts, which is harder on the graphics card. If the view wobbles at the monitors when you move your head, lower it step by step until that stops.",
                    "Eigene Auflösung für die Station zwischen den Schichten, die die Grafikkarte stärker fordert. Wackelt das Bild an den Monitoren, wenn du den Kopf bewegst, schrittweise senken, bis es aufhört."),
                "Instead of Aufloesung. The wobble comes from frames the runtime computes in between when the headset's frame rate is missed; an RTX 4070 Ti at 2688x2880 per eye needed 0.7.",
                Between(VrResolution.ResolutionScaleMin, VrResolution.HabResolution));
            WorldScale = Bind("Bild", "Weltmassstab", 1.0f,
                ("Your size relative to the world. Above 1, distant objects keep more depth and are easier to judge, but the world looks smaller; below 1 the opposite. 1 is true to scale.",
                    "Deine Größe im Verhältnis zur Welt. Über 1 behalten ferne Objekte mehr Tiefe und lassen sich besser einschätzen, die Welt wirkt aber kleiner; unter 1 umgekehrt. 1 ist maßstabsgetreu."),
                "Above 1 widens eye distance and head movement.", Between(0.5f, 3f));
            HeadHeight = Bind("Bild", "KopfHoehe", 0f,
                ("Moves your eyes up or down against the game's head position. Raise it if your view seems to sit too low in the suit.",
                    "Verschiebt die Augen gegenüber der Kopfposition des Spiels nach oben oder unten. Erhöhen, wenn du zu tief im Anzug zu sitzen scheinst."),
                "In centimeters, positive is up.", Between(-VrCamera.HeadOffsetRange, VrCamera.HeadOffsetRange));
            HeadSideways = Bind("Bild", "KopfSeitlich", 0f,
                ("Moves your eyes right (positive) or left (negative) against the game's head position, relative to the direction you face.",
                    "Verschiebt die Augen gegenüber der Kopfposition des Spiels nach rechts (positiv) oder links (negativ), bezogen auf die Blickrichtung."),
                "In centimeters.", Between(-VrCamera.HeadOffsetRange, VrCamera.HeadOffsetRange));

            ToggleVrKey = Bind("Tasten", "VrAnAus", new KeyboardShortcut(KeyCode.F3), "Starts and stops VR.");
            CenterKey = Bind("Tasten", "AnsichtZentrieren", new KeyboardShortcut(KeyCode.F4),
                "Puts the view and the menus in front of you again, like pressing the left stick.");
            MonitorLookKey = Bind("Tasten", "MonitorEffektUmschalten", new KeyboardShortcut(KeyCode.F7),
                "Switches the screen look of the station's monitors (Anzeige/MonitorEffekt) on and off while a monitor shows.");
            DiagnosticsKey = Bind("Tasten", "Diagnose", new KeyboardShortcut(KeyCode.F8),
                "With Debug/Diagnose on: writes what is drawn around the player into the log and saves screenshots.");
            ToolExportKey = Bind("Tasten", "WerkzeugExport", new KeyboardShortcut(KeyCode.F8, KeyCode.LeftShift),
                "With Debug/WerkzeugExport on: saves the model of the tool in the hand.");
            EyeSharingKey = Bind("Tasten", "AugenTeilen", new KeyboardShortcut(KeyCode.F10),
                "Switches Rendering/AugenTeilen on and off.");
            AlternateEyesKey = Bind("Tasten", "AugenAbwechselnd", new KeyboardShortcut(KeyCode.F11),
                "Switches Rendering/AugenAbwechselnd on and off.");
        }

        // A setting without an option row, described in the .cfg only.
        public static ConfigEntry<T> Bind<T>(string section, string key, T value, string description, AcceptableValueBase range = null) =>
            File.Bind(section, key, value, new ConfigDescription(description, range));

        // A setting with an option row: hint in English and German, note for the .cfg only.
        public static ConfigEntry<T> Bind<T>(string section, string key, T value, (string En, string De) hint, string note = null, AcceptableValueBase range = null) =>
            Bind(section, key, value, new Texts { HintEn = hint.En, HintDe = hint.De }, note, range);

        // A list setting with an option row, with the menu's names of its values in enum order.
        private static ConfigEntry<T> Bind<T>(string section, string key, T value, (string En, string De) hint, (string[] En, string[] De) names, string note = null)
            where T : System.Enum
        {
            var values = System.Enum.GetNames(typeof(T));
            var legend = "Values: " + string.Join(", ", values.Select((v, i) => i < names.En.Length && names.En[i] != v ? $"{v} = {names.En[i]}" : v)) + ".";
            return Bind(section, key, value, new Texts { HintEn = hint.En, HintDe = hint.De, NamesEn = names.En, NamesDe = names.De },
                note == null ? legend : note + " " + legend, null);
        }

        private static ConfigEntry<T> Bind<T>(string section, string key, T value, Texts texts, string note, AcceptableValueBase range)
        {
            var entry = Bind(section, key, value, note == null ? texts.HintEn : texts.HintEn + " " + note, range);
            Shown[entry] = texts;
            return entry;
        }

        private static AcceptableValueRange<float> Between(float min, float max) => new AcceptableValueRange<float>(min, max);

        private static readonly KeyCode[][] ModifierKeys =
        {
            new[] { KeyCode.LeftShift, KeyCode.RightShift },
            new[] { KeyCode.LeftControl, KeyCode.RightControl },
            new[] { KeyCode.LeftAlt, KeyCode.RightAlt, KeyCode.AltGr },
        };

        // The shortcut's key went down this frame while its modifiers are held (either Shift for Shift, and so on) and no
        // other Shift, Ctrl or Alt is, so F8 and Shift+F8 stay apart. Unlike KeyboardShortcut.IsDown, other keys held
        // (flying with the keyboard) do not block it.
        public static bool Pressed(ConfigEntry<KeyboardShortcut> setting)
        {
            var shortcut = setting.Value;
            if (shortcut.MainKey == KeyCode.None || !Input.GetKeyDown(shortcut.MainKey)) return false;
            var modifiers = shortcut.Modifiers.ToArray();
            foreach (var key in modifiers)
            {
                var group = ModifierKeys.FirstOrDefault(g => g.Contains(key));
                if (!(group != null ? group.Any(Input.GetKey) : Input.GetKey(key))) return false;
            }
            foreach (var group in ModifierKeys)
                if (!group.Any(modifiers.Contains) && group.Any(Input.GetKey)) return false;
            return true;
        }
    }
}
