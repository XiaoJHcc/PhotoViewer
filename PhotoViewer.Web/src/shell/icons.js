/**
 * 主屏图标（TOOLBAR-04、FILM-05、FILTER-08、MENU-02）：统一取 icon.font 字体字形。
 * 字体子集由 tools/subset-icons.py 生成（design/tokens.json 的 icon.* 码位），
 * 实心字形取 icon.fontSolid（uicons solid-straight）的 icon.starFilled。
 */
import { el } from "./dom.js";

export function codepoint(value) {
    const source = String(value ?? "");
    const match = /^U\+([0-9A-Fa-f]{4,6})$/.exec(source);
    if (!match) throw new Error(`图标令牌不是码位：${source}`);
    return String.fromCodePoint(parseInt(match[1], 16));
}

/** 描边字形（uicons-regular-rounded 子集）。token 为 "icon.*" 名称。 */
export function icon(t, token, className = "") {
    return el("span", {
        class: `pv-icon ${className}`.trim(),
        "aria-hidden": "true",
    }, codepoint(t(token)));
}

/** 实心字形（icon.fontSolid 子集）。token 为 "icon.*Filled" 名称。 */
export function solidIcon(t, token, className = "") {
    return el("span", {
        class: `pv-icon pv-icon-solid ${className}`.trim(),
        "aria-hidden": "true",
    }, codepoint(t(token)));
}
