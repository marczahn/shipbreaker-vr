using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using System.Reflection;
using HarmonyLib;
using UnityEngine;
using UnityEngine.Experimental.Rendering.RenderGraphModule;
using UnityEngine.Rendering;
using UnityEngine.Rendering.HighDefinition;

namespace ShipbreakerVR
{
    // Work that MultiPass does twice although both eyes need the same result: culling the scene and rendering the shadow
    // maps. In MultiPass every eye is a render request of its own. HDRP shares the culling results between the eyes only
    // if the runtime gives both passes the same culling pass (HDRenderPipeline.Render, "culling results can be shared if
    // the pass has the same culling id"); otherwise it culls the scene, the shadow casters of every light, decals and
    // probes once per eye. The shadow maps are rendered inside every request (HDShadowManager.RenderShadows) into
    // persistent atlases, from the culling results and the camera's transform, which both eyes share.
    //
    // With [Rendering] AugenTeilen (F10 switches it), the first eye culls a frustum that holds both eyes and the second
    // eye takes its results, and the second eye then keeps the shadow atlases the first eye rendered instead of drawing
    // the same shadow casters into them again.
    internal static class VrEyeSharing
    {
        private static readonly System.Type XrPassType = AccessTools.TypeByName("UnityEngine.Rendering.HighDefinition.XRPass");
        private static readonly PropertyInfo CullingPassId = AccessTools.Property(XrPassType, "cullingPassId");
        private static readonly PropertyInfo CullingParams = AccessTools.Property(XrPassType, "cullingParams");
        private static readonly PropertyInfo MultipassId = AccessTools.Property(XrPassType, "multipassId");
        private static readonly MethodInfo GetViewMatrix = AccessTools.Method(XrPassType, "GetViewMatrix");
        private static readonly PropertyInfo HdCameraXr = AccessTools.Property(typeof(HDCamera), "xr");
        private static readonly FieldInfo CurrentHdCamera = AccessTools.Field(typeof(HDRenderPipeline), "m_CurrentHDCamera");

        // Looser planes by this much (meters, times the world scale), so nothing at the edge of either eye is culled.
        private const float PlaneMargin = 0.05f;

        public static bool Enabled => Plugin.EyeSharing.Value && VrXr.IsRunning;

        // Whether the second eye uses the first eye's culling results this frame (by the runtime or by us).
        public static bool SharedThisFrame { get; private set; }
        private static int sharedFrame = -1;

        // The multipass ids of the VR camera's two eyes this frame (0 and 1 unless another XR camera renders first).
        private static int firstEye = 0, secondEye = 1;

        // The fields of the (Camera, XRPass) tuples SetupFrame returns.
        private static FieldInfo item1, item2;

        // The culling pass indices the runtime gave the two eyes, logged once; the same for moving the eyes last.
        private static bool loggedPasses;
        private static bool loggedOrder;

        private static readonly object[] FirstView = { 0 };

        // Per atlas whether it may be reused at all (not blurred, not a cache); they don't change.
        private static readonly Dictionary<object, bool> reusable = new Dictionary<object, bool>();

        // Shadow atlases the first eye rendered this frame.
        private static readonly HashSet<object> renderedAtlases = new HashSet<object>();
        private static int atlasFrame = -1;
        public static int ReusedAtlases { get; private set; }
        private static bool loggedReuse;

        // Culling statistics for the frame time line (VrPerformance).
        public static int CullCalls;
        public static double CullSeconds;

        public static void Toggle()
        {
            Plugin.EyeSharing.Value = !Plugin.EyeSharing.Value;
            loggedReuse = false;
            Plugin.Log.LogInfo($"Eye sharing (culling and shadow maps once for both eyes) {(Plugin.EyeSharing.Value ? "on" : "off")} (F10)");
        }

