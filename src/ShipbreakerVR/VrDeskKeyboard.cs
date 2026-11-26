using System.Collections.Generic;
using TMPro;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.UI;

namespace ShipbreakerVR
{
    // A keyboard to point at, for the account name of a new career's contract (Raik, 2026-11-23: "a keyboard shown
    // below, over the keys of the desk; while it is there the controllers are seen, with laser pointers. A stopgap for
    // now"). While the contract's name field is selected, a board of keys lies below the terminal's screen
    // (VrContractDesk), and each controller shows as a pen with a beam where it points. A key pointed at lights up,
    // the trigger of that hand types it into the field; OK ends it and selects what comes after the field.
    // Board and beams are drawn as world-space UI on the contract's own layer, as the contract is: canvases with plain
    // images and the field's own font, so they need no shader of their own. The pens (Raik: "do it with the pens, any
    // dark grey material") are a mesh made here, a round body with a pointed tip, in a plain dark grey material of the
    // shader the desk's parts are drawn with; without that shader a controller shows as a bar of the same UI.
    // The triggers do nothing else in the menu meanwhile (VrMenuInput).
    internal static class VrDeskKeyboard
    {
        static VrDeskKeyboard() => VrSession.OnUnload("desk keyboard", Release);

        // The contract's layer, which the front end's main camera draws.
        private const int Layer = 10;
        // The board in millimeters (one unit of its canvas is a millimeter): its size, the rows of keys, the gap
        // between keys.
        private const float Width = 540f;
        private const float Height = 215f;
        private const float Gap = 4f;
        private static readonly string[] Rows = { "1234567890", "QWERTZUIOP", "ASDFGHJKL-", "YXCVBNM_.<" };
        private const string Delete = "<";
        // It lies below the screen: its upper edge this far below the screen's lower edge and this far towards the
        // viewer, rising away from them by Slope degrees, lifted off the desk's own keys by Lift (meters, degrees).
        private const float Below = 0.03f;
        private const float Nearer = 0.03f;
        private const float Slope = 25f;
        // Raik (2026-11-26, a picture of it floating well above the desk's keys): a few centimeters lower, just above
        // them, which is also kinder to the arms; from 3 cm up to 1 cm down, after trying it 3 cm more.
        private const float Lift = -0.04f;
        // A beam is this long when it meets nothing, and this thick; the bar that stands for a controller.
        private const float BeamFree = 1.2f;
        private const float BeamThick = 4f;
        private const float BarLong = 110f;
        private const float BarThick = 28f;
        // The pen, in meters along where the controller points: this far back from the controller's place and this far
        // ahead of it, the last PenTip of it tapering, this thick; round by PenSides.
        private const float PenBack = 0.11f;
        private const float PenAhead = 0.035f;
        private const float PenTip = 0.03f;
        private const float PenRadius = 0.0065f;
        private const int PenSides = 14;
        private static readonly Color PenColor = new Color(0.11f, 0.115f, 0.125f, 1f);
        private static readonly Color BoardColor = new Color(0.03f, 0.04f, 0.05f, 0.92f);
        private static readonly Color KeyColor = new Color(0.16f, 0.18f, 0.2f, 1f);
        private static readonly Color KeyLit = new Color(0.95f, 0.75f, 0.1f, 1f);
        private static readonly Color BeamColor = new Color(0.2f, 0.8f, 1f, 0.9f);
        private static readonly Color BarColor = new Color(0.75f, 0.78f, 0.8f, 1f);

        private class Key
        {
            public string Text;
            public Rect Area;
            public Image Face;
        }

        private class Pointer
        {
            public VrInput.Hand Hand;
            public RectTransform Beam;
            public RectTransform Bar;
            public Transform Pen;
            public Key At;
        }

        // The triggers type while the board is shown (VrMenuInput leaves them out of the menu then).
        public static bool Active { get; private set; }

        private static GameObject board;
        private static readonly List<Key> Keys = new List<Key>();
        private static readonly Pointer[] Pointers = { new Pointer { Hand = VrInput.Left }, new Pointer { Hand = VrInput.Right } };
        private static TMP_InputField doneWith;
        private static Mesh penMesh;
        private static Material penMaterial;
        private static int logs;

