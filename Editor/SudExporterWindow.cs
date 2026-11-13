using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using UnityEditor;
using UnityEditor.Build.Reporting;
using UnityEditor.SceneManagement;
using UnityEngine;

namespace Sud.UnityExporter
{
    [InitializeOnLoad]
    public class SudExporterWindow : EditorWindow
    {
        static SudExporterWindow()
        {
            CheckUnityVersionOnce();
        }

        // Assets 安装沿用常量记录版本；UPM 安装读取包版本。
        private const string PluginVersion = "1.0.8";

        private const string WindowTitle = "SUD 小游戏";
        private const string MenuPath = "SUD 小游戏/转换小游戏";

        private const string RuntimeLogLevelLogDefine = "SUD_RUNTIME_LOG_LEVEL_LOG";
        private const string RuntimeLogLevelWarnDefine = "SUD_RUNTIME_LOG_LEVEL_WARN";
        private const string RuntimeLogLevelErrorDefine = "SUD_RUNTIME_LOG_LEVEL_ERROR";
        private static readonly string[] DataLoadModes = { "CDN", "直接打包" };

        // 已验证可用的 Unity 版本，新增适配版本时在此追加。
        private static readonly string[] SupportedUnityVersions =
        {
            "2022.3.18f1c1",
        };

        private static readonly string SupportedVersionsText = string.Join(" / ", SupportedUnityVersions);

        // 永久存储的提醒标记，记录已提醒过的「插件版本|Unity 版本」组合，任一变化都会重新提醒一次。
        private const string VersionPromptedPrefKey = "Sud.UnityExporter.VersionPrompted";

        private string _outputFolderAbsolutePath;
        private Vector2 _scrollPos;
        private Vector2 _scenesScrollPos;
        private bool _scenesFoldout = true;
        private bool _developmentBuild;
        private SudRuntimeLogLevel _runtimeLogLevel;
        private bool _useRemoteResources;
        private bool _useDataCdn;
        private string _gameResourceCdnUrl;
        private bool _legacyCdnConflict;
        private string _startupLoadingCoverImage;
        private string _startupLoadingCoverPreviewPath;
        private Texture2D _startupLoadingCoverPreview;
        private bool _enablePredownload;
        private int _bundleHashLength;
        private int _predownloadStartupConcurrency = 10;
        private int _predownloadRuntimeConcurrency = 1;
        private string _predownloadFiles;
        private bool _predownloadFilesEdited;
        private SudExporterSettings _settings;

        [MenuItem(MenuPath)]
        public static void ShowWindow()
        {
            var window = GetWindow<SudExporterWindow>(utility: false, title: WindowTitle, focus: true);
            window.minSize = new Vector2(420, 620);
            window.Show();
        }

        private void OnEnable()
        {
            _settings = SudExporterSettings.LoadOrCreate();
            _outputFolderAbsolutePath = string.IsNullOrEmpty(_settings.outputFolder) ? GetDefaultOutputFolder() : _settings.outputFolder;

            _developmentBuild = _settings.developmentBuild;
            _runtimeLogLevel = _settings.runtimeLogLevel;
            _useRemoteResources = _settings.useRemoteResources;
            _useDataCdn = _settings.useDataCdn;
            _startupLoadingCoverImage = _settings.startupLoadingCoverImage;
            _enablePredownload = _settings.enablePredownload;
            _gameResourceCdnUrl = _settings.gameResourceCdnUrl;
            _legacyCdnConflict = false;
            if (!_settings.gameResourceCdnMigrated && string.IsNullOrWhiteSpace(_gameResourceCdnUrl))
            {
                var dataUrl = _settings.unityDataCdnUrl;
                var predownloadUrl = _settings.predownloadCdnUrl;
                _legacyCdnConflict = !string.IsNullOrWhiteSpace(dataUrl) && !string.IsNullOrWhiteSpace(predownloadUrl) &&
                    !string.Equals(dataUrl.TrimEnd('/'), predownloadUrl.TrimEnd('/'), StringComparison.OrdinalIgnoreCase);
                if (_useDataCdn && !string.IsNullOrWhiteSpace(dataUrl))
                    _gameResourceCdnUrl = dataUrl;
                else if (!string.IsNullOrWhiteSpace(predownloadUrl))
                    _gameResourceCdnUrl = predownloadUrl;
                else
                    _gameResourceCdnUrl = dataUrl;
            }
            _predownloadFiles = _settings.predownloadFiles;
            _bundleHashLength = _settings.bundleHashLength > 0 ? _settings.bundleHashLength : 32;
            _predownloadStartupConcurrency = Mathf.Clamp(_settings.predownloadStartupConcurrency, 1, 10);
            _predownloadRuntimeConcurrency = Mathf.Clamp(_settings.predownloadRuntimeConcurrency, 1, 10);
            _predownloadFilesEdited = false;
            if (_enablePredownload && string.IsNullOrWhiteSpace(_predownloadFiles) && _settings.predownloadResources != null)
            {
                _predownloadFiles = string.Join(";", _settings.predownloadResources
                    .Where(resource => resource != null && !string.IsNullOrWhiteSpace(resource.streamingAssetsPath))
                    .OrderByDescending(resource => resource.priority)
                    .Select(resource => resource.streamingAssetsPath));
            }
        }

