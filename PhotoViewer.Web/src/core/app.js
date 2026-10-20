/**
 * 主屏模型：把业务状态集中在一个对象里，壳层只读快照、翻译输入、注入 DOM 能力。
 *
 * createApp({ t, clock, profile, library, decoder, store, storage, settings, dpr, reducedMotion, measureText })
 *   app.state                    只读快照（window/layout/folder/cards/current/viewport/session/toolbar/...）
 *   app.settings                 当前生效设置（只读）
 *   app.on(name, fn) / off       事件："change"(Set<区域>) 与 "request-open"({ kind })
 *
 * 动作：setWindowSize/openFolder/openFile/selectCard/selectSimilar/navigate/exchange/setSort/
 * setRatingFilter/rate/toggleFilmbar/toggleControlbar/toggleAnalysis/setSimilarityExpanded/
 * fit/zoomPreset/zoomScale/zoomBy/pan/centerOn/toggleEnhance/runCommand/runMenu/requestOpen/
 * keyDown/keyUp/blur/setModifierState/wheelOnPhoto/mouseOnPhoto/filmUserScrolled/filmResized/bindFilmScroller/layoutReady/
 * setMenuOpen/setDropdownOpen/setTextFocus/updateSettings/hoverDetail/clickDiagnostic/
 * clickAnalysisBlank/locateDetail/extractAll/setHistogramSource/simulateMemoryWarning/setDpr/menuModel/notify/canOpen
 *   app.filmAnimating             胶片栏定位动画是否在播放（调试读数）
 */
import { FileList } from "./filter.js";
import { Viewport, displaySize } from "./viewport.js";
import { FilmScroller, FilmPositioner, contentLength } from "./filmscroll.js";
import { PhotoSession, RatingWriter } from "./session.js";
import { AnalysisPresenter } from "./analysis.js";
import { SimilarityPanel } from "./similarity.js";
import { CommandMap, KeyDispatcher, WheelInterpreter, gestureFromMouse, gestureText, tooltipText } from "./input.js";
import { defaultSettings } from "./settings.js";
import { computeLayout, approximateTextWidth } from "./layout/index.js";
import { formatExifValue } from "./exifformat.js";

const SETTINGS_KEY = "PhotoViewer.Settings";
const SHOW_FILM_KEY = "PhotoViewer.Native.ShowFilm";
const SHOW_CONTROLS_KEY = "PhotoViewer.Native.ShowControls";
const CACHE_LIMIT_DEFAULT = 30;
const HISTORY_LIMIT = 50;
const LAYOUT_MODES = new Set(["auto", "row", "col"]);
const LAYOUT_REGIONS = new Set([
    "window", "layout", "list", "panes", "toolbar", "analysis", "similarity", "current", "settings",
]);
const NEEDS_PHOTO = new Set([
    "Previous", "Next", "Exchange", "Fit",
    "ZoomInPreset", "ZoomOutPreset", "ZoomInScale", "ZoomOutScale",
    "ToggleEnhance", "SetRating0", "SetRating1", "SetRating2", "SetRating3", "SetRating4", "SetRating5",
]);

const NULL_STORE = Object.freeze({
    read: async () => null,
    compute: async () => null,
    extractGroup: async () => {},
});

function finite(value) {
    return typeof value === "number" && Number.isFinite(value);
}

function clamp(value, min, max) {
    return value < min ? min : value > max ? max : value;
}

function gestureSlot(value) {
    return typeof value === "string" && value.length > 0 ? value : null;
}

function mergeFormats(list, baseList) {
    if (!Array.isArray(list)) return baseList;
    const byId = new Map(baseList.map((entry) => [entry.id, entry]));
    const out = [];
    const seen = new Set();
    for (const entry of list) {
        if (!entry || typeof entry.id !== "string" || !byId.has(entry.id) || seen.has(entry.id)) continue;
        seen.add(entry.id);
        out.push({ ...byId.get(entry.id), enabled: entry.enabled !== false });
    }
    for (const entry of baseList) if (!seen.has(entry.id)) out.push(entry);
    return out;
}

function mergeExifItems(list, baseList) {
    if (!Array.isArray(list)) return baseList;
    const byId = new Map(baseList.map((entry) => [entry.id, entry]));
    const out = [];
    const seen = new Set();
    for (const entry of list) {
        if (!entry || typeof entry.id !== "string" || !byId.has(entry.id) || seen.has(entry.id)) continue;
        seen.add(entry.id);
        out.push({ ...byId.get(entry.id), enabled: entry.enabled !== false });
    }
    for (const entry of baseList) if (!seen.has(entry.id)) out.push(entry);
    return out;
}

function mergeHotkeys(list, baseList) {
    if (!Array.isArray(list)) return baseList;
    const byCommand = new Map(baseList.map((entry) => [entry.command, entry]));
    const out = [];
    const seen = new Set();
    for (const entry of list) {
        if (!entry || typeof entry.command !== "string" || !byCommand.has(entry.command) || seen.has(entry.command)) continue;
        seen.add(entry.command);
        const baseRow = byCommand.get(entry.command);
        out.push({
            ...baseRow,
            display: typeof entry.display === "boolean" ? entry.display : baseRow.display,
            primary: "primary" in entry ? gestureSlot(entry.primary) : baseRow.primary,
            secondary: "secondary" in entry ? gestureSlot(entry.secondary) : baseRow.secondary,
        });
    }
    for (const entry of baseList) if (!seen.has(entry.command)) out.push(entry);
    return out;
}

