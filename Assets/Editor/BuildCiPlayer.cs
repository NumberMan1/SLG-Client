using System;
using System.IO;
using System.Linq;
using UnityEditor;
using UnityEditor.Build;
using UnityEditor.Build.Reporting;
using UnityEngine;

namespace Slg.EditorTools
{
    /// <summary>提供本地和 CI 共用的开发版客户端构建入口。</summary>
    public static class BuildCiPlayer
    {
        /// <summary>从批处理入口构建 Windows 客户端，输出路径须以 .exe 结尾。</summary>
        public static void Build()
        {
            BuildPlayer(BuildTarget.StandaloneWindows64);
        }

        /// <summary>构建 WebGL 客户端。批处理从参数读取输出目录，编辑器菜单会打开目录选择框。</summary>
        [MenuItem("SLG/Build Web Player")]
        public static void BuildWeb()
        {
            BuildPlayer(BuildTarget.WebGL);
        }

        private static void BuildPlayer(BuildTarget target)
        {
            if (!BuildPipeline.IsBuildTargetSupported(BuildPipeline.GetBuildTargetGroup(target), target))
            {
                throw new BuildFailedException($"Install {target} Build Support for this Unity editor in Unity Hub.");
            }
            string[] args = Environment.GetCommandLineArgs();
            int pathIndex = Array.IndexOf(args, "-customBuildPath") + 1;
            if (pathIndex == 0 || pathIndex >= args.Length || string.IsNullOrWhiteSpace(args[pathIndex]))
            {
                if (target == BuildTarget.WebGL && !Application.isBatchMode)
                {
                    string folder = EditorUtility.SaveFolderPanel("Build Web Player", "", "Frontier-Web");
                    if (string.IsNullOrEmpty(folder)) return;
                    BuildAtPath(target, folder);
                    return;
                }
                throw new BuildFailedException("Pass -customBuildPath with the player output path.");
            }

            string path = Path.GetFullPath(args[pathIndex]);
            BuildAtPath(target, path);
        }

        private static void BuildAtPath(BuildTarget target, string path)
        {
            if (target == BuildTarget.StandaloneWindows64 && !string.Equals(Path.GetExtension(path), ".exe", StringComparison.OrdinalIgnoreCase))
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
            // Web 开发包使用未压缩文件供网关直接托管，构建结束后恢复项目设置。
            var compression = PlayerSettings.WebGL.compressionFormat;
            BuildReport report;
            try
            {
                if (target == BuildTarget.WebGL)
                {
                    PlayerSettings.WebGL.compressionFormat = WebGLCompressionFormat.Disabled;
                }
                report = BuildPipeline.BuildPlayer(scenes, path, target, BuildOptions.Development);
            }
            finally
            {
                PlayerSettings.WebGL.compressionFormat = compression;
            }
            if (report.summary.result != BuildResult.Succeeded)
            {
                throw new BuildFailedException($"{target} player build ended with {report.summary.result}.");
            }

            Debug.Log($"Built {target} development player at {path}.");
        }
    }
}
