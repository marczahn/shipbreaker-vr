using UnityEngine;

namespace ShipbreakerVR
{
    // Measures holding on (VrClimb) for a test build, to find out why the view gets unsteady and why a held part gains
    // momentum. While a hand holds on, a line "Climbing trace:" every half second sums up the physics steps and frames
    // since the last one; letting go of the last hold logs the parts' speed against when they were taken. Only logs,
    // changes nothing (but for a probe on the player's body that counts its collisions while holding on).
    internal static class VrClimbTrace
    {
        static VrClimbTrace() => VrSession.OnUnload("climbing trace", RemoveProbe);

        private const float Period = 0.5f;
        private const int MaxLines = 400;

        private static int lines;
        private static float nextLine;

        // Physics steps since the last line.
        private static int steps;
        // How far each hand was from its hold before the step, in m.
        private static readonly Stat[] HandError = { new Stat(), new Stat() };
        // How far the controller moved in tracking space since the step before, in m (hand tremor, tracking noise).
        private static readonly Stat[] HandMotion = { new Stat(), new Stat() };
        private static readonly Vector3[] LastLocal = new Vector3[2];
        private static readonly bool[] HasLocal = new bool[2];
        // The controller's smoothed speed against the body, as VrClimb uses it, in m/s.
        private static readonly Stat[] HandSpeed = { new Stat(), new Stat() };
        // The velocity change VrClimb gave the body in a step, and the one something else gave it since the step before
        // (the game's thrust, brake or drag, collisions), in m/s.
        private static readonly Stat ClimbChange = new Stat();
        private static readonly Stat OtherChange = new Stat();
        private static Vector3 velocitySet;
        private static bool hasVelocitySet;
        // The impulse handed to the held part since the last line (N s), summed as vectors and as sizes.
        private static Vector3 impulseNet;
        private static float impulseSum;
        private static Rigidbody part;
        private static Vector3 partVelocityAtHold;
        private static Vector3 partSpinAtHold;

        // Frames since the last line.
        private static int frames;
        // How far the rig moved against the (interpolated) body from one frame to the next, in m: a head turn turns
        // the body, and the camera off its center swings the rig and with it the hands.
        private static readonly Stat RigShift = new Stat();
        private static Vector3 lastRigOffset;
        private static bool hasRigOffset;
        // How far the body turned per frame, in degrees.
        private static readonly Stat BodyTurn = new Stat(1f);
        private static Quaternion lastBodyRotation;
        // The interpolated body (transform) against its physics position, when the rig was placed, in m.
        private static readonly Stat InterpolationLag = new Stat();
        // The player's own solid colliders, and how far the head (VR camera) is from their middle, in m.
        private static Collider[] bodyColliders;
        private static bool bodyLogged;
        private static readonly Stat HeadToBody = new Stat();
        // Frames in which a body collider overlaps something else, how deep, and the deepest one's name.
        private static int overlapFrames;
        private static readonly Stat Overlap = new Stat();
        private static string overlapWith;
        private static readonly Collider[] Found = new Collider[16];
        // Collisions the physics reported for the player's body (ContactProbe): their impulses in N s.
        private static readonly Stat ContactImpulse = new Stat(1f);
        private static string contactWith;
        private static ContactProbe probe;

        private class Stat
        {
            private readonly float scale;
            private float sum;
            private int count;
            public float Max;

            // Values are logged times this (m as mm, m/s as mm/s).
            public Stat(float scale = 1000f) => this.scale = scale;

            public void Add(float value)
            {
                sum += value;
                count++;
                if (value > Max) Max = value;
            }

            public float Average => count > 0 ? sum / count : 0f;
            public int Count => count;

            public void Clear()
            {
                sum = 0f;
                count = 0;
                Max = 0f;
            }

            public override string ToString() => count > 0 ? $"{Average * scale:0.0}/{Max * scale:0.0}" : "-";
        }

        public static bool Active => lines < MaxLines;

        // The first hand took hold.
        public static void Start(Rigidbody player, Rigidbody body, bool movable)
        {
            if (!Active) return;
            if (player != null)
            {
                bodyColliders = System.Array.FindAll(player.GetComponentsInChildren<Collider>(), c => !c.isTrigger);
                if (probe == null) probe = player.gameObject.AddComponent<ContactProbe>();
                if (!bodyLogged) LogBody(player);
            }
            ClearPeriod();
            hasVelocitySet = false;
            hasRigOffset = false;
            HasLocal[0] = HasLocal[1] = false;
            nextLine = Time.unscaledTime + Period;
            TakePart(body, movable);
        }

