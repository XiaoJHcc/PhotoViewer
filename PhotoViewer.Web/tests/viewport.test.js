import { test } from "node:test";
import assert from "node:assert/strict";
import { createTokens } from "../src/core/tokens.js";
import { Viewport, displaySize } from "../src/core/viewport.js";

const t = createTokens();
const PRESETS = [12.5, 25, 33.333, 50, 75, 100, 200, 400];

function make({ dpr = 1, allowBelowFit = true, view = { width: 1000, height: 800 } } = {}) {
    const viewport = new Viewport(t, { dpr, allowBelowFit });
    viewport.setViewSize(view.width, view.height);
    return viewport;
}

function open(viewport, { width = 4000, height = 3000, focus = null, mode = "reset" } = {}) {
    viewport.setImage({ width, height, focus }, { mode });
    return viewport;
}

function near(actual, expected, message = "") {
    assert.ok(
        Math.abs(actual - expected) < 1e-9,
        `${message} 期望 ${expected}，实际 ${actual}`,
    );
}

test("IMAGE-02：双击以触点为锚点放大到物理 100%", () => {
    const viewport = open(make());
    assert.equal(viewport.isFit, true);
    const anchor = { x: 250, y: 200 };
    const before = viewport.viewToImage(anchor.x, anchor.y);
    near(before.u, 0.25);
    near(before.v, 175 / 750);
    assert.equal(viewport.toggleFit(anchor), true);
    near(viewport.physicalScale, 1);
    assert.equal(viewport.isFit, false);
    const after = viewport.viewToImage(anchor.x, anchor.y);
    near(after.u, before.u);
    near(after.v, before.v);
});

test("IMAGE-02：Fit 有对焦点以对焦点为中心到 100% 并进入跟随", () => {
    const viewport = open(make(), { focus: { x: 0.7, y: 0.3 } });
    viewport.toggleFit();
    near(viewport.physicalScale, 1);
    const center = viewport.viewToImage(500, 400);
    near(center.u, 0.7);
    near(center.v, 0.3);
    assert.equal(viewport.followsFocus, true);
});

test("IMAGE-02：Fit 无对焦点以视口中心到 100%", () => {
    const viewport = open(make());
    viewport.toggleFit();
    const rect = viewport.imageRect();
    near(rect.x + rect.w / 2, 500);
    near(rect.y + rect.h / 2, 400);
    assert.equal(viewport.followsFocus, false);
});

test("IMAGE-02：已放大时双击或 Fit 命令回到适应", () => {
    const byDoubleClick = open(make());
    byDoubleClick.toggleFit({ x: 200, y: 200 });
    assert.equal(byDoubleClick.toggleFit({ x: 200, y: 200 }), true);
    assert.equal(byDoubleClick.isFit, true);
    near(byDoubleClick.scale, 0.25);
    near(byDoubleClick.imageRect().x, 0);

    const byCommand = open(make());
    byCommand.toggleFit();
    assert.equal(byCommand.toggleFit(), true);
    assert.equal(byCommand.isFit, true);
    near(byCommand.imageRect().y, 25);
});

test("IMAGE-02：放大态切图保持物理倍率与归一化中心", () => {
    const viewport = open(make());
    viewport.toggleFit();
    viewport.panBy(300, -200);
    const before = viewport.viewToImage(500, 400);
    viewport.setImage({ width: 3000, height: 2000, focus: null }, { mode: "batch" });
    near(viewport.physicalScale, 1);
    const after = viewport.viewToImage(500, 400);
    near(after.u, before.u);
    near(after.v, before.v);
    near(after.u, 0.425);
});

test("IMAGE-02：跟随对焦切图居中到新照片对焦点", () => {
    const viewport = open(make(), { focus: { x: 0.5, y: 0.5 } });
    viewport.toggleFit();
    assert.equal(viewport.followsFocus, true);
    viewport.setImage({ width: 4000, height: 3000, focus: { x: 0.25, y: 0.75 } }, { mode: "batch" });
    const center = viewport.viewToImage(500, 400);
    near(center.u, 0.25);
    near(center.v, 0.75);
    assert.equal(viewport.followsFocus, true);
});

