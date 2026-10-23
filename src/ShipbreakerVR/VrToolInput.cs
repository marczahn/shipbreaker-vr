using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using BBI.Unity.Game;
using HarmonyLib;
using UnityEngine;
using UnityEngine.XR;

namespace ShipbreakerVR
{
    // The right controller works the tools while they aim with the hand (VrAim), all on one hand, as Marc asked; the right
    // stick stays with turning, the left hand with flying:
    //   trigger         the tool's main action: cut, grapple (hold to keep hold), place or detonate a demo charge
    //   A               with the trigger held: throw what the grapple holds (hold A to charge, release to throw);
    //                   otherwise interact (doors, switches, containers)
    //   grip            with the trigger held: reel in what the grapple holds
    //   B tap           tool mode (cutter Stinger/Splitsaw, grapple/magnet, demo charge mode)
    //   B held          place a tether from where the hand points when the hold starts to where it points on release;
    //                   held while pointing at nothing: recall all tethers
    // A + X (pause) and B + Y (center the view) are combos of their own; A and B count only without X and Y.
    // The game polls its actions through LynxControlExtensions; those methods are small enough for Mono to inline, so the
    // calls in the tool classes are redirected (ToolInputPatch) to versions that add these presses to the real ones.
    internal static class VrToolInput
    {
        private enum Action { Fire, Throw, Interact, ToolMode, Tether, Recall, Retract }

        private const float TriggerPress = 0.5f;
        private const float TriggerRelease = 0.4f;
        private const float GripPress = 0.5f;
        private const float LongPress = 0.4f;

        private static readonly int Count = System.Enum.GetValues(typeof(Action)).Length;
        private static readonly bool[] now = new bool[Count];
        private static readonly bool[] before = new bool[Count];
        private static readonly Dictionary<LynxControls.ActionSetAndId, Action> Actions = new Dictionary<LynxControls.ActionSetAndId, Action>
        {
            { GameplayActions.GameplayActionSet.GrappleFire, Action.Fire },
            { GameplayActions.GameplayActionSet.CutterFire, Action.Fire },
            { GameplayActions.GameplayActionSet.DemoChargeFire, Action.Fire },
            { GameplayActions.GameplayActionSet.Throw, Action.Throw },
            { GameplayActions.GameplayActionSet.Interact, Action.Interact },
            { GameplayActions.GameplayActionSet.ToolMode, Action.ToolMode },
            { GameplayActions.GameplayActionSet.PlaceTether, Action.Tether },
            { GameplayActions.GameplayActionSet.RecallTethers, Action.Recall },
            { GameplayActions.GameplayActionSet.RetractionModifier, Action.Retract },
        };

        private static InputDevice left;
        private static InputDevice right;
        private static int updatedFrame = -1;
        private static bool trigger;
        // A decides at its press whether it throws (trigger held) or interacts, and keeps that until released.
        private static bool aDown;
        private static bool aThrows;
        private static bool bDown;
        private static float bDownAt;
        private static bool bLong;
        private static bool bTether;
        private static int logs;

        private static void Update()
        {
            if (updatedFrame == Time.frameCount) return;
            updatedFrame = Time.frameCount;
            System.Array.Copy(now, before, Count);
            System.Array.Clear(now, 0, Count);
            if (!VrAim.Active || !Get(ref right, XRNode.RightHand))
            {
                trigger = aDown = bDown = false;
                return;
            }
            Get(ref left, XRNode.LeftHand);

            right.TryGetFeatureValue(CommonUsages.trigger, out var triggerValue);
            trigger = triggerValue >= (trigger ? TriggerRelease : TriggerPress);
            now[(int)Action.Fire] = trigger;
            right.TryGetFeatureValue(CommonUsages.grip, out var grip);
            now[(int)Action.Retract] = trigger && grip >= GripPress;

            var a = Button(right, CommonUsages.primaryButton) && !Button(left, CommonUsages.primaryButton);
            if (a && !aDown) aThrows = trigger;
            aDown = a;
            now[(int)(aThrows ? Action.Throw : Action.Interact)] = a;

            var b = Button(right, CommonUsages.secondaryButton) && !Button(left, CommonUsages.secondaryButton);
            if (b && !bDown)
            {
                bDownAt = Time.unscaledTime;
                bLong = bTether = false;
            }
            if (b && !bLong && Time.unscaledTime - bDownAt >= LongPress)
            {
                bLong = true;
                bTether = VrAim.PointsAtSomething();
                // Recalling is a single press.
                now[(int)Action.Recall] = !bTether;
            }
            now[(int)Action.Tether] = b && bTether;
            // A tap switches the mode when it ends, so a long press never does.
            now[(int)Action.ToolMode] = !b && bDown && !bLong;
            bDown = b;

            LogChanges();
        }

