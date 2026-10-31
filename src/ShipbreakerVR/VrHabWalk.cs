using System.IO;
using BepInEx;
using UnityEngine;

namespace ShipbreakerVR
{
    // Walking through the Hab instead of hopping between its spots (Station/FreiGehen). The game stays on the spot where
    // one sits on the bed and knows nothing of it; only the rig leaves the spot. A stands up, B sits down again.
    internal static class VrHabWalk
    {
        private const string SitSpot = "BedSitting_Virtual_Camera";
        private const float Speed = 1.8f;
        private const float Deadzone = 0.2f;
        private const float TurnStep = 45f;
        private const float TurnPress = 0.6f;
        private const float TurnRest = 0.3f;

        // In front of the bed.
        private static readonly Vector2 Start = new Vector2(130.6f, 113.7f);

        private static Vector2 place;
        private static Quaternion heading;
        private static bool turned;
        private static bool mapping;
        private static int updatedFrame = -1;

        public static bool Walking { get; private set; }

        // True while the sticks and A and B belong to walking, not to the game.
        public static bool TakesInput => Walking && !Paused;

        private static bool Paused => VrHab.Controller == null || VrHab.Controller.IsHabPaused;

        public static Quaternion Heading => heading;

        // Runs from the rig's placing, which can come more than once a frame. spotHeading is the game camera's.
        public static void Update(Quaternion spotHeading)
        {
            if (Time.frameCount == updatedFrame) return;
            updatedFrame = Time.frameCount;
            if (!Walking)
            {
                if (VrSettings.FreeWalking.Value && VrInput.Right.Primary.Pressed && !Paused && !VrHab.Holding
                    && VrHab.ActiveSpot == SitSpot && VrHab.TryRoomFloor(out _))
                {
                    Walking = true;
                    place = Start;
                    heading = spotHeading;
                    Plugin.Log.LogInfo("Hab: standing up, walking");
                }
                return;
            }
            if (!VrSettings.FreeWalking.Value || VrHab.ActiveSpot != SitSpot)
            {
                Stop();
                return;
            }
            if (Paused) return;
            if (VrInput.Right.Secondary.Pressed && !VrInput.Left.Secondary.Down)
            {
                Stop();
                return;
            }
            if (VrSettings.Diagnostics.Value) Map();
            Turn(VrInput.Right.Stick.x);
            Move(VrInput.Left.Stick);
            VrHabPosters.Update(VrCamera.Instance.transform, VrCamera.WorldScale);
        }

        public static void Stop()
        {
            if (!Walking) return;
            Walking = false;
            mapping = false;
            VrHabPosters.Stop();
            Plugin.Log.LogInfo("Hab: sitting down again");
        }

        // Where the head goes while walking.
        public static Vector3 Head(float scale)
        {
            VrHab.TryRoomFloor(out var floor);
            return VrHab.Room.TransformPoint(new Vector3(place.x, floor + HabStanding.Height * scale, place.y));
        }

        // For drawing the floor by hand, with Debug/Diagnose on: X lets one walk through everything, and A writes where the
        // head is, so one can walk along the walls and lean up to their corners.
        private static void Map()
        {
            if (VrInput.Left.Primary.Pressed)
            {
                mapping = !mapping;
                VrInput.Left.Buzz(0.5f, mapping ? 0.3f : 0.06f);
                Plugin.Log.LogInfo($"Hab: floor limits {(mapping ? "off" : "on")}");
            }
            if (!VrInput.Right.Primary.Pressed) return;
            var head = VrHab.Room.InverseTransformPoint(VrCamera.Instance.transform.position);
            File.AppendAllText(System.IO.Path.Combine(Paths.BepInExRootPath, "ShipbreakerVR-hab-floor.txt"), $"{head.x:0.00} {head.z:0.00}
");
            VrInput.Right.Buzz(0.5f, 0.06f);
            Plugin.Log.LogInfo($"Hab: floor point {head.x:0.00} {head.z:0.00}");
        }

        private static void Turn(float stick)
        {
            if (Mathf.Abs(stick) < TurnRest) turned = false;
            if (turned || Mathf.Abs(stick) < TurnPress) return;
            turned = true;
            heading = Quaternion.AngleAxis(Mathf.Sign(stick) * TurnStep, VrHab.RoomUp) * heading;
        }

        private static void Move(Vector2 stick)
        {
            if (stick.magnitude < Deadzone) return;
            var up = VrHab.RoomUp;
            var forward = Vector3.ProjectOnPlane(VrCamera.Instance.transform.forward, up);
            if (forward.sqrMagnitude < 1e-4f) forward = heading * Vector3.forward;
            forward = forward.normalized;
            var way = forward * stick.y + Vector3.Cross(up, forward) * stick.x;
            var step = VrHab.Room.InverseTransformDirection(way) * (Speed * Time.unscaledDeltaTime);
            var to = place + new Vector2(step.x, step.z);
            place = mapping ? to : HabFloor.Limit(place, to);
        }
    }

    // Where one can walk in the Hab, in the room's axes (x, z). Nothing there has a collider, neither walls nor floor.
    internal static class HabFloor
    {
        // Lowest and highest corner of the aisle, the work area and the window niche.
        private static readonly Rect[] Areas =
        {
            Rect.MinMaxRect(129.75f, 113.45f, 132.1f, 118.5f),
            Rect.MinMaxRect(130.6f, 118.5f, 132.9f, 121.35f),
            Rect.MinMaxRect(128.7f, 115.95f, 130.1f, 116.8f),
        };

        public static bool Inside(Vector2 point)
        {
            foreach (var area in Areas)
                if (point.x >= area.xMin && point.x <= area.xMax && point.y >= area.yMin && point.y <= area.yMax) return true;
            return false;
        }

        // How far a step gets: along an edge if it runs into one at a slant.
        public static Vector2 Limit(Vector2 from, Vector2 to)
        {
            if (Inside(to)) return to;
            var alongX = new Vector2(to.x, from.y);
            if (Inside(alongX)) return alongX;
            var alongZ = new Vector2(from.x, to.y);
            return Inside(alongZ) ? alongZ : from;
        }
    }
}
