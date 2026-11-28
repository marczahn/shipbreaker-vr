using System.Diagnostics;
using System.Reflection;
using BBI.Unity.Game;
using HarmonyLib;
using UnityEngine;

namespace ShipbreakerVR
{
    // A new career's profile is made when the player leaves the desk (VrContractDesk), not as the account's name is
    // confirmed. The game makes, loads and saves the profile and writes its options all in one frame as the name page's
    // Next is pressed (ContractToggleCheck.SubmitContract): 700 ms in one frame at the desk, "a hard judder" (Raik
    // 2026-11-24). A short dark over it (0.33.0's first try) was a blink in the middle of the form; Raik: "can't that
    // happen when I leave the desk, before the intro video? Then it runs through smoothly."
    // The game's own flow (docs/NOTES.md): the footer's Next is one button for every page; its first press on the name
    // page makes the profile and turns to the next page (m_FinishCreatingProfile), each later press only writes voice,
    // meal and orientation into the profile (ApplyOptions). The fingerprint on the last page sets "skip tutorial" in the
    // profile (TrySkipTutorialOnProfile), the contract's end turns the apartment off and fades to black, the flight
    // video plays, and only the onboarding after it needs the profile (the fees screen books the starting debt into it).
    // So in VR at the desk: the name page's Next only turns the page; every later Next does nothing of the game's; the
    // fingerprint's "skip tutorial" is kept. Once the apartment is off and the fade has gone black (at most
    // AtMostAfter seconds), the profile is made in one go, as the game would have, with all the choices. Should
    // anything of the game's reach for the profile before (the fees screen, the onboarding's end), it is made right
    // then. Flat, or without the desk, the game does it as always.
    internal static class VrContractSubmit
    {
        // After the apartment went off: made once the fade is this dark, or after this many seconds whatever it is.
        private const float DarkEnough = 0.95f;
        private const float AtMostAfter = 1.5f;

        private static ContractToggleCheck pending;
        private static string name;
        private static DifficultyModeAsset mode;
        private static bool ignoreOxygenDrain, skipTutorial, passing;
        private static float leftSince = -1f;

        private static readonly MethodInfo ApplyOptions = AccessTools.Method(typeof(ContractToggleCheck), "ApplyOptions");
        private static readonly MethodInfo SkipTutorial = AccessTools.Method(typeof(ContractToggleCheck), "TrySkipTutorialOnProfile");

        static VrContractSubmit() => VrSession.OnUnload("contract submit", Drop);

        private static void Drop()
        {
            pending = null;
            name = null;
            mode = null;
            skipTutorial = false;
            leftSince = -1f;
        }

        // While a profile waits to be made.
        public static bool Waiting => pending != null;

        // The name page's Next (and every later one): in VR at the desk, the profile waits.
        internal static bool Defer(ContractToggleCheck check)
        {
            if (passing) return false;
            if (pending != null)
            {
                // A later page's Next: its voice, meal and orientation are read when the profile is made. Gone back to
                // the name page and changed the name: that name.
                if (pending == check)
                {
                    var field0 = Traverse.Create(check).Field("m_ProfileNameUI").Property("RequiredInputField").GetValue<TMPro.TMP_InputField>();
                    var now = field0 != null ? field0.text.Trim() : "";
                    var manager0 = PlayerProfileSaveLoadManager.Instance;
                    if (now.Length > 0 && now != name && manager0 != null && manager0.IsProfileNameUnique(now, out _))
                    {
                        Plugin.Log.LogInfo($"Contract submit: the name is now '{now}' (was '{name}')");
                        name = now;
                    }
                    return true;
                }
                Drop();
            }
            if (!VrXr.IsRunning || !VrContractDesk.Shown || ApplyOptions == null) return false;
            var traverse = Traverse.Create(check);
            var field = traverse.Field("m_ProfileNameUI").Property("RequiredInputField").GetValue<TMPro.TMP_InputField>();
            var typed = field != null ? field.text.Trim() : "";
            var page = traverse.Field("m_FinishCreatingProfile").GetValue<DoozyEventSender>();
            var difficulty = traverse.Property("CurrentDifficultyMode").GetValue<DifficultyModeAsset>();
            var manager = PlayerProfileSaveLoadManager.Instance;
            // What the game would refuse it refuses itself, with its own message.
            if (string.IsNullOrEmpty(typed) || page == null || difficulty == null || manager == null || !manager.IsProfileNameUnique(typed, out _))
                return false;
            pending = check;
            name = typed;
            mode = difficulty;
            var controller = traverse.Field("m_FrontEndProfileUIController").GetValue<FrontEndProfileUIController>();
            ignoreOxygenDrain = controller != null && controller.IgnoreOxygenDrain;
            skipTutorial = false;
            leftSince = -1f;
            Plugin.Log.LogInfo($"Contract submit: the profile '{name}' is made when the desk is left, the contract turns its page");
            page.SendEvent();
            return true;
        }

