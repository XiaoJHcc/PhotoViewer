import { test } from "node:test";
import assert from "node:assert/strict";
import { createTokens } from "../src/core/tokens.js";
import { manualClock } from "../src/core/clock.js";
import { createApp } from "../src/core/app.js";
import { featuresFor } from "../src/core/features.js";
import { defaultSettings } from "../src/core/settings.js";

const t = createTokens();
const flush = () => new Promise((resolve) => setImmediate(resolve));
const measureText = (text, size) => String(text).length * size * 0.5;

function makePhoto(folderId, name, index, overrides = {}) {
    const stem = name.replace(/\.[^.]+$/, "");
    const ext = name.slice(stem.length).toLowerCase();
    return {
        id: `${folderId}/${name}`,
        folderId,
        name,
        stem,
        ext,
        size: 1_000_000 + index * 4096,
        modified: Date.UTC(2026, 9, 12, 0, 0) + index * 60000,
        taken: Date.UTC(2026, 9, 12, 5, 0) + index * 1000,
        tzOffset: 480,
        rating: 0,
        width: 6000,
        height: 4000,
        orientation: 1,
        focus: null,
        exif: {
            aperture: 2.8,
            exposure: 1 / 250,
            iso: 400,
            focal: 50,
            equivFocal: 75,
            camera: "ILCE-7M4",
            lens: "FE 50mm F1.2 GM",
            ev: 0,
            wb: "自动",
            flash: "未闪光",
        },
        shake: false,
        fingerprint: `fp:${folderId}/${stem}`,
        extracted: true,
        fault: {},
        scene: { base: index + 10, variant: 0 },
        ...overrides,
    };
}

function makeLibrary() {
    const photos = [];
    const add = (name, index, overrides = {}) => {
        const entry = makePhoto("f1", name, index, overrides);
        photos.push(entry);
        return entry;
    };
    add("A001.ARW", 0, { rating: 3, fingerprint: "fp:f1/A001", scene: { base: 1, variant: 0 } });
    add("A001.JPG", 0, { rating: 3, fingerprint: "fp:f1/A001", scene: { base: 1, variant: 0 } });
    add("B002.JPG", 1, { rating: 3 });
    add("C003.JPG", 2, { rating: 5 });
    add("D004.JPG", 3, { rating: 4, fault: { write: true } });
    add("E005.JPG", 4, { rating: 1 });
    for (let number = 6; number <= 30; number += 1) {
        add(`P${String(number).padStart(3, "0")}.JPG`, number, { rating: 0 });
    }
    add("S031.JPG", 31, { rating: 2, scene: { base: 700, variant: 0 } });
    add("S032.JPG", 32, { rating: 2, scene: { base: 700, variant: 1 } });
    add("S033.JPG", 33, { rating: 2, scene: { base: 700, variant: 2 } });

    const single = makePhoto("one", "IMG_5000.HEIC", 0, { rating: 3, extracted: false });
    return {
        folders: [
            { id: "f1", name: "测试文件夹", photos, enumerable: true },
            { id: "one", name: "共享的照片", photos: [single], enumerable: false },
            { id: "empty", name: "空文件夹", photos: [], enumerable: true },
        ],
    };
}

function createDecoder() {
    const calls = [];
    return {
        calls,
        decode(photo, tier, options = {}) {
            const longEdge = options.longEdge ?? 1600;
            const width = tier === "full" ? photo.width : Math.min(photo.width, longEdge);
            const height = Math.round(width * (photo.height / photo.width));
            calls.push({ photo, tier, longEdge });
            return Promise.resolve({ tier, width, height, surface: { kind: tier, photoId: photo.id } });
        },
        enhance(surface) {
            return Promise.resolve({ kind: "enhanced", source: surface });
        },
    };
}

function createStore() {
    return {
        read: async (photo) => (photo.extracted ? featuresFor(photo) : null),
        compute: async (photo) => featuresFor(photo),
        extractGroup: async (photos) => {
            for (const photo of photos) photo.extracted = true;
        },
    };
}

function createStorage(map = new Map()) {
    return {
        map,
        get: (key) => (map.has(key) ? map.get(key) : null),
        set: (key, value) => {
            map.set(key, String(value));
        },
    };
}

