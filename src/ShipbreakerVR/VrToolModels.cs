using System.Collections.Generic;
using BBI.Unity.Game;
using HarmonyLib;
using UnityEngine;

namespace ShipbreakerVR
{
    // The tool models in the right hand while aiming with it (VrAim). The game hangs the equipped tool on the right hand's
    // equipment point (HandGrab.EquipmentGeoTransform, via GrabController.GetHandPositionTransformAndSetEquipment), a fixed
    // point on the player's body to the lower right of the view, and moves the game's hand to the tool's grip
    // (m_EquipmentHandTargetTransforms). Right before rendering, that point goes to the controller: turned like the aim
    // pose (the barrels run along the point's forward), and shifted so that the tool's grip lies in the controller's grip.
    // The tools aim from their muzzle (Muzzle), so beam, cut and grapple line leave from the barrel. With hand aiming off
    // the point goes back. Demo charges have no model on the point: the game's hand holds them, animated, so for them the
    // arm is bent after the animation until the hand reaches the controller (PoseArm).
    internal static class VrToolModels
    {
        private static readonly AccessTools.FieldRef<HandGrab, List<Transform>> HandTargets =
            AccessTools.FieldRefAccess<HandGrab, List<Transform>>("m_EquipmentHandTargetTransforms");

        private static GrabController grab;
        private static float nextSearch;
        private static Transform point;
        // The point's own pose under its parent, to put it back, and relative to the game camera, for the angle.
        private static Vector3 restPosition;
        private static Quaternion restRotation;
        private static bool moved;
        // This frame's tool: its grip and muzzle relative to the point, for Muzzle.
        private static Vector3 gripOffset;
        private static Vector3 muzzleOffset;
        private static bool hasMuzzle;
        private static CuttingToolController cutter;
        private static CuttingFXController splitsaw;
        private static ScalpelFXController stinger;
        private static GrapplingHook grapple;
        private static readonly AccessTools.FieldRef<CuttingFXController, Transform> SplitsawOrigin =
            AccessTools.FieldRefAccess<CuttingFXController, Transform>("m_CutOriginTransform");
        private static readonly AccessTools.FieldRef<CuttingFXController, Vector3> SplitsawOffset =
            AccessTools.FieldRefAccess<CuttingFXController, Vector3>("m_CutOriginOffset");
        private static readonly AccessTools.FieldRef<ScalpelFXController, Transform> StingerOrigin =
            AccessTools.FieldRefAccess<ScalpelFXController, Transform>("m_CutOriginTransform");
        private static readonly AccessTools.FieldRef<ScalpelFXController, Vector3> StingerOffset =
            AccessTools.FieldRefAccess<ScalpelFXController, Vector3>("m_CutOriginOffset");
        private static readonly AccessTools.FieldRef<GrapplingHook, Transform> GrappleBarrel =
            AccessTools.FieldRefAccess<GrapplingHook, Transform>("m_GrapplingGunBarrel");
        private static readonly HashSet<int> logged = new HashSet<int>();
        // Where the game camera was when the rig last followed it (PoseArm).
        private static Vector3 lastCameraPosition;
        private static bool rigKnown;
        private static int logCandidate;
        private static float logCandidateSince;

        // From VrCamera.FollowGameCamera, after the rig has moved.
        public static void Place()
        {
            var camera = VrCamera.Instance != null ? VrCamera.Instance.GameCamera : null;
            rigKnown = camera != null;
            if (rigKnown) lastCameraPosition = camera.transform.position;
            var hand = RightHand();
            var target = hand != null ? hand.EquipmentGeoTransform : null;
            if (target != point)
            {
                Restore();
                point = target;
                if (point != null)
                {
                    restPosition = point.localPosition;
                    restRotation = point.localRotation;
                }
            }
            if (point == null || point.parent == null || !VrAim.TryGetHand(out var grip, out var aim))
            {
                Restore();
                return;
            }

            // The tool's grip and muzzle relative to the point, in the point's axes but world units (the point at rest if
            // the grip does not move with it).
            var equipment = hand.CurrentEquipmentType;
            var gripTarget = GripTarget(hand, equipment);
            var toPoint = Quaternion.Inverse(point.rotation);
            gripOffset = Vector3.zero;
            if (gripTarget != null)
            {
                gripOffset = gripTarget.IsChildOf(point)
                    ? toPoint * (gripTarget.position - point.position)
                    : Quaternion.Inverse(point.parent.rotation * restRotation) * (gripTarget.position - point.parent.TransformPoint(restPosition));
            }
            var muzzle = MuzzleTransform(equipment, out var muzzleWorld);
            hasMuzzle = muzzle != null && muzzle.IsChildOf(point);
            if (hasMuzzle) muzzleOffset = toPoint * (muzzleWorld - point.position);
            Log(hand, equipment, gripTarget, muzzle);

            point.rotation = aim;
            point.position = grip - aim * gripOffset;
            moved = true;
        }

