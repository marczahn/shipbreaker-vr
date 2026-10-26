using BBI.Unity.Game;
using UnityEngine;

namespace ShipbreakerVR
{
    // Logs the game's interactions (InteractionEvent) and hand grabs (GrabEvent), to find out why the airlock controls
    // do not work in VR: what the game starts, whether the interact press reaches the object, and whether its progress
    // runs. Through the game's own event system, so nothing is patched.
    internal static class VrInteractionTrace
    {
        private const int MaxLogs = 300;

        private static bool attached;
        private static int logs;
        private static float lastProgressLog;

        // From VrCamera.Update.
        public static void Attach()
        {
            if (attached || Main.EventSystem == null) return;
            attached = true;
            Main.EventSystem.AddHandler<InteractionEvent>(OnInteraction);
            Main.EventSystem.AddHandler<GrabEvent>(OnGrab);
            Plugin.Log.LogInfo("Interaction trace: listening");
        }

        public static void Detach()
        {
            if (!attached) return;
            attached = false;
            Main.EventSystem.RemoveHandler<InteractionEvent>(OnInteraction);
            Main.EventSystem.RemoveHandler<GrabEvent>(OnGrab);
        }

        private static void OnInteraction(InteractionEvent ev)
        {
            if (logs >= MaxLogs) return;
            var interactable = ev.InteractableObject;
            // Progress comes every frame while held: twice a second is enough.
            if (ev.Interaction == InteractionEvent.InteractionType.ProgressChange)
            {
                if (Time.unscaledTime - lastProgressLog < 0.5f) return;
                lastProgressLog = Time.unscaledTime;
            }
            logs++;
            if (interactable == null)
            {
                Plugin.Log.LogInfo($"Interaction trace: {ev.Interaction} (nothing)");
                return;
            }
            var data = interactable.Asset != null ? interactable.Asset.Data : default;
            Plugin.Log.LogInfo($"Interaction trace: {ev.Interaction} '{interactable.name}' < '{(interactable.transform.parent != null ? interactable.transform.parent.name : "-")}'"
                + (interactable.Asset != null ? $" ({data.ActivationType}, {data.ProgressType} {data.InteractionTime:0.0} s, wait for release {data.WaitForRelease})" : " (no asset)")
                + $", hand target {(interactable.HandTarget != null ? $"'{interactable.HandTarget.name}'" : "none")}, pressed {interactable.IsPressed},"
                + $" progress {interactable.Progress:0.00}, interactable {interactable.IsInteractable}, conditions {interactable.CheckConditions()}");
        }

        private static void OnGrab(GrabEvent ev)
        {
            if (logs >= MaxLogs) return;
            logs++;
            Plugin.Log.LogInfo($"Interaction trace: grab {ev.State} '{(ev.GrabbedHierarchyObject != null ? ev.GrabbedHierarchyObject.name : "-")}'"
                + $" at '{(ev.GrabbedTransform != null ? ev.GrabbedTransform.name : "-")}'");
        }
    }
}
