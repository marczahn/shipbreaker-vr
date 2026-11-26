using UnityEngine;

namespace ShipbreakerVR
{
    // Picking the tools off the body instead of the flat game's wheel, as Marc wanted: reaching there with either hand and
    // pressing the grip draws the tool (VrToolInput), letting go puts it back. The zones are measured in tracking space
    // (real meters); they are the same for both hands. Entering a zone gives that hand a short buzz.
    // The zones at the helmet are measured from the head along its heading. Those at the hips and over the shoulder
    // hang on the body (Raik, 2026-11-18: the body stays put, only the head looks around): from the neck along the
    // heading the body keeps (VrTorso), so they neither turn with a look to the side nor move as the head nods (the eye
    // goes 17 cm forward and 9 cm down against the neck when looking down at the hip). Their bounds are still written
    // as from the head, for the pose they were found in: looking down at the hip (HipLook), upright for the shoulder.
    //   right hip            grapple
    //   left hip             the demo charges' detonator
    //   behind the lower back    demo charges, for either hand
    //   over the right shoulder  cutter
    //   over the left shoulder   nothing to draw: there things are let go into the backpack (VrShoulderStow)
    //   right side of the helmet scanner
    //   left side of the helmet  flashlight on or off, left hand only
    // Each place has one use (Raik, 2026-11-18): the demo charges were at the left hip with their detonator a mode of
    // theirs; now the detonator has the hip and the charges the lower back. For left-handers all of it is mirrored
    // (Steuerung/Haendigkeit): the hand's place is mirrored before it is looked up, and what hangs on the body is hung
    // on the other side (Sided).
    internal static class VrToolBelt
    {
        public enum Zone { None, Grapple, Cutter, DemoCharge, Scanner, Flashlight, Detonator }

        public enum Handed { Rechts, Links }

        public static bool LeftHanded => VrSettings.Handedness != null && VrSettings.Handedness.Value == Handed.Links;

        // A place on the body written for right-handers (x right), on the side it has for this player.
        internal static Vector3 Sided(Vector3 place) => LeftHanded ? new Vector3(-place.x, place.y, place.z) : place;

        // Head-relative bounds in meters: x right, y up, z forward.
        private const float HipBelow = -0.45f;
        private const float HipDeepest = -0.85f;
        private const float HipSide = 0.1f;
        private const float HipOutside = 0.35f;
        // With the grapple at the hip (VrBeltGrapple), the hand draws it only within GripReach of its grip, as Raik asked:
        // the zone around the hip reached far beyond the grapple. The grip hangs at GripAt, where Raik's hand reached for
        // it (12 grips, all within 9 cm); before, a copy hung at the body model's hip, 30 cm further back, and the hand took
        // it from here.
        // 7 cm further forward with the grapple itself (VrBeltGrapple, Raik 2026-11-18).
        // Placed in the headset (VrToolPlace, Raik 2026-11-26), it moves with the grapple.
        private static readonly Vector3 DefaultGripAt = new Vector3(0.30f, -0.72f, -0.01f);
        public static Vector3 GripAt => VrToolPlace.GripAt(DefaultGripAt);
        private const float GripReach = 0.13f;
        // The detonator's box, 10 cm further forward than the demo charges' was (Raik, 2026-11-18: the detonator, hung
        // in its middle, was out of sight beside the body; zone and detonator forward, so it can be seen).
        private const float HipForward = 0.2f;
        private const float HipBehind = -0.1f;
        // The hip zones are turned this far around the head towards the back, in degrees, so a hand reaching out to the
        // side to hold on does not draw a tool.
        private const float HipTurn = 15f;
        private static readonly float HipTurnCos = Mathf.Cos(HipTurn * Mathf.Deg2Rad);
        private static readonly float HipTurnSin = Mathf.Sin(HipTurn * Mathf.Deg2Rad);
        private const float ShoulderAbove = -0.12f;
        private const float ShoulderBehind = -0.05f;
        private const float ShoulderInside = -0.1f;
        // The cutter's zone over the right shoulder reaches lower and further in than that (Raik, 2026-11-17: it may be
        // a touch larger; 2026-11-18: one cannot see the cutter there, so the zone is to be large, whatever its grip);
        // the zone over the left shoulder for stowing (OverLeftShoulder) stays as it was.
        // It counts from the head as before and against the body as well (ZoneOf): hung on the body alone, the hand had
        // to go 8 to 14 cm further back than Raik was used to, behind the neck instead of behind the head, which leans
        // forward with the reach. Against the body it begins CutterBehindBody from the neck's line.
        private const float CutterAbove = -0.20f;
        private const float CutterInside = -0.15f;
        private const float CutterBehindBody = -0.02f;
        private const float HelmetReach = 0.22f;
        private const float HelmetSide = 0.06f;

