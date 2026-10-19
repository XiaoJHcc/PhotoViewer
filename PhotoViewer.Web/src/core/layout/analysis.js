/**
 * 分析栏几何（ANALYSIS-03/05）。
 * analysisTileSize({ mainSize, tileCount }, t) → 等边长；analysisCrossSize(...) → 边长 + 两侧内边距。
 * analysisGeometry({ axis, pane, tileCount }, t) → { axis, tileCount, tileSize, cross, padding, spacing, tiles }；
 * axis "vertical" 为列布局（瓦片竖排，主轴取面板高），"horizontal" 为行布局（横排，主轴取面板宽）。
 */

export function analysisTileSize({ mainSize, tileCount }, t) {
    const padding = t("analysis.panel.padding");
    const spacing = t("analysis.tile.spacing");
    return Math.max(1, Math.floor((mainSize - padding * 2 - spacing * (tileCount - 1)) / tileCount));
}

export function analysisCrossSize({ mainSize, tileCount }, t) {
    return analysisTileSize({ mainSize, tileCount }, t) + t("analysis.panel.padding") * 2;
}

export function analysisGeometry({ axis, pane, tileCount }, t) {
    const padding = t("analysis.panel.padding");
    const spacing = t("analysis.tile.spacing");
    const mainSize = axis === "vertical" ? pane.h : pane.w;
    const tileSize = analysisTileSize({ mainSize, tileCount }, t);
    const tiles = [];
    for (let index = 0; index < tileCount; index++) {
        const offset = padding + index * (tileSize + spacing);
        tiles.push(axis === "vertical"
            ? { index, x: padding, y: offset, w: tileSize, h: tileSize }
            : { index, x: offset, y: padding, w: tileSize, h: tileSize });
    }
    return {
        axis,
        tileCount,
        tileSize,
        cross: tileSize + padding * 2,
        padding,
        spacing,
        tiles,
    };
}
