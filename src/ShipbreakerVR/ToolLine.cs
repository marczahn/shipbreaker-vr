using UnityEngine;

namespace ShipbreakerVR
{
    // Experiment (Raik, 2026-11-17): the line that ties a tool to the hip, as a chain of points (Verlet): each point keeps
    // moving as it did, and each link is brought back to its length. The line keeps its length both ways and is a
    // little stiff against bending (bend), as a cable is: with more paid out than the way between its ends it bows out
    // in a round curve instead of folding, swings, and carries waves from an end that jerks; with the ends as far apart
    // as it is long it is straight. There is no gravity and next to no air, so only a little of the movement is lost
    // each second (keep), and that against the player's own way (carried), which the line shares: flying along at speed
    // it must not trail behind as in a wind.
    // Up to two of its points can be held in the hands (Hold): they go with the hands, and each stretch between the
    // ends and the hands has its own length, so a pull of a hand goes to the next stretch alone.
    // This is only what the line looks like. What it does to the tool at its end is worked out between the two ends
    // (VrLooseTools). Engine-free, so the tests run it (ToolLineTests).
    internal sealed class ToolLine
    {
        // A point held in a hand: which, where the hand is, and how much line lies up to it from the fixed point before
        // (the start, or the other hand). Point -1: none.
        public struct Hold
        {
            public int Point;
            public Vector3 At;
            public float Length;

            public static readonly Hold None = new Hold { Point = -1 };
        }

        public readonly Vector3[] At;
        private readonly Vector3[] before;
        // Each link's length and whether a point is fixed, for the step in hand; and room to lay the points anew.
        private readonly float[] link;
        private readonly bool[] pinned;
        private readonly float[] arc;
        private readonly Vector3[] laid, laidBefore;
        // No sharp bends: a point's two neighbors stay at least this share of two links apart (0.95: a bend of at most
        // about 36 degrees at each point). Without it slack folded into a zigzag, link against link.
        private const float LeastSpan = 0.95f;

        public ToolLine(int points)
        {
            At = new Vector3[points];
            before = new Vector3[points];
            link = new float[points];
            pinned = new bool[points];
            arc = new float[points];
            laid = new Vector3[points];
            laidBefore = new Vector3[points];
        }

        // Between its ends, at rest, bowed a little towards bow (how far its middle is off the straight way), so slack
        // has a side to go to: links pushed along a perfectly straight line never leave it.
        public void Lay(Vector3 from, Vector3 to, Vector3 bow)
        {
            for (var i = 0; i < At.Length; i++)
            {
                var t = i / (float)(At.Length - 1);
                At[i] = before[i] = Vector3.Lerp(from, to, t) + bow * Mathf.Sin(Mathf.PI * t);
            }
        }

        // One step: the ends at from and to, length paid out, the player's own way in the step before and in this one
        // (carriedBefore, carried), keep of the line's own movement left, bend how far each point goes towards the
        // middle of its neighbors (0 to 1), the links brought to their length in rounds passes.
        public void Step(Vector3 from, Vector3 to, float length, Vector3 carriedBefore, Vector3 carried, float keep, float bend, int rounds) =>
            Step(from, to, length, Hold.None, Hold.None, carriedBefore, carried, keep, bend, rounds);

        // The same with one point held in a hand (held, at heldAt): nearLength of line lies between from and the hand,
        // length between the hand and to.
        public void Step(Vector3 from, Vector3 to, float length, int held, Vector3 heldAt, float nearLength, Vector3 carriedBefore, Vector3 carried, float keep, float bend, int rounds) =>
            Step(from, to, length, new Hold { Point = held, At = heldAt, Length = nearLength }, Hold.None, carriedBefore, carried, keep, bend, rounds);

