/**
 * 胶片栏几何与程序化定位（FILM-01–04）。
 *
 * 几何纯函数（axis 取 "x" 表示行布局横向列表，"y" 表示列布局纵向列表）：
 * - filmMetrics(t)：卡片宽高、步长与列表内边距
 * - contentLength(count, axis, t)：列表内容主轴长度
 * - itemStart(index, count, axis, viewportLength, t)：卡片起点
 * - visibleRange(offset, viewportLength, count, axis, t)：可见区间 [first, last]（含边界）
 * - centerOffset(index, count, axis, viewportLength, t)：FILM-02 定位目标偏移
 * - easeOutCubic(p)：三次缓出 1 − (1 − p)³
 *
 * 定位：
 * - FilmScroller：单轴定位动画器。request(targetOffset) 从当前显示位置起播，cancel() 停在当前帧，
 *   clamp(contentLength, viewportLength) 只把偏移约束到合法范围，requestCentered(geometry) 按 FILM-02 公式请求。
 * - FilmPositioner：把导航、点选、滚动、列表替换、换向、显隐、尺寸变化等入口集中映射为定位/取消/夹取，
 *   并在布局未就绪时排队到 layoutReady()。
 */

export function filmMetrics(t) {
    const cardWidth = t("film.card.width");
    const cardHeight = t("film.card.height");
    const spacing = t("film.card.spacing");
    return {
        cardWidth,
        cardHeight,
        stepX: cardWidth + spacing,
        stepY: cardHeight + spacing,
        padLeft: t("film.list.padLeft"),
        padTop: t("film.list.padTop"),
        padRight: t("film.list.padRight"),
        padBottom: t("film.list.padBottom"),
    };
}

function axisMetrics(axis, t) {
    const m = filmMetrics(t);
    if (axis === "x") {
        return { card: m.cardWidth, step: m.stepX, lead: m.padLeft, trail: m.padRight };
    }
    if (axis === "y") {
        return { card: m.cardHeight, step: m.stepY, lead: m.padTop, trail: m.padBottom };
    }
    throw new Error(`未知胶片栏方向：${axis}`);
}

export function contentLength(count, axis, t) {
    const a = axisMetrics(axis, t);
    return a.lead + Math.max(0, count) * a.step + a.trail;
}

export function itemStart(index, count, axis, viewportLength, t) {
    const a = axisMetrics(axis, t);
    // FILM-02：横向列表短于视口时整段居中，padLeft 是首项起点的下限；纵向列表仍从顶边起排
    const slack = axis === "x" ? Math.max(0, viewportLength - contentLength(count, axis, t)) : 0;
    return a.lead + slack / 2 + index * a.step;
}

export function visibleRange(offset, viewportLength, count, axis, t) {
    if (count <= 0) return [0, -1];
    const a = axisMetrics(axis, t);
    const lead = itemStart(0, count, axis, viewportLength, t);
    const first = Math.max(0, Math.floor((offset - lead) / a.step));
    const last = Math.min(count - 1, Math.ceil((offset + viewportLength - lead) / a.step) - 1);
    return [first, last];
}

export function centerOffset(index, count, axis, viewportLength, t) {
    const a = axisMetrics(axis, t);
    const target = itemStart(index, count, axis, viewportLength, t) + (a.card - viewportLength) / 2;
    const max = Math.max(0, contentLength(count, axis, t) - viewportLength);
    return Math.min(max, Math.max(0, target));
}

export function easeOutCubic(p) {
    const x = Math.min(1, Math.max(0, p));
    return 1 - (1 - x) ** 3;
}

export class FilmScroller {
    constructor({ t, clock, getOffset, setOffset, reducedMotion }) {
        this.t = t;
        this.clock = clock;
        this.getOffset = getOffset;
        this.setOffset = setOffset;
        this.reducedMotion = reducedMotion;
        this._frame = null;
        this._bounds = null;
    }