        private void OnDisable()
        {
            if (_settings != null)
            {
                _settings.developmentBuild = _developmentBuild;
                _settings.runtimeLogLevel = _runtimeLogLevel;
                _settings.outputFolder = _outputFolderAbsolutePath ?? string.Empty;
                _settings.useRemoteResources = _useRemoteResources;
                _settings.useDataCdn = _useDataCdn;
                _settings.startupLoadingCoverImage = _startupLoadingCoverImage ?? string.Empty;
                SaveResourceSettings();
                UnityEditor.EditorUtility.SetDirty(_settings);
                UnityEditor.AssetDatabase.SaveAssets();
            }

            ClearStartupLoadingCoverPreview();
        }

        private static void CheckUnityVersionOnce()
        {
            if (SupportedUnityVersions.Contains(Application.unityVersion))
            {
                return;
            }

            EditorApplication.delayCall += () =>
            {
                // 等待导入完成后读取插件包信息。
                var versionPromptedValue = $"{SudPluginPaths.Version ?? PluginVersion}|{Application.unityVersion}";
                if (EditorPrefs.GetString(VersionPromptedPrefKey, string.Empty) == versionPromptedValue)
                    return;

                EditorPrefs.SetString(VersionPromptedPrefKey, versionPromptedValue);

                Debug.LogWarning(
                    $"[SudExporter] 当前 Unity 版本 {Application.unityVersion} 不在插件支持范围内（已支持版本：{SupportedVersionsText}），建议切换后导出。");

                EditorUtility.DisplayDialog(
                    "SUD 小游戏导出插件",
                    $"插件已支持版本：Unity {SupportedVersionsText}\n当前编辑器版本：Unity {Application.unityVersion}\n\n建议切换到已支持版本后再导出。",
                    "确定");
            };
        }

        private static string GetRuntimeLogDefine(SudRuntimeLogLevel level)
        {
            switch (level)
            {
                case SudRuntimeLogLevel.Log:
                    return RuntimeLogLevelLogDefine;
                case SudRuntimeLogLevel.Warn:
                    return RuntimeLogLevelWarnDefine;
                case SudRuntimeLogLevel.Error:
                    return RuntimeLogLevelErrorDefine;
                default:
                    throw new ArgumentOutOfRangeException(
                        nameof(level),
                        level,
                        "未知的运行时日志等级。");
            }
        }

        private void OnGUI()
        {
            _scrollPos = EditorGUILayout.BeginScrollView(_scrollPos);

            EditorGUILayout.Space(6);

            DrawOutputFolderSelector();

            EditorGUILayout.Space(8);
            DrawWebGLInfo();

            EditorGUILayout.Space(8);
            DrawPackageOptimization();

            if (_useRemoteResources && _useDataCdn)
            {
                EditorGUILayout.Space(8);
                DrawStartupLoading();
            }
            else
            {
                ClearStartupLoadingCoverPreview();
            }

            EditorGUILayout.Space(8);
            DrawScenesInBuild();

            EditorGUILayout.Space(40);
            var btnWidth = GUILayout.Width(100);
            EditorGUILayout.BeginHorizontal();
            GUILayout.FlexibleSpace();
            if (GUILayout.Button("手动清理缓存", btnWidth, GUILayout.Height(30)))
            {
                ClearBuildCache();
            }
            if (GUILayout.Button("导出", btnWidth, GUILayout.Height(30)))
            {
                EditorApplication.delayCall += RunWebGLExportAndConvert;
            }
            EditorGUILayout.EndHorizontal();

            EditorGUILayout.EndScrollView();
        }

        private void DrawWebGLInfo()
        {
            EditorGUILayout.LabelField("基本信息配置", EditorStyles.boldLabel);

            EditorGUI.indentLevel++;

            _developmentBuild = EditorGUILayout.Toggle("Development Build", _developmentBuild);
            _runtimeLogLevel = (SudRuntimeLogLevel)EditorGUILayout.EnumPopup(
                "Runtime Log Level",
                _runtimeLogLevel);
            PlayerSettings.colorSpace = (ColorSpace)EditorGUILayout.EnumPopup("Color Space", PlayerSettings.colorSpace);
            DrawReflectionEnumField(typeof(EditorUserBuildSettings), "webGLBuildSubtarget", "Texture Compression");
            PlayerSettings.WebGL.exceptionSupport = (WebGLExceptionSupport)EditorGUILayout.EnumPopup(
                "Exception Support", PlayerSettings.WebGL.exceptionSupport);

            EditorGUILayout.Space(4);
            EditorGUI.BeginDisabledGroup(true);
            EditorGUILayout.EnumPopup("Compression Format", WebGLCompressionFormat.Disabled);
            EditorGUILayout.LabelField("Data Caching", "Disabled");
            EditorGUI.EndDisabledGroup();

            EditorGUI.indentLevel--;
        }

