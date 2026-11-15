/**
 * 胶片栏落位（FILM-02/05/07）：大档 / 小档，横向 / 纵向。
 * filmGeometry(spec, t) → { axis, tier, scroll, clip, document, contentLength, items,
 *   cardMain, cardCross, step, lead, trail, slack, band }。
 * scroll 为窗口坐标（胶片层是全窗图层），document 与 items 相对滚动区。
 * 主轴两端内边距、交叉轴内边距与卡片档位由布局按形态给出，本模块不读平台私有补偿。
 */

export function filmMetrics(t, tier = "large") {
    const size = filmTierSizes(t, tier);
    const small = tier === "small";
    return {
        cardWidth: size.width,
        cardHeight: size.height,
        stepX: size.width + size.spacing,
        stepY: size.height + size.spacing,
        padLeft: small ? t("film.list.small.padMain") : t("film.list.padLeft"),
        padTop: small ? t("film.list.small.padCross") : t("film.list.padTop"),
        padRight: small ? t("film.list.small.padMain") : t("film.list.padRight"),
        padBottom: small ? t("film.list.small.padCross") : t("film.list.padBottom"),
    };
}

export function filmTierSizes(t, tier = "large") {
    if (tier === "small") {
        return {
            width: t("film.card.small.width"),
            height: t("film.card.small.height"),
            spacing: t("film.card.spacing"),
        };
    }
    return {
        width: t("film.card.width"),
        height: t("film.card.height"),
        spacing: t("film.card.spacing"),
    };
}

export function filmContentLength(t, count, axis, tier = "large", lead = null, trail = null) {
    const size = filmTierSizes(t, tier);
    const card = axis === "y" ? size.height : size.width;
    const leadPad = lead ?? (axis === "y" ? t("film.list.padTop") : t("film.list.padLeft"));
    const trailPad = trail ?? (axis === "y" ? t("film.list.padBottom") : t("film.list.padRight"));
    return leadPad + Math.max(0, count) * (card + size.spacing) + trailPad;
}

export function filmItemFrame(t, index, count, axis, viewportLength, tier = "large", lead = null, trail = null, crossLead = null) {
    const size = filmTierSizes(t, tier);
    const card = axis === "y" ? size.height : size.width;
    const step = card + size.spacing;
    const leadPad = lead ?? (axis === "y" ? t("film.list.padTop") : t("film.list.padLeft"));
    const trailPad = trail ?? (axis === "y" ? t("film.list.padBottom") : t("film.list.padRight"));
    const length = leadPad + Math.max(0, count) * step + trailPad;
    const slack = axis === "x" ? Math.max(0, viewportLength - length) : 0;
    const start = leadPad + slack / 2 + index * step;
    const crossPad = axis === "y"
        ? crossLead ?? t("film.list.padLeft")
        : crossLead ?? t("film.list.padTop");
    return axis === "y"
        ? { x: crossPad, y: start, w: size.width, h: size.height }
        : { x: start, y: crossPad, w: size.width, h: size.height };
}

export function filmGeometry(spec, t) {
    const {
        axis,
        tier = "large",
        scroll,
        count = 0,
        lead = 0,
        trail = 0,
        crossLead = null,
        crossTrail = null,
    } = spec;
    const size = filmTierSizes(t, tier);
    const cardMain = axis === "y" ? size.height : size.width;
    const cardCross = axis === "y" ? size.width : size.height;
    const step = cardMain + size.spacing;
    const viewportMain = axis === "y" ? scroll.h : scroll.w;
    const contentLength = lead + Math.max(0, count) * step + trail;
    const slack = axis === "x" ? Math.max(0, viewportMain - contentLength) : 0;
    const documentLength = Math.max(viewportMain, contentLength);
    const mainPad = axis === "y" ? crossLead ?? t("film.list.padLeft") : crossLead ?? t("film.list.padTop");
    const mainPadTrail = axis === "y" ? crossTrail ?? t("film.list.padRight") : crossTrail ?? t("film.list.padBottom");
    const document = axis === "y"
        ? { x: 0, y: 0, w: scroll.w, h: documentLength }
        : { x: 0, y: 0, w: documentLength, h: scroll.h };
    const items = [];
    for (let index = 0; index < count; index += 1) {
        items.push(filmItemFrame(t, index, count, axis, viewportMain, tier, lead, trail, mainPad));
    }
    return {
        axis,
        tier,
        scroll,
        clip: { x: 0, y: 0, w: scroll.w, h: scroll.h },
        document,
        contentLength,
        items,
        cardMain,
        cardCross,
        step,
        lead,
        trail,
        slack,
        band: axis === "y" ? scroll.w : scroll.h,
        crossLead: mainPad,
        crossTrail: mainPadTrail,
    };
}

export function filmCenterOffset(film, index, viewportLength, t) {
    if (!film || !(film.items.length > 0)) return 0;
    return centerOffset(
        Math.max(0, Math.min(film.items.length - 1, index)),
        film.items.length,
        film.axis,
        viewportLength,
        t,
        film.tier,
        film.lead,
        film.trail,
    );
}


export function contentLength(count, axis, t, tier = "large", lead = null, trail = null) {
    if (axis !== "x" && axis !== "y") throw new Error(`未知胶片栏方向：${axis}`);
    return filmContentLength(t, count, axis, tier, lead, trail);
}

export function itemStart(index, count, axis, viewportLength, t, tier = "large", lead = null, trail = null) {
    if (axis !== "x" && axis !== "y") throw new Error(`未知胶片栏方向：${axis}`);
    const frame = filmItemFrame(t, index, count, axis, viewportLength, tier, lead, trail);
    return axis === "y" ? frame.y : frame.x;
}

export function visibleRange(offset, viewportLength, count, axis, t, tier = "large", lead = null, trail = null) {
    if (count <= 0) return [0, -1];
    const m = filmTierSizes(t, tier);
    const step = (axis === "y" ? m.height : m.width) + m.spacing;
    const start = itemStart(0, count, axis, viewportLength, t, tier, lead, trail);
    if (!(step > 0) || !(viewportLength > 0)) return [0, -1];
    const first = Math.max(0, Math.floor((offset - start) / step));
    const last = Math.min(count - 1, Math.ceil((offset + viewportLength - start) / step) - 1);
    return [first, last];
}

export function centerOffset(index, count, axis, viewportLength, t, tier = "large", lead = null, trail = null) {
    const m = filmTierSizes(t, tier);
    const card = axis === "y" ? m.height : m.width;
    const start = itemStart(index, count, axis, viewportLength, t, tier, lead, trail);
    const length = contentLength(count, axis, t, tier, lead, trail);
    const max = Math.max(0, length - viewportLength);
    return Math.min(max, Math.max(0, start + (card - viewportLength) / 2));
}
