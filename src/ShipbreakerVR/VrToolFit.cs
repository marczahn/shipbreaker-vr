using System.Globalization;
using BBI.Unity.Game;
using BepInEx.Configuration;
using UnityEngine;
using UnityEngine.XR;

namespace ShipbreakerVR
{
    // Where the cutter and the grapple sit in each hand (VrToolModels.Place): the tool's equipment point relative to the
    // hand's wrist (the grip pose moved back by VrInput.WristBehindGrip, in the aim pose's axes: x right, y up,
    // z forward) in centimeters, and its turn as pitch, yaw and roll in degrees, one config entry per tool and hand
    // ("x y z / pitch yaw roll"). The tool turns with it, and so does the aim, which runs along the barrel. Measuring the
    // flat game's hold left the tools off in both hands, each differently, so they are set in the headset: with
    // "WerkzeugeJustieren" on, the grip of the other hand grabs the tool in the hand and moves it rigidly, and letting go
    // stores where it is (Adjust). The defaults are where Raik put them; the option stays for anyone whose hands differ.
    // Raik, 2026-11-18, to try: the same for the charges' detonator and for a demo charge held as a thing of its own
    // (Sprengsatzplatzierung = Manuell), whose hold had taken several rounds of builds to get right.
    // - The detonator is a prop on the hand: the other hand's grip drags it against the hand that holds it (Detonator,
    //   from VrHands), and letting go stores where it sits in that hand (ZuenderHaltung).
    // - The charge is handed from hand to hand until it lies right; when the other hand lets go, how it lies in the
    //   hand that keeps it is stored (SprengsatzHaltung, VrChargeByHand), and a charge comes into the hand like that.
    // Both are kept as in the right hand; the left hand's is the mirror image. Empty: as the mod has them.
    internal static class VrToolFit
    {
        // A hold kept in the config as "x y z / pitch yaw roll": the place in centimeters and the turn in degrees.
        internal sealed class Hold
        {
            public ConfigEntry<string> Entry;
            private string parsed;
            private bool set;
            private Vector3 at;
            private Quaternion turn = Quaternion.identity;

            // In meters at world scale 1; false while nothing is stored.
            public bool TryGet(out Vector3 place, out Quaternion turned)
            {
                place = Vector3.zero;
                turned = Quaternion.identity;
                if (Entry == null) return false;
                var text = Entry.Value;
                if (!ReferenceEquals(text, parsed))
                {
                    parsed = text;
                    set = TryParse(text, out var offset, out var angles);
                    if (set)
                    {
                        at = offset / 100f;
                        turn = Quaternion.Euler(angles);
                    }
                }
                place = at;
                turned = turn;
                return set;
            }

            public void Store(Vector3 place, Quaternion turned)
            {
                if (Entry == null) return;
                var angles = turned.eulerAngles;
                for (var i = 0; i < 3; i++)
                    if (angles[i] > 180f) angles[i] -= 360f;
                Entry.Value = string.Format(CultureInfo.InvariantCulture, "{0:0.0} {1:0.0} {2:0.0} / {3:0.0} {4:0.0} {5:0.0}",
                    place.x * 100f, place.y * 100f, place.z * 100f, angles.x, angles.y, angles.z);
                Plugin.Log.LogInfo($"Tool fit: {Entry.Definition.Key} = {Entry.Value}");
            }
        }

        public static readonly Hold ChargeHold = new Hold();
        public static readonly Hold DetonatorHold = new Hold();

        public static bool On => Adjusting != null && Adjusting.Value;

        // A pose in one hand's axes as the other hand has it.
        internal static void Mirror(ref Vector3 at, ref Quaternion turn)
        {
            at.x = -at.x;
            turn = new Quaternion(turn.x, -turn.y, -turn.z, turn.w);
        }

        // The other hand drags the detonator: the grip as last seen, and the detonator against the dragging hand.
        private static bool detonatorGripping, detonatorDragged;
        private static Vector3 detonatorGrabAt, dragAt;
        private static Quaternion detonatorGrabTurn, dragTurn;