        // The same with up to two points held, first the one nearer to from: length is the line from the last held point
        // to the end at to.
        public void Step(Vector3 from, Vector3 to, float length, Hold first, Hold second, Vector3 carriedBefore, Vector3 carried, float keep, float bend, int rounds)
        {
            var last = At.Length - 1;
            if (first.Point <= 0 || first.Point >= last)
            {
                first = second;
                second = Hold.None;
            }
            if (second.Point <= first.Point || second.Point >= last) second = Hold.None;
            if (first.Point <= 0 || first.Point >= last) first = Hold.None;
            for (var i = 1; i < last; i++)
            {
                var move = (At[i] - before[i] - carriedBefore) * keep + carried;
                before[i] = At[i];
                At[i] += move;
                pinned[i] = false;
            }
            At[0] = before[0] = from;
            At[last] = before[last] = to;
            pinned[0] = pinned[last] = true;
            // The stretches: from the start to the first hand, between the hands, and on to the end.
            var start = 0;
            if (first.Point > 0)
            {
                At[first.Point] = before[first.Point] = first.At;
                pinned[first.Point] = true;
                for (var i = start; i < first.Point; i++) link[i] = first.Length / (first.Point - start);
                start = first.Point;
            }
            if (second.Point > 0)
            {
                At[second.Point] = before[second.Point] = second.At;
                pinned[second.Point] = true;
                for (var i = start; i < second.Point; i++) link[i] = second.Length / (second.Point - start);
                start = second.Point;
            }
            for (var i = start; i < last; i++) link[i] = length / (last - start);
            if (bend > 0f)
            {
                // From the points as they were, so the line is smoothed evenly, not from one end.
                var previous = At[0];
                for (var i = 1; i < last; i++)
                {
                    var own = At[i];
                    if (!pinned[i]) At[i] += ((previous + At[i + 1]) * 0.5f - own) * bend;
                    previous = own;
                }
            }
            for (var round = 0; round < rounds; round++)
            {
                for (var i = 0; i < last; i++)
                {
                    if (pinned[i] && pinned[i + 1]) continue;
                    var along = At[i + 1] - At[i];
                    var now = along.magnitude;
                    if (now < 1e-7f) continue;
                    var back = along * (1f - link[i] / now);
                    // The ends and the held points stay where they are; the point next to one takes the whole correction.
                    if (pinned[i]) At[i + 1] -= back;
                    else if (pinned[i + 1]) At[i] += back;
                    else
                    {
                        At[i] += back * 0.5f;
                        At[i + 1] -= back * 0.5f;
                    }
                }
                // Kept in the same passes as the links, or one undoes the other.
                if (round % 2 == 1) continue;
                for (var i = 0; i + 2 <= last; i++)
                {
                    // The line may bend sharply in the hand that holds it.
                    if (pinned[i + 1] || (pinned[i] && pinned[i + 2])) continue;
                    var span = (link[i] + link[i + 1]) * LeastSpan;
                    var across = At[i + 2] - At[i];
                    var now = across.magnitude;
                    if (now >= span || now < 1e-7f) continue;
                    var apart = across * (span / now - 1f);
                    if (pinned[i]) At[i + 2] += apart;
                    else if (pinned[i + 2]) At[i] -= apart;
                    else
                    {
                        At[i] -= apart * 0.5f;
                        At[i + 2] += apart * 0.5f;
                    }
                }
            }
        }

        // The place on the line nearest to a point (a hand that takes hold of it): how far the line is from the point,
        // and how much line lies from the start up to that place.
        public float Nearest(Vector3 point, out float along)
        {
            var last = At.Length - 1;
            var nearest = float.MaxValue;
            var passed = 0f;
            along = 0f;
            for (var i = 0; i < last; i++)
            {
                var step = At[i + 1] - At[i];
                var length = step.magnitude;
                var t = length > 1e-6f ? Mathf.Clamp01(Vector3.Dot(point - At[i], step) / (length * length)) : 0f;
                var distance = (At[i] + step * t - point).sqrMagnitude;
                if (distance < nearest)
                {
                    nearest = distance;
                    along = passed + length * t;
                }
                passed += length;
            }
            return Mathf.Sqrt(nearest);
        }

