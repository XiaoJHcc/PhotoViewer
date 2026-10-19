import { test } from "node:test";
import assert from "node:assert/strict";
import { HOTKEYS } from "../src/core/settings.js";
import { createTokens } from "../src/core/tokens.js";
import { manualClock } from "../src/core/clock.js";
import {
    gestureFromKey,
    gestureFromMouse,
    WheelInterpreter,
    CommandMap,
    isRepeatable,
    KeyDispatcher,
    gestureText,
    tooltipText,
    SwipeRecognizer,
    LongPressRecognizer,
    WHEEL_PINCH_SCALE,
} from "../src/core/input.js";

function dispatcherFor(hotkeys = HOTKEYS, isSuppressed = () => false) {
    return new KeyDispatcher({ commandMap: new CommandMap(hotkeys), isSuppressed });
}

test("INPUT-01：默认命令表逐行主/次绑定都命中同一命令", () => {
    const map = new CommandMap(HOTKEYS);
    for (const row of HOTKEYS) {
        for (const slot of ["primary", "secondary"]) {
            const gesture = row[slot];
            if (gesture === null) {
                assert.equal(map.bindingsOf(row.command)[slot], null, `${row.command} ${slot} 应为空槽`);
                continue;
            }
            assert.equal(map.commandFor(gesture), row.command, `${row.command} ${slot} ${gesture}`);
        }
    }
    assert.equal(map.bindingsOf("NoSuchCommand"), null);
});

test("INPUT-01：冲突取设置列表顺序的第一条，双方槽位都标记", () => {
    const hotkeys = [
        { command: "A", primary: "KeyQ", secondary: null },
        { command: "B", primary: "KeyQ", secondary: "KeyW" },
        { command: "C", primary: "KeyW", secondary: null },
    ];
    const map = new CommandMap(hotkeys);
    assert.equal(map.commandFor("KeyQ"), "A");
    assert.equal(map.commandFor("KeyW"), "B");
    assert.deepEqual(map.conflicts(), [
        ["A", "primary"],
        ["B", "primary"],
        ["B", "secondary"],
        ["C", "primary"],
    ]);
    assert.deepEqual(map.bindingsOf("B"), { primary: "KeyQ", secondary: "KeyW" });
});

test("INPUT-01：平台键位等价也参与冲突检测，setHotkeys 立即生效", () => {
    const map = new CommandMap([
        { command: "Plus", primary: "Ctrl+Equal", secondary: null },
        { command: "ShiftPlus", primary: "Ctrl+Shift+Equal", secondary: null },
    ]);
    assert.deepEqual(map.conflicts(), [["Plus", "primary"], ["ShiftPlus", "primary"]]);
    map.setHotkeys(HOTKEYS);
    assert.equal(map.commandFor("Ctrl+Equal"), "ZoomInPreset");
    assert.deepEqual(map.conflicts(), []);
});

test("INPUT-02：抑制态不派发且不标记已处理，恢复后立即派发", () => {
    let suppressed = true;
    const dispatcher = dispatcherFor(HOTKEYS, () => suppressed);
    assert.deepEqual(dispatcher.keyDown({ code: "ArrowRight" }), { command: null, handled: false });
    suppressed = false;
    assert.deepEqual(dispatcher.keyDown({ code: "ArrowRight" }), { command: "Next", handled: true });
    suppressed = true;
    assert.deepEqual(dispatcher.keyDown({ code: "ArrowRight" }), { command: null, handled: false });
    suppressed = false;
    assert.deepEqual(dispatcher.keyDown({ code: "ArrowLeft" }), { command: "Previous", handled: true });
});

test("INPUT-02/MENU-03：菜单展开或录入期间照片快捷键暂停，关闭后恢复", () => {
    let menuOpen = false;
    const dispatcher = dispatcherFor(HOTKEYS, () => menuOpen);
    assert.equal(dispatcher.keyDown({ code: "Digit3" }).command, "SetRating3");
    menuOpen = true;
    assert.deepEqual(dispatcher.keyDown({ code: "Digit3" }), { command: null, handled: false });
    menuOpen = false;
    assert.equal(dispatcher.keyDown({ code: "Digit3" }).command, "SetRating3");
});

