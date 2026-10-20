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

        private VrCamera vr;
        private Camera uiCamera;
        private RenderTexture texture;
        private Material material;
        private Mesh quad;
        private int renderedFrame = -1;
        private int loggedEyes;
        private readonly VrCanvases canvases = new VrCanvases();

        private bool menuMode;
        private bool menuPlaced;
        private Vector3 menuPosition;
        private Quaternion menuRotation;

        // Pose and size of the quad in the world for the current frame.
        private Matrix4x4 screenMatrix;

        private Camera ownUiCamera;

        public static void Attach(VrCamera vr)
        {
            var ui = vr.gameObject.AddComponent<VrUi>();
            ui.vr = vr;
            Instance = ui;

            // Our pass runs after post-processing so the UI doesn't go through tonemapping and exposure.
            var go = new GameObject("ShipbreakerVR UI Pass");
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

        // For F4: place the menu canvas in front of the head again.
        public void Recenter() => menuPlaced = false;

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

            canvases.Update(uiCamera);
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
            var gameplay = GameSession.CurrentGameState == GameSession.GameState.Gameplay && LynxCameraController.MainCamera != null;
            if (menuMode == gameplay) menuPlaced = false;
            menuMode = !gameplay;

            if (!menuMode)
            {
                // Visor: in front of the head, as large as the game camera's field of view.
                // In world units, so the visor keeps its felt distance at any world scale.
                var distance = Plugin.HudDistance.Value * VrCamera.WorldScale;
                var fov = LynxCameraController.MainCamera != null ? LynxCameraController.MainCamera.fieldOfView : 60f;
                var h = 2f * distance * Mathf.Tan(fov * 0.5f * Mathf.Deg2Rad) * Plugin.HudScale.Value;
                screenMatrix = Matrix4x4.TRS(head.position + head.rotation * new Vector3(0, 0, distance), head.rotation, new Vector3(h * aspect, h, 1f));
                return;
            }

            // Menu: place once in front of the head (without roll), then keep it fixed in the playspace.
            var rig = head.parent;
            if (!menuPlaced)
            {
                menuPlaced = true;
                // Upright and at eye height, only turned towards where the head looks.
                var forward = head.localRotation * Vector3.forward;
                forward.y = 0f;
                forward = forward.sqrMagnitude > 1e-4f ? forward.normalized : Vector3.forward;
                menuRotation = Quaternion.LookRotation(forward, Vector3.up);
                menuPosition = head.localPosition + forward * Plugin.MenuDistance.Value;
            }
            // menuPosition is in rig space and already scales with the rig; the width is in world units.
            var w = Plugin.MenuWidth.Value * VrCamera.WorldScale;
            screenMatrix = Matrix4x4.TRS(rig.TransformPoint(menuPosition), rig.rotation * menuRotation, new Vector3(w, w / aspect, 1f));
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
            cmd.ClearRenderTarget(true, true, Plugin.ShowUiFrame.Value ? new Color(0f, 0.4f, 1f, 0.15f) : Color.clear);
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
            if (VrUi.Instance != null) VrUi.Instance.Draw(ctx);
        }
    }
}