function createFilmHost({ offset = 0, viewportLength = 600 } = {}) {
    const state = { offset, viewportLength, writes: [] };
    return {
        state,
        getOffset: () => state.offset,
        setOffset: (value) => {
            state.offset = value;
            state.writes.push(value);
        },
        viewportLength: () => state.viewportLength,
    };
}

function filmViewport(app) {
    const film = app.state.layout.film;
    if (!film) return 0;
    return app.state.layout.layoutMode === "col" ? film.scroll.h : film.scroll.w;
}

function harness({ storage = createStorage(), settings = null, width = 1200, height = 850, library = makeLibrary() } = {}) {
    const clock = manualClock();
    const film = createFilmHost();
    const app = createApp({
        t,
        clock,
        library,
        decoder: createDecoder(),
        store: createStore(),
        storage,
        settings: { appleKeyboard: false, ...(settings ?? {}) },
        dpr: 1,
        reducedMotion: () => true,
        measureText,
    });
    app.setWindowSize(width, height);
    // 假滚动宿主的视口长取布局给出的胶片栏主轴长度，与真实壳层一致。
    film.state.viewportLength = filmViewport(app);
    app.bindFilmScroller(film);
    return { app, clock, library, film, storage };
}

async function settle(clock, rounds = 6) {
    for (let index = 0; index < rounds; index += 1) {
        clock.advance(200);
        await flush();
    }
}

async function settleThen(clock, promise, rounds = 6) {
    await settle(clock, rounds);
    return promise;
}

const cardIndexOf = (app, id) => app.state.cards.findIndex((card) => card.key === id);

test("FILM-01/OPEN-05：打开文件夹把目标项定位到列表中心", () => {
    const { app, film } = harness();
    assert.equal(app.openFolder("f1", { target: "f1/P025.JPG" }), true);
    assert.equal(app.state.displayedId, "f1/P025.JPG");
    assert.equal(app.state.anchorId, "f1/P025.JPG");
    assert.equal(app.state.count, 33);
    // 列布局纵向列表：卡片起点 + (卡片长 − 视口长) / 2，首尾之外不夹取。
    const expected = 5 + 24 * 144 + (144 - filmViewport(app)) / 2;
    assert.equal(film.state.offset, expected);
    assert.equal(film.state.writes.at(-1), expected);
});

test("FILM-01：点选不定位、上一张/下一张定位、Exchange 不定位", () => {
    const { app, film } = harness();
    app.openFolder("f1", { target: "f1/A001.JPG" });
    const baseline = film.state.writes.length;

    assert.equal(app.selectCard("f1/P010.JPG"), true);
    assert.equal(app.state.displayedId, "f1/P010.JPG");
    assert.equal(film.state.writes.length, baseline);

    app.navigate(1);
    assert.equal(app.state.displayedId, "f1/P011.JPG");
    const nextIndex = cardIndexOf(app, "f1/P011.JPG");
    assert.equal(film.state.writes.length, baseline + 1);
    assert.equal(film.state.writes.at(-1), 5 + nextIndex * 144 + (144 - filmViewport(app)) / 2);

    const located = film.state.writes.length;
    assert.equal(app.exchange(), true);
    assert.equal(app.state.displayedId, "f1/P010.JPG");
    assert.equal(film.state.writes.length, located);
    assert.equal(app.state.anchorId, "f1/P010.JPG");
});

test("FILM-01：排序、筛选与评分刷新不定位，尺寸变化只夹取偏移", async () => {
    const { app, clock, film } = harness();
    app.openFolder("f1", { target: "f1/A001.JPG" });
    const baseline = film.state.writes.length;

    app.setSort("taken", "desc");
    app.setRatingFilter("3");
    assert.equal(film.state.writes.length, baseline);

    const writing = app.rate("f1/A001.JPG", 2);
    await settle(clock);
    assert.equal(await writing, true);
    assert.equal(film.state.writes.length, baseline);

    film.state.offset = 9999;
    app.setRatingFilter("all");
    app.filmResized();
    assert.equal(film.state.writes.length, baseline + 1);
    assert.equal(film.state.offset, 5 + 33 * 144 + 10 - filmViewport(app));

    const clamped = film.state.writes.length;
    app.setWindowSize(1100, 850);
    assert.equal(film.state.writes.length, clamped);
});

