using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.HighDefinition;

namespace ShipbreakerVR
{
    // Experiment (Raik, 2026-11-17): the lines from the hip to the cutter and the grapple, as seen (VrLooseTools has what
    // they do). Each is a chain of points (ToolLine) stepped once a frame with its ends at the hip and under the tool's
    // grip, and drawn as a flat band turned to the head, darker along its edges so it reads as round (Raik: flat is
    // enough as long as it looks like it is in the room). It is drawn with the laser, after the scene and hidden by what
    // lies in front of it. The scene's light does not reach it there, so it is shaded by hand (Shade): round against the
    // sun, and dark where something stands between it and the sun.
    // With the tool in the hand the hip reels the line: a little more than the way to the tool is out, the rest wound in.
    // Afloat nothing is wound in, so a tool that comes back leaves its line slack in the room.
    // A hand can take hold of the line of a tool afloat (Take): the point it holds goes with the hand, the hip reels the
    // part up to the hand, and the part from the hand to the tool keeps its length, so a pull goes to the tool.
    internal static class VrToolLine
    {
        private const int Points = 26;
        // The smooth curve through the points has this many steps between two of them.
        private const int Between = 3;
        // In meters at world scale 1.
        private const float Width = 0.007f;
        // In the hand: this share of the way to the tool, and Spare, is out as slack; wound in at Reel per second.
        private const float Slack = 0.25f;
        private const float Spare = 0.12f;
        private const float Reel = 1.2f;
        // The share of its own movement the line loses per second.
        private const float Loss = 1.4f;
        // How stiff it is against bending: each point goes this share of the way towards the middle of its neighbors
        // per second (1 - e^-Stiff dt).
        private const float Stiff = 9f;
        // Laid out anew it bows this far towards the feet, so its slack has a side to go to.
        private const float Bow = 0.02f;
        private const int Rounds = 14;
        // An end that jumps this far in a frame (the player was moved) lays the line anew.
        private const float Jump = 2f;
        private static readonly Color LineColor = new Color(0.85f, 0.5f, 0.08f, 1f);
        private const float Edge = 0.4f;
        private const float Band = 0.82f;
        // Shaded: what the line shows of its color away from the sun or in shadow, and what the sun adds where it
        // falls straight on it; the band's edges are the sides of a round line, turned EdgeTurn degrees off the eye.
        private const float Ambient = 0.3f;
        private const float Sun = 0.85f;
        private const float EdgeTurn = 65f;
        // The sides stay a little darker than the middle whatever the light, so the band keeps its round look.
        private const float Limb = 0.8f;
        // Whether a point lies in shadow is asked of the physics engine, for RaysEachFrame points of a line per frame,
        // up to ShadowReach meters towards the sun; the points take their light on at LightRate per second.
        private const int RaysEachFrame = 6;
        private const float ShadowReach = 80f;
        private const float LightRate = 8f;

        private sealed class Shown
        {
            public readonly ToolLine Line = new ToolLine(Points);
            public bool Out;
            public float PaidOut;
            public Vector3 HipBefore, EndBefore, CarriedBefore;
            public float StepBefore;
            public Mesh Mesh;
            public bool Reels;
            // The point a hand holds (-1: none), where the hand is, and how much line lies between the hip and the hand
            // (Near) and between the hand and the tool (Rest).
            public int Held = -1;
            public Vector3 HeldAt;
            public float Near, Rest;
            // How much of the sun reaches each point (0 in shadow to 1), what it goes towards, and the point asked next.
            public readonly float[] Lit = new float[Points];
            public readonly float[] LitGoal = new float[Points];
            public int Asked;
        }

        private static readonly Shown[] Lines = { new Shown(), new Shown() };
        private static readonly List<Vector3> Vertices = new List<Vector3>();
        private static readonly List<Color> Colors = new List<Color>();
        private static readonly List<int> Triangles = new List<int>();
        private static readonly RaycastHit[] ShadowHits = new RaycastHit[8];
        private static Material material;
        private static Light sun;
        private static float nextSun;
        private static int logs;

        // How much of a tool's line is out, in meters (0 while it is not shown).
        public static float PaidOut(int index) => Lines[index].Out ? Lines[index].PaidOut : 0f;

