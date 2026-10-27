using System.Collections.Generic;
using System.Diagnostics;
using System.Reflection;
using System.Text;
using HarmonyLib;
using UnityEngine;
using UnityEngine.Experimental.Rendering.RenderGraphModule;
using UnityEngine.Rendering.HighDefinition;

namespace ShipbreakerVR
{
    // Where HDRP's main thread time goes (PostLateUpdate.FinishFrameRendering in the frame profile): per render request
    // (one per eye, plus one per other camera such as the UI camera), split into building the render graph (the C# that
    // sets up the passes), compiling it, and executing its passes (recording their commands), with the passes that take
    // the most by name. Whatever the render loop does outside the requests (culling, which the frame time line times,
    // Submit, the mirror view) is the rest of the frame time line's "rendering". Only while the frame time line records;
    // logged with it (VrPerformance).
    internal static class VrRenderProfile
    {
        private const int Shown = 10;
        private const double MinShownMs = 0.05;
        private static readonly double TickMs = 1000.0 / Stopwatch.Frequency;

        private static readonly FieldInfo Passes = AccessTools.Field(typeof(RenderGraph), "m_RenderPasses");
        private static readonly MethodInfo PassName = AccessTools.PropertyGetter(AccessTools.TypeByName("UnityEngine.Experimental.Rendering.RenderGraphModule.RenderGraphPass"), "name");
        private static readonly FieldInfo RequestCamera = AccessTools.Field(AccessTools.Inner(typeof(HDRenderPipeline), "RenderRequest"), "hdCamera");

        // The request being rendered: true for an eye of the VR camera.
        public static bool InVrRequest;
        private static string otherCamera;

        private static long vrRequests, vrExecute, vrCompile, vrPasses;
        private static int vrRequestCount;
        private static readonly Dictionary<string, long> PassTicks = new Dictionary<string, long>();
        private static readonly Dictionary<string, long> OtherTicks = new Dictionary<string, long>();
        private static readonly List<KeyValuePair<string, long>> Sorted = new List<KeyValuePair<string, long>>();

        public static bool Recording => VrPerformance.Recording;

        public static void BeginRequest(object request)
        {
            InVrRequest = false;
            otherCamera = null;
            var hdCamera = RequestCamera?.GetValue(request) as HDCamera;
            var vr = VrCamera.Instance;
            if (hdCamera == null) return;
            if (vr != null && hdCamera.camera == vr.Camera) InVrRequest = true;
            else otherCamera = hdCamera.camera != null ? hdCamera.camera.name : "?";
        }

        public static void EndRequest(long ticks)
        {
            if (InVrRequest)
            {
                vrRequests += ticks;
                vrRequestCount++;
            }
            else if (otherCamera != null)
            {
                OtherTicks.TryGetValue(otherCamera, out var sum);
                OtherTicks[otherCamera] = sum + ticks;
            }
            InVrRequest = false;
            otherCamera = null;
        }

        public static void Executed(long ticks) { if (InVrRequest) vrExecute += ticks; }

        public static void Compiled(long ticks) { if (InVrRequest) vrCompile += ticks; }

        public static void PassDone(RenderGraph graph, int index, long ticks)
        {
            if (!InVrRequest) return;
            vrPasses += ticks;
            var name = "?";
            if (Passes?.GetValue(graph) is System.Collections.IList passes && index >= 0 && index < passes.Count && PassName != null)
                name = PassName.Invoke(passes[index], null) as string ?? "?";
            PassTicks.TryGetValue(name, out var sum);
            PassTicks[name] = sum + ticks;
        }