test("IMAGE-02：新照片无对焦点时停止跟随", () => {
    const viewport = open(make(), { focus: { x: 0.5, y: 0.5 } });
    viewport.toggleFit();
    viewport.setImage({ width: 4000, height: 3000, focus: { x: 0.25, y: 0.75 } }, { mode: "batch" });
    viewport.setImage({ width: 4000, height: 3000, focus: null }, { mode: "batch" });
    assert.equal(viewport.followsFocus, false);
    near(viewport.physicalScale, 1);
    const center = viewport.viewToImage(500, 400);
    near(center.u, 0.25);
    near(center.v, 0.75);
});

test("IMAGE-02：适应态切图不沿用上一张倍率", () => {
    const viewport = open(make());
    viewport.toggleFit();
    near(viewport.scale, 1);
    viewport.toggleFit();
    viewport.setImage({ width: 2000, height: 1000, focus: null }, { mode: "batch" });
    assert.equal(viewport.isFit, true);
    near(viewport.scale, 0.5);
    near(viewport.imageRect().x, 0);
    near(viewport.imageRect().y, 150);
});

test("IMAGE-02：物理 100% 不高于适应倍率的小图保持适应", () => {
    const viewport = open(make(), { width: 600, height: 400 });
    near(viewport.fitScale, 1000 / 600);
    assert.equal(viewport.toggleFit(), false);
    assert.equal(viewport.isFit, true);
    near(viewport.scale, 1000 / 600);
    near(viewport.imageRect().x, 0);
});

test("IMAGE-02：reset 打开回到适应并退出跟随", () => {
    const viewport = open(make(), { focus: { x: 0.5, y: 0.5 } });
    viewport.toggleFit();
    viewport.panBy(100, 100);
    viewport.setImage({ width: 2000, height: 1500, focus: { x: 0.2, y: 0.2 } }, { mode: "reset" });
    assert.equal(viewport.isFit, true);
    near(viewport.scale, 0.5);
    near(viewport.imageRect().x, 0);
    near(viewport.imageRect().y, 25);
    assert.equal(viewport.followsFocus, false);
});

test("IMAGE-02：EXIF 对焦点异步到达后下一次命令才使用", () => {
    const viewport = open(make());
    viewport.setFocus({ x: 0.8, y: 0.2 });
    assert.equal(viewport.isFit, true);
    near(viewport.scale, 0.25);
    near(viewport.imageRect().y, 25);
    viewport.toggleFit();
    const center = viewport.viewToImage(500, 400);
    near(center.u, 0.8);
    near(center.v, 0.2);
    assert.equal(viewport.followsFocus, true);
});

test("IMAGE-02：拖动离开对焦点退出跟随，回到容差内重新跟随", () => {
    const viewport = open(make(), { focus: { x: 0.5, y: 0.5 } });
    viewport.toggleFit();
    viewport.panBy(80, 0);
    assert.equal(viewport.followsFocus, true);
    viewport.panBy(400, 0);
    assert.equal(viewport.followsFocus, false);
    viewport.panBy(-480, 0);
    assert.equal(viewport.followsFocus, true);
});

test("IMAGE-04：百分比按物理倍率显示，最多两位小数", () => {
    const viewport = open(make());
    assert.equal(viewport.percentText(), "25%");
    viewport.toggleFit();
    assert.equal(viewport.percentText(), "100%");
    viewport.zoomPreset(-1, PRESETS);
    assert.equal(viewport.percentText(), "75%");
    viewport.zoomPreset(-1, PRESETS);
    assert.equal(viewport.percentText(), "50%");
    viewport.zoomPreset(-1, PRESETS);
    assert.equal(viewport.percentText(), "33.33%");
    viewport.zoomPreset(-1, PRESETS);
    assert.equal(viewport.percentText(), "25%");
});

test("IMAGE-04：dpr=2 时物理 100% 的 scale 为 0.5", () => {
    const viewport = open(make({ dpr: 2 }));
    assert.equal(viewport.percentText(), "50%");
    viewport.toggleFit();
    near(viewport.scale, 0.5);
    near(viewport.physicalScale, 1);
    assert.equal(viewport.percentText(), "100%");
});

test("IMAGE-04：预设缩放取严格大/小于当前物理百分比的最近档", () => {
    const viewport = open(make());
    assert.equal(viewport.zoomPreset(1, PRESETS), true);
    assert.equal(viewport.percentText(), "33.33%");
    assert.equal(viewport.zoomPreset(1, PRESETS), true);
    assert.equal(viewport.percentText(), "50%");
    assert.equal(viewport.zoomPreset(-1, PRESETS), true);
    assert.equal(viewport.percentText(), "33.33%");
    assert.equal(viewport.zoomPreset(-1, PRESETS), true);
    assert.equal(viewport.percentText(), "25%");
});

