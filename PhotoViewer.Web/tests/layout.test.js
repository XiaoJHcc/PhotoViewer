import { test } from "node:test";
import assert from "node:assert/strict";
import { createTokens } from "../src/core/tokens.js";
import { computeLayout, approximateTextWidth } from "../src/core/layout/index.js";
import { resolveScreenForm, normalizeSafeArea, COMPACT_WIDTH } from "../src/core/layout/form.js";
import { computeControlBar, groupMainLength, ratingMainLength } from "../src/core/layout/controlbar.js";
import { analysisTileSize, analysisCrossSize, analysisGeometry } from "../src/core/layout/analysis.js";
import { columnSimilarity, rowSimilarityWidth, rowSimilarityHeight } from "../src/core/layout/similarity.js";
import { filmContentLength, filmItemFrame, filmCenterOffset } from "../src/core/layout/film.js";
import { cardFrames, smallBadgeFrame } from "../src/core/layout/card.js";
import {
    controlRatingPopoverDirection,
    placeRatingSources,
    ratingSourcesHeight,
    ratingSourcesMetrics,
    resolveRatingSourcesDirection,
} from "../src/core/layout/ratingpopover.js";

const t = createTokens("web");
const mac = createTokens("mac");

const scene = {
    width: 1200,
    height: 850,
    layoutMode: "col",
    safeArea: { top: 0, right: 0, bottom: 0, left: 0 },
    showFilmbar: true,
    showControlbar: true,
    showAnalysis: false,
    similarityExpanded: false,
    similarityItemCount: 0,
    photoCount: 12,
    countText: "共 12 张",
    exifCount: 4,
    showRating: true,
    commandCount: 6,
    analysisTileCount: 6,
    hasPhoto: true,
};

function layout(overrides = {}, tokens = t, profile = "web") {
    return computeLayout({ ...scene, ...overrides }, { t: tokens, profile, measureText: approximateTextWidth });
}

const countWidth = approximateTextWidth("共 12 张", 12);

test("SHELL-07：自动 <600 映射紧凑横向，显式行/列恒宽，手机模式可检验", () => {
    assert.equal(COMPACT_WIDTH, 600);
    assert.deepEqual(resolveScreenForm({ width: 590, height: 800, layoutMode: "auto" }),
        { form: "phone", orientation: "portrait", mode: "phone", compact: true, reason: "compact-width" });
    assert.equal(resolveScreenForm({ width: 590, height: 800, layoutMode: "auto" }).orientation, "portrait");
    assert.equal(resolveScreenForm({ width: 800, height: 590, layoutMode: "auto" }).form, "wide");
    assert.equal(resolveScreenForm({ width: 800, height: 590, layoutMode: "auto" }).mode, "col");
    assert.equal(resolveScreenForm({ width: 390, height: 844, layoutMode: "row" }).form, "wide");
    assert.equal(resolveScreenForm({ width: 844, height: 390, layoutMode: "phone" }).orientation, "landscape");
    assert.equal(layout({ width: 390, height: 844, layoutMode: "auto" }).form, "phone");
    assert.equal(layout({ width: 600, height: 800, layoutMode: "auto" }).form, "wide");
});

test("SHELL-10：安全区只归一方向；横屏忽略底部手势条，刘海取较大侧", () => {
    assert.deepEqual(normalizeSafeArea({ top: 59, right: 0, bottom: 34, left: 0 }, "portrait"), {
        top: 59, right: 0, bottom: 34, left: 0, notchSide: "left", notchInset: 0, source: "env",
    });
    const landscape = normalizeSafeArea({ top: 0, right: 0, bottom: 34, left: 59 }, "landscape");
    assert.equal(landscape.bottom, 0);
    assert.equal(landscape.notchSide, "left");
    assert.equal(landscape.notchInset, 59);
    assert.equal(normalizeSafeArea({ left: 20, right: 20 }, "landscape").notchSide, "left");
    assert.equal(normalizeSafeArea({ left: 10, right: 40 }, "landscape").notchSide, "right");
});

test("SHELL-01：宽形态主图铺满窗口，适应区独立随分区收缩", () => {
    const col = layout();
    assert.deepEqual(col.panes.photoView, { x: 0, y: 0, w: 1200, h: 850 });
    assert.deepEqual(col.panes.fitRect, { x: 117, y: 0, w: 1018, h: 850 });
    assert.deepEqual(col.panes.filePane, { x: 0, y: 0, w: 116, h: 850 });
    assert.deepEqual(col.panes.fileDivider, { x: 116, y: 0, w: 1, h: 850 });
    assert.deepEqual(col.panes.controlPane, { x: 1136, y: 0, w: 64, h: 850 });
    assert.deepEqual(col.dividers.map((entry) => entry.kind), ["file", "tool"]);

    const noControl = layout({ showControlbar: false });
    assert.equal(noControl.panes.controlPane.w, 0);
    assert.deepEqual(noControl.panes.fitRect, { x: 117, y: 0, w: 1083, h: 850 });
    assert.deepEqual(noControl.dividers.map((entry) => entry.kind), ["file"]);

    const noFile = layout({ showFilmbar: false });
    assert.equal(noFile.panes.filePane.w, 0);
    assert.equal(noFile.filter, null);
    assert.equal(noFile.film, null);
    assert.deepEqual(noFile.panes.fitRect, { x: 0, y: 0, w: 1135, h: 850 });

    const row = layout({ layoutMode: "row" });
    assert.equal(row.panes.filePane.h, row.filterBandHeight + t("film.band.height"));
    assert.equal(row.panes.filePane.h, 64 + 159);
    assert.equal(row.film.scroll.y, 64);
    assert.deepEqual(row.panes.fitRect, { x: 0, y: 224, w: 1200, h: 561 });
});

