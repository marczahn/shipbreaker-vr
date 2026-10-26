using System.Collections.Generic;
using System.Reflection;
using BBI;
using BBI.Unity.Game;
using HarmonyLib;
using UnityEngine;

namespace ShipbreakerVR
{
    // Makes the body follow the head. The game rotates the body in OrientationController.HandleAxisRotation via
    // Rigidbody.MoveRotation(rotation * mouse rotation). We intercept that call: the mouse rotation turns the playspace,
    // and the body is set to playspace * head. If something else rotates the body (death, respawn), we adopt its orientation.
    internal static class VrBody
    {
        private const float ReorientSpeed = 2f;
        private const int RecentCount = 8;

        // Rotation of the game camera relative to the body, measured at sync.
        private static Quaternion cameraOffset = Quaternion.identity;
        private static bool synced;
        private static bool offsetLogged;

        // Recently set targets; MoveRotation only applies on the next physics step, so the body is at one of these.
        private static readonly Quaternion[] recent = new Quaternion[RecentCount];
        private static int recentNext;

        public static void Resync() => synced = false;

        public static void MoveRotation(Rigidbody body, Quaternion gameTarget)
        {
            var vr = VrCamera.Instance;
            if (vr == null || !vr.BodyFollowsHead)
            {
                // Without VR the game turns the body as usual.
                synced = false;
                // Turns from the motion controllers act on the body directly; the camera follows it.
                body.MoveRotation(vr != null ? VrControls.TakeTurn() * gameTarget : gameTarget);
                return;
            }

            var current = body.rotation;
            var mouse = Quaternion.Inverse(current) * gameTarget;
            var head = vr.HeadRotation;

            if (!synced || !IsRecent(current))
            {
                var cam = LynxCameraController.MainCameraTransform;
                if (!synced && cam != null) cameraOffset = Quaternion.Inverse(body.transform.rotation) * cam.rotation;
                if (!offsetLogged)
                {
                    offsetLogged = true;
                    Plugin.Log.LogInfo($"Body follows head. Camera relative to body: {cameraOffset.eulerAngles}");
                }
                // Place the playspace so the view stays exactly where the game camera is looking.
                vr.Playspace = current * cameraOffset * Quaternion.Inverse(head);
                System.Array.Clear(recent, 0, RecentCount);
                Remember(current);
                synced = true;
            }

            // Head expressed as a body rotation; the mouse rotation acts around the body's axes.
            var headAsBody = head * Quaternion.Inverse(cameraOffset);
            var playspace = vr.Playspace * headAsBody * mouse * Quaternion.Inverse(headAsBody);
            // Roll and turns from the motion controllers act in world space.
            playspace = VrControls.TakeTurn() * playspace;

            // In VR, "Reorient" levels the playspace instead of the body.
            if (LynxControls.Instance != null && LynxControls.Instance.GetInputIsPressed(GameplayActions.GameplayActionSet.Reorient))
            {
                var forward = playspace * Vector3.forward;
                forward.y = 0f;
                if (forward.sqrMagnitude > 1e-6f)
                    playspace = Quaternion.Slerp(playspace, Quaternion.LookRotation(forward.normalized, Vector3.up), ReorientSpeed * Time.deltaTime);
            }

            vr.Playspace = playspace;
            var target = playspace * headAsBody;
            Remember(target);
            body.MoveRotation(target);
        }

        private static void Remember(Quaternion rotation)
        {
            recent[recentNext] = rotation;
            recentNext = (recentNext + 1) % RecentCount;
        }

        private static bool IsRecent(Quaternion rotation)
        {
            foreach (var r in recent)
                if (Mathf.Abs(Quaternion.Dot(r, rotation)) > 0.999999f) return true;
            return false;
        }
    }

    [HarmonyPatch(typeof(OrientationController), "HandleAxisRotation")]
    internal static class OrientationControllerAxisRotationPatch
    {
        private static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> instructions)
        {
            var moveRotation = AccessTools.Method(typeof(Rigidbody), nameof(Rigidbody.MoveRotation));
            var replacement = AccessTools.Method(typeof(VrBody), nameof(VrBody.MoveRotation));
            var replaced = 0;
            foreach (var instruction in instructions)
            {
                if (instruction.Calls(moveRotation))
                {
                    instruction.opcode = System.Reflection.Emit.OpCodes.Call;
                    instruction.operand = replacement;
                    replaced++;
                }
                yield return instruction;
            }
            Plugin.Log.LogInfo($"OrientationController: {replaced} rotation call(s) redirected");
        }
    }

    // On "Reorient" the game aligns the body directly; in VR, VrBody handles it via the playspace.
    [HarmonyPatch(typeof(OrientationController), "HandleReorient")]
    internal static class OrientationControllerReorientPatch
    {
        private static bool Prefix() => VrCamera.Instance == null || !VrCamera.Instance.BodyFollowsHead;
    }
}
