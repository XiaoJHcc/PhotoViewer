# PhotoViewer.Web — Web 参考壳

浏览器里的第五个壳（平台代号 `web`）：完整复刻主屏的业务行为与 macOS 视觉，照片为程序化合成数据。它同时是三样东西：免下载的在线试用、设计规则的可执行基准（规则变更可先在这里验证再分发四端）、各平台几何比对的基准来源（替代原 `Design/prototype/` 样机）。设置窗口与工具窗口不做（`SCOPE-01@web`）。计划与进度见 [Plans/web-shell-plan-1.md](../Plans/web-shell-plan-1.md)，文档缺口台账见 [Plans/web-shell-gaps.md](../Plans/web-shell-gaps.md)。

## 唯一依据：Design/

**硬规则**：本壳只依据 `Design/`（`spec/`、`tokens.json`、`README.md`、`platforms.md` 中的 `@web` 条目）实现，**不读取** `PhotoViewer/`、`PhotoViewer.*/`（本目录除外）、`Design/baseline/`、`release/` 下的源码与验收常量，也不照搬其他平台的写法。规则缺失、含糊或无法实现时，按最合理的理解实现，同时在缺口台账追加一条（分类写 `待审`），不得去其他平台的代码里找答案。这样本壳与 Mac 的每一处差异都能定位到"文档没写清"或"某端没照文档做"。

## 运行与测试

```bash
cd PhotoViewer.Web
python3 -m http.server 8080      # 打开 http://localhost:8080/
node --test "tests/**/*.test.js" # 纯逻辑单测（零依赖）
```

VS Code Tasks：**Serve Web**、**Test Web**。ES module 在 `file://` 下会被浏览器拦截，必须经本地服务打开。令牌产物 `src/tokens.g.js` 由 `python3 Tools/generate-design-tokens.py` 生成，不得手改。

页面参数：`?demo` 打开窗口外演示面板（尺寸、设置、故障注入、状态读数、几何导出）；`?check=<模块|all>` 运行页内行为检查并输出按设计 ID 的报告；`?platform=mac` 切到 mac 配置档（只为几何比对：交通灯让位等 `@mac` 差异）；`?empty` 以空态启动；`?folder=<id>` 指定首个文件夹。

页内检查可无人值守跑：无头 Chrome 的 `--virtual-time-budget` 至少给到 120000 ms，预算偏小时只跑完部分检查（报告是 `done/total`，不是 FAIL）；`--headless=new` 需同时关掉 crashpad（`--disable-breakpad --disable-crash-reporter --disable-features=Crashpad`），否则在沙箱环境里会以 SIGTRAP 退出。

## 结构与分层

```text
index.html
src/
  tokens.g.js           # 生成；默认导出基准值，platformTokens 导出平台覆盖
  core/                 # 纯逻辑：无 DOM、无全局状态，可在 Node 里单测
    types.js            # 模块间共用数据形状（JSDoc）
    tokens.js           # 按平台配置档取令牌（缺令牌立即抛错）
    clock.js            # 可注入的时间源与定时器（单测用假时钟）
    settings.js         # 设置模型默认值（只取 spec/settings.md 中影响主屏的项）
    library.js          # 合成照片库
    filter.js           # 同名合并、格式、星级筛选、排序、暂留、锚点翻页
    rating.js           # 星级来源模型：内嵌/已有 sidecar 来源、组级冲突、筛选投影与写入预检
    viewport.js         # 主图视口数学
    filmscroll.js       # 胶片栏几何（步长/内容长/居中偏移）、定位动画器与定位门控
    features.js         # 合成 DINO/CV 特征（同一连拍场景彼此相似）
    input.js            # 手势编码、主/次绑定、Repeatable、滚轮分流
    session.js          # 加载/代理→Full/增强/药丸/提示条时序；展示产物缓存（STATE-01 保留前一张）
    analysis.js         # 分析栏瓦片集合、三态、准星、cosine
    similarity.js       # 相似面板三态、计算/取消、提取进度
    exifformat.js       # 控制栏 EXIF 值与卡片时间格式化
    app.js              # 主屏模型：整合以上模块，对外只暴露状态快照、动作与事件
    layout/             # 分区与各区几何（只依据 spec + tokens 推导）；胶片栏步长取 filmscroll.js
  render/               # 浏览器实现：procedural（矢量程序化照片，存储朝向）、decoder（代理/Full/增强）、store（特征库）、thumbs（缩略图队列）
  shell/                # DOM 组件：订阅 app 事件按区域增量更新，不整窗重建
    dom.js / menu.js    # 组件约定与工具（el/place/令牌 CSS 变量 --pv-<点分名转连字符，保留驼峰>）；共用弹出菜单
    icons.js / tooltip.js  # 图标字形（icon.* 令牌）与自绘悬停提示（data-tooltip 全局委托）
    filterbar / filmstrip / card / similarity / photo / analysis / controlbar / picker  # 各分区组件与 .css
  checks/               # ?check=<模块|all> 页内行为检查，按设计 ID 出报告
  demo/                 # 窗口外演示面板（尺寸、设置项、故障注入、减少动态效果）
tests/                  # node --test，测试名以设计 ID 开头
dev/                    # 开发用（不进产品）：render-preview（方向码/代理/Full/增强/连拍目视）、<组件>-harness（单组件自查页）
assets/                 # 图标字体子集：uicons-pv.woff2（描边）、uicons-pv-solid.woff2（实心星/叉）
tools/export-geometry.mjs  # 导出几何 JSON（--profile mac 与 macOS 报告比对；--count-width 固定计数宽，Design/README §8）
tools/subset-icons.py      # 从 Design/tokens.json 的 icon.* 码位生成 assets/ 下的图标字体子集
```

