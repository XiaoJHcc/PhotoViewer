/**
 * 主屏几何汇总：computeLayout(scene, { t, profile, measureText }) →
 * { layoutMode, panes, filter, film, control, analysis, similarity, overlays, nodes }。
 * 坐标单位逻辑点；panes 为窗口坐标，filter 相对文件栏，control 相对控制栏文档，film 滚动区相对文件栏、
 * clip/document 相对滚动区，card{i}.root 相对胶片栏文档（卡片内层相对内层占位面）。
 * overlays 以函数给出：navigatorFrame / busyPillFrame / toastFrame / hintFrames。
 */
import { computePanes, resolveLayoutMode } from "./partition.js";
import { filterGeometry } from "./filterbar.js";
import { computeControlBar } from "./controlbar.js";
import { filmGeometry } from "./film.js";
import { cardFrames, cardNodes } from "./card.js";
import { analysisCrossSize, analysisGeometry } from "./analysis.js";
import { similarityGeometry } from "./similarity.js";
import { approximateTextWidth } from "./text.js";

export { approximateTextWidth } from "./text.js";

const HINT_TITLE_LINE_HEIGHT = 20;

export function computeLayout(scene, { t, profile = "web", measureText = approximateTextWidth } = {}) {
    if (typeof t !== "function") throw new Error("computeLayout 需要令牌函数 t");
    const width = scene.width;
    const height = scene.height;
    const layout = resolveLayoutMode(scene.layoutMode ?? "auto", width, height);
    const axis = layout === "col" ? "vertical" : "horizontal";
    const showFilmbar = Boolean(scene.showFilmbar);
    const showControlbar = Boolean(scene.showControlbar);
    const showAnalysis = Boolean(scene.showAnalysis);
    const photoCount = scene.photoCount ?? 0;
    const exifCount = scene.exifCount ?? 0;
    const commandCount = scene.commandCount ?? 0;
    const tileCount = scene.analysisTileCount ?? 6;
    const countText = scene.countText ?? `共 ${photoCount} 张`;
    // FILTER-04：计数字号取 filter.count.fontSize，宽度按平台字体自然宽度测量。
    const countWidth = measureText(countText, t("filter.count.fontSize"), 400);

    const control = computeControlBar({
        axis,
        mainSize: layout === "col" ? height : width,
        exifCount,
        showRating: scene.showRating ?? true,
        commandCount,
    }, t);

    const filter = showFilmbar
        ? filterGeometry({
            paneWidth: layout === "col" ? t("shell.filebar.content.width") : width,
            axis: layout === "col" ? "col" : "row",
            topInset: layout === "col"
                ? (profile === "mac" ? t("shell.filter.leadingInset") : t("filter.margin"))
                : t("filter.margin"),
            countWidth,
        }, t)
        : null;

    const analysisCross = showAnalysis
        ? analysisCrossSize({ mainSize: layout === "col" ? height : width, tileCount }, t)
        : 0;

    const similarity = similarityGeometry({
        expanded: Boolean(scene.similarityExpanded),
        axis,
        availableWidth: width,
        itemCount: scene.similarityItemCount ?? 0,
        dockTexts: scene.similarityDockTexts ?? [],
    }, t, measureText);

    const { panes } = computePanes({
        width,
        height,
        layoutMode: layout,
        showFilmbar,
        showControlbar,
        showAnalysis,
    }, {
        controlCross: control.paneCross,
        analysisCross,
        similarityWidth: similarity.occupation,
    }, t);

    const film = showFilmbar
        ? filmGeometry({
            axis: layout === "col" ? "col" : "row",
            contentWidth: t("shell.filebar.content.width"),
            paneWidth: panes.filePane.w,
            paneHeight: panes.filePane.h,
            filter,
            count: photoCount,
            similarityWidth: similarity.occupation,
        }, t)
        : null;

    const analysis = showAnalysis
        ? analysisGeometry({ axis, pane: panes.analysisPane, tileCount }, t)
        : null;

    const overlays = createOverlays(t, profile, layout, panes);

    const frames = cardFrames(t);
    const cards = [];
    if (film) {
        film.items.forEach((frame, index) => {
            cards.push({ index, frame, nodes: cardNodes(frames, index, frame) });
        });
        film.cards = cards;
    }

    const nodes = collectNodes({ width, height, panes, filter, film, control, showFilmbar, showControlbar });

    return { layoutMode: layout, panes, filter, film, control, analysis, similarity, overlays, nodes };
}