        private void DrawStartupLoading()
        {
            EditorGUILayout.LabelField("启动 Loading", EditorStyles.boldLabel);

            EditorGUI.indentLevel++;

            EditorGUILayout.BeginHorizontal();
            _startupLoadingCoverImage = EditorGUILayout.TextField("背景图", _startupLoadingCoverImage ?? string.Empty);
            if (GUILayout.Button("选择", GUILayout.Width(46)))
            {
                var initialDir = GetStartupLoadingCoverInitialDirectory(_startupLoadingCoverImage);
                var selected = EditorUtility.OpenFilePanel("选择启动 Loading 背景图", initialDir, string.Empty);
                if (!string.IsNullOrEmpty(selected))
                {
                    _startupLoadingCoverImage = selected;
                    GUI.FocusControl(null);
                }
            }
            EditorGUI.BeginDisabledGroup(string.IsNullOrEmpty(_startupLoadingCoverImage));
            if (GUILayout.Button("清除", GUILayout.Width(46)))
            {
                _startupLoadingCoverImage = string.Empty;
                ClearStartupLoadingCoverPreview();
                GUI.FocusControl(null);
            }
            EditorGUI.EndDisabledGroup();
            EditorGUILayout.EndHorizontal();

            DrawStartupLoadingCoverPreview();

            EditorGUILayout.HelpBox(".data 使用 CDN 时必须设置背景图，用于展示下载期间的启动 Loading。背景图等比铺满屏幕、居中裁剪，宽高均不可超过 2048。",
                string.IsNullOrWhiteSpace(_startupLoadingCoverImage) ? MessageType.Warning : MessageType.Info);

            EditorGUI.indentLevel--;
        }

        private void DrawStartupLoadingCoverPreview()
        {
            if (!_useRemoteResources || !_useDataCdn)
            {
                ClearStartupLoadingCoverPreview();
                return;
            }

            if (string.IsNullOrEmpty(_startupLoadingCoverImage))
            {
                ClearStartupLoadingCoverPreview();
                return;
            }

            string errorMessage;
            var preview = LoadStartupLoadingCoverPreview(_startupLoadingCoverImage, out errorMessage);
            if (preview == null)
            {
                EditorGUILayout.HelpBox(errorMessage, MessageType.Warning);
                return;
            }

            var previewWidth = Mathf.Min(160f, preview.width);
            var previewHeight = Mathf.Min(90f, preview.height * previewWidth / preview.width);
            EditorGUILayout.BeginHorizontal();
            GUILayout.Space(EditorGUI.indentLevel * 15f);
            GUILayout.Label(preview, GUILayout.Width(previewWidth), GUILayout.Height(previewHeight));
            EditorGUILayout.LabelField($"{preview.width} x {preview.height}", GUILayout.Width(100));
            EditorGUILayout.EndHorizontal();
        }

        private Texture2D LoadStartupLoadingCoverPreview(string path, out string errorMessage)
        {
            errorMessage = string.Empty;
            if (_startupLoadingCoverPreview != null && _startupLoadingCoverPreviewPath == path)
            {
                return _startupLoadingCoverPreview;
            }

            ClearStartupLoadingCoverPreview();

            var sourcePath = SudTemplateConverter.GetStartupLoadingCoverSourcePath(path);
            if (!File.Exists(sourcePath))
            {
                errorMessage = $"背景图不存在：{path}";
                return null;
            }

            try
            {
                var bytes = File.ReadAllBytes(sourcePath);
                var texture = new Texture2D(2, 2, TextureFormat.RGBA32, false);
                if (!ImageConversion.LoadImage(texture, bytes))
                {
                    UnityEngine.Object.DestroyImmediate(texture);
                    errorMessage = $"背景图格式无法识别：{path}";
                    return null;
                }

                _startupLoadingCoverPreview = texture;
                _startupLoadingCoverPreviewPath = path;
                return _startupLoadingCoverPreview;
            }
            catch (Exception ex)
            {
                errorMessage = $"读取背景图失败：{ex.Message}";
                return null;
            }
        }

