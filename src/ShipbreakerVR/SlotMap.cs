using System;
using System.Collections.Generic;

namespace ShipbreakerVR
{
    // Keeps a list that can only add at its end and remove by moving its last entry into the gap (Unity's
    // TransformAccessArray, see VrTransformRead) in step with a set of keys that comes in a new order each time, and
    // says for each slot of the list where its key stands in that order. So the list is never built anew: keys that
    // are gone leave, new ones join at the end, and the others stay in their slots.
    internal sealed class SlotMap<TKey>
    {
        private readonly Dictionary<TKey, int> slotOf = new Dictionary<TKey, int>();
        private readonly List<int> unknown = new List<int>();
        private TKey[] keys = new TKey[0];
        private int[] target = new int[0];
        private int[] seen = new int[0];
        private int round;

        // The slots in use.
        public int Count { get; private set; }
        // For each slot, the place of its key in the order of the last Sync. Longer than Count.
        public int[] Target => target;
        public TKey KeyAt(int slot) => keys[slot];

        // order[0..count) are the keys now, in the order their values are wanted. removeAtSwapBack(slot): the list
        // moves its last entry into this slot. tryAdd(key): the list took the key's entry as its new last one, or
        // false when it has none to take; such a key is offered again on the next Sync.
        public void Sync(TKey[] order, int count, Func<TKey, bool> tryAdd, Action<int> removeAtSwapBack)
        {
            round++;
            unknown.Clear();
            for (var i = 0; i < count; i++)
            {
                if (slotOf.TryGetValue(order[i], out var slot))
                {
                    target[slot] = i;
                    seen[slot] = round;
                }
                else unknown.Add(i);
            }
            // From the end, so the entry that moves into a gap is one that stays.
            for (var slot = Count - 1; slot >= 0; slot--)
            {
                if (seen[slot] == round) continue;
                removeAtSwapBack(slot);
                slotOf.Remove(keys[slot]);
                var last = --Count;
                if (slot != last)
                {
                    keys[slot] = keys[last];
                    target[slot] = target[last];
                    seen[slot] = seen[last];
                    slotOf[keys[slot]] = slot;
                }
                keys[last] = default;
            }
            foreach (var i in unknown)
            {
                if (slotOf.ContainsKey(order[i]) || !tryAdd(order[i])) continue;
                if (Count == keys.Length)
                {
                    var size = Math.Max(64, Count * 2);
                    Array.Resize(ref keys, size);
                    Array.Resize(ref target, size);
                    Array.Resize(ref seen, size);
                }
                keys[Count] = order[i];
                target[Count] = i;
                seen[Count] = round;
                slotOf[order[i]] = Count++;
            }
        }

        public void Clear()
        {
            slotOf.Clear();
            Array.Clear(keys, 0, keys.Length);
            Count = 0;
        }
    }
}
