import { el, place, setText, touches } from "./dom.js";
import { icon, solidIcon } from "./icons.js";

const REGIONS = ["layout", "toolbar", "current", "settings", "panes", "window"];
const RATING_COUNT = 6;
const GROUP_ORDER = ["exif", "rating", "command"];

// TOOLBAR-04：命令 → icon.* 令牌；四端与 Web 同一字形。
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

// 实心星（fi-ss-star，uicons solid-straight）：点亮态、评分快捷命令星内叠数字。

function ratingValueOf(command) {
    const match = /^SetRating([0-5])$/.exec(command);
    return match ? Number(match[1]) : null;
}

function commandGlyph(command, t) {
    const value = ratingValueOf(command);
    if (value !== null) {
        if (value === 0) return icon(t, "icon.clear", "pv-command-icon");
        // TOOLBAR-04：1–5 为星形内叠数字，数字取按钮底色。
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
    const { t } = ctx;
    const scroll = el("div", {
        class: "pv-control-scroll is-row",
        role: "group",
        "aria-label": "控制栏",
    });
    const documentLayer = el("div", { class: "pv-control-document" });
    const indicator = el("div", { class: "pv-control-indicator", "aria-hidden": "true" });
    indicator.hidden = true;
    scroll.append(documentLayer);
    host.append(scroll, indicator);

    const groups = new Map();
    const exifBlocks = [];
    const ratingButtons = [];
    const commandButtons = new Map();
    let layoutRef = null;
    let metrics = null;
    let drag = null;
    let ratingConflict = false;

    function ensureGroup(kind) {
        let node = groups.get(kind);
        if (!node) {
            node = el("div", { class: `pv-control-group pv-control-${kind}` });
            if (kind === "exif") {
                node.setAttribute("role", "group");
                node.setAttribute("aria-label", "EXIF 摘要");
            } else if (kind === "rating") {
                node.setAttribute("role", "group");
                node.setAttribute("aria-label", "评分");
            } else {
                node.setAttribute("role", "group");
                node.setAttribute("aria-label", "命令");
            }
            groups.set(kind, node);
            documentLayer.append(node);
        }
        return node;
    }

    function createExifBlock() {
        const label = el("span", { class: "pv-control-exif-label" });
        const value = el("span", { class: "pv-control-exif-value" });
        const block = el("div", { class: "pv-control-exif-block" }, label, value);
        block.labelNode = label;
        block.valueNode = value;
        return block;
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
            // RATING-04：冲突三角只打开来源浮窗；横排控制栏向上、竖排控制栏向左。
            if (value === 0 && ratingConflict) {
                ctx.ratingPopover?.open(button, {
                    mode: "control",
                    axis: layoutRef?.axis ?? "horizontal",
                });
                return;
            }
            ctx.app.rate(undefined, value);
        });
        return button;
    }

    function createCommandButton(command) {
        const glyph = commandGlyph(command, t);
        const button = el("button", { type: "button", class: "pv-control-command-button" });
        if (glyph) button.append(glyph);
        button.dataset.command = command;
        button.addEventListener("click", () => ctx.app.runCommand(command));
        return button;
    }

    for (let value = 0; value < RATING_COUNT; value += 1) {
        ratingButtons.push(createRatingButton(value));
    }

    function updateExif(group, items) {
        const container = groups.get("exif");
        while (exifBlocks.length < items.length) {
            const block = createExifBlock();
            exifBlocks.push(block);
            container.append(block);
        }
        for (let index = 0; index < items.length; index += 1) {
            const block = exifBlocks[index];
            const child = group.children[index];
            place(block, child);
            setText(block.labelNode, items[index].label);
            setText(block.valueNode, items[index].value);
            block.dataset.tooltip = items[index].tooltip;
            block.setAttribute("aria-label", items[index].tooltip);
        }
        for (let index = items.length; index < exifBlocks.length; index += 1) {
            place(exifBlocks[index], null);
        }
    }

    function updateRating(group, rating, axis) {
        const container = groups.get("rating");
        const order = visualOrder(group, axis);
        for (const child of order) {
            const button = ratingButtons[child.index];
            if (button) place(button, child);
        }
        reorder(container, order.map((child) => ratingButtons[child.index]).filter(Boolean));
        for (let value = 0; value < RATING_COUNT; value += 1) {
            ratingButtons[value].disabled = !rating.enabled;
        }
        // RATING-04：组级冲突时 0 号按钮保持 30×30 与命中区，只换成红色警示三角。
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
        // TOOLBAR-04：悬停提示自绘（data-tooltip），读屏名称同文案。
        button.dataset.tooltip = item.tooltip;
        button.setAttribute("aria-label", item.tooltip);
        button.disabled = item.enabled === false;
        if ("active" in item) button.setAttribute("aria-pressed", item.active ? "true" : "false");
        else button.removeAttribute("aria-pressed");
    }

    function updateCommands(group, commands, axis) {
        const container = groups.get("command");
        const order = visualOrder(group, axis);
        const used = new Set();
        for (const child of order) {
            const item = commands[child.index];
            if (!item) continue;
            let button = commandButtons.get(item.command);
            if (!button) {
                button = createCommandButton(item.command);
                commandButtons.set(item.command, button);
                container.append(button);
            }
            used.add(button);
            place(button, child);
            updateCommandButton(button, item);
        }
        for (const button of commandButtons.values()) {
            if (!used.has(button)) place(button, null);
        }
        const nodes = order
            .map((child) => commands[child.index])
            .filter(Boolean)
            .map((item) => commandButtons.get(item.command));
        reorder(container, nodes);
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
        if (!layout) return;
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

    function applyLayout(layout, state) {
        layoutRef = layout;
        const vertical = layout.axis === "vertical";
        scroll.classList.toggle("is-row", !vertical);
        scroll.classList.toggle("is-col", vertical);
        documentLayer.style.width = `${layout.document.w}px`;
        documentLayer.style.height = `${layout.document.h}px`;

        const seen = new Set();
        for (const group of layout.groups) {
            const node = ensureGroup(group.kind);
            place(node, group);
            seen.add(group.kind);
            if (group.kind === "exif") updateExif(group, state.toolbar.exif);
            else if (group.kind === "rating") updateRating(group, state.toolbar.rating, layout.axis);
            else updateCommands(group, state.toolbar.commands, layout.axis);
        }
        for (const [kind, node] of groups) {
            if (!seen.has(kind)) node.hidden = true;
        }
        reorder(documentLayer, GROUP_ORDER.map((kind) => groups.get(kind)).filter(Boolean));
        clampScroll();
        updateIndicator();
    }

    const onScroll = () => updateIndicator();
    // TOOLBAR-08：行布局下把普通滚轮的纵向增量转成横向滚动，内容不被裁切。
    const onWheel = (event) => {
        if (!layoutRef || layoutRef.axis !== "horizontal" || event.ctrlKey) return;
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

    // TOOLBAR-08：悬浮滚动条可拖动；不占布局，也不改变文档几何。
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
        applyLayout(layout, state);
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

    return { update, destroy };
}