        private void ClearStartupLoadingCoverPreview()
        {
            if (_startupLoadingCoverPreview != null)
            {
                UnityEngine.Object.DestroyImmediate(_startupLoadingCoverPreview);
                _startupLoadingCoverPreview = null;
            }
            _startupLoadingCoverPreviewPath = string.Empty;
        }

        private static string GetStartupLoadingCoverInitialDirectory(string path)
        {
            if (!string.IsNullOrEmpty(path))
            {
                var sourcePath = SudTemplateConverter.GetStartupLoadingCoverSourcePath(path);
                var directory = Path.GetDirectoryName(sourcePath);
                if (!string.IsNullOrEmpty(directory) && Directory.Exists(directory))
                {
                    return directory;
                }
            }

            return Directory.GetParent(Application.dataPath)?.FullName ?? Application.dataPath;
        }

        private void DrawPackageOptimization()
        {
            EditorGUILayout.LabelField("游戏资源配置", EditorStyles.boldLabel);

            EditorGUI.indentLevel++;
            _useRemoteResources = EditorGUILayout.Toggle("使用远程资源", _useRemoteResources);
            if (_useRemoteResources)
            {
                _useDataCdn = EditorGUILayout.Popup("启动资源加载方式", _useDataCdn ? 0 : 1, DataLoadModes) == 0;
                _gameResourceCdnUrl = EditorGUILayout.TextField("游戏资源 CDN 地址", _gameResourceCdnUrl ?? string.Empty);
                DrawPredownloadSettings();

                if (_legacyCdnConflict)
                    EditorGUILayout.HelpBox("旧配置的两个 CDN 地址不同，请确认当前游戏资源 CDN 地址后重新导出。", MessageType.Warning);
                if (!string.IsNullOrWhiteSpace(_gameResourceCdnUrl))
                {
                    string normalizedUrl;
                    string errorMessage;
                    if (!TryNormalizeGameResourceCdnUrl(_gameResourceCdnUrl, out normalizedUrl, out errorMessage))
                        EditorGUILayout.HelpBox(errorMessage, MessageType.Warning);
                }
            }
            EditorGUI.indentLevel--;
        }

        private void DrawPredownloadSettings()
        {
            _predownloadStartupConcurrency = EditorGUILayout.IntSlider(new GUIContent("启动阶段下载并发",
                "Unity 初始化期间同时执行的资源下载任务上限。"), _predownloadStartupConcurrency, 1, 10);
            _predownloadRuntimeConcurrency = EditorGUILayout.IntSlider(new GUIContent("Unity 就绪后下载并发",
                "Unity 初始化完成后的下载任务上限；已经开始的任务继续完成。"), _predownloadRuntimeConcurrency, 1, 10);
            _bundleHashLength = EditorGUILayout.IntField(new GUIContent("Bundle 名称 Hash 长度(?)",
                "填写资源构建工具生成的 Bundle 文件名中 Hash 的十六进制字符数，默认 32；仅用于识别并清理同名旧版本，不会生成 Hash。"), _bundleHashLength);
            if (_bundleHashLength < 1)
                EditorGUILayout.HelpBox("Bundle 名称 Hash 长度必须大于 0。", MessageType.Warning);
            var files = EditorGUILayout.TextField(new GUIContent("预下载文件列表(?)",
                "用 ; 分隔多个关键词，不必写后缀。构建后匹配导出的 StreamingAssets 文件（包含 Addressables 构建结果）；无 / 时匹配文件名，有 / 时匹配相对路径。区分大小写，每个关键词取排序后的第一个匹配文件，构建后未匹配时导出报错。"),
                _predownloadFiles ?? string.Empty);
            if (!string.Equals(files, _predownloadFiles, StringComparison.Ordinal))
                _predownloadFilesEdited = true;
            _predownloadFiles = files;
            _enablePredownload = !string.IsNullOrWhiteSpace(_predownloadFiles);
            if (!_enablePredownload)
            {
                EditorGUILayout.HelpBox("留空则不自动预下载；仍可按需加载资源，或在导出的 preload_mainfest.json 手动填写启动列表。", MessageType.None);
            }
        }

        private void DrawScenesInBuild()
        {
            var scenes = EditorBuildSettings.scenes;
            _scenesFoldout = EditorGUILayout.Foldout(_scenesFoldout, $"Scenes In Build ({scenes.Length})", true, EditorStyles.foldoutHeader);

            if (!_scenesFoldout) return;

            if (scenes.Length == 0)
            {
                EditorGUILayout.HelpBox("没有场景。请添加当前打开的场景。", MessageType.Warning);
                DrawAddOpenScenesButton();
            }
            else
            {
                DrawAddOpenScenesButton();
                _scenesScrollPos = EditorGUILayout.BeginScrollView(_scenesScrollPos, EditorStyles.helpBox, GUILayout.Height(100));
                EditorGUI.indentLevel++;
                for (int i = 0; i < scenes.Length; i++)
                {
                    var scene = scenes[i];
                    var sceneName = Path.GetFileNameWithoutExtension(scene.path);
                    var label = $"{i}: {sceneName}";
                    if (string.IsNullOrEmpty(scene.path))
                        label = $"{i}: (Missing)";

                    var newEnabled = EditorGUILayout.ToggleLeft(label, scene.enabled);
                    if (newEnabled != scene.enabled)
                    {
                        scenes[i].enabled = newEnabled;
                        EditorBuildSettings.scenes = scenes;
                    }
                }
                EditorGUI.indentLevel--;
                EditorGUILayout.EndScrollView();
            }
        }

