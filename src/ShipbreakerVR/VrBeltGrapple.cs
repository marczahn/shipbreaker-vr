using System.Collections.Generic;
using BBI.Unity.Game;
using HarmonyLib;
using UnityEngine;

namespace ShipbreakerVR
{
    // While it is not in a hand (always, Raik 2026-11-08: no option), the game's own grapple hangs at the right hip, by where it
    // is drawn from (VrToolBelt), so one can look down and reach for it. The game puts a tool away by hanging its
    // model on a rest point of the player (GunController.UnequipGun: m_GrapplingGunRestPoint, its shadow model on
    // m_GrapplingGunShadowRestPoint) and lets it glide there (HandleGunPosition). Those points sit below the flat game's
    // view, so in VR the grapple and the cutter lay at one's feet, with a copy of the grapple at the hip besides (Raik,
    // 2026-11-08). Now the grapple's rest points are moved to the hip each frame: it is the one grapple, put away it glides
    // to the hip, and drawn it leaves from there. The cutter's rest points go onto the back the same way.
    // The demo charges' detonator shows at the other hip, where it is drawn from (Raik, 2026-11-18: "like the grapple,
    // only on the left side, on the belt, to be seen"). The game has no model of it but the prop on the hand, hidden
    // while the charges are put away: copies of that prop's meshes show it, upright, turned as a hand there with its
    // forearm forward would hold it, while it is neither in a hand nor afloat (VrLooseTools).
    internal static class VrBeltGrapple
    {
        static VrBeltGrapple() => VrSession.OnUnload("belt grapple", Release);

        private const string ModelPrefix = "PRFB_GrappleGun";
        private const string Grip = "RHandTarget";
        private const string Body = "GB_GrappleTool_Main_1";

        // Where the grapple's body hangs, from the head along its heading (x right, y up, z forward), in real meters, and
        // how far its muzzle points forward from straight down, in degrees: where Raik held it, as he would like it to
        // hang (hand snapshot 2026-11-08 13:31:16, looking down about 58°). Hung by its grip at the middle of the zone
        // (VrToolBelt.GripAt) its body lay 23 cm lower and 26 cm further forward, too far away (Raik). Then 10 cm further
        // out to the right, and 3 cm further right, forward and up (Raik). Then 7 cm further forward, to see and reach it
        // better (Raik, 2026-11-18).
        // Both tools hang on the body, not on the head (Raik, 2026-11-18): the places stay written as from the head, for
        // the head as it was when they were found (VrToolBelt.HipRef, ShoulderRef), and are taken from the neck along the
        // heading the body keeps (VrToolBelt.BodyFrame), so the head looks around freely without moving them.
        // Raik (2026-11-26): placed in the headset with VrToolPlace, which keeps these as the mod's places.
        internal static readonly Vector3 BodyAt = new Vector3(0.40f, -0.69f, -0.09f);
        internal const float MuzzleForward = 25f;
        // Where the cutter's grip hangs on the back, in the same axes, within the zone over the right shoulder it is drawn
        // from (VrToolBelt: above -0.12 m, behind -0.05 m), and how far its muzzle points towards the left, in degrees.
        private static readonly Vector3 CutterGripAt = new Vector3(0.15f, -0.08f, -0.22f);
        private const float CutterSlant = 20f;

        // Where the detonator's middle hangs, in the axes its zone is measured in (VrToolBelt.ChargeRef; mirrored with
        // the tools for the left-handed). Raik, 2026-11-18: "into the middle of the grip's field, or at least a little
        // closer to the body and a little further back"; it hung by a wrist placed in the grapple's axes before. The
        // zone's middle lies 20 cm to the side, 65 cm down and 11 cm back; the detonator hangs 3 cm further out than
        // that, so it does not sit in the suit's hip.
        // There it was out of sight (Raik, the same day): at the neck's line beside the body, 11 cm higher and some
        // 10 cm further back than where he had seen it before. The zone went 10 cm forward (VrToolBelt), and the
        // detonator hangs near its middle again: 26 cm to the side, 10 cm in front of the neck's line (as far forward
        // as the grapple's body on the other side) and 55 cm below the neck. Then 7 cm further forward still (Raik).
        internal static readonly Vector3 DetonatorAt = new Vector3(-0.26f, -0.72f, 0.07f);

