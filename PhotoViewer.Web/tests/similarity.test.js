import { test } from "node:test";
import assert from "node:assert/strict";
import { createTokens } from "../src/core/tokens.js";
import { manualClock } from "../src/core/clock.js";
import { SimilarityPanel, countUnextracted } from "../src/core/similarity.js";

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
        width: 100,
        height: 100,
        orientation: 1,
        focus: null,
        exif: {},
        shake: null,
        fingerprint: `fp-${id}`,
        extracted: true,
        scene: { base: 1, variant: 0 },
        ...overrides,
    };
}

function card(representative, companions = []) {
    return {
        key: representative.id,
        photo: representative,
        companions,
        displayName: representative.name,
    };
}

function features(cls) {
    const vector = Float32Array.from(cls);
    let sum = 0;
    for (const value of vector) sum += value * value;
    const norm = Math.sqrt(sum) || 1;
    for (let i = 0; i < vector.length; i++) vector[i] /= norm;
    return { cls: vector, patches: null, pca: null, sharpness: null, shake: null, verdict: "稳定" };
}

function delayed(clock, ms, run, signal) {
    if (signal?.aborted) return Promise.reject(new Error("aborted"));
    if (!ms) {
        try {
            return Promise.resolve(run());
        } catch (error) {
            return Promise.reject(error);
        }
    }
    return new Promise((resolve, reject) => {
        const timer = clock.setTimeout(() => {
            try {
                resolve(run());
            } catch (error) {
                reject(error);
            }
        }, ms);
        signal?.addEventListener("abort", () => {
            clock.clearTimeout(timer);
            reject(new Error("aborted"));
        }, { once: true });
    });
}

function makeStore(clock, options = {}) {
    const {
        featuresById = new Map(),
        readDelay = 0,
        extractDelay = 0,
        failReads = new Set(),
        failGroups = new Set(),
        extractedFeatures = () => features([1, 0.2]),
    } = options;
    const calls = { read: [], extract: [] };
    return {
        calls,
        read(target, { signal } = {}) {
            calls.read.push({ photo: target, signal });
            if (failReads.has(target.id)) return Promise.reject(new Error("read failed"));
            return delayed(clock, readDelay, () => featuresById.get(target.id) ?? null, signal);
        },
        extractGroup(photos) {
            calls.extract.push(photos.map((entry) => entry.id));
            const key = photos[0]?.fingerprint ?? photos[0]?.id;
            return delayed(clock, extractDelay, () => {
                if (failGroups.has(key)) throw new Error("extract failed");
                for (const entry of photos) {
                    entry.extracted = true;
                    if (!featuresById.has(entry.id)) featuresById.set(entry.id, extractedFeatures(entry));
                }
            });
        },
    };
}

function ids(panel) {
    return panel.snapshot().items.map((item) => item.card.photo.id);
}

test("SIM-01：可见 = 展开且文件栏可见；不可见不计算、不读库、不报错", async () => {
    const clock = manualClock();
    const anchor = photo("a");
    const other = photo("b");
    const store = makeStore(clock, {
        featuresById: new Map([[anchor.id, features([1, 0])], [other.id, features([1, 0.2])]]),
    });
    const panel = new SimilarityPanel({ t, clock, store });
    panel.setPool([card(anchor), card(other)]);
    panel.setAnchor(anchor);
    await flush();
    assert.equal(panel.snapshot().visible, false);
    assert.equal(store.calls.read.length, 0);
    assert.equal(panel.snapshot().error, null);

    panel.setExpanded(true);
    await flush();
    assert.equal(panel.snapshot().visible, true);
    assert.ok(store.calls.read.length > 0);
    assert.deepEqual(ids(panel), ["b"]);

    panel.setFilebarVisible(false);
    assert.equal(panel.snapshot().visible, false);
    const reads = store.calls.read.length;
    panel.setAnchor(photo("c"));
    await flush();
    assert.equal(store.calls.read.length, reads);

    panel.setFilebarVisible(true);
    await flush();
    assert.ok(store.calls.read.length > reads);
});

