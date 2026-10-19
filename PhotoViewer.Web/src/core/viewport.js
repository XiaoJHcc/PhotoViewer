/**
 * 主图视口数学（IMAGE-01/02/04/05/06/08/09、INPUT-03/09、OPEN-05、SHELL-05、SETTINGS-07）。
 *
 * 单位：视口与偏移为逻辑点（CSS px）；scale 为每源像素的逻辑点数，physicalScale = scale × dpr，
 * 物理 100% 即 scale = 1 / dpr；width/height 一律为显示朝向的全分辨率源尺寸（IMAGE-08）。
 * 平移状态用“图像中心相对视口中心的偏移”表达。
 *
 * 导出：
 *   new Viewport(t, { dpr, allowBelowFit })          t = createTokens() 返回的令牌函数
 *   vp.setViewSize(width, height) / vp.setDpr(dpr)
 *   vp.setImage({ width, height, focus }, { mode })  mode："batch"（切图，默认）| "reset"（显式打开）
 *   vp.setFocus({ x, y } | null)
 *   vp.scale / vp.physicalScale / vp.fitScale / vp.isFit / vp.followsFocus / vp.hasImage
 *   vp.toggleFit(anchor?) → boolean                anchor 为视口坐标 { x, y }
 *   vp.zoomPreset(direction, presetsPercent) → boolean
 *   vp.zoomBy(factor, anchor?) → boolean
 *   vp.panBy(dx, dy) → boolean
 *   vp.centerOnPoint(u, v) → boolean
 *   vp.imageRect() → { x, y, w, h }
 *   vp.viewToImage(x, y) → { u, v }；vp.imageToView(u, v) → { x, y }
 *   vp.percentText() → string
 *   vp.navigator() → { visible, thumbWidth, thumbHeight, frame: { x, y, w, h } }
 *   vp.needsFull(proxyLongEdgePx) → boolean
 *   vp.proxyTargetLongEdge() → number             长边物理像素 × 代理 overscan，四舍五入
 *   vp.snapshot() → 可序列化状态
 *   displaySize(width, height, orientation) → { width, height }   方向码 ≥5 宽高换轴
 */

const EPS = 1e-9;
const PRESET_EPS = 1e-7;

function finite(value) {
    return typeof value === "number" && Number.isFinite(value);
}

function clamp(value, min, max) {
    return value < min ? min : value > max ? max : value;
}

function same(a, b) {
    return Math.abs(a - b) <= EPS * Math.max(1, Math.abs(a), Math.abs(b));
}

export function displaySize(width, height, orientation) {
    // IMAGE-08：方向码 5–8 只换显示轴，不改源像素。
    if (orientation != null && orientation >= 5) return { width: height, height: width };
    return { width, height };
}

export class Viewport {
    constructor(t, { dpr = 1, allowBelowFit = true } = {}) {
        if (typeof t !== "function") throw new TypeError("Viewport 需要令牌函数 t");
        this._t = t;
        this._dpr = finite(dpr) && dpr > 0 ? dpr : 1;
        this._allowBelowFit = Boolean(allowBelowFit);
        this._viewW = 0;
        this._viewH = 0;
        this._srcW = 0;
        this._srcH = 0;
        this._focus = null;
        this._scale = 1;
        this._offX = 0;
        this._offY = 0;
        this._fit = true;
        this._follow = false;
    }

    get hasImage() {
        return this._srcW > 0 && this._srcH > 0;
    }

    get scale() {
        return this._scale;
    }

    get physicalScale() {
        return this._scale * this._dpr;
    }

    get fitScale() {
        return this.hasImage ? this._fitScale() : 0;
    }

    get isFit() {
        return this._fit && this.hasImage;
    }

    get followsFocus() {
        return this._follow;
    }

    setViewSize(width, height) {
        const w = finite(width) ? Math.max(0, width) : 0;
        const h = finite(height) ? Math.max(0, height) : 0;
        const changed = w !== this._viewW || h !== this._viewH;
        this._viewW = w;
        this._viewH = h;
        if (!this.hasImage) return changed;
        if (this._fit) {
            this._scale = this._fitScale();
            this._offX = 0;
            this._offY = 0;
        } else {
            // IMAGE-05：尺寸变化只约束偏移，倍率与居中图像点不变。
            this._applyOffsets();
        }
        return changed;
    }

