using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using BBI.Unity.Game;
using BBI.Unity.Game.UI;
using BepInEx.Configuration;
using HarmonyLib;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

namespace ShipbreakerVR
{
    // The radio's malfunction (Raik, 2026-11-28). Electrical damage, an explosion or a hard knock now and then (the
    // game's PlayerFX ElectricalDamage, ExplosionDamage and ImpactDamageAudio_9pip, at most every 20 s) post
    // 'mus_fastTakeover_start': the helmet's radio hisses and crackles, the HUD shows RADIO MALFUNCTION, and 21 s on
    // the game's left hand knocks against the helmet on its own (GrabController.TryHelmetKnock), as the sound ends by
    // itself (its stop is held back inside the Wwise event).
    // In VR with both controllers:
    // - The game's knock is not played: the hand is the player's. Knocking by hand is: either hand comes against the
    //   helmet (into Reach of the head, moving towards it). Each knock sounds as the game's animation does
    //   ('sfx_helmet_radio_knock', on the game's master SFX object) with a buzz; the second one (Raik, 2026-11-28:
    //   "two blows, and a knock heard both times") ends the malfunction there and then: the event's sounds fade out,
    //   its stop and the music's way back to the helmet are posted, the HUD's warning goes off.
    // - Not knocked, it ends by itself when the game would have knocked, as before, only without the knock.
    // - The hiss is a little louder (Louder): the event plays on an emitter of the mod's at the game's own.
    // - Until a player has once knocked by hand (Hinweise/FunkKlopfenGelernt), a line under the HUD's warning tells
    //   what to do after HintAfter, with a figure knocking on its head (drawn here, the mod's own).
    // With VR off (F3) all of it is the game's again.
    [HarmonyPatch]
    internal static class VrHelmetKnock
    {
        static VrHelmetKnock() => VrSession.OnUnload("helmet knock", Release);

        private const string StartName = "mus_fastTakeover_start";
        private const string StopName = "mus_fastTakeover_stop";
        private const string BackName = "mix_music_sendToHelmet_andRestartTimeline";
        private const string KnockName = "sfx_helmet_radio_knock";
        // Knocks it takes, and the least time between two.
        private const int Knocks = 2;
        private const float Again = 0.15f;
        private const uint Start = 4110815201;
        // The hiss's loudness, as a multiple of the game's.
        private const float Louder = 1.5f;
        // How near the head a hand knocks, in m as the player really stands, and how fast towards it at least.
        private const float Reach = 0.22f;
        private const float Towards = 0.2f;
        // The line comes this long after the malfunction began.
        private const float HintAfter = 4f;
        private const int FadeOut = 300;

        private static readonly AccessTools.FieldRef<GrabController, bool> Waiting =
            AccessTools.FieldRefAccess<GrabController, bool>("mWaitingToHelmetKnock");
        private static readonly AccessTools.FieldRef<GrabController, float> Waited =
            AccessTools.FieldRefAccess<GrabController, float>("mHelmentKnockCurrentWaitedTime");

        private static ConfigEntry<bool> learned;
        private static GrabController grab;
        private static float since;
        private static GameObject emitter;
        private static GameObject postedOn;
        private static GameObject master;
        private static int knocked;
        private static float knockedAt;
        private static readonly float[] Distance = { -1f, -1f };
        private static GameObject hint;
        private static Sprite figure;
        private static bool posting;
        private static int logs;

        private static bool Learned
        {
            get
            {
                if (learned == null && VrSettings.File != null)
                    learned = VrSettings.Bind("Hinweise", "FunkKlopfenGelernt", false,
                        "True once the radio's malfunction was ended by a knock of the hand against the helmet; until then a line on the HUD tells how. Set to false to see it again.");
                return learned != null && learned.Value;
            }
        }

        private static bool ByHand => VrXr.IsRunning && VrInput.Left.Tracked && VrInput.Right.Tracked;

        public static bool Active => grab != null;

        // ---- The game's malfunction ----

        [HarmonyPatch(typeof(GrabController), "OnSFXPlayed")]
        [HarmonyPostfix]
        private static void Began(GrabController __instance)
        {
            if (!Waiting(__instance) || grab == __instance) return;
            grab = __instance;
            since = Time.time;
            knocked = 0;
            Distance[0] = Distance[1] = -1f;
            if (logs++ < 20) Plugin.Log.LogInfo($"Helmet knock: the radio malfunctions{(ByHand ? ", a knock of the hand against the helmet ends it" : "")}");
        }