test("INPUT-05：只有上一张/下一张/预设缩放可重复", () => {
    for (const command of ["Previous", "Next", "ZoomInPreset", "ZoomOutPreset"]) {
        assert.equal(isRepeatable(command), true, command);
    }
    for (const command of ["Open", "Exchange", "Fit", "ToggleEnhance", "SetRating3", "ToggleAnalysisView"]) {
        assert.equal(isRepeatable(command), false, command);
    }
});

test("INPUT-05：按住可重复命令继续执行，其余只执行一次但吞掉按键", () => {
    const dispatcher = dispatcherFor();
    assert.deepEqual(dispatcher.keyDown({ code: "ArrowRight" }), { command: "Next", handled: true });
    assert.deepEqual(dispatcher.keyDown({ code: "ArrowRight", repeat: true }), { command: "Next", handled: true });
    assert.deepEqual(dispatcher.keyDown({ code: "Digit3" }), { command: "SetRating3", handled: true });
    assert.deepEqual(dispatcher.keyDown({ code: "Digit3", repeat: true }), { command: null, handled: true });
    assert.deepEqual(dispatcher.keyDown({ code: "KeyF" }), { command: "Fit", handled: true });
    assert.deepEqual(dispatcher.keyDown({ code: "KeyF", repeat: true }), { command: null, handled: true });
});

test("INPUT-05：失焦清空按住状态，返回后不补发积压重复", () => {
    const dispatcher = dispatcherFor();
    assert.deepEqual(dispatcher.keyDown({ code: "ArrowRight" }), { command: "Next", handled: true });
    dispatcher.blur();
    assert.deepEqual(dispatcher.keyDown({ code: "ArrowRight", repeat: true }), { command: null, handled: true });
    assert.deepEqual(dispatcher.keyDown({ code: "ArrowRight" }), { command: "Next", handled: true });
});

test("INPUT-05：keyUp 后重复事件不再执行", () => {
    const dispatcher = dispatcherFor();
    assert.equal(dispatcher.keyDown({ code: "ArrowLeft" }).command, "Previous");
    dispatcher.keyUp({ code: "ArrowLeft" });
    assert.deepEqual(dispatcher.keyDown({ code: "ArrowLeft", repeat: true }), { command: null, handled: true });
});

test("INPUT-11：未绑定按键原样放行，不标记已处理", () => {
    const dispatcher = dispatcherFor();
    assert.deepEqual(dispatcher.keyDown({ code: "KeyQ" }), { command: null, handled: false });
    assert.deepEqual(dispatcher.keyDown({ code: "KeyQ", ctrlKey: true }), { command: null, handled: false });
    assert.deepEqual(dispatcher.keyDown({ code: "ArrowLeft", altKey: true }), { command: null, handled: false });
    assert.deepEqual(dispatcher.keyDown({ code: "ShiftLeft", shiftKey: true }), { command: null, handled: false });
    assert.deepEqual(dispatcher.keyDown(null), { command: null, handled: false });
});

test("SETTINGS-08：Apple 键盘映射下 ⌘ 视同 Ctrl、⌃ 视同 Meta", () => {
    const map = new CommandMap(HOTKEYS);
    assert.equal(gestureFromKey({ code: "Digit0", metaKey: true }, { appleKeyboard: true }), "Ctrl+Digit0");
    assert.equal(map.commandFor(gestureFromKey({ code: "Digit0", metaKey: true }, { appleKeyboard: true })), "Fit");
    assert.equal(gestureFromKey({ code: "Digit0", ctrlKey: true }, { appleKeyboard: true }), "Meta+Digit0");
    assert.equal(map.commandFor(gestureFromKey({ code: "Digit0", ctrlKey: true }, { appleKeyboard: true })), null);
    assert.equal(gestureFromKey({ code: "Digit0", metaKey: true }), "Meta+Digit0");
    assert.equal(gestureFromKey({ code: "ControlLeft", ctrlKey: true }), null);
    assert.equal(gestureFromKey({ code: "ShiftRight", shiftKey: true }), null);
});

