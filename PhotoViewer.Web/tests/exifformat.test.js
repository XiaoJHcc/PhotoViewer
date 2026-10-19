import { test } from "node:test";
import assert from "node:assert/strict";
import {
    formatAperture,
    formatShutter,
    formatIso,
    formatFocal,
    formatEv,
    formatTakenShort,
    formatTakenFull,
    formatExifValue,
} from "../src/core/exifformat.js";

function photo(overrides = {}) {
    return {
        name: "DSC0123.JPG",
        taken: Date.UTC(2026, 9, 12, 5, 40, 30),
        tzOffset: 480,
        exif: {
            aperture: 2.8,
            exposure: 1 / 250,
            iso: 400,
            focal: 50,
            equivFocal: 75,
            camera: "ILCE-7M4",
            lens: "FE 50mm F1.2 GM",
            ev: -0.7,
            wb: "日光",
            flash: "未闪光",
        },
        ...overrides,
    };
}

test("TOOLBAR-05：光圈去尾零，缺失写 --", () => {
    assert.equal(formatAperture(2.8), "f/2.8");
    assert.equal(formatAperture(4), "f/4");
    assert.equal(formatAperture(1.4), "f/1.4");
    assert.equal(formatAperture(11), "f/11");
    assert.equal(formatAperture(null), "--");
    assert.equal(formatAperture(0), "--");
});

test("TOOLBAR-05：快门不足 1 秒写 1/n，1 秒以上写秒数", () => {
    assert.equal(formatShutter(1 / 250), "1/250");
    assert.equal(formatShutter(1 / 8000), "1/8000");
    assert.equal(formatShutter(0.5), "1/2");
    assert.equal(formatShutter(1 / 3), "1/3");
    assert.equal(formatShutter(1), "1s");
    assert.equal(formatShutter(2), "2s");
    assert.equal(formatShutter(2.5), "2.5s");
    assert.equal(formatShutter(null), "--");
    assert.equal(formatShutter(0), "--");
});

test("TOOLBAR-05：ISO、焦距与曝光补偿按摄影习惯格式化", () => {
    assert.equal(formatIso(400), "400");
    assert.equal(formatIso(12800), "12800");
    assert.equal(formatIso(undefined), "--");
    assert.equal(formatFocal(14), "14mm");
    assert.equal(formatFocal(35), "35mm");
    assert.equal(formatFocal(null), "--");
    assert.equal(formatEv(0), "0 EV");
    assert.equal(formatEv(0.7), "+0.7 EV");
    assert.equal(formatEv(-1.3), "-1.3 EV");
    assert.equal(formatEv(null), "--");
});

test("FILM-05 / MENU-05：拍摄时间短格式 MM-dd HH:mm、完整格式 yyyy-MM-dd HH:mm:ss zzz", () => {
    const taken = Date.UTC(2026, 9, 12, 5, 40, 30);
    assert.equal(formatTakenShort(taken, 480), "10-12 13:40");
    assert.equal(formatTakenFull(taken, 480), "2026-10-12 13:40:30 +08:00");
    assert.equal(formatTakenFull(taken, -300), "2026-10-12 00:40:30 -05:00");
    assert.equal(formatTakenFull(taken, 0), "2026-10-12 05:40:30 +00:00");
    assert.equal(formatTakenShort(null, 480), "--");
    assert.equal(formatTakenFull(undefined, 480), "--");
});

test("TOOLBAR-05：EXIF 项按 id 取值，字符串缺失与未知项一律 --", () => {
    const target = photo();
    assert.equal(formatExifValue("aperture", target), "f/2.8");
    assert.equal(formatExifValue("exposure", target), "1/250");
    assert.equal(formatExifValue("iso", target), "400");
    assert.equal(formatExifValue("equivFocal", target), "75mm");
    assert.equal(formatExifValue("focal", target), "50mm");
    assert.equal(formatExifValue("camera", target), "ILCE-7M4");
    assert.equal(formatExifValue("lens", target), "FE 50mm F1.2 GM");
    assert.equal(formatExifValue("taken", target), "2026-10-12 13:40:30 +08:00");
    assert.equal(formatExifValue("ev", target), "-0.7 EV");
    assert.equal(formatExifValue("wb", target), "日光");
    assert.equal(formatExifValue("flash", target), "未闪光");
    assert.equal(formatExifValue("unknown", target), "--");
    assert.equal(formatExifValue("aperture", null), "--");

    const missing = photo({ exif: {}, taken: null });
    for (const id of ["aperture", "exposure", "iso", "equivFocal", "focal", "camera", "lens", "taken", "ev", "wb", "flash"]) {
        assert.equal(formatExifValue(id, missing), "--", id);
    }
});
