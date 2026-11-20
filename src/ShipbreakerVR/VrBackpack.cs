using System.Linq;
using UnityEngine;

namespace ShipbreakerVR
{
    // The suit's backpack (the thruster pack, 'SM_Character_ThrusterPack_Cine') is not part of the suit's model: the game
    // hangs it straight on the GrabController, 47 cm below and 18 cm behind the eye, unturned. On a flat screen the
    // body turns with the camera anyway; in VR the GrabController turns with the whole head (VrBody), while VrTorso keeps
    // the model upright below it. So the pack swung with every look, up over the shoulders when looking down, and its
    // shadow showed it (Raik 2026-11-17, a snapshot at the kiosk). Here, after VrTorso has posed the model, the pack is
    // put on the suit's chest bone (rig_spine3) where it sits on the suit in the suit's bind pose, as if it were skinned
    // to that bone. With VR off, in the Hab view, or if anything is missing, it is back where the game has it.
    internal static class VrBackpack
    {
        static VrBackpack() => VrSession.OnUnload("backpack", Release);

        private const string PackName = "SM_Character_ThrusterPack_Cine";
        private const string SuitName = "geo_suit";
        private const string ChestName = "rig_spine3";

        private static Transform pack;
        private static Transform chest;
        // The pack in the chest bone's axes, from the suit's bind pose.
        private static Matrix4x4 onChest;
        private static Vector3 gameLocalPosition;
        private static Quaternion gameLocalRotation;
        private static bool found;
        private static bool moved;
        private static bool missingLogged;

        // From VrFrame, after the torso step.
        public static void LateUpdate()
        {
            var vr = VrCamera.Instance;
            var active = VrAim.Active && vr != null && vr.GameCamera != null && !vr.FollowsHab && Find();
            if (!active)
            {
                Restore();
                return;
            }
            var m = chest.localToWorldMatrix * onChest;
            pack.SetPositionAndRotation(m.GetColumn(3), Quaternion.LookRotation(m.GetColumn(2), m.GetColumn(1)));
            moved = true;
        }

        private static bool Find()
        {
            if (found && pack != null && chest != null) return true;
            found = false;
            var grab = GameRefs.Grab;
            if (grab == null) return false;
            var p = grab.transform.Find(PackName);
            var suit = grab.GetComponentsInChildren<SkinnedMeshRenderer>(true).FirstOrDefault(r => r != null && r.name == SuitName);
            var index = suit != null && suit.sharedMesh != null ? System.Array.FindIndex(suit.bones, b => b != null && b.name == ChestName) : -1;
            var binds = index >= 0 ? suit.sharedMesh.bindposes : null;
            if (p == null || binds == null || index >= binds.Length)
            {
                if (!missingLogged)
                {
                    missingLogged = true;
                    Plugin.Log.LogInfo($"Backpack: left to the game, pack {(p != null ? "found" : "missing")}, suit {(suit != null ? "found" : "missing")}, chest bone {(index >= 0 ? "found" : "missing")}");
                }
                return false;
            }
            pack = p;
            chest = suit.bones[index];
            gameLocalPosition = pack.localPosition;
            gameLocalRotation = pack.localRotation;
            // The pack in the suit's mesh space (its place relative to the suit never changes: both hang unturned on
            // the GrabController), then in the chest bone's axes as the bone sits in the bind pose.
            onChest = binds[index] * suit.transform.worldToLocalMatrix * pack.localToWorldMatrix;
            found = true;
            var at = (Vector3)onChest.GetColumn(3);
            Plugin.Log.LogInfo($"Backpack: '{PackName}' now on '{ChestName}', {at.x:0.000}, {at.y:0.000}, {at.z:0.000} m in the bone's axes");
            return true;
        }

        // The pack back on its place on the GrabController.
        private static void Restore()
        {
            if (!moved) return;
            moved = false;
            if (pack == null) return;
            pack.localPosition = gameLocalPosition;
            pack.localRotation = gameLocalRotation;
        }

        // On unload (hot reload).
        public static void Release()
        {
            Restore();
            pack = chest = null;
            found = false;
            missingLogged = false;
        }
    }
}
