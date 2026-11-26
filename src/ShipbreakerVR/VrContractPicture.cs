using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.HighDefinition;
using UnityEngine.UI;

namespace ShipbreakerVR
{
    // The contract at the desk drawn as the Hab's screens are (Raik, 2026-11-26: "the station's screens are well readable
    // and smooth, here at the campaign screen the edges still flicker quite a bit"). The contract is a world-space
    // canvas the headset's camera drew straight into the room, every line and letter at whatever pixels it fell on.
    // The Hab's screens go through the UI texture (VrUi): Anzeige/MonitorSchaerfe times the window's size, at most
    // 4096 wide, with mip levels, trilinear and anisotropic filtering and the same mip bias. Here the contract's canvas
    // is moved onto the mod's pass layer (VrUi.PassLayer, which the headset's camera leaves out), an orthographic
    // camera of its own draws just that layer into such a texture, and a world-space canvas on the contract's own layer
    // shows the texture in the contract's place. The canvas itself stays where it is, so the keyboard (VrDeskKeyboard)
    // and the game's own handling of it are as before.
    // Raik's first test: the terminal stayed empty, the contract gone. The camera drew into the texture through
    // SetTargetBuffers, which HDRP does not take; it renders to targetTexture. And the contract is only taken off the
    // headset's view once the picture is known to work: after the camera has rendered once (endCameraRendering), and a
    // read of the texture's smallest mip levels a moment later finds something drawn in it (Checks tries); otherwise the
    // contract is drawn as before, and the log says so.
    internal static class VrContractPicture
    {
        private const int Checks = 3;
        private const float CheckAfter = 1.5f;
        // -1: given up, the contract drawn as before; 0: not known yet; 1: the picture works.
        private static int works;
        private static bool rendered, hooked, checking;
        private static int checks;
        private static float checkAt = -1f;

        private const int Layer = VrUi.PassLayer;

        private static Camera camera;
        private static RenderTexture texture;
        private static RectTransform picture;
        private static RawImage image;
        private static readonly Dictionary<GameObject, int> layers = new Dictionary<GameObject, int>();
        private static readonly List<Transform> parts = new List<Transform>();
        private static int shownLayer = -1;
        private static bool logged;

        // Every frame while the contract lies on the terminal (VrContractDesk.Keep), after it is placed.
        public static void Update(RectTransform contract)
        {
            if (contract == null || contract.parent == null || !contract.gameObject.activeInHierarchy)
            {
                Hide();
                return;
            }
            var rect = contract.rect;
            var scale = contract.lossyScale;
            var wide = rect.width * Mathf.Abs(scale.x);
            var high = rect.height * Mathf.Abs(scale.y);
            if (rect.width <= 1f || rect.height <= 1f || wide < 1e-4f || high < 1e-4f)
            {
                Hide();
                return;
            }
            if (works < 0)
            {
                Hide();
                return;
            }
            if (shownLayer < 0) shownLayer = contract.gameObject.layer;
            if (!hooked)
            {
                hooked = true;
                RenderPipelineManager.endCameraRendering += Rendered;
            }
            // Each frame once the camera renders: the game adds its pop-ups to the contract as it goes.
            if (rendered)
            {
                contract.GetComponentsInChildren(true, parts);
                foreach (var part in parts)
                {
                    var go = part.gameObject;
                    if (go.layer == Layer) continue;
                    if (!layers.ContainsKey(go)) layers[go] = go.layer;
                    go.layer = Layer;
                }
                parts.Clear();
                if (works == 0 && !checking && checkAt < 0f) checkAt = Time.unscaledTime + CheckAfter;
                if (works == 0 && !checking && checkAt >= 0f && Time.unscaledTime >= checkAt) Check();
            }

            var sharpness = Mathf.Max(1f, VrSettings.MonitorSharpness.Value);
            var width = Mathf.Min(Mathf.RoundToInt(Mathf.Max(Screen.width, 640) * sharpness), VrUi.MaxMonitorWidth);
            var height = Mathf.Max(16, Mathf.RoundToInt(width * rect.height / rect.width));
            if (texture == null || texture.width != width || texture.height != height)
            {
                if (texture != null)
                {
                    texture.Release();
                    Object.Destroy(texture);
                }
                texture = new RenderTexture(width, height, 24, RenderTextureFormat.ARGBHalf)
                {
                    name = "ShipbreakerVR contract",
                    useMipMap = true,
                    autoGenerateMips = true,
                    filterMode = FilterMode.Trilinear,
                    anisoLevel = 8,
                    mipMapBias = VrUi.MonitorMipBias,
                };
                texture.Create();
                if (image != null) image.texture = texture;
            }
            if (camera == null) camera = MakeCamera();
            if (picture == null) MakePicture();
            picture.gameObject.layer = shownLayer;

            // The camera before the contract's middle, looking into it as the player does, just as wide and high.
            var center = contract.TransformPoint(rect.center);
            var depth = Mathf.Max(wide, high);
            camera.transform.SetPositionAndRotation(center - contract.forward * depth, contract.rotation);
            camera.orthographicSize = high * 0.5f;
            camera.aspect = wide / high;
            camera.nearClipPlane = depth * 0.01f;
            camera.farClipPlane = depth * 2f;
            if (camera.targetTexture != texture) camera.targetTexture = texture;
            camera.enabled = true;

            // The picture where the contract's canvas lies, as large.
            if (picture.parent != contract.parent) picture.SetParent(contract.parent, false);
            picture.localRotation = contract.localRotation;
            picture.localScale = contract.localScale;
            picture.sizeDelta = rect.size;
            picture.position = center;
            if (picture.gameObject.activeSelf != rendered) picture.gameObject.SetActive(rendered);
            if (!logged)
            {
                logged = true;
                Plugin.Log.LogInfo($"Contract desk: the contract drawn as the Hab's screens are, {width} x {height} with mip levels ({wide * 100f:0} x {high * 100f:0} cm)");
            }
        }