test("FILTER-07：iPad 96 合体两段各 48，列布局与卡片末端同缘，AI 开关独立", () => {
    const col = layout().filter;
    assert.deepEqual(col.composite, { x: 10, y: 10, w: 96, h: 44 });
    assert.deepEqual(col.entry, { x: 10, y: 10, w: 48, h: 44 });
    assert.deepEqual(col.more, { x: 58, y: 10, w: 48, h: 44 });
    assert.deepEqual(col.toggle, { x: 10, y: 62, w: 96, h: 44 });
    assert.equal(col.bar.h, 116);
    assert.equal(layout().film.lead, 116);
    assert.deepEqual(layout().film.items[0], { x: 10, y: 116, w: 96, h: 144 });

    const row = layout({ layoutMode: "row" }).filter;
    assert.deepEqual(row.entry, { x: 500, y: 10, w: 48, h: 44 });
    assert.deepEqual(row.more, { x: 548, y: 10, w: 48, h: 44 });
    assert.deepEqual(row.toggle, { x: 604, y: 10, w: 96, h: 44 });
    assert.equal(row.bar.h, 64);

    const standard = layout({ showSimilarity: false, layoutMode: "row" });
    assert.equal(standard.filter.toggle, null);
    assert.equal(standard.filter.composite.x, (1200 - 96) / 2);
    assert.equal(standard.filter.bar.h, 64);
});

test("TOOLBAR-02/03/05：EXIF 52×44、评分 224×44、命令 44，整组换行/居中", () => {
    assert.equal(groupMainLength("exif", 4, "horizontal", t), 2 * 6 + 4 * 52 + 3 * 2);
    assert.equal(groupMainLength("exif", 4, "vertical", t), 4 * 36 + 3 * 2);
    assert.equal(ratingMainLength(t), 224);
    assert.equal(groupMainLength("command", 6, "horizontal", t), 6 * 44 + 5 * 8);

    const col = layout().control;
    assert.deepEqual(col.document, { x: 0, y: 0, w: 64, h: 850 });
    assert.deepEqual(col.groups.map((group) => [group.kind, group.x, group.y, group.w, group.h]), [
        ["exif", 10, 76, 44, 150],
        ["rating", 10, 236, 44, 224],
        ["command", 10, 470, 44, 304],
    ]);
    assert.deepEqual(col.groups[0].children[0], { kind: "exif", index: 0, x: 0, y: 0, w: 44, h: 36 });
    assert.deepEqual(col.groups[1].children[0], { kind: "rating", index: 0, x: 0, y: 184, w: 44, h: 36 });
    assert.deepEqual(col.groups[1].children[5], { kind: "rating", index: 5, x: 0, y: 4, w: 44, h: 36 });

    const rowControl = layout({ layoutMode: "row" }).control;
    assert.deepEqual(rowControl.groups.map((group) => [group.kind, group.x, group.y, group.w, group.h]), [
        ["exif", 213, 10, 226, 44],
        ["rating", 449, 10, 224, 44],
        ["command", 683, 10, 304, 44],
    ]);
    assert.deepEqual(rowControl.groups[0].children[0], { kind: "exif", index: 0, x: 6, y: 0, w: 52, h: 44 });
    assert.deepEqual(rowControl.groups[0].children[3], { kind: "exif", index: 3, x: 168, y: 0, w: 52, h: 44 });

    const wrapped = layout({ width: 700, height: 480, layoutMode: "row" }).control;
    assert.equal(wrapped.lines.length, 2);
    assert.deepEqual(wrapped.lines.map((line) => line.length), [460, 304]);
    assert.equal(wrapped.document.h, 118);
    assert.equal(wrapped.groups[2].x, 198);
    assert.equal(wrapped.groups[2].y, 64);
});

test("TOOLBAR-02/08：单组超长从 padding 起排并标记溢出，不裁切", () => {
    const control = computeControlBar({ axis: "horizontal", mainSize: 1200, exifCount: 0, showRating: true, commandCount: 30 }, t);
    assert.equal(control.groups[1].x, 10);
    assert.equal(control.groups[1].w, 30 * 44 + 29 * 8);
    assert.equal(control.overflow, true);
    assert.equal(control.document.w, 10 + (30 * 44 + 29 * 8) + 10);
});

test("TOOLBAR-01/09：空分组移除；三组全空只剩四周 padding", () => {
    const empty = computeControlBar({ axis: "vertical", mainSize: 850, exifCount: 0, showRating: false, commandCount: 0 }, t);
    assert.equal(empty.empty, true);
    assert.deepEqual(empty.document, { x: 0, y: 0, w: 20, h: 20 });
    assert.deepEqual(empty.groups, []);
    assert.equal(layout({ exifCount: 0, showRating: false, commandCount: 0 }).panes.controlPane.w, 20);

    const noExif = layout({ exifCount: 0 });
    assert.deepEqual(noExif.control.groups.map((group) => group.kind), ["rating", "command"]);
    const noRating = layout({ showRating: false });
    assert.deepEqual(noRating.control.groups.map((group) => group.kind), ["exif", "command"]);
});

