/**
 * 筛选条几何（FILTER-07）。
 * filterGeometry({ paneWidth, axis, topInset, countWidth }, t) → { axis, bar, height, width, groupWidth, controls }。
 * controls 为 sort/order/rating/count/toggle 五个 [x,y,w,h] 矩形，坐标相对文件栏；列布局纵向一行一个，
 * 行布局同排且交叉轴居中。topInset 只用于列布局首控件顶边（mac 交通灯让位）。
 */

export function filterGeometry({ paneWidth, axis, topInset, countWidth }, t) {
    const controlWidth = t("filter.control.width");
    const controlHeight = t("filter.control.height");
    const countHeight = t("filter.count.height");
    const toggleWidth = t("filter.toggle.width");
    const toggleHeight = t("filter.toggle.height");
    const gap = t("filter.gap");
    const margin = t("filter.margin");

    if (axis === "col") {
        const x = (paneWidth - controlWidth) / 2;
        const top = topInset ?? margin;
        const sort = { x, y: top, w: controlWidth, h: controlHeight };
        const order = { x, y: sort.y + controlHeight + gap, w: controlWidth, h: controlHeight };
        const rating = { x, y: order.y + controlHeight + gap, w: controlWidth, h: controlHeight };
        const count = {
            x: (paneWidth - countWidth) / 2,
            y: rating.y + controlHeight + gap,
            w: countWidth,
            h: countHeight,
        };
        const toggle = {
            x: (paneWidth - toggleWidth) / 2,
            y: count.y + countHeight + gap,
            w: toggleWidth,
            h: toggleHeight,
        };
        const height = toggle.y + toggleHeight + margin;
        return {
            axis,
            bar: { x: 0, y: 0, w: paneWidth, h: height },
            height,
            width: paneWidth,
            groupWidth: controlWidth,
            controls: { sort, order, rating, count, toggle },
        };
    }

    const rowHeight = t("filter.bar.row.height");
    const controlY = (rowHeight - controlHeight) / 2;
    const countY = (rowHeight - countHeight) / 2;
    const toggleY = (rowHeight - toggleHeight) / 2;
    const groupWidth = controlWidth * 3 + countWidth + toggleWidth + gap * 4;
    // FILTER-07：行布局整组在文件栏横向居中，窄于外边距允许时退回 margin。
    const startX = Math.max(margin, (paneWidth - groupWidth) / 2);
    const sort = { x: startX, y: controlY, w: controlWidth, h: controlHeight };
    const order = { x: sort.x + controlWidth + gap, y: controlY, w: controlWidth, h: controlHeight };
    const rating = { x: order.x + controlWidth + gap, y: controlY, w: controlWidth, h: controlHeight };
    const count = { x: rating.x + controlWidth + gap, y: countY, w: countWidth, h: countHeight };
    const toggle = { x: count.x + countWidth + gap, y: toggleY, w: toggleWidth, h: toggleHeight };
    return {
        axis,
        bar: { x: 0, y: 0, w: paneWidth, h: rowHeight },
        height: rowHeight,
        width: paneWidth,
        groupWidth,
        controls: { sort, order, rating, count, toggle },
    };
}
