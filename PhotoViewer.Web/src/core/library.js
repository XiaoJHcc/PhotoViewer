/**
 * 合成照片库：确定性生成演示文件夹，覆盖筛选、同名合并、相似聚类与故障状态。
 * createLibrary() → { folders: Folder[] }；generateFolder({ id, name, count, seed }) → Folder。
 * 同一随机种子必然生成同一份数据；Photo 对象可变（rating / extracted 由上层改写）。
 */
import { mulberry32 } from "./features.js";

const TZ = 480;
const SECOND = 1000;

const APERTURES = [1.4, 1.8, 2, 2.8, 3.5, 4, 5.6, 8, 11];
const EXPOSURES = [
    1 / 8000, 1 / 4000, 1 / 2000, 1 / 1000, 1 / 500, 1 / 250, 1 / 125,
    1 / 60, 1 / 30, 1 / 15, 1 / 8, 1 / 4, 1 / 2, 1, 2,
];
const ISOS = [100, 200, 400, 800, 1600, 3200, 6400, 12800];
const FOCALS = [16, 20, 24, 35, 50, 85, 135, 200];
const EVS = [-2, -1.3, -0.7, 0, 0, 0, 0, 0.3, 0.7, 1];
const CAMERAS = ["ILCE-7M4", "ILCE-7RM5"];
const LENSES = [
    "FE 16-35mm F2.8 GM",
    "FE 24-70mm F2.8 GM II",
    "FE 35mm F1.4 GM",
    "FE 50mm F1.2 GM",
    "FE 70-200mm F2.8 GM OSS II",
    "FE 200-600mm F5.6-6.3 G OSS",
];
const WBS = ["自动", "日光", "阴天", "白炽灯", "荧光灯", "自定义"];
const ORIENTATIONS = [1, 1, 1, 1, 1, 1, 1, 1, 1, 6, 6, 8, 8, 3];
const RATINGS = [0, 0, 1, 1, 2, 2, 2, 3, 3, 3, 3, 4, 4, 5];
const DIMS = [[6000, 4000], [6000, 4000], [6000, 4000], [6000, 4000], [6000, 4000], [4000, 6000]];
const SIZE_RANGES = {
    ".arw": [24, 52],
    ".jpg": [4, 14],
    ".jpeg": [4, 14],
    ".hif": [2, 6],
    ".heic": [2, 6],
    ".png": [12, 40],
    ".tif": [60, 120],
};

function pick(rand, list) {
    return list[Math.floor(rand() * list.length)];
}

function round(value, digits = 3) {
    const scale = 10 ** digits;
    return Math.round(value * scale) / scale;
}

function bytesFor(ext, rand) {
    const range = SIZE_RANGES[ext] ?? [1, 8];
    return Math.round((range[0] + (range[1] - range[0]) * rand()) * 1024 * 1024);
}

function makeFocus(rand) {
    const w = round(0.06 + rand() * 0.16);
    const h = round(w * (0.65 + rand() * 0.5));
    return {
        x: round(0.08 + rand() * Math.max(0, 0.84 - w)),
        y: round(0.08 + rand() * Math.max(0, 0.84 - h)),
        w,
        h,
    };
}

function makeExif(rand, focal) {
    return {
        aperture: pick(rand, APERTURES),
        exposure: pick(rand, EXPOSURES),
        iso: pick(rand, ISOS),
        focal,
        equivFocal: Math.round(focal * 1.5),
        camera: pick(rand, CAMERAS),
        lens: rand() < 0.9 ? pick(rand, LENSES) : null,
        ev: rand() < 0.88 ? pick(rand, EVS) : null,
        wb: pick(rand, WBS),
        flash: rand() < 0.9 ? "未闪光" : null,
    };
}

function makeShake(rand) {
    const value = rand();
    if (value < 0.18) return true;
    if (value < 0.62) return false;
    return null;
}

