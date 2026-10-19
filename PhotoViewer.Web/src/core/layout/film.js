/**
 * 胶片栏在文件栏内的落位（FILM-04/05）；卡片步长、内容长度与短列表居中统一取 core/filmscroll.js。
 * filmGeometry({ axis, contentWidth, paneWidth, paneHeight, filter, count, similarityWidth }, t) →
 * { axis, scroll, clip, document, contentLength, items }；scroll 在文件栏坐标系，clip/document 相对滚动区。
 * axis 取布局方向 "col"（纵向列表）| "row"（横向列表）。
 */
import { contentLength as listLength, filmMetrics, itemStart } from "../filmscroll.js";

const scrollAxis = (axis) => (axis === "col" ? "y" : "x");

export function filmContentLength(t, count, axis) {
    return listLength(count, scrollAxis(axis), t);
}

export function filmItemFrame(t, index, count, axis, viewportLength) {
    const m = filmMetrics(t);
    const start = itemStart(index, count, scrollAxis(axis), viewportLength, t);
    return axis === "col"
        ? { x: m.padLeft, y: start, w: m.cardWidth, h: m.cardHeight }
        : { x: start, y: m.padTop, w: m.cardWidth, h: m.cardHeight };
}

export function filmGeometry({ axis, contentWidth, paneWidth, paneHeight, filter, count, similarityWidth = 0 }, t) {
    const y = filter.bar.h;
    const scroll = axis === "col"
        ? { x: 0, y, w: contentWidth, h: Math.max(0, paneHeight - y) }
        : { x: 0, y, w: Math.max(0, paneWidth - similarityWidth), h: t("film.band.height") };
    const contentLength = filmContentLength(t, count, axis);
    const viewportMain = axis === "col" ? scroll.h : scroll.w;
    const documentLength = Math.max(viewportMain, contentLength);
    const document = axis === "col"
        ? { x: 0, y: 0, w: scroll.w, h: documentLength }
        : { x: 0, y: 0, w: documentLength, h: scroll.h };
    const items = [];
    for (let index = 0; index < count; index++) items.push(filmItemFrame(t, index, count, axis, viewportMain));
    return { axis, scroll, clip: { x: 0, y: 0, w: scroll.w, h: scroll.h }, document, contentLength, items };
}
