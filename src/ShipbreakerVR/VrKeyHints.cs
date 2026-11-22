using System.Collections.Generic;
using System.Reflection;
using BBI.Unity.Game;
using HarmonyLib;
using UnityEngine;

namespace ShipbreakerVR
{
    // The game's HUD names the key for the flashlight and the scanner (ContextualButtonPrompt: a key's picture and its
    // label, for an action of the gameplay controls). In VR neither has a key: the flashlight is switched with the
    // left hand at the left of the helmet, the scanner is taken from the right of the helmet (VrToolBelt), so the hints
    // told something that does not work (Raik's list of 2026-11-21, point 7). While VR runs those two hints are made
    // unseen, by their scale, so the game's own switching of them stays as it is; with VR off they are back.
    internal static class VrKeyHints
    {
        static VrKeyHints() => VrSession.OnUnload("key hints", Release);

        internal const string PromptType = "BBI.Unity.Game.ContextualButtonPrompt";

        private sealed class Hint
        {
            public bool Hides;
            public bool Hidden;
            public Vector3 Scale;
            // Its label, where that is not a part of the prompt itself.
            public Transform Label;
            public Vector3 LabelScale;
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
            if (!hint.Hides) return;
            var hide = VrXr.IsRunning;
            if (hide == hint.Hidden) return;
            hint.Hidden = hide;
            if (hide)
            {
                hint.Scale = prompt.transform.localScale;
                prompt.transform.localScale = Vector3.zero;
                if (hint.Label != null)
                {
                    hint.LabelScale = hint.Label.localScale;
                    hint.Label.localScale = Vector3.zero;
                }
            }
            else Show(prompt, hint);
        }

        private static void Show(MonoBehaviour prompt, Hint hint)
        {
            if (prompt != null) prompt.transform.localScale = hint.Scale;
            if (hint.Label != null) hint.Label.localScale = hint.LabelScale;
        }

        // Which action the hint is for, read once for each hint.
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
            if (what != GameplayActions.GameplayActionSet.ToggleFlashlight && what != GameplayActions.GameplayActionSet.ActivateScanner) return hint;
            hint.Hides = true;
            var text = label != null ? label.GetValue(prompt) as Component : null;
            if (text != null && !text.transform.IsChildOf(prompt.transform)) hint.Label = text.transform;
            if (logs++ < 6) Plugin.Log.LogInfo($"Key hints: the hint for {what} ('{VrUtil.Path(prompt.transform)}') is not shown while VR runs");
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
            foreach (var pair in Hints)
                if (pair.Value.Hidden) Show(pair.Key, pair.Value);
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
