/**
 * 相似聚类面板（SIM-01–11）。
 *
 * new SimilarityPanel({ t, clock, store, onChange })
 *   store.read(photo, { signal }) -> Promise<Features|null>（只读库，不触发推理）
 *   store.extractGroup(photos) -> Promise<void>（按指纹组批量提取，成功后组内 extracted=true）
 *
 * 公开 API：
 *   setExpanded(v) / setFilebarVisible(v) / setAnchor(photo|null) / setDisplayed(photoId)
 *   setPool(cards) / setParams({ threshold, maxResults }) / rated(photoId)
 *   snapshot() -> { visible, state: "empty"|"list", emptyText, calculating,
 *                   items: [{ card, score, percent, barRatio, current }],
 *                   unextracted, action: { label, visible }, progress: {done,total}|null, error }
 *   extractAll() -> Promise<void>
 */
import { cosine } from "./features.js";
import { defaultSettings } from "./settings.js";

const PARAM_THROTTLE_MS = 150;

function clamp(value, min, max) {
    return Math.min(max, Math.max(min, value));
}

function cardPhotos(card) {
    return [card.photo, ...(card.companions ?? [])].filter(Boolean);
}

function groupKey(card) {
    const photos = cardPhotos(card);
    const fingerprint = photos.map((photo) => photo.fingerprint).find((value) => value != null) ?? null;
    return fingerprint ? `fp:${fingerprint}` : `id:${card.photo.id}`;
}

function groupExtracted(photos) {
    return photos.some((photo) => photo.extracted === true);
}

function pickExtractedPhoto(card) {
    const photos = cardPhotos(card);
    return photos.find((photo) => photo.extracted === true) ?? card.photo;
}

/** 按指纹组统计未提取数（SIM-02）：同一次曝光的伴侣只算一张，缺拍摄时间的孤立文件各算一张。 */
export function countUnextracted(cards) {
    const groups = new Map();
    for (const card of cards ?? []) {
        if (!card?.photo) continue;
        const key = groupKey(card);
        const extracted = groupExtracted(cardPhotos(card));
        groups.set(key, (groups.get(key) ?? false) || extracted);
    }
    let count = 0;
    for (const extracted of groups.values()) if (!extracted) count += 1;
    return count;
}

function collectGroups(cards) {
    const groups = new Map();
    for (const card of cards ?? []) {
        if (!card?.photo) continue;
        const key = groupKey(card);
        let group = groups.get(key);
        if (!group) {
            group = { key, photos: [], seen: new Set() };
            groups.set(key, group);
        }
        for (const photo of cardPhotos(card)) {
            if (group.seen.has(photo.id)) continue;
            group.seen.add(photo.id);
            group.photos.push(photo);
        }
    }
    return [...groups.values()].map((group) => ({ key: group.key, photos: group.photos }));
}

function isCurrentCard(card, displayedId) {
    if (!displayedId) return false;
    return cardPhotos(card).some((photo) => photo.id === displayedId);
}

export class SimilarityPanel {
    constructor({ t, clock, store, onChange = null }) {
        this.t = t;
        this.clock = clock;
        this.store = store;
        this.onChange = onChange;

        const settings = defaultSettings();
        this.params = {
            threshold: settings.similarityThreshold,
            maxResults: settings.similarityMaxResults,
        };

        this.expanded = false;
        this.filebarVisible = true;
        this.anchor = null;
        this.displayedId = null;
        this.pool = [];
        this.items = [];
        this.unextracted = 0;
        this.progress = null;
        this.error = null;
        this.calculating = false;

        this.generation = 0;
        this.controller = null;
        this.throttleTimer = null;
        this.lastComputeAt = Number.NEGATIVE_INFINITY;
    }

    get visible() {
        return this.expanded && this.filebarVisible;
    }

    setExpanded(value) {
        const next = Boolean(value);
        if (this.expanded === next) return;
        this.expanded = next;
        this.syncVisibility();
    }

    setFilebarVisible(value) {
        const next = Boolean(value);
        if (this.filebarVisible === next) return;
        this.filebarVisible = next;
        this.syncVisibility();
    }