    setDpr(dpr) {
        if (!finite(dpr) || dpr <= 0) throw new RangeError("dpr 必须为正数");
        if (dpr === this._dpr) return false;
        const ratio = this._dpr / dpr;
        this._dpr = dpr;
        if (!this.hasImage || this._fit) return true;
        // SHELL-05：物理倍率不变，scale 按 dpr 反比换算，中心图像点不动。
        this._scale *= ratio;
        this._offX *= ratio;
        this._offY *= ratio;
        this._applyOffsets();
        return true;
    }

    setImage(image, options = {}) {
        const mode = options.mode ?? "batch";
        if (mode !== "batch" && mode !== "reset") throw new Error(`未知的切图模式：${mode}`);
        if (!image || !(image.width > 0) || !(image.height > 0)) {
            this._srcW = 0;
            this._srcH = 0;
            this._focus = null;
            this._scale = 1;
            this._offX = 0;
            this._offY = 0;
            this._fit = true;
            this._follow = false;
            return false;
        }
        const wasFollowing = this._follow;
        const center = this.hasImage ? this.viewToImage(this._viewW / 2, this._viewH / 2) : null;
        this._srcW = image.width;
        this._srcH = image.height;
        this._focus = image.focus && finite(image.focus.x) && finite(image.focus.y)
            ? { x: image.focus.x, y: image.focus.y }
            : null;
        if (mode === "reset" || this._fit) {
            // IMAGE-02 / OPEN-05：适应态切图保持适应，显式打开总是回到适应。
            this._fit = true;
            this._scale = this._fitScale();
            this._offX = 0;
            this._offY = 0;
            this._follow = false;
            return true;
        }
        this._fit = false;
        if (wasFollowing && this._focus) {
            // IMAGE-02：跟随对焦时切图居中到新照片对焦点。
            this._offX = (0.5 - this._focus.x) * this._displayWidth();
            this._offY = (0.5 - this._focus.y) * this._displayHeight();
        } else if (center) {
            this._offX = (0.5 - center.u) * this._displayWidth();
            this._offY = (0.5 - center.v) * this._displayHeight();
        }
        this._applyOffsets();
        this._follow = wasFollowing && this._focus != null;
        return true;
    }

    setFocus(focus) {
        // IMAGE-02：EXIF 异步到达只换值，下一次命令才使用。
        this._focus = focus && finite(focus.x) && finite(focus.y)
            ? { x: focus.x, y: focus.y }
            : null;
    }

    toggleFit(anchor = null) {
        if (!this.hasImage) return false;
        const fit = this._fitScale();
        if (!this._fit) {
            this._fit = true;
            this._scale = fit;
            this._offX = 0;
            this._offY = 0;
            this._follow = false;
            return true;
        }
        const target = 1 / this._dpr;
        // IMAGE-02：物理 100% 不高于适应倍率时保持适应，不把照片缩到比适应更小。
        if (target <= fit + EPS) return false;
        const ax = anchor && finite(anchor.x) ? anchor.x : null;
        const ay = anchor && finite(anchor.y) ? anchor.y : null;
        const point = ax !== null ? this.viewToImage(ax, ay) : null;
        this._scale = target;
        this._fit = false;
        if (point) {
            // 双击：以触点为锚点，触点到 100%。
            this._offX = (0.5 - point.u) * this._displayWidth() + (ax - this._viewW / 2);
            this._offY = (0.5 - point.v) * this._displayHeight() + (ay - this._viewH / 2);
            this._follow = this._geometricFollow();
        } else if (this._focus) {
            // Fit 命令：以对焦点为中心并进入跟随。
            this._offX = (0.5 - this._focus.x) * this._displayWidth();
            this._offY = (0.5 - this._focus.y) * this._displayHeight();
            this._follow = true;
        } else {
            this._offX = 0;
            this._offY = 0;
            this._follow = false;
        }
        this._applyOffsets();
        return true;
    }