test("SHELL-12：分析与控制相邻时工具区共用 8 点接缝，交叉轴取单一材质宿主", () => {
    const both = layout({ showAnalysis: true, analysisTileCount: 6 });
    assert.equal(both.analysis.cross, 131 + 10 + 8);
    assert.equal(both.analysis.tiles[0].x, 10);
    assert.deepEqual(both.panes.analysisPane, { x: 997, y: 0, w: 149, h: 850 });
    assert.deepEqual(both.panes.controlPane, { x: 1146, y: 0, w: 54, h: 850 });
    assert.deepEqual(both.panes.toolRegion, { x: 997, y: 0, w: 203, h: 850 });
    assert.deepEqual(both.panes.fitRect, { x: 117, y: 0, w: 879, h: 850 });
    assert.deepEqual(both.dividers.map((entry) => entry.kind), ["file", "tool"]);

    const analysisOnly = layout({ showAnalysis: true, showControlbar: false });
    assert.deepEqual(analysisOnly.panes.analysisPane, { x: 1049, y: 0, w: 151, h: 850 });
    assert.deepEqual(analysisOnly.panes.controlPane, { x: 1200, y: 0, w: 0, h: 850 });
    assert.deepEqual(analysisOnly.panes.fitRect, { x: 117, y: 0, w: 931, h: 850 });

    const controlOnly = layout();
    assert.equal(controlOnly.panes.toolRegion.w, 64);
});

test("ANALYSIS-03：瓦片等边长公式与独立/相邻交叉轴", () => {
    assert.equal(analysisTileSize({ mainSize: 850, tileCount: 6 }, t), 131);
    assert.equal(analysisCrossSize({ mainSize: 850, tileCount: 6 }, t), 151);
    assert.equal(analysisTileSize({ mainSize: 850, tileCount: 7 }, t), 111);
    assert.equal(analysisCrossSize({ mainSize: 850, tileCount: 7 }, t), 131);
    assert.equal(analysisTileSize({ mainSize: 850, tileCount: 4 }, t), 201);
    assert.equal(analysisTileSize({ mainSize: 850, tileCount: 5 }, t), 159);

    const adjacent = analysisGeometry({
        axis: "vertical",
        pane: { x: 0, y: 0, w: 129, h: 850 },
        tileCount: 6,
        padding: 10,
        depthStart: 10,
        depthEnd: 8,
    }, t);
    assert.equal(adjacent.tileSize, 131);
    assert.equal(adjacent.cross, 149);
    assert.deepEqual(adjacent.tiles[0], { index: 0, x: 10, y: 10, w: 131, h: 131 });
    assert.equal(adjacent.tiles[5].y + 131, 10 + 5 * 139 + 131);

    const visible = layout({ showAnalysis: true, analysisTileCount: 7, showSimilarity: false });
    assert.equal(visible.analysis.tileCount, 7);
    assert.equal(visible.analysis.cross, 129);
    assert.equal(visible.panes.analysisPane.w, 129);
});

test("SIM-07：行布局按大档卡片与停靠区切宽，列布局加外边距", () => {
    assert.deepEqual(columnSimilarity(t), {
        axis: "vertical", width: 110, occupation: 114, minWidth: 106, maxWidth: null, dockWidth: 0,
    });
    const two = rowSimilarityWidth({ availableWidth: 600, itemCount: 2 }, t);
    assert.equal(two.dockWidth, 96);
    assert.equal(two.width, 2 * 96 + 8 + 2 + 96);
    assert.equal(two.occupation, 298 + 4);
    assert.equal(rowSimilarityHeight(t), 159);
    const wideText = rowSimilarityWidth({
        availableWidth: 900,
        itemCount: 1,
        dockTexts: ["12 组提取失败，可再次尝试"],
        measureText: approximateTextWidth,
    }, t);
    const expectedDock = Math.max(t("sim.action.width"), approximateTextWidth("12 组提取失败，可再次尝试", 11)) + 6;
    assert.equal(wideText.dockWidth, expectedDock);
    const capped = rowSimilarityWidth({ availableWidth: 600, itemCount: 8 }, t);
    assert.equal(capped.maxWidth, 300);
    assert.equal(capped.width, 300);
    assert.equal(capped.occupation, 304);
    const tiny = rowSimilarityWidth({ availableWidth: 200, itemCount: 2 }, t);
    assert.equal(tiny.maxWidth, 100);
    assert.equal(tiny.width, 100, "可用宽一半小于最小宽时以一半为准");

    const colOpen = layout({ similarityExpanded: true, similarityItemCount: 2 });
    assert.equal(colOpen.similarity.occupation, 114);
    assert.equal(colOpen.panes.filePane.w, 230);
    assert.deepEqual(colOpen.similarity.panel, { x: 116, y: 116, w: 110, h: 734 });
    const rowOpen = layout({ layoutMode: "row", similarityExpanded: true, similarityItemCount: 2 });
    assert.equal(rowOpen.similarity.occupation, 302);
    assert.equal(rowOpen.film.scroll.w, 1200 - 302);
    const rowCapped = layout({ width: 700, height: 850, layoutMode: "row", similarityExpanded: true, similarityItemCount: 8 });
    assert.equal(rowCapped.similarity.maxWidth, 350);
    assert.equal(rowCapped.similarity.width, 350);
    assert.equal(rowCapped.film.scroll.w, 700 - rowCapped.similarity.occupation);
});

