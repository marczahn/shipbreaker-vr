using System.Reflection;
using BBI.Unity.Game;
using HarmonyLib;
using UnityEngine;
using UnityEngine.Events;

namespace ShipbreakerVR
{
    // Raik (2026-11-26): the text shown before the campaign's desk is skipped by holding Space, and in VR that should be
    // the A button. The game's menus take A as Confirm through InControl (VrMenuInput), but this text is driven by
    // 'HotkeyHandler' (Assembly-CSharp), which reads the keyboard itself (Input.GetKey of its key, Space here) and so
    // never sees a controller. Its Update is replaced while VR runs and its key is Space: the key or the right
    // controller's A button held, with the same down / held / up events. In gameplay A belongs to the hands, so it counts
    // only outside it.
    [HarmonyPatch]
    internal static class VrHotkeySkip
    {
        private static System.Type type;
        private static FieldInfo keyField, downField, upField, heldField, isDownField;
        private static int logs;

        private static bool Prepare()
        {
            type = AccessTools.TypeByName("HotkeyHandler");
            if (type == null) return false;
            keyField = AccessTools.Field(type, "m_KeyCode");
            downField = AccessTools.Field(type, "m_OnKeyDown");
            upField = AccessTools.Field(type, "m_OnKeyUp");
            heldField = AccessTools.Field(type, "m_OnKeyHeld");
            isDownField = AccessTools.Field(type, "isDown");
            return keyField != null && downField != null && upField != null && heldField != null && isDownField != null;
        }

        private static MethodBase TargetMethod() => AccessTools.Method(type, "Update");

        private static bool Prefix(object __instance)
        {
            if (!VrXr.IsRunning || (KeyCode)keyField.GetValue(__instance) != KeyCode.Space) return true;
            var pad = GameSession.CurrentGameState != GameSession.GameState.Gameplay && VrInput.Right.Valid && VrInput.Right.Primary.Down;
            var held = Input.GetKey(KeyCode.Space) || pad;
            var isDown = (bool)isDownField.GetValue(__instance);
            if (held && !isDown)
            {
                isDownField.SetValue(__instance, true);
                (downField.GetValue(__instance) as UnityEvent)?.Invoke();
                if (pad && logs++ < 10) Plugin.Log.LogInfo($"Skip: A held as Space on '{((Component)__instance).name}'");
            }
            else if (!held && isDown)
            {
                isDownField.SetValue(__instance, false);
                (upField.GetValue(__instance) as UnityEvent)?.Invoke();
            }
            else if (held)
            {
                (heldField.GetValue(__instance) as UnityEvent)?.Invoke();
            }
            return false;
        }
    }
}
