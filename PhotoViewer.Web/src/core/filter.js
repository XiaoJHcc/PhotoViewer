/**
 * 文件列表：格式与同名合并（OPEN-02、PRIN-06）→ 星级筛选（FILTER-03）→ 排序（FILTER-02），
 * 附带暂留（FILM-06）、锚点翻页（FILTER-05）与首图回退（OPEN-04）。
 * 所有变更方法返回 cards 是否变化；version 在 cards 变化时 +1。
 */

export const RATING_FILTERS = Object.freeze([
    { value: "all", label: "全部" },
    { value: "none", label: "无星级" },
    { value: "1", label: "一星" },
    { value: "2", label: "二星" },
    { value: "3", label: "三星" },
    { value: "4", label: "四星" },
    { value: "5", label: "五星" },
    { value: "1+", label: "一星以上" },
    { value: "2+", label: "二星以上" },
    { value: "3+", label: "三星以上" },
    { value: "4+", label: "四星以上" },
    { value: "conflict", label: "星级冲突" },
]);

export const SORT_KEYS = Object.freeze([
    { value: "name", label: "名称" },
    { value: "taken", label: "拍摄时间" },
    { value: "size", label: "文件大小" },
]);

export const SORT_ORDERS = Object.freeze([
    { value: "asc", label: "升序 ↑" },
    { value: "desc", label: "降序 ↓" },
]);

const RATING_VALUES = new Set(RATING_FILTERS.map((entry) => entry.value));
const SORT_KEY_VALUES = new Set(SORT_KEYS.map((entry) => entry.value));
const SORT_ORDER_VALUES = new Set(SORT_ORDERS.map((entry) => entry.value));

function buildExtIndex(formats) {
    const index = new Map();
    formats.forEach((format, position) => {
        for (const ext of format.exts) {
            index.set(ext.toLowerCase(), {
                position,
                id: format.id,
                enabled: format.enabled !== false,
            });
        }
    });
    return index;
}

function takenOf(photo) {
    return photo.taken ?? photo.modified;
}

function compareName(a, b) {
    const left = a.toLowerCase();
    const right = b.toLowerCase();
    if (left < right) return -1;
    if (left > right) return 1;
    return 0;
}

function signatureOf(cards) {
    let out = "";
    for (const card of cards) {
        out += `${card.key}:${card.photo.rating}`;
        for (const companion of card.companions) out += `|${companion.id}:${companion.rating}`;
        out += ";";
    }
    return out;
}

export class FileList {
    constructor({ formats = [], sameNameAsOne = true } = {}) {
        this._formats = formats;
        this._exts = buildExtIndex(formats);
        this._sameNameAsOne = sameNameAsOne !== false;
        this._photos = [];
        this._groups = [];
        this._allSorted = [];
        this._cards = [];
        this._cardByPhoto = new Map();
        this._cardIndex = new Map();
        this._sortKey = "name";
        this._sortOrder = "asc";
        this._ratingFilter = "all";
        this._currentId = null;
        this._retainedKey = null;
        this._anchor = null;
        this._signature = "";
        this._version = 0;
        this._recompute();
    }

    get cards() {
        return this._cards;
    }

    get count() {
        return this._cards.length;
    }

    get groups() {
        return this._groups;
    }

    get sortKey() {
        return this._sortKey;
    }

    get sortOrder() {
        return this._sortOrder;
    }

    get ratingFilter() {
        return this._ratingFilter;
    }

    get version() {
        return this._version;
    }

    setPhotos(photos) {
        this._photos = Array.isArray(photos) ? photos.slice() : [];
        this._retainedKey = null;
        return this._recompute();
    }

    setFormatOptions({ formats, sameNameAsOne } = {}) {
        if (formats) {
            this._formats = formats;
            this._exts = buildExtIndex(formats);
        }
        if (sameNameAsOne !== undefined) this._sameNameAsOne = sameNameAsOne !== false;
        return this._recompute();
    }

