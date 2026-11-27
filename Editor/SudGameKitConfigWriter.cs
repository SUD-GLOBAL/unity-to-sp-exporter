using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Reflection;
using System.Runtime.Serialization.Json;
using System.Text;
using System.Xml;
using System.Xml.Linq;
using UnityEditor;
using UnityEngine;

namespace Sud.UnityExporter
{
    public static class SudGameKitConfigWriter
    {
        public const string GameKitDirName = "sud-game-kit";
        public const string ConfigFileName = "config.json";
        internal const string WasmStartupFileName = "sud-wasm-startup.js";
        private const string ObsoleteMetadataFileName = "sud-export-metadata.json";
        private static readonly Encoding Utf8NoBom = new UTF8Encoding(false);

        public static void WriteGameKitConfig(
            string webglDir,
            SudExporterSettings settings,
            bool developmentBuild,
            string emscriptenArgs,
            bool useRemoteResources,
            bool useDataCdn,
            string gameResourceCdnUrl,
            string startupLoadingCoverImage)
        {
            using var exportFiles = SudExportFiles.Begin();
            var buildDir = Path.Combine(webglDir, "Build");
            if (!Directory.Exists(buildDir))
            {
                throw new DirectoryNotFoundException("WebGL Build directory not found: " + buildDir);
            }

            var wasmPath = FindSingleBuildFile(buildDir, ".wasm");
            var symbolsPath = FindSingleBuildFile(buildDir, ".symbols.json", allowMissing: developmentBuild);
            var dataPath = FindSingleBuildFile(buildDir, ".data");
            var loaderPath = FindSingleBuildFile(buildDir, ".loader.js");
            var frameworkPath = FindSingleBuildFile(buildDir, ".framework.js");
            ValidateMatchingBuildFileNames(wasmPath, symbolsPath, dataPath, loaderPath, frameworkPath);
            var wasmInfo = SudExportFiles.InspectWasm(wasmPath);
            if (symbolsPath != null)
                ValidateSymbols(symbolsPath, wasmInfo);

            var gameKitDir = Path.Combine(webglDir, GameKitDirName);
            if (!Directory.Exists(gameKitDir))
            {
                Directory.CreateDirectory(gameKitDir);
            }

            var json = BuildGameKitConfigJson(
                settings,
                developmentBuild,
                emscriptenArgs,
                useRemoteResources,
                useDataCdn,
                gameResourceCdnUrl,
                startupLoadingCoverImage,
                wasmPath,
                symbolsPath,
                dataPath,
                loaderPath,
                frameworkPath,
                Directory.GetParent(Path.GetFullPath(webglDir))?.FullName);

            var configPath = Path.Combine(gameKitDir, ConfigFileName);
            File.WriteAllText(configPath, json, Utf8NoBom);
            File.Delete(Path.Combine(webglDir, ObsoleteMetadataFileName));
            Debug.Log("[SudExporter] Wrote game kit config: " + configPath);
        }

