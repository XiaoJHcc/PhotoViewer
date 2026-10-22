import { el, place, touches } from "./dom.js";
import { visibleRange } from "../core/filmscroll.js";
import { createCard } from "./card.js";

const OVERSCAN = 3;
const SCROLLBAR_THICKNESS = 6;
const SCROLLBAR_INSET = 3;
const SCROLLBAR_MARGIN = 2;
const THUMB_MIN = 24;
const ACTIVE_MS = 900;
const POOL_LIMIT = 32;

export function mountFilmstrip(host, ctx) {
    const { app, t, clock } = ctx;

    const scroller = el("div", {
        class: "pv-film-scroll is-x",
        role: "list",
        "aria-label": "胶片栏",
        tabindex: "-1",
    });
    const documentLayer = el("div", { class: "pv-film-document" });
    const track = el("div", { class: "pv-film-scrollbar is-x", "aria-hidden": "true" });
    const thumb = el("div", { class: "pv-film-scrollbar-thumb" });
    track.append(thumb);
    scroller.append(documentLayer);
    host.append(scroller, track);

    const rendered = new Map();
    const pool = [];
    let state = null;
    let film = null;
    let axis = "x";
    let viewport = 0;
    let documentLength = 0;
    let trackRect = null;
    let thumbLength = THUMB_MIN;
    let expected = null;
    let expectedTimer = null;
    let activeTimer = null;
    let dragging = false;

    function liveFilm() {
        return app.state?.layout?.film ?? null;
    }

    function liveAxis() {
        const current = liveFilm();
        if (!current) return axis;
        return current.axis === "col" ? "y" : "x";
    }

    function liveMaxOffset() {
        const current = liveFilm();
        if (!current) return Math.max(0, documentLength - viewport);
        const length = current.axis === "col" ? current.document.h : current.document.w;
        const view = current.axis === "col" ? current.scroll.h : current.scroll.w;
        return Math.max(0, length - view);
    }

    function offset() {
        return liveAxis() === "x" ? scroller.scrollLeft : scroller.scrollTop;
    }

    function noteProgrammaticOffset() {
        if (expectedTimer !== null) clock.clearTimeout(expectedTimer);
        expectedTimer = clock.setTimeout(() => {
            expectedTimer = null;
            expected = null;
        }, 200);
    }

    function setOffset(value) {
        // 浏览器会把偏移夹到真实可滚范围；不在这里二次夹取，避免布局切换瞬间读到过期范围
        const numeric = Number(value);
        const next = Number.isFinite(numeric) ? numeric : 0;
        const target = liveAxis();
        expected = next;
        noteProgrammaticOffset();
        if (target === "x") scroller.scrollLeft = next;
        else scroller.scrollTop = next;
    }

    function trackRectFor(scroll) {
        if (axis === "x") {
            return {
                x: scroll.x + SCROLLBAR_INSET,
                y: scroll.y + scroll.h - SCROLLBAR_THICKNESS - SCROLLBAR_MARGIN,
                w: Math.max(0, scroll.w - SCROLLBAR_INSET * 2),
                h: SCROLLBAR_THICKNESS,
            };
        }
        return {
            x: scroll.x + scroll.w - SCROLLBAR_THICKNESS - SCROLLBAR_MARGIN,
            y: scroll.y + SCROLLBAR_INSET,
            w: SCROLLBAR_THICKNESS,
            h: Math.max(0, scroll.h - SCROLLBAR_INSET * 2),
        };
    }

    function updateScrollbar() {
        if (!trackRect || viewport <= 0 || documentLength <= 0) {
            track.hidden = true;
            return;
        }
        const max = Math.max(0, documentLength - viewport);
        if (max <= 0.5) {
            track.hidden = true;
            return;
        }
        track.hidden = false;
        const length = axis === "x" ? trackRect.w : trackRect.h;
        const ratio = Math.min(1, viewport / documentLength);
        thumbLength = Math.max(THUMB_MIN, Math.round(length * ratio));
        const travel = Math.max(1, length - thumbLength);
        const position = Math.round((Math.min(max, offset()) / max) * travel);
        if (axis === "x") {
            thumb.style.width = `${thumbLength}px`;
            thumb.style.height = "";
            thumb.style.transform = `translateX(${position}px)`;
        } else {
            thumb.style.height = `${thumbLength}px`;
            thumb.style.width = "";
            thumb.style.transform = `translateY(${position}px)`;
        }
    }

    function markActive() {
        track.classList.add("is-active");
        if (activeTimer !== null) clock.clearTimeout(activeTimer);
        activeTimer = clock.setTimeout(() => {
            activeTimer = null;
            if (!dragging) track.classList.remove("is-active");
        }, ACTIVE_MS);
    }

    function recycle(entry) {
        entry.el.remove();
        pool.push(entry.card);
        while (pool.length > POOL_LIMIT) pool.shift().destroy();
    }

    function clearRendered() {
        for (const entry of rendered.values()) recycle(entry);
        rendered.clear();
    }

    function pooledCard() {
        return pool.pop() ?? createCard(ctx, { mode: "film" });
    }

    function syncRange() {
        if (!film || !state) return;
        if (state.count === 0) {
            clearRendered();
            return;
        }
        const [first, last] = visibleRange(offset(), viewport, state.count, axis, t);
        const low = Math.max(0, first - OVERSCAN);
        const high = Math.min(state.count - 1, last + OVERSCAN);
        const wanted = new Map();
        for (let index = low; index <= high; index += 1) wanted.set(state.cards[index].key, index);
        for (const [key, entry] of rendered) {
            if (wanted.has(key)) continue;
            rendered.delete(key);
            recycle(entry);
        }
        for (const [key, index] of wanted) {
            let entry = rendered.get(key);
            if (!entry) {
                const card = pooledCard();
                entry = { card, el: card.el };
                rendered.set(key, entry);
                documentLayer.append(entry.el);
            }
            place(entry.el, film.items[index]);
            entry.card.bind(state.cards[index], state, {
                priority: index >= first && index <= last ? "visible" : "near",
            });
        }
    }

    function applyGeometry(nextState, nextFilm) {
        const nextAxis = nextFilm.axis === "col" ? "y" : "x";
        if (nextAxis !== axis) {
            // FILM-03：换向废弃旧偏移语义，定位由 app 随后的显式请求负责
            axis = nextAxis;
            expected = null;
            if (expectedTimer !== null) {
                clock.clearTimeout(expectedTimer);
                expectedTimer = null;
            }
            scroller.classList.toggle("is-x", axis === "x");
            scroller.classList.toggle("is-y", axis === "y");
            track.classList.toggle("is-x", axis === "x");
            track.classList.toggle("is-y", axis === "y");
        }
        viewport = axis === "x" ? nextFilm.scroll.w : nextFilm.scroll.h;
        documentLength = axis === "x" ? nextFilm.document.w : nextFilm.document.h;
        place(scroller, nextFilm.scroll);
        place(documentLayer, nextFilm.document);
        trackRect = trackRectFor(nextFilm.scroll);
        place(track, trackRect);
        updateScrollbar();
    }

    function onScroll() {
        // FILM-03：程序化 setOffset 引起的 scroll 事件不算用户接管；真实用户输入由 wheel/pointer/touch 先撤销动画
        const programmatic = expected !== null;
        expected = null;
        if (expectedTimer !== null) {
            clock.clearTimeout(expectedTimer);
            expectedTimer = null;
        }
        if (!programmatic) app.filmUserScrolled();
        markActive();
        updateScrollbar();
        syncRange();
    }

    scroller.addEventListener("scroll", onScroll, { passive: true });

    scroller.addEventListener("wheel", (event) => {
        // FILM-04：胶片栏吞掉滚轮，不把翻页交给主图
        event.stopPropagation();
        app.filmUserScrolled();
        markActive();
        if (axis === "y") return;
        if (!event.deltaY) return;
        if (Math.abs(event.deltaX) > Math.abs(event.deltaY)) return;
        event.preventDefault();
        const unit = event.deltaMode === 1 ? 16 : event.deltaMode === 2 ? viewport : 1;
        setOffset(offset() + event.deltaY * unit);
        updateScrollbar();
        syncRange();
    }, { passive: false });

    scroller.addEventListener("pointerdown", () => app.filmUserScrolled(), { passive: true });
    scroller.addEventListener("touchstart", () => app.filmUserScrolled(), { passive: true });

    thumb.addEventListener("pointerdown", (event) => {
        event.preventDefault();
        event.stopPropagation();
        app.filmUserScrolled();
        const max = liveMaxOffset();
        if (max <= 0 || !trackRect) return;
        const horizontal = liveAxis() === "x";
        const startPointer = horizontal ? event.clientX : event.clientY;
        const startOffset = offset();
        const length = horizontal ? trackRect.w : trackRect.h;
        const travel = Math.max(1, length - thumbLength);
        dragging = true;
        markActive();
        thumb.setPointerCapture(event.pointerId);
        const move = (moveEvent) => {
            const delta = (horizontal ? moveEvent.clientX : moveEvent.clientY) - startPointer;
            setOffset(startOffset + (delta / travel) * max);
            updateScrollbar();
            syncRange();
        };
        const end = (endEvent) => {
            dragging = false;
            if (thumb.hasPointerCapture?.(endEvent.pointerId)) thumb.releasePointerCapture(endEvent.pointerId);
            thumb.removeEventListener("pointermove", move);
            thumb.removeEventListener("pointerup", end);
            thumb.removeEventListener("pointercancel", end);
            markActive();
        };
        thumb.addEventListener("pointermove", move);
        thumb.addEventListener("pointerup", end);
        thumb.addEventListener("pointercancel", end);
    });

    function update(nextState, regions) {
        state = nextState;
        const nextFilm = nextState.layout?.film ?? null;
        const geometry = !regions || touches(regions, "layout", "panes", "window");

        if (!nextFilm) {
            film = null;
            if (geometry) {
                place(scroller, null);
                place(documentLayer, null);
                place(track, null);
                viewport = 0;
                documentLength = 0;
                trackRect = null;
                track.hidden = true;
            }
            clearRendered();
            return;
        }

        if (geometry || !film) {
            film = nextFilm;
            applyGeometry(nextState, nextFilm);
            app.filmResized();
            app.layoutReady();
        }

        if (geometry || touches(regions, "list", "current", "settings", "image")) syncRange();
        updateScrollbar();
    }

    function destroy() {
        if (activeTimer !== null) clock.clearTimeout(activeTimer);
        if (expectedTimer !== null) clock.clearTimeout(expectedTimer);
        activeTimer = null;
        expectedTimer = null;
        for (const entry of rendered.values()) entry.card.destroy();
        rendered.clear();
        for (const card of pool) card.destroy();
        pool.length = 0;
        scroller.remove();
        track.remove();
    }

    app.bindFilmScroller({
        getOffset: () => offset(),
        setOffset: (value) => setOffset(value),
        viewportLength: () => {
            const current = liveFilm();
            if (!current) return 0;
            return current.axis === "col" ? current.scroll.h : current.scroll.w;
        },
    });

    return { update, destroy };
}
