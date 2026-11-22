using System.Collections.Generic;
using BBI.Unity.Game;
using BepInEx.Configuration;
using HarmonyLib;
using UnityEngine;

namespace ShipbreakerVR
{
    // Experiment (Raik, 2026-11-17, branch idea/werkzeug-leine): the cutter and the grapple can be let go. A tool whose
    // grip is let go away from its place on the body stays where it is, afloat, as a body of its own with the shape of
    // its model (VrToolShape); let go at its place (the grapple at the right hip, the cutter over the right shoulder) it
    // goes back onto the body as before. A line of Length ties each tool to the hip (VrToolLine draws it): the tool
    // drifts freely within it, and at its end the line stops tool and player moving apart, so whichever moves on drags
    // the other along, each by its mass.
    // The game knows no tool lying about: to it the tool is put away, its model hanging on its rest point
    // (GunController.m_GrapplingGunRestPoint, CuttingToolController.m_ToolRestPoint), which VrBeltGrapple otherwise keeps
    // at the hip and on the back. While the tool is afloat that rest point follows the floating body instead, so the one
    // model of the tool is where the tool is: nothing hangs on the body meanwhile, and the hand cannot draw it there. It
    // is taken out of the air instead: the tool's belt zone is around the tool itself (Zone), where the grip draws it as
    // it does at the belt.
    // Letting go is told from the game putting the tool away (ToolLetGoPatch) with the grip of the hand that held it open;
    // the game also puts tools away on its own (the scanner, another tool drawn), which leaves them on the body.
    // The line ends on the tool's model, at the lowest place of its shape under the hand that holds it (Fit): where the
    // hand is on the model is taken from the hand itself while it holds the tool, so the tool's fit to the hand
    // (VrToolFit) is in it.
    // A free hand can take hold of a line and pull (Haul), both hands of the same one, hand over hand: the line from the
    // hand nearest to the tool keeps its length then, so a tool afloat comes along with that hand and flies on towards
    // it when the hand stops. With the tool in one hand the other can take its line as well and draw more of it out.
    // Hands and arms neither stop at a tool afloat nor knock it (VrHands, VrHandShape.Stops, VrArmPush): let go, it is
    // still in the hand, and the arm sent the cutter off at up to 4.7 m/s (the log, 2026-11-17).
    // A tool is taken by its grip (Raik, 2026-11-17): within GripBall of where the hand holds it on its model, afloat
    // and, for the grapple, at the belt as well, where the hand used to reach for a fixed place beside the hip instead.
    // A hand that closes on a tool afloat anywhere else holds it by its body as it holds any loose part (VrClimb) and
    // can move it about; to use the tool, a hand has to take its grip. Where the hand holds each tool on its model is
    // kept in the config (GriffGreifhaken, GriffCutter), so it is known before the tool was first held.
    // The demo charges' detonator is let go the same way (Raik, 2026-11-18: "as the cutter and the grapple, with its
    // line, a thing of its own that I can let go and collect again"). To the game it is no tool of its own: the charges
    // in their other mode, shown as a prop on the hand ('PRF_Tool_ExplosiveChargeDetonator'), with no model that hangs
    // on the body. So its floating body carries copies of the prop's meshes to show it afloat; letting go is told from
    // the grip of the hand that held it opening (the game then puts the charges away as always); and it is taken out of
    // the air by its grip, which draws the charges in that mode as the grip at the hip does. Put away it shows at the
    // hip opposite the tools', where its line ends (VrBeltGrapple).
    internal static class VrLooseTools
    {
        static VrLooseTools() => VrSession.OnUnload("loose tools", Release);

        // The line's length, the tool's mass and how it floats, in meters, kilograms and per second at world scale 1.
        private const float Mass = 5f;
        // Raik, 2026-11-18: 1.5 kg, for what it does to other things; a demo charge afloat has the game's own 1.6 kg.
        private const float DetonatorMass = 1.5f;
        private const string DetonatorName = "PRF_Tool_ExplosiveChargeDetonator";
        // A grip that opens this long after the hand last had the detonator lets it go (seconds).
        private const float PropLetGoWithin = 0.15f;
        // Taken out of the air, the detonator glides into the hand this long (seconds), as the cutter and the grapple
        // do (VrToolModels).
        private const float HandOverTime = 0.18f;
        private const float Drag = 0.05f;
        private const float TurnDrag = 0.2f;
        // A tool let go inside something is pushed out of it no faster than this.
        private const float MostPushOut = 0.5f;
        // The hand's own movement at letting go below these puts nothing into the tool (VrClimb.LetGoMotion): a tool
        // simply let go stays where it is.
        private const float Drift = 0.1f;
        private const float TurnDrift = 60f;
        // The taut line pulls back what is stretched past its length at Pull per second, at most MostPull.
        private const float Pull = 4f;
        private const float MostPull = 1f;
        // The hand takes a tool by its grip within GripBall of where it holds it, and a tool let go within StowBall of
        // where its grip hangs on the body goes back there.
        // Raik's third test (2026-11-17): 10 cm was too tight, 20 cm it is, and wider still for laying the tool back.
        private const float GripBall = 0.20f;
        private const float StowBall = 0.25f;
        // The cutter hangs on the back, unseen: it goes back within this of where its grip hangs there.
        private const float CutterStowBall = 0.32f;
        // A grip pressed within this of a tool's grip but outside its ball is logged, to fit the ball.
        private const float MissWithin = 0.5f;
        // A tool this far beyond its line's length (the player was moved, a respawn) goes back onto the body.
        private const float Lost = 4f;
        // The line leaves the tool this far under its grip, as long as the hand's place on the model is not known.
        private const float EndUnderGrip = 0.06f;
        // The line's end on the model: the lowest place of the shape within Around of the hand sideways and Below under
        // it; looked for again once the hand has moved Refit on the model.
        private const float Around = 0.06f;
        private const float Below = 0.22f;
        private const float Refit = 0.01f;
        // The hand's place on the model is not read while the tool still comes into the hand, FitAfter seconds long.
        private const float FitAfter = 0.5f;
        // A free hand takes hold of a line within LineReach of it. Its own speed counts up to MostPullSpeed, and a line
        // stretched Slip past its length (the tool is caught on something) runs through the hand.
        // Raik (2026-11-22): taking the line was fiddly, above all in movement. The reach was 12 cm and only the very
        // frame the grip closed counted; now 18 cm, and a grip that closed up to LineCatch seconds ago still takes the
        // line it comes to (as a hand catches a hold, VrClimb.CatchTime).
        private const float LineReach = 0.18f;
        private const float LineCatch = 0.25f;
        private static readonly float[] GripClosedAt = { -10f, -10f };
        private const float MostPullSpeed = 6f;
        private const float Slip = 0.6f;
        private const string GripName = "RHandTarget";
        private const string SuitName = "geo_suit";

        private static ConfigEntry<bool> enabled, exact, lineInHand, shaded, pulling;
        private static int missLogs;
        private static ConfigEntry<float> lineLength;
        private static bool bound, closing;

        private static void Bind()
        {
            if (bound || VrSettings.File == null) return;
            bound = true;
            enabled = VrSettings.Bind("Experimente", "WerkzeugeLoslassen", true,
                "Experiment: with extended tool physics, the cutter and the grapple stay afloat, on a line to the hip, when their grip is let go away from their place on the body.");
            exact = VrSettings.Bind("Experimente", "WerkzeugformGenau", true,
                "Experiment: with extended tool physics, the tool in the hand meets things with the shape of its model instead of boxes around its parts.");
            lineInHand = VrSettings.Bind("Experimente", "LeineInDerHand", true,
                "Experiment: the line from the hip also shows while the tool is in the hand.");
            lineLength = VrSettings.Bind("Experimente", "LeinenLaenge", 5f,
                "Experiment: how long a tool's line is, in meters.", new AcceptableValueRange<float>(1f, 15f));
            shaded = VrSettings.Bind("Experimente", "LeineSchattiert", true,
                "Experiment: the line is shaded by the sun, round against it and dark in shadow, instead of evenly bright.");
            pulling = VrSettings.Bind("Experimente", "LeineZiehen", true,
                "Experiment: a free hand takes hold of the line of a tool afloat with the grip and pulls the tool in by it.");
            // Not GriffGreifhaken and GriffCutter, as in the third state: those were measured against the controller and
            // hold places up to 19 cm off (see Fit); under new names they are measured anew.
            // The defaults are the places measured with Raik's fit of the tools (VrToolFit's defaults), right hand.
            // The place measured with Raik's fit of the detonator (VrToolFit, ZuenderHaltung), right hand.
            Detonator.GripEntry = VrSettings.Bind("Experimente", "GriffstelleZuender", "-0.043 0.1568 0.0601",
                "Experiment: where the hand holds the charges' detonator on its model (x y z in the model's own axes), measured by the mod while it is held. Empty: not measured yet.");
            Grapple.GripEntry = VrSettings.Bind("Experimente", "GriffstelleGreifhaken", "0.0313 -0.0675 0.3393",
                "Experiment: where the hand holds the grapple on its model (x y z in the model's own axes), measured by the mod while it is held. Empty: not measured yet.");
            Cutter.GripEntry = VrSettings.Bind("Experimente", "GriffstelleCutter", "0.0188 0.0654 -0.1185",
                "Experiment: where the hand holds the cutter on its model (x y z in the model's own axes), measured by the mod while it is held. Empty: not measured yet.");
            foreach (var tool in Tools) Load(tool);
        }

