using System.Collections.Generic;
using System.Reflection;
using BBI;
using BBI.Unity.Game;
using HarmonyLib;
using UnityEngine;

namespace ShipbreakerVR
{
    // Lässt die Figur dem Kopf folgen. Das Spiel dreht die Figur in OrientationController.HandleAxisRotation per
    // Rigidbody.MoveRotation(Drehung * Mausdrehung). Wir fangen diesen Aufruf ab: Die Mausdrehung dreht den Spielraum,
    // die Figur wird auf Spielraum * Kopf gesetzt. Dreht etwas anderes die Figur (Tod, Respawn), übernehmen wir deren Lage.
    internal static class VrBody
    {
        private const float ReorientSpeed = 2f;
        private const int RecentCount = 8;

        // Drehung der Spielkamera relativ zur Figur, gemessen beim Abgleich.
        private static Quaternion cameraOffset = Quaternion.identity;
        private static bool synced;
        private static bool offsetLogged;

        // Zuletzt gesetzte Ziele; MoveRotation wirkt erst im nächsten Physikschritt, die Figur steht also auf einem davon.
        private static readonly Quaternion[] recent = new Quaternion[RecentCount];
        private static int recentNext;

        public static void Resync() => synced = false;

        public static void MoveRotation(Rigidbody body, Quaternion gameTarget)
        {
            var vr = VrCamera.Instance;
            if (vr == null || !vr.BodyFollowsHead)
            {
                synced = false;
                body.MoveRotation(gameTarget);
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
                    Plugin.Log.LogInfo($"Figur folgt dem Kopf. Kamera relativ zur Figur: {cameraOffset.eulerAngles}");
                }
                // Spielraum so legen, dass der Blick genau dort bleibt, wo die Spielkamera hinschaut.
                vr.Playspace = current * cameraOffset * Quaternion.Inverse(head);
                System.Array.Clear(recent, 0, RecentCount);
                Remember(current);
                synced = true;
            }

            // Kopf als Drehung der Figur ausgedrückt; die Mausdrehung wirkt um die Achsen der Figur.
            var headAsBody = head * Quaternion.Inverse(cameraOffset);
            var playspace = vr.Playspace * headAsBody * mouse * Quaternion.Inverse(headAsBody);

            // "Aufrichten" richtet in VR den Spielraum auf statt der Figur.
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
            Plugin.Log.LogInfo($"OrientationController: {replaced} Drehaufruf(e) umgeleitet");
        }
    }

    // Das Spiel richtet beim "Aufrichten" die Figur direkt aus; in VR übernimmt das VrBody über den Spielraum.
    [HarmonyPatch(typeof(OrientationController), "HandleReorient")]
    internal static class OrientationControllerReorientPatch
    {
        private static bool Prefix() => VrCamera.Instance == null || !VrCamera.Instance.BodyFollowsHead;
    }
}
