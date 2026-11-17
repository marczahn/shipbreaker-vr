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
    internal static class VrLooseTools
    {
        static VrLooseTools() => VrSession.OnUnload("loose tools", Release);

        // The line's length, the tool's mass and how it floats, in meters, kilograms and per second at world scale 1.
        private const float Mass = 5f;
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
        // The hand takes a tool afloat within GripReach of its grip or Touch of its shape.
        private const float GripReach = 0.16f;
        private const float Touch = 0.07f;
        // A tool this far beyond its line's length (the player was moved, a respawn) goes back onto the body.
        private const float Lost = 4f;
        // The line leaves the tool this far under its grip.
        private const float EndUnderGrip = 0.06f;
        private const string GripName = "RHandTarget";
        private const string SuitName = "geo_suit";

        private static ConfigEntry<bool> enabled, exact, lineInHand;
        private static ConfigEntry<float> lineLength;
        private static bool bound, closing;

        private static void Bind()
        {
            if (bound || VrSettings.File == null) return;
            bound = true;
            enabled = VrSettings.Bind("Experimente", "WerkzeugeLoslassen", true,
                "Experiment: the cutter and the grapple stay afloat, on a line to the hip, when their grip is let go away from their place on the body.");
            exact = VrSettings.Bind("Experimente", "WerkzeugformGenau", true,
                "Experiment: with extended tool physics, the tool in the hand meets things with the shape of its model instead of boxes around its parts.");
            lineInHand = VrSettings.Bind("Experimente", "LeineInDerHand", true,
                "Experiment: the line from the hip also shows while the tool is in the hand.");
            lineLength = VrSettings.Bind("Experimente", "LeinenLaenge", 5f,
                "Experiment: how long a tool's line is, in meters.", new AcceptableValueRange<float>(1f, 15f));
        }

        public static bool On { get { Bind(); return enabled != null && enabled.Value && !closing; } }
        public static bool Exact { get { Bind(); return exact != null && exact.Value; } }
        public static bool LineInHand { get { Bind(); return lineInHand == null || lineInHand.Value; } }
        public static float Length { get { Bind(); return lineLength != null ? lineLength.Value : 5f; } }

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

            public Tool(EquipmentController.Equipment kind, VrToolBelt.Zone zone, int index, Vector3 hip)
            {
                Kind = kind;
                Zone = zone;
                Index = index;
                Hip = hip;
            }
        }

        private static readonly Tool Grapple = new Tool(EquipmentController.Equipment.GrappleHook, VrToolBelt.Zone.Grapple, 0, new Vector3(0.09f, 0.04f, 0.03f));
        private static readonly Tool Cutter = new Tool(EquipmentController.Equipment.CuttingTool, VrToolBelt.Zone.Cutter, 1, new Vector3(0.07f, 0.04f, -0.09f));
        private static readonly Tool[] Tools = { Grapple, Cutter };
        private static readonly List<MeshFilter> Filters = new List<MeshFilter>();
        private static readonly List<Collider> PlayerColliders = new List<Collider>();
        private static Transform rightHip, leftHip, pelvis, spine;
        private static float nextBones;
        private static int logs;

        private static Tool Of(EquipmentController.Equipment kind) =>
            kind == EquipmentController.Equipment.GrappleHook ? Grapple : kind == EquipmentController.Equipment.CuttingTool ? Cutter : null;

        public static bool IsLoose(EquipmentController.Equipment kind) => Of(kind) is Tool tool && tool.Loose;

        // Whether that collider is a piece of a tool afloat.
        public static bool Owns(Collider collider)
        {
            if (collider == null || (!Grapple.Loose && !Cutter.Loose)) return false;
            var body = collider.attachedRigidbody;
            return body != null && ((Grapple.Loose && body == Grapple.Rigid) || (Cutter.Loose && body == Cutter.Rigid));
        }

        // Whether that rest point follows a tool afloat (VrBeltGrapple leaves it alone then).
        public static bool Holds(Transform point)
        {
            foreach (var tool in Tools)
                if (tool.Loose && VrBeltGrapple.Parts(tool.Kind, out _, out var rest, out _, out var shadowRest) && (point == rest || point == shadowRest)) return true;
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
                    if (!hand.HoldsTool || hand.Tool != tool.Kind) continue;
                    held = true;
                    if (!hand.Input.Grip.Down) continue;
                    tool.Holder = hand;
                    tool.HeldAt = Time.unscaledTime;
                }
                if (!held && tool.Holder != null && Time.unscaledTime - tool.HeldAt > 1f) tool.Holder = null;
            }
        }

        // ---- Letting go ----

        // From ToolLetGoPatch: the game has just put that tool away, its model on its rest point where the hand held it.
        public static void PutAway(EquipmentController.Equipment kind)
        {
            var tool = Of(kind);
            if (tool == null) return;
            var holder = tool.Holder;
            tool.Holder = null;
            if (!On || tool.Loose || holder == null || !Usable()) return;
            // Put away by the game with the grip still held (the scanner hides it, another tool takes its place).
            if (holder.Input.Grip.Down) return;
            if (!VrBeltGrapple.Parts(kind, out var model, out var rest, out _, out _) || model.parent != rest) return;
            if (VrToolBelt.HeadLocal(holder.Input, out var local) && VrToolBelt.Stows(tool.Zone, local))
            {
                if (logs++ < 80) Plugin.Log.LogInfo($"Loose tools: {kind} let go at its place on the body, put away");
                return;
            }
            LetGo(tool, model, holder);
        }

        private static void LetGo(Tool tool, Transform model, VrHand holder)
        {
            var player = VrToolCollide.Player();
            var rig = VrCamera.Instance != null ? VrCamera.Instance.transform.parent : null;
            if (player == null || rig == null || !Build(tool, model, true)) return;
            var scale = VrCamera.WorldScale;
            var body = tool.Body.transform;
            body.SetPositionAndRotation(model.position, model.rotation);
            // The parts as they sit now (the grapple's claws open and close).
            foreach (var piece in tool.Pieces)
                if (piece.Part != null && piece.Holder != null)
                    piece.Holder.SetPositionAndRotation(piece.Part.position + piece.Part.rotation * piece.Shape.At[piece.Index], piece.Part.rotation * piece.Shape.Turn[piece.Index]);
            Measure(tool, model);
            tool.Body.SetActive(true);
            // The player's own body does not bump into its tools (nor do they knock it about on the line).
            PlayerColliders.Clear();
            player.GetComponentsInChildren(true, PlayerColliders);
            PlayerColliders.RemoveAll(own => own == null || !own.enabled || !own.gameObject.activeInHierarchy);
            foreach (var piece in tool.Pieces)
                foreach (var own in PlayerColliders)
                    if (piece.Collider != null) Physics.IgnoreCollision(piece.Collider, own, true);

            var rigid = tool.Rigid;
            rigid.position = model.position;
            rigid.rotation = model.rotation;
            var velocity = player.velocity;
            var spin = Vector3.zero;
            var moved = Vector3.zero;
            if (VrClimb.LetGoMotion(holder.Input, Drift * scale, TurnDrift, out var handSpeed, out var handSpin) && holder.Input.TryGetPosition(out var handLocal))
            {
                // The tool's middle goes as a point of the hand there would, no faster than an arm can throw it.
                var lever = body.TransformPoint(tool.Middle) - rig.TransformPoint(handLocal);
                moved = Vector3.ClampMagnitude(handSpeed + Vector3.Cross(handSpin, lever), ClimbContact.MostThrowSpeed(Mass) * scale);
                spin = handSpin;
            }
            rigid.velocity = velocity + moved;
            rigid.angularVelocity = spin;
            tool.Loose = true;
            tool.LooseSince = Time.unscaledTime;
            tool.Taut = false;
            Hang(tool);
            holder.Input.Buzz(0.2f, 0.03f);
            Plugin.Log.LogInfo($"Loose tools: {tool.Kind} let go by the {holder.Name} hand, afloat at {moved.magnitude:0.00} m/s against the body"
                + $" (turning {spin.magnitude * Mathf.Rad2Deg:0}°/s), {tool.Pieces.Count} piece(s), layer {LayerMask.LayerToName(tool.Body.layer)}");
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
            var body = new GameObject($"ShipbreakerVR loose {tool.Kind}") { layer = layer };
            body.SetActive(false);
            body.transform.SetPositionAndRotation(model.position, model.rotation);
            var rigid = body.AddComponent<Rigidbody>();
            rigid.mass = Mass;
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
            tool.Body = body;
            tool.Rigid = rigid;
            tool.Signature = signature;
            tool.BuiltSize = size;
            tool.BuiltAt = Time.unscaledTime;
            Measure(tool, model);
            Plugin.Log.LogInfo($"Loose tools: body for the {tool.Kind} from {Filters.Count} part(s) of '{model.name}' in {tool.Pieces.Count} piece(s), "
                + $"size {model.lossyScale.x:0.00}, layer {LayerMask.LayerToName(layer)} ({layer}), built in {watch.ElapsedMilliseconds} ms");
            return tool.Pieces.Count > 0;
        }

        // The grip, the line's end and the middle of the shape, in the body's axes, with the body at the model's place.
        private static void Measure(Tool tool, Transform model)
        {
            var body = tool.Body.transform;
            var gripAt = GripAt(tool, model);
            tool.Grip = body.InverseTransformPoint(gripAt);
            tool.End = body.InverseTransformPoint(gripAt - model.up * (EndUnderGrip * VrCamera.WorldScale));
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
            if (tool.Body == null || !VrBeltGrapple.Parts(tool.Kind, out var model, out var rest, out var shadow, out var shadowRest)) return;
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
            if (tool.Body != null) tool.Body.SetActive(false);
            if (logs++ < 80) Plugin.Log.LogInfo($"Loose tools: {tool.Kind} no longer afloat ({why}) after {Time.unscaledTime - tool.LooseSince:0.0} s");
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
            foreach (var tool in Tools)
            {
                var known = VrBeltGrapple.Parts(tool.Kind, out var model, out var rest, out _, out _);
                if (tool.Loose)
                {
                    if (!usable) End(tool, "no shift in VR");
                    else if (tool.Body == null || !known) End(tool, "gone");
                    else if (model.parent != rest) End(tool, "taken");
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
                var inHand = !tool.Loose && model.parent != rest && model.gameObject.activeInHierarchy;
                if (inHand && Time.unscaledTime - tool.BuiltAt > 1f)
                {
                    // Its body built ahead while it is held, so letting go costs nothing; looked at again every second,
                    // as the model may not be ready yet or change (the cutter's mirrored side, VrToolMirror).
                    tool.BuiltAt = Time.unscaledTime;
                    Build(tool, model, false);
                }
                if (tool.Loose && (tool.Body.transform.TransformPoint(tool.End) - hip).magnitude > (Length + Lost) * scale)
                    End(tool, "too far off, back onto the body");
                if (!tool.Loose && !(inHand && LineInHand))
                {
                    VrToolLine.Update(tool.Index, false, default, default, false, default);
                    continue;
                }
                var end = tool.Loose ? tool.Body.transform.TransformPoint(tool.End) : GripAt(tool, model) - model.up * (EndUnderGrip * scale);
                VrToolLine.Update(tool.Index, true, hip, end, !tool.Loose, carried);
            }
            VrToolLine.Build();
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
                    hip = rightHip.position + (right * tool.Hip.x + up * tool.Hip.y + forward * tool.Hip.z) * scale;
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
            hip = rig.TransformPoint(vr.transform.localPosition + Quaternion.LookRotation(ahead, Vector3.up) * new Vector3(0.2f + tool.Hip.x, -0.72f, tool.Hip.z));
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
                if (!tool.Loose || tool.Rigid == null || !tool.HipKnown || !tool.Body.activeInHierarchy) continue;
                var rigid = tool.Rigid;
                var hip = player.position + tool.HipFromBody;
                var end = rigid.position + rigid.rotation * tool.End;
                var along = end - hip;
                var far = along.magnitude;
                var over = far - most;
                if (over <= 0f || far < 1e-4f)
                {
                    tool.Taut = false;
                    continue;
                }
                var way = along / far;
                var lever = end - rigid.worldCenterOfMass;
                var apart = Vector3.Dot(rigid.GetPointVelocity(end) - player.velocity, way);
                var toolShare = 1f / rigid.mass + Vector3.Dot(Vector3.Cross(Turning(rigid, Vector3.Cross(lever, way)), lever), way);
                var held = VrHand.Left.State == HandState.Holding || VrHand.Right.State == HandState.Holding;
                var bodyShare = player.isKinematic || held ? 0f : 1f / VrClimb.BodyWeight;
                var impulse = ToolLine.Taut(apart, over, Pull, MostPull * scale, toolShare, bodyShare);
                if (impulse > 0f)
                {
                    rigid.AddForceAtPosition(-way * impulse, end, ForceMode.Impulse);
                    if (bodyShare > 0f) player.AddForce(way * (impulse * bodyShare), ForceMode.VelocityChange);
                }
                if (!tool.Taut && logs++ < 120)
                    Plugin.Log.LogInfo($"Loose tools: {tool.Kind}'s line taut at {far / scale:0.00} m, moving apart at {apart:0.00} m/s, impulse {impulse:0.0} kg m/s"
                        + $" (body {(bodyShare > 0f ? $"{1f / bodyShare:0} kg" : "held")})");
                tool.Taut = true;
            }
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

        // The belt zone of a hand (VrToolBelt): a tool afloat is not drawn at the body, but where it floats.
        public static VrToolBelt.Zone Zone(VrInput.Hand controller, VrToolBelt.Zone zone)
        {
            if (!On || (!Grapple.Loose && !Cutter.Loose)) return zone;
            foreach (var tool in Tools)
                if (tool.Loose && zone == tool.Zone) zone = VrToolBelt.Zone.None;
            var rig = VrCamera.Instance != null ? VrCamera.Instance.transform.parent : null;
            if (rig == null || !controller.TryGetPosition(out var local)) return zone;
            var hand = rig.TransformPoint(local);
            foreach (var tool in Tools)
                if (tool.Loose && Near(tool, hand)) return tool.Zone;
            return zone;
        }

        private static bool Near(Tool tool, Vector3 hand)
        {
            if (tool.Body == null || !tool.Body.activeInHierarchy) return false;
            var scale = VrCamera.WorldScale;
            var body = tool.Body.transform;
            if ((hand - body.TransformPoint(tool.Middle)).magnitude > 1.5f * scale) return false;
            if ((hand - body.TransformPoint(tool.Grip)).magnitude < GripReach * scale) return true;
            var touch = Touch * scale;
            foreach (var piece in tool.Pieces)
                if (piece.Collider != null && (piece.Collider.ClosestPoint(hand) - hand).sqrMagnitude < touch * touch) return true;
            return false;
        }

        // For the F8 dump.
        public static string Describe()
        {
            var text = $"loose tools: {(On ? "on" : "off")}, line {Length:0.#} m";
            foreach (var tool in Tools)
                text += $", {tool.Kind} {(tool.Loose ? $"afloat{(tool.Taut ? " (line taut)" : "")}" : "not afloat")}";
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
            }
            VrBeltGrapple.Hide();
            VrToolLine.Release();
            rightHip = leftHip = pelvis = spine = null;
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