        // The game's own knock: in VR only the end it comes with.
        [HarmonyPatch(typeof(GrabController), "TryHelmetKnock")]
        [HarmonyPrefix]
        private static bool GameKnock(GrabController __instance)
        {
            if (!ByHand) return true;
            End(__instance);
            if (logs++ < 20) Plugin.Log.LogInfo("Helmet knock: not knocked, the radio's malfunction ends by itself (the game's knock is left out in VR)");
            return false;
        }

        private static void End(GrabController controller)
        {
            if (controller != null)
            {
                Waiting(controller) = false;
                Waited(controller) = 0f;
                Main.EventSystem.Post(ToggleHelmetMalfunctionUIEvent.GetEvent(isActive: false));
            }
            grab = null;
            ShowHint(false);
        }

        // ---- The sound: on the mod's emitter, a little louder ----

        private static IEnumerable<MethodBase> PostEvents() =>
            typeof(AkSoundEngine).GetMethods(BindingFlags.Public | BindingFlags.Static)
                .Where(m => m.Name == "PostEvent" && m.GetParameters().Length >= 2
                    && m.GetParameters()[0].ParameterType == typeof(uint) && m.GetParameters()[1].ParameterType == typeof(GameObject));

        [HarmonyPatch]
        private static class Post
        {
            private static IEnumerable<MethodBase> TargetMethods() => PostEvents();

