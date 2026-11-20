using System;
using UnityEngine;
using Xunit;

namespace ShipbreakerVR.Tests
{
    public class TestTrackTests
    {
        private static readonly Quaternion Identity = new Quaternion(0f, 0f, 0f, 1f);

        // A turn about y by the given degrees, without Quaternion.Euler (an engine call).
        private static Quaternion Yaw(float degrees)
        {
            var half = degrees * (float)Math.PI / 360f;
            return new Quaternion(0f, (float)Math.Sin(half), 0f, (float)Math.Cos(half));
        }

        private static VrTestXr.Pose At(float x, float y, float z, Quaternion? rotation = null) => new VrTestXr.Pose(new Vector3(x, y, z), rotation ?? Identity);

        private static float YawOf(Quaternion q) => 2f * (float)Math.Atan2(q.y, q.w) * 180f / (float)Math.PI;

        [Fact]
        public void LinearGlideMovesEvenly()
        {
            var track = new VrTestTrack(VrTestXr.Device.Head, 10f, new[] { 0f, 2f }, new[] { At(0, 0, 0), At(2, 0, 0, Yaw(90)) });
            Assert.Equal(0.5f, track.Evaluate(10.5f).Position.x, 4);
            Assert.Equal(45f, YawOf(track.Evaluate(11f).Rotation), 2);
            Assert.False(track.Done(11.9f));
            Assert.True(track.Done(12f));
            Assert.Equal(2f, track.Evaluate(20f).Position.x, 4);
            Assert.Equal(0f, track.Evaluate(5f).Position.x, 4);
        }

        [Fact]
        public void EasedGlideStartsAndStopsGently()
        {
            var track = new VrTestTrack(VrTestXr.Device.Left, 0f, new[] { 0f, 1f }, new[] { At(0, 0, 0), At(1, 0, 0) }, VrTestTrack.Easing.InOut);
            Assert.InRange(track.Evaluate(0.05f).Position.x, 0f, 0.01f);
            Assert.Equal(0.5f, track.Evaluate(0.5f).Position.x, 4);
            Assert.InRange(track.Evaluate(0.95f).Position.x, 0.99f, 1f);
            Assert.Equal(0.25f, VrTestTrack.Ease(0.5f, VrTestTrack.Easing.In), 4);
            Assert.Equal(0.75f, VrTestTrack.Ease(0.5f, VrTestTrack.Easing.Out), 4);
        }

        [Fact]
        public void SplinePassesEveryKeyWithoutJumps()
        {
            var times = new[] { 0f, 1f, 3f, 4f };
            var keys = new[] { At(0, 0, 0, Yaw(0)), At(1, 1, 0, Yaw(30)), At(3, 1, 2, Yaw(-20)), At(4, 0, 2, Yaw(10)) };
            var track = new VrTestTrack(VrTestXr.Device.Head, 0f, times, keys);
            for (var i = 0; i < times.Length; i++)
            {
                var pose = track.Evaluate(times[i]);
                Assert.Equal(keys[i].Position.x, pose.Position.x, 4);
                Assert.Equal(keys[i].Position.y, pose.Position.y, 4);
                Assert.Equal(keys[i].Position.z, pose.Position.z, 4);
                Assert.Equal(YawOf(keys[i].Rotation), YawOf(pose.Rotation), 2);
            }
            // Small steps everywhere, also across the keys: no jumps in position or turn.
            var previous = track.Evaluate(0f);
            for (var t = 0.01f; t <= 4f; t += 0.01f)
            {
                var pose = track.Evaluate(t);
                Assert.InRange((pose.Position - previous.Position).magnitude, 0f, 0.05f);
                Assert.InRange(Math.Abs(YawOf(pose.Rotation) - YawOf(previous.Rotation)), 0f, 1.5f);
                previous = pose;
            }
        }

        [Fact]
        public void SplineRestsAtItsEndsAndKeepsMovingThroughTheMiddle()
        {
            var track = new VrTestTrack(VrTestXr.Device.Head, 0f, new[] { 0f, 1f, 2f }, new[] { At(0, 0, 0), At(1, 0, 0), At(2, 0, 0) });
            Assert.InRange(track.Evaluate(0.02f).Position.x, 0f, 0.005f);
            Assert.InRange(track.Evaluate(1.98f).Position.x, 1.995f, 2f);
            // Through the middle key at full speed, not stopping there.
            var speed = (track.Evaluate(1.01f).Position.x - track.Evaluate(0.99f).Position.x) / 0.02f;
            Assert.InRange(speed, 0.9f, 1.1f);
        }

        [Fact]
        public void TurnsTakeTheShortWay()
        {
            var track = new VrTestTrack(VrTestXr.Device.Head, 0f, new[] { 0f, 1f }, new[] { At(0, 0, 0, Yaw(170)), At(0, 0, 0, Yaw(-170)) });
            var middle = Math.Abs(YawOf(track.Evaluate(0.5f).Rotation));
            Assert.InRange(middle, 179f, 181f);
        }

        [Fact]
        public void KeysMustBeInOrderAndEasingsAreNamed()
        {
            Assert.Throws<FormatException>(() => new VrTestTrack(VrTestXr.Device.Head, 0f, new[] { 0f, 2f, 1f }, new[] { At(0, 0, 0), At(1, 0, 0), At(2, 0, 0) }));
            Assert.Equal(VrTestTrack.Easing.InOut, VrTestTrack.ParseEasing("ease"));
            Assert.Equal(VrTestTrack.Easing.Out, VrTestTrack.ParseEasing("ease-out"));
            Assert.False(VrTestTrack.IsEasing("async"));
        }
    }
}
