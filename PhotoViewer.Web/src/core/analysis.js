/**
 * 分析栏编排（ANALYSIS-01–11、EDITION-02/03）。
 *
 * new AnalysisPresenter({ t, clock, store, dino, onChange, onLocate, thumbnailsBusy })
 *   t(name)                 设计令牌创建的函数
 *   clock                   可注入时间源（clock.js）
 *   store                   { read(photo, { signal }), compute(photo, { signal, variant, scope }), extractGroup(photos) }
 *   dino                    是否具备 DINO 能力（标准版为 false）
 *   onChange()              对外状态变化后的回调
 *   onLocate(u, v)          双击细节预览时抛出的定位事件（显示朝向归一化坐标）
 *   thumbnailsBusy()        ANALYSIS-09：缩略图通道是否忙，未提取回退让位时一并等待
 *
 * 公开 API：
 *   setVisible(v) / setPhoto(photo|null) / setImmediateDino(v) / setImmediateCv(v)
 *   setEnhanced(v) / setDisplaySurfaceReady(v)
 *   tiles() -> [{ kind, label, state: "content"|"empty"|"pending"|"failed", message }]
 *   crosshair -> { u, v } | null；hoverKind -> "focus"|"center"|null
 *   cosineMap() -> Float32Array(patchGrid²) | null
 *   diagnostics() -> Features | null
 *   histogram() -> { r, g, b, luma, count } | null
 *   clickDiagnostic(u, v) / clickBlank()
 *   hoverDetail(kind|null) / locateDetail(kind)
 *   detailCrop(kind, tileSizePt, dpr, sourceSize) -> { cx, cy, w, h } | null
 *   highlightRect(kind, tileSizePt, dpr, sourceSize) -> { cx, cy, w, h } | null
 *   setHistogramSource(pixels, width, height)
 *   refresh() / invalidate() / removeCachedFeatures(photo)
 */
import { PATCH_DIM } from "./types.js";
import { cosine } from "./features.js";

const DETAIL_KINDS = ["focus", "center"];
// ANALYSIS-09：让位等待同时观察主图上屏与缩略图通道，轮询间隔只需覆盖肉眼可辨的变化。
const YIELD_POLL_MS = 50;

export function isValidFocus(focus) {
    if (!focus) return false;
    const values = [focus.x, focus.y, focus.w, focus.h];
    if (!values.every((value) => typeof value === "number" && Number.isFinite(value))) return false;
    if (!(focus.w > 0) || !(focus.h > 0)) return false;
    return focus.x >= 0 && focus.x <= 1 && focus.y >= 0 && focus.y <= 1;
}

function clamp(value, min, max) {
    return Math.min(max, Math.max(min, value));
}

function clampCenter(center, size, total) {
    const half = Math.floor(size / 2);
    return clamp(Math.round(center), half, total - size + half);
}

function sourceWidth(sourceSize) {
    return sourceSize?.width ?? sourceSize?.w ?? 0;
}

function sourceHeight(sourceSize) {
    return sourceSize?.height ?? sourceSize?.h ?? 0;
}

function hasDinoData(features) {
    return Boolean(features && (features.patches || features.pca));
}

function hasCvData(features) {
    return Boolean(features && features.sharpness && features.shake);
}

function dinoView(features) {
    if (!features) return null;
    return {
        pca: features.pca,
        patches: features.patches,
        cls: features.cls,
        verdict: features.verdict,
    };
}

function cvView(features) {
    if (!features) return null;
    return {
        sharpness: features.sharpness,
        shake: features.shake,
        verdict: features.verdict,
    };
}

function newSide() {
    return {
        route: "idle",
        state: "idle",
        error: null,
        render: null,
        controller: null,
    };
}

