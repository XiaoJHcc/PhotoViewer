import { test } from "node:test";
import assert from "node:assert/strict";
import { manualClock } from "../src/core/clock.js";
import { createTokens } from "../src/core/tokens.js";
import { featuresFor } from "../src/core/features.js";
import {
    displayDimensions,
    orientationMatrix,
    rectToDisplay,
    rectToStored,
    focusInStored,
    tileRegion,
    sceneParams,
    drawScene,
    drawTile,
} from "../src/render/procedural.js";
import { createDecoder, proxyDimensions } from "../src/render/decoder.js";
import { createStore } from "../src/render/store.js";
import { createThumbnailQueue } from "../src/render/thumbs.js";

const t = createTokens();

function fakePhoto(overrides = {}) {
    return {
        id: "dev/DSC0001.ARW",
        folderId: "dev",
        name: "DSC0001.ARW",
        stem: "DSC0001",
        ext: ".arw",
        width: 6000,
        height: 4000,
        orientation: 1,
        focus: { x: 0.5, y: 0.5, w: 0.12, h: 0.1 },
        shake: false,
        fault: {},
        extracted: true,
        fingerprint: "dev:DSC0001:1",
        scene: { base: 101, variant: 0 },
        ...overrides,
    };
}

function flush() {
    return new Promise((resolve) => setImmediate(resolve));
}

function approx(actual, expected, epsilon = 1e-9) {
    assert.ok(Math.abs(actual - expected) <= epsilon, `${actual} ≉ ${expected}`);
}

function applyMatrix(m, x, y) {
    return { x: m.a * x + m.c * y + m.e, y: m.b * x + m.d * y + m.f };
}

function fakeContext() {
    const gradient = () => ({ addColorStop() {} });
    const calls = [];
    const record = (name) => (...args) => calls.push({ name, args });
    return {
        calls,
        fillStyle: "#000",
        strokeStyle: "#000",
        lineWidth: 1,
        lineCap: "butt",
        globalAlpha: 1,
        font: "",
        textAlign: "left",
        textBaseline: "alphabetic",
        filter: "none",
        save: record("save"),
        restore: record("restore"),
        beginPath: record("beginPath"),
        closePath: record("closePath"),
        rect: record("rect"),
        clip: record("clip"),
        scale: record("scale"),
        translate: record("translate"),
        rotate: record("rotate"),
        setTransform: record("setTransform"),
        fillRect: record("fillRect"),
        strokeRect: record("strokeRect"),
        moveTo: record("moveTo"),
        lineTo: record("lineTo"),
        arc: record("arc"),
        ellipse: record("ellipse"),
        fill: record("fill"),
        stroke: record("stroke"),
        fillText: record("fillText"),
        drawImage: record("drawImage"),
        createLinearGradient: () => gradient(),
        createRadialGradient: () => gradient(),
    };
}

function namesOf(calls) {
    return calls.map((call) => call.name);
}

test("IMAGE-08：八个方向码的显示尺寸与变换矩阵", () => {
    for (const code of [1, 2, 3, 4]) {
        assert.deepEqual(displayDimensions(6000, 4000, code), { width: 6000, height: 4000 });
    }
    for (const code of [5, 6, 7, 8]) {
        assert.deepEqual(displayDimensions(6000, 4000, code), { width: 4000, height: 6000 });
    }
    assert.deepEqual(displayDimensions(4000, 6000, 1), { width: 4000, height: 6000 });
    assert.deepEqual(displayDimensions(6000, 4000, 0), { width: 6000, height: 4000 });

    const w = 6000;
    const h = 4000;
    const expected = {
        1: [[0, 0], [w, 0], [0, h], [w, h]],
        2: [[w, 0], [0, 0], [w, h], [0, h]],
        3: [[w, h], [0, h], [w, 0], [0, 0]],
        4: [[0, h], [w, h], [0, 0], [w, 0]],
        5: [[0, 0], [0, w], [h, 0], [h, w]],
        6: [[h, 0], [h, w], [0, 0], [0, w]],
        7: [[h, w], [h, 0], [0, w], [0, 0]],
        8: [[0, w], [0, 0], [h, w], [h, 0]],
    };
    const corners = [[0, 0], [w, 0], [0, h], [w, h]];
    for (let code = 1; code <= 8; code++) {
        const matrix = orientationMatrix(code, w, h);
        const dims = displayDimensions(w, h, code);
        assert.equal(matrix.width, dims.width);
        assert.equal(matrix.height, dims.height);
        corners.forEach(([x, y], index) => {
            const point = applyMatrix(matrix, x, y);
            approx(point.x, expected[code][index][0], 1e-6);
            approx(point.y, expected[code][index][1], 1e-6);
        });
    }
});

