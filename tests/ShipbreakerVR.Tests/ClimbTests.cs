using UnityEngine;
using Xunit;

namespace ShipbreakerVR.Tests
{
    public class ClimbTests
    {
        private const float Step = 0.02f;
        private static readonly Vector3 Up = new Vector3(0f, 1f, 0f);

        [Theory]
        [InlineData(361.2f)]
        [InlineData(361.11f)]
        public void StandsAtTheSameHeightFromEverySpot(float height)
        {
            var placed = HabStanding.Place(new Vector3(3f, height, 4f), Up, 359.3f, 1f);
            Assert.Equal(361.2f, placed.y, 3);
            Assert.Equal(3f, placed.x, 3);
            Assert.Equal(4f, placed.z, 3);
        }

        [Fact]
        public void FrameSitsWhereTheLineToItsSpotCrossesTheCanvas()
        {
            Assert.True(HabStanding.Across(new Vector3(0f, 0f, -2f), new Vector3(1f, 2f, 2f), out var place));
            Assert.Equal(0.5f, place.x, 3);
            Assert.Equal(1f, place.y, 3);
        }

        [Fact]
        public void FrameBeyondTheCanvasStaysOnIt()
        {
            var seen = HabStanding.Inside(new Vector2(0.9f, -0.1f), 0.2f, 0.3f);
            Assert.Equal(0.4f, seen.x, 3);
            Assert.Equal(-0.1f, seen.y, 3);
        }

        [Fact]
        public void FrameForASpotBehindTheEyeGoesToItsSide()
        {
            Assert.True(HabStanding.Across(new Vector3(0f, 0f, -2f), new Vector3(-1f, 2f, -3f), out var place));
            Assert.Equal(-1f, place.x, 3);
        }

        [Fact]
        public void PosterChoicesSurviveTheFile()
        {
            var choices = PosterChoices.Parse(new[] { "2	mein bild.png", "kaputt", "0	auch.jpg" });
            Assert.Equal(2, choices.Count);
            Assert.Equal("mein bild.png", choices[2]);
            Assert.Equal(new[] { "0	auch.jpg", "2	mein bild.png" }, System.Linq.Enumerable.ToArray(PosterChoices.Format(choices)));
        }

        [Fact]
        public void WalksFreelyInsideTheFloor()
        {
            var to = new Vector2(131f, 116f);
            Assert.Equal(to.y, HabFloor.Limit(new Vector2(131f, 115f), to).y, 3);
        }

        [Fact]
        public void SlidesAlongAnEdge()
        {
            var limited = HabFloor.Limit(new Vector2(132.3f, 114.7f), new Vector2(133f, 115f));
            Assert.True(limited.x > 132.3f && limited.x < 132.4f);
            Assert.Equal(115f, limited.y, 2);
        }

        [Fact]
        public void SlidesAlongASlantedEdge()
        {
            var start = new Vector2(132.3f, 121.2f);
            var place = start;
            for (var i = 0; i < 60; i++) place = HabFloor.Limit(place, place + new Vector2(0.02f, 0f));
            Assert.True(place.x > start.x + 0.2f);
            Assert.True(place.y < start.y - 0.1f);
            Assert.True(HabFloor.Inside(place));
        }

        [Fact]
        public void ALongFrameDoesNotJumpOverAnEdge()
        {
            Assert.True(HabFloor.Inside(HabFloor.Limit(new Vector2(131f, 116f), new Vector2(131f, 125f))));
        }

        [Fact]
        public void TheFloorsEdgeDoesNotCrossItself()
        {
            var edge = HabFloor.Outline;
            for (var i = 0; i < edge.Length; i++)
                for (var j = i + 2; j < edge.Length; j++)
                {
                    if (i == 0 && j == edge.Length - 1) continue;
                    Assert.False(Crosses(edge[i], edge[i + 1], edge[j], edge[(j + 1) % edge.Length]), $"edges {i} and {j}");
                }
        }