/** 设置文档归一化：未知字段忽略，越界值夹取，缺失列表回退默认。 */
export function normalizeSettings(raw, base = defaultSettings()) {
    const out = {
        ...base,
        formats: base.formats.map((entry) => ({ ...entry, exts: [...entry.exts] })),
        exifItems: base.exifItems.map((entry) => ({ ...entry })),
        hotkeys: base.hotkeys.map((entry) => ({ ...entry })),
        zoomPresets: [...base.zoomPresets],
        cacheCount: finite(base.cacheCount) ? base.cacheCount : CACHE_LIMIT_DEFAULT,
    };
    if (!raw || typeof raw !== "object") return out;
    if (LAYOUT_MODES.has(raw.layoutMode)) out.layoutMode = raw.layoutMode;
    for (const key of [
        "showZoomIndicator", "showRating", "safeSetRating", "sameNameAsOne",
        "analysisImmediateCompute", "similarityPanelExpanded", "appleKeyboard",
    ]) {
        if (typeof raw[key] === "boolean") out[key] = raw[key];
    }
    if (Array.isArray(raw.zoomPresets)) {
        const presets = raw.zoomPresets.filter((value) => finite(value) && value > 0 && value <= 1600);
        if (presets.length > 0) out.zoomPresets = presets;
    }
    if (finite(raw.similarityThreshold)) out.similarityThreshold = clamp(raw.similarityThreshold, 0.75, 0.95);
    if (finite(raw.similarityMaxResults)) {
        out.similarityMaxResults = Math.round(clamp(raw.similarityMaxResults, 1, 32));
    }
    if (finite(raw.cacheCount)) out.cacheCount = Math.round(clamp(raw.cacheCount, 1, 400));
    out.formats = mergeFormats(raw.formats, out.formats);
    out.exifItems = mergeExifItems(raw.exifItems, out.exifItems);
    out.hotkeys = mergeHotkeys(raw.hotkeys, out.hotkeys);
    return out;
}

class MainApp {
    constructor({
        t,
        clock,
        profile = "web",
        library,
        decoder,
        store,
        storage = null,
        settings = null,
        dpr = 1,
        reducedMotion = () => false,
        measureText = approximateTextWidth,
    } = {}) {
        if (typeof t !== "function") throw new TypeError("createApp 需要令牌函数 t");
        if (!clock) throw new TypeError("createApp 需要 clock");
        if (!decoder) throw new TypeError("createApp 需要 decoder");
        this._t = t;
        this._clock = clock;
        this._profile = profile;
        this._storage = storage;
        this._reducedMotion = typeof reducedMotion === "function" ? reducedMotion : () => false;
        this._measureText = typeof measureText === "function" ? measureText : approximateTextWidth;
        this._library = library && Array.isArray(library.folders) ? library : { folders: [] };
        this._decoder = decoder;
        this._store = store ?? NULL_STORE;

        this._settings = this._loadSettings(settings);
        this._window = { width: 0, height: 0 };
        this._panes = {
            film: this._read(SHOW_FILM_KEY) !== "false",
            control: this._read(SHOW_CONTROLS_KEY) !== "false",
            analysis: false,
        };
        this._folder = null;
        this._singleMode = false;
        this._photos = [];
        this._displayedId = null;
        this._anchorId = null;
        this._history = [];
        this._menuOpen = false;
        this._dropdownOpen = false;
        this._textFocus = false;
        this._cached = new Set();
        this._cachedLimit = this._settings.cacheCount;
        this._listeners = new Map();
        this._pendingRegions = new Set();
        this._flushScheduled = false;
        this._state = null;
        this._layout = null;
        this._layoutDirty = true;
        this._pendingFilmLocate = null;
        this._positioner = null;
        this._scroller = null;
        this._filmViewportLength = null;
        this._analysisTileCount = 6;
        this._similarityLayout = { visible: false, count: 0 };

        this._fileList = new FileList({
            formats: this._settings.formats,
            sameNameAsOne: this._settings.sameNameAsOne,
        });
        this._viewport = new Viewport(t, { dpr, allowBelowFit: true });

        this._photoSession = new PhotoSession({
            t,
            clock,
            decoder,
            onChange: (snapshot) => this._onSession(snapshot),
        });
        this._sessionSnap = this._photoSession.snapshot();

        this._analysis = new AnalysisPresenter({
            t,
            clock,
            store: this._store,
            onChange: () => this._onAnalysisChange(),
            onLocate: (u, v) => this.centerOn(u, v),
        });
        this._analysis.setImmediateCompute(this._settings.analysisImmediateCompute);
        this._analysisTileCount = this._analysis.tiles().length;

        this._similarity = new SimilarityPanel({
            t,
            clock,
            store: this._store,
            onChange: () => this._onSimilarityChange(),
        });
        this._similarity.setFilebarVisible(this._panes.film);
        this._similarity.setParams({
            threshold: this._settings.similarityThreshold,
            maxResults: this._settings.similarityMaxResults,
        });
        this._similarity.setExpanded(this._settings.similarityPanelExpanded);

        this._ratingWriter = new RatingWriter({
            clock,
            onToast: (text) => this._photoSession.toast(text),
        });
        this._commandMap = new CommandMap(this._settings.hotkeys);
        this._dispatcher = new KeyDispatcher({
            commandMap: this._commandMap,
            isSuppressed: () => this._menuOpen || this._dropdownOpen || this._textFocus,
        });
        this._wheel = new WheelInterpreter(clock);

        this._recomputeLayout();
        this._syncProxyAndWantFull();
    }

    get state() {
        if (this._layoutDirty || !this._layout) this._recomputeLayout();
        if (!this._state) this._buildState();
        return this._state;
    }

    get settings() {
        return this._settings;
    }

    on(name, fn) {
        if (typeof fn !== "function") return () => {};
        let set = this._listeners.get(name);
        if (!set) {
            set = new Set();
            this._listeners.set(name, set);
        }
        set.add(fn);
        return () => this.off(name, fn);
    }

    off(name, fn) {
        const set = this._listeners.get(name);
        if (!set) return;
        set.delete(fn);
        if (set.size === 0) this._listeners.delete(name);
    }

