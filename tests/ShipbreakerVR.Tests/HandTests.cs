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