        // For VrToolPlace: the grapple hangs on its rest point at the hip, the detonator's copy shows there.
        internal static bool GrappleHanging { get; private set; }
        internal static bool DetonatorHanging => detonatorPlaced && detonatorShown != null && detonatorShown.activeSelf;
        private static GameObject detonatorShown;
        // The middle of its parts, in its own axes; the prop's size, which the copy's own frame does not have.
        private static Vector3 detonatorMiddle;
        private static Vector3 detonatorSize = Vector3.one;
        private static bool detonatorPlaced;
        private static Transform detonatorOf;
        private static float nextDetonator;

        // How fast the grapple glides onto its rest point, per second, as the game's spring may only run while it is held.
        private const float Glide = 10f;

        private static readonly AccessTools.FieldRef<GunController, Transform> Gun =
            AccessTools.FieldRefAccess<GunController, Transform>("m_GrapplingGun");
        private static readonly AccessTools.FieldRef<GunController, Transform> GunRest =
            AccessTools.FieldRefAccess<GunController, Transform>("m_GrapplingGunRestPoint");
        private static readonly AccessTools.FieldRef<GunController, Transform> Shadow =
            AccessTools.FieldRefAccess<GunController, Transform>("m_GrapplingGunShadow");
        private static readonly AccessTools.FieldRef<GunController, Transform> ShadowRest =
            AccessTools.FieldRefAccess<GunController, Transform>("m_GrapplingGunShadowRestPoint");
        private static readonly AccessTools.FieldRef<CuttingToolController, Transform> Cutter =
            AccessTools.FieldRefAccess<CuttingToolController, Transform>("m_CuttingTool");
        private static readonly AccessTools.FieldRef<CuttingToolController, Transform> CutterRest =
            AccessTools.FieldRefAccess<CuttingToolController, Transform>("m_ToolRestPoint");
        private static readonly AccessTools.FieldRef<CuttingToolController, Transform> CutterShadow =
            AccessTools.FieldRefAccess<CuttingToolController, Transform>("m_CuttingToolShadow");
        private static readonly AccessTools.FieldRef<CuttingToolController, Transform> CutterShadowRest =
            AccessTools.FieldRefAccess<CuttingToolController, Transform>("m_CuttingToolShadowRestPoint");

        private static GunController gun;
        private static CuttingToolController cutter;
        private static Transform model;
        private static Transform grip;
        private static Transform body;
        private static Vector3 bodyCenter;
        private static float nextFind;
        private static int logs;

        // A rest point moved here, with its own pose to put back.
        private struct Moved
        {
            public Transform Point;
            public Vector3 Position;
            public Quaternion Rotation;
        }

        private static readonly List<Moved> moved = new List<Moved>();