        // Per frame over the given number of frames. Resets.
        public static string Describe(int frames)
        {
            if (frames <= 0 || vrRequestCount == 0)
            {
                Reset();
                return null;
            }
            double Ms(long ticks) => ticks * TickMs / frames;
            var build = vrRequests - vrExecute;
            var text = new StringBuilder();
            text.Append($"VR eyes {Ms(vrRequests):0.00} ms ({(double)vrRequestCount / frames:0.0} requests per frame): building the graph {Ms(build):0.00}, ");
            text.Append($"compiling {Ms(vrCompile):0.00}, passes {Ms(vrPasses):0.00}, rest of executing {Ms(vrExecute - vrCompile - vrPasses):0.00}; passes ");
            AppendTop(text, PassTicks, frames);
            if (OtherTicks.Count > 0)
            {
                text.Append("; other cameras ");
                AppendTop(text, OtherTicks, frames);
            }
            Reset();
            return text.ToString();
        }

        private static void AppendTop(StringBuilder text, Dictionary<string, long> ticks, int frames)
        {
            Sorted.Clear();
            Sorted.AddRange(ticks);
            Sorted.Sort((a, b) => b.Value.CompareTo(a.Value));
            var shown = 0;
            foreach (var entry in Sorted)
            {
                var ms = entry.Value * TickMs / frames;
                if (shown >= Shown || ms < MinShownMs) break;
                text.Append(shown++ > 0 ? ", " : "").Append($"'{entry.Key}' {ms:0.00}");
            }
            if (shown == 0) text.Append("-");
            text.Append(" ms");
        }

        public static void Reset()
        {
            vrRequests = vrExecute = vrCompile = vrPasses = 0;
            vrRequestCount = 0;
            PassTicks.Clear();
            OtherTicks.Clear();
        }
    }

    // One render request of a camera (an eye, or another camera).
    [HarmonyPatch]
    internal static class RenderRequestTimerPatch
    {
        private static MethodBase TargetMethod() => AccessTools.Method(typeof(HDRenderPipeline), "ExecuteRenderRequest");

        private static void Prefix(object __0, out long __state)
        {
            __state = -1L;
            if (!VrRenderProfile.Recording) return;
            VrRenderProfile.BeginRequest(__0);
            __state = Stopwatch.GetTimestamp();
        }

        private static void Postfix(long __state)
        {
            if (__state >= 0) VrRenderProfile.EndRequest(Stopwatch.GetTimestamp() - __state);
        }
    }

    [HarmonyPatch]
    internal static class RenderGraphExecuteTimerPatch
    {
        private static MethodBase TargetMethod() => AccessTools.Method(typeof(RenderGraph), "Execute");

        private static void Prefix(out long __state) => __state = VrRenderProfile.InVrRequest ? Stopwatch.GetTimestamp() : -1L;

        private static void Postfix(long __state)
        {
            if (__state >= 0) VrRenderProfile.Executed(Stopwatch.GetTimestamp() - __state);
        }
    }

    [HarmonyPatch]
    internal static class RenderGraphCompileTimerPatch
    {
        private static MethodBase TargetMethod() => AccessTools.Method(typeof(RenderGraph), "CompileRenderGraph");

        private static void Prefix(out long __state) => __state = VrRenderProfile.InVrRequest ? Stopwatch.GetTimestamp() : -1L;

        private static void Postfix(long __state)
        {
            if (__state >= 0) VrRenderProfile.Compiled(Stopwatch.GetTimestamp() - __state);
        }
    }

    // ExecuteCompiledPass(ref CompiledPassInfo passInfo, int passIndex): the pass's index into the graph's passes.
    [HarmonyPatch]
    internal static class RenderGraphPassTimerPatch
    {
        private static MethodBase TargetMethod() => AccessTools.Method(typeof(RenderGraph), "ExecuteCompiledPass");

        private static void Prefix(out long __state) => __state = VrRenderProfile.InVrRequest ? Stopwatch.GetTimestamp() : -1L;

        private static void Postfix(RenderGraph __instance, int __1, long __state)
        {
            if (__state >= 0) VrRenderProfile.PassDone(__instance, __1, Stopwatch.GetTimestamp() - __state);
        }
    }
}
