/**
 * 星级来源浮窗几何（RATING-04）。
 * 宽度由六格星排、条目内边距与浮窗内边距决定；条目高度按信息行单行/双行分别推导。
 * 展开方向：卡片默认向下，下方不足且上方可容纳时向上；横排控制栏向上，竖排控制栏向左。
 * 坐标与尺寸均为逻辑点；anchor/host 使用宿主坐标系。
 */

export const RATING_SOURCE_COUNT = 6;
// RATING-04：信息行是排版布局值（文件名单行高），字号取 popup.ratingSources.nameFontSize。
export const RATING_SOURCE_INFO_HEIGHT = 16;

export function ratingSourcesMetrics(t) {
    const starSize = t("toolbar.rating.button.size");
    const entryPadding = t("popup.ratingSources.entryPadding");
    const padding = t("popup.padding");
    const labelGap = t("popup.ratingSources.labelGap");
    const metaLineGap = t("popup.ratingSources.metaLineGap");
    const entryWidth = starSize * RATING_SOURCE_COUNT + entryPadding * 2;
    const infoHeight = RATING_SOURCE_INFO_HEIGHT;
    // RATING-04：条目四边内边距均取 entryPadding，浮窗内边距另取 popup.padding。
    const shortEntryHeight = entryPadding * 2 + infoHeight + labelGap + starSize;
    return {
        starCount: RATING_SOURCE_COUNT,
        starSize,
        entryPadding,
        padding,
        infoHeight,
        labelGap,
        metaLineGap,
        entrySpacing: t("popup.ratingSources.entrySpacing"),
        dividerHeight: t("popup.ratingSources.dividerHeight"),
        arrowMargin: t("popup.ratingSources.arrowMargin"),
        entryWidth,
        contentWidth: entryWidth + padding * 2,
        shortEntryHeight,
        longEntryHeight: shortEntryHeight + infoHeight + metaLineGap,
    };
}

/** 浮窗内容高度（不含描边）：上下内边距 + 各条目 + 条目间距。 */
export function ratingSourcesHeight(t, longFlags = []) {
    const metrics = ratingSourcesMetrics(t);
    const entries = longFlags.reduce(
        (total, long) => total + (long ? metrics.longEntryHeight : metrics.shortEntryHeight),
        0,
    );
    return metrics.padding * 2 + entries + Math.max(0, longFlags.length - 1) * metrics.entrySpacing;
}

export function resolveRatingSourcesDirection({ mode, axis = "horizontal", anchor, host, height, t }) {
    const gap = t("popup.ratingSources.arrowMargin");
    // RATING-04：底部横排控制栏向上，右侧竖排控制栏向左。
    if (mode === "control") return { direction: axis === "vertical" ? "left" : "up", gap };
    const needed = height + gap;
    const below = host.height - (anchor.y + anchor.h);
    const above = anchor.y;
    return { direction: below < needed && above >= needed ? "up" : "down", gap };
}

function clamp(value, min, max) {
    if (max < min) return min;
    return Math.min(Math.max(value, min), max);
}

export function placeRatingSources({ anchor, host, width, height, direction, gap }) {
    let x;
    let y;
    if (direction === "up") {
        x = anchor.x + anchor.w / 2 - width / 2;
        y = anchor.y - gap - height;
    } else if (direction === "left") {
        x = anchor.x - gap - width;
        y = anchor.y + anchor.h / 2 - height / 2;
    } else {
        x = anchor.x + anchor.w / 2 - width / 2;
        y = anchor.y + anchor.h + gap;
    }
    return {
        x: clamp(x, 0, host.width - width),
        y: clamp(y, 0, host.height - height),
    };
}
