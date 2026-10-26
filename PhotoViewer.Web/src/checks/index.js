/**
 * 页内行为检查（?check=<模块|all>）：用真实 DOM 事件驱动壳层，按设计 ID 汇总到浮层、console 与
 * globalThis.pvCheckResult。每条检查前重置到已知场景（harbor、1200×850、auto、减少动态效果开），
 * 故障照片按字段查找而不是写死文件名。
 *
 * 覆盖：FILM / FILTER / RATING / MENU / INPUT / IMAGE / TOOLBAR / STATE / ANALYSIS / SIM / OPEN / SHELL。
 */

import { displayStars, hasRatingConflict, ratingSources, ratingWriteBlockReason, representativeRating } from "../core/rating.js";

const MODULES = ["film", "filter", "rating", "menu", "input", "image", "toolbar", "state", "analysis", "similarity", "open", "shell"];

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
    const match = /rgba?\(([^)]+)\)/.exec(source);
    if (!match) return null;
    const parts = match[1].split(/[,/\s]+/).filter(Boolean).map(Number);
    return { r: parts[0], g: parts[1], b: parts[2], a: parts[3] ?? 1 };
}

function colorMatches(actual, expected) {
    const a = parseColor(actual);
    const b = parseColor(expected);
    if (!a || !b) return false;
    return Math.abs(a.r - b.r) <= 1 && Math.abs(a.g - b.g) <= 1
        && Math.abs(a.b - b.b) <= 1 && Math.abs(a.a - b.a) <= 0.01;
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

            const displayBeforeStar = app.state.displayedId;
            const starCard = h.filmCardAt(1);
            h.click(starCard.querySelectorAll(".pv-card-star")[4]);
            await h.tick(120);
            h.assert(app.state.displayedId === displayBeforeStar, "点卡片星级切换了当前照片");
            h.assert(h.filmOffset() === offsetAfterNav, "点卡片星级改变了列表偏移");

            await h.openFilter("rating");
            h.click(h.menuItem("3"));
            await h.tick(80);
            h.assert(app.state.ratingFilter === "3", "星级筛选未应用");
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
            h.assert(app.state.ratingFilter === "3", "三星筛选未应用");
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
            h.assert(app.state.countText === `共 ${countBefore} 张`, `计数文本不一致：${app.state.countText}`);
            h.assert(h.filmOffset() === beforeOffset, "改星改变了列表偏移");

            h.key("ArrowRight");
            await h.tick(120);
            h.assert(!app.state.cards.some((entry) => entry.key === card.key), "切走后暂留项仍在列表");
            h.assert(app.state.count === countBefore - 1, `切走后计数 ${app.state.count} ≠ ${countBefore - 1}`);
        },
    },
    {
        id: "FILTER-01",
        title: "三项顺序、计数文本与控件结构",
        async run(shell, h) {
            const { app } = shell;
            const popups = h.domAll(".pv-popup");
            h.assert(popups.length === 3, `下拉数量 ${popups.length} ≠ 3`);
            const labels = popups.map((node) => node.querySelector(".pv-popup-label").textContent);
            h.deepEqual(labels, ["名称", "升序 ↑", "全部"], "默认下拉文案或顺序不符");
            h.assert(h.filmScroller().parentElement.contains(popups[0]), "筛选条不在文件栏");
            const count = h.dom(".pv-filter-count");
            h.assert(count.textContent === `共 ${app.state.count} 张`, `计数文本 ${count.textContent}`);
            // FILTER-04：计数字号取 filter.count.fontSize。
            h.assert(getComputedStyle(count).fontSize === `${shell.t("filter.count.fontSize")}px`,
                `计数字号 ${getComputedStyle(count).fontSize}`);
            const toggle = h.dom(".pv-filter-toggle");
            h.assert(toggle.getAttribute("role") === "switch", "相似聚类开关缺少 switch 语义");
            h.assert(toggle.getAttribute("aria-label") === "相似聚类", "相似聚类开关名称不符");
            const children = [...h.dom(".pv-pane-file").children];
            h.assert(children.indexOf(count) > children.indexOf(popups[2]), "计数不在星级筛选之后");
            h.assert(children.indexOf(toggle) > children.indexOf(count), "相似聚类开关不在末位");
        },
    },
    {
        id: "FILTER-03",
        title: "星级筛选取值与菜单顺序固定，菜单打开期间 → 不切图",
        async run(shell, h) {
            const { app } = shell;
            await h.openFilter("rating");
            const labels = h.domAll(".pv-menu .pv-menu-item").map((node) => node.querySelector(".pv-menu-label").textContent);
            h.deepEqual(labels, ["全部", "无星级", "一星", "二星", "三星", "四星", "五星", "一星以上", "二星以上", "三星以上", "四星以上", "已拒绝", "星级冲突"],
                "星级筛选项或顺序不符");
            const checked = h.domAll(".pv-menu .pv-menu-item").filter((node) => node.getAttribute("aria-checked") === "true");
            h.assert(checked.length === 1 && checked[0].dataset.id === "all", "当前项未勾选");

            const displayed = app.state.displayedId;
            h.key("ArrowRight");
            await h.tick(80);
            h.assert(app.state.displayedId === displayed, "菜单打开期间 → 键切换了照片");
            h.assert(h.menu() !== null, "菜单因 → 键被关闭");

            h.key("Escape");
            await h.tick();
            h.assert(h.menu() === null, "Esc 未关闭筛选菜单");
            h.assert(app.state.ratingFilter === "all", "Esc 改变了星级筛选");

            await h.openFilter("rating");
            h.click(h.menuItem("5"));
            await h.tick(80);
            h.assert(app.state.ratingFilter === "5", "选中即应用失败");
            h.assert(h.menu() === null, "选中后菜单未关闭");
            h.assert(h.dom(".pv-filter-count").textContent === `共 ${app.state.count} 张`, "应用后的计数不一致");
        },
    },
    {
        id: "FILTER-06",
        title: "相似聚类开关为药丸四态，常态/勾选取新令牌颜色",
        async run(shell, h) {
            const { app } = shell;
            const toggle = h.dom(".pv-filter-toggle");
            const style = () => getComputedStyle(toggle);
            const uncheckedBackground = style().backgroundColor;
            const uncheckedColor = style().color;
            h.assert(uncheckedBackground === "rgba(0, 0, 0, 0)", `未勾选底色 ${uncheckedBackground}`);
            h.assert(toggle.getAttribute("aria-checked") === String(Boolean(app.settings.similarityPanelExpanded)), "勾选态与设置不一致");

            h.click(toggle);
            await h.tick(80);
            h.assert(app.settings.similarityPanelExpanded === true, "点击未勾选相似聚类");
            h.assert(style().backgroundColor !== uncheckedBackground, "勾选态底色未变化");
            h.assert(style().color !== uncheckedColor, "勾选态文字色未变化");
            h.assert(toggle.getAttribute("aria-checked") === "true", "勾选态未更新为 true");

            h.click(toggle);
            await h.tick(80);
            h.assert(app.settings.similarityPanelExpanded === false, "再次点击未取消相似聚类");
        },
    },
    {
        id: "FILTER-08",
        title: "自绘下拉外观与弹层位置：贴按钮下方居中、不覆盖按钮、当前项整行强调色",
        async run(shell, h) {
            const { app } = shell;
            const t = (name) => shell.t(name);
            const button = h.domAll(".pv-popup")[0];
            const buttonStyle = getComputedStyle(button);
            h.assert(colorMatches(buttonStyle.backgroundColor, t("color.dropdown.background")),
                `下拉按钮底色 ${buttonStyle.backgroundColor}`);
            h.assert(colorMatches(buttonStyle.borderTopColor, t("color.dropdown.border")),
                `下拉按钮描边 ${buttonStyle.borderTopColor}`);
            h.assert(buttonStyle.borderTopWidth === "1px", `下拉按钮描边宽 ${buttonStyle.borderTopWidth}`);
            h.assert(buttonStyle.borderTopLeftRadius === `${t("dropdown.cornerRadius")}px`, `下拉圆角 ${buttonStyle.borderTopLeftRadius}`);
            h.assert(buttonStyle.fontSize === `${t("dropdown.fontSize")}px`, `下拉字号 ${buttonStyle.fontSize}`);
            h.assert(buttonStyle.paddingLeft === `${t("dropdown.paddingLeft")}px`, `下拉左内边距 ${buttonStyle.paddingLeft}`);
            h.assert(buttonStyle.backgroundImage === "none", "下拉按钮仍用渐变");
            const chevron = button.querySelector(".pv-popup-chevron");
            h.assert(chevron !== null, "下拉缺少 icon.chevronDown 字形");
            h.assert(getComputedStyle(chevron).fontSize === `${t("dropdown.chevron.size")}px`, `箭头字号 ${getComputedStyle(chevron).fontSize}`);
            h.assert(button.querySelector(".pv-popup-arrows") === null, "下拉仍画上下双箭头");

            await h.openFilter("sort");
            const rect = button.getBoundingClientRect();
            const menu = h.menu();
            const menuRect = menu.getBoundingClientRect();
            const menuStyle = getComputedStyle(menu);
            h.assert(Math.abs((menuRect.left + menuRect.width / 2) - (rect.left + rect.width / 2)) <= 1,
                `弹层未以按钮水平居中：${menuRect.left + menuRect.width / 2} ≠ ${rect.left + rect.width / 2}`);
            h.assert(menuRect.top >= rect.bottom - 1, `弹层覆盖按钮：top ${menuRect.top} < 按钮底 ${rect.bottom}`);
            h.assert(menuRect.top - rect.bottom <= 1, `弹层未贴在按钮正下方：间隙 ${menuRect.top - rect.bottom}`);
            h.assert(menuRect.width >= rect.width - 0.5, `弹层最小宽不足：${menuRect.width} < ${rect.width}`);
            h.assert(colorMatches(menuStyle.backgroundColor, t("color.popup.background")), `弹层底色 ${menuStyle.backgroundColor}`);
            h.assert(colorMatches(menuStyle.borderTopColor, t("color.popup.border")), `弹层描边 ${menuStyle.borderTopColor}`);
            h.assert(menuStyle.boxShadow === "none", `弹层仍有阴影：${menuStyle.boxShadow}`);
            h.assert(menuStyle.borderTopLeftRadius === `${t("popup.cornerRadius")}px`, `弹层圆角 ${menuStyle.borderTopLeftRadius}`);
            h.assert(menuStyle.paddingTop === `${t("popup.padding")}px`, `弹层上内边距 ${menuStyle.paddingTop}`);

            const items = h.domAll(".pv-menu .pv-menu-item");
            const first = items[0];
            h.assert(first.offsetHeight === 31, `下拉项高 ${first.offsetHeight} ≠ 31`);
            h.assert(getComputedStyle(first).paddingLeft === `${t("dropdown.item.paddingX")}px`, "下拉项左内边距不符");
            const selected = items.find((node) => node.classList.contains("is-selected"));
            h.assert(selected !== null, "当前项未整行强调");
            // 键盘焦点即悬停态：当前项带焦点时取 selected.hover。
            const selectedToken = selected.classList.contains("is-focused")
                ? "color.dropdown.item.selected.hover"
                : "color.dropdown.item.selected";
            h.assert(colorMatches(getComputedStyle(selected).backgroundColor, t(selectedToken)),
                `当前项底色 ${getComputedStyle(selected).backgroundColor}`);
            h.assert(menu.querySelector(".pv-menu-mark") === null, "下拉仍画勾号");

            h.key("Escape");
            await h.tick(40);
            h.assert(h.menu() === null, "Esc 未关闭下拉");
            h.assert(app.state.sortKey === "name", "Esc 改变了下拉取值");
        },
    },
    {
        id: "FILTER-08b",
        title: "键盘 ↓ 打开菜单，上下键移动、回车确认",
        async run(shell, h) {
            const { app } = shell;
            const button = h.domAll(".pv-popup")[0];
            button.focus();
            button.dispatchEvent(new KeyboardEvent("keydown", { key: "ArrowDown", code: "ArrowDown", bubbles: true, cancelable: true }));
            await h.tick();
            h.assert(h.menu() !== null, "↓ 未打开下拉菜单");
            h.key("ArrowDown");
            await h.tick(20);
            h.key("Enter");
            await h.tick(80);
            h.assert(h.menu() === null, "回车未关闭菜单");
            h.assert(app.state.sortKey !== "name", "回车未应用到下一项");
        },
    },
    {
        id: "FILTER-10",
        title: "空结果计数「共 0 张」、胶片栏留空，筛选条件保留",
        async run(shell, h) {
            const { app } = shell;
            app.updateSettings({ sameNameAsOne: false });
            await h.tick(60);
            // 夜景没有 -1 来源，选「已拒绝」必然无命中：空结果共用 FILTER-10 的空状态。
            await h.openFilter("rating");
            h.click(h.menuItem("rejected"));
            await h.tick(100);
            app.openFolder("night");
            await h.tick(120);
            h.assert(app.state.count === 0, `空结果计数 ${app.state.count} ≠ 0`);
            h.assert(app.state.countText === "共 0 张", `空结果计数文本 ${app.state.countText}`);
            // FILM-05 / STATE-02：空结果胶片栏留空，不写占位文字。
            h.assert(h.filmScroller().textContent.trim() === "", `胶片栏空态有文字：${h.filmScroller().textContent}`);
            h.assert(document.querySelector(".pv-film-empty") === null, "胶片栏仍有空状态元素");
            h.assert(h.domAll(".pv-popup").every((node) => !node.disabled), "空结果时筛选控件被禁用");
            h.assert(h.domAll(".pv-popup")[2].querySelector(".pv-popup-label").textContent === "已拒绝", "空结果时筛选条件未保留");
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
            const photoWidth = h.paneRect("photo").width;
            await h.openMainMenu();
            h.click(h.menuItem("film"));
            await h.tick(120);
            h.assert(app.state.panes.film === false, "菜单未隐藏文件栏");
            h.assert(h.dom(".pv-pane-file").hidden, "文件栏节点仍可见");
            h.assert(h.paneRect("photo").width > photoWidth, "隐藏文件栏后主图未扩大");

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
        title: "空态只有主图分区点击打开选择器，文件栏/控制栏/分析栏空白处不响应",
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
            h.assert(picker.hidden === false, "主图区点击未打开选择器");
            h.click(h.dom(".pv-picker-button:not(.is-primary)"));
            await h.tick(40);
            h.assert(closed(), "取消后选择器未关闭");
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
        title: "命令按钮提示为「命令名 · 主 / 次」、下一张切图并定位、无照片禁用",
        async run(shell, h) {
            const { app } = shell;
            const next = h.dom('.pv-control-command-button[data-command="Next"]');
            // TOOLBAR-04：格式 `命令名 · 主 / 次`，不含「未设置」占位；提示走 data-tooltip。
            h.assert(next.dataset.tooltip === "下一张 · → / 滚轮下", `按钮提示格式不符：${next.dataset.tooltip}`);
            const fit = h.dom('.pv-control-command-button[data-command="Fit"]');
            h.assert(fit.dataset.tooltip === "缩放适应 · Ctrl+0 / F", `按钮提示格式不符：${fit.dataset.tooltip}`);
            h.assert(!next.dataset.tooltip.includes("未设置"), "提示出现未设置占位");
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
        id: "STATE-01",
        title: "缓存命中切图保留前一张，未命中即清图并延迟提示",
        async run(shell, h) {
            const { app } = shell;
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
            await h.until(() => document.querySelectorAll(".pv-analysis-tile").length === 7
                && app.state.analysis.tiles.length === 7, 4000);
            const tiles = h.domAll(".pv-analysis-tile");
            h.assert(tiles.length === 7, `有对焦点时瓦片数 ${tiles.length} ≠ 7`);
            const widths = tiles.map((node) => Math.round(node.getBoundingClientRect().width));
            h.assert(widths.every((width) => Math.abs(width - widths[0]) <= 1), `瓦片不等边：${widths.join(",")}`);
            h.assert(tiles[0].dataset.kind === "focus", "首瓦片不是对焦点");
            h.assert(document.querySelector('.pv-analysis-tile[data-kind="focus"]') !== null, "有对焦点时缺少对焦点瓦片");

            const withoutFocus = h.harborPhoto((entry) => !entry.focus);
            app.selectCard(withoutFocus.id);
            await h.tick(120);
            await h.until(() => document.querySelectorAll(".pv-analysis-tile").length === 6
                && app.state.analysis.tiles.length === 6, 4000);
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
            await h.until(() => app.state.panes.analysis && h.domAll(".pv-analysis-tile").length === 7, 4000);
            await h.until(() => app.state.analysis.diagnostics !== null, 4000);

            const center = h.dom('.pv-analysis-tile[data-kind="center"]');
            const focus = h.dom('.pv-analysis-tile[data-kind="focus"]');
            const point = (node) => {
                const rect = node.getBoundingClientRect();
                return { x: rect.left + rect.width / 2, y: rect.top + rect.height / 2 };
            };
            const centerPoint = point(center);
            const focusPoint = point(focus);
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

            const pca = h.dom('.pv-analysis-tile[data-kind="pca"]');
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
            const tile = h.dom('.pv-analysis-tile[data-kind="pca"]');
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
        title: "相似开关展开面板、文件栏加宽、计数与排序不变",
        async run(shell, h) {
            const { app } = shell;
            const before = h.paneRect("file").width;
            const countText = h.dom(".pv-filter-count").textContent;
            const sortKey = app.state.sortKey;
            h.click(h.dom(".pv-filter-toggle"));
            await h.until(() => app.state.similarity.visible === true, 4000);
            await h.tick(80);
            h.assert(h.dom(".pv-sim").hidden === false, "相似面板未显示");
            // SIM-07：列布局文件栏加宽 = 面板宽 + sim.panel.margin。
            h.assert(Math.abs(h.paneRect("file").width - (before + shell.t("sim.panel.width") + shell.t("sim.panel.margin"))) <= 1,
                `文件栏宽度 ${h.paneRect("file").width} ≠ ${before + shell.t("sim.panel.width") + shell.t("sim.panel.margin")}`);
            h.assert(h.dom(".pv-filter-count").textContent === countText, "打开面板改变了计数");
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
            h.click(h.dom(".pv-filter-toggle"));
            await h.until(() => app.state.similarity.visible === true, 4000);
            await h.until(() => app.state.similarity.items.length > 0 && app.state.similarity.calculating === false, 4000);
            await h.tick(80);

            const file = h.paneRect("file");
            const panel = h.dom(".pv-sim").getBoundingClientRect();
            const filter = app.state.layout.filter;
            h.assert(Math.abs(panel.left - file.left - shell.t("shell.filebar.content.width")) <= 1,
                `列布局面板未接在 116 宽文件栏右侧：${panel.left - file.left}`);
            h.assert(Math.abs(panel.top - file.top - filter.bar.h) <= 1, `列布局面板未与胶片栏同顶：${panel.top - file.top} ≠ ${filter.bar.h}`);
            h.assert(Math.abs(panel.bottom - file.bottom) <= 1, `列布局面板未到文件栏底：${panel.bottom} ≠ ${file.bottom}`);
            h.assert(Math.abs(file.width - (shell.t("shell.filebar.content.width") + panel.width + shell.t("sim.panel.margin"))) <= 1,
                `文件栏加宽不含外边距：${file.width}`);

            shell.setWindowSize(700, 850);
            await h.tick(160);
            h.assert(app.state.layout.layoutMode === "row", "700×850 未进入行布局");
            const rowFile = h.paneRect("file");
            const rowPanel = h.dom(".pv-sim").getBoundingClientRect();
            h.assert(Math.abs(rowPanel.height - shell.t("sim.list.cross")) <= 1, `行布局面板高 ${rowPanel.height} ≠ ${shell.t("sim.list.cross")}`);
            h.assert(Math.abs(rowFile.right - rowPanel.right - shell.t("sim.panel.margin")) <= 1, "行布局面板右侧未留外边距");
            h.assert(Math.abs(app.state.layout.similarity.width - rowPanel.width) <= 1, "行布局面板宽与布局不一致");
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
        title: "auto 布局随宽高比切换，窗口尺寸与分区一致",
        async run(shell, h) {
            const { app } = shell;
            shell.setWindowSize(1200, 850);
            await h.tick(100);
            h.assert(app.state.layout.layoutMode === "col", "1200×850 未取列布局");
            h.assert(h.dom(".pv-window").dataset.layout === "col", "窗口 dataset.layout 未更新");
            const file = h.paneRect("file");
            h.assert(file.height === 850 && file.width >= 116, "列布局文件栏几何不符");

            shell.setWindowSize(700, 850);
            await h.tick(100);
            h.assert(app.state.layout.layoutMode === "row", "700×850 未取行布局");
            const rowFile = h.paneRect("file");
            h.assert(Math.abs(rowFile.height - (shell.t("filter.bar.row.height") + shell.t("film.band.height"))) <= 1,
                `行布局文件栏高 ${rowFile.height} 不符`);
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

            h.click(h.dom(".pv-filter-toggle"));
            await h.until(() => JSON.parse(localStorage.getItem("PhotoViewer.Settings")).similarityPanelExpanded === true, 2000);
            h.click(h.dom(".pv-filter-toggle"));
            await h.tick(80);
            h.assert(JSON.parse(localStorage.getItem("PhotoViewer.Settings")).similarityPanelExpanded === false, "相似开关未持久化");

            await h.openFilter("sort");
            h.click(h.menuItem("size"));
            await h.tick(80);
            const settings = JSON.parse(localStorage.getItem("PhotoViewer.Settings"));
            h.assert(!("sortKey" in settings) && !("ratingFilter" in settings), "排序/筛选被写入设置");
            h.assert(app.state.sortKey === "size", "排序未应用");
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

            // 相似面板复用卡片组件；打开一次确认列表态也满足同一条约束。
            app.setSimilarityExpanded(true);
            await h.tick(160);
            result = scan();
            h.assert(result.offenders.length === 0, `相似面板字形进入可读文本：${result.offenders.slice(0, 4).join("；")}`);
            app.setSimilarityExpanded(false);
            await h.tick(40);
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
                + t("toolbar.rating.button.size") * 6;
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
                h.assert(Math.round(box.height) === 68, `短名条目高 ${box.height} ≠ 68`);
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
                    h.assert(Math.round(starBox.width) === t("toolbar.rating.button.size")
                        && Math.round(starBox.height) === t("toolbar.rating.button.size"),
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
            // 两个短名条目：4 + 68 + 8 + 68 + 4 = 152。
            h.assert(Math.round(popup().clientHeight) === 152, `浮窗内容高 ${popup().clientHeight} ≠ 152`);
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
                h.assert(getComputedStyle(glyph).fontSize === `${t("toolbar.rating.glyph.fontSize")}px`,
                    `星形字号 ${getComputedStyle(glyph).fontSize}`);
                const clear = entryNodes[index].querySelector(".pv-rating-source-clear");
                h.assert(getComputedStyle(clear).fontSize === `${t("toolbar.rating.clearGlyph.fontSize")}px`,
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
            app.setSort("name", "asc");
            await h.tick(140);
            h.assert(popup(), "外部来源变化关闭了浮窗");
            const refreshedStars = entries()[1].querySelectorAll(".pv-rating-source-star");
            for (let value = 1; value < 6; value += 1) {
                h.assert(refreshedStars[value].classList.contains("is-on")
                    === (externalSource.photo.rating >= value), `外部变化后第 ${value} 星未刷新`);
            }
            externalSource.photo.rating = externalOriginal;
            app.setSort("name", "asc");
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
            h.assert(Math.round(controlZero.getBoundingClientRect().width) === t("toolbar.rating.button.size"),
                `控制栏 0 号宽度 ${controlZero.getBoundingClientRect().width}`);
            const controlWarning = controlZero.querySelector(".pv-rating-conflict");
            h.assert(controlWarning.textContent.codePointAt(0) === warningCode, "控制栏警示三角字形不是 icon.warning");
            h.assert(getComputedStyle(controlWarning).fontSize === `${t("toolbar.rating.glyph.fontSize")}px`,
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
            h.assert(Math.round(longEntry.getBoundingClientRect().height) === 88,
                `长名条目高 ${longEntry.getBoundingClientRect().height} ≠ 88`);
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
            h.assert(Math.round(popup().clientHeight) === 172, `长名浮窗内容高 ${popup().clientHeight} ≠ 172`);
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

            for (const value of ["1", "2", "3", "4", "5", "1+", "2+", "3+", "4+"]) {
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

    async function openFilter(kind) {
        const index = { sort: 0, order: 1, rating: 2 }[kind];
        click(domAll(".pv-popup")[index]);
        await tick(20);
        assert(menu() !== null, "点击下拉未弹出菜单");
    }

    function filmScroller() {
        return dom(".pv-film-scroll");
    }

    function filmAxis() {
        return state().layout.layoutMode === "col" ? "y" : "x";
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
        const card = axis === "x" ? t("film.card.width") : t("film.card.height");
        const view = axis === "x" ? film.scroll.w : film.scroll.h;
        const start = axis === "x" ? frame.x : frame.y;
        return Math.min(Math.max(start + (card - view) / 2, 0), filmMaxOffset(axis));
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
        app.updateSettings({
            layoutMode: "auto",
            showRating: true,
            showZoomIndicator: true,
            sameNameAsOne: true,
            analysisImmediateCompute: false,
            similarityPanelExpanded: false,
            appleKeyboard: false,
            similarityThreshold: 0.85,
            similarityMaxResults: 8,
        });
        app.setSort("name", "asc");
        app.setRatingFilter("all");
        if (app.state.panes.analysis) app.toggleAnalysis();
        if (!app.state.panes.film) app.toggleFilmbar();
        if (!app.state.panes.control) app.toggleControlbar();
        if (app.settings.similarityPanelExpanded) app.setSimilarityExpanded(false);
        shell.setWindowSize(1200, 850);
        // OPEN-08：同一文件夹重开保持当前照片；检查统一从筛选后首张开始，避免上一检查的残留状态。
        const first = app.state.cards[0];
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
