/**
 * 程序化解码器：实现 session.js 的 decoder 接口，用 Canvas 绘制存储朝向的位图。
 *
 * createDecoder({ clock?, latency?, createSurface?, enhanceSurface? }) → {
 *     decode(photo, "proxy" | "full", { longEdge, signal })
 *         -> Promise<{ tier, width, height, surface }>    surface 为 ImageBitmap，存储朝向
 *     enhance(surface, { signal }) -> Promise<surface>    视觉差异明显的增强版本
 * }
 *
 * latency 可传数字（三档统一）或 { proxy: [min,max], full: [min,max], enhance: [min,max] }；
 * 代理默认 40–120ms，Full 默认 180–420ms，增强默认 300–800ms。延迟按 photo.id 确定性抽取。
 * 未给 longEdge 时代理长边取 2048，避免误把整幅源图当代理。Full 恒为源尺寸（存储朝向）。
 * setLatencyMultiplier(n) 运行时按倍数缩放三档延迟（演示面板观察 300 ms 加载提示与 500 ms 药丸门槛用）。
 * createSurface/enhanceSurface 用于单测注入，默认实现需要 OffscreenCanvas 或 HTMLCanvasElement。
 */
import { systemClock } from "../core/clock.js";
import { mulberry32 } from "../core/features.js";
import { drawTile } from "./procedural.js";

const DEFAULT_LATENCY = {
    proxy: [40, 120],
    full: [180, 420],
    enhance: [300, 800],
};

const DEFAULT_PROXY_LONG_EDGE = 2048;

function toNumber(value, fallback) {
    const number = Number(value);
    return Number.isFinite(number) ? number : fallback;
}

function normalizeLatency(latency) {
    const ranges = {
        proxy: [...DEFAULT_LATENCY.proxy],
        full: [...DEFAULT_LATENCY.full],
        enhance: [...DEFAULT_LATENCY.enhance],
    };
    const apply = (key, value) => {
        if (typeof value === "number" && Number.isFinite(value)) {
            ranges[key] = [Math.max(0, value), Math.max(0, value)];
            return;
        }
        if (Array.isArray(value) && value.length >= 2) {
            const min = Math.max(0, toNumber(value[0], 0));
            const max = Math.max(min, toNumber(value[1], min));
            ranges[key] = [min, max];
        }
    };
    if (typeof latency === "number" && Number.isFinite(latency)) {
        for (const key of Object.keys(ranges)) apply(key, latency);
    } else if (latency && typeof latency === "object") {
        for (const key of Object.keys(ranges)) {
            if (key in latency) apply(key, latency[key]);
        }
    }
    return ranges;
}

function hashString(text) {
    let hash = 2166136261;
    for (let i = 0; i < text.length; i++) {
        hash ^= text.charCodeAt(i);
        hash = Math.imul(hash, 16777619);
    }
    return hash >>> 0;
}

function pickLatency(range, seed) {
    const [min, max] = range;
    if (max <= min) return min;
    return Math.round(min + (max - min) * mulberry32(hashString(seed))());
}

function abortError() {
    const error = new Error("已取消");
    error.name = "AbortError";
    return error;
}

function wait(clock, ms, signal) {
    return new Promise((resolve, reject) => {
        if (signal && signal.aborted) {
            reject(abortError());
            return;
        }
        let settled = false;
        const timer = clock.setTimeout(() => {
            if (settled) return;
            settled = true;
            cleanup();
            resolve();
        }, Math.max(0, ms));
        const onAbort = () => {
            if (settled) return;
            settled = true;
            clock.clearTimeout(timer);
            cleanup();
            reject(abortError());
        };
        const cleanup = () => {
            if (signal) signal.removeEventListener("abort", onAbort);
        };
        if (signal) signal.addEventListener("abort", onAbort, { once: true });
    });
}

function sourceWidth(photo) {
    return Math.max(1, Math.round(toNumber(photo ? photo.width : 1, 1)));
}

function sourceHeight(photo) {
    return Math.max(1, Math.round(toNumber(photo ? photo.height : 1, 1)));
}

export function proxyDimensions(photo, longEdge) {
    const width = sourceWidth(photo);
    const height = sourceHeight(photo);
    const sourceEdge = Math.max(width, height);
    const requested = toNumber(longEdge, 0);
    const edge = requested > 0 ? Math.min(Math.round(requested), sourceEdge) : sourceEdge;
    if (width >= height) {
        return { width: edge, height: Math.max(1, Math.round((edge * height) / width)) };
    }
    return { width: Math.max(1, Math.round((edge * width) / height)), height: edge };
}

export function createCanvas(width, height) {
    const Offscreen = globalThis.OffscreenCanvas;
    if (typeof Offscreen === "function") return new Offscreen(width, height);
    const documentRef = globalThis.document;
    if (documentRef && typeof documentRef.createElement === "function") {
        const canvas = documentRef.createElement("canvas");
        canvas.width = width;
        canvas.height = height;
        return canvas;
    }
    throw new Error("当前环境不支持 Canvas 绘制");
}

function transferCanvas(canvas) {
    if (typeof canvas.transferToImageBitmap === "function") return canvas.transferToImageBitmap();
    return canvas;
}

