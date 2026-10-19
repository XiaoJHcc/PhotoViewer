import { test } from "node:test";
import assert from "node:assert/strict";
import { createTokens } from "../src/core/tokens.js";
import { manualClock } from "../src/core/clock.js";
import {
    filmMetrics,
    contentLength,
    itemStart,
    visibleRange,
    centerOffset,
    easeOutCubic,
    FilmScroller,
    FilmPositioner,
} from "../src/core/filmscroll.js";

const t = createTokens();

function createScroller({ start = 0, reducedMotion = false } = {}) {
    const clock = manualClock();
    const state = { offset: start, reduced: reducedMotion, writes: [] };
    const scroller = new FilmScroller({
        t,
        clock,
        getOffset: () => state.offset,
        setOffset: (value) => {
            state.offset = value;
            state.writes.push({ value, time: clock.now() });
        },
        reducedMotion: () => state.reduced,
    });
    return { clock, state, scroller };
}

function fakeScroller() {
    const calls = { requested: [], cancels: 0, clamps: [] };
    return {
        calls,
        cancel() {
            calls.cancels += 1;
        },
        clamp(length, viewport) {
            calls.clamps.push([length, viewport]);
        },
        requestCentered(geometry) {
            calls.requested.push(geometry);
        },
    };
}

function geometry(index = 3, count = 10, axis = "x", viewportLength = 500) {
    return { index, count, axis, viewportLength };
}

test("FILM-02：按令牌推导卡片宽高、步长与列表内边距", () => {
    const m = filmMetrics(t);
    assert.deepEqual(m, {
        cardWidth: 96,
        cardHeight: 144,
        stepX: 96,
        stepY: 144,
        padLeft: 10,
        padTop: 5,
        padRight: 10,
        padBottom: 10,
    });
    assert.throws(() => contentLength(1, "z", t), /未知胶片栏方向/);
});

test("FILM-02：横向与纵向内容长度分别取各自的前后内边距", () => {
    assert.equal(contentLength(3, "x", t), 10 + 3 * 96 + 10);
    assert.equal(contentLength(3, "y", t), 5 + 3 * 144 + 10);
    assert.equal(contentLength(0, "x", t), 20);
    assert.equal(contentLength(0, "y", t), 15);
});

test("FILM-02：横向短列表整段居中，首项起点不越过 padLeft；纵向列表从顶部起排", () => {
    assert.equal(contentLength(1, "x", t), 116);
    assert.equal(itemStart(0, 1, "x", 500, t), (500 - 96) / 2);
    const start3 = itemStart(0, 3, "x", 500, t);
    assert.equal(start3, 106);
    assert.equal(500 - (start3 + 3 * 96), start3);
    assert.equal(itemStart(2, 3, "x", 500, t), 106 + 2 * 96);
    assert.equal(itemStart(2, 10, "x", 500, t), 10 + 2 * 96);
    assert.equal(itemStart(0, 1, "y", 500, t), 5);
    assert.equal(itemStart(1, 3, "y", 500, t), 5 + 144);
});

test("FILM-02：可见区间按卡片实际覆盖范围给出，含部分可见卡片", () => {
    assert.deepEqual(visibleRange(0, 500, 10, "x", t), [0, 5]);
    assert.deepEqual(visibleRange(300, 500, 10, "x", t), [3, 8]);
    assert.deepEqual(visibleRange(480, 500, 10, "x", t), [4, 9]);
    assert.deepEqual(visibleRange(0, 500, 3, "x", t), [0, 2]);
    assert.deepEqual(visibleRange(0, 500, 0, "x", t), [0, -1]);
    assert.deepEqual(visibleRange(0, 0, 10, "x", t), [0, -1]);
    assert.deepEqual(visibleRange(0, 500, 4, "y", t), [0, 3]);
});

test("FILM-02：定位目标 = 卡片起点 + (卡片长 − 视口长) / 2，并夹到合法范围", () => {
    assert.equal(centerOffset(0, 10, "x", 500, t), 0);
    assert.equal(centerOffset(5, 10, "x", 500, t), 288);
    assert.equal(centerOffset(9, 10, "x", 500, t), 480);
    assert.equal(centerOffset(5, 10, "y", 500, t), 547);
    assert.equal(centerOffset(9, 10, "y", 500, t), 955);
});