        private static bool Crosses(Vector2 a, Vector2 b, Vector2 c, Vector2 d) =>
            Side(a, b, c) * Side(a, b, d) < 0f && Side(c, d, a) * Side(c, d, b) < 0f;

        private static float Side(Vector2 a, Vector2 b, Vector2 point) =>
            (b.x - a.x) * (point.y - a.y) - (b.y - a.y) * (point.x - a.x);

        [Theory]
        [InlineData(130.6f, 113.7f)]
        [InlineData(131.11f, 114.61f)]
        [InlineData(131.07f, 116.67f)]
        [InlineData(131.86f, 119.17f)]
        [InlineData(131.63f, 120.17f)]
        [InlineData(131.36f, 120.85f)]
        public void CanWalkWhereTheGameStands(float x, float z)
        {
            Assert.True(HabFloor.Inside(new Vector2(x, z)));
        }

        [Theory]
        [InlineData(133.4f, 121.2f)]
        [InlineData(130f, 111f)]
        [InlineData(129f, 114f)]
        public void CannotWalkIntoFurniture(float x, float z)
        {
            Assert.False(HabFloor.Inside(new Vector2(x, z)));
        }

        [Fact]
        public void WalksFromTheAisleToTheWorkArea()
        {
            Assert.True(HabFloor.Inside(new Vector2(131f, 118.5f)));
            Assert.True(HabFloor.Inside(new Vector2(132.5f, 120f)));
        }

        [Fact]
        public void StopsInACorner()
        {
            Assert.True(HabFloor.Inside(HabFloor.Limit(new Vector2(130.6f, 113.7f), new Vector2(120f, 100f))));
        }

        [Fact]
        public void SittingOnTheBedKeepsTheSpot()
        {
            var placed = HabStanding.Place(new Vector3(3f, 1.28f, 4f), Up, 0f, 1f);
            Assert.Equal(1.28f, placed.y, 3);
        }

        [Fact]
        public void StandsOnATiltedFloorAlongItsUp()
        {
            var up = new Vector3(0f, 0.99756f, 0.06976f);
            var placed = HabStanding.Place(new Vector3(1f, 2f, 3f), up, 0f, 1f);
            Assert.Equal(HabStanding.Height, Vector3.Dot(placed, up), 3);
        }

        [Fact]
        public void StopsOnTheSpotWithNoRoomLeft()
        {
            var limited = ClimbContact.LimitApproach(new Vector3(0f, -5f, 0f), Vector3.zero, Up, 0f, Step);
            Assert.Equal(0f, limited.y, 3);
        }

        [Fact]
        public void TreatsHavingPassedTheClearanceAsNoRoom()
        {
            var limited = ClimbContact.LimitApproach(new Vector3(0f, -5f, 0f), Vector3.zero, Up, -0.01f, Step);
            Assert.Equal(0f, limited.y, 3);
        }

        [Fact]
        public void SlowsToWhatFitsInTheStep()
        {
            var limited = ClimbContact.LimitApproach(new Vector3(0f, -5f, 0f), Vector3.zero, Up, 0.02f, Step);
            Assert.Equal(-1f, limited.y, 3);
        }

        [Fact]
        public void KeepsASpeedThatFits()
        {
            var velocity = new Vector3(0f, -0.5f, 0f);
            Assert.Equal(velocity, ClimbContact.LimitApproach(velocity, Vector3.zero, Up, 0.02f, Step));
        }

        [Fact]
        public void KeepsSlidingAlongTheSurface()
        {
            var limited = ClimbContact.LimitApproach(new Vector3(3f, -5f, 0f), Vector3.zero, Up, 0f, Step);
            Assert.Equal(3f, limited.x, 3);
            Assert.Equal(0f, limited.y, 3);
        }

        [Fact]
        public void KeepsMovingAway()
        {
            var velocity = new Vector3(0f, 2f, 0f);
            Assert.Equal(velocity, ClimbContact.LimitApproach(velocity, Vector3.zero, Up, 0f, Step));
        }

