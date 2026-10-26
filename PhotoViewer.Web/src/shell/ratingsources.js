/**
 * 星级来源冲突浮窗（RATING-04）。
 * 卡片/相似卡片与控制栏评定组的 0 号槽位在组级冲突时打开本浮窗；浮窗只展示组内全部
 * 实际来源，点击任一条目的星或清零叉都走 app.rate 的整组写入，不单独写某一来源。
 *
 * 打开期间跟随 app 状态刷新条目；写入成功且冲突解除才关闭，失败保留并显示刷新后的来源。
 * Esc、外部点击、切图、入口隐藏/移除、布局重排、胶片栏/控制栏滚动、菜单或下拉打开时关闭。
 */
import { el, setText } from "./dom.js";
import { icon, solidIcon } from "./icons.js";
import { hasRatingConflict, ratingSources } from "../core/rating.js";
import {
    placeRatingSources,
    ratingSourcesMetrics,
    resolveRatingSourcesDirection,
} from "../core/layout/ratingpopover.js";

function badgeTextOf(source) {
    if (source.kind === "sidecar") return "XMP";
    const ext = String(source.photo?.ext ?? "").replace(/^\./, "").toUpperCase();
    return ext || source.type;
}

function setPressed(button, pressed) {
    button.setAttribute("aria-pressed", pressed ? "true" : "false");
}

