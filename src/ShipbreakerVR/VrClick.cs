using UnityEngine;

namespace ShipbreakerVR
{
    // A click to hear when a button is pushed in (Raik, 0.29): heard through the suit, also in vacuum, so it plays as is,
    // not placed in the room. Made here from a few lines of sound (a short bright knock, then a lower one as the button
    // seats), no sound taken from the game. The game's own sound runs through Wwise; this plays through Unity's audio,
    // with a listener of its own on the VR camera if the scene has none (logged once).
    internal static class VrClick
    {
        private const int Rate = 48000;
        private const float Volume = 0.55f;
        private static AudioClip clip;
        private static AudioSource source;
        private static bool logged;

        public static void Play()
        {
            if (!Ready()) return;
            source.PlayOneShot(clip, Volume);
        }

        private static bool Ready()
        {
            if (source != null && clip != null) return true;
            var camera = VrCamera.Instance;
            if (camera == null) return false;
            if (clip == null) clip = Make();
            var holder = new GameObject("ShipbreakerVR click");
            Object.DontDestroyOnLoad(holder);
            source = holder.AddComponent<AudioSource>();
            source.spatialBlend = 0f;
            source.playOnAwake = false;
            source.bypassEffects = true;
            source.bypassListenerEffects = true;
            source.bypassReverbZones = true;
            var listeners = Object.FindObjectsOfType<AudioListener>();
            var active = 0;
            foreach (var listener in listeners)
                if (listener.isActiveAndEnabled) active++;
            if (active == 0) camera.gameObject.AddComponent<AudioListener>();
            if (!logged)
            {
                logged = true;
                Plugin.Log.LogInfo($"Click: Unity audio at {AudioSettings.outputSampleRate} Hz, {active} listener(s) in the scene{(active == 0 ? ", one added on the VR camera" : "")}");
            }
            return true;
        }

        // About 45 ms: a 3.2 kHz knock dying away in 4 ms with a little noise, and 12 ms later a 1.4 kHz one, softer.
        private static AudioClip Make()
        {
            var length = (int)(Rate * 0.045f);
            var samples = new float[length];
            var noise = new System.Random(7);
            for (var i = 0; i < length; i++)
            {
                var t = i / (float)Rate;
                var first = Mathf.Sin(2f * Mathf.PI * 3200f * t) * Mathf.Exp(-t / 0.004f);
                var crack = ((float)noise.NextDouble() * 2f - 1f) * Mathf.Exp(-t / 0.0015f) * 0.6f;
                var late = t - 0.012f;
                var second = late > 0f ? Mathf.Sin(2f * Mathf.PI * 1400f * late) * Mathf.Exp(-late / 0.006f) * 0.5f : 0f;
                samples[i] = Mathf.Clamp((first + crack + second) * 0.8f, -1f, 1f);
            }
            var made = AudioClip.Create("ShipbreakerVR click", length, 1, Rate, false);
            made.SetData(samples, 0);
            return made;
        }
    }
}
