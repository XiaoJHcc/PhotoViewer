/**
 * 演示面板（仅 ?demo）：窗口尺寸、设置项、故障注入、状态读数与几何导出。
 * mountDemoPanel(root, shell)，shell 为 main.js 暴露的 globalThis.pv。这是开发者面板，允许说明文字。
 */
import { el, setText } from "../shell/dom.js";
import { generateFolder } from "../core/library.js";

const WINDOW_PRESETS = [
    { label: "自适应", width: null, height: null },
    { label: "1200×850", width: 1200, height: 850 },
    { label: "830×650", width: 830, height: 650 },
    { label: "700×480", width: 700, height: 480 },
    { label: "560×480", width: 560, height: 480 },
];

function clampInteger(value, min, max, fallback) {
    const number = Math.round(Number(value));
    if (!Number.isFinite(number)) return fallback;
    return Math.min(max, Math.max(min, number));
}

function group(title, nodes) {
    return el("details", { class: "pv-demo-group", open: true },
        el("summary", { class: "pv-demo-summary" }, title),
        el("div", { class: "pv-demo-body" }, nodes));
}

function button(label, onClick, primary = false) {
    return el("button", {
        class: primary ? "pv-demo-button is-primary" : "pv-demo-button",
        type: "button",
        onclick: onClick,
    }, label);
}

function checkbox(label, checked, onChange) {
    const input = el("input", { type: "checkbox", checked, onchange: (event) => onChange(event.target.checked) });
    return { input, node: el("label", { class: "pv-demo-check" }, input, el("span", {}, label)) };
}

function field(label, control) {
    return el("label", { class: "pv-demo-field" }, el("span", {}, label), control);
}

function note(text) {
    return el("div", { class: "pv-demo-note" }, text);
}

function scrollList(children) {
    return el("div", { class: "pv-demo-scroll" }, children);
}

function readoutRow(label) {
    const value = el("span", { class: "pv-demo-value" }, "—");
    return { value, node: el("div", { class: "pv-demo-readout" }, el("span", { class: "pv-demo-label" }, label), value) };
}

