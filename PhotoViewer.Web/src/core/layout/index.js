/**
 * 主屏几何汇总（SHELL-01/07–12、FILTER-07、FILM-02/05/07、TOOLBAR-02/03/05/08、ANALYSIS-03/05、SIM-07）。
 * computeLayout(scene, { t, profile, measureText }) →
 * { form, orientation, layoutMode, panes, filter, film, exif, control, analysis, similarity,
 *   materials, dividers, overlays, nodes }。
 * 坐标单位逻辑点；photoView 与各图层为窗口坐标，film.document/items 相对滚动区，
 * control 组相对控制文档，analysis.tiles 相对分析分区。
 */
import { resolveScreenForm, normalizeSafeArea } from "./form.js";
import { computePanes } from "./partition.js";
import { filterGeometry, filterBarHeight } from "./filterbar.js";
import { computeControlBar, computePhoneControl, ratingMainLength } from "./controlbar.js";
import { filmGeometry, filmTierSizes } from "./film.js";
import { cardFrames, cardNodes } from "./card.js";
import { analysisGeometry } from "./analysis.js";
import { similarityGeometry } from "./similarity.js";
import { approximateTextWidth } from "./text.js";

export { approximateTextWidth } from "./text.js";
export { resolveScreenForm, normalizeSafeArea } from "./form.js";
export { filterBarHeight } from "./filterbar.js";

const HINT_TITLE_LINE_HEIGHT = 20;
const LARGE_TIER_MIN_CARDS = 5;