function buildHistogram(pixels, width, height) {
    const r = new Uint32Array(256);
    const g = new Uint32Array(256);
    const b = new Uint32Array(256);
    const luma = new Uint32Array(256);
    const count = width * height;
    for (let i = 0, offset = 0; i < count; i++, offset += 4) {
        const rv = pixels[offset];
        const gv = pixels[offset + 1];
        const bv = pixels[offset + 2];
        r[rv] += 1;
        g[gv] += 1;
        b[bv] += 1;
        luma[Math.round(0.2126 * rv + 0.7152 * gv + 0.0722 * bv)] += 1;
    }
    return { r, g, b, luma, count };
}

export class AnalysisPresenter {
    constructor({ t, clock, store, dino = true, onChange = null, onLocate = null, thumbnailsBusy = () => false }) {
        this.t = t;
        this.clock = clock;
        this.store = store;
        this.onChange = onChange;
        this.onLocate = onLocate;
        this.dino = dino !== false;
        // ANALYSIS-09：未提取回退让位时，缩略图通道也必须空闲。
        this.thumbnailsBusy = typeof thumbnailsBusy === "function" ? thumbnailsBusy : () => false;

        this.visible = false;
        this.photo = null;
        this.immediateDino = false;
        this.immediateCv = false;
        this.enhanced = false;
        this.displaySurfaceReady = false;

        this.hover = null;
        this.crosshairPos = null;
        this.cosineRef = this.centerCell();
        this.cosineData = null;

        this.dinoSide = newSide();
        this.cvSide = newSide();
        this.libraryState = "idle";
        this.libraryError = null;
        this.diagnosticsView = null;

        this.histogramData = null;
        this.histogramFrame = null;
        this.histogramPending = false;

        this.cache = new Map();
        this.generation = 0;
        this.controller = null;
        this.timers = new Map();
    }

    get crosshair() {
        return this.crosshairPos;
    }

    get hoverKind() {
        return this.hover;
    }

    get error() {
        return this.dinoSide.error ?? this.cvSide.error ?? this.libraryError;
    }

    get route() {
        if (this.libraryState === "loading") return "library";
        if (this.dino && this.dinoSide.state === "loading") return this.dinoSide.route;
        if (this.cvSide.state === "loading") return this.cvSide.route;
        if (this.dino && this.dinoSide.route === "enhanced") return "enhanced";
        if (this.dino && this.dinoSide.route === "fallback") return "fallback";
        if (this.cvSide.route === "enhanced") return "enhanced";
        if (this.cvSide.route === "fallback") return "fallback";
        return "library";
    }

    get routeState() {
        return this.loading ? "loading" : this.error ? "failed" : "done";
    }

    get loading() {
        return this.libraryState === "loading"
            || (this.dino && this.dinoSide.state === "loading")
            || this.cvSide.state === "loading";
    }

    patchGrid() {
        return this.t("analysis.dino.patchGrid");
    }

    centerCell() {
        const center = Math.floor(this.patchGrid() / 2);
        return { x: center, y: center };
    }

    diagnosticKinds() {
        return this.dino
            ? ["pca", "cosine", "sharpness", "shake"]
            : ["sharpness", "shake"];
    }

    sideFor(kind) {
        // 路由用 "dino"/"cv"，瓦片用 pca/cosine/sharpness/shake，两者指向同一侧。
        return kind === "dino" || kind === "pca" || kind === "cosine" ? this.dinoSide : this.cvSide;
    }

    setVisible(value) {
        const next = Boolean(value);
        if (this.visible === next) return;
        this.visible = next;
        if (!next) {
            // ANALYSIS-01：隐藏即取消在途任务，再清空内容与准星。
            this.cancelInFlight();
            this.clearSession();
        }
        this.reroute();
        this.emit();
    }

    setPhoto(photo) {
        const next = photo ?? null;
        if (!next && !this.photo) return;
        if (next && this.photo && next.id === this.photo.id) {
            // ANALYSIS-06：同一张照片的 EXIF 异步到达只替换对象，不重排、不闪烁。
            if (next !== this.photo) {
                this.photo = next;
                this.emit();
            }
            return;
        }
        this.photo = next;
        this.hover = null;
        if (!next) this.crosshairPos = null;
        this.cosineRef = this.centerCell();
        this.cosineData = null;
        this.libraryError = null;
        this.clearHistogram();
        this.reroute();
        this.emit();
    }