        [Fact]
        public void AHandFarFromTheHeadStaysWhereItIs()
        {
            var hand = new Vector3(0f, 0f, 0.5f);
            Assert.Equal(hand, ClimbContact.Closer(hand, Vector3.zero));
        }

        [Fact]
        public void AHandNearTheHeadCountsAsCloser()
        {
            Assert.Equal(0.15f, ClimbContact.Closer(new Vector3(0f, 0f, 0.3f), Vector3.zero).z, 3);
        }

        [Fact]
        public void APulledInHandCountsAsAtTheHead()
        {
            var head = new Vector3(0f, 1.6f, 0f);
            Assert.Equal(head, ClimbContact.Closer(new Vector3(0f, 1.6f, 0.2f), head));
        }

        [Fact]
        public void TinyDebrisIsThrownFaster()
        {
            Assert.Equal(1.5f, ClimbContact.ThrowGain(1f), 3);
            Assert.Equal(1.5f, ClimbContact.ThrowGain(2f), 3);
            Assert.Equal(1.25f, ClimbContact.ThrowGain(3.5f), 3);
            Assert.Equal(1f, ClimbContact.ThrowGain(5f), 3);
            Assert.Equal(1f, ClimbContact.ThrowGain(10f), 3);
        }

        [Fact]
        public void HeavierPartsLagBehindMore()
        {
            Assert.Equal(0.5f, ClimbContact.Follow(0f, 30f, 0.5f, 0.08f), 3);
            Assert.Equal(0.29f, ClimbContact.Follow(15f, 30f, 0.5f, 0.08f), 3);
            Assert.Equal(0.08f, ClimbContact.Follow(30f, 30f, 0.5f, 0.08f), 3);
        }

        [Fact]
        public void MovesAlongWithASurfaceComingCloser()
        {
            var limited = ClimbContact.LimitApproach(Vector3.zero, new Vector3(0f, 2f, 0f), Up, 0f, Step);
            Assert.Equal(2f, limited.y, 3);
        }

        [Theory]
        [InlineData(1f, 0.1f)]
        [InlineData(3f, 0.9f)]
        [InlineData(6f, 3.6f)]
        public void GloveBrakesAGlideTheFurtherTheFaster(float speed, float expected)
        {
            var velocity = new Vector3(speed, 0f, 0f);
            var brake = ClimbContact.GloveBrake(500f, 1, 1f);
            var way = 0f;
            var steps = 0;
            while (velocity != Vector3.zero && steps++ < 10000)
            {
                way += velocity.x * Step;
                velocity = ClimbContact.Brake(velocity, brake, Step);
            }
            Assert.InRange(way, expected * 0.9f, expected * 1.1f);
        }

        // Raik, 0.30.1: realistic climbing, the arms push the 100 kg body with 500 N each over an arm's length.
        [Theory]
        [InlineData(1, 2.24f)]
        [InlineData(2, 3.16f)]
        public void PushingOffOverAnArmLengthGivesWhatTheArmsCan(int hands, float expected)
        {
            var acceleration = ClimbContact.GloveBrake(500f, hands, 1f);
            var velocity = Vector3.zero;
            var way = 0f;
            var steps = 0;
            while (way < 0.5f && steps++ < 10000)
            {
                var before = velocity.x;
                velocity = ClimbContact.Accelerate(velocity, new Vector3(10f, 0f, 0f), acceleration, Step);
                way += (before + velocity.x) * 0.5f * Step;
            }
            Assert.InRange(velocity.x, expected * 0.95f, expected * 1.05f);
        }

        [Theory]
        [InlineData(1f, 0.056f)]
        [InlineData(2f, 0.222f)]
        [InlineData(3.2f, 0.569f)]
        public void TakingHoldTheBodyGlidesOnByTheCatchForce(float speed, float expected)
        {
            var velocity = new Vector3(speed, 0f, 0f);
            var brake = ClimbContact.GloveBrake(900f, 1, 1f);
            var way = 0f;
            var steps = 0;
            while (velocity != Vector3.zero && steps++ < 10000)
            {
                var before = velocity.x;
                velocity = ClimbContact.Brake(velocity, brake, Step);
                way += (before + velocity.x) * 0.5f * Step;
            }
            Assert.InRange(way, expected * 0.95f, expected * 1.05f);
        }

