using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using BBI.Unity.Game;
using HarmonyLib;
using UnityEngine;
using UnityEngine.XR;

namespace ShipbreakerVR
{
    // The controller of the hand that holds the tool works it while the tools aim with that hand (VrAim), all on one hand,
    // as Marc asked. That is the right hand, unless the left one drew the tool; the left controller then works it like the
    // right one, with X for A and Y for B, and flying rests until it lets go (VrControls). The right stick stays with
    // turning. A hand that draws a tool, or takes the one the other hand holds, holds it from then on. The cutter and the
    // grapple can be held one in each hand (VrDualTools): each hand's buttons then work its own tool.
    //   grip at the body  draw a tool (VrToolBelt): right hip grapple, left hip demo charges, over the right shoulder
    //                   cutter, right side of the helmet scanner; the tool stays in the hand while the grip is held and
    //                   goes back to the belt when it is let go. Either hand can draw; reaching for the tool the other
    //                   hand holds takes it over.
    //   trigger         the tool's main action: cut, grapple (hold to keep hold), place or detonate a demo charge
    //   A / X           with the trigger held: throw what the grapple holds (hold to charge, release to throw);
    //                   otherwise interact (doors, switches, containers)
    //   B / Y           with the trigger held: reel in what the grapple holds, while the button is held
    //   B / Y tap       tool mode (cutter Stinger/Splitsaw, grapple/magnet, demo charge mode)
    //   B / Y held      place a tether from where the hand points when the hold starts to where it points on release;
    //                   held while pointing at nothing: recall all tethers
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
        // Drawing and putting away (the select actions and Holster); the tool actions are per hand (Hand.Now).
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

        // One controller: its trigger (with hysteresis), its grip and the belt zone it is in this frame, and the tool
        // actions its buttons press for the tool it holds.
        private class Hand
        {
            public readonly XRNode Node;
            public InputDevice Device;
            public bool Trigger;
            public bool GripDown;
            public bool GripPressed;
            public VrToolBelt.Zone Zone;
            public readonly bool[] Now = new bool[Count];
            public readonly bool[] Before = new bool[Count];
            // A decides at its press whether it throws (trigger held) or interacts, and keeps that until released.
            public bool ADown;
            public bool AThrows;
            public bool BDown;
            public float BDownAt;
            public bool BLong;
            public bool BTether;
            // B decides at its press whether it reels in (trigger held) or works as tap/hold, and keeps that until released.
            public bool BReels;

            public Hand(XRNode node) => Node = node;

            public string Name => Node == XRNode.LeftHand ? "left" : "right";
        }

        private static readonly Hand LeftHand = new Hand(XRNode.LeftHand);
        private static readonly Hand RightHand = new Hand(XRNode.RightHand);

        // The hand that holds the tool: the right one unless the left one drew it or took it over; back to the right once
        // the tool is put away.
        public static XRNode ToolNode { get; private set; } = XRNode.RightHand;

        // While the left hand holds a tool its controller works the tool, so it does not fly (VrControls).
        public static bool LeftHoldsTool => VrAim.Active && (ToolNode == XRNode.LeftHand
            || (VrDualTools.Second != EquipmentController.Equipment.None && VrDualTools.SecondNode == XRNode.LeftHand));

        // Whether that controller's grip is held this frame (VrToolModels: a right hand that takes the demo charges over
        // from the left one holds its grip).
        public static bool GripHeld(XRNode node)
        {
            Update();
            return (node == XRNode.LeftHand ? LeftHand : RightHand).GripDown;
        }

        // Whether that hand is free to hold on (VrClimb): not at the belt, where its grip draws a tool, holding no tool,
        // and not fitting one (VrToolFit). Without aiming with the hand the grips draw nothing, so both are free.
        public static bool Free(XRNode node)
        {
            Update();
            if (!VrAim.Active) return true;
            var hand = node == XRNode.LeftHand ? LeftHand : RightHand;
            if (hand.Zone != VrToolBelt.Zone.None || VrToolFit.Blocks(node)) return false;
            if (VrDualTools.Second != EquipmentController.Equipment.None && VrDualTools.SecondNode == node) return false;
            if (VrDualTools.Suspended != EquipmentController.Equipment.None && VrDualTools.SuspendedNode == node) return false;
            return node != ToolNode || !FindEquipment() || equipment.IsNothingEquipped;
        }

