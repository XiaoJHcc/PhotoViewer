/**
 * 页内行为检查（?check=<模块|all>）：用真实 DOM 事件驱动壳层，按设计 ID 汇总到浮层、console 与
 * globalThis.pvCheckResult。每条检查前重置到已知场景（harbor、1200×850、auto、减少动态效果开），
 * 故障照片按字段查找而不是写死文件名。
 *
 * 覆盖：FILM / FILTER / RATING / MENU / INPUT / IMAGE / TOOLBAR / STATE / ANALYSIS / SIM / EDITION / OPEN / SHELL。
 */

import { displayStars, hasRatingConflict, ratingSources, ratingWriteBlockReason, representativeRating } from "../core/rating.js";
import { createTokens } from "../core/tokens.js";
import { computeLayout, approximateTextWidth } from "../core/layout/index.js";

const MODULES = ["film", "filter", "rating", "menu", "input", "image", "toolbar", "state", "analysis", "similarity", "edition", "open", "shell", "mac"];

const KEY_OF = {
    ArrowLeft: "ArrowLeft",
    ArrowRight: "ArrowRight",
    ArrowUp: "ArrowUp",
    ArrowDown: "ArrowDown",
    Escape: "Escape",
    Enter: "Enter",
    Backquote: "`",
    Equal: "=",
    Minus: "-",
    KeyZ: "z",
    KeyF: "f",
    KeyE: "e",
    Digit0: "0",
    Digit1: "1",
    Digit2: "2",
    Digit3: "3",
    Digit4: "4",
    Digit5: "5",
};

// 令牌颜色是 #AARRGGBB，getComputedStyle 返回 rgb()/rgba()；逐通道比较，允许 1/255 的取整误差。
function parseColor(value) {
    const source = String(value ?? "").trim();
    if (source.startsWith("#")) {
        const hex = source.slice(1);
        const alpha = hex.length === 8 ? parseInt(hex.slice(0, 2), 16) / 255 : 1;
        const body = hex.length === 8 ? hex.slice(2) : hex;
        return {
            r: parseInt(body.slice(0, 2), 16),
            g: parseInt(body.slice(2, 4), 16),
            b: parseInt(body.slice(4, 6), 16),
            a: alpha,
        };
    }
    // color-mix 的计算值在 Chromium 里是 color(srgb r g b / a)，分量是 0–1。
    const srgb = /^color\(srgb\s+([\d.]+)\s+([\d.]+)\s+([\d.]+)\s*(?:\/\s*([\d.]+))?\)$/.exec(source);
    if (srgb) {
        return {
            r: Math.round(parseFloat(srgb[1]) * 255),
            g: Math.round(parseFloat(srgb[2]) * 255),
            b: Math.round(parseFloat(srgb[3]) * 255),
            a: srgb[4] === undefined ? 1 : parseFloat(srgb[4]),
        };
    }
    const match = /rgba?\(([^)]+)\)/.exec(source);
    if (!match) return null;
    const parts = match[1].split(/[,/\s]+/).filter(Boolean).map(Number);
    return { r: parts[0], g: parts[1], b: parts[2], a: parts[3] ?? 1 };
}

function cssRule(fragment) {
    for (const sheet of document.styleSheets) {
        let rules = null;
        try {
            rules = sheet.cssRules;
        } catch {
            continue;
        }
        for (const rule of rules) {
            if (rule.selectorText && rule.selectorText.includes(fragment)) return rule;
        }
    }
    return null;
}

// 把产品样式里的 :hover / :active 换成临时 class，读一次真实计算值后立即撤掉（仅检查使用）。
function computedState(node, fragment, { pseudo = null, state = ":hover" } = {}) {
    const rule = cssRule(fragment);
    if (!rule) return null;
    const marker = "pv-check-forced-state";
    const selector = rule.selectorText.replaceAll(state, `.${marker}`);
    const style = document.createElement("style");
    style.textContent = `${selector} { ${rule.style.cssText} }`;
    document.head.append(style);
    node.classList.add(marker);
    const target = pseudo ? getComputedStyle(node, pseudo) : getComputedStyle(node);
    const snapshot = {
        backgroundColor: target.backgroundColor,
        borderTopColor: getComputedStyle(node).borderTopColor,
        width: target.width,
        height: target.height,
        borderRadius: target.borderTopLeftRadius,
    };
    node.classList.remove(marker);
    style.remove();
    return snapshot;
}

function colorMatches(actual, expected, opacity) {
    const a = parseColor(actual);
    const b = parseColor(expected);
    if (!a || !b) return false;
    return Math.abs(a.r - b.r) <= 1 && Math.abs(a.g - b.g) <= 1
        && Math.abs(a.b - b.b) <= 1 && Math.abs(a.a - (opacity ?? b.a)) <= 0.01;
}

