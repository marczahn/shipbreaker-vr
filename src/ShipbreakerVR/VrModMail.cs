using System;
using System.Collections.Generic;
using BBI.Unity.Game;
using BepInEx.Configuration;
using HarmonyLib;
using UnityEngine;

namespace ShipbreakerVR
{
    // Collection (Raik 2026-11-15, in 0.32.0): the mod's own messages in the Hab's inbox, as if from LYNX. Each
    // is a NarrativeMessageAsset made at run time, its texts answered by the localization for its own IDs, and it never
    // enters the save: it is in the inbox's runtime list of reconciled messages only while the inbox builds its list
    // (HabNarrativeMessageButtonController.RefreshMessages), and opening it is not handed to the game's inventory
    // (NarrativeItemSystem.ProcessOpenedNarrativeMessage), which would store it as read. Read, it is with LYNX's own read
    // messages ('LYNX (Intern)'), at the time it was read. Whether one waits or was read is kept in the mod's config.
    internal sealed class VrModMail
    {
        private static readonly List<VrModMail> all = new List<VrModMail>();
        private static bool registered;

        // Every mail of the mod, each made by its own class the first time.
        public static List<VrModMail> All
        {
            get
            {
                if (!registered)
                {
                    registered = true;
                    _ = VrAiNodeMail.Mail;
                    _ = VrMoraleMail.Mail;
                }
                return all;
            }
        }

        private readonly string prefix;
        private readonly string guid;
        private readonly string name;
        private readonly Func<ConfigEntry<int>> state;
        private readonly Func<ConfigEntry<long>> readAt;
        private readonly Dictionary<string, (string De, string En)> texts = new Dictionary<string, (string De, string En)>();
        private NarrativeMessageAsset message;

        private string Sender => prefix + "SENDER";
        private string Receiver => prefix + "RECEIVER";
        private string Subject => prefix + "SUBJECT";
        private string Body => prefix + "BODY";

        // prefix: the start of its text IDs ('SBVR_..._'); guid: any fixed id of its own, so the inbox's buttons keep it
        // apart from the game's messages; state: 0 not sent, 1 waiting unread, 2 read; readAt: when, in Unix seconds.
        public VrModMail(string prefix, string guid, string name, Func<ConfigEntry<int>> state, Func<ConfigEntry<long>> readAt,
            (string De, string En) sender, (string De, string En) subject, (string De, string En) body)
        {
            this.prefix = prefix;
            this.guid = guid;
            this.name = name;
            this.state = state;
            this.readAt = readAt;
            texts[Sender] = sender;
            texts[Receiver] = ("Mitarbeiter*in #[NICHT GEFUNDEN]", "Employee #[NOT FOUND]");
            texts[Subject] = subject;
            texts[Body] = body;
            all.Add(this);
        }

        // Its texts from the mod's own text files (Mails\<file>-Deutsch.txt and -English.txt, built into the DLL), so they are
        // written and changed as plain text (Raik 2026-11-15). Each starts with the sender's and the subject's line
        // ('Absender:'/'Sender:', 'Betreff:'/'Subject:'), then a blank line, then the message.
        public VrModMail(string prefix, string guid, string name, Func<ConfigEntry<int>> state, Func<ConfigEntry<long>> readAt, string file)
            : this(prefix, guid, name, state, readAt, default, default, default)
        {
            var de = Load(file + "-Deutsch.txt");
            var en = Load(file + "-English.txt");
            texts[Sender] = (de.Sender, en.Sender);
            texts[Subject] = (de.Subject, en.Subject);
            texts[Body] = (de.Body, en.Body);
        }

        private static (string Sender, string Subject, string Body) Load(string file)
        {
            using (var stream = typeof(VrModMail).Assembly.GetManifestResourceStream("Mails/" + file))
            {
                if (stream == null)
                {
                    Plugin.Log.LogError($"Mod mail: text '{file}' is not in the DLL");
                    return ("LYNX", "?", "?");
                }
                using (var reader = new System.IO.StreamReader(stream, System.Text.Encoding.UTF8))
                    return Parse(reader.ReadToEnd());
            }
        }

