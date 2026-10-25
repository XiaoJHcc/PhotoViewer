import { test } from "node:test";
import assert from "node:assert/strict";
import { createTokens } from "../src/core/tokens.js";
import { manualClock } from "../src/core/clock.js";
import { createApp } from "../src/core/app.js";
import { createLibrary } from "../src/core/library.js";
import { FileList } from "../src/core/filter.js";
import { FORMATS } from "../src/core/settings.js";
import { RatingWriter } from "../src/core/session.js";
import {
    displayStars,
    hasRatingConflict,
    matchesRatingFilter,
    ratingSources,
    ratingWriteBlockReason,
    readRatingRecord,
    representativeRating,
} from "../src/core/rating.js";

const t = createTokens();
const flush = () => new Promise((resolve) => setImmediate(resolve));
const measureText = (text, size) => String(text).length * size * 0.5;

function photo(name, options = {}) {
    const stem = options.stem ?? name.slice(0, name.lastIndexOf("."));
    const ext = options.ext ?? name.slice(name.lastIndexOf(".")).toLowerCase();
    return {
        id: options.id ?? `f1/${name}`,
        folderId: options.folderId ?? "f1",
        name,
        stem,
        ext,
        size: options.size ?? 1_000_000,
        modified: 0,
        taken: 1_000,
        tzOffset: 480,
        rating: "rating" in options ? options.rating : 3,
        ratingState: options.ratingState,
        sidecar: options.sidecar,
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
            lens: null,
            ev: 0,
            wb: "自动",
            flash: null,
        },
        shake: false,
        fingerprint: null,
        fault: {},
        scene: { base: 1, variant: 0 },
        extracted: false,
    };
}

function card(rep, companions = []) {
    return { key: rep.id, photo: rep, companions, displayName: rep.name };
}

function listOf(photos) {
    const list = new FileList({ formats: FORMATS, sameNameAsOne: true });
    list.setPhotos(photos);
    return list;
}

test("RATING-01：组内每个文件内嵌与每个实际存在的 sidecar 各算一个来源", () => {
    const jpg = photo("A.JPG", { rating: 3, sidecar: { rating: 3 } });
    const arw = photo("A.ARW", { rating: 5, sidecar: jpg.sidecar });
    const sources = ratingSources(card(jpg, [arw]));
    assert.deepEqual(sources.map((source) => source.kind), ["embedded", "embedded", "sidecar"]);
    assert.deepEqual(sources.map((source) => source.label), ["A.JPG", "A.ARW", "A.xmp"]);
    assert.deepEqual(sources.map((source) => source.type), ["内嵌", "内嵌", "XMP"]);
    assert.deepEqual(sources.map((source) => source.value), [3, 5, 3]);

    const noSidecar = ratingSources(card(photo("B.JPG", { rating: 3 }), [photo("B.ARW", { rating: 3 })]));
    assert.equal(noSidecar.length, 2);
    assert.ok(noSidecar.every((source) => source.kind === "embedded"));
});

test("RATING-01：组级冲突覆盖取值不同、缺失、无效、不可读、内部冲突与 sidecar", () => {
    const consistent = card(photo("A.JPG", { rating: 3 }), [photo("A.ARW", { rating: 3 })]);
    assert.equal(hasRatingConflict(consistent), false);

    assert.equal(hasRatingConflict(card(photo("B.JPG", { rating: 3 }), [photo("B.ARW", { rating: 5 })])), true);
    assert.equal(hasRatingConflict(card(photo("C.JPG", { rating: 3 }), [photo("C.ARW", { rating: null, ratingState: "missing" })])), true);
    assert.equal(hasRatingConflict(card(photo("D.JPG", { rating: 3, sidecar: { rating: 1 } }), [photo("D.ARW", { rating: 3 })])), true);
    assert.equal(hasRatingConflict(card(photo("E.JPG", { rating: 3, sidecar: { state: "invalid" } }))), true);
    assert.equal(hasRatingConflict(card(photo("F.JPG", { rating: 3 }), [photo("F.ARW", { rating: null, ratingState: "unreadable" })])), true);
    assert.equal(hasRatingConflict(card(photo("G.JPG", { rating: null, ratingState: "conflict" }))), true);
    // 只有一个来源且缺失也算冲突（RATING-01「任一来源缺失」）。
    assert.equal(hasRatingConflict(card(photo("H.JPG", { rating: null, ratingState: "missing" }))), true);
    // 已拒绝是带值状态，全部 -1 组内部一致。
    assert.equal(hasRatingConflict(card(photo("I.JPG", { rating: -1, ratingState: "rejected" }))), false);
});

