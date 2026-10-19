import { test } from "node:test";
import assert from "node:assert/strict";
import { createTokens } from "../src/core/tokens.js";
import { manualClock } from "../src/core/clock.js";
import { AnalysisPresenter, isValidFocus } from "../src/core/analysis.js";
import { featuresFor } from "../src/core/features.js";
import { GRID } from "../src/core/types.js";

const t = createTokens();

const flush = () => new Promise((resolve) => setImmediate(resolve));

function photo(id, overrides = {}) {
    return {
        id,
        folderId: "f",
        name: `${id}.jpg`,
        stem: id,
        ext: ".jpg",
        size: 1,
        modified: 0,
        taken: 0,
        tzOffset: 0,
        rating: 0,
        width: 6000,
        height: 4000,
        orientation: 1,
        focus: null,
        exif: {},
        shake: false,
        fingerprint: `fp-${id}`,
        extracted: true,
        scene: { base: 1, variant: 0 },
        ...overrides,
    };
}

function abortError() {
    const error = new Error("aborted");
    error.name = "AbortError";
    return error;
}

function delayed(clock, ms, value, signal, ignoreAbort) {
    if (signal?.aborted && !ignoreAbort) return Promise.reject(abortError());
    if (!ms) return Promise.resolve(value);
    return new Promise((resolve, reject) => {
        const timer = clock.setTimeout(() => resolve(value), ms);
        if (ignoreAbort) return;
        signal?.addEventListener("abort", () => {
            clock.clearTimeout(timer);
            reject(abortError());
        }, { once: true });
    });
}

function makeStore(clock, options = {}) {
    const {
        features = new Map(),
        readDelay = 0,
        computeDelay = 0,
        computeFeatures = (target) => featuresFor(target),
        failRead = false,
        ignoreAbort = false,
    } = options;
    const calls = { read: [], compute: [] };
    const store = {
        calls,
        read(target, { signal } = {}) {
            calls.read.push({ photo: target, signal });
            if (failRead) return Promise.reject(new Error("read failed"));
            const wait = typeof readDelay === "function" ? readDelay(target) : readDelay;
            return delayed(clock, wait, features.get(target.id) ?? null, signal, ignoreAbort);
        },
        compute(target, { signal, variant } = {}) {
            calls.compute.push({ photo: target, variant, signal });
            return delayed(clock, computeDelay, computeFeatures(target, variant), signal, ignoreAbort);
        },
        extractGroup: async () => {},
    };
    return store;
}

function tile(presenter, kind) {
    return presenter.tiles().find((entry) => entry.kind === kind);
}

test("ANALYSIS-01：瓦片顺序固定，对焦点瓦片只在该照片有对焦数据时出现", () => {
    const clock = manualClock();
    const presenter = new AnalysisPresenter({ t, clock, store: makeStore(clock) });

    presenter.setPhoto(photo("a"));
    assert.deepEqual(
        presenter.tiles().map((entry) => entry.kind),
        ["center", "histogram", "pca", "cosine", "sharpness", "shake"],
    );
    assert.deepEqual(
        presenter.tiles().map((entry) => entry.label),
        ["中心", "直方图", "DINO PCA", "Cosine 16,16", "锐度", "抖动拖影"],
    );

    presenter.setPhoto(photo("b", { focus: { x: 0.4, y: 0.5, w: 0.1, h: 0.08 } }));
    assert.deepEqual(
        presenter.tiles().map((entry) => entry.kind),
        ["focus", "center", "histogram", "pca", "cosine", "sharpness", "shake"],
    );
    assert.equal(tile(presenter, "focus").label, "对焦点");

    presenter.setPhoto(photo("c", { focus: { x: 0.4, y: 0.5, w: 0, h: 0.08 } }));
    assert.equal(presenter.tiles().some((entry) => entry.kind === "focus"), false);
    assert.equal(isValidFocus({ x: 0.4, y: 0.5, w: 0, h: 0.08 }), false);
});

