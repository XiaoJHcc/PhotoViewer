/**
 * 加载会话：把「切图 → 代理上屏 → 按需全分辨率 → 雾天增强」编排成一份可订阅快照。
 * 纯逻辑，不触 DOM；时间走注入的 clock，数值走注入的令牌函数 t。
 *
 * decoder = {
 *     decode(photo, "proxy" | "full", { longEdge, signal }) -> Promise<{ tier, width, height, surface }>,
 *     enhance(surface, { signal }) -> Promise<surface>,
 * }
 *
 * 主图展示产物在会话内按照片 id 保留最近若干份（cacheLimit，取设置里的缓存数量上限）：
 * 切到的照片若已有满足当前档位的产物，即同步换上新 surface（STATE-01「保留前一张」）；
 * 档位不足或没有产物则清图走黑屏加载（低分辨率顶替默认禁用）。
 *
 * new PhotoSession({ t, clock, decoder, onChange, cacheLimit })
 *     show(photo, { proxyLongEdge, wantFull })   切图；photo 为 null 进入空态
 *     setWantFull(bool)                          视口/分析栏可见性变化后的升级需求
 *     setProxyLongEdge(px)                       视口变大时重出代理
 *     setCacheLimit(count)                       缓存数量上限变化后裁剪
 *     cachedFullIds() -> string[]                已缓存全分辨率的照片 id（胶片栏细边口径）
 *     setEnhance(bool)                           雾天增强会话开关（无照片时不可开）；返回生效值
 *     memoryWarning()                            内存压力：自动关闭雾天增强并提示；返回是否因此关闭
 *     toast(text)                                状态提示条（STATE-03）
 *     openFailed(name, kind)                     打开失败提示条，返回文案
 *     snapshot() -> {
 *         photoId, surface, original, tier, loadingHint,
 *         error: null | { title, detail },
 *         enhance, busy,
 *         toast: null | { text, id },
 *         toastBottom,
 *     }
 *
 * new RatingWriter({ clock, latency, onToast })
 *     write(card, rating) -> Promise<boolean>    代表文件先行，同名伴侣随后同步
 */

const DEFAULT_CACHE_LIMIT = 30;

function positiveInt(value, fallback) {
    const number = Math.round(Number(value));
    return Number.isFinite(number) && number > 0 ? number : fallback;
}

export class PhotoSession {
    constructor({ t, clock, decoder, onChange, cacheLimit = DEFAULT_CACHE_LIMIT } = {}) {
        if (typeof t !== "function") throw new Error("PhotoSession 需要令牌函数 t");
        if (!clock) throw new Error("PhotoSession 需要 clock");
        if (!decoder) throw new Error("PhotoSession 需要 decoder");
        this._t = t;
        this._clock = clock;
        this._decoder = decoder;
        this._onChange = typeof onChange === "function" ? onChange : () => {};
        this._cache = new Map();
        this._cacheLimit = positiveInt(cacheLimit, DEFAULT_CACHE_LIMIT);

        this._photo = null;
        this._generation = 0;
        this._surface = null;
        this._original = null;
        this._tier = "none";
        this._loadingHint = false;
        this._error = null;
        this._enhance = false;
        this._busy = false;

        this._toast = null;
        this._toastSeq = 0;
        this._toastTimer = null;

        this._loadingTimer = null;
        this._proxyReq = null;
        this._fullReq = null;
        this._enhanceReq = null;
        this._enhanceToken = 0;
        this._busyTimer = null;

        this._wantFull = false;
        this._proxyLongEdge = null;
        this._proxyLongEdgeUsed = null;
    }

    snapshot() {
        const toastBottom = this._busy
            ? this._t("image.busy.bottomOffset") + this._t("image.busy.height") + this._t("status.toast.stackGap")
            : this._t("status.toast.bottomMargin");
        return {
            photoId: this._photo ? this._photo.id : null,
            surface: this._surface,
            original: this._original,
            tier: this._tier,
            loadingHint: this._loadingHint,
            error: this._error ? { ...this._error } : null,
            enhance: this._enhance,
            busy: this._busy,
            toast: this._toast ? { ...this._toast } : null,
            toastBottom,
        };
    }