test("FILM-02：短列表不制造滚动空白，目标恒为 0", () => {
    for (const index of [0, 1, 2]) {
        assert.equal(centerOffset(index, 3, "x", 500, t), 0);
        assert.equal(centerOffset(index, 3, "y", 500, t), 0);
    }
    assert.equal(centerOffset(0, 1, "x", 1200, t), 0);
});

test("FILM-02：三次缓出 1 − (1 − t)³，单调且两端精确", () => {
    assert.equal(easeOutCubic(0), 0);
    assert.equal(easeOutCubic(0.5), 0.875);
    assert.equal(easeOutCubic(1), 1);
    assert.equal(easeOutCubic(-1), 0);
    assert.equal(easeOutCubic(2), 1);
    let previous = -1;
    for (let step = 0; step <= 20; step += 1) {
        const value = easeOutCubic(step / 20);
        assert.ok(value >= previous);
        previous = value;
    }
});

test("FILM-02：350 ms 后到达终点，中间帧单调且符合 1−(1−t)³", () => {
    const { clock, state, scroller } = createScroller();
    scroller.request(960);
    assert.equal(scroller.isAnimating, true);

    clock.advance(349);
    assert.equal(scroller.isAnimating, true);
    assert.ok(state.offset > 0 && state.offset < 960);
    assert.ok(state.writes.length > 10);
    let previous = 0;
    for (const write of state.writes) {
        assert.ok(write.value >= previous);
        previous = write.value;
        const expected = 960 * easeOutCubic(write.time / 350);
        assert.ok(Math.abs(write.value - expected) < 1e-9);
    }

    clock.advance(1);
    assert.equal(state.offset, 960);
    assert.equal(scroller.isAnimating, false);
    assert.equal(clock.pendingFrames(), 0);
});

test("FILM-02：位移小于 1 点直接到位，等于 1 点仍播放动画", () => {
    const { clock, state, scroller } = createScroller();
    scroller.request(0.5);
    assert.equal(state.offset, 0.5);
    assert.equal(scroller.isAnimating, false);
    assert.equal(clock.pendingFrames(), 0);
    assert.deepEqual(state.writes, [{ value: 0.5, time: 0 }]);

    const second = createScroller();
    second.scroller.request(1);
    assert.equal(second.scroller.isAnimating, true);
    second.clock.advance(16);
    assert.ok(second.state.offset > 0 && second.state.offset < 1);
});

test("FILM-03：减少动态效果时直接定位到终点", () => {
    const { clock, state, scroller } = createScroller({ reducedMotion: true });
    scroller.request(400);
    assert.equal(state.offset, 400);
    assert.equal(scroller.isAnimating, false);
    assert.equal(clock.pendingFrames(), 0);
});

test("FILM-02：新请求从当前显示位置接续，最后一次请求生效", () => {
    const { clock, state, scroller } = createScroller();
    scroller.request(1000);
    clock.advance(100);
    const before = state.offset;
    assert.ok(before > 0 && before < 1000);

    scroller.request(200);
    assert.equal(scroller.isAnimating, true);
    clock.advance(16);
    const expectedFirstFrame = before + (200 - before) * easeOutCubic(16 / 350);
    assert.ok(Math.abs(state.offset - expectedFirstFrame) < 1e-9);
    assert.ok(state.offset < before && state.offset > 200);

    clock.advance(1000);
    assert.equal(state.offset, 200);
    assert.equal(scroller.isAnimating, false);
});

test("FILM-03：取消后偏移停在当前帧，旧目标不再推进", () => {
    const { clock, state, scroller } = createScroller();
    scroller.request(800);
    clock.advance(100);
    const stopped = state.offset;
    scroller.cancel();
    assert.equal(scroller.isAnimating, false);
    assert.equal(clock.pendingFrames(), 0);

    clock.advance(1000);
    assert.equal(state.offset, stopped);
    assert.ok(stopped < 800);
});

test("FILM-01：普通尺寸变化只夹取偏移，不定位也不起动画", () => {
    const { clock, state, scroller } = createScroller({ start: 500 });
    scroller.clamp(300, 200);
    assert.equal(state.offset, 100);
    assert.equal(scroller.isAnimating, false);
    assert.equal(clock.pendingFrames(), 0);

    scroller.clamp(300, 400);
    assert.equal(state.offset, 0);
    scroller.clamp(1000, 200);
    assert.equal(state.offset, 0);

    const inside = createScroller({ start: 50 });
    inside.scroller.clamp(1000, 200);
    assert.equal(inside.state.offset, 50);
    assert.deepEqual(inside.state.writes, []);
});

