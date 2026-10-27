using System.Collections.Generic;
using System.Diagnostics;
using System.Text;
using UnityEngine;
using UnityEngine.LowLevel;

namespace ShipbreakerVR
{
    // Where the main thread's time goes, step by step of Unity's player loop: a marker before every step of every phase
    // (EarlyUpdate.XRUpdate, Update.ScriptRunBehaviourUpdate, the game's ECS system groups, PostLateUpdate.
    // FinishFrameRendering, ...) and one after a phase's last, each taking a timestamp, so the time between two markers
    // is the step's. The release player has no profiler, and "game logic" in the frame time line is one span from our
    // Update to the start of rendering that also holds the wait for the headset; this splits it up, and shows in which
    // step the wait sits. Logged with the frame time line (VrPerformance), as the average per frame of the steps that
    // take the most. The game's ECS world rebuilds the player loop when it sets up its systems, so the markers are
    // checked once a second, like VrFrame's step, and put in again around whatever the loop now holds.
    internal static class VrFrameProfile
    {
        static VrFrameProfile() => VrSession.OnUnload("frame profile", Uninstall);

        private const int Shown = 12;
        private const double MinShownMs = 0.05;

        private static string[] names = new string[0];
        private static double[] sums = new double[0];
        private static double[] phaseSums = new double[0];
        private static int[] phaseOf = new int[0];
        private static string[] phaseNames = new string[0];
        private static int current = -1;
        private static long last;
        private static string signature;
        private static float nextCheck;
        private static readonly double TickMs = 1000.0 / Stopwatch.Frequency;

        // From VrSession.Start, after VrFrame.Install.
        public static void Install()
        {
            var loop = PlayerLoop.GetCurrentPlayerLoop();
            Remove(ref loop);
            Build(ref loop);
            PlayerLoop.SetPlayerLoop(loop);
            Plugin.Log.LogInfo($"Frame profile installed: {names.Length} steps in {phaseNames.Length} phases");
        }

        // From VrSession.Update, after VrFrame.Check: puts the markers back if something rebuilt the loop.
        public static void Check()
        {
            if (Time.unscaledTime < nextCheck) return;
            nextCheck = Time.unscaledTime + 1f;
            var loop = PlayerLoop.GetCurrentPlayerLoop();
            Remove(ref loop);
            if (Signature(loop) == signature) return;
            Build(ref loop);
            PlayerLoop.SetPlayerLoop(loop);
            Plugin.Log.LogInfo($"Frame profile installed again after the player loop changed: {names.Length} steps");
        }

        private static void Uninstall()
        {
            var loop = PlayerLoop.GetCurrentPlayerLoop();
            if (Remove(ref loop)) PlayerLoop.SetPlayerLoop(loop);
        }

        // The steps' averages per frame over the given number of frames, longest first, then the phases. Resets.
        public static string Describe(int frames)
        {
            if (frames <= 0 || names.Length == 0) return null;
            var order = new List<int>();
            for (var i = 0; i < sums.Length; i++)
                if (sums[i] * TickMs / frames >= MinShownMs) order.Add(i);
            order.Sort((a, b) => sums[b].CompareTo(sums[a]));
            var text = new StringBuilder();
            for (var n = 0; n < order.Count && n < Shown; n++)
                text.Append(n > 0 ? ", " : "").Append($"{names[order[n]]} {sums[order[n]] * TickMs / frames:0.00}");
            text.Append(" ms; phases");
            for (var p = 0; p < phaseNames.Length; p++)
                text.Append(p > 0 ? ", " : " ").Append($"{phaseNames[p]} {phaseSums[p] * TickMs / frames:0.00}");
            text.Append(" ms");
            Reset();
            return text.ToString();
        }

        public static void Reset()
        {
            System.Array.Clear(sums, 0, sums.Length);
            System.Array.Clear(phaseSums, 0, phaseSums.Length);
        }

        // A marker: closes the step that ran since the previous marker and opens the next one (-1 after a phase's last).
        private static void Mark(int next)
        {
            var now = Stopwatch.GetTimestamp();
            if (current >= 0)
            {
                var ticks = now - last;
                sums[current] += ticks;
                phaseSums[phaseOf[current]] += ticks;
            }
            current = next;
            last = now;
        }

        // Puts a marker before each step of each phase and one after its last.
        private static void Build(ref PlayerLoopSystem loop)
        {
            var stepNames = new List<string>();
            var stepPhases = new List<int>();
            var phases = new List<string>();
            var systems = loop.subSystemList ?? new PlayerLoopSystem[0];
            for (var p = 0; p < systems.Length; p++)
            {
                var steps = systems[p].subSystemList;
                if (steps == null || steps.Length == 0) continue;
                var phase = phases.Count;
                var phaseName = systems[p].type != null ? systems[p].type.Name : $"phase {p}";
                phases.Add(phaseName);
                var marked = new List<PlayerLoopSystem>(steps.Length * 2 + 1);
                foreach (var step in steps)
                {
                    var index = stepNames.Count;
                    stepNames.Add($"{phaseName}.{(step.type != null ? step.type.Name : "?")}");
                    stepPhases.Add(phase);
                    marked.Add(MarkerSystem(index));
                    marked.Add(step);
                }
                marked.Add(MarkerSystem(-1));
                systems[p].subSystemList = marked.ToArray();
            }
            names = stepNames.ToArray();
            phaseOf = stepPhases.ToArray();
            phaseNames = phases.ToArray();
            sums = new double[names.Length];
            phaseSums = new double[phaseNames.Length];
            current = -1;
            var unmarked = loop;
            Remove(ref unmarked);
            signature = Signature(unmarked);
        }

        private static PlayerLoopSystem MarkerSystem(int next) =>
            new PlayerLoopSystem { type = typeof(VrFrameProfile), updateDelegate = () => Mark(next) };

        // The loop's steps without our markers, to tell whether it changed.
        private static string Signature(PlayerLoopSystem loop)
        {
            var text = new StringBuilder();
            foreach (var phase in loop.subSystemList ?? new PlayerLoopSystem[0])
            {
                text.Append(phase.type?.FullName).Append(':');
                foreach (var step in phase.subSystemList ?? new PlayerLoopSystem[0]) text.Append(step.type?.FullName).Append(',');
                text.Append(';');
            }
            return text.ToString();
        }

        // Removes the markers (of any instance, by name: a hot reload brings a new type); true if there were any. Works
        // on a copy of the phases' step arrays, so a loop that is not set again stays as it was.
        private static bool Remove(ref PlayerLoopSystem loop)
        {
            var systems = loop.subSystemList;
            if (systems == null) return false;
            var removed = false;
            systems = (PlayerLoopSystem[])systems.Clone();
            for (var i = 0; i < systems.Length; i++)
            {
                var steps = systems[i].subSystemList;
                if (steps == null) continue;
                var kept = new List<PlayerLoopSystem>(steps.Length);
                foreach (var step in steps)
                    if (step.type == null || step.type.FullName != typeof(VrFrameProfile).FullName) kept.Add(step);
                if (kept.Count == steps.Length) continue;
                systems[i].subSystemList = kept.ToArray();
                removed = true;
            }
            loop.subSystemList = systems;
            return removed;
        }
    }
}
