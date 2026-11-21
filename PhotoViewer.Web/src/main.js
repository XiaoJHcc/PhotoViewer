import { createTokens } from "./core/tokens.js";
import { normalizeEdition } from "./core/edition.js";
import baseTokens from "./tokens.g.js";
import { systemClock } from "./core/clock.js";
import { createLibrary } from "./core/library.js";
import { createApp } from "./core/app.js";
import { createDecoder } from "./render/decoder.js";
import { createStore } from "./render/store.js";
import { createThumbnailQueue } from "./render/thumbs.js";
import { el, place, applyTokenVariables, createTextMeasurer } from "./shell/dom.js";
import { createMenu } from "./shell/menu.js";
import { createTooltip } from "./shell/tooltip.js";
import { mountFilterBar } from "./shell/filterbar.js";
import { mountFilmstrip } from "./shell/filmstrip.js";
import { mountSimilarity } from "./shell/similarity.js";
import { mountPhoto } from "./shell/photo.js";
import { mountAnalysis } from "./shell/analysis.js";
import { mountControlBar } from "./shell/controlbar.js";
import { mountExifBar } from "./shell/exifbar.js";
import { mountPicker } from "./shell/picker.js";
import { mountRatingPopover } from "./shell/ratingsources.js";
import { mountDemoPanel } from "./demo/panel.js";

const params = new URLSearchParams(location.search);
const edition = normalizeEdition(params.get("edition"));
const t = createTokens(params.get("platform") ?? "web");
const clock = systemClock();
const library = createLibrary();
const decoder = createDecoder({ clock });
const store = createStore({ clock });
const storage = {
    get: (key) => { try { return localStorage.getItem(key); } catch { return null; } },
    set: (key, value) => { try { localStorage.setItem(key, value); } catch { /* 隐私模式下不持久化 */ } },
};
const reducedMotionQuery = matchMedia("(prefers-reduced-motion: reduce)");
let reducedMotionOverride = null;
const reducedMotion = () => reducedMotionOverride ?? reducedMotionQuery.matches;

const body = document.getElementById("app");
body.dataset.platform = t.profile;
body.dataset.edition = edition;
const demo = params.has("demo");
body.classList.toggle("is-demo", demo);

const windowEl = el("div", { class: "pv-window", role: "application", "aria-label": "PhotoViewer", tabindex: "-1" });
const stage = el("div", { class: "pv-stage" }, windowEl);
body.append(stage);
applyTokenVariables(document.documentElement, t, Object.keys(baseTokens));

const measureText = createTextMeasurer();
const thumbs = createThumbnailQueue({ t, dpr: devicePixelRatio, decoder, clock });
const app = createApp({
    t,
    clock,
    profile: t.profile,
    edition,
    library,
    decoder,
    store,
    storage,
    dpr: devicePixelRatio,
    reducedMotion,
    measureText,
    // ANALYSIS-09：未提取回退让位时等待缩略图通道空闲。
    thumbnailsBusy: () => thumbs.stats().running > 0 || thumbs.stats().pending > 0,
});

const panes = {
    file: el("div", { class: "pv-pane pv-pane-file pv-layer" }),
    photo: el("div", { class: "pv-pane pv-pane-photo" }),
    analysis: el("div", { class: "pv-pane pv-pane-analysis" }),
    control: el("div", { class: "pv-pane pv-pane-control" }),
    filter: el("div", { class: "pv-filter-layer pv-layer" }),
    exif: el("div", { class: "pv-exif-layer pv-layer" }),
    material: el("div", { class: "pv-material-layer pv-layer" }),
    divider: el("div", { class: "pv-divider-layer pv-layer" }),
};
// INPUT-12：焦点顺序沿主图 → 文件栏 → 分析栏 → 控制栏 → 筛选 → EXIF；图层只做分区承载。
// INPUT-12：焦点顺序沿文件栏 → 主图 → 分析栏 → 控制栏；图层叠放交给 z-index。
windowEl.append(panes.filter, panes.file, panes.photo, panes.analysis, panes.control, panes.exif, panes.material, panes.divider);
const overlay = el("div", { class: "pv-overlay" });
windowEl.append(overlay);

const menu = createMenu({ host: windowEl, t, onOpenChange: (open) => app.setMenuOpen(open) });
// TOOLBAR-04@web：data-tooltip 的全局委托，替换浏览器原生 title。
const tooltip = createTooltip({ host: windowEl, t });
const ctx = { app, t, clock, thumbs, decoder, library, measureText, menu, tooltip, overlay, windowEl, reducedMotion };
const ratingPopover = mountRatingPopover(windowEl, ctx);
ctx.ratingPopover = ratingPopover;