test("IMAGE-04：33.333 档不会因浮点误差被重复选中", () => {
    const viewport = open(make());
    viewport.zoomPreset(1, PRESETS);
    assert.equal(viewport.percentText(), "33.33%");
    viewport.zoomPreset(1, PRESETS);
    assert.equal(viewport.percentText(), "50%");
    viewport.zoomPreset(-1, PRESETS);
    assert.equal(viewport.percentText(), "33.33%");
    viewport.zoomPreset(1, PRESETS);
    assert.equal(viewport.percentText(), "50%");
});

test("IMAGE-04：预设档首尾之外不动作", () => {
    const viewport = open(make());
    while (viewport.zoomPreset(1, PRESETS));
    assert.equal(viewport.percentText(), "400%");
    assert.equal(viewport.zoomPreset(1, PRESETS), false);
    assert.equal(viewport.percentText(), "400%");
    while (viewport.zoomPreset(-1, PRESETS));
    assert.equal(viewport.percentText(), "12.5%");
    assert.equal(viewport.zoomPreset(-1, PRESETS), false);
});

test("IMAGE-04：缩放范围 1%–1600%，到界返回 false", () => {
    const viewport = open(make());
    assert.equal(viewport.zoomBy(1000), true);
    near(viewport.physicalScale, 16);
    assert.equal(viewport.percentText(), "1600%");
    assert.equal(viewport.zoomBy(2), false);
    assert.equal(viewport.zoomBy(0.000001), true);
    near(viewport.physicalScale, 0.01);
    assert.equal(viewport.percentText(), "1%");
    assert.equal(viewport.zoomBy(0.5), false);
    assert.equal(viewport.zoomBy(1), false);
});

test("INPUT-09：比例缩放 ×1.25 以视口中心为锚点", () => {
    const viewport = open(make());
    assert.equal(viewport.zoomBy(t("image.zoom.step.in")), true);
    near(viewport.physicalScale, 0.25 * 1.25);
    assert.equal(viewport.percentText(), "31.25%");
    const center = viewport.viewToImage(500, 400);
    near(center.u, 0.5);
    near(center.v, 0.5);
});

test("INPUT-03：捏合缩放的锚点留在手势中心", () => {
    const viewport = open(make());
    const before = viewport.viewToImage(300, 200);
    assert.equal(viewport.zoomBy(1.5, { x: 300, y: 200 }), true);
    const after = viewport.viewToImage(300, 200);
    near(after.u, before.u);
    near(after.v, before.v);
    near(viewport.physicalScale, 0.375);
});

test("IMAGE-05：平移钳制为显示尺寸与视口尺寸之差的一半", () => {
    const viewport = open(make());
    viewport.toggleFit();
    assert.equal(viewport.panBy(5000, 5000), true);
    const rect = viewport.imageRect();
    near(rect.x, 0);
    near(rect.y, 0);
    near(rect.x + rect.w, 4000);
    near(rect.y + rect.h, 3000);
    assert.equal(viewport.panBy(100, 100), false);
});

test("IMAGE-05：小于视口的轴始终居中", () => {
    const viewport = open(make(), { width: 4000, height: 600 });
    viewport.toggleFit();
    near(viewport.imageRect().y, 100);
    assert.equal(viewport.panBy(0, 200), false);
    near(viewport.imageRect().y, 100);
    assert.equal(viewport.panBy(100, 0), true);
});

test("IMAGE-05：适应态不可平移", () => {
    const viewport = open(make());
    const before = viewport.imageRect();
    assert.equal(viewport.panBy(10, 10), false);
    assert.deepEqual(viewport.imageRect(), before);
});

test("IMAGE-06：centerOnNormalized 只改视口中心，保持倍率与适应态", () => {
    const viewport = open(make());
    // 适应态没有可移动的余量，框心本来就在中心。
    assert.equal(viewport.centerOnNormalized(0.2, 0.2), false);
    assert.equal(viewport.isFit, true);

    viewport.centerOnPoint(0.5, 0.5);
    const scale = viewport.scale;
    assert.equal(viewport.centerOnNormalized(0.7, 0.6), true);
    near(viewport.scale, scale);
    const center = viewport.viewToImage(500, 400);
    near(center.u, 0.7);
    near(center.v, 0.6);

    // 视口中心按 IMAGE-05 钳制在合法范围。
    viewport.centerOnNormalized(2, -1);
    const rect = viewport.imageRect();
    near(rect.x + rect.w, 1000);
    near(rect.y, 0);
});

