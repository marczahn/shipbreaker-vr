using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Rendering;

namespace ShipbreakerVR
{
    // A small globe at the bottom of the visor that shows where the left controller is while flying (VrControls):
    // three axes through its center, and a 3D cross that moves away from the center as the hand moves away from the
    // reference pose and turns as the hand turns. The cross's arms are the hand's own axes; the upper one is longer and
    // capped, so up stays recognizable however far the hand rolls. The globe's surface is full thrust, the small inner
    // globe the deadzone; the line from the center to the cross grows stronger with thrust. Kept plain and in one color
    // to fit the game's HUD, except for the orange while the trigger is held. The globe is fixed to the visor like the rest of the HUD, its axes along and across the view; they
    // stand for those of the playspace, the frame the hand offset is measured in.
    // When the trigger is released the cross glides back to the center.
    // Drawn in 3D for each eye after the visor (VrUi.Draw), from meshes built here, since the plugin brings no shaders.
    internal static class VrFlightGlobe
    {
        public enum Mode { Aus, Immer, BeimFliegen }

        private const float ReturnSpeed = 12f;
        private const float FadeSpeed = 6f;

        // One light, slightly blue tint like the game's HUD lines; only the brightness tells the parts apart. While the
        // trigger is held, the cross and its line take the orange of the game's gauge bars: the hand is in control.
        private static readonly Color RingColor = new Color(0.8f, 0.92f, 1f, 0.18f);
        private static readonly Color DeadzoneColor = new Color(0.8f, 0.92f, 1f, 0.35f);
        private static readonly Color AxisColor = new Color(0.8f, 0.92f, 1f, 0.4f);
        private static readonly Color MarkerColor = new Color(0.92f, 0.97f, 1f, 0.55f);
        private static readonly Color ActiveColor = new Color(1f, 139f / 255f, 0f, 1f);
        private static readonly Color IdleTether = new Color(0.8f, 0.92f, 1f, 0.25f);

        private static Mesh globe;
        private static Mesh deadzone;
        private static float deadzoneShare;
        private static Mesh marker;
        private static Mesh tether;
        private static Material globeMaterial;
        private static Material markerMaterial;
        private static Material tetherMaterial;
        private static bool logged;

        // What is shown, easing back to the center after the trigger is released; offset in globe radii.
        private static Vector3 shownOffset;
        private static Quaternion shownTurn = Quaternion.identity;
        private static float alpha;
        private static int updatedFrame = -1;

        // Draws the globe into the current eye; view and projection are already set. visor: the visor quad's matrix,
        public static void Draw(CommandBuffer cmd, Matrix4x4 visor)
        {
            var mode = Plugin.FlightGlobe.Value;
            if (mode == Mode.Aus || !VrControls.Flying) { alpha = 0f; return; }
            if (updatedFrame != Time.frameCount)
            {
                updatedFrame = Time.frameCount;
                Animate(mode);
            }
            if (alpha <= 0.01f) return;
            if (globe == null) Create();

            var height = ((Vector3)visor.GetColumn(1)).magnitude;
            var radius = Plugin.FlightGlobeSize.Value * height;
            var center = visor.MultiplyPoint(new Vector3(Plugin.FlightGlobeX.Value, Plugin.FlightGlobeY.Value, 0f));
            var facing = Quaternion.LookRotation(visor.GetColumn(2), visor.GetColumn(1));
            var frame = Matrix4x4.TRS(center, facing, Vector3.one * radius);

            globeMaterial.color = new Color(1f, 1f, 1f, alpha);
            cmd.DrawMesh(globe, frame, globeMaterial, 0, 0);
            if (!Mathf.Approximately(deadzoneShare, VrControls.DeadzoneShare)) CreateDeadzone();
            cmd.DrawMesh(deadzone, frame, globeMaterial, 0, 0);

            var length = shownOffset.magnitude;
            if (length > 0.01f)
            {
                // Faint inside the deadzone, solid at full thrust.
                var strength = Mathf.Clamp01((length - VrControls.DeadzoneShare) / (1f - VrControls.DeadzoneShare));
                var color = VrControls.Holding ? ActiveColor : IdleTether;
                color.a *= alpha * (VrControls.Holding ? Mathf.Lerp(0.45f, 1f, strength) : 1f);
                tetherMaterial.color = color;
                var along = Matrix4x4.TRS(Vector3.zero, Quaternion.LookRotation(shownOffset), new Vector3(1f, 1f, length));
                cmd.DrawMesh(tether, frame * along, tetherMaterial, 0, 0);
            }

            var tint = VrControls.Holding ? ActiveColor : MarkerColor;
            tint.a *= alpha;
            markerMaterial.color = tint;
            cmd.DrawMesh(marker, frame * Matrix4x4.TRS(shownOffset, shownTurn, Vector3.one), markerMaterial, 0, 0);

            if (!logged)
            {
                logged = true;
                Plugin.Log.LogInfo($"Flight globe drawn ({globeMaterial.shader.name}, radius {radius:0.000} m)");
            }
        }

