/**
 * 主屏分区几何（SHELL-01/07/08/09/11/12）。
 * 宽形态：行布局（文件栏在上、控制栏在下）与列布局（文件栏在左、控制栏在右）。
 * 手机形态：竖屏顶部行/分析/主图/胶片/底部行；横屏刘海侧列/主图/分析/控制栏。
 * 所有矩形为窗口坐标；photoView 始终铺满窗口，fitRect 是照片适应区。
 */

export function resolveLayoutMode(mode, width, height) {
    if (mode === "row" || mode === "col") return mode;
    if (mode === "phone") return "phone";
    return width > height ? "col" : "row";
}

export function computePanes(spec, t) {
    const { form, orientation, mode, width, height, safe } = spec;
    if (form === "phone") {
        return orientation === "landscape" ? landscapePanes(spec, t) : portraitPanes(spec, t);
    }
    return mode === "col" ? wideColumnPanes(spec, t) : wideRowPanes(spec, t);
}

function wideColumnPanes(spec, t) {
    const {
        width, height, safe, showFilmbar, showControlbar, showAnalysis,
        controlCross, analysisCross, similarityWidth, toolMaterial,
    } = spec;
    const dividerWidth = t("shell.divider.thickness");
    const fileContent = t("shell.filebar.content.width");
    const fileWidth = showFilmbar ? fileContent + similarityWidth : 0;
    const fileDivider = { x: fileWidth, y: 0, w: showFilmbar ? dividerWidth : 0, h: height };
    const filePane = { x: 0, y: 0, w: fileWidth, h: height };
    const toolWidth = (showAnalysis ? analysisCross : 0) + (showControlbar ? controlCross : 0);
    const toolRegion = toolWidth > 0
        ? { x: width - toolWidth, y: 0, w: toolWidth, h: height }
        : { x: width, y: 0, w: 0, h: height };
    const controlPane = showControlbar
        ? { x: width - controlCross, y: 0, w: controlCross, h: height }
        : { x: width, y: 0, w: 0, h: height };
    const analysisPane = showAnalysis
        ? {
            x: width - controlCross - analysisCross,
            y: 0,
            w: analysisCross,
            h: height,
        }
        : { x: width, y: 0, w: 0, h: height };
    const photoX = fileWidth + fileDivider.w;
    const toolEdge = toolWidth > 0 ? toolRegion.x - t("shell.divider.thickness") : width;
    const fitRect = {
        x: photoX,
        y: 0,
        w: Math.max(1, toolEdge - photoX),
        h: height,
    };
    return {
        layout: "col",
        panes: {
            photoView: { x: 0, y: 0, w: width, h: height },
            fitRect,
            filePane,
            fileDivider,
            analysisPane,
            analysisDivider: { x: 0, y: 0, w: 0, h: 0 },
            controlPane,
            controlDivider: { x: 0, y: 0, w: 0, h: 0 },
            toolRegion,
            toolEdge: toolWidth > 0 ? toolRegion.x - t("shell.divider.thickness") : null,
        },
        materials: [
            ...(showFilmbar ? [{ kind: "file", frame: filePane }] : []),
            ...(toolWidth > 0 && toolMaterial ? [{ kind: "tool", frame: toolRegion }] : []),
        ],
        dividers: [
            ...(showFilmbar ? [{ kind: "file", frame: fileDivider }] : []),
            ...(toolWidth > 0 && toolMaterial ? [{ kind: "tool", frame: { x: toolRegion.x - t("shell.divider.thickness"), y: 0, w: t("shell.divider.thickness"), h: height } }] : []),
        ],
    };
}

