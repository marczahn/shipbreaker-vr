using System;
using System.Reflection;
using BBI.Unity.Game;
using BepInEx.Bootstrap;
using BepInEx.Configuration;
using HarmonyLib;
using UnityEngine;

namespace ShipbreakerVR.Dev
{
    // The self-test's view into ShipbreakerVR, through reflection: after a hot reload the plugin lives in a renamed
    // assembly, so it cannot be referenced at compile time. A renamed or removed member fails the test that uses it.
    internal class Mod
    {
        public const string Guid = "de.marczahn.shipbreakervr";

        public readonly Assembly Assembly;

        private Mod(Assembly assembly) => Assembly = assembly;

        // The copy from the dev folder if there is one, otherwise the one the chainloader loaded from the plugins folder.
        public static Mod Find()
        {
            var assembly = DevPlugin.Reload?.FindAssembly(Guid);
            if (assembly == null && Chainloader.PluginInfos.TryGetValue(Guid, out var info) && info.Instance != null)
                assembly = info.Instance.GetType().Assembly;
            return assembly != null ? new Mod(assembly) : null;
        }

        public bool VrRunning => (bool)Get(Type("VrXr"), null, "IsRunning");

        public void ToggleVr() => Call(Type("VrXr"), "Toggle");

        // The VrCamera component on the VR head, or null before it is attached.
        public Component VrCamera => (Component)Get(Type("VrCamera"), null, "Instance");

        public Camera EyeCamera
        {
            get
            {
                var vr = VrCamera;
                return vr != null ? (Camera)Get(vr.GetType(), vr, "Camera") : null;
            }
        }

        public ConfigEntry<T> Setting<T>(string name) => (ConfigEntry<T>)Get(Type("Plugin"), null, name);

        public bool IsOurs(OptionEntry entry) => (bool)Call(Type("VrOptions"), "IsOurs", entry);

        public Type Type(string name) =>
            Assembly.GetType("ShipbreakerVR." + name) ?? throw new TestFailure($"type ShipbreakerVR.{name} not found in the plugin");

        private static object Get(Type type, object instance, string name)
        {
            var property = AccessTools.Property(type, name);
            if (property != null) return property.GetValue(instance, null);
            var field = AccessTools.Field(type, name);
            if (field != null) return field.GetValue(instance);
            throw new TestFailure($"{type.Name}.{name} not found in the plugin");
        }

        private static object Call(Type type, string name, params object[] args)
        {
            var method = AccessTools.Method(type, name) ?? throw new TestFailure($"{type.Name}.{name}() not found in the plugin");
            try
            {
                return method.Invoke(null, args);
            }
            catch (TargetInvocationException e)
            {
                throw new TestFailure($"{type.Name}.{name}() threw {e.InnerException}");
            }
        }
    }
}
