/**
 * 程序化照片：用纯矢量在 Canvas2D 上按任意尺寸重绘同一场景（存储朝向，未应用 EXIF 方向）。
 *
 * drawScene(ctx, photo, { width, height, region })   整幅或源像素子矩形 → 输出像素尺寸
 * drawTile(ctx, photo, sx, sy, sw, sh, outW, outH)   源像素子矩形 → 输出像素矩形
 * orientationMatrix(o, w, h)  存储位图 → 显示朝向的 Canvas 仿射矩阵（含显示宽高）
 * displayDimensions(w, h, o)  显示朝向的宽高（方向 ≥5 换轴）
 * rectToDisplay(rect, o) / rectToStored(rect, o)     归一化中心矩形在两个坐标系间换算
 * focusInStored(photo)        Sony 对焦框（显示朝向）换回存储朝向；非法时 null
 * tileRegion(photo, region)   把可选的源像素子矩形夹进源尺寸内
 * sceneParams(photo)          确定性的场景参数（同一 scene.base 相同，variant 只做微小变化）
 */
import { mulberry32 } from "../core/features.js";

const TAU = Math.PI * 2;

const THEMES = [
    {
        skyTop: "#12233d", skyBottom: "#e59a63", glow: "rgba(255, 210, 150, 0.55)", disc: "#fff1c9",
        groundTop: "#3c3630", ground: "#171411", ridgeFar: "#33455a", ridgeNear: "#1d2b3a",
        building: "#101820", window: "rgba(255, 206, 138, 0.55)", accent: "#f0b45e",
        accent2: "#c96a4b", subject: "#f6e7c6", foreground: "#0d0b09",
    },
    {
        skyTop: "#0b1c33", skyBottom: "#5f7fa3", glow: "rgba(200, 220, 255, 0.35)", disc: "#dfe9ff",
        groundTop: "#28313a", ground: "#0e1217", ridgeFar: "#3a4f63", ridgeNear: "#22303d",
        building: "#0b1218", window: "rgba(160, 210, 255, 0.45)", accent: "#8fc4e8",
        accent2: "#d78a5d", subject: "#eef4ff", foreground: "#080a0d",
    },
    {
        skyTop: "#4a2d3c", skyBottom: "#f2b47a", glow: "rgba(255, 190, 140, 0.6)", disc: "#ffe8b8",
        groundTop: "#4a3a2c", ground: "#1a1410", ridgeFar: "#6b4a46", ridgeNear: "#3b2b2c",
        building: "#17100f", window: "rgba(255, 196, 120, 0.5)", accent: "#e8a45c",
        accent2: "#7f9b6a", subject: "#f8ead2", foreground: "#0f0b08",
    },
    {
        skyTop: "#1d3a32", skyBottom: "#cdd9a8", glow: "rgba(235, 245, 190, 0.45)", disc: "#f7ffdc",
        groundTop: "#33402b", ground: "#12160f", ridgeFar: "#4c6047", ridgeNear: "#2a3726",
        building: "#101510", window: "rgba(250, 240, 170, 0.45)", accent: "#b9d17a",
        accent2: "#c77c58", subject: "#f3f5df", foreground: "#0a0d08",
    },
    {
        skyTop: "#07101f", skyBottom: "#243a58", glow: "rgba(150, 190, 255, 0.25)", disc: "#e8eeff",
        groundTop: "#161d26", ground: "#070a0e", ridgeFar: "#22344a", ridgeNear: "#141f2b",
        building: "#060a0e", window: "rgba(255, 214, 140, 0.6)", accent: "#ffcf7a",
        accent2: "#5f88b8", subject: "#e6ecff", foreground: "#040607",
    },
    {
        skyTop: "#2a1b3d", skyBottom: "#d98a86", glow: "rgba(255, 170, 170, 0.5)", disc: "#ffd9d2",
        groundTop: "#3a2b34", ground: "#130f13", ridgeFar: "#5a4058", ridgeNear: "#32232f",
        building: "#120d13", window: "rgba(255, 180, 190, 0.5)", accent: "#e9a0a6",
        accent2: "#88a7c9", subject: "#fbe6e0", foreground: "#0b080b",
    },
    {
        skyTop: "#20334d", skyBottom: "#b8c9d8", glow: "rgba(230, 240, 255, 0.5)", disc: "#ffffff",
        groundTop: "#404a52", ground: "#14181c", ridgeFar: "#56697c", ridgeNear: "#32414f",
        building: "#0f151b", window: "rgba(220, 235, 255, 0.5)", accent: "#a9c7e0",
        accent2: "#d9a35f", subject: "#f4f8ff", foreground: "#090c0f",
    },
    {
        skyTop: "#3a2414", skyBottom: "#f0c07a", glow: "rgba(255, 220, 150, 0.6)", disc: "#fff2cf",
        groundTop: "#4a3620", ground: "#1b130b", ridgeFar: "#6d5137", ridgeNear: "#3d2d1d",
        building: "#160f09", window: "rgba(255, 210, 140, 0.5)", accent: "#e5b061",
        accent2: "#7f8f5a", subject: "#fbefd6", foreground: "#0e0905",
    },
];

