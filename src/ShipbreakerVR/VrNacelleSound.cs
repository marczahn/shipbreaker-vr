using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using HarmonyLib;
using UnityEngine;

namespace ShipbreakerVR
{
    // The nacelle switch's sound follows the hand (Raik, 2026-11-13). The game plays 'sfx_flush_switch_valve' (4.24 s,
    // made for its 4.33 s switch animation) only after the switch is used, so worked by hand it came late. The game's own
    // posting of it is muted (FlushSwitchMutePatch); instead it plays here from where the hand has the switch: it runs
    // while the switch moves, pauses while it stands, and is set to the right place in it when it drifts off. Let go
    // before the end, it fades out; folded over, it plays out.
    internal static class VrNacelleSound
    {
        public const uint FlushSwitch = 738408611;
        private const string FlushSwitchName = "sfx_flush_switch_valve";
        // The sound's length and the animation's it was made for, in seconds.
        private const float SoundLength = 4.241847f;
        private const float ClipLength = 4.33f;
        // The animation's steps (VrNacelle): fold down until FoldEnd, pull until PullEnd, fold over until the end.
        private const float FoldEnd = 1.44f;
        private const float PullEnd = 3.25f;
        // Standing this long, the sound pauses; off by more than Drift from where the switch is, it is set there.
        private const float StandAfter = 0.08f;
        private const float Drift = 0.15f;
        private const int FadeOut = 200;
        // The suit hears what the hand holds (Touch_Transfer_Active_Grab), also where there is no air.
        private const uint TouchGrab = 3153169078;

        // True while this posts the sound, so the mute lets it through.
        internal static bool Posting;

        private static GameObject emitter;
        private static Transform on;
        private static uint playing;
        private static bool paused;
        private static float lastAt = -1f;
        private static float movedAt;
        private static int logs;

        // Each frame the switch is held, with where in the animation it is (in seconds).
        public static void Follow(Transform handle, float at)
        {
            if (handle == null) return;
            var moved = lastAt >= 0f && Mathf.Abs(at - lastAt) > 1e-4f;
            lastAt = at;
            if (moved) movedAt = Time.unscaledTime;
            var moving = Time.unscaledTime - movedAt < StandAfter;
            if (playing == 0)
            {
                if (!moved) return;
                Start(handle, at);
                return;
            }
            if (!moving)
            {
                if (!paused) Act(AkActionOnEventType.AkActionOnEventType_Pause);
                paused = true;
                return;
            }
            if (paused)
            {
                Act(AkActionOnEventType.AkActionOnEventType_Resume);
                paused = false;
            }
            Keep(at);
        }

        // Folded over: the rest plays out from where it is.
        public static void Done(float at)
        {
            if (playing == 0) return;
            if (paused) Act(AkActionOnEventType.AkActionOnEventType_Resume);
            paused = false;
            if (logs++ < 30) Plugin.Log.LogInfo($"Nacelle sound: switch folded over at {Position(at):0.00} s, its sound plays out");
            Forget();
        }

        // Let go before the end: it fades out.
        public static void Stop()
        {
            if (playing != 0)
            {
                AkSoundEngine.StopPlayingID(playing, FadeOut);
                if (logs++ < 30) Plugin.Log.LogInfo("Nacelle sound: switch let go, its sound fades out");
            }
            Forget();
        }

        private static void Forget()
        {
            playing = 0;
            paused = false;
            lastAt = -1f;
        }

        private static void Start(Transform handle, float at)
        {
            if (emitter == null || on != handle)
            {
                if (emitter != null) Object.Destroy(emitter);
                emitter = new GameObject("ShipbreakerVR_FlushSwitchSound");
                emitter.transform.SetParent(handle, false);
                emitter.AddComponent<AkGameObj>();
                on = handle;
            }
            AkSoundEngine.SetRTPCValue(TouchGrab, 1f, emitter);
            Posting = true;
            try
            {
                playing = AkSoundEngine.PostEvent(FlushSwitch, emitter, (uint)AkCallbackType.AK_EnableGetSourcePlayPosition, null, null);
            }
            finally
            {
                Posting = false;
            }
            paused = false;
            if (logs++ < 30) Plugin.Log.LogInfo($"Nacelle sound: '{FlushSwitchName}' starts with the switch at {at:0.00} s of its animation (playing id {playing})");
            if (playing == 0) return;
            Seek(at);
        }

        // Set to where the switch is, if it has drifted off.
        private static void Keep(float at)
        {
            if (AkSoundEngine.GetSourcePlayPosition(playing, out int position) != AKRESULT.AK_Success) return;
            var want = Position(at);
            if (Mathf.Abs(position / 1000f - want) <= Drift) return;
            Seek(at);
        }

        private static void Seek(float at)
        {
            if (emitter == null || playing == 0) return;
            var result = AkSoundEngine.SeekOnEvent(FlushSwitch, emitter, Mathf.RoundToInt(Position(at) * 1000f), false, playing);
            if (logs++ < 30) Plugin.Log.LogInfo($"Nacelle sound: set to {Position(at):0.00} s ({result})");
        }

        private static void Act(AkActionOnEventType action) => AkSoundEngine.ExecuteActionOnPlayingID(action, playing);

        // Where in the sound the animation's time at is.
        private static float Position(float at) => Mathf.Clamp(at, 0f, ClipLength) / ClipLength * SoundLength;

        // Where in the animation the switch is: by the step it is in and how far along it.
        public static float AnimationTime(int step, float foldShare, float pullShare)
        {
            switch (step)
            {
                case 0: return FoldEnd * Mathf.Clamp01(foldShare);
                case 1: return FoldEnd + (PullEnd - FoldEnd) * Mathf.Clamp01(pullShare);
                case 2: return PullEnd + (ClipLength - PullEnd) * Mathf.Clamp01(foldShare);
                default: return ClipLength;
            }
        }

        internal static bool Mutes(uint id) => id == FlushSwitch && !Posting && VrXr.IsRunning;
        internal static bool Mutes(string name) => name == FlushSwitchName && !Posting && VrXr.IsRunning;
    }

    // The game's own 'sfx_flush_switch_valve' is not played while in VR: VrNacelleSound plays it with the hand.
    [HarmonyPatch]
    internal static class FlushSwitchMutePatch
    {
        private static int logs;

        private static IEnumerable<MethodBase> TargetMethods() =>
            typeof(AkSoundEngine).GetMethods(BindingFlags.Public | BindingFlags.Static)
                .Where(m => m.Name == "PostEvent" && m.GetParameters().Length > 0 && m.GetParameters()[0].ParameterType == typeof(uint));

        private static bool Prefix(uint __0, ref uint __result)
        {
            if (!VrNacelleSound.Mutes(__0)) return true;
            __result = 0;
            if (logs++ < 10) Plugin.Log.LogInfo("Nacelle sound: the game's switch sound muted");
            return false;
        }
    }

    [HarmonyPatch]
    internal static class FlushSwitchMuteByNamePatch
    {
        private static IEnumerable<MethodBase> TargetMethods() =>
            typeof(AkSoundEngine).GetMethods(BindingFlags.Public | BindingFlags.Static)
                .Where(m => m.Name == "PostEvent" && m.GetParameters().Length > 0 && m.GetParameters()[0].ParameterType == typeof(string));

        private static bool Prefix(string __0, ref uint __result)
        {
            if (!VrNacelleSound.Mutes(__0)) return true;
            __result = 0;
            return false;
        }
    }
}
