using System.Globalization;
using BBI.Unity.Game;
using BepInEx.Configuration;
using UnityEngine;
using UnityEngine.XR;

namespace ShipbreakerVR
{
    // Where the cutter and the grapple sit in each hand (VrToolModels.Place): the tool's equipment point relative to the
    // hand's wrist (the grip pose moved back by VrToolModels.WristBehindGrip, in the aim pose's axes: x right, y up,
    // z forward) in centimeters, and its turn as pitch, yaw and roll in degrees, one config entry per tool and hand
    // ("x y z / pitch yaw roll"). The tool turns with it, and so does the aim, which runs along the barrel. Measuring the
    // flat game's hold left the tools off in both hands, each differently, so Marc sets them in the headset: with
    // "WerkzeugeJustieren" on, the grip of the other hand grabs the tool in the hand and moves it rigidly, and letting go
    // stores where it is (Adjust). Temporary, until the values are known and become the defaults.
    internal static class VrToolFit
    {
        private const float GripPress = 0.5f;
        private const float GripRelease = 0.4f;

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

        // The starting points come from the measured flat-game hold, mirrored for the left hand.
        private static readonly Fit GrappleRight = new Fit("GreifhakenRechts", "25 30 -34 / 0 0 0");
        private static readonly Fit GrappleLeft = new Fit("GreifhakenLinks", "-25 30 -34 / 0 0 0");
        private static readonly Fit CutterRight = new Fit("CutterRechts", "32 34 -42 / 0 0 0");
        private static readonly Fit CutterLeft = new Fit("CutterLinks", "-34 34 -42 / 0 0 0");

        public static ConfigEntry<bool> Adjusting;

        private static InputDevice otherDevice;
        private static XRNode otherNode;
        private static bool gripping;
        private static Fit dragged;
        // The tool relative to the grabbing controller, in tracking space.
        private static Vector3 grabPosition;
        private static Quaternion grabRotation;

        public static void Bind(ConfigFile config)
        {
            Adjusting = config.Bind("Justierung", "WerkzeugeJustieren", false,
                "Temporary, for fitting the tools into the hands: while a hand holds the cutter or the grapple, the other hand's grip grabs the tool and moves it; letting go stores its place in the entries below.");
            foreach (var fit in new[] { GrappleRight, GrappleLeft, CutterRight, CutterLeft })
                fit.Entry = config.Bind("Justierung", fit.Key, fit.Default,
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

        // While adjusting: the other hand's grip blocks drawing from the belt with that hand.
        public static bool Blocks(XRNode node) => Adjusting != null && Adjusting.Value && node != VrToolInput.ToolNode && HoldsModel();

        // From VrToolModels.Place, before the tool is placed, with the tool hand's wrist and aim pose in tracking space: the
        // other hand's grip drags the tool while adjusting.
        public static void Adjust(EquipmentController.Equipment tool, bool left, Vector3 wrist, Quaternion aim)
        {
            var fit = Adjusting != null && Adjusting.Value ? Find(tool, left) : null;
            var node = left ? XRNode.RightHand : XRNode.LeftHand;
            if (node != otherNode || !otherDevice.isValid)
            {
                otherNode = node;
                otherDevice = InputDevices.GetDeviceAtXRNode(node);
            }
            var held = false;
            if (fit != null && otherDevice.isValid && otherDevice.TryGetFeatureValue(CommonUsages.grip, out var grip))
                held = grip >= (gripping ? GripRelease : GripPress);
            var pressed = held && !gripping;
            gripping = held;
            if (dragged != null && (dragged != fit || !held))
            {
                Store(dragged);
                dragged = null;
            }
            if (fit == null || !held) return;
            if (!otherDevice.TryGetFeatureValue(CommonUsages.devicePosition, out var handPosition)
                || !otherDevice.TryGetFeatureValue(CommonUsages.deviceRotation, out var handRotation)) return;
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

        private static bool HoldsModel() => VrAim.Active && Find(VrToolModels.Equipped, false) != null;

        private static Fit Find(EquipmentController.Equipment tool, bool left)
        {
            if (tool == EquipmentController.Equipment.GrappleHook) return left ? GrappleLeft : GrappleRight;
            if (tool == EquipmentController.Equipment.CuttingTool) return left ? CutterLeft : CutterRight;
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

        private static bool TryParse(string text, out Vector3 offset, out Vector3 angles)
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