        // A hand took hold of something with a rigidbody, movable or not; the first one is followed.
        public static void TakePart(Rigidbody body, bool movable)
        {
            if (!Active || part != null || body == null) return;
            part = body;
            partVelocityAtHold = body.velocity;
            partSpinAtHold = body.angularVelocity;
        }

        // Before VrClimb changes the body's velocity in a step.
        public static void StepStart(Rigidbody player)
        {
            if (!Active) return;
            steps++;
            if (hasVelocitySet) OtherChange.Add((player.velocity - velocitySet).magnitude);
        }

        public static void Hand(int index, Vector3 local, bool tracked, float error, float speed)
        {
            if (!Active) return;
            HandError[index].Add(error);
            HandSpeed[index].Add(speed);
            if (tracked && HasLocal[index]) HandMotion[index].Add((local - LastLocal[index]).magnitude);
            LastLocal[index] = local;
            HasLocal[index] = tracked;
        }

        public static void Impulse(Rigidbody body, Vector3 impulse)
        {
            if (!Active || body == null || body != part) return;
            impulseNet += impulse;
            impulseSum += impulse.magnitude;
        }

        // After VrClimb set the body's velocity in a step.
        public static void StepEnd(Rigidbody player, Vector3 change)
        {
            if (!Active) return;
            ClimbChange.Add(change.magnitude);
            velocitySet = player.velocity;
            hasVelocitySet = true;
        }

        // The rig was placed for the frame (VrClimb.RigPlaced), while a hand holds on.
        public static void Frame(Rigidbody player, Transform rig)
        {
            if (!Active || rig == null) return;
            frames++;
            var offset = rig.position - player.transform.position;
            if (hasRigOffset)
            {
                RigShift.Add((offset - lastRigOffset).magnitude);
                BodyTurn.Add(Quaternion.Angle(lastBodyRotation, player.rotation));
            }
            lastRigOffset = offset;
            lastBodyRotation = player.rotation;
            hasRigOffset = true;
            InterpolationLag.Add((player.transform.position - player.position).magnitude);
            MeasureBody();

            if (Time.unscaledTime < nextLine) return;
            nextLine = Time.unscaledTime + Period;
            Log(player);
            ClearPeriod();
        }

        // The last hand let go; flying on sets the body's velocity once more (push off).
        public static void End(Rigidbody player, string reason, Vector3 pushOffVelocity, bool pushed)
        {
            if (!Active) return;
            Log(player);
            var text = $"Climbing trace: all hands off ({reason})";
            if (player != null) text += pushed ? $", body set to {pushOffVelocity.magnitude:0.00} m/s (push off, no impulse on the part)" : $", body {player.velocity.magnitude:0.00} m/s";
            if (part != null)
                text += $"; part '{part.name}' {part.mass:0.#} kg: velocity {partVelocityAtHold.magnitude:0.000} -> {part.velocity.magnitude:0.000} m/s, "
                    + $"spin {partSpinAtHold.magnitude * Mathf.Rad2Deg:0.0} -> {part.angularVelocity.magnitude * Mathf.Rad2Deg:0.0} deg/s";
            Write(text);
            part = null;
            RemoveProbe();
            ClearPeriod();
        }

        // Once: the player's solid colliders, where they sit against the head, and the trigger colliders next to them.
        private static void LogBody(Rigidbody player)
        {
            var head = VrCamera.Instance != null ? VrCamera.Instance.transform.position : player.position;
            var text = $"Climbing trace: player colliders (head {Describe(player.transform.InverseTransformPoint(head))} in the body)";
            foreach (var collider in player.GetComponentsInChildren<Collider>())
            {
                text += $"; '{collider.name}' {collider.GetType().Name}{(collider.isTrigger ? " trigger" : "")} layer {collider.gameObject.layer}";
                if (collider is CapsuleCollider capsule) text += $" r {capsule.radius:0.00} h {capsule.height:0.00} axis {capsule.direction}";
                else if (collider is SphereCollider sphere) text += $" r {sphere.radius:0.00}";
                else if (collider is BoxCollider box) text += $" size {Describe(box.size)}";
                text += $", middle {Describe(player.transform.InverseTransformPoint(collider.bounds.center))}, bounds {Describe(collider.bounds.size)}";
            }
            Write(text);
            bodyLogged = true;
        }