    get isAnimating() {
        return this._frame !== null;
    }

    requestCentered({ index, count, axis, viewportLength }) {
        this.request(centerOffset(index, count, axis, viewportLength, this.t));
    }

    request(targetOffset) {
        const target = Number(targetOffset);
        if (!Number.isFinite(target)) throw new TypeError(`定位目标不是有效数值：${targetOffset}`);
        this.cancel();
        this._bounds = null;
        const current = this.getOffset();
        if (
            this.reducedMotion?.()
            || Math.abs(target - current) < this.t("motion.film.scroll.negligibleThreshold")
        ) {
            // FILM-03：减少动态效果时直接到终点；FILM-02：位移不足 1 点不播放
            this.setOffset(target);
            return;
        }
        // FILM-02：新请求从当前显示位置起播，不回跳旧起点
        const startTime = this.clock.now();
        const duration = this.t("motion.film.scroll.duration");
        const tick = () => {
            this._frame = null;
            const progress = duration > 0 ? Math.min(1, (this.clock.now() - startTime) / duration) : 1;
            const value = current + (target - current) * easeOutCubic(progress);
            this.setOffset(this._applyBounds(value));
            if (progress < 1) this._frame = this.clock.requestFrame(tick);
        };
        this._frame = this.clock.requestFrame(tick);
    }

    cancel() {
        if (this._frame === null) return;
        this.clock.cancelFrame(this._frame);
        this._frame = null;
    }

    clamp(contentLength, viewportLength) {
        const max = Math.max(0, contentLength - viewportLength);
        this._bounds = { min: 0, max };
        const current = this.getOffset();
        const clamped = this._applyBounds(current);
        if (clamped !== current) this.setOffset(clamped);
    }

    _applyBounds(value) {
        if (!this._bounds) return value;
        return Math.min(this._bounds.max, Math.max(this._bounds.min, value));
    }
}

export class FilmPositioner {
    constructor({ scroller, geometry }) {
        this.scroller = scroller;
        this.geometry = geometry;
        this._pending = false;
    }

    get isPending() {
        return this._pending;
    }

    onNavigate() {
        this._request();
    }

    onReshow() {
        this._request();
    }

    onSelectByClick() {
        this._cancel();
    }

    onExchange() {
        // FILM-01：切换上一张只恢复照片，不产生定位请求
    }

    onSortOrFilter() {
        // FILM-01：排序与筛选只更新列表，不抢用户位置
    }

    onRatingRefresh() {
        // FILM-01：评分引起的列表刷新不定位
    }

    onThumbnailReady() {
        // FILM-01：缩略图就绪只刷新卡片内容
    }

    onResize(contentLength, viewportLength) {
        // FILM-01：普通尺寸变化只把偏移约束到合法范围
        this.scroller.clamp(contentLength, viewportLength);
    }

    onUserScroll() {
        // FILM-03：用户滚动接管，撤销在途动画与排队请求
        this._cancel();
    }

    onListReplaced() {
        this._cancel();
    }

    onAxisChanged() {
        this._cancel();
    }

    onHidden() {
        this._cancel();
    }

    onShown() {
        // FILM-04：重新显示由集成方随后调用 onReshow() 显式定位
    }

    layoutReady() {
        if (!this._pending) return;
        this._pending = false;
        this._request();
    }

    _cancel() {
        this._pending = false;
        this.scroller.cancel();
    }

    _request() {
        let geometry;
        try {
            geometry = this.geometry();
        } catch {
            this._pending = true;
            return;
        }
        if (!geometry) {
            this._pending = false;
            return;
        }
        if (!(geometry.viewportLength > 0)) {
            this._pending = true;
            return;
        }
        if (!(geometry.count > 0) || geometry.index < 0 || geometry.index >= geometry.count) {
            this._pending = false;
            return;
        }
        this._pending = false;
        this.scroller.requestCentered(geometry);
    }
}
