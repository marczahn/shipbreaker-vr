using System.Reflection;
using BBI.Unity.Game;
using BepInEx.Configuration;
using HarmonyLib;
using Unity.Entities;
using Unity.Mathematics;
using UnityEngine;

namespace ShipbreakerVR
{
    // Experiment (Raik, 2026-11-18): demo charges are put on by hand instead of being aimed. The game finds the place for
    // a charge with a ray from the camera (DemoChargeController.TryGetNewTarget, some meters far; the hand's aim camera in
    // VR), turns it with the camera's up and the cut toggle, and throws it where the camera looks when the ray finds
    // nothing. With this on, for the extended way of working the tools:
    // - The ray leaves the charge in the hand through its back and reaches only Reach beyond it: the charge has to be
    //   brought to the wall. There the game's own preview shows, and the trigger puts the charge on as the game does.
    // - The charge goes on as it is held: the line on it shows the cut. The game cuts along the right axis of the placed
    //   charge (DemoChargeAsset draws the cut's sheet across its up), and the charge in the hand is the same mesh as the
    //   placed one, unturned in its prefab (PRF_Demo_Charge / SM_Props_Demo_Charge). So the placing frame gets the
    //   hand charge's up, and the cut toggle is kept on its first setting.
    // - Away from a wall the trigger lets the charge go where it is, as it is turned, with the hand's own motion (the
    //   game's throw without its fling): it floats, and a moved hand throws it.
    // - Opening the grip away from the charges' place on the body lets one charge go the same way, onto the wall or
    //   afloat, and then puts the charges away (VrToolInput.ChargeLetGo).
    // The game's waiting times before the charge appears (for its hand animations) are left out: the charge leaves the
    // hand at once. Off, the charges are aimed and thrown as before.
    // The game's code asks for the camera through VrAim (AimPatch); inside DemoChargeController.Update it gets the placing
    // frame here instead.
    internal static class VrChargeByHand
    {
        static VrChargeByHand() => VrSession.OnUnload("charge by hand", Release);

        // How far beyond the charge's back a wall may be, in meters at world scale 1.
        private const float Reach = 0.15f;
        // The mesh's origin lies this far in front of its back (SM_Props_Demo_Charge sits at z 0.03 in its prefab), and the
        // ray starts this far in front of the origin, so a charge pressed into the wall still finds it.
        private const float BackOfMesh = 0.03f;
        private const float Lead = 0.10f;
        // What stays below these counts as a hand at rest, and the hand throws no faster than this (m/s, degrees/s).
        private const float Drift = 0.1f;
        private const float TurnDrift = 60f;
        private const float MostThrow = 8f;
        private const string MeshName = "SM_Props_Demo_Charge";

        private static readonly AccessTools.FieldRef<DemoChargeController, DemoChargeState> State =
            AccessTools.FieldRefAccess<DemoChargeController, DemoChargeState>("mState");
        private static readonly AccessTools.FieldRef<DemoChargeController, bool> Horizontal =
            AccessTools.FieldRefAccess<DemoChargeController, bool>("mHorizontalCutNext");
        private static readonly MethodInfo ToggleCut = AccessTools.Method(typeof(DemoChargeController), "ToggleCutRotation");
        private static readonly FieldInfo Timer = AccessTools.Field(typeof(DemoChargeController), "mTimerInfo");

        private static ConfigEntry<bool> enabled;
        private static bool bound;
        private static DemoChargeController controller;
        private static Transform mesh;
        private static Renderer meshRenderer;
        private static float nextFind;
        private static Transform frame, throwFrame;
        // Inside DemoChargeController.Update, with the placing frame set.
        private static bool inside;
        private static Vector3 launch;
        private static float launchUntil;
        private static int logs;

        // When a charge last left the hand (the controller went on to its cooldown), in unscaled time.
        public static float OutAt { get; private set; } = -10f;

