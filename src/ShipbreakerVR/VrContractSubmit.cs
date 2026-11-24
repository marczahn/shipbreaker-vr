using System.Collections.Generic;
using System.Diagnostics;
using System.Reflection;
using BBI.Unity.Game;
using HarmonyLib;
using UnityEngine;

namespace ShipbreakerVR
{
    // A new career's account name confirmed at the desk (VrContractDesk): the game makes, loads and saves the profile
    // and writes its options, all in one frame (ContractToggleCheck.SubmitContract). Raik 2026-11-24: "a hard judder"
    // there, 706 to 768 ms in one frame; the mod's own reading ahead of the radio was not it (it waits now, VrRadio).
    // A frame that long cannot be made shorter from here, so in VR it is hidden: the view darkens quickly first, the
    // game does its work while it is dark (the headset holds the dark frame meanwhile), and the view comes back.
    // Flat, or without the desk, the game does it at once as always. How long each part took is logged ("Contract
    // submit: ..."), to see what of the game's takes the time.
    internal static class VrContractSubmit
    {
        // Darkening in, at least this many frames fully dark before the game's work, and coming back, in seconds.
        private const float DarkIn = 0.12f;
        private const int DarkFrames = 2;
        private const float DarkOut = 0.3f;

        private static ContractToggleCheck pending;
        private static float since;
        private static int darkFrames;
        private static bool passing;
        private static float backSince = -1f;
        private static readonly MethodInfo Submit = AccessTools.Method(typeof(ContractToggleCheck), "SubmitContract");

        static VrContractSubmit() => VrSession.OnUnload("contract submit", () =>
        {
            pending = null;
            backSince = -1f;
            Darkness = 0f;
        });

        // How dark the view is made, 0 to 1 (VrUi, with the game's fades).
        public static float Darkness { get; private set; }

        // While the game's work waits for the dark (VrContractDesk does not step on through the contract meanwhile).
        public static bool Busy => pending != null;

        // The game asks to submit: in VR at the desk it waits for the dark.
        internal static bool Defer(ContractToggleCheck check)
        {
            if (passing || Submit == null || !VrXr.IsRunning || !VrContractDesk.Shown) return false;
            if (pending != null) return true;
            pending = check;
            since = Time.unscaledTime;
            darkFrames = 0;
            backSince = -1f;
            Plugin.Log.LogInfo("Contract submit: the view darkens before the game makes the profile");
            return true;
        }

        // From VrContractDesk.Update, every frame (also while the desk is not shown, to let the dark go again).
        public static void Update()
        {
            if (pending != null)
            {
                Darkness = Mathf.Clamp01((Time.unscaledTime - since) / DarkIn);
                if (Darkness < 1f) return;
                // Rendered dark for a few frames, so the frame the headset holds is a dark one.
                if (++darkFrames <= DarkFrames) return;
                var check = pending;
                pending = null;
                backSince = Time.unscaledTime;
                if (check == null) return;
                var clock = Stopwatch.StartNew();
                passing = true;
                try { Submit.Invoke(check, null); }
                catch (System.Exception e) { Plugin.Log.LogError($"Contract submit: the game's submit failed: {e.InnerException ?? e}"); }
                finally { passing = false; }
                Plugin.Log.LogInfo($"Contract submit: the game took {clock.ElapsedMilliseconds} ms while dark");
                backSince = Time.unscaledTime;
                return;
            }
            if (backSince < 0f) return;
            Darkness = 1f - Mathf.Clamp01((Time.unscaledTime - backSince) / DarkOut);
            if (Darkness <= 0f) backSince = -1f;
        }
    }

    [HarmonyPatch(typeof(ContractToggleCheck), "SubmitContract")]
    internal static class ContractSubmitPatch
    {
        private static bool Prefix(ContractToggleCheck __instance) => !VrContractSubmit.Defer(__instance);
    }

    // How long the parts of making a new profile take, while a career starts (at most a few dozen lines a game).
    [HarmonyPatch]
    internal static class ContractSubmitTimingPatch
    {
        private static int logs;

        private static IEnumerable<MethodBase> TargetMethods()
        {
            var methods = new[]
            {
                AccessTools.Method(typeof(LynxUserOptions), nameof(LynxUserOptions.HotWriteAndReload)),
                AccessTools.Method(typeof(LynxControlsUserOptions), nameof(LynxControlsUserOptions.ApplyOptions)),
                AccessTools.Method(typeof(PlayerProfileService), nameof(PlayerProfileService.TryCreateNewPlayerProfile)),
                AccessTools.Method(typeof(PlayerProfileService), nameof(PlayerProfileService.TryLoadPlayerProfile)),
                AccessTools.Method(typeof(PlayerProfileService), nameof(PlayerProfileService.TrySavePlayerProfile)),
                AccessTools.Method(typeof(PlayerMetaProgressService), nameof(PlayerMetaProgressService.TrySaveMetaProgress)),
                AccessTools.Method(typeof(DoozyEventSender), nameof(DoozyEventSender.SendEvent)),
            };
            foreach (var method in methods)
                if (method != null) yield return method;
        }

        private static void Prefix(out Stopwatch __state) => __state = VrContractDesk.CareerStarting && logs < 40 ? Stopwatch.StartNew() : null;

        private static void Postfix(Stopwatch __state, MethodBase __originalMethod)
        {
            if (__state == null || __state.ElapsedMilliseconds < 3) return;
            logs++;
            Plugin.Log.LogInfo($"Contract submit: {__originalMethod.DeclaringType?.Name}.{__originalMethod.Name} took {__state.ElapsedMilliseconds} ms");
        }
    }
}