        private static void Load(Tool tool)
        {
            var parts = tool.GripEntry.Value.Split(new[] { ' ' }, System.StringSplitOptions.RemoveEmptyEntries);
            var culture = System.Globalization.CultureInfo.InvariantCulture;
            if (parts.Length != 3 || !float.TryParse(parts[0], System.Globalization.NumberStyles.Float, culture, out var x)
                || !float.TryParse(parts[1], System.Globalization.NumberStyles.Float, culture, out var y)
                || !float.TryParse(parts[2], System.Globalization.NumberStyles.Float, culture, out var z)) return;
            tool.HandOn = new Vector3(x, y, z);
            tool.HandKnown = true;
        }

        // The hand's place on the model into the config, once the tool leaves the hand.
        private static void Save(Tool tool)
        {
            if (!tool.Fresh || tool.GripEntry == null) return;
            var text = string.Format(System.Globalization.CultureInfo.InvariantCulture, "{0:0.####} {1:0.####} {2:0.####}", tool.HandOn.x, tool.HandOn.y, tool.HandOn.z);
            if (tool.GripEntry.Value != text) tool.GripEntry.Value = text;
        }

        // Raik, 2026-11-18: letting tools go afloat belongs to the extended tool physics (VR options); without it a tool
        // let go is back at its place on the body, as before.
        public static bool On { get { Bind(); return enabled != null && enabled.Value && !closing && VrSettings.ToolPhysics != null && VrSettings.ToolPhysics.Value; } }
        public static bool Exact { get { Bind(); return exact != null && exact.Value; } }
        public static bool LineInHand { get { Bind(); return lineInHand == null || lineInHand.Value; } }
        public static float Length { get { Bind(); return lineLength != null ? lineLength.Value : 5f; } }
        public static bool Shaded { get { Bind(); return shaded == null || shaded.Value; } }
        private static bool Pulling { get { Bind(); return pulling == null || pulling.Value; } }

        private struct Piece
        {
            public Transform Part;
            public Transform Holder;
            public VrToolShape.Part Shape;
            public int Index;
            public Collider Collider;
        }

        private sealed class Tool
        {
            public readonly EquipmentController.Equipment Kind;
            public readonly VrToolBelt.Zone Zone;
            public readonly int Index;
            // Where its line leaves the body: from the right hip joint outwards, forwards and up, in meters.
            public readonly Vector3 Hip;
            public readonly float Mass;
            // The charges' detonator: a prop on the hand, no model on the body, at the hip opposite the tools'.
            public readonly bool Prop;
            // For the log: the detonator is the charges' equipment to the game.
            public string Name => Prop ? "detonator" : Kind.ToString();
            // Where the prop was in the hand the frame before, against the rig (the detonator).
            public Vector3 PropAt;
            public Quaternion PropTurn;
            public bool PropKnown;
            // Taken out of the air and on its way into the hand: since when, and from where against the rig.
            public bool Handing;
            public float HandAt;
            public Vector3 FromAt;
            public Quaternion FromTurn;
            // The hand that last held it by its grip, until the game puts it away.
            public VrHand Holder;
            public float HeldAt;
            public bool Loose;
            public float LooseSince;
            public GameObject Body;
            public Rigidbody Rigid;
            // What it was built for: the model with its meshes, and the model's size.
            public long Signature;
            public float BuiltSize;
            public Transform GripOf, GripPart;
            public readonly List<Piece> Pieces = new List<Piece>();
            // In the body's axes: the grip, the line's end and the middle of the shape.
            public Vector3 Grip, End, Middle;
            // The hip against the player's body, for the physics steps between two frames.
            public Vector3 HipFromBody;
            public bool HipKnown;
            public bool Taut;
            public float BuiltAt;
            // Where the hand that holds it is on its model, and the line's end there, both in the model's axes.
            public VrHand HandOf;
            public Transform HandModel;
            public Vector3 HandOn, EndOn, EndFor;
            public bool HandKnown, EndKnown;
            public long EndShape;
            public float FitFrom;
            // Measured on this model with this hand since it took the tool (not only read from the config).
            public bool Fresh;
            public ConfigEntry<string> GripEntry;
            // The hands that hold its line (left, right), each against the player's body with its own speed there, for
            // the physics steps; and of them the one nearest to the tool (-1: none), with the line from it to the tool.
            public readonly bool[] Hauls = new bool[2];
            public readonly Vector3[] PullFromBody = new Vector3[2], PullSpeed = new Vector3[2];
            public readonly bool[] PullKnown = new bool[2];
            public int Outer = -1;
            public float Rest;
            public bool Pulled, Slipped;
            // Its speed before the physics step, for what a hit did to it (Hit).
            public Vector3 SpeedBefore;
            public float HitAt;

            public Tool(EquipmentController.Equipment kind, VrToolBelt.Zone zone, int index, Vector3 hip, float mass = VrLooseTools.Mass, bool prop = false)
            {
                Kind = kind;
                Zone = zone;
                Index = index;
                Hip = hip;
                Mass = mass;
                Prop = prop;
            }
        }

        private static readonly Tool Grapple = new Tool(EquipmentController.Equipment.GrappleHook, VrToolBelt.Zone.Grapple, 0, new Vector3(0.09f, 0.04f, 0.03f));
        private static readonly Tool Cutter = new Tool(EquipmentController.Equipment.CuttingTool, VrToolBelt.Zone.Cutter, 1, new Vector3(0.07f, 0.04f, -0.09f));
        private static readonly Tool Detonator = new Tool(EquipmentController.Equipment.DemoCharge, VrToolBelt.Zone.Detonator, 2, new Vector3(0.09f, 0.04f, 0.03f), DetonatorMass, true);
        private static readonly Tool[] Tools = { Grapple, Cutter, Detonator };
        private static readonly List<MeshFilter> Filters = new List<MeshFilter>();
        private static readonly List<Collider> PlayerColliders = new List<Collider>();
        private static Transform rightHip, leftHip, pelvis, spine;
        private static float nextBones;
        private static int logs;

        private static Tool Of(EquipmentController.Equipment kind) =>
            kind == EquipmentController.Equipment.GrappleHook ? Grapple : kind == EquipmentController.Equipment.CuttingTool ? Cutter : null;

        public static bool IsLoose(EquipmentController.Equipment kind) => Of(kind) is Tool tool && tool.Loose;

        // Whether that collider is a piece of a tool afloat.
        public static bool Owns(Collider collider) => collider != null && Owns(collider.attachedRigidbody);

        // ---- The detonator: a prop on the hand ----

        private static Transform detonatorProp;
        private static float nextProp;

        private static Transform DetonatorProp()
        {
            if (detonatorProp != null || Time.unscaledTime < nextProp) return detonatorProp;
            nextProp = Time.unscaledTime + 2f;
            var grab = GameRefs.Grab;
            if (grab == null) return null;
            foreach (var each in grab.transform.root.GetComponentsInChildren<Transform>(true))
                if (each.name.StartsWith(DetonatorName, System.StringComparison.Ordinal))
                {
                    detonatorProp = each;
                    break;
                }
            return detonatorProp;
        }

        // For VrBeltGrapple, which shows the detonator at the hip while it is neither in a hand nor afloat.
        internal static Transform DetonatorModel => DetonatorProp();
        public static bool DetonatorLoose => Detonator.Loose;
        // For VrDetonatorLights: the detonator's own body afloat, while it has one.
        internal static Transform DetonatorAfloat => Detonator.Body != null ? Detonator.Body.transform : null;

        // Whether the prop shows on a hand now (the game shows it some frames after the charges are drawn).
        private static MeshRenderer[] propParts;
        private static Transform propPartsOf;

