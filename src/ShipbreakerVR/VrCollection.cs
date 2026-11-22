using System.Collections.Generic;
using BBI.Unity.Game;
using BepInEx.Configuration;
using HarmonyLib;

namespace ShipbreakerVR
{
    // What a save has collected (0.32.1, Marc's decisions of 2026-11-22): the AI nodes, the radio and where LYNX's
    // messages stand are kept for each of the game's profiles on its own, still in the mod's config and never in the
    // save. A profile is known by its name (PlayerProfile.ProfileName): the game keeps the names unique
    // (PlayerProfileSaveLoadManager.IsProfileNameUnique) and names the save's file after it, so it stays the same for
    // as long as the save lives. Each profile has a section of its own, 'Sammlung <name>'. A profile deleted and made
    // again under the same name finds the old one's collection.
    // What 0.32.0 kept for all saves together under [Experimente] is read no more: 0.32.0 never went to players, and
    // every save starts with nothing collected.
    //
    // Collected counts once the shift is over: a node or the radio put in the backpack is only noted (Note...), and
    // the notes are booked when the shift ends as the game ends it, on the way from the shift's summary to the Hab
    // (PostMissionScreen.EndShiftAndLoadHabAsync; right after, SceneLoader.PreTearDown saves the profile). Any other
    // way out of the shift (to the title, another save, the game closed) drops them: the game then has the ship as it
    // was saved before the shift, the node on it again, and the radio comes back in it (VrRadio).
    internal static class VrCollection
    {
        internal sealed class Save
        {
            // The kinds of AI node collected, in order, comma separated, and how many nodes in all.
            public ConfigEntry<string> Nodes;
            public ConfigEntry<int> NodeCount;
            // LYNX's warning for the first node and its morale notice: 0 not sent, 1 waiting unread, 2 read; and when read.
            public ConfigEntry<int> Warning;
            public ConfigEntry<long> WarningRead;
            public ConfigEntry<int> Morale;
            public ConfigEntry<long> MoraleRead;
            public ConfigEntry<bool> Radio;
            // The last ship asked whether it has the radio, and the answer.
            public ConfigEntry<string> RadioRoll;
        }

        private const string SectionStart = "Sammlung ";
        private static readonly Dictionary<string, Save> Saves = new Dictionary<string, Save>();
        private static string lastName;
        private static Save last;

        // The loaded profile's collection; null in the menus before a profile is loaded.
        public static Save Current
        {
            get
            {
                var service = PlayerProfileService.Instance;
                var profile = service != null ? service.Profile : null;
                var name = profile != null ? profile.ProfileName : null;
                if (string.IsNullOrEmpty(name) || VrSettings.File == null) return null;
                if (name == lastName) return last;
                var section = Section(name);
                if (!Saves.TryGetValue(section, out var save))
                {
                    save = Bind(section);
                    Saves[section] = save;
                    Plugin.Log.LogInfo($"Collection: profile '{name}' keeps its own under [{section}]: {Describe(save)}");
                }
                lastName = name;
                last = save;
                return save;
            }
        }

        private static Save Bind(string section) => new Save
        {
            Nodes = VrSettings.Bind(section, "KiKnoten", "",
                "The kinds of AI node this save brought home, in the order collected, comma separated. Empty clears their places in the Hab."),
            NodeCount = VrSettings.Bind(section, "KiKnotenAnzahl", 0,
                "How many AI nodes this save put in the backpack, shown in the inventory list."),
            Warning = VrSettings.Bind(section, "KiKnotenAbmahnung", 0,
                "LYNX's warning for taking an AI node home. 0 not sent, 1 waiting unread in the Hab's inbox, 2 read."),
            WarningRead = VrSettings.Bind(section, "KiKnotenAbmahnungGelesen", 0L,
                "When LYNX's warning was read (Unix seconds), its place among LYNX's read messages."),
            Morale = VrSettings.Bind(section, "MoralMitteilung", 0,
                "LYNX's notice after the tutorial that own posters may hang in the Hab. 0 not sent, 1 waiting unread in the Hab's inbox, 2 read. Own posters only from 1 on."),
            MoraleRead = VrSettings.Bind(section, "MoralMitteilungGelesen", 0L,
                "When LYNX's morale notice was read (Unix seconds), its place among LYNX's read messages."),
            Radio = VrSettings.Bind(section, "Radio", false,
                "This save brought the radio home from a ship: it stands in the Hab."),
            RadioRoll = VrSettings.Bind(section, "RadioWurf", "",
                "The last ship asked whether it has the radio and the answer, so loading it again does not ask anew."),
        };

