using System;
using System.Linq.Expressions;
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
        // HDCamera.xr and its XRPass are internal: compiled once into delegates, so the render path neither boxes
        // nor allocates. GetEye runs for each eye from VrAim.Draw and Draw; the matrices are cached per eye and frame.
        private static Func<HDCamera, Matrix4x4> xrViewMatrix;
        private static Func<HDCamera, Matrix4x4> xrProjMatrix;
        private static Func<HDCamera, int> xrMultipassId;
        private static readonly int[] eyeFrame = { -1, -1, -1, -1, -1, -1, -1, -1 };
        private static readonly Matrix4x4[] eyeView = new Matrix4x4[8];
        private static readonly Matrix4x4[] eyeProj = new Matrix4x4[8];

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
        private bool quadFlipped;
        private int renderedFrame = -1;
        private float nextUiCameraSearch;
        private int loggedEyes;
        private readonly VrCanvases canvases = new VrCanvases();

        private bool menuMode;
        private bool menuPlaced;
        private bool menuFollowing;
        private Vector3 menuPosition;
        private Quaternion menuRotation;

        // The menu canvas stays at eye height: once the eyes are more than MenuDeadZone (tracked meters) above or below
        // its center, it eases back to their height.
        private const float MenuDeadZone = 0.3f;
        private const float MenuFollowSpeed = 4f;

        public enum Steadying { Aus, Schwach, Stark }

        // Visor rotation relative to the rig (playspace), lagging behind the head's tracked rotation (see SteadyVisor).
        private Quaternion visorRotation = Quaternion.identity;
        private bool visorFollowing;
        private bool visorPlaced;

        // Pose and size of the quad in the world for the current frame.
        private Matrix4x4 screenMatrix;

        // On a monitor in the Hab (VrHab.Monitor) the canvas gets a screen look, so it sits in the room like the game's
        // own screens: a little dimmer and cooler, fine scan lines, edges falling off towards the frame, a faint
        // reflection on the glass and light spilling onto the frame (the game's bloom doesn't reach our pass). Made of
        // UI/Default and textures built here, since the plugin brings no shaders of its own.
        private const float GlowMargin = 0.12f;
        private const float ScreenCorner = 0.04f;
        private static readonly Color MonitorTint = new Color(0.9f, 0.93f, 0.96f, 1f);
        private const int MaxMonitorWidth = 4096;
        // Mip levels alone left the writing soft: a finer level than the distance asks for keeps it sharp, without shimmer yet.
        private const float MonitorMipBias = -0.8f;
        private static readonly Color GlowColor = new Color(0.55f, 0.85f, 1f, 0.14f);
        private bool onMonitor;
        private Matrix4x4 glowMatrix;
        private Material shadeMaterial;
        private Material glareMaterial;
        private Material glowMaterial;
        private float lookAspect;

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
            // Unloading (ScriptEngine reload): the canvases go back to the game, and its UI camera no longer draws into
            // our texture, which goes away now.
            canvases.Restore();
            if (uiCamera != null && uiCamera != ownUiCamera) uiCamera.targetTexture = null;
            if (texture != null)
            {
                texture.Release();
                Destroy(texture);
            }
            foreach (var m in new[] { material, fadeMaterial, gameFadeMaterial, shadeMaterial, glareMaterial, glowMaterial })
            {
                if (m == null) continue;
                if (m.mainTexture != null && m.mainTexture != texture) Destroy(m.mainTexture);
                Destroy(m);
            }
            if (quad != null) Destroy(quad);
        }

        public bool Active => VrXr.IsRunning && VrSettings.UiInVr.Value && vr != null;

        // For the F8 dump (VrDiagnostics).
        public RenderTexture Texture => texture;

        public bool IsConverted(Canvas canvas) => canvases.IsConverted(canvas);

        public void Adopt(Transform part) => canvases.Adopt(part);

        // Where a spot in the room shows on the menu canvas: -0.5 to 0.5 across, 0 in the middle. Seen from the resting
        // head, not the tracked one: from that the prompts swam across the canvas with every move of the head.
        public bool OnMenuCanvas(Vector3 spot, out Vector2 place)
        {
            place = Vector2.zero;
            if (!Active || !menuMode || onMonitor) return false;
            var world = screenMatrix.inverse;
            return HabStanding.Across(world.MultiplyPoint3x4(vr.RestingHead), world.MultiplyPoint3x4(spot), out place);
        }

        public string Describe(Transform head)
        {
            if (!Active) return "UI canvas off";
            Vector3 center = screenMatrix.GetColumn(3);
            var width = ((Vector3)screenMatrix.GetColumn(0)).magnitude;
            var height = ((Vector3)screenMatrix.GetColumn(1)).magnitude;
            return $"UI canvas as {(menuMode ? "menu" : "visor")}, at {VrDiagnostics.Direction(head, center)} {Vector3.Distance(head.position, center):0.00} m, {width:0.00} x {height:0.00} m, UI camera '{(uiCamera != null ? uiCamera.name : "-")}' at {(uiCamera != null ? uiCamera.transform.position.ToString() : "-")}";
        }

        // Place the menu canvas in front of the head again: upright at eye height, in the direction the head faces.
        public void Recenter()
        {
            menuPlaced = false;
            visorPlaced = false;
        }

        // From VrCamera.LateUpdate, after the rig's own update (before the helmet), so the order is fixed.
        public void Late()
        {
            if (!Active)
            {
                canvases.Restore();
                return;
            }

            if (onMonitor && VrSettings.Pressed(VrSettings.MonitorLookKey))
            {
                VrSettings.MonitorLook.Value = !VrSettings.MonitorLook.Value;
                Plugin.Log.LogInfo($"Monitor look {(VrSettings.MonitorLook.Value ? "on" : "off")} ({VrSettings.MonitorLookKey.Value})");
            }

            // Until the game has its UI camera (main menu) we use our own; look for the game's twice a second, not
            // every frame.
            if (uiCamera == null || (uiCamera == ownUiCamera && Time.unscaledTime >= nextUiCameraSearch))
            {
                nextUiCameraSearch = Time.unscaledTime + 0.5f;
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
            // At a Hab screen more pixels and mip levels keep the writing readable (Anzeige/MonitorSchaerfe).
            var sharp = onMonitor && VrSettings.MonitorSharpness.Value > 1f;
            if (sharp)
            {
                var factor = Mathf.Max(1f, Mathf.Min(VrSettings.MonitorSharpness.Value, (float)MaxMonitorWidth / width));
                width = Mathf.RoundToInt(width * factor);
                height = Mathf.RoundToInt(height * factor);
            }
            if (texture == null || texture.width != width || texture.height != height || texture.useMipMap != sharp)
            {
                if (texture != null)
                {
                    texture.Release();
                    Destroy(texture);
                }
                texture = new RenderTexture(width, height, 24, RenderTextureFormat.ARGBHalf)
                {
                    name = "ShipbreakerVR UI",
                    useMipMap = sharp,
                    autoGenerateMips = sharp,
                    filterMode = sharp ? FilterMode.Trilinear : FilterMode.Bilinear,
                    anisoLevel = sharp ? 8 : 1,
                    mipMapBias = sharp ? MonitorMipBias : 0f,
                };
                texture.Create();
                if (material == null) material = VrUtil.OverlayMaterial();
                material.mainTexture = texture;
                Plugin.Log.LogInfo($"UI texture {width}x{height} created");
            }
            // UiSpiegeln takes effect at once: the quad is built again when it changes.
            if (quad == null || quadFlipped != VrSettings.FlipUi.Value)
            {
                if (quad != null) Destroy(quad);
                quadFlipped = VrSettings.FlipUi.Value;
                quad = CreateQuad(quadFlipped);
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
            onMonitor = false;
            material.color = Color.white;
            if (menuMode == gameplay)
            {
                menuPlaced = false;
                visorPlaced = false;
            }
            menuMode = !gameplay;

            if (!menuMode)
            {
                // Visor: in front of the head, as large as the game camera's field of view.
                // In world units, so the visor keeps its felt distance at any world scale.
                var distance = VrSettings.HudDistance.Value * VrCamera.WorldScale;
                var fov = LynxCameraController.MainCamera != null ? LynxCameraController.MainCamera.fieldOfView : 60f;
                var h = 2f * distance * Mathf.Tan(fov * 0.5f * Mathf.Deg2Rad) * VrSettings.HudScale.Value;
                // Stays centered on the head, only its rotation lags; the rig's rotation (turning the body) applies at once.
                var rotation = head.parent.rotation * SteadyVisor(head.localRotation);
                var height = VrSettings.HudHeight.Value * 0.01f * VrCamera.WorldScale;
                screenMatrix = Matrix4x4.TRS(head.position + rotation * new Vector3(0, height, distance), rotation, new Vector3(h * aspect, h, 1f));
                return;
            }

            // At one of the Hab's screens the canvas lies on the monitor, as large as fits on it.
            var monitor = vr.FollowsHab ? VrHab.Monitor : null;
            if (monitor != null)
            {
                onMonitor = true;
                if (VrSettings.MonitorLook.Value) material.color = MonitorTint;
                VrHab.MonitorScreen(monitor, out var center, out var rotation, out var size);
                var fit = Mathf.Min(size.x, size.y * aspect);
                var height = fit / aspect;
                screenMatrix = Matrix4x4.TRS(center, rotation, new Vector3(fit, height, 1f));
                var margin = 2f * GlowMargin * height;
                glowMatrix = Matrix4x4.TRS(center, rotation, new Vector3(fit + margin, height + margin, 1f));
                if (!Mathf.Approximately(aspect, lookAspect)) CreateMonitorLook(aspect);
                return;
            }

            // Menu: place once in front of the head, upright at eye height, then keep it fixed in the playspace except for
            // following the eye height.
            var rig = head.parent;
            if (!menuPlaced)
            {
                menuPlaced = true;
                menuFollowing = false;
                // Turned to the head's yaw, so looking far up or down still puts it straight ahead at eye height.
                menuRotation = Quaternion.Euler(0f, head.localEulerAngles.y, 0f);
                menuPosition = head.localPosition + menuRotation * Vector3.forward * VrSettings.MenuDistance.Value;
            }
            else
            {
                var offset = head.localPosition.y - menuPosition.y;
                if (Mathf.Abs(offset) > MenuDeadZone) menuFollowing = true;
                if (menuFollowing)
                {
                    menuPosition.y += offset * (1f - Mathf.Exp(-MenuFollowSpeed * Time.unscaledDeltaTime));
                    if (Mathf.Abs(head.localPosition.y - menuPosition.y) < 0.01f) menuFollowing = false;
                }
            }
            // menuPosition is in rig space and already scales with the rig; the width is in world units.
            var w = VrSettings.MenuWidth.Value * VrCamera.WorldScale;
            screenMatrix = Matrix4x4.TRS(rig.TransformPoint(menuPosition), rig.rotation * menuRotation, new Vector3(w, w / aspect, 1f));
        }

        // A visor fixed to the head copies every small tremor of it. Within a dead zone the visor stays where it is; beyond
        // it, it eases towards the gaze until it is centered again, but never lags more than maxLag behind the head.
        private Quaternion SteadyVisor(Quaternion head)
        {
            float deadZone, speed, maxLag;
            switch (VrSettings.HudSteadying.Value)
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
            // The game's fades to black, kept off the menu canvas (VrCanvases): over the whole room, under the canvas,
            // as in the flat game, where subtitles and prompts stay on top of them.
            if (gameFade > 0f)
            {
                if (gameFadeMaterial == null) gameFadeMaterial = VrUtil.OverlayMaterial();
                gameFadeMaterial.color = new Color(0f, 0f, 0f, gameFade);
                cmd.SetViewProjectionMatrices(Matrix4x4.identity, Matrix4x4.identity);
                cmd.DrawMesh(quad, Matrix4x4.Scale(new Vector3(2f, 2f, 1f)), gameFadeMaterial, 0, 0);
            }
            cmd.SetViewProjectionMatrices(view, proj);
            var look = onMonitor && VrSettings.MonitorLook.Value && shadeMaterial != null;
            if (look) cmd.DrawMesh(quad, glowMatrix, glowMaterial, 0, 0);
            cmd.DrawMesh(quad, screenMatrix, material, 0, 0);
            if (!menuMode) VrFlightGlobe.Draw(cmd, screenMatrix);
            if (look)
            {
                cmd.DrawMesh(quad, screenMatrix, shadeMaterial, 0, 0);
                cmd.DrawMesh(quad, screenMatrix, glareMaterial, 0, 0);
            }
            renderContext.ExecuteCommandBuffer(cmd);
            cmd.Clear();
            renderContext.SetupCameraProperties(context.hdCamera.camera, true);

            VrLoadTrace.UiDrawn();
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
            if (fadeMaterial == null) fadeMaterial = VrUtil.OverlayMaterial();
            if (quad == null) quad = CreateQuad(quadFlipped = VrSettings.FlipUi.Value);
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
        internal static int GetEye(HDCamera hdCamera, out Matrix4x4 view, out Matrix4x4 proj)
        {
            if (xrMultipassId == null) CompileXrAccess();
            var eye = Mathf.Clamp(xrMultipassId(hdCamera), 0, 7);
            if (eyeFrame[eye] != Time.frameCount)
            {
                eyeFrame[eye] = Time.frameCount;
                eyeView[eye] = xrViewMatrix(hdCamera);
                eyeProj[eye] = xrProjMatrix(hdCamera);
            }
            view = eyeView[eye];
            proj = eyeProj[eye];
            return eye;
        }

        // camera => camera.xr.GetViewMatrix(0) and so on.
        private static void CompileXrAccess()
        {
            var xrProperty = AccessTools.Property(typeof(HDCamera), "xr");
            var passType = xrProperty.PropertyType;
            var camera = Expression.Parameter(typeof(HDCamera), "camera");
            var xr = Expression.Property(camera, xrProperty);
            xrViewMatrix = Expression.Lambda<Func<HDCamera, Matrix4x4>>(
                Expression.Call(xr, AccessTools.Method(passType, "GetViewMatrix"), Expression.Constant(0)), camera).Compile();
            xrProjMatrix = Expression.Lambda<Func<HDCamera, Matrix4x4>>(
                Expression.Call(xr, AccessTools.Method(passType, "GetProjMatrix"), Expression.Constant(0)), camera).Compile();
            xrMultipassId = Expression.Lambda<Func<HDCamera, int>>(
                Expression.Property(xr, AccessTools.Property(passType, "multipassId")), camera).Compile();
        }

        // The monitor look's textures for a canvas of this aspect (width / height). Distances are in screen heights.
        private void CreateMonitorLook(float aspect)
        {
            lookAspect = aspect;
            var halfWidth = aspect * 0.5f;

            // Darkening: 128 fine scan lines, and the edges falling off over the outer 10 %.
            var shade = LookTexture("Shade", 256, 512, (x, y) =>
            {
                var line = Mathf.FloorToInt(y * 512f) % 4 < 2 ? 0.09f : 0f;
                var inside = -RoundedRect((x - 0.5f) * aspect, y - 0.5f, halfWidth, 0.5f, ScreenCorner);
                var edge = 0.3f * (1f - Mathf.SmoothStep(0f, 1f, inside / 0.1f));
                return new Color(0f, 0f, 0f, 1f - (1f - line) * (1f - edge));
            });
            // A faint sheen on the glass, from the upper left.
            var glare = LookTexture("Glare", 64, 64, (x, y) =>
            {
                var diagonal = (x + (1f - y)) * 0.5f;
                var sheen = 1f - Mathf.SmoothStep(0f, 1f, diagonal / 0.55f);
                return new Color(1f, 1f, 1f, 0.06f * sheen * sheen);
            });
            // Light around the screen, nothing on it (the canvas may be see-through in places).
            var glowWidth = aspect + 2f * GlowMargin;
            var glowHeight = 1f + 2f * GlowMargin;
            var glow = LookTexture("Glow", 256, 128, (x, y) =>
            {
                var outside = RoundedRect((x - 0.5f) * glowWidth, (y - 0.5f) * glowHeight, halfWidth, 0.5f, ScreenCorner);
                var fall = outside <= 0f ? 0f : 1f - Mathf.SmoothStep(0f, 1f, outside / GlowMargin);
                return new Color(1f, 1f, 1f, fall * fall);
            });

            shadeMaterial = LookMaterial(shadeMaterial, shade, Color.white);
            glareMaterial = LookMaterial(glareMaterial, glare, Color.white);
            glowMaterial = LookMaterial(glowMaterial, glow, GlowColor);
            Plugin.Log.LogInfo($"Monitor look created for aspect {aspect:0.00}");
        }

        private static Texture2D LookTexture(string name, int width, int height, System.Func<float, float, Color> pixel)
        {
            var texture = new Texture2D(width, height, TextureFormat.RGBA32, true)
            {
                name = "ShipbreakerVR Monitor " + name,
                wrapMode = TextureWrapMode.Clamp,
                filterMode = FilterMode.Trilinear,
                anisoLevel = 4
            };
            var pixels = new Color[width * height];
            for (var y = 0; y < height; y++)
                for (var x = 0; x < width; x++)
                    pixels[y * width + x] = pixel((x + 0.5f) / width, (y + 0.5f) / height);
            texture.SetPixels(pixels);
            texture.Apply(true, true);
            return texture;
        }

        private static Material LookMaterial(Material material, Texture2D texture, Color color)
        {
            if (material == null) material = VrUtil.OverlayMaterial();
            else if (material.mainTexture != null) Destroy(material.mainTexture);
            material.mainTexture = texture;
            material.color = color;
            return material;
        }

        // Signed distance to a rectangle with rounded corners (half sizes, corner radius), negative inside.
        private static float RoundedRect(float x, float y, float halfWidth, float halfHeight, float radius)
        {
            var qx = Mathf.Abs(x) - halfWidth + radius;
            var qy = Mathf.Abs(y) - halfHeight + radius;
            var outside = new Vector2(Mathf.Max(qx, 0f), Mathf.Max(qy, 0f)).magnitude;
            return outside + Mathf.Min(Mathf.Max(qx, qy), 0f) - radius;
        }

        private static Mesh CreateQuad(bool flip)
        {
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
            try
            {
                if (VrUi.Instance == null) return;
                VrAim.Draw(ctx, VrCamera.Instance != null ? VrCamera.Instance.Camera : null);
                VrUi.Instance.Draw(ctx);
                VrUi.Instance.DrawFade(ctx);
            }
            finally
            {
                // Closes the GPU sample our BeforeRendering pass opened (VrPerformance).
                VrPerformance.EndGpuSample(ctx);
            }
        }
    }
}
