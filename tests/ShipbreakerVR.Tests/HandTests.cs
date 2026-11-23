using BBI.Unity.Game;
using Xunit;
using Equipment = BBI.Unity.Game.EquipmentController.Equipment;

namespace ShipbreakerVR.Tests
{
    public class HandTests
    {
        private static readonly VrHand Left = VrHand.Left;
        private static readonly VrHand Right = VrHand.Right;

        // Both hands empty, the right one the tool hand, the tools aiming with the hand.
        public HandTests()
        {
            SettingsTests.Bind();
            VrHand.StopHolding(Left);
            VrHand.StopHolding(Right);
            VrHand.DropSecond();
            VrHand.Unhide();
            VrHand.ResetGameTool();
            VrHand.Apply(true, Equipment.None);
        }

        // The empty hand's fingers from the controller (0.33.0).
        [Theory]
        [InlineData(0f, 0f)]
        [InlineData(0.03f, 0f)]
        [InlineData(0.465f, 0.5f)]
        [InlineData(0.9f, 1f)]
        [InlineData(1f, 1f)]
        public void TheGripClosesTheHandAsFarAsItIsPressed(float grip, float closed) =>
            Assert.Equal(closed, VrInput.Squeezed(grip), 3);

        [Theory]
        [InlineData(0.02f, 0f)]
        [InlineData(0.9f, 1f)]
        public void TheTriggerClosesTheIndexFinger(float trigger, float closed) =>
            Assert.Equal(closed, VrInput.Pulled(trigger), 3);

        private static VrInput.Hand TestHand() => new VrInput.Hand(UnityEngine.XR.XRNode.LeftHand, "test");

        // index touched, buttons touched, stick touched, thumb rest touched; nothing used.
        private static void Touch(VrInput.Hand hand, float now, bool index = false, bool buttons = false, bool stick = false, bool rest = false) =>
            hand.Fingers(now, index, false, buttons, false, stick, false, rest);

        [Fact]
        public void WithoutSensorsTheFingersAreOnTheController()
        {
            var hand = TestHand();
            Touch(hand, 1f);
            Touch(hand, 5f);
            Assert.True(hand.IndexOn);
            Assert.True(hand.ThumbOn);
        }

        // A thumb lying on the thumb rest is down; without that sensor nobody can tell, so the thumb stays on.
        [Fact]
        public void TheThumbIsUpOnlyWithTheThumbRestsSensorToo()
        {
            var hand = TestHand();
            Touch(hand, 1f, buttons: true);
            Touch(hand, 5f);
            Assert.True(hand.ThumbOn);
            Touch(hand, 6f, rest: true);
            Touch(hand, 7f, rest: true);
            Assert.True(hand.ThumbOn);
            Touch(hand, 8f);
            Assert.False(hand.ThumbOn);
        }

        [Fact]
        public void AFingerIsOffOnceItsSensorToldOfATouchAndLetGo()
        {
            var hand = TestHand();
            Touch(hand, 1f, index: true, buttons: true, rest: true);
            Assert.True(hand.IndexOn && hand.ThumbOn);
            // A flicker of the sensor is not a finger off.
            Touch(hand, 1f + VrInput.FingerOffAfter * 0.5f);
            Assert.True(hand.IndexOn && hand.ThumbOn);
            Touch(hand, 2f);
            Assert.False(hand.IndexOn);
            Assert.False(hand.ThumbOn);
            // The trigger pulled or a button pressed has a finger on it, whatever the sensor tells.
            hand.Fingers(3f, false, true, false, true, false, false, false);
            Assert.True(hand.IndexOn && hand.ThumbOn);
        }

        // Raik's right controller, 2026-11-23: the stick tells of a touch all the time. Once the thumb has lain on the
        // thumb rest for a while, the stick is left out, and a thumb lifted off the rest is up.
        [Fact]
        public void AStickThatHangsIsLeftOutOnceTheThumbLiesOnTheThumbRest()
        {
            var hand = TestHand();
            Touch(hand, 1f, stick: true);
            Touch(hand, 2f, stick: true, rest: true);
            Touch(hand, 2f + VrInput.SensorHangsAfter * 0.5f, stick: true, rest: true);
            Assert.False(hand.StickHangs);
            Touch(hand, 2.1f + VrInput.SensorHangsAfter, stick: true, rest: true);
            Assert.True(hand.StickHangs);
            Touch(hand, 4f, stick: true);
            Touch(hand, 5f, stick: true);
            Assert.False(hand.ThumbOn);
            // A button touched is the thumb again, and so is the stick moved.
            Touch(hand, 6f, stick: true, buttons: true);
            Assert.True(hand.ThumbOn);
            Touch(hand, 8f, stick: true);
            Assert.False(hand.ThumbOn);
            hand.Fingers(9f, false, false, false, false, true, true, false);
            Assert.True(hand.ThumbOn);
            Assert.False(hand.StickHangs);
        }

