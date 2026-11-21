using Xunit;

namespace ShipbreakerVR.Tests
{
    public class TestProfileTests
    {
        [Fact]
        public void AMethodsTimeLeavesOutTheTimedMethodsItCalls()
        {
            VrTestProfile.Prepare(3);
            VrTestProfile.Enter(0, 100);
            VrTestProfile.Enter(1, 110);
            VrTestProfile.Enter(2, 120);
            VrTestProfile.Exit(2, 125);
            VrTestProfile.Exit(1, 150);
            VrTestProfile.Exit(0, 200);
            Assert.Equal(5, VrTestProfile.TicksOf(2));
            Assert.Equal(35, VrTestProfile.TicksOf(1));
            Assert.Equal(60, VrTestProfile.TicksOf(0));
            Assert.Equal(1, VrTestProfile.CallsOf(0));
        }

        [Fact]
        public void CallsOfTheSameMethodAddUp()
        {
            VrTestProfile.Prepare(1);
            for (var i = 0; i < 4; i++)
            {
                VrTestProfile.Enter(0, i * 10);
                VrTestProfile.Exit(0, i * 10 + 3);
            }
            Assert.Equal(12, VrTestProfile.TicksOf(0));
            Assert.Equal(4, VrTestProfile.CallsOf(0));
        }

        [Fact]
        public void AMethodThatThrewDoesNotShiftTheOthers()
        {
            VrTestProfile.Prepare(3);
            VrTestProfile.Enter(0, 0);
            // Throws: no end.
            VrTestProfile.Enter(1, 10);
            VrTestProfile.Exit(0, 30);
            VrTestProfile.Enter(2, 40);
            VrTestProfile.Exit(2, 45);
            Assert.Equal(30, VrTestProfile.TicksOf(0));
            Assert.Equal(0, VrTestProfile.CallsOf(1));
            Assert.Equal(5, VrTestProfile.TicksOf(2));
        }

        [Fact]
        public void AnEndWithoutAStartIsIgnored()
        {
            VrTestProfile.Prepare(2);
            VrTestProfile.Exit(1, 50);
            VrTestProfile.Enter(0, 60);
            VrTestProfile.Exit(0, 70);
            Assert.Equal(0, VrTestProfile.CallsOf(1));
            Assert.Equal(10, VrTestProfile.TicksOf(0));
        }

        [Fact]
        public void AListFullOfMethodsThatThrewStartsOver()
        {
            VrTestProfile.Prepare(2);
            for (var i = 0; i < VrTestProfile.MaxDepth + 3; i++) VrTestProfile.Enter(1, i);
            VrTestProfile.Enter(0, 100);
            VrTestProfile.Exit(0, 108);
            Assert.Equal(8, VrTestProfile.TicksOf(0));
        }

        [Fact]
        public void TheLineGivesTheMessagesTotalsAndTheClassesByTime()
        {
            var names = new[] { "Small.Update", "Large.LateUpdate", "Middle.Update", "Tiny.FixedUpdate" };
            var messageOf = new[] { 0, 1, 0, 2 };
            // One tick is a millisecond here, over 10 frames.
            var ticks = new long[] { 1, 20, 5, 0 };
            var calls = new[] { 10, 30, 1000, 5 };
            var line = VrTestProfile.Describe(10, names, messageOf, ticks, calls, 1.0);
            Assert.StartsWith(string.Format("Update {0:0.00} ms in 101 calls, LateUpdate {1:0.00} ms in 3 calls, FixedUpdate {2:0.00} ms in {3:0} calls; most:", 0.6, 2.0, 0.0, 0.5), line);
            Assert.True(line.IndexOf("Large.LateUpdate") < line.IndexOf("Middle.Update"));
            Assert.True(line.IndexOf("Middle.Update") < line.IndexOf("Small.Update"));
            Assert.DoesNotContain("Tiny.FixedUpdate", line);
        }

        [Fact]
        public void MethodsOfNamedClassesHaveATotalOfTheirOwn()
        {
            var names = new[] { "Camera.LateUpdate", "VrHands.Curl", "VrHands.Pose" };
            var messageOf = new[] { 1, 3, 3 };
            var ticks = new long[] { 10, 30, 50 };
            var calls = new[] { 10, 20, 20 };
            var line = VrTestProfile.Describe(10, names, messageOf, ticks, calls, 1.0);
            Assert.Contains(string.Format("LateUpdate {0:0.00} ms in 1 calls", 1.0), line);
            Assert.Contains(string.Format("named classes {0:0.00} ms in 4 calls; most: VrHands.Pose", 8.0), line);
        }

        [Fact]
        public void PhysicsMessagesAndSystemsHaveTotalsOfTheirOwn()
        {
            var names = new[] { "Main.FixedUpdate", "ForceVolume.OnTriggerStay", "LynxCopyTransformsSystem" };
            var messageOf = new[] { 2, 4, 5 };
            var ticks = new long[] { 10, 30, 50 };
            var calls = new[] { 10, 400, 10 };
            var line = VrTestProfile.Describe(10, names, messageOf, ticks, calls, 1.0);
            Assert.DoesNotContain("named classes", line);
            Assert.Contains(string.Format("physics messages {0:0.00} ms in 40 calls, systems {1:0.00} ms in 1 calls; most: LynxCopyTransformsSystem", 3.0, 5.0), line);
        }
    }
}
