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
        private static readonly Vector3 BodyAt = new Vector3(0.40f, -0.69f, -0.09f);
        private const float MuzzleForward = 25f;
        // Where the cutter's grip hangs on the back, in the same axes, within the zone over the right shoulder it is drawn
        // from (VrToolBelt: above -0.12 m, behind -0.05 m), and how far its muzzle points towards the left, in degrees.
        private static readonly Vector3 CutterGripAt = new Vector3(0.15f, -0.08f, -0.22f);
        private const float CutterSlant = 20f;

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
            var hanging = heading * Quaternion.Euler(-MuzzleForward, 0f, 0f) * Quaternion.LookRotation(Vector3.down, Vector3.forward);
            if (gunModel != null && rest != null && !VrLooseTools.IsLoose(EquipmentController.Equipment.GrappleHook)
                && VrToolModels.TrackingToWorld(neck + heading * (BodyAt - VrToolBelt.HipRef), hanging, out var at, out var rotation))
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
            var onBack = heading * Quaternion.Euler(0f, 0f, -CutterSlant) * Quaternion.LookRotation(Vector3.down, Vector3.back);
            if (cutterModel != null && cutterRest != null && !VrLooseTools.IsLoose(EquipmentController.Equipment.CuttingTool)
                && VrToolModels.TrackingToWorld(neck + heading * (CutterGripAt - VrToolBelt.ShoulderRef), onBack, out var cutterAt, out var cutterRotation))
                Hang(cutterModel, cutterRest, CutterShadow(cutter), CutterShadowRest(cutter), cutterAt, cutterRotation, Vector3.zero);
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
            gun = null;
            cutter = null;
            model = null;
            grip = null;
            body = null;
        }
    }
}