        // From VrContractDesk, every frame while its desk is shown: that field (or none), the terminal's screen in the
        // world and the desk's scale.
        public static void Update(TMP_InputField field, Vector3 screenCenter, Quaternion screenTurn, Vector2 screenSize, float scale)
        {
            var system = EventSystem.current;
            var selected = system != null ? system.currentSelectedGameObject : null;
            var onField = field != null && field.gameObject.activeInHierarchy && (selected == field.gameObject || field.isFocused);
            // Ended with OK: not again until the field has been left.
            if (!onField) doneWith = null;
            var wanted = onField && doneWith != field && VrCamera.Instance != null;
            if (!wanted)
            {
                Hide();
                return;
            }
            if (board == null) Build(field);
            if (!Active)
            {
                Active = true;
                board.SetActive(true);
                foreach (var pointer in Pointers) Show(pointer, true);
                if (logs++ < 20) Plugin.Log.LogInfo("Desk keyboard: shown for the contract's name field");
            }
            Place(screenCenter, screenTurn, screenSize, scale);
            foreach (var pointer in Pointers) Point(pointer, field, scale);
        }

        public static void Hide()
        {
            if (!Active) return;
            Active = false;
            if (board != null) board.SetActive(false);
            foreach (var pointer in Pointers) Show(pointer, false);
        }

        // A controller's pen (or bar) and beam on or off.
        private static void Show(Pointer pointer, bool on)
        {
            if (pointer.Beam != null && pointer.Beam.parent.gameObject.activeSelf != on) pointer.Beam.parent.gameObject.SetActive(on);
            if (pointer.Pen != null && pointer.Pen.gameObject.activeSelf != on) pointer.Pen.gameObject.SetActive(on);
        }

        // Below the screen, across its width, sloping up away from the viewer.
        private static void Place(Vector3 screenCenter, Quaternion screenTurn, Vector2 screenSize, float scale)
        {
            var right = screenTurn * Vector3.right;
            var screenUp = screenTurn * Vector3.up;
            var away = Vector3.ProjectOnPlane(screenTurn * Vector3.forward, Vector3.up);
            if (away.sqrMagnitude < 1e-4f) away = Vector3.Cross(right, Vector3.up);
            away.Normalize();
            var up = (away * Mathf.Cos(Slope * Mathf.Deg2Rad) + Vector3.up * Mathf.Sin(Slope * Mathf.Deg2Rad)).normalized;
            var into = Vector3.Cross(right, up);
            var unit = 0.001f * scale;
            var top = screenCenter - screenUp * (screenSize.y * 0.5f + Below * scale) - away * (Nearer * scale) + Vector3.up * (Lift * scale);
            var t = board.transform;
            t.rotation = Quaternion.LookRotation(into, up);
            t.position = top - up * (Height * 0.5f * unit);
            t.localScale = Vector3.one * unit;
        }

        // A controller's bar and beam, the key it points at, and its trigger.
        private static void Point(Pointer pointer, TMP_InputField field, float scale)
        {
            var vr = VrCamera.Instance;
            var rig = vr.transform.parent;
            if (rig == null || !pointer.Hand.Tracked || !pointer.Hand.TryGetAimPose(out var localAt, out var localTurn, out _))
            {
                Show(pointer, false);
                Light(pointer, null);
                return;
            }
            Show(pointer, true);
            var at0 = rig.TransformPoint(localAt);
            var way = rig.rotation * localTurn * Vector3.forward;
            // The pen where the controller is, pointing as it does; the beam starts at its tip.
            var from = at0;
            if (pointer.Pen != null)
            {
                pointer.Pen.position = at0;
                pointer.Pen.rotation = rig.rotation * localTurn;
                pointer.Pen.localScale = Vector3.one * scale;
                from = at0 + way * (PenAhead * scale);
            }
            // Where the beam meets the board.
            var plane = board.transform;
            var normal = plane.forward;
            var facing = Vector3.Dot(way, normal);
            var length = BeamFree * scale;
            Key at = null;
            if (Mathf.Abs(facing) > 1e-4f)
            {
                var far = Vector3.Dot(plane.position - from, normal) / facing;
                if (far > 0f && far < 3f * scale)
                {
                    Vector2 on = plane.InverseTransformPoint(from + way * far);
                    foreach (var key in Keys)
                        if (key.Area.Contains(on)) at = key;
                    if (Mathf.Abs(on.x) <= Width * 0.5f && Mathf.Abs(on.y) <= Height * 0.5f) length = far;
                }
            }
            Light(pointer, at);
            // The strips lie along the beam, their faces to the head.
            var toHead = Vector3.ProjectOnPlane(vr.transform.position - from, way);
            var face = toHead.sqrMagnitude > 1e-6f ? -toHead.normalized : Vector3.Cross(way, Vector3.up).normalized;
            var turn = Quaternion.LookRotation(face, Vector3.Cross(face, way));
            var unit = 0.001f * scale;
            var strips = pointer.Beam.parent;
            strips.position = from;
            strips.rotation = turn;
            strips.localScale = Vector3.one * unit;
            pointer.Beam.sizeDelta = new Vector2(length / unit, BeamThick);
            if (at == null || !pointer.Hand.Trigger.Pressed) return;
            pointer.Hand.Buzz(0.4f, 0.03f);
            Type(field, at.Text);
        }

