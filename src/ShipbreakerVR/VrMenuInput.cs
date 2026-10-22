using System.Linq;
using BBI.Unity.Game;
using HarmonyLib;
using InControl;
using UnityEngine;
using UnityEngine.XR;
using XrDevice = UnityEngine.XR.InputDevice;

namespace ShipbreakerVR
{
    // Menu control with the right motion controller. The game's menus are fully usable with a gamepad: they read
    // InControl actions (Confirm = Action1, Decline = Action2, navigation = left stick or D-pad, tabs = bumpers,
    // scrolling = right stick, pause = Menu). So the right controller is presented to InControl as one more gamepad and
    // the game does the rest, including its gamepad button prompts.
    //   Stick: navigate. Grip + stick left/right: previous/next tab, grip + stick up/down: scroll.
    //   A or trigger: confirm. B: back. Stick press: pause menu (also in gameplay) and close it again.
    // In gameplay only the stick press counts, so the stick keeps snap turning and nothing else reaches the game.
    // The actions read only InControl's active device, and another device kept that role (see ActiveDevicePatch).
    internal class VrMenuInput : InControl.InputDevice
    {
        private const float TriggerPress = 0.5f;
        private const float TabPress = 0.6f;

        private static VrMenuInput instance;
        private static int pressLogs;

        private XrDevice right;
        private GameSession.GameState lastState;
        private bool waitForRelease;
        private string lastPressed = "";

        private VrMenuInput() : base("ShipbreakerVR right controller")
        {
            DeviceClass = InputDeviceClass.Controller;
            // The game picks its button glyphs by style; Xbox A/B sit where the Touch controller has its A/B.
            DeviceStyle = InputDeviceStyle.XboxOne;
            AddControl(InputControlType.LeftStickUp, "Left Stick Up");
            AddControl(InputControlType.LeftStickDown, "Left Stick Down");
            AddControl(InputControlType.LeftStickLeft, "Left Stick Left");
            AddControl(InputControlType.LeftStickRight, "Left Stick Right");
            AddControl(InputControlType.RightStickUp, "Right Stick Up");
            AddControl(InputControlType.RightStickDown, "Right Stick Down");
            AddControl(InputControlType.RightStickLeft, "Right Stick Left");
            AddControl(InputControlType.RightStickRight, "Right Stick Right");
            AddControl(InputControlType.DPadUp, "DPad Up");
            AddControl(InputControlType.DPadDown, "DPad Down");
            AddControl(InputControlType.DPadLeft, "DPad Left");
            AddControl(InputControlType.DPadRight, "DPad Right");
            AddControl(InputControlType.Action1, "A");
            AddControl(InputControlType.Action2, "B");
            AddControl(InputControlType.LeftBumper, "Left Bumper");
            AddControl(InputControlType.RightBumper, "Right Bumper");
            AddControl(InputControlType.Menu, "Menu");
        }

        // True while the right controller works the menus, i.e. outside gameplay. Snap turning pauses meanwhile.
        public static VrMenuInput Current => instance;

        public static bool HasInput => instance != null && instance.IsActive;

        public static bool InMenus => VrXr.IsRunning && GameSession.CurrentGameState != GameSession.GameState.Gameplay;

        // Attaches the device once InControl is set up, and again if InControl was reset. Called every frame.
        public static void EnsureAttached()
        {
            if (!InputManager.IsSetup) return;
            if (instance == null) instance = new VrMenuInput();
            if (InputManager.Devices.Contains(instance)) return;
            InputManager.AttachDevice(instance);
            Plugin.Log.LogInfo("Menu control: right controller attached to InControl as a gamepad");
            LogDevices("attached");
        }

        public static void LogDevices(string when)
        {
            var devices = InputManager.Devices.Select(d => $"'{d.Name}' ({d.DeviceClass}, {d.DeviceStyle}, last input {d.LastInputTick}{(d.Passive ? ", passive" : "")})");
            Plugin.Log.LogInfo($"Menu control, InControl devices {when} (tick {InputManager.CurrentTick}): {string.Join(", ", devices)}; active '{InputManager.ActiveDevice?.Name}'");
        }