        // From VrHands, each frame the detonator shows in a hand, with that hand's wrist and axes in the world and where
        // the detonator is against them (as in the right hand, in meters at world scale 1): the place it is fitted to
        // instead, dragged by the other hand's grip while adjusting. False if it stays where the game has it.
        public static bool Detonator(bool left, Vector3 wrist, Quaternion frame, ref Vector3 at, ref Quaternion turn)
        {
            var changed = false;
            if (detonatorDragged)
            {
                at = dragAt;
                turn = dragTurn;
                changed = true;
            }
            else if (DetonatorHold.TryGet(out var fitAt, out var fitTurn))
            {
                at = fitAt;
                turn = fitTurn;
                changed = true;
            }
            var other = left ? VrInput.Right : VrInput.Left;
            var down = On && other.Grip.Down;
            var pressed = down && !detonatorGripping;
            detonatorGripping = down;
            var hand = Vector3.zero;
            var handTurn = Quaternion.identity;
            var held = down && other.TryGetGripPose(out var handLocal, out var handTurnLocal)
                && VrToolModels.TrackingToWorld(handLocal, handTurnLocal, out hand, out handTurn);
            if (!held)
            {
                if (!down) DetonatorIdle();
                return changed;
            }
            var scale = VrCamera.WorldScale;
            if (pressed)
            {
                if (!NearTool(other.Node)) return changed;
                var nowAt = at;
                var nowTurn = turn;
                if (left) Mirror(ref nowAt, ref nowTurn);
                detonatorGrabTurn = Quaternion.Inverse(handTurn) * (frame * nowTurn);
                detonatorGrabAt = Quaternion.Inverse(handTurn) * (wrist + frame * (nowAt * scale) - hand);
                detonatorDragged = true;
                dragAt = at;
                dragTurn = turn;
                Plugin.Log.LogInfo($"Tool fit: the detonator grabbed with the {other.Name} hand");
                return changed;
            }
            if (!detonatorDragged) return changed;
            var draggedAt = Quaternion.Inverse(frame) * (hand + handTurn * detonatorGrabAt - wrist) / scale;
            var draggedTurn = Quaternion.Inverse(frame) * (handTurn * detonatorGrabTurn);
            if (left) Mirror(ref draggedAt, ref draggedTurn);
            dragAt = at = draggedAt;
            dragTurn = turn = draggedTurn;
            return true;
        }

        // The detonator is not in a hand, or the dragging grip opened: what was dragged is stored.
        public static void DetonatorIdle()
        {
            detonatorGripping = false;
            if (!detonatorDragged) return;
            detonatorDragged = false;
            DetonatorHold.Store(dragAt, dragTurn);
        }

        // The demo charge in a hand, with either way of placing (Raik, 2026-11-19: while fitting, the other hand took the
        // charge over or held it with both hands; fitting is to come first). As the detonator: the other hand's grip near
        // it drags the charge against the hand that holds it, and letting go stores how it lies (SprengsatzHaltung).
        // Placed the game's way the charge in the hand is drawn from that hold (VrChargeByHand.HoldInTracking); placed by
        // hand, the hand's hold on the charge afloat is set to it (VrClimb.Refit).
        private static VrInput.Hand chargeHolder;
        private static bool chargeGripping, chargeDragged;
        private static Vector3 chargeGrabAt, chargeDragAt;
        private static Quaternion chargeGrabTurn, chargeDragTurn;

        public static bool ChargeDragged => chargeDragged;

        // The hold being dragged, as in the right hand; false while none is.
        public static bool ChargeDrag(out Vector3 at, out Quaternion turn)
        {
            at = chargeDragAt;
            turn = chargeDragTurn;
            return chargeDragged;
        }

        // From VrChargeByHand once a frame, with the hand that holds a charge (null for none).
        public static void Charge(VrInput.Hand holder)
        {
            if (holder != chargeHolder) ChargeIdle();
            chargeHolder = holder;
            if (holder == null || !On)
            {
                ChargeIdle();
                return;
            }
            var other = holder.IsLeft ? VrInput.Right : VrInput.Left;
            var down = other.Grip.Down;
            var pressed = down && !chargeGripping;
            chargeGripping = down;
            if (!down)
            {
                ChargeIdle();
                return;
            }
            if (!other.TryGetGripPose(out var handAt, out var handTurn) || !holder.TryGetPosition(out var gripAt)
                || !holder.TryGetAimRotation(out var aim)) return;
            if (pressed)
            {
                if (!NearTool(other.Node) || !VrChargeByHand.HoldInTracking(holder.IsLeft, out var meshAt, out var meshTurn)) return;
                chargeGrabTurn = Quaternion.Inverse(handTurn) * meshTurn;
                chargeGrabAt = Quaternion.Inverse(handTurn) * (meshAt - handAt);
                chargeDragged = true;
                Plugin.Log.LogInfo($"Tool fit: the demo charge in the {holder.Name} hand grabbed with the {other.Name} hand");
            }
            if (!chargeDragged) return;
            var inverse = Quaternion.Inverse(aim);
            var at = inverse * (handAt + handTurn * chargeGrabAt - gripAt);
            var turn = inverse * (handTurn * chargeGrabTurn);
            if (holder.IsLeft) Mirror(ref at, ref turn);
            chargeDragAt = at;
            chargeDragTurn = turn;
        }

