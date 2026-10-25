import { test } from "node:test";
import assert from "node:assert/strict";
import { FileList, RATING_FILTERS, SORT_KEYS, SORT_ORDERS } from "../src/core/filter.js";
import { FORMATS } from "../src/core/settings.js";
import { createLibrary } from "../src/core/library.js";

function photo(name, options = {}) {
    const dot = name.lastIndexOf(".");
    return {
        id: options.id ?? `f/${name}`,
        folderId: "f",
        name,
        stem: options.stem ?? name.slice(0, dot),
        ext: options.ext ?? name.slice(dot).toLowerCase(),
        size: options.size ?? 1000,
        modified: "modified" in options ? options.modified : 0,
        taken: "taken" in options ? options.taken : null,
        tzOffset: 480,
        rating: options.rating ?? 0,
        width: 6000,
        height: 4000,
        orientation: 1,
        focus: null,
        exif: {
            aperture: 2.8,
            exposure: 1 / 250,
            iso: 400,
            focal: 35,
            equivFocal: 52,
            camera: "ILCE-7M4",
            lens: null,
            ev: 0,
            wb: "自动",
            flash: null,
        },
        shake: null,
        fingerprint: options.fingerprint ?? null,
        fault: {},
        scene: { base: 1, variant: 0 },
        extracted: false,
    };
}

const names = (list) => list.cards.map((card) => card.photo.name);

function makeList(photos, options = {}) {
    const list = new FileList({ formats: FORMATS, sameNameAsOne: true, ...options });
    if (photos) list.setPhotos(photos);
    return list;
}

test("FILTER-02：名称排序忽略大小写，同键值保持原文件顺序", () => {
    const list = makeList([photo("b.JPG"), photo("A.JPG"), photo("a.JPG"), photo("C.JPG")], { sameNameAsOne: false });
    assert.deepEqual(names(list), ["A.JPG", "a.JPG", "b.JPG", "C.JPG"]);
});

test("FILTER-02：排序键、方向与星级筛选取值固定", () => {
    assert.deepEqual(SORT_KEYS.map((entry) => [entry.value, entry.label]), [
        ["name", "名称"],
        ["taken", "拍摄时间"],
        ["size", "文件大小"],
    ]);
    assert.deepEqual(SORT_ORDERS.map((entry) => [entry.value, entry.label]), [
        ["asc", "升序 ↑"],
        ["desc", "降序 ↓"],
    ]);
    assert.deepEqual(RATING_FILTERS.map((entry) => entry.value), [
        "all", "none", "1", "2", "3", "4", "5", "1+", "2+", "3+", "4+", "rejected", "conflict",
    ]);
    assert.deepEqual(RATING_FILTERS.map((entry) => entry.label), [
        "全部", "无星级", "一星", "二星", "三星", "四星", "五星",
        "一星以上", "二星以上", "三星以上", "四星以上", "已拒绝", "星级冲突",
    ]);
    const list = makeList([]);
    assert.equal(list.sortKey, "name");
    assert.equal(list.sortOrder, "asc");
    assert.equal(list.ratingFilter, "all");
});

test("FILTER-02：换键保留方向，换方向保留键", () => {
    const list = makeList([photo("A.JPG"), photo("B.JPG")]);
    assert.equal(list.setSort("taken", "desc"), false);
    assert.equal(list.sortKey, "taken");
    assert.equal(list.sortOrder, "desc");
    list.setSort("size");
    assert.equal(list.sortKey, "size");
    assert.equal(list.sortOrder, "desc");
    list.setSort("size", "asc");
    assert.equal(list.sortOrder, "asc");
});

test("FILTER-02：拍摄时间缺失时回退文件修改时间", () => {
    const list = makeList([
        photo("a.JPG", { taken: 3000 }),
        photo("b.JPG", { taken: null, modified: 1000 }),
        photo("c.JPG", { taken: 2000 }),
        photo("d.JPG", { taken: null, modified: 4000 }),
    ]);
    list.setSort("taken", "asc");
    assert.deepEqual(names(list), ["b.JPG", "c.JPG", "a.JPG", "d.JPG"]);
});

