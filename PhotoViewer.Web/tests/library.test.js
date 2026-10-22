import { test } from "node:test";
import assert from "node:assert/strict";
import { createLibrary, generateFolder } from "../src/core/library.js";

const library = createLibrary();
const folder = (id) => library.folders.find((entry) => entry.id === id);

function groupBy(list, keyOf) {
    const groups = new Map();
    for (const item of list) {
        const key = keyOf(item);
        const group = groups.get(key) ?? [];
        group.push(item);
        groups.set(key, group);
    }
    return groups;
}

test("OPEN-02：合成库包含海港、夜景、空文件夹与共享照片", () => {
    assert.deepEqual(library.folders.map((entry) => entry.id), ["harbor", "night", "empty", "single"]);
    assert.equal(folder("harbor").name, "2026-10-12 海港");
    assert.equal(folder("night").name, "城市夜景");
    assert.equal(folder("empty").name, "空文件夹");
    assert.equal(folder("empty").photos.length, 0);
    assert.equal(folder("empty").enumerable, true);
});

test("OPEN-02：海港约 90 个可见文件，含 25 组同名对、HEIF、PNG、停用 TIFF 与隐藏文件", () => {
    const harbor = folder("harbor");
    assert.equal(harbor.enumerable, true);
    assert.equal(harbor.photos.length, 92);
    assert.equal(harbor.photos.filter((photo) => !photo.name.startsWith(".")).length, 91);
    assert.equal(harbor.photos.filter((photo) => photo.name.startsWith(".")).length, 1);
    assert.equal(harbor.photos.filter((photo) => photo.ext === ".tif").length, 1);
    assert.equal(harbor.photos.filter((photo) => photo.ext === ".hif").length, 6);
    assert.equal(harbor.photos.filter((photo) => photo.ext === ".png").length, 2);

    const stems = groupBy(harbor.photos.filter((photo) => !photo.name.startsWith(".")), (photo) => photo.stem);
    const pairs = [...stems.values()].filter(
        (group) => group.some((photo) => photo.ext === ".jpg") && group.some((photo) => photo.ext === ".arw"),
    );
    assert.equal(pairs.length, 25);
});

test("OPEN-02：扩展名大小写混用且 ext 一律小写含点", () => {
    const harbor = folder("harbor");
    for (const suffix of [".JPG", ".jpg", ".ARW", ".arw", ".HIF", ".hif", ".PNG", ".png"]) {
        assert.ok(harbor.photos.some((photo) => photo.name.endsWith(suffix)), `缺少 ${suffix}`);
    }
    for (const entry of library.folders) {
        for (const photo of entry.photos) {
            assert.equal(photo.ext, photo.ext.toLowerCase());
            assert.equal(photo.name.slice(photo.stem.length).toLowerCase(), photo.ext);
            assert.equal(photo.id, `${entry.id}/${photo.name}`);
            assert.equal(photo.folderId, entry.id);
        }
    }
});

test("OPEN-03：共享照片文件夹不可枚举，只有一张 HEIF", () => {
    const single = folder("single");
    assert.equal(single.enumerable, false);
    assert.equal(single.photos.length, 1);
    assert.equal(single.photos[0].ext, ".heic");
    assert.ok(single.photos[0].name.endsWith(".HEIC"));
    assert.ok(folder("night").enumerable);
});

test("SIM-02：海港约六成照片已提取，同指纹组一致", () => {
    const harbor = folder("harbor");
    const ratio = harbor.photos.filter((photo) => photo.extracted).length / harbor.photos.length;
    assert.ok(ratio > 0.5 && ratio < 0.72, `提取比例 ${ratio}`);

    const groups = groupBy(harbor.photos.filter((photo) => photo.fingerprint), (photo) => photo.fingerprint);
    assert.ok(groups.size > 20);
    for (const group of groups.values()) {
        assert.ok(group.every((photo) => photo.extracted === group[0].extracted));
        assert.ok(group.every((photo) => photo.shake === group[0].shake));
        assert.ok(group.every((photo) => photo.scene.base === group[0].scene.base));
    }
});

test("SIM-02：缺拍摄时间的三张没有指纹并计入未提取", () => {
    const missing = folder("harbor").photos.filter((photo) => photo.taken === null);
    assert.equal(missing.length, 3);
    for (const photo of missing) {
        assert.equal(photo.fingerprint, null);
        assert.equal(photo.extracted, false);
        assert.ok(photo.modified > 0);
    }
});

test("FILM-05：故障注入覆盖全部解码失败、全分辨率失败与写回失败", () => {
    const harbor = folder("harbor");
    assert.equal(harbor.photos.filter((photo) => photo.fault.decode === "all").length, 1);
    assert.equal(harbor.photos.filter((photo) => photo.fault.decode === "full").length, 1);
    assert.equal(harbor.photos.filter((photo) => photo.fault.write === true).length, 1);
    assert.ok(harbor.photos.every((photo) => photo.fault !== undefined));
});