        public static void Update()
        {
            var vr = VrCamera.Instance;
            if (!VrAim.Active || vr == null || vr.FollowsHab || !Find())
            {
                Hide();
                return;
            }
            if (!VrToolBelt.BodyFrame(out var neck, out var heading)) return;

            // The grapple: muzzle down along the leg and a little forward, the grip to the back, hung by its body.
            var gunModel = Gun(gun);
            var rest = GunRest(gun);
            // Where it hangs may be set in the headset (VrToolPlace); the mod's place is BodyAt, muzzle MuzzleForward.
            VrToolPlace.Grapple(out var bodyAt, out var bodyTurn);
            var hanging = heading * VrToolPlace.Sided(bodyTurn);
            GrappleHanging = gunModel != null && rest != null && gunModel.parent == rest && !VrLooseTools.IsLoose(EquipmentController.Equipment.GrappleHook);
            if (gunModel != null && rest != null && !VrLooseTools.IsLoose(EquipmentController.Equipment.GrappleHook)
                && VrToolModels.TrackingToWorld(neck + heading * (VrToolBelt.Sided(bodyAt) - VrToolBelt.HipRef), hanging, out var at, out var rotation))
            {
                var modelInGun = Quaternion.Inverse(gunModel.rotation) * model.rotation;
                Hang(gunModel, rest, Shadow(gun), ShadowRest(gun), at, rotation * Quaternion.Inverse(modelInGun),
                    gunModel.InverseTransformPoint(body.TransformPoint(bodyCenter)));
                if (logs++ % 2000 == 0 && gunModel.parent == rest) LogGrip(vr.transform.parent, neck, heading);
            }

            // The cutter on the back, like a sword: its grip by the right shoulder, where it is drawn from (VrToolBelt), the
            // muzzle down towards the left hip, its top away from the back. Hung by its pivot, where the hand holds it.
            var cutterModel = cutter != null ? Cutter(cutter) : null;
            var cutterRest = cutter != null ? CutterRest(cutter) : null;
            var onBack = heading * Quaternion.Euler(0f, 0f, VrToolBelt.LeftHanded ? CutterSlant : -CutterSlant) * Quaternion.LookRotation(Vector3.down, Vector3.back);
            if (cutterModel != null && cutterRest != null && !VrLooseTools.IsLoose(EquipmentController.Equipment.CuttingTool)
                && VrToolModels.TrackingToWorld(neck + heading * (VrToolBelt.Sided(CutterGripAt) - VrToolBelt.ShoulderRef), onBack, out var cutterAt, out var cutterRotation))
                Hang(cutterModel, cutterRest, CutterShadow(cutter), CutterShadowRest(cutter), cutterAt, cutterRotation, Vector3.zero);

            HangDetonator(neck, heading);
        }

        // The detonator's copy at the hip, while the player has the charges and the detonator is put away.
        private static void HangDetonator(Vector3 neck, Quaternion heading)
        {
            var prop = VrLooseTools.DetonatorModel;
            var equipment = GameRefs.Equipment;
            var inHandAt = Vector3.zero;
            var inHandTurn = Quaternion.identity;
            var wrist = Vector3.zero;
            var frame = Quaternion.identity;
            // Its place is kept also while it does not show (in a hand, afloat): where it is put back (DetonatorAtHip).
            VrToolPlace.Detonator(out var detonatorAt, out var detonatorTurn);
            var placed = prop != null && equipment != null && equipment.CanEquip(EquipmentController.Equipment.DemoCharge)
                && VrHands.DetonatorRest(out inHandAt, out inHandTurn)
                && VrToolModels.TrackingToWorld(neck + heading * (VrToolBelt.Sided(detonatorAt) - VrToolBelt.ChargeRef), heading * VrToolPlace.Sided(detonatorTurn), out wrist, out frame);
            var shows = placed && !VrLooseTools.DetonatorShowsInHand && !VrLooseTools.DetonatorLoose;
            if (placed && (detonatorShown == null || detonatorOf != prop) && Time.unscaledTime >= nextDetonator)
            {
                nextDetonator = Time.unscaledTime + 2f;
                BuildDetonator(prop);
            }
            detonatorPlaced = placed && detonatorShown != null;
            if (!detonatorPlaced)
            {
                if (detonatorShown != null && detonatorShown.activeSelf) detonatorShown.SetActive(false);
                return;
            }
            // Turned as in a hand there, its middle at the place.
            var turned = frame * inHandTurn;
            detonatorShown.transform.SetPositionAndRotation(wrist - turned * detonatorMiddle, turned);
            if (detonatorShown.activeSelf != shows) detonatorShown.SetActive(shows);
        }

        // For VrDetonatorLights: the copy at the hip, while there is one.
        internal static Transform DetonatorCopy => detonatorShown != null ? detonatorShown.transform : null;

        // For VrLooseTools: where a point of the detonator's model (in the prop's own axes) is on the copy at the hip,
        // in the world; shown: whether the copy shows there now. False while it has no place at the hip.
        internal static bool DetonatorAtHip(Vector3 inModel, out Vector3 at, out bool shown)
        {
            at = Vector3.zero;
            shown = false;
            if (!detonatorPlaced || detonatorShown == null) return false;
            at = detonatorShown.transform.TransformPoint(Vector3.Scale(detonatorSize, inModel));
            shown = detonatorShown.activeSelf;
            return true;
        }