        // From VrLooseTools.Place, each frame for each tool: its line's ends in the world, whether the hip reels it, and
        // how fast the player moves (which the line shares).
        public static void Update(int index, bool show, Vector3 hip, Vector3 end, bool reels, Vector3 carried)
        {
            var line = Lines[index];
            if (!show)
            {
                line.Out = false;
                line.Held = -1;
                return;
            }
            var scale = VrCamera.WorldScale;
            var most = VrLooseTools.Length * scale;
            var straight = (end - hip).magnitude;
            var dt = Mathf.Min(Time.deltaTime, 1f / 30f);
            if (!line.Out || (hip - line.HipBefore).magnitude > Jump * scale || (end - line.EndBefore).magnitude > Jump * scale)
            {
                var rig = VrCamera.Instance != null ? VrCamera.Instance.transform.parent : null;
                line.Line.Lay(hip, end, rig != null ? rig.up * (-Bow * scale) : Vector3.zero);
                line.PaidOut = Mathf.Min(straight, most);
                line.StepBefore = dt;
                line.CarriedBefore = Vector3.zero;
                for (var i = 0; i < line.Lit.Length; i++) line.Lit[i] = line.LitGoal[i] = 1f;
                if (!line.Out && logs++ < 40) Plugin.Log.LogInfo($"Tool line {index}: out, {straight / scale:0.00} m to the tool");
                line.Out = true;
            }
            line.HipBefore = hip;
            line.EndBefore = end;
            line.Reels = reels;
            // Paused: it stays as it is.
            if (dt <= 1e-5f) return;
            // Frames differ in length: the points' last move is scaled to this frame's.
            var ratio = line.StepBefore > 1e-5f ? Mathf.Clamp(dt / line.StepBefore, 0.5f, 2f) : 1f;
            line.StepBefore = dt;
            var keep = Mathf.Exp(-Loss * dt) * ratio;
            var bend = 1f - Mathf.Exp(-Stiff * dt);
            if (line.Held > 0)
            {
                // Held in a hand: the hip reels what lies up to the hand, as it does up to a tool in the hand; the rest
                // keeps its length (drawn no shorter than the way to the tool, should the tool not come along).
                line.Near = ToolLine.PaidOut(line.Near, (line.HeldAt - hip).magnitude, most, true, Slack, Spare * scale, Reel * scale, dt);
                var far = Mathf.Max(line.Rest, (end - line.HeldAt).magnitude);
                line.PaidOut = line.Near + far;
                line.Line.Step(hip, end, far, line.Held, line.HeldAt, line.Near, line.CarriedBefore, carried * dt, keep, bend, Rounds);
            }
            else
            {
                line.PaidOut = ToolLine.PaidOut(line.PaidOut, straight, most, reels, Slack, Spare * scale, Reel * scale, dt);
                line.Line.Step(hip, end, line.PaidOut, line.CarriedBefore, carried * dt, keep, bend, Rounds);
            }
            line.CarriedBefore = carried * dt;
        }

        // A hand at that place takes hold of a tool's line, if the line is within reach of it: rest is how much line
        // lies from the hand to the tool then.
        public static bool Take(int index, Vector3 at, float reach, out float rest)
        {
            var line = Lines[index];
            rest = 0f;
            if (!line.Out || line.Held > 0) return false;
            if (line.Line.Nearest(at, out var point, out rest) > reach) return false;
            var points = line.Line.At;
            line.Held = point;
            line.HeldAt = at;
            line.Near = Mathf.Max(line.Line.Length() - rest, (at - points[0]).magnitude);
            line.Rest = rest = Mathf.Max(rest, (points[points.Length - 1] - at).magnitude);
            return true;
        }

        // Where the hand that holds a tool's line is now.
        public static void Carry(int index, Vector3 at) => Lines[index].HeldAt = at;

        public static void Drop(int index) => Lines[index].Held = -1;

        public static void Hide()
        {
            foreach (var line in Lines)
            {
                line.Out = false;
                line.Held = -1;
            }
        }

