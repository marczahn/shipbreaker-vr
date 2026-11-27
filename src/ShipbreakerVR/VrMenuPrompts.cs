using System;
using System.Reflection;
using BBI.Unity.Game;
using HarmonyLib;
using UnityEngine;

namespace ShipbreakerVR
{
    // Raik (2026-11-27): in the main menu (the campaigns and so on) the button prompts showed the keyboard's keys in VR.
    // The game's menu prompts (ContextualButtonPrompt) pick keyboard or gamepad pictures by the last input it saw; the
    // controllers are a gamepad to the game (VrMenuInput, Xbox style: A/B/X/Y where the Touch controllers have theirs),
    // but a key pressed last (F3, F4, ...) or nothing pressed yet left them on the keyboard's. While VR runs a prompt
    // never goes to the keyboard's pictures: it takes the Xbox ones, which match the controllers. When VR starts, the
    // prompts showing go to the controllers' pictures; when it stops, back to what the last input says.
    [HarmonyPatch]
    internal static class VrMenuPrompts
    {
        static VrMenuPrompts() => VrSession.OnUnload("menu prompts", Release);

        private static readonly Type PromptType = AccessTools.TypeByName("BBI.Unity.Game.ContextualButtonPrompt");
        private static readonly MethodInfo Change = PromptType != null ? AccessTools.Method(PromptType, "ChangeControlType") : null;
        private static readonly MethodInfo FollowInput = PromptType != null ? AccessTools.Method(PromptType, "OnLastInputTypeChanged") : null;
        private static readonly FieldInfo Current = PromptType != null ? AccessTools.Field(PromptType, "mCurrControlSet") : null;
        private static readonly Type SetType = PromptType != null ? AccessTools.Inner(PromptType, "ControlSet") : null;
        private static bool changing;
        private static bool wasVr;
        private static int logs;

        private static bool Prepare() => Change != null && Current != null && SetType != null;

        private static MethodBase TargetMethod() => Change;

        private static void Postfix(object __instance)
        {
            if (changing || !VrXr.IsRunning || Convert.ToInt32(Current.GetValue(__instance)) != 0) return;
            ToControllers(__instance);
        }

        private static void ToControllers(object prompt)
        {
            changing = true;
            try { Change.Invoke(prompt, new[] { Enum.Parse(SetType, "Xbox_One") }); }
            catch (Exception e) { if (logs++ < 3) Plugin.Log.LogWarning($"Menu prompts: could not show the controllers' picture: {e.InnerException?.Message ?? e.Message}"); }
            finally { changing = false; }
        }

        // Once a frame (VrFrame): the prompts showing follow VR starting and stopping.
        public static void Update()
        {
            var vr = VrXr.IsRunning;
            if (vr == wasVr || !Prepare()) return;
            wasVr = vr;
            var count = 0;
            foreach (var prompt in UnityEngine.Object.FindObjectsOfType(PromptType))
            {
                if (vr) ToControllers(prompt);
                else if (FollowInput != null && LynxControls.Instance != null)
                {
                    try { FollowInput.Invoke(prompt, new object[] { LynxControls.Instance.LastInputType }); }
                    catch (Exception) { }
                }
                count++;
            }
            if (logs++ < 10) Plugin.Log.LogInfo($"Menu prompts: {count} showing go to the {(vr ? "controllers'" : "last input's")} pictures");
        }

        private static void Release()
        {
            if (!wasVr || !Prepare() || FollowInput == null || LynxControls.Instance == null) return;
            foreach (var prompt in UnityEngine.Object.FindObjectsOfType(PromptType))
            {
                try { FollowInput.Invoke(prompt, new object[] { LynxControls.Instance.LastInputType }); }
                catch (Exception) { }
            }
            wasVr = false;
        }
    }
}
