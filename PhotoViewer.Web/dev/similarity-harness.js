import { createTokens } from "../src/core/tokens.js";
import baseTokens from "../src/tokens.g.js";
import { systemClock } from "../src/core/clock.js";
import { createLibrary } from "../src/core/library.js";
import { createApp } from "../src/core/app.js";
import { createDecoder } from "../src/render/decoder.js";
import { createStore } from "../src/render/store.js";
import { createThumbnailQueue } from "../src/render/thumbs.js";
import { el, place, applyTokenVariables, createTextMeasurer } from "../src/shell/dom.js";
import { createMenu } from "../src/shell/menu.js";
import { mountSimilarity } from "../src/shell/similarity.js";

const t = createTokens("web");
applyTokenVariables(document.documentElement, t, Object.keys(baseTokens));

const clock = systemClock();
const library = createLibrary();
const decoder = createDecoder({ clock });
const store = createStore({ clock });
const measureText = createTextMeasurer();
const storage = { get: () => null, set: () => {} };
const app = createApp({
    t,
    clock,
    profile: "web",
    library,
    decoder,
    store,
    storage,
    dpr: devicePixelRatio,
    reducedMotion: () => false,
    measureText,
});
const thumbs = createThumbnailQueue({ t, dpr: devicePixelRatio, decoder, clock });

const stage = document.getElementById("stage");
const windowEl = el("div", { class: "pv-window", role: "application", "aria-label": "PhotoViewer harness" });
const contextStrip = el("div", { class: "h-context" });
const host = el("div", { class: "pv-pane pv-pane-file h-host" });
windowEl.append(contextStrip, host);
stage.append(windowEl);

const menu = createMenu({ host: windowEl, t, onOpenChange: (open) => app.setMenuOpen(open) });
const ctx = {
    app,
    t,
    clock,
    thumbs,
    decoder,
    library,
    measureText,
    menu,
    overlay: el("div"),
    windowEl,
    reducedMotion: () => false,
};
const similarity = mountSimilarity(host, ctx);

const status = document.getElementById("status");
const toggle = document.querySelector('[data-action="toggle"]');

function render(regions) {
    const state = app.state;
    const panes = state.layout.panes;
    windowEl.style.width = `${state.window.width}px`;
    windowEl.style.height = `${state.window.height}px`;
    place(host, panes.filePane);
    if (state.layout.layoutMode === "col") {
        place(contextStrip, { x: 0, y: 0, w: t("shell.filebar.content.width"), h: panes.filePane.h });
    } else {
        place(contextStrip, { x: 0, y: 0, w: panes.filePane.w, h: state.layout.film.scroll.y });
    }
    similarity.update(state, regions);
    const s = state.similarity;
    status.textContent = [
        `${state.layout.layoutMode} ${state.window.width}×${state.window.height}`,
        `visible=${s.visible}`,
        `state=${s.state}`,
        `items=${s.items.length}`,
        `unextracted=${s.unextracted}`,
        `action=${s.action.visible}`,
        `progress=${s.progress ? `${s.progress.done}/${s.progress.total}` : "-"}`,
        `error=${s.error ?? "-"}`,
    ].join(" · ");
    toggle.textContent = app.settings.similarityPanelExpanded ? "收起面板" : "展开面板";
}

app.on("change", render);

document.querySelector(".h-controls").addEventListener("click", (event) => {
    const action = event.target.dataset?.action;
    if (!action) return;
    switch (action) {
        case "col":
            app.setWindowSize(1200, 850);
            break;
        case "compact":
            app.setWindowSize(1200, 640);
            break;
        case "row":
            app.setWindowSize(1000, 1000);
            break;
        case "narrow":
            app.setWindowSize(620, 820);
            break;
        case "harbor":
            app.openFolder("harbor");
            break;
        case "night":
            app.openFolder("night");
            break;
        case "empty":
            app.openFolder("empty");
            break;
        case "toggle":
            app.setSimilarityExpanded(!app.settings.similarityPanelExpanded);
            break;
        case "next":
            app.navigate(1);
            break;
        case "extract":
            app.extractAll();
            break;
        case "fail":
            store.failNext = 3;
            app.extractAll();
            break;
        default:
            break;
    }
});

const params = new URLSearchParams(location.search);
const SIZES = {
    col: [1200, 850],
    compact: [1200, 640],
    row: [1000, 1000],
    narrow: [620, 820],
};

app.setWindowSize(...(SIZES[params.get("size")] ?? SIZES.col));
app.openFolder(params.get("folder") ?? "harbor");
app.setSimilarityExpanded(params.get("panel") !== "0");
render(null);

const run = params.get("run");
if (run === "extract") app.extractAll();
if (run === "fail") {
    store.failNext = 3;
    app.extractAll();
}
if (params.has("select")) {
    setTimeout(() => {
        const item = app.state.similarity.items[0];
        if (item) app.selectSimilar(item.card.photo.id);
    }, Number(params.get("select")) || 600);
}

globalThis.pv = { app, store, render };


