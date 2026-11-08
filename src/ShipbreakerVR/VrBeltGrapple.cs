using System.Collections.Generic;
using BBI.Unity.Game;
using HarmonyLib;
using UnityEngine;

namespace ShipbreakerVR
{
    // Steuerung/GrapplerAmKoerper: while it is not in a hand, the game's own grapple hangs at the right hip, by where it
    // is drawn from (VrToolBelt), so one can look down and reach for it. The game puts a tool away by hanging its
    // model on a rest point of the player (GunController.UnequipGun: m_GrapplingGunRestPoint, its shadow model on
    // m_GrapplingGunShadowRestPoint) and lets it glide there (HandleGunPosition). Those points sit below the flat game's
    // view, so in VR the grapple and the cutter lay at one's feet, with a copy of the grapple at the hip besides (Raik,
    // 2026-11-08). Now the grapple's rest points are moved to the hip each frame: it is the one grapple, put away it glides
    // to the hip, and drawn it leaves from there. The cutter, not yet hung anywhere, is not drawn while it rests.
    internal static class VrBeltGrapple
    {
        static VrBeltGrapple() => VrSession.OnUnload("belt grapple", Release);

        private const string ModelPrefix = "PRFB_GrappleGun";
        private const string Grip = "RHandTarget";
        private const string Body = "GB_GrappleTool_Main_1";

        // Where the grapple's body hangs, from the head along its heading (x right, y up, z forward), in real meters, and
        // how far its muzzle points forward from straight down, in degrees: where Raik held it, as he would like it to
        // hang (hand snapshot 2026-11-08 13:31:16, looking down about 58°). Hung by its grip at the middle of the zone
        // (VrToolBelt.GripAt) its body lay 23 cm lower and 26 cm further forward, too far away (Raik).
        private static readonly Vector3 BodyAt = new Vector3(0.27f, -0.72f, -0.19f);
        private const float MuzzleForward = 25f;

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
        private static readonly List<Renderer> hidden = new List<Renderer>();
        private static readonly List<Renderer> found = new List<Renderer>();

        public static void Update()
        {
            var vr = VrCamera.Instance;
            if (!VrSettings.BeltGrapple.Value || !VrAim.Active || vr == null || vr.FollowsHab || !Find())
            {
                Hide();
                return;
            }
            HideResting();
            var forward = vr.transform.localRotation * Vector3.forward;
            forward.y = 0f;
            if (forward.sqrMagnitude < 1e-4f) return;
            var heading = Quaternion.LookRotation(forward, Vector3.up);
            // Muzzle down along the leg and a little forward, the grip to the back.
            var hanging = heading * Quaternion.Euler(-MuzzleForward, 0f, 0f) * Quaternion.LookRotation(Vector3.down, Vector3.forward);
            if (!VrToolModels.TrackingToWorld(vr.transform.localPosition + heading * BodyAt, hanging, out var at, out var rotation))
                return;

            var gunModel = Gun(gun);
            var rest = GunRest(gun);
            if (gunModel == null || rest == null) return;
            // The rest point turned so the model hangs as wanted once the grapple lies on it, and placed so its body is here.
            var modelInGun = Quaternion.Inverse(gunModel.rotation) * model.rotation;
            var bodyInGun = gunModel.InverseTransformPoint(body.TransformPoint(bodyCenter));
            Remember(rest);
            rest.rotation = rotation * Quaternion.Inverse(modelInGun);
            rest.position = at - rest.rotation * Vector3.Scale(Vector3.Scale(rest.lossyScale, gunModel.localScale), bodyInGun);
            var shadowRest = ShadowRest(gun);
            if (shadowRest != null)
            {
                Remember(shadowRest);
                shadowRest.SetPositionAndRotation(rest.position, rest.rotation);
            }
            Settle(gunModel, rest);
            Settle(Shadow(gun), shadowRest);
            if (logs++ % 2000 == 0 && gunModel.parent == rest) LogGrip(vr.transform, heading);
        }

        // Where the grip hangs against the zone it is drawn from, in the head's heading as VrToolBelt measures.
        private static void LogGrip(Transform head, Quaternion heading)
        {
            var rig = head.parent;
            if (rig == null || grip == null) return;
            var local = Quaternion.Inverse(heading) * (rig.InverseTransformPoint(grip.position) - head.localPosition);
            Plugin.Log.LogInfo($"Belt grapple: its grip hangs at ({local.x:0.00}, {local.y:0.00}, {local.z:0.00}) m from the head, its zone around ({VrToolBelt.GripAt.x:0.00}, {VrToolBelt.GripAt.y:0.00}, {VrToolBelt.GripAt.z:0.00})");
        }

        // The grapple glides onto its rest point once it lies on it.
        private static void Settle(Transform tool, Transform rest)
        {
            if (tool == null || rest == null || tool.parent != rest) return;
            var t = 1f - Mathf.Exp(-Glide * Time.unscaledDeltaTime);
            tool.localPosition = Vector3.Lerp(tool.localPosition, Vector3.zero, t);
            tool.localRotation = Quaternion.Slerp(tool.localRotation, Quaternion.identity, t);
        }

        // The cutter is not drawn while it lies on its rest point (at the feet); its renderers are left as the game set them.
        private static void HideResting()
        {
            var rest = cutter != null ? CutterRest(cutter) : null;
            var tool = cutter != null ? Cutter(cutter) : null;
            if (tool == null || rest == null || !tool.IsChildOf(rest))
            {
                ShowCutter();
                return;
            }
            if (hidden.Count > 0) return;
            tool.GetComponentsInChildren(true, found);
            hidden.AddRange(found);
            var shadow = CutterShadow(cutter);
            var shadowRest = CutterShadowRest(cutter);
            if (shadow != null && shadowRest != null && shadow.IsChildOf(shadowRest))
            {
                shadow.GetComponentsInChildren(true, found);
                hidden.AddRange(found);
            }
            foreach (var renderer in hidden) renderer.forceRenderingOff = true;
        }

        private static void ShowCutter()
        {
            foreach (var renderer in hidden)
                if (renderer != null) renderer.forceRenderingOff = false;
            hidden.Clear();
        }

        private static void Remember(Transform point)
        {
            foreach (var m in moved)
                if (m.Point == point) return;
            moved.Add(new Moved { Point = point, Position = point.localPosition, Rotation = point.localRotation });
        }

        private static bool Find()
        {
            if (gun != null && model != null && grip != null && body != null) return true;
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
                    if (target.name == Body && target.GetComponent<MeshFilter>()?.sharedMesh is Mesh mesh)
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
            foreach (var m in moved)
                if (m.Point != null)
                {
                    m.Point.localPosition = m.Position;
                    m.Point.localRotation = m.Rotation;
                }
            moved.Clear();
            ShowCutter();
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
