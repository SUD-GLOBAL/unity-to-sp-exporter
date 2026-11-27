using System;
using System.Collections.Generic;
using System.Text;
using UnityEditor;
using UnityEditor.Build;

namespace Sud.UnityExporter
{
    public static class SudWebGLBuildConfigurator
    {
        private static readonly string[] RequiredExportedFunctions =
        {
            "_sbrk",
            "_malloc",
            "_free",
            "_emscripten_stack_get_base",
            "_emscripten_stack_get_end"
        };

        public static void Configure(string existingEmscriptenArgs, bool il2cppOptimizeSize)
        {
#if UNITY_2021_2_OR_NEWER
            PlayerSettings.WebGL.debugSymbolMode = WebGLDebugSymbolMode.External;
#else
            PlayerSettings.WebGL.debugSymbols = true;
#endif

            PlayerSettings.WebGL.emscriptenArgs = BuildEmscriptenArgs(existingEmscriptenArgs);
            UnityEngine.Debug.Log("[SudExporter] PlayerSettings.WebGL.emscriptenArgs: " + PlayerSettings.WebGL.emscriptenArgs);

            ConfigureIl2CppCodeGeneration(il2cppOptimizeSize);
        }

        // IL2CPP code generation drives how many C++ functions the build emits, which is
        // the single biggest factor in wasm size and in how many functions the splitter
        // has to place. OptimizeSize turns on full generic sharing, so generic methods
        // over value types collapse into one shared body instead of one body per value
        // type; OptimizeSpeed (Unity's default) emits a specialised body for each.
        //
        // On the FPS sample that is 61,069 defined functions vs 74,530 (+22 %), a
        // 22.3 MB vs 27.0 MB Code section (+21 %), and a 67,213 vs 125,338 entry
        // indirect function table (+86 %) — from identical scenes and scripts.
        //
        // The WeChat minigame plugin sets OptimizeSize, so keeping the same default
        // here is also what makes our split products comparable to theirs.
        //
        // Enum values are serialised into ProjectSettings.asset as
        // `il2cppCodeGeneration: WebGL: <n>`, where OptimizeSpeed = 0 and OptimizeSize = 1.
        private static void ConfigureIl2CppCodeGeneration(bool optimizeSize)
        {
#if UNITY_2021_2_OR_NEWER
            var mode = optimizeSize ? Il2CppCodeGeneration.OptimizeSize : Il2CppCodeGeneration.OptimizeSpeed;
#if UNITY_2022_1_OR_NEWER
            // EditorUserBuildSettings.il2CppCodeGeneration is obsolete from 2022.1 on.
            PlayerSettings.SetIl2CppCodeGeneration(NamedBuildTarget.WebGL, mode);
#else
            EditorUserBuildSettings.il2CppCodeGeneration = mode;
#endif
            UnityEngine.Debug.Log("[SudExporter] IL2CPP code generation: " + mode);
#endif
        }

        internal static string BuildEmscriptenArgs(string existingArgs)
        {
            var tokens = TokenizeArguments(existingArgs);
            var mergedArgs = new List<string>();
            var exportedFunctions = new List<string>();
            var exportedFunctionSet = new HashSet<string>(StringComparer.Ordinal);

            for (var i = 0; i < tokens.Count; i++)
            {
                string exportedFunctionsValue;
                if (tokens[i].Value == "-s" && i + 1 < tokens.Count &&
                    TryGetExportedFunctionsValue(tokens[i + 1].Value, out exportedFunctionsValue))
                {
                    AddExportedFunctions(exportedFunctionsValue, exportedFunctions, exportedFunctionSet);
                    i++;
                    continue;
                }

                if (TryGetInlineExportedFunctionsValue(tokens[i].Value, out exportedFunctionsValue))
                {
                    AddExportedFunctions(exportedFunctionsValue, exportedFunctions, exportedFunctionSet);
                    continue;
                }

                mergedArgs.Add(tokens[i].Raw);
            }

            for (var i = 0; i < RequiredExportedFunctions.Length; i++)
            {
                AddExportedFunction(RequiredExportedFunctions[i], exportedFunctions, exportedFunctionSet);
            }

            mergedArgs.Add("-s");
            mergedArgs.Add("EXPORTED_FUNCTIONS=" + string.Join(",", exportedFunctions.ToArray()));

            return string.Join(" ", mergedArgs.ToArray()).Trim();
        }

