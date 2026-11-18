using System.Collections.Generic;
using System.Reflection;
using BBI.Unity.Game;
using HarmonyLib;
using Unity.Entities;
using UnityEngine;

namespace ShipbreakerVR
{
    // Test (Raik, 2026-11-14): fire on the suit burns until it is shaken off. The game sets the player burning for a
    // time (BurningTimerComponent on the local player's entity, counted down by BurningTimerSystem, which then ends it
    // and with it the damage) and shows flames on both arms and the helmet for a time of their own
    // (PlayerElementalHandsAndHeadController: mEffectTime, counted down in its Update). In VR the flames are held as
    // long as an arm still burns; the game's burning runs its own time with its own damage, and after it the fire
    // takes its own toll (Burn). An arm's flames go out once its hand has been shaken out, ShakesNeeded turns of the
    // controller's way at ShakeSpeed or faster, none more than ShakeGap apart; with both arms out the fire ends as when
    // its time runs out. Without both controllers (no VR) the game's own times stay.
    [HarmonyPatch(typeof(PlayerElementalHandsAndHeadController), "Update")]
    internal static class VrFire
    {
        static VrFire() => VrSession.OnUnload("fire", CancelFades);

        // Turns of the hand's way for an arm (about three shakes back and forth), from what speed in m/s a stroke
        // counts, how far a turn must swing round (cosine), and the longest pause between two, in seconds.
        // Test (Raik, 2026-11-14: five went out a little too soon): seven.
        private const int ShakesNeeded = 7;
        private const float ShakeSpeed = 0.5f;
        private const float ShakeTurn = -0.3f;
        private const float ShakeGap = 0.5f;
        // What the game's times are held at, seconds.
        private const float HoldTime = 0.5f;
        // Raik, 2026-11-18: shaken out, the flames were gone within one frame. They get this long to go out, in seconds
        // of game time. First 0.6 s with the effect only stopped (no new flames, those alight burn down): still too quick,
        // as the flames alight live only a moment. Now a second (Raik), and the flames are made smaller over it: the
        // effect burns on while its object shrinks to SmallSize of its size, is stopped after StopShare of the time and
        // shrinks on to LeastSize while what is alight burns down. Then it is switched off and has its size back.
        private const float FadeTime = 1f;
        private const float StopShare = 0.7f;
        private const float SmallSize = 0.35f;
        private const float LeastSize = 0.05f;
        // The game's own time for the flames runs this much longer than the fade, so the fade ends them, not the game.
        private const float FadeMargin = 0.2f;

        private sealed class Fading
        {
            public Behaviour Effect;
            public Transform Frame;
            public Vector3 Size;
            public float From;
            public bool Stopped;
        }

        private static readonly List<Fading> fades = new List<Fading>();
        // The game's time for the flames after the last step here: it only runs down, so more of it means a new fire.
        private static float knownTime;
        // Test (Raik, 2026-11-14: held burning, the suit took no damage): while held, the fire takes its own toll, a
        // DeathTime-th of full health each Tick seconds, so a fire never shaken off kills from full health in a minute.
        // Taken straight off the player's HealthComponent (VitalitySystem.TryModifyHealth changed nothing).
        // Test (Raik, 2026-11-14: in free play no damage at all, not even the game's own when the cutter overheated, and
        // no hands put out by the game; in the campaign the game's damage came on top of this one, killed within some
        // twelve seconds, and the game's hands slapping out the flames took the arms, so they could not be shaken):
        // - The game's burning is no longer held: it runs its own time with its own damage, the small hurt of the fire
        //   breaking out. This toll only starts once that is over, so the two never add up.
        // - Only where the game's burning hurt: a mode that deals no damage (free play) gets none from this either.
        // - VrFireHands keeps the game's hands from putting the flames out while they burn.
        // - At no health left the flames go out.
        private const float DeathTime = 60f;
        private const float Tick = 1f;
        private static float nextTick;
        private static float startHealth;
        private static bool gameBurnOver;
        private static bool hurts;