        internal static (string Sender, string Subject, string Body) Parse(string text)
        {
            var lines = text.Replace("\r\n", "\n").Split('\n');
            string After(int i) => i < lines.Length && lines[i].Contains(":") ? lines[i].Substring(lines[i].IndexOf(':') + 1).Trim() : "";
            var start = 2;
            while (start < lines.Length && lines[start].Trim().Length == 0) start++;
            var body = start < lines.Length ? string.Join("\n", lines, start, lines.Length - start).TrimEnd() : "";
            return (After(0), After(1), body);
        }

        // A text of its own besides the message's, such as a name in a list.
        public void AddText(string id, (string De, string En) text) => texts[id] = text;

        public NarrativeMessageAsset Message
        {
            get
            {
                if (message != null) return message;
                var text = ScriptableObject.CreateInstance<NarrativeContentTextAsset>();
                text.name = "ShipbreakerVR " + name + " text";
                Traverse.Create(text).Field("m_Content").SetValue(Body);
                Traverse.Create(text).Field("m_ContentDisplayName").SetValue(Subject);
                var mail = ScriptableObject.CreateInstance<NarrativeMessageAsset>();
                mail.name = "ShipbreakerVR " + name;
                var fields = Traverse.Create(mail);
                fields.Field("m_AssetGUID").SetValue(guid);
                fields.Field("m_ReceiverLocIDs").SetValue(new List<string> { Receiver });
                fields.Field("m_SenderLocID").SetValue(Sender);
                fields.Field("m_SubjectLineLocID").SetValue(Subject);
                fields.Field("m_MessageTextAsset").SetValue(text);
                fields.Field("m_Attachments").SetValue(new List<NarrativeContentAsset>());
                fields.Field("m_Rewards").SetValue(new List<NarrativeMessageRewardAsset>());
                message = mail;
                return message;
            }
        }

        private int State => state()?.Value ?? 0;
        public bool Sent => State >= 1;
        public bool Waiting => State == 1;
        public bool WasRead => State == 2;
        public bool Is(NarrativeMessageAsset asset) => asset != null && message != null && asset == message;

        // Into the inbox, unread; once only.
        public void Send()
        {
            var entry = state();
            if (entry == null || entry.Value != 0) return;
            entry.Value = 1;
            VrSettings.File.Save();
            Plugin.Log.LogInfo($"Mod mail: '{name}' sent to the Hab's inbox");
        }

        public void Read()
        {
            var entry = state();
            if (entry == null || entry.Value == 2) return;
            entry.Value = 2;
            readAt().Value = DateTimeOffset.Now.ToUnixTimeSeconds();
            VrSettings.File.Save();
            Plugin.Log.LogInfo($"Mod mail: '{name}' read in the Hab's inbox");
        }

        // Read before its time was kept: from now on.
        public long ReadAt
        {
            get
            {
                var entry = readAt();
                if (entry == null) return 0L;
                if (entry.Value == 0L)
                {
                    entry.Value = DateTimeOffset.Now.ToUnixTimeSeconds();
                    VrSettings.File.Save();
                }
                return entry.Value;
            }
        }

        public static bool TryText(string id, out string text)
        {
            text = null;
            if (id == null || !id.StartsWith("SBVR_")) return false;
            foreach (var mail in All)
            {
                if (!mail.texts.TryGetValue(id, out var both)) continue;
                var german = (LynxGameUserOptions.Language ?? "").StartsWith("de", StringComparison.OrdinalIgnoreCase);
                text = german ? both.De : both.En;
                return true;
            }
            return false;
        }

        public static VrModMail Of(NarrativeMessageAsset asset)
        {
            foreach (var mail in All) if (mail.Is(asset)) return mail;
            return null;
        }

