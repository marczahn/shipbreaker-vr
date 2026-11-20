using BBI.Unity.Game;
using Carbon.Audio;
using HarmonyLib;
using Unity.Entities;
using UnityEngine;

namespace ShipbreakerVR
{
    // The charges and their detonator in the hands at once, with the charges placed the game's way (Automatisch; Raik,
    // 2026-11-19: "that you can hold charges and the detonator at the same time", as with Manuell). The game has them as
    // one tool in two modes, and with Automatisch the charges must stay that tool, for the preview and the trigger. So the
    // other hand gets a copy of the detonator (as VrBeltGrapple shows it at the hip), held as the game's hand would hold
    // the detonator (ZuenderHaltung, VrToolFit, mirrored for the left hand), and its trigger sets the charges off as the
    // game's detonator would (DemoChargeController.UpdateDetonationState), without the tool leaving its mode:
    //   - no charge out: the game's click for that;
    //   - too much debris in the world: the game's warning;
    //   - the detonator worn out and failing (DemoChargeDurabilityHandler): the game's failing sound;
    //   - else the game's detonating sequence (the same ECS component the game's state Detonating adds), its sound and its
    //     wear on the detonator. The component goes again once no charge is left, as the game's own state does it.
    // Taken by the grip pressed at the detonator's place on the hip, while the other hand holds the charges as the game's
    // tool. Let go by opening the grip: the detonator is back at the hip. Should the charges leave the other hand while
    // this one still holds the copy, this hand gets the game's detonator instead (VrToolInput.TakeDetonator).
    internal static class VrSecondDetonator
    {
        // The game's private parts, in a class of their own: the hand tests ask Holds without the game.
        private static class Game
        {
            public static readonly AccessTools.FieldRef<DemoChargeController, DemoChargeState> State =
                AccessTools.FieldRefAccess<DemoChargeController, DemoChargeState>("mState");
            public static readonly AccessTools.FieldRef<DemoChargeController, EntityQuery> Query =
                AccessTools.FieldRefAccess<DemoChargeController, EntityQuery>("mDemoChargeQuery");
            public static readonly AccessTools.FieldRef<DemoChargeController, IDemoChargeControllerData> Data =
                AccessTools.FieldRefAccess<DemoChargeController, IDemoChargeControllerData>("mData");
            public static readonly AccessTools.FieldRef<DemoChargeController, DemoChargeDurabilityHandler> Durability =
                AccessTools.FieldRefAccess<DemoChargeController, DemoChargeDurabilityHandler>("mDurabilityHandler");
            public static readonly AccessTools.FieldRef<DemoChargeController, ObjectLimitSystem> Limit =
                AccessTools.FieldRefAccess<DemoChargeController, ObjectLimitSystem>("mObjectLimitSystem");
            public static readonly System.Reflection.MethodInfo AddSequence = AccessTools.Method(typeof(DemoChargeController), "AddDetonatingSequenceComponent");
            public static readonly System.Reflection.MethodInfo RemoveSequence = AccessTools.Method(typeof(DemoChargeController), "RemoveDetonatingSequenceComponent");
            public static readonly System.Reflection.MethodInfo HasSequence = AccessTools.Method(typeof(DemoChargeController), "HasDetonatingSequenceComponent");
        }

        // How long this hand keeps showing the copy while the game's detonator comes into it.
        private const float HandOverTime = 1f;

        private static VrInput.Hand holder;
        private static bool pulled;
        private static bool handing;
        private static float handingUntil;
        private static bool detonating;
        private static DemoChargeController controller;
        private static GameObject copy;
        private static Transform copyOf;
        private static float nextCopy;
        private static bool unloadKnown;
        private static int logs;

        // Whether that hand holds the copy (VrHand.Free, VrControls: the left trigger does not fly then).
        public static bool Holds(VrInput.Hand hand) => holder != null && holder == hand;

