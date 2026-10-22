/**
 * 共用弹出菜单（筛选下拉与主图右键菜单，MENU-03@web / FILTER-08@web：浏览器无原生弹层，自绘）。
 * createMenu({ host, onOpenChange }) → { open(items, anchor, options), close(), isOpen }
 *   items: [{ id, label, type: "command"|"check"|"radio"|"separator"|"header", checked, enabled, shortcut }]
 *   anchor: { x, y }（窗口坐标点，右键：菜单左上角对齐指针）或 { rect }（下拉控件矩形：
 *     菜单贴按钮正下方、以按钮水平居中，最小宽 = 按钮宽，下方不足翻到上方）
 *   options: { onSelect(id), onClose(), returnFocus: HTMLElement, label, style: "menu"|"dropdown", focusFirst }
 * 行为：夹在宿主内（MENU-03）；上下键移动、回车/空格选择、Esc/点外部关闭且不穿透（MENU-03、FILTER-08）；
 * 选择即关闭；同时只开一个；关闭后焦点回到 returnFocus。
 * 外观（旧壳 Avalonia Fluent 深色）：弹层 popup.*，菜单项 menu.*；下拉项 dropdown.*（当前项整行强调色、无勾号）。
 */
import { el } from "./dom.js";
import { codepoint } from "./icons.js";

const EDGE = 4;