export function computeLayout(scene, { t, profile = "web", measureText = approximateTextWidth } = {}) {
    if (typeof t !== "function") throw new Error("computeLayout 需要令牌函数 t");
    const width = scene.width;
    const height = scene.height;
    const form = resolveScreenForm(scene);
    const safe = normalizeSafeArea(scene.safeArea, form.orientation);
    const layoutMode = form.form === "phone" ? "phone" : form.mode;
    const showFilmbar = Boolean(scene.showFilmbar);
    const showControlbar = Boolean(scene.showControlbar);
    const showAnalysis = Boolean(scene.showAnalysis);
    const showExif = scene.showExif !== false;
    const showNavigation = scene.showNavigation !== false;
    const similarityCapable = scene.showSimilarity !== false && form.form === "wide";
    const similarityExpanded = Boolean(similarityCapable && scene.similarityExpanded);
    const photoCount = scene.photoCount ?? 0;
    const exifCount = scene.exifCount ?? 0;
    const commandCount = scene.commandCount ?? 0;
    const tileCount = scene.analysisTileCount ?? 6;
    const countText = scene.countText ?? `共 ${photoCount} 张`;
    const countWidth = measureText(countText, t("filter.count.fontSize"), 400);

    const filterBandHeight = filterBarHeight(t);
    const filmBandHeight = t("film.band.height");

    const similarity = similarityGeometry({
        expanded: similarityExpanded,
        axis: form.form === "wide" && form.mode === "col" ? "vertical" : "horizontal",
        availableWidth: width,
        itemCount: scene.similarityItemCount ?? 0,
        dockTexts: scene.similarityDockTexts ?? [],
    }, t, measureText);
    const similarityWidth = form.form === "wide" && showFilmbar ? similarity.occupation : 0;

    const exifWantedWide = showControlbar && exifCount > 0;
    const exifWantedPortrait = showExif && exifCount > 0;
    const exifWantedLandscape = showExif && exifCount > 0 && showControlbar;
    const exifWanted = form.form === "phone"
        ? form.orientation === "portrait" ? exifWantedPortrait : exifWantedLandscape
        : exifWantedWide;

    const phoneOrientation = form.orientation;
    const button = t("shell.glass.button.size");
    const edge = t("shell.phone.edgeMargin");
    const rowInset = t("shell.phone.rowInset");
    const adaptiveTier = form.form === "phone"
        ? choosePhoneTier({ orientation: phoneOrientation, width, height, t })
        : "large";

    const side = form.form === "phone" && phoneOrientation === "landscape"
        ? computeSideGeometry(t, safe, adaptiveTier)
        : null;
    const sideWidth = side?.sideWidth ?? 0;
    const filterY = Math.max(safe.top, t("filter.margin"));

    // SHELL-08：竖屏上下张只要求控制栏与上下张开关；横屏才要求胶片栏与上下张开关。
    const navVisible = form.form === "phone"
        ? phoneOrientation === "portrait"
            ? showControlbar && showNavigation
            : showFilmbar && showNavigation
        : false;

    const portraitControlLeft = safe.left + edge;
    const portraitControlRight = safe.right + edge;
    const portraitBottomInset = Math.max(
        safe.bottom - rowInset,
        Math.min(portraitControlLeft, width - portraitControlRight),
        rowInset,
    );

    const wideAxis = form.mode === "col" ? "vertical" : "horizontal";
    const adjacent = showAnalysis && showControlbar;
    const crossNear = adjacent ? 0 : t("toolbar.padding");
    const crossFar = t("toolbar.padding");
    const wideControl = form.form === "wide"
        ? computeControlBar({
            axis: wideAxis,
            mainSize: form.mode === "col" ? height : width,
            exifCount: exifWanted ? exifCount : 0,
            showRating: scene.showRating ?? true,
            commandCount,
            crossNear,
            crossFar,
        }, t)
        : null;

    const phoneLandscapeControlCross = form.form === "phone" && phoneOrientation === "landscape" && showControlbar
        ? button + (showAnalysis ? 0 : t("toolbar.padding")) + t("toolbar.padding")
        : 0;
    const controlCross = form.form === "wide"
        ? (showControlbar ? wideControl.paneCross : 0)
        : phoneOrientation === "portrait" && showControlbar
            ? button
            : phoneLandscapeControlCross;

    const analysis = computeAnalysis({
        form: form.form, orientation: phoneOrientation, mode: form.mode,
        width, height, tileCount, showAnalysis, showControlbar,
        mirror: safe.notchSide === "right",
        t,
    });

    const { panes, materials, dividers, safe: safeExtra } = computePanes({
        form: form.form,
        orientation: phoneOrientation,
        mode: form.mode,
        width,
        height,
        safe,
        showFilmbar,
        showControlbar,
        showAnalysis,
        controlCross,
        analysisCross: analysis.cross,
        similarityWidth,
        toolMaterial: showAnalysis || exifWanted,
        filterBandHeight,
        filmBandHeight: form.form === "phone" && phoneOrientation === "portrait"
            ? adaptiveTier === "small"
                ? t("film.card.small.height") + 2 * t("film.list.small.padCross")
                : t("film.card.height") + 2 * t("shell.phone.rowInset")
            : filmBandHeight,
        topRowY: form.form === "phone" && phoneOrientation === "portrait"
            ? portraitTopRowY({ safe, exif: exifWanted, t })
            : 0,
        controlLeft: portraitControlLeft,
        controlRight: portraitControlRight,
        bottomInset: portraitBottomInset,
        sideWidth,
        navVisible,
    }, t);

    const filter = showFilmbar || form.form === "phone"
        ? filterGeometry({
            form: form.form,
            orientation: phoneOrientation,
            mode: form.mode,
            width,
            safe,
            profile,
            segmentWidth: scene.filterSegmentWidth ?? t("filter.control.width"),
            segmentWidths: scene.filterSegmentWidths ?? null,
            showSimilarity: similarityCapable,
            showFilmbar,
            fileContentWidth: t("shell.filebar.content.width"),
            countWidth,
            topRowX: portraitControlLeft,
            topRowY: panes.topRow ? panes.topRow.y : 0,
            topRowRight: width - portraitControlRight,
            landscapeX: side
                ? (safeExtra?.mirror ? width - side.contentTrailing : side.filterX)
                : 0,
            landscapeTop: filterY,
        }, t)
        : null;
    const film = showFilmbar
        ? computeFilm({
            form: form.form, orientation: phoneOrientation, mode: form.mode, width, height,
            panes, filter, safe, safeExtra, similarityWidth, adaptiveTier,
            side, filterY, navVisible, count: photoCount,
        }, t)
        : null;

    const control = form.form === "wide"
        ? wideControl
        : computePhoneSlots({
            orientation: phoneOrientation,
            width,
            height,
            panes,
            exifVisible: exifWanted,
            exifCount,
            showRating: scene.showRating ?? true,
            showNavigation: navVisible,
            controlLeft: portraitControlLeft,
            controlRight: portraitControlRight,
            crossNear: phoneOrientation === "landscape" && showControlbar
                ? (safe.notchSide === "right" || !showAnalysis ? t("toolbar.padding") : 0)
                : 0,
        }, t);
    if (control && control.mode === "phone" && !showControlbar) {
        if (phoneOrientation === "portrait") {
            control.slots = {};
        } else {
            delete control.slots.exif;
            delete control.slots.rating;
            delete control.slots.fit;
            delete control.slots.enhance;
        }
        control.document = { x: 0, y: 0, w: 0, h: 0 };
    }
    if (control && control.mode === "wide" && !showControlbar) {
        control.groups = [];
        control.lines = [];
        control.empty = true;
        control.document = { x: 0, y: 0, w: 0, h: 0 };
        control.paneCross = 0;
    }

    const exif = computeExif({
        form: form.form, orientation: phoneOrientation, mode: form.mode,
        panes, filter, control, exifCount, exifWanted,
        wideAxis, t,
    });

    const similarityPanel = similarityExpanded && similarity.visible !== false && showFilmbar
        ? (form.mode === "col"
            ? { x: t("shell.filebar.content.width"), y: filter.bar.h, w: similarity.width, h: Math.max(0, height - filter.bar.h) }
            : {
                x: film?.scroll.w ?? width,
                y: film?.scroll.y ?? 0,
                w: similarity.width,
                h: similarity.height ?? t("film.band.height"),
            })
        : null;

    if (form.form === "phone") {
        // FILTER-07：手机形态没有相似聚类开关与面板。
        similarity.expanded = false;
        similarity.occupation = 0;
    }

    const nav = form.form === "phone" && phoneOrientation === "landscape" && navVisible
        ? {
            visible: true,
            previous: { x: filter.composite.x, y: height - t("filter.margin") - button, w: button, h: button },
            next: { x: filter.composite.x + button, y: height - t("filter.margin") - button, w: button, h: button },
        }
        : { visible: false, previous: null, next: null };

    if (nav.visible && control?.slots) {
        control.slots.previous = { ...nav.previous };
        control.slots.next = { ...nav.next };
    }

    const overlays = createOverlays(t, profile, form.mode, panes);
    const frames = cardFrames(t, adaptiveTier);
    const cards = [];
    if (film) {
        film.items.forEach((frame, index) => {
            cards.push({ index, frame, nodes: cardNodes(frames, index, frame) });
        });
        film.cards = cards;
    }

    const nodes = collectNodes({
        width, height, form, panes, filter, film, exif, control, analysis: analysis.layout,
        similarity, similarityPanel, materials, dividers, nav,
    });

    return {
        form: form.form,
        orientation: phoneOrientation,
        layoutMode,
        filterBandHeight,
        panes,
        filter,
        film,
        exif,
        control,
        analysis: showAnalysis ? analysis.layout : null,
        similarity: { ...similarity, panel: similarityPanel },
        nav,
        materials,
        dividers,
        overlays,
        nodes,
        safe: safeExtra ?? safe,
    };
}

