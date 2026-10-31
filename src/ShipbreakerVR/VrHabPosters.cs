using System.Collections.Generic;
using BBI.Unity.Game;
using BBI.Unity.Game.UI.Hab;
using HarmonyLib;
using UnityEngine;

namespace ShipbreakerVR
{
    // Walking through the Hab: the poster one looks at from close by gets the game's selection border, and the triggers
    // change its picture as the tabs do at the game's poster spots. Those spots are not used: the game stays where it is.
    internal static class VrHabPosters
    {
        private const float Range = 3f;
        private const float Cone = 12f;

        private static readonly AccessTools.FieldRef<Hab3DPoster, GameObject> Border = AccessTools.FieldRefAccess<Hab3DPoster, GameObject>("m_SelectedBorder");
        private static readonly AccessTools.FieldRef<Hab3DPosterLocation, Hab3DPoster> Selected = AccessTools.FieldRefAccess<Hab3DPosterLocation, Hab3DPoster>("mCurrentlySelectedPoster");
        private static readonly AccessTools.FieldRef<Hab3DPosterLocation, List<NarrativeContentImageAsset>> Images = AccessTools.FieldRefAccess<Hab3DPosterLocation, List<NarrativeContentImageAsset>>("mCollectedPosterImages");
        private static readonly List<Hab3DPoster> posters = new List<Hab3DPoster>();
        private static Hab3DPoster looked;

        public static void Update(Transform head, float scale)
        {
            if (posters.Count == 0 || posters.Exists(p => p == null)) Find();
            var next = Nearest(head, Range * scale);
            if (next != looked)
            {
                Show(looked, false);
                looked = next;
                Show(looked, true);
            }
            if (looked == null) return;
            if (VrInput.Right.Trigger.Pressed) Change(1);
            else if (VrInput.Left.Trigger.Pressed) Change(-1);
        }

        public static void Stop()
        {
            Show(looked, false);
            looked = null;
        }

        private static void Find()
        {
            posters.Clear();
            foreach (var poster in Resources.FindObjectsOfTypeAll<Hab3DPoster>())
                if (poster.gameObject.scene.IsValid()) posters.Add(poster);
            Plugin.Log.LogInfo($"Hab: {posters.Count} poster(s)");
        }

        private static Hab3DPoster Nearest(Transform head, float range)
        {
            Hab3DPoster best = null;
            var bestAngle = Cone;
            foreach (var poster in posters)
            {
                if (poster == null || !poster.gameObject.activeInHierarchy) continue;
                var way = poster.RectTransform.position - head.position;
                if (way.magnitude > range) continue;
                var angle = Vector3.Angle(head.forward, way);
                if (angle >= bestAngle) continue;
                bestAngle = angle;
                best = poster;
            }
            return best;
        }

        private static void Show(Hab3DPoster poster, bool show)
        {
            if (poster != null && Border(poster) != null) Border(poster).SetActive(show);
        }

        // Through a poster location, even a switched off one: it picks, saves and sounds as at the game's poster spots.
        private static void Change(int by)
        {
            var location = FirstLocation();
            var saved = PlayerProfileService.Instance.Profile.HabSavedPosters;
            if (location == null || saved.Count <= looked.PosterIndex) return;
            Images(location) = Hab3DPosterLocation.GeneratePosterImageCollection();
            Selected(location) = looked;
            looked.CurrentImageIndex = saved[looked.PosterIndex];
            if (by > 0) location.TabToNextImage();
            else location.TabToPrevImage();
        }

        private static Hab3DPosterLocation FirstLocation()
        {
            foreach (var location in Resources.FindObjectsOfTypeAll<Hab3DPosterLocation>())
                if (location.gameObject.scene.IsValid()) return location;
            return null;
        }
    }
}