        public static bool DetonatorShowsInHand
        {
            get
            {
                var prop = DetonatorProp();
                if (prop == null) return false;
                if (propPartsOf != prop)
                {
                    propPartsOf = prop;
                    propParts = prop.GetComponentsInChildren<MeshRenderer>(true);
                }
                foreach (var part in propParts)
                    if (part != null && part.enabled && part.gameObject.activeInHierarchy) return true;
                return false;
            }
        }

        // ---- The detonator taken out of the air ----

        // Raik, 2026-11-18: taken out of the air the detonator was gone for two or three frames and then in the hand:
        // its floating body ended as the charges were drawn, and the game shows the prop on the hand a little later.
        // Now the floating body glides into the hand first, over HandOverTime, the prop hidden meanwhile, and only then
        // gives way to the prop.
        private static readonly List<Renderer> PropHidden = new List<Renderer>();

        private static void BeginHandOver(Tool tool, Transform rig)
        {
            if (rig == null || tool.Rigid == null)
            {
                End(tool, "taken");
                return;
            }
            var body = tool.Body.transform;
            tool.Handing = true;
            tool.HandAt = Time.unscaledTime;
            tool.FromAt = rig.InverseTransformPoint(body.position);
            tool.FromTurn = Quaternion.Inverse(rig.rotation) * body.rotation;
            tool.Rigid.detectCollisions = false;
            tool.Taut = tool.Pulled = false;
            if (logs++ < 80) Plugin.Log.LogInfo($"Loose tools: the detonator taken out of the air, gliding into the hand over {HandOverTime:0.00} s");
        }

        private static void HandOver(Tool tool, Transform model, bool taken, Transform rig, Rigidbody player)
        {
            var carried = player != null ? player.velocity : Vector3.zero;
            if (!taken || rig == null)
            {
                // The hand opened on the way: it floats on from where it is.
                ShowProp();
                tool.Handing = false;
                tool.Rigid.detectCollisions = true;
                tool.Rigid.velocity = carried;
                tool.Rigid.angularVelocity = Vector3.zero;
                return;
            }
            var t = Mathf.Clamp01((Time.unscaledTime - tool.HandAt) / HandOverTime);
            if (t >= 1f)
            {
                End(tool, "taken");
                return;
            }
            foreach (var part in model.GetComponentsInChildren<Renderer>(true))
            {
                if (!part.enabled) continue;
                part.enabled = false;
                if (!PropHidden.Contains(part)) PropHidden.Add(part);
            }
            var eased = t * t * (3f - 2f * t);
            var at = Vector3.Lerp(rig.TransformPoint(tool.FromAt), model.position, eased);
            var turn = Quaternion.Slerp(rig.rotation * tool.FromTurn, model.rotation, eased);
            tool.Body.transform.SetPositionAndRotation(at, turn);
            tool.Rigid.position = at;
            tool.Rigid.rotation = turn;
            tool.Rigid.velocity = carried;
            tool.Rigid.angularVelocity = Vector3.zero;
        }

        // What the glide hid of the prop shows again; the game and VrToolModels hide it themselves when it is put away.
        private static void ShowProp()
        {
            foreach (var part in PropHidden)
                if (part != null) part.enabled = true;
            PropHidden.Clear();
        }

        // Whether that hand has the tool out.
        private static bool Has(Tool tool, VrHand hand) =>
            hand.HoldsTool && hand.Tool == tool.Kind && (!tool.Prop || VrChargeByHand.DetonatorOut);

        // The tool's model, and where it hangs when put away (none for the detonator, whose model is the prop on the
        // hand); taken: a hand has it.
        private static bool Model(Tool tool, out Transform model, out Transform rest, out bool taken)
        {
            taken = false;
            if (tool.Prop)
            {
                rest = null;
                model = DetonatorProp();
                if (model == null) return false;
                foreach (var hand in VrHand.Both)
                    if (Has(tool, hand) && hand.Input.Grip.Down) taken = true;
                return true;
            }
            if (!VrBeltGrapple.Parts(tool.Kind, out model, out rest, out _, out _)) return false;
            taken = model.parent != rest;
            return true;
        }

        // The detonator in a hand, each frame: where it is against the rig, and its body (built ahead, switched off)
        // measured there. By the time its grip is seen open the game may have put the charges away already, the prop
        // hidden and back on the right hand: it is let go where it was the frame before.
        private static void Remember(Tool tool)
        {
            var model = DetonatorProp();
            var rig = VrCamera.Instance != null ? VrCamera.Instance.transform.parent : null;
            if (model == null || rig == null) return;
            tool.PropAt = rig.InverseTransformPoint(model.position);
            tool.PropTurn = Quaternion.Inverse(rig.rotation) * model.rotation;
            tool.PropKnown = true;
            if (tool.Body == null || tool.Loose) return;
            tool.Body.transform.SetPositionAndRotation(model.position, model.rotation);
            Measure(tool, model);
        }

        // The grip of the hand that held the detonator opened: away from its place at the hip it stays afloat.
        private static void PropLetGo(Tool tool)
        {
            var holder = tool.Holder;
            if (holder == null || holder.Input.Grip.Down) return;
            tool.Holder = null;
            Save(tool);
            if (tool.Loose || !Usable() || Time.unscaledTime - tool.HeldAt > PropLetGoWithin) return;
            var model = DetonatorProp();
            if (model == null) return;
            // Put back where its grip hangs at the hip, as the grapple (within StowBall); by the box at the hip while
            // that place is not known yet.
            var rig = VrCamera.Instance != null ? VrCamera.Instance.transform.parent : null;
            bool stows;
            if (tool.HandKnown && rig != null && holder.Input.TryGetPosition(out var local) && VrBeltGrapple.DetonatorAtHip(tool.HandOn, out var hung, out _))
                stows = (rig.TransformPoint(local) - hung).magnitude < StowBall * VrCamera.WorldScale;
            else stows = VrToolBelt.Stows(tool.Zone, holder.Input);
            if (stows)
            {
                if (logs++ < 80) Plugin.Log.LogInfo("Loose tools: the detonator let go at its place on the body, put away");
                return;
            }
            LetGo(tool, model, holder);
        }

        // What the hands do not stop at: a tool afloat, and a part just let go that is still in the hand (a demo charge
        // as it leaves the hand, VrChargeByHand; any part a hand let go, VrArmPush.Spare). Raik, 2026-11-18: letting a
        // charge go, the hand jumped away from it.
        public static bool HandsPass(Collider collider) => Owns(collider) || VrChargeByHand.LeftAlone(collider);

        public static bool Owns(Rigidbody body)
        {
            if (body == null) return false;
            foreach (var tool in Tools)
                if (tool.Loose && body == tool.Rigid) return true;
            return false;
        }

        // Whether that rest point follows a tool afloat (VrBeltGrapple leaves it alone then).
        public static bool Holds(Transform point)
        {
            foreach (var tool in Tools)
                if (tool.Loose && !tool.Prop && VrBeltGrapple.Parts(tool.Kind, out _, out var rest, out _, out var shadowRest) && (point == rest || point == shadowRest)) return true;
            return false;
        }

        // VR in a shift, with the tools worked by hand.
        private static bool Usable()
        {
            var vr = VrCamera.Instance;
            if (!VrXr.IsRunning || vr == null || vr.FollowsHab || !VrSettings.AimWithHand.Value || !PatchReport.HandAimWorks) return false;
            var state = GameSession.CurrentGameState;
            return state == GameSession.GameState.Gameplay || state == GameSession.GameState.Paused;
        }

        // ---- Each frame, after the hands are settled (VrFrame) ----

        // The hand that holds each tool by its grip, kept until the game puts the tool away (PutAway).
        public static void Track()
        {
            if (!On) return;
            foreach (var tool in Tools)
            {
                var held = false;
                foreach (var hand in VrHand.Both)
                {
                    if (!Has(tool, hand)) continue;
                    held = true;
                    if (!hand.Input.Grip.Down) continue;
                    tool.Holder = hand;
                    tool.HeldAt = Time.unscaledTime;
                    if (tool.Prop) Remember(tool);
                }
                if (tool.Prop) PropLetGo(tool);
                if (!held && tool.Holder != null && Time.unscaledTime - tool.HeldAt > 1f) tool.Holder = null;
                Haul(tool);
            }
        }

        // ---- Pulling a tool in by its line ----