test("RATING-01：来源记录归一：显式状态优先，未知状态按取值判定", () => {
    assert.deepEqual(readRatingRecord({ rating: 3 }), { state: "rated", value: 3 });
    assert.deepEqual(readRatingRecord({ rating: 0 }), { state: "rated", value: 0 });
    assert.deepEqual(readRatingRecord({ rating: -1 }), { state: "rejected", value: -1 });
    assert.deepEqual(readRatingRecord({ rating: null }), { state: "missing", value: null });
    assert.deepEqual(readRatingRecord({ rating: null, state: "invalid" }), { state: "invalid", value: null });
    assert.deepEqual(readRatingRecord({ rating: 9 }), { state: "invalid", value: null });
    assert.deepEqual(readRatingRecord({ rating: 2.5 }), { state: "invalid", value: null });
    assert.deepEqual(readRatingRecord({ rating: 4, state: "missing" }), { state: "missing", value: null });
});

test("RATING-05：显示星取代表内嵌，-1 与无效不点亮", () => {
    assert.equal(displayStars(photo("A.JPG", { rating: 4 })), 4);
    assert.equal(displayStars(photo("A.JPG", { rating: 0 })), 0);
    assert.equal(displayStars(photo("A.JPG", { rating: -1, ratingState: "rejected" })), 0);
    assert.equal(displayStars(photo("A.JPG", { rating: null, ratingState: "missing" })), 0);
    assert.equal(displayStars(photo("A.JPG", { rating: null, ratingState: "invalid" })), 0);
    assert.equal(displayStars(null), 0);
    assert.deepEqual(representativeRating(photo("A.JPG", { rating: -1 })), { state: "rejected", value: -1 });
});

test("FILTER-03：数字/以上按代表内嵌，已拒绝独立，冲突独立", () => {
    const zero = card(photo("A.JPG", { rating: 0 }));
    const missing = card(photo("A.JPG", { rating: null, ratingState: "missing" }));
    const rejected = card(photo("A.JPG", { rating: -1, ratingState: "rejected" }));
    const invalid = card(photo("A.JPG", { rating: null, ratingState: "invalid" }));
    const conflict = card(photo("C.JPG", { rating: 3 }), [photo("C.ARW", { rating: 5 })]);

    assert.equal(matchesRatingFilter(zero, "none"), true);
    assert.equal(matchesRatingFilter(missing, "none"), true);
    assert.equal(matchesRatingFilter(rejected, "none"), false);
    assert.equal(matchesRatingFilter(rejected, "rejected"), true);
    assert.equal(matchesRatingFilter(missing, "rejected"), false);
    assert.equal(matchesRatingFilter(invalid, "rejected"), false);
    assert.equal(matchesRatingFilter(invalid, "none"), false);
    for (const value of ["1", "2", "3", "4", "5", "1+", "2+", "3+", "4+"]) {
        assert.equal(matchesRatingFilter(invalid, value), false, `无效代表命中了 ${value}`);
    }
    assert.equal(matchesRatingFilter(conflict, "3"), true);
    assert.equal(matchesRatingFilter(conflict, "5"), false);
    assert.equal(matchesRatingFilter(conflict, "conflict"), true);
    assert.equal(matchesRatingFilter(zero, "conflict"), false);

    const list = listOf([
        photo("A.JPG", { rating: 0 }),
        photo("B.JPG", { rating: -1, ratingState: "rejected" }),
        photo("C.JPG", { rating: null, ratingState: "missing" }),
        photo("D.JPG", { rating: 3 }),
        photo("D.ARW", { rating: 5 }),
    ]);
    list.setRatingFilter("rejected");
    assert.deepEqual(list.cards.map((entry) => entry.key), ["f1/B.JPG"]);
    list.setRatingFilter("none");
    assert.deepEqual(list.cards.map((entry) => entry.key), ["f1/A.JPG", "f1/C.JPG"]);
    list.setRatingFilter("conflict");
    assert.deepEqual(list.cards.map((entry) => entry.key), ["f1/C.JPG", "f1/D.JPG"]);
});

test("RATING-06：无 canonical XMP 一律不支持新建（含 JPEG）", () => {
    assert.equal(ratingWriteBlockReason({ state: "rated", kind: "embedded" }), null);
    assert.match(ratingWriteBlockReason({ state: "missing", kind: "embedded" }), /无 canonical XMP 包，不支持新建/);
    assert.match(ratingWriteBlockReason({ state: "missing", kind: "sidecar" }), /无 canonical XMP 包，不支持新建/);
    assert.match(ratingWriteBlockReason({ state: "invalid", kind: "embedded" }), /无法解析/);
    assert.match(ratingWriteBlockReason({ state: "unreadable", kind: "embedded" }), /不可读/);
    assert.match(ratingWriteBlockReason({ state: "conflict", kind: "embedded" }), /不一致/);
});

