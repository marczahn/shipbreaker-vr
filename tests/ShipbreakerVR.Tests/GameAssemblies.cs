using System.IO;
using System.Runtime.CompilerServices;
using System.Runtime.Loader;
using BepInEx.Logging;

namespace ShipbreakerVR.Tests
{
    internal static class GameAssemblies
    {
        // The game's and BepInEx's DLLs are copied next to the tests but are not in their dependency list, so .NET does
        // not look for them there by itself. The plugin logs through Plugin.Log, which the game would set.
        [ModuleInitializer]
        internal static void Init()
        {
            AssemblyLoadContext.Default.Resolving += (context, name) =>
            {
                var path = Path.Combine(System.AppContext.BaseDirectory, name.Name + ".dll");
                return File.Exists(path) ? context.LoadFromAssemblyPath(path) : null;
            };
            Plugin.Log = new ManualLogSource("Tests");
        }
    }
}
