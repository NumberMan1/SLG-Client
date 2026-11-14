using System;
using System.IO;
using System.Linq;
using UnityEditor;
using UnityEditor.Build;
using UnityEditor.Build.Reporting;
using UnityEngine;

namespace Slg.EditorTools
{
    public static class BuildCiPlayer
    {
        public static void Build()
        {
            string[] args = Environment.GetCommandLineArgs();
            int pathIndex = Array.IndexOf(args, "-customBuildPath") + 1;
            if (pathIndex == 0 || pathIndex >= args.Length || string.IsNullOrWhiteSpace(args[pathIndex]))
            {
                throw new BuildFailedException("Pass -customBuildPath with the Windows player output path.");
            }

            string path = Path.GetFullPath(args[pathIndex]);
            if (!string.Equals(Path.GetExtension(path), ".exe", StringComparison.OrdinalIgnoreCase))
            {
                throw new BuildFailedException("The Windows player output path must end in .exe.");
            }

            string[] scenes = EditorBuildSettings.scenes
                .Where(scene => scene.enabled)
                .Select(scene => scene.path)
                .ToArray();
            if (scenes.Length == 0)
            {
                throw new BuildFailedException("Enable at least one scene in Unity Build Settings.");
            }

            Directory.CreateDirectory(Path.GetDirectoryName(path));
            BuildReport report = BuildPipeline.BuildPlayer(scenes, path,
                BuildTarget.StandaloneWindows64, BuildOptions.Development);
            if (report.summary.result != BuildResult.Succeeded)
            {
                throw new BuildFailedException($"Windows player build ended with {report.summary.result}.");
            }

            Debug.Log($"Built Windows development player at {path}.");
        }
    }
}