        // How much line lies from the start up to a point of the chain.
        public float Along(int point)
        {
            var length = 0f;
            for (var i = 0; i < point && i + 1 < At.Length; i++) length += (At[i + 1] - At[i]).magnitude;
            return length;
        }

        // The point of the chain for a place that much line from the start, were the points spread evenly.
        public int PointAt(float along)
        {
            var total = Length();
            return total > 1e-6f ? Mathf.RoundToInt(along / total * (At.Length - 1)) : 0;
        }

        // Lays the points anew along the line as it lies, evenly between its ends and the places that are to be held:
        // point first comes to lie firstAlong of line from the start, point second secondAlong (-1 for none). So
        // the hands can take hold anywhere and again and again, without the chain running out of points on one side;
        // the line keeps its shape and its movement.
        public void Spread(int first, float firstAlong, int second, float secondAlong)
        {
            var last = At.Length - 1;
            arc[0] = 0f;
            for (var i = 0; i < last; i++) arc[i + 1] = arc[i] + (At[i + 1] - At[i]).magnitude;
            var total = arc[last];
            if (total < 1e-6f) return;
            var from = 0;
            var fromAlong = 0f;
            for (var stretch = 0; stretch < 3; stretch++)
            {
                var to = stretch == 0 ? first : stretch == 1 ? second : last;
                var toAlong = stretch == 0 ? firstAlong : stretch == 1 ? secondAlong : total;
                if (to <= from || to > last) continue;
                toAlong = Mathf.Clamp(toAlong, fromAlong, total);
                var old = 0;
                for (var i = from; i <= to; i++)
                {
                    var along = Mathf.Lerp(fromAlong, toAlong, (i - from) / (float)(to - from));
                    while (old < last - 1 && arc[old + 1] < along) old++;
                    var span = arc[old + 1] - arc[old];
                    var t = span > 1e-7f ? Mathf.Clamp01((along - arc[old]) / span) : 0f;
                    laid[i] = Vector3.Lerp(At[old], At[old + 1], t);
                    laidBefore[i] = Vector3.Lerp(before[old], before[old + 1], t);
                }
                from = to;
                fromAlong = toAlong;
            }
            if (from != last) return;
            System.Array.Copy(laid, At, At.Length);
            System.Array.Copy(laidBefore, before, before.Length);
        }

        // The line's length along its points.
        public float Length()
        {
            var length = 0f;
            for (var i = 0; i + 1 < At.Length; i++) length += (At[i + 1] - At[i]).magnitude;
            return length;
        }

        // How much line is out after dt: never less than the straight way between the ends (up to most, the whole line).
        // With a reel (the tool in the hand) it follows that way plus slack of it and spare, paying out at once and
        // winding in at reel per second; without one (the tool afloat) what is out stays out.
        public static float PaidOut(float paidOut, float straight, float most, bool reels, float slack, float spare, float reel, float dt)
        {
            var least = Mathf.Min(straight, most);
            if (!reels) return Mathf.Clamp(paidOut, least, most);
            var wanted = Mathf.Min(straight * (1f + slack) + spare, most);
            if (paidOut > wanted) paidOut = Mathf.Max(wanted, paidOut - reel * dt);
            else paidOut = wanted;
            return Mathf.Clamp(paidOut, least, most);
        }

        // What the taut line does between the tool at its end and the body at the hip: the impulse along the line (from
        // the hip to the tool, a unit) that stops them moving apart, in kg m/s; zero while they do not. apart is how fast
        // the tool's end moves away from the hip, over how far the line is stretched past its length (pulled back at
        // pull per second, at most mostPull), toolShare and bodyShare what an impulse of one moves each (1 / mass, with
        // the tool's turning in it).
        public static float Taut(float apart, float over, float pull, float mostPull, float toolShare, float bodyShare)
        {
            if (over <= 0f) return 0f;
            var wanted = -Mathf.Min(over * pull, mostPull);
            if (apart <= wanted) return 0f;
            var shares = toolShare + bodyShare;
            return shares > 1e-9f ? (apart - wanted) / shares : 0f;
        }
    }
}
