using System.Collections.Generic;
using BBI.Unity.Game;
using HarmonyLib;
using UnityEngine;

namespace ShipbreakerVR
{
    // The tool models in the hand that holds the tool (VrHand.GameToolHand) while aiming with it (VrAim). The game hangs
    // the equipped tool on the right hand's equipment point (HandGrab.EquipmentGeoTransform, via
    // GrabController.GetHandPositionTransformAndSetEquipment), a fixed point on the player's body to the lower right of the
    // view, and its animation closes the right hand around the tool there (the same grab animation for every tool). Right
    // before rendering, that point goes to the controller: turned like the aim pose (the barrels run along the point's
    // forward), and placed relative to the wrist of the game's hand, which follows the controller (VrHands), as set per
    // tool and hand (VrToolFit). The tools aim from their muzzle along their barrel (Muzzle), so laser, cut and
    // grapple line leave from the barrel. With hand aiming off the point goes back. Demo charges have no model on the
    // point: the game's hand holds them, animated, so for them the arm is bent after the animation until the hand reaches
    // the controller (PoseArm); with them in the left hand VrHands moves them over to that hand.
    internal static class VrToolModels
    {
        static VrToolModels() => VrSession.OnUnload("tool models", Release);

        private static readonly AccessTools.FieldRef<HandGrab, List<Transform>> HandTargets =
            AccessTools.FieldRefAccess<HandGrab, List<Transform>>("m_EquipmentHandTargetTransforms");

        private static GrabController grab;
        private static float nextMirror;
        private static Transform point;
        // The point's own pose under its parent, to put it back, and relative to the game camera, for the angle.
        private static Vector3 restPosition;
        private static Quaternion restRotation;
        private static bool moved;
        // This frame's tool: the point relative to the grip pose (in the aim pose's axes) and turned against it, and the
        // muzzle relative to the point, for Muzzle.
        private static Vector3 mount;
        private static Quaternion turn = Quaternion.identity;
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
            // The cutter's plain right side gets its left side's detail (VrToolMirror); checked once a second, since the
            // game may swap the model.
            if (point != null && hand.CurrentEquipmentType == EquipmentController.Equipment.CuttingTool && Time.unscaledTime >= nextMirror)
            {
                nextMirror = Time.unscaledTime + 1f;
                VrToolMirror.Apply(point);
            }
            // The second tool goes to its own hand (VrDualTools).
            VrDualTools.Place(point);
            if (point == null || point.parent == null || !VrAim.TryGetHand(out var grip, out var aim))
            {
                Restore();
                return;
            }

            // The muzzle relative to the point, in the point's axes but world units.
            var equipment = hand.CurrentEquipmentType;
            var muzzle = MuzzleTransform(equipment, out var muzzleWorld);
            hasMuzzle = muzzle != null && muzzle.IsChildOf(point);
            if (hasMuzzle) muzzleOffset = Quaternion.Inverse(point.rotation) * (muzzleWorld - point.position);

            // Cutter and grapple sit in the hand as set for them (VrToolFit, adjusted here while the other hand drags them),
            // relative to the wrist where VrHands puts the game's; the other tools have no model on the point.
            var left = VrHand.GameToolHand.IsLeft;
            if (VrAim.TryGetHandLocal(out var gripLocal, out var aimLocal))
                VrToolFit.Adjust(equipment, left, gripLocal + aimLocal * VrInput.WristBehindGrip, aimLocal);
            var fitted = VrToolFit.TryGet(equipment, left, out var offset, out turn);
            mount = fitted ? VrInput.WristBehindGrip * VrCamera.WorldScale + offset : Vector3.zero;
            Log(hand, equipment, muzzle, fitted);

            point.rotation = aim * turn;
            point.position = grip + aim * mount;
            moved = true;
        }

        // The equipped tool (the game keeps it on its right hand).
        public static EquipmentController.Equipment Equipped
        {
            get
            {
                var hand = RightHand();
                return hand != null ? hand.CurrentEquipmentType : EquipmentController.Equipment.None;
            }
        }

        // The point the equipped tool's model hangs on (VrToolExport).
        public static Transform EquipmentPoint
        {
            get
            {
                var hand = RightHand();
                return hand != null ? hand.EquipmentGeoTransform : null;
            }
        }

        private static int Key(EquipmentController.Equipment equipment)
        {
            if (equipment != EquipmentController.Equipment.CuttingTool) return (int)equipment;
            if (cutter == null) cutter = GameRefs.Cutter;
            return (int)equipment * 10 + (cutter != null ? (int)cutter.CurrentMode : 0);
        }