// ?check=all 的执行顺序：先读状态，再做会改变内存数据的提取，最后跑外壳持久化。
const CHECKS = [
    {
        id: "FILM-01",
        title: "点选不定位；导航居中；交换/排序/筛选/评分不滚",
        async run(shell, h) {
            const { app } = shell;
            const cards = app.state.cards;
            h.assert(cards.length > 5, "harbor 卡片数不足");
            const targetIndex = 2;
            const node = h.filmCardAt(targetIndex);
            const before = h.filmOffset();
            h.click(node);
            await h.tick();
            h.assert(h.filmOffset() === before, `点选卡片改变了列表偏移：${before} → ${h.filmOffset()}`);
            h.assert(app.state.displayedId === cards[targetIndex].photo.id, "点选未切换当前照片");
            h.assert(h.currentFilmKey() === cards[targetIndex].key, "当前项高亮未跟随点选");

            h.key("ArrowRight");
            await h.tick(80);
            const nextIndex = h.cardIndex(app.state.displayedId);
            const expected = h.expectedCenter(nextIndex);
            h.assert(Math.abs(h.filmOffset() - expected) <= 1, `→ 后偏移 ${h.filmOffset()} ≠ 期望 ${expected}`);

            const offsetAfterNav = h.filmOffset();
            h.key("KeyZ");
            await h.tick(80);
            h.assert(app.state.displayedId === cards[targetIndex].photo.id, "Z 未切回上一张");
            h.assert(h.filmOffset() === offsetAfterNav, "切换上一张改变了列表偏移");

            await h.openFilter("sort");
            h.click(h.menuItem("taken"));
            await h.tick(80);
            h.assert(app.state.sortKey === "taken", "排序未应用");
            h.assert(h.filmOffset() === offsetAfterNav, "排序改变了列表偏移");
            h.key("Escape");
            await h.tick(40);

            const displayBeforeStar = app.state.displayedId;
            const starCard = h.filmCardAt(1);
            h.click(starCard.querySelectorAll(".pv-card-star")[4]);
            await h.tick(120);
            h.assert(app.state.displayedId === displayBeforeStar, "点卡片星级切换了当前照片");
            h.assert(h.filmOffset() === offsetAfterNav, "点卡片星级改变了列表偏移");

            await h.openFilter("rating");
            h.click(h.menuItem("3"));
            await h.tick(80);
            h.assert(app.state.ratingFilters.includes("3"), "星级筛选未应用");
            h.key("Escape");
            await h.tick(40);
            h.assert(app.state.displayedId === displayBeforeStar, "星级筛选切换了当前照片");
            h.assert(h.filmOffset() === offsetAfterNav, "星级筛选改变了列表偏移");
        },
    },
    {
        id: "FILM-02",
        title: "定位目标 = 卡片起点 + (卡片长 − 视口长)/2，首尾夹取且不环绕",
        async run(shell, h) {
            const { app } = shell;
            const cards = app.state.cards;
            app.selectCard(cards[1].photo.id);
            await h.tick(60);
            h.key("ArrowLeft");
            await h.tick(80);
            h.assert(app.state.displayedId === cards[0].photo.id, "← 未到达首张");
            h.assert(h.filmOffset() === 0, `首张定位偏移 ${h.filmOffset()} ≠ 0`);
            h.key("ArrowLeft");
            await h.tick(80);
            h.assert(app.state.displayedId === cards[0].photo.id, "首张再按 ← 发生了环绕");
            h.assert(h.filmOffset() === 0, `首张重复定位偏移 ${h.filmOffset()} ≠ 0`);

            app.selectCard(cards[cards.length - 3].photo.id);
            await h.tick();
            h.key("ArrowRight");
            await h.tick(80);
            h.key("ArrowRight");
            await h.tick(120);
            const last = cards[cards.length - 1];
            h.assert(app.state.displayedId === last.photo.id, "→ 未到达末张");
            const max = h.filmMaxOffset();
            h.assert(Math.abs(h.filmOffset() - max) <= 1, `末张偏移 ${h.filmOffset()} ≠ max ${max}`);
            const settled = h.filmOffset();
            h.key("ArrowRight");
            await h.tick(100);
            h.assert(app.state.displayedId === last.photo.id, "末张再按 → 发生了环绕");
            h.assert(h.filmOffset() === settled, "端点重复请求改变了偏移");
        },
    },
    {
        id: "FILM-03",
        title: "定位动画可被滚轮接管，停在用户位置",
        async run(shell, h) {
            const { app } = shell;
            shell.setReducedMotion(false);
            shell.setWindowSize(700, 850);
            await h.tick(120);
            h.assert(app.state.layout.layoutMode === "row", "700×850 未进入行布局");
            const cards = app.state.cards;
            app.selectCard(cards[0].photo.id);
            await h.tick(60);
            for (let index = 0; index < 4; index += 1) h.key("ArrowRight");
            const targetIndex = h.cardIndex(app.state.displayedId);
            const target = h.expectedCenter(targetIndex);
            h.assert(Math.abs(target - h.filmOffset()) > 20, "动画距离过小，无法验证接管");
            h.assert(app.filmAnimating, "按 → 后定位动画未开始");
            h.wheel(h.filmScroller(), { deltaY: 3, deltaMode: 1 });
            const takenOver = h.filmOffset();
            await h.wait(500);
            const final = h.filmOffset();
            h.assert(!app.filmAnimating, "滚轮接管后动画仍在播放");
            h.assert(Math.abs(final - takenOver) <= 2, `滚轮接管后位置继续漂移：${takenOver} → ${final}`);
            h.assert(Math.abs(final - target) > 2, `最终位置仍等于动画终点 ${target}，接管失败`);
        },
    },
    {
        id: "FILM-04",
        title: "隐藏再显示、行/列切换后显式定位当前项",
        async run(shell, h) {
            const { app } = shell;
            shell.setWindowSize(700, 850);
            await h.tick(120);
            h.assert(app.state.layout.layoutMode === "row", "700×850 未进入行布局");
            const targetIndex = 12;
            app.selectCard(app.state.cards[targetIndex].photo.id);
            await h.tick(60);
            h.filmScroller().scrollLeft = 0;
            await h.tick(60);

            await h.openMainMenu();
            h.click(h.menuItem("film"));
            await h.tick(120);
            h.assert(app.state.panes.film === false, "菜单未隐藏胶片栏");
            h.assert(document.querySelector(".pv-pane-file").hidden, "文件栏仍可见");

            await h.openMainMenu();
            h.click(h.menuItem("film"));
            await h.tick(160);
            h.assert(app.state.panes.film === true, "菜单未重新显示胶片栏");
            const expectedX = h.expectedCenter(targetIndex, "x");
            h.assert(Math.abs(h.filmOffset() - expectedX) <= 1, `重新显示后偏移 ${h.filmOffset()} ≠ ${expectedX}`);

            shell.setWindowSize(1200, 850);
            await h.tick(160);
            h.assert(app.state.layout.layoutMode === "col", "1200×850 未进入列布局");
            const expectedY = h.expectedCenter(targetIndex, "y");
            h.assert(Math.abs(h.filmOffset() - expectedY) <= 1, `换列后偏移 ${h.filmOffset()} ≠ ${expectedY}`);

            shell.setWindowSize(700, 850);
            await h.tick(160);
            const expectedBack = h.expectedCenter(targetIndex, "x");
            h.assert(Math.abs(h.filmOffset() - expectedBack) <= 1, `换回行后偏移 ${h.filmOffset()} ≠ ${expectedBack}`);
        },
    },
    {
        id: "FILM-04b",
        title: "纵向胶片栏滑块可拖动：列布局与手机横屏都按 y 轴更新偏移",
        async run(shell, h) {
            const { app } = shell;
            async function dragThumb(label) {
                const scroller = h.filmScroller();
                const rect = scroller.getBoundingClientRect();
                h.pointer(scroller, "pointerdown", rect.left + 6, rect.top + 6);
                await h.tick(30);
                const track = h.dom(".pv-film-scrollbar");
                h.assert(!track.hidden, `${label} 滚动条未出现`);
                const thumb = track.querySelector(".pv-film-scrollbar-thumb");
                const box = thumb.getBoundingClientRect();
                h.assert(box.height > 0, `${label} 滑块高度为 0`);
                const before = h.filmOffset();
                const x = box.left + box.width / 2;
                const y = box.top + box.height / 2;
                h.pointer(thumb, "pointerdown", x, y);
                h.pointer(thumb, "pointermove", x, y + 36);
                h.pointer(thumb, "pointerup", x, y + 36);
                await h.tick(60);
                h.assert(h.filmOffset() > before + 4, `${label} 拖动后偏移 ${h.filmOffset()} 未超过 ${before}`);
            }

            app.setLayoutModeOverride("col");
            shell.setWindowSize(1200, 850);
            app.openFolder("harbor", { target: app.state.cards[12]?.photo.id });
            await h.tick(160);
            h.assert(app.state.layout.film.axis === "y", "列布局胶片不是纵向");
            await dragThumb("列布局");

            app.setLayoutModeOverride("phone");
            shell.setSafeArea({ top: 0, right: 0, bottom: 0, left: 59 });
            shell.setWindowSize(844, 390);
            app.openFolder("harbor", { target: app.state.cards[12]?.photo.id });
            await h.tick(160);
            h.assert(app.state.layout.orientation === "landscape", "未进入手机横屏");
            h.assert(app.state.layout.film.axis === "y", "手机横屏胶片不是纵向");
            await dragThumb("手机横屏");
            shell.setSafeArea(null);
        },
    },
    {
        id: "FILM-05",
        title: "当前项高亮跟随锚点；已缓存细边；抖动徽标只在 shake=true",
        async run(shell, h) {
            const { app } = shell;
            h.assert(h.currentFilmKey() === h.cardByPhotoId(app.state.anchorId).key, "当前项高亮不在锚点上");

            h.dblclick(h.photoCanvas());
            await h.tick(80);
            await h.until(() => app.state.cached.has(app.state.displayedId), 6000);
            const currentCard = h.filmCardAt(h.cardIndex(app.state.displayedId));
            const cachedBorder = currentCard.querySelector(".pv-card-cached");
            h.assert(cachedBorder.hidden === false, "100% 且已缓存后未出现缓存细边");
            h.assert(cachedBorder.hidden === !app.state.cached.has(app.state.displayedId),
                "缓存细边可见性与已缓存状态不一致");

            for (const node of h.domAll(".pv-card", h.filmScroller())) {
                const key = node.dataset.key;
                const card = app.state.cards.find((entry) => entry.key === key);
                if (!card) continue;
                const badge = node.querySelector(".pv-card-badge");
                h.assert(badge.hidden === (card.photo.shake !== true),
                    `${card.displayName} 抖动徽标可见性 ${!badge.hidden} 与 shake=${card.photo.shake} 不符`);
            }

            // FILM-05：第二行与文件名同色粗体；0 号槽位按组级冲突显示清零叉或警示三角。
            const styleCard = currentCard;
            const dateStyle = getComputedStyle(styleCard.querySelector(".pv-card-date"));
            h.assert(dateStyle.fontWeight === "700", `第二行字重 ${dateStyle.fontWeight}`);
            h.assert(dateStyle.color === getComputedStyle(styleCard.querySelector(".pv-card-name")).color,
                "第二行颜色与文件名不一致");
            const zero = styleCard.querySelector('.pv-card-star[data-rating="0"]');
            if (app.state.toolbar.rating.conflict) {
                const warning = zero.querySelector(".pv-card-warning");
                h.assert(getComputedStyle(warning).fontSize === `${shell.t("film.card.star.fontSize")}px`,
                    `警示三角字号 ${getComputedStyle(warning).fontSize}`);
                h.assert(colorMatches(getComputedStyle(warning).color, shell.t("color.star.conflict")),
                    `警示三角颜色 ${getComputedStyle(warning).color}`);
            } else {
                h.assert(getComputedStyle(zero).fontSize === `${shell.t("film.card.clearGlyph.size")}px`,
                    `清零叉字号 ${getComputedStyle(zero).fontSize}`);
            }
            h.assert(styleCard.querySelector(".pv-card-date").textContent.length > 0, "第二行未显示日期或 --");
            const placeholder = styleCard.querySelector(".pv-card-placeholder");
            h.assert(placeholder !== null, "缩略图占位字形未渲染");
            h.assert(placeholder.hidden === true, "缩略图就绪后仍显示占位字形");
        },
    },
    {
        id: "FILM-05c",
        title: "小档卡片角标为金星+白色独立数字，冲突前置 icon.warning，右缘贴照片区",
        async run(shell, h) {
            const { app } = shell;
            app.updateSettings({ layoutMode: "phone" });
            shell.setSafeArea({ top: 0, right: 59, bottom: 0, left: 0 });
            shell.setWindowSize(844, 390);
            await h.tick(120);
            const t = (name) => shell.t(name);
            const rated = app.state.cards.find((card) => displayStars(card.photo) > 0 && !hasRatingConflict(card));
            h.assert(rated, "harbor 中找不到可显示评分的小档卡片");
            app.openFolder("harbor", { target: rated.photo.id });
            await h.tick(160);
            const node = h.filmCardByKey(rated.key);
            h.assert(node && node.classList.contains("pv-card-small"), "目标未渲染为小档卡片");
            const badge = node.querySelector(".pv-card-small-badge");
            const star = badge.querySelector(".pv-card-small-star");
            const number = badge.querySelector(".pv-card-small-number");
            const warning = badge.querySelector(".pv-card-small-warning");
            h.assert(!badge.hidden, "有评分时小档角标未显示");
            h.assert(star.textContent.codePointAt(0) === parseInt(t("icon.starFilled").slice(2), 16),
                "小档角标金星不是 icon.starFilled");
            h.assert(colorMatches(getComputedStyle(star).color, t("color.star.on")), "小档金星颜色不符");
            h.assert(number.textContent === String(displayStars(rated.photo)), "小档角标数字不独立或不符");
            h.assert(colorMatches(getComputedStyle(number).color, "#FFFFFF"), `小档数字颜色 ${getComputedStyle(number).color}`);
            h.assert(warning.hidden, "无冲突时仍显示警示三角");
            const badgeRect = badge.getBoundingClientRect();
            const imageRect = node.querySelector(".pv-card-image").getBoundingClientRect();
            h.assert(Math.abs(badgeRect.right - imageRect.right) <= 1, `角标未贴照片区右缘：${badgeRect.right} ≠ ${imageRect.right}`);
            h.assert(Math.abs(badgeRect.bottom - imageRect.bottom) <= 1, `角标未贴照片区下缘：${badgeRect.bottom} ≠ ${imageRect.bottom}`);
            // 静态导出只按一位数字估宽，不作为实际冲突/字体宽度的精确比对；
            // DOM 只核对由核心 badge frame 给出的外框与内容，不在这里反推新宽度。
            h.assert(badgeRect.width >= t("film.card.small.badge.fontSize"),
                `角标宽度 ${badgeRect.width} 小于字形令牌`);

            const conflict = app.state.cards.find((card) => hasRatingConflict(card));
            if (conflict) {
                app.openFolder("harbor", { target: conflict.photo.id });
                await h.tick(160);
                const conflictNode = h.filmCardByKey(conflict.key);
                const conflictBadge = conflictNode?.querySelector(".pv-card-small-badge");
                h.assert(conflictBadge && !conflictBadge.hidden, "冲突小档卡片未显示角标");
                const conflictWarning = conflictBadge.querySelector(".pv-card-small-warning");
                h.assert(conflictWarning && !conflictWarning.hidden, "冲突角标未显示警示三角");
                h.assert(conflictWarning.textContent.codePointAt(0) === parseInt(t("icon.warning").slice(2), 16),
                    "冲突角标警示三角不是 icon.warning");
            }
        },
    },
    {
        id: "FILM-05b",
        title: "空列表在胶片栏留空，不写「无匹配照片」",
        async run(shell, h) {
            const { app } = shell;
            app.openFolder("empty");
            await h.tick(120);
            h.assert(app.state.count === 0, `空文件夹计数 ${app.state.count} ≠ 0`);
            h.assert(h.dom(".pv-film-scroll").textContent.trim() === "", "空列表仍显示占位文字");
            h.assert(document.querySelector(".pv-film-empty") === null, "空列表仍存在占位元素");
        },
    },
    {
        id: "FILM-06",
        title: "改星落出筛选时暂留，切走后移除，计数含暂留项",
        async run(shell, h) {
            const { app } = shell;
            await h.openFilter("rating");
            h.click(h.menuItem("3"));
            await h.tick(80);
            h.assert(app.state.ratingFilters.includes("3"), "三星筛选未应用");
            h.key("Escape");
            await h.tick(40);
            const list = app.state.cards;
            let index = list.findIndex((card, position) => card.photo.rating === 3 && position < list.length - 1);
            if (index < 0) index = 0;
            const card = list[index];
            app.selectCard(card.photo.id);
            await h.tick(60);
            const countBefore = app.state.count;
            const beforeOffset = h.filmOffset();

            h.click(h.ratingButton(2));
            await h.until(() => app.state.photo.rating === 2, 4000);
            await h.tick(80);
            h.assert(app.state.displayedId === card.photo.id, "改星切换了当前照片");
            h.assert(app.state.cards.some((entry) => entry.key === card.key), "暂留项立即被移出列表");
            h.assert(app.state.count === countBefore, `暂留项未计入计数：${app.state.count} ≠ ${countBefore}`);
            h.assert(h.filmOffset() === beforeOffset, "改星改变了列表偏移");

            h.key("ArrowRight");
            await h.tick(120);
            h.assert(!app.state.cards.some((entry) => entry.key === card.key), "切走后暂留项仍在列表");
            h.assert(app.state.count === countBefore - 1, `切走后计数 ${app.state.count} ≠ ${countBefore - 1}`);
        },
    },
    {
        id: "FILTER-01",
        title: "宽形态三段合体 / 单胶囊、无计数占位、AI 开关独立且标准版无空槽",
        async run(shell, h) {
            const { app } = shell;
            const t = (name) => shell.t(name);
            const button = t("shell.glass.button.size");

            app.updateSettings({ layoutMode: "row" });
            await h.tick(120);
            const row = app.state.layout.filter;
            h.assert(row.kind === "segments", `行布局未使用三段合体：${row.kind}`);
            h.assert(row.control.h === button, `三段合体高 ${row.control.h}`);
            h.assert(row.control.w === 3 * t("filter.control.width"),
                `三段合体自然宽 ${row.control.w} ≠ ${3 * t("filter.control.width")}`);
            for (const name of ["sort", "order", "rating"]) {
                h.assert(row.controls[name].w >= t("filter.control.width"), `${name} 段宽 ${row.controls[name].w}`);
            }
            const segments = h.domAll(".pv-popup.is-segment");
            h.assert(segments.length === 3 && segments.every((node) => !node.hidden), "行布局没有三段可见入口");
            h.assert(h.dom(".pv-filter-entry").hidden, "三段模式下仍显示单胶囊按钮");
            h.assert(Math.abs(h.dom(".pv-filter-capsule").getBoundingClientRect().width - row.control.w) <= 1,
                "玻璃胶囊宽与三段组合宽不一致");
            h.assert(document.querySelector(".pv-filter-count") === null, "筛选控件仍有计数元素");
            h.assert(!("count" in row) && !("filter.count" in app.state.layout.nodes), "筛选几何仍预留计数");

            const toggle = document.querySelector(".pv-filter-toggle");
            if (app.capabilities.similarity) {
                h.assert(toggle !== null, "AI 版缺少相似聚类开关");
                h.assert(toggle.getAttribute("role") === "switch", "相似聚类开关缺少 switch 语义");
                h.assert(row.toggle && row.toggle.w === t("filter.toggle.width") && row.toggle.h === button,
                    "相似聚类开关不是独立 90×44");
            } else {
                h.assert(toggle === null, "标准版仍创建相似聚类开关");
                h.assert(row.toggle === null, "标准版仍留相似开关几何");
            }

            app.updateSettings({ layoutMode: "col" });
            await h.tick(120);
            const col = app.state.layout.filter;
            h.assert(col.kind === "capsule", `96 宽列布局未回退单胶囊：${col.kind}`);
            h.assert(col.composite.w === t("film.card.width"), `列胶囊宽 ${col.composite.w}`);
            h.assert(col.entry !== null && col.entry.w === t("film.card.width"), "单胶囊入口宽不是 96");
            h.assert(h.domAll(".pv-popup.is-segment").every((node) => node.hidden), "单胶囊下三段仍可见");
            h.assert(!h.dom(".pv-filter-entry").hidden, "单胶囊入口未显示");
            const entryRect = h.dom(".pv-filter-entry").getBoundingClientRect();
            const colCapsule = h.dom(".pv-filter-capsule").getBoundingClientRect();
            h.assert(entryRect.left >= colCapsule.left - 0.5 && entryRect.right <= colCapsule.right + 0.5,
                "列布局入口越出胶囊");
            app.updateSettings({ layoutMode: "auto" });
            await h.tick(80);
        },
    },
    {
        id: "FILTER-03",
        title: "星级筛选取值与菜单顺序固定，多选就地更新且菜单打开期间 → 不切图",
        async run(shell, h) {
            const { app } = shell;
            await h.openFilter();
            const ratingItems = h.domAll('.pv-menu .pv-menu-item[data-group="rating"]');
            h.deepEqual(ratingItems.map((node) => node.querySelector(".pv-menu-label").textContent),
                ["全部", "无星级", "一星", "二星", "三星", "四星", "五星", "已拒绝", "星级冲突"],
                "星级筛选项或顺序不符");
            h.assert(ratingItems.every((node) => node.getAttribute("role") === "menuitemcheckbox"),
                "星级筛选项不是多选语义");
            const checked = h.domAll(".pv-menu .pv-menu-item").filter((node) => node.getAttribute("aria-checked") === "true");
            h.assert(checked.length === 3, `三个分组各应有一项勾选，实际 ${checked.length}`);
            const groups = new Set(h.domAll(".pv-menu .pv-menu-item").map((node) => node.dataset.group));
            h.deepEqual([...groups], ["sort", "order", "rating"], "筛选菜单不是三分组");

            const displayed = app.state.displayedId;
            h.key("ArrowRight");
            await h.tick(80);
            h.assert(app.state.displayedId === displayed, "菜单打开期间 → 键切换了照片");
            h.assert(h.menu() !== null, "菜单因 → 键被关闭");

            h.key("Escape");
            await h.tick();
            h.assert(h.menu() === null, "Esc 未关闭筛选菜单");
            h.deepEqual(app.state.ratingFilters, ["all"], "Esc 改变了星级筛选");

            await h.openFilter();
            h.click(h.menuItem("5"));
            await h.tick(80);
            h.deepEqual(app.state.ratingFilters, ["5"], "选中即应用失败");
            h.assert(h.menu() !== null, "选中后筛选菜单被关闭");
            h.assert(h.menuItem("5").getAttribute("aria-checked") === "true", "选中项 ARIA 未原地更新");
            h.assert(h.menuItem("all").getAttribute("aria-checked") === "false", "全部项 ARIA 未原地取消");
            h.assert(h.menu().contains(document.activeElement), "选中后焦点离开了菜单");
            h.assert(document.querySelector(".pv-filter-count") === null, "筛选控件仍有计数元素");
        },
    },
    {
        id: "FILTER-06",
        title: "相似聚类开关与筛选胶囊同一按钮材质，勾选态取既有令牌",
        async run(shell, h) {
            const { app } = shell;
            const t = (name) => shell.t(name);
            if (!app.capabilities.similarity) {
                h.assert(document.querySelector(".pv-filter-toggle") === null, "标准版仍创建相似聚类开关");
                h.assert(app.setSimilarityExpanded(true) === false, "标准版响应了相似展开请求");
                h.assert(app.state.similarity.visible === false, "标准版恢复了相似面板");
                return;
            }
            const toggle = h.dom(".pv-filter-toggle");
            const style = () => getComputedStyle(toggle);
            h.assert(style().backgroundColor !== "rgba(0, 0, 0, 0)", "未勾选底色没有按钮材质");
            h.assert(style().borderTopWidth === "1px" && colorMatches(style().borderTopColor, "#00000000"),
                `未勾选常态仍画描边：${style().borderTopColor}`);
            h.assert(style().backdropFilter !== "none", "相似开关缺少玻璃材质");
            h.assert(style().borderTopLeftRadius === `${t("shell.glass.button.size") / 2}px`,
                `相似开关圆角 ${style().borderTopLeftRadius}`);
            h.assert(toggle.getAttribute("aria-checked") === String(Boolean(app.settings.similarityPanelExpanded)), "勾选态与设置不一致");
            h.click(toggle);
            await h.tick(80);
            h.assert(app.settings.similarityPanelExpanded === true, "点击未勾选相似聚类");
            h.assert(colorMatches(style().backgroundColor, t("color.filter.toggle.checked.background")),
                `勾选态底色 ${style().backgroundColor}`);
            h.assert(colorMatches(style().color, t("color.filter.toggle.checked.foreground")),
                `勾选态文字色 ${style().color}`);
            h.assert(colorMatches(style().borderTopColor, t("color.filter.toggle.checked.border")),
                `勾选态描边 ${style().borderTopColor}`);
            h.assert(toggle.getAttribute("aria-checked") === "true", "勾选态未更新为 true");
            h.click(toggle);
            await h.tick(80);
            h.assert(app.settings.similarityPanelExpanded === false, "再次点击未取消相似聚类");
        },
    },
    {
        id: "FILTER-08",
        title: "筛选胶囊 / 三段外观与弹层：按钮材质、胶囊形状态层、贴入口下方居中、当前项画勾",
        async run(shell, h) {
            const { app } = shell;
            const t = (name) => shell.t(name);
            const capsule = h.dom(".pv-filter-capsule");
            const capsuleStyle = getComputedStyle(capsule);
            h.assert(capsuleStyle.backdropFilter !== "none", "筛选胶囊缺少 backdrop-filter");
            h.assert(capsuleStyle.borderTopLeftRadius === `${t("shell.glass.button.size") / 2}px`,
                `筛选胶囊圆角 ${capsuleStyle.borderTopLeftRadius}`);
            const entry = h.dom(".pv-filter-entry");
            h.assert(getComputedStyle(entry).backgroundColor === "rgba(0, 0, 0, 0)", "单胶囊按钮重复铺底");
            h.assert(getComputedStyle(entry).borderTopWidth === "0px", "单胶囊按钮常态画描边");
            h.assert(colorMatches(capsuleStyle.backgroundColor, t("color.toolbar.button.tint"), t("shell.glass.button.tint.opacity")),
                `筛选胶囊材质 ${capsuleStyle.backgroundColor} 与命令按钮不同`);
            const entryRect = entry.getBoundingClientRect();
            const entryState = getComputedStyle(entry, "::after");
            const stateInset = parseFloat(entryState.top);
            h.assert(Math.abs(parseFloat(entryState.borderTopLeftRadius) - (t("shell.glass.button.size") / 2 - stateInset)) <= 0.5
                && Math.abs(entryRect.height - 2 * stateInset - 2 * parseFloat(entryState.borderTopLeftRadius)) <= 0.5,
                `单胶囊状态层不是同心胶囊形：圆角 ${entryState.borderTopLeftRadius}、内缩 ${entryState.top}`);
            const entryHover = computedState(entry, ".pv-filter-entry.is-capsule:hover", { pseudo: "::after" });
            const entryPressed = computedState(entry, ".pv-filter-entry.is-capsule:active", { pseudo: "::after", state: ":active" });
            h.assert(entryHover !== null && colorMatches(entryHover.backgroundColor, t("color.menu.item.hover")),
                `单胶囊悬停 ${entryHover?.backgroundColor}`);
            h.assert(entryPressed !== null && colorMatches(entryPressed.backgroundColor, t("color.menu.item.pressed")),
                `单胶囊按下 ${entryPressed?.backgroundColor}`);

            await h.openFilter();
            const rect = entry.getBoundingClientRect();
            let menu = h.menu();
            let menuRect = menu.getBoundingClientRect();
            const menuStyle = getComputedStyle(menu);
            const hostRect = h.dom(".pv-window").getBoundingClientRect();
            let expectedLeft = Math.min(Math.max(4, rect.left + rect.width / 2 - menuRect.width / 2), hostRect.width - menuRect.width - 4);
            h.assert(Math.abs(menuRect.left - expectedLeft) <= 1,
                `弹层未以单胶囊水平居中并夹在窗口内：${menuRect.left} ≠ ${expectedLeft}`);
            h.assert(menuRect.top >= rect.bottom - 1, `弹层覆盖入口：top ${menuRect.top} < 入口底 ${rect.bottom}`);
            h.assert(menuRect.top - rect.bottom <= 1, `弹层未贴在入口正下方：间隙 ${menuRect.top - rect.bottom}`);
            h.assert(menuRect.width >= rect.width - 0.5, `弹层最小宽不足：${menuRect.width} < ${rect.width}`);
            h.assert(colorMatches(menuStyle.backgroundColor, t("color.popup.background")), `弹层底色 ${menuStyle.backgroundColor}`);
            h.assert(colorMatches(menuStyle.borderTopColor, t("color.popup.border")), `弹层描边 ${menuStyle.borderTopColor}`);
            h.assert(menuStyle.boxShadow === "none", `弹层仍有阴影：${menuStyle.boxShadow}`);
            h.assert(menuStyle.borderTopLeftRadius === `${t("popup.cornerRadius")}px`, `弹层圆角 ${menuStyle.borderTopLeftRadius}`);

            const items = h.domAll(".pv-menu .pv-menu-item");
            const selected = items.find((node) => node.getAttribute("aria-checked") === "true");
            h.assert(selected !== undefined, "下拉没有当前项");
            h.assert(menuStyle.fontSize === `${t("dropdown.fontSize")}px`, `下拉字号 ${menuStyle.fontSize}`);
            const itemStyle = getComputedStyle(selected);
            h.assert(itemStyle.paddingLeft === `${t("dropdown.item.paddingX")}px`, "下拉项左内边距不符");
            const checkGlyph = String.fromCodePoint(parseInt(t("icon.check").slice(2), 16));
            for (const item of items) {
                const mark = item.querySelector(".pv-menu-mark");
                const checked = item.getAttribute("aria-checked") === "true";
                h.assert(mark !== null, `下拉项「${item.textContent}」未让出勾选列`);
                h.assert(mark.textContent === (checked ? checkGlyph : ""), `下拉项「${item.textContent}」勾号与选中态不符`);
                const expected = item.classList.contains("is-focused") ? t("color.menu.item.hover") : "#00000000";
                h.assert(colorMatches(getComputedStyle(item).backgroundColor, expected),
                    `下拉项「${item.textContent}」底色 ${getComputedStyle(item).backgroundColor}，当前项不得整行强调`);
            }

            h.key("Escape");
            await h.tick(40);
            h.assert(h.menu() === null, "Esc 未关闭筛选菜单");
            h.assert(app.state.sortKey === "taken", "Esc 改变了排序");

            app.updateSettings({ layoutMode: "row" });
            await h.tick(120);
            const row = app.state.layout.filter;
            h.assert(row.kind === "segments", "行布局没有三段合体");
            const sortButton = h.dom('.pv-popup.is-segment[data-group="sort"]');
            const sortRect = sortButton.getBoundingClientRect();
            h.assert(Math.abs(sortRect.width - row.controls.sort.w) <= 1 && Math.abs(sortRect.height - 44) <= 1,
                `排序段 DOM ${sortRect.width}×${sortRect.height}`);
            h.assert(getComputedStyle(sortButton).backgroundColor === "rgba(0, 0, 0, 0)", "排序段自带底色");
            h.assert(getComputedStyle(sortButton, "::before").content === "none", "首段不应有段间分隔线");
            const orderButton = h.dom('.pv-popup.is-segment[data-group="order"]');
            const orderDivider = getComputedStyle(orderButton, "::before");
            h.assert(orderDivider.width === "1px" && colorMatches(orderDivider.backgroundColor, t("color.dropdown.border")),
                `段间分隔线 ${orderDivider.width}/${orderDivider.backgroundColor}`);
            h.assert(colorMatches(getComputedStyle(orderButton).color, t("color.text.primary")), "段文字色不是主文字色");

            h.click(orderButton);
            await h.tick(40);
            menu = h.menu();
            h.assert(menu !== null, "点击排序方向段未弹出菜单");
            h.deepEqual(h.domAll(".pv-menu .pv-menu-item").map((node) => node.dataset.group), ["order", "order"],
                "段菜单不是单组单选");
            const orderRect = orderButton.getBoundingClientRect();
            const orderMenuRect = menu.getBoundingClientRect();
            expectedLeft = Math.min(Math.max(4, orderRect.left + orderRect.width / 2 - orderMenuRect.width / 2),
                hostRect.width - orderMenuRect.width - 4);
            h.assert(Math.abs(orderMenuRect.left - expectedLeft) <= 1, "段菜单未以该段水平居中");
            h.assert(orderMenuRect.top >= orderRect.bottom - 1, "段菜单覆盖入口");
            h.key("Escape");
            await h.tick(40);
            app.updateSettings({ layoutMode: "auto" });
            await h.tick(80);
        },
    },
    {
        id: "FILTER-08b",
        title: "键盘 ↓ 从筛选入口打开菜单，上下键移动、回车确认后就地保持展开",
        async run(shell, h) {
            const { app } = shell;
            const entry = h.dom(".pv-filter-entry");
            entry.focus();
            entry.dispatchEvent(new KeyboardEvent("keydown", { key: "ArrowDown", code: "ArrowDown", bubbles: true, cancelable: true }));
            await h.tick();
            h.assert(h.menu() !== null, "↓ 未从筛选入口打开菜单");
            h.key("ArrowDown");
            h.key("ArrowDown");
            h.key("ArrowDown");
            await h.tick(20);
            h.key("Enter");
            await h.tick(80);
            h.assert(app.state.sortOrder === "desc", "回车未应用到下一项");
            h.assert(h.menu() !== null, "回车后筛选菜单被关闭");
            h.assert(document.activeElement?.classList.contains("pv-menu-item"), "回车后焦点离开菜单项");
            h.key("Escape");
            await h.tick(40);
            h.assert(h.menu() === null, "Esc 未关闭筛选菜单");
            h.assert(document.activeElement === entry, "Esc 后焦点未回到筛选入口");
        },
    },
    {
        id: "FILTER-08c",
        title: "筛选菜单选择后就地更新并保持展开/焦点/滚动，入口失效时关闭，主菜单仍选择即关",
        async run(shell, h) {
            const { app } = shell;

            app.updateSettings({ layoutMode: "row" });
            await h.tick(120);
            await h.openFilter("sort");
            const sortMenu = h.menu();
            const sortButton = h.dom('.pv-popup.is-segment[data-group="sort"]');
            const beforeSort = app.state.sortKey;
            h.key("ArrowDown");
            await h.tick(20);
            h.key("Enter");
            await h.tick(60);
            h.assert(app.state.sortKey !== beforeSort, "段菜单回车未应用排序");
            h.assert(h.menu() === sortMenu, "段菜单选择后被重建或关闭");
            h.assert(sortMenu.contains(document.activeElement), "段菜单选择后焦点离开菜单");
            h.key("Escape");
            await h.tick(40);
            h.assert(document.activeElement === sortButton, "Esc 后焦点未回到排序段");

            app.updateSettings({ layoutMode: "col" });
            shell.setWindowSize(700, 360);
            app.setSort("taken", "asc");
            app.setRatingFilters(["all"]);
            await h.tick(120);
            await h.openFilter();
            const list = h.menu();
            list.scrollTop = 24;
            const scrollBefore = list.scrollTop;
            for (let step = 0; step < 8; step += 1) h.key("ArrowDown");
            await h.tick(20);
            h.key("Enter");
            await h.tick(60);
            h.assert(h.menu() === list, "星级菜单选择后被重建或关闭");
            h.assert(list.scrollTop === scrollBefore, `选择后菜单滚动位置 ${list.scrollTop} ≠ ${scrollBefore}`);
            h.assert(document.activeElement?.dataset.id === "3",
                `选择后焦点未留在当前菜单项：${document.activeElement?.className ?? "无"}/${document.activeElement?.dataset?.id ?? "-"}`);
            h.assert(h.menuItem("3").getAttribute("aria-checked") === "true", "三星勾选未原地更新");
            h.assert(h.menuItem("all").getAttribute("aria-checked") === "false", "全部未随多选取消");

            h.key("ArrowDown");
            await h.tick(20);
            h.key("Enter");
            await h.tick(60);
            h.deepEqual(app.state.ratingFilters, ["3", "4"], "多选并集未生效");
            h.assert(h.menuItem("4").getAttribute("aria-checked") === "true", "四星勾选未原地更新");

            h.click(h.menuItem("all"));
            await h.tick(60);
            h.deepEqual(app.state.ratingFilters, ["all"], "点全部未清空条件");
            h.assert(h.menu() === list, "点全部关闭了菜单");
            h.assert(h.menuItem("3").getAttribute("aria-checked") === "false", "点全部后旧条件仍勾选");

            h.click(h.menuItem("3"));
            h.click(h.menuItem("4"));
            await h.tick(60);
            h.deepEqual(app.state.ratingFilters, ["3", "4"], "多选条件未按并集保留");
            h.click(h.menuItem("3"));
            h.click(h.menuItem("4"));
            await h.tick(60);
            h.deepEqual(app.state.ratingFilters, ["all"], "取消最后一项未回到全部");
            h.assert(h.menuItem("all").getAttribute("aria-checked") === "true", "回到全部后未勾选全部");

            app.updateSettings({ layoutMode: "row" });
            await h.tick(120);
            h.assert(h.menu() === null, "筛选入口移走后菜单仍展开");
            h.assert(app.state.dropdownOpen === false, "入口移走后下拉状态未复位");

            app.updateSettings({ layoutMode: "auto" });
            shell.setWindowSize(1200, 850);
            await h.tick(120);
            await h.openMainMenu();
            h.click(h.menuItem("film"));
            await h.tick(80);
            h.assert(h.menu() === null, "主图菜单选择后未关闭");
        },
    },
    {
        id: "FILTER-10",
        title: "空结果胶片栏留空，筛选条件保留",
        async run(shell, h) {
            const { app } = shell;
            app.updateSettings({ sameNameAsOne: false });
            await h.tick(60);
            await h.openFilter();
            h.click(h.menuItem("rejected"));
            await h.tick(100);
            h.assert(h.menu() !== null, "选中后筛选菜单被关闭");
            h.key("Escape");
            await h.tick(40);
            app.openFolder("night");
            await h.tick(120);
            h.assert(app.state.count === 0, `空结果计数 ${app.state.count} ≠ 0`);
            h.assert(h.filmScroller().textContent.trim() === "", `胶片栏空态有文字：${h.filmScroller().textContent}`);
            h.assert(document.querySelector(".pv-film-empty") === null, "胶片栏仍有空状态元素");
            h.assert(!h.dom(".pv-filter-entry").disabled, "空结果时筛选入口被禁用");
            await h.openFilter();
            const checked = h.domAll('.pv-menu .pv-menu-item[data-group="rating"]').find((node) => node.getAttribute("aria-checked") === "true");
            h.assert(checked && checked.dataset.id === "rejected", "空结果时星级筛选条件未保留");
        },
    },
    {
        id: "MENU-01",
        title: "主图右键菜单的项、顺序、分组与勾选",
        async run(shell, h) {
            const { app } = shell;
            await h.openMainMenu();
            const nodes = [...h.menu().children].map((node) => (
                node.classList.contains("pv-menu-sep") ? "---" : node.dataset.id
            ));
            h.deepEqual(nodes, ["open-file", "open-folder", "---", "film", "analysis", "control", "---", "enhance"],
                "菜单项或分组顺序不符");
            const labels = h.domAll(".pv-menu .pv-menu-item").map((node) => node.querySelector(".pv-menu-label").textContent);
            h.deepEqual(labels, ["打开文件…", "打开文件夹…", "胶片栏", "分析栏", "控制栏", "雾天增强"], "菜单文案不符");
            // MENU-06：图标角色固定，增强复用命令图标；DOM 顺序为「勾选列 → 图标 → 文字 → 快捷键」。
            h.deepEqual(app.menuModel().map((item) => item.icon),
                ["icon.menu.openFile", "icon.menu.openFolder", "icon.menu.filmstrip", "icon.menu.analysis",
                    "icon.menu.control", "icon.command.toggleEnhance"], "菜单图标角色不符");
            const rows = h.domAll(".pv-menu .pv-menu-item");
            h.deepEqual(rows.map((row) => [...row.children].map((child) => child.className)),
                rows.map(() => ["pv-menu-mark", "pv-icon pv-menu-icon", "pv-menu-label", "pv-menu-shortcut"]),
                "菜单项列顺序不是「勾选列 → 图标 → 文字 → 快捷键」");
            h.deepEqual(rows.map((row) => row.querySelector(".pv-menu-icon").textContent.codePointAt(0)),
                app.menuModel().map((item) => parseInt(shell.t(item.icon).slice(2), 16)), "菜单项图标字形不符");
            h.assert(h.menuItem("film").getAttribute("aria-checked") === "true", "胶片栏勾选未反映可见性");
            h.assert(h.menuItem("control").getAttribute("aria-checked") === "true", "控制栏勾选未反映可见性");
            h.assert(h.menuItem("analysis").getAttribute("aria-checked") === "false", "分析栏勾选未反映可见性");
            h.assert(app.state.photo !== null, "菜单打开改变了当前照片");
        },
    },
    {
        id: "MENU-02",
        title: "菜单动作与按钮同语义、隐藏文件栏扩大主图、Esc 不穿透",
        async run(shell, h) {
            const { app } = shell;
            const photoWidth = app.state.layout.panes.fitRect.w;
            await h.openMainMenu();
            h.click(h.menuItem("film"));
            await h.tick(120);
            h.assert(app.state.panes.film === false, "菜单未隐藏文件栏");
            h.assert(h.dom(".pv-pane-file").hidden, "文件栏节点仍可见");
            h.assert(app.state.layout.panes.fitRect.w > photoWidth, "隐藏文件栏后适应区未扩大");

            await h.openMainMenu();
            h.assert(h.menuItem("film").getAttribute("aria-checked") === "false", "重新弹出未读取最新勾选");
            h.click(h.menuItem("film"));
            await h.tick(120);
            h.assert(app.state.panes.film === true, "菜单未恢复文件栏");

            await h.openMainMenu();
            const displayed = app.state.displayedId;
            const viewport = JSON.stringify(app.state.viewport);
            h.key("Escape");
            await h.tick();
            h.assert(h.menu() === null, "Esc 未关闭主图菜单");
            h.assert(app.state.displayedId === displayed, "Esc 穿透到底层照片");
            h.assert(JSON.stringify(app.state.viewport) === viewport, "Esc 改变了主图视口");
        },
    },
    {
        id: "MENU-03",
        title: "自绘菜单外观：弹层无阴影、项高 31、整行悬停、勾选列共享、快捷键右对齐",
        async run(shell, h) {
            const t = (name) => shell.t(name);
            const windowEl = h.dom(".pv-window");
            const canvas = h.photoCanvas();
            const photoRect = canvas.getBoundingClientRect();
            const windowRect = windowEl.getBoundingClientRect();
            const pointerX = photoRect.left + photoRect.width / 2;
            const pointerY = photoRect.top + photoRect.height / 2;
            h.contextmenu(canvas, pointerX, pointerY);
            await h.tick(20);

            const menu = h.menu();
            const menuRect = menu.getBoundingClientRect();
            const menuStyle = getComputedStyle(menu);
            h.assert(Math.abs(menuRect.left - (pointerX - windowRect.left)) <= 1,
                `右键菜单位置 ${menuRect.left - windowRect.left} ≠ 指针 ${pointerX - windowRect.left}`);
            h.assert(Math.abs(menuRect.top - (pointerY - windowRect.top)) <= 1,
                `右键菜单位置 ${menuRect.top - windowRect.top} ≠ 指针 ${pointerY - windowRect.top}`);
            h.assert(colorMatches(menuStyle.backgroundColor, t("color.popup.background")), `弹层底色 ${menuStyle.backgroundColor}`);
            h.assert(colorMatches(menuStyle.borderTopColor, t("color.popup.border")), `弹层描边 ${menuStyle.borderTopColor}`);
            h.assert(menuStyle.boxShadow === "none", `弹层仍有阴影：${menuStyle.boxShadow}`);
            h.assert(menuStyle.borderTopLeftRadius === `${t("popup.cornerRadius")}px`, `弹层圆角 ${menuStyle.borderTopLeftRadius}`);
            h.assert(menuStyle.fontSize === `${t("menu.item.fontSize")}px`, `菜单字号 ${menuStyle.fontSize}`);

            const items = h.domAll(".pv-menu .pv-menu-item");
            h.assert(items.every((node) => node.offsetHeight === t("menu.item.height")),
                `菜单项高 ${items.map((node) => node.offsetHeight).join(",")} ≠ ${t("menu.item.height")}`);
            const itemStyle = getComputedStyle(items[0]);
            h.assert(itemStyle.paddingLeft === `${t("menu.item.paddingX")}px`, `菜单项左内边距 ${itemStyle.paddingLeft}`);
            h.assert(itemStyle.paddingRight === `${t("menu.item.paddingX")}px`, `菜单项右内边距 ${itemStyle.paddingRight}`);

            // 勾选列：菜单中有勾选项时所有项都让出 menu.check.size + menu.check.gap。
            h.assert(menu.classList.contains("has-check"), "含勾选项的菜单未启用勾选列");
            for (const item of items) {
                const mark = item.querySelector(".pv-menu-mark");
                h.assert(mark !== null, `菜单项 ${item.dataset.id} 未让出勾选列`);
                h.assert(mark.offsetWidth === t("menu.check.size"), `勾选列宽 ${mark.offsetWidth}`);
                h.assert(getComputedStyle(mark).marginRight === `${t("menu.check.gap")}px`, "勾选列间距不符");
            }
            const checkedMark = h.menuItem("film").querySelector(".pv-menu-mark");
            h.assert(checkedMark.textContent.codePointAt(0) === parseInt(t("icon.check").slice(2), 16),
                `勾选字形不是 icon.check：U+${checkedMark.textContent.codePointAt(0).toString(16)}`);
            h.assert(h.menuItem("analysis").querySelector(".pv-menu-mark").textContent === "", "未勾选项仍画勾号");

            // MENU-06：命令图标占勾选列右侧 menu.icon.size 列、字号 menu.icon.fontSize、与文字间距 menu.icon.gap。
            for (const item of items) {
                const glyph = item.querySelector(".pv-menu-icon");
                h.assert(glyph !== null, `菜单项 ${item.dataset.id} 缺少命令图标`);
                h.assert(glyph.offsetWidth === t("menu.icon.size"), `图标列宽 ${glyph.offsetWidth} ≠ ${t("menu.icon.size")}`);
                const glyphStyle = getComputedStyle(glyph);
                h.assert(glyphStyle.marginRight === `${t("menu.icon.gap")}px`, `图标与文字间距 ${glyphStyle.marginRight}`);
                h.assert(glyphStyle.fontSize === `${t("menu.icon.fontSize")}px`, `图标字号 ${glyphStyle.fontSize}`);
                const textLeft = item.querySelector(".pv-menu-label").getBoundingClientRect().left;
                h.assert(Math.abs(textLeft - glyph.getBoundingClientRect().right - t("menu.icon.gap")) <= 0.5,
                    `图标与文字实际间距 ${textLeft - glyph.getBoundingClientRect().right}`);
            }
            // 勾选项保留图标：勾选列与图标列是两条独立的列。
            h.assert(h.menuItem("film").querySelector(".pv-menu-mark").textContent !== ""
                && h.menuItem("film").querySelector(".pv-menu-icon").textContent !== "", "勾选项丢了命令图标");

            // 快捷键右对齐、颜色取 color.menu.shortcut，与文字间距 ≥ menu.shortcut.gap。
            const nextItem = h.menuItem("analysis");
            const shortcut = nextItem.querySelector(".pv-menu-shortcut");
            const label = nextItem.querySelector(".pv-menu-label");
            const gap = shortcut.getBoundingClientRect().left - label.getBoundingClientRect().right;
            h.assert(gap >= t("menu.shortcut.gap") - 0.5, `快捷键与文字间距 ${gap} < ${t("menu.shortcut.gap")}`);
            h.assert(colorMatches(getComputedStyle(shortcut).color, t("color.menu.shortcut")),
                `快捷键颜色 ${getComputedStyle(shortcut).color}`);

            // 悬停整行直角高亮、文字不变色。
            const row = h.menuItem("enhance");
            const textColor = getComputedStyle(row).color;
            row.dispatchEvent(new PointerEvent("pointerenter", { bubbles: false }));
            await h.tick(20);
            h.assert(row.classList.contains("is-focused"), "悬停未高亮整行");
            h.assert(colorMatches(getComputedStyle(row).backgroundColor, t("color.menu.item.hover")),
                `悬停底色 ${getComputedStyle(row).backgroundColor}`);
            h.assert(getComputedStyle(row).color === textColor, "悬停改变了文字颜色");
            h.assert(getComputedStyle(row).borderTopLeftRadius === "0px", `悬停高亮不是直角：${getComputedStyle(row).borderTopLeftRadius}`);

            // 分隔线与禁用文字。
            const separator = h.dom(".pv-menu-sep");
            const separatorStyle = getComputedStyle(separator);
            h.assert(separatorStyle.height === "1px", `分隔线厚 ${separatorStyle.height}`);
            h.assert(separatorStyle.marginLeft === `${t("menu.separator.inset")}px`, `分隔线内缩 ${separatorStyle.marginLeft}`);
            h.assert(colorMatches(separatorStyle.backgroundColor, t("color.menu.separator")), "分隔线颜色不符");

            h.key("Escape");
            await h.tick(40);
            h.assert(h.menu() === null, "Esc 未关闭菜单");
        },
    },
    {
        id: "MENU-07",
        title: "无照片时打开与栏位可用、雾天增强置灰",
        async run(shell, h) {
            const { app } = shell;
            app.openFolder("empty");
            await h.tick(120);
            h.assert(app.state.photo === null, "空文件夹仍有当前照片");
            await h.openMainMenu();
            h.assert(h.menuItem("enhance").getAttribute("aria-disabled") === "true", "无照片时雾天增强未置灰");
            h.assert(h.menuItem("enhance").classList.contains("is-disabled"), "雾天增强缺少禁用样式");
            h.assert(h.menuItem("film").getAttribute("aria-disabled") === "false", "无照片时胶片栏被禁用");
            h.assert(h.menuItem("open-file").getAttribute("aria-disabled") === "false", "无照片时打开文件被禁用");
        },
    },
    {
        id: "MENU-05",
        title: "触屏长按胶片栏与相似卡片弹出卡片菜单，标题含完整文件名与时间，选择即评分",
        async run(shell, h) {
            const { app } = shell;
            const cards = app.state.cards.filter((card) => !card.photo.fault?.write);
            h.assert(cards.length > 1, "harbor 可写卡片不足");
            const target = cards[1];
            const node = h.filmCardAt(app.state.cards.indexOf(target));
            h.assert(node, "目标卡片未渲染");
            const rect = node.getBoundingClientRect();
            const x = rect.left + rect.width / 2;
            const y = rect.top + rect.height / 2;
            const displayed = app.state.displayedId;

            h.pointer(node, "pointerdown", x, y, { pointerType: "touch" });
            await h.wait(650);
            h.assert(h.menu() !== null, "胶片栏卡片长按未弹出卡片菜单");
            const header = h.dom(".pv-menu-header");
            h.assert(header.textContent.includes(target.displayName), `卡片菜单标题缺少文件名：${header.textContent}`);
            const detail = header.querySelector(".pv-menu-header-detail");
            h.assert(detail && /^\d{4}-\d{2}-\d{2} \d{2}:\d{2}:\d{2} [+-]\d{2}:\d{2}$/.test(detail.textContent),
                `卡片菜单时间格式不符：${detail ? detail.textContent : "缺失"}`);

            h.pointer(node, "pointerup", x, y, { pointerType: "touch" });
            h.click(node);
            await h.tick(80);
            h.assert(app.state.displayedId === displayed, "长按抬起触发了点选");

            h.click(h.menuItem("rate-3"));
            await h.until(() => target.photo.rating === 3, 4000);
            h.assert(h.menu() === null, "选择评分后菜单未关闭");

            // EDITION-02：标准版没有相似卡片，长按行为只在 AI 版验收。
            if (!app.capabilities.similarity) {
                h.assert(document.querySelector(".pv-filter-toggle") === null, "标准版仍创建相似聚类开关");
                return;
            }
            h.click(h.dom(".pv-filter-toggle"));
            await h.until(() => app.state.similarity.items.length > 0, 4000);
            await h.tick(80);
            const simCard = h.domAll(".pv-sim-item .pv-card")[0];
            const simRect = simCard.getBoundingClientRect();
            h.pointer(simCard, "pointerdown", simRect.left + simRect.width / 2, simRect.top + simRect.height / 2, { pointerType: "touch" });
            await h.wait(650);
            h.assert(h.menu() !== null, "相似卡片长按未弹出卡片菜单");
            h.assert(h.dom(".pv-menu-header-title").textContent.length > 0, "相似卡片菜单缺少标题");
            h.assert(h.menuItem("rate-0") !== null, "相似卡片菜单缺少无星级项");
            h.pointer(simCard, "pointerup", simRect.left + simRect.width / 2, simRect.top + simRect.height / 2, { pointerType: "touch" });
            h.key("Escape");
            await h.tick(40);
            h.assert(h.menu() === null, "Esc 未关闭卡片菜单");
        },
    },
    {
        id: "INPUT-01",
        title: "← → 翻页、F/Ctrl+0 适应、E 雾天增强、1–5/` 评分",
        async run(shell, h) {
            const { app } = shell;
            const start = app.state.displayedId;
            h.key("ArrowRight");
            await h.tick(80);
            h.assert(app.state.displayedId !== start, "→ 未翻页");
            const moved = app.state.displayedId;
            h.key("ArrowLeft");
            await h.tick(80);
            h.assert(app.state.displayedId === start, "← 未翻回");

            h.key("KeyF");
            await h.tick(60);
            h.assert(app.state.viewport.isFit === false, "F 未切换到放大");
            h.key("KeyF");
            await h.tick(60);
            h.assert(app.state.viewport.isFit === true, "F 未切回适应");
            h.key("KeyF");
            await h.tick(40);
            h.key("Digit0", { ctrl: true });
            await h.tick(60);
            h.assert(app.state.viewport.isFit === true, "Ctrl+0 未回到适应");
            // SETTINGS-08：Apple 键盘映射下 ⌘ 视同 Ctrl，同一手势仍命中「缩放适应」。
            app.updateSettings({ appleKeyboard: true });
            h.key("KeyF");
            await h.tick(40);
            h.key("Digit0", { meta: true });
            await h.tick(60);
            h.assert(app.state.viewport.isFit === true, "⌘+0 未回到适应");
            app.updateSettings({ appleKeyboard: false });
            await h.tick(40);

            h.key("KeyE");
            await h.tick(60);
            h.assert(app.state.session.enhance === true, "E 未开启雾天增强");
            h.key("KeyE");
            await h.tick(60);
            h.assert(app.state.session.enhance === false, "E 未关闭雾天增强");

            h.key("Digit3");
            await h.until(() => app.state.photo.rating === 3, 4000);
            h.key("Backquote");
            await h.until(() => app.state.photo.rating === 0, 4000);
            h.assert(app.state.displayedId !== moved, "评分切换了当前照片");
        },
    },
    {
        id: "INPUT-02",
        title: "菜单打开期间照片快捷键被抑制",
        async run(shell, h) {
            const { app } = shell;
            await h.openFilter("sort");
            const displayed = app.state.displayedId;
            h.key("ArrowRight");
            h.key("KeyE");
            await h.tick(80);
            h.assert(app.state.displayedId === displayed, "菜单打开期间 → 仍翻页");
            h.assert(app.state.session.enhance === false, "菜单打开期间 E 仍生效");
        },
    },
    {
        id: "INPUT-05",
        title: "按住重复只对可重复命令生效",
        async run(shell, h) {
            const { app } = shell;
            const cards = app.state.cards;
            app.selectCard(cards[1].photo.id);
            await h.tick(60);
            const first = app.state.displayedId;
            h.keyDown("ArrowRight");
            h.keyDown("ArrowRight", { repeat: true });
            await h.tick(100);
            h.keyUp("ArrowRight");
            h.assert(app.state.displayedId !== first, "→ 按住重复未生效");

            const rate = app.rate.bind(app);
            let calls = 0;
            app.rate = (...args) => {
                calls += 1;
                return rate(...args);
            };
            try {
                h.keyDown("Digit1");
                h.keyDown("Digit1", { repeat: true });
                h.keyDown("Digit1", { repeat: true });
                await h.tick(60);
                h.keyUp("Digit1");
            } finally {
                delete app.rate;
            }
            h.assert(calls === 1, `评分按住重复执行了 ${calls} 次（应为 1 次）`);
        },
    },
    {
        id: "INPUT-04",
        title: "离散滚轮一格一次翻页，精确滚动不翻页，中键交换",
        async run(shell, h) {
            const { app } = shell;
            const canvas = h.photoCanvas();
            const before = app.state.displayedId;
            h.wheel(canvas, { deltaY: 3, deltaMode: 1 });
            await h.tick(100);
            h.assert(app.state.displayedId !== before, "离散滚轮未翻页");

            const afterWheel = app.state.displayedId;
            h.wheel(canvas, { deltaY: 40, deltaMode: 0 });
            await h.tick(80);
            h.assert(app.state.displayedId === afterWheel, "精确滚动被当成翻页");

            h.key("ArrowRight");
            await h.tick(100);
            const current = app.state.displayedId;
            const previous = afterWheel;
            h.pointer(canvas, "pointerdown", 10, 10, { button: 1 });
            await h.tick(100);
            h.assert(app.state.displayedId !== current, "中键未切换上一张");
            h.assert(app.state.displayedId === previous, "中键未回到上一张（History）");
        },
    },
    {
        id: "INPUT-10",
        title: "空文件夹不是首屏，主图与其他分区点击都不打开选择器",
        async run(shell, h) {
            const { app } = shell;
            app.openFolder("empty");
            await h.tick(120);
            h.assert(app.state.photo === null, "空文件夹仍有当前照片");
            const picker = h.dom(".pv-picker");
            const closed = () => picker.hidden === true;

            for (const [name, selector] of [["文件栏", ".pv-pane-file"], ["控制栏", ".pv-pane-control"], ["分析栏", ".pv-pane-analysis"]]) {
                const pane = h.dom(selector);
                if (pane.hidden) continue;
                // 直接点在分区根节点上，等同空白处点击。
                h.click(pane);
                await h.tick(40);
                h.assert(closed(), `${name}空白处点击打开了选择器`);
            }

            h.click(h.photoCanvas());
            await h.tick(40);
            h.assert(closed(), "空文件夹主图区点击错误打开了选择器");
        },
    },
    {
        id: "IMAGE-02",
        title: "双击 100% ⇄ 适应，切图保持倍率，打开文件夹回适应",
        async run(shell, h) {
            const { app } = shell;
            const canvas = h.photoCanvas();
            h.dblclick(canvas);
            await h.tick(80);
            h.assert(app.state.viewport.percentText === "100%", `双击后百分比 ${app.state.viewport.percentText}`);
            h.assert(app.state.viewport.isFit === false, "双击后仍为适应");
            h.assert(h.dom(".pv-photo-nav").hidden === false, "放大态未显示导航图");

            h.dblclick(canvas);
            await h.tick(80);
            h.assert(app.state.viewport.isFit === true, "再次双击未回到适应");
            h.assert(h.dom(".pv-photo-nav").hidden === true, "适应态仍显示导航图");

            h.dblclick(canvas);
            await h.tick(80);
            h.key("ArrowRight");
            await h.tick(120);
            h.assert(app.state.viewport.percentText === "100%", `切图后倍率变为 ${app.state.viewport.percentText}`);

            app.openFolder("night");
            await h.tick(120);
            h.assert(app.state.viewport.isFit === true, "打开文件夹后未回到适应");
        },
    },
    {
        id: "IMAGE-05",
        title: "放大态拖动平移并受边界钳制",
        async run(shell, h) {
            const { app } = shell;
            const canvas = h.photoCanvas();
            h.dblclick(canvas);
            await h.tick(80);
            const before = JSON.stringify(app.state.viewport.offset);
            const rect = canvas.getBoundingClientRect();
            const x = rect.left + rect.width / 2;
            const y = rect.top + rect.height / 2;
            h.pointer(canvas, "pointerdown", x, y);
            h.pointer(canvas, "pointermove", x + 80, y + 60);
            h.pointer(canvas, "pointerup", x + 80, y + 60);
            await h.tick(80);
            h.assert(JSON.stringify(app.state.viewport.offset) !== before, "放大态拖动未平移");
            h.assert(app.state.viewport.percentText === "100%", "拖动改变了倍率");
        },
    },
    {
        id: "IMAGE-06",
        title: "导航图白框可拖动：只改视口中心、不改倍率；按在别处先把框心移过去；第二指让位捏合",
        async run(shell, h) {
            const { app } = shell;
            const canvas = h.photoCanvas();
            h.dblclick(canvas);
            await h.tick(80);
            const nav = h.dom(".pv-photo-nav");
            h.assert(nav.hidden === false, "放大态未显示导航图");
            h.assert(getComputedStyle(nav).pointerEvents !== "none", "导航图不吃指针，无法拖动");
            const percent = app.state.viewport.percentText;
            const before = { ...app.state.viewport.navigator.frame };

            const frame = h.dom(".pv-photo-nav-frame").getBoundingClientRect();
            const startX = frame.left + frame.width / 2;
            const startY = frame.top + frame.height / 2;
            h.pointer(nav, "pointerdown", startX, startY);
            h.pointer(nav, "pointermove", startX + 30, startY + 20);
            h.pointer(nav, "pointerup", startX + 30, startY + 20);
            await h.tick(80);
            const after = app.state.viewport.navigator.frame;
            h.assert(after.x !== before.x || after.y !== before.y, "拖动白框未改变视口中心");
            const dx = (after.x + after.w / 2) - (before.x + before.w / 2);
            const dy = (after.y + after.h / 2) - (before.y + before.h / 2);
            h.assert(dx > 0 && dy > 0, `白框未跟随指针方向：dx ${dx}, dy ${dy}`);
            h.assert(app.state.viewport.percentText === percent, `拖动改变了倍率：${percent} → ${app.state.viewport.percentText}`);
            h.assert(app.state.viewport.isFit === false, "拖动回到了适应态");

            // 按在缩略图其他位置：先把框心移到该点，再继续拖。
            const thumb = h.dom(".pv-photo-nav-thumb").getBoundingClientRect();
            const blankX = thumb.left + thumb.width * 0.15;
            const blankY = thumb.top + thumb.height * 0.15;
            h.pointer(nav, "pointerdown", blankX, blankY);
            h.pointer(nav, "pointerup", blankX, blankY);
            await h.tick(80);
            const jumped = app.state.viewport.navigator.frame;
            const centerU = (jumped.x + jumped.w / 2) / thumb.width;
            const centerV = (jumped.y + jumped.h / 2) / thumb.height;
            // 靠近缩略图边缘时中心受钳制，框留在缩略图内。
            const expectU = Math.max(0.15, jumped.w / (2 * thumb.width));
            const expectV = Math.max(0.15, jumped.h / (2 * thumb.height));
            h.assert(Math.abs(centerU - expectU) <= 0.02 && Math.abs(centerV - expectV) <= 0.02,
                `按在别处未把框心移过去：${centerU.toFixed(2)}, ${centerV.toFixed(2)} ≠ ${expectU.toFixed(2)}, ${expectV.toFixed(2)}`);
            h.assert(app.state.viewport.percentText === percent, "点按导航图改变了倍率");

            // 导航图拖动中第二指按下：取消拖动，两指交给主图捏合。
            const navRect = nav.getBoundingClientRect();
            const navX = navRect.left + navRect.width / 2;
            const navY = navRect.top + navRect.height / 2;
            const canvasRect = canvas.getBoundingClientRect();
            const canvasX = canvasRect.left + canvasRect.width / 2;
            const canvasY = canvasRect.top + canvasRect.height / 2;
            // 第二指沿两指连线向外移动，保证间距增大。
            const spanX = canvasX - navX;
            const spanY = canvasY - navY;
            const span = Math.hypot(spanX, spanY) || 1;
            const outX = canvasX + spanX / span * 80;
            const outY = canvasY + spanY / span * 80;
            h.pointer(nav, "pointerdown", navX, navY, { pointerType: "touch", pointerId: 21 });
            await h.tick(20);
            h.assert(nav.classList.contains("is-dragging"), "单指按在导航图未开始拖动");
            const frameBeforeSecond = { ...app.state.viewport.navigator.frame };
            const scaleBefore = app.state.viewport.scale;

            h.pointer(canvas, "pointerdown", canvasX, canvasY, { pointerType: "touch", pointerId: 22 });
            await h.tick(20);
            h.assert(!nav.classList.contains("is-dragging"), "第二指按下未取消导航图拖动");
            const frameAfterSecond = app.state.viewport.navigator.frame;
            h.assert(frameAfterSecond.x === frameBeforeSecond.x && frameAfterSecond.y === frameBeforeSecond.y,
                "第二指按下时导航图仍移动了视口");

            h.pointer(canvas, "pointermove", outX, outY, { pointerType: "touch", pointerId: 22 });
            await h.tick(20);
            h.assert(app.state.viewport.scale > scaleBefore,
                `两指未缩放主图：${scaleBefore} → ${app.state.viewport.scale}`);
            h.pointer(canvas, "pointerup", outX, outY, { pointerType: "touch", pointerId: 22 });
            h.pointer(nav, "pointerup", navX, navY, { pointerType: "touch", pointerId: 21 });
            await h.tick(20);
            // 恢复指针类型：后续检查模拟的是鼠标双击。
            h.pointer(canvas, "pointerdown", canvasX, canvasY);
            h.pointer(canvas, "pointerup", canvasX, canvasY);
            await h.tick(20);
        },
    },
    {
        id: "IMAGE-10b",
        title: "画布铺满窗口、适应区数据与窄主图导航用真实 DOM 核对",
        async run(shell, h) {
            const { app } = shell;
            app.setLayoutModeOverride("col");
            if (!app.state.panes.analysis) app.toggleAnalysis();
            shell.setWindowSize(560, 480);
            app.openFolder("empty");
            await h.tick(140);
            const windowRect = h.dom(".pv-window").getBoundingClientRect();
            const canvas = h.photoCanvas();
            const canvasRect = canvas.getBoundingClientRect();
            h.assert(Math.abs(canvasRect.width - windowRect.width) <= 1 && Math.abs(canvasRect.height - windowRect.height) <= 1,
                `主图画布 ${canvasRect.width}×${canvasRect.height} 未铺满窗口 ${windowRect.width}×${windowRect.height}`);
            const fit = app.state.layout.panes.fitRect;
            const host = h.dom(".pv-photo");
            h.assert(host.dataset.fitRect === `${fit.x},${fit.y},${fit.w},${fit.h}`,
                `主图实际 render fitRect ${host.dataset.fitRect} ≠ ${fit.x},${fit.y},${fit.w},${fit.h}`);
            app.openFolder("harbor");
            await h.tick(160);
            const fitAfter = app.state.layout.panes.fitRect;
            h.dblclick(canvas);
            await h.tick(100);
            const nav = h.dom(".pv-photo-nav");
            h.assert(!nav.hidden, "放大态未显示导航图");
            const navRect = nav.getBoundingClientRect();
            h.assert(Math.abs((windowRect.left + fitAfter.x + fitAfter.w) - navRect.right - 12) <= 1,
                `导航图未贴适应区右缘 ${fitAfter.w}：${windowRect.left + fitAfter.x + fitAfter.w - navRect.right}`);
            h.assert(Math.abs((windowRect.top + fitAfter.y + fitAfter.h) - navRect.bottom - 12) <= 1,
                `导航图未贴适应区下缘：${windowRect.top + fitAfter.y + fitAfter.h - navRect.bottom}`);
            app.fit();
        },
    },
    {
        id: "TOOLBAR-03",
        title: "评分点亮、列布局 5→0，点星不切图",
        async run(shell, h) {
            const { app } = shell;
            const displayed = app.state.displayedId;
            h.click(h.ratingButton(4));
            await h.until(() => app.state.photo.rating === 4, 4000);
            await h.tick(60);
            const buttons = h.domAll(".pv-control-rating-button");
            for (const button of buttons) {
                const value = Number(button.dataset.rating);
                if (value === 0) continue;
                h.assert(button.classList.contains("is-on") === (value <= 4), `评分按钮 ${value} 点亮状态不符`);
            }
            h.assert(app.state.displayedId === displayed, "点控制栏评分切换了当前照片");

            const ordered = button => button.getBoundingClientRect();
            const sorted = buttons.slice().sort((a, b) => ordered(a).top - ordered(b).top).map((button) => button.dataset.rating);
            h.deepEqual(sorted, ["5", "4", "3", "2", "1", "0"], "列布局评分组顺序不是 5→0");
        },
    },
    {
        id: "TOOLBAR-04",
        title: "命令按钮提示为「名称（主 / 次）」、下一张切图并定位、无照片禁用",
        async run(shell, h) {
            const { app } = shell;
            const next = h.dom('.pv-control-command-button[data-command="Next"]');
            // TOOLBAR-04：格式 `名称（主 / 次）`，不含「未设置」占位、不用点号分隔；提示走 data-tooltip。
            h.assert(next.dataset.tooltip === "下一张（→ / 滚轮下）", `按钮提示格式不符：${next.dataset.tooltip}`);
            const fit = h.dom('.pv-control-command-button[data-command="Fit"]');
            h.assert(fit.dataset.tooltip === "缩放适应（Ctrl+0 / F）", `按钮提示格式不符：${fit.dataset.tooltip}`);
            h.assert(!next.dataset.tooltip.includes("未设置"), "提示出现未设置占位");
            h.assert(!next.dataset.tooltip.includes("·"), `提示仍用点号分隔命令名与绑定：${next.dataset.tooltip}`);
            h.assert(next.getAttribute("title") === null, "命令按钮仍用浏览器原生 title");
            const before = app.state.displayedId;
            h.click(next);
            await h.tick(100);
            h.assert(app.state.displayedId !== before, "点「下一张」未切图");
            const index = h.cardIndex(app.state.displayedId);
            h.assert(Math.abs(h.filmOffset() - h.expectedCenter(index)) <= 1, "点「下一张」未定位");

            app.openFolder("empty");
            await h.tick(120);
            const commands = h.domAll(".pv-control-command-button").filter((node) => !node.hidden);
            h.assert(commands.length > 0, "空态控制栏没有命令按钮");
            h.assert(commands.every((node) => node.disabled), "空态仍有点得动的命令按钮");
            h.assert(h.domAll(".pv-control-rating-button").every((node) => node.disabled), "空态评分组未禁用");
            h.assert(h.domAll(".pv-control-exif-block").every((node) => node.textContent.includes("--")), "空态 EXIF 未显示 --");
        },
    },
    {
        id: "TOOLBAR-04a",
        title: "自绘悬停提示：延迟后出现于指针右下，按下即隐藏，不挡指针",
        async run(shell, h) {
            const t = (name) => shell.t(name);
            // 清掉上一条检查可能留下的提示与「连续划过」窗口，从独立悬停开始计时。
            document.body.dispatchEvent(new PointerEvent("pointerdown", { bubbles: true }));
            await h.wait(t("motion.tooltip.delay") + 50);
            const button = h.dom('.pv-control-command-button[data-command="Next"]');
            const rect = button.getBoundingClientRect();
            const x = rect.left + rect.width / 2;
            const y = rect.top + rect.height / 2;
            h.pointer(button, "pointermove", x, y);
            h.pointer(button, "pointerover", x, y);
            await h.wait(t("motion.tooltip.delay") - 120);
            const tooltip = h.dom(".pv-tooltip");
            h.assert(tooltip.hidden === true, "悬停未满延迟就显示了提示");

            await h.until(() => tooltip.hidden === false, 1200);
            h.assert(tooltip.textContent === button.dataset.tooltip, `提示内容 ${tooltip.textContent}`);
            const hostRect = h.dom(".pv-window").getBoundingClientRect();
            const tooltipRect = tooltip.getBoundingClientRect();
            const expectedLeft = Math.min(x + 12, hostRect.right - tooltipRect.width);
            const expectedTop = Math.min(y + 16, hostRect.bottom - tooltipRect.height);
            h.assert(tooltipRect.left >= expectedLeft - 1.5,
                `提示未出现在指针右下：left ${tooltipRect.left}, x ${x}, 宽 ${tooltipRect.width}, 左右界 ${hostRect.right}`);
            h.assert(tooltipRect.top >= expectedTop - 1.5,
                `提示未出现在指针右下：top ${tooltipRect.top}, y ${y}, 高 ${tooltipRect.height}, 下界 ${hostRect.bottom}`);
            const style = getComputedStyle(tooltip);
            h.assert(colorMatches(style.backgroundColor, t("color.popup.background")), `提示底色 ${style.backgroundColor}`);
            h.assert(colorMatches(style.borderTopColor, t("color.popup.border")), `提示描边 ${style.borderTopColor}`);
            h.assert(style.borderTopLeftRadius === `${t("popup.cornerRadius")}px`, `提示圆角 ${style.borderTopLeftRadius}`);
            h.assert(style.fontSize === `${t("tooltip.fontSize")}px`, `提示字号 ${style.fontSize}`);
            h.assert(style.maxWidth === `${t("tooltip.maxWidth")}px`, `提示最大宽 ${style.maxWidth}`);
            h.assert(style.pointerEvents === "none", `提示阻挡指针：${style.pointerEvents}`);

            // 按下即隐藏；窗口内不再残留原生 title。
            h.pointer(button, "pointerdown", x, y);
            await h.tick(20);
            h.assert(tooltip.hidden === true, "按下后提示未隐藏");
            h.pointer(button, "pointerup", x, y);
            h.assert(document.querySelectorAll(".pv-window [title]").length === 0, "窗口内仍有原生 title 属性");
        },
    },
    {
        id: "TOOLBAR-04b",
        title: "评分快捷命令为星形内叠数字（SetRating3），0 为叉",
        async run(shell, h) {
            const { app } = shell;
            const t = (name) => shell.t(name);
            const original = app.settings.hotkeys.map((row) => ({ ...row }));
            try {
                app.updateSettings({
                    hotkeys: original.map((row) => (row.command === "SetRating3" ? { ...row, display: true } : { ...row })),
                });
                await h.tick(120);
                const button = h.dom('.pv-control-command-button[data-command="SetRating3"]');
                const star = button.querySelector(".pv-icon-solid");
                const digit = button.querySelector(".pv-command-rating-digit");
                h.assert(star !== null, "评分快捷命令缺少实心星字形");
                h.assert(digit !== null && digit.textContent === "3", `星内数字 ${digit ? digit.textContent : "缺失"}`);
                h.assert(getComputedStyle(star).fontSize === `${t("toolbar.command.glyph.fontSize")}px`,
                    `星形字号 ${getComputedStyle(star).fontSize}`);
                h.assert(getComputedStyle(digit).fontSize === `${t("toolbar.rating.digit.fontSize")}px`,
                    `数字字号 ${getComputedStyle(digit).fontSize}`);
                h.assert(getComputedStyle(digit).fontWeight === "600", `数字字重 ${getComputedStyle(digit).fontWeight}`);
                h.assert(colorMatches(getComputedStyle(digit).color, t("color.toolbar.command.background")),
                    `数字颜色 ${getComputedStyle(digit).color}`);
                h.assert(button.querySelector("svg") === null, "评分快捷命令仍用内联 SVG");
            } finally {
                app.updateSettings({ hotkeys: original });
                await h.tick(80);
            }
        },
    },
    {
        id: "TOOLBAR-10",
        title: "命令常态无描边、悬停出描边；评分槽内切正圆且禁用只降内容",
        async run(shell, h) {
            const { app } = shell;
            const t = (name) => shell.t(name);
            const command = h.dom('.pv-control-command-button[data-command="Next"]');
            const commandStyle = getComputedStyle(command);
            h.assert(colorMatches(commandStyle.backgroundColor, t("color.toolbar.button.tint"), t("shell.glass.button.tint.opacity")),
                `命令常态底色 ${commandStyle.backgroundColor}`);
            h.assert(commandStyle.borderTopWidth === "1px" && colorMatches(commandStyle.borderTopColor, "#00000000"),
                `命令常态仍画描边 ${commandStyle.borderTopColor}`);

            const hover = computedState(command, ".pv-control-command-button:hover");
            const pressed = computedState(command, ".pv-control-command-button:active", { state: ":active" });
            h.assert(hover !== null && pressed !== null, "缺少命令按钮悬停/按下状态");
            h.assert(colorMatches(hover.borderTopColor, t("color.toolbar.command.hover.border")),
                `悬停描边 ${hover.borderTopColor}`);
            h.assert(colorMatches(hover.backgroundColor, t("color.toolbar.command.hover.background")),
                `悬停底色 ${hover.backgroundColor}`);
            h.assert(colorMatches(pressed.backgroundColor, t("color.toolbar.command.pressed.background")),
                `按下底色 ${pressed.backgroundColor}`);
            h.assert(cssRule(".pv-control-command-button[aria-pressed") === null, "命令按钮仍按 aria-pressed 强调底色");

            const rating = h.dom(".pv-control-rating");
            const ratingRect = rating.getBoundingClientRect();
            h.assert(!rating.classList.contains("is-compact"), "宽形态评分出现紧凑态");
            h.assert(Math.round(ratingRect.height) === 224 && Math.round(ratingRect.width) === 44,
                `宽形态评分胶囊 ${ratingRect.width}×${ratingRect.height}`);
            const star = h.dom('.pv-control-rating-button[data-rating="3"]');
            const starRect = star.getBoundingClientRect();
            const slot = Math.min(starRect.width, starRect.height);
            h.assert(getComputedStyle(star).backgroundColor === "rgba(0, 0, 0, 0)", "评分槽常态有底色");
            h.assert(star.style.getPropertyValue("--pv-rating-slot") === `${slot}px`,
                `评分槽内切圆直径 ${star.style.getPropertyValue("--pv-rating-slot")} ≠ 槽位短边 ${slot}`);
            const circle = getComputedStyle(star, "::before");
            h.assert(circle.width === `${slot}px` && circle.height === `${slot}px`, `内切圆尺寸 ${circle.width}×${circle.height}`);
            h.assert(circle.borderTopLeftRadius === "50%", `内切圆不是正圆：${circle.borderTopLeftRadius}`);
            const ratingHover = computedState(star, ".pv-control-rating-button:hover", { pseudo: "::before" });
            const ratingPressed = computedState(star, ".pv-control-rating-button:active", { pseudo: "::before", state: ":active" });
            h.assert(ratingHover !== null && ratingPressed !== null, "缺少评分槽悬停/按下状态");
            h.assert(colorMatches(getComputedStyle(rating).backgroundColor, commandStyle.backgroundColor),
                `星级胶囊材质 ${getComputedStyle(rating).backgroundColor} ≠ 命令按钮 ${commandStyle.backgroundColor}`);
            h.assert(colorMatches(ratingHover.backgroundColor, t("color.menu.item.hover")),
                `评分悬停 ${ratingHover.backgroundColor}`);
            h.assert(colorMatches(ratingPressed.backgroundColor, t("color.menu.item.pressed")),
                `评分按下 ${ratingPressed.backgroundColor}`);
            h.assert(ratingHover.width === `${slot}px` && ratingHover.height === `${slot}px`,
                `悬停正圆越出槽位：${ratingHover.width}×${ratingHover.height}`);

            const exif = app.state.layout.exif;
            h.assert(exif.variant === "bare", `竖排 EXIF 变体 ${exif.variant}`);
            h.assert(exif.frame.w === t("toolbar.exif.item.width"), `竖排 EXIF 容器宽 ${exif.frame.w}`);
            const exifBlock = h.dom(".pv-control-exif-block");
            h.assert(exifBlock.classList.contains("is-bare"), "竖排 EXIF 未使用无衬底变体");
            h.assert(getComputedStyle(exifBlock).backgroundColor === "rgba(0, 0, 0, 0)", "竖排 EXIF 仍有衬底色");

            app.openFolder("empty");
            await h.tick(120);
            const disabled = h.dom('.pv-control-command-button[data-command="Next"]');
            const disabledColor = parseColor(getComputedStyle(disabled).color);
            h.assert(disabled.disabled && disabledColor && Math.abs(disabledColor.a - t("color.toolbar.disabled.content.alpha")) <= 0.01,
                `命令禁用内容 alpha ${disabledColor?.a}`);
            h.assert(colorMatches(getComputedStyle(disabled).backgroundColor, t("color.toolbar.button.tint"), t("shell.glass.button.tint.opacity")),
                "命令禁用换了底色");
            const disabledStar = h.dom('.pv-control-rating-button[data-rating="3"]');
            const starColor = parseColor(getComputedStyle(disabledStar).color);
            h.assert(disabledStar.disabled && starColor && Math.abs(starColor.a - t("color.toolbar.disabled.content.alpha")) <= 0.01,
                `评分禁用内容 alpha ${starColor?.a}`);
        },
    },
    {
        id: "STATE-01",
        title: "缓存命中切图保留前一张，未命中即清图并延迟提示",
        async run(shell, h) {
            const { app } = shell;
            const previousFolder = app.state.folder?.id;
            app.openFolder("empty");
            await h.tick(120);
            h.assert(h.dom(".pv-photo-hint-title").hidden && h.dom(".pv-photo-hint-detail").hidden,
                "空文件夹错误显示了首屏操作提示");
            app.openFolder(previousFolder);
            await h.tick(120);
            const usable = app.state.cards
                .map((card) => card.photo)
                .filter((photo) => !(photo.fault && photo.fault.decode === "all"));
            h.assert(usable.length >= 2, "harbor 可解码照片不足");
            const [first, second] = usable;
            const cacheCount = app.settings.cacheCount;
            const hitCacheCount = Math.max(4, cacheCount);
            const slow = typeof shell.decoder?.setLatencyMultiplier === "function";

            try {
                // 缓存数量上限压到 1：把缓存收敛到一张（上屏 second 再上屏 first），
                // 之后切到 second 必然未命中，才能验证黑屏加载路径。解码放慢到 8 倍，
                // 让代理稳定超过 300 ms，露出加载提示的时序。
                app.updateSettings({ cacheCount: 1 });
                if (slow) shell.decoder.setLatencyMultiplier(8);
                app.selectCard(second.id);
                await h.until(() => app.state.session.surface !== null, 8000);
                app.selectCard(first.id);
                await h.until(() => app.state.session.surface !== null, 8000);

                app.selectCard(second.id);
                // STATE-01：需要解码时切图即清旧图，不让上一张冒充当前照片。
                h.assert(app.state.session.surface === null, "未命中的切图没有清旧图");
                h.assert(app.state.session.tier === "none", `未命中的切图仍标记档位 ${app.state.session.tier}`);
                h.assert(app.state.session.loadingHint === false, "切图瞬间就显示了加载提示");
                if (slow) {
                    await h.until(() => app.state.session.loadingHint === true, 3000);
                    h.assert(app.state.session.surface === null, "加载提示出现时已不是黑屏加载");
                }
                await h.until(() => app.state.session.surface !== null, 8000);
                h.assert(app.state.session.loadingHint === false, "图到达后加载提示未撤下");
                if (slow) shell.decoder.setLatencyMultiplier(1);
                app.updateSettings({ cacheCount: hitCacheCount });

                // 两张都上屏过：切回已上屏的照片命中展示产物缓存，同步换图、不空屏、不显示提示。
                app.selectCard(first.id);
                await h.until(() => app.state.session.surface !== null, 5000);
                const surfaceFirst = app.state.session.surface;
                app.selectCard(second.id);
                await h.until(() => app.state.session.surface !== null, 5000);

                app.selectCard(first.id);
                h.assert(app.state.session.surface === surfaceFirst, "命中缓存没有同步换回该照片的展示产物");
                h.assert(app.state.session.loadingHint === false, "命中缓存切图显示了加载提示");
                h.assert(app.state.viewport.isFit === true, "命中缓存切图没有保持适应态");
                await h.tick(400);
                h.assert(app.state.session.surface === surfaceFirst, "命中缓存后又发生了解码替换");
                h.assert(app.state.session.loadingHint === false, "命中缓存 300 ms 后出现加载提示");

                // 需要 Full 而缓存只有代理：按未命中清图，低分辨率顶替默认禁用。
                h.dblclick(h.photoCanvas());
                await h.tick(80);
                h.assert(app.state.viewport.isFit === false, "双击未进入 100%");
                await h.until(() => app.state.session.tier === "full", 5000);
                app.selectCard(second.id);
                h.assert(app.state.session.surface === null, "需要 Full 时仍拿代理缓存顶替上屏");
                h.assert(app.state.session.tier === "none", `需要 Full 的未命中切图仍标记档位 ${app.state.session.tier}`);
                await h.until(() => app.state.session.surface !== null, 5000);
            } finally {
                if (slow) shell.decoder.setLatencyMultiplier(1);
                app.updateSettings({ cacheCount });
            }
        },
    },
    {
        id: "STATE-02",
        title: "解码失败显示失败文案，仍可选中评分",
        async run(shell, h) {
            const { app } = shell;
            const photo = h.harborPhoto((entry) => entry.fault?.decode === "all");
            h.assert(photo, "harbor 中找不到 decode=all 的照片");
            app.selectCard(photo.id);
            const error = h.dom(".pv-photo-error");
            await h.until(() => error.hidden === false, 5000);
            h.assert(error.textContent.includes("无法打开该图片"), `失败标题不符：${error.textContent}`);
            h.assert(error.textContent.includes(photo.name), `失败详情缺少文件名：${error.textContent}`);
            h.assert(app.state.displayedId === photo.id, "失败照片未成为当前照片");
            h.assert(app.state.session.surface === null, "失败照片不应有展示图");
        },
    },
    {
        id: "STATE-03",
        title: "写回失败星级不变、提示条出现并在 4s 后消失",
        async run(shell, h) {
            const { app } = shell;
            const photo = h.harborPhoto((entry) => entry.fault?.write === true);
            h.assert(photo, "harbor 中找不到 write 故障照片");
            app.selectCard(photo.id);
            await h.tick(120);
            const original = app.state.photo.rating;
            const target = original === 1 ? 2 : 1;
            h.click(h.ratingButton(target));
            const toast = h.dom(".pv-photo-toast");
            await h.until(() => toast.hidden === false, 4000);
            // STATE-03：文案统一为「动作：对象（原因）」。
            h.assert(toast.textContent === `无法写入星级：${photo.name}（文件只读）`, `提示条文案不符：${toast.textContent}`);
            h.assert(app.state.photo.rating === original, `写回失败后星级变为 ${app.state.photo.rating}`);
            await h.until(() => toast.hidden === true, 6000);
        },
    },
    {
        id: "STATE-03b",
        title: "全分辨率失败提示且保留代理图",
        async run(shell, h) {
            const { app } = shell;
            const photo = h.harborPhoto((entry) => entry.fault?.decode === "full");
            h.assert(photo, "harbor 中找不到 decode=full 的照片");
            app.selectCard(photo.id);
            await h.until(() => app.state.session.surface !== null, 5000);
            h.dblclick(h.photoCanvas());
            await h.tick(80);
            const toast = h.dom(".pv-photo-toast");
            await h.until(() => toast.hidden === false, 5000);
            h.assert(toast.textContent === `无法载入全分辨率：${photo.name}`, `提示条文案不符：${toast.textContent}`);
            h.assert(app.state.session.surface !== null, "全分辨率失败后代理图被清空");
            h.assert(app.state.session.tier !== "full", "全分辨率失败却标记为 full");
        },
    },
    {
        id: "ANALYSIS-01",
        title: "菜单打开分析栏，瓦片等边且对焦点瓦片按对焦数据出现",
        async run(shell, h) {
            const { app } = shell;
            const withFocus = h.harborPhoto((entry) => entry.focus);
            app.selectCard(withFocus.id);
            await h.tick(60);
            await h.openMainMenu();
            h.click(h.menuItem("analysis"));
            await h.tick(120);
            h.assert(app.state.panes.analysis === true, "菜单未打开分析栏");
            const dino = app.capabilities.dino;
            const focusTileCount = dino ? 7 : 5;
            const withoutFocusCount = dino ? 6 : 4;
            await h.until(() => document.querySelectorAll(".pv-analysis-tile").length === focusTileCount
                && app.state.analysis.tiles.length === focusTileCount, 4000);
            const tiles = h.domAll(".pv-analysis-tile");
            h.assert(tiles.length === focusTileCount, `有对焦点时瓦片数 ${tiles.length} ≠ ${focusTileCount}`);
            const widths = tiles.map((node) => Math.round(node.getBoundingClientRect().width));
            h.assert(widths.every((width) => Math.abs(width - widths[0]) <= 1), `瓦片不等边：${widths.join(",")}`);
            h.assert(tiles[0].dataset.kind === "focus", "首瓦片不是对焦点");
            h.assert(document.querySelector('.pv-analysis-tile[data-kind="focus"]') !== null, "有对焦点时缺少对焦点瓦片");
            h.deepEqual(tiles.map((node) => node.dataset.kind), dino
                ? ["focus", "center", "histogram", "pca", "cosine", "sharpness", "shake"]
                : ["focus", "center", "histogram", "sharpness", "shake"], "分析栏瓦片顺序不符");
            if (!dino) {
                h.assert(document.querySelector('.pv-analysis-tile[data-kind="pca"]') === null, "标准版出现 PCA 空框");
                h.assert(document.querySelector('.pv-analysis-tile[data-kind="cosine"]') === null, "标准版出现 Cosine 空框");
            }

            const withoutFocus = h.harborPhoto((entry) => !entry.focus);
            app.selectCard(withoutFocus.id);
            await h.tick(120);
            await h.until(() => document.querySelectorAll(".pv-analysis-tile").length === withoutFocusCount
                && app.state.analysis.tiles.length === withoutFocusCount, 4000);
            h.assert(document.querySelector('.pv-analysis-tile[data-kind="focus"]') === null, "无对焦点时仍出现对焦点瓦片");
        },
    },
    {
        id: "ANALYSIS-04",
        title: "触控按下点亮、滑动切换、取消/失焦/隐藏清除，旧指针不清新目标",
        async run(shell, h) {
            const { app } = shell;
            const photo = h.harborPhoto((entry) => entry.focus && entry.extracted === true);
            h.assert(photo, "harbor 中找不到有对焦点且已提取的照片");
            app.selectCard(photo.id);
            await h.tick(60);
            await h.openMainMenu();
            h.click(h.menuItem("analysis"));
            const tileCount = app.capabilities.dino ? 7 : 5;
            await h.until(() => app.state.panes.analysis && h.domAll(".pv-analysis-tile").length === tileCount, 4000);
            await h.until(() => app.state.analysis.diagnostics !== null, 4000);

            const center = h.dom('.pv-analysis-tile[data-kind="center"]');
            const focus = h.dom('.pv-analysis-tile[data-kind="focus"]');
            const point = (node) => {
                const rect = node.getBoundingClientRect();
                return { x: rect.left + rect.width / 2, y: rect.top + rect.height / 2 };
            };
            const centerPoint = point(center);
            const focusPoint = point(focus);
            h.assert(center.classList.contains("is-detail") && focus.classList.contains("is-detail"),
                "细节瓦片缺少 is-detail 标记");
            const detailHover = computedState(center, ".pv-analysis-tile.is-detail:hover");
            h.assert(detailHover !== null && colorMatches(detailHover.borderTopColor, shell.t("color.analysis.highlight")),
                `细节瓦片悬停描边 ${detailHover?.borderTopColor}`);
            h.assert(center.getAttribute("aria-pressed") === "false", "细节瓦片未暴露初始未激活状态");

            h.pointer(center, "pointerdown", centerPoint.x, centerPoint.y, { pointerType: "touch", pointerId: 41 });
            await h.tick(40);
            h.assert(app.state.analysis.hoverKind === "center", "触控按下未立即点亮中心瓦片");
            h.assert(app.state.analysis.highlight !== null, "触控按下未显示主图绿框");
            h.assert(center.getAttribute("aria-pressed") === "true", "活动瓦片未暴露激活状态");
            h.assert(focus.getAttribute("aria-pressed") === "false", "非活动瓦片被标记为激活");
            h.assert(app.state.analysis.crosshair === null, "触控按下细节瓦片落了诊断准星");

            h.pointer(center, "pointermove", focusPoint.x, focusPoint.y, { pointerType: "touch", pointerId: 41 });
            await h.tick(40);
            h.assert(app.state.analysis.hoverKind === "focus", "滑入对焦点瓦片未切换活动目标");
            h.assert(focus.getAttribute("aria-pressed") === "true", "切换后新瓦片未标记激活");
            h.assert(center.getAttribute("aria-pressed") === "false", "切换后旧瓦片仍标记激活");

            const pane = h.paneRect("analysis");
            h.pointer(center, "pointermove", pane.left + 2, pane.top + pane.height / 2, { pointerType: "touch", pointerId: 41 });
            await h.tick(40);
            h.assert(app.state.analysis.hoverKind === null, "滑出细节瓦片后仍保留活动目标");
            h.assert(app.state.analysis.highlight === null, "滑出细节瓦片后绿框未清除");

            h.pointer(center, "pointermove", focusPoint.x, focusPoint.y, { pointerType: "touch", pointerId: 41 });
            await h.tick(40);
            h.assert(app.state.analysis.hoverKind === "focus", "滑回细节瓦片未重新点亮");

            h.pointer(center, "pointerdown", centerPoint.x, centerPoint.y, { pointerType: "touch", pointerId: 42 });
            await h.tick(40);
            h.assert(app.state.analysis.hoverKind === "center", "新触控未接管活动目标");
            h.pointer(focus, "pointerup", focusPoint.x, focusPoint.y, { pointerType: "touch", pointerId: 41 });
            await h.tick(40);
            h.assert(app.state.analysis.hoverKind === "center", "旧指针抬起清除了新目标");
            h.assert(app.state.analysis.highlight !== null, "旧指针抬起清除了新目标的绿框");

            h.pointer(center, "pointerup", centerPoint.x, centerPoint.y, { pointerType: "touch", pointerId: 42 });
            await h.tick(40);
            h.assert(app.state.analysis.hoverKind === null, "抬手后未清除活动目标");
            h.assert(app.state.analysis.highlight === null, "抬手后绿框残留");
            h.assert(center.getAttribute("aria-pressed") === "false", "抬手后瓦片仍标记激活");

            h.pointer(center, "pointerdown", centerPoint.x, centerPoint.y, { pointerType: "touch", pointerId: 45 });
            await h.tick(40);
            h.assert(app.state.analysis.hoverKind === "center", "取消前触控未点亮");
            h.pointer(center, "pointercancel", centerPoint.x, centerPoint.y, { pointerType: "touch", pointerId: 45 });
            await h.tick(40);
            h.assert(app.state.analysis.hoverKind === null, "pointercancel 未清除活动目标");
            h.assert(app.state.analysis.highlight === null, "pointercancel 后绿框残留");
            h.pointer(center, "pointerup", centerPoint.x, centerPoint.y, { pointerType: "touch", pointerId: 45 });
            await h.tick(40);
            h.assert(app.state.analysis.hoverKind === null, "取消后的抬起重新点亮了绿框");

            h.pointer(center, "pointerdown", centerPoint.x, centerPoint.y, { pointerType: "touch", pointerId: 46 });
            await h.tick(40);
            h.assert(app.state.analysis.hoverKind === "center", "失去捕获前触控未点亮");
            h.pointer(center, "lostpointercapture", centerPoint.x, centerPoint.y, { pointerType: "touch", pointerId: 46 });
            await h.tick(40);
            h.assert(app.state.analysis.hoverKind === null, "lostpointercapture 未清除活动目标");
            h.assert(app.state.analysis.highlight === null, "lostpointercapture 后绿框残留");

            h.pointer(center, "pointerdown", centerPoint.x, centerPoint.y, { pointerType: "touch", pointerId: 46 });
            await h.tick(40);
            h.assert(app.state.analysis.hoverKind === "center", "旧指针未点亮");
            h.pointer(focus, "pointerdown", focusPoint.x, focusPoint.y, { pointerType: "touch", pointerId: 47 });
            await h.tick(40);
            h.assert(app.state.analysis.hoverKind === "focus", "新触控未接管活动目标");
            h.pointer(center, "lostpointercapture", centerPoint.x, centerPoint.y, { pointerType: "touch", pointerId: 46 });
            await h.tick(40);
            h.assert(app.state.analysis.hoverKind === "focus", "旧指针的 lostpointercapture 清除了新目标");
            h.assert(app.state.analysis.highlight !== null, "旧指针的 lostpointercapture 清除了新目标绿框");
            h.pointer(focus, "lostpointercapture", focusPoint.x, focusPoint.y, { pointerType: "touch", pointerId: 47 });
            await h.tick(40);
            h.assert(app.state.analysis.hoverKind === null, "当前指针的 lostpointercapture 未清除活动目标");

            h.pointer(focus, "pointerenter", focusPoint.x, focusPoint.y, { pointerType: "mouse" });
            await h.tick(40);
            h.assert(app.state.analysis.hoverKind === "focus", "鼠标悬停未点亮绿框");
            h.pointer(focus, "pointerleave", focusPoint.x, focusPoint.y, { pointerType: "mouse" });
            await h.tick(40);
            h.assert(app.state.analysis.hoverKind === null, "鼠标离开未清除绿框");

            const activate = () => center.dispatchEvent(new MouseEvent("click", {
                bubbles: true, cancelable: true, detail: 0, view: window,
            }));
            activate();
            await h.tick(40);
            h.assert(app.state.analysis.hoverKind === "center", "辅助技术激活未显示绿框");
            h.assert(center.getAttribute("aria-pressed") === "true", "辅助技术激活后未暴露活动状态");
            activate();
            await h.tick(40);
            h.assert(app.state.analysis.hoverKind === null, "辅助技术再次激活未清除绿框");
            h.assert(center.getAttribute("aria-pressed") === "false", "辅助技术清除后仍标记激活");

            // EDITION-03：标准版只有 CV 诊断瓦片可点。
            const pca = h.dom(`.pv-analysis-tile[data-kind="${app.capabilities.dino ? "pca" : "sharpness"}"]`);
            h.click(pca);
            await h.tick(60);
            const crosshair = app.state.analysis.crosshair;
            h.assert(crosshair !== null, "点诊断瓦片未落准星");
            h.assert(app.state.analysis.hoverKind === null, "点诊断瓦片点亮了细节绿框");

            const pcaPoint = point(pca);
            h.pointer(pca, "pointerdown", pcaPoint.x, pcaPoint.y, { pointerType: "touch", pointerId: 48 });
            h.pointer(pca, "pointermove", centerPoint.x, centerPoint.y, { pointerType: "touch", pointerId: 48 });
            await h.tick(40);
            h.assert(app.state.analysis.hoverKind === null, "非细节起点的触控滑入细节瓦片点亮了绿框");
            h.pointer(pca, "pointerup", pcaPoint.x, pcaPoint.y, { pointerType: "touch", pointerId: 48 });
            await h.tick(40);
            h.assert(app.state.analysis.hoverKind === null, "非细节起点的触控抬手后仍点亮");

            h.pointer(pca, "pointerdown", pcaPoint.x, pcaPoint.y, { pointerType: "touch", pointerId: 49 });
            h.pointer(pca, "pointerup", pcaPoint.x, pcaPoint.y, { pointerType: "touch", pointerId: 49 });
            await h.tick(40);
            h.assert(app.state.analysis.hoverKind === null, "诊断触控点亮了细节绿框");
            h.click(pca);
            await h.tick(60);
            const touchCrosshair = app.state.analysis.crosshair;
            h.assert(touchCrosshair !== null, "触控点按诊断瓦片未落准星");
            h.assert(app.state.analysis.hoverKind === null, "触控点按诊断瓦片点亮了细节绿框");

            const beforePhoto = app.state.displayedId;
            h.dblclick(center, centerPoint.x, centerPoint.y);
            await h.tick(80);
            h.assert(app.state.displayedId === beforePhoto, "双击细节预览切换了照片");
            h.assert(app.state.analysis.hoverKind === null, "双击细节预览点亮了绿框");
            h.deepEqual(app.state.analysis.crosshair, touchCrosshair, "双击定位改变了诊断准星");

            app.fit();
            await h.tick(60);
            h.pointer(center, "pointerdown", centerPoint.x, centerPoint.y, { pointerType: "touch", pointerId: 50 });
            h.pointer(center, "pointerup", centerPoint.x, centerPoint.y, { pointerType: "touch", pointerId: 50 });
            h.click(center);
            h.pointer(center, "pointerdown", centerPoint.x, centerPoint.y, { pointerType: "touch", pointerId: 51 });
            h.pointer(center, "pointerup", centerPoint.x, centerPoint.y, { pointerType: "touch", pointerId: 51 });
            h.click(center);
            h.dblclick(center, centerPoint.x, centerPoint.y);
            await h.tick(100);
            h.assert(app.state.viewport.percent === "100%", `触控双击细节预览未定位：${app.state.viewport.percent}`);
            h.assert(app.state.displayedId === beforePhoto, "触控双击细节预览切换了照片");
            h.assert(app.state.analysis.hoverKind === null, "触控双击后绿框残留");
            h.deepEqual(app.state.analysis.crosshair, touchCrosshair, "触控双击定位改变了诊断准星");

            h.pointer(focus, "pointerdown", focusPoint.x, focusPoint.y, { pointerType: "touch", pointerId: 52 });
            await h.tick(40);
            h.assert(app.state.analysis.hoverKind === "focus", "失焦前触控未点亮");
            window.dispatchEvent(new Event("blur"));
            await h.tick(40);
            h.assert(app.state.analysis.hoverKind === null, "窗口失焦后仍保留活动目标");
            h.assert(app.state.analysis.highlight === null, "窗口失焦后绿框残留");
            h.pointer(focus, "pointerup", focusPoint.x, focusPoint.y, { pointerType: "touch", pointerId: 52 });
            await h.tick(40);
            h.assert(app.state.analysis.hoverKind === null, "失焦后的抬起重新点亮了绿框");

            h.pointer(center, "pointerdown", centerPoint.x, centerPoint.y, { pointerType: "touch", pointerId: 53 });
            await h.tick(40);
            h.assert(app.state.analysis.hoverKind === "center", "换图前触控未点亮");
            const other = h.harborPhoto((entry) => h.cardIndex(entry.id) !== h.cardIndex(photo.id));
            h.assert(other, "harbor 中找不到另一张照片");
            app.selectCard(other.id);
            await h.tick(120);
            h.assert(app.state.displayedId !== beforePhoto, "换图未生效，无法验证清除");
            h.assert(app.state.analysis.hoverKind === null, "换图后仍保留活动目标");
            h.assert(app.state.analysis.highlight === null, "换图后绿框残留");
            h.pointer(center, "pointermove", centerPoint.x, centerPoint.y, { pointerType: "touch", pointerId: 53 });
            await h.tick(40);
            h.assert(app.state.analysis.hoverKind === null, "旧手势把绿框带到了下一张");

            const hiddenTile = h.dom('.pv-analysis-tile[data-kind="center"]');
            const hiddenPoint = point(hiddenTile);
            h.pointer(hiddenTile, "pointerdown", hiddenPoint.x, hiddenPoint.y, { pointerType: "touch", pointerId: 54 });
            await h.tick(40);
            h.assert(app.state.analysis.hoverKind === "center", "隐藏前触控未点亮");
            await h.openMainMenu();
            h.click(h.menuItem("analysis"));
            await h.until(() => app.state.panes.analysis === false, 4000);
            await h.tick(60);
            h.assert(app.state.analysis.hoverKind === null, "隐藏分析栏后仍保留活动目标");
            h.assert(app.state.analysis.highlight === null, "隐藏分析栏后绿框残留");
            await h.openMainMenu();
            h.click(h.menuItem("analysis"));
            await h.until(() => app.state.panes.analysis === true, 4000);
            await h.tick(80);
            h.pointer(hiddenTile, "pointermove", hiddenPoint.x, hiddenPoint.y, { pointerType: "touch", pointerId: 54 });
            await h.tick(40);
            h.assert(app.state.analysis.hoverKind === null, "隐藏前的手势在重新显示后点亮了绿框");
        },
    },
    {
        id: "ANALYSIS-08",
        title: "点诊断瓦片落准星，面板滚轮不翻页",
        async run(shell, h) {
            const { app } = shell;
            const photo = h.harborPhoto((entry) => entry.extracted === true);
            app.selectCard(photo.id);
            await h.tick(60);
            await h.openMainMenu();
            h.click(h.menuItem("analysis"));
            await h.until(() => app.state.panes.analysis && h.domAll(".pv-analysis-tile").length > 0, 4000);
            await h.until(() => app.state.analysis.diagnostics !== null, 4000);
            const tile = h.dom(`.pv-analysis-tile[data-kind="${app.capabilities.dino ? "pca" : "sharpness"}"]`);
            h.click(tile);
            await h.tick(80);
            const cross = app.state.analysis.crosshair;
            h.assert(cross && cross.u > 0 && cross.u < 1 && cross.v > 0 && cross.v < 1, "点诊断瓦片未落准星");

            const displayed = app.state.displayedId;
            h.wheel(h.dom(".pv-pane-analysis"), { deltaY: 3, deltaMode: 1 });
            await h.tick(120);
            h.assert(app.state.displayedId === displayed, "分析栏滚轮触发了翻页");
            h.assert(app.state.analysis.crosshair !== null, "面板滚轮清除了准星");
        },
    },
    {
        id: "SIM-01",
        title: "相似开关展开面板、文件栏加宽、列表与排序不变",
        async run(shell, h) {
            const { app } = shell;
            if (!app.capabilities.similarity) {
                h.assert(document.querySelector(".pv-filter-toggle") === null, "标准版仍创建相似聚类开关");
                h.assert(document.querySelector(".pv-sim") === null, "标准版仍创建相似面板");
                h.assert(app.state.similarity.visible === false, "标准版相似面板不可见");
                h.assert(app.state.layout.similarity.occupation === 0, "标准版相似面板仍占位");
                h.assert(await app.extractAll() === false, "标准版仍可批量提取");
                return;
            }
            const before = app.state.layout.panes.filePane.w;
            const counts = app.state.count;
            const sortKey = app.state.sortKey;
            h.click(h.dom(".pv-filter-toggle"));
            await h.until(() => app.state.similarity.visible === true, 4000);
            await h.tick(80);
            const sim = h.dom(".pv-sim");
            h.assert(sim.hidden === false, "相似面板未显示");
            // SIM-07：列布局文件栏加宽 = 面板宽 + sim.panel.margin；模型与实际 DOM 同时核对。
            const simRect = sim.getBoundingClientRect();
            const expectedWidth = before + shell.t("sim.panel.width") + shell.t("sim.panel.margin");
            h.assert(Math.abs(app.state.layout.panes.filePane.w - expectedWidth) <= 1,
                `文件栏宽度 ${app.state.layout.panes.filePane.w} ≠ ${expectedWidth}`);
            h.assert(Math.abs(simRect.width - app.state.layout.similarity.width) <= 1,
                `DOM 面板宽 ${simRect.width} ≠ 布局 ${app.state.layout.similarity.width}`);
            h.assert(app.state.count === counts, "打开面板改变了列表");
            h.assert(app.state.displayedId === app.state.anchorId, "打开面板改变了当前照片");
            h.assert(app.state.sortKey === sortKey, "打开面板改变了排序");
            await h.until(() => app.state.similarity.items.length > 0 && app.state.similarity.calculating === false, 4000);
            h.assert(h.domAll(".pv-sim-item").length === app.state.similarity.items.length, "相似列表与状态不一致");
            const anchorId = app.state.anchorId;
            const self = app.state.similarity.items.find((item) => item.card.photo.id === anchorId
                || item.card.companions.some((photo) => photo.id === anchorId));
            h.assert(!self, `相似列表包含锚点自身：${self ? self.card.displayName : ""}`);
            const scores = app.state.similarity.items.map((item) => item.score);
            h.assert(scores.every((score, index) => index === 0 || score <= scores[index - 1] + 1e-9), "相似列表未按分数降序");
            // SIM-08：阈值与百分比都按归一分数 (cos+1)/2。
            h.assert(scores.every((score) => score >= 0.85 && score <= 1), `相似列表分数越界：${scores.join(",")}`);
            h.assert(app.state.similarity.items.every((item) => item.percent === Math.round(item.score * 100)), "百分比不是归一分数");
        },
    },
    {
        id: "SIM-07",
        title: "列布局面板与胶片栏同顶到文件栏底，行布局高 159、宽含停靠区",
        async run(shell, h) {
            const { app } = shell;
            if (!app.capabilities.similarity) {
                h.assert(document.querySelector(".pv-sim") === null, "标准版仍创建相似面板");
                h.assert(app.state.layout.panes.filePane.w === shell.t("shell.filebar.content.width"), "标准版文件栏为相似面板让位");
                return;
            }
            h.click(h.dom(".pv-filter-toggle"));
            await h.until(() => app.state.similarity.visible === true, 4000);
            await h.until(() => app.state.similarity.items.length > 0 && app.state.similarity.calculating === false, 4000);
            await h.tick(80);

            const file = app.state.layout.panes.filePane;
            const panel = app.state.layout.similarity.panel;
            const filter = app.state.layout.filter;
            const windowRect = h.dom(".pv-window").getBoundingClientRect();
            const simRect = h.dom(".pv-sim").getBoundingClientRect();
            const domPanel = {
                x: simRect.left - windowRect.left,
                y: simRect.top - windowRect.top,
                w: simRect.width,
                h: simRect.height,
            };
            h.assert(panel.x === shell.t("shell.filebar.content.width"), `列布局面板未接在 116 宽文件栏右侧：${panel.x}`);
            h.assert(panel.y === filter.bar.h, `列布局面板未与胶片栏同顶：${panel.y} ≠ ${filter.bar.h}`);
            h.assert(panel.y + panel.h === file.y + file.h, `列布局面板未到文件栏底：${panel.y + panel.h} ≠ ${file.y + file.h}`);
            h.assert(file.w === shell.t("shell.filebar.content.width") + panel.w + shell.t("sim.panel.margin"),
                `文件栏加宽不含外边距：${file.w}`);
            h.assert(Math.abs(domPanel.x - panel.x) <= 1 && Math.abs(domPanel.y - panel.y) <= 1,
                `列布局 DOM 面板位置 ${domPanel.x},${domPanel.y} ≠ 布局 ${panel.x},${panel.y}`);
            h.assert(Math.abs(domPanel.w - panel.w) <= 1 && Math.abs(domPanel.h - panel.h) <= 1,
                `列布局 DOM 面板尺寸 ${domPanel.w}×${domPanel.h} ≠ 布局 ${panel.w}×${panel.h}`);

            shell.setWindowSize(700, 850);
            await h.tick(160);
            h.assert(app.state.layout.layoutMode === "row", "700×850 未进入行布局");
            const rowFile = app.state.layout.panes.filePane;
            const rowPanel = app.state.layout.similarity.panel;
            const rowRect = h.dom(".pv-sim").getBoundingClientRect();
            const rowDom = {
                x: rowRect.left - windowRect.left,
                y: rowRect.top - windowRect.top,
                w: rowRect.width,
                h: rowRect.height,
            };
            h.assert(Math.abs(rowPanel.h - shell.t("sim.list.cross")) <= 1, `行布局面板高 ${rowPanel.h} ≠ ${shell.t("sim.list.cross")}`);
            h.assert(rowPanel.w <= 700 * shell.t("sim.panel.maxWidthRatio") + 1,
                `行布局面板宽 ${rowPanel.w} 超过可用宽一半`);
            h.assert(Math.abs((rowFile.x + rowFile.w) - (rowPanel.x + rowPanel.w) - shell.t("sim.panel.margin")) <= 1,
                "行布局面板右侧未留外边距");
            h.assert(Math.abs(rowDom.x - rowPanel.x) <= 1 && Math.abs(rowDom.y - rowPanel.y) <= 1
                && Math.abs(rowDom.w - rowPanel.w) <= 1 && Math.abs(rowDom.h - rowPanel.h) <= 1,
                `行布局 DOM 面板 ${rowDom.x},${rowDom.y},${rowDom.w}×${rowDom.h} ≠ 布局 ${rowPanel.x},${rowPanel.y},${rowPanel.w}×${rowPanel.h}`);
            h.assert(Math.abs(app.state.layout.similarity.width - rowPanel.w) <= 1, "行布局面板宽与布局不一致");
            h.assert(app.state.layout.similarity.dockWidth >= shell.t("sim.action.width") + 2 * shell.t("sim.dock.margin"),
                `停靠区宽 ${app.state.layout.similarity.dockWidth} 未含动作区`);
            shell.setWindowSize(1200, 850);
            await h.tick(120);
        },
    },
    {
        id: "SIM-05",
        title: "点相似卡片切主图，胶片栏高亮留在锚点",
        async run(shell, h) {
            const { app } = shell;
            if (!app.capabilities.similarity) {
                h.assert(h.domAll(".pv-sim-item").length === 0, "标准版仍渲染相似项");
                h.assert(app.state.displayedId === app.state.anchorId, "标准版相似入口改变了主图");
                return;
            }
            h.click(h.dom(".pv-filter-toggle"));
            await h.until(() => app.state.similarity.items.length > 0 && app.state.similarity.calculating === false, 4000);
            await h.tick(80);
            const anchor = app.state.anchorId;
            const anchorKey = h.cardByPhotoId(anchor).key;
            const target = h.domAll(".pv-sim-item .pv-card")[0];
            const targetKey = target.dataset.key;
            h.click(target);
            await h.tick(80);
            h.assert(app.state.displayedId !== anchor, "点相似卡片未切换主图");
            h.assert(app.state.anchorId === anchor, "点相似卡片改变了锚点");
            h.assert(h.currentFilmKey() === anchorKey, "胶片栏高亮离开了锚点");
            h.assert(targetKey === h.cardByPhotoId(app.state.displayedId).key, "相似卡片主图身份不符");
        },
    },
    {
        id: "SIM-03",
        title: "提取全部显示进度并完成，计数收敛",
        async run(shell, h) {
            const { app } = shell;
            if (!app.capabilities.similarity) {
                const state = h.harborPhotos().map((photo) => photo.extracted);
                h.assert(await app.extractAll() === false, "标准版仍可批量提取");
                h.assert(h.harborPhotos().every((photo, index) => photo.extracted === state[index]), "标准版批量提取写入了特征库");
                h.assert(app.state.similarity.progress === null, "标准版出现提取进度");
                return;
            }
            h.click(h.dom(".pv-filter-toggle"));
            await h.until(() => app.state.similarity.visible === true, 4000);
            await h.until(() => app.state.similarity.unextracted > 0 || app.state.similarity.items.length > 0, 4000);
            const photoState = h.harborPhotos().map((photo) => [photo, photo.extracted]);
            try {
                const action = h.dom(".pv-sim-action");
                h.assert(action.hidden === false, "提取按钮不可见");
                h.assert(action.textContent === "补齐全部" || action.textContent === "提取全部", `提取按钮文案不符：${action.textContent}`);
                h.click(action);
                await h.until(() => app.state.similarity.progress !== null, 2000);
                const progress = h.dom(".pv-sim-progress");
                h.assert(progress.hidden === false, "提取进度未显示");
                await h.until(() => app.state.similarity.progress === null, 30000, 80);
                h.assert(progress.hidden === true, "提取完成后进度未收起");
                h.assert(app.state.similarity.unextracted === 0, `提取完成后仍有 ${app.state.similarity.unextracted} 张未提取`);
            } finally {
                for (const [photo, value] of photoState) photo.extracted = value;
            }
        },
    },
    {
        id: "EDITION-02",
        title: "版本入口差异：标准版四项筛选与 4/5 瓦片，AI 版五项与 6/7 瓦片",
        async run(shell, h) {
            const { app } = shell;
            const dino = app.capabilities.dino;
            const focusPhoto = h.harborPhoto((entry) => entry.focus);
            app.selectCard(focusPhoto.id);
            await h.tick(60);
            await h.openMainMenu();
            h.click(h.menuItem("analysis"));
            const expected = dino ? 7 : 5;
            await h.until(() => app.state.panes.analysis
                && app.state.analysis.tiles.length === expected
                && h.domAll(".pv-analysis-tile").length === expected, 5000);
            h.deepEqual(app.state.analysis.tiles.map((tile) => tile.kind), dino
                ? ["focus", "center", "histogram", "pca", "cosine", "sharpness", "shake"]
                : ["focus", "center", "histogram", "sharpness", "shake"], "版本瓦片构成不符");

            const toggle = dino ? h.dom(".pv-filter-toggle") : document.querySelector(".pv-filter-toggle");
            if (dino) {
                h.assert(toggle !== null, "AI 版缺少相似聚类开关");
                h.assert(h.dom(".pv-sim") !== null, "AI 版缺少相似面板容器");
            } else {
                h.assert(toggle === null, "标准版仍创建相似聚类开关");
                h.assert(document.querySelector(".pv-sim") === null, "标准版仍创建相似面板");
                h.assert(app.state.layout.filter.toggle === null, "标准版筛选条仍留开关槽位");
                h.assert(app.state.analysis.dino === false, "标准版分析栏仍声明 DINO 能力");
            }
        },
    },
    {
        id: "EDITION-03",
        title: "即时计算 DINO/CV 分侧门控，另一侧保持未提取",
        async run(shell, h) {
            const { app } = shell;
            const photo = h.harborPhoto((entry) => entry.extracted !== true);
            h.assert(photo, "harbor 中找不到未提取照片");
            app.selectCard(photo.id);
            await h.tick(60);
            await h.openMainMenu();
            h.click(h.menuItem("analysis"));
            const tileState = (kind) => app.state.analysis.tiles.find((tile) => tile.kind === kind)?.state;
            const waitState = (kind, state) => h.until(() => tileState(kind) === state, 8000, 60);

            app.updateSettings({ analysisImmediateDinoCompute: false, analysisImmediateCvCompute: false });
            await waitState("sharpness", "empty");
            h.assert(tileState("sharpness") === "empty", "两侧关闭时 CV 未保持未提取");

            app.updateSettings({ analysisImmediateCvCompute: true });
            await waitState("sharpness", "content");
            h.assert(tileState("sharpness") === "content", "只开 CV 时锐度未现算");
            if (app.capabilities.dino) {
                h.assert(tileState("pca") === "empty", "只开 CV 时 DINO 被顺带现算");
                app.updateSettings({ analysisImmediateDinoCompute: true, analysisImmediateCvCompute: false });
                await waitState("pca", "content");
                await waitState("sharpness", "empty");
                h.assert(tileState("pca") === "content", "只开 DINO 时 PCA 未现算");
                h.assert(tileState("sharpness") === "empty", "只开 DINO 时 CV 被顺带现算");
            } else {
                h.assert(tileState("pca") === undefined, "标准版出现 DINO 瓦片");
                h.assert(tileState("cosine") === undefined, "标准版出现 Cosine 瓦片");
            }
            h.assert(photo.extracted !== true, "分析栏现算写入了特征库");
        },
    },
    {
        id: "EDITION-06",
        title: "即时计算字段拆分持久化，标准版保留隐藏的 DINO/相似值",
        async run(shell, h) {
            const { app } = shell;
            const before = JSON.parse(localStorage.getItem("PhotoViewer.Settings") ?? "null") ?? {};
            if (!app.capabilities.similarity) {
                app.updateSettings({ analysisImmediateCvCompute: true });
            } else {
                app.updateSettings({ analysisImmediateDinoCompute: true, analysisImmediateCvCompute: false });
            }
            const saved = JSON.parse(localStorage.getItem("PhotoViewer.Settings"));
            h.assert("analysisImmediateCompute" in saved === false, "仍写出旧组合字段");
            h.assert(typeof saved.analysisImmediateCvCompute === "boolean", "缺少独立 CV 字段");
            h.assert(typeof saved.analysisImmediateDinoCompute === "boolean", "缺少独立 DINO 字段");
            if (!app.capabilities.similarity) {
                h.assert(saved.analysisImmediateDinoCompute === (before.analysisImmediateDinoCompute ?? app.settings.analysisImmediateDinoCompute),
                    "标准版改写了不可见 DINO 开关");
                if ("similarityThreshold" in before) {
                    h.assert(saved.similarityThreshold === before.similarityThreshold, "标准版改写了相似阈值");
                }
                if ("similarityPanelExpanded" in before) {
                    h.assert(saved.similarityPanelExpanded === before.similarityPanelExpanded, "标准版改写了相似展开态");
                }
            }
        },
    },
    {
        id: "OPEN-01",
        title: "菜单「打开文件夹…」选择器打开、应用与取消",
        async run(shell, h) {
            const { app } = shell;
            const beforeCount = app.state.count;
            await h.openMainMenu();
            h.click(h.menuItem("open-folder"));
            await h.until(() => h.dom(".pv-picker").hidden === false, 3000);
            h.assert(h.dom(".pv-picker-title").textContent === "打开文件夹", "选择器标题不符");
            h.click(h.dom('.pv-picker-folder[data-id="night"]'));
            h.click(h.dom(".pv-picker-button.is-primary"));
            await h.until(() => app.state.folder && app.state.folder.id === "night", 4000);
            h.assert(app.state.count !== beforeCount, "打开文件夹后计数未变化");
            h.assert(app.state.viewport.isFit === true, "打开文件夹后未回到适应");
            const nightCount = app.state.count;

            await h.openMainMenu();
            h.click(h.menuItem("open-folder"));
            await h.until(() => h.dom(".pv-picker").hidden === false, 3000);
            h.click(h.domAll(".pv-picker-button").find((node) => !node.classList.contains("is-primary")));
            await h.tick(80);
            h.assert(h.dom(".pv-picker").hidden === true, "取消后选择器未关闭");
            h.assert(app.state.folder.id === "night" && app.state.count === nightCount, "取消改变了文件夹或列表");
        },
    },
    {
        id: "SHELL-01",
        title: "主图铺满窗口、独立适应区；行/列分区与栏位隐藏释放正确",
        async run(shell, h) {
            const { app } = shell;
            const t = (name) => shell.t(name);
            shell.setWindowSize(1200, 850);
            await h.tick(100);
            const col = app.state.layout;
            h.assert(col.layoutMode === "col", "1200×850 未取列布局");
            h.deepEqual(col.panes.photoView, { x: 0, y: 0, w: 1200, h: 850 });
            h.deepEqual(col.panes.fitRect, {
                x: t("shell.filebar.content.width") + t("shell.divider.thickness"),
                y: 0,
                w: 1200 - t("shell.filebar.content.width") - t("shell.divider.thickness")
                    - col.panes.controlPane.w - t("shell.divider.thickness"),
                h: 850,
            });
            h.assert(col.film.scroll.w === t("shell.filebar.content.width") && col.film.scroll.h === 850,
                "列布局胶片列表没有纵贯文件栏");
            h.assert(col.filter.composite.w === t("film.card.width"), "列布局筛选合体宽不符");

            shell.setWindowSize(700, 850);
            await h.tick(100);
            const row = app.state.layout;
            h.assert(row.layoutMode === "row", "700×850 未取行布局");
            const filterBand = t("shell.glass.button.size") + 2 * t("filter.margin");
            h.assert(row.panes.filePane.h === filterBand + t("film.band.height"), `行布局文件栏高 ${row.panes.filePane.h}`);
            h.assert(row.film.scroll.y === filterBand, `行布局胶片带 y ${row.film.scroll.y}`);
            h.assert(row.panes.fitRect.y === filterBand + t("film.band.height") + 1, "行布局适应区未从胶片带下缘开始");

            app.toggleFilmbar();
            await h.tick(80);
            h.assert(app.state.layout.panes.filePane.h === 0 && app.state.layout.filter === null, "关闭文件栏未释放筛选与列表");
            h.assert(app.state.layout.panes.fitRect.y === 0, "关闭文件栏未把空间还给适应区");
            app.toggleFilmbar();
            app.toggleControlbar();
            await h.tick(80);
            h.assert(app.state.layout.panes.fitRect.y + app.state.layout.panes.fitRect.h === 850,
                "关闭控制栏未把空间还给适应区");
            app.toggleControlbar();
            await h.tick(80);
        },
    },
    {
        id: "SHELL-07",
        title: "自动按 CSS 视口宽 <600 映射紧凑横向，显式行/列恒为宽形态，手机模式可检验",
        async run(shell, h) {
            const { app } = shell;
            shell.setWindowSize(590, 800);
            await h.tick(80);
            h.assert(app.state.layout.form === "phone" && app.state.layout.orientation === "portrait",
                `590×800 auto 未进入手机竖屏：${app.state.layout.form}/${app.state.layout.orientation}`);

            shell.setWindowSize(600, 800);
            await h.tick(80);
            h.assert(app.state.layout.form === "wide" && app.state.layout.layoutMode === "row",
                "600×800 auto 未回到宽形态");

            app.updateSettings({ layoutMode: "row" });
            shell.setWindowSize(390, 844);
            await h.tick(80);
            h.assert(app.state.layout.form === "wide" && app.state.layout.layoutMode === "row", "显式行随窄屏变成手机形态");

            app.updateSettings({ layoutMode: "phone" });
            shell.setWindowSize(844, 390);
            await h.tick(80);
            h.assert(app.state.layout.form === "phone" && app.state.layout.orientation === "landscape", "手机模式横屏未生效");
            h.assert(app.state.layout.panes.sideColumn !== null, "手机横屏缺少刘海侧列");
        },
    },
    {
        id: "SHELL-08",
        title: "手机竖屏：顶行/分析/主图/胶片/底行、开关各管各栏、44 命中",
        async run(shell, h) {
            const { app } = shell;
            const t = (name) => shell.t(name);
            app.updateSettings({ layoutMode: "phone" });
            shell.setSafeArea({ top: 59, right: 0, bottom: 34, left: 0 });
            shell.setWindowSize(390, 844);
            await h.tick(120);
            const layout = app.state.layout;
            h.assert(layout.orientation === "portrait", "竖屏排布未生效");
            h.assert(layout.filter.entry.x === t("shell.phone.edgeMargin"), `筛选入口左缘 ${layout.filter.entry.x}`);
            h.assert(layout.filter.entry.y === 59, `筛选入口顶边 ${layout.filter.entry.y}`);
            h.assert(layout.filter.more.x + layout.filter.more.w === 390 - t("shell.phone.edgeMargin"), "更多入口未贴右侧");
            h.assert(layout.nav.visible === false, "竖屏不应有刘海侧上下张");
            const slots = layout.control.slots;
            h.assert(slots.previous && slots.next && slots.rating && slots.fit && slots.enhance, "竖屏底部行槽位不全");
            h.assert(slots.rating.h === 44 && slots.rating.w >= t("rating.compact.minLength"), "竖屏评分槽不符合 44 厚/紧凑下限");
            h.assert(layout.filter.toggle === null, "手机形态出现 AI 相似开关");
            const navCapsule = h.dom(".pv-control-capsule.is-nav");
            const utilityCapsule = h.dom(".pv-control-capsule.is-utility");
            const capsuleStyle = getComputedStyle(navCapsule);
            const navRect = navCapsule.getBoundingClientRect();
            const utilityRect = utilityCapsule.getBoundingClientRect();
            h.assert(Math.round(navRect.width) === 88 && Math.round(navRect.height) === 44,
                `竖屏上下张合体 ${navRect.width}×${navRect.height}`);
            h.assert(Math.round(utilityRect.width) === 88 && Math.round(utilityRect.height) === 44,
                `竖屏缩放增强合体 ${utilityRect.width}×${utilityRect.height}`);
            for (const capsule of [navCapsule, utilityCapsule]) {
                const segments = [...capsule.querySelectorAll(".pv-control-command-button")];
                h.assert(segments.length === 2, `合体段数 ${segments.length} ≠ 2`);
                for (const segment of segments) {
                    const rect = segment.getBoundingClientRect();
                    h.assert(Math.round(rect.width) === 44 && Math.round(rect.height) === 44,
                        `合体段 ${rect.width}×${rect.height}`);
                    h.assert(getComputedStyle(segment).backgroundColor === "rgba(0, 0, 0, 0)", "合体段自绘底色");
                    h.assert(getComputedStyle(segment).borderTopWidth === "0px", "合体段自绘描边");
                }
            }
            h.assert(capsuleStyle.backgroundColor !== "rgba(0, 0, 0, 0)", "胶囊宿主无背景");
            h.assert(capsuleStyle.overflow === "hidden", "胶囊宿主未裁切段");

            app.toggleFilmbar();
            await h.tick(80);
            h.assert(app.state.layout.filter.entry !== null, "关闭胶片栏把筛选入口一起隐藏了");
            const entryRect = h.dom(".pv-filter-entry").getBoundingClientRect();
            h.assert(Math.round(entryRect.width) === 44 && Math.round(entryRect.height) === 44,
                `关闭胶片栏后筛选入口 DOM ${entryRect.width}×${entryRect.height}`);
            h.assert(app.state.layout.control.slots.previous, "关闭胶片栏后上下张被误隐藏");
            h.assert(!h.dom(".pv-control-capsule.is-nav").hidden, "关闭胶片栏后上下张合体被误隐藏");
            app.toggleFilmbar();
            await h.tick(80);

            app.toggleControlbar();
            await h.tick(80);
            h.assert(app.state.layout.control.slots === undefined || app.state.layout.control.slots.rating === undefined,
                "关闭控制栏后底部行仍占槽");
            h.assert(h.dom(".pv-control-capsule.is-nav").hidden
                && h.dom(".pv-control-capsule.is-utility").hidden,
                "关闭控制栏后竖屏合体仍残留");
            app.toggleControlbar();
            app.toggleExif();
            await h.tick(80);
            h.assert(app.state.layout.exif.visible === false, "EXIF 开关没有收起 EXIF 块");
            h.assert(app.state.layout.filter.entry !== null, "EXIF 开关误隐藏筛选入口");
            app.toggleExif();
            await h.tick(80);
            shell.setSafeArea(null);
        },
    },
    {
        id: "SHELL-09",
        title: "手机横屏：刘海侧列/主图/分析/控制列，镜像与固定槽位",
        async run(shell, h) {
            const { app } = shell;
            const t = (name) => shell.t(name);
            app.updateSettings({ layoutMode: "phone" });
            if (!app.state.panes.analysis) app.toggleAnalysis();
            shell.setSafeArea({ top: 0, right: 0, bottom: 0, left: 59 });
            shell.setWindowSize(844, 390);
            await h.tick(120);
            const layout = app.state.layout;
            h.assert(layout.orientation === "landscape", "横屏排布未生效");
            h.assert(layout.panes.sideColumn.w >= 59 + 8 + layout.film.cardCross, "刘海侧列宽未容纳安全区与卡片");
            h.assert(layout.filter.composite.w === 88 && layout.filter.entry.w === 44, "横屏筛选不是 88 合体");
            h.assert(layout.filter.toggle === null, "手机横屏出现 AI 相似开关");
            h.assert(layout.nav.visible === true, "横屏上下张缺失");
            h.assert(layout.panes.analysisPane.w > 0 && layout.panes.controlPane.w > 0, "分析/控制列缺失");
            h.assert(layout.control.slots.exif && layout.control.slots.rating, "横屏控制列缺少 EXIF 或评分槽");
            h.assert(layout.control.slots.exif.w === t("toolbar.exif.item.width"), "无衬底 EXIF 容器宽不是 52");
            h.assert(Math.abs(layout.control.slots.exif.x + layout.control.slots.exif.w / 2
                - (layout.control.slots.fit.x + t("shell.glass.button.size") / 2)) <= 0.01,
            "无衬底 EXIF 容器未与按钮列同轴");
            h.assert(layout.panes.controlPane.w === t("shell.glass.button.size") + t("toolbar.padding"),
                `横屏控制列宽 ${layout.panes.controlPane.w}`);
            const exifRect = h.dom(".pv-control-exif-block").getBoundingClientRect();
            const fitRect = h.dom('.pv-control-command-button[data-command="Fit"]').getBoundingClientRect();
            h.assert(Math.abs(exifRect.width - t("toolbar.exif.item.width")) <= 1, `横屏 EXIF DOM 宽 ${exifRect.width}`);
            h.assert(Math.abs((exifRect.left + exifRect.width / 2) - (fitRect.left + fitRect.width / 2)) <= 1,
                "横屏 EXIF DOM 未与按钮同轴");
            const landNav = h.dom(".pv-control-capsule.is-nav");
            const landUtility = h.dom(".pv-control-capsule.is-utility");
            const landNavRect = landNav.getBoundingClientRect();
            const landUtilityRect = landUtility.getBoundingClientRect();
            h.assert(Math.round(landNavRect.width) === 88 && Math.round(landNavRect.height) === 44,
                `横屏上下张合体 ${landNavRect.width}×${landNavRect.height}`);
            h.assert(Math.round(landUtilityRect.width) === 44 && Math.round(landUtilityRect.height) === 88,
                `横屏缩放增强合体 ${landUtilityRect.width}×${landUtilityRect.height}`);
            for (const capsule of [landNav, landUtility]) {
                const segments = [...capsule.querySelectorAll(".pv-control-command-button")];
                h.assert(segments.length === 2, `横屏合体段数 ${segments.length} ≠ 2`);
                for (const segment of segments) {
                    const rect = segment.getBoundingClientRect();
                    h.assert(Math.round(rect.width) === 44 && Math.round(rect.height) === 44,
                        `横屏合体段 ${rect.width}×${rect.height}`);
                }
            }
            app.selectCard(app.state.cards[5].photo.id);
            await h.tick(80);

            app.toggleControlbar();
            await h.tick(100);
            h.assert(app.state.panes.control === false, "未关闭控制栏");
            h.assert(app.state.layout.nav.visible === true, "关闭控制栏把横屏上下张一起隐藏了");
            const prev = h.dom('.pv-control-command-button[data-command="Previous"]');
            h.assert(prev && !prev.hidden, "横屏上下张 DOM 仍被隐藏");
            const prevRect = prev.getBoundingClientRect();
            h.assert(Math.round(prevRect.width) === t("shell.glass.button.size"),
                `横屏上下张 DOM 宽 ${prevRect.width}`);
            const fitOff = h.dom('.pv-control-command-button[data-command="Fit"]');
            const enhanceOff = h.dom('.pv-control-command-button[data-command="ToggleEnhance"]');
            h.assert(fitOff.hidden && enhanceOff.hidden, "关闭控制栏后缩放/增强按钮仍可见");
            h.assert(h.dom(".pv-control-capsule.is-utility").hidden, "关闭控制栏后缩放增强合体未隐藏");
            h.assert(!h.dom(".pv-control-capsule.is-nav").hidden, "关闭控制栏后上下张合体被误隐藏");
            const displayed = app.state.displayedId;
            h.click(prev);
            await h.tick(100);
            h.assert(app.state.displayedId !== displayed,
                `关闭控制栏后横屏上下张不可点：${displayed} -> ${app.state.displayedId}（disabled ${prev.disabled}, cmd ${prev.dataset.command}, rect ${prevRect.left},${prevRect.top}）`);
            app.toggleControlbar();
            await h.tick(80);
            const fitOn = h.dom('.pv-control-command-button[data-command="Fit"]');
            const enhanceOn = h.dom('.pv-control-command-button[data-command="ToggleEnhance"]');
            h.assert(!fitOn.hidden && !enhanceOn.hidden, "重新显示控制栏后缩放/增强按钮未恢复");
            h.assert(!h.dom(".pv-control-capsule.is-utility").hidden, "重新显示控制栏后缩放增强合体未恢复");

            app.toggleFilmbar();
            await h.tick(100);
            h.assert(h.dom(".pv-control-capsule.is-nav").hidden, "横屏关闭胶片栏后上下张合体仍残留");
            h.assert(!h.dom(".pv-control-capsule.is-utility").hidden, "横屏关闭胶片栏后缩放增强合体被误隐藏");
            app.toggleFilmbar();
            await h.tick(100);

            shell.setSafeArea({ top: 0, right: 59, bottom: 0, left: 0 });
            await h.tick(100);
            const mirrored = app.state.layout;
            h.assert(mirrored.panes.sideColumn.x === 844 - mirrored.panes.sideColumn.w, "刘海在右时侧列未镜像");
            h.assert(mirrored.panes.controlPane.x === 0, "刘海在右时控制列未镜像到左侧");
            const windowRect = h.dom(".pv-window").getBoundingClientRect();
            const filterRect = h.dom(".pv-filter-entry").getBoundingClientRect();
            const ratingRect = h.dom(".pv-control-rating").getBoundingClientRect();
            h.assert(Math.abs(filterRect.left - windowRect.left - mirrored.filter.entry.x) <= 1,
                `镜像筛选 DOM x ${filterRect.left - windowRect.left} ≠ ${mirrored.filter.entry.x}`);
            h.assert(Math.abs(ratingRect.left - windowRect.left - 10) <= 1,
                `镜像控制内容未保留窗口侧 10：${ratingRect.left - windowRect.left}`);
            shell.setSafeArea(null);
        },
    },
    {
        id: "SHELL-10/12",
        title: "栏位背景出现条件、唯一外边界、工具区接缝与透明边栏避让",
        async run(shell, h) {
            const { app } = shell;
            const t = (name) => shell.t(name);
            app.updateSettings({ layoutMode: "auto" });
            shell.setSafeArea(null);
            shell.setWindowSize(1200, 850);
            await h.tick(100);
            const wide = app.state.layout;
            h.assert(wide.materials.some((entry) => entry.kind === "file"), "宽形态文件栏缺少材质宿主");
            h.assert(wide.dividers.filter((entry) => entry.kind === "tool").length === 1, "工具区不是唯一外边界");
            h.assert(wide.panes.fitRect.x === t("shell.filebar.content.width") + 1, "适应区未让到文件栏缘");
            const substrate = h.dom(".pv-substrate");
            const substrateStyle = getComputedStyle(substrate);
            h.assert(substrate && substrateStyle.backdropFilter !== "none", "栏底缺少 backdrop-filter");
            h.assert(colorMatches(substrateStyle.backgroundColor, t("color.bar.overlay"), t("shell.bar.overlay.opacity")),
                `栏底色层 ${substrateStyle.backgroundColor}`);
            h.assert(!substrateStyle.backdropFilter.includes("saturate"), "栏底使用了自创饱和度增强");
            h.assert(substrateStyle.boxShadow === "none", "栏底使用了自创高光/内阴影");

            app.updateSettings({ layoutMode: "phone" });
            shell.setSafeArea({ top: 59, right: 0, bottom: 34, left: 0 });
            shell.setWindowSize(390, 844);
            await h.tick(120);
            h.assert(app.state.layout.materials.some((entry) => entry.kind === "bottom"), "竖屏胶片栏缺少底部材质");
            h.assert(!app.state.layout.materials.some((entry) => entry.kind === "top"), "无分析栏时顶部行仍有背景");
            app.toggleAnalysis();
            await h.tick(100);
            h.assert(app.state.layout.materials.some((entry) => entry.kind === "top"), "显示分析栏后顶部带仍无背景");
            h.assert(app.state.layout.panes.fitRect.y >= app.state.layout.panes.analysisPane.y
                + app.state.layout.panes.analysisPane.h, "适应区未避开分析栏");
            app.toggleAnalysis();
            app.toggleFilmbar();
            await h.tick(100);
            h.assert(!app.state.layout.materials.some((entry) => entry.kind === "bottom"), "关闭胶片栏后底部栏仍有背景");
            app.toggleFilmbar();
            await h.tick(80);
            shell.setSafeArea(null);
        },
    },
    {
        id: "SHELL-04",
        title: "栏位显隐与相似开关持久化，排序筛选不持久化",
        async run(shell, h) {
            const { app } = shell;
            await h.openMainMenu();
            h.click(h.menuItem("film"));
            await h.tick(80);
            h.assert(localStorage.getItem("PhotoViewer.Native.ShowFilm") === "false", "文件栏显隐未写入偏好");
            await h.openMainMenu();
            h.click(h.menuItem("film"));
            await h.tick(80);
            h.assert(localStorage.getItem("PhotoViewer.Native.ShowFilm") === "true", "文件栏显隐恢复未写入偏好");

            if (app.capabilities.similarity) {
                h.click(h.dom(".pv-filter-toggle"));
                await h.until(() => JSON.parse(localStorage.getItem("PhotoViewer.Settings")).similarityPanelExpanded === true, 2000);
                h.click(h.dom(".pv-filter-toggle"));
                await h.tick(80);
                h.assert(JSON.parse(localStorage.getItem("PhotoViewer.Settings")).similarityPanelExpanded === false, "相似开关未持久化");
            } else {
                // EDITION-02/06：标准版没有相似开关，隐藏值不因主屏操作被改写。
                h.assert(document.querySelector(".pv-filter-toggle") === null, "标准版仍创建相似开关");
                const stored = JSON.parse(localStorage.getItem("PhotoViewer.Settings") ?? "null");
                h.assert(stored === null || typeof stored.similarityPanelExpanded !== "boolean" || stored.similarityPanelExpanded === app.settings.similarityPanelExpanded,
                    "标准版改写了已存相似展开态");
            }

            await h.openFilter("sort");
            h.click(h.menuItem("size"));
            await h.tick(80);
            h.assert(h.menu() !== null, "选中后筛选菜单被关闭");
            const settings = JSON.parse(localStorage.getItem("PhotoViewer.Settings"));
            h.assert(!("sortKey" in settings) && !("ratingFilter" in settings) && !("ratingFilters" in settings),
                "排序/筛选被写入设置");
            h.assert(app.state.sortKey === "size", "排序未应用");
            h.key("Escape");
            await h.tick(40);
        },
    },
    {
        id: "SHELL-06",
        title: "图标字形只出现在 aria-hidden 文本中，纯字形控件由 aria-label 命名",
        async run(shell, h) {
            const { app } = shell;
            const windowEl = h.dom(".pv-window");

            function isPrivateUse(code) {
                return (code >= 0xE000 && code <= 0xF8FF) || code >= 0xF0000;
            }

            function privateUseOf(text) {
                for (const character of String(text ?? "")) {
                    if (isPrivateUse(character.codePointAt(0))) return character;
                }
                return null;
            }

            // 可读文本：跳过 aria-hidden 子树的文本内容。
            function readableText(node) {
                let text = "";
                for (const child of node.childNodes) {
                    if (child.nodeType === Node.TEXT_NODE) text += child.nodeValue;
                    else if (child.nodeType === Node.ELEMENT_NODE && child.getAttribute("aria-hidden") !== "true") {
                        text += readableText(child);
                    }
                }
                return text.trim();
            }

            function scan() {
                const offenders = [];
                let glyphs = 0;
                const walker = document.createTreeWalker(windowEl, NodeFilter.SHOW_TEXT);
                for (let node = walker.nextNode(); node; node = walker.nextNode()) {
                    const character = privateUseOf(node.nodeValue);
                    if (!character) continue;
                    glyphs += 1;
                    // 字形必须单独落在 aria-hidden 的 span 里，不与可读文字混排。
                    const holder = node.parentElement?.closest('span[aria-hidden="true"]');
                    const alone = holder && [...holder.textContent].every((entry) => isPrivateUse(entry.codePointAt(0)));
                    if (!alone) {
                        offenders.push(`「${character}」@ ${node.parentElement?.className || node.parentElement?.tagName}`);
                    }
                }
                // 纯字形按钮：去掉 aria-hidden 子树后没有文字时，名称必须由 aria-label 提供。
                for (const button of windowEl.querySelectorAll("button")) {
                    if (!button.querySelector(".pv-icon")) continue;
                    if (readableText(button)) continue;
                    if (button.getAttribute("aria-label") || button.getAttribute("aria-labelledby")) continue;
                    offenders.push(`按钮无名称 @ ${button.className}`);
                }
                return { offenders, glyphs };
            }

            let result = scan();
            h.assert(result.glyphs > 0, "主屏没有图标字形，检查无效");
            h.assert(result.offenders.length === 0, `字形进入可读文本：${result.offenders.slice(0, 4).join("；")}`);

            // 菜单勾选字形只在菜单打开时存在。
            await h.openMainMenu();
            result = scan();
            h.assert(result.offenders.length === 0, `菜单字形进入可读文本：${result.offenders.slice(0, 4).join("；")}`);
            h.key("Escape");
            await h.tick(20);

            // 相似面板复用卡片组件；AI 版打开一次确认列表态也满足同一条约束。
            if (app.capabilities.similarity) {
                app.setSimilarityExpanded(true);
                await h.tick(160);
                result = scan();
                h.assert(result.offenders.length === 0, `相似面板字形进入可读文本：${result.offenders.slice(0, 4).join("；")}`);
                app.setSimilarityExpanded(false);
                await h.tick(40);
            } else {
                h.assert(document.querySelector(".pv-sim") === null, "标准版仍创建相似面板");
            }
        },
    },
    {
        id: "TOOLBAR-05b",
        title: "宽形态 EXIF 随控制文档滚动；手机竖屏顶行 EXIF 留在全窗层",
        async run(shell, h) {
            const { app } = shell;
            app.setLayoutModeOverride("col");
            shell.setWindowSize(700, 300);
            await h.tick(120);
            const block = h.dom(".pv-control-exif-block");
            h.assert(block && block.closest(".pv-control-document"), "宽形态 EXIF 不在控制文档内");
            h.assert(block.closest(".pv-exif-layer") === null, "宽形态 EXIF 仍在全窗层");
            const before = block.getBoundingClientRect().top;
            const scroll = h.dom(".pv-control-scroll");
            h.assert(scroll.scrollHeight > scroll.clientHeight, "控制文档未溢出，无法验证滚动");
            scroll.scrollTop = 20;
            await h.tick(40);
            const moved = before - block.getBoundingClientRect().top;
            h.assert(Math.abs(moved - 20) <= 1, `EXIF 未随控制文档滚动：位移 ${moved}`);

            app.setLayoutModeOverride("phone");
            shell.setSafeArea({ top: 59, right: 0, bottom: 34, left: 0 });
            shell.setWindowSize(390, 844);
            await h.tick(120);
            const topBlock = h.dom(".pv-control-exif-block");
            h.assert(topBlock.closest(".pv-exif-layer"), "手机竖屏顶行 EXIF 未回到全窗层");
            h.assert(topBlock.closest(".pv-control-document") === null, "手机竖屏 EXIF 误入控制文档");
            shell.setSafeArea(null);
        },
    },
    {
        id: "INPUT-12",
        title: "DOM 焦点顺序沿文件栏→主图→分析→控制，图层用 z-index 叠放",
        async run(shell, h) {
            const windowEl = h.dom(".pv-window");
            const order = [...windowEl.children].map((node) => node.className);
            const indexOf = (name) => order.findIndex((value) => value.split(" ").includes(name));
            const filter = indexOf("pv-filter-layer");
            const file = indexOf("pv-pane-file");
            const photo = indexOf("pv-pane-photo");
            const analysis = indexOf("pv-pane-analysis");
            const control = indexOf("pv-pane-control");
            const exif = indexOf("pv-exif-layer");
            h.assert(filter >= 0 && file >= 0 && photo >= 0 && analysis >= 0 && control >= 0 && exif >= 0,
                `窗口图层缺失：${order.join(" | ")}`);
            h.assert(filter < file && file < photo && photo < analysis && analysis < control && control < exif,
                `DOM 语义顺序不符：${order.join(" | ")}`);
            h.assert(getComputedStyle(h.dom(".pv-pane-photo")).zIndex === "0", "主图未在最底图层");
            h.assert(getComputedStyle(h.dom(".pv-material-layer")).zIndex === "1", "材质层 z-index 不是 1");
            h.assert(getComputedStyle(h.dom(".pv-pane-file")).zIndex === "2", "文件栏 z-index 不是 2");
            h.assert(getComputedStyle(h.dom(".pv-filter-layer")).zIndex === "3", "筛选层 z-index 不是 3");
            h.assert(getComputedStyle(h.dom(".pv-overlay")).zIndex === "4", "覆盖层 z-index 不是 4");
        },
    },
    {
        id: "SHELL-04b",
        title: "开发参数使用会话布局 override，不写持久设置",
        async run(shell, h) {
            const { app } = shell;
            const stored = () => {
                const raw = localStorage.getItem("PhotoViewer.Settings");
                return raw ? JSON.parse(raw) : null;
            };
            const before = stored()?.layoutMode ?? "auto";
            app.setLayoutModeOverride("phone");
            await h.tick(60);
            h.assert(app.settings.layoutMode === "phone", "会话 override 未生效");
            h.assert((stored()?.layoutMode ?? "auto") === before, "开发参数写入了持久布局设置");
            app.setLayoutModeOverride(before);
            await h.tick(60);

            const filmStored = localStorage.getItem("PhotoViewer.Native.ShowFilm");
            app.toggleFilmbar({ persist: false });
            await h.tick(60);
            h.assert(localStorage.getItem("PhotoViewer.Native.ShowFilm") === filmStored, "开发参数写入了胶片栏偏好");
            app.toggleFilmbar({ persist: false });
            await h.tick(60);
            const controlStored = localStorage.getItem("PhotoViewer.Native.ShowControls");
            app.toggleControlbar({ persist: false });
            await h.tick(60);
            h.assert(localStorage.getItem("PhotoViewer.Native.ShowControls") === controlStored, "开发参数写入了控制栏偏好");
            app.toggleControlbar({ persist: false });
            await h.tick(60);
        },
    },
    {
        id: "FILTER-07@mac",
        title: "mac 配置档：列布局 42 让位、行三段 / 列单胶囊，其余与默认档一致",
        async run(shell, h) {
            const { app } = shell;
            if (shell.t.profile !== "mac") return;
            const t = (name) => shell.t(name);
            app.setLayoutModeOverride("col");
            shell.setWindowSize(1200, 850);
            await h.tick(100);
            const col = app.state.layout;
            h.assert(col.filter.kind === "capsule", `mac 列布局未回退 96 胶囊：${col.filter.kind}`);
            h.assert(col.filter.leadingInset === t("shell.filter.leadingInset"), "mac 列布局未使用 42 让位");
            h.assert(col.filter.composite.w === 96 && col.filter.composite.h === 44,
                `mac 列胶囊 ${col.filter.composite.w}×${col.filter.composite.h}`);
            h.assert(col.filter.toggle.w === 96 && col.filter.toggle.h === 44,
                `mac 列 AI 开关 ${col.filter.toggle.w}×${col.filter.toggle.h}`);
            h.assert(col.filter.bar.h === 148, `mac 列筛选带高 ${col.filter.bar.h}`);
            h.assert(col.exif.variant === "bare", "mac 列布局 EXIF 仍有衬底");
            h.assert(col.exif.frame.w === t("toolbar.exif.item.width"), `mac 纵向 EXIF 容器宽 ${col.exif.frame.w}`);
            h.assert(col.control.paneCross === t("toolbar.exif.item.width") + 2 * t("toolbar.padding"),
                `mac 控制列宽 ${col.control.paneCross}`);
            const windowRect = h.dom(".pv-window").getBoundingClientRect();
            const capsule = h.dom(".pv-filter-entry");
            const capsuleRect = capsule.getBoundingClientRect();
            h.assert(!capsule.hidden && Math.round(capsuleRect.width) === 96 && Math.round(capsuleRect.height) === 44,
                `mac 列胶囊 DOM ${capsuleRect.width}×${capsuleRect.height}`);
            const exifBlock = h.dom(".pv-control-exif-block");
            h.assert(exifBlock.classList.contains("is-bare") && !exifBlock.classList.contains("is-block"),
                "mac EXIF 未使用无衬底变体");

            app.setLayoutModeOverride("row");
            await h.tick(120);
            const rowLayout = app.state.layout;
            h.assert(rowLayout.filter.kind === "segments", "mac 行布局三段缺失");
            h.assert(rowLayout.filter.bar.h === 64, `mac 行筛选带高 ${rowLayout.filter.bar.h}`);
            h.assert(rowLayout.film.scroll.y === 64, `mac 行胶片带 y ${rowLayout.film.scroll.y}`);
            h.assert(rowLayout.filter.toggle.h === 44, "mac 行 AI 开关不是 44 高");
            const sort = h.domAll(".pv-popup.is-segment")[0];
            const sortRect = sort.getBoundingClientRect();
            h.assert(Math.round(sortRect.width) === 90 && Math.round(sortRect.height) === 44,
                `mac 排序控件 DOM ${sortRect.width}×${sortRect.height}`);
            h.assert(Math.abs((sortRect.left - windowRect.left) - rowLayout.filter.controls.sort.x) <= 1,
                `mac 排序控件 DOM x ${sortRect.left - windowRect.left} ≠ 布局 ${rowLayout.filter.controls.sort.x}`);

            shell.setWindowSize(360, 800);
            await h.tick(100);
            h.assert(app.state.layout.filter.kind === "capsule", "mac 窄宽度未回退单胶囊");
            const fallback = h.dom(".pv-filter-entry");
            h.assert(!fallback.hidden && Math.round(fallback.getBoundingClientRect().width) === 96,
                `mac 行单胶囊 DOM ${fallback.getBoundingClientRect().width}`);
            h.assert(h.domAll(".pv-popup.is-segment").every((node) => node.hidden), "mac 单胶囊下三段仍可见");
        },
    },
    {
        id: "SHELL-02@mac",
        title: "默认档与 mac 档的几何差异只在窗口装饰相关节点",
        async run(shell, h) {
            if (shell.t.profile !== "mac") return;
            const t = (name) => shell.t(name);
            const web = createTokens("web");
            const macTokens = createTokens("mac");
            const scene = {
                width: 1200,
                height: 850,
                safeArea: { top: 0, right: 0, bottom: 0, left: 0 },
                showFilmbar: true,
                showControlbar: true,
                showAnalysis: false,
                showSimilarity: true,
                similarityExpanded: false,
                similarityItemCount: 0,
                photoCount: 12,
                filterSegmentWidths: [90, 90, 90],
                exifCount: 4,
                showRating: true,
                commandCount: 6,
                analysisTileCount: 6,
                hasPhoto: true,
            };
            const webLayout = (layoutMode) => computeLayout({ ...scene, layoutMode },
                { t: web, profile: "web", measureText: approximateTextWidth });
            const macLayout = (layoutMode) => computeLayout({ ...scene, layoutMode },
                { t: macTokens, profile: "mac", measureText: approximateTextWidth });

            const row = webLayout("row");
            const macRow = macLayout("row");
            h.deepEqual(Object.keys(macRow.nodes).sort(), Object.keys(row.nodes).sort(), "行布局节点集合不同");
            const rowDiff = Object.keys(row.nodes).filter((name) => JSON.stringify(row.nodes[name]) !== JSON.stringify(macRow.nodes[name]));
            h.deepEqual(rowDiff, [], "行布局在窗口装饰之外出现几何差异");
            h.assert(row.overlays.dragzone === null && macRow.overlays.dragzone !== null, "行布局缺少 mac 拖动区导出");

            const col = webLayout("col");
            const macCol = macLayout("col");
            const decoration = /^(filter\.|filmstrip|card|exif\.)/;
            const colDiff = Object.keys({ ...col.nodes, ...macCol.nodes })
                .filter((name) => JSON.stringify(col.nodes[name]) !== JSON.stringify(macCol.nodes[name]))
                .filter((name) => !decoration.test(name));
            h.deepEqual(colDiff, [], "列布局在筛选让位与窗口装饰之外出现几何差异");
            h.deepEqual(macCol.nodes["root.fitRect"], col.nodes["root.fitRect"], "mac 档改变了主图适应区");
            h.deepEqual(macCol.nodes["root.controlPane"], col.nodes["root.controlPane"], "mac 档改变了控制栏列宽");
            h.deepEqual(macCol.control.groups, col.control.groups, "mac 档改变了控制栏分组几何");
            h.deepEqual(macCol.exif.frame, col.exif.frame, "mac 档改变了 EXIF 几何");
            h.assert(col.filter.leadingInset === t("filter.margin"), "默认档列布局未从外边距起排");
            h.assert(macCol.filter.leadingInset === t("shell.filter.leadingInset"), "mac 列布局未取 42 让位");
            h.assert(macCol.filter.leadingInset - col.filter.leadingInset
                === t("shell.filter.leadingInset") - t("filter.margin"), "让位差值不是标题栏高");
            h.assert(col.overlays.dragzone === null && macCol.overlays.dragzone?.height === t("shell.titlebar.height"),
                "mac 列布局拖动区未取标题栏高");
        },
    },
    {
        id: "RATING-04",
        title: "冲突三角打开来源浮窗：几何、方向、整组写入与关闭条件",
        async run(shell, h) {
            const { app } = shell;
            const t = (name) => shell.t(name);
            const popup = () => document.querySelector(".pv-rating-sources");
            const entries = () => [...document.querySelectorAll(".pv-rating-source")];
            const sourceSignature = (card) => ratingSources(card)
                .map((source) => `${source.kind}:${source.state}:${source.value}`);
            const fileState = (card) => [card.photo, ...card.companions].map((file) => ({
                file,
                rating: file.rating,
                ratingState: file.ratingState,
                sidecar: file.sidecar,
                sidecarRating: file.sidecar?.rating,
                sidecarState: file.sidecar?.state,
            }));
            const restoreFiles = (saved) => {
                for (const item of saved) {
                    item.file.rating = item.rating;
                    item.file.ratingState = item.ratingState;
                    item.file.sidecar = item.sidecar;
                    if (item.sidecar) {
                        item.sidecar.rating = item.sidecarRating;
                        item.sidecar.state = item.sidecarState;
                    }
                }
            };
            const popupRect = () => popup().getBoundingClientRect();

            // 其他检查可能留下尚未完成的整组写入；先等它们落盘，避免目标在用例中途解除冲突。
            await h.tick(700);
            // 冲突组：两个内嵌来源，取值不同；非当前卡片入口不应切主图。
            const cards = app.state.cards;
            const target = cards.find((entry) => hasRatingConflict(entry)
                && ratingSources(entry).length === 2
                && ratingSources(entry).every((source) => source.kind === "embedded" && source.state === "rated"));
            h.assert(target, "harbor 中找不到两来源冲突组");
            // 先把冲突组定位到可见范围，再选相邻卡片为当前项，验证非当前卡片入口。
            app.openFolder("harbor", { target: target.photo.id });
            await h.tick(160);
            let card = h.cardByPhotoId(target.photo.id);
            let node = h.filmCardByKey(card.key);
            h.assert(node, "冲突卡片未渲染");
            const targetIndex = app.state.cards.findIndex((entry) => entry.key === card.key);
            const baseline = app.state.cards[targetIndex + 1] ?? app.state.cards[targetIndex - 1];
            h.assert(baseline, "冲突组旁没有可用卡片");
            app.selectCard(baseline.photo.id);
            await h.tick(120);
            const zero = node.querySelector('.pv-card-star[data-rating="0"]');
            const warningCode = parseInt(t("icon.warning").slice(2), 16);
            const displayed = app.state.displayedId;
            h.assert(displayed !== target.photo.id, "基线卡片仍是冲突组自身");
            const sourcesBefore = sourceSignature(card);

            // RATING-04：卡片 0 号保持槽位几何，换 icon.warning + color.star.conflict。
            h.assert(zero.classList.contains("is-conflict"), "卡片 0 号未换成警示三角");
            h.assert(zero.getBoundingClientRect().width === t("film.card.star.size"),
                `卡片 0 号宽度 ${zero.getBoundingClientRect().width}`);
            const cardWarning = zero.querySelector(".pv-card-warning");
            h.assert(cardWarning.textContent.codePointAt(0) === warningCode, "卡片警示三角字形不是 icon.warning");
            h.assert(getComputedStyle(cardWarning).display !== "none", "卡片警示三角未绘制");
            h.assert(colorMatches(getComputedStyle(cardWarning).color, t("color.star.conflict")),
                `卡片警示三角颜色 ${getComputedStyle(cardWarning).color}`);

            // 卡片入口：只打开浮窗，不写值、不切主图。
            h.click(zero);
            await h.tick(80);
            h.assert(popup(), "点卡片冲突三角未打开来源浮窗");
            h.assert(app.state.displayedId === displayed, "点非当前卡片冲突三角切换了主图");
            h.deepEqual(sourceSignature(h.cardByPhotoId(target.photo.id)), sourcesBefore, "打开浮窗写了评分");

            const contentWidth = t("popup.padding") * 2
                + t("popup.ratingSources.entryPadding") * 2
                + t("rating.control.slot") * 6;
            const entryWidth = contentWidth - t("popup.padding") * 2;
            h.assert(Math.round(popup().clientWidth) === contentWidth,
                `浮窗内容宽 ${popup().clientWidth} ≠ ${contentWidth}`);
            h.assert(getComputedStyle(popup()).borderRadius === `${t("popup.ratingSources.cornerRadius")}px`,
                `浮窗圆角 ${getComputedStyle(popup()).borderRadius}`);
            const entryNodes = entries();
            h.assert(entryNodes.length === ratingSources(card).length,
                `浮窗条目 ${entryNodes.length} ≠ 来源 ${ratingSources(card).length}`);
            for (const entry of entryNodes) {
                const box = entry.getBoundingClientRect();
                h.assert(Math.round(box.width) === entryWidth, `条目宽 ${box.width} ≠ ${entryWidth}`);
                h.assert(Math.round(box.height) === 74, `短名条目高 ${box.height} ≠ 74`);
                h.assert(getComputedStyle(entry).paddingLeft === `${t("popup.ratingSources.entryPadding")}px`,
                    `条目左内边距 ${getComputedStyle(entry).paddingLeft}`);
                h.assert(getComputedStyle(entry).paddingRight === `${t("popup.ratingSources.entryPadding")}px`,
                    `条目右内边距 ${getComputedStyle(entry).paddingRight}`);
                h.assert(getComputedStyle(entry).paddingTop === `${t("popup.ratingSources.entryPadding")}px`,
                    `条目上内边距 ${getComputedStyle(entry).paddingTop}`);
                h.assert(getComputedStyle(entry).paddingBottom === `${t("popup.ratingSources.entryPadding")}px`,
                    `条目下内边距 ${getComputedStyle(entry).paddingBottom}`);
                const stars = [...entry.querySelectorAll(".pv-rating-source-star")];
                h.assert(stars.length === 6, `星排槽位 ${stars.length} ≠ 6`);
                for (const star of stars) {
                    const starBox = star.getBoundingClientRect();
                    h.assert(Math.round(starBox.width) === t("rating.control.slot")
                        && Math.round(starBox.height) === t("rating.control.slot"),
                        `星槽位 ${starBox.width}×${starBox.height}`);
                }
                h.assert(getComputedStyle(entry.querySelector(".pv-rating-source-name")).fontSize
                    === `${t("popup.ratingSources.nameFontSize")}px`, "文件名字号不符");
                h.assert(getComputedStyle(entry.querySelector(".pv-rating-source-status")).fontSize
                    === `${t("popup.ratingSources.metaFontSize")}px`, "状态字号不符");
                h.assert(getComputedStyle(entry.querySelector(".pv-rating-source-badge")).paddingLeft
                    === `${t("popup.ratingSources.badgePaddingX")}px`, "来源标签水平内边距不符");
                h.assert(getComputedStyle(entry.querySelector(".pv-rating-source-badge")).borderRadius
                    === `${t("popup.ratingSources.badgeCornerRadius")}px`, "来源标签圆角不符");
                h.assert(getComputedStyle(entry.querySelector(".pv-rating-source-stars")).marginTop
                    === `${t("popup.ratingSources.labelGap")}px`, "信息行与星排间距不符");
            }
            // 两个短名条目：4 + 74 + 8 + 74 + 4 = 164。
            h.assert(Math.round(popup().clientHeight) === 164, `浮窗内容高 ${popup().clientHeight} ≠ 164`);
            const separator = popup().querySelector(".pv-rating-source-sep");
            h.assert(separator, "条目之间缺少分隔线");
            h.assert(Math.round(separator.getBoundingClientRect().height) === t("popup.ratingSources.dividerHeight"),
                "分隔线高度不符");
            h.assert(getComputedStyle(separator).marginTop
                === `${(t("popup.ratingSources.entrySpacing") - t("popup.ratingSources.dividerHeight")) / 2}px`,
                `分隔线间距 ${getComputedStyle(separator).marginTop}`);

            // 每个来源按自己的 rated 真值点亮，不冒充 0 星。
            const currentSources = ratingSources(card);
            for (let index = 0; index < entryNodes.length; index += 1) {
                const source = currentSources[index];
                const stars = entryNodes[index].querySelectorAll(".pv-rating-source-star");
                for (let value = 1; value < 6; value += 1) {
                    const on = source.state === "rated" && source.value >= value;
                    h.assert(stars[value].classList.contains("is-on") === on,
                        `${source.label} 第 ${value} 星点亮状态不符`);
                }
                const expectedBadge = source.kind === "sidecar"
                    ? "XMP"
                    : String(source.photo.ext ?? "").replace(/^\./, "").toUpperCase();
                h.assert(entryNodes[index].querySelector(".pv-rating-source-badge").textContent === expectedBadge,
                    `来源标签 ${entryNodes[index].querySelector(".pv-rating-source-badge").textContent} ≠ ${expectedBadge}`);
                h.assert(entryNodes[index].querySelector(".pv-rating-source-status").textContent === source.stateLabel,
                    "来源状态文案不符");
                const glyph = entryNodes[index].querySelector('.pv-rating-source-star[data-rating="1"] .pv-rating-source-glyph');
                h.assert(getComputedStyle(glyph).fontSize === `${t("rating.control.glyph.fontSize")}px`,
                    `星形字号 ${getComputedStyle(glyph).fontSize}`);
                const clear = entryNodes[index].querySelector(".pv-rating-source-clear");
                h.assert(getComputedStyle(clear).fontSize === `${t("rating.control.clearGlyph.fontSize")}px`,
                    `清零叉字号 ${getComputedStyle(clear).fontSize}`);
                h.assert(colorMatches(getComputedStyle(clear).color, t("color.star.clear")), "清零叉颜色不符");
                const one = entryNodes[index].querySelector('.pv-rating-source-star[data-rating="1"]');
                const on = source.state === "rated" && source.value >= 1;
                h.assert(colorMatches(getComputedStyle(one).color, on ? t("color.star.on") : t("color.star.off")),
                    `星形颜色不符：${getComputedStyle(one).color}`);
            }

            // 打开期间外部来源变化：不关闭，按最新来源刷新全部条目。
            const externalSource = currentSources[1];
            const externalOriginal = externalSource.photo.rating;
            let externalNext = externalOriginal === 2 ? 1 : 2;
            if (externalNext === currentSources[0].value) externalNext = externalNext === 1 ? 3 : 1;
            externalSource.photo.rating = externalNext;
            app.setSort(app.state.sortKey, app.state.sortOrder);
            await h.tick(140);
            h.assert(popup(), "外部来源变化关闭了浮窗");
            const refreshedStars = entries()[1].querySelectorAll(".pv-rating-source-star");
            for (let value = 1; value < 6; value += 1) {
                h.assert(refreshedStars[value].classList.contains("is-on")
                    === (externalSource.photo.rating >= value), `外部变化后第 ${value} 星未刷新`);
            }
            externalSource.photo.rating = externalOriginal;
            app.setSort(app.state.sortKey, app.state.sortOrder);
            await h.tick(140);

            // 卡片默认向下，以真实 0 号按钮为锚点并保留 arrowMargin。
            const zeroBox = zero.getBoundingClientRect();
            const hostBox = h.dom(".pv-window").getBoundingClientRect();
            h.assert(popup().dataset.direction === "down", `卡片展开方向 ${popup().dataset.direction}`);
            h.assert(Math.abs(popupRect().top - zeroBox.bottom - t("popup.ratingSources.arrowMargin")) <= 0.5,
                `卡片 0 号与浮窗间距 ${popupRect().top - zeroBox.bottom}`);
            h.assert(popupRect().left >= hostBox.left - 0.5 && popupRect().right <= hostBox.right + 0.5
                && popupRect().top >= hostBox.top - 0.5 && popupRect().bottom <= hostBox.bottom + 0.5,
                "浮窗未夹在宿主内");

            // 重复点同一入口只有一层；Esc 关闭。
            h.click(zero);
            await h.tick(80);
            h.assert(document.querySelectorAll(".pv-rating-sources").length === 1, "重复打开出现多层浮窗");
            h.key("Escape");
            await h.tick(60);
            h.assert(popup() === null, "Esc 未关闭浮窗");

            // 横排胶片默认向下。
            shell.setWindowSize(700, 850);
            await h.tick(160);
            app.openFolder("harbor", { target: target.photo.id });
            await h.tick(160);
            node = h.filmCardByKey(h.cardByPhotoId(target.photo.id).key);
            h.click(node.querySelector('.pv-card-star[data-rating="0"]'));
            await h.tick(80);
            h.assert(popup()?.dataset.direction === "down", `横排胶片展开方向 ${popup()?.dataset.direction}`);
            h.key("Escape");
            await h.tick(40);
            shell.setWindowSize(1200, 850);
            await h.tick(160);

            // 横排控制栏：向上；整组写入成功后冲突解除才关闭。
            shell.setWindowSize(700, 850);
            await h.tick(160);
            app.openFolder("harbor", { target: target.photo.id });
            await h.tick(160);
            h.assert(app.state.layout.control.axis === "horizontal", "700×850 不是横排控制栏");
            let saved = fileState(h.cardByPhotoId(target.photo.id));
            let controlZero = h.ratingButton(0);
            h.assert(controlZero.classList.contains("is-conflict"), "控制栏 0 号未换成警示三角");
            h.assert(Math.round(controlZero.getBoundingClientRect().width) === t("rating.control.slot"),
                `控制栏 0 号宽度 ${controlZero.getBoundingClientRect().width}`);
            const controlWarning = controlZero.querySelector(".pv-rating-conflict");
            h.assert(controlWarning.textContent.codePointAt(0) === warningCode, "控制栏警示三角字形不是 icon.warning");
            h.assert(getComputedStyle(controlWarning).fontSize === `${t("rating.control.glyph.fontSize")}px`,
                `控制栏警示三角字号 ${getComputedStyle(controlWarning).fontSize}`);
            h.assert(colorMatches(getComputedStyle(controlWarning).color, t("color.star.conflict")),
                `控制栏警示三角颜色 ${getComputedStyle(controlWarning).color}`);
            let controlZeroBox = controlZero.getBoundingClientRect();
            const displayedBeforeWrite = app.state.displayedId;
            h.click(controlZero);
            await h.tick(80);
            h.assert(popup(), "点控制栏冲突三角未打开来源浮窗");
            h.assert(popup().dataset.direction === "up", `横排控制栏展开方向 ${popup().dataset.direction}`);
            h.assert(Math.abs(controlZeroBox.top - popupRect().bottom
                - t("popup.ratingSources.arrowMargin")) <= 0.5,
                `控制栏 0 号与浮窗间距 ${controlZeroBox.top - popupRect().bottom}`);
            h.assert(app.state.displayedId === displayedBeforeWrite, "打开控制栏浮窗切换了主图");

            h.click(entries()[0].querySelector('.pv-rating-source-star[data-rating="3"]'));
            await h.until(() => popup() === null, 5000);
            const written = h.cardByPhotoId(target.photo.id);
            h.assert(!hasRatingConflict(written), `整组写入成功后仍显示冲突（${sourceSignature(written).join("|")}）`);
            h.assert(ratingSources(written).every((source) => source.state === "rated" && source.value === 3),
                "整组写入未覆盖全部来源");
            h.assert(app.state.displayedId === displayedBeforeWrite, "写入后切换了主图");
            restoreFiles(saved);
            app.openFolder("harbor", { target: target.photo.id });
            await h.tick(160);
            h.assert(hasRatingConflict(h.cardByPhotoId(target.photo.id)), "恢复来源后没有重新形成冲突");

            // 竖排控制栏：向左。
            shell.setWindowSize(1200, 850);
            await h.tick(160);
            h.assert(app.state.layout.control.axis === "vertical", "1200×850 不是竖排控制栏");
            controlZero = h.ratingButton(0);
            controlZeroBox = controlZero.getBoundingClientRect();
            h.click(controlZero);
            await h.tick(80);
            h.assert(popup(), "竖排控制栏未打开来源浮窗");
            h.assert(popup().dataset.direction === "left", `竖排控制栏展开方向 ${popup().dataset.direction}`);
            h.assert(Math.abs(controlZeroBox.left - popupRect().right
                - t("popup.ratingSources.arrowMargin")) <= 0.5,
                `竖排控制栏 0 号与浮窗间距 ${controlZeroBox.left - popupRect().right}`);
            h.assert(Math.abs((popupRect().top + popupRect().bottom) / 2
                - (controlZeroBox.top + controlZeroBox.bottom) / 2) <= 1,
                "竖排浮窗未以 0 号按钮垂直居中");
            h.assert(popupRect().left >= hostBox.left - 0.5, "竖排浮窗越出宿主左侧");
            h.key("Escape");
            await h.tick(60);
            h.assert(popup() === null, "竖排浮窗 Esc 未关闭");

            // 卡片下方不足且上方充足时向上；胶片栏滚动关闭。
            const last = app.state.cards[app.state.cards.length - 1];
            const lastSidecar = last.photo.sidecar;
            last.photo.sidecar = { rating: last.photo.rating === 5 ? 1 : 5 };
            app.openFolder("harbor", { target: last.photo.id });
            await h.tick(160);
            const scroller = h.filmScroller();
            scroller.scrollTop = h.filmMaxOffset("y");
            await h.tick(160);
            const lastNode = h.filmCardByKey(h.cardByPhotoId(last.photo.id).key);
            h.assert(lastNode, "末张冲突卡片未渲染");
            const lastZero = lastNode.querySelector('.pv-card-star[data-rating="0"]');
            const lastBox = lastZero.getBoundingClientRect();
            h.click(lastZero);
            await h.tick(80);
            h.assert(popup(), "末张卡片未打开来源浮窗");
            h.assert(popup().dataset.direction === "up", `卡片展开方向 ${popup().dataset.direction}`);
            h.assert(Math.abs(lastBox.top - popupRect().bottom - t("popup.ratingSources.arrowMargin")) <= 0.5,
                `卡片 0 号与浮窗间距 ${lastBox.top - popupRect().bottom}`);
            scroller.scrollTop = Math.max(0, scroller.scrollTop - 40);
            // 无头 Chrome 不派发脚本改 scrollTop 产生的 scroll 事件，这里显式补发以验证监听接线。
            scroller.dispatchEvent(new Event("scroll"));
            await h.tick(160);
            h.assert(popup() === null, "胶片栏滚动未关闭浮窗");
            last.photo.sidecar = lastSidecar;
            app.openFolder("harbor", { target: target.photo.id });
            await h.tick(160);

            // 布局重排、切图、入口隐藏、菜单/下拉打开时关闭。
            node = h.filmCardByKey(h.cardByPhotoId(target.photo.id).key);
            h.click(node.querySelector('.pv-card-star[data-rating="0"]'));
            await h.tick(120);
            h.assert(popup(), "重排检查前浮窗未打开");
            shell.setWindowSize(1000, 800);
            await h.tick(160);
            h.assert(popup() === null, "布局重排未关闭浮窗");
            shell.setWindowSize(1200, 850);
            await h.tick(160);

            app.openFolder("harbor", { target: target.photo.id });
            await h.tick(160);
            node = h.filmCardByKey(h.cardByPhotoId(target.photo.id).key);
            h.click(node.querySelector('.pv-card-star[data-rating="0"]'));
            await h.tick(80);
            h.assert(popup(), "切图检查前浮窗未打开");
            const otherPhoto = h.harborPhotos().find((photo) => photo.id !== target.photo.id
                && h.state().cards.some((entry) => entry.photo.id === photo.id));
            h.assert(otherPhoto, "找不到可切换的其它照片");
            app.selectCard(otherPhoto.id);
            await h.tick(160);
            h.assert(popup() === null, "切图未关闭浮窗");

            app.openFolder("harbor", { target: target.photo.id });
            await h.tick(160);
            h.click(h.ratingButton(0));
            await h.tick(80);
            h.assert(popup(), "隐藏检查前浮窗未打开");
            app.updateSettings({ showRating: false });
            await h.tick(120);
            h.assert(popup() === null, "入口隐藏未关闭浮窗");
            app.updateSettings({ showRating: true });
            await h.tick(120);

            app.openFolder("harbor", { target: target.photo.id });
            await h.tick(160);
            node = h.filmCardByKey(h.cardByPhotoId(target.photo.id).key);
            h.click(node.querySelector('.pv-card-star[data-rating="0"]'));
            await h.tick(80);
            h.assert(popup(), "下拉检查前浮窗未打开");
            await h.openFilter("sort");
            await h.tick(80);
            h.assert(popup() === null, "下拉打开时浮窗未关闭");
            h.click(node.querySelector('.pv-card-star[data-rating="0"]'));
            await h.tick(80);
            h.assert(popup() === null, "下拉打开时仍打开了浮窗");
            h.key("Escape");
            await h.tick(60);

            // 外部点击关闭；再次打开按最新来源重建。
            app.openFolder("harbor", { target: target.photo.id });
            await h.tick(160);
            node = h.filmCardByKey(h.cardByPhotoId(target.photo.id).key);
            for (let round = 0; round < 3; round += 1) {
                h.click(node.querySelector('.pv-card-star[data-rating="0"]'));
                await h.tick(60);
                h.assert(document.querySelectorAll(".pv-rating-sources").length === 1,
                    `第 ${round + 1} 次打开浮窗层数不为 1`);
                h.pointer(h.dom(".pv-photo-canvas"), "pointerdown", 1, 1);
                await h.tick(60);
                h.assert(popup() === null, `第 ${round + 1} 次外部点击未关闭浮窗`);
            }

            // 长文件名：独占首行、保留首尾中间省略，标签与状态第二行居右，条目高 88。
            const longName = "DSC01200_LONG_FILENAME_FOR_RATING_SOURCES_LAYOUT_CHECK.JPG";
            const longSource = ratingSources(h.cardByPhotoId(target.photo.id))[0];
            const originalName = longSource.photo.name;
            longSource.photo.name = longName;
            app.openFolder("harbor", { target: target.photo.id });
            await h.tick(160);
            node = h.filmCardByKey(h.cardByPhotoId(target.photo.id).key);
            h.click(node.querySelector('.pv-card-star[data-rating="0"]'));
            await h.tick(80);
            const longEntry = entries()[0];
            h.assert(longEntry.classList.contains("is-long"), "长文件名未切换双行");
            h.assert(Math.round(longEntry.getBoundingClientRect().height) === 94,
                `长名条目高 ${longEntry.getBoundingClientRect().height} ≠ 94`);
            const longNameNode = longEntry.querySelector(".pv-rating-source-name");
            const longMeta = longEntry.querySelector(".pv-rating-source-meta");
            h.assert(longNameNode.getAttribute("aria-label") === longName, "长名完整名称未保留在 aria-label");
            h.assert(longNameNode.textContent.includes("…") && longNameNode.textContent.length < longName.length,
                "长名未截断");
            h.assert(!longNameNode.textContent.endsWith("…"), "长名仍是尾部省略");
            h.assert(longNameNode.textContent.startsWith(longName.slice(0, 4))
                && longNameNode.textContent.endsWith(".JPG"), "长名未保留首尾（中间省略）");
            h.assert(longNameNode.scrollWidth <= longNameNode.clientWidth + 1, "长名超出浮窗内容宽");
            h.assert(Math.round(longMeta.getBoundingClientRect().top - longNameNode.getBoundingClientRect().top)
                === 16 + t("popup.ratingSources.metaLineGap"), "长名第二行行距不符");
            h.assert(Math.round(popup().clientHeight) === 184, `长名浮窗内容高 ${popup().clientHeight} ≠ 184`);
            h.key("Escape");
            await h.tick(60);
            longSource.photo.name = originalName;
            app.openFolder("harbor", { target: target.photo.id });
            await h.tick(160);

            // 写入失败：PNG 内嵌不可写，浮窗保留并刷新全部条目。
            const png = app.state.cards.find((entry) => entry.photo.ext === ".png"
                && !entry.photo.sidecar && entry.photo.rating !== 5);
            h.assert(png, "harbor 中找不到可制造冲突的 PNG");
            const pngSidecar = { rating: png.photo.rating === 5 ? 1 : 5 };
            png.photo.sidecar = pngSidecar;
            app.openFolder("harbor", { target: png.photo.id });
            await h.tick(160);
            const pngCard = h.cardByPhotoId(png.photo.id);
            const pngSignature = sourceSignature(pngCard);
            const pngEntryCount = ratingSources(pngCard).length;
            node = h.filmCardByKey(pngCard.key);
            h.assert(node, "PNG 冲突卡片未渲染");
            h.click(node.querySelector('.pv-card-star[data-rating="0"]'));
            await h.tick(80);
            h.assert(entries().length === pngEntryCount, "PNG 浮窗条目数与来源不符");
            h.click(entries()[0].querySelector('.pv-rating-source-star[data-rating="3"]'));
            await h.tick(400);
            h.assert(popup(), "写入失败后浮窗被关闭");
            h.assert(entries().length === pngEntryCount, "写入失败后条目未保留");
            h.deepEqual(sourceSignature(h.cardByPhotoId(png.photo.id)), pngSignature, "写入失败改动了来源状态");
            h.assert(hasRatingConflict(h.cardByPhotoId(png.photo.id)), "写入失败后冲突指示消失");
            const toast = h.dom(".pv-photo-toast");
            h.assert(toast.hidden === false && toast.textContent.includes("无法写入星级"),
                `写入失败提示不可读：${toast.hidden} ${toast.textContent}`);
            h.assert(getComputedStyle(toast).zIndex === "891", `失败提示层级 ${getComputedStyle(toast).zIndex}`);
            h.key("Escape");
            await h.tick(60);
            png.photo.sidecar = null;
            app.openFolder("harbor", { target: baseline.photo.id });
            await h.tick(160);
            h.assert(popup() === null, "检查结束时残留浮窗");
        },
    },
    {
        id: "RATING-04b",
        title: "冲突解除且代表星仍为 0 时，卡片 0 号 aria-pressed 恢复按下",
        async run(shell, h) {
            const { app } = shell;
            const target = app.state.cards.find((entry) => displayStars(entry.photo) === 0
                && !entry.photo.sidecar
                && ratingSources(entry).every((source) => source.kind === "embedded" && source.state === "rated"));
            h.assert(target, "harbor 中找不到 0 星可写组");

            // 模拟外部应用新建同名 sidecar：来源取值不同形成组级冲突，代表星仍为 0。
            target.photo.sidecar = { rating: 3 };
            app.openFolder("harbor", { target: target.photo.id });
            await h.tick(160);
            h.assert(app.state.toolbar.rating.conflict === true, "新增 sidecar 后未标冲突");
            let node = h.filmCardByKey(target.key);
            h.assert(node, "目标卡片未渲染");
            let zero = node.querySelector('.pv-card-star[data-rating="0"]');
            h.assert(zero.classList.contains("is-conflict"), "冲突时卡片 0 号未换成警示三角");
            h.assert(zero.getAttribute("aria-pressed") === "false", "冲突三角仍标记为按下");

            // MENU-05：冲突时卡片菜单「无星级」直接整组写 0，代表星保持 0。
            const rect = node.getBoundingClientRect();
            const x = rect.left + rect.width / 2;
            const y = rect.top + rect.height / 2;
            h.pointer(node, "pointerdown", x, y, { pointerType: "touch" });
            await h.wait(650);
            h.assert(h.menu() !== null, "卡片长按未弹出卡片菜单");
            h.pointer(node, "pointerup", x, y, { pointerType: "touch" });
            h.click(h.menuItem("rate-0"));
            await h.until(() => app.state.toolbar.rating.conflict === false, 4000);
            h.assert(app.state.photo?.rating === 0, "解除冲突后代表星不再是 0");

            node = h.filmCardByKey(target.key);
            zero = node.querySelector('.pv-card-star[data-rating="0"]');
            h.assert(!zero.classList.contains("is-conflict"), "冲突解除后 0 号仍是警示三角");
            h.assert(zero.classList.contains("is-clear"), "冲突解除后 0 号没有恢复清零叉");
            h.assert(zero.getAttribute("aria-pressed") === "true", "冲突解除后 0 号 aria-pressed 未恢复");
            h.assert(getComputedStyle(zero.querySelector(".pv-card-warning")).display === "none",
                "冲突解除后警示三角仍显示");
            h.assert(document.querySelector(".pv-rating-sources") === null, "出现了来源浮窗");
            delete target.photo.sidecar;
        },
    },
    {
        id: "RATING-04c",
        title: "冲突浮窗方向按当前形态/刘海侧：横屏左左、左右、竖屏上",
        async run(shell, h) {
            const { app } = shell;
            const target = app.state.cards.find((card) => hasRatingConflict(card));
            h.assert(target, "harbor 中找不到冲突组");
            const popupDirection = async (expected) => {
                app.openFolder("harbor", { target: target.photo.id });
                await h.tick(160);
                h.click(h.ratingButton(0));
                try {
                    await h.until(() => document.querySelectorAll(".pv-rating-sources").length === 1
                        && document.querySelector(".pv-rating-sources").dataset.direction === expected, 4000);
                } catch (error) {
                    const roots = document.querySelectorAll(".pv-rating-sources");
                    const actual = [...roots].map((root) => root.dataset.direction).join("/");
                    throw new Error(`${error.message}；实际 direction=${actual || "无"}，浮窗数=${roots.length}，形态=${app.state.layout?.form}，刘海=${app.state.layout?.safe?.notchSide}`);
                }
                h.key("Escape");
                await h.until(() => document.querySelectorAll(".pv-rating-sources").length === 0, 4000);
                return expected;
            };
            app.setLayoutModeOverride("phone");
            shell.setSafeArea({ top: 0, right: 0, bottom: 0, left: 59 });
            shell.setWindowSize(844, 390);
            await h.tick(120);
            h.assert(await popupDirection("left") === "left", "刘海在左时浮窗未向左展开");
            shell.setSafeArea({ top: 0, right: 59, bottom: 0, left: 0 });
            await h.tick(100);
            h.assert(app.state.layout.safe.notchSide === "right", `右刘海布局 notchSide=${app.state.layout.safe.notchSide}`);
            const rightDirection = await popupDirection("right");
            h.assert(rightDirection === "right", `刘海在右时浮窗方向 ${rightDirection}`);

            // 同一入口重复打开：保留唯一浮窗，方向仍按当前右刘海。
            app.openFolder("harbor", { target: target.photo.id });
            await h.tick(160);
            h.click(h.ratingButton(0));
            await h.tick(60);
            h.click(h.ratingButton(0));
            await h.tick(60);
            const repeated = document.querySelectorAll(".pv-rating-sources");
            h.assert(repeated.length === 1 && repeated[0].dataset.direction === "right",
                `重复打开浮窗数量 ${repeated.length} / 方向 ${repeated[0]?.dataset.direction}`);
            h.key("Escape");
            await h.tick(40);

            shell.setSafeArea({ top: 59, right: 0, bottom: 34, left: 0 });
            shell.setWindowSize(390, 844);
            await h.tick(120);
            h.assert(await popupDirection("up") === "up", "手机竖屏浮窗未向上展开");
            shell.setSafeArea(null);
        },
    },
    {
        id: "RATING-05",
        title: "筛选：已拒绝独立、无星级含缺失、无效不命中数字档，冲突独立于数字档",
        async run(shell, h) {
            const { app } = shell;
            const cards = app.state.cards;
            const rejected = cards.find((entry) => representativeRating(entry.photo).state === "rejected");
            const singleMissing = cards.find((entry) => ratingSources(entry).length === 1
                && representativeRating(entry.photo).state === "missing");
            const groupMissing = cards.find((entry) => ratingSources(entry).length > 1
                && ratingSources(entry).every((source) => source.state === "missing"));
            const invalid = cards.find((entry) => representativeRating(entry.photo).state === "invalid");
            const conflict = cards.find((entry) => representativeRating(entry.photo).state === "rated"
                && hasRatingConflict(entry));
            h.assert(rejected && singleMissing && groupMissing && invalid && conflict,
                "harbor 缺少已拒绝/单一缺失/多来源缺失/无效/冲突代表");

            const has = (card) => app.state.cards.some((entry) => entry.key === card.key);
            app.setRatingFilter("rejected");
            await h.tick(40);
            h.assert(has(rejected), "已拒绝未命中「已拒绝」");
            h.assert(!has(singleMissing), "缺失命中了「已拒绝」");

            app.setRatingFilter("none");
            await h.tick(40);
            h.assert(has(singleMissing), "代表缺失未命中「无星级」");
            h.assert(has(groupMissing), "多来源缺失未命中「无星级」");
            h.assert(!has(rejected), "已拒绝被并入「无星级」");

            for (const value of ["1", "2", "3", "4", "5"]) {
                app.setRatingFilter(value);
                await h.tick(20);
                h.assert(!has(invalid), `无效代表命中了「${value}」`);
            }

            const representative = representativeRating(conflict.photo);
            app.setRatingFilter(String(representative.value));
            await h.tick(40);
            h.assert(has(conflict), "冲突组未按代表命中数字档");
            app.setRatingFilter("conflict");
            await h.tick(40);
            h.assert(has(conflict), "冲突组未命中「星级冲突」");
            h.assert(has(groupMissing), "多来源缺失未命中「星级冲突」");
            h.assert(!has(singleMissing), "单一缺失来源被误判为「星级冲突」");
            app.setRatingFilter("all");
            await h.tick(40);
        },
    },
    {
        id: "RATING-05b",
        title: "已拒绝来源可整组写 0–5：写后来源为已评分并点亮对应星",
        async run(shell, h) {
            const { app } = shell;
            const target = app.state.cards.find((entry) => representativeRating(entry.photo).state === "rejected"
                && ratingSources(entry).length === 1);
            h.assert(target, "harbor 中找不到单一已拒绝样本");
            app.openFolder("harbor", { target: target.photo.id });
            await h.tick(160);
            const card = h.cardByPhotoId(target.photo.id);
            h.assert(displayStars(card.photo) === 0, "已拒绝代表点亮了星");

            h.click(h.ratingButton(0));
            await h.until(() => app.state.photo.rating === 0, 4000);
            h.assert(ratingSources(card)[0].state === "rated", "写 0 后来源仍是已拒绝");
            h.assert(displayStars(card.photo) === 0, "写 0 后代表星不是 0");

            h.click(h.ratingButton(3));
            await h.until(() => app.state.photo.rating === 3, 4000);
            h.assert(ratingSources(card)[0].state === "rated", "写 3 后来源仍是已拒绝");
            const node = h.filmCardByKey(card.key);
            h.assert(node, "目标卡片未渲染");
            const stars = Array.from(node.querySelectorAll(".pv-card-star"));
            h.assert(stars.slice(1, 4).every((button) => button.classList.contains("is-on")),
                "写 3 后 1–3 号星未点亮");
            h.assert(stars.slice(4).every((button) => !button.classList.contains("is-on")),
                "写 3 后 4–5 号星被点亮");
            h.assert(stars[0].getAttribute("aria-pressed") === "false", "写 3 后 0 号仍标记为按下");
            h.assert(app.state.toolbar.rating.value === 3, "控制栏点亮星与写入值不一致");

            target.photo.rating = -1;
            target.photo.ratingState = "rejected";
        },
    },
    {
        id: "RATING-06",
        title: "无 canonical XMP 一律不新建（含 JPEG），写入预检失败且不改来源",
        async run(shell, h) {
            const { app } = shell;
            const missing = app.state.cards.find((entry) => representativeRating(entry.photo).state === "missing"
                && entry.photo.ext === ".jpg" && ratingSources(entry).length === 1);
            h.assert(missing, "harbor 中找不到缺包 JPEG 样本");
            app.openFolder("harbor", { target: missing.photo.id });
            await h.tick(160);
            const card = h.cardByPhotoId(missing.photo.id);
            const sources = ratingSources(card);
            h.assert(sources.every((source) => source.state === "missing"), "缺包 JPEG 组不是全缺失");
            h.assert(app.state.toolbar.rating.conflict === false, "单一缺失来源被误判为冲突");

            h.click(h.ratingButton(5));

            const toast = h.dom(".pv-photo-toast");
            const expected = `无法写入星级：${missing.photo.name}（无 canonical XMP 包，不支持新建）`;
            await h.until(() => toast.hidden === false && toast.textContent === expected, 4000);
            h.assert(app.state.photo.rating === null, "缺包 JPEG 被写入了评分");
            h.assert(ratingSources(h.cardByPhotoId(missing.photo.id)).every((source) => source.state === "missing"),
                "缺包 JPEG 的来源状态被改写");
            h.assert(h.cardByPhotoId(missing.photo.id).photo.sidecar === null
                || h.cardByPhotoId(missing.photo.id).photo.sidecar === undefined, "写入创建了 sidecar");
        },
    },
    {
        id: "RATING-06b",
        title: "PNG 内嵌来源整体挂账：写入预检失败且零修改",
        async run(shell, h) {
            const { app } = shell;
            const target = app.state.cards.find((entry) => entry.photo.ext === ".png"
                && !hasRatingConflict(entry) && !entry.photo.sidecar);
            h.assert(target, "harbor 中找不到可用的 PNG 样本");
            app.openFolder("harbor", { target: target.photo.id });
            await h.tick(160);
            const card = h.cardByPhotoId(target.photo.id);
            const before = ratingSources(card).map((source) => `${source.state}:${source.value}`);
            const rating = app.state.photo.rating;
            const value = rating === 5 ? 4 : rating + 1;

            h.click(h.ratingButton(value));
            const toast = h.dom(".pv-photo-toast");
            const expected = `无法写入星级：${target.photo.name}（PNG 内嵌 XMP 写入不支持）`;
            await h.until(() => toast.hidden === false && toast.textContent === expected, 4000);
            h.assert(app.state.photo.rating === rating, `PNG 评分被改写：${app.state.photo.rating}`);
            h.assert(app.state.toolbar.rating.value === rating, "控制栏点亮星与磁盘真值不一致");
            const after = ratingSources(h.cardByPhotoId(target.photo.id)).map((source) => `${source.state}:${source.value}`);
            h.deepEqual(after, before, "PNG 来源状态被改写");
            h.assert(h.cardByPhotoId(target.photo.id).photo.sidecar === null
                || h.cardByPhotoId(target.photo.id).photo.sidecar === undefined, "PNG 写入创建了 sidecar");
        },
    },
    {
        id: "RATING-01",
        title: "OPEN-08 重复打开按磁盘真值刷新来源与冲突指示",
        async run(shell, h) {
            const { app } = shell;
            const target = app.state.cards.find((entry) => !hasRatingConflict(entry)
                && entry.photo.sidecar && ratingSources(entry).length >= 3);
            h.assert(target, "harbor 中找不到含 sidecar 的一致组");
            app.openFolder("harbor", { target: target.photo.id });
            await h.tick(160);
            h.assert(app.state.toolbar.rating.conflict === false, "一致组误报冲突");
            app.setRatingFilter("conflict");
            await h.tick(40);
            const before = app.state.count;

            const sidecar = target.photo.sidecar;
            const original = sidecar.rating;
            sidecar.rating = original === 5 ? 1 : 5;
            app.openFolder("harbor", { target: target.photo.id });
            await h.tick(160);
            h.assert(app.state.toolbar.rating.conflict === true, "刷新后未按磁盘真值显示冲突");
            h.assert(app.state.count === before + 1, `刷新后冲突计数 ${app.state.count} ≠ ${before + 1}`);

            sidecar.rating = original;
            app.openFolder("harbor", { target: target.photo.id });
            await h.tick(120);
            h.assert(app.state.toolbar.rating.conflict === false, "恢复来源后仍显示冲突");
            app.setRatingFilter("all");
            await h.tick(40);
        },
    },
    {
        id: "RATING-01b",
        title: "单一来源仅缺失不报冲突：卡片无警示三角，也不命中「星级冲突」",
        async run(shell, h) {
            const { app } = shell;
            const cards = app.state.cards;
            const single = cards.find((entry) => ratingSources(entry).length === 1
                && representativeRating(entry.photo).state === "missing");
            const group = cards.find((entry) => ratingSources(entry).length > 1
                && ratingSources(entry).every((source) => source.state === "missing"));
            h.assert(single && group, "harbor 缺少单一/多来源缺失样本");

            app.openFolder("harbor", { target: single.photo.id });
            await h.tick(160);
            h.assert(app.state.toolbar.rating.conflict === false, "单一缺失来源被误判为冲突");
            const node = h.filmCardByKey(single.key);
            h.assert(node, "单一缺失卡片未渲染");
            const zero = node.querySelector('.pv-card-star[data-rating="0"]');
            h.assert(!zero.classList.contains("is-conflict"), "单一缺失卡片的 0 号仍是警示三角");
            h.assert(zero.classList.contains("is-clear"), "单一缺失卡片的 0 号没有清零叉");
            h.assert(getComputedStyle(zero.querySelector(".pv-card-warning")).display === "none",
                "单一缺失卡片显示了警示三角");
            h.assert(zero.getAttribute("aria-label") === "无星级",
                `单一缺失 0 号名称是 ${zero.getAttribute("aria-label")}`);

            app.setRatingFilter("conflict");
            await h.tick(40);
            const keys = app.state.cards.map((entry) => entry.key);
            h.assert(!keys.includes(single.key), "单一缺失卡片命中了「星级冲突」");
            h.assert(keys.includes(group.key), "多来源全缺失未命中「星级冲突」");
            app.setRatingFilter("all");
            await h.tick(40);
        },
    },
];

