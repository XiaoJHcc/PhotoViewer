/**
 * 发行版本能力（EDITION-01/02/03）：参考壳用 URL 参数表达两个构建变体。
 * 标准版不含 DINO 与相似聚类；其余浏览、评分、筛选与 CV 诊断能力保留。
 * 版本是构建期事实，页面内没有切换入口。
 */

export const EDITIONS = Object.freeze(["ai", "standard"]);
export const DEFAULT_EDITION = "ai";

export function normalizeEdition(value) {
    return value === "standard" ? "standard" : DEFAULT_EDITION;
}

export function editionCapabilities(edition) {
    const standard = normalizeEdition(edition) === "standard";
    return Object.freeze({
        dino: !standard,
        similarity: !standard,
    });
}
