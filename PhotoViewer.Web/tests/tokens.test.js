import { test } from "node:test";
import assert from "node:assert/strict";
import { createTokens, colorToCss } from "../src/core/tokens.js";
import { manualClock } from "../src/core/clock.js";

test("令牌：基准值可读，缺失令牌立即抛错", () => {
    const t = createTokens();
    assert.equal(t("film.card.width"), 96);
    assert.equal(t("motion.film.scroll.duration"), 350);
    assert.throws(() => t("film.card.nope"), /缺少设计令牌/);
});

test("令牌：平台配置档只覆盖登记的平台值", () => {
    assert.equal(createTokens("web")("filter.toggle.width"), 90);
    assert.equal(createTokens("mac")("filter.toggle.width"), 90);
    assert.throws(() => createTokens("vr"), /未知平台配置档/);
});

test("令牌：#AARRGGBB 转 CSS rgba", () => {
    assert.equal(colorToCss("#0A84FF"), "#0A84FF");
    assert.equal(colorToCss("#88000000"), "rgba(0, 0, 0, 0.533)");
});

test("假时钟：定时器按到期顺序执行，帧回调每步一次", () => {
    const clock = manualClock();
    const order = [];
    clock.setTimeout(() => order.push("b"), 300);
    clock.setTimeout(() => order.push("a"), 100);
    let frames = 0;
    const tick = () => { frames++; if (clock.now() < 100) clock.requestFrame(tick); };
    clock.requestFrame(tick);
    clock.advance(400);
    assert.deepEqual(order, ["a", "b"]);
    assert.ok(frames >= 6 && frames <= 8);
    assert.equal(clock.pendingTimers(), 0);
});
