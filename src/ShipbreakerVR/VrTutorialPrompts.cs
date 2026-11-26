using System.Collections.Generic;
using System.Reflection;
using BBI.Unity.Game;
using Doozy.Engine.Themes;
using HarmonyLib;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

namespace ShipbreakerVR
{
    // Raik (2026-11-26): the tutorial's tips (the "Lynx Onboarding Manual" popups of the first shift) show the keyboard's
    // keys or the gamepad's buttons for flying, which in VR do something else or nothing. While VR runs with the
    // controllers flying (Steuerung/Controllersteuerung VTOL or Shooter), each tip's picture for an action of the flight
    // shows how it is done with the controllers; the words beside it stay the game's:
    // - rolling: Shooter the left controller's trigger + the left stick; VTOL the trigger + an arc bowing down with its
    //   heads up left and up right (tilt the hand);
    // - thrust (forward, back, left, right, up, down): Shooter the left stick; VTOL the trigger + a cross of four arrows;
    // - braking: Y (the gamepad's second brake picture is left out);
    // - looking around: the right stick.
    // Under the roll tip's lines one more of the mod's own: "Straighten up" with the left stick pressed in
    // (VrCamera.UpdateCenterPress: a short press straightens up in flight).
    // The pictures are the game's own glyphs (its table for the Xbox One gamepad) and the mod's own drawings
    // (Icons/*.png: trigger, roll, thrust, plus). A tip's picture comes from ContextualButtonPrompt.ChangeControlType
    // (on enable and on every change of the input device), so the picture is set again after it; what the mod adds
    // (the trigger and the plus before the picture, the straightening line) is added in the prompt's Update and taken
    // away again, with the game's picture back, when VR or the controllers' flying is switched off.
    internal static class VrTutorialPrompts
    {
        static VrTutorialPrompts() => VrSession.OnUnload("tutorial prompts", Release);

        private const string Extra = "ShipbreakerVR ";

        private enum Kind { None, Roll, Thrust, Brake, BrakeSecond, Look }

        private sealed class State
        {
            public Kind Kind;
            public GameplayActions.GameplayActionSet Action;
            public VrControls.Scheme Applied = VrControls.Scheme.Aus;
            public object ControlSet;
            public bool HasControlSet;
            public readonly List<GameObject> Added = new List<GameObject>();
            public LayoutElement Ignored;
            public bool IgnoredAdded;
        }

        private static readonly Dictionary<MonoBehaviour, State> States = new Dictionary<MonoBehaviour, State>();
        private static readonly List<MonoBehaviour> Gone = new List<MonoBehaviour>();
        private static FieldInfo setField, actionField, targetField, imageField;
        private static MethodInfo change;
        private static bool looked;
        private static readonly Dictionary<string, Sprite> Own = new Dictionary<string, Sprite>();
        private static int logs;

        private static VrControls.Scheme Wanted =>
            VrXr.IsRunning && VrSettings.ControlScheme != null ? VrSettings.ControlScheme.Value : VrControls.Scheme.Aus;

        // After the game's ChangeControlType: its control set remembered, and the picture set again.
        public static void Changed(MonoBehaviour prompt, object controlSet)
        {
            var state = Find(prompt);
            if (state == null) return;
            state.ControlSet = controlSet;
            state.HasControlSet = true;
            if (state.Applied != VrControls.Scheme.Aus) SetPicture(prompt, state, state.Applied);
        }

        // Each frame for each prompt switched on: the tip as the controllers fly now.
        public static void Check(MonoBehaviour prompt)
        {
            var state = Find(prompt);
            if (state == null) return;
            var wanted = Wanted;
            if (wanted == state.Applied) return;
            Undo(prompt, state);
            if (wanted != VrControls.Scheme.Aus) Apply(prompt, state, wanted);
        }

        private static State Find(MonoBehaviour prompt)
        {
            if (prompt == null) return null;
            if (States.TryGetValue(prompt, out var known)) return known.Kind == Kind.None ? null : known;
            if (States.Count > 64) Sweep();
            var state = Read(prompt);
            States[prompt] = state;
            return state.Kind == Kind.None ? null : state;
        }