test("SHELL-08：手机竖屏顶行/分析/胶片/底行与 44 槽位", () => {
    const phone = layout({
        layoutMode: "phone",
        width: 390,
        height: 844,
        safeArea: { top: 59, right: 0, bottom: 34, left: 0 },
        showAnalysis: true,
    });
    assert.equal(phone.form, "phone");
    assert.equal(phone.orientation, "portrait");
    assert.deepEqual(phone.panes.fitRect, { x: 0, y: 174, w: 390, h: 508 });
    assert.deepEqual(phone.panes.topRow, { x: 0, y: 59, w: 390, h: 44 });
    assert.deepEqual(phone.filter.entry, { x: 16, y: 59, w: 44, h: 44 });
    assert.deepEqual(phone.filter.more, { x: 330, y: 59, w: 44, h: 44 });
    assert.equal(phone.filter.toggle, null);
    assert.equal(phone.exif.visible, true);
    assert.deepEqual(phone.exif.frame, { x: 68, y: 59, w: 254, h: 44 });
    assert.equal(phone.exif.items.length, 4);
    assert.equal(phone.exif.items[0].w, (254 - 12 - 6) / 4);
    assert.deepEqual(phone.panes.analysisPane, { x: 0, y: 103, w: 390, h: 71 });
    assert.equal(phone.film.tier, "small");
    assert.deepEqual(phone.film.scroll, { x: 0, y: 682, w: 390, h: 92 });
    assert.deepEqual(phone.film.items[0], { x: 6, y: 8, w: 64, h: 76 });
    assert.deepEqual(phone.panes.bottomRow, { x: 0, y: 774, w: 390, h: 44 });
    assert.deepEqual(phone.control.slots.previous, { x: 16, y: 0, w: 44, h: 44 });
    assert.deepEqual(phone.control.slots.next, { x: 60, y: 0, w: 44, h: 44 });
    const { children: phoneRatingChildren, ...phoneRatingSlot } = phone.control.slots.rating;
    assert.deepEqual(phoneRatingSlot, {
        x: 112, y: 0, w: 166, h: 44, compact: true,
        visual: { x: 112, y: 0, w: 166, h: 44 },
    });
    assert.equal(phoneRatingChildren.length, 6);
    assert.deepEqual(phone.control.slots.fit, { x: 286, y: 0, w: 44, h: 44 });
    assert.deepEqual(phone.nodes["control.previous"], [16, 774, 44, 44]);
    assert.deepEqual(phone.nodes["control.fit"], [286, 774, 44, 44]);
    assert.deepEqual(phone.materials.map((entry) => entry.kind), ["top", "bottom"]);
});

test("SHELL-08：竖屏开关各管各栏，竖屏无 AI 开关", () => {
    const base = {
        layoutMode: "phone", width: 390, height: 844,
        safeArea: { top: 59, bottom: 34 }, showAnalysis: true,
    };
    const noFilm = layout({ ...base, showFilmbar: false });
    assert.notEqual(noFilm.filter.entry, null);
    assert.deepEqual(noFilm.control.slots.previous, { x: 16, y: 0, w: 44, h: 44 });
    assert.equal(noFilm.materials.some((entry) => entry.kind === "bottom"), false);
    const noControl = layout({ ...base, showControlbar: false });
    assert.deepEqual(noControl.control.slots, {});
    assert.equal(noControl.panes.bottomRow, null);
    assert.equal(noControl.exif.visible, true);
    const noExif = layout({ ...base, showExif: false });
    assert.equal(noExif.exif.visible, false);
    assert.notEqual(noExif.filter.entry, null);
    const noNav = layout({ ...base, showNavigation: false });
    assert.equal(noNav.control.slots.previous, undefined);
    const { children: noNavChildren, ...noNavSlot } = noNav.control.slots.rating;
    assert.deepEqual(noNavSlot, {
        x: 16, y: 0, w: 262, h: 44, compact: false,
        visual: { x: 35, y: 0, w: 224, h: 44 },
    });
    assert.equal(noNavChildren.length, 6);
    const noAnalysis = layout({ ...base, showAnalysis: false });
    assert.equal(noAnalysis.materials.some((entry) => entry.kind === "top"), false);
    assert.equal(noAnalysis.panes.fitRect.y, 111);
    const noExifTop = layout({ ...base, showExif: false, showAnalysis: false });
    assert.equal(noExifTop.filter.entry.y, 16, "无 EXIF 时筛选未上移到刘海两旁");
    assert.equal(noExifTop.panes.fitRect.y, 68);
});

test("SHELL-01/10：上下布局竖屏避让底部安全区，横屏不为手势条留位", () => {
    const portrait = layout({ layoutMode: "row", width: 800, height: 1200, safeArea: { top: 20, bottom: 34 } });
    assert.equal(portrait.panes.filePane.y, 20);
    assert.equal(portrait.panes.controlPane.y + portrait.panes.controlPane.h, 1200 - 34);
    const landscape = layout({ layoutMode: "row", width: 1200, height: 800, safeArea: { top: 0, bottom: 34 } });
    assert.equal(landscape.panes.controlPane.y + landscape.panes.controlPane.h, 800);
});