        private static readonly AccessTools.FieldRef<PlayerElementalHandsAndHeadController, ElementalType> Playing =
            AccessTools.FieldRefAccess<PlayerElementalHandsAndHeadController, ElementalType>("mCurrentPlayingFX");
        private static readonly AccessTools.FieldRef<PlayerElementalHandsAndHeadController, float> EffectTime =
            AccessTools.FieldRefAccess<PlayerElementalHandsAndHeadController, float>("mEffectTime");
        private static readonly FieldInfo EffectsField = AccessTools.Field(typeof(PlayerElementalHandsAndHeadController), "m_ElementalFX");

        private sealed class Side
        {
            public readonly bool Left;
            public bool Out;
            public int Shakes;
            public Vector3 Way;
            public float LastShakeAt;
            public Vector3 LastAt;
            public bool Known;
            public Side(bool left) { Left = left; }
            public VrInput.Hand Input => Left ? VrInput.Left : VrInput.Right;
        }

        private static readonly Side LeftSide = new Side(true);
        private static readonly Side RightSide = new Side(false);
        private static bool burning;
        private static bool held;
        private static float startedAt;

        private static void Prefix(PlayerElementalHandsAndHeadController __instance)
        {
            // Set alight again while it burns or goes out (the game switches all flames on anew): a fire of its own.
            if (burning && Playing(__instance) == ElementalType.Fire && EffectTime(__instance) > knownTime + 0.05f)
            {
                burning = false;
                Plugin.Log.LogInfo("Fire: set alight again");
            }
            Step(__instance);
            knownTime = EffectTime(__instance);
        }

        private static void Step(PlayerElementalHandsAndHeadController __instance)
        {
            if (Playing(__instance) != ElementalType.Fire)
            {
                burning = false;
                CancelFades();
                return;
            }
            if (!burning)
            {
                burning = true;
                CancelFades();
                startedAt = Time.unscaledTime;
                Reset(LeftSide);
                Reset(RightSide);
                held = VrXr.IsRunning && VrInput.Left.TryGetPosition(out _) && VrInput.Right.TryGetPosition(out _);
                nextTick = Time.time + Tick;
                gameBurnOver = false;
                hurts = false;
                startHealth = Health(__instance, out _, out _);
                var timer = Timer(__instance);
                Plugin.Log.LogInfo(held
                    ? $"Fire: the suit burns until both hands are shaken out; health {startHealth:0.0}, the game's burning {(timer >= 0f ? $"{timer:0.0} s" : "not found")}"
                    : "Fire: the suit burns for the game's time (no controllers to shake it off with)");
            }
            Fade();
            if (!held) return;
            if (!VrXr.IsRunning)
            {
                held = false;
                return;
            }
            Shake(__instance, LeftSide);
            Shake(__instance, RightSide);
            if (LeftSide.Out && RightSide.Out)
            {
                // The last flames go out over FadeTime, the helmet's with the arm's; the game's time for them ends a
                // little after. The burning itself, with its damage, ends now.
                held = false;
                FadeOut(Effect(__instance, "HeadEffect"));
                EffectTime(__instance) = FadeTime + FadeMargin;
                SetTimer(__instance, 0f);
                VrInput.Left.Buzz(0.6f, 0.15f);
                VrInput.Right.Buzz(0.6f, 0.15f);
                Plugin.Log.LogInfo($"Fire: shaken off after {Time.unscaledTime - startedAt:0.0} s");
                return;
            }
            if (EffectTime(__instance) < HoldTime) EffectTime(__instance) = HoldTime;
            var health = Health(__instance, out var least, out _);
            if (health >= 0f && health <= least + 0.001f)
            {
                // Nothing left to burn.
                held = false;
                EffectTime(__instance) = 0f;
                Plugin.Log.LogInfo($"Fire: out, no health left ({Time.unscaledTime - startedAt:0.0} s)");
                return;
            }
            if (Timer(__instance) > 0f)
            {
                // The game's own burning (again): its damage, not this one.
                nextTick = Time.time + Tick;
                return;
            }
            if (!gameBurnOver)
            {
                gameBurnOver = true;
                hurts = health < startHealth - 0.01f;
                Plugin.Log.LogInfo(hurts
                    ? $"Fire: the game's burning over after {Time.unscaledTime - startedAt:0.0} s, it took {startHealth - health:0.0} health; burns on at {100f / DeathTime:0.0} % a second"
                    : $"Fire: the game's burning over after {Time.unscaledTime - startedAt:0.0} s and took no health (health {health:0.0}): no damage in this mode, nor from the flames after");
            }
            // Game time: no damage while paused.
            if (hurts && Time.time >= nextTick)
            {
                nextTick = Time.time + Tick;
                Burn(__instance, Tick / DeathTime);
            }
        }

