using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Reflection.Emit;
using BBI.Unity.Game;
using HarmonyLib;
using UnityEngine;
using UnityEngine.XR;

namespace ShipbreakerVR
{
    using Family = GameTools.Family;

    // One tool in each hand, as Marc asked: the cutter in one, the grapple in the other. The game knows a single equipped
    // tool (EquipmentController.CurrentEquipment); drawing another one puts the old one away (SetEquipment). Here the hand
    // that draws the second tool gets it as the game's own, and the tool the other hand holds stays out: its unequip is
    // skipped (UnequipPatch), and the code of that tool's family sees it as equipped (GatePatch). Each tool exists once,
    // so a hand that reaches for the tool the other hand holds takes it over, as before. The cutter, the grapple and the
    // demo charges pair up. The demo charges stay the game's tool whichever hand drew first (their arm, animation and
    // pieces hang on the game's right hand, VrToolModels), so a cutter or grapple drawn next to them is added as the
    // second tool through its own controller (Add). The tools re-equip themselves while working (EquipGun on every
    // grapple shot, EquipCutter on cutting); for the second one that neither touches the game's right hand nor tells the
    // game (HandPatch, EquipPatch). The scanner does not pair: drawn next to another tool it hides that one, which comes
    // back into its hand when the scanner is put away while that hand still holds its grip (Suspend, Resume).
    // The second tool works from its own hand: its family's input polls read that hand's buttons (VrToolInput), its aim
    // goes through a camera of its own on its muzzle (VrAim.CameraFor), and its model hangs on a point of its own that
    // follows that controller (Place), fitted like the first (VrToolFit). Letting go of the game's tool hands the game the
    // second one (Promote); letting go of the second one puts it away through its own controller (PutAway).
    internal static class VrDualTools
    {
        static VrDualTools() => VrSession.OnUnload("second tool", Release);

        public static EquipmentController.Equipment Second => VrHand.Second;
        public static XRNode SecondNode => VrHand.SecondHand?.Node ?? XRNode.LeftHand;

        private static EquipmentController equipment;
        private static CuttingToolController cutter;
        private static GunController gun;
        private static readonly MethodInfo SetEquipment = AccessTools.Method(typeof(EquipmentController), "SetEquipment");
        private static readonly MethodInfo PlayEquipAudio = AccessTools.Method(typeof(EquipmentController), "PlayEquipAudio");
        private static readonly MethodInfo PlayUnequipAudio = AccessTools.Method(typeof(EquipmentController), "PlayUnequipAudio");
        private static readonly AccessTools.FieldRef<CuttingToolController, Transform> CutterModel =
            AccessTools.FieldRefAccess<CuttingToolController, Transform>("m_CuttingTool");
        private static readonly AccessTools.FieldRef<GunController, Transform> GrappleModel =
            AccessTools.FieldRefAccess<GunController, Transform>("m_GrapplingGun");
        private static int logs;

        // The option, with all the patches it needs (PatchReport).
        public static bool Enabled => Plugin.TwoTools != null && Plugin.TwoTools.Value && PatchReport.TwoToolsWork;

        // The family of a game class (GameTools).
        public static Family FamilyOf(System.Type type) => GameTools.FamilyOf(type);

        // For code shared by several families (RaycastSystem): the family of the instance running it.
        public static int FamilyOfInstance(object instance) => instance == null ? 0 : (int)FamilyOf(instance.GetType());

        private static EquipmentController.Equipment ToolOf(Family family) =>
            family == Family.Cutter ? EquipmentController.Equipment.CuttingTool
            : family == Family.Grapple ? EquipmentController.Equipment.GrappleHook
            : EquipmentController.Equipment.None;

        // Whether that family's code works from the second hand (its tool is the second one).
        public static bool Routes(int family) => Second != EquipmentController.Equipment.None && ToolOf((Family)family) == Second;

        // Whether a hand that draws next while the other hand holds held may keep both.
        public static bool CanPair(EquipmentController.Equipment held, EquipmentController.Equipment next) =>
            Enabled && held != next && IsPairable(held) && IsPairable(next);

        private static bool IsPairable(EquipmentController.Equipment tool) =>
            tool == EquipmentController.Equipment.CuttingTool || tool == EquipmentController.Equipment.GrappleHook
            || tool == EquipmentController.Equipment.DemoCharge;

        // Whether that hand holds the second tool.
        public static bool Holds(XRNode node) => Second != EquipmentController.Equipment.None && SecondNode == node;