        // A free hand that closes its grip at a tool's line holds the line until the grip opens: the line of a tool
        // afloat, or of a tool in the other hand.
        private static void Haul(Tool tool)
        {
            var rig = VrCamera.Instance != null ? VrCamera.Instance.transform.parent : null;
            for (var h = 0; h < 2; h++)
            {
                var hand = VrHand.Both[h];
                if (tool.Hauls[h])
                {
                    if (!VrToolLine.Holds(tool.Index, h)) Drop(tool, h, "the line is gone");
                    else if (tool.Slipped && tool.Outer == h) Drop(tool, h, "the line ran through the hand, the tool does not come");
                    else if (!hand.Input.Grip.Down) Drop(tool, h, "let go");
                    else if (hand.State != HandState.Empty) Drop(tool, h, "the hand took something else");
                    continue;
                }
                if (hand.Input.Grip.Pressed) GripClosedAt[h] = Time.unscaledTime;
                var closing = hand.Input.Grip.Down && Time.unscaledTime - GripClosedAt[h] <= LineCatch;
                if (!Pulling || rig == null || !closing || !hand.Free || HaulsAny(h)) continue;
                if (!hand.Input.TryGetPosition(out var local)) continue;
                if (!VrToolLine.Take(tool.Index, h, FistMiddle(hand, rig.TransformPoint(local)), FistAxis(hand, rig), LineReach * VrCamera.WorldScale)) continue;
                GripClosedAt[h] = -10f;
                tool.Hauls[h] = true;
                tool.PullKnown[h] = false;
                tool.PullSpeed[h] = Vector3.zero;
                hand.Input.Buzz(0.3f, 0.04f);
                VrToolLine.Outer(tool.Index, out _, out var rest);
                if (logs++ < 200)
                    Plugin.Log.LogInfo($"Loose tools: the {hand.Name} hand takes the {tool.Name}'s line{(tool.Hauls[1 - h] ? " as well" : "")}"
                        + $"{(tool.Loose ? "" : " (the tool is in the other hand)")}, {rest / VrCamera.WorldScale:0.00} m of it from the last hand to the tool");
            }
        }

        // The way through that hand's fist, in the world: across the hand, from the little finger's side to the
        // thumb's, as the hand is drawn (square to its palm's normal and to the way from the wrist to the knuckles);
        // without the drawn hand, the controller's grip axis (OpenXR: the axis of the tube the closed fingers form).
        private static Vector3 FistAxis(VrHand hand, Transform rig)
        {
            if (VrClimb.TryGetPalm(hand.Input, out var palm) && VrHands.TryGetArm(hand.IsLeft, out _, out _, out var wrist, out var knuckles))
            {
                var across = Vector3.Cross(palm, knuckles - wrist);
                if (across.sqrMagnitude > 1e-8f) return across.normalized;
            }
            return hand.Input.TryGetGripPose(out _, out var grip) ? rig.rotation * grip * Vector3.forward : rig.up;
        }

        // Where the line lies in that hand's fist, from the controller's place: in the hollow between the palm and the
        // closed fingers. At the controller's own place, which lies within the hand, the line ran through the palm
        // (Raik's screenshot, 2026-11-23); at VrClimb.PalmPoint (5.5 cm out along the palm's normal, where an open
        // hand takes hold) it ran through the closed fingers (his next ones: about 2 cm too far out, seen along the
        // fist). So FistOut out along the palm's normal and FistToFingers towards the finger tips, in meters at world
        // scale 1; Experimente/LeineFaustTiefe and LeineFaustZuFingern (in cm) to fine-tune it in the headset.
        private const float FistOut = 0.035f;
        private const float FistToFingers = 0.015f;
        private static ConfigEntry<float> fistOut, fistToFingers;

        private static Vector3 FistMiddle(VrHand hand, Vector3 controller)
        {
            if (fistOut == null && VrSettings.File != null)
            {
                fistOut = VrSettings.Bind("Experimente", "LeineFaustTiefe", FistOut * 100f,
                    "Where a tool's line lies in the fist that holds it: this many cm out from the controller's place along the palm's normal (0: in the palm, 5.5: in the closed fingers).");
                fistToFingers = VrSettings.Bind("Experimente", "LeineFaustZuFingern", FistToFingers * 100f,
                    "Where a tool's line lies in the fist that holds it: this many cm from the controller's place towards the finger tips (negative: towards the wrist).");
            }
            if (!VrClimb.TryGetPalm(hand.Input, out var palm)) return controller;
            var scale = VrCamera.WorldScale;
            var point = controller + palm * ((fistOut != null ? fistOut.Value * 0.01f : FistOut) * scale);
            if (VrHands.TryGetArm(hand.IsLeft, out _, out _, out var wrist, out var knuckles))
            {
                var fingers = Vector3.ProjectOnPlane(knuckles - wrist, palm);
                if (fingers.sqrMagnitude > 1e-6f) point += fingers.normalized * ((fistToFingers != null ? fistToFingers.Value * 0.01f : FistToFingers) * scale);
            }
            return point;
        }

        private static bool HaulsAny(int h)
        {
            foreach (var tool in Tools)
                if (tool.Hauls[h]) return true;
            return false;
        }

        private static void Drop(Tool tool, int h, string why)
        {
            if (!tool.Hauls[h]) return;
            var hand = VrHand.Both[h];
            if (why != null && logs++ < 200) Plugin.Log.LogInfo($"Loose tools: the {hand.Name} hand no longer holds the {tool.Name}'s line ({why})");
            if (tool.Slipped && tool.Outer == h)
            {
                hand.Input.Buzz(0.5f, 0.08f);
                tool.Slipped = false;
            }
            tool.Hauls[h] = false;
            tool.PullKnown[h] = false;
            if (tool.Outer == h) tool.Pulled = false;
            tool.Outer = -1;
            VrToolLine.Drop(tool.Index, h);
        }

        // Each frame while hands hold the line: where each is against the body and how fast it moves there, and which
        // of them is nearest to the tool.
        private static void Carry(Tool tool, Rigidbody player, Transform rig)
        {
            if (!tool.Hauls[0] && !tool.Hauls[1])
            {
                tool.Outer = -1;
                return;
            }
            if (player == null || rig == null) return;
            var dt = Time.deltaTime;
            for (var h = 0; h < 2; h++)
            {
                if (!tool.Hauls[h] || !VrHand.Both[h].Input.TryGetPosition(out var local)) continue;
                var at = FistMiddle(VrHand.Both[h], rig.TransformPoint(local));
                VrToolLine.Carry(tool.Index, h, at, FistAxis(VrHand.Both[h], rig));
                // The hand pulls at the tool from where the line leaves its fist.
                if (VrToolLine.ToolSide(tool.Index, h, out var leaves)) at = leaves;
                var from = at - player.transform.position;
                if (tool.PullKnown[h] && dt > 1e-5f)
                    tool.PullSpeed[h] = Vector3.ClampMagnitude(Vector3.Lerp(tool.PullSpeed[h], (from - tool.PullFromBody[h]) / dt, 0.5f), MostPullSpeed * VrCamera.WorldScale);
                tool.PullFromBody[h] = from;
                tool.PullKnown[h] = true;
            }
            if (!VrToolLine.Outer(tool.Index, out tool.Outer, out tool.Rest)) tool.Outer = -1;
            // The line felt in the hand while it pulls at the tool.
            if (tool.Loose && tool.Pulled && tool.Outer >= 0) VrHand.Both[tool.Outer].Input.Buzz(0.12f, 0.02f);
        }

        // ---- Letting go ----

        // From ToolLetGoPatch: the game has just put that tool away, its model on its rest point where the hand held it.
        public static void PutAway(EquipmentController.Equipment kind)
        {
            var tool = Of(kind);
            if (tool == null) return;
            var holder = tool.Holder;
            tool.Holder = null;
            Save(tool);
            if (!On || tool.Loose || holder == null || !Usable()) return;
            // Put away by the game with the grip still held (the scanner hides it, another tool takes its place).
            if (holder.Input.Grip.Down) return;
            if (!VrBeltGrapple.Parts(kind, out var model, out var rest, out _, out _) || model.parent != rest) return;
            if (VrToolBelt.Stows(tool.Zone, holder.Input) || AtItsGripOnTheBody(tool, holder.Input, model, rest))
            {
                if (logs++ < 80) Plugin.Log.LogInfo($"Loose tools: {kind} let go at its place on the body, put away");
                return;
            }
            // Where the hand was, to fit the places the tools go back to.
            if (awayLogs++ < 60 && VrToolBelt.ShoulderLocal(holder.Input, out var where))
                Plugin.Log.LogInfo($"Loose tools: {kind} let go away from its place: the hand at ({where.x:0.00}, {where.y:0.00}, {where.z:0.00}) m as the shoulder is measured"
                    + (TryGetHung(tool, holder.Input, model, rest, out var off) ? $", {off / VrCamera.WorldScale * 100f:0} cm from where its grip hangs" : ""));
            LetGo(tool, model, holder);
        }

