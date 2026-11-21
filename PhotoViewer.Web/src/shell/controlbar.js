import { el, place, setText, touches } from "./dom.js";
import { icon, solidIcon } from "./icons.js";
import { tooltipText } from "../core/input.js";
import { controlRatingPopoverDirection } from "../core/layout/ratingpopover.js";

const REGIONS = ["layout", "toolbar", "current", "settings", "panes", "window", "viewport"];
const RATING_COUNT = 6;
const GROUP_ORDER = ["rating", "command"];

const COMMAND_ICONS = {
    Open: "icon.command.open",
    Previous: "icon.command.previous",
    Next: "icon.command.next",
    Exchange: "icon.command.exchange",
    Fit: "icon.command.fit",
    ZoomInPreset: "icon.command.zoomInPreset",
    ZoomOutPreset: "icon.command.zoomOutPreset",
    ZoomInScale: "icon.command.zoomInScale",
    ZoomOutScale: "icon.command.zoomOutScale",
    ToggleAnalysisView: "icon.command.toggleAnalysisView",
    ToggleEnhance: "icon.command.toggleEnhance",
};

function ratingValueOf(command) {
    const match = /^SetRating([0-5])$/.exec(command);
    return match ? Number(match[1]) : null;
}

function commandGlyph(command, t) {
    const value = ratingValueOf(command);
    if (value !== null) {
        if (value === 0) return icon(t, "icon.clear", "pv-command-icon");
        return el("span", { class: "pv-command-rating", "aria-hidden": "true" },
            solidIcon(t, "icon.starFilled", "pv-command-rating-star"),
            el("span", { class: "pv-command-rating-digit" }, String(value)));
    }
    const token = COMMAND_ICONS[command];
    return token ? icon(t, token, "pv-command-icon") : null;
}

function createStarGlyph(t) {
    return [
        icon(t, "icon.star", "pv-rating-glyph pv-rating-outline"),
        solidIcon(t, "icon.starFilled", "pv-rating-glyph pv-rating-solid"),
    ];
}

function createClearGlyph(t) {
    return icon(t, "icon.clear", "pv-rating-glyph pv-rating-clear");
}

function createConflictGlyph(t) {
    return icon(t, "icon.warning", "pv-rating-glyph pv-rating-conflict");
}

function coordinateOf(axis, child) {
    return axis === "vertical" ? child.y : child.x;
}

function visualOrder(group, axis) {
    return group.children.slice().sort((a, b) => coordinateOf(axis, a) - coordinateOf(axis, b));
}

function reorder(container, nodes) {
    const current = Array.from(container.children);
    if (current.length === nodes.length && current.every((node, index) => node === nodes[index])) return;
    for (const node of nodes) container.append(node);
}

