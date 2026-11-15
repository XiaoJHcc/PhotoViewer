/**
 * 主屏形态判定（SHELL-07@web）：宽形态 / 手机竖屏 / 手机横屏。
 * 自动模式把 CSS 视口宽 < 600 映射为 iPad 紧凑分屏的横向紧凑（手机形态），>= 600 为常规；
 * 显式行/列恒为宽形态，手机模式用于规范检验。不读取 User-Agent，也不模拟垂直紧凑。
 */

export const COMPACT_WIDTH = 600;

export function resolveScreenForm(scene = {}, options = {}) {
    const width = Number(scene.width) || 0;
    const height = Number(scene.height) || 0;
    const mode = scene.layoutMode ?? "auto";
    const phone = options.phone === false ? false : true;
    const compactWidth = options.compactWidth ?? COMPACT_WIDTH;
    if (!phone) {
        return { form: "wide", orientation: null, mode: mode === "col" ? "col" : "row", compact: false, reason: "no-phone" };
    }
    if (mode === "phone") return phoneForm(width, height, "explicit-phone");
    if (mode === "row" || mode === "col") {
        return { form: "wide", orientation: null, mode, compact: false, reason: "explicit-wide" };
    }
    if (width < compactWidth) return phoneForm(width, height, "compact-width");
    return {
        form: "wide",
        orientation: null,
        mode: width > height ? "col" : "row",
        compact: false,
        reason: "regular",
    };
}

function phoneForm(width, height, reason) {
    return {
        form: "phone",
        orientation: width > height ? "landscape" : "portrait",
        mode: "phone",
        compact: true,
        reason,
    };
}

export function normalizeSafeArea(area, orientation) {
    const raw = area && typeof area === "object" ? area : {};
    const read = (key) => {
        const value = Number(raw[key]);
        return Number.isFinite(value) && value > 0 ? value : 0;
    };
    const safe = {
        top: read("top"),
        right: read("right"),
        bottom: read("bottom"),
        left: read("left"),
    };
    // SHELL-10：横屏不为底部手势条留位；刘海侧取左右安全区较大的一侧，相等按左侧。
    if (orientation === "landscape") safe.bottom = 0;
    const notchSide = safe.left >= safe.right ? "left" : "right";
    return {
        ...safe,
        notchSide,
        notchInset: Math.max(safe.left, safe.right),
        source: raw.source === "injected" ? "injected" : "env",
    };
}
