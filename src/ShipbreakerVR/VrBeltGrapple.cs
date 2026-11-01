using BBI.Unity.Game;
using UnityEngine;

namespace ShipbreakerVR
{
    // Steuerung/GrapplerAmKoerper: while it is not in a hand, the grapple hangs at the right hip with its grip in the zone it
    // is drawn from (VrToolBelt), so one can look down and reach for it. It is a copy of the visible parts of the game's
    // model at runtime, built from the same meshes and materials; the game's model stays where it is, hidden.
    internal static class VrBeltGrapple
    {
        static VrBeltGrapple() => VrSession.OnUnload("belt grapple", Release);

        private const string ModelPrefix = "PRFB_GrappleGun";
        private const string Grip = "RHandTarget";

        // In the grapple's zone, from the head along its heading (x right, y up, z forward), in real meters.
        private static readonly Vector3 Hip = new Vector3(0.19f, -0.62f, -0.35f);

        private static Transform copy;
        private static Transform model;
        private static Vector3 grip;

        public static void Update()
        {
            var vr = VrCamera.Instance;
            var free = VrToolModels.Equipped != EquipmentController.Equipment.GrappleHook
                && VrDualTools.Second != EquipmentController.Equipment.GrappleHook;
            if (!VrSettings.BeltGrapple.Value || !VrAim.Active || vr == null || vr.FollowsHab || !free)
            {
                Hide();
                return;
            }
            if ((copy == null || model == null) && !Build())
            {
                Hide();
                return;
            }
            var forward = vr.transform.localRotation * Vector3.forward;
            forward.y = 0f;
            if (forward.sqrMagnitude < 1e-4f)
            {
                Hide();
                return;
            }
            var heading = Quaternion.LookRotation(forward, Vector3.up);
            // Muzzle down along the leg, the grip to the back.
            var hanging = heading * Quaternion.LookRotation(Vector3.down, Vector3.forward);
            if (!VrToolModels.TrackingToWorld(vr.transform.localPosition + heading * Hip, hanging, out var position, out var rotation))
            {
                Hide();
                return;
            }
            copy.localScale = model.lossyScale;
            copy.SetPositionAndRotation(position - rotation * Vector3.Scale(grip, copy.localScale), rotation);
            copy.gameObject.SetActive(true);
        }

        private static bool Build()
        {
            Release();
            var hook = GameRefs.Grapple;
            var part = hook != null ? VrToolModels.GrappleBarrelOf(hook) : null;
            while (part != null && !part.name.StartsWith(ModelPrefix)) part = part.parent;
            if (part == null) return false;
            model = part;
            var root = new GameObject("ShipbreakerVR Belt grapple").transform;
            root.SetPositionAndRotation(model.position, model.rotation);
            root.localScale = model.lossyScale;
            var pieces = 0;
            foreach (var renderer in model.GetComponentsInChildren<MeshRenderer>(true))
            {
                var filter = renderer.GetComponent<MeshFilter>();
                if (filter == null || filter.sharedMesh == null || !renderer.enabled || !Shown(renderer.transform)) continue;
                var piece = new GameObject(renderer.name);
                piece.layer = renderer.gameObject.layer;
                piece.transform.SetPositionAndRotation(renderer.transform.position, renderer.transform.rotation);
                piece.transform.localScale = renderer.transform.lossyScale;
                piece.transform.SetParent(root, true);
                piece.AddComponent<MeshFilter>().sharedMesh = filter.sharedMesh;
                var shown = piece.AddComponent<MeshRenderer>();
                shown.sharedMaterials = renderer.sharedMaterials;
                shown.shadowCastingMode = renderer.shadowCastingMode;
                pieces++;
            }
            foreach (var target in model.GetComponentsInChildren<Transform>(true))
                if (target.name == Grip) grip = root.InverseTransformPoint(target.position);
            copy = root;
            root.gameObject.SetActive(false);
            Plugin.Log.LogInfo($"Belt grapple: {pieces} part(s), grip at {grip} of the model");
            return pieces > 0;
        }

        // Parts the model switches off stay off the copy.
        private static bool Shown(Transform part)
        {
            for (; part != null && part != model; part = part.parent)
                if (!part.gameObject.activeSelf) return false;
            return true;
        }

        private static void Hide()
        {
            if (copy != null) copy.gameObject.SetActive(false);
        }

        private static void Release()
        {
            if (copy != null) Object.Destroy(copy.gameObject);
            copy = null;
            model = null;
        }
    }
}
