import { el, place, setText, touches } from "./dom.js";
import { SORT_KEYS, SORT_ORDERS, RATING_FILTERS } from "../core/filter.js";

// FILTER-01：排序方式 → 排序方向 → 星级筛选 → 计数 → 相似聚类开关，顺序由布局给出。
const MENUS = {
    sort: { label: "排序方式", options: SORT_KEYS, valueOf: (state) => state.sortKey },
    order: { label: "排序方向", options: SORT_ORDERS, valueOf: (state) => state.sortOrder },
    rating: { label: "星级筛选", options: RATING_FILTERS, valueOf: (state) => state.ratingFilter },
};

function chevron(className) {
    return el("span", { class: `pv-popup-arrow ${className}` });
}

export function mountFilterBar(host, ctx) {
    const { app, menu } = ctx;

    const popups = ["sort", "order", "rating"].map((kind) => {
        const spec = MENUS[kind];
        const text = el("span", { class: "pv-popup-label" });
        const arrows = el("span", { class: "pv-popup-arrows", "aria-hidden": "true" }, chevron("is-up"), chevron("is-down"));
        const button = el("button", {
            class: "pv-popup",
            type: "button",
            "aria-haspopup": "menu",
            "aria-expanded": "false",
        }, text, arrows);
        button.addEventListener("click", () => openPopup(kind, button));
        button.addEventListener("keydown", (event) => {
            if (event.key !== "ArrowDown") return;
            // FILTER-08：↓ 与点击同义（空格/回车走按钮原生激活）；打开后按键交给共用菜单。
            event.preventDefault();
            event.stopPropagation();
            openPopup(kind, button);
        });
        return { kind, spec, button, text };
    });

    const count = el("div", { class: "pv-filter-count" });

    const toggle = el("button", {
        class: "pv-filter-toggle",
        type: "button",
        role: "switch",
        "aria-checked": "false",
        "aria-label": "相似聚类",
        title: "展开/关闭相似聚类面板",
    }, el("span", { class: "pv-filter-toggle-label" }, "相似聚类"));
    toggle.addEventListener("click", () => {
        // FILTER-06：勾选态即设置里的展开状态，与面板当前可见性无关。
        app.setSimilarityExpanded(!app.settings.similarityPanelExpanded);
    });

    host.append(popups[0].button, popups[1].button, popups[2].button, count, toggle);

    function openPopup(kind, button) {
        const state = app.state;
        const filter = state.layout.filter;
        if (!filter) return;
        const spec = MENUS[kind];
        const current = spec.valueOf(state);
        const frame = filter.controls[kind];
        const pane = state.layout.panes.filePane;
        const items = spec.options.map((option) => ({
            id: option.value,
            label: option.label,
            type: "radio",
            checked: option.value === current,
        }));
        app.setDropdownOpen(true);
        button.setAttribute("aria-expanded", "true");
        menu.open(items, { rect: { x: pane.x + frame.x, y: pane.y + frame.y, w: frame.w, h: frame.h } }, {
            alignChecked: true,
            label: spec.label,
            returnFocus: button,
            onSelect: (id) => applyChoice(kind, id),
            onClose: () => {
                button.setAttribute("aria-expanded", "false");
                app.setDropdownOpen(false);
            },
        });
    }

    function applyChoice(kind, value) {
        const state = app.state;
        if (kind === "sort") app.setSort(value, state.sortOrder);
        else if (kind === "order") app.setSort(state.sortKey, value);
        else app.setRatingFilter(value);
    }

    function placeControls(state) {
        const controls = state.layout.filter ? state.layout.filter.controls : null;
        place(popups[0].button, controls?.sort);
        place(popups[1].button, controls?.order);
        place(popups[2].button, controls?.rating);
        place(count, controls?.count);
        place(toggle, controls?.toggle);
    }

    function renderChoices(state) {
        for (const popup of popups) {
            const value = popup.spec.valueOf(state);
            const label = popup.spec.options.find((option) => option.value === value)?.label ?? "";
            setText(popup.text, label);
            popup.button.setAttribute("aria-label", `${popup.spec.label}：${label}`);
        }
        setText(count, state.countText);
    }

    function renderToggle() {
        toggle.setAttribute("aria-checked", String(Boolean(app.settings.similarityPanelExpanded)));
    }

    return {
        update(state, regions) {
            // FILTER-04：计数文本宽度随内容变化，列表变化时按新布局重落位，文本与列表同帧对齐。
            if (touches(regions, "layout", "list")) {
                placeControls(state);
                renderChoices(state);
            }
            if (touches(regions, "layout", "settings", "similarity")) renderToggle();
        },
    };
}
