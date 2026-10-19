/**
 * 相似聚类面板让位几何（SIM-07）。
 * columnSimilarity(t) → 列布局固定宽（接在 116 宽文件栏右侧，文件栏整体加宽）。
 * rowSimilarityWidth({ availableWidth, itemCount }, t) → 行布局从胶片栏右侧切出的宽度；
 * 宽度 = clamp(卡片数 × 96 + 列表内边距 8 + 两侧描边 2, minWidth, 可用宽 × 0.5) + 外边距 4，
 * 可用宽一半小于最小宽时以可用宽一半为准。rowSimilarityHeight(t) → 面板交叉轴高。
 */

export function columnSimilarity(t) {
    const width = Math.max(t("sim.panel.width"), t("sim.panel.minWidth"));
    return { axis: "vertical", width, occupation: width, minWidth: t("sim.panel.minWidth"), maxWidth: null };
}

export function rowSimilarityWidth({ availableWidth, itemCount }, t) {
    const raw = itemCount * t("film.card.width") + t("sim.list.padding") + 2 * t("sim.panel.border.width");
    const minWidth = t("sim.panel.minWidth");
    const maxWidth = Math.max(0, availableWidth * t("sim.panel.maxWidthRatio"));
    const width = maxWidth < minWidth ? maxWidth : Math.min(Math.max(raw, minWidth), maxWidth);
    return {
        axis: "horizontal",
        width,
        occupation: width + t("sim.panel.margin"),
        minWidth,
        maxWidth,
    };
}

export function rowSimilarityHeight(t) {
    return Math.max(t("sim.panel.minHeight"), t("sim.list.cross"));
}

export function similarityGeometry({ expanded, axis, availableWidth, itemCount }, t) {
    const base = axis === "vertical"
        ? columnSimilarity(t)
        : { ...rowSimilarityWidth({ availableWidth, itemCount }, t), height: rowSimilarityHeight(t) };
    const height = axis === "vertical" ? null : base.height;
    return {
        expanded: Boolean(expanded),
        axis: base.axis,
        width: base.width,
        occupation: expanded ? base.occupation : 0,
        minWidth: base.minWidth,
        maxWidth: base.maxWidth ?? null,
        height,
        itemCount,
    };
}