    show(photo, options = {}) {
        if (!photo) {
            this._reset();
            this._emit();
            return;
        }
        const same = this._photo && photo.id === this._photo.id && this._surface && !this._error;
        if (same) {
            // OPEN-06：同一目标重复打开幂等，不重解码，只调整升级需求
            if (options.proxyLongEdge != null) this.setProxyLongEdge(options.proxyLongEdge);
            if (options.wantFull !== undefined) this.setWantFull(options.wantFull);
            return;
        }

        this._cancelEnhance();
        this._generation++;
        this._abort(this._proxyReq);
        this._abort(this._fullReq);
        this._proxyReq = null;
        this._fullReq = null;
        this._clearLoadingHint();
        this._stopBusy();

        this._photo = photo;
        this._original = null;
        this._tier = "none";
        this._error = null;
        this._loadingHint = false;
        this._wantFull = options.wantFull === undefined ? this._wantFull : Boolean(options.wantFull);
        if (options.proxyLongEdge != null) this._proxyLongEdge = Number(options.proxyLongEdge);
        this._proxyLongEdgeUsed = null;

        const cached = this._cache.get(photo.id);
        if (cached && this._cacheSatisfies(cached, this._wantFull)) {
            // STATE-01：缓存档位已满足当前需求，只剩上屏——保留前一张直到这次 emit 换上新图，
            // 不插黑帧、不起加载提示计时；新 surface 与新几何在同一次调用里生效。
            this._cacheSurface(photo.id, cached);
            this._surface = cached.surface;
            this._original = cached.surface;
            this._tier = cached.tier;
            this._proxyLongEdgeUsed = cached.longEdge;
            if (this._enhance) this._startEnhance();
            this._emit();
            return;
        }

        this._surface = null;
        // IMAGE-10 / STATE-01：缓存不满足需求即按未命中清旧图；低分辨率顶替默认禁用，
        // 需要 Full 而屏上只有代理时不得先拿代理顶替。
        this._emit();

        this._loadingTimer = this._clock.setTimeout(() => {
            this._loadingTimer = null;
            if (this._photo && !this._surface && !this._error) {
                this._loadingHint = true;
                this._emit();
            }
        }, this._t("status.loading.delay"));

        this._requestProxy({ longEdge: this._proxyLongEdge, keepSurface: false });
    }

    setWantFull(want) {
        const next = Boolean(want);
        this._wantFull = next;
        if (next) this._requestFull();
    }

    setProxyLongEdge(px) {
        const next = Number(px);
        if (!Number.isFinite(next) || next <= 0 || next === this._proxyLongEdge) return;
        this._proxyLongEdge = next;
        if (!this._photo || this._error || this._tier === "full") return;

        if (!this._surface) {
            if (this._proxyReq) {
                // 首张代理尚未上屏：按新档位重发，加载提示照常计时
                this._abort(this._proxyReq);
                this._proxyReq = null;
                this._requestProxy({ longEdge: next, keepSurface: false });
            }
            return;
        }
        if (this._tier !== "proxy") return;
        const used = this._proxyLongEdgeUsed ?? 0;
        if (next <= used) return;
        // 代理覆盖不足：后台重出，新图到达前旧代理维持上屏
        this._requestProxy({ longEdge: next, keepSurface: true });
    }

    setCacheLimit(count) {
        const next = positiveInt(count, this._cacheLimit);
        if (next === this._cacheLimit) return;
        this._cacheLimit = next;
        this._trimCache();
    }

    /** 已缓存全分辨率的照片 id：胶片栏细边（FILM 主图缓存标记）与展示产物缓存同一份口径。 */
    cachedFullIds() {
        const ids = [];
        for (const [id, entry] of this._cache) {
            if (entry.tier === "full") ids.push(id);
        }
        return ids;
    }