const components = [
    mountFilterBar(panes.filter, ctx),
    mountFilmstrip(panes.file, ctx),
];
// EDITION-02：标准版不创建相似面板组件。
if (app.capabilities.similarity) components.push(mountSimilarity(panes.file, ctx));
const controlBar = mountControlBar(panes.control, ctx);
const exifBar = mountExifBar(panes.exif);
const exifRelay = {
    update(state, regions) {
        exifBar.setHost(state.layout.exif?.host === "control" ? controlBar.exifHost : panes.exif);
        exifBar.update(state, regions);
    },
};
components.push(
    mountPhoto(panes.photo, ctx),
    mountAnalysis(panes.analysis, ctx),
    controlBar,
    exifRelay,
);
const picker = mountPicker(windowEl, ctx, library);
components.push(picker);
// RATING-04：来源浮窗最后更新，确保读到的卡片/控制栏节点已就位。
components.push(ratingPopover);

const materialPool = [];
const dividerPool = [];

function layoutPanes(state) {
    const p = state.layout.panes;
    place(panes.file, state.panes.film ? p.photoView : null);
    panes.file.hidden = !state.panes.film;
    place(panes.photo, p.photoView);
    place(panes.analysis, state.panes.analysis ? p.analysisPane : null);
    const phoneLandscape = state.layout.form === "phone" && state.layout.orientation === "landscape";
    const showControlHost = state.panes.control || (phoneLandscape && Boolean(state.layout.nav?.visible));
    place(panes.control, showControlHost ? (phoneLandscape ? p.photoView : p.controlPane) : null);
    place(panes.filter, p.photoView);
    place(panes.exif, p.photoView);
    place(panes.material, p.photoView);
    place(panes.divider, p.photoView);
    syncSubstrate(materialPool, panes.material, state.layout.materials, "pv-substrate");
    syncSubstrate(dividerPool, panes.divider, state.layout.dividers, "pv-divider");
    windowEl.dataset.layout = state.layout.layoutMode;
    windowEl.dataset.form = state.layout.form;
    windowEl.dataset.orientation = state.layout.orientation ?? "";
}

function syncSubstrate(pool, host, entries, className) {
    const list = Array.isArray(entries) ? entries : [];
    while (pool.length < list.length) {
        const node = el("div", { class: className });
        node.hidden = true;
        host.append(node);
        pool.push(node);
    }
    pool.forEach((node, index) => {
        const entry = list[index];
        if (!entry) {
            node.hidden = true;
            return;
        }
        node.hidden = false;
        node.className = `${className} is-${entry.kind}`;
        place(node, entry.frame);
    });
}

function render(regions) {
    const state = app.state;
    if (!regions || regions.has("layout") || regions.has("panes") || regions.has("window")) layoutPanes(state);
    for (const component of components) component.update(state, regions);
}

// STATE-05：目录不可用时停止在途缩略图解码，恢复后由胶片栏重新请求。
let thumbnailsPaused = false;
app.on("change", (regions) => {
    if (regions.has("folder")) {
        const paused = app.state.folderAccess.available === false;
        if (paused && !thumbnailsPaused) thumbs.cancelAll();
        thumbnailsPaused = paused;
    }
    render(regions);
});
app.on("request-open", ({ kind }) => picker.show(kind));

// MENU-01/MENU-03：主图菜单是窗口级入口；桌面右键在主图区弹出（文件栏/控制栏/分析栏继承同一菜单，菜单内自管键盘）。
windowEl.addEventListener("contextmenu", (event) => {
    event.preventDefault();
    if (event.target.closest(".pv-menu-layer, .pv-picker")) return;
    if (event.defaultPrevented && event.pvHandled) return;
    const rect = windowEl.getBoundingClientRect();
    const items = [];
    let group = null;
    for (const item of app.menuModel()) {
        if (group !== null && item.group !== group) items.push({ type: "separator" });
        group = item.group;
        items.push(item);
    }
    menu.open(items, { x: event.clientX - rect.left, y: event.clientY - rect.top }, {
        label: "主图菜单",
        focusFirst: false,
        onSelect: (id) => app.runMenu(id),
    });
});

function isTextTarget(target) {
    return target instanceof HTMLElement && (target.isContentEditable || /^(INPUT|TEXTAREA|SELECT)$/.test(target.tagName));
}