test("SETTINGS-11：+/- 需 Shift，小键盘加减归一到 Equal/Minus", () => {
    const map = new CommandMap(HOTKEYS);
    assert.equal(gestureFromKey({ code: "Equal", ctrlKey: true, shiftKey: true }), "Ctrl+Equal");
    assert.equal(gestureFromKey({ code: "NumpadAdd", ctrlKey: true }), "Ctrl+Equal");
    assert.equal(gestureFromKey({ code: "NumpadSubtract", ctrlKey: true }), "Ctrl+Minus");
    assert.equal(map.commandFor("Ctrl+Shift+Equal"), "ZoomInPreset");
    assert.equal(map.commandFor("Ctrl+NumpadAdd"), "ZoomInPreset");
    assert.equal(map.commandFor("Ctrl+NumpadSubtract"), "ZoomOutPreset");
    assert.equal(gestureFromKey({ code: "Equal", shiftKey: true }), "Equal");
});

test("INPUT-03：中键编码为 MiddleClick，其他鼠标键不编码", () => {
    assert.equal(gestureFromMouse({ button: 1 }), "MiddleClick");
    assert.equal(gestureFromMouse({ button: 0 }), null);
    assert.equal(gestureFromMouse({ button: 2 }), null);
    assert.equal(gestureFromMouse({ button: 1, ctrlKey: true }), "Ctrl+MiddleClick");
    assert.equal(gestureFromMouse({ button: 1, metaKey: true }, { appleKeyboard: true }), "Ctrl+MiddleClick");
    assert.equal(new CommandMap(HOTKEYS).commandFor("MiddleClick"), "Exchange");
});

test("INPUT-04：LINE/PAGE 离散事件按格累积，凑满一格才派发一次", () => {
    const wheel = new WheelInterpreter(manualClock());
    assert.equal(wheel.feed({ deltaX: 0, deltaY: -1, deltaMode: 1 }), null);
    assert.equal(wheel.feed({ deltaX: 0, deltaY: -1, deltaMode: 1 }), null);
    assert.deepEqual(wheel.feed({ deltaX: 0, deltaY: -1, deltaMode: 1 }), {
        kind: "discrete",
        gesture: "WheelUp",
        steps: 1,
    });
    assert.deepEqual(wheel.feed({ deltaX: 0, deltaY: 1, deltaMode: 2 }), {
        kind: "discrete",
        gesture: "WheelDown",
        steps: 1,
    });
});

test("INPUT-04：像素模式识别鼠标滚轮，一格一次、多格多步", () => {
    const wheel = new WheelInterpreter(manualClock());
    assert.deepEqual(wheel.feed({ deltaX: 0, deltaY: -100, deltaMode: 0, wheelDeltaY: -120 }), {
        kind: "discrete",
        gesture: "WheelUp",
        steps: 1,
    });
    assert.deepEqual(wheel.feed({ deltaX: 0, deltaY: -200, deltaMode: 0, wheelDeltaY: -240 }), {
        kind: "discrete",
        gesture: "WheelUp",
        steps: 2,
    });
    assert.deepEqual(wheel.feed({ deltaX: 0, deltaY: 100, deltaMode: 0, wheelDeltaY: 120 }), {
        kind: "discrete",
        gesture: "WheelDown",
        steps: 1,
    });
    assert.deepEqual(wheel.feed({ deltaX: 0, deltaY: 50, deltaMode: 0, wheelDeltaY: 120 }), {
        kind: "precise",
        dx: 0,
        dy: 50,
    });
});

test("INPUT-04：触控板精确滚动只平移，不产生命令", () => {
    const wheel = new WheelInterpreter(manualClock());
    const result = wheel.feed({ deltaX: 3.5, deltaY: 12, deltaMode: 0 });
    assert.deepEqual(result, { kind: "precise", dx: 3.5, dy: 12 });
    assert.equal(result.gesture, undefined);
    assert.equal(new CommandMap(HOTKEYS).commandFor(result.gesture), null);
});