    // SHELL-01：按实际内容区尺寸重算布局；SHELL-03 不向最小尺寸夹取（web 视口可更小）。
    setWindowSize(width, height) {
        if (!finite(width) || !finite(height)) return false;
        const next = { width: Math.max(0, width), height: Math.max(0, height) };
        if (next.width === this._window.width && next.height === this._window.height) return false;
        const previousMode = this._layout ? this._layout.layoutMode : null;
        this._window = next;
        this._recomputeLayout();
        if (previousMode && previousMode !== this._layout.layoutMode) {
            // FILM-04：换向视同一次显式定位请求，旧目标按新几何作废。
            this._positioner?.onAxisChanged();
            this._requestLocate("reshow");
        }
        this._syncProxyAndWantFull();
        this._invalidate(["window", "layout", "viewport"]);
        return true;
    }

    setDpr(dpr) {
        if (!finite(dpr) || dpr <= 0) return false;
        const changed = this._viewport.setDpr(dpr);
        if (changed) {
            this._syncProxyAndWantFull();
            this._invalidate(["viewport"]);
        }
        return changed;
    }

    openFolder(folderId, { target } = {}) {
        const folder = this._library.folders.find((entry) => entry && entry.id === folderId);
        if (!folder) return false;
        const folderPhotos = Array.isArray(folder.photos) ? folder.photos : [];
        const requested = this._photoInFolder(folder, target);
        const preferredId = requested && this._allowed(requested) ? requested : null;
        const currentId = this._folder && this._folder.id === folder.id ? this._displayedId : null;
        // OPEN-08：同一文件夹重复打开视为刷新，以当前照片为首选目标。
        const refresh = !preferredId && currentId ? this._photoInFolder(folder, currentId) : null;
        const single = folder.enumerable === false;
        let photos;
        if (single) {
            const only = preferredId ?? refresh ?? folderPhotos.find((photo) => this._allowed(photo)) ?? folderPhotos[0] ?? null;
            photos = only ? [only] : [];
        } else {
            photos = folderPhotos.slice();
        }
        this._commitFolder({
            id: folder.id,
            name: single && photos[0] ? photos[0].name : folder.name,
            single,
            photos,
            preferred: preferredId ?? refresh,
        });
        return true;
    }

    openFile(photoId) {
        const found = this._findPhoto(photoId);
        if (!found || !this._allowed(found)) return false;
        const folder = this._library.folders.find((entry) => entry.id === found.folderId);
        if (!folder || folder.enumerable === false) {
            return this._openSingle(found.folderId, found);
        }
        return this.openFolder(folder.id, { target: found });
    }

    selectCard(photoId) {
        const id = typeof photoId === "string" ? photoId : photoId?.id;
        if (!id) return false;
        // FILM-03：用户点选即接管，撤销尚未执行的定位与动画。
        this._positioner?.onSelectByClick();
        const card = this._fileList.cardOf(id);
        if (!card) return false;
        const targetId = card.photo.id;
        if (targetId === this._displayedId && targetId === this._anchorId) return false;
        this._applyDisplayed(targetId, { mode: "batch", anchor: true, similarityAnchor: true });
        return true;
    }

    selectSimilar(photoId) {
        const id = typeof photoId === "string" ? photoId : photoId?.id;
        if (!id || id === this._displayedId) return false;
        const photo = this._photoById(id) ?? this._findPhoto(id);
        if (!photo) return false;
        const card = this._fileList.cardOf(photo.id);
        const targetId = card ? card.photo.id : photo.id;
        if (targetId === this._displayedId) return false;
        // SIM-05：只改主图身份（显示项），锚点、列表位置与面板结果都不动。
        this._applyDisplayed(targetId, { mode: "batch", anchor: false, similarityAnchor: false });
        return true;
    }

    navigate(delta) {
        if (!finite(delta) || delta === 0) return false;
        const inList = this._displayedId && this._fileList.cardIndexOf(this._displayedId) >= 0;
        const base = inList ? this._displayedId : this._anchorId ?? this._displayedId;
        if (!base) return false;
        const target = this._fileList.neighbor(base, delta);
        // FILM-01：到端点不环绕，也不发出新的定位请求。
        if (!target) return false;
        this._applyDisplayed(target.photo.id, { mode: "batch", anchor: true, similarityAnchor: true });
        this._requestLocate("navigate");
        return true;
    }

    exchange() {
        if (!this._history.length) return false;
        let target = null;
        while (this._history.length > 0) {
            const id = this._history.pop();
            if (id && this._photoById(id)) {
                target = id;
                break;
            }
        }
        if (!target || target === this._displayedId) return false;
        if (this._displayedId) this._history.push(this._displayedId);
        // FILM-01：切换上一张只恢复照片，不定位。
        this._positioner?.onExchange();
        this._applyDisplayed(target, { mode: "batch", anchor: true, similarityAnchor: true, recordHistory: false });
        return true;
    }

    setSort(key, order) {
        const changed = this._fileList.setSort(key, order);
        // FILM-01：排序只更新列表，不抢用户位置；只把偏移夹到合法范围。
        this._positioner?.onSortOrFilter();
        this.filmResized();
        this._similarity.setPool(this._fileList.cards);
        this._invalidate(["list", "similarity"]);
        return changed;
    }

    setRatingFilter(value) {
        const changed = this._fileList.setRatingFilter(value);
        // FILTER-05：筛选不改主图、不定位；保留偏移并只夹到新范围，候选池随筛选更新。
        this._positioner?.onSortOrFilter();
        this.filmResized();
        this._similarity.setPool(this._fileList.cards);
        this._invalidate(["list", "toolbar", "layout", "similarity"]);
        return changed;
    }

