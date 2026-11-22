using System.Collections.Generic;
using System.Reflection;
using BBI.Unity.Game;
using BepInEx.Configuration;
using HarmonyLib;
using Unity.Entities;
using Unity.Mathematics;
using UnityEngine;

namespace ShipbreakerVR
{
    // Steuerung/Sprengsatzplatzierung = Manuell (VR options, Gameplay; Raik, 2026-11-18): demo charges are put on by
    // hand instead of being aimed. Since 2026-11-20 Automatisch is Manuell with the game's preview and the trigger (Auto,
    // below); the rest of this note is about the game's tool with the charges in the hand, which neither way of placing
    // gives any more (it stays for the code that still serves it). Before, Automatisch was the game's way: the trigger
    // placed or threw, with the preview. The game finds the place for
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
    // What a charge let go does belongs to the extended tool physics instead (Steuerung/ErweiterteWerkzeugphysik; Raik,
    // 2026-11-18), with either way of placing: with it the charge stays afloat where it was let go and is put back
    // behind the back by hand; without it, it is back with the charges.
    // With Automatisch a charge in a hand is always the game's tool: a hand that closes round a charge afloat, or takes
    // one off a wall (VrChargeDisarm), gets the charges with the preview and the trigger (VrToolInput.TakeCharges).
    // The game's code asks for the camera through VrAim (AimPatch); inside DemoChargeController.Update it gets the placing
    // frame here instead.
    internal static class VrChargeByHand
    {
        static VrChargeByHand() => VrSession.OnUnload("charge by hand", Release);

        // How far beyond the charge's back a wall may be, in meters at world scale 1.
        private const float Reach = 0.20f;
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

        public enum Placing { Automatisch, Manuell }

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

        // With the charges put on by hand, the charge does not ride on the game's animated hand: the hands follow their
        // controllers as with any tool, and the charge is set in front of the controller that holds it, its back where the
        // controller points and its top up (VrHands.ChargeInHand). Raik, 2026-11-18: on the animated hand the charge's back
        // pointed 79 degrees to the side; turning the hand to straighten it turned the hand and the detonator by as much.
        // With either way of placing (Raik, 2026-11-19, testing 0.31.0 with Automatisch: the hand held the charge a little
        // turned, and letting go it went through part of the game's stow animation): the charge lies in the hand as with
        // Manuell, and the hand stays at its controller after letting go (VrToolModels.PoseArm bends the game's arm no more).
        public static bool Decoupled => VrAim.Active;

        // Both ways of placing take the charges into the hand as things of their own (Raik, 2026-11-20: Automatisch with the
        // game's tool in the hand and a copy of the detonator beside it still did not work well enough; "take over the
        // manual system completely"). Automatisch adds the game's preview and the trigger to it (Auto).
        private static bool On => VrSettings.ChargePlacing != null && VrAim.Active;

        // The charges are taken into the hand as things of their own (either way of placing), for VrChargeDisarm.
        public static bool ByHand => On;

        // Placed with the preview and the trigger (Automatisch): the game's preview shows where a charge in the hand goes
        // on, at the wall its back is held to or else where the controller points, as far as the game reaches; the
        // trigger puts it on there, and a hand that keeps its grip closed gets the next charge from behind the back
        // after the game's wait. Let go at the wall it goes on there as well, as with Manuell.
        public static bool Auto => On && VrSettings.ChargePlacing.Value == Placing.Automatisch;

        // Whether that hand holds a charge afloat, as a thing of its own (VrControls: Y brakes then).
        public static bool Holds(VrInput.Hand hand) => afloat[hand.IsLeft ? 0 : 1].Charge != null;

        // Whether that hand's controller is steadied (VrInput): it holds a charge afloat, or the game's charges or
        // their detonator.
        public static bool Steadies(VrInput.Hand hand)
        {
            if (Holds(hand)) return true;
            var held = hand.IsLeft ? VrHand.Left : VrHand.Right;
            return held.HoldsTool && held.Tool == EquipmentController.Equipment.DemoCharge;
        }

        // The game's charges are out in their other mode: the hand shows their detonator (VrLooseTools, VrToolFit).
        public static bool DetonatorOut => ChargesInHand() && DetonatorMode;

        // The game's tool is in its detonator mode, whether or not it is drawn yet (VrToolModels: the charge on the hand
        // stays hidden then).
        public static bool DetonatorMode
        {
            get
            {
                if (controller == null) return false;
                var state = State(controller);
                return state == DemoChargeState.Detonation || state == DemoChargeState.Detonating || state == DemoChargeState.DetonationFailed;
            }
        }

        // A charge let go stays afloat: with the extended tool physics.
        private static bool Floats => VrSettings.ToolPhysics != null && VrSettings.ToolPhysics.Value;

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

        // The game's preview of where the charge would go on (a ghost of it on the wall) is not shown with the charges
        // put on by hand (Raik, 2026-11-18): the line on the charge in the hand tells how it cuts, and once it is on, the
        // game shows the cut through the charge itself. The preview still runs unseen: it tells whether the place is
        // blocked, and sounds as the charge comes within reach.
        private static DemoChargePreview previewOf;
        private static Renderer[] previewParts;
        private static bool previewHidden;

        private static void HidePreview(bool hide)
        {
            var preview = Preview();
            if (preview != previewOf)
            {
                previewOf = preview;
                previewParts = preview != null ? preview.GetComponentsInChildren<Renderer>(true) : null;
                previewHidden = false;
            }
            if (previewParts == null || hide == previewHidden) return;
            previewHidden = hide;
            foreach (var part in previewParts)
                if (part != null) part.enabled = !hide;
        }

        public static void Enter(DemoChargeController instance)
        {
            controller = instance;
            inside = false;
            HidePreview(On && !Auto);
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
            if (state == DemoChargeState.Detonating && State(instance) == state && VrAim.Active) QuietHand(instance);
            if (!inside || (state != DemoChargeState.Placing && state != DemoChargeState.Throwing) || State(instance) != state || Timer == null) return;
            // No wait for the game's hand animation: the charge leaves the hand on the next frame.
            var timer = Timer.GetValue(instance);
            var delay = timer != null ? AccessTools.Field(timer.GetType(), "Delay") : null;
            if (delay == null) return;
            delay.SetValue(timer, 0f);
            Timer.SetValue(instance, timer);
            if (logs++ < 40) Plugin.Log.LogInfo($"Charge by hand: {(state == DemoChargeState.Placing ? "onto the wall" : "let go afloat")}");
        }

        // As placed charges go off, the game closes the whole hand round the detonator (its hand animator's trigger
        // DetonatorDetonateParameter, set as the controller goes on to Detonating). In VR the fingers on the lever press
        // it with the controller's trigger (VrHands.PressDetonator), and the game's squeeze came on top of that (Raik,
        // 2026-11-18: "a double animation, that seems to be an old one, take it out"). The trigger is taken back
        // before the animator sees it. The detonator's own lights and a failed detonation's animation stay.
        private static readonly AccessTools.FieldRef<DemoChargeController, Animator> HandAnimator =
            AccessTools.FieldRefAccess<DemoChargeController, Animator>("m_HandAnimatorController");
        private static bool quietLogged;

        private static void QuietHand(DemoChargeController instance)
        {
            var animator = HandAnimator(instance);
            var trigger = instance.AnimationParamters.DetonatorDetonateParameter;
            if (animator == null || string.IsNullOrEmpty(trigger)) return;
            animator.ResetTrigger(trigger);
            if (quietLogged) return;
            quietLogged = true;
            Plugin.Log.LogInfo($"Charge by hand: the game's hand animation for detonating ('{trigger}') is left out, the fingers press the lever instead");
        }