function createShotMaker(folderId, rand, start) {
    let clock = start.time;
    let number = start.number;
    let base = start.base;
    const make = (options = {}) => {
        clock += (options.gap ?? 20) * SECOND;
        const stem = options.stem ?? `DSC${String(number++).padStart(5, "0")}`;
        const taken = options.taken !== undefined ? options.taken : clock;
        const scene = options.scene ?? { base: ++base, variant: 0 };
        const randomRating = pick(rand, RATINGS);
        return {
            folderId,
            stem,
            exts: options.exts ?? [".ARW"],
            taken,
            modified: options.modified,
            scene,
            focal: options.focal ?? pick(rand, FOCALS),
            // RATING-01：rating 为 null 表示代表内嵌缺 Rating，不能用默认值顶替。
            rating: "rating" in options ? options.rating : randomRating,
            ratings: options.ratings,
            ratingState: options.ratingState,
            ratingStates: options.ratingStates,
            // RATING-01：只描述磁盘上实际存在的 <stem>.xmp；缺省表示没有 sidecar。
            sidecar: options.sidecar,
            orientations: options.orientations ?? [pick(rand, ORIENTATIONS)],
            dims: options.dims ?? [pick(rand, DIMS)],
            focus: options.focus !== undefined ? options.focus : rand() < 0.65,
            shake: options.shake !== undefined ? options.shake : makeShake(rand),
            extracted: options.extracted !== undefined ? options.extracted : rand() < 0.55,
            fault: options.fault,
            faults: options.faults,
            exif: options.exif,
        };
    };
    return { make };
}

function createShotFiles(rand, shot) {
    const taken = shot.taken ?? null;
    const fingerprint = taken === null ? null : `${shot.folderId}:${shot.stem}:${taken}`;
    const focus = shot.focus ? makeFocus(rand) : null;
    const exif = shot.exif ?? makeExif(rand, shot.focal);
    // RATING-01：同名组的 sidecar 是同一个磁盘文件，伴侣共享同一份记录。
    const sidecar = shot.sidecar ? { ...shot.sidecar } : null;
    return shot.exts.map((typedExt, index) => {
        const ext = typedExt.toLowerCase();
        const orientation = shot.orientations[index] ?? shot.orientations[0] ?? 1;
        const dims = shot.dims[index] ?? shot.dims[0] ?? [6000, 4000];
        const rating = shot.ratings && index < shot.ratings.length ? shot.ratings[index] : shot.rating;
        const ratingState = shot.ratingStates && index < shot.ratingStates.length
            ? shot.ratingStates[index]
            : shot.ratingState;
        return {
            id: `${shot.folderId}/${shot.stem}${typedExt}`,
            folderId: shot.folderId,
            name: `${shot.stem}${typedExt}`,
            stem: shot.stem,
            ext,
            size: bytesFor(ext, rand),
            modified: taken === null
                ? (shot.modified ?? 0) + index * SECOND
                : taken + (index + Math.round(rand() * 900)) * SECOND,
            taken,
            tzOffset: TZ,
            rating: rating === undefined ? 0 : rating,
            ratingState,
            sidecar,
            width: dims[0],
            height: dims[1],
            orientation,
            focus,
            exif: { ...exif },
            shake: shot.shake ?? null,
            fingerprint,
            fault: shot.faults?.[index] ?? shot.fault ?? {},
            scene: { base: shot.scene.base, variant: shot.scene.variant },
            extracted: Boolean(shot.extracted),
        };
    });
}

function collect(rand, shots) {
    const photos = [];
    for (const shot of shots) photos.push(...createShotFiles(rand, shot));
    return photos;
}

