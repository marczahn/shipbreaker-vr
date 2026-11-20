using UnityEngine;
using Xunit;

namespace ShipbreakerVR.Tests
{
    public class TestBayTests
    {
        [Fact]
        public void GridSpansTheTwoWideSidesOfTheTriggerBox()
        {
            // The furnace's trigger: 17.4 m square and 4.4 m deep along its z.
            Assert.True(VrTestBay.Grid(new Vector3(17.4f, 17.4f, 4.4f), out var a, out var b, out var columns, out var rows));
            Assert.Equal(0, a);
            Assert.Equal(1, b);
            Assert.Equal(11, columns);
            Assert.Equal(11, rows);
        }

        [Theory]
        [InlineData(2f, 9f, 6f, 1, 2, 6, 4)]
        [InlineData(9f, 2f, 6f, 2, 0, 4, 6)]
        public void GridLeavesOutTheThinSideWhicheverItIs(float x, float y, float z, int a, int b, int columns, int rows)
        {
            Assert.True(VrTestBay.Grid(new Vector3(x, y, z), out var gotA, out var gotB, out var gotColumns, out var gotRows));
            Assert.Equal((a, b, columns, rows), (gotA, gotB, gotColumns, gotRows));
        }

        [Fact]
        public void NoGridInABoxSmallerThanACell()
        {
            Assert.False(VrTestBay.Grid(new Vector3(1f, 8f, 0.5f), out _, out _, out _, out _));
        }

        [Fact]
        public void CellsStayInsideTheBoxAndAtLeastACellApart()
        {
            const float side = 17.4f;
            const int cells = 11;
            Assert.True(VrTestBay.Share(0, cells) * side - VrTestBay.Cell / 2f >= -side / 2f);
            Assert.True(VrTestBay.Share(cells - 1, cells) * side + VrTestBay.Cell / 2f <= side / 2f);
            for (var i = 1; i < cells; i++)
                Assert.True((VrTestBay.Share(i, cells) - VrTestBay.Share(i - 1, cells)) * side >= VrTestBay.Cell - 0.001f);
        }
    }
}