        // Where the tool in the hand aims from, for a hand at grip pointing along rotation (VrAim, also before Place has
        // run in a frame).
        public static bool Muzzle(Vector3 grip, Quaternion rotation, out Vector3 position)
        {
            position = grip + rotation * (muzzleOffset - gripOffset);
            return moved && hasMuzzle && point != null;
        }

        // The barrel's end of the equipped tool, and the transform it moves with.
        private static Transform MuzzleTransform(EquipmentController.Equipment equipment, out Vector3 position)
        {
            position = Vector3.zero;
            if (equipment == EquipmentController.Equipment.GrappleHook)
            {
                if (grapple == null) grapple = Object.FindObjectOfType<GrapplingHook>();
                var barrel = grapple != null ? GrappleBarrel(grapple) : null;
                if (barrel != null) position = barrel.position;
                return barrel;
            }
            if (equipment != EquipmentController.Equipment.CuttingTool) return null;
            if (cutter == null) cutter = Object.FindObjectOfType<CuttingToolController>();
            if (cutter != null && cutter.CurrentMode == CuttingToolController.CutterMode.Scalpel)
            {
                if (stinger == null) stinger = Object.FindObjectOfType<ScalpelFXController>();
                var origin = stinger != null ? StingerOrigin(stinger) : null;
                if (origin != null) position = origin.TransformPoint(StingerOffset(stinger));
                return origin;
            }
            if (splitsaw == null) splitsaw = Object.FindObjectOfType<CuttingFXController>();
            var cut = splitsaw != null ? SplitsawOrigin(splitsaw) : null;
            if (cut != null) position = cut.TransformPoint(SplitsawOffset(splitsaw));
            return cut;
        }

        private static HandGrab RightHand()
        {
            if (grab == null)
            {
                if (Time.unscaledTime < nextSearch) return null;
                nextSearch = Time.unscaledTime + 1f;
                grab = Object.FindObjectOfType<GrabController>();
                if (grab == null) return null;
            }
            return grab.RightHand;
        }

        private static Transform GripTarget(HandGrab hand, EquipmentController.Equipment equipment)
        {
            var targets = HandTargets(hand);
            var index = (int)equipment;
            return targets != null && index > 0 && index < targets.Count ? targets[index] : null;
        }

        private static void Restore()
        {
            hasMuzzle = false;
            if (!moved) return;
            moved = false;
            if (point == null) return;
            point.localPosition = restPosition;
            point.localRotation = restRotation;
        }

