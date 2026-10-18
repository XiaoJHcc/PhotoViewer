/*
 * 主窗口几何：忠实复刻 PhotoViewer.Shared 的平台中立数学。
 *
 * 对照源：
 *   MainScreenLayout.cs（分区、筛选条五控件、文件栏/控制栏主轴长度）
 *   FilmstripGeometry.cs（96×144 卡片、内边距、首项居中、可见范围、350ms 三次缓出）
 *   ControlBarLayout.cs（EXIF/评分/命令三分组的尺寸、换行、行内居中）
 *   SimilarityPanelGeometry.cs / AnalysisPanelGeometry.cs
 *   PhotoViewport.NavigationSize（导航图长边）
 *
 * 浏览器与 Node 共用这一个文件：file:// 下浏览器禁止 ES module 跨文件加载，
 * 因此不用静态 export，而是挂到 globalThis.PVGeometry；Node 以 CommonJS 读取。
 * 输入只读令牌对象，输出与 macOS 验收同名的节点矩形 {名称: [x, y, w, h]}（左上角原点，单位点）。
 */
(function (root, factory) {
    "use strict";
    const api = factory();
    root.PVGeometry = api;
    if (typeof module === "object" && module.exports) module.exports = api;
})(typeof globalThis !== "undefined" ? globalThis : this, function () {
    "use strict";

    /** 样机与 Mac 验收同配置时的命令列表（控制栏命令分组的默认六项）。 */
    const DEFAULT_COMMANDS = ["Previous", "Next", "Exchange", "Fit", "ZoomInPreset", "ZoomOutPreset"];

    /**
     * FILTER-04 / FILTER-07：计数按自然文本宽度占位。
     * 52 是 Mac 验收素材（12 张、「共 12 张」、12 点系统字体）的量得宽度，作为样机与 Node 导出的确定性基准；
     * 浏览器实际渲染仍按同一宽度占位，保证几何与 Mac 参考一致。
     */
    const REFERENCE_COUNT_WIDTH = 52;

    function token(tokens, name) {
        if (!tokens || tokens[name] === undefined) throw new Error("缺少设计令牌：" + name);
        return tokens[name];
    }

    function clamp(value, low, high) {
        return Math.min(Math.max(value, low), high);
    }

    function rect(x, y, width, height) {
        return [x, y, width, height];
    }

    // ---------------------------------------------------------------------
    // MainScreenLayout
    // ---------------------------------------------------------------------

    /** 胶片栏带宽：卡片高 + 顶内边距 + 底内边距（FILM-04，等于 film.band.height）。 */
    function filmstripBand(tokens) {
        return token(tokens, "film.card.height") + token(tokens, "film.list.padTop") + token(tokens, "film.list.padBottom");
    }

    /** 筛选条主轴长度：行布局为控件高 + 上下外边距；列布局为前导让位 + 三控件 + 计数 + toggle（SHELL-01/SHELL-02）。 */
    function filterBarMainLength(tokens, horizontal, leadingInset) {
        const controlHeight = token(tokens, "filter.control.height");
        const gap = token(tokens, "filter.gap");
        const margin = token(tokens, "filter.margin");
        const countHeight = token(tokens, "filter.count.height");
        const toggleHeight = token(tokens, "filter.toggle.height");
        return horizontal
            ? controlHeight + margin * 2
            : leadingInset + controlHeight * 3 + gap * 3 + countHeight + gap + toggleHeight + margin;
    }

    /** 文件栏主轴长度（含分隔线）：行布局为筛选条 + 胶片带 + 分隔线；列布局为内容宽 + 分隔线 + 附加交叉轴（SHELL-01）。 */
    function fileBarMainLength(tokens, horizontal, filterBarMain, extraCross) {
        return horizontal
            ? filterBarMain + filmstripBand(tokens) + token(tokens, "shell.divider.thickness")
            : token(tokens, "shell.filebar.content.width") + token(tokens, "shell.divider.thickness") + extraCross;
    }

    /** 控制栏主轴长度（含分隔线）。 */
    function controlBarMainLength(tokens, measuredMain) {
        return measuredMain + token(tokens, "shell.divider.thickness");
    }

    /**
     * 筛选条五个控件（FILTER-07）：行布局默认整组横向居中（macOS 交通灯让位），alignLeading 时从前导 +10 起排；
     * 列布局固定在 116 栏内纵向排列，计数与 toggle 与控件垂直居中。
     */
    function filterBarFrames(tokens, options) {
        const horizontal = !!options.horizontal;
        const contentWidth = options.contentWidth;
        const leadingInset = options.leadingInset || 0;
        const countWidth = options.countWidth;
        const includeSimilarityToggle = options.includeSimilarityToggle !== false;
        const alignLeading = !!options.alignLeading;
        const controlWidth = options.controlWidth !== undefined ? options.controlWidth : token(tokens, "filter.control.width");
        const toggleWidth = options.toggleWidth !== undefined ? options.toggleWidth : token(tokens, "filter.toggle.width");
        const controlHeight = token(tokens, "filter.control.height");
        const gap = token(tokens, "filter.gap");
        const margin = token(tokens, "filter.margin");
        const countHeight = token(tokens, "filter.count.height");
        const toggleHeight = token(tokens, "filter.toggle.height");
        if (horizontal) {
            const controlsWidth = controlWidth * 3 + gap * 2 + gap + countWidth;
            const used = includeSimilarityToggle ? controlsWidth + gap + toggleWidth : controlsWidth;
            const start = alignLeading ? margin : (contentWidth - used) / 2;
            const controlY = margin;
            const countX = start + (controlWidth + gap) * 3;
            const toggleX = countX + countWidth + gap;
            return {
                sort: rect(start, controlY, controlWidth, controlHeight),
                order: rect(start + controlWidth + gap, controlY, controlWidth, controlHeight),
                rating: rect(start + (controlWidth + gap) * 2, controlY, controlWidth, controlHeight),
                count: rect(countX, controlY + (controlHeight - countHeight) / 2, countWidth, countHeight),
                toggle: rect(toggleX, controlY + (controlHeight - toggleHeight) / 2, toggleWidth, toggleHeight)
            };
        }
        // 列布局的 toggle 始终紧随计数下方，与是否参与整组居中无关。
        const content = token(tokens, "shell.filebar.content.width");
        const left = (content - controlWidth) / 2;
        const top = leadingInset;
        const countY = top + (controlHeight + gap) * 3;
        return {
            sort: rect(left, top, controlWidth, controlHeight),
            order: rect(left, top + controlHeight + gap, controlWidth, controlHeight),
            rating: rect(left, top + (controlHeight + gap) * 2, controlWidth, controlHeight),
            count: rect((content - countWidth) / 2, countY, countWidth, countHeight),
            toggle: rect(left, countY + countHeight + gap, toggleWidth, toggleHeight)
        };
    }

    /**
     * 两向分区（SHELL-01）：文件栏与控制栏贴主轴两端，主图取剩余空间，分析栏夹在主图与相邻栏之间。
     * 长度为 0 表示该栏隐藏；隐藏栏的分隔线按零宽落在边界上（验收只记录可见态）。
     */
    function partition(tokens, options) {
        const horizontal = !!options.horizontal;
        const width = options.width;
        const height = options.height;
        const file = Math.max(0, options.fileMain);
        const control = Math.max(0, options.controlMain);
        const analysis = Math.max(0, options.analysisCross);
        const divider = token(tokens, "shell.divider.thickness");
        if (horizontal) {
            const photoHeight = Math.max(1, height - file - analysis - control);
            return {
                fileBar: rect(0, 0, width, Math.max(0, file - divider)),
                fileDivider: rect(0, file >= divider ? file - divider : 0, width, file >= divider ? divider : 0),
                photo: rect(0, file, width, photoHeight),
                analysis: rect(0, file + photoHeight, width, analysis),
                controlDivider: rect(0, control >= divider ? height - control : height, width, control >= divider ? divider : 0),
                controlBar: rect(0, control >= divider ? height - control + divider : height, width, Math.max(0, control - divider))
            };
        }
        const photoWidth = Math.max(1, width - file - analysis - control);
        return {
            fileBar: rect(0, 0, Math.max(0, file - divider), height),
            fileDivider: rect(file >= divider ? file - divider : 0, 0, file >= divider ? divider : 0, height),
            photo: rect(file, 0, photoWidth, height),
            analysis: rect(file + photoWidth, 0, analysis, height),
            controlDivider: rect(control >= divider ? width - control : width, 0, control >= divider ? divider : 0, height),
            controlBar: rect(control >= divider ? width - control + divider : width, 0, Math.max(0, control - divider), height)
        };
    }

    // ---------------------------------------------------------------------
    // ControlBarLayout
    // ---------------------------------------------------------------------

    /** 分组内子控件指标：EXIF 每块两端各留一次内边距，评分组只留两端，命令组不留内边距（TOOLBAR-02）。 */
    function controlMetrics(tokens, kind) {
        if (kind === "exif") {
            return {
                inset: token(tokens, "toolbar.exif.inset"),
                spacing: token(tokens, "toolbar.exif.spacing"),
                width: token(tokens, "toolbar.exif.block.width"),
                height: token(tokens, "toolbar.exif.block.height")
            };
        }
        if (kind === "rating") {
            const size = token(tokens, "toolbar.rating.button.size");
            return { inset: token(tokens, "toolbar.rating.group.inset"), spacing: 0, width: size, height: size };
        }
        const size = token(tokens, "toolbar.command.button.size");
        return { inset: 0, spacing: token(tokens, "toolbar.command.spacing"), width: size, height: size };
    }

    /** 按 TOOLBAR-01 顺序生成三分组；空分组整组移除。 */
    function controlSpecs(scenario) {
        const specs = [];
        const exifItems = Math.max(0, scenario.exifItems | 0);
        if (exifItems > 0) specs.push({ kind: "exif", count: exifItems });
        if (scenario.showRating) specs.push({ kind: "rating", count: 6 });
        const commandCount = Array.isArray(scenario.commands)
            ? scenario.commands.length
            : Math.max(0, scenario.commands | 0);
        if (commandCount > 0) specs.push({ kind: "command", count: commandCount });
        return specs;
    }

    /** 分组主轴长度（含分组自带内边距）。 */
    function controlMainLength(tokens, group, horizontal) {
        const metrics = controlMetrics(tokens, group.kind);
        const childMain = horizontal ? metrics.width : metrics.height;
        const count = Math.max(0, group.count);
        return count * childMain + Math.max(0, count - 1) * metrics.spacing
            + metrics.inset * 2 * (group.kind === "exif" ? count : 1);
    }

    /** 分组自身交叉轴长度（未考虑行内拉伸）。 */
    function controlCrossLength(tokens, group, horizontal) {
        const metrics = controlMetrics(tokens, group.kind);
        return horizontal ? metrics.height : metrics.width;
    }

    /** 整组沿主轴排布，空间不足时换行/换列；单个超长分组保持整体不被拆开（TOOLBAR-08）。 */
    function controlPackLines(tokens, groups, horizontal, available) {
        const lines = [];
        for (let index = 0; index < groups.length; index++) {
            if (lines.length === 0
                || (lines[lines.length - 1].length > 0
                    && controlLineLength(tokens, groups, horizontal, lines[lines.length - 1]) + token(tokens, "toolbar.group.spacing")
                        + controlMainLength(tokens, groups[index], horizontal) > available)) {
                lines.push([]);
            }
            lines[lines.length - 1].push(index);
        }
        return lines;
    }

    /** 一行/列的主轴总长度，组间空隙不计入最后一项。 */
    function controlLineLength(tokens, groups, horizontal, line) {
        let total = 0;
        for (const index of line) total += controlMainLength(tokens, groups[index], horizontal);
        return total + Math.max(0, line.length - 1) * token(tokens, "toolbar.group.spacing");
    }

    /** 一行内最高的分组交叉轴长度。 */
    function controlLineCross(tokens, groups, horizontal, line) {
        let cross = 0;
        for (const index of line) cross = Math.max(cross, controlCrossLength(tokens, groups[index], horizontal));
        return cross;
    }

    /** 各行交叉轴长度之和加行间距（不含四周外边距）。 */
    function controlCrossTotal(tokens, groups, horizontal, lines) {
        let total = 0;
        for (const line of lines) total += controlLineCross(tokens, groups, horizontal, line);
        return total + Math.max(0, lines.length - 1) * token(tokens, "toolbar.group.spacing");
    }

    /** 包含四周外边距的文档尺寸；空文档保留纯外边距（TOOLBAR-09）。 */
    function controlDocumentSize(tokens, groups, horizontal, lines) {
        let main = 0;
        for (const line of lines) main = Math.max(main, controlLineLength(tokens, groups, horizontal, line));
        const cross = controlCrossTotal(tokens, groups, horizontal, lines);
        const padding = token(tokens, "toolbar.padding");
        return horizontal ? [main + padding * 2, cross + padding * 2] : [cross + padding * 2, main + padding * 2];
    }

    /** 子控件相对分组原点的位置（主轴与交叉轴分量）。评分组列布局自上而下 5→0（TOOLBAR-03）。 */
    function controlChildOffsets(tokens, group, horizontal, crossSize) {
        const metrics = controlMetrics(tokens, group.kind);
        const childMain = horizontal ? metrics.width : metrics.height;
        const childCross = horizontal ? metrics.height : metrics.width;
        const cross = Math.max(childCross, crossSize);
        const offsets = [];
        for (let index = 0; index < Math.max(0, group.count); index++) {
            const position = group.kind === "rating" && !horizontal ? group.count - index - 1 : index;
            const main = metrics.inset + position
                * (childMain + metrics.spacing + (group.kind === "exif" ? metrics.inset * 2 : 0));
            offsets.push({ main, cross: (cross - childCross) / 2 });
        }
        return offsets;
    }

    /** 在裁剪视口内居中每一行/列；文档尺寸取视口与量得尺寸的较大者（TOOLBAR-02/TOOLBAR-08）。 */
    function layoutControlBar(tokens, groups, options) {
        const horizontal = !!options.horizontal;
        const viewportWidth = options.viewportWidth;
        const viewportHeight = options.viewportHeight;
        const availableMain = horizontal ? viewportWidth : viewportHeight;
        const padding = token(tokens, "toolbar.padding");
        const lines = controlPackLines(tokens, groups, horizontal, Math.max(1, availableMain - padding * 2));
        const measured = controlDocumentSize(tokens, groups, horizontal, lines);
        const documentWidth = Math.max(viewportWidth, measured[0]);
        const documentHeight = Math.max(viewportHeight, measured[1]);
        const arranged = [];
        let cross = padding;
        for (const line of lines) {
            const lineCross = controlLineCross(tokens, groups, horizontal, line);
            let main = Math.max(padding, (availableMain - controlLineLength(tokens, groups, horizontal, line)) / 2);
            for (const index of line) {
                const group = groups[index];
                const metrics = controlMetrics(tokens, group.kind);
                const groupMain = controlMainLength(tokens, group, horizontal);
                const groupCross = Math.max(controlCrossLength(tokens, group, horizontal), lineCross);
                const frame = horizontal
                    ? rect(main, cross, groupMain, groupCross)
                    : rect(cross, main, groupCross, groupMain);
                const children = controlChildOffsets(tokens, group, horizontal, lineCross).map(offset => horizontal
                    ? rect(offset.main, offset.cross, metrics.width, metrics.height)
                    : rect(offset.cross, offset.main, metrics.width, metrics.height));
                arranged.push({ kind: group.kind, count: group.count, frame, children });
                main += groupMain + token(tokens, "toolbar.group.spacing");
            }
            cross += lineCross + token(tokens, "toolbar.group.spacing");
        }
        return {
            document: rect(0, 0, documentWidth, documentHeight),
            measured,
            lines,
            groups: arranged,
            availableMain
        };
    }

    /** 只量测文档（用于决定控制栏主轴长度），不依赖最终视口。 */
    function measureControlBar(tokens, groups, horizontal, width, height) {
        const availableMain = horizontal ? width : height;
        const padding = token(tokens, "toolbar.padding");
        const lines = controlPackLines(tokens, groups, horizontal, Math.max(1, availableMain - padding * 2));
        const size = controlDocumentSize(tokens, groups, horizontal, lines);
        return { width: size[0], height: size[1], lines };
    }

    // ---------------------------------------------------------------------
    // FilmstripGeometry / FilmstripAnimation
    // ---------------------------------------------------------------------

    function filmstripStep(tokens, horizontal) {
        return (horizontal ? token(tokens, "film.card.width") : token(tokens, "film.card.height"))
            + token(tokens, "film.card.spacing");
    }

    /** 首项沿主轴起点：横向在内容宽度内居中（至少保留内边距），纵向固定顶边内边距（FILM-04）。 */
    function filmstripItemStart(tokens, horizontal, contentWidth, count) {
        if (!horizontal) return token(tokens, "film.list.padTop");
        const step = filmstripStep(tokens, true);
        return Math.max(token(tokens, "film.list.padLeft"),
            (contentWidth - count * step + token(tokens, "film.card.spacing")) / 2);
    }

    /** 文档尺寸：只在主轴延伸，空列表与短列表仍至少覆盖裁剪视口。 */
    function filmstripContentSize(tokens, horizontal, viewportWidth, viewportHeight, count) {
        const step = filmstripStep(tokens, horizontal);
        const spacing = token(tokens, "film.card.spacing");
        const leading = horizontal ? token(tokens, "film.list.padLeft") : token(tokens, "film.list.padTop");
        const trailing = horizontal ? token(tokens, "film.list.padRight") : token(tokens, "film.list.padBottom");
        const length = count === 0 ? 0 : count * step - spacing + leading + trailing;
        return horizontal
            ? [Math.max(viewportWidth, length), viewportHeight]
            : [viewportWidth, Math.max(viewportHeight, length)];
    }

    /** 单个卡片在文档中的矩形。 */
    function filmstripItemFrame(tokens, horizontal, index, count, contentWidth) {
        const width = token(tokens, "film.card.width");
        const height = token(tokens, "film.card.height");
        const top = token(tokens, "film.list.padTop");
        if (horizontal) {
            return rect(filmstripItemStart(tokens, true, contentWidth, count) + index * filmstripStep(tokens, true),
                top, width, height);
        }
        return rect(Math.max(0, (contentWidth - width) / 2), top + index * filmstripStep(tokens, false), width, height);
    }

    /** 仅与可见矩形相交的项目范围。 */
    function filmstripVisibleRange(count, itemStart, step, rectStart, rectLength) {
        if (count <= 0) return { first: 0, last: -1 };
        const end = rectStart + rectLength;
        const first = Math.max(0, Math.floor((rectStart - itemStart) / step));
        const last = Math.min(count - 1, Math.floor((end - itemStart) / step));
        return { first, last };
    }

    /** FILM-02：把某一项中心移到视口中心所需的主轴偏移，夹到合法范围。 */
    function filmstripCenterOffset(itemStart, itemLength, viewportLength, documentLength) {
        return clamp(itemStart + (itemLength - viewportLength) / 2, 0, Math.max(0, documentLength - viewportLength));
    }

    /** FILM-02：350ms 三次缓出。 */
    function filmstripEaseOutCubic(progress) {
        return 1 - Math.pow(1 - progress, 3);
    }

    function filmstripInterpolate(start, end, progress) {
        return start + (end - start) * filmstripEaseOutCubic(progress);
    }

    /** FILM-02：位移小于 motion.film.scroll.negligibleThreshold（默认 1 点）时直接定位。 */
    function filmstripIsNegligible(startX, startY, endX, endY, threshold) {
        const limit = threshold === undefined ? 1 : threshold;
        return Math.abs(endX - startX) < limit && Math.abs(endY - startY) < limit;
    }

    // ---------------------------------------------------------------------
    // SimilarityPanelGeometry
    // ---------------------------------------------------------------------

    /** 相似面板几何：列布局固定宽 110，行布局按卡片数切出、封顶可用宽度一半（SIM-07）。 */
    function similarityGeometry(tokens, options) {
        const horizontal = !!options.horizontal;
        const minWidth = token(tokens, "sim.panel.minWidth");
        const minHeight = token(tokens, "sim.panel.minHeight");
        const margin = token(tokens, "sim.panel.margin");
        const border = token(tokens, "sim.panel.border.width");
        const listPadding = token(tokens, "sim.list.padding");
        const cardWidth = token(tokens, "film.card.width");
        const panelWidth = cardWidth + 4 + listPadding + border * 2;
        const listCross = token(tokens, "film.card.height") + token(tokens, "film.list.padTop")
            + token(tokens, "film.list.padBottom") + border * 2;
        const availableCross = Math.max(0, options.availableCross || 0);
        if (!horizontal) {
            const needed = Math.max(minWidth, panelWidth);
            const crossSize = availableCross > 0 ? Math.min(needed, availableCross) : needed;
            return { crossSize, rowWidth: 0, panelWidth, listCross, margin };
        }
        const hasItems = !!options.hasItems;
        const itemCount = Math.max(0, options.itemCount | 0);
        const content = hasItems
            ? itemCount * cardWidth + listPadding + border * 2
            : minWidth;
        const rowWidth = Math.min(Math.max(minWidth, content), Math.max(1, options.paneWidth) / 2) + margin;
        const needed = Math.max(minHeight, hasItems ? listCross : (options.emptyHeight || minHeight));
        const crossSize = availableCross > 0 ? Math.min(needed, availableCross) : needed;
        return { crossSize, rowWidth, panelWidth, listCross, margin };
    }

    // ---------------------------------------------------------------------
    // AnalysisPanelGeometry
    // ---------------------------------------------------------------------

    function analysisTileSize(tokens, availableMain, count) {
        const n = Math.max(1, count | 0);
        const padding = token(tokens, "analysis.panel.padding");
        const spacing = token(tokens, "analysis.tile.spacing");
        const usable = Math.max(0, availableMain - padding * 2 - spacing * (n - 1));
        return Math.max(1, Math.floor(usable / n));
    }

    function analysisCrossSize(tokens, availableMain, count) {
        return analysisTileSize(tokens, availableMain, count) + token(tokens, "analysis.panel.padding") * 2;
    }

    function analysisTileOffset(tokens, index, tileSize) {
        return token(tokens, "analysis.panel.padding") + index * (tileSize + token(tokens, "analysis.tile.spacing"));
    }

    /** 分析栏瓦片（ANALYSIS-03）：主轴从内边距 10 起排，间距 8，交叉轴 = 边长 + 20。 */
    function analysisGeometry(tokens, options) {
        const horizontal = !!options.horizontal;
        const availableMain = options.availableMain;
        const count = Math.max(1, options.count | 0);
        const tile = analysisTileSize(tokens, availableMain, count);
        const padding = token(tokens, "analysis.panel.padding");
        const tiles = [];
        for (let index = 0; index < count; index++) {
            const offset = analysisTileOffset(tokens, index, tile);
            tiles.push(horizontal ? rect(offset, padding, tile, tile) : rect(padding, offset, tile, tile));
        }
        return { tileSize: tile, crossSize: tile + padding * 2, tiles };
    }

    // ---------------------------------------------------------------------
    // 导航图与主图内叠加
    // ---------------------------------------------------------------------

    /** IMAGE-06：导航图长边 150、右下角距边 12，宿主 = 缩略图 + 内边距 + 百分比行。 */
    function navigationGeometry(tokens, options) {
        if (!options.visible || options.isFit) return null;
        const imageWidth = options.imageWidth;
        const imageHeight = options.imageHeight;
        if (!(imageWidth > 0) || !(imageHeight > 0)) return null;
        const longEdge = token(tokens, "nav.longEdge");
        const factor = longEdge / Math.max(imageWidth, imageHeight);
        const width = imageWidth * factor;
        const height = imageHeight * factor;
        const padding = token(tokens, "nav.padding");
        const topGap = token(tokens, "nav.percent.topGap");
        const labelHeight = token(tokens, "nav.label.height");
        const bottomGap = token(tokens, "nav.percent.bottomGap");
        const hostWidth = width + padding * 2;
        const hostHeight = height + padding * 2 + topGap + labelHeight + bottomGap;
        const hostX = Math.max(0, options.photoWidth - hostWidth - token(tokens, "nav.margin"));
        const hostY = Math.max(0, options.photoHeight - hostHeight - token(tokens, "nav.margin"));
        // 可见框按当前倍率收缩；样机不模拟平移，故取居中框（IMAGE-06 只要求与视口比例一致）。
        const zoom = Math.max(1, Number(options.zoomFactor) || 1);
        const visibleWidth = width / zoom;
        const visibleHeight = height / zoom;
        return {
            host: rect(hostX, hostY, hostWidth, hostHeight),
            image: rect(padding, padding, width, height),
            frame: rect(padding + (width - visibleWidth) / 2, padding + (height - visibleHeight) / 2,
                visibleWidth, visibleHeight),
            label: rect(padding, padding + height + topGap, width, labelHeight),
            size: [width, height]
        };
    }

    /** IMAGE-07：主图底部「计算中…」药丸。 */
    function busyPillGeometry(tokens, photoWidth, photoHeight) {
        const width = token(tokens, "image.busy.width");
        const height = token(tokens, "image.busy.height");
        return rect(Math.max(0, (photoWidth - width) / 2),
            Math.max(0, photoHeight - token(tokens, "image.busy.bottomOffset") - height), width, height);
    }

    /** STATE-03：主图底部提示条，水平居中、距底 24，最大宽 500。 */
    function toastGeometry(tokens, photoWidth, photoHeight, textHeight) {
        const width = Math.min(token(tokens, "status.toast.maxWidth"), Math.max(1, photoWidth));
        const height = Math.max(1, textHeight) + token(tokens, "status.toast.paddingY") * 2;
        return rect((photoWidth - width) / 2,
            Math.max(0, photoHeight - token(tokens, "status.toast.bottomMargin") - height), width, height);
    }

    // ---------------------------------------------------------------------
    // 卡片内部（FILM-05）
    // ---------------------------------------------------------------------

    /**
     * 卡片内层几何：surface 相对卡片根，图片/文字/星级/抖动徽标相对 surface。
     * 令牌覆盖缩略图、文件名宽、第二行 y、星级尺寸与徽标尺寸；行内偏移由字号推导（行高 = 字号 + 2，底部留 8）。
     */
    function cardNodes(tokens, cardFrame, options) {
        const surfaceWidth = token(tokens, "film.card.surface.width");
        const surfaceHeight = token(tokens, "film.card.surface.height");
        const inset = token(tokens, "film.card.inset");
        const thumb = token(tokens, "film.thumb.size");
        const nameWidth = token(tokens, "film.card.name.width");
        const secondWidth = token(tokens, "film.card.secondLine.width");
        const nameFont = token(tokens, "film.card.name.fontSize");
        const secondFont = token(tokens, "film.card.secondLine.fontSize");
        const starSize = token(tokens, "film.card.star.size");
        const badgeSize = token(tokens, "film.card.badge.size");
        const imageX = (surfaceWidth - thumb) / 2;
        const imageY = (surfaceWidth - thumb) / 2;
        const starY = surfaceHeight - 8 - starSize;
        const secondHeight = secondFont + 2;
        const secondY = token(tokens, "film.card.secondLine.y");
        const nameHeight = nameFont + 2;
        const nameY = secondY - 2 - nameHeight;
        const nodes = {
            root: cardFrame,
            surface: rect(inset, inset, surfaceWidth, surfaceHeight),
            image: rect(imageX, imageY, thumb, thumb),
            name: rect((surfaceWidth - nameWidth) / 2, nameY, nameWidth, nameHeight),
            date: rect((surfaceWidth - secondWidth) / 2, secondY, secondWidth, secondHeight),
            shake: rect(imageX + thumb - badgeSize - 2, imageY + thumb - badgeSize - 2, badgeSize, badgeSize)
        };
        for (let rating = 0; rating < 6; rating++) {
            nodes["star" + rating] = rect(inset + rating * starSize, starY, starSize, starSize);
        }
        return nodes;
    }

    // ---------------------------------------------------------------------
    // 场景与总装
    // ---------------------------------------------------------------------

    function resolveLayout(scenario) {
        const mode = scenario.layout || "auto";
        if (mode === "auto") return scenario.width > scenario.height ? "col" : "row";
        return mode === "row" ? "row" : "col";
    }

    /** 场景默认值与清洗；photoCount/currentIndex 保持列表与导出一致。 */
    function normalizeScenario(tokens, scenario) {
        const defaults = {
            layout: "auto",
            width: token(tokens, "shell.window.default.width"),
            height: token(tokens, "shell.window.default.height"),
            fileBarVisible: true,
            controlVisible: true,
            photoCount: 12,
            currentIndex: 0,
            exifItems: 4,
            commands: DEFAULT_COMMANDS.slice(),
            showRating: true,
            similarityOpen: false,
            similarityItems: 0,
            similarityHasItems: false,
            analysisVisible: false,
            analysisTileCount: 6,
            macAppearance: true,
            countWidth: REFERENCE_COUNT_WIDTH,
            zoomed: false,
            zoomFactor: 1.6,
            showZoomIndicator: true,
            imageWidth: 6000,
            imageHeight: 4000,
            toastVisible: false,
            toastTextHeight: 17,
            busyVisible: false
        };
        const merged = Object.assign(defaults, scenario || {});
        merged.width = Math.max(1, Number(merged.width));
        merged.height = Math.max(1, Number(merged.height));
        merged.photoCount = Math.max(0, merged.photoCount | 0);
        merged.currentIndex = merged.photoCount === 0
            ? 0
            : clamp(merged.currentIndex | 0, 0, merged.photoCount - 1);
        merged.exifItems = Math.max(0, merged.exifItems | 0);
        merged.analysisTileCount = Math.max(1, merged.analysisTileCount | 0);
        merged.similarityItems = Math.max(0, merged.similarityItems | 0);
        merged.similarityHasItems = !!merged.similarityHasItems || merged.similarityItems > 0;
        return merged;
    }

    /**
     * 计算一个场景的全部节点矩形。
     * @param {object} tokens Design/prototype/tokens.js 的扁平令牌表。
     * @param {object} scenario 场景（见 normalizeScenario 默认值）。
     * @returns {{layout: string, horizontal: boolean, scene: object, nodes: object, details: object}}
     */
    function computeGeometry(tokens, scenario) {
        const scene = normalizeScenario(tokens, scenario);
        const layout = resolveLayout(scene);
        const horizontal = layout === "row";
        const width = scene.width;
        const height = scene.height;
        const divider = token(tokens, "shell.divider.thickness");
        // SHELL-02/FILTER-07：列布局首控件让位 34 给 macOS 交通灯；行布局让位由整组居中自然实现。
        const leadingInset = horizontal ? 0 : token(tokens, "shell.filter.leadingInset");
        const filterMain = filterBarMainLength(tokens, horizontal, leadingInset);

        const similarityOpen = scene.fileBarVisible && scene.similarityOpen;
        const similarity = similarityOpen
            ? similarityGeometry(tokens, {
                horizontal,
                paneWidth: width,
                hasItems: scene.similarityHasItems,
                itemCount: scene.similarityItems,
                availableCross: horizontal ? filmstripBand(tokens) : width
            })
            : null;
        const similarityColumn = similarityOpen
            ? (horizontal ? similarity.rowWidth : similarity.crossSize + token(tokens, "sim.panel.margin"))
            : 0;

        const fileMain = scene.fileBarVisible
            ? fileBarMainLength(tokens, horizontal, filterMain, similarityColumn)
            : 0;

        const specs = controlSpecs(scene);
        const controlMeasure = measureControlBar(tokens, specs, horizontal, width, height);
        const controlMain = scene.controlVisible
            ? controlBarMainLength(tokens, horizontal ? controlMeasure.height : controlMeasure.width)
            : 0;

        const analysisVisible = !!scene.analysisVisible;
        const analysisMain = horizontal ? width : height;
        const analysisCross = analysisVisible
            ? analysisCrossSize(tokens, analysisMain, scene.analysisTileCount)
            : 0;

        const panes = partition(tokens, {
            horizontal, width, height, fileMain, controlMain, analysisCross
        });

        const filter = filterBarFrames(tokens, {
            horizontal,
            contentWidth: horizontal ? width : token(tokens, "shell.filebar.content.width"),
            leadingInset,
            countWidth: scene.countWidth,
            includeSimilarityToggle: true,
            // SHELL-02：macOS 行布局整组居中，避免交通灯；无窗口装饰的平台从内容前导边 +10 起排。
            alignLeading: horizontal && !scene.macAppearance
        });

        const control = layoutControlBar(tokens, specs, {
            horizontal,
            viewportWidth: panes.controlBar[2],
            viewportHeight: panes.controlBar[3]
        });

        const nodes = {
            root: rect(0, 0, width, height),
            "root.filePane": panes.fileBar,
            "root.photoPane": panes.photo,
            "root.controlScroll": panes.controlBar,
            "root.fileDivider": panes.fileDivider,
            "root.controlDivider": panes.controlDivider,
            photoViewport: panes.photo.slice()
        };

        // 拖动区与零宽分析栏：macOS 外观下按 Mac 验收的节点命名输出（SHELL-02）。
        if (scene.macAppearance) {
            const dragHeight = horizontal
                ? token(tokens, "shell.dragzone.row.height")
                : token(tokens, "shell.dragzone.column.height");
            const dragStart = horizontal && scene.fileBarVisible ? filter.sort[0] : width;
            const dragEnd = horizontal && scene.fileBarVisible ? filter.toggle[0] + filter.toggle[2] : width;
            nodes["root.overlay5"] = rect(0, 0, dragStart, dragHeight);
            nodes["root.overlay6"] = rect(dragEnd, 0, Math.max(0, width - dragEnd), dragHeight);
            nodes["root.overlay7"] = panes.analysis.slice();
        }

        nodes.controlClip = rect(0, 0, panes.controlBar[2], panes.controlBar[3]);
        nodes.controlDocument = control.document;
        control.groups.forEach((group, index) => {
            nodes["control.group" + index] = group.frame;
            group.children.forEach((child, childIndex) => {
                nodes["control.group" + index + ".child" + childIndex] = child;
            });
        });

        nodes["filter.sort"] = filter.sort;
        nodes["filter.order"] = filter.order;
        nodes["filter.rating"] = filter.rating;
        nodes["filter.count"] = filter.count;
        nodes["filter.toggle"] = filter.toggle;

        // 文件栏隐藏时不再生成胶片栏节点（SHELL-01：隐藏即空间全部还给主图）。
        let filmstrip = null;
        let cards = [];
        if (scene.fileBarVisible) {
            const scrollFrame = horizontal
                ? rect(0, filterMain, Math.max(1, width - similarityColumn), filmstripBand(tokens))
                : rect(0, filterMain, token(tokens, "shell.filebar.content.width"),
                    Math.max(1, height - filterMain));
            const documentSize = filmstripContentSize(tokens, horizontal, scrollFrame[2], scrollFrame[3], scene.photoCount);
            const itemStart = filmstripItemStart(tokens, horizontal, documentSize[0], scene.photoCount);
            const step = filmstripStep(tokens, horizontal);
            const viewportMain = horizontal ? scrollFrame[2] : scrollFrame[3];
            const documentMain = horizontal ? documentSize[0] : documentSize[1];
            const maxOffset = Math.max(0, documentMain - viewportMain);
            nodes.filmstripClip = rect(0, 0, scrollFrame[2], scrollFrame[3]);
            nodes.filmstripDocument = rect(0, 0, documentSize[0], documentSize[1]);
            nodes.filmstripScroll = scrollFrame;
            for (let index = 0; index < scene.photoCount; index++) {
                const frame = filmstripItemFrame(tokens, horizontal, index, scene.photoCount, documentSize[0]);
                const card = cardNodes(tokens, frame, scene);
                Object.keys(card).forEach(name => {
                    nodes["card" + index + "." + name] = card[name];
                });
                cards.push({ index, frame, rating: index === 0 ? 3 : (index % 4), current: index === scene.currentIndex });
            }
            filmstrip = {
                horizontal,
                scroll: scrollFrame,
                document: documentSize,
                itemStart,
                step,
                maxOffset,
                visible: filmstripVisibleRange(scene.photoCount, itemStart, step, 0, viewportMain),
                cards
            };
        }

        let analysis = null;
        if (analysisVisible) {
            analysis = analysisGeometry(tokens, {
                horizontal,
                availableMain: analysisMain,
                count: scene.analysisTileCount
            });
            analysis.panel = panes.analysis;
            analysis.divider = horizontal
                ? rect(0, 0, panes.analysis[2], token(tokens, "shell.analysis.divider.thickness"))
                : rect(0, 0, token(tokens, "shell.analysis.divider.thickness"), panes.analysis[3]);
            analysis.tiles.forEach((tile, index) => {
                nodes["analysis.tile" + index] = tile;
            });
            nodes["analysis.divider"] = analysis.divider;
        }

        let similarPanel = null;
        if (similarityOpen) {
            const panelFrame = horizontal
                ? rect(width - similarityColumn, filterMain, Math.max(1, similarityColumn - similarity.margin),
                    similarity.crossSize)
                : rect(token(tokens, "shell.filebar.content.width"), filterMain, similarity.crossSize,
                    Math.max(1, height - filterMain));
            nodes["sim.panel"] = panelFrame;
            similarPanel = {
                frame: panelFrame,
                horizontal,
                hasItems: scene.similarityHasItems,
                crossSize: similarity.crossSize,
                itemCount: scene.similarityItems
            };
        }

        if (scene.toastVisible) {
            nodes["photo.toast"] = toastGeometry(tokens, panes.photo[2], panes.photo[3], scene.toastTextHeight);
        }
        if (scene.busyVisible) {
            nodes["photo.busy"] = busyPillGeometry(tokens, panes.photo[2], panes.photo[3]);
        }
        if (scene.showZoomIndicator) {
            const navigation = navigationGeometry(tokens, {
                visible: scene.zoomed,
                isFit: !scene.zoomed,
                imageWidth: scene.imageWidth,
                imageHeight: scene.imageHeight,
                photoWidth: panes.photo[2],
                photoHeight: panes.photo[3],
                zoomFactor: scene.zoomFactor
            });
            if (navigation) {
                nodes["nav.host"] = navigation.host;
                nodes["nav.image"] = navigation.image;
                nodes["nav.frame"] = navigation.frame;
                nodes["nav.label"] = navigation.label;
            }
        }

        return {
            layout,
            horizontal,
            scene,
            nodes,
            details: {
                panes,
                filter,
                filterMain,
                similarity,
                similarityColumn,
                control,
                filmstrip,
                analysis,
                similarPanel
            }
        };
    }

    return {
        DEFAULT_COMMANDS,
        REFERENCE_COUNT_WIDTH,
        token,
        rect,
        // MainScreenLayout
        filmstripBand,
        filterBarMainLength,
        fileBarMainLength,
        controlBarMainLength,
        filterBarFrames,
        partition,
        // ControlBarLayout
        controlMetrics,
        controlSpecs,
        controlMainLength,
        controlCrossLength,
        controlPackLines,
        controlLineLength,
        controlLineCross,
        controlCrossTotal,
        controlDocumentSize,
        controlChildOffsets,
        layoutControlBar,
        measureControlBar,
        // FilmstripGeometry / FilmstripAnimation
        filmstripStep,
        filmstripItemStart,
        filmstripContentSize,
        filmstripItemFrame,
        filmstripVisibleRange,
        filmstripCenterOffset,
        filmstripEaseOutCubic,
        filmstripInterpolate,
        filmstripIsNegligible,
        // Similarity / Analysis
        similarityGeometry,
        analysisTileSize,
        analysisCrossSize,
        analysisTileOffset,
        analysisGeometry,
        // 主图内叠加
        navigationGeometry,
        busyPillGeometry,
        toastGeometry,
        cardNodes,
        // 总装
        resolveLayout,
        normalizeScenario,
        computeGeometry
    };
});