function choosePhoneTier({ orientation, width, height, t }) {
    const main = orientation === "portrait" ? width : height;
    // FILM-05：大档主轴两端内边距取 film.list.*，横向 10+10、纵向 5+10。
    const mainPadding = orientation === "portrait"
        ? t("film.list.padLeft") + t("film.list.padRight")
        : t("film.list.padTop") + t("film.list.padBottom");
    const large = orientation === "portrait" ? t("film.card.width") : t("film.card.height");
    return main - mainPadding >= LARGE_TIER_MIN_CARDS * (large + t("film.card.spacing"))
        ? "large"
        : "small";
}

function computeSideGeometry(t, safe, tier) {
    const cardWidth = filmTierSizes(t, tier).width;
    const crossLead = tier === "small" ? t("film.list.small.padCross") : t("film.list.padLeft");
    const crossTrail = tier === "small" ? t("film.list.small.padCross") : t("film.list.padRight");
    const filmBand = cardWidth + crossLead + crossTrail;
    const filmTrailing = Math.max(0, safe.notchInset - crossLead) + filmBand - crossTrail;
    const contentTrailing = Math.max(filmTrailing, t("filter.margin") + t("shell.glass.button.size") * 2);
    const sideWidth = contentTrailing + crossTrail;
    return {
        cardWidth,
        crossLead,
        crossTrail,
        filmBand,
        filmTrailing,
        contentTrailing,
        sideWidth,
        cardX: contentTrailing - cardWidth,
        filterX: contentTrailing - t("shell.glass.button.size") * 2,
    };
}