    setImmediateDino(value) {
        const next = Boolean(value) && this.dino;
        if (this.immediateDino === next) return;
        this.immediateDino = next;
        this.reroute();
        this.emit();
    }

    setImmediateCv(value) {
        const next = Boolean(value);
        if (this.immediateCv === next) return;
        this.immediateCv = next;
        this.reroute();
        this.emit();
    }

    setEnhanced(value) {
        const next = Boolean(value);
        if (this.enhanced === next) return;
        this.enhanced = next;
        this.clearHistogram();
        this.reroute();
        this.emit();
    }

    setDisplaySurfaceReady(value) {
        const next = Boolean(value);
        if (this.displaySurfaceReady === next) return;
        this.displaySurfaceReady = next;
        if (!next) this.clearHistogram();
        this.emit();
    }

    refresh() {
        this.reroute();
        this.emit();
    }

    invalidate() {
        // SCOPE-03：清库同时清进程缓存与派生结果，诊断瓦片不保留上一轮的内存结果。
        this.cache.clear();
        this.dinoSide.render = null;
        this.cvSide.render = null;
        this.diagnosticsView = null;
        this.cosineData = null;
        this.reroute();
        this.emit();
    }

    clearSession() {
        this.hover = null;
        this.crosshairPos = null;
        this.libraryState = "idle";
        this.libraryError = null;
        this.dinoSide = newSide();
        this.cvSide = newSide();
        this.diagnosticsView = null;
        this.cosineRef = this.centerCell();
        this.cosineData = null;
        this.clearHistogram();
    }

    removeCachedFeatures(photo) {
        if (!photo) return;
        this.cache.delete(this.libraryKey(photo));
        for (const kind of ["dino", "cv"]) {
            for (const variant of ["original", "enhanced"]) {
                this.cache.delete(this.computeKey(photo, kind, variant));
            }
        }
        this.libraryState = "idle";
    }

    libraryKey(photo) {
        const scope = this.dino ? "all" : "cv";
        return photo.fingerprint ? `fp:${photo.fingerprint}:${scope}` : `id:${photo.id}:${scope}`;
    }

    libraryView(features) {
        if (!features || this.dino) return features;
        // EDITION-03/05：标准版只保留 CV 结果，不缓存 DINO patch。
        return cvView(features);
    }

    computeKey(photo, kind, variant) {
        return `${photo.id}#${kind}#${variant}`;
    }

    immediateFor(kind) {
        return kind === "dino" ? this.immediateDino : this.immediateCv;
    }

    reroute() {
        this.generation += 1;
        this.cancelInFlight();
        if (!this.visible || !this.photo) {
            this.libraryState = "idle";
            this.libraryError = null;
            this.dinoSide = newSide();
            this.cvSide = newSide();
            this.diagnosticsView = null;
            return;
        }
        const photo = this.photo;
        const gen = this.generation;
        this.libraryState = "loading";
        this.libraryError = null;
        // ANALYSIS-10：切图时先保留上一张瓦片，库结果到达后整体替换。
        for (const kind of ["dino", "cv"]) {
            const side = this.sideFor(kind);
            side.route = "library";
            side.state = "loading";
            side.error = null;
        }
        this.emit();
        const cached = this.cache.get(this.libraryKey(photo));
        if (cached) {
            this.applyLibrary(cached, gen);
            return;
        }
        this.startRead(photo, gen);
    }

