using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Rendering;

namespace ShipbreakerVR
{
    // A small globe in the lower left of the visor that shows where the left controller is while flying (VrControls):
    // three axes through its center, and a stylized controller that moves away from the center as the hand moves away
    // from the reference pose and tilts as the hand tilts. The globe's surface is full thrust, the small inner globe the
    // deadzone. The globe is aligned with the playspace, the frame the hand offset is measured in, so its axes are the
    // directions the hand thrusts in. When the trigger is released the controller glides back to the center.
    // Drawn in 3D for each eye after the visor (VrUi.Draw), from meshes built here, since the plugin brings no shaders.
    internal static class VrFlightGlobe
    {
        public enum Mode { Aus, Immer, BeimFliegen }

        private const float ReturnSpeed = 12f;
        private const float FadeSpeed = 6f;

        private static readonly Color RingColor = new Color(0.6f, 0.85f, 1f, 0.3f);
        private static readonly Color DeadzoneColor = new Color(0.6f, 0.85f, 1f, 0.45f);
        private static readonly Color RightColor = new Color(1f, 0.4f, 0.35f, 0.9f);
        private static readonly Color UpColor = new Color(0.45f, 1f, 0.45f, 0.9f);
        private static readonly Color ForwardColor = new Color(0.4f, 0.65f, 1f, 0.9f);
        private static readonly Color BodyColor = new Color(0.75f, 0.78f, 0.82f, 1f);
        private static readonly Color AccentColor = new Color(0.35f, 0.85f, 1f, 1f);
        private static readonly Color StickColor = new Color(1f, 1f, 1f, 1f);
        private static readonly Color IdleTether = new Color(1f, 1f, 1f, 0.35f);
        private static readonly Color FullTether = new Color(1f, 0.65f, 0.2f, 0.95f);

        private static Mesh globe;
        private static Mesh controller;
        private static Mesh tether;
        private static Material globeMaterial;
        private static Material controllerMaterial;
        private static Material tetherMaterial;
        private static bool logged;

        // What is shown, easing back to the center after the trigger is released; offset in globe radii.
        private static Vector3 shownOffset;
        private static Quaternion shownTurn = Quaternion.identity;
        private static float alpha;
        private static int updatedFrame = -1;

        // Draws the globe into the current eye; view and projection are already set. visor: the visor quad's matrix,
        // playspace: the rig's rotation in the world.
        public static void Draw(CommandBuffer cmd, Matrix4x4 visor, Quaternion playspace)
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
            var frame = Matrix4x4.TRS(center, playspace, Vector3.one * radius);

            globeMaterial.color = new Color(1f, 1f, 1f, alpha);
            cmd.DrawMesh(globe, frame, globeMaterial, 0, 0);

            var length = shownOffset.magnitude;
            if (length > 0.01f)
            {
                var strength = Mathf.Clamp01((length - VrControls.DeadzoneShare) / (1f - VrControls.DeadzoneShare));
                var color = strength > 0f ? Color.Lerp(IdleTether, FullTether, strength) : IdleTether;
                color.a *= alpha;
                tetherMaterial.color = color;
                var along = Matrix4x4.TRS(Vector3.zero, Quaternion.LookRotation(shownOffset), new Vector3(1f, 1f, length));
                cmd.DrawMesh(tether, frame * along, tetherMaterial, 0, 0);
            }

            controllerMaterial.color = new Color(1f, 1f, 1f, alpha * (VrControls.Holding ? 1f : 0.55f));
            cmd.DrawMesh(controller, frame * Matrix4x4.TRS(shownOffset, shownTurn, Vector3.one), controllerMaterial, 0, 0);

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
            // BeimFliegen: only while the trigger is held, and until the controller is back in the center.
            var visible = mode == Mode.Immer || VrControls.Holding || shownOffset.magnitude > 0.02f || Quaternion.Angle(shownTurn, Quaternion.identity) > 2f;
            alpha = Mathf.MoveTowards(alpha, visible ? 1f : 0f, FadeSpeed * dt);
        }

        private static void Create()
        {
            var deadzone = VrControls.DeadzoneShare;
            var builder = new Builder();
            builder.Ring(Vector3.right, Vector3.up, 1f, 0.008f, RingColor);
            builder.Ring(Vector3.right, Vector3.forward, 1f, 0.008f, RingColor);
            builder.Ring(Vector3.up, Vector3.forward, 1f, 0.008f, RingColor);
            builder.Ring(Vector3.right, Vector3.up, deadzone, 0.006f, DeadzoneColor);
            builder.Ring(Vector3.right, Vector3.forward, deadzone, 0.006f, DeadzoneColor);
            builder.Ring(Vector3.up, Vector3.forward, deadzone, 0.006f, DeadzoneColor);
            Axis(builder, Vector3.right, RightColor);
            Axis(builder, Vector3.up, UpColor);
            Axis(builder, Vector3.forward, ForwardColor);
            globe = builder.Mesh("Globe");

            // Pointing forward (+z) with the thumbstick on top, like the controller held level; in globe radii.
            builder = new Builder();
            builder.Box(new Vector3(0f, 0f, -0.02f), new Vector3(0.13f, 0.11f, 0.34f), BodyColor);
            builder.Box(new Vector3(0f, 0.01f, 0.18f), new Vector3(0.1f, 0.08f, 0.06f), AccentColor);
            builder.Box(new Vector3(0f, 0.075f, -0.03f), new Vector3(0.05f, 0.04f, 0.05f), StickColor);
            builder.Box(new Vector3(0f, -0.07f, 0.09f), new Vector3(0.05f, 0.04f, 0.06f), AccentColor);
            controller = builder.Mesh("Controller");

            builder = new Builder();
            builder.Tube(new[] { Vector3.zero, Vector3.forward }, 0.012f, Color.white);
            tether = builder.Mesh("Tether");

            globeMaterial = CreateMaterial();
            controllerMaterial = CreateMaterial();
            tetherMaterial = CreateMaterial();
        }

        // An axis through the globe, a little longer than it, with a small cube at its positive end.
        private static void Axis(Builder builder, Vector3 direction, Color color)
        {
            builder.Tube(new[] { -1.1f * direction, 1.1f * direction }, 0.012f, color);
            builder.Box(1.1f * direction, Vector3.one * 0.06f, color);
        }

        // Unlit with vertex colors, drawn over the world like the visor, but culling back faces so the controller's
        // boxes look solid. Hidden/Internal-Colored offers that; UI/Default (no culling) is the fallback.
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
