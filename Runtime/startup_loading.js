(function () {
    // MD5 identifies exported file content; it is computed only for newly downloaded bytes.
    function dataMd5(data) {
        const bytes = data instanceof Uint8Array ? data : new Uint8Array(data);
        const shifts = [7,12,17,22,7,12,17,22,7,12,17,22,7,12,17,22,
            5,9,14,20,5,9,14,20,5,9,14,20,5,9,14,20,
            4,11,16,23,4,11,16,23,4,11,16,23,4,11,16,23,
            6,10,15,21,6,10,15,21,6,10,15,21,6,10,15,21];
        const constants = new Int32Array(64);
        for (let i = 0; i < 64; i++) constants[i] = Math.floor(Math.abs(Math.sin(i + 1)) * 4294967296) | 0;
        const words = new Int32Array(16);
        const length = Math.ceil((bytes.length + 9) / 64) * 64;
        const state = [0x67452301, 0xefcdab89 | 0, 0x98badcfe | 0, 0x10325476];
        for (let offset = 0; offset < length; offset += 64) {
            words.fill(0);
            for (let i = 0; i < 64 && offset + i < bytes.length; i++) words[i >>> 2] |= bytes[offset + i] << ((i % 4) * 8);
            if (offset <= bytes.length && bytes.length < offset + 64) words[(bytes.length - offset) >>> 2] |= 0x80 << ((bytes.length % 4) * 8);
            if (offset === length - 64) {
                words[14] = bytes.length << 3;
                words[15] = Math.floor(bytes.length / 0x20000000);
            }
            let a = state[0], b = state[1], c = state[2], d = state[3];
            for (let i = 0; i < 64; i++) {
                let f, g;
                if (i < 16) { f = (b & c) | (~b & d); g = i; }
                else if (i < 32) { f = (d & b) | (~d & c); g = (5 * i + 1) % 16; }
                else if (i < 48) { f = b ^ c ^ d; g = (3 * i + 5) % 16; }
                else { f = c ^ (b | ~d); g = (7 * i) % 16; }
                const sum = (a + f + constants[i] + words[g]) | 0;
                a = d; d = c; c = b;
                b = (b + ((sum << shifts[i]) | (sum >>> (32 - shifts[i])))) | 0;
            }
            state[0] = (state[0] + a) | 0; state[1] = (state[1] + b) | 0;
            state[2] = (state[2] + c) | 0; state[3] = (state[3] + d) | 0;
        }
        return state.map(function (word) {
            let hex = "";
            for (let i = 0; i < 4; i++) hex += ((word >>> (i * 8)) & 255).toString(16).padStart(2, "0");
            return hex;
        }).join("");
    }

    function createRemoteDataPreparer(version) {
        if (!version || !/^[a-f0-9]{32}$/i.test(version.md5) || !Number.isSafeInteger(version.size) || version.size < 0) {
            throw new Error("Invalid exported Unity data version");
        }
        const md5 = version.md5.toLowerCase();
        const cacheDir = ral.env.USER_DATA_PATH + "/sud-unity-data";
        const filePath = cacheDir + "/" + md5 + ".data";
        const partPath = filePath + ".part";
        const fs = ral.getFileSystemManager();
        let dataDownloadXhr = null;
        let preparation = null;
        let dataBytes = null;
        let rejectReady = null;
        let restoreFetch = null;

        function removePart() {
            try { fs.unlinkSync(partPath); } catch (error) {}
        }

        function cleanupOldData() {
            try {
                fs.readdirSync(cacheDir).forEach(function (name) {
                    if (name === md5 + ".data") return;
                    // Only files created by this loader, including the former URL-based cache names.
                    if (!/^(?:[a-f0-9]{32}\.data(?:\.part)?|[a-f0-9]{1,8}-[A-Za-z0-9._-]+\.data(?:\.(?:br|gz|unityweb))?)$/i.test(name)) return;
                    const oldPath = cacheDir + "/" + name;
                    try {
                        if (!fs.statSync(oldPath, false).isFile()) return;
                        fs.unlinkSync(oldPath);
                    } catch (error) { console.warn("[SUD] Old data cleanup skipped: " + oldPath, error); }
                });
            } catch (error) { console.warn("[SUD] Data cache cleanup skipped", error); }
        }

        function writeDataFile(data) {
            return new Promise(function (resolve, reject) {
                try { fs.accessSync(cacheDir); } catch (error) { fs.mkdirSync(cacheDir, true); }
                fs.writeFile({ filePath: partPath, data: data, encoding: "binary", success: resolve, fail: reject });
            }).then(function () {
                if (fs.statSync(partPath, false).size !== version.size) throw new Error("Unity data cached size mismatch");
                let targetExists = false;
                try { fs.accessSync(filePath); targetExists = true; } catch (missing) {}
                // A wrong-size file at this version path was already rejected by prepare().
                if (targetExists) fs.unlinkSync(filePath);
                return new Promise(function (resolve, reject) {
                    fs.rename({ oldPath: partPath, newPath: filePath, success: resolve, fail: reject });
                });
            }).then(cleanupOldData);
        }

        function abort() {
            if (dataDownloadXhr) dataDownloadXhr.abort();
            if (restoreFetch) restoreFetch();
            if (rejectReady) rejectReady(new Error("Unity data preparation aborted"));
        }

        function prepare(url, callbacks) {
            callbacks = callbacks || {};
            if (preparation) return preparation;
            let cached = null;
            try { cached = fs.statSync(filePath, false); } catch (error) {}
            const cacheHit = cached && cached.isFile() && cached.size === version.size;
            let resolveReady;
            preparation = {
                url: cacheHit ? filePath : url,
                ready: new Promise(function (resolve, reject) {
                    resolveReady = resolve;
                    rejectReady = reject;
                }),
            };

            // Install before Unity starts. Only its first matching GET consumes this bridge.
            const originalFetch = window.fetch;
            restoreFetch = function () {
                window.fetch = originalFetch;
                restoreFetch = null;
                dataBytes = null;
            };
            window.fetch = function (requestUrl, options = {}) {
                if (requestUrl !== preparation.url || (options.method || "GET").toUpperCase() !== "GET") {
                    return originalFetch.apply(this, arguments);
                }
                const bytes = dataBytes;
                restoreFetch();
                if (cacheHit) {
                    // The adapter's fetch resolves after its local file read finishes.
                    const response = originalFetch.apply(this, arguments);
                    response.then(function () { resolveReady(); }, rejectReady);
                    return response;
                }
                return bytes.then(function (buffer) {
                    return {
                        ok: true,
                        status: 200,
                        url: url,
                        headers: {
                            get: function (name) {
                                return name.toLowerCase() === "content-length" ? String(buffer.byteLength) : null;
                            },
                        },
                        arrayBuffer: function () { return Promise.resolve(buffer); },
                    };
                });
            };

            if (cacheHit) {
                if (callbacks.onCacheHit) callbacks.onCacheHit(filePath);
                return preparation;
            }
            removePart();
            dataBytes = new Promise(function (resolve, reject) {
                const xhr = dataDownloadXhr = new XMLHttpRequest();
                xhr.open("GET", url, true);
                xhr.timeout = 60000;
                xhr.responseType = "arraybuffer";
                xhr.onprogress = function (event) {
                    if (callbacks.onProgress) callbacks.onProgress(event.loaded || 0, event.total || version.size);
                };
                xhr.onload = function () {
                    if (xhr.status < 200 || xhr.status >= 300) {
                        reject(new Error("startup loading data download failed: " + url + ", status: " + xhr.status));
                        return;
                    }
                    resolve(xhr.response);
                };
                xhr.onerror = function () { reject(new Error("startup loading data download failed: " + url)); };
                xhr.ontimeout = function () { reject(new Error("startup loading data download timeout: " + url)); };
                xhr.onabort = function () { reject(new Error("startup loading data download aborted: " + url)); };
                xhr.send();
            }).then(function (data) {
                if (!(data instanceof ArrayBuffer) || data.byteLength !== version.size) throw new Error("Unity data downloaded size mismatch");
                if (dataMd5(data) !== md5) throw new Error("Unity data downloaded MD5 mismatch");

                // Cache persistence owns the temporary file and must not delay Unity startup.
                // These bytes must stay unchanged until the host finishes writing them.
                writeDataFile(data).catch(function (error) {
                    console.warn("[SUD] Data cache write skipped: " + filePath, error);
                }).finally(removePart);
                return data;
            }).finally(function () {
                dataDownloadXhr = null;
            });
            // Keep readiness independent from Unity's fetch: the loader can swallow fetch errors.
            dataBytes.then(function () { resolveReady(); }, function (error) {
                if (restoreFetch) restoreFetch();
                rejectReady(error);
            });
            return preparation;
        }
        return { prepare: prepare, abort: abort };
    }

    function createStartupLoading(options) {
        options = options || {};
        let startupLoadingCoverUrl = options.coverUrl;
        if (typeof startupLoadingCoverUrl !== "string" || !startupLoadingCoverUrl.trim()) {
            throw new Error("startup loading cover image is required");
        }
        let startupLoadingConfig = options.config || {};
        let remoteDataPreparer = options.remoteDataPreparer || createRemoteDataPreparer(options.dataFile);

        // The host presents native canvases, not DOM layers. Loading and Unity must share one.
        let loadingCanvas = options.canvas;

        let gl = loadingCanvas.getContext("webgl2", {
            alpha: true,
            depth: true,
            stencil: true,
            antialias: false,
            premultipliedAlpha: false,
            preserveDrawingBuffer: false,
            powerPreference: "high-performance",
        });
        // The native host reports a zero-based ACTIVE_TEXTURE; WebGL reports TEXTURE0 + index.
        // Detect the format once on the new context, then restore using the enum activeTexture expects.
        const activeTextureEnumOffset = gl.getParameter(gl.ACTIVE_TEXTURE) === 0 ? gl.TEXTURE0 : 0;

        let width = loadingCanvas.width;
        let height = loadingCanvas.height;
        let currentProgress = 0;
        let targetProgress = 0;
        let progressSpeed = 0;
        let lastRenderTime = Date.now();
        let animationFrameId = null;
        let resolveFinish = null;
        let visible = false;
        let dataProgress = 0;
        let unityProgress = 0;
        let dataProgressWeight = 0.7;
        let progressVisible = false;

        let colorVertexShaderSource = [
            "attribute vec2 a_position;",
            "void main() {",
            "  gl_Position = vec4(a_position, 0.0, 1.0);",
            "}",
        ].join("\n");
        let colorFragmentShaderSource = [
            "precision mediump float;",
            "uniform vec4 u_color;",
            "void main() {",
            "  gl_FragColor = u_color;",
            "}",
        ].join("\n");
        let textureVertexShaderSource = [
            "attribute vec2 a_position;",
            "attribute vec2 a_uv;",
            "varying vec2 v_uv;",
            "void main() {",
            "  gl_Position = vec4(a_position, 0.0, 1.0);",
            "  v_uv = a_uv;",
            "}",
        ].join("\n");
        let textureFragmentShaderSource = [
            "precision mediump float;",
            "uniform sampler2D u_texture;",
            "varying vec2 v_uv;",
            "void main() {",
            "  gl_FragColor = texture2D(u_texture, v_uv);",
            "}",
        ].join("\n");

        function createShader(type, source) {
            let shader = gl.createShader(type);
            gl.shaderSource(shader, source);
            gl.compileShader(shader);
            return shader;
        }

        function createProgram(vertexSource, fragmentSource) {
            let program = gl.createProgram();
            gl.attachShader(program, createShader(gl.VERTEX_SHADER, vertexSource));
            gl.attachShader(program, createShader(gl.FRAGMENT_SHADER, fragmentSource));
            gl.linkProgram(program);
            return program;
        }

        let colorProgram = createProgram(colorVertexShaderSource, colorFragmentShaderSource);
        let textureProgram = createProgram(textureVertexShaderSource, textureFragmentShaderSource);
        let colorPosition = gl.getAttribLocation(colorProgram, "a_position");
        let colorUniform = gl.getUniformLocation(colorProgram, "u_color");
        let texturePosition = gl.getAttribLocation(textureProgram, "a_position");
        let textureUv = gl.getAttribLocation(textureProgram, "a_uv");
        let vertexBuffer = gl.createBuffer();
        let uvBuffer = gl.createBuffer();
        let vertexArray = gl.createVertexArray();
        let coverTexture = null;
        let coverWidth = 0;
        let coverHeight = 0;
        let disposed = false;
        let coverImagePromise = null;
        let coverReadyPromise = null;

        function loadCoverImage() {
            if (!coverImagePromise) {
                coverImagePromise = new Promise(function (resolve, reject) {
                    let image = new Image();
                    image.onload = function () {
                        if (disposed) {
                            return;
                        }

                        if (image.width > startupLoadingConfig.maxCoverSize || image.height > startupLoadingConfig.maxCoverSize) {
                            reject(new Error("startup loading cover width or height must not exceed " + startupLoadingConfig.maxCoverSize));
                            return;
                        }

                        resolve(image);
                    };
                    image.onerror = function () {
                        reject(new Error("startup loading cover load failed: " + startupLoadingCoverUrl));
                    };
                    image.src = startupLoadingCoverUrl;
                });
            }

            return coverImagePromise;
        }

        loadCoverImage();

        function ensureCoverReady() {
            if (coverReadyPromise) {
                return coverReadyPromise;
            }

            coverReadyPromise = loadCoverImage().then(function (image) {
                coverWidth = image.width;
                coverHeight = image.height;
                coverTexture = gl.createTexture();
                const previousTexture = gl.getParameter(gl.TEXTURE_BINDING_2D);
                gl.bindTexture(gl.TEXTURE_2D, coverTexture);
                gl.texParameteri(gl.TEXTURE_2D, gl.TEXTURE_WRAP_S, gl.CLAMP_TO_EDGE);
                gl.texParameteri(gl.TEXTURE_2D, gl.TEXTURE_WRAP_T, gl.CLAMP_TO_EDGE);
                gl.texParameteri(gl.TEXTURE_2D, gl.TEXTURE_MIN_FILTER, gl.LINEAR);
                gl.texParameteri(gl.TEXTURE_2D, gl.TEXTURE_MAG_FILTER, gl.LINEAR);
                gl.texImage2D(gl.TEXTURE_2D, 0, gl.RGBA, gl.RGBA, gl.UNSIGNED_BYTE, image);
                gl.bindTexture(gl.TEXTURE_2D, previousTexture);
                drawLoading(currentProgress);
                return waitForPresentedFrame();
            });

            return coverReadyPromise;
        }

        function toClipX(x) {
            return x / width * 2 - 1;
        }

        function toClipY(y) {
            return 1 - y / height * 2;
        }

        function rectVertices(x, y, rectWidth, rectHeight) {
            let x1 = toClipX(x);
            let x2 = toClipX(x + rectWidth);
            let y1 = toClipY(y);
            let y2 = toClipY(y + rectHeight);
            return new Float32Array([
                x1, y1, x2, y1, x1, y2,
                x1, y2, x2, y1, x2, y2,
            ]);
        }

        function drawRect(x, y, rectWidth, rectHeight, color) {
            let vertices = rectVertices(x, y, rectWidth, rectHeight);
            gl.useProgram(colorProgram);
            gl.bindBuffer(gl.ARRAY_BUFFER, vertexBuffer);
            gl.bufferData(gl.ARRAY_BUFFER, vertices, gl.STREAM_DRAW);
            gl.enableVertexAttribArray(colorPosition);
            gl.vertexAttribPointer(colorPosition, 2, gl.FLOAT, false, 0, 0);
            gl.uniform4fv(colorUniform, color);
            gl.drawArrays(gl.TRIANGLES, 0, vertices.length / 2);
        }

        function hexToColor(hex, alpha) {
            let value = hex.charAt(0) === "#" ? hex.substring(1) : hex;
            let r = parseInt(value.substring(0, 2), 16) / 255;
            let g = parseInt(value.substring(2, 4), 16) / 255;
            let b = parseInt(value.substring(4, 6), 16) / 255;
            return [r, g, b, alpha];
        }

        function getLoadingScale() {
            let designWidth = startupLoadingConfig.designWidth;
            let designHeight = startupLoadingConfig.designHeight;
            if (!designWidth || !designHeight) {
                return 1;
            }
            if (startupLoadingConfig.scaleMode === "FIXED_WIDTH") {
                return width / designWidth;
            }
            if (startupLoadingConfig.scaleMode === "FIXED_HEIGHT") {
                return height / designHeight;
            }
            return Math.min(width / designWidth, height / designHeight);
        }

        function getStyleWidth(style, scale) {
            if (style.horizontalMarginRatio !== undefined) {
                return width * (1 - style.horizontalMarginRatio * 2);
            }
            if (style.widthRatio !== undefined) {
                return width * style.widthRatio;
            }
            return style.width * scale;
        }

        function getBottomCenterRect(style, scale) {
            let rectWidth = getStyleWidth(style, scale);
            let rectHeight = style.heightRatio !== undefined ? height * style.heightRatio : style.height * scale;
            let bottom = style.bottomRatio !== undefined ? height * style.bottomRatio : style.bottom * scale;
            return {
                x: (width - rectWidth) / 2,
                y: height - bottom - rectHeight,
                width: rectWidth,
                height: rectHeight,
            };
        }

        function coverUvVertices(imageWidth, imageHeight) {
            let imageAspect = imageWidth / imageHeight;
            let canvasAspect = width / height;
            let u0 = 0;
            let u1 = 1;
            let v0 = 0;
            let v1 = 1;

            if (imageAspect > canvasAspect) {
                let visibleWidthRatio = canvasAspect / imageAspect;
                let cropX = (1 - visibleWidthRatio) / 2;
                u0 = cropX;
                u1 = 1 - cropX;
            } else if (imageAspect < canvasAspect) {
                let visibleHeightRatio = imageAspect / canvasAspect;
                let cropY = (1 - visibleHeightRatio) / 2;
                v0 = cropY;
                v1 = 1 - cropY;
            }

            return new Float32Array([
                u0, v0, u1, v0, u0, v1,
                u0, v1, u1, v0, u1, v1,
            ]);
        }

        function drawCover() {
            gl.useProgram(textureProgram);
            gl.bindTexture(gl.TEXTURE_2D, coverTexture);

            gl.bindBuffer(gl.ARRAY_BUFFER, vertexBuffer);
            gl.bufferData(gl.ARRAY_BUFFER, rectVertices(0, 0, width, height), gl.STREAM_DRAW);
            gl.enableVertexAttribArray(texturePosition);
            gl.vertexAttribPointer(texturePosition, 2, gl.FLOAT, false, 0, 0);

            gl.bindBuffer(gl.ARRAY_BUFFER, uvBuffer);
            gl.bufferData(gl.ARRAY_BUFFER, coverUvVertices(coverWidth, coverHeight), gl.STREAM_DRAW);
            gl.enableVertexAttribArray(textureUv);
            gl.vertexAttribPointer(textureUv, 2, gl.FLOAT, false, 0, 0);

            gl.drawArrays(gl.TRIANGLES, 0, 6);
        }

        function drawDigit(digit, x, y, size, color) {
            let segments = [
                [1, 0, 3, 1],
                [4, 1, 1, 3],
                [4, 5, 1, 3],
                [1, 8, 3, 1],
                [0, 5, 1, 3],
                [0, 1, 1, 3],
                [1, 4, 3, 1],
            ];
            let digitSegments = [
                [0, 1, 2, 3, 4, 5],
                [1, 2],
                [0, 1, 6, 4, 3],
                [0, 1, 2, 3, 6],
                [5, 6, 1, 2],
                [0, 5, 6, 2, 3],
                [0, 5, 4, 3, 2, 6],
                [0, 1, 2],
                [0, 1, 2, 3, 4, 5, 6],
                [0, 1, 2, 3, 5, 6],
            ][digit];

            for (let i = 0; i < digitSegments.length; i++) {
                let segment = segments[digitSegments[i]];
                drawRect(x + segment[0] * size, y + segment[1] * size, segment[2] * size, segment[3] * size, color);
            }
        }

        function drawPercentSign(x, y, size, color) {
            drawRect(x, y + size, size, size, color);
            drawRect(x + size * 3, y + size * 7, size, size, color);
            drawRect(x + size * 3, y + size * 2, size, size, color);
            drawRect(x + size * 2, y + size * 3, size, size, color);
            drawRect(x + size, y + size * 4, size, size, color);
            drawRect(x, y + size * 5, size, size, color);
        }

        function drawPercent(progress, rect, style, scale) {
            let percentText = String(Math.round(progress * 100));
            let fontSize = style.fontSizeRatio !== undefined ? height * style.fontSizeRatio : style.fontSize * scale;
            let size = fontSize / 9;
            let charWidth = size * 5;
            let gap = size * 2;
            let totalWidth = percentText.length * charWidth + percentText.length * gap + charWidth;
            let x = rect.x + (rect.width - totalWidth) / 2;
            let lineHeight = style.lineHeightRatio !== undefined
                ? height * style.lineHeightRatio
                : (style.lineHeight || style.fontSize) * scale;
            let y = rect.y + (rect.height - lineHeight) / 2 + (lineHeight - fontSize) / 2;
            let color = hexToColor(style.color, 0.92);

            for (let i = 0; i < percentText.length; i++) {
                drawDigit(Number(percentText[i]), x, y, size, color);
                x += charWidth + gap;
            }
            drawPercentSign(x, y, size, color);
        }

        function drawProgressBar(progress, rect, style, scale) {
            let padding = style.paddingRatio !== undefined ? height * style.paddingRatio : style.padding * scale;
            let innerX = rect.x + padding;
            let innerY = rect.y + padding;
            let innerWidth = Math.max(0, rect.width - padding * 2);
            let innerHeight = Math.max(0, rect.height - padding * 2);

            drawRect(rect.x, rect.y, rect.width, rect.height, [0.22, 0.26, 0.3, 1.0]);
            drawRect(innerX, innerY, innerWidth * progress, innerHeight, hexToColor(style.backgroundColor, 1.0));
        }

        function drawProgress(progress) {
            let scale = getLoadingScale();
            let barStyle = startupLoadingConfig.barConfig.style;
            let textStyle = startupLoadingConfig.textConfig.style;
            let barRect = getBottomCenterRect(barStyle, scale);
            let textRect = getBottomCenterRect(textStyle, scale);

            drawProgressBar(progress, barRect, barStyle, scale);
            drawPercent(progress, textRect, textStyle, scale);
        }

        function drawLoading(progress) {
            currentProgress = progress;
            width = gl.drawingBufferWidth;
            height = gl.drawingBufferHeight;
            // Unity initialization also uses this context. Restore everything this draw changes
            // so Unity's cached bindings still match GL when its first game frame is allowed.
            const program = gl.getParameter(gl.CURRENT_PROGRAM);
            const vao = gl.getParameter(gl.VERTEX_ARRAY_BINDING);
            const arrayBuffer = gl.getParameter(gl.ARRAY_BUFFER_BINDING);
            const activeTexture = gl.getParameter(gl.ACTIVE_TEXTURE) + activeTextureEnumOffset;
            gl.activeTexture(gl.TEXTURE0);
            const texture = gl.getParameter(gl.TEXTURE_BINDING_2D);
            const sampler = gl.getParameter(gl.SAMPLER_BINDING);
            const framebuffer = gl.getParameter(gl.DRAW_FRAMEBUFFER_BINDING);
            const viewport = gl.getParameter(gl.VIEWPORT);
            const clearColor = gl.getParameter(gl.COLOR_CLEAR_VALUE);
            const colorMask = gl.getParameter(gl.COLOR_WRITEMASK);
            const blendSrcRgb = gl.getParameter(gl.BLEND_SRC_RGB);
            const blendDstRgb = gl.getParameter(gl.BLEND_DST_RGB);
            const blendSrcAlpha = gl.getParameter(gl.BLEND_SRC_ALPHA);
            const blendDstAlpha = gl.getParameter(gl.BLEND_DST_ALPHA);
            const blendEquationRgb = gl.getParameter(gl.BLEND_EQUATION_RGB);
            const blendEquationAlpha = gl.getParameter(gl.BLEND_EQUATION_ALPHA);
            const blend = gl.isEnabled(gl.BLEND);
            const disabledCaps = [gl.DEPTH_TEST, gl.STENCIL_TEST, gl.SCISSOR_TEST, gl.CULL_FACE, gl.RASTERIZER_DISCARD];
            const enabledCaps = disabledCaps.map(function (cap) { return gl.isEnabled(cap); });

            gl.bindVertexArray(vertexArray);
            gl.bindSampler(0, null);
            gl.bindFramebuffer(gl.DRAW_FRAMEBUFFER, null);
            disabledCaps.forEach(function (cap) { gl.disable(cap); });
            gl.enable(gl.BLEND);
            gl.blendFunc(gl.SRC_ALPHA, gl.ONE_MINUS_SRC_ALPHA);
            gl.blendEquation(gl.FUNC_ADD);
            gl.colorMask(true, true, true, true);
            gl.viewport(0, 0, width, height);
            gl.clearColor(0, 0, 0, 1);
            gl.clear(gl.COLOR_BUFFER_BIT);

            drawCover();
            if (progressVisible) {
                drawProgress(progress);
            }

            gl.useProgram(program);
            gl.bindVertexArray(vao);
            gl.bindBuffer(gl.ARRAY_BUFFER, arrayBuffer);
            gl.bindTexture(gl.TEXTURE_2D, texture);
            gl.bindSampler(0, sampler);
            gl.activeTexture(activeTexture);
            gl.bindFramebuffer(gl.DRAW_FRAMEBUFFER, framebuffer);
            gl.viewport(viewport[0], viewport[1], viewport[2], viewport[3]);
            gl.clearColor(clearColor[0], clearColor[1], clearColor[2], clearColor[3]);
            gl.colorMask(colorMask[0], colorMask[1], colorMask[2], colorMask[3]);
            gl.blendFuncSeparate(blendSrcRgb, blendDstRgb, blendSrcAlpha, blendDstAlpha);
            gl.blendEquationSeparate(blendEquationRgb, blendEquationAlpha);
            if (!blend) gl.disable(gl.BLEND);
            disabledCaps.forEach(function (cap, index) { if (enabledCaps[index]) gl.enable(cap); });
            gl.flush();
        }

        function clampProgress(progress) {
            if (typeof progress !== "number" || isNaN(progress)) {
                return 0;
            }
            return Math.max(0, Math.min(1, progress));
        }

        function requestAnimationFrameCompat(callback) {
            if (typeof window.requestAnimationFrame === "function") {
                return window.requestAnimationFrame(callback);
            }
            return setTimeout(callback, 16);
        }

        function cancelAnimationFrameCompat(id) {
            if (typeof window.cancelAnimationFrame === "function") {
                window.cancelAnimationFrame(id);
                return;
            }
            clearTimeout(id);
        }

        function waitForPresentedFrame() {
            return new Promise(function (resolve) {
                requestAnimationFrameCompat(function () {
                    setTimeout(resolve, 0);
                });
            });
        }

        function startRenderLoop() {
            if (!visible || disposed || animationFrameId !== null) {
                return;
            }
            animationFrameId = requestAnimationFrameCompat(renderLoading);
        }

        function renderLoading() {
            animationFrameId = null;
            if (!visible || disposed) {
                return;
            }

            const now = Date.now();
            // Unity initialization can block JS; resume smoothly instead of jumping over missed frames.
            const deltaSeconds = Math.min((now - lastRenderTime) / 1000, 0.05);
            lastRenderTime = now;
            const finishing = resolveFinish !== null;
            const diff = targetProgress - currentProgress;
            // Ease the speed as well as the position, keeping it continuous across progress callbacks.
            const desiredSpeed = Math.min(finishing ? 1 : 0.22, diff / (finishing ? 0.18 : 0.9));
            progressSpeed += (desiredSpeed - progressSpeed) * (1 - Math.exp(-deltaSeconds / 0.15));
            currentProgress += Math.min(diff, progressSpeed * deltaSeconds);
            if (targetProgress - currentProgress < (finishing ? 0.001 : 0.0001)) currentProgress = targetProgress;

            drawLoading(currentProgress);
            if (resolveFinish && currentProgress === 1) {
                const complete = resolveFinish;
                resolveFinish = null;
                setTimeout(complete, 0);
                return;
            }
            startRenderLoop();
        }

        function setProgressTarget(progress, immediate) {
            if (immediate) {
                targetProgress = progress;
                currentProgress = progress;
                progressSpeed = 0;
                lastRenderTime = Date.now();
                if (visible && !disposed) {
                    drawLoading(currentProgress);
                }
                return;
            }
            targetProgress = Math.max(targetProgress, progress);
            startRenderLoop();
        }

        function updateProgress() {
            // Completion belongs to finish(), after Unity has successfully started.
            setProgressTarget(Math.min(0.99, dataProgressWeight * dataProgress + (1 - dataProgressWeight) * unityProgress));
        }

        return {
            systemInfoContext: gl,
            show: function () {
                visible = true;
                return ensureCoverReady().then(function () {
                    startRenderLoop();
                });
            },
            hide: function () {
                if (disposed) {
                    return;
                }

                disposed = true;
                visible = false;
                if (animationFrameId !== null) {
                    cancelAnimationFrameCompat(animationFrameId);
                    animationFrameId = null;
                }
                remoteDataPreparer.abort();
                gl.deleteBuffer(vertexBuffer);
                gl.deleteBuffer(uvBuffer);
                gl.deleteVertexArray(vertexArray);
                gl.deleteProgram(colorProgram);
                gl.deleteProgram(textureProgram);
                if (coverTexture) {
                    gl.deleteTexture(coverTexture);
                }
            },
            downloadData: function (url) {
                progressVisible = true;
                setProgressTarget(0, true);
                const preparation = remoteDataPreparer.prepare(url, {
                    onCacheHit: function () {
                        dataProgressWeight = 0;
                    },
                    onProgress: function (loaded, total) {
                        if (total > 0) {
                            dataProgress = Math.max(dataProgress, clampProgress(loaded / total));
                            updateProgress();
                        }
                    },
                });
                return {
                    url: preparation.url,
                    ready: preparation.ready.then(function () {
                        dataProgress = 1;
                        updateProgress();
                    }),
                };
            },
            setUnityProgress: function (progress) {
                unityProgress = Math.max(unityProgress, clampProgress(progress));
                updateProgress();
            },
            finish: function () {
                return new Promise(function (resolve) {
                    resolveFinish = resolve;
                    setProgressTarget(1);
                });
            },
            abort: remoteDataPreparer.abort,
        };
    }

    window.createRemoteDataPreparer = createRemoteDataPreparer;
    window.createStartupLoading = createStartupLoading;
})();
