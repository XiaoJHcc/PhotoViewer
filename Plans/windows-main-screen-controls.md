# Windows 主屏控件统一方案（2026-10-20）

> 起因：作者对 Windows 主屏做了一轮外观复核，指出禁用态灰底、图标不贴切、悬停/按下闪烁、相似聚类文字悬停发黑、下拉框风格偏离、EXIF 值不够粗、控制栏溢出表现为滚动、相似面板右侧缺边距、直方图不该有准星，并要求放大指示器白框可拖动（全平台）。
> 本文是这批问题的统一控件方案与实现记录；规则正文已回写到 [Design/spec/toolbar.md](../Design/spec/toolbar.md)、[image.md](../Design/spec/image.md)、[similarity.md](../Design/spec/similarity.md)，平台差异写 [platforms.md](../Design/platforms.md)，验收状态写 [status.md](../Design/status.md)。

## 1. 范围与原则

覆盖 Windows 主屏上所有小型控件：控制栏命令按钮、评分星、EXIF 信息块、相似聚类开关、相似面板动作按钮、筛选下拉、胶片栏卡片星级。分析栏瓦片与导航图按各自模块规则处理。

三条原则：

1. **状态只改颜色**：常态/悬停/按下/禁用只改底色、描边、前景，不改尺寸、不缩放、不做透明度过渡。连续划过一排按钮时不得出现闪烁或跳动。
2. **禁用不换皮**：禁用保持常态底色与描边，只把内容降到 0.42 不透明度。禁止出现系统禁用灰底，也不得用近乎隐藏的透明度代替禁用态。
3. **小型控件允许原生**：下拉、菜单、tooltip 的形状与交互走 WinUI 原生；几何占位、语义与按键绑定仍按设计规范，不另画一套控件。

## 2. 状态与配色

| 控件 | 常态 | 悬停 | 按下 | 禁用 |
|---|---|---|---|---|
| 命令按钮 | `#333333` / 描边 `#555555` | `#3D3D3D` / 描边 `#666666` | `#2A2A2A` / `#555555` | 底色描边不变，内容 0.42 |
| 评分星（控制栏） | 透明底 | `#333333` | `#3A3A3A` | 透明底，内容 0.42 |
| 相似聚类开关（未选中） | 透明 / 描边 `#555` / 字 `#AAA` | `#333333` / 字 `#CCCCCC` | `#2A2A2A` / 字 `#CCCCCC` | 字 0.42，底色不变 |
| 相似聚类开关（选中） | `#1F3A5F` / 描边 `#3A6FA8` / 字 `#AECBEE` | `#26477A` / 字 `#FFFFFF` | `#26477A` / 字 `#FFFFFF` | 字 0.42 |
| 相似面板动作按钮 | `#2A2A2A` / `#555` / 字 `#CCC` | `#3A3A3A` / `#666` / 字 `#FFF` | `#333333` / `#555` | 内容 0.42 |
| EXIF 块 | `#222222`（无悬停/按下） | — | — | 显示 `--`，块不变 |

令牌：`color.toolbar.command.hover.background`、`color.toolbar.command.pressed.background`、`color.toolbar.command.hover.border`、`color.toolbar.rating.hover.background`、`color.toolbar.rating.pressed.background`、`color.toolbar.disabled.content.alpha`、`color.filter.toggle.hover.foreground`、`color.filter.toggle.checked.hover.foreground`、`color.sim.action.hover`、`color.sim.action.pressed`（`Design/tokens.json` → 生成 `DesignTokens.g.cs`）。

实现位置：`PhotoViewer.Windows/Native/WindowsControlStyles.cs`。WinUI 3 的 Button/ToggleButton 模板用主题键画各状态，只在元素上设 `Background` 会被禁用/悬停状态覆盖；这里按元素覆盖全部状态键（含 `*Disabled`），禁用态因此保持常态底色。内容禁用只在控件内容（字形/文字）上降不透明度，不动按钮自身。

## 3. 命令图标

旧壳的字形语义保留，Windows 用 Segoe Fluent Icons 等价字：

| 命令 | 字形 | 说明 |
|---|---|---|
| 打开文件 | `E8B7` Folder | 不用 OpenFile 的“带箭头文档” |
| 上一张 / 下一张 | `E76B` / `E76C` | 单向箭头 |
| 切换上一张 | `E8AB` Switch | 双向交换箭头 |
| 缩放适应 | `E73F` BackToWindow | 向外双对角箭头，与旧壳一致；不用 FitPage |
| 放大 / 缩小（预设与比例） | `E710` Add / `E738` Remove | 纯加号/减号，不用放大镜 |
| 分析栏 | `E9D9` Diagnostic | 保持 |
| 增强预览 | `E7A1` Contrast | 对比度半圆，与旧壳一致；不用闪电 |

映射表在 `PhotoViewer.Windows/Native/WindowsIcons.cs`；共享命令标识与快捷键 JSON 不变。

## 4. Tooltip 与 EXIF 排版