test("SIM-02：两种空态互斥，未提取按指纹组统计（含缺拍摄时间的孤立文件）", async () => {
    const clock = manualClock();
    const anchor = photo("a");
    const groupA = photo("g1", { fingerprint: "same", extracted: false });
    const groupB = photo("g2", { fingerprint: "same", extracted: false });
    const orphan = photo("orphan", { fingerprint: null, taken: null, extracted: false });
    const store = makeStore(clock, { featuresById: new Map([[anchor.id, features([1, 0])]]) });
    const panel = new SimilarityPanel({ t, clock, store });
    panel.setPool([card(anchor), card(groupA, [groupB]), card(orphan)]);
    panel.setAnchor(anchor);
    panel.setExpanded(true);
    await flush();

    assert.equal(panel.snapshot().unextracted, 2);
    assert.equal(panel.snapshot().state, "empty");
    assert.equal(panel.snapshot().emptyText, "尚未提取特征");
    assert.equal(panel.snapshot().action.visible, true);
    assert.equal(panel.snapshot().action.label, "提取全部");
    assert.equal(panel.snapshot().items.length, 0);

    const sameGroupAcrossCards = photo("g3", { fingerprint: "same", extracted: false });
    panel.setPool([card(anchor), card(groupA, [groupB]), card(orphan), card(sameGroupAcrossCards)]);
    assert.equal(panel.snapshot().unextracted, 2);

    for (const entry of [groupA, groupB, orphan, sameGroupAcrossCards]) entry.extracted = true;
    store.calls.read.length = 0;
    panel.setPool([card(anchor), card(groupA, [groupB]), card(orphan), card(sameGroupAcrossCards)]);
    await flush();
    assert.equal(panel.snapshot().unextracted, 0);
    assert.equal(panel.snapshot().emptyText, "无相似照片");
    assert.equal(panel.snapshot().action.visible, false);

    panel.setAnchor(null);
    assert.equal(panel.snapshot().emptyText, "无相似照片");
    assert.equal(panel.snapshot().action.visible, false);
    assert.equal(panel.snapshot().error, null);
});

test("SIM-02：countUnextracted 只按指纹组计数，已入库伴侣整组算齐备", () => {
    const a = photo("a", { fingerprint: "fp", extracted: false });
    const b = photo("b", { fingerprint: "fp", extracted: true });
    const c = photo("c", { fingerprint: null, taken: null, extracted: false });
    const d = photo("d", { fingerprint: null, taken: null, extracted: false });
    assert.equal(countUnextracted([card(a, [b])]), 0);
    assert.equal(countUnextracted([card(a, [b]), card(c), card(d)]), 2);
    assert.equal(countUnextracted([]), 0);
});

test("SIM-03：提取按指纹组推进，重复触发无效，完成后刷新计数与列表", async () => {
    const clock = manualClock();
    const anchor = photo("anchor");
    const g1a = photo("g1a", { fingerprint: "g1", extracted: false });
    const g1b = photo("g1b", { fingerprint: "g1", extracted: false });
    const g2 = photo("g2", { fingerprint: "g2", extracted: false });
    const g3 = photo("g3", { fingerprint: "g3", extracted: true });
    const store = makeStore(clock, {
        featuresById: new Map([
            [anchor.id, features([1, 0])],
            [g3.id, features([1, 0.45])],
        ]),
        extractDelay: 10,
    });
    const panel = new SimilarityPanel({ t, clock, store });
    panel.setPool([card(anchor), card(g1a, [g1b]), card(g2), card(g3)]);
    panel.setAnchor(anchor);
    panel.setExpanded(true);
    await flush();
    assert.deepEqual(ids(panel), ["g3"]);

    const running = panel.extractAll();
    assert.deepEqual(panel.snapshot().progress, { done: 0, total: 2 });
    await panel.extractAll();
    assert.equal(store.calls.extract.length, 1);

    clock.advance(10);
    await flush();
    assert.deepEqual(panel.snapshot().progress, { done: 1, total: 2 });
    assert.equal(panel.snapshot().unextracted, 1);

    clock.advance(10);
    await flush();
    await running;
    assert.equal(panel.snapshot().progress, null);
    assert.equal(panel.snapshot().unextracted, 0);
    assert.equal(panel.snapshot().action.visible, true);
    assert.equal(panel.snapshot().action.label, "补齐全部");
    assert.deepEqual(ids(panel).sort(), ["g1a", "g2", "g3"]);
    assert.deepEqual(store.calls.extract, [["g1a", "g1b"], ["g2"]]);
});