        // The player's health now, its least and most; -1 without one.
        private static float Health(PlayerElementalHandsAndHeadController controller, out float least, out float most)
        {
            least = 0f;
            most = 0f;
            if (!controller.TryGetSingletonEntity<SingletonLocalPlayer>(out EntityManager manager, out Entity player)) return -1f;
            if (!manager.Exists(player) || !manager.HasComponent<HealthComponent>(player)) return -1f;
            var health = manager.GetComponentData<HealthComponent>(player);
            least = health.Min;
            most = health.Max;
            return health.Current;
        }

        // The game's burning time left; -1 without one.
        private static float Timer(PlayerElementalHandsAndHeadController controller)
        {
            if (!controller.TryGetSingletonEntity<SingletonLocalPlayer>(out EntityManager manager, out Entity player)) return -1f;
            if (!manager.Exists(player) || !manager.HasComponent<BurningTimerComponent>(player)) return -1f;
            return manager.GetComponentData<BurningTimerComponent>(player).RemainingTime;
        }

        // Whether the player burns with both hands to shake it off with, the game's hands kept off it (VrFireHands).
        public static bool Shaking => held && burning;

        private static int burnLogs;

        // That share of full health off the player's health, no lower than its least.
        private static void Burn(PlayerElementalHandsAndHeadController controller, float share)
        {
            if (!controller.TryGetSingletonEntity<SingletonLocalPlayer>(out EntityManager manager, out Entity player)) return;
            if (!manager.Exists(player) || !manager.HasComponent<HealthComponent>(player)) return;
            var health = manager.GetComponentData<HealthComponent>(player);
            var before = health.Current;
            health.Current = Mathf.Max(health.Min, before - health.Max * share);
            manager.SetComponentData(player, health);
            if (burnLogs++ < 70)
                Plugin.Log.LogInfo($"Fire: burns {before - health.Current:0.0} of {health.Max:0} health ({health.Current:0.0} left)");
        }

        private static void Reset(Side side)
        {
            side.Out = false;
            side.Shakes = 0;
            side.Way = Vector3.zero;
            side.Known = false;
        }

        // The controller's speed in the playspace (the body's own frame, so flying does not count): each time it swings
        // round at ShakeSpeed or faster counts a shake.
        private static void Shake(PlayerElementalHandsAndHeadController controller, Side side)
        {
            if (side.Out || !side.Input.TryGetPosition(out var at)) return;
            var dt = Time.unscaledDeltaTime;
            var now = Time.unscaledTime;
            var speed = side.Known && dt > 1e-4f ? (at - side.LastAt) / dt : Vector3.zero;
            side.LastAt = at;
            side.Known = true;
            if (side.Shakes > 0 && now - side.LastShakeAt > ShakeGap)
            {
                side.Shakes = 0;
                side.Way = Vector3.zero;
            }
            if (speed.magnitude < ShakeSpeed) return;
            var way = speed.normalized;
            if (side.Way == Vector3.zero)
            {
                side.Way = way;
                side.LastShakeAt = now;
                return;
            }
            if (Vector3.Dot(way, side.Way) > ShakeTurn) return;
            side.Way = way;
            side.Shakes++;
            side.LastShakeAt = now;
            side.Input.Buzz(0.2f, 0.03f);
            if (side.Shakes < ShakesNeeded) return;
            side.Out = true;
            StopArm(controller, side);
            side.Input.Buzz(0.5f, 0.1f);
            Plugin.Log.LogInfo($"Fire: {(side.Left ? "left" : "right")} hand shaken out");
        }

        // That arm's flames go out (FadeOut); its next fire turns them on again.
        private static void StopArm(PlayerElementalHandsAndHeadController controller, Side side) =>
            FadeOut(Effect(controller, side.Left ? "LeftArmEffect" : "RightArmEffect"));

