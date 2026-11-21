using BBI.Unity.Game;
using BepInEx.Configuration;

namespace ShipbreakerVR
{
    // Collection (Raik 2026-11-15, in 0.32.0): after the first shift of the career, the tutorial with Weaver
    // (PlayerProfile.TutorialCompleted), LYNX's department for employee satisfaction writes to the Hab's inbox (VrModMail):
    // keeping up morale matters, so everyone may now hang their own posters in their quarters. Only from then on the
    // mod's own pictures (VrHabCustomPosters, BepInEx\ShipbreakerVR-Poster) are among the posters' pictures; before,
    // the posters have the game's own only. Kept in the mod's config (Experimente/MoralMitteilung), not in the save.
    internal static class VrMoraleMail
    {
        private static ConfigEntry<int> state;
        private static ConfigEntry<long> readAt;

        public static readonly VrModMail Mail = new VrModMail("SBVR_MORALE_MAIL_", "8c3d61f0a2b94e7d95c4e0b7a1f2d3c8", "morale notice",
            () => { Bind(); return state; },
            () => { Bind(); return readAt; },
            "Moral-Mitteilung");

        private static void Bind()
        {
            if (state != null || VrSettings.File == null) return;
            state = VrSettings.Bind("Experimente", "MoralMitteilung", 0,
                "Experiment: LYNX's notice after the tutorial that own posters may hang in the Hab. 0 not sent, 1 waiting unread in the Hab's inbox, 2 read. Own posters only from 1 on.");
            readAt = VrSettings.Bind("Experimente", "MoralMitteilungGelesen", 0L,
                "Experiment: when LYNX's morale notice was read (Unix seconds), its place among LYNX's read messages.");
        }

        // The tutorial done: the notice is sent, once.
        public static void Check(PlayerProfile profile)
        {
            if (profile != null && profile.TutorialCompleted) Mail.Send();
        }

        // Own posters allowed: the notice has come, in a career past its tutorial.
        public static bool PostersAllowed
        {
            get
            {
                var profile = PlayerProfileService.Instance != null ? PlayerProfileService.Instance.Profile : null;
                if (profile == null || !profile.TutorialCompleted) return false;
                Check(profile);
                return Mail.Sent;
            }
        }
    }
}