        // What the tool classes read instead of EquipmentController.CurrentEquipment: their own tool while it is the
        // second one, the game's otherwise.
        public static EquipmentController.Equipment CurrentEquipment(EquipmentController controller, int family)
        {
            if (Routes(family)) return Second;
            return controller.CurrentEquipment;
        }

        // TetherController.IsEquipped is small enough for Mono to inline, so its callers get this instead.
        public static bool TetherEquipped(TetherController tether) =>
            Routes((int)Family.Grapple) || (Find() && equipment.CurrentEquipment == EquipmentController.Equipment.GrappleHook);

        // From UnequipPatch: the game puts away the tool the second hand holds (drawing the other tool into the first
        // hand); it stays out.
        public static bool Keeps(EquipmentController.Equipment tool) => Second != EquipmentController.Equipment.None && tool == Second;

        // The hand that held the game's tool keeps it as the second one, and the drawing hand gets the new tool from the
        // game, without the game putting the kept one away.
        public static void Draw(EquipmentController controller, EquipmentController.Equipment kept, XRNode keptBy, EquipmentController.Equipment next)
        {
            equipment = controller;
            VrHand.KeepSecond(VrHand.Of(keptBy), kept);
            Invoke(SetEquipment, next);
            Invoke(PlayEquipAudio, next);
            if (logs++ < 60) Plugin.Log.LogInfo($"Two tools: {Name(keptBy)} hand keeps the {kept}, the other hand draws the {next}");
        }

        // Next to the demo charges, which stay the game's tool: the cutter or the grapple is equipped as the second tool
        // through its own controller, without telling the game (which would put the charges away).
        public static void Add(EquipmentController controller, EquipmentController.Equipment tool, XRNode node)
        {
            equipment = controller;
            if (!FindTools()) return;
            VrHand.KeepSecond(VrHand.Of(node), tool);
            model = null;
            if (tool == EquipmentController.Equipment.CuttingTool && cutter != null) cutter.EquipCutter(false);
            else if (tool == EquipmentController.Equipment.GrappleHook && gun != null) gun.EquipGun(false);
            Invoke(PlayEquipAudio, tool);
            if (logs++ < 60) Plugin.Log.LogInfo($"Two tools: {Name(node)} hand adds the {tool} to the {controller.CurrentEquipment}");
        }

        // The second tool changes hands (the other hand reached for it).
        public static void TakeOver(XRNode node)
        {
            if (logs++ < 60) Plugin.Log.LogInfo($"Two tools: {Name(node)} hand takes the {Second} over");
            VrHand.KeepSecond(VrHand.Of(node), Second);
        }

        // The game's tool was let go while the second hand still holds its own: that one becomes the game's tool.
        public static void Promote()
        {
            if (!Find()) return;
            var tool = Second;
            if (logs++ < 60) Plugin.Log.LogInfo($"Two tools: the {equipment.CurrentEquipment} is put away, the {tool} in the {Name(SecondNode)} hand becomes the game's tool");
            VrHand.DropSecond(true);
            Invoke(PlayUnequipAudio, equipment.CurrentEquipment);
            Invoke(SetEquipment, tool);
        }

        // The second hand let go: its tool goes back to the belt through its own controller, which the game's
        // EquipmentController no longer looks after. Waits while the tool may not be put away (the cutter mid-cut).
        public static void PutAway(bool force = false)
        {
            var tool = Second;
            if (tool == EquipmentController.Equipment.None) return;
            var node = SecondNode;
            if (tool == EquipmentController.Equipment.CuttingTool)
            {
                if (!FindTools() || cutter == null) { VrHand.DropSecond(); return; }
                if (!force && !cutter.CanBeUnequipped()) return;
                VrHand.DropSecond();
                if (Find()) Invoke(PlayUnequipAudio, tool);
                cutter.UnequipCutter();
            }
            else
            {
                if (!FindTools() || gun == null) { VrHand.DropSecond(); return; }
                if (!force && !gun.CanBeUnequipped()) return;
                VrHand.DropSecond();
                if (Find()) Invoke(PlayUnequipAudio, tool);
                gun.UnequipGun();
            }
            model = null;
            if (logs++ < 60) Plugin.Log.LogInfo($"Two tools: the {tool} in the {Name(node)} hand is put away");
        }

        // The tool the scanner hid, and the hand that held it.
        public static EquipmentController.Equipment Suspended => VrHand.Hidden;
        public static XRNode SuspendedNode => VrHand.HiddenHand?.Node ?? XRNode.RightHand;

