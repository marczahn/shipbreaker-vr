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
    // stores where it is (Adjust). The defaults are where Marc put them; the option stays for anyone whose hands differ.
    internal static class VrToolFit
    {

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

        // As Marc fitted them in the headset.
        private static readonly Fit GrappleRight = new Fit("GreifhakenRechts", "-6.5 12.1 -20.7 / 5.9 1.1 8.6");
        private static readonly Fit GrappleLeft = new Fit("GreifhakenLinks", "3.7 10.0 -23.2 / 4.9 -0.3 -1.6");
        private static readonly Fit CutterRight = new Fit("CutterRechts", "1.6 -4.6 -0.5 / 7.0 5.2 10.8");
        private static readonly Fit CutterLeft = new Fit("CutterLinks", "2.8 -4.9 1.9 / 6.5 6.2 -6.7");
        // The demo charge hangs on the game's right hand: its fit moves that hand (its wrist) against the controller.
        // Turned 6 degrees anticlockwise as seen from behind (Raik, 2026-11-13: the hand was turned 5 to 7 degrees too far
        // clockwise against the empty hand).
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
                ("While one hand holds the cutter, the grapple or (right) a demo charge, squeeze the other hand's grip to grab the tool and move it until it sits in the hand; letting go stores the place for that tool and hand. Switch it off when they fit.",
                    "Während eine Hand Cutter, Greifhaken oder (rechts) einen Sprengsatz hält: Grip der anderen Hand greift das Werkzeug, bewege es, bis es in der Hand sitzt. Loslassen speichert die Lage für dieses Werkzeug und diese Hand. Danach wieder ausschalten."),
                "The places are stored in the entries below; delete an entry to get its default back.");
            foreach (var fit in new[] { GrappleRight, GrappleLeft, CutterRight, CutterLeft, ChargeRight })
                fit.Entry = VrSettings.Bind("Justierung", fit.Key, fit.Default,
                    "Where the tool sits in that hand: its mount relative to the wrist in centimeters (x right, y up, z forward along the controller), then its turn in degrees (pitch, yaw, roll), as \"x y z / pitch yaw roll\".");
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
        public static bool Blocks(XRNode node) => Adjusting != null && Adjusting.Value && node != VrHand.GameToolHand.Node && HoldsModel()
            && (dragged != null || NearTool(node));

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
            var fit = Adjusting != null && Adjusting.Value ? Find(tool, left) : null;
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

        private static bool HoldsModel() => VrAim.Active && Find(VrToolModels.Equipped, VrHand.GameToolHand.IsLeft) != null;

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