        // Once per tool, a second after it was drawn (the model first slides in from its rest point): where the game keeps
        // it, for judging the fit in the headset.
        private static void Log(HandGrab hand, EquipmentController.Equipment equipment, Transform gripTarget, Transform muzzle)
        {
            var key = equipment == EquipmentController.Equipment.CuttingTool && cutter != null ? (int)equipment * 10 + (int)cutter.CurrentMode : (int)equipment;
            if (key != logCandidate)
            {
                logCandidate = key;
                logCandidateSince = Time.unscaledTime;
            }
            if (equipment == EquipmentController.Equipment.None || Time.unscaledTime - logCandidateSince < 1f || !logged.Add(key)) return;
            var models = new List<string>();
            foreach (Transform child in point) models.Add($"'{child.name}'");
            Plugin.Log.LogInfo($"Tool in hand: {equipment}; equipment point {Chain(point)}, rest {restPosition} {restRotation.eulerAngles}; " +
                $"models on it: {string.Join(", ", models)}; grip {(gripTarget == null ? "none" : Chain(gripTarget))}, " +
                $"{(gripTarget != null && gripTarget.IsChildOf(point) ? "moves with the tool" : "stays")}, offset {gripOffset} m, scale {point.lossyScale}; " +
                $"muzzle {(muzzle == null ? "none" : $"{Chain(muzzle)} at {muzzleOffset} m, {Vector3.Angle(muzzle.forward, point.forward):0}° off the point's forward")}; hand target {Chain(hand.HandPositionTransform)}");
        }

        private static string Chain(Transform transform)
        {
            var names = new List<string>();
            for (var t = transform; t != null && names.Count < 8; t = t.parent) names.Add(t.name);
            return string.Join(" < ", names);
        }

        // The game's right hand holding a demo charge (or the detonator): the animation poses the arm, with Animation
        // Rigging's IK (TwoBoneIKConstraint) towards HandGrab.m_HandTarget. Moving that target had no visible effect, so
        // after the animation, before skinning (from VrCamera.LateUpdate), the arm itself is bent so that the wrist sits
        // at the controller, the hand keeping the angle to the eye that the animation gave it. The rig only moves to the
        // game camera right before rendering, so the hand is placed where the rig will be: moved by as much as the game
        // camera moved since then.
        public static void PoseArm()
        {
            var vr = VrCamera.Instance;
            var hand = RightHand();
            var holding = hand != null ? hand.CurrentEquipmentType : EquipmentController.Equipment.None;
            if (heldBefore == EquipmentController.Equipment.DemoCharge && holding == EquipmentController.Equipment.None && VrAim.Active)
            {
                StowCharge(true);
                HideProps(true);
                stowingSince = Time.time;
            }
            else if (heldBefore != EquipmentController.Equipment.DemoCharge && holding == EquipmentController.Equipment.DemoCharge)
            {
                if (stowed) StowCharge(false);
                HideProps(false);
                stowingSince = -1f;
            }
            heldBefore = holding;

            // Fully at the controller while the charge is held; after letting go the arm blends back into the stow
            // animation, starting from the hand instead of the flat game's spot at the right.
            var weight = 0f;
            if (hand != null && hand.Hand == HandGrab.PlayerHand.Right && hand.CurrentHandState == HandGrab.HandState.Equipped
                && holding == EquipmentController.Equipment.DemoCharge) weight = 1f;
            else if (stowingSince >= 0f && Time.time - stowingSince < StowBlend) weight = 1f - (Time.time - stowingSince) / StowBlend;
            if (weight <= 0f || vr == null || hand == null || !rigKnown) return;
            var camera = vr.GameCamera;
            if (camera == null || !VrAim.TryGetHandLocal(out var gripLocal, out var aimLocal))
            {
                if (!loggedArmSkip)
                {
                    loggedArmSkip = true;
                    Plugin.Log.LogInfo($"Tool in hand: demo charge held, but no {(camera == null ? "game camera" : "hand pose")}");
                }
                return;
            }
            if (!FindArm(hand)) return;
            var head = camera.transform;
            var rig = vr.transform.parent;
            var rigPosition = rig.position + (head.position - lastCameraPosition);
            var grip = rigPosition + rig.rotation * Vector3.Scale(rig.localScale, gripLocal);
            var aim = rig.rotation * aimLocal;
            var animated = armTip.rotation;
            var wristRotation = aim * (Quaternion.Inverse(head.rotation) * animated);
            var wrist = grip + aim * (WristBehindGrip * rig.localScale.z);
            if (weight >= 1f) LogArm(hand, head);
            Bend(armRoot, armMid, armTip, Vector3.Lerp(armTip.position, wrist, weight));
            armTip.rotation = Quaternion.Slerp(animated, wristRotation, weight);
        }

        // How long the arm takes to blend from the controller into the stow animation.
        private const float StowBlend = 0.35f;
        private static float stowingSince = -1f;
        private static readonly List<Renderer> hiddenProps = new List<Renderer>();

        // The charge and the detonator hang on the hand bone; they disappear as soon as the grip lets go instead of riding
        // along through the stow animation, and come back when the charge is drawn again.
        private static void HideProps(bool hide)
        {
            if (!hide)
            {
                foreach (var renderer in hiddenProps)
                    if (renderer != null) renderer.enabled = true;
                hiddenProps.Clear();
                return;
            }
            if (armTip == null) return;
            foreach (var renderer in armTip.GetComponentsInChildren<Renderer>())
            {
                if (!renderer.enabled) continue;
                renderer.enabled = false;
                hiddenProps.Add(renderer);
            }
            if (stowLogs < 5) Plugin.Log.LogInfo($"Tool in hand: hid {hiddenProps.Count} renderer(s) on the hand: {string.Join(", ", hiddenProps.ConvertAll(r => r.name))}");
        }

        // Put away with the grip (VrToolInput), the demo charge stays in the animated hand: the game only ever leaves it by
        // drawing another tool, whose grab animation takes over. Its animator has stow triggers that the game's code never
        // fires (AnimationParameters.DemoChargeStowParameter, DetonatorStowParameter); they are fired here instead, and
        // reset when the charge is drawn again, in case the animator did not use them.
        private static void StowCharge(bool stow)
        {
            stowed = stow;
            if (charges == null) charges = Object.FindObjectOfType<DemoChargeController>();
            if (charges == null) return;
            var animator = Traverse.Create(charges).Field("m_HandAnimatorController").GetValue<Animator>();
            var parameters = charges.AnimationParamters;
            if (!stow)
            {
                if (animator == null) return;
                foreach (var parameter in animator.parameters)
                    if (parameter.type == AnimatorControllerParameterType.Trigger
                        && (parameter.name == parameters.DemoChargeStowParameter || parameter.name == parameters.DetonatorStowParameter))
                        animator.ResetTrigger(parameter.name);
                return;
            }
            var detonator = ChargeState(charges) >= DemoChargeState.Detonation;
            var trigger = detonator ? parameters.DetonatorStowParameter : parameters.DemoChargeStowParameter;
            var known = false;
            if (animator != null && !string.IsNullOrEmpty(trigger))
                foreach (var parameter in animator.parameters)
                    known |= parameter.name == trigger && parameter.type == AnimatorControllerParameterType.Trigger;
            if (known) animator.SetTrigger(trigger);
            if (stowLogs++ < 5)
                Plugin.Log.LogInfo($"Tool in hand: demo charge put away ({(detonator ? "detonator" : "charge")}), stow trigger '{trigger}' {(known ? "fired" : "not on the animator")}");
        }

        private static readonly AccessTools.FieldRef<DemoChargeController, DemoChargeState> ChargeState =
            AccessTools.FieldRefAccess<DemoChargeController, DemoChargeState>("mState");
        private static DemoChargeController charges;
        private static EquipmentController.Equipment heldBefore;
        private static int stowLogs;
        private static bool stowed;

        // The wrist relative to the grip pose, along the aim: an estimate, as in VrControls.
        private static readonly Vector3 WristBehindGrip = new Vector3(0f, 0f, -0.08f);

        // Two-bone IK: the elbow bends until shoulder to wrist is as long as shoulder to target, then the shoulder turns
        // the arm onto the target. The elbow stays in the plane the animation gave it.
        private static void Bend(Transform root, Transform mid, Transform tip, Vector3 target)
        {
            var a = root.position;
            var b = mid.position;
            var upper = Vector3.Distance(a, b);
            var lower = Vector3.Distance(b, tip.position);
            if (upper < 0.001f || lower < 0.001f) return;
            var reach = Mathf.Clamp(Vector3.Distance(a, target), Mathf.Abs(upper - lower) + 0.001f, upper + lower - 0.001f);
            var toRoot = a - b;
            var toTip = tip.position - b;
            var axis = Vector3.Cross(toRoot, toTip);
            if (axis.sqrMagnitude < 1e-8f) axis = Vector3.Cross(toRoot, root.up);
            if (axis.sqrMagnitude < 1e-8f) return;
            var current = Vector3.Angle(toRoot, toTip);
            var wanted = Mathf.Acos(Mathf.Clamp((upper * upper + lower * lower - reach * reach) / (2f * upper * lower), -1f, 1f)) * Mathf.Rad2Deg;
            mid.rotation = Quaternion.AngleAxis(wanted - current, axis.normalized) * mid.rotation;
            root.rotation = Quaternion.FromToRotation(tip.position - a, target - a) * root.rotation;
        }

        private static Transform armRoot;
        private static Transform armMid;
        private static Transform armTip;
        private static Transform armFor;
        private static bool loggedArm;
        private static bool loggedArmSkip;

        // The right arm's bones from the IK constraint that aims at the hand target (Unity.Animation.Rigging, read by
        // reflection; the plugin does not reference that assembly).
        private static bool FindArm(HandGrab hand)
        {
            var target = hand.HandPositionTransform;
            if (target == armFor) return armTip != null;
            armFor = target;
            armRoot = armMid = armTip = null;
            if (target == null) return false;
            var constraints = new List<string>();
            try
            {
                foreach (var component in target.root.GetComponentsInChildren<MonoBehaviour>(true))
                {
                    // Missing scripts come back as null.
                    if (component == null) continue;
                    var type = component.GetType();
                    if (type.Namespace != "UnityEngine.Animations.Rigging") continue;
                    // RigConstraint.data returns by reference, which reflection cannot call; its field can be read.
                    var data = Field(component, "m_Data");
                    var constrainedTarget = Field(data, "m_Target") as Transform;
                    constraints.Add($"{type.Name} on '{component.name}' (weight {Field(component, "m_Weight")}{(constrainedTarget != null ? $", target '{constrainedTarget.name}'" : "")})");
                    if (constrainedTarget == null || constrainedTarget != target) continue;
                    armRoot = Field(data, "m_Root") as Transform;
                    armMid = Field(data, "m_Mid") as Transform;
                    armTip = Field(data, "m_Tip") as Transform;
                }
                // Without such a constraint: the humanoid bones of the player's animator.
                if (armTip == null)
                {
                    foreach (var animator in target.root.GetComponentsInChildren<Animator>(true))
                    {
                        if (animator == null || !animator.isHuman) continue;
                        armRoot = animator.GetBoneTransform(HumanBodyBones.RightUpperArm);
                        armMid = animator.GetBoneTransform(HumanBodyBones.RightLowerArm);
                        armTip = animator.GetBoneTransform(HumanBodyBones.RightHand);
                        constraints.Add($"humanoid animator '{animator.name}'");
                        if (armTip != null) break;
                    }
                }
            }
            catch (System.Exception e)
            {
                Plugin.Log.LogWarning($"Tool in hand: looking for the arm failed: {e}");
                armRoot = armMid = armTip = null;
            }
            if (armRoot == null || armMid == null) armTip = null;
            Plugin.Log.LogInfo($"Tool in hand: arm for the demo charge {(armTip != null ? $"{Chain(armTip)}, from '{armRoot.name}' over '{armMid.name}'" : "not found")}; rig constraints: {string.Join("; ", constraints)}");
            return armTip != null;
        }

        private static object Field(object instance, string name) =>
            instance == null ? null : AccessTools.Field(instance.GetType(), name)?.GetValue(instance);

        // Once: what the demo charge's pieces hang on.
        private static void LogArm(HandGrab hand, Transform head)
        {
            if (loggedArm) return;
            loggedArm = true;
            var charges = Object.FindObjectOfType<DemoChargeController>();
            var detonator = charges != null ? Traverse.Create(charges).Field("m_DetonatorMeshRoot").GetValue<GameObject>() : null;
            var animator = charges != null ? Traverse.Create(charges).Field("m_HandAnimatorController").GetValue<Animator>() : null;
            Plugin.Log.LogInfo($"Tool in hand: demo charge; wrist '{armTip.name}' at {head.InverseTransformPoint(armTip.position)} from the eye; " +
                $"detonator {(detonator == null ? "none" : $"{Chain(detonator.transform)}{(detonator.transform.IsChildOf(armTip) ? ", moves with the hand" : ", not on the hand")}")}; " +
                $"hand animator {(animator == null ? "none" : Chain(animator.transform))}");
        }

        // On unload (hot reload): the equipment point goes back to the game.
        public static void Release()
        {
            HideProps(false);
            stowingSince = -1f;
            Restore();
            point = null;
            grab = null;
            cutter = null;
            splitsaw = null;
            stinger = null;
            grapple = null;
            logged.Clear();
            armRoot = armMid = armTip = armFor = null;
            charges = null;
            loggedArm = loggedArmSkip = false;
            rigKnown = false;
        }
    }
}