function selectChecks(name) {
    if (!name || name === "all") return CHECKS;
    const wanted = name.toLowerCase();
    if (wanted === "mac") return CHECKS.filter((check) => check.id.endsWith("@mac"));
    const prefix = wanted === "similarity" ? "SIM" : wanted === "sim" ? "SIM" : wanted.toUpperCase();
    return CHECKS.filter((check) => check.id.split("-")[0] === prefix);
}

function createHelpers(shell) {
    const { app } = shell;
    const t = (name) => shell.t(name);
    const state = () => app.state;

    const wait = (ms) => new Promise((resolve) => setTimeout(resolve, ms));

    async function tick(ms = 40) {
        await Promise.resolve();
        await wait(ms);
    }

    async function until(fn, timeout = 5000, interval = 40) {
        const started = performance.now();
        let lastError = null;
        for (;;) {
            try {
                const value = await fn();
                if (value) return value;
            } catch (error) {
                lastError = error;
            }
            if (performance.now() - started > timeout) {
                throw new Error(`等待超时（${timeout}ms）${lastError ? `：${lastError.message}` : ""}`);
            }
            await wait(interval);
        }
    }

    function assert(condition, message) {
        if (!condition) throw new Error(message);
    }

    function deepEqual(actual, expected, message) {
        const left = JSON.stringify(actual);
        const right = JSON.stringify(expected);
        if (left !== right) throw new Error(`${message}：得到 ${left}，期望 ${right}`);
    }

    function dom(selector, root = document) {
        const node = root.querySelector(selector);
        if (!node) throw new Error(`找不到元素：${selector}`);
        return node;
    }

    function domAll(selector, root = document) {
        return [...root.querySelectorAll(selector)];
    }

    function click(node) {
        const rect = node.getBoundingClientRect();
        const x = rect.left + rect.width / 2;
        const y = rect.top + rect.height / 2;
        node.dispatchEvent(new MouseEvent("click", {
            bubbles: true, cancelable: true, detail: 1, view: window, button: 0, clientX: x, clientY: y,
        }));
    }

    function dblclick(node, pointX, pointY) {
        const rect = node.getBoundingClientRect();
        const x = pointX ?? rect.left + rect.width / 2;
        const y = pointY ?? rect.top + rect.height / 2;
        node.dispatchEvent(new MouseEvent("dblclick", {
            bubbles: true, cancelable: true, detail: 2, view: window, button: 0, clientX: x, clientY: y,
        }));
    }

    function keyDown(code, options = {}) {
        const init = {
            code,
            key: KEY_OF[code] ?? code,
            bubbles: true,
            cancelable: true,
            ctrlKey: Boolean(options.ctrl),
            metaKey: Boolean(options.meta),
            shiftKey: Boolean(options.shift),
            altKey: Boolean(options.alt),
            repeat: Boolean(options.repeat),
        };
        const target = options.target ?? window;
        const event = new KeyboardEvent("keydown", init);
        target.dispatchEvent(event);
        return event;
    }

    function keyUp(code, options = {}) {
        const init = {
            code,
            key: KEY_OF[code] ?? code,
            bubbles: true,
            cancelable: true,
            ctrlKey: Boolean(options.ctrl),
            metaKey: Boolean(options.meta),
            shiftKey: Boolean(options.shift),
            altKey: Boolean(options.alt),
        };
        const target = options.target ?? window;
        target.dispatchEvent(new KeyboardEvent("keyup", init));
    }

    function key(code, options = {}) {
        keyDown(code, options);
        if (!options.repeat) keyUp(code, options);
    }

    function wheel(node, options = {}) {
        const event = new Event("wheel", { bubbles: true, cancelable: true });
        const values = {
            deltaX: options.deltaX ?? 0,
            deltaY: options.deltaY ?? 0,
            deltaZ: 0,
            deltaMode: options.deltaMode ?? 0,
            ctrlKey: Boolean(options.ctrlKey),
            shiftKey: Boolean(options.shiftKey),
            altKey: Boolean(options.altKey),
            metaKey: Boolean(options.metaKey),
            wheelDeltaY: options.wheelDeltaY ?? 0,
            clientX: options.x ?? 0,
            clientY: options.y ?? 0,
        };
        for (const [name, value] of Object.entries(values)) {
            Object.defineProperty(event, name, { value, configurable: true });
        }
        node.dispatchEvent(event);
        return event;
    }

    function contextmenu(node, x, y) {
        node.dispatchEvent(new MouseEvent("contextmenu", {
            bubbles: true, cancelable: true, view: window, button: 2, clientX: x, clientY: y,
        }));
    }

    function pointer(node, type, x, y, options = {}) {
        const Ctor = globalThis.PointerEvent ?? MouseEvent;
        node.dispatchEvent(new Ctor(type, {
            bubbles: true,
            cancelable: true,
            view: window,
            clientX: x,
            clientY: y,
            button: options.button ?? 0,
            buttons: options.buttons ?? (type === "pointerup" ? 0 : 1),
            pointerId: options.pointerId ?? 1,
            pointerType: options.pointerType ?? "mouse",
            isPrimary: true,
        }));
    }

    function menu() {
        return document.querySelector(".pv-menu");
    }

    function menuItem(id) {
        return dom(`.pv-menu .pv-menu-item[data-id="${id}"]`);
    }

    async function openMainMenu() {
        const canvas = dom(".pv-photo-canvas");
        const rect = canvas.getBoundingClientRect();
        contextmenu(canvas, rect.left + rect.width / 2, rect.top + rect.height / 2);
        await tick(20);
        assert(menu() !== null, "右键未弹出主图菜单");
    }

    async function openFilter(group) {
        const segment = group ? document.querySelector(`.pv-popup.is-segment[data-group="${group}"]`) : null;
        click(segment && !segment.hidden ? segment : dom(".pv-filter-entry"));
        await tick(20);
        assert(menu() !== null, "点击筛选入口未弹出菜单");
    }

    function filmScroller() {
        return dom(".pv-film-scroll");
    }

    function filmAxis() {
        return state().layout.film?.axis ?? "x";
    }

    function filmOffset(axis = filmAxis()) {
        const node = filmScroller();
        return axis === "x" ? node.scrollLeft : node.scrollTop;
    }

    function filmMaxOffset(axis = filmAxis()) {
        const film = state().layout.film;
        const content = axis === "x" ? film.document.w : film.document.h;
        const view = axis === "x" ? film.scroll.w : film.scroll.h;
        return Math.max(0, content - view);
    }

    function expectedCenter(index, axis = filmAxis()) {
        const film = state().layout.film;
        const frame = film.items[index];
        const view = axis === "x" ? film.scroll.w : film.scroll.h;
        const start = axis === "x" ? frame.x : frame.y;
        return Math.min(Math.max(start + (film.cardMain - view) / 2, 0), filmMaxOffset(axis));
    }

    function filmCards() {
        return domAll(".pv-card", filmScroller());
    }

    function filmCardAt(index) {
        const card = state().cards[index];
        if (!card) return null;
        return filmCardByKey(card.key);
    }

    function filmCardByKey(key) {
        return filmCards().find((node) => node.dataset.key === key) ?? null;
    }

    function cardIndex(photoId) {
        return state().cards.findIndex((card) => card.photo.id === photoId || card.companions.some((photo) => photo.id === photoId));
    }

    function cardByPhotoId(photoId) {
        const card = state().cards.find((entry) => entry.photo.id === photoId || entry.companions.some((photo) => photo.id === photoId));
        if (!card) throw new Error(`列表中没有照片 ${photoId}`);
        return card;
    }

    function currentFilmKey() {
        const surface = document.querySelector(".pv-film-scroll .pv-card-surface.is-current");
        return surface ? surface.closest(".pv-card").dataset.key : null;
    }

    function harborPhotos() {
        const folder = shell.library.folders.find((entry) => entry.id === "harbor");
        return folder ? folder.photos : [];
    }

    function harborPhoto(predicate) {
        return harborPhotos().find(predicate) ?? null;
    }

    function ratingButton(value) {
        return dom(`.pv-control-rating-button[data-rating="${value}"]`);
    }

    function photoCanvas() {
        return dom(".pv-photo-canvas");
    }

    function paneRect(name) {
        const selector = { file: ".pv-pane-file", photo: ".pv-pane-photo", analysis: ".pv-pane-analysis", control: ".pv-pane-control" }[name];
        return dom(selector).getBoundingClientRect();
    }

    async function closeOverlays() {
        const shield = document.querySelector(".pv-menu-shield");
        if (shield) pointer(shield, "pointerdown", 1, 1);
        const popover = document.querySelector(".pv-rating-sources");
        if (popover) {
            const host = document.querySelector(".pv-photo-canvas") ?? document.querySelector(".pv-window");
            pointer(host, "pointerdown", 1, 1);
        }
        const picker = document.querySelector(".pv-picker:not([hidden])");
        if (picker) {
            const cancel = picker.querySelector(".pv-picker-button:not(.is-primary)");
            if (cancel) click(cancel);
        }
        await Promise.resolve();
    }

    async function reset() {
        // 检查统一按 Ctrl 解码，因此关闭 Apple 键盘映射，避免 ⌘/⌃ 互换。
        shell.setReducedMotion(true);
        await closeOverlays();
        shell.setSafeArea(null);
        const patch = {
            layoutMode: "auto",
            showRating: true,
            showNavigationButtons: true,
            showZoomIndicator: true,
            sameNameAsOne: true,
            analysisImmediateCvCompute: false,
            appleKeyboard: false,
        };
        // EDITION-06：标准版保留不可见的 DINO/相似值，不在检查重置时改写。
        if (app.capabilities.dino) patch.analysisImmediateDinoCompute = false;
        if (app.capabilities.similarity) {
            patch.similarityPanelExpanded = false;
            patch.similarityThreshold = 0.85;
            patch.similarityMaxResults = 8;
        }
        app.updateSettings(patch);
        app.setSort("taken", "asc");
        app.setRatingFilters(["all"]);
        if (app.state.panes.analysis) app.toggleAnalysis();
        if (app.state.panes.exif === false) app.toggleExif();
        if (!app.state.panes.film) app.toggleFilmbar();
        if (!app.state.panes.control) app.toggleControlbar();
        if (app.capabilities.similarity && app.settings.similarityPanelExpanded) app.setSimilarityExpanded(false);
        shell.setWindowSize(1200, 850);
        // OPEN-08：同一文件夹重开保持当前照片；检查统一从已提取、有相似伙伴的首张开始，避免上一检查的残留状态。
        app.openFolder("harbor");
        await tick(40);
        const candidates = app.state.cards.filter((card) => card.photo.extracted);
        const first = candidates.find((card) => candidates.some((other) => other.key !== card.key
            && other.photo.scene?.base === card.photo.scene?.base)) ?? candidates[0] ?? app.state.cards[0];
        app.openFolder("harbor", { target: first ? first.photo.id : undefined });
        await tick(80);
        if (!app.state.viewport.isFit) app.fit();
        await tick(40);
    }

    async function restore() {
        shell.setReducedMotion(true);
        await closeOverlays();
        shell.setWindowSize(1200, 850);
        if (!app.state.viewport.isFit) app.fit();
        await tick(20);
    }

    return {
        state, wait, tick, until, assert, deepEqual, dom, domAll, click, dblclick,
        key, keyDown, keyUp, wheel, contextmenu, pointer,
        menu, menuItem, openMainMenu, openFilter,
        filmScroller, filmOffset, filmMaxOffset, expectedCenter, filmCardAt, filmCardByKey, filmCards,
        cardIndex, cardByPhotoId, currentFilmKey,
        harborPhotos, harborPhoto, ratingButton, photoCanvas, paneRect,
        reset, restore,
    };
}