        private static void DrawAddOpenScenesButton()
        {
            EditorGUILayout.BeginHorizontal();
            GUILayout.FlexibleSpace();
            if (GUILayout.Button("添加当前打开场景", GUILayout.Width(140), GUILayout.Height(24)))
            {
                AddOpenScenesToBuildSettings();
            }
            EditorGUILayout.EndHorizontal();
        }

        private static void AddOpenScenesToBuildSettings()
        {
            var scenePaths = GetOpenScenePaths();
            if (scenePaths.Count == 0)
            {
                EditorUtility.DisplayDialog("添加当前打开场景", "当前没有可添加的已保存场景。请先保存场景。", "OK");
                return;
            }

            var existingScenes = EditorBuildSettings.scenes;
            var existingPaths = new HashSet<string>();
            var mergedScenes = new List<EditorBuildSettingsScene>(existingScenes);
            for (int i = 0; i < existingScenes.Length; i++)
            {
                if (!string.IsNullOrEmpty(existingScenes[i].path))
                {
                    existingPaths.Add(existingScenes[i].path);
                }
            }

            var addedCount = 0;
            for (int i = 0; i < scenePaths.Count; i++)
            {
                var path = scenePaths[i];
                if (existingPaths.Contains(path))
                {
                    continue;
                }

                mergedScenes.Add(new EditorBuildSettingsScene(path, true));
                existingPaths.Add(path);
                addedCount++;
            }

            if (addedCount == 0)
            {
                EditorUtility.DisplayDialog("添加当前打开场景", "当前打开的场景已在 Scenes In Build 中。", "OK");
                return;
            }

            EditorBuildSettings.scenes = mergedScenes.ToArray();
        }

        private static List<string> GetOpenScenePaths()
        {
            var scenePaths = new List<string>();
            for (int i = 0; i < EditorSceneManager.sceneCount; i++)
            {
                var scene = EditorSceneManager.GetSceneAt(i);
                if (!scene.IsValid() ||
                    string.IsNullOrEmpty(scene.path) ||
                    AssetDatabase.LoadAssetAtPath<SceneAsset>(scene.path) == null)
                {
                    continue;
                }

                scenePaths.Add(scene.path);
            }

            return scenePaths;
        }

        private void DrawOutputFolderSelector()
        {
            if (string.IsNullOrEmpty(_outputFolderAbsolutePath))
            {
                _outputFolderAbsolutePath = GetDefaultOutputFolder();
            }

            EditorGUILayout.BeginHorizontal();
            EditorGUILayout.LabelField("导出路径", GUILayout.Width(50));
            GUI.SetNextControlName("SudExporterOutputFolder");
            _outputFolderAbsolutePath = EditorGUILayout.TextField(_outputFolderAbsolutePath ?? string.Empty);

            var hasPath = !string.IsNullOrEmpty(_outputFolderAbsolutePath);
            EditorGUI.BeginDisabledGroup(!hasPath);
            if (GUILayout.Button("打开", GUILayout.Width(40)))
            {
                OpenOutputFolder();
            }
            EditorGUI.EndDisabledGroup();

            if (GUILayout.Button("选择", GUILayout.Width(40)))
            {
                var initialDirectory = string.IsNullOrEmpty(_outputFolderAbsolutePath) ? GetDefaultOutputFolder() : _outputFolderAbsolutePath;
                var selected = EditorUtility.OpenFolderPanel("Select Output Folder", initialDirectory, "");
                if (!string.IsNullOrEmpty(selected))
                {
                    GUI.FocusControl(null);
                    EditorGUIUtility.editingTextField = false;
                    _outputFolderAbsolutePath = selected;
                    if (_settings != null)
                    {
                        _settings.outputFolder = selected;
                        EditorUtility.SetDirty(_settings);
                        AssetDatabase.SaveAssets();
                    }
                    Repaint();
                }
            }
            EditorGUILayout.EndHorizontal();
        }

