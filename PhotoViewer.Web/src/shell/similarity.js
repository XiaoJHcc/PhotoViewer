import { el, place, setText, touches } from "./dom.js";
import { createCard } from "./card.js";

const KEYBOARD_STEP = 40;

export function mountSimilarity(host, ctx) {
    const { app, t } = ctx;

    const list = el("div", {
        class: "pv-sim-list",
        role: "list",
        tabindex: "0",
        "aria-label": "相似照片",
    });
    const emptyText = el("div", { class: "pv-sim-empty-text", "aria-live": "polite" });
    const empty = el("div", { class: "pv-sim-empty" }, emptyText);

    const caption = el("div", { class: "pv-sim-caption" });
    const button = el("button", { class: "pv-sim-action", type: "button" });
    const progressFill = el("div", { class: "pv-sim-progress-fill" });
    const progressText = el("span", { class: "pv-sim-progress-text" });
    const progress = el("div", {
        class: "pv-sim-progress",
        role: "progressbar",
        "aria-label": "提取进度",
        "aria-valuemin": "0",
    }, progressFill, progressText);
    const error = el("div", { class: "pv-sim-error", role: "status" });
    const actionBlock = el("div", { class: "pv-sim-action-block" }, button, progress, error);
    const dock = el("div", { class: "pv-sim-dock" }, caption);

    const panel = el("div", {
        class: "pv-sim",
        role: "region",
        "aria-label": "相似照片",
    }, list, empty, dock);
    host.append(panel);

    button.addEventListener("click", () => app.extractAll());

    const cards = new Map();
    let itemKeys = "";

    function panelRect(state) {
        const layout = state.layout;
        const sim = layout?.similarity;
        const pane = layout?.panes?.filePane;
        // SIM-01：面板可见性只由共享状态决定，不可见时不占位。
        if (!sim || !pane || !sim.expanded || !state.similarity.visible) return null;
        if (sim.axis === "vertical") {
            // SIM-07：列布局接在 116 宽文件栏右侧，从文件栏顶到底。
            return { x: t("shell.filebar.content.width"), y: 0, w: sim.width, h: pane.h };
        }
        const band = layout.film?.scroll;
        if (!band) return null;
        // SIM-07：行布局从胶片带右端切出，右侧留 sim.panel.margin。
        return {
            x: pane.w - t("sim.panel.margin") - sim.width,
            y: band.y,
            w: sim.width,
            h: Math.min(band.h, sim.height ?? band.h),
        };
    }

    function syncItems(state, items) {
        const keys = items.map((item) => item.card.photo.id).join("\n");
        const nodes = [];
        const seen = new Set();
        for (const item of items) {
            const id = item.card.photo.id;
            seen.add(id);
            let entry = cards.get(id);
            if (!entry) {
                const card = createCard(ctx, { mode: "similarity" });
                const wrapper = el("div", { class: "pv-sim-item", role: "listitem" }, card.el);
                entry = { card, wrapper };
                cards.set(id, entry);
            }
            entry.card.bind(item.card, state, { percent: item.percent, barRatio: item.barRatio });
            nodes.push(entry.wrapper);
        }
        for (const [id, entry] of cards) {
            if (seen.has(id)) continue;
            entry.card.destroy?.();
            entry.wrapper.remove();
            cards.delete(id);
        }
        if (keys !== itemKeys) {
            // SIM-07：列表整体替换后回到起点；内容不变时不动用户滚动位置。
            list.replaceChildren(...nodes);
            list.scrollTop = 0;
            list.scrollLeft = 0;
            itemKeys = keys;
        }
    }

    function render(state) {
        const snapshot = state.similarity;
        const rect = panelRect(state);
        if (!rect) {
            panel.hidden = true;
            return;
        }
        panel.hidden = false;
        place(panel, rect);
        panel.dataset.axis = state.layout.similarity.axis;

        const inList = snapshot.state === "list";
        panel.classList.toggle("is-empty", !inList);

        syncItems(state, inList ? snapshot.items : []);

        const text = snapshot.emptyText ?? "";
        setText(emptyText, text);
        emptyText.hidden = text === "";

        const extracting = Boolean(snapshot.progress);
        button.hidden = !snapshot.action.visible || extracting;
        setText(button, snapshot.action.label);

        progress.hidden = !extracting;
        if (extracting) {
            const { done, total } = snapshot.progress;
            const known = Number.isFinite(total) && total > 0;
            progress.classList.toggle("is-indeterminate", !known);
            progressFill.style.width = known ? `${Math.min(1, done / total) * 100}%` : "";
            setText(progressText, known ? `${done} / ${total}` : `${done} / —`);
            if (known) {
                progress.setAttribute("aria-valuenow", String(done));
                progress.setAttribute("aria-valuemax", String(total));
            } else {
                progress.removeAttribute("aria-valuenow");
                progress.removeAttribute("aria-valuemax");
            }
        }

        const message = snapshot.error ?? "";
        setText(error, message);
        error.hidden = message === "";

        const showCaption = inList && snapshot.unextracted > 0;
        caption.hidden = !showCaption;
        if (showCaption) setText(caption, `${snapshot.unextracted} 张未提取`);

        const hasAction = snapshot.action.visible || extracting || message !== "";
        dock.hidden = !inList || !(showCaption || hasAction);

        const owner = inList ? dock : empty;
        if (actionBlock.parentNode !== owner) {
            const focused = document.activeElement === button && !button.hidden;
            owner.append(actionBlock);
            if (focused) button.focus({ preventScroll: true });
        }
    }

    // SIM-07 / FILM-04：行布局里普通纵向滚轮滚横向列表，滚轮事件不外泄。
    list.addEventListener("wheel", (event) => {
        event.stopPropagation();
        if (panel.dataset.axis !== "horizontal") return;
        if (Math.abs(event.deltaY) > Math.abs(event.deltaX)) {
            list.scrollLeft += event.deltaY;
            event.preventDefault();
        }
    }, { passive: false });

    // SIM-11：键盘聚焦列表时可滚动，不被主屏快捷键截走。
    list.addEventListener("keydown", (event) => {
        if (event.target !== list) return;
        const horizontal = panel.dataset.axis === "horizontal";
        let handled = true;
        switch (event.key) {
            case "ArrowLeft":
                if (horizontal) list.scrollLeft -= KEYBOARD_STEP;
                else handled = false;
                break;
            case "ArrowRight":
                if (horizontal) list.scrollLeft += KEYBOARD_STEP;
                else handled = false;
                break;
            case "ArrowUp":
                if (horizontal) handled = false;
                else list.scrollTop -= KEYBOARD_STEP;
                break;
            case "ArrowDown":
                if (horizontal) handled = false;
                else list.scrollTop += KEYBOARD_STEP;
                break;
            case "PageUp":
                if (horizontal) list.scrollLeft -= list.clientWidth;
                else list.scrollTop -= list.clientHeight;
                break;
            case "PageDown":
            case " ":
                if (horizontal) list.scrollLeft += list.clientWidth;
                else list.scrollTop += list.clientHeight;
                break;
            case "Home":
                if (horizontal) list.scrollLeft = 0;
                else list.scrollTop = 0;
                break;
            case "End":
                if (horizontal) list.scrollLeft = list.scrollWidth;
                else list.scrollTop = list.scrollHeight;
                break;
            default:
                handled = false;
        }
        if (!handled) return;
        event.preventDefault();
        event.stopPropagation();
    });

    return {
        update(state, regions) {
            if (!touches(regions, "layout", "similarity", "current", "list", "panes", "window", "image", "settings")) return;
            render(state);
        },
    };
}