    rate(photoId, rating) {
        const id = photoId ?? this._displayedId;
        if (!id) return Promise.resolve(false);
        const card = this._fileList.cardOf(id);
        if (!card) return Promise.resolve(false);
        const value = clamp(Math.round(finite(Number(rating)) ? Number(rating) : 0), 0, 5);
        return this._ratingWriter.write(card, value).then((ok) => {
            if (!ok) {
                // STATE-03 / FILM-06：失败时星级保持原值，提示条由 RatingWriter 给出。
                this._invalidate(["toast"]);
                return false;
            }
            this._fileList.noteRatingWritten(card.photo.id);
            this._positioner?.onRatingRefresh();
            this._similarity.setPool(this._fileList.cards);
            this._similarity.rated(card.photo.id);
            this._invalidate(["list", "toolbar", "current", "similarity"]);
            return true;
        });
    }

    toggleFilmbar() {
        const next = !this._panes.film;
        this._panes.film = next;
        this._write(SHOW_FILM_KEY, next ? "true" : "false");
        this._similarity.setFilebarVisible(next);
        this._recomputeLayout();
        this._syncProxyAndWantFull();
        if (next) {
            // FILM-04：重新显示胶片栏时显式定位当前项。
            this._positioner?.onShown();
            this._requestLocate("reshow");
        } else {
            this._positioner?.onHidden();
        }
        this._invalidate(["panes", "layout", "viewport", "similarity"]);
        return next;
    }

    toggleControlbar() {
        const next = !this._panes.control;
        this._panes.control = next;
        this._write(SHOW_CONTROLS_KEY, next ? "true" : "false");
        this._recomputeLayout();
        this._syncProxyAndWantFull();
        this._invalidate(["panes", "layout", "viewport"]);
        return next;
    }

    toggleAnalysis() {
        const next = !this._panes.analysis;
        this._panes.analysis = next;
        this._analysis.setVisible(next);
        this._recomputeLayout();
        this._syncProxyAndWantFull();
        this._invalidate(["panes", "layout", "viewport", "analysis"]);
        return next;
    }

    setSimilarityExpanded(value) {
        const next = Boolean(value);
        if (this._settings.similarityPanelExpanded === next) return false;
        return this._applySimilarityExpanded(next, true);
    }

    fit(anchor = null) {
        return this._afterViewport(this._viewport.toggleFit(anchor && finite(anchor.x) ? anchor : null));
    }

    zoomPreset(direction) {
        return this._afterViewport(this._viewport.zoomPreset(direction, this._settings.zoomPresets));
    }

    zoomScale(direction, anchor = null) {
        if (!finite(direction) || direction === 0) return false;
        const factor = direction > 0 ? this._t("image.zoom.step.in") : this._t("image.zoom.step.out");
        return this.zoomBy(factor, anchor);
    }

    zoomBy(factor, anchor = null) {
        return this._afterViewport(this._viewport.zoomBy(factor, anchor));
    }

    pan(dx, dy) {
        return this._afterViewport(this._viewport.panBy(dx, dy));
    }

    centerOn(u, v) {
        // IMAGE-09：以显示朝向归一化点为中心到物理 100%，退出跟随对焦。
        return this._afterViewport(this._viewport.centerOnPoint(u, v));
    }

    toggleEnhance() {
        if (!this._displayedPhoto()) return false;
        const current = this._photoSession.snapshot();
        const applied = this._photoSession.setEnhance(!current.enhance);
        this._invalidate(["toolbar", "image", "analysis"]);
        return applied;
    }

    runCommand(command) {
        if (typeof command !== "string" || command.length === 0) return false;
        if (command === "Open") {
            this.requestOpen("file");
            return true;
        }
        if (NEEDS_PHOTO.has(command) && !this._displayedPhoto()) return false;
        if (command.startsWith("SetRating")) {
            const value = Number(command.slice("SetRating".length));
            if (!finite(value)) return false;
            this.rate(this._displayedId, value);
            return true;
        }
        switch (command) {
            case "Previous":
                return this.navigate(-1);
            case "Next":
                return this.navigate(1);
            case "Exchange":
                return this.exchange();
            case "Fit":
                return this.fit();
            case "ZoomInPreset":
                return this.zoomPreset(1);
            case "ZoomOutPreset":
                return this.zoomPreset(-1);
            case "ZoomInScale":
                return this.zoomScale(1);
            case "ZoomOutScale":
                return this.zoomScale(-1);
            case "ToggleAnalysisView":
                this.toggleAnalysis();
                return true;
            case "ToggleEnhance":
                this.toggleEnhance();
                return true;
            default:
                return false;
        }
    }

    runMenu(id) {
        switch (id) {
            case "open-file":
                return this.requestOpen("file");
            case "open-folder":
                return this.requestOpen("folder");
            case "film":
                return this.toggleFilmbar();
            case "analysis":
                return this.toggleAnalysis();
            case "control":
                return this.toggleControlbar();
            case "enhance":
                return this.toggleEnhance();
            default:
                return false;
        }
    }

    requestOpen(kind = "file") {
        this._emit("request-open", { kind: kind === "folder" ? "folder" : "file" });
        return true;
    }

    keyDown(desc) {
        const result = this._dispatcher.keyDown(desc, { appleKeyboard: this._settings.appleKeyboard });
        if (result.command) this.runCommand(result.command);
        return result;
    }

    keyUp(desc) {
        this._dispatcher.keyUp(desc, { appleKeyboard: this._settings.appleKeyboard });
    }

    blur() {
        this._dispatcher.blur();
    }

    // INPUT-04：壳层把物理 Ctrl 状态报给滚轮解释器，用来区分捏合与用户绑定的 Ctrl+滚轮。
    setModifierState(state = {}) {
        this._wheel.noteModifierKeys({ ctrl: Boolean(state.ctrl) });
        return this._wheel.ctrlDown;
    }