        // Hand over hand: the hand going back along the body at 2 m/s keeps the body going at 2 m/s, gap or not.
        [Fact]
        public void HandOverHandKeepsTheBodysSpeed()
        {
            var wanted = ClimbContact.KeepToHold(Vector3.zero, new Vector3(-2f, 0f, 0f), Step, 5f, 10f);
            Assert.Equal(2f, wanted.x, 3);
        }

        // The hand held still against the body: nothing but the gap counts, which pulls the body back to the hold.
        [Fact]
        public void AHandHeldStillDrawsTheBodyBackToTheHold()
        {
            var wanted = ClimbContact.KeepToHold(new Vector3(-0.2f, 0f, 0f), Vector3.zero, Step, 5f, 10f);
            Assert.Equal(-ClimbContact.MostApproach(0.2f, 5f), wanted.x, 3);
        }

        [Fact]
        public void TheBodyClosesAGapNoFasterThanItCanStopAtIt()
        {
            Assert.Equal(1.732f, ClimbContact.MostApproach(0.5f, 3f), 3);
            Assert.Equal(0f, ClimbContact.MostApproach(0f, 3f), 4);
            Assert.Equal(0f, ClimbContact.MostApproach(-0.1f, 3f), 4);
        }

        [Fact]
        public void TwoHandsBrakeTwiceAsHard()
        {
            Assert.Equal(2f * ClimbContact.GloveBrake(500f, 1, 1f), ClimbContact.GloveBrake(500f, 2, 1f), 4);
            Assert.Equal(5f, ClimbContact.GloveBrake(500f, 1, 1f), 4);
        }

        [Fact]
        public void AHandleCatchesUpToThreeMetersASecondAcross()
        {
            var limit = ClimbContact.CatchLimit(ClimbContact.HandleForce, ClimbContact.HandleStroke, 1f);
            Assert.Equal(3f, limit, 3);
            Assert.True(ClimbContact.Catch(2.9f, limit, out var none));
            Assert.Equal(0f, none);
            Assert.False(ClimbContact.Catch(4f, limit, out var left));
            Assert.Equal(Mathf.Sqrt(7f), left, 3);
        }

        [Fact]
        public void ComingInAtASlantOnlyTheSpeedAcrossCounts()
        {
            var limit = ClimbContact.CatchLimit(ClimbContact.HandleForce, ClimbContact.HandleStroke, 1f);
            var edge = new Vector3(0f, 0f, 1f);
            // 4 m/s at 40 degrees to the edge: 2.6 across, held; 3.1 along, glides on.
            var velocity = new Vector3(Mathf.Sin(40f * Mathf.Deg2Rad), 0f, Mathf.Cos(40f * Mathf.Deg2Rad)) * 4f;
            var across = Vector3.ProjectOnPlane(velocity, edge).magnitude;
            Assert.True(ClimbContact.Catch(across, limit, out _));
            Assert.Equal(4f * Mathf.Cos(40f * Mathf.Deg2Rad), Vector3.Dot(velocity, edge), 3);
            // Straight on it is not.
            Assert.False(ClimbContact.Catch(4f, limit, out _));
        }

        [Fact]
        public void ASeamTakesSomeSpeedNeverTurningItBack()
        {
            Assert.Equal(0.7f, ClimbContact.Lose(new Vector3(1f, 0f, 0f), ClimbContact.SeamLoss).x, 4);
            Assert.Equal(Vector3.zero, ClimbContact.Lose(new Vector3(0.2f, 0f, 0f), ClimbContact.SeamLoss));
        }

