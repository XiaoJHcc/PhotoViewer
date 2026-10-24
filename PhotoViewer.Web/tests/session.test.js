import { test } from "node:test";
import assert from "node:assert/strict";
import { createTokens } from "../src/core/tokens.js";
import { manualClock } from "../src/core/clock.js";
import { PhotoSession, RatingWriter } from "../src/core/session.js";

const t = createTokens();

function fakePhoto(name) {
    const stem = name.replace(/\.[^.]+$/, "");
    return {
        id: `folder/${name}`,
        folderId: "folder",
        name,
        stem,
        ext: name.slice(stem.length).toLowerCase(),
        size: 1024,
        modified: 0,
        taken: null,
        tzOffset: 0,
        rating: 0,
        width: 6000,
        height: 4000,
        orientation: 1,
        focus: null,
        exif: {},
        shake: null,
        fingerprint: null,
        fault: {},
        scene: { base: 1, variant: 0 },
    };
}

function fakeCard(repName, companionNames = []) {
    const photo = fakePhoto(repName);
    const companions = companionNames.map(fakePhoto);
    return { key: photo.id, photo, companions, displayName: photo.stem };
}

function proxySurface(tag, width = 1600) {
    return { tier: "proxy", width, height: Math.round(width * 0.75), surface: { kind: "proxy", tag } };
}

function fullSurface(tag) {
    return { tier: "full", width: 6000, height: 4000, surface: { kind: "full", tag } };
}

function deferred() {
    let resolve;
    let reject;
    const promise = new Promise((res, rej) => {
        resolve = res;
        reject = rej;
    });
    return { promise, resolve, reject };
}

function flush() {
    return new Promise((resolve) => setImmediate(resolve));
}

function harness(options = {}) {
    const clock = manualClock();
    const decodeCalls = [];
    const enhanceCalls = [];
    const snapshots = [];
    const decoder = {
        decode(photo, tier, options) {
            const pending = deferred();
            decodeCalls.push({ photo, tier, options, pending });
            return pending.promise;
        },
        enhance(surface, options) {
            const pending = deferred();
            enhanceCalls.push({ surface, options, pending });
            return pending.promise;
        },
    };
    const session = new PhotoSession({
        t,
        clock,
        decoder,
        cacheLimit: options.cacheLimit,
        onChange: (snapshot) => snapshots.push(snapshot),
    });
    return { clock, session, decodeCalls, enhanceCalls, snapshots };
}

async function loadProxy(harnessRef, index, result) {
    harnessRef.decodeCalls[index].pending.resolve(result);
    await flush();
}

test("STATE-01：未命中缓存的切图立即清旧图，300 ms 内完成不显示加载提示", async () => {
    const h = harness();
    const a = fakePhoto("A.JPG");

    h.session.show(a);
    assert.equal(h.session.snapshot().photoId, a.id);
    assert.equal(h.session.snapshot().surface, null);
    assert.equal(h.session.snapshot().tier, "none");
    assert.equal(h.session.snapshot().loadingHint, false);

    await loadProxy(h, 0, proxySurface("A"));
    assert.deepEqual(h.session.snapshot().surface, { kind: "proxy", tag: "A" });

    const b = fakePhoto("B.JPG");
    h.session.show(b);
    assert.equal(h.session.snapshot().photoId, b.id);
    // IMAGE-10：需要解码时切图即清旧图
    assert.equal(h.session.snapshot().surface, null);
    assert.equal(h.session.snapshot().tier, "none");

    h.clock.advance(250);
    assert.equal(h.session.snapshot().loadingHint, false);
    await loadProxy(h, 1, proxySurface("B"));
    h.clock.advance(1000);
    assert.equal(h.session.snapshot().loadingHint, false);
    assert.deepEqual(h.session.snapshot().surface, { kind: "proxy", tag: "B" });
});