test("FILTER-02：降序只翻转键值比较，同键值仍按原文件顺序", () => {
    const list = makeList([photo("a.JPG", { size: 10 }), photo("b.JPG", { size: 10 }), photo("c.JPG", { size: 5 })]);
    list.setSort("size", "asc");
    assert.deepEqual(names(list), ["c.JPG", "a.JPG", "b.JPG"]);
    list.setSort("size", "desc");
    assert.deepEqual(names(list), ["a.JPG", "b.JPG", "c.JPG"]);
});

test("FILTER-02：排序不改变成员与计数", () => {
    const list = makeList([photo("b.JPG"), photo("a.JPG"), photo("c.JPG")]);
    const before = list.count;
    list.setSort("size", "desc");
    list.setSort("name", "desc");
    assert.equal(list.count, before);
    assert.deepEqual(new Set(names(list)), new Set(["a.JPG", "b.JPG", "c.JPG"]));
});

test("FILTER-02：同名合并的显示名带伴侣格式，且不参与排序", () => {
    const list = makeList([photo("b.JPG"), photo("b.ARW"), photo("a.JPG")]);
    assert.equal(list.cardOf("f/b.JPG").displayName, "b.JPG(RAW)");
    assert.deepEqual(names(list), ["a.JPG", "b.JPG"]);
});

test("FILTER-02：重算保持当前照片在列表中的位置语义（排序只换位置）", () => {
    const list = makeList([photo("A.JPG"), photo("B.JPG"), photo("C.JPG")]);
    list.setCurrent("f/B.JPG");
    list.setSort("name", "desc");
    assert.deepEqual(names(list), ["C.JPG", "B.JPG", "A.JPG"]);
    assert.equal(list.cardIndexOf("f/B.JPG"), 1);
    assert.equal(list.neighbor("f/B.JPG", 1).photo.name, "A.JPG");
});

const RATING_CASES = [
    ["all", "全部", ["A", "B", "C", "D", "E", "F", "G"]],
    ["none", "无星级", ["A"]],
    ["1", "一星", ["B"]],
    ["2", "二星", ["C"]],
    ["3", "三星", ["D", "G"]],
    ["4", "四星", ["E"]],
    ["5", "五星", ["F"]],
    ["1+", "一星以上", ["B", "C", "D", "E", "F", "G"]],
    ["2+", "二星以上", ["C", "D", "E", "F", "G"]],
    ["3+", "三星以上", ["D", "E", "F", "G"]],
    ["4+", "四星以上", ["E", "F"]],
    ["conflict", "星级冲突", ["G"]],
];

function ratingFixture() {
    return [
        photo("A.JPG", { rating: 0 }),
        photo("B.JPG", { rating: 1 }),
        photo("C.JPG", { rating: 2 }),
        photo("D.JPG", { rating: 3 }),
        photo("E.JPG", { rating: 4 }),
        photo("F.JPG", { rating: 5 }),
        photo("G.JPG", { rating: 3 }),
        photo("G.ARW", { rating: 5 }),
    ];
}

for (const [value, label, expected] of RATING_CASES) {
    test(`FILTER-03：星级筛选「${label}」`, () => {
        const list = makeList(ratingFixture());
        const entry = RATING_FILTERS.find((item) => item.value === value);
        assert.equal(entry.label, label);
        list.setRatingFilter(value === "all" ? "none" : "all");
        assert.equal(list.setRatingFilter(value), true);
        assert.deepEqual(names(list), expected.map((name) => `${name}.JPG`));
        assert.equal(list.count, expected.length);
        assert.equal(list.ratingFilter, value);
    });
}

test("FILTER-03：同名合并关闭时星级冲突为空且不报错", () => {
    const list = makeList([photo("G.JPG", { rating: 3 }), photo("G.ARW", { rating: 5 })], { sameNameAsOne: false });
    assert.equal(list.setRatingFilter("conflict"), true);
    assert.equal(list.count, 0);
    assert.equal(list.hasRatingConflict(list.cardOf("f/G.JPG")), false);
    list.setFormatOptions({ sameNameAsOne: true });
    assert.equal(list.count, 1);
    assert.equal(list.hasRatingConflict(list.cardOf("f/G.JPG")), true);
});