    startRead(photo, gen) {
        const controller = new AbortController();
        this.controller = controller;
        // EDITION-03/05：标准版只申请 CV 只读结果；AI 版可一次取回两侧。
        const scope = this.dino ? "all" : "cv";
        let request;
        try {
            request = Promise.resolve(this.store.read(photo, { signal: controller.signal, scope }));
        } catch {
            this.controller = null;
            this.failLibrary();
            return;
        }
        request
            .then((features) => {
                if (gen !== this.generation) return;
                this.controller = null;
                const view = this.libraryView(features);
                const usable = this.dino
                    ? Boolean(hasDinoData(view) || hasCvData(view))
                    : hasCvData(view);
                // EDITION-03：空结果不是成功结果，不得写入库缓存。
                if (usable) this.cache.set(this.libraryKey(photo), view);
                this.applyLibrary(features, gen);
            })
            .catch(() => {
                if (gen !== this.generation) return;
                this.controller = null;
                if (controller.signal.aborted) return;
                this.failLibrary();
            });
    }

    applyLibrary(features, gen) {
        if (gen !== this.generation) return;
        this.libraryState = "done";
        this.libraryError = null;
        const missing = [];
        const recompute = [];
        for (const kind of ["dino", "cv"]) {
            const side = this.sideFor(kind);
            if (kind === "dino" && !this.dino) {
                // EDITION-03/05：标准版只读取和派生 CV，不保留 DINO patch。
                side.render = null;
                side.route = "library";
                side.state = "done";
                side.error = null;
                continue;
            }
            const available = kind === "dino" ? hasDinoData(features) : hasCvData(features);
            if (available) {
                side.render = kind === "dino" ? dinoView(features) : cvView(features);
                side.route = "library";
                side.state = "done";
                side.error = null;
                // EDITION-03/09：只读库先出图；雾天增强下启用侧仍需按当前展示图重算，禁用侧保留只读结果。
                if (this.enhanced && this.immediateFor(kind)) recompute.push(kind);
            } else {
                side.render = null;
                side.route = "library";
                side.state = "done";
                side.error = null;
                if (this.immediateFor(kind)) missing.push(kind);
            }
        }
        this.updateDiagnostics();
        this.emit();
        for (const kind of recompute) this.startSideCompute(kind, this.photo, "enhanced", gen);
        for (const kind of missing) this.routeMiss(kind, this.photo, gen);
    }

    failLibrary() {
        this.libraryState = "done";
        this.libraryError = "计算失败";
        for (const kind of ["dino", "cv"]) {
            const side = this.sideFor(kind);
            side.render = null;
            side.route = "library";
            side.state = "failed";
            side.error = this.libraryError;
        }
        this.diagnosticsView = null;
        this.emit();
    }

    routeMiss(kind, photo, gen) {
        if (gen !== this.generation || !photo) return;
        if (!this.immediateFor(kind)) return;
        if (this.enhanced) {
            this.startSideCompute(kind, photo, "enhanced", gen);
            return;
        }
        this.startFallback(kind, photo, gen);
    }

    startFallback(kind, photo, gen) {
        const side = this.sideFor(kind);
        side.route = "fallback";
        side.state = "loading";
        side.error = null;
        // ANALYSIS-09：回退期间该侧诊断瓦片显示“计算中…”，不拿上一张的图冒充当前照片。
        side.render = null;
        this.updateDiagnostics();
        this.emit();
        this.wait(this.t("motion.analysis.fallback.delay"))
            .then((ok) => {
                if (!ok || gen !== this.generation) return false;
                return this.waitForDisplayYield();
            })
            .then((ready) => {
                if (ready === false || gen !== this.generation) return;
                this.startSideCompute(kind, photo, "original", gen);
            });
    }