        public static void CopyGameKitToSud(string webglDir, string sudDir)
        {
            using var exportFiles = SudExportFiles.Begin();
            var relativePath = Path.Combine(GameKitDirName, ConfigFileName);
            XDocument document;
            using (var stream = File.OpenRead(Path.Combine(webglDir, relativePath)))
            using (var reader = JsonReaderWriterFactory.CreateJsonReader(stream, XmlDictionaryReaderQuotas.Max))
                document = XDocument.Load(reader);

            // LINQ to XML represents an explicit empty closing tag as empty text.
            // The JSON writer permits text only in scalar values.
            foreach (var element in document.Descendants())
            {
                var type = (string)element.Attribute("type");
                if (type == "object")
                {
                    var keys = new HashSet<string>(StringComparer.Ordinal);
                    foreach (var member in element.Elements())
                        if (!keys.Add((string)member.Attribute("item") ?? member.Name.LocalName))
                            throw new InvalidDataException("Duplicate JSON member in " + relativePath);
                }
                if (!element.HasElements && (type == "null" || type == "object" || type == "array"))
                    element.RemoveNodes();
            }
            var files = document.Root.Element("launch");
            var buildDir = Path.Combine(sudDir, "Build");
            foreach (var role in new[] { "loader", "framework" })
            {
                var descriptor = files?.Element(role);
                if (descriptor == null)
                    throw new InvalidDataException("Missing " + role + " in " + relativePath);
                var file = FindSingleBuildFile(buildDir, "." + role + ".js");
                var fingerprint = SudExportFiles.Read(file);
                descriptor.SetElementValue("fileName", Path.GetFileName(file));
                descriptor.SetElementValue("fileSHA256", fingerprint.Sha256);
                descriptor.SetElementValue("fileMD5", fingerprint.Md5);
                descriptor.SetElementValue("fileSize", new FileInfo(file).Length);
                descriptor.Element("fileSize").SetAttributeValue("type", "number");
            }
            var startupPath = Path.Combine(sudDir, WasmStartupFileName);
            document.Root.Element("wasmStartup")?.Remove();
            document.Root.Add(new XElement("wasmStartup", new XAttribute("type", "object"),
                new XElement("version", new XAttribute("type", "number"), 1),
                new XElement("defaultSha256", new XAttribute("type", "string"), SudExportFiles.Read(startupPath).Sha256)));
            var destination = Path.Combine(sudDir, relativePath);
            Directory.CreateDirectory(Path.GetDirectoryName(destination));
            using (var output = File.Create(destination))
            using (var writer = JsonReaderWriterFactory.CreateJsonWriter(output, Utf8NoBom, false, true, "  "))
                document.WriteTo(writer);
            File.Delete(Path.Combine(sudDir, ObsoleteMetadataFileName));
            Debug.Log("[SudExporter] Wrote SUD game kit config: " + destination);
        }