test("STATE-01：缓存命中切图保留前一张——同步换上新图、全程不空、不显示加载提示", async () => {
    const h = harness();
    const a = fakePhoto("A.JPG");
    const b = fakePhoto("B.JPG");

    h.session.show(a);
    await loadProxy(h, 0, proxySurface("A"));
    const surfaceA = h.session.snapshot().surface;
    h.session.show(b);
    await loadProxy(h, 1, proxySurface("B"));
    assert.equal(h.decodeCalls.length, 2);

    const mark = h.snapshots.length;
    h.session.show(a);
    const switched = h.snapshots.slice(mark);
    assert.ok(switched.length >= 1, "命中缓存没有发出状态通知");
    assert.equal(switched[0].surface, surfaceA, "命中缓存没有在 show 返回时换上新图");
    assert.equal(switched[0].photoId, a.id);
    assert.ok(switched.every((snapshot) => snapshot.surface !== null), "命中缓存期间出现空屏快照");
    assert.ok(switched.every((snapshot) => snapshot.loadingHint === false), "命中缓存期间出现加载提示");
    assert.equal(h.decodeCalls.length, 2, "命中缓存仍发起了重新解码");
    assert.equal(h.clock.pendingTimers(), 0, "命中缓存仍起了 300 ms 提示计时");

    h.clock.advance(1000);
    assert.equal(h.session.snapshot().loadingHint, false);
    assert.deepEqual(h.session.snapshot().surface, surfaceA);
});

test("STATE-01：需要 Full 而缓存只有代理按未命中处理，清图走黑屏加载", async () => {
    const h = harness();
    const a = fakePhoto("A.JPG");
    h.session.show(a);
    await loadProxy(h, 0, proxySurface("A", 800));
    h.session.show(fakePhoto("B.JPG"));
    await loadProxy(h, 1, proxySurface("B"));

    h.session.show(a, { wantFull: true });
    // 低分辨率顶替默认禁用：代理不得先顶替 Full 上屏。
    assert.equal(h.session.snapshot().surface, null, "需要 Full 时仍拿代理缓存上屏");
    assert.equal(h.session.snapshot().tier, "none");
    assert.equal(h.decodeCalls.length, 3);
    assert.equal(h.decodeCalls[2].tier, "proxy");

    h.clock.advance(300);
    assert.equal(h.session.snapshot().loadingHint, true);

    await loadProxy(h, 2, proxySurface("A", 800));
    assert.equal(h.session.snapshot().tier, "proxy");
    assert.equal(h.decodeCalls.length, 4);
    assert.equal(h.decodeCalls[3].tier, "full");
    h.decodeCalls[3].pending.resolve(fullSurface("A"));
    await flush();
    assert.equal(h.session.snapshot().tier, "full");
    assert.deepEqual(h.session.snapshot().surface, { kind: "full", tag: "A" });
});

test("STATE-01：缓存代理长边覆盖当前代理目标才算命中，覆盖不足按未命中清图", async () => {
    const h = harness();
    const a = fakePhoto("A.JPG");
    h.session.show(a, { proxyLongEdge: 800 });
    await loadProxy(h, 0, proxySurface("A", 800));
    h.session.show(fakePhoto("B.JPG"), { proxyLongEdge: 800 });
    await loadProxy(h, 1, proxySurface("B", 800));

    h.session.show(a, { proxyLongEdge: 1600 });
    assert.equal(h.session.snapshot().surface, null, "代理覆盖不足仍命中缓存");
    assert.equal(h.decodeCalls.length, 3);
    assert.equal(h.decodeCalls[2].options.longEdge, 1600);

    await loadProxy(h, 2, proxySurface("A", 1600));
    h.session.show(fakePhoto("B.JPG"), { proxyLongEdge: 1600 });
    await loadProxy(h, 3, proxySurface("B", 1600));

    h.session.show(a, { proxyLongEdge: 1600 });
    assert.deepEqual(h.session.snapshot().surface, { kind: "proxy", tag: "A" }, "覆盖够用的代理未命中");
    assert.equal(h.session.snapshot().tier, "proxy");
    assert.equal(h.decodeCalls.length, 4);
});