test("FILTER-03：筛选不修改评分、不改变当前照片", () => {
    const data = ratingFixture();
    const list = makeList(data);
    list.setCurrent("f/D.JPG");
    list.setRatingFilter("5");
    assert.deepEqual(data.map((item) => item.rating), [0, 1, 2, 3, 4, 5, 3, 5]);
    assert.equal(list.neighbor("f/D.JPG", 0), null);
});

test("FILTER-04：计数含暂留项，列表与文本同一次刷新", () => {
    const data = [photo("A.JPG", { rating: 5 }), photo("B.JPG", { rating: 5 })];
    const list = makeList(data);
    list.setRatingFilter("5");
    list.setCurrent("f/A.JPG");
    assert.equal(list.count, 2);
    data[0].rating = 2;
    assert.equal(list.noteRatingWritten("f/A.JPG"), true);
    assert.equal(list.count, 2);
    assert.deepEqual(names(list), ["A.JPG", "B.JPG"]);
    assert.equal(list.cards.length, list.count);
    assert.equal(list.cardIndexOf("f/A.JPG"), 0);
});

test("FILTER-05：当前项被筛选排除后，翻页以重算前索引为参照落到最近前后项", () => {
    const list = makeList([
        photo("A.JPG", { rating: 1 }),
        photo("B.JPG", { rating: 5 }),
        photo("C.JPG", { rating: 2 }),
        photo("D.JPG", { rating: 3 }),
        photo("E.JPG", { rating: 5 }),
        photo("F.JPG", { rating: 1 }),
    ]);
    list.setCurrent("f/D.JPG");
    list.setRatingFilter("5");
    assert.deepEqual(names(list), ["B.JPG", "E.JPG"]);
    assert.equal(list.cardIndexOf("f/D.JPG"), -1);
    assert.equal(list.neighbor("f/D.JPG", 1).photo.name, "E.JPG");
    assert.equal(list.neighbor("f/D.JPG", -1).photo.name, "B.JPG");
});

test("FILTER-05：筛选结果端点的上一张/下一张不环绕", () => {
    const list = makeList([
        photo("A.JPG", { rating: 3 }),
        photo("B.JPG", { rating: 5 }),
        photo("C.JPG", { rating: 5 }),
    ]);
    list.setRatingFilter("5");
    assert.equal(list.neighbor("f/B.JPG", -1), null);
    assert.equal(list.neighbor("f/C.JPG", 1), null);
    assert.equal(list.neighbor("f/B.JPG", 1).photo.name, "C.JPG");
});

test("FILTER-05：锚点跟随当前照片，不沿用被替换照片的位置", () => {
    const list = makeList([
        photo("A.JPG", { rating: 5 }),
        photo("B.JPG", { rating: 5 }),
        photo("C.JPG", { rating: 5 }),
        photo("D.JPG", { rating: 1 }),
        photo("E.JPG", { rating: 5 }),
        photo("F.JPG", { rating: 1 }),
    ]);
    list.setCurrent("f/D.JPG");
    list.setRatingFilter("5");
    assert.deepEqual(names(list), ["A.JPG", "B.JPG", "C.JPG", "E.JPG"]);
    assert.equal(list.neighbor("f/D.JPG", 1).photo.name, "E.JPG");
    list.setCurrent("f/F.JPG");
    assert.equal(list.neighbor("f/F.JPG", -1).photo.name, "E.JPG");
    assert.equal(list.neighbor("f/F.JPG", 1), null);
});

test("FILTER-05：隐藏伴侣映射到代表卡片", () => {
    const list = makeList([
        photo("X.JPG", { rating: 3 }),
        photo("X.ARW", { rating: 3 }),
        photo("Y.JPG", { rating: 3 }),
        photo("Z.JPG", { rating: 3 }),
    ]);
    assert.deepEqual(names(list), ["X.JPG", "Y.JPG", "Z.JPG"]);
    assert.equal(list.cardIndexOf("f/X.ARW"), 0);
    assert.equal(list.cardOf("f/X.ARW").key, "f/X.JPG");
    list.setCurrent("f/X.ARW");
    assert.equal(list.neighbor("f/X.ARW", 1).photo.name, "Y.JPG");
    assert.equal(list.neighbor("f/X.ARW", -1), null);
});