    setSort(key, order) {
        if (!SORT_KEY_VALUES.has(key)) throw new Error(`未知排序键：${key}`);
        const nextOrder = order ?? this._sortOrder;
        if (!SORT_ORDER_VALUES.has(nextOrder)) throw new Error(`未知排序方向：${nextOrder}`);
        if (key === this._sortKey && nextOrder === this._sortOrder) return false;
        this._sortKey = key;
        this._sortOrder = nextOrder;
        return this._recompute();
    }

    setRatingFilter(value) {
        if (!RATING_VALUES.has(value)) throw new Error(`未知星级筛选：${value}`);
        if (value === this._ratingFilter) return false;
        this._ratingFilter = value;
        return this._recompute();
    }

    setCurrent(photoId) {
        if ((photoId ?? null) !== this._currentId) this._anchor = null;
        this._currentId = photoId ?? null;
        let changed = false;
        if (this._retainedKey && this._retainedKey !== this._currentKey()) {
            // FILM-06：离开暂留项后才移除，改星瞬间不切图也不丢项
            this._retainedKey = null;
            changed = this._recompute();
        }
        if (!this._currentId) this._anchor = null;
        else this._syncAnchor();
        return changed;
    }

    noteRatingWritten(photoId) {
        const card = this._cardByPhoto.get(photoId) ?? null;
        if (card && this._currentKey() === card.key && !this._matches(card)) {
            // FILM-06：当前照片改星落出筛选时暂留，切走后由 setCurrent 移除
            this._retainedKey = card.key;
        }
        return this._recompute();
    }

    cardOf(photoId) {
        return this._cardByPhoto.get(photoId) ?? null;
    }

    cardIndexOf(photoId) {
        const card = this._cardByPhoto.get(photoId);
        if (!card) return -1;
        return this._cardIndex.get(card.key) ?? -1;
    }

    hasRatingConflict(card) {
        if (!this._sameNameAsOne || !card || card.companions.length === 0) return false;
        return card.companions.some((companion) => companion.rating !== card.photo.rating);
    }

    firstCard() {
        if (this._cards.length > 0) return this._cards[0];
        // OPEN-04：筛选为空时回退枚举首项所属同名合并组的代表
        return this._groups[0] ?? null;
    }

    neighbor(currentPhotoId, delta) {
        if (!delta) return null;
        const card = this._cardByPhoto.get(currentPhotoId) ?? null;
        const key = card ? card.key : currentPhotoId;
        const index = this._cardIndex.get(key) ?? -1;
        if (index >= 0) {
            const target = index + delta;
            return target >= 0 && target < this._cards.length ? this._cards[target] : null;
        }
        const anchor = this._anchor ?? this._anchorFromAllSorted(key);
        if (!anchor) return null;
        // FILTER-05：以重算前列表中的索引为参照，落到新列表最近的前/后项
        const step = delta > 0 ? 1 : -1;
        const start = step > 0
            ? this._firstPresent(anchor, anchor.index + 1, anchor.keys.length, 1)
            : this._firstPresent(anchor, anchor.index - 1, -1, -1);
        if (start < 0) return null;
        const target = start + step * (Math.abs(delta) - 1);
        return target >= 0 && target < this._cards.length ? this._cards[target] : null;
    }

    _currentKey() {
        if (!this._currentId) return null;
        return this._cardByPhoto.get(this._currentId)?.key ?? null;
    }

    _syncAnchor() {
        const key = this._currentKey();
        if (!key) return;
        const index = this._cardIndex.get(key) ?? -1;
        if (index >= 0) this._anchor = { keys: this._cards.map((card) => card.key), index };
    }

    _anchorFromAllSorted(key) {
        const index = this._allSorted.findIndex((card) => card.key === key);
        if (index < 0) return null;
        return { keys: this._allSorted.map((card) => card.key), index };
    }

