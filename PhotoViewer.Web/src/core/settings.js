/**
 * 设置模型默认值：只收影响主屏的项（spec/settings.md SETTINGS-06–10、input.md INPUT-01）。
 * 手势用规范字符串：修饰键按 Ctrl/Alt/Shift/Meta 顺序以 "+" 连接，主键取 KeyboardEvent.code
 * （"ArrowLeft"、"KeyN"、"Digit0"、"Equal"、"Minus"、"Backquote"、"Numpad0"）或鼠标动作
 * （"WheelUp"、"WheelDown"、"MiddleClick"）。例："Ctrl+KeyN"、"Ctrl+WheelUp"。
 */

export const FORMATS = Object.freeze([
    { id: "JPG", exts: [".jpg", ".jpeg"], enabled: true },
    { id: "HEIF", exts: [".heif", ".heic", ".avif", ".hif"], enabled: true },
    { id: "PNG", exts: [".png"], enabled: true },
    { id: "TIFF", exts: [".tiff", ".tif"], enabled: false },
    { id: "WebP", exts: [".webp"], enabled: true },
    { id: "RAW", exts: [".cr2", ".cr3", ".nef", ".arw", ".dng", ".raf", ".orf", ".rw2", ".srw"], enabled: true },
    { id: "BMP", exts: [".bmp"], enabled: false },
    { id: "GIF", exts: [".gif"], enabled: false },
]);

export const EXIF_ITEMS = Object.freeze([
    { id: "aperture", label: "光圈", enabled: true },
    { id: "exposure", label: "快门", enabled: true },
    { id: "iso", label: "ISO", enabled: true },
    { id: "equivFocal", label: "等效焦距", enabled: true },
    { id: "focal", label: "实际焦距", enabled: false },
    { id: "camera", label: "相机型号", enabled: false },
    { id: "lens", label: "镜头型号", enabled: false },
    { id: "taken", label: "拍摄时间", enabled: false },
    { id: "ev", label: "曝光补偿", enabled: false },
    { id: "wb", label: "白平衡", enabled: false },
    { id: "flash", label: "闪光灯", enabled: false },
]);

export const HOTKEYS = Object.freeze([
    { command: "Open", name: "打开文件", display: false, primary: "Ctrl+KeyN", secondary: "Ctrl+KeyO" },
    { command: "Previous", name: "上一张", display: true, primary: "ArrowLeft", secondary: "WheelUp" },
    { command: "Next", name: "下一张", display: true, primary: "ArrowRight", secondary: "WheelDown" },
    { command: "Exchange", name: "切换上一张", display: true, primary: "KeyZ", secondary: "MiddleClick" },
    { command: "Fit", name: "缩放适应", display: true, primary: "Ctrl+Digit0", secondary: "KeyF" },
    { command: "ZoomInPreset", name: "放大（预设）", display: true, primary: "Ctrl+Equal", secondary: null },
    { command: "ZoomOutPreset", name: "缩小（预设）", display: true, primary: "Ctrl+Minus", secondary: null },
    { command: "ZoomInScale", name: "放大（比例）", display: false, primary: "Ctrl+WheelUp", secondary: null },
    { command: "ZoomOutScale", name: "缩小（比例）", display: false, primary: "Ctrl+WheelDown", secondary: null },
    { command: "ToggleAnalysisView", name: "分析栏展开/收纳", display: false, primary: null, secondary: null },
    { command: "ToggleEnhance", name: "雾天增强", display: true, primary: "KeyE", secondary: null },
    { command: "SetRating0", name: "评分 0 星", display: false, primary: "Backquote", secondary: "Numpad0" },
    { command: "SetRating1", name: "评分 1 星", display: false, primary: "Digit1", secondary: "Numpad1" },
    { command: "SetRating2", name: "评分 2 星", display: false, primary: "Digit2", secondary: "Numpad2" },
    { command: "SetRating3", name: "评分 3 星", display: false, primary: "Digit3", secondary: "Numpad3" },
    { command: "SetRating4", name: "评分 4 星", display: false, primary: "Digit4", secondary: "Numpad4" },
    { command: "SetRating5", name: "评分 5 星", display: false, primary: "Digit5", secondary: "Numpad5" },
]);

export function defaultSettings() {
    return {
        layoutMode: "auto",
        showZoomIndicator: true,
        zoomPresets: [12.5, 25, 33.333, 50, 75, 100, 200, 400],
        showRating: true,
        safeSetRating: true,
        sameNameAsOne: true,
        formats: FORMATS.map((f) => ({ ...f, exts: [...f.exts] })),
        exifItems: EXIF_ITEMS.map((e) => ({ ...e })),
        hotkeys: HOTKEYS.map((h) => ({ ...h })),
        appleKeyboard: isApplePlatform(),
        similarityThreshold: 0.85,
        similarityMaxResults: 8,
        analysisImmediateCompute: false,
        similarityPanelExpanded: false,
    };
}

export function isApplePlatform() {
    const nav = globalThis.navigator;
    if (!nav) return false;
    const platform = nav.userAgentData?.platform ?? nav.platform ?? "";
    return /mac|iphone|ipad|ipod/i.test(platform);
}