        private static void Light(Pointer pointer, Key at)
        {
            if (pointer.At == at) return;
            var other = Pointers[0] == pointer ? Pointers[1] : Pointers[0];
            if (pointer.At != null && other.At != pointer.At) pointer.At.Face.color = KeyColor;
            pointer.At = at;
            if (at != null) at.Face.color = KeyLit;
        }

        private static void Type(TMP_InputField field, string key)
        {
            var text = field.text ?? "";
            if (key == "OK")
            {
                doneWith = field;
                if (field.isFocused) field.DeactivateInputField();
                else field.onEndEdit.Invoke(text);
                var next = field.FindSelectableOnDown() ?? field.FindSelectableOnRight();
                var system = EventSystem.current;
                if (next != null && system != null) system.SetSelectedGameObject(next.gameObject);
                if (logs++ < 20) Plugin.Log.LogInfo($"Desk keyboard: done, {text.Length} character(s) in the name; next is {(next != null ? $"'{next.name}'" : "nothing")}");
                return;
            }
            if (key == Delete)
            {
                if (text.Length > 0) field.text = text.Substring(0, text.Length - 1);
                return;
            }
            if (field.characterLimit > 0 && text.Length >= field.characterLimit) return;
            // Names: a capital at the start and after a space, small letters otherwise.
            var letter = key == "SPACE" ? " " : key;
            if (text.Length > 0 && text[text.Length - 1] != ' ') letter = letter.ToLowerInvariant();
            field.text = text + letter;
        }

        private static void Build(TMP_InputField field)
        {
            var font = field.textComponent != null ? field.textComponent.font : null;
            board = Canvas("ShipbreakerVR desk keyboard", new Vector2(Width, Height));
            Strip(board.transform, "Board", new Rect(-Width * 0.5f, -Height * 0.5f, Width, Height), BoardColor);
            Keys.Clear();
            var rows = Rows.Length + 1;
            var tall = (Height - Gap * (rows + 1)) / rows;
            for (var row = 0; row < Rows.Length; row++)
            {
                var wide = (Width - Gap * (Rows[row].Length + 1)) / Rows[row].Length;
                for (var i = 0; i < Rows[row].Length; i++)
                    Add(Rows[row][i].ToString(), new Rect(-Width * 0.5f + Gap + i * (wide + Gap), Height * 0.5f - (row + 1) * (tall + Gap), wide, tall), font);
            }
            // The last row: a wide space bar and OK.
            var bottom = Height * 0.5f - rows * (tall + Gap);
            var space = (Width - 3f * Gap) * 0.72f;
            Add("SPACE", new Rect(-Width * 0.5f + Gap, bottom, space, tall), font);
            Add("OK", new Rect(-Width * 0.5f + 2f * Gap + space, bottom, Width - 3f * Gap - space, tall), font);
            foreach (var pointer in Pointers)
            {
                var strips = Canvas($"ShipbreakerVR desk pointer {pointer.Hand.Name}", new Vector2(10f, 10f));
                // The controller: a bar round where it is; the beam from there on, along x.
                pointer.Bar = Strip(strips.transform, "Controller", new Rect(-BarLong * 0.6f, -BarThick * 0.5f, BarLong, BarThick), BarColor).rectTransform;
                var beam = Strip(strips.transform, "Beam", new Rect(0f, -BeamThick * 0.5f, 100f, BeamThick), BeamColor).rectTransform;
                beam.pivot = new Vector2(0f, 0.5f);
                beam.anchoredPosition = Vector2.zero;
                pointer.Beam = beam;
                strips.SetActive(false);
            }
            var shader = VrContractDesk.DeskShader;
            if (shader != null)
            {
                penMesh = PenMesh();
                penMaterial = new Material(shader) { name = "ShipbreakerVR desk pen", hideFlags = HideFlags.HideAndDontSave };
                foreach (var color in new[] { "_BaseColor", "_Color" })
                    if (penMaterial.HasProperty(color)) penMaterial.SetColor(color, PenColor);
                if (penMaterial.HasProperty("_Smoothness")) penMaterial.SetFloat("_Smoothness", 0.35f);
                if (penMaterial.HasProperty("_Metallic")) penMaterial.SetFloat("_Metallic", 0.1f);
                foreach (var pointer in Pointers)
                {
                    var pen = new GameObject($"ShipbreakerVR desk pen {pointer.Hand.Name}");
                    pen.AddComponent<MeshFilter>().sharedMesh = penMesh;
                    var drawn = pen.AddComponent<MeshRenderer>();
                    drawn.sharedMaterial = penMaterial;
                    drawn.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
                    pen.SetActive(false);
                    pointer.Pen = pen.transform;
                    pointer.Bar.gameObject.SetActive(false);
                }
            }
            board.SetActive(false);
            Plugin.Log.LogInfo($"Desk keyboard: built, {Keys.Count} keys, font {(font != null ? $"'{font.name}'" : "none")}, "
                + (shader != null ? $"pens in the shader '{shader.name}'" : "no shader of the desk's, the controllers show as bars"));
        }