    zoomPreset(direction, presetsPercent) {
        if (!this.hasImage || !Array.isArray(presetsPercent)) return false;
        const dir = Math.sign(direction);
        if (dir === 0) return false;
        const hardMin = this._t("image.zoom.minScale");
        const hardMax = this._t("image.zoom.maxScale");
        const list = presetsPercent
            .filter((percent) => finite(percent) && percent > 0)
            .map((percent) => percent / 100)
            .filter((scale) => scale >= hardMin - PRESET_EPS && scale <= hardMax + PRESET_EPS)
            .sort((a, b) => a - b);
        const current = this.physicalScale;
        let pick = null;
        for (const scale of list) {
            if (dir > 0 && scale > current + PRESET_EPS) {
                pick = scale;
                break;
            }
            if (dir < 0 && scale < current - PRESET_EPS) pick = scale;
        }
        // INPUT-09 / SETTINGS-07：没有更远档就不动作。
        if (pick === null) return false;
        return this._zoomTo(pick / this._dpr, this._viewW / 2, this._viewH / 2);
    }

    zoomBy(factor, anchor = null) {
        if (!this.hasImage || !finite(factor) || factor <= 0) return false;
        const ax = anchor && finite(anchor.x) ? anchor.x : this._viewW / 2;
        const ay = anchor && finite(anchor.y) ? anchor.y : this._viewH / 2;
        return this._zoomTo(this._scale * factor, ax, ay);
    }

    panBy(dx, dy) {
        // IMAGE-05：只有放大态可平移，适应态拖动不移动照片。
        if (!this.hasImage || this._fit || !finite(dx) || !finite(dy)) return false;
        const [ox, oy] = this._clampOffsets(this._offX + dx, this._offY + dy, this._scale);
        const changed = ox !== this._offX || oy !== this._offY;
        this._offX = ox;
        this._offY = oy;
        this._recomputeFollow();
        return changed;
    }

    centerOnPoint(u, v) {
        if (!this.hasImage || !finite(u) || !finite(v)) return false;
        const fit = this._fitScale();
        const target = 1 / this._dpr;
        const before = this._state();
        this._follow = false;
        if (target <= fit + EPS) {
            // IMAGE-09：物理 100% 不高于适应倍率时保持适应。
            this._fit = true;
            this._scale = fit;
            this._offX = 0;
            this._offY = 0;
        } else {
            this._fit = false;
            this._scale = target;
            this._offX = (0.5 - u) * this._displayWidth();
            this._offY = (0.5 - v) * this._displayHeight();
            this._applyOffsets();
        }
        return !this._sameState(before);
    }

    imageRect() {
        const w = this._srcW * this._scale;
        const h = this._srcH * this._scale;
        return {
            x: this._viewW / 2 - w / 2 + this._offX,
            y: this._viewH / 2 - h / 2 + this._offY,
            w,
            h,
        };
    }

    viewToImage(x, y) {
        const rect = this.imageRect();
        if (rect.w <= 0 || rect.h <= 0) return { u: 0, v: 0 };
        return { u: (x - rect.x) / rect.w, v: (y - rect.y) / rect.h };
    }

    imageToView(u, v) {
        const rect = this.imageRect();
        return { x: rect.x + u * rect.w, y: rect.y + v * rect.h };
    }

    percentText() {
        if (!this.hasImage) return "";
        // IMAGE-04：物理倍率百分比，最多两位小数并去掉尾零。
        const percent = Math.round(this.physicalScale * 10000) / 100;
        return `${percent}%`;
    }

    navigator() {
        if (!this.hasImage) {
            return { visible: false, thumbWidth: 0, thumbHeight: 0, frame: { x: 0, y: 0, w: 0, h: 0 } };
        }
        const longEdge = this._t("nav.longEdge");
        const portrait = this._srcH > this._srcW;
        const thumbWidth = portrait ? longEdge * (this._srcW / this._srcH) : longEdge;
        const thumbHeight = portrait ? longEdge : longEdge * (this._srcH / this._srcW);
        const rect = this.imageRect();
        if (!(rect.w > 0) || !(rect.h > 0)) {
            return { visible: false, thumbWidth, thumbHeight, frame: { x: 0, y: 0, w: 0, h: 0 } };
        }
        const u0 = clamp(-rect.x / rect.w, 0, 1);
        const u1 = clamp((this._viewW - rect.x) / rect.w, 0, 1);
        const v0 = clamp(-rect.y / rect.h, 0, 1);
        const v1 = clamp((this._viewH - rect.y) / rect.h, 0, 1);
        return {
            // IMAGE-06：可见框只表达非适应态，设置开关由集成方叠加。
            visible: !this._fit,
            thumbWidth,
            thumbHeight,
            frame: {
                x: u0 * thumbWidth,
                y: v0 * thumbHeight,
                w: (u1 - u0) * thumbWidth,
                h: (v1 - v0) * thumbHeight,
            },
        };
    }