图标（TOOLBAR-04、FILM-05、FILTER-08、MENU-02）统一用 `icon.font` 字形，不写内联 SVG：`src/shell/icons.js` 的 `icon(t, token)` / `solidIcon(code)` 输出带 `pv-icon` 的文本节点，字号取各控件 glyph 令牌。字形一律放在单独的 `aria-hidden="true"` span 里，纯字形控件的可访问名称由 `aria-label` 提供，不得让私有区字符进入 `role="application"` 容器的可读文本；`?check=shell` 的 SHELL-06 会遍历窗口文本节点拦截回归。`src/assets/` 只放子集产物，改 `Design/tokens.json` 的 `icon.*` 后运行：

```bash
python3 PhotoViewer.Web/tools/subset-icons.py        # 需要 fonttools + brotli，pyftsubset 可用 PYFTSUBSET 指定
```

实心字形（点亮星、评分快捷命令星内叠数字）取自 uicons solid-straight（`U+FCD7` 实心星、`U+F4F8` 实心叉）；该字体已不在工作树，脚本回退到 git 历史 `78ed013^`，或用 `--solid-source` 指定。

- **依赖方向**：`shell/ → core/app.js → core/*`；`core/` 不得引用 `shell/`、`document`、`window`。时间一律经 `clock.js`，便于假时钟断言 300/350/500/4000 ms 等门槛。
- **状态唯一**：业务状态只在 `app.js`；组件不缓存第二份（菜单勾选每次弹出重读快照，MENU-02）。
- **星级来源（RATING-01/03/04/05）**：口径只在 `core/rating.js`，状态、冲突判定与写入边界按 [Design/spec/rating.md](../Design/spec/rating.md)。同名组里每个文件的内嵌 XMP 与每个实际存在的 `<stem>.xmp` 各算一个来源，没有读取/写入优先级；代表文件内嵌 Rating 只用于卡片/控制栏点亮星与 FILTER-03 的数字/「以上」档。冲突时卡片与控制栏 0 号槽位换成 `icon.warning` + `color.star.conflict` 的警示三角（几何与命中区不变）；三角只作指示、点击不写值。Web 不提供来源浮窗，点击三角不动作、不写值。整组写入不新增 sidecar；缺 canonical XMP 的来源（含 JPEG）一律预检失败，合成模型也不建模已有包缺 Rating 的插入。PNG 内嵌 XMP 写入不支持，sidecar 不受此限。已拒绝（-1）不点亮任何星、不提供设置入口，只命中「已拒绝」筛选；已有 canonical 的已拒绝来源可整组改写为 0–5。
- **分析栏细节绿框**：唯一活动目标在 `core/analysis.js`（`hoverDetail`），`shell/analysis.js` 只把指针序列翻译成该状态——悬停指针进入/离开、触控按下即亮、滑入另一细节瓦片切换、抬手/取消/失去捕获/失焦/隐藏/换图清除；任意时刻只有一个活动 pointer，旧 pointer 的抬起、取消或失去捕获不得清除新目标。键盘与辅助技术通过瓦片 `aria-pressed` 与 `detail=0` 激活等价显示/清除；诊断准星与双击定位独立（ANALYSIS-04）。
- **单位**：几何一律为逻辑点（CSS px = pt）；物理 100% 用 `devicePixelRatio` 换算（IMAGE-01）。
- **展示产物缓存**：`core/session.js` 按照片 id 保留最近 `cacheCount` 份已上屏 surface（与设置里的缓存数量上限、胶片栏细边同一份表）。命中要求缓存档位满足当前需求：需要 Full 时必须已是 Full，否则产物长边要覆盖当前代理目标或本身已是源分辨率。命中时不清旧图、不起加载提示计时，新 surface 与新几何在同一次调用里交给同一帧绘制（STATE-01「保留前一张」）；档位不足与没有产物一样按未命中清旧图走「黑屏加载」——低分辨率顶替默认禁用，需要 Full 时不得先拿代理顶替。代理→Full 的后台升级属于同一张照片的档位工作，不阻塞上屏（IMAGE-03）。
- **注释**：默认不写；只在实现某条规则的关键分支写 `// FILM-03：<为什么>`。不写阶段号。
- **风格**：4 空格缩进、双引号、分号；命名导出；不引入任何第三方依赖。
- **主图手势**：`core/input.js` 的 `PhotoGesture` 判定单指/鼠标的平移、轻扫、导航图拖动与捏合；导航图只接单指/鼠标拖动，第二指按下即取消拖动并把两指交给主图捏合（IMAGE-06），DOM 捕获与导航图命中留在 `shell/photo.js`。

## 平台差异（`@web`）

登记在 [Design/platforms.md](../Design/platforms.md)，本目录不另存一份清单。
