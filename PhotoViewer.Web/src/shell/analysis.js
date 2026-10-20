/**
 * 分析栏（ANALYSIS-01–11）：对焦点/中心细节、直方图与四张 DINO/CV 诊断瓦片共享准星。
 * mountAnalysis(host, ctx) → { update(state, regions), destroy() }
 * 几何全部来自 state.layout.analysis（tiles 相对分区）；业务判定一律交给 ctx.app 的动作。
 */
import { el, place, setText, touches } from "./dom.js";
import { colorToCss } from "../core/tokens.js";
import { rectToStored } from "../render/procedural.js";

const DETAIL_KINDS = new Set(["focus", "center"]);
const DIAGNOSTIC_KINDS = new Set(["pca", "cosine", "sharpness", "shake"]);

// ANALYSIS-06：方形裁剪块在 8 个方向码下从存储朝向到显示朝向的局部仿射（单位方块坐标）。
const ORIENTATION_MATRIX = [
    [1, 0, 0, 1, 0, 0],
    [-1, 0, 0, 1, 1, 0],
    [-1, 0, 0, -1, 1, 1],
    [1, 0, 0, -1, 0, 1],
    [0, 1, 1, 0, 0, 0],
    [0, 1, -1, 0, 1, 0],
    [0, -1, -1, 0, 1, 1],
    [0, -1, 1, 0, 0, 1],
];

// 锐度热力配色（蓝 → 黄 → 红）与 cosine 相似度配色：设计文档未给具体色阶。
const HEAT_STOPS = [[0, 20, 34, 78], [0.5, 255, 210, 0], [1, 255, 44, 26]];
const COSINE_STOPS = [[0, 16, 26, 48], [0.5, 74, 122, 180], [1, 236, 244, 255]];

function clamp(value, min, max) {
    return value < min ? min : value > max ? max : value;
}

function orientationCode(value) {
    const code = Math.floor(Number(value));
    return code >= 1 && code <= 8 ? code : 1;
}

function displayAspect(photo) {
    if (!photo) return 1;
    const width = Math.max(1, Number(photo.width) || 1);
    const height = Math.max(1, Number(photo.height) || 1);
    return orientationCode(photo.orientation) >= 5 ? height / width : width / height;
}

function fitRect(areaW, areaH, aspect) {
    if (!(aspect > 0) || !Number.isFinite(aspect)) return { x: 0, y: 0, w: areaW, h: areaH };
    const w = Math.min(areaW, areaH * aspect);
    const h = w / aspect;
    return { x: (areaW - w) / 2, y: (areaH - h) / 2, w, h };
}

function statusOf(tile) {
    if (tile.state === "content") return "";
    if (tile.message) return tile.message;
    if (tile.state === "pending") return "计算中…";
    if (tile.state === "failed") return "计算失败";
    return "未提取";
}

function maxOf(values) {
    let max = 0;
    for (let i = 0; i < values.length; i++) if (values[i] > max) max = values[i];
    return max;
}

function sampleStops(stops, value, out) {
    const v = clamp(value, 0, 1);
    for (let i = 1; i < stops.length; i++) {
        const upper = stops[i];
        if (v > upper[0] && i < stops.length - 1) continue;
        const lower = stops[i - 1];
        const span = upper[0] - lower[0] || 1;
        const k = clamp((v - lower[0]) / span, 0, 1);
        out[0] = Math.round(lower[1] + (upper[1] - lower[1]) * k);
        out[1] = Math.round(lower[2] + (upper[2] - lower[2]) * k);
        out[2] = Math.round(lower[3] + (upper[3] - lower[3]) * k);
        return out;
    }
    out[0] = stops[stops.length - 1][1];
    out[1] = stops[stops.length - 1][2];
    out[2] = stops[stops.length - 1][3];
    return out;
}