        public static string BuildGameKitConfigJson(
            SudExporterSettings settings,
            bool developmentBuild,
            string emscriptenArgs,
            bool useRemoteResources,
            bool useDataCdn,
            string gameResourceCdnUrl,
            string startupLoadingCoverImage,
            string wasmPath,
            string symbolsPath,
            string dataPath,
            string loaderPath,
            string frameworkPath,
            string outputRoot = null)
        {
            using var exportFiles = SudExportFiles.Begin();
            var appName = string.IsNullOrEmpty(PlayerSettings.productName) ? "webgl" : PlayerSettings.productName;
            var companyName = PlayerSettings.companyName ?? string.Empty;
            var pluginVersion = SudExporterWindow.GetPluginVersion();
            var pluginName = SudPluginPaths.IsUpm ? "com.sud.unity-exporter" : "sud-unity-plugin";
            var textureCompression = GetTextureSubtargetName();
            var effectiveUseDataCdn = useRemoteResources && useDataCdn;
            var effectiveStartupLoadingCoverImage = effectiveUseDataCdn ? startupLoadingCoverImage : string.Empty;
            var hasLoadingCover = !string.IsNullOrEmpty(effectiveStartupLoadingCoverImage);
            var loadingCoverImageDst = hasLoadingCover ? "images/" + Path.GetFileName(effectiveStartupLoadingCoverImage) : string.Empty;
            var normalizedBaseUrl = gameResourceCdnUrl ?? string.Empty;
            var dataUrl = effectiveUseDataCdn && !string.IsNullOrEmpty(dataPath)
                ? normalizedBaseUrl + "Build/" + Path.GetFileName(dataPath)
                : string.Empty;

            var builder = new StringBuilder();
            builder.AppendLine("{");

            // project
            builder.AppendLine("  \"project\": {");
            builder.AppendLine("    \"app\": {");
            AppendProperty(builder, 3, "name", appName, true, false);
            AppendProperty(builder, 3, "company", companyName, true, true);
            builder.AppendLine("    },");
            builder.AppendLine("    \"engine\": {");
            AppendProperty(builder, 3, "name", "Unity", true, false);
            AppendProperty(builder, 3, "version", Application.unityVersion, true, true);
            builder.AppendLine("    },");
            AppendProperty(builder, 2, "convertPluginVersion", pluginVersion, true, false);
            AppendProperty(builder, 2, "pluginVersion", pluginVersion, true, false);
            AppendProperty(builder, 2, "pluginName", pluginName, true, true);
            builder.AppendLine("  },");

            // launch
            builder.AppendLine("  \"launch\": {");

            // launch.code
            builder.AppendLine("    \"code\": {");
            if (!string.IsNullOrEmpty(wasmPath) && File.Exists(wasmPath))
            {
                var wasmMd5 = SudExportFiles.Read(wasmPath).Md5;
                AppendProperty(builder, 3, "fileName", Path.GetFileName(wasmPath), true, false);
                AppendProperty(builder, 3, "outputFolder", "Build", true, false);
                AppendProperty(builder, 3, "codeMD5", ComputeMiddle16(wasmMd5), true, false);
                AppendProperty(builder, 3, "fileMD5", wasmMd5, true, false);
                AppendProperty(builder, 3, "fileSHA256", SudExportFiles.Read(wasmPath).Sha256, true, false);
                AppendProperty(builder, 3, "fileSize", new FileInfo(wasmPath).Length.ToString(), false, false);
                try
                {
                    var wasmInfo = SudExportFiles.InspectWasm(wasmPath);
                    AppendProperty(builder, 3, "totalFunctionCount", wasmInfo.TotalFunctionCount.ToString(), false, false);
                    AppendProperty(builder, 3, "importedFunctionCount", wasmInfo.ImportedFunctionCount.ToString(), false, false);
                    AppendProperty(builder, 3, "definedFunctionCount", wasmInfo.DefinedFunctionCount.ToString(), false, false);
                }
                catch
                {
                    // Ignore if wasm inspection is not applicable
                }
            }
            AppendProperty(builder, 3, "useWasmCodeSplit", "false", false, true);
            builder.AppendLine("    },");

            // launch.data
            builder.AppendLine("    \"data\": {");
            if (!string.IsNullOrEmpty(dataPath) && File.Exists(dataPath))
            {
                var dataMd5 = SudExportFiles.Read(dataPath).Md5;
                AppendProperty(builder, 3, "fileName", Path.GetFileName(dataPath), true, false);
                AppendProperty(builder, 3, "codeMD5", ComputeMiddle16(dataMd5), true, false);
                AppendProperty(builder, 3, "fileMD5", dataMd5, true, false);
                AppendProperty(builder, 3, "fileSHA256", SudExportFiles.Read(dataPath).Sha256, true, false);
                AppendProperty(builder, 3, "fileSize", new FileInfo(dataPath).Length.ToString(), false, false);
            }
            AppendProperty(builder, 3, "loadFromSubpackage", effectiveUseDataCdn ? "false" : "true", false, false);
            AppendProperty(builder, 3, "useDataCdn", effectiveUseDataCdn ? "true" : "false", false, false);
            AppendProperty(builder, 3, "dataFileCDN", dataUrl, true, true);
            builder.AppendLine("    },");

            // launch.symbols
            builder.AppendLine("    \"symbols\": {");
            if (!string.IsNullOrEmpty(symbolsPath) && File.Exists(symbolsPath))
            {
                var symbolsMd5 = SudExportFiles.Read(symbolsPath).Md5;
                AppendProperty(builder, 3, "fileName", Path.GetFileName(symbolsPath), true, false);
                AppendProperty(builder, 3, "outputFolder", "Build", true, false);
                AppendProperty(builder, 3, "codeMD5", ComputeMiddle16(symbolsMd5), true, false);
                AppendProperty(builder, 3, "fileMD5", symbolsMd5, true, false);
                AppendProperty(builder, 3, "fileSHA256", SudExportFiles.Read(symbolsPath).Sha256, true, false);
                AppendProperty(builder, 3, "fileSize", new FileInfo(symbolsPath).Length.ToString(), false, true);
            }
            else
            {
                AppendProperty(builder, 3, "fileName", string.Empty, true, true);
            }
            builder.AppendLine("    },");

            // launch.loader
            builder.AppendLine("    \"loader\": {");
            if (!string.IsNullOrEmpty(loaderPath) && File.Exists(loaderPath))
            {
                AppendProperty(builder, 3, "fileName", Path.GetFileName(loaderPath), true, false);
                AppendProperty(builder, 3, "fileMD5", SudExportFiles.Read(loaderPath).Md5, true, false);
                AppendProperty(builder, 3, "fileSHA256", SudExportFiles.Read(loaderPath).Sha256, true, false);
                AppendProperty(builder, 3, "fileSize", new FileInfo(loaderPath).Length.ToString(), false, true);
            }
            else
            {
                AppendProperty(builder, 3, "fileName", string.Empty, true, true);
            }
            builder.AppendLine("    },");

            // launch.framework
            builder.AppendLine("    \"framework\": {");
            if (!string.IsNullOrEmpty(frameworkPath) && File.Exists(frameworkPath))
            {
                AppendProperty(builder, 3, "fileName", Path.GetFileName(frameworkPath), true, false);
                AppendProperty(builder, 3, "fileMD5", SudExportFiles.Read(frameworkPath).Md5, true, false);
                AppendProperty(builder, 3, "fileSHA256", SudExportFiles.Read(frameworkPath).Sha256, true, false);
                AppendProperty(builder, 3, "fileSize", new FileInfo(frameworkPath).Length.ToString(), false, true);
            }
            else
            {
                AppendProperty(builder, 3, "fileName", string.Empty, true, true);
            }
            builder.AppendLine("    }");
            builder.AppendLine("  },");

            // network
            builder.AppendLine("  \"network\": {");
            AppendProperty(builder, 2, "dataFileCDN", effectiveUseDataCdn ? normalizedBaseUrl : string.Empty, true, false);
            AppendProperty(builder, 2, "streamingCDN", useRemoteResources ? normalizedBaseUrl : string.Empty, true, false);
            AppendProperty(builder, 2, "useRemoteResources", useRemoteResources ? "true" : "false", false, false);
            AppendProperty(builder, 2, "useDataCdn", useDataCdn ? "true" : "false", false, false);
            builder.AppendLine("    \"predownload\": {");
            AppendProperty(builder, 3, "enabled", (settings != null && settings.enablePredownload) ? "true" : "false", false, false);
            AppendProperty(builder, 3, "startupConcurrency", (settings != null ? settings.predownloadStartupConcurrency : 10).ToString(), false, false);
            AppendProperty(builder, 3, "runtimeConcurrency", (settings != null ? settings.predownloadRuntimeConcurrency : 1).ToString(), false, false);
            AppendProperty(builder, 3, "bundleHashLength", (settings != null ? settings.bundleHashLength : 32).ToString(), false, true);
            builder.AppendLine("    }");
            builder.AppendLine("  },");

            // runtime
            builder.AppendLine("  \"runtime\": {");
            AppendProperty(builder, 2, "colorSpace", PlayerSettings.colorSpace.ToString(), true, false);
            AppendProperty(builder, 2, "textureCompression", textureCompression, true, false);
            AppendProperty(builder, 2, "exceptionSupport", PlayerSettings.WebGL.exceptionSupport.ToString(), true, false);
            AppendProperty(builder, 2, "compressionFormat", "Disabled", true, false);
            AppendProperty(builder, 2, "dataCaching", "false", false, false);
            AppendProperty(builder, 2, "il2cppOptimizeSize", (settings != null && settings.il2cppOptimizeSize) ? "true" : "false", false, false);
            AppendProperty(builder, 2, "developmentBuild", developmentBuild ? "true" : "false", false, false);
            AppendProperty(builder, 2, "runtimeLogLevel", (settings != null ? settings.runtimeLogLevel : SudRuntimeLogLevel.Log).ToString(), true, false);
            AppendProperty(builder, 2, "emscriptenArgs", emscriptenArgs ?? string.Empty, true, true);
            builder.AppendLine("  },");

            // wasmSplit (ready for future wasm splitting tool)
            builder.AppendLine("  \"wasmSplit\": {");
            AppendProperty(builder, 2, "subVersion", "0", false, false);
            AppendProperty(builder, 2, "apiVersion", "0", false, true);
            builder.AppendLine("  },");

            // loadingPage
            builder.AppendLine("  \"loadingPage\": {");
            AppendProperty(builder, 2, "using", hasLoadingCover ? "true" : "false", false, false);
            AppendProperty(builder, 2, "hideAfterCallMain", "true", false, false);
            AppendProperty(builder, 2, "backgroundImage", loadingCoverImageDst, true, false);
            builder.AppendLine("    \"materialConfig\": {");
            AppendProperty(builder, 3, "backgroundImage", loadingCoverImageDst, true, true);
            builder.AppendLine("    }");
            builder.AppendLine("  },");

            // uiConfig (dynamically serialized from settings ScriptableObject)
            builder.AppendLine("  \"uiConfig\": " + FormatUiConfig(settings, outputRoot));

            builder.AppendLine("}");
            return builder.ToString();
        }