        // A hand holds the copy (VrBeltGrapple: none shows at the hip then).
        public static bool Active => holder != null;

        // The charges go off from the copy's trigger (VrDetonatorLights: blinking red).
        public static bool Detonating => detonating;

        // For VrDetonatorLights: the copy, while it shows.
        internal static Transform Copy => copy != null && copy.activeSelf ? copy.transform : null;

        private static VrHand Of(VrInput.Hand hand) => hand.IsLeft ? VrHand.Left : VrHand.Right;

        // From VrToolInput.Draw: the grip pressed at the detonator's place. True if the copy came into that hand.
        public static bool Take(VrInput.Hand hand)
        {
            if (!VrXr.IsRunning || !VrAim.Active || VrChargeByHand.Manual || holder != null) return false;
            var me = Of(hand);
            var other = me.Other;
            if (me.State != HandState.Empty || VrHand.GameToolHand != other || !Charges(other)) return false;
            if (VrChargeByHand.DetonatorOut || VrDualTools.Second != EquipmentController.Equipment.None) return false;
            if (VrLooseTools.DetonatorLoose || VrLooseTools.DetonatorModel == null) return false;
            if (!unloadKnown)
            {
                unloadKnown = true;
                VrSession.OnUnload("second detonator", Release);
            }
            holder = hand;
            pulled = true;
            handing = false;
            hand.Buzz(0.4f, 0.05f);
            if (logs++ < 40) Plugin.Log.LogInfo($"Second detonator: the {hand.Name} hand takes the detonator beside the charges in the {other.Name} hand");
            return true;
        }

        // The game's charges in that hand, as its tool, held.
        private static bool Charges(VrHand hand) => hand.HoldsTool && hand.Tool == EquipmentController.Equipment.DemoCharge && hand.Input.Grip.Down;

        // From VrFrame once the rig is placed, before VrBeltGrapple and VrDetonatorLights.
        public static void Update()
        {
            Settle();
            if (holder == null)
            {
                Show(false);
                return;
            }
            var me = Of(holder);
            string why = null;
            if (!VrXr.IsRunning) why = "VR off";
            else if (!holder.Grip.Down) why = "let go";
            else if (handing)
            {
                if (VrHand.GameToolHand == me && VrLooseTools.DetonatorShowsInHand) why = "the game's detonator is in that hand now";
                else if (Time.unscaledTime > handingUntil) why = "the game's detonator did not come";
            }
            else if (me.State != HandState.Empty) why = "the hand took something else";
            else if (!Charges(me.Other) || VrHand.GameToolHand != me.Other || VrChargeByHand.DetonatorOut)
            {
                handing = true;
                handingUntil = Time.unscaledTime + HandOverTime;
                VrToolInput.TakeDetonator(holder);
                if (logs++ < 40) Plugin.Log.LogInfo($"Second detonator: the charges left the {me.Other.Name} hand, the {holder.Name} hand gets the game's detonator");
            }
            if (why != null)
            {
                if (logs++ < 40) Plugin.Log.LogInfo($"Second detonator: the {holder.Name} hand lets go of it ({why})");
                holder = null;
                handing = false;
                Show(false);
                return;
            }
            var trigger = holder.Trigger.Down;
            if (trigger && !pulled && !handing) Fire();
            pulled = trigger;
            Place();
        }

