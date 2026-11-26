using System.Collections.Generic;
using System.Reflection;
using System.Reflection.Emit;
using BBI.Unity.Game;
using HarmonyLib;
using InControl;
using UnityEngine;

namespace ShipbreakerVR
{
    // Raik (2026-11-26): the tutorial's step "roll" never completed in VR. Its objective (ControlActionObjective, checked
    // by UpdateControlActionObjectiveProgressSystem) waits for the game's roll actions (RollBodyLeft/Right, on a gamepad
    // the sticks pressed in) to be held, but the mod rolls the body itself (VrControls: VTOL tilting the left
    // controller, Shooter the left stick with the trigger held), so the actions never went down. In that check only, the
    // roll actions count as held while the mod rolls that way: left for a roll to the left, right to the right (the
    // modifier ModifiedRoll for either), at full value, going down and up with the roll. Elsewhere the actions are left
    // as they are, so the game does not roll a second time.
    [HarmonyPatch]
    internal static class VrTutorialRoll
    {
        private static readonly HashSet<string> Getters = new HashSet<string> { "get_IsPressed", "get_Value", "get_WasPressed", "get_WasReleased" };
        private static int resolved = -1;
        private static PlayerAction left, right, modifiedLeft, modifiedRight, modifier;
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
                    instruction.operand = AccessTools.Method(typeof(VrTutorialRoll), method.Name.Substring(4));
                    replaced++;
                }
                yield return instruction;
            }
            PatchReport.Count("Tutorial roll", original, replaced, "action read(s)");
        }

        private static void Resolve()
        {
            if (resolved == Time.frameCount) return;
            resolved = Time.frameCount;
            var controls = LynxControls.Instance;
            if (controls == null) { left = right = modifiedLeft = modifiedRight = modifier = null; return; }
            left = controls.TryGetLoadedPlayerAction(GameplayActions.GameplayActionSet.RollBodyLeft);
            right = controls.TryGetLoadedPlayerAction(GameplayActions.GameplayActionSet.RollBodyRight);
            modifiedLeft = controls.TryGetLoadedPlayerAction(GameplayActions.GameplayActionSet.ModifiedRollBodyLeft);
            modifiedRight = controls.TryGetLoadedPlayerAction(GameplayActions.GameplayActionSet.ModifiedRollBodyRight);
            modifier = controls.TryGetLoadedPlayerAction(GameplayActions.GameplayActionSet.ModifiedRoll);
        }

        // Whether the mod rolls the way that action does, now (or a frame before).
        private static bool Rolls(OneAxisInputControl action, bool before)
        {
            if (action == null || !VrXr.IsRunning) return false;
            var roll = before ? VrControls.RolledBefore : VrControls.Rolling;
            if (roll == 0) return false;
            Resolve();
            var rolls = action == modifier || roll > 0 && (action == left || action == modifiedLeft) || roll < 0 && (action == right || action == modifiedRight);
            if (rolls && !before && logs++ < 10) Plugin.Log.LogInfo($"Tutorial roll: rolling {(roll > 0 ? "left" : "right")} counts as the game's roll action for the objective");
            return rolls;
        }

        private static bool IsPressed(OneAxisInputControl action) => action.IsPressed || Rolls(action, false);

        private static float Value(OneAxisInputControl action) => Rolls(action, false) ? 1f : action.Value;

        private static bool WasPressed(OneAxisInputControl action) => action.WasPressed || Rolls(action, false) && !Rolls(action, true);

        private static bool WasReleased(OneAxisInputControl action) => action.WasReleased || !Rolls(action, false) && Rolls(action, true) && !action.IsPressed;
    }
}