        // After XRSystem.SetupFrame: the frame's passes as (Camera, XRPass). For the VR camera's two eyes, cull once.
        public static void AfterSetupFrame(IList passes)
        {
            var vr = VrCamera.Instance;
            if (vr == null || passes == null) return;
            object first = null, second = null;
            int firstIndex = -1, secondIndex = -1;
            for (var i = 0; i < passes.Count; i++)
            {
                var entry = passes[i];
                if (entry == null) continue;
                if (item1 == null || item1.DeclaringType != entry.GetType())
                {
                    item1 = AccessTools.Field(entry.GetType(), "Item1");
                    item2 = AccessTools.Field(entry.GetType(), "Item2");
                }
                if (item1.GetValue(entry) as Camera != vr.Camera) continue;
                var pass = item2.GetValue(entry);
                if (pass == null || XrPassType == null || !XrPassType.IsInstanceOfType(pass)) continue;
                // In order: the left eye's pass comes first.
                if (first == null)
                {
                    first = pass;
                    firstIndex = i;
                }
                else if (second == null)
                {
                    second = pass;
                    secondIndex = i;
                }
            }
            if (first == null || second == null)
            {
                SharedThisFrame = false;
                return;
            }
            var firstCull = (int)CullingPassId.GetValue(first);
            var secondCull = (int)CullingPassId.GetValue(second);
            if (!loggedPasses)
            {
                loggedPasses = true;
                Plugin.Log.LogInfo($"XR culling passes from the runtime: eye 0 culls pass {firstCull}, eye 1 pass {secondCull} ({(firstCull == secondCull ? "HDRP shares the culling" : "HDRP culls each eye")})");
            }
            if (firstCull != secondCull && Enabled)
            {
                var combined = Combine(first, second);
                CullingParams.SetValue(first, combined);
                CullingPassId.SetValue(second, firstCull);
                secondCull = firstCull;
            }
            // HDRP marks a shared culling result as "don't release" in a set it clears at the start of every camera in
            // its culling loop. A camera after the second eye (one rendering into a texture, for example) would wipe the
            // mark, both eyes would release the same result into the pool, and the next frame would hand it out twice.
            // So when the eyes share, they go last; cameras that render into textures then render before the eyes,
            // which only means their images are fresh.
            if (firstCull == secondCull && secondIndex != passes.Count - 1)
            {
                var firstEntry = passes[firstIndex];
                var secondEntry = passes[secondIndex];
                passes.RemoveAt(secondIndex);
                passes.RemoveAt(firstIndex);
                passes.Add(firstEntry);
                passes.Add(secondEntry);
                if (!loggedOrder)
                {
                    loggedOrder = true;
                    Plugin.Log.LogInfo($"XR: the VR camera's eyes moved to the end of {passes.Count} camera passes, so HDRP keeps their shared culling");
                }
            }
            SharedThisFrame = firstCull == secondCull;
            sharedFrame = Time.frameCount;
            firstEye = (int)MultipassId.GetValue(first);
            secondEye = (int)MultipassId.GetValue(second);
        }

        // A culling volume that holds both eyes' frusta: per plane the one of the two eyes that keeps both eyes inside
        // (the left eye's left plane, the right eye's right plane; top, bottom, near and far are nearly the same for
        // both), moved out a little. The same for the shadow caster culling planes.
        private static ScriptableCullingParameters Combine(object first, object second)
        {
            var a = (ScriptableCullingParameters)CullingParams.GetValue(first);
            var b = (ScriptableCullingParameters)CullingParams.GetValue(second);
            var eyeA = EyePosition(first);
            var eyeB = EyePosition(second);
            var margin = PlaneMargin * VrCamera.WorldScale;
            var count = Mathf.Min(a.cullingPlaneCount, b.cullingPlaneCount);
            for (var i = 0; i < count; i++)
                a.SetCullingPlane(i, Looser(a.GetCullingPlane(i), b.GetCullingPlane(i), eyeA, eyeB, margin));
            var propertiesA = a.cameraProperties;
            var propertiesB = b.cameraProperties;
            for (var i = 0; i < 6; i++)
                propertiesA.SetShadowCullingPlane(i, Looser(propertiesA.GetShadowCullingPlane(i), propertiesB.GetShadowCullingPlane(i), eyeA, eyeB, margin));
            a.cameraProperties = propertiesA;
            return a;
        }

        private static Plane Looser(Plane a, Plane b, Vector3 eyeA, Vector3 eyeB, float margin)
        {
            // The plane under which the nearer of the two eyes is further inside.
            var insideA = Mathf.Min(a.GetDistanceToPoint(eyeA), a.GetDistanceToPoint(eyeB));
            var insideB = Mathf.Min(b.GetDistanceToPoint(eyeA), b.GetDistanceToPoint(eyeB));
            var plane = insideA >= insideB ? a : b;
            plane.distance += margin;
            return plane;
        }

        private static Vector3 EyePosition(object pass)
        {
            var view = (Matrix4x4)GetViewMatrix.Invoke(pass, FirstView);
            return view.inverse.GetColumn(3);
        }

        // Inside HDShadowAtlas.RenderShadows: the eye HDRP is rendering, from the pipeline's current HDCamera.
        private static int CurrentEye(out bool isVr)
        {
            isVr = false;
            var vr = VrCamera.Instance;
            if (vr == null || CurrentHdCamera == null) return -1;
            var hdCamera = CurrentHdCamera.GetValue(RenderPipelineManager.currentPipeline) as HDCamera;
            if (hdCamera == null || hdCamera.camera != vr.Camera) return -1;
            isVr = true;
            var xr = HdCameraXr.GetValue(hdCamera);
            return xr != null ? (int)MultipassId.GetValue(xr) : -1;
        }

