/**
 * 输入映射：浏览器事件描述 → 规范手势 → 命令，含重复闸门、焦点抑制、滚轮精度分流与触屏手势识别。
 *
 * 规范手势字符串（同 core/settings.js 顶部注释）：修饰键按 Ctrl/Alt/Shift/Meta 顺序以 "+"
 * 连接，主键取 KeyboardEvent.code（"ArrowLeft"、"Digit0"、"Equal"、"Numpad0" 等）或鼠标动作
 * （"WheelUp"、"WheelDown"、"MiddleClick"），例："Ctrl+KeyN"、"Ctrl+WheelUp"。
 *
 * 公开 API：gestureFromKey / gestureFromMouse / WheelInterpreter / CommandMap / isRepeatable /
 * KeyDispatcher / gestureText / tooltipText / SwipeRecognizer / LongPressRecognizer。
 */

const MODIFIER_ORDER = ["ctrl", "alt", "shift", "meta"];
const MODIFIER_LABELS = { ctrl: "Ctrl", alt: "Alt", shift: "Shift", meta: "Meta" };

const MODIFIER_CODES = new Set([
    "ControlLeft", "ControlRight",
    "AltLeft", "AltRight",
    "ShiftLeft", "ShiftRight",
    "MetaLeft", "MetaRight",
    "CapsLock", "NumLock", "ScrollLock",
    "Fn", "FnLock", "Hyper", "Super", "Symbol", "OS",
]);

const NON_KEY_CODES = new Set(["Unidentified", "Dead", "Process", "Compose"]);

// SETTINGS-11：平台键位等价，小键盘加减与主键区 +/− 是同一手势。
const KEY_ALIASES = Object.freeze({
    NumpadAdd: "Equal",
    NumpadSubtract: "Minus",
});

const KEY_LABELS = Object.freeze({
    ArrowLeft: "←",
    ArrowRight: "→",
    ArrowUp: "↑",
    ArrowDown: "↓",
    Equal: "+",
    Minus: "−",
    Backquote: "`",
    Space: "空格",
    Escape: "Esc",
    Enter: "回车",
    Tab: "Tab",
    Backspace: "退格",
    Delete: "Delete",
    Insert: "Insert",
    Home: "Home",
    End: "End",
    PageUp: "PageUp",
    PageDown: "PageDown",
    Comma: ",",
    Period: ".",
    Slash: "/",
    Semicolon: ";",
    Quote: "'",
    BracketLeft: "[",
    BracketRight: "]",
    Backslash: "\\",
    NumpadAdd: "小键盘 +",
    NumpadSubtract: "小键盘 −",
    NumpadMultiply: "小键盘 *",
    NumpadDivide: "小键盘 /",
    NumpadDecimal: "小键盘 .",
    NumpadEnter: "小键盘回车",
    WheelUp: "滚轮上",
    WheelDown: "滚轮下",
    MiddleClick: "中键",
});

const KEY_LETTER = /^Key([A-Z])$/;
const KEY_DIGIT = /^Digit([0-9])$/;
const KEY_NUMPAD = /^Numpad([0-9])$/;
const KEY_FUNCTION = /^F([0-9]{1,2})$/;

export const WHEEL_PIXEL_NOTCH = 120;
export const WHEEL_LINE_NOTCH = 3;
export const WHEEL_DISCRETE_MIN_DELTA = 100;
export const WHEEL_PINCH_SCALE = 0.01;
export const WHEEL_ACCUMULATOR_IDLE_MS = 250;

const SWIPE_AXIS_RATIO = 2;
const REPEATABLE_COMMANDS = new Set(["Previous", "Next", "ZoomInPreset", "ZoomOutPreset"]);

function parseGesture(gesture) {
    if (typeof gesture !== "string" || gesture.length === 0) return null;
    const parts = gesture.split("+");
    const mods = { ctrl: false, alt: false, shift: false, meta: false };
    let index = 0;
    while (index < parts.length) {
        const name = MODIFIER_ORDER.find((item) => MODIFIER_LABELS[item] === parts[index]);
        if (!name) break;
        mods[name] = true;
        index++;
    }
    const key = parts.slice(index).join("+");
    if (!key) return null;
    return { mods, key };
}

