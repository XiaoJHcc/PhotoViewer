/**
 * 文本宽度近似（布局与几何导出用）：CJK 与全角按 1 em，其余按 0.55 em。
 * 壳层用真实字体测量（shell/dom.js createTextMeasurer），布局只接受注入的 measureText。
 */

export function approximateTextWidth(text, fontSizePt) {
    let units = 0;
    for (const char of String(text)) {
        units += char.codePointAt(0) < 0x2e80 ? 0.55 : 1;
    }
    return units * fontSizePt;
}
