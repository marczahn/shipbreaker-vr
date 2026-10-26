using System.Collections.Generic;
using BBI.Unity.Game;
using HarmonyLib;
using UnityEngine;
using UnityEngine.XR;

namespace ShipbreakerVR
{
    // A second mode for the grapple, like the cutter's, as Marc asked: B tap switches between grappling and placing
    // tethers (VrToolInput). In the tether mode the trigger places a tether: pressed it starts where the hand points,
    // released it ends where the hand points then; A held recalls the tethers, and B only switches back, so a slow
    // tap never recalls them. The mode is the mod's own, the game only
    // gets the tether actions instead of the grapple's. The game's own grapple modes (GunController: Grapple and an
    // unreleased Magnet) are left alone; B tap no longer sends ToolMode to the grapple.
    // In the tether mode a tap on A lets go of the tether the laser points at (on its rope or near one of its ends), as
    // recalling does for all of them (TetherController.TryDespawnTether: Tether.DespawnRope); that tether is traced in
    // orange (VrAim). Pointing at no tether, A interacts as usual.
    // Shown by the laser, orange instead of blue, a ring where the laser meets something, a line from where the tether
    // started to where the hand points while the trigger is held (VrAim), and by the controller: one buzz when the grapple
    // mode comes back, two for the tether mode.
    internal static class VrTetherMode
    {
        private const float PulseAmplitude = 0.4f;
        private const float PulseDuration = 0.05f;
        private const float PulseGap = 0.12f;
        // How far off the laser a tether may be and still count as pointed at, as an angle (its tangent).
        private const float TargetSlack = 0.05f;
        private const float TargetRange = 100f;

        private static readonly AccessTools.FieldRef<TetherController, List<Tether>> ActiveTethers =
            AccessTools.FieldRefAccess<TetherController, List<Tether>>("mActiveTethers");

        static VrTetherMode() => VrSession.OnUnload("tether mode", Release);

        public static bool On { get; private set; }

        private static InputDevice pendingDevice;
        private static float pendingPulseAt = -1f;
        private static bool loggedModes;

        // Where the tether being placed with each hand's trigger started, on what it hit there, so the line follows a
        // part that drifts.
        private static Transform leftOn, rightOn;
        private static Vector3 leftAt, rightAt;

        // Whether that tool works in the tether mode.
        public static bool Applies(EquipmentController.Equipment tool) => On && tool == EquipmentController.Equipment.GrappleHook;

        // B tap on the grapple.
        public static void Toggle(InputDevice device)
        {
            On = !On;
            Plugin.Log.LogInfo($"Grapple mode: {(On ? "tethers" : "grapple")}");
            LogGameModes();
            if (!device.isValid) return;
            device.SendHapticImpulse(0, PulseAmplitude, PulseDuration);
            pendingDevice = device;
            pendingPulseAt = On ? Time.unscaledTime + PulseGap : -1f;
        }

        // The second buzz of the tether mode (VrToolInput.Update, once per frame).
        public static void Update()
        {
            if (pendingPulseAt < 0f || Time.unscaledTime < pendingPulseAt) return;
            pendingPulseAt = -1f;
            if (pendingDevice.isValid) pendingDevice.SendHapticImpulse(0, PulseAmplitude, PulseDuration);
        }

        // The trigger in the tether mode, pressed while pointing at something (VrToolInput).
        public static void Begin(XRNode node, RaycastHit hit)
        {
            var on = hit.collider.transform;
            var at = on.InverseTransformPoint(hit.point);
            if (node == XRNode.LeftHand)
            {
                leftOn = on;
                leftAt = at;
            }
            else
            {
                rightOn = on;
                rightAt = at;
            }
        }

        // That trigger released, or the hand no longer works the grapple.
        public static void End(XRNode node)
        {
            if (node == XRNode.LeftHand) leftOn = null;
            else rightOn = null;
        }

        // Where the tether being placed by that hand starts (VrAim draws a line from there).
        public static bool TryGetStart(XRNode node, out Vector3 start)
        {
            var on = node == XRNode.LeftHand ? leftOn : rightOn;
            start = on != null ? on.TransformPoint(node == XRNode.LeftHand ? leftAt : rightAt) : Vector3.zero;
            return on != null;
        }

        // The tether a ray from origin along direction points at: the one whose rope passes closest to it, by angle, within
        // TargetSlack; null if none.
        public static Tether Targeted(Vector3 origin, Vector3 direction)
        {
            var controller = GameRefs.Tethers;
            var list = controller != null ? ActiveTethers(controller) : null;
            if (list == null) return null;
            Tether best = null;
            var bestSlack = TargetSlack;
            var range = TargetRange * VrCamera.WorldScale;
            foreach (var tether in list)
            {
                if (tether == null || !tether.gameObject.activeInHierarchy) continue;
                var slack = Slack(origin, direction, tether.WorldStartAttachPoint, tether.WorldEndAttachPoint, range);
                if (slack >= bestSlack) continue;
                bestSlack = slack;
                best = tether;
            }
            return best;
        }

        // How far the segment a-b passes from the ray, as the tangent of the angle seen from the origin; infinity behind
        // the origin or out of range. Sampled along the segment, which is plenty for a pointer.
        private static float Slack(Vector3 origin, Vector3 direction, Vector3 a, Vector3 b, float range)
        {
            var best = float.PositiveInfinity;
            const int steps = 32;
            for (var i = 0; i <= steps; i++)
            {
                var point = Vector3.Lerp(a, b, i / (float)steps) - origin;
                var along = Vector3.Dot(point, direction);
                if (along <= 0.05f || along > range) continue;
                var off = (point - direction * along).magnitude / along;
                if (off < best) best = off;
            }
            return best;
        }

        // A in the tether mode, pointing at that tether: it lets go, as when recalled.
        public static void LetGo(Tether tether)
        {
            Plugin.Log.LogInfo($"Grapple mode: letting go of one tether ({Vector3.Distance(tether.WorldStartAttachPoint, tether.WorldEndAttachPoint):0.0} m)");
            tether.DespawnRope();
        }

        // On unload (hot reload).
        public static void Release()
        {
            leftOn = rightOn = null;
            pendingPulseAt = -1f;
        }

        // Whether the game would cycle the grapple through more than one mode of its own, which B tap no longer reaches.
        private static void LogGameModes()
        {
            if (loggedModes) return;
            loggedModes = true;
            var gun = GameRefs.Gun;
            if (gun == null) return;
            var modes = Traverse.Create(gun).Property("HasMultipleModes");
            Plugin.Log.LogInfo($"Grapple mode: the game's grapple has {(modes.PropertyExists() && modes.GetValue<bool>() ? "several modes (magnet available, B tap does not reach it)" : "one mode")}");
        }
    }
}
