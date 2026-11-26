using System.Collections.Generic;
using System.Reflection;
using BBI.Unity.Game;
using Doozy.Engine.UI;
using HarmonyLib;
using UnityEngine;
using UnityEngine.Events;
using UnityEngine.Playables;
using UnityEngine.Timeline;

namespace ShipbreakerVR
{
    // Raik (2026-11-26): "all video sequences skipped with one press of A", and the dialogues in the station (the Hab, not
    // a shift) with Y, "one line or one speaker after the other, so the talk can be skipped piece by piece".
    // A, one press:
    // - the game's videos (LynxVideoPlayer: logos, the flight to the contract, the closing ones), whether or not the
    //   game lets the gamepad skip that one;
    // - the onboarding's video of the LYNX copy and the extraction after it (OnboardingSceneController): the game skips
    //   them with B or Start only, to the cloning; A now does the same there (its popups take A as Confirm already);
    // - the text before the campaign's desk (PRF_IntroCanvas: an InputHandlerFrontEnd "Action - Skip On Confirm"), which
    //   waited for the gamepad's Confirm.
    // Y, in the Hab: the line playing now stops (LynxSpeechCancelEvent for that speaker only); a sequence of lines goes
    // on with its next by itself. The scenes after a shift are timelines of lines (or one speech): there the timeline
    // jumps to the next line's start and the scene's clock (Hab3DController.mWaitTimeForFade) by as much, so after the
    // last line the scene ends as it would have. Y with B held is the diagnostics' (VrDiagnostics), left out.
    internal static class VrSkip
    {
        private static int logs;

        public static bool APressed => VrXr.IsRunning && VrInput.Right.Valid && VrInput.Right.Primary.Pressed;

        public static void Log(string message)
        {
            if (logs++ < 60) Plugin.Log.LogInfo("Skip: " + message);
        }
    }

    [HarmonyPatch(typeof(LynxVideoPlayer), "LateUpdate")]
    internal static class VrSkipVideo
    {
        private static readonly AccessTools.FieldRef<LynxVideoPlayer, bool> Started = AccessTools.FieldRefAccess<LynxVideoPlayer, bool>("mVideoStarted");
        private static readonly MethodInfo Stop = AccessTools.Method(typeof(LynxVideoPlayer), "StopVideo");

        private static bool Prefix(LynxVideoPlayer __instance)
        {
            if (!Started(__instance) || !VrSkip.APressed) return true;
            VrSkip.Log($"A stops the video '{__instance.name}'");
            Stop.Invoke(__instance, null);
            return false;
        }
    }

    [HarmonyPatch(typeof(OnboardingSceneController), "Update")]
    internal static class VrSkipOnboarding
    {
        private static readonly AccessTools.FieldRef<OnboardingSceneController, OnboardingSceneController.OnboardingState> State =
            AccessTools.FieldRefAccess<OnboardingSceneController, OnboardingSceneController.OnboardingState>("mCurrentState");
        private static readonly AccessTools.FieldRef<OnboardingSceneController, PlayableDirector> Video =
            AccessTools.FieldRefAccess<OnboardingSceneController, PlayableDirector>("m_Video");
        private static readonly AccessTools.FieldRef<OnboardingSceneController, PlayableDirector> Extraction =
            AccessTools.FieldRefAccess<OnboardingSceneController, PlayableDirector>("m_ExtractionTimeline");
        private static readonly AccessTools.FieldRef<OnboardingSceneController, GameObject> ExtractionScreen =
            AccessTools.FieldRefAccess<OnboardingSceneController, GameObject>("m_ExtractionScreenContainer");
        private static readonly AccessTools.FieldRef<OnboardingSceneController, GameObject> Welcome =
            AccessTools.FieldRefAccess<OnboardingSceneController, GameObject>("m_WelcomeContainer");
        private static readonly AccessTools.FieldRef<OnboardingSceneController, UIView> Popup1 =
            AccessTools.FieldRefAccess<OnboardingSceneController, UIView>("m_Popup1UIView");
        private static readonly FieldInfo KillPlayerStop = AccessTools.Field(typeof(OnboardingSceneController), "m_KillPlayerStopEvent");
        private static readonly FieldInfo Skip1 = AccessTools.Field(typeof(OnboardingSceneController), "m_Skip1Event");
        private static readonly MethodInfo SetState = AccessTools.Method(typeof(OnboardingSceneController), "SetSpecificState");
        private static readonly MethodInfo Fire = AccessTools.Method(typeof(OnboardingSceneController), "FireAudioEvent");

