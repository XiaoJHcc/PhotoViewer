/**
 * EXIF 摘要格式化（TOOLBAR-05）与拍摄时间文本（FILM-05、MENU-05）。
 *
 * formatExifValue(id, photo) -> string       id 取 settings.EXIF_ITEMS 的 id；缺失值一律 "--"
 * formatExifDetail(id, photo, value?)        悬停提示用的完整值（拍摄时间给完整格式，其余同摘要）
 * formatAperture(aperture)                   f/2.8、f/4、f/1.4（至多 1 位小数、去尾零）
 * formatShutter(seconds)                     1/250、0.3s、1s、2s；不足 1 秒且能近似为 1/n（偏差 ≤2%）时写分数
 * formatIso(iso) / formatFocal(mm) / formatEv(ev)
 * formatTakenShort(epochMs, tzOffsetMin)     MM-dd HH:mm（卡片第二行）
 * formatTakenSummary(epochMs, tzOffsetMin)   yyyy-MM-dd HH:mm（控制栏摘要）
 * formatTakenFull(epochMs, tzOffsetMin)      yyyy-MM-dd HH:mm:ss +08:00（菜单标题、tooltip）
 */

function finite(value) {
    return typeof value === "number" && Number.isFinite(value);
}

function trim(value, digits = 2) {
    const scale = 10 ** digits;
    return String(Math.round(value * scale) / scale);
}

function pad(value, width = 2) {
    return String(Math.abs(Math.trunc(value))).padStart(width, "0");
}

function textOrPlaceholder(value) {
    if (typeof value !== "string") return "--";
    const trimmed = value.trim();
    return trimmed === "" ? "--" : trimmed;
}

export function formatAperture(aperture) {
    if (!finite(aperture) || aperture <= 0) return "--";
    return `f/${trim(aperture, 1)}`;
}

export function formatShutter(seconds) {
    if (!finite(seconds) || seconds <= 0) return "--";
    if (seconds >= 1) return `${trim(seconds, 2)}s`;
    const reciprocal = Math.round(1 / seconds);
    // TOOLBAR-05：摄影习惯下不足 1 秒的快门优先写 1/n；与真实值相差超过 2% 时退回秒数。
    if (reciprocal >= 1 && Math.abs(1 / reciprocal - seconds) <= seconds * 0.02) {
        return `1/${reciprocal}`;
    }
    return `${trim(seconds, 2)}s`;
}

export function formatIso(iso) {
    if (!finite(iso) || iso <= 0) return "--";
    return String(Math.round(iso));
}

export function formatFocal(mm) {
    if (!finite(mm) || mm <= 0) return "--";
    return `${trim(mm, 1)}mm`;
}

export function formatEv(ev) {
    if (!finite(ev)) return "--";
    const sign = ev > 0 ? "+" : "";
    return `${sign}${trim(ev, 1)} EV`;
}

function offsetText(offsetMin) {
    const sign = offsetMin < 0 ? "-" : "+";
    const total = Math.abs(offsetMin);
    return `${sign}${pad(Math.floor(total / 60))}:${pad(total % 60)}`;
}

function dateParts(epochMs, tzOffsetMin) {
    const offset = finite(tzOffsetMin) ? Math.round(tzOffsetMin) : 0;
    const shifted = new Date(epochMs + offset * 60000);
    return {
        year: shifted.getUTCFullYear(),
        month: shifted.getUTCMonth() + 1,
        day: shifted.getUTCDate(),
        hour: shifted.getUTCHours(),
        minute: shifted.getUTCMinutes(),
        second: shifted.getUTCSeconds(),
        offset,
    };
}

export function formatTakenShort(epochMs, tzOffsetMin) {
    if (!finite(epochMs)) return "--";
    // FILM-05：卡片第二行 MM-dd HH:mm（照片当地时间）。
    const parts = dateParts(epochMs, tzOffsetMin);
    return `${pad(parts.month)}-${pad(parts.day)} ${pad(parts.hour)}:${pad(parts.minute)}`;
}

export function formatTakenSummary(epochMs, tzOffsetMin) {
    if (!finite(epochMs)) return "--";
    // TOOLBAR-05：控制栏摘要 yyyy-MM-dd HH:mm，完整值进悬停提示。
    const parts = dateParts(epochMs, tzOffsetMin);
    const date = `${String(parts.year).padStart(4, "0")}-${pad(parts.month)}-${pad(parts.day)}`;
    return `${date} ${pad(parts.hour)}:${pad(parts.minute)}`;
}

export function formatTakenFull(epochMs, tzOffsetMin) {
    if (!finite(epochMs)) return "--";
    const parts = dateParts(epochMs, tzOffsetMin);
    const date = `${String(parts.year).padStart(4, "0")}-${pad(parts.month)}-${pad(parts.day)}`;
    const time = `${pad(parts.hour)}:${pad(parts.minute)}:${pad(parts.second)}`;
    return `${date} ${time} ${offsetText(parts.offset)}`;
}

export function formatExifValue(id, photo) {
    if (!photo) return "--";
    const exif = photo.exif ?? {};
    switch (id) {
        case "aperture":
            return formatAperture(exif.aperture);
        case "exposure":
            return formatShutter(exif.exposure);
        case "iso":
            return formatIso(exif.iso);
        case "equivFocal":
            return formatFocal(exif.equivFocal);
        case "focal":
            return formatFocal(exif.focal);
        case "camera":
            return textOrPlaceholder(exif.camera);
        case "lens":
            return textOrPlaceholder(exif.lens);
        case "taken":
            return formatTakenSummary(photo.taken, photo.tzOffset);
        case "ev":
            return formatEv(exif.ev);
        case "wb":
            return textOrPlaceholder(exif.wb);
        case "flash":
            return textOrPlaceholder(exif.flash);
        default:
            return "--";
    }
}

/** TOOLBAR-05：悬停提示的完整值；拍摄时间给完整格式，其余与摘要相同。 */
export function formatExifDetail(id, photo, value = null) {
    if (id === "taken" && photo) return formatTakenFull(photo.taken, photo.tzOffset);
    return value ?? formatExifValue(id, photo);
}
