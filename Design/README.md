# PhotoViewer 设计规范总则

本目录是四端（Windows / macOS / iOS·iPadOS / Android）与 Web 参考壳 UI 与交互的唯一设计基准。Web 参考壳（[PhotoViewer.Web](../PhotoViewer.Web/AGENTS.md)）只依据本目录实现，是本规范的可执行版本：规则改动可以先在它上面验证，它与各端的差异用来发现规范缺口。Avalonia 旧壳退役后，对标不再以任何一个平台的实现为准：先改设计，再分别在四端执行。

> 迁移完成（2026-10-19，[Phase 6 清单](../Plans/native-rebuild-phase-6.md) 6B）：`spec/`、`tokens.json`、`platforms.md`、`status.md` 已从原 `ui-contract.md` 拆分并成为唯一真源；旧文件只保留跳转说明。

## 1. 组成

| 位置 | 层 | 内容 |
|---|---|---|
| `spec/` | L0–L2 | 原则、分区、行为规则（按模块分文件） |
| `tokens.json` | L3 | 尺寸、间距、动效、颜色语义、字号层级等数值的唯一来源；由 `python3 Tools/generate-design-tokens.py` 生成 `PhotoViewer/Core/Design/DesignTokens.g.cs`（共享程序集，四端与共享会话均可引用）与 Web 参考壳的 `PhotoViewer.Web/src/tokens.g.js`；`--check` 校验是否最新 |
| `platforms.md` | L4 | 各平台控件选型、实现入口与已登记的平台差异 |
| `status.md` | — | 按设计 ID 记录四端实现与验收状态；规则正文不写状态 |
| `baseline/avalonia/` | — | 冻结的旧壳几何与截图，只读，仅供追溯 |
| [architecture.md](architecture.md) | — | 非 UI 的跨模块架构与流程（不在本规范分层内） |

## 2. 分层

- **L0 原则**：产品目标与不可违背的体验原则，例如键盘优先、空间优先给主图、选片进度不丢失、画质红线。
- **L1 分区**：屏幕与窗口的分区及各自职责（窗口外壳、文件栏、筛选条、胶片栏、主图、分析栏、控制栏、相似面板；设置窗口、工具窗口）。
- **L2 行为规则**：每条一个设计 ID，写清意图、触发条件、不触发的操作、边界、并发与中断、持久化和错误状态。只比较截图不算规则。
- **L3 设计令牌**：规则里出现的数值写成令牌名，可附当前值，如 `motion.film.scroll.duration`（现 350ms）；两处不一致时以 `tokens.json` 为准。
- **L4 平台实现**：同一规则在各平台用什么控件、入口在哪个文件、哪里有登记过的差异。

## 3. 规范等级与对标强度

条款用三个等级：**必须**（四端一致，偏离即缺口）、**应当**（默认如此，偏离须登记为平台差异）、**可以**（平台自定）。

| 范围 | 对标强度 |
|---|---|
| 主窗口分区结构、几何、占位、边距、方形约束、动效参数 | 必须对齐 tokens 与 Web 参考壳导出的几何 |
| 主窗口内的小型控件（下拉、菜单、按钮外观、tooltip、滚动条） | 功能与操作必须一致；外观可以原生。没有原生控件、需要自绘的平台（Web、Android 自绘弹层）以旧壳 Avalonia Fluent 深色外观为准，数值见 `dropdown.*`、`popup.*`、`menu.*`、`tooltip.*` 令牌 |
| 设置窗口、工具窗口 | 分类、项目、行为与持久化必须一致；工具窗口的导航与容器与该平台设置统一（macOS 原生侧栏、iPad 分组列表 + 设置同款 sheet、Windows 常驻分类、Android Material 浮层）；视觉应当遵循各平台规范（macOS 系统设置、Win11 设置、iOS 设置、Material 3） |
| 主屏图标 | 语义与字形必须一致：四端与 Web 共用 `icon.font`（uicons Regular Rounded，描边）/ `icon.fontSolid`（uicons Solid Straight，只用于实心星）与 `icon.*` 码位，尺寸取各控件的 glyph 字号令牌；平台系统图标集只能作为已登记的平台差异 |
| 色彩、画质、物理 100% | 必须一致，由验收素材与色卡判定，不由 Web 参考壳判定（其照片为合成数据） |

悬停、按下、禁用与焦点反馈属于 L2 行为（TOOLBAR-10）：四端都不得因状态切换产生布局抖动、残留旧态或裁切内容。原生控件可以使用系统状态色与轻量状态动效（L4），但禁用必须仍可辨、不近似隐藏；自绘控件按 `color.*` / `toolbar.*` 令牌绘制，圆角不与系统 bezel 或系统描边裁切叠加。

## 4. 编号