export function mountDemoPanel(root, shell) {
    const { app, library, store } = shell;
    const settings = () => app.settings;
    let generated = 0;
    let lastRegions = [];

    const panel = el("aside", { class: "pv-demo-panel", "aria-label": "演示面板" });
    panel.addEventListener("keydown", (event) => event.stopPropagation());

    const sizeRow = el("div", { class: "pv-demo-row" },
        WINDOW_PRESETS.map((preset) => button(preset.label, () => shell.setWindowSize(preset.width, preset.height))));
    const customWidth = el("input", { class: "pv-demo-number", type: "number", min: "200", max: "4000", step: "10", placeholder: "宽" });
    const customHeight = el("input", { class: "pv-demo-number", type: "number", min: "200", max: "4000", step: "10", placeholder: "高" });
    const customRow = el("div", { class: "pv-demo-row" },
        customWidth,
        customHeight,
        button("应用", () => {
            shell.setWindowSize(
                clampInteger(customWidth.value, 200, 4000, 1200),
                clampInteger(customHeight.value, 200, 4000, 850));
        }));
    const layoutSelect = el("select", { class: "pv-demo-select", onchange: (event) => app.updateSettings({ layoutMode: event.target.value }) },
        el("option", { value: "auto" }, "自动"),
        el("option", { value: "row" }, "行"),
        el("option", { value: "col" }, "列"));
    layoutSelect.value = settings().layoutMode;

    const folderList = el("div", { class: "pv-demo-row" });
    const generateCount = el("input", { class: "pv-demo-number", type: "number", min: "1", max: "20000", step: "100", value: 1000 });
    const generateRow = el("div", { class: "pv-demo-row" },
        generateCount,
        button("生成并打开", () => {
            const count = clampInteger(generateCount.value, 1, 20000, 1000);
            generated += 1;
            const folder = generateFolder({
                id: `generated-${generated}`,
                name: `生成 ${count} 张`,
                count,
                seed: 0x9e37 + generated * 7919,
            });
            library.folders.push(folder);
            app.openFolder(folder.id);
            renderFolders();
        }, true));

    function renderFolders() {
        folderList.textContent = "";
        for (const folder of library.folders) {
            folderList.append(button(folder.name, () => app.openFolder(folder.id)));
        }
    }

    const showRating = checkbox("显示评分", settings().showRating !== false, (value) => app.updateSettings({ showRating: value }));
    const sameName = checkbox("同名合并", settings().sameNameAsOne !== false, (value) => app.updateSettings({ sameNameAsOne: value }));
    const navigator = checkbox("导航图", settings().showZoomIndicator !== false, (value) => app.updateSettings({ showZoomIndicator: value }));
    const immediate = checkbox("即时计算 DINO / CV", Boolean(settings().analysisImmediateCompute), (value) => app.updateSettings({ analysisImmediateCompute: value }));
    const appleKeyboard = checkbox("Apple 键盘映射", Boolean(settings().appleKeyboard), (value) => app.updateSettings({ appleKeyboard: value }));

    const thresholdValue = el("span", { class: "pv-demo-value" }, settings().similarityThreshold.toFixed(2));
    const thresholdRange = el("input", {
        class: "pv-demo-range",
        type: "range",
        min: "0.75",
        max: "0.95",
        step: "0.01",
        value: settings().similarityThreshold,
        oninput: (event) => app.updateSettings({ similarityThreshold: Number(event.target.value) }),
    });
    const maxResultsValue = el("span", { class: "pv-demo-value" }, String(settings().similarityMaxResults));
    const maxResultsRange = el("input", {
        class: "pv-demo-range",
        type: "range",
        min: "1",
        max: "32",
        step: "1",
        value: settings().similarityMaxResults,
        oninput: (event) => app.updateSettings({ similarityMaxResults: Number(event.target.value) }),
    });

    const exifInputs = new Map();
    const exifList = scrollList(settings().exifItems.map((item) => {
        const row = checkbox(item.label, item.enabled !== false, (value) => {
            const next = settings().exifItems.map((entry) => (
                entry.id === item.id ? { ...entry, enabled: value } : { ...entry }
            ));
            app.updateSettings({ exifItems: next });
        });
        exifInputs.set(item.id, row.input);
        return row.node;
    }));
    const hotkeyInputs = new Map();
    const hotkeyList = scrollList(settings().hotkeys.map((row) => {
        const item = checkbox(row.name, Boolean(row.display), (value) => {
            const next = settings().hotkeys.map((entry) => (
                entry.command === row.command ? { ...entry, display: value } : { ...entry }
            ));
            app.updateSettings({ hotkeys: next });
        });
        hotkeyInputs.set(row.command, item.input);
        return item.node;
    }));
    const formatInputs = new Map();
    const formatList = scrollList(settings().formats.map((row) => {
        const item = checkbox(row.id, row.enabled !== false, (value) => {
            const next = settings().formats.map((entry) => (
                entry.id === row.id ? { ...entry, enabled: value } : { ...entry }
            ));
            app.updateSettings({ formats: next });
        });
        formatInputs.set(row.id, item.input);
        return item.node;
    }));

    const latencySelect = el("select", {
        class: "pv-demo-select",
        onchange: (event) => shell.decoder.setLatencyMultiplier(Number(event.target.value)),
    },
    el("option", { value: "0.25" }, "0.25×"),
    el("option", { value: "1", selected: true }, "1×"),
    el("option", { value: "4" }, "4×"),
    el("option", { value: "10" }, "10×"));
    const failNextInput = el("input", { class: "pv-demo-number", type: "number", min: "0", max: "5", step: "1", value: store.failNext ?? 0 });
    const failNextNote = el("span", { class: "pv-demo-note" }, `failNext = ${store.failNext ?? 0}`);
    failNextInput.addEventListener("change", () => {
        store.failNext = clampInteger(failNextInput.value, 0, 5, 0);
        setText(failNextNote, `failNext = ${store.failNext}`);
    });
    const motionSelect = el("select", {
        class: "pv-demo-select",
        onchange: (event) => shell.setReducedMotion(event.target.value === "system" ? null : event.target.value === "on"),
    },
    el("option", { value: "system" }, "跟随系统"),
    el("option", { value: "on" }, "开"),
    el("option", { value: "off" }, "关"));

    const photoRow = readoutRow("当前照片");
    const anchorRow = readoutRow("锚点");
    const displayedRow = readoutRow("主图");
    const viewportRow = readoutRow("视口");
    const filmRow = readoutRow("胶片栏动画");
    const regionsRow = readoutRow("最近 regions");

    function refreshReadout() {
        const state = app.state;
        setText(photoRow.value, state.photo?.id ?? "—");
        setText(anchorRow.value, state.anchorId ?? "—");
        setText(displayedRow.value, state.displayedId ?? "—");
        setText(viewportRow.value, state.viewport.percentText || "—");
        setText(filmRow.value, app.filmAnimating ? "播放中" : "静止");
        setText(regionsRow.value, lastRegions.length ? lastRegions.join(", ") : "—");
    }

    function syncSettings() {
        const current = settings();
        showRating.input.checked = current.showRating !== false;
        sameName.input.checked = current.sameNameAsOne !== false;
        navigator.input.checked = current.showZoomIndicator !== false;
        immediate.input.checked = Boolean(current.analysisImmediateCompute);
        appleKeyboard.input.checked = Boolean(current.appleKeyboard);
        if (document.activeElement !== thresholdRange) thresholdRange.value = current.similarityThreshold;
        setText(thresholdValue, current.similarityThreshold.toFixed(2));
        if (document.activeElement !== maxResultsRange) maxResultsRange.value = current.similarityMaxResults;
        setText(maxResultsValue, String(current.similarityMaxResults));
        for (const item of current.exifItems) {
            const input = exifInputs.get(item.id);
            if (input) input.checked = item.enabled !== false;
        }
        for (const row of current.hotkeys) {
            const input = hotkeyInputs.get(row.command);
            if (input) input.checked = Boolean(row.display);
        }
        for (const row of current.formats) {
            const input = formatInputs.get(row.id);
            if (input) input.checked = row.enabled !== false;
        }
        layoutSelect.value = current.layoutMode;
    }

    function exportGeometry() {
        const state = app.state;
        const mode = state.layout.layoutMode;
        const prefix = mode === "col" ? "Col" : mode === "row" ? "Row" : "Auto";
        const name = `${prefix}-${Math.round(state.window.width)}x${Math.round(state.window.height)}`;
        const nodes = {};
        for (const [key, frame] of Object.entries(state.layout.nodes)) {
            nodes[key] = frame.map((value) => Math.round(value * 100) / 100);
        }
        const payload = { source: "web", profile: shell.t.profile, layouts: { [name]: nodes } };
        const blob = new Blob([`${JSON.stringify(payload, null, 2)}\n`], { type: "application/json" });
        const url = URL.createObjectURL(blob);
        const link = el("a", { href: url, download: `web-geometry-${name}.json` });
        document.body.append(link);
        link.click();
        link.remove();
        setTimeout(() => URL.revokeObjectURL(url), 0);
    }

    panel.append(
        group("窗口", [
            sizeRow,
            customRow,
            field("布局模式", layoutSelect),
        ]),
        group("文件夹", [
            folderList,
            generateRow,
        ]),
        group("设置", [
            showRating.node,
            sameName.node,
            navigator.node,
            immediate.node,
            appleKeyboard.node,
            el("div", { class: "pv-demo-field" },
                el("span", {}, "相似阈值"),
                el("span", { class: "pv-demo-inline" }, thresholdRange, thresholdValue)),
            el("div", { class: "pv-demo-field" },
                el("span", {}, "最多数量"),
                el("span", { class: "pv-demo-inline" }, maxResultsRange, maxResultsValue)),
            el("div", { class: "pv-demo-label" }, "EXIF 显示项"),
            exifList,
            el("div", { class: "pv-demo-label" }, "控制栏命令显示"),
            hotkeyList,
            el("div", { class: "pv-demo-label" }, "格式启用"),
            formatList,
        ]),
        group("故障与时序", [
            field("解码延迟倍率", latencySelect),
            field("下一次提取失败", failNextInput),
            failNextNote,
            button("模拟内存告警", () => app.simulateMemoryWarning()),
            field("减少动态效果", motionSelect),
        ]),
        group("状态读数", [
            photoRow.node,
            anchorRow.node,
            displayedRow.node,
            viewportRow.node,
            filmRow.node,
            regionsRow.node,
        ]),
        group("几何导出", [
            button("导出几何 JSON", exportGeometry),
            note("与 tools/export-geometry.mjs 的单布局格式一致。"),
        ]),
    );

    renderFolders();
    refreshReadout();
    root.append(panel);

    app.on("change", (regions) => {
        lastRegions = [...regions];
        refreshReadout();
        if (regions.has("settings")) syncSettings();
    });
}
