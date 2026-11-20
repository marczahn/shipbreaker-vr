using System.Collections.Generic;
using BBI.Unity.Game;
using HarmonyLib;
using Unity.Entities;
using UnityEngine;

namespace ShipbreakerVR
{
    // The detonator's lights tell what the charges do (Raik, 2026-11-19, testing 0.31.0): yellow once at least one charge
    // is out in the world (on a wall or afloat), ready to go off; green while all are with the player; blinking red while
    // the trigger is pressed and they go off. The same on the detonator in the hand, its copy at the hip
    // (VrBeltGrapple) and afloat (VrLooseTools). Before, the copy at the hip lit yellow always and the one in the hand
    // only as the game's animator had it.
    // The lights are no objects of their own: they are the glowing parts of the body's material
    // ('M_Tool_ExplosiveChargeDetonator', a shader graph whose glow colors are Color_2B5A31C3 and Color_73D23370, both
    // the orange-yellow (47.9, 8.3, 0) also in _EmissiveColor). They are set through each renderer's property block,
    // after the game's animation and before rendering (VrFrame, once the rig is placed), so whatever else the game keeps
    // there (its wear on the detonator, DemoChargeDurabilityHandler) stays.
    // The game's own count of charges out (DemoChargeController.mDemoChargeQuery, as CheckDetonatorLightUp asks it):
    // charges in the world that are not going off yet.
    internal static class VrDetonatorLights
    {
        static VrDetonatorLights() => VrSession.OnUnload("detonator lights", Release);

        private const string MaterialName = "M_Tool_ExplosiveChargeDetonator";
        private static readonly int[] GlowColors =
        {
            Shader.PropertyToID("Color_2B5A31C3"),
            Shader.PropertyToID("Color_73D23370"),
            Shader.PropertyToID("_EmissiveColor"),
        };

        // A third as bright as the game's own yellow (its luminance about 16): at full strength they glared and
        // outshone everything around them (Raik, 2026-11-19: "the colors are very good, a little less bright").
        private const float Brightness = 0.33f;
        private static readonly Color Yellow = new Color(47.937256f, 8.282353f, 0f, 0f) * Brightness;
        private static readonly Color Green = new Color(1.5f, 21f, 2f, 0f) * Brightness;
        private static readonly Color Red = new Color(60f, 1.5f, 0f, 0f) * Brightness;
        // Blinking: this many times a second, and red at least this long once the trigger was pressed.
        private const float BlinkRate = 4f;
        private const float RedAtLeast = 1.5f;

        private static readonly AccessTools.FieldRef<DemoChargeController, EntityQuery> Query =
            AccessTools.FieldRefAccess<DemoChargeController, EntityQuery>("mDemoChargeQuery");
        private static readonly AccessTools.FieldRef<DemoChargeController, DemoChargeState> State =
            AccessTools.FieldRefAccess<DemoChargeController, DemoChargeState>("mState");

        private class Lit
        {
            public Transform Root;
            public readonly List<Renderer> Glowing = new List<Renderer>();
        }

        private static readonly Lit InHand = new Lit();
        private static readonly Lit AtHip = new Lit();
        private static readonly Lit Afloat = new Lit();
        private static readonly Lit Beside = new Lit();
        private static readonly MaterialPropertyBlock Block = new MaterialPropertyBlock();
        private static DemoChargeController controller;
        private static float nextFind;
        private static float redSince = -10f, redUntil = -10f;
        private static string told;

        // From VrFrame once the rig is placed: after the game's animation, before rendering.
        public static void Update()
        {
            if (!VrXr.IsRunning)
            {
                Release();
                return;
            }
            var color = Wanted(out var what);
            Paint(InHand, VrLooseTools.DetonatorModel, color);
            Paint(AtHip, VrBeltGrapple.DetonatorCopy, color);
            Paint(Afloat, VrLooseTools.DetonatorAfloat, color);
            Paint(Beside, VrSecondDetonator.Copy, color);
            if (what != told)
            {
                told = what;
                Plugin.Log.LogInfo($"Detonator lights: {what} ({InHand.Glowing.Count} glowing part(s) in the hand, {AtHip.Glowing.Count} at the hip, {Afloat.Glowing.Count} afloat)");
            }
        }

        private static Color Wanted(out string what)
        {
            if (controller == null && Time.unscaledTime >= nextFind)
            {
                nextFind = Time.unscaledTime + 2f;
                controller = Object.FindObjectOfType<DemoChargeController>();
            }
            if (controller == null)
            {
                what = "no charges' controller, green";
                return Green;
            }
            var now = Time.unscaledTime;
            if (State(controller) == DemoChargeState.Detonating || VrSecondDetonator.Detonating)
            {
                if (now > redUntil)
                {
                    redSince = now;
                    redUntil = now + RedAtLeast;
                }
                else redUntil = Mathf.Max(redUntil, now);
            }
            if (now <= redUntil)
            {
                what = "going off, blinking red";
                return Mathf.Repeat((now - redSince) * BlinkRate, 1f) < 0.5f ? Red : Color.black;
            }
            bool someOut;
            try { someOut = !Query(controller).IsEmptyIgnoreFilter; }
            catch (System.Exception)
            {
                // The query belongs to a world that is gone (a new shift): find the controller again.
                controller = null;
                what = "no charges' query, green";
                return Green;
            }
            what = someOut ? "charges out, yellow" : "all charges in, green";
            return someOut ? Yellow : Green;
        }

        private static void Paint(Lit lit, Transform root, Color color)
        {
            if (root != lit.Root)
            {
                lit.Root = root;
                lit.Glowing.Clear();
                if (root != null)
                    foreach (var renderer in root.GetComponentsInChildren<Renderer>(true))
                        foreach (var material in renderer.sharedMaterials)
                            if (material != null && material.name.StartsWith(MaterialName, System.StringComparison.Ordinal))
                            {
                                lit.Glowing.Add(renderer);
                                break;
                            }
            }
            foreach (var renderer in lit.Glowing)
            {
                if (renderer == null) continue;
                renderer.GetPropertyBlock(Block);
                foreach (var id in GlowColors) Block.SetColor(id, color);
                renderer.SetPropertyBlock(Block);
            }
        }

        // The game's own colors back: the mod off (F3) or unloaded. The block is emptied; what the game keeps in it, it
        // sets again itself.
        private static void Release()
        {
            Clear(InHand);
            Clear(AtHip);
            Clear(Afloat);
            Clear(Beside);
            controller = null;
            told = null;
            redUntil = -10f;
        }

        private static void Clear(Lit lit)
        {
            foreach (var renderer in lit.Glowing)
                if (renderer != null) renderer.SetPropertyBlock(null);
            lit.Glowing.Clear();
            lit.Root = null;
        }
    }
}