test("SHELL-09：手机横屏刘海侧列、88 合体、无衬底 EXIF 与镜像", () => {
    const landscape = layout({
        layoutMode: "phone",
        width: 844,
        height: 390,
        safeArea: { left: 59 },
        showAnalysis: true,
    });
    assert.equal(landscape.orientation, "landscape");
    assert.deepEqual(landscape.panes.sideColumn, { x: 0, y: 0, w: 131, h: 390 });
    assert.deepEqual(landscape.filter.composite, { x: 35, y: 10, w: 88, h: 44 });
    assert.deepEqual(landscape.filter.entry, { x: 35, y: 10, w: 44, h: 44 });
    assert.deepEqual(landscape.filter.more, { x: 79, y: 10, w: 44, h: 44 });
    assert.equal(landscape.film.tier, "small");
    assert.deepEqual(landscape.film.items[0], { x: 59, y: 64, w: 64, h: 76 });
    assert.equal(landscape.film.trail, 64);
    assert.deepEqual(landscape.nav, {
        visible: true,
        previous: { x: 35, y: 336, w: 44, h: 44 },
        next: { x: 79, y: 336, w: 44, h: 44 },
    });
    assert.deepEqual(landscape.panes.analysisPane, { x: 717, y: 0, w: 73, h: 390 });
    assert.deepEqual(landscape.panes.controlPane, { x: 790, y: 0, w: 54, h: 390 });
    assert.deepEqual(landscape.panes.fitRect, { x: 132, y: 0, w: 585, h: 390 });
    assert.equal(landscape.exif.variant, "bare");
    assert.deepEqual(landscape.exif.frame, { x: 790, y: 10, w: 44, h: 150 });
    assert.equal(landscape.control.slots.exif.w, 44);
    const { children: landscapeChildren, ...landscapeSlot } = landscape.control.slots.rating;
    assert.deepEqual(landscapeSlot, {
        x: 790, y: 170, w: 44, h: 112, compact: true,
        visual: { x: 790, y: 170, w: 44, h: 112 },
    });
    assert.equal(landscapeChildren.length, 6);
    assert.deepEqual(landscape.control.slots.fit, { x: 790, y: 292, w: 44, h: 44 });
    assert.deepEqual(landscape.control.slots.enhance, { x: 790, y: 336, w: 44, h: 44 });
    assert.deepEqual(landscape.nodes["control.fit"], [790, 292, 44, 44]);
    assert.equal(landscape.materials[0].kind, "side");

    const mirrored = layout({
        layoutMode: "phone",
        width: 844,
        height: 390,
        safeArea: { right: 59 },
        showAnalysis: true,
    });
    assert.deepEqual(mirrored.panes.sideColumn, { x: 713, y: 0, w: 131, h: 390 });
    assert.deepEqual(mirrored.filter.composite, { x: 721, y: 10, w: 88, h: 44 });
    assert.deepEqual(mirrored.panes.controlPane, { x: 0, y: 0, w: 54, h: 390 });
    assert.deepEqual(mirrored.panes.analysisPane, { x: 54, y: 0, w: 73, h: 390 });
    assert.deepEqual(mirrored.panes.fitRect, { x: 128, y: 0, w: 585, h: 390 });
    assert.deepEqual(mirrored.panes.topRow, { x: 713, y: 10, w: 131, h: 44 });
    assert.deepEqual(mirrored.film.items[0], { x: 8, y: 64, w: 64, h: 76 });
    const { children: mirroredChildren, ...mirroredSlot } = mirrored.control.slots.rating;
    assert.deepEqual(mirroredSlot, {
        x: 10, y: 170, w: 44, h: 112, compact: true,
        visual: { x: 10, y: 170, w: 44, h: 112 },
    });
    assert.equal(mirroredChildren.length, 6);
    assert.deepEqual(mirrored.control.slots.fit, { x: 10, y: 292, w: 44, h: 44 });
    assert.deepEqual(mirrored.nodes["control.fit"], [10, 292, 44, 44]);
    assert.equal(mirrored.analysis.tiles[0].x, 8, "镜像后接缝未换到控制栏一侧");
    assert.equal(landscape.analysis.tiles[0].x, 10, "非镜像分析瓦片未留朝主图内边距");
});

test("SHELL-12：竖屏顶部/底部材质只在对应栏出现时存在", () => {
    const base = {
        layoutMode: "phone", width: 390, height: 844,
        safeArea: { top: 59, bottom: 34 },
    };
    assert.deepEqual(layout({ ...base, showAnalysis: false }).materials.map((entry) => entry.kind), ["bottom"]);
    assert.deepEqual(layout({ ...base, showAnalysis: true }).materials.map((entry) => entry.kind), ["top", "bottom"]);
    assert.deepEqual(layout({ ...base, showAnalysis: true, showFilmbar: false }).materials.map((entry) => entry.kind), ["top"]);
    assert.deepEqual(layout({ ...base, showAnalysis: false, showFilmbar: false }).materials, []);
});

test("STATE-03：药丸可见时提示条整体上移，底距为 60", () => {
    const pane = { x: 100, y: 50, w: 600, h: 400 };
    const stacked = layout().overlays.toastFrame(pane, 200, 14, true);
    assert.equal(stacked.h, 14 + 6 * 2);
    assert.equal(pane.y + pane.h - (stacked.y + stacked.h), 60);
    assert.equal(stacked.x, 100 + (600 - 224) / 2);
    const alone = layout().overlays.toastFrame(pane, 200, 14, false);
    assert.equal(pane.y + pane.h - (alone.y + alone.h), 24);
    const clipped = layout().overlays.toastFrame(pane, 900, 14, false);
    assert.equal(clipped.w, 500);
});