        private static State Read(MonoBehaviour prompt)
        {
            var state = new State();
            if (!looked)
            {
                looked = true;
                var type = prompt.GetType();
                setField = AccessTools.Field(type, "m_ControlActionSet");
                actionField = AccessTools.Field(type, "m_GameplayAction");
                targetField = AccessTools.Field(type, "m_SpriteTargetImage");
                imageField = AccessTools.Field(type, "m_PromptImage");
                change = AccessTools.Method(type, "ChangeControlType");
                if (setField == null || actionField == null || change == null) Plugin.Log.LogWarning("Tutorial prompts: the game's prompt has no action to read, its pictures stay");
            }
            if (setField == null || actionField == null || change == null) return state;
            if (setField.GetValue(prompt).ToString() != "Controls_Gameplay" || !InTutorial(prompt.transform)) return state;
            state.Action = (GameplayActions.GameplayActionSet)actionField.GetValue(prompt);
            switch (state.Action)
            {
                case GameplayActions.GameplayActionSet.RollBodyLeft:
                case GameplayActions.GameplayActionSet.RollBodyRight:
                    state.Kind = Kind.Roll; break;
                case GameplayActions.GameplayActionSet.ThrustMoveForward:
                case GameplayActions.GameplayActionSet.ThrustMoveBackward:
                case GameplayActions.GameplayActionSet.ThrustMoveLeft:
                case GameplayActions.GameplayActionSet.ThrustMoveRight:
                case GameplayActions.GameplayActionSet.ThrustMoveUp:
                case GameplayActions.GameplayActionSet.ThrustMoveDown:
                    state.Kind = Kind.Thrust; break;
                case GameplayActions.GameplayActionSet.ThrustBrakeLeft:
                    state.Kind = Kind.Brake; break;
                case GameplayActions.GameplayActionSet.ThrustBrakeRight:
                    state.Kind = Kind.BrakeSecond; break;
                case GameplayActions.GameplayActionSet.RotateBodyUp:
                    state.Kind = Kind.Look; break;
            }
            if (state.Kind != Kind.None && logs++ < 30) Plugin.Log.LogInfo($"Tutorial prompts: the tip's picture for {state.Action} ('{VrUtil.Path(prompt.transform)}') shows the controllers while VR runs");
            return state;
        }

        // A prompt of the tutorial's tips (PRF_A1_TUT_..._Tip, or a popup named TUT_...).
        private static bool InTutorial(Transform at)
        {
            for (; at != null; at = at.parent)
                if (at.name.Contains("TUT_")) return true;
            return false;
        }

        private static Image PictureOf(MonoBehaviour prompt)
        {
            if (targetField != null && targetField.GetValue(prompt) is SpriteTargetImage target && target.Image != null) return target.Image;
            return imageField != null ? imageField.GetValue(prompt) as Image : null;
        }

        private static void Apply(MonoBehaviour prompt, State state, VrControls.Scheme scheme)
        {
            state.Applied = scheme;
            var picture = PictureOf(prompt);
            if (picture == null) return;
            // The line for straightening up first: making it switches the roll tip's line off and on, which runs the
            // game's ChangeControlType (and Changed) once more.
            if (state.Action == GameplayActions.GameplayActionSet.RollBodyRight) AddStraighten(prompt, state, picture);
            if (state.Kind == Kind.BrakeSecond)
            {
                state.Ignored = picture.GetComponent<LayoutElement>();
                state.IgnoredAdded = state.Ignored == null;
                if (state.Ignored == null) state.Ignored = picture.gameObject.AddComponent<LayoutElement>();
                state.Ignored.ignoreLayout = true;
            }
            var withTrigger = state.Kind == Kind.Roll || state.Kind == Kind.Thrust && scheme == VrControls.Scheme.Vtol;
            if (withTrigger)
            {
                AddBefore(picture, "trigger", 1f, state);
                AddBefore(picture, "plus", 0.55f, state);
            }
            SetPicture(prompt, state, scheme);
        }