function clamp(value, min, max) {
    return Math.min(max, Math.max(min, value));
}

function toNumber(value, fallback) {
    const number = Number(value);
    return Number.isFinite(number) ? number : fallback;
}

export function normalizeOrientation(value) {
    const code = Math.floor(toNumber(value, 1));
    return code >= 1 && code <= 8 ? code : 1;
}

export function displayDimensions(width, height, orientation) {
    const w = Math.max(1, toNumber(width, 1));
    const h = Math.max(1, toNumber(height, 1));
    return normalizeOrientation(orientation) >= 5
        ? { width: h, height: w }
        : { width: w, height: h };
}

export function orientationMatrix(orientation, width, height) {
    const w = Math.max(1, toNumber(width, 1));
    const h = Math.max(1, toNumber(height, 1));
    const code = normalizeOrientation(orientation);
    const table = {
        1: [1, 0, 0, 1, 0, 0],
        2: [-1, 0, 0, 1, w, 0],
        3: [-1, 0, 0, -1, w, h],
        4: [1, 0, 0, -1, 0, h],
        5: [0, 1, 1, 0, 0, 0],
        6: [0, 1, -1, 0, h, 0],
        7: [0, -1, -1, 0, h, w],
        8: [0, -1, 1, 0, 0, w],
    };
    const [a, b, c, d, e, f] = table[code];
    const dims = displayDimensions(w, h, code);
    return { a, b, c, d, e, f, width: dims.width, height: dims.height, sourceWidth: w, sourceHeight: h };
}

function mapToDisplay(x, y, code) {
    switch (code) {
        case 2: return { x: 1 - x, y };
        case 3: return { x: 1 - x, y: 1 - y };
        case 4: return { x, y: 1 - y };
        case 5: return { x: y, y: x };
        case 6: return { x: 1 - y, y: x };
        case 7: return { x: 1 - y, y: 1 - x };
        case 8: return { x: y, y: 1 - x };
        default: return { x, y };
    }
}

function mapToStored(x, y, code) {
    switch (code) {
        case 2: return { x: 1 - x, y };
        case 3: return { x: 1 - x, y: 1 - y };
        case 4: return { x, y: 1 - y };
        case 5: return { x: y, y: x };
        case 6: return { x: y, y: 1 - x };
        case 7: return { x: 1 - y, y: 1 - x };
        case 8: return { x: 1 - y, y: x };
        default: return { x, y };
    }
}

function mapRect(rect, code, map) {
    if (!rect) return null;
    const center = map(rect.x, rect.y, code);
    const swapped = code >= 5;
    const w = toNumber(rect.w, 0);
    const h = toNumber(rect.h, 0);
    return { x: center.x, y: center.y, w: swapped ? h : w, h: swapped ? w : h };
}

export function rectToDisplay(rect, orientation) {
    return mapRect(rect, normalizeOrientation(orientation), mapToDisplay);
}

export function rectToStored(rect, orientation) {
    return mapRect(rect, normalizeOrientation(orientation), mapToStored);
}

export function focusInStored(photo) {
    const focus = photo ? photo.focus : null;
    if (!focus) return null;
    const values = [focus.x, focus.y, focus.w, focus.h];
    if (!values.every((value) => typeof value === "number" && Number.isFinite(value))) return null;
    if (!(focus.w > 0) || !(focus.h > 0)) return null;
    const rect = rectToStored(focus, photo.orientation);
    rect.x = clamp(rect.x, 0, 1);
    rect.y = clamp(rect.y, 0, 1);
    return rect;
}

