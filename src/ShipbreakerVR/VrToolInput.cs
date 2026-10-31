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
    //                   otherwise interact (doors, switches, containers, airlock consoles), from either hand, also
    //                   one without a tool; the grip never does (it holds on, VrClimb)
    //   A / X on the cutter  switch the Splitsaw between horizontal and vertical cuts (CutterAltFire, the right mouse
    //                   button in the flat game) instead; the other hand's A or X still interacts
    //   B / Y           with the trigger held: reel in what the grapple holds, while the button is held
    //   B / Y tap       tool mode (cutter Stinger/Splitsaw, demo charge mode)
    //   B / Y on the grapple  grappling or placing tethers (VrTetherMode), however long it is held
    //   B / Y held      (not on the grapple) place a tether from where the hand points when the hold starts to where it
    //                   points on release; held while pointing at nothing: recall all tethers
    //   in the grapple's tether mode:
    //   trigger         place a tether, from where the hand points at the press to where it points at the release
    //   A / X tap       let go of the tether the hand points at (otherwise interact)
    //   A / X held      recall all tethers
    //   B / Y           back to grappling, however long it is held
    // The game polls its actions through LynxControlExtensions; those methods are small enough for Mono to inline, so the
    // calls in the tool classes are redirected (ToolInputPatch) to versions that add these presses to the real ones.
    internal static class VrToolInput
    {
        private enum Action { Fire, Throw, Interact, Angle, ToolMode, Tether, Recall, Retract, Grapple, Cutter, DemoCharge, Scanner, Holster }

        private const float LongPress = 0.4f;

        private static readonly AccessTools.FieldRef<InputHandlerGameplay, GameplayActions.GameplayActionSet> HandlerAction =
            AccessTools.FieldRefAccess<InputHandlerGameplay, GameplayActions.GameplayActionSet>("m_Action");
        private static readonly AccessTools.FieldRef<InputHandlerGameplay, UnityEngine.Events.UnityEvent> HandlerEvent =
            AccessTools.FieldRefAccess<InputHandlerGameplay, UnityEngine.Events.UnityEvent>("m_OnAction");

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
            { GameplayActions.GameplayActionSet.CutterAltFire, Action.Angle },
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

        // One controller (VrInput) and what it does (VrHand): its grip pressed for drawing, and the tool actions its
        // buttons press for the tool it holds.
        private class Hand
        {
            public readonly VrHand Vr;
            public readonly VrInput.Hand Input;
            public bool GripPressed;
            public readonly bool[] Now = new bool[Count];
            public readonly bool[] Before = new bool[Count];
            // A decides at its press whether it throws (trigger held) or interacts, and keeps that until released.
            public bool ADown;
            public bool AThrows;
            // In the grapple's tether mode A works the tethers (decided at its press): the tether it points at, let go of
            // when a tap ends, and whether it was held long enough to recall them all.
            public bool ATethers;
            public Tether ATether;
            public float ADownAt;
            public bool ALong;
            public bool BDown;
            public float BDownAt;
            public bool BLong;
            public bool BTether;
            // B decides at its press whether it reels in (trigger held) or works as tap/hold, and keeps that until released.
            public bool BReels;
            // The trigger decides at its press whether it places a tether (the grapple's tether mode) and keeps that until
            // released, so switching the mode never turns a held grapple into a tether or back.
            public bool TriggerDown;
            public bool TriggerTethers;
            // Whether that press pointed at something; one that pointed at nothing places no tether.
            public bool TriggerAnchored;

            public Hand(VrHand vr)
            {
                Vr = vr;
                Input = vr.Input;
            }

            public XRNode Node => Input.Node;
            public VrToolBelt.Zone Zone => Vr.Zone;
            public string Name => Input.Name;
            public bool Trigger => Input.Trigger.Down;
            public bool GripDown => Input.Grip.Down;
        }

        private static readonly Hand LeftHand = new Hand(VrHand.Left);
        private static readonly Hand RightHand = new Hand(VrHand.Right);
        private static readonly Hand[] Hands = { LeftHand, RightHand };

        private static Hand Of(VrHand hand) => hand == null ? null : hand.IsLeft ? LeftHand : RightHand;

        private static int updatedFrame = -1;
        private static int holsterPulses;
        private static EquipmentController.Equipment lastEquipment;
        private static EquipmentController equipment;
        private static int logs;
        private static readonly MethodInfo SetEquipment = AccessTools.Method(typeof(EquipmentController), "SetEquipment");
        // Pulses of the unequip key before giving up, if the game keeps the tool (it may refuse while a tool is busy).
        private const int MaxHolsterPulses = 10;

        // From VrFrame, once per frame, after VrInput and before VrAim places the aim camera on the tool hand.
        public static void Update()
        {
            if (updatedFrame == Time.frameCount) return;
            updatedFrame = Time.frameCount;
            System.Array.Copy(now, before, Count);
            System.Array.Clear(now, 0, Count);
            foreach (var each in Hands)
            {
                System.Array.Copy(each.Now, each.Before, Count);
                System.Array.Clear(each.Now, 0, Count);
            }
            VrDualTools.Check();
            VrTetherMode.Update();
            var active = VrHand.Update(FindEquipment() && !equipment.IsNothingEquipped ? equipment.CurrentEquipment : EquipmentController.Equipment.None);
            Read(LeftHand, active);
            Read(RightHand, active);
            if (!active)
            {
                LeftHand.ADown = LeftHand.BDown = RightHand.ADown = RightHand.BDown = false;
                LeftHand.TriggerDown = RightHand.TriggerDown = false;
                VrTetherMode.End(XRNode.LeftHand);
                VrTetherMode.End(XRNode.RightHand);
                VrToolBelt.Reset();
                return;
            }

            // Grip pressed at the belt draws that tool into the hand, or takes it over from the other hand. While the tools
            // are being fitted the other hand's grip moves the tool instead (VrToolFit).
            if (RightHand.GripPressed && !RightHand.Trigger && RightHand.Zone != VrToolBelt.Zone.None && !VrToolFit.Blocks(RightHand.Node)) Draw(RightHand);
            if (LeftHand.GripPressed && !LeftHand.Trigger && LeftHand.Zone != VrToolBelt.Zone.None && !VrToolFit.Blocks(LeftHand.Node)) Draw(LeftHand);
            // The second hand letting go puts its tool away; the first one hands the game the second tool, if there is one.
            var second = Of(VrHand.SecondHand);
            if (second != null && !second.GripDown) VrDualTools.PutAway();
            var hidden = Of(VrHand.HiddenHand);
            if (hidden != null && !hidden.GripDown) VrDualTools.Forget();
            var tool = Of(VrHand.GameToolHand);
            if (tool.GripPressed) holsterPulses = 0;
            else if (!tool.GripDown) Holster();

            second = Of(VrHand.SecondHand);
            tool = Of(VrHand.GameToolHand);
            // An empty tool hand only interacts: the demo charges keep listening for their fire action after being put
            // away, and a trigger pulled with the empty hand took them out behind the game's back, leaving their red
            // placement marker stuck in the view (DemoChargeController.EquipDemoCharge).
            var empty = !FindEquipment() || equipment.IsNothingEquipped;
            foreach (var each in Hands)
            {
                if (each == second) Buttons(each, VrDualTools.Second);
                else if (each == tool && !empty) Buttons(each, equipment.CurrentEquipment);
                else if (each == tool) Interact(each);
                else Interact(each);
            }
            // Interacting reaches the game from either hand: the game's interaction code reads the tool hand's actions.
            foreach (var each in Hands)
                if (each != tool && each.Now[(int)Action.Interact]) tool.Now[(int)Action.Interact] = true;
            LogChanges();
        }

        // The tool actions of one hand's buttons, for the tool it holds.
        private static void Buttons(Hand hand, EquipmentController.Equipment held)
        {
            var tethers = VrTetherMode.Applies(held);
            if (hand.Trigger && !hand.TriggerDown)
            {
                hand.TriggerTethers = tethers;
                hand.TriggerAnchored = false;
                if (tethers && VrAim.PointsAt(VrDualTools.Holds(hand.Node), out var hit))
                {
                    hand.TriggerAnchored = true;
                    VrTetherMode.Begin(hand.Node, hit);
                }
            }
            if (!hand.Trigger || !hand.TriggerTethers) VrTetherMode.End(hand.Node);
            hand.TriggerDown = hand.Trigger;
            // A trigger that places a tether neither grapples nor makes A throw or B reel in.
            var trigger = hand.Trigger && !hand.TriggerTethers;
            var now = hand.Now;
            now[(int)Action.Fire] = trigger;

            var a = hand.Input.Primary.Down;
            if (a && !hand.ADown)
            {
                hand.AThrows = trigger;
                hand.ADownAt = Time.unscaledTime;
                hand.ALong = false;
                // In the tether mode A works the tethers: a tap lets go of the one the hand points at, held it recalls
                // them all. Pointing at a tether it neither throws nor interacts.
                hand.ATether = tethers && !trigger ? VrAim.TetherAt(VrHand.SecondHand == hand.Vr) : null;
                hand.ATethers = tethers && !trigger;
            }
            if (hand.ATethers)
            {
                if (a && !hand.ALong && Time.unscaledTime - hand.ADownAt >= LongPress)
                {
                    hand.ALong = true;
                    // Recalling is a single press.
                    now[(int)Action.Recall] = true;
                }
                if (!a && hand.ADown && !hand.ALong && hand.ATether != null) VrTetherMode.LetGo(hand.ATether);
            }
            if (!a) hand.ATether = null;
            hand.ADown = a;
            // On the cutter A switches the cut's angle; there is nothing to throw, and the other hand interacts.
            if (held == EquipmentController.Equipment.CuttingTool) now[(int)Action.Angle] = a;
            else if (!(hand.ATethers && (hand.ATether != null || hand.ALong))) now[(int)(hand.AThrows ? Action.Throw : Action.Interact)] = a;

            var b = hand.Input.Secondary.Down;
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
            // On the grapple B only switches between grappling and placing tethers, however long it is held: tethers are
            // placed and recalled in the tether mode, as Marc asked, so a slow tap never places or recalls one.
            var grapple = held == EquipmentController.Equipment.GrappleHook;
            if (b && !hand.BLong && !grapple && Time.unscaledTime - hand.BDownAt >= LongPress)
            {
                hand.BLong = true;
                hand.BTether = VrAim.PointsAtSomething(VrHand.SecondHand == hand.Vr);
                // Recalling is a single press.
                now[(int)Action.Recall] = !hand.BTether;
            }
            now[(int)Action.Tether] = (b && hand.BTether) || (hand.Trigger && hand.TriggerTethers && hand.TriggerAnchored);
            // A tap switches the mode when it ends, so a long press never does. The grapple switches the mod's own mode
            // (VrTetherMode), not while its trigger is held.
            var tap = !b && hand.BDown && !hand.BLong;
            if (grapple)
            {
                if (tap && !hand.Trigger) VrTetherMode.Toggle(hand.Input.Device);
            }
            else now[(int)Action.ToolMode] = tap;
            hand.BDown = b;
        }

        // A of an empty hand: interact (with the trigger held too, since there is nothing to throw).
        private static void Interact(Hand hand)
        {
            var a = hand.Input.Primary.Down;
            hand.AThrows = hand.ATethers = false;
            hand.ATether = null;
            hand.ADown = a;
            hand.BDown = hand.TriggerDown = false;
            VrTetherMode.End(hand.Node);
            hand.Now[(int)Action.Interact] = a;
        }

        // The grip pressed this frame, and the belt zone the hand is in (VrHand.Zone). VrInput follows the grip also while the tools do
        // not aim from the hand, so a grip held through such a moment (tracking lost) does not draw again afterwards.
        private static void Read(Hand hand, bool active)
        {
            hand.GripPressed = false;
            hand.Vr.Zone = VrToolBelt.Zone.None;
            if (!hand.Input.Valid) return;
            hand.GripPressed = active && hand.Input.Grip.Pressed;
            // No buzz while the trigger is held: the grip does not draw then (flying with the left hand, or firing).
            if (active) hand.Vr.Zone = VrToolBelt.Update(hand.Input, hand.Trigger);
        }

        // Grip pressed at a zone of the tool belt: that tool goes into this hand. Through the game's own selection keys
        // (EquipmentController.Update), which check whether the tool may change; a tool that is already out (in the other
        // hand) only changes hands. With the cutter or the grapple in the other hand, the other one of the two joins it
        // in this hand (VrDualTools).
        private static void Draw(Hand hand)
        {
            if (hand.Zone == VrToolBelt.Zone.Flashlight)
            {
                ToggleFlashlight(hand);
                return;
            }
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
            var otherHolds = current != EquipmentController.Equipment.None && other.Vr == VrHand.GameToolHand && other.GripDown;
            if (VrDualTools.Second == EquipmentController.Equipment.None && otherHolds && VrDualTools.CanPair(current, tool))
            {
                // The demo charges stay the game's tool, the other tools join them.
                if (current == EquipmentController.Equipment.DemoCharge) VrDualTools.Add(equipment, tool, hand.Node);
                else
                {
                    VrDualTools.Draw(equipment, current, other.Node, tool);
                    VrHand.TakeGameTool(hand.Vr, tool);
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
            VrHand.TakeGameTool(hand.Vr, tool);
            holsterPulses = 0;
        }

        // The flashlight has no code of its own to call: an InputHandlerGameplay on the player runs it when its action is
        // pressed. Pressing that action for the game would need a patch on every frame, so its event runs from here, and
        // only when the game does not block the action.
        private static void ToggleFlashlight(Hand hand)
        {
            var flashlight = GameplayActions.GameplayActionSet.ToggleFlashlight;
            if (ActionBlockerService.Instance.IsActionBlocked(flashlight)) return;
            var handlers = 0;
            foreach (var handler in Object.FindObjectsOfType<InputHandlerGameplay>())
            {
                if (HandlerAction(handler) != flashlight) continue;
                HandlerEvent(handler)?.Invoke();
                handlers++;
            }
            hand.Input.Buzz(0.5f, 0.06f);
            if (logs++ < 60) Plugin.Log.LogInfo($"Tool control: {hand.Name} hand switches the flashlight ({handlers} handler(s))");
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
                VrHand.ResetGameTool();
                return;
            }
            var current = equipment.CurrentEquipment;
            if (current != lastEquipment) holsterPulses = 0;
            lastEquipment = current;
            if (holsterPulses >= MaxHolsterPulses || before[(int)Action.Holster] || !equipment.CanUnequipCurrentEquipment()) return;
            // The other hand still holds the second tool: it becomes the game's tool, and that hand the tool hand.
            if (VrDualTools.Second != EquipmentController.Equipment.None)
            {
                VrDualTools.Promote();
                holsterPulses = 0;
                return;
            }
            holsterPulses++;
            if (current == EquipmentController.Equipment.Scanner)
            {
                // The tool the scanner hid comes back into the hand that still holds it.
                if (VrDualTools.Resume(equipment))
                {
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
            equipment = GameRefs.Equipment;
            return equipment != null;
        }

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
            index = (int)action;
            if (action >= Action.Grapple)
            {
                current = now;
                previous = before;
                return true;
            }
            var hand = Of(VrDualTools.Routes(family) ? VrHand.SecondHand : VrHand.GameToolHand);
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
        private static readonly Dictionary<MethodInfo, MethodInfo> Replacements = new[]
        {
            nameof(LynxControlExtensions.GetInputWasPressed), nameof(LynxControlExtensions.GetInputIsPressed),
            nameof(LynxControlExtensions.GetInputIsHolding), nameof(LynxControlExtensions.GetInputWasReleased),
        }.ToDictionary(
            name => AccessTools.Method(typeof(LynxControlExtensions), name, new[] { typeof(LynxControls), typeof(LynxControls.ActionSetAndId) }),
            name => AccessTools.Method(typeof(VrToolInput), name));

        private static IEnumerable<MethodBase> TargetMethods() =>
            GameTools.MethodsCalling(GameTools.Redirect.Input, "Tool control", Replacements.Keys);

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
            PatchReport.Count("Tool control", original, replaced, "input poll(s)");
        }
    }
}
