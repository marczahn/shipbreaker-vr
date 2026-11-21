using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Reflection;
using System.Reflection.Emit;
using System.Text;
using HarmonyLib;
using Unity.Entities;
using UnityEngine;

namespace ShipbreakerVR
{
    // Test mode: what the scripts cost on the main thread, class by class. The frame profile (VrFrameProfile) only
    // gives Unity's steps as a whole: ScriptRunBehaviourUpdate and ScriptRunBehaviourLateUpdate together take about
    // 2.4 ms per frame in the bay, for every Update and LateUpdate of the game, its libraries and the mod. 'profile on'
    // (VrTestScript) puts a timestamp at the start and before every return of each Update, LateUpdate and FixedUpdate
    // that a loaded script class declares, with a transpiler, and the frame time line (VrPerformance, so name a stretch
    // with 'stats') gets a "Script steps" line: the totals per message and the methods that take the most, in
    // milliseconds per frame with their calls per frame. 'profile off', the end of the script and unloading take the
    // timestamps out again.
    // - The physics messages a script class declares (OnCollisionEnter, -Stay, -Exit, OnTriggerEnter, -Stay, -Exit) are
    //   timed too, and so is every system of the game's entity worlds, by its class, around Unity's Update of it: with
    //   the wait for the jobs it depends on, which is main thread time too, and without the jobs' own work on other
    //   threads. A group's time is what it spends outside its systems.
    // - 'profile on CLASS ...' also times every method of the named classes (the mod's by their short name, others by
    //   their full name), to see where inside a step the time goes. Methods Mono inlined count for their caller.
    // - The line is only logged for a stretch that was timed from its start to its end, so begin the stretch after
    //   'profile on' and end it (the next 'stats') before or in the frame of 'profile off'.
    // - A class's time is its own: what it spends in another timed method it calls (a base class's Update, a manager
    //   that runs others) counts for that one.
    // - Only the method's body is timed. Unity's call into it is not (count on some tenths of a microsecond per call,
    //   which is what the call counts are for), and neither are other patches' prefixes and postfixes on it, the mod's
    //   own among them.
    // - The classes come from the script objects loaded at that moment, prefabs included; generic classes are left out
    //   and named in the log. Run 'profile on' again to add what has been loaded since.
    // - The timestamps cost time themselves, per call about what "Script profile: ..." logs, so the frame time of a
    //   profiled stretch is not the game's.
    internal static class VrTestProfile
    {
        static VrTestProfile() => VrSession.OnUnload("script profile", Stop);

        private static readonly string[] Messages = { "Update", "LateUpdate", "FixedUpdate" };
        private static readonly string[] PhysicsMessages = { "OnCollisionEnter", "OnCollisionStay", "OnCollisionExit", "OnTriggerEnter", "OnTriggerStay", "OnTriggerExit" };
        // The kinds the totals are given for: the three messages, the methods of classes named with 'profile on', the
        // physics messages and the entity worlds' systems. The last three only where there were calls.
        private static readonly string[] Kinds = { "Update", "LateUpdate", "FixedUpdate", "named classes", "physics messages", "systems" };
        private const int Named = 3, Physics = 4, Systems = 5;
        private const BindingFlags Declared = BindingFlags.Instance | BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly;
        private const int Shown = 40;
        private const double MinShownMs = 0.003;
        internal const int MaxDepth = 32;
        private static readonly double TickMs = 1000.0 / Stopwatch.Frequency;

        private static Harmony harmony;
        private static readonly Dictionary<MethodBase, int> Indexes = new Dictionary<MethodBase, int>();
        private static readonly Dictionary<Type, int> SystemIndexes = new Dictionary<Type, int>();
        private static string[] names = new string[0];
        private static int[] messageOf = new int[0];
        private static long[] ticks = new long[0];
        private static int[] calls = new int[0];

        // The timed methods that are running, innermost last, with their start and the time spent in timed methods
        // they called.
        private static readonly int[] RunningIndex = new int[MaxDepth];
        private static readonly long[] RunningStart = new long[MaxDepth];
        private static readonly long[] RunningInner = new long[MaxDepth];
        private static int depth;
        // The frames the timestamps went in and came out; the numbers are a stretch's only if it lies between them.
        private static int startedFrame, stoppedFrame = -1;