        private static int updatedFrame = -1;
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
            foreach (var each in new[] { LeftHand, RightHand })
            {
                System.Array.Copy(each.Now, each.Before, Count);
                System.Array.Clear(each.Now, 0, Count);
            }
            // A left controller that is gone (switched off) holds nothing.
            if (ToolNode == XRNode.LeftHand && !Get(LeftHand)) ToolNode = XRNode.RightHand;
            VrDualTools.Check();
            var active = VrAim.Active;
            Read(LeftHand, active);
            Read(RightHand, active);
            if (!active)
            {
                LeftHand.ADown = LeftHand.BDown = RightHand.ADown = RightHand.BDown = false;
                VrToolBelt.Reset();
                return;
            }

            // Grip pressed at the belt draws that tool into the hand, or takes it over from the other hand. While the tools
            // are being fitted the other hand's grip moves the tool instead (VrToolFit).
            if (RightHand.GripPressed && !RightHand.Trigger && RightHand.Zone != VrToolBelt.Zone.None && !VrToolFit.Blocks(RightHand.Node)) Draw(RightHand);
            if (LeftHand.GripPressed && !LeftHand.Trigger && LeftHand.Zone != VrToolBelt.Zone.None && !VrToolFit.Blocks(LeftHand.Node)) Draw(LeftHand);
            // The second hand letting go puts its tool away; the first one hands the game the second tool, if there is one.
            var second = VrDualTools.Second != EquipmentController.Equipment.None ? (VrDualTools.SecondNode == XRNode.LeftHand ? LeftHand : RightHand) : null;
            if (second != null && !second.GripDown) VrDualTools.PutAway();
            if (VrDualTools.Suspended != EquipmentController.Equipment.None && !(VrDualTools.SuspendedNode == XRNode.LeftHand ? LeftHand : RightHand).GripDown)
                VrDualTools.Forget();
            var tool = ToolNode == XRNode.LeftHand ? LeftHand : RightHand;
            if (tool.GripPressed) holsterPulses = 0;
            else if (!tool.GripDown) Holster();