// INPUT-01/02/11：全局快捷键只在无编辑焦点、无菜单时派发；未绑定按键原样放行。
window.addEventListener("keydown", (event) => {
    app.setModifierState({ ctrl: event.ctrlKey });
    if (event.defaultPrevented) return;
    if (isTextTarget(event.target) && !event.target.closest(".pv-window")) return;
    app.setTextFocus(isTextTarget(event.target));
    const result = app.keyDown({
        code: event.code,
        key: event.key,
        ctrlKey: event.ctrlKey,
        altKey: event.altKey,
        shiftKey: event.shiftKey,
        metaKey: event.metaKey,
        repeat: event.repeat,
    });
    if (result.handled) event.preventDefault();
});
window.addEventListener("keyup", (event) => {
    app.setModifierState({ ctrl: event.ctrlKey });
    app.keyUp({ code: event.code, ctrlKey: event.ctrlKey, altKey: event.altKey, shiftKey: event.shiftKey, metaKey: event.metaKey });
});
window.addEventListener("blur", () => app.blur());
document.addEventListener("focusin", (event) => app.setTextFocus(isTextTarget(event.target)));

const sizeOverride = {
    width: Number.isFinite(Number(params.get("width"))) && Number(params.get("width")) > 0 ? Number(params.get("width")) : null,
    height: Number.isFinite(Number(params.get("height"))) && Number(params.get("height")) > 0 ? Number(params.get("height")) : null,
};
let safeAreaOverride = null;
const safeParam = params.get("safe");
if (safeParam) {
    const [top, right, bottom, left] = safeParam.split(",").map((value) => Number(value) || 0);
    safeAreaOverride = { top, right, bottom, left };
}
if (params.get("layout")) app.setLayoutModeOverride(params.get("layout"));
if (params.get("film") === "0" && app.state.panes.film) app.toggleFilmbar({ persist: false });
if (params.get("control") === "0" && app.state.panes.control) app.toggleControlbar({ persist: false });
if (params.get("analysis") === "1" && !app.state.panes.analysis) app.toggleAnalysis({ persist: false });

function readSafeArea() {
    if (safeAreaOverride) return { ...safeAreaOverride, source: "injected" };
    const style = getComputedStyle(document.documentElement);
    const value = (name) => {
        const number = Number.parseFloat(style.getPropertyValue(name));
        return Number.isFinite(number) && number > 0 ? number : 0;
    };
    return {
        top: value("--pv-safe-top"),
        right: value("--pv-safe-right"),
        bottom: value("--pv-safe-bottom"),
        left: value("--pv-safe-left"),
        source: "env",
    };
}

function syncWindowSize() {
    const width = sizeOverride.width ?? stage.clientWidth;
    const height = sizeOverride.height ?? stage.clientHeight;
    windowEl.style.width = `${width}px`;
    windowEl.style.height = `${height}px`;
    app.setSafeArea(readSafeArea(), safeAreaOverride ? "injected" : "env");
    app.setWindowSize(width, height);
}
new ResizeObserver(syncWindowSize).observe(stage);
matchMedia(`(resolution: ${devicePixelRatio}dppx)`).addEventListener?.("change", () => app.setDpr(devicePixelRatio));
reducedMotionQuery.addEventListener?.("change", () => render(null));

const shell = {
    app,
    t,
    edition,
    library,
    store,
    decoder,
    render,
    setWindowSize(width, height) {
        sizeOverride.width = width;
        sizeOverride.height = height;
        syncWindowSize();
    },
    setReducedMotion(value) {
        reducedMotionOverride = value;
    },
    setSafeArea(area) {
        safeAreaOverride = area ? { top: area.top ?? 0, right: area.right ?? 0, bottom: area.bottom ?? 0, left: area.left ?? 0 } : null;
        syncWindowSize();
    },
    safeArea() {
        return app.state.window && app.state.layout ? app.state.layout.safe : null;
    },
    safeAreaSource() {
        return safeAreaOverride ? "injected" : "env";
    },
};
globalThis.pv = shell;

if (demo) mountDemoPanel(body, shell);

syncWindowSize();
const initialFolder = params.has("empty") ? null : (params.get("folder") ?? "harbor");
if (initialFolder) app.openFolder(initialFolder);
render(null);

const check = params.get("check");
if (check) {
    const { runChecks } = await import("./checks/index.js");
    runChecks(check, shell);
}