test("STATE-01：代理被夹到源分辨率时按源分辨率命中", async () => {
    const h = harness();
    const a = fakePhoto("A.JPG");
    a.width = 900;
    a.height = 600;
    h.session.show(a, { proxyLongEdge: 4000 });
    await loadProxy(h, 0, proxySurface("A", 900));
    h.session.show(fakePhoto("B.JPG"), { proxyLongEdge: 4000 });
    await loadProxy(h, 1, proxySurface("B", 900));

    h.session.show(a, { proxyLongEdge: 4000 });
    assert.deepEqual(h.session.snapshot().surface, { kind: "proxy", tag: "A" }, "源分辨率产物未命中");
    assert.equal(h.decodeCalls.length, 2);
});

test("STATE-01：缓存命中 Full 的照片直接以 Full 上屏，不再解码", async () => {
    const h = harness();
    const a = fakePhoto("A.JPG");
    h.session.show(a, { wantFull: true });
    await loadProxy(h, 0, proxySurface("A"));
    h.decodeCalls[1].pending.resolve(fullSurface("A"));
    await flush();
    assert.equal(h.session.snapshot().tier, "full");

    h.session.show(fakePhoto("B.JPG"), { wantFull: false });
    await loadProxy(h, 2, proxySurface("B"));

    const mark = h.snapshots.length;
    h.session.show(a, { wantFull: true });
    assert.equal(h.session.snapshot().tier, "full");
    assert.deepEqual(h.session.snapshot().surface, { kind: "full", tag: "A" });
    assert.equal(h.decodeCalls.length, 3);
    assert.equal(h.clock.pendingTimers(), 0);
    assert.ok(h.snapshots.slice(mark).every((entry) => entry.surface !== null), "命中 Full 缓存期间出现空屏快照");
});

test("STATE-01：展示产物缓存按缓存上限淘汰，超出的照片回到黑屏加载", async () => {
    const h = harness({ cacheLimit: 1 });
    const a = fakePhoto("A.JPG");
    const b = fakePhoto("B.JPG");
    h.session.show(a);
    await loadProxy(h, 0, proxySurface("A"));
    h.session.show(b);
    await loadProxy(h, 1, proxySurface("B"));

    h.session.show(a);
    assert.equal(h.session.snapshot().surface, null, "被淘汰的照片仍命中缓存");
    assert.equal(h.session.snapshot().tier, "none");
    assert.equal(h.decodeCalls.length, 3);
    assert.equal(h.decodeCalls[2].photo.id, a.id);
    assert.deepEqual(h.session.cachedFullIds(), [], "只有代理产物不应标记为已缓存全分辨率");

    h.clock.advance(400);
    assert.equal(h.session.snapshot().loadingHint, true, "淘汰后未按黑屏加载显示提示");
});

test("STATE-01：加载超过 300 ms 显示提示，图一到即撤下", async () => {
    const h = harness();
    h.session.show(fakePhoto("A.JPG"));

    h.clock.advance(299);
    assert.equal(h.session.snapshot().loadingHint, false);
    h.clock.advance(1);
    assert.equal(h.session.snapshot().loadingHint, true);

    await loadProxy(h, 0, proxySurface("A"));
    assert.equal(h.session.snapshot().loadingHint, false);
    h.clock.advance(1000);
    assert.equal(h.session.snapshot().loadingHint, false);
});

test("OPEN-08：后发起的切图取代旧代际，慢结果不得覆盖新照片", async () => {
    const h = harness();
    const a = fakePhoto("A.JPG");
    const b = fakePhoto("B.JPG");

    h.session.show(a);
    h.session.show(b);
    assert.equal(h.decodeCalls[0].options.signal.aborted, true);

    await loadProxy(h, 1, proxySurface("B"));
    assert.deepEqual(h.session.snapshot().surface, { kind: "proxy", tag: "B" });

    await loadProxy(h, 0, proxySurface("A"));
    assert.equal(h.session.snapshot().photoId, b.id);
    assert.deepEqual(h.session.snapshot().surface, { kind: "proxy", tag: "B" });
});