        private static bool On
        {
            get
            {
                if (!bound && VrSettings.File != null)
                {
                    bound = true;
                    enabled = VrSettings.Bind("Experimente", "SprengsatzVonHand", true,
                        "Experiment: demo charges are put on by hand. The charge has to be brought to the wall, it goes on as it is held (the line on it shows the cut), "
                        + "and away from a wall it is let go afloat instead of thrown. Off: aimed and thrown as before.");
                }
                return enabled != null && enabled.Value && VrAim.Active;
            }
        }

        // The charge in the hand, as the game's hand holds it (moved over to the left hand by VrHands).
        private static bool FindMesh()
        {
            if (mesh != null) return true;
            if (Time.unscaledTime < nextFind) return false;
            nextFind = Time.unscaledTime + 2f;
            var grab = GameRefs.Grab;
            if (grab == null) return false;
            foreach (var filter in grab.transform.root.GetComponentsInChildren<MeshFilter>(true))
            {
                if (filter.name != MeshName) continue;
                mesh = filter.transform;
                meshRenderer = filter.GetComponent<Renderer>();
                Plugin.Log.LogInfo($"Charge by hand: the charge in the hand is '{VrUtil.Chain(mesh, 5)}'");
                return true;
            }
            return false;
        }

        private static bool ChargesInHand()
        {
            var equipment = GameRefs.Equipment;
            return equipment != null && !equipment.IsNothingEquipped && equipment.CurrentEquipment == EquipmentController.Equipment.DemoCharge;
        }

        // ---- Around DemoChargeController.Update (ChargeByHandPatch) ----

        public static void Enter(DemoChargeController instance)
        {
            controller = instance;
            inside = false;
            if (!On || !FindMesh() || !ChargesInHand()) return;
            if (frame == null) frame = new GameObject("ShipbreakerVR Charge frame").transform;
            var scale = VrCamera.WorldScale;
            // Looking through the charge's back, with the charge's up.
            frame.SetPositionAndRotation(mesh.position + mesh.forward * (Lead * scale), Quaternion.LookRotation(-mesh.forward, mesh.up));
            // The line on the charge is the cut: the toggle stays on its first setting.
            if (!Horizontal(instance) && ToggleCut != null) ToggleCut.Invoke(instance, null);
            inside = true;
        }

        public static void Leave() => inside = false;

        // From VrAim.CameraTransformFor: what the charges' code gets as the camera.
        public static bool Aims(out Transform from)
        {
            from = inside ? frame : null;
            return from != null;
        }

        // From ChargeReachPatch: whether a wall that far along the ray is near enough.
        public static bool Within(float distance) => !inside || distance <= (Lead + BackOfMesh + Reach) * VrCamera.WorldScale;

        // From ChargeStatePatch: the controller went on to that state.
        public static void StateSet(DemoChargeController instance, DemoChargeState state)
        {
            if (state == DemoChargeState.Cooldown) OutAt = Time.unscaledTime;
            if (!inside || (state != DemoChargeState.Placing && state != DemoChargeState.Throwing) || State(instance) != state || Timer == null) return;
            // No wait for the game's hand animation: the charge leaves the hand on the next frame.
            var timer = Timer.GetValue(instance);
            var delay = timer != null ? AccessTools.Field(timer.GetType(), "Delay") : null;
            if (delay == null) return;
            delay.SetValue(timer, 0f);
            Timer.SetValue(instance, timer);
            if (logs++ < 40) Plugin.Log.LogInfo($"Charge by hand: {(state == DemoChargeState.Placing ? "onto the wall" : "let go afloat")}");
        }