        // The sun: the brightest light that shines from one direction, looked for now and then.
        private static bool TryGetSun(out Vector3 toSun)
        {
            if (Time.unscaledTime >= nextSun)
            {
                nextSun = Time.unscaledTime + 5f;
                var before = sun;
                sun = null;
                foreach (var light in Object.FindObjectsOfType<Light>())
                    if (light != null && light.type == LightType.Directional && light.isActiveAndEnabled && (sun == null || light.intensity > sun.intensity)) sun = light;
                if (sun != before && logs++ < 40) Plugin.Log.LogInfo("Tool line: shaded by " + (sun != null ? "the light " + sun.name : "no sun, none found"));
            }
            toSun = sun != null ? -sun.transform.forward : Vector3.zero;
            return sun != null && sun.isActiveAndEnabled;
        }

        // How much of the sun reaches the line's points: a few of them asked each frame, taken on smoothly.
        private static void Shade(Shown line, Vector3 toSun, float dt)
        {
            var player = VrToolCollide.Player();
            var reach = ShadowReach * VrCamera.WorldScale;
            var at = line.Line.At;
            for (var n = 0; n < RaysEachFrame; n++)
            {
                var i = line.Asked = (line.Asked + 1) % at.Length;
                var count = Physics.RaycastNonAlloc(at[i], toSun, ShadowHits, reach, Physics.DefaultRaycastLayers, QueryTriggerInteraction.Ignore);
                var shadowed = false;
                for (var h = 0; h < count && !shadowed; h++)
                    shadowed = !VrToolCollide.Own(ShadowHits[h].collider, player) && !VrLooseTools.Owns(ShadowHits[h].collider);
                line.LitGoal[i] = shadowed ? 0f : 1f;
            }
            for (var i = 0; i < at.Length; i++) line.Lit[i] = Mathf.MoveTowards(line.Lit[i], line.LitGoal[i], LightRate * dt);
        }