test("OPEN-08：空态清空在途加载与提示，迟到结果不写入", async () => {
    const h = harness();
    h.session.show(fakePhoto("A.JPG"));
    h.session.show(null);

    const empty = h.session.snapshot();
    assert.equal(empty.photoId, null);
    assert.equal(empty.surface, null);
    assert.equal(empty.tier, "none");
    assert.equal(empty.error, null);
    assert.equal(empty.loadingHint, false);
    assert.equal(h.clock.pendingTimers(), 0);

    await loadProxy(h, 0, proxySurface("A"));
    assert.equal(h.session.snapshot().photoId, null);
    assert.equal(h.session.snapshot().surface, null);

    h.clock.advance(1000);
    assert.equal(h.session.snapshot().loadingHint, false);
});

test("STATE-02：代理解码失败显示标题与文件名，不残留图像、不显示加载提示", async () => {
    const h = harness();
    const a = fakePhoto("A.JPG");
    h.session.show(a);

    h.decodeCalls[0].pending.reject(new Error("decode failed"));
    await flush();

    const snapshot = h.session.snapshot();
    assert.deepEqual(snapshot.error, { title: "无法打开该图片", detail: "A.JPG" });
    assert.equal(snapshot.surface, null);
    assert.equal(snapshot.original, null);
    assert.equal(snapshot.tier, "none");
    assert.equal(snapshot.loadingHint, false);
    assert.equal(snapshot.toast, null);

    h.clock.advance(1000);
    assert.equal(h.session.snapshot().loadingHint, false);
    assert.equal(h.session.snapshot().error.title, "无法打开该图片");

    h.session.show(fakePhoto("B.JPG"));
    assert.equal(h.session.snapshot().error, null);
    assert.equal(h.session.snapshot().surface, null);
    assert.equal(h.decodeCalls.length, 2);
});

test("OPEN-06：同一照片重复 show 幂等，不重解码", async () => {
    const h = harness();
    h.session.show(fakePhoto("A.JPG"));
    await loadProxy(h, 0, proxySurface("A"));
    const surface = h.session.snapshot().surface;

    h.session.show(fakePhoto("A.JPG"));
    assert.equal(h.decodeCalls.length, 1);
    assert.deepEqual(h.session.snapshot().surface, surface);
    assert.equal(h.session.snapshot().photoId, "folder/A.JPG");
});

test("STATE-02/03：打开失败的提示条文案区分文件夹与文件，原因不可得时省略括号", () => {
    const h = harness();
    assert.equal(h.session.openFailed("图集", "folder"), "无法读取文件夹：图集");
    assert.equal(h.session.snapshot().toast.text, "无法读取文件夹：图集");
    const firstId = h.session.snapshot().toast.id;

    assert.equal(h.session.openFailed("A.JPG", "file"), "无法打开：A.JPG");
    assert.equal(h.session.snapshot().toast.text, "无法打开：A.JPG");
    assert.notEqual(h.session.snapshot().toast.id, firstId);

    assert.equal(h.session.openFailed("B.JPG", "file", "权限不足"), "无法打开：B.JPG（权限不足）");
    assert.equal(h.session.openFailed("图集", "folder", "目录已删除"), "无法读取文件夹：图集（目录已删除）");
});

test("STATE-03：提示条 4 s 自动消失，新提示替换旧提示并重置计时", () => {
    const h = harness();
    h.session.toast("第一条");
    assert.deepEqual(h.session.snapshot().toast.text, "第一条");

    h.clock.advance(3999);
    assert.equal(h.session.snapshot().toast.text, "第一条");
    h.clock.advance(1);
    assert.equal(h.session.snapshot().toast, null);

    h.session.toast("第二条");
    h.clock.advance(2000);
    h.session.toast("第三条");
    h.clock.advance(2000);
    assert.equal(h.session.snapshot().toast.text, "第三条");
    h.clock.advance(1999);
    assert.equal(h.session.snapshot().toast.text, "第三条");
    h.clock.advance(1);
    assert.equal(h.session.snapshot().toast, null);
});

