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
    // This is only what the line looks like. What it does to the tool at its end is worked out between the two ends
    // (VrLooseTools). Engine-free, so the tests run it (ToolLineTests).
    internal sealed class ToolLine
    {
        public readonly Vector3[] At;
        private readonly Vector3[] before;
        // No sharp bends: a point's two neighbors stay at least this share of two links apart (0.95: a bend of at most
        // about 36 degrees at each point). Without it slack folded into a zigzag, link against link.
        private const float LeastSpan = 0.95f;

        public ToolLine(int points)
        {
            At = new Vector3[points];
            before = new Vector3[points];
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
        public void Step(Vector3 from, Vector3 to, float length, Vector3 carriedBefore, Vector3 carried, float keep, float bend, int rounds)
        {
            var last = At.Length - 1;
            for (var i = 1; i < last; i++)
            {
                var move = (At[i] - before[i] - carriedBefore) * keep + carried;
                before[i] = At[i];
                At[i] += move;
            }
            At[0] = before[0] = from;
            At[last] = before[last] = to;
            if (bend > 0f)
            {
                // From the points as they were, so the line is smoothed evenly, not from one end.
                var previous = At[0];
                for (var i = 1; i < last; i++)
                {
                    var own = At[i];
                    At[i] += ((previous + At[i + 1]) * 0.5f - own) * bend;
                    previous = own;
                }
            }
            var link = length / last;
            for (var round = 0; round < rounds; round++)
            {
                for (var i = 0; i < last; i++)
                {
                    var along = At[i + 1] - At[i];
                    var now = along.magnitude;
                    if (now < 1e-7f) continue;
                    var back = along * (1f - link / now);
                    // The ends stay where they are held; the point next to one takes the whole correction.
                    if (i == 0) At[1] -= back;
                    else if (i + 1 == last) At[i] += back;
                    else
                    {
                        At[i] += back * 0.5f;
                        At[i + 1] -= back * 0.5f;
                    }
                }
                // Kept in the same passes as the links, or one undoes the other.
                if (round % 2 == 1) continue;
                var span = link * 2f * LeastSpan;
                for (var i = 0; i + 2 <= last; i++)
                {
                    var across = At[i + 2] - At[i];
                    var now = across.magnitude;
                    if (now >= span || now < 1e-7f) continue;
                    var apart = across * (span / now - 1f);
                    if (i == 0) At[2] += apart;
                    else if (i + 2 == last) At[i] -= apart;
                    else
                    {
                        At[i] -= apart * 0.5f;
                        At[i + 2] += apart * 0.5f;
                    }
                }
            }
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
