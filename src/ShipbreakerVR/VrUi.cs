using System.Reflection;
using BBI.Unity.Game;
using HarmonyLib;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.HighDefinition;

namespace ShipbreakerVR
{
    // Shows the game UI in VR on a canvas in the world.
    // The game renders its UI with its own camera (tag "UI_Camera") in a custom pass (HUDCustomPass or
    // DrawUICameraCustomPass) full-screen over the image. In VR that doesn't match the eyes, so we disable
    // these passes for the VR camera and draw in our own pass (own CustomPassVolume, because the game
    // disables its volumes e.g. when paused): the UI camera renders once per frame into its own texture, and
    // the texture is drawn for each eye onto a quad in the world. In gameplay it hangs in front of the head like a
    // visor display; in menus and pause it stays fixed in the world. If there is no UI camera (main menu), we use our own.
    internal class VrUi : MonoBehaviour
    {
        private static readonly PropertyInfo XrProperty = AccessTools.Property(typeof(HDCamera), "xr");
        private static MethodInfo getViewMatrix;
        private static MethodInfo getProjMatrix;
        private static MethodInfo getMultipassId;

        public static VrUi Instance { get; private set; }

        // Layer of our pass volume; unused by the game. HDRP only runs a custom pass volume for a camera whose
        // volumeLayerMask contains its layer, and the load screen camera's mask had only the UI layer.
        public const int PassLayer = 29;

        private VrCamera vr;
        private Camera uiCamera;
        private RenderTexture texture;
        private Material material;
        private Material fadeMaterial;
        private Material gameFadeMaterial;
        private float gameFade;
        private bool loggedGameFade;
        private Mesh quad;
        private int renderedFrame = -1;
        private int loggedEyes;
        private readonly VrCanvases canvases = new VrCanvases();

        private bool menuMode;
        private bool menuPlaced;
        private bool placeAlongGaze;
        private Vector3 menuPosition;
        private Quaternion menuRotation;

        public enum Steadying { Aus, Schwach, Stark }

        // Visor rotation relative to the rig (playspace), lagging behind the head's tracked rotation (see SteadyVisor).
        private Quaternion visorRotation = Quaternion.identity;
        private bool visorFollowing;
        private bool visorPlaced;

        // Pose and size of the quad in the world for the current frame.
        private Matrix4x4 screenMatrix;

        private Camera ownUiCamera;

        public static void Attach(VrCamera vr)
        {
            var ui = vr.gameObject.AddComponent<VrUi>();
            ui.vr = vr;
            Instance = ui;

            // Our pass runs after post-processing so the UI doesn't go through tonemapping and exposure.
            var go = new GameObject("ShipbreakerVR UI Pass") { layer = PassLayer };
            go.SetActive(false);
            go.transform.SetParent(vr.transform.parent, false);
            var volume = go.AddComponent<CustomPassVolume>();
            volume.isGlobal = true;
            volume.injectionPoint = CustomPassInjectionPoint.AfterPostProcess;
            volume.AddPassOfType<VrUiPass>();
            go.SetActive(true);
        }

        // For the VR camera our own pass takes over; the game's UI passes draw nothing there.
        public bool OwnsCamera(Camera camera) => Active && vr != null && camera == vr.Camera;

        private void OnDestroy()
        {
            if (Instance == this) Instance = null;
            if (texture != null) texture.Release();
        }

        public bool Active => VrXr.IsRunning && Plugin.UiInVr.Value && vr != null;

        // For the F8 dump (VrDiagnostics).
        public RenderTexture Texture => texture;

        public bool IsConverted(Canvas canvas) => canvases.IsConverted(canvas);