        public static bool Running => harmony != null;

        // From VrTestScript: profile on [CLASS ...] | off.
        public static void Execute(string[] words)
        {
            var what = words.Length > 1 ? words[1].ToLowerInvariant() : "";
            if (what == "on") Start(words);
            else if (what == "off") Stop();
            else Plugin.Log.LogWarning($"Test script: 'profile {what}' is neither on nor off");
        }

        private static void Start(string[] words)
        {
            if (!Running) Forget();
            var types = new HashSet<Type>();
            foreach (var behaviour in Resources.FindObjectsOfTypeAll<MonoBehaviour>())
            {
                if (behaviour == null) continue;
                for (var type = behaviour.GetType(); type != null && type != typeof(MonoBehaviour); type = type.BaseType)
                    if (!types.Add(type)) break;
            }
            var found = new List<MethodInfo>();
            var kinds = new List<int>();
            var skipped = new List<string>();
            foreach (var type in types)
            {
                for (var m = 0; m < Messages.Length; m++) Find(type, Messages[m], Type.EmptyTypes, m, found, kinds, skipped);
                foreach (var message in PhysicsMessages)
                    Find(type, message, new[] { message.StartsWith("OnCollision") ? typeof(Collision) : typeof(Collider) }, Physics, found, kinds, skipped);
            }
            for (var w = 2; w < words.Length; w++)
            {
                var type = typeof(Plugin).Assembly.GetType($"{typeof(Plugin).Namespace}.{words[w]}", false, true) ?? AccessTools.TypeByName(words[w]);
                if (type == null)
                {
                    Plugin.Log.LogWarning($"Script profile: no class '{words[w]}'");
                    continue;
                }
                var before = found.Count;
                AddMethods(type, found);
                while (kinds.Count < found.Count) kinds.Add(Named);
                Plugin.Log.LogInfo($"Script profile: {found.Count - before} methods of {type.FullName}");
            }
            var systems = new List<Type>();
            for (var w = 0; w < World.All.Count; w++)
            {
                var all = World.All[w].Systems;
                for (var s = 0; s < all.Count; s++)
                {
                    var type = all[s]?.GetType();
                    if (type != null && !SystemIndexes.ContainsKey(type) && !systems.Contains(type)) systems.Add(type);
                }
            }

            var first = names.Length;
            var count = found.Count + systems.Count;
            Array.Resize(ref names, first + count);
            Array.Resize(ref messageOf, first + count);
            Array.Resize(ref ticks, first + count);
            Array.Resize(ref calls, first + count);
            var fresh = harmony == null;
            if (fresh) harmony = new Harmony($"{Plugin.Guid}/profile/{typeof(Plugin).Assembly.GetName().Name}");
            var transpiler = new HarmonyMethod(typeof(VrTestProfile), nameof(AddTimestamps));
            var watch = Stopwatch.StartNew();
            var failed = 0;
            for (var i = 0; i < found.Count; i++)
            {
                var method = found[i];
                names[first + i] = $"{method.DeclaringType.Name}.{method.Name}";
                messageOf[first + i] = kinds[i];
                Indexes[method] = first + i;
                try { harmony.Patch(method, transpiler: transpiler); }
                catch (Exception e)
                {
                    Indexes.Remove(method);
                    if (failed++ < 5) Plugin.Log.LogWarning($"Script profile: {names[first + i]} cannot be timed: {e.Message}");
                }
            }
            for (var i = 0; i < systems.Count; i++)
            {
                names[first + found.Count + i] = systems[i].Name;
                messageOf[first + found.Count + i] = Systems;
                SystemIndexes[systems[i]] = first + found.Count + i;
            }
            // Unity runs every system through one of these three, whatever its class.
            if (fresh)
                foreach (var kind in new[] { typeof(ComponentSystem), typeof(JobComponentSystem), typeof(SystemBase) })
                {
                    try
                    {
                        harmony.Patch(kind.GetMethod("Update", Type.EmptyTypes),
                            prefix: new HarmonyMethod(typeof(VrTestProfile), nameof(SystemEnter)), postfix: new HarmonyMethod(typeof(VrTestProfile), nameof(SystemExit)));
                    }
                    catch (Exception e) { Plugin.Log.LogWarning($"Script profile: the systems of {kind.Name} cannot be timed: {e.Message}"); }
                }
            depth = 0;
            Reset();
            startedFrame = Time.frameCount;
            stoppedFrame = -1;
            Plugin.Log.LogInfo($"Script profile: {found.Count - failed} methods of {types.Count} script classes and {systems.Count} systems timed " +
                $"({Indexes.Count} methods and {SystemIndexes.Count} systems in all, {failed} failed) " +
                $"in {watch.ElapsedMilliseconds} ms, timing costs about {PairCost():0} ns per call" +
                (skipped.Count > 0 ? $"; left out: {string.Join(", ", skipped.ToArray())}" : ""));
        }