test("IMAGE-06：导航图贴适应区右下 12 点，长边 150 并含百分比行", () => {
    const pane = { x: 100, y: 50, w: 600, h: 400 };
    const nav = layout().overlays.navigatorFrame(pane, 300, 200);
    assert.equal(nav.thumb.w, 150);
    assert.equal(nav.thumb.h, 100);
    assert.equal(nav.w, 150 + 16);
    assert.equal(pane.x + pane.w - (nav.x + nav.w), 12);
    assert.equal(pane.y + pane.h - (nav.y + nav.h), 12);
    assert.equal(nav.label.h, 15);
    assert.equal(nav.label.y - (nav.thumb.y + nav.thumb.h), 8);
    const portrait = layout().overlays.navigatorFrame(pane, 100, 300);
    assert.equal(portrait.thumb.h, 150);
    assert.equal(portrait.thumb.w, 50);
});

test("IMAGE-07：计算中药丸居中贴底 24", () => {
    const pane = { x: 100, y: 50, w: 600, h: 400 };
    const pill = layout().overlays.busyPillFrame(pane);
    assert.deepEqual(pill, { x: 100 + (600 - 96) / 2, y: 50 + 400 - 24 - 28, w: 96, h: 28 });
});

test("IMAGE-10：空态提示居中，副文相对标题顶边下移 28", () => {
    const pane = { x: 0, y: 0, w: 1200, h: 850 };
    const hint = layout().overlays.hintFrames(pane);
    assert.equal(hint.title.w, 500);
    assert.equal(hint.title.x, 350);
    assert.equal(hint.detail.y - hint.title.y, 28);
    assert.equal(hint.detail.h, 11);
});

test("FILM-02/05：大档内容长度、卡片节点与小档几何", () => {
    assert.equal(filmContentLength(t, 12, "y", "large", 116, 10), 116 + 12 * 144 + 10);
    assert.equal(filmContentLength(t, 12, "x", "large"), 10 + 12 * 96 + 10);
    assert.deepEqual(filmItemFrame(t, 0, 12, "y", 800, "large", 116, 10), { x: 10, y: 116, w: 96, h: 144 });
    assert.deepEqual(filmItemFrame(t, 11, 12, "y", 800, "large", 116, 10), { x: 10, y: 116 + 11 * 144, w: 96, h: 144 });

    const large = cardFrames(t);
    assert.deepEqual(large.root, { x: 0, y: 0, w: 96, h: 144 });
    assert.deepEqual(large.surface, { x: 3, y: 3, w: 90, h: 138 });
    assert.deepEqual(large.image, { x: 5, y: 5, w: 80, h: 80 });
    assert.deepEqual(large.date, { x: 5, y: 103, w: 80, h: 11 });
    assert.deepEqual(large.stars[5], { x: 73, y: 116, w: 14, h: 14 });

    const small = cardFrames(t, "small");
    assert.deepEqual(small.root, { x: 0, y: 0, w: 64, h: 76 });
    assert.deepEqual(small.surface, { x: 2, y: 2, w: 60, h: 72 });
    assert.deepEqual(small.image, { x: 3, y: 3, w: 54, h: 54 });
    assert.equal(small.name.y, 59);
    assert.equal(small.badge.h, 12);
    assert.equal(small.shake.w, 8);
    assert.equal(small.date, null);
    assert.deepEqual(small.stars, []);

    const film = layout().film;
    assert.equal(film.lead, 116);
    assert.equal(film.contentLength, 1854);
    assert.equal(filmCenterOffset(film, 11, 850, t), Math.max(0, film.contentLength - 850));
    assert.deepEqual(film.cards[11].nodes["card11.root"], [10, 116 + 11 * 144, 96, 144]);
});

test("RATING-04：来源浮窗宽度、条目高度与内边距由令牌推导", () => {
    const metrics = ratingSourcesMetrics(t);
    assert.equal(metrics.starSize, 36);
    assert.equal(metrics.entryWidth, 232);
    assert.equal(metrics.contentWidth, 240);
    assert.equal(metrics.shortEntryHeight, 74);
    assert.equal(ratingSourcesHeight(t, [false, false]), 164);
});

test("RATING-04：卡片默认向下、空间不足向上；控制栏横向上、竖向左", () => {
    const host = { width: 1200, height: 850 };
    const height = ratingSourcesHeight(t, [false, false]);
    const cardTop = { x: 3, y: 116, w: 14, h: 14 };
    assert.equal(resolveRatingSourcesDirection({ mode: "card", anchor: cardTop, host, height, t }).direction, "down");
    assert.equal(resolveRatingSourcesDirection({
        mode: "control", axis: "horizontal", anchor: cardTop, host, height, t,
    }).direction, "up");
    assert.equal(resolveRatingSourcesDirection({
        mode: "control", axis: "vertical", anchor: cardTop, host, height, t, direction: "right",
    }).direction, "right");
    assert.deepEqual(placeRatingSources({
        anchor: { x: 500, y: 300, w: 30, h: 30 }, host, width: 206, height: 100, direction: "right", gap: 12,
    }), { x: 542, y: 265 });
});

test("RATING-04c：控制栏浮窗方向只由当前形态/刘海侧决定", () => {
    const phonePortrait = { form: "phone", orientation: "portrait", safe: { notchSide: "left" } };
    const phoneLeft = { form: "phone", orientation: "landscape", safe: { notchSide: "left" } };
    const phoneRight = { form: "phone", orientation: "landscape", safe: { notchSide: "right" } };
    assert.equal(controlRatingPopoverDirection(phonePortrait), "up");
    assert.equal(controlRatingPopoverDirection(phoneLeft), "left");
    assert.equal(controlRatingPopoverDirection(phoneRight), "right");
    assert.equal(controlRatingPopoverDirection({ form: "wide", control: { axis: "horizontal" } }), "up");
    assert.equal(controlRatingPopoverDirection({ form: "wide", control: { axis: "vertical" } }), "left");
});