test("IMAGE-08：对焦框在显示朝向与存储朝向间往返，方向 ≥5 换轴", () => {
    const rect = { x: 0.3, y: 0.7, w: 0.12, h: 0.2 };
    for (let code = 1; code <= 8; code++) {
        const stored = rectToStored(rect, code);
        const back = rectToDisplay(stored, code);
        approx(back.x, rect.x);
        approx(back.y, rect.y);
        approx(back.w, rect.w);
        approx(back.h, rect.h);
        if (code >= 5) {
            approx(stored.w, rect.h);
            approx(stored.h, rect.w);
        } else {
            approx(stored.w, rect.w);
            approx(stored.h, rect.h);
        }
    }

    const photo = fakePhoto({ orientation: 6, focus: { x: 0.25, y: 0.4, w: 0.2, h: 0.1 } });
    const stored = focusInStored(photo);
    approx(stored.x, 0.4);
    approx(stored.y, 0.75);
    approx(stored.w, 0.1);
    approx(stored.h, 0.2);
    assert.equal(focusInStored(fakePhoto({ focus: null })), null);
    assert.equal(focusInStored(fakePhoto({ focus: { x: 0.5, y: 0.5, w: 0, h: 0.1 } })), null);
    assert.equal(focusInStored(fakePhoto({ focus: { x: 0.5, y: 0.5, w: 0.1 } })), null);
});

test("IMAGE-03：代理长边取 min(请求, 源长边) 并保持比例", () => {
    const landscape = fakePhoto();
    assert.deepEqual(proxyDimensions(landscape, 1600), { width: 1600, height: 1067 });
    assert.deepEqual(proxyDimensions(landscape, 9000), { width: 6000, height: 4000 });
    assert.deepEqual(proxyDimensions(landscape, 0), { width: 6000, height: 4000 });
    const portrait = fakePhoto({ width: 4000, height: 6000 });
    assert.deepEqual(proxyDimensions(portrait, 1600), { width: 1067, height: 1600 });
    const small = fakePhoto({ width: 1200, height: 800 });
    assert.deepEqual(proxyDimensions(small, 1600), { width: 1200, height: 800 });
});

test("IMAGE-01：源像素子矩形被夹进源尺寸内", () => {
    const photo = fakePhoto();
    assert.deepEqual(tileRegion(photo, null), { x: 0, y: 0, width: 6000, height: 4000 });
    assert.deepEqual(
        tileRegion(photo, { x: 5900, y: 3900, width: 500, height: 500 }),
        { x: 5900, y: 3900, width: 100, height: 100 },
    );
    assert.deepEqual(
        tileRegion(photo, { x: -20, y: -30, width: 300.4, height: 200.6 }),
        { x: 0, y: 0, width: 300, height: 201 },
    );
    assert.deepEqual(
        tileRegion(photo, { x: 100, y: 100, width: 0, height: 0 }),
        { x: 100, y: 100, width: 1, height: 1 },
    );
});

