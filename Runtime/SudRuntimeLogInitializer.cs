using UnityEngine;

namespace Sud.UnityExporter
{
    internal static class SudRuntimeLogInitializer
    {
#if UNITY_WEBGL && !UNITY_EDITOR && (SUD_RUNTIME_LOG_LEVEL_LOG || SUD_RUNTIME_LOG_LEVEL_WARN || SUD_RUNTIME_LOG_LEVEL_ERROR)
        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.BeforeSceneLoad)]
        private static void InitializeBeforeSceneLoad()
        {
#if SUD_RUNTIME_LOG_LEVEL_ERROR
            // Unity uses Assert as the threshold that includes Error, Assert and Exception.
            Debug.unityLogger.filterLogType = LogType.Assert;
#elif SUD_RUNTIME_LOG_LEVEL_WARN
            Debug.unityLogger.filterLogType = LogType.Warning;
#else
            Debug.unityLogger.filterLogType = LogType.Log;
#endif
        }
#endif
    }
}
