using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;
using BepInEx.Logging;
using HarmonyLib;

namespace PatchCheck
{
    // Loads the game's assemblies and ShipbreakerVR.dll into a plain .NET Framework process and checks, without
    // starting the game, that every Harmony patch class finds its target method, can be applied, and that every
    // transpiler changes its method. Catches renamed game methods, typos in patch targets and transpilers that no
    // longer find their call after a game update, in a few seconds.
    //
    // PatchCheck --game <game folder> [--plugin <ShipbreakerVR.dll>]
    internal static class Program
    {
        private static readonly List<string> SearchDirs = new List<string>();
        private static int failures;
        private static int warnings;

        private static int Main(string[] args)
        {
            var game = Option(args, "--game");
            if (game == null || !Directory.Exists(game))
            {
                Console.Error.WriteLine("Usage: PatchCheck --game <game folder> [--plugin <ShipbreakerVR.dll>]");
                return 2;
            }
            var plugin = Option(args, "--plugin") ?? NewestBuild();
            if (plugin == null || !File.Exists(plugin))
            {
                Console.Error.WriteLine("ShipbreakerVR.dll not found; build src/ShipbreakerVR first or pass --plugin");
                return 2;
            }
            var managed = Path.Combine(game, "Shipbreaker_Data", "Managed");
            SearchDirs.AddRange(new[]
            {
                Path.GetDirectoryName(Path.GetFullPath(plugin)), managed,
                Path.Combine(game, "BepInEx", "core"), Path.Combine(game, "BepInEx", "plugins", "ShipbreakerVR"),
            });
            AppDomain.CurrentDomain.AssemblyResolve += Resolve;
            // AccessTools.TypeByName only searches loaded assemblies; the plugin looks up some HDRP and Doozy types by name.
            Preload(managed);
            return Run(plugin);
        }

        // Separate method so the BepInEx and Harmony types are only needed once the resolver is installed.
        [MethodImpl(MethodImplOptions.NoInlining)]
        private static int Run(string pluginPath)
        {
            Console.WriteLine($"Plugin: {pluginPath} ({File.GetLastWriteTime(pluginPath):yyyy-MM-dd HH:mm})");
            var plugin = Assembly.LoadFrom(pluginPath);

            // Patches log through Plugin.Log, which BepInEx sets in the plugin's Awake.
            var log = new List<string>();
            var source = new ManualLogSource("ShipbreakerVR");
            source.LogEvent += (sender, e) => log.Add($"{e.Level}: {e.Data}");
            var logField = plugin.GetType("ShipbreakerVR.Plugin")?.GetField("Log", BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic);
            if (logField != null) logField.SetValue(null, source);
            else Warn("Plugin.Log not found; patches that log will fail");

            var harmony = new Harmony("de.marczahn.shipbreakervr.patchcheck");
            var classes = AccessTools.GetTypesFromAssembly(plugin).Where(t => t.GetCustomAttributes(typeof(HarmonyPatch), false).Length > 0).ToList();
            foreach (var type in classes) Check(harmony, type);

            foreach (var method in harmony.GetPatchedMethods().ToList())
            {
                var info = Harmony.GetPatchInfo(method);
                if (info == null || !info.Transpilers.Any(p => p.owner == harmony.Id)) continue;
                var original = PatchProcessor.GetOriginalInstructions(method);
                var current = PatchProcessor.GetCurrentInstructions(method);
                var changed = original.Count != current.Count || original.Zip(current, (a, b) => a.opcode != b.opcode || !Equals(a.operand, b.operand)).Any(c => c);
                if (changed) Ok($"transpiler changes {Describe(method)}");
                else Fail($"transpiler changes nothing in {Describe(method)}");
            }
            harmony.UnpatchSelf();

            foreach (var line in log) Console.WriteLine($"     log  {line}");
            Console.WriteLine($"{classes.Count} patch classes, {failures} failure(s), {warnings} warning(s)");
            return failures > 0 ? 1 : 0;
        }

        private static void Check(Harmony harmony, Type type)
        {
            List<MethodBase> targets;
            try
            {
                targets = Targets(type);
            }
            catch (Exception e)
            {
                Fail($"{type.Name}: target lookup threw {Inner(e).GetType().Name}: {Inner(e).Message}");
                return;
            }
            var missing = targets.Count == 0 || targets.Any(t => t == null);
            if (missing)
            {
                Fail($"{type.Name}: target method not found");
                return;
            }
            try
            {
                var replaced = harmony.CreateClassProcessor(type).Patch();
                if (replaced == null || replaced.Count == 0) Fail($"{type.Name}: patched nothing");
                else Ok($"{type.Name} -> {string.Join(", ", targets.Select(Describe))}");
            }
            catch (Exception e)
            {
                var inner = Inner(e);
                // Some methods cannot be compiled outside Unity (engine calls in static constructors); the target itself was found.
                if (inner is TypeInitializationException || inner is System.Security.SecurityException || inner.Message.Contains("ECall"))
                    Warn($"{type.Name} -> {string.Join(", ", targets.Select(Describe))}: found, but cannot be applied outside the game ({inner.GetType().Name}: {inner.Message})");
                else
                    Fail($"{type.Name}: applying failed: {inner.GetType().Name}: {inner.Message}");
            }
        }

