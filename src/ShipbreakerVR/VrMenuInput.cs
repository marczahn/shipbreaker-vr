using System.Linq;
using BBI.Unity.Game;
using HarmonyLib;
using InControl;
using UnityEngine;
using UnityEngine.XR;

namespace ShipbreakerVR
{
    // Menu control with the motion controllers. The game's menus are fully usable with a gamepad: they read InControl
    // actions (Confirm = Action1, Decline = Action2, navigation = left stick or D-pad, scrolling = right stick, tabs =
    // bumpers, pause = Menu). So both controllers together are presented to InControl as one more gamepad and the game
    // does the rest, including its gamepad button prompts. The mapping follows the buttons' places, as Marc asked:
    //   sticks = sticks, A/B/X/Y = A/B/X/Y, triggers = bumpers, grips = triggers.
    //   Pressing the right stick is Menu (Start, the pause menu) in gameplay and on loading screens (where it cycles the
    //   tips), and R3 in menus: the left menu button belongs to Virtual Desktop, and in menus the game reads Start like
    //   Confirm on the selected button, which set off things Marc never chose. (Menu was A + X for a while.)
    //   In the station (Hab) the press opens the station's pause menu, but not through Menu: there the game reads Start as
    //   both Pause and Confirm, so it goes straight to Hab3DController (HabPausePatch), as Esc does.
    //   Pressing the left stick centers the view (VrCamera, like F4) and sends nothing.
    //   There is no View button (Back; B does the same in menus) and no D-pad.
    // In gameplay only the right stick press counts (it opens the pause menu); the rest belongs to controller flying.
    // The actions read only InControl's active device, and another device kept that role (see ActiveDevicePatch).
    internal class VrMenuInput : InControl.InputDevice
    {
        static VrMenuInput() => VrSession.OnUnload("menu control", Detach);

        private static readonly InputControlType[] Buttons =
        {
            InputControlType.Action1, InputControlType.Action2, InputControlType.Action3, InputControlType.Action4,
            InputControlType.LeftBumper, InputControlType.RightBumper,
            InputControlType.RightStickButton, InputControlType.Menu,
        };

        private static VrMenuInput instance;
        private static int pressLogs;

        private GameSession.GameState lastState;
        private bool waitForRelease;
        private readonly bool[] pressed = new bool[Buttons.Length];
        private string lastPressed = "";
        private int lastMask;
        private bool habMenuHeld;

        // Set when the right stick was pressed in the Hab; HabPausePatch opens the station's pause menu and clears it.
        public static bool HabPauseRequested { get; set; }

        private VrMenuInput() : base("ShipbreakerVR controllers")
        {
            DeviceClass = InputDeviceClass.Controller;
            // The game picks its button glyphs by style; Xbox A/B/X/Y sit where the Touch controllers have theirs.
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
            AddControl(InputControlType.LeftTrigger, "Left Trigger");
            AddControl(InputControlType.RightTrigger, "Right Trigger");
            foreach (var button in Buttons) AddControl(button, button.ToString());
        }

        public static VrMenuInput Current => instance;

        public static bool HasInput => instance != null && instance.IsActive;

        // Attaches the device once InControl is set up, and again if InControl was reset. Called every frame.
        public static void EnsureAttached()
        {
            if (!InputManager.IsSetup) return;
            if (instance == null) instance = new VrMenuInput();
            if (InputManager.Devices.Contains(instance)) return;
            InputManager.AttachDevice(instance);
            Plugin.Log.LogInfo("Menu control: controllers attached to InControl as a gamepad");
            LogDevices("attached");
        }

        // Unloading (ScriptEngine reload): InControl would otherwise keep a device of the old instance next to the new one.
        // The game takes a detached controller for an unplugged gamepad and opens the pause menu
        // (InputHandlerGameplay.OnDeviceDetachedGameplay), unless the device is passive; so it goes as a passive one.
        public static void Detach()
        {
            if (instance == null) return;
            instance.Passive = true;
            if (InputManager.IsSetup && InputManager.Devices.Contains(instance)) InputManager.DetachDevice(instance);
            instance = null;
            Plugin.Log.LogInfo("Menu control: controllers detached from InControl");
        }

        public static void LogDevices(string when)
        {
            var devices = InputManager.Devices.Select(d => $"'{d.Name}' ({d.DeviceClass}, {d.DeviceStyle}, last input {d.LastInputTick}{(d.Passive ? ", passive" : "")})");
            Plugin.Log.LogInfo($"Menu control, InControl devices {when} (tick {InputManager.CurrentTick}): {string.Join(", ", devices)}; active '{InputManager.ActiveDevice?.Name}'");
        }