test("FILM-06：当前照片改星落出筛选后暂留，切走后移除", () => {
    const data = [photo("A.JPG", { rating: 5 }), photo("B.JPG", { rating: 5 }), photo("C.JPG", { rating: 3 })];
    const list = makeList(data);
    list.setRatingFilter("5");
    list.setCurrent("f/A.JPG");
    assert.deepEqual(names(list), ["A.JPG", "B.JPG"]);

    data[0].rating = 2;
    assert.equal(list.noteRatingWritten("f/A.JPG"), true);
    assert.deepEqual(names(list), ["A.JPG", "B.JPG"]);
    assert.equal(list.count, 2);
    assert.equal(list.cardIndexOf("f/A.JPG"), 0);

    list.setCurrent("f/B.JPG");
    assert.deepEqual(names(list), ["B.JPG"]);
    assert.equal(list.count, 1);
});

test("FILM-06：非当前照片改星按筛选正常进出", () => {
    const data = [photo("A.JPG", { rating: 5 }), photo("B.JPG", { rating: 5 })];
    const list = makeList(data);
    list.setRatingFilter("5");
    list.setCurrent("f/B.JPG");
    data[0].rating = 1;
    list.noteRatingWritten("f/A.JPG");
    assert.deepEqual(names(list), ["B.JPG"]);
    data[0].rating = 5;
    list.noteRatingWritten("f/A.JPG");
    assert.deepEqual(names(list), ["A.JPG", "B.JPG"]);
});

test("FILM-06：暂留项在重新命中筛选后不再保留", () => {
    const data = [photo("A.JPG", { rating: 5 }), photo("B.JPG", { rating: 5 })];
    const list = makeList(data);
    list.setRatingFilter("5");
    list.setCurrent("f/A.JPG");
    data[0].rating = 4;
    list.noteRatingWritten("f/A.JPG");
    assert.equal(list.count, 2);
    data[0].rating = 5;
    list.noteRatingWritten("f/A.JPG");
    assert.equal(list.count, 2);
    // 暂留标记已清除：之后由用户改筛选排除时不得继续留在列表
    list.setRatingFilter("4");
    assert.equal(list.count, 0);
});

test("FILTER-10：空结果计数为 0，筛选条件不被自动放宽", () => {
    const empty = new FileList({ formats: FORMATS, sameNameAsOne: true });
    empty.setPhotos([]);
    assert.equal(empty.count, 0);
    assert.deepEqual(empty.cards, []);
    assert.equal(empty.firstCard(), null);
    assert.equal(empty.setRatingFilter("none"), false);
    assert.equal(empty.ratingFilter, "none");
    assert.equal(empty.count, 0);

    const list = makeList([photo("A.JPG", { rating: 0 }), photo("B.JPG", { rating: 0 })]);
    list.setRatingFilter("5");
    assert.equal(list.count, 0);
    assert.equal(list.ratingFilter, "5");
    assert.equal(list.firstCard().photo.name, "A.JPG");
});

test("OPEN-02：隐藏文件与停用格式不参与列表", () => {
    const list = makeList([
        photo(".hidden.JPG"),
        photo("A.TIF"),
        photo("A.JPG"),
        photo("A.ARW"),
        photo("B.PNG"),
    ]);
    assert.deepEqual(names(list), ["A.JPG", "B.PNG"]);
    assert.equal(list.groups.length, 2);
    assert.equal(list.cardIndexOf("f/.hidden.JPG"), -1);
    assert.equal(list.cardIndexOf("f/A.TIF"), -1);
    assert.equal(list.cardOf("f/A.ARW").key, "f/A.JPG");
});

