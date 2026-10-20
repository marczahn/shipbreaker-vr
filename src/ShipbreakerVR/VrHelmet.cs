using BBI.Unity.Game;
using HarmonyLib;
using UnityEngine;
using UnityEngine.Rendering;

namespace ShipbreakerVR
{
    // Der Helm des Spiels hängt an der Figur. In VR bewegt sich der Kopf darin und schaut aus ihm heraus.
    // Modus "Kopf": Der Helm folgt dem VR-Kopf, mit derselben Lage relativ zum Kopf wie im Spiel zur Spielkamera.
    // Modus "Aus": Das Helm-Modell wird ausgeblendet (Licht und Warnlampe bleiben). Modus "Spiel": nichts ändern.
    internal class VrHelmet : MonoBehaviour
    {
        public enum Mode { Kopf, Aus, Spiel }

        private static readonly AccessTools.FieldRef<HelmetController, GameObject> ContainerRef =
            AccessTools.FieldRefAccess<HelmetController, GameObject>("m_HelmetContainer");

        private VrCamera vr;
        private HelmetController controller;
        private Transform container;
        private Vector3 relativePosition;
        private Quaternion relativeRotation;
        private Vector3 originalLocalPosition;
        private Quaternion originalLocalRotation;
        private bool moved;
        private float nextSearch;
        private bool hidden;

        public static void Attach(VrCamera vr) => vr.gameObject.AddComponent<VrHelmet>().vr = vr;

        private void OnEnable() => RenderPipelineManager.beginCameraRendering += OnBeginCamera;

        private void OnDisable() => RenderPipelineManager.beginCameraRendering -= OnBeginCamera;

        private void LateUpdate()
        {
            if (controller == null)
            {
                container = null;
                if (Time.unscaledTime < nextSearch) return;
                nextSearch = Time.unscaledTime + 2f;
                controller = FindObjectOfType<HelmetController>();
                if (controller == null) return;
                var go = ContainerRef(controller);
                if (go == null) return;
                container = go.transform;
                originalLocalPosition = container.localPosition;
                originalLocalRotation = container.localRotation;
                moved = false;
                var cam = LynxCameraController.MainCameraTransform;
                if (cam == null) return;
                // Lage des Helms relativ zur Spielkamera merken, solange das Spiel ihn noch selbst setzt.
                relativePosition = cam.InverseTransformPoint(container.position);
                relativeRotation = Quaternion.Inverse(cam.rotation) * container.rotation;
                Plugin.Log.LogInfo($"Helm gefunden: '{Path(container)}', relativ zur Kamera {relativePosition} / {relativeRotation.eulerAngles}");
            }

            var mode = VrXr.IsRunning ? Plugin.Helmet.Value : Mode.Spiel;
            SetHidden(mode == Mode.Aus);
            if (mode == Mode.Kopf) Follow();
            else if (moved && container != null)
            {
                moved = false;
                container.localPosition = originalLocalPosition;
                container.localRotation = originalLocalRotation;
            }
        }

        // Kurz vor dem Rendern der VR-Kamera noch einmal, damit der Helm die neueste Kopfhaltung hat.
        private void OnBeginCamera(ScriptableRenderContext context, Camera camera)
        {
            if (vr != null && camera == vr.Camera && VrXr.IsRunning && Plugin.Helmet.Value == Mode.Kopf) Follow();
        }

        private void Follow()
        {
            if (container == null) return;
            var head = vr.transform;
            moved = true;
            container.SetPositionAndRotation(head.TransformPoint(relativePosition), head.rotation * relativeRotation);
        }

        private void SetHidden(bool hide)
        {
            if (hidden == hide || controller == null || controller.CurrentHelment == null) return;
            hidden = hide;
            var mesh = controller.CurrentHelment.MeshGameObject;
            if (mesh != null) mesh.SetActive(!hide);
        }

        private static string Path(Transform t) => t.parent == null ? t.name : Path(t.parent) + "/" + t.name;
    }
}