        private void SaveResourceSettings()
        {
            _settings.enablePredownload = _enablePredownload;
            _settings.bundleHashLength = _bundleHashLength;
            _settings.predownloadStartupConcurrency = _predownloadStartupConcurrency;
            _settings.predownloadRuntimeConcurrency = _predownloadRuntimeConcurrency;
            _settings.gameResourceCdnUrl = _gameResourceCdnUrl ?? string.Empty;
            _settings.gameResourceCdnMigrated = true;
            if (_predownloadFilesEdited || _enablePredownload)
                _settings.predownloadFiles = _predownloadFiles ?? string.Empty;
        }

        private static void DrawReflectionEnumField(System.Type type, string propertyName, string label)
        {
            var prop = type.GetProperty(propertyName, BindingFlags.Public | BindingFlags.Static);
            if (prop == null) return;

            var current = prop.GetValue(null);
            var newValue = EditorGUILayout.EnumPopup(label, (Enum)current);
            if (!newValue.Equals(current))
            {
                prop.SetValue(null, newValue);
            }
        }

        private void RunWebGLExportAndConvert()
        {
            var textureSubtargetProp = typeof(EditorUserBuildSettings).GetProperty("webGLBuildSubtarget",
                BindingFlags.Public | BindingFlags.Static);
            object originalTextureSubtarget = textureSubtargetProp?.GetValue(null);
            var originalCompressionFormat = PlayerSettings.WebGL.compressionFormat;
            var originalDataCaching = PlayerSettings.WebGL.dataCaching;

            try
            {
                var hasPredownloadFiles = !string.IsNullOrWhiteSpace(_predownloadFiles);
                _enablePredownload = hasPredownloadFiles;
                var outputRoot = string.IsNullOrEmpty(_outputFolderAbsolutePath)
                    ? GetDefaultOutputFolder()
                    : _outputFolderAbsolutePath;

                var normalizedCdnUrl = string.Empty;
                SudPredownloadExportOptions predownloadOptions = null;
                if (_useRemoteResources)
                {
                    string errorMessage;
                    if (!TryNormalizeGameResourceCdnUrl(_gameResourceCdnUrl, out normalizedCdnUrl, out errorMessage))
                    {
                        EditorUtility.DisplayDialog("游戏资源 CDN 地址无效", errorMessage, "OK");
                        return;
                    }
                    _gameResourceCdnUrl = normalizedCdnUrl;
                    if (_useDataCdn)
                        SudTemplateConverter.ValidateStartupLoadingCoverImage(_startupLoadingCoverImage);
                    if (_bundleHashLength < 1)
                        throw new ArgumentOutOfRangeException(nameof(_bundleHashLength), "Bundle 名称 Hash 长度必须大于 0。");
                    // 文件匹配由转换阶段检查本次 WebGL 构建输出，包含 Unity 自动加入的 Addressables 文件。
                    predownloadOptions = new SudPredownloadExportOptions
                    {
                        enabled = _enablePredownload,
                        startupConcurrency = _predownloadStartupConcurrency,
                        runtimeConcurrency = _predownloadRuntimeConcurrency,
                        cdnBaseUrl = normalizedCdnUrl,
                        fileList = _predownloadFiles,
                        bundleHashLength = _bundleHashLength,
                        legacyResources = _enablePredownload ? _settings?.predownloadResources : null,
                    };
                }

                if (_settings != null)
                {
                    _settings.developmentBuild = _developmentBuild;
                    _settings.runtimeLogLevel = _runtimeLogLevel;
                    _settings.outputFolder = _outputFolderAbsolutePath ?? string.Empty;
                    _settings.useRemoteResources = _useRemoteResources;
                    _settings.useDataCdn = _useDataCdn;
                    _settings.startupLoadingCoverImage = _startupLoadingCoverImage ?? string.Empty;
                    SaveResourceSettings();
                    EditorUtility.SetDirty(_settings);
                    AssetDatabase.SaveAssets();
                }

                if (!Directory.Exists(outputRoot))
                {
                    Directory.CreateDirectory(outputRoot);
                }

                var webglDir = Path.Combine(outputRoot, "webgl");
                var sudDir = Path.Combine(outputRoot, "sud");
                var remoteAssetsDir = Path.Combine(outputRoot, "remote-assets");

                // ============================================
                // 第一次构建：标准 Unity WebGL 项目
                // ============================================
                Debug.Log("[SudExporter] Step 1: Building standard Unity WebGL project...");

                var buildOptions = BuildOptions.None;
                if (_developmentBuild)
                {
                    buildOptions |= BuildOptions.Development;
                }
                buildOptions |= BuildOptions.CleanBuildCache;

                PlayerSettings.WebGL.compressionFormat = WebGLCompressionFormat.Disabled;
                PlayerSettings.WebGL.dataCaching = false;

                var enabledScenes = EditorBuildSettings.scenes;
                var scenePaths = new List<string>();
                for (int i = 0; i < enabledScenes.Length; i++)
                {
                    var scene = enabledScenes[i];
                    if (scene.enabled &&
                        !string.IsNullOrEmpty(scene.path) &&
                        AssetDatabase.LoadAssetAtPath<SceneAsset>(scene.path) != null)
                    {
                        scenePaths.Add(scene.path);
                    }
                }
                if (scenePaths.Count == 0)
                {
                    EditorUtility.DisplayDialog("Export WebGL", "无可用场景可导出。请先在 Scenes In Build 中添加当前打开场景。", "OK");
                    return;
                }

                if (!Directory.Exists(webglDir)) Directory.CreateDirectory(webglDir);

                var buildPlayerOptions = new BuildPlayerOptions
                {
                    scenes = scenePaths.ToArray(),
                    locationPathName = webglDir,
                    target = BuildTarget.WebGL,
                    options = buildOptions
                };

                buildPlayerOptions.extraScriptingDefines = new[]
                {
                    GetRuntimeLogDefine(_runtimeLogLevel),
                };

                var subtargetProp = typeof(BuildPlayerOptions).GetProperty("subtarget");
                if (subtargetProp != null && textureSubtargetProp != null)
                {
                    var value = (int)textureSubtargetProp.GetValue(null);
                    subtargetProp.SetValue(buildPlayerOptions, value);
                    Debug.Log($"[SudExporter] Texture subtarget: {textureSubtargetProp.GetValue(null)}");
                }

                Debug.Log($"[SudExporter] Building to: {webglDir}");
                var report = BuildPipeline.BuildPlayer(buildPlayerOptions);
                var summary = report.summary;

                if (summary.result != BuildResult.Succeeded)
                {
                    Debug.LogError($"[SudExporter] WebGL 导出失败。状态: {summary.result}, 错误数: {summary.totalErrors}");
                    ShowNotification(new GUIContent("WebGL 导出失败。详见 Console"));
                    return;
                }

                Debug.Log($"[SudExporter] ✅ Step 1 completed: Standard WebGL project built");
                Debug.Log($"[SudExporter] Output: {webglDir}");

                // ============================================
                // 第二次转换：生成 SUD 项目
                // ============================================
                Debug.Log("[SudExporter] ╔════════════════════════════════════════════════════════╗");
                Debug.Log("[SudExporter] ║ Step 2: Converting to SUD (main.js entry)            ║");
                Debug.Log("[SudExporter] ╚════════════════════════════════════════════════════════╝");

                SudTemplateConverter.ConvertWebGLToMinigameUsingTemplate(
                    webglDir,
                    sudDir,
                    _useRemoteResources,
                    _useDataCdn,
                    normalizedCdnUrl,
                    remoteAssetsDir,
                    _startupLoadingCoverImage,
                    predownloadOptions);

                Debug.Log($"[SudExporter] ╔════════════════════════════════════════════════════════╗");
                Debug.Log($"[SudExporter] ║           🎉 Export Completed Successfully!          ║");
                Debug.Log($"[SudExporter] ╚════════════════════════════════════════════════════════╝");
                Debug.Log($"[SudExporter] 📦 WebGL: {webglDir}");
                Debug.Log($"[SudExporter] 📦 SUD:   {sudDir}");
                Debug.Log($"[SudExporter] 🚀 Entry: main.js");
                Debug.Log($"[SudExporter] 🔧 Mode: {(_developmentBuild ? "Development" : "Release")}");
                Debug.Log($"[SudExporter] 📝 Runtime Log Level: {_runtimeLogLevel}");
                if (_useRemoteResources)
                {
                    var hasRemoteStreamingAssets = SudTemplateConverter.HasStreamingAssetsFiles(Path.Combine(remoteAssetsDir, "StreamingAssets"));
                    if (_useDataCdn || hasRemoteStreamingAssets)
                    {
                        var uploadDirectory = _useDataCdn && hasRemoteStreamingAssets
                            ? remoteAssetsDir
                            : Path.Combine(remoteAssetsDir, _useDataCdn ? "Build" : "StreamingAssets");
                        var dataUrl = _useDataCdn
                            ? $"\n\n确认启动数据可访问：\n{normalizedCdnUrl}Build/{SudTemplateConverter.LastDataFileName}"
                            : string.Empty;
                        var uploadInstructions = _useDataCdn && hasRemoteStreamingAssets
                            ? "将 remote-assets 下的 Build/ 和 StreamingAssets/ 上传到该 CDN 根地址"
                            : _useDataCdn
                                ? "将 remote-assets 下的 Build/ 上传到该 CDN 根地址"
                                : "将 remote-assets 下的 StreamingAssets/ 上传到该 CDN 根地址";
                        Debug.Log($"[SudExporter] 🌐 Upload {uploadDirectory} to CDN root: {normalizedCdnUrl}");
                        EditorUtility.DisplayDialog(
                            "导出完成",
                            $"主包目录：\n{sudDir}\n\n需要上传的资源：\n{uploadDirectory}\n\n{uploadInstructions}：\n{normalizedCdnUrl}{dataUrl}",
                            "OK");
                    }
                }
                ShowNotification(new GUIContent("✅ Export completed!"));
                EditorUtility.RevealInFinder(sudDir);
            }
            catch (Exception ex)
            {
                Debug.LogError($"[SudExporter] 导出异常: {ex}");
                ShowNotification(new GUIContent("导出异常。详见 Console"));
            }
            finally
            {
                PlayerSettings.WebGL.compressionFormat = originalCompressionFormat;
                PlayerSettings.WebGL.dataCaching = originalDataCaching;
                if (textureSubtargetProp != null && originalTextureSubtarget != null)
                {
                    textureSubtargetProp.SetValue(null, originalTextureSubtarget);
                }
            }
        }

