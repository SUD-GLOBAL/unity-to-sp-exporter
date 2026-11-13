using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using UnityEditor;
using UnityEngine;

namespace Sud.UnityExporter
{
    [Serializable]
    public sealed class SudPredownloadManifest
    {
        public string[] preloadDataList = Array.Empty<string>();
        public string preloadListUrl = string.Empty;
        public int schemaVersion = 1;
        public string cdnBaseUrl = string.Empty;
        public int startupConcurrency = 10;
        public int runtimeConcurrency = 1;
        public int bundleHashLength = 32;
        public List<SudPredownloadManifestResource> resources = new List<SudPredownloadManifestResource>();
    }

    [Serializable]
    public sealed class SudPredownloadManifestResource
    {
        public string id = string.Empty;
        public string relativePath = string.Empty;
        public string url = string.Empty;
        public string[] aliases = Array.Empty<string>();
        public long size;
        public string cacheKey = string.Empty;
    }

    public sealed class SudPredownloadExportOptions
    {
        public bool enabled;
        public string cdnBaseUrl = string.Empty;
        public string fileList = string.Empty;
        public IList<SudPredownloadResourceSetting> legacyResources;
        public int startupConcurrency = 10;
        public int runtimeConcurrency = 1;
        public int bundleHashLength = 32;
    }

    public static class SudTemplateConverter
    {
        private static string TemplateSourceDir => SudPluginPaths.RuntimeDirectory;
        private const int MaxStartupLoadingCoverSize = 2048;

        public static string LastDataFileName { get; private set; }

