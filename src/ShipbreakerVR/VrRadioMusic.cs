using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using BepInEx.Configuration;
using HarmonyLib;
using UnityEngine;

namespace ShipbreakerVR
{
    // The collected radio on the Hab's desk plays the shifts' music (Raik 2026-11-16): standing near it and looking at it,
    // A switches it on and, while on, on to the next song; B switches it off (VrHabStations shows the prompt).
    //  - It plays the game's own shift music, 'mus_level_music_play', on a sound emitter of its own at the radio. That
    //    is a Wwise music switch by the global state Main_Flow (and Level_Flow, Music_LevelIntroPlayed); for 'HAB' it has
    //    no music, which is why the first try stayed silent (Raik 2026-11-16). While the radio plays, Main_Flow is 'Level'
    //    and the level's intro counted as played; switched off with B, both are as they were. Leaving the Hab with it on,
    //    they are left to the game, which sets its own for where it goes. Its playlist tells which item it picked but
    //    takes no choice, so the next song is the music stopped and started anew, the playlist then picking another.
    //  - The Hab's own steady background sound (the hum and groans 'state_mainFlow_HAB' starts, on the emitter it is
    //    posted on; also 'sfx_HAB_start_radio' should it play) is turned down while the radio plays and back up when it
    //    stops. Its emitter is seen when the game posts the event (PostEventSeen).
    //    Wwise's states are logged by their ids (Main_Flow 'Level' and so on are not looked up).
    //  - The music is meant for the whole room (no distance in Wwise): it is made quieter here by the distance from the
    //    radio, as a radio's would be.
    // Nothing of it is saved; the radio is off whenever the Hab is entered.
    internal static class VrRadioMusic
    {
        static VrRadioMusic() => VrSession.OnUnload("radio music", Release);

        // On unload (hot reload): the music stops, Wwise's states are as they were and the Hab's background is back up.
        // The emitter and the listener go with the radio they hang on (VrRadio).
        private static void Release()
        {
            try
            {
                if (On)
                {
                    Stop(false);
                    RestoreStates();
                }
                Dampen(false);
            }
            catch (System.Exception e)
            {
                Plugin.Log.LogWarning($"Radio: switching off on unload failed ({e.Message})");
            }
            if (emitter != null) Object.Destroy(emitter);
            emitter = null;
            ears = null;
            HabRadios.Clear();
            StatesWere.Clear();
        }

        public static ConfigEntry<float> HabMusic;
        public static ConfigEntry<float> Loudness;

        private const string PlayEvent = "mus_level_music_play";
        private const string LayersFull = "mus_layers_setAllToMaxNow";
        private const string HabRadioEvent = "sfx_HAB_start_radio";
        private const string HabAmbienceEvent = "state_mainFlow_HAB";
        // The global states the shift music needs, and the values it plays at.
        private static readonly (string Group, string Value)[] MusicStates =
            { ("Main_Flow", "Level"), ("Music_LevelIntroPlayed", "Music_LevelIntroPlayedTrue") };
        private const int FadeMs = 1200;
        // Full loudness this near to the radio, Far's share of it this far off and beyond.
        private const float Near = 1.5f;
        private const float FarAt = 6f;
        private const float Far = 0.35f;
        // Where it comes from (Raik 2026-11-16: louder, and heard from the radio). The shift music is made for the whole
        // room, without a place: Wwise puts it in both ears alike, whatever the emitter's. So the radio's emitter is heard
        // by a listener of its own, whose loudness per speaker is set each frame by where the radio is from the head
        // (Wwise's per-listener speaker offsets, as for split screens): a channel towards the radio full, one away from it
        // down to Away, and a little quieter with the radio behind.
        private const float Away = 0.12f;
        private const float Behind = 0.7f;
        private const float OffsetStep = 0.5f;

        public static bool On { get; private set; }

        private static GameObject emitter;
        private static uint playing;
        private static uint habRadioId;
        private static uint habAmbienceId;
        // The states as they were before the radio set them, while it has.
        private static readonly List<(uint Group, uint Was)> StatesWere = new List<(uint, uint)>();
        private static readonly HashSet<GameObject> HabRadios = new HashSet<GameObject>();
        private static GameObject listener;
        private static bool listenerLooked;
        private static bool dampened;
        private static float volume = -1f;
        private static int logs;
        // The radio's own listener, the output's speakers and their directions, and the offsets last set.
        private static GameObject ears;
        private static bool earsWork;
        private static AkChannelConfig speakers;
        private static float[] speakerAngles;
        private static float[] offsets;
        // Worked out each frame while the radio plays (Aim), kept so no array is made each frame.
        private static float[] gains;