function portraitTopRowY({ safe, exif, t }) {
    const button = t("shell.glass.button.size");
    const edge = t("shell.phone.edgeMargin");
    const rowInset = t("shell.phone.rowInset");
    const base = Math.max(safe.top, rowInset);
    if (exif) return base;
    if (safe.top > edge) return Math.max(edge, safe.top - button);
    return base;
}

function computeAnalysis({ form, orientation, mode, width, height, tileCount, showAnalysis, showControlbar, mirror, t }) {
    const analysisPadding = t("analysis.panel.padding");
    const seam = t("shell.toolRegion.spacing");
    const toolbarPadding = t("toolbar.padding");
    const rowInset = t("shell.phone.rowInset");
    if (!showAnalysis) return { cross: 0, layout: null };
    if (form === "phone" && orientation === "portrait") {
        const layout = analysisGeometry({
            axis: "horizontal",
            pane: { x: 0, y: 0, w: width, h: 0 },
            tileCount,
            padding: rowInset,
            depthStart: rowInset,
            depthEnd: rowInset,
        }, t);
        return { cross: layout.cross, layout };
    }
    if (form === "phone" && orientation === "landscape") {
        const adjacent = showControlbar;
        const depthStart = adjacent ? (mirror ? seam : toolbarPadding) : analysisPadding;
        const depthEnd = adjacent ? (mirror ? toolbarPadding : seam) : analysisPadding;
        const layout = analysisGeometry({
            axis: "vertical",
            pane: { x: 0, y: 0, w: 0, h: height },
            tileCount,
            padding: analysisPadding,
            depthStart,
            depthEnd,
        }, t);
        return { cross: layout.cross, layout };
    }
    const axis = mode === "col" ? "vertical" : "horizontal";
    const adjacent = showAnalysis && showControlbar;
    const depthStart = adjacent ? toolbarPadding : analysisPadding;
    const depthEnd = adjacent ? seam : analysisPadding;
    const layout = analysisGeometry({
        axis,
        pane: axis === "vertical" ? { x: 0, y: 0, w: 0, h: height } : { x: 0, y: 0, w: width, h: 0 },
        tileCount,
        padding: analysisPadding,
        depthStart,
        depthEnd,
    }, t);
    return { cross: layout.cross, layout };
}