export function tileRegion(photo, region) {
    const width = Math.max(1, Math.round(toNumber(photo ? photo.width : 1, 1)));
    const height = Math.max(1, Math.round(toNumber(photo ? photo.height : 1, 1)));
    const hasRegion = region && typeof region === "object";
    const rawX = hasRegion ? toNumber(region.x, 0) : 0;
    const rawY = hasRegion ? toNumber(region.y, 0) : 0;
    const x = clamp(Math.round(rawX), 0, width - 1);
    const y = clamp(Math.round(rawY), 0, height - 1);
    const rawW = hasRegion ? toNumber(region.width, width) : width;
    const rawH = hasRegion ? toNumber(region.height, height) : height;
    return {
        x,
        y,
        width: Math.max(1, Math.min(Math.round(rawW), width - x)),
        height: Math.max(1, Math.min(Math.round(rawH), height - y)),
    };
}

export function sceneParams(photo) {
    const base = Math.floor(toNumber(photo && photo.scene ? photo.scene.base : 0, 0));
    const variant = Math.max(0, Math.floor(toNumber(photo && photo.scene ? photo.scene.variant : 0, 0)));
    const rand = mulberry32((Math.imul(base, 2654435761) ^ 0x9e3779b9) >>> 0);
    const theme = THEMES[Math.abs(base) % THEMES.length];

    const horizon = 0.44 + rand() * 0.08;
    const sun = { x: 0.58 + rand() * 0.3, y: 0.1 + rand() * 0.12, r: 0.028 + rand() * 0.022 };

    const mountains = [0, 1].map((layer) => {
        const count = 7 + layer * 2;
        const points = [];
        for (let i = 0; i <= count; i++) {
            const x = -0.05 + (i / count) * 1.1;
            const peak = 0.025 + rand() * (layer === 0 ? 0.13 : 0.08);
            points.push({ x, y: horizon - peak });
        }
        return { points };
    });

    const buildings = [];
    const buildingCount = 5 + Math.floor(rand() * 4);
    for (let i = 0; i < buildingCount; i++) {
        const w = 0.03 + rand() * 0.07;
        buildings.push({
            x: 0.02 + rand() * 0.9,
            w,
            h: 0.02 + rand() * 0.11,
            seed: Math.floor(rand() * 97),
        });
    }

    const objects = [];
    for (let i = 0; i < 3; i++) {
        objects.push({
            kind: i % 3,
            x: 0.06 + rand() * 0.42,
            y: horizon + 0.06 + rand() * 0.16,
            r: 0.02 + rand() * 0.035,
            tone: i % 2,
        });
    }

    const chart = {
        x: 0.585 + rand() * 0.04,
        y: 0.55 + rand() * 0.04,
        w: 0.3,
        h: 0.22,
    };

    const subject = focusInStored(photo) ?? { x: 0.5, y: 0.52, w: 0.16, h: 0.14 };

    return {
        base,
        variant,
        theme,
        horizon,
        sun,
        mountains,
        buildings,
        objects,
        chart,
        subject,
        foreground: { y: 0.855 + rand() * 0.03, bars: 14 + Math.floor(rand() * 10) },
        shift: { x: variant * 0.0045, y: variant * -0.0032 },
        dim: Math.min(0.14, variant * 0.018),
        blur: { angle: rand() * TAU + variant * 0.41, radius: 0.012 + rand() * 0.006 },
    };
}

function drawRidge(ctx, width, height, ridge, horizon, color, pad) {
    ctx.beginPath();
    ctx.moveTo(-pad, height * horizon + 1);
    for (const point of ridge.points) ctx.lineTo(point.x * width, point.y * height);
    ctx.lineTo(width + pad, height * horizon + 1);
    ctx.closePath();
    ctx.fillStyle = color;
    ctx.fill();
}

