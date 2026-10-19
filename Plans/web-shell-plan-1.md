# PhotoViewer.Web — 主屏参考壳与在线演示（计划 1）

> 立项依据：作者要求把 `Design/prototype/` 的"只能看"样机重做为可操作的完整主屏，兼作设计功能的对标基准与免下载试用；可升格为与四端并列的 `PhotoViewer.Web`。
> 原则：**只依据 `Design/` 独立实现**，实现不出或实现错即先怀疑文档；视觉对标 macOS，已登记为 `@mac` 的差异不跟随。

## 0. 现状结论

审计 `Design/prototype/`（index/css/js/geometry/tokens/export 共约 2.8k 行）：

| 方面 | 现状 |
|---|---|
| 可用资产 | `tokens.json → tokens.js` 生成链路；`geometry.js` 逐函数移植自 `PhotoViewer.Shared`，与 Mac 几何报告节点级一致；`compare-layout-geometry.py` 比对工具 |
| 交互 | 仅胶片栏定位动画、控制栏按钮、星级、栏位开关可用；**筛选下拉、右键菜单、键盘、触控、真实缩放/平移、滚动条拖动、分析/相似交互全部缺失** |
| 结构 | `prototype.js` 单 IIFE、每次交互整窗重建 DOM（焦点丢失），不适合继续堆功能 |
| 准确性 | 当前几何比对实为 18 处差异（控制栏 17 点滚动条槽，疑为 Mac 报告早于 `0650402`），与"381 节点 0 差异"记录不符；STATE-03 叠放、500 ms 药丸门槛、导航百分比均为硬编码 |
| 独立性 | `geometry.js` 是从 C# 移植的，**没有检验过文档能否单独约束几何** |

结论：保留令牌管线与比对工具，样机交互层推倒重写；几何层按文档重写一遍，旧 `geometry.js` 只作对照预言机。

## 1. 定位与范围

**定位**：`PhotoViewer.Web` = 第五个壳，平台代号 `web`。第一阶段是"参考壳 + 在线演示"：数据为合成占位，业务逻辑完整；真实文件夹模式列为后续可选（§7）。

**附带收益**：今后 UI 规则变更可先在 Web 落地验证（迭代最便宜），再分发四端，等于给变更流程加了一个"可执行规范"环节。

**做**（主屏全部默认开启功能，外加相似面板这类主屏常用开关项）：

| 模块 | 必须具备 |
|---|---|
| SHELL | 行/列/自动布局；文件栏、控制栏显隐（持久化）；分析栏会话显隐；全屏（Fullscreen API）；三档窗口尺寸 + 拖拽改尺寸 |
| FILTER | 三个下拉可开可选（键盘可操作、勾选当前项、Esc 取消）、12 档星级语义、`共 N 张` 计数同帧提交、相似聚类开关 |
| FILM | 真实滚动容器（滚轮/横向滚轮/滚动条拖动/触摸）、FILM-01 定位请求表逐行复刻、350 ms 三次缓出与接续、用户接管撤销、减少动态效果、换向/重显时定位、卡片全部标识（当前、已缓存细边、抖动徽标、星级、清零叉、tooltip）、暂留（FILM-06）、空态 |
| IMAGE | 适应 ⇄ 物理 100%（`devicePixelRatio` 口径）、预设与比例缩放、双击锚点、拖动平移与钳制、对焦点跟随、切图倍率保持、代理 → Full 热替换、导航图（只展示）、增强预览 + 500 ms 药丸、加载 300 ms 提示、解码失败态、空态点击打开 |
| ANALYSIS | 6/7 瓦片等边长、对焦点瓦片条件出现、悬停绿框、双击定位 100%、共享准星与 cosine 参考点、直方图现算、三态（有内容/未提取/计算中）与即时计算门控 |
| SIM | 三态 + 动作区、两种空态文案、提取全部/补齐全部与进度、相似度第二行与柱条、点选双高亮、取消/重算时机、行布局 RowWidth |
| TOOLBAR | 三组固定顺序、整组换行与居中、溢出滚动、EXIF 格式化与 `--`、评分组方向（列布局 5→0）、命令组 tooltip 含主/次绑定、禁用态 |
| MENU | 主图右键菜单（项、分组、勾选每次重建、键盘与 Esc、夹在窗口内）；触屏模式长按卡片菜单 |
| INPUT | 默认键表、Repeatable 闸门、焦点抑制（菜单/输入）、离散滚轮一格一次、精确滚动平移、捏合、触屏轻扫翻页、长按 |
| STATE | 提示条 4 s 替换、与药丸叠放上移、写回失败星级不变 |
| OPEN | 打开文件/文件夹 = 在合成"文件夹"之间切换（同一共享打开流程：定位、回适应、代际作废） |