        // As the game's own skip (Update, on B or Start) before the cloning: the state first, so the video stopping does
        // not advance it once more (OnVideoStopped).
        private static void Prefix(OnboardingSceneController __instance)
        {
            if (!VrSkip.APressed) return;
            var state = State(__instance);
            if (state != OnboardingSceneController.OnboardingState.PlayVideo && state != OnboardingSceneController.OnboardingState.Extracting) return;
            VrSkip.Log($"A skips the onboarding from {state} to the cloning");
            SetState.Invoke(__instance, new object[] { OnboardingSceneController.OnboardingState.Cloning });
            var video = Video(__instance);
            if (video != null && video.state == PlayState.Playing) video.Stop();
            var extraction = Extraction(__instance);
            if (extraction != null && extraction.state == PlayState.Playing)
            {
                extraction.Stop();
                var screen = ExtractionScreen(__instance);
                if (screen != null) screen.SetActive(false);
                Fire.Invoke(__instance, new[] { KillPlayerStop.GetValue(__instance) });
                Main.EventSystem.Post(TriggerableCancelSpeechEvent.GetEvent(null, cancelAllSpeech: true));
            }
            var welcome = Welcome(__instance);
            if (welcome != null) welcome.SetActive(false);
            var popup = Popup1(__instance);
            if (popup != null) popup.Hide();
            Fire.Invoke(__instance, new[] { Skip1.GetValue(__instance) });
        }
    }

    // Raik (2026-11-26): the first list of the debts after the videos "sped up, so everything stands there at once and
    // one only confirms with the thumbprint". A while the list is counting up: its lines come one a frame (the game's own
    // way, with their sounds), then the total goes to its end at once and the thumbprint comes up, but only once A is let
    // go, so the same press does not confirm the thumbprint right away (it is selected as the total ends).
    [HarmonyPatch(typeof(FeesScreenController), "Update")]
    internal static class VrSkipDebtList
    {
        private static readonly AccessTools.FieldRef<FeesScreenController, bool> Animating = AccessTools.FieldRefAccess<FeesScreenController, bool>("mDebtScreenAnimating");
        private static readonly AccessTools.FieldRef<FeesScreenController, double> Change = AccessTools.FieldRefAccess<FeesScreenController, double>("mDebtChange");
        private static readonly AccessTools.FieldRef<FeesScreenController, double> Amount = AccessTools.FieldRefAccess<FeesScreenController, double>("mAnimAmount");
        private static readonly AccessTools.FieldRef<FeesScreenController, double> Start = AccessTools.FieldRefAccess<FeesScreenController, double>("mStartAmount");
        private static readonly AccessTools.FieldRef<FeesScreenController, float> Clock = AccessTools.FieldRefAccess<FeesScreenController, float>("mTime");
        private static readonly AccessTools.FieldRef<FeesScreenController, float> TotalClock = AccessTools.FieldRefAccess<FeesScreenController, float>("mTotalTime");
        private static readonly AccessTools.FieldRef<FeesScreenController, float> LastTick = AccessTools.FieldRefAccess<FeesScreenController, float>("mLastTotalTickTime");
        private static readonly AccessTools.FieldRef<FeesScreenController, int> Shown = AccessTools.FieldRefAccess<FeesScreenController, int>("mEntriesShown");
        private static readonly AccessTools.FieldRef<FeesScreenController, int> Cap = AccessTools.FieldRefAccess<FeesScreenController, int>("mEntryCap");
        private static FeesScreenController hurried;