export function mountControlBar(host, ctx) {
    const { app, t } = ctx;
    const scroll = el("div", {
        class: "pv-control-scroll is-row",
        role: "group",
        "aria-label": "控制栏",
    });
    const documentLayer = el("div", { class: "pv-control-document" });
    const indicator = el("div", { class: "pv-control-indicator", "aria-hidden": "true" });
    indicator.hidden = true;
    const phoneLayer = el("div", { class: "pv-control-phone" });
    phoneLayer.hidden = true;
    const exifHost = el("div", { class: "pv-control-exif-host" });
    const navCapsule = el("div", {
        class: "pv-control-capsule is-nav",
        role: "group",
        "aria-label": "上一张/下一张",
    });
    const commandCapsule = el("div", {
        class: "pv-control-capsule is-command",
        role: "group",
        "aria-label": "命令",
    });
    navCapsule.hidden = true;
    commandCapsule.hidden = true;
    phoneLayer.append(navCapsule, commandCapsule);
    scroll.append(documentLayer);
    documentLayer.append(exifHost);
    host.append(scroll, indicator, phoneLayer);

    const groups = new Map();
    const ratingButtons = [];
    const commandButtons = new Map();
    let layoutRef = null;
    let metrics = null;
    let drag = null;
    let ratingConflict = false;
    let phoneMode = false;

    function ensureGroup(kind) {
        let node = groups.get(kind);
        if (!node) {
            node = el("div", { class: `pv-control-group pv-control-${kind}` });
            node.setAttribute("role", "group");
            node.setAttribute("aria-label", kind === "rating" ? "评分" : "命令");
            if (kind === "rating") node.classList.add("pv-control-rating");
            groups.set(kind, node);
            documentLayer.append(node);
        }
        return node;
    }

    function createRatingButton(value) {
        const label = value === 0 ? "无星级" : `${value} 星`;
        const glyph = value === 0
            ? [createClearGlyph(t), createConflictGlyph(t)]
            : createStarGlyph(t);
        const button = el("button", {
            type: "button",
            class: value === 0 ? "pv-control-rating-button is-clear" : "pv-control-rating-button is-off",
            "aria-label": label,
            "data-tooltip": label,
        }, glyph);
        button.dataset.rating = String(value);
        button.addEventListener("click", () => {
            if (value === 0 && ratingConflict) {
                ctx.ratingPopover?.open(button, {
                    mode: "control",
                    axis: layoutRef?.axis ?? "horizontal",
                    direction: controlRatingPopoverDirection(app.state.layout),
                });
                return;
            }
            app.rate(undefined, value);
        });
        return button;
    }

    function commandTooltip(command) {
        const row = app.settings.hotkeys.find((entry) => entry.command === command);
        if (!row) return command;
        const bindings = { primary: row.primary ?? null, secondary: row.secondary ?? null };
        return tooltipText(command, row.name, { bindingsOf: () => bindings }, {
            apple: app.settings.appleKeyboard,
        });
    }

    function createCommandButton(command) {
        const glyph = commandGlyph(command, t);
        const button = el("button", { type: "button", class: "pv-control-command-button" });
        if (glyph) button.append(glyph);
        button.dataset.command = command;
        button.dataset.tooltip = commandTooltip(command);
        button.setAttribute("aria-label", button.dataset.tooltip);
        button.addEventListener("click", () => app.runCommand(command));
        return button;
    }

    function ensureCommand(command) {
        let button = commandButtons.get(command);
        if (!button) {
            button = createCommandButton(command);
            commandButtons.set(command, button);
        }
        return button;
    }

    for (let value = 0; value < RATING_COUNT; value += 1) {
        ratingButtons.push(createRatingButton(value));
    }

    function placeRatingChild(button, child) {
        place(button, child);
        // TOOLBAR-10：悬停/按下的内切正圆直径取槽位短边，紧凑槽也不越出。
        button.style.setProperty("--pv-rating-slot", `${Math.min(child.w, child.h)}px`);
    }

    function ratingFrame(group, axis) {
        // TOOLBAR-02：分组在交叉轴被拉到该列最大长度，评分胶囊自身仍是 44 见方并居中。
        const size = t("shell.glass.button.size");
        const first = group.children[0];
        if (axis === "vertical" && group.w > size) {
            return { frame: { x: group.x + (first?.x ?? 0), y: group.y, w: size, h: group.h }, offset: first?.x ?? 0 };
        }
        if (axis === "horizontal" && group.h > size) {
            return { frame: { x: group.x, y: group.y + (first?.y ?? 0), w: group.w, h: size }, offset: first?.y ?? 0 };
        }
        return { frame: group, offset: 0 };
    }

    function updateRating(container, group, rating, axis) {
        container.classList.toggle("is-compact", Boolean(group.compact));
        const order = visualOrder(group, axis);
        const { frame, offset } = ratingFrame(group, axis);
        place(container, frame);
        for (const child of order) {
            const button = ratingButtons[child.index];
            if (!button) continue;
            const local = axis === "vertical"
                ? { ...child, x: child.x - offset }
                : { ...child, y: child.y - offset };
            placeRatingChild(button, local);
        }
        reorder(container, order.map((child) => ratingButtons[child.index]).filter(Boolean));
        for (let value = 0; value < RATING_COUNT; value += 1) {
            ratingButtons[value].disabled = !rating.enabled;
        }
        const conflict = Boolean(rating.conflict);
        if (conflict !== ratingConflict) {
            ratingConflict = conflict;
            const zero = ratingButtons[0];
            zero.classList.toggle("is-conflict", conflict);
            zero.classList.toggle("is-clear", !conflict);
            const label = conflict ? "星级冲突" : "无星级";
            zero.setAttribute("aria-label", label);
            zero.dataset.tooltip = label;
        }
        for (let value = 1; value < RATING_COUNT; value += 1) {
            const on = rating.value >= value;
            ratingButtons[value].classList.toggle("is-on", on);
            ratingButtons[value].classList.toggle("is-off", !on);
        }
    }

    function updateCommandButton(button, item) {
        button.dataset.tooltip = item.tooltip;
        button.setAttribute("aria-label", item.tooltip);
        button.disabled = item.enabled === false;
        if ("active" in item) button.setAttribute("aria-pressed", item.active ? "true" : "false");
        else button.removeAttribute("aria-pressed");
    }

    function updateCommands(container, group, commands, axis) {
        const order = visualOrder(group, axis);
        const used = new Set();
        for (const child of order) {
            const item = commands[child.index];
            if (!item) continue;
            const button = ensureCommand(item.command);
            used.add(button);
            button.hidden = false;
            place(button, child);
            updateCommandButton(button, item);
        }
        for (const button of commandButtons.values()) {
            if (!used.has(button) && !phoneMode) button.hidden = true;
        }
        const nodes = order
            .map((child) => commands[child.index])
            .filter(Boolean)
            .map((item) => ensureCommand(item.command));
        reorder(container, nodes);
    }

    function placeSlotRating(slot, rating, axis) {
        const group = ensureGroup("rating");
        if (phoneMode && group.parentNode !== phoneLayer) phoneLayer.append(group);
        group.hidden = false;
        const visual = slot.visual ?? slot;
        place(group, visual);
        const compact = Boolean(slot.compact);
        group.classList.toggle("is-compact", compact);
        const pseudo = { children: slot.children ?? [] };
        const order = visualOrder(pseudo, axis);
        for (const child of order) {
            const button = ratingButtons[child.index];
            if (button) {
                button.hidden = false;
                placeRatingChild(button, child);
            }
        }
        reorder(group, order.map((child) => ratingButtons[child.index]).filter(Boolean));
        for (let value = 0; value < RATING_COUNT; value += 1) ratingButtons[value].disabled = !rating.enabled;
        const conflict = Boolean(rating.conflict);
        if (conflict !== ratingConflict) {
            ratingConflict = conflict;
            const zero = ratingButtons[0];
            zero.classList.toggle("is-conflict", conflict);
            zero.classList.toggle("is-clear", !conflict);
        }
        for (let value = 1; value < RATING_COUNT; value += 1) {
            const on = rating.value >= value;
            ratingButtons[value].classList.toggle("is-on", on);
            ratingButtons[value].classList.toggle("is-off", !on);
        }
    }

    function capsuleRect(slots) {
        if (!slots.length) return null;
        const x = Math.min(...slots.map((slot) => slot.x));
        const y = Math.min(...slots.map((slot) => slot.y));
        const w = Math.max(...slots.map((slot) => slot.x + slot.w)) - x;
        const h = Math.max(...slots.map((slot) => slot.y + slot.h)) - y;
        return { x, y, w, h };
    }

    // SHELL-08/09：手机命令是同一胶囊里的独立动作按钮，按主轴顺序排列，不是分段选择控件。
    function placeCapsule(capsule, slots, commands, axis, items) {
        const rect = capsuleRect(slots);
        if (!rect) {
            capsule.hidden = true;
            for (const command of commands) ensureCommand(command).hidden = true;
            return;
        }
        capsule.hidden = false;
        place(capsule, rect);
        const ordered = slots.slice().sort((a, b) => coordinateOf(axis, a) - coordinateOf(axis, b));
        const buttons = commands.map((command) => ensureCommand(command));
        for (const slot of ordered) {
            const button = buttons[slot.index];
            if (!button) continue;
            button.hidden = false;
            button.classList.add("is-capsule-segment");
            capsule.append(button);
            place(button, { x: slot.x - rect.x, y: slot.y - rect.y, w: slot.w, h: slot.h });
            updateCommandButton(button, items[slot.index]);
        }
        reorder(capsule, ordered.map((slot) => buttons[slot.index]).filter(Boolean));
    }

    function applyPhone(layout, state) {
        phoneMode = true;
        scroll.hidden = true;
        phoneLayer.hidden = false;
        indicator.hidden = true;
        const ratingNode = groups.get("rating");
        if (ratingNode && ratingNode.parentNode !== phoneLayer) phoneLayer.append(ratingNode);
        if (exifHost.parentNode !== phoneLayer) phoneLayer.append(exifHost);
        const slots = layout.slots ?? {};
        const rating = state.toolbar.rating;
        const configured = new Map(state.toolbar.commands.map((item) => [item.command, item]));
        const commandItem = (command) => configured.get(command) ?? {
            command,
            name: app.settings.hotkeys.find((row) => row.command === command)?.name ?? command,
            enabled: command === "Open" || Boolean(state.photo),
            tooltip: commandTooltip(command),
        };
        const phoneCommands = state.toolbar.phoneCommands ?? [];
        if (slots.rating && rating.visible) placeSlotRating(slots.rating, rating, layout.axis);
        else if (groups.get("rating")) groups.get("rating").hidden = true;
        if (navCapsule.parentNode !== phoneLayer) phoneLayer.append(navCapsule);
        if (commandCapsule.parentNode !== phoneLayer) phoneLayer.append(commandCapsule);

        if (slots.previous && slots.next) {
            placeCapsule(navCapsule, [
                { ...slots.previous, index: 0 },
                { ...slots.next, index: 1 },
            ], ["Previous", "Next"], "horizontal", [commandItem("Previous"), commandItem("Next")]);
        } else {
            placeCapsule(navCapsule, [], ["Previous", "Next"], "horizontal", []);
        }

        const commandSlots = slots.commandSegments ?? [];
        const visible = phoneCommands.slice(0, commandSlots.length);
        placeCapsule(
            commandCapsule,
            commandSlots,
            visible.map((item) => item.command),
            layout.axis,
            visible,
        );

        const used = new Set(visible.map((item) => item.command));
        if (!navCapsule.hidden) {
            used.add("Previous");
            used.add("Next");
        }
        for (const [command, button] of commandButtons) {
            if (used.has(command)) continue;
            button.hidden = true;
            button.classList.remove("is-capsule-segment");
        }
        for (const node of groups.values()) {
            if (node !== groups.get("rating")) node.hidden = true;
        }
        const ratingGroup = groups.get("rating");
        if (ratingGroup) ratingGroup.hidden = !(slots.rating && rating.visible);
    }

    function applyWide(layout, state) {
        phoneMode = false;
        scroll.hidden = false;
        phoneLayer.hidden = true;
        navCapsule.hidden = true;
        commandCapsule.hidden = true;
        for (const button of commandButtons.values()) button.classList.remove("is-capsule-segment");
        if (exifHost.parentNode !== documentLayer) documentLayer.append(exifHost);
        layoutRef = layout;
        const vertical = layout.axis === "vertical";
        scroll.classList.toggle("is-row", !vertical);
        scroll.classList.toggle("is-col", vertical);
        documentLayer.style.width = `${layout.document.w}px`;
        documentLayer.style.height = `${layout.document.h}px`;
        const seen = new Set();
        for (const group of layout.groups) {
            if (group.kind === "exif") continue;
            const node = ensureGroup(group.kind);
            node.hidden = false;
            seen.add(group.kind);
            if (group.kind === "rating") updateRating(node, group, state.toolbar.rating, layout.axis);
            else {
                place(node, group);
                updateCommands(node, group, state.toolbar.commands, layout.axis);
            }
        }
        for (const [kind, node] of groups) {
            if (!seen.has(kind)) node.hidden = true;
            if (node.parentNode !== documentLayer) documentLayer.append(node);
        }
        reorder(documentLayer, GROUP_ORDER.map((kind) => groups.get(kind)).filter(Boolean));
        clampScroll();
        updateIndicator();
    }

    function clampScroll() {
        const width = documentLayer.offsetWidth;
        const height = documentLayer.offsetHeight;
        const maxX = Math.max(0, width - scroll.clientWidth);
        const maxY = Math.max(0, height - scroll.clientHeight);
        if (scroll.scrollLeft > maxX) scroll.scrollLeft = maxX;
        if (scroll.scrollTop > maxY) scroll.scrollTop = maxY;
    }

    function updateIndicator() {
        const layout = layoutRef;
        if (!layout || phoneMode) return;
        const width = scroll.clientWidth;
        const height = scroll.clientHeight;
        const doc = layout.document;
        const maxX = Math.max(0, doc.w - width);
        const maxY = Math.max(0, doc.h - height);
        const pad = 3;
        const thickness = 6;
        if (layout.axis === "horizontal" && maxX > 0) {
            const track = Math.max(0, width - pad * 2);
            const thumb = Math.min(track, Math.max(24, track * width / doc.w));
            const offset = maxX > 0 ? scroll.scrollLeft / maxX : 0;
            metrics = { axis: "horizontal", max: maxX, track, thumb };
            indicator.hidden = false;
            Object.assign(indicator.style, {
                left: `${pad + offset * (track - thumb)}px`,
                top: `${height - pad - thickness}px`,
                width: `${thumb}px`,
                height: `${thickness}px`,
            });
            return;
        }
        if (layout.axis === "vertical" && maxY > 0) {
            const track = Math.max(0, height - pad * 2);
            const thumb = Math.min(track, Math.max(24, track * height / doc.h));
            const offset = maxY > 0 ? scroll.scrollTop / maxY : 0;
            metrics = { axis: "vertical", max: maxY, track, thumb };
            indicator.hidden = false;
            Object.assign(indicator.style, {
                left: `${width - pad - thickness}px`,
                top: `${pad + offset * (track - thumb)}px`,
                width: `${thickness}px`,
                height: `${thumb}px`,
            });
            return;
        }
        metrics = null;
        indicator.hidden = true;
    }

    const onScroll = () => updateIndicator();
    const onWheel = (event) => {
        if (!layoutRef || phoneMode || layoutRef.axis !== "horizontal" || event.ctrlKey) return;
        const max = scroll.scrollWidth - scroll.clientWidth;
        if (max <= 0) return;
        const unit = event.deltaMode === 1 ? 16 : event.deltaMode === 2 ? scroll.clientWidth : 1;
        const delta = (Math.abs(event.deltaX) > Math.abs(event.deltaY) ? event.deltaX : event.deltaY) * unit;
        if (!delta) return;
        const before = scroll.scrollLeft;
        scroll.scrollLeft = Math.min(max, Math.max(0, before + delta));
        if (scroll.scrollLeft !== before) event.preventDefault();
    };
    scroll.addEventListener("scroll", onScroll, { passive: true });
    scroll.addEventListener("wheel", onWheel, { passive: false });

    const onIndicatorDown = (event) => {
        if (!metrics || event.button !== 0) return;
        drag = {
            pointerId: event.pointerId,
            start: metrics.axis === "horizontal" ? event.clientX : event.clientY,
            offset: metrics.axis === "horizontal" ? scroll.scrollLeft : scroll.scrollTop,
        };
        indicator.setPointerCapture(event.pointerId);
        event.preventDefault();
    };
    const onIndicatorMove = (event) => {
        if (!drag || !metrics || event.pointerId !== drag.pointerId) return;
        const range = metrics.track - metrics.thumb;
        if (range <= 0) return;
        const current = metrics.axis === "horizontal" ? event.clientX : event.clientY;
        const value = drag.offset + (current - drag.start) / range * metrics.max;
        const next = Math.min(metrics.max, Math.max(0, value));
        if (metrics.axis === "horizontal") scroll.scrollLeft = next;
        else scroll.scrollTop = next;
    };
    const onIndicatorUp = (event) => {
        if (!drag || event.pointerId !== drag.pointerId) return;
        if (indicator.hasPointerCapture(event.pointerId)) indicator.releasePointerCapture(event.pointerId);
        drag = null;
    };
    indicator.addEventListener("pointerdown", onIndicatorDown);
    indicator.addEventListener("pointermove", onIndicatorMove);
    indicator.addEventListener("pointerup", onIndicatorUp);
    indicator.addEventListener("pointercancel", onIndicatorUp);

    function update(state, regions) {
        if (!touches(regions, ...REGIONS)) return;
        const layout = state.layout && state.layout.control;
        if (!layout) return;
        if (layout.mode === "phone") applyPhone(layout, state);
        else applyWide(layout, state);
    }

    function destroy() {
        scroll.removeEventListener("scroll", onScroll);
        scroll.removeEventListener("wheel", onWheel);
        indicator.removeEventListener("pointerdown", onIndicatorDown);
        indicator.removeEventListener("pointermove", onIndicatorMove);
        indicator.removeEventListener("pointerup", onIndicatorUp);
        indicator.removeEventListener("pointercancel", onIndicatorUp);
        scroll.remove();
        indicator.remove();
    }

    return { update, destroy, exifHost };
}