        public static void ConvertWebGLToMinigameUsingTemplate(string webglDir, string minigameDir,
            bool useRemoteResources, bool useDataCdn, string unityDataCdnUrl, string remoteAssetsDir,
            string startupLoadingCoverImage, SudPredownloadExportOptions predownloadOptions)
        {
            if (string.IsNullOrEmpty(webglDir) || string.IsNullOrEmpty(minigameDir))
            {
                throw new ArgumentException("Invalid path");
            }

            if (!Directory.Exists(webglDir))
            {
                throw new DirectoryNotFoundException($"WebGL directory not found: {webglDir}");
            }

            var templateSourceDir = TemplateSourceDir;
            useDataCdn = useRemoteResources && useDataCdn;
            SudPredownloadManifest predownloadManifest = null;
            if (useRemoteResources)
            {
                unityDataCdnUrl = NormalizePredownloadCdnUrl(unityDataCdnUrl);
                if (useDataCdn)
                    ValidateStartupLoadingCoverImage(startupLoadingCoverImage);
                if (string.IsNullOrEmpty(remoteAssetsDir))
                    throw new ArgumentException("Remote assets directory is required when CDN resources are exported.");
                predownloadManifest = BuildPredownloadManifest(webglDir, predownloadOptions, unityDataCdnUrl);
            }
            var streamingAssetsSrc = Path.GetFullPath(Path.Combine(webglDir, "StreamingAssets"));
            var streamingFiles = GetStreamingAssetsFiles(streamingAssetsSrc);

            if (Directory.Exists(minigameDir))
            {
                FileUtil.DeleteFileOrDirectory(minigameDir);
            }

            if (!string.IsNullOrEmpty(remoteAssetsDir) && Directory.Exists(remoteAssetsDir))
            {
                FileUtil.DeleteFileOrDirectory(remoteAssetsDir);
            }

            Debug.Log($"[SudTemplateConverter] Starting conversion...");
            Debug.Log($"[SudTemplateConverter] Template source: {templateSourceDir}");
            Debug.Log($"[SudTemplateConverter] WebGL source: {webglDir}");
            Debug.Log($"[SudTemplateConverter] Minigame target: {minigameDir}");

            // Step 1: Find WebGL build artifacts.
            string dataFileName, frameworkJsFileName, loaderJsFileName, wasmFileName;
            FindBuildArtifacts(webglDir, out dataFileName, out frameworkJsFileName, out loaderJsFileName, out wasmFileName);
            LastDataFileName = dataFileName ?? string.Empty;

            if (string.IsNullOrEmpty(loaderJsFileName))
            {
                throw new FileNotFoundException($"Unity loader file not found in WebGL Build directory: {Path.Combine(webglDir, "Build")}");
            }

            if (useDataCdn && string.IsNullOrEmpty(dataFileName))
            {
                throw new FileNotFoundException("Unity data file not found in WebGL Build directory.");
            }

            var dataFileMd5 = string.Empty;
            long dataFileSize = 0;
            if (useDataCdn)
            {
                var dataPath = Path.Combine(webglDir, "Build", dataFileName);
                dataFileSize = new FileInfo(dataPath).Length;
                using (var stream = File.OpenRead(dataPath))
                using (var md5 = MD5.Create())
                    dataFileMd5 = BitConverter.ToString(md5.ComputeHash(stream)).Replace("-", "").ToLowerInvariant();
            }

            foreach (var sourcePath in streamingFiles)
            {
                var relativePath = sourcePath.Substring(streamingAssetsSrc.Length + 1);
                var destinationPath = Path.Combine(useRemoteResources ? remoteAssetsDir : minigameDir, "StreamingAssets", relativePath);
                Directory.CreateDirectory(Path.GetDirectoryName(destinationPath));
                File.Copy(sourcePath, destinationPath, true);
            }

            // Step 2: Copy template files.
            CopyTemplateFiles(minigameDir, useRemoteResources, useDataCdn);
            if (useRemoteResources)
                File.WriteAllText(Path.Combine(minigameDir, "preload_mainfest.json"),
                    JsonUtility.ToJson(predownloadManifest, true), new UTF8Encoding(false));

            var startupLoadingCoverUrl = useDataCdn
                ? CopyStartupLoadingCoverImage(startupLoadingCoverImage, minigameDir)
                : string.Empty;

            // Step 3: Replace placeholders in main.js
            ReplaceMainJsPlaceholders(minigameDir, useRemoteResources, useDataCdn, unityDataCdnUrl,
                dataFileName, frameworkJsFileName, loaderJsFileName, wasmFileName, startupLoadingCoverUrl,
                dataFileMd5, dataFileSize);

            // Step 4: Copy Build files
            CopyBuildFiles(webglDir, minigameDir, useDataCdn, dataFileName, remoteAssetsDir);
            ExposeCreateUnityInstance(minigameDir, loaderJsFileName);

            var manifestPath = Path.Combine(minigameDir, "manifest.json");
            if (!File.Exists(manifestPath))
            {
                File.WriteAllText(manifestPath, "{}", new UTF8Encoding(false));
                Debug.Log($"[SudTemplateConverter] ✅ Created default manifest.json");
            }

            Debug.Log($"[SudTemplateConverter] ✅ Conversion completed successfully!");
            Debug.Log($"[SudTemplateConverter] Output: {minigameDir}");
            Debug.Log($"[SudTemplateConverter] Entry: main.js");
        }

        public static void ValidateStartupLoadingCoverImage(string assetPath)
        {
            if (string.IsNullOrWhiteSpace(assetPath))
            {
                throw new ArgumentException(".data 使用 CDN 时必须配置启动 Loading 背景图。", nameof(assetPath));
            }

            var sourcePath = GetStartupLoadingCoverSourcePath(assetPath);
            if (!File.Exists(sourcePath))
            {
                throw new FileNotFoundException($"Startup loading cover image not found: {assetPath}");
            }

            Texture2D texture = null;
            try
            {
                var bytes = File.ReadAllBytes(sourcePath);
                texture = new Texture2D(2, 2, TextureFormat.RGBA32, false);
                if (!ImageConversion.LoadImage(texture, bytes))
                {
                    throw new Exception($"Startup loading cover image format is invalid: {assetPath}");
                }

                if (texture.width > MaxStartupLoadingCoverSize || texture.height > MaxStartupLoadingCoverSize)
                {
                    throw new Exception($"封面图宽高不可超过{MaxStartupLoadingCoverSize}");
                }
            }
            finally
            {
                if (texture != null)
                {
                    UnityEngine.Object.DestroyImmediate(texture);
                }
            }
        }