function drawChart(ctx, width, height, chart, theme) {
    const x = chart.x * width;
    const y = chart.y * height;
    const w = chart.w * width;
    const h = chart.h * height;
    const cols = 16;
    const rows = 6;
    const boardHeight = h * 0.52;
    const cellWidth = w / cols;
    const cellHeight = boardHeight / rows;

    ctx.fillStyle = "#f1eee4";
    ctx.fillRect(x, y, w, h);
    ctx.fillStyle = "#20242a";
    for (let row = 0; row < rows; row++) {
        for (let col = 0; col < cols; col++) {
            if ((row + col) % 2 === 0) continue;
            ctx.fillRect(x + col * cellWidth, y + row * cellHeight, cellWidth, cellHeight);
        }
    }

    const stripTop = y + h * 0.82;
    const stripHeight = h * 0.18;
    ctx.fillStyle = "#f1eee4";
    ctx.fillRect(x, stripTop, w, stripHeight);
    ctx.fillStyle = "#14181d";
    const periods = [24, 17, 12, 9, 6, 4, 3, 2];
    const groupWidth = w / periods.length;
    periods.forEach((period, index) => {
        const gx = x + index * groupWidth;
        for (let lx = 0; lx < groupWidth; lx += period) {
            ctx.fillRect(gx + lx, stripTop, Math.max(0.75, period * 0.35), stripHeight);
        }
    });

    ctx.fillStyle = "#14181d";
    ctx.textAlign = "left";
    ctx.textBaseline = "top";
    ctx.font = `700 ${Math.max(4, h * 0.16)}px "Helvetica Neue", Arial, sans-serif`;
    ctx.fillText("PV", x + w * 0.03, y + boardHeight + h * 0.03);
    ctx.font = `${Math.max(3, h * 0.07)}px "Helvetica Neue", Arial, sans-serif`;
    ctx.fillText(`${Math.round(width)} x ${Math.round(height)}`, x + w * 0.38, y + boardHeight + h * 0.05);
    ctx.fillStyle = theme.accent2;
    ctx.fillRect(x + w * 0.86, y + h * 0.04, w * 0.1, h * 0.08);
}

function drawSubject(ctx, width, height, subject, theme) {
    const cx = subject.x * width;
    const cy = subject.y * height;
    const rx = Math.max(2, subject.w * width * 0.5);
    const ry = Math.max(2, subject.h * height * 0.5);

    const glow = ctx.createRadialGradient(cx, cy, Math.min(rx, ry) * 0.2, cx, cy, Math.max(rx, ry) * 2);
    glow.addColorStop(0, "rgba(255, 248, 225, 0.35)");
    glow.addColorStop(1, "rgba(255, 248, 225, 0)");
    ctx.fillStyle = glow;
    ctx.beginPath();
    ctx.ellipse(cx, cy, rx * 2, ry * 2, 0, 0, TAU);
    ctx.fill();

    ctx.fillStyle = theme.subject;
    ctx.beginPath();
    ctx.ellipse(cx, cy, rx, ry, 0, 0, TAU);
    ctx.fill();

    ctx.strokeStyle = "rgba(20, 20, 20, 0.55)";
    ctx.lineWidth = Math.max(1, Math.min(rx, ry) * 0.02);
    for (const scale of [0.78, 0.55, 0.32]) {
        ctx.beginPath();
        ctx.ellipse(cx, cy, rx * scale, ry * scale, 0, 0, TAU);
        ctx.stroke();
    }

    const spokes = 24;
    ctx.lineWidth = Math.max(0.75, Math.min(rx, ry) * 0.012);
    for (let i = 0; i < spokes; i++) {
        const angle = (i / spokes) * TAU;
        ctx.beginPath();
        ctx.moveTo(cx + Math.cos(angle) * rx * 0.38, cy + Math.sin(angle) * ry * 0.38);
        ctx.lineTo(cx + Math.cos(angle) * rx * 0.92, cy + Math.sin(angle) * ry * 0.92);
        ctx.stroke();
    }

    ctx.fillStyle = "rgba(20, 20, 20, 0.8)";
    ctx.textAlign = "center";
    ctx.textBaseline = "middle";
    ctx.font = `700 ${Math.max(3, Math.min(rx, ry) * 0.22)}px "Helvetica Neue", Arial, sans-serif`;
    ctx.fillText("FOCUS", cx, cy);
}

function drawForeground(ctx, width, height, foreground, theme, pad) {
    const top = foreground.y * height;
    ctx.fillStyle = theme.foreground;
    ctx.fillRect(-pad, top, width + pad * 2, height - top + pad);
    const step = width / (foreground.bars * 2.4);
    ctx.strokeStyle = "rgba(220, 210, 190, 0.16)";
    ctx.lineWidth = Math.max(0.75, width * 0.0008);
    for (let x = 0; x < width; x += step) {
        const grass = ((x / step) % 3) * height * 0.006;
        ctx.beginPath();
        ctx.moveTo(x, top);
        ctx.lineTo(x + step * 0.35, top - grass - height * 0.006);
        ctx.stroke();
    }
}

