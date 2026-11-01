using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using BBI.Unity.Game;
using BBI.Unity.Game.UI.Hab;
using BepInEx;
using HarmonyLib;
using UnityEngine;
using UnityEngine.UI;

namespace ShipbreakerVR
{
    // Own pictures for the Hab's posters: image files in BepInEx\ShipbreakerVR-Poster join the game's list of pictures.
    // The game saves a poster's choice as a place in that list, and the list grows with every message read, so the
    // choice of an own picture lives in a file of its own, by name; the save game gets the empty picture, and stays right
    // without the mod.
    internal static class VrHabCustomPosters
    {
        private const int LargestSide = 2048;
        private const string ChoicesFile = "auswahl.txt";

        private static readonly string[] Types = { ".png", ".jpg", ".jpeg" };
        private static readonly Dictionary<string, NarrativeContentImageAsset> pictures = new Dictionary<string, NarrativeContentImageAsset>(StringComparer.OrdinalIgnoreCase);
        private static readonly HashSet<Sprite> sprites = new HashSet<Sprite>();
        private static Dictionary<int, string> choices;
        private static bool loaded;

        private static string Folder => System.IO.Path.Combine(Paths.BepInExRootPath, "ShipbreakerVR-Poster");

        public static bool IsOwn(Sprite sprite) => sprite != null && sprites.Contains(sprite);

        public static void AddTo(List<NarrativeContentImageAsset> list)
        {
            Load();
            list.AddRange(pictures.Values);
        }

        // The place of a poster's current picture in the game's list.
        public static int Place(Hab3DPoster poster, int saved) =>
            Choices.ContainsKey(poster.PosterIndex) ? poster.CurrentImageIndex : saved;

        // From the game's own switch of a poster's picture.
        public static void Chosen(Hab3DPoster poster, NarrativeContentImageAsset picture)
        {
            var name = pictures.FirstOrDefault(p => ReferenceEquals(p.Value, picture)).Key;
            if (name != null)
            {
                Choices[poster.PosterIndex] = name;
                var saved = PlayerProfileService.Instance.Profile.HabSavedPosters;
                if (saved.Count > poster.PosterIndex) saved[poster.PosterIndex] = 0;
            }
            else Choices.Remove(poster.PosterIndex);
            Save();
        }

        // A poster coming up in the Hab.
        public static void Restore(Hab3DPoster poster)
        {
            Load();
            if (!Choices.TryGetValue(poster.PosterIndex, out var name)) return;
            if (!pictures.TryGetValue(name, out var picture))
            {
                Plugin.Log.LogWarning($"Poster {poster.PosterIndex}: '{name}' is gone");
                return;
            }
            poster.SetSprite(picture.Content);
            poster.CurrentImageIndex = Hab3DPosterLocation.GeneratePosterImageCollection().IndexOf(picture);
        }

        private static Dictionary<int, string> Choices
        {
            get
            {
                if (choices != null) return choices;
                var file = System.IO.Path.Combine(Folder, ChoicesFile);
                choices = PosterChoices.Parse(File.Exists(file) ? File.ReadAllLines(file) : new string[0]);
                return choices;
            }
        }

        private static void Save()
        {
            Directory.CreateDirectory(Folder);
            File.WriteAllLines(System.IO.Path.Combine(Folder, ChoicesFile), PosterChoices.Format(Choices));
        }

        private static void Load()
        {
            if (loaded) return;
            loaded = true;
            try
            {
                Directory.CreateDirectory(Folder);
                foreach (var file in Directory.GetFiles(Folder).OrderBy(f => f, StringComparer.OrdinalIgnoreCase))
                {
                    if (Array.IndexOf(Types, System.IO.Path.GetExtension(file).ToLowerInvariant()) < 0) continue;
                    var picture = Picture(file);
                    if (!ReferenceEquals(picture, null)) pictures[System.IO.Path.GetFileName(file)] = picture;
                }
                Plugin.Log.LogInfo($"Own posters: {pictures.Count} picture(s) in {Folder}");
            }
            catch (Exception e)
            {
                Plugin.Log.LogWarning($"Own posters: {e.Message}");
            }
        }

