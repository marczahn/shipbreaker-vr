using BBI.Unity.Game;
using HarmonyLib;
using UnityEngine;

namespace ShipbreakerVR
{
    // The hand goes along with the tool's recoil (Raik, 2026-11-27: the tool moved through the hand). The game shakes
    // the tool's model in its own Animator: the cutter's (controller Anim_CuttingTool, state Anim_CuttingTool_Recoil,
    // 1.5 s, on each cut, the wide split saw too, hit or not) moves and turns the Animator's own transform; the grapple's
    // (AC_GrappleTool_01, states Recoil, Push 1, Push 2, Impact) moves and turns its bone SK_GrappleTool_Root, the whole
    // gun, with its pieces on it. VrToolModels holds the tool's point still in the hand, so that part moved off the
    // point while the hand stayed. Now each frame, before the hands are posed (VrHands), the part's pose against the
    // point is compared with its pose at rest (taken while the Animator is in a resting state: Anim_CuttingTool_Idle,
    // Idle, Idle2), and the hand holding the tool, and a second hand on it (VrToolSupport), are moved and turned by
    // just as much about the tool, so the grip stays in the hand and the arm follows (its bend, VrHands). The tool
    // itself is placed as before. Only the tool the game knows (VrHand.GameToolHand), not a second tool (VrDualTools).
    internal static class VrToolRecoil
    {
        static VrToolRecoil() => VrSession.OnUnload("tool recoil", Release);

        private static readonly int[] RestStates =
        {
            Animator.StringToHash("Anim_CuttingTool_Idle"),
            Animator.StringToHash("Idle"),
            Animator.StringToHash("Idle2"),
        };
        private const string GrappleBody = "SK_GrappleTool_Root";

        private static readonly AccessTools.FieldRef<CuttingToolController, Animator> CutterAnimator =
            AccessTools.FieldRefAccess<CuttingToolController, Animator>("m_CuttingToolAnimator");
        private static readonly AccessTools.FieldRef<GrapplingHook, Animator> GrappleAnimator =
            AccessTools.FieldRefAccess<GrapplingHook, Animator>("m_GrapplingGunAnimator");

        private static Animator animator;
        private static Transform point, body;
        // The part's pose against the point at rest, in the point's axes but world units.
        private static bool hasRest;
        private static Vector3 restPosition;
        private static Quaternion restRotation = Quaternion.identity;
        // This frame's move of the part, in the tool hand's axes (its aim) and world units: turned by Turn about the
        // hand's grip, then shifted by Shift.
        private static bool moved;
        private static Vector3 shift;
        private static Quaternion turnBy = Quaternion.identity;
        // The largest move of this recoil, for the log.
        private static float mostShift, mostTurn;
        private static int logs;

        // Each frame, in LateUpdate before the hands (VrFrame): after the game's animation, so the part is where this
        // frame's recoil has it.
        public static void Update()
        {
            moved = false;
            var hand = VrHand.GameToolHand;
            if (!VrXr.IsRunning || hand == null || !hand.HoldsTool || !VrToolModels.TryGetFrame(out var model, out var mount, out var turn))
            {
                Forget();
                return;
            }
            var tool = hand.Tool;
            var found = tool == EquipmentController.Equipment.CuttingTool ? Of(GameRefs.Cutter)
                : tool == EquipmentController.Equipment.GrappleHook ? Of(GameRefs.Grapple) : null;
            if (found != animator || model != point)
            {
                animator = found;
                point = model;
                body = Body(found);
                hasRest = false;
                LogFound(tool);
            }
            if (animator == null || body == null || body == point || !body.IsChildOf(point)) return;

            var inverse = Quaternion.Inverse(point.rotation);
            var position = inverse * (body.position - point.position);
            var rotation = inverse * body.rotation;
            if (Resting(animator))
            {
                EndOfRecoil();
                restPosition = position;
                restRotation = rotation;
                hasRest = true;
                return;
            }
            if (!hasRest) return;

            // The point sits at mount in the hand's axes, turned by turn; the part at rest and now from there.
            turnBy = turn * rotation * Quaternion.Inverse(restRotation) * Quaternion.Inverse(turn);
            var now = mount + turn * position;
            var rest = mount + turn * restPosition;
            shift = now - turnBy * rest;
            moved = true;
            mostShift = Mathf.Max(mostShift, (now - rest).magnitude);
            mostTurn = Mathf.Max(mostTurn, Quaternion.Angle(Quaternion.identity, turnBy));
        }

        // A hand's pose in tracking space carried along with the tool's recoil: grip and aim the tool hand's (its frame,
        // also in tracking space), pose that of the hand on the tool, the tool hand itself or a second one.
        public static void Apply(Vector3 grip, Quaternion aim, ref Vector3 position, ref Quaternion rotation)
        {
            if (!moved) return;
            var vr = VrCamera.Instance;
            var scale = vr != null && vr.transform.parent != null ? vr.transform.parent.localScale.x : 1f;
            if (scale <= 0f) return;
            var turned = aim * turnBy * Quaternion.Inverse(aim);
            position = grip + turned * (position - grip) + aim * (shift / scale);
            rotation = turned * rotation;
        }

        private static Animator Of(CuttingToolController cutter) => cutter != null ? CutterAnimator(cutter) : null;
        private static Animator Of(GrapplingHook grapple) => grapple != null ? GrappleAnimator(grapple) : null;

        // The part the recoil moves: the grapple's bone, else the Animator's own transform (the cutter's).
        private static Transform Body(Animator found)
        {
            if (found == null) return null;
            foreach (var child in found.GetComponentsInChildren<Transform>(true))
                if (child.name == GrappleBody) return child;
            return found.transform;
        }

        private static bool Resting(Animator found)
        {
            if (!found.isActiveAndEnabled || found.IsInTransition(0)) return !found.isActiveAndEnabled;
            var state = found.GetCurrentAnimatorStateInfo(0).shortNameHash;
            foreach (var rest in RestStates)
                if (state == rest) return true;
            return false;
        }

        private static void EndOfRecoil()
        {
            if (mostShift <= 0f && mostTurn <= 0f) return;
            if (logs++ < 30)
                Plugin.Log.LogInfo($"Tool recoil: the hand went along with '{body.name}', up to {mostShift * 100f:0.0} cm and {mostTurn:0.0}°");
            mostShift = mostTurn = 0f;
        }

        private static void LogFound(EquipmentController.Equipment tool)
        {
            if (logs++ >= 30) return;
            if (animator == null) Plugin.Log.LogInfo($"Tool recoil: no animator on the {tool}");
            else if (body == null || body == point || !body.IsChildOf(point))
                Plugin.Log.LogInfo($"Tool recoil: '{(body != null ? body.name : "?")}' of the {tool} is not under the tool's point {VrUtil.Chain(point)}, the hand stays as it was");
            else Plugin.Log.LogInfo($"Tool recoil: the {tool}'s hand goes along with '{body.name}' ({VrUtil.Chain(body)})");
        }

        private static void Forget()
        {
            moved = false;
            if (animator == null && point == null) return;
            animator = null;
            point = body = null;
            hasRest = false;
            mostShift = mostTurn = 0f;
        }

        public static void Release()
        {
            Forget();
            logs = 0;
        }
    }
}
