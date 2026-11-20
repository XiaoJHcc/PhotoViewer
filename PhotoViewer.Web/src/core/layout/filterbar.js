/**
 * 筛选入口几何（FILTER-07@web、FILTER-04）：宽形态为一个玻璃胶囊内的三段合体（排序 / 方向 / 星级各一段），
 * 三段放不下时回退同一胶囊内的单按钮；手机竖屏为筛选圆按钮 + 更多圆按钮，横屏为「筛选 / 更多」88 合体。
 * AI 版相似聚类开关独立。筛选控件不显示计数，也不为计数预留空间。所有矩形为窗口坐标；筛选层是全窗图层。
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
        segmentWidths = null,
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

    if (form === "phone" && orientation === "portrait") {
        const entry = { x: topRowX, y: topRowY, w: button, h: button };
        const more = { x: topRowRight - button, y: topRowY, w: button, h: button };
        return {
            form,
            orientation,
            axis: "row",
            kind: "portrait",
            entry,
            more,
            composite: null,
            toggle: null,
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
        return {
            form,
            orientation,
            axis: "col",
            kind: "composite",
            entry,
            more,
            composite,
            toggle: null,
            segments: [
                { kind: "filter", frame: entry },
                { kind: "more", frame: more },
            ],
            bar: { x, y, w: total, h: button },
        };
    }

    const capsuleWidth = t("film.card.width");
    const rowToggleWidth = t("filter.toggle.width");
    const withToggle = showSimilarity && showFilmbar;
    const widths = Array.isArray(segmentWidths) && segmentWidths.length === 3
        ? segmentWidths
        : [segmentWidth, segmentWidth, segmentWidth];
    const natural = widths.reduce((sum, value) => sum + value, 0);
    // FILTER-07：首控件顶部让位只有 macOS 取标题栏让位；浏览器从窗口/安全区顶边加一份外边距起排。
    const inset = profile === "mac" ? t("shell.filter.leadingInset") : safe.top + margin;

    if (mode === "col") {
        const x = (fileContentWidth - capsuleWidth) / 2;
        const composite = { x, y: inset, w: capsuleWidth, h: button };
        const fits = natural <= capsuleWidth;
        const controls = {};
        if (fits) {
            const extra = (capsuleWidth - natural) / 3;
            let cursor = x;
            for (const [index, name] of ["sort", "order", "rating"].entries()) {
                const segment = widths[index] + extra;
                controls[name] = { x: cursor, y: inset, w: segment, h: button };
                cursor += segment;
            }
        }
        const toggle = withToggle
            ? { x, y: inset + button + gap, w: capsuleWidth, h: button }
            : null;
        const barHeight = inset + button + (withToggle ? gap + button : 0) + margin;
        return {
            form: "wide",
            orientation: null,
            axis: "col",
            kind: fits ? "segments" : "capsule",
            leadingInset: inset,
            composite,
            control: composite,
            controls,
            entry: fits ? null : composite,
            more: null,
            toggle,
            segments: [],
            bar: { x: 0, y: 0, w: fileContentWidth, h: barHeight },
        };
    }

    const rowHeight = button + margin * 2;
    const toggleRun = withToggle ? gap + rowToggleWidth : 0;
    const contentLeft = safe.left;
    const contentWidth = Math.max(0, width - safe.left - safe.right);
    const fits = natural + toggleRun <= contentWidth - margin * 2;
    const controlWidth = fits ? natural : capsuleWidth;
    const start = contentLeft + Math.max(margin, (contentWidth - controlWidth - toggleRun) / 2);
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
        ? { x: start + controlWidth + gap, y, w: rowToggleWidth, h: button }
        : null;
    return {
        form: "wide",
        orientation: null,
        axis: "row",
        kind: fits ? "segments" : "capsule",
        leadingInset: y,
        composite: control,
        control,
        controls,
        entry: fits ? null : control,
        more: null,
        toggle,
        segments: [],
        bar: { x: 0, y: safe.top, w: width, h: rowHeight },
    };
}

export function filterBarHeight(t) {
    return t("shell.glass.button.size") + t("filter.margin") * 2;
}