        private static string FormatUiConfig(SudExporterSettings settings, string outputRoot)
        {
            if (settings == null)
            {
                return "{}";
            }

            var projectRoot = Directory.GetParent(Application.dataPath).FullName;
            if (string.IsNullOrEmpty(outputRoot))
            {
                var outputFolder = string.IsNullOrEmpty(settings.outputFolder) ? "Build" : settings.outputFolder;
                outputRoot = Path.GetFullPath(Path.Combine(projectRoot, outputFolder));
            }
            var relativeFolder = Path.GetRelativePath(projectRoot, outputRoot);
            var snapshot = UnityEngine.Object.Instantiate(settings);
            string rawJson;
            try
            {
                snapshot.name = settings.name;
                // Windows paths on different volumes have no relative form.
                snapshot.outputFolder = Path.IsPathRooted(relativeFolder)
                    ? string.Empty
                    : relativeFolder.Replace(Path.DirectorySeparatorChar, '/');
                rawJson = JsonUtility.ToJson(snapshot, true);
            }
            finally
            {
                UnityEngine.Object.DestroyImmediate(snapshot);
            }

            if (string.IsNullOrEmpty(rawJson))
            {
                return "{}";
            }

            var lines = rawJson.Split(new[] { "\r\n", "\r", "\n" }, StringSplitOptions.None);
            var sb = new StringBuilder();
            for (int i = 0; i < lines.Length; i++)
            {
                if (i == 0)
                {
                    sb.AppendLine(lines[i]);
                }
                else
                {
                    sb.Append("  ").AppendLine(lines[i]);
                }
            }
            return sb.ToString().TrimEnd();
        }

