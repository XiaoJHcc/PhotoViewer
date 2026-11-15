/**
 * 胶片栏卡片内部节点（FILM-05）：大档 / 小档两套静态几何。
 * cardFrames(t, tier) → { tier, root, surface, image, name, date, shake, stars, badge? }
 * root 相对文档，surface 相对 root，其余相对内层占位面（surface）。
 * 小档没有第二行与逐星按钮，只有照片区、文件名、星级角标与抖动点。
 */
import { approximateTextWidth } from "./text.js";

export function cardFrames(t, tier = "large", measureText = approximateTextWidth) {
    return tier === "small" ? smallFrames(t, measureText) : largeFrames(t);
}

export function smallBadgeFrame(frames, t, { rating = 0, conflict = false } = {}, measureText = approximateTextWidth) {
    const fontSize = t("film.card.small.badge.fontSize");
    const height = t("film.card.small.badge.height");
    const value = Math.max(0, Math.min(5, Math.round(Number(rating) || 0)));
    const width = (conflict ? fontSize : 0)
        + (value > 0 ? fontSize + measureText(String(value), fontSize, 700) : 0);
    return {
        x: frames.image.x + frames.image.w - width,
        y: frames.image.y + frames.image.h - height,
        w: width,
        h: height,
    };
}

function largeFrames(t) {
    const inset = t("film.card.inset");
    const surfaceWidth = t("film.card.surface.width");
    const surfaceHeight = t("film.card.surface.height");
    const thumb = t("film.thumb.size");
    const thumbInset = t("film.card.thumb.inset");

    const image = { x: thumbInset, y: thumbInset, w: thumb, h: thumb };
    const nameWidth = t("film.card.name.width");
    const name = {
        x: (surfaceWidth - nameWidth) / 2,
        y: t("film.card.name.y"),
        w: nameWidth,
        h: t("film.card.name.height"),
    };

    const secondLineWidth = t("film.card.secondLine.width");
    const date = {
        x: (surfaceWidth - secondLineWidth) / 2,
        y: t("film.card.secondLine.y"),
        w: secondLineWidth,
        h: t("film.card.secondLine.height"),
    };

    const badge = t("film.card.badge.size");
    const badgeInset = t("film.card.badge.inset");
    const shake = {
        x: image.x + thumb - badge - badgeInset,
        y: image.y + thumb - badge - badgeInset,
        w: badge,
        h: badge,
    };

    const starSize = t("film.card.star.size");
    const starStart = (surfaceWidth - starSize * 6) / 2;
    const starY = t("film.card.star.y");
    const stars = [];
    for (let index = 0; index < 6; index++) {
        stars.push({ x: starStart + index * starSize, y: starY, w: starSize, h: starSize });
    }

    return {
        tier: "large",
        root: { x: 0, y: 0, w: t("film.card.width"), h: t("film.card.height") },
        surface: { x: inset, y: inset, w: surfaceWidth, h: surfaceHeight },
        image,
        name,
        date,
        shake,
        stars,
    };
}

function smallFrames(t, measureText = approximateTextWidth) {
    const inset = t("film.card.small.inset");
    const thumbInset = t("film.card.small.thumb.inset");
    const thumb = t("film.card.small.thumb.size");
    const width = t("film.card.small.width");
    const height = t("film.card.small.height");
    const surface = { x: inset, y: inset, w: width - inset * 2, h: height - inset * 2 };
    const image = { x: thumbInset, y: thumbInset, w: thumb, h: thumb };
    const nameHeight = t("film.card.small.name.height");
    const name = {
        x: 0,
        y: t("film.card.small.name.y"),
        w: surface.w,
        h: nameHeight,
    };
    // FILM-05：静态导出按一位数字估算；实际节点由 smallBadgeFrame 按当前测量与冲突态收口。
    const badge = smallBadgeFrame({ image }, t, { rating: 5, conflict: false }, measureText);
    const dot = t("film.card.small.shakeDot.size");
    const dotInset = t("film.card.small.shakeDot.inset");
    const shake = {
        x: image.x + thumb - dot - dotInset,
        y: image.y + dotInset,
        w: dot,
        h: dot,
    };
    return {
        tier: "small",
        root: { x: 0, y: 0, w: width, h: height },
        surface,
        image,
        name,
        date: null,
        badge,
        shake,
        stars: [],
    };
}

export function cardNodes(frames, index, frame) {
    const nodes = {};
    const prefix = `card${index}`;
    nodes[`${prefix}.root`] = [frame.x, frame.y, frame.w, frame.h];
    nodes[`${prefix}.surface`] = [frames.surface.x, frames.surface.y, frames.surface.w, frames.surface.h];
    const inner = (rect) => [rect.x, rect.y, rect.w, rect.h];
    nodes[`${prefix}.image`] = inner(frames.image);
    nodes[`${prefix}.name`] = inner(frames.name);
    if (frames.date) nodes[`${prefix}.date`] = inner(frames.date);
    if (frames.badge) nodes[`${prefix}.badge`] = inner(frames.badge);
    nodes[`${prefix}.shake`] = inner(frames.shake);
    frames.stars.forEach((rect, star) => {
        nodes[`${prefix}.star${star}`] = inner(rect);
    });
    return nodes;
}