    startSideCompute(kind, photo, variant, gen = this.generation) {
        if (gen !== this.generation) return;
        const side = this.sideFor(kind);
        const cached = this.cache.get(this.computeKey(photo, kind, variant));
        if (cached) {
            this.applySide(kind, cached, variant === "enhanced" ? "enhanced" : "fallback", gen);
            return;
        }
        side.route = variant === "enhanced" ? "enhanced" : "fallback";
        side.state = "loading";
        side.error = null;
        side.render = null;
        this.updateDiagnostics();
        this.emit();
        const controller = new AbortController();
        side.controller = controller;
        let request;
        try {
            // EDITION-03：两侧各按自己的能力请求，另一侧不得随这次计算顺带出图。
            request = Promise.resolve(this.store.compute(photo, { signal: controller.signal, variant, scope: kind }));
        } catch {
            side.controller = null;
            this.failSide(kind);
            return;
        }
        request
            .then((features) => {
                if (gen !== this.generation) return;
                side.controller = null;
                const view = kind === "dino" ? dinoView(features) : cvView(features);
                const usable = kind === "dino" ? hasDinoData(view) : hasCvData(view);
                // EDITION-03：空结果不得缓存为成功，也不得冒充该侧已出图。
                if (!usable) throw new Error("缺少特征结果");
                // SCOPE-03 / EDITION-05：现算产物只进进程缓存，不改 photo.extracted、不写库。
                this.cache.set(this.computeKey(photo, kind, variant), view);
                this.applySide(kind, view, variant === "enhanced" ? "enhanced" : "fallback", gen);
            })
            .catch(() => {
                if (gen !== this.generation) return;
                side.controller = null;
                if (controller.signal.aborted) return;
                this.failSide(kind);
            });
    }

    applySide(kind, view, route, gen) {
        if (gen !== this.generation) return;
        const side = this.sideFor(kind);
        side.render = view;
        side.route = route;
        side.state = "done";
        side.error = null;
        this.updateDiagnostics();
        this.emit();
    }

    failSide(kind) {
        // ANALYSIS-10 / EDITION-03：单侧失败回落到可区分的失败状态，另一侧保持原样。
        const side = this.sideFor(kind);
        side.render = null;
        side.state = "failed";
        side.error = "计算失败";
        this.updateDiagnostics();
        this.emit();
    }

    updateDiagnostics() {
        const dino = this.dino ? this.dinoSide.render : null;
        const cv = this.cvSide.render;
        this.diagnosticsView = dino || cv ? { ...(dino ?? {}), ...(cv ?? {}) } : null;
    }

    wait(ms) {
        return new Promise((resolve) => {
            const id = this.clock.setTimeout(() => {
                this.timers.delete(id);
                resolve(true);
            }, ms);
            this.timers.set(id, resolve);
        });
    }

    waitForDisplayYield() {
        if (this.displayReady()) return Promise.resolve(true);
        return new Promise((resolve) => {
            let settled = false;
            let poll = null;
            let deadline = null;
            const clear = (id) => {
                if (id === null) return null;
                this.clock.clearTimeout(id);
                this.timers.delete(id);
                return null;
            };
            const finish = (value) => {
                if (settled) return;
                settled = true;
                poll = clear(poll);
                deadline = clear(deadline);
                resolve(value);
            };
            const arm = (ms, callback) => {
                let id = null;
                id = this.clock.setTimeout(() => {
                    this.timers.delete(id);
                    callback();
                }, ms);
                this.timers.set(id, () => finish(false));
                return id;
            };
            const tick = () => {
                poll = null;
                if (settled) return;
                if (this.displayReady()) {
                    finish(true);
                    return;
                }
                poll = arm(YIELD_POLL_MS, tick);
            };
            // ANALYSIS-09：最长等待 motion.analysis.fallback.yieldTimeout，超时后照常计算。
            deadline = arm(this.t("motion.analysis.fallback.yieldTimeout"), () => finish(true));
            poll = arm(YIELD_POLL_MS, tick);
        });
    }

    displayReady() {
        return this.displaySurfaceReady && !this.thumbnailsBusy();
    }

