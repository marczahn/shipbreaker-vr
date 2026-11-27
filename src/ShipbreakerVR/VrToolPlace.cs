using BepInEx.Configuration;
using UnityEngine;
using UnityEngine.XR;

namespace ShipbreakerVR
{
    // Raik (2026-11-26): where the grapple and the detonator hang on the body, and so where they are reached for, set in
    // the headset. With Justierung/WerkzeugPositionAnpassen on (in the VR options right under "Werkzeuge justieren"),
    // squeezing the grip at a hanging tool's place (the zone it is drawn from, VrToolBelt) takes hold of the tool
    // itself instead of drawing it: it moves and turns rigidly with the controller, and where the grip lets go it stays,
    // stored in the config (GreifhakenPlatz, ZuenderPlatz). Its zone goes with it: the grapple's ball around its grip,
    // the detonator's box by as much as the detonator moved. Switched off, the tools are drawn as before, from where they
    // were put. Kept as for right-handers (the body's x to the right); the left-handed get the mirror image, as all of
    // the belt (VrToolBelt.Sided). Empty entries: the mod's own places (VrBeltGrapple.BodyAt, DetonatorAt).
    // The cutter and the charges' place behind the back may follow later (Raik: "first those two, because one can see
    // them").
    internal static class VrToolPlace
    {
        public static ConfigEntry<bool> Positioning;
        private static readonly VrToolFit.Hold GrappleHold = new VrToolFit.Hold();
        private static readonly VrToolFit.Hold DetonatorHold = new VrToolFit.Hold();

        public static bool On => Positioning != null && Positioning.Value;

        public static void Bind()
        {
            Positioning = VrSettings.Bind("Justierung", "WerkzeugPositionAnpassen", false,
                ("Squeeze the grip at the grapple on the right hip or the detonator on the left to take hold of the tool itself instead of drawing it; move it where you like it, and where you let go it stays, its grip zone with it. Switch it off when they hang right.",
                    "Grip am Greifhaken an der rechten Hüfte oder am Zünder an der linken greift das Werkzeug selbst, statt es zu ziehen; bewege es, wohin du es haben willst. Wo du loslässt, bleibt es, sein Griffbereich mit ihm. Danach wieder ausschalten."),
                "The places are stored in the entries below; delete an entry to get the mod's place back.");
            // As Raik placed them in the headset, the standard since 2026-11-27 (before, empty: the mod's own places).
            GrappleHold.Entry = VrSettings.Bind("Justierung", "GreifhakenPlatz", "33.0 -72.2 -10.4 / 58.8 11.0 28.2",
                "Where the grapple hangs on the body: its body's middle in centimeters from the eye, in the axes the belt is measured in (x right, y up, z forward, as when looking down at the hip), then its turn against the body's heading in degrees (pitch, yaw, roll), as \"x y z / pitch yaw roll\". Stored while placing the tools; empty for the mod's place.");
            DetonatorHold.Entry = VrSettings.Bind("Justierung", "ZuenderPlatz", "-27.1 -76.1 3.4 / 44.2 41.4 17.5",
                "Where the charges' detonator hangs on the body: its middle in centimeters, in the axes its zone is measured in (x right, y up, z forward), then its turn against the body's heading in degrees (pitch, yaw, roll), as \"x y z / pitch yaw roll\". Stored while placing the tools; empty for the mod's place.");
        }

        private static Quaternion GrappleDefaultTurn =>
            Quaternion.Euler(-VrBeltGrapple.MuzzleForward, 0f, 0f) * Quaternion.LookRotation(Vector3.down, Vector3.forward);

        // The grapple's body and turn against the heading, for right-handers (the caller sides them).
        public static void Grapple(out Vector3 at, out Quaternion turn)
        {
            if (dragging == Tool.Grapple)
            {
                at = dragAt;
                turn = dragTurn;
                return;
            }
            if (GrappleHold.TryGet(out at, out turn)) return;
            at = VrBeltGrapple.BodyAt;
            turn = GrappleDefaultTurn;
        }

        public static void Detonator(out Vector3 at, out Quaternion turn)
        {
            if (dragging == Tool.Detonator)
            {
                at = dragAt;
                turn = dragTurn;
                return;
            }
            if (DetonatorHold.TryGet(out at, out turn)) return;
            at = VrBeltGrapple.DetonatorAt;
            turn = Quaternion.identity;
        }

        // The grapple's grip zone's middle (VrToolBelt.GripAt), as it lies against the grapple's body.
        public static Vector3 GripAt(Vector3 defaultGrip)
        {
            // Not placed: the mod's grip, without an engine call (the tests run without the engine).
            Vector3 at;
            Quaternion turn;
            if (dragging == Tool.Grapple)
            {
                at = dragAt;
                turn = dragTurn;
            }
            else if (!GrappleHold.TryGet(out at, out turn)) return defaultGrip;
            var fromBody = Quaternion.Inverse(GrappleDefaultTurn) * (defaultGrip - VrBeltGrapple.BodyAt);
            return at + turn * fromBody;
        }