test("STATE-03：药丸可见时提示条底距 60，否则回到 24", async () => {
    const h = harness();
    assert.equal(h.session.snapshot().toastBottom, 24);

    h.session.show(fakePhoto("A.JPG"));
    await loadProxy(h, 0, proxySurface("A"));
    h.session.setEnhance(true);
    h.clock.advance(500);
    assert.equal(h.session.snapshot().busy, true);
    assert.equal(h.session.snapshot().toastBottom, 60);

    h.enhanceCalls[0].pending.resolve({ kind: "enhanced", tag: "A" });
    await flush();
    assert.equal(h.session.snapshot().busy, false);
    assert.equal(h.session.snapshot().toastBottom, 24);
});

test("IMAGE-03：先代理后 Full，Full 就绪原地热替换", async () => {
    const h = harness();
    h.session.show(fakePhoto("A.JPG"), { proxyLongEdge: 1600, wantFull: true });

    assert.equal(h.decodeCalls.length, 1);
    assert.equal(h.decodeCalls[0].tier, "proxy");
    assert.equal(h.decodeCalls[0].options.longEdge, 1600);

    await loadProxy(h, 0, proxySurface("A"));
    assert.equal(h.session.snapshot().tier, "proxy");
    assert.equal(h.decodeCalls.length, 2);
    assert.equal(h.decodeCalls[1].tier, "full");

    h.decodeCalls[1].pending.resolve(fullSurface("A"));
    await flush();
    const snapshot = h.session.snapshot();
    assert.equal(snapshot.tier, "full");
    assert.deepEqual(snapshot.surface, { kind: "full", tag: "A" });
    assert.deepEqual(snapshot.original, { kind: "full", tag: "A" });
    assert.equal(snapshot.error, null);

    h.session.setProxyLongEdge(4000);
    assert.equal(h.decodeCalls.length, 2);
});

test("IMAGE-03：适应态不主动升 Full，setWantFull 升级幂等", async () => {
    const h = harness();
    h.session.show(fakePhoto("A.JPG"));
    await loadProxy(h, 0, proxySurface("A"));
    assert.equal(h.decodeCalls.length, 1);

    h.session.setWantFull(false);
    assert.equal(h.decodeCalls.length, 1);

    h.session.setWantFull(true);
    assert.equal(h.decodeCalls.length, 2);
    assert.equal(h.decodeCalls[1].tier, "full");
    h.session.setWantFull(true);
    assert.equal(h.decodeCalls.length, 2);

    h.decodeCalls[1].pending.resolve(fullSurface("A"));
    await flush();
    assert.equal(h.session.snapshot().tier, "full");
});

test("IMAGE-03：Full 失败保留代理并提示、不报 error，下一次触发重试成功", async () => {
    const h = harness();
    h.session.show(fakePhoto("A.JPG"), { wantFull: true });
    await loadProxy(h, 0, proxySurface("A"));
    assert.equal(h.decodeCalls[1].tier, "full");

    h.decodeCalls[1].pending.reject(new Error("full failed"));
    await flush();
    let snapshot = h.session.snapshot();
    assert.equal(snapshot.tier, "proxy");
    assert.deepEqual(snapshot.surface, { kind: "proxy", tag: "A" });
    assert.equal(snapshot.error, null);
    assert.equal(snapshot.toast.text, "无法载入全分辨率：A.JPG");

    h.session.setWantFull(true);
    assert.equal(h.decodeCalls.length, 3);
    assert.equal(h.decodeCalls[2].tier, "full");
    h.decodeCalls[2].pending.resolve(fullSurface("A"));
    await flush();
    snapshot = h.session.snapshot();
    assert.equal(snapshot.tier, "full");
    assert.deepEqual(snapshot.surface, { kind: "full", tag: "A" });
});