**不做**：设置窗口、工具窗口（菜单「工具…/设置…」与 EXIF 组点击按 SCOPE-01 不建入口，登记 `@web`）；真实 RAW/HEIF 解码、XMP 写回、DINO 推理、色彩管理（演示中以合成数据模拟，不宣称具备）。

设置项中影响主屏的（显示评分、同名合并、即时计算、相似阈值/数量、命令栏显示项、导航图开关、缩放预设）放在窗口外的演示面板里调整，走与设置提交相同的"即时重建"路径，便于验证 TOOLBAR-07 / FILTER-09。

## 2. "文档优先"的执行协议

这是本计划的核心，目的是检验 `Design/` 是否足以约束实现。

1. **隔离实现**：执行代理只允许读 `Design/`（spec、tokens、platforms 中与 `web` 无关的条目不得照搬平台实现）与 `PhotoViewer.Web/`；**禁止读取 `PhotoViewer*/` 下的 C# 源码与 Mac 验收常量**。规则缺失或含糊时按最合理理解实现，并在缺口台账登记，不得去代码里找答案。
2. **差分审计**：每个模块完成后，另派审计代理（可读全仓）对比 Web 行为与 Mac 实现、共享 C#，逐项分类：
   - `文档缺口`：规则没写或写不清 → 修 `spec/` 或 `tokens.json`；
   - `文档错误`：规则与作者认可的 Mac 行为冲突 → 按作者裁定修一方；
   - `Mac 缺口`：Mac 偏离规则 → 记入 `status.md`，列入四端待办；
   - `web 差异`：浏览器约束导致 → 登记 `<ID>@web`。
3. **台账**：`Plans/web-shell-gaps.md`，每条写设计 ID、现象、分类、裁定、落点（改了哪份文档/哪端）。裁定前不改 spec。
4. **几何独立重写**：几何层按 spec + tokens 重写，再与旧 `geometry.js`、Mac 几何报告三方比对；凡需要"偷看"旧代码才能对齐的数，都是令牌缺口。

已在调研中发现、可直接入账的缺口种子：

| # | ID | 问题 |
|---|---|---|
| 1 | FILM-05 | 卡片内部几何（外框 3/3/90/138、文件名/日期行位置、星级 14 步距、徽标偏移）不在 tokens，只存在于 Mac 验收常量 |
| 2 | FILM-05 | 第二行日期的**显示格式**未定义（Mac 为 `MM-dd HH:mm`），只定义了完整时间格式 |
| 3 | OPEN-02/03、STATE-02 | 引用"文件栏标题/文件夹名"，但 L1 分区与 FILTER-01"固定五项"中没有它的位置；Mac 不显示 |
| 4 | FILM-05、STATE-02 | 胶片栏空态「无匹配照片」Mac 未实现 |
| 5 | FILTER-06 | 相似聚类开关的四态视觉（常态/悬停 × 勾选）无规则与令牌 |
| 6 | TOOLBAR-08 | Mac 报告留 17 点滚动条槽，与"溢出才显示指示器"冲突；需重跑 `--pv-native-layout` 判定是报告过期还是 Mac 缺口 |
| 7 | INPUT-04 | 浏览器不暴露"精确滚动"标志，只能按 `deltaMode`/增量形态推断 → `INPUT-04@web`；Chrome 的触控板捏合以 Ctrl+wheel 投递，与"Ctrl+滚轮 = 比例缩放"绑定撞车 |
| 8 | INPUT-01 | 浏览器保留 Cmd/Ctrl+N 等组合不可拦截，「打开文件」主绑定在 Web 失效（次绑定 Ctrl+O 可用）→ `INPUT-01@web` |
| 9 | MENU-01、SCOPE-01 | 无设置/工具的平台，「工具…/设置…」与 EXIF 组点击的处置需要明文规则 |
| 10 | SHELL-03 | 浏览器视口可小于最小窗口 560×480，规则未定义此时行为 |

## 3. 技术方案

