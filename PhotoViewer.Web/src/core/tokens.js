import baseTokens, { platformTokens } from "../tokens.g.js";

const PROFILE_PLATFORM = { web: "web", mac: "macos" };

export function createTokens(profile = "web") {
    const platform = PROFILE_PLATFORM[profile];
    if (!platform) throw new Error(`未知平台配置档：${profile}`);
    const values = { ...baseTokens, ...(platformTokens[platform] ?? {}) };
    const t = (name) => {
        if (!(name in values)) throw new Error(`缺少设计令牌：${name}`);
        return values[name];
    };
    t.profile = profile;
    t.has = (name) => name in values;
    return t;
}

export function colorToCss(value) {
    if (typeof value !== "string" || !value.startsWith("#")) return value;
    const hex = value.slice(1);
    if (hex.length === 6) return `#${hex}`;
    if (hex.length !== 8) throw new Error(`颜色格式不合法：${value}`);
    const alpha = parseInt(hex.slice(0, 2), 16) / 255;
    const r = parseInt(hex.slice(2, 4), 16);
    const g = parseInt(hex.slice(4, 6), 16);
    const b = parseInt(hex.slice(6, 8), 16);
    return `rgba(${r}, ${g}, ${b}, ${Number(alpha.toFixed(3))})`;
}
