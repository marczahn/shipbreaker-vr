using System.Reflection;
using BBI.Unity.Game;
using HarmonyLib;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.HighDefinition;

namespace ShipbreakerVR
{
    // Zeigt die Spiel-UI in VR auf einer Leinwand im Raum.
    // Das Spiel rendert seine UI mit einer eigenen Kamera (Tag "UI_Camera") in einem Custom Pass (HUDCustomPass bzw.
    // DrawUICameraCustomPass) bildschirmfüllend über das Bild. In VR passt das nicht zu den Augen, also fangen wir die
    // Pässe für die VR-Kamera ab: Die UI-Kamera rendert einmal pro Frame in eine eigene Textur, und die Textur wird
    // für jedes Auge auf ein Rechteck im Raum gezeichnet. Im Spiel hängt es vor dem Kopf wie ein Visier-Display
    // (gleicher Blickwinkel wie die Spielkamera, damit Markierungen auf Objekten passen); sonst steht es fest im Raum.
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
        private int drawnFrame = -1;
        private int drawnEyes;
        private int loggedEyes;
        private readonly VrCanvases canvases = new VrCanvases();

        private bool menuMode;
        private bool menuPlaced;
        private Vector3 menuPosition;
        private Quaternion menuRotation;

        // Lage und Größe des Rechtecks in der Welt für den aktuellen Frame.
        private Matrix4x4 screenMatrix;

        public static void Attach(VrCamera vr)
        {
            var ui = vr.gameObject.AddComponent<VrUi>();
            ui.vr = vr;
            Instance = ui;
        }

        private void OnDestroy()
        {
            if (Instance == this) Instance = null;
            if (texture != null) texture.Release();
        }

        public bool Active => VrXr.IsRunning && Plugin.UiInVr.Value && vr != null;

        // Für F4: Menüleinwand neu vor den Kopf setzen.
        public void Recenter() => menuPlaced = false;

        private void LateUpdate()
        {
            if (!Active)
            {
                canvases.Restore();
                return;
            }

            if (uiCamera == null)
            {
                var go = GameObject.FindGameObjectWithTag("UI_Camera");
                if (go != null) uiCamera = go.GetComponent<Camera>();
                if (uiCamera == null) return;
                Plugin.Log.LogInfo($"UI-Kamera gefunden: '{uiCamera.name}', Ebenen {uiCamera.cullingMask}");
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
                    // Immer über der Welt zeichnen, wie ein Display im Helm.
                    material.SetInt("unity_GUIZTestMode", (int)CompareFunction.Always);
                }
                material.mainTexture = texture;
                if (quad == null) quad = CreateQuad();
                Plugin.Log.LogInfo($"UI-Textur {width}x{height} angelegt");
            }

            // Die Canvas-Größe richtet sich nach der UI-Kamera; früh im Frame setzen, damit das Layout passt.
            uiCamera.SetTargetBuffers(texture.colorBuffer, texture.depthBuffer);
            uiCamera.pixelRect = new Rect(0, 0, width, height);
            uiCamera.aspect = (float)width / height;

            canvases.Update(uiCamera);
        }

        private void UpdatePlacement()
        {
            var head = vr.transform;
            var aspect = (float)texture.width / texture.height;
            var gameplay = GameSession.CurrentGameState == GameSession.GameState.Gameplay;
            if (menuMode == gameplay) menuPlaced = false;
            menuMode = !gameplay;

            if (!menuMode)
            {
                // Visier: vor dem Kopf, so groß wie das Sichtfeld der Spielkamera.
                var distance = Plugin.HudDistance.Value;
                var fov = LynxCameraController.MainCamera != null ? LynxCameraController.MainCamera.fieldOfView : 60f;
                var h = 2f * distance * Mathf.Tan(fov * 0.5f * Mathf.Deg2Rad) * Plugin.HudScale.Value;
                screenMatrix = Matrix4x4.TRS(head.position + head.rotation * new Vector3(0, 0, distance), head.rotation, new Vector3(h * aspect, h, 1f));
                return;
            }

            // Menü: einmal vor den Kopf setzen (ohne Neigung zur Seite), dann fest im Spielraum stehen lassen.
            var rig = head.parent;
            if (!menuPlaced)
            {
                menuPlaced = true;
                var forward = head.localRotation * Vector3.forward;
                menuRotation = Quaternion.LookRotation(forward, Vector3.up);
                menuPosition = head.localPosition + forward * Plugin.MenuDistance.Value;
            }
            var w = Plugin.MenuWidth.Value;
            screenMatrix = Matrix4x4.TRS(rig.TransformPoint(menuPosition), rig.rotation * menuRotation, new Vector3(w, w / aspect, 1f));
        }

        // Ersetzt den UI-Pass des Spiels für die VR-Kamera. Liefert false, wenn die Kamera nicht unsere ist.
        public bool TryExecute(CustomPassContext context)
        {
            if (!Active || context.hdCamera.camera != vr.Camera) return false;
            if (uiCamera == null || texture == null) return true;

            var cmd = context.cmd;
            var renderContext = context.renderContext;

            if (renderedFrame != Time.frameCount)
            {
                renderedFrame = Time.frameCount;
                // Erst hier, nach der letzten Kopfabfrage vor dem Rendern, damit das Display nicht nachzieht.
                UpdatePlacement();
                RenderUi(context);
            }

            // Beide UI-Pässe des Spiels können laufen; pro Auge nur einmal zeichnen.
            var eye = GetEye(context.hdCamera, out var view, out var proj);
            if (drawnFrame != Time.frameCount) { drawnFrame = Time.frameCount; drawnEyes = 0; }
            if ((drawnEyes & (1 << eye)) != 0) return true;
            drawnEyes |= 1 << eye;

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
                Plugin.Log.LogInfo($"UI wird in VR gezeichnet (Auge {eye}, {context.hdCamera.actualWidth}x{context.hdCamera.actualHeight})");
            }
            return true;
        }

        // Wie im HUDCustomPass des Spiels, nur in unsere Textur und mit festem Seitenverhältnis.
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

        // Matrizen des Auges, das HDRP gerade rendert (MultiPass: ein Durchgang pro Auge).
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

        private static bool Prefix(CustomPassContext context) => VrUi.Instance == null || !VrUi.Instance.TryExecute(context);
    }

    [HarmonyPatch]
    internal static class DrawUiCameraCustomPassPatch
    {
        private static MethodBase TargetMethod() => AccessTools.Method(AccessTools.TypeByName("DrawUICameraCustomPass"), "Execute", new[] { typeof(CustomPassContext) });

        private static bool Prefix(CustomPassContext context) => VrUi.Instance == null || !VrUi.Instance.TryExecute(context);
    }
}