        public string Describe(Transform head)
        {
            if (!Active) return "UI canvas off";
            Vector3 center = screenMatrix.GetColumn(3);
            var width = ((Vector3)screenMatrix.GetColumn(0)).magnitude;
            var height = ((Vector3)screenMatrix.GetColumn(1)).magnitude;
            return $"UI canvas as {(menuMode ? "menu" : "visor")}, at {VrDiagnostics.Direction(head, center)} {Vector3.Distance(head.position, center):0.00} m, {width:0.00} x {height:0.00} m, UI camera '{(uiCamera != null ? uiCamera.name : "-")}' at {(uiCamera != null ? uiCamera.transform.position.ToString() : "-")}";
        }

        // Place the menu canvas in front of the head again. toGaze (F4): straight in front of the eyes, also tilted up or down. Otherwise upright at eye height.
        public void Recenter(bool toGaze)
        {
            menuPlaced = false;
            placeAlongGaze = toGaze;
            visorPlaced = false;
        }

        private void LateUpdate()
        {
            if (!Active)
            {
                canvases.Restore();
                return;
            }

            if (uiCamera == null || uiCamera == ownUiCamera)
            {
                var go = GameObject.FindGameObjectWithTag("UI_Camera");
                var found = go != null ? go.GetComponent<Camera>() : null;
                if (found != null)
                {
                    if (uiCamera != null) canvases.Restore();
                    uiCamera = found;
                    Plugin.Log.LogInfo($"UI camera found: '{uiCamera.name}', layers {uiCamera.cullingMask}");
                }
                else if (uiCamera == null)
                {
                    uiCamera = OwnUiCamera();
                    Plugin.Log.LogInfo("No UI camera in game, using our own");
                }
            }

            var width = Mathf.Max(Screen.width, 640);
            var height = Mathf.Max(Screen.height, 360);
            if (texture == null || texture.width != width || texture.height != height)
            {
                if (texture != null) texture.Release();
                texture = new RenderTexture(width, height, 24, RenderTextureFormat.ARGBHalf) { name = "ShipbreakerVR UI" };
                texture.Create();
                if (material == null)
                {
                    material = new Material(Shader.Find("UI/Default"));
                    // Always draw on top of the world, like a display in the helmet.
                    material.SetInt("unity_GUIZTestMode", (int)CompareFunction.Always);
                }
                material.mainTexture = texture;
                if (quad == null) quad = CreateQuad();
                Plugin.Log.LogInfo($"UI texture {width}x{height} created");
            }

            // Canvas size follows the UI camera; set it early in the frame so the layout fits.
            uiCamera.SetTargetBuffers(texture.colorBuffer, texture.depthBuffer);
            uiCamera.pixelRect = new Rect(0, 0, width, height);
            uiCamera.aspect = (float)width / height;

            canvases.HideFades = menuMode;
            canvases.Update(uiCamera);
            gameFade = canvases.FadeAlpha();
            if (gameFade > 0.5f && !loggedGameFade)
            {
                loggedGameFade = true;
                Plugin.Log.LogInfo("Game fade to black drawn over the whole view instead of on the menu canvas");
            }
        }

        // Own UI camera for scenes without the game's; renders only the UI layer and never on its own.
        private Camera OwnUiCamera()
        {
            if (ownUiCamera != null) return ownUiCamera;
            var go = new GameObject("ShipbreakerVR UI Camera");
            go.transform.SetParent(transform.parent, false);
            go.transform.position = new Vector3(0f, -10000f, 0f);
            ownUiCamera = go.AddComponent<Camera>();
            ownUiCamera.enabled = false;
            ownUiCamera.cullingMask = 1 << 5;
            ownUiCamera.nearClipPlane = 0.1f;
            ownUiCamera.farClipPlane = 1000f;
            ownUiCamera.clearFlags = CameraClearFlags.SolidColor;
            ownUiCamera.backgroundColor = Color.clear;
            return ownUiCamera;
        }

