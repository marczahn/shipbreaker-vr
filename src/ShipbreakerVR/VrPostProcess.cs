using System;
using System.Collections.Generic;
using System.Reflection;
using HarmonyLib;
using UnityEngine.Rendering;
using UnityEngine.Rendering.HighDefinition;

namespace ShipbreakerVR
{
    // The game's own full-screen effects (scanlines and CRT curve when a shift starts, RetroLookPro glitches, ...) are
    // custom post-processes whose shaders only know flat textures. In VR they sample the eye texture array wrongly and
    // fill the view with grey and oversized scanlines. For the VR camera we pass the image through unchanged instead.
    [HarmonyPatch]
    internal static class CustomPostProcessPatch
    {
        // Assemblies with effects that might not be loaded yet when the plugin patches.
        private static readonly string[] EffectAssemblies = { "RetroLookProHDRP.Effects" };

        private static readonly HashSet<string> Logged = new HashSet<string>();

        private static IEnumerable<MethodBase> TargetMethods()
        {
            foreach (var name in EffectAssemblies)
            {
                try { Assembly.Load(name); }
                catch (Exception e) { Plugin.Log.LogWarning($"Effect assembly {name} not loaded: {e.Message}"); }
            }
            var parameters = new[] { typeof(CommandBuffer), typeof(HDCamera), typeof(RTHandle), typeof(RTHandle) };
            foreach (var assembly in AppDomain.CurrentDomain.GetAssemblies())
            {
                // HDRP's own effects work in VR.
                if (assembly == typeof(CustomPostProcessVolumeComponent).Assembly) continue;
                Type[] types;
                try { types = assembly.GetTypes(); }
                catch (ReflectionTypeLoadException e) { types = e.Types; }
                catch (Exception) { continue; }
                foreach (var type in types)
                {
                    if (type == null || type.IsAbstract || !typeof(CustomPostProcessVolumeComponent).IsAssignableFrom(type)) continue;
                    var render = AccessTools.DeclaredMethod(type, "Render", parameters);
                    if (render == null) continue;
                    // Harmony asks for the targets more than once.
                    if (Logged.Add(type.FullName)) Plugin.Log.LogInfo($"Full-screen effect bypassed in VR: {type.FullName}");
                    yield return render;
                }
            }
        }

        private static bool Prefix(CustomPostProcessVolumeComponent __instance, CommandBuffer __0, HDCamera __1, RTHandle __2, RTHandle __3)
        {
            var vr = VrCamera.Instance;
            if (vr == null || __1.camera != vr.Camera) return true;
            HDUtils.BlitCameraTexture(__0, __2, __3);
            var name = __instance.GetType().Name;
            if (Logged.Add("skip " + name)) Plugin.Log.LogInfo($"Effect '{name}' skipped for the VR camera");
            return false;
        }
    }
}