function wideRowPanes(spec, t) {
    const {
        width, height, safe, showFilmbar, showControlbar, showAnalysis,
        controlCross, analysisCross, filterBandHeight, filmBandHeight, toolMaterial,
    } = spec;
    const dividerThickness = t("shell.divider.thickness");
    const top = safe.top;
    // SHELL-01/10：上下布局继续避让顶部安全区；横屏不为底部手势条留位，竖屏保留。
    const bottomInset = width > height ? 0 : safe.bottom;
    const bottom = height - bottomInset;
    const fileHeight = showFilmbar ? filterBandHeight + filmBandHeight : 0;
    const filePane = { x: 0, y: top, w: width, h: fileHeight };
    // SHELL-12：背景从窗口顶边铺到内容末端；只有内容从 safe.top 起。
    const fileMaterial = { x: 0, y: 0, w: width, h: top + fileHeight };
    const fileDivider = { x: 0, y: top + fileHeight, w: width, h: showFilmbar ? dividerThickness : 0 };
    const controlPane = showControlbar
        ? { x: 0, y: bottom - controlCross, w: width, h: controlCross }
        : { x: 0, y: bottom, w: width, h: 0 };
    const analysisPane = showAnalysis
        ? { x: 0, y: bottom - controlCross - analysisCross, w: width, h: analysisCross }
        : { x: 0, y: bottom, w: width, h: 0 };
    const toolHeight = (showAnalysis ? analysisCross : 0) + (showControlbar ? controlCross : 0);
    const toolRegion = toolHeight > 0
        ? { x: 0, y: bottom - toolHeight, w: width, h: toolHeight }
        : { x: 0, y: bottom, w: width, h: 0 };
    const toolEdge = toolHeight > 0 ? toolRegion.y - dividerThickness : bottom;
    const photoY = filePane.y + filePane.h + fileDivider.h;
    const fitRect = {
        x: 0,
        y: photoY,
        w: width,
        h: Math.max(1, toolEdge - photoY),
    };
    return {
        layout: "row",
        panes: {
            photoView: { x: 0, y: 0, w: width, h: height },
            fitRect,
            filePane,
            fileDivider,
            analysisPane,
            analysisDivider: { x: 0, y: 0, w: 0, h: 0 },
            controlPane,
            controlDivider: { x: 0, y: 0, w: 0, h: 0 },
            toolRegion,
            toolEdge: toolHeight > 0 ? toolRegion.y - dividerThickness : null,
        },
        materials: [
            ...(showFilmbar ? [{ kind: "file", frame: fileMaterial }] : []),
            ...(toolHeight > 0 && toolMaterial ? [{ kind: "tool", frame: toolRegion }] : []),
        ],
        dividers: [
            ...(showFilmbar ? [{ kind: "file", frame: fileDivider }] : []),
            ...(toolHeight > 0 && toolMaterial ? [{ kind: "tool", frame: { x: 0, y: toolRegion.y - dividerThickness, w: width, h: dividerThickness } }] : []),
        ],
    };
}

function portraitPanes(spec, t) {
    const {
        width, height, safe, showFilmbar, showControlbar, showAnalysis,
        topRowY, exifVisible, analysisCross, filmBandHeight, controlCross,
        controlLeft, controlRight, bottomInset,
    } = spec;
    const button = t("shell.glass.button.size");
    const rowInset = t("shell.phone.rowInset");
    const topRow = { x: 0, y: topRowY, w: width, h: button };
    const bottomRowY = height - bottomInset - button;
    const controlPane = showControlbar
        ? { x: 0, y: bottomRowY, w: width, h: button }
        : { x: 0, y: height, w: width, h: 0 };
    const filmPane = showFilmbar
        ? {
            x: 0,
            y: (showControlbar ? bottomRowY : height - bottomInset) - filmBandHeight,
            w: width,
            h: filmBandHeight,
        }
        : { x: 0, y: height, w: 0, h: 0 };
    const analysisPane = showAnalysis
        ? { x: 0, y: topRowY + button, w: width, h: analysisCross }
        : { x: 0, y: topRowY + button, w: 0, h: 0 };
    const fitTop = showAnalysis ? analysisPane.y + analysisPane.h : topRowY + button + rowInset;
    const fitBottom = showFilmbar
        ? filmPane.y
        : showControlbar
            ? bottomRowY - rowInset
            : height;
    const fitRect = {
        x: safe.left,
        y: fitTop,
        w: Math.max(1, width - safe.left - safe.right),
        h: Math.max(1, fitBottom - fitTop),
    };
    const topBand = showAnalysis
        ? { x: 0, y: 0, w: width, h: analysisPane.y + analysisPane.h }
        : null;
    const bottomBand = showFilmbar
        ? { x: 0, y: filmPane.y, w: width, h: height - filmPane.y }
        : null;
    return {
        layout: "phone",
        orientation: "portrait",
        panes: {
            photoView: { x: 0, y: 0, w: width, h: height },
            fitRect,
            topRow,
            bottomRow: showControlbar ? controlPane : null,
            sideColumn: null,
            filePane: filmPane,
            fileDivider: { x: 0, y: 0, w: 0, h: 0 },
            filmPane,
            analysisPane,
            analysisDivider: { x: 0, y: 0, w: 0, h: 0 },
            controlPane,
            controlDivider: { x: 0, y: 0, w: 0, h: 0 },
            toolRegion: topBand ?? { x: 0, y: 0, w: 0, h: 0 },
            toolEdge: null,
        },
        materials: [
            ...(topBand ? [{ kind: "top", frame: topBand }] : []),
            ...(bottomBand ? [{ kind: "bottom", frame: bottomBand }] : []),
        ],
        dividers: [],
        safe: {
            ...safe,
            controlLeft,
            controlRight,
            bottomInset,
        },
    };
}

