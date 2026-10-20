using System.IO;
using System.Linq;
using UnityEditor;
using UnityEditor.Build.Reporting;
using UnityEngine;

// Baut alles, was der Mod aus Unity braucht, und legt es unter Output/ in der Ordnerstruktur des Spiels ab:
//   Output/BepInEx/plugins/ShipbreakerVR/            AssetBundle mit den XR-Einstellungen + Unity.XR.*.dll, Unity.InputSystem.dll
//   Output/Shipbreaker_Data/Plugins/x86_64/          UnityOpenXR.dll, openxr_loader.dll
//   Output/Shipbreaker_Data/UnitySubsystems/         UnitySubsystemsManifest.json
public static class ShipbreakerVRBuild
{
    [MenuItem("ShipbreakerVR/Build für das Spiel")]
    public static void BuildAll()
    {
        var root = Path.GetFullPath(Path.Combine(Application.dataPath, ".."));
        var outDir = Path.Combine(root, "Output");
        var playerDir = Path.Combine(root, "Build");
        if (Directory.Exists(outDir)) Directory.Delete(outDir, true);

        var pluginDir = Path.Combine(outDir, "BepInEx", "plugins", "ShipbreakerVR");
        // Zusätzlich flach unter Output/AssetBundles, damit Claude die Datei vom PC lesen kann (maximale Ordnertiefe).
        var flatBundleDir = Path.Combine(outDir, "AssetBundles");
        Directory.CreateDirectory(flatBundleDir);
        BuildPipeline.BuildAssetBundles(flatBundleDir, BuildAssetBundleOptions.None, BuildTarget.StandaloneWindows64);
        CopyDirectory(flatBundleDir, Path.Combine(pluginDir, "AssetBundles"));

        var report = BuildPipeline.BuildPlayer(new BuildPlayerOptions
        {
            scenes = new[] { "Assets/Scene.unity" },
            locationPathName = Path.Combine(playerDir, "ShipbreakerVrUnity.exe"),
            target = BuildTarget.StandaloneWindows64,
            options = BuildOptions.None
        });
        if (report.summary.result != BuildResult.Succeeded)
        {
            Debug.LogError("[ShipbreakerVR] Player-Build fehlgeschlagen: " + report.summary.result);
            return;
        }

        var dataDir = Directory.GetDirectories(playerDir, "*_Data").Single();

        var managedDir = Path.Combine(dataDir, "Managed");
        foreach (var dll in Directory.GetFiles(managedDir, "Unity.XR.*.dll").Append(Path.Combine(managedDir, "Unity.InputSystem.dll")))
            CopyFile(dll, pluginDir);

        var nativeOut = Path.Combine(outDir, "Shipbreaker_Data", "Plugins", "x86_64");
        foreach (var name in new[] { "UnityOpenXR.dll", "openxr_loader.dll" })
        {
            var found = Directory.GetFiles(Path.Combine(dataDir, "Plugins"), name, SearchOption.AllDirectories).FirstOrDefault();
            if (found == null) Debug.LogError("[ShipbreakerVR] Nicht gefunden: " + name);
            else CopyFile(found, nativeOut);
        }

        var subsystems = Path.Combine(dataDir, "UnitySubsystems");
        if (Directory.Exists(subsystems)) CopyDirectory(subsystems, Path.Combine(outDir, "Shipbreaker_Data", "UnitySubsystems"));
        else Debug.LogError("[ShipbreakerVR] Ordner UnitySubsystems fehlt im Player-Build");

        File.WriteAllText(Path.Combine(outDir, "files.txt"),
            string.Join("\n", Directory.GetFiles(outDir, "*", SearchOption.AllDirectories).Select(f => f.Substring(outDir.Length + 1))));
        Debug.Log("[ShipbreakerVR] Fertig: " + outDir);
        EditorUtility.RevealInFinder(outDir);
    }

    private static void CopyFile(string file, string targetDir)
    {
        Directory.CreateDirectory(targetDir);
        File.Copy(file, Path.Combine(targetDir, Path.GetFileName(file)), true);
    }

    private static void CopyDirectory(string source, string target)
    {
        foreach (var file in Directory.GetFiles(source, "*", SearchOption.AllDirectories))
            CopyFile(file, Path.Combine(target, Path.GetDirectoryName(file.Substring(source.Length + 1)) ?? ""));
    }
}
