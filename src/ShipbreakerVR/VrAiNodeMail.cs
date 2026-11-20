using BBI.Unity.Game;

namespace ShipbreakerVR
{
    // Collection (Raik 2026-11-15, in 0.32.0): after the first AI node went into the backpack (VrAiNode),
    // LYNX sends a warning to the Hab's inbox, one of the mod's own messages (VrModMail). Read or not is kept in
    // Experimente/KiKnotenAbmahnung. Its text is in Mails/KiKnoten-Abmahnung-Deutsch.txt and -English.txt.
    internal static class VrAiNodeMail
    {
        public static readonly VrModMail Mail = new VrModMail("SBVR_AINODE_MAIL_", "5b7e2a91c4d34f0a8e6b1d2c3f4a5b6c", "AI node warning",
            () => { VrAiNode.Bind(); return VrAiNode.Warning; },
            () => { VrAiNode.Bind(); return VrAiNode.WarningRead; },
            "KiKnoten-Abmahnung");

        static VrAiNodeMail() => Mail.AddText(VrAiNodeInventory.ItemName, ("KI-Knoten", "AI Node"));
    }
}
