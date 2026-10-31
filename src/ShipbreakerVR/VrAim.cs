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
    // one drew it (VrHand.GameToolHand; the hand's aim pose without a tool model), pointing where the controller points,
    // with the main camera's lens, and takes the main camera's place in the tools' code (AimPatch). The screen-space
    // patterns (the cutter's line, the grapple's search grid) then fan out from the hand as they did from the eye, and
    // rolling the hand rolls the cutter's line.
    // A thin laser from the muzzle to what it hits shows where the hand points. The game's crosshair in the middle of the
    // view is hidden meanwhile (VrCrosshair). With an empty hand there is nothing to aim, as Marc found: no laser
    // then, and interacting aims at the middle of the view as in the flat game, where the game's dot marks it (Aiming).
    // Where the laser of the grapple or the cutter hits, a bright dot marks the point, like a laser pointer's; on the
    // Splitsaw smaller dots mark the line it will cut, only where it meets a part the cut reaches (the game's edge
    // detection points, CuttingController.EdgeDetectionPoints), as Marc asked.
    // In the grapple's tether mode (VrTetherMode) a ring marks where it meets something, and while
    // a tether is being placed a line runs from where it started to where the laser ends; the tether the laser points at,
    // which A lets go of, is traced in orange.
    // The buttons of the tool hand's controller work the tools while aiming with the hand (VrToolInput).
    // The tool models go into the hand (VrToolModels).
    internal static class VrAim
    {
        static VrAim() => VrSession.OnUnload("aim camera", Release);

        private const float BeamWidth = 0.003f;
        private const float BeamMiss = 5f;
        private const float BeamRange = 100f;
        private static readonly Color LaserColor = new Color(2.5f, 0.25f, 0.15f);
        // The bright middle of the dots, so that they stay red too.
        private static readonly Color DotCoreColor = new Color(2.5f, 1.2f, 1f);
        // The grapple's laser and dots, in both its modes; the ring and the traced tether stay orange.
        private static readonly Color GrappleColor = new Color(0.9f, 2.0f, 2.5f);
        private static readonly Color GrappleCoreColor = new Color(2.2f, 2.5f, 2.5f);
        private static readonly Color TetherColor = new Color(1f, 0.6f, 0.15f);
        // The ring at the end of the tether mode's laser: its outer radius as a share of the distance from the eye, so it
        // looks the same size near and far, and its inner radius as a share of the outer one.
        private const float RingSize = 0.008f;
        private const float RingInner = 0.6f;
        private const int RingSegments = 24;
        // The traced tether is this many times as wide as the laser.
        private const float TraceWidth = 4f;
        // The dot where the laser hits, like a laser pointer's on a surface: the radius of its glow and of its white core
        // in meters (times the world scale), so it gets smaller with distance, as Marc wanted; at least these shares of
        // the distance from the eye, so it stays visible far away. The dots on the Splitsaw's line look the same, as Marc
        // wanted. All are drawn this share of the way towards the eye, so the surface does not cut into them but the
        // tool in the hand still hides them.
        private const float DotSize = 0.008f;
        private const float DotCore = 0.003f;
        private const float DotMinSize = 0.0012f;
        private const float DotMinCore = 0.0005f;
        // Extra dots on the Splitsaw's line between two neighboring edge points of the game, so the dots sit closer
        // together than the game's points (Marc wanted them finer and closer).
        private const int LineDotsBetween = 2;
        private const float DotLift = 0.03f;
        private const int DotSegments = 16;

        // How far in front of the hand a thrown demo charge starts, clear of the player's collider.
        private const float ThrowAhead = 0.2f;

        // The hand that held the tool when the aim pose was last logged.
        private static XRNode handNode = XRNode.RightHand;
        private static Camera aim;
        private static Transform throwOrigin;
        private static int updatedFrame = -1;
        private static bool active;
        private static bool withTool;
        private static bool loggedPose;
        private static bool loggedDepth;

        private static int beamFrame = -1;
        private static bool beamVisible;
        private static bool beamHit;
        private static Vector3 beamStart;
        private static Vector3 beamEnd;
        private static bool secondBeamVisible;
        private static bool secondBeamHit;
        // In the tether mode: the tether each laser points at (VrTetherMode.Targeted), traced in orange.
        private static Tether beamTether;
        private static Tether secondBeamTether;
        private static Vector3 secondBeamStart;
        private static Vector3 secondBeamEnd;
        private static bool beamDot;
        private static bool secondBeamDot;
        // The Splitsaw's line where it will cut.
        private static readonly List<Vector3> cutPoints = new List<Vector3>();
        private static string beamTarget = "-";
        private static int pointerMask;
        private static Mesh beamMesh;
        // The lasers are hidden by what lies in front of them, the tool in the hand above all (depth tested); the tether
        // mode's ring and lines are drawn over everything, since they lie on or along the surfaces they mark.
        private static Material beamMaterial;
        private static Material grappleMaterial;
        private static Material grappleCoreMaterial;
        private static Mesh ringMesh;
        private static Material tetherMaterial;
        private static Mesh dotMesh;
        private static Material coreMaterial;

        private static readonly AccessTools.FieldRef<CuttingToolController, CuttingController> CuttingControllerOf =
            AccessTools.FieldRefAccess<CuttingToolController, CuttingController>("m_CuttingController");
        // The parts the Splitsaw's cut reaches (CuttingController.mTargetBuffer, refreshed with the edge points).
        private static readonly AccessTools.FieldRef<CuttingController, List<CuttingController.CuttableInfo>> CutTargets =
            AccessTools.FieldRefAccess<CuttingController, List<CuttingController.CuttableInfo>>("mTargetBuffer");
        private static readonly HashSet<StructurePart> cutTargets = new HashSet<StructurePart>();
        private static bool loggedCutLine;
        private static string cutLineInfo = "-";

        // True while the tools aim from the hand: VR in gameplay, the option on and the tool hand's controller tracked.
        // Settled once per frame (VrFrame).
        public static bool Active => active;

        // True while a tool in the hand aims (Active, with a tool drawn): the game's code then aims through the aim camera,
        // and the laser shows. With an empty hand it aims through the main camera, at the middle of the view.
        public static bool Aiming => active && withTool;

        // Whether the tools can aim from that hand now (VrToolInput, before it settles the tool hand for the frame).
        public static bool CanAim(XRNode node) =>
            Usable() && LynxCameraController.MainCamera != null && VrInput.Of(node).TryGetAimPose(out _, out _, out _);

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

        // From VrFrame, once per frame, after VrToolInput settled the tool hand.
        public static void Update()
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
            // The game's hand keeps naming the scanner after it was put away; the equipment knows it is gone.
            var equipment = GameRefs.Equipment;
            withTool = VrToolModels.Equipped != EquipmentController.Equipment.None && (equipment == null || !equipment.IsNothingEquipped);
            active = true;
        }

        // Where the tools aim from: the muzzle of the tool in the hand (VrToolModels), along its barrel (where the hand points,
        // turned as the tool is turned in the hand), or the hand itself without a tool model (scanner, demo charges, empty
        // hand).
        private static bool TryGetAim(out Vector3 position, out Quaternion rotation)
        {
            if (!TryGetPose(out position, out rotation)) return false;
            if (Tool.TryGetPosition(out var local)
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
            if (!Tool.TryGetPosition(out var local)) return false;
            grip = VrCamera.Instance.transform.parent.TransformPoint(local);
            return true;
        }

        // The same for another controller (the second tool's, VrDualTools).
        public static bool TryGetHand(VrInput.Hand hand, out Vector3 grip, out Quaternion rotation)
        {
            grip = Vector3.zero;
            if (!TryGetPose(hand, out _, out rotation)) return false;
            if (!hand.TryGetPosition(out var local)) return false;
            grip = VrCamera.Instance.transform.parent.TransformPoint(local);
            return true;
        }

        public static bool TryGetPose(VrInput.Hand hand, out Vector3 position, out Quaternion rotation)
        {
            position = Vector3.zero;
            rotation = Quaternion.identity;
            if (!Usable() || !hand.TryGetAimPose(out var local, out var localRotation, out _)) return false;
            var rig = VrCamera.Instance.transform.parent;
            position = rig.TransformPoint(local);
            rotation = rig.rotation * VrToolSteady.Apply(hand, ToolOf(hand), localRotation);
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
            return TryGetPoseLocal(out _, out rotation) && Tool.TryGetPosition(out grip);
        }

        // The controller of the hand that holds the tool (VrHand.GameToolHand), the right one unless the left hand drew it.
        private static VrInput.Hand Tool => VrHand.GameToolHand.Input;

        private static EquipmentController.Equipment ToolOf(VrInput.Hand hand) =>
            hand == Tool ? VrToolModels.Equipped : hand.Node == VrDualTools.SecondNode ? VrDualTools.Second : EquipmentController.Equipment.None;

        private static bool TryGetPoseLocal(out Vector3 local, out Quaternion localRotation)
        {
            local = Vector3.zero;
            localRotation = Quaternion.identity;
            if (!Usable()) return false;
            var hand = Tool;
            if (hand.Node != handNode)
            {
                loggedPose = false;
                handNode = hand.Node;
            }
            if (!hand.TryGetAimPose(out local, out localRotation, out var pointer)) return false;
            localRotation = VrToolSteady.Apply(hand, VrToolModels.Equipped, localRotation);
            if (!loggedPose)
            {
                loggedPose = true;
                Plugin.Log.LogInfo($"Aiming with the {hand.Name} hand ('{hand.Device.name}', {(pointer ? "aim pose" : "grip pose, no aim pose")})");
            }
            return true;
        }

        // VR in gameplay with the option on.
        private static bool Usable()
        {
            var vr = VrCamera.Instance;
            if (!VrXr.IsRunning || !VrSettings.AimWithHand.Value || !PatchReport.HandAimWorks || vr == null || vr.FollowsHab) return false;
            return GameSession.CurrentGameState == GameSession.GameState.Gameplay;
        }

        // Called from our UI pass for each eye, before the UI: the laser from the hand to what it hits. Tested against the
        // scene's depth, so the tool in the hand (and anything else in front) hides it, as Marc asked (issue #39).
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
            cmd.SetRenderTarget(context.cameraColorBuffer, context.cameraDepthBuffer, 0, CubemapFace.Unknown, -1);
            cmd.SetViewport(new Rect(0, 0, context.hdCamera.actualWidth, context.hdCamera.actualHeight));
            cmd.SetViewProjectionMatrices(view, proj);
            if (!loggedDepth)
            {
                loggedDepth = true;
                Plugin.Log.LogInfo($"Aim laser depth tested: depth buffer {(context.cameraDepthBuffer != null && context.cameraDepthBuffer.rt != null ? $"{context.cameraDepthBuffer.rt.width}x{context.cameraDepthBuffer.rt.height}" : "missing")}, color {(context.cameraColorBuffer != null && context.cameraColorBuffer.rt != null ? $"{context.cameraColorBuffer.rt.width}x{context.cameraColorBuffer.rt.height}" : "missing")}");
            }

            var tethers = VrTetherMode.Applies(VrToolModels.Equipped);
            var secondTethers = VrTetherMode.Applies(VrDualTools.Second);
            // The demo charges show where they go with the game's marker, they need no laser.
            var tool = VrToolModels.Equipped;
            var secondTool = VrDualTools.Second;
            if (tool != EquipmentController.Equipment.DemoCharge)
                DrawBeam(cmd, eye, scale, beamStart, beamEnd, beamHit, tethers, handNode, beamTether, tool);
            if (secondBeamVisible && secondTool != EquipmentController.Equipment.DemoCharge)
                DrawBeam(cmd, eye, scale, secondBeamStart, secondBeamEnd, secondBeamHit, secondTethers, VrDualTools.SecondNode, secondBeamTether, secondTool);
            foreach (var point in cutPoints) DrawImpact(cmd, eye, scale, point, beamMaterial, coreMaterial);
            if (beamDot) DrawImpact(cmd, eye, scale, beamEnd, Beam(tool), Core(tool));
            if (secondBeamDot) DrawImpact(cmd, eye, scale, secondBeamEnd, Beam(secondTool), Core(secondTool));
            context.renderContext.ExecuteCommandBuffer(cmd);
            cmd.Clear();
            context.renderContext.SetupCameraProperties(context.hdCamera.camera, true);
        }

        // One tool's laser; in the tether mode orange, with the ring where it hits, the line of the tether being placed by
        // that hand and the tether it points at traced.
        private static void DrawBeam(CommandBuffer cmd, Vector3 eye, float scale, Vector3 start, Vector3 end, bool hit, bool tethers, XRNode node, Tether target, EquipmentController.Equipment tool)
        {
            DrawLine(cmd, eye, scale, start, end, Beam(tool));
            if (!tethers) return;
            var material = tetherMaterial;
            if (target != null) DrawLine(cmd, eye, scale * TraceWidth, target.WorldStartAttachPoint, target.WorldEndAttachPoint, material);
            if (VrTetherMode.TryGetStart(node, out var tetherStart)) DrawLine(cmd, eye, scale, tetherStart, end, material);
            if (!hit) return;
            var size = RingSize * Vector3.Distance(eye, end);
            cmd.DrawMesh(ringMesh, Matrix4x4.TRS(end, Quaternion.LookRotation(end - eye), Vector3.one * size), material, 0, 0);
        }

        // A laser's dot: a glow in its color with a white core, of a size in the world.
        private static Material Beam(EquipmentController.Equipment tool) =>
            tool == EquipmentController.Equipment.GrappleHook ? grappleMaterial : beamMaterial;

        private static Material Core(EquipmentController.Equipment tool) =>
            tool == EquipmentController.Equipment.GrappleHook ? grappleCoreMaterial : coreMaterial;

        private static void DrawImpact(CommandBuffer cmd, Vector3 eye, float scale, Vector3 point, Material glow, Material core)
        {
            var distance = Vector3.Distance(eye, point);
            DrawDisc(cmd, eye, point, Mathf.Max(DotSize * scale, DotMinSize * distance), glow);
            DrawDisc(cmd, eye, point, Mathf.Max(DotCore * scale, DotMinCore * distance), core);
        }

        // A disc of that radius facing the eye.
        private static void DrawDisc(CommandBuffer cmd, Vector3 eye, Vector3 point, float radius, Material material)
        {
            var toEye = eye - point;
            if (toEye.sqrMagnitude <= 1e-4f) return;
            var matrix = Matrix4x4.TRS(point + toEye * DotLift, Quaternion.LookRotation(-toEye), Vector3.one * radius);
            cmd.DrawMesh(dotMesh, matrix, material, 0, 0);
        }

        // A line is a strip along its direction, turned to face the eye.
        private static void DrawLine(CommandBuffer cmd, Vector3 eye, float scale, Vector3 start, Vector3 end, Material material)
        {
            var along = end - start;
            var length = along.magnitude;
            if (length <= 0.01f) return;
            var direction = along / length;
            var toEye = Vector3.ProjectOnPlane(eye - start, direction);
            if (toEye.sqrMagnitude < 1e-8f) toEye = Vector3.ProjectOnPlane(Vector3.up, direction);
            var matrix = Matrix4x4.TRS(start, Quaternion.LookRotation(toEye, direction), new Vector3(BeamWidth * scale, length, 1f));
            cmd.DrawMesh(beamMesh, matrix, material, 0, 0);
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
            if (secondBeamVisible) secondBeamHit = Cast(secondBeamStart, secondRotation, out secondBeamEnd, out _);
            secondBeamTether = secondBeamVisible && VrTetherMode.Applies(VrDualTools.Second)
                ? VrTetherMode.Targeted(secondBeamStart, secondRotation * Vector3.forward) : null;
            secondBeamDot = secondBeamVisible && secondBeamHit && Dotted(VrDualTools.Second);
            cutPoints.Clear();
            if (secondBeamVisible && VrDualTools.Second == EquipmentController.Equipment.CuttingTool)
                CutLine(secondBeamStart, secondRotation);
            beamVisible = Aiming && TryGetAim(out beamStart, out rotation);
            if (!beamVisible)
            {
                beamVisible = secondBeamVisible;
                beamStart = beamEnd = secondBeamStart;
                beamHit = beamDot = false;
                beamTether = null;
                return;
            }
            beamHit = Cast(beamStart, rotation, out beamEnd, out var hit);
            beamDot = beamHit && Dotted(VrToolModels.Equipped);
            if (VrToolModels.Equipped == EquipmentController.Equipment.CuttingTool && VrDualTools.Second != EquipmentController.Equipment.CuttingTool)
                CutLine(beamStart, rotation);
            beamTether = VrTetherMode.Applies(VrToolModels.Equipped) ? VrTetherMode.Targeted(beamStart, rotation * Vector3.forward) : null;
            beamTarget = beamHit ? $"'{hit.collider.name}' (layer {LayerMask.LayerToName(hit.collider.gameObject.layer)}) at {hit.distance:0.00} m" : "nothing";
        }

        // The tools whose laser ends in a dot.
        private static bool Dotted(EquipmentController.Equipment tool) =>
            tool == EquipmentController.Equipment.GrappleHook || tool == EquipmentController.Equipment.CuttingTool;

        // The Splitsaw's line, from the cutter's muzzle along its barrel (start, rotation). The game looks for its edge
        // points along the cut line across the whole screen (CuttingController.EdgeDetectionPoints, Valid where they meet
        // a part the cut reaches); they lie too far apart for a fine line, and a small part may hold only one. So each
        // stretch between two neighboring points of which one is Valid is sampled LineDotsBetween + 1 times, and a
        // sample shows a dot where it meets one of the parts the cut reaches (the cutter's targets). The screen points
        // are in the space of the camera the cutter aimed through this frame (CameraFor), turned onto the laser's
        // latest pose and cast again to find the surface.
        private static void CutLine(Vector3 start, Quaternion rotation)
        {
            var tool = GameRefs.Cutter;
            if (tool == null || tool.CurrentMode != CuttingToolController.CutterMode.Cutter) return;
            var cutter = CuttingControllerOf(tool);
            if (cutter == null || !cutter.AnyValidTargetables) return;
            var points = cutter.EdgeDetectionPoints.Points;
            var camera = CameraFor((int)GameTools.Family.Cutter);
            if (points == null || points.Length < 2 || camera == null) return;
            cutTargets.Clear();
            foreach (var target in CutTargets(cutter)) cutTargets.Add(target.Component);
            var range = cutter.ActiveCutData.Range * 3f;
            var mask = cutter.RaycastLayerMask;
            var toLaser = rotation * Quaternion.Inverse(camera.transform.rotation);
            var valid = 0;
            for (var k = 0; k < points.Length; k++)
            {
                var isValid = Valid(points[k]);
                if (isValid) valid++;
                if (k == points.Length - 1)
                {
                    if (isValid) AddCutPoint(camera, toLaser, start, points[k].ScreenPosition, range, mask);
                    break;
                }
                if (!isValid && !Valid(points[k + 1])) continue;
                for (var i = 0; i <= LineDotsBetween; i++)
                    AddCutPoint(camera, toLaser, start,
                        Vector3.Lerp(points[k].ScreenPosition, points[k + 1].ScreenPosition, i / (LineDotsBetween + 1f)), range, mask);
            }
            cutLineInfo = $"{points.Length} edge points of the game, {valid} on the {cutTargets.Count} part(s) the cut reaches, {cutPoints.Count} dots";
            if (!loggedCutLine && cutPoints.Count > 0)
            {
                loggedCutLine = true;
                Plugin.Log.LogInfo($"Splitsaw line: {cutLineInfo}");
            }
        }

        private static bool Valid(CuttingController.EdgeDetectionPoint point) =>
            point.State == CuttingController.EdgeDetectionPoint.PointState.Valid;

        // A dot where the ray through that screen point of the cutter's camera, turned onto the laser, meets a part the
        // cut reaches.
        private static void AddCutPoint(Camera camera, Quaternion toLaser, Vector3 start, Vector3 screen, float range, int mask)
        {
            var direction = toLaser * camera.ScreenPointToRay(screen).direction;
            if (!Physics.Raycast(start, direction, out var hit, range, mask)) return;
            var part = hit.collider.GetComponentInParent<StructurePart>();
            if (part != null && cutTargets.Contains(part)) cutPoints.Add(hit.point);
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
        public static bool PointsAtSomething(bool second = false) => PointsAt(second, out _);

        // The tether that hand points at (VrToolInput: A in the tether mode lets go of it); null if none.
        public static Tether TetherAt(bool second)
        {
            var from = second && VrDualTools.AimCamera != null ? VrDualTools.AimCamera.transform : CameraTransform;
            return Active && from != null ? VrTetherMode.Targeted(from.position, from.forward) : null;
        }

        // The same, with what it points at (VrTetherMode: where a tether placed with the trigger starts).
        public static bool PointsAt(bool second, out RaycastHit hit)
        {
            hit = default;
            var from = second && VrDualTools.AimCamera != null ? VrDualTools.AimCamera.transform : CameraTransform;
            if (!Active || from == null) return false;
            if (pointerMask == 0) pointerMask = PointerMask();
            return Physics.Raycast(from.position, from.forward, out hit, BeamRange * VrCamera.WorldScale, pointerMask, QueryTriggerInteraction.Ignore);
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
            var near = new Color(1f, 1f, 1f, 1f);
            var far = new Color(1f, 1f, 1f, 0.75f);
            beamMesh = new Mesh { name = "ShipbreakerVR Aim Beam" };
            beamMesh.vertices = new[] { new Vector3(-0.5f, 0f, 0f), new Vector3(0.5f, 0f, 0f), new Vector3(0.5f, 1f, 0f), new Vector3(-0.5f, 1f, 0f) };
            beamMesh.colors = new[] { near, near, far, far };
            beamMesh.triangles = new[] { 0, 2, 1, 0, 3, 2 };
            beamMaterial = VrUtil.DepthTestedMaterial(LaserColor);
            grappleMaterial = VrUtil.DepthTestedMaterial(GrappleColor);
            grappleCoreMaterial = VrUtil.DepthTestedMaterial(GrappleCoreColor);
            tetherMaterial = VrUtil.OverlayMaterial(TetherColor);
            coreMaterial = VrUtil.DepthTestedMaterial(DotCoreColor);

            // A flat disc of radius 1 facing along z, opaque in the middle and fading to its edge.
            var dotVertices = new Vector3[DotSegments + 1];
            var dotColors = new Color[DotSegments + 1];
            var dotTriangles = new int[DotSegments * 3];
            dotColors[0] = Color.white;
            for (var i = 0; i < DotSegments; i++)
            {
                var angle = i * 2f * Mathf.PI / DotSegments;
                dotVertices[i + 1] = new Vector3(Mathf.Cos(angle), Mathf.Sin(angle), 0f);
                dotColors[i + 1] = new Color(1f, 1f, 1f, 0f);
                dotTriangles[i * 3] = 0;
                dotTriangles[i * 3 + 1] = i + 1;
                dotTriangles[i * 3 + 2] = (i + 1) % DotSegments + 1;
            }
            dotMesh = new Mesh { name = "ShipbreakerVR Aim Dot", vertices = dotVertices, colors = dotColors, triangles = dotTriangles };

            // A flat ring of radius 1 facing along z.
            var vertices = new Vector3[RingSegments * 2];
            var colors = new Color[RingSegments * 2];
            var triangles = new int[RingSegments * 6];
            for (var i = 0; i < RingSegments; i++)
            {
                var angle = i * 2f * Mathf.PI / RingSegments;
                var direction = new Vector3(Mathf.Cos(angle), Mathf.Sin(angle), 0f);
                vertices[i * 2] = direction;
                vertices[i * 2 + 1] = direction * RingInner;
                colors[i * 2] = colors[i * 2 + 1] = near;
                var next = (i + 1) % RingSegments * 2;
                var t = i * 6;
                triangles[t] = i * 2;
                triangles[t + 1] = next;
                triangles[t + 2] = i * 2 + 1;
                triangles[t + 3] = i * 2 + 1;
                triangles[t + 4] = next;
                triangles[t + 5] = next + 1;
            }
            ringMesh = new Mesh { name = "ShipbreakerVR Tether Ring", vertices = vertices, colors = colors, triangles = triangles };
        }

        // On unload (hot reload): the aim camera and the pointer's meshes and materials go with the old instance.
        public static void Release()
        {
            if (aim != null) Object.Destroy(aim.gameObject);
            foreach (var o in new Object[] { beamMesh, beamMaterial, grappleMaterial, grappleCoreMaterial, ringMesh, tetherMaterial, dotMesh, coreMaterial })
                if (o != null) Object.Destroy(o);
            aim = null;
            throwOrigin = null;
            beamMesh = null;
            beamMaterial = null;
            grappleMaterial = null;
            grappleCoreMaterial = null;
            beamTether = secondBeamTether = null;
            ringMesh = null;
            tetherMaterial = null;
            dotMesh = null;
            coreMaterial = null;
            cutPoints.Clear();
        }

        // For the F8 dump.
        public static string Describe(Transform head)
        {
            var text = $"aim with hand: option {VrSettings.AimWithHand.Value}{(PatchReport.HandAimWorks ? "" : " (off, patches incomplete)")}, active {Active}, tool {withTool}";
            if (active) text += $", hand at {VrDiagnostics.Direction(head, aim.transform.position)} {Vector3.Distance(head.position, aim.transform.position):0.00} m, pointing {VrDiagnostics.Direction(head, head.position + aim.transform.forward)}, beam on {beamTarget}, Splitsaw line {cutLineInfo}";
            return text + "; " + VrHand.Describe() + "; " + VrDualTools.Describe() + CutterModel();
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
            var methods = new HashSet<MethodBase>(GameTools.MethodsCalling(GameTools.Redirect.Aim, "Aim", new[] { MainCamera, MainCameraTransform }));
            foreach (var (type, name) in Extra)
            {
                var method = AccessTools.Method(AccessTools.TypeByName(type), name);
                if (method == null) Plugin.Log.LogWarning($"Aim: {type}.{name} not found");
                else methods.Add(method);
            }
            return methods;
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
            PatchReport.Count("Aim", original, replaced, "camera call(s)");
            return list;
        }
    }
}