test("IMAGE-05：视口尺寸变化保持中心图像点与倍率", () => {
    const viewport = open(make());
    viewport.centerOnPoint(0.7, 0.3);
    near(viewport.scale, 1);
    viewport.setViewSize(800, 600);
    near(viewport.scale, 1);
    const center = viewport.viewToImage(400, 300);
    near(center.u, 0.7);
    near(center.v, 0.3);
});

test("IMAGE-05：适应态尺寸变化重新适应", () => {
    const viewport = open(make());
    near(viewport.scale, 0.25);
    viewport.setViewSize(500, 800);
    near(viewport.scale, 0.125);
    const rect = viewport.imageRect();
    near(rect.x, 0);
    near(rect.w, 500);
    near(rect.y, (800 - 375) / 2);
    assert.equal(viewport.isFit, true);
});

test("IMAGE-05：视口变大后偏移被钳制回居中", () => {
    const viewport = open(make());
    viewport.toggleFit();
    viewport.setViewSize(5000, 4000);
    near(viewport.scale, 1);
    near(viewport.imageRect().x, 500);
    near(viewport.imageRect().y, 500);
});

test("IMAGE-05：缩小回适应后平移归零", () => {
    const viewport = open(make());
    viewport.toggleFit();
    viewport.panBy(500, 300);
    assert.equal(viewport.isFit, false);
    viewport.zoomPreset(-1, PRESETS);
    viewport.zoomPreset(-1, PRESETS);
    viewport.zoomPreset(-1, PRESETS);
    viewport.zoomPreset(-1, PRESETS);
    assert.equal(viewport.isFit, true);
    near(viewport.scale, 0.25);
    near(viewport.snapshot().offset.x, 0);
    near(viewport.snapshot().offset.y, 0);
});

test("SHELL-05：dpr 变化保持同一物理倍率与中心图像点", () => {
    const viewport = open(make());
    viewport.centerOnPoint(0.6, 0.4);
    near(viewport.physicalScale, 1);
    assert.equal(viewport.setDpr(2), true);
    near(viewport.scale, 0.5);
    near(viewport.physicalScale, 1);
    const center = viewport.viewToImage(500, 400);
    near(center.u, 0.6);
    near(center.v, 0.4);
});

test("IMAGE-08：方向码 5–8 宽高换轴", () => {
    assert.deepEqual(displaySize(6000, 4000, 1), { width: 6000, height: 4000 });
    assert.deepEqual(displaySize(6000, 4000, 4), { width: 6000, height: 4000 });
    for (const orientation of [5, 6, 7, 8]) {
        assert.deepEqual(displaySize(6000, 4000, orientation), { width: 4000, height: 6000 });
    }
    assert.deepEqual(displaySize(6000, 4000), { width: 6000, height: 4000 });
    assert.deepEqual(displaySize(6000, 4000, 0), { width: 6000, height: 4000 });
});

test("IMAGE-09：定位到指定点直接到物理 100% 并退出跟随", () => {
    const viewport = open(make(), { focus: { x: 0.5, y: 0.5 } });
    viewport.toggleFit();
    assert.equal(viewport.followsFocus, true);
    assert.equal(viewport.centerOnPoint(0.6, 0.4), true);
    near(viewport.physicalScale, 1);
    assert.equal(viewport.followsFocus, false);
    const center = viewport.viewToImage(500, 400);
    near(center.u, 0.6);
    near(center.v, 0.4);
});

test("IMAGE-09：目标点受边界钳制", () => {
    const viewport = open(make());
    assert.equal(viewport.centerOnPoint(0.9, 0.1), true);
    near(viewport.physicalScale, 1);
    const rect = viewport.imageRect();
    near(rect.x + rect.w, 1000);
    near(rect.y, 0);
    const center = viewport.viewToImage(500, 400);
    near(center.u, 0.875);
    near(center.v, 0.13333333333333333);
});