- **设计 ID**：`<模块>-<两位序号>`，如 `FILM-03`。ID 永不复用；废弃时标"已退役"并指向替代条目。条款内的细项不另编号，需要单独引用时拆成新 ID。
- **平台差异**：`<ID>@<平台>`，平台取 `win` / `mac` / `ios` / `android` / `web`，如 `FILM-04@mac`。`web` 是只依据本目录实现的浏览器参考壳（[PhotoViewer.Web](../PhotoViewer.Web/AGENTS.md)），用于在线试用与检验本规范能否独立约束实现。
- **令牌名**：点分 `域.元素.属性`，段内 lowerCamelCase，如 `film.card.width`、`nav.longEdge`。域：`shell` `filter` `film` `image` `nav` `analysis` `toolbar` `sim` `status` `exiftable` `tools` `input` `motion` `color`。设置项的取值范围与默认值属于设置模型，不进令牌。
- **阶段号**（`P1-M4`、`I5`、`A3`、`W-M7` 等）只出现在 `Plans/` 与提交信息里，不进 `Design/` 与代码注释。
- **代码注释**：在实现某条规则的关键分支、或偏离通常写法的地方写 `// FILM-03：<为什么>`；验收断言名与报告同样引用 ID。不必逐行标注。
- 不使用 `P` 字头前缀，避免与解码优先级 `P0`–`P3`、Display P3 色域混淆。

模块前缀登记（新增前缀先在此登记）：

| 前缀 | 范围 |
|---|---|
| `PRIN` | L0 原则 |
| `SHELL` | 窗口外壳、标题栏、分区布局与显隐 |
| `FILTER` | 筛选条 |
| `FILM` | 胶片栏 |
| `IMAGE` | 主图显示、缩放、导航图 |
| `TOOLBAR` | 控制栏 |
| `ANALYSIS` | 分析栏（原 `ANL-02`–`04` 改为同号 `ANALYSIS-02`–`04`；`ANL-01` 是旧壳实现约束，随 Avalonia 退役） |
| `SIM` | 相似聚类面板 |
| `MENU` | 主图与卡片上下文菜单 |
| `INPUT` | 键盘、鼠标、触控与快捷键 |
| `OPEN` | 打开文件/文件夹、外部打开、拖入 |
| `STATE` | 加载、空、失败与提示状态 |
| `SETTINGS` | 设置窗口 |
| `TOOLS` | 工具窗口 |
| `SCOPE` | 功能边界与隐式计算约束 |

## 5. 权威顺序

作者最新决定 > `spec/` > `tokens.json` > Web 参考壳 > 冻结基线 > 各平台实现。平台实现与上层不一致即为缺口，除非已登记为 `@平台` 差异。macOS 原生壳是已通过作者验收的交互参考实现，但不是规范来源。

## 6. 变更流程

1. 改规则：在 `spec/` 新增或修订条目；数值改 `tokens.json`；主屏规则同步改 Web 参考壳并跑 `?check=all`（可先在 Web 上试做，确认规则写得出来再分发四端）。
2. 在 `status.md` 把该 ID 四端置为"待实现"。
3. 各平台实现，注释与验收引用 ID，完成后回填状态。
4. 实现中发现规则不可行：回到 `spec/` 修订或登记 `@平台` 差异，不在代码里悄悄偏离。

规则与实现可以在同一提交，但规则先写。

## 7. 验收状态

`代码核对` / `程序化通过` / `作者手验通过` / `待核验` / `缺口` / `延期` / `不适用`。构建通过不提升交互验收状态；状态格只写状态词与简短覆盖边界，OS、设备、素材与证据路径记在对应计划文档；不能把一个平台的结果复制给另一个平台。

## 8. Web 参考壳、几何比对与基线

- Web 参考壳覆盖完整主屏：行/列/自动布局、栏位显隐、筛选、胶片栏定位、缩放平移与导航图、分析栏、相似面板、控制栏、右键菜单、键盘与触控；设置与工具窗口不做（`SCOPE-01@web`）。照片、特征与故障均为程序化合成数据。运行与参数见 [PhotoViewer.Web/AGENTS.md](../PhotoViewer.Web/AGENTS.md)；`?check=all` 按设计 ID 输出行为检查报告。
- 它导出的几何 JSON 与各平台验收程序导出的格式一致，用脚本比对，不需要两台机器同时运行：
  ```
  node PhotoViewer.Web/tools/export-geometry.mjs --profile mac --count-width 52 --out /tmp/web-geometry.json
  python3 Tools/compare-layout-geometry.py /tmp/web-geometry.json release/native-p2-acceptance/native-layout-geometry.txt
  ```
  `--profile mac` 打开 macOS 交通灯让位等已登记差异；`--count-width` 固定计数文本宽（各平台字体度量不同）。比对脚本自动识别 JSON 与 `native-layout-geometry.txt`；只比较双方共有布局/节点，单轴容差默认 0.5 点（`--tolerance` 可调），单方独有节点只列出、不算失败。节点命名：显式布局用 `root.<分区>`、`filter.<控件>`、`control.group<i>[.child<j>]`、`card<i>.<部件>` 等，Auto 布局只导出顶层 `photoPane`。
- `baseline/avalonia/` 冻结后不再更新，不作为验收标准，只用于追溯原设计意图。