        // The message as the class itself declares it, if it has a body to put timestamps into.
        private static void Find(Type type, string message, Type[] parameters, int kind, List<MethodInfo> found, List<int> kinds, List<string> skipped)
        {
            MethodInfo method;
            try { method = type.GetMethod(message, Declared & ~BindingFlags.Static, null, parameters, null); }
            catch (AmbiguousMatchException) { return; }
            if (method == null || method.IsAbstract || method.ReturnType != typeof(void) || Indexes.ContainsKey(method)) return;
            if (type.IsGenericType || method.GetMethodBody() == null) skipped.Add($"{type.Name}.{message}");
            else
            {
                found.Add(method);
                kinds.Add(kind);
            }
        }

        private static void SystemEnter(ComponentSystemBase __instance, out int __state)
        {
            if (SystemIndexes.TryGetValue(__instance.GetType(), out __state)) Enter(__state);
            else __state = -1;
        }

        private static void SystemExit(int __state)
        {
            if (__state >= 0) Exit(__state);
        }

        // The numbers stay for the stretch that ends in this frame or the next (Describe).
        public static void Stop()
        {
            if (harmony == null) return;
            var count = Indexes.Count;
            harmony.UnpatchSelf();
            Indexes.Clear();
            SystemIndexes.Clear();
            harmony = null;
            depth = 0;
            stoppedFrame = Time.frameCount;
            Plugin.Log.LogInfo($"Script profile: off, {count} methods and the systems as they were");
        }

        private static void Forget()
        {
            names = new string[0];
            messageOf = new int[0];
            ticks = new long[0];
            calls = new int[0];
            stoppedFrame = -1;
        }

        // Every method the class and the classes inside it declare, as far as it has a body of its own to time.
        private static void AddMethods(Type type, List<MethodInfo> found)
        {
            if (type.IsGenericType) return;
            foreach (var method in type.GetMethods(Declared))
            {
                if (method.IsAbstract || method.IsGenericMethod || Indexes.ContainsKey(method) || found.Contains(method)) continue;
                if (method.GetMethodBody() != null) found.Add(method);
            }
            foreach (var inner in type.GetNestedTypes(BindingFlags.Public | BindingFlags.NonPublic)) AddMethods(inner, found);
        }

        // Whether there are numbers to reset (VrPerformance, while a stretch settles).
        public static bool Holds => ticks.Length > 0;

        // The kinds' totals per frame over the given number of frames, then the methods that took the most. Resets.
        // Null for a stretch that was not timed from its start to its end.
        public static string Describe(int frames)
        {
            if (frames <= 0 || ticks.Length == 0) return null;
            var whole = Time.frameCount - startedFrame >= frames && (Running || Time.frameCount - stoppedFrame <= 1);
            var text = whole ? Describe(frames, names, messageOf, ticks, calls, TickMs) : null;
            if (Running) Reset();
            else Forget();
            return text;
        }

