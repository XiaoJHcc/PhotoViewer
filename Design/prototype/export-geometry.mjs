// 无浏览器导出主窗口样机几何：与 PhotoViewer.Mac 的 --pv-native-layout 验收同配置。
// 用法：node Design/prototype/export-geometry.mjs [--out <file>]
// 输出：{"source":"prototype","layouts":{"Col-1200x850":{"root.filePane":[0,0,116,850],...},...}}
import { writeFileSync } from "node:fs";
import geometry from "./geometry.js";
import tokens from "./tokens.js";

/** Mac 验收的三档窗口尺寸（NativeMainLayoutAcceptance.WindowSizes）。 */
const WINDOW_SIZES = [[1200, 850], [830, 650], [700, 480]];

/** 与 NativeMainLayoutAcceptance.RunAsync 相同的场景：12 张素材、4 个 EXIF 项、6 个命令、显示评分、相似面板收起。 */
const ACCEPTANCE_SCENARIO = {
    fileBarVisible: true,
    controlVisible: true,
    photoCount: 12,
    currentIndex: 0,
    exifItems: 4,
    commands: geometry.DEFAULT_COMMANDS.slice(),
    showRating: true,
    similarityOpen: false,
    analysisVisible: false,
    macAppearance: true,
    countWidth: geometry.REFERENCE_COUNT_WIDTH
};

function round(value) {
    const rounded = Number(value.toFixed(2));
    return Object.is(rounded, -0) ? 0 : rounded;
}

function roundNodes(nodes) {
    const output = {};
    for (const [name, frame] of Object.entries(nodes)) output[name] = frame.map(round);
    return output;
}

function layout(mode, width, height) {
    const result = geometry.computeGeometry(tokens, Object.assign({}, ACCEPTANCE_SCENARIO, {
        layout: mode,
        width,
        height
    }));
    return roundNodes(result.nodes);
}

const layouts = {};
for (const mode of ["col", "row"]) {
    for (const [width, height] of WINDOW_SIZES) {
        const name = (mode === "col" ? "Col" : "Row") + `-${width}x${height}`;
        layouts[name] = layout(mode, width, height);
    }
}

// 自动布局：Mac 只记录 photoPane（SHELL-01：按内容区宽高比在行列间切换）。
for (const [width, height] of [[1200, 850], [700, 850], [850, 700]]) {
    const result = geometry.computeGeometry(tokens, Object.assign({}, ACCEPTANCE_SCENARIO, {
        layout: "auto",
        width,
        height
    }));
    layouts[`Auto-${width}x${height}`] = { photoPane: result.nodes["root.photoPane"].map(round) };
}

const payload = { source: "prototype", layouts };
const json = JSON.stringify(payload, null, 2) + "\n";
const index = process.argv.indexOf("--out");
if (index >= 0 && process.argv[index + 1]) {
    writeFileSync(process.argv[index + 1], json, "utf8");
    console.log(`已导出 ${process.argv[index + 1]}（${Object.keys(layouts).length} 个布局）`);
} else {
    process.stdout.write(json);
}
