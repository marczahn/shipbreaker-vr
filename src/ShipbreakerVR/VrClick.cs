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

        // A mechanical clack of about 80 ms (Raik: the first, electronic click was too high): two knocks of a hard plastic
        // part, the button going down and then seating 22 ms later, each a burst of noise rung through a body resonance
        // (band pass, 750 Hz and 520 Hz) over a short low thump (170 Hz), dying away in a few milliseconds.
        private static AudioClip Make()
        {
            var length = (int)(Rate * 0.08f);
            var samples = new float[length];
            Knock(samples, 0f, 750f, 0.007f, 1f, 1);
            Knock(samples, 0.022f, 520f, 0.009f, 0.75f, 2);
            var most = 0f;
            foreach (var sample in samples) most = Mathf.Max(most, Mathf.Abs(sample));
            if (most > 0f)
                for (var i = 0; i < length; i++) samples[i] = samples[i] / most * 0.9f;
            var made = AudioClip.Create("ShipbreakerVR clack", length, 1, Rate, false);
            made.SetData(samples, 0);
            return made;
        }

        // One knock added to samples from start on: noise through a band pass at ring Hz, and a thump below it.
        private static void Knock(float[] samples, float start, float ring, float decay, float loudness, int seed)
        {
            var noise = new System.Random(seed);
            // A two-pole band pass (resonator), quality about 6.
            var w = 2f * Mathf.PI * ring / Rate;
            var r = 1f - w / 12f;
            var a1 = 2f * r * Mathf.Cos(w);
            var a2 = -r * r;
            float y1 = 0f, y2 = 0f;
            var from = (int)(start * Rate);
            for (var i = from; i < samples.Length; i++)
            {
                var t = (i - from) / (float)Rate;
                var hit = ((float)noise.NextDouble() * 2f - 1f) * Mathf.Exp(-t / 0.0012f);
                var y = hit + a1 * y1 + a2 * y2;
                y2 = y1;
                y1 = y;
                var body = y * Mathf.Exp(-t / decay) * 0.12f;
                var thump = Mathf.Sin(2f * Mathf.PI * 170f * t) * Mathf.Exp(-t / 0.006f) * 0.5f;
                samples[i] += (body + thump) * loudness;
            }
        }
    }
}