        private static void ChargeIdle()
        {
            chargeGripping = false;
            if (!chargeDragged) return;
            chargeDragged = false;
            ChargeHold.Store(chargeDragAt, chargeDragTurn);
            (chargeHolder ?? VrInput.Right).Buzz(0.5f, 0.08f);
        }

        private class Fit
        {
            public readonly string Key;
            public readonly string Default;
            public ConfigEntry<string> Entry;
            public string Parsed;
            public Vector3 Offset;
            public Quaternion Turn = Quaternion.identity;

            public Fit(string key, string fallback)
            {
                Key = key;
                Default = fallback;
            }
        }

        // As Raik fitted them in the headset (his config of 2026-11-17, the mod's standard from then on, as he asked).
        // Before, as Marc had fitted them: grapple "-6.5 12.1 -20.7 / 5.9 1.1 8.6" right and "3.7 10.0 -23.2 / 4.9 -0.3
        // -1.6" left, cutter "1.6 -4.6 -0.5 / 7.0 5.2 10.8" right and "2.8 -4.9 1.9 / 6.5 6.2 -6.7" left.
        private static readonly Fit GrappleRight = new Fit("GreifhakenRechts", "-1.3 6.0 -19.1 / 2.0 -3.9 7.0");
        private static readonly Fit GrappleLeft = new Fit("GreifhakenLinks", "-3.4 10.0 -17.5 / 10.6 10.8 -4.5");
        private static readonly Fit CutterRight = new Fit("CutterRechts", "-3.2 -4.5 20.5 / -10.0 -8.2 4.3");
        private static readonly Fit CutterLeft = new Fit("CutterLinks", "4.7 -4.9 18.6 / -9.4 12.0 -5.0");
        // The demo charge hangs on the game's right hand: its fit moves that hand (its wrist) against the controller.
        // Turned 6 degrees anticlockwise as seen from behind (Raik, 2026-11-13: the hand was turned 5 to 7 degrees too far
        // clockwise against the empty hand).
        // Measured with Raik, 2026-11-18: with this fit the charge's back points 79 degrees to the right and 25 degrees
        // down of the controller's aim. A fit that straightens the charge ("20 0 18 / 11.6 -80.0 33.3") turned the hand
        // and the detonator by as much, so the charge is set by itself instead (VrChargeByHand.Decoupled, since 0.31.5
        // with both ways of placing) and this fit only counts with that off: aiming without the hands.
        private static readonly Fit ChargeRight = new Fit("SprengsatzRechts", "0 0 0 / 0 0 6");

        public static ConfigEntry<bool> Adjusting;

        // The other hand's grip as Adjust last saw it.
        private static bool gripping;
        private static Fit dragged;
        // The tool relative to the grabbing controller, in tracking space.
        private static Vector3 grabPosition;
        private static Quaternion grabRotation;