- **形态**：原生 ES modules + HTML/CSS，无框架、无运行时依赖。放弃 `file://` 双击打开（ESM 在 `file://` 下被 CORS 拦截），改为本地静态服务（`python3 -m http.server`）与 GitHub Pages；`Design/README.md` §8 相应改写。
- **测试**：纯逻辑模块用 Node 自带 `node --test`，零依赖；行为验收用页内脚本化检查（`?check=film` 等），按设计 ID 输出报告，与其他平台 `--pv-check` 同风格。
- **令牌**：`generate-design-tokens.py` 增加 ESM 产物 `PhotoViewer.Web/src/tokens.g.js`，并把 `platforms.web` 覆盖值一并生成；`--check` 覆盖新产物。
- **平台配置档**：`web`（默认）与 `mac`（仅为几何比对：交通灯让位 34、mac 滚动条行为），避免为了对表把 `@mac` 特例写进 Web 默认行为。

目录：

```text
PhotoViewer.Web/
├── index.html            # 产品模式：浏览器视口即窗口
├── src/
│   ├── tokens.g.js       # 生成
│   ├── core/             # 纯逻辑，无 DOM，可 node --test
│   │   ├── library.js    # 合成照片库（同名 RAW 组、缺 EXIF、失败、抖动、对焦点、方向码、特征缺失）
│   │   ├── filter.js     # 格式/同名合并/星级/排序/暂留/锚点翻页（FILTER-02~05、FILM-06）
│   │   ├── navigation.js # 当前照片双身份、定位请求（FILM-01）、打开流程与代际（OPEN）
│   │   ├── rating.js     # 写回状态机（可注入失败）
│   │   ├── viewport.js   # 适应/100%/预设/比例/平移/跟随/切图保持（IMAGE-02~05、09）
│   │   ├── input.js      # 键表、主/次绑定、Repeatable、手势解码与抑制（INPUT）
│   │   ├── session.js    # 加载 300 ms、代理→Full、增强 500 ms 药丸、提示条队列（STATE）
│   │   ├── analysis.js   # 瓦片集合与三态、准星、cosine（ANALYSIS）
│   │   ├── similarity.js # 三态、计算/取消、提取进度、RowWidth（SIM）
│   │   └── layout/       # 分区、筛选条、胶片栏、控制栏、分析栏、相似面板几何（按文档重写）
│   ├── shell/            # DOM 组件，按区域增量更新，不整窗重建
│   │   ├── menu.js       # 下拉与右键菜单共用的菜单组件（MENU-03 键盘/夹取/无障碍）
│   │   ├── filmstrip.js  # 原生滚动容器 + 虚拟化 + 定位动画器
│   │   ├── photo.js      # Canvas 主图、手势、导航图、绿框
│   │   └── …             # filterbar / controlbar / analysis / similarity / toast
│   ├── assets/procedural.js  # 程序化占位图（Worker + OffscreenCanvas，含细节纹理以便 100% 可辨）
│   └── demo/             # 窗口外演示面板：尺寸、照片数、故障注入、延迟、减少动态效果、设置项
├── tests/                # node --test
└── AGENTS.md
```

占位图全部程序化生成（不入库二进制）：每张有声明的源尺寸与方向码，代理与 Full 分档生成并模拟延迟；直方图对真实像素现算；DINO/锐度/抖动瓦片用确定性伪随机 32×32 网格，cosine 按合成 patch 特征真实计算，点选参考点即可看到变化。

## 4. 里程碑

| 里程碑 | 内容 | 验收 |
|---|---|---|
| W0 立项与骨架 | 目录、ESM 骨架、令牌 ESM 产物、`node --test`、VS Code「Serve Web」任务；`platforms.md` 登记 `web` 代号；缺口台账建档并录入 §2 种子；重跑 Mac `--pv-native-layout` 刷新基准 | `--check` 通过；空壳可在本地服务打开 |
| W1 核心逻辑 | `core/` 全部模块，按 spec 写单测（每条测试名带设计 ID） | 单测全绿；FILM-01 表、FILTER-03 表、IMAGE-02 表、INPUT-01 表逐行有用例 |
| W2 布局与静态视觉 | `layout/` 按文档重写；各区域 DOM 与 Mac 视觉对齐（深色、系统字体、系统蓝、原生风下拉）；几何导出 | `mac` 档 vs Mac 报告 0 差异（或差异全部入账裁定）；与 Mac 截图三档尺寸并排目视 |
| W3 交互 | 胶片栏滚动/定位/接管、下拉与右键菜单、键盘与重复闸门、滚轮二分、捏合、触控（轻扫、长按卡片菜单）、缩放平移、分析与相似交互、提示条与药丸 | 页内 `?check=` 各模块报告 PASS；作者在浏览器（含触屏模拟）手验 |
| W4 差分审计与修文档 | 逐模块审计（§2），裁定后修 `spec/`、`tokens.json`、`platforms.md`；`status.md` 增 Web 列；Mac 缺口转四端待办 | 台账全部有裁定；文档改动后 Web 回归仍绿 |
| W5 收口与发布 | `Design/prototype/` 退役（几何导出迁入 Web，`Design/README.md` §8、根 README/AGENTS/DEV 改写）；GitHub Pages 发布（**需作者确认**：涉及推送工作流与公开站点） | 线上地址可直接试用；退役后文档无死链 |