        public static string GetStartupLoadingCoverSourcePath(string imagePath)
        {
            if (string.IsNullOrEmpty(imagePath))
            {
                return string.Empty;
            }

            if (Path.IsPathRooted(imagePath))
            {
                return Path.GetFullPath(imagePath);
            }

            var projectRoot = Directory.GetParent(Application.dataPath)?.FullName ?? Application.dataPath;
            return Path.GetFullPath(Path.Combine(projectRoot, imagePath));
        }

        private static void CopyTemplateFiles(string targetDir, bool useRemoteResources, bool useDataCdn)
        {
            Debug.Log($"[SudTemplateConverter] Step 1: Copying template files from sud-default/");

            Directory.CreateDirectory(targetDir);

            CopyRuntimeTemplateFile(targetDir, "main.js");
            CopyRuntimeTemplateFile(targetDir, "unity_adapter.js");
            CopyRuntimeTemplateFile(targetDir, "api-bridge.js");

            var splitBridgeSourceDir = Path.Combine(TemplateSourceDir, "SUD", "tlv-bridge-js");
            if (Directory.Exists(splitBridgeSourceDir))
            {
                CopyDirectory(Path.Combine(TemplateSourceDir, "SUD"), Path.Combine(targetDir, "SUD"), path =>
                    string.Equals(Path.GetExtension(path), ".js", StringComparison.OrdinalIgnoreCase));
                Debug.Log("[SudTemplateConverter] ✅ Copied SUD/");
            }
            if (useDataCdn)
                CopyRuntimeTemplateFile(targetDir, "startup_loading.js");
            if (useRemoteResources)
                CopyRuntimeTemplateFile(targetDir, "predownload.js");
        }

        private static void CopyRuntimeTemplateFile(string targetDir, string fileName)
        {
            var sourcePath = Path.Combine(TemplateSourceDir, fileName);
            var targetPath = Path.Combine(targetDir, fileName);
            if (!File.Exists(sourcePath))
            {
                throw new FileNotFoundException($"{fileName} not found in template: {sourcePath}");
            }
            File.Copy(sourcePath, targetPath, true);
            Debug.Log($"[SudTemplateConverter] ✅ Copied {fileName}");
        }

        private static void ReplaceMainJsPlaceholders(string targetDir, bool useRemoteResources, bool useDataCdn, string unityDataCdnUrl,
            string dataFileName, string frameworkJsFileName, string loaderJsFileName, string wasmFileName, string startupLoadingCoverUrl,
            string dataFileMd5, long dataFileSize)
        {
            Debug.Log($"[SudTemplateConverter] Step 3: Replacing placeholders in main.js");

            var mainJsPath = Path.Combine(targetDir, "main.js");
            if (!File.Exists(mainJsPath))
            {
                throw new FileNotFoundException($"main.js not found: {mainJsPath}");
            }

            var content = File.ReadAllText(mainJsPath, Encoding.UTF8);
            var originalLength = content.Length;
            var dataUrl = useDataCdn
                ? (unityDataCdnUrl ?? string.Empty) + "Build/" + (dataFileName ?? string.Empty)
                : "Build/" + (dataFileName ?? string.Empty);

            content = content.Replace("$LOADER_FILENAME", loaderJsFileName ?? "");
            content = content.Replace("$DATA_URL", EscapeJavaScriptString(dataUrl));
            content = content.Replace("$DATA_FILE_MD5", dataFileMd5);
            content = content.Replace("$DATA_FILE_SIZE", dataFileSize.ToString(System.Globalization.CultureInfo.InvariantCulture));
            content = content.Replace("$USE_REMOTE_RESOURCES", useRemoteResources ? "true" : "false");
            content = content.Replace("$USE_REMOTE_DATA", useDataCdn ? "true" : "false");
            content = content.Replace("$FRAMEWORK_FILENAME", frameworkJsFileName ?? "");
            content = content.Replace("$CODE_FILENAME", wasmFileName ?? "");
            content = content.Replace("$STARTUP_LOADING_COVER_URL", EscapeJavaScriptString(startupLoadingCoverUrl));
            content = content.Replace("$UNITY_VERSION", EscapeJavaScriptString(Application.unityVersion));

            File.WriteAllText(mainJsPath, content, Encoding.UTF8);

            Debug.Log($"[SudTemplateConverter] ✅ Placeholders replaced. Size: {originalLength} -> {content.Length}");
        }