        private static string Describe(Save save) =>
            $"{save.NodeCount.Value} AI node(s) of {Kinds(save.Nodes.Value).Count} kind(s), radio {(save.Radio.Value ? "collected" : "not collected")}";

        // The config section of a profile. BepInEx takes no section with one of =, [, ], ", ', a backslash, a tab or
        // a line break in it, nor one starting or ending in a blank: those become '_'.
        internal static string Section(string profileName)
        {
            var name = (profileName ?? "").Trim().ToCharArray();
            for (var i = 0; i < name.Length; i++)
                if ("=[]\"'\\\t\r\n".IndexOf(name[i]) >= 0 || char.IsControl(name[i])) name[i] = '_';
            return SectionStart + new string(name);
        }

        // The kinds in a list as it is kept: trimmed, each once, in order.
        internal static List<string> Kinds(string kept)
        {
            var kinds = new List<string>();
            foreach (var kind in (kept ?? "").Split(','))
            {
                var k = kind.Trim();
                if (k.Length > 0 && !kinds.Contains(k)) kinds.Add(k);
            }
            return kinds;
        }

        // A list with more kinds booked into it: those not in it yet, at its end.
        internal static string WithKinds(string kept, IEnumerable<string> more)
        {
            var kinds = Kinds(kept);
            foreach (var kind in more)
                if (!string.IsNullOrWhiteSpace(kind) && !kinds.Contains(kind.Trim())) kinds.Add(kind.Trim());
            return string.Join(",", kinds);
        }

        // ---- Noted during the shift, booked at its end ----

        private static readonly List<string> NotedKinds = new List<string>();
        private static bool notedRadio;

        public static int NotedNodes => NotedKinds.Count;
        public static bool RadioNoted => notedRadio;

        public static void NoteNode(string kind) => NotedKinds.Add(kind);

        public static void NoteRadio() => notedRadio = true;

        // The shift ended as the game ends it: what was noted is the save's now. One write of the config for all of it.
        public static void Book()
        {
            if (NotedKinds.Count == 0 && !notedRadio) return;
            var save = Current;
            if (save == null)
            {
                Drop("no profile is loaded");
                return;
            }
            var file = VrSettings.File;
            var saveOnSet = file.SaveOnConfigSet;
            file.SaveOnConfigSet = false;
            try
            {
                if (NotedKinds.Count > 0)
                {
                    save.NodeCount.Value += NotedKinds.Count;
                    save.Nodes.Value = WithKinds(save.Nodes.Value, NotedKinds);
                    // Only the very first node brings LYNX's warning (Raik: three at once would be too much).
                    if (save.Warning.Value == 0) save.Warning.Value = 1;
                }
                if (notedRadio) save.Radio.Value = true;
            }
            finally
            {
                file.SaveOnConfigSet = saveOnSet;
            }
            file.Save();
            Plugin.Log.LogInfo($"Collection: shift over, booked {NotedKinds.Count} AI node(s){(notedRadio ? " and the radio" : "")}; now {Describe(save)}");
            NotedKinds.Clear();
            notedRadio = false;
            VrAiNodeInventory.Changed();
        }

        // The shift left another way: nothing of it counts.
        public static void Drop(string why)
        {
            if (NotedKinds.Count == 0 && !notedRadio) return;
            Plugin.Log.LogInfo($"Collection: {NotedKinds.Count} AI node(s){(notedRadio ? " and the radio" : "")} noted in this shift are not booked ({why})");
            NotedKinds.Clear();
            notedRadio = false;
        }
    }

    // The shift's summary closed: the game goes on to the Hab (a career) and saves the profile on the way.
    [HarmonyPatch(typeof(PostMissionScreen), "EndShiftAndLoadHabAsync")]
    internal static class CollectionBookPatch
    {
        private static void Prefix()
        {
            try { VrCollection.Book(); }
            catch (System.Exception e) { Plugin.Log.LogWarning($"Collection: booking failed ({e})"); }
        }
    }

    // Every change of scene the game makes (to the Hab, a level, the title). After a shift's regular end the notes
    // are booked by now; anything still noted belongs to a shift that was left.
    [HarmonyPatch(typeof(SceneLoader), "StartProcess")]
    internal static class CollectionDropPatch
    {
        private static void Prefix(SceneLoader.SceneType destinationType) => VrCollection.Drop($"the shift was left for {destinationType}");
    }
}