        // That effect's flames go out over FadeTime (Fade), then it is off as the game puts it out (Stop, the object off).
        private static void FadeOut(Behaviour effect)
        {
            if (effect == null) return;
            foreach (var fade in fades)
                if (fade.Effect == effect) return;
            fades.Add(new Fading { Effect = effect, Frame = effect.transform, Size = effect.transform.localScale, From = Time.time });
        }

        // Each frame while the fire plays: the effects going out shrink, are stopped, and at last switched off.
        private static void Fade()
        {
            for (var i = fades.Count - 1; i >= 0; i--)
            {
                var fade = fades[i];
                if (fade.Effect == null)
                {
                    fades.RemoveAt(i);
                    continue;
                }
                var t = (Time.time - fade.From) / FadeTime;
                if (t >= 1f)
                {
                    Stop(fade.Effect);
                    fade.Effect.gameObject.SetActive(false);
                    fade.Frame.localScale = fade.Size;
                    fades.RemoveAt(i);
                    continue;
                }
                if (!fade.Stopped && t >= StopShare)
                {
                    fade.Stopped = true;
                    Stop(fade.Effect);
                }
                var size = t < StopShare
                    ? Mathf.Lerp(1f, SmallSize, t / StopShare)
                    : Mathf.Lerp(SmallSize, LeastSize, (t - StopShare) / (1f - StopShare));
                fade.Frame.localScale = fade.Size * size;
            }
        }

        // The effects going out get their size back and are left to the game: a new fire, the fire over, or unloading.
        private static void CancelFades()
        {
            foreach (var fade in fades)
                if (fade.Effect != null) fade.Frame.localScale = fade.Size;
            fades.Clear();
        }

        // One of the fire's effects (LeftArmEffect, RightArmEffect, HeadEffect), a VisualEffect; null without one.
        private static Behaviour Effect(PlayerElementalHandsAndHeadController controller, string name)
        {
            if (!(EffectsField?.GetValue(controller) is System.Array effects) || effects.Length <= (int)ElementalType.Fire) return null;
            var fx = effects.GetValue((int)ElementalType.Fire);
            if (fx == null) return null;
            return AccessTools.Field(fx.GetType(), name)?.GetValue(fx) is Behaviour effect && effect != null ? effect : null;
        }

        private static void Stop(Behaviour effect)
        {
            if (effect != null) AccessTools.Method(effect.GetType(), "Stop", new System.Type[0])?.Invoke(effect, null);
        }

        private static int timerLogs;

        // The player's burning time set to that.
        private static void SetTimer(PlayerElementalHandsAndHeadController controller, float time)
        {
            if (!controller.TryGetSingletonEntity<SingletonLocalPlayer>(out EntityManager manager, out Entity player)) return;
            if (!manager.Exists(player) || !manager.HasComponent<BurningTimerComponent>(player))
            {
                if (timerLogs++ < 3) Plugin.Log.LogInfo("Fire: the player has no burning time to end");
                return;
            }
            var timer = manager.GetComponentData<BurningTimerComponent>(player);
            if (timer.RemainingTime <= time) return;
            timer.RemainingTime = time;
            manager.SetComponentData(player, timer);
        }
    }

    // Test (Raik, 2026-11-14: in the campaign the game's hands slapped out the flames on their own, so they could not
    // be shaken out): the game starts that on both hands for each damage of a type it has an animation for
    // (GrabController.UpdateDamageAnimations, from the player's DamageHistory). Left out while the player burns with
    // both hands to shake it off with; other damage keeps its animation.
    [HarmonyPatch(typeof(GrabController), "UpdateDamageAnimations")]
    internal static class VrFireHands
    {
        private static int logs;

        private static bool Prefix(Entity playerEntity, EntityManager entityManager)
        {
            if (!VrXr.IsRunning || !VrInput.Left.TryGetPosition(out _) || !VrInput.Right.TryGetPosition(out _)) return true;
            var burning = VrFire.Shaking;
            if (!burning && entityManager.Exists(playerEntity) && entityManager.HasComponent<BurningTimerComponent>(playerEntity))
                burning = entityManager.GetComponentData<BurningTimerComponent>(playerEntity).RemainingTime > 0f;
            if (!burning) return true;
            if (logs++ < 3) Plugin.Log.LogInfo("Fire: the game's hands left off putting out the flames, they are shaken out");
            return false;
        }
    }
}