    wheelOnPhoto(desc, anchor = null) {
        if (!desc) return { handled: false };
        const result = this._wheel.feed(desc, { appleKeyboard: this._settings.appleKeyboard });
        if (!result) return { handled: false };
        if (result.kind === "discrete") {
            const command = this._commandMap.commandFor(result.gesture);
            if (!command) return { handled: false };
            for (let step = 0; step < result.steps; step += 1) this.runCommand(command);
            return { handled: true };
        }
        if (result.kind === "precise") {
            if (!this._displayedPhoto()) return { handled: false };
            if (result.pinch) this.zoomBy(result.pinch, anchor);
            // INPUT-03：精确滚动平移照片，方向与内容滚动一致。
            else this.pan(-result.dx, -result.dy);
            return { handled: true };
        }
        return { handled: false };
    }

    notify(text) {
        this._photoSession.toast(text);
    }

    // OPEN-02：隐藏文件与未启用格式不参与列表，选择器等入口用同一判据。
    canOpen(photo) {
        return this._allowed(photo);
    }

    get filmAnimating() {
        return Boolean(this._scroller?.isAnimating);
    }

    // INPUT-01/03：鼠标按键手势（默认中键 = 切换上一张）按同一绑定表派发；未绑定原样放行。
    mouseOnPhoto(desc) {
        if (!desc || this._menuOpen || this._dropdownOpen) return { handled: false };
        const gesture = gestureFromMouse(desc, { appleKeyboard: this._settings.appleKeyboard });
        if (!gesture) return { handled: false };
        const command = this._commandMap.commandFor(gesture);
        if (!command) return { handled: false };
        this.runCommand(command);
        return { handled: true };
    }

    filmUserScrolled() {
        // FILM-03：用户接管滚动，撤销在途动画与排队请求。
        this._positioner?.onUserScroll();
    }

    filmResized() {
        if (!this._positioner) return;
        const axis = this._layout && this._layout.layoutMode === "col" ? "y" : "x";
        this._positioner.onResize(contentLength(this._fileList.count, axis, this._t), this._filmViewportLengthValue());
    }

    bindFilmScroller({ getOffset, setOffset, viewportLength } = {}) {
        if (typeof getOffset !== "function" || typeof setOffset !== "function") {
            throw new TypeError("bindFilmScroller 需要 getOffset 与 setOffset");
        }
        this._filmViewportLength = viewportLength;
        this._scroller?.cancel();
        this._scroller = new FilmScroller({
            t: this._t,
            clock: this._clock,
            getOffset,
            setOffset,
            reducedMotion: () => Boolean(this._reducedMotion()),
        });
        this._positioner = new FilmPositioner({
            scroller: this._scroller,
            geometry: () => this._filmGeometry(),
        });
        const pending = this._pendingFilmLocate;
        this._pendingFilmLocate = null;
        if (pending === "reshow") this._positioner.onReshow();
        else if (pending === "navigate") this._positioner.onNavigate();
        this._positioner.layoutReady();
    }

    layoutReady() {
        this._positioner?.layoutReady();
    }

    setMenuOpen(open) {
        const next = Boolean(open);
        if (next && !this._menuOpen) this._dispatcher.blur();
        this._menuOpen = next;
        this._invalidate(["settings"]);
    }

    setDropdownOpen(open) {
        const next = Boolean(open);
        if (next && !this._dropdownOpen) this._dispatcher.blur();
        this._dropdownOpen = next;
        this._invalidate(["settings"]);
    }

    setTextFocus(focus) {
        this._textFocus = Boolean(focus);
    }

    updateSettings(patch) {
        if (!patch || typeof patch !== "object") return false;
        this._settings = normalizeSettings({ ...this._settings, ...patch }, this._settings);
        this._cachedLimit = this._settings.cacheCount;
        this._trimCache();
        const previousMode = this._layout ? this._layout.layoutMode : null;
        // FILTER-09 / SETTINGS-06：格式与同名合并只重算列表，不重置排序与星级筛选、不定位。
        this._fileList.setFormatOptions({
            formats: this._settings.formats,
            sameNameAsOne: this._settings.sameNameAsOne,
        });
        this._commandMap.setHotkeys(this._settings.hotkeys);
        this._similarity.setParams({
            threshold: this._settings.similarityThreshold,
            maxResults: this._settings.similarityMaxResults,
        });
        this._similarity.setExpanded(this._settings.similarityPanelExpanded);
        this._analysis.setImmediateCompute(this._settings.analysisImmediateCompute);
        this._persistSettings();
        this._recomputeLayout();
        if (previousMode && this._layout.layoutMode !== previousMode) {
            this._positioner?.onAxisChanged();
            this._requestLocate("navigate");
        } else {
            this.filmResized();
        }
        this._syncProxyAndWantFull();
        this._invalidate(["settings", "list", "toolbar", "layout", "viewport", "analysis", "similarity"]);
        return true;
    }

    hoverDetail(kind = null) {
        return this._analysis.hoverDetail(kind);
    }

    clickDiagnostic(u, v) {
        this._analysis.clickDiagnostic(u, v);
    }

    clickAnalysisBlank() {
        this._analysis.clickBlank();
    }

    locateDetail(kind) {
        return this._analysis.locateDetail(kind);
    }

    extractAll() {
        return this._similarity.extractAll();
    }

    // ANALYSIS-07：直方图取当前上屏图像素（壳层从展示表面采样后交给 presenter，面板不可见时忽略）。
    setHistogramSource(pixels, width, height) {
        if (!this._panes.analysis) return;
        this._analysis.setHistogramSource(pixels, width, height);
    }

    simulateMemoryWarning() {
        return this._photoSession.memoryWarning();
    }

