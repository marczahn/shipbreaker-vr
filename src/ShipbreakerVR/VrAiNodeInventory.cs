using System.Collections.Generic;
using BBI.Unity.Game;
using HarmonyLib;
using UnityEngine;

namespace ShipbreakerVR
{
    // Collection (Raik 2026-11-15, in 0.32.0): the inventory list (InventoryScreenController, the game's
    // currencies and truck parts) shows a row "AI Node" with how many the loaded save has brought home (VrCollection,
    // KiKnotenAnzahl; one put in the backpack counts once its shift is over). The row is only drawn, one of the list's own rows with an id of its own: no
    // currency, nothing in the save. The game has no icon for an AI node: the mod draws its own (Icon), a dark dome seen
    // from above with its red eye, as the node looks lying on the workbench.
    internal static class VrAiNodeInventory
    {
        public const string ItemName = "SBVR_AINODE_ITEM";

        private static readonly AccessTools.FieldRef<InventoryScreenController, CurrencyItemController> RowPrefab =
            AccessTools.FieldRefAccess<InventoryScreenController, CurrencyItemController>("m_InventoryListItemPrefab");
        private static readonly AccessTools.FieldRef<InventoryScreenController, Transform> List =
            AccessTools.FieldRefAccess<InventoryScreenController, Transform>("m_InventoryListContainer");

        private static readonly Dictionary<InventoryScreenController, CurrencyItemController> Rows =
            new Dictionary<InventoryScreenController, CurrencyItemController>();
        private static readonly List<InventoryScreenController> Gone = new List<InventoryScreenController>();
        private static int logs;
        private static Sprite icon;

        private static Sprite Icon
        {
            get
            {
                if (icon != null) return icon;
                const int size = 128;
                var texture = new Texture2D(size, size, TextureFormat.RGBA32, false) { name = "ShipbreakerVR AI node icon" };
                var pixels = new Color32[size * size];
                var c = (size - 1) / 2f;
                for (var y = 0; y < size; y++)
                for (var x = 0; x < size; x++)
                {
                    var dx = (x - c) / c;
                    var dy = (y - c) / c;
                    var r = Mathf.Sqrt(dx * dx + dy * dy);
                    var color = new Color(0f, 0f, 0f, 0f);
                    if (r <= 0.96f)
                    {
                        // The dome: lit from the upper left, a ring of ribs near its rim.
                        var light = Mathf.Clamp01(0.55f - 0.35f * (dx - dy) * 0.5f - 0.25f * r);
                        var shade = 0.12f + 0.16f * light;
                        if (r > 0.78f && r < 0.86f) shade *= 0.6f;
                        if (r > 0.86f && Mathf.Repeat(Mathf.Atan2(dy, dx) * 24f / Mathf.PI, 2f) < 1f) shade *= 0.75f;
                        color = new Color(shade, shade, shade * 1.05f, Mathf.Clamp01((0.96f - r) * 40f));
                        // The eye's dark panel, a rounded square around the middle.
                        if (Mathf.Max(Mathf.Abs(dx), Mathf.Abs(dy + 0.05f)) < 0.3f) color = new Color(0.06f, 0.06f, 0.07f, 1f);
                        // The eye: a red ring and a bright core, with a glow around.
                        var e = Mathf.Sqrt(dx * dx + (dy + 0.05f) * (dy + 0.05f));
                        var glow = Mathf.Clamp01(1f - e / 0.32f);
                        color = Color.Lerp(color, new Color(0.9f, 0.08f, 0.05f, 1f), glow * glow * 0.8f);
                        if (e > 0.12f && e < 0.17f) color = new Color(1f, 0.15f, 0.1f, 1f);
                        if (e < 0.05f) color = new Color(1f, 0.55f, 0.45f, 1f);
                    }
                    pixels[y * size + x] = color;
                }
                texture.SetPixels32(pixels);
                texture.Apply(false, true);
                icon = Sprite.Create(texture, new Rect(0, 0, size, size), new Vector2(0.5f, 0.5f));
                icon.name = texture.name;
                return icon;
            }
        }

        private static int Collected
        {
            get
            {
                var save = VrCollection.Current;
                return save != null ? save.NodeCount.Value : 0;
            }
        }

        // From InventoryScreenController.SetupInventoryDisplay: the screen's list is built.
        public static void Built(InventoryScreenController screen)
        {
            if (!Rows.ContainsKey(screen)) Rows[screen] = null;
            Show(screen);
        }

        // One more went into the backpack: every list there is shows it now.
        public static void Changed()
        {
            Gone.Clear();
            foreach (var screen in Rows.Keys) if (screen == null) Gone.Add(screen);
            foreach (var screen in Gone) Rows.Remove(screen);
            foreach (var screen in new List<InventoryScreenController>(Rows.Keys)) Show(screen);
        }

        private static void Show(InventoryScreenController screen)
        {
            var count = Collected;
            Rows.TryGetValue(screen, out var row);
            if (count <= 0)
            {
                if (row != null) row.gameObject.SetActive(false);
                return;
            }
            if (row == null)
            {
                var prefab = RowPrefab(screen);
                var list = List(screen);
                if (prefab == null || list == null) return;
                row = Object.Instantiate(prefab, list);
                row.name = "ShipbreakerVR AI Node row";
                row.InitializeController(AssetTypeID<CurrencyAsset>.CreateDebugAssetTypeID(7734), ItemName, Icon);
                Rows[screen] = row;
                if (logs++ < 10) Plugin.Log.LogInfo($"AI node: row in the inventory list '{VrUtil.Path(list)}'");
            }
            row.gameObject.SetActive(true);
            row.SetAmount(count);
        }
    }

    [HarmonyPatch(typeof(InventoryScreenController), "SetupInventoryDisplay")]
    internal static class AiNodeInventoryPatch
    {
        private static void Postfix(InventoryScreenController __instance) => VrAiNodeInventory.Built(__instance);
    }
}
