using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using BepInEx;
using BepInEx.Bootstrap;
using HarmonyLib;
using Mono.Cecil;
using UnityEngine;
using Object = UnityEngine.Object;

namespace ShipbreakerVR.Dev
{
    // Loads BepInEx plugins from a dev folder and replaces them when their DLL changes, like BepInEx's ScriptEngine.
    // The DLL is read into memory (the build can overwrite it any time) and loaded under a new assembly name, because
    // Mono hands out an already loaded assembly of the same name instead of the new one. Old assemblies cannot be
    // unloaded; what they did is undone instead: the plugin's GameObject is destroyed, its components and the objects
    // it created are removed, and its Harmony patches are taken out. ModHooks covers what ShipbreakerVR cannot undo
    // on its own yet.
    internal class HotReload
    {
        private class Loaded
        {
            public string Guid;
            public string Name;
            public DateTime WriteTime;
            public Assembly Assembly;
            public GameObject Host;
            public ModHooks.State State;
        }

        private readonly string folder;
        private readonly Func<bool> canReload;
        private readonly Dictionary<string, Loaded> loaded = new Dictionary<string, Loaded>(StringComparer.OrdinalIgnoreCase);
        // Write times of DLLs that failed to load, so they are only retried once they change.
        private readonly Dictionary<string, DateTime> failed = new Dictionary<string, DateTime>(StringComparer.OrdinalIgnoreCase);
        private readonly DefaultAssemblyResolver resolver = new DefaultAssemblyResolver();
        private readonly List<string> dependencyDirs = new List<string>();
        private int generation;
        private bool all = true;
        private float nextScan;

        public HotReload(string folder, Func<bool> canReload)
        {
            this.folder = folder;
            this.canReload = canReload;
            Directory.CreateDirectory(folder);
            dependencyDirs.Add(Paths.PluginPath);
            dependencyDirs.AddRange(Directory.GetDirectories(Paths.PluginPath, "*", SearchOption.AllDirectories));
            foreach (var dir in dependencyDirs.Concat(new[] { Paths.ManagedPath, Paths.BepInExAssemblyDirectory, folder })) resolver.AddSearchDirectory(dir);
            // An assembly loaded from bytes has no folder to look for its dependencies in (e.g. the XR DLLs next to the
            // regular plugin), and neither has this plugin for the XR DLLs. The dev folder stays out: its DLLs only load renamed.
            AppDomain.CurrentDomain.AssemblyResolve += ResolveDependency;
        }

        private Assembly ResolveDependency(object sender, ResolveEventArgs args)
        {
            var name = new AssemblyName(args.Name).Name;
            var loaded = AppDomain.CurrentDomain.GetAssemblies().FirstOrDefault(a => a.GetName().Name == name);
            if (loaded != null) return loaded;
            foreach (var dir in dependencyDirs)
            {
                var path = Path.Combine(dir, name + ".dll");
                if (File.Exists(path)) return Assembly.LoadFrom(path);
            }
            return null;
        }

        // The newest assembly of the plugin with this GUID, if it was loaded from the dev folder.
        public Assembly FindAssembly(string guid) => loaded.Values.FirstOrDefault(l => l.Guid == guid)?.Assembly;

        public void RequestAll() => all = true;

        public IEnumerator Watch(Func<bool> auto)
        {
            while (true)
            {
                if ((all || (auto() && Time.unscaledTime >= nextScan)) && canReload())
                {
                    nextScan = Time.unscaledTime + 1f;
                    var force = all;
                    all = false;
                    foreach (var path in Changed(force)) yield return Reload(path);
                }
                yield return null;
            }
        }

        private List<string> Changed(bool force)
        {
            var result = new List<string>();
            try
            {
                var now = DateTime.UtcNow;
                foreach (var path in Directory.GetFiles(folder, "*.dll"))
                {
                    var time = File.GetLastWriteTimeUtc(path);
                    // Still being written by the build: wait until it has rested for a moment.
                    if ((now - time).TotalSeconds < 1.0) continue;
                    if (!force && loaded.TryGetValue(path, out var current) && current.WriteTime == time) continue;
                    if (!force && failed.TryGetValue(path, out var failedTime) && failedTime == time) continue;
                    result.Add(path);
                }
            }
            catch (Exception e)
            {
                DevPlugin.Log.LogError($"Scanning {folder} failed: {e.Message}");
            }
            return result;
        }

        private IEnumerator Reload(string path)
        {
            var time = File.GetLastWriteTimeUtc(path);
            ModHooks.State state = null;
            if (loaded.TryGetValue(path, out var old))
            {
                yield return Unload(old);
                loaded.Remove(path);
                state = old.State;
            }
            var plugin = Load(path);
            if (plugin == null)
            {
                failed[path] = time;
                yield break;
            }
            failed.Remove(path);
            plugin.WriteTime = time;
            loaded[path] = plugin;
            yield return null;
            yield return ModHooks.AfterLoad(plugin.Assembly, state);
        }