function computeFilm(spec, t) {
    const {
        form, orientation, mode, width, height, panes, filter, safe, safeExtra,
        similarityWidth, adaptiveTier, side, filterY, navVisible, count,
    } = spec;
    if (form === "phone" && orientation === "portrait") {
        return filmGeometry({
            axis: "x",
            tier: adaptiveTier,
            scroll: panes.filmPane,
            count,
            lead: adaptiveTier === "small" ? t("film.list.small.padMain") : t("shell.phone.rowInset"),
            trail: adaptiveTier === "small" ? t("film.list.small.padMain") : t("shell.phone.rowInset"),
            crossLead: adaptiveTier === "small" ? t("film.list.small.padCross") : t("shell.phone.rowInset"),
            crossTrail: adaptiveTier === "small" ? t("film.list.small.padCross") : t("shell.phone.rowInset"),
        }, t);
    }
    if (form === "phone" && orientation === "landscape") {
        const entryBottom = filterY + t("shell.glass.button.size");
        const navTop = height - t("filter.margin") - t("shell.glass.button.size");
        const lead = entryBottom + t("filter.margin");
        const trail = navVisible
            ? t("shell.glass.button.size") + 2 * t("filter.margin")
            : t("filter.margin");
        const crossLead = safeExtra?.mirror ? side.sideWidth - side.contentTrailing : side.cardX;
        return filmGeometry({
            axis: "y",
            tier: adaptiveTier,
            scroll: panes.filmPane,
            count,
            lead,
            trail: Math.max(0, trail),
            crossLead,
            crossTrail: side.crossTrail,
        }, t);
    }
    if (mode === "col") {
        return filmGeometry({
            axis: "y",
            tier: "large",
            scroll: { x: 0, y: 0, w: t("shell.filebar.content.width"), h: height },
            count,
            lead: filter.bar.h,
            trail: t("film.list.padBottom"),
            crossLead: t("film.list.padLeft"),
            crossTrail: t("film.list.padRight"),
        }, t);
    }
    return filmGeometry({
        axis: "x",
        tier: "large",
        scroll: {
            x: 0,
            y: (panes.filePane?.y ?? 0) + (filter?.bar.h ?? 0),
            w: Math.max(0, width - similarityWidth),
            h: t("film.band.height"),
        },
        count,
        lead: t("film.list.padLeft"),
        trail: t("film.list.padRight"),
        crossLead: t("film.list.padTop"),
        crossTrail: t("film.list.padBottom"),
    }, t);
}

function computePhoneSlots(spec, t) {
    const {
        orientation, width, height, panes, exifVisible, exifCount,
        showRating, showNavigation, crossNear, controlLeft, controlRight,
    } = spec;
    if (orientation === "portrait") {
        const layout = computePhoneControl({
            orientation,
            width,
            height: t("shell.glass.button.size"),
            controlLeft,
            controlRight,
            exifCount: 0,
            showRating,
            showNavigation,
        }, t);
        layout.origin = { x: panes.controlPane.x, y: panes.controlPane.y };
        return layout;
    }
    const layout = computePhoneControl({
        orientation,
        width,
        height,
        exifCount: exifVisible ? exifCount : 0,
        showRating,
        showNavigation: false,
    }, t);
    const originX = panes.controlPane.x;
    if (crossNear) {
        for (const slot of Object.values(layout.slots)) {
            slot.x += crossNear;
            if (slot.visual) slot.visual.x += crossNear;
        }
    }
    for (const slot of Object.values(layout.slots)) {
        slot.x += originX;
        if (slot.visual) slot.visual.x += originX;
    }
    layout.origin = { x: 0, y: 0 };
    return layout;
}

