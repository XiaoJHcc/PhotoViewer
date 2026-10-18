# shell.md 拆分草稿：平台事实、验收状态与待决问题

> 仅供拆分与核对使用，不是规范。规则正文见 [../shell.md](../shell.md)；本文件只记录 SHELL 模块的平台实现、验收状态与未闭合问题。

## platforms

### SHELL-01

- **macOS**：`MacMainWindow.Layout` 用共享 `MainScreenLayout.Partition` 落 AppKit frame；列布局文件栏 116 + 1 分隔线，行布局文件栏 = 筛选条 + 胶片栏带 159 + 1；筛选条前导 34 为隐藏标题栏的交通灯让位；相似面板在列布局扩宽文件栏、行布局从胶片栏右侧切出（`SimilarityPanelGeometry.RowWidth`，封顶可用宽一半）。
- **iPad**：`iOSMainScreenLayout` 在共享 `MainScreenLayout` 上叠加真实 `SafeAreaInsets`；栏位背景铺到屏幕边缘、内容收进安全区；行布局筛选条 `alignLeading` 从安全区前导边 + 10 起排，列布局仍在 116 栏内居中；旋转、Split View、Stage Manager 只改尺寸与安全区，不新增布局模式。
- **Windows**：`WindowsLayout` 排五分区（文件栏 / 主图 / 分析栏 / 控制栏 + 分隔线），2 个方向同构；文件栏内容宽 116 + 1 px 分隔线；caption 标题左界随文件栏变化；`Border.Width` 在 WinUI 含边框，带边框占位须按“内容宽 + BorderThickness”计算（`native-rebuild-windows-p2.md` §12.2）。
- **Android**：`AndroidMainScreenLayout` 在共享分区上叠加 systemBars/displayCutout insets；手机纵向恒为行布局（400dp 级宽度放不下 116 栏 + 主图），横向按 Auto 规则列布局；**不新增手机专属布局模式**（`native-rebuild-phase-android.md` §1.7）。行布局筛选条 `alignLeading: true`，窄屏用 84/76 紧凑控件。
- **已登记差异**：`SHELL-01@android` 纵向只走行布局；`SHELL-01@mac` 行布局整组居中为交通灯让位；`SHELL-01@ios / @android` 无交通灯，行布局从内容前导边起排。

### SHELL-02

- **macOS**：`NSWindow` 用 `FullSizeContentView` + `TitleVisibility.Hidden` + `TitlebarAppearsTransparent`，保留交通灯/缩放/关闭/全屏；顶边本地事件监视器在指针 Y ≤ 50 且窗口为 key window 时显示两个拖动区（行高 44 / 列高 24），鼠标移出或 `DidResignKey` 收起；拖动交给系统 `PerformWindowDrag`。隐藏式标题栏**不自带**双击，`HandleTitlebarDoubleClick` 按 `AppleActionOnDoubleClick` 分派（Maximize→`Zoom`、Minimize→`Miniaturize`、None→不动作），命中拖动区即吞事件；全屏不接管（`MacMainWindow.cs:111-115,173-191,223-246,500-508`）。
- **Windows**：`OverlappedPresenter.SetBorderAndTitleBar(hasBorder: true, hasTitleBar: false)` 去掉系统标题栏与系统按钮，`WindowsTitleBar` 自绘标题文本 + 最小化/最大化/全屏/关闭四个 36×32 按钮；指针进 50 DIP 热区显示，移开或失活隐藏；拖动区只在标题栏可见时登记为系统 caption（`InputNonClientPointerSource.SetRegionRects`），隐藏态零区域；caption 右界按实际按钮布局推导，不压按钮、不吞筛选条（`WindowsTitleBar.cs:11-18,35-71`；`WindowsMainWindow.cs:390+`）。**未找到自绘标题栏的双击处理**，系统 caption 区域的双击行为未专项验证。
- **iPad**：无窗口装饰与标题栏双击；隐藏状态栏（根控制器 `PrefersStatusBarHidden => true`，实测 `safe.Top = 0`）；设置与栏位显隐由主图 `UIContextMenuInteraction` 长按菜单承担；主图菜单锚在手指（触点处 1 点透明定向预览），照片不抬升不缩小。
- **Android**：窗口装饰属平台差异；`Theme.PhotoViewer.Native` 固定深色、edge-to-edge + insets；长按主图弹自绘 `PopupWindow`（屏幕坐标夹取、可滚动、返回键先关）；无标题栏双击语义。

