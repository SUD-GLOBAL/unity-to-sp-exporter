window.require("api-bridge.js");
window.require("unity_adapter.js");

const ENVIRONMENT_IS_PTHREAD = true;

// 下面这行的 Unity 版本号在导出时由插件写入。
console.log("[SUD] Unity Version: $UNITY_VERSION");

document.URL = "http://localhost/";

let _onScreenCanvas = document.createElement("canvas");
_onScreenCanvas.id = "glcanvas";
document.body.appendChild(_onScreenCanvas);

_onScreenCanvas.width = window.innerWidth;
_onScreenCanvas.height = window.innerHeight;

// 启动 loading 定制入口。
// .data 使用 CDN 时必须配置背景图，导出前由插件校验。
const startupLoadingCoverUrl = "$STARTUP_LOADING_COVER_URL";
const useRemoteResources = $USE_REMOTE_RESOURCES;
const useRemoteData = useRemoteResources && $USE_REMOTE_DATA;
const unityDataFile = { md5: "$DATA_FILE_MD5", size: $DATA_FILE_SIZE };
let predownloadManager = null;
if (useRemoteResources) {
    if (useRemoteData) window.require("startup_loading.js");
    window.require("predownload.js");
    predownloadManager = window.sudPredownloadRuntime;
}
const startupLoadingConfig = {
    maxCoverSize: 2048,
    designWidth: 0,
    designHeight: 0,
    scaleMode: "",
    textConfig: {
        style: {
            bottomRatio: 0.13,
            heightRatio: 0.04,
            widthRatio: 0.9,
            lineHeightRatio: 0.04,
            color: "#ffffff",
            fontSizeRatio: 0.026,
        },
    },
    barConfig: {
        style: {
            horizontalMarginRatio: 0.05,
            heightRatio: 0.018,
            paddingRatio: 0.003,
            bottomRatio: 0.09,
            backgroundColor: "#07C160",
        },
    },
};

// Unity Instance Configuration
let buildUrl = "Build/";
let loaderUrl = buildUrl + "$LOADER_FILENAME";
let unityLoaderLoaded = false;
let remoteDataPreparer = useRemoteData ? window.createRemoteDataPreparer(unityDataFile) : null;
let startupLoading = useRemoteData ? window.createStartupLoading({
    coverUrl: startupLoadingCoverUrl,
    config: startupLoadingConfig,
    remoteDataPreparer: remoteDataPreparer,
}) : null;

let config = {
    dataUrl: "$DATA_URL",
    frameworkUrl: buildUrl + "$FRAMEWORK_FILENAME",
    codeUrl: buildUrl + "$CODE_FILENAME",
    streamingAssetsUrl: predownloadManager ? predownloadManager.streamingAssetsUrl : "StreamingAssets",
    wasmBinary: buildUrl + "$CODE_FILENAME",
    devicePixelRatio: 1,
    instantiateWasm: function (imports, successCallback) {
        return WebAssembly.instantiateFromFile(this.codeUrl, imports)
            .then(function (output) {
                console.log(" instantiate success.");
                try {
                    successCallback(output.instance);
                } catch (e) {
                    console.error("Error inside successCallback: " + e);
                    console.error(e.stack);
                }
            })
            .catch(function (e) {
                console.log(" instantiateFromFile failed, e=" + e);
            });
    },
    unityFileSystemInit: function () {},
    UnityFileSystem: {
        init: function () {
            console.log(": Bypassing UnityFileSystem.init().");
        },
    },
};

function loadUnityLoader() {
    if (!unityLoaderLoaded) {
        window.require(loaderUrl);
        unityLoaderLoaded = true;
    }
}

async function startUnity() {
    try {
        if (startupLoading) await startupLoading.show();
        if (remoteDataPreparer) {
            loadUnityLoader();
            config.dataUrl = await startupLoading.downloadData(config.dataUrl);
        }
        if (predownloadManager) {
            try {
                await predownloadManager.ready;
                predownloadManager.start();
            } catch (error) {
                console.error("[SUDPredownload] start failed", error);
            }
        }
        if (startupLoading) await startupLoading.startUnityLoading();
        loadUnityLoader();
        await createUnityInstance(_onScreenCanvas, config, (progress) => {
            console.log("progress", 100 * progress + "%");
            if (startupLoading) {
                startupLoading.setUnityProgress(progress);
            }
        }).then(() => {
            if (predownloadManager) {
                predownloadManager.setUnityReady();
            }
            if (startupLoading) {
                startupLoading.finish();
                startupLoading.hide();
            }
        });
    } catch (error) {
        if (remoteDataPreparer) remoteDataPreparer.abort();
        // 原生控制台序列化 Error 时可能只显示 {}，必须显式输出错误文字。
        const message = error && (error.message || error.errMsg) || String(error);
        console.error("[SUD] Unity startup failed: " + message);
    }
}

let gameStarted = false;
function startGame() {
    if (gameStarted) return;
    gameStarted = true;
    startUnity();
}

// 自定义启动时替换下面的 startGame()；等待 predownloadManager.ready 后设置列表，再调用 startGame()。
startGame();