test("FILM-04：隐藏后重新显示定位当前项，换向按新方向重算", () => {
    const { app, film } = harness();
    app.openFolder("f1", { target: "f1/P020.JPG" });
    const baseline = film.state.writes.length;

    app.toggleFilmbar();
    assert.equal(app.state.panes.film, false);
    assert.equal(film.state.writes.length, baseline);
    app.toggleFilmbar();
    assert.equal(app.state.panes.film, true);
    // FILM-04：显隐请求按布局就绪后的新几何定位（壳层在落位后调用 layoutReady）。
    app.layoutReady();
    const expectedY = 5 + 19 * 144 + (144 - filmViewport(app)) / 2;
    assert.equal(film.state.writes.at(-1), expectedY);

    app.setWindowSize(700, 850);
    assert.equal(app.state.layout.layoutMode, "row");
    app.layoutReady();
    // 换向按新方向与新视口（行布局胶片带宽）重算，不沿用旧视口长度。
    const expectedX = 10 + 19 * 96 + (96 - filmViewport(app)) / 2;
    assert.equal(film.state.writes.at(-1), expectedX);
});

test("FILM-06 / FILTER-04：三星筛选下当前照片改为两星暂留，切走后移除", async () => {
    const { app, clock } = harness();
    app.openFolder("f1", { target: "f1/A001.JPG" });
    app.setRatingFilter("3");
    assert.equal(app.state.count, 2);
    assert.equal(app.state.displayedId, "f1/A001.JPG");

    const writing = app.rate("f1/A001.JPG", 2);
    assert.equal(await settleThen(clock, writing), true);
    assert.equal(app.state.displayedId, "f1/A001.JPG");
    assert.equal(app.state.count, 2);
    const retained = app.state.cards.find((card) => card.key === "f1/A001.JPG");
    assert.ok(retained, "当前照片应暂留在列表中");
    assert.equal(retained.photo.rating, 2);
    assert.equal(retained.companions[0].rating, 2);

    app.navigate(1);
    assert.equal(app.state.displayedId, "f1/B002.JPG");
    assert.equal(app.state.count, 1);
    assert.equal(cardIndexOf(app, "f1/A001.JPG"), -1);
});

test("FILTER-05：筛选排除当前照片后主图不变、无高亮，下一张落到最近邻", () => {
    const { app } = harness();
    app.openFolder("f1", { target: "f1/C003.JPG" });
    app.setRatingFilter("4");

    assert.equal(app.state.displayedId, "f1/C003.JPG");
    assert.equal(app.state.photo.name, "C003.JPG");
    assert.deepEqual(app.state.cards.map((card) => card.key), ["f1/D004.JPG"]);
    assert.equal(cardIndexOf(app, app.state.anchorId), -1);

    app.navigate(1);
    assert.equal(app.state.displayedId, "f1/D004.JPG");

    app.openFolder("f1", { target: "f1/C003.JPG" });
    app.setRatingFilter("3");
    app.navigate(-1);
    assert.equal(app.state.displayedId, "f1/B002.JPG");
});

test("OPEN-03/OPEN-04：单图模式、目标不在筛选结果中仍为当前照片", () => {
    const { app } = harness();
    app.openFolder("one");
    assert.equal(app.state.singleMode, true);
    assert.equal(app.state.count, 1);
    assert.equal(app.state.folder.name, "IMG_5000.HEIC");
    assert.equal(app.state.toolbar.rating.value, 3);

    app.openFolder("f1", { target: "f1/A001.JPG" });
    app.setRatingFilter("5");
    assert.equal(app.state.count, 1);
    app.openFolder("f1", { target: "f1/B002.JPG" });
    assert.equal(app.state.displayedId, "f1/B002.JPG");
    assert.deepEqual(app.state.cards.map((card) => card.key), ["f1/C003.JPG"]);
    assert.equal(cardIndexOf(app, "f1/B002.JPG"), -1);
});