        // Called by InControl in InputManager.Update (from LynxControls.Update), before the actions read the devices.
        public override void Update(ulong updateTick, float deltaTime)
        {
            var stick = Vector2.zero;
            var scroll = Vector2.zero;
            bool confirm = false, back = false, menu = false, tabLeft = false, tabRight = false;

            if (VrXr.IsRunning && GetRight())
            {
                right.TryGetFeatureValue(CommonUsages.primary2DAxis, out var axis);
                right.TryGetFeatureValue(CommonUsages.primaryButton, out var a);
                right.TryGetFeatureValue(CommonUsages.secondaryButton, out var b);
                right.TryGetFeatureValue(CommonUsages.trigger, out var trigger);
                right.TryGetFeatureValue(CommonUsages.gripButton, out var grip);
                right.TryGetFeatureValue(CommonUsages.primary2DAxisClick, out var click);

                var state = GameSession.CurrentGameState;
                var gameplay = state == GameSession.GameState.Gameplay;
                var anyButton = a || b || click || trigger >= TriggerPress;
                // A press that opened or closed a menu is still held when the next state starts; it must not act there
                // too (the stick press that opens the pause menu would close it again at once).
                if (state != lastState)
                {
                    lastState = state;
                    waitForRelease = anyButton;
                }
                if (waitForRelease && !anyButton) waitForRelease = false;

                if (!waitForRelease)
                {
                    menu = click;
                    if (!gameplay)
                    {
                        confirm = a || trigger >= TriggerPress;
                        back = b;
                    }
                }
                if (!gameplay)
                {
                    if (grip)
                    {
                        tabLeft = axis.x <= -TabPress;
                        tabRight = axis.x >= TabPress;
                        scroll = new Vector2(0f, axis.y);
                    }
                    else stick = axis;
                }
                LogPresses(state, confirm, back, menu, tabLeft, tabRight);
            }

            UpdateLeftStickWithValue(stick, updateTick, deltaTime);
            UpdateRightStickWithValue(scroll, updateTick, deltaTime);
            UpdateWithState(InputControlType.Action1, confirm, updateTick, deltaTime);
            UpdateWithState(InputControlType.Action2, back, updateTick, deltaTime);
            UpdateWithState(InputControlType.Menu, menu, updateTick, deltaTime);
            UpdateWithState(InputControlType.LeftBumper, tabLeft, updateTick, deltaTime);
            UpdateWithState(InputControlType.RightBumper, tabRight, updateTick, deltaTime);
        }

        private bool GetRight()
        {
            if (!right.isValid) right = InputDevices.GetDeviceAtXRNode(XRNode.RightHand);
            return right.isValid;
        }

        private void LogPresses(GameSession.GameState state, bool confirm, bool back, bool menu, bool tabLeft, bool tabRight)
        {
            var pressed = (confirm ? " confirm" : "") + (back ? " back" : "") + (menu ? " menu" : "") + (tabLeft ? " tab-left" : "") + (tabRight ? " tab-right" : "");
            if (pressed == lastPressed) return;
            lastPressed = pressed;
            if (pressed.Length > 0 && pressLogs++ < 50)
            {
                Plugin.Log.LogInfo($"Menu control:{pressed} ({state}, active device '{InputManager.ActiveDevice?.Name}')");
                if (pressLogs <= 2) LogDevices("on press");
            }
        }
    }

    // InControl hands the active device role only to a device whose last input is strictly newer than the current one's.
    // In the first test an "Unknown Device" (likely a joystick the runtime exposes) reported input every frame, so it kept
    // the role, our presses tied with it and never reached the game. While the right controller presses something, it
    // takes the role.
    [HarmonyPatch(typeof(InputManager), "UpdateActiveDevice")]
    internal static class ActiveDevicePatch
    {
        private static readonly AccessTools.FieldRef<InControl.InputDevice> Active =
            AccessTools.StaticFieldRefAccess<InControl.InputDevice>(AccessTools.Field(typeof(InputManager), "activeDevice"));

        private static int logs;

        private static void Postfix()
        {
            if (!VrMenuInput.HasInput || InputManager.ActiveDevice is VrMenuInput) return;
            if (logs++ < 5) Plugin.Log.LogInfo($"Menu control: taking the active device role from '{InputManager.ActiveDevice?.Name}'");
            Active() = VrMenuInput.Current;
        }
    }
}