test("IMAGE-01：子矩形按输出尺寸缩放，裁剪固定在输出矩形内", () => {
    const ctx = fakeContext();
    const result = drawTile(ctx, fakePhoto(), 1000, 500, 600, 400, 300, 200);
    assert.deepEqual(result, { x: 1000, y: 500, width: 600, height: 400, outWidth: 300, outHeight: 200 });
    const clip = ctx.calls.find((call) => call.name === "rect");
    assert.deepEqual(clip.args, [0, 0, 300, 200]);
    const scale = ctx.calls.find((call) => call.name === "scale");
    assert.deepEqual(scale.args, [0.5, 0.5]);
    const translate = ctx.calls.find((call) => call.name === "translate");
    assert.deepEqual(translate.args, [-1000, -500]);
});

test("IMAGE-01：代理与 Full 发出完全相同的源坐标绘制，只差输出缩放", () => {
    const photo = fakePhoto();
    const proxy = fakeContext();
    const full = fakeContext();
    drawTile(proxy, photo, 0, 0, photo.width, photo.height, 1600, 1067);
    drawTile(full, photo, 0, 0, photo.width, photo.height, 6000, 4000);

    const geometry = (calls) => calls.filter((call) => !["save", "restore", "rect", "clip", "scale", "translate", "setTransform"].includes(call.name));
    assert.deepEqual(geometry(proxy.calls), geometry(full.calls));

    const proxyScale = proxy.calls.find((call) => call.name === "scale").args;
    const fullScale = full.calls.find((call) => call.name === "scale").args;
    approx(proxyScale[0], 1600 / 6000);
    assert.deepEqual(fullScale, [1, 1]);
});

test("IMAGE-01：场景参数确定，variant 只带来微小位移与亮度变化", () => {
    const photo = fakePhoto();
    const first = sceneParams(photo);
    const second = sceneParams(fakePhoto());
    assert.deepEqual(first, second);
    assert.ok(first.mountains[0].points.length > 0);

    const variant = sceneParams(fakePhoto({ scene: { base: 101, variant: 3 } }));
    assert.equal(variant.theme, first.theme);
    assert.equal(variant.chart.x, first.chart.x);
    assert.notDeepEqual(variant.shift, first.shift);
    assert.ok(variant.dim > first.dim);

    const other = sceneParams(fakePhoto({ scene: { base: 104, variant: 0 } }));
    assert.notDeepEqual(other.mountains, first.mountains);
});

test("IMAGE-08：方向标记与主体被绘制，shake 只增加涂抹次数", () => {
    const clean = fakeContext();
    drawScene(clean, fakePhoto({ orientation: 6 }), { width: 800, height: 533 });
    const cleanTexts = clean.calls.filter((call) => call.name === "fillText").map((call) => call.args[0]);
    assert.ok(cleanTexts.includes("F"));
    assert.ok(cleanTexts.includes("FOCUS"));
    assert.ok(namesOf(clean.calls).includes("clip"));

    const shaken = fakeContext();
    drawScene(shaken, fakePhoto({ shake: true }), { width: 400, height: 267 });
    const cleanAlpha = clean.calls.filter((call) => call.name === "save").length;
    const shakenAlpha = shaken.calls.filter((call) => call.name === "save").length;
    assert.ok(shakenAlpha > cleanAlpha, "抖动照片应多次绘制同一场景");
});

test("IMAGE-08：对焦点主体落在存储朝向的正确位置", () => {
    const photo = fakePhoto({ orientation: 6, focus: { x: 0.25, y: 0.4, w: 0.12, h: 0.1 } });
    const params = sceneParams(photo);
    approx(params.subject.x, 0.4);
    approx(params.subject.y, 0.75);
    const portrait = fakePhoto({ orientation: 1, focus: { x: 0.25, y: 0.4, w: 0.12, h: 0.1 } });
    approx(sceneParams(portrait).subject.x, 0.25);
});

