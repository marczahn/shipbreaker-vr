using BBI.Unity.Game;
using Carbon.Audio;
using HarmonyLib;
using UnityEngine;

namespace ShipbreakerVR
{
    // Steuerung/UeberDieSchulterEinpacken (Raik, 2026-11-11): things to collect (cables, circuit boards, data cassettes)
    // go into an imaginary backpack. Held in a hand (VrClimb) and let go over the left shoulder, about level with the
    // cutter's grip over the right one (VrToolBelt.OverLeftShoulder), they are collected as the game collects them when
    // their prompt is held to the end (InteractionController.HandleInteractionProgress): the interactable completes, which
    // plays its own collect sound and removes it, and the game hears that its progress completed, which hands out what it
    // is worth. A short rattle of keys and a buzz of that controller confirm it. Either hand stows: the hand that held it
    // has its grip, and the left hand reaches its own shoulder as easily as the right one.
    // Only loose parts whose interaction removes them (InteractableObject.DestroyOnComplete): switches, consoles and the
    // air conditioner stay where they are.
    internal static class VrShoulderStow
    {
        // The game's own sound of keys picked up in the Hab ('sfx_HAB_pickup_keys' in its sound banks), played as is,
        // as the rattle of a full backpack: no sound file of the mod's own.
        private const uint RattleSound = 1711490748;
        private const float BuzzStrength = 0.6f;
        private const float BuzzTime = 0.08f;

        private static readonly AccessTools.FieldRef<InteractionController, InteractableObject> Current =
            AccessTools.FieldRefAccess<InteractionController, InteractableObject>("mCurrentInteractable");
        private static readonly System.Action<InteractionController> EndInteraction =
            AccessTools.MethodDelegate<System.Action<InteractionController>>(AccessTools.Method(typeof(InteractionController), "EndInteraction"));

        private class Held
        {
            public InteractableObject Thing;
            public Rigidbody Body;
            public bool Over;
        }

        private static readonly Held Left = new Held();
        private static readonly Held Right = new Held();
        private static InteractionController interaction;
        private static int logs;

        // From VrFrame, once a frame: after the hands held on or let go in the physics steps (VrClimb).
        public static void Update()
        {
            if (!VrXr.IsRunning || VrSettings.ShoulderStow == null || !VrSettings.ShoulderStow.Value
                || VrSettings.Climbing == null || !VrSettings.Climbing.Value)
            {
                Left.Thing = Right.Thing = null;
                Left.Body = Right.Body = null;
                return;
            }
            Check(VrInput.Left, Left);
            Check(VrInput.Right, Right);
        }

        private static void Check(VrInput.Hand hand, Held held)
        {
            if (VrClimb.HeldLoose(hand, out var body, out var thing) && Collectable(thing))
            {
                held.Thing = thing;
                held.Body = body;
                held.Over = VrToolBelt.HeadLocal(hand, out var local) && VrToolBelt.OverLeftShoulder(local);
                return;
            }
            var was = held.Thing;
            var wasBody = held.Body;
            held.Thing = null;
            held.Body = null;
            // Let go with the grip (not lost by tracking) while over the shoulder, and not still held by the other hand.
            if (was == null || hand.Grip.Down) return;
            if (wasBody != null && VrClimb.HeldBody(hand.IsLeft ? VrInput.Right : VrInput.Left) == wasBody) return;
            var over = held.Over || (VrToolBelt.HeadLocal(hand, out var at) && VrToolBelt.OverLeftShoulder(at));
            if (!over)
            {
                if (logs < 30 && VrToolBelt.HeadLocal(hand, out var off) && off.z < 0.1f && off.y > -0.3f)
                {
                    logs++;
                    Plugin.Log.LogInfo($"Shoulder stow: '{was.name}' let go by the {hand.Name} hand at ({off.x:0.00}, {off.y:0.00}, {off.z:0.00}) m from the head, not over the left shoulder");
                }
                return;
            }
            Stow(hand, was);
        }

        private static bool Collectable(InteractableObject thing) =>
            thing != null && thing.isActiveAndEnabled && thing.DestroyOnComplete;

        private static void Stow(VrInput.Hand hand, InteractableObject thing)
        {
            if (thing == null || !thing.isActiveAndEnabled) return;
            if (!thing.IsInteractable || !thing.HasUsesRemaining)
            {
                if (logs++ < 30) Plugin.Log.LogInfo($"Shoulder stow: '{thing.name}' cannot be collected now (interactable {thing.IsInteractable}, uses left {thing.HasUsesRemaining})");
                return;
            }
            var name = thing.name;
            // A prompt the game is working on this thing ends first, as the game ends it on completing.
            if (interaction == null) interaction = Object.FindObjectOfType<InteractionController>();
            if (interaction != null && Current(interaction) == thing) EndInteraction(interaction);
            thing.Complete();
            ((Carbon.Core.Events.IEventBroadcaster)Main.EventSystem).Post(InteractionEvent.GetEvent(InteractionEvent.InteractionType.ProgressComplete, thing));
            // One that waits for the game's hand to take it (WaitForGrab) is taken now: the player's hand did.
            if (thing != null && thing.isActiveAndEnabled && thing.WaitForGrab) thing.OnGrabComplete();
            // Played as the game plays its sound of a completed interaction (MasterSFXEvent), not at a place.
            ((Carbon.Core.Events.IEventBroadcaster)Main.EventSystem).Post(MasterSFXEvent.GetEvent(new WwiseShortID(RattleSound)));
            hand.Buzz(BuzzStrength, BuzzTime);
            if (logs++ < 60) Plugin.Log.LogInfo($"Shoulder stow: '{name}' collected over the left shoulder by the {hand.Name} hand");
        }
    }
}
