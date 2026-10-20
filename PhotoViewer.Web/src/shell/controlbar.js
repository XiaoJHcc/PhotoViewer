import { el, place, setText, touches } from "./dom.js";

const REGIONS = ["layout", "toolbar", "current", "settings", "panes", "window"];
const RATING_COUNT = 6;
const GROUP_ORDER = ["exif", "rating", "command"];
const SVG_NS = "http://www.w3.org/2000/svg";
const STAR_PATH = "M12 2.6 L14.85 8.38 L21.2 9.3 L16.6 13.78 L17.69 20.1 L12 17.1 L6.31 20.1 L7.4 13.78 L2.8 9.3 L9.15 8.38 Z";
const CROSS_PATH = "M6.5 6.5 L13.5 13.5 M13.5 6.5 L6.5 13.5";

const COMMAND_ICONS = {
    Open: "folder",
    Previous: "previous",
    Next: "next",
    Exchange: "exchange",
    Fit: "fit",
    ZoomInPreset: "zoomIn",
    ZoomOutPreset: "zoomOut",
    ZoomInScale: "zoomScaleIn",
    ZoomOutScale: "zoomScaleOut",
    ToggleAnalysisView: "sidebar",
    ToggleEnhance: "wand",
};

function svgNode(children, viewBox = "0 0 20 20") {
    const svg = document.createElementNS(SVG_NS, "svg");
    svg.setAttribute("viewBox", viewBox);
    svg.setAttribute("aria-hidden", "true");
    svg.setAttribute("focusable", "false");
    for (const child of children) svg.append(child);
    return svg;
}

function pathNode(d) {
    const node = document.createElementNS(SVG_NS, "path");
    node.setAttribute("d", d);
    return node;
}

function circleNode(cx, cy, r) {
    const node = document.createElementNS(SVG_NS, "circle");
    node.setAttribute("cx", String(cx));
    node.setAttribute("cy", String(cy));
    node.setAttribute("r", String(r));
    return node;
}

function magnifier(inner) {
    return svgNode([
        circleNode(8.8, 8.8, 4.6),
        pathNode("M12.3 12.3 L16.4 16.4"),
        ...inner.map((d) => pathNode(d)),
    ]);
}

const ICON_BUILDERS = {
    previous: () => svgNode([pathNode("M12.5 4.5 L7 10 L12.5 15.5")]),
    next: () => svgNode([pathNode("M7.5 4.5 L13 10 L7.5 15.5")]),
    exchange: () => svgNode([
        pathNode("M15.5 6.5 H5.5"),
        pathNode("M8.5 3.5 L5.5 6.5 L8.5 9.5"),
        pathNode("M4.5 13.5 H14.5"),
        pathNode("M11.5 10.5 L14.5 13.5 L11.5 16.5"),
    ]),
    fit: () => svgNode([
        pathNode("M4 8 V4 H8"),
        pathNode("M4.4 4.4 L9.4 9.4"),
        pathNode("M16 12 V16 H12"),
        pathNode("M15.6 15.6 L10.6 10.6"),
    ]),
    zoomIn: () => magnifier(["M8.8 6.4 V11", "M6.5 8.8 H11.1"]),
    zoomOut: () => magnifier(["M6.5 8.8 H11.1"]),
    zoomScaleIn: () => magnifier(["M6.6 11 L11 6.6", "M8.4 6.6 H11 V9.2"]),
    zoomScaleOut: () => magnifier(["M6.6 9 L11 13.4", "M8.4 13.4 H11 V10.8"]),
    sidebar: () => svgNode([
        pathNode("M5 5 H15 A1.5 1.5 0 0 1 16.5 6.5 V13.5 A1.5 1.5 0 0 1 15 15 H5 A1.5 1.5 0 0 1 3.5 13.5 V6.5 A1.5 1.5 0 0 1 5 5 Z"),
        pathNode("M8.4 5 V15"),
    ]),
    wand: () => svgNode([
        pathNode("M3.8 16.2 L12.2 7.8"),
        pathNode("M14.4 3.8 V7.6"),
        pathNode("M12.5 5.7 H16.3"),
        pathNode("M16 10 V12.6"),
        pathNode("M14.7 11.3 H17.3"),
    ]),
    folder: () => svgNode([
        pathNode("M3.5 6.2 C3.5 5.6 3.9 5.2 4.5 5.2 H7.8 L9.4 6.9 H15.5 C16.1 6.9 16.5 7.3 16.5 7.9 V14.4 C16.5 15 16.1 15.4 15.5 15.4 H4.5 C3.9 15.4 3.5 15 3.5 14.4 Z"),
    ]),
};

function ratingValueOf(command) {
    const match = /^SetRating([0-5])$/.exec(command);
    return match ? Number(match[1]) : null;
}

function commandGlyph(command) {
    const value = ratingValueOf(command);
    if (value !== null) {
        // TOOLBAR-04：评分快捷命令统一画数字，0 画叉。
        if (value === 0) return svgNode([pathNode(CROSS_PATH)]);
        return el("span", { class: "pv-control-command-digit", "aria-hidden": "true" }, String(value));
    }
    const builder = ICON_BUILDERS[COMMAND_ICONS[command]];
    return builder ? builder() : null;
}

function createStarGlyph() {
    return svgNode([pathNode(STAR_PATH)], "0 0 24 24");
}

function createClearGlyph() {
    return svgNode([pathNode(CROSS_PATH)]);
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
        const glyph = value === 0 ? createClearGlyph() : createStarGlyph();
        const button = el("button", {
            type: "button",
            class: value === 0 ? "pv-control-rating-button is-clear" : "pv-control-rating-button is-off",
            "aria-label": value === 0 ? "无星级" : `${value} 星`,
            title: value === 0 ? "无星级" : `${value} 星`,
        }, glyph);
        button.dataset.rating = String(value);
        button.addEventListener("click", () => ctx.app.rate(undefined, value));
        return button;
    }

    function createCommandButton(command) {
        const glyph = commandGlyph(command);
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
            block.title = items[index].tooltip;
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
        for (let value = 1; value < RATING_COUNT; value += 1) {
            const on = rating.value >= value;
            ratingButtons[value].classList.toggle("is-on", on);
            ratingButtons[value].classList.toggle("is-off", !on);
        }
    }

    function updateCommandButton(button, item) {
        button.title = item.tooltip;
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
