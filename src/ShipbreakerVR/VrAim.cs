using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Reflection.Emit;
using BBI.Unity.Game;
using HarmonyLib;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.HighDefinition;
using UnityEngine.XR;

namespace ShipbreakerVR
{
    // Aiming with the right hand. Every tool aims through the game's main camera: rays through screen points
    // (ScreenPointToRay), its position and forward, and its matrices for the ray grids of grabbing and the grapple.
    // A camera that never renders sits on the right controller's aim pose with the main camera's lens and takes the
    // main camera's place in the tools' code (AimPatch). The screen-space patterns (the cutter's line, the grapple's
    // search grid) then fan out from the hand as they did from the eye, and rolling the hand rolls the cutter's line.
    // A beam from the hand to what it points at shows where that is; the visor's crosshair still marks the head's center.
    // The buttons of the right controller work the tools while aiming with the hand (VrToolInput).
    // The tool models stay where the game puts them, in front of the head.
    internal static class VrAim
    {
        private const float BeamWidth = 0.004f;
        private const float BeamMiss = 5f;
        private const float BeamRange = 100f;
        // Size of the dot on what the hand points at, as a fraction of its distance from the eye (about 0.7 degrees).
        private const float DotSize = 0.012f;

        private static readonly InputFeatureUsage<Vector3> PointerPosition = new InputFeatureUsage<Vector3>("PointerPosition");
        private static readonly InputFeatureUsage<Quaternion> PointerRotation = new InputFeatureUsage<Quaternion>("PointerRotation");

        private static InputDevice right;
        private static Camera aim;
        private static int updatedFrame = -1;
        private static bool active;
        private static bool loggedPose;

        private static int beamFrame = -1;
        private static bool beamVisible;
        private static bool beamHit;
        private static Vector3 beamStart;
        private static Vector3 beamEnd;
        private static string beamTarget = "-";
        private static int pointerMask;
        private static Mesh beamMesh;
        private static Mesh dotMesh;
        private static Material beamMaterial;
        private static Material dotMaterial;

        // True while the tools aim from the right hand: VR in gameplay, the option on and the controller tracked.
        public static bool Active
        {
            get
            {
                Update();
                return active;
            }
        }

        // What the patched tool code uses instead of LynxCameraController.MainCamera and MainCameraTransform.
        public static Camera Camera => Active ? aim : LynxCameraController.MainCamera;

        public static Transform CameraTransform => Active ? aim.transform : LynxCameraController.MainCameraTransform;

        // Where the cutter's line starts in CuttingFXController.StartCutLine (the tool's own transform in the flat game).
        public static Transform CutOrigin(Component tool) => Active ? aim.transform : tool.transform;

        private static void Update()
        {
            if (updatedFrame == Time.frameCount) return;
            updatedFrame = Time.frameCount;
            active = false;
            var main = LynxCameraController.MainCamera;
            if (main == null || !TryGetPose(out var position, out var rotation)) return;

            if (aim == null)
            {
                var go = new GameObject("ShipbreakerVR Aim");
                Object.DontDestroyOnLoad(go);
                aim = go.AddComponent<Camera>();
                aim.enabled = false;
                aim.stereoTargetEye = StereoTargetEyeMask.None;
                Plugin.Log.LogInfo("Aim camera created on the right hand");
            }
            aim.transform.SetPositionAndRotation(position, rotation);
            // The main camera's lens and screen rectangle, so screen points map to the same angles as from the eye.
            aim.fieldOfView = main.fieldOfView;
            aim.rect = main.rect;
            aim.aspect = main.aspect;
            aim.nearClipPlane = main.nearClipPlane;
            aim.farClipPlane = main.farClipPlane;
            active = true;
        }