        private static string FindSingleBuildFile(string buildDir, string suffix, bool allowMissing = false)
        {
            var files = Directory.GetFiles(buildDir, "*" + suffix);
            if (files.Length == 0)
            {
                if (allowMissing)
                    return null;
                throw new FileNotFoundException("Missing WebGL Build file with suffix " + suffix + " in " + buildDir);
            }
            if (files.Length != 1)
            {
                throw new InvalidDataException(
                    "Expected a single WebGL Build file with suffix " + suffix + " in " + buildDir +
                    ", but found " + files.Length + ": " + string.Join(", ", files));
            }
            return files[0];
        }

        private static void ValidateMatchingBuildFileNames(string wasmPath, string symbolsPath, string dataPath,
            string loaderPath, string frameworkPath)
        {
            var expectedBaseName = GetBuildFileBaseName(wasmPath, ".wasm");
            if (!string.IsNullOrEmpty(symbolsPath))
            {
                ValidateBuildFileBaseName(symbolsPath, ".symbols.json", expectedBaseName);
            }
            ValidateBuildFileBaseName(dataPath, ".data", expectedBaseName);
            ValidateBuildFileBaseName(loaderPath, ".loader.js", expectedBaseName);
            ValidateBuildFileBaseName(frameworkPath, ".framework.js", expectedBaseName);
        }