test("SIM-03：关闭面板不中断提取，但不再重算列表", async () => {
    const clock = manualClock();
    const anchor = photo("anchor");
    const g1 = photo("g1", { fingerprint: "g1", extracted: false });
    const store = makeStore(clock, {
        featuresById: new Map([[anchor.id, features([1, 0])]]),
        extractDelay: 10,
    });
    const panel = new SimilarityPanel({ t, clock, store });
    panel.setPool([card(anchor), card(g1)]);
    panel.setAnchor(anchor);
    panel.setExpanded(true);
    await flush();
    const reads = store.calls.read.length;

    const running = panel.extractAll();
    panel.setExpanded(false);
    clock.advance(10);
    await flush();
    await running;
    assert.equal(panel.snapshot().progress, null);
    assert.equal(panel.snapshot().unextracted, 0);
    assert.equal(store.calls.read.length, reads);
});

test("SIM-04/SIM-08：归一分数 = (cos+1)/2，柱长以下限为基准，百分比四舍五入", async () => {
    const clock = manualClock();
    const anchor = photo("anchor");
    const p90 = photo("p90");
    const p75 = photo("p75");
    const p100 = photo("p100");
    const store = makeStore(clock, {
        featuresById: new Map([
            [anchor.id, features([1, 0])],
            [p90.id, features([0.9, Math.sqrt(1 - 0.81)])],
            [p75.id, features([0.75, Math.sqrt(1 - 0.5625)])],
            [p100.id, features([1, 0])],
        ]),
    });
    const panel = new SimilarityPanel({ t, clock, store });
    panel.setParams({ threshold: 0.75, maxResults: 8 });
    panel.setPool([card(anchor), card(p90), card(p75), card(p100)]);
    panel.setAnchor(anchor);
    panel.setExpanded(true);
    await flush();

    const items = panel.snapshot().items;
    assert.deepEqual(items.map((item) => item.card.photo.id), ["p100", "p90", "p75"]);
    assert.ok(Math.abs(items.find((item) => item.card.photo.id === "p90").score - 0.95) < 1e-6);
    assert.equal(items.find((item) => item.card.photo.id === "p90").percent, 95);
    assert.equal(items.find((item) => item.card.photo.id === "p75").percent, 88);
    assert.ok(Math.abs(items.find((item) => item.card.photo.id === "p90").barRatio - 0.8) < 1e-5);
    assert.ok(Math.abs(items.find((item) => item.card.photo.id === "p75").barRatio - 0.5) < 1e-5);
    assert.equal(items.find((item) => item.card.photo.id === "p100").barRatio, 1);

    panel.setParams({ threshold: 0.9 });
    clock.advance(150);
    await flush();
    assert.deepEqual(ids(panel), ["p100", "p90"]);
    assert.ok(Math.abs(panel.snapshot().items[1].barRatio - 0.8) < 1e-5);
});

test("SIM-05：锚点只由胶片栏切图与重新打开改变，显示身份只用于面板内高亮", async () => {
    const clock = manualClock();
    const anchor = photo("a");
    const other = photo("b");
    const store = makeStore(clock, {
        featuresById: new Map([[anchor.id, features([1, 0])], [other.id, features([1, 0.2])]]),
    });
    const panel = new SimilarityPanel({ t, clock, store });
    panel.setPool([card(anchor), card(other)]);
    panel.setAnchor(anchor);
    panel.setExpanded(true);
    await flush();
    assert.deepEqual(ids(panel), ["b"]);
    assert.equal(panel.snapshot().items[0].current, false);

    const reads = store.calls.read.length;
    panel.setDisplayed("b");
    assert.equal(store.calls.read.length, reads);
    assert.equal(panel.snapshot().items[0].current, true);
    assert.equal(panel.anchor.id, "a");

    panel.setExpanded(false);
    panel.setExpanded(true);
    await flush();
    assert.equal(panel.anchor.id, "b");
    assert.ok(store.calls.read.length > reads);
    assert.deepEqual(ids(panel), ["a"]);

    panel.setAnchor(anchor);
    await flush();
    assert.equal(panel.anchor.id, "a");
    assert.deepEqual(ids(panel), ["b"]);
});