test("FILM-05：手机选档按大档横向 10+10、纵向 5+10 的主轴内边距", () => {
    const portraitSmall = layout({ layoutMode: "phone", width: 499, height: 900 });
    assert.equal(portraitSmall.film.tier, "small");
    const portraitLarge = layout({ layoutMode: "phone", width: 500, height: 900 });
    assert.equal(portraitLarge.film.tier, "large", "500 宽扣 20 后正好容纳 5 张 96 卡");
    const landscapeSmall = layout({ layoutMode: "phone", width: 844, height: 734, safeArea: { left: 59 } });
    assert.equal(landscapeSmall.film.tier, "small");
    const landscapeLarge = layout({ layoutMode: "phone", width: 844, height: 735, safeArea: { left: 59 } });
    assert.equal(landscapeLarge.film.tier, "large", "735 高扣 15 后正好容纳 5 张 144 卡");
});

test("SHELL-12：宽形态行布局文件栏背景从窗口顶边铺到内容末端", () => {
    const row = layout({ layoutMode: "row", safeArea: { top: 24, bottom: 0, left: 0, right: 0 } });
    assert.deepEqual(row.panes.filePane, { x: 0, y: 24, w: 1200, h: row.filterBandHeight + t("film.band.height") });
    assert.deepEqual(row.materials.find((entry) => entry.kind === "file"), {
        kind: "file",
        frame: { x: 0, y: 0, w: 1200, h: 24 + row.panes.filePane.h },
    });
    assert.deepEqual(row.panes.fitRect, { x: 0, y: 248, w: 1200, h: 537 });
});

test("TOOLBAR-03：宽评分组可用主轴不足 224 时用 88 紧凑外框，子节点不越出", () => {
    const compact = computeControlBar({ axis: "horizontal", mainSize: 200, exifCount: 0, showRating: true, commandCount: 0 }, t);
    const rating = compact.groups[0];
    assert.equal(rating.w, 88);
    assert.equal(rating.h, 44);
    for (const child of rating.children) {
        assert.ok(child.x >= 0 && child.x + child.w <= 88.0001, `紧凑子节点越出外框：${child.x}+${child.w}`);
        assert.equal(child.h, 44);
    }
    const expanded = computeControlBar({ axis: "horizontal", mainSize: 1200, exifCount: 0, showRating: true, commandCount: 0 }, t);
    assert.equal(expanded.groups[0].w, 224);
    assert.deepEqual(expanded.groups[0].children[0], { kind: "rating", index: 0, x: 4, y: 0, w: 36, h: 44 });
    const vertical = computeControlBar({ axis: "vertical", mainSize: 850, exifCount: 0, showRating: true, commandCount: 0 }, t);
    assert.deepEqual(vertical.groups[0].children[0], { kind: "rating", index: 0, x: 0, y: 184, w: 44, h: 36 });
});

test("SHELL-08/10：竖屏适应区与底行使用左右安全区，底距压入 home indicator", () => {
    const phone = layout({
        layoutMode: "phone",
        width: 390,
        height: 844,
        safeArea: { top: 59, right: 30, bottom: 0, left: 20 },
        showAnalysis: false,
    });
    assert.deepEqual(phone.panes.fitRect, { x: 20, y: 111, w: 340, h: 561 });
    assert.deepEqual(phone.filter.entry, { x: 36, y: 59, w: 44, h: 44 });
    assert.deepEqual(phone.filter.more, { x: 300, y: 59, w: 44, h: 44 });
    assert.deepEqual(phone.control.slots.previous, { x: 36, y: 0, w: 44, h: 44 });
    assert.deepEqual(phone.control.slots.fit, { x: 256, y: 0, w: 44, h: 44 });
    assert.equal(phone.panes.bottomRow.y, 764);
});