        private static string CopyStartupLoadingCoverImage(string assetPath, string minigameDir)
        {
            var sourcePath = GetStartupLoadingCoverSourcePath(assetPath);
            var imagesDir = Path.Combine(minigameDir, "images");
            Directory.CreateDirectory(imagesDir);

            var fileName = Path.GetFileName(sourcePath);
            var targetPath = Path.Combine(imagesDir, fileName);
            File.Copy(sourcePath, targetPath, true);

            Debug.Log($"[SudTemplateConverter] ✅ Copied startup loading cover: images/{fileName}");
            return "images/" + fileName;
        }

        private static void CopyBuildFiles(string webglDir, string minigameDir,
            bool useDataCdn, string dataFileName, string remoteAssetsDir)
        {
            Debug.Log($"[SudTemplateConverter] Step 4: Copying Build files");

            var buildSrc = Path.Combine(webglDir, "Build");
            var buildDst = Path.Combine(minigameDir, "Build");

            if (!Directory.Exists(buildSrc))
            {
                throw new DirectoryNotFoundException($"Build directory not found: {buildSrc}");
            }

            CopyDirectory(buildSrc, buildDst, filePath => !useDataCdn || Path.GetFileName(filePath) != dataFileName);
            Debug.Log($"[SudTemplateConverter] ✅ Copied Build directory");

            if (useDataCdn)
            {
                var remoteBuildDir = Path.Combine(remoteAssetsDir, "Build");
                Directory.CreateDirectory(remoteBuildDir);
                File.Copy(Path.Combine(buildSrc, dataFileName), Path.Combine(remoteBuildDir, dataFileName), true);
                Debug.Log($"[SudTemplateConverter] ✅ Copied remote Unity data: {Path.Combine(remoteBuildDir, dataFileName)}");
            }
        }

        public static string NormalizePredownloadResourcePath(string value)
        {
            var rawPath = (value ?? string.Empty).Trim();
            if (string.IsNullOrEmpty(rawPath) || Path.IsPathRooted(rawPath) || rawPath.IndexOf(':') >= 0)
            {
                throw new ArgumentException($"Invalid StreamingAssets relative path: {value}");
            }

            var path = rawPath.Replace('\\', '/');
            if (path.StartsWith("StreamingAssets/", StringComparison.Ordinal))
            {
                path = path.Substring("StreamingAssets/".Length);
            }

            var segments = path.Split('/');
            if (segments.Any(segment => string.IsNullOrEmpty(segment) || segment == "." || segment == ".."))
            {
                throw new ArgumentException($"StreamingAssets path traversal is not allowed: {value}");
            }

            return string.Join("/", segments);
        }

        public static string NormalizePredownloadCdnUrl(string value)
        {
            Uri uri;
            var trimmed = (value ?? string.Empty).Trim();
            if (!Uri.TryCreate(trimmed, UriKind.Absolute, out uri) ||
                (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps))
            {
                throw new ArgumentException("游戏资源 CDN 地址必须是合法的 http:// 或 https:// URL。");
            }
            if (!string.IsNullOrEmpty(uri.Query) || !string.IsNullOrEmpty(uri.Fragment))
            {
                throw new ArgumentException("游戏资源 CDN 根地址不能包含查询参数或 # 片段。");
            }
            return trimmed.EndsWith("/", StringComparison.Ordinal) ? trimmed : trimmed + "/";
        }

