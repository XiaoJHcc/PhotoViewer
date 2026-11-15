/**
 * 筛选入口几何（FILTER-07@web、FILTER-04）：iPad 宽形态与手机横屏为「筛选 / 更多」96/88 合体，
 * 手机竖屏为筛选圆按钮 + 更多圆按钮，AI 版相似聚类开关独立。计数只占筛选入口内部，不改组宽。
 * 所有矩形为窗口坐标；筛选层是全窗图层。
 */

export function filterGeometry(spec, t) {
    const {
        form,
        orientation,
        mode,
        width,
        safe,
        profile = "web",
        segmentWidth = t("filter.control.width"),
        showSimilarity = true,
        showFilmbar = true,
        fileContentWidth = t("shell.filebar.content.width"),
        topRowY = 0,
        topRowX = 0,
        topRowRight = width,
        landscapeX = 0,
        landscapeTop = 0,
    } = spec;
    const button = t("shell.glass.button.size");
    const margin = t("filter.margin");
    const gap = t("shell.glass.group.gap");
    const edge = t("shell.phone.edgeMargin");
    const countWidth = spec.countWidth ?? 0;

    if (profile === "mac" && form === "wide") {
        return macFilterGeometry({
            mode, width, safe, showSimilarity, showFilmbar, fileContentWidth, countWidth, segmentWidth,
            segmentWidths: spec.segmentWidths ?? null,
            t,
        });
    }

    if (form === "phone" && orientation === "portrait") {
        const entry = { x: topRowX, y: topRowY, w: button, h: button };
        const more = { x: topRowRight - button, y: topRowY, w: button, h: button };
        const count = { x: entry.x, y: entry.y, w: entry.w, h: entry.h };
        return {
            form,
            orientation,
            axis: "row",
            kind: "portrait",
            entry,
            more,
            composite: null,
            toggle: null,
            count,
            countWidth,
            segments: [
                { kind: "filter", frame: entry },
                { kind: "more", frame: more },
            ],
            bar: { x: 0, y: 0, w: width, h: topRowY + button },
        };
    }

    if (form === "phone" && orientation === "landscape") {
        const total = button * 2;
        const x = landscapeX;
        const y = landscapeTop;
        const entry = { x, y, w: button, h: button };
        const more = { x: x + button, y, w: button, h: button };
        const composite = { x, y, w: total, h: button };
        const count = { x, y, w: total, h: button };
        return {
            form,
            orientation,
            axis: "col",
            kind: "composite",
            entry,
            more,
            composite,
            toggle: null,
            count,
            countWidth,
            segments: [
                { kind: "filter", frame: entry },
                { kind: "more", frame: more },
            ],
            bar: { x, y, w: total, h: button },
        };
    }

    const compositeWidth = t("film.card.width");
    const entry = { x: 0, y: 0, w: compositeWidth / 2, h: button };
    const more = { x: compositeWidth / 2, y: 0, w: compositeWidth / 2, h: button };
    const composite = { x: 0, y: 0, w: compositeWidth, h: button };
    const count = { x: entry.x, y: entry.y, w: compositeWidth, h: button };
    const segments = [
        { kind: "filter", frame: entry },
        { kind: "more", frame: more },
    ];

    if (mode === "col") {
        const x = fileContentWidth - margin - compositeWidth;
        const top = safe.top + margin;
        composite.x = x;
        composite.y = top;
        entry.x = x;
        entry.y = top;
        more.x = x + compositeWidth / 2;
        more.y = top;
        count.x = x;
        count.y = top;
        count.w = compositeWidth;
        count.h = button;
        const toggle = showSimilarity && showFilmbar
            ? { x, y: top + button + gap, w: compositeWidth, h: button }
            : null;
        const height = margin + button + (toggle ? gap + button : 0) + margin;
        return {
            form,
            orientation,
            axis: "col",
            kind: "composite",
            entry,
            more,
            composite,
            toggle,
            count,
            countWidth,
            segments,
            bar: { x: 0, y: 0, w: fileContentWidth, h: height },
        };
    }

    const groupWidth = compositeWidth + (showSimilarity ? gap + compositeWidth : 0);
    const contentLeft = safe.left;
    const contentWidth = Math.max(0, width - safe.left - safe.right);
    const start = contentLeft + Math.max(margin, (contentWidth - groupWidth) / 2);
    const top = safe.top;
    composite.x = start;
    composite.y = top + margin;
    entry.x = start;
    entry.y = top + margin;
    more.x = start + compositeWidth / 2;
    more.y = top + margin;
    count.x = start;
    count.y = top + margin;
    count.w = compositeWidth;
    count.h = button;
    const toggle = showSimilarity
        ? { x: start + compositeWidth + gap, y: top + margin, w: compositeWidth, h: button }
        : null;
    const barHeight = button + margin * 2;
    return {
        form,
        orientation,
        axis: "row",
        kind: "composite",
        entry,
        more,
        composite,
        toggle,
        count,
        countWidth,
        segments,
        bar: { x: 0, y: top, w: width, h: barHeight },
    };
}