        // The patch class's target methods, resolved the way Harmony does it, but without patching.
        private static List<MethodBase> Targets(Type type)
        {
            var targetMethod = AccessTools.DeclaredMethod(type, "TargetMethod");
            if (targetMethod != null) return new List<MethodBase> { (MethodBase)Invoke(targetMethod) };
            var targetMethods = AccessTools.DeclaredMethod(type, "TargetMethods");
            if (targetMethods != null) return ((IEnumerable<MethodBase>)Invoke(targetMethods)).ToList();

            var info = HarmonyMethod.Merge(HarmonyMethodExtensions.GetFromType(type));
            if (info.declaringType == null) throw new InvalidOperationException("no target type in [HarmonyPatch] and no TargetMethod()");
            var t = info.declaringType;
            var args = info.argumentTypes;
            MethodBase method;
            switch (info.methodType ?? MethodType.Normal)
            {
                case MethodType.Getter:
                    method = AccessTools.DeclaredPropertyGetter(t, info.methodName) ?? AccessTools.PropertyGetter(t, info.methodName);
                    break;
                case MethodType.Setter:
                    method = AccessTools.DeclaredPropertySetter(t, info.methodName) ?? AccessTools.PropertySetter(t, info.methodName);
                    break;
                case MethodType.Constructor:
                    method = AccessTools.DeclaredConstructor(t, args);
                    break;
                case MethodType.StaticConstructor:
                    method = t.TypeInitializer;
                    break;
                default:
                    method = AccessTools.DeclaredMethod(t, info.methodName, args) ?? AccessTools.Method(t, info.methodName, args);
                    break;
            }
            return new List<MethodBase> { method };
        }

        private static object Invoke(MethodInfo method) => method.Invoke(null, new object[method.GetParameters().Length]);

        private static string Describe(MethodBase method) =>
            method == null ? "-" : $"{method.DeclaringType?.Name}.{method.Name}({string.Join(", ", method.GetParameters().Select(p => p.ParameterType.Name))})";

        private static Exception Inner(Exception e)
        {
            while (e is TargetInvocationException && e.InnerException != null) e = e.InnerException;
            return e;
        }

        private static void Ok(string text) => Console.WriteLine($"OK   {text}");

        private static void Warn(string text)
        {
            warnings++;
            Console.WriteLine($"WARN {text}");
        }

        private static void Fail(string text)
        {
            failures++;
            Console.WriteLine($"FAIL {text}");
        }

        private static Assembly Resolve(object sender, ResolveEventArgs args)
        {
            var name = new AssemblyName(args.Name).Name;
            var loaded = AppDomain.CurrentDomain.GetAssemblies().FirstOrDefault(a => a.GetName().Name == name);
            if (loaded != null) return loaded;
            foreach (var dir in SearchDirs)
            {
                var path = Path.Combine(dir, name + ".dll");
                if (File.Exists(path)) return Assembly.LoadFrom(path);
            }
            return null;
        }

        // Unity's copies of the framework assemblies stay out; the process has its own.
        private static void Preload(string managed)
        {
            foreach (var path in Directory.GetFiles(managed, "*.dll"))
            {
                var name = Path.GetFileNameWithoutExtension(path);
                if (name == "mscorlib" || name == "netstandard" || name == "System" || name.StartsWith("System.") ||
                    name.StartsWith("Mono.") || name.StartsWith("Microsoft.")) continue;
                try { Assembly.LoadFrom(path); }
                catch (Exception e) { Console.WriteLine($"     skip {name}: {e.GetType().Name}"); }
            }
        }

        // The newest ShipbreakerVR.dll under src/ShipbreakerVR/bin, searched upwards from this tool.
        private static string NewestBuild()
        {
            for (var dir = new DirectoryInfo(AppDomain.CurrentDomain.BaseDirectory); dir != null; dir = dir.Parent)
            {
                var bin = Path.Combine(dir.FullName, "src", "ShipbreakerVR", "bin");
                if (!Directory.Exists(bin)) continue;
                return Directory.GetFiles(bin, "ShipbreakerVR.dll", SearchOption.AllDirectories).OrderByDescending(File.GetLastWriteTimeUtc).FirstOrDefault();
            }
            return null;
        }

        private static string Option(string[] args, string name)
        {
            var i = Array.IndexOf(args, name);
            return i >= 0 && i + 1 < args.Length ? args[i + 1] : null;
        }
    }
}