        private void UpdatePlacement()
        {
            var head = vr.transform;
            var aspect = (float)texture.width / texture.height;
            // The load screen is a menu too, even if the game already switched to gameplay behind it.
            var loadScreen = SceneLoader.Instance != null && SceneLoader.Instance.LoadScreenVisible;
            var gameplay = GameSession.CurrentGameState == GameSession.GameState.Gameplay && LynxCameraController.MainCamera != null && !loadScreen;
            if (menuMode == gameplay)
            {
                menuPlaced = false;
                visorPlaced = false;
            }
            menuMode = !gameplay;

            if (!menuMode)
            {
                // F4 in gameplay only recenters the head; the next menu opens upright again.
                placeAlongGaze = false;
                // Visor: in front of the head, as large as the game camera's field of view.
                // In world units, so the visor keeps its felt distance at any world scale.
                var distance = Plugin.HudDistance.Value * VrCamera.WorldScale;
                var fov = LynxCameraController.MainCamera != null ? LynxCameraController.MainCamera.fieldOfView : 60f;
                var h = 2f * distance * Mathf.Tan(fov * 0.5f * Mathf.Deg2Rad) * Plugin.HudScale.Value;
                // Stays centered on the head, only its rotation lags; the rig's rotation (turning the body) applies at once.
                var rotation = head.parent.rotation * SteadyVisor(head.localRotation);
                screenMatrix = Matrix4x4.TRS(head.position + rotation * new Vector3(0, 0, distance), rotation, new Vector3(h * aspect, h, 1f));
                return;
            }

            // Menu: place once in front of the head (without roll), then keep it fixed in the playspace.
            var rig = head.parent;
            if (!menuPlaced)
            {
                menuPlaced = true;
                // Turned towards where the head looks, without roll. Upright at eye height unless placed along the gaze.
                var forward = head.localRotation * Vector3.forward;
                if (!placeAlongGaze) forward.y = 0f;
                placeAlongGaze = false;
                forward = forward.sqrMagnitude > 1e-4f && Mathf.Abs(forward.normalized.y) < 0.99f ? forward.normalized : Vector3.forward;
                menuRotation = Quaternion.LookRotation(forward, Vector3.up);
                menuPosition = head.localPosition + forward * Plugin.MenuDistance.Value;
            }
            // menuPosition is in rig space and already scales with the rig; the width is in world units.
            var w = Plugin.MenuWidth.Value * VrCamera.WorldScale;
            screenMatrix = Matrix4x4.TRS(rig.TransformPoint(menuPosition), rig.rotation * menuRotation, new Vector3(w, w / aspect, 1f));
        }

        // A visor fixed to the head copies every small tremor of it. Within a dead zone the visor stays where it is; beyond
        // it, it eases towards the gaze until it is centered again, but never lags more than maxLag behind the head.
        private Quaternion SteadyVisor(Quaternion head)
        {
            float deadZone, speed, maxLag;
            switch (Plugin.HudSteadying.Value)
            {
                case Steadying.Schwach: deadZone = 1.5f; speed = 8f; maxLag = 8f; break;
                case Steadying.Stark: deadZone = 4f; speed = 4f; maxLag = 15f; break;
                default: return head;
            }
            if (!visorPlaced)
            {
                visorPlaced = true;
                visorFollowing = false;
                visorRotation = head;
                return head;
            }
            var angle = Quaternion.Angle(visorRotation, head);
            if (angle > deadZone) visorFollowing = true;
            if (visorFollowing)
            {
                visorRotation = Quaternion.Slerp(visorRotation, head, 1f - Mathf.Exp(-speed * Time.unscaledDeltaTime));
                angle = Quaternion.Angle(visorRotation, head);
                if (angle < 0.2f) visorFollowing = false;
            }
            if (angle > maxLag) visorRotation = Quaternion.RotateTowards(head, visorRotation, maxLag);
            return visorRotation;
        }

