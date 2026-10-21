using System;
using System.Collections.Generic;
using BepInEx.Logging;
using UnityEngine;

namespace ShipbreakerVR.Dev
{
    // Collects errors of the plugin and video memory errors since the game started, for the self-test.
    // Errors of the game itself are ignored; it logs plenty of them on its own.
    internal class LogWatch : ILogListener
    {
        private const string ModSource = "ShipbreakerVR";
        private static readonly List<string> Errors = new List<string>();
        private static bool installed;

        public static int Count
        {
            get { lock (Errors) return Errors.Count; }
        }

        public static List<string> Since(int index)
        {
            lock (Errors) return Errors.GetRange(Math.Min(index, Errors.Count), Math.Max(0, Errors.Count - index));
        }

        public static void Install()
        {
            if (installed) return;
            installed = true;
            BepInEx.Logging.Logger.Listeners.Add(new LogWatch());
            Application.logMessageReceivedThreaded += OnUnityLog;
        }

        public void LogEvent(object sender, LogEventArgs e)
        {
            // Unity's own messages also arrive here when BepInEx forwards them; those are handled in OnUnityLog.
            if ((e.Level & (LogLevel.Error | LogLevel.Fatal)) == 0 || e.Source?.SourceName != ModSource) return;
            Add($"{e.Level}: {e.Data}");
        }

        private static void OnUnityLog(string message, string stackTrace, LogType type)
        {
            if (type != LogType.Error && type != LogType.Exception && type != LogType.Assert) return;
            // D3D11 reports exhausted video memory as E_OUTOFMEMORY (0x8007000e).
            if (Contains(message, "ShipbreakerVR") || Contains(stackTrace, "ShipbreakerVR") ||
                Contains(message, "0x8007000e") || Contains(message, "out of memory"))
                Add($"{type}: {FirstLine(message)}");
        }

        private static void Add(string text)
        {
            lock (Errors) Errors.Add(text);
        }

        private static bool Contains(string text, string part) =>
            text != null && text.IndexOf(part, StringComparison.OrdinalIgnoreCase) >= 0;

        private static string FirstLine(string text)
        {
            var end = text.IndexOf('\n');
            return end < 0 ? text : text.Substring(0, end);
        }

        public void Dispose() { }
    }
}