        private static int adoptLogs;

        // Like LYNX's own messages: their category (the read list's 'LYNX internal' group) and their receiver, the player's
        // shipbreaker ID. Taken from the messages in the inbox whose category's name says LYNX; the one with 'intern' in it
        // first. False while there is none.
        public static bool AdoptLynx(NarrativeInventory inventory, out NarrativeCategoryAsset category)
        {
            category = null;
            var localization = Main.Instance != null ? Main.Instance.LocalizationService : null;
            if (localization == null) return false;
            var best = 0;
            foreach (var key in inventory.ReadMessages.Keys)
            {
                if (key == null) continue;
                var name = localization.Localize(key.CategoryName) ?? "";
                var score = name.IndexOf("LYNX", StringComparison.OrdinalIgnoreCase) >= 0
                    ? (name.IndexOf("intern", StringComparison.OrdinalIgnoreCase) >= 0 ? 2 : 1) : 0;
                if (score > best)
                {
                    best = score;
                    category = key;
                }
            }
            if (category == null) return false;
            // The player's own shipbreaker ID: the receiver most messages name, of all the inbox's messages and all their
            // receivers, but not a list to everyone ('Shipbreakers_ALL@LYNX.railmail' was most in LYNX's own group).
            var counts = new Dictionary<string, int>();
            void Count(NarrativeMessageAsset asset)
            {
                if (asset == null || asset.ReceiverLocIDs == null || Of(asset) != null) return;
                foreach (var id in asset.ReceiverLocIDs)
                {
                    if (string.IsNullOrEmpty(id)) continue;
                    counts.TryGetValue(id, out var n);
                    counts[id] = n + 1;
                }
            }
            foreach (var group in inventory.ReadMessages.Values) foreach (var data in group.Values) Count(data.Asset);
            foreach (var data in inventory.UnreadMessages.Values) Count(data.Asset);
            string receiver = null;
            var seen = new List<string>();
            foreach (var pair in counts)
            {
                var name = localization.Localize(pair.Key) ?? pair.Key;
                seen.Add($"'{name}' {pair.Value}x");
                if (name.IndexOf("ALL", StringComparison.Ordinal) >= 0) continue;
                if (receiver == null || pair.Value > counts[receiver]) receiver = pair.Key;
            }
            if (adoptLogs == 0) Plugin.Log.LogInfo($"Mod mail: receivers in the inbox: {string.Join(", ", seen)}");
            foreach (var mail in All)
            {
                var fields = Traverse.Create(mail.Message);
                fields.Field("m_Category").SetValue(category);
                if (receiver != null) fields.Field("m_ReceiverLocIDs").SetValue(new List<string> { receiver });
            }
            if (adoptLogs++ < 5)
                Plugin.Log.LogInfo($"Mod mail: goes with '{localization.Localize(category.CategoryName)}', to '{(receiver != null ? localization.Localize(receiver) : "-")}' ({receiver ?? "own receiver"})");
            return true;
        }

        // While the inbox builds its list: every mail sent is put in, unread at the top of the reconciled messages, read in
        // LYNX's group of read messages. What was put in is added to 'put' as it goes in, to take out again right after
        // (the inventory is saved with the profile), also when this or the game's list building throws.
        public static void PutIn(NarrativeInventory inventory, List<(VrModMail Mail, NarrativeCategoryAsset Category)> put)
        {
            NarrativeCategoryAsset category = null;
            var adopted = false;
            var lynx = false;
            foreach (var mail in All)
            {
                if (!mail.Sent) continue;
                if (!adopted)
                {
                    adopted = true;
                    lynx = AdoptLynx(inventory, out category);
                }
                var asset = mail.Message;
                if (mail.Waiting)
                {
                    if (inventory.ReconciledMessages.Contains(asset)) continue;
                    inventory.ReconciledMessages.Insert(0, asset);
                    put.Add((mail, null));
                    continue;
                }
                if (!lynx || inventory.ReadMessages[category].ContainsKey(asset.ID)) continue;
                inventory.ReadMessages[category].Add(asset.ID,
                    new NarrativeInventory.CollectedMessageData { Asset = asset, TimeCollected = mail.ReadAt });
                put.Add((mail, category));
            }
        }