        // Called from our own pass: draws the UI for the VR camera.
        public void Draw(CustomPassContext context)
        {
            if (!OwnsCamera(context.hdCamera.camera) || uiCamera == null || texture == null) return;

            var cmd = context.cmd;
            var renderContext = context.renderContext;

            if (renderedFrame != Time.frameCount)
            {
                renderedFrame = Time.frameCount;
                // Only here, after the last head pose update before rendering, so the display doesn't lag.
                UpdatePlacement();
                RenderUi(context);
            }

            var eye = GetEye(context.hdCamera, out var view, out var proj);

            cmd.SetRenderTarget(context.cameraColorBuffer, 0, CubemapFace.Unknown, -1);
            cmd.SetViewport(new Rect(0, 0, context.hdCamera.actualWidth, context.hdCamera.actualHeight));
            // The game's fades to black, kept off the menu canvas (VrCanvases), and the dimmed room at a Hab screen
            // (VrHab): over the whole room, under the canvas, as in the flat game, where subtitles and prompts stay on
            // top of them.
            var under = Mathf.Max(gameFade, vr.FollowsHab ? VrHab.ScreenDim : 0f);
            if (under > 0f)
            {
                if (gameFadeMaterial == null) gameFadeMaterial = BlackMaterial();
                gameFadeMaterial.color = new Color(0f, 0f, 0f, under);
                cmd.SetViewProjectionMatrices(Matrix4x4.identity, Matrix4x4.identity);
                cmd.DrawMesh(quad, Matrix4x4.Scale(new Vector3(2f, 2f, 1f)), gameFadeMaterial, 0, 0);
            }
            cmd.SetViewProjectionMatrices(view, proj);
            cmd.DrawMesh(quad, screenMatrix, material, 0, 0);
            renderContext.ExecuteCommandBuffer(cmd);
            cmd.Clear();
            renderContext.SetupCameraProperties(context.hdCamera.camera, true);

            if ((loggedEyes & (1 << eye)) == 0)
            {
                loggedEyes |= 1 << eye;
                Plugin.Log.LogInfo($"UI drawn in VR (eye {eye}, {context.hdCamera.actualWidth}x{context.hdCamera.actualHeight})");
            }
        }

        // Black over the whole view, UI included, while the Hab fades between its camera spots (VrHab).
        public void DrawFade(CustomPassContext context)
        {
            var fade = VrHab.Fade;
            if (fade <= 0f || !VrXr.IsRunning || vr == null || context.hdCamera.camera != vr.Camera) return;
            if (fadeMaterial == null) fadeMaterial = BlackMaterial();
            if (quad == null) quad = CreateQuad();
            fadeMaterial.color = new Color(0f, 0f, 0f, fade);

            var cmd = context.cmd;
            cmd.SetRenderTarget(context.cameraColorBuffer, 0, CubemapFace.Unknown, -1);
            cmd.SetViewport(new Rect(0, 0, context.hdCamera.actualWidth, context.hdCamera.actualHeight));
            // Identity matrices: the quad's corners at +-1 are the corners of the view.
            cmd.SetViewProjectionMatrices(Matrix4x4.identity, Matrix4x4.identity);
            cmd.DrawMesh(quad, Matrix4x4.Scale(new Vector3(2f, 2f, 1f)), fadeMaterial, 0, 0);
            context.renderContext.ExecuteCommandBuffer(cmd);
            cmd.Clear();
            context.renderContext.SetupCameraProperties(context.hdCamera.camera, true);
        }

        // Its color sets how dark; drawn on a quad with identity matrices, whose corners at +-1 are the corners of the view.
        private static Material BlackMaterial()
        {
            var black = new Material(Shader.Find("UI/Default"));
            black.SetInt("unity_GUIZTestMode", (int)CompareFunction.Always);
            return black;
        }