        // The scanner is drawn while the other hand holds the game's tool: the game puts that tool away, and it comes back
        // after the scan (Resume).
        public static void Suspend(EquipmentController.Equipment tool, XRNode node)
        {
            if (!Enabled) return;
            VrHand.Hide(VrHand.Of(node), tool);
            if (logs++ < 60) Plugin.Log.LogInfo($"Two tools: the scanner hides the {tool} in the {Name(node)} hand");
        }

        // The hand that held the hidden tool let go during the scan: it stays put away.
        public static void Forget()
        {
            if (Suspended == EquipmentController.Equipment.None) return;
            if (logs++ < 60) Plugin.Log.LogInfo($"Two tools: the {Suspended} hidden by the scanner stays put away");
            VrHand.Unhide();
        }

        // The scanner is put away: the hidden tool comes back into its hand instead of an empty hand. False without one.
        public static bool Resume(EquipmentController controller)
        {
            var tool = Suspended;
            var node = SuspendedNode;
            var back = tool != EquipmentController.Equipment.None && Enabled;
            VrHand.Unhide(back);
            if (!back) return false;
            equipment = controller;
            Invoke(SetEquipment, tool);
            Invoke(PlayEquipAudio, tool);
            if (logs++ < 60) Plugin.Log.LogInfo($"Two tools: the scanner is put away, the {tool} comes back into the {Name(node)} hand");
            return true;
        }

        // From HandPatch: the second tool re-equipping itself hangs on its own point, not on the game's right hand.
        public static Transform HoldPoint(GrabController grab)
        {
            var equipmentPoint = grab != null && grab.RightHand != null ? grab.RightHand.EquipmentGeoTransform : null;
            if (equipmentPoint != null) EnsurePoint(equipmentPoint);
            return point;
        }

        // Once a frame from VrToolInput.Update: the second tool ends with the option, outside gameplay (pausing keeps it), and
        // when the game itself took it as its tool.
        public static void Check()
        {
            if (Second == EquipmentController.Equipment.None) return;
            var state = GameSession.CurrentGameState;
            if (!Enabled || !Plugin.AimWithHand.Value || (state != GameSession.GameState.Gameplay && state != GameSession.GameState.Paused))
            {
                PutAway(true);
                return;
            }
            if (Find() && !equipment.IsNothingEquipped && equipment.CurrentEquipment == Second)
            {
                if (logs++ < 60) Plugin.Log.LogInfo($"Two tools: the game took the {Second} as its tool");
                VrHand.DropSecond();
            }
        }

        private static void Invoke(MethodInfo method, EquipmentController.Equipment tool)
        {
            try { method.Invoke(equipment, new object[] { tool }); }
            catch (System.Exception e) { Plugin.Log.LogWarning($"Two tools: {method.Name}({tool}) failed: {e.InnerException ?? e}"); }
        }

        private static bool Find()
        {
            if (equipment == null) equipment = GameRefs.Equipment;
            return equipment != null;
        }

        private static bool FindTools()
        {
            if (cutter == null) cutter = GameRefs.Cutter;
            if (gun == null) gun = GameRefs.Gun;
            return cutter != null || gun != null;
        }

        private static string Name(XRNode node) => node == XRNode.LeftHand ? "left" : "right";

        // ---- The second tool's model, aim and laser ----

        private static Transform point;
        private static Transform model;
        private static bool placed;
        private static Vector3 mount;
        private static Quaternion turn = Quaternion.identity;
        private static Vector3 muzzleOffset;
        private static bool hasMuzzle;
        private static float nextMirror;
        private static Camera aim;
        private static bool aimValid;

        // The game's model of the second tool.
        private static Transform Model(EquipmentController.Equipment tool)
        {
            if (!FindTools()) return null;
            if (tool == EquipmentController.Equipment.CuttingTool) return cutter != null ? CutterModel(cutter) : null;
            if (tool == EquipmentController.Equipment.GrappleHook) return gun != null ? GrappleModel(gun) : null;
            return null;
        }

        // The controller of the hand that holds the second tool.
        private static VrInput.Hand Hand => VrInput.Of(SecondNode);