        public static void Bind()
        {
            Adjusting = VrSettings.Bind("Justierung", "WerkzeugeJustieren", false,
                ("While one hand holds the cutter, the grapple, the detonator or a demo charge, squeeze the other hand's grip near it to grab the tool and move it until it sits in the hand; letting go stores the place for that tool and hand. The other hand neither takes the tool over nor holds it with both hands meanwhile. A demo charge's hold counts for both hands (mirrored) and both ways of placing. Switch it off when they fit.",
                    "Während eine Hand Cutter, Greifhaken, Zünder oder einen Sprengsatz hält: Grip der anderen Hand in der Nähe greift das Werkzeug, bewege es, bis es in der Hand sitzt. Loslassen speichert die Lage für dieses Werkzeug und diese Hand. Die andere Hand übernimmt das Werkzeug dabei nicht und hält es auch nicht mit. Die Haltung des Sprengsatzes gilt für beide Hände (gespiegelt) und beide Arten der Sprengsatzplatzierung. Danach wieder ausschalten."),
                "The places are stored in the entries below; delete an entry to get its default back.");
            foreach (var fit in new[] { GrappleRight, GrappleLeft, CutterRight, CutterLeft, ChargeRight })
                fit.Entry = VrSettings.Bind("Justierung", fit.Key, fit.Default,
                    "Where the tool sits in that hand: its mount relative to the wrist in centimeters (x right, y up, z forward along the controller), then its turn in degrees (pitch, yaw, roll), as \"x y z / pitch yaw roll\".");
            ChargeHold.Entry = VrSettings.Bind("Justierung", "SprengsatzHaltung", "",
                "How a demo charge placed by hand lies in the right hand (the left hand's is the mirror image): its middle from the grip in centimeters (x right, y up, z forward along the controller), then its turn in degrees, as \"x y z / pitch yaw roll\". Stored while fitting the tools; empty for the mod's own hold.");
            // As Raik fitted it in the headset on 2026-11-18, the standard for new installs as he asked; before, empty
            // (the game's own place), which stays for whoever has that stored.
            DetonatorHold.Entry = VrSettings.Bind("Justierung", "ZuenderHaltung", "-1.8 -13.0 -3.3 / 15.7 -20.5 -5.5",
                "Where the charges' detonator sits in the right hand (the left hand's is the mirror image): from the wrist in centimeters in the hand's own axes (x to the palm's side, y to the thumb's side, z along the knuckles), then its turn in degrees, as \"x y z / pitch yaw roll\". Stored while fitting the tools; empty for the game's own place, delete the entry for the mod's.");
        }

        // The fit of the tool in the hand, in world units relative to the wrist and the aim pose; false for tools without a
        // model on the equipment point.
        public static bool TryGet(EquipmentController.Equipment tool, bool left, out Vector3 offset, out Quaternion turn)
        {
            offset = Vector3.zero;
            turn = Quaternion.identity;
            var fit = Find(tool, left);
            if (fit == null || fit.Entry == null) return false;
            if (fit != dragged) Parse(fit);
            offset = fit.Offset;
            turn = fit.Turn;
            return true;
        }

        // While adjusting: the other hand's grip blocks drawing from the belt and holding on with that hand, only while it
        // drags the tool or is near it (Raik, 2026-11-14: with a charge in the left hand the right one could not hold the
        // wall; that charge has no fit to adjust at all).
        // A charge in a hand: the other hand's grip near it, or dragging it, moves it and holds nothing else.
        public static bool Blocks(XRNode node) => Adjusting != null && Adjusting.Value
            && ((node != VrHand.GameToolHand.Node && HoldsModel() && (dragged != null || detonatorDragged || NearTool(node)))
                || (chargeHolder != null && node != chargeHolder.Node && (chargeDragged || NearTool(node))));

        // Within this of the tool hand's grip, the other hand's grip takes the tool to adjust it.
        private const float AdjustReach = 0.2f;
        private static bool farGrip;

        private static bool NearTool(XRNode node)
        {
            var hand = node == XRNode.LeftHand ? VrInput.Left : VrInput.Right;
            var tool = node == XRNode.LeftHand ? VrInput.Right : VrInput.Left;
            return hand.TryGetPosition(out var at) && tool.TryGetPosition(out var toolAt) && Vector3.Distance(at, toolAt) <= AdjustReach;
        }