        private Loaded Load(string path)
        {
            try
            {
                var assembly = Assembly.Load(Rename(File.ReadAllBytes(path)));
                foreach (var type in AccessTools.GetTypesFromAssembly(assembly))
                {
                    if (type.IsAbstract || !typeof(BaseUnityPlugin).IsAssignableFrom(type)) continue;
                    var metadata = MetadataHelper.GetMetadata(type);
                    if (metadata == null) continue;
                    if (Chainloader.PluginInfos.ContainsKey(metadata.GUID))
                    {
                        DevPlugin.Log.LogWarning($"{metadata.Name} is also installed in {Paths.PluginPath}; remove it there to load it from {folder}");
                        return null;
                    }
                    var host = new GameObject($"Dev host: {metadata.Name}");
                    Object.DontDestroyOnLoad(host);
                    // Runs the plugin's Awake (config, Harmony patches) right away.
                    host.AddComponent(type);
                    DevPlugin.Log.LogInfo($"Loaded {metadata.Name} {metadata.Version} from {path} as {assembly.GetName().Name}");
                    return new Loaded { Guid = metadata.GUID, Name = metadata.Name, Assembly = assembly, Host = host };
                }
                DevPlugin.Log.LogWarning($"No BepInEx plugin in {path}");
            }
            catch (Exception e)
            {
                DevPlugin.Log.LogError($"Loading {path} failed: {e}");
            }
            return null;
        }

        private byte[] Rename(byte[] bytes)
        {
            using (var input = new MemoryStream(bytes))
            using (var definition = AssemblyDefinition.ReadAssembly(input, new ReaderParameters { AssemblyResolver = resolver }))
            using (var output = new MemoryStream())
            {
                definition.Name.Name = $"{definition.Name.Name}-dev{++generation}";
                definition.Write(output);
                return output.ToArray();
            }
        }

        private IEnumerator Unload(Loaded old)
        {
            DevPlugin.Log.LogInfo($"Unloading {old.Name} ({old.Assembly.GetName().Name})");
            try
            {
                old.State = ModHooks.BeforeUnload(old.Assembly);
            }
            catch (Exception e)
            {
                DevPlugin.Log.LogError($"Preparing the unload failed: {e}");
                old.State = new ModHooks.State();
            }
            // Give the old components a few frames to put things back (e.g. after VR was switched off).
            for (var i = 0; i < old.State.FramesToWait; i++) yield return null;
            if (old.Host != null) Object.Destroy(old.Host);
            // Destroy takes effect at the end of the frame; the plugin's OnDestroy runs then.
            yield return null;
            DestroyLeftovers(old.Assembly);
            Unpatch(old.Assembly);
            ModHooks.AfterUnload(old.State);
            yield return null;
        }

        // Components of the old assembly anywhere in the scenes, and the objects the plugin created (by their name prefix).
        private static void DestroyLeftovers(Assembly assembly)
        {
            var components = 0;
            var objects = 0;
            try
            {
                foreach (var behaviour in Resources.FindObjectsOfTypeAll<MonoBehaviour>())
                {
                    if (behaviour == null || behaviour.GetType().Assembly != assembly || !behaviour.gameObject.scene.IsValid()) continue;
                    Object.Destroy(behaviour);
                    components++;
                }
                foreach (var t in Resources.FindObjectsOfTypeAll<Transform>())
                {
                    if (t == null || !t.gameObject.scene.IsValid() || !t.name.StartsWith(ModHooks.ObjectPrefix)) continue;
                    if (t.parent != null && t.parent.name.StartsWith(ModHooks.ObjectPrefix)) continue;
                    Object.Destroy(t.gameObject);
                    objects++;
                }
            }
            catch (Exception e)
            {
                DevPlugin.Log.LogError($"Removing the old plugin's objects failed: {e}");
            }
            DevPlugin.Log.LogInfo($"Removed {components} component(s) and {objects} object(s) of the old plugin");
        }

        // Removes every patch whose method lives in the old assembly, whatever Harmony ID it was applied with.
        private static void Unpatch(Assembly assembly)
        {
            var harmony = new Harmony(DevPlugin.Guid);
            var removed = 0;
            foreach (var method in Harmony.GetAllPatchedMethods().ToList())
            {
                var info = Harmony.GetPatchInfo(method);
                if (info == null) continue;
                foreach (var patch in info.Prefixes.Concat(info.Postfixes).Concat(info.Transpilers).Concat(info.Finalizers))
                {
                    if (patch.PatchMethod?.DeclaringType?.Assembly != assembly) continue;
                    try
                    {
                        harmony.Unpatch(method, patch.PatchMethod);
                        removed++;
                    }
                    catch (Exception e)
                    {
                        DevPlugin.Log.LogError($"Unpatching {method.DeclaringType?.Name}.{method.Name} failed: {e.Message}");
                    }
                }
            }
            DevPlugin.Log.LogInfo($"Removed {removed} Harmony patch(es) of the old plugin");
        }
    }
}