test("OPEN-02：格式优先级决定同名组的代表文件", () => {
    const list = makeList([photo("A.JPG"), photo("A.ARW")]);
    assert.equal(list.cards.length, 1);
    assert.equal(list.cards[0].key, "f/A.JPG");
    assert.equal(list.cards[0].displayName, "A.JPG(RAW)");

    const raw = FORMATS.find((format) => format.id === "RAW");
    const reordered = [raw, ...FORMATS.filter((format) => format.id !== "RAW")];
    assert.equal(list.setFormatOptions({ formats: reordered }), true);
    assert.equal(list.cards[0].key, "f/A.ARW");
    assert.equal(list.cards[0].displayName, "A.ARW(JPG)");
});

test("OPEN-02：同名合并开关即时重算且保留排序与星级筛选", () => {
    const list = makeList([photo("A.JPG", { rating: 3 }), photo("A.ARW", { rating: 5 })]);
    list.setSort("size", "desc");
    list.setRatingFilter("conflict");
    assert.equal(list.count, 1);
    assert.equal(list.setFormatOptions({ sameNameAsOne: false }), true);
    assert.equal(list.count, 0);
    assert.equal(list.sortKey, "size");
    assert.equal(list.sortOrder, "desc");
    assert.equal(list.ratingFilter, "conflict");
    list.setFormatOptions({ sameNameAsOne: true });
    assert.equal(list.count, 1);
});

test("OPEN-02：替换文件夹保留排序与星级筛选", () => {
    const list = makeList([photo("A.JPG", { rating: 3 }), photo("B.JPG", { rating: 5 })]);
    list.setSort("size", "desc");
    list.setRatingFilter("5");
    const next = [photo("C.JPG", { rating: 5, size: 10 }), photo("D.JPG", { rating: 5, size: 30 })];
    assert.equal(list.setPhotos(next), true);
    assert.equal(list.sortKey, "size");
    assert.equal(list.sortOrder, "desc");
    assert.equal(list.ratingFilter, "5");
    assert.deepEqual(names(list), ["D.JPG", "C.JPG"]);
    assert.equal(list.groups.length, 2);
});

test("OPEN-02：设置项未变化时不算列表变化", () => {
    const list = makeList([photo("A.JPG"), photo("B.ARW")]);
    const before = list.version;
    const formats = FORMATS.map((format) => ({ ...format, exts: [...format.exts] }));
    assert.equal(list.setFormatOptions({ formats }), false);
    assert.equal(list.version, before);
});

test("OPEN-04：首图为筛选后首项，筛选为空回退枚举首项的代表", () => {
    const list = makeList([
        photo(".hidden.JPG", { rating: 5 }),
        photo("A.JPG", { rating: 0 }),
        photo("A.ARW", { rating: 0 }),
        photo("B.JPG", { rating: 5 }),
    ]);
    list.setRatingFilter("5");
    assert.equal(list.firstCard().photo.name, "B.JPG");
    list.setRatingFilter("4");
    assert.equal(list.count, 0);
    assert.equal(list.firstCard().photo.name, "A.JPG");
    assert.equal(list.firstCard().key, "f/A.JPG");
});

test("FILTER-04：version 只在列表内容或顺序变化时递增", () => {
    const list = new FileList({ formats: FORMATS, sameNameAsOne: true });
    const start = list.version;
    assert.equal(list.setPhotos([photo("B.JPG"), photo("A.JPG")]), true);
    assert.equal(list.version, start + 1);
    const afterSet = list.version;
    assert.equal(list.setSort("name", "asc"), false);
    assert.equal(list.version, afterSet);
    assert.equal(list.setSort("name", "desc"), true);
    assert.equal(list.version, afterSet + 1);

    const single = makeList([photo("A.JPG")]);
    const singleVersion = single.version;
    assert.equal(single.setSort("size", "desc"), false);
    assert.equal(single.version, singleVersion);
});

test("OPEN-02：海港按默认格式与同名合并得到 65 张卡片", () => {
    const harbor = createLibrary().folders[0];
    const list = makeList(harbor.photos);
    assert.equal(list.count, 65);
    assert.equal(list.groups.length, 65);
    assert.ok(list.cards.every((card) => !card.photo.name.startsWith(".")));
    assert.ok(list.cards.every((card) => card.photo.ext !== ".tif"));
    assert.equal(list.cards.filter((card) => card.companions.length > 0).length, 25);
});
