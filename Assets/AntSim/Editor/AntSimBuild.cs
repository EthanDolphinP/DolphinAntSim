using System.IO;
using UnityEditor;
using UnityEditor.Build.Reporting;
using UnityEngine;

namespace AntSim.EditorTools
{
    /// <summary>
    /// Builds the Windows release: AntSim ▸ Build Windows Release in the menu, or from the command line:
    /// Unity.exe -batchmode -quit -projectPath . -executeMethod AntSim.EditorTools.AntSimBuild.BuildWindows
    /// Output: Builds/Windows/Dolphin Ant Simulator.exe (plus its _Data folder), zip that folder to release it.
    /// </summary>
    public static class AntSimBuild
    {
        const string OutDir = "Builds/Windows";

        [MenuItem("AntSim/Build Windows Release")]
        public static void BuildWindows()
        {
            if (Directory.Exists(OutDir)) Directory.Delete(OutDir, true);
            var options = new BuildPlayerOptions
            {
                scenes = new[] { "Assets/Scenes/SampleScene.unity" },
                locationPathName = Path.Combine(OutDir, PlayerSettings.productName + ".exe"),
                target = BuildTarget.StandaloneWindows64,
                targetGroup = BuildTargetGroup.Standalone,
                options = BuildOptions.None,
            };
            var report = BuildPipeline.BuildPlayer(options);
            var s = report.summary;
            Debug.Log($"AntSimBuild: {s.result}, {s.totalSize / (1024 * 1024)} MB, {s.totalErrors} errors, {s.totalTime.TotalSeconds:0}s -> {s.outputPath}");
            if (Application.isBatchMode && s.result != BuildResult.Succeeded) EditorApplication.Exit(1);
        }
    }
}
