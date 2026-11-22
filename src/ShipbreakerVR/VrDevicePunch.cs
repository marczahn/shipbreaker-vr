using System.Collections.Generic;
using BBI.Unity.Game;
using BepInEx.Configuration;
using UnityEngine;

namespace ShipbreakerVR
{
    // Devices that are stripped for a part of the truck (VrPullOff.Extractable: computer terminals, data banks and the
    // like) also give it up to blows of the fist (Raik, 2026-11-22: "for fun, we try it out"). Not by time, by blows:
    // "the first blow takes a third of the device's health, the second the second third, and at the third it breaks".
    // At the third the device is stripped as by its prompt (VrPullOff.StripNow).
    // A blow: a closed hand (grip held) that holds nothing, no tool either, comes onto the device faster than From, by
    // the controller's own swing against the body, not the body's flight, and into its surface. The hand is on the device
    // when the controller is within Fist of a solid shape on the device's body and of a shape of the device itself (its
    // 'VOL_Interactable', which is what a hand holds of it). A grip closed only as the hand arrives takes hold instead
    // (VrClimb.CatchTime), so the fist is made before. Each blow: a buzz, harder and longer each time, the game's sparks
    // of metal on metal and the sound of a part breaking off (VrPullOff.Blow). Experimente/GeraeteSchlag switches it off.
    // Raik's test (2026-11-22): the blows work, but he felt and heard nothing. The buzz was one impulse of 0.12 s; it is
    // now sent anew each frame for its time, as the buzz of pulling is, which he feels. A sound for the blow is a thing
    // for the next release (he and Marc think of one).
    internal static class VrDevicePunch
    {
        // Blows it takes.
        public const int Blows = 3;
        // The fist's least speed, in m/s as the arm really swings.
        private const float From = 1.5f;
        // Moves faster than this are a jump of the tracking, not a swing.
        private const float Jump = 15f;
        // How near the controller is to the device's surface when the knuckles meet it, in m at world scale 1.
        private const float Fist = 0.09f;
        // The same hand strikes again only after this long, in seconds.
        private const float Again = 0.25f;
        // A blow's buzz: how long for the first blow and for the last, in seconds.
        private const float BuzzFirst = 0.18f;
        private const float BuzzLast = 0.35f;

        private sealed class Arm
        {
            public Vector3 Last;
            public bool Known;
            public float Speed;
            public bool On;
            public float BlowAt = -10f;
            public float BuzzUntil;
            public float BuzzStrength;
        }

        private static readonly Arm[] Arms = { new Arm(), new Arm() };
        private static readonly Dictionary<InteractableObject, int> Taken = new Dictionary<InteractableObject, int>();
        private static readonly List<InteractableObject> Gone = new List<InteractableObject>();
        private static readonly Collider[] Found = new Collider[32];
        private static ConfigEntry<bool> on;
        private static int logs;

        static VrDevicePunch() => VrSession.OnUnload("device punch", Release);

        private static bool On
        {
            get
            {
                if (on == null && VrSettings.File != null)
                    on = VrSettings.Bind("Experimente", "GeraeteSchlag", true,
                        "Experiment: a device that is stripped for a spare part (computer terminal, data bank and the like) also gives it up to three blows of the fist: grip held, nothing in the hand.");
                return on == null || on.Value;
            }
        }

        // From VrFrame once the rig is placed, after the hands took hold or let go.
        public static void Update()
        {
            if (!VrXr.IsRunning || !On)
            {
                Arms[0].Known = Arms[1].Known = false;
                Arms[0].On = Arms[1].On = false;
                return;
            }
            Step(VrInput.Left, Arms[0]);
            Step(VrInput.Right, Arms[1]);
        }