        private static void Animate(Mode mode)
        {
            var dt = Time.unscaledDeltaTime;
            if (VrControls.TryGetHand(out var offset, out var turn))
            {
                shownOffset = Vector3.ClampMagnitude(offset / VrControls.FullOffset, 1f);
                shownTurn = turn;
            }
            else
            {
                var ease = 1f - Mathf.Exp(-ReturnSpeed * dt);
                shownOffset = Vector3.Lerp(shownOffset, Vector3.zero, ease);
                shownTurn = Quaternion.Slerp(shownTurn, Quaternion.identity, ease);
            }
            // BeimFliegen: only while the trigger is held, and until the cross is back in the center.
            var visible = mode == Mode.Immer || VrControls.Holding || shownOffset.magnitude > 0.02f || Quaternion.Angle(shownTurn, Quaternion.identity) > 2f;
            alpha = Mathf.MoveTowards(alpha, visible ? 1f : 0f, FadeSpeed * dt);
        }

        private static void Create()
        {
            var builder = new Builder();
            builder.Ring(Vector3.right, Vector3.up, 1f, 0.005f, RingColor);
            builder.Ring(Vector3.right, Vector3.forward, 1f, 0.005f, RingColor);
            builder.Ring(Vector3.up, Vector3.forward, 1f, 0.005f, RingColor);
            foreach (var axis in new[] { Vector3.right, Vector3.up, Vector3.forward })
                builder.Tube(new[] { -axis, axis }, 0.007f, AxisColor);
            globe = builder.Mesh("Globe");

            // The hand's axes as a cross, the upper arm longer and capped; in globe radii.
            builder = new Builder();
            // White: the material color tints it (MarkerColor, or ActiveColor while the trigger is held).
            builder.Tube(new[] { new Vector3(-0.22f, 0f, 0f), new Vector3(0.22f, 0f, 0f) }, 0.015f, Color.white);
            builder.Tube(new[] { new Vector3(0f, -0.22f, 0f), new Vector3(0f, 0.32f, 0f) }, 0.015f, Color.white);
            builder.Tube(new[] { new Vector3(0f, 0f, -0.22f), new Vector3(0f, 0f, 0.22f) }, 0.015f, Color.white);
            builder.Tube(new[] { new Vector3(-0.06f, 0.32f, 0f), new Vector3(0.06f, 0.32f, 0f) }, 0.015f, Color.white);
            builder.Box(Vector3.zero, Vector3.one * 0.05f, Color.white);
            marker = builder.Mesh("Cross");

            builder = new Builder();
            builder.Tube(new[] { Vector3.zero, Vector3.forward }, 0.007f, Color.white);
            tether = builder.Mesh("Tether");

            globeMaterial = CreateMaterial();
            markerMaterial = CreateMaterial();
            tetherMaterial = CreateMaterial();
        }

        // The deadzone: a small globe of its own around the center, rebuilt when the deadzone or full deflection changes.
        private static void CreateDeadzone()
        {
            deadzoneShare = VrControls.DeadzoneShare;
            var builder = new Builder();
            builder.Ring(Vector3.right, Vector3.up, deadzoneShare, 0.004f, DeadzoneColor);
            builder.Ring(Vector3.right, Vector3.forward, deadzoneShare, 0.004f, DeadzoneColor);
            builder.Ring(Vector3.up, Vector3.forward, deadzoneShare, 0.004f, DeadzoneColor);
            if (deadzone != null) Object.Destroy(deadzone);
            deadzone = builder.Mesh("Deadzone");
        }

        // Unlit with vertex colors, drawn over the world like the visor, but culling back faces so the
        // cross's center looks solid. Hidden/Internal-Colored offers that; UI/Default (no culling) is the fallback.
        private static Material CreateMaterial()
        {
            var shader = Shader.Find("Hidden/Internal-Colored");
            if (shader == null)
            {
                var material = new Material(Shader.Find("UI/Default"));
                material.SetInt("unity_GUIZTestMode", (int)CompareFunction.Always);
                return material;
            }
            var colored = new Material(shader) { hideFlags = HideFlags.HideAndDontSave };
            colored.SetInt("_SrcBlend", (int)BlendMode.SrcAlpha);
            colored.SetInt("_DstBlend", (int)BlendMode.OneMinusSrcAlpha);
            colored.SetInt("_Cull", (int)CullMode.Back);
            colored.SetInt("_ZWrite", 0);
            colored.SetInt("_ZTest", (int)CompareFunction.Always);
            return colored;
        }