        private static void Rendered(ScriptableRenderContext context, Camera rendering)
        {
            if (rendering == null || rendering != camera || rendered) return;
            rendered = true;
            Plugin.Log.LogInfo("Contract desk: the contract's camera renders, the contract goes onto its picture");
        }

        // The texture's smallest mip levels read back: anything drawn (not clear) means the picture works.
        private static void Check()
        {
            if (texture == null) return;
            checking = true;
            var mip = Mathf.Max(0, texture.mipmapCount - 5);
            AsyncGPUReadback.Request(texture, mip, request =>
            {
                checking = false;
                checkAt = -1f;
                if (works != 0) return;
                var drawn = false;
                if (!request.hasError)
                {
                    var data = request.GetData<ushort>();
                    for (var i = 3; i < data.Length; i += 4)
                        if (data[i] != 0)
                        {
                            drawn = true;
                            break;
                        }
                }
                checks++;
                if (drawn)
                {
                    works = 1;
                    Plugin.Log.LogInfo("Contract desk: the contract's picture has the contract in it");
                }
                else if (checks >= Checks || request.hasError)
                {
                    works = -1;
                    Hide();
                    Plugin.Log.LogWarning($"Contract desk: the contract's picture stayed empty ({(request.hasError ? "could not be read" : $"{checks} looks")}), the contract is drawn as before");
                }
            });
        }

        private static Camera MakeCamera()
        {
            var go = new GameObject("ShipbreakerVR contract camera");
            Object.DontDestroyOnLoad(go);
            var made = go.AddComponent<Camera>();
            made.cullingMask = 1 << Layer;
            made.orthographic = true;
            made.clearFlags = CameraClearFlags.SolidColor;
            made.backgroundColor = Color.clear;
            made.stereoTargetEye = StereoTargetEyeMask.None;
            made.allowMSAA = false;
            made.depth = -50f;
            var hd = go.GetComponent<HDAdditionalCameraData>() ?? go.AddComponent<HDAdditionalCameraData>();
            hd.xrRendering = false;
            hd.clearColorMode = HDAdditionalCameraData.ClearColorMode.Color;
            hd.backgroundColorHDR = Color.clear;
            hd.volumeLayerMask = 0;
            hd.antialiasing = HDAdditionalCameraData.AntialiasingMode.None;
            // The headset's camera tone-maps the picture as it did the canvas; not twice.
            hd.customRenderingSettings = true;
            hd.renderingPathCustomFrameSettingsOverrideMask.mask[(uint)FrameSettingsField.Postprocess] = true;
            hd.renderingPathCustomFrameSettings.SetEnabled(FrameSettingsField.Postprocess, false);
            return made;
        }

        private static void MakePicture()
        {
            var go = new GameObject("ShipbreakerVR contract picture", typeof(RectTransform), typeof(Canvas), typeof(RawImage));
            go.SetActive(false);
            var canvas = go.GetComponent<Canvas>();
            canvas.renderMode = RenderMode.WorldSpace;
            picture = (RectTransform)go.transform;
            picture.pivot = new Vector2(0.5f, 0.5f);
            image = go.GetComponent<RawImage>();
            image.texture = texture;
            image.raycastTarget = false;
        }

        // The contract's canvas back on its own layers, the camera and the picture off.
        public static void Hide()
        {
            foreach (var each in layers)
                if (each.Key != null) each.Key.layer = each.Value;
            layers.Clear();
            if (camera != null) camera.enabled = false;
            if (picture != null && picture.gameObject.activeSelf) picture.gameObject.SetActive(false);
            rendered = false;
            checkAt = -1f;
        }

        // The front end left: all of it gone.
        public static void Release()
        {
            Hide();
            if (camera != null) Object.Destroy(camera.gameObject);
            camera = null;
            if (picture != null) Object.Destroy(picture.gameObject);
            picture = null;
            image = null;
            if (texture != null)
            {
                texture.Release();
                Object.Destroy(texture);
            }
            texture = null;
            shownLayer = -1;
            logged = false;
            works = 0;
            checks = 0;
            if (hooked) RenderPipelineManager.endCameraRendering -= Rendered;
            hooked = false;
        }
    }
}