test("OPEN-05/IMAGE-06：打开回到适应态，导航图只在放大态显示", () => {
    const { app } = harness();
    app.openFolder("f1", { target: "f1/P010.JPG" });
    assert.equal(app.state.viewport.isFit, true);
    assert.equal(app.state.viewport.navigator.visible, false);

    assert.equal(app.fit(), true);
    assert.equal(app.state.viewport.isFit, false);
    assert.equal(app.state.viewport.navigator.visible, true);
    assert.equal(app.state.viewport.percentText, "100%");

    app.updateSettings({ showZoomIndicator: false });
    assert.equal(app.state.viewport.navigator.visible, false);

    app.openFolder("f1", { target: "f1/P010.JPG" });
    assert.equal(app.state.viewport.isFit, true);
});

test("IMAGE-06：导航图拖动只改视口中心、保持倍率", () => {
    const { app } = harness();
    app.openFolder("f1", { target: "f1/P010.JPG" });
    assert.equal(app.fit(), true);
    const percent = app.state.viewport.percentText;
    const scale = app.state.viewport.scale;
    const before = app.state.viewport.navigator.frame;

    assert.equal(app.navigateTo(0.8, 0.7), true);
    const after = app.state.viewport.navigator.frame;
    assert.ok(after.x > before.x && after.y > before.y, "框心没有跟随归一化点");
    assert.equal(app.state.viewport.percentText, percent);
    assert.equal(app.state.viewport.scale, scale);

    const view = app.state.viewport.view;
    const rect = app.state.viewport.rect;
    assert.ok(Math.abs((view.width / 2 - rect.x) / rect.w - 0.8) < 1e-6);
    assert.ok(Math.abs((view.height / 2 - rect.y) / rect.h - 0.7) < 1e-6);
});

test("SIM-05：相似项点选只改显示身份，锚点与胶片栏位置不动", async () => {
    const { app, clock, film } = harness();
    app.openFolder("f1", { target: "f1/S031.JPG" });
    app.setSimilarityExpanded(true);
    await settle(clock, 4);

    const items = app.state.similarity.items;
    assert.ok(items.length >= 2);
    const target = items.find((item) => item.card.key === "f1/S032.JPG");
    assert.ok(target);
    const anchor = app.state.anchorId;
    const writes = film.state.writes.length;

    assert.equal(app.selectSimilar("f1/S032.JPG"), true);
    assert.equal(app.state.displayedId, "f1/S032.JPG");
    assert.equal(app.state.anchorId, anchor);
    assert.equal(film.state.writes.length, writes);
    assert.equal(app.selectSimilar("f1/S032.JPG"), false);

    const current = app.state.similarity.items.filter((item) => item.current);
    assert.deepEqual(current.map((item) => item.card.key), ["f1/S032.JPG"]);
});

test("INPUT-01/02/05/10：快捷键派发、菜单抑制、按住重复与无照片命令", () => {
    const { app } = harness();
    app.openFolder("f1", { target: "f1/A001.JPG" });

    assert.deepEqual(app.keyDown({ code: "ArrowRight" }), { command: "Next", handled: true });
    assert.equal(app.state.displayedId, "f1/B002.JPG");
    assert.deepEqual(app.keyDown({ code: "ArrowRight", repeat: true }), { command: "Next", handled: true });
    assert.equal(app.state.displayedId, "f1/C003.JPG");
    assert.deepEqual(app.keyDown({ code: "ArrowLeft", repeat: true }), { command: null, handled: true });
    assert.equal(app.state.displayedId, "f1/C003.JPG");

    app.setMenuOpen(true);
    assert.deepEqual(app.keyDown({ code: "ArrowRight" }), { command: null, handled: false });
    assert.equal(app.state.displayedId, "f1/C003.JPG");
    app.setMenuOpen(false);
    assert.deepEqual(app.keyDown({ code: "ArrowRight" }), { command: "Next", handled: true });

    const opened = [];
    app.on("request-open", (payload) => opened.push(payload));
    assert.deepEqual(app.keyDown({ code: "KeyN", ctrlKey: true }), { command: "Open", handled: true });
    assert.deepEqual(opened, [{ kind: "file" }]);

    const empty = harness().app;
    const emptyOpened = [];
    empty.on("request-open", (payload) => emptyOpened.push(payload));
    const result = empty.keyDown({ code: "Digit3" });
    assert.equal(result.command, "SetRating3");
    assert.equal(empty.state.displayedId, null);
    assert.deepEqual(empty.keyDown({ code: "KeyZ", repeat: true }), { command: null, handled: true });
    empty.keyDown({ code: "KeyN", ctrlKey: true });
    assert.deepEqual(emptyOpened, [{ kind: "file" }]);
    assert.equal(empty.state.toolbar.rating.enabled, false);
});