        private static void LetGo(Tool tool, Transform model, VrHand holder)
        {
            var player = VrToolCollide.Player();
            var rig = VrCamera.Instance != null ? VrCamera.Instance.transform.parent : null;
            // The detonator's prop may be hidden already (the charges are being put away): the body built while it was held.
            if (player == null || rig == null || (!Build(tool, model, true) && !(tool.Prop && tool.Body != null && tool.Pieces.Count > 0))) return;
            var scale = VrCamera.WorldScale;
            var body = tool.Body.transform;
            var at = model.position;
            var turn = model.rotation;
            if (tool.Prop && tool.PropKnown)
            {
                // As it was in the hand the frame before (Remember), its body measured there.
                at = rig.TransformPoint(tool.PropAt);
                turn = rig.rotation * tool.PropTurn;
                body.SetPositionAndRotation(at, turn);
            }
            else
            {
                body.SetPositionAndRotation(at, turn);
                // The parts as they sit now (the grapple's claws open and close).
                foreach (var piece in tool.Pieces)
                    if (piece.Part != null && piece.Holder != null)
                        piece.Holder.SetPositionAndRotation(piece.Part.position + piece.Part.rotation * piece.Shape.At[piece.Index], piece.Part.rotation * piece.Shape.Turn[piece.Index]);
                Measure(tool, model);
            }
            tool.Body.SetActive(true);
            // The player's own body does not bump into its tools (nor do they knock it about on the line).
            PlayerColliders.Clear();
            player.GetComponentsInChildren(true, PlayerColliders);
            PlayerColliders.RemoveAll(own => own == null || !own.enabled || !own.gameObject.activeInHierarchy);
            foreach (var piece in tool.Pieces)
                foreach (var own in PlayerColliders)
                    if (piece.Collider != null) Physics.IgnoreCollision(piece.Collider, own, true);

            var rigid = tool.Rigid;
            rigid.position = at;
            rigid.rotation = turn;
            var velocity = player.velocity;
            var spin = Vector3.zero;
            var moved = Vector3.zero;
            if (VrClimb.LetGoMotion(holder.Input, Drift * scale, TurnDrift, out var handSpeed, out var handSpin) && holder.Input.TryGetPosition(out var handLocal))
            {
                // The tool's middle goes as a point of the hand there would, no faster than an arm can throw it.
                var lever = body.TransformPoint(tool.Middle) - rig.TransformPoint(handLocal);
                moved = Vector3.ClampMagnitude(handSpeed + Vector3.Cross(handSpin, lever), ClimbContact.MostThrowSpeed(tool.Mass) * scale);
                spin = handSpin;
            }
            rigid.velocity = velocity + moved;
            rigid.angularVelocity = spin;
            tool.Loose = true;
            tool.LooseSince = Time.unscaledTime;
            tool.Taut = false;
            Hang(tool);
            holder.Input.Buzz(0.2f, 0.03f);
            Plugin.Log.LogInfo($"Loose tools: {tool.Name} let go by the {holder.Name} hand, afloat at {moved.magnitude:0.00} m/s against the body"
                + $" (turning {spin.magnitude * Mathf.Rad2Deg:0}°/s), {tool.Pieces.Count} piece(s), layer {LayerMask.LayerToName(tool.Body.layer)}"
                + $", line's end {(tool.EndKnown && tool.HandModel == model ? "on the model under the hand" : "under the grip target")}, {(tool.End - tool.Grip).magnitude / scale * 100f:0} cm from the grip");
        }

        // ---- The floating body ----

        // The body with the tool's shape, built once for its model as it is (its meshes; and its size, when sized: in the
        // hand the model may be of another size than on its rest point, where it floats) and kept switched off while the
        // tool is not afloat.
        private static bool Build(Tool tool, Transform model, bool sized)
        {
            VrToolShape.PartsOf(model, Filters);
            if (Filters.Count == 0) return false;
            long signature = model.GetInstanceID();
            foreach (var filter in Filters) signature = signature * 31 + filter.sharedMesh.GetInstanceID();
            var size = model.lossyScale.x;
            if (tool.Body != null && tool.Signature == signature && (!sized || Mathf.Abs(tool.BuiltSize - size) < 0.001f)) return true;
            if (tool.Body != null) Object.Destroy(tool.Body);
            var watch = System.Diagnostics.Stopwatch.StartNew();
            var layer = ShipLayer();
            var body = new GameObject($"ShipbreakerVR loose {tool.Name}") { layer = layer };
            body.SetActive(false);
            body.transform.SetPositionAndRotation(model.position, model.rotation);
            var rigid = body.AddComponent<Rigidbody>();
            rigid.mass = tool.Mass;
            rigid.useGravity = false;
            rigid.drag = Drag;
            rigid.angularDrag = TurnDrag;
            rigid.interpolation = RigidbodyInterpolation.Interpolate;
            rigid.collisionDetectionMode = CollisionDetectionMode.ContinuousDynamic;
            rigid.maxDepenetrationVelocity = MostPushOut * VrCamera.WorldScale;
            tool.Pieces.Clear();
            foreach (var filter in Filters)
            {
                var part = filter.transform;
                var shape = VrToolShape.For(filter.sharedMesh, part.lossyScale);
                if (shape == null) continue;
                for (var i = 0; i < shape.Pieces.Length; i++)
                {
                    var collider = VrToolShape.Add(body.transform, shape, i, part.position, part.rotation, layer, false);
                    tool.Pieces.Add(new Piece { Part = part, Holder = collider.transform, Shape = shape, Index = i, Collider = collider });
                }
            }
            // The detonator has no model of its own to float: copies of the prop's meshes on the body show it.
            if (tool.Prop)
                foreach (var filter in Filters)
                {
                    var source = filter.GetComponent<MeshRenderer>();
                    if (source == null) continue;
                    var copy = new GameObject("visual") { layer = filter.gameObject.layer };
                    copy.transform.SetParent(body.transform, false);
                    copy.transform.SetPositionAndRotation(filter.transform.position, filter.transform.rotation);
                    copy.transform.localScale = filter.transform.lossyScale;
                    copy.AddComponent<MeshFilter>().sharedMesh = filter.sharedMesh;
                    var shown = copy.AddComponent<MeshRenderer>();
                    shown.sharedMaterials = source.sharedMaterials;
                    shown.shadowCastingMode = source.shadowCastingMode;
                    shown.receiveShadows = source.receiveShadows;
                }
            body.AddComponent<Hits>().Of = tool;
            tool.Body = body;
            tool.Rigid = rigid;
            tool.Signature = signature;
            tool.BuiltSize = size;
            tool.BuiltAt = Time.unscaledTime;
            Measure(tool, model);
            Plugin.Log.LogInfo($"Loose tools: body for the {tool.Name} from {Filters.Count} part(s) of '{model.name}' in {tool.Pieces.Count} piece(s), "
                + $"size {model.lossyScale.x:0.00}, layer {LayerMask.LayerToName(layer)} ({layer}), built in {watch.ElapsedMilliseconds} ms");
            return tool.Pieces.Count > 0;
        }

        // The grip, the line's end and the middle of the shape, in the body's axes, with the body at the model's place.
        private static void Measure(Tool tool, Transform model)
        {
            var body = tool.Body.transform;
            tool.Grip = body.InverseTransformPoint(tool.HandKnown ? model.TransformPoint(tool.HandOn) : GripAt(tool, model));
            tool.End = body.InverseTransformPoint(EndAt(tool, model));
            var middle = Vector3.zero;
            var count = 0;
            foreach (var piece in tool.Pieces)
            {
                if (piece.Part == null) continue;
                middle += piece.Part.TransformDirection(piece.Shape.Pieces[piece.Index].Middle) + piece.Part.position;
                count++;
            }
            tool.Middle = count > 0 ? body.InverseTransformPoint(middle / count) : Vector3.zero;
        }

        // What a tool afloat runs into, for the log (Raik, 2026-11-17: the two tools thrown at each other gave each other
        // rather much): the other tool always, anything else from HardHit on.
        private sealed class Hits : MonoBehaviour
        {
            public Tool Of;

            private void OnCollisionEnter(Collision collision) => Hit(Of, collision);
        }

        private const float HardHit = 1.5f;
        // The two tools meet piece by piece, each pair of pieces a hit of its own (some 30 lines for one touch in Raik's
        // fourth test): one line for the two, from the grapple, and the next for a tool only after HitPause seconds.
        private const float HitPause = 0.5f;
        private static int hitLogs;