        private static bool Prefix(FeesScreenController __instance)
        {
            if (!Animating(__instance)) { if (hurried == __instance) hurried = null; return true; }
            if (hurried != __instance)
            {
                if (!VrSkip.APressed) return true;
                hurried = __instance;
                VrSkip.Log("A speeds the list of the debts up to its end");
            }
            // Past the first wait, the next line due now.
            TotalClock(__instance) = 1e6f;
            Clock(__instance) = 1e6f;
            if (Shown(__instance) < Cap(__instance)) return true;
            if (VrInput.Right.Valid && VrInput.Right.Primary.Down) return false;
            // The total at its end: the next tick ends the count (IncrementTotal) and brings up the thumbprint.
            Amount(__instance) = Start(__instance) + Change(__instance);
            LastTick(__instance) = 0f;
            return true;
        }
    }

    [HarmonyPatch(typeof(InputHandlerFrontEnd), "Update")]
    internal static class VrSkipIntroText
    {
        private static readonly AccessTools.FieldRef<InputHandlerFrontEnd, FrontEndActions.FrontEndActionSet> Action =
            AccessTools.FieldRefAccess<InputHandlerFrontEnd, FrontEndActions.FrontEndActionSet>("m_Action");
        private static readonly AccessTools.FieldRef<InputHandlerFrontEnd, UnityEvent> OnAction =
            AccessTools.FieldRefAccess<InputHandlerFrontEnd, UnityEvent>("m_OnAction");
        // The one taken by A, for a moment: the gamepad's Confirm for the same press may come a frame later.
        private static InputHandlerFrontEnd fired;
        private static float firedAt;

        private static bool Prefix(InputHandlerFrontEnd __instance)
        {
            if (fired == __instance && Time.unscaledTime - firedAt < 1f) return false;
            if (!VrSkip.APressed || Action(__instance) != FrontEndActions.FrontEndActionSet.Confirm || !__instance.name.Contains("Skip")) return true;
            var action = OnAction(__instance);
            if (action == null) return true;
            VrSkip.Log($"A skips the text ('{__instance.name}')");
            fired = __instance;
            firedAt = Time.unscaledTime;
            action.Invoke();
            return false;
        }
    }

    // Keeps the game's speech controller, which knows the sequences of lines still going on.
    [HarmonyPatch(typeof(LynxSpeechEventController), "OnTriggeredSpeechEvent")]
    internal static class VrSkipSpeechEvents
    {
        public static LynxSpeechEventController Controller;

        private static void Postfix(LynxSpeechEventController __instance) => Controller = __instance;
    }

    [HarmonyPatch(typeof(Hab3DController), "Update")]
    internal static class VrSkipHabLine
    {
        private static readonly AccessTools.FieldRef<Hab3DController, bool> InAfterShift = AccessTools.FieldRefAccess<Hab3DController, bool>("mIsInAfterShiftHab");
        private static readonly AccessTools.FieldRef<Hab3DController, HabAfterShiftAsset> Scene = AccessTools.FieldRefAccess<Hab3DController, HabAfterShiftAsset>("mCurrentAfterShiftData");
        private static readonly AccessTools.FieldRef<Hab3DController, float> SceneClock = AccessTools.FieldRefAccess<Hab3DController, float>("mWaitTimeForFade");
        private static readonly AccessTools.FieldRef<Hab3DController, EntityBlueprintComponent> SceneDirector =
            AccessTools.FieldRefAccess<Hab3DController, EntityBlueprintComponent>("mAttachedPlayableDirectorEBC");
        private static readonly AccessTools.FieldRef<LynxSpeechEventController, List<LynxSpeechSequenceController>> Sequences =
            AccessTools.FieldRefAccess<LynxSpeechEventController, List<LynxSpeechSequenceController>>("mLynxSpeechSequenceControllers");
        private static readonly AccessTools.FieldRef<LynxSpeechSequenceController, int> Played = AccessTools.FieldRefAccess<LynxSpeechSequenceController, int>("mPlayCount");
        private static readonly AccessTools.FieldRef<LynxSpeechSequenceController, int> Lines = AccessTools.FieldRefAccess<LynxSpeechSequenceController, int>("mNumEventsInSequence");
        private static readonly List<LynxSpeechViewComponent> Views = new List<LynxSpeechViewComponent>();
        private static Transform speech;