    _firstPresent(anchor, from, to, step) {
        for (let i = from; step > 0 ? i < to : i > to; i += step) {
            const index = this._cardIndex.get(anchor.keys[i]);
            if (index !== undefined) return index;
        }
        return -1;
    }

    _isAllowed(photo) {
        if (photo.name.startsWith(".")) return false;
        const entry = this._exts.get(photo.ext);
        return Boolean(entry && entry.enabled);
    }

    _formatPosition(ext) {
        return this._exts.get(ext)?.position ?? Number.MAX_SAFE_INTEGER;
    }

    _formatId(ext) {
        return this._exts.get(ext)?.id ?? ext.replace(".", "").toUpperCase();
    }

    _buildGroups() {
        const cards = [];
        const byStem = new Map();
        for (const photo of this._photos) {
            if (!this._isAllowed(photo)) continue;
            if (!this._sameNameAsOne) {
                cards.push({ key: photo.id, photo, companions: [], displayName: photo.name });
                continue;
            }
            const stemKey = photo.stem.toLowerCase();
            let group = byStem.get(stemKey);
            if (!group) {
                group = [];
                byStem.set(stemKey, group);
                cards.push(group);
            }
            group.push(photo);
        }
        if (!this._sameNameAsOne) return cards;
        return cards.map((group) => this._finishGroup(group));
    }

    _finishGroup(group) {
        let representative = group[0];
        let best = this._formatPosition(representative.ext);
        for (const photo of group) {
            const position = this._formatPosition(photo.ext);
            if (position < best) {
                best = position;
                representative = photo;
            }
        }
        const companions = group.filter((photo) => photo !== representative);
        if (companions.length === 0) {
            return { key: representative.id, photo: representative, companions, displayName: representative.name };
        }
        // FILTER-02：显示名带 (RAW) 后缀，仅用于展示，不参与排序
        const ids = [...new Set(companions.map((photo) => this._formatId(photo.ext)))];
        return {
            key: representative.id,
            photo: representative,
            companions,
            displayName: `${representative.name}(${ids.join("/")})`,
        };
    }

    _matches(card) {
        const rating = card.photo.rating;
        const value = this._ratingFilter;
        if (value === "all") return true;
        if (value === "none") return rating === 0;
        if (value === "conflict") return this.hasRatingConflict(card);
        if (value.endsWith("+")) return rating >= Number(value.slice(0, -1));
        return rating === Number(value);
    }

    _compare(a, b) {
        let result;
        if (this._sortKey === "name") result = compareName(a.photo.name, b.photo.name);
        else if (this._sortKey === "taken") result = takenOf(a.photo) - takenOf(b.photo);
        else result = a.photo.size - b.photo.size;
        return this._sortOrder === "desc" ? -result : result;
    }

    _sortCards(cards) {
        return cards
            .map((card, index) => ({ card, index }))
            // FILTER-02：同键值保持原文件顺序，方向翻转不影响稳定次序
            .sort((left, right) => this._compare(left.card, right.card) || left.index - right.index)
            .map((entry) => entry.card);
    }

    _recompute() {
        this._syncAnchor();
        const groups = this._buildGroups();
        this._groups = groups;

        const byPhoto = new Map();
        for (const card of groups) {
            byPhoto.set(card.photo.id, card);
            for (const companion of card.companions) byPhoto.set(companion.id, card);
        }
        this._cardByPhoto = byPhoto;

        let retained = this._retainedKey;
        if (retained) {
            const card = groups.find((entry) => entry.key === retained);
            if (!card || this._matches(card)) retained = null;
        }
        this._retainedKey = retained;

        this._allSorted = this._sortCards(groups);
        this._cards = this._sortCards(groups.filter((card) => card.key === retained || this._matches(card)));
        this._cardIndex = new Map(this._cards.map((card, index) => [card.key, index]));

        const signature = signatureOf(this._cards);
        const changed = signature !== this._signature;
        if (changed) {
            this._signature = signature;
            this._version += 1;
        }
        return changed;
    }
}
