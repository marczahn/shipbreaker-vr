using System.Collections.Generic;
using BBI.Unity.Game;
using UnityEngine;

namespace ShipbreakerVR
{
    // The grapple was built for the flat game's view and looks oversized in the hand. Its model goes down in size around
    // the grip, so the hand keeps its hold; the stickers on it keep their size. The barrel is part of the model, so laser
    // and line leave from where it ends up.
    internal static class VrGrappleSize
    {
        private const float Size = 0.9f;
        private const float SearchEvery = 1f;

        private static readonly List<StickerSlot> slots = new List<StickerSlot>();
        private static Transform model;
        private static Vector3 restScale;
        private static Vector3 restPosition;
        private static float nextSearch;

        public static void Apply()
        {
            if (model != null || Time.unscaledTime < nextSearch) return;
            nextSearch = Time.unscaledTime + SearchEvery;
            var hook = Object.FindObjectOfType<GrapplingHook>();
            var part = hook != null ? VrToolModels.GrappleBarrelOf(hook) : null;
            while (part != null && !part.name.StartsWith("PRFB_GrappleGun")) part = part.parent;
            if (part == null || part.parent == null) return;
            model = part;
            restScale = model.localScale;
            restPosition = model.localPosition;
            var grip = Find(model, "RHandTarget");
            var held = grip != null ? model.parent.InverseTransformPoint(grip.position) : model.localPosition;
            model.localScale = restScale * Size;
            if (grip != null) model.localPosition += held - model.parent.InverseTransformPoint(grip.position);
            model.GetComponentsInChildren(true, slots);
            foreach (var slot in slots) slot.transform.localScale /= Size;
            Plugin.Log.LogInfo($"Grapple at {Size:0.00} of its size around {(grip != null ? grip.name : "its pivot")}, {slots.Count} sticker slot(s) kept");
        }

        public static void Reset()
        {
            if (model == null) return;
            foreach (var slot in slots)
                if (slot != null) slot.transform.localScale *= Size;
            slots.Clear();
            model.localScale = restScale;
            model.localPosition = restPosition;
            model = null;
        }

        private static Transform Find(Transform root, string name)
        {
            foreach (var part in root.GetComponentsInChildren<Transform>(true))
                if (part.name == name) return part;
            return null;
        }
    }
}
