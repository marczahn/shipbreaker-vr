using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using BBI.Unity.Game;
using BepInEx;
using UnityEngine;

namespace ShipbreakerVR.Dev
{
    internal class TestFailure : Exception
    {
        public TestFailure(string message) : base(message) { }
    }

    internal class TestSkipped : Exception
    {
        public TestSkipped(string reason) : base(reason) { }
    }

    internal class SelfTest
    {
        public string Name;
        public bool NeedsGameplay;
        public bool NeedsVr;
        public float Timeout = 30f;
        public Func<TestContext, IEnumerator> Body;
    }

    // What a test can do: check, skip, note details, register cleanups, and wait (in real time; the pause menu stops the game clock).
    internal class TestContext
    {
        public readonly Mod Mod;
        public readonly List<string> Notes = new List<string>();
        private readonly List<Action> cleanups = new List<Action>();

        public TestContext(Mod mod) => Mod = mod;

        public static bool InGameplay =>
            GameSession.CurrentGameState == GameSession.GameState.Gameplay && LynxCameraController.MainCamera != null;

        public void Check(bool condition, string message)
        {
            if (!condition) throw new TestFailure(message);
        }

        public void Skip(string reason) => throw new TestSkipped(reason);

        public void Note(string text) => Notes.Add(text);

        // Runs after the test, also when it fails (e.g. to put a changed setting back).
        public void OnCleanup(Action action) => cleanups.Add(action);

        public void RunCleanups()
        {
            for (var i = cleanups.Count - 1; i >= 0; i--)
            {
                try { cleanups[i](); }
                catch (Exception e) { DevPlugin.Log.LogError($"Self-test cleanup failed: {e}"); }
            }
            cleanups.Clear();
        }

        public static IEnumerator Frames(int count)
        {
            for (var i = 0; i < count; i++) yield return null;
        }

        public static IEnumerator Seconds(float seconds)
        {
            var until = Time.unscaledTime + seconds;
            while (Time.unscaledTime < until) yield return null;
        }

        public IEnumerator Until(Func<bool> condition, float timeout, string what)
        {
            var until = Time.unscaledTime + timeout;
            while (!condition())
            {
                if (Time.unscaledTime >= until) throw new TestFailure($"timed out after {timeout:0.#} s waiting for {what}");
                yield return null;
            }
        }
    }

    internal class SelfTestRequest
    {
        public string[] Filter = new string[0];
        public bool Quit;
        // 0: run right away.
        public float WaitForGameplay;

        // --vr-selftest[=aim,options]  --vr-selftest-quit  --vr-selftest-nowait
        public static SelfTestRequest FromCommandLine(string[] args, float waitForGameplay)
        {
            SelfTestRequest request = null;
            foreach (var arg in args)
            {
                if (arg != "--vr-selftest" && !arg.StartsWith("--vr-selftest=")) continue;
                request = new SelfTestRequest { WaitForGameplay = waitForGameplay };
                var eq = arg.IndexOf('=');
                if (eq > 0) request.Filter = arg.Substring(eq + 1).Split(new[] { ',' }, StringSplitOptions.RemoveEmptyEntries).Select(f => f.Trim()).ToArray();
            }
            if (request == null) return null;
            request.Quit = args.Contains("--vr-selftest-quit");
            if (args.Contains("--vr-selftest-nowait")) request.WaitForGameplay = 0f;
            return request;
        }

        public bool Matches(string name) => Filter.Length == 0 || Filter.Any(f => name == f || name.StartsWith(f + "."));
    }

    // Runs the tests one after another as a coroutine and writes BepInEx\selftest\results.txt:
    // a START line, one "PASS|FAIL|SKIP <tab> name <tab> detail" line per test, and a DONE line with the totals.
    internal class SelfTestRunner
    {
        public bool Running { get; private set; }

        public IEnumerator Run(SelfTestRequest request)
        {
            Running = true;
            var results = new Results();
            var mod = Mod.Find();
            if (mod == null)
            {
                results.Add("mod.loaded", "FAIL", "ShipbreakerVR is not loaded, neither from BepInEx\\plugins nor from the dev folder");
            }
            else
            {
                if (request.WaitForGameplay > 0f && !TestContext.InGameplay)
                {
                    DevPlugin.Log.LogInfo($"Self-test: waiting up to {request.WaitForGameplay:0} s for gameplay, load a shift");
                    var until = Time.unscaledTime + request.WaitForGameplay;
                    while (!TestContext.InGameplay && Time.unscaledTime < until) yield return null;
                    // Let the shift finish loading.
                    if (TestContext.InGameplay) yield return TestContext.Seconds(5f);
                }
                bool? vrBefore = null;
                try { vrBefore = mod.VrRunning; }
                catch (Exception e) { DevPlugin.Log.LogWarning($"Self-test: VR state unknown: {e.Message}"); }

                foreach (var test in SelfTests.All.Where(t => request.Matches(t.Name)))
                    yield return RunOne(test, mod, results);

                // Leave VR as it was.
                try
                {
                    if (vrBefore.HasValue && mod.VrRunning != vrBefore.Value) mod.ToggleVr();
                }
                catch (Exception e) { DevPlugin.Log.LogWarning($"Self-test: could not restore the VR state: {e.Message}"); }
            }
            results.Finish();
            Running = false;
            if (!request.Quit) yield break;
            yield return TestContext.Seconds(1f);
            Application.Quit();
        }