test("IMAGE-09：小图定位保持适应", () => {
    const viewport = open(make(), { width: 600, height: 400 });
    viewport.centerOnPoint(0.2, 0.2);
    assert.equal(viewport.isFit, true);
    near(viewport.scale, 1000 / 600);
    near(viewport.snapshot().offset.x, 0);
    near(viewport.snapshot().offset.y, 0);
});

test("IMAGE-09：定位后拖动从新状态继续", () => {
    const viewport = open(make());
    viewport.centerOnPoint(0.6, 0.4);
    const before = viewport.imageRect();
    assert.equal(viewport.panBy(10, 10), true);
    const after = viewport.imageRect();
    near(after.x, before.x + 10);
    near(after.y, before.y + 10);
});

test("IMAGE-06：导航图只在高倍率下可见，缩略图按长边等比", () => {
    const landscape = open(make());
    const fitNav = landscape.navigator();
    assert.equal(fitNav.visible, false);
    near(fitNav.thumbWidth, 150);
    near(fitNav.thumbHeight, 112.5);
    landscape.toggleFit();
    assert.equal(landscape.navigator().visible, true);

    const portrait = open(make(), { width: 3000, height: 4000 });
    const portraitNav = portrait.navigator();
    near(portraitNav.thumbWidth, 112.5);
    near(portraitNav.thumbHeight, 150);
});

test("IMAGE-06：可见框与视口比例一致并夹在缩略图内", () => {
    const viewport = open(make());
    viewport.toggleFit();
    const nav = viewport.navigator();
    near(nav.frame.x, 56.25);
    near(nav.frame.y, 41.25);
    near(nav.frame.w, 37.5);
    near(nav.frame.h, 30);

    viewport.panBy(5000, 5000);
    const corner = viewport.navigator();
    near(corner.frame.x, 0);
    near(corner.frame.y, 0);
    near(corner.frame.w, 37.5);
    near(corner.frame.h, 30);
    assert.ok(corner.frame.x + corner.frame.w <= 150 + 1e-9);
    assert.ok(corner.frame.y + corner.frame.h <= 112.5 + 1e-9);
});

test("IMAGE-03：适应态不因倍率升级；非适应态按物理 100% 与代理覆盖", () => {
    const viewport = open(make());
    assert.equal(viewport.isFit, true);
    assert.equal(viewport.needsFull(2000), false);
    assert.equal(viewport.needsFull(null), false);
    viewport.toggleFit();
    assert.equal(viewport.needsFull(100000), true);
    viewport.zoomBy(0.5);
    assert.equal(viewport.isFit, false);
    assert.equal(viewport.needsFull(900), true);
    assert.equal(viewport.needsFull(4000), false);

    const small = open(make(), { width: 600, height: 400 });
    assert.ok(small.physicalScale > 1);
    assert.equal(small.isFit, true);
    assert.equal(small.needsFull(2000), false, "适应态倍率高于 100% 也不升 Full");
    assert.equal(small.needsFull(null), false);

    const hidpi = open(make({ dpr: 2 }));
    assert.equal(hidpi.needsFull(2000), false);
    hidpi.toggleFit();
    assert.equal(hidpi.physicalScale, 1);
    assert.equal(hidpi.needsFull(2000), true);
});

test("IMAGE-03：代理目标长边 = 视口长边物理像素 × overscan", () => {
    const viewport = make();
    assert.equal(viewport.proxyTargetLongEdge(), 2000);
    const hidpi = make({ dpr: 2 });
    assert.equal(hidpi.proxyTargetLongEdge(), 4000);
    const narrow = make({ view: { width: 500, height: 1200 } });
    assert.equal(narrow.proxyTargetLongEdge(), 2400);
});

test("状态：无图时查询安全，setImage(null) 清空", () => {
    const viewport = make();
    assert.equal(viewport.hasImage, false);
    assert.equal(viewport.percentText(), "");
    assert.equal(viewport.navigator().visible, false);
    assert.equal(viewport.zoomBy(2), false);
    assert.equal(viewport.zoomPreset(1, PRESETS), false);
    assert.equal(viewport.panBy(10, 10), false);
    assert.equal(viewport.toggleFit(), false);
    assert.equal(viewport.centerOnPoint(0.5, 0.5), false);

    open(viewport);
    assert.equal(viewport.hasImage, true);
    assert.equal(viewport.setImage(null), false);
    assert.equal(viewport.hasImage, false);
    const snapshot = JSON.parse(JSON.stringify(viewport.snapshot()));
    assert.equal(snapshot.hasImage, false);
});