        // The right controller's aim pose in the world (it points where the controller points; the grip pose runs along
        // the handle). Poses are in tracking space, i.e. relative to the rig.
        private static bool TryGetPose(out Vector3 position, out Quaternion rotation)
        {
            position = Vector3.zero;
            rotation = Quaternion.identity;
            var vr = VrCamera.Instance;
            if (!VrXr.IsRunning || !Plugin.AimWithHand.Value || vr == null || vr.FollowsHab) return false;
            if (GameSession.CurrentGameState != GameSession.GameState.Gameplay) return false;
            if (!right.isValid) right = InputDevices.GetDeviceAtXRNode(XRNode.RightHand);
            if (!right.isValid) return false;
            if (right.TryGetFeatureValue(CommonUsages.isTracked, out var tracked) && !tracked) return false;
            var local = Vector3.zero;
            var localRotation = Quaternion.identity;
            var pointer = right.TryGetFeatureValue(PointerPosition, out local) && right.TryGetFeatureValue(PointerRotation, out localRotation);
            if (!pointer && !(right.TryGetFeatureValue(CommonUsages.devicePosition, out local) && right.TryGetFeatureValue(CommonUsages.deviceRotation, out localRotation)))
                return false;
            var rig = vr.transform.parent;
            position = rig.TransformPoint(local);
            rotation = rig.rotation * localRotation;
            if (!loggedPose)
            {
                loggedPose = true;
                Plugin.Log.LogInfo($"Aiming with the right hand ('{right.name}', {(pointer ? "aim pose" : "grip pose, no aim pose")})");
            }
            return true;
        }

        // Called from our UI pass for each eye, before the UI: the beam from the hand and a dot on what it hits. Drawn
        // over everything (the pass has no depth), which is fine since the beam ends at the first thing it meets.
        public static void Draw(CustomPassContext context, Camera vrCamera)
        {
            if (vrCamera == null || context.hdCamera.camera != vrCamera) return;
            UpdateBeam();
            if (!beamVisible) return;
            if (beamMesh == null) CreateMeshes();

            VrUi.GetEye(context.hdCamera, out var view, out var proj);
            var eye = (Vector3)view.inverse.GetColumn(3);
            var scale = VrCamera.WorldScale;
            var cmd = context.cmd;
            cmd.SetRenderTarget(context.cameraColorBuffer, 0, CubemapFace.Unknown, -1);
            cmd.SetViewport(new Rect(0, 0, context.hdCamera.actualWidth, context.hdCamera.actualHeight));
            cmd.SetViewProjectionMatrices(view, proj);

            // The beam is a strip along its direction, turned to face the eye.
            var along = beamEnd - beamStart;
            var length = along.magnitude;
            if (length > 0.01f)
            {
                var direction = along / length;
                var toEye = Vector3.ProjectOnPlane(eye - beamStart, direction);
                if (toEye.sqrMagnitude < 1e-8f) toEye = Vector3.ProjectOnPlane(Vector3.up, direction);
                var matrix = Matrix4x4.TRS(beamStart, Quaternion.LookRotation(toEye, direction), new Vector3(BeamWidth * scale, length, 1f));
                cmd.DrawMesh(beamMesh, matrix, beamMaterial, 0, 0);
            }
            if (beamHit)
            {
                var size = Vector3.Distance(eye, beamEnd) * DotSize;
                cmd.DrawMesh(dotMesh, Matrix4x4.TRS(beamEnd, Quaternion.LookRotation(eye - beamEnd), new Vector3(size, size, 1f)), dotMaterial, 0, 0);
            }
            context.renderContext.ExecuteCommandBuffer(cmd);
            cmd.Clear();
            context.renderContext.SetupCameraProperties(context.hdCamera.camera, true);
        }

        // Once per frame at render time, with the latest hand pose and the rig where it is drawn.
        private static void UpdateBeam()
        {
            if (beamFrame == Time.frameCount) return;
            beamFrame = Time.frameCount;
            var rotation = Quaternion.identity;
            beamVisible = LynxCameraController.MainCamera != null && TryGetPose(out beamStart, out rotation);
            if (!beamVisible) return;
            if (pointerMask == 0) pointerMask = PointerMask();
            var forward = rotation * Vector3.forward;
            beamHit = Physics.Raycast(beamStart, forward, out var hit, BeamRange * VrCamera.WorldScale, pointerMask, QueryTriggerInteraction.Ignore);
            beamEnd = beamHit ? hit.point : beamStart + forward * BeamMiss * VrCamera.WorldScale;
            beamTarget = beamHit ? $"'{hit.collider.name}' (layer {LayerMask.LayerToName(hit.collider.gameObject.layer)}) at {hit.distance:0.00} m" : "nothing";
        }

