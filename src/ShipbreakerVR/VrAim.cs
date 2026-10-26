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
    // Aiming with the hand. Every tool aims through the game's main camera: rays through screen points
    // (ScreenPointToRay), its position and forward, and its matrices for the ray grids of grabbing and the grapple.
    // A camera that never renders sits on the muzzle of the tool in the hand that holds it, the right one unless the left
    // one drew it (VrToolInput.ToolNode; the hand's aim pose without a tool model), pointing where the controller points,
    // with the main camera's lens, and takes the main camera's place in the tools' code (AimPatch). The screen-space
    // patterns (the cutter's line, the grapple's search grid) then fan out from the hand as they did from the eye, and
    // rolling the hand rolls the cutter's line.
    // A thin laser from the muzzle to what it hits shows where the hand points. The game's crosshair in the middle of the
    // view is hidden meanwhile (VrCrosshair). With an empty hand there is nothing to aim, as Marc found: no laser
    // then, and interacting aims at the middle of the view as in the flat game, where the game's dot marks it (Aiming).
    // The buttons of the tool hand's controller work the tools while aiming with the hand (VrToolInput).
    // The tool models go into the hand (VrToolModels).
    internal static class VrAim
    {
        private const float BeamWidth = 0.002f;
        private const float BeamMiss = 5f;
        private const float BeamRange = 100f;
        private static readonly Color LaserColor = new Color(0.55f, 0.95f, 1f);

        // How far in front of the hand a thrown demo charge starts, clear of the player's collider.
        private const float ThrowAhead = 0.2f;

        private static readonly InputFeatureUsage<Vector3> PointerPosition = new InputFeatureUsage<Vector3>("PointerPosition");
        private static readonly InputFeatureUsage<Quaternion> PointerRotation = new InputFeatureUsage<Quaternion>("PointerRotation");

        // The controller that holds the tool (VrToolInput.ToolNode), the right one unless the left hand drew the tool.
        private static InputDevice hand;
        private static XRNode handNode = XRNode.RightHand;
        private static Camera aim;
        private static Transform throwOrigin;
        private static int updatedFrame = -1;
        private static bool active;
        private static bool withTool;
        private static bool loggedPose;

        private static int beamFrame = -1;
        private static bool beamVisible;
        private static bool beamHit;
        private static Vector3 beamStart;
        private static Vector3 beamEnd;
        private static bool secondBeamVisible;
        private static Vector3 secondBeamStart;
        private static Vector3 secondBeamEnd;
        private static string beamTarget = "-";
        private static int pointerMask;
        private static Mesh beamMesh;
        private static Material beamMaterial;

        // True while the tools aim from the hand: VR in gameplay, the option on and the tool hand's controller tracked.
        public static bool Active
        {
            get
            {
                Update();
                return active;
            }
        }

        // True while a tool in the hand aims (Active, with a tool drawn): the game's code then aims through the aim camera,
        // and the laser shows. With an empty hand it aims through the main camera, at the middle of the view.
        public static bool Aiming
        {
            get
            {
                Update();
                return active && withTool;
            }
        }

        // What the patched tool code uses instead of LynxCameraController.MainCamera and MainCameraTransform.
        public static Camera Camera => Aiming ? aim : LynxCameraController.MainCamera;

        public static Transform CameraTransform => Aiming ? aim.transform : LynxCameraController.MainCameraTransform;

        // The same for the code of one tool family (VrDualTools.Family): the second tool's own aim camera while that
        // family's tool is in the second hand.
        public static Camera CameraFor(int family)
        {
            var second = VrDualTools.Routes(family) && Active ? VrDualTools.AimCamera : null;
            return second != null ? second : Camera;
        }

        public static Transform CameraTransformFor(int family)
        {
            var second = VrDualTools.Routes(family) && Active ? VrDualTools.AimCamera : null;
            return second != null ? second.transform : CameraTransform;
        }

        // Where the cutter's line starts in CuttingFXController.StartCutLine (the tool's own transform in the flat game).
        public static Transform CutOrigin(Component tool, int family)
        {
            var second = VrDualTools.Routes(family) && Active ? VrDualTools.AimCamera : null;
            return second != null ? second.transform : Aiming ? aim.transform : tool.transform;
        }

        // A thrown demo charge leaves from just in front of the hand (DemoChargeController.ThrowDemoCharge, ThrowOffset in
        // front of the head in the flat game) and flies where the hand points (DemoChargeAsset.SetComponentData, the
        // player's forward in the flat game). Placed charges are jointed to the ship, which drops that velocity.
        public static Transform ThrowOrigin(Transform offset)
        {
            if (!Aiming) return offset;
            if (throwOrigin == null)
            {
                throwOrigin = new GameObject("ShipbreakerVR Throw origin").transform;
                throwOrigin.SetParent(aim.transform, false);
                throwOrigin.localPosition = new Vector3(0f, 0f, ThrowAhead);
            }
            return throwOrigin;
        }

        public static Vector3 ThrowDirection(Transform player) => Aiming ? aim.transform.forward : player.forward;

        private static void Update()
        {
            if (updatedFrame == Time.frameCount) return;
            updatedFrame = Time.frameCount;
            active = false;
            var main = LynxCameraController.MainCamera;
            if (main == null || !TryGetAim(out var position, out var rotation)) return;

            if (aim == null)
            {
                var go = new GameObject("ShipbreakerVR Aim");
                Object.DontDestroyOnLoad(go);
                aim = go.AddComponent<Camera>();
                aim.enabled = false;
                aim.stereoTargetEye = StereoTargetEyeMask.None;
                Plugin.Log.LogInfo("Aim camera created");
            }
            aim.transform.SetPositionAndRotation(position, rotation);
            // The main camera's lens and screen rectangle, so screen points map to the same angles as from the eye.
            aim.fieldOfView = main.fieldOfView;
            aim.rect = main.rect;
            aim.aspect = main.aspect;
            aim.nearClipPlane = main.nearClipPlane;
            aim.farClipPlane = main.farClipPlane;
            withTool = VrToolModels.Equipped != EquipmentController.Equipment.None;
            active = true;
        }

        // Where the tools aim from: the muzzle of the tool in the hand (VrToolModels), along its barrel (where the hand points,
        // turned as the tool is turned in the hand), or the hand itself without a tool model (scanner, demo charges, empty
        // hand).
        private static bool TryGetAim(out Vector3 position, out Quaternion rotation)
        {
            if (!TryGetPose(out position, out rotation)) return false;
            if (hand.TryGetFeatureValue(CommonUsages.devicePosition, out var local)
                && VrToolModels.Muzzle(VrCamera.Instance.transform.parent.TransformPoint(local), rotation, out var muzzle, out var barrel))
            {
                position = muzzle;
                rotation = barrel;
            }
            return true;
        }

        // For holding the tool models (VrToolModels), right before rendering: where the hand holds (the grip pose's
        // position) and where it points (the aim pose's rotation), in the world.
        public static bool TryGetHand(out Vector3 grip, out Quaternion rotation)
        {
            grip = Vector3.zero;
            if (!TryGetPose(out _, out rotation)) return false;
            if (!hand.TryGetFeatureValue(CommonUsages.devicePosition, out var local)) return false;
            grip = VrCamera.Instance.transform.parent.TransformPoint(local);
            return true;
        }

        // The same for another controller (the second tool's, VrDualTools).
        public static bool TryGetHand(InputDevice device, out Vector3 grip, out Quaternion rotation)
        {
            grip = Vector3.zero;
            if (!TryGetPose(device, out _, out rotation)) return false;
            if (!device.TryGetFeatureValue(CommonUsages.devicePosition, out var local)) return false;
            grip = VrCamera.Instance.transform.parent.TransformPoint(local);
            return true;
        }

        public static bool TryGetPose(InputDevice device, out Vector3 position, out Quaternion rotation)
        {
            position = Vector3.zero;
            rotation = Quaternion.identity;
            if (!Usable() || !TryGetDevicePose(device, out var local, out var localRotation, out _)) return false;
            var rig = VrCamera.Instance.transform.parent;
            position = rig.TransformPoint(local);
            rotation = rig.rotation * localRotation;
            return true;
        }

        // The tool hand's aim pose in the world (it points where the controller points; the grip pose runs along
        // the handle). Poses are in tracking space, i.e. relative to the rig.
        private static bool TryGetPose(out Vector3 position, out Quaternion rotation)
        {
            position = Vector3.zero;
            rotation = Quaternion.identity;
            if (!TryGetPoseLocal(out var local, out var localRotation)) return false;
            var rig = VrCamera.Instance.transform.parent;
            position = rig.TransformPoint(local);
            rotation = rig.rotation * localRotation;
            return true;
        }

        // The grip pose's position and the aim pose's rotation in tracking space (VrToolModels.PoseArm, which runs before
        // the rig has moved for the frame).
        public static bool TryGetHandLocal(out Vector3 grip, out Quaternion rotation)
        {
            grip = Vector3.zero;
            return TryGetPoseLocal(out _, out rotation) && hand.TryGetFeatureValue(CommonUsages.devicePosition, out grip);
        }

        private static bool TryGetPoseLocal(out Vector3 local, out Quaternion localRotation)
        {
            local = Vector3.zero;
            localRotation = Quaternion.identity;
            if (!Usable()) return false;
            var node = VrToolInput.ToolNode;
            if (node != handNode || !hand.isValid)
            {
                if (node != handNode) loggedPose = false;
                handNode = node;
                hand = InputDevices.GetDeviceAtXRNode(node);
            }
            if (!TryGetDevicePose(hand, out local, out localRotation, out var pointer)) return false;
            if (!loggedPose)
            {
                loggedPose = true;
                Plugin.Log.LogInfo($"Aiming with the {(handNode == XRNode.LeftHand ? "left" : "right")} hand ('{hand.name}', {(pointer ? "aim pose" : "grip pose, no aim pose")})");
            }
            return true;
        }

        // VR in gameplay with the option on.
        private static bool Usable()
        {
            var vr = VrCamera.Instance;
            if (!VrXr.IsRunning || !Plugin.AimWithHand.Value || vr == null || vr.FollowsHab) return false;
            return GameSession.CurrentGameState == GameSession.GameState.Gameplay;
        }

        // A tracked controller's aim pose (or its grip pose without one) in tracking space.
        private static bool TryGetDevicePose(InputDevice device, out Vector3 local, out Quaternion localRotation, out bool pointer)
        {
            local = Vector3.zero;
            localRotation = Quaternion.identity;
            pointer = false;
            if (!device.isValid) return false;
            if (device.TryGetFeatureValue(CommonUsages.isTracked, out var tracked) && !tracked) return false;
            pointer = device.TryGetFeatureValue(PointerPosition, out local) && device.TryGetFeatureValue(PointerRotation, out localRotation);
            return pointer || (device.TryGetFeatureValue(CommonUsages.devicePosition, out local) && device.TryGetFeatureValue(CommonUsages.deviceRotation, out localRotation));
        }

        // Called from our UI pass for each eye, before the UI: the laser from the hand to what it hits. Drawn over
        // everything (the pass has no depth), which is fine since the laser ends at the first thing it meets.
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

            DrawBeam(cmd, eye, scale, beamStart, beamEnd);
            if (secondBeamVisible) DrawBeam(cmd, eye, scale, secondBeamStart, secondBeamEnd);
            context.renderContext.ExecuteCommandBuffer(cmd);
            cmd.Clear();
            context.renderContext.SetupCameraProperties(context.hdCamera.camera, true);
        }

        // The beam is a strip along its direction, turned to face the eye.
        private static void DrawBeam(CommandBuffer cmd, Vector3 eye, float scale, Vector3 start, Vector3 end)
        {
            var along = end - start;
            var length = along.magnitude;
            if (length <= 0.01f) return;
            var direction = along / length;
            var toEye = Vector3.ProjectOnPlane(eye - start, direction);
            if (toEye.sqrMagnitude < 1e-8f) toEye = Vector3.ProjectOnPlane(Vector3.up, direction);
            var matrix = Matrix4x4.TRS(start, Quaternion.LookRotation(toEye, direction), new Vector3(BeamWidth * scale, length, 1f));
            cmd.DrawMesh(beamMesh, matrix, beamMaterial, 0, 0);
        }

        // Once per frame at render time, with the latest hand pose and the rig where it is drawn; a second beam from the
        // second tool (VrDualTools).
        private static void UpdateBeam()
        {
            if (beamFrame == Time.frameCount) return;
            beamFrame = Time.frameCount;
            var rotation = Quaternion.identity;
            var secondRotation = Quaternion.identity;
            secondBeamVisible = Active && VrDualTools.TryGetAim(out secondBeamStart, out secondRotation);
            if (secondBeamVisible) Cast(secondBeamStart, secondRotation, out secondBeamEnd, out _);
            beamVisible = Aiming && TryGetAim(out beamStart, out rotation);
            if (!beamVisible)
            {
                beamVisible = secondBeamVisible;
                beamStart = beamEnd = secondBeamStart;
                return;
            }
            beamHit = Cast(beamStart, rotation, out beamEnd, out var hit);
            beamTarget = beamHit ? $"'{hit.collider.name}' (layer {LayerMask.LayerToName(hit.collider.gameObject.layer)}) at {hit.distance:0.00} m" : "nothing";
        }

        private static bool Cast(Vector3 start, Quaternion rotation, out Vector3 end, out RaycastHit hit)
        {
            if (pointerMask == 0) pointerMask = PointerMask();
            var forward = rotation * Vector3.forward;
            var hits = Physics.Raycast(start, forward, out hit, BeamRange * VrCamera.WorldScale, pointerMask, QueryTriggerInteraction.Ignore);
            end = hits ? hit.point : start + forward * BeamMiss * VrCamera.WorldScale;
            return hits;
        }

        // Whether the hand points at anything within the beam's range, with an empty hand the middle of the view (VrToolInput:
        // B held without a target recalls the tethers); second: the second tool's hand (VrDualTools).
        public static bool PointsAtSomething(bool second = false)
        {
            var from = second && VrDualTools.AimCamera != null ? VrDualTools.AimCamera.transform : CameraTransform;
            if (!Active || from == null) return false;
            if (pointerMask == 0) pointerMask = PointerMask();
            return Physics.Raycast(from.position, from.forward, BeamRange * VrCamera.WorldScale, pointerMask, QueryTriggerInteraction.Ignore);
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
            // Bright near the hand, fading a little towards the target; tinted by the material.
            var near = new Color(1f, 1f, 1f, 0.9f);
            var far = new Color(1f, 1f, 1f, 0.4f);
            beamMesh = new Mesh { name = "ShipbreakerVR Aim Beam" };
            beamMesh.vertices = new[] { new Vector3(-0.5f, 0f, 0f), new Vector3(0.5f, 0f, 0f), new Vector3(0.5f, 1f, 0f), new Vector3(-0.5f, 1f, 0f) };
            beamMesh.colors = new[] { near, near, far, far };
            beamMesh.triangles = new[] { 0, 2, 1, 0, 3, 2 };
            beamMaterial = Overlay(LaserColor);
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
            foreach (var o in new Object[] { beamMesh, beamMaterial })
                if (o != null) Object.Destroy(o);
            aim = null;
            throwOrigin = null;
            beamMesh = null;
            beamMaterial = null;
        }

        // For the F8 dump.
        public static string Describe(Transform head)
        {
            var text = $"aim with hand: option {Plugin.AimWithHand.Value}, active {Active}, tool {withTool}";
            if (active) text += $", hand at {VrDiagnostics.Direction(head, aim.transform.position)} {Vector3.Distance(head.position, aim.transform.position):0.00} m, pointing {VrDiagnostics.Direction(head, head.position + aim.transform.forward)}, beam on {beamTarget}";
            return text + "; " + VrDualTools.Describe() + CutterModel();
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
            "BBI.Unity.Game.DemoChargeController", "BBI.Unity.Game.RaycastSystem", "BBI.Unity.Game.ScalpelController",
            "BBI.Unity.Game.ScalpelFXController", "BBI.Unity.Game.ScalpelTarget",
        };

        // Methods that aim without the main camera: where thrown demo charges start and fly.
        private static readonly (string Type, string Method)[] Extra =
        {
            ("BBI.Unity.Game.DemoChargeController", "ThrowDemoCharge"),
            ("BBI.Unity.Game.DemoChargeAsset", "SetComponentData"),
        };

        private static readonly MethodInfo MainCamera = AccessTools.PropertyGetter(typeof(LynxCameraController), nameof(LynxCameraController.MainCamera));
        private static readonly MethodInfo MainCameraTransform = AccessTools.PropertyGetter(typeof(LynxCameraController), nameof(LynxCameraController.MainCameraTransform));
        private static readonly MethodInfo AimCamera = AccessTools.Method(typeof(VrAim), nameof(VrAim.CameraFor));
        private static readonly MethodInfo AimCameraTransform = AccessTools.Method(typeof(VrAim), nameof(VrAim.CameraTransformFor));
        private static readonly MethodInfo GetTransform = AccessTools.PropertyGetter(typeof(Component), nameof(Component.transform));
        private static readonly MethodInfo CutOrigin = AccessTools.Method(typeof(VrAim), nameof(VrAim.CutOrigin));
        private static readonly MethodInfo ThrowOrigin = AccessTools.Method(typeof(VrAim), nameof(VrAim.ThrowOrigin));
        private static readonly MethodInfo ThrowDirection = AccessTools.Method(typeof(VrAim), nameof(VrAim.ThrowDirection));
        private static readonly MethodInfo GetForward = AccessTools.PropertyGetter(typeof(Transform), nameof(Transform.forward));

        private static IEnumerable<MethodBase> TargetMethods()
        {
            var methods = new HashSet<MethodBase>(MainCameraUsers());
            foreach (var (type, name) in Extra)
            {
                var method = AccessTools.Method(AccessTools.TypeByName(type), name);
                if (method == null) Plugin.Log.LogWarning($"Aim: {type}.{name} not found");
                else methods.Add(method);
            }
            return methods;
        }

        private static IEnumerable<MethodBase> MainCameraUsers()
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
            var thrown = original.Name == "ThrowDemoCharge";
            var throwSpeed = original.Name == "SetComponentData";
            for (var i = 0; i < list.Count; i++)
            {
                var instruction = list[i];
                // The camera calls and the cut origin take the family of the code (VrDualTools), loaded right before them.
                var redirect = instruction.Calls(MainCamera) ? AimCamera
                    : instruction.Calls(MainCameraTransform) ? AimCameraTransform
                    : cutLine && instruction.Calls(GetTransform) && i > 0 && list[i - 1].opcode == OpCodes.Ldarg_0 ? CutOrigin
                    : null;
                if (redirect != null)
                {
                    var load = VrDualTools.LoadFamily(instruction, original).ToList();
                    list.InsertRange(i, load);
                    i += load.Count;
                    instruction.operand = redirect;
                }
                else if (throwSpeed && instruction.Calls(GetForward)) instruction.operand = ThrowDirection;
                else if (thrown && instruction.operand is MethodInfo m && m.Name == "get_ThrowOffset")
                {
                    // Pass the value through the hand's version of it.
                    list.Insert(++i, new CodeInstruction(OpCodes.Call, ThrowOrigin));
                    replaced++;
                    continue;
                }
                else continue;
                instruction.opcode = OpCodes.Call;
                replaced++;
            }
            Plugin.Log.LogInfo($"Aim: {original.DeclaringType?.Name}.{original.Name}: {replaced} camera call(s) redirected to the hand");
            return list;
        }
    }
}
