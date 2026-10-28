using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using BepInEx;
using BepInEx.Logging;
using UnityEngine;

namespace ShipbreakerVR
{
    // [Test] Testmodus: runs test scripts that move the head and the controllers and press their buttons (VrTestXr),
    // take screenshots and write what the mod sees, so a change can be checked without wearing the headset, also by a
    // developer who is not at the computer: a script dropped into BepInEx\ShipbreakerVR-test\inbox runs on its own,
    // and its results land in BepInEx\ShipbreakerVR-test\results\<script>\ (log.txt with every log line of the run,
    // screenshots, finished.txt at the end). The script then moves to ...\done.
    //
    // One command per line, # starts a comment. Positions are meters in tracking space (Unity's axes: x right, y up,
    // z forward), angles degrees (pitch yaw roll, Unity's Euler order), times seconds.
    //   head X Y Z [P Y R]                 the headset's pose
    //   left|right X Y Z [P Y R]           a controller's grip pose (the aim pose follows, see aim)
    //   left|right off                     the controller's pose from the runtime again
    //   move head|left|right X Y Z [P Y R] in T   glides there over T seconds (waits for it)
    //   left|right BUTTON VALUE            trigger, grip, a, b, x, y, menu, stickclick, or an input path (input/...)
    //   left|right stick X Y               the thumbstick
    //   press left|right BUTTON [T]        down, T seconds (default 0.2), up again
    //   aim P Y R                          the aim pose relative to the grip pose
    //   ipd METERS                         distance between the eyes (default 0.064)
    //   reset                              everything from the runtime again
    //   wait T | frames N                  waits
    //   waitlog TEXT [T]                   waits until a log line contains TEXT, at most T seconds (default 60)
    //   vr on|off                          starts or stops VR (F3)
    //   recenter                           centers the view (F4)
    //   config SECTION/KEY VALUE           changes a setting, e.g. config Steuerung/Klettern false
    //   screenshot NAME                    saves each eye's image (with alternate eyes), the game window and the UI texture
    //   dump [LABEL]                       writes what is drawn around the player (as F8) and the runtime's poses
    //   log TEXT                           writes TEXT into the log
    //   quit                               quits the game
    //   restart                            quits the game and starts it again (for changes to the OpenXR setup)
    internal static class VrTestScript
    {
        static VrTestScript() => VrSession.OnUnload("test scripts", () => Finish("unloaded"));

        private static readonly string Root = Path.Combine(Paths.BepInExRootPath, "ShipbreakerVR-test");
        private static string Inbox => Path.Combine(Root, "inbox");

        private class Glide
        {
            public VrTestXr.Device Device;
            public VrTestXr.Pose From, To;
            public float Start, Duration;
        }

        private static string name;
        private static string results;
        private static List<string> lines;
        private static int next;
        private static float waitUntil;
        private static int waitFrames;
        private static string waitForLog;
        private static volatile bool logSeen;
        private static readonly List<Glide> Glides = new List<Glide>();
        private static readonly List<(float At, VrTestXr.Device Hand, string Input)> Releases = new List<(float, VrTestXr.Device, string)>();
        private static Listener listener;
        private static int failures;
        private static float nextInboxCheck;

        public static bool Running => lines != null;

        // From VrSession.Update.
        public static void Update()
        {
            if (!VrSettings.TestMode.Value) return;
            VrTestXr.Register();
            if (!Running)
            {
                if (Time.unscaledTime < nextInboxCheck) return;
                nextInboxCheck = Time.unscaledTime + 1f;
                TryStartNext();
                return;
            }
            var now = Time.unscaledTime;
            Step(now);
            while (Running && next < lines.Count && Ready(now))
            {
                var line = lines[next++];
                try { Execute(line.Trim(), now); }
                catch (Exception e)
                {
                    failures++;
                    Plugin.Log.LogError($"Test {name} line {next}: '{line}' failed: {e.Message}");
                }
            }
            if (Running && next >= lines.Count && Ready(now) && Glides.Count == 0 && Releases.Count == 0) Finish(failures == 0 ? "finished" : $"finished with {failures} failed lines");
        }

        private static bool Ready(float now)
        {
            if (waitFrames > 0)
            {
                waitFrames--;
                return false;
            }
            if (waitForLog != null)
            {
                if (logSeen || now >= waitUntil)
                {
                    if (!logSeen) Plugin.Log.LogWarning($"Test {name}: no log line with '{waitForLog}' in time");
                    waitForLog = null;
                }
                else return false;
            }
            return now >= waitUntil;
        }

