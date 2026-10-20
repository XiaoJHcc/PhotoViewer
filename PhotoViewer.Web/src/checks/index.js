/**
 * 页内行为检查（?check=<模块|all>）：用真实 DOM 事件驱动壳层，按设计 ID 汇总到浮层、console 与
 * globalThis.pvCheckResult。每条检查前重置到已知场景（harbor、1200×850、auto、减少动态效果开），
 * 故障照片按字段查找而不是写死文件名。
 *
 * 覆盖：FILM / FILTER / MENU / INPUT / IMAGE / TOOLBAR / STATE / ANALYSIS / SIM / OPEN / SHELL。
 */

const MODULES = ["film", "filter", "menu", "input", "image", "toolbar", "state", "analysis", "similarity", "open", "shell"];

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
            h.deepEqual(labels, ["全部", "无星级", "一星", "二星", "三星", "四星", "五星", "一星以上", "二星以上", "三星以上", "四星以上", "星级冲突"],
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
        id: "FILTER-08",
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
        title: "空结果显示「无匹配照片」与「共 0 张」，筛选条件保留",
        async run(shell, h) {
            const { app } = shell;
            app.updateSettings({ sameNameAsOne: false });
            await h.tick(60);
            await h.openFilter("rating");
            h.click(h.menuItem("conflict"));
            await h.tick(100);
            h.assert(app.state.count === 0, `空结果计数 ${app.state.count} ≠ 0`);
            h.assert(app.state.countText === "共 0 张", `空结果计数文本 ${app.state.countText}`);
            const empty = h.dom(".pv-film-empty");
            h.assert(empty.hidden === false, "胶片栏空状态不可见");
            h.assert(empty.textContent.includes("无匹配照片"), `空状态文案不符：${empty.textContent}`);
            h.assert(h.domAll(".pv-popup").every((node) => !node.disabled), "空结果时筛选控件被禁用");
            h.assert(h.domAll(".pv-popup")[2].querySelector(".pv-popup-label").textContent === "星级冲突", "空结果时筛选条件未保留");
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
            h.deepEqual(labels, ["打开文件…", "打开文件夹…", "胶片栏", "分析栏", "控制栏", "增强"], "菜单文案不符");
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
        id: "MENU-07",
        title: "无照片时打开与栏位可用、增强置灰",
        async run(shell, h) {
            const { app } = shell;
            app.openFolder("empty");
            await h.tick(120);
            h.assert(app.state.photo === null, "空文件夹仍有当前照片");
            await h.openMainMenu();
            h.assert(h.menuItem("enhance").getAttribute("aria-disabled") === "true", "无照片时增强未置灰");
            h.assert(h.menuItem("enhance").classList.contains("is-disabled"), "增强缺少禁用样式");
            h.assert(h.menuItem("film").getAttribute("aria-disabled") === "false", "无照片时胶片栏被禁用");
            h.assert(h.menuItem("open-file").getAttribute("aria-disabled") === "false", "无照片时打开文件被禁用");
        },
    },
    {
        id: "INPUT-01",
        title: "← → 翻页、F/Ctrl+0 适应、E 增强、1–5/` 评分",
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
            h.assert(app.state.session.enhance === true, "E 未开启增强");
            h.key("KeyE");
            await h.tick(60);
            h.assert(app.state.session.enhance === false, "E 未关闭增强");

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
        title: "命令按钮提示含绑定、下一张切图并定位、无照片禁用",
        async run(shell, h) {
            const { app } = shell;
            const next = h.dom('.pv-control-command-button[data-command="Next"]');
            h.assert(next.title.includes("下一张"), `按钮提示缺少命令名：${next.title}`);
            h.assert(next.title.includes("→") && next.title.includes("滚轮下"), `按钮提示缺少绑定：${next.title}`);
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
            h.assert(toast.textContent.includes("无法写入星级"), `提示条文案不符：${toast.textContent}`);
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
            h.assert(toast.textContent.includes("无法载入全分辨率"), `提示条文案不符：${toast.textContent}`);
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
            h.assert(Math.abs(h.paneRect("file").width - (before + shell.t("sim.panel.width"))) <= 1,
                `文件栏宽度 ${h.paneRect("file").width} ≠ ${before + shell.t("sim.panel.width")}`);
            h.assert(h.dom(".pv-filter-count").textContent === countText, "打开面板改变了计数");
            h.assert(app.state.displayedId === app.state.anchorId, "打开面板改变了当前照片");
            h.assert(app.state.sortKey === sortKey, "打开面板改变了排序");
            await h.until(() => app.state.similarity.items.length > 0, 4000);
            h.assert(h.domAll(".pv-sim-item").length === app.state.similarity.items.length, "相似列表与状态不一致");
            const anchorId = app.state.anchorId;
            const self = app.state.similarity.items.find((item) => item.card.photo.id === anchorId
                || item.card.companions.some((photo) => photo.id === anchorId));
            h.assert(!self, `相似列表包含锚点自身：${self ? self.card.displayName : ""}`);
            const scores = app.state.similarity.items.map((item) => item.score);
            h.assert(scores.every((score, index) => index === 0 || score <= scores[index - 1] + 1e-9), "相似列表未按分数降序");
            h.assert(scores.every((score) => score >= 0.85), "相似列表包含低于阈值的项");
        },
    },
    {
        id: "SIM-05",
        title: "点相似卡片切主图，胶片栏高亮留在锚点",
        async run(shell, h) {
            const { app } = shell;
            h.click(h.dom(".pv-filter-toggle"));
            await h.until(() => app.state.similarity.items.length > 0, 4000);
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
        return filmCards().find((node) => node.dataset.key === card.key) ?? null;
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
        filmScroller, filmOffset, filmMaxOffset, expectedCenter, filmCardAt, filmCards,
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