    syncVisibility() {
        if (this.visible) {
            this.adoptDisplayedAnchor();
            this.recount();
            this.requestCompute();
        } else {
            // SIM-06：面板关闭或文件栏隐藏立即取消在途计算，保留已算结果。
            this.cancelInFlight();
            this.calculating = false;
        }
        this.emit();
    }

    adoptDisplayedAnchor() {
        // SIM-05：面板关闭后重新打开时锚点回到当前照片（主图身份）。
        if (!this.displayedId) return;
        for (const card of this.pool) {
            const found = cardPhotos(card).find((entry) => entry.id === this.displayedId);
            if (found) {
                this.anchor = found;
                return;
            }
        }
    }

    setAnchor(photo) {
        const next = photo ?? null;
        if (!next && !this.anchor) return;
        if (next && this.anchor && next.id === this.anchor.id) return;
        this.anchor = next;
        this.recount();
        if (this.visible) this.requestCompute();
        this.emit();
    }

    setDisplayed(photoId) {
        const next = photoId ?? null;
        if (this.displayedId === next) return;
        this.displayedId = next;
        this.emit();
    }

    setPool(cards) {
        this.pool = Array.isArray(cards) ? cards.slice() : [];
        this.recount();
        if (this.visible) this.requestCompute();
        this.emit();
    }

    setParams(next = {}) {
        // SETTINGS-10：阈值 0.75–0.95，最多数量 1–32（设置模型范围，不进令牌）。
        const threshold = clamp(
            Number.isFinite(next.threshold) ? next.threshold : this.params.threshold,
            0.75,
            0.95,
        );
        const maxResults = Math.round(
            clamp(Number.isFinite(next.maxResults) ? next.maxResults : this.params.maxResults, 1, 32),
        );
        if (threshold === this.params.threshold && maxResults === this.params.maxResults) return;
        this.params = { threshold, maxResults };
        if (this.visible) this.scheduleCompute();
        this.emit();
    }

    scheduleCompute() {
        const wait = this.lastComputeAt + PARAM_THROTTLE_MS - this.clock.now();
        if (wait <= 0) {
            this.requestCompute();
            return;
        }
        if (this.throttleTimer !== null) return;
        this.throttleTimer = this.clock.setTimeout(() => {
            this.throttleTimer = null;
            if (this.visible) this.requestCompute();
        }, wait);
    }

    requestCompute() {
        if (this.throttleTimer !== null) {
            this.clock.clearTimeout(this.throttleTimer);
            this.throttleTimer = null;
        }
        this.lastComputeAt = this.clock.now();
        this.startCompute();
    }

    startCompute() {
        this.generation += 1;
        const gen = this.generation;
        this.cancelInFlight();
        if (!this.visible || !this.anchor) {
            this.calculating = false;
            this.items = [];
            this.emit();
            return;
        }
        this.calculating = true;
        const controller = new AbortController();
        this.controller = controller;
        this.run(gen, controller.signal);
        this.emit();
    }

    cancelInFlight() {
        if (this.controller) {
            this.controller.abort();
            this.controller = null;
        }
    }

    candidates() {
        const anchor = this.anchor;
        const anchorFingerprint = anchor.fingerprint;
        return this.pool.filter((card) => {
            const photos = cardPhotos(card);
            if (photos.some((photo) => photo.id === anchor.id)) return false;
            if (anchorFingerprint && photos.some((photo) => photo.fingerprint === anchorFingerprint)) return false;
            return groupExtracted(photos);
        });
    }

    async run(gen, signal) {
        try {
            const anchorFeatures = await this.store.read(this.anchor, { signal });
            if (gen !== this.generation) return;
            if (!anchorFeatures) {
                this.finish([]);
                return;
            }
            const entries = await Promise.all(this.candidates().map(async (card) => {
                const photo = pickExtractedPhoto(card);
                const features = await this.store.read(photo, { signal });
                if (!features) return null;
                return { card, score: cosine(anchorFeatures.cls, features.cls) };
            }));
            if (gen !== this.generation) return;
            this.finish(this.buildItems(entries));
        } catch (error) {
            if (gen !== this.generation) return;
            // SIM-10：计算失败或取消保留上一次结果，不切成空态、不弹提示。
            this.controller = null;
            this.calculating = false;
            this.emit();
        }
    }