test("FILTER-02：海港星级覆盖 0–5 且存在星级冲突的同名组", () => {
    const harbor = folder("harbor");
    const counts = new Map();
    for (const photo of harbor.photos) counts.set(photo.rating, (counts.get(photo.rating) ?? 0) + 1);
    for (let rating = 0; rating <= 5; rating++) assert.ok((counts.get(rating) ?? 0) > 0, `缺少 ${rating} 星`);

    const stems = groupBy(harbor.photos.filter((photo) => !photo.name.startsWith(".")), (photo) => photo.stem);
    const conflicts = [...stems.values()].filter(
        (group) => group.length > 1 && new Set(group.map((photo) => photo.rating)).size > 1,
    );
    assert.ok(conflicts.length >= 3 && conflicts.length <= 8, `冲突组 ${conflicts.length}`);
});

test("FILM-05：抖动状态 true/false/null 混合，约七成带索尼对焦框", () => {
    const harbor = folder("harbor");
    const shakes = new Set(harbor.photos.map((photo) => photo.shake));
    assert.ok(shakes.has(true));
    assert.ok(shakes.has(false));
    assert.ok(shakes.has(null));
    const focusRatio = harbor.photos.filter((photo) => photo.focus).length / harbor.photos.length;
    assert.ok(focusRatio > 0.6 && focusRatio < 0.85, `对焦框比例 ${focusRatio}`);
    for (const photo of harbor.photos) {
        if (!photo.focus) continue;
        const { x, y, w, h } = photo.focus;
        assert.ok(w > 0 && h > 0 && x >= 0 && y >= 0 && x + w <= 1 && y + h <= 1);
    }
});

test("OPEN-04：方向码覆盖 1/3/6/8，横竖幅与存储朝向混合", () => {
    const harbor = folder("harbor");
    const orientations = new Set(harbor.photos.map((photo) => photo.orientation));
    for (const code of [1, 3, 6, 8]) assert.ok(orientations.has(code), `缺少方向码 ${code}`);
    const dims = new Set(harbor.photos.map((photo) => `${photo.width}x${photo.height}`));
    assert.ok(dims.has("6000x4000"));
    assert.ok(dims.has("4000x6000"));
});

test("PRIN-06：连拍组 3–6 张共享 scene.base", () => {
    const harbor = folder("harbor");
    const scenes = groupBy(harbor.photos, (photo) => photo.scene.base);
    const bursts = [...scenes.values()].filter((group) => group.length >= 3 && group.length <= 6);
    assert.ok(bursts.length >= 4, `连拍组 ${bursts.length}`);
    for (const group of bursts) {
        const stems = new Set(group.map((photo) => photo.stem));
        const variants = new Set(group.map((photo) => photo.scene.variant));
        assert.equal(variants.size, stems.size);
    }
});

test("FILM-05：EXIF 数值落在真实范围", () => {
    for (const photo of folder("harbor").photos) {
        const { aperture, exposure, iso, focal, equivFocal } = photo.exif;
        assert.ok(aperture >= 1.4 && aperture <= 11);
        assert.ok(exposure >= 1 / 8000 && exposure <= 2);
        assert.ok(iso >= 100 && iso <= 12800);
        assert.ok(focal >= 16 && focal <= 200);
        assert.equal(equivFocal, Math.round(focal * 1.5));
        assert.ok(photo.size > 1024 * 1024);
    }
    assert.ok(folder("harbor").photos.some((photo) => photo.exif.lens === null));
});

test("SIM-02：夜景三十张 JPG 全部已提取且含连拍组", () => {
    const night = folder("night");
    assert.equal(night.photos.length, 30);
    assert.ok(night.photos.every((photo) => photo.ext === ".jpg"));
    assert.ok(night.photos.every((photo) => photo.extracted));
    assert.ok(night.photos.every((photo) => photo.taken !== null));
    const scenes = groupBy(night.photos, (photo) => photo.scene.base);
    assert.ok([...scenes.values()].filter((group) => group.length >= 3).length >= 2);
});

test("确定性：两次 createLibrary() 完全相同", () => {
    assert.deepEqual(createLibrary(), createLibrary());
});

test("确定性：同种子 generateFolder 完全相同，换种子不同", () => {
    const first = generateFolder({ id: "gen", name: "生成", count: 120, seed: 7 });
    const second = generateFolder({ id: "gen", name: "生成", count: 120, seed: 7 });
    const other = generateFolder({ id: "gen", name: "生成", count: 120, seed: 8 });
    assert.deepEqual(first, second);
    assert.notDeepEqual(first, other);
});

test("OPEN-02：generateFolder 生成任意张数的 JPG/RAW 组合", () => {
    for (const count of [1, 0, 37, 1000]) {
        const generated = generateFolder({ id: "gen", name: "生成", count, seed: 3 });
        assert.equal(generated.photos.length, count);
        assert.equal(generated.enumerable, true);
        assert.ok(generated.photos.every((photo) => photo.ext === ".jpg" || photo.ext === ".arw"));
        assert.ok(generated.photos.every((photo) => photo.id === `gen/${photo.name}`));
    }
});

test("PRIN-06：generateFolder 的同名对共享指纹、场景与提取状态", () => {
    const generated = generateFolder({ id: "gen", name: "生成", count: 120, seed: 7 });
    const stems = groupBy(generated.photos, (photo) => photo.stem);
    const pairs = [...stems.values()].filter((group) => group.length === 2);
    assert.ok(pairs.length >= 10);
    for (const group of pairs) {
        assert.equal(group[0].fingerprint, group[1].fingerprint);
        assert.equal(group[0].scene.base, group[1].scene.base);
        assert.equal(group[0].extracted, group[1].extracted);
        assert.equal(group[0].taken, group[1].taken);
    }
});