    needsFull(proxyLongEdgePx) {
        if (!this.hasImage) return false;
        // IMAGE-03：物理倍率到 100% 一律需要全分辨率。
        if (this.physicalScale >= 1 - 1e-6) return true;
        if (!finite(proxyLongEdgePx) || proxyLongEdgePx <= 0) return true;
        const needed = Math.max(this._srcW, this._srcH) * this.physicalScale;
        // IMAGE-03：当前倍率超出代理可支撑的放大。
        return needed > proxyLongEdgePx + 1e-6;
    }

    proxyTargetLongEdge() {
        const viewLongEdgePx = Math.max(this._viewW, this._viewH) * this._dpr;
        return Math.round(viewLongEdgePx * this._t("image.proxy.overscan"));
    }

    snapshot() {
        return {
            hasImage: this.hasImage,
            dpr: this._dpr,
            view: { width: this._viewW, height: this._viewH },
            image: { width: this._srcW, height: this._srcH },
            focus: this._focus ? { ...this._focus } : null,
            scale: this._scale,
            physicalScale: this.physicalScale,
            fitScale: this.fitScale,
            isFit: this.isFit,
            followsFocus: this._follow,
            offset: { x: this._offX, y: this._offY },
            rect: this.imageRect(),
            percent: this.percentText(),
        };
    }

    _fitScale() {
        if (!this.hasImage || this._viewW <= 0 || this._viewH <= 0) return 0;
        return Math.min(this._viewW / this._srcW, this._viewH / this._srcH);
    }

    _displayWidth(scale = this._scale) {
        return this._srcW * scale;
    }

    _displayHeight(scale = this._scale) {
        return this._srcH * scale;
    }

    _clampOffsets(x, y, scale) {
        // IMAGE-05：钳制为“显示尺寸 − 视口尺寸”的一半；小于视口的轴居中。
        const maxX = Math.max(0, (this._displayWidth(scale) - this._viewW) / 2);
        const maxY = Math.max(0, (this._displayHeight(scale) - this._viewH) / 2);
        return [clamp(x, -maxX, maxX), clamp(y, -maxY, maxY)];
    }

    _applyOffsets() {
        const [ox, oy] = this._clampOffsets(this._offX, this._offY, this._scale);
        this._offX = ox;
        this._offY = oy;
    }

    _zoomTo(target, ax, ay) {
        const fit = this._fitScale();
        const hardMin = this._t("image.zoom.minScale") / this._dpr;
        const hardMax = Math.max(this._t("image.zoom.maxScale") / this._dpr, fit);
        const lower = this._allowBelowFit ? hardMin : Math.max(hardMin, fit);
        const upper = Math.max(hardMax, lower);
        const next = clamp(target, lower, upper);
        if (same(next, this._scale)) return false;
        if (Math.abs(next - fit) <= EPS * Math.max(1, fit)) {
            // IMAGE-05：缩回适应后平移归零。
            this._fit = true;
            this._scale = fit;
            this._offX = 0;
            this._offY = 0;
            this._follow = false;
            return true;
        }
        const point = this.viewToImage(ax, ay);
        this._scale = next;
        this._fit = false;
        this._offX = (0.5 - point.u) * this._displayWidth() + (ax - this._viewW / 2);
        this._offY = (0.5 - point.v) * this._displayHeight() + (ay - this._viewH / 2);
        this._applyOffsets();
        this._recomputeFollow();
        return true;
    }

    _geometricFollow() {
        if (!this.hasImage || this._fit || !this._focus) return false;
        const center = this.viewToImage(this._viewW / 2, this._viewH / 2);
        const tolerance = this._t("image.focus.followTolerance");
        // IMAGE-02：两轴归一化偏差都小于容差才算仍在跟随。
        return Math.abs(center.u - this._focus.x) < tolerance
            && Math.abs(center.v - this._focus.y) < tolerance;
    }

    _recomputeFollow() {
        this._follow = this._geometricFollow();
    }

    _state() {
        return {
            fit: this._fit,
            scale: this._scale,
            offX: this._offX,
            offY: this._offY,
            follow: this._follow,
        };
    }

    _sameState(state) {
        return state.fit === this._fit
            && state.follow === this._follow
            && same(state.scale, this._scale)
            && same(state.offX, this._offX)
            && same(state.offY, this._offY);
    }
}
