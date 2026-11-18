using System.Collections.Generic;
using BBI.Unity.Game;
using HarmonyLib;
using TMPro;
using UnityEngine;

namespace ShipbreakerVR
{
    // How many demo charges are left, shown while a hand holds one as a thing of its own (Sprengsatzplatzierung =
    // Manuell; Raik, 2026-11-18): the game's own line for it on the HUD (DemoChargeCapacityUIController, "DEMO CHARGES
    // [n]"), which the game shows only while the charges are its tool in the hand. Held as things of their own they are
    // no tool to the game, and the HUD kept the last tool's lines (the grapple's tethers, say).
    // - The number counts the charges in the hands too, as Raik asked: the stock and one for each hand that holds one.
    //   Also while the game shows the line itself (the detonator in the other hand). In free mode the game's sign for
    //   "without end" stays.
    // - With no tool in a hand the line is switched on. What it would lie over on the HUD (the last tool's line in the
    //   same place) is made unseen for as long, by its transparency, so the game's own switching of those lines is not
    //   touched. With the cutter or the grapple in the other hand the HUD is theirs (heat, tethers), as Raik wants: the
    //   line stays off.
    internal static class VrChargeCount
    {
        static VrChargeCount() => VrSession.OnUnload("charge count", Release);

        private static readonly AccessTools.FieldRef<DemoChargeController, int> Available =
            AccessTools.FieldRefAccess<DemoChargeController, int>("mAvailableDemoCharges");
        private static readonly AccessTools.FieldRef<DemoChargeCapacityUIController, TextMeshProUGUI> Label =
            AccessTools.FieldRefAccess<DemoChargeCapacityUIController, TextMeshProUGUI>("m_DemoChargeCount");

        // A neighbor on the HUD is in the line's way if this share of the smaller of the two is covered, and is a line
        // like it (not a frame around everything): at most this many times its area.
        private const float Covered = 0.3f;
        private const float MostArea = 4f;

        private static DemoChargeCapacityUIController panel;
        private static DemoChargeController charges;
        private static float nextFind;
        private static float nextLook;
        private static bool shown;
        private static bool counted;
        private static readonly List<CanvasGroup> hidden = new List<CanvasGroup>();
        private static readonly List<float> hiddenAlpha = new List<float>();
        private static readonly Vector3[] Corners = new Vector3[4];
        private static int logs;

        private static bool FreeMode =>
            SceneLoader.Instance != null && SceneLoader.Instance.CurrentLevelData.SessionType == GameSession.SessionType.FreeMode;

        // From VrFrame, once a frame after the hands held on or let go (VrChargeByHand.Track).
        public static void Update()
        {
            var held = 0;
            if (VrXr.IsRunning)
            {
                if (VrChargeByHand.Holds(VrInput.Left)) held++;
                if (VrChargeByHand.Holds(VrInput.Right)) held++;
            }
            if (held == 0 || !Find())
            {
                Restore();
                return;
            }
            if (!FreeMode)
            {
                var label = Label(panel);
                var text = (Available(charges) + held).ToString();
                if (label != null && label.text != text) label.text = text;
                counted = true;
            }
            var equipment = GameRefs.Equipment;
            if (equipment == null || !equipment.IsNothingEquipped)
            {
                // A tool in a hand: its lines are the game's to show (the charges' own line too, with the detonator).
                Unshow(equipment);
                return;
            }
            if (!panel.gameObject.activeSelf)
            {
                panel.gameObject.SetActive(true);
                shown = true;
                nextLook = 0f;
                if (logs++ < 10)
                    Plugin.Log.LogInfo($"Charge count: the HUD's line '{VrUtil.Chain(panel.transform, 6)}' switched on for the charge in the hand"
                        + $"{(panel.gameObject.activeInHierarchy ? "" : " (something above it is off: it does not show)")}");
            }
            if (shown && Time.unscaledTime >= nextLook)
            {
                nextLook = Time.unscaledTime + 1f;
                HideCovered();
            }
        }

        private static bool Find()
        {
            if (panel != null && charges != null) return true;
            if (Time.unscaledTime < nextFind) return false;
            nextFind = Time.unscaledTime + 2f;
            if (charges == null) charges = Object.FindObjectOfType<DemoChargeController>();
            if (panel == null)
                foreach (var each in Resources.FindObjectsOfTypeAll<DemoChargeCapacityUIController>())
                    if (each != null && each.gameObject.scene.IsValid()) panel = each;
            return panel != null && charges != null;
        }

        // Its neighbors on the HUD that lie where the line shows, made unseen until it is switched off again.
        private static void HideCovered()
        {
            var own = panel.transform as RectTransform;
            var parent = own != null ? own.parent : null;
            if (parent == null || !Area(own, parent, out var mine)) return;
            foreach (Transform child in parent)
            {
                var other = child as RectTransform;
                if (other == null || other == own || !other.gameObject.activeInHierarchy || !Area(other, parent, out var theirs)) continue;
                var width = Mathf.Min(mine.xMax, theirs.xMax) - Mathf.Max(mine.xMin, theirs.xMin);
                var height = Mathf.Min(mine.yMax, theirs.yMax) - Mathf.Max(mine.yMin, theirs.yMin);
                var smaller = Mathf.Min(mine.width * mine.height, theirs.width * theirs.height);
                var covered = width > 0f && height > 0f && smaller > 0f && width * height >= Covered * smaller
                    && theirs.width * theirs.height <= MostArea * mine.width * mine.height;
                var group = other.GetComponent<CanvasGroup>();
                var known = group != null && hidden.Contains(group);
                if (!covered || known) continue;
                if (group == null) group = other.gameObject.AddComponent<CanvasGroup>();
                hidden.Add(group);
                hiddenAlpha.Add(group.alpha);
                group.alpha = 0f;
                if (logs++ < 20) Plugin.Log.LogInfo($"Charge count: '{other.name}' on the HUD lies where the charges' line shows, unseen for as long");
            }
        }

        // The rectangle of a HUD element in its parent's own plane.
        private static bool Area(RectTransform element, Transform parent, out Rect area)
        {
            element.GetWorldCorners(Corners);
            var least = new Vector2(float.MaxValue, float.MaxValue);
            var most = new Vector2(float.MinValue, float.MinValue);
            foreach (var corner in Corners)
            {
                var local = parent.InverseTransformPoint(corner);
                least = Vector2.Min(least, local);
                most = Vector2.Max(most, local);
            }
            area = Rect.MinMaxRect(least.x, least.y, most.x, most.y);
            return area.width > 0f && area.height > 0f;
        }

        // The line off again, unless the game shows it itself now (the charges are its tool in a hand).
        private static void Unshow(EquipmentController equipment)
        {
            foreach (var group in hidden)
                if (group != null) group.alpha = hiddenAlpha[hidden.IndexOf(group)];
            hidden.Clear();
            hiddenAlpha.Clear();
            if (!shown) return;
            shown = false;
            var theirs = equipment != null && !equipment.IsNothingEquipped && equipment.CurrentEquipment == EquipmentController.Equipment.DemoCharge;
            if (panel != null && !theirs && panel.gameObject.activeSelf) panel.gameObject.SetActive(false);
        }

        private static void Restore()
        {
            if (counted)
            {
                counted = false;
                var label = panel != null ? Label(panel) : null;
                if (label != null && charges != null && !FreeMode) label.text = Available(charges).ToString();
            }
            if (shown || hidden.Count > 0) Unshow(GameRefs.Equipment);
        }

        // On unload (hot reload).
        public static void Release()
        {
            Restore();
            panel = null;
            charges = null;
        }
    }
}
