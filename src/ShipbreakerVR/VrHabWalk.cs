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
            File.AppendAllText(System.IO.Path.Combine(Paths.BepInExRootPath, "ShipbreakerVR-hab-floor.txt"), $"{head.x:0.00} {head.z:0.00}{System.Environment.NewLine}");
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
        // The floor's edge, walked and measured in the headset with the head at the walls and furniture.
        public static readonly Vector2[] Outline =
        {
            new Vector2(131.10f, 112.63f), new Vector2(128.99f, 112.55f), new Vector2(129.05f, 112.93f), new Vector2(129.59f, 112.93f),
            new Vector2(129.59f, 113.36f), new Vector2(129.84f, 113.39f), new Vector2(130.15f, 114.21f), new Vector2(129.51f, 114.57f),
            new Vector2(129.51f, 115.57f), new Vector2(129.43f, 115.58f), new Vector2(128.47f, 115.64f), new Vector2(128.52f, 116.59f),
            new Vector2(129.91f, 117.90f), new Vector2(130.16f, 117.89f), new Vector2(130.18f, 118.44f), new Vector2(130.10f, 118.42f),
            new Vector2(130.12f, 118.69f), new Vector2(130.06f, 119.90f), new Vector2(130.12f, 119.90f), new Vector2(130.15f, 121.09f),
            new Vector2(130.15f, 121.15f), new Vector2(129.34f, 121.15f), new Vector2(129.35f, 121.82f), new Vector2(129.99f, 121.46f),
            new Vector2(130.15f, 121.51f), new Vector2(130.37f, 122.18f), new Vector2(131.14f, 122.22f), new Vector2(131.18f, 122.00f),
            new Vector2(131.41f, 122.01f), new Vector2(131.50f, 122.23f), new Vector2(133.63f, 120.42f), new Vector2(133.97f, 120.48f),
            new Vector2(134.47f, 121.04f), new Vector2(134.52f, 120.07f), new Vector2(134.36f, 120.05f), new Vector2(134.20f, 119.95f),
            new Vector2(133.76f, 120.03f), new Vector2(132.87f, 118.90f), new Vector2(132.86f, 118.84f), new Vector2(133.20f, 118.60f),
            new Vector2(133.16f, 118.20f), new Vector2(133.01f, 118.20f), new Vector2(133.02f, 117.80f), new Vector2(132.72f, 117.82f),
            new Vector2(132.73f, 117.28f), new Vector2(132.58f, 117.28f), new Vector2(132.59f, 116.88f), new Vector2(132.46f, 116.77f),
            new Vector2(132.42f, 115.84f), new Vector2(132.63f, 115.68f), new Vector2(132.60f, 115.20f), new Vector2(132.47f, 115.08f),
            new Vector2(132.46f, 114.22f), new Vector2(132.62f, 114.11f), new Vector2(132.53f, 113.51f), new Vector2(132.55f, 113.45f),
            new Vector2(132.40f, 113.53f), new Vector2(132.21f, 113.40f), new Vector2(132.34f, 113.21f), new Vector2(131.74f, 112.63f),
            new Vector2(131.60f, 112.76f), new Vector2(131.38f, 112.56f), new Vector2(131.55f, 112.42f), new Vector2(131.34f, 112.27f),
            new Vector2(131.25f, 112.42f), new Vector2(131.21f, 112.52f),
        };

        // How far the head stays from that edge, so the view does not end up inside what it was measured against.
        private const float Margin = 0.1f;

        public static bool Inside(Vector2 point)
        {
            var inside = false;
            for (int i = 0, last = Outline.Length - 1; i < Outline.Length; last = i++)
            {
                var a = Outline[i];
                var b = Outline[last];
                if ((a.y > point.y) != (b.y > point.y) && point.x < a.x + (b.x - a.x) * (point.y - a.y) / (b.y - a.y)) inside = !inside;
                if (Distance(point, a, b) < Margin) return false;
            }
            return inside;
        }

        private static float Distance(Vector2 point, Vector2 a, Vector2 b)
        {
            var along = b - a;
            var share = Mathf.Clamp01(Vector2.Dot(point - a, along) / Mathf.Max(along.sqrMagnitude, 1e-6f));
            return (point - (a + along * share)).magnitude;
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
