using Random = System.Random;
using UnityEngine;
using Xunit;

namespace ShipbreakerVR.Tests
{
    public class HandShapeTests
    {
        private static Vector3 Point(Random random, float within) => new Vector3(
            (float)(random.NextDouble() * 2 - 1) * within, (float)(random.NextDouble() * 2 - 1) * within, (float)(random.NextDouble() * 2 - 1) * within);

        // The palm is only checked point by point against the other arm when its box's middle is not clear of it, so
        // no point of a box that counts as clear may lie in the arm.
        [Theory]
        [InlineData(1f)]
        [InlineData(1.2f)]
        [InlineData(0.7f)]
        public void ABoxClearOfTheArmHasNoPointInIt(float scale)
        {
            var random = new Random(7);
            var clear = 0;
            for (var i = 0; i < 20000; i++)
            {
                var limb = new VrHandShape.Limb { Known = true, Shoulder = Point(random, 0.4f), Elbow = Point(random, 0.4f), Wrist = Point(random, 0.4f) };
                // Now and then an arm folded up or with no length at all.
                if (i % 50 == 0) limb.Elbow = limb.Shoulder;
                if (i % 70 == 0) limb.Wrist = limb.Elbow + Point(random, 0.001f);
                var middle = Point(random, 0.6f);
                var half = new Vector3(0.01f + (float)random.NextDouble() * 0.05f, 0.01f + (float)random.NextDouble() * 0.05f, 0.01f + (float)random.NextDouble() * 0.05f) * scale;
                if (!VrHandShape.BoxClearOfArm(VrHandShape.ArmDistance(limb, middle, scale, out _), half, scale)) continue;
                clear++;
                for (var k = 0; k < 30; k++)
                {
                    // Anywhere within the corners' reach, whichever way the box is turned.
                    var off = Point(random, 1f);
                    if (off.sqrMagnitude > 1f) off = off.normalized;
                    var distance = VrHandShape.ArmDistance(limb, middle + off * half.magnitude, scale, out _);
                    Assert.True(distance > 0f, $"case {i}: a point of a clear box is {distance} from the arm");
                }
            }
            // The check is there to skip most boxes.
            Assert.True(clear > 10000, $"only {clear} boxes counted as clear");
        }

        [Fact]
        public void ABoxAtTheArmIsNotClear()
        {
            var limb = new VrHandShape.Limb { Known = true, Shoulder = new Vector3(0f, 0.3f, 0f), Elbow = Vector3.zero, Wrist = new Vector3(0f, 0f, 0.25f) };
            var half = new Vector3(0.02f, 0.05f, 0.05f);
            // On the forearm's skin, and a hand's width off it.
            Assert.False(VrHandShape.BoxClearOfArm(VrHandShape.ArmDistance(limb, new Vector3(0.05f, 0f, 0.12f), 1f, out _), half, 1f));
            Assert.False(VrHandShape.BoxClearOfArm(VrHandShape.ArmDistance(limb, new Vector3(0.1f, 0f, 0.12f), 1f, out _), half, 1f));
            Assert.True(VrHandShape.BoxClearOfArm(VrHandShape.ArmDistance(limb, new Vector3(0.3f, 0f, 0.12f), 1f, out _), half, 1f));
        }

        [Fact]
        public void WithoutAnArmEveryBoxIsClear()
        {
            Assert.True(VrHandShape.BoxClearOfArm(VrHandShape.ArmDistance(default, Vector3.zero, 1f, out _), new Vector3(0.05f, 0.05f, 0.05f), 1f));
        }
    }
}
