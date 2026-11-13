using System.IO;
using UnityEditor;
using UnityEditor.Build;
using UnityEditor.Build.Reporting;
using UnityEditor.UnityLinker;
using UnityEngine;

namespace Sud.UnityExporter
{
    public sealed class SudLinkerProcessor : IUnityLinkerProcessor
    {
        public int callbackOrder => 0;

        public string GenerateAdditionalLinkXmlFile(BuildReport report, UnityLinkerBuildPipelineData data)
        {
            if (report.summary.platform != BuildTarget.WebGL)
                return null;

            var outputDirectory = Path.Combine(Path.GetDirectoryName(Application.dataPath), "Library", "SudUnityExporter");
            Directory.CreateDirectory(outputDirectory);
            var outputPath = Path.Combine(outputDirectory, "link.xml");
            File.Copy(Path.Combine(SudPluginPaths.RuntimeDirectory, "Plugins", "link.xml"), outputPath, true);
            return outputPath;
        }
    }
}