        // The fingerprint on the last page: "skip tutorial" kept for when the profile is made.
        internal static bool DeferSkip(ContractToggleCheck check)
        {
            if (passing || pending == null || pending != check) return false;
            skipTutorial = true;
            return true;
        }

        // From VrContractDesk.Update, every frame.
        public static void Update()
        {
            if (pending == null)
            {
                // The front end was left with the contract unfinished: nothing is made.
                if (name != null)
                {
                    Plugin.Log.LogInfo($"Contract submit: the contract was left unfinished, no profile '{name}' made");
                    Drop();
                }
                return;
            }
            if (VrContractDesk.ApartmentShown) return;
            if (leftSince < 0f) leftSince = Time.unscaledTime;
            var since = Time.unscaledTime - leftSince;
            if (VrContractDesk.FadeDarkness < DarkEnough && since < AtMostAfter && !VrContractDesk.FlightShown) return;
            Make($"the desk was left, {since:0.0} s into the fade");
        }

        // Whatever of the game's needs the profile, it is made now if it still waits.
        internal static void Need(string what)
        {
            if (pending != null) Make(what);
        }

        private static void Make(string when)
        {
            var check = pending;
            var profileName = name;
            var difficulty = mode;
            var skip = skipTutorial;
            var oxygen = ignoreOxygenDrain;
            Drop();
            if (check == null || difficulty == null) return;
            var clock = Stopwatch.StartNew();
            passing = true;
            try
            {
                var service = PlayerProfileService.Instance;
                // As the game's TryCreateProfile, then ApplyOptions with the choices of the later pages.
                if (!service.TryCreateNewPlayerProfile(profileName, difficulty, out var error))
                {
                    Plugin.Log.LogError($"Contract submit: the game could not make the profile '{profileName}' (message {error})");
                    return;
                }
                service.TryLoadPlayerProfile(profileName, out _);
                PlayerMetaProgressService.Instance.TrySaveMetaProgress(out _);
                service.Profile.IgnoreOxygenDrain = oxygen && difficulty.AllowsOxygenDrainToBeIgnored;
                ApplyOptions.Invoke(check, null);
                service.TrySavePlayerProfile(out _);
                if (skip && SkipTutorial != null) SkipTutorial.Invoke(check, null);
                Plugin.Log.LogInfo($"Contract submit: the profile '{profileName}' made in {clock.ElapsedMilliseconds} ms ({when}){(skip ? ", tutorial skipped" : "")}");
            }
            catch (System.Exception e)
            {
                Plugin.Log.LogError($"Contract submit: making the profile '{profileName}' failed: {e.InnerException ?? e}");
            }
            finally { passing = false; }
        }
    }

    [HarmonyPatch(typeof(ContractToggleCheck), "SubmitContract")]
    internal static class ContractSubmitPatch
    {
        private static bool Prefix(ContractToggleCheck __instance) => !VrContractSubmit.Defer(__instance);
    }

    [HarmonyPatch(typeof(ContractToggleCheck), nameof(ContractToggleCheck.TrySkipTutorialOnProfile))]
    internal static class ContractSkipTutorialPatch
    {
        private static bool Prefix(ContractToggleCheck __instance) => !VrContractSubmit.DeferSkip(__instance);
    }

    // The game's first uses of the new profile after the contract: the fees screen of the onboarding (it books the
    // starting debt into the profile) and the onboarding's end (it marks the profile done and saves it).
    [HarmonyPatch(typeof(FeesScreenController), "Initialize")]
    internal static class ContractFeesPatch
    {
        private static void Prefix() => VrContractSubmit.Need("the fees screen needs it");
    }

    [HarmonyPatch(typeof(OnboardingSceneController), "FinalizePlayerProfile")]
    internal static class ContractFinalizePatch
    {
        private static void Prefix() => VrContractSubmit.Need("the onboarding's end needs it");
    }
}