### SHELL-03

- **macOS**：系统绿色按钮/显示菜单 `ToggleFullScreen`；全屏态写 `NSUserDefaults`（`PhotoViewer.Native.Fullscreen`），启动时按该值恢复（`MacMainWindow.cs:189-190,208-219`）；默认 1200×850、min 560×480。
- **Windows**：自绘全屏按钮走 `OverlappedPresenter` 最大化/全屏切换，`--pv-native-smoke` 断言最大化/还原/全屏/退出全屏四态；默认 1200×850、min 560×480；退出全屏回有边框还原态。
- **iPad**：无应用内全屏入口；多任务尺寸由 Split View/Stage Manager 决定；`IPadOS 26` 拒绝程序化方向切换，两向用 `--pv-layout-mode` 核对。
- **Android**：无应用内全屏入口；edge-to-edge + system bars insets；Activity/窗口尺寸由系统决定；平板沿用两向布局，不做专项适配。
- **设置/工具窗口**：Windows 设置 960×720（min 720×560）、工具 960×720（min 800×560），均不进任务栏、随主窗口关闭；macOS 设置窗口独立、工具窗口 880×720；iPad/Android 为 sheet/BottomSheet，不适用桌面最小尺寸。

### SHELL-04

- **macOS**：窗口帧 `FrameAutosaveName = PhotoViewer.Native.MainWindow`；胶片栏/控制栏显隐写 `PhotoViewer.Native.ShowFilm/ShowControls`；全屏写 `PhotoViewer.Native.Fullscreen`；布局模式写设置 JSON；启动读取后夹到当前屏幕。
- **Windows**：布局模式写设置 JSON（与四端同源）；胶片栏/控制栏显隐取旧壳口径，仅会话内内存态（`native-rebuild-windows-p2.md` §4），窗口位置/尺寸不持久化，每次启动显式 `AppWindow.Resize(1200×850)`。
- **iPad**：布局模式写设置；栏位显隐、窗口尺寸、全屏不跨会话（由 UIScene/系统恢复）；相似聚类开关写设置。
- **Android**：布局模式写设置；栏位显隐不跨会话；Activity 重建由系统 `savedInstanceState` 恢复，应用不额外持久化窗口帧。
- **共享规则**：排序/星级筛选、当前照片、滚动位置、缩放/增强都不写设置（`SettingsModel` 无对应字段）；相似聚类 `SimilarityPanelExpanded` 写设置。

### SHELL-05

- **macOS**：`DidChangeBackingProperties` 触发 `Photo.SetFrameSize` 重算物理尺寸；跨屏色彩/画质作者 2026-10-16 验收通过；Metal 纹理按新 backing scale 重标，不重读文件。
- **iPad**：旋转/分屏按“同一图像点留在视口中心 + 同一相对缩放”重建滚动基准；真机旋转与 Split View 手感待作者手验；`UIScreen` 色域变化由 Metal 层与展示编码策略处理。
- **Windows**：`XamlRoot`/DPI 变化重算 caption 与代理目标；200%/175% DPI 与真实跨屏待验；W-M7 前不做 ICC/ACM 广色域标签。
- **Android**：`OnSizeChanged`/`SurfaceChanged` 保留 `CenterUV` 并补帧；多屏/折叠屏未专项验证；P3 表面探测失败回退 sRGB。
- **未实现/未验证**：系统缩放变化时保持“同一相对倍率”在四端未做统一断言；跨屏不重读文件由各加载会话保证，壳层不重新解码。

### SHELL-06

- **macOS**：窗口按钮为系统交通灯，辅助技术名称由系统提供；拖动区是透明 `NSView`，不进入键盘焦点链。
- **Windows**：自绘窗口按钮只设 `ToolTipService.SetToolTip`，**未见 `AutomationProperties.Name`**；拖动区是系统 caption 区域，不进入 XAML 焦点链。
- **iPad/Android**：无自绘窗口按钮；主图长按菜单是窗口级入口，菜单项有标题文本；触控目标由系统菜单保证。
- **减少动态效果**：macOS 读 `NSWorkspace.AccessibilityDisplayShouldReduceMotion`（胶片栏 350ms 定位直达终点）；Windows 读 `UISettings.AnimationsEnabled`（定位直达）；iPad 读 `UIAccessibility.IsReduceMotionEnabled`；Android 读 `Settings.Global.ANIMATOR_DURATION_SCALE == 0`。壳层布局切换本身没有过渡动画。