test("ANALYSIS-01：隐藏即取消在途任务并清空诊断与准星", () => {
    const clock = manualClock();
    const store = makeStore(clock, { readDelay: 100 });
    const presenter = new AnalysisPresenter({ t, clock, store });

    presenter.setVisible(true);
    presenter.setDisplaySurfaceReady(true);
    presenter.setPhoto(photo("a"));
    assert.equal(store.calls.read.length, 1);

    presenter.clickDiagnostic(0.25, 0.75);
    assert.deepEqual(presenter.crosshair, { u: 0.25, v: 0.75 });

    presenter.setVisible(false);
    assert.equal(presenter.crosshair, null);
    assert.equal(store.calls.read[0].signal.aborted, true);
    assert.equal(clock.pendingTimers(), 0);
    assert.equal(tile(presenter, "pca").state, "empty");
    assert.equal(tile(presenter, "pca").message, "未提取");

    const reads = store.calls.read.length;
    presenter.setPhoto(photo("b"));
    assert.equal(store.calls.read.length, reads);
});

test("ANALYSIS-08：准星落准、cosine 参考点按 patch 网格、重复点同一格不重算", async () => {
    const clock = manualClock();
    const target = photo("a");
    const store = makeStore(clock, { features: new Map([[target.id, featuresFor(target)]]) });
    const presenter = new AnalysisPresenter({ t, clock, store });
    presenter.setDisplaySurfaceReady(true);
    presenter.setVisible(true);
    presenter.setPhoto(target);
    await flush();

    assert.equal(tile(presenter, "pca").state, "content");
    assert.equal(tile(presenter, "cosine").label, "Cosine 16,16");

    presenter.clickDiagnostic(0.3, 0.7);
    assert.deepEqual(presenter.crosshair, { u: 0.3, v: 0.7 });
    assert.equal(tile(presenter, "cosine").label, "Cosine 9,22");

    const map = presenter.cosineMap();
    assert.ok(map instanceof Float32Array);
    assert.equal(map.length, GRID * GRID);
    assert.ok(Math.abs(map[22 * GRID + 9] - 1) < 1e-5);

    presenter.clickDiagnostic(0.31, 0.71);
    assert.equal(presenter.cosineMap(), map);

    presenter.clickBlank();
    assert.equal(presenter.crosshair, null);
    assert.equal(tile(presenter, "cosine").label, "Cosine 9,22");

    presenter.clickDiagnostic(0.05, 0.95);
    presenter.setPhoto(photo("b"));
    assert.deepEqual(presenter.crosshair, { u: 0.05, v: 0.95 });
    assert.equal(tile(presenter, "cosine").label, "Cosine 16,16");
});

test("ANALYSIS-08：点按空白清除准星但保留 cosine 参考点；无 patch 数据只动准星", async () => {
    const clock = manualClock();
    const target = photo("a");
    const bare = featuresFor(target);
    const noPatches = { ...bare, patches: null };
    const store = makeStore(clock, { features: new Map([[target.id, noPatches]]) });
    const presenter = new AnalysisPresenter({ t, clock, store });
    presenter.setDisplaySurfaceReady(true);
    presenter.setVisible(true);
    presenter.setPhoto(target);
    await flush();

    presenter.clickDiagnostic(0.3, 0.7);
    assert.deepEqual(presenter.crosshair, { u: 0.3, v: 0.7 });
    assert.equal(tile(presenter, "cosine").label, "Cosine 16,16");
    assert.equal(presenter.cosineMap(), null);

    presenter.clickDiagnostic(1.4, 0.2);
    assert.equal(presenter.crosshair, null);
    assert.equal(tile(presenter, "cosine").label, "Cosine 16,16");

    presenter.clickDiagnostic(0.2, 0.2);
    assert.deepEqual(presenter.crosshair, { u: 0.2, v: 0.2 });
    presenter.setPhoto(null);
    assert.equal(presenter.crosshair, null);
    assert.equal(presenter.diagnostics(), null);
});

test("ANALYSIS-09：只读库优先；缺结果且即时计算关闭时保持未提取、零推理", async () => {
    const clock = manualClock();
    const target = photo("a");
    const store = makeStore(clock, { features: new Map([[target.id, featuresFor(target)]]) });
    const presenter = new AnalysisPresenter({ t, clock, store });
    presenter.setDisplaySurfaceReady(true);
    presenter.setVisible(true);
    presenter.setPhoto(target);
    await flush();
    assert.equal(store.calls.read.length, 1);
    assert.equal(store.calls.compute.length, 0);
    assert.equal(tile(presenter, "pca").state, "content");

    const emptyStore = makeStore(clock);
    const empty = new AnalysisPresenter({ t, clock, store: emptyStore });
    empty.setDisplaySurfaceReady(true);
    empty.setVisible(true);
    empty.setPhoto(photo("b"));
    await flush();
    clock.advance(2000);
    await flush();
    assert.equal(emptyStore.calls.compute.length, 0);
    assert.equal(tile(empty, "pca").state, "empty");
    assert.equal(tile(empty, "pca").message, "未提取");
    assert.equal(tile(empty, "histogram").state, "empty");
});

