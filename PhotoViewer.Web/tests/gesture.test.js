import { test } from "node:test";
import assert from "node:assert/strict";
import { PhotoGesture } from "../src/core/input.js";

function point(gesture, id) {
    return gesture.pointers.get(id);
}

test("IMAGE-06：鼠标按在导航图上进入拖动，移动只回报抓取偏移", () => {
    const gesture = new PhotoGesture();
    const start = gesture.begin({ id: 1, x: 100, y: 100, zoomed: true, navigator: { offsetU: 0.25, offsetV: -0.1 } });
    assert.equal(start.mode, "navigator");
    assert.equal(start.navigatorCanceled, undefined);
    const moved = gesture.move({ id: 1, x: 120, y: 110 });
    assert.equal(moved.mode, "navigator");
    assert.equal(moved.offsetU, 0.25);
    assert.equal(moved.offsetV, -0.1);
    const ended = gesture.end(1);
    assert.equal(ended.navigatorEnded, true);
    assert.equal(ended.tap, false);
    assert.equal(gesture.mode, "none");
});

test("IMAGE-06：单指拖动导航图时第二指按下即取消拖动并转为捏合", () => {
    const gesture = new PhotoGesture();
    gesture.begin({ id: 1, x: 200, y: 200, touch: true, zoomed: true, navigator: { offsetU: 0, offsetV: 0 } });
    assert.equal(gesture.mode, "navigator");

    const second = gesture.begin({ id: 2, x: 260, y: 200, touch: true, zoomed: true });
    assert.equal(second.mode, "pinch");
    assert.equal(second.navigatorCanceled, true);
    assert.equal(gesture.nav, null, "捏合开始后仍保留导航图抓取");
    assert.equal(gesture.pointerCount, 2);

    const moved = gesture.move({ id: 2, x: 320, y: 200 });
    assert.equal(moved.mode, "pinch");
    assert.equal(moved.ratio, 2, "两指间距从 60 增到 120，倍率应为 2");
    assert.deepEqual(moved.center, { x: 260, y: 200 });

    const ended = gesture.end(1);
    assert.equal(ended.mode, "pinch");
    assert.equal(ended.pinchEnded, true);
    assert.equal(gesture.mode, "none");
    assert.equal(gesture.pointerCount, 1);
});

test("IMAGE-06：主图起手后第二指落在导航图上仍归主图捏合", () => {
    const gesture = new PhotoGesture();
    gesture.begin({ id: 7, x: 400, y: 300, touch: true, zoomed: false });
    assert.equal(gesture.mode, "swipe");

    const second = gesture.begin({
        id: 8,
        x: 440,
        y: 300,
        touch: true,
        zoomed: false,
        navigator: { offsetU: 0.2, offsetV: 0.3 },
    });
    assert.equal(second.mode, "pinch");
    assert.equal(second.navigatorCanceled, false, "第二指不应另起一次导航图拖动");
    assert.equal(gesture.nav, null);
    assert.equal(gesture.move({ id: 8, x: 480, y: 300 }).ratio, 2);
});

test("IMAGE-06：导航图上的轻点不触发主图轻点", () => {
    const gesture = new PhotoGesture();
    gesture.begin({ id: 3, x: 50, y: 50, touch: true, zoomed: true, navigator: { offsetU: 0, offsetV: 0 } });
    const ended = gesture.end(3);
    assert.equal(ended.tap, false);
    assert.equal(ended.navigatorEnded, true);
});

test("INPUT-06：适应态单指触摸为轻扫，放大态为平移", () => {
    const fit = new PhotoGesture();
    assert.equal(fit.begin({ id: 1, x: 10, y: 10, touch: true, zoomed: false }).mode, "swipe");
    assert.equal(fit.end(1).tap, true);

    const zoomed = new PhotoGesture();
    assert.equal(zoomed.begin({ id: 1, x: 10, y: 10, touch: true, zoomed: true }).mode, "pan");
    assert.deepEqual(zoomed.move({ id: 1, x: 30, y: 25 }), { mode: "pan", dx: 20, dy: 15 });
});

test("INPUT-07：轻扫/捏合期间超出容差不再算轻点", () => {
    const gesture = new PhotoGesture({ tapSlop: 6 });
    gesture.begin({ id: 1, x: 0, y: 0, touch: true, zoomed: false });
    gesture.move({ id: 1, x: 20, y: 0 });
    assert.equal(point(gesture, 1).x, 20);
    assert.equal(gesture.tap, false);
    assert.equal(gesture.end(1).tap, false);
});