function buildHarbor() {
    const rand = mulberry32(0x10A2B3C4);
    const maker = createShotMaker("harbor", rand, {
        time: Date.UTC(2026, 9, 12, 5, 40),
        number: 1200,
        base: 1000,
    });
    const shots = [];
    const add = (options) => {
        const shot = maker.make(options);
        shots.push(shot);
        return shot;
    };

    // PRIN-06：同名对的连拍组，三组 JPG+ARW 共享 scene.base 供相似聚类使用
    for (let i = 0; i < 3; i++) {
        add({
            gap: 2,
            exts: i % 2 === 0 ? [".JPG", ".ARW"] : [".jpg", ".arw"],
            scene: { base: 101, variant: i },
            ratings: i === 0 ? [5, 4] : undefined,
            rating: 4,
            orientations: [i === 1 ? 6 : 1, i === 1 ? 6 : 1],
            extracted: true,
        });
    }

    // FILTER-03：其余同名对，若干组代表与伴侣星级不同以命中「星级冲突」
    for (let i = 0; i < 22; i++) {
        const conflict = i === 4 || i === 11 || i === 17;
        const exts = i % 3 === 0 ? [".JPG", ".ARW"] : i % 3 === 1 ? [".jpg", ".arw"] : [".JPG", ".arw"];
        // RATING-01：来源级故障样本——sidecar 取值不同、sidecar 无效、伴侣内嵌不可读、
        // 全部内嵌缺包；都是实际存在的来源，不隐藏、不合并。
        const sources = i === 4
            ? { sidecar: { rating: 3 } }
            : i === 5
                ? { ratings: [2, 2], sidecar: { rating: 2 } }
                : i === 7
                    ? { ratings: [2, 2], sidecar: { rating: 4 } }
                    : i === 9
                        ? { ratings: [1, 1], sidecar: { state: "invalid" } }
                        : i === 14
                            ? { ratings: [4, 4], ratingStates: ["rated", "unreadable"] }
                            : i === 20
                                ? { ratings: [null, null], ratingStates: ["missing", "missing"] }
                                : {};
        add({
            gap: 14 + (i % 4) * 6,
            exts,
            ratings: conflict ? [3, 5] : undefined,
            rating: pick(rand, RATINGS),
            orientations: [pick(rand, ORIENTATIONS), pick(rand, ORIENTATIONS)],
            ...sources,
        });
    }

    // PRIN-06：单张连拍组，同一 scene.base 下 variant 递增
    const bursts = [
        { base: 102, frames: 5, orientation: 1 },
        { base: 103, frames: 4, orientation: 8 },
        { base: 104, frames: 3, orientation: 1 },
        { base: 105, frames: 6, orientation: 6 },
    ];
    for (const burst of bursts) {
        const rating = pick(rand, RATINGS);
        for (let i = 0; i < burst.frames; i++) {
            add({
                gap: 1,
                exts: [i % 2 === 0 ? ".arw" : ".ARW"],
                scene: { base: burst.base, variant: i },
                rating,
                orientations: [i === 0 ? burst.orientation : pick(rand, ORIENTATIONS)],
            });
        }
    }

    // SIM-02：缺拍摄时间即无指纹，按文件时间回退并计入未提取
    for (let i = 0; i < 3; i++) {
        add({
            gap: 0,
            taken: null,
            modified: Date.UTC(2026, 9, 12, 1, 5 + i * 23),
            exts: [i === 1 ? ".JPG" : ".ARW"],
            extracted: false,
            shake: null,
        });
    }

    add({ gap: 12, exts: [".ARW"], fault: { decode: "all" }, rating: 2, extracted: true });
    add({ gap: 9, exts: [".JPG"], fault: { decode: "full" }, rating: 3 });
    add({ gap: 7, exts: [".JPG"], fault: { write: true }, rating: 4 });

    // RATING-01/05：代表文件内嵌的独立状态——已拒绝（-1）、缺失、无效与同一文件内冲突。
    // 缺失样本取 JPEG：无 canonical XMP 时连 JPEG 也不新建，写入按预检失败处理。
    const representativeStates = [
        { rating: -1, ratingState: "rejected" },
        { rating: null, ratingState: "missing" },
        { rating: null, ratingState: "invalid" },
        { rating: null, ratingState: "conflict" },
    ];
    for (let i = 0; i < 8; i++) {
        add({
            gap: 15 + i * 3,
            exts: [i % 2 === 1 ? ".JPG" : ".ARW"],
            orientations: [i === 0 ? 3 : pick(rand, ORIENTATIONS)],
            ...(i >= 4 ? representativeStates[i - 4] : {}),
        });
    }

    for (let i = 0; i < 6; i++) {
        add({ gap: 20, exts: [i % 2 === 1 ? ".HIF" : ".hif"], dims: [[4000, 6000]], orientations: [1] });
    }
    for (let i = 0; i < 2; i++) {
        add({ gap: 25, exts: [i === 1 ? ".PNG" : ".png"], dims: [[4000, 6000]], orientations: [1] });
    }

    // OPEN-02：停用格式与点号开头的隐藏文件留在枚举结果里，由列表排除
    add({ gap: 30, stem: "DSC09998", exts: [".TIF"], extracted: false });
    add({ gap: 30, stem: ".DSC09997", exts: [".ARW"], extracted: false });

    return {
        id: "harbor",
        name: "2026-10-12 海港",
        photos: collect(rand, shots),
        enumerable: true,
    };
}