    cancelInFlight() {
        if (this.controller) {
            this.controller.abort();
            this.controller = null;
        }
        for (const side of [this.dinoSide, this.cvSide]) {
            if (side.controller) {
                side.controller.abort();
                side.controller = null;
            }
        }
        for (const [id, resolve] of this.timers) {
            this.clock.clearTimeout(id);
            resolve(false);
        }
        this.timers.clear();
    }

    tiles() {
        const photo = this.photo;
        const list = [];
        const detailReady = Boolean(photo) && this.displaySurfaceReady;
        if (isValidFocus(photo?.focus)) list.push(this.detailTile("focus", detailReady));
        list.push(this.detailTile("center", detailReady));
        list.push(this.histogramTile());
        for (const kind of this.diagnosticKinds()) list.push(this.diagnosticTile(kind));
        return list;
    }

    detailTile(kind, ready) {
        return {
            kind,
            label: kind === "focus" ? "对焦点" : "中心",
            state: ready ? "content" : "empty",
            message: null,
        };
    }

    histogramTile() {
        let state = "empty";
        let message = null;
        if (this.photo && this.displaySurfaceReady) {
            if (this.histogramData) {
                state = "content";
            } else if (this.histogramPending) {
                state = "pending";
                message = "计算中…";
            }
        }
        return { kind: "histogram", label: "直方图", state, message };
    }

    diagnosticTile(kind) {
        const side = this.sideFor(kind);
        let state = "empty";
        let message = "未提取";
        if (!this.photo) {
            return { kind, label: this.labelFor(kind), state, message };
        }
        if (side.state === "failed") {
            state = "failed";
            message = side.error ?? "计算失败";
        } else if (side.state === "loading" && side.route !== "library") {
            state = "pending";
            message = "计算中…";
        } else if (side.render) {
            // ANALYSIS-10：只读库读取期间保留上一张瓦片直到新数据就绪；计算路径不保留。
            state = "content";
            message = null;
        } else if (side.state === "loading") {
            state = "pending";
            message = "计算中…";
        }
        return { kind, label: this.labelFor(kind), state, message };
    }

    labelFor(kind) {
        switch (kind) {
            case "focus":
                return "对焦点";
            case "center":
                return "中心";
            case "histogram":
                return "直方图";
            case "pca":
                return "DINO PCA";
            case "cosine":
                // ANALYSIS-02：Cosine 角标写参考点网格坐标。
                return `Cosine ${this.cosineRef.x},${this.cosineRef.y}`;
            case "sharpness":
                return "锐度";
            case "shake":
                return this.cvSide.render?.verdict ?? "抖动拖影";
            default:
                return kind;
        }
    }

    diagnostics() {
        return this.diagnosticsView;
    }

    cosineMap() {
        // EDITION-03：标准版不建立 cosine 参考点，也不派生相似度图。
        if (!this.dino) return null;
        const features = this.dinoSide.render;
        if (!features || !features.patches) return null;
        if (this.cosineData) return this.cosineData;
        const grid = this.patchGrid();
        const reference = (this.cosineRef.y * grid + this.cosineRef.x) * PATCH_DIM;
        const map = new Float32Array(grid * grid);
        for (let cell = 0; cell < map.length; cell++) {
            map[cell] = cosine(features.patches, features.patches, cell * PATCH_DIM, reference, PATCH_DIM);
        }
        this.cosineData = map;
        return map;
    }

    clickDiagnostic(u, v) {
        if (!this.visible || !this.photo) return;
        if (!Number.isFinite(u) || !Number.isFinite(v) || u < 0 || v < 0 || u > 1 || v > 1) {
            this.clickBlank();
            return;
        }
        this.crosshairPos = { u, v };
        if (this.dino) {
            const features = this.dinoSide.render;
            if (features?.patches) {
                const grid = this.patchGrid();
                const gx = clamp(Math.floor(u * grid), 0, grid - 1);
                const gy = clamp(Math.floor(v * grid), 0, grid - 1);
                // ANALYSIS-08：重复点同一格不重算 cosine。
                if (gx !== this.cosineRef.x || gy !== this.cosineRef.y) {
                    this.cosineRef = { x: gx, y: gy };
                    this.cosineData = null;
                }
            }
        }
        this.emit();
    }