        // The pen: round, from PenBack behind the controller's place to PenAhead before it along z, its last PenTip
        // tapering to a point.
        private static Mesh PenMesh()
        {
            float[] along = { -PenBack, -PenBack, PenAhead - PenTip, PenAhead };
            float[] radius = { 0.0005f, PenRadius, PenRadius, 0.0008f };
            var vertices = new List<Vector3>();
            var triangles = new List<int>();
            for (var ring = 0; ring < along.Length; ring++)
                for (var side = 0; side <= PenSides; side++)
                {
                    var angle = 2f * Mathf.PI * side / PenSides;
                    vertices.Add(new Vector3(Mathf.Cos(angle) * radius[ring], Mathf.Sin(angle) * radius[ring], along[ring]));
                }
            for (var ring = 0; ring + 1 < along.Length; ring++)
                for (var side = 0; side < PenSides; side++)
                {
                    var a = ring * (PenSides + 1) + side;
                    var b = a + 1;
                    var c = a + PenSides + 1;
                    var d = c + 1;
                    triangles.Add(a); triangles.Add(b); triangles.Add(c);
                    triangles.Add(b); triangles.Add(d); triangles.Add(c);
                }
            var mesh = new Mesh { name = "ShipbreakerVR desk pen", hideFlags = HideFlags.HideAndDontSave };
            mesh.SetVertices(vertices);
            mesh.SetTriangles(triangles, 0);
            mesh.RecalculateNormals();
            mesh.RecalculateBounds();
            return mesh;
        }

        private static void Add(string text, Rect area, TMP_FontAsset font)
        {
            var face = Strip(board.transform, "Key " + text, area, KeyColor);
            var label = new GameObject("Label") { layer = Layer };
            var rect = label.AddComponent<RectTransform>();
            rect.SetParent(face.transform, false);
            rect.anchorMin = Vector2.zero;
            rect.anchorMax = Vector2.one;
            rect.offsetMin = rect.offsetMax = Vector2.zero;
            var shown = label.AddComponent<TextMeshProUGUI>();
            if (font != null) shown.font = font;
            shown.text = text == Delete ? "DEL" : text;
            shown.fontSize = text.Length > 1 ? 15f : 22f;
            shown.alignment = TextAlignmentOptions.Center;
            shown.color = Color.white;
            shown.raycastTarget = false;
            shown.enableWordWrapping = false;
            Keys.Add(new Key { Text = text, Area = area, Face = face });
        }

        // A world-space canvas on the contract's layer.
        private static GameObject Canvas(string name, Vector2 size)
        {
            var made = new GameObject(name) { layer = Layer };
            var canvas = made.AddComponent<Canvas>();
            canvas.renderMode = RenderMode.WorldSpace;
            ((RectTransform)made.transform).sizeDelta = size;
            return made;
        }

        // A plain colored rectangle on a canvas, in its units from its middle.
        private static Image Strip(Transform canvas, string name, Rect area, Color color)
        {
            var made = new GameObject(name) { layer = Layer };
            var rect = made.AddComponent<RectTransform>();
            rect.SetParent(canvas, false);
            rect.anchorMin = rect.anchorMax = new Vector2(0.5f, 0.5f);
            rect.pivot = new Vector2(0.5f, 0.5f);
            rect.sizeDelta = area.size;
            rect.anchoredPosition = area.center;
            var image = made.AddComponent<Image>();
            image.color = color;
            image.raycastTarget = false;
            return image;
        }

        public static void Release()
        {
            Active = false;
            if (board != null) Object.Destroy(board);
            board = null;
            Keys.Clear();
            foreach (var pointer in Pointers)
            {
                if (pointer.Beam != null) Object.Destroy(pointer.Beam.parent.gameObject);
                if (pointer.Pen != null) Object.Destroy(pointer.Pen.gameObject);
                pointer.Beam = pointer.Bar = null;
                pointer.Pen = null;
                pointer.At = null;
            }
            if (penMesh != null) Object.Destroy(penMesh);
            if (penMaterial != null) Object.Destroy(penMaterial);
            penMesh = null;
            penMaterial = null;
            doneWith = null;
        }
    }
}
