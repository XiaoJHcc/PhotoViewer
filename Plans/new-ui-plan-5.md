# 新 UI 全平台合规起点 · Plan 5 · Windows 主屏静态对标

> 依据：[Plan 3](new-ui-plan-3.md) 的静态首屏规则包、[Plan 4](new-ui-plan-4.md) 的 Mac / Web 宽形态收拾结果，以及作者本轮要求。分支：`new-ui`。
> 阶段号 `W1–W4` 只用于本计划、验收证据与提交信息，不进入 `Design/` 和代码注释。

## 1. 目标与边界

把 Plan 3 / Plan 4 已在 macOS 与 Web 落地的宽形态主屏静态布局迁到 Windows：以桌面规范为标准、Web 为参照（Mac 壳无法在 PC 上运行对照），形状、尺寸与悬停区对标 Mac / Web，装饰（底色、描边、模糊、阴影）用 Windows 原生方案；同时改正既有主屏中既非 Windows 原生、也不符合现定规范的部分。

- **包含**：满窗口主图与适应区（SHELL-01/11）、栏位材质与工具区（SHELL-12）、44 点圆形命令按钮（TOOLBAR-04）、224×44 星级胶囊与槽内正圆悬停（TOOLBAR-03/10、RATING-07 展开态）、横排衬底 / 竖排无衬底 EXIF（TOOLBAR-05）、桌面三合体 / 单胶囊筛选（FILTER-01/07，内部设计按现有简单实现）、流式换组（TOOLBAR-01/02/08）、分析栏工具区几何（ANALYSIS-05）。
- **不包含**：筛选计数与菜单的最终内部设计、星级弹窗 / 拖动、手机形态（Windows 无手机形态）、旧共享几何（`ControlBarLayout`、`MainScreenLayout.FilterBarFrames`）删除。

## 2. 方案要点

| 项 | 落地 |
|---|---|
| W1 外壳 | `WindowsMainWindow.LayoutShell` 取共享 `ShellPaneLayout.Partition`；`WindowsPhotoView` 铺满窗口，`SetFitInsets` 给出适应区（放大态按适应区中心位移平移，同 Mac）；文件栏、分析栏、控制栏按矩形浮在主图上，栏位 XAML 只铺透明命中底；导航图 / 提示 / 药丸 / 提示条落在适应区覆盖层。窄宽度常驻标题栏改为安全区顶边让位。 |
| W1 材质 | WinUI `SwapChainPanel` 不能被 XAML 亚克力取样，把原有「标题栏单段交换链亚克力」推广为 `BackdropRegion` 列表：文件栏（含分隔线）、工具区、只剩评分与命令时每个控件的圆角衬底、标题栏，依次在交换链内按 `AcrylicInAppFillColorDefaultBrush` 运行时配方绘制；输出色彩转换按同一圆角判定排除最多 32 段。 |
| W2 筛选 | `WindowsFilterBar` 按 `WideFilterLayout`：原生按钮材质胶囊内三段透明按钮（状态层同心内缩 3 点）、段间分隔线，`Button.Flyout` + `MenuFlyout` 单选；放不下改单胶囊三分组菜单；计数在星级段 / 单胶囊底边；AI 开关为默认样式 `ToggleButton`。 |
| W3 控制栏 | `WindowsControlBar` 改用 `ToolbarFlowLayout` 文档面板；命令为默认样式 `Button` 44 圆钮；星级胶囊取原生按钮材质，六槽 `SlotRect` 落位、悬停 / 按下为槽内内切正圆；EXIF 横排原生卡片材质块、竖排无衬底 52 点容器；同名命令、槽位与 EXIF 项就地保留。删除 `WindowsWrapPanel`。 |
| W3 分析 / 相似 | 分析栏面板透明、按工具区口径落内边距并从主轴前导起排瓦片；相似面板列布局宽改为 `sim.panel.width` + 外边距（与 Mac 同口径）。 |
| W4 验收 | 新增 `WindowsShellLayoutAcceptance`（行/列 × 分析栏开关 + 透明工具区）：适应区让到栏缘、交换链区段与工具区背景条件、唯一分隔线、胶囊 / 圆钮外形、筛选形态、分析栏交叉轴，并逐组合导出窗口截图。旧断言按新结构等价改写：标题栏背板、常驻栏让位、筛选菜单不被诊断通知关闭、命令按钮实例就地保留、评分 0 号槽几何。 |

## 3. 自主裁定

- 浮动控件衬底：工具区透明（只剩评分与命令）时，Mac 玻璃按钮自带材质；Windows 原生没有玻璃，按「原生控件放在亚克力上」的层次给胶囊与圆钮各铺圆角亚克力，再叠原生按钮材质。
- 缩放适应图标：原 `E73F` BackToWindow 实为向内箭头（与标题栏「退出全屏」同形），与 TOOLBAR-04「向外双对角箭头」及 Mac 不符，改为 `E740`。
- 原生化：命令按钮、星级胶囊、筛选胶囊与 AI 开关去掉自定义状态色，回到 Fluent 默认材质与系统状态；评分槽状态层用 `SubtleFillColor*`；相似面板动作按钮与 Mac 同为令牌色，保持不变。
- 单胶囊菜单点选即收起：WinUI `MenuFlyout` 点击恒关闭，登记 FILTER-08@win。

## 4. 验收结果

| 检查 | 结果 |
|---|---|
| 构建 | standard / AI Debug 0 错误 0 警告；`Tools/WindowsTypeCheck` 通过 |
| 冒烟 `--pv-native-smoke` | standard 45/45（基线 40）、AI 47/47（基线 42），新增 5 项外壳几何 |
| 专项 | color 双版 23 PASS + 1 BOUNDARY（真屏 ACM/HDR 未切换）；edition standard 12/12、AI 14/14；settings 19/19；tools 9/9 |

截图（本机 1778×1264 物理像素，150%）在 `artifacts/acceptance/new-ui/windows-static/`（不入库）：`shell-{row,col}-{plain,analysis}.png`、`shell-col-bare.png`。AI 版因本机有用户实例占用常规输出，构建到隔离输出目录运行。

## 5. 待作者复核与后续

- 作者目视：亚克力栏位色阶（Fluent 应用内亚克力比 Web 深色半透明略亮）、圆钮 / 胶囊的 Fluent 抬升描边、悬停与按下观感、150%/200% DPI。
- 旧共享几何清理：`ControlBarLayout`、`MainScreenLayout.FilterBarFrames/FilterBarMainLength` 已无壳消费，删除需同步 `CoreContractCheck` 并复跑其他平台回归。
- 星级字形定稿（TOOLBAR-10 16/12 与 RATING-07 20/15）沿用 Plan 4 待裁定项，Windows 现取桌面 16/12。