function createOverlays(t, profile, layout, panes) {
    const photoPane = panes.photoPane;

    function navigatorFrame(pane, thumbW, thumbH) {
        const padding = t("nav.padding");
        const scale = t("nav.longEdge") / Math.max(thumbW, thumbH);
        const thumbWidth = thumbW * scale;
        const thumbHeight = thumbH * scale;
        const topGap = t("nav.percent.topGap");
        const labelHeight = t("nav.label.height");
        const hostWidth = thumbWidth + padding * 2;
        const hostHeight = padding * 2 + thumbHeight + topGap + labelHeight + t("nav.percent.bottomGap");
        const x = pane.x + pane.w - t("nav.margin") - hostWidth;
        const y = pane.y + pane.h - t("nav.margin") - hostHeight;
        return {
            x,
            y,
            w: hostWidth,
            h: hostHeight,
            thumb: { x: x + padding, y: y + padding, w: thumbWidth, h: thumbHeight },
            label: {
                x: x + padding,
                y: y + padding + thumbHeight + topGap,
                w: thumbWidth,
                h: labelHeight,
            },
        };
    }

    function busyPillFrame(pane = photoPane) {
        const pillWidth = t("image.busy.width");
        const pillHeight = t("image.busy.height");
        return {
            x: pane.x + (pane.w - pillWidth) / 2,
            y: pane.y + pane.h - t("image.busy.bottomOffset") - pillHeight,
            w: pillWidth,
            h: pillHeight,
        };
    }

    function toastFrame(pane, textW, textH, pillVisible = false) {
        const width = Math.min(t("status.toast.maxWidth"), textW + t("status.toast.paddingX") * 2, pane.w);
        const height = textH + t("status.toast.paddingY") * 2;
        // STATE-03：药丸可见时提示条整体上移到药丸之上，底距 = 24 + 28 + 8。
        const bottom = pillVisible
            ? t("image.busy.bottomOffset") + t("image.busy.height") + t("status.toast.stackGap")
            : t("status.toast.bottomMargin");
        return {
            x: pane.x + (pane.w - width) / 2,
            y: pane.y + pane.h - bottom - height,
            w: width,
            h: height,
        };
    }

    function hintFrames(pane) {
        // STATE-01：提示块（标题 + 副文自然高）整体垂直居中，左右各留 ≥ image.hint.inset。
        const width = Math.max(0, Math.min(t("image.hint.maxWidth"), pane.w - 2 * t("image.hint.inset")));
        const detailHeight = t("image.hint.detail.fontSize");
        const gap = t("image.hint.detail.gap");
        const total = HINT_TITLE_LINE_HEIGHT + (gap - HINT_TITLE_LINE_HEIGHT) + detailHeight;
        const top = pane.y + (pane.h - total) / 2;
        const x = pane.x + (pane.w - width) / 2;
        return {
            title: { x, y: top, w: width, h: HINT_TITLE_LINE_HEIGHT },
            detail: {
                x,
                y: top + gap,
                w: width,
                h: detailHeight,
            },
        };
    }

    const dragzone = profile === "mac"
        ? layout === "col"
            ? {
                height: t("shell.dragzone.column.height"),
                rect: { x: 0, y: 0, w: t("shell.filter.leadingInset"), h: t("shell.dragzone.column.height") },
            }
            : { height: t("shell.dragzone.row.height"), rect: null }
        : null;

    return { navigatorFrame, busyPillFrame, toastFrame, hintFrames, dragzone };
}

function collectNodes({ width, height, panes, filter, film, control, showFilmbar, showControlbar }) {
    const nodes = { root: [0, 0, width, height] };
    if (showFilmbar) {
        nodes["root.filePane"] = rect(panes.filePane);
        nodes["root.fileDivider"] = rect(panes.fileDivider);
    }
    nodes["root.photoPane"] = rect(panes.photoPane);
    nodes.photoViewport = rect(panes.photoPane);
    if (showControlbar) {
        nodes["root.controlScroll"] = rect(panes.controlPane);
        nodes["root.controlDivider"] = rect(panes.controlDivider);
        nodes.controlClip = [0, 0, panes.controlPane.w, panes.controlPane.h];
        nodes.controlDocument = rect(control.document);
        control.groups.forEach((group) => {
            nodes[`control.group${group.index}`] = rect(group);
            group.children.forEach((child) => {
                nodes[`control.group${group.index}.child${child.index}`] = rect(child);
            });
        });
    }
    if (filter) {
        Object.entries(filter.controls).forEach(([name, frame]) => {
            nodes[`filter.${name}`] = rect(frame);
        });
    }
    if (film) {
        nodes.filmstripScroll = rect(film.scroll);
        nodes.filmstripClip = rect(film.clip);
        nodes.filmstripDocument = rect(film.document);
        film.cards.forEach((card) => {
            Object.assign(nodes, card.nodes);
        });
    }
    return nodes;
}

function rect(frame) {
    return [frame.x, frame.y, frame.w, frame.h];
}