        // The zone each hand last buzzed for (left, right).
        private static readonly Zone[] announced = new Zone[2];
        // The zone each hand was in when last looked at, and in which frame (Last).
        private static readonly Zone[] last = new Zone[2];
        private static readonly int[] lastFrame = { -10, -10 };

        // The zone that hand is in, as Update found it this frame or the one before; None if it was not looked at.
        // For what shows by where a hand is (VrKeyHints).
        internal static Zone Last(bool left)
        {
            var index = left ? 0 : 1;
            return Time.frameCount - lastFrame[index] <= 1 ? last[index] : Zone.None;
        }
        private static int logs;
        private static int missLogs;
        private static int wallLogs;

        // The zone the hand is in this frame (None without tracking). Quiet (while its trigger is held) it does not buzz;
        // the buzz comes when the trigger is let go inside the zone.
        public static Zone Update(VrInput.Hand hand, bool quiet)
        {
            var zone = Find(hand, out var local, out var onBody);
            // Placing the grapple or the detonator on the body (VrToolPlace): the hand takes the tool, draws nothing.
            if (VrToolPlace.Handle(hand, zone)) zone = Zone.None;
            // Raik (2026-11-26): reaching towards the tools with a wall near, the wall comes first, so climbing along it
            // does not keep drawing a tool; to draw one, turn a little away from the wall.
            else if (onBody && zone != Zone.None && zone != Zone.Scanner && zone != Zone.Flashlight && VrClimb.WallNear(hand))
            {
                if (wallLogs++ < 20 && hand.Grip.Pressed) Plugin.Log.LogInfo($"Tool belt: {hand.Name} hand at {zone}, but a wall is in reach: the wall comes first");
                zone = Zone.None;
            }
            var index = hand.IsLeft ? 0 : 1;
            last[index] = zone;
            lastFrame[index] = Time.frameCount;
            // A grip pressed near the hip grapple but outside its zone: where the hand was, to fit GripReach.
            if (zone == Zone.None && !hand.IsLeft && hand.Grip.Pressed && missLogs < 30 && HipLocal(hand, out var atHip))
            {
                var off = atHip - GripAt;
                if (off.magnitude < 0.4f)
                {
                    missLogs++;
                    Plugin.Log.LogInfo($"Tool belt: right grip pressed {off.magnitude:0.00} m from where the hip grapple is drawn (off {off.x:0.00}, {off.y:0.00}, {off.z:0.00}), outside its {GripReach:0.00} m");
                }
            }
            if (zone == Zone.None) announced[index] = Zone.None;
            else if (zone != announced[index] && !quiet)
            {
                announced[index] = zone;
                hand.Buzz(0.25f, 0.04f);
                if (logs++ < 40)
                    Plugin.Log.LogInfo($"Tool belt: {hand.Name} hand at {zone} ({local.x:0.00}, {local.y:0.00}, {local.z:0.00} m from the head)");
            }
            return zone;
        }

        private static Zone Find(VrInput.Hand controller, out Vector3 local, out bool onBody)
        {
            onBody = false;
            if (!HeadLocal(controller, out local)) return Zone.None;
            // Experiment (VrLooseTools): a tool afloat is not at the body; its zone is around the tool itself.
            var body = ZoneOf(controller, local);
            var zone = VrLooseTools.Zone(controller, body);
            onBody = zone == body;
            // The flashlight's hand is the one on its side of the helmet.
            return zone == Zone.Flashlight && controller.IsLeft == LeftHanded ? Zone.None : zone;
        }