        // For VrChargeDisarm: one charge from the stock let go at that place and turn, at rest against the player's body;
        // what the game does for a thrown charge (ThrowDemoCharge), without its fling. Null if it could not.
        public static StructurePart LetGoFromStock(Vector3 at, Quaternion turned, Vector3 motion = default)
        {
            var instance = controller != null ? controller : Object.FindObjectOfType<DemoChargeController>();
            if (instance == null || Instantiate == null) return null;
            var player = VrToolCollide.Player();
            launch = (player != null ? player.velocity : Vector3.zero) + motion;
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
        public static bool LetsGo(VrInput.Hand hand, bool evenAtItsPlace = false)
        {
            if (!On || controller == null || !FindMesh() || !ChargesInHand()) return false;
            if (State(controller) != DemoChargeState.Placement) return false;
            if (meshRenderer != null && (!meshRenderer.enabled || !meshRenderer.gameObject.activeInHierarchy)) return false;
            return evenAtItsPlace || !VrToolBelt.Stows(VrToolBelt.Zone.DemoCharge, hand);
        }

        // From VrToolInput.ChargeLetGo: with the charges placed the game's way (Automatisch) and extended tool physics, the
        // hand that holds them opens its grip away from their place on the body. One charge from the stock stays afloat
        // where the charge in the hand was, turned as it was, with the hand's motion; VrToolInput puts the charges away.
        // False if there was none to let go.
        public static bool LetGoAfloat(VrInput.Hand hand)
        {
            if (On || !Floats || !VrAim.Active || controller == null || !FindMesh() || !ChargesInHand()) return false;
            if (State(controller) != DemoChargeState.Placement) return false;
            if (meshRenderer != null && (!meshRenderer.enabled || !meshRenderer.gameObject.activeInHierarchy)) return false;
            if (VrToolBelt.Stows(VrToolBelt.Zone.DemoCharge, hand)) return false;
            if (!(AnyCharges?.Invoke(controller, null) is bool any) || !any) return false;
            var scale = VrCamera.WorldScale;
            var motion = VrClimb.LetGoMotion(hand, Drift * scale, TurnDrift, out var speed, out _) ? Vector3.ClampMagnitude(speed, MostThrow * scale) : Vector3.zero;
            return LetGoFromStock(mesh.position - mesh.forward * (BackOfMesh * scale), mesh.rotation, motion) != null;
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

        // From VrToolInput.Draw: the grip pressed behind the lower back with the charges put on by hand. A charge from the
        // stock comes into that hand as a thing of its own, held as any loose part (Raik, 2026-11-18: a charge handed
        // over, a thing of its own from then on, was the most pleasant to work with: it meets the wall, goes on when let
        // go, and is turned in the hand as one likes; so it is that from the start). True if that was done, or there is
        // none to take; false to draw the game's tool.
        // It comes into the hand as the game's own hand holds the charges (Raik, 2026-11-18, of his screenshots with
        // Automatisch: "even more pleasant" than the upright slab before, and how a charge taken off a wall is to lie in
        // the hand as well): as the game's prefab has the charge on the hand ('PRF_Demo_Charge' on rig_right_hand),
        // taken against a hand that follows the controller: upright in the hand, its top rim up and a little forward,
        // its back outwards and a little forward, its face with the orange sign (the mesh's +z) inwards, to the other
        // hand, and the hand at its outer edge. Measured in the controller's axes (x right, y up, z along its aim) on
        // the frame the charges are drawn, while the mod still drives the right hand: where the back and the top point,
        // and the charge's middle from the grip. The left hand's is the mirror image.
        // Not as it lies against the controller once the game's own arm has the charge (back (0.89, -0.42, 0.17), top
        // (0.44, 0.87, -0.20), middle (-1, 9, 14) cm): that arm is turned against the controller by the hand's fit
        // (VrToolFit), and on a hand that follows the controller the charge was tipped outwards by 27 degrees (Raik's
        // screenshots of 12:52, "the starting hold now" against "the hold I want").
        // Nor quite the prefab's pose (back (0.95, -0.07, 0.32), top (0, 0.98, 0.22), middle (-7, -2, 12) cm; Raik at
        // 13:00: "nearly there"): he took the charge from hand to hand until it lay as he wants it, twice, and the log
        // had it both times (MeasureHeld: back (0.79, -0.06, 0.61) and (0.79, -0.15, 0.60), top (-0.07, 0.98, 0.19) and
        // (0.01, 0.97, 0.22), middle (-14, -1, 9) and (-15, -1, 10) cm). The mean of the two: the face turned 20 degrees
        // further to him, the charge 8 cm further inwards.
        // That was the mod's own hold until 0.31.5 (middle (-14.5, -1, 9.5) cm, back (0.79, -0.10, 0.61), top
        // (-0.03, 0.98, 0.21)). Since then (Raik, 2026-11-19 23:05, with Automatisch, fitted by dragging the charge with the
        // other hand, VrToolFit.Charge, and his screenshot "for the hold of the charge"): StandardHold, as
        // SprengsatzHaltung writes it.
        private const string StandardHold = "-10.3 -1.4 7.1 / 1.5 -140.2 -11.7";
        private static bool standardParsed;
        private static Vector3 standardAt;
        private static Quaternion standardTurn = Quaternion.identity;

        // The hold as it counts: the one being dragged or stored while fitting the tools (VrToolFit.ChargeHold,
        // SprengsatzHaltung), else the mod's own. As in the right hand: the middle from the grip and the turn, in the
        // controller's axes.
        private static void Hold(out Vector3 middle, out Quaternion turn)
        {
            if (VrToolFit.ChargeDrag(out middle, out turn) || VrToolFit.ChargeHold.TryGet(out middle, out turn)) return;
            if (!standardParsed && VrToolFit.TryParse(StandardHold, out var at, out var angles))
            {
                standardParsed = true;
                standardAt = at / 100f;
                standardTurn = Quaternion.Euler(angles);
            }
            middle = standardAt;
            turn = standardTurn;
        }

        // How the charge is turned in that hand, against the hand's or the controller's axes.
        internal static Quaternion InHand(bool left)
        {
            Hold(out var middle, out var turn);
            if (left) VrToolFit.Mirror(ref middle, ref turn);
            return turn;
        }

        // While the tools are fitted (VrToolFit): a charge handed from hand to hand lies in the hand that keeps it as
        // the user wants it, once the other hand has let go. That hold is stored, and charges come into the hand so.
        private static void StoreHold(VrInput.Hand hand, Afloat held)
        {
            var part = held.Charge != null ? held.Charge.DemoCharge : null;
            var rig = VrCamera.Instance != null ? VrCamera.Instance.transform.parent : null;
            if (part == null || rig == null || !hand.TryGetAimRotation(out var aimLocal) || !hand.TryGetPosition(out var gripLocal)) return;
            var scale = VrCamera.WorldScale;
            var frame = part.transform;
            var toController = Quaternion.Inverse(rig.rotation * aimLocal);
            var turn = toController * frame.rotation;
            var middle = toController * (frame.position + frame.forward * (BackOfMesh * scale) - rig.TransformPoint(gripLocal)) / scale;
            if (hand.IsLeft) VrToolFit.Mirror(ref middle, ref turn);
            VrToolFit.ChargeHold.Store(middle, turn);
            hand.Buzz(0.5f, 0.08f);
            Plugin.Log.LogInfo($"Charge by hand: the hold of the {hand.Name} hand is the one charges come into the hand with from now on");
        }

        // Where the charge's mesh (its middle, BackOfMesh before its origin) lies in that hand and how it is turned, in
        // tracking space: the game's own charge in the hand (VrHands.ChargeInHand) as a charge taken out by hand lies
        // (Raik, 2026-11-19: with Automatisch it sat off the hand, with Manuell right). False without the controller's pose.
        internal static bool HoldInTracking(bool left, out Vector3 meshAt, out Quaternion turned)
        {
            meshAt = Vector3.zero;
            turned = Quaternion.identity;
            var hand = left ? VrInput.Left : VrInput.Right;
            if (!hand.TryGetPosition(out var gripLocal) || !hand.TryGetAimRotation(out var aimLocal)) return false;
            Hold(out var at, out var turn);
            if (left) VrToolFit.Mirror(ref at, ref turn);
            turned = aimLocal * turn;
            meshAt = gripLocal + aimLocal * at;
            return true;
        }

        // Where a charge lies in that hand, in the world: its own origin (the middle of its back) and its turn. False
        // without the controller's pose.
        public static bool HoldPose(VrInput.Hand hand, out Vector3 origin, out Quaternion turned)
        {
            origin = Vector3.zero;
            turned = Quaternion.identity;
            var rig = VrCamera.Instance != null ? VrCamera.Instance.transform.parent : null;
            if (rig == null || !hand.TryGetPosition(out var gripLocal) || !hand.TryGetAimRotation(out var aimLocal)) return false;
            var scale = VrCamera.WorldScale;
            var aim = rig.rotation * aimLocal;
            Hold(out var at, out var turn);
            if (hand.IsLeft) VrToolFit.Mirror(ref at, ref turn);
            turned = aim * turn;
            origin = rig.TransformPoint(gripLocal) + aim * (at * scale) - turned * Vector3.forward * (BackOfMesh * scale);
            return true;
        }

        public static bool TakesOut(VrInput.Hand hand)
        {
            if (!On) return false;
            var instance = controller != null ? controller : Object.FindObjectOfType<DemoChargeController>();
            if (instance == null || !HoldPose(hand, out var origin, out var turned)) return false;
            controller = instance;
            if (!(AnyCharges?.Invoke(instance, null) is bool any) || !any)
            {
                if (logs++ < 40) Plugin.Log.LogInfo("Charge by hand: no demo charges left behind the back");
                return true;
            }
            // As many out as may be (Raik, 2026-11-22): with Automatisch no further charge comes into the hand, as the
            // game hands out none; with Manuell it does, with the warning, to be held and thrown as the thing it is,
            // but it goes on nowhere and does not cling until there is room for it (PutOn, Reactivate).
            var full = Out(out var count, out var limit) && count >= limit;
            if (full && Auto)
            {
                Warn(false);
                hand.Buzz(0.3f, 0.1f);
                if (logs++ < 60) Plugin.Log.LogInfo($"Charge by hand: {count} of {limit} charges are out, none comes into the {hand.Name} hand (Automatisch)");
                return true;
            }
            var part = LetGoFromStock(origin, turned);
            if (part == null) return false;
            VrChargeDisarm.Give(part, hand, true);
            hand.Buzz(0.4f, 0.05f);
            if (full) Warn(true);
            if (logs++ < 40) Plugin.Log.LogInfo($"Charge by hand: {hand.Name} hand takes a charge out from behind the back, as a thing of its own{(full ? $" ({count} of {limit} are out: it will not go on)" : "")}");
            return true;
        }

        // ---- A charge afloat: left alone by the arm that let it go, and put back behind the back ----

        private sealed class Afloat
        {
            public Rigidbody Body;
            public DemoChargeRetrievalController Charge;
            public bool AtPlace;
            // What the game makes a charge afloat cling with (Sticky on what it hangs on), taken away while it is held.
            public Entity StickyOn;
            public LayerMask StickyMask;
            public bool StickyOff;
            // The wall within reach of its back: where it goes on when let go.
            public bool Near;
            public bool Shown;
            public StructurePart Target;
            public Collider Wall;
            public Vector3 Point;
            public Quaternion Turn;
            // With Auto: no wall at its back, but one where the controller points, within the game's reach.
            public bool Aimed;
            // The other hand holds it as well (VrToolFit: once that hand lets go, the hold is stored).
            public bool Shared;
            // How far the wall is from the charge's back, in meters at world scale 1 (MaxValue without one in reach).
            public float Reach = float.MaxValue;
        }

        // For the short ray from the charge's back to the wall (a few centimetres): room for all that can lie in it.
        private static readonly RaycastHit[] Hits = new RaycastHit[32];
        private static readonly FieldInfo PreviewField = AccessTools.Field(typeof(DemoChargeController), "m_Preview");
        private static readonly FieldInfo DataField = AccessTools.Field(typeof(DemoChargeController), "mData");
        private static readonly FieldInfo JointSetup = AccessTools.Field(typeof(DemoChargeController), "mCachedJSA");
        private static readonly MethodInfo CacheJointSetup = AccessTools.Method(typeof(DemoChargeController), "CacheJointSetupAsset");

        // A charge afloat in a hand behaves as the charge from the stock does (Raik, 2026-11-18: taken out of the air or
        // handed over, it clung to any wall the hand came near): held, it does not cling by itself; near a wall the game's
        // preview shows where and how it would go on; let go there, it goes on (the charge afloat goes back to the stock
        // the game's way and one from the stock is put on the wall, as the game places one).
        private static void Unstick(Afloat held)
        {
            var part = held.Charge.DemoCharge;
            if (part == null || !EntityBlueprintComponent.IsValid(part.EntityBlueprintComponent)) return;
            var manager = part.EntityBlueprintComponent.EntityManager;
            var entity = part.EntityBlueprintComponent.Entity;
            if (!manager.Exists(entity) || !manager.HasComponent<HierarchyMember>(entity)) return;
            var parent = manager.GetComponentData<HierarchyMember>(entity).Parent;
            if (!manager.Exists(parent)) return;
            if (!manager.HasComponent<Sticky>(parent))
            {
                // Let go at the game's limit before (LeaveLoose): what it would cling with is remembered, and it is
                // given back when it is let go again below the limit (Restick).
                var left = held.StickyOff ? -1 : LeftLoose.FindIndex(loose => loose.On == parent);
                if (left >= 0)
                {
                    held.StickyMask = LeftLoose[left].Mask;
                    held.StickyOn = parent;
                    held.StickyOff = true;
                    LeftLoose.RemoveAt(left);
                }
                return;
            }
            held.StickyMask = manager.GetComponentData<Sticky>(parent).LayerMask;
            held.StickyOn = parent;
            held.StickyOff = true;
            manager.RemoveComponent<Sticky>(parent);
            if (logs++ < 40) Plugin.Log.LogInfo("Charge by hand: a charge afloat in a hand does not cling by itself");
        }

        private static void Restick(Afloat held)
        {
            if (!held.StickyOff) return;
            held.StickyOff = false;
            var world = World.DefaultGameObjectInjectionWorld;
            if (world == null) return;
            var manager = world.EntityManager;
            if (manager.Exists(held.StickyOn) && !manager.HasComponent<Sticky>(held.StickyOn))
                manager.AddComponentData(held.StickyOn, new Sticky { LayerMask = held.StickyMask });
        }

        // Charges afloat that were let go with the limit of charges reached: they do not cling by themselves, and
        // what they would cling with (their parent's Sticky mask) is kept here, in the order they were left, until a
        // hand takes them again or fewer charges than the limit are out (Reactivate).
        private static readonly List<(Entity On, LayerMask Mask)> LeftLoose = new List<(Entity, LayerMask)>();

        private static void LeaveLoose(Afloat held)
        {
            if (!held.StickyOff) return;
            held.StickyOff = false;
            var on = held.StickyOn;
            LeftLoose.RemoveAll(loose => loose.On == on);
            LeftLoose.Add((on, held.StickyMask));
        }

        // Fewer charges out than the limit again (one taken back, or gone): the charge left loose longest clings
        // again as the game has it, by itself, wherever it drifts (Raik, 2026-11-22: a surplus charge thrown away
        // becomes live once there is room for it, and one has to mind where it went). Looked at twice a second.
        // What it does not do: the game sets off every charge that is out, the loose ones among them, so setting the
        // others off does not leave a loose one behind to become live.
        private static float nextReactivate;

        private static void Reactivate()
        {
            if (LeftLoose.Count == 0 || Time.unscaledTime < nextReactivate) return;
            nextReactivate = Time.unscaledTime + 0.5f;
            var world = World.DefaultGameObjectInjectionWorld;
            if (world == null || !Out(out var count, out var limit)) return;
            var manager = world.EntityManager;
            LeftLoose.RemoveAll(loose => !manager.Exists(loose.On));
            var inHands = 0;
            foreach (var hand in afloat)
                if (hand.Charge != null) inHands++;
            var live = count - LeftLoose.Count - inHands;
            while (LeftLoose.Count > 0 && live < limit)
            {
                var (on, mask) = LeftLoose[0];
                LeftLoose.RemoveAt(0);
                if (!manager.HasComponent<Sticky>(on)) manager.AddComponentData(on, new Sticky { LayerMask = mask });
                live++;
                if (logs++ < 60) Plugin.Log.LogInfo($"Charge by hand: a charge left loose clings again, {live} of {limit} charges are live now");
            }
        }

        // A hand's hold on a charge ended without the grip opening (lost by tracking, taken by the other hand, the hand
        // made to let go): it clings again as the game has it, but not with the game's limit reached.
        private static void EndHold(Afloat held, DemoChargeRetrievalController charge)
        {
            if (charge != null && AtLimit(charge)) LeaveLoose(held);
            else Restick(held);
        }

        // On unload: the charges left loose cling again as the game has them.
        private static void GiveBackSticky()
        {
            var world = World.DefaultGameObjectInjectionWorld;
            if (world != null)
            {
                var manager = world.EntityManager;
                foreach (var (on, mask) in LeftLoose)
                    if (manager.Exists(on) && !manager.HasComponent<Sticky>(on))
                        manager.AddComponentData(on, new Sticky { LayerMask = mask });
            }
            LeftLoose.Clear();
        }

        private static DemoChargePreview Preview() => controller != null && PreviewField != null ? PreviewField.GetValue(controller) as DemoChargePreview : null;

        private static readonly AccessTools.FieldRef<DemoChargeController, IDemoChargeControllerData> Data =
            AccessTools.FieldRefAccess<DemoChargeController, IDemoChargeControllerData>("mData");
        private static readonly AccessTools.FieldRef<DemoChargeController, EntityQuery> ChargeQuery =
            AccessTools.FieldRefAccess<DemoChargeController, EntityQuery>("mDemoChargeQuery");
        private static readonly MethodInfo BlockedSound = AccessTools.Method(typeof(DemoChargeController), "DemoChargeBlocked");
        private static readonly MethodInfo LimitWarning = AccessTools.Method(typeof(DemoChargeController), "InWorldLimitReached");

        private static LayerMask WallMask()
        {
            var data = controller != null && DataField != null ? DataField.GetValue(controller) : null;
            var mask = data != null ? Traverse.Create(data).Property("RaycastLayerMask").GetValue() : null;
            return mask is LayerMask layers ? layers : (LayerMask)Physics.DefaultRaycastLayers;
        }

        // What the rays to a wall and the preview's test for room leave out: the charges in the hands (and that charge's
        // own pieces), the player's body and what hangs on the VR rig. A hand's body counts only while it is a charge:
        // Check keeps whatever loose body a hand holds, and a ship that is not kinematic is one. With the other hand on
        // the ship, as one holds on to lay a charge on, the whole ship was left out and no wall found (Raik,
        // 2026-11-22: laid on by hand they did not stick, drifting to the wall they did; the log: "no wall within 20 cm
        // of its back" with the left hand on a panel of that 44 t body).
        private static bool Ours(Collider collider, Transform charge)
        {
            if (collider == null) return true;
            var body = collider.attachedRigidbody;
            if (body != null && (HoldsAsCharge(afloat[0], body) || HoldsAsCharge(afloat[1], body) || body == VrToolCollide.Player())) return true;
            if (charge != null && collider.transform.IsChildOf(charge)) return true;
            var rig = VrCamera.Instance != null ? VrCamera.Instance.transform.parent : null;
            return rig != null && collider.transform.IsChildOf(rig);
        }

        private static bool HoldsAsCharge(Afloat held, Rigidbody body) => held.Charge != null && held.Body == body;

        // A demo charge, on a wall or afloat: no wall for another one (as the game's UpdateTargeting has it).
        private static bool IsCharge(StructurePart part)
        {
            var blueprint = part.EntityBlueprintComponent;
            var manager = blueprint.EntityManager;
            return manager.Exists(blueprint.Entity) && manager.HasComponent<DemoCharge>(blueprint.Entity);
        }

        private static bool Fits(StructurePart target, StructurePart part) =>
            target != null && target != part && EntityBlueprintComponent.IsValid(target.EntityBlueprintComponent) && !IsCharge(target);

        private static void Found(Afloat held, StructurePart target, RaycastHit hit, Transform charge)
        {
            held.Target = target;
            held.Wall = hit.collider;
            held.Point = hit.point;
            held.Turn = MathUtility.GetLookRotation(hit.normal, charge.up);
        }

        // Where the controller points, in the world.
        private static bool AimRay(VrInput.Hand hand, out Ray ray)
        {
            ray = default;
            var rig = VrCamera.Instance != null ? VrCamera.Instance.transform.parent : null;
            if (rig == null || !hand.TryGetAimPose(out var at, out var turn, out _)) return false;
            ray = new Ray(rig.TransformPoint(at), rig.rotation * turn * Vector3.forward);
            return true;
        }

        // Where the charge that hand holds would go on: at the wall within reach of its back, turned as it is held (the
        // line on it is the cut); with Auto, without such a wall, where the controller points, within the game's reach
        // (IDemoChargeControllerData.RaycastDistance), turned the same way against that wall.
        private static void Aim(VrInput.Hand hand, Afloat held)
        {
            held.Near = held.Aimed = false;
            held.Target = null;
            held.Wall = null;
            held.Reach = float.MaxValue;
            var part = held.Charge.DemoCharge;
            if (part == null || controller == null) return;
            var frame = part.transform;
            var scale = VrCamera.WorldScale;
            var mask = WallMask();
            var count = Physics.RaycastNonAlloc(frame.position + frame.forward * (Lead * scale), -frame.forward, Hits, (Lead + BackOfMesh + Reach) * scale, mask, QueryTriggerInteraction.Ignore);
            var nearest = float.MaxValue;
            for (var i = 0; i < count; i++)
            {
                var hit = Hits[i];
                if (hit.distance >= nearest || Ours(hit.collider, frame)) continue;
                var target = hit.collider.GetComponent<StructurePart>();
                if (!Fits(target, part)) continue;
                // Put on by hand it goes on with its back, as it clings (ClingsBackOn): not held on edge to the wall.
                if (BackOnly && !ChargeCling.BackOn(Vector3.Dot(frame.forward, hit.normal), -1f, MostTilt)) continue;
                nearest = hit.distance;
                Found(held, target, hit, frame);
            }
            held.Near = held.Target != null;
            if (held.Near)
            {
                held.Reach = nearest / scale - Lead - BackOfMesh;
                return;
            }
            var data = Data(controller);
            if (!Auto || data == null || !AimRay(hand, out var ray)) return;
            // The first thing the ray meets, as with the game's ray from the camera: a wall behind a pipe is not reached.
            if (!FirstHit(ray, data.RaycastDistance, mask, frame, out var met)) return;
            var aimed = met.collider.GetComponent<StructurePart>();
            if (!Fits(aimed, part)) return;
            Found(held, aimed, met, frame);
            held.Aimed = true;
        }

        // The nearest thing along a ray that is not ours (Ours). Asked for one hit at a time, going on behind each of
        // our own: Physics.RaycastNonAlloc fills its array with any of the hits along the ray, not the nearest ones, so
        // with more things along the ray than the array holds (16; in tight rooms full of pipes and panels, over the
        // game's whole reach) the wall in front could be missing from it, and the preview went to one behind.
        private static bool FirstHit(Ray ray, float reach, LayerMask mask, Transform charge, out RaycastHit hit)
        {
            var from = ray.origin;
            for (var own = 0; own < 16 && reach > 0f; own++)
            {
                if (!Physics.Raycast(from, ray.direction, out hit, reach, mask, QueryTriggerInteraction.Ignore)) return false;
                if (!Ours(hit.collider, charge)) return true;
                // On from just inside that one, which a ray starting within it does not meet again.
                var step = hit.distance + 0.001f;
                from += ray.direction * step;
                reach -= step;
            }
            if (reach > 0f && logs++ < 60) Plugin.Log.LogInfo("Charge by hand: the aimed ray met 16 of the mod's own shapes one behind the other, no place shown");
            hit = default;
            return false;
        }

        // ---- The preview and the trigger (Auto) ----

        // The hand whose charge the preview shows (there is one preview: with a charge in each hand, the one that had it
        // first keeps it).
        private static VrInput.Hand previewHand;
        // The hand that put its charge on with the trigger and keeps its grip closed: the next charge comes into it.
        private static VrInput.Hand reloadHand;
        private static float reloadAt;
        private static int previewLogs;

        private static Afloat Of(VrInput.Hand hand) => afloat[hand.IsLeft ? 0 : 1];

        private static bool Previews(VrInput.Hand hand)
        {
            if (hand == null || !Auto) return false;
            var held = Of(hand);
            return held.Charge != null && held.Target != null && (held.Near || held.Aimed);
        }

        // The game's preview where the charge would go on, as the game shows it for the charges in its hand.
        private static void ShowPreview()
        {
            afloat[0].Shown = afloat[1].Shown = false;
            var owner = Previews(previewHand) ? previewHand : Previews(VrInput.Left) ? VrInput.Left : Previews(VrInput.Right) ? VrInput.Right : null;
            var preview = Preview();
            if (owner == null || preview == null)
            {
                if (previewHand != null && preview != null) preview.Deactivate();
                previewHand = null;
                return;
            }
            if (owner != previewHand && previewLogs++ < 20) Plugin.Log.LogInfo($"Charge by hand: the preview shows where the charge in the {owner.Name} hand goes on");
            previewHand = owner;
            var held = Of(owner);
            if (JointSetup != null && JointSetup.GetValue(controller) == null) CacheJointSetup?.Invoke(controller, null);
            preview.Activate();
            preview.SetTargetData(held.Point, held.Turn, held.Wall, WallMask(), held.Target, JointSetup?.GetValue(controller) as JointSetupAsset);
            held.Shown = true;
        }

        // The preview calls the place taken: the game's sound for it.
        private static bool Refused()
        {
            var preview = Preview();
            if (preview == null || !preview.IsBlocked) return false;
            BlockedSound?.Invoke(controller, null);
            return true;
        }

        // How many charges may be out at once. The game's own number (IDemoChargeControllerData.InWorldLimit) is 15 in
        // its data, with 5 carried (more with upgrades): it is only reached after buying more charges at the kiosk in
        // the same shift. Experimente/SprengsatzLimit sets a smaller number (0: the game's). For a day the mod's own
        // limit was 6 (Raik, 2026-11-22: "LYNX gives a most of 6 active charges"); he took that lock out again on
        // 2026-11-23 ("for now"), so the game's number holds unless the config says otherwise.
        private const int OwnLimit = 0;
        private static ConfigEntry<int> ownLimit;
        private static int limitLogged = -1;

        // How many charges are out (on walls, afloat, in the hands: all the game counts) and how many may be.
        private static bool Out(out int count, out int limit)
        {
            count = limit = 0;
            var data = controller != null ? Data(controller) : null;
            if (data == null) return false;
            try { count = ChargeQuery(controller).CalculateEntityCount(); }
            catch (System.Exception) { return false; }
            if (ownLimit == null && VrSettings.File != null)
                ownLimit = VrSettings.Bind("Experimente", "SprengsatzLimit", OwnLimit,
                    "How many demo charges may be out at once in VR (on walls and afloat), at most the game's own limit (15). 0: the game's limit, no limit of the mod's own.");
            limit = ownLimit != null && ownLimit.Value > 0 ? Mathf.Min(ownLimit.Value, data.InWorldLimit) : data.InWorldLimit;
            if (limit != limitLogged)
            {
                limitLogged = limit;
                Plugin.Log.LogInfo($"Charge by hand: at most {limit} charges out at once (the game's limit {data.InWorldLimit}, {data.MaxCarryAmount} carried)");
            }
            return true;
        }

        // The limit of charges out reached (as the game's own placing asks before it puts one on or throws one), for
        // that charge: it is one of those the game counts, and so is a charge the other hand holds, which is not out
        // yet either (with one free place and a charge in each hand, the first let go still goes on).
        private static bool AtLimit(DemoChargeRetrievalController charge)
        {
            if (!Out(out var count, out var limit)) return false;
            var inHands = 1;
            foreach (var other in afloat)
                if (other.Charge != null && other.Charge != charge) inHands++;
            return count - inHands >= limit;
        }

        // The same with the game's warning for it (its sound and its line on the HUD), and the mod's hint under that
        // line (LimitShown).
        private static bool LimitReached(DemoChargeRetrievalController charge)
        {
            if (!AtLimit(charge)) return false;
            Warn(true);
            return true;
        }

        // The game's warning of the limit (its sound, and its line on the HUD with the mod's text in it, LimitShown), with
        // or without the hint to stow the charge under it.
        private static void Warn(bool hint)
        {
            warnAsked = Time.unscaledTime;
            warnHint = hint;
            LimitWarning?.Invoke(controller, null);
        }

        // The warning on the HUD (Raik, 2026-11-22): taking a charge beyond the limit into the hand tells that LYNX
        // gives a most of so many active charges and that further ones are not armed for the time. It is there for
        // WarnShow seconds and then fades within WarnFade, not gone at once. Under it, with a charge that came into the
        // hand all the same (Manuell), what to do about it (Marc, 2026-11-22): the charge does not go on and does not
        // cling, so it is to be put back, and one left behind is charged for as any lost charge.
        // The text is written into the game's own label for its warning ('Maximum number of demo charges reached'),
        // whose time on the screen is set to the mod's; the fading is a CanvasGroup on that label. The label's own
        // text and look are put back when the game switches its warnings next.
        private const float WarnShow = 3f;
        private const float WarnFade = 0.5f;
        // The game switches its label on in the same call as the warning; a warning of its own later is left as it is.
        private const float WarnWindow = 1f;
        private static float warnAsked = -1f;
        private static bool warnHint;
        // Each label with the mod's text in it and its own text (a HUD may have more than one of these controllers; each
        // switches its own labels).
        private static readonly Dictionary<TMPro.TMP_Text, string> Hinted = new Dictionary<TMPro.TMP_Text, string>();
        // The warning's own colour got lost against the scene (Raik, 2026-11-23: "the writing must be a good deal
        // brighter, it is lost in the background"): the mod's text is written in WarnColor, bold, and the label's own
        // colour and style are put back with its text.
        private static readonly Color WarnColor = new Color(1f, 0.97f, 0.88f, 1f);
        private static readonly Dictionary<TMPro.TMP_Text, (Color Color, TMPro.FontStyles Style)> Looks =
            new Dictionary<TMPro.TMP_Text, (Color, TMPro.FontStyles)>();

        private static void OwnLook(TMPro.TMP_Text text)
        {
            if (!Looks.TryGetValue(text, out var look)) return;
            text.color = look.Color;
            text.fontStyle = look.Style;
            Looks.Remove(text);
        }

        private sealed class Fading
        {
            public CanvasGroup Group;
            public float Until;
        }

        private static readonly List<Fading> Fades = new List<Fading>();
        // The groups the mod put on the game's labels, to take them off again when it is unloaded.
        private static readonly List<CanvasGroup> OwnGroups = new List<CanvasGroup>();
        private static readonly AccessTools.FieldRef<InWorldLimitUIController, float> WarnTimeLeft =
            AccessTools.FieldRefAccess<InWorldLimitUIController, float>("mRemainingTime");
        private static int hintLogs;

        // What the warning says, for that limit.
        internal static string LimitText(int limit, bool hint)
        {
            var text = VrOptions.L($"LYNX allows at most {limit} active demo charges.\nFurther charges are not armed for now.",
                $"LYNX gibt höchstens {limit} aktive Sprengsätze vor.\nWeitere Sprengsätze werden in dieser Zeit nicht aktiv geschaltet.");
            if (hint)
                text += "\n" + VrOptions.L("Stow the charge again. Lost charges will be billed.",
                    "Sprengsatz wieder verstauen. Verlorene Sprengsätze werden in Rechnung gestellt.");
            return text;
        }

        // From ChargeLimitHintPatch (InWorldLimitUIController.ToggleWarnings): that controller switched its limit
        // warnings.
        public static void LimitShown(InWorldLimitUIController shown, List<GameObject> labels, InWorldLimitType type)
        {
            const int charges = (int)InWorldLimitType.DemoCharges;
            var label = labels != null && charges < labels.Count ? labels[charges] : null;
            var text = label != null ? label.GetComponentInChildren<TMPro.TMP_Text>(true) : null;
            if (text == null) return;
            if (Hinted.TryGetValue(text, out var was))
            {
                text.text = was;
                Hinted.Remove(text);
            }
            OwnLook(text);
            var group = label.GetComponent<CanvasGroup>();
            for (var i = Fades.Count - 1; i >= 0; i--)
                if (Fades[i].Group == null || Fades[i].Group == group) Fades.RemoveAt(i);
            if (group != null && OwnGroups.Contains(group)) group.alpha = 1f;
            if (type != InWorldLimitType.DemoCharges || !VrXr.IsRunning || Time.unscaledTime > warnAsked + WarnWindow) return;
            if (!Out(out _, out var limit)) return;
            was = text.text;
            Hinted[text] = was;
            text.text = LimitText(limit, warnHint);
            var own = text.color;
            Looks[text] = (own, text.fontStyle);
            text.color = WarnColor;
            text.fontStyle |= TMPro.FontStyles.Bold;
            if (group == null)
            {
                group = label.AddComponent<CanvasGroup>();
                group.interactable = false;
                group.blocksRaycasts = false;
                OwnGroups.Add(group);
            }
            // Only a label the mod may fade is kept longer and faded; one with a group of the game's is left to the game.
            if (OwnGroups.Contains(group))
            {
                group.alpha = 1f;
                if (shown != null) WarnTimeLeft(shown) = WarnShow + WarnFade;
                Fades.Add(new Fading { Group = group, Until = Time.time + WarnShow + WarnFade });
            }
            if (hintLogs++ < 5) Plugin.Log.LogInfo($"Charge by hand: the limit of {limit} charges is reached, told in the place of the game's warning '{was}' for {WarnShow:0} s, fading within {WarnFade:0.0} s; its own colour was {own}, the mod's text is {WarnColor}, bold");
        }

        // Each frame: the warnings fade towards their end (the game's clock, as its own count of the time left).
        private static void FadeWarnings()
        {
            for (var i = Fades.Count - 1; i >= 0; i--)
            {
                var fading = Fades[i];
                if (fading.Group == null)
                {
                    Fades.RemoveAt(i);
                    continue;
                }
                fading.Group.alpha = ChargeWarning.FadeOut(fading.Until - Time.time, WarnFade);
            }
        }

        // ---- Only the back clings (Manuell) ----

        // Put on by hand (Manuell), a charge clings with its back only (Raik, 2026-11-22: "so people do not just throw
        // stuff about"): one that meets a wall with its front, with its side, or tilted more than MostTilt against
        // the wall bounces off, and may cling at a later touch. With Automatisch a charge clings wherever it meets a
        // fitting surface, as in the game (Raik).
        // The game makes a clinging part fast in HierarchyCollisionListener.HandleECSCollisions: a part whose hierarchy
        // parent has Sticky is turned to the contact's normal, set on the surface and jointed, whichever side met it.
        // That call is left out for a charge that did not meet the wall back on (ChargeClingPatch); it keeps its
        // Sticky. The normal the game hands over is the same for both parts of a contact, so its sign tells nothing:
        // which side met the wall is told by where the contact lies against the charge's middle (its origin lies
        // between its back and its front, 3 cm before the back).
        // 35 degrees at first; Raik's test (2026-11-23): 45.
        private const float DefaultTilt = 45f;
        private static ConfigEntry<float> mostTilt;
        private static int clingLogs;

        private static float MostTilt
        {
            get
            {
                if (mostTilt == null && VrSettings.File != null)
                    mostTilt = VrSettings.Bind("Experimente", "SprengsatzHaftWinkel", DefaultTilt,
                        "With demo charges placed by hand (Manuell): a charge clings only with its back, tilted at most this many degrees against the wall. 0: any side clings, as in the game.");
                return mostTilt != null ? mostTilt.Value : DefaultTilt;
            }
        }

        private static bool BackOnly => On && !Auto && MostTilt > 0f;

        // From ChargeClingPatch: whether the game may make that part fast where it met the other. False for a demo
        // charge that still clings by itself and met the wall other than back on.
        public static bool ClingsBackOn(StructurePart part, Vector3 position, Vector3 normal, StructurePart otherPart)
        {
            if (!BackOnly || part == null || otherPart == null) return true;
            var blueprint = part.EntityBlueprintComponent;
            if (!EntityBlueprintComponent.IsValid(blueprint)) return true;
            var manager = blueprint.EntityManager;
            var entity = blueprint.Entity;
            if (!manager.HasComponent<DemoCharge>(entity)) return true;
            if (!manager.HasComponent<HierarchyMember>(entity)) return true;
            var parent = manager.GetComponentData<HierarchyMember>(entity).Parent;
            if (!manager.Exists(parent) || !manager.HasComponent<Sticky>(parent)) return true;
            var frame = part.transform;
            var along = Vector3.Dot(frame.forward, normal.normalized);
            var side = Vector3.Dot(position - frame.position, frame.forward);
            if (ChargeCling.BackOn(along, side, MostTilt)) return true;
            if (clingLogs++ < 40)
                Plugin.Log.LogInfo($"Charge by hand: a charge meets '{otherPart.name}' {(side > 0f ? "with its front" : "with its back")}, tilted {ChargeCling.Tilt(along):0}° against it (at most {MostTilt:0}° and back on): it does not cling");
            return false;
        }

        // The charge afloat goes back to the stock the game's way and one from the stock goes on the wall, as the game
        // places one.
        private static void Place(VrInput.Hand hand, DemoChargeRetrievalController charge, Vector3 point, Quaternion turn, StructurePart target)
        {
            charge.OnSuccess();
            var part = Instantiate.Invoke(controller, new object[] { point, turn }) as StructurePart;
            if (part == null) return;
            JointingService.Instance.JointStructureParts(part, target);
            ModifyCount?.Invoke(controller, new object[] { -1, false, false });
            hand.Buzz(0.6f, 0.08f);
        }

        private static void Forget(Afloat held)
        {
            held.Charge = null;
            held.Body = null;
            held.AtPlace = held.Shared = false;
            held.StickyOff = false;
            held.Near = held.Aimed = held.Shown = false;
        }

        // The trigger of a hand that holds a charge (Auto): the charge goes on where the preview shows it.
        private static void Fire(VrInput.Hand hand, Afloat held)
        {
            if (previewHand != hand || !held.Shown || held.Target == null || controller == null || Instantiate == null)
            {
                hand.Buzz(0.15f, 0.04f);
                if (logs++ < 60) Plugin.Log.LogInfo($"Charge by hand: trigger of the {hand.Name} hand, no place for its charge shown");
                return;
            }
            if (Refused() || LimitReached(held.Charge))
            {
                hand.Buzz(0.3f, 0.1f);
                if (logs++ < 60) Plugin.Log.LogInfo($"Charge by hand: trigger of the {hand.Name} hand, the charge does not go on (the place is taken or too many charges are out)");
                return;
            }
            var charge = held.Charge;
            var body = held.Body;
            var near = held.Near;
            var point = held.Point;
            var turn = held.Turn;
            var target = held.Target;
            Forget(held);
            VrClimb.Drop(hand, "the demo charge goes on with the trigger");
            var other = hand.IsLeft ? VrInput.Right : VrInput.Left;
            if (body != null && VrClimb.HeldBody(other) == body)
            {
                Forget(Of(other));
                VrClimb.Drop(other, "the demo charge goes on with the other hand's trigger");
            }
            Place(hand, charge, point, turn, target);
            reloadHand = hand;
            var data = Data(controller);
            reloadAt = Time.unscaledTime + (data != null ? Mathf.Clamp(data.PlacementCooldown, 0.1f, 2f) : 0.3f);
            if (logs++ < 60) Plugin.Log.LogInfo($"Charge by hand: the {hand.Name} hand's trigger puts its charge on {(near ? "the wall at its back" : "where the controller points")}");
        }

        // The hand that put its charge on with the trigger, its grip still closed: the next charge from behind the back,
        // after the game's wait (Raik, 2026-11-20: with the grip held the charges come into the hand one after another).
        private static void Reload()
        {
            var hand = reloadHand;
            if (hand == null) return;
            if (!Auto || !hand.Grip.Down)
            {
                reloadHand = null;
                return;
            }
            if (Time.unscaledTime < reloadAt) return;
            reloadHand = null;
            if (VrClimb.HeldBody(hand) != null || (hand.IsLeft ? VrHand.Left : VrHand.Right).State != HandState.Empty) return;
            if (TakesOut(hand) && logs++ < 60) Plugin.Log.LogInfo($"Charge by hand: the {hand.Name} hand kept its grip closed, the next charge comes into it");
        }

        // ---- The preview's test for room ----

        private static readonly AccessTools.FieldRef<DemoChargePreview, bool> RoomRefresh = AccessTools.FieldRefAccess<DemoChargePreview, bool>("mRefreshState");
        private static readonly AccessTools.FieldRef<DemoChargePreview, Vector3> RoomExtents = AccessTools.FieldRefAccess<DemoChargePreview, Vector3>("m_Extents");
        private static readonly AccessTools.FieldRef<DemoChargePreview, Collider> RoomTarget = AccessTools.FieldRefAccess<DemoChargePreview, Collider>("mTargetCol");
        private static readonly AccessTools.FieldRef<DemoChargePreview, StructurePart> RoomPart = AccessTools.FieldRefAccess<DemoChargePreview, StructurePart>("mTargetPart");
        private static readonly AccessTools.FieldRef<DemoChargePreview, JointSetupAsset> RoomJoint = AccessTools.FieldRefAccess<DemoChargePreview, JointSetupAsset>("mDemoChargeJSA");
        private static readonly AccessTools.FieldRef<DemoChargePreview, LayerMask> RoomMask = AccessTools.FieldRefAccess<DemoChargePreview, LayerMask>("mLayerMask");
        private static readonly MethodInfo RoomState = AccessTools.Method(typeof(DemoChargePreview), "SetState");
        private static readonly Collider[] Room = new Collider[32];
        private static int roomLogs;

        // From ChargePreviewRoomPatch (DemoChargePreview.FixedUpdate): while the preview shows for a charge in a hand, its
        // test for room is the game's, leaving out that charge, the player and the rig (Raik, 2026-11-18: it found the very
        // charge held to the wall and called the place taken). False to run the game's own.
        public static bool TestsRoom(DemoChargePreview preview)
        {
            if (previewHand == null || preview == null || preview != Preview() || RoomState == null) return false;
            if (!RoomRefresh(preview)) return true;
            RoomRefresh(preview) = false;
            var half = RoomExtents(preview) * 0.5f;
            var frame = preview.transform;
            var count = Physics.OverlapBoxNonAlloc(frame.position + frame.forward * (half.z + 0.001f), half, Room, frame.rotation, RoomMask(preview), QueryTriggerInteraction.Ignore);
            var target = RoomTarget(preview);
            Collider blocker = null;
            for (var i = 0; i < count && blocker == null; i++)
                if (Room[i] != target && !Ours(Room[i], null)) blocker = Room[i];
            var part = RoomPart(preview);
            var joins = part == null || part.StructurePartAsset == null
                || JointingService.Instance.CanJoint(part.StructurePartAsset.Data.JointSetupAsset, RoomJoint(preview));
            var taken = blocker != null || !joins;
            if (taken && !preview.IsBlocked && roomLogs++ < 30)
                Plugin.Log.LogInfo($"Charge by hand: the preview calls the place taken: {(blocker != null ? $"'{VrUtil.Chain(blocker.transform, 3)}' is in the way" : "the charge does not joint to that part")}");
            RoomState.Invoke(preview, new object[] { taken ? DemoChargePreviewState.InRangeBlocked : DemoChargePreviewState.InRangeUnblocked });
            return true;
        }

        // Let go at the wall its preview showed: on it goes. With the preview (Auto) not where it calls the place taken;
        // and with either way of placing not with the game's limit of charges reached (atLimit: then it is not to cling
        // by itself either). Until 0.32.0 the limit was asked with Auto only, and by hand (Manuell) any number went on.
        private static bool PutOn(VrInput.Hand hand, Afloat held, DemoChargeRetrievalController charge, out bool atLimit)
        {
            var near = held.Near;
            var shown = held.Shown;
            held.Near = held.Aimed = held.Shown = false;
            if (!near || held.Target == null || controller == null || Instantiate == null)
            {
                // Told here as well: let go at the limit it floats and never clings, and the warning says why.
                atLimit = LimitReached(charge);
                if (logs++ < 60) Plugin.Log.LogInfo($"Charge by hand: a charge afloat let go by the {hand.Name} hand with no wall within {Reach * 100f:0} cm of its back");
                return false;
            }
            var taken = Auto && shown && Refused();
            atLimit = taken ? AtLimit(charge) : LimitReached(charge);
            if (taken || atLimit)
            {
                hand.Buzz(0.3f, 0.1f);
                if (logs++ < 60) Plugin.Log.LogInfo($"Charge by hand: a charge let go by the {hand.Name} hand at a wall does not go on ({(taken ? "the place is taken" : "the game's limit of charges out is reached")})");
                return false;
            }
            Place(hand, charge, held.Point, held.Turn, held.Target);
            if (logs++ < 60) Plugin.Log.LogInfo($"Charge by hand: a charge afloat let go by the {hand.Name} hand {held.Reach * 100f:0} cm from a wall, put on it");
            return true;
        }

        // How a charge afloat lies in the hand that holds it, for the log, once a second: as the hold before 0.31.5 was
        // given, so a hold Raik likes can be read off and made the one a charge comes into the hand with (StandardHold).
        private static float nextHeldMeasure;
        private static int heldMeasures;

        private static void MeasureHeld(VrInput.Hand hand, Afloat held)
        {
            if (heldMeasures >= 80 || Time.unscaledTime < nextHeldMeasure) return;
            var part = held.Charge.DemoCharge;
            var rig = VrCamera.Instance != null ? VrCamera.Instance.transform.parent : null;
            if (part == null || rig == null || !hand.TryGetAimRotation(out var aimLocal) || !hand.TryGetPosition(out var gripLocal)) return;
            nextHeldMeasure = Time.unscaledTime + 1f;
            heldMeasures++;
            var scale = VrCamera.WorldScale;
            var frame = part.transform;
            var toController = Quaternion.Inverse(rig.rotation * aimLocal);
            var back = toController * -frame.forward;
            var top = toController * frame.up;
            var middle = toController * (frame.position + frame.forward * (BackOfMesh * scale) - rig.TransformPoint(gripLocal)) / scale * 100f;
            Plugin.Log.LogInfo($"Charge by hand: held in the {hand.Name} hand: back ({back.x:0.00} {back.y:0.00} {back.z:0.00}), top ({top.x:0.00} {top.y:0.00} {top.z:0.00}), "
                + $"middle ({middle.x:0} {middle.y:0} {middle.z:0}) cm from the grip, in the controller's axes");
        }

        private static readonly Afloat[] afloat = { new Afloat(), new Afloat() };
        private static StructurePart fresh;
        private static float freshUntil;
        // The charge made last, for the hands to pass through while it has no body yet.
        private static StructurePart newest;
        private static float newestAt = -10f;
        // The hand the next charge made goes into (HandTo).
        private static VrInput.Hand giveTo;
        private static float giveUntil;

        // From ChargeFreshPatch: the game has just made that charge. Its body comes with its jointing, a moment later.
        public static void Fresh(StructurePart part)
        {
            fresh = part;
            freshUntil = Time.unscaledTime + 1f;
            newest = part;
            newestAt = Time.unscaledTime;
            if (giveTo != null && Time.unscaledTime <= giveUntil) VrChargeDisarm.Give(part, giveTo);
            giveTo = null;
        }

        // From VrToolInput: the charges held with both hands, the first hand lets go. The charge let go now goes into
        // that hand as the thing it is, held where that hand took hold of it (Raik, 2026-11-18: handed over as the
        // game's tool it jumped into the hand's fit).
        public static void HandTo(VrInput.Hand hand)
        {
            giveTo = hand;
            giveUntil = Time.unscaledTime + 1f;
        }

        // Whether the hands pass through that collider: of the charge just made, or of a part the arms spare.
        public static bool LeftAlone(Collider collider)
        {
            if (collider == null) return false;
            if (newest != null && Time.unscaledTime - newestAt < 1f && collider.transform.IsChildOf(newest.transform)) return true;
            return VrArmPush.Spares(collider.attachedRigidbody);
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
            Reactivate();
            FadeWarnings();
            ShowPreview();
            Reload();
            Fit();
        }

        // While the tools are fitted (VrToolFit.Charge): the charge in a hand, afloat or the game's, is dragged by the
        // other hand. One afloat is held by its hand as the dragged hold has it.
        private static void Fit()
        {
            var hand = !VrXr.IsRunning ? null : Holds(VrInput.Left) ? VrInput.Left : Holds(VrInput.Right) ? VrInput.Right : null;
            var afloatHand = hand;
            if (hand == null && VrXr.IsRunning && Decoupled && !DetonatorOut)
            {
                var tool = VrHand.GameToolHand;
                if (tool != null && tool.HoldsTool && tool.Tool == EquipmentController.Equipment.DemoCharge) hand = tool.Input;
            }
            VrToolFit.Charge(hand);
            if (afloatHand == null || !VrToolFit.ChargeDragged) return;
            var held = afloat[afloatHand.IsLeft ? 0 : 1];
            var part = held.Charge != null ? held.Charge.DemoCharge : null;
            if (part == null || held.Body == null || !HoldPose(afloatHand, out var origin, out var turned)) return;
            var frame = part.transform;
            var body = held.Body;
            var inverse = Quaternion.Inverse(frame.rotation);
            VrClimb.Refit(afloatHand, origin + turned * (inverse * (body.position - frame.position)), turned * (inverse * body.rotation));
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
                    EndHold(held, held.Charge);
                    held.Body = body;
                    held.AtPlace = false;
                    held.Shared = false;
                    held.Near = held.Aimed = held.Shown = false;
                    held.Charge = body.mass <= HeaviestCharge ? body.GetComponentInChildren<DemoChargeRetrievalController>(true) : null;
                    if (held.Charge != null && Deployed(held.Charge)) held.Charge = null;
                }
                if (held.Charge == null) return;
                // Placed the game's way, a charge in a hand is the game's tool: this one goes back to the stock, and the
                // charges come into the hand that closed round it.
                if (!On && VrAim.Active)
                {
                    var taken = held.Charge;
                    held.Charge = null;
                    VrClimb.Drop(hand, "a demo charge afloat becomes the charges in the hand");
                    taken.OnSuccess();
                    VrToolInput.TakeCharges(hand);
                    hand.Buzz(0.4f, 0.05f);
                    if (logs++ < 60) Plugin.Log.LogInfo($"Charge by hand: a charge afloat taken by the {hand.Name} hand goes back to the stock, the hand gets the charges as the game's tool");
                    return;
                }
                Unstick(held);
                Aim(hand, held);
                MeasureHeld(hand, held);
                var shared = VrClimb.HeldBody(hand.IsLeft ? VrInput.Right : VrInput.Left) == body;
                if (held.Shared && !shared && VrToolFit.On) StoreHold(hand, held);
                held.Shared = shared;
                var at = VrToolBelt.Stows(VrToolBelt.Zone.DemoCharge, hand);
                if (at && !held.AtPlace) hand.Buzz(0.25f, 0.04f);
                held.AtPlace = at;
                if (Auto && hand.Trigger.Pressed) Fire(hand, held);
                return;
            }
            var was = held.Charge;
            var wasBody = held.Body;
            var wasAt = held.AtPlace;
            held.Charge = null;
            held.Body = null;
            held.AtPlace = false;
            // Let go with the grip (not lost by tracking), and not still held by the other hand.
            if (was == null || hand.Grip.Down || (wasBody != null && VrClimb.HeldBody(hand.IsLeft ? VrInput.Right : VrInput.Left) == wasBody))
            {
                held.Near = held.Aimed = held.Shown = false;
                EndHold(held, was);
                return;
            }
            var away = !wasAt && !VrToolBelt.Stows(VrToolBelt.Zone.DemoCharge, hand);
            if (away)
            {
                if (PutOn(hand, held, was, out var atLimit))
                {
                    held.StickyOff = false;
                    return;
                }
                if (Floats)
                {
                    // With the game's limit reached it does not cling by itself where it drifts, and can be taken
                    // and put back behind the back.
                    if (atLimit) LeaveLoose(held);
                    else Restick(held);
                    if (logs++ < 40) Plugin.Log.LogInfo($"Charge by hand: a charge afloat let go by the {hand.Name} hand at {VrToolBelt.ChargePlace(hand)}, not behind the back: it floats{(atLimit ? " and does not cling (limit of charges reached)" : "")}");
                    return;
                }
            }
            held.Near = held.Aimed = held.Shown = false;
            held.StickyOff = false;
            was.OnSuccess();
            hand.Buzz(1f, 0.15f);
            if (logs++ < 40)
                Plugin.Log.LogInfo(away
                    ? $"Charge by hand: a charge let go by the {hand.Name} hand away from a wall is back with the charges (no extended tool physics)"
                    : $"Charge by hand: a charge afloat put back behind the back by the {hand.Name} hand");
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
            HidePreview(false);
            previewOf = null;
            previewParts = null;
            if (frame != null) Object.Destroy(frame.gameObject);
            if (throwFrame != null) Object.Destroy(throwFrame.gameObject);
            frame = throwFrame = null;
            mesh = null;
            meshRenderer = null;
            controller = null;
            foreach (var pair in Hinted)
                if (pair.Key != null) pair.Key.text = pair.Value;
            Hinted.Clear();
            foreach (var pair in Looks)
                if (pair.Key != null)
                {
                    pair.Key.color = pair.Value.Color;
                    pair.Key.fontStyle = pair.Value.Style;
                }
            Looks.Clear();
            Fades.Clear();
            foreach (var group in OwnGroups)
                if (group != null) Object.Destroy(group);
            OwnGroups.Clear();
            try { GiveBackSticky(); }
            catch (System.Exception e) { Plugin.Log.LogWarning($"Charge by hand: giving the loose charges their cling back on unload failed ({e.Message})"); }
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

    // The preview's test for room, for a charge in a hand (VrChargeByHand.TestsRoom).
    [HarmonyPatch(typeof(DemoChargePreview), "FixedUpdate")]
    internal static class ChargePreviewRoomPatch
    {
        private static bool Prefix(DemoChargePreview __instance) => !VrChargeByHand.TestsRoom(__instance);
    }

    // The game's HUD switches its limit warnings: the charges' one gets the mod's text and time (VrChargeByHand.LimitShown).
    [HarmonyPatch(typeof(InWorldLimitUIController), "ToggleWarnings")]
    internal static class ChargeLimitHintPatch
    {
        private static readonly AccessTools.FieldRef<InWorldLimitUIController, List<GameObject>> Labels =
            AccessTools.FieldRefAccess<InWorldLimitUIController, List<GameObject>>("m_WarningLabels");

        private static void Postfix(InWorldLimitUIController __instance, InWorldLimitType type)
        {
            try { VrChargeByHand.LimitShown(__instance, Labels(__instance), type); }
            catch (System.Exception e) { Plugin.Log.LogWarning($"Charge by hand: the limit hint failed ({e.Message})"); }
        }
    }

    // The game makes a clinging part fast where it met another: not a demo charge that did not meet it back on
    // (VrChargeByHand.ClingsBackOn). The rest of that call is for frozen parts, which a charge is not.
    [HarmonyPatch]
    internal static class ChargeClingPatch
    {
        private static System.Reflection.MethodBase TargetMethod() =>
            AccessTools.Method(typeof(HierarchyCollisionListener), "HandleECSCollisions");

        private static bool Prefix(StructurePart part, Vector3 position, Vector3 normal, StructurePart otherPart)
        {
            try { return VrChargeByHand.ClingsBackOn(part, position, normal, otherPart); }
            catch (System.Exception e)
            {
                if (failed++ < 3) Plugin.Log.LogWarning($"Charge by hand: the test for a charge meeting a wall back on failed ({e.Message}), it clings as in the game");
                return true;
            }
        }

        private static int failed;
    }

    // Whether a demo charge met a wall with its back, the part that is plain numbers.
    internal static class ChargeCling
    {
        // How far the charge is tilted against the wall, in degrees: 0 flat on it (either face), 90 on edge. along is
        // the cosine between the charge's forward and the wall's normal, whichever way that normal points.
        public static float Tilt(float along) => Mathf.Acos(Mathf.Clamp01(Mathf.Abs(along))) * Mathf.Rad2Deg;

        // Back on: the contact lies behind the charge's middle (side, along its forward, negative) and the charge is
        // tilted no more than most degrees.
        public static bool BackOn(float along, float side, float most) => side <= 0f && Tilt(along) <= most;
    }

    // The limit warning's fading, the part that is plain numbers.
    internal static class ChargeWarning
    {
        // How much of the warning shows with that long left of it: all of it until its last 'fade' seconds.
        public static float FadeOut(float left, float fade) => fade <= 0f ? (left > 0f ? 1f : 0f) : Mathf.Clamp01(left / fade);
    }

    [HarmonyPatch(typeof(DemoChargeAsset), nameof(DemoChargeAsset.SetComponentData))]
    internal static class ChargeLaunchPatch
    {
        private static void Postfix(EntityManager entityManager, Entity entity) => VrChargeByHand.Launch(entityManager, entity);
    }
}