        private static bool Get(ref InputDevice device, XRNode node)
        {
            if (!device.isValid) device = InputDevices.GetDeviceAtXRNode(node);
            return device.isValid;
        }

        private static bool Button(InputDevice device, InputFeatureUsage<bool> usage) =>
            device.isValid && device.TryGetFeatureValue(usage, out var value) && value;

        private static void LogChanges()
        {
            if (logs >= 60) return;
            for (var i = 0; i < Count; i++)
            {
                if (now[i] == before[i]) continue;
                logs++;
                Plugin.Log.LogInfo($"Tool control: {(Action)i} {(now[i] ? "pressed" : "released")}");
            }
        }

        private static bool Virtual(LynxControls.ActionSetAndId actionSetAndId, out Action action)
        {
            if (!Actions.TryGetValue(actionSetAndId, out action)) return false;
            Update();
            return true;
        }

        public static bool GetInputWasPressed(LynxControls controls, LynxControls.ActionSetAndId actionSetAndId) =>
            controls.GetInputWasPressed(actionSetAndId) || (Virtual(actionSetAndId, out var a) && now[(int)a] && !before[(int)a]);

        public static bool GetInputIsPressed(LynxControls controls, LynxControls.ActionSetAndId actionSetAndId) =>
            controls.GetInputIsPressed(actionSetAndId) || (Virtual(actionSetAndId, out var a) && now[(int)a]);

        public static bool GetInputIsHolding(LynxControls controls, LynxControls.ActionSetAndId actionSetAndId) =>
            controls.GetInputIsHolding(actionSetAndId) || (Virtual(actionSetAndId, out var a) && now[(int)a] && before[(int)a]);

        public static bool GetInputWasReleased(LynxControls controls, LynxControls.ActionSetAndId actionSetAndId) =>
            controls.GetInputWasReleased(actionSetAndId) || (Virtual(actionSetAndId, out var a) && !now[(int)a] && before[(int)a]);
    }

    // Redirects the input polls in the tool classes to VrToolInput (see there).
    [HarmonyPatch]
    internal static class ToolInputPatch
    {
        private static readonly string[] Types =
        {
            "GrapplingHook", "TetherController", "BBI.Unity.Game.Tether", "BBI.Unity.Game.MagnetController",
            "BBI.Unity.Game.CuttingController", "BBI.Unity.Game.CuttingFXController", "BBI.Unity.Game.CuttingToolController",
            "BBI.Unity.Game.DemoChargeController", "BBI.Unity.Game.Scanner", "BBI.Unity.Game.InteractionController",
            "BBI.Unity.Game.ThrowController", "BBI.Unity.Game.HandGrab", "BBI.Unity.Game.GrabController",
        };

        private static readonly Dictionary<MethodInfo, MethodInfo> Replacements = new[]
        {
            nameof(LynxControlExtensions.GetInputWasPressed), nameof(LynxControlExtensions.GetInputIsPressed),
            nameof(LynxControlExtensions.GetInputIsHolding), nameof(LynxControlExtensions.GetInputWasReleased),
        }.ToDictionary(
            name => AccessTools.Method(typeof(LynxControlExtensions), name, new[] { typeof(LynxControls), typeof(LynxControls.ActionSetAndId) }),
            name => AccessTools.Method(typeof(VrToolInput), name));

        private static IEnumerable<MethodBase> TargetMethods()
        {
            foreach (var name in Types)
            {
                var type = AccessTools.TypeByName(name);
                if (type == null)
                {
                    Plugin.Log.LogWarning($"Tool control: type {name} not found");
                    continue;
                }
                foreach (var nested in new[] { type }.Concat(type.GetNestedTypes(AccessTools.all)))
                    foreach (var method in AccessTools.GetDeclaredMethods(nested))
                        if (!method.IsAbstract && !method.ContainsGenericParameters && method.GetMethodBody() != null && PollsInput(method))
                            yield return method;
            }
        }

        private static bool PollsInput(MethodBase method)
        {
            try
            {
                return PatchProcessor.ReadMethodBody(method).Any(i => i.Value is MethodInfo called && Replacements.ContainsKey(called));
            }
            catch (System.Exception e)
            {
                Plugin.Log.LogWarning($"Tool control: could not read {method.DeclaringType?.Name}.{method.Name}: {e.Message}");
                return false;
            }
        }

        private static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> instructions, MethodBase original)
        {
            var replaced = 0;
            foreach (var instruction in instructions)
            {
                if (instruction.operand is MethodInfo called && Replacements.TryGetValue(called, out var replacement))
                {
                    instruction.operand = replacement;
                    replaced++;
                }
                yield return instruction;
            }
            Plugin.Log.LogInfo($"Tool control: {original.DeclaringType?.Name}.{original.Name}: {replaced} input poll(s) redirected");
        }
    }
}
