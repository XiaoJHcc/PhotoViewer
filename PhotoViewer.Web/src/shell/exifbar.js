import { el, place, setText, touches } from "./dom.js";

/**
 * EXIF 摘要（TOOLBAR-05）：宽形态横排有衬底块、左右布局无衬底纵排，手机竖屏顶行块、手机横屏无衬底。
 * 几何只取 state.layout.exif；宽形态与手机横屏挂在控制宿主内随控制文档滚动，
 * 手机竖屏挂在全窗 EXIF 层；项坐标相对宿主块。
 */
export function mountExifBar(initialHost) {
    const block = el("div", { class: "pv-control-exif-block", role: "group", "aria-label": "EXIF 摘要" });
    let host = initialHost;
    host.append(block);
    const items = [];

    function createItem() {
        const label = el("span", { class: "pv-control-exif-label" });
        const value = el("span", { class: "pv-control-exif-value" });
        const node = el("div", { class: "pv-control-exif-item" }, label, value);
        node.labelNode = label;
        node.valueNode = value;
        return node;
    }

    function apply(state) {
        const exif = state.layout.exif;
        const source = state.toolbar.exif;
        if (!exif || !exif.visible) {
            block.hidden = true;
            return;
        }
        block.hidden = false;
        block.classList.toggle("is-bare", exif.variant === "bare");
        block.classList.toggle("is-block", exif.variant === "block");
        place(block, exif.frame);
        while (items.length < exif.items.length) {
            const item = createItem();
            items.push(item);
            block.append(item);
        }
        exif.items.forEach((frame, index) => {
            const item = items[index];
            const data = source[index];
            if (!data) {
                item.hidden = true;
                return;
            }
            item.hidden = false;
            place(item, frame);
            setText(item.labelNode, data.label);
            setText(item.valueNode, data.value);
            item.dataset.tooltip = data.tooltip;
            item.setAttribute("aria-label", data.tooltip);
        });
        for (let index = exif.items.length; index < items.length; index += 1) {
            items[index].hidden = true;
        }
    }

    return {
        setHost(next) {
            if (!next || next === host) return;
            host = next;
            host.append(block);
        },
        update(state, regions) {
            if (!touches(regions, "layout", "toolbar", "current", "settings", "panes", "window")) return;
            apply(state);
        },
        destroy() {
            block.remove();
        },
    };
}
