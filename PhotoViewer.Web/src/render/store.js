/**
 * 合成特征库：实现 analysis.js / similarity.js 的 store 接口。
 *
 * createStore({ clock?, readLatency?, computeLatency?, extractLatency? }) → {
 *     read(photo, { signal }) -> Promise<Features|null>       只读库，未提取或未入指纹 → null
 *     compute(photo, { signal, variant }) -> Promise<Features> 现算；variant "enhanced" 小幅提升锐度
 *     extractGroup(photos) -> Promise<void>                   每组约 300ms，成功后组内 extracted = true
 *     failNext = n                                            接下来 n 次提取失败（演示 SIM-10）
 *     clear()                                                 清进程内现算缓存
 * }
 *
 * read 默认 20–60ms，compute 默认 600–1200ms，extract 默认每组 300ms；
 * 延迟可传数字或 [min,max]。compute 只进进程缓存，不改 photo.extracted（SCOPE-03）。
 */
import { systemClock } from "../core/clock.js";
import { featuresFor, mulberry32 } from "../core/features.js";

function toNumber(value, fallback) {
    const number = Number(value);
    return Number.isFinite(number) ? number : fallback;
}

function normalizeRange(value, fallback) {
    if (typeof value === "number" && Number.isFinite(value)) {
        return [Math.max(0, value), Math.max(0, value)];
    }
    if (Array.isArray(value) && value.length >= 2) {
        const min = Math.max(0, toNumber(value[0], fallback[0]));
        const max = Math.max(min, toNumber(value[1], min));
        return [min, max];
    }
    return [...fallback];
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

function withSignal(promise, signal) {
    if (!signal) return promise;
    if (signal.aborted) return Promise.reject(abortError());
    return new Promise((resolve, reject) => {
        let settled = false;
        const onAbort = () => {
            if (settled) return;
            settled = true;
            cleanup();
            reject(abortError());
        };
        const cleanup = () => signal.removeEventListener("abort", onAbort);
        signal.addEventListener("abort", onAbort, { once: true });
        promise.then(
            (value) => {
                if (settled) return;
                settled = true;
                cleanup();
                resolve(value);
            },
            (error) => {
                if (settled) return;
                settled = true;
                cleanup();
                reject(error);
            },
        );
    });
}

function photoKey(photo) {
    if (photo.fingerprint) return `fp:${photo.fingerprint}`;
    const scene = photo.scene ?? {};
    return `scene:${scene.base ?? 0}:${scene.variant ?? 0}`;
}

function enhancedFeatures(photo) {
    const base = featuresFor(photo);
    const sharpness = new Float32Array(base.sharpness.length);
    for (let i = 0; i < base.sharpness.length; i++) {
        sharpness[i] = Math.min(1, base.sharpness[i] * 1.22 + 0.02);
    }
    return { ...base, sharpness, enhanced: true };
}

export function createStore(options = {}) {
    const clock = options.clock ?? systemClock();
    const readLatency = normalizeRange(options.readLatency, [20, 60]);
    const computeLatency = normalizeRange(options.computeLatency, [600, 1200]);
    const extractLatency = normalizeRange(options.extractLatency, [300, 300]);
    const computing = new Map();
    const computed = new Map();

    function acquire(entry, signal) {
        entry.waiters += 1;
        let released = false;
        const release = () => {
            if (released) return;
            released = true;
            entry.waiters -= 1;
            // SCOPE-02：所有等待者都已取消时停在途计算，不留后台推理。
            if (entry.waiters === 0 && !entry.done) entry.controller.abort();
        };
        return withSignal(entry.promise, signal).then(
            (value) => {
                release();
                return value;
            },
            (error) => {
                release();
                throw error;
            },
        );
    }

    const api = {
        failNext: 0,

        read(photo, request = {}) {
            const delay = pickLatency(readLatency, `read:${photo ? photo.id : ""}`);
            return wait(clock, delay, request.signal).then(() => {
                if (!photo || photo.extracted !== true || !photo.fingerprint) return null;
                return featuresFor(photo);
            });
        },

        compute(photo, request = {}) {
            if (!photo) return Promise.reject(new Error("缺少照片"));
            const variant = request.variant === "enhanced" ? "enhanced" : "original";
            const key = `${photoKey(photo)}:${variant}`;
            const cached = computed.get(key);
            if (cached) return withSignal(Promise.resolve(cached), request.signal);

            let entry = computing.get(key);
            if (!entry) {
                const delay = pickLatency(computeLatency, `compute:${key}`);
                const controller = new AbortController();
                entry = { controller, waiters: 0, done: false, promise: null };
                entry.promise = wait(clock, delay, controller.signal).then(
                    () => {
                        const features = variant === "enhanced" ? enhancedFeatures(photo) : featuresFor(photo);
                        entry.done = true;
                        computed.set(key, features);
                        computing.delete(key);
                        return features;
                    },
                    (error) => {
                        entry.done = true;
                        computing.delete(key);
                        throw error;
                    },
                );
                computing.set(key, entry);
            }
            return acquire(entry, request.signal);
        },

        extractGroup(photos) {
            const list = Array.isArray(photos) ? photos.filter(Boolean) : [];
            const delay = pickLatency(extractLatency, `extract:${list.map((photo) => photo.id).join(",")}`);
            return wait(clock, delay).then(() => {
                if (api.failNext > 0) {
                    api.failNext -= 1;
                    throw new Error("特征提取失败");
                }
                for (const photo of list) photo.extracted = true;
            });
        },

        clear() {
            for (const entry of computing.values()) entry.controller.abort();
            computing.clear();
            computed.clear();
        },

        stats() {
            return { computed: computed.size, computing: computing.size };
        },
    };

    return api;
}