test("IMAGE-03/FILM-05：分析栏可见触发 Full 升级并记入主图缓存", async () => {
    const { app, clock } = harness();
    app.openFolder("f1", { target: "f1/A001.JPG" });
    await settle(clock, 3);
    assert.equal(app.state.session.tier, "proxy");
    assert.equal(app.state.cached.has("f1/A001.JPG"), false);

    app.toggleAnalysis();
    await settle(clock, 3);
    assert.equal(app.state.session.tier, "full");
    assert.equal(app.state.cached.has("f1/A001.JPG"), true);
    assert.ok(app.state.analysis.tiles.length >= 6);
});

test("STATE-01：切回已上屏的照片同步换图，几何与新图同一次调用生效", async () => {
    const left = makePhoto("pair", "LEFT.JPG", 0, { width: 3000, height: 2000 });
    const right = makePhoto("pair", "RIGHT.JPG", 1, { width: 6000, height: 4000 });
    const library = { folders: [{ id: "pair", name: "两张", photos: [left, right], enumerable: true }] };
    const { app, clock } = harness({ library });

    app.openFolder("pair", { target: left.id });
    await settle(clock, 3);
    const surfaceLeft = app.state.session.surface;
    assert.ok(surfaceLeft, "首张未上屏");
    app.selectCard(right.id);
    await settle(clock, 3);
    assert.equal(app.state.viewport.image.width, 6000);

    app.selectCard(left.id);
    assert.equal(app.state.session.surface, surfaceLeft, "命中缓存没有同步换回已有展示产物");
    assert.equal(app.state.session.loadingHint, false, "命中缓存显示了加载提示");
    assert.equal(app.state.session.tier, "proxy");
    assert.equal(app.state.viewport.image.width, 3000, "几何没有与新图在同一次调用里生效");
    assert.equal(app.state.viewport.isFit, true);

    await settle(clock, 2);
    assert.equal(app.state.session.surface, surfaceLeft, "命中缓存后又发生了解码替换");
    assert.equal(app.state.session.loadingHint, false, "命中缓存 300 ms 后出现加载提示");
});

test("IMAGE-03：小图适应倍率高于 100% 不升 Full，分析栏可见仍升级", async () => {
    const small = makePhoto("small", "SMALL.JPG", 0, { width: 600, height: 400 });
    const library = { folders: [{ id: "small", name: "小图", photos: [small], enumerable: true }] };
    const { app, clock } = harness({ library });
    app.openFolder("small", { target: "small/SMALL.JPG" });
    await settle(clock, 2);
    assert.equal(app.state.viewport.isFit, true);
    assert.ok(app.state.viewport.physicalScale > 1, "小图适应倍率应高于物理 100%");
    assert.equal(app.state.session.tier, "proxy", "适应态不应因倍率升 Full");

    app.toggleAnalysis();
    await settle(clock, 3);
    assert.equal(app.state.session.tier, "full", "分析栏可见时应升 Full");
});

