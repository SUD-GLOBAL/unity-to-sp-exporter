using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Serialization;

namespace Sud.UnityExporter
{
    [Serializable]
    public sealed class SudPredownloadResourceSetting
    {
        public string id = string.Empty;
        public string streamingAssetsPath = string.Empty;
        public int priority;
        public string type = "assetbundle";
    }

    public enum SudRuntimeLogLevel
    {
        Log = 0,
        Warn = 1,
        Error = 2,
    }

    public class SudExporterSettings : ScriptableObject
    {
        private static string AssetDirectory => SudPluginPaths.IsUpm
            ? "Assets/SudExporterConfig"
            : "Assets/sud-unity-plugin/Editor";

        [Header("Build Settings")]
        public bool developmentBuild = false;
        public string outputFolder = string.Empty;

        [Header("Runtime Logging")]
        public SudRuntimeLogLevel runtimeLogLevel = SudRuntimeLogLevel.Log;

        [Header("Remote Resources")]
        [FormerlySerializedAs("useUnityDataCdn")]
        [Tooltip("使用远程资源：将 StreamingAssets 部署到 CDN，并启用预下载缓存。")]
        public bool useRemoteResources = false;
        // 旧配置缺少此字段时沿用原 CDN 行为；新建配置在 LoadOrCreate 中设为直接打包。
        public bool useDataCdn = true;
        public string gameResourceCdnUrl = string.Empty;
        [HideInInspector]
        public bool gameResourceCdnMigrated;
        // Legacy addresses are retained so existing projects can migrate to one CDN root.
        [HideInInspector]
        public string unityDataCdnUrl = string.Empty;

        [Header("Startup Loading")]
        public string startupLoadingCoverImage = string.Empty;

        [Header("Predownload")]
        public bool enablePredownload;
        [HideInInspector]
        public string predownloadCdnUrl = string.Empty;
        [Range(1, 10)] public int predownloadStartupConcurrency = 10;
        [Range(1, 10)] public int predownloadRuntimeConcurrency = 1;
        public int bundleHashLength = 32;
        public string predownloadFiles = string.Empty;
        // Kept so existing projects can migrate their configured paths and resource IDs.
        [HideInInspector]
        public List<SudPredownloadResourceSetting> predownloadResources = new List<SudPredownloadResourceSetting>();

        public static SudExporterSettings LoadOrCreate()
        {
            var assetDirectory = AssetDirectory;
            var assetPath = assetDirectory + "/SudExporterSettings.asset";
            var settings = UnityEditor.AssetDatabase.LoadAssetAtPath<SudExporterSettings>(assetPath);
            if (settings == null)
            {
                settings = ScriptableObject.CreateInstance<SudExporterSettings>();
                settings.useDataCdn = false;
                if (SudPluginPaths.IsUpm && !UnityEditor.AssetDatabase.IsValidFolder(assetDirectory))
                {
                    UnityEditor.AssetDatabase.CreateFolder("Assets", "SudExporterConfig");
                }
                UnityEditor.AssetDatabase.CreateAsset(settings, assetPath);
                UnityEditor.AssetDatabase.SaveAssets();
            }
            return settings;
        }
    }
}