        // Called by InControl in InputManager.Update (from LynxControls.Update), before the actions read the devices.
        public override void Update(ulong updateTick, float deltaTime)
        {
            var leftStick = Vector2.zero;
            var rightStick = Vector2.zero;
            float leftGrip = 0f, rightGrip = 0f;
            for (var i = 0; i < pressed.Length; i++) pressed[i] = false;

            var left = VrInput.Left;
            var right = VrInput.Right;
            if (VrXr.IsRunning && (left.Valid || right.Valid))
            {
                var state = GameSession.CurrentGameState;
                var gameplay = state == GameSession.GameState.Gameplay;
                var hab = state == GameSession.GameState.Hab;
                var menu = (gameplay || hab || state == GameSession.GameState.LoadingInProgress)
                    && right.StickClick.Down;
                if (gameplay)
                {
                    pressed[7] = menu;
                }
                else
                {
                    leftStick = DominantAxis(left.Stick);
                    rightStick = right.Stick;
                    if (VrHab.WaitForStick)
                    {
                        if (left.Stick.magnitude < StickRest && right.Stick.magnitude < StickRest) VrHab.WaitForStick = false;
                        else leftStick = rightStick = Vector2.zero;
                    }
                    leftGrip = left.GripValue;
                    rightGrip = right.GripValue;
                    pressed[0] = right.Primary.Down;
                    pressed[1] = right.Secondary.Down;
                    pressed[2] = left.Primary.Down;
                    pressed[3] = left.Secondary.Down;
                    pressed[4] = left.Trigger.Down;
                    pressed[5] = right.Trigger.Down;
                    pressed[6] = right.StickClick.Down && !menu;
                    pressed[7] = menu && !hab;
                    if (VrHabWalk.TakesInput)
                    {
                        leftStick = rightStick = Vector2.zero;
                        pressed[0] = pressed[1] = false;
                    }
                }

                // A press that opened or closed a menu is still held when the next state starts; it must not act there
                // too (the press that opens the pause menu would close it again at once).
                var any = false;
                foreach (var p in pressed) any |= p;
                if (state != lastState)
                {
                    lastState = state;
                    waitForRelease = any;
                }
                if (waitForRelease && !any) waitForRelease = false;
                if (waitForRelease)
                    for (var i = 0; i < pressed.Length; i++) pressed[i] = false;
                LogPresses(state);
                if (hab && menu && !habMenuHeld && !waitForRelease) HabPauseRequested = true;
                else if (!hab) HabPauseRequested = false;
                habMenuHeld = hab && menu;
            }

            UpdateLeftStickWithValue(leftStick, updateTick, deltaTime);
            UpdateRightStickWithValue(rightStick, updateTick, deltaTime);
            UpdateWithValue(InputControlType.LeftTrigger, leftGrip, updateTick, deltaTime);
            UpdateWithValue(InputControlType.RightTrigger, rightGrip, updateTick, deltaTime);
            for (var i = 0; i < Buttons.Length; i++) UpdateWithState(Buttons[i], pressed[i], updateTick, deltaTime);
        }

        // Menus move one row per up/down and change a value per left/right. A thumb pushed sideways is rarely level, and
        // its vertical part jumped to the row above or below in the middle of adjusting a slider. Only the stronger axis counts.
        // How far in a stick counts as let go.
        private const float StickRest = 0.3f;

        private static Vector2 DominantAxis(Vector2 stick) =>
            Mathf.Abs(stick.x) >= Mathf.Abs(stick.y) ? new Vector2(stick.x, 0f) : new Vector2(0f, stick.y);

        private void LogPresses(GameSession.GameState state)
        {
            // The names only when the buttons held change, not every frame.
            var mask = 0;
            for (var i = 0; i < pressed.Length; i++)
                if (pressed[i]) mask |= 1 << i;
            if (mask == lastMask) return;
            lastMask = mask;
            var names = string.Concat(Buttons.Where((b, i) => pressed[i]).Select(b => " " + b));
            if (names == lastPressed) return;
            lastPressed = names;
            if (names.Length > 0 && pressLogs++ < 50)
            {
                Plugin.Log.LogInfo($"Menu control:{names} ({state}, active device '{InputManager.ActiveDevice?.Name}')");
                if (pressLogs <= 2) LogDevices("on press");
            }
        }
    }

    // InControl hands the active device role only to a device whose last input is strictly newer than the current one's.
    // In the first test an "Unknown Device" (likely a joystick the runtime exposes) reported input every frame, so it kept
    // the role, our presses tied with it and never reached the game. While the controllers press something, they take
    // the role.
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

    // Opens the station's pause menu for the right stick press (VrMenuInput) the way Hab3DController.Update does for Pause (Esc or Start):
    // not inside a terminal or other navigable screen, not before the splash, and not while the game blocks Pause.
    [HarmonyPatch(typeof(Hab3DController), "Update")]
    internal static class HabPausePatch
    {
        private static readonly AccessTools.FieldRef<Hab3DController, bool> InNavigableScreen =
            AccessTools.FieldRefAccess<Hab3DController, bool>("mIsInNavigableScreen");
        private static readonly AccessTools.FieldRef<Hab3DController, bool> PastSplash =
            AccessTools.FieldRefAccess<Hab3DController, bool>("mHasProgressedPastSplash");
        private static readonly System.Action<Hab3DController, bool> TogglePaused =
            AccessTools.MethodDelegate<System.Action<Hab3DController, bool>>(AccessTools.Method(typeof(Hab3DController), "ToggleHabPaused"));

        private static void Postfix(Hab3DController __instance)
        {
            if (!VrMenuInput.HabPauseRequested) return;
            VrMenuInput.HabPauseRequested = false;
            var blocked = ActionBlockerService.Instance != null
                && ActionBlockerService.Instance.IsActionBlocked(LynxControls.ActionSetAndId.FromEnum(FrontEndActions.FrontEndActionSet.Pause));
            if (InNavigableScreen(__instance) || !PastSplash(__instance) || blocked || __instance.IsHabPaused)
            {
                Plugin.Log.LogInfo($"Menu control: right stick press in the station ignored (screen {InNavigableScreen(__instance)}, past splash {PastSplash(__instance)}, blocked {blocked}, paused {__instance.IsHabPaused})");
                return;
            }
            Plugin.Log.LogInfo("Menu control: right stick press opens the station's pause menu");
            ((Carbon.Core.Events.IEventBroadcaster)Main.EventSystem).Post(HabPauseEvent.GetEvent(pause: true));
            TogglePaused(__instance, true);
        }
    }
}