        private static void BuildDetonator(Transform prop)
        {
            if (detonatorShown != null) Object.Destroy(detonatorShown);
            detonatorShown = null;
            detonatorOf = null;
            var root = CopyDetonator(prop, "ShipbreakerVR detonator at the hip", out var parts, out var middle);
            if (root == null) return;
            detonatorMiddle = middle;
            detonatorSize = prop.lossyScale;
            detonatorShown = root;
            detonatorOf = prop;
            Plugin.Log.LogInfo($"Belt grapple: the detonator shows at the hip, {parts} part(s) copied from '{prop.name}', its middle {detonatorMiddle.magnitude / VrCamera.WorldScale * 100f:0} cm from its origin, "
                + $"hung at ({DetonatorAt.x:0.00}, {DetonatorAt.y:0.00}, {DetonatorAt.z:0.00}) m as its zone is measured");
        }

        // A copy of the detonator's meshes, its root at the prop's origin and turned as the prop; middle: the middle of
        // its parts in the root's axes. Made inactive; null without parts.
        internal static GameObject CopyDetonator(Transform prop, string name, out int parts, out Vector3 middle)
        {
            var root = new GameObject(name);
            root.transform.SetPositionAndRotation(prop.position, prop.rotation);
            parts = 0;
            var sum = Vector3.zero;
            foreach (var filter in prop.GetComponentsInChildren<MeshFilter>(true))
            {
                var source = filter.GetComponent<MeshRenderer>();
                if (source == null || filter.sharedMesh == null) continue;
                var copy = new GameObject(filter.name) { layer = filter.gameObject.layer };
                copy.transform.SetParent(root.transform, false);
                copy.transform.SetPositionAndRotation(filter.transform.position, filter.transform.rotation);
                copy.transform.localScale = filter.transform.lossyScale;
                copy.AddComponent<MeshFilter>().sharedMesh = filter.sharedMesh;
                var shown = copy.AddComponent<MeshRenderer>();
                shown.sharedMaterials = source.sharedMaterials;
                shown.shadowCastingMode = source.shadowCastingMode;
                shown.receiveShadows = source.receiveShadows;
                sum += copy.transform.TransformPoint(filter.sharedMesh.bounds.center);
                parts++;
            }
            middle = Vector3.zero;
            if (parts == 0)
            {
                Object.Destroy(root);
                return null;
            }
            middle = root.transform.InverseTransformPoint(sum / parts);
            root.SetActive(false);
            return root;
        }

        // Moves a tool's rest points so that the tool, once it lies on them, has its anchor (in its own axes) at the point
        // and its rest point turned so.
        private static void Hang(Transform tool, Transform rest, Transform shadow, Transform shadowRest, Vector3 at, Quaternion rotation, Vector3 anchor)
        {
            Remember(rest);
            rest.rotation = rotation;
            rest.position = at - rotation * Vector3.Scale(Vector3.Scale(rest.lossyScale, tool.localScale), anchor);
            if (shadowRest != null)
            {
                Remember(shadowRest);
                shadowRest.SetPositionAndRotation(rest.position, rest.rotation);
            }
            Settle(tool, rest);
            Settle(shadow, shadowRest);
        }

        // Where the grip hangs against the zone it is drawn from, against the body as VrToolBelt measures the hip.
        private static void LogGrip(Transform rig, Vector3 neck, Quaternion heading)
        {
            if (rig == null || grip == null) return;
            var local = Quaternion.Inverse(heading) * (rig.InverseTransformPoint(grip.position) - neck) + VrToolBelt.HipRef;
            Plugin.Log.LogInfo($"Belt grapple: its grip hangs at ({local.x:0.00}, {local.y:0.00}, {local.z:0.00}) m from the head (as when looking down at the hip; on the body, {(VrTorso.Upright ? "its own heading" : "the head's heading")}), "
                + $"its zone around ({VrToolBelt.GripAt.x:0.00}, {VrToolBelt.GripAt.y:0.00}, {VrToolBelt.GripAt.z:0.00})");
        }

