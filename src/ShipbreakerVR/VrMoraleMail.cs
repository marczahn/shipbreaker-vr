using BBI.Unity.Game;

namespace ShipbreakerVR
{
    // Collection (Raik 2026-11-15, in 0.32.0): after the first shift of the career, the tutorial with Weaver
    // (PlayerProfile.TutorialCompleted), LYNX's department for employee satisfaction writes to the Hab's inbox (VrModMail):
    // keeping up morale matters, so everyone may now hang their own posters in their quarters. Only from then on the
    // mod's own pictures (VrHabCustomPosters, BepInEx\ShipbreakerVR-Poster) are among the posters' pictures; before,
    // the posters have the game's own only. Kept in the mod's config for each save (VrCollection, MoralMitteilung), not
    // in the save. Only a career has a Hab: free play and the weekly ship start in the bay and end at the title (the
    // game's SceneLoader loads the Hab for a career only), so there is nothing to send or to hang there.
    internal static class VrMoraleMail
    {
        public static readonly VrModMail Mail = new VrModMail("SBVR_MORALE_MAIL_", "8c3d61f0a2b94e7d95c4e0b7a1f2d3c8", "morale notice",
            () => VrCollection.Current?.Morale,
            () => VrCollection.Current?.MoraleRead,
            "Moral-Mitteilung");

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