test("RATING-06：PNG 内嵌来源整体挂账，sidecar 不受限", () => {
    for (const state of ["rated", "rejected", "missing", "invalid"]) {
        const source = { state, kind: "embedded", photo: { name: "A.PNG" }, label: "A.PNG" };
        assert.equal(ratingWriteBlockReason(source), "PNG 内嵌 XMP 写入不支持", `PNG ${state} 未被拒绝`);
    }
    assert.equal(ratingWriteBlockReason({ state: "rated", kind: "embedded", photo: { name: "A.JPG" } }), null);
    // sidecar 是独立来源，PNG 挂账只约束内嵌位置。
    assert.equal(ratingWriteBlockReason({ state: "rated", kind: "sidecar", photo: { name: "A.PNG" }, label: "A.xmp" }), null);
});

function writerHarness() {
    const clock = manualClock();
    const toasts = [];
    const writer = new RatingWriter({ clock, latency: 10, onToast: (text) => toasts.push(text) });
    return { clock, toasts, writer };
}

async function settle(clock, rounds = 16) {
    for (let index = 0; index < rounds; index += 1) {
        clock.advance(50);
        await flush();
    }
}

test("RATING-03：整组写入全部来源，不创建 sidecar", async () => {
    const { clock, toasts, writer } = writerHarness();
    const jpg = photo("A.JPG", { rating: 3 });
    const arw = photo("A.ARW", { rating: 5 });
    const result = writer.write(card(jpg, [arw]), 4);
    await settle(clock);
    assert.equal(await result, true);
    assert.equal(jpg.rating, 4);
    assert.equal(arw.rating, 4);
    assert.equal(jpg.ratingState, "rated");
    assert.equal(jpg.sidecar, undefined);
    assert.deepEqual(toasts, []);
    assert.equal(hasRatingConflict(card(jpg, [arw])), false);

    const sidecar = { rating: 3 };
    const jpg2 = photo("B.JPG", { rating: 3, sidecar });
    const arw2 = photo("B.ARW", { rating: 5, sidecar });
    const writing = writer.write(card(jpg2, [arw2]), 2);
    await settle(clock);
    assert.equal(await writing, true);
    assert.equal(sidecar.rating, 2);
    assert.equal(hasRatingConflict(card(jpg2, [arw2])), false);
});

test("RATING-03：预检失败不写任何来源，提示来源与原因", async () => {
    const { clock, toasts, writer } = writerHarness();
    const jpg = photo("A.JPG", { rating: 3, sidecar: { state: "invalid" } });
    const arw = photo("A.ARW", { rating: 5 });
    const result = writer.write(card(jpg, [arw]), 2);
    await settle(clock);
    assert.equal(await result, false);
    assert.equal(jpg.rating, 3);
    assert.equal(arw.rating, 5);
    assert.equal(clock.pendingTimers(), 0);
    assert.deepEqual(toasts, ["无法写入星级：A.xmp（XMP 包无法解析）"]);

    const missing = photo("B.ARW", { rating: null, ratingState: "missing" });
    const writing = writer.write(card(missing), 1);
    await settle(clock);
    assert.equal(await writing, false);
    assert.equal(missing.rating, null);
    assert.match(toasts.at(-1), /^无法写入星级：B\.ARW（无 canonical XMP 包，不支持新建）$/);

    // 缺包 JPEG 同样不可建：预检在首次修改前失败，不写任何字节。
    const jpegMissing = photo("C.JPG", { rating: null, ratingState: "missing" });
    const jpegWrite = writer.write(card(jpegMissing), 5);
    await settle(clock);
    assert.equal(await jpegWrite, false);
    assert.equal(jpegMissing.rating, null);
    assert.equal(jpegMissing.sidecar, undefined);
    assert.equal(toasts.at(-1), "无法写入星级：C.JPG（无 canonical XMP 包，不支持新建）");
});

test("RATING-06：PNG 组写入在预检失败，代表与伴侣零修改", async () => {
    const { clock, toasts, writer } = writerHarness();
    const png = photo("A.PNG", { rating: 3 });
    const arw = photo("A.ARW", { rating: 3 });
    const result = writer.write(card(png, [arw]), 4);
    await settle(clock);
    assert.equal(await result, false);
    assert.equal(png.rating, 3);
    assert.equal(arw.rating, 3);
    assert.equal(png.sidecar, undefined);
    assert.equal(clock.pendingTimers(), 0);
    assert.deepEqual(toasts, ["无法写入星级：A.PNG（PNG 内嵌 XMP 写入不支持）"]);

    // 全部来源已是目标值时仍按 RATING-03 不做物理写入（PNG 也不产生写入）。
    const noop = writer.write(card(photo("B.PNG", { rating: 2 })), 2);
    await flush();
    assert.equal(await noop, true);
    assert.equal(clock.pendingTimers(), 0);
});

