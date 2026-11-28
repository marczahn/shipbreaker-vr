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
    // - on the left scale the speed (Raik, 2026-11-27, instead of the thrust it showed first): the player's speed as the
    //   game's number under it gives it, full at the top for the fastest the jetpack gets with the best thrusters
    //   (BUFF_Thruster_Speed: 3.5 m/s free; more if the thrusters in use give more), yellow, 30 % see-through; red from
    //   the speed at which a crash hurts (the player's ImpactDamageRangeAsset, the lowest of its ranges: 8.51 m/s
    //   against parts from 100 kg), where the bar stays full;
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
        private static readonly Color Speed = new Color(1f, 0.82f, 0.1f, 0.7f);
        private static readonly Color Danger = new Color(0.95f, 0.2f, 0.15f, 0.75f);
        // The fastest the jetpack gets, fully upgraded (BUFF_Thruster_Speed adds 3.5 m/s to the thrusters' 0), and the
        // crash that hurts, should the game's own not be found (Player_ImpactDamageRangeAsset).
        private const float JetpackTop = 3.5f;
        private const float HurtsFallback = 8.51f;
        private static readonly Color[] Thirds =
        {
            new Color(0.95f, 0.2f, 0.15f, 0.75f),
            new Color(1f, 0.82f, 0.1f, 0.75f),
            new Color(0.25f, 0.9f, 0.35f, 0.75f),
        };

        private static VrHudBars instance;
        private static GameObject pressureRoot;
        private static int logs;

        // From ThrustControllerTop: the top speed of the thrusters in use, free of the grapple.
        internal static float ThrustersTop;

        private RectTransform speedFill;
        private readonly RectTransform[] pressureFill = new RectTransform[3];
        private readonly Image[] images = new Image[4];
        private Rigidbody body;
        private float hurts;
        private float shownSpeed;
        private bool red;
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
            var speedRoot = Bar(left, true, "speed bar");
            instance = speedRoot.AddComponent<VrHudBars>();
            instance.speedFill = Fill(speedRoot.transform, Speed, 0, out instance.images[0]);
            instance.body = BodyOf(velocity);
            instance.hurts = Hurts(MotionOf(velocity));
            if (right != null)
            {
                pressureRoot = Bar(right, false, "pressure bar");
                for (var k = 0; k < 3; k++) instance.pressureFill[k] = Fill(pressureRoot.transform, Thirds[k], k, out instance.images[k + 1]);
            }
            Plugin.Log.LogInfo($"HUD bars: speed on '{VrUtil.Path(left)}' (red from {instance.hurts:0.00} m/s){(right != null ? $", air pressure on '{VrUtil.Path(right)}'" : ", no place for the air pressure found")}");
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
            var speed = body != null ? body.velocity.magnitude : 0f;
            shownSpeed = Mathf.MoveTowards(shownSpeed, Mathf.Clamp01(speed / Mathf.Max(JetpackTop, ThrustersTop)), dt * 4f);
            speedFill.anchorMax = new Vector2(1f, shownSpeed);
            var hurt = speed >= hurts;
            if (hurt != red)
            {
                red = hurt;
                images[0].color = red ? Danger : Speed;
            }
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

        private static readonly AccessTools.FieldRef<VelocityUIController, Rigidbody> BodyOf =
            AccessTools.FieldRefAccess<VelocityUIController, Rigidbody>("m_PlayerRigidBodyReference");
        private static readonly AccessTools.FieldRef<VelocityUIController, PlayerMotion> MotionOf =
            AccessTools.FieldRefAccess<VelocityUIController, PlayerMotion>("m_PlayerMotionReference");
        private static readonly AccessTools.FieldRef<PlayerMotion, ImpactDamageRangeAsset> Ranges =
            AccessTools.FieldRefAccess<PlayerMotion, ImpactDamageRangeAsset>("m_ImpactDamageRanges");

        // The lowest speed at which a crash hurts the player, as the game checks it (ImpactDamageRangeAsset.CanDamageEntity);
        // the range is read by name, its type (RangeDefinition) lives in an assembly the mod does not build against.
        private static float Hurts(PlayerMotion motion)
        {
            var asset = motion != null ? Ranges(motion) : null;
            var ranges = asset != null && asset.Data != null ? asset.Data.DamageRanges : null;
            var lowest = float.MaxValue;
            if (ranges != null)
                foreach (var range in ranges)
                    if (range != null && range.DamageType != null && range.VitalityType != null) lowest = Mathf.Min(lowest, Traverse.Create(range).Property("VelocityRange").Field("Min").GetValue<float>());
            return lowest < float.MaxValue && lowest > 0f ? lowest : HurtsFallback;
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

    // The top speed of the thrusters in use (their upgrades included), free of the grapple, for the speed's scale.
    [HarmonyPatch(typeof(ThrustController), "Update")]
    internal static class ThrustControllerTop
    {
        private static readonly AccessTools.FieldRef<ThrustController, IThrusterData> Data = AccessTools.FieldRefAccess<ThrustController, IThrusterData>("mData");

        private static void Postfix(ThrustController __instance)
        {
            var data = Data(__instance);
            VrHudBars.ThrustersTop = data != null ? data.MaxThrustSpeedFree : 0f;
        }
    }
}