        // From VrToolModels.Place, right before rendering, after the first tool is placed: the second tool's model onto
        // its own point (a sibling of the game's equipment point, so it lives and scales with the player), which goes to
        // the second hand as the first tool's point goes to the first one.
        public static void Place(Transform equipmentPoint)
        {
            placed = false;
            hasMuzzle = false;
            if (Second == EquipmentController.Equipment.None || equipmentPoint == null || equipmentPoint.parent == null) return;
            var tool = Model(Second);
            if (tool == null || !VrAim.TryGetHand(Hand, out var grip, out var rotation)) return;
            EnsurePoint(equipmentPoint);
            if (tool != model || tool.parent != point)
            {
                model = tool;
                tool.SetParent(point, false);
                tool.localPosition = Vector3.zero;
                tool.localRotation = Quaternion.identity;
                Plugin.Log.LogInfo($"Two tools: '{tool.name}' into the {Name(SecondNode)} hand");
            }
            if (Second == EquipmentController.Equipment.CuttingTool && Time.unscaledTime >= nextMirror)
            {
                nextMirror = Time.unscaledTime + 1f;
                VrToolMirror.Apply(point);
            }

            var muzzle = VrToolModels.MuzzleTransform(Second, out var muzzleWorld);
            hasMuzzle = muzzle != null && muzzle.IsChildOf(point);
            if (hasMuzzle) muzzleOffset = Quaternion.Inverse(point.rotation) * (muzzleWorld - point.position);
            var fitted = VrToolFit.TryGet(Second, SecondNode == XRNode.LeftHand, out var offset, out turn);
            mount = fitted ? VrInput.WristBehindGrip * VrCamera.WorldScale + offset : Vector3.zero;
            point.rotation = rotation * turn;
            point.position = grip + rotation * mount;
            placed = true;
        }

        // A point of its own for the second tool, a sibling of the game's equipment point.
        private static void EnsurePoint(Transform equipmentPoint)
        {
            if (point != null && point.parent == equipmentPoint.parent) return;
            if (point != null) Object.Destroy(point.gameObject);
            point = new GameObject("ShipbreakerVR Second tool point").transform;
            point.SetParent(equipmentPoint.parent, false);
            point.localScale = equipmentPoint.localScale;
        }

        // Where the second tool aims from and along: its muzzle along its barrel, or the hand's aim pose until its model
        // is in the hand.
        public static bool TryGetAim(out Vector3 position, out Quaternion rotation)
        {
            position = Vector3.zero;
            rotation = Quaternion.identity;
            if (Second == EquipmentController.Equipment.None || !VrAim.TryGetPose(Hand, out position, out rotation)) return false;
            if (placed && hasMuzzle && VrAim.TryGetHand(Hand, out var grip, out var hand))
            {
                rotation = hand * turn;
                position = grip + hand * mount + rotation * muzzleOffset;
            }
            return true;
        }

        // The camera the second tool's code aims through (VrAim.CameraFor), null without a second tool. Placed once per
        // frame (VrFrame).
        public static Camera AimCamera => aimValid ? aim : null;

        // From VrFrame, after VrAim.
        public static void UpdateAim() => aimValid = PlaceAim();

        private static bool PlaceAim()
        {
            var main = LynxCameraController.MainCamera;
            if (main == null || !TryGetAim(out var position, out var rotation)) return false;
            if (aim == null)
            {
                var go = new GameObject("ShipbreakerVR Second aim");
                Object.DontDestroyOnLoad(go);
                aim = go.AddComponent<Camera>();
                aim.enabled = false;
                aim.stereoTargetEye = StereoTargetEyeMask.None;
            }
            aim.transform.SetPositionAndRotation(position, rotation);
            aim.fieldOfView = main.fieldOfView;
            aim.rect = main.rect;
            aim.aspect = main.aspect;
            aim.nearClipPlane = main.nearClipPlane;
            aim.farClipPlane = main.farClipPlane;
            return true;
        }

        // On unload (hot reload): the second tool goes back to the belt, so the new instance starts from the game's state.
        public static void Release()
        {
            PutAway(true);
            VrHand.Unhide();
            if (point != null)
            {
                // The game's tool model still hangs on the point: back onto the game's equipment point, where the game
                // equips it again, instead of being destroyed with the point.
                var grab = GameRefs.Grab;
                var home = grab != null && grab.RightHand != null && grab.RightHand.EquipmentGeoTransform != null
                    ? grab.RightHand.EquipmentGeoTransform : point.parent;
                for (var i = point.childCount - 1; i >= 0; i--) point.GetChild(i).SetParent(home, false);
                Object.Destroy(point.gameObject);
            }
            if (aim != null) Object.Destroy(aim.gameObject);
            point = null;
            model = null;
            aim = null;
            equipment = null;
            cutter = null;
            gun = null;
        }

        // For the F8 dump.
        public static string Describe() =>
            Second == EquipmentController.Equipment.None ? "second tool: none" : $"second tool: {Second} in the {Name(SecondNode)} hand{(placed ? "" : " (not placed)")}";