    menuModel() {
        const photo = this._displayedPhoto();
        const shortcut = (command) => {
            const bindings = this._commandMap.bindingsOf(command);
            if (!bindings) return null;
            const gesture = bindings.primary ?? bindings.secondary;
            return gesture ? gestureText(gesture, { apple: this._settings.appleKeyboard }) : null;
        };
        // MENU-01@web / SCOPE-01@web：Web 壳不实现工具与设置窗口，因此不返回这两项及其分组。
        return [
            { id: "open-file", group: "open", label: "打开文件…", type: "command", enabled: true, shortcut: shortcut("Open") },
            { id: "open-folder", group: "open", label: "打开文件夹…", type: "command", enabled: true, shortcut: null },
            { id: "film", group: "panes", label: "胶片栏", type: "check", checked: this._panes.film, enabled: true, shortcut: null },
            { id: "analysis", group: "panes", label: "分析栏", type: "check", checked: this._panes.analysis, enabled: true, shortcut: shortcut("ToggleAnalysisView") },
            { id: "control", group: "panes", label: "控制栏", type: "check", checked: this._panes.control, enabled: true, shortcut: null },
            { id: "enhance", group: "preview", label: "增强", type: "check", checked: Boolean(this._sessionSnap.enhance), enabled: Boolean(photo), shortcut: shortcut("ToggleEnhance") },
        ];
    }

    _applySimilarityExpanded(value, persist) {
        this._settings.similarityPanelExpanded = Boolean(value);
        this._similarity.setExpanded(Boolean(value));
        if (persist) this._persistSettings();
        this._recomputeLayout();
        // SIM-07：让位只夹取胶片栏偏移，不主动居中。
        this.filmResized();
        this._syncProxyAndWantFull();
        this._invalidate(["settings", "layout", "viewport", "similarity"]);
        return true;
    }

    _commitFolder({ id, name, single, photos, preferred }) {
        this._photos = Array.isArray(photos) ? photos : [];
        this._folder = { id, name };
        this._singleMode = Boolean(single);
        this._history = [];
        this._fileList.setPhotos(this._photos);
        this._similarity.setPool(this._fileList.cards);
        const card = preferred && this._fileList.cardOf(preferred.id)
            ? this._fileList.cardOf(preferred.id)
            : this._fileList.firstCard();
        this._positioner?.onListReplaced();
        // OPEN-04：目标优先，即使不满足当前星级筛选；否则回退筛选后首项/枚举首组代表。
        this._applyDisplayed(card ? card.photo.id : null, {
            mode: "reset",
            anchor: true,
            similarityAnchor: true,
            recordHistory: false,
        });
        // OPEN-05：列表替换后定位当前项（布局未就绪时排队）。
        this._requestLocate("navigate");
        this._invalidate(["list", "current", "layout", "toolbar"]);
    }

    _openSingle(folderId, photo) {
        this._commitFolder({
            id: folderId,
            name: photo.name,
            single: true,
            photos: [photo],
            preferred: photo,
        });
        return true;
    }

    _applyDisplayed(id, { mode = "batch", anchor = true, similarityAnchor = true, recordHistory = true } = {}) {
        const previous = this._displayedId;
        if (recordHistory && previous && id !== previous) {
            this._history.push(previous);
            if (this._history.length > HISTORY_LIMIT) this._history.shift();
        }
        this._displayedId = id;
        if (anchor) {
            this._anchorId = id;
            this._fileList.setCurrent(id);
        }
        this._showImage(mode);
        if (similarityAnchor) this._similarity.setAnchor(id ? this._photoById(id) : null);
        this._similarity.setDisplayed(id);
        this._invalidate(["current", "viewport", "image", "toolbar", "analysis", "list", "similarity"]);
    }

    _showImage(mode) {
        const photo = this._displayedPhoto();
        if (!photo) {
            this._viewport.setImage(null);
            this._photoSession.show(null);
            this._analysis.setPhoto(null);
            return;
        }
        const size = displaySize(photo.width, photo.height, photo.orientation);
        this._viewport.setImage({
            width: size.width,
            height: size.height,
            focus: photo.focus ? { x: photo.focus.x, y: photo.focus.y } : null,
        }, { mode });
        const proxy = this._viewport.proxyTargetLongEdge();
        this._photoSession.show(photo, {
            proxyLongEdge: proxy,
            wantFull: this._viewport.needsFull(proxy) || this._panes.analysis,
        });
        this._analysis.setPhoto(photo);
    }

    _afterViewport(changed) {
        if (!changed) return false;
        this._syncWantFull();
        this._invalidate(["viewport", "image"]);
        return true;
    }

    _syncWantFull() {
        const proxy = this._viewport.proxyTargetLongEdge();
        this._photoSession.setWantFull(this._viewport.needsFull(proxy) || this._panes.analysis);
    }

    _syncProxyAndWantFull() {
        const proxy = this._viewport.proxyTargetLongEdge();
        this._photoSession.setProxyLongEdge(proxy);
        this._photoSession.setWantFull(this._viewport.needsFull(proxy) || this._panes.analysis);
    }

    _displayedPhoto() {
        return this._displayedId ? this._photoById(this._displayedId) : null;
    }

    _photoById(id) {
        if (!id) return null;
        return this._photos.find((photo) => photo.id === id) ?? null;
    }

    _photoInFolder(folder, target) {
        if (!target || !folder || !Array.isArray(folder.photos)) return null;
        const id = typeof target === "string"
            ? target
            : target.photo?.id ?? target.id ?? target.key ?? null;
        if (!id) return null;
        return folder.photos.find((photo) => photo.id === id) ?? null;
    }

    _findPhoto(photoId) {
        const id = typeof photoId === "string" ? photoId : photoId?.id;
        if (!id) return null;
        const local = this._photoById(id);
        if (local) return local;
        for (const folder of this._library.folders) {
            const found = folder.photos?.find((photo) => photo.id === id);
            if (found) return found;
        }
        return null;
    }

    _allowed(photo) {
        if (!photo || photo.name.startsWith(".")) return false;
        const ext = String(photo.ext ?? "").toLowerCase();
        const format = this._settings.formats.find((entry) => entry.exts.some((value) => value.toLowerCase() === ext));
        return Boolean(format && format.enabled !== false);
    }