test("RATING-03：全部来源已是目标值时不产生物理写入", async () => {
    const { clock, writer } = writerHarness();
    const jpg = photo("A.JPG", { rating: 2, sidecar: { rating: 2 } });
    const arw = photo("A.ARW", { rating: 2, sidecar: jpg.sidecar });
    const result = writer.write(card(jpg, [arw]), 2);
    await flush();
    assert.equal(await result, true);
    assert.equal(clock.pendingTimers(), 0);
});

function makeLibrary() {
    const bSidecar = { rating: 2 };
    const photos = [
        photo("A001.JPG", { rating: 3, sidecar: { rating: 3 }, id: "f1/A001.JPG" }),
        photo("A001.ARW", { rating: 5, sidecar: { rating: 3 }, id: "f1/A001.ARW" }),
        photo("B002.JPG", { rating: 2, sidecar: bSidecar, id: "f1/B002.JPG" }),
        photo("B002.ARW", { rating: 2, sidecar: bSidecar, id: "f1/B002.ARW" }),
        photo("C003.JPG", { rating: 3, sidecar: { state: "invalid" }, id: "f1/C003.JPG" }),
        photo("D004.JPG", { rating: 4, id: "f1/D004.JPG" }),
    ];
    for (const [index, entry] of photos.entries()) {
        entry.folderId = "f1";
        entry.taken = 1_000 + index * 1000;
        entry.size = 1_000_000 + index;
    }
    return {
        folders: [
            { id: "f1", name: "测试", photos, enumerable: true },
            { id: "empty", name: "空", photos: [], enumerable: true },
        ],
    };
}

function harness() {
    const clock = manualClock();
    const library = makeLibrary();
    const app = createApp({
        t,
        clock,
        library,
        decoder: {
            decode: async (entry, tier) => ({
                tier,
                width: 600,
                height: 400,
                surface: { kind: tier, photoId: entry.id },
            }),
            enhance: async (surface) => ({ kind: "enhanced", source: surface }),
        },
        store: { read: async () => null, compute: async () => null, extractGroup: async () => {} },
        settings: { appleKeyboard: false },
        dpr: 1,
        reducedMotion: () => true,
        measureText,
    });
    app.setWindowSize(1200, 850);
    return { app, clock, library };
}

test("OPEN-08：重复打开刷新来源状态与冲突指示", async () => {
    const { app, library } = harness();
    app.openFolder("f1", { target: "f1/B002.JPG" });
    await flush();
    assert.equal(app.state.toolbar.rating.conflict, false);
    app.setRatingFilter("conflict");
    const before = app.state.count;

    // 外部应用改了已有 sidecar：刷新后按磁盘真值呈现冲突，不沿用内存旧值。
    library.folders[0].photos[2].sidecar.rating = 1;
    app.openFolder("f1", { target: "f1/A001.JPG" });
    await flush();
    assert.equal(app.state.toolbar.rating.conflict, true);
    assert.equal(app.state.count, before + 1);
});

test("RATING-01：合成库覆盖 sidecar 来源与代表级独立状态", () => {
    const harbor = createLibrary().folders.find((entry) => entry.id === "harbor");
    assert.ok(harbor.photos.some((entry) => entry.sidecar && entry.sidecar.rating === 4), "缺少 sidecar 来源样本");
    assert.ok(harbor.photos.some((entry) => entry.ratingState === "rejected" && entry.rating === -1));
    assert.ok(harbor.photos.some((entry) => entry.ratingState === "invalid"));
    assert.ok(harbor.photos.some((entry) => entry.ratingState === "missing"));
    assert.ok(harbor.photos.some((entry) => entry.ratingState === "unreadable"));
    assert.ok(harbor.photos.some((entry) => entry.ratingState === "conflict"));

    const list = listOf(harbor.photos);
    assert.ok(list.cards.some((entry) => hasRatingConflict(entry)), "合成库没有冲突卡片");
    assert.ok(list.cards.some((entry) => entry.photo.sidecar && !hasRatingConflict(entry)),
        "缺少含 sidecar 且一致的组");
    assert.ok(ratingSources(list.cards.find((entry) => entry.photo.sidecar)).some((source) => source.kind === "sidecar"));
});
