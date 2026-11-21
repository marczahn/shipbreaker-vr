using System;
using System.Collections.Generic;
using System.Linq;
using ShipbreakerVR;
using Xunit;

namespace ShipbreakerVR.Tests
{
    public class SlotMapTests
    {
        // A list like the one the map is for: adds at the end, removes by moving the last entry into the gap.
        private sealed class SwapList
        {
            public readonly List<int> Items = new List<int>();
            public int Adds, Removes;
            public Func<int, bool> Has = key => true;

            public bool TryAdd(int key)
            {
                if (!Has(key)) return false;
                Items.Add(key);
                Adds++;
                return true;
            }

            public void RemoveAtSwapBack(int slot)
            {
                Items[slot] = Items[Items.Count - 1];
                Items.RemoveAt(Items.Count - 1);
                Removes++;
            }
        }

        private static void AssertInStep(SlotMap<int> map, SwapList list, int[] order)
        {
            Assert.Equal(list.Items.Count, map.Count);
            for (var slot = 0; slot < map.Count; slot++)
            {
                Assert.Equal(list.Items[slot], map.KeyAt(slot));
                Assert.Equal(list.Items[slot], order[map.Target[slot]]);
            }
            Assert.Equal(map.Count, list.Items.Distinct().Count());
        }

        [Fact]
        public void EverySlotPointsAtItsKeysPlace()
        {
            var map = new SlotMap<int>();
            var list = new SwapList();
            var order = new[] { 5, 3, 9, 1 };
            map.Sync(order, order.Length, list.TryAdd, list.RemoveAtSwapBack);
            AssertInStep(map, list, order);
            Assert.Equal(4, map.Count);
        }

        [Fact]
        public void ANewOrderMovesNothingInTheList()
        {
            var map = new SlotMap<int>();
            var list = new SwapList();
            var order = new[] { 5, 3, 9, 1 };
            map.Sync(order, order.Length, list.TryAdd, list.RemoveAtSwapBack);
            var turned = new[] { 1, 9, 3, 5 };
            map.Sync(turned, turned.Length, list.TryAdd, list.RemoveAtSwapBack);
            AssertInStep(map, list, turned);
            Assert.Equal(4, list.Adds);
            Assert.Equal(0, list.Removes);
        }

        [Fact]
        public void OnlyWhatChangedIsAddedAndRemoved()
        {
            var map = new SlotMap<int>();
            var list = new SwapList();
            var order = new[] { 1, 2, 3, 4, 5, 6 };
            map.Sync(order, order.Length, list.TryAdd, list.RemoveAtSwapBack);
            var next = new[] { 6, 7, 2, 4, 8 };
            map.Sync(next, next.Length, list.TryAdd, list.RemoveAtSwapBack);
            AssertInStep(map, list, next);
            Assert.Equal(8, list.Adds);
            Assert.Equal(3, list.Removes);
        }

        [Fact]
        public void OnlyTheFirstCountKeysOfTheOrderCount()
        {
            var map = new SlotMap<int>();
            var list = new SwapList();
            var order = new[] { 1, 2, 3, 4, 5, 6 };
            map.Sync(order, 6, list.TryAdd, list.RemoveAtSwapBack);
            map.Sync(order, 2, list.TryAdd, list.RemoveAtSwapBack);
            AssertInStep(map, list, order);
            Assert.Equal(2, map.Count);
        }

        [Fact]
        public void AKeyTheListCannotTakeIsOfferedAgain()
        {
            var map = new SlotMap<int>();
            var list = new SwapList { Has = key => key != 3 };
            var order = new[] { 1, 3, 2 };
            map.Sync(order, order.Length, list.TryAdd, list.RemoveAtSwapBack);
            AssertInStep(map, list, order);
            Assert.Equal(2, map.Count);
            list.Has = key => true;
            map.Sync(order, order.Length, list.TryAdd, list.RemoveAtSwapBack);
            AssertInStep(map, list, order);
            Assert.Equal(3, map.Count);
        }

        [Fact]
        public void StaysInStepOverManyRandomChanges()
        {
            var random = new Random(7);
            var map = new SlotMap<int>();
            var list = new SwapList();
            var keys = new List<int>();
            var next = 0;
            for (var step = 0; step < 500; step++)
            {
                for (var n = random.Next(0, 4); n > 0 && keys.Count > 0; n--) keys.RemoveAt(random.Next(keys.Count));
                for (var n = random.Next(0, 5); n > 0; n--) keys.Insert(random.Next(keys.Count + 1), next++);
                if (step % 50 == 49) keys.Clear();
                // In an array longer than the keys, as the mod's own.
                var order = keys.Concat(new[] { -1, -1, -1 }).ToArray();
                map.Sync(order, keys.Count, list.TryAdd, list.RemoveAtSwapBack);
                AssertInStep(map, list, order);
                Assert.Equal(keys.Count, map.Count);
            }
        }

        [Fact]
        public void ClearForgetsEveryKey()
        {
            var map = new SlotMap<int>();
            var list = new SwapList();
            var order = new[] { 1, 2, 3 };
            map.Sync(order, order.Length, list.TryAdd, list.RemoveAtSwapBack);
            map.Clear();
            list.Items.Clear();
            Assert.Equal(0, map.Count);
            map.Sync(order, order.Length, list.TryAdd, list.RemoveAtSwapBack);
            AssertInStep(map, list, order);
            Assert.Equal(3, map.Count);
        }
    }
}