        private static void TryStartNext()
        {
            if (!Directory.Exists(Inbox))
            {
                Directory.CreateDirectory(Inbox);
                return;
            }
            var file = Directory.GetFiles(Inbox, "*.txt").OrderBy(f => f, StringComparer.OrdinalIgnoreCase).FirstOrDefault();
            if (file == null) return;
            name = Path.GetFileNameWithoutExtension(file);
            lines = File.ReadAllLines(file).ToList();
            var done = Path.Combine(Root, "done");
            Directory.CreateDirectory(done);
            var target = Path.Combine(done, Path.GetFileName(file));
            if (File.Exists(target)) File.Delete(target);
            File.Move(file, target);
            results = Path.Combine(Path.Combine(Root, "results"), name);
            if (Directory.Exists(results)) Directory.Delete(results, true);
            Directory.CreateDirectory(results);
            next = 0;
            failures = 0;
            waitUntil = 0f;
            waitFrames = 0;
            waitForLog = null;
            listener = new Listener(Path.Combine(results, "log.txt"));
            BepInEx.Logging.Logger.Listeners.Add(listener);
            Plugin.Log.LogInfo($"Test {name} started ({lines.Count} lines), {Plugin.Name} {Plugin.Version}, VR {(VrXr.IsRunning ? "running" : "off")}, test input {(VrTestXr.Hooked ? "hooked" : "NOT hooked: restart the game with Test/Testmodus on")}");
        }

        private static void Finish(string how)
        {
            if (!Running) return;
            Plugin.Log.LogInfo($"Test {name} {how}");
            VrTestXr.Reset();
            Glides.Clear();
            Releases.Clear();
            lines = null;
            if (listener != null)
            {
                BepInEx.Logging.Logger.Listeners.Remove(listener);
                listener.Dispose();
                listener = null;
            }
            try { File.WriteAllText(Path.Combine(results, "finished.txt"), $"{how}\r\n"); }
            catch (Exception e) { Plugin.Log.LogWarning($"Test {name}: {e.Message}"); }
        }

        // Glides and timed releases, every frame.
        private static void Step(float now)
        {
            for (var i = Glides.Count - 1; i >= 0; i--)
            {
                var g = Glides[i];
                var t = g.Duration <= 0f ? 1f : Mathf.Clamp01((now - g.Start) / g.Duration);
                VrTestXr.SetPose(g.Device, new VrTestXr.Pose(Vector3.Lerp(g.From.Position, g.To.Position, t), Quaternion.Slerp(g.From.Rotation, g.To.Rotation, t)));
                if (t >= 1f) Glides.RemoveAt(i);
            }
            for (var i = Releases.Count - 1; i >= 0; i--)
            {
                if (now < Releases[i].At) continue;
                VrTestXr.SetInput(Releases[i].Hand, Releases[i].Input, 0f);
                Releases.RemoveAt(i);
            }
        }