test("INPUT-04：物理 Ctrl 的精确滚动按绑定连续缩放，120 px 累积触发其他命令", () => {
    const { app } = harness();
    app.openFolder("f1", { target: "f1/P010.JPG" });
    app.setModifierState({ ctrl: true });
    const before = app.state.viewport.physicalScale;
    assert.deepEqual(app.wheelOnPhoto({ deltaMode: 0, deltaY: -10, ctrlKey: true }), { handled: true });
    const after = app.state.viewport.physicalScale;
    assert.ok(Math.abs(after / before - Math.exp(10 * 0.01)) < 1e-6, `连续缩放倍率 ${after / before}`);

    const mapped = harness({
        settings: { hotkeys: [{ command: "Next", primary: "Ctrl+WheelUp", secondary: null }] },
    });
    mapped.app.openFolder("f1", { target: "f1/P010.JPG" });
    mapped.app.setModifierState({ ctrl: true });
    const start = mapped.app.state.displayedId;
    mapped.app.wheelOnPhoto({ deltaMode: 0, deltaY: -50, ctrlKey: true });
    assert.equal(mapped.app.state.displayedId, start, "未满 120 px 不应触发命令");
    mapped.app.wheelOnPhoto({ deltaMode: 0, deltaY: -70, ctrlKey: true });
    assert.notEqual(mapped.app.state.displayedId, start, "累积 120 px 应触发一次命令");
});

test("TOOLBAR-04/05/07：命令列表随设置即时变化，tooltip 含当前绑定，EXIF 取自当前照片", () => {
    const { app } = harness();
    const defaults = defaultSettings();
    app.updateSettings({
        hotkeys: [
            { command: "Open", display: true, primary: "Ctrl+KeyN", secondary: null },
            { command: "Next", display: true, primary: "KeyN", secondary: null },
        ],
        exifItems: defaults.exifItems,
    });
    assert.deepEqual(
        app.state.toolbar.commands.slice(0, 2).map((item) => item.command),
        ["Open", "Next"],
    );
    assert.equal(app.state.toolbar.commands[1].tooltip, "下一张 · N");
    assert.equal(app.state.toolbar.commands[0].enabled, true);
    assert.equal(app.state.toolbar.commands[1].enabled, false);

    app.openFolder("f1", { target: "f1/A001.JPG" });
    assert.deepEqual(
        app.state.toolbar.exif.map((item) => item.id),
        ["aperture", "exposure", "iso", "equivFocal"],
    );
    assert.deepEqual(
        app.state.toolbar.exif.map((item) => item.value),
        ["f/2.8", "1/250", "400", "75mm"],
    );
    assert.equal(app.state.toolbar.exif[0].tooltip, "光圈 · f/2.8");
    assert.equal(app.state.toolbar.rating.value, 3);

    app.updateSettings({ hotkeys: [
        { command: "Next", display: true, primary: "KeyN", secondary: "WheelDown" },
        { command: "Open", display: true, primary: "Ctrl+KeyN", secondary: null },
    ] });
    assert.deepEqual(
        app.state.toolbar.commands.slice(0, 2).map((item) => item.command),
        ["Next", "Open"],
    );
    assert.equal(app.state.toolbar.commands[0].tooltip, "下一张 · N / 滚轮下");
});

test("MENU-01/02/06/07：固定项与实时勾选，无照片增强置灰，Web 不显示工具与设置", () => {
    const { app } = harness();
    const model = app.menuModel();
    assert.deepEqual(model.map((item) => item.id), ["open-file", "open-folder", "film", "analysis", "control", "enhance"]);
    assert.deepEqual(model.map((item) => item.label), ["打开文件…", "打开文件夹…", "胶片栏", "分析栏", "控制栏", "增强"]);
    assert.equal(model.find((item) => item.id === "enhance").enabled, false);
    assert.equal(model.find((item) => item.id === "enhance").checked, false);
    assert.equal(model.find((item) => item.id === "open-file").shortcut, "Ctrl+N");

    app.toggleFilmbar();
    assert.equal(app.menuModel().find((item) => item.id === "film").checked, false);
    app.toggleControlbar();
    assert.equal(app.menuModel().find((item) => item.id === "control").checked, false);

    app.openFolder("f1");
    assert.equal(app.menuModel().find((item) => item.id === "enhance").enabled, true);
    assert.equal(app.runMenu("enhance"), true);
    assert.equal(app.state.session.enhance, true);
    assert.equal(app.menuModel().find((item) => item.id === "enhance").checked, true);
});

