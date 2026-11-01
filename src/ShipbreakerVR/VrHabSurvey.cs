using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using BBI.Unity.Game;
using BepInEx;
using Cinemachine;
using HarmonyLib;
using UnityEngine;

namespace ShipbreakerVR
{
    // The Hab as a floor plan, for walking through it: its spots, their ways, what blocks them, its colliders and the
    // boxes of everything larger than a mug. Written with every F8 dump in the Hab, to a file of its own since it is
    // long. Everything is in the room's axes (x and z across the floor, y up from the floor), in meters. Boxes come from
    // the renderers, as the meshes' own bounds say nothing for static batched meshes (they cover the whole batch).
    internal static class VrHabSurvey
    {
        private const float SmallestPart = 0.25f;

        public static void Write()
        {
            var room = VrHab.Room;
            var controller = VrHab.Controller;
            if (room == null || controller == null) return;
            if (!VrHab.TryRoomFloor(out var floor)) floor = 0f;
            var text = new StringBuilder();
            text.AppendLine($"Room '{VrUtil.Path(room)}', scale {room.lossyScale}, floor at {floor:0.000}");

            text.AppendLine("Spots (x y z, heading in degrees):");
            foreach (var camera in controller.GetComponentsInChildren<CinemachineVirtualCamera>(true).OrderBy(c => c.Name))
            {
                var ahead = room.InverseTransformDirection(camera.transform.forward);
                text.AppendLine($"  {camera.Name}: {Place(room, camera.transform.position, floor)}, heading {Mathf.Atan2(ahead.x, ahead.z) * Mathf.Rad2Deg:0}, pitch {Mathf.Asin(Mathf.Clamp(ahead.y, -1f, 1f)) * Mathf.Rad2Deg:0}");
            }

            text.AppendLine("Ways:");
            var options = AccessTools.FieldRefAccess<Hab3DLocationNavigation, List<NavigationOption>>("m_NavigationOptions");
            foreach (var location in Resources.FindObjectsOfTypeAll<Hab3DLocationNavigation>().Where(l => l.gameObject.scene.IsValid()))
            {
                if (options(location) == null) continue;
                foreach (var way in options(location))
                {
                    var target = way.TransformToTrack != null ? $", target '{way.TransformToTrack.name}' at {Place(room, way.TransformToTrack.position, floor)}" : "";
                    var blocks = way.BlockingConditions != null ? string.Join(", ", way.BlockingConditions.Where(c => c != null).Select(c => c.name)) : "";
                    text.AppendLine($"  {(location.LocationVirtualCamera != null ? location.LocationVirtualCamera.Name : "-")}: {way.ButtonOption} to {(way.CameraToGoTo != null ? way.CameraToGoTo.Name : "-")}{(way.GoesToNavigableScreen ? " (screen)" : "")}{target}{(blocks.Length > 0 ? $", blocked by {blocks}" : "")}");
                }
            }

            var root = room.root;
            text.AppendLine("Colliders:");
            foreach (var collider in root.GetComponentsInChildren<Collider>(true).OrderBy(c => VrUtil.Path(c.transform)))
                text.AppendLine($"  {collider.GetType().Name}{(collider.isTrigger ? " trigger" : "")}{(collider.enabled && collider.gameObject.activeInHierarchy ? "" : " off")} layer {collider.gameObject.layer}: {Box(room, collider.bounds, floor)} '{VrUtil.Path(collider.transform)}'");

            text.AppendLine($"Renderers of at least {SmallestPart} m:");
            foreach (var renderer in root.GetComponentsInChildren<MeshRenderer>(true).OrderBy(r => VrUtil.Path(r.transform)))
            {
                if (renderer.bounds.size.magnitude < SmallestPart) continue;
                var filter = renderer.GetComponent<MeshFilter>();
                text.AppendLine($"  {Box(room, renderer.bounds, floor)}{(renderer.enabled && renderer.gameObject.activeInHierarchy ? "" : " off")}{(filter != null && filter.sharedMesh != null && filter.sharedMesh.isReadable ? " readable" : "")} '{VrUtil.Path(renderer.transform)}'");
            }

            var path = System.IO.Path.Combine(Paths.BepInExRootPath, "ShipbreakerVR-hab-survey.txt");
            File.WriteAllText(path, text.ToString());
            Plugin.Log.LogInfo($"Hab survey written to {path}");
        }

        private static string Place(Transform room, Vector3 world, float floor)
        {
            var p = room.InverseTransformPoint(world);
            return $"{p.x:0.00} {p.y - floor:0.00} {p.z:0.00}";
        }

        // The world's box in the room's axes. A little too large, as the room is tilted against the world.
        private static string Box(Transform room, Bounds bounds, float floor)
        {
            var low = Vector3.positiveInfinity;
            var high = Vector3.negativeInfinity;
            for (var x = -1; x <= 1; x += 2)
                for (var y = -1; y <= 1; y += 2)
                    for (var z = -1; z <= 1; z += 2)
                    {
                        var corner = room.InverseTransformPoint(bounds.center + Vector3.Scale(bounds.extents, new Vector3(x, y, z)));
                        low = Vector3.Min(low, corner);
                        high = Vector3.Max(high, corner);
                    }
            return $"x {low.x:0.00} to {high.x:0.00}, y {low.y - floor:0.00} to {high.y - floor:0.00}, z {low.z:0.00} to {high.z:0.00}";
        }
    }
}