    setEnhance(on) {
        const next = Boolean(on);
        // MENU-07：无当前照片时增强不可开
        if (next && !this._photo) return false;
        if (next === this._enhance) {
            if (next && this._original && !this._enhanceReq) this._startEnhance();
            return this._enhance;
        }
        this._enhance = next;
        if (!next) {
            // IMAGE-07：关闭立即还原原片
            this._cancelEnhance();
            this._surface = this._original;
            this._emit();
            return false;
        }
        if (this._original) this._startEnhance();
        this._emit();
        return true;
    }

    memoryWarning() {
        if (!this._enhance) return false;
        this._enhance = false;
        this._cancelEnhance();
        this._surface = this._original;
        this._setToast("内存不足，已关闭雾天增强");
        this._emit();
        return true;
    }

    toast(text) {
        if (this._setToast(text)) this._emit();
    }

    openFailed(name, kind = "file", reason = null) {
        // STATE-03：文案统一为「动作：对象（原因）」，原因不可得时省略括号。
        const target = String(name ?? "");
        const action = kind === "folder" || kind === "directory" ? "无法读取文件夹" : "无法打开";
        const detail = reason === null || reason === undefined || String(reason).trim() === ""
            ? ""
            : `（${String(reason)}）`;
        const text = `${action}：${target}${detail}`;
        this.toast(text);
        return text;
    }

    _requestProxy({ longEdge, keepSurface }) {
        this._abort(this._proxyReq);
        const gen = this._generation;
        const req = { gen, ctl: new AbortController(), longEdge, keepSurface };
        this._proxyReq = req;
        let promise;
        try {
            promise = this._decoder.decode(this._photo, "proxy", { longEdge, signal: req.ctl.signal });
        } catch (error) {
            promise = Promise.reject(error);
        }
        Promise.resolve(promise).then(
            (result) => this._onProxy(gen, req, result),
            (error) => this._onProxyError(gen, req, error),
        );
    }

    _onProxy(gen, req, result) {
        if (gen !== this._generation || this._proxyReq !== req) return;
        this._proxyReq = null;
        if (req.keepSurface && this._tier !== "proxy") return;
        if (!result || !result.surface) {
            if (!req.keepSurface) this._proxyFailed();
            return;
        }
        this._clearLoadingHint();
        this._original = result.surface;
        this._surface = result.surface;
        this._tier = "proxy";
        this._proxyLongEdgeUsed = this._coverage(result, req.longEdge);
        // STATE-01：上屏产物进内存缓存，之后切回这张照片直接换图。
        this._cacheSurface(this._photo?.id, {
            surface: result.surface,
            tier: "proxy",
            longEdge: this._proxyLongEdgeUsed,
            sourceEdge: this._sourceEdge(),
        });
        if (this._enhance) this._startEnhance();
        if (this._wantFull) this._requestFull();
        this._emit();
    }

    _onProxyError(gen, req, error) {
        if (gen !== this._generation || this._proxyReq !== req) return;
        this._proxyReq = null;
        if (req.ctl.signal.aborted || req.keepSurface) return;
        this._proxyFailed(error);
    }

    _proxyFailed(error) {
        this._clearLoadingHint();
        this._abort(this._fullReq);
        this._fullReq = null;
        this._cancelEnhance();
        this._stopBusy();
        this._surface = null;
        this._original = null;
        this._tier = "none";
        const detail = this._photo && this._photo.name
            ? this._photo.name
            : String(error && error.message ? error.message : "");
        // STATE-02：代理也失败时主图给失败提示，不显示伪造或残留图像
        this._error = { title: "无法打开该图片", detail };
        this._emit();
    }

    _requestFull() {
        if (!this._photo || this._tier !== "proxy" || this._fullReq) return;
        const gen = this._generation;
        const req = { gen, ctl: new AbortController() };
        this._fullReq = req;
        let promise;
        try {
            promise = this._decoder.decode(this._photo, "full", { signal: req.ctl.signal });
        } catch (error) {
            promise = Promise.reject(error);
        }
        Promise.resolve(promise).then(
            (result) => this._onFull(gen, req, result),
            (error) => this._onFullError(gen, req, error),
        );
    }