            private static void Prefix(uint __0, ref GameObject __1)
            {
                if (__0 != Start || posting || __1 == null) return;
                postedOn = __1;
                master = __1;
                if (!ByHand) return;
                try
                {
                    if (emitter == null || emitter.transform.parent != __1.transform)
                    {
                        if (emitter != null) Object.Destroy(emitter);
                        emitter = new GameObject("ShipbreakerVR radio malfunction");
                        emitter.transform.SetParent(__1.transform, false);
                        emitter.AddComponent<AkGameObj>();
                    }
                    var listener = VrRadioMusic.Listener();
                    if (listener != null) AkSoundEngine.SetGameObjectOutputBusVolume(emitter, listener, Louder);
                    __1 = emitter;
                    postedOn = emitter;
                    if (logs++ < 20) Plugin.Log.LogInfo($"Helmet knock: '{StartName}' plays on the mod's emitter, {Louder:0.0} times as loud{(listener == null ? " (no listener found, as loud as the game's)" : "")}");
                }
                catch (System.Exception e)
                {
                    Plugin.Log.LogWarning($"Helmet knock: the hiss plays as the game's ({e.Message})");
                }
            }
        }

        // ---- The hand ----

        // From VrFrame once a frame.
        public static void Update()
        {
            if (grab == null) return;
            if (!Waiting(grab))
            {
                // The game reset it (death, a level loaded) or knocked itself with VR off.
                grab = null;
                ShowHint(false);
                return;
            }
            if (!ByHand)
            {
                ShowHint(false);
                return;
            }
            var vr = VrCamera.Instance;
            if (vr == null) return;
            var head = vr.transform.localPosition;
            if (Knocking(VrInput.Left, 0, head) || Knocking(VrInput.Right, 1, head)) return;
            ShowHint(!Learned && Time.time - since >= HintAfter);
        }

        private static bool Knocking(VrInput.Hand input, int index, Vector3 head)
        {
            var dt = Time.deltaTime;
            if (!input.TryGetPosition(out var at) || dt <= 0f)
            {
                Distance[index] = -1f;
                return false;
            }
            var distance = Vector3.Distance(at, head);
            var before = Distance[index];
            Distance[index] = distance;
            if (before < 0f || before < Reach || distance >= Reach || (before - distance) / dt < Towards) return false;
            if (knocked > 0 && Time.time - knockedAt < Again) return false;
            knocked++;
            knockedAt = Time.time;
            input.Buzz(0.8f, 0.12f);
            Sound();
            if (knocked < Knocks)
            {
                if (logs++ < 20) Plugin.Log.LogInfo($"Helmet knock: {input.Name} hand knocked against the helmet ({knocked} of {Knocks}) after {Time.time - since:0.0} s");
                return false;
            }
            Knocked(input);
            return true;
        }

        // The game's knock on the helmet, as its animation plays it.
        private static void Sound()
        {
            var on = master != null ? master : postedOn;
            if (on == null && VrCamera.Instance != null) on = VrCamera.Instance.gameObject;
            if (on == null) return;
            try { AkSoundEngine.PostEvent(KnockName, on); }
            catch (System.Exception e) { if (logs++ < 20) Plugin.Log.LogWarning($"Helmet knock: no knock heard ({e.Message})"); }
        }

        private static void Knocked(VrInput.Hand input)
        {
            var on = postedOn;
            if (on != null)
            {
                posting = true;
                try
                {
                    AkSoundEngine.ExecuteActionOnEvent(Start, AkActionOnEventType.AkActionOnEventType_Stop, on, FadeOut, AkCurveInterpolation.AkCurveInterpolation_Linear);
                    AkSoundEngine.PostEvent(StopName, on);
                    AkSoundEngine.PostEvent(BackName, on);
                }
                catch (System.Exception e)
                {
                    Plugin.Log.LogWarning($"Helmet knock: the hiss could not be ended ({e.Message})");
                }
                finally
                {
                    posting = false;
                }
            }
            var first = !Learned;
            if (learned != null) learned.Value = true;
            if (logs++ < 20) Plugin.Log.LogInfo($"Helmet knock: {input.Name} hand knocked against the helmet ({knocked} of {Knocks}) after {Time.time - since:0.0} s, the radio works again{(first ? " (learned, the line on the HUD no longer comes)" : "")}");
            End(grab);
        }

        // ---- The line under the HUD's warning ----

        private static void ShowHint(bool show)
        {
            if (!show)
            {
                if (hint != null && hint.activeSelf) hint.SetActive(false);
                return;
            }
            if (hint == null && !MakeHint()) return;
            if (!hint.activeSelf)
            {
                hint.SetActive(true);
                if (logs++ < 20) Plugin.Log.LogInfo("Helmet knock: not knocked yet, the line on the HUD tells how");
            }
        }

        private static bool MakeHint()
        {
            var warning = Object.FindObjectOfType<HelmetMalfunctionUIController>();
            if (warning == null) return false;
            var sample = warning.GetComponentInChildren<TextMeshProUGUI>(true);
            var frame = warning.GetComponentsInChildren<Image>(true).FirstOrDefault(i => i.gameObject.name == "Frame");
            var parent = (RectTransform)warning.transform;

            hint = new GameObject("ShipbreakerVR knock hint", typeof(RectTransform));
            hint.layer = warning.gameObject.layer;
            var rect = (RectTransform)hint.transform;
            rect.SetParent(parent, false);
            rect.anchorMin = rect.anchorMax = new Vector2(1f, 0f);
            rect.pivot = new Vector2(1f, 1f);
            rect.anchoredPosition = new Vector2(0f, -6f);
            rect.sizeDelta = new Vector2(430f, 52f);
            var back = hint.AddComponent<Image>();
            back.color = frame != null ? frame.color : new Color(1f, 0.42f, 0.42f, 1f);
            back.raycastTarget = false;

            var icon = new GameObject("Figure", typeof(RectTransform));
            icon.layer = hint.layer;
            var iconRect = (RectTransform)icon.transform;
            iconRect.SetParent(rect, false);
            iconRect.anchorMin = iconRect.anchorMax = new Vector2(0f, 0.5f);
            iconRect.pivot = new Vector2(0f, 0.5f);
            iconRect.anchoredPosition = new Vector2(6f, 0f);
            iconRect.sizeDelta = new Vector2(44f, 44f);
            var image = icon.AddComponent<Image>();
            image.sprite = Figure();
            image.preserveAspect = true;
            image.raycastTarget = false;

            var words = new GameObject("Text", typeof(RectTransform));
            words.layer = hint.layer;
            var wordsRect = (RectTransform)words.transform;
            wordsRect.SetParent(rect, false);
            wordsRect.anchorMin = new Vector2(0f, 0f);
            wordsRect.anchorMax = new Vector2(1f, 1f);
            wordsRect.offsetMin = new Vector2(56f, 2f);
            wordsRect.offsetMax = new Vector2(-8f, -2f);
            var text = words.AddComponent<TextMeshProUGUI>();
            if (sample != null)
            {
                text.font = sample.font;
                text.fontSharedMaterial = sample.fontSharedMaterial;
                text.color = sample.color;
            }
            text.fontSize = 13f;
            text.alignment = TextAlignmentOptions.MidlineLeft;
            text.enableWordWrapping = true;
            text.raycastTarget = false;
            text.text = VrOptions.L("RADIO MALFUNCTION FROM ELECTRICAL DISCHARGE\nPHYSICAL CORRECTION REQUIRED",
                "FEHLFUNKTION FUNKGERÄT DURCH ELEKTRISCHE ENTLADUNG\nPHYSISCHE KORREKTUR NOTWENDIG");
            hint.SetActive(false);
            Plugin.Log.LogInfo($"Helmet knock: the line under the HUD's warning is made ('{parent.name}')");
            return true;
        }

        // A figure knocking on its head: white on clear, 64 by 64, drawn here.
        private static Sprite Figure()
        {
            if (figure != null) return figure;
            const int size = 64;
            var texture = new Texture2D(size, size, TextureFormat.RGBA32, false) { name = "ShipbreakerVR knock figure", wrapMode = TextureWrapMode.Clamp };
            texture.hideFlags = HideFlags.DontUnloadUnusedAsset;
            var segments = new List<(Vector2 A, Vector2 B, float Width)>
            {
                // Body and legs.
                (new Vector2(30f, 38f), new Vector2(30f, 18f), 4f),
                (new Vector2(30f, 18f), new Vector2(23f, 3f), 4f),
                (new Vector2(30f, 18f), new Vector2(37f, 3f), 4f),
                // The arm hanging.
                (new Vector2(30f, 34f), new Vector2(20f, 22f), 4f),
                // The arm knocking: up and bent, the fist at the side of the head.
                (new Vector2(30f, 34f), new Vector2(44f, 40f), 4f),
                (new Vector2(44f, 40f), new Vector2(40f, 50f), 4f),
                // The knock's lines.
                (new Vector2(46f, 55f), new Vector2(53f, 60f), 2.5f),
                (new Vector2(48f, 50f), new Vector2(57f, 51f), 2.5f),
                (new Vector2(46f, 45f), new Vector2(53f, 41f), 2.5f),
            };
            var head = new Vector2(30f, 48f);
            const float headRadius = 7f;
            const float ring = 2f;
            var pixels = new Color32[size * size];
            for (var y = 0; y < size; y++)
                for (var x = 0; x < size; x++)
                {
                    var p = new Vector2(x + 0.5f, y + 0.5f);
                    var cover = Mathf.Clamp01(ring + 0.5f - Mathf.Abs(Vector2.Distance(p, head) - headRadius));
                    foreach (var (a, b, width) in segments)
                        cover = Mathf.Max(cover, Mathf.Clamp01(width * 0.5f + 0.5f - Along(p, a, b)));
                    // The fist.
                    cover = Mathf.Max(cover, Mathf.Clamp01(3.5f + 0.5f - Vector2.Distance(p, new Vector2(40f, 50f))));
                    pixels[y * size + x] = new Color32(255, 255, 255, (byte)Mathf.RoundToInt(cover * 255f));
                }
            texture.SetPixels32(pixels);
            texture.Apply(false, true);
            figure = Sprite.Create(texture, new Rect(0f, 0f, size, size), new Vector2(0.5f, 0.5f), 100f);
            figure.hideFlags = HideFlags.DontUnloadUnusedAsset;
            return figure;
        }

        private static float Along(Vector2 p, Vector2 a, Vector2 b)
        {
            var ab = b - a;
            var t = Mathf.Clamp01(Vector2.Dot(p - a, ab) / Mathf.Max(1e-6f, ab.sqrMagnitude));
            return Vector2.Distance(p, a + ab * t);
        }

        // On unload, and so with VR switched off: the game's knock and its warning are its own again.
        private static void Release()
        {
            if (hint != null) Object.Destroy(hint);
            hint = null;
            grab = null;
        }
    }
}