        private static NarrativeContentImageAsset Picture(string file)
        {
            var texture = new Texture2D(2, 2, TextureFormat.RGBA32, false);
            if (!texture.LoadImage(File.ReadAllBytes(file)))
            {
                Plugin.Log.LogWarning($"Own posters: cannot read {file}");
                UnityEngine.Object.Destroy(texture);
                return null;
            }
            texture = Limited(texture);
            texture.wrapMode = TextureWrapMode.Clamp;
            var sprite = Sprite.Create(texture, new Rect(0f, 0f, texture.width, texture.height), new Vector2(0.5f, 0.5f));
            sprites.Add(sprite);
            var picture = ScriptableObject.CreateInstance<NarrativeContentImageAsset>();
            Traverse.Create(picture).Field("m_Content").SetValue(sprite);
            return picture;
        }

        private static Texture2D Limited(Texture2D texture)
        {
            var side = Mathf.Max(texture.width, texture.height);
            if (side <= LargestSide) return texture;
            var width = texture.width * LargestSide / side;
            var height = texture.height * LargestSide / side;
            var target = RenderTexture.GetTemporary(width, height, 0, RenderTextureFormat.ARGB32);
            Graphics.Blit(texture, target);
            var before = RenderTexture.active;
            RenderTexture.active = target;
            var small = new Texture2D(width, height, TextureFormat.RGBA32, false);
            small.ReadPixels(new Rect(0f, 0f, width, height), 0, 0);
            small.Apply();
            RenderTexture.active = before;
            RenderTexture.ReleaseTemporary(target);
            UnityEngine.Object.Destroy(texture);
            return small;
        }
    }

    // One line per poster: its number, a tab, the picture's file name.
    internal static class PosterChoices
    {
        public static Dictionary<int, string> Parse(IEnumerable<string> lines)
        {
            var choices = new Dictionary<int, string>();
            foreach (var line in lines)
            {
                var parts = line.Split('\t');
                if (parts.Length == 2 && int.TryParse(parts[0], out var poster) && parts[1].Length > 0) choices[poster] = parts[1];
            }
            return choices;
        }

        public static IEnumerable<string> Format(Dictionary<int, string> choices) =>
            choices.OrderBy(c => c.Key).Select(c => $"{c.Key}\t{c.Value}");
    }

    [HarmonyPatch(typeof(Hab3DPosterLocation), nameof(Hab3DPosterLocation.GeneratePosterImageCollection))]
    internal static class PosterListPatch
    {
        private static void Postfix(List<NarrativeContentImageAsset> __result) => VrHabCustomPosters.AddTo(__result);
    }

    [HarmonyPatch(typeof(Hab3DPosterLocation), "TabToImage")]
    internal static class PosterChoicePatch
    {
        private static void Postfix(int indexToShow, Hab3DPoster ___mCurrentlySelectedPoster, List<NarrativeContentImageAsset> ___mCollectedPosterImages)
        {
            if (___mCurrentlySelectedPoster == null || indexToShow < 0 || indexToShow >= ___mCollectedPosterImages.Count) return;
            VrHabCustomPosters.Chosen(___mCurrentlySelectedPoster, ___mCollectedPosterImages[indexToShow]);
        }
    }

    [HarmonyPatch(typeof(Hab3DPoster), "LoadSavedPoster")]
    internal static class PosterLoadPatch
    {
        private static void Postfix(Hab3DPoster __instance) => VrHabCustomPosters.Restore(__instance);
    }

    // Own pictures keep their shape on the poster instead of being stretched to it.
    [HarmonyPatch(typeof(Hab3DPoster), nameof(Hab3DPoster.SetSprite))]
    internal static class PosterShapePatch
    {
        private static void Postfix(Sprite sprite, Image ___m_Image) => ___m_Image.preserveAspect = VrHabCustomPosters.IsOwn(sprite);
    }
}