function composeGesture(mods, key) {
    const parts = [];
    for (const name of MODIFIER_ORDER) if (mods[name]) parts.push(MODIFIER_LABELS[name]);
    parts.push(key);
    return parts.join("+");
}

function normalizeGesture(gesture) {
    const parsed = parseGesture(gesture);
    if (!parsed) return null;
    const key = KEY_ALIASES[parsed.key] ?? parsed.key;
    const mods = { ...parsed.mods };
    // SETTINGS-11：主键区 +/− 需要 Shift，与绑定表里的 Equal/Minus 视为同一手势。
    if (key === "Equal" || key === "Minus") mods.shift = false;
    return composeGesture(mods, key);
}

function modifiersFromEvent(desc, appleKeyboard) {
    let ctrl = Boolean(desc.ctrlKey);
    let meta = Boolean(desc.metaKey);
    // SETTINGS-08：使用苹果键盘时 ⌘ 视同 Ctrl、⌃ 视同 Meta，持久化的规范手势不变。
    if (appleKeyboard) [ctrl, meta] = [meta, ctrl];
    return { ctrl, alt: Boolean(desc.altKey), shift: Boolean(desc.shiftKey), meta };
}

export function gestureFromKey(desc, opts = {}) {
    if (!desc || typeof desc.code !== "string" || desc.code.length === 0) return null;
    if (MODIFIER_CODES.has(desc.code) || NON_KEY_CODES.has(desc.code)) return null;
    const key = KEY_ALIASES[desc.code] ?? desc.code;
    const mods = modifiersFromEvent(desc, Boolean(opts.appleKeyboard));
    if (key === "Equal" || key === "Minus") mods.shift = false;
    return composeGesture(mods, key);
}

export function gestureFromMouse(desc, opts = {}) {
    if (!desc || desc.button !== 1) return null;
    return composeGesture(modifiersFromEvent(desc, Boolean(opts.appleKeyboard)), "MiddleClick");
}

function keyLabel(key) {
    if (Object.hasOwn(KEY_LABELS, key)) return KEY_LABELS[key];
    let match = KEY_LETTER.exec(key);
    if (match) return match[1];
    match = KEY_DIGIT.exec(key);
    if (match) return match[1];
    match = KEY_NUMPAD.exec(key);
    if (match) return `小键盘 ${match[1]}`;
    if (KEY_FUNCTION.test(key)) return key;
    return key;
}

export function gestureText(gesture, opts = {}) {
    const normalized = normalizeGesture(gesture);
    if (!normalized) return "";
    const { mods, key } = parseGesture(normalized);
    const label = keyLabel(key);
    if (opts.apple) {
        let prefix = "";
        if (mods.ctrl) prefix += "⌘";
        if (mods.alt) prefix += "⌥";
        if (mods.meta) prefix += "⌃";
        if (mods.shift) prefix += "⇧";
        return prefix + label;
    }
    const parts = [];
    if (mods.ctrl) parts.push("Ctrl");
    if (mods.alt) parts.push("Alt");
    if (mods.shift) parts.push("Shift");
    if (mods.meta) parts.push("Win");
    parts.push(label);
    return parts.join("+");
}

export function tooltipText(command, name, commandMap, opts = {}) {
    const bindings = commandMap ? commandMap.bindingsOf(command) : null;
    if (!bindings) return name;
    const texts = [];
    for (const slot of ["primary", "secondary"]) {
        const text = gestureText(bindings[slot], opts);
        if (text) texts.push(text);
    }
    return texts.length > 0 ? `${name}（${texts.join(" / ")}）` : name;
}

export class CommandMap {
    constructor(hotkeys = []) {
        this.hotkeys = [];
        this.setHotkeys(hotkeys);
    }

    setHotkeys(hotkeys = []) {
        this.hotkeys = Array.isArray(hotkeys) ? hotkeys.map((row) => ({ ...row })) : [];
    }

    // INPUT-01：按设置列表顺序取第一条命中，冲突不阻止派发。
    commandFor(gesture) {
        const wanted = normalizeGesture(gesture);
        if (!wanted) return null;
        for (const row of this.hotkeys) {
            if (normalizeGesture(row.primary) === wanted) return row.command;
            if (normalizeGesture(row.secondary) === wanted) return row.command;
        }
        return null;
    }

