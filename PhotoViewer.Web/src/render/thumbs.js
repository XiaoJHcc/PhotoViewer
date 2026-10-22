/**
 * 胶片栏缩略图队列：按需解码代理图并转成显示朝向，LRU 缓存、可取消、并发 ≤ 4。
 *
 * createThumbnailQueue({ t?, dpr?, size?, decoder, clock?, orient?, cacheLimit?, concurrency? }) → {
 *     request(photo, priority?, { signal }?) -> Promise<surface>   长边 = film.thumb.size × dpr
 *     cancel(photoOrId) / cancelAll()
 *     stats() -> { pending, running, cached, jobs }
 * }
 *
 * priority 越小越先执行：true / "visible" = 0，false / "near" = 1，"far" = 2，数字原样使用。
 * 可见项在下一个空出的并发位先于远处项开始（FILM-02：动画经过的可见卡片照常加载缩略图）。
 * 解码失败直接 reject，不返回占位图（FILM-05）。
 */
import { systemClock } from "../core/clock.js";
import { displayDimensions, orientationMatrix } from "./procedural.js";
import { createCanvas } from "./decoder.js";

function toNumber(value, fallback) {
    const number = Number(value);
    return Number.isFinite(number) ? number : fallback;
}

function abortError() {
    const error = new Error("已取消");
    error.name = "AbortError";
    return error;
}

function transferCanvas(canvas) {
    if (typeof canvas.transferToImageBitmap === "function") return canvas.transferToImageBitmap();
    return canvas;
}

function browserOrient(surface, orientation, width, height) {
    const code = Number(orientation) >= 1 && Number(orientation) <= 8 ? Number(orientation) : 1;
    if (code === 1) return surface;
    const dims = displayDimensions(width, height, code);
    const canvas = createCanvas(dims.width, dims.height);
    const ctx = canvas.getContext("2d");
    if (!ctx) throw new Error("无法取得 Canvas2D 上下文");
    const matrix = orientationMatrix(code, width, height);
    ctx.setTransform(matrix.a, matrix.b, matrix.c, matrix.d, matrix.e, matrix.f);
    ctx.drawImage(surface, 0, 0, width, height);
    return transferCanvas(canvas);
}

function normalizePriority(priority) {
    if (priority === true || priority === "visible") return 0;
    if (priority === false || priority === null || priority === undefined) return 1;
    if (priority === "far") return 2;
    if (priority === "near") return 1;
    const value = Number(priority);
    return Number.isFinite(value) ? value : 1;
}

function resolveSize({ t, dpr, size }) {
    if (size !== undefined && size !== null) {
        const value = Math.round(toNumber(size, 0));
        if (value > 0) return value;
    }
    if (typeof t === "function") {
        const factor = Math.max(0.1, toNumber(dpr, 1));
        return Math.max(1, Math.round(t("film.thumb.size") * factor));
    }
    throw new Error("缩略图队列需要 size 或令牌函数 t");
}

export function createThumbnailQueue(options = {}) {
    const clock = options.clock ?? systemClock();
    const decoder = options.decoder;
    if (!decoder || typeof decoder.decode !== "function") throw new Error("缩略图队列需要 decoder");
    const orient = typeof options.orient === "function" ? options.orient : browserOrient;
    const size = resolveSize(options);
    const cacheLimit = Math.max(1, Math.floor(toNumber(options.cacheLimit, 200)));
    const concurrency = Math.max(1, Math.floor(toNumber(options.concurrency, 4)));

    const cache = new Map();
    const jobs = new Map();
    const pending = [];
    let running = 0;
    let sequence = 0;

    function touch(id) {
        const surface = cache.get(id);
        if (surface === undefined) return null;
        cache.delete(id);
        cache.set(id, surface);
        return surface;
    }

    function trimCache() {
        while (cache.size > cacheLimit) {
            const oldest = cache.keys().next().value;
            cache.delete(oldest);
        }
    }

    function settleWaiter(job, waiter, error, surface) {
        if (waiter.done) return;
        waiter.done = true;
        if (waiter.onAbort) waiter.signal.removeEventListener("abort", waiter.onAbort);
        if (error) waiter.reject(error);
        else waiter.resolve(surface);
    }

    function finish(job, error, surface) {
        running = Math.max(0, running - 1);
        job.state = "done";
        if (jobs.get(job.photo.id) === job) jobs.delete(job.photo.id);
        if (!error && surface) {
            cache.set(job.photo.id, surface);
            trimCache();
        }
        const waiters = job.waiters;
        job.waiters = [];
        for (const waiter of waiters) settleWaiter(job, waiter, error, surface);
        pump();
    }

    function start(job) {
        job.state = "running";
        running += 1;
        let request;
        try {
            request = decoder.decode(job.photo, "proxy", { longEdge: size, signal: job.controller.signal });
        } catch (error) {
            request = Promise.reject(error);
        }
        Promise.resolve(request)
            .then((result) => {
                if (!result || !result.surface) throw new Error("缩略图解码失败");
                return orient(result.surface, job.photo.orientation ?? 1, result.width, result.height);
            })
            .then(
                (surface) => finish(job, null, surface),
                (error) => finish(job, error),
            );
    }

    function pump() {
        pending.sort((a, b) => (a.priority - b.priority) || (a.sequence - b.sequence));
        while (running < concurrency && pending.length > 0) {
            start(pending.shift());
        }
    }

    function removePending(job) {
        const index = pending.indexOf(job);
        if (index >= 0) pending.splice(index, 1);
    }

    function dropJob(job) {
        jobs.delete(job.photo.id);
        if (job.state === "pending") removePending(job);
        else job.controller.abort();
        const error = abortError();
        const waiters = job.waiters;
        job.waiters = [];
        for (const waiter of waiters) settleWaiter(job, waiter, error, null);
        return true;
    }

    function abortWaiter(job, waiter) {
        if (waiter.done) return;
        waiter.done = true;
        const index = job.waiters.indexOf(waiter);
        if (index >= 0) job.waiters.splice(index, 1);
        waiter.reject(abortError());
        if (job.waiters.length === 0) dropJob(job);
    }

    function request(photo, priority = false, requestOptions = {}) {
        if (!photo || !photo.id) return Promise.reject(new Error("缺少照片"));
        const cached = touch(photo.id);
        if (cached !== null) return Promise.resolve(cached);

        const signal = requestOptions.signal;
        if (signal && signal.aborted) return Promise.reject(abortError());

        const level = normalizePriority(priority);
        let job = jobs.get(photo.id);
        if (!job) {
            job = {
                photo,
                priority: level,
                sequence: sequence++,
                waiters: [],
                controller: new AbortController(),
                state: "pending",
            };
            jobs.set(photo.id, job);
            pending.push(job);
        } else {
            job.priority = Math.min(job.priority, level);
        }

        return new Promise((resolve, reject) => {
            const waiter = { resolve, reject, signal, onAbort: null, done: false };
            if (signal) {
                waiter.onAbort = () => abortWaiter(job, waiter);
                signal.addEventListener("abort", waiter.onAbort, { once: true });
            }
            job.waiters.push(waiter);
            pump();
        });
    }

    function cancel(target) {
        const id = typeof target === "string" ? target : target ? target.id : null;
        const job = id ? jobs.get(id) : null;
        if (!job) return false;
        return dropJob(job);
    }

    function cancelAll() {
        for (const job of [...jobs.values()]) dropJob(job);
    }

    function stats() {
        return { pending: pending.length, running, cached: cache.size, jobs: jobs.size, size };
    }

    return { request, cancel, cancelAll, stats };
}