    _onFull(gen, req, result) {
        if (gen !== this._generation || this._fullReq !== req) return;
        this._fullReq = null;
        if (!result || !result.surface) {
            this._fullFailed();
            return;
        }
        // 全分辨率已覆盖一切代理需求，在途的重出代理作废
        this._abort(this._proxyReq);
        this._proxyReq = null;
        this._original = result.surface;
        this._surface = result.surface;
        this._tier = "full";
        // STATE-01：Full 覆盖代理成为该照片的展示产物，胶片栏缓存细边也以此为口径。
        this._cacheSurface(this._photo?.id, {
            surface: result.surface,
            tier: "full",
            longEdge: this._coverage(result, 0),
            sourceEdge: this._sourceEdge(),
        });
        if (this._enhance) this._startEnhance();
        this._emit();
    }

    _onFullError(gen, req, error) {
        if (gen !== this._generation || this._fullReq !== req) return;
        this._fullReq = null;
        if (req.ctl.signal.aborted) return;
        this._fullFailed();
    }

    _fullFailed() {
        // IMAGE-03：保留已上屏代理，不报 error，等下一次触发再试
        this._setToast(`无法载入全分辨率：${this._photo ? this._photo.name : ""}`);
        this._emit();
    }

    _startEnhance() {
        if (!this._enhance || !this._original || this._error) return;
        this._abortEnhance();
        const token = ++this._enhanceToken;
        const ctl = new AbortController();
        this._enhanceReq = { ctl, token };
        const original = this._original;
        // IMAGE-07：计算期间继续显示原片；新产物到达时先回到原片再重算
        this._surface = original;
        if (!this._busy) {
            this._clearBusyTimer();
            this._busyTimer = this._clock.setTimeout(() => {
                this._busyTimer = null;
                if (token !== this._enhanceToken || !this._enhanceReq) return;
                this._busy = true;
                this._emit();
            }, this._t("image.busy.delay"));
        }
        let promise;
        try {
            promise = this._decoder.enhance(original, { signal: ctl.signal });
        } catch (error) {
            promise = Promise.reject(error);
        }
        Promise.resolve(promise).then(
            (result) => {
                if (token !== this._enhanceToken || ctl.signal.aborted) return;
                this._enhanceReq = null;
                this._clearBusyTimer();
                this._busy = false;
                this._surface = result;
                this._emit();
            },
            () => {
                if (token !== this._enhanceToken) return;
                this._enhanceReq = null;
                this._clearBusyTimer();
                this._busy = false;
                this._surface = this._original;
                this._emit();
            },
        );
    }

    _cancelEnhance() {
        this._abortEnhance();
        this._clearBusyTimer();
        this._busy = false;
    }

    _abortEnhance() {
        this._enhanceToken++;
        if (this._enhanceReq) {
            this._enhanceReq.ctl.abort();
            this._enhanceReq = null;
        }
    }

    _reset() {
        this._generation++;
        this._abort(this._proxyReq);
        this._abort(this._fullReq);
        this._proxyReq = null;
        this._fullReq = null;
        this._cancelEnhance();
        this._clearLoadingHint();
        this._photo = null;
        this._surface = null;
        this._original = null;
        this._tier = "none";
        this._error = null;
        this._wantFull = false;
        this._proxyLongEdgeUsed = null;
    }

    _coverage(result, requested) {
        const width = Number(result.width) || 0;
        const height = Number(result.height) || 0;
        const edge = Math.max(width, height);
        if (edge > 0) return edge;
        return Number(requested) || 0;
    }

    _sourceEdge() {
        const photo = this._photo;
        const width = Number(photo?.width) || 0;
        const height = Number(photo?.height) || 0;
        return Math.max(width, height);
    }

