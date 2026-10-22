/**
 * 胶片栏卡片内部节点（FILM-05）。
 * cardFrames(t) → { root, surface, image, name, date, shake, stars }；
 * root 相对文档，surface 相对 root，image/name/date/shake/star{k} 相对内层占位面（surface）。
 * 坐标与行高直接取令牌，不再由字号行高比例推导。
 */

export function cardFrames(t) {
    const cardWidth = t("film.card.width");
    const cardHeight = t("film.card.height");
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
        root: { x: 0, y: 0, w: cardWidth, h: cardHeight },
        surface: { x: inset, y: inset, w: surfaceWidth, h: surfaceHeight },
        image,
        name,
        date,
        shake,
        stars,
    };
}

export function cardNodes(frames, index, frame) {
    const nodes = {};
    const prefix = `card${index}`;
    nodes[`${prefix}.root`] = [frame.x, frame.y, frame.w, frame.h];
    const surface = frames.surface;
    nodes[`${prefix}.surface`] = [surface.x, surface.y, surface.w, surface.h];
    const inner = (rect) => [rect.x, rect.y, rect.w, rect.h];
    nodes[`${prefix}.image`] = inner(frames.image);
    nodes[`${prefix}.name`] = inner(frames.name);
    nodes[`${prefix}.date`] = inner(frames.date);
    nodes[`${prefix}.shake`] = inner(frames.shake);
    frames.stars.forEach((rect, star) => {
        nodes[`${prefix}.star${star}`] = inner(rect);
    });
    return nodes;
}