test("IMAGE-03：视口变大重出更大代理，旧图维持到新图到达", async () => {
    const h = harness();
    h.session.show(fakePhoto("A.JPG"), { proxyLongEdge: 800 });
    await loadProxy(h, 0, proxySurface("A", 800));

    h.session.setProxyLongEdge(1600);
    assert.equal(h.decodeCalls.length, 2);
    assert.equal(h.decodeCalls[1].options.longEdge, 1600);
    assert.deepEqual(h.session.snapshot().surface, { kind: "proxy", tag: "A" });

    await loadProxy(h, 1, proxySurface("A2", 1600));
    assert.deepEqual(h.session.snapshot().surface, { kind: "proxy", tag: "A2" });
    assert.equal(h.session.snapshot().tier, "proxy");

    h.session.setProxyLongEdge(1200);
    assert.equal(h.decodeCalls.length, 2);

    const pending = harness();
    pending.session.show(fakePhoto("C.JPG"), { proxyLongEdge: 800 });
    pending.session.setProxyLongEdge(1600);
    assert.equal(pending.decodeCalls[0].options.signal.aborted, true);
    assert.equal(pending.decodeCalls[1].options.longEdge, 1600);
});

test("IMAGE-07 / MENU-07：无照片时雾天增强不可开", () => {
    const h = harness();
    assert.equal(h.session.setEnhance(true), false);
    assert.equal(h.session.snapshot().enhance, false);

    h.clock.advance(500);
    assert.equal(h.session.snapshot().busy, false);
});

test("IMAGE-07：400 ms 内完成不显示药丸", async () => {
    const h = harness();
    h.session.show(fakePhoto("A.JPG"));
    await loadProxy(h, 0, proxySurface("A"));

    assert.equal(h.session.setEnhance(true), true);
    assert.deepEqual(h.session.snapshot().surface, { kind: "proxy", tag: "A" });

    h.clock.advance(400);
    assert.equal(h.session.snapshot().busy, false);
    h.enhanceCalls[0].pending.resolve({ kind: "enhanced", tag: "A" });
    await flush();

    assert.equal(h.session.snapshot().busy, false);
    assert.equal(h.snapshots.some((snapshot) => snapshot.busy), false);
    assert.deepEqual(h.session.snapshot().surface, { kind: "enhanced", tag: "A" });
    assert.deepEqual(h.session.snapshot().original, { kind: "proxy", tag: "A" });
});

test("IMAGE-07：600 ms 完成显示药丸后收起，雾天增强图原地替换", async () => {
    const h = harness();
    h.session.show(fakePhoto("A.JPG"));
    await loadProxy(h, 0, proxySurface("A"));
    h.session.setEnhance(true);

    h.clock.advance(400);
    assert.equal(h.session.snapshot().busy, false);
    h.clock.advance(200);
    assert.equal(h.session.snapshot().busy, true);
    assert.equal(h.session.snapshot().toastBottom, 60);

    h.enhanceCalls[0].pending.resolve({ kind: "enhanced", tag: "A" });
    await flush();
    assert.equal(h.session.snapshot().busy, false);
    assert.equal(h.session.snapshot().enhance, true);
    assert.deepEqual(h.session.snapshot().surface, { kind: "enhanced", tag: "A" });
});