        private static IEnumerator RunOne(SelfTest test, Mod mod, Results results)
        {
            if (test.NeedsGameplay && !TestContext.InGameplay)
            {
                results.Add(test.Name, "SKIP", "needs gameplay (load a shift)");
                yield break;
            }
            bool vr;
            try { vr = mod.VrRunning; }
            catch (Exception e)
            {
                results.Add(test.Name, "FAIL", e.Message);
                yield break;
            }
            if (test.NeedsVr && !vr)
            {
                results.Add(test.Name, "SKIP", "VR is not running (see xr.start)");
                yield break;
            }

            var context = new TestContext(mod);
            var errorsBefore = LogWatch.Count;
            var started = Time.unscaledTime;
            Exception error = null;
            yield return Drive(test.Body(context), e => error = e, test.Timeout);
            context.RunCleanups();
            var notes = string.Join("; ", context.Notes);
            var newErrors = LogWatch.Since(errorsBefore);
            var seconds = $"{Time.unscaledTime - started:0.0} s";

            switch (error)
            {
                case TestSkipped skip:
                    results.Add(test.Name, "SKIP", skip.Message);
                    break;
                case TestFailure failure:
                    results.Add(test.Name, "FAIL", Join(failure.Message, notes, seconds));
                    break;
                case null when newErrors.Count > 0:
                    results.Add(test.Name, "FAIL", Join($"{newErrors.Count} error(s) logged meanwhile, first: {newErrors[0]}", notes, seconds));
                    break;
                case null:
                    results.Add(test.Name, "PASS", Join(notes, seconds));
                    break;
                default:
                    results.Add(test.Name, "FAIL", Join($"{error.GetType().Name}: {error.Message}", notes, seconds));
                    DevPlugin.Log.LogError($"Self-test {test.Name}: {error}");
                    break;
            }
        }

        private static string Join(params string[] parts) => string.Join(" | ", parts.Where(p => !string.IsNullOrEmpty(p)));

        // Steps a test, including nested IEnumerators, so its exceptions can be caught (a try block cannot contain yield).
        private static IEnumerator Drive(IEnumerator body, Action<Exception> onError, float timeout)
        {
            var stack = new Stack<IEnumerator>();
            stack.Push(body);
            var until = Time.unscaledTime + timeout;
            while (stack.Count > 0)
            {
                if (Time.unscaledTime >= until)
                {
                    onError(new TestFailure($"test timed out after {timeout:0} s"));
                    yield break;
                }
                object current;
                try
                {
                    if (!stack.Peek().MoveNext())
                    {
                        stack.Pop();
                        continue;
                    }
                    current = stack.Peek().Current;
                }
                catch (Exception e)
                {
                    onError(e);
                    yield break;
                }
                if (current is IEnumerator nested) stack.Push(nested);
                else yield return current;
            }
        }

        private class Results
        {
            private readonly string path = Path.Combine(Path.Combine(Paths.BepInExRootPath, "selftest"), "results.txt");
            private int pass, fail, skip;

            public Results()
            {
                Directory.CreateDirectory(Path.GetDirectoryName(path));
                File.WriteAllText(path, $"START\t{DateTime.Now:yyyy-MM-dd HH:mm:ss}\n");
                DevPlugin.Log.LogInfo($"Self-test started, results in {path}");
            }

            public void Add(string name, string outcome, string detail)
            {
                if (outcome == "PASS") pass++;
                else if (outcome == "FAIL") fail++;
                else skip++;
                detail = (detail ?? "").Replace('\t', ' ').Replace('\r', ' ').Replace('\n', ' ');
                File.AppendAllText(path, $"{outcome}\t{name}\t{detail}\n");
                var line = $"Self-test {outcome} {name}: {detail}";
                if (outcome == "FAIL") DevPlugin.Log.LogWarning(line);
                else DevPlugin.Log.LogInfo(line);
            }

            public void Finish()
            {
                File.AppendAllText(path, $"DONE\tpass={pass}\tfail={fail}\tskip={skip}\n");
                DevPlugin.Log.LogInfo($"Self-test done: {pass} passed, {fail} failed, {skip} skipped");
            }
        }
    }
}
