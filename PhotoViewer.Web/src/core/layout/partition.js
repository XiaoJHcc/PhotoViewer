/**
 * 主窗口分区几何（SHELL-01）。
 * resolveLayoutMode(mode, width, height) → "row" | "col"（auto 只看内容区宽高比）。
 * computePanes(scene, measured, t) → { layout, panes }，panes 含 filePane/fileDivider/photoPane/
 * analysisPane/analysisDivider/controlPane/controlDivider，隐藏栏位尺寸为 0、分隔线消失。
 */

export function resolveLayoutMode(mode, width, height) {
    if (mode === "row" || mode === "col") return mode;
    return width > height ? "col" : "row";
}

export function computePanes(scene, measured, t) {
    const { width, height, showFilmbar, showControlbar, showAnalysis } = scene;
    const layout = resolveLayoutMode(scene.layoutMode, width, height);
    const divider = t("shell.divider.thickness");
    const analysisDivider = t("shell.analysis.divider.thickness");
    const fileContent = t("shell.filebar.content.width");
    const controlCross = showControlbar ? measured.controlCross : 0;
    const analysisCross = showAnalysis ? measured.analysisCross : 0;
    const similarityWidth = showFilmbar ? measured.similarityWidth ?? 0 : 0;

    if (layout === "col") {
        const fileWidth = showFilmbar ? fileContent + similarityWidth : 0;
        const fileDivider = { x: fileWidth, y: 0, w: showFilmbar ? divider : 0, h: height };
        const control = { x: width - controlCross, y: 0, w: controlCross, h: height };
        const controlDivider = {
            x: control.x - (showControlbar ? divider : 0),
            y: 0,
            w: showControlbar ? divider : 0,
            h: height,
        };
        const photoX = fileWidth + fileDivider.w;
        const photoWidth = Math.max(1, controlDivider.x - photoX - analysisCross - (showAnalysis ? analysisDivider : 0));
        const photoPane = { x: photoX, y: 0, w: photoWidth, h: height };
        const analysisPane = {
            x: photoX + photoWidth + (showAnalysis ? analysisDivider : 0),
            y: 0,
            w: analysisCross,
            h: height,
        };
        return {
            layout,
            panes: {
                filePane: { x: 0, y: 0, w: fileWidth, h: height },
                fileDivider,
                photoPane,
                analysisDivider: { x: photoPane.x + photoWidth, y: 0, w: showAnalysis ? analysisDivider : 0, h: height },
                analysisPane,
                controlPane: control,
                controlDivider,
            },
        };
    }

    const fileHeight = showFilmbar ? t("filter.bar.row.height") + t("film.band.height") : 0;
    const filePane = { x: 0, y: 0, w: width, h: fileHeight };
    const fileDivider = { x: 0, y: fileHeight, w: width, h: showFilmbar ? divider : 0 };
    const controlPane = { x: 0, y: height - controlCross, w: width, h: controlCross };
    const controlDivider = {
        x: 0,
        y: controlPane.y - (showControlbar ? divider : 0),
        w: width,
        h: showControlbar ? divider : 0,
    };
    const analysisPane = { x: 0, y: controlDivider.y - analysisCross, w: width, h: analysisCross };
    const analysisDividerRect = {
        x: 0,
        y: analysisPane.y - (showAnalysis ? analysisDivider : 0),
        w: width,
        h: showAnalysis ? analysisDivider : 0,
    };
    const photoY = fileHeight + fileDivider.h;
    const photoPane = { x: 0, y: photoY, w: width, h: Math.max(1, analysisDividerRect.y - photoY) };
    return {
        layout,
        panes: {
            filePane,
            fileDivider,
            photoPane,
            analysisDivider: analysisDividerRect,
            analysisPane,
            controlPane,
            controlDivider,
        },
    };
}
