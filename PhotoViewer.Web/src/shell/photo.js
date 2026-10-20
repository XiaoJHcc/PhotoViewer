/**
 * 主图（IMAGE / STATE / INPUT-03/04/06/07/10 / OPEN-01/07 / ANALYSIS-04）。
 * canvas 上屏：按 EXIF 方向画存储朝向的表面，几何只取视口（IMAGE-01/08）；
 * 叠加层：空态、加载、失败、导航图、计算中药丸、提示条、分析绿框；
 * 输入全部翻译成 app 动作（双击/拖动/滚轮/捏合/轻扫/中键/拖入），组件内不判定业务条件。
 * mountPhoto(host, ctx) → { update(state, regions), destroy() }
 */
import { el, place, setText, touches } from "./dom.js";
import { orientationMatrix } from "../render/procedural.js";
import { PhotoGesture, SwipeRecognizer } from "../core/input.js";

const REGIONS = ["layout", "window", "viewport", "image", "current", "toast", "analysis", "settings"];
const DRAW_REGIONS = ["layout", "window", "viewport", "image", "current", "settings"];
const TOAST_LINE_HEIGHT = 16;
const LOADING_LINE_HEIGHT = 16;
const DETAIL_LINE_HEIGHT = 15;
const DOUBLE_TAP_MS = 300;
const DOUBLE_TAP_SLOP = 32;
const TAP_SLOP = 6;
const EMPTY_TITLE = "点击打开文件 或 拖拽图片到此处";
const LOADING_TEXT = "正在载入…";
const NO_LINE_START = "、。，．,.;:：；！？!?）)】」』》〉”’…—～";
const IMAGE_FILE = /\.(jpe?g|png|heic|heif|hif|tiff?|dng|arw|cr2|cr3|nef|raf|orf|rw2)$/i;

function clamp01(value) {
    return Math.max(0, Math.min(1, value));
}

function rel(rect, pane) {
    return { x: rect.x - pane.x, y: rect.y - pane.y, w: rect.w, h: rect.h };
}

function sizeCanvas(node, width, height, dpr) {
    const w = Math.max(1, Math.round(width * dpr));
    const h = Math.max(1, Math.round(height * dpr));
    if (node.width !== w) node.width = w;
    if (node.height !== h) node.height = h;
}