test("IMAGE-03：解码器按配置延迟产出代理与 Full，尺寸来自源尺寸", async () => {
    const clock = manualClock();
    const surfaces = [];
    const decoder = createDecoder({
        clock,
        latency: { proxy: 80, full: 300, enhance: 400 },
        createSurface({ width, height, tier }) {
            const surface = { kind: "surface", tier, width, height };
            surfaces.push(surface);
            return surface;
        },
    });
    let proxySettled = false;
    const proxy = decoder.decode(fakePhoto(), "proxy", { longEdge: 1600 });
    proxy.then(() => { proxySettled = true; });
    clock.advance(79);
    await flush();
    assert.equal(proxySettled, false);
    clock.advance(1);
    const result = await proxy;
    assert.deepEqual(result, { tier: "proxy", width: 1600, height: 1067, surface: surfaces[0] });
    assert.equal(surfaces[0].tier, "proxy");

    const full = decoder.decode(fakePhoto({ width: 4000, height: 6000 }), "full");
    clock.advance(300);
    const fullResult = await full;
    assert.equal(fullResult.tier, "full");
    assert.equal(fullResult.width, 4000);
    assert.equal(fullResult.height, 6000);
});

test("IMAGE-03：解码故障注入 all 两档皆失败、full 只失败 Full", async () => {
    const clock = manualClock();
    const decoder = createDecoder({
        clock,
        latency: 10,
        createSurface: ({ width, height, tier }) => ({ width, height, tier }),
    });
    await assert.rejects(decoder.decode(fakePhoto({ fault: { decode: "all" } }), "proxy"), /无法解码/);
    await assert.rejects(decoder.decode(fakePhoto({ fault: { decode: "all" } }), "full"), /无法载入全分辨率/);
    await assert.rejects(decoder.decode(fakePhoto({ fault: { decode: "full" } }), "full"), /无法载入全分辨率/);
    const proxy = decoder.decode(fakePhoto({ fault: { decode: "full" } }), "proxy", { longEdge: 800 });
    clock.advance(10);
    const result = await proxy;
    assert.equal(result.width, 800);
});

test("IMAGE-03：解码与增强支持取消，取消后不产出 surface", async () => {
    const clock = manualClock();
    let created = 0;
    const decoder = createDecoder({
        clock,
        latency: { proxy: 100, enhance: 100 },
        createSurface: ({ width, height, tier }) => {
            created += 1;
            return { width, height, tier };
        },
        enhanceSurface: (surface) => ({ ...surface, enhanced: true }),
    });
    const controller = new AbortController();
    const pending = decoder.decode(fakePhoto(), "proxy", { signal: controller.signal });
    controller.abort();
    clock.advance(100);
    await assert.rejects(pending, (error) => error.name === "AbortError");
    assert.equal(created, 0);

    const surface = { width: 100, height: 80 };
    const second = new AbortController();
    const enhance = decoder.enhance(surface, { signal: second.signal });
    second.abort();
    clock.advance(100);
    await assert.rejects(enhance, (error) => error.name === "AbortError");
});

test("IMAGE-07：增强默认延迟 300–800ms，成功后返回新 surface", async () => {
    const clock = manualClock();
    const decoder = createDecoder({
        clock,
        latency: { enhance: [300, 800] },
        createSurface: ({ width, height, tier }) => ({ width, height, tier }),
        enhanceSurface: (surface) => ({ ...surface, enhanced: true }),
    });
    const surface = { width: 640, height: 427 };
    const pending = decoder.enhance(surface);
    clock.advance(299);
    await flush();
    let settled = false;
    pending.then(() => { settled = true; });
    await flush();
    assert.equal(settled, false);
    clock.advance(501);
    const enhanced = await pending;
    assert.equal(enhanced.enhanced, true);
    assert.equal(enhanced.width, 640);
});

