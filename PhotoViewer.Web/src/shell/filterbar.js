import { el, place, setText, touches } from "./dom.js";
import { SORT_KEYS, SORT_ORDERS, RATING_FILTERS } from "../core/filter.js";

const MENU_GROUPS = [
    { kind: "sort", label: "排序方式", options: SORT_KEYS, valueOf: (state) => state.sortKey },
    { kind: "order", label: "排序方向", options: SORT_ORDERS, valueOf: (state) => state.sortOrder },
    { kind: "rating", label: "星级筛选", options: RATING_FILTERS, valueOf: (state) => state.ratingFilter },
];

function menuItemsFor(state, group) {
    const current = group.valueOf(state);
    return group.options.map((option) => ({
        id: option.value,
        group: group.kind,
        label: option.label,
        type: "radio",
        checked: option.value === current,
    }));
}

function mainMenuItems(app) {
    const items = [];
    let group = null;
    for (const item of app.menuModel()) {
        if (group !== null && item.group !== group) items.push({ type: "separator" });
        group = item.group;
        items.push(item);
    }
    return items;
}

export function mountFilterBar(host, ctx) {
    const { app, menu, t } = ctx;

    const segmentButtons = t.profile === "mac" ? MENU_GROUPS.map((group) => {
        const text = el("span", { class: "pv-popup-label" });
        const button = el("button", {
            class: "pv-popup",
            type: "button",
            "aria-haspopup": "menu",
            "aria-expanded": "false",
            "aria-label": group.label,
        }, text);
        button.addEventListener("click", () => openSegmentMenu(group, button));
        return { group, button, text };
    }) : [];

    const entry = el("button", {
        class: "pv-filter-entry",
        type: "button",
        "aria-haspopup": "menu",
        "aria-expanded": "false",
        "aria-label": "筛选",
    }, el("span", { class: "pv-filter-label" }, "筛选"));
    const more = el("button", {
        class: "pv-filter-more",
        type: "button",
        "aria-haspopup": "menu",
        "aria-expanded": "false",
        "aria-label": "更多",
    }, el("span", { class: "pv-filter-label" }, "更多"));
    const composite = el("div", { class: "pv-filter-composite" });
    const count = el("div", { class: "pv-filter-count", "aria-hidden": "true" });

    const toggle = app.capabilities.similarity
        ? el("button", {
            class: "pv-filter-toggle",
            type: "button",
            role: "switch",
            "aria-checked": "false",
            "aria-label": "相似聚类",
            "data-tooltip": "展开/关闭相似聚类面板",
        }, el("span", { class: "pv-filter-toggle-label" }, "相似聚类"))
        : null;
    if (toggle) {
        toggle.addEventListener("click", () => {
            app.setSimilarityExpanded(!app.settings.similarityPanelExpanded);
        });
    }

    host.classList.toggle("is-mac", t.profile === "mac");
    host.append(composite, entry, more, count, ...segmentButtons.map((item) => item.button));
    if (toggle) host.append(toggle);

    entry.addEventListener("click", () => openFilterMenu(entry));
    entry.addEventListener("keydown", (event) => {
        if (event.key !== "ArrowDown") return;
        event.preventDefault();
        event.stopPropagation();
        openFilterMenu(entry);
    });
    more.addEventListener("click", () => openMoreMenu(more));

    function ensureParent(mode) {
        // 合体只是视觉容器；两个分段始终是筛选层的直接子节点，避免坐标叠加。
        composite.hidden = mode !== "composite";
    }

    function openSegmentMenu(group, button) {
        const state = app.state;
        const filter = state.layout.filter;
        if (!filter?.controls?.[group.kind]) return;
        app.setDropdownOpen(true);
        button.setAttribute("aria-expanded", "true");
        button.setAttribute("aria-label", `${group.label}：${group.options.find((option) => option.value === group.valueOf(state))?.label ?? ""}`);
        menu.open(menuItemsFor(state, group), { rect: filter.controls[group.kind] }, {
            style: "dropdown",
            label: group.label,
            returnFocus: button,
            onSelect: (id) => {
                applyChoice(group.kind, id);
            },
            onClose: () => {
                button.setAttribute("aria-expanded", "false");
                app.setDropdownOpen(false);
            },
        });
    }

    function openFilterMenu(button) {
        const state = app.state;
        const filter = state.layout.filter;
        if (!filter?.entry) return;
        const items = [];
        MENU_GROUPS.forEach((group, index) => {
            if (index > 0) items.push({ type: "separator" });
            items.push(...menuItemsFor(state, group));
        });
        app.setDropdownOpen(true);
        button.setAttribute("aria-expanded", "true");
        menu.open(items, { rect: filter.entry }, {
            style: "dropdown",
            label: "筛选",
            returnFocus: button,
            onSelect: (id) => {
                const kind = MENU_GROUPS.find((group) => group.options.some((option) => option.value === id))?.kind;
                if (kind) applyChoice(kind, id);
            },
            onClose: () => {
                entry.setAttribute("aria-expanded", "false");
                more.setAttribute("aria-expanded", "false");
                app.setDropdownOpen(false);
            },
        });
    }

    function openMoreMenu(button) {
        const filter = app.state.layout.filter;
        if (!filter?.more) return;
        button.setAttribute("aria-expanded", "true");
        app.setDropdownOpen(true);
        menu.open(mainMenuItems(app), { rect: filter.more }, {
            label: "更多",
            returnFocus: button,
            onSelect: (id) => app.runMenu(id),
            onClose: () => {
                entry.setAttribute("aria-expanded", "false");
                more.setAttribute("aria-expanded", "false");
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
        const layout = state.layout;
        const filter = layout.filter;
        const phoneLandscape = layout.form === "phone" && layout.orientation === "landscape";
        const phonePortrait = layout.form === "phone" && layout.orientation === "portrait";
        const segments = filter?.kind === "segments";
        const capsule = filter?.kind === "capsule";
        const portrait = filter?.kind === "portrait";
        const compositeMode = !phonePortrait && !segments && !capsule;
        ensureParent(compositeMode ? "composite" : "separate");
        host.classList.toggle("is-composite", compositeMode);
        if (!filter || (!filter.entry && !filter.toggle && !segments)) {
            entry.hidden = true;
            more.hidden = true;
            count.hidden = true;
            for (const item of segmentButtons) item.button.hidden = true;
            if (toggle) toggle.hidden = true;
            return;
        }
        entry.hidden = !(compositeMode || capsule || portrait);
        more.hidden = !(compositeMode || portrait);
        count.hidden = segments ? false : !(compositeMode || portrait);
        for (const item of segmentButtons) {
            item.button.hidden = !segments;
            if (segments) place(item.button, filter.controls[item.group.kind]);
        }
        if (compositeMode || capsule) {
            place(entry, filter.entry);
            place(more, filter.more);
            place(composite, filter.composite ?? filter.entry);
            place(count, filter.count);
        } else if (portrait) {
            place(entry, filter.entry);
            place(more, filter.more);
            place(count, filter.count);
        } else if (segments) {
            if (filter.composite) place(composite, filter.composite);
            place(count, filter.count);
        }
        if (toggle) {
            toggle.hidden = !filter.toggle;
            place(toggle, filter.toggle);
        }
    }

    function renderToggle() {
        if (!toggle) return;
        toggle.setAttribute("aria-checked", String(Boolean(app.settings.similarityPanelExpanded)));
    }

    function renderSegmentLabels(state) {
        for (const item of segmentButtons) {
            const label = item.group.options.find((option) => option.value === item.group.valueOf(state))?.label ?? "";
            setText(item.text, label);
        }
    }

    return {
        update(state, regions) {
            if (touches(regions, "layout", "list", "settings", "similarity", "panes", "window")) {
                placeControls(state);
                setText(count, state.countText);
                entry.setAttribute("aria-label", `筛选，${state.countText}`);
                renderSegmentLabels(state);
                renderToggle();
            }
        },
    };
}