        internal static List<string> ParseArgumentGroups(string args)
        {
            var tokens = TokenizeArguments(args);
            var groups = new List<string>();
            for (var i = 0; i < tokens.Count; i++)
            {
                if (tokens[i].Value == "-s" && i + 1 < tokens.Count)
                {
                    groups.Add("-s " + tokens[i + 1].Value);
                    i++;
                }
                else
                {
                    groups.Add(tokens[i].Value);
                }
            }

            return groups;
        }

        private static bool ContainsArgument(string args, string expected)
        {
            return ContainsArgument(TokenizeArguments(args), expected);
        }

        private static bool ContainsArgument(List<ArgumentToken> tokens, string expected)
        {
            for (var i = 0; i < tokens.Count; i++)
            {
                if (tokens[i].Value == expected)
                {
                    return true;
                }
            }

            return false;
        }

        private static bool TryGetExportedFunctionsValue(string value, out string exportedFunctions)
        {
            const string Prefix = "EXPORTED_FUNCTIONS=";
            if (value.StartsWith(Prefix, StringComparison.Ordinal))
            {
                exportedFunctions = value.Substring(Prefix.Length);
                return true;
            }

            exportedFunctions = null;
            return false;
        }

        private static bool TryGetInlineExportedFunctionsValue(string value, out string exportedFunctions)
        {
            const string Prefix = "-sEXPORTED_FUNCTIONS=";
            if (value.StartsWith(Prefix, StringComparison.Ordinal))
            {
                exportedFunctions = value.Substring(Prefix.Length);
                return true;
            }

            exportedFunctions = null;
            return false;
        }

        private static void AddExportedFunctions(string value, List<string> functions, HashSet<string> seen)
        {
            value = (value ?? string.Empty).Trim();
            if (value.StartsWith("@", StringComparison.Ordinal))
            {
                throw new InvalidOperationException(
                    "Cannot merge required exports into an EXPORTED_FUNCTIONS response file: " + value);
            }

            if (value.Length >= 2 && value[0] == '[' && value[value.Length - 1] == ']')
            {
                value = value.Substring(1, value.Length - 2);
            }

            var entries = value.Split(',');
            for (var i = 0; i < entries.Length; i++)
            {
                var function = entries[i].Trim().Trim('\'', '"');
                if (!string.IsNullOrEmpty(function))
                {
                    AddExportedFunction(function, functions, seen);
                }
            }
        }

        private static void AddExportedFunction(string function, List<string> functions, HashSet<string> seen)
        {
            if (seen.Add(function))
            {
                functions.Add(function);
            }
        }

        private static List<ArgumentToken> TokenizeArguments(string args)
        {
            var tokens = new List<ArgumentToken>();
            if (string.IsNullOrWhiteSpace(args))
            {
                return tokens;
            }

            var index = 0;
            while (index < args.Length)
            {
                while (index < args.Length && char.IsWhiteSpace(args[index])) index++;
                if (index >= args.Length) break;

                var start = index;
                var value = new StringBuilder();
                var quote = '\0';
                while (index < args.Length)
                {
                    var current = args[index];
                    if (quote == '\0')
                    {
                        if (char.IsWhiteSpace(current)) break;
                        if (current == '\'' || current == '"')
                        {
                            quote = current;
                            index++;
                            continue;
                        }
                    }
                    else if (current == quote)
                    {
                        quote = '\0';
                        index++;
                        continue;
                    }

                    value.Append(current);
                    index++;
                }

                if (quote != '\0')
                {
                    throw new ArgumentException("Unterminated quote in WebGL Emscripten arguments.", "args");
                }

                tokens.Add(new ArgumentToken(args.Substring(start, index - start), value.ToString()));
            }

            return tokens;
        }

        private sealed class ArgumentToken
        {
            public readonly string Raw;
            public readonly string Value;

            public ArgumentToken(string raw, string value)
            {
                Raw = raw;
                Value = value;
            }
        }
    }
}