        public static void Bind()
        {
            if (HabMusic != null) return;
            HabMusic = VrSettings.Bind("Experimente", "RadioStationsmusikLeiser", 0.2f,
                "Experiment: while the collected radio plays, how loud the Hab's own background sound stays (0 to 1).");
            Loudness = VrSettings.Bind("Experimente", "RadioLautstaerke", 2f,
                "Experiment: how loud the collected radio plays near it (1 = as loud as the shift music, up to 16).");
        }

        // A on the radio: on, or on to the next song.
        public static void Next(Transform radio)
        {
            if (radio == null) return;
            if (On) Stop(false);
            Play(radio);
        }

        // B on the radio: off.
        public static void Off()
        {
            if (!On) return;
            Stop(true);
            RestoreStates();
            Plugin.Log.LogInfo("Radio: switched off");
        }

        // From VrFrame, each frame.
        public static void Update()
        {
            Bind();
            var radio = VrRadio.InHab;
            if (On && (radio == null || emitter == null))
            {
                // The radio gone (the Hab left, the place changed): its emitter went with it.
                On = false;
                playing = 0;
                emitter = null;
                ears = null;
                volume = -1f;
                // Left to the game, which sets its own for where it goes.
                if (StatesWere.Count > 0) Plugin.Log.LogInfo("Radio: gone while playing, the music states are left to the game");
                StatesWere.Clear();
            }
            Dampen(On);
            if (!On || radio == null) return;
            var head = VrCamera.Instance != null ? VrCamera.Instance.transform : null;
            var hearing = earsWork ? ears : Listener();
            if (head == null || hearing == null) return;
            var source = emitter.transform.position;
            var distance = Vector3.Distance(head.position, source) / Mathf.Max(0.01f, VrCamera.WorldScale);
            // Where the radio is from the head, seen from above: 0 ahead, 90 right, -90 left.
            var local = head.InverseTransformDirection(source - head.position);
            var side = Mathf.Atan2(local.x, local.z) * Mathf.Rad2Deg;
            var behind = (1f - Mathf.Cos(side * Mathf.Deg2Rad)) * 0.5f;
            var want = Mathf.Max(0f, Loudness.Value) * Mathf.Lerp(1f, Far, Mathf.InverseLerp(Near, FarAt, distance))
                * (earsWork ? Mathf.Lerp(1f, Behind, behind) : 1f);
            if (earsWork) Aim(side);
            if (Mathf.Abs(want - volume) < 0.01f) return;
            volume = want;
            AkSoundEngine.SetGameObjectOutputBusVolume(emitter, hearing, Mathf.Min(16f, volume));
        }

        // The radio's listener: its speakers louder towards the radio, quieter away from it, the loudest at 0 dB.
        private static void Aim(float side)
        {
            if (speakerAngles == null) return;
            if (gains == null || gains.Length != speakerAngles.Length) gains = new float[speakerAngles.Length];
            var most = 0f;
            for (var i = 0; i < gains.Length; i++)
            {
                gains[i] = 0f;
                if (float.IsNaN(speakerAngles[i])) continue;
                var off = Mathf.DeltaAngle(side, speakerAngles[i]) * Mathf.Deg2Rad;
                gains[i] = Mathf.Max(Away, 0.5f + 0.5f * Mathf.Cos(off));
                most = Mathf.Max(most, gains[i]);
            }
            var changed = offsets == null;
            offsets = offsets ?? new float[gains.Length];
            for (var i = 0; i < gains.Length; i++)
            {
                // The low-frequency speaker as it is.
                var db = float.IsNaN(speakerAngles[i]) ? 0f : 20f * Mathf.Log10(gains[i] / most);
                if (Mathf.Abs(db - offsets[i]) >= OffsetStep) changed = true;
                offsets[i] = db;
            }
            if (!changed) return;
            AkSoundEngine.SetListenerSpatialization(ears, true, speakers, offsets);
        }