        // Where the tool in the hand aims from and along, for a hand at grip pointing along aim (VrAim, also before Place
        // has run in a frame): its muzzle, along its barrel.
        public static bool Muzzle(Vector3 grip, Quaternion aim, out Vector3 position, out Quaternion rotation)
        {
            rotation = aim * turn;
            position = grip + aim * mount + rotation * muzzleOffset;
            return moved && hasMuzzle && point != null;
        }

        // The barrel's end of that tool, and the transform it moves with.
        public static Transform MuzzleTransform(EquipmentController.Equipment equipment, out Vector3 position)
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
            if (cutter == null) cutter = GameRefs.Cutter;
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
            if (grab == null) grab = GameRefs.Grab;
            return grab != null ? grab.RightHand : null;
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
        private static void Log(HandGrab hand, EquipmentController.Equipment equipment, Transform muzzle, bool fitted)
        {
            var key = Key(equipment);
            if (key != logCandidate)
            {
                logCandidate = key;
                logCandidateSince = Time.unscaledTime;
            }
            if (equipment == EquipmentController.Equipment.None || Time.unscaledTime - logCandidateSince < 1f || !logged.Add(key)) return;
            var models = new List<string>();
            foreach (Transform child in point) models.Add($"'{child.name}'");
            var gripTarget = GripTarget(hand, equipment);
            Plugin.Log.LogInfo($"Tool in hand: {equipment}; equipment point {VrUtil.Chain(point)}, rest {restPosition} {restRotation.eulerAngles}; " +
                $"models on it: {string.Join(", ", models)}; grip target {(gripTarget == null ? "none" : VrUtil.Chain(gripTarget))}, " +
                $"{(gripTarget != null && gripTarget.IsChildOf(point) ? "moves with the tool" : "stays")}; point at {mount} m from the grip pose " +
                $"({(fitted ? "fitted" : "no fit")}), scale {point.lossyScale}; " +
                $"muzzle {(muzzle == null ? "none" : $"{VrUtil.Chain(muzzle)} at {muzzleOffset} m, {Vector3.Angle(muzzle.forward, point.forward):0}° off the point's forward")}; hand target {VrUtil.Chain(hand.HandPositionTransform)}");
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
            var aiming = VrAim.Active;
            var chargeLeft = ChargeInLeftHand;
            if (heldBefore == EquipmentController.Equipment.DemoCharge && holding == EquipmentController.Equipment.None && aiming)
            {
                StowCharge(true);
                // From the left hand the right arm was with its own controller; it stays there.
                stowingSince = chargeLeft ? -1f : Time.time;
            }
            else if (heldBefore != EquipmentController.Equipment.DemoCharge && holding == EquipmentController.Equipment.DemoCharge)
            {
                if (stowed) StowCharge(false);
                stowingSince = -1f;
            }
            heldBefore = holding;
            holdingAtHide = holding;
            HideProps(aiming && hand != null && holding != EquipmentController.Equipment.DemoCharge && FindArm(hand));

            // The charge in the left hand: VrHands moves it over to that hand. The tool hand switches back to the right one
            // only once the charge is gone; a right hand that takes the charge over holds its grip.
            var charge = hand != null && hand.Hand == HandGrab.PlayerHand.Right && hand.CurrentHandState == HandGrab.HandState.Equipped
                && holding == EquipmentController.Equipment.DemoCharge;
            ChargeInLeftHand = aiming && charge && (VrHand.GameToolHand.IsLeft
                || (chargeLeft && !VrInput.Right.Grip.Down));

            // Fully at the controller while the charge is held in the right hand; after letting go the arm blends back
            // into the stow animation, starting from the hand instead of the flat game's spot at the right.
            var weight = 0f;
            if (charge && !ChargeInLeftHand) weight = 1f;
            else if (stowingSince >= 0f && Time.time - stowingSince < StowBlend) weight = 1f - (Time.time - stowingSince) / StowBlend;
            PosesRightArm = weight > 0f;
            if (weight <= 0f || vr == null || hand == null || !rigKnown) return;
            var camera = vr.GameCamera;
            if (camera == null || !VrAim.TryGetHandLocal(out var gripLocal, out var aimLocal)
                || !TrackingToWorld(gripLocal + aimLocal * VrInput.WristBehindGrip, aimLocal, out var wrist, out var aim))
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
            var animated = armTip.rotation;
            var wristRotation = aim * (Quaternion.Inverse(VrTorso.Frame(head.rotation)) * animated);
            if (weight >= 1f) LogArm(hand, head);
            Reach(armRoot.parent, armRoot, armMid, armTip, Vector3.Lerp(armTip.position, wrist, weight));
            armTip.rotation = Quaternion.Slerp(animated, wristRotation, weight);
        }

        // True while PoseArm bends the right arm (VrHands leaves it alone then).
        public static bool PosesRightArm { get; private set; }

        // True while the left hand holds the demo charges, which hang on the game's right hand (VrHands moves them).
        public static bool ChargeInLeftHand { get; private set; }

        // A pose in tracking space in the world, for code that runs after the animation (LateUpdate), before the rig
        // follows the game camera (onBeforeRender): the rig is moved by as much as the game camera moved since then.
        public static bool TrackingToWorld(Vector3 local, Quaternion localRotation, out Vector3 position, out Quaternion rotation)
        {
            position = Vector3.zero;
            rotation = Quaternion.identity;
            var vr = VrCamera.Instance;
            var camera = vr != null ? vr.GameCamera : null;
            if (!rigKnown || camera == null) return false;
            var rig = vr.transform.parent;
            position = rig.position + (camera.transform.position - lastCameraPosition) + rig.rotation * Vector3.Scale(rig.localScale, local);
            rotation = rig.rotation * localRotation;
            return true;
        }

        // How long the arm takes to blend from the controller into the stow animation.
        private const float StowBlend = 0.35f;
        private static float stowingSince = -1f;
        private static readonly List<Renderer> hiddenProps = new List<Renderer>();
        private static bool propsHidden;
        private static int propLogs;
        // The charge's renderers on the hand bone (chargesOn), also those not showing yet.
        private static readonly List<Renderer> chargePieces = new List<Renderer>();
        private static Transform chargesOn;
        private static float nextChargeSearch;
        private static EquipmentController.Equipment holdingAtHide;

        // The charge and the detonator hang on the right hand's bone, and the game leaves them there: the flat game keeps
        // that hand out of view unless it holds them. Here the hand follows its controller (VrHands), so they are hidden
        // whenever the charge is not held, from the start (when the game's hand already carries a charge) and as soon as
        // the grip lets go instead of riding along through the stow animation. They come back when the charge is drawn.
        // Checked every frame while hidden: at the start no piece was showing yet when they were first hidden, and the
        // charge appeared in the hand later anyway.
        private static void HideProps(bool hide)
        {
            if (!hide)
            {
                if (!propsHidden) return;
                propsHidden = false;
                foreach (var renderer in hiddenProps)
                    if (renderer != null) renderer.enabled = true;
                hiddenProps.Clear();
                return;
            }
            propsHidden = true;
            if (chargesOn != armTip || (chargePieces.Count == 0 && Time.unscaledTime >= nextChargeSearch))
            {
                chargesOn = armTip;
                nextChargeSearch = Time.unscaledTime + 2f;
                chargePieces.Clear();
                foreach (var renderer in armTip.GetComponentsInChildren<Renderer>(true))
                    if (OnCharge(renderer.transform)) chargePieces.Add(renderer);
                Plugin.Log.LogInfo($"Tool in hand: charge pieces on the hand: {string.Join(", ", chargePieces.ConvertAll(r => $"{VrUtil.Chain(r.transform)} ({(r.enabled ? "enabled" : "disabled")}, {(r.gameObject.activeInHierarchy ? "active" : "inactive")})"))}");
            }
            var hid = new List<string>();
            foreach (var renderer in chargePieces)
            {
                if (renderer == null || !renderer.enabled) continue;
                renderer.enabled = false;
                if (!hiddenProps.Contains(renderer)) hiddenProps.Add(renderer);
                hid.Add(renderer.name);
            }
            if (hid.Count > 0 && propLogs++ < 10)
                Plugin.Log.LogInfo($"Tool in hand: hid {hid.Count} renderer(s) on the hand: {string.Join(", ", hid)} ({(holdingAtHide == EquipmentController.Equipment.None ? "nothing" : holdingAtHide.ToString())} held)");
        }

        // Only the charge's own pieces (SM_Props_Demo_Charge, PRF_Tool_ExplosiveChargeDetonator), not whatever else
        // the game may hang on the hand.
        private static bool OnCharge(Transform transform)
        {
            for (var t = transform; t != null && t != armTip; t = t.parent)
                if (t.name.IndexOf("Charge", System.StringComparison.OrdinalIgnoreCase) >= 0) return true;
            return false;
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

        // How far the collarbone pushes the shoulder towards a target out of the arm's reach, in meters of the model.
        public const float CollarReach = 0.08f;
        // The most the collarbone turns for it, in degrees.
        private const float CollarTurn = 30f;

        // Bend, with the collarbone (the shoulder's parent) first turning the shoulder towards a target the arm cannot
        // reach, as one pushes the shoulder forward reaching for something. Returns how far the wrist still stays short
        // of the target, in meters (0 when it gets there).
        public static float Reach(Transform collar, Transform root, Transform mid, Transform tip, Vector3 target)
        {
            var length = Vector3.Distance(root.position, mid.position) + Vector3.Distance(mid.position, tip.position);
            var over = Vector3.Distance(root.position, target) - length;
            if (over > 0f && collar != null)
            {
                var shoulder = root.position;
                var toShoulder = shoulder - collar.position;
                var moved = shoulder + (target - shoulder).normalized * Mathf.Min(over, CollarReach) - collar.position;
                var turn = Quaternion.FromToRotation(toShoulder, moved);
                turn.ToAngleAxis(out var angle, out var axis);
                if (angle > 180f) angle -= 360f;
                if (axis.sqrMagnitude > 1e-8f && Mathf.Abs(angle) > 1e-3f)
                    collar.rotation = Quaternion.AngleAxis(Mathf.Clamp(angle, -CollarTurn, CollarTurn), axis) * collar.rotation;
            }
            Bend(root, mid, tip, target);
            return Mathf.Max(0f, Vector3.Distance(root.position, target) - length);
        }

        // Two-bone IK: the elbow bends until shoulder to wrist is as long as shoulder to target, then the shoulder turns
        // the arm onto the target. The elbow stays in the plane the animation gave it.
        public static void Bend(Transform root, Transform mid, Transform tip, Vector3 target)
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
            Plugin.Log.LogInfo($"Tool in hand: game arm {(armTip != null ? $"{VrUtil.Chain(armTip)}, from '{armRoot.name}' over '{armMid.name}'" : "not found")}; rig constraints: {string.Join("; ", constraints)}");
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
                $"detonator {(detonator == null ? "none" : $"{VrUtil.Chain(detonator.transform)}{(detonator.transform.IsChildOf(armTip) ? ", moves with the hand" : ", not on the hand")}")}; " +
                $"hand animator {(animator == null ? "none" : VrUtil.Chain(animator.transform))}");
        }

