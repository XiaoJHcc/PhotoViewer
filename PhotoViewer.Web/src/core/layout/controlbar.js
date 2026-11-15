/**
 * 控制栏几何（TOOLBAR-01/02/03/05/08、RATING-07、SHELL-08/09/12）。
 * 宽形态：EXIF → 评分 → 命令，44 点按钮，整组换行/换列、居中、溢出滚动。
 * 手机形态：固定槽位，不换行/不换列/不滚动；展开态评分 224、紧凑态 88、厚 44。
 * 矩形相对控制栏宿主；EXIF 块内项相对块。
 */

export const RATING_COUNT = 6;

function glass(t) {
    return t("shell.glass.button.size");
}

export function ratingMainLength(t) {
    return RATING_COUNT * t("rating.control.slot") + t("rating.control.endInset") * 2;
}

export function exifItemWidth(t) {
    return t("toolbar.exif.item.width");
}

export function exifItemHeight(t) {
    return t("toolbar.exif.item.height");
}

export function compactRatingLength(t) {
    return t("rating.compact.minLength");
}

export function exifMainLength(kind, count, axis, t, profile = "web") {
    if (kind === "exif") {
        if (axis === "vertical" && profile !== "mac") {
            const itemHeight = exifItemHeight(t);
            return count > 0 ? count * itemHeight + (count - 1) * t("toolbar.exif.item.spacing") : 0;
        }
        const padding = t("toolbar.exif.padding");
        const itemMain = axis === "vertical" ? exifItemHeight(t) : exifItemWidth(t);
        return count > 0
            ? padding * 2 + count * itemMain + (count - 1) * t("toolbar.exif.item.spacing")
            : 0;
    }
    if (kind === "rating") return ratingMainLength(t);
    return count > 0 ? count * glass(t) + (count - 1) * t("toolbar.command.spacing") : 0;
}

export function groupMainLength(kind, count, axis, t) {
    return exifMainLength(kind, count, axis, t);
}

function exifChildFrames(kind, count, axis, t, blockCross, profile = "web") {
    const frames = [];
    const itemWidth = exifItemWidth(t);
    const itemHeight = exifItemHeight(t);
    const spacing = t("toolbar.exif.item.spacing");
    if (axis === "vertical") {
        const padding = profile === "mac" ? t("toolbar.exif.padding") : 0;
        for (let index = 0; index < count; index += 1) {
            frames.push({
                kind,
                index,
                x: padding,
                y: padding + index * (itemHeight + spacing),
                w: profile === "mac" ? itemWidth : blockCross,
                h: itemHeight,
            });
        }
        return frames;
    }
    const padding = t("toolbar.exif.padding");
    for (let index = 0; index < count; index += 1) {
        frames.push({
            kind,
            index,
            x: padding + index * (itemWidth + spacing),
            y: 0,
            w: itemWidth,
            h: blockCross,
        });
    }
    return frames;
}

function ratingChildFrames(axis, t, { compact = false, length = ratingMainLength(t) } = {}) {
    const cross = glass(t);
    const frames = [];
    if (compact) {
        const step = length / RATING_COUNT;
        for (let star = 0; star < RATING_COUNT; star += 1) {
            const order = axis === "vertical" ? RATING_COUNT - 1 - star : star;
            frames.push({
                kind: "rating",
                index: star,
                x: axis === "vertical" ? 0 : order * step,
                y: axis === "vertical" ? order * step : 0,
                w: axis === "vertical" ? cross : step,
                h: axis === "vertical" ? step : cross,
            });
        }
        return frames;
    }
    const slot = t("rating.control.slot");
    const inset = t("rating.control.endInset");
    for (let star = 0; star < RATING_COUNT; star += 1) {
        const order = axis === "vertical" ? RATING_COUNT - 1 - star : star;
        frames.push({
            kind: "rating",
            index: star,
            x: axis === "vertical" ? 0 : inset + order * slot,
            y: axis === "vertical" ? inset + order * slot : 0,
            w: axis === "vertical" ? cross : slot,
            h: axis === "vertical" ? slot : cross,
        });
    }
    return frames;
}

function commandChildFrames(count, axis, t) {
    const size = glass(t);
    const spacing = t("toolbar.command.spacing");
    const frames = [];
    for (let index = 0; index < count; index += 1) {
        const offset = index * (size + spacing);
        frames.push({
            kind: "command",
            index,
            x: axis === "vertical" ? 0 : offset,
            y: axis === "vertical" ? offset : 0,
            w: size,
            h: size,
        });
    }
    return frames;
}