function computeExif(spec, t) {
    const { form, orientation, mode, panes, filter, control, exifCount, exifWanted, wideAxis, t: token } = spec;
    if (!exifWanted || exifCount <= 0) return { visible: false, variant: null, items: [] };
    const button = token("shell.glass.button.size");
    const padding = token("toolbar.exif.padding");
    const spacing = token("toolbar.exif.item.spacing");

    if (form === "phone" && orientation === "portrait") {
        const gap = token("shell.glass.group.gap");
        const left = (filter?.entry?.x ?? 0) + button + gap;
        const right = (filter?.more?.x ?? 0) - gap;
        const width = Math.max(0, right - left);
        if (width <= padding * 2) return { visible: false, variant: null, items: [] };
        const itemWidth = Math.max(0, (width - padding * 2 - spacing * (exifCount - 1)) / exifCount);
        if (itemWidth <= 0) return { visible: false, variant: null, items: [] };
        return {
            visible: true,
            host: "window",
            origin: { x: 0, y: 0 },
            variant: "block",
            frame: { x: left, y: filter.entry.y, w: width, h: button },
            items: Array.from({ length: exifCount }, (_, index) => ({
                index,
                x: padding + index * (itemWidth + spacing),
                y: 0,
                w: itemWidth,
                h: button,
            })),
        };
    }

    if (form === "phone" && orientation === "landscape") {
        const slot = control?.slots?.exif;
        if (!slot) return { visible: false, variant: null, items: [] };
        return {
            visible: true,
            host: "control",
            origin: { x: 0, y: 0 },
            variant: "bare",
            frame: { x: slot.x, y: slot.y, w: slot.w, h: slot.h },
            items: slot.items.map((item) => ({ ...item })),
        };
    }

    const group = control?.groups?.find((entry) => entry.kind === "exif");
    if (!group) return { visible: false, variant: null, items: [] };
    const origin = panes.controlPane;
    if (wideAxis === "vertical") {
        return {
            visible: true,
            host: "control",
            origin: { x: origin.x, y: origin.y },
            variant: "bare",
            frame: { x: group.x, y: group.y, w: group.w, h: group.h },
            items: group.children.map((child) => ({ ...child })),
        };
    }
    return {
        visible: true,
        host: "control",
        origin: { x: origin.x, y: origin.y },
        variant: "block",
        frame: { x: group.x, y: group.y, w: group.w, h: group.h },
        items: group.children.map((child) => ({ ...child })),
    };
}

function createOverlays(t, profile, layout, panes) {
    const fit = panes.fitRect;
    const photoPane = fit;

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
        const width = Math.max(0, Math.min(t("image.hint.maxWidth"), pane.w - 2 * t("image.hint.inset")));
        const detailHeight = t("image.hint.detail.fontSize");
        const gap = t("image.hint.detail.gap");
        const total = HINT_TITLE_LINE_HEIGHT + (gap - HINT_TITLE_LINE_HEIGHT) + detailHeight;
        const top = pane.y + (pane.h - total) / 2;
        const x = pane.x + (pane.w - width) / 2;
        return {
            title: { x, y: top, w: width, h: HINT_TITLE_LINE_HEIGHT },
            detail: { x, y: top + gap, w: width, h: detailHeight },
        };
    }

    const dragzone = profile === "mac"
        ? layout === "col"
            ? {
                height: t("shell.titlebar.height"),
                rect: { x: 0, y: 0, w: panes.photoView.w, h: t("shell.titlebar.height") },
            }
            : { height: t("shell.titlebar.height"), rect: null }
        : null;

    return { navigatorFrame, busyPillFrame, toastFrame, hintFrames, dragzone };
}

