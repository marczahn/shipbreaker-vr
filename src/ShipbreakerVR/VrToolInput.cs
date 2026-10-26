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
    //   grip at the body  draw a tool (VrToolBelt): right hip grapple, left hip demo charges, over the right shoulder
    //                   cutter, right side of the helmet scanner; the tool stays in the hand while the grip is held and
    //                   goes back to the belt when it is let go
    //   trigger         the tool's main action: cut, grapple (hold to keep hold), place or detonate a demo charge
    //   A               with the trigger held: throw what the grapple holds (hold A to charge, release to throw);
    //                   otherwise interact (doors, switches, containers)
    //   B               with the trigger held: reel in what the grapple holds, while B is held
    //   B tap           tool mode (cutter Stinger/Splitsaw, grapple/magnet, demo charge mode)
    //   B held          place a tether from where the hand points when the hold starts to where it points on release;
    //                   held while pointing at nothing: recall all tethers
    // A + X (pause) and B + Y (center the view) are combos of their own; A and B count only without X and Y.
    // The game polls its actions through LynxControlExtensions; those methods are small enough for Mono to inline, so the
    // calls in the tool classes are redirected (ToolInputPatch) to versions that add these presses to the real ones.
    internal static class VrToolInput
    {
        private enum Action { Fire, Throw, Interact, ToolMode, Tether, Recall, Retract, Grapple, Cutter, DemoCharge, Scanner, Holster }

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
            { GameplayActions.GameplayActionSet.SelectGrapple, Action.Grapple },
            { GameplayActions.GameplayActionSet.SelectCutter, Action.Cutter },
            { GameplayActions.GameplayActionSet.SelectDemoCharge, Action.DemoCharge },
            { GameplayActions.GameplayActionSet.ActivateScanner, Action.Scanner },
            { GameplayActions.GameplayActionSet.UnequipCurrentEquipment, Action.Holster },
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
        // B decides at its press whether it reels in (trigger held) or works as tap/hold, and keeps that until released.
        private static bool bReels;
        private static bool gripDown;
        private static int holsterPulses;
        private static EquipmentController.Equipment lastEquipment;
        private static EquipmentController equipment;
        private static float nextSearch;
        private static int logs;
        private static readonly MethodInfo SetEquipment = AccessTools.Method(typeof(EquipmentController), "SetEquipment");
        // Pulses of the unequip key before giving up, if the game keeps the tool (it may refuse while a tool is busy).
        private const int MaxHolsterPulses = 10;

        private static void Update()
        {
            if (updatedFrame == Time.frameCount) return;
            updatedFrame = Time.frameCount;
            System.Array.Copy(now, before, Count);
            System.Array.Clear(now, 0, Count);
            if (!VrAim.Active || !Get(ref right, XRNode.RightHand))
            {
                trigger = aDown = bDown = gripDown = false;
                VrToolBelt.Reset();
                return;
            }
            Get(ref left, XRNode.LeftHand);

            right.TryGetFeatureValue(CommonUsages.trigger, out var triggerValue);
            trigger = triggerValue >= (trigger ? TriggerRelease : TriggerPress);
            now[(int)Action.Fire] = trigger;
            right.TryGetFeatureValue(CommonUsages.grip, out var grip);
            var zone = VrToolBelt.Update(right);
            var gripPressed = grip >= GripPress && !gripDown;
            gripDown = grip >= GripPress;
            if (gripPressed)
            {
                holsterPulses = 0;
                if (!trigger && zone != VrToolBelt.Zone.None) Draw(zone);
            }
            else if (!gripDown) Holster();

            var a = Button(right, CommonUsages.primaryButton) && !Button(left, CommonUsages.primaryButton);
            if (a && !aDown) aThrows = trigger;
            aDown = a;
            now[(int)(aThrows ? Action.Throw : Action.Interact)] = a;

            var b = Button(right, CommonUsages.secondaryButton) && !Button(left, CommonUsages.secondaryButton);
            if (b && !bDown)
            {
                bDownAt = Time.unscaledTime;
                bLong = bTether = false;
                bReels = trigger;
            }
            now[(int)Action.Retract] = b && bReels;
            if (bReels)
            {
                bDown = b;
                LogChanges();
                return;
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

        // Grip pressed at a zone of the tool belt: draws that tool, or puts it away if it is still the one in the hand.
        // Through the game's own selection keys (EquipmentController.Update), which check whether the tool may change.
        private static void Draw(VrToolBelt.Zone zone)
        {
            if (!FindEquipment()) return;
            var tool = zone == VrToolBelt.Zone.Grapple ? EquipmentController.Equipment.GrappleHook
                : zone == VrToolBelt.Zone.Cutter ? EquipmentController.Equipment.CuttingTool
                : zone == VrToolBelt.Zone.DemoCharge ? EquipmentController.Equipment.DemoCharge
                : EquipmentController.Equipment.Scanner;
            if (!equipment.CanEquip(tool) || equipment.IsEquipmentBlocked(tool)) return;
            var holster = tool != EquipmentController.Equipment.Scanner && !equipment.IsNothingEquipped && equipment.CurrentEquipment == tool;
            now[(int)(holster ? Action.Holster
                : zone == VrToolBelt.Zone.Grapple ? Action.Grapple
                : zone == VrToolBelt.Zone.Cutter ? Action.Cutter
                : zone == VrToolBelt.Zone.DemoCharge ? Action.DemoCharge
                : Action.Scanner)] = true;
        }

        // Grip let go: the hand stays empty, as Marc wanted, whatever put the tool there (the belt, or the game at the
        // start of a shift). Through the unequip key, which the game ignores for the scanner; that one switches off
        // through EquipmentController.SetEquipment(None), the way the game ends a scan without a previous tool.
        private static void Holster()
        {
            if (!FindEquipment() || equipment.IsNothingEquipped)
            {
                holsterPulses = 0;
                return;
            }
            var current = equipment.CurrentEquipment;
            if (current != lastEquipment) holsterPulses = 0;
            lastEquipment = current;
            if (holsterPulses >= MaxHolsterPulses || before[(int)Action.Holster] || !equipment.CanUnequipCurrentEquipment()) return;
            holsterPulses++;
            if (current == EquipmentController.Equipment.Scanner)
            {
                if (logs++ < 60) Plugin.Log.LogInfo("Tool control: scanner put away");
                SetEquipment.Invoke(equipment, new object[] { EquipmentController.Equipment.None });
            }
            else now[(int)Action.Holster] = true;
        }

        private static bool FindEquipment()
        {
            if (equipment == null && Time.unscaledTime >= nextSearch)
            {
                nextSearch = Time.unscaledTime + 1f;
                equipment = Object.FindObjectOfType<EquipmentController>();
            }
            return equipment != null;
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
            "BBI.Unity.Game.ScalpelController", "BBI.Unity.Game.ScalpelFXController",
            "BBI.Unity.Game.DemoChargeController", "BBI.Unity.Game.Scanner", "BBI.Unity.Game.InteractionController",
            "BBI.Unity.Game.ThrowController", "BBI.Unity.Game.HandGrab", "BBI.Unity.Game.GrabController",
            "BBI.Unity.Game.EquipmentController",
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
