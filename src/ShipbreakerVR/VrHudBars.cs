using BBI.Unity.Game;
using BBI.Unity.Game.UI;
using HarmonyLib;
using Unity.Entities;
using UnityEngine;
using UnityEngine.UI;

namespace ShipbreakerVR
{
    // Raik (2026-11-26): the HUD's two brackets left and right of the middle (the speed under the left one, the room's
    // pressure under the right one) carry a scale of ticks that shows nothing. While VR runs:
    // - on the left scale a yellow bar, the thrust the game gives now (any input: controllers, keys, gamepad; not while
    //   braking), from 0 at the bottom to full at the top, 30 % see-through;
    // - on the right scale the air pressure where the player is (the game's 0..1, vacuum to full atmosphere), full
    //   atmosphere at three quarters of the scale ("not quite full, some 70, 80 %"), the bar's lower third red, its middle
    //   yellow, its upper green, 25 % see-through.
    // The bars are children of the brackets (so they come and go, fade and reboot with the HUD), over the ticks beside
    // the bracket's line. Plain images without sprites; their fill is the height of their rect.
    internal sealed class VrHudBars : MonoBehaviour
    {
        static VrHudBars() => VrSession.OnUnload("HUD bars", Release);

        private const float Width = 9f;
        // Off the bracket's line, and in from its ends as far as its ticks are (the ticks span ±201 of its 480).
        private const float Gap = 3f;
        private const float Inset = 39f;
        // Full atmosphere on the pressure's scale.
        private const float FullAtmosphere = 0.75f;
        private static readonly Color Thrust = new Color(1f, 0.82f, 0.1f, 0.7f);
        private static readonly Color[] Thirds =
        {
            new Color(0.95f, 0.2f, 0.15f, 0.75f),
            new Color(1f, 0.82f, 0.1f, 0.75f),
            new Color(0.25f, 0.9f, 0.35f, 0.75f),
        };

        private static VrHudBars instance;
        private static GameObject pressureRoot;
        private static int logs;

        // From ThrustControllerShare: the share of full thrust the game gives this frame.
        internal static float ThrustShare;

        private RectTransform thrustFill;
        private readonly RectTransform[] pressureFill = new RectTransform[3];
        private readonly Image[] images = new Image[4];
        private float shownThrust;
        private float shownPressure;
        private World queryWorld;
        private EntityQuery player;

        // From the postfix on VelocityUIController.Update: the bars once for this HUD.
        public static void Ensure(VelocityUIController velocity)
        {
            if (instance != null || velocity == null) return;
            var left = velocity.transform.parent != null ? velocity.transform.parent.parent : null;
            if (left == null || !left.name.StartsWith("Bracket_Left")) return;
            Transform right = null;
            var room = Object.FindObjectOfType<RoomInfoUIController>();
            if (room != null && room.transform.parent != null && room.transform.parent.parent != null && room.transform.parent.parent.name.StartsWith("Bracket_Right"))
                right = room.transform.parent.parent;
            var thrustRoot = Bar(left, true, "thrust bar");
            instance = thrustRoot.AddComponent<VrHudBars>();
            instance.thrustFill = Fill(thrustRoot.transform, Thrust, 0, out instance.images[0]);
            if (right != null)
            {
                pressureRoot = Bar(right, false, "pressure bar");
                for (var k = 0; k < 3; k++) instance.pressureFill[k] = Fill(pressureRoot.transform, Thirds[k], k, out instance.images[k + 1]);
            }
            Plugin.Log.LogInfo($"HUD bars: thrust on '{VrUtil.Path(left)}'{(right != null ? $", air pressure on '{VrUtil.Path(right)}'" : ", no place for the air pressure found")}");
        }

        private static GameObject Bar(Transform bracket, bool left, string name)
        {
            var bar = new GameObject("ShipbreakerVR " + name, typeof(RectTransform));
            bar.layer = bracket.gameObject.layer;
            var rect = (RectTransform)bar.transform;
            rect.SetParent(bracket, false);
            // The ticks lie on the HUD's outer side of the line: left of the left bracket's, right of the right one's.
            var side = left ? 1f : 0f;
            rect.anchorMin = new Vector2(side, 0f);
            rect.anchorMax = new Vector2(side, 1f);
            rect.pivot = new Vector2(side, 0.5f);
            rect.sizeDelta = new Vector2(Width, -2f * Inset);
            rect.anchoredPosition = new Vector2(left ? -Gap : Gap, 0f);
            return bar;
        }