    bindingsOf(command) {
        const row = this.hotkeys.find((item) => item.command === command);
        if (!row) return null;
        return { primary: row.primary ?? null, secondary: row.secondary ?? null };
    }

    conflicts() {
        const slots = new Map();
        for (const row of this.hotkeys) {
            for (const slot of ["primary", "secondary"]) {
                const gesture = normalizeGesture(row[slot]);
                if (!gesture) continue;
                const list = slots.get(gesture) ?? [];
                list.push([row.command, slot]);
                slots.set(gesture, list);
            }
        }
        const result = [];
        for (const list of slots.values()) {
            if (list.length > 1) result.push(...list);
        }
        return result;
    }
}

export function isRepeatable(command) {
    return REPEATABLE_COMMANDS.has(command);
}

export class KeyDispatcher {
    constructor({ commandMap, isSuppressed = () => false } = {}) {
        this.commandMap = commandMap ?? null;
        this.suppressed = isSuppressed;
        this.held = new Set();
    }

    isSuppressed() {
        return typeof this.suppressed === "function" ? Boolean(this.suppressed()) : Boolean(this.suppressed);
    }

    keyDown(desc, opts = {}) {
        // INPUT-02：文本输入、录制与模态期间不派发，且不把按键标记为已处理。
        if (this.isSuppressed()) {
            this.held.clear();
            return { command: null, handled: false };
        }
        const gesture = gestureFromKey(desc, opts);
        if (!gesture) return { command: null, handled: false };
        const command = this.commandMap ? this.commandMap.commandFor(gesture) : null;
        // INPUT-11：未绑定按键原样放行。
        if (!command) {
            this.held.delete(gesture);
            return { command: null, handled: false };
        }
        if (desc.repeat) {
            // INPUT-05：失焦清空后不补发积压重复；不可重复命令按住只执行一次。
            if (!this.held.has(gesture)) return { command: null, handled: true };
            if (!isRepeatable(command)) return { command: null, handled: true };
            return { command, handled: true };
        }
        this.held.add(gesture);
        return { command, handled: true };
    }

    keyUp(desc, opts = {}) {
        const gesture = gestureFromKey(desc, opts);
        if (gesture) this.held.delete(gesture);
    }

    // INPUT-05：窗口失活、焦点切换、弹出菜单时清空按住状态。
    blur() {
        this.held.clear();
    }
}

export class WheelInterpreter {
    constructor(clock, opts = {}) {
        this.clock = clock;
        this.discreteDeltaThreshold = opts.discreteDeltaThreshold ?? WHEEL_DISCRETE_MIN_DELTA;
        this.pinchScale = opts.pinchScale ?? WHEEL_PINCH_SCALE;
        this.ctrlDown = false;
        this.pending = 0;
        this.lastFeedAt = null;
    }

    noteModifierKeys(state = {}) {
        this.ctrlDown = Boolean(state.ctrl);
    }

    notchesFor(desc, mode, dy, wheelDelta) {
        if (mode === 2) {
            if (dy === 0) return null;
            return { direction: dy < 0 ? -1 : 1, notches: 1 };
        }
        if (mode === 1) {
            if (dy === 0) return null;
            return { direction: dy < 0 ? -1 : 1, notches: Math.abs(dy) / WHEEL_LINE_NOTCH };
        }
        if (mode !== 0) return null;
        // INPUT-04@web：浏览器没有精确/离散标志；像素模式只有 wheelDeltaY 恰为 120 的整数倍
        // 且位移足够大时才当作鼠标滚轮（Chromium/Safari），其余按触控板精确滚动处理。
        if (!Number.isFinite(wheelDelta) || wheelDelta === 0) return null;
        if (!Number.isInteger(wheelDelta) || wheelDelta % WHEEL_PIXEL_NOTCH !== 0) return null;
        if (Math.abs(dy) < this.discreteDeltaThreshold) return null;
        const direction = dy < 0 ? -1 : dy > 0 ? 1 : wheelDelta > 0 ? -1 : 1;
        return { direction, notches: Math.abs(wheelDelta) / WHEEL_PIXEL_NOTCH };
    }