test("SCOPE-02：只读库只在已提取且有指纹时返回特征", async () => {
    const clock = manualClock();
    const store = createStore({ clock, readLatency: 40 });
    const photo = fakePhoto();

    const pending = store.read(photo, {});
    let settled = false;
    pending.then(() => { settled = true; });
    clock.advance(39);
    await flush();
    assert.equal(settled, false);
    clock.advance(1);
    const features = await pending;
    assert.equal(features, featuresFor(photo));

    const notExtracted = fakePhoto({ id: "dev/A.ARW", extracted: false });
    const missingFingerprint = fakePhoto({ id: "dev/B.ARW", fingerprint: null });
    const a = store.read(notExtracted, {});
    const b = store.read(missingFingerprint, {});
    clock.advance(40);
    assert.equal(await a, null);
    assert.equal(await b, null);
});

test("SCOPE-02：同一照片的并发现算合并为一次，结果对象相同", async () => {
    const clock = manualClock();
    const store = createStore({ clock, computeLatency: 700 });
    const photo = fakePhoto({ extracted: false, fingerprint: null });
    const first = store.compute(photo, { variant: "original" });
    const second = store.compute(photo, { variant: "original" });
    clock.advance(699);
    await flush();
    clock.advance(1);
    const [a, b] = await Promise.all([first, second]);
    assert.equal(a, b);
    const cached = await store.compute(photo, { variant: "original" });
    assert.equal(cached, a);
});

test("SCOPE-02：共享现算中一个调用取消，不影响另一个调用", async () => {
    const clock = manualClock();
    const store = createStore({ clock, computeLatency: 600 });
    const photo = fakePhoto({ fingerprint: null, scene: { base: 88, variant: 0 } });
    const controller = new AbortController();
    const first = store.compute(photo, { signal: controller.signal, variant: "original" });
    const rejected = assert.rejects(first, (error) => error.name === "AbortError");
    const second = store.compute(photo, { variant: "original" });
    controller.abort();
    await flush();
    clock.advance(600);
    const value = await second;
    await rejected;
    assert.ok(value && value.cls);
    assert.equal(store.stats().computed, 1);
});

test("SCOPE-03：清进程缓存后重新走现算路径", async () => {
    const clock = manualClock();
    const store = createStore({ clock, computeLatency: 600 });
    const photo = fakePhoto({ fingerprint: null, scene: { base: 89, variant: 0 } });
    const first = store.compute(photo, { variant: "original" });
    clock.advance(600);
    const base = await first;
    store.clear();
    assert.deepEqual(store.stats(), { computed: 0, computing: 0 });
    const again = store.compute(photo, { variant: "original" });
    clock.advance(600);
    assert.equal(await again, base);
    assert.equal(store.stats().computed, 1);
});

test("SCOPE-03：现算不改 extracted，增强变体只提升锐度", async () => {
    const clock = manualClock();
    const store = createStore({ clock, computeLatency: 600 });
    const photo = fakePhoto({ extracted: false });
    const original = store.compute(photo, { variant: "original" });
    clock.advance(600);
    const base = await original;
    assert.equal(photo.extracted, false);

    const enhancedPromise = store.compute(photo, { variant: "enhanced" });
    clock.advance(600);
    const enhanced = await enhancedPromise;
    assert.notEqual(enhanced, base);
    assert.equal(enhanced.cls, base.cls);
    assert.equal(photo.extracted, false);
    let baseSum = 0;
    let enhancedSum = 0;
    for (let i = 0; i < base.sharpness.length; i++) {
        baseSum += base.sharpness[i];
        enhancedSum += enhanced.sharpness[i];
    }
    assert.ok(enhancedSum > baseSum);
    assert.ok(enhanced.sharpness[0] <= 1);
});

test("SCOPE-02：现算支持取消，取消后不写缓存", async () => {
    const clock = manualClock();
    const store = createStore({ clock, computeLatency: 600 });
    const photo = fakePhoto({ fingerprint: null, scene: { base: 77, variant: 0 } });
    const controller = new AbortController();
    const pending = store.compute(photo, { signal: controller.signal, variant: "original" });
    const checked = assert.rejects(pending, (error) => error.name === "AbortError");
    controller.abort();
    await flush();
    clock.advance(600);
    await checked;
    assert.equal(store.stats().computed, 0);
});