    buildItems(entries) {
        const lowerBound = this.t("sim.score.lowerBound");
        const threshold = this.params.threshold;
        const limit = this.params.maxResults;
        const scored = entries.filter((entry) => entry && entry.score >= threshold);
        // SIM-08：分数降序，同分时拍摄时间更近的优先。
        scored.sort((a, b) => {
            if (b.score !== a.score) return b.score - a.score;
            const at = Number.isFinite(a.card.photo.taken) ? a.card.photo.taken : Number.NEGATIVE_INFINITY;
            const bt = Number.isFinite(b.card.photo.taken) ? b.card.photo.taken : Number.NEGATIVE_INFINITY;
            if (bt !== at) return bt - at;
            return a.card.key < b.card.key ? -1 : a.card.key > b.card.key ? 1 : 0;
        });
        return scored.slice(0, limit).map(({ card, score }) => ({
            card,
            score,
            percent: Math.round(score * 100),
            // SIM-04：柱长以 sim.score.lowerBound 为下限，不随阈值变化。
            barRatio: clamp((score - lowerBound) / (1 - lowerBound), 0, 1),
        }));
    }

    finish(items) {
        this.items = items;
        this.calculating = false;
        this.controller = null;
        this.recount();
        this.emit();
    }

    recount() {
        this.unextracted = countUnextracted(this.pool);
    }

    extractAll() {
        // SIM-03：提取中重复触发无效。
        if (this.progress) return Promise.resolve();
        const groups = collectGroups(this.pool).filter((group) => !groupExtracted(group.photos));
        this.error = null;
        this.progress = { done: 0, total: groups.length };
        this.emit();
        return this.runExtraction(groups);
    }

    async runExtraction(groups) {
        let failures = 0;
        for (const group of groups) {
            try {
                await this.store.extractGroup(group.photos);
            } catch (error) {
                failures += 1;
            }
            this.progress.done += 1;
            this.recount();
            // SIM-03：每写成功一组立即刷新计数，面板可见时按当前锚点重算。
            if (this.visible) this.requestCompute();
            this.emit();
        }
        this.progress = null;
        // SIM-10：失败组跳过，结束时给出可读文案并恢复按钮。
        this.error = failures > 0 ? `${failures} 组提取失败，可再次尝试` : null;
        this.recount();
        if (this.visible) this.requestCompute();
        this.emit();
    }

    rated(photoId) {
        // SIM-09：面板内改星成功后按同一锚点重算，锚点不因改星改变。
        if (!this.visible) {
            this.emit();
            return;
        }
        this.recount();
        this.requestCompute();
    }

    snapshot() {
        const items = this.items.map((item) => ({
            ...item,
            current: isCurrentCard(item.card, this.displayedId),
        }));
        const hasAnchor = Boolean(this.anchor);
        let emptyText = null;
        if (items.length === 0) {
            if (this.calculating) {
                emptyText = null;
            } else if (!hasAnchor || this.pool.length === 0) {
                emptyText = "无相似照片";
            } else {
                // SIM-02：两种空态互斥。
                emptyText = this.unextracted > 0 ? "尚未提取特征" : "无相似照片";
            }
        }
        const actionable = this.visible && hasAnchor && this.pool.length > 0 && this.progress === null;
        const hasWork = items.length > 0 || this.unextracted > 0;
        return {
            visible: this.visible,
            state: items.length > 0 ? "list" : "empty",
            emptyText,
            items,
            unextracted: this.unextracted,
            action: {
                label: items.length > 0 ? "补齐全部" : "提取全部",
                visible: actionable && hasWork,
            },
            progress: this.progress ? { done: this.progress.done, total: this.progress.total } : null,
            error: this.error,
            calculating: this.calculating,
        };
    }

    emit() {
        this.onChange?.();
    }
}