        private static void ValidateBuildFileBaseName(string path, string suffix, string expectedBaseName)
        {
            var actualBaseName = GetBuildFileBaseName(path, suffix);
            if (!string.Equals(actualBaseName, expectedBaseName, StringComparison.Ordinal))
            {
                throw new InvalidDataException(
                    "WebGL Build artifacts do not share a common base name: expected " + expectedBaseName +
                    " but found " + Path.GetFileName(path));
            }
        }

        private static string GetBuildFileBaseName(string path, string suffix)
        {
            var fileName = Path.GetFileName(path);
            if (string.IsNullOrEmpty(fileName) || !fileName.EndsWith(suffix, StringComparison.Ordinal))
            {
                throw new InvalidDataException("Unexpected WebGL Build artifact name: " + path);
            }

            return fileName.Substring(0, fileName.Length - suffix.Length);
        }

        private static void ValidateSymbols(string symbolsPath, SudWasmInfo wasmInfo)
        {
            if (string.IsNullOrEmpty(symbolsPath) || !File.Exists(symbolsPath))
            {
                throw new FileNotFoundException("WebGL symbols file not found.", symbolsPath);
            }

            var seen = new bool[wasmInfo.TotalFunctionCount];
            var count = 0;
            using (var stream = File.OpenRead(symbolsPath))
            using (var reader = JsonReaderWriterFactory.CreateJsonReader(stream, XmlDictionaryReaderQuotas.Max))
            {
                reader.MoveToContent();
                if (reader.GetAttribute("type") != "object")
                    throw new InvalidDataException("Symbols must be a JSON object.");
                reader.ReadStartElement("root");
                while (reader.MoveToContent() == XmlNodeType.Element)
                {
                    var key = reader.GetAttribute("item") ?? reader.LocalName;
                    if (!int.TryParse(key, NumberStyles.None, CultureInfo.InvariantCulture, out int index) ||
                        index < 0 || index >= seen.Length)
                        throw new InvalidDataException("Invalid symbols function index: " + key);
                    if (seen[index]) throw new InvalidDataException("Duplicate symbols function index: " + index);
                    if (reader.GetAttribute("type") != "string" || reader.ReadElementContentAsString().Length == 0)
                        throw new InvalidDataException("Invalid symbols value at function index: " + index);
                    seen[index] = true;
                    count++;
                }
                reader.ReadEndElement();
                if (reader.MoveToContent() != XmlNodeType.None)
                    throw new InvalidDataException("Unexpected content after the symbols object.");
            }

            if (count != wasmInfo.TotalFunctionCount)
            {
                throw new InvalidDataException(
                    "symbols function count mismatch: symbols=" + count + " wasm=" + wasmInfo.TotalFunctionCount);
            }

            for (var i = 0; i < seen.Length; i++)
            {
                if (!seen[i])
                {
                    throw new InvalidDataException("symbols function index is missing: " + i);
                }
            }
        }

        private static string ComputeMiddle16(string md5)
        {
            return !string.IsNullOrEmpty(md5) && md5.Length >= 24 ? md5.Substring(8, 16) : md5;
        }

        private static string GetTextureSubtargetName()
        {
            var prop = typeof(EditorUserBuildSettings).GetProperty("webGLBuildSubtarget",
                BindingFlags.Public | BindingFlags.Static);
            if (prop != null)
            {
                var val = prop.GetValue(null);
                if (val != null)
                {
                    return val.ToString();
                }
            }

            return "Generic";
        }

        private static void AppendProperty(StringBuilder builder, int indent, string name, string value, bool quoteValue, bool last)
        {
            builder.Append(new string(' ', indent * 2)).Append('"').Append(name).Append("\": ");
            if (value == null)
            {
                builder.Append("null");
            }
            else if (quoteValue)
            {
                builder.Append('"').Append(JsonEscape(value)).Append('"');
            }
            else
            {
                builder.Append(value);
            }

            builder.AppendLine(last ? string.Empty : ",");
        }

        private static string JsonEscape(string value)
        {
            if (value == null)
            {
                return string.Empty;
            }

            return value
                .Replace("\\", "\\\\")
                .Replace("\"", "\\\"")
                .Replace("\r", "\\r")
                .Replace("\n", "\\n")
                .Replace("\t", "\\t");
        }
    }
}