test("SIM-06：新计算取消旧计算，计算中保留上一次结果", async () => {
    const clock = manualClock();
    const anchor = photo("a");
    const b = photo("b");
    const c = photo("c");
    const d = photo("d");
    const store = makeStore(clock, {
        featuresById: new Map([
            [anchor.id, features([1, 0])],
            [b.id, features([1, 0.3])],
            [c.id, features([0.95, 0.4])],
            [d.id, features([0.9, 0.5])],
        ]),
        readDelay: 30,
    });
    const panel = new SimilarityPanel({ t, clock, store });
    panel.setPool([card(anchor), card(b)]);
    panel.setAnchor(anchor);
    panel.setExpanded(true);
    clock.advance(30);
    await flush();
    clock.advance(30);
    await flush();
    assert.deepEqual(ids(panel), ["b"]);

    panel.setPool([card(anchor), card(c)]);
    const inFlightSignal = store.calls.read[2].signal;
    assert.deepEqual(ids(panel), ["b"]);

    panel.setPool([card(anchor), card(d)]);
    assert.equal(inFlightSignal.aborted, true);
    assert.deepEqual(ids(panel), ["b"]);

    clock.advance(30);
    await flush();
    clock.advance(30);
    await flush();
    assert.deepEqual(ids(panel), ["d"]);
});

test("SIM-06：没有上一次结果时按 SIM-02 显示空态文案，不留白", async () => {
    const clock = manualClock();
    const anchor = photo("a");
    const b = photo("b");
    const store = makeStore(clock, {
        featuresById: new Map([[anchor.id, features([1, 0])], [b.id, features([1, 0.2])]]),
        readDelay: 30,
    });
    const panel = new SimilarityPanel({ t, clock, store });
    panel.setPool([card(anchor), card(b)]);
    panel.setAnchor(anchor);
    panel.setExpanded(true);
    const snapshot = panel.snapshot();
    assert.equal(snapshot.calculating, true);
    assert.equal(snapshot.emptyText, "无相似照片");
    assert.equal(snapshot.state, "empty");

    clock.advance(30);
    await flush();
    clock.advance(30);
    await flush();
    assert.deepEqual(ids(panel), ["b"]);
    assert.equal(panel.snapshot().emptyText, null);
});

test("SIM-02/03：未提取计数与提取范围取整个文件夹，候选池仍为筛选结果", async () => {
    const clock = manualClock();
    const anchor = photo("anchor");
    const shown = photo("shown", { fingerprint: "g1", extracted: true });
    const hidden = photo("hidden", { fingerprint: "g2", extracted: false });
    const store = makeStore(clock, {
        featuresById: new Map([[anchor.id, features([1, 0])], [shown.id, features([1, 0.2])]]),
    });
    const panel = new SimilarityPanel({ t, clock, store });
    panel.setPool([card(anchor), card(shown)]);
    panel.setFolderCards([card(anchor), card(shown), card(hidden)]);
    panel.setAnchor(anchor);
    panel.setExpanded(true);
    await flush();
    assert.equal(panel.snapshot().unextracted, 1);
    assert.deepEqual(ids(panel), ["shown"]);

    await panel.extractAll();
    assert.deepEqual(store.calls.extract, [["hidden"]]);
    assert.equal(hidden.extracted, true);
    assert.equal(panel.snapshot().unextracted, 0);
});