## status

> 口径同 ui-contract.md §4：程序化通过 ≠ 作者手验；未登记项标“未单独登记”。日期照抄来源。

- **SHELL-01**：macOS 程序化通过（`--pv-native-layout` 等回归）；iPad 2026-10-17 模拟器两机型 `--pv-native-layout-check` 24 checks 通过（两向/自动、真实安全区、隐藏栏位释放空间、方形控件 60×60/50×40/30×30/40×40）；Windows 2026-10-17 150% 下三尺寸两向 + 最大化/还原往返程序化通过（200%/175%、真实跨屏待验）；Android 2026-10-18 模拟器 `pv-check main` PASS 14（矩阵/真机门未做）。
- **SHELL-02**：macOS 程序化 + 真实双击真机双向切换（`--pv-native-input` 四点断言）；Windows 150% 下 caption 契约、最大化/还原/全屏/退出全屏往返程序化通过，真实鼠标拖动/最大化已实机，**双击、200%/175%、跨屏与手感待验**；iPad 2026-10-17 长按菜单与 1 点定向预览在 `input` 27/28/29 覆盖；Android 长按菜单目视（自绘 PopupWindow 夹取/返回关闭）。
- **SHELL-03**：未单独登记；Windows 窗口四态往返由 `--pv-native-smoke` 覆盖；macOS 全屏持久化与恢复未见专项断言（仅代码路径）；iPad/Android 不适用（无应用内全屏入口）。
- **SHELL-04**：未单独登记；macOS frame autosave/显隐/全屏偏好为代码事实，未见跨会话专项断言；Windows 显隐仅内存态、窗口帧不持久化（与 P2 方向不一致）；iPad/Android 由系统恢复。
- **SHELL-05**：未单独登记；macOS 跨屏色彩/画质作者 2026-10-16 通过；Windows 200%/175% 与真实跨屏未验（W-M6/W-M7）；iPad 旋转/多任务待作者手验；Android 矩阵与真机未做。
- **SHELL-06**：未单独登记；Windows 自绘按钮缺无障碍名称（代码核对）；macOS 系统按钮/菜单由系统提供；触屏长按菜单可用。

## questions