        public static void TakeOut(NarrativeInventory inventory, List<(VrModMail Mail, NarrativeCategoryAsset Category)> put)
        {
            foreach (var (mail, category) in put)
            {
                var asset = mail.Message;
                inventory.ReconciledMessages.Remove(asset);
                if (category != null && inventory.ReadMessages.TryGetValue(category, out var read)) read.Remove(asset.ID);
            }
        }
    }

    [HarmonyPatch(typeof(HabNarrativeMessageButtonController), "RefreshMessages")]
    internal static class ModMailListPatch
    {
        private sealed class Put
        {
            public NarrativeInventory Inventory;
            public readonly List<(VrModMail Mail, NarrativeCategoryAsset Category)> Mails = new List<(VrModMail, NarrativeCategoryAsset)>();
        }

        private static void Prefix(out Put __state)
        {
            __state = null;
            var profile = PlayerProfileService.Instance != null ? PlayerProfileService.Instance.Profile : null;
            if (profile == null || profile.NarrativeInventory == null) return;
            __state = new Put { Inventory = profile.NarrativeInventory };
            try
            {
                VrMoraleMail.Check(profile);
                VrModMail.PutIn(__state.Inventory, __state.Mails);
            }
            catch (Exception e)
            {
                Plugin.Log.LogWarning($"Mod mail: putting the mod's messages in the inbox failed ({e})");
            }
        }

        // A finalizer, not a postfix: the mod's messages come out of the game's inventory again also when the game's
        // list building throws. Left in, they would be written to the save with the profile (0.32.0 took them out in
        // a postfix, which an exception skips). The inventory they went into is the one they come out of, whatever
        // profile is loaded by then.
        private static Exception Finalizer(Exception __exception, Put __state)
        {
            if (__state != null && __state.Mails.Count > 0)
            {
                try { VrModMail.TakeOut(__state.Inventory, __state.Mails); }
                catch (Exception e) { Plugin.Log.LogError($"Mod mail: taking the mod's messages out of the inbox failed ({e})"); }
            }
            return __exception;
        }
    }

    [HarmonyPatch(typeof(NarrativeItemSystem), "ProcessOpenedNarrativeMessage")]
    internal static class ModMailOpenedPatch
    {
        private static bool Prefix(NarrativeMessageAsset messageAsset)
        {
            var mail = VrModMail.Of(messageAsset);
            if (mail == null) return true;
            mail.Read();
            return false;
        }
    }

    [HarmonyPatch]
    internal static class ModMailTextPatch
    {
        private static System.Reflection.MethodBase TargetMethod() =>
            AccessTools.Method(AccessTools.TypeByName("Carbon.Localization.Core.LocalizationService"), "TryLocalize",
                new[] { typeof(string), typeof(string).MakeByRefType() });

        private static bool Prefix(string __0, ref string __1, ref bool __result)
        {
            if (!VrModMail.TryText(__0, out var text)) return true;
            __1 = text;
            __result = true;
            return false;
        }
    }

    [HarmonyPatch]
    internal static class ModMailLocalizePatch
    {
        private static System.Reflection.MethodBase TargetMethod() =>
            AccessTools.Method(AccessTools.TypeByName("Carbon.Localization.Core.LocalizationService"), "Localize",
                new[] { typeof(string), typeof(string) });

        private static bool Prefix(string __0, ref string __result)
        {
            if (!VrModMail.TryText(__0, out var text)) return true;
            __result = text;
            return false;
        }
    }
}
