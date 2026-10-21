using System;
using System.IO;
using BepInEx;
using BepInEx.Configuration;
using BepInEx.Logging;
using UnityEngine;

namespace ShipbreakerVR.Dev
{
    // Development helper, not part of the release (docs/TESTING.md):
    // - reloads the plugins in BepInEx\dev without restarting the game (F6, or automatically when a DLL there changes),
    // - runs the in-game self-test (F7, or --vr-selftest on the command line).
    [BepInPlugin(Guid, Name, Version)]
    public class DevPlugin : BaseUnityPlugin
    {
        public const string Guid = "de.marczahn.shipbreakervr.dev";
        public const string Name = "ShipbreakerVR Dev";
        public const string Version = "0.1.0";

        internal static ManualLogSource Log;
        internal static HotReload Reload;

        private ConfigEntry<KeyCode> reloadKey;
        private ConfigEntry<bool> autoReload;
        private ConfigEntry<KeyCode> selfTestKey;
        private ConfigEntry<float> waitForGameplay;
        private readonly SelfTestRunner selfTest = new SelfTestRunner();

        private void Awake()
        {
            Log = Logger;
            var folder = Config.Bind("HotReload", "Folder", "dev",
                "Folder with the plugins to reload, relative to the BepInEx folder. A plugin in it must not also be in BepInEx\\plugins.");
            reloadKey = Config.Bind("HotReload", "Key", KeyCode.F6, "Reloads all plugins in the dev folder.");
            autoReload = Config.Bind("HotReload", "Auto", true, "Reloads a plugin as soon as its DLL in the dev folder changes.");
            selfTestKey = Config.Bind("SelfTest", "Key", KeyCode.F7,
                "Runs the self-test right away. Tests that need gameplay or an open options list are skipped elsewhere.");
            waitForGameplay = Config.Bind("SelfTest", "WaitForGameplay", 600f,
                "Seconds a self-test started from the command line (--vr-selftest) waits for gameplay (a loaded shift) before it runs anyway.");
            // From the start, so the self-test also sees errors logged while the game loaded.
            LogWatch.Install();
            Reload = new HotReload(Path.Combine(Paths.BepInExRootPath, folder.Value), () => !selfTest.Running);
        }

        private void Start()
        {
            // Start runs after the chainloader has loaded all regular plugins; the dev folder's plugins load now.
            StartCoroutine(Reload.Watch(() => autoReload.Value));
            var request = SelfTestRequest.FromCommandLine(Environment.GetCommandLineArgs(), waitForGameplay.Value);
            if (request != null) StartCoroutine(selfTest.Run(request));
        }

        private void Update()
        {
            if (Input.GetKeyDown(reloadKey.Value)) Reload.RequestAll();
            if (Input.GetKeyDown(selfTestKey.Value) && !selfTest.Running) StartCoroutine(selfTest.Run(new SelfTestRequest()));
        }
    }
}
