using System.Reflection;
using HarmonyLib;
using UnityEngine;
using UnityEngine.XR;

namespace ShipbreakerVR
{
    // Scales the distance between the eyes by VrCamera.EyeScale (world scale times stereo depth).
    // With the real eye distance stereo vision only gives depth up to a few meters; a wider base gives depth
    // and size cues further out (and makes the world look smaller), a narrower one the opposite.
    // HDRP takes each eye's view matrix from the XR display (XRPass.AddView); we move the eye along its offset from
    // the head center. The culling frustum stays the headset's, so very large factors can clip objects at the edges.
    [HarmonyPatch]
    internal static class VrStereo
    {
        private static MethodBase TargetMethod() =>
            AccessTools.Method(AccessTools.TypeByName("UnityEngine.Rendering.HighDefinition.XRPass"), "AddView",
                new[] { typeof(XRDisplaySubsystem.XRRenderPass), typeof(XRDisplaySubsystem.XRRenderParameter) });

        private static int loggedEyes;
        private static float loggedScale = -1f;

        private static void Prefix(ref XRDisplaySubsystem.XRRenderParameter __1)
        {
            var vr = VrCamera.Instance;
            if (vr == null || !VrXr.IsRunning) return;

            var eyeToWorld = __1.view.inverse;
            var center = vr.transform.position;
            var offset = (Vector3)eyeToWorld.GetColumn(3) - center;
            var rendered = offset.magnitude;
            if (rendered < 1e-5f) return;

            // In case Unity already scales the eye offset with the rig, relate it to the headset's real eye distance.
            var real = RealHalfIpd();
            var applied = real > 1e-3f ? rendered / real : 1f;
            var target = VrCamera.EyeScale;
            var eye = center + offset * (target / applied);
            eyeToWorld.SetColumn(3, new Vector4(eye.x, eye.y, eye.z, 1f));
            __1.view = eyeToWorld.inverse;

            if (!Mathf.Approximately(loggedScale, target))
            {
                loggedScale = target;
                loggedEyes = 0;
            }
            var side = Vector3.Dot(offset, vr.transform.right) < 0f ? 0 : 1;
            if ((loggedEyes & (1 << side)) != 0) return;
            loggedEyes |= 1 << side;
            Plugin.Log.LogInfo($"{(side == 0 ? "Left" : "Right")} eye: offset from head {rendered * 1000f:0.0} mm as rendered by Unity, " +
                               $"headset {(real > 1e-3f ? $"{real * 1000f:0.0} mm" : "unknown")}, now {(eye - center).magnitude * 1000f:0.0} mm (x{target:0.00})");
        }

        // Half the distance between the eyes as the headset reports it, in tracking space (unaffected by the rig).
        private static float RealHalfIpd()
        {
            var head = InputDevices.GetDeviceAtXRNode(XRNode.Head);
            if (!head.isValid ||
                !head.TryGetFeatureValue(CommonUsages.leftEyePosition, out var left) ||
                !head.TryGetFeatureValue(CommonUsages.rightEyePosition, out var right)) return 0f;
            return (left - right).magnitude * 0.5f;
        }
    }
}