function buildNight() {
    const rand = mulberry32(0x51EE7A11);
    const maker = createShotMaker("night", rand, {
        time: Date.UTC(2026, 9, 11, 11, 30),
        number: 5200,
        base: 2000,
    });
    const shots = [];
    const add = (options) => {
        const shot = maker.make(options);
        shots.push(shot);
        return shot;
    };

    const bursts = [4, 3, 5, 6];
    bursts.forEach((frames, burstIndex) => {
        const rating = pick(rand, RATINGS);
        for (let i = 0; i < frames; i++) {
            add({
                gap: 2,
                exts: [i % 2 === 0 ? ".JPG" : ".jpg"],
                scene: { base: 2100 + burstIndex, variant: i },
                rating,
                extracted: true,
                shake: i === 0 ? true : undefined,
            });
        }
    });
    for (let i = 0; i < 12; i++) {
        add({ gap: 22, exts: [".JPG"], extracted: true });
    }

    return {
        id: "night",
        name: "城市夜景",
        photos: collect(rand, shots),
        enumerable: true,
    };
}

function buildSingle() {
    const rand = mulberry32(0x0A11CE);
    const maker = createShotMaker("single", rand, {
        time: Date.UTC(2026, 9, 10, 9, 12),
        number: 4821,
        base: 3000,
    });
    const shot = maker.make({
        stem: "IMG_4821",
        exts: [".HEIC"],
        dims: [[4032, 3024]],
        orientations: [1],
        extracted: false,
        shake: null,
        rating: 3,
    });
    return {
        id: "single",
        name: "共享的照片",
        photos: collect(rand, [shot]),
        enumerable: false,
    };
}

export function createLibrary() {
    return {
        folders: [
            buildHarbor(),
            buildNight(),
            { id: "empty", name: "空文件夹", photos: [], enumerable: true },
            buildSingle(),
        ],
    };
}

export function generateFolder({ id, name, count = 0, seed = 1 }) {
    const total = Math.max(0, Math.floor(Number(count) || 0));
    const rand = mulberry32(seed >>> 0);
    const maker = createShotMaker(id, rand, {
        time: Date.UTC(2026, 9, 10, 2, 0),
        number: 1,
        base: 3000,
    });
    const photos = [];
    let scene = null;
    let frames = 0;
    let remaining = total;
    while (remaining > 0) {
        const pair = remaining >= 2 && rand() < 0.35;
        let burst = null;
        if (scene && frames < 6 && rand() < 0.2) {
            frames += 1;
            burst = { base: scene.base, variant: frames };
        }
        const shot = maker.make({
            gap: 3 + Math.floor(rand() * 24),
            exts: pair ? (rand() < 0.5 ? [".JPG", ".ARW"] : [".jpg", ".arw"]) : [rand() < 0.5 ? ".JPG" : ".ARW"],
            scene: burst ?? undefined,
        });
        if (!burst) {
            scene = shot.scene;
            frames = 0;
        }
        const files = createShotFiles(rand, shot);
        remaining -= files.length;
        if (remaining < 0) files.length = files.length + remaining;
        photos.push(...files);
    }
    return { id, name, photos, enumerable: true };
}
