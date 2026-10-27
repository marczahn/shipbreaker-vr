using System;
using System.Collections;
using System.Reflection;
using System.Runtime.InteropServices;
using HarmonyLib;
using UnityEngine;
using UnityEngine.Experimental.Rendering;
using UnityEngine.Rendering;
using UnityEngine.XR.OpenXR.Features;

namespace ShipbreakerVR
{
    // [Rendering] AugenAbwechselnd: one eye per frame, the left and the right in turn (alternate eye rendering, as the
    // R.E.A.L. VR mods and UEVR do it for games that are limited by the processor). The main thread spends about as
    // much on each eye as on the game's own logic (building HDRP's render graph, recording the passes, submitting the
    // draws), and the render thread passes every draw to the driver once per eye.
    //
    // Every frame HDRP renders only the eye whose turn it is: the other eye's pass is taken out of the frame's XR passes
    // (and put back before XRSystem.ReleaseFrame, which returns them to their pool). After the render, the rendered eye's
    // image is copied aside, and the other eye's image from the frame before is copied into its swapchain image. The
    // runtime has to know that this image is older: OpenXR submits every view with the pose it was rendered with, and the
    // compositor turns the image from that pose to the head's current one. So xrEndFrame is hooked (an OpenXR feature of
    // ours, VrAlternateEyesFeature) and the skipped view gets the pose and field of view it had when it was rendered.
    // Which eye a frame skipped reaches the render thread, where Unity calls xrEndFrame, through a plugin event in the
    // frame's commands.
    internal static class VrAlternateEyes
    {
        static VrAlternateEyes() => VrSession.OnUnload("alternate eyes", Release);

        private static readonly System.Type XrPassType = AccessTools.TypeByName("UnityEngine.Rendering.HighDefinition.XRPass");
        private static readonly PropertyInfo RenderTarget = AccessTools.Property(XrPassType, "renderTarget");
        private static readonly PropertyInfo RenderTargetDesc = AccessTools.Property(XrPassType, "renderTargetDesc");
        private static readonly MethodInfo TextureArraySlice = AccessTools.Method(XrPassType, "GetTextureArraySlice");
        private static readonly object[] FirstView = { 0 };

        public static bool Enabled => VrSettings.AlternateEyes.Value && VrXr.IsRunning && VrAlternateEyesXr.Hooked;

        // This frame: each eye's XR pass, and which eye was taken out (-1: none).
        private static readonly object[] eyePasses = new object[2];
        private static int skipped = -1;
        private static IList removedFrom;
        private static object removedEntry;
        private static FieldInfo item1, item2;

        // Each eye's last image, and whether it is current (rendered with the target's size and format).
        private static readonly RenderTexture[] saved = new RenderTexture[2];
        private static readonly bool[] savedValid = new bool[2];
        private static CommandBuffer cmd;
        private static bool attached;
        private static bool loggedStart;

        // Plugin events: the skipped eye, or None.
        public const int None = 2;
        private static RenderEvent renderEvent;
        private static IntPtr renderEventPtr;

        [UnmanagedFunctionPointer(CallingConvention.Winapi)]
        private delegate void RenderEvent(int eventId);

        public static void Toggle()
        {
            VrSettings.AlternateEyes.Value = !VrSettings.AlternateEyes.Value;
            Plugin.Log.LogInfo($"Alternate eyes (one eye per frame) {(VrSettings.AlternateEyes.Value ? "on" : "off")} ({VrSettings.AlternateEyesKey.Value}){(VrAlternateEyesXr.Hooked ? "" : "; not available: xrEndFrame is not hooked (VR must have started with this version)")}");
        }

