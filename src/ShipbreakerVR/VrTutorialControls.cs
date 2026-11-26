using System.Collections.Generic;
using System.Reflection;
using System.Reflection.Emit;
using BBI.Unity.Game;
using HarmonyLib;
using InControl;
using UnityEngine;

namespace ShipbreakerVR
{
    // Raik (2026-11-26): the tutorial's steps for the controls never completed in VR, first "roll", then "thrust left,
    // right, forward, back", then "brake" (both brake actions held; Y brakes with both). Their objectives (ControlActionObjective, checked by
    // UpdateControlActionObjectiveProgressSystem) wait for the game's single actions to be held (RollBodyLeft/Right,
    // ThrustMoveForward/Backward/Left/Right/Up/Down), but the mod rolls the body itself and gives its thrust into the
    // game's thrust axes (VrControls, ThrustAxisPatch), so those actions never went down. In that check only, an action
    // counts as held while the mod does what it stands for: a roll that way (VTOL tilting the left controller, Shooter
    // the left stick with the trigger held; ModifiedRoll for either way), thrust along that axis of at least
    // ThrustShare (VTOL moving the controller, Shooter the left stick), at full value, going down and up with it.
    // Elsewhere the actions are left as they are, so the game does not roll or thrust a second time.
    [HarmonyPatch]
    internal static class VrTutorialControls
    {
        // The share of full thrust along an axis that counts as its action held (the objectives want 0.3 of an axis).
        private const float ThrustShare = 0.3f;

        private static readonly HashSet<string> Getters = new HashSet<string> { "get_IsPressed", "get_Value", "get_WasPressed", "get_WasReleased" };
        private static readonly GameplayActions.GameplayActionSet[] Watched =
        {
            GameplayActions.GameplayActionSet.RollBodyLeft, GameplayActions.GameplayActionSet.RollBodyRight,
            GameplayActions.GameplayActionSet.ModifiedRollBodyLeft, GameplayActions.GameplayActionSet.ModifiedRollBodyRight,
            GameplayActions.GameplayActionSet.ModifiedRoll,
            GameplayActions.GameplayActionSet.ThrustMoveForward, GameplayActions.GameplayActionSet.ThrustMoveBackward,
            GameplayActions.GameplayActionSet.ThrustMoveLeft, GameplayActions.GameplayActionSet.ThrustMoveRight,
            GameplayActions.GameplayActionSet.ThrustMoveUp, GameplayActions.GameplayActionSet.ThrustMoveDown,
            GameplayActions.GameplayActionSet.ThrustBrakeLeft, GameplayActions.GameplayActionSet.ThrustBrakeRight,
        };
        private static readonly PlayerAction[] Actions = new PlayerAction[Watched.Length];
        // Which of Watched the mod does, this frame and the frame before (bit i for Watched[i]).
        private static int now, before;
        private static int resolved = -1;
        private static int logs;

        private static MethodBase TargetMethod() => AccessTools.Method(typeof(UpdateControlActionObjectiveProgressSystem), "OnUpdate");

        private static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> instructions, MethodBase original)
        {
            var replaced = 0;
            foreach (var instruction in instructions)
            {
                if ((instruction.opcode == OpCodes.Callvirt || instruction.opcode == OpCodes.Call) && instruction.operand is MethodInfo method
                    && Getters.Contains(method.Name) && method.DeclaringType != null && method.DeclaringType.IsAssignableFrom(typeof(PlayerAction)))
                {
                    instruction.opcode = OpCodes.Call;
                    instruction.operand = AccessTools.Method(typeof(VrTutorialControls), method.Name.Substring(4));
                    replaced++;
                }
                yield return instruction;
            }
            PatchReport.Count("Tutorial controls", original, replaced, "action read(s)");
        }

        private static void Resolve()
        {
            if (resolved == Time.frameCount) return;
            resolved = Time.frameCount;
            before = now;
            now = 0;
            var controls = LynxControls.Instance;
            for (var i = 0; i < Watched.Length; i++) Actions[i] = controls != null ? controls.TryGetLoadedPlayerAction(Watched[i]) : null;
            if (!VrXr.IsRunning) return;
            var roll = VrControls.Rolling;
            VrControls.TryGetThrust(out var thrust);
            for (var i = 0; i < Watched.Length; i++)
                if (Does(Watched[i], roll, thrust)) now |= 1 << i;
            if (now != 0 && (before & now) != now && logs++ < 20)
                Plugin.Log.LogInfo($"Tutorial controls: the controllers count as the game's {Names(now & ~before)} for the objective");
        }

        private static bool Does(GameplayActions.GameplayActionSet action, int roll, Vector3 thrust)
        {
            switch (action)
            {
                case GameplayActions.GameplayActionSet.RollBodyLeft:
                case GameplayActions.GameplayActionSet.ModifiedRollBodyLeft: return roll > 0;
                case GameplayActions.GameplayActionSet.RollBodyRight:
                case GameplayActions.GameplayActionSet.ModifiedRollBodyRight: return roll < 0;
                case GameplayActions.GameplayActionSet.ModifiedRoll: return roll != 0;
                case GameplayActions.GameplayActionSet.ThrustMoveForward: return thrust.z >= ThrustShare;
                case GameplayActions.GameplayActionSet.ThrustMoveBackward: return thrust.z <= -ThrustShare;
                case GameplayActions.GameplayActionSet.ThrustMoveRight: return thrust.x >= ThrustShare;
                case GameplayActions.GameplayActionSet.ThrustMoveLeft: return thrust.x <= -ThrustShare;
                case GameplayActions.GameplayActionSet.ThrustMoveUp: return thrust.y >= ThrustShare;
                case GameplayActions.GameplayActionSet.ThrustMoveDown: return thrust.y <= -ThrustShare;
                // The brake objective wants both of the gamepad's brakes held; Y brakes with both.
                case GameplayActions.GameplayActionSet.ThrustBrakeLeft:
                case GameplayActions.GameplayActionSet.ThrustBrakeRight: return VrControls.IsBraking();
                default: return false;
            }
        }

        private static string Names(int bits)
        {
            var names = new List<string>();
            for (var i = 0; i < Watched.Length; i++)
                if ((bits & (1 << i)) != 0) names.Add(Watched[i].ToString());
            return string.Join(", ", names);
        }

        // Whether the mod does what that action stands for, now (or a frame before).
        private static bool Does(OneAxisInputControl action, bool earlier)
        {
            if (action == null) return false;
            Resolve();
            var bits = earlier ? before : now;
            if (bits == 0) return false;
            for (var i = 0; i < Actions.Length; i++)
                if (Actions[i] == action) return (bits & (1 << i)) != 0;
            return false;
        }

        private static bool IsPressed(OneAxisInputControl action) => action.IsPressed || Does(action, false);

        private static float Value(OneAxisInputControl action) => Does(action, false) ? 1f : action.Value;

        private static bool WasPressed(OneAxisInputControl action) => action.WasPressed || Does(action, false) && !Does(action, true);

        private static bool WasReleased(OneAxisInputControl action) => action.WasReleased || !Does(action, false) && Does(action, true) && !action.IsPressed;
    }
}
