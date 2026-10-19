/**
 * 合成 DINO/CV 特征：由照片的场景种子确定性生成，同一 base（连拍）彼此相似，variant 带来差异。
 * featuresFor(photo) → Features（见 types.js）；同指纹的伴侣得到同一份特征。
 * cosine(a, b) 为两个单位向量的余弦。
 */
import { GRID, PATCH_DIM, CLS_DIM } from "./types.js";

export function mulberry32(seed) {
    let a = seed >>> 0;
    return () => {
        a = (a + 0x6D2B79F5) >>> 0;
        let t = a;
        t = Math.imul(t ^ (t >>> 15), t | 1);
        t ^= t + Math.imul(t ^ (t >>> 7), t | 61);
        return ((t ^ (t >>> 14)) >>> 0) / 4294967296;
    };
}

function gaussian(rand) {
    const u = Math.max(rand(), 1e-9);
    return Math.sqrt(-2 * Math.log(u)) * Math.cos(2 * Math.PI * rand());
}

function normalize(v, offset = 0, length = v.length) {
    let sum = 0;
    for (let i = offset; i < offset + length; i++) sum += v[i] * v[i];
    const n = Math.sqrt(sum) || 1;
    for (let i = offset; i < offset + length; i++) v[i] /= n;
    return v;
}

export function cosine(a, b, aOffset = 0, bOffset = 0, length = a.length) {
    let dot = 0;
    for (let i = 0; i < length; i++) dot += a[aOffset + i] * b[bOffset + i];
    return dot;
}

const cache = new Map();

export function featuresFor(photo) {
    const key = `${photo.scene.base}:${photo.scene.variant}`;
    if (cache.has(key)) return cache.get(key);
    const baseRand = mulberry32(photo.scene.base * 7919 + 17);
    const varRand = mulberry32(photo.scene.base * 104729 + photo.scene.variant * 31 + 3);
    const spread = 0.18 + 0.12 * mulberry32(photo.scene.base + 5)();

    const cls = new Float32Array(CLS_DIM);
    for (let i = 0; i < CLS_DIM; i++) cls[i] = gaussian(baseRand) + spread * photo.scene.variant * 0.35 * gaussian(varRand);
    normalize(cls);

    const cells = GRID * GRID;
    const regions = 5;
    const centers = [];
    for (let r = 0; r < regions; r++) {
        const c = new Float32Array(PATCH_DIM);
        for (let i = 0; i < PATCH_DIM; i++) c[i] = gaussian(baseRand);
        centers.push({ vec: normalize(c), x: baseRand(), y: baseRand() });
    }
    const shiftX = (varRand() - 0.5) * 0.08 * photo.scene.variant;
    const shiftY = (varRand() - 0.5) * 0.08 * photo.scene.variant;
    const patches = new Float32Array(cells * PATCH_DIM);
    const pca = new Float32Array(cells * 3);
    const sharpness = new Float32Array(cells);
    const dx = new Float32Array(cells);
    const dy = new Float32Array(cells);
    const mask = new Uint8Array(cells);
    const shaky = photo.shake === true;
    const shakeAngle = baseRand() * Math.PI;
    const focusX = photo.focus ? photo.focus.x : 0.5;
    const focusY = photo.focus ? photo.focus.y : 0.5;

    for (let gy = 0; gy < GRID; gy++) {
        for (let gx = 0; gx < GRID; gx++) {
            const cell = gy * GRID + gx;
            const u = (gx + 0.5) / GRID + shiftX;
            const v = (gy + 0.5) / GRID + shiftY;
            let best = 0;
            let bestDist = Infinity;
            for (let r = 0; r < regions; r++) {
                const d = (u - centers[r].x) ** 2 + (v - centers[r].y) ** 2;
                if (d < bestDist) { bestDist = d; best = r; }
            }
            const o = cell * PATCH_DIM;
            for (let i = 0; i < PATCH_DIM; i++) patches[o + i] = centers[best].vec[i] + 0.25 * gaussian(varRand);
            normalize(patches, o, PATCH_DIM);
            for (let k = 0; k < 3; k++) pca[cell * 3 + k] = Math.min(1, Math.max(0, 0.5 + 0.5 * patches[o + k] * 1.6));
            const df = Math.hypot(u - focusX, v - focusY);
            sharpness[cell] = Math.max(0, Math.min(1, (shaky ? 0.45 : 0.95) * Math.exp(-df * df * 6) + 0.08 * varRand()));
            const strength = shaky ? 0.6 + 0.4 * varRand() : 0.05 * varRand();
            dx[cell] = Math.cos(shakeAngle) * strength;
            dy[cell] = Math.sin(shakeAngle) * strength;
            mask[cell] = sharpness[cell] > 0.15 ? 1 : 0;
        }
    }
    const verdict = photo.shake === true ? "抖动" : photo.shake === false ? "稳定" : "未判定";
    const features = Object.freeze({ cls, patches, pca, sharpness, shake: { dx, dy, mask }, verdict });
    cache.set(key, features);
    return features;
}