function drawOrientationMarker(ctx, width, height) {
    const size = Math.min(width, height) * 0.12;
    const x = width * 0.03;
    const y = height * 0.04;
    ctx.save();
    ctx.globalAlpha = 0.94;
    ctx.fillStyle = "#f3efe3";
    ctx.fillRect(x, y, size, size);
    ctx.strokeStyle = "#161616";
    ctx.lineWidth = Math.max(1, size * 0.025);
    ctx.strokeRect(x + ctx.lineWidth / 2, y + ctx.lineWidth / 2, size - ctx.lineWidth, size - ctx.lineWidth);

    const ax = x + size * 0.32;
    ctx.strokeStyle = "#161616";
    ctx.lineCap = "round";
    ctx.lineWidth = Math.max(1, size * 0.09);
    ctx.beginPath();
    ctx.moveTo(ax, y + size * 0.82);
    ctx.lineTo(ax, y + size * 0.24);
    ctx.stroke();
    ctx.fillStyle = "#161616";
    ctx.beginPath();
    ctx.moveTo(ax - size * 0.16, y + size * 0.4);
    ctx.lineTo(ax, y + size * 0.16);
    ctx.lineTo(ax + size * 0.16, y + size * 0.4);
    ctx.closePath();
    ctx.fill();

    ctx.fillStyle = "#161616";
    ctx.textAlign = "center";
    ctx.textBaseline = "middle";
    ctx.font = `700 ${Math.max(4, size * 0.5)}px "Helvetica Neue", Arial, sans-serif`;
    ctx.fillText("F", x + size * 0.73, y + size * 0.56);
    ctx.restore();
}

function paintOnce(ctx, width, height, params, pad, dx, dy) {
    const theme = params.theme;
    const horizonY = height * params.horizon;
    ctx.save();
    ctx.translate(dx + params.shift.x * width, dy + params.shift.y * height);

    const sky = ctx.createLinearGradient(0, -pad, 0, horizonY);
    sky.addColorStop(0, theme.skyTop);
    sky.addColorStop(1, theme.skyBottom);
    ctx.fillStyle = sky;
    ctx.fillRect(-pad, -pad, width + pad * 2, horizonY + pad);

    const ground = ctx.createLinearGradient(0, horizonY, 0, height);
    ground.addColorStop(0, theme.groundTop);
    ground.addColorStop(1, theme.ground);
    ctx.fillStyle = ground;
    ctx.fillRect(-pad, horizonY, width + pad * 2, height - horizonY + pad);

    const sx = width * params.sun.x;
    const sy = height * params.sun.y;
    const sr = Math.min(width, height) * params.sun.r;
    const glow = ctx.createRadialGradient(sx, sy, sr * 0.2, sx, sy, sr * 3.4);
    glow.addColorStop(0, theme.glow);
    glow.addColorStop(1, "rgba(0, 0, 0, 0)");
    ctx.fillStyle = glow;
    ctx.beginPath();
    ctx.arc(sx, sy, sr * 3.4, 0, TAU);
    ctx.fill();
    ctx.fillStyle = theme.disc;
    ctx.beginPath();
    ctx.arc(sx, sy, sr, 0, TAU);
    ctx.fill();

    drawRidge(ctx, width, height, params.mountains[0], params.horizon, theme.ridgeFar, pad);
    drawRidge(ctx, width, height, params.mountains[1], params.horizon, theme.ridgeNear, pad);

    for (const building of params.buildings) {
        const bx = building.x * width;
        const bw = building.w * width;
        const top = height * (params.horizon - building.h);
        ctx.fillStyle = theme.building;
        ctx.fillRect(bx, top, bw, horizonY - top + Math.max(1, height * 0.008));
        const cols = Math.max(1, Math.floor(bw / (width * 0.012)));
        const rows = Math.max(1, Math.floor((horizonY - top) / (height * 0.02)));
        ctx.fillStyle = theme.window;
        for (let row = 0; row < rows; row++) {
            for (let col = 0; col < cols; col++) {
                if ((row * 7 + col * 3 + building.seed) % 5 === 0) continue;
                const wx = bx + (bw * (col + 0.5)) / cols - width * 0.002;
                const wy = top + ((horizonY - top) * (row + 0.5)) / rows - height * 0.004;
                ctx.fillRect(wx, wy, Math.max(0.75, width * 0.004), Math.max(0.75, height * 0.008));
            }
        }
    }

    for (const object of params.objects) {
        const ox = object.x * width;
        const oy = object.y * height;
        const radius = object.r * Math.min(width, height);
        ctx.fillStyle = object.tone === 0 ? theme.accent2 : theme.accent;
        ctx.globalAlpha = 0.9;
        ctx.beginPath();
        if (object.kind === 0) {
            ctx.arc(ox, oy, radius, 0, TAU);
        } else if (object.kind === 1) {
            ctx.moveTo(ox, oy - radius);
            ctx.lineTo(ox + radius * 0.95, oy + radius * 0.75);
            ctx.lineTo(ox - radius * 0.95, oy + radius * 0.75);
            ctx.closePath();
        } else {
            ctx.rect(ox - radius, oy - radius * 0.7, radius * 2, radius * 1.4);
        }
        ctx.fill();
        ctx.globalAlpha = 1;
        ctx.strokeStyle = "rgba(12, 12, 12, 0.45)";
        ctx.lineWidth = Math.max(0.75, radius * 0.06);
        ctx.stroke();
    }

    drawChart(ctx, width, height, params.chart, theme);
    drawSubject(ctx, width, height, params.subject, theme);
    drawForeground(ctx, width, height, params.foreground, theme, pad);
    ctx.restore();
}

