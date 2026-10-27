import { createTokens } from "./core/tokens.js";
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
import { mountPicker } from "./shell/picker.js";
import { mountRatingPopover } from "./shell/ratingsources.js";
import { mountDemoPanel } from "./demo/panel.js";

const params = new URLSearchParams(location.search);
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
    file: el("div", { class: "pv-pane pv-pane-file" }),
    fileDivider: el("div", { class: "pv-divider" }),
    photo: el("div", { class: "pv-pane pv-pane-photo" }),
    analysisDivider: el("div", { class: "pv-divider pv-divider-analysis" }),
    analysis: el("div", { class: "pv-pane pv-pane-analysis" }),
    controlDivider: el("div", { class: "pv-divider" }),
    control: el("div", { class: "pv-pane pv-pane-control" }),
};
// INPUT-12：焦点顺序沿可见分区（文件栏 → 主图 → 分析栏 → 控制栏）。
windowEl.append(panes.file, panes.fileDivider, panes.photo, panes.analysisDivider, panes.analysis, panes.controlDivider, panes.control);
const overlay = el("div", { class: "pv-overlay" });
windowEl.append(overlay);

const menu = createMenu({ host: windowEl, t, onOpenChange: (open) => app.setMenuOpen(open) });
// TOOLBAR-04@web：data-tooltip 的全局委托，替换浏览器原生 title。
const tooltip = createTooltip({ host: windowEl, t });
const ctx = { app, t, clock, thumbs, decoder, library, measureText, menu, tooltip, overlay, windowEl, reducedMotion };
const ratingPopover = mountRatingPopover(windowEl, ctx);
ctx.ratingPopover = ratingPopover;

const components = [
    mountFilterBar(panes.file, ctx),
    mountFilmstrip(panes.file, ctx),
    mountSimilarity(panes.file, ctx),
    mountPhoto(panes.photo, ctx),
    mountAnalysis(panes.analysis, ctx),
    mountControlBar(panes.control, ctx),
];
const picker = mountPicker(windowEl, ctx, library);
components.push(picker);
// RATING-04：来源浮窗最后更新，确保读到的卡片/控制栏节点已就位。
components.push(ratingPopover);

function layoutPanes(state) {
    const p = state.layout.panes;
    place(panes.file, state.panes.film ? p.filePane : null);
    place(panes.fileDivider, state.panes.film ? p.fileDivider : null);
    place(panes.photo, p.photoPane);
    place(panes.analysisDivider, state.panes.analysis ? p.analysisDivider : null);
    place(panes.analysis, state.panes.analysis ? p.analysisPane : null);
    place(panes.controlDivider, state.panes.control ? p.controlDivider : null);
    place(panes.control, state.panes.control ? p.controlPane : null);
    windowEl.dataset.layout = state.layout.layoutMode;
}

function render(regions) {
    const state = app.state;
    if (!regions || regions.has("layout") || regions.has("panes") || regions.has("window")) layoutPanes(state);
    for (const component of components) component.update(state, regions);
}

app.on("change", (regions) => render(regions));
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

const sizeOverride = { width: null, height: null };
function syncWindowSize() {
    const width = sizeOverride.width ?? stage.clientWidth;
    const height = sizeOverride.height ?? stage.clientHeight;
    windowEl.style.width = `${width}px`;
    windowEl.style.height = `${height}px`;
    app.setWindowSize(width, height);
}
new ResizeObserver(syncWindowSize).observe(stage);
matchMedia(`(resolution: ${devicePixelRatio}dppx)`).addEventListener?.("change", () => app.setDpr(devicePixelRatio));
reducedMotionQuery.addEventListener?.("change", () => render(null));

const shell = {
    app,
    t,
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