    clickBlank() {
        if (!this.visible) return;
        if (!this.crosshairPos) return;
        this.crosshairPos = null;
        this.emit();
    }

    detailCenter(kind) {
        const focus = this.photo?.focus;
        if (kind === "focus" && isValidFocus(focus)) return { u: focus.x, v: focus.y };
        return { u: 0.5, v: 0.5 };
    }

    detailCrop(kind, tileSizePt, dpr, sourceSize) {
        const width = sourceWidth(sourceSize);
        const height = sourceHeight(sourceSize);
        if (!(width > 0) || !(height > 0)) return null;
        // ANALYSIS-06：裁剪窗口 = 瓦片边长的物理像素方块，代理期不放大代理冒充 1:1 细节。
        const scale = Number.isFinite(dpr) && dpr > 0 ? dpr : 1;
        const physical = Math.max(1, Math.round((Number.isFinite(tileSizePt) ? tileSizePt : 1) * scale));
        const w = Math.min(width, physical);
        const h = Math.min(height, physical);
        const center = this.detailCenter(kind);
        return {
            cx: clampCenter(center.u * width, w, width),
            cy: clampCenter(center.v * height, h, height),
            w,
            h,
        };
    }

    highlightRect(kind, tileSizePt, dpr, sourceSize) {
        const focus = this.photo?.focus;
        const width = sourceWidth(sourceSize);
        const height = sourceHeight(sourceSize);
        if (kind === "focus" && isValidFocus(focus) && width > 0 && height > 0) {
            // ANALYSIS-04：对焦点绿框用实际对焦框尺寸，中心瓦片用裁剪窗口。
            const w = clamp(Math.round(focus.w * width), 1, width);
            const h = clamp(Math.round(focus.h * height), 1, height);
            return {
                cx: clampCenter(focus.x * width, w, width),
                cy: clampCenter(focus.y * height, h, height),
                w,
                h,
            };
        }
        return this.detailCrop(kind, tileSizePt, dpr, sourceSize);
    }

    hoverDetail(kind = null) {
        const next = DETAIL_KINDS.includes(kind) ? kind : null;
        if (this.hover !== next) {
            this.hover = next;
            this.emit();
        }
        return this.hover;
    }

    locateDetail(kind) {
        if (!this.photo || !DETAIL_KINDS.includes(kind)) return null;
        const center = this.detailCenter(kind);
        this.onLocate?.(center.u, center.v);
        return { u: center.u, v: center.v };
    }

    histogram() {
        return this.histogramData;
    }

    setHistogramSource(pixels, width, height) {
        this.cancelHistogramFrame();
        if (!this.visible || !this.photo || !this.displaySurfaceReady) {
            this.histogramData = null;
            this.emit();
            return;
        }
        if (!pixels || !(width > 0) || !(height > 0) || pixels.length < width * height * 4) {
            this.histogramData = null;
            this.emit();
            return;
        }
        const gen = this.generation;
        this.histogramPending = true;
        // ANALYSIS-07：多请求在途时以最后一次为准，过期结果丢弃。
        this.histogramFrame = this.clock.requestFrame(() => {
            this.histogramFrame = null;
            if (gen !== this.generation) return;
            this.histogramData = buildHistogram(pixels, width, height);
            this.histogramPending = false;
            this.emit();
        });
        this.emit();
    }

    clearHistogram() {
        this.cancelHistogramFrame();
        this.histogramData = null;
        this.histogramPending = false;
    }

    cancelHistogramFrame() {
        if (this.histogramFrame !== null) {
            this.clock.cancelFrame(this.histogramFrame);
            this.histogramFrame = null;
        }
        this.histogramPending = false;
    }

    emit() {
        this.onChange?.();
    }
}