        private static void Step(VrInput.Hand input, Arm arm)
        {
            // A blow is felt for its time, whatever the hand does after it.
            if (Time.time < arm.BuzzUntil) input.Buzz(arm.BuzzStrength, 0.05f);
            var vr = VrCamera.Instance;
            var rig = vr != null ? vr.transform.parent : null;
            var dt = Time.deltaTime;
            if (rig == null || dt <= 0f || !input.Tracked || !input.TryGetPosition(out var local))
            {
                arm.Known = false;
                arm.On = false;
                return;
            }
            var step = arm.Known ? local - arm.Last : Vector3.zero;
            var speed = step.magnitude / dt;
            // The blow lands a frame after the arm was fastest as often as not.
            var fastest = Mathf.Max(speed, arm.Speed);
            arm.Last = local;
            arm.Known = true;
            arm.Speed = speed;
            var fist = input.Grip.Down && VrHand.Of(input.Node).State == HandState.Empty && VrClimb.HeldCollider(input) == null
                && !VrChargeByHand.Holds(input);
            if (!fist || speed > Jump)
            {
                arm.On = false;
                return;
            }
            var at = rig.TransformPoint(local);
            var reach = Fist * VrCamera.WorldScale;
            var count = Physics.OverlapSphereNonAlloc(at, reach, Found, ~0, QueryTriggerInteraction.Collide);
            InteractableObject device = null;
            for (var i = 0; i < count && device == null; i++)
            {
                var each = Found[i].GetComponentInParent<InteractableObject>();
                if (VrPullOff.Extractable(each)) device = each;
            }
            var ship = device != null ? device.GetComponentInParent<Rigidbody>() : null;
            var touches = false;
            if (device != null)
                for (var i = 0; i < count && !touches; i++)
                    touches = !Found[i].isTrigger && Found[i].attachedRigidbody == ship;
            var came = touches && !arm.On;
            arm.On = touches;
            if (!came || step.sqrMagnitude < 1e-10f) return;
            // Into the surface, not along it or away: the way the fist goes meets a solid shape of that body.
            var way = rig.TransformDirection(step).normalized;
            var ray = new Ray(at - way * (2f * reach), way);
            var met = false;
            var hit = default(RaycastHit);
            for (var i = 0; i < count && !met; i++)
                met = !Found[i].isTrigger && Found[i].attachedRigidbody == ship && Found[i].Raycast(ray, out hit, 4f * reach);
            var part = device.GetComponentInParent<StructurePart>();
            var name = part != null ? part.name : device.name;
            Taken.TryGetValue(device, out var before);
            if (!Punch.Lands(fastest, From, met, Time.time - arm.BlowAt, Again))
            {
                if (logs++ < 60)
                    Plugin.Log.LogInfo($"Device punch: {input.Name} fist came onto '{name}' at {fastest:0.0} m/s, {(met ? "" : "not into its surface, ")}no blow (from {From:0.0} m/s, {before} of {Blows} blows taken)");
                return;
            }
            arm.BlowAt = Time.time;
            var blows = before + 1;
            var share = (blows - 1) / (Blows - 1f);
            arm.BuzzStrength = Mathf.Lerp(0.7f, 1f, share);
            arm.BuzzUntil = Time.time + Mathf.Lerp(BuzzFirst, BuzzLast, share);
            input.Buzz(arm.BuzzStrength, 0.05f);
            VrPullOff.Blow(device, ship, hit.point, hit.normal);
            if (logs++ < 60)
                Plugin.Log.LogInfo($"Device punch: {input.Name} fist strikes '{name}' at {fastest:0.0} m/s, blow {blows} of {Blows}, {Punch.Left(blows, Blows) * 100f:0} % of it left");
            if (blows < Blows)
            {
                Taken[device] = blows;
                Sweep();
                return;
            }
            Taken.Remove(device);
            VrPullOff.StripNow(device, input, $"at the blow {blows} of its fist");
        }

        // Devices that are gone (a ship taken away) are forgotten.
        private static void Sweep()
        {
            if (Taken.Count < 16) return;
            Gone.Clear();
            foreach (var each in Taken.Keys)
                if (each == null) Gone.Add(each);
            foreach (var each in Gone) Taken.Remove(each);
            Gone.Clear();
        }

        private static void Release()
        {
            Taken.Clear();
            Gone.Clear();
            Arms[0].Known = Arms[1].Known = false;
            Arms[0].On = Arms[1].On = false;
            Arms[0].BuzzUntil = Arms[1].BuzzUntil = 0f;
            System.Array.Clear(Found, 0, Found.Length);
        }
    }

    // A blow of the fist on a device, the part that is plain numbers.
    internal static class Punch
    {
        // A fist that came onto the device is a blow: fast enough, into the surface, and not the same hand again at once.
        public static bool Lands(float speed, float from, bool into, float since, float again) =>
            into && speed >= from && since >= again;

        // What is left of a device after that many blows of 'of', from 1 (whole) to 0 (stripped).
        public static float Left(int blows, int of) => of <= 0 ? 0f : Mathf.Clamp01(1f - (float)blows / of);
    }
}