        private static void Hit(Tool tool, Collision collision)
        {
            if (tool == null || tool.Rigid == null || hitLogs >= 150) return;
            var other = collision.rigidbody;
            var otherTool = other == null ? null : Grapple.Loose && other == Grapple.Rigid ? Grapple : Cutter.Loose && other == Cutter.Rigid ? Cutter : null;
            if (otherTool != null && tool != Grapple) return;
            var scale = VrCamera.WorldScale;
            var meets = collision.relativeVelocity.magnitude / scale;
            if (otherTool == null && meets < HardHit) return;
            if (Time.unscaledTime - tool.HitAt < HitPause) return;
            tool.HitAt = Time.unscaledTime;
            hitLogs++;
            var player = VrToolCollide.Player();
            var carried = player != null ? player.velocity : Vector3.zero;
            string Went(Tool each) => $"the {each.Name} went {each.SpeedBefore.magnitude / scale:0.00} m/s before and goes {(each.Rigid.velocity - carried).magnitude / scale:0.00} m/s now, turning {each.Rigid.angularVelocity.magnitude * Mathf.Rad2Deg:0}\u00b0/s";
            Plugin.Log.LogInfo($"Loose tools: {tool.Name} hits {(otherTool != null ? $"the {otherTool.Name}" : $"'{collision.collider.name}'{(other != null ? $" ({other.mass:0.#} kg)" : " (fixed)")}")}"
                + $", meeting at {meets:0.00} m/s, impulse {collision.impulse.magnitude / scale:0.0} kg m/s; against the body {Went(tool)}{(otherTool != null ? $", {Went(otherTool)}" : "")}"
                + $"{(VrHand.Left.State == HandState.Holding || VrHand.Right.State == HandState.Holding ? "; a hand holds on to something" : "")}");
        }

        // The layer of the ship's own parts, which the tools are to bump into as loose parts do: the one most of the
        // first few hundred parts are on, looked up once. Not Ignore Raycast, where the small floating debris is, which
        // the hands and tools do not look for.
        private static int shipLayer = -1;

        private static int ShipLayer()
        {
            if (shipLayer >= 0) return shipLayer;
            var counts = new int[32];
            var seen = 0;
            foreach (var part in Object.FindObjectsOfType<StructurePart>())
            {
                var collider = part != null ? part.GetComponentInChildren<Collider>() : null;
                if (collider == null || collider.isTrigger || collider.gameObject.layer == 2) continue;
                counts[collider.gameObject.layer]++;
                if (++seen >= 300) break;
            }
            if (seen == 0) return 0;
            var best = 0;
            for (var layer = 1; layer < 32; layer++)
                if (counts[layer] > counts[best]) best = layer;
            shipLayer = best;
            return best;
        }

        // The tool's rest points onto the floating body, the model straight on them.
        private static void Hang(Tool tool)
        {
            if (tool.Body == null || tool.Prop || !VrBeltGrapple.Parts(tool.Kind, out var model, out var rest, out var shadow, out var shadowRest)) return;
            var body = tool.Body.transform;
            VrBeltGrapple.Remember(rest);
            rest.SetPositionAndRotation(body.position, body.rotation);
            Settle(model, rest);
            if (shadowRest == null) return;
            VrBeltGrapple.Remember(shadowRest);
            shadowRest.SetPositionAndRotation(body.position, body.rotation);
            Settle(shadow, shadowRest);
        }

        private static void Settle(Transform model, Transform rest)
        {
            if (model == null || model.parent != rest) return;
            model.localPosition = Vector3.zero;
            model.localRotation = Quaternion.identity;
        }

        private static void End(Tool tool, string why)
        {
            if (!tool.Loose) return;
            tool.Loose = false;
            tool.Taut = false;
            tool.Pulled = tool.Slipped = false;
            if (tool.Prop)
            {
                ShowProp();
                tool.Handing = false;
                if (tool.Rigid != null) tool.Rigid.detectCollisions = true;
            }
            if (tool.Body != null) tool.Body.SetActive(false);
            if (logs++ < 80) Plugin.Log.LogInfo($"Loose tools: {tool.Name} no longer afloat ({why}) after {Time.unscaledTime - tool.LooseSince:0.0} s");
        }

        // ---- Each frame, once the rig is placed and the tools are in the hands, before VrBeltGrapple (VrFrame) ----

        public static void Place()
        {
            if (!On)
            {
                foreach (var tool in Tools) End(tool, "switched off");
                VrToolLine.Hide();
                return;
            }
            var usable = Usable();
            var player = VrToolCollide.Player();
            var scale = VrCamera.WorldScale;
            var carried = player != null ? player.velocity : Vector3.zero;
            var rig = VrCamera.Instance != null ? VrCamera.Instance.transform.parent : null;
            foreach (var tool in Tools)
            {
                var known = Model(tool, out var model, out var rest, out var taken);
                if (tool.Loose)
                {
                    if (!usable) End(tool, "no shift in VR");
                    else if (tool.Body == null || !known) End(tool, "gone");
                    else if (tool.Handing) HandOver(tool, model, taken, rig, player);
                    else if (taken && tool.Prop) BeginHandOver(tool, rig);
                    else if (taken) End(tool, "taken");
                    else Hang(tool);
                }
                var hip = Vector3.zero;
                tool.HipKnown = usable && known && player != null && TryGetHip(tool, out hip);
                if (!tool.HipKnown)
                {
                    VrToolLine.Update(tool.Index, false, default, default, false, default);
                    continue;
                }
                tool.HipFromBody = hip - player.transform.position;
                // Shown while the tool is out: afloat, or in a hand (its model off its rest point).
                var inHand = !tool.Loose && taken && (tool.Prop || model.gameObject.activeInHierarchy);
                if (inHand && Time.unscaledTime - tool.BuiltAt > 1f)
                {
                    // Its body built ahead while it is held, so letting go costs nothing; looked at again every second,
                    // as the model may not be ready yet or change (the cutter's mirrored side, VrToolMirror).
                    tool.BuiltAt = Time.unscaledTime;
                    Build(tool, model, false);
                }
                if (inHand) Fit(tool, model, rig);
                else tool.FitFrom = Time.unscaledTime;
                Carry(tool, player, rig);
                if (tool.Loose && (tool.Body.transform.TransformPoint(tool.End) - hip).magnitude > (Length + Lost) * scale)
                    End(tool, "too far off, back onto the body");
                if (!tool.Loose && !(inHand && LineInHand))
                {
                    VrToolLine.Update(tool.Index, false, default, default, false, default);
                    continue;
                }
                var end = tool.Loose ? tool.Body.transform.TransformPoint(tool.End) : EndAt(tool, model);
                VrToolLine.Update(tool.Index, true, hip, end, !tool.Loose, carried);
            }
            VrToolLine.Build();
        }

        // Where the line ends on the tool, in the world: on its model under the hand, once that is known (Fit).
        private static Vector3 EndAt(Tool tool, Transform model) =>
            tool.EndKnown && tool.HandModel == model ? model.TransformPoint(tool.EndOn) : GripAt(tool, model) - model.up * (EndUnderGrip * VrCamera.WorldScale);

