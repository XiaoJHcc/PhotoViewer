import { el, place, setText, touches } from "./dom.js";
import { icon } from "./icons.js";
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

    const segmentButtons = MENU_GROUPS.map((group) => {
        const text = el("span", { class: "pv-popup-label" });
        const button = el("button", {
            class: "pv-popup is-segment",
            type: "button",
            "aria-haspopup": "menu",
            "aria-expanded": "false",
            "aria-label": group.label,
        }, text);
        button.dataset.group = group.kind;
        button.addEventListener("click", () => openSegmentMenu(group, button));
        return { group, button, text };
    });

    const entry = el("button", {
        class: "pv-filter-entry",
        type: "button",
        "aria-haspopup": "menu",
        "aria-expanded": "false",
        "aria-label": "筛选",
    }, icon(t, "icon.filter", "pv-filter-glyph"), el("span", { class: "pv-filter-label" }, "筛选"));
    const more = el("button", {
        class: "pv-filter-more",
        type: "button",
        "aria-haspopup": "menu",
        "aria-expanded": "false",
        "aria-label": "更多",
    }, icon(t, "icon.more", "pv-filter-glyph"));
    const capsule = el("div", { class: "pv-filter-capsule" });
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

    host.append(capsule, entry, more, ...segmentButtons.map((item) => item.button), count);
    if (toggle) host.append(toggle);

    entry.addEventListener("click", () => openFilterMenu(entry));
    entry.addEventListener("keydown", (event) => {
        if (event.key !== "ArrowDown") return;
        event.preventDefault();
        event.stopPropagation();
        openFilterMenu(entry);
    });
    more.addEventListener("click", () => openMoreMenu(more));

    function collapse() {
        entry.setAttribute("aria-expanded", "false");
        more.setAttribute("aria-expanded", "false");
        for (const item of segmentButtons) item.button.setAttribute("aria-expanded", "false");
    }

    function openSegmentMenu(group, button) {
        const state = app.state;
        const filter = state.layout.filter;
        if (!filter?.controls?.[group.kind]) return;
        app.setDropdownOpen(true);
        button.setAttribute("aria-expanded", "true");
        menu.open(menuItemsFor(state, group), { rect: filter.controls[group.kind] }, {
            style: "dropdown",
            label: group.label,
            returnFocus: button,
            onSelect: (id) => {
                applyChoice(group.kind, id);
            },
            onClose: () => {
                collapse();
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
                collapse();
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
                collapse();
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
        const phone = layout.form === "phone";
        const portrait = filter?.kind === "portrait";
        const composite = filter?.kind === "composite";
        const segments = filter?.kind === "segments";
        const capsuleMode = filter?.kind === "capsule";

        host.classList.toggle("is-desktop", !phone);
        host.classList.toggle("is-portrait", Boolean(portrait));
        host.classList.toggle("is-composite", Boolean(composite));

        if (!filter) {
            capsule.hidden = true;
            entry.hidden = true;
            more.hidden = true;
            count.hidden = true;
            for (const item of segmentButtons) item.button.hidden = true;
            if (toggle) toggle.hidden = true;
            return;
        }

        const hasCapsule = Boolean(filter.composite) && !portrait;
        capsule.hidden = !hasCapsule;
        if (hasCapsule) place(capsule, filter.composite);
        count.hidden = false;
        place(count, filter.count);

        for (const item of segmentButtons) {
            item.button.hidden = !segments;
            if (segments) place(item.button, filter.controls[item.group.kind]);
        }

        // FILTER-07：宽形态单胶囊与手机入口的差异只在字形/文字与自身材质，命中区同为一个控件。
        entry.hidden = !(capsuleMode || composite || portrait);
        entry.classList.toggle("is-icon", phone);
        entry.classList.toggle("is-capsule", capsuleMode);
        if (!entry.hidden) place(entry, filter.entry);
        more.hidden = !(composite || portrait);
        if (!more.hidden) place(more, filter.more);

        if (toggle) {
            toggle.hidden = !filter.toggle;
            if (filter.toggle) place(toggle, filter.toggle);
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
            item.button.setAttribute("aria-label", `${item.group.label}：${label}`);
        }
    }

    return {
        update(state, regions) {
            if (touches(regions, "layout", "list", "settings", "similarity", "panes", "window")) {
                placeControls(state);
                setText(count, state.countText);
                renderSegmentLabels(state);
                renderToggle();
            }
        },
    };
}
