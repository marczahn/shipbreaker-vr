using System;
using System.IO;
using System.Runtime.InteropServices;
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
    // The mock runtime logs through a trace interface that Unity hands it when it is the game's loader (SetXRTrace);
    // loaded as a runtime nobody does, and its first log line (an event in xrWaitFrame) crashed the game. So the mod
    // loads it first and hands it a trace function of its own.
    internal static class VrTestRuntime
    {
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        private delegate void TraceFn(int level, IntPtr format);

        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        private delegate void SetTraceFn(IntPtr trace);

        [DllImport("kernel32", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern IntPtr LoadLibrary(string path);

        [DllImport("kernel32", CharSet = CharSet.Ansi)]
        private static extern IntPtr GetProcAddress(IntPtr module, string name);

        // Kept for as long as the runtime may call it: the game session.
        private static TraceFn trace;
        private static IntPtr traceInterface;
        private static int traced;

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
            if (!GiveTrace(library)) return;
            Environment.SetEnvironmentVariable("XR_RUNTIME_JSON", manifest);
            Plugin.Log.LogInfo($"Test: VR starts with Unity's mock runtime ({manifest})");
        }

        private static bool GiveTrace(string library)
        {
            if (traceInterface != IntPtr.Zero) return true;
            var module = LoadLibrary(library);
            var setTrace = module != IntPtr.Zero ? GetProcAddress(module, "SetXRTrace") : IntPtr.Zero;
            if (setTrace == IntPtr.Zero)
            {
                Plugin.Log.LogError($"Test/MockRuntime: {library} cannot be loaded or has no SetXRTrace (error {Marshal.GetLastWin32Error()}); starting VR with the system's runtime");
                return false;
            }
            trace = Trace;
            traceInterface = Marshal.AllocHGlobal(IntPtr.Size);
            Marshal.WriteIntPtr(traceInterface, Marshal.GetFunctionPointerForDelegate(trace));
            Marshal.GetDelegateForFunctionPointer<SetTraceFn>(setTrace)(traceInterface);
            return true;
        }

        // The format string only: its arguments (varargs) cannot be read from here. The first lines, then every 1000th.
        private static void Trace(int level, IntPtr format)
        {
            var n = traced++;
            if (n >= 50 && n % 1000 != 0) return;
            try { Plugin.Log.LogInfo($"Mock runtime ({level}): {Marshal.PtrToStringAnsi(format)?.Trim()}"); }
            catch { }
        }
    }
}