        // A listener for the radio alone, with the output's speakers and where each one is (Wwise's channel order: the
        // ones in the mask's bit order, the low-frequency one last).
        private static void MakeEars()
        {
            if (ears != null && ears.transform.parent == emitter.transform) return;
            ears = new GameObject("ShipbreakerVR Radio Listener");
            ears.transform.SetParent(emitter.transform, false);
            ears.AddComponent<AkGameObj>();
            offsets = null;
            earsWork = false;
            try
            {
                speakers = AkSoundEngine.GetSpeakerConfiguration();
                var mask = speakers.uChannelMask;
                var bits = new (uint Bit, float Angle)[]
                {
                    (0x1, -30f), (0x2, 30f), (0x4, 0f), (0x10, -150f), (0x20, 150f), (0x100, 180f), (0x200, -90f), (0x400, 90f),
                };
                var angles = new List<float>();
                foreach (var (bit, angle) in bits) if ((mask & bit) != 0) angles.Add(angle);
                // Speakers above, as the ones below them.
                for (var bit = 0x800u; bit != 0 && bit <= 0x20000u; bit <<= 1)
                    if ((mask & bit) != 0) angles.Add(0f);
                if ((mask & 0x8) != 0) angles.Add(float.NaN);
                if (angles.Count != speakers.uNumChannels)
                {
                    // An output without a standard layout: two channels taken as left and right.
                    angles.Clear();
                    for (var i = 0; i < speakers.uNumChannels; i++) angles.Add(i == 0 ? -30f : i == 1 ? 30f : 0f);
                }
                speakerAngles = angles.ToArray();
                var listened = AkSoundEngine.SetListeners(emitter, new[] { AkSoundEngine.GetAkGameObjectID(ears) }, 1);
                earsWork = listened == AKRESULT.AK_Success;
                Plugin.Log.LogInfo($"Radio: heard by its own listener ({listened}), output with {speakers.uNumChannels} speaker(s), mask {mask:X}");
            }
            catch (System.Exception e)
            {
                Plugin.Log.LogWarning($"Radio: no listener of its own ({e.GetType().Name}: {e.Message}), heard as the game's music");
                earsWork = false;
            }
            if (!earsWork) AkSoundEngine.ResetListenersToDefault(emitter);
        }

        private static void Play(Transform radio)
        {
            if (emitter == null || emitter.transform.parent != radio)
            {
                emitter = new GameObject("ShipbreakerVR Radio Sound");
                emitter.transform.SetParent(radio, false);
                emitter.transform.localPosition = VrRadioModel.Body.center;
                emitter.AddComponent<AkGameObj>();
            }
            MakeEars();
            SetStates();
            playing = AkSoundEngine.PostEvent(PlayEvent, emitter,
                (uint)(AkCallbackType.AK_EndOfEvent | AkCallbackType.AK_MusicPlaylistSelect | AkCallbackType.AK_MusicPlayStarted), Heard, null);
            var layers = AkSoundEngine.PostEvent(LayersFull, emitter);
            On = playing != 0;
            volume = -1f;
            Plugin.Log.LogInfo(On
                ? $"Radio: plays the shift music (playing id {playing}, layers {(layers != 0 ? "full" : "not set")}), the Hab's background {(HabRadios.Count > 0 ? $"on {HabRadios.Count} emitter(s)" : "not seen yet")}"
                : $"Radio: '{PlayEvent}' did not start (its bank may not be loaded in the Hab)");
        }

        private static void SetStates()
        {
            if (StatesWere.Count > 0) return;
            var text = new List<string>();
            foreach (var (group, value) in MusicStates)
            {
                var groupId = AkSoundEngine.GetIDFromString(group);
                AkSoundEngine.GetState(groupId, out var was);
                StatesWere.Add((groupId, was));
                AkSoundEngine.SetState(group, value);
                text.Add($"{group} {was} -> {value}");
            }
            Plugin.Log.LogInfo($"Radio: music states set: {string.Join(", ", text)}");
        }

        private static void RestoreStates()
        {
            if (StatesWere.Count == 0) return;
            foreach (var (group, was) in StatesWere) AkSoundEngine.SetState(group, was);
            Plugin.Log.LogInfo($"Radio: music states back ({string.Join(", ", StatesWere.Select(s => s.Was))})");
            StatesWere.Clear();
        }

        private static void Stop(bool fade)
        {
            if (playing != 0) AkSoundEngine.StopPlayingID(playing, fade ? FadeMs : 300);
            playing = 0;
            On = false;
        }

