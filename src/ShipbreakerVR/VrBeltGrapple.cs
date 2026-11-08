using System.Collections.Generic;
using BBI.Unity.Game;
using HarmonyLib;
using UnityEngine;

namespace ShipbreakerVR
{
    // Steuerung/GrapplerAmKoerper: while it is not in a hand, the game's own grapple hangs at the right hip with its grip
    // where it is drawn from (VrToolBelt), so one can look down and reach for it. The game puts a tool away by hanging its
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
            // Muzzle down along the leg, the grip to the back.
            var hanging = heading * Quaternion.LookRotation(Vector3.down, Vector3.forward);
            if (!VrToolModels.TrackingToWorld(vr.transform.localPosition + heading * VrToolBelt.GripAt, hanging, out var at, out var rotation))
                return;

            var gunModel = Gun(gun);
            var rest = GunRest(gun);
            if (gunModel == null || rest == null) return;
            // The rest point turned so the model hangs as wanted once the grapple lies on it, and placed so its grip is here.
            var modelInGun = Quaternion.Inverse(gunModel.rotation) * model.rotation;
            var gripInGun = gunModel.InverseTransformPoint(grip.position);
            Remember(rest);
            rest.rotation = rotation * Quaternion.Inverse(modelInGun);
            rest.position = at - rest.rotation * Vector3.Scale(Vector3.Scale(rest.lossyScale, gunModel.localScale), gripInGun);
            var shadowRest = ShadowRest(gun);
            if (shadowRest != null)
            {
                Remember(shadowRest);
                shadowRest.SetPositionAndRotation(rest.position, rest.rotation);
            }
            Settle(gunModel, rest);
            Settle(Shadow(gun), shadowRest);
            if (logs++ == 0) Plugin.Log.LogInfo($"Belt grapple: the game's grapple rests at the hip, grip at {gripInGun} of it");
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
            if (gun != null && model != null && grip != null) return true;
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
                    if (target.name == Grip) grip = target;
            return gun != null && model != null && grip != null;
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
        }
    }
}
