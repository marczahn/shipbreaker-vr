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
        private static readonly MethodInfo AnyCharges = AccessTools.PropertyGetter(typeof(DemoChargeController), "AnyDemoCharges");
        private static readonly MethodInfo Instantiate =
            AccessTools.Method(typeof(DemoChargeController), "InstantiateDemoCharge", new[] { typeof(Vector3), typeof(Quaternion) });
        private static readonly MethodInfo ModifyCount = AccessTools.Method(typeof(DemoChargeController), "ModifyDemoChargeCount");
        // The hand's fit is measured for the log every so often while a charge is held (Raik, 2026-11-18: the hand and the
        // charge are turned against the controller).
        private static float nextMeasure;
        private static int measures;
        private static readonly AccessTools.FieldRef<DemoChargeRetrievalController, bool> Deployed =
            AccessTools.FieldRefAccess<DemoChargeRetrievalController, bool>("mIsDeployed");
        // A charge afloat with what it hangs on weighs 1.6 kg; a part of the ship with a charge on it is not taken for one.
        private const float HeaviestCharge = 5f;

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
            Measure();
            // The line on the charge is the cut: the toggle stays on its first setting.
            if (!Horizontal(instance) && ToggleCut != null) ToggleCut.Invoke(instance, null);
            inside = true;
        }

        public static void Leave() => inside = false;

        // How the charge in the hand lies against the controller, for the log: where its back and its top point in the
        // controller's own axes (x right, y up, z along its aim), and the back's turn off the aim. From that the hand's
        // fit (VrToolFit, SprengsatzRechts) can be set so the back points where the controller does.
        private static void Measure()
        {
            if (measures >= 40 || Time.unscaledTime < nextMeasure) return;
            var rig = VrCamera.Instance != null ? VrCamera.Instance.transform.parent : null;
            var hand = VrHand.GameToolHand;
            if (rig == null || !hand.Input.TryGetAimRotation(out var aimLocal) || !hand.Input.TryGetPosition(out var gripLocal)) return;
            nextMeasure = Time.unscaledTime + 1.5f;
            measures++;
            var toController = Quaternion.Inverse(rig.rotation * aimLocal);
            var back = toController * -mesh.forward;
            var top = toController * mesh.up;
            var off = toController * (mesh.position - rig.TransformPoint(gripLocal)) / VrCamera.WorldScale * 100f;
            var yaw = Mathf.Atan2(back.x, back.z) * Mathf.Rad2Deg;
            var pitch = Mathf.Asin(Mathf.Clamp(back.y, -1f, 1f)) * Mathf.Rad2Deg;
            Plugin.Log.LogInfo($"Charge by hand: fit in the {hand.Name} hand: the charge's back points {yaw:0}° to the right and {pitch:0}° up of the controller's aim "
                + $"(back {back:F2}, top {top:F2} in the controller's axes), its middle at {off:F0} cm from the grip");
        }

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

        // For VrChargeDisarm: one charge from the stock let go at that place and turn, at rest against the player's body;
        // what the game does for a thrown charge (ThrowDemoCharge), without its fling. Null if it could not.
        public static StructurePart LetGoFromStock(Vector3 at, Quaternion turned)
        {
            var instance = controller != null ? controller : Object.FindObjectOfType<DemoChargeController>();
            if (instance == null || Instantiate == null) return null;
            var player = VrToolCollide.Player();
            launch = player != null ? player.velocity : Vector3.zero;
            launchUntil = Time.unscaledTime + 2f;
            var part = Instantiate.Invoke(instance, new object[] { at, turned }) as StructurePart;
            if (part == null) return null;
            JointingService.Instance.JointStructureParts(part);
            ModifyCount?.Invoke(instance, new object[] { -1, false, false });
            return part;
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

        // From VrToolInput.Draw: the grip pressed behind the lower back (the charges) or at the hip (their detonator). The
        // two are one tool of the game's in two modes, which it changes between Placement and Detonation only, and to the
        // charges only while there are some. False when there is nothing to draw.
        public static bool Draws(bool detonator)
        {
            var instance = controller != null ? controller : Object.FindObjectOfType<DemoChargeController>();
            if (instance == null) return true;
            controller = instance;
            var state = State(instance);
            if (detonator)
            {
                if (state == DemoChargeState.Placement) instance.SetMode(DemoChargeMode.Detonation);
                return true;
            }
            if (state != DemoChargeState.Detonation) return true;
            if (!(AnyCharges?.Invoke(instance, null) is bool any) || !any)
            {
                if (logs++ < 40) Plugin.Log.LogInfo("Charge by hand: no demo charges left behind the back");
                return false;
            }
            instance.SetMode(DemoChargeMode.Placement);
            return true;
        }

        // ---- A charge afloat: left alone by the arm that let it go, and put back behind the back ----

        private sealed class Afloat
        {
            public Rigidbody Body;
            public DemoChargeRetrievalController Charge;
            public bool AtPlace;
        }

        private static readonly Afloat[] afloat = { new Afloat(), new Afloat() };
        private static StructurePart fresh;
        private static float freshUntil;

        // From ChargeFreshPatch: the game has just made that charge. Its body comes with its jointing, a moment later.
        public static void Fresh(StructurePart part)
        {
            fresh = part;
            freshUntil = Time.unscaledTime + 1f;
        }

        // From VrFrame, once a frame after the hands held on or let go (VrClimb).
        public static void Track()
        {
            if (fresh != null)
            {
                var body = fresh.GetComponentInParent<Rigidbody>();
                if (body != null && !body.isKinematic)
                {
                    VrArmPush.Spare(body);
                    fresh = null;
                }
                else if (Time.unscaledTime > freshUntil) fresh = null;
            }
            Check(VrInput.Left, afloat[0]);
            Check(VrInput.Right, afloat[1]);
        }

        // A charge afloat that a hand holds (as any loose part, VrClimb) and lets go where the charges are drawn from goes
        // back to them: the game's own way of taking a charge back (DemoChargeRetrievalController.OnSuccess: one more
        // charge, this one removed). A light buzz tells the hand is there.
        private static void Check(VrInput.Hand hand, Afloat held)
        {
            var body = VrXr.IsRunning ? VrClimb.HeldBody(hand) : null;
            if (body != null && !body.isKinematic)
            {
                if (body != held.Body)
                {
                    held.Body = body;
                    held.AtPlace = false;
                    held.Charge = body.mass <= HeaviestCharge ? body.GetComponentInChildren<DemoChargeRetrievalController>(true) : null;
                    if (held.Charge != null && Deployed(held.Charge)) held.Charge = null;
                }
                if (held.Charge == null) return;
                var at = VrToolBelt.Stows(VrToolBelt.Zone.DemoCharge, hand);
                if (at && !held.AtPlace) hand.Buzz(0.25f, 0.04f);
                held.AtPlace = at;
                return;
            }
            var was = held.Charge;
            var wasBody = held.Body;
            var wasAt = held.AtPlace;
            held.Charge = null;
            held.Body = null;
            held.AtPlace = false;
            // Let go with the grip (not lost by tracking), and not still held by the other hand.
            if (was == null || hand.Grip.Down) return;
            if (wasBody != null && VrClimb.HeldBody(hand.IsLeft ? VrInput.Right : VrInput.Left) == wasBody) return;
            if (!wasAt && !VrToolBelt.Stows(VrToolBelt.Zone.DemoCharge, hand))
            {
                if (logs++ < 40) Plugin.Log.LogInfo($"Charge by hand: a charge afloat let go by the {hand.Name} hand at {VrToolBelt.ChargePlace(hand)}, not behind the back");
                return;
            }
            was.OnSuccess();
            hand.Buzz(1f, 0.15f);
            if (logs++ < 40) Plugin.Log.LogInfo($"Charge by hand: a charge afloat put back behind the back by the {hand.Name} hand");
        }

        // How far a point in the world is from the charge showing in the hand (its middle); MaxValue without one. Whether
        // or not the charges are put on by hand.
        public static float Distance(Vector3 point)
        {
            if (!FindMesh() || !ChargesInHand()) return float.MaxValue;
            if (meshRenderer != null && (!meshRenderer.enabled || !meshRenderer.gameObject.activeInHierarchy)) return float.MaxValue;
            return (point - mesh.position).magnitude;
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

    [HarmonyPatch(typeof(DemoChargeController), "InstantiateDemoCharge", new[] { typeof(Vector3), typeof(Quaternion), typeof(Entity) })]
    internal static class ChargeFreshPatch
    {
        private static void Postfix(StructurePart __result)
        {
            if (__result != null) VrChargeByHand.Fresh(__result);
        }
    }

    [HarmonyPatch(typeof(DemoChargeAsset), nameof(DemoChargeAsset.SetComponentData))]
    internal static class ChargeLaunchPatch
    {
        private static void Postfix(EntityManager entityManager, Entity entity) => VrChargeByHand.Launch(entityManager, entity);
    }
}