test("ANALYSIS-09：增强联动优先于只读库，按 enhanced 变体现算", async () => {
    const clock = manualClock();
    const target = photo("a");
    const store = makeStore(clock, { features: new Map([[target.id, featuresFor(target)]]) });
    const presenter = new AnalysisPresenter({ t, clock, store });
    presenter.setImmediateCompute(true);
    presenter.setEnhanced(true);
    presenter.setVisible(true);
    presenter.setPhoto(target);
    await flush();

    assert.equal(store.calls.read.length, 0);
    assert.equal(store.calls.compute.length, 1);
    assert.equal(store.calls.compute[0].variant, "enhanced");
    assert.equal(tile(presenter, "pca").state, "content");
});

test("ANALYSIS-09：未提取回退先延迟 500ms，快速切图期间零计算", async () => {
    const clock = manualClock();
    const first = photo("a");
    const second = photo("b");
    const store = makeStore(clock);
    const presenter = new AnalysisPresenter({ t, clock, store });
    presenter.setImmediateCompute(true);
    presenter.setDisplaySurfaceReady(true);
    presenter.setVisible(true);

    presenter.setPhoto(first);
    await flush();
    assert.equal(tile(presenter, "pca").state, "pending");
    assert.equal(tile(presenter, "pca").message, "计算中…");
    clock.advance(499);
    await flush();
    assert.equal(store.calls.compute.length, 0);

    clock.advance(1);
    await flush();
    assert.equal(store.calls.compute.length, 1);
    assert.equal(store.calls.compute[0].variant, "original");
    assert.equal(tile(presenter, "pca").state, "content");

    const quickStore = makeStore(clock);
    const quick = new AnalysisPresenter({ t, clock, store: quickStore });
    quick.setImmediateCompute(true);
    quick.setDisplaySurfaceReady(true);
    quick.setVisible(true);
    quick.setPhoto(first);
    await flush();
    clock.advance(100);
    await flush();
    quick.setPhoto(second);
    await flush();
    clock.advance(400);
    await flush();
    assert.equal(quickStore.calls.compute.length, 0);
    clock.advance(100);
    await flush();
    assert.equal(quickStore.calls.compute.length, 1);
    assert.equal(quickStore.calls.compute[0].photo.id, second.id);
});

test("ANALYSIS-10：切图保留上一张瓦片直到新数据到达，过期结果丢弃", async () => {
    const clock = manualClock();
    const a = photo("a", { shake: true, scene: { base: 11, variant: 0 } });
    const b = photo("b", { shake: false, scene: { base: 12, variant: 0 } });
    const store = makeStore(clock, {
        features: new Map([[a.id, featuresFor(a)], [b.id, featuresFor(b)]]),
        readDelay: (target) => (target.id === b.id ? 50 : 0),
    });
    const presenter = new AnalysisPresenter({ t, clock, store });
    presenter.setDisplaySurfaceReady(true);
    presenter.setVisible(true);
    presenter.setPhoto(a);
    await flush();
    assert.equal(tile(presenter, "shake").label, "抖动");

    presenter.setPhoto(b);
    assert.equal(tile(presenter, "shake").label, "抖动");
    assert.equal(tile(presenter, "shake").state, "content");

    clock.advance(50);
    await flush();
    assert.equal(tile(presenter, "shake").label, "稳定");

    const slow = makeStore(clock, {
        features: new Map([[a.id, featuresFor(a)], [b.id, featuresFor(b)]]),
        readDelay: (target) => (target.id === a.id ? 200 : 0),
        ignoreAbort: true,
    });
    const stale = new AnalysisPresenter({ t, clock, store: slow });
    stale.setDisplaySurfaceReady(true);
    stale.setVisible(true);
    stale.setPhoto(a);
    stale.setPhoto(b);
    await flush();
    assert.equal(tile(stale, "shake").label, "稳定");
    clock.advance(200);
    await flush();
    assert.equal(tile(stale, "shake").label, "稳定");
});

