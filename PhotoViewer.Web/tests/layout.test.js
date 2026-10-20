import { test } from "node:test";
import assert from "node:assert/strict";
import { createTokens } from "../src/core/tokens.js";
import { computeLayout, approximateTextWidth } from "../src/core/layout/index.js";
import { resolveLayoutMode } from "../src/core/layout/partition.js";
import { computeControlBar, groupMainLength } from "../src/core/layout/controlbar.js";
import { analysisTileSize, analysisCrossSize, analysisGeometry } from "../src/core/layout/analysis.js";
import { columnSimilarity, rowSimilarityWidth, rowSimilarityHeight } from "../src/core/layout/similarity.js";
import { filmContentLength, filmItemFrame } from "../src/core/layout/film.js";
import { cardFrames } from "../src/core/layout/card.js";

const t = createTokens("web");
const mac = createTokens("mac");

const scene = {
    width: 1200,
    height: 850,
    layoutMode: "col",
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

test("SHELL-01：隐藏栏位主轴归零、分隔线消失，空间全部还给主图", () => {
    const full = layout();
    assert.deepEqual(full.nodes["root.filePane"], [0, 0, 116, 850]);
    assert.deepEqual(full.nodes["root.photoPane"], [117, 0, 1012, 850]);

    const noControl = layout({ showControlbar: false });
    assert.deepEqual(noControl.panes.controlPane, { x: 1200, y: 0, w: 0, h: 850 });
    assert.equal(noControl.panes.controlDivider.w, 0);
    assert.deepEqual(noControl.nodes["root.photoPane"], [117, 0, 1083, 850]);

    const noFile = layout({ showFilmbar: false });
    assert.equal(noFile.panes.filePane.w, 0);
    assert.equal(noFile.panes.fileDivider.w, 0);
    assert.equal(noFile.filter, null);
    assert.equal(noFile.film, null);
    assert.deepEqual(noFile.nodes["root.photoPane"], [0, 0, 1129, 850]);

    const none = layout({ showFilmbar: false, showControlbar: false });
    assert.deepEqual(none.nodes["root.photoPane"], [0, 0, 1200, 850]);
});

test("SHELL-01：最小窗口下主图不被压到零，行布局隐藏文件栏后主图顶到 0", () => {
    const small = layout({ width: 560, height: 480 });
    assert.ok(small.panes.photoPane.w >= 1);
    assert.equal(small.panes.photoPane.w, 322);

    const row = layout({ width: 560, height: 480, layoutMode: "row" });
    assert.ok(row.panes.photoPane.h >= 1);

    const rowNoFile = layout({ width: 700, height: 480, layoutMode: "row", showFilmbar: false });
    assert.equal(rowNoFile.panes.photoPane.y, 0);
    assert.equal(rowNoFile.panes.photoPane.h, 369);
});

test("SHELL-01：auto 只看窗口宽高比，手动模式不随尺寸改变", () => {
    assert.equal(resolveLayoutMode("auto", 1200, 850), "col");
    assert.equal(resolveLayoutMode("auto", 850, 700), "col");
    assert.equal(resolveLayoutMode("auto", 700, 850), "row");
    assert.equal(resolveLayoutMode("auto", 900, 900), "row");
    assert.equal(resolveLayoutMode("row", 1200, 850), "row");
    assert.equal(resolveLayoutMode("col", 700, 850), "col");
    assert.equal(layout({ width: 700, height: 850, layoutMode: "auto" }).layoutMode, "row");
    assert.equal(layout({ width: 1200, height: 850, layoutMode: "auto" }).layoutMode, "col");
});

test("FILTER-07：列布局五控件纵向排列、居中，计数按自然宽占位", () => {
    const web = layout({ width: 1200, height: 850, layoutMode: "col" }).filter;
    assert.deepEqual(web.controls.sort, { x: 13, y: 10, w: 90, h: 32 });
    assert.deepEqual(web.controls.order, { x: 13, y: 47, w: 90, h: 32 });
    assert.deepEqual(web.controls.rating, { x: 13, y: 84, w: 90, h: 32 });
    assert.deepEqual(web.controls.count, { x: (116 - countWidth) / 2, y: 121, w: countWidth, h: 17 });
    assert.deepEqual(web.controls.toggle, { x: 13, y: 143, w: 90, h: 27 });
    assert.equal(web.bar.h, 180);
    assert.equal(layout().film.scroll.y, 180);

    const macLayout = layout({}, mac, "mac").filter;
    assert.equal(macLayout.controls.sort.y, 34);
    assert.equal(macLayout.controls.count.y, 145);
    assert.equal(macLayout.controls.toggle.y, 167);
    assert.equal(macLayout.bar.h, 204);
    assert.equal(layout({}, mac, "mac").film.scroll.y, 204);
});

test("FILTER-07：行布局整组同排横居中，胶片栏在筛选条下方通栏", () => {
    const row = layout({ width: 1200, height: 850, layoutMode: "row" });
    const filter = row.filter;
    const groupWidth = 90 * 3 + countWidth + 90 + 5 * 4;
    const start = (1200 - groupWidth) / 2;
    assert.deepEqual(filter.controls.sort, { x: start, y: 10, w: 90, h: 32 });
    assert.equal(filter.controls.order.x, start + 95);
    assert.equal(filter.controls.rating.x, start + 190);
    assert.deepEqual(filter.controls.count, { x: start + 285, y: 17.5, w: countWidth, h: 17 });
    assert.deepEqual(filter.controls.toggle, { x: start + 285 + countWidth + 5, y: 12.5, w: 90, h: 27 });
    assert.equal(filter.bar.h, 52);
    assert.deepEqual(row.film.scroll, { x: 0, y: 52, w: 1200, h: 159 });
});

test("TOOLBAR-02：空间不足整组换行，每行按公式居中且组长不拆", () => {
    const row = layout({ width: 700, height: 480, layoutMode: "row" });
    const control = row.control;
    assert.equal(groupMainLength("exif", 4, "horizontal", t), 244);
    assert.equal(groupMainLength("rating", 6, "horizontal", t), 196);
    assert.equal(groupMainLength("command", 6, "horizontal", t), 280);

    assert.equal(control.lines.length, 2);
    assert.deepEqual(control.lines.map((line) => line.length), [450, 280]);
    assert.equal(control.groups[0].x, 125);
    assert.equal(control.groups[1].x, 379);
    assert.equal(control.groups[2].x, 210);
    assert.equal(control.groups[2].y, 60);
    assert.deepEqual(control.document, { x: 0, y: 0, w: 700, h: 110 });
    assert.equal(row.panes.controlPane.h, 110);
    assert.equal(control.groups[0].w, 244);
});

test("TOOLBAR-02/08：单组超长从 padding 起排并标记溢出，不裁切", () => {
    const control = computeControlBar({ axis: "horizontal", mainSize: 1200, exifCount: 0, showRating: true, commandCount: 30 }, t);
    assert.equal(control.groups[1].x, 10);
    assert.equal(control.groups[1].w, 30 * 40 + 29 * 8);
    assert.equal(control.overflow, true);
    assert.equal(control.document.w, 10 + 1432 + 10);
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

test("TOOLBAR-03：列布局评分 5→0 自上而下，行布局 0→5 自左向右", () => {
    const col = layout({ layoutMode: "col" }).control.groups[1];
    assert.equal(col.children[0].y, 158);
    assert.equal(col.children[5].y, 8);
    assert.equal(col.children[0].x, 10);

    const row = layout({ width: 1200, height: 850, layoutMode: "row" }).control.groups[1];
    assert.deepEqual(row.children[0], { kind: "rating", index: 0, x: 8, y: 5, w: 30, h: 30 });
    assert.equal(row.children[5].x, 158);
});

test("ANALYSIS-03：瓦片等边长公式 n=6/7，交叉轴加两侧内边距", () => {
    assert.equal(analysisTileSize({ mainSize: 850, tileCount: 6 }, t), 131);
    assert.equal(analysisCrossSize({ mainSize: 850, tileCount: 6 }, t), 151);
    assert.equal(analysisTileSize({ mainSize: 850, tileCount: 7 }, t), 111);
    assert.equal(analysisCrossSize({ mainSize: 850, tileCount: 7 }, t), 131);

    const tall = analysisGeometry({ axis: "vertical", pane: { x: 0, y: 0, w: 151, h: 850 }, tileCount: 6 }, t);
    assert.equal(tall.tileSize, 131);
    assert.deepEqual(tall.tiles[0], { index: 0, x: 10, y: 10, w: 131, h: 131 });
    assert.deepEqual(tall.tiles[1], { index: 1, x: 10, y: 149, w: 131, h: 131 });
    assert.ok(tall.tiles[5].y + tall.tiles[5].h <= 850 - 10);

    const wide = analysisGeometry({ axis: "horizontal", pane: { x: 0, y: 0, w: 1012, h: 151 }, tileCount: 7 }, t);
    assert.equal(wide.tileSize, 134);
    assert.deepEqual(wide.tiles[0], { index: 0, x: 10, y: 10, w: 134, h: 134 });
    assert.ok(wide.tiles[6].x + 134 <= 1012 - 10);

    const visible = layout({ showAnalysis: true, analysisTileCount: 7 });
    assert.equal(visible.analysis.cross, 131);
    assert.equal(visible.panes.analysisPane.w, 131);
    assert.equal(visible.panes.analysisDivider.w, 1);
});

test("SIM-07：行布局宽含停靠区并夹到最小宽与可用宽一半，列布局加外边距", () => {
    assert.deepEqual(columnSimilarity(t), {
        axis: "vertical", width: 110, occupation: 114, minWidth: 106, maxWidth: null, dockWidth: 0,
    });

    // 列表态停靠区 = max(动作按钮宽 90, 文本自然宽) + 2 × 3。
    const two = rowSimilarityWidth({ availableWidth: 600, itemCount: 2 }, t);
    assert.equal(two.dockWidth, 96);
    assert.equal(two.width, 2 * 96 + 8 + 2 + 96);
    assert.equal(two.occupation, 298 + 4);
    assert.equal(rowSimilarityWidth({ availableWidth: 600, itemCount: 0 }, t).width, 106);
    assert.equal(rowSimilarityWidth({ availableWidth: 600, itemCount: 0 }, t).dockWidth, 0);
    assert.equal(rowSimilarityWidth({ availableWidth: 200, itemCount: 2 }, t).width, 100);
    assert.equal(rowSimilarityWidth({ availableWidth: 1000, itemCount: 8 }, t).width, 500);
    assert.equal(rowSimilarityHeight(t), 159);

    const wideText = rowSimilarityWidth({
        availableWidth: 900,
        itemCount: 1,
        dockTexts: ["12 组提取失败，可再次尝试"],
        measureText: approximateTextWidth,
    }, t);
    const expectedDock = Math.max(t("sim.action.width"), approximateTextWidth("12 组提取失败，可再次尝试", 11)) + 6;
    assert.equal(wideText.dockWidth, expectedDock);
    assert.equal(wideText.width, 96 + 8 + 2 + expectedDock);

    const colOpen = layout({ similarityExpanded: true, similarityItemCount: 2 });
    assert.equal(colOpen.similarity.occupation, 114);
    assert.equal(colOpen.panes.filePane.w, 230);
    assert.equal(colOpen.panes.photoPane.w, 898);

    const rowOpen = layout({ width: 1200, height: 850, layoutMode: "row", similarityExpanded: true, similarityItemCount: 2 });
    assert.equal(rowOpen.similarity.occupation, 302);
    assert.equal(rowOpen.film.scroll.w, 1200 - 302);
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

test("IMAGE-06：导航图贴主图区右下 12 点，长边 150 并含百分比行", () => {
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
    const narrow = layout().overlays.hintFrames({ x: 0, y: 0, w: 300, h: 400 });
    assert.equal(narrow.title.w, 300 - 2 * 10);
    assert.equal(narrow.title.x, 10);
});

test("FILM-02/04：内容长度与卡片步长，短列表在视口内居中不放空白滚动", () => {
    assert.equal(filmContentLength(t, 12, "col"), 5 + 12 * 144 + 10);
    assert.equal(filmContentLength(t, 12, "row"), 10 + 12 * 96 + 10);
    assert.deepEqual(filmItemFrame(t, 0, 12, "col", 800), { x: 10, y: 5, w: 96, h: 144 });
    assert.deepEqual(filmItemFrame(t, 11, 12, "col", 800), { x: 10, y: 5 + 11 * 144, w: 96, h: 144 });

    const col = layout();
    assert.deepEqual(col.film.document, { x: 0, y: 0, w: 116, h: 1743 });
    assert.deepEqual(col.nodes["card11.root"], [10, 5 + 11 * 144, 96, 144]);

    const row = layout({ width: 1200, height: 850, layoutMode: "row" });
    assert.deepEqual(row.film.document, { x: 0, y: 0, w: 1200, h: 159 });
    assert.deepEqual(row.nodes["card0.root"], [24, 5, 96, 144]);
    assert.equal(row.film.items[11].x + 96, 24 + 11 * 96 + 96);

    const short = layout({ photoCount: 3, width: 1200, height: 850, layoutMode: "row" });
    assert.deepEqual(short.film.document, { x: 0, y: 0, w: 1200, h: 159 });
    assert.equal(short.film.items[0].x, 456);
});

test("FILM-05：卡片内层节点由令牌推导", () => {
    const frames = cardFrames(t);
    assert.deepEqual(frames.root, { x: 0, y: 0, w: 96, h: 144 });
    assert.deepEqual(frames.surface, { x: 3, y: 3, w: 90, h: 138 });
    assert.deepEqual(frames.image, { x: 5, y: 5, w: 80, h: 80 });
    assert.deepEqual(frames.name, { x: 2, y: 89, w: 86, h: 12 });
    assert.deepEqual(frames.date, { x: 5, y: 103, w: 80, h: 11 });
    assert.deepEqual(frames.shake, { x: 67, y: 67, w: 16, h: 16 });
    assert.deepEqual(frames.stars[0], { x: 3, y: 116, w: 14, h: 14 });
    assert.deepEqual(frames.stars[5], { x: 73, y: 116, w: 14, h: 14 });
});