        // The grapple glides onto its rest point once it lies on it.
        private static void Settle(Transform tool, Transform rest)
        {
            if (tool == null || rest == null || tool.parent != rest) return;
            var t = 1f - Mathf.Exp(-Glide * Time.unscaledDeltaTime);
            tool.localPosition = Vector3.Lerp(tool.localPosition, Vector3.zero, t);
            tool.localRotation = Quaternion.Slerp(tool.localRotation, Quaternion.identity, t);
        }

        // Experiment (VrLooseTools): a tool's model and rest points as the game keeps them; false while they are not there.
        internal static bool Parts(EquipmentController.Equipment kind, out Transform tool, out Transform rest, out Transform shadow, out Transform shadowRest)
        {
            tool = rest = shadow = shadowRest = null;
            if (kind == EquipmentController.Equipment.GrappleHook)
            {
                var controller = GameRefs.Gun;
                if (controller == null) return false;
                tool = Gun(controller);
                rest = GunRest(controller);
                shadow = Shadow(controller);
                shadowRest = ShadowRest(controller);
            }
            else if (kind == EquipmentController.Equipment.CuttingTool)
            {
                var controller = GameRefs.Cutter;
                if (controller == null) return false;
                tool = Cutter(controller);
                rest = CutterRest(controller);
                shadow = CutterShadow(controller);
                shadowRest = CutterShadowRest(controller);
            }
            return tool != null && rest != null;
        }

        internal static void Remember(Transform point)
        {
            foreach (var m in moved)
                if (m.Point == point) return;
            moved.Add(new Moved { Point = point, Position = point.localPosition, Rotation = point.localRotation });
        }

        private static bool Find()
        {
            if (gun != null && model != null && grip != null && body != null)
            {
                // The cutter may come later than the grapple (just after loading into a ship).
                if (cutter == null) cutter = GameRefs.Cutter;
                return true;
            }
            if (Time.unscaledTime < nextFind) return false;
            nextFind = Time.unscaledTime + 1f;
            Release();
            gun = GameRefs.Gun;
            cutter = GameRefs.Cutter;
            var hook = GameRefs.Grapple;
            var part = hook != null ? VrToolModels.GrappleBarrelOf(hook) : null;
            while (part != null && !part.name.StartsWith(ModelPrefix)) part = part.parent;
            model = part;
            if (model != null)
                foreach (var target in model.GetComponentsInChildren<Transform>(true))
                {
                    if (target.name == Grip) grip = target;
                    var filter = target.name == Body ? target.GetComponent<MeshFilter>() : null;
                    if (filter != null && filter.sharedMesh is Mesh mesh)
                    {
                        body = target;
                        bodyCenter = mesh.bounds.center;
                    }
                }
            return gun != null && model != null && grip != null && body != null;
        }

        // The rest points back where the game had them, and the cutter shown; also when VR stops, as Update then no
        // longer runs.
        public static void Hide()
        {
            GrappleHanging = false;
            if (detonatorShown != null && detonatorShown.activeSelf) detonatorShown.SetActive(false);
            detonatorPlaced = false;
            // A tool afloat keeps its rest points where it floats (VrLooseTools); they go back once it is on the body again.
            foreach (var m in moved)
                if (m.Point != null && !VrLooseTools.Holds(m.Point))
                {
                    m.Point.localPosition = m.Position;
                    m.Point.localRotation = m.Rotation;
                }
            moved.RemoveAll(m => m.Point == null || !VrLooseTools.Holds(m.Point));
        }

        private static void Release()
        {
            Hide();
            if (detonatorShown != null) Object.Destroy(detonatorShown);
            detonatorShown = null;
            detonatorOf = null;
            gun = null;
            cutter = null;
            model = null;
            grip = null;
            body = null;
        }
    }
}