            second = VrDualTools.Second != EquipmentController.Equipment.None ? (VrDualTools.SecondNode == XRNode.LeftHand ? LeftHand : RightHand) : null;
            tool = ToolNode == XRNode.LeftHand ? LeftHand : RightHand;
            foreach (var each in new[] { LeftHand, RightHand })
            {
                if (each == tool || each == second) Buttons(each);
                else each.ADown = each.BDown = false;
            }
            LogChanges();
        }

        // The tool actions of one hand's buttons, for the tool it holds.
        private static void Buttons(Hand hand)
        {
            var trigger = hand.Trigger;
            var now = hand.Now;
            now[(int)Action.Fire] = trigger;

            var a = Button(hand.Device, CommonUsages.primaryButton);
            if (a && !hand.ADown) hand.AThrows = trigger;
            hand.ADown = a;
            now[(int)(hand.AThrows ? Action.Throw : Action.Interact)] = a;

            var b = Button(hand.Device, CommonUsages.secondaryButton);
            if (b && !hand.BDown)
            {
                hand.BDownAt = Time.unscaledTime;
                hand.BLong = hand.BTether = false;
                hand.BReels = trigger;
            }
            now[(int)Action.Retract] = b && hand.BReels;
            if (hand.BReels)
            {
                hand.BDown = b;
                return;
            }
            if (b && !hand.BLong && Time.unscaledTime - hand.BDownAt >= LongPress)
            {
                hand.BLong = true;
                hand.BTether = VrAim.PointsAtSomething(VrDualTools.Second != EquipmentController.Equipment.None && hand.Node == VrDualTools.SecondNode);
                // Recalling is a single press.
                now[(int)Action.Recall] = !hand.BTether;
            }
            now[(int)Action.Tether] = b && hand.BTether;
            // A tap switches the mode when it ends, so a long press never does.
            now[(int)Action.ToolMode] = !b && hand.BDown && !hand.BLong;
            hand.BDown = b;
        }

        // The hand's trigger and grip this frame, and the belt zone it is in. The grip is followed also while the tools do
        // not aim from the hand, so a grip held through such a moment (tracking lost) does not draw again afterwards.
        private static void Read(Hand hand, bool active)
        {
            hand.GripPressed = false;
            hand.Zone = VrToolBelt.Zone.None;
            if (!Get(hand))
            {
                hand.Trigger = hand.GripDown = false;
                return;
            }
            hand.Device.TryGetFeatureValue(CommonUsages.trigger, out var trigger);
            hand.Trigger = trigger >= (hand.Trigger ? TriggerRelease : TriggerPress);
            hand.Device.TryGetFeatureValue(CommonUsages.grip, out var grip);
            var down = grip >= GripPress;
            hand.GripPressed = active && down && !hand.GripDown;
            hand.GripDown = down;
            // No buzz while the trigger is held: the grip does not draw then (flying with the left hand, or firing).
            if (active) hand.Zone = VrToolBelt.Update(hand.Node, hand.Device, hand.Trigger);
        }

        // Grip pressed at a zone of the tool belt: that tool goes into this hand. Through the game's own selection keys
        // (EquipmentController.Update), which check whether the tool may change; a tool that is already out (in the other
        // hand) only changes hands. With the cutter or the grapple in the other hand, the other one of the two joins it
        // in this hand (VrDualTools).
        private static void Draw(Hand hand)
        {
            if (!FindEquipment()) return;
            var zone = hand.Zone;
            var tool = zone == VrToolBelt.Zone.Grapple ? EquipmentController.Equipment.GrappleHook
                : zone == VrToolBelt.Zone.Cutter ? EquipmentController.Equipment.CuttingTool
                : zone == VrToolBelt.Zone.DemoCharge ? EquipmentController.Equipment.DemoCharge
                : EquipmentController.Equipment.Scanner;
            if (!equipment.CanEquip(tool) || equipment.IsEquipmentBlocked(tool)) return;
            if (VrDualTools.Second == tool)
            {
                VrDualTools.TakeOver(hand.Node);
                return;
            }
            var current = equipment.IsNothingEquipped ? EquipmentController.Equipment.None : equipment.CurrentEquipment;
            var other = hand == LeftHand ? RightHand : LeftHand;
            var otherHolds = current != EquipmentController.Equipment.None && other.Node == ToolNode && other.GripDown;
            if (VrDualTools.Second == EquipmentController.Equipment.None && otherHolds && VrDualTools.CanPair(current, tool))
            {
                // The demo charges stay the game's tool, the other tools join them.
                if (current == EquipmentController.Equipment.DemoCharge) VrDualTools.Add(equipment, tool, hand.Node);
                else
                {
                    VrDualTools.Draw(equipment, current, other.Node, tool);
                    ToolNode = hand.Node;
                }
                holsterPulses = 0;
                return;
            }
            // The scanner hides the other hand's tool until it is put away.
            if (tool == EquipmentController.Equipment.Scanner && otherHolds && VrDualTools.Second == EquipmentController.Equipment.None)
                VrDualTools.Suspend(current, other.Node);
            var taken = current == tool;
            if (!taken)
                now[(int)(zone == VrToolBelt.Zone.Grapple ? Action.Grapple
                    : zone == VrToolBelt.Zone.Cutter ? Action.Cutter
                    : zone == VrToolBelt.Zone.DemoCharge ? Action.DemoCharge
                    : Action.Scanner)] = true;
            if (logs++ < 60) Plugin.Log.LogInfo($"Tool control: {hand.Name} hand {(taken ? "takes" : "draws")} the {tool}");
            ToolNode = hand.Node;
            holsterPulses = 0;
        }

        // Grip let go by the hand that holds the tool: the hand stays empty, as Marc wanted, whatever put the tool there
        // (the belt, or the game at the start of a shift). Through the unequip key, which the game ignores for the scanner;
        // that one switches off through EquipmentController.SetEquipment(None), the way the game ends a scan without a
        // previous tool. Once the hand is empty the right hand is the tool hand again.
        private static void Holster()
        {
            if (!FindEquipment() || equipment.IsNothingEquipped)
            {
                holsterPulses = 0;
                ToolNode = XRNode.RightHand;
                return;
            }
            var current = equipment.CurrentEquipment;
            if (current != lastEquipment) holsterPulses = 0;
            lastEquipment = current;
            if (holsterPulses >= MaxHolsterPulses || before[(int)Action.Holster] || !equipment.CanUnequipCurrentEquipment()) return;
            // The other hand still holds the second tool: it becomes the game's tool, and that hand the tool hand.
            if (VrDualTools.Second != EquipmentController.Equipment.None)
            {
                var node = VrDualTools.SecondNode;
                VrDualTools.Promote();
                ToolNode = node;
                holsterPulses = 0;
                return;
            }
            holsterPulses++;
            if (current == EquipmentController.Equipment.Scanner)
            {
                // The tool the scanner hid comes back into the hand that still holds it.
                var node = VrDualTools.SuspendedNode;
                if (VrDualTools.Resume(equipment))
                {
                    ToolNode = node;
                    holsterPulses = 0;
                    return;
                }
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

        private static bool Get(Hand hand)
        {
            if (!hand.Device.isValid) hand.Device = InputDevices.GetDeviceAtXRNode(hand.Node);
            return hand.Device.isValid;
        }

        private static bool Button(InputDevice device, InputFeatureUsage<bool> usage) =>
            device.isValid && device.TryGetFeatureValue(usage, out var value) && value;

        private static void LogChanges()
        {
            if (logs >= 60) return;
            Log(now, before, "");
            Log(LeftHand.Now, LeftHand.Before, " (left)");
            Log(RightHand.Now, RightHand.Before, " (right)");
        }

        private static void Log(bool[] current, bool[] previous, string hand)
        {
            for (var i = 0; i < Count; i++)
            {
                if (current[i] == previous[i]) continue;
                logs++;
                Plugin.Log.LogInfo($"Tool control: {(Action)i} {(current[i] ? "pressed" : "released")}{hand}");
            }
        }

        // The presses for that action as the code of a tool family sees them (VrDualTools.Family): drawing and putting
        // away from both hands, the tool actions from the second hand for the second tool's family, from the tool hand
        // otherwise.
        private static bool Virtual(LynxControls.ActionSetAndId actionSetAndId, int family, out bool[] current, out bool[] previous, out int index)
        {
            current = previous = null;
            index = 0;
            if (!Actions.TryGetValue(actionSetAndId, out var action)) return false;
            Update();
            index = (int)action;
            if (action >= Action.Grapple)
            {
                current = now;
                previous = before;
                return true;
            }
            var node = VrDualTools.Routes(family) ? VrDualTools.SecondNode : ToolNode;
            var hand = node == XRNode.LeftHand ? LeftHand : RightHand;
            current = hand.Now;
            previous = hand.Before;
            return true;
        }

        public static bool GetInputWasPressed(LynxControls controls, LynxControls.ActionSetAndId actionSetAndId, int family) =>
            controls.GetInputWasPressed(actionSetAndId) || (Virtual(actionSetAndId, family, out var n, out var b, out var i) && n[i] && !b[i]);

        public static bool GetInputIsPressed(LynxControls controls, LynxControls.ActionSetAndId actionSetAndId, int family) =>
            controls.GetInputIsPressed(actionSetAndId) || (Virtual(actionSetAndId, family, out var n, out _, out var i) && n[i]);

        public static bool GetInputIsHolding(LynxControls controls, LynxControls.ActionSetAndId actionSetAndId, int family) =>
            controls.GetInputIsHolding(actionSetAndId) || (Virtual(actionSetAndId, family, out var n, out var b, out var i) && n[i] && b[i]);

        public static bool GetInputWasReleased(LynxControls controls, LynxControls.ActionSetAndId actionSetAndId, int family) =>
            controls.GetInputWasReleased(actionSetAndId) || (Virtual(actionSetAndId, family, out var n, out var b, out var i) && !n[i] && b[i]);
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
                    // The replacements take the family of the code polling (VrDualTools) as a third argument.
                    foreach (var load in VrDualTools.LoadFamily(instruction, original)) yield return load;
                    instruction.operand = replacement;
                    replaced++;
                }
                yield return instruction;
            }
            Plugin.Log.LogInfo($"Tool control: {original.DeclaringType?.Name}.{original.Name}: {replaced} input poll(s) redirected");
        }
    }
}