function browserSurface({ width, height, paint }) {
    const canvas = createCanvas(width, height);
    const ctx = canvas.getContext("2d");
    if (!ctx) throw new Error("无法取得 Canvas2D 上下文");
    paint(ctx);
    return transferCanvas(canvas);
}

function enhancePixels(data) {
    const lut = new Uint8ClampedArray(256);
    for (let i = 0; i < 256; i++) {
        const value = i / 255;
        const sCurve = value * value * (3 - 2 * value);
        lut[i] = Math.round(Math.min(1, Math.max(0, value + (sCurve - value) * 0.55)) * 255);
    }
    for (let offset = 0; offset < data.length; offset += 4) {
        const r = lut[data[offset]];
        const g = lut[data[offset + 1]];
        const b = lut[data[offset + 2]];
        const luma = 0.2126 * r + 0.7152 * g + 0.0722 * b;
        data[offset] = Math.round(Math.min(255, luma + (r - luma) * 1.35));
        data[offset + 1] = Math.round(Math.min(255, luma + (g - luma) * 1.35));
        data[offset + 2] = Math.round(Math.min(255, luma + (b - luma) * 1.35));
    }
}

function browserEnhance(surface, { width, height }) {
    const w = Math.max(1, Math.round(toNumber(width, toNumber(surface.width, 1))));
    const h = Math.max(1, Math.round(toNumber(height, toNumber(surface.height, 1))));
    const canvas = createCanvas(w, h);
    const ctx = canvas.getContext("2d");
    if (!ctx) throw new Error("无法取得 Canvas2D 上下文");
    const filterSupported = "filter" in ctx;
    if (filterSupported) ctx.filter = "contrast(1.16) saturate(1.45) brightness(1.03)";
    ctx.drawImage(surface, 0, 0, w, h);
    if (!filterSupported) {
        const frame = ctx.getImageData(0, 0, w, h);
        enhancePixels(frame.data);
        ctx.putImageData(frame, 0, 0);
    }
    return transferCanvas(canvas);
}

function decodeFailure(photo, tier) {
    const error = new Error(tier === "full" ? "无法载入全分辨率" : "无法解码该图片");
    error.code = "decode-failed";
    error.tier = tier;
    error.photoId = photo ? photo.id : null;
    return error;
}

export function createDecoder(options = {}) {
    const clock = options.clock ?? systemClock();
    const baseLatency = normalizeLatency(options.latency);
    let multiplier = 1;
    const scaled = (range) => [range[0] * multiplier, range[1] * multiplier];
    const latency = {
        get proxy() { return scaled(baseLatency.proxy); },
        get full() { return scaled(baseLatency.full); },
        get enhance() { return scaled(baseLatency.enhance); },
    };
    const createSurface = typeof options.createSurface === "function" ? options.createSurface : browserSurface;
    const enhanceSurface = typeof options.enhanceSurface === "function" ? options.enhanceSurface : browserEnhance;
    let enhanceSeq = 0;

    function decode(photo, tier, request = {}) {
        const kind = tier === "proxy" || tier === "full" ? tier : null;
        if (!photo) return Promise.reject(new Error("缺少照片"));
        if (!kind) return Promise.reject(new Error(`未知画质档：${tier}`));
        const fault = photo.fault ? photo.fault.decode : null;
        if (fault === "all" || (fault === "full" && kind === "full")) {
            return Promise.reject(decodeFailure(photo, kind));
        }
        const signal = request.signal;
        if (signal && signal.aborted) return Promise.reject(abortError());

        const requestedEdge = toNumber(request.longEdge, 0);
        const dims = kind === "full"
            ? { width: sourceWidth(photo), height: sourceHeight(photo) }
            : proxyDimensions(photo, requestedEdge > 0 ? requestedEdge : DEFAULT_PROXY_LONG_EDGE);
        const delay = pickLatency(latency[kind], `${photo.id}:${kind}`);

        return wait(clock, delay, signal).then(() => {
            if (signal && signal.aborted) throw abortError();
            const surface = createSurface({
                width: dims.width,
                height: dims.height,
                tier: kind,
                photo,
                paint(ctx) {
                    drawTile(ctx, photo, 0, 0, sourceWidth(photo), sourceHeight(photo), dims.width, dims.height);
                },
            });
            return { tier: kind, width: dims.width, height: dims.height, surface };
        });
    }

    function enhance(surface, request = {}) {
        if (!surface) return Promise.reject(new Error("缺少展示图"));
        const signal = request.signal;
        if (signal && signal.aborted) return Promise.reject(abortError());
        const width = toNumber(surface.width, 0);
        const height = toNumber(surface.height, 0);
        const delay = pickLatency(latency.enhance, `enhance:${++enhanceSeq}`);
        return wait(clock, delay, signal).then(() => {
            if (signal && signal.aborted) throw abortError();
            return enhanceSurface(surface, { width, height });
        });
    }

    return {
        decode,
        enhance,
        get latencyMultiplier() {
            return multiplier;
        },
        setLatencyMultiplier(value) {
            const number = Number(value);
            multiplier = Number.isFinite(number) && number >= 0 ? number : 1;
        },
    };
}