        // Where the controller is relative to the head, in meters along the head's heading (x right, y up, z forward), as
        // the zones are measured; also for stowing things over the left shoulder (VrShoulderStow).
        internal static bool HeadLocal(VrInput.Hand controller, out Vector3 local)
        {
            local = Vector3.zero;
            var vr = VrCamera.Instance;
            if (vr == null || !controller.TryGetPosition(out var hand)) return false;
            var head = vr.transform;
            var forward = head.localRotation * Vector3.forward;
            forward.y = 0f;
            // Looking straight down the top of the head points ahead.
            if (forward.sqrMagnitude < 0.01f) forward = head.localRotation * Vector3.up;
            forward.y = 0f;
            if (forward.sqrMagnitude < 0.0001f) return false;
            local = Sided(Quaternion.Inverse(Quaternion.LookRotation(forward, Vector3.up)) * (hand - head.localPosition));
            return true;
        }

        // The pitch of the head, in degrees down, at which the places at the hip were found (Raik looking down at the
        // grapple, VrBeltGrapple), and where the neck is from the eye then and with the head upright, along the heading.
        private const float HipLook = 58f;
        internal static Vector3 HipRef => Quaternion.Euler(HipLook, 0f, 0f) * VrTorso.NeckFromEye;
        internal static Vector3 ShoulderRef => VrTorso.NeckFromEye;
        // The demo charges' box at the left hip was found with no one pose of the head on record: taken half way between
        // looking down and upright, it lies within 9 cm of where it was either way, well inside its 30 by 40 cm.
        internal static Vector3 ChargeRef => Quaternion.Euler(HipLook * 0.5f, 0f, 0f) * VrTorso.NeckFromEye;

        // The body's frame in tracking space: its neck, below the tracked head, and the heading it keeps (VrTorso; the
        // head's while the model is not upright). A place written as from the head, found in a pose with the neck at ref
        // from the eye, is at neck + heading * (place - ref).
        internal static bool BodyFrame(out Vector3 neck, out Quaternion heading)
        {
            neck = Vector3.zero;
            heading = Quaternion.identity;
            var vr = VrCamera.Instance;
            if (vr == null) return false;
            var head = vr.transform;
            neck = head.localPosition + head.localRotation * VrTorso.NeckFromEye;
            if (VrTorso.Upright)
            {
                heading = Quaternion.AngleAxis(VrTorso.Heading, Vector3.up);
                return true;
            }
            var forward = head.localRotation * Vector3.forward;
            forward.y = 0f;
            if (forward.sqrMagnitude < 0.01f) forward = head.localRotation * Vector3.up;
            forward.y = 0f;
            if (forward.sqrMagnitude < 0.0001f) return false;
            heading = Quaternion.LookRotation(forward, Vector3.up);
            return true;
        }

        // Where the controller is against the body, written as from the head in the pose the hip's places were found in
        // (HipLocal) or with the head upright (ShoulderLocal), for ZoneAt.
        internal static bool HipLocal(VrInput.Hand controller, out Vector3 local) => BodyLocal(controller, HipRef, out local);

        internal static bool ShoulderLocal(VrInput.Hand controller, out Vector3 local) => BodyLocal(controller, ShoulderRef, out local);

        // A point in tracking space written as ShoulderLocal writes a controller, and the way back (VrShoulderStow: the
        // thing held in the zone over the left shoulder, not only the hand).
        internal static bool ShoulderLocalOf(Vector3 tracking, out Vector3 local)
        {
            local = Vector3.zero;
            if (!BodyFrame(out var neck, out var heading)) return false;
            local = Sided(Quaternion.Inverse(heading) * (tracking - neck) + ShoulderRef);
            return true;
        }

        internal static bool ShoulderToTracking(Vector3 local, out Vector3 tracking)
        {
            tracking = Vector3.zero;
            if (!BodyFrame(out var neck, out var heading)) return false;
            tracking = neck + heading * (Sided(local) - ShoulderRef);
            return true;
        }