        private static void Postfix(Hab3DController __instance)
        {
            if (!VrXr.IsRunning || __instance.IsHabPaused || GameSession.CurrentGameState == GameSession.GameState.Paused) return;
            if (!VrInput.Left.Valid || !VrInput.Left.Secondary.Pressed || VrInput.Right.Secondary.Down) return;
            if (speech == null)
            {
                var found = GameObject.Find("SpeechObjects");
                if (found == null) return;
                speech = found.transform;
            }
            speech.GetComponentsInChildren(Views);
            LynxSpeechViewComponent speaking = null;
            foreach (var view in Views)
                if (view != null && view.SpeechObject != null && view.SpeechObject.CurrentlyPlayingPlaybackID.IsValid) { speaking = view; break; }
            Views.Clear();
            if (speaking == null) return;
            var goesOn = SequenceGoesOn(speaking.gameObject);
            Main.EventSystem.Post(LynxSpeechCancelEvent.GetEvent(new List<LynxSpeechViewComponent> { speaking }, false, false));
            VrSkip.Log($"Y ends the line of '{speaking.name}'{(goesOn ? ", its sequence goes on" : "")}");
            if (goesOn || !InAfterShift(__instance)) return;
            var scene = Scene(__instance);
            if (scene == null) return;
            if (scene.SceneType == HabAfterShiftAsset.AfterShiftSceneType.Speech && scene.SpeechToPlay != null)
            {
                // The last line: the scene ends as after its speech.
                SceneClock(__instance) = Mathf.Max(SceneClock(__instance), scene.SpeechToPlay.SpeechLength);
            }
            else if (scene.SceneType == HabAfterShiftAsset.AfterShiftSceneType.PlayableDirector)
            {
                var holder = SceneDirector(__instance);
                var director = holder != null ? holder.GetComponent<PlayableDirector>() : null;
                if (director != null) NextLine(__instance, director);
            }
        }

        // Whether a sequence of lines on that speaker has lines still to come.
        private static bool SequenceGoesOn(GameObject speaker)
        {
            var controller = VrSkipSpeechEvents.Controller;
            if (controller == null) return false;
            foreach (var sequence in Sequences(controller))
                if (sequence != null && !sequence.Cancelled && sequence.SourceObject == speaker && Played(sequence) < Lines(sequence)) return true;
            return false;
        }

        // The scene's timeline on to the next line's start (its speech tracks only), the scene's clock by as much; after
        // the last line to its end.
        private static void NextLine(Hab3DController hab, PlayableDirector director)
        {
            var now = director.time;
            var next = double.MaxValue;
            if (director.playableAsset is TimelineAsset timeline)
            {
                foreach (var track in timeline.GetOutputTracks())
                {
                    if (track == null || track.muted || track.GetType().Name != "TriggerableSpeechAssetTrack") continue;
                    foreach (var clip in track.GetClips())
                        if (clip.start > now + 0.05 && clip.start < next) next = clip.start;
                }
            }
            if (next == double.MaxValue)
            {
                var end = (float)director.duration;
                SceneClock(hab) = Mathf.Max(SceneClock(hab), end);
                VrSkip.Log($"Y: that was the scene's last line, it ends ({now:0.0} of {end:0.0} s)");
                return;
            }
            var ahead = (float)(next - now);
            director.time = next;
            SceneClock(hab) += ahead;
            VrSkip.Log($"Y: the scene goes on to its next line at {next:0.0} s ({ahead:0.0} s on)");
        }
    }
}