1. **文件范围与重复内容**：本轮要求写 13 个前缀，但 `Design/spec/` 下已由其他分支并存 `filter.md`、`image.md`、`toolbar.md`、`analysis.md`、`similarity.md`、`menu.md`、`input.md`、`open.md`、`state.md`、`settings.md`、`tools.md`、`scope.md`。为免同一 ID 在两个文件里含义不同，本文件已收窄为 **SHELL only**；`state.md` 内仍有一份 `SHELL-01`–`03`（`state.md:7-20`），与本文件重叠且编号含义不同，需要删除或合并。请确认最终归属。
2. **SHELL 新 ID 分配**：现有 ID 只有 `SHELL-01`（布局/显隐）与 `SHELL-02`（标题栏）。本文件新增 `SHELL-03` 全屏与尺寸、`SHELL-04` 持久化、`SHELL-05` 跨屏、`SHELL-06` 无障碍；`state.md` 把“全屏 + 持久化”合并为 `SHELL-03`。以哪套为准？若以本文件为准，`state.md` 的 SHELL 段应删除。
3. **栏位显隐持久化等级**：P2 UI 方向是“窗口位置、尺寸及现有持久化状态延续”（[native-rebuild-phase-2-ui.md:131](../../../Plans/native-rebuild-phase-2-ui.md)），但 Windows 显隐只存内存（[native-rebuild-windows-p2.md:129](../../../Plans/native-rebuild-windows-p2.md)），iPad/Android 也不持久化。正文写 `应当`；是否改为“桌面必须、移动端不适用”，并把 Windows 登记为缺口？
4. **Windows 标题栏双击**：自绘标题栏没有双击处理代码（`WindowsTitleBar.cs` 只有按钮与 tooltip；`WindowsMainWindow.cs:390+` 需再核 caption 区域是否收到系统双击）；ui-contract 也把 Windows 双击列为待验。SHELL-02 要求按系统偏好执行，Windows 是补实现还是登记为平台差异？
5. **移动端“不提供全屏入口”**：正文写 iPad/Android 不提供应用内全屏。iPad 的 Stage Manager/Split View、Android 的系统栏隐藏与手势是否已覆盖“全屏”需求？还是需要应用内全屏入口？
6. **标题文本的定位**：正文写“标题文本只作窗口元数据”，但 Windows 自绘标题栏实际绘制标题（`WindowsTitleBar.cs:37-46`）。是允许自绘平铺标题，还是要求 Windows 改为不绘制（只留系统 caption 元数据）？
7. **拖动区高度的令牌口径**：`shell.dragzone.row.height`（44）/`shell.dragzone.column.height`（24）是 macOS 实测值，`shell.titlebar.hotzone.height`（50）与 Windows 的 50 DIP 热区一致但语义不同（热区 vs 拖动区高）。Windows 是否也应有 `SHELL-02@win` 的拖动区高度令牌，或在正文改为“按平台标题栏形态取值”？
8. **窗口帧持久化的 Windows 缺口**：SHELL-04 写“桌面应当恢复窗口位置/尺寸”，Windows 当前每次 1200×850、无持久化。是补实现还是把 Windows 登记为差异？
9. **窗口按钮无障碍**：SHELL-06 要求自绘按钮有可读名称，Windows 只有 tooltip（`WindowsTitleBar.cs:157`），没有 `AutomationProperties.Name`。是新增要求还是降为 `可以`？
10. **全屏与最大化语义**：Windows 自绘全屏按钮与 `OverlappedPresenter` 状态机在 smoke 中四态往返通过，但“退出全屏回到还原态而非最大化态”是否覆盖“从最大化进入全屏”的路径？代码按 `_isFullScreen` 记录前一态，仍需作者手验。
11. **自动布局的等宽高**：`MainViewModel.UpdateScreenOrientation` 用 `width > height` 判横屏，`Auto => !IsScreenLandscape`，即 **宽 == 高时取行布局**（`MainViewModel.cs:300-315`）。这是有意还是应保持上一方向？
12. **交通灯让位令牌**：`shell.filter.leadingInset`（34）与 `shell.filter.margin`（10）并存，前者只在 macOS 列布局生效；iPad/Android 行布局用 10。令牌名未体现平台限定，是否改为 `shell.filter.trafficLightInset`？
13. **相似面板与 116 栏**：列布局相似面板“扩宽文件栏”后，文件栏总宽 = 116 + 面板宽 + 分隔线？正文只写 116 是内容基准；具体总宽由 `SimilarityPanelGeometry.PanelWidth`（110）与 `MainScreenLayout.FileBarMainLength(extraCross)` 给出，需与 SIM 文件对齐口径。
14. **最小尺寸与文本缩放**：560×480 是 100% 文本缩放下的值；Windows 125/150/200% 文本缩放、macOS 辅助功能大字体、Android fontScale 1.3 下是否仍以逻辑点 560×480 为最小，还是允许内容压缩/滚动？
15. **减少动态效果与布局**：SHELL-06 写“布局切换、全屏切换直接到位”，但当前四端布局切换本就没有过渡动画，规则是否只是声明性条款？若未来加过渡，需在这里补时长/缓动令牌。
16. **窗口级错误处理**：SHELL-06 提到“全屏失败、恢复窗口帧失败”的可见提示，但四端都没有对应错误路径；是新增要求还是删除该条？
17. **观片环境固定深色**：SHELL-05 写“不随系统亮/暗切换”，但 Windows 设置/工具窗口用 Mica（会随系统主题回退），主窗口固定 `RequestedTheme=Dark`；边界是“主窗口 + 观片区固定深色，设置/工具可跟随平台材质”还是全部固定？
18. **macOS 标题栏双击的最小化语义**：`AppleActionOnDoubleClick` 取 `Minimize` 时调用 `Miniaturize`，`None` 时不动作，缺省 `Zoom`；正文写“缺省缩放/最大化、最小化、不动作”，与实现一致，但 `Zoom` 在部分 macOS 版本是“缩放”而非“最大化”，文案是否改为“缩放/最大化”？
19. **Windows caption 边界**：正文要求“caption 边界按实际按钮布局推导”，Windows 实现按 `_buttons.TransformToVisual` 取左界、布局未完成时用常量兜底（`WindowsTitleBar.cs:88-103`）；该兜底是否算合规实现，还是必须等布局完成再登记？
20. **iPad 状态栏**：SHELL-02 写“iOS 隐藏状态栏”，实现依赖根控制器 `PrefersStatusBarHidden() => true` 且 `UIViewControllerBasedStatusBarAppearance=true`；若未来显示状态栏，正文需要允许安全区自动让位，当前未写这条边界。