    _filmGeometry() {
        if (!this._panes.film) return null;
        const axis = this._layout && this._layout.layoutMode === "col" ? "y" : "x";
        const count = this._fileList.count;
        const index = this._anchorId ? this._fileList.cardIndexOf(this._anchorId) : -1;
        if (count <= 0 || index < 0) return null;
        return {
            axis,
            count,
            index,
            viewportLength: this._filmViewportLengthValue(),
        };
    }

    _filmViewportLengthValue() {
        // FILM-02：定位按最新布局几何计算；布局在动作内同步重算，壳层回调可能仍是上一帧的尺寸。
        const film = this._layout?.film;
        if (film && this._panes.film) {
            const length = this._layout.layoutMode === "col" ? film.scroll.h : film.scroll.w;
            if (finite(length) && length > 0) return length;
        }
        if (typeof this._filmViewportLength === "function") {
            try {
                const value = Number(this._filmViewportLength());
                return finite(value) && value > 0 ? value : 0;
            } catch {
                return 0;
            }
        }
        const value = Number(this._filmViewportLength);
        return finite(value) && value > 0 ? value : 0;
    }

    _requestLocate(kind) {
        if (this._positioner) {
            this._pendingFilmLocate = null;
            if (kind === "reshow") this._positioner.onReshow();
            else this._positioner.onNavigate();
            return;
        }
        this._pendingFilmLocate = kind === "reshow" ? "reshow" : "navigate";
    }

    _scene() {
        const toolbar = this._buildToolbar();
        const similarity = this._similarity.snapshot();
        return {
            width: this._window.width,
            height: this._window.height,
            layoutMode: this._settings.layoutMode,
            showFilmbar: this._panes.film,
            showControlbar: this._panes.control,
            showAnalysis: this._panes.analysis,
            similarityExpanded: similarity.visible,
            similarityItemCount: similarity.items.length,
            photoCount: this._fileList.count,
            countText: `共 ${this._fileList.count} 张`,
            exifCount: toolbar.exif.length,
            showRating: this._settings.showRating,
            commandCount: toolbar.commands.length,
            analysisTileCount: this._analysis.tiles().length,
        };
    }

    _recomputeLayout() {
        this._layout = computeLayout(this._scene(), {
            t: this._t,
            profile: this._profile,
            measureText: this._measureText,
        });
        const pane = this._layout.panes.photoPane;
        this._viewport.setViewSize(pane.w, pane.h);
        this._layoutDirty = false;
    }

    _buildToolbar() {
        const photo = this._displayedPhoto();
        const exif = this._settings.exifItems
            .filter((item) => item.enabled !== false)
            .map((item) => {
                const value = formatExifValue(item.id, photo);
                return {
                    id: item.id,
                    label: item.label,
                    value,
                    tooltip: `${item.label} · ${value}`,
                };
            });
        const rating = {
            visible: this._settings.showRating !== false,
            value: photo ? photo.rating : 0,
            enabled: Boolean(photo),
        };
        // TOOLBAR-04：命令列表完全取设置的显示项与顺序，不写死第二份列表。
        const commands = this._settings.hotkeys
            .filter((row) => row.display)
            .map((row) => {
                const item = {
                    command: row.command,
                    name: row.name,
                    enabled: row.command === "Open" || Boolean(photo),
                    tooltip: tooltipText(row.command, row.name, this._commandMap, {
                        apple: this._settings.appleKeyboard,
                    }),
                };
                if (row.command === "ToggleAnalysisView") item.active = this._panes.analysis;
                if (row.command === "ToggleEnhance") item.active = Boolean(this._sessionSnap.enhance);
                return item;
            });
        return { exif, rating, commands };
    }

    _buildState() {
        const similarity = this._similarity.snapshot();
        this._state = {
            window: { ...this._window },
            layout: this._layout,
            folder: this._folder ? { ...this._folder } : null,
            singleMode: this._singleMode,
            photo: this._displayedPhoto(),
            cards: this._fileList.cards,
            count: this._fileList.count,
            countText: `共 ${this._fileList.count} 张`,
            sortKey: this._fileList.sortKey,
            sortOrder: this._fileList.sortOrder,
            ratingFilter: this._fileList.ratingFilter,
            listVersion: this._fileList.version,
            anchorId: this._anchorId,
            displayedId: this._displayedId,
            panes: { ...this._panes },
            viewport: this._viewportState(),
            session: this._sessionSnap,
            analysis: this._analysisState(),
            similarity,
            toolbar: this._buildToolbar(),
            menuOpen: this._menuOpen,
            dropdownOpen: this._dropdownOpen,
            cached: new Set(this._cached),
        };
        return this._state;
    }

    _viewportState() {
        const snapshot = this._viewport.snapshot();
        const navigator = this._viewport.navigator();
        return {
            ...snapshot,
            imageRect: this._viewport.imageRect(),
            percentText: snapshot.percent,
            // IMAGE-06：可见框只表达非适应态，叠加设置开关与有图条件。
            navigator: {
                ...navigator,
                visible: navigator.visible && snapshot.hasImage && !snapshot.isFit && this._settings.showZoomIndicator !== false,
            },
        };
    }

    _detailGeometry(kind) {
        const photo = this._displayedPhoto();
        const layout = this._layout?.analysis;
        if (!photo || !layout || !kind) return null;
        const source = displaySize(photo.width, photo.height, photo.orientation);
        const args = [kind, layout.tileSize, this._viewport.snapshot().dpr ?? 1, source];
        return {
            source,
            crop: this._analysis.detailCrop(...args),
            highlight: this._analysis.highlightRect(...args),
        };
    }