test("ANALYSIS-10：只读库按指纹缓存，切回已缓存照片直接出图", async () => {
    const clock = manualClock();
    const a = photo("a");
    const b = photo("b");
    const store = makeStore(clock, {
        features: new Map([[a.id, featuresFor(a)], [b.id, featuresFor(b)]]),
    });
    const presenter = new AnalysisPresenter({ t, clock, store });
    presenter.setDisplaySurfaceReady(true);
    presenter.setVisible(true);
    presenter.setPhoto(a);
    await flush();
    presenter.setPhoto(b);
    await flush();
    assert.equal(store.calls.read.length, 2);

    presenter.setPhoto(a);
    assert.equal(tile(presenter, "pca").state, "content");
    assert.equal(store.calls.read.length, 2);
});

test("ANALYSIS-10：单张读取失败回落到可区分的失败状态并可恢复", async () => {
    const clock = manualClock();
    const failingStore = makeStore(clock, { failRead: true });
    const presenter = new AnalysisPresenter({ t, clock, store: failingStore });
    presenter.setDisplaySurfaceReady(true);
    presenter.setVisible(true);
    presenter.setPhoto(photo("a"));
    await flush();
    assert.equal(failingStore.calls.read.length, 1);
    assert.equal(tile(presenter, "pca").state, "failed");
    assert.equal(tile(presenter, "pca").message, "计算失败");

    const outcomeStore = makeStore(clock, { features: new Map([["b", null]]) });
    const ok = new AnalysisPresenter({ t, clock, store: outcomeStore });
    ok.setDisplaySurfaceReady(true);
    ok.setVisible(true);
    ok.setPhoto(photo("b"));
    await flush();
    assert.equal(tile(ok, "pca").state, "empty");
    assert.equal(tile(ok, "pca").message, "未提取");
    assert.notEqual(tile(ok, "pca").state, "failed");
});

test("ANALYSIS-07：直方图 256 桶、最后一次为准、隐藏或代理期清空", async () => {
    const clock = manualClock();
    const target = photo("a");
    const presenter = new AnalysisPresenter({ t, clock, store: makeStore(clock) });
    presenter.setVisible(true);
    presenter.setPhoto(target);
    presenter.setHistogramSource(new Uint8ClampedArray(16), 2, 2);
    assert.equal(presenter.histogram(), null);

    presenter.setDisplaySurfaceReady(true);
    const first = new Uint8ClampedArray([255, 0, 0, 255, 0, 255, 0, 255, 0, 0, 255, 255, 255, 255, 255, 255]);
    const second = new Uint8ClampedArray([10, 10, 10, 255, 20, 20, 20, 255, 30, 30, 30, 255, 40, 40, 40, 255]);
    presenter.setHistogramSource(first, 2, 2);
    presenter.setHistogramSource(second, 2, 2);
    assert.equal(tile(presenter, "histogram").state, "pending");
    clock.advance(16);
    await flush();

    const histogram = presenter.histogram();
    assert.equal(histogram.r.length, 256);
    assert.equal(histogram.count, 4);
    assert.equal(histogram.r[255], 0);
    assert.equal(histogram.r[10], 1);
    assert.equal(histogram.luma[10], 1);
    assert.equal(tile(presenter, "histogram").state, "content");

    presenter.setHistogramSource(first, 2, 2);
    clock.advance(16);
    await flush();
    assert.equal(presenter.histogram().r[255], 2);
    assert.equal(presenter.histogram().luma[255], 1);
    assert.equal(presenter.histogram().luma[54], 1);
    assert.equal(presenter.histogram().luma[182], 1);
    assert.equal(presenter.histogram().luma[18], 1);

    presenter.setDisplaySurfaceReady(false);
    assert.equal(presenter.histogram(), null);
    assert.equal(tile(presenter, "histogram").state, "empty");

    presenter.setVisible(false);
    assert.equal(presenter.histogram(), null);
});

test("ANALYSIS-06：代理期细节预览留空，Full 就绪后出图", () => {
    const clock = manualClock();
    const target = photo("a", { focus: { x: 0.5, y: 0.5, w: 0.1, h: 0.1 } });
    const presenter = new AnalysisPresenter({ t, clock, store: makeStore(clock) });
    presenter.setVisible(true);
    presenter.setPhoto(target);
    assert.equal(tile(presenter, "focus").state, "empty");
    assert.equal(tile(presenter, "center").state, "empty");
    assert.equal(tile(presenter, "histogram").state, "empty");

    presenter.setDisplaySurfaceReady(true);
    assert.equal(tile(presenter, "focus").state, "content");
    assert.equal(tile(presenter, "center").state, "content");
});