        // After XRSystem.SetupFrame, before VrEyeSharing: the frame's passes as (Camera, XRPass); takes out the eye whose
        // turn it isn't, once both eyes have an image to show.
        public static void AfterSetupFrame(IList passes)
        {
            eyePasses[0] = eyePasses[1] = null;
            skipped = -1;
            removedFrom = null;
            removedEntry = null;
            var vr = VrCamera.Instance;
            if (vr == null || passes == null || XrPassType == null) return;
            int first = -1, second = -1;
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
                if (pass == null || !XrPassType.IsInstanceOfType(pass)) continue;
                if (first < 0) first = i;
                else if (second < 0) second = i;
            }
            if (first < 0 || second < 0) return;
            eyePasses[0] = item2.GetValue(passes[first]);
            eyePasses[1] = item2.GetValue(passes[second]);
            Attach();
            if (!Enabled || !savedValid[0] || !savedValid[1]) return;
            skipped = Time.frameCount & 1;
            var index = skipped == 0 ? first : second;
            removedEntry = passes[index];
            removedFrom = passes;
            passes.RemoveAt(index);
            if (!loggedStart)
            {
                loggedStart = true;
                Plugin.Log.LogInfo("Alternate eyes: rendering one eye per frame");
            }
        }

        // Before XRSystem.ReleaseFrame: the pass taken out goes back, so it is released with the others.
        public static void BeforeReleaseFrame()
        {
            if (removedFrom != null && removedEntry != null) removedFrom.Add(removedEntry);
            removedFrom = null;
            removedEntry = null;
        }

        private static void Attach()
        {
            if (attached) return;
            attached = true;
            RenderPipelineManager.endFrameRendering += OnEndFrame;
            VrAlternateEyesXr.Register();
        }

        // After HDRP's render: the rendered eye's image is saved, the skipped eye's saved image goes into its target.
        private static void OnEndFrame(ScriptableRenderContext context, Camera[] cameras)
        {
            if (eyePasses[0] == null || eyePasses[1] == null) return;
            var enabled = Enabled;
            if (!enabled && skipped < 0)
            {
                savedValid[0] = savedValid[1] = false;
                return;
            }
            if (cmd == null) cmd = new CommandBuffer { name = "ShipbreakerVR alternate eyes" };
            cmd.Clear();
            for (var eye = 0; eye < 2; eye++)
            {
                var pass = eyePasses[eye];
                var target = (RenderTargetIdentifier)RenderTarget.GetValue(pass);
                var desc = (RenderTextureDescriptor)RenderTargetDesc.GetValue(pass);
                var slice = Mathf.Max(0, (int)TextureArraySlice.Invoke(pass, FirstView));
                var copy = Saved(eye, desc);
                if (eye == skipped) cmd.CopyTexture(copy, 0, target, slice);
                else
                {
                    cmd.CopyTexture(target, slice, copy, 0);
                    savedValid[eye] = true;
                }
            }
            if (renderEvent == null)
            {
                renderEvent = VrAlternateEyesXr.OnRenderEvent;
                renderEventPtr = Marshal.GetFunctionPointerForDelegate(renderEvent);
            }
            cmd.IssuePluginEvent(renderEventPtr, skipped < 0 ? None : skipped);
            context.ExecuteCommandBuffer(cmd);
            context.Submit();
            eyePasses[0] = eyePasses[1] = null;
        }

        // The eye's saved image, made again (and not current) when the target's size or format changed.
        private static RenderTexture Saved(int eye, RenderTextureDescriptor target)
        {
            var copy = saved[eye];
            if (copy != null && copy.width == target.width && copy.height == target.height && copy.graphicsFormat == target.graphicsFormat) return copy;
            if (copy != null) copy.Release();
            var desc = new RenderTextureDescriptor(target.width, target.height, target.graphicsFormat, 0)
            {
                dimension = TextureDimension.Tex2D,
                volumeDepth = 1,
                msaaSamples = 1,
            };
            copy = new RenderTexture(desc) { name = $"ShipbreakerVR eye {eye}" };
            copy.Create();
            saved[eye] = copy;
            savedValid[eye] = false;
            Plugin.Log.LogInfo($"Alternate eyes: image of eye {eye} kept in {target.width}x{target.height} {target.graphicsFormat}");
            return copy;
        }