export function mountPhoto(host, ctx) {
    const { app, t, measureText } = ctx;

    const canvas = el("canvas", { class: "pv-photo-canvas", role: "img", "aria-label": "主图" });
    const highlight = el("div", { class: "pv-photo-highlight", hidden: true, "aria-hidden": "true" });
    const hintTitle = el("button", { type: "button", class: "pv-photo-hint-title", hidden: true }, EMPTY_TITLE);
    const hint = el("div", { class: "pv-photo-hint", hidden: true }, hintTitle);
    const loading = el("div", { class: "pv-photo-loading", role: "status", hidden: true }, LOADING_TEXT);
    const errorTitle = el("div", { class: "pv-photo-error-title" });
    const errorDetail = el("div", { class: "pv-photo-error-detail" });
    const errorLayer = el("div", { class: "pv-photo-error", role: "alert", hidden: true }, errorTitle, errorDetail);
    const navThumb = el("canvas", { class: "pv-photo-nav-thumb" });
    const navFrame = el("div", { class: "pv-photo-nav-frame" });
    const navLabel = el("div", { class: "pv-photo-nav-label" });
    const navEl = el("div", { class: "pv-photo-nav", hidden: true, "aria-hidden": "true" }, navThumb, navFrame, navLabel);
    const busy = el("div", { class: "pv-photo-busy", role: "status", hidden: true }, "计算中…");
    const toast = el("div", { class: "pv-photo-toast", role: "status", hidden: true });

    host.append(canvas, highlight, hint, loading, errorLayer, navEl, busy, toast);
    host.classList.add("pv-photo");

    const swipe = new SwipeRecognizer(t);
    const gesture = new PhotoGesture({ tapSlop: TAP_SLOP });
    let lastTap = null;
    let lastPointerType = "mouse";
    let gestureScale = 1;
    let latest = null;
    let frame = 0;
    let navSurface = null;
    let navSignature = "";

    function draw() {
        frame = 0;
        const state = latest;
        if (!state) return;
        const g = canvas.getContext("2d");
        if (!g) return;
        const dpr = state.viewport.dpr || 1;
        g.setTransform(1, 0, 0, 1, 0, 0);
        g.clearRect(0, 0, canvas.width, canvas.height);
        const surface = state.session.surface;
        const photo = state.photo;
        const view = state.viewport;
        if (!surface || !photo || !view.hasImage) return;
        const rect = view.imageRect;
        const srcW = view.image.width;
        const srcH = view.image.height;
        if (!(rect.w > 0) || !(rect.h > 0) || !(srcW > 0) || !(srcH > 0)) return;
        // IMAGE-08：存储朝向的位图按方向表变换到显示朝向，宽高 ≥5 换轴。
        const orientation = photo.orientation ?? 1;
        const storedW = orientation >= 5 ? srcH : srcW;
        const storedH = orientation >= 5 ? srcW : srcH;
        const matrix = orientationMatrix(orientation, storedW, storedH);
        const scale = rect.w / srcW;
        g.setTransform(dpr, 0, 0, dpr, 0, 0);
        // IMAGE-01：物理 100% 起关闭插值，1 源像素对 1 物理像素；低于 100% 用高质量缩放。
        g.imageSmoothingEnabled = view.physicalScale < 1;
        g.imageSmoothingQuality = "high";
        g.save();
        g.translate(rect.x, rect.y);
        g.scale(scale, scale);
        g.transform(matrix.a, matrix.b, matrix.c, matrix.d, matrix.e, matrix.f);
        g.drawImage(surface, 0, 0, storedW, storedH);
        g.restore();
    }

    function requestDraw() {
        if (frame) return;
        frame = requestAnimationFrame(draw);
    }

    function drawNavigator(state, thumb, dpr) {
        const surface = state.session.surface;
        const photo = state.photo;
        const view = state.viewport;
        const signature = `${Math.round(thumb.w)}x${Math.round(thumb.h)}@${dpr}:${photo ? photo.orientation : 1}`;
        if (surface === navSurface && signature === navSignature) return;
        navSurface = surface;
        navSignature = signature;
        sizeCanvas(navThumb, thumb.w, thumb.h, dpr);
        const g = navThumb.getContext("2d");
        if (!g) return;
        g.setTransform(1, 0, 0, 1, 0, 0);
        g.clearRect(0, 0, navThumb.width, navThumb.height);
        if (!surface || !photo || !view.hasImage) return;
        const srcW = view.image.width;
        const srcH = view.image.height;
        if (!(srcW > 0) || !(srcH > 0)) return;
        const orientation = photo.orientation ?? 1;
        const storedW = orientation >= 5 ? srcH : srcW;
        const storedH = orientation >= 5 ? srcW : srcH;
        const matrix = orientationMatrix(orientation, storedW, storedH);
        // IMAGE-06：缩略图取当前展示产物，只在产物或方向变化时重画。
        const scale = Math.min(thumb.w / srcW, thumb.h / srcH);
        g.setTransform(dpr, 0, 0, dpr, 0, 0);
        g.imageSmoothingEnabled = true;
        g.imageSmoothingQuality = "high";
        g.translate((thumb.w - srcW * scale) / 2, (thumb.h - srcH * scale) / 2);
        g.scale(scale, scale);
        g.transform(matrix.a, matrix.b, matrix.c, matrix.d, matrix.e, matrix.f);
        g.drawImage(surface, 0, 0, storedW, storedH);
    }

    function wrapLines(text, fontSize, maxWidth) {
        const source = String(text ?? "");
        const lines = [];
        let line = "";
        for (const char of source) {
            if (char === "\n") {
                lines.push(line);
                line = "";
                continue;
            }
            // 中文排版禁则：标点不出现在行首，宁可略微超出最大宽。
            if (line && !NO_LINE_START.includes(char) && measureText(line + char, fontSize) > maxWidth) {
                lines.push(line);
                line = char;
            } else {
                line += char;
            }
        }
        if (line || lines.length === 0) lines.push(line);
        return lines;
    }

    function syncHint(state, pane, empty) {
        hint.hidden = !empty;
        hintTitle.hidden = !empty;
        if (!empty) return;
        place(hint, { x: 0, y: 0, w: pane.w, h: pane.h });
        const frames = state.layout.overlays.hintFrames(pane);
        // STATE-01：副提示可省略；只有标题时按主图区垂直居中。
        place(hintTitle, {
            x: frames.title.x - pane.x,
            y: Math.max(0, (pane.h - frames.title.h) / 2),
            w: frames.title.w,
            h: frames.title.h,
        });
    }

    function syncLoading(state, pane) {
        const visible = Boolean(state.photo) && state.session.loadingHint && !state.session.error;
        loading.hidden = !visible;
        if (!visible) return;
        const width = Math.min(pane.w, Math.ceil(measureText(LOADING_TEXT, t("image.hint.detail.fontSize"))) + 16);
        place(loading, {
            x: (pane.w - width) / 2,
            y: (pane.h - LOADING_LINE_HEIGHT) / 2,
            w: width,
            h: LOADING_LINE_HEIGHT,
        });
    }

    function syncError(state, pane) {
        const error = state.session.error;
        errorLayer.hidden = !error;
        if (!error) return;
        const frames = state.layout.overlays.hintFrames(pane);
        place(errorLayer, { x: 0, y: 0, w: pane.w, h: pane.h });
        setText(errorTitle, error.title);
        setText(errorDetail, error.detail ?? "");
        place(errorTitle, rel(frames.title, pane));
        const detail = frames.detail;
        place(errorDetail, {
            x: detail.x - pane.x,
            y: detail.y - pane.y - (DETAIL_LINE_HEIGHT - detail.h) / 2,
            w: detail.w,
            h: DETAIL_LINE_HEIGHT,
        });
    }

    function syncNavigator(state, pane) {
        const navigator = state.viewport.navigator;
        const visible = navigator.visible && Boolean(state.photo) && Boolean(state.session.surface);
        navEl.hidden = !visible;
        if (!visible) {
            navSurface = null;
            return;
        }
        const hostFrame = state.layout.overlays.navigatorFrame(pane, navigator.thumbWidth, navigator.thumbHeight);
        place(navEl, rel(hostFrame, pane));
        place(navThumb, {
            x: hostFrame.thumb.x - hostFrame.x,
            y: hostFrame.thumb.y - hostFrame.y,
            w: hostFrame.thumb.w,
            h: hostFrame.thumb.h,
        });
        place(navFrame, {
            x: hostFrame.thumb.x - hostFrame.x + navigator.frame.x,
            y: hostFrame.thumb.y - hostFrame.y + navigator.frame.y,
            w: navigator.frame.w,
            h: navigator.frame.h,
        });
        place(navLabel, {
            x: hostFrame.label.x - hostFrame.x,
            y: hostFrame.label.y - hostFrame.y,
            w: hostFrame.label.w,
            h: hostFrame.label.h,
        });
        setText(navLabel, state.viewport.percentText);
        drawNavigator(state, hostFrame.thumb, state.viewport.dpr || 1);
    }

    function navPointOf(event) {
        const navigator = app.state.viewport.navigator;
        if (!navigator || !(navigator.thumbWidth > 0) || !(navigator.thumbHeight > 0)) return null;
        const rect = navEl.getBoundingClientRect();
        const localX = event.clientX - rect.left - navThumb.offsetLeft;
        const localY = event.clientY - rect.top - navThumb.offsetTop;
        return {
            navigator,
            localX,
            localY,
            u: clamp01(localX / navigator.thumbWidth),
            v: clamp01(localY / navigator.thumbHeight),
        };
    }

    function dragNavigator(point, offsetU, offsetV) {
        app.navigateTo(clamp01(point.u - offsetU), clamp01(point.v - offsetV));
    }

    // IMAGE-06：按在框内保留抓取偏移；按在缩略图其他位置先把框心移到该点。
    function navigatorGrab(event) {
        if (navEl.hidden || !navEl.contains(event.target)) return null;
        const point = navPointOf(event);
        if (!point) return null;
        const frame = point.navigator.frame;
        const inside = point.localX >= frame.x && point.localX <= frame.x + frame.w
            && point.localY >= frame.y && point.localY <= frame.y + frame.h;
        const centerU = (frame.x + frame.w / 2) / point.navigator.thumbWidth;
        const centerV = (frame.y + frame.h / 2) / point.navigator.thumbHeight;
        return {
            point,
            offsetU: inside ? point.u - centerU : 0,
            offsetV: inside ? point.v - centerV : 0,
        };
    }

    function syncBusy(state, pane) {
        busy.hidden = !state.session.busy;
        if (state.session.busy) place(busy, rel(state.layout.overlays.busyPillFrame(pane), pane));
    }

    function syncToast(state, pane) {
        const notice = state.session.toast;
        toast.hidden = !notice;
        if (!notice) return;
        const fontSize = t("status.toast.fontSize");
        const maxWidth = Math.max(40, t("status.toast.maxWidth") - t("status.toast.paddingX") * 2);
        const lines = wrapLines(notice.text, fontSize, maxWidth);
        const textWidth = lines.reduce((max, line) => Math.max(max, measureText(line, fontSize)), 0);
        const textHeight = lines.length * TOAST_LINE_HEIGHT;
        setText(toast, lines.join("\n"));
        place(toast, rel(state.layout.overlays.toastFrame(pane, textWidth, textHeight, state.session.busy), pane));
    }

    function syncHighlight(state) {
        const box = state.analysis.highlight;
        const view = state.viewport;
        const srcW = view.image.width;
        const rect = view.imageRect;
        const visible = Boolean(box) && view.hasImage && srcW > 0 && rect.w > 0;
        highlight.hidden = !visible;
        if (!visible) return;
        const scale = rect.w / srcW;
        // ANALYSIS-04：源像素矩形随缩放/平移换算到视口，绿框随每帧变化。
        place(highlight, {
            x: rect.x + (box.cx - box.w / 2) * scale,
            y: rect.y + (box.cy - box.h / 2) * scale,
            w: Math.max(1, box.w * scale),
            h: Math.max(1, box.h * scale),
        });
    }

    function sync(state, redraw) {
        const pane = state.layout.panes.photoPane;
        const dpr = state.viewport.dpr || 1;
        place(canvas, { x: 0, y: 0, w: pane.w, h: pane.h });
        sizeCanvas(canvas, pane.w, pane.h, dpr);
        const photo = state.photo;
        canvas.setAttribute("aria-label", photo ? `照片 ${photo.name}` : "主图");
        host.classList.toggle("is-zoomed", Boolean(photo) && state.viewport.hasImage && !state.viewport.isFit);
        syncHint(state, pane, !photo);
        syncLoading(state, pane);
        syncError(state, pane);
        syncNavigator(state, pane);
        syncBusy(state, pane);
        syncToast(state, pane);
        syncHighlight(state);
        if (redraw) requestDraw();
    }

    function update(state, regions) {
        latest = state;
        if (!touches(regions, ...REGIONS)) return;
        sync(state, touches(regions, ...DRAW_REGIONS));
    }

    function pointOf(event) {
        const rect = host.getBoundingClientRect();
        return { x: event.clientX - rect.left, y: event.clientY - rect.top };
    }

    function capture(event) {
        try {
            host.setPointerCapture(event.pointerId);
        } catch {
            // 浏览器不支持指针捕获时按事件冒泡继续
        }
    }

    function release(event) {
        try {
            host.releasePointerCapture(event.pointerId);
        } catch {
            // 已释放
        }
    }

    function endPan() {
        host.classList.remove("is-panning");
        gesture.pan = null;
    }

    function onPointerDown(event) {
        if (event.button === 1) {
            // INPUT-03：中键按用户绑定表（默认切换上一张），命中才吞掉自动滚动。
            const result = app.mouseOnPhoto({
                button: 1,
                ctrlKey: event.ctrlKey,
                altKey: event.altKey,
                shiftKey: event.shiftKey,
                metaKey: event.metaKey,
            });
            if (result && result.handled) event.preventDefault();
            return;
        }
        if (event.button !== 0) return;
        const state = app.state;
        lastPointerType = event.pointerType || "mouse";
        const point = pointOf(event);
        const zoomed = state.viewport.hasImage && !state.viewport.isFit;
        const touch = event.pointerType === "touch";
        const grab = navigatorGrab(event);
        capture(event);
        const result = gesture.begin({
            id: event.pointerId,
            x: point.x,
            y: point.y,
            touch,
            zoomed,
            navigator: grab,
        });
        // IMAGE-06：导航图拖动中出现第二指即让位，两指交给主图捏合。
        if (result.navigatorCanceled) navEl.classList.remove("is-dragging");
        if (result.mode === "navigator") {
            navEl.classList.add("is-dragging");
            host.classList.remove("is-panning");
            if (grab) dragNavigator(grab.point, grab.offsetU, grab.offsetV);
        } else if (result.mode === "pan") {
            host.classList.add("is-panning");
        } else {
            host.classList.remove("is-panning");
        }
        if (touch) swipe.start(event.pointerId, point.x, point.y);
    }

    function onPointerMove(event) {
        const point = pointOf(event);
        const result = gesture.move({ id: event.pointerId, x: point.x, y: point.y });
        if (!result) return;
        if (result.mode === "pinch") {
            // INPUT-03：捏合锚点在两指中点。
            if (result.ratio) app.zoomBy(result.ratio, result.center);
            return;
        }
        if (result.mode === "navigator") {
            const navPoint = navPointOf(event);
            event.preventDefault();
            if (navPoint) dragNavigator(navPoint, result.offsetU, result.offsetV);
            return;
        }
        if (result.mode === "pan") {
            if (result.dx || result.dy) app.pan(result.dx, result.dy);
            return;
        }
        if (result.mode === "swipe") swipe.move(event.pointerId, point.x, point.y);
    }

    function handleTap(point) {
        const now = performance.now();
        if (lastTap && now - lastTap.time <= DOUBLE_TAP_MS
            && Math.hypot(point.x - lastTap.x, point.y - lastTap.y) <= DOUBLE_TAP_SLOP) {
            lastTap = null;
            app.fit(point);
            return;
        }
        lastTap = { time: now, x: point.x, y: point.y };
    }

    function onPointerUp(event) {
        const id = event.pointerId;
        const point = pointOf(event);
        const result = gesture.end(id);
        if (!result) return;
        release(event);
        if (result.navigatorEnded) navEl.classList.remove("is-dragging");
        if (result.touch) {
            const swipeResult = swipe.end(id);
            // INPUT-06：适应态单指横滑翻页；放大态横滑是平移，不翻页。
            if (result.mode === "swipe" && swipeResult) {
                app.navigate(swipeResult === "next" ? 1 : -1);
                result.tap = false;
            }
        }
        if (result.mode === "pinch") return;
        if (result.touch && result.tap) handleTap(point);
        if (result.done) endPan();
    }

    function onPointerCancel(event) {
        const id = event.pointerId;
        const result = gesture.cancel(id);
        if (event.pointerType === "touch") swipe.end(id);
        release(event);
        if (result?.navigatorEnded) navEl.classList.remove("is-dragging");
        if (result?.done) endPan();
    }

    function onWheel(event) {
        const result = app.wheelOnPhoto({
            deltaX: event.deltaX,
            deltaY: event.deltaY,
            deltaMode: event.deltaMode,
            ctrlKey: event.ctrlKey,
            altKey: event.altKey,
            shiftKey: event.shiftKey,
            metaKey: event.metaKey,
            wheelDeltaY: event.wheelDeltaY,
        }, pointOf(event));
        if (result && result.handled) event.preventDefault();
    }

    function onDoubleClick(event) {
        if (lastPointerType === "touch") return;
        app.fit(pointOf(event));
    }

    function onGestureStart(event) {
        gestureScale = 1;
        event.preventDefault();
    }

    function onGestureChange(event) {
        const ratio = gestureScale > 0 ? event.scale / gestureScale : 1;
        gestureScale = event.scale;
        if (ratio > 0) app.zoomBy(ratio, pointOf(event));
        event.preventDefault();
    }

    function onClick(event) {
        if (event.button !== 0) return;
        if (app.state.photo) return;
        // INPUT-10 / OPEN-01：空态主图区单击打开文件选择器。
        app.requestOpen("file");
    }

    function onHintClick(event) {
        event.stopPropagation();
        app.requestOpen("file");
    }

    function hasFiles(event) {
        const transfer = event.dataTransfer;
        return Boolean(transfer) && Array.from(transfer.types ?? []).includes("Files");
    }

    function findPhotoByName(name) {
        const wanted = String(name ?? "").toLowerCase();
        if (!wanted) return null;
        for (const folder of ctx.library?.folders ?? []) {
            for (const photo of folder.photos ?? []) {
                if (photo.name && photo.name.toLowerCase() === wanted) return photo;
            }
        }
        return null;
    }

    function onDragOver(event) {
        if (!hasFiles(event)) return;
        event.preventDefault();
        event.dataTransfer.dropEffect = "copy";
        host.classList.add("is-drop-target");
    }

    function onDragLeave(event) {
        if (event.relatedTarget && host.contains(event.relatedTarget)) return;
        host.classList.remove("is-drop-target");
    }

    function onDrop(event) {
        event.preventDefault();
        host.classList.remove("is-drop-target");
        const files = Array.from(event.dataTransfer?.files ?? []);
        if (files.length === 0) return;
        const photo = findPhotoByName(files[0].name);
        if (photo) {
            // OPEN-07@web：合成库里同名文件按外部打开同语义选中。
            app.openFile(photo.id);
            return;
        }
        if (!files.some((file) => IMAGE_FILE.test(file.name))) return;
        const folder = (ctx.library?.folders ?? []).find((entry) => entry.enumerable !== false && (entry.photos ?? []).length > 0);
        // OPEN-07@web：拖入的真实图片没有可枚举的父目录，落到首个可浏览的合成文件夹。
        if (folder) app.openFolder(folder.id);
    }

    host.addEventListener("pointerdown", onPointerDown);
    host.addEventListener("pointermove", onPointerMove);
    host.addEventListener("pointerup", onPointerUp);
    host.addEventListener("pointercancel", onPointerCancel);
    host.addEventListener("wheel", onWheel, { passive: false });
    host.addEventListener("dblclick", onDoubleClick);
    host.addEventListener("click", onClick);
    host.addEventListener("gesturestart", onGestureStart);
    host.addEventListener("gesturechange", onGestureChange);
    host.addEventListener("dragover", onDragOver);
    host.addEventListener("dragleave", onDragLeave);
    host.addEventListener("drop", onDrop);
    hintTitle.addEventListener("click", onHintClick);

    return {
        update,
        destroy() {
            if (frame) cancelAnimationFrame(frame);
            host.removeEventListener("pointerdown", onPointerDown);
            host.removeEventListener("pointermove", onPointerMove);
            host.removeEventListener("pointerup", onPointerUp);
            host.removeEventListener("pointercancel", onPointerCancel);
            host.removeEventListener("wheel", onWheel);
            host.removeEventListener("dblclick", onDoubleClick);
            host.removeEventListener("click", onClick);
            host.removeEventListener("gesturestart", onGestureStart);
            host.removeEventListener("gesturechange", onGestureChange);
            host.removeEventListener("dragover", onDragOver);
            host.removeEventListener("dragleave", onDragLeave);
            host.removeEventListener("drop", onDrop);
            hintTitle.removeEventListener("click", onHintClick);
            host.classList.remove("pv-photo", "is-zoomed", "is-panning", "is-drop-target");
            canvas.remove();
            highlight.remove();
            hint.remove();
            loading.remove();
            errorLayer.remove();
            navEl.remove();
            busy.remove();
            toast.remove();
        },
    };
}