        internal static string Describe(int frames, string[] names, int[] messageOf, long[] ticks, int[] calls, double tickMs)
        {
            var text = new StringBuilder();
            for (var m = 0; m < Kinds.Length; m++)
            {
                var sum = 0L;
                var count = 0L;
                for (var i = 0; i < names.Length; i++)
                    if (messageOf[i] == m)
                    {
                        sum += ticks[i];
                        count += calls[i];
                    }
                if (m >= Named && count == 0) continue;
                text.Append(m > 0 ? ", " : "").Append($"{Kinds[m]} {sum * tickMs / frames:0.00} ms in {(double)count / frames:0} calls");
            }
            var order = new List<int>();
            for (var i = 0; i < names.Length; i++)
                if (ticks[i] * tickMs / frames >= MinShownMs) order.Add(i);
            order.Sort((a, b) => ticks[b].CompareTo(ticks[a]));
            text.Append("; most:");
            for (var n = 0; n < order.Count && n < Shown; n++)
                text.Append($" {names[order[n]]} {ticks[order[n]] * tickMs / frames:0.000} x{(double)calls[order[n]] / frames:0.#}");
            return text.ToString();
        }

        public static void Reset()
        {
            Array.Clear(ticks, 0, ticks.Length);
            Array.Clear(calls, 0, calls.Length);
        }

        // What one timed call costs on top, in nanoseconds: start and end around nothing.
        private static double PairCost()
        {
            if (ticks.Length == 0) return 0.0;
            const int Pairs = 20000;
            var start = Stopwatch.GetTimestamp();
            for (var i = 0; i < Pairs; i++)
            {
                Enter(0);
                Exit(0);
            }
            var cost = (Stopwatch.GetTimestamp() - start) * TickMs * 1e6 / Pairs;
            Reset();
            return cost;
        }

        // Called from the timed methods (AddTimestamps below).
        public static void Enter(int index) => Enter(index, Stopwatch.GetTimestamp());
        public static void Exit(int index) => Exit(index, Stopwatch.GetTimestamp());

        internal static void Enter(int index, long now)
        {
            // A method that threw never reached its end and stays in the list; a full list starts over.
            if (depth >= MaxDepth) depth = 0;
            RunningIndex[depth] = index;
            RunningStart[depth] = now;
            RunningInner[depth] = 0;
            depth++;
        }

        internal static void Exit(int index, long now)
        {
            // Normally the innermost; further out when methods called from this one threw.
            var at = depth - 1;
            while (at >= 0 && RunningIndex[at] != index) at--;
            if (at < 0 || index >= ticks.Length) return;
            var whole = now - RunningStart[at];
            ticks[index] += whole - RunningInner[at];
            calls[index]++;
            depth = at;
            if (at > 0) RunningInner[at - 1] += whole;
        }

        // For the tests: room for the given number of methods, nothing running.
        internal static void Prepare(int count)
        {
            ticks = new long[count];
            calls = new int[count];
            depth = 0;
        }

        internal static long TicksOf(int index) => ticks[index];
        internal static int CallsOf(int index) => calls[index];

        private static readonly MethodInfo EnterMethod = typeof(VrTestProfile).GetMethod(nameof(Enter), new[] { typeof(int) });
        private static readonly MethodInfo ExitMethod = typeof(VrTestProfile).GetMethod(nameof(Exit), new[] { typeof(int) });

        private static IEnumerable<CodeInstruction> AddTimestamps(IEnumerable<CodeInstruction> instructions, MethodBase original)
        {
            if (original == null || !Indexes.TryGetValue(original, out var index))
            {
                foreach (var instruction in instructions) yield return instruction;
                yield break;
            }
            yield return new CodeInstruction(OpCodes.Ldc_I4, index);
            yield return new CodeInstruction(OpCodes.Call, EnterMethod);
            foreach (var instruction in instructions)
            {
                if (instruction.opcode == OpCodes.Ret)
                {
                    // Jumps to the return have to pass the end call.
                    var load = new CodeInstruction(OpCodes.Ldc_I4, index);
                    load.labels.AddRange(instruction.labels);
                    load.blocks.AddRange(instruction.blocks);
                    instruction.labels.Clear();
                    instruction.blocks.Clear();
                    yield return load;
                    yield return new CodeInstruction(OpCodes.Call, ExitMethod);
                }
                yield return instruction;
            }
        }
    }
}