        // With the tool in a hand: where that hand is on the model (smoothed, as the tool lags behind a fast hand), and
        // from the tool's shape the line's end, the lowest place of the model under the hand: the foot of its grip.
        private static void Fit(Tool tool, Transform model, Transform rig)
        {
            VrHand hand = null;
            foreach (var each in VrHand.Both)
                if (Has(tool, each) && each.Input.Grip.Down) hand = each;
            // The hand where the tool is held (VrAim, as VrToolModels places the model), not the controller: with its
            // weight the tool lags behind a quick hand (VrToolWeight) and is held back at a wall (VrToolCollide). Measured
            // against the controller the place wandered by up to 19 cm during a throw (Raik's third test, 2026-11-17), and
            // the grip's ball with it: the grapple afloat could not be taken by its grip.
            if (hand == null || rig == null || !VrAim.TryGetHand(hand.Input, out var held, out _)) return;
            if (tool.HandOf != hand || tool.HandModel != model)
            {
                // Another hand or another model (the cutter's side for the left hand): measured anew. Until then the
                // place last measured counts, or the one from the config.
                tool.HandOf = hand;
                tool.HandModel = model;
                tool.Fresh = tool.EndKnown = false;
            }
            if (Time.unscaledTime - tool.FitFrom >= FitAfter)
            {
                var on = model.InverseTransformPoint(held);
                tool.HandOn = tool.Fresh ? Vector3.Lerp(tool.HandOn, on, 0.1f) : on;
                tool.HandKnown = tool.Fresh = true;
            }
            if (!tool.HandKnown) return;
            if (tool.Body == null || tool.Pieces.Count == 0) return;
            var size = Mathf.Max(Mathf.Abs(model.lossyScale.x), 1e-4f);
            var scale = VrCamera.WorldScale / size;
            if (tool.EndKnown && tool.EndShape == tool.Signature && (tool.HandOn - tool.EndFor).magnitude < Refit * scale) return;
            var around = Around * scale;
            var below = Below * scale;
            var lowest = 0f;
            var nearest = float.MaxValue;
            Vector3 under = Vector3.zero, next = Vector3.zero;
            foreach (var piece in tool.Pieces)
            {
                if (piece.Part == null) continue;
                var shape = piece.Shape.Pieces[piece.Index];
                var count = shape.Flat ? 4 : shape.Points != null ? shape.Points.Length : 0;
                for (var i = 0; i < count; i++)
                {
                    var own = shape.Flat ? shape.Center + shape.U * (i % 2 == 0 ? shape.Half.x : -shape.Half.x) + shape.V * (i < 2 ? shape.Half.y : -shape.Half.y) : shape.Points[i];
                    var point = model.InverseTransformPoint(piece.Part.position + piece.Part.rotation * own);
                    var off = point - tool.HandOn;
                    var distance = off.sqrMagnitude;
                    if (distance < nearest)
                    {
                        nearest = distance;
                        next = point;
                    }
                    if (-off.y <= lowest || -off.y > below || off.x * off.x + off.z * off.z > around * around) continue;
                    lowest = -off.y;
                    under = point;
                }
            }
            if (nearest == float.MaxValue) return;
            // Nothing of the model under the hand (the hand is beside it): the place of the model next to the hand.
            tool.EndOn = lowest > 0f ? under : next;
            tool.EndFor = tool.HandOn;
            tool.EndShape = tool.Signature;
            if (!tool.EndKnown && logs++ < 160)
                Plugin.Log.LogInfo($"Loose tools: the {tool.Name}'s line ends on its model {(lowest > 0f ? $"{lowest * size / VrCamera.WorldScale * 100f:0} cm under" : $"{Mathf.Sqrt(nearest) * size / VrCamera.WorldScale * 100f:0} cm beside")} the {hand.Name} hand"
                    + $" (hand at {tool.HandOn * (size / VrCamera.WorldScale * 100f):F0} cm on the model, {tool.HandOn.x:0.0000} {tool.HandOn.y:0.0000} {tool.HandOn.z:0.0000} in its axes)");
            tool.EndKnown = true;
        }

        // The tool's grip, where the hand holds it (the model's own origin without one); looked up once per model.
        private static Vector3 GripAt(Tool tool, Transform model)
        {
            if (tool.GripOf != model)
            {
                tool.GripOf = model;
                tool.GripPart = null;
                foreach (var each in model.GetComponentsInChildren<Transform>(true))
                    if (each.name == GripName) { tool.GripPart = each; break; }
            }
            return tool.GripPart != null ? tool.GripPart.position : model.position;
        }