        private static bool TryNormalizeGameResourceCdnUrl(string input, out string normalizedUrl, out string errorMessage)
        {
            normalizedUrl = string.Empty;
            errorMessage = string.Empty;

            var value = (input ?? string.Empty).Trim();
            if (string.IsNullOrEmpty(value))
            {
                errorMessage = "游戏资源 CDN 地址不能为空。";
                return false;
            }

            Uri uri;
            if (!Uri.TryCreate(value, UriKind.Absolute, out uri) ||
                (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps))
            {
                errorMessage = "游戏资源 CDN 地址必须是合法的 http:// 或 https:// URL。";
                return false;
            }

            if (!string.IsNullOrEmpty(uri.Query) || !string.IsNullOrEmpty(uri.Fragment))
            {
                errorMessage = "CDN 根地址不能包含查询参数或 # 片段。";
                return false;
            }

            var lastSegment = Path.GetFileName(uri.AbsolutePath.Replace('\\', '/')).ToLowerInvariant();
            if (lastSegment.EndsWith(".data") ||
                lastSegment.EndsWith(".wasm") ||
                lastSegment.EndsWith(".framework.js") ||
                lastSegment.EndsWith(".loader.js") ||
                lastSegment.EndsWith(".js"))
            {
                errorMessage = "请填写 CDN 根地址，不要填写具体的 game.data、wasm、framework.js 或 loader.js 文件地址。";
                return false;
            }

            normalizedUrl = value.EndsWith("/") ? value : value + "/";
            return true;
        }