        private static void Execute(string line, float now)
        {
            if (line.Length == 0 || line.StartsWith("#")) return;
            var words = line.Split((char[])null, StringSplitOptions.RemoveEmptyEntries);
            var command = words[0].ToLowerInvariant();
            Plugin.Log.LogInfo($"Test {name} line {next}: {line}");
            switch (command)
            {
                case "head":
                case "left":
                case "right":
                    Device(command, words.Skip(1).ToArray());
                    return;
                case "move":
                {
                    var device = ParseDevice(words[1]);
                    var inAt = Array.FindIndex(words, w => w.ToLowerInvariant() == "in");
                    if (inAt < 0) throw new FormatException("move needs 'in SECONDS'");
                    var to = ParsePose(words.Skip(2).Take(inAt - 2).ToArray(), VrTestXr.GetPose(device));
                    var duration = Number(words[inAt + 1]);
                    Glides.Add(new Glide { Device = device, From = Current(device), To = to, Start = now, Duration = duration });
                    waitUntil = now + duration;
                    return;
                }
                case "press":
                {
                    var hand = ParseDevice(words[1]);
                    var input = Input(words[2]);
                    var duration = words.Length > 3 ? Number(words[3]) : 0.2f;
                    VrTestXr.SetInput(hand, input, 1f);
                    Releases.Add((now + duration, hand, input));
                    waitUntil = now + duration;
                    waitFrames = 1;
                    return;
                }
                case "aim":
                    VrTestXr.SetAimOffset(Quaternion.Euler(Number(words[1]), Number(words[2]), Number(words[3])));
                    return;
                case "ipd":
                    VrTestXr.Ipd = Number(words[1]);
                    return;
                case "reset":
                    VrTestXr.Reset();
                    Glides.Clear();
                    return;
                case "wait":
                    waitUntil = now + Number(words[1]);
                    return;
                case "frames":
                    waitFrames = (int)Number(words[1]);
                    return;
                case "waitlog":
                {
                    var timeout = 60f;
                    var text = line.Substring(words[0].Length).Trim();
                    var last = words[words.Length - 1];
                    if (words.Length > 2 && float.TryParse(last, NumberStyles.Float, CultureInfo.InvariantCulture, out var t))
                    {
                        timeout = t;
                        text = text.Substring(0, text.Length - last.Length).Trim();
                    }
                    logSeen = false;
                    waitForLog = text;
                    listener.WaitFor = text;
                    waitUntil = now + timeout;
                    return;
                }
                case "vr":
                    var on = words[1].ToLowerInvariant() == "on";
                    if (on != VrXr.IsRunning) VrXr.Toggle();
                    return;
                case "recenter":
                    VrCamera.RecenterView();
                    return;
                case "config":
                    SetConfig(words[1], line.Substring(line.IndexOf(words[1], StringComparison.Ordinal) + words[1].Length).Trim());
                    return;
                case "screenshot":
                    VrDiagnostics.SaveImages(VrUi.Instance, results, words.Length > 1 ? words[1] : $"line{next}");
                    return;
                case "dump":
                    var label = words.Length > 1 ? line.Substring(words[0].Length).Trim() : $"test {name} line {next}";
                    if (VrCamera.Instance != null) VrDiagnostics.Dump(VrCamera.Instance, label, false);
                    else Plugin.Log.LogInfo($"Dump ({label}): no VR camera");
                    Plugin.Log.LogInfo($"Poses from the runtime: {VrTestXr.DescribeReported()}");
                    return;
                case "log":
                    Plugin.Log.LogInfo($"Test {name}: {line.Substring(words[0].Length).Trim()}");
                    return;
                case "quit":
                case "restart":
                    Finish(command == "quit" ? "quits the game" : "restarts the game");
                    if (command == "restart") Relaunch();
                    Application.Quit();
                    return;
                default:
                    throw new FormatException($"unknown command '{words[0]}'");
            }
        }

        // head|left|right X Y Z [P Y R], left|right off, left|right BUTTON VALUE, left|right stick X Y
        private static void Device(string which, string[] args)
        {
            var device = ParseDevice(which);
            if (args.Length == 0) throw new FormatException("missing values");
            var first = args[0].ToLowerInvariant();
            if (first == "off")
            {
                VrTestXr.SetPose(device, null);
                return;
            }
            if (device != VrTestXr.Device.Head && !IsNumber(first))
            {
                if (first == "stick")
                {
                    VrTestXr.SetInput(device, "input/thumbstick", Number(args[1]), Number(args[2]));
                    return;
                }
                VrTestXr.SetInput(device, Input(first), Number(args[1]));
                if (first == "trigger" || Buttons.ContainsKey(first) && Buttons[first].EndsWith("/click"))
                {
                    // The touch sensor goes with the press.
                    var touch = first == "trigger" ? "input/trigger/touch" : Buttons[first].Replace("/click", "/touch");
                    VrTestXr.SetInput(device, touch, Number(args[1]) > 0f ? 1f : 0f);
                }
                return;
            }
            VrTestXr.SetPose(device, ParsePose(args, VrTestXr.GetPose(device)));
        }

        private static readonly Dictionary<string, string> Buttons = new Dictionary<string, string>
        {
            { "trigger", "input/trigger/value" },
            { "grip", "input/squeeze/value" },
            { "a", "input/a/click" },
            { "b", "input/b/click" },
            { "x", "input/x/click" },
            { "y", "input/y/click" },
            { "menu", "input/menu/click" },
            { "stickclick", "input/thumbstick/click" },
        };