function createOverlay() {
    const root = document.createElement("div");
    root.id = "pv-check-overlay";
    root.style.cssText = [
        "position:fixed", "left:8px", "top:8px", "z-index:2147483647",
        "max-width:680px", "max-height:94vh", "overflow:hidden",
        "background:rgba(8,10,14,0.9)", "color:#e8edf4",
        "font:11px/1.5 ui-monospace,SFMono-Regular,Menlo,monospace",
        "padding:8px 10px", "border-radius:6px", "white-space:pre-wrap",
        "pointer-events:none", "box-shadow:0 6px 24px rgba(0,0,0,0.45)",
    ].join(";");
    document.body.append(root);
    return {
        update(lines) {
            root.textContent = lines.join("\n");
        },
        remove() {
            root.remove();
        },
    };
}

export async function runChecks(name, shell) {
    const selected = selectChecks(name);
    const overlay = createOverlay();
    const helpers = createHelpers(shell);
    const results = [];
    const summary = () => ({
        name,
        total: selected.length,
        pass: results.filter((entry) => entry.ok).length,
        fail: results.filter((entry) => !entry.ok).length,
        done: results.length,
        running: results.length < selected.length,
        results,
    });

    if (selected.length === 0) {
        const message = `未知检查：${name}（可用：all、${MODULES.join("、")}）`;
        overlay.update([`PV CHECK ${name}`, message]);
        globalThis.pvCheckResult = { pass: 0, fail: 0, results: [], error: message };
        console.error(message);
        return globalThis.pvCheckResult;
    }

    const render = (current) => {
        const data = summary();
        const lines = [`PV CHECK ${name}   ${data.done}/${data.total}   PASS ${data.pass}  FAIL ${data.fail}`];
        if (current) lines.push(`> ${current.id} ${current.title}`);
        for (const entry of results) {
            if (entry.ok) continue;
            lines.push(`✗ ${entry.id} ${entry.title}`);
            lines.push(`  ${entry.error}`);
        }
        const passed = results.filter((entry) => entry.ok);
        if (passed.length > 0) {
            lines.push(`✓ ${passed.slice(-12).map((entry) => entry.id).join(" ")}${passed.length > 12 ? " …" : ""}`);
        }
        overlay.update(lines);
        globalThis.pvCheckResult = data;
    };

    render(null);
    for (const check of selected) {
        render(check);
        let entry;
        try {
            await helpers.reset();
            await check.run(shell, helpers);
            entry = { id: check.id, title: check.title, ok: true, error: null };
        } catch (error) {
            entry = { id: check.id, title: check.title, ok: false, error: error && error.message ? error.message : String(error) };
            console.error(`[${check.id}] ${check.title}`, error);
        } finally {
            try {
                await helpers.restore();
            } catch (error) {
                console.error("恢复场景失败", error);
            }
        }
        results.push(entry);
        render(null);
    }

    shell.setReducedMotion(null);
    render(null);
    console.table(results.map((entry) => ({ id: entry.id, ok: entry.ok, error: entry.error ?? "" })));
    document.title = `PV CHECK ${summary().pass}/${selected.length}`;
    globalThis.pvCheckResult = summary();
    return globalThis.pvCheckResult;
}