        private static SudPredownloadManifest BuildPredownloadManifest(
            string webglDir,
            SudPredownloadExportOptions options,
            string cdnBaseUrl)
        {
            options = options ?? new SudPredownloadExportOptions();
            if (options.bundleHashLength < 1)
                throw new ArgumentOutOfRangeException(nameof(options.bundleHashLength), "Bundle hash length must be positive.");
            if (options.startupConcurrency < 1 || options.startupConcurrency > 10 ||
                options.runtimeConcurrency < 1 || options.runtimeConcurrency > 10)
                throw new ArgumentOutOfRangeException(nameof(options), "Predownload concurrency must be between 1 and 10.");
            var manifest = new SudPredownloadManifest
            {
                cdnBaseUrl = cdnBaseUrl,
                startupConcurrency = options.startupConcurrency,
                runtimeConcurrency = options.runtimeConcurrency,
                bundleHashLength = options.bundleHashLength,
            };
            if (!options.enabled) return manifest;

            var hasFiles = !string.IsNullOrWhiteSpace(options.fileList);
            var streamingAssetsRoot = Path.Combine(webglDir, "StreamingAssets");
            var paths = hasFiles ? MatchPredownloadFiles(options.fileList, streamingAssetsRoot) : new List<string>();
            var legacyByPath = new Dictionary<string, SudPredownloadResourceSetting>(StringComparer.Ordinal);
            foreach (var legacy in options.legacyResources ?? Array.Empty<SudPredownloadResourceSetting>())
            {
                if (legacy == null || string.IsNullOrWhiteSpace(legacy.id) || string.IsNullOrWhiteSpace(legacy.streamingAssetsPath)) continue;
                try
                {
                    var path = NormalizePredownloadResourcePath(legacy.streamingAssetsPath);
                    if (!legacyByPath.ContainsKey(path)) legacyByPath.Add(path, legacy);
                }
                catch (ArgumentException) { }
            }
            var usedIds = new HashSet<string>(StringComparer.Ordinal);

            foreach (var relativePath in paths)
            {
                var sourcePath = Path.Combine(streamingAssetsRoot, relativePath.Replace('/', Path.DirectorySeparatorChar));
                var remoteRelativePath = "StreamingAssets/" + relativePath;
                var fileInfo = new FileInfo(sourcePath);
                SudPredownloadResourceSetting legacy;
                legacyByPath.TryGetValue(relativePath, out legacy);
                var id = legacy != null ? legacy.id.Trim() : relativePath;
                if (!usedIds.Add(id))
                {
                    id = relativePath;
                    if (!usedIds.Add(id)) throw new ArgumentException($"预下载资源 ID 冲突：{relativePath}");
                }
                manifest.resources.Add(new SudPredownloadManifestResource
                {
                    id = id,
                    relativePath = remoteRelativePath,
                    url = cdnBaseUrl + string.Join("/", remoteRelativePath.Split('/').Select(Uri.EscapeDataString)),
                    size = fileInfo.Length,
                    cacheKey = remoteRelativePath,
                });
            }

            manifest.preloadDataList = manifest.resources.Select(resource => resource.id).ToArray();
            Debug.Log($"[SudTemplateConverter] 本次构建匹配的预下载资源（{paths.Count} 个）：\n{string.Join("\n", paths)}");
            return manifest;
        }

        private static string[] GetStreamingAssetsFiles(string directory)
        {
            return Directory.Exists(directory)
                ? Directory.GetFiles(directory, "*", SearchOption.AllDirectories)
                    .Where(path => !path.EndsWith(".meta", StringComparison.OrdinalIgnoreCase) &&
                        Path.GetFileName(path) != ".DS_Store")
                    .OrderBy(path => path, StringComparer.Ordinal).ToArray()
                : Array.Empty<string>();
        }

        public static bool HasStreamingAssetsFiles(string directory) => GetStreamingAssetsFiles(directory).Length > 0;

