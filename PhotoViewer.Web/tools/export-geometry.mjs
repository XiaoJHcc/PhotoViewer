#!/usr/bin/env node
/**
 * 导出 Web 参考壳的主屏几何 JSON，供 Tools/compare-layout-geometry.py 与各平台报告比对。
 * 用法：node PhotoViewer.Web/tools/export-geometry.mjs [--profile mac] [--edition standard|ai] [--count-width 52] [--form wide|phone|all] --out <file>
 *   --edition      导出标准版（无相似聚类开关、分析栏 4/5 瓦片）或 AI 版（缺省 ai）
 *   --count-width  固定计数文本宽（与 macOS 报告实测值对齐；缺省按字体近似测量）
 *   --form         只导出宽形态或手机形态；缺省 all（宽 + 手机开关矩阵）
 * 输出：{ source, profile, edition, layouts: { "Col-1200x850": { 节点: [x,y,w,h] }, ... } }，数值保留两位小数。
 */
import { writeFileSync } from "node:fs";
import { computeLayout, approximateTextWidth } from "../src/core/layout/index.js";
import { createTokens } from "../src/core/tokens.js";
import { normalizeEdition } from "../src/core/edition.js";

const args = process.argv.slice(2);
let profile = "web";
let edition = "ai";
let out = null;
let countWidth = null;
let formFilter = "all";
let filterWidths = null;
for (let index = 0; index < args.length; index++) {
    if (args[index] === "--profile") profile = args[++index];
    else if (args[index] === "--edition") edition = normalizeEdition(args[++index]);
    else if (args[index] === "--out") out = args[++index];
    else if (args[index] === "--count-width") countWidth = Number(args[++index]);
    else if (args[index] === "--form") formFilter = args[++index];
    else if (args[index] === "--filter-widths") {
        filterWidths = args[++index].split(",").map(Number);
        if (filterWidths.length !== 3 || filterWidths.some(value => !Number.isFinite(value) || value <= 0)) {
            console.error("--filter-widths 需要三个正数，例如 96,96,95");
            process.exit(2);
        }
    }
}
if (!out) {
    console.error("用法：node PhotoViewer.Web/tools/export-geometry.mjs [--profile mac] [--edition standard|ai] [--count-width 52] --out <file>");
    process.exit(2);
}
const standard = edition === "standard";

const t = createTokens(profile);
const defaultMeasure = (text, fontSizePt) => approximateTextWidth(text, fontSizePt);
// FILTER-04：计数宽按平台字体自然宽度；比对 macOS 报告时可用实测值固定。
const measureText = Number.isFinite(countWidth) && countWidth > 0
    ? (text, fontSizePt, weight) => (String(text).startsWith("共 ") ? countWidth : defaultMeasure(text, fontSizePt, weight))
    : defaultMeasure;
const base = {
    showFilmbar: true,
    showControlbar: true,
    showAnalysis: false,
    showSimilarity: !standard,
    similarityExpanded: false,
    similarityItemCount: 0,
    similarityDockTexts: [],
    photoCount: 12,
    countText: "共 12 张",
    exifCount: 4,
    showRating: true,
    commandCount: 6,
    analysisTileCount: standard ? 5 : 6,
    hasPhoto: true,
};
if (filterWidths) base.filterSegmentWidths = filterWidths;

const layouts = {};
const meta = {};

function exportLayout(name, overrides) {
    const result = computeLayout({ ...base, ...overrides }, { t, profile, measureText });
    layouts[name] = roundNodes(result.nodes);
    meta[name] = {
        form: result.form,
        orientation: result.orientation,
        layoutMode: result.layoutMode,
        switches: {
            film: Boolean(overrides.showFilmbar ?? base.showFilmbar),
            control: Boolean(overrides.showControlbar ?? base.showControlbar),
            analysis: Boolean(overrides.showAnalysis ?? base.showAnalysis),
            exif: overrides.showExif !== false,
            rating: overrides.showRating !== false,
            navigation: overrides.showNavigation !== false,
        },
    };
}

if (formFilter !== "phone") {
    for (const mode of ["col", "row"]) {
        for (const [width, height] of [[1200, 850], [830, 650], [700, 480]]) {
            exportLayout(`${mode === "col" ? "Col" : "Row"}-${width}x${height}`, { width, height, layoutMode: mode });
        }
    }
    for (const [width, height] of [[1200, 850], [700, 850], [850, 700]]) {
        exportLayout(`Auto-${width}x${height}`, { width, height, layoutMode: "auto" });
    }
    exportLayout("Row-1200x850-safe-top", {
        width: 1200,
        height: 850,
        layoutMode: "row",
        safeArea: { top: 24, right: 0, bottom: 0, left: 0 },
    });
    if (profile === "mac") {
        exportLayout("Col-1200x850-segments28", {
            width: 1200,
            height: 850,
            layoutMode: "col",
            filterSegmentWidth: 28,
        });
        exportLayout("Row-360x800", { width: 360, height: 800, layoutMode: "row" });
    }
}

if (formFilter !== "wide") {
    const portrait = { width: 390, height: 844, layoutMode: "phone", safeArea: { top: 59, right: 0, bottom: 34, left: 0 } };
    const landscape = { width: 844, height: 390, layoutMode: "phone", safeArea: { top: 0, right: 0, bottom: 0, left: 59 } };
    const mirror = { width: 844, height: 390, layoutMode: "phone", safeArea: { top: 0, right: 59, bottom: 0, left: 0 } };
    exportLayout("Phone-portrait", portrait);
    exportLayout("Phone-portrait-analysis", { ...portrait, showAnalysis: true });
    exportLayout("Phone-portrait-film-off", { ...portrait, showAnalysis: true, showFilmbar: false });
    exportLayout("Phone-portrait-control-off", { ...portrait, showAnalysis: true, showControlbar: false });
    exportLayout("Phone-portrait-exif-off", { ...portrait, showAnalysis: true, showExif: false });
    exportLayout("Phone-portrait-rating-off", { ...portrait, showAnalysis: true, showRating: false });
    exportLayout("Phone-portrait-navigation-off", { ...portrait, showAnalysis: true, showNavigation: false });
    exportLayout("Phone-portrait-safe-sides", {
        ...portrait,
        safeArea: { top: 59, right: 30, bottom: 0, left: 20 },
    });
    exportLayout("Phone-landscape", landscape);
    exportLayout("Phone-landscape-analysis", { ...landscape, showAnalysis: true });
    exportLayout("Phone-landscape-mirror", { ...mirror, showAnalysis: true });
    exportLayout("Phone-landscape-film-off", { ...landscape, showAnalysis: true, showFilmbar: false });
    exportLayout("Phone-landscape-control-off", { ...landscape, showAnalysis: true, showControlbar: false });
    exportLayout("Phone-landscape-exif-off", { ...landscape, showAnalysis: true, showExif: false });
    exportLayout("Phone-landscape-rating-off", { ...landscape, showAnalysis: true, showRating: false });
    exportLayout("Phone-landscape-navigation-off", { ...landscape, showAnalysis: true, showNavigation: false });
}

writeFileSync(out, `${JSON.stringify({ source: "web", profile, edition, layouts, meta }, null, 2)}\n`);
console.log(`已写出 ${out}（profile ${profile}，edition ${edition}，${Object.keys(layouts).length} 个布局）`);

function roundNodes(nodes) {
    const rounded = {};
    for (const [name, frame] of Object.entries(nodes)) rounded[name] = roundFrame(frame);
    return rounded;
}

function roundFrame(frame) {
    return frame.map((value) => Math.round(value * 100) / 100);
}