test("ANALYSIS-06：同一张照片 EXIF 异步到达后插入对焦点瓦片，不清空准星", async () => {
    const clock = manualClock();
    const target = photo("a");
    const presenter = new AnalysisPresenter({
        t,
        clock,
        store: makeStore(clock, { features: new Map([[target.id, featuresFor(target)]]) }),
    });
    presenter.setDisplaySurfaceReady(true);
    presenter.setVisible(true);
    presenter.setPhoto(target);
    await flush();
    presenter.clickDiagnostic(0.4, 0.6);

    const updated = { ...target, focus: { x: 0.4, y: 0.6, w: 0.1, h: 0.1 } };
    presenter.setPhoto(updated);
    assert.equal(presenter.photo, updated);
    assert.deepEqual(presenter.crosshair, { u: 0.4, v: 0.6 });
    assert.equal(tile(presenter, "focus").state, "content");
});

test("ANALYSIS-06：裁剪窗口按显示朝向取整并夹到边界", () => {
    const clock = manualClock();
    const target = photo("a", { focus: { x: 0.005, y: 0.995, w: 0.1, h: 0.05 } });
    const presenter = new AnalysisPresenter({ t, clock, store: makeStore(clock) });
    presenter.setPhoto(target);

    assert.deepEqual(presenter.detailCrop("center", 100, 2, { width: 6000, height: 4000 }), {
        cx: 3000,
        cy: 2000,
        w: 200,
        h: 200,
    });
    assert.deepEqual(presenter.detailCrop("focus", 100, 2, { width: 6000, height: 4000 }), {
        cx: 100,
        cy: 3900,
        w: 200,
        h: 200,
    });
    assert.deepEqual(presenter.detailCrop("center", 100, 2, { width: 120, height: 80 }), {
        cx: 60,
        cy: 40,
        w: 120,
        h: 80,
    });
    assert.deepEqual(presenter.detailCrop("center", 100, 2, { width: 4000, height: 6000 }), {
        cx: 2000,
        cy: 3000,
        w: 200,
        h: 200,
    });
    assert.equal(presenter.detailCrop("center", 100, 2, { width: 0, height: 0 }), null);

    assert.deepEqual(presenter.highlightRect("focus", 100, 2, { width: 6000, height: 4000 }), {
        cx: 300,
        cy: 3900,
        w: 600,
        h: 200,
    });
    assert.deepEqual(presenter.highlightRect("center", 100, 2, { width: 6000, height: 4000 }), {
        cx: 3000,
        cy: 2000,
        w: 200,
        h: 200,
    });
});

test("ANALYSIS-04：绿框只在悬停/定格细节预览时存在，离开、换图与隐藏立即清除", () => {
    const clock = manualClock();
    const presenter = new AnalysisPresenter({ t, clock, store: makeStore(clock) });
    presenter.setVisible(true);
    presenter.setPhoto(photo("a"));

    assert.equal(presenter.hoverDetail("center"), "center");
    assert.equal(presenter.hoverKind, "center");
    presenter.setPhoto(photo("b"));
    assert.equal(presenter.hoverKind, null);

    presenter.hoverDetail("center");
    presenter.setVisible(false);
    assert.equal(presenter.hoverKind, null);

    presenter.setVisible(true);
    assert.equal(presenter.hoverDetail("pca"), null);
});

test("ANALYSIS-04：双击细节预览抛出定位事件，不改变照片与评分", () => {
    const clock = manualClock();
    const calls = [];
    const presenter = new AnalysisPresenter({
        t,
        clock,
        store: makeStore(clock),
        onLocate: (u, v) => calls.push({ u, v }),
    });
    const target = photo("a", { focus: { x: 0.25, y: 0.75, w: 0.1, h: 0.1 }, rating: 3 });
    presenter.setPhoto(target);

    presenter.locateDetail("center");
    presenter.locateDetail("focus");
    presenter.locateDetail("pca");
    assert.deepEqual(calls, [{ u: 0.5, v: 0.5 }, { u: 0.25, v: 0.75 }]);
    assert.equal(presenter.photo, target);
    assert.equal(target.rating, 3);
});
