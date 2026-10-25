/**
 * 星级来源模型（RATING-01/05）：同名合并组里每个文件的内嵌 XMP 与每个实际存在的
 * `<stem>.xmp` 各算一个来源，互不遮蔽；代表文件内嵌 Rating 只用于卡片/控制栏显示与
 * FILTER-03 的数字档。
 *
 * 来源级状态取 RATING-01 表：rated / missing / rejected / invalid / conflict / unreadable。
 * 合成数据用 photo.rating（代表值）与 photo.ratingState 描述文件内嵌；photo.sidecar 为 null
 * 表示磁盘上没有 sidecar（不预留来源），否则为 { rating, state } 记录。
 */

const RATING_STATES = Object.freeze({
    RATED: "rated",
    MISSING: "missing",
    REJECTED: "rejected",
    INVALID: "invalid",
    CONFLICT: "conflict",
    UNREADABLE: "unreadable",
});

const RATING_STATE_LABELS = Object.freeze({
    rated: "已评分",
    missing: "缺失",
    rejected: "已拒绝",
    invalid: "无效",
    conflict: "冲突",
    unreadable: "不可读",
});

const RATING_SOURCE_LABELS = Object.freeze({
    embedded: "内嵌",
    sidecar: "XMP",
});

const STATE_VALUES = new Set(Object.values(RATING_STATES));

// RATING-06：无 canonical XMP 时一律不支持新建（含 JPEG）；不用 sidecar 顶替，
// 也不新建 sidecar。已有 canonical 包、仅包内缺 xap:Rating 的安全插入不在合成模型内建模。
// PNG 的 XMP 写入整体挂账（含标准 iTXt），内嵌来源一律不写，sidecar 不受此限。
const PNG_NAME = /\.png$/i;

const WRITE_BLOCK_REASONS = Object.freeze({
    invalid: "XMP 包无法解析",
    conflict: "同一文件内多份 XMP 的星级不一致",
    unreadable: "XMP 包不可读",
    missing: "无 canonical XMP 包，不支持新建",
});

/** 记录归一：显式状态优先，缺失/非法取值按实际数字推断；未知状态按取值判定。 */
export function readRatingRecord(record) {
    const source = record ?? {};
    const declared = source.state ?? source.ratingState;
    const raw = source.rating ?? source.value;
    const present = raw !== null && raw !== undefined && raw !== "";
    const number = present ? Number(raw) : null;
    const rated = present && Number.isInteger(number) && number >= 0 && number <= 5;
    if (declared === RATING_STATES.REJECTED || number === -1) return { state: RATING_STATES.REJECTED, value: -1 };
    if (declared === RATING_STATES.RATED) {
        return rated ? { state: RATING_STATES.RATED, value: number } : { state: RATING_STATES.INVALID, value: null };
    }
    if (declared && STATE_VALUES.has(declared)) return { state: declared, value: null };
    if (!present) return { state: RATING_STATES.MISSING, value: null };
    if (rated) return { state: RATING_STATES.RATED, value: number };
    return { state: RATING_STATES.INVALID, value: null };
}

/** 代表文件内嵌 Rating：只用于显示与数字档筛选，不用于遮蔽其它来源。 */
export function representativeRating(photo) {
    if (!photo) return { state: RATING_STATES.MISSING, value: null };
    return readRatingRecord(photo);
}

/** FILM-05/TOOLBAR-03：点亮星数；已拒绝、缺失、无效、不可读与内部冲突都不点亮。 */
export function displayStars(photo) {
    const representative = representativeRating(photo);
    return representative.state === RATING_STATES.RATED ? representative.value : 0;
}

function sourceValueText(source) {
    if (!source) return "--";
    if (source.state === RATING_STATES.RATED) return `${source.value} 星`;
    if (source.state === RATING_STATES.REJECTED) return "-1";
    return "--";
}

function embeddedSourceOf(photo) {
    const record = representativeRating(photo);
    return {
        id: `embedded:${photo.id}`,
        kind: "embedded",
        label: photo.name,
        type: RATING_SOURCE_LABELS.embedded,
        state: record.state,
        value: record.value,
        stateLabel: RATING_STATE_LABELS[record.state],
        valueText: sourceValueText(record),
        photo,
    };
}