test("IMAGE-07：雾天增强跨切图持续生效，新产物先显示原片再重算", async () => {
    const h = harness();
    h.session.show(fakePhoto("A.JPG"));
    await loadProxy(h, 0, proxySurface("A"));
    h.session.setEnhance(true);
    h.enhanceCalls[0].pending.resolve({ kind: "enhanced", tag: "A" });
    await flush();
    assert.deepEqual(h.session.snapshot().surface, { kind: "enhanced", tag: "A" });

    h.session.show(fakePhoto("B.JPG"));
    assert.equal(h.session.snapshot().enhance, true);
    await loadProxy(h, 1, proxySurface("B"));
    assert.equal(h.enhanceCalls.length, 2);
    assert.deepEqual(h.session.snapshot().surface, { kind: "proxy", tag: "B" });

    h.enhanceCalls[1].pending.resolve({ kind: "enhanced", tag: "B" });
    await flush();
    assert.deepEqual(h.session.snapshot().surface, { kind: "enhanced", tag: "B" });
    assert.deepEqual(h.session.snapshot().original, { kind: "proxy", tag: "B" });
});

test("IMAGE-07：代理→Full 后自动重算，重算期间药丸保持不闪", async () => {
    const h = harness();
    h.session.show(fakePhoto("A.JPG"));
    await loadProxy(h, 0, proxySurface("A"));
    h.session.setEnhance(true);
    h.clock.advance(600);
    assert.equal(h.session.snapshot().busy, true);

    h.session.setWantFull(true);
    h.decodeCalls[1].pending.resolve(fullSurface("A"));
    await flush();
    assert.equal(h.session.snapshot().tier, "full");
    assert.equal(h.session.snapshot().busy, true);
    assert.deepEqual(h.session.snapshot().surface, { kind: "full", tag: "A" });
    assert.equal(h.enhanceCalls.length, 2);

    h.enhanceCalls[0].pending.resolve({ kind: "enhanced", tag: "stale" });
    await flush();
    assert.deepEqual(h.session.snapshot().surface, { kind: "full", tag: "A" });

    h.enhanceCalls[1].pending.resolve({ kind: "enhanced", tag: "A-full" });
    await flush();
    assert.equal(h.session.snapshot().busy, false);
    assert.deepEqual(h.session.snapshot().surface, { kind: "enhanced", tag: "A-full" });
    assert.deepEqual(h.session.snapshot().original, { kind: "full", tag: "A" });
});

test("IMAGE-07：关闭雾天增强立即还原原片并取消在途计算", async () => {
    const h = harness();
    h.session.show(fakePhoto("A.JPG"));
    await loadProxy(h, 0, proxySurface("A"));
    h.session.setEnhance(true);
    h.clock.advance(600);
    assert.equal(h.session.snapshot().busy, true);

    assert.equal(h.session.setEnhance(false), false);
    assert.equal(h.session.snapshot().enhance, false);
    assert.equal(h.session.snapshot().busy, false);
    assert.deepEqual(h.session.snapshot().surface, { kind: "proxy", tag: "A" });

    h.enhanceCalls[0].pending.resolve({ kind: "enhanced", tag: "late" });
    await flush();
    assert.deepEqual(h.session.snapshot().surface, { kind: "proxy", tag: "A" });
});

test("IMAGE-07：内存压力自动关闭雾天增强并只提示一次", async () => {
    const h = harness();
    h.session.show(fakePhoto("A.JPG"));
    await loadProxy(h, 0, proxySurface("A"));
    h.session.setEnhance(true);
    h.enhanceCalls[0].pending.resolve({ kind: "enhanced", tag: "A" });
    await flush();
    assert.deepEqual(h.session.snapshot().surface, { kind: "enhanced", tag: "A" });

    assert.equal(h.session.memoryWarning(), true);
    assert.equal(h.session.snapshot().enhance, false);
    assert.deepEqual(h.session.snapshot().surface, { kind: "proxy", tag: "A" });
    assert.equal(h.session.snapshot().toast.text, "内存不足，已关闭雾天增强");
    const toastId = h.session.snapshot().toast.id;

    assert.equal(h.session.memoryWarning(), false);
    assert.equal(h.session.snapshot().toast.id, toastId);
});