test("INPUT-04：ctrl+精确滚动在物理 Ctrl 未按下时解释为捏合", () => {
    const wheel = new WheelInterpreter(manualClock());
    const pinch = wheel.feed({ deltaX: 0, deltaY: -10, deltaMode: 0, ctrlKey: true });
    assert.equal(pinch.kind, "precise");
    assert.equal(pinch.gesture, undefined);
    assert.ok(Math.abs(pinch.pinch - Math.exp(10 * WHEEL_PINCH_SCALE)) < 1e-12);
    assert.ok(pinch.pinch > 1);

    wheel.noteModifierKeys({ ctrl: true });
    const pan = wheel.feed({ deltaX: 0, deltaY: -10, deltaMode: 0, ctrlKey: true });
    assert.equal(pan.kind, "precise");
    assert.equal(pan.pinch, undefined);

    wheel.noteModifierKeys({ ctrl: false });
    const out = wheel.feed({ deltaX: 0, deltaY: 10, deltaMode: 0, ctrlKey: true });
    assert.ok(Math.abs(out.pinch - Math.exp(-10 * WHEEL_PINCH_SCALE)) < 1e-12);
    assert.ok(out.pinch < 1);
});

test("INPUT-04/INPUT-09：离散滚轮带 Ctrl 走比例缩放绑定", () => {
    const wheel = new WheelInterpreter(manualClock());
    const result = wheel.feed({ deltaX: 0, deltaY: -100, deltaMode: 0, wheelDeltaY: -120, ctrlKey: true });
    assert.deepEqual(result, { kind: "discrete", gesture: "Ctrl+WheelUp", steps: 1 });
    const map = new CommandMap(HOTKEYS);
    assert.equal(map.commandFor(result.gesture), "ZoomInScale");
    const down = wheel.feed({ deltaX: 0, deltaY: 100, deltaMode: 0, wheelDeltaY: 120, ctrlKey: true });
    assert.equal(map.commandFor(down.gesture), "ZoomOutScale");
});

test("INPUT-04：空闲超过累积阈值后重新按格计数，方向反转丢弃旧格", () => {
    const clock = manualClock();
    const wheel = new WheelInterpreter(clock);
    assert.equal(wheel.feed({ deltaY: -1, deltaMode: 1 }), null);
    clock.advance(300);
    assert.equal(wheel.feed({ deltaY: -1, deltaMode: 1 }), null);
    assert.equal(wheel.feed({ deltaY: -1, deltaMode: 1 }), null);
    assert.deepEqual(wheel.feed({ deltaY: -1, deltaMode: 1 }), {
        kind: "discrete",
        gesture: "WheelUp",
        steps: 1,
    });

    const reversed = new WheelInterpreter(manualClock());
    assert.equal(reversed.feed({ deltaY: -1, deltaMode: 1 }), null);
    assert.equal(reversed.feed({ deltaY: 1, deltaMode: 1 }), null);
    assert.equal(reversed.feed({ deltaY: 1, deltaMode: 1 }), null);
    assert.deepEqual(reversed.feed({ deltaY: 1, deltaMode: 1 }), {
        kind: "discrete",
        gesture: "WheelDown",
        steps: 1,
    });
});

test("INPUT-04：零位移滚轮不产生事件", () => {
    const wheel = new WheelInterpreter(manualClock());
    assert.equal(wheel.feed({ deltaX: 0, deltaY: 0, deltaMode: 0 }), null);
    assert.equal(wheel.feed(null), null);
});

test("INPUT-06：向左滑=下一张，向右滑=上一张", () => {
    const swipe = new SwipeRecognizer(createTokens());
    swipe.start(1, 100, 100);
    swipe.move(1, 60, 100);
    assert.equal(swipe.end(1), "next");

    swipe.start(1, 100, 100);
    swipe.move(1, 130, 100);
    assert.equal(swipe.end(1), "previous");
});

test("INPUT-06：水平位移需超过阈值且明显大于纵向（≥2 倍）", () => {
    const swipe = new SwipeRecognizer(createTokens());
    swipe.start(1, 0, 0);
    swipe.move(1, -24, 0);
    assert.equal(swipe.end(1), null);

    swipe.start(1, 0, 0);
    swipe.move(1, -40, 25);
    assert.equal(swipe.end(1), null);

    swipe.start(1, 0, 0);
    swipe.move(1, -40, 20);
    assert.equal(swipe.end(1), "next");

    swipe.start(1, 0, 0);
    swipe.move(1, -5, -40);
    assert.equal(swipe.end(1), null);
});