        // The trigger pulled: as the game's detonator would.
        private static void Fire()
        {
            if (controller == null) controller = Object.FindObjectOfType<DemoChargeController>();
            if (controller == null) return;
            holder.Buzz(0.5f, 0.08f);
            try
            {
                if (detonating || Game.State(controller) == DemoChargeState.Detonating || Game.HasSequence.Invoke(controller, null) is bool has && has) return;
                var data = Game.Data(controller);
                var limit = Game.Limit(controller);
                var durability = Game.Durability(controller);
                string what;
                if (Game.Query(controller).IsEmptyIgnoreFilter)
                {
                    Sound(data.DetonateWithoutPlaced);
                    what = "no charge out";
                }
                else if (limit != null && !limit.IsUnderLimit)
                {
                    Sound(Main.Instance.MainSettings.ObjectLimitSettings.LimitWarningAudioEvent);
                    ((Carbon.Core.Events.IEventBroadcaster)Main.EventSystem).Post(InWorldLimitReachedEvent.GetEvent(InWorldLimitType.Debris));
                    what = "too much debris in the world";
                }
                else if (durability != null && durability.ShouldDetonatorFail())
                {
                    Sound(data.DetonationFailed);
                    what = "the worn detonator fails";
                }
                else
                {
                    Game.AddSequence.Invoke(controller, null);
                    Sound(data.DetonateWithPlaced);
                    durability?.HandleDurabilityDamageOfType(DurabilityDamageDef.DurabilityDamageType.DetonatorUsed);
                    detonating = true;
                    what = "the charges go off";
                }
                if (logs++ < 40) Plugin.Log.LogInfo($"Second detonator: trigger pulled, {what}");
            }
            catch (System.Exception e)
            {
                controller = null;
                Plugin.Log.LogWarning($"Second detonator: could not set the charges off: {e.Message}");
            }
        }

        private static void Sound(WwiseShortID id) => ((Carbon.Core.Events.IEventBroadcaster)Main.EventSystem).Post(MasterSFXEvent.GetEvent(id));

        // The detonating sequence ends once no charge is left, as the game's state Detonating ends it; the game's own
        // state going off as well takes it over.
        private static void Settle()
        {
            if (!detonating) return;
            try
            {
                if (controller == null) detonating = false;
                else if (Game.State(controller) == DemoChargeState.Detonating) detonating = false;
                else if (Game.Query(controller).IsEmptyIgnoreFilter)
                {
                    Game.RemoveSequence.Invoke(controller, null);
                    detonating = false;
                    if (logs++ < 40) Plugin.Log.LogInfo("Second detonator: all charges went off");
                }
            }
            catch (System.Exception)
            {
                // The world is gone (a new shift), and the sequence with it.
                controller = null;
                detonating = false;
            }
        }

        // The copy in the hand, as the game's hand holds the detonator.
        private static void Place()
        {
            var prop = VrLooseTools.DetonatorModel;
            if (prop == null)
            {
                Show(false);
                return;
            }
            if ((copy == null || copyOf != prop) && Time.unscaledTime >= nextCopy)
            {
                nextCopy = Time.unscaledTime + 2f;
                if (copy != null) Object.Destroy(copy);
                copy = VrBeltGrapple.CopyDetonator(prop, "ShipbreakerVR detonator beside the charges", out var parts, out _);
                copyOf = copy != null ? prop : null;
                Plugin.Log.LogInfo($"Second detonator: {parts} part(s) copied from '{prop.name}'");
            }
            if (copy == null || !VrHands.HandFrame(holder.IsLeft, out var wrist, out var frame)
                || !VrToolFit.DetonatorHold.TryGet(out var at, out var turn) && !VrHands.DetonatorRest(out at, out turn))
            {
                Show(false);
                return;
            }
            if (holder.IsLeft) VrToolFit.Mirror(ref at, ref turn);
            copy.transform.SetPositionAndRotation(wrist + frame * (at * VrCamera.WorldScale), frame * turn);
            Show(true);
        }

        private static void Show(bool on)
        {
            if (copy != null && copy.activeSelf != on) copy.SetActive(on);
        }

        // VR off or the mod unloaded: the copy goes, and a sequence it started ends.
        private static void Release()
        {
            holder = null;
            handing = false;
            if (detonating && controller != null)
                try { Game.RemoveSequence.Invoke(controller, null); }
                catch (System.Exception) { }
            detonating = false;
            controller = null;
            if (copy != null) Object.Destroy(copy);
            copy = null;
            copyOf = null;
        }
    }
}