        private static string Input(string word)
        {
            var key = word.ToLowerInvariant();
            if (Buttons.TryGetValue(key, out var path)) return path;
            if (key.StartsWith("input/")) return key;
            throw new FormatException($"unknown button '{word}'");
        }

        private static VrTestXr.Device ParseDevice(string word)
        {
            switch (word.ToLowerInvariant())
            {
                case "head": return VrTestXr.Device.Head;
                case "left": return VrTestXr.Device.Left;
                case "right": return VrTestXr.Device.Right;
                default: throw new FormatException($"'{word}' is not head, left or right");
            }
        }

        // X Y Z [P Y R]; without angles the rotation stays as it was.
        private static VrTestXr.Pose ParsePose(string[] args, VrTestXr.Pose? previous)
        {
            if (args.Length != 3 && args.Length != 6) throw new FormatException("a pose is X Y Z or X Y Z P Y R");
            var position = new Vector3(Number(args[0]), Number(args[1]), Number(args[2]));
            var rotation = args.Length == 6 ? Quaternion.Euler(Number(args[3]), Number(args[4]), Number(args[5]))
                : previous.HasValue ? previous.Value.Rotation : Quaternion.identity;
            return new VrTestXr.Pose(position, rotation);
        }

        // Where a glide starts: the script's pose, or else where the device is in the rig.
        private static VrTestXr.Pose Current(VrTestXr.Device device)
        {
            var pose = VrTestXr.GetPose(device);
            if (pose.HasValue) return pose.Value;
            return new VrTestXr.Pose(device == VrTestXr.Device.Head ? new Vector3(0f, 1.7f, 0f) : new Vector3(device == VrTestXr.Device.Left ? -0.2f : 0.2f, 1.3f, 0.3f), Quaternion.identity);
        }

        // A new game process, through Steam (started directly, the game quits again without Steam), once this one has had
        // time to quit (the game runs only once at a time).
        private static void Relaunch()
        {
            const string url = "steam://rungameid/1161580";
            var start = new System.Diagnostics.ProcessStartInfo("cmd.exe", $"/c timeout /t 20 /nobreak >nul & start \"\" \"{url}\"")
            {
                CreateNoWindow = true,
                UseShellExecute = false,
            };
            System.Diagnostics.Process.Start(start);
            Plugin.Log.LogInfo($"Test: the game starts again in 20 s ({url})");
        }

        private static void SetConfig(string key, string value)
        {
            var slash = key.IndexOf('/');
            if (slash < 0) throw new FormatException("config needs SECTION/KEY");
            var definition = new BepInEx.Configuration.ConfigDefinition(key.Substring(0, slash), key.Substring(slash + 1));
            if (!VrSettings.File.ContainsKey(definition)) throw new FormatException($"no setting {key}");
            VrSettings.File[definition].SetSerializedValue(value);
        }

        private static bool IsNumber(string word) => float.TryParse(word, NumberStyles.Float, CultureInfo.InvariantCulture, out _);

        private static float Number(string word)
        {
            if (!float.TryParse(word, NumberStyles.Float, CultureInfo.InvariantCulture, out var value)) throw new FormatException($"'{word}' is not a number");
            return value;
        }

        public static string Format(Vector3 v) => string.Format(CultureInfo.InvariantCulture, "({0:0.000} {1:0.000} {2:0.000})", v.x, v.y, v.z);

        // Every log line of a run, also from the game, into the run's log.txt; and the line waitlog waits for.
        private class Listener : ILogListener
        {
            private readonly StreamWriter writer;
            public volatile string WaitFor;

            public Listener(string path)
            {
                writer = new StreamWriter(path, false, Encoding.UTF8) { AutoFlush = true };
            }

            public void LogEvent(object sender, LogEventArgs e)
            {
                var text = e.Data?.ToString() ?? "";
                var wait = WaitFor;
                if (wait != null && text.Contains(wait) && !text.StartsWith($"Test {name} line"))
                {
                    logSeen = true;
                    WaitFor = null;
                }
                try
                {
                    lock (writer) writer.WriteLine($"{DateTime.Now:HH:mm:ss.fff} [{e.Level,-7}:{e.Source.SourceName}] {text}");
                }
                catch (ObjectDisposedException) { }
            }

            public void Dispose()
            {
                lock (writer) writer.Dispose();
            }
        }
    }
}
