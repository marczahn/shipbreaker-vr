using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using BBI.Unity.Game;
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
    //   move head|left|right X Y Z [P Y R] in T [EASING] [async]
    //                                      glides there over T seconds and waits for it; with async the next lines run
    //                                      at once, so head and hands move together. EASING: linear (default), ease
    //                                      (smooth start and stop), ease-in, ease-out
    //   track head|left|right [async]      a smooth path through keyframes, one per line up to 'end':
    //     T X Y Z [P Y R]                  T seconds after the start; the spline passes every key and starts and stops
    //   end                                gently. Without a key at 0 it starts where the device is.
    //   waitmoves                          waits until every glide and track has arrived
    //   left|right BUTTON VALUE            trigger, grip, a, b, x, y, menu, stickclick, or an input path (input/...)
    //   left|right stick X Y               the thumbstick
    //   press left|right BUTTON [T]        down, T seconds (default 0.2), up again
    //   aim P Y R                          the aim pose relative to the grip pose
    //   ipd METERS                         distance between the eyes (default 0.064)
    //   reset                              everything from the runtime again
    //   wait T | frames N                  waits
    //   waitlog TEXT [T]                   waits until a log line contains TEXT, at most T seconds (default 60); only
    //                                      lines written after this command count
    //   waitgame [T]                       waits until the player is in a shift: no load screen, not in the Hab, and the
    //                                      VR camera follows the game's main camera; at most T seconds (default 180)
    //   vr on|off                          starts or stops VR (F3)
    //   recenter                           centers the view (F4)
    //   config SECTION/KEY VALUE           changes a setting, e.g. config Steuerung/Klettern false
    //   screenshot NAME                    saves each eye's image (with alternate eyes), the game window and the UI texture
    //   dump [LABEL]                       writes what is drawn around the player (as F8) and the runtime's poses
    //   log TEXT                           writes TEXT into the log
    //   quit                               quits the game
    //   restart                            quits the game and starts it again (for changes to the OpenXR setup)
    // For trailer recordings:
    //   capturefps N                       the game advances exactly 1/N s per frame (Time.captureFramerate), however
    //                                      long the frame takes; the script's clock follows it. 0 switches it off
    //   record NAME [N]                    saves the game window as results\<script>\NAME\00001.png ... every frame (every
    //                                      Nth), for ffmpeg -framerate <fps> -i %05d.png; best with capturefps
    //   record stop                        stops saving
    //   hud on|off                         the HUD visor in front of the eyes (menus stay)
    //   lasers on|off                      the aiming lasers and tool lines
    //   hide TEXT                          switches off every renderer whose object path contains TEXT, e.g. geo_arms
    //                                      (the suit's arms); objects that appear later are not hidden; hide off shows
    //                                      them all again
    //   renderers [R] [TEXT]               logs the paths of the visible renderers within R meters of the view
    //                                      (default 5), nearest first, optionally only those containing TEXT; to find
    //                                      names for hide
    // capturefps, record, hud, lasers and hide end with the script.
    internal static class VrTestScript
    {
        static VrTestScript() => VrSession.OnUnload("test scripts", () => Finish("unloaded"));

        private static readonly string Root = Path.Combine(Paths.BepInExRootPath, "ShipbreakerVR-test");
        private static string Inbox => Path.Combine(Root, "inbox");

        private static string name;
        private static string results;
        private static List<string> lines;
        private static int next;
        private static float waitUntil;
        private static int waitFrames;
        private static string waitForLog;
        private static volatile bool logSeen;
        private static readonly List<VrTestTrack> Tracks = new List<VrTestTrack>();
        private static bool waitForTracks;
        private static Func<bool> waitCondition;
        private static string waitConditionName;
        // The script's clock: real time, or 1/capturefps per frame while that is set.
        private static float clock;
        private static int captureFps;
        private static string recordFolder;
        private static int recordEvery, recordFrame, recordSaved;
        private static readonly List<Renderer> Hidden = new List<Renderer>();

        // Read by VrUi and its pass.
        public static bool HideHud { get; private set; }
        public static bool HideLasers { get; private set; }
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
            clock += captureFps > 0 ? 1f / captureFps : Time.unscaledDeltaTime;
            var now = clock;
            Step(now);
            Record();
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
            if (Running && next >= lines.Count && Ready(now) && Tracks.Count == 0 && Releases.Count == 0) Finish(failures == 0 ? "finished" : $"finished with {failures} failed lines");
        }

        private static bool Ready(float now)
        {
            if (waitCondition != null)
            {
                var met = waitCondition();
                if (!met && now < waitUntil) return false;
                if (met) Plugin.Log.LogInfo($"Test {name}: {waitConditionName} after {now:0.0} s script time, game state {GameSession.CurrentGameState}");
                else Plugin.Log.LogWarning($"Test {name}: {waitConditionName} not reached in time, game state {GameSession.CurrentGameState}");
                waitCondition = null;
                waitUntil = now;
            }
            if (waitForTracks)
            {
                if (Tracks.Count > 0) return false;
                waitForTracks = false;
            }
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
                    waitUntil = now;
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
            waitForTracks = false;
            waitCondition = null;
            clock = 0f;
            listener = new Listener(Path.Combine(results, "log.txt"));
            BepInEx.Logging.Logger.Listeners.Add(listener);
            Plugin.Log.LogInfo($"Test {name} started ({lines.Count} lines), {Plugin.Name} {Plugin.Version}, VR {(VrXr.IsRunning ? "running" : "off")}, test input {(VrTestXr.Hooked ? "hooked" : "NOT hooked: restart the game with Test/Testmodus on")}");
        }

        private static void Finish(string how)
        {
            if (!Running) return;
            Plugin.Log.LogInfo($"Test {name} {how}");
            VrTestXr.Reset();
            Tracks.Clear();
            Releases.Clear();
            lines = null;
            if (captureFps > 0) Time.captureFramerate = 0;
            captureFps = 0;
            StopRecording();
            HideHud = false;
            HideLasers = false;
            ShowHidden();
            if (listener != null)
            {
                BepInEx.Logging.Logger.Listeners.Remove(listener);
                listener.Dispose();
                listener = null;
            }
            try { File.WriteAllText(Path.Combine(results, "finished.txt"), $"{how}\r\n"); }
            catch (Exception e) { Plugin.Log.LogWarning($"Test {name}: {e.Message}"); }
        }

        // Glides, tracks and timed releases, every frame.
        private static void Step(float now)
        {
            for (var i = Tracks.Count - 1; i >= 0; i--)
            {
                var track = Tracks[i];
                VrTestXr.SetPose(track.Device, track.Evaluate(now));
                if (track.Done(now)) Tracks.RemoveAt(i);
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
                    if (inAt < 0 || inAt + 1 >= words.Length) throw new FormatException("move needs 'in SECONDS'");
                    var to = ParsePose(words.Skip(2).Take(inAt - 2).ToArray(), VrTestXr.GetPose(device));
                    var duration = Number(words[inAt + 1]);
                    var easing = VrTestTrack.Easing.Linear;
                    var async = false;
                    foreach (var word in words.Skip(inAt + 2))
                    {
                        if (word.ToLowerInvariant() == "async") async = true;
                        else easing = VrTestTrack.ParseEasing(word);
                    }
                    Start(new VrTestTrack(device, now, new[] { 0f, duration }, new[] { Current(device), to }, easing), async, now);
                    return;
                }
                case "track":
                {
                    var device = ParseDevice(words[1]);
                    var async = words.Length > 2 && words[2].ToLowerInvariant() == "async";
                    var times = new List<float>();
                    var keys = new List<VrTestXr.Pose>();
                    var previous = VrTestXr.GetPose(device);
                    while (true)
                    {
                        if (next >= lines.Count) throw new FormatException("track without 'end'");
                        var key = lines[next++].Trim();
                        if (key.Length == 0 || key.StartsWith("#")) continue;
                        var parts = key.Split((char[])null, StringSplitOptions.RemoveEmptyEntries);
                        if (parts[0].ToLowerInvariant() == "end") break;
                        var pose = ParsePose(parts.Skip(1).ToArray(), previous);
                        times.Add(Number(parts[0]));
                        keys.Add(pose);
                        previous = pose;
                    }
                    if (times.Count == 0) throw new FormatException("track without keys");
                    if (times[0] > 0f)
                    {
                        times.Insert(0, 0f);
                        keys.Insert(0, Current(device));
                    }
                    if (times.Count == 1)
                    {
                        times.Add(0f);
                        keys.Add(keys[0]);
                    }
                    Start(new VrTestTrack(device, now, times.ToArray(), keys.ToArray()), async, now);
                    return;
                }
                case "waitgame":
                    waitCondition = InShift;
                    waitConditionName = "in the shift";
                    waitUntil = now + (words.Length > 1 ? Number(words[1]) : 180f);
                    return;
                case "renderers":
                {
                    var radius = words.Length > 1 && IsNumber(words[1]) ? Number(words[1]) : 5f;
                    var skip = words.Length > 1 && IsNumber(words[1]) ? 2 : 1;
                    ListRenderers(radius, words.Length > skip ? string.Join(" ", words.Skip(skip)) : null);
                    return;
                }
                case "waitmoves":
                    waitForTracks = true;
                    return;
                case "capturefps":
                    captureFps = Math.Max(0, (int)Number(words[1]));
                    Time.captureFramerate = captureFps;
                    return;
                case "record":
                    if (words[1].ToLowerInvariant() == "stop") StopRecording();
                    else StartRecording(words[1], words.Length > 2 ? Math.Max(1, (int)Number(words[2])) : 1);
                    return;
                case "hud":
                    HideHud = !OnOff(words[1]);
                    return;
                case "lasers":
                    HideLasers = !OnOff(words[1]);
                    return;
                case "hide":
                {
                    var text = line.Substring(words[0].Length).Trim();
                    if (text.ToLowerInvariant() == "off") ShowHidden();
                    else Hide(text);
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
                    Tracks.Clear();
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

        // A new path for the device replaces the one it is on; without async the script waits until it has arrived.
        private static void Start(VrTestTrack track, bool async, float now)
        {
            Tracks.RemoveAll(t => t.Device == track.Device);
            Tracks.Add(track);
            if (!async) waitUntil = now + track.Duration;
        }

        private static bool OnOff(string word)
        {
            switch (word.ToLowerInvariant())
            {
                case "on": return true;
                case "off": return false;
                default: throw new FormatException($"'{word}' is not on or off");
            }
        }

        private static void StartRecording(string folder, int every)
        {
            StopRecording();
            recordFolder = Path.Combine(results, folder);
            if (Directory.Exists(recordFolder)) Directory.Delete(recordFolder, true);
            Directory.CreateDirectory(recordFolder);
            recordEvery = every;
            recordFrame = 0;
            recordSaved = 0;
            Plugin.Log.LogInfo($"Test {name}: recording the game window to {recordFolder}{(captureFps > 0 ? $" at {captureFps} fps game time" : " (no capturefps: frames follow real time)")}");
        }

        private static void StopRecording()
        {
            if (recordFolder == null) return;
            Plugin.Log.LogInfo($"Test {name}: recording stopped, {recordSaved} frames in {recordFolder}");
            recordFolder = null;
        }

        // ScreenCapture writes the window at the end of this frame.
        private static void Record()
        {
            if (recordFolder == null) return;
            if (recordFrame++ % recordEvery != 0) return;
            recordSaved++;
            ScreenCapture.CaptureScreenshot(Path.Combine(recordFolder, recordSaved.ToString("00000", CultureInfo.InvariantCulture) + ".png"));
        }

        private static void Hide(string text)
        {
            var count = 0;
            foreach (var renderer in UnityEngine.Object.FindObjectsOfType<Renderer>())
            {
                if (!renderer.enabled || Hidden.Contains(renderer)) continue;
                if (VrUtil.Path(renderer.transform).IndexOf(text, StringComparison.OrdinalIgnoreCase) < 0) continue;
                renderer.enabled = false;
                Hidden.Add(renderer);
                count++;
                if (count <= 20) Plugin.Log.LogInfo($"Test {name}: hidden {VrUtil.Path(renderer.transform)}");
            }
            Plugin.Log.LogInfo($"Test {name}: {count} renderers with '{text}' hidden");
        }

        // In a shift: the game's main camera exists and the VR camera follows it, outside the Hab and any load screen.
        private static bool InShift()
        {
            var main = LynxCameraController.MainCamera;
            var vr = VrCamera.Instance;
            var loading = SceneLoader.Instance != null && SceneLoader.Instance.LoadScreenVisible;
            return main != null && vr != null && vr.GameCamera == main && !vr.FollowsHab && VrHab.Camera == null && !loading;
        }

        private static void ListRenderers(float radius, string text)
        {
            var vr = VrCamera.Instance;
            var eye = vr != null && vr.Camera != null ? vr.Camera.transform.position : Camera.main != null ? Camera.main.transform.position : Vector3.zero;
            var found = new List<(float Distance, string Line)>();
            foreach (var renderer in UnityEngine.Object.FindObjectsOfType<Renderer>())
            {
                if (!renderer.enabled || !renderer.gameObject.activeInHierarchy) continue;
                var distance = Mathf.Sqrt(renderer.bounds.SqrDistance(eye));
                if (distance > radius) continue;
                var path = VrUtil.Path(renderer.transform);
                if (text != null && path.IndexOf(text, StringComparison.OrdinalIgnoreCase) < 0) continue;
                found.Add((distance, $"{distance:0.00} m {renderer.GetType().Name} layer {renderer.gameObject.layer} {path}"));
            }
            Plugin.Log.LogInfo($"Test {name}: {found.Count} renderers within {radius} m{(text != null ? $" with '{text}'" : "")}");
            foreach (var entry in found.OrderBy(f => f.Distance).Take(80)) Plugin.Log.LogInfo($"  {entry.Line}");
        }

        private static void ShowHidden()
        {
            foreach (var renderer in Hidden)
                if (renderer != null) renderer.enabled = true;
            Hidden.Clear();
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