test("SCOPE-03：批量提取每组成功后置 extracted，failNext 注入失败", async () => {
    const clock = manualClock();
    const store = createStore({ clock, extractLatency: 300 });
    const first = fakePhoto({ id: "dev/C.ARW", extracted: false });
    const second = fakePhoto({ id: "dev/D.ARW", extracted: false });
    const pending = store.extractGroup([first, second]);
    clock.advance(299);
    assert.equal(first.extracted, false);
    clock.advance(1);
    await pending;
    assert.equal(first.extracted, true);
    assert.equal(second.extracted, true);

    store.failNext = 1;
    const third = fakePhoto({ id: "dev/E.ARW", extracted: false });
    const failing = store.extractGroup([third]);
    clock.advance(300);
    await assert.rejects(failing, /特征提取失败/);
    assert.equal(third.extracted, false);
    assert.equal(store.failNext, 0);

    const retry = store.extractGroup([third]);
    clock.advance(300);
    await retry;
    assert.equal(third.extracted, true);
});

test("FILM-05：缩略图长边 = film.thumb.size × dpr，按显示朝向换轴", async () => {
    const clock = manualClock();
    const calls = [];
    const decoder = {
        decode(photo, tier, options) {
            calls.push({ photo, tier, options });
            const dims = proxyDimensions(photo, options.longEdge);
            return Promise.resolve({ tier, ...dims, surface: { id: photo.id } });
        },
    };
    const queue = createThumbnailQueue({
        t,
        dpr: 2,
        decoder,
        clock,
        orient: (surface, orientation, width, height) => ({
            ...surface,
            orientation,
            display: displayDimensions(width, height, orientation),
        }),
    });
    const landscape = await queue.request(fakePhoto());
    assert.equal(calls[0].options.longEdge, t("film.thumb.size") * 2);
    assert.equal(calls[0].tier, "proxy");
    assert.deepEqual(landscape.display, { width: 160, height: 107 });

    const portrait = await queue.request(fakePhoto({
        id: "dev/DSC0002.ARW",
        orientation: 6,
        width: 6000,
        height: 4000,
        fingerprint: "dev:2",
    }));
    assert.deepEqual(portrait.display, { width: 107, height: 160 });
});

test("FILM-02：可见项在空出的并发位优先开始，同时并发 ≤ 4", async () => {
    const clock = manualClock();
    const started = [];
    const calls = [];
    const decoder = {
        decode(photo, tier, options) {
            const dims = proxyDimensions(photo, options.longEdge);
            let resolve;
            const promise = new Promise((res) => { resolve = res; });
            calls.push({ photo, resolve });
            started.push(photo.id);
            return promise.then(() => ({ tier, ...dims, surface: { id: photo.id } }));
        },
    };
    const queue = createThumbnailQueue({
        size: 160,
        dpr: 1,
        decoder,
        clock,
        concurrency: 4,
        orient: (surface) => surface,
    });
    const photos = [];
    for (let i = 1; i <= 6; i++) {
        photos.push(fakePhoto({ id: `dev/F${i}.ARW`, fingerprint: `dev:f${i}`, scene: { base: 300 + i, variant: 0 } }));
    }
    const requests = photos.map((photo) => queue.request(photo, false));
    const visible = queue.request(fakePhoto({
        id: "dev/VISIBLE.ARW",
        fingerprint: "dev:visible",
        scene: { base: 401, variant: 0 },
    }), true);
    assert.equal(queue.stats().running, 4);
    assert.deepEqual(started.slice(0, 4), photos.slice(0, 4).map((photo) => photo.id));

    calls[0].resolve();
    await flush();
    assert.equal(started[4], "dev/VISIBLE.ARW");
    assert.equal(queue.stats().running, 4);

    for (let round = 0; round < 10; round++) {
        for (const call of calls) {
            if (call.released) continue;
            call.released = true;
            call.resolve();
        }
        await flush();
        if (queue.stats().jobs === 0 && queue.stats().pending === 0) break;
    }
    await Promise.all(requests);
    await visible;
    assert.ok(queue.stats().running <= 4);
});

