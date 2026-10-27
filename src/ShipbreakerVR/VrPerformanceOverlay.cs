using TMPro;
using UnityEngine;
using UnityEngine.UI;
using UnityEngine.XR;

namespace ShipbreakerVR
{
    // The frame rate in the upper left of the visor and the menus ([Anzeige] Leistungsanzeige), for judging a setting in the
    // headset without reading the log. A canvas of our own in Screen Space - Overlay, which VrCanvases attaches to the UI
    // camera like the game's own, so it shows wherever the UI does. Refreshed twice a second from the frames in between:
    // frames per second, the headset's rate and the share of frames that missed its best rate (a runtime that can't keep
    // up halves the rate, so 45 Hz at 0 % missed still means 90 Hz was missed), and the main thread's two parts from
    // VrPerformance: game logic (which also holds the wait for the headset) and preparing the render.
    internal static class VrPerformanceOverlay
    {
        static VrPerformanceOverlay() => VrSession.OnUnload("performance overlay", Release);

        private const float Period = 0.5f;
        private static readonly Color Good = new Color(0.55f, 1f, 0.6f);
        private static readonly Color Bad = new Color(1f, 0.6f, 0.3f);

        private static GameObject root;
        private static TextMeshProUGUI text;
        private static float nextFontSearch;
        private static bool logged;

        private static float periodStart = -1f;
        private static int frames, missed;
        private static float logicSum, renderSum, bestRefresh;

        private static int errors;

        // Called every frame by VrPerformance.Update. A failure here must not stop the rest of the VR camera's LateUpdate.
        public static void Update(bool running, bool inHab)
        {
            try { Refresh(running, inHab); }
            catch (System.Exception e)
            {
                if (errors++ < 3) Plugin.Log.LogError($"Performance overlay: {e}");
            }
        }

        private static void Refresh(bool running, bool inHab)
        {
            var show = running && VrSettings.PerformanceOverlay.Value;
            if (!show)
            {
                if (root != null && root.activeSelf) root.SetActive(false);
                periodStart = -1f;
                return;
            }
            if (root == null && !Create()) return;
            if (!root.activeSelf) root.SetActive(true);

            var refresh = XRDevice.refreshRate;
            bestRefresh = Mathf.Max(bestRefresh, refresh);
            if (periodStart < 0f)
            {
                Reset();
                return;
            }
            var frame = Time.unscaledDeltaTime;
            frames++;
            if (bestRefresh > 0f && frame >= 1.4f / bestRefresh && frame < 4f / bestRefresh) missed++;
            logicSum += VrPerformance.FrameLogic;
            renderSum += VrPerformance.FrameRender;
            var elapsed = Time.unscaledTime - periodStart;
            if (elapsed < Period) return;

            var fps = frames / elapsed;
            var share = (float)missed / frames;
            var steady = share < 0.05f && refresh >= bestRefresh - 1f;
            var color = ColorUtility.ToHtmlStringRGB(steady ? Good : Bad);
            // English only: the game's language (VrOptions.L) comes from LynxGameUserOptions, whose static constructor
            // throws while the game is still starting, and a type whose static constructor failed stays broken for the
            // whole session. The overlay shows from the first frame of VR, so it once froze the game at the logo.
            text.text =
                $"<color=#{color}>{fps:0} fps</color>   headset {refresh:0} Hz   {share * 100f:0} % missed\n" +
                $"game {logicSum / frames * 1000f:0.0} ms   render {renderSum / frames * 1000f:0.0} ms\n" +
                $"{(inHab ? "station" : "bay")} {XRSettings.eyeTextureResolutionScale:0.00}x   eye sharing {(VrSettings.EyeSharing.Value ? "on" : "off")}";
            Reset();
        }

        private static void Reset()
        {
            periodStart = Time.unscaledTime;
            frames = missed = 0;
            logicSum = renderSum = 0f;
        }

        private static bool Create()
        {
            // The game's font; TMP's default may be unset until the game loaded one.
            if (Time.unscaledTime < nextFontSearch) return false;
            nextFontSearch = Time.unscaledTime + 1f;
            TMP_FontAsset font = null;
            // TMP_Settings throws while TMP has no settings asset loaded.
            try { font = TMP_Settings.defaultFontAsset; }
            catch (System.NullReferenceException) { }
            if (font == null)
            {
                var fonts = Resources.FindObjectsOfTypeAll<TMP_FontAsset>();
                if (fonts.Length > 0) font = fonts[0];
            }
            if (font == null) return false;

            root = new GameObject("ShipbreakerVR Performance Overlay");
            Object.DontDestroyOnLoad(root);
            var canvas = root.AddComponent<Canvas>();
            canvas.renderMode = RenderMode.ScreenSpaceOverlay;
            canvas.sortingOrder = 32000;
            var scaler = root.AddComponent<CanvasScaler>();
            scaler.uiScaleMode = CanvasScaler.ScaleMode.ScaleWithScreenSize;
            scaler.referenceResolution = new Vector2(1920f, 1080f);
            scaler.matchWidthOrHeight = 1f;

            var panel = new GameObject("Background", typeof(RectTransform));
            panel.transform.SetParent(root.transform, false);
            var background = panel.AddComponent<Image>();
            background.color = new Color(0f, 0f, 0f, 0.6f);
            background.raycastTarget = false;
            var rect = (RectTransform)panel.transform;
            // Well inside the corner: at the visor's edge the headset's lenses blur it.
            rect.anchorMin = rect.anchorMax = new Vector2(0.3f, 0.72f);
            rect.pivot = new Vector2(0.5f, 0.5f);
            rect.anchoredPosition = Vector2.zero;
            rect.sizeDelta = new Vector2(560f, 116f);

            var label = new GameObject("Text", typeof(RectTransform));
            label.transform.SetParent(panel.transform, false);
            text = label.AddComponent<TextMeshProUGUI>();
            text.font = font;
            text.fontSize = 28f;
            text.color = Color.white;
            text.raycastTarget = false;
            text.enableWordWrapping = false;
            text.richText = true;
            var textRect = (RectTransform)label.transform;
            textRect.anchorMin = Vector2.zero;
            textRect.anchorMax = Vector2.one;
            textRect.offsetMin = new Vector2(14f, 8f);
            textRect.offsetMax = new Vector2(-14f, -8f);
            if (!logged)
            {
                logged = true;
                Plugin.Log.LogInfo($"Performance overlay shown (font {font.name})");
            }
            return true;
        }

        private static void Release()
        {
            if (root != null) Object.Destroy(root);
            root = null;
            text = null;
        }
    }
}