        // Like the game's HUDCustomPass, but into our texture and with a fixed aspect ratio.
        private void RenderUi(CustomPassContext context)
        {
            var cmd = context.cmd;
            var renderContext = context.renderContext;
            uiCamera.SetTargetBuffers(texture.colorBuffer, texture.depthBuffer);
            uiCamera.pixelRect = new Rect(0, 0, texture.width, texture.height);
            uiCamera.aspect = (float)texture.width / texture.height;
            if (!uiCamera.TryGetCullingParameters(out var culling)) return;
            culling.cullingOptions = CullingOptions.None;

            cmd.SetRenderTarget(texture.colorBuffer, texture.depthBuffer);
            cmd.ClearRenderTarget(true, true, Color.clear);
            renderContext.ExecuteCommandBuffer(cmd);
            cmd.Clear();
            renderContext.SetupCameraProperties(uiCamera);
            ScriptableRenderContext.EmitGeometryForCamera(uiCamera);
            var results = renderContext.Cull(ref culling);
            var sorting = new SortingSettings(uiCamera) { criteria = SortingCriteria.CommonTransparent };
            var drawing = new DrawingSettings(new ShaderTagId("SRPDefaultUnlit"), sorting) { perObjectData = PerObjectData.None };
            var filtering = new FilteringSettings(RenderQueueRange.transparent);
            renderContext.DrawRenderers(results, ref drawing, ref filtering);
            renderContext.SetupCameraProperties(context.hdCamera.camera, true);
        }

        // Matrices of the eye HDRP is currently rendering (MultiPass: one pass per eye).
        private static int GetEye(HDCamera hdCamera, out Matrix4x4 view, out Matrix4x4 proj)
        {
            var xr = XrProperty.GetValue(hdCamera);
            if (getViewMatrix == null)
            {
                var type = xr.GetType();
                getViewMatrix = AccessTools.Method(type, "GetViewMatrix");
                getProjMatrix = AccessTools.Method(type, "GetProjMatrix");
                getMultipassId = AccessTools.PropertyGetter(type, "multipassId");
            }
            var args = new object[] { 0 };
            view = (Matrix4x4)getViewMatrix.Invoke(xr, args);
            proj = (Matrix4x4)getProjMatrix.Invoke(xr, args);
            return Mathf.Clamp((int)getMultipassId.Invoke(xr, null), 0, 7);
        }

        private static Mesh CreateQuad()
        {
            var flip = Plugin.FlipUi.Value;
            var mesh = new Mesh { name = "ShipbreakerVR UI" };
            mesh.vertices = new[] { new Vector3(-0.5f, -0.5f, 0), new Vector3(0.5f, -0.5f, 0), new Vector3(0.5f, 0.5f, 0), new Vector3(-0.5f, 0.5f, 0) };
            mesh.uv = flip
                ? new[] { new Vector2(0, 1), new Vector2(1, 1), new Vector2(1, 0), new Vector2(0, 0) }
                : new[] { new Vector2(0, 0), new Vector2(1, 0), new Vector2(1, 1), new Vector2(0, 1) };
            mesh.colors = new[] { Color.white, Color.white, Color.white, Color.white };
            mesh.triangles = new[] { 0, 2, 1, 0, 3, 2 };
            return mesh;
        }
    }

    [HarmonyPatch]
    internal static class HudCustomPassPatch
    {
        private static MethodBase TargetMethod() => AccessTools.Method(AccessTools.TypeByName("HUDCustomPass"), "Execute", new[] { typeof(CustomPassContext) });

        private static bool Prefix(CustomPassContext context) => VrUi.Instance == null || !VrUi.Instance.OwnsCamera(context.hdCamera.camera);
    }

    [HarmonyPatch]
    internal static class DrawUiCameraCustomPassPatch
    {
        private static MethodBase TargetMethod() => AccessTools.Method(AccessTools.TypeByName("DrawUICameraCustomPass"), "Execute", new[] { typeof(CustomPassContext) });

        private static bool Prefix(CustomPassContext context) => VrUi.Instance == null || !VrUi.Instance.OwnsCamera(context.hdCamera.camera);
    }

    internal class VrUiPass : CustomPass
    {
        protected override void Execute(CustomPassContext ctx)
        {
            if (VrUi.Instance == null) return;
            VrUi.Instance.Draw(ctx);
            VrUi.Instance.DrawFade(ctx);
        }
    }
}
