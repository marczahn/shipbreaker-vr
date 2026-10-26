using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using HarmonyLib;

namespace ShipbreakerVR
{
    // The game's classes that the tool transpilers reach into, in one table: which tool family each belongs to
    // (VrDualTools: whose input, aim and "is it equipped" check it gets) and which of the three redirects apply to it.
    //   Aim    LynxCameraController.MainCamera / MainCameraTransform -> the aim camera on the hand (AimPatch)
    //   Input  the LynxControlExtensions polls -> the controllers' buttons (ToolInputPatch)
    //   Gate   EquipmentController.CurrentEquipment, TetherController.IsEquipped -> the second tool's own (GatePatch)
    // Before, the three patches and VrDualTools each kept a list of their own, and they had to match. A class the game
    // no longer has is warned about once, by name, for every redirect that needed it.
    internal static class GameTools
    {
        // Which tool's code a game class belongs to.
        public enum Family { Other, Cutter, Grapple }

        [System.Flags]
        public enum Redirect { None = 0, Aim = 1, Input = 2, Gate = 4 }

        private static readonly (string Type, Family Family, Redirect Redirects)[] Classes =
        {
            ("BBI.Unity.Game.CuttingController", Family.Cutter, Redirect.Aim | Redirect.Input | Redirect.Gate),
            ("BBI.Unity.Game.CuttingFXController", Family.Cutter, Redirect.Aim | Redirect.Input),
            ("BBI.Unity.Game.CuttingToolController", Family.Cutter, Redirect.Input | Redirect.Gate),
            ("BBI.Unity.Game.ScalpelController", Family.Cutter, Redirect.Aim | Redirect.Input | Redirect.Gate),
            ("BBI.Unity.Game.ScalpelFXController", Family.Cutter, Redirect.Aim | Redirect.Input | Redirect.Gate),
            ("BBI.Unity.Game.ScalpelTarget", Family.Cutter, Redirect.Aim),
            ("BBI.Unity.Game.CutterHeatBarUIController", Family.Cutter, Redirect.Gate),
            ("GrapplingHook", Family.Grapple, Redirect.Aim | Redirect.Input | Redirect.Gate),
            ("TetherController", Family.Grapple, Redirect.Aim | Redirect.Input | Redirect.Gate),
            ("BBI.Unity.Game.Tether", Family.Grapple, Redirect.Input),
            ("BBI.Unity.Game.MagnetController", Family.Grapple, Redirect.Aim | Redirect.Input),
            ("BBI.Unity.Game.GunController", Family.Grapple, Redirect.Gate),
            // Instances of the shared RaycastSystem (VrDualTools.LoadFamily).
            ("BBI.Unity.Game.GrappleRaycastSystem", Family.Grapple, Redirect.None),
            ("BBI.Unity.Game.GrapplePushRaycastSystem", Family.Grapple, Redirect.None),
            ("BBI.Unity.Game.RaycastSystem", Family.Other, Redirect.Aim),
            ("BBI.Unity.Game.Scanner", Family.Other, Redirect.Aim | Redirect.Input),
            ("BBI.Unity.Game.ThrowController", Family.Other, Redirect.Aim | Redirect.Input),
            ("BBI.Unity.Game.DemoChargeController", Family.Other, Redirect.Aim | Redirect.Input),
            ("BBI.Unity.Game.InteractionController", Family.Other, Redirect.Input),
            ("BBI.Unity.Game.HandGrab", Family.Other, Redirect.Input),
            ("BBI.Unity.Game.GrabController", Family.Other, Redirect.Input),
            ("BBI.Unity.Game.EquipmentController", Family.Other, Redirect.Input),
        };

        private static readonly Dictionary<string, Family> Families = Classes
            .Where(c => c.Family != Family.Other).ToDictionary(c => c.Type, c => c.Family);

        // The family of a game class (or of the class a nested type, a job or a lambda, sits in).
        public static Family FamilyOf(System.Type type)
        {
            for (var t = type; t != null; t = t.DeclaringType)
                if (Families.TryGetValue(t.FullName ?? "", out var family)) return family;
            return Family.Other;
        }

        // The methods of the classes with that redirect (and their nested types) whose body calls one of these methods.
        // patch names the transpiler in warnings.
        public static IEnumerable<MethodBase> MethodsCalling(Redirect redirect, string patch, ICollection<MethodInfo> called)
        {
            foreach (var (name, _, redirects) in Classes)
            {
                if ((redirects & redirect) == 0) continue;
                var type = AccessTools.TypeByName(name);
                if (type == null)
                {
                    Plugin.Log.LogWarning($"{patch}: type {name} not found");
                    continue;
                }
                foreach (var nested in new[] { type }.Concat(type.GetNestedTypes(AccessTools.all)))
                    foreach (var method in AccessTools.GetDeclaredMethods(nested))
                        if (!method.IsAbstract && !method.ContainsGenericParameters && method.GetMethodBody() != null && Calls(method, called, patch))
                            yield return method;
            }
        }

        private static bool Calls(MethodBase method, ICollection<MethodInfo> called, string patch)
        {
            try
            {
                return PatchProcessor.ReadMethodBody(method).Any(i => i.Value is MethodInfo m && called.Contains(m));
            }
            catch (System.Exception e)
            {
                Plugin.Log.LogWarning($"{patch}: could not read {method.DeclaringType?.Name}.{method.Name}: {e.Message}");
                return false;
            }
        }
    }
}
