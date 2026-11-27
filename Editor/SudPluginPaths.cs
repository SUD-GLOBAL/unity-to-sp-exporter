using System.IO;
using UnityEngine;
using PackageInfo = UnityEditor.PackageManager.PackageInfo;

namespace Sud.UnityExporter
{
    internal static class SudPluginPaths
    {
        internal static readonly string Root;
        internal static readonly string RuntimeDirectory;
        internal static readonly string Version;
        internal static readonly bool IsUpm;

        static SudPluginPaths()
        {
            var package = PackageInfo.FindForAssembly(typeof(SudPluginPaths).Assembly);
            IsUpm = package != null;
            if (package != null)
            {
                Root = package.resolvedPath;
                Version = package.version;
            }
            else
            {
                Root = Path.Combine(Application.dataPath, "sud-unity-plugin");
            }

            RuntimeDirectory = Path.Combine(Root, "Runtime");
            if (!Directory.Exists(RuntimeDirectory))
                throw new DirectoryNotFoundException($"SUD 插件缺少 Runtime 目录：{RuntimeDirectory}");
        }
    }
}
