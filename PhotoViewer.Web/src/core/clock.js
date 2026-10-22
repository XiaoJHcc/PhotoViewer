export function systemClock() {
    return {
        now: () => performance.now(),
        setTimeout: (fn, ms) => globalThis.setTimeout(fn, ms),
        clearTimeout: (id) => globalThis.clearTimeout(id),
        requestFrame: (fn) => (globalThis.requestAnimationFrame
            ? globalThis.requestAnimationFrame(() => fn(performance.now()))
            : globalThis.setTimeout(() => fn(performance.now()), 16)),
        cancelFrame: (id) => (globalThis.cancelAnimationFrame
            ? globalThis.cancelAnimationFrame(id)
            : globalThis.clearTimeout(id)),
    };
}

/** 单测用假时钟：advance(ms) 按帧步推进，到期定时器按到期顺序执行，每步末尾回调一次帧。 */
export function manualClock(start = 0) {
    let time = start;
    let nextId = 1;
    const timers = new Map();
    const frames = new Map();

    function runDue() {
        for (;;) {
            let first = null;
            for (const entry of timers) {
                if (entry[1].at <= time && (!first || entry[1].at < first[1].at)) first = entry;
            }
            if (!first) return;
            timers.delete(first[0]);
            first[1].fn();
        }
    }

    return {
        now: () => time,
        setTimeout(fn, ms) {
            const id = nextId++;
            timers.set(id, { at: time + Math.max(0, ms), fn });
            return id;
        },
        clearTimeout(id) {
            timers.delete(id);
        },
        requestFrame(fn) {
            const id = nextId++;
            frames.set(id, fn);
            return id;
        },
        cancelFrame(id) {
            frames.delete(id);
        },
        advance(ms, frameStep = 16) {
            const end = time + ms;
            runDue();
            while (time < end) {
                time = Math.min(end, time + frameStep);
                runDue();
                const pending = [...frames.values()];
                frames.clear();
                for (const fn of pending) fn(time);
            }
        },
        pendingTimers: () => timers.size,
        pendingFrames: () => frames.size,
    };
}