执行分工：W1–W3 按模块并行派 DeepSeek Flash，每个任务给明确的设计 ID 清单与验收用例；W2 几何重写与 W4 审计由主模型把关裁定。

## 5. 验证方法

- **逻辑**：`node --test PhotoViewer.Web/tests`。
- **几何**：`mac` 档导出 → `python3 Tools/compare-layout-geometry.py`，容差 0.5。
- **行为**：页内检查脚本按 ID 断言（如 FILM-01 逐行：点选不滚、下一张滚、排序不滚、改星不滚；FILM-03：滚轮中断动画；STATE-03：药丸 + 提示条底边 60）。
- **视觉**：同尺寸与 Mac 截图并排，作者目视；必要时用内置浏览器截图存证。
- 构建通过不提升交互验收状态（Design/README §7）。

## 6. 文档影响（W0/W4/W5 落地）

- `Design/README.md`：平台代号增加 `web`；§8 样机改为指向 Web 壳与几何导出；权威顺序中"样机"改为"Web 参考壳"。
- `Design/platforms.md`：新增 `@web` 条目（INPUT-01/04、MENU-01、SCOPE-01、SHELL-02/03 等）。
- `Design/status.md`：增 Web 列。
- 根 `AGENTS.md`、`README.md`、`DEV.md`：项目结构、运行方式、在线地址；新建 `PhotoViewer.Web/AGENTS.md`。
- `Tools/generate-design-tokens.py`：新增 ESM 产物与 `platforms.web`。

## 7. 后续可选：真实文件夹模式

在参考壳稳定后再评估：File System Access API（Chromium）或 `<input webkitdirectory>` 读本地 JPG/PNG/WebP（Safari 另可 HEIC），自写 EXIF 解析；评分写回受浏览器能力限制（Chromium 可写回，其余只能导出），按 SCOPE-01 如实呈现。照片不离开本机，不上传。

## 8. 已定默认（作者可推翻）

1. 先做合成数据的参考壳，真实文件夹模式后置。
2. 放弃 `file://`，改本地服务 + Pages。
3. Web 进 `status.md` 第五列；规则变更可先在 Web 验证再分发四端。
4. 几何层按文档重写，旧 `geometry.js` 只作对照，W5 随样机一起退役。
5. 下拉与右键菜单统一用自绘菜单组件（浏览器无 Mac 风原生弹层），登记 `MENU-03@web` / `FILTER-08@web`。

## 9. 进度

| 里程碑 | 状态 | 记录 |
|---|---|---|
| W0 | ✅ 10-20 | 骨架、`tokens.g.js`（含平台覆盖）、Serve/Test Web 任务、台账建档；Mac `--pv-native-layout` 重跑 PASS，报告与旧版逐字节相同 → 17 点差异坐实为 Mac 缺口（G6，`-AppleShowScrollBars WhenScrolling` 下 0 差异） |
| W1 | ✅ 10-20 | `core/` 12 个模块 + `layout/` 7 个文件，227 个单测全绿（测试名带设计 ID）；七个子代理只读 `Design/` 独立实现。按文档重写的几何与 Mac（悬浮滚动条偏好）9 布局 363 节点比对仅 18 处差异，全部来自计数文本宽（G11）；新增缺口 G11–G23 |
| W2a | ✅ 10-20 | `core/app.js` 主屏模型（状态快照/动作/事件/菜单模型/持久化）+ `exifformat.js`；`render/` 程序化照片、解码器、特征库、缩略图队列；281 单测全绿；`dev/render-preview.html` 目视确认方向码、代理/Full 构图一致、增强与连拍；新增缺口 G24–G27 |