        private static bool BodyLocal(VrInput.Hand controller, Vector3 reference, out Vector3 local)
        {
            local = Vector3.zero;
            if (!BodyFrame(out var neck, out var heading) || !controller.TryGetPosition(out var hand)) return false;
            local = Sided(Quaternion.Inverse(heading) * (hand - neck) + reference);
            return true;
        }

        // The zone of a hand: the helmet's from the head, the hips' and the shoulder's against the body.
        private static Zone ZoneOf(VrInput.Hand controller, Vector3 head)
        {
            var zone = ZoneAt(head);
            if (zone == Zone.Scanner || zone == Zone.Flashlight) return zone;
            if (HipLocal(controller, out var hip) && ZoneAt(hip) == Zone.Grapple) return Zone.Grapple;
            if (BodyLocal(controller, ChargeRef, out var box) && ChargeBox(box, 0f)) return Zone.Detonator;
            if (zone == Zone.Cutter || OverRightShoulderOfBody(controller)) return Zone.Cutter;
            return ShoulderLocal(controller, out var back) && LowerBack(back, 0f) ? Zone.DemoCharge : Zone.None;
        }

        // Behind the lower back, where the demo charges are drawn from (Raik, 2026-11-18): for either hand, and large, as
        // the hand finds it unseen. Measured against the body as the shoulder is (upright, y from the eye's height): from
        // BackFront behind the neck's line, where a hand hanging at the side is not yet, down from the middle of the back.
        private const float BackTop = -0.35f;
        private const float BackBottom = -1.0f;
        private const float BackFront = -0.12f;
        private const float BackDeep = -0.7f;
        private const float BackSide = 0.28f;

        private static bool LowerBack(Vector3 local, float wider) =>
            local.y < BackTop + wider && local.y > BackBottom - wider && local.z < BackFront + wider && local.z > BackDeep - wider
            && Mathf.Abs(local.x) < BackSide + wider;

        private static bool OverRightShoulderOfBody(VrInput.Hand controller) =>
            ShoulderLocal(controller, out var shoulder) && OverRightShoulder(shoulder, CutterBehindBody);

        // Over the left shoulder, the mirror of the cutter's zone over the right one, with its inner edge at the middle
        // of the head (the cutter's reaches 10 cm past it), for stowing what a hand holds (VrShoulderStow).
        internal static bool OverLeftShoulder(Vector3 local) => local.y > ShoulderAbove && local.z < ShoulderBehind && local.x < 0f;

        // Over the right shoulder, where the cutter is drawn from: from behind (z, forward) on.
        private static bool OverRightShoulder(Vector3 local, float behind) => local.y > CutterAbove && local.z < behind && local.x > CutterInside;

        // The detonator's box at the left hip (the demo charges' until 2026-11-18), that much wider all round.
        private static bool ChargeBox(Vector3 local, float wider)
        {
            // Moved with the detonator where it is placed in the headset (VrToolPlace).
            local -= VrToolPlace.DetonatorShift;
            if (local.y >= HipBelow + wider || local.y <= HipDeepest - wider) return false;
            // Measured as if the hand were turned forward by HipTurn on its side of the body, mirrored for the left hip.
            var side = Mathf.Abs(local.x);
            var turnedSide = side * HipTurnCos - local.z * HipTurnSin;
            var turnedForward = local.z * HipTurnCos + side * HipTurnSin;
            return local.x <= wider && turnedForward < HipForward + wider && turnedForward > HipBehind - wider
                && turnedSide > HipSide - wider && turnedSide < HipOutside + wider;
        }

        // The zone at a hand position relative to the head, in meters along the head's heading (x right, y up, z forward).
        // The grapple's zone is a ball of GripReach around its grip at the right hip, the detonator's the box at the left,
        // the demo charges' the space behind the lower back.
        internal static Zone ZoneAt(Vector3 local)
        {
            if ((local - GripAt).magnitude < GripReach) return Zone.Grapple;
            if (ChargeBox(local, 0f)) return Zone.Detonator;
            if (LowerBack(local, 0f)) return Zone.DemoCharge;
            if (local.magnitude < HelmetReach && local.x > HelmetSide && local.z > ShoulderBehind) return Zone.Scanner;
            if (local.magnitude < HelmetReach && local.x < -HelmetSide && local.z > ShoulderBehind) return Zone.Flashlight;
            if (OverRightShoulder(local, ShoulderBehind)) return Zone.Cutter;
            return Zone.None;
        }