export function mountAnalysis(host, ctx) {
    const { app, t } = ctx;

    host.setAttribute("role", "group");
    host.setAttribute("aria-label", "分析栏");

    const nodes = new Map();
    const refIds = new WeakMap();
    const rgb = [0, 0, 0];
    const gridCanvas = document.createElement("canvas");
    const gridCtx = gridCanvas.getContext("2d");
    const sampleCanvas = document.createElement("canvas");
    let gridImage = null;
    let refSeq = 0;
    let current = null;
    let lastPointerType = "mouse";
    let histSource = null;

    function refId(value) {
        if (value === null || value === undefined) return 0;
        if (typeof value !== "object" && typeof value !== "function") return String(value);
        let id = refIds.get(value);
        if (id === undefined) {
            refSeq += 1;
            id = refSeq;
            refIds.set(value, id);
        }
        return id;
    }

    function ensureGridImage(size) {
        if (!gridImage || gridCanvas.width !== size) {
            gridCanvas.width = size;
            gridCanvas.height = size;
            gridImage = gridCtx.createImageData(size, size);
        }
        return gridImage;
    }

    function prepareCanvas(node, cssW, cssH, dpr) {
        const width = Math.max(1, Math.round(cssW * dpr));
        const height = Math.max(1, Math.round(cssH * dpr));
        if (node.canvas.width !== width) node.canvas.width = width;
        if (node.canvas.height !== height) node.canvas.height = height;
        const context = node.ctx;
        context.setTransform(1, 0, 0, 1, 0, 0);
        context.clearRect(0, 0, width, height);
        context.setTransform(dpr, 0, 0, dpr, 0, 0);
        context.imageSmoothingEnabled = true;
        return context;
    }

    function drawGrid(context, rect, size, values, mode) {
        const image = ensureGridImage(size);
        const data = image.data;
        for (let cell = 0; cell < size * size; cell++) {
            if (mode === "pca") {
                const offset = cell * 3;
                rgb[0] = clamp(values[offset] ?? 0, 0, 1) * 255;
                rgb[1] = clamp(values[offset + 1] ?? 0, 0, 1) * 255;
                rgb[2] = clamp(values[offset + 2] ?? 0, 0, 1) * 255;
            } else if (mode === "sharpness") {
                sampleStops(HEAT_STOPS, values[cell] ?? 0, rgb);
            } else {
                sampleStops(COSINE_STOPS, ((values[cell] ?? 0) + 1) / 2, rgb);
            }
            const p = cell * 4;
            data[p] = rgb[0];
            data[p + 1] = rgb[1];
            data[p + 2] = rgb[2];
            data[p + 3] = 255;
        }
        gridCtx.putImageData(image, 0, 0);
        // ANALYSIS-02：热力图/PCA-RGB 不插值放大，保留逐格可读。
        context.imageSmoothingEnabled = false;
        context.drawImage(gridCanvas, rect.x, rect.y, rect.w, rect.h);
        context.imageSmoothingEnabled = true;
    }

    function drawThumbnail(context, rect, surface, photo, alpha) {
        if (!surface || !photo) return;
        const width = Math.max(1, surface.width);
        const height = Math.max(1, surface.height);
        const code = orientationCode(photo.orientation);
        const displayW = code >= 5 ? height : width;
        const displayH = code >= 5 ? width : height;
        const m = ORIENTATION_MATRIX[code - 1];
        context.save();
        context.globalAlpha = alpha;
        context.translate(rect.x, rect.y);
        context.scale(rect.w / displayW, rect.h / displayH);
        context.transform(m[0], m[1], m[2], m[3], m[4], m[5]);
        context.drawImage(surface, 0, 0);
        context.restore();
    }

    function drawShake(context, rect, features, surface, photo) {
        const size = Math.max(1, Math.round(t("analysis.dino.patchGrid")));
        // ANALYSIS-02：原图缩略图按 analysis.background.alpha 压暗作底，矢量线画在其上。
        drawThumbnail(context, rect, surface, photo, t("analysis.background.alpha"));
        const shake = features.shake;
        if (!shake || !shake.dx || !shake.dy) return;
        const cellW = rect.w / size;
        const cellH = rect.h / size;
        const arm = Math.min(cellW, cellH) * 0.9;
        context.save();
        context.beginPath();
        context.rect(rect.x, rect.y, rect.w, rect.h);
        context.clip();
        context.lineWidth = Math.max(1, Math.min(cellW, cellH) * 0.12);
        context.lineCap = "round";
        for (let gy = 0; gy < size; gy++) {
            for (let gx = 0; gx < size; gx++) {
                const cell = gy * size + gx;
                if (shake.mask && !shake.mask[cell]) continue;
                const dx = shake.dx[cell] ?? 0;
                const dy = shake.dy[cell] ?? 0;
                const length = Math.hypot(dx, dy);
                if (!(length > 0.001)) continue;
                const x = rect.x + (gx + 0.5) * cellW;
                const y = rect.y + (gy + 0.5) * cellH;
                context.strokeStyle = `hsl(${Math.round(120 * (1 - Math.min(1, length)))}, 90%, 55%)`;
                context.beginPath();
                context.moveTo(x, y);
                context.lineTo(x + dx * arm, y + dy * arm);
                context.stroke();
            }
        }
        context.restore();
    }

    function drawCrosshair(context, rect, cross) {
        const arm = t("analysis.crosshair.arm");
        const x = rect.x + cross.u * rect.w;
        const y = rect.y + cross.v * rect.h;
        context.save();
        context.beginPath();
        context.rect(rect.x, rect.y, rect.w, rect.h);
        context.clip();
        context.strokeStyle = colorToCss(t("color.analysis.crosshair"));
        context.lineWidth = 1;
        context.beginPath();
        context.moveTo(x - arm, y);
        context.lineTo(x + arm, y);
        context.moveTo(x, y - arm);
        context.lineTo(x, y + arm);
        context.stroke();
        context.restore();
    }

    function paintDetail(node, tile, state, cssW, cssH, dpr) {
        const detail = tile.kind === "focus" ? state.analysis.focusDetail : state.analysis.centerDetail;
        const photo = state.photo;
        const session = state.session ?? {};
        const surface = session.surface;
        const crop = detail ? detail.crop : null;
        // ANALYSIS-06：只有全分辨率展示图才能按 1:1 裁出源像素，代理期留空框。
        const usable = Boolean(photo && surface && session.tier === "full" && session.photoId === photo.id
            && crop && crop.w > 0 && crop.h > 0);
        const sig = `${usable ? refId(surface) : 0}|${session.tier}|${photo ? photo.id : ""}|${photo ? photo.orientation : ""}|`
            + `${crop ? `${crop.cx},${crop.cy},${crop.w},${crop.h}` : ""}|${cssW}x${cssH}@${dpr}`;
        if (node.sig === sig) return;
        node.sig = sig;
        node.geom = null;
        if (!usable) {
            node.canvas.hidden = true;
            return;
        }
        node.canvas.hidden = false;
        const size = Math.max(1, Math.round(cssW * dpr));
        if (node.canvas.width !== size) node.canvas.width = size;
        if (node.canvas.height !== size) node.canvas.height = size;
        const context = node.ctx;
        context.setTransform(1, 0, 0, 1, 0, 0);
        context.clearRect(0, 0, size, size);

        const sourceW = Math.max(1, surface.width);
        const sourceH = Math.max(1, surface.height);
        const code = orientationCode(photo.orientation);
        const displayW = code >= 5 ? sourceH : sourceW;
        const displayH = code >= 5 ? sourceW : sourceH;
        const stored = rectToStored({
            x: crop.cx / displayW,
            y: crop.cy / displayH,
            w: crop.w / displayW,
            h: crop.h / displayH,
        }, code);
        const side = Math.max(1, Math.min(sourceW, sourceH, Math.round(stored.w * sourceW)));
        const sx = clamp(stored.x * sourceW - side / 2, 0, sourceW - side);
        const sy = clamp(stored.y * sourceH - side / 2, 0, sourceH - side);
        const m = ORIENTATION_MATRIX[code - 1];
        context.imageSmoothingEnabled = true;
        context.setTransform(size * m[0], size * m[1], size * m[2], size * m[3], size * m[4], size * m[5]);
        context.drawImage(surface, sx, sy, side, side, 0, 0, 1, 1);
        context.setTransform(1, 0, 0, 1, 0, 0);
    }

    function paintHistogram(node, state, cssW, cssH, dpr) {
        const hist = state.analysis.histogram;
        const sig = `${refId(hist)}|${cssW}x${cssH}@${dpr}`;
        if (node.sig === sig) return;
        node.sig = sig;
        node.geom = null;
        if (!hist) {
            node.canvas.hidden = true;
            return;
        }
        node.canvas.hidden = false;
        const context = prepareCanvas(node, cssW, cssH, dpr);
        const rgbMax = Math.max(1, maxOf(hist.r), maxOf(hist.g), maxOf(hist.b));
        const lumaMax = Math.max(1, maxOf(hist.luma));
        const bin = cssW / 256;
        context.fillStyle = "rgba(232, 238, 246, 0.26)";
        for (let i = 0; i < 256; i++) {
            const height = (hist.luma[i] / lumaMax) * cssH;
            if (height <= 0) continue;
            context.fillRect(i * bin, cssH - height, bin + 0.5, height);
        }
        // ANALYSIS-07：R/G/B 叠加半透明（相加混合），亮度另画一层。
        context.globalCompositeOperation = "lighter";
        const channels = [[hist.r, "rgba(255, 74, 74, 0.55)"], [hist.g, "rgba(74, 255, 118, 0.5)"], [hist.b, "rgba(84, 132, 255, 0.62)"]];
        for (const [values, style] of channels) {
            context.fillStyle = style;
            for (let i = 0; i < 256; i++) {
                const height = (values[i] / rgbMax) * cssH;
                if (height <= 0) continue;
                context.fillRect(i * bin, cssH - height, bin + 0.5, height);
            }
        }
        context.globalCompositeOperation = "source-over";
    }

    function paintDiagnostic(node, tile, state, cssW, cssH, dpr) {
        const analysis = state.analysis;
        const features = analysis.diagnostics;
        const cross = analysis.crosshair;
        const photo = state.photo;
        const session = state.session ?? {};
        const sig = `${refId(features)}|${refId(analysis.cosine)}|${cross ? `${cross.u},${cross.v}` : ""}|`
            + `${refId(session.surface)}|${photo ? `${photo.id}:${photo.orientation}` : ""}|${cssW}x${cssH}@${dpr}`;
        if (node.sig === sig) return;
        node.sig = sig;
        if (!features) {
            node.canvas.hidden = true;
            node.geom = null;
            return;
        }
        node.canvas.hidden = false;
        // ANALYSIS-02：诊断内容按当前照片显示宽高比居中 letterbox。
        const rect = fitRect(cssW, cssH, displayAspect(photo));
        node.geom = { rect };
        const context = prepareCanvas(node, cssW, cssH, dpr);
        const size = Math.max(1, Math.round(t("analysis.dino.patchGrid")));
        if (tile.kind === "pca") drawGrid(context, rect, size, features.pca, "pca");
        else if (tile.kind === "sharpness") drawGrid(context, rect, size, features.sharpness, "sharpness");
        else if (tile.kind === "cosine") {
            if (analysis.cosine) drawGrid(context, rect, size, analysis.cosine, "cosine");
        } else drawShake(context, rect, features, session.photoId === photo.id ? session.surface : null, photo);
        if (cross) drawCrosshair(context, rect, cross);
    }

    function paintTile(node, tile, state, rect, dpr) {
        const border = t("analysis.tile.border.width");
        const cssW = Math.max(1, rect.w - border * 2);
        const cssH = Math.max(1, rect.h - border * 2);
        const width = `${cssW}px`;
        const height = `${cssH}px`;
        if (node.canvas.style.width !== width) node.canvas.style.width = width;
        if (node.canvas.style.height !== height) node.canvas.style.height = height;
        setText(node.badge, tile.label);
        node.root.title = tile.label;
        const status = statusOf(tile);
        const label = status ? `${tile.label}，${status}` : tile.label;
        if (node.root.getAttribute("aria-label") !== label) node.root.setAttribute("aria-label", label);
        setText(node.placeholder, status);
        node.placeholder.hidden = !status;
        if (tile.state !== "content") {
            node.canvas.hidden = true;
            node.sig = null;
            node.geom = null;
            return;
        }
        if (tile.kind === "histogram") paintHistogram(node, state, cssW, cssH, dpr);
        else if (DIAGNOSTIC_KINDS.has(tile.kind)) paintDiagnostic(node, tile, state, cssW, cssH, dpr);
        else paintDetail(node, tile, state, cssW, cssH, dpr);
    }

    function sampleHistogram(state) {
        if (!state.analysis.visible) {
            histSource = null;
            return;
        }
        const session = state.session ?? {};
        const surface = session.surface;
        const photo = state.photo;
        if (!surface || !photo || session.photoId !== photo.id) {
            histSource = null;
            return;
        }
        if (histSource && histSource.surface === surface && histSource.photoId === photo.id) return;
        histSource = { surface, photoId: photo.id };
        const longEdge = t("analysis.histogram.sampleLongEdge");
        const sourceW = Math.max(1, surface.width);
        const sourceH = Math.max(1, surface.height);
        const scale = Math.min(1, longEdge / Math.max(sourceW, sourceH));
        const width = Math.max(1, Math.round(sourceW * scale));
        const height = Math.max(1, Math.round(sourceH * scale));
        try {
            sampleCanvas.width = width;
            sampleCanvas.height = height;
            const context = sampleCanvas.getContext("2d", { willReadFrequently: true });
            context.clearRect(0, 0, width, height);
            context.drawImage(surface, 0, 0, width, height);
            const pixels = context.getImageData(0, 0, width, height).data;
            app.setHistogramSource(pixels, width, height);
        } catch {
            // 画布不可读时保持占位，不在每次渲染里重复尝试。
        }
    }

    function handleDiagnosticClick(node, event) {
        const geom = node.geom;
        if (!geom) {
            app.clickAnalysisBlank();
            return;
        }
        const bounds = node.canvas.getBoundingClientRect();
        const x = event.clientX - bounds.left;
        const y = event.clientY - bounds.top;
        const badge = node.badge.getBoundingClientRect();
        const onBadge = badge.width > 0 && event.clientX >= badge.left && event.clientX <= badge.right
            && event.clientY >= badge.top && event.clientY <= badge.bottom;
        const rect = geom.rect;
        if (onBadge || x < rect.x || y < rect.y || x > rect.x + rect.w || y > rect.y + rect.h) {
            // ANALYSIS-08：letterbox、角标与内边距按面板空白处理，只清准星。
            app.clickAnalysisBlank();
            return;
        }
        app.clickDiagnostic(clamp((x - rect.x) / rect.w, 0, 1), clamp((y - rect.y) / rect.h, 0, 1));
    }

    function createTile(kind) {
        const canvas = el("canvas", { class: "pv-analysis-canvas" });
        const badge = el("span", { class: "pv-analysis-badge" });
        const placeholder = el("span", { class: "pv-analysis-placeholder" });
        const interactive = kind !== "histogram";
        const root = interactive
            ? el("button", { class: "pv-analysis-tile", type: "button", dataset: { kind } }, canvas, badge, placeholder)
            : el("div", { class: "pv-analysis-tile", role: "img", dataset: { kind } }, canvas, badge, placeholder);
        const node = { kind, root, canvas, badge, placeholder, ctx: canvas.getContext("2d"), sig: null, geom: null, pointerType: "mouse" };
        if (interactive) {
            const detail = DETAIL_KINDS.has(kind);
            root.addEventListener("pointerdown", (event) => {
                node.pointerType = event.pointerType || "mouse";
            });
            root.addEventListener("click", (event) => {
                if (!current) return;
                event.stopPropagation();
                if (detail) {
                    // ANALYSIS-11：触屏点按作为悬停等价入口，再点一次解除定格。
                    if (node.pointerType !== "mouse" && event.detail !== 0) {
                        app.hoverDetail(current.analysis.hoverKind === kind ? null : kind);
                    }
                    return;
                }
                handleDiagnosticClick(node, event);
            });
            root.addEventListener("keydown", (event) => {
                if (event.key !== "Enter" && event.key !== " ") return;
                event.preventDefault();
                event.stopPropagation();
                if (detail) app.locateDetail(kind);
                else app.clickDiagnostic(0.5, 0.5);
            });
            if (detail) {
                let hovered = false;
                root.addEventListener("pointerenter", (event) => {
                    if (event.pointerType !== "mouse") return;
                    hovered = true;
                    app.hoverDetail(kind);
                });
                root.addEventListener("pointerleave", (event) => {
                    if (event.pointerType !== "mouse") return;
                    hovered = false;
                    if (document.activeElement !== root) app.hoverDetail(null);
                });
                root.addEventListener("focus", () => app.hoverDetail(kind));
                root.addEventListener("blur", () => {
                    if (!hovered) app.hoverDetail(null);
                });
                root.addEventListener("dblclick", (event) => {
                    event.preventDefault();
                    app.locateDetail(kind);
                });
            }
        }
        return node;
    }

    function ensureTile(kind) {
        let node = nodes.get(kind);
        if (!node) {
            node = createTile(kind);
            nodes.set(kind, node);
        }
        return node;
    }

    function render(state) {
        current = state;
        const layout = state.layout ? state.layout.analysis : null;
        const tiles = state.analysis && state.analysis.tiles ? state.analysis.tiles : [];
        if (!state.panes.analysis || !layout || tiles.length === 0) {
            histSource = null;
            for (const node of nodes.values()) node.root.hidden = true;
            return;
        }
        sampleHistogram(state);
        const kinds = new Set(tiles.map((tile) => tile.kind));
        for (const [kind, node] of nodes) {
            if (kinds.has(kind)) continue;
            node.root.remove();
            nodes.delete(kind);
        }
        const dpr = state.viewport && state.viewport.dpr > 0 ? state.viewport.dpr : 1;
        tiles.forEach((tile, index) => {
            const node = ensureTile(tile.kind);
            const rect = layout.tiles[index];
            if (!rect) {
                node.root.hidden = true;
                return;
            }
            place(node.root, rect);
            if (host.children[index] !== node.root) host.insertBefore(node.root, host.children[index] ?? null);
            paintTile(node, tile, state, rect, dpr);
        });
    }

    function onWheel(event) {
        // ANALYSIS-11：面板不是滚动区，滚轮不翻页也不缩放主图。
        event.preventDefault();
        event.stopPropagation();
    }

    function onPointerDown(event) {
        lastPointerType = event.pointerType || "mouse";
    }

    function onHostClick(event) {
        const tile = event.target.closest ? event.target.closest(".pv-analysis-tile") : null;
        if (tile) return;
        app.clickAnalysisBlank();
        // ANALYSIS-04：触屏在主图/面板空白点按解除细节预览的定格。
        if (lastPointerType !== "mouse") app.hoverDetail(null);
    }

    host.addEventListener("wheel", onWheel, { passive: false });
    host.addEventListener("pointerdown", onPointerDown);
    host.addEventListener("click", onHostClick);

    return {
        update(state, regions) {
            current = state;
            if (!touches(regions, "layout", "analysis", "image", "current", "panes", "window", "viewport")) return;
            render(state);
        },
        destroy() {
            host.removeEventListener("wheel", onWheel);
            host.removeEventListener("pointerdown", onPointerDown);
            host.removeEventListener("click", onHostClick);
            for (const node of nodes.values()) node.root.remove();
            nodes.clear();
        },
    };
}
