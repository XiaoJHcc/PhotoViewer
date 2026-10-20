/**
 * 自绘悬停提示（TOOLBAR-04@web、MENU-03@web）：浏览器原生 title 外观不受控，改为统一组件。
 * createTooltip({ host, t }) → { destroy }
 * 触发：任意带 data-tooltip 的元素（宿主内全局委托）；延迟 motion.tooltip.delay，
 * 从上一个提示连续划到下一个控件时取 motion.tooltip.chainDelay；出现于指针右下（tooltip.offset.*），
 * 超出宿主向内夹取。按下、滚轮、离开控件、键盘操作、滚动、窗口失焦均隐藏；提示层 pointer-events: none。
 */
import { el } from "./dom.js";

export function createTooltip({ host, t }) {
    const offsetX = t("tooltip.offset.x");
    const offsetY = t("tooltip.offset.y");
    const chainDelay = t("motion.tooltip.chainDelay");
    const chainWindow = t("motion.tooltip.delay");
    const node = el("div", { class: "pv-tooltip", role: "tooltip", hidden: true });
    host.append(node);

    let timer = null;
    let current = null;
    let point = { x: 0, y: 0 };
    let lastHiddenAt = -Infinity;

    function clearTimer() {
        if (timer !== null) {
            clearTimeout(timer);
            timer = null;
        }
    }

    function hide() {
        clearTimer();
        if (!node.hidden) {
            node.hidden = true;
            lastHiddenAt = performance.now();
        }
        current = null;
    }

    function place(x, y) {
        const hostRect = host.getBoundingClientRect();
        node.hidden = false;
        const width = node.offsetWidth;
        const height = node.offsetHeight;
        // 出现在指针右下；超出宿主时向内夹取。
        const left = Math.min(x + offsetX, Math.max(0, hostRect.width - width));
        const top = Math.min(y + offsetY, Math.max(0, hostRect.height - height));
        node.style.left = `${Math.max(0, left)}px`;
        node.style.top = `${Math.max(0, top)}px`;
    }

    function show(target) {
        const text = target.dataset.tooltip ?? "";
        if (!text) return;
        node.textContent = text;
        place(point.x, point.y);
    }

    function schedule(target) {
        clearTimer();
        current = target;
        const text = target.dataset.tooltip ?? "";
        if (!text) return;
        // TOOLBAR-04：连续划过相邻控件时缩短等待，避免提示逐条重等。
        const chained = performance.now() - lastHiddenAt <= chainWindow;
        const delay = chained ? chainDelay : t("motion.tooltip.delay");
        timer = setTimeout(() => {
            timer = null;
            if (current !== target) return;
            show(target);
        }, delay);
    }

    function onPointerOver(event) {
        const target = event.target instanceof Element ? event.target.closest("[data-tooltip]") : null;
        if (!target || !host.contains(target)) {
            if (current) hide();
            return;
        }
        if (target === current) return;
        schedule(target);
    }

    function onPointerOut(event) {
        const target = event.target instanceof Element ? event.target.closest("[data-tooltip]") : null;
        if (!target || target !== current) return;
        if (event.relatedTarget instanceof Node && target.contains(event.relatedTarget)) return;
        hide();
    }

    function onPointerMove(event) {
        point = { x: event.clientX, y: event.clientY };
    }

    function onPointerDown() {
        hide();
    }

    function onWheel() {
        hide();
    }

    function onKeyDown() {
        hide();
    }

    const onScroll = () => hide();
    const onBlur = () => hide();

    host.addEventListener("pointerover", onPointerOver);
    host.addEventListener("pointerout", onPointerOut);
    host.addEventListener("pointermove", onPointerMove, { passive: true });
    window.addEventListener("pointerdown", onPointerDown, true);
    window.addEventListener("wheel", onWheel, { passive: true, capture: true });
    window.addEventListener("keydown", onKeyDown, true);
    window.addEventListener("blur", onBlur);
    host.addEventListener("scroll", onScroll, true);

    return {
        hide,
        destroy() {
            clearTimer();
            host.removeEventListener("pointerover", onPointerOver);
            host.removeEventListener("pointerout", onPointerOut);
            host.removeEventListener("pointermove", onPointerMove);
            window.removeEventListener("pointerdown", onPointerDown, true);
            window.removeEventListener("wheel", onWheel, { capture: true });
            window.removeEventListener("keydown", onKeyDown, true);
            window.removeEventListener("blur", onBlur);
            host.removeEventListener("scroll", onScroll, true);
            node.remove();
        },
    };
}