function landscapePanes(spec, t) {
    const {
        width, height, safe, showFilmbar, showControlbar, showAnalysis,
        sideWidth, controlCross, analysisCross, filterBandHeight, toolMaterial,
    } = spec;
    const button = t("shell.glass.button.size");
    const margin = t("filter.margin");
    const mirror = safe.notchSide === "right";
    const sideX = mirror ? width - sideWidth : 0;
    const toolWidth = (showAnalysis ? analysisCross : 0) + (showControlbar ? controlCross : 0);
    const controlPane = showControlbar
        ? (mirror
            ? { x: 0, y: 0, w: controlCross, h: height }
            : { x: width - controlCross, y: 0, w: controlCross, h: height })
        : { x: mirror ? 0 : width, y: 0, w: 0, h: height };
    const analysisPane = showAnalysis
        ? (mirror
            ? { x: controlCross, y: 0, w: analysisCross, h: height }
            : { x: width - controlCross - analysisCross, y: 0, w: analysisCross, h: height })
        : { x: mirror ? 0 : width, y: 0, w: 0, h: height };
    const toolRegion = toolWidth > 0
        ? (mirror
            ? { x: 0, y: 0, w: toolWidth, h: height }
            : { x: width - toolWidth, y: 0, w: toolWidth, h: height })
        : { x: mirror ? 0 : width, y: 0, w: 0, h: height };
    const fitLeft = (mirror ? toolWidth : sideWidth) + (toolWidth > 0 ? t("shell.divider.thickness") : 0);
    const fitRight = mirror ? width - sideWidth : width - toolWidth;
    const fitRect = {
        x: fitLeft,
        y: 0,
        w: Math.max(1, fitRight - fitLeft),
        h: height,
    };
    const sideColumn = {
        x: sideX,
        y: 0,
        w: sideWidth,
        h: height,
    };
    const filterY = Math.max(safe.top, margin);
    return {
        layout: "phone",
        orientation: "landscape",
        panes: {
            photoView: { x: 0, y: 0, w: width, h: height },
            fitRect,
            topRow: { x: sideX, y: filterY, w: sideWidth, h: button },
            bottomRow: null,
            sideColumn,
            filePane: sideColumn,
            fileDivider: { x: 0, y: 0, w: 0, h: 0 },
            filmPane: sideColumn,
            analysisPane,
            analysisDivider: { x: 0, y: 0, w: 0, h: 0 },
            controlPane,
            controlDivider: { x: 0, y: 0, w: 0, h: 0 },
            toolRegion,
            toolEdge: toolWidth > 0
                ? (mirror ? toolRegion.x + toolRegion.w : toolRegion.x - t("shell.divider.thickness"))
                : null,
        },
        materials: [
            ...(showFilmbar ? [{ kind: "side", frame: sideColumn }] : []),
            ...(toolWidth > 0 && toolMaterial ? [{ kind: "tool", frame: toolRegion }] : []),
        ],
        dividers: toolWidth > 0 && toolMaterial
            ? [{
                kind: "tool",
                frame: mirror
                    ? { x: toolRegion.x + toolRegion.w, y: 0, w: t("shell.divider.thickness"), h: height }
                    : { x: toolRegion.x - t("shell.divider.thickness"), y: 0, w: t("shell.divider.thickness"), h: height },
            }]
            : [],
        safe: {
            ...safe,
            mirror,
            sideWidth,
            filterY,
            filterBandHeight,
        },
    };
}