export function computeControlBar(spec, t) {
    const {
        axis,
        mainSize,
        exifCount = 0,
        showRating = true,
        commandCount = 0,
        crossNear = t("toolbar.padding"),
        crossFar = t("toolbar.padding"),
        profile = "web",
    } = spec;
    const padding = t("toolbar.padding");
    const spacing = t("toolbar.group.spacing");
    const available = mainSize - padding * 2;
    const ratingCompact = available < ratingMainLength(t);
    const specs = [];
    if (exifCount > 0) specs.push({ kind: "exif", count: exifCount });
    if (showRating) {
        specs.push({
            kind: "rating",
            count: RATING_COUNT,
            compact: ratingCompact,
            length: ratingCompact ? compactRatingLength(t) : ratingMainLength(t),
        });
    }
    if (commandCount > 0) specs.push({ kind: "command", count: commandCount });

    const lines = [];
    let current = null;
    for (const item of specs) {
        const length = item.length ?? exifMainLength(item.kind, item.count, axis, t, profile);
        const cross = item.kind === "exif" && axis === "vertical" && profile === "mac"
            ? exifItemWidth(t) + t("toolbar.exif.padding") * 2
            : glass(t);
        if (current && current.length + spacing + length > available) {
            lines.push(current);
            current = null;
        }
        if (!current) current = { length: 0, cross: 0, mainStart: 0, crossStart: 0, groups: [] };
        current.length += (current.groups.length ? spacing : 0) + length;
        current.groups.push({ ...item, length, cross });
        current.cross = Math.max(current.cross, cross);
    }
    if (current) lines.push(current);

    const groups = [];
    let crossCursor = crossNear;
    let mainExtent = padding;
    for (const line of lines) {
        line.mainStart = Math.max(padding, (mainSize - line.length) / 2);
        line.crossStart = crossCursor;
        let cursor = line.mainStart;
        for (const item of line.groups) {
            const cross = item.cross ?? glass(t);
            const childCross = axis === "vertical" ? cross : cross;
            const children = item.kind === "exif"
                ? exifChildFrames(item.kind, item.count, axis, t, childCross, profile)
                : item.kind === "rating"
                    ? ratingChildFrames(axis, t, { compact: item.compact, length: item.length })
                    : commandChildFrames(item.count, axis, t);
            const crossOffset = (line.cross - cross) / 2;
            for (const child of children) {
                if (axis === "vertical") child.x += crossOffset;
                else child.y += crossOffset;
            }
            groups.push({
                kind: item.kind,
                index: groups.length,
                length: item.length,
                cross,
                compact: Boolean(item.compact),
                x: axis === "vertical" ? crossCursor : cursor,
                y: axis === "vertical" ? cursor : crossCursor,
                w: axis === "vertical" ? line.cross : item.length,
                h: axis === "vertical" ? item.length : line.cross,
                children,
            });
            cursor += item.length + spacing;
        }
        mainExtent = Math.max(mainExtent, cursor - spacing);
        crossCursor += line.cross + spacing;
    }

    const empty = groups.length === 0;
    const documentCross = empty ? crossNear + crossFar : crossCursor - spacing + crossFar;
    const documentMain = empty ? padding * 2 : Math.max(mainSize, mainExtent + padding);
    const document = axis === "vertical"
        ? { x: 0, y: 0, w: documentCross, h: documentMain }
        : { x: 0, y: 0, w: documentMain, h: documentCross };
    return {
        mode: "wide",
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

function verticalStack(entries, mainSize, t) {
    const spacing = t("toolbar.group.spacing");
    const total = entries.reduce((sum, entry) => sum + entry.h, 0) + spacing * Math.max(0, entries.length - 1);
    let cursor = Math.max(0, (mainSize - total) / 2);
    for (const entry of entries) {
        entry.y = cursor;
        cursor += entry.h + spacing;
    }
    return total;
}

export function computePhoneControl(spec, t) {
    const {
        orientation,
        width,
        height,
        controlLeft = t("shell.phone.edgeMargin"),
        controlRight = t("shell.phone.edgeMargin"),
        exifCount = 0,
        showRating = true,
        showNavigation = true,
    } = spec;
    const button = glass(t);
    const gap = t("shell.glass.group.gap");
    const compactMin = compactRatingLength(t);
    const expanded = ratingMainLength(t);
    const pairLength = button * 2;

    if (orientation === "portrait") {
        const slots = {};
        const showNav = Boolean(showNavigation);
        if (showNav) {
            slots.previous = { x: controlLeft, y: 0, w: button, h: button };
            slots.next = { x: controlLeft + button, y: 0, w: button, h: button };
        }
        const ratingStart = showNav ? controlLeft + pairLength + gap : controlLeft;
        const rightStart = width - controlRight - pairLength;
        const slotWidth = Math.max(0, rightStart - gap - ratingStart);
        if (showRating) {
            const compact = slotWidth < expanded;
            const visualWidth = compact ? slotWidth : expanded;
            slots.rating = {
                x: ratingStart,
                y: 0,
                w: slotWidth,
                h: button,
                compact,
                visual: {
                    x: ratingStart + (slotWidth - visualWidth) / 2,
                    y: 0,
                    w: visualWidth,
                    h: button,
                },
                children: ratingChildFrames("horizontal", t, { compact, length: visualWidth }),
            };
        }
        slots.fit = { x: rightStart, y: 0, w: button, h: button };
        slots.enhance = { x: rightStart + button, y: 0, w: button, h: button };
        return {
            mode: "phone",
            orientation,
            axis: "horizontal",
            slots,
            document: { x: 0, y: 0, w: width, h: button },
            overflow: false,
        };
    }

    const slots = {};
    const inner = Math.max(0, height - t("toolbar.padding") * 2);
    const groupSpacing = t("toolbar.group.spacing");
    const itemHeight = exifItemHeight(t);
    const itemSpacing = t("toolbar.exif.item.spacing");
    const itemStep = itemHeight + itemSpacing;
    // TOOLBAR-02：仅显示评分时为评分保留紧凑最短长度；关闭评分时这份保留与对应组距一起释放。
    const reservedRating = showRating ? compactMin : 0;
    const baseGroups = 1 + (showRating ? 1 : 0);
    const exifBudget = inner - pairLength - reservedRating - groupSpacing * baseGroups;
    let exifVisible = 0;
    if (exifCount > 0 && exifBudget >= itemHeight) {
        exifVisible = Math.min(exifCount, Math.floor((exifBudget + itemSpacing) / itemStep));
    }
    const exifLength = exifVisible > 0 ? exifVisible * itemHeight + (exifVisible - 1) * itemSpacing : 0;
    const groupCount = (exifVisible > 0 ? 1 : 0) + (showRating ? 1 : 0) + 1;
    const spacingTotal = groupSpacing * Math.max(0, groupCount - 1);
    const ratingAvailable = inner - exifLength - pairLength - spacingTotal;
    const ratingHeight = showRating ? Math.min(expanded, Math.max(0, ratingAvailable)) : 0;
    const entries = [];
    if (exifVisible > 0) {
        entries.push({
            kind: "exif",
            h: exifLength,
            items: Array.from({ length: exifVisible }, (_, index) => ({
                index,
                x: 0,
                y: index * itemStep,
                w: button,
                h: itemHeight,
            })),
        });
    }
    if (showRating) entries.push({ kind: "rating", h: ratingHeight, compact: ratingHeight < expanded });
    entries.push({ kind: "pair", h: pairLength });
    verticalStack(entries, height, t);
    for (const entry of entries) {
        if (entry.kind === "exif") {
            slots.exif = { x: 0, y: entry.y, w: button, h: entry.h, items: entry.items };
        } else if (entry.kind === "rating") {
            slots.rating = {
                x: 0,
                y: entry.y,
                w: button,
                h: entry.h,
                compact: entry.compact,
                visual: { x: 0, y: entry.y, w: button, h: entry.h },
                children: ratingChildFrames("vertical", t, { compact: entry.compact, length: entry.h }),
            };
        } else {
            slots.fit = { x: 0, y: entry.y, w: button, h: button };
            slots.enhance = { x: 0, y: entry.y + button, w: button, h: button };
        }
    }
    return {
        mode: "phone",
        orientation,
        axis: "vertical",
        slots,
        document: { x: 0, y: 0, w: button, h: height },
        overflow: false,
    };
}