        // From VrToolModels.Place, before the tool is placed, with the tool hand's wrist and aim pose in tracking space: the
        // other hand's grip drags the tool while adjusting.
        public static void Adjust(EquipmentController.Equipment tool, bool left, Vector3 wrist, Quaternion aim)
        {
            var fit = Adjusting != null && Adjusting.Value ? Adjustable(tool, left) : null;
            var other = left ? VrInput.Right : VrInput.Left;
            var held = fit != null && other.Grip.Down;
            var pressed = held && !gripping;
            gripping = held;
            // A grip pressed away from the tool holds on to the wall instead (Blocks), until it is let go.
            if (pressed) farGrip = dragged == null && !NearTool(other.Node);
            if (!held) farGrip = false;
            if (farGrip) held = pressed = false;
            if (dragged != null && (dragged != fit || !held))
            {
                Store(dragged);
                dragged = null;
            }
            if (fit == null || !held) return;
            if (!other.TryGetGripPose(out var handPosition, out var handRotation)) return;
            var scale = VrCamera.WorldScale;
            if (pressed)
            {
                Parse(fit);
                var toolPosition = wrist + aim * (fit.Offset / scale);
                var toolRotation = aim * fit.Turn;
                grabRotation = Quaternion.Inverse(handRotation) * toolRotation;
                grabPosition = Quaternion.Inverse(handRotation) * (toolPosition - handPosition);
                dragged = fit;
                Plugin.Log.LogInfo($"Tool fit: {fit.Key} grabbed with the {(left ? "right" : "left")} hand");
                return;
            }
            if (dragged != fit) return;
            var position = handPosition + handRotation * grabPosition;
            fit.Turn = Quaternion.Inverse(aim) * (handRotation * grabRotation);
            fit.Offset = Quaternion.Inverse(aim) * (position - wrist) * scale;
        }

        private static bool HoldsModel() => VrAim.Active && (Adjustable(VrToolModels.Equipped, VrHand.GameToolHand.IsLeft) != null || VrChargeByHand.DetonatorOut);

        // The fits the other hand drags: not the charge's on the game's hand while the charge lies in the hand by itself
        // (VrChargeByHand.Decoupled), which then counts for nothing; that charge is dragged by Charge.
        private static Fit Adjustable(EquipmentController.Equipment tool, bool left) =>
            tool == EquipmentController.Equipment.DemoCharge && VrChargeByHand.Decoupled ? null : Find(tool, left);

        private static Fit Find(EquipmentController.Equipment tool, bool left)
        {
            if (tool == EquipmentController.Equipment.GrappleHook) return left ? GrappleLeft : GrappleRight;
            if (tool == EquipmentController.Equipment.CuttingTool) return left ? CutterLeft : CutterRight;
            if (tool == EquipmentController.Equipment.DemoCharge && !left) return ChargeRight;
            return null;
        }

        private static void Parse(Fit fit)
        {
            var text = fit.Entry.Value;
            if (text == fit.Parsed) return;
            fit.Parsed = text;
            if (TryParse(text, out var offset, out var angles) || TryParse(fit.Default, out offset, out angles))
            {
                fit.Offset = offset / 100f;
                fit.Turn = Quaternion.Euler(angles);
            }
            Plugin.Log.LogInfo($"Tool fit: {fit.Key} = {Format(fit)}");
        }

        internal static bool TryParse(string text, out Vector3 offset, out Vector3 angles)
        {
            offset = angles = Vector3.zero;
            var parts = (text ?? "").Replace(',', '.').Split('/');
            return parts.Length == 2 && TryParse(parts[0], out offset) && TryParse(parts[1], out angles);
        }

        private static bool TryParse(string text, out Vector3 value)
        {
            value = Vector3.zero;
            var numbers = text.Split(new[] { ' ', '\t', ';' }, System.StringSplitOptions.RemoveEmptyEntries);
            if (numbers.Length != 3) return false;
            for (var i = 0; i < 3; i++)
            {
                if (!float.TryParse(numbers[i], NumberStyles.Float, CultureInfo.InvariantCulture, out var number)) return false;
                value[i] = number;
            }
            return true;
        }

        private static string Format(Fit fit)
        {
            var offset = fit.Offset * 100f;
            var angles = fit.Turn.eulerAngles;
            for (var i = 0; i < 3; i++)
                if (angles[i] > 180f) angles[i] -= 360f;
            return string.Format(CultureInfo.InvariantCulture, "{0:0.0} {1:0.0} {2:0.0} / {3:0.0} {4:0.0} {5:0.0}",
                offset.x, offset.y, offset.z, angles.x, angles.y, angles.z);
        }

        private static void Store(Fit fit)
        {
            var text = Format(fit);
            fit.Entry.Value = text;
            fit.Parsed = text;
            Plugin.Log.LogInfo($"Tool fit: {fit.Key} = {text}");
        }
    }
}