        // What Wwise says about the music: which playlist item it picked, and when it ended.
        private static void Heard(object cookie, AkCallbackType type, AkCallbackInfo info)
        {
            if (info is AkMusicPlaylistCallbackInfo list)
            {
                if (logs++ < 40) Plugin.Log.LogInfo($"Radio: playlist {list.playlistID} picked item {list.uPlaylistSelection} of {list.uNumPlaylistItems}");
                return;
            }
            if (type == AkCallbackType.AK_MusicPlayStarted)
            {
                if (logs++ < 40) Plugin.Log.LogInfo("Radio: music started");
                return;
            }
            if (type != AkCallbackType.AK_EndOfEvent) return;
            if (info is AkEventCallbackInfo ended && ended.playingID != playing) return;
            if (logs++ < 40) Plugin.Log.LogInfo("Radio: the music ended");
            playing = 0;
            On = false;
            RestoreStates();
        }

        // The Hab's own music turned down while the radio plays, and back up after.
        private static void Dampen(bool down)
        {
            if (down == dampened || HabRadios.Count == 0) return;
            HabRadios.RemoveWhere(o => o == null);
            if (Listener() == null) return;
            var level = down ? Mathf.Clamp01(HabMusic.Value) : 1f;
            foreach (var habRadio in HabRadios) AkSoundEngine.SetGameObjectOutputBusVolume(habRadio, Listener(), level);
            dampened = down;
            Plugin.Log.LogInfo($"Radio: the Hab's background {(down ? $"down to {level:0.00}" : "back up")} ({HabRadios.Count} emitter(s))");
        }

        // The game's one listener: its audio service registers the proxy of its 'Listener' audio object as Wwise's default.
        internal static GameObject Listener()
        {
            if (listener != null) return listener;
            if (listenerLooked && Time.frameCount % 120 != 0) return null;
            listenerLooked = true;
            try
            {
                var global = Object.FindObjectOfType<Carbon.Audio.Unity.GlobalAudioListener>();
                var service = global != null ? Traverse.Create(global).Field("mAudioService").GetValue() : null;
                var audioObject = service != null ? Traverse.Create(service).Property("Listener").GetValue() : null;
                if (audioObject != null) listener = Traverse.Create(service).Method("GetProxyObject", audioObject).GetValue<GameObject>();
            }
            catch (System.Exception e)
            {
                Plugin.Log.LogWarning($"Radio: the game's sound listener not found ({e.GetType().Name}: {e.Message})");
            }
            Plugin.Log.LogInfo(listener != null ? $"Radio: the game's sound listener is '{listener.name}'" : "Radio: the game's sound listener not found yet");
            return listener;
        }

        // ---- The game's sounds ----

        // Every event the game posts on an emitter: the emitters of the Hab's own background are remembered, to turn
        // them down while the radio plays. (0.32.0 also wrote the first 150 sounds in the Hab to the log by name, read
        // from the game's SoundbanksInfo.xml, to find what plays there; that is found, and gone.)
        internal static void PostEventSeen(uint id, GameObject on)
        {
            if (habRadioId == 0) habRadioId = AkSoundEngine.GetIDFromString(HabRadioEvent);
            if (habAmbienceId == 0) habAmbienceId = AkSoundEngine.GetIDFromString(HabAmbienceEvent);
            if ((id != habRadioId && id != habAmbienceId) || on == null || !HabRadios.Add(on)) return;
            Plugin.Log.LogInfo($"Radio: the Hab's background '{(id == habRadioId ? HabRadioEvent : HabAmbienceEvent)}' plays on '{on.name}' (id {on.GetInstanceID()})");
            if (dampened && Listener() != null) AkSoundEngine.SetGameObjectOutputBusVolume(on, Listener(), Mathf.Clamp01(HabMusic.Value));
        }
    }

    // Sees every event the game posts on an emitter (its audio service goes through this one), for VrRadioMusic.
    [HarmonyPatch]
    internal static class RadioMusicSeePatch
    {
        private static IEnumerable<MethodBase> TargetMethods() =>
            typeof(AkSoundEngine).GetMethods(BindingFlags.Public | BindingFlags.Static)
                .Where(m => m.Name == "PostEvent" && m.GetParameters().Length >= 2
                    && m.GetParameters()[0].ParameterType == typeof(uint) && m.GetParameters()[1].ParameterType == typeof(GameObject));

        private static void Prefix(uint __0, GameObject __1)
        {
            try
            {
                VrRadioMusic.PostEventSeen(__0, __1);
            }
            catch (System.Exception e)
            {
                Plugin.Log.LogWarning($"Radio: seeing a sound failed ({e.Message})");
            }
        }
    }
}