export function drawTile(ctx, photo, sx, sy, sw, sh, outW, outH) {
    if (!ctx) throw new Error("drawTile 需要 Canvas2D 上下文");
    const sourceWidth = Math.max(1, Math.round(toNumber(photo ? photo.width : 1, 1)));
    const sourceHeight = Math.max(1, Math.round(toNumber(photo ? photo.height : 1, 1)));
    const region = tileRegion(photo, {
        x: sx,
        y: sy,
        width: sw ?? sourceWidth,
        height: sh ?? sourceHeight,
    });
    const outWidth = Math.max(1, Math.round(toNumber(outW, region.width)));
    const outHeight = Math.max(1, Math.round(toNumber(outH, region.height)));
    const params = sceneParams(photo);
    const pad = Math.max(sourceWidth, sourceHeight) * 0.06;

    ctx.save();
    ctx.beginPath();
    ctx.rect(0, 0, outWidth, outHeight);
    ctx.clip();
    ctx.scale(outWidth / region.width, outHeight / region.height);
    ctx.translate(-region.x, -region.y);

    if (photo && photo.shake === true) {
        const radius = Math.max(sourceWidth, sourceHeight) * params.blur.radius;
        const dx = Math.cos(params.blur.angle) * radius;
        const dy = Math.sin(params.blur.angle) * radius;
        const steps = 5;
        ctx.globalAlpha = 0.55;
        for (let i = 0; i < steps; i++) {
            const t = steps === 1 ? 0 : (i / (steps - 1)) * 2 - 1;
            paintOnce(ctx, sourceWidth, sourceHeight, params, pad, dx * t, dy * t);
        }
        ctx.globalAlpha = 1;
    } else {
        paintOnce(ctx, sourceWidth, sourceHeight, params, pad, 0, 0);
    }

    if (params.dim > 0) {
        ctx.fillStyle = `rgba(0, 0, 0, ${params.dim.toFixed(3)})`;
        ctx.fillRect(region.x, region.y, region.width, region.height);
    }

    // IMAGE-08：方向标记画在存储朝向的左上角，显示变换后应随之旋转/翻转，用于核对方向码。
    drawOrientationMarker(ctx, sourceWidth, sourceHeight);

    ctx.restore();
    return { ...region, outWidth, outHeight };
}

export function drawScene(ctx, photo, options = {}) {
    const width = Math.max(1, Math.round(toNumber(options.width, photo ? photo.width : 1)));
    const height = Math.max(1, Math.round(toNumber(options.height, photo ? photo.height : 1)));
    const region = tileRegion(photo, options.region);
    drawTile(ctx, photo, region.x, region.y, region.width, region.height, width, height);
    return { ...region, outWidth: width, outHeight: height };
}