    _analysisState() {
        const crosshair = this._analysis.crosshair;
        const hoverKind = this._analysis.hoverKind;
        return {
            focusDetail: this._detailGeometry("focus"),
            centerDetail: this._detailGeometry("center"),
            // ANALYSIS-04：主图绿框只在悬停/定格细节预览时存在，源像素矩形由壳层换算到视口。
            highlight: hoverKind ? this._detailGeometry(hoverKind)?.highlight ?? null : null,
            visible: this._panes.analysis,
            tiles: this._analysis.tiles(),
            crosshair: crosshair ? { ...crosshair } : null,
            hoverKind: this._analysis.hoverKind,
            cosine: this._analysis.cosineMap(),
            diagnostics: this._analysis.diagnostics(),
            histogram: this._analysis.histogram(),
            route: this._analysis.route,
            loading: this._analysis.routeState === "loading",
            error: this._analysis.error,
        };
    }

    _onSession(snapshot) {
        const previous = this._sessionSnap;
        this._sessionSnap = snapshot;
        const regions = new Set();
        const errorChanged = previous.error?.title !== snapshot.error?.title
            || previous.error?.detail !== snapshot.error?.detail;
        const imageChanged = previous.photoId !== snapshot.photoId
            || previous.surface !== snapshot.surface
            || previous.original !== snapshot.original
            || previous.tier !== snapshot.tier
            || previous.loadingHint !== snapshot.loadingHint
            || previous.busy !== snapshot.busy
            || previous.toastBottom !== snapshot.toastBottom
            || errorChanged;
        if (imageChanged) regions.add("image");
        if ((previous.toast?.id ?? null) !== (snapshot.toast?.id ?? null)) regions.add("toast");
        if (previous.enhance !== snapshot.enhance) {
            this._analysis.setEnhanced(snapshot.enhance);
            regions.add("toolbar");
            regions.add("analysis");
        }
        if (Boolean(previous.surface) !== Boolean(snapshot.surface)) {
            this._analysis.setDisplaySurfaceReady(Boolean(snapshot.surface));
            regions.add("analysis");
        }
        if (snapshot.tier === "full" && snapshot.photoId) {
            this._rememberCached(snapshot.photoId);
            regions.add("list");
        }
        if (regions.size > 0) this._invalidate([...regions]);
    }

    _onAnalysisChange() {
        const regions = ["analysis"];
        const count = this._analysis.tiles().length;
        if (this._layout && count !== this._analysisTileCount) {
            // ANALYSIS-06：EXIF 异步到达后瓦片数变化，分析栏交叉轴与主图分区随之重算。
            this._analysisTileCount = count;
            this._recomputeLayout();
            this._syncProxyAndWantFull();
            regions.push("layout", "viewport");
        } else if (count !== this._analysisTileCount) {
            this._analysisTileCount = count;
            this._layoutDirty = true;
        }
        this._invalidate(regions);
    }

    _onSimilarityChange() {
        const snapshot = this._similarity.snapshot();
        const layoutChanged = snapshot.visible !== this._similarityLayout.visible
            || snapshot.items.length !== this._similarityLayout.count;
        this._similarityLayout = { visible: snapshot.visible, count: snapshot.items.length };
        const regions = ["similarity"];
        if (layoutChanged) {
            if (this._layout) {
                // SIM-07：行布局面板宽度由相似项数决定，变化时重算让位并夹取胶片栏偏移。
                this._recomputeLayout();
                this.filmResized();
                this._syncProxyAndWantFull();
                regions.push("layout", "viewport");
            } else {
                this._layoutDirty = true;
            }
        }
        this._invalidate(regions);
    }

    _rememberCached(photoId) {
        this._cached.delete(photoId);
        this._cached.add(photoId);
        while (this._cached.size > this._cachedLimit) {
            const oldest = this._cached.values().next().value;
            this._cached.delete(oldest);
        }
    }

    _trimCache() {
        while (this._cached.size > this._cachedLimit) {
            const oldest = this._cached.values().next().value;
            this._cached.delete(oldest);
        }
    }

    _invalidate(regions) {
        let layoutTouched = false;
        for (const region of regions) {
            if (!region) continue;
            this._pendingRegions.add(region);
            if (LAYOUT_REGIONS.has(region)) layoutTouched = true;
        }
        if (layoutTouched) {
            this._layoutDirty = true;
            this._pendingRegions.add("layout");
        }
        this._state = null;
        if (this._pendingRegions.size === 0) return;
        if (!this._flushScheduled) {
            this._flushScheduled = true;
            queueMicrotask(() => this._dispatch());
        }
    }

    _dispatch() {
        this._flushScheduled = false;
        if (this._pendingRegions.size === 0) return;
        const regions = this._pendingRegions;
        this._pendingRegions = new Set();
        const listeners = this._listeners.get("change");
        if (!listeners) return;
        for (const listener of listeners) listener(regions);
    }

    _emit(name, payload) {
        const listeners = this._listeners.get(name);
        if (!listeners) return;
        for (const listener of listeners) listener(payload);
    }

    _loadSettings(overrides) {
        let raw = null;
        try {
            const text = this._read(SETTINGS_KEY);
            if (text) raw = JSON.parse(text);
        } catch {
            raw = null;
        }
        let settings = normalizeSettings(raw);
        if (overrides && typeof overrides === "object") {
            settings = normalizeSettings({ ...settings, ...overrides }, settings);
        }
        return settings;
    }

    _persistSettings() {
        try {
            this._write(SETTINGS_KEY, JSON.stringify(this._settings));
        } catch {
            // SETTINGS-05：持久化失败不得清空设置文档或阻断主屏。
        }
    }

    _read(key) {
        try {
            return this._storage ? this._storage.get(key) : null;
        } catch {
            return null;
        }
    }

    _write(key, value) {
        try {
            if (this._storage) this._storage.set(key, String(value));
        } catch {
            // SHELL-04：偏好存储失败按默认值继续，不阻塞主屏。
        }
    }
}

export function createApp(options) {
    return new MainApp(options);
}