        public static List<string> MatchPredownloadFiles(string fileList, string streamingAssetsRoot)
        {
            var patterns = (fileList ?? string.Empty).Split(';')
                .Select(value => value.Trim().Replace('\\', '/'))
                .Where(value => value.Length > 0)
                .ToArray();
            if (patterns.Length == 0)
            {
                throw new ArgumentException("请填写至少一个预下载文件名或相对路径。");
            }
            if (!Directory.Exists(streamingAssetsRoot))
            {
                throw new DirectoryNotFoundException($"StreamingAssets 目录不存在：{streamingAssetsRoot}");
            }

            var files = GetStreamingAssetsFiles(streamingAssetsRoot)
                .Select(path => path.Substring(streamingAssetsRoot.TrimEnd(Path.DirectorySeparatorChar).Length + 1).Replace('\\', '/'))
                .OrderBy(path => path, StringComparer.Ordinal)
                .ToArray();
            var selected = new List<string>();
            var seen = new HashSet<string>(StringComparer.Ordinal);
            foreach (var rawPattern in patterns)
            {
                var pattern = rawPattern.TrimStart('/');
                if (pattern.StartsWith("StreamingAssets/", StringComparison.OrdinalIgnoreCase))
                    pattern = pattern.Substring("StreamingAssets/".Length);
                var matchPath = pattern.Contains("/");
                var match = files.FirstOrDefault(path =>
                    (matchPath ? path : Path.GetFileName(path)).Contains(pattern));
                if (match == null)
                    throw new ArgumentException($"预下载列表中的“{rawPattern}”未匹配到 StreamingAssets 文件。");
                if (seen.Add(match)) selected.Add(match);
            }
            return selected;
        }

        private static void ExposeCreateUnityInstance(string minigameDir, string loaderJsFileName)
        {
            var loaderPath = Path.Combine(minigameDir, "Build", loaderJsFileName);
            const string exportStatement = "window.createUnityInstance = createUnityInstance;";
            var content = File.ReadAllText(loaderPath, Encoding.UTF8);
            if (content.Contains(exportStatement))
            {
                return;
            }

            File.AppendAllText(loaderPath, Environment.NewLine + exportStatement + Environment.NewLine, new UTF8Encoding(false));
            Debug.Log($"[SudTemplateConverter] ✅ Exported window.createUnityInstance in {loaderJsFileName}");
        }

        private static void FindBuildArtifacts(string webglDir, out string dataFileName, out string frameworkJsFileName, out string loaderJsFileName, out string wasmFileName)
        {
            dataFileName = frameworkJsFileName = loaderJsFileName = wasmFileName = null;

            var buildPath = Path.Combine(webglDir, "Build");
            if (!Directory.Exists(buildPath))
            {
                Debug.LogError($"[SudTemplateConverter] Build directory not found: {buildPath}");
                return;
            }

            var buildFiles = Directory.GetFiles(buildPath);
            foreach (var filePath in buildFiles)
            {
                var fileName = Path.GetFileName(filePath);

                if (fileName.Contains(".data"))
                    dataFileName = fileName;
                if (fileName.Contains(".framework.js"))
                    frameworkJsFileName = fileName;
                if (fileName.Contains(".loader.js"))
                    loaderJsFileName = fileName;
                if (fileName.Contains(".wasm"))
                    wasmFileName = fileName;
            }

            Debug.Log($"[SudTemplateConverter] Found artifacts: data={dataFileName}, framework={frameworkJsFileName}, loader={loaderJsFileName}, wasm={wasmFileName}");
        }

        private static void CopyDirectory(string sourceDir, string destDir, Func<string, bool> shouldCopyFile)
        {
            foreach (var file in Directory.GetFiles(sourceDir))
            {
                if (shouldCopyFile != null && !shouldCopyFile(file))
                {
                    continue;
                }

                Directory.CreateDirectory(destDir);
                var fileName = Path.GetFileName(file);
                File.Copy(file, Path.Combine(destDir, fileName), true);
            }

            foreach (var subDir in Directory.GetDirectories(sourceDir))
            {
                var dirName = Path.GetFileName(subDir);
                CopyDirectory(subDir, Path.Combine(destDir, dirName), shouldCopyFile);
            }
        }

        private static string EscapeJavaScriptString(string value)
        {
            return (value ?? string.Empty)
                .Replace("\\", "\\\\")
                .Replace("\"", "\\\"")
                .Replace("\b", "\\b")
                .Replace("\f", "\\f")
                .Replace("\t", "\\t")
                .Replace("\r", "\\r")
                .Replace("\n", "\\n")
                .Replace("\u2028", "\\u2028")
                .Replace("\u2029", "\\u2029");
        }

    }
}
