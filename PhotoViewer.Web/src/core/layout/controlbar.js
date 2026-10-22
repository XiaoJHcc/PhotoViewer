/**
 * 控制栏几何（TOOLBAR-01/02/03/08/09）。
 * computeControlBar({ axis, mainSize, exifCount, showRating, commandCount }, t) →
 * { axis, mainSize, document:{w,h}, paneCross, groups, lines, overflow, empty }。
 * axis "vertical" 用于列布局（主轴为高、评分 5→0 自上而下），"horizontal" 用于行布局（主轴为宽、0→5 自左向右）。
 * 组矩形与子控件坐标相对控制栏文档；子控件相对所属组。
 */

export const RATING_COUNT = 6;

function childSize(kind, axis, t) {
    if (kind === "exif") {
        const width = t("toolbar.exif.block.width");
        const height = t("toolbar.exif.block.height");
        return axis === "vertical" ? { main: height, cross: width } : { main: width, cross: height };
    }
    if (kind === "rating") {
        const size = t("toolbar.rating.button.size");
        return { main: size, cross: size };
    }
    const size = t("toolbar.command.button.size");
    return { main: size, cross: size };
}

function childMainOffsets(kind, count, axis, t) {
    const offsets = [];
    if (kind === "exif") {
        const inset = t("toolbar.exif.inset");
        const pitch = childSize(kind, axis, t).main + inset * 2 + t("toolbar.exif.spacing");
        for (let i = 0; i < count; i++) offsets.push(inset + i * pitch);
    } else if (kind === "rating") {
        const inset = t("toolbar.rating.group.inset");
        const size = t("toolbar.rating.button.size");
        for (let star = 0; star < RATING_COUNT; star++) {
            const slot = axis === "vertical" ? RATING_COUNT - 1 - star : star;
            offsets.push(inset + slot * size);
        }
    } else {
        const size = t("toolbar.command.button.size");
        const spacing = t("toolbar.command.spacing");
        for (let i = 0; i < count; i++) offsets.push(i * (size + spacing));
    }
    return offsets;
}

export function groupMainLength(kind, count, axis, t) {
    if (kind === "exif") {
        const inset = t("toolbar.exif.inset");
        return count * (childSize(kind, axis, t).main + inset * 2) + (count - 1) * t("toolbar.exif.spacing");
    }
    if (kind === "rating") {
        return RATING_COUNT * t("toolbar.rating.button.size") + t("toolbar.rating.group.inset") * 2;
    }
    return count * t("toolbar.command.button.size") + (count - 1) * t("toolbar.command.spacing");
}

export function groupCrossLength(kind, axis, t) {
    return childSize(kind, axis, t).cross;
}

export function computeControlBar({ axis, mainSize, exifCount = 0, showRating = true, commandCount = 0 }, t) {
    const padding = t("toolbar.padding");
    const spacing = t("toolbar.group.spacing");
    const specs = [];
    if (exifCount > 0) specs.push({ kind: "exif", count: exifCount });
    if (showRating) specs.push({ kind: "rating", count: RATING_COUNT });
    if (commandCount > 0) specs.push({ kind: "command", count: commandCount });

    const available = mainSize - padding * 2;
    const lines = [];
    let current = null;
    for (const spec of specs) {
        const length = groupMainLength(spec.kind, spec.count, axis, t);
        const cross = groupCrossLength(spec.kind, axis, t);
        if (current && current.length + spacing + length > available) {
            lines.push(current);
            current = null;
        }
        if (!current) current = { length: 0, cross: 0, mainStart: 0, crossStart: 0, groups: [] };
        current.length += (current.groups.length ? spacing : 0) + length;
        current.groups.push({ ...spec, length, cross });
        current.cross = Math.max(current.cross, cross);
    }
    if (current) lines.push(current);

    const groups = [];
    let crossCursor = padding;
    let mainExtent = padding;
    for (const line of lines) {
        line.mainStart = Math.max(padding, (mainSize - line.length) / 2);
        line.crossStart = crossCursor;
        let cursor = line.mainStart;
        for (const spec of line.groups) {
            const size = childSize(spec.kind, axis, t);
            const children = childMainOffsets(spec.kind, spec.count, axis, t).map((offset, index) => {
                const cross = (line.cross - size.cross) / 2;
                return axis === "vertical"
                    ? { kind: spec.kind, index, x: cross, y: offset, w: size.cross, h: size.main }
                    : { kind: spec.kind, index, x: offset, y: cross, w: size.main, h: size.cross };
            });
            groups.push({
                kind: spec.kind,
                index: groups.length,
                length: spec.length,
                cross: spec.cross,
                x: axis === "vertical" ? crossCursor : cursor,
                y: axis === "vertical" ? cursor : crossCursor,
                w: axis === "vertical" ? line.cross : spec.length,
                h: axis === "vertical" ? spec.length : line.cross,
                children,
            });
            cursor += spec.length + spacing;
        }
        mainExtent = Math.max(mainExtent, cursor - spacing);
        crossCursor += line.cross + spacing;
    }

    const empty = groups.length === 0;
    const documentCross = empty ? padding * 2 : crossCursor - spacing + padding;
    const documentMain = empty ? padding * 2 : Math.max(mainSize, mainExtent + padding);
    const document = axis === "vertical"
        ? { x: 0, y: 0, w: documentCross, h: documentMain }
        : { x: 0, y: 0, w: documentMain, h: documentCross };

    return {
        axis,
        mainSize,
        document,
        paneCross: documentCross,
        groups,
        lines,
        overflow: documentMain > mainSize,
        empty,
    };
}