        // Where a tool's line leaves the body: at the suit's right hip, out to the side of its hip joint.
        private static bool TryGetHip(Tool tool, out Vector3 hip)
        {
            hip = Vector3.zero;
            if ((rightHip == null || leftHip == null || pelvis == null || spine == null) && Time.unscaledTime >= nextBones)
            {
                nextBones = Time.unscaledTime + 1f;
                var grab = GameRefs.Grab;
                if (grab != null)
                    foreach (var suit in grab.GetComponentsInChildren<SkinnedMeshRenderer>(true))
                    {
                        if (suit == null || suit.name != SuitName) continue;
                        foreach (var bone in suit.bones)
                        {
                            if (bone == null) continue;
                            if (bone.name == "rig_right_hip") rightHip = bone;
                            else if (bone.name == "rig_left_hip") leftHip = bone;
                            else if (bone.name == "rig_pelvis") pelvis = bone;
                            else if (bone.name == "rig_spine1") spine = bone;
                        }
                    }
                if (logs++ < 20)
                    Plugin.Log.LogInfo($"Loose tools: hip for the lines {(rightHip != null && leftHip != null && pelvis != null && spine != null ? "on the suit's hip joints" : "not found on the suit, from the head instead")}");
            }
            var scale = VrCamera.WorldScale;
            if (rightHip != null && leftHip != null && pelvis != null && spine != null)
            {
                var right = rightHip.position - leftHip.position;
                var up = spine.position - pelvis.position;
                if (right.sqrMagnitude > 1e-8f && up.sqrMagnitude > 1e-8f)
                {
                    right.Normalize();
                    up = Vector3.ProjectOnPlane(up, right).normalized;
                    var forward = Vector3.Cross(right, up);
                    // On the hip of the side the tools hang on (VrToolBelt.LeftHanded); the detonator's on the other.
                    var mirrored = VrToolBelt.LeftHanded != tool.Prop;
                    hip = (mirrored ? leftHip : rightHip).position + (right * (mirrored ? -tool.Hip.x : tool.Hip.x) + up * tool.Hip.y + forward * tool.Hip.z) * scale;
                    return true;
                }
            }
            // Without the suit's bones: below the head, as the belt's zones are measured.
            var vr = VrCamera.Instance;
            var rig = vr != null ? vr.transform.parent : null;
            if (rig == null) return false;
            var ahead = vr.transform.localRotation * Vector3.forward;
            ahead.y = 0f;
            if (ahead.sqrMagnitude < 1e-4f) return false;
            hip = rig.TransformPoint(vr.transform.localPosition + Quaternion.LookRotation(ahead, Vector3.up) * VrToolBelt.Sided(new Vector3((tool.Prop ? -1f : 1f) * (0.2f + tool.Hip.x), -0.72f, tool.Hip.z)));
            return true;
        }

        // ---- Each physics step (VrFrame) ----

        // The line at its end: the tool's end and the hip no longer move apart, each giving way by its mass (the tool
        // also by turning about its middle, as the line pulls at its grip). A body holding on to something does not give.
        public static void FixedUpdate()
        {
            if (!On) return;
            var player = VrToolCollide.Player();
            if (player == null) return;
            var scale = VrCamera.WorldScale;
            var most = Length * scale;
            foreach (var tool in Tools)
            {
                if (!tool.Loose || tool.Handing || tool.Rigid == null || !tool.HipKnown || !tool.Body.activeInHierarchy) continue;
                tool.SpeedBefore = tool.Rigid.velocity - player.velocity;
                var held = VrHand.Left.State == HandState.Holding || VrHand.Right.State == HandState.Holding;
                var bodyShare = player.isKinematic || held ? 0f : 1f / VrClimb.BodyWeight;
                var taut = Tighten(tool, player, player.position + tool.HipFromBody, player.velocity, most, bodyShare, out var far, out var apart, out var impulse);
                if (taut && !tool.Taut && logs++ < 120)
                    Plugin.Log.LogInfo($"Loose tools: {tool.Name}'s line taut at {far / scale:0.00} m, moving apart at {apart:0.00} m/s, impulse {impulse:0.0} kg m/s"
                        + $" (body {(bodyShare > 0f ? $"{1f / bodyShare:0} kg" : "held")})");
                tool.Taut = taut;
                var outer = tool.Outer;
                if (outer < 0 || !tool.Hauls[outer] || !tool.PullKnown[outer]) continue;
                // The line in a hand: the same between the tool and the hand nearest to it, over the line between them.
                var pulled = Tighten(tool, player, player.position + tool.PullFromBody[outer], player.velocity + tool.PullSpeed[outer], tool.Rest, bodyShare, out far, out apart, out impulse);
                if (pulled && !tool.Pulled && logs++ < 200)
                    Plugin.Log.LogInfo($"Loose tools: {tool.Name} pulled by its line, {far / scale:0.00} m from the {VrHand.Both[outer].Name} hand, hand at {tool.PullSpeed[outer].magnitude:0.00} m/s, moving apart at {apart:0.00} m/s, impulse {impulse:0.0} kg m/s");
                tool.Pulled = pulled;
                if (pulled && far - tool.Rest > Slip * scale) tool.Slipped = true;
            }
        }

        // One end of a line at the tool, the other at a place that moves with the player's body (from, going at speed),
        // most line between them: past that the two no longer move apart. True while the line is taut.
        private static bool Tighten(Tool tool, Rigidbody player, Vector3 from, Vector3 speed, float most, float bodyShare, out float far, out float apart, out float impulse)
        {
            var rigid = tool.Rigid;
            var end = rigid.position + rigid.rotation * tool.End;
            var along = end - from;
            far = along.magnitude;
            apart = impulse = 0f;
            var over = far - most;
            if (over <= 0f || far < 1e-4f) return false;
            var way = along / far;
            var lever = end - rigid.worldCenterOfMass;
            apart = Vector3.Dot(rigid.GetPointVelocity(end) - speed, way);
            var toolShare = 1f / rigid.mass + Vector3.Dot(Vector3.Cross(Turning(rigid, Vector3.Cross(lever, way)), lever), way);
            impulse = ToolLine.Taut(apart, over, Pull, MostPull * VrCamera.WorldScale, toolShare, bodyShare);
            if (impulse > 0f)
            {
                rigid.AddForceAtPosition(-way * impulse, end, ForceMode.Impulse);
                if (bodyShare > 0f) player.AddForce(way * (impulse * bodyShare), ForceMode.VelocityChange);
            }
            return true;
        }

        // How a turning impulse turns the body (its inverse inertia, in the world).
        private static Vector3 Turning(Rigidbody body, Vector3 impulse)
        {
            var frame = body.rotation * body.inertiaTensorRotation;
            var local = Quaternion.Inverse(frame) * impulse;
            var inertia = body.inertiaTensor;
            local = new Vector3(inertia.x > 1e-6f ? local.x / inertia.x : 0f, inertia.y > 1e-6f ? local.y / inertia.y : 0f, inertia.z > 1e-6f ? local.z / inertia.z : 0f);
            return frame * local;
        }

        // ---- Taking a tool out of the air ----

        // The belt zone of a hand (VrToolBelt): a tool afloat is not drawn at the body, but by its grip where it floats;
        // and the grapple at the belt is drawn by its grip as well, besides the place beside the hip where the hand has
        // found it so far (VrToolBelt.GripAt; Raik's third test: the zone at the hip is to be clearly larger). Of several
        // grips within reach the nearest counts.
        public static VrToolBelt.Zone Zone(VrInput.Hand controller, VrToolBelt.Zone zone)
        {
            if (!On) return zone;
            var rig = VrCamera.Instance != null ? VrCamera.Instance.transform.parent : null;
            if (rig == null || !controller.TryGetPosition(out var local)) return zone;
            var hand = rig.TransformPoint(local);
            var scale = VrCamera.WorldScale;
            var reach = GripBall * scale;
            foreach (var tool in Tools)
                if (tool.Loose && zone == tool.Zone) zone = VrToolBelt.Zone.None;
            // The detonator at the hip is taken by its grip as the grapple is (Raik, 2026-11-18: "that works really
            // well, take it over"): once its grip is known, the box at the hip no longer counts.
            if (zone == VrToolBelt.Zone.Detonator && TryGetGripOnBody(Detonator, out _)) zone = VrToolBelt.Zone.None;
            var found = VrToolBelt.Zone.None;
            var nearest = float.MaxValue;
            Tool missed = null;
            var missedBy = float.MaxValue;
            foreach (var tool in Tools)
            {
                Vector3 grip;
                if (tool.Loose)
                {
                    if (tool.Body == null || !tool.Body.activeInHierarchy) continue;
                    grip = tool.Body.transform.TransformPoint(tool.Grip);
                }
                else if ((tool != Grapple && !tool.Prop) || !TryGetGripOnBody(tool, out grip)) continue;
                var off = (hand - grip).magnitude;
                if (off < reach)
                {
                    if (off >= nearest) continue;
                    nearest = off;
                    found = tool.Zone;
                }
                else if (off < missedBy)
                {
                    missedBy = off;
                    missed = tool;
                }
            }
            if (found != VrToolBelt.Zone.None) return found;
            if (zone == VrToolBelt.Zone.None && missed != null && controller.Grip.Pressed && missedBy < MissWithin * scale && missLogs++ < 60)
                Plugin.Log.LogInfo($"Loose tools: {controller.Name} grip pressed {missedBy / scale:0.00} m from the grip of the {missed.Name} {(missed.Loose ? "afloat" : "at the belt")}, outside its {GripBall:0.00} m");
            return zone;
        }

        // Where the grip of a tool is that hangs on the body (the hand's place on its model), in the world; false while
        // it does not hang there or the hand's place is not known yet.
        private static bool TryGetGripOnBody(Tool tool, out Vector3 grip)
        {
            grip = Vector3.zero;
            // The detonator: on its copy at the hip (VrBeltGrapple), while that shows.
            if (tool.Prop) return tool.HandKnown && VrBeltGrapple.DetonatorAtHip(tool.HandOn, out grip, out var shown) && shown;
            if (!tool.HandKnown || !VrBeltGrapple.Parts(tool.Kind, out var model, out var rest, out _, out _) || model.parent != rest) return false;
            if (!model.gameObject.activeInHierarchy) return false;
            grip = model.TransformPoint(tool.HandOn);
            return true;
        }

        // Whether the hand that lets a tool go is where the tool's grip hangs on the body (the grapple's, at the belt):
        // the tool has just been laid on its rest point, which is at the hip already (VrBeltGrapple).
        private static bool AtItsGripOnTheBody(Tool tool, VrInput.Hand controller, Transform model, Transform rest) =>
            TryGetHung(tool, controller, model, rest, out var off) && off < (tool == Cutter ? CutterStowBall : StowBall) * VrCamera.WorldScale;

        private static int awayLogs;

        // How far that hand is from where the tool's grip hangs on the body (the grapple's at the hip, the cutter's on
        // the back), in the world's units; false while the hand's place on the tool is not known.
        private static bool TryGetHung(Tool tool, VrInput.Hand controller, Transform model, Transform rest, out float off)
        {
            off = 0f;
            if (tool.Prop || !tool.HandKnown || model == null || rest == null) return false;
            var rig = VrCamera.Instance != null ? VrCamera.Instance.transform.parent : null;
            if (rig == null || !controller.TryGetPosition(out var local)) return false;
            var hung = rest.TransformPoint(Vector3.Scale(model.localScale, tool.HandOn));
            off = (rig.TransformPoint(local) - hung).magnitude;
            return true;
        }

        // For the F8 dump.
        public static string Describe()
        {
            var text = $"loose tools: {(On ? "on" : "off")}, line {Length:0.#} m";
            foreach (var tool in Tools)
                text += $", {tool.Name} {(tool.Loose ? $"afloat{(tool.Taut ? " (line taut)" : "")}" : "not afloat")}{(tool.Hauls[0] || tool.Hauls[1] ? $" (line in {(tool.Hauls[0] && tool.Hauls[1] ? "both hands" : tool.Hauls[0] ? "the left hand" : "the right hand")})" : "")}"
                    + (tool.EndKnown ? "" : ", line's end not yet on the model") + (tool.HandKnown ? "" : ", grip not yet measured");
            return text;
        }

        // On unload (hot reload): the tools go back onto the body, and the bodies go with the old instance.
        public static void Release()
        {
            closing = true;
            foreach (var tool in Tools)
            {
                End(tool, "unloading");
                if (tool.Body != null) Object.Destroy(tool.Body);
                tool.Body = null;
                tool.Rigid = null;
                tool.Pieces.Clear();
                tool.Holder = null;
                tool.HandOf = null;
                tool.HandModel = null;
                tool.HandKnown = tool.EndKnown = false;
            }
            VrBeltGrapple.Hide();
            VrToolLine.Release();
            rightHip = leftHip = pelvis = spine = null;
            detonatorProp = null;
        }
    }

    // The game puts the grapple or the cutter away: let go by the hand, it stays afloat (VrLooseTools.PutAway).
    [HarmonyPatch]
    internal static class ToolLetGoPatch
    {
        [HarmonyPostfix]
        [HarmonyPatch(typeof(GunController), nameof(GunController.UnequipGun))]
        private static void GrapplePutAway() => PutAway(EquipmentController.Equipment.GrappleHook);

        [HarmonyPostfix]
        [HarmonyPatch(typeof(CuttingToolController), nameof(CuttingToolController.UnequipCutter))]
        private static void CutterPutAway() => PutAway(EquipmentController.Equipment.CuttingTool);

        private static int errors;

        private static void PutAway(EquipmentController.Equipment kind)
        {
            try { VrLooseTools.PutAway(kind); }
            catch (System.Exception e)
            {
                if (errors++ < 5) Plugin.Log.LogError($"Loose tools: letting go of the {kind} failed: {e}");
            }
        }
    }
}