        private static void SetPicture(MonoBehaviour prompt, State state, VrControls.Scheme scheme)
        {
            var picture = PictureOf(prompt);
            if (picture == null) return;
            if (state.Kind == Kind.BrakeSecond)
            {
                picture.enabled = false;
                return;
            }
            var vtol = scheme == VrControls.Scheme.Vtol;
            Sprite sprite = null;
            switch (state.Kind)
            {
                case Kind.Roll: sprite = vtol ? OwnSprite("roll") : Glyph(LynxConfigurableControlOptions.ConfigKey.LeftStickUp); break;
                case Kind.Thrust: sprite = vtol ? OwnSprite("thrust") : Glyph(LynxConfigurableControlOptions.ConfigKey.LeftStickUp); break;
                case Kind.Brake: sprite = Glyph(LynxConfigurableControlOptions.ConfigKey.Action4); break;
                case Kind.Look: sprite = Glyph(LynxConfigurableControlOptions.ConfigKey.RightStickUp); break;
            }
            if (sprite != null) picture.sprite = sprite;
        }

        // One of the mod's pictures before the tip's picture, in its line, as tall as that picture by that share.
        private static void AddBefore(Image picture, string name, float share, State state)
        {
            var sprite = OwnSprite(name);
            if (sprite == null) return;
            var line = picture.transform.parent;
            if (line == null) return;
            var made = new GameObject(Extra + name, typeof(RectTransform), typeof(Image), typeof(LayoutElement));
            made.layer = picture.gameObject.layer;
            made.transform.SetParent(line, false);
            made.transform.SetSiblingIndex(picture.transform.GetSiblingIndex());
            var image = made.GetComponent<Image>();
            image.sprite = sprite;
            image.preserveAspect = true;
            image.raycastTarget = false;
            image.color = picture.color;
            var size = picture.rectTransform.rect.height > 1f ? picture.rectTransform.rect.height : 64f;
            var layout = made.GetComponent<LayoutElement>();
            layout.preferredWidth = layout.preferredHeight = layout.minWidth = layout.minHeight = size * share;
            state.Added.Add(made);
        }

        // Under the roll tip's last line a copy of it: the left stick pressed in, "Straighten up".
        private static void AddStraighten(MonoBehaviour prompt, State state, Image picture)
        {
            var line = picture.transform.parent;
            var list = line != null ? line.parent : null;
            if (list == null) return;
            var was = line.gameObject.activeSelf;
            line.gameObject.SetActive(false);
            var copy = Object.Instantiate(line.gameObject, list);
            line.gameObject.SetActive(was);
            copy.name = Extra + "straighten up";
            // Made inactive, so none of the copy's own scripts woke: the prompt and the localized text are taken away,
            // and anything the mod added to the line.
            foreach (var behaviour in copy.GetComponentsInChildren<MonoBehaviour>(true))
            {
                var type = behaviour.GetType().Name;
                if (type == "ContextualButtonPrompt" || type == "LocalizedTextMeshProUGUI") Object.DestroyImmediate(behaviour);
            }
            for (var i = copy.transform.childCount - 1; i >= 0; i--)
            {
                var child = copy.transform.GetChild(i);
                if (child.name.StartsWith(Extra)) Object.DestroyImmediate(child.gameObject);
            }
            copy.transform.SetSiblingIndex(line.GetSiblingIndex() + 1);
            copy.SetActive(true);
            var copyPicture = copy.transform.Find(picture.name)?.GetComponent<Image>();
            var stick = Glyph(LynxConfigurableControlOptions.ConfigKey.LeftStickButton);
            if (copyPicture != null && stick != null) copyPicture.sprite = stick;
            foreach (var text in copy.GetComponentsInChildren<TMP_Text>(true))
                text.text = VrOptions.L("Straighten up", "Gerade ausrichten");
            state.Added.Add(copy);
            Plugin.Log.LogInfo("Tutorial prompts: the roll tip has a line for straightening up (left stick pressed in)");
        }