- 命令 tooltip 改走 `WindowsInput.FormatGesture`：`D2` → `2`、`Oem3` → `` ` ``（并补齐 `Oem1`–`Oem8` 别名）、`NumPad3` → `小键盘3`，与设置页同一套可读键名；未设置的绑定仍不显示占位。
- EXIF 值字号 12、**Bold**（原 SemiBold 在 12 点下与正文几乎同重）；标签保持 10 点次要色。

## 5. 筛选下拉

去掉 90×32 之外的全部自定义（`CornerRadius`、`BorderBrush`、`FontSize`），保留 `Width`/`Height`/`MinWidth=0` 让 WinUI 原生模板出图。占位仍与 `MainScreenLayout.FilterBarFrames` 的 90×32 对齐，不改变筛选条几何。

## 6. 控制栏换行

- 症状：列布局（控制栏在右列）高度不足时整条滚动，而不是三组换列。
- 根因：`ScrollViewer` 在可滚方向用无限主轴测量内容，`WindowsWrapPanel` 永远拿不到高度上限。
- 修法：`WindowsWrapPanel.MainLimit` 由 `ScrollViewer` 视口主轴驱动（`WindowsControlBar.UpdateWrapLimit`）；能整组换行时面板主轴铺满视口、`ScrollableMain` 为 0；只有单组本身超过主轴时才报告内容长度，交给滚动宿主兜底。滚动偏移的保存/夹取逻辑不变。
- 验收：smoke 在 700 宽、行/列两向断言 `GroupLineCount ≥ 2` 且 `ScrollableMain ≤ 0.5`。

## 7. 相似聚类面板

- 右侧边距：`RowWidth`/`PanelWidth` 已含 4 点外边距，旧实现又在外框上扣了一次，最后一张卡片被裁 4 点且右缘贴住分隔线。现按共享几何全额落宽，外框统一让出 `sim.panel.margin`（4），行/列两向都有右缘空隙。
- 文本可读性：列表项没有自绘悬停，问题来自开关在悬停/按下时回落到系统前景色；开关四态前景已全部显式覆盖（见 §2）。
- 动作按钮补齐 `color.sim.action.hover`（#3A3A3A）与按下态。

## 8. 直方图与准星

`ANALYSIS-08` 早已规定“直方图与细节预览不显示准星”。macOS/iOS 已按 `AnalysisTileKind` 排除；Windows 的 `SyncCrosshair` 改为只同步四张诊断瓦片并显式清除直方图，Android 自绘同样只给 `Diagnostic` 画准星。点按语义不变：直方图点击仍是吞掉事件、不落准星。

## 9. 导航图白框拖动（全平台）

- 规则改为 **白框可手动拖动定位**（IMAGE-06，必须）。按住框内拖动保留抓取偏移；按在缩略图其他位置先把框心移到该点；拖动只改视口中心、不改缩放；滚轮/捏合仍归主图。
- 共享：`PhotoViewport.CenterOnNormalizedPoint(u,v)` 保持当前 `Scale`，只改 `PanX/PanY`，走 `ClampPan` 与跟随对焦重算。
- Windows：`WindowsNavigator` 打开命中测试，`PressAt/DragTo` 换算归一化点，回调 `WindowsPhotoView.CenterOnNormalizedPoint`；滚轮事件仍冒泡给主图。
- macOS：`MacMainWindow` 的导航宿主改为 `NavigatorHostView`（flipped NSView，处理 MouseDown/Dragged/Up），交 `MetalPhotoView.CenterOnNormalizedPoint`。
- iOS/Android：原有整块导航图拖动改为“框内保留抓取偏移、框外先移框心”，语义与桌面一致。

## 10. 验收

| 项 | 证据 |
|---|---|
| 构建 | `dotnet build PhotoViewer.Windows` 0 错误 0 警告 |
| Windows smoke | `--pv-native-smoke` **25/25 PASS**（含新增“700 宽两向按组换行且不可滚”、导航图白框拖动后视口中心与框位置同步、诊断瓦片有准星/直方图无准星） |
| Windows 设置专项 | `--pv-native-settings` **7/7 PASS**（含 `D2`→`2`、`Oem3`→`` ` `` 的可读键名断言） |
| 目视 | 无照片时评分星与命令按钮禁用无灰底；命令图标为文件夹/对角箭头/加减/对比度；相似面板右缘有空隙 |
| Android 构建 | `dotnet build PhotoViewer.Android -c Debug` **0 错误**（仅既有 `AndroidHeifDecoder` 空引用警告）；未跑模拟器/真机检查 |
| macOS | 代码与 acceptance 断言已补（`CenterOnNormalizedPoint`）；本机无 Apple 工具链，未构建运行 |
| iOS | 代码已改（白框抓取偏移、缩略图区域判定）；本机无 Apple 工具链，未构建运行 |
| 作者手验 | 2026-10-21 常规 UI 通过（含悬停/拖框手感、下拉观感、EXIF 字重与 175%/200% DPI）；焦点环与系统下拉细节见 §11 |

## 11. 有意保留（作者确认）

以下四项是有意的平台差异或设计取舍，不是缺陷，不再作为待办：

- 命令按钮的键盘焦点环用 WinUI 系统焦点视觉，不做自定义描边；[TOOLBAR-10](../Design/spec/toolbar.md) 只要求焦点可见、不改几何、不被裁切，系统视觉已满足。
- 下拉回到原生后弹出层为系统深色菜单，列表项圆角与内边距由系统决定，与主屏深色底一致。
- 用户同时配置「预设缩放」与「比例缩放」两组命令时会出现两个加号/两个减号：命令内容由用户设置决定，不是壳重复生成；是否合并属设置模型议题。
- 相似面板行布局的停靠动作区宽度已计入可用宽分配（`WindowsSimilarityPanel.SetAvailableWidth` 把 `DockMeasureWidth()` 传给共享 `SimilarityPanelGeometry.RowWidth`，见 `Design/platforms.md` SIM-07），因此列表会被动作区占去一部分宽度，属设计行为。