        [Fact]
        public void ThrustGlidesNoFasterThanItsLimit()
        {
            var velocity = Vector3.zero;
            for (var i = 0; i < 500; i++)
                velocity = ClimbContact.ThrustGlide(velocity + new Vector3(0.1f, 0f, 0f), velocity.magnitude, ClimbContact.GloveBrake(500f, 1, 1f), Step, 1f);
            Assert.Equal(1.5f, velocity.x, 3);
        }

        [Fact]
        public void FastGlideBrakesDownToTheThrustLimit()
        {
            var velocity = new Vector3(4f, 0f, 0f);
            var brake = ClimbContact.GloveBrake(500f, 1, 1f);
            var slower = ClimbContact.ThrustGlide(velocity + new Vector3(0.1f, 0f, 0f), 4f, brake, Step, 1f);
            Assert.InRange(slower.x, 1.5f, 4f - 0.01f);
            for (var i = 0; i < 200; i++) slower = ClimbContact.ThrustGlide(slower + new Vector3(0.1f, 0f, 0f), slower.magnitude, brake, Step, 1f);
            Assert.Equal(1.5f, slower.x, 3);
        }

        [Fact]
        public void GlideBrakesAlongItsOwnWay()
        {
            var slower = ClimbContact.Brake(new Vector3(0f, 0f, -1f), ClimbContact.GloveBrake(500f, 1, 1f), Step);
            Assert.Equal(0f, slower.x, 5);
            Assert.InRange(slower.z, -1f, -0.9f);
        }

        private static Quaternion About(Vector3 axis, float degrees)
        {
            var half = degrees * Mathf.Deg2Rad / 2f;
            var sin = Mathf.Sin(half);
            return new Quaternion(axis.x * sin, axis.y * sin, axis.z * sin, Mathf.Cos(half));
        }

        [Fact]
        public void LeverTurnsAsFarAsTheHandTwistsAboutItsAxis()
        {
            var axis = new Vector3(-1f, 0f, 0f);
            Assert.Equal(40f, LeverMath.Twist(About(axis, 40f), axis), 2);
            Assert.Equal(-25f, LeverMath.Twist(About(axis, -25f), axis), 2);
        }

        [Fact]
        public void LeverIgnoresTheHandSwingingAwayFromItsAxis()
        {
            var axis = new Vector3(-1f, 0f, 0f);
            var swing = About(new Vector3(0f, 1f, 0f), 30f);
            var twist = About(axis, 50f);
            var both = new Quaternion(
                swing.w * twist.x + swing.x * twist.w + swing.y * twist.z - swing.z * twist.y,
                swing.w * twist.y - swing.x * twist.z + swing.y * twist.w + swing.z * twist.x,
                swing.w * twist.z + swing.x * twist.y - swing.y * twist.x + swing.z * twist.w,
                swing.w * twist.w - swing.x * twist.x - swing.y * twist.y - swing.z * twist.z);
            Assert.Equal(50f, LeverMath.Twist(both, axis), 0);
        }

        [Fact]
        public void LeverTwistIsTheSameForBothSignsOfTheTurn()
        {
            var axis = new Vector3(0f, 0f, 1f);
            var turn = About(axis, 70f);
            var flipped = new Quaternion(-turn.x, -turn.y, -turn.z, -turn.w);
            Assert.Equal(70f, LeverMath.Twist(flipped, axis), 2);
        }

        [Fact]
        public void TenKilogramsAreThrownAtMostByTheArmsForce()
        {
            // 170 N over 0.7 m into 10 kg and the arm's 2 kg: about 4.45 m/s.
            Assert.Equal(4.45f, ClimbContact.MostThrowSpeed(10f), 2);
            Assert.True(ClimbContact.MostThrowSpeed(1f) > ClimbContact.MostThrowSpeed(10f) * 1.9f);
        }

        [Fact]
        public void TwoArmsSpeedAPartUpFasterThanOne()
        {
            Assert.Equal(170f / 12f, ClimbContact.MostAcceleration(10f, 1), 3);
            Assert.Equal(340f / 14f, ClimbContact.MostAcceleration(10f, 2), 3);
        }
    }
}