        // From VrLooseTools.Place, after the lines were stepped: their bands for this frame, turned to the head.
        public static void Build()
        {
            var vr = VrCamera.Instance;
            if (vr == null) return;
            var eye = vr.transform.position;
            var half = Width * 0.5f * VrCamera.WorldScale;
            var toSun = Vector3.zero;
            var shaded = VrLooseTools.Shaded && (Lines[0].Out || Lines[1].Out) && TryGetSun(out toSun);
            var edgeTo = Mathf.Cos(EdgeTurn * Mathf.Deg2Rad);
            var edgeOut = Mathf.Sin(EdgeTurn * Mathf.Deg2Rad);
            foreach (var line in Lines)
            {
                if (!line.Out) continue;
                if (shaded) Shade(line, toSun, Mathf.Min(Time.deltaTime, 1f / 30f));
                if (line.Mesh == null)
                {
                    line.Mesh = new Mesh { name = "ShipbreakerVR Tool Line", hideFlags = HideFlags.HideAndDontSave };
                    line.Mesh.MarkDynamic();
                }
                Vertices.Clear();
                Colors.Clear();
                var at = line.Line.At;
                var rows = (at.Length - 1) * Between + 1;
                for (var row = 0; row < rows; row++)
                {
                    var i = row / Between;
                    var t = (row % Between) / (float)Between;
                    Vector3 point, along;
                    if (i >= at.Length - 1)
                    {
                        point = at[at.Length - 1];
                        along = at[at.Length - 1] - at[at.Length - 2];
                    }
                    else
                    {
                        var p0 = at[Mathf.Max(i - 1, 0)];
                        var p1 = at[i];
                        var p2 = at[i + 1];
                        var p3 = at[Mathf.Min(i + 2, at.Length - 1)];
                        // Catmull-Rom through the points, with its direction.
                        point = 0.5f * (2f * p1 + (p2 - p0) * t + (2f * p0 - 5f * p1 + 4f * p2 - p3) * (t * t) + (3f * p1 - p0 - 3f * p2 + p3) * (t * t * t));
                        along = 0.5f * ((p2 - p0) + (2f * p0 - 5f * p1 + 4f * p2 - p3) * (2f * t) + (3f * p1 - p0 - 3f * p2 + p3) * (3f * t * t));
                    }
                    var toEye = eye - point;
                    var side = Vector3.Cross(along, toEye);
                    var out1 = side.sqrMagnitude > 1e-12f ? side.normalized : Vector3.zero;
                    side = out1 * half;
                    var light = row % 2 == 0 ? 1f : Band;
                    Vertices.Add(point - side);
                    Vertices.Add(point);
                    Vertices.Add(point + side);
                    if (!shaded)
                    {
                        Colors.Add(new Color(Edge * light, Edge * light, Edge * light, 1f));
                        Colors.Add(new Color(light, light, light, 1f));
                        Colors.Add(new Color(Edge * light, Edge * light, Edge * light, 1f));
                        continue;
                    }
                    // A round line: its middle faces the eye, its two sides are turned away to the left and the right,
                    // and each takes the sun by how far it faces it (none in shadow).
                    toEye = toEye.sqrMagnitude > 1e-12f ? toEye.normalized : Vector3.zero;
                    var lit = Sun * Mathf.Lerp(line.Lit[Mathf.Min(i, at.Length - 1)], line.Lit[Mathf.Min(i + 1, at.Length - 1)], t);
                    var left = Mathf.Min(1f, Ambient + lit * Mathf.Max(0f, Vector3.Dot(toEye * edgeTo - out1 * edgeOut, toSun))) * light * Limb;
                    var middle = Mathf.Min(1f, Ambient + lit * Mathf.Max(0f, Vector3.Dot(toEye, toSun))) * light;
                    var right = Mathf.Min(1f, Ambient + lit * Mathf.Max(0f, Vector3.Dot(toEye * edgeTo + out1 * edgeOut, toSun))) * light * Limb;
                    Colors.Add(new Color(left, left, left, 1f));
                    Colors.Add(new Color(middle, middle, middle, 1f));
                    Colors.Add(new Color(right, right, right, 1f));
                }
                var built = line.Mesh.vertexCount == Vertices.Count;
                line.Mesh.SetVertices(Vertices);
                line.Mesh.SetColors(Colors);
                if (!built)
                {
                    Triangles.Clear();
                    for (var row = 0; row + 1 < rows; row++)
                        for (var column = 0; column < 2; column++)
                        {
                            var a = row * 3 + column;
                            var b = a + 3;
                            Triangles.Add(a);
                            Triangles.Add(b);
                            Triangles.Add(a + 1);
                            Triangles.Add(a + 1);
                            Triangles.Add(b);
                            Triangles.Add(b + 1);
                        }
                    line.Mesh.SetTriangles(Triangles, 0);
                }
                line.Mesh.bounds = new Bounds(eye, Vector3.one * 1000f);
            }
        }

        // From our UI pass for each eye (VrUiPass), after the laser: the lines, hidden by what lies in front of them.
        public static void Draw(CustomPassContext context, Camera vrCamera)
        {
            if (vrCamera == null || context.hdCamera.camera != vrCamera) return;
            if (!Lines[0].Out && !Lines[1].Out) return;
            if (material == null) material = VrUtil.DepthTestedMaterial(LineColor);
            VrUi.GetEye(context.hdCamera, out var view, out var proj);
            var cmd = context.cmd;
            cmd.SetRenderTarget(context.cameraColorBuffer, context.cameraDepthBuffer, 0, CubemapFace.Unknown, -1);
            cmd.SetViewport(new Rect(0, 0, context.hdCamera.actualWidth, context.hdCamera.actualHeight));
            cmd.SetViewProjectionMatrices(view, proj);
            foreach (var line in Lines)
                if (line.Out && line.Mesh != null && line.Mesh.vertexCount > 0) cmd.DrawMesh(line.Mesh, Matrix4x4.identity, material, 0, 0);
            context.renderContext.ExecuteCommandBuffer(cmd);
            cmd.Clear();
            context.renderContext.SetupCameraProperties(context.hdCamera.camera, true);
        }

        // On unload (hot reload).
        public static void Release()
        {
            foreach (var line in Lines)
            {
                line.Out = false;
                if (line.Mesh != null) Object.Destroy(line.Mesh);
                line.Mesh = null;
            }
            if (material != null) Object.Destroy(material);
            material = null;
        }
    }
}