function macFilterGeometry({ mode, width, safe, showSimilarity, showFilmbar, fileContentWidth, countWidth, segmentWidth, segmentWidths = null, t }) {
    const button = t("shell.glass.button.size");
    const gap = t("shell.glass.group.gap");
    const margin = t("filter.margin");
    const capsuleWidth = t("film.card.width");
    const rowToggleWidth = t("filter.toggle.width");
    const withToggle = showSimilarity && showFilmbar;
    const leadingInset = t("shell.filter.leadingInset");
    const widths = Array.isArray(segmentWidths) && segmentWidths.length === 3
        ? segmentWidths
        : [segmentWidth, segmentWidth, segmentWidth];
    const natural = widths.reduce((sum, value) => sum + value, 0);

    if (mode === "col") {
        const x = (fileContentWidth - capsuleWidth) / 2;
        const composite = { x, y: leadingInset, w: capsuleWidth, h: button };
        const fits = natural <= capsuleWidth;
        const controls = {};
        if (fits) {
            const extra = (capsuleWidth - natural) / 3;
            let cursor = x;
            for (const [index, name] of ["sort", "order", "rating"].entries()) {
                const width = widths[index] + extra;
                controls[name] = { x: cursor, y: leadingInset, w: width, h: button };
                cursor += width;
            }
        }
        const toggle = withToggle
            ? { x, y: leadingInset + button + gap, w: capsuleWidth, h: button }
            : null;
        const barHeight = leadingInset + button + (withToggle ? gap + button : 0) + margin;
        return {
            form: "wide",
            orientation: null,
            axis: "col",
            kind: fits ? "segments" : "capsule",
            leadingInset,
            composite,
            control: composite,
            controls,
            entry: fits ? null : composite,
            more: null,
            toggle,
            count: { x: composite.x, y: composite.y, w: composite.w, h: composite.h },
            countWidth,
            segments: [],
            bar: { x: 0, y: 0, w: fileContentWidth, h: barHeight },
        };
    }

    const rowHeight = button + margin * 2;
    const toggleRun = withToggle ? gap + rowToggleWidth : 0;
    const fits = natural + toggleRun <= width - margin * 2;
    const controlWidth = fits ? natural : capsuleWidth;
    const start = Math.max(margin, (width - controlWidth - toggleRun) / 2);
    const y = safe.top + margin;
    const control = { x: start, y, w: controlWidth, h: button };
    const controls = {};
    if (fits) {
        let cursor = start;
        for (const [index, name] of ["sort", "order", "rating"].entries()) {
            controls[name] = { x: cursor, y, w: widths[index], h: button };
            cursor += widths[index];
        }
    }
    const toggle = withToggle
        ? { x: control.x + controlWidth + gap, y, w: rowToggleWidth, h: button }
        : null;
    return {
        form: "wide",
        orientation: null,
        axis: "row",
        kind: fits ? "segments" : "capsule",
        composite: null,
        control,
        controls,
        entry: fits ? null : control,
        more: null,
        toggle,
        count: { x: control.x, y: control.y, w: control.w, h: control.h },
        countWidth,
        segments: [],
        bar: { x: 0, y: safe.top, w: width, h: rowHeight },
    };
}

export function filterBarHeight(t) {
    return t("shell.glass.button.size") + t("filter.margin") * 2;
}