test("FILM-03：动画中缩小视口后偏移不越界", () => {
    const { clock, state, scroller } = createScroller();
    scroller.request(400);
    clock.advance(50);
    assert.ok(state.offset > 0);

    scroller.clamp(100, 100);
    assert.equal(state.offset, 0);
    const writesBefore = state.writes.length;
    clock.advance(1000);
    assert.equal(state.offset, 0);
    for (const write of state.writes.slice(writesBefore)) assert.equal(write.value, 0);
});

test("FILM-02：列表增长后的新定位不受旧夹取范围限制", () => {
    const { clock, state, scroller } = createScroller({ start: 80 });
    scroller.clamp(100, 100);
    assert.equal(state.offset, 0);

    scroller.requestCentered(geometry(5, 10, "x", 500));
    clock.advance(350);
    assert.equal(state.offset, 288);
});

test("FILM-02：requestCentered 按几何算目标并夹取", () => {
    const { clock, state, scroller } = createScroller();
    scroller.requestCentered(geometry(5, 10, "x", 500));
    clock.advance(350);
    assert.equal(state.offset, 288);

    const vertical = createScroller();
    vertical.scroller.requestCentered(geometry(5, 10, "y", 500));
    vertical.clock.advance(350);
    assert.equal(vertical.state.offset, 547);
});

test("FILM-01：点选卡片只撤销定位，不发出请求", () => {
    const scroller = fakeScroller();
    const positioner = new FilmPositioner({ scroller, geometry: () => geometry() });
    positioner.onSelectByClick();
    assert.deepEqual(scroller.calls.requested, []);
    assert.equal(scroller.calls.cancels, 1);
});

test("FILM-01：上一张/下一张与打开共用同一导航定位请求", () => {
    const scroller = fakeScroller();
    const positioner = new FilmPositioner({ scroller, geometry: () => geometry(4, 10, "y", 400) });
    positioner.onNavigate();
    assert.deepEqual(scroller.calls.requested, [geometry(4, 10, "y", 400)]);
    assert.equal(scroller.calls.cancels, 0);

    const opened = fakeScroller();
    const opening = new FilmPositioner({ scroller: opened, geometry: () => geometry(0, 10) });
    opening.onNavigate();
    assert.equal(opened.calls.requested.length, 1);
});

test("FILM-01：切换上一张、排序筛选、评分刷新、缩略图就绪都不定位", () => {
    const scroller = fakeScroller();
    const positioner = new FilmPositioner({ scroller, geometry: () => geometry() });
    positioner.onExchange();
    positioner.onSortOrFilter();
    positioner.onRatingRefresh();
    positioner.onThumbnailReady();
    assert.deepEqual(scroller.calls.requested, []);
    assert.equal(scroller.calls.cancels, 0);
    assert.deepEqual(scroller.calls.clamps, []);
});

test("FILM-01：手动滚动撤销请求与动画，只交还位置", () => {
    const scroller = fakeScroller();
    const positioner = new FilmPositioner({ scroller, geometry: () => geometry() });
    positioner.onUserScroll();
    assert.deepEqual(scroller.calls.requested, []);
    assert.equal(scroller.calls.cancels, 1);
});

test("FILM-01：普通尺寸变化只调用夹取", () => {
    const scroller = fakeScroller();
    const positioner = new FilmPositioner({ scroller, geometry: () => geometry() });
    positioner.onResize(980, 500);
    assert.deepEqual(scroller.calls.clamps, [[980, 500]]);
    assert.deepEqual(scroller.calls.requested, []);
});

test("FILM-01：列表替换、换向、隐藏取消；重新显示后 onReshow 按新几何定位", () => {
    const scroller = fakeScroller();
    let current = geometry(3, 10, "x", 500);
    const positioner = new FilmPositioner({ scroller, geometry: () => current });

    positioner.onListReplaced();
    positioner.onAxisChanged();
    positioner.onHidden();
    assert.equal(scroller.calls.cancels, 3);
    assert.deepEqual(scroller.calls.requested, []);

    current = geometry(3, 10, "y", 440);
    positioner.onShown();
    assert.deepEqual(scroller.calls.requested, []);
    positioner.onReshow();
    assert.deepEqual(scroller.calls.requested, [geometry(3, 10, "y", 440)]);
});

test("FILM-02：当前项不在列表、列表为空或栏位隐藏时不定位", () => {
    const scroller = fakeScroller();
    const positioner = new FilmPositioner({ scroller, geometry: () => null });
    positioner.onNavigate();
    positioner.onReshow();
    positioner.layoutReady();
    assert.deepEqual(scroller.calls.requested, []);
    assert.equal(positioner.isPending, false);
});

