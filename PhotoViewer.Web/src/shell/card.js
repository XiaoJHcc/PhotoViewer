import { el, place, setText } from "./dom.js";
import { codepoint, icon, solidIcon } from "./icons.js";
import { cardFrames } from "../core/layout/card.js";
import { formatTakenFull, formatTakenShort } from "../core/exifformat.js";
import { LongPressRecognizer } from "../core/input.js";
import { displayStars, hasRatingConflict } from "../core/rating.js";

const RATING_LABELS = ["无星级", "1 星", "2 星", "3 星", "4 星", "5 星"];
const PRIORITY_RANK = { visible: 0, near: 1, far: 2 };

function takenOf(photo) {
    return photo.taken ?? photo.modified;
}

function priorityRank(priority) {
    if (typeof priority === "number" && Number.isFinite(priority)) return priority;
    return PRIORITY_RANK[priority] ?? 0;
}

function ratingValue(rating) {
    const value = Math.round(Number(rating) || 0);
    return Math.max(0, Math.min(5, value));
}

function clampRatio(value) {
    const number = Number(value);
    if (!Number.isFinite(number)) return 0;
    return Math.max(0, Math.min(1, number));
}

export function createCard(ctx, { mode = "film" } = {}) {
    const { app, t, thumbs, clock, menu } = ctx;
    const frames = cardFrames(t);
    const similarity = mode === "similarity";

    const root = el("div", {
        class: `pv-card pv-card-${mode}`,
        role: similarity ? "group" : "listitem",
        tabindex: "-1",
    });
    const surface = el("div", { class: "pv-card-surface" });
    const well = el("div", { class: "pv-card-well" });
    const canvas = el("canvas", { class: "pv-card-image" });
    // FILM-05：缩略图未就绪与解码失败显示 icon.photoPlaceholder，不伪造图像。
    const placeholder = el("span", {
        class: "pv-card-placeholder pv-icon",
        "aria-hidden": "true",
    }, codepoint(t("icon.photoPlaceholder")));
    const cached = el("div", { class: "pv-card-cached" });
    const badge = el("div", { class: "pv-card-badge" }, "抖");
    const name = el("div", { class: "pv-card-name" });
    const second = el("div", { class: "pv-card-second" });
    const dateText = el("div", { class: "pv-card-date" });
    const bar = el("div", { class: "pv-card-bar" });
    const barFill = el("div", { class: "pv-card-bar-fill" });
    const barLabel = el("div", { class: "pv-card-bar-label" });
    const paint = canvas.getContext("2d");

    bar.append(barFill, barLabel);
    second.append(dateText, bar);
    dateText.hidden = similarity;
    bar.hidden = !similarity;
    well.append(canvas, placeholder, cached);
    surface.append(well, badge, name, second);
    root.append(surface);

    const stars = [];
    for (let index = 0; index < 6; index += 1) {
        const button = el("button", { type: "button", class: "pv-card-star", tabindex: "-1" });
        const label = index === 0 ? "无星级" : `${index} 星`;
        button.setAttribute("aria-label", label);
        button.dataset.tooltip = label;
        button.dataset.rating = String(index);
        if (index === 0) {
            button.classList.add("is-clear");
            // RATING-04：0 号槽位同时持有清零叉与冲突警示三角，按组级状态切换。
            button.append(
                icon(t, "icon.clear", "pv-card-clear"),
                icon(t, "icon.warning", "pv-card-warning"));
        } else {
            button.append(
                icon(t, "icon.star", "pv-card-star-glyph pv-card-star-outline"),
                solidIcon(t, "icon.starFilled", "pv-card-star-glyph pv-card-star-solid"));
        }
        button.addEventListener("pointerdown", (event) => event.stopPropagation());
        button.addEventListener("click", (event) => {
            event.stopPropagation();
            if (!currentCard) return;
            // RATING-04：冲突三角只打开来源浮窗，不写值，也不切换主图。
            if (index === 0 && hasRatingConflict(currentCard)) {
                ctx.ratingPopover?.open(button, { mode: "card", card: currentCard });
                return;
            }
            app.rate(currentCard.photo.id, index);
        });
        place(button, frames.stars[index]);
        surface.append(button);
        stars.push(button);
    }

    place(surface, frames.surface);
    place(well, frames.image);
    place(canvas, { x: 0, y: 0, w: frames.image.w, h: frames.image.h });
    place(cached, { x: 0, y: 0, w: frames.image.w, h: frames.image.h });
    place(badge, frames.shake);
    place(name, frames.name);
    place(second, frames.date);
    if (!similarity) place(root, frames.root);

    let currentCard = null;
    let lastRating = null;
    let lastConflict = null;
    let lastCached = null;
    let lastShake = null;
    let lastCurrent = null;
    let lastShowRating = null;
    let lastPhotoId = null;
    let lastPriority = null;
    let thumbToken = 0;
    let suppressClickUntil = 0;

    const longPress = clock && menu
        ? new LongPressRecognizer(clock, {
            delay: t("input.longPress.delay"),
            slop: t("input.longPress.slop"),
        })
        : null;

    function menuPoint(event) {
        const host = ctx.windowEl ?? document.documentElement;
        const rect = host.getBoundingClientRect();
        return { x: event.clientX - rect.left, y: event.clientY - rect.top };
    }

    function openCardMenu(point) {
        if (!currentCard || !menu) return;
        const photo = currentCard.photo;
        const taken = takenOf(photo);
        const items = [
            {
                // MENU-05：卡片菜单标题为完整文件名 + 完整拍摄时间，不可点击。
                type: "header",
                label: currentCard.displayName,
                detail: taken === null || taken === undefined ? "" : formatTakenFull(taken, photo.tzOffset),
            },
            { type: "separator" },
        ];
        const rating = ratingValue(photo.rating);
        for (let index = 0; index < 6; index += 1) {
            items.push({
                id: `rate-${index}`,
                type: "radio",
                label: RATING_LABELS[index],
                checked: rating === index,
            });
        }
        menu.open(items, point, {
            label: "照片操作",
            returnFocus: root,
            onSelect: (id) => {
                const value = Number(String(id).replace("rate-", ""));
                if (Number.isFinite(value)) app.rate(photo.id, value);
            },
        });
    }

    function ensureCanvas() {
        const dpr = Math.max(1, globalThis.devicePixelRatio || 1);
        const width = Math.max(1, Math.round(frames.image.w * dpr));
        const height = Math.max(1, Math.round(frames.image.h * dpr));
        if (canvas.width !== width) canvas.width = width;
        if (canvas.height !== height) canvas.height = height;
        return { width, height };
    }

    function clearCanvas() {
        const { width, height } = ensureCanvas();
        paint.clearRect(0, 0, width, height);
        placeholder.hidden = false;
    }

    function drawSurface(source) {
        const { width, height } = ensureCanvas();
        paint.clearRect(0, 0, width, height);
        const sourceWidth = source.width || source.naturalWidth || 0;
        const sourceHeight = source.height || source.naturalHeight || 0;
        if (!sourceWidth || !sourceHeight) return;
        // FILM-05：按原比例适应留边，不拉伸
        const scale = Math.min(width / sourceWidth, height / sourceHeight);
        const drawWidth = sourceWidth * scale;
        const drawHeight = sourceHeight * scale;
        paint.imageSmoothingQuality = "high";
        paint.drawImage(source, (width - drawWidth) / 2, (height - drawHeight) / 2, drawWidth, drawHeight);
        placeholder.hidden = true;
    }

    function drawFailure() {
        // FILM-05：解码失败显示占位字形，不伪造图像
        clearCanvas();
    }

    function loadThumbnail(photo, priority) {
        if (typeof thumbs?.request !== "function") return;
        const token = (thumbToken += 1);
        lastPriority = priorityRank(priority);
        thumbs.request(photo, priority).then(
            (source) => {
                if (token !== thumbToken) return;
                drawSurface(source);
            },
            () => {
                if (token !== thumbToken) return;
                drawFailure();
            },
        );
    }

    function select() {
        if (!currentCard) return;
        if (similarity) app.selectSimilar(currentCard.photo.id);
        else app.selectCard(currentCard.photo.id);
    }

    root.addEventListener("click", (event) => {
        // MENU-05：长按抬起不触发点选（只吞掉紧随其后的视点点击）。
        if (suppressClickUntil > 0 && performance.now() < suppressClickUntil) {
            suppressClickUntil = 0;
            event.stopPropagation();
            event.preventDefault();
            return;
        }
        select();
    });
    root.addEventListener("keydown", (event) => {
        if (event.target !== root) return;
        if (event.key !== "Enter" && event.key !== " " && event.key !== "Spacebar") return;
        event.preventDefault();
        select();
    });

    // INPUT-07 / MENU-05：触屏长按弹卡片菜单；桌面右键仍走主图菜单，不建卡片菜单。
    root.addEventListener("pointerdown", (event) => {
        if (!longPress || event.pointerType !== "touch" || event.button !== 0) return;
        suppressClickUntil = 0;
        const point = menuPoint(event);
        longPress.start(event.clientX, event.clientY, () => {
            suppressClickUntil = performance.now() + 700;
            openCardMenu(point);
        });
    });
    root.addEventListener("pointermove", (event) => {
        if (!longPress || event.pointerType !== "touch") return;
        longPress.move(event.clientX, event.clientY);
    });
    root.addEventListener("pointerup", (event) => {
        if (!longPress || event.pointerType !== "touch") return;
        longPress.end();
    });
    root.addEventListener("pointercancel", () => {
        longPress?.cancel();
    });

    function bind(card, state, info = {}) {
        if (!card) return;
        currentCard = card;
        const photo = card.photo;
        const rating = displayStars(photo);
        const conflict = hasRatingConflict(card);
        const identity = similarity ? state.displayedId : state.anchorId;
        const isCurrent = Boolean(identity)
            && (identity === photo.id || card.companions.some((companion) => companion.id === identity));
        const showRating = state.toolbar?.rating?.visible !== false;
        const cachedOn = Boolean(state.cached?.has(photo.id));
        const shakeOn = photo.shake === true;
        const taken = takenOf(photo);

        setText(name, card.displayName);
        root.dataset.key = card.key;
        // FILM-05：完整文件名与时间走自绘悬停提示（data-tooltip），不再用浏览器原生 title。
        root.dataset.tooltip = taken === undefined || taken === null
            ? card.displayName
            : `${card.displayName}\n${formatTakenFull(taken, photo.tzOffset)}`;
        const ratingLabel = conflict ? "星级冲突" : RATING_LABELS[rating];
        const label = `${card.displayName}，${ratingLabel}`;
        if (root.getAttribute("aria-label") !== label) root.setAttribute("aria-label", label);

        if (similarity) {
            const percent = Math.round(Number(info.percent) || 0);
            setText(barLabel, `${percent}%`);
            barFill.style.width = `${frames.date.w * clampRatio(info.barRatio)}px`;
        } else {
            setText(dateText, formatTakenShort(taken, photo.tzOffset));
        }

        if (isCurrent !== lastCurrent) {
            surface.classList.toggle("is-current", isCurrent);
            if (isCurrent) root.setAttribute("aria-current", "true");
            else root.removeAttribute("aria-current");
            lastCurrent = isCurrent;
        }
        const tabIndex = isCurrent ? 0 : -1;
        if (root.tabIndex !== tabIndex) root.tabIndex = tabIndex;

        if (showRating !== lastShowRating) {
            surface.classList.toggle("is-rating-hidden", !showRating);
            lastShowRating = showRating;
        }

        if (conflict !== lastConflict) {
            // RATING-04：冲突时 0 号槽位换成红色警示三角，几何与命中区不变。
            const zero = stars[0];
            zero.classList.toggle("is-conflict", conflict);
            zero.classList.toggle("is-clear", !conflict);
            const zeroLabel = conflict ? "星级冲突" : "无星级";
            zero.setAttribute("aria-label", zeroLabel);
            zero.dataset.tooltip = zeroLabel;
            // 冲突解除后星级可能仍是 0：此时星级分支不会重跑，0 号需在这里恢复按下态。
            zero.setAttribute("aria-pressed", rating === 0 && !conflict ? "true" : "false");
            lastConflict = conflict;
        }

        if (rating !== lastRating) {
            for (let index = 0; index < stars.length; index += 1) {
                const button = stars[index];
                const active = index === 0 ? rating === 0 && !conflict : rating >= index;
                button.classList.toggle("is-on", index > 0 && active);
                button.setAttribute("aria-pressed", active ? "true" : "false");
            }
            lastRating = rating;
        }
        for (const button of stars) {
            if (button.tabIndex !== tabIndex) button.tabIndex = tabIndex;
        }

        if (cachedOn !== lastCached) {
            cached.hidden = !cachedOn;
            lastCached = cachedOn;
        }
        if (shakeOn !== lastShake) {
            badge.hidden = !shakeOn;
            lastShake = shakeOn;
        }

        const priority = info.priority ?? "visible";
        if (photo.id !== lastPhotoId) {
            lastPhotoId = photo.id;
            clearCanvas();
            loadThumbnail(photo, priority);
        } else if (lastPriority !== null && priorityRank(priority) < lastPriority) {
            loadThumbnail(photo, priority);
        }
    }

    function destroy() {
        thumbToken += 1;
        currentCard = null;
        root.remove();
    }

    return { el: root, bind, destroy };
}
