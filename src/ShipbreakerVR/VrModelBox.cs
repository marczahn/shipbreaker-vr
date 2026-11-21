using UnityEngine;

namespace ShipbreakerVR
{
    // A drawn thing's box in another transform's axes (the Hab's room), from its model's own points rather than its world
    // box, which is too large where the room stands turned. The Hab's furniture is drawn statically batched (Raik
    // 2026-11-15: two of the three AI nodes far outside the room): its MeshFilter then holds the scene's combined mesh,
    // whose bounds are those of the whole batch; its parts there gave boxes beside the crates seen as well. For those the
    // renderer's own world box counts, turned back into the room's axes.
    internal static class VrModelBox
    {
        // Grows box by the renderer's model, in 'into's axes. False where it has none.
        public static bool Add(Renderer renderer, Transform into, ref Bounds box, ref bool any)
        {
            var filter = renderer != null ? renderer.GetComponent<MeshFilter>() : null;
            var mesh = filter != null ? filter.sharedMesh : null;
            if (mesh == null) return false;
            if (!renderer.isPartOfStaticBatch)
            {
                var b = mesh.bounds;
                for (var c = 0; c < 8; c++) Grow(into.InverseTransformPoint(filter.transform.TransformPoint(Corner(b, c))), ref box, ref any);
                return true;
            }
            // Statically batched (Raik 2026-11-15, pictures: the combined mesh's parts gave boxes far off the crates seen):
            // the renderer's own world box is right for it alone. Its middle is the model's middle; its size, where the
            // thing stands square in the room, follows from the room's turn (Solve).
            var w = renderer.bounds;
            var h = Solve(into, w.extents);
            var center = into.InverseTransformPoint(w.center);
            var scale = into.lossyScale;
            var half = new Vector3(h.x / Mathf.Abs(scale.x), h.y / Mathf.Abs(scale.y), h.z / Mathf.Abs(scale.z));
            Grow(center - half, ref box, ref any);
            Grow(center + half, ref box, ref any);
            return true;
        }

        // The half sizes along the room's own axes of a box standing square in the room, from its world box's half sizes:
        // each world half size is the sum of the room axes' shares of it. Where the room's turn leaves that open (turned
        // by about 45°), or a size comes out below nothing, the world box's own half sizes, larger but around it.
        private static Vector3 Solve(Transform room, Vector3 world)
        {
            var axes = new[] { room.right, room.up, room.forward };
            var m = new float[3, 3];
            for (var j = 0; j < 3; j++)
                for (var i = 0; i < 3; i++)
                    m[j, i] = Mathf.Abs(axes[i][j]);
            var det = Det(m);
            if (Mathf.Abs(det) < 0.05f) return world;
            var h = new Vector3();
            for (var i = 0; i < 3; i++)
            {
                var mi = (float[,])m.Clone();
                for (var j = 0; j < 3; j++) mi[j, i] = world[j];
                h[i] = Det(mi) / det;
            }
            for (var i = 0; i < 3; i++) if (h[i] < -0.01f) return world;
            return Vector3.Max(h, Vector3.zero);
        }

        private static float Det(float[,] m) =>
            m[0, 0] * (m[1, 1] * m[2, 2] - m[1, 2] * m[2, 1])
            - m[0, 1] * (m[1, 0] * m[2, 2] - m[1, 2] * m[2, 0])
            + m[0, 2] * (m[1, 0] * m[2, 1] - m[1, 1] * m[2, 0]);

        private static Vector3 Corner(Bounds b, int c) =>
            new Vector3((c & 1) == 0 ? b.min.x : b.max.x, (c & 2) == 0 ? b.min.y : b.max.y, (c & 4) == 0 ? b.min.z : b.max.z);

        private static void Grow(Vector3 p, ref Bounds box, ref bool any)
        {
            if (!any) box = new Bounds(p, Vector3.zero);
            else box.Encapsulate(p);
            any = true;
        }
    }
}