export function mountRatingPopover(host, ctx) {
    const { app, t, measureText } = ctx;
    const metrics = ratingSourcesMetrics(t);
    let current = null;

    function ellipsize(text, maxWidth) {
        const fontSize = t("popup.ratingSources.nameFontSize");
        if (measureText(text, fontSize, 400) <= maxWidth) return text;
        // RATING-04：长名保留首尾、中间省略，避免丢失扩展名与关键区分部分。
        const candidate = (kept) => {
            const head = Math.ceil(kept / 2);
            const tail = kept - head;
            return `${text.slice(0, head)}…${text.slice(text.length - tail)}`;
        };
        let low = 0;
        let high = text.length - 1;
        while (low < high) {
            const middle = Math.ceil((low + high) / 2);
            if (measureText(candidate(middle), fontSize, 400) <= maxWidth) low = middle;
            else high = middle - 1;
        }
        return candidate(low);
    }

    function createStarButton(source, value) {
        const label = value === 0 ? "无星级" : `${value} 星`;
        const button = el("button", {
            type: "button",
            class: value === 0 ? "pv-rating-source-star is-clear" : "pv-rating-source-star",
            "aria-label": label,
        });
        button.dataset.rating = String(value);
        if (value === 0) {
            button.append(icon(t, "icon.clear", "pv-rating-source-clear"));
        } else {
            button.append(
                icon(t, "icon.star", "pv-rating-source-glyph pv-rating-source-outline"),
                solidIcon(t, "icon.starFilled", "pv-rating-source-glyph pv-rating-source-solid"),
            );
        }
        const rated = source.state === "rated";
        const on = value > 0 && rated && source.value >= value;
        if (value > 0) {
            button.classList.toggle("is-on", on);
            button.classList.toggle("is-off", !on);
        }
        setPressed(button, rated && source.value === value);
        button.addEventListener("click", (event) => {
            event.stopPropagation();
            choose(source.photo.id, value);
        });
        return button;
    }

    function createEntry(source) {
        const nameText = String(source.label ?? "");
        const badgeText = badgeTextOf(source);
        const statusText = String(source.stateLabel ?? "");
        const entry = el("div", { class: "pv-rating-source", role: "group" });
        const info = el("div", { class: "pv-rating-source-info" });
        const name = el("div", { class: "pv-rating-source-name" });
        const meta = el("div", { class: "pv-rating-source-meta" });
        const badge = el("span", { class: "pv-rating-source-badge" }, badgeText);
        const status = el("span", { class: "pv-rating-source-status" }, statusText);
        const stars = el("div", { class: "pv-rating-source-stars", role: "group" });

        // RATING-04：长名按实际字体测量决定双行，文件名截断但完整名保留在 aria-label。
        const metaWidth = measureText(badgeText, t("popup.ratingSources.metaFontSize"), 400)
            + t("popup.ratingSources.badgePaddingX") * 2
            + metrics.labelGap
            + measureText(statusText, t("popup.ratingSources.metaFontSize"), 400);
        const available = metrics.entryWidth - metrics.entryPadding * 2 - metrics.labelGap - metaWidth;
        const long = measureText(nameText, t("popup.ratingSources.nameFontSize"), 400) > available;
        name.setAttribute("aria-label", nameText);
        setText(name, long ? ellipsize(nameText, metrics.entryWidth - metrics.entryPadding * 2) : nameText);
        if (long) entry.classList.add("is-long");

        meta.append(badge, status);
        info.append(name, meta);
        for (let value = 0; value < metrics.starCount; value += 1) {
            stars.append(createStarButton(source, value));
        }
        entry.append(info, stars);
        entry.dataset.source = source.kind;
        return entry;
    }

    function rebuild(card) {
        const sources = ratingSources(card);
        const body = current.body;
        body.replaceChildren();
        sources.forEach((source, index) => {
            if (index > 0) body.append(el("div", { class: "pv-rating-source-sep", role: "separator" }));
            body.append(createEntry(source));
        });
        body.dataset.sources = String(sources.length);
    }

    function signatureOf(card) {
        return ratingSources(card)
            .map((source) => `${source.id}|${source.label}|${source.state}|${source.value}`)
            .join("||");
    }

    function refresh(card) {
        if (!current || !card) return;
        current.card = card;
        const signature = signatureOf(card);
        if (signature !== current.signature) {
            current.signature = signature;
            rebuild(card);
        }
    }

    function anchorBox(node) {
        const hostRect = host.getBoundingClientRect();
        const rect = node.getBoundingClientRect();
        return {
            x: rect.left - hostRect.left,
            y: rect.top - hostRect.top,
            w: rect.width,
            h: rect.height,
            hostWidth: hostRect.width,
            hostHeight: hostRect.height,
        };
    }

    function position() {
        if (!current) return;
        const anchor = anchorBox(current.anchor);
        const width = current.root.offsetWidth;
        const height = current.root.offsetHeight;
        const { direction, gap } = resolveRatingSourcesDirection({
            mode: current.mode,
            axis: current.axis,
            anchor,
            host: { width: anchor.hostWidth, height: anchor.hostHeight },
            height,
            t,
        });
        const placed = placeRatingSources({
            anchor,
            host: { width: anchor.hostWidth, height: anchor.hostHeight },
            width,
            height,
            direction,
            gap,
        });
        current.root.style.left = `${placed.x}px`;
        current.root.style.top = `${placed.y}px`;
        current.root.dataset.direction = direction;
    }

    function cardFor(state) {
        if (!current) return null;
        const byKey = current.key ? state.cards.find((entry) => entry.key === current.key) : null;
        if (byKey) return byKey;
        return state.cards.find((entry) => current.photoId
            && (entry.photo.id === current.photoId || entry.companions.some((photo) => photo.id === current.photoId)))
            ?? null;
    }

    async function choose(photoId, value) {
        if (!current) return;
        const ok = await app.rate(photoId, value);
        if (!current) return;
        const state = app.state;
        const card = cardFor(state);
        if (!card) {
            close();
            return;
        }
        refresh(card);
        position();
        // RATING-04：只有写入成功且刷新后冲突解除才关闭；失败或仍冲突时保留。
        if (ok && !hasRatingConflict(card)) close();
    }

    function isVisible(node) {
        if (!node || !node.isConnected) return false;
        if (node.closest("[hidden]")) return false;
        return node.getClientRects().length > 0;
    }

    function close() {
        if (!current) return;
        const { root, cleanup } = current;
        current = null;
        for (const dispose of cleanup) dispose();
        root.remove();
        host.classList.remove("is-rating-sources-open");
    }

    function show(anchor, options = {}) {
        if (!anchor) return false;
        const state = app.state;
        if (state.menuOpen || state.dropdownOpen) return false;
        const mode = options.mode === "control" ? "control" : "card";
        let card = options.card ?? null;
        if (!card) {
            card = state.cards.find((entry) => entry.photo.id === state.displayedId
                || entry.companions.some((photo) => photo.id === state.displayedId)) ?? null;
        }
        if (!card) return false;
        if (current) {
            if (current.anchor === anchor) {
                current.card = card;
                current.key = card.key ?? null;
                current.photoId = card.photo?.id ?? null;
                current.displayedId = state.displayedId;
                current.axis = options.axis === "vertical" ? "vertical" : "horizontal";
                current.anchorBox = anchorBox(anchor);
                refresh(card);
                position();
                return true;
            }
            close();
        }

        const root = el("div", {
            class: "pv-rating-sources",
            role: "group",
            "aria-label": "评分来源",
        });
        const body = el("div", { class: "pv-rating-sources-body" });
        root.append(body);
        host.append(root);

        current = {
            anchor,
            mode,
            axis: options.axis === "vertical" ? "vertical" : "horizontal",
            key: card.key ?? null,
            photoId: card.photo?.id ?? null,
            displayedId: state.displayedId,
            card,
            root,
            body,
            anchorBox: anchorBox(anchor),
            signature: "",
            cleanup: [],
        };
        refresh(card);
        position();
        host.classList.add("is-rating-sources-open");

        const onOutside = (event) => {
            if (!current) return;
            if (current.root.contains(event.target) || current.anchor.contains(event.target)) return;
            close();
        };
        const onKey = (event) => {
            if (event.key !== "Escape") return;
            event.preventDefault();
            event.stopImmediatePropagation();
            close();
        };
        const onScroll = () => {
            if (!current) return;
            const box = anchorBox(current.anchor);
            const moved = ["x", "y", "w", "h"]
                .some((key) => Math.abs(box[key] - current.anchorBox[key]) > 0.5);
            if (moved) close();
        };
        const onResize = () => close();

        document.addEventListener("pointerdown", onOutside, true);
        window.addEventListener("keydown", onKey, true);
        window.addEventListener("resize", onResize);
        current.cleanup.push(() => document.removeEventListener("pointerdown", onOutside, true));
        current.cleanup.push(() => window.removeEventListener("keydown", onKey, true));
        current.cleanup.push(() => window.removeEventListener("resize", onResize));
        for (const node of host.querySelectorAll(".pv-film-scroll, .pv-control-scroll, .pv-sim-list")) {
            node.addEventListener("scroll", onScroll, { passive: true });
            current.cleanup.push(() => node.removeEventListener("scroll", onScroll));
        }
        return true;
    }

    function update(state, regions) {
        if (!current) return;
        if (state.menuOpen || state.dropdownOpen) {
            close();
            return;
        }
        if (regions && (regions.has("window") || regions.has("panes"))) {
            close();
            return;
        }
        if (state.displayedId !== current.displayedId) {
            close();
            return;
        }
        if (state.toolbar?.rating?.visible === false) {
            close();
            return;
        }
        const card = cardFor(state);
        if (!card || !isVisible(current.anchor)) {
            close();
            return;
        }
        if (current.mode === "card") {
            const owner = current.anchor.closest?.(".pv-card");
            if (owner && owner.dataset.key !== undefined && owner.dataset.key !== card.key) {
                close();
                return;
            }
        }
        // RATING-04：锚点因重排或滚动移动时关闭；外部来源变化只刷新内容。
        const box = anchorBox(current.anchor);
        const moved = ["x", "y", "w", "h", "hostWidth", "hostHeight"]
            .some((key) => Math.abs(box[key] - current.anchorBox[key]) > 0.5);
        if (moved) {
            close();
            return;
        }
        if (!hasRatingConflict(card)) {
            close();
            return;
        }
        refresh(card);
        position();
    }

    return {
        open: show,
        close,
        update,
        destroy: close,
        get isOpen() {
            return current !== null;
        },
    };
}
