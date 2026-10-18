/*
 * 主窗口样机驱动：把 geometry.js 的节点矩形落成 DOM 位置，并驱动胶片栏定位动画与几何导出。
 * 控制面板不属于窗口本体；所有视觉数值经 tokens.js 注入的 CSS 变量取得。
 */
(function () {
    "use strict";

    const TOKENS = window.PV_TOKENS;
    const GEO = window.PVGeometry;
    if (!TOKENS || !GEO) {
        document.getElementById("status").textContent = "缺少 tokens.js 或 geometry.js，请确认三个脚本按顺序加载。";
        return;
    }

    const $ = id => document.getElementById(id);
    const root = document.documentElement;

    // ------------------------------------------------------------------
    // 令牌 → CSS 变量（颜色、字号、圆角、内边距）
    // ------------------------------------------------------------------

    const COLOR_VARS = {
        "--pv-text-primary": "color.text.primary",
        "--pv-text-secondary": "color.text.secondary",
        "--pv-viewer-bg": "color.viewer.background",
        "--pv-bar-bg": "color.bar.background",
        "--pv-divider": "color.divider",
        "--pv-film-card-bg": "color.film.card.background",
        "--pv-film-current": "color.film.card.currentBorder",
        "--pv-film-cached": "color.film.card.cachedBorder",
        "--pv-film-name": "color.film.card.name",
        "--pv-film-shake": "color.film.shakeBadge",
        "--pv-star-on": "color.star.on",
        "--pv-star-off": "color.star.off",
        "--pv-star-clear": "color.star.clear",
        "--pv-filter-border": "color.filter.toggle.border",
        "--pv-filter-fg": "color.filter.toggle.foreground",
        "--pv-filter-hover": "color.filter.toggle.hover",
        "--pv-filter-checked-bg": "color.filter.toggle.checked.background",
        "--pv-filter-checked-border": "color.filter.toggle.checked.border",
        "--pv-filter-checked-fg": "color.filter.toggle.checked.foreground",
        "--pv-toolbar-block-bg": "color.toolbar.block.background",
        "--pv-command-bg": "color.toolbar.command.background",
        "--pv-command-border": "color.toolbar.command.border",
        "--pv-sim-bg": "color.sim.panel.background",
        "--pv-sim-border": "color.sim.panel.border",
        "--pv-sim-placeholder": "color.sim.placeholder",
        "--pv-sim-caption": "color.sim.caption",
        "--pv-sim-action-bg": "color.sim.action.background",
        "--pv-sim-action-border": "color.sim.action.border",
        "--pv-sim-action-fg": "color.sim.action.foreground",
        "--pv-sim-action-hover": "color.sim.action.hover",
        "--pv-analysis-tile-bg": "color.analysis.tile.background",
        "--pv-analysis-tile-border": "color.analysis.tile.border",
        "--pv-analysis-badge-bg": "color.analysis.badge.background",
        "--pv-analysis-badge-fg": "color.analysis.badge.foreground",
        "--pv-analysis-divider": "color.analysis.divider",
        "--pv-analysis-caption": "color.analysis.caption",
        "--pv-nav-frame": "color.nav.frame",
        "--pv-toast-fg": "color.status.toast.foreground"
    };

    const PX_VARS = {
        "--pv-film-card-radius": "film.card.cornerRadius",
        "--pv-film-thumb-radius": "film.thumb.cornerRadius",
        "--pv-film-card-border": "film.card.border.width",
        "--pv-film-badge-radius": "film.card.badge.cornerRadius",
        "--pv-filter-radius": "filter.control.cornerRadius",
        "--pv-toolbar-radius": "toolbar.cornerRadius",
        "--pv-sim-radius": "sim.panel.cornerRadius",
        "--pv-sim-border-thickness": "sim.panel.border.width",
        "--pv-sim-action-width": "sim.action.width",
        "--pv-sim-action-height": "sim.action.height",
        "--pv-sim-action-radius": "sim.action.cornerRadius",
        "--pv-analysis-radius": "analysis.tile.cornerRadius",
        "--pv-analysis-border": "analysis.tile.border.width",
        "--pv-analysis-badge-radius": "analysis.badge.cornerRadius",
        "--pv-analysis-badge-pad-x": "analysis.badge.paddingX",
        "--pv-analysis-badge-pad-y": "analysis.badge.paddingY",
        "--pv-busy-radius": "image.busy.cornerRadius",
        "--pv-toast-radius": "status.toast.cornerRadius",
        "--pv-toast-padding-x": "status.toast.paddingX",
        "--pv-hint-max-width": "image.hint.maxWidth",
        "--pv-nav-radius": "nav.cornerRadius",
        "--pv-nav-frame-thickness": "nav.frame.thickness",
        "--pv-font-film-name": "film.card.name.fontSize",
        "--pv-font-film-second": "film.card.secondLine.fontSize",
        "--pv-font-film-star": "film.card.star.fontSize",
        "--pv-font-film-badge": "film.card.badge.fontSize",
        "--pv-font-exif-label": "toolbar.exif.label.fontSize",
        "--pv-font-exif-value": "toolbar.exif.value.fontSize",
        "--pv-font-rating": "toolbar.rating.glyph.fontSize",
        "--pv-font-rating-clear": "toolbar.rating.clearGlyph.fontSize",
        "--pv-font-command": "toolbar.command.glyph.fontSize",
        "--pv-font-hint-title": "image.hint.title.fontSize",
        "--pv-font-hint-detail": "image.hint.detail.fontSize",
        "--pv-font-nav-percent": "nav.percent.fontSize",
        "--pv-font-toast": "status.toast.fontSize",
        "--pv-font-analysis-caption": "analysis.caption.fontSize"
    };

    /** 令牌颜色支持 #RRGGBB 与 #AARRGGBB；CSS 的 8 位十六进制是 #RRGGBBAA，故统一转成 rgba()。 */
    function cssColor(value) {
        const text = String(value);
        if (/^#[0-9a-f]{8}$/i.test(text)) {
            const alpha = parseInt(text.slice(1, 3), 16) / 255;
            const red = parseInt(text.slice(3, 5), 16);
            const green = parseInt(text.slice(5, 7), 16);
            const blue = parseInt(text.slice(7, 9), 16);
            return `rgba(${red}, ${green}, ${blue}, ${Number(alpha.toFixed(3))})`;
        }
        return text;
    }

    function rgbaFromToken(colorToken, alphaToken) {
        const hex = String(TOKENS[colorToken]).replace("#", "");
        const red = parseInt(hex.slice(0, 2), 16);
        const green = parseInt(hex.slice(2, 4), 16);
        const blue = parseInt(hex.slice(4, 6), 16);
        return `rgba(${red}, ${green}, ${blue}, ${TOKENS[alphaToken]})`;
    }

    function applyTokenVars() {
        for (const [name, token] of Object.entries(COLOR_VARS)) root.style.setProperty(name, cssColor(TOKENS[token]));
        for (const [name, token] of Object.entries(PX_VARS)) root.style.setProperty(name, `${TOKENS[token]}px`);
        // IMAGE-06 / STATE-03：背景色 + 独立不透明度令牌。
        root.style.setProperty("--pv-nav-bg", rgbaFromToken("color.nav.background", "color.nav.background.alpha"));
        root.style.setProperty("--pv-toast-bg", rgbaFromToken("color.status.toast.background", "color.status.toast.background.alpha"));
    }

    // ------------------------------------------------------------------
    // 场景状态
    // ------------------------------------------------------------------

    const STATE = {
        layout: "auto",
        width: TOKENS["shell.window.default.width"],
        height: TOKENS["shell.window.default.height"],
        fileBarVisible: true,
        controlVisible: true,
        showRating: true,
        similarityOpen: false,
        analysisOpen: false,
        photoCount: 12,
        currentIndex: 0,
        lastIndex: null,
        ratings: [],
        reducedMotion: window.matchMedia && window.matchMedia("(prefers-reduced-motion: reduce)").matches,
        macAppearance: true,
        zoomed: false,
        busy: false,
        toast: false,
        scroll: { x: 0, y: 0 },
        lastHorizontal: null,
        lastFileBarVisible: null
    };

    let currentGeometry = null;
    let animationHandle = null;

    function ensureRatings() {
        const pattern = [3, 0, 2, 0, 4, 0, 1, 0, 5, 0, 2, 0];
        while (STATE.ratings.length < STATE.photoCount) STATE.ratings.push(pattern[STATE.ratings.length % pattern.length]);
        if (STATE.ratings.length > STATE.photoCount) STATE.ratings.length = STATE.photoCount;
    }

    function scenario() {
        ensureRatings();
        // FILTER-04/FILTER-07：计数按自然文本宽度占位；两位数字沿用 Mac 参考宽 52，每多一位补 7 点近似。
        const countDigits = Math.max(2, String(STATE.photoCount).length);
        return {
            layout: STATE.layout,
            width: STATE.width,
            height: STATE.height,
            fileBarVisible: STATE.fileBarVisible,
            controlVisible: STATE.controlVisible,
            photoCount: STATE.photoCount,
            currentIndex: STATE.currentIndex,
            exifItems: 4,
            commands: GEO.DEFAULT_COMMANDS.slice(),
            showRating: STATE.showRating,
            similarityOpen: STATE.similarityOpen,
            similarityItems: 0,
            similarityHasItems: false,
            analysisVisible: STATE.analysisOpen,
            analysisTileCount: 6,
            macAppearance: STATE.macAppearance,
            countWidth: GEO.REFERENCE_COUNT_WIDTH + Math.max(0, countDigits - 2) * 7,
            zoomed: STATE.zoomed,
            showZoomIndicator: true,
            imageWidth: 6000,
            imageHeight: 4000,
            busyVisible: STATE.busy,
            toastVisible: STATE.toast
        };
    }

    // ------------------------------------------------------------------
    // DOM 工具
    // ------------------------------------------------------------------

    function clear(node) {
        while (node.firstChild) node.removeChild(node.firstChild);
    }

    function place(node, frame) {
        node.hidden = false;
        node.style.left = `${frame[0]}px`;
        node.style.top = `${frame[1]}px`;
        node.style.width = `${frame[2]}px`;
        node.style.height = `${frame[3]}px`;
    }

    function make(tag, className, text) {
        const node = document.createElement(tag);
        if (className) node.className = className;
        if (text !== undefined) node.textContent = text;
        return node;
    }

    function clamp(value, low, high) {
        return Math.min(Math.max(value, low), high);
    }

    // ------------------------------------------------------------------
    // 分区与筛选条
    // ------------------------------------------------------------------

    function renderPanes(geom) {
        place($("mock-window"), geom.nodes.root);
        place($("file-pane"), geom.nodes["root.filePane"]);
        place($("photo-pane"), geom.nodes["root.photoPane"]);
        place($("control-scroll"), geom.nodes["root.controlScroll"]);
        place($("file-divider"), geom.nodes["root.fileDivider"]);
        place($("control-divider"), geom.nodes["root.controlDivider"]);
        $("file-pane").hidden = !geom.scene.fileBarVisible;
        $("file-divider").hidden = !geom.scene.fileBarVisible;
        $("control-scroll").hidden = !geom.scene.controlVisible;
        $("control-divider").hidden = !geom.scene.controlVisible;
        place($("analysis-pane"), geom.details.panes.analysis);
        $("analysis-pane").hidden = !geom.scene.analysisVisible;
    }

    function filterControl(text) {
        const node = make("div", "filter-control");
        const label = make("span", "", text);
        const chevron = make("span", "chevron", "▾");
        node.appendChild(label);
        node.appendChild(chevron);
        return node;
    }

    function renderFilter(geom) {
        const host = $("filter-controls");
        host.style.position = "absolute";
        host.style.inset = "0";
        host.style.pointerEvents = "none";
        clear(host);
        const nodes = geom.nodes;
        const sort = filterControl("名称");
        const order = filterControl("升序");
        const rating = filterControl("全部");
        for (const [node, frame] of [[sort, nodes["filter.sort"]], [order, nodes["filter.order"]], [rating, nodes["filter.rating"]]]) {
            place(node, frame);
            node.style.pointerEvents = "auto";
            host.appendChild(node);
        }
        const count = make("div", "filter-count", `共 ${geom.scene.photoCount} 张`);
        place(count, nodes["filter.count"]);
        host.appendChild(count);

        const toggle = make("button", "filter-toggle", "相似聚类");
        toggle.type = "button";
        toggle.setAttribute("aria-pressed", String(geom.scene.similarityOpen));
        toggle.setAttribute("aria-label", "相似聚类");
        toggle.title = geom.scene.similarityOpen ? "关闭相似聚类面板" : "展开相似聚类面板";
        place(toggle, nodes["filter.toggle"]);
        toggle.style.pointerEvents = "auto";
        toggle.addEventListener("click", () => {
            STATE.similarityOpen = !STATE.similarityOpen;
            render();
        });
        host.appendChild(toggle);
    }

    // ------------------------------------------------------------------
    // 胶片栏与定位动画
    // ------------------------------------------------------------------

    function cardFileName(index) {
        const number = 1489 + index;
        return `A7C${String(number).padStart(5, "0")}.ARW`;
    }

    function cardDate(index) {
        const minute = 10 + (index * 7) % 50;
        return `2026-10-18 ${String(9 + Math.floor(index / 6)).padStart(2, "0")}:${String(minute).padStart(2, "0")}`;
    }

    function renderFilmstrip(geom) {
        const scroll = $("filmstrip-scroll");
        const doc = $("filmstrip-doc");
        const filmstrip = geom.details.filmstrip;
        if (!filmstrip) {
            scroll.hidden = true;
            STATE.lastFileBarVisible = false;
            return;
        }
        scroll.hidden = false;
        place(scroll, filmstrip.scroll);
        doc.style.width = `${filmstrip.document[0]}px`;
        doc.style.height = `${filmstrip.document[1]}px`;
        clear(doc);

        for (let index = 0; index < geom.scene.photoCount; index++) {
            const current = index === geom.scene.currentIndex;
            const rating = STATE.ratings[index] || 0;
            const card = make("div", "film-card" + (current ? " current" : ""));
            place(card, geom.nodes[`card${index}.root`]);
            card.setAttribute("aria-label", `${cardFileName(index)}，${rating} 星`);
            card.addEventListener("click", () => selectCard(index));

            const surface = make("div", "film-surface");
            place(surface, geom.nodes[`card${index}.surface`]);

            const image = make("div", "film-thumb" + (index === 0 ? " cached" : ""));
            place(image, geom.nodes[`card${index}.image`]);

            const name = make("div", "film-name", cardFileName(index));
            place(name, geom.nodes[`card${index}.name`]);

            const date = make("div", "film-date", cardDate(index));
            place(date, geom.nodes[`card${index}.date`]);

            surface.appendChild(image);
            surface.appendChild(name);
            surface.appendChild(date);

            // FILM-05：抖动徽标只在判定为抖动时出现；样机给首卡与每第 5 张示例。
            if (index === 0 || index % 5 === 0) {
                const shake = make("div", "film-shake", "抖");
                place(shake, geom.nodes[`card${index}.shake`]);
                surface.appendChild(shake);
            }

            if (geom.scene.showRating) {
                for (let value = 0; value < 6; value++) {
                    const star = make("button", "film-star" + (value === 0 ? " clear" : (value <= rating ? " on" : "")),
                        value === 0 ? "×" : (value <= rating ? "★" : "☆"));
                    star.type = "button";
                    star.title = value === 0 ? "无星级" : `${value} 星`;
                    star.setAttribute("aria-label", value === 0 ? "无星级" : `${value} 星`);
                    place(star, geom.nodes[`card${index}.star${value}`]);
                    star.addEventListener("click", event => {
                        event.stopPropagation();
                        STATE.ratings[index] = value;
                        render();
                    });
                    surface.appendChild(star);
                }
            }

            card.appendChild(surface);
            doc.appendChild(card);
        }

        // FILM-04：方向切换或重新显示文件栏时显式定位当前项；普通尺寸变化只夹取。
        const directionChanged = STATE.lastHorizontal !== null && STATE.lastHorizontal !== geom.horizontal;
        const fileBarShown = STATE.lastFileBarVisible === false && geom.scene.fileBarVisible;
        if (directionChanged || fileBarShown) {
            STATE.scroll = filmstripTarget(geom);
        } else {
            STATE.scroll.x = clamp(STATE.scroll.x, 0, filmstrip.horizontal ? filmstrip.maxOffset : 0);
            STATE.scroll.y = clamp(STATE.scroll.y, 0, filmstrip.horizontal ? 0 : filmstrip.maxOffset);
        }
        STATE.lastHorizontal = geom.horizontal;
        STATE.lastFileBarVisible = geom.scene.fileBarVisible;
        positionFilmstrip();
    }

    /** FILM-02：当前卡片中心到视口中心的主轴偏移（首尾允许不居中）。 */
    function filmstripTarget(geom) {
        const filmstrip = geom.details && geom.details.filmstrip;
        if (!filmstrip || geom.scene.photoCount === 0) return { x: 0, y: 0 };
        const card = filmstrip.cards[geom.scene.currentIndex];
        if (!card) return { x: 0, y: 0 };
        const horizontal = filmstrip.horizontal;
        const itemStart = horizontal ? card.frame[0] : card.frame[1];
        const itemLength = horizontal ? card.frame[2] : card.frame[3];
        const viewportLength = horizontal ? filmstrip.scroll[2] : filmstrip.scroll[3];
        const documentLength = horizontal ? filmstrip.document[0] : filmstrip.document[1];
        const offset = GEO.filmstripCenterOffset(itemStart, itemLength, viewportLength, documentLength);
        return horizontal ? { x: offset, y: 0 } : { x: 0, y: offset };
    }

    function positionFilmstrip() {
        const doc = $("filmstrip-doc");
        doc.style.transform = `translate(${-STATE.scroll.x}px, ${-STATE.scroll.y}px)`;
    }

    function cancelAnimation() {
        if (animationHandle !== null) {
            cancelAnimationFrame(animationHandle);
            animationHandle = null;
        }
    }

    /** FILM-02/FILM-03：350ms 三次缓出；减少动态效果直接到终点。 */
    function animateScrollToCurrent() {
        cancelAnimation();
        const geom = currentGeometry;
        if (!geom || !geom.details.filmstrip) return;
        const target = filmstripTarget(geom);
        const start = { x: STATE.scroll.x, y: STATE.scroll.y };
        const threshold = TOKENS["motion.film.scroll.negligibleThreshold"];
        if (STATE.reducedMotion || GEO.filmstripIsNegligible(start.x, start.y, target.x, target.y, threshold)) {
            STATE.scroll = target;
            positionFilmstrip();
            return;
        }
        const duration = TOKENS["motion.film.scroll.duration"];
        const began = performance.now();
        const tick = now => {
            const progress = clamp((now - began) / duration, 0, 1);
            const eased = GEO.filmstripEaseOutCubic(progress);
            STATE.scroll = {
                x: start.x + (target.x - start.x) * eased,
                y: start.y + (target.y - start.y) * eased
            };
            positionFilmstrip();
            animationHandle = progress < 1 ? requestAnimationFrame(tick) : null;
        };
        animationHandle = requestAnimationFrame(tick);
    }

    function selectCard(index) {
        if (index === STATE.currentIndex) return;
        cancelAnimation();
        STATE.lastIndex = STATE.currentIndex;
        STATE.currentIndex = index;
        render();
    }

    function navigate(delta) {
        if (STATE.photoCount === 0) return;
        const target = clamp(STATE.currentIndex + delta, 0, STATE.photoCount - 1);
        if (target === STATE.currentIndex) return;
        cancelAnimation();
        STATE.lastIndex = STATE.currentIndex;
        STATE.currentIndex = target;
        render();
        animateScrollToCurrent();
    }

    // ------------------------------------------------------------------
    // 控制栏
    // ------------------------------------------------------------------

    const EXIF_ITEMS = [
        ["光圈", "f/2.8"],
        ["快门", "1/250"],
        ["ISO", "400"],
        ["焦距", "35mm"]
    ];

    const COMMANDS = {
        Previous: { glyph: "‹", name: "上一张" },
        Next: { glyph: "›", name: "下一张" },
        Exchange: { glyph: "⇄", name: "切换上一张" },
        Fit: { glyph: "⤢", name: "适应" },
        ZoomInPreset: { glyph: "＋", name: "放大（预设）" },
        ZoomOutPreset: { glyph: "－", name: "缩小（预设）" }
    };

    function runCommand(command) {
        switch (command) {
            case "Previous": navigate(-1); break;
            case "Next": navigate(1); break;
            case "Exchange":
                if (STATE.lastIndex !== null && STATE.lastIndex >= 0 && STATE.lastIndex < STATE.photoCount) {
                    const previous = STATE.lastIndex;
                    STATE.lastIndex = STATE.currentIndex;
                    STATE.currentIndex = previous;
                    render();
                }
                break;
            case "Fit": STATE.zoomed = false; render(); break;
            case "ZoomInPreset":
            case "ZoomOutPreset": STATE.zoomed = true; render(); break;
            default: break;
        }
    }

    function renderControl(geom) {
        const host = $("control-doc");
        place(host, geom.nodes.controlDocument);
        host.style.width = `${geom.nodes.controlDocument[2]}px`;
        host.style.height = `${geom.nodes.controlDocument[3]}px`;
        clear(host);
        if (!geom.scene.controlVisible) return;
        const rating = STATE.ratings[STATE.currentIndex] || 0;
        geom.details.control.groups.forEach((group, groupIndex) => {
            const node = make("div", `control-group ${group.kind}`);
            place(node, group.frame);
            if (group.kind === "exif") {
                group.children.forEach((frame, index) => {
                    if (index >= EXIF_ITEMS.length) return;
                    const block = make("div", "control-exif-block");
                    place(block, frame);
                    block.appendChild(make("span", "label", EXIF_ITEMS[index][0]));
                    block.appendChild(make("span", "value", EXIF_ITEMS[index][1]));
                    node.appendChild(block);
                });
            } else if (group.kind === "rating") {
                group.children.forEach((frame, index) => {
                    const value = index;
                    const button = make("button", "rating-button" + (value === 0 ? " clear" : (value <= rating ? " on" : "")),
                        value === 0 ? "×" : (value <= rating ? "★" : "☆"));
                    button.type = "button";
                    button.title = value === 0 ? "无星级" : `${value} 星`;
                    button.setAttribute("aria-label", value === 0 ? "无星级" : `${value} 星`);
                    button.disabled = geom.scene.photoCount === 0;
                    place(button, frame);
                    button.addEventListener("click", () => {
                        if (STATE.photoCount === 0) return;
                        STATE.ratings[STATE.currentIndex] = value;
                        render();
                    });
                    node.appendChild(button);
                });
            } else if (group.kind === "command") {
                GEO.DEFAULT_COMMANDS.forEach((command, index) => {
                    if (index >= group.children.length) return;
                    const meta = COMMANDS[command] || { glyph: "·", name: command };
                    const button = make("button", "command-button", meta.glyph);
                    button.type = "button";
                    button.title = meta.name;
                    button.setAttribute("aria-label", meta.name);
                    button.disabled = geom.scene.photoCount === 0;
                    place(button, group.children[index]);
                    button.addEventListener("click", () => runCommand(command));
                    node.appendChild(button);
                });
            }
            host.appendChild(node);
        });
    }

    // ------------------------------------------------------------------
    // 主图、导航图、提示
    // ------------------------------------------------------------------

    function renderPhoto(geom) {
        const pane = geom.details.panes.photo;
        const image = $("photo-image");
        const label = $("photo-placeholder-label");
        const hint = $("photo-hint");
        if (geom.scene.photoCount === 0) {
            image.hidden = true;
            label.hidden = true;
            hint.hidden = false;
        } else {
            hint.hidden = true;
            image.hidden = false;
            label.hidden = false;
            const aspect = geom.scene.imageWidth / geom.scene.imageHeight;
            const fit = Math.min(pane[2] / geom.scene.imageWidth, pane[3] / geom.scene.imageHeight);
            const scale = fit * (STATE.zoomed ? 1.6 : 1);
            const width = geom.scene.imageWidth * scale;
            const height = geom.scene.imageHeight * scale;
            image.style.left = `${(pane[2] - width) / 2}px`;
            image.style.top = `${(pane[3] - height) / 2}px`;
            image.style.width = `${width}px`;
            image.style.height = `${height}px`;
            label.style.left = `${pane[2] / 2}px`;
            label.style.top = `${pane[3] / 2}px`;
        }

        const navHost = $("nav-host");
        if (geom.nodes["nav.host"]) {
            place(navHost, geom.nodes["nav.host"]);
            place($("nav-image"), geom.nodes["nav.image"]);
            place($("nav-frame"), geom.nodes["nav.frame"]);
            place($("nav-label"), geom.nodes["nav.label"]);
            $("nav-label").textContent = `${Math.round(160)}%`;
        } else {
            navHost.hidden = true;
        }

        const busy = $("busy-pill");
        if (geom.nodes["photo.busy"]) {
            place(busy, geom.nodes["photo.busy"]);
        } else {
            busy.hidden = true;
        }

        const toast = $("toast");
        if (geom.nodes["photo.toast"]) {
            place(toast, geom.nodes["photo.toast"]);
        } else {
            toast.hidden = true;
        }
    }

    // ------------------------------------------------------------------
    // 分析栏与相似面板
    // ------------------------------------------------------------------

    const ANALYSIS_LABELS = ["对焦点", "中心", "直方图", "DINO PCA", "Cosine 16,16", "锐度", "抖动拖影"];

    function renderAnalysis(geom) {
        const pane = $("analysis-pane");
        if (!geom.scene.analysisVisible || !geom.details.analysis) {
            pane.hidden = true;
            return;
        }
        clear(pane);
        const divider = make("div", "analysis-divider");
        place(divider, geom.nodes["analysis.divider"]);
        pane.appendChild(divider);
        const labels = geom.scene.analysisTileCount >= 7 ? ANALYSIS_LABELS : ANALYSIS_LABELS.slice(1);
        geom.details.analysis.tiles.forEach((frame, index) => {
            const tile = make("div", "analysis-tile" + (labels[index] === "直方图" ? " histogram" : ""));
            place(tile, frame);
            tile.appendChild(make("div", "content"));
            tile.appendChild(make("div", "analysis-badge", labels[index] || `瓦片 ${index + 1}`));
            pane.appendChild(tile);
        });
    }

    function renderSimilarity(geom) {
        const pane = $("similarity-pane");
        if (!geom.nodes["sim.panel"]) {
            pane.hidden = true;
            return;
        }
        place(pane, geom.nodes["sim.panel"]);
        clear(pane);
        // SIM-02：空态只显示占位文案与按钮，不重复「N 张未提取」。
        pane.appendChild(make("div", "placeholder", "尚未提取特征"));
        const action = make("button", "action", "提取全部");
        action.type = "button";
        pane.appendChild(action);
    }

    // ------------------------------------------------------------------
    // 舞台缩放与状态
    // ------------------------------------------------------------------

    function fitStage(geom) {
        const stage = $("stage");
        const rect = stage.getBoundingClientRect();
        const pad = 48;
        const scale = Math.min(1,
            Math.max(0.05, (rect.width - pad) / geom.scene.width),
            Math.max(0.05, (rect.height - pad) / geom.scene.height));
        const mock = $("mock-window");
        mock.style.width = `${geom.scene.width}px`;
        mock.style.height = `${geom.scene.height}px`;
        mock.style.transform = `scale(${scale})`;
        mock.style.left = `${Math.max(0, (rect.width - geom.scene.width * scale) / 2)}px`;
        mock.style.top = `${Math.max(0, (rect.height - geom.scene.height * scale) / 2)}px`;
        return scale;
    }

    function updateStatus(geom, scale) {
        const layoutName = geom.horizontal ? "行布局（上下）" : "列布局（左右）";
        const mode = STATE.layout === "auto" ? "自动 → " : "";
        const current = geom.scene.photoCount === 0 ? "0/0" : `${STATE.currentIndex + 1}/${geom.scene.photoCount}`;
        $("status").textContent = [
            `${mode}${layoutName} · ${geom.scene.width} × ${geom.scene.height}`,
            `节点 ${Object.keys(geom.nodes).length} 个 · 显示缩放 ${scale.toFixed(2)}`,
            `当前 ${current} · 定位 ${STATE.scroll.x.toFixed(1)}, ${STATE.scroll.y.toFixed(1)}`,
            STATE.reducedMotion ? "减少动态效果：开" : "减少动态效果：关"
        ].join("\n");
    }

    // ------------------------------------------------------------------
    // 总装
    // ------------------------------------------------------------------

    function render() {
        ensureRatings();
        if (STATE.currentIndex >= STATE.photoCount) STATE.currentIndex = Math.max(0, STATE.photoCount - 1);
        const geom = GEO.computeGeometry(TOKENS, scenario());
        currentGeometry = geom;
        renderPanes(geom);
        renderFilter(geom);
        renderFilmstrip(geom);
        renderControl(geom);
        renderPhoto(geom);
        renderAnalysis(geom);
        renderSimilarity(geom);
        const scale = fitStage(geom);
        updateStatus(geom, scale);
    }

    // ------------------------------------------------------------------
    // 导出
    // ------------------------------------------------------------------

    function roundFrame(frame) {
        return frame.map(value => {
            const rounded = Number(value.toFixed(2));
            return Object.is(rounded, -0) ? 0 : rounded;
        });
    }

    function exportGeometry() {
        if (!currentGeometry) return;
        const geom = currentGeometry;
        const mode = geom.horizontal ? "Row" : "Col";
        const layouts = {};
        layouts[`${mode}-${geom.scene.width}x${geom.scene.height}`] = Object.fromEntries(
            Object.entries(geom.nodes).map(([name, frame]) => [name, roundFrame(frame)]));
        const payload = {
            source: "prototype",
            scenario: Object.assign({}, scenario(), { currentIndex: STATE.currentIndex }),
            layouts
        };
        const blob = new Blob([JSON.stringify(payload, null, 2) + "\n"], { type: "application/json" });
        const link = document.createElement("a");
        link.href = URL.createObjectURL(blob);
        link.download = `${mode}-${geom.scene.width}x${geom.scene.height}-geometry.json`;
        document.body.appendChild(link);
        link.click();
        link.remove();
        setTimeout(() => URL.revokeObjectURL(link.href), 1000);
        $("status").textContent += `\n已导出 ${link.download}`;
    }

    // ------------------------------------------------------------------
    // 控件接线
    // ------------------------------------------------------------------

    function setPreset(value) {
        $("preset").value = value;
        if (value === "custom") return;
        const [width, height] = value.split("x").map(Number);
        STATE.width = width;
        STATE.height = height;
        $("width").value = String(width);
        $("height").value = String(height);
    }

    function bind() {
        $("layout").value = STATE.layout;
        $("layout").addEventListener("change", event => {
            STATE.layout = event.target.value;
            render();
        });
        $("preset").addEventListener("change", event => {
            setPreset(event.target.value);
            render();
        });
        for (const [id, key] of [["width", "width"], ["height", "height"]]) {
            $(id).addEventListener("input", event => {
                const value = Math.max(1, Number(event.target.value) || 1);
                STATE[key] = value;
                $("preset").value = "custom";
                render();
            });
        }
        $("file-visible").addEventListener("change", event => { STATE.fileBarVisible = event.target.checked; render(); });
        $("control-visible").addEventListener("change", event => { STATE.controlVisible = event.target.checked; render(); });
        $("show-rating").addEventListener("change", event => { STATE.showRating = event.target.checked; render(); });
        $("similarity-open").addEventListener("change", event => { STATE.similarityOpen = event.target.checked; render(); });
        $("analysis-open").addEventListener("change", event => { STATE.analysisOpen = event.target.checked; render(); });
        $("photo-count").addEventListener("input", event => {
            STATE.photoCount = clamp(Number(event.target.value) || 0, 0, 60);
            ensureRatings();
            if (STATE.currentIndex >= STATE.photoCount) STATE.currentIndex = Math.max(0, STATE.photoCount - 1);
            render();
        });
        $("reduced-motion").addEventListener("change", event => { STATE.reducedMotion = event.target.checked; });
        $("mac-appearance").addEventListener("change", event => { STATE.macAppearance = event.target.checked; render(); });
        $("zoomed").addEventListener("change", event => { STATE.zoomed = event.target.checked; render(); });
        $("busy").addEventListener("change", event => { STATE.busy = event.target.checked; render(); });
        $("toast-demo").addEventListener("change", event => { STATE.toast = event.target.checked; render(); });
        $("previous").addEventListener("click", () => navigate(-1));
        $("next").addEventListener("click", () => navigate(1));
        $("export").addEventListener("click", exportGeometry);

        // FILM-03：用户滚动或按下胶片栏即撤销未完成的定位动画，位置交还用户。
        const scroll = $("filmstrip-scroll");
        scroll.addEventListener("pointerdown", cancelAnimation);
        scroll.addEventListener("wheel", event => {
            const geom = currentGeometry;
            const filmstrip = geom && geom.details.filmstrip;
            if (!filmstrip) return;
            event.preventDefault();
            cancelAnimation();
            const delta = filmstrip.horizontal
                ? (Math.abs(event.deltaX) > Math.abs(event.deltaY) ? event.deltaX : event.deltaY)
                : event.deltaY;
            if (filmstrip.horizontal) {
                STATE.scroll.x = clamp(STATE.scroll.x + delta, 0, filmstrip.maxOffset);
            } else {
                STATE.scroll.y = clamp(STATE.scroll.y + delta, 0, filmstrip.maxOffset);
            }
            positionFilmstrip();
        }, { passive: false });

        window.addEventListener("resize", () => {
            if (currentGeometry) {
                const scale = fitStage(currentGeometry);
                updateStatus(currentGeometry, scale);
            }
        });
    }

    applyTokenVars();
    $("reduced-motion").checked = STATE.reducedMotion;
    bind();
    render();
})();
