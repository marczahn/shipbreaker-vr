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
    // lies in front of it, without the scene's light.
    // With the tool in the hand the hip reels the line: a little more than the way to the tool is out, the rest wound in.
    // Afloat nothing is wound in, so a tool that comes back leaves its line slack in the room.
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

        private sealed class Shown
        {
            public readonly ToolLine Line = new ToolLine(Points);
            public bool Out;
            public float PaidOut;
            public Vector3 HipBefore, EndBefore, CarriedBefore;
            public float StepBefore;
            public Mesh Mesh;
            public bool Reels;
        }

        private static readonly Shown[] Lines = { new Shown(), new Shown() };
        private static readonly List<Vector3> Vertices = new List<Vector3>();
        private static readonly List<Color> Colors = new List<Color>();
        private static readonly List<int> Triangles = new List<int>();
        private static Material material;
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
                if (!line.Out && logs++ < 40) Plugin.Log.LogInfo($"Tool line {index}: out, {straight / scale:0.00} m to the tool");
                line.Out = true;
            }
            line.HipBefore = hip;
            line.EndBefore = end;
            line.Reels = reels;
            // Paused: it stays as it is.
            if (dt <= 1e-5f) return;
            line.PaidOut = ToolLine.PaidOut(line.PaidOut, straight, most, reels, Slack, Spare * scale, Reel * scale, dt);
            // Frames differ in length: the points' last move is scaled to this frame's.
            var ratio = line.StepBefore > 1e-5f ? Mathf.Clamp(dt / line.StepBefore, 0.5f, 2f) : 1f;
            line.StepBefore = dt;
            line.Line.Step(hip, end, line.PaidOut, line.CarriedBefore, carried * dt, Mathf.Exp(-Loss * dt) * ratio, 1f - Mathf.Exp(-Stiff * dt), Rounds);
            line.CarriedBefore = carried * dt;
        }

        public static void Hide()
        {
            foreach (var line in Lines) line.Out = false;
        }

        // From VrLooseTools.Place, after the lines were stepped: their bands for this frame, turned to the head.
        public static void Build()
        {
            var vr = VrCamera.Instance;
            if (vr == null) return;
            var eye = vr.transform.position;
            var half = Width * 0.5f * VrCamera.WorldScale;
            foreach (var line in Lines)
            {
                if (!line.Out) continue;
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
                    var side = Vector3.Cross(along, eye - point);
                    side = side.sqrMagnitude > 1e-12f ? side.normalized * half : Vector3.zero;
                    var light = row % 2 == 0 ? 1f : Band;
                    Vertices.Add(point - side);
                    Vertices.Add(point);
                    Vertices.Add(point + side);
                    Colors.Add(new Color(Edge * light, Edge * light, Edge * light, 1f));
                    Colors.Add(new Color(light, light, light, 1f));
                    Colors.Add(new Color(Edge * light, Edge * light, Edge * light, 1f));
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
