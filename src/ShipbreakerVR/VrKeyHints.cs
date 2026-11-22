using System.Collections.Generic;
using System.Reflection;
using BBI.Unity.Game;
using HarmonyLib;
using UnityEngine;
using UnityEngine.UI;

namespace ShipbreakerVR
{
    // The game's HUD names the key for the flashlight and the scanner (ContextualButtonPrompt: a key's picture in a
    // bracket and the word beside it, for an action of the gameplay controls). In VR neither has a key: the flashlight is
    // switched with the left hand at the left of the helmet, the scanner is taken from the right of the helmet
    // (VrToolBelt), so the hints named keys that do nothing (Raik's list of 2026-11-21, point 7).
    // While VR runs the key and its bracket are not drawn at all, and the word comes up in its place on the HUD only
    // while a hand is at that thing's place at the helmet (Raik, 2026-11-22: "the word springs up when the hand is at
    // the right place"): it tells what the hand would take there. Drawn or not by the alpha of their canvas renderers,
    // set each frame, so the game's own switching, colours and layout of the hints stay as they are; with VR off they
    // are back.
    internal static class VrKeyHints
    {
        static VrKeyHints() => VrSession.OnUnload("key hints", Release);

        internal const string PromptType = "BBI.Unity.Game.ContextualButtonPrompt";

        private sealed class Hint
        {
            // The place at the helmet it belongs to; None for any other hint, which is left alone.
            public VrToolBelt.Zone Zone;
            // The hint's word, and everything else of it (the key, its bracket).
            public readonly List<Graphic> Word = new List<Graphic>();
            public readonly List<Graphic> Key = new List<Graphic>();
            public bool Changed;
        }

        private static readonly Dictionary<MonoBehaviour, Hint> Hints = new Dictionary<MonoBehaviour, Hint>();
        private static readonly List<MonoBehaviour> Gone = new List<MonoBehaviour>();
        private static FieldInfo set, action, label;
        private static bool looked;
        private static int logs;

        // From KeyHintPatch (ContextualButtonPrompt.Update), each frame for each hint that is switched on.
        public static void Check(MonoBehaviour prompt)
        {
            if (prompt == null) return;
            if (!Hints.TryGetValue(prompt, out var hint))
            {
                if (Hints.Count > 64) Sweep();
                hint = Read(prompt);
                Hints[prompt] = hint;
            }
            if (hint.Zone == VrToolBelt.Zone.None) return;
            if (!VrXr.IsRunning)
            {
                if (hint.Changed) Show(hint);
                return;
            }
            hint.Changed = true;
            var there = VrToolBelt.Last(true) == hint.Zone || VrToolBelt.Last(false) == hint.Zone;
            Draw(hint.Key, 0f);
            Draw(hint.Word, there ? 1f : 0f);
        }

        private static void Draw(List<Graphic> graphics, float alpha)
        {
            foreach (var graphic in graphics)
                if (graphic != null) graphic.canvasRenderer.SetAlpha(alpha);
        }

        private static void Show(Hint hint)
        {
            hint.Changed = false;
            Draw(hint.Key, 1f);
            Draw(hint.Word, 1f);
        }

        // Which action the hint is for and what it is made of, read once for each hint.
        private static Hint Read(MonoBehaviour prompt)
        {
            var hint = new Hint();
            if (!looked)
            {
                looked = true;
                var type = prompt.GetType();
                set = AccessTools.Field(type, "m_ControlActionSet");
                action = AccessTools.Field(type, "m_GameplayAction");
                label = AccessTools.Field(type, "m_LocPromptLabel");
                if (set == null || action == null) Plugin.Log.LogWarning("Key hints: the game's hint has no action to read, the flashlight's and the scanner's hints stay");
            }
            if (set == null || action == null) return hint;
            // The fourth of the hint's own sets of controls is the gameplay's (its enum is private).
            if (set.GetValue(prompt).ToString() != "Controls_Gameplay") return hint;
            var what = (GameplayActions.GameplayActionSet)action.GetValue(prompt);
            if (what == GameplayActions.GameplayActionSet.ToggleFlashlight) hint.Zone = VrToolBelt.Zone.Flashlight;
            else if (what == GameplayActions.GameplayActionSet.ActivateScanner) hint.Zone = VrToolBelt.Zone.Scanner;
            else return hint;
            // The whole hint: the nearest thing above the key's picture that also holds the word (in the game's HUD
            // '<name> Toggle Container', over 'Bracket/ControlSprite'); without a word, the picture's bracket.
            var word = label != null ? label.GetValue(prompt) as Component : null;
            var whole = prompt.transform;
            if (word != null)
            {
                for (var up = 0; up < 3 && whole.parent != null && !word.transform.IsChildOf(whole); up++) whole = whole.parent;
                // A word somewhere else on the HUD: only the picture is the hint's own then.
                if (!word.transform.IsChildOf(whole)) whole = prompt.transform;
            }
            else if (whole.parent != null) whole = whole.parent;
            foreach (var graphic in whole.GetComponentsInChildren<Graphic>(true))
            {
                if (word != null && graphic.transform.IsChildOf(word.transform)) hint.Word.Add(graphic);
                else hint.Key.Add(graphic);
            }
            if (word != null && hint.Word.Count == 0)
                foreach (var graphic in word.GetComponentsInChildren<Graphic>(true)) hint.Word.Add(graphic);
            if (logs++ < 6)
                Plugin.Log.LogInfo($"Key hints: the hint for {what} ('{VrUtil.Path(whole)}'): its key ({hint.Key.Count} part(s)) is not shown while VR runs, its word ({hint.Word.Count} part(s)) while a hand is at the {hint.Zone}'s place");
            return hint;
        }

        private static void Sweep()
        {
            Gone.Clear();
            foreach (var prompt in Hints.Keys) if (prompt == null) Gone.Add(prompt);
            foreach (var prompt in Gone) Hints.Remove(prompt);
        }

        // On unload (hot reload): the hints are back.
        private static void Release()
        {
            foreach (var hint in Hints.Values)
                if (hint.Changed) Show(hint);
            Hints.Clear();
        }
    }

    [HarmonyPatch]
    internal static class KeyHintPatch
    {
        private static MethodBase TargetMethod() => AccessTools.Method(AccessTools.TypeByName(VrKeyHints.PromptType), "Update");

        private static void Postfix(MonoBehaviour __instance) => VrKeyHints.Check(__instance);
    }
}