test("SHELL-04/FILTER-09：显隐与相似开关持久化，排序与星级筛选不持久化", () => {
    const storage = createStorage();
    const first = harness({ storage });
    first.app.openFolder("f1", { target: "f1/A001.JPG" });
    first.app.toggleFilmbar();
    first.app.toggleControlbar();
    first.app.setSimilarityExpanded(true);
    first.app.setSort("taken", "desc");
    first.app.setRatingFilter("3");
    assert.equal(storage.get("PhotoViewer.Native.ShowFilm"), "false");
    assert.equal(storage.get("PhotoViewer.Native.ShowControls"), "false");
    const saved = JSON.parse(storage.get("PhotoViewer.Settings"));
    assert.equal(saved.similarityPanelExpanded, true);
    assert.equal(saved.layoutMode, "auto");
    assert.equal("sortKey" in saved, false);

    const second = harness({ storage });
    assert.equal(second.app.state.panes.film, false);
    assert.equal(second.app.state.panes.control, false);
    assert.equal(second.app.settings.similarityPanelExpanded, true);
    assert.equal(second.app.state.sortKey, "name");
    assert.equal(second.app.state.sortOrder, "asc");
    assert.equal(second.app.state.ratingFilter, "all");
    assert.equal(second.app.state.displayedId, null);
    assert.equal(second.app.state.similarity.visible, false);

    second.app.toggleFilmbar();
    assert.equal(second.app.state.panes.film, true);
    assert.equal(second.app.state.similarity.visible, true);
});

test("STATE-03/FILM-06：星级写回失败保持原值并给出提示条", async () => {
    const { app, clock } = harness();
    app.openFolder("f1", { target: "f1/D004.JPG" });
    assert.equal(app.state.toolbar.rating.value, 4);

    const writing = app.rate("f1/D004.JPG", 1);
    await settle(clock, 3);
    assert.equal(await writing, false);
    assert.equal(app.state.photo.rating, 4);
    assert.equal(app.state.toolbar.rating.value, 4);
    assert.equal(app.state.session.toast.text, "无法写入星级：D004.JPG（文件只读）");
});

test("change 事件：同一 tick 内多次变化合并为一次派发", async () => {
    const { app } = harness();
    await flush();
    const events = [];
    app.on("change", (regions) => events.push(regions));

    app.toggleFilmbar();
    app.toggleControlbar();
    app.setSort("taken", "asc");
    assert.equal(events.length, 0);
    await flush();
    assert.equal(events.length, 1);
    const regions = events[0];
    assert.ok(regions.has("panes"));
    assert.ok(regions.has("layout"));
    assert.ok(regions.has("list"));
    assert.ok(regions.has("window") === false);
});

test("FILTER-06/SIM-07：相似开关写入设置并扩宽文件栏，关闭后几何还原", () => {
    const { app } = harness();
    app.openFolder("f1", { target: "f1/A001.JPG" });
    assert.equal(app.state.layout.panes.filePane.w, 116);
    app.setSimilarityExpanded(true);
    // SIM-07：文件栏加宽 = 116 + 面板宽 110 + 外边距 4。
    assert.equal(app.state.layout.panes.filePane.w, 230);
    assert.equal(app.settings.similarityPanelExpanded, true);
    app.setSimilarityExpanded(false);
    assert.equal(app.state.layout.panes.filePane.w, 116);
});

test("FILTER-10/OPEN-02：空文件夹计数为零、主图空态，不进入单图模式", () => {
    const { app } = harness();
    assert.equal(app.openFolder("empty"), true);
    assert.equal(app.state.count, 0);
    assert.deepEqual(app.state.cards, []);
    assert.equal(app.state.displayedId, null);
    assert.equal(app.state.photo, null);
    assert.equal(app.state.folder.name, "空文件夹");
    assert.equal(app.state.singleMode, false);
});