    // STATE-01：命中要求缓存档位已满足当前需求——需要 Full 时必须已是 Full；否则产物长边
    // 要覆盖当前代理目标，或产物本身已是源分辨率（代理被夹到源尺寸时不会再产出更大档）。
    _cacheSatisfies(entry, wantFull) {
        if (wantFull) return entry.tier === "full";
        if (entry.tier === "full") return true;
        const target = Math.max(0, Number(this._proxyLongEdge) || 0);
        if (entry.longEdge >= target) return true;
        return entry.sourceEdge > 0 && entry.longEdge >= entry.sourceEdge;
    }

    // 最近用过的照片排到表尾；超出缓存数量上限时从表头淘汰（与胶片栏细边共用这份表）。
    _cacheSurface(photoId, entry) {
        if (!photoId || !entry || !entry.surface) return;
        this._cache.delete(photoId);
        this._cache.set(photoId, entry);
        this._trimCache();
    }

    _trimCache() {
        while (this._cache.size > this._cacheLimit) {
            this._cache.delete(this._cache.keys().next().value);
        }
    }

    _setToast(text) {
        if (text == null || text === "") return false;
        this._toast = { text: String(text), id: ++this._toastSeq };
        if (this._toastTimer != null) this._clock.clearTimeout(this._toastTimer);
        this._toastTimer = this._clock.setTimeout(() => {
            this._toastTimer = null;
            this._toast = null;
            this._emit();
        }, this._t("motion.status.toast.duration"));
        return true;
    }

    _clearLoadingHint() {
        if (this._loadingTimer != null) {
            this._clock.clearTimeout(this._loadingTimer);
            this._loadingTimer = null;
        }
        this._loadingHint = false;
    }

    _stopBusy() {
        this._clearBusyTimer();
        this._busy = false;
    }

    _clearBusyTimer() {
        if (this._busyTimer != null) {
            this._clock.clearTimeout(this._busyTimer);
            this._busyTimer = null;
        }
    }

    _abort(request) {
        if (request && request.ctl) request.ctl.abort();
    }

    _emit() {
        this._onChange(this.snapshot());
    }
}

export class RatingWriter {
    constructor({ clock, latency = 120, onToast } = {}) {
        if (!clock) throw new Error("RatingWriter 需要 clock");
        this._clock = clock;
        this._latency = Math.max(0, Number(latency) || 0);
        this._onToast = typeof onToast === "function" ? onToast : () => {};
        this._jobs = new Map();
    }

    write(card, rating) {
        if (!card || !card.photo) return Promise.resolve(false);
        const key = card.key ?? card.photo.id;
        let job = this._jobs.get(key);
        return new Promise((resolve) => {
            if (!job) {
                job = { running: false, latest: null, waiters: [] };
                this._jobs.set(key, job);
            }
            job.waiters.push(resolve);
            const value = Number(rating);
            if (job.running) {
                // FILM-06：写回期间的重复请求排队，最终按最后一次为准
                job.latest = { card, rating: value };
                return;
            }
            job.running = true;
            this._run(job, key, { card, rating: value });
        });
    }

    async _run(job, key, first) {
        let current = first;
        let ok = false;
        for (;;) {
            ok = await this._perform(current.card, current.rating);
            if (!ok || !job.latest) break;
            current = job.latest;
            job.latest = null;
        }
        // FILM-06：全部写成功后才改内存，不先点亮再回滚
        if (ok) this._commit(current.card, current.rating);
        job.running = false;
        this._jobs.delete(key);
        const waiters = job.waiters;
        job.waiters = [];
        for (const resolve of waiters) resolve(ok);
    }

    async _perform(card, rating) {
        const companions = card.companions ?? [];
        for (const file of [card.photo, ...companions]) {
            await this._delay();
            if (file.fault && file.fault.write === true) {
                // STATE-03：评分写回失败「无法写入星级：<文件名>（<原因>）」，合成故障原因写「文件只读」。
                this._onToast(`无法写入星级：${file.name}（文件只读）`);
                return false;
            }
        }
        return true;
    }

    _commit(card, rating) {
        card.photo.rating = rating;
        for (const file of card.companions ?? []) file.rating = rating;
    }

    _delay() {
        return new Promise((resolve) => this._clock.setTimeout(resolve, this._latency));
    }
}