test("IMAGE-07：雾天增强失败收起药丸、保持原片且不自动重试", async () => {
    const h = harness();
    h.session.show(fakePhoto("A.JPG"));
    await loadProxy(h, 0, proxySurface("A"));
    h.session.setEnhance(true);
    h.clock.advance(600);
    assert.equal(h.session.snapshot().busy, true);

    h.enhanceCalls[0].pending.reject(new Error("enhance failed"));
    await flush();
    assert.equal(h.session.snapshot().busy, false);
    assert.equal(h.session.snapshot().enhance, true);
    assert.deepEqual(h.session.snapshot().surface, { kind: "proxy", tag: "A" });
    assert.equal(h.enhanceCalls.length, 1);
});

test("FILM-06：写回成功前不改星级，成功后同步代表与伴侣", async () => {
    const clock = manualClock();
    const toasts = [];
    const writer = new RatingWriter({ clock, onToast: (text) => toasts.push(text) });
    const card = fakeCard("A.JPG", ["A.ARW"]);
    const result = writer.write(card, 4);

    assert.equal(card.photo.rating, 0);
    clock.advance(119);
    await flush();
    assert.equal(card.photo.rating, 0);

    clock.advance(1);
    await flush();
    assert.equal(card.photo.rating, 0);
    assert.equal(card.companions[0].rating, 0);

    clock.advance(120);
    await flush();
    assert.equal(await result, true);
    assert.equal(card.photo.rating, 4);
    assert.equal(card.companions[0].rating, 4);
    assert.deepEqual(toasts, []);
});

test("FILM-06：伴侣写回失败时星级保持原值并提示失败文件", async () => {
    const clock = manualClock();
    const toasts = [];
    const writer = new RatingWriter({ clock, onToast: (text) => toasts.push(text) });
    const card = fakeCard("A.JPG", ["A.ARW"]);
    card.photo.rating = 2;
    card.companions[0].rating = 2;
    card.companions[0].fault = { write: true };

    const result = writer.write(card, 3);
    clock.advance(120);
    await flush();
    clock.advance(120);
    await flush();

    assert.equal(await result, false);
    assert.equal(card.photo.rating, 2);
    assert.equal(card.companions[0].rating, 2);
    assert.deepEqual(toasts, ["无法写入星级：A.ARW（文件只读）"]);
});

test("FILM-06：代表写回失败即停止，不继续写伴侣", async () => {
    const clock = manualClock();
    const toasts = [];
    const writer = new RatingWriter({ clock, onToast: (text) => toasts.push(text) });
    const card = fakeCard("A.JPG", ["A.ARW"]);
    card.photo.fault = { write: true };

    const result = writer.write(card, 5);
    clock.advance(120);
    await flush();

    assert.equal(await result, false);
    assert.equal(clock.pendingTimers(), 0);
    assert.equal(card.photo.rating, 0);
    assert.deepEqual(toasts, ["无法写入星级：A.JPG（文件只读）"]);
});

test("FILM-06：写回期间第二次写入排队，按最后一次为准", async () => {
    const clock = manualClock();
    const writer = new RatingWriter({ clock });
    const card = fakeCard("A.JPG");

    const first = writer.write(card, 2);
    clock.advance(60);
    await flush();
    const second = writer.write(card, 5);
    assert.equal(card.photo.rating, 0);

    clock.advance(60);
    await flush();
    assert.equal(card.photo.rating, 0);

    clock.advance(120);
    await flush();
    assert.equal(await first, true);
    assert.equal(await second, true);
    assert.equal(card.photo.rating, 5);
});

test("TOOLBAR-03 / SCOPE-05：只写评分，0 星同样回写代表与伴侣", async () => {
    const clock = manualClock();
    const writer = new RatingWriter({ clock, latency: 10 });
    const card = fakeCard("B.JPG", ["B.ARW"]);
    card.photo.rating = 3;
    card.companions[0].rating = 3;

    const result = writer.write(card, 0);
    clock.advance(10);
    await flush();
    clock.advance(10);
    await flush();

    assert.equal(await result, true);
    assert.equal(card.photo.rating, 0);
    assert.equal(card.companions[0].rating, 0);
});