        private void OpenOutputFolder()
        {
            var path = string.IsNullOrEmpty(_outputFolderAbsolutePath) ? GetDefaultOutputFolder() : _outputFolderAbsolutePath;
            try
            {
                if (!Directory.Exists(path))
                {
                    Directory.CreateDirectory(path);
                }
                EditorUtility.RevealInFinder(path);
            }
            catch (Exception ex)
            {
                Debug.LogError($"[SudExporter] Open folder failed: {ex}");
            }
        }

        private static string GetDefaultOutputFolder()
        {
            var root = GetProjectRoot();
            return Path.Combine(root, "Build");
        }

        private static string GetProjectRoot()
        {
            var dataPath = Application.dataPath;
            var root = Directory.GetParent(dataPath)?.FullName ?? dataPath;
            return root;
        }

        private void ClearBuildCache()
        {
            try
            {
                Debug.Log("[SudExporter] Clearing build cache...");

                var buildCachePath = Path.Combine(GetProjectRoot(), "Library", "BuildCache");
                if (Directory.Exists(buildCachePath))
                {
                    Directory.Delete(buildCachePath, true);
                    Debug.Log("[SudExporter] Cleared Unity BuildCache");
                }

                var il2cppCachePath = Path.Combine(GetProjectRoot(), "Library", "il2cpp_cache");
                if (Directory.Exists(il2cppCachePath))
                {
                    Directory.Delete(il2cppCachePath, true);
                    Debug.Log("[SudExporter] Cleared IL2CPP cache");
                }

                var webglCachePath = Path.Combine(GetProjectRoot(), "Library", "PlayerDataCache", "WebGL");
                if (Directory.Exists(webglCachePath))
                {
                    Directory.Delete(webglCachePath, true);
                    Debug.Log("[SudExporter] Cleared WebGL player data cache");
                }

                AssetDatabase.Refresh();

                Debug.Log("[SudExporter] Build cache cleared successfully");
                ShowNotification(new GUIContent("Build cache cleared"));
            }
            catch (Exception ex)
            {
                Debug.LogError($"[SudExporter] Failed to clear build cache: {ex}");
                ShowNotification(new GUIContent("Failed to clear cache"));
            }
        }
    }
}
