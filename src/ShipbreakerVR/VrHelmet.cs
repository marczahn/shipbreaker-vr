using BBI.Unity.Game;
using HarmonyLib;
using UnityEngine;
using UnityEngine.Rendering;

namespace ShipbreakerVR
{
    // The game's helmet is attached to the body. In VR the head moves inside it and looks out of it.
    // Mode "Kopf" (head): the helmet follows the VR head, with the same pose relative to the head as to the game camera in-game.
    // Mode "Aus" (off): the helmet model is hidden (light and warning lamp remain). Mode "Spiel" (game): change nothing.
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
                // Remember the helmet pose relative to the game camera while the game still positions it itself.
                relativePosition = cam.InverseTransformPoint(container.position);
                relativeRotation = Quaternion.Inverse(cam.rotation) * container.rotation;
                Plugin.Log.LogInfo($"Helmet found: '{Path(container)}', relative to camera {relativePosition} / {relativeRotation.eulerAngles}");
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

        // Once more right before the VR camera renders, so the helmet has the latest head pose.
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