        // From VrAim.ThrowOrigin (DemoChargeController.ThrowDemoCharge): a charge let go starts where the hand has it, as
        // it is turned, and takes the hand's motion along (Launch).
        public static bool ThrowFrom(out Transform from)
        {
            from = null;
            if (!inside || mesh == null) return false;
            if (throwFrame == null) throwFrame = new GameObject("ShipbreakerVR Charge let go").transform;
            var scale = VrCamera.WorldScale;
            throwFrame.SetPositionAndRotation(mesh.position - mesh.forward * (BackOfMesh * scale), mesh.rotation);
            var player = VrToolCollide.Player();
            launch = player != null ? player.velocity : Vector3.zero;
            if (VrClimb.LetGoMotion(VrHand.GameToolHand.Input, Drift * scale, TurnDrift, out var speed, out _))
                launch += Vector3.ClampMagnitude(speed, MostThrow * scale);
            launchUntil = Time.unscaledTime + 2f;
            from = throwFrame;
            return true;
        }

        // From ChargeLaunchPatch (DemoChargeAsset.SetComponentData, as the new charge is set up): its starting speed is the
        // hand's, not the game's throw.
        public static void Launch(EntityManager manager, Entity entity)
        {
            if (Time.unscaledTime > launchUntil) return;
            launchUntil = 0f;
            if (!manager.Exists(entity) || !manager.HasComponent<InitDemoChargeVelocity>(entity)) return;
            var speed = launch.magnitude;
            var way = speed > 1e-4f ? launch / speed : Vector3.forward;
            manager.SetComponentData(entity, new InitDemoChargeVelocity
            {
                Speed = speed,
                Offset = 0f,
                OffsetAxis = float3.zero,
                Direction = new float3(way.x, way.y, way.z),
            });
            if (logs++ < 40) Plugin.Log.LogInfo($"Charge by hand: let go at {speed / VrCamera.WorldScale:0.00} m/s in the world");
        }

        // ---- For VrToolInput ----

        // Whether opening that hand's grip now lets a charge go rather than only putting the charges away: put on by
        // hand, a charge ready in the hand, and the hand not at the charges' place on the body.
        public static bool LetsGo(VrInput.Hand hand)
        {
            if (!On || controller == null || !FindMesh() || !ChargesInHand()) return false;
            if (State(controller) != DemoChargeState.Placement) return false;
            if (meshRenderer != null && (!meshRenderer.enabled || !meshRenderer.gameObject.activeInHierarchy)) return false;
            return !VrToolBelt.Stows(VrToolBelt.Zone.DemoCharge, hand);
        }

        // On unload (hot reload).
        public static void Release()
        {
            inside = false;
            if (frame != null) Object.Destroy(frame.gameObject);
            if (throwFrame != null) Object.Destroy(throwFrame.gameObject);
            frame = throwFrame = null;
            mesh = null;
            meshRenderer = null;
            controller = null;
        }
    }

    [HarmonyPatch(typeof(DemoChargeController), "Update")]
    internal static class ChargeByHandPatch
    {
        private static void Prefix(DemoChargeController __instance) => VrChargeByHand.Enter(__instance);

        private static void Finalizer() => VrChargeByHand.Leave();
    }

    // A wall counts only within reach of the charge in the hand.
    [HarmonyPatch(typeof(DemoChargeController), nameof(DemoChargeController.TryGetNewTarget))]
    internal static class ChargeReachPatch
    {
        private static void Postfix(ref bool __result, ref StructurePart target, ref RaycastHit hitInfo)
        {
            if (!__result || VrChargeByHand.Within(hitInfo.distance)) return;
            __result = false;
            target = null;
        }
    }

    [HarmonyPatch(typeof(DemoChargeController), "SetState")]
    internal static class ChargeStatePatch
    {
        private static void Postfix(DemoChargeController __instance, DemoChargeState newState) => VrChargeByHand.StateSet(__instance, newState);
    }

    [HarmonyPatch(typeof(DemoChargeAsset), nameof(DemoChargeAsset.SetComponentData))]
    internal static class ChargeLaunchPatch
    {
        private static void Postfix(EntityManager entityManager, Entity entity) => VrChargeByHand.Launch(entityManager, entity);
    }
}