test("INPUT-06：第二根手指加入即作废，抬起后可开始新手势", () => {
    const swipe = new SwipeRecognizer(createTokens());
    swipe.start(1, 0, 0);
    swipe.start(2, 40, 0);
    swipe.move(1, -60, 0);
    assert.equal(swipe.end(1), null);
    assert.equal(swipe.end(2), null);

    swipe.start(3, 0, 0);
    swipe.move(3, -60, 0);
    assert.equal(swipe.end(3), "next");
});

test("INPUT-06：mid-gesture 端点以最后一次 move 为准，错误 id 不结束手势", () => {
    const swipe = new SwipeRecognizer(createTokens());
    swipe.start(7, 0, 0);
    swipe.move(7, -10, 0);
    assert.equal(swipe.end(8), null);
    swipe.move(7, 60, 5);
    assert.equal(swipe.end(7), "previous");
});

test("INPUT-07/MENU-03：长按到时长触发一次，end 报告是否触发", () => {
    const clock = manualClock();
    const press = new LongPressRecognizer(clock);
    let fired = 0;
    press.start(10, 10, () => { fired++; });
    clock.advance(499);
    assert.equal(fired, 0);
    assert.equal(press.end(), false);
    assert.equal(fired, 0);

    press.start(10, 10, () => { fired++; });
    clock.advance(500);
    assert.equal(fired, 1);
    assert.equal(press.end(), true);
    assert.equal(fired, 1);
});

test("INPUT-07：超出 slop 取消长按，slop 内移动不取消", () => {
    const clock = manualClock();
    const press = new LongPressRecognizer(clock);
    let fired = 0;
    press.start(0, 0, () => { fired++; });
    press.move(9, 0);
    clock.advance(1000);
    assert.equal(fired, 0);
    assert.equal(press.end(), false);

    press.start(0, 0, () => { fired++; });
    press.move(8, 0);
    clock.advance(500);
    assert.equal(fired, 1);
    assert.equal(press.end(), true);

    press.start(0, 0, () => { fired++; });
    press.cancel();
    clock.advance(1000);
    assert.equal(press.end(), false);
    assert.equal(fired, 1);
});

test("SETTINGS-11：手势文本按平台命名，鼠标手势用语义名", () => {
    assert.equal(gestureText("ArrowLeft"), "←");
    assert.equal(gestureText("ArrowRight"), "→");
    assert.equal(gestureText("Ctrl+Digit0"), "Ctrl+0");
    assert.equal(gestureText("Numpad0"), "小键盘 0");
    assert.equal(gestureText("Backquote"), "`");
    assert.equal(gestureText("Minus"), "−");
    assert.equal(gestureText("WheelUp"), "滚轮上");
    assert.equal(gestureText("WheelDown"), "滚轮下");
    assert.equal(gestureText("MiddleClick"), "中键");
    assert.equal(gestureText("Shift+ArrowLeft"), "Shift+←");
    assert.equal(gestureText("Ctrl+Shift+Equal"), "Ctrl++");
    assert.equal(gestureText(null), "");
});

test("SETTINGS-11：Apple 手势文本用 ⌘⌥⌃⇧，其余用 Ctrl/Alt/Win", () => {
    assert.equal(gestureText("Ctrl+Digit0", { apple: true }), "⌘0");
    assert.equal(gestureText("Ctrl+WheelUp", { apple: true }), "⌘滚轮上");
    assert.equal(gestureText("Ctrl+Shift+Equal", { apple: true }), "⌘+");
    assert.equal(gestureText("Meta+KeyE", { apple: true }), "⌃E");
    assert.equal(gestureText("Meta+KeyE"), "Win+E");
    assert.equal(gestureText("Alt+Shift+KeyZ", { apple: true }), "⌥⇧Z");
});

test("TOOLBAR-04：提示包含命令名与实际主/次绑定，空槽不显示", () => {
    const map = new CommandMap(HOTKEYS);
    assert.equal(tooltipText("Previous", "上一张", map), "上一张（← / 滚轮上）");
    assert.equal(tooltipText("ZoomInPreset", "放大（预设）", map), "放大（预设）（Ctrl++）");
    assert.equal(tooltipText("ToggleAnalysisView", "分析栏展开/收纳", map), "分析栏展开/收纳");
    assert.equal(tooltipText("Fit", "缩放适应", map, { apple: true }), "缩放适应（⌘0 / F）");
    assert.equal(tooltipText("NoSuchCommand", "未知", map), "未知");
});
