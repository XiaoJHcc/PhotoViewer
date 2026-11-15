/**
 * 分析栏几何（ANALYSIS-03/05、SHELL-12）。
 * analysisTileSize({ mainSize, tileCount, padding, spacing }, t) → 等边长；
 * analysisGeometry({ axis, pane, tileCount, padding, spacing, depthStart, depthEnd }, t) → { tileSize, cross, tiles }。
 * 宽形态独立分析栏两侧各 analysis.panel.padding；与工具区相邻时朝主图一侧取 toolbar.padding + 接缝。
 * 手机竖屏取 shell.phone.rowInset，横屏与宽形态同轴。
 */

export function analysisTileSize(spec = {}, t) {
    const padding = spec.padding ?? t("analysis.panel.padding");
    const spacing = spec.spacing ?? t("analysis.tile.spacing");
    const count = Math.max(1, spec.tileCount ?? 1);
    return Math.max(1, Math.floor((spec.mainSize - padding * 2 - spacing * (count - 1)) / count));
}

export function analysisCrossSize(spec, t) {
    const padding = spec.padding ?? t("analysis.panel.padding");
    return analysisTileSize(spec, t) + padding * 2;
}

export function analysisGeometry(spec, t) {
    const {
        axis,
        pane,
        tileCount,
        padding = t("analysis.panel.padding"),
        spacing = t("analysis.tile.spacing"),
        depthStart = padding,
        depthEnd = padding,
    } = spec;
    const mainSize = axis === "vertical" ? pane.h : pane.w;
    const tileSize = analysisTileSize({ mainSize, tileCount, padding, spacing }, t);
    const tiles = [];
    for (let index = 0; index < tileCount; index += 1) {
        const offset = depthStart + index * (tileSize + spacing);
        tiles.push(axis === "vertical"
            ? { index, x: depthStart, y: offset, w: tileSize, h: tileSize }
            : { index, x: offset, y: depthStart, w: tileSize, h: tileSize });
    }
    return {
        axis,
        tileCount,
        tileSize,
        cross: tileSize + depthStart + depthEnd,
        padding,
        spacing,
        tiles,
    };
}