        // True if the second eye should keep this atlas as the first eye left it. Records the atlases the first eye renders.
        // Cached atlases only render pending updates; blurred ones (EVSM, IM) end in textures that don't survive.
        public static bool SkipAtlas(object atlas, System.Func<object, bool> isReusable)
        {
            if (atlasFrame != Time.frameCount)
            {
                atlasFrame = Time.frameCount;
                renderedAtlases.Clear();
                ReusedAtlases = 0;
            }
            if (!reusable.TryGetValue(atlas, out var canReuse))
            {
                canReuse = isReusable(atlas);
                reusable[atlas] = canReuse;
            }
            if (!canReuse) return false;
            var eye = CurrentEye(out var isVr);
            // Anything else rendered in between (a reflection probe draws its own shadows into the same atlases) leaves
            // the atlas with other content than the first eye's.
            if (!isVr || eye < 0)
            {
                renderedAtlases.Remove(atlas);
                return false;
            }
            if (eye == firstEye)
            {
                renderedAtlases.Add(atlas);
                return false;
            }
            if (eye != secondEye || !Enabled || !SharedThisFrame || sharedFrame != Time.frameCount || !renderedAtlases.Contains(atlas)) return false;
            ReusedAtlases++;
            if (!loggedReuse)
            {
                loggedReuse = true;
                Plugin.Log.LogInfo("Shadow maps rendered by the first eye reused for the second eye");
            }
            return true;
        }
    }

    [HarmonyPatch]
    internal static class XrSetupFramePatch
    {
        private static MethodBase TargetMethod() =>
            AccessTools.Method(AccessTools.TypeByName("UnityEngine.Rendering.HighDefinition.XRSystem"), "SetupFrame");

        private static void Postfix(object __result) => VrEyeSharing.AfterSetupFrame(__result as IList);
    }

    // Times HDRP's culling per camera render request, to see whether the eyes share it and what it costs.
    [HarmonyPatch]
    internal static class TryCullPatch
    {
        private static MethodBase TargetMethod() => AccessTools.Method(typeof(HDRenderPipeline), "TryCull");

        private static void Prefix(Camera camera, out long __state)
        {
            var vr = VrCamera.Instance;
            __state = vr != null && camera == vr.Camera ? Stopwatch.GetTimestamp() : -1L;
        }

        private static void Postfix(long __state)
        {
            if (__state < 0) return;
            VrEyeSharing.CullCalls++;
            VrEyeSharing.CullSeconds += (Stopwatch.GetTimestamp() - __state) / (double)Stopwatch.Frequency;
        }
    }

    // The atlas render with the render graph. Skipped for the second eye: the atlas keeps what the first eye drew.
    [HarmonyPatch]
    internal static class ShadowAtlasRenderGraphPatch
    {
        private static readonly System.Type AtlasType = AccessTools.TypeByName("UnityEngine.Rendering.HighDefinition.HDShadowAtlas");
        private static readonly FieldInfo Atlas = AccessTools.Field(AtlasType, "m_Atlas");
        private static readonly FieldInfo Blur = AccessTools.Field(AtlasType, "m_BlurAlgorithm");
        private static readonly FieldInfo Cache = AccessTools.Field(AtlasType, "m_IsACacheForShadows");

        private static MethodBase TargetMethod()
        {
            foreach (var method in AccessTools.GetDeclaredMethods(AtlasType))
            {
                if (method.Name != "RenderShadows") continue;
                var parameters = method.GetParameters();
                if (parameters.Length > 0 && parameters[0].ParameterType == typeof(RenderGraph)) return method;
            }
            return null;
        }

        private static readonly System.Func<object, bool> Reusable = atlas =>
            System.Convert.ToInt32(Blur.GetValue(atlas)) == 0 && !(bool)Cache.GetValue(atlas);

        internal static bool Skip(object atlas) => VrEyeSharing.SkipAtlas(atlas, Reusable);

        private static bool Prefix(object __instance, RenderGraph renderGraph, ref TextureHandle result)
        {
            if (!Skip(__instance)) return true;
            result = renderGraph.ImportTexture((RTHandle)Atlas.GetValue(__instance));
            return false;
        }
    }

    // The same without the render graph (HDRP's older path, in case the game runs it).
    [HarmonyPatch]
    internal static class ShadowAtlasImmediatePatch
    {
        private static MethodBase TargetMethod()
        {
            foreach (var method in AccessTools.GetDeclaredMethods(AccessTools.TypeByName("UnityEngine.Rendering.HighDefinition.HDShadowAtlas")))
            {
                if (method.Name != "RenderShadows") continue;
                var parameters = method.GetParameters();
                if (parameters.Length == 5 && parameters[0].ParameterType == typeof(CullingResults)) return method;
            }
            return null;
        }

        private static bool Prefix(object __instance) => !ShadowAtlasRenderGraphPatch.Skip(__instance);
    }
}