export function createMenu({ host, t, onOpenChange = () => {} }) {
    let current = null;

    function close(reason = "dismiss") {
        if (!current) return;
        const { root, shield, options, cleanup } = current;
        current = null;
        cleanup();
        root.remove();
        shield.remove();
        onOpenChange(false);
        options.onClose?.(reason);
        options.returnFocus?.focus?.({ preventScroll: true });
    }

    function open(items, anchor, options = {}) {
        close("replaced");
        const dropdown = options.style === "dropdown";
        const checkable = items.some((item) => item.type === "check" || item.type === "radio");
        // MENU-03：菜单内有任一勾选项时所有项都让出勾选列 + 间距；下拉用整行选中色，不画勾号。
        const hasCheckColumn = checkable && !dropdown;
        const checkGlyph = codepoint(t("icon.check"));
        const shield = el("div", { class: "pv-menu-shield" });
        const list = el("div", {
            class: dropdown ? "pv-menu is-dropdown" : "pv-menu",
            role: "menu",
            "aria-label": options.label ?? "",
        });
        if (hasCheckColumn) list.classList.add("has-check");
        const rows = [];
        const rowItems = [];
        for (const item of items) {
            if (item.type === "separator") {
                list.append(el("div", { class: "pv-menu-sep", role: "separator" }));
                continue;
            }
            if (item.type === "header") {
                // MENU-05：卡片菜单标题行不可点击、不参与键盘移动。
                list.append(el("div", { class: "pv-menu-header", role: "presentation" },
                    el("span", { class: "pv-menu-header-title" }, item.label ?? ""),
                    item.detail ? el("span", { class: "pv-menu-header-detail" }, item.detail) : null));
                continue;
            }
            const role = item.type === "check" ? "menuitemcheckbox" : item.type === "radio" ? "menuitemradio" : "menuitem";
            const row = el("div", {
                class: "pv-menu-item",
                role,
                tabindex: "-1",
                "aria-disabled": item.enabled === false ? "true" : "false",
                "aria-checked": item.type === "check" || item.type === "radio" ? String(Boolean(item.checked)) : null,
                dataset: { id: item.id },
            });
            if (hasCheckColumn) {
                row.append(el("span", {
                    class: "pv-menu-mark",
                    "aria-hidden": "true",
                }, item.checked ? checkGlyph : ""));
            }
            row.append(
                el("span", { class: "pv-menu-label" }, item.label),
                el("span", { class: "pv-menu-shortcut" }, item.shortcut ?? ""));
            if (dropdown && item.checked) row.classList.add("is-selected");
            if (item.enabled === false) row.classList.add("is-disabled");
            row.addEventListener("pointerenter", () => focusRow(rows.indexOf(row)));
            row.addEventListener("pointerdown", (event) => {
                event.stopPropagation();
                if (item.enabled === false) return;
                row.classList.add("is-pressed");
            });
            const release = () => row.classList.remove("is-pressed");
            row.addEventListener("pointerup", release);
            row.addEventListener("pointercancel", release);
            row.addEventListener("pointerleave", release);
            row.addEventListener("click", (event) => {
                event.stopPropagation();
                if (item.enabled === false) return;
                select(item.id);
            });
            rows.push(row);
            rowItems.push(item);
            list.append(row);
        }
        const root = el("div", { class: "pv-menu-layer" }, list);
        host.append(shield, root);

        const hostRect = host.getBoundingClientRect();
        const maxH = Math.max(40, hostRect.height - EDGE * 2);
        if (list.getBoundingClientRect().height > maxH) list.style.maxHeight = `${maxH}px`;
        const height = Math.min(list.getBoundingClientRect().height, maxH);
        let x;
        let y;
        if (anchor.rect) {
            const r = anchor.rect;
            list.style.minWidth = `${r.w}px`;
            const width = list.getBoundingClientRect().width;
            // FILTER-08：贴在按钮正下方、水平居中；下方不足翻到上方。
            x = r.x + r.w / 2 - width / 2;
            y = r.y + r.h;
            if (y + height > hostRect.height - EDGE) y = r.y - height;
        } else {
            // MENU-03：右键菜单左上角对齐指针。
            x = anchor.x;
            y = anchor.y;
        }
        const width = list.getBoundingClientRect().width;
        x = Math.min(Math.max(EDGE, x), Math.max(EDGE, hostRect.width - width - EDGE));
        y = Math.min(Math.max(EDGE, y), Math.max(EDGE, hostRect.height - height - EDGE));
        list.style.left = `${x}px`;
        list.style.top = `${y}px`;

        let focused = -1;
        function focusRow(index) {
            if (index < 0 || index >= rows.length) return;
            rows[focused]?.classList.remove("is-focused");
            focused = index;
            rows[focused].classList.add("is-focused");
            rows[focused].focus({ preventScroll: true });
        }
        function step(delta) {
            if (rows.length === 0) return;
            let i = focused;
            for (let n = 0; n < rows.length; n++) {
                i = (i + delta + rows.length) % rows.length;
                if (!rows[i].classList.contains("is-disabled")) return focusRow(i);
            }
        }
        function select(id) {
            const handler = options.onSelect;
            close("select");
            handler?.(id);
        }
        const onKey = (event) => {
            const key = event.key;
            if (key === "ArrowDown") step(1);
            else if (key === "ArrowUp") step(-1);
            else if (key === "Home") { focused = -1; step(1); }
            else if (key === "End") { focused = 0; step(-1); }
            else if (key === "Enter" || key === " ") {
                const row = rows[focused];
                if (row && !row.classList.contains("is-disabled")) select(row.dataset.id);
            } else if (key === "Escape") close("escape");
            else if (key === "Tab") close("dismiss");
            else return;
            event.preventDefault();
            event.stopPropagation();
        };
        // MENU-03：点外部只关闭菜单，不穿透到底层照片。
        const swallow = (event) => {
            event.preventDefault();
            event.stopPropagation();
            if (event.type === "pointerdown") close("outside");
        };
        shield.addEventListener("pointerdown", swallow);
        shield.addEventListener("contextmenu", swallow);
        shield.addEventListener("wheel", swallow, { passive: false });
        window.addEventListener("keydown", onKey, true);
        const onBlur = () => close("blur");
        window.addEventListener("blur", onBlur);
        const onResize = () => close("resize");
        window.addEventListener("resize", onResize);

        current = {
            root,
            shield,
            options,
            cleanup() {
                window.removeEventListener("keydown", onKey, true);
                window.removeEventListener("blur", onBlur);
                window.removeEventListener("resize", onResize);
            },
        };
        onOpenChange(true);
        const initial = rowItems.findIndex((item) => item.checked && item.type !== "check");
        if (options.focusFirst !== false) {
            if (initial >= 0) focusRow(initial);
            else step(1);
        } else {
            list.focus?.({ preventScroll: true });
        }
    }

    return {
        open,
        close,
        get isOpen() {
            return current !== null;
        },
    };
}