        // Collects triangles with vertex colors. Each triangle is turned to face away from the point it is given, so the
        // winding never has to be worked out by hand.
        private class Builder
        {
            private readonly List<Vector3> vertices = new List<Vector3>();
            private readonly List<Color> colors = new List<Color>();
            private readonly List<int> triangles = new List<int>();

            private static readonly Vector3 Light = new Vector3(0.3f, 0.8f, -0.5f).normalized;

            public Mesh Mesh(string name)
            {
                var mesh = new Mesh { name = "ShipbreakerVR Flight " + name };
                mesh.SetVertices(vertices);
                mesh.SetColors(colors);
                mesh.SetTriangles(triangles, 0);
                mesh.RecalculateBounds();
                return mesh;
            }

            // Two triangles a-b-c and a-c-d, facing away from inside; lit: shaded by direction.
            private void Quad(Vector3 a, Vector3 b, Vector3 c, Vector3 d, Vector3 inside, Color color, bool lit)
            {
                var normal = Vector3.Cross(b - a, c - a);
                // Unity draws a triangle's front where Cross(b - a, c - a) points.
                if (Vector3.Dot(normal, (a + b + c + d) * 0.25f - inside) < 0f)
                {
                    var t = b; b = d; d = t;
                    normal = -normal;
                }
                // A little shading by direction, so the boxes read as solid.
                var shade = lit ? 0.6f + 0.4f * Mathf.Max(0f, Vector3.Dot(normal.normalized, Light)) : 1f;
                var shaded = new Color(color.r * shade, color.g * shade, color.b * shade, color.a);
                var start = vertices.Count;
                vertices.Add(a); vertices.Add(b); vertices.Add(c); vertices.Add(d);
                for (var i = 0; i < 4; i++) colors.Add(shaded);
                triangles.Add(start); triangles.Add(start + 1); triangles.Add(start + 2);
                triangles.Add(start); triangles.Add(start + 2); triangles.Add(start + 3);
            }

            public void Box(Vector3 center, Vector3 size, Color color)
            {
                var h = size * 0.5f;
                Vector3 P(float x, float y, float z) => center + new Vector3(x * h.x, y * h.y, z * h.z);
                Quad(P(-1, -1, -1), P(1, -1, -1), P(1, 1, -1), P(-1, 1, -1), center, color, true);
                Quad(P(-1, -1, 1), P(1, -1, 1), P(1, 1, 1), P(-1, 1, 1), center, color, true);
                Quad(P(-1, -1, -1), P(-1, 1, -1), P(-1, 1, 1), P(-1, -1, 1), center, color, true);
                Quad(P(1, -1, -1), P(1, 1, -1), P(1, 1, 1), P(1, -1, 1), center, color, true);
                Quad(P(-1, -1, -1), P(1, -1, -1), P(1, -1, 1), P(-1, -1, 1), center, color, true);
                Quad(P(-1, 1, -1), P(1, 1, -1), P(1, 1, 1), P(-1, 1, 1), center, color, true);
            }

            // A circle of the given radius in the plane spanned by u and v.
            public void Ring(Vector3 u, Vector3 v, float radius, float thickness, Color color)
            {
                const int segments = 64;
                var points = new Vector3[segments + 1];
                for (var i = 0; i <= segments; i++)
                {
                    var angle = 2f * Mathf.PI * i / segments;
                    points[i] = radius * (Mathf.Cos(angle) * u + Mathf.Sin(angle) * v);
                }
                Tube(points, thickness, color);
            }

            // A thin square tube along the points, unshaded so lines keep one color.
            public void Tube(Vector3[] points, float thickness, Color color)
            {
                const int sides = 4;
                for (var i = 0; i + 1 < points.Length; i++)
                {
                    var a = points[i];
                    var b = points[i + 1];
                    var along = (b - a).normalized;
                    var side = Vector3.Cross(along, Mathf.Abs(along.y) < 0.9f ? Vector3.up : Vector3.right).normalized;
                    var up = Vector3.Cross(along, side);
                    var mid = (a + b) * 0.5f;
                    for (var s = 0; s < sides; s++)
                    {
                        var a0 = 2f * Mathf.PI * (s + 0.5f) / sides;
                        var a1 = 2f * Mathf.PI * (s + 1.5f) / sides;
                        var o0 = thickness * (Mathf.Cos(a0) * side + Mathf.Sin(a0) * up);
                        var o1 = thickness * (Mathf.Cos(a1) * side + Mathf.Sin(a1) * up);
                        Quad(a + o0, b + o0, b + o1, a + o1, mid, color, false);
                    }
                }
            }
        }
    }
}