test("SHELL-09：横屏关闭评分释放 rating 保留位，EXIF 整项裁剪，极矮窗口不重叠", () => {
    const noRating = layout({
        layoutMode: "phone", width: 844, height: 390,
        safeArea: { left: 59 }, showAnalysis: true, showRating: false,
    });
    assert.equal(noRating.control.slots.rating, undefined);
    assert.deepEqual(noRating.control.slots.exif, { x: 790, y: 71, w: 44, h: 150, items: noRating.control.slots.exif.items });
    assert.deepEqual(noRating.control.slots.fit, { x: 790, y: 231, w: 44, h: 44 });
    assert.deepEqual(noRating.control.slots.enhance, { x: 790, y: 275, w: 44, h: 44 });

    const noRatingShort = layout({
        layoutMode: "phone", width: 844, height: 250,
        safeArea: { left: 59 }, showAnalysis: true, showRating: false,
    });
    assert.equal(noRatingShort.control.slots.exif.items.length, 3, "关闭评分后 EXIF 未获得释放的空间");
    assert.deepEqual(noRatingShort.control.slots.fit, { x: 790, y: 142, w: 44, h: 44 });
    assert.deepEqual(noRatingShort.control.slots.enhance, { x: 790, y: 186, w: 44, h: 44 });

    const short = layout({
        layoutMode: "phone", width: 844, height: 250,
        safeArea: { left: 59 }, showAnalysis: true,
    });
    assert.equal(short.exif.visible, false);
    const { children: shortChildren, ...shortSlot } = short.control.slots.rating;
    assert.deepEqual(shortSlot, {
        x: 790, y: 10, w: 44, h: 132, compact: true,
        visual: { x: 790, y: 10, w: 44, h: 132 },
    });
    assert.equal(shortChildren.length, 6);
    assert.deepEqual(short.control.slots.fit, { x: 790, y: 152, w: 44, h: 44 });
    assert.deepEqual(short.control.slots.enhance, { x: 790, y: 196, w: 44, h: 44 });

    const controlOff = layout({
        layoutMode: "phone", width: 844, height: 390,
        safeArea: { left: 59 }, showAnalysis: true, showControlbar: false,
    });
    assert.deepEqual(Object.keys(controlOff.control.slots).sort(), ["next", "previous"]);
    assert.deepEqual(controlOff.control.slots.previous, { x: 35, y: 336, w: 44, h: 44 });
    assert.deepEqual(controlOff.control.slots.next, { x: 79, y: 336, w: 44, h: 44 });
    const controlOffMirror = layout({
        layoutMode: "phone", width: 844, height: 390,
        safeArea: { right: 59 }, showAnalysis: true, showControlbar: false,
    });
    assert.deepEqual(controlOffMirror.control.slots.previous, { x: 721, y: 336, w: 44, h: 44 });
    assert.deepEqual(controlOffMirror.control.slots.next, { x: 765, y: 336, w: 44, h: 44 });

    const tiny = layout({
        layoutMode: "phone", width: 844, height: 120,
        safeArea: { left: 59 }, showAnalysis: true,
    });
    const { rating, fit, enhance } = tiny.control.slots;
    assert.equal(rating.h, 2);
    assert.equal(rating.compact, true);
    assert.ok(rating.y + rating.h <= fit.y, "评分槽与上下张/缩放槽重叠");
    assert.equal(fit.y + fit.h, enhance.y, "缩放/增强两段之间出现组距");
});

test("FILTER-07/TOOLBAR-05@mac：mac 配置档使用 42 让位、96 胶囊与桌面 EXIF 衬底", () => {
    const col = layout({}, mac, "mac");
    assert.equal(col.filter.kind, "capsule");
    assert.equal(col.filter.leadingInset, mac("shell.filter.leadingInset"));
    assert.deepEqual(col.filter.composite, { x: 10, y: 42, w: 96, h: 44 });
    assert.deepEqual(col.filter.control, { x: 10, y: 42, w: 96, h: 44 });
    assert.deepEqual(col.filter.entry, { x: 10, y: 42, w: 96, h: 44 });
    assert.deepEqual(col.filter.controls, {});
    assert.deepEqual(col.filter.toggle, { x: 10, y: 94, w: 96, h: 44 });
    assert.equal(col.filter.bar.h, 148);
    assert.equal(col.film.lead, 148);
    assert.equal(col.exif.variant, "block");
    assert.deepEqual(col.exif.frame, { x: 10, y: 70, w: 64, h: 162 });
    assert.deepEqual(col.exif.origin, { x: 1116, y: 0 });
    assert.deepEqual(col.nodes["exif.block"], [1126, 70, 64, 162]);
    assert.deepEqual(col.exif.items[0], { kind: "exif", index: 0, x: 6, y: 6, w: 52, h: 36 });
    assert.deepEqual(col.control.document, { x: 0, y: 0, w: 84, h: 850 });

    const colSegments = layout({ filterSegmentWidth: 28 }, mac, "mac");
    assert.equal(colSegments.filter.kind, "segments");
    assert.deepEqual(colSegments.filter.controls.sort, { x: 10, y: 42, w: 32, h: 44 });
    assert.deepEqual(colSegments.filter.controls.order, { x: 42, y: 42, w: 32, h: 44 });
    assert.deepEqual(colSegments.filter.controls.rating, { x: 74, y: 42, w: 32, h: 44 });

    const row = layout({ layoutMode: "row" }, mac, "mac");
    assert.equal(row.filter.kind, "segments");
    assert.deepEqual(row.filter.controls.sort, { x: 416, y: 10, w: 90, h: 44 });
    assert.deepEqual(row.filter.controls.order, { x: 506, y: 10, w: 90, h: 44 });
    assert.deepEqual(row.filter.controls.rating, { x: 596, y: 10, w: 90, h: 44 });
    assert.deepEqual(row.filter.toggle, { x: 694, y: 10, w: 90, h: 44 });
    assert.equal(row.filter.bar.h, 64);
    assert.equal(row.film.scroll.y, 64);
    assert.equal(row.exif.variant, "block");

    const capsule = layout({ layoutMode: "row", width: 360, height: 800 }, mac, "mac");
    assert.equal(capsule.filter.kind, "capsule");
    assert.deepEqual(capsule.filter.entry, { x: 83, y: 10, w: 96, h: 44 });
    assert.deepEqual(capsule.filter.toggle, { x: 187, y: 10, w: 90, h: 44 });
    assert.equal(capsule.filter.bar.h, 64);

    const measured = layout({ layoutMode: "row", filterSegmentWidths: [100, 87, 100] }, mac, "mac");
    assert.deepEqual(measured.filter.control, { x: 407.5, y: 10, w: 287, h: 44 });
    assert.deepEqual(measured.filter.controls.sort, { x: 407.5, y: 10, w: 100, h: 44 });
    assert.deepEqual(measured.filter.controls.order, { x: 507.5, y: 10, w: 87, h: 44 });
    assert.deepEqual(measured.filter.controls.rating, { x: 594.5, y: 10, w: 100, h: 44 });
    assert.deepEqual(measured.filter.toggle, { x: 702.5, y: 10, w: 90, h: 44 });
});