    feed(desc, opts = {}) {
        if (!desc) return null;
        const now = this.clock ? this.clock.now() : 0;
        if (this.lastFeedAt !== null && now - this.lastFeedAt > WHEEL_ACCUMULATOR_IDLE_MS) this.pending = 0;
        this.lastFeedAt = now;

        const dx = Number.isFinite(desc.deltaX) ? desc.deltaX : 0;
        const dy = Number.isFinite(desc.deltaY) ? desc.deltaY : 0;
        const mode = desc.deltaMode ?? 0;
        const step = this.notchesFor(desc, mode, dy, desc.wheelDeltaY);
        if (step) {
            if (this.pending !== 0 && Math.sign(this.pending) !== step.direction) this.pending = 0;
            this.pending += step.direction * step.notches;
            const steps = Math.floor(Math.abs(this.pending) + 1e-9);
            if (steps > 0) {
                this.pending -= step.direction * steps;
                if (Math.abs(this.pending) < 1e-9) this.pending = 0;
                const mods = modifiersFromEvent(desc, Boolean(opts.appleKeyboard));
                const gesture = composeGesture(mods, step.direction < 0 ? "WheelUp" : "WheelDown");
                return { kind: "discrete", gesture, steps };
            }
            // INPUT-04：离散事件先按格累积，凑满一格才派发，避免高频事件逐帧翻页。
            return null;
        }

        if (dx === 0 && dy === 0) return null;
        const result = { kind: "precise", dx, dy };
        // INPUT-04：Chromium/Safari 把触控板捏合投递为 ctrl+wheel，物理 Ctrl 未按下时按捏合解释。
        if (desc.ctrlKey && !this.ctrlDown && dy !== 0) result.pinch = Math.exp(-dy * this.pinchScale);
        return result;
    }
}

export class SwipeRecognizer {
    constructor(t) {
        this.threshold = t("image.swipe.threshold");
        this.pointers = new Set();
        this.active = null;
        this.cancelled = false;
    }

    start(id, x, y) {
        this.pointers.add(id);
        // INPUT-06：第二根手指加入即作废，双指起手不翻页。
        if (this.pointers.size > 1) {
            this.active = null;
            this.cancelled = true;
            return;
        }
        this.cancelled = false;
        this.active = { id, startX: x, startY: y, x, y };
    }

    move(id, x, y) {
        if (!this.active || this.active.id !== id) return;
        this.active.x = x;
        this.active.y = y;
    }

    end(id) {
        const gesture = this.active && this.active.id === id ? this.active : null;
        this.pointers.delete(id);
        if (gesture) this.active = null;

        let result = null;
        if (gesture && !this.cancelled) {
            const dx = gesture.x - gesture.startX;
            const dy = gesture.y - gesture.startY;
            const horizontal = Math.abs(dx) > this.threshold;
            const dominant = Math.abs(dx) >= SWIPE_AXIS_RATIO * Math.abs(dy);
            if (horizontal && dominant) result = dx < 0 ? "next" : "previous";
        }
        if (this.pointers.size === 0) this.cancelled = false;
        return result;
    }
}

export class LongPressRecognizer {
    constructor(clock, { delay = 500, slop = 8 } = {}) {
        this.clock = clock;
        this.delay = delay;
        this.slop = slop;
        this.active = null;
    }

    start(x, y, onFire) {
        this.cancel();
        const entry = { x, y, fired: false, timer: null };
        entry.timer = this.clock.setTimeout(() => {
            entry.timer = null;
            entry.fired = true;
            if (onFire) onFire();
        }, this.delay);
        this.active = entry;
    }

    move(x, y) {
        if (!this.active || this.active.fired) return;
        const dx = x - this.active.x;
        const dy = y - this.active.y;
        // INPUT-07：起手后移动超过拖动阈值转为平移，不弹菜单。
        if (Math.hypot(dx, dy) > this.slop) this.cancel();
    }

    end() {
        if (!this.active) return false;
        const fired = this.active.fired;
        this.cancel();
        return fired;
    }

    cancel() {
        if (this.active && this.active.timer !== null) this.clock.clearTimeout(this.active.timer);
        this.active = null;
    }
}