test("FILM-02：布局未就绪时排队，layoutReady 按最新几何只执行一次", () => {
    const scroller = fakeScroller();
    let ready = false;
    let current = geometry(1, 10, "x", 0);
    const positioner = new FilmPositioner({
        scroller,
        geometry: () => {
            if (!ready) throw new Error("布局未完成");
            return current;
        },
    });

    positioner.onNavigate();
    positioner.onNavigate();
    assert.equal(positioner.isPending, true);
    assert.deepEqual(scroller.calls.requested, []);

    ready = true;
    current = geometry(7, 12, "x", 640);
    positioner.layoutReady();
    assert.equal(positioner.isPending, false);
    assert.deepEqual(scroller.calls.requested, [geometry(7, 12, "x", 640)]);

    positioner.layoutReady();
    assert.equal(scroller.calls.requested.length, 1);
});

test("FILM-02：viewportLength 为 0 时继续排队，就绪后定位一次", () => {
    const scroller = fakeScroller();
    let current = geometry(2, 10, "x", 0);
    const positioner = new FilmPositioner({ scroller, geometry: () => current });

    positioner.onNavigate();
    assert.equal(positioner.isPending, true);
    positioner.layoutReady();
    assert.equal(positioner.isPending, true);
    assert.deepEqual(scroller.calls.requested, []);

    current = geometry(2, 10, "x", 720);
    positioner.layoutReady();
    assert.equal(positioner.isPending, false);
    assert.deepEqual(scroller.calls.requested, [geometry(2, 10, "x", 720)]);
});

test("FILM-03：排队期间用户接管或隐藏，布局就绪后不再补做定位", () => {
    const clicks = fakeScroller();
    const byClick = new FilmPositioner({ scroller: clicks, geometry: () => geometry(0, 0, "x", 0) });
    byClick.onNavigate();
    assert.equal(byClick.isPending, true);
    byClick.onSelectByClick();
    assert.equal(byClick.isPending, false);
    assert.equal(clicks.calls.cancels, 1);
    byClick.layoutReady();
    assert.deepEqual(clicks.calls.requested, []);

    const hidden = fakeScroller();
    const byHide = new FilmPositioner({ scroller: hidden, geometry: () => geometry(0, 0, "x", 0) });
    byHide.onNavigate();
    byHide.onHidden();
    byHide.layoutReady();
    assert.deepEqual(hidden.calls.requested, []);

    const scrolled = fakeScroller();
    const byScroll = new FilmPositioner({ scroller: scrolled, geometry: () => geometry(0, 0, "x", 0) });
    byScroll.onNavigate();
    byScroll.onUserScroll();
    byScroll.layoutReady();
    assert.deepEqual(scrolled.calls.requested, []);
});

test("FILM-01：排队期间普通尺寸变化只夹取，不提前执行请求", () => {
    const scroller = fakeScroller();
    const positioner = new FilmPositioner({ scroller, geometry: () => geometry(1, 10, "x", 0) });
    positioner.onNavigate();
    positioner.onResize(980, 0);
    assert.deepEqual(scroller.calls.clamps, [[980, 0]]);
    assert.deepEqual(scroller.calls.requested, []);
    assert.equal(positioner.isPending, true);
});

test("FILM-04：换向后 onReshow 使用新方向的卡片几何", () => {
    const { clock, state, scroller } = createScroller();
    let current = geometry(5, 10, "x", 500);
    const positioner = new FilmPositioner({ scroller, geometry: () => current });

    positioner.onAxisChanged();
    current = geometry(5, 10, "y", 500);
    positioner.onReshow();
    clock.advance(350);
    assert.equal(state.offset, centerOffset(5, 10, "y", 500, t));
});

test("OPEN-05：外部打开非首张时排队，布局就绪后定位到目标项附近", () => {
    const scroller = fakeScroller();
    let current = geometry(42, 120, "x", 0);
    const positioner = new FilmPositioner({ scroller, geometry: () => current });

    positioner.onNavigate();
    assert.equal(positioner.isPending, true);

    current = geometry(42, 120, "x", 720);
    positioner.layoutReady();
    assert.deepEqual(scroller.calls.requested, [geometry(42, 120, "x", 720)]);

    const target = centerOffset(42, 120, "x", 720, t);
    assert.ok(target > 0 && target <= contentLength(120, "x", t) - 720);
});