        // Whether the hand points at anything within the beam's range (VrToolInput: B held without a target recalls the tethers).
        public static bool PointsAtSomething()
        {
            if (!Active) return false;
            if (pointerMask == 0) pointerMask = PointerMask();
            return Physics.Raycast(aim.transform.position, aim.transform.forward, BeamRange * VrCamera.WorldScale, pointerMask, QueryTriggerInteraction.Ignore);
        }

        // Everything a ray hits by default, except the player's own layers and the UI.
        private static int PointerMask()
        {
            var mask = Physics.DefaultRaycastLayers & ~(1 << VrUi.PassLayer);
            var skipped = new List<string>();
            for (var layer = 0; layer < 32; layer++)
            {
                var name = LayerMask.LayerToName(layer);
                if (name.IndexOf("Player", System.StringComparison.OrdinalIgnoreCase) < 0 && name != "UI") continue;
                mask &= ~(1 << layer);
                skipped.Add($"{layer} {name}");
            }
            Plugin.Log.LogInfo($"Aim beam ignores layers: {string.Join(", ", skipped)}");
            return mask;
        }

        private static void CreateMeshes()
        {
            // Bright near the hand, fading towards the target.
            var near = new Color(0.55f, 0.95f, 1f, 0.55f);
            var far = new Color(0.55f, 0.95f, 1f, 0.12f);
            beamMesh = new Mesh { name = "ShipbreakerVR Aim Beam" };
            beamMesh.vertices = new[] { new Vector3(-0.5f, 0f, 0f), new Vector3(0.5f, 0f, 0f), new Vector3(0.5f, 1f, 0f), new Vector3(-0.5f, 1f, 0f) };
            beamMesh.colors = new[] { near, near, far, far };
            beamMesh.triangles = new[] { 0, 2, 1, 0, 3, 2 };

            const int segments = 20;
            var vertices = new Vector3[segments + 1];
            var colors = new Color[segments + 1];
            var triangles = new int[segments * 3];
            colors[0] = Color.white;
            for (var i = 0; i < segments; i++)
            {
                var angle = i * Mathf.PI * 2f / segments;
                vertices[i + 1] = new Vector3(Mathf.Cos(angle) * 0.5f, Mathf.Sin(angle) * 0.5f, 0f);
                colors[i + 1] = Color.white;
                triangles[i * 3] = 0;
                triangles[i * 3 + 1] = 1 + (i + 1) % segments;
                triangles[i * 3 + 2] = 1 + i;
            }
            dotMesh = new Mesh { name = "ShipbreakerVR Aim Dot", vertices = vertices, colors = colors, triangles = triangles };

            beamMaterial = Overlay(Color.white);
            dotMaterial = Overlay(new Color(0.75f, 1f, 1f, 0.9f));
        }

        private static Material Overlay(Color color)
        {
            var material = new Material(Shader.Find("UI/Default")) { color = color };
            material.SetInt("unity_GUIZTestMode", (int)CompareFunction.Always);
            return material;
        }

        // On unload (hot reload): the aim camera and the pointer's meshes and materials go with the old instance.
        public static void Release()
        {
            if (aim != null) Object.Destroy(aim.gameObject);
            foreach (var o in new Object[] { beamMesh, dotMesh, beamMaterial, dotMaterial })
                if (o != null) Object.Destroy(o);
            aim = null;
            beamMesh = dotMesh = null;
            beamMaterial = dotMaterial = null;
        }

        // For the F8 dump.
        public static string Describe(Transform head)
        {
            var text = $"aim with hand: option {Plugin.AimWithHand.Value}, active {Active}";
            if (active) text += $", hand at {VrDiagnostics.Direction(head, aim.transform.position)} {Vector3.Distance(head.position, aim.transform.position):0.00} m, pointing {VrDiagnostics.Direction(head, head.position + aim.transform.forward)}, beam on {beamTarget}";
            return text + CutterModel();
        }