function collectNodes(spec) {
    const {
        width, height, form, panes, filter, film, exif, control, analysis,
        similarity, similarityPanel, materials, dividers, nav,
    } = spec;
    const nodes = { root: [0, 0, width, height] };
    nodes["root.photoView"] = rect(panes.photoView);
    nodes["root.fitRect"] = rect(panes.fitRect);
    if (panes.filePane && panes.filePane.w > 0) nodes["root.filePane"] = rect(panes.filePane);
    if (panes.fileDivider && panes.fileDivider.w > 0 && panes.fileDivider.h > 0) nodes["root.fileDivider"] = rect(panes.fileDivider);
    if (panes.filmPane && panes.filmPane.w > 0) nodes["root.filmPane"] = rect(panes.filmPane);
    if (panes.analysisPane && panes.analysisPane.w > 0) nodes["root.analysisPane"] = rect(panes.analysisPane);
    if (panes.controlPane && panes.controlPane.w > 0) nodes["root.controlPane"] = rect(panes.controlPane);
    if (panes.toolRegion && panes.toolRegion.w > 0 && panes.toolRegion.h > 0) nodes["root.toolRegion"] = rect(panes.toolRegion);
    if (panes.topRow) nodes["phone.topRow"] = rect(panes.topRow);
    if (panes.bottomRow) nodes["phone.bottomRow"] = rect(panes.bottomRow);
    if (panes.sideColumn) nodes["phone.sideColumn"] = rect(panes.sideColumn);
    materials.forEach((entry) => {
        nodes[`material.${entry.kind}`] = rect(entry.frame);
    });
    dividers.forEach((entry) => {
        nodes[`divider.${entry.kind}`] = rect(entry.frame);
    });
    if (filter) {
        if (filter.composite) nodes["filter.composite"] = rect(filter.composite);
        if (filter.control) nodes["filter.control"] = rect(filter.control);
        if (filter.entry) nodes["filter.entry"] = rect(filter.entry);
        if (filter.more) nodes["filter.more"] = rect(filter.more);
        if (filter.toggle) nodes["filter.toggle"] = rect(filter.toggle);
        if (filter.count) nodes["filter.count"] = rect(filter.count);
        if (filter.controls) {
            for (const [name, frame] of Object.entries(filter.controls)) {
                nodes[`filter.${name}`] = rect(frame);
            }
        }
    }
    if (film) {
        nodes.filmstripScroll = rect(film.scroll);
        nodes.filmstripClip = rect(film.clip);
        nodes.filmstripDocument = rect(film.document);
        film.cards.forEach((card) => {
            Object.assign(nodes, card.nodes);
        });
    }
    if (exif && exif.visible) {
        const origin = exif.origin ?? { x: 0, y: 0 };
        nodes["exif.block"] = [origin.x + exif.frame.x, origin.y + exif.frame.y, exif.frame.w, exif.frame.h];
        exif.items.forEach((item) => {
            nodes[`exif.item${item.index}`] = rect(item);
        });
    }
    if (control) {
        if (control.mode === "wide") {
            nodes.controlClip = [0, 0, panes.controlPane.w, panes.controlPane.h];
            nodes.controlDocument = rect(control.document);
            control.groups.forEach((group) => {
                nodes[`control.group${group.index}`] = rect(group);
                group.children.forEach((child) => {
                    nodes[`control.group${group.index}.child${child.index}`] = rect(child);
                });
            });
        } else {
            nodes.controlDocument = rect(control.document);
            const origin = control.origin ?? { x: 0, y: 0 };
            for (const [name, slot] of Object.entries(control.slots)) {
                if (name === "exif") continue;
                if (!slot) continue;
                nodes[`control.${name}`] = [
                    origin.x + slot.x,
                    origin.y + slot.y,
                    slot.w,
                    slot.h,
                ];
                if (slot.visual) {
                    nodes[`control.${name}Visual`] = [
                        origin.x + slot.visual.x,
                        origin.y + slot.visual.y,
                        slot.visual.w,
                        slot.visual.h,
                    ];
                }
            }
        }
    }
    if (analysis) {
        analysis.tiles.forEach((tile) => {
            nodes[`analysis.tile${tile.index}`] = rect(tile);
        });
    }
    if (similarityPanel) nodes["similarity.panel"] = rect(similarityPanel);
    if (nav?.visible) {
        nodes["phone.navPrevious"] = rect(nav.previous);
        nodes["phone.navNext"] = rect(nav.next);
    }
    if (form === "phone") nodes["phone.form"] = [0, 0, width, height];
    return nodes;
}

function rect(frame) {
    return [frame.x, frame.y, frame.w, frame.h];
}
