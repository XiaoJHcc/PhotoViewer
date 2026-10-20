#!/usr/bin/env node
/**
 * 导出 Web 参考壳的主屏几何 JSON，供 Tools/compare-layout-geometry.py 与各平台报告比对。
 * 用法：node PhotoViewer.Web/tools/export-geometry.mjs [--profile mac] [--count-width 52] --out <file>
 *   --count-width  固定计数文本宽（与 macOS 报告实测值对齐；缺省按字体近似测量）
 * 输出：{ source, profile, layouts: { "Col-1200x850": { 节点: [x,y,w,h] }, ... } }，数值保留两位小数。
 */
import { writeFileSync } from "node:fs";
import { computeLayout, approximateTextWidth } from "../src/core/layout/index.js";
import { createTokens } from "../src/core/tokens.js";

const args = process.argv.slice(2);
let profile = "web";
let out = null;
let countWidth = null;
for (let index = 0; index < args.length; index++) {
    if (args[index] === "--profile") profile = args[++index];
    else if (args[index] === "--out") out = args[++index];
    else if (args[index] === "--count-width") countWidth = Number(args[++index]);
}
if (!out) {
    console.error("用法：node PhotoViewer.Web/tools/export-geometry.mjs [--profile mac] [--count-width 52] --out <file>");
    process.exit(2);
}

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
    similarityExpanded: false,
    similarityItemCount: 0,
    similarityDockTexts: [],
    photoCount: 12,
    countText: "共 12 张",
    exifCount: 4,
    showRating: true,
    commandCount: 6,
    analysisTileCount: 6,
    hasPhoto: true,
};

const sizes = [[1200, 850], [830, 650], [700, 480]];
const layouts = {};
for (const mode of ["col", "row"]) {
    for (const [width, height] of sizes) {
        const name = `${mode === "col" ? "Col" : "Row"}-${width}x${height}`;
        const result = computeLayout({ ...base, width, height, layoutMode: mode }, { t, profile, measureText });
        layouts[name] = roundNodes(result.nodes);
    }
}
for (const [width, height] of [[1200, 850], [700, 850], [850, 700]]) {
    const result = computeLayout({ ...base, width, height, layoutMode: "auto" }, { t, profile, measureText });
    layouts[`Auto-${width}x${height}`] = { photoPane: roundFrame(result.nodes["root.photoPane"]) };
}

writeFileSync(out, `${JSON.stringify({ source: "web", profile, layouts }, null, 2)}\n`);
console.log(`已写出 ${out}（profile ${profile}，${Object.keys(layouts).length} 个布局）`);

function roundNodes(nodes) {
    const rounded = {};
    for (const [name, frame] of Object.entries(nodes)) rounded[name] = roundFrame(frame);
    return rounded;
}

function roundFrame(frame) {
    return frame.map((value) => Math.round(value * 100) / 100);
}