        // The game parents a drawn tool to the equipment point keeping its world pose, so it starts where it rested in front
        // of the body and springs into the hand from there (CuttingFXController / GunController lerp it to the point). With
        // the point in the controller the tool is taken from the body where the hand reaches for it, so it starts in the
        // hand at once.
        public static void SnapIntoHand(Transform tool)
        {
            if (!moved || point == null || tool == null || tool.parent != point) return;
            Plugin.Log.LogInfo($"Tool in hand: '{tool.name}' drawn straight into the hand ({tool.localPosition.magnitude:0.00} m from it)");
            tool.localPosition = Vector3.zero;
            tool.localRotation = Quaternion.identity;
        }

        // On unload (hot reload): the equipment point goes back to the game.
        public static void Release()
        {
            HideProps(false);
            PosesRightArm = false;
            ChargeInLeftHand = false;
            stowingSince = -1f;
            Restore();
            point = null;
            grab = null;
            cutter = null;
            splitsaw = null;
            stinger = null;
            grapple = null;
            nextMirror = 0f;
            VrToolMirror.Release();
            logged.Clear();
            armRoot = armMid = armTip = armFor = null;
            chargePieces.Clear();
            chargesOn = null;
            charges = null;
            loggedArm = loggedArmSkip = false;
            rigKnown = false;
        }
    }

    // The cutter and the grapple start in the hand when drawn (VrToolModels.SnapIntoHand).
    [HarmonyPatch]
    internal static class ToolDrawPatch
    {
        private static readonly AccessTools.FieldRef<CuttingToolController, Transform> Cutter =
            AccessTools.FieldRefAccess<CuttingToolController, Transform>("m_CuttingTool");
        private static readonly AccessTools.FieldRef<GunController, Transform> Grapple =
            AccessTools.FieldRefAccess<GunController, Transform>("m_GrapplingGun");

        [HarmonyPostfix]
        [HarmonyPatch(typeof(CuttingToolController), "EquipCurrentMode")]
        private static void CutterDrawn(CuttingToolController __instance) => VrToolModels.SnapIntoHand(Cutter(__instance));

        [HarmonyPostfix]
        [HarmonyPatch(typeof(GunController), nameof(GunController.EquipGun))]
        private static void GrappleDrawn(GunController __instance) => VrToolModels.SnapIntoHand(Grapple(__instance));
    }
}