        // A thumb that goes from the stick to the thumb rest for a moment and back does not make the stick hang, and one
        // resting on the stick alone stays down.
        [Fact]
        public void AStickThatLetsGoDoesNotHang()
        {
            var hand = TestHand();
            Touch(hand, 1f, rest: true);
            Touch(hand, 2f, stick: true);
            Touch(hand, 3f, rest: true);
            Touch(hand, 5f, rest: true);
            Touch(hand, 6f, stick: true);
            Touch(hand, 9f, stick: true);
            Assert.False(hand.StickHangs);
            Assert.True(hand.ThumbOn);
        }

        [Theory]
        [InlineData(0f, 0f)]
        [InlineData(0.02f, 50f)]
        [InlineData(0.1f, 250f)]
        [InlineData(0.5f, 250f)]
        public void TheStiffFingerPushesWithALimitedForce(float depth, float force) =>
            Assert.Equal(force, ClimbContact.PokePush(depth), 3);

        [Fact]
        public void GameToolGoesIntoTheToolHand()
        {
            VrHand.Apply(true, Equipment.CuttingTool);
            Assert.Equal(HandState.GameTool, Right.State);
            Assert.Equal(Equipment.CuttingTool, Right.Tool);
            Assert.Equal(HandState.Empty, Left.State);
        }

        [Fact]
        public void WithoutAimingBothHandsAreEmpty()
        {
            VrHand.Apply(false, Equipment.CuttingTool);
            Assert.Equal(HandState.Empty, Right.State);
            Assert.Equal(HandState.Empty, Left.State);
        }

        // The second tool is kept while the tools do not aim with the hand, and comes back with it.
        [Fact]
        public void WithoutAimingTheSecondToolRests()
        {
            VrHand.KeepSecond(Left, Equipment.GrappleHook);
            VrHand.Apply(false, Equipment.CuttingTool);
            Assert.Equal(HandState.Empty, Left.State);
            Assert.Equal(Equipment.GrappleHook, VrHand.Second);
            VrHand.Apply(true, Equipment.CuttingTool);
            Assert.Equal(HandState.SecondTool, Left.State);
        }

        [Fact]
        public void LeftHandDrawsAndPutsAway()
        {
            VrHand.TakeGameTool(Left, Equipment.GrappleHook);
            Assert.Equal(HandState.GameTool, Left.State);
            Assert.Equal(HandState.Empty, Right.State);
            VrHand.ResetGameTool();
            Assert.Same(Right, VrHand.GameToolHand);
        }

        [Fact]
        public void SecondToolBecomesTheGamesTool()
        {
            VrHand.TakeGameTool(Right, Equipment.CuttingTool);
            VrHand.KeepSecond(Left, Equipment.GrappleHook);
            Assert.Equal(HandState.SecondTool, Left.State);
            Assert.Equal(Equipment.GrappleHook, VrHand.Second);
            Assert.True(Left.HoldsTool);

            VrHand.DropSecond(intoHand: true);
            Assert.Same(Left, VrHand.GameToolHand);
            Assert.Equal(HandState.GameTool, Left.State);
            Assert.Equal(Equipment.GrappleHook, Left.Tool);
            Assert.Equal(HandState.Empty, Right.State);
        }

        [Fact]
        public void HiddenToolComesBack()
        {
            VrHand.Hide(Left, Equipment.GrappleHook);
            Assert.Equal(HandState.HiddenTool, Left.State);
            Assert.False(Left.HoldsTool);
            VrHand.Unhide(intoHand: true);
            Assert.Equal(HandState.GameTool, Left.State);
            Assert.Equal(Equipment.None, VrHand.Hidden);
        }

        [Fact]
        public void OnlyAFreeHandHoldsOn()
        {
            VrHand.Apply(true, Equipment.CuttingTool);
            Assert.False(VrHand.StartHolding(Right));
            Assert.True(VrHand.StartHolding(Left));
            Assert.Equal(HandState.Holding, Left.State);
            Assert.False(Left.HoldsTool);
        }

        [Fact]
        public void BeltZoneKeepsTheHandFromHoldingOn()
        {
            Left.Zone = VrToolBelt.Zone.DemoCharge;
            try
            {
                Assert.False(VrHand.StartHolding(Left));
            }
            finally
            {
                Left.Zone = VrToolBelt.Zone.None;
            }
        }

        [Fact]
        public void TakingAToolLetsGo()
        {
            Assert.True(VrHand.StartHolding(Left));
            VrHand.TakeGameTool(Left, Equipment.GrappleHook);
            Assert.Equal(HandState.GameTool, Left.State);
        }
    }
}