        // Puts the instructions that load a family in front of a redirected call, keeping the call's labels and blocks on
        // the first of them: the method's own family, or for code shared by several (RaycastSystem) its instance's.
        public static IEnumerable<CodeInstruction> LoadFamily(CodeInstruction call, MethodBase original)
        {
            var shared = !original.IsStatic && original.DeclaringType != null && original.DeclaringType.FullName == "BBI.Unity.Game.RaycastSystem";
            var load = shared
                ? new[] { new CodeInstruction(OpCodes.Ldarg_0), new CodeInstruction(OpCodes.Call, AccessTools.Method(typeof(VrDualTools), nameof(FamilyOfInstance))) }
                : new[] { new CodeInstruction(OpCodes.Ldc_I4, (int)FamilyOf(original.DeclaringType)) };
            load[0].labels.AddRange(call.labels);
            load[0].blocks.AddRange(call.blocks);
            call.labels.Clear();
            call.blocks.Clear();
            return load;
        }
    }

    // The game puts away the tool the second hand keeps (see VrDualTools).
    [HarmonyPatch]
    internal static class UnequipPatch
    {
        private static MethodBase TargetMethod() => AccessTools.Method(typeof(EquipmentController), "UnequipEquipment");

        private static bool Prefix(EquipmentController.Equipment equipmentType) => !VrDualTools.Keeps(equipmentType);
    }

    // The second tool re-equipping itself (EquipGun, EquipCutter) asks for the game's right hand, which holds the game's
    // tool; it gets its own point instead, and the right hand stays as it is (see VrDualTools).
    [HarmonyPatch]
    internal static class HandPatch
    {
        private static MethodBase TargetMethod() => AccessTools.Method(typeof(GrabController), nameof(GrabController.GetHandPositionTransformAndSetEquipment));

        private static bool Prefix(GrabController __instance, EquipmentController.Equipment equipmentType, ref Transform __result)
        {
            if (!VrDualTools.Keeps(equipmentType)) return true;
            __result = VrDualTools.HoldPoint(__instance);
            return __result == null;
        }
    }

    // Nor does it tell the game it is equipped, which would make the demo charges (DemoChargeController.OnEquipmentChanged)
    // and the HUD think they were put away.
    [HarmonyPatch]
    internal static class EquipPatch
    {
        private static IEnumerable<MethodBase> TargetMethods()
        {
            yield return AccessTools.Method(typeof(CuttingToolController), nameof(CuttingToolController.EquipCutter));
            yield return AccessTools.Method(typeof(GunController), nameof(GunController.EquipGun));
        }

        private static void Prefix(MethodBase __originalMethod, ref bool postEquipmentChangedEvent)
        {
            var tool = __originalMethod.DeclaringType == typeof(GunController) ? EquipmentController.Equipment.GrappleHook : EquipmentController.Equipment.CuttingTool;
            if (VrDualTools.Keeps(tool)) postEquipmentChangedEvent = false;
        }
    }

    // The tool classes check whether their tool is the equipped one; the second tool's family gets its own (VrDualTools).
    // A transpiler on the callers, because the getter is small enough for Mono to inline.
    [HarmonyPatch]
    internal static class GatePatch
    {
        private static readonly MethodInfo CurrentEquipment = AccessTools.PropertyGetter(typeof(EquipmentController), nameof(EquipmentController.CurrentEquipment));
        private static readonly MethodInfo TetherIsEquipped = AccessTools.PropertyGetter(typeof(TetherController), nameof(TetherController.IsEquipped));
        private static readonly MethodInfo Current = AccessTools.Method(typeof(VrDualTools), nameof(VrDualTools.CurrentEquipment));
        private static readonly MethodInfo TetherEquipped = AccessTools.Method(typeof(VrDualTools), nameof(VrDualTools.TetherEquipped));

        private static IEnumerable<MethodBase> TargetMethods() =>
            GameTools.MethodsCalling(GameTools.Redirect.Gate, "Two tools", new[] { CurrentEquipment, TetherIsEquipped });

        private static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> instructions, MethodBase original)
        {
            var replaced = 0;
            foreach (var instruction in instructions)
            {
                if (instruction.Calls(CurrentEquipment))
                {
                    foreach (var load in VrDualTools.LoadFamily(instruction, original)) yield return load;
                    instruction.opcode = OpCodes.Call;
                    instruction.operand = Current;
                    replaced++;
                }
                else if (instruction.Calls(TetherIsEquipped))
                {
                    instruction.opcode = OpCodes.Call;
                    instruction.operand = TetherEquipped;
                    replaced++;
                }
                yield return instruction;
            }
            PatchReport.Count("Two tools", original, replaced, $"equipment check(s) ({VrDualTools.FamilyOf(original.DeclaringType)})");
        }
    }
}