        private static RectTransform Fill(Transform bar, Color color, int third, out Image image)
        {
            var fill = new GameObject("ShipbreakerVR fill", typeof(RectTransform), typeof(Image));
            fill.layer = bar.gameObject.layer;
            var rect = (RectTransform)fill.transform;
            rect.SetParent(bar, false);
            rect.anchorMin = new Vector2(0f, third / 3f);
            rect.anchorMax = new Vector2(1f, third / 3f);
            rect.offsetMin = rect.offsetMax = Vector2.zero;
            image = fill.GetComponent<Image>();
            image.color = color;
            image.raycastTarget = false;
            return rect;
        }

        private void LateUpdate()
        {
            var on = VrXr.IsRunning;
            foreach (var image in images)
                if (image != null && image.enabled != on) image.enabled = on;
            if (!on) return;
            var dt = Time.unscaledDeltaTime;
            shownThrust = Mathf.MoveTowards(shownThrust, Mathf.Clamp01(ThrustShare), dt * 4f);
            thrustFill.anchorMax = new Vector2(1f, shownThrust);
            if (pressureFill[0] == null) return;
            shownPressure = Mathf.MoveTowards(shownPressure, Mathf.Clamp01(Pressure()) * FullAtmosphere, dt * 1.5f);
            for (var k = 0; k < 3; k++)
            {
                var from = k / 3f;
                var to = Mathf.Clamp(shownPressure, from, (k + 1) / 3f);
                pressureFill[k].anchorMin = new Vector2(0f, from);
                pressureFill[k].anchorMax = new Vector2(1f, to);
            }
        }

        // The air pressure where the player is (the game's AirPressure on the local player, 0 outside any room).
        private float Pressure()
        {
            var world = World.DefaultGameObjectInjectionWorld;
            if (world == null || !world.IsCreated) return 0f;
            var manager = world.EntityManager;
            if (queryWorld != world)
            {
                queryWorld = world;
                player = manager.CreateEntityQuery(ComponentType.ReadOnly<SingletonLocalPlayer>());
            }
            if (player.CalculateEntityCount() != 1) return 0f;
            var entity = player.GetSingletonEntity();
            if (!manager.HasComponent<AirPressure>(entity)) return 0f;
            var value = manager.GetComponentData<AirPressure>(entity).Value;
            if (logs < 3 && value > 0f) { logs++; Plugin.Log.LogInfo($"HUD bars: air pressure {value:0.00}"); }
            return value;
        }

        private void OnDestroy()
        {
            if (instance == this) instance = null;
            if (pressureRoot != null) Destroy(pressureRoot);
            pressureRoot = null;
        }

        private static void Release()
        {
            if (instance != null) Destroy(instance.gameObject);
            if (pressureRoot != null) Destroy(pressureRoot);
            instance = null;
            pressureRoot = null;
        }
    }

    [HarmonyPatch(typeof(VelocityUIController), "Update")]
    internal static class VelocityHudBarsPatch
    {
        private static void Postfix(VelocityUIController __instance)
        {
            if (VrXr.IsRunning) VrHudBars.Ensure(__instance);
        }
    }

    // The thrust the game gives this frame, whatever gives it: its input's size while it really thrusts (draining the
    // tank, a push this frame), none while braking or with the tank held back.
    [HarmonyPatch(typeof(ThrustController), "Update")]
    internal static class ThrustControllerShare
    {
        private static readonly AccessTools.FieldRef<ThrustController, float> Magnitude = AccessTools.FieldRefAccess<ThrustController, float>("mPrevThrustMagnitude");
        private static readonly AccessTools.FieldRef<ThrustController, Vector3> Push = AccessTools.FieldRefAccess<ThrustController, Vector3>("mVelocity");
        private static readonly AccessTools.FieldRef<ThrustController, bool> Braking = AccessTools.FieldRefAccess<ThrustController, bool>("mBraking");

        private static void Postfix(ThrustController __instance)
        {
            VrHudBars.ThrustShare = __instance.CurrentChargeState == ThrustController.ChargeState.Draining && Push(__instance) != Vector3.zero && !Braking(__instance)
                ? Mathf.Clamp01(Magnitude(__instance)) : 0f;
        }
    }
}