        private static void Undo(MonoBehaviour prompt, State state)
        {
            var was = state.Applied;
            state.Applied = VrControls.Scheme.Aus;
            foreach (var made in state.Added)
                if (made != null) Object.Destroy(made);
            state.Added.Clear();
            if (state.Ignored != null)
            {
                if (state.IgnoredAdded) Object.Destroy(state.Ignored);
                else state.Ignored.ignoreLayout = false;
            }
            state.Ignored = null;
            if (was == VrControls.Scheme.Aus || prompt == null) return;
            var picture = PictureOf(prompt);
            if (picture != null) picture.enabled = true;
            // The game's picture back.
            if (state.HasControlSet)
            {
                try { change.Invoke(prompt, new[] { state.ControlSet }); }
                catch (System.Exception e) { Plugin.Log.LogWarning($"Tutorial prompts: the game's picture did not come back: {e.InnerException?.Message ?? e.Message}"); }
            }
        }

        // The game's own glyph of the Xbox One gamepad for that key.
        private static Sprite Glyph(LynxConfigurableControlOptions.ConfigKey key)
        {
            var list = Main.Instance != null && Main.Instance.MainSettings != null ? Main.Instance.MainSettings.ControlSpriteListAsset : null;
            if (list == null) return null;
            return list.XboxOne_ControlSprites.TryGetValue(key, out var asset) && asset != null ? asset.Data.Sprite : null;
        }

        // The mod's own pictures, 256 pixels drawn at 64 of the UI's units as the game's glyphs are.
        private static Sprite OwnSprite(string name)
        {
            if (Own.TryGetValue(name, out var known)) return known;
            Sprite sprite = null;
            try
            {
                using (var stream = typeof(VrTutorialPrompts).Assembly.GetManifestResourceStream("Icons/" + name + ".png"))
                {
                    if (stream != null)
                    {
                        var bytes = new byte[stream.Length];
                        var read = 0;
                        while (read < bytes.Length)
                        {
                            var n = stream.Read(bytes, read, bytes.Length - read);
                            if (n <= 0) break;
                            read += n;
                        }
                        var texture = new Texture2D(2, 2, TextureFormat.RGBA32, true) { name = "ShipbreakerVR " + name };
                        if (ImageConversion.LoadImage(texture, bytes, true))
                        {
                            texture.wrapMode = TextureWrapMode.Clamp;
                            texture.filterMode = FilterMode.Trilinear;
                            sprite = Sprite.Create(texture, new Rect(0, 0, texture.width, texture.height), new Vector2(0.5f, 0.5f), texture.width / 0.64f);
                            sprite.name = "ShipbreakerVR " + name;
                        }
                    }
                }
            }
            catch (System.Exception e)
            {
                Plugin.Log.LogWarning($"Tutorial prompts: the picture '{name}' did not load: {e.Message}");
            }
            if (sprite == null) Plugin.Log.LogWarning($"Tutorial prompts: no picture '{name}'");
            Own[name] = sprite;
            return sprite;
        }

        private static void Sweep()
        {
            Gone.Clear();
            foreach (var prompt in States.Keys) if (prompt == null) Gone.Add(prompt);
            foreach (var prompt in Gone) States.Remove(prompt);
        }

        // On unload (hot reload): the game's pictures are back.
        private static void Release()
        {
            foreach (var pair in States)
                if (pair.Value.Kind != Kind.None) Undo(pair.Key, pair.Value);
            States.Clear();
        }
    }

    [HarmonyPatch]
    internal static class TutorialPromptChangePatch
    {
        private static MethodBase TargetMethod() => AccessTools.Method(AccessTools.TypeByName(VrKeyHints.PromptType), "ChangeControlType");

        private static void Postfix(MonoBehaviour __instance, object[] __args) => VrTutorialPrompts.Changed(__instance, __args[0]);
    }

    [HarmonyPatch]
    internal static class TutorialPromptUpdatePatch
    {
        private static MethodBase TargetMethod() => AccessTools.Method(AccessTools.TypeByName(VrKeyHints.PromptType), "Update");

        private static void Postfix(MonoBehaviour __instance) => VrTutorialPrompts.Check(__instance);
    }
}
