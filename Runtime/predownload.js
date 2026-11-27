(function (global) {
    "use strict";

    // Keep the adapter's transport before installing the preload interceptor.
    const TransportXHR = global.XMLHttpRequest;
    // 预下载完成后保留原始字节 60 秒，供启动阶段直接复用；命中不会延长保留时间。
    const MEMORY_TTL_MS = 60000;
    // require 时读取配置并安装 XHR 拦截；main 在创建 Unity 实例前调用启动接口的 start()。
    const fileSystem = global.ral.getFileSystemManager();
    const manifest = JSON.parse(fileSystem.readFileSync("preload_mainfest.json", "utf8").replace(/^\uFEFF/, ""));
    if (!manifest || manifest.schemaVersion !== 1 || !Array.isArray(manifest.resources)) throw new Error("invalid predownload manifest");
    const hashLength = manifest.bundleHashLength == null ? 32 : manifest.bundleHashLength;
    if (!Number.isSafeInteger(hashLength) || hashLength < 1) throw new Error("invalid bundle hash length");
    const cacheRoot = global.ral.env.USER_DATA_PATH + "/sud-predownload";
    const versionSuffix = new RegExp("^([\\s\\S]*)[_-][a-f0-9]{" + hashLength + "}(\\.[^./]*)?$", "i");
    let started = false;
    let activeCount = 0;
    let concurrency = Math.max(1, Math.min(10, Number(manifest.startupConcurrency || 10)));
    const resourcesById = new Map();
    const resourcesByUrl = new Map();
    let preloadResources = new Map();
    let preloadUrls = new Map();
    const tasks = new Map();
    // 按 cacheKey 保存 { data: ArrayBuffer, expiresAt, timer }，与磁盘文件的生命周期独立。
    const memory = new Map();
    const queue = [];

    function canonicalUrl(value) {
        const match = /^(https?:\/\/[^/]+)?([^?]*)(.*)$/.exec(String(value).split("#")[0]);
        const origin = (match[1] || "").toLowerCase();
        const segments = [];
        match[2].replace(/\\/g, "/").split("/").forEach(function (part) {
            if (!part || part === ".") return;
            if (part === "..") { segments.pop(); return; }
            try { part = encodeURIComponent(decodeURIComponent(part)); } catch (error) {}
            segments.push(part);
        });
        return origin + "/" + segments.join("/") + match[3];
    }

    function validateCacheKey(key) {
        if (typeof key !== "string" || !key || key.split("/").some(part =>
            !part || part === "." || part === ".." || part === ".tmp" || part.includes("\\")) ||
            ["index.json", "index.json.part", "index.json.bak"].includes(key)) {
            throw new Error("invalid predownload cache key: " + key);
        }
    }

    function registerUrls(resource, target) {
        const aliases = [resource.url].concat(resource.aliases || []);
        if (resource.relativePath) aliases.push(resource.relativePath, manifest.cdnBaseUrl + resource.relativePath);
        aliases.forEach(url => target.set(canonicalUrl(url), resource));
    }

    function forgetMemory(key) {
        // 只解除本模块持有的字节引用并清理定时器，不删除磁盘文件；实际回收由 JS GC 决定。
        const entry = memory.get(key);
        if (entry) clearTimeout(entry.timer);
        memory.delete(key);
    }

    function removeOldVersions(resource) {
        const slash = resource.cacheKey.lastIndexOf("/");
        const directory = resource.cacheKey.substring(0, slash + 1);
        const name = resource.cacheKey.substring(slash + 1);
        const logicalName = name.replace(versionSuffix, "$1$2");
        if (logicalName === name) return;
        try {
            fileSystem.readdirSync(directory ? cacheRoot + "/" + directory.slice(0, -1) : cacheRoot).forEach(function (oldName) {
                if (oldName === name || !versionSuffix.test(oldName) || oldName.replace(versionSuffix, "$1$2") !== logicalName) return;
                const key = directory + oldName;
                // Keep versions still selected by this game or currently being read/downloaded.
                if (preloadResources.has(key) || tasks.has(key)) return;
                const path = cacheRoot + "/" + key;
                if (fileSystem.statSync(path, false).isDirectory()) return;
                fileSystem.unlinkSync(path);
                forgetMemory(key);
                console.log("[SUDPredownload] old version removed", JSON.stringify({ path: path }));
            });
        } catch (error) { console.log("[SUDPredownload] old version cleanup skipped", JSON.stringify({ error: error.message || error.errMsg || String(error) })); }
    }

    function pumpQueue() {
        while (activeCount < concurrency && queue.length) {
            const item = queue.shift();
            activeCount++;
            new Promise(function (resolve, reject) {
                const xhr = new TransportXHR();
                xhr.open("GET", item.resource.url, true);
                xhr.responseType = "arraybuffer";
                xhr.timeout = 60000;
                xhr.onload = function () {
                    if (xhr.status < 200 || xhr.status >= 300) {
                        const error = new Error("HTTP " + xhr.status);
                        error.status = xhr.status;
                        reject(error);
                        return;
                    }
                    // 宿主每次读取 response 可能生成新的字节对象，只取一次供校验和交付复用。
                    const data = xhr.response;
                    if (!(data instanceof ArrayBuffer) ||
                        (item.resource.size >= 0 && data.byteLength !== item.resource.size)) {
                        reject(new Error("predownload size mismatch: " + item.resource.url));
                    } else resolve(data);
                };
                xhr.onerror = xhr.ontimeout = xhr.onabort = () => reject(new Error("predownload request failed: " + item.resource.url));
                console.log("[SUDPredownload] download start", JSON.stringify({ url: item.resource.url }));
                xhr.send();
            }).then(item.resolve, item.reject).then(function () {
                activeCount--;
                pumpQueue();
            });
        }
    }

    async function loadResource(resource, task, cachedSize) {
        const path = cacheRoot + "/" + resource.cacheKey;
        if (cachedSize !== null) {
            try {
                const result = await new Promise((resolve, reject) => {
                    fileSystem.readFile({ filePath: path, encoding: "binary", success: resolve, fail: reject });
                });
                if (!(result.data instanceof ArrayBuffer) || result.data.byteLength !== cachedSize) throw new Error("cached size mismatch");
                console.log("[SUDPredownload] cache hit", JSON.stringify({ url: resource.url, path: path }));
                removeOldVersions(resource);
                return result.data;
            } catch (error) { /* 文件读取失败或内容不完整：重新下载并修复缓存。 */ }
        }

        const data = await new Promise(function (resolve, reject) {
            const item = { resource: resource, resolve: resolve, reject: reject };
            if (task.highPriority) queue.unshift(item); else queue.push(item);
            pumpQueue();
        });
        const partPath = cacheRoot + "/.tmp/" + Date.now() + "." + Math.random().toString(16).slice(2) + ".part";
        try {
            for (const directory of [cacheRoot + "/.tmp", path.substring(0, path.lastIndexOf("/"))]) {
                try { fileSystem.accessSync(directory); }
                catch (missing) { fileSystem.mkdirSync(directory, true); }
            }
            await new Promise((resolve, reject) => {
                fileSystem.writeFile({ filePath: partPath, data: data, encoding: "binary", success: resolve, fail: reject });
            });
            if (fileSystem.statSync(partPath, false).size !== data.byteLength) throw new Error("cache write size mismatch");
            let targetExists = false;
            try { fileSystem.accessSync(path); targetExists = true; } catch (missing) {}
            if (targetExists) fileSystem.unlinkSync(path);
            await new Promise((resolve, reject) => {
                fileSystem.rename({ oldPath: partPath, newPath: path, success: resolve, fail: reject });
            });
            removeOldVersions(resource);
        } catch (error) {
            // Saving is optional for this request; never evict unrelated resources to make room.
            console.log("[SUDPredownload] cache write skipped", JSON.stringify({ url: resource.url, error: error.message || error.errMsg || String(error) }));
        } finally {
            try { fileSystem.unlinkSync(partPath); } catch (missing) {}
        }
        console.log("[SUDPredownload] download success", JSON.stringify({ url: resource.url, bytes: data.byteLength }));
        return data;
    }

    function request(resource, preload = false) {
        const key = resource.cacheKey;
        const entry = memory.get(key);
        if (entry && entry.expiresAt > Date.now()) {
            // 检查到期时间，避免后台暂停导致定时器延迟执行时继续命中。
            console.log("[SUDPredownload] memory hit", JSON.stringify({ url: resource.url }));
            return Promise.resolve(entry.data);
        }
        forgetMemory(key);
        let task = tasks.get(key);
        if (!task) {
            // 文件状态只在这里检查一次；null 表示没有有效文件，0 仍是有效的空文件。
            let cachedSize = null;
            try {
                const stat = fileSystem.statSync(cacheRoot + "/" + key, false);
                if (!stat.isDirectory() && (resource.size < 0 || stat.size === resource.size)) cachedSize = stat.size;
            } catch (missing) {}
            // 只有主动预下载才发起新下载；普通请求未命中时交回原 XHR。
            if (!preload && cachedSize === null) return null;
            task = { warm: preload, highPriority: !preload };
            tasks.set(key, task);
            task.promise = loadResource(resource, task, cachedSize).then(function (data) {
                if (task.warm) {
                    // 预下载完成后保留启动阶段的字节；普通磁盘读取不增加内存驻留。
                    memory.set(key, { data: data, expiresAt: Date.now() + MEMORY_TTL_MS,
                        timer: setTimeout(() => forgetMemory(key), MEMORY_TTL_MS) });
                }
                return data;
            });
            task.promise.then(() => tasks.delete(key), () => tasks.delete(key));
        } else {
            if (preload) task.warm = true;
            else {
                task.highPriority = true;
                const index = queue.findIndex(item => item.resource.cacheKey === key);
                if (index > 0) queue.unshift(queue.splice(index, 1)[0]);
            }
            console.log("[SUDPredownload] join in-flight", JSON.stringify({ url: resource.url }));
        }
        return task.promise;
    }

    function setPreloadList(list) {
        if (started) throw new Error("preload list cannot be changed after predownload starts");
        if (!Array.isArray(list)) throw new Error("preload list must be an array of IDs or URLs");
        const selected = new Map();
        const urls = new Map();
        list.forEach(function (value) {
            if (typeof value !== "string" || !value.trim()) throw new Error("preload entry must be a non-empty string");
            let resource = resourcesById.get(value) || resourcesByUrl.get(canonicalUrl(value));
            if (!resource) {
                let url = value;
                if (!/^https?:\/\//.test(url)) {
                    const relative = url.replace(/^\/+/, "");
                    url = manifest.cdnBaseUrl + (relative.startsWith("StreamingAssets/") ? relative : "StreamingAssets/" + relative);
                }
                url = canonicalUrl(url);
                const match = /^(https?:\/\/[^/]+)\/([^?]*)(?:\?.*)?$/.exec(url);
                if (!match) throw new Error("preload URL requires a CDN address: " + value);
                const cdn = manifest.cdnBaseUrl && canonicalUrl(manifest.cdnBaseUrl).replace(/\/?$/, "/");
                const relativePath = cdn && url.startsWith(cdn) ? url.substring(cdn.length).split("?")[0] : "";
                const path = (relativePath || match[2]).split("/").map(decodeURIComponent).join("/");
                const cacheKey = relativePath ? path : "external/" + encodeURIComponent(match[1]) + "/" + path;
                resource = { id: value, url: url, relativePath: relativePath, cacheKey: cacheKey, size: -1 };
            }
            validateCacheKey(resource.cacheKey);
            selected.set(resource.cacheKey, resource);
            registerUrls(resource, urls);
        });
        preloadResources = selected;
        preloadUrls = urls;
    }

    manifest.resources.forEach(function (resource) {
        validateCacheKey(resource.cacheKey);
        resourcesById.set(resource.id, resource);
        registerUrls(resource, resourcesByUrl);
    });
    setPreloadList(manifest.preloadDataList);
    const ready = manifest.preloadListUrl ? new Promise((resolve, reject) => {
        const request = new TransportXHR();
        request.open("GET", manifest.preloadListUrl, true);
        request.timeout = 5000;
        request.onload = () => {
            if (request.status < 200 || request.status >= 300) {
                reject(new Error("HTTP " + request.status));
                return;
            }
            try { resolve(JSON.parse(request.responseText)); }
            catch (error) { reject(error); }
        };
        request.onerror = request.ontimeout = () => reject(new Error("preload list request failed"));
        request.send();
    }).then(result => {
        setPreloadList(Array.isArray(result) ? result : result.list || result.preloadDataList);
    }).catch(error => {
        console.warn("[SUDPredownload] remote preload list failed; using exported list", error);
    }) : Promise.resolve();

    // 只为 Unity 使用的 arraybuffer 请求交付结果；不模拟完整 XHR 事件流程。
    const RequestState = {
        Unsent: 0,
        Opened: 1,
        Done: 4
    };

    class PreloadXHR extends TransportXHR {
        constructor() {
            super();
            this._preloadUrl = null;
        }
        open(method, url, async, user, password) {
            if (String(method).toUpperCase() === "GET") {
                const key = canonicalUrl(url);
                const mapped = preloadUrls.get(key) || resourcesByUrl.get(key);
                // 未选入预下载列表的 StreamingAssets 也已经部署到 CDN。
                url = mapped ? mapped.url : manifest.cdnBaseUrl && key.startsWith("/StreamingAssets/")
                    ? manifest.cdnBaseUrl + key.substring(1) : url;
                // 每次请求新建 XHR；只记录可复用的远程地址，其余请求保留初始 null。
                if (async !== false && !user && !password && /^https?:\/\//.test(url)) this._preloadUrl = canonicalUrl(url);
            }
            super.open(method, url, async, user, password);
            // 适配层可能将 localhost URL 转为包内路径；本地请求不参与远程预下载复用。
            if (this._isLocal) this._preloadUrl = null;
        }
        abort() {
            if (!this._preloadUrl || !this._isLocal) { super.abort(); return; }
            const active = this._readyState === RequestState.Opened;
            // 在取消事件之前清理本请求的响应引用，不修改其他请求或缓存共用的字节。
            this._response = null;
            this._responseURL = this._statusText = "";
            this._status = 0;
            this._readyState = RequestState.Unsent;
            if (active) this.dispatchEvent(new global.Event("abort"));
        }
        send(body) {
            if ((this._preloadUrl && this._isLocal) || this.readyState !== RequestState.Opened) throw new Error("XMLHttpRequest must be opened before send");
            const resource = preloadUrls.get(this._preloadUrl);
            if (!resource || this.responseType !== "arraybuffer" || body != null || this.withCredentials) { super.send(body); return; }
            const pending = request(resource);
            if (!pending) { super.send(body); return; }
            // 只接管内存/磁盘命中或已有任务；未下载、预下载失败且无缓存时走原 send。
            this._isLocal = true;
            this._readyState = RequestState.Opened;
            this._responseURL = resource.url;
            const finish = (data, error) => {
                // 等待期间可能已取消，只停止当前结果交付，共享下载继续。
                if (this._readyState === RequestState.Unsent) return;
                this._response = data;
                this._status = error ? error.status || 0 : 200;
                this._statusText = error ? error.message || "" : "OK";
                this._readyState = RequestState.Done;
                this.dispatchEvent(new global.Event(error && !error.status ? "error" : "load"));
            };
            pending.then(data => finish(data), error => finish(null, error));
        }
    }
    global.XMLHttpRequest = PreloadXHR;

    // 与 startup_loading.js 一样，通过 window 提供启动接口；宿主 require 不提供 CommonJS module。
    // 资源表、下载任务和缓存仍保存在上面的闭包内。
    window.sudPredownloadRuntime = {
        ready: ready,
        streamingAssetsUrl: manifest.cdnBaseUrl ? manifest.cdnBaseUrl + "StreamingAssets" : "StreamingAssets",
        setPreloadList: setPreloadList,
        start: function () {
            if (started) return;
            started = true;
            preloadResources.forEach(function (resource) {
                // 启动预下载主动保留字节，避免 Unity 紧接着使用资源时再次读盘。
                request(resource, true)
                    .catch(error => console.log("[SUDPredownload] preload failed", JSON.stringify({ url: resource.url, error: error.message || String(error) })));
            });
        },
        setUnityReady: function () {
            concurrency = Math.max(1, Math.min(10, Number(manifest.runtimeConcurrency || 1)));
            pumpQueue();
        },
    };
})(typeof window !== "undefined" ? window : globalThis);