test("INPUT-03/04：离散滚轮翻页、精确滚动平移、捏合缩放", () => {
    const { app } = harness();
    app.openFolder("f1", { target: "f1/P010.JPG" });
    assert.equal(app.state.displayedId, "f1/P010.JPG");

    assert.deepEqual(app.wheelOnPhoto({ deltaMode: 0, deltaY: 120, wheelDeltaY: 120 }), { handled: true });
    assert.equal(app.state.displayedId, "f1/P011.JPG");

    assert.equal(app.state.viewport.isFit, true);
    const fitOffset = app.state.viewport.offset;
    assert.deepEqual(app.wheelOnPhoto({ deltaMode: 0, deltaY: 10 }), { handled: true });
    assert.deepEqual(app.state.viewport.offset, fitOffset);

    const before = app.state.viewport.physicalScale;
    assert.deepEqual(app.wheelOnPhoto({ deltaMode: 0, deltaY: -100, ctrlKey: true }), { handled: true });
    assert.equal(app.state.viewport.isFit, false);
    assert.ok(app.state.viewport.physicalScale > before);

    const panBefore = app.state.viewport.offset;
    app.wheelOnPhoto({ deltaMode: 0, deltaY: 40 });
    assert.notDeepEqual(app.state.viewport.offset, panBefore);
});

test("IMAGE-07/MENU-07：增强预览需照片，内存压力关闭增强并提示", async () => {
    const { app, clock } = harness();
    assert.equal(app.toggleEnhance(), false);

    app.openFolder("f1", { target: "f1/A001.JPG" });
    await settle(clock, 2);
    assert.equal(app.toggleEnhance(), true);
    assert.equal(app.state.session.enhance, true);
    assert.equal(app.menuModel().find((item) => item.id === "enhance").checked, true);

    assert.equal(app.simulateMemoryWarning(), true);
    assert.equal(app.state.session.enhance, false);
    assert.equal(app.state.session.toast.text, "内存不足，已关闭增强预览");
});

test("ANALYSIS-04/08/09：准星、细节悬停与双击定位转发", async () => {
    const { app, clock } = harness();
    app.openFolder("f1", { target: "f1/A001.JPG" });
    app.toggleAnalysis();
    await settle(clock, 2);

    app.clickDiagnostic(0.25, 0.75);
    assert.deepEqual(app.state.analysis.crosshair, { u: 0.25, v: 0.75 });
    assert.equal(app.hoverDetail("center"), "center");
    assert.equal(app.state.analysis.hoverKind, "center");
    assert.equal(app.state.viewport.isFit, true);

    assert.deepEqual(app.locateDetail("center"), { u: 0.5, v: 0.5 });
    assert.equal(app.state.viewport.isFit, false);

    app.clickAnalysisBlank();
    assert.equal(app.state.analysis.crosshair, null);
    assert.equal(app.hoverDetail(null), null);
});

test("OPEN-03：打开文件在不可枚举目录进入单图模式", () => {
    const { app } = harness();
    assert.equal(app.openFile("one/IMG_5000.HEIC"), true);
    assert.equal(app.state.singleMode, true);
    assert.equal(app.state.folder.name, "IMG_5000.HEIC");
    assert.equal(app.state.count, 1);
    assert.equal(app.openFile("one/missing.JPG"), false);
});

test("FILTER-09/OPEN-04：打开不改变排序、星级筛选与相似开关", () => {
    const { app } = harness();
    app.openFolder("f1");
    app.setSort("taken", "desc");
    app.setRatingFilter("3");
    app.setSimilarityExpanded(true);

    app.openFolder("one");
    assert.equal(app.state.sortKey, "taken");
    assert.equal(app.state.sortOrder, "desc");
    assert.equal(app.state.ratingFilter, "3");
    assert.equal(app.settings.similarityPanelExpanded, true);
    assert.equal(app.state.count, 1);

    app.openFolder("empty");
    assert.equal(app.state.sortKey, "taken");
    assert.equal(app.state.ratingFilter, "3");
    assert.equal(app.state.count, 0);
});

test("SIM-03/SIM-10：提取全部转发相似面板并刷新未提取计数", async () => {
    const { app } = harness();
    app.openFile("one/IMG_5000.HEIC");
    assert.equal(app.state.similarity.unextracted, 1);

    const running = app.extractAll();
    await flush();
    await running;
    assert.equal(app.state.photo.extracted, true);
    assert.equal(app.state.similarity.unextracted, 0);
});