        // Where the game keeps the cutter's model, for moving the tools into the hand later.
        private static string CutterModel()
        {
            var fx = Object.FindObjectOfType<CuttingFXController>();
            if (fx == null) return "";
            var model = Traverse.Create(fx).Field("m_CuttingTool").GetValue<Transform>();
            if (model == null) return "";
            var chain = new List<string>();
            for (var t = model; t != null; t = t.parent)
                chain.Add($"'{t.name}' (local {t.localPosition}, layer {t.gameObject.layer})");
            return $"; cutter model: {string.Join(" < ", chain)}";
        }
    }

    // Puts the aim camera in the place of LynxCameraController.MainCamera and MainCameraTransform in the code that aims
    // the tools, grabbing and interacting. A transpiler on the callers, because the getters are small enough for Mono to
    // inline. Pushing off from surfaces keeps the head's direction: it moves the body.
    [HarmonyPatch]
    internal static class AimPatch
    {
        private static readonly string[] Types =
        {
            "BBI.Unity.Game.CuttingController", "BBI.Unity.Game.CuttingFXController", "TetherController", "GrapplingHook",
            "BBI.Unity.Game.MagnetController", "BBI.Unity.Game.Scanner", "BBI.Unity.Game.ThrowController",
            "BBI.Unity.Game.DemoChargeController", "BBI.Unity.Game.RaycastSystem",
        };

        private static readonly MethodInfo MainCamera = AccessTools.PropertyGetter(typeof(LynxCameraController), nameof(LynxCameraController.MainCamera));
        private static readonly MethodInfo MainCameraTransform = AccessTools.PropertyGetter(typeof(LynxCameraController), nameof(LynxCameraController.MainCameraTransform));
        private static readonly MethodInfo AimCamera = AccessTools.PropertyGetter(typeof(VrAim), nameof(VrAim.Camera));
        private static readonly MethodInfo AimCameraTransform = AccessTools.PropertyGetter(typeof(VrAim), nameof(VrAim.CameraTransform));
        private static readonly MethodInfo GetTransform = AccessTools.PropertyGetter(typeof(Component), nameof(Component.transform));
        private static readonly MethodInfo CutOrigin = AccessTools.Method(typeof(VrAim), nameof(VrAim.CutOrigin));

        private static IEnumerable<MethodBase> TargetMethods()
        {
            foreach (var name in Types)
            {
                var type = AccessTools.TypeByName(name);
                if (type == null)
                {
                    Plugin.Log.LogWarning($"Aim: type {name} not found");
                    continue;
                }
                foreach (var nested in new[] { type }.Concat(type.GetNestedTypes(AccessTools.all)))
                    foreach (var method in AccessTools.GetDeclaredMethods(nested))
                        if (!method.IsAbstract && !method.ContainsGenericParameters && method.GetMethodBody() != null && UsesMainCamera(method))
                            yield return method;
            }
        }

        private static bool UsesMainCamera(MethodBase method)
        {
            try
            {
                return PatchProcessor.ReadMethodBody(method).Any(i => i.Value is MethodInfo called && (called == MainCamera || called == MainCameraTransform));
            }
            catch (System.Exception e)
            {
                Plugin.Log.LogWarning($"Aim: could not read {method.DeclaringType?.Name}.{method.Name}: {e.Message}");
                return false;
            }
        }

        private static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> instructions, MethodBase original)
        {
            var list = instructions.ToList();
            var replaced = 0;
            // In StartCutLine the line's ends lie along the aim rays, measured from the tool's own position (this.transform);
            // they have to start at the hand instead.
            var cutLine = original.Name == "StartCutLine";
            for (var i = 0; i < list.Count; i++)
            {
                var instruction = list[i];
                if (instruction.Calls(MainCamera)) instruction.operand = AimCamera;
                else if (instruction.Calls(MainCameraTransform)) instruction.operand = AimCameraTransform;
                else if (cutLine && instruction.Calls(GetTransform) && i > 0 && list[i - 1].opcode == OpCodes.Ldarg_0) instruction.operand = CutOrigin;
                else continue;
                instruction.opcode = OpCodes.Call;
                replaced++;
            }
            Plugin.Log.LogInfo($"Aim: {original.DeclaringType?.Name}.{original.Name}: {replaced} camera call(s) redirected to the hand");
            return list;
        }
    }
}
