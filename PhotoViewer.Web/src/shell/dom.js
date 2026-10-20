/**
 * 壳层公共工具：元素创建、矩形落位、令牌 CSS 变量。
 * 组件约定：export function mountXxx(host, ctx) → { update(state, regions) , destroy?() }
 * ctx = { app, t, thumbs, clock, measureText, overlay（浮层宿主）, menu（共用菜单组件） }
 * update 只在 regions 命中本组件关心的区域时做事；DOM 节点复用，不整块重建。
 */
import { colorToCss } from "../core/tokens.js";

export function el(tag, props = {}, ...children) {
    const node = document.createElement(tag);
    for (const [key, value] of Object.entries(props)) {
        if (value === undefined || value === null || value === false) continue;
        if (key === "class") node.className = value;
        else if (key === "style") Object.assign(node.style, value);
        else if (key === "dataset") Object.assign(node.dataset, value);
        else if (key.startsWith("on") && typeof value === "function") node.addEventListener(key.slice(2), value);
        else if (key in node && typeof value !== "string") node[key] = value;
        else node.setAttribute(key, value === true ? "" : String(value));
    }
    for (const child of children.flat()) {
        if (child === null || child === undefined || child === false) continue;
        node.append(child instanceof Node ? child : document.createTextNode(String(child)));
    }
    return node;
}

export function place(node, rect) {
    if (!rect || rect.w <= 0 || rect.h <= 0) {
        node.hidden = true;
        return;
    }
    node.hidden = false;
    const s = node.style;
    s.left = `${rect.x}px`;
    s.top = `${rect.y}px`;
    s.width = `${rect.w}px`;
    s.height = `${rect.h}px`;
}

export function setText(node, text) {
    const value = String(text ?? "");
    if (node.textContent !== value) node.textContent = value;
}

export function touches(regions, ...names) {
    if (!regions) return true;
    return names.some((name) => regions.has(name));
}

/** 把令牌写成 CSS 变量：--pv-<点分名转连字符>，颜色转 CSS、数值带 px（ms/ratio/count 不带）。 */
export function applyTokenVariables(root, t, names) {
    for (const name of names) {
        const value = t(name);
        const variable = `--pv-${name.replaceAll(".", "-")}`;
        let css;
        if (typeof value === "string" && value.startsWith("#")) css = colorToCss(value);
        else if (typeof value === "number" && !/(duration|delay|alpha|ratio|ms$|tick|timeout|lowerBound|threshold|overscan|Scale|step)/i.test(name)) css = `${value}px`;
        else css = String(value);
        root.style.setProperty(variable, css);
    }
}

export function createTextMeasurer() {
    const canvas = document.createElement("canvas");
    const ctx = canvas.getContext("2d");
    const family = getComputedStyle(document.documentElement).fontFamily;
    return (text, fontSize, weight = 400) => {
        ctx.font = `${weight} ${fontSize}px ${family}`;
        return ctx.measureText(String(text)).width;
    };
}