        private static void Release()
        {
            if (attached) RenderPipelineManager.endFrameRendering -= OnEndFrame;
            attached = false;
            for (var eye = 0; eye < 2; eye++)
            {
                if (saved[eye] != null) saved[eye].Release();
                saved[eye] = null;
                savedValid[eye] = false;
            }
            cmd?.Release();
            cmd = null;
            VrAlternateEyesXr.Unregister();
        }
    }

    // The xrEndFrame side, on the render thread. The hook itself lives in VrAlternateEyesFeature and survives a reload
    // of the plugin (the OpenXR instance does); it reaches the current plugin's handler through the app domain.
    internal static class VrAlternateEyesXr
    {
        public const string HandlerKey = "ShipbreakerVR.AlternateEyes.EndFrame";
        public const string HookedKey = "ShipbreakerVR.AlternateEyes.Hooked";

        public static bool Hooked => AppDomain.CurrentDomain.GetData(HookedKey) is bool hooked && hooked;

        // OpenXR structure types and the offsets of a 64 bit build.
        private const int TypeProjectionLayer = 35;
        private const int FrameEndInfoSize = 40, LayerCountOffset = 28, LayersOffset = 32;
        private const int ProjectionLayerSize = 48, ViewCountOffset = 32, ViewsOffset = 40;
        private const int ViewSize = 96, PoseOffset = 16, PoseAndFovSize = 44;
        private const int MaxLayers = 16, MaxViews = 4;

        private static volatile int pendingSkip = VrAlternateEyes.None;
        private static readonly byte[][] lastPose = { new byte[PoseAndFovSize], new byte[PoseAndFovSize] };
        private static readonly bool[] lastPoseValid = new bool[2];
        private static IntPtr info, layers, layer, views;
        private static Func<IntPtr, IntPtr> handler;
        private static int errors;
        private static bool loggedRewrite;

        public static void Register()
        {
            if (handler != null) return;
            info = Marshal.AllocHGlobal(FrameEndInfoSize);
            layers = Marshal.AllocHGlobal(MaxLayers * IntPtr.Size);
            layer = Marshal.AllocHGlobal(ProjectionLayerSize);
            views = Marshal.AllocHGlobal(MaxViews * ViewSize);
            handler = Rewrite;
            AppDomain.CurrentDomain.SetData(HandlerKey, handler);
        }

        // Kept allocated: the render thread may still be in Rewrite.
        public static void Unregister()
        {
            if (AppDomain.CurrentDomain.GetData(HandlerKey) as Func<IntPtr, IntPtr> == handler) AppDomain.CurrentDomain.SetData(HandlerKey, null);
        }

        public static void OnRenderEvent(int eventId) => pendingSkip = eventId;

        // The frame end info to hand on: the original, or a copy whose skipped view carries the pose and field of view it
        // had when it was rendered. Every rendered view's pose is kept for that.
        private static IntPtr Rewrite(IntPtr frameEndInfo)
        {
            var skip = pendingSkip;
            pendingSkip = VrAlternateEyes.None;
            try
            {
                var count = Marshal.ReadInt32(frameEndInfo, LayerCountOffset);
                var source = Marshal.ReadIntPtr(frameEndInfo, LayersOffset);
                if (source == IntPtr.Zero || count <= 0 || count > MaxLayers) return frameEndInfo;
                for (var i = 0; i < count; i++)
                {
                    var current = Marshal.ReadIntPtr(source, i * IntPtr.Size);
                    if (current == IntPtr.Zero || Marshal.ReadInt32(current) != TypeProjectionLayer) continue;
                    var viewCount = Marshal.ReadInt32(current, ViewCountOffset);
                    var sourceViews = Marshal.ReadIntPtr(current, ViewsOffset);
                    if (sourceViews == IntPtr.Zero || viewCount < 2 || viewCount > MaxViews) return frameEndInfo;
                    var rewrite = skip == 0 || skip == 1;
                    for (var view = 0; view < 2; view++)
                    {
                        if (rewrite && view == skip) continue;
                        Marshal.Copy(sourceViews + view * ViewSize + PoseOffset, lastPose[view], 0, PoseAndFovSize);
                        lastPoseValid[view] = true;
                    }
                    if (!rewrite || !lastPoseValid[skip]) return frameEndInfo;
                    Copy(frameEndInfo, info, FrameEndInfoSize);
                    Copy(source, layers, count * IntPtr.Size);
                    Copy(current, layer, ProjectionLayerSize);
                    Copy(sourceViews, views, viewCount * ViewSize);
                    Marshal.Copy(lastPose[skip], 0, views + skip * ViewSize + PoseOffset, PoseAndFovSize);
                    Marshal.WriteIntPtr(layer, ViewsOffset, views);
                    Marshal.WriteIntPtr(layers, i * IntPtr.Size, layer);
                    Marshal.WriteIntPtr(info, LayersOffset, layers);
                    if (!loggedRewrite)
                    {
                        loggedRewrite = true;
                        Plugin.Log.LogInfo($"Alternate eyes: xrEndFrame gives eye {skip} the pose it was rendered with");
                    }
                    return info;
                }
            }
            catch (Exception e)
            {
                if (errors++ < 3) Plugin.Log.LogError($"Alternate eyes: xrEndFrame: {e}");
            }
            return frameEndInfo;
        }

        private static readonly byte[] buffer = new byte[MaxViews * ViewSize];

        private static void Copy(IntPtr from, IntPtr to, int bytes)
        {
            Marshal.Copy(from, buffer, 0, bytes);
            Marshal.Copy(buffer, 0, to, bytes);
        }
    }

    // Hooks xrEndFrame for VrAlternateEyes. Added to the OpenXR settings before the loader starts (VrXr), always
    // enabled; without a handler, or with the option off, it hands every frame on unchanged.
    internal class VrAlternateEyesFeature : OpenXRFeature
    {
        [UnmanagedFunctionPointer(CallingConvention.Winapi)]
        private delegate int GetInstanceProcAddrFn(ulong instance, IntPtr name, IntPtr function);

        [UnmanagedFunctionPointer(CallingConvention.Winapi)]
        private delegate int EndFrameFn(ulong session, IntPtr frameEndInfo);

        // Static: the runtime calls them for as long as the instance lives.
        private static GetInstanceProcAddrFn originalGetProc, hookedGetProc;
        private static EndFrameFn originalEndFrame, hookedEndFrame;

        protected override IntPtr HookGetInstanceProcAddr(IntPtr func)
        {
            originalGetProc = Marshal.GetDelegateForFunctionPointer<GetInstanceProcAddrFn>(func);
            hookedGetProc = GetInstanceProcAddr;
            Plugin.Log.LogInfo("Alternate eyes: OpenXR feature hooked xrGetInstanceProcAddr");
            return Marshal.GetFunctionPointerForDelegate(hookedGetProc);
        }

        private static int GetInstanceProcAddr(ulong instance, IntPtr name, IntPtr function)
        {
            var result = originalGetProc(instance, name, function);
            if (result != 0 || function == IntPtr.Zero || Marshal.PtrToStringAnsi(name) != "xrEndFrame") return result;
            var original = Marshal.ReadIntPtr(function);
            if (original == IntPtr.Zero) return result;
            originalEndFrame = Marshal.GetDelegateForFunctionPointer<EndFrameFn>(original);
            hookedEndFrame = EndFrame;
            Marshal.WriteIntPtr(function, Marshal.GetFunctionPointerForDelegate(hookedEndFrame));
            AppDomain.CurrentDomain.SetData(VrAlternateEyesXr.HookedKey, true);
            return result;
        }

        private static int EndFrame(ulong session, IntPtr frameEndInfo)
        {
            var info = frameEndInfo;
            if (AppDomain.CurrentDomain.GetData(VrAlternateEyesXr.HandlerKey) is Func<IntPtr, IntPtr> handler)
            {
                try { info = handler(frameEndInfo); }
                catch { info = frameEndInfo; }
            }
            return originalEndFrame(session, info);
        }
    }

    [HarmonyPatch]
    internal static class XrReleaseFramePatch
    {
        private static MethodBase TargetMethod() =>
            AccessTools.Method(AccessTools.TypeByName("UnityEngine.Rendering.HighDefinition.XRSystem"), "ReleaseFrame");

        private static void Prefix() => VrAlternateEyes.BeforeReleaseFrame();
    }
}
