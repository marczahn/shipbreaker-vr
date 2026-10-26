using System.Collections.Generic;
using System.Linq;
using System.Reflection;

namespace ShipbreakerVR
{
    // Counts what the transpilers redirect and logs one line per patch after patching. A game update that renames a
    // type or changes a method body makes a transpiler match less, which used to show only as a smaller number in one of
    // ~70 Info lines; now it is a warning. A feature that needs a patch that failed or came out incomplete is switched
    // off instead of half working (HandAimWorks, TwoToolsWork), with a warning that says why.
    internal static class PatchReport
    {
        // The patches each feature needs: transpilers by their name here, the other patch classes by their class name.
        private static readonly string[] HandAimPatches = { "Aim", "Tool control", "AimPatch", "ToolInputPatch" };
        private static readonly string[] TwoToolsPatches = { "Two tools", "GatePatch", "UnequipPatch", "HandPatch", "EquipPatch" };

        private static readonly HashSet<string> Broken = new HashSet<string>();

        // Aiming with the hand (VrAim) and two tools at once (VrDualTools, which also needs aiming with the hand) have all
        // their patches. True until LogSummary has looked.
        public static bool HandAimWorks { get; private set; } = true;
        public static bool TwoToolsWork { get; private set; } = true;

        // From VrSession, when a patch class could not be applied.
        public static void Failed(string patchClass) => Broken.Add(patchClass);

        // Each transpiler, with the number of methods it reached with game 1.3.0.1263554. Fewer means a part of the
        // feature silently stopped working.
        private static readonly Dictionary<string, int> Expected = new Dictionary<string, int>
        {
            ["Aim"] = 23,
            ["Tool control"] = 34,
            ["Two tools"] = 11,
            ["Body rotation"] = 1,
            ["Braking"] = 1,
            ["Grapple swing"] = 1,
        };

        private static readonly Dictionary<string, Dictionary<MethodBase, int>> counts = new Dictionary<string, Dictionary<MethodBase, int>>();

        // Called at the end of a transpiler. Harmony runs all transpilers of a method again when another patch is added
        // to it, so a method counts once, with its last result.
        public static void Count(string patch, MethodBase original, int redirected, string what)
        {
            Plugin.Log.LogDebug($"{patch}: {Name(original)}: {redirected} {what} redirected");
            if (!counts.TryGetValue(patch, out var methods)) counts[patch] = methods = new Dictionary<MethodBase, int>();
            methods[original] = redirected;
        }

        public static void LogSummary()
        {
            foreach (var patch in Expected.Keys.Concat(counts.Keys.Except(Expected.Keys)))
            {
                counts.TryGetValue(patch, out var methods);
                var reached = methods?.Count ?? 0;
                var redirected = methods?.Values.Sum() ?? 0;
                var line = $"Patch {patch}: {redirected} call(s) redirected in {reached} method(s)";
                var problems = new List<string>();
                if (Expected.TryGetValue(patch, out var expected) && reached < expected) problems.Add($"expected {expected} methods");
                var empty = methods?.Where(m => m.Value == 0).Select(m => Name(m.Key)).ToList();
                if (empty != null && empty.Count > 0) problems.Add($"nothing redirected in {string.Join(", ", empty)}");
                if (problems.Count > 0)
                {
                    Broken.Add(patch);
                    Plugin.Log.LogWarning($"{line}: {string.Join("; ", problems)}. Part of this feature may not work.");
                }
                else Plugin.Log.LogInfo(line);
            }
            counts.Clear();
            HandAimWorks = Check("Aiming with the hand", HandAimPatches);
            TwoToolsWork = HandAimWorks && Check("Two tools at once", TwoToolsPatches);
        }

        private static bool Check(string feature, string[] patches)
        {
            var missing = patches.Where(Broken.Contains).ToList();
            if (missing.Count == 0) return true;
            Plugin.Log.LogWarning($"{feature} is off: patch {string.Join(", ", missing)} failed or is incomplete");
            return false;
        }

        private static string Name(MethodBase method) => $"{method.DeclaringType?.Name}.{method.Name}";
    }
}
