import { el, place, setText } from "./dom.js";
import { cardFrames } from "../core/layout/card.js";
import { formatTakenFull, formatTakenShort } from "../core/exifformat.js";

const RATING_LABELS = ["无星级", "1 星", "2 星", "3 星", "4 星", "5 星"];
const PRIORITY_RANK = { visible: 0, near: 1, far: 2 };
const FAILURE_STROKE = "rgba(255, 255, 255, 0.28)";
const SVG_NS = "http://www.w3.org/2000/svg";
const STAR_PATH = "M7 0.7 L8.47 4.98 L12.99 5.05 L9.38 7.77 L10.7 12.1 L7 9.5 L3.3 12.1 L4.62 7.77 L1.01 5.05 L5.53 4.98 Z";

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
    const { app, t, thumbs } = ctx;
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
    well.append(canvas, cached);
    surface.append(well, badge, name, second);
    root.append(surface);

    const stars = [];
    for (let index = 0; index < 6; index += 1) {
        const button = el("button", { type: "button", class: "pv-card-star", tabindex: "-1" });
        button.setAttribute("aria-label", index === 0 ? "无星级" : `${index} 星`);
        if (index === 0) {
            button.classList.add("is-clear");
            setText(button, "×");
        } else {
            const glyph = document.createElementNS(SVG_NS, "svg");
            glyph.setAttribute("viewBox", "0 0 14 14");
            glyph.setAttribute("class", "pv-card-star-glyph");
            glyph.setAttribute("aria-hidden", "true");
            const outline = document.createElementNS(SVG_NS, "path");
            outline.setAttribute("d", STAR_PATH);
            glyph.append(outline);
            button.append(glyph);
        }
        button.addEventListener("pointerdown", (event) => event.stopPropagation());
        button.addEventListener("click", (event) => {
            event.stopPropagation();
            if (currentCard) app.rate(currentCard.photo.id, index);
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
    let lastCached = null;
    let lastShake = null;
    let lastCurrent = null;
    let lastShowRating = null;
    let lastPhotoId = null;
    let lastPriority = null;
    let thumbToken = 0;

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
    }

    function drawFailure() {
        const { width, height } = ensureCanvas();
        paint.clearRect(0, 0, width, height);
        // FILM-05：解码失败只画中性细线叉，不伪造图像
        paint.strokeStyle = FAILURE_STROKE;
        paint.lineWidth = Math.max(1, width / frames.image.w);
        const insetX = width * 0.32;
        const insetY = height * 0.32;
        paint.beginPath();
        paint.moveTo(insetX, insetY);
        paint.lineTo(width - insetX, height - insetY);
        paint.moveTo(width - insetX, insetY);
        paint.lineTo(insetX, height - insetY);
        paint.stroke();
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

    root.addEventListener("click", select);
    root.addEventListener("keydown", (event) => {
        if (event.target !== root) return;
        if (event.key !== "Enter" && event.key !== " " && event.key !== "Spacebar") return;
        event.preventDefault();
        select();
    });

    function bind(card, state, info = {}) {
        if (!card) return;
        currentCard = card;
        const photo = card.photo;
        const rating = ratingValue(photo.rating);
        const identity = similarity ? state.displayedId : state.anchorId;
        const isCurrent = Boolean(identity)
            && (identity === photo.id || card.companions.some((companion) => companion.id === identity));
        const showRating = state.toolbar?.rating?.visible !== false;
        const cachedOn = Boolean(state.cached?.has(photo.id));
        const shakeOn = photo.shake === true;
        const taken = takenOf(photo);

        setText(name, card.displayName);
        root.title = taken === undefined || taken === null
            ? card.displayName
            : `${card.displayName}\n${formatTakenFull(taken, photo.tzOffset)}`;
        const label = `${card.displayName}，${RATING_LABELS[rating]}`;
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

        if (rating !== lastRating) {
            for (let index = 0; index < stars.length; index += 1) {
                const button = stars[index];
                const active = index === 0 ? rating === 0 : rating >= index;
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