        private static string Describe(Vector3 v) => $"({v.x:0.00}, {v.y:0.00}, {v.z:0.00})";

        // Each frame while holding on: the head against the body, and whether the body sticks into something.
        private static void MeasureBody()
        {
            if (bodyColliders == null || bodyColliders.Length == 0) return;
            if (VrCamera.Instance != null) HeadToBody.Add((VrCamera.Instance.transform.position - bodyColliders[0].bounds.center).magnitude);
            var deepest = 0f;
            foreach (var own in bodyColliders)
            {
                if (own == null || !own.enabled) continue;
                var bounds = own.bounds;
                var count = Physics.OverlapBoxNonAlloc(bounds.center, bounds.extents, Found, Quaternion.identity, Physics.DefaultRaycastLayers, QueryTriggerInteraction.Ignore);
                for (var i = 0; i < count; i++)
                {
                    var other = Found[i];
                    if (System.Array.IndexOf(bodyColliders, other) >= 0) continue;
                    var ownTransform = own.transform;
                    var otherTransform = other.transform;
                    if (!Physics.ComputePenetration(own, ownTransform.position, ownTransform.rotation, other, otherTransform.position, otherTransform.rotation, out _, out var depth)) continue;
                    if (depth <= deepest) continue;
                    deepest = depth;
                    overlapWith = other.name;
                }
            }
            if (deepest <= 0f) return;
            overlapFrames++;
            Overlap.Add(deepest);
        }

        private static void RemoveProbe()
        {
            if (probe != null) Object.Destroy(probe);
            probe = null;
        }

        private static void Contact(Collision collision)
        {
            if (!Active) return;
            var impulse = collision.impulse.magnitude;
            if (impulse >= ContactImpulse.Max) contactWith = collision.collider.name;
            ContactImpulse.Add(impulse);
        }

        // On the player's body while a hand holds on: Unity calls a body's collision callbacks on its own game object.
        private class ContactProbe : MonoBehaviour
        {
            private void OnCollisionEnter(Collision collision) => Contact(collision);
            private void OnCollisionStay(Collision collision) => Contact(collision);
        }

        private static void Log(Rigidbody player)
        {
            if (steps == 0 && frames == 0) return;
            var text = $"Climbing trace: {steps} steps, {frames} frames; hand off its hold mm avg/max L {HandError[0]} R {HandError[1]}; "
                + $"controller motion mm/step L {HandMotion[0]} R {HandMotion[1]}; hand speed mm/s L {HandSpeed[0]} R {HandSpeed[1]}; "
                + $"head to body mm {HeadToBody}; body overlaps in {overlapFrames} frames, mm avg/max {Overlap}{(overlapFrames > 0 ? $" ('{overlapWith}')" : "")}; "
                + $"contacts {ContactImpulse.Count}, N s avg/max {ContactImpulse}{(ContactImpulse.Count > 0 ? $" ('{contactWith}')" : "")}; "
                + $"body dv mm/s per step: climbing {ClimbChange}, other {OtherChange}; "
                + $"rig vs body mm/frame {RigShift}, body turn deg/frame {BodyTurn}, interpolation lag mm {InterpolationLag}";
            if (player != null) text += $"; body {player.velocity.magnitude:0.00} m/s";
            if (part != null)
                text += $"; impulse on '{part.name}' ({part.mass:0.#} kg) net {impulseNet.magnitude:0.00} N s of {impulseSum:0.00} N s, "
                    + $"part {part.velocity.magnitude:0.000} m/s, spin {part.angularVelocity.magnitude * Mathf.Rad2Deg:0.0} deg/s";
            Write(text);
        }

        private static void Write(string text)
        {
            if (lines++ >= MaxLines) return;
            Plugin.Log.LogInfo(lines == MaxLines ? text + " (trace ends here)" : text);
        }

        private static void ClearPeriod()
        {
            steps = 0;
            frames = 0;
            foreach (var stat in HandError) stat.Clear();
            foreach (var stat in HandMotion) stat.Clear();
            foreach (var stat in HandSpeed) stat.Clear();
            ClimbChange.Clear();
            OtherChange.Clear();
            RigShift.Clear();
            BodyTurn.Clear();
            InterpolationLag.Clear();
            HeadToBody.Clear();
            Overlap.Clear();
            overlapFrames = 0;
            ContactImpulse.Clear();
            impulseNet = Vector3.zero;
            impulseSum = 0f;
        }
    }
}