function sidecarSourceOf(photo, record) {
    const sidecar = readRatingRecord(record);
    return {
        id: `sidecar:${photo.folderId}/${photo.stem}`.toLowerCase(),
        kind: "sidecar",
        label: `${photo.stem}.xmp`,
        type: RATING_SOURCE_LABELS.sidecar,
        state: sidecar.state,
        value: sidecar.value,
        stateLabel: RATING_STATE_LABELS[sidecar.state],
        valueText: sourceValueText(sidecar),
        photo,
        record,
    };
}

/**
 * 组的实际来源：每个文件的内嵌位置各一个；每个实际存在的 sidecar 各一个。
 * sidecar 按 `<folder>/<stem>` 去重——它是同名的单个文件，不是每个伴侣一份。
 */
export function ratingSources(card) {
    if (!card || !card.photo) return [];
    const files = [card.photo, ...(card.companions ?? [])].filter((photo) => photo && photo.id);
    const sources = files.map(embeddedSourceOf);
    const seen = new Set();
    for (const photo of files) {
        if (!photo.sidecar || typeof photo.sidecar !== "object") continue;
        const key = `${photo.folderId}/${photo.stem}`.toLowerCase();
        if (seen.has(key)) continue;
        seen.add(key);
        sources.push(sidecarSourceOf(photo, photo.sidecar));
    }
    return sources;
}

function consistentValue(state) {
    return state === RATING_STATES.RATED || state === RATING_STATES.REJECTED;
}

/**
 * RATING-01：组级冲突 = 实际来源不处于「全部可读、全部带值且取值相同」。
 * 缺失、无效、不可读、同一文件内冲突与取值不同都算冲突。
 */
export function hasRatingConflict(card) {
    const sources = ratingSources(card);
    if (sources.length === 0) return false;
    const first = sources[0];
    if (!consistentValue(first.state)) return true;
    return sources.some((source) => !consistentValue(source.state) || source.value !== first.value);
}

/** FILTER-03：数字/以上档按代表内嵌；冲突独立；已拒绝只看 -1。 */
export function matchesRatingFilter(card, value) {
    if (value === "all") return true;
    if (value === "conflict") return hasRatingConflict(card);
    const representative = representativeRating(card?.photo);
    if (value === "rejected") return representative.state === RATING_STATES.REJECTED;
    if (value === "none") {
        return representative.state === RATING_STATES.MISSING
            || (representative.state === RATING_STATES.RATED && representative.value === 0);
    }
    const text = String(value);
    const threshold = text.endsWith("+");
    const target = Number(threshold ? text.slice(0, -1) : text);
    if (!Number.isFinite(target)) return false;
    if (representative.state !== RATING_STATES.RATED) return false;
    return threshold ? representative.value >= target : representative.value === target;
}

/** RATING-03 预检：返回该来源不能安全写入的原因；null 表示可写。 */
export function ratingWriteBlockReason(source) {
    if (!source) return "来源不存在";
    // RATING-06：PNG 内嵌 XMP 写入本轮挂账，任何状态都不写；sidecar 仍按普通来源处理。
    if (source.kind === "embedded" && PNG_NAME.test(source.photo?.name ?? source.label ?? "")) {
        return "PNG 内嵌 XMP 写入不支持";
    }
    if (source.state === RATING_STATES.RATED) return null;
    return WRITE_BLOCK_REASONS[source.state] ?? "来源状态未知";
}

/** 把统一值写进一个来源记录（内存真值；不创建文件、不新增 sidecar）。 */
export function applyRatingToSource(source, value) {
    if (!source) return;
    if (source.kind === "embedded") {
        source.photo.rating = value;
        source.photo.ratingState = RATING_STATES.RATED;
        return;
    }
    if (source.record && typeof source.record === "object") {
        source.record.rating = value;
        source.record.state = RATING_STATES.RATED;
    }
}
