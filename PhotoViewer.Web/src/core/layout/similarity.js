/**
 * 相似聚类面板让位几何（SIM-07）。
 * columnSimilarity(t) → 列布局面板宽与占位（面板宽 + sim.panel.margin）。
 * rowSimilarityWidth({ availableWidth, itemCount, dockTexts, measureText }, t) → 行布局从胶片栏右端切出的宽度；
 * 内容宽 = 卡片数 × 96 + sim.list.padding + 2 × 描边 + 停靠区宽（列表态才有），
 * 停靠区宽 = max(sim.action.width, 文本自然宽) + 2 × sim.dock.margin；
 * 面板宽 = clamp(内容宽, sim.panel.minWidth, 可用宽 × sim.panel.maxWidthRatio)，占位再加 sim.panel.margin。
 * rowSimilarityHeight(t) → 行布局面板交叉轴高 = sim.list.cross。
 */
import { approximateTextWidth } from "./text.js";


export function columnSimilarity(t) {
    const width = Math.max(t("sim.panel.width"), t("sim.panel.minWidth"));
    const margin = t("sim.panel.margin");
    return {
        axis: "vertical",
        width,
        occupation: width + margin,
        minWidth: t("sim.panel.minWidth"),
        maxWidth: null,
        dockWidth: 0,
    };
}

export function dockWidthFor(itemCount, dockTexts, t, measureText = approximateTextWidth) {
    if (!(itemCount > 0)) return 0;
    let textWidth = 0;
    for (const text of dockTexts ?? []) {
        if (text === null || text === undefined || text === "") continue;
        textWidth = Math.max(textWidth, measureText(String(text), t("sim.dock.fontSize")));
    }
    return Math.max(t("sim.action.width"), textWidth) + 2 * t("sim.dock.margin");
}

export function rowSimilarityWidth({ availableWidth, itemCount, dockTexts = [], measureText = approximateTextWidth }, t) {
    const dockWidth = dockWidthFor(itemCount, dockTexts, t, measureText);
    const raw = itemCount * t("film.card.width") + t("sim.list.padding")
        + 2 * t("sim.panel.border.width") + dockWidth;
    const minWidth = t("sim.panel.minWidth");
    const maxWidth = Math.max(0, availableWidth * t("sim.panel.maxWidthRatio"));
    const width = maxWidth < minWidth ? maxWidth : Math.min(Math.max(raw, minWidth), maxWidth);
    return {
        axis: "horizontal",
        width,
        occupation: width + t("sim.panel.margin"),
        minWidth,
        maxWidth,
        dockWidth,
    };
}

export function rowSimilarityHeight(t) {
    return t("sim.list.cross");
}

export function similarityGeometry({ expanded, axis, availableWidth, itemCount, dockTexts = [] }, t, measureText = approximateTextWidth) {
    const base = axis === "vertical"
        ? columnSimilarity(t)
        : { ...rowSimilarityWidth({ availableWidth, itemCount, dockTexts, measureText }, t), height: rowSimilarityHeight(t) };
    const height = axis === "vertical" ? null : base.height;
    return {
        expanded: Boolean(expanded),
        axis: base.axis,
        width: base.width,
        occupation: expanded ? base.occupation : 0,
        minWidth: base.minWidth,
        maxWidth: base.maxWidth ?? null,
        dockWidth: base.dockWidth,
        height,
        itemCount,
    };
}