        // Experiment (VrLooseTools): whether a tool let go with the hand there goes back onto the body, where it is drawn
        // from; the grapple's ball a little wider than for drawing it, as the hand need not find the grip.
        private const float StowWider = 1.6f;

        internal static bool Stows(Zone zone, Vector3 local) =>
            zone == Zone.Grapple ? (local - GripAt).magnitude < GripReach * StowWider : zone != Zone.None && ZoneAt(local) == zone;

        // The detonator and the demo charges go back within this much around their places (Raik, 2026-11-18: let go just
        // beside their box, a charge was let go afloat each time and the charges could not be put back).
        private const float ChargeStowWider = 0.15f;
        private const float BackStowWider = 0.08f;
        // The cutter goes back within a larger space than it is drawn from (Raik, 2026-11-18: "sometimes a little hard
        // to put away; larger, and above all further forward"): this much further forward, lower and to the inside.
        private const float CutterStowForward = 0.18f;
        private const float CutterStowLower = 0.10f;
        private const float CutterStowInside = 0.08f;
        private const float CutterStowWithin = 0.55f;

        // Where a hand is as the demo charges' place behind the back is measured, for the log.
        internal static string ChargePlace(VrInput.Hand controller) =>
            ShoulderLocal(controller, out var at) ? $"({at.x:0.00}, {at.y:0.00}, {at.z:0.00}) m as the place behind the back is measured" : "untracked";

        // The same for a hand, measured as its zone is: at the hip, over the shoulder, or from the head.
        internal static bool Stows(Zone zone, VrInput.Hand controller)
        {
            if (zone == Zone.Grapple) return HipLocal(controller, out var hip) && Stows(zone, hip);
            if (zone == Zone.Detonator) return BodyLocal(controller, ChargeRef, out var box) && ChargeBox(box, ChargeStowWider);
            if (zone == Zone.DemoCharge) return ShoulderLocal(controller, out var back) && LowerBack(back, BackStowWider);
            if (zone == Zone.Cutter)
                return (HeadLocal(controller, out var from) && ZoneAt(from) == Zone.Cutter) || OverRightShoulderOfBody(controller)
                    || (ShoulderLocal(controller, out var wide) && wide.y > CutterAbove - CutterStowLower
                        && wide.z < CutterBehindBody + CutterStowForward && wide.x > CutterInside - CutterStowInside);
            return HeadLocal(controller, out var head) && Stows(zone, head);
        }

        // The same for a point in tracking space, measured against the body: whether something there is at that zone's
        // place (a part of a tool afloat that a hand holds by its housing, VrLooseTools). The places on the body only:
        // the helmet's are for the hand.
        internal static bool StowsAt(Zone zone, Vector3 tracking)
        {
            if (!BodyFrame(out var neck, out var heading)) return false;
            var fromNeck = Quaternion.Inverse(heading) * (tracking - neck);
            if (zone == Zone.Grapple) return Stows(zone, Sided(fromNeck + HipRef));
            if (zone == Zone.Detonator) return ChargeBox(Sided(fromNeck + ChargeRef), ChargeStowWider);
            if (zone == Zone.DemoCharge) return LowerBack(Sided(fromNeck + ShoulderRef), BackStowWider);
            if (zone != Zone.Cutter) return false;
            // The cutter's place is open upwards, backwards and to the side, which suits a hand that reaches over the
            // shoulder; a part of a tool half a metre long gets into it far from the shoulder too (held at arm's length
            // to the side at head height), so for those it ends CutterStowWithin from the eyes (review, 2026-11-23).
            var wide = Sided(fromNeck + ShoulderRef);
            return wide.y > CutterAbove - CutterStowLower && wide.z < CutterBehindBody + CutterStowForward && wide.x > CutterInside - CutterStowInside
                && wide.sqrMagnitude < CutterStowWithin * CutterStowWithin;
        }

        public static void Reset() => announced[0] = announced[1] = Zone.None;
    }
}