test("SIM-08：排除自身与同指纹、归一阈值、上限、同分按拍摄时间更近优先", async () => {
    const clock = manualClock();
    const anchor = photo("a");
    const clone = photo("clone", { fingerprint: "fp-a" });
    const b = photo("b", { taken: 100 });
    const d = photo("d", { taken: 200 });
    const e = photo("e", { taken: 10 });
    const f = photo("f", { taken: 20 });
    const low = photo("low", { taken: 999 });
    const store = makeStore(clock, {
        featuresById: new Map([
            [anchor.id, features([1, 0])],
            [clone.id, features([1, 0])],
            [b.id, features([0.9, Math.sqrt(1 - 0.81)])],
            [d.id, features([0.9, Math.sqrt(1 - 0.81)])],
            [e.id, features([0.96, Math.sqrt(1 - 0.9216)])],
            [f.id, features([0.92, Math.sqrt(1 - 0.8464)])],
            [low.id, features([0.6, Math.sqrt(1 - 0.36)])],
        ]),
    });
    const panel = new SimilarityPanel({ t, clock, store });
    panel.setParams({ threshold: 0.85, maxResults: 8 });
    panel.setPool([card(anchor), card(clone), card(b), card(d), card(e), card(f), card(low)]);
    panel.setAnchor(anchor);
    panel.setExpanded(true);
    await flush();
    assert.deepEqual(ids(panel), ["e", "f", "d", "b"]);
    assert.equal(panel.snapshot().items[0].percent, 98);
    assert.ok(Math.abs(panel.snapshot().items.find((item) => item.card.photo.id === "b").score - 0.95) < 1e-6);

    panel.setParams({ maxResults: 2 });
    clock.advance(150);
    await flush();
    assert.deepEqual(ids(panel), ["e", "f"]);
});

test("SIM-09：改星后按同一锚点重算，落出筛选的项消失", async () => {
    const clock = manualClock();
    const anchor = photo("a");
    const b = photo("b", { rating: 3 });
    const c = photo("c");
    const store = makeStore(clock, {
        featuresById: new Map([
            [anchor.id, features([1, 0])],
            [b.id, features([1, 0.2])],
            [c.id, features([1, 0.3])],
        ]),
    });
    const panel = new SimilarityPanel({ t, clock, store });
    panel.setPool([card(anchor), card(b), card(c)]);
    panel.setAnchor(anchor);
    panel.setExpanded(true);
    await flush();
    assert.deepEqual(ids(panel), ["b", "c"]);

    panel.setPool([card(anchor), card(c)]);
    panel.rated("b");
    await flush();
    assert.deepEqual(ids(panel), ["c"]);
    assert.equal(panel.anchor.id, "a");

    const reads = store.calls.read.length;
    panel.rated("a");
    assert.ok(store.calls.read.length > reads);
    assert.equal(panel.anchor.id, "a");
    assert.deepEqual(ids(panel), ["c"]);
});

test("SIM-10：提取失败给出可读文案、跳过失败组并可重试", async () => {
    const clock = manualClock();
    const anchor = photo("anchor");
    const g1 = photo("g1", { fingerprint: "g1", extracted: false });
    const g2 = photo("g2", { fingerprint: "g2", extracted: false });
    const failGroups = new Set(["g1"]);
    const store = makeStore(clock, {
        featuresById: new Map([[anchor.id, features([1, 0])]]),
        failGroups,
    });
    const panel = new SimilarityPanel({ t, clock, store });
    panel.setPool([card(anchor), card(g1), card(g2)]);
    panel.setAnchor(anchor);
    panel.setExpanded(true);
    await flush();

    await panel.extractAll();
    assert.equal(panel.snapshot().progress, null);
    assert.match(panel.snapshot().error, /1 组提取失败/);
    assert.equal(panel.snapshot().unextracted, 1);
    assert.equal(panel.snapshot().action.visible, true);

    failGroups.clear();
    await panel.extractAll();
    assert.equal(panel.snapshot().error, null);
    assert.equal(panel.snapshot().unextracted, 0);
    assert.equal(panel.snapshot().progress, null);
});

test("SIM-10：相似度计算失败保留上一次结果，不显示错误", async () => {
    const clock = manualClock();
    const anchor = photo("a");
    const b = photo("b");
    const c = photo("c");
    const failReads = new Set();
    const store = makeStore(clock, {
        featuresById: new Map([
            [anchor.id, features([1, 0])],
            [b.id, features([1, 0.2])],
            [c.id, features([1, 0.3])],
        ]),
        failReads,
    });
    const panel = new SimilarityPanel({ t, clock, store });
    panel.setPool([card(anchor), card(b)]);
    panel.setAnchor(anchor);
    panel.setExpanded(true);
    await flush();
    assert.deepEqual(ids(panel), ["b"]);

    failReads.add(c.id);
    panel.setPool([card(anchor), card(c)]);
    await flush();
    assert.deepEqual(ids(panel), ["b"]);
    assert.equal(panel.snapshot().error, null);
    assert.equal(panel.snapshot().state, "list");
});