        // How far the detonator's box is moved with it.
        public static Vector3 DetonatorShift
        {
            get
            {
                Vector3 at;
                if (dragging == Tool.Detonator) at = dragAt;
                else if (!DetonatorHold.TryGet(out at, out _)) return Vector3.zero;
                return at - VrBeltGrapple.DetonatorAt;
            }
        }

        // A turn against the heading as the left-handed have it, or back.
        public static Quaternion Sided(Quaternion turn) =>
            VrToolBelt.LeftHanded ? new Quaternion(turn.x, -turn.y, -turn.z, turn.w) : turn;

        private enum Tool { None, Grapple, Detonator }

        private static Tool dragging;
        private static XRNode draggedBy;
        private static Vector3 dragAt;
        private static Quaternion dragTurn = Quaternion.identity;
        // The tool against the dragging controller, in tracking space.
        private static Vector3 grabAt;
        private static Quaternion grabTurn = Quaternion.identity;

        // While that hand drags a tool: it draws nothing, holds nothing else and hands nothing over (VrToolFit.Blocks).
        public static bool Blocks(XRNode node) => dragging != Tool.None && draggedBy == node;

        // From VrToolBelt.Update with the zone the hand is in: true while this hand takes or drags a tool to place it,
        // and the zone then counts as none.
        public static bool Handle(VrInput.Hand hand, VrToolBelt.Zone zone)
        {
            if (dragging != Tool.None && draggedBy == hand.Node)
            {
                if (!On || !hand.Grip.Down || !Move(hand))
                {
                    Store();
                    return true;
                }
                return true;
            }
            if (!On || dragging != Tool.None || !hand.Grip.Pressed) return false;
            var tool = zone == VrToolBelt.Zone.Grapple && VrBeltGrapple.GrappleHanging ? Tool.Grapple
                : zone == VrToolBelt.Zone.Detonator && VrBeltGrapple.DetonatorHanging ? Tool.Detonator : Tool.None;
            if (tool == Tool.None) return false;
            if (!hand.TryGetGripPose(out var controller, out var controllerTurn) || !ToTracking(tool, out var toolAt, out var toolTurn)) return false;
            grabAt = Quaternion.Inverse(controllerTurn) * (toolAt - controller);
            grabTurn = Quaternion.Inverse(controllerTurn) * toolTurn;
            if (tool == Tool.Grapple) Grapple(out dragAt, out dragTurn);
            else Detonator(out dragAt, out dragTurn);
            dragging = tool;
            draggedBy = hand.Node;
            hand.Buzz(0.4f, 0.05f);
            Plugin.Log.LogInfo($"Tool place: {hand.Name} hand takes the {(tool == Tool.Grapple ? "grapple" : "detonator")} to place it");
            return true;
        }

        // The tool's place and turn in tracking space, from the body's frame.
        private static bool ToTracking(Tool tool, out Vector3 at, out Quaternion turn)
        {
            at = Vector3.zero;
            turn = Quaternion.identity;
            if (!VrToolBelt.BodyFrame(out var neck, out var heading)) return false;
            Vector3 local;
            Quaternion localTurn;
            if (tool == Tool.Grapple) Grapple(out local, out localTurn);
            else Detonator(out local, out localTurn);
            var reference = tool == Tool.Grapple ? VrToolBelt.HipRef : VrToolBelt.ChargeRef;
            at = neck + heading * (VrToolBelt.Sided(local) - reference);
            turn = heading * Sided(localTurn);
            return true;
        }

        private static bool Move(VrInput.Hand hand)
        {
            if (!hand.TryGetGripPose(out var controller, out var controllerTurn) || !VrToolBelt.BodyFrame(out var neck, out var heading)) return false;
            var at = controller + controllerTurn * grabAt;
            var turn = controllerTurn * grabTurn;
            var reference = dragging == Tool.Grapple ? VrToolBelt.HipRef : VrToolBelt.ChargeRef;
            dragAt = VrToolBelt.Sided(Quaternion.Inverse(heading) * (at - neck) + reference);
            dragTurn = Sided(Quaternion.Inverse(heading) * turn);
            return true;
        }

        private static void Store()
        {
            var hold = dragging == Tool.Grapple ? GrappleHold : DetonatorHold;
            var name = dragging == Tool.Grapple ? "grapple" : "detonator";
            dragging = Tool.None;
            hold.Store(dragAt, dragTurn);
            Plugin.Log.LogInfo($"Tool place: the {name} stays at ({dragAt.x:0.00}, {dragAt.y:0.00}, {dragAt.z:0.00}) m, its zone with it");
        }
    }
}
