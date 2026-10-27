using System.Collections.Generic;
using System.Diagnostics;
using System.Reflection;
using System.Text;
using HarmonyLib;

namespace ShipbreakerVR
{
    // Which of the game's ECS systems take the main thread's time: the frame profile shows the system groups
    // (Update.SimulationSystemGroup, ...) as one step each; this times every system's Update (SystemBase,
    // ComponentSystem and JobComponentSystem, the three that implement it) by its class, groups left out since they
    // only hold their systems. A JobComponentSystem's time includes waiting for the jobs it completes. Only while the
    // frame time line records; logged with it (VrPerformance).
    internal static class VrSystemProfile
    {
        private const int Shown = 15;
        private const double MinShownMs = 0.05;
        private static readonly double TickMs = 1000.0 / Stopwatch.Frequency;

        private static readonly System.Type GroupType = AccessTools.TypeByName("Unity.Entities.ComponentSystemGroup");
        private static readonly Dictionary<System.Type, long> Ticks = new Dictionary<System.Type, long>();
        private static readonly List<KeyValuePair<System.Type, long>> Sorted = new List<KeyValuePair<System.Type, long>>();

        public static void Done(object system, long ticks)
        {
            var type = system.GetType();
            if (GroupType != null && GroupType.IsAssignableFrom(type)) return;
            Ticks.TryGetValue(type, out var sum);
            Ticks[type] = sum + ticks;
        }

        // Per frame over the given number of frames, longest first. Resets.
        public static string Describe(int frames)
        {
            if (frames <= 0 || Ticks.Count == 0)
            {
                Reset();
                return null;
            }
            Sorted.Clear();
            Sorted.AddRange(Ticks);
            Sorted.Sort((a, b) => b.Value.CompareTo(a.Value));
            long total = 0;
            foreach (var entry in Sorted) total += entry.Value;
            var text = new StringBuilder($"{Ticks.Count} systems {total * TickMs / frames:0.00} ms; ");
            var shown = 0;
            foreach (var entry in Sorted)
            {
                var ms = entry.Value * TickMs / frames;
                if (shown >= Shown || ms < MinShownMs) break;
                text.Append(shown++ > 0 ? ", " : "").Append($"{entry.Key.Name} {ms:0.00}");
            }
            text.Append(" ms");
            Reset();
            return text.ToString();
        }

        public static void Reset() => Ticks.Clear();
    }

    [HarmonyPatch]
    internal static class SystemUpdateTimerPatch
    {
        private static IEnumerable<MethodBase> TargetMethods()
        {
            foreach (var name in new[] { "Unity.Entities.SystemBase", "Unity.Entities.ComponentSystem", "Unity.Entities.JobComponentSystem" })
            {
                var method = AccessTools.Method(AccessTools.TypeByName(name), "Update");
                if (method != null) yield return method;
            }
        }

        private static void Prefix(out long __state) => __state = VrPerformance.Recording ? Stopwatch.GetTimestamp() : -1L;

        private static void Postfix(object __instance, long __state)
        {
            if (__state >= 0) VrSystemProfile.Done(__instance, Stopwatch.GetTimestamp() - __state);
        }
    }
}
