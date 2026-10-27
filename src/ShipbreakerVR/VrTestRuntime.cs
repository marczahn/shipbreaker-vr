using System;
using System.IO;
using BepInEx;

namespace ShipbreakerVR
{
    // [Test] MockRuntime: VR starts with Unity's mock OpenXR runtime instead of the headset's, so the whole VR path
    // (rendering both eyes, the controllers, the mod's features) runs without a headset; VrTestXr gives the head and
    // the controllers their poses. The mock runtime comes with Unity's OpenXR package 1.2.8
    // (MockRuntime/windows/x64/openxr_loader.dll, a loader and runtime in one), copied as mock_runtime.dll into
    // BepInEx\plugins\ShipbreakerVR\MockRuntime: under its own name, since the game has already loaded the real loader
    // as openxr_loader.dll. It is not part of a release (development only).
    // The OpenXR loader picks the runtime from the manifest named by the environment variable XR_RUNTIME_JSON, which
    // takes precedence over the system's active runtime; the variable is set for this process only, right before the
    // loader starts.
    internal static class VrTestRuntime
    {
        public static string Folder => Path.Combine(Path.Combine(Paths.PluginPath, "ShipbreakerVR"), "MockRuntime");

        public static void Select()
        {
            if (!VrSettings.TestMode.Value || !VrSettings.MockRuntime.Value) return;
            var library = Path.Combine(Folder, "mock_runtime.dll");
            if (!File.Exists(library))
            {
                Plugin.Log.LogError($"Test/MockRuntime is on, but {library} is missing; starting VR with the system's runtime");
                return;
            }
            var manifest = Path.Combine(Folder, "mock_runtime.json");
            File.WriteAllText(manifest, "{\n  \"file_format_version\": \"1.0.0\",\n  \"runtime\": {\n    \"api_version\": \"1.0\",\n    \"name\": \"Unity Mock Runtime\",\n    \"library_path\": \"./mock_runtime.dll\"\n  }\n}\n");
            Environment.SetEnvironmentVariable("XR_RUNTIME_JSON", manifest);
            Plugin.Log.LogInfo($"Test: VR starts with Unity's mock runtime ({manifest})");
        }
    }
}
