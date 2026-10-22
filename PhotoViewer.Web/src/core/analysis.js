/**
 * 分析栏编排（ANALYSIS-01–11）。
 *
 * new AnalysisPresenter({ t, clock, store, onChange, onLocate, thumbnailsBusy })
 *   t(name)                 设计令牌创建的函数
 *   clock                   可注入时间源（clock.js）
 *   store                   { read(photo, { signal }), compute(photo, { signal, variant }), extractGroup(photos) }
 *   onChange()              对外状态变化后的回调
 *   onLocate(u, v)          双击细节预览时抛出的定位事件（显示朝向归一化坐标）
 *   thumbnailsBusy()        ANALYSIS-09：缩略图通道是否忙，未提取回退让位时一并等待
 *
 * 公开 API：
 *   setVisible(v) / setPhoto(photo|null) / setImmediateCompute(v) / setEnhanced(v) / setDisplaySurfaceReady(v)
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
const DIAGNOSTIC_KINDS = ["pca", "cosine", "sharpness", "shake"];
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
    constructor({ t, clock, store, onChange = null, onLocate = null, thumbnailsBusy = () => false }) {
        this.t = t;
        this.clock = clock;
        this.store = store;
        this.onChange = onChange;
        this.onLocate = onLocate;
        // ANALYSIS-09：未提取回退让位时，缩略图通道也必须空闲。
        this.thumbnailsBusy = typeof thumbnailsBusy === "function" ? thumbnailsBusy : () => false;

        this.visible = false;
        this.photo = null;
        this.immediateCompute = false;
        this.enhanced = false;
        this.displaySurfaceReady = false;

        this.hover = null;
        this.crosshairPos = null;
        this.cosineRef = this.centerCell();
        this.cosineData = null;

        this.render = null;
        this.route = "idle";
        this.routeState = "idle";
        this.routeError = null;

        this.histogramData = null;
        this.histogramFrame = null;
        this.histogramPending = false;

        this.cache = new Map();
        this.generation = 0;
        this.controller = null;
        this.timers = new Set();
        this.surfaceWaiter = null;
    }

    get crosshair() {
        return this.crosshairPos;
    }

    get hoverKind() {
        return this.hover;
    }

    get error() {
        return this.routeError;
    }

    patchGrid() {
        return this.t("analysis.dino.patchGrid");
    }

    centerCell() {
        const center = Math.floor(this.patchGrid() / 2);
        return { x: center, y: center };
    }

    setVisible(value) {
        const next = Boolean(value);
        if (this.visible === next) return;
        this.visible = next;
        if (!next) this.clearSession();
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
        this.routeError = null;
        this.clearHistogram();
        this.reroute();
        this.emit();
    }

    setImmediateCompute(value) {
        const next = Boolean(value);
        if (this.immediateCompute === next) return;
        this.immediateCompute = next;
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
        this.resumeSurfaceWaiter();
        this.emit();
    }

    refresh() {
        this.reroute();
        this.emit();
    }

    invalidate() {
        // SCOPE-03：清库同时清进程缓存与派生结果，诊断瓦片不保留上一轮的内存结果。
        this.cache.clear();
        this.render = null;
        this.cosineData = null;
        this.reroute();
        this.emit();
    }

    clearSession() {
        this.hover = null;
        this.crosshairPos = null;
        this.render = null;
        this.route = "idle";
        this.routeState = "idle";
        this.routeError = null;
        this.cosineRef = this.centerCell();
        this.cosineData = null;
        this.clearHistogram();
    }

    removeCachedFeatures(photo) {
        if (!photo) return;
        this.cache.delete(this.libraryKey(photo));
        this.cache.delete(this.computeKey(photo, "original"));
        this.cache.delete(this.computeKey(photo, "enhanced"));
    }

    libraryKey(photo) {
        return photo.fingerprint ? `fp:${photo.fingerprint}` : `id:${photo.id}`;
    }

    computeKey(photo, variant) {
        return `${photo.id}#${variant}`;
    }

    reroute() {
        this.generation += 1;
        this.cancelInFlight();
        if (!this.visible || !this.photo) {
            this.render = null;
            this.route = "idle";
            this.routeState = "idle";
            this.routeError = null;
            return;
        }
        const photo = this.photo;
        // ANALYSIS-09：三条路径同一时刻只走一条，增强联动优先于只读库。
        if (this.immediateCompute && this.enhanced) {
            const cached = this.cache.get(this.computeKey(photo, "enhanced"));
            if (cached) {
                this.applyRender(cached, "enhanced");
                return;
            }
            this.startCompute(photo, "enhanced");
            return;
        }
        const cached = this.cache.get(this.libraryKey(photo));
        if (cached) {
            this.applyRender(cached, "library");
            return;
        }
        this.startRead(photo);
    }

    startRead(photo) {
        const gen = this.generation;
        const controller = new AbortController();
        this.controller = controller;
        this.route = "library";
        this.routeState = "loading";
        this.routeError = null;
        this.emit();
        let request;
        try {
            request = Promise.resolve(this.store.read(photo, { signal: controller.signal }));
        } catch (error) {
            this.controller = null;
            this.fail();
            return;
        }
        request
            .then((features) => {
                if (gen !== this.generation) return;
                this.controller = null;
                if (features) {
                    this.cache.set(this.libraryKey(photo), features);
                    this.applyRender(features, "library");
                    return;
                }
                this.onLibraryMiss(photo);
            })
            .catch(() => {
                if (gen !== this.generation) return;
                this.controller = null;
                if (controller.signal.aborted) return;
                this.fail();
            });
    }

    onLibraryMiss(photo) {
        if (!this.immediateCompute) {
            // ANALYSIS-10：即时计算关闭时库与缓存都无结果即“未提取”，不后台现算。
            this.render = null;
            this.route = "library";
            this.routeState = "done";
            this.routeError = null;
            this.emit();
            return;
        }
        const cached = this.cache.get(this.computeKey(photo, "original"));
        if (cached) {
            this.applyRender(cached, "fallback");
            return;
        }
        this.startFallback(photo);
    }

    startFallback(photo) {
        const gen = this.generation;
        this.route = "fallback";
        this.routeState = "loading";
        this.routeError = null;
        // ANALYSIS-09：回退期间四张诊断瓦片显示“计算中…”，不拿上一张的图冒充当前照片。
        this.render = null;
        this.emit();
        this.wait(this.t("motion.analysis.fallback.delay"))
            .then(() => {
                if (gen !== this.generation) return false;
                return this.waitForDisplayYield();
            })
            .then((ready) => {
                if (gen !== this.generation || ready === false) return null;
                return this.startCompute(photo, "original", gen);
            })
            .catch(() => {
                if (gen !== this.generation) return;
                this.fail();
            });
    }

    startCompute(photo, variant, gen = this.generation) {
        if (gen !== this.generation) return Promise.resolve();
        this.route = variant === "enhanced" ? "enhanced" : "fallback";
        this.routeState = "loading";
        this.routeError = null;
        this.render = null;
        this.emit();
        const controller = new AbortController();
        this.controller = controller;
        let request;
        try {
            request = Promise.resolve(this.store.compute(photo, { signal: controller.signal, variant }));
        } catch (error) {
            this.controller = null;
            this.fail();
            return Promise.resolve();
        }
        return request
            .then((features) => {
                if (gen !== this.generation) return;
                this.controller = null;
                if (!features) throw new Error("缺少特征结果");
                // SCOPE-03：现算产物只进进程缓存，不改 photo.extracted、不写库。
                this.cache.set(this.computeKey(photo, variant), features);
                this.applyRender(features, variant === "enhanced" ? "enhanced" : "fallback");
            })
            .catch(() => {
                if (gen !== this.generation) return;
                this.controller = null;
                if (controller.signal.aborted) return;
                this.fail();
            });
    }

    wait(ms) {
        return new Promise((resolve) => {
            const id = this.clock.setTimeout(() => {
                this.timers.delete(id);
                resolve();
            }, ms);
            this.timers.add(id);
        });
    }

    waitForDisplayYield() {
        if (this.displayReady()) return Promise.resolve(true);
        return new Promise((resolve) => {
            let settled = false;
            let poll = null;
            let deadline = null;
            const finish = () => {
                if (settled) return;
                settled = true;
                this.surfaceWaiter = null;
                if (poll !== null) {
                    this.clock.clearTimeout(poll);
                    this.timers.delete(poll);
                    poll = null;
                }
                if (deadline !== null) {
                    this.clock.clearTimeout(deadline);
                    this.timers.delete(deadline);
                    deadline = null;
                }
                resolve(true);
            };
            const tick = () => {
                poll = null;
                if (settled) return;
                if (this.displayReady()) {
                    finish();
                    return;
                }
                poll = this.clock.setTimeout(tick, YIELD_POLL_MS);
                this.timers.add(poll);
            };
            // ANALYSIS-09：最长等待 motion.analysis.fallback.yieldTimeout，超时后照常计算。
            deadline = this.clock.setTimeout(finish, this.t("motion.analysis.fallback.yieldTimeout"));
            this.timers.add(deadline);
            poll = this.clock.setTimeout(tick, YIELD_POLL_MS);
            this.timers.add(poll);
            this.surfaceWaiter = (force) => {
                if (force || this.displayReady()) finish();
            };
        });
    }

    displayReady() {
        return this.displaySurfaceReady && !this.thumbnailsBusy();
    }

    resumeSurfaceWaiter(force = false) {
        if (this.surfaceWaiter) this.surfaceWaiter(force);
    }

    cancelInFlight() {
        if (this.controller) {
            this.controller.abort();
            this.controller = null;
        }
        for (const id of this.timers) this.clock.clearTimeout(id);
        this.timers.clear();
        // 取消时强制解开让位等待，避免被清掉定时器后 Promise 永远挂起。
        this.resumeSurfaceWaiter(true);
    }

    applyRender(features, route) {
        this.render = { photoId: this.photo?.id ?? null, features, route };
        this.route = route;
        this.routeState = "done";
        this.routeError = null;
        this.cosineData = null;
        this.emit();
    }

    fail() {
        // ANALYSIS-10：失败回落到空状态，与“未提取”文案可区分。
        this.render = null;
        this.routeState = "failed";
        this.routeError = "计算失败";
        this.emit();
    }

    tiles() {
        const photo = this.photo;
        const list = [];
        const detailReady = Boolean(photo) && this.displaySurfaceReady;
        if (isValidFocus(photo?.focus)) list.push(this.detailTile("focus", detailReady));
        list.push(this.detailTile("center", detailReady));
        list.push(this.histogramTile());
        for (const kind of DIAGNOSTIC_KINDS) list.push(this.diagnosticTile(kind));
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
        let state = "empty";
        let message = "未提取";
        if (!this.photo) {
            return { kind, label: this.labelFor(kind), state, message };
        }
        if (this.routeState === "failed") {
            state = "failed";
            message = this.routeError ?? "计算失败";
        } else if (this.routeState === "loading" && this.route !== "library") {
            state = "pending";
            message = "计算中…";
        } else if (this.render) {
            // ANALYSIS-10：只读库读取期间保留上一张瓦片直到新数据就绪；计算路径不保留。
            state = "content";
            message = null;
        } else if (this.routeState === "loading") {
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
                return this.render?.features?.verdict ?? "抖动拖影";
            default:
                return kind;
        }
    }

    diagnostics() {
        return this.render?.features ?? null;
    }

    cosineMap() {
        const features = this.render?.features;
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
        const features = this.render?.features;
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