test("FILM-05：解码失败直接拒绝，不缓存，重试走新解码", async () => {
    const clock = manualClock();
    let attempts = 0;
    const decoder = {
        decode(photo, tier, options) {
            attempts += 1;
            if (attempts === 1) return Promise.reject(new Error("无法解码该图片"));
            const dims = proxyDimensions(photo, options.longEdge);
            return Promise.resolve({ tier, ...dims, surface: { id: photo.id } });
        },
    };
    const queue = createThumbnailQueue({ size: 160, decoder, clock, orient: (surface) => surface });
    const photo = fakePhoto();
    await assert.rejects(queue.request(photo), /无法解码/);
    assert.equal(queue.stats().cached, 0);
    const surface = await queue.request(photo);
    assert.deepEqual(surface, { id: photo.id });
    assert.equal(attempts, 2);
    assert.equal(queue.stats().cached, 1);
});

test("FILM-05：LRU 缓存命中不重解码，超出上限淘汰最久未用", async () => {
    const clock = manualClock();
    let attempts = 0;
    const decoder = {
        decode(photo, tier, options) {
            attempts += 1;
            const dims = proxyDimensions(photo, options.longEdge);
            return Promise.resolve({ tier, ...dims, surface: { id: photo.id } });
        },
    };
    const queue = createThumbnailQueue({
        size: 160,
        decoder,
        clock,
        cacheLimit: 2,
        orient: (surface) => surface,
    });
    const a = fakePhoto({ id: "dev/L1.ARW", fingerprint: "dev:l1", scene: { base: 501, variant: 0 } });
    const b = fakePhoto({ id: "dev/L2.ARW", fingerprint: "dev:l2", scene: { base: 502, variant: 0 } });
    const c = fakePhoto({ id: "dev/L3.ARW", fingerprint: "dev:l3", scene: { base: 503, variant: 0 } });
    await queue.request(a);
    await queue.request(b);
    assert.equal(attempts, 2);
    await queue.request(a);
    assert.equal(attempts, 2);
    await queue.request(c);
    assert.equal(queue.stats().cached, 2);
    await queue.request(b);
    assert.equal(attempts, 4);
});

test("FILM-02：排队中与在途的缩略图请求都可取消", async () => {
    const clock = manualClock();
    const calls = [];
    const decoder = {
        decode(photo, tier, options) {
            let resolve;
            const promise = new Promise((res) => { resolve = res; });
            calls.push({ photo, options, resolve });
            return promise;
        },
    };
    const queue = createThumbnailQueue({
        size: 160,
        decoder,
        clock,
        concurrency: 1,
        orient: (surface) => surface,
    });
    const running = queue.request(fakePhoto({ id: "dev/R1.ARW", fingerprint: "dev:r1" }));
    const pendingController = new AbortController();
    const pending = queue.request(
        fakePhoto({ id: "dev/R2.ARW", fingerprint: "dev:r2" }),
        false,
        { signal: pendingController.signal },
    );
    pendingController.abort();
    await assert.rejects(pending, (error) => error.name === "AbortError");
    assert.equal(calls.length, 1);
    assert.equal(queue.stats().pending, 0);

    const runningController = new AbortController();
    const inFlight = queue.request(
        fakePhoto({ id: "dev/R3.ARW", fingerprint: "dev:r3" }),
        false,
        { signal: runningController.signal },
    );
    assert.equal(queue.stats().pending, 1);
    queue.cancelAll();
    await assert.rejects(running, (error) => error.name === "AbortError");
    await assert.rejects(inFlight, (error) => error.name === "AbortError");
    assert.equal(calls.length, 1);
});
