# tools.md 拆分笔记（草稿，不属于规范正文）

来源：`Design/ui-contract.md`（§3 TOOLS-01、§4 对账表、iOS/iPadOS 差异）、`Plans/native-rebuild-phase-2-ui.md`、`Plans/native-rebuild-phase-3.md`、`Plans/native-rebuild-phase-ipad.md`、`Plans/native-rebuild-phase-android.md`、`Plans/native-rebuild-windows-p2.md` §18、`Design/architecture.md` §5.4，以及四端原生源码与旧壳 `PhotoViewer/Views/Tools/`、`PhotoViewer/ViewModels/Tools/`。

## platforms

### TOOLS-01 入口、单例与只读边界

| 端 | 实现 | 事实 |
|---|---|---|
| macOS | `PhotoViewer.Mac/Native/MacToolsWindow.cs`、`MacNativeApplication.cs:130-143`、`MacMainWindow.cs:777,895` | 独立 AppKit `NSWindow`（Titled/Closable/Miniaturizable/Resizable），首次 `ShowOrActivate` 懒创建；初始 880×720、min 560×420、`DarkAqua`、标题「工具」、居中于主窗口；关闭后对象保留可重开。入口：主图右键「工具」（无省略号）+ 控制栏 EXIF 分组（`MacControlBar.cs:44`，整组 `Clicked=openTools`） |
| iOS/iPadOS | `PhotoViewer.iOS/Native/iOSToolsPresenter.cs`、`iOSContextMenus.cs:63`、`iOSControlBar.cs:37,70` | `PageSheet` + `UINavigationController`，深色导航栏（背景 `#121212`），右上「关闭」；列表页两个入口。主图长按菜单「工具…」与控制栏 EXIF 组（`AccessibilityLabel=EXIF 信息，点击打开工具`）同一入口 |
| Android | `PhotoViewer.Android/Native/AndroidToolsPresenter.cs`、`AndroidNativeRootView.cs:129,554,610` | `BottomSheetDialog`，内容高 90% 屏高、peek 55%，同一 sheet 内 root/exif/dino 三态；返回键 `HandleBack` 先回 root 再关闭。入口：主图长按菜单「工具…」与控制栏 EXIF 组 |
| Windows | `PhotoViewer.Windows/Native/WindowsToolsWindow.cs`、`WindowsNativeToolsPresenter.cs`、`WindowsMainWindow.cs:378` | 独立 960×720（min 800×560、不进任务栏、可缩放不可最大化）Mica + `TitleBar` + `NavigationView`；单例，关闭即释放，下次重新创建。入口：右键「工具…」+ 控制栏 EXIF 整组 `Tapped`，均走 `MainViewModel.OpenToolsWindow` |

差异要点：
- 桌面非模态；iOS/Android 为覆盖式浮层，打开期间主窗口不可操作（与正文 TOOLS-01 一致）。
- Mac 通过 `NativeWindowPresenter.ShowOrActivateToolsWindow(_main.Tools)` 直接前置（不重置导航）；Android 走 `MainViewModel.OpenToolsWindow()`（先 `Tools.ShowList()`）；Windows 同样走 `OpenToolsWindow`，但窗口保留自身分类选择（见问题 1）。
- Mac 键事件监视器按 `nativeEvent.Window != Window` 早退（`MacMainWindow.cs:173-184`），工具窗口获焦时主窗口快捷键不派发；Android 的 BottomSheet 是否拦下硬件键盘未验证（见问题 11）。

### TOOLS-02 工具外壳、落点与空状态

| 端 | 外壳 | 细节 |
|---|---|---|
| macOS | 列表 + 子页 | `MacToolsWindow.LayoutShell`：返回按钮通宽 32 高、外边距 8；列表外边距 16、按钮高 32、间距 8。列表只有 EXIF 详情 / DINO 诊断（`MacToolsWindow.cs:89` 注释明确不建照片统计入口） |
| iOS/iPadOS | 列表 + 子页（导航栈） | 列表页 `iOSNativeToolsListViewController`：16 外边距、8 间距、32 按钮高；`ViewWillAppear` 调 `BackToList()`，即每次回到列表都复位共享导航状态（`iOSToolsPresenter.cs:193-196`） |
| Android | 列表 + 子页（同一 sheet） | root 两入口；`Page` 由 `ToolsViewModel.CurrentTool` 决定 |
| Windows | 常驻分类 | 三分类左栏；`SelectInitialPage` 首次按共享状态或「EXIF 可用→EXIF，否则统计」；`EnsureSelection` 在再次请求时恢复上次分类（`WindowsToolsWindow.cs:190-211`） |

顺序事实：旧壳与 Mac/iOS/Android 列表顺序为 EXIF 详情 → DINO 诊断 →（照片数据统计）；Windows `NavigationView` 顺序为 EXIF 详情 → 照片数据统计 → DINO 诊断（windows-p2 §18.1）。正文取前一种，Windows 现状记为待对齐（问题 2）。

### TOOLS-03 EXIF 详情：表格与文本

| 端 | 控件与入口 | 事实 |
|---|---|---|
| macOS | `MacExifDetailView.cs` | 自绘 `NSView` + `ExifTableGeometry`；7 列、ID 60、2:2:3、值 3 行尾部截断；标签 `NSTextField` 设 `Selectable = true`（`:497-502`）；分组标题 16 号粗体、卡片 `#2a2a2a` 圆角、ID 单元格 `#222`、分隔线 `#3a3a3a` |
| iOS/iPadOS | `iOSExifDetailView.cs` | `UITableView`（sections = 元数据分组，`RowHeight=0` 自算），表头为文件名 + 格式按钮；使用共享 `ExifTableGeometry`；文本不可选中 |
| Android | `AndroidToolsPresenter.cs:273-293` | 线性行：60dp ID + 名称/中文名/值权重 2:2:3，值 `MaxLines=3`、`Ellipsize=End`；不使用 7 列网格（无分隔线列，行间另画分隔线）；文本不可选中 |
| Windows | `WindowsExifToolPage.cs`、`WindowsLayout.cs:553-562` | 7 列分组表格；单元格内边距 8/6（非共享 10/8）；表格最小宽 720 会把 `Compression` 折断，故工具窗口 min 宽抬到 800（windows-p2 §18.3）；文本不可选中 |

共同数据源：`ViewModels/Tools/ExifDetailViewModel.cs`；分组来自 `Core/Exif/ExifLoader.cs:55` → `ExifMetadataGrouper.ExtractAll`，顺序即解析顺序。加载状态文案：旧壳「加载中...」、Mac「加载中...」、Android「载入中…」。

### TOOLS-04 同一照片的格式切换

- 共享实现：`ExifDetailViewModel.BuildFormats`（当前文件 + `HiddenFiles` 伴侣、RAW 扩展名集合 `:66-70`、RAW 优先 + 字母序 `:208-215`）、默认选 RAW `:129-130`、`UpdateToFile` 保留同名格式标签并丢弃过期加载 `:143-195`、伴侣惰性加载 `:220-256`。
- 切换控件：Mac 自绘胶囊按钮；iOS 表头 `UIButton` 组；Windows `SelectorBar`；Android 自绘胶囊（plan 记「格式切换按钮为自绘胶囊」为已知视觉差异）。
- 已知时序问题：打开文件夹时当前项可能落在同名组的非代表项，`Tools.ExifDetail` 在伴侣关系建立前构建，只显示单一格式；共享层时序问题，Mac 同源（ipad plan 2026-10-17 记录）。

### TOOLS-05 DINO 诊断：瓦片与准星

- 数据与编排：`PhotoViewer/Core/AI/DinoDiagnosticPresenter.cs`；瓦片顺序与说明文字在构造函数 `:94-102`；点击任意瓦片 `OnTileClicked`（归一化坐标 → 32×32 参考点 → `PatchHeatmap.ComputeRefCosine`）；空白点击 `ClearCrosshair`；切图时 `CrosshairU/V=null`、参考点回 `16,16`（`:243-247`）。
- 布局：旧壳/Mac/iOS/Android 为三列两行，第二行第三格放刚体拟合文本（`DinoDebugView.axaml:40-106`、`iOSDinoDiagnosticView.cs:37-40`、`AndroidToolsPresenter.cs:295+`）。Windows 为 3+2 等宽、第六格留空、刚体文本全宽（windows-p2 §18.3.2、§18.6），150% 下五张均 206 DIP。
- 矢量场配色统一走 `CvHeatmap.ColorForShake`；判定文字与缩略图徽标同源 `ShakeClassifier`。

### TOOLS-06 计算时机、状态与中断

- 共享 presenter：先 `TryReadCachedAsync`（指纹 → `ReadPatchesAsync` + `ReadCvGridAsync`），命中跳过推理/CV；不写库。状态字符串：`未加载` / `计算中…  {file.Name}` / `无法解码图片` / `失败: {message}`；缺数据瓦片 `Placeholder = "未提取"`。
- 联动条件：`ToolsViewModel.SyncCurrentFile` 仅在 `CurrentTool is DinoDebugViewModel` 时 `SetSource`（`ToolsViewModel.cs:104-106`）；进入页面 `OpenDinoDebug` 总是 `SetSource`（`:81-85`）。
- 未实现：离开 DINO 页或关闭工具窗口时不取消在途任务（presenter 只在下次 `SetSource`/`Dispose` 取消）；重新进入同一照片总是重算（无「数据未变」早退）。正文按 SCOPE-01/PRIN-07 收紧，见问题 6。

### TOOLS-07 照片数据统计（Windows 专属）

- 可用性：`ToolsViewModel.IsPhotoStatsAvailable => OperatingSystem.IsWindows()`（`ToolsViewModel.cs:35`）；旧壳入口 `IsVisible`/`IsEnabled` 同时绑定（`ToolsView.axaml:34-37`），Mac/iOS/Android 不建入口。
- 实现：`WindowsStatsToolPage`（文件夹行 + 通配符 + `FileSavePicker` + 取消/进度/状态）、`WindowsToolsDialogs`；文件夹与保存选择器绑定工具窗口自身句柄；业务在 `PhotoStatsViewModel` / `Core/Tools/PhotoStatsService.cs`。
- 业务事实：默认通配符 `*.HIF,*.JPG`（`PhotoStatsViewModel.cs:20`）；逗号/分号分隔、空则 `*.*`、递归 + 去重（`PhotoStatsService.cs:29-46`）；CSV 列「文件名,等效焦距(mm),星级」、UTF-8 BOM（`:73-82`）；单文件失败返回空焦距 + 0 星，不中断（`:53-68`）。
- 平台差异（已登记/未登记）：`@win` 专属、`@win` 窗口不显示在任务栏、不可最大化；Android 分析栏手机瓦片偏小等与工具无关，不在此列。

## status

> 以下为拆分时从 ui-contract §4（第 157 行 TOOLS-01 行）与各 plan 复制的原始措辞，仅供回填 `status.md`，不得进入规范正文。

- TOOLS-01
  - Windows：「原生已落地（2026-10-17）：Mica + `TitleBar` + `NavigationView` 三分类（EXIF 详情 / 照片数据统计 / DINO 诊断）+ `SettingsCard`，960×720（min 800×560、不进任务栏）；入口为右键「工具…」与控制栏 EXIF 块；`--pv-native-tools` 5 项 PASS；真实多格式切换与文件夹/CSV 选择器待作者手验」；另有「主图右键「工具…」与控制栏 EXIF 块点击均实机验证可打开工具窗口」（windows-p2 §18.4）
  - macOS：「独立 AppKit 工具窗口（EXIF 详情 / DINO 诊断，照片数据统计不建入口）；`--pv-native-tools` PASS（懒创建单例、EXIF 6 组、DINO 5 瓦片、关闭重开）」（ui-contract §4；`native-tools-checks.txt`，2026-10-16）
  - iOS/iPadOS：「sheet + 导航控制器，列表页只有 EXIF 详情 / DINO 诊断；`--pv-native-tools-check` PASS（模拟器两机型 + 真机）；触摸手感待作者真机」（ui-contract §4，2026-10-17）
  - Android：「原生壳 2026-10-18 模拟器目视：工具 BottomSheet 列表只有 EXIF 详情/DINO 诊断；EXIF 分组/列/格式切换、DINO 5 瓦片 + 刚体拟合文本可见；照片数据统计不建入口」；`--es pv-check tools` 16 项 PASS（native-rebuild-phase-android.md 文首与 §验收）
- TOOLS-02：无独立验收记录（四端外壳形态分别记录在 TOOLS-01 行内）。
- TOOLS-03：Windows「EXIF 6 组 / 25 标签 + 单格式标签（文件名去后缀）」（windows-p2 §18.4 第 2 项，2026-10-17）；macOS「EXIF 页 6 组 / 50 标签渲染 + 返回按钮」（phase-3 §3，2026-10-16）；iOS「`--pv-native-tools-check` PASS」；Android 目视（4 列，登记差异）。
- TOOLS-04：Windows「多格式 `SelectorBar` 程序化只覆盖单格式素材；真实伴侣切换待作者手验」（windows-p2 §18.5）；macOS/iPad「筛选完成后切到带伴侣的代表项复核多格式切换」（ipad plan，2026-10-17）；Android 目视格式切换。
- TOOLS-05：Windows「DINO 5 瓦片出图、3+2 同宽网格（206 DIP）且两排上沿齐平、状态 = 文件名、点击瓦片把 cosine 参考点设为 8,8」（windows-p2 §18.4 第 3 项）；macOS「DINO 页 5 张瓦片 + presenter 状态就绪」；iOS 工具页 DINO 5 瓦片 PASS；Android 目视「5 瓦片 + 刚体拟合文本、点按落准星」。
- TOOLS-06：随 TOOLS-05 的各端套件；无「离开页面取消」的断言。
- TOOLS-07：Windows「照片统计文件夹行/通配符写回/CSV 导出/移除」（windows-p2 §18.4 第 4 项）；「文件夹/CSV 选择器是 WinRT 系统对话框，程序化无法可靠驱动，验收只覆盖 VM 与列表/导出数据路径」（§18.5）。macOS/iPadOS/Android：不适用（不建入口）。

## questions

1. **工具窗口再次打开的落点不一致。** macOS 从右键/EXIF 块进入直接调 `NativeWindowPresenter`（`MacMainWindow.cs:777`），关闭后再开仍停在上次的子页；iOS 每次列表重现都 `BackToList()`（`iOSToolsPresenter.cs:193-196`）；Android 走 `OpenToolsWindow()` 先 `ShowList()`（`AndroidNativeRootView.cs:610`、`ToolsViewModel.cs:71-88`）；Windows 无首页、首次默认 EXIF、再次请求恢复上次分类（`WindowsToolsWindow.cs:190-211`）。正文按「打开回首页、已开着只前置」写，macOS/iOS 现状与之不符，需作者定夺。
2. **工具顺序冲突。** 旧壳与 macOS/iOS/Android 列表为 EXIF 详情 → DINO 诊断 → 照片数据统计（`PhotoViewer/Views/Tools/ToolsView.axaml:25-37`），Windows `NavigationView` 为 EXIF 详情 → 照片数据统计 → DINO 诊断（windows-p2 §18.1）。正文取前者，Windows 是否重排？
3. **EXIF「加载中」被当成「没有 EXIF」。** `ToolsViewModel.SyncCurrentFile` 在 `imageFile.ExifData == null` 时把 `ExifDetail` 置空；若正停在 EXIF 页则 `ShowList()`（`ToolsViewModel.cs:91-107`）。切图瞬间 EXIF 尚未加载完，用户会被踢回首页；需要区分「未加载」与「确实无 EXIF」，正文 TOOLS-02 空状态表按后者写。
4. **EXIF 文本可复制是否四端要求？** 旧壳名称/中文名/值是 `SelectableTextBlock`（`PhotoViewer/Views/Tools/ExifDetailView.axaml:40-47`），macOS 原生 `Selectable=true`（`MacExifDetailView.cs:497-502`）；iOS/Windows/Android 为不可选标签（`iOSExifDetailView.cs:370`、`WindowsExifToolPage.cs`、`AndroidToolsPresenter.cs:283-293`）。正文 TOOLS-03 按旧壳意图写成应当，属三端缺口。
5. **Android 的 4 列算差异还是缺口？** `AndroidToolsPresenter.TagRow` 只有 4 个内容列（60dp ID + 2:2:3 权重），没有 7 列网格的分隔线列（`AndroidToolsPresenter.cs:273-293`；android plan §194 与发现 #26）。正文登记为 `TOOLS-03@android`，需确认是否接受为长期平台表现（平板也一样）。
6. **DINO 诊断重复推理与不取消。** 每次 `OpenDinoDebug` 都 `SetSource` 重算（`ToolsViewModel.cs:81-85`、`DinoDiagnosticPresenter.cs:106-112`）；离开页面或关闭工具窗口不取消在途任务（只有下次 `SetSource`/`Dispose` 取消）。与 SCOPE-01「只在可见时工作」和 PRIN-07 的张力：正文写成必须取消，四端目前都未做到。
7. **工具窗口尺寸没有令牌域。** 正文只写「独立可缩放窗口」，没给数值；现值为 macOS 880×720 / min 560×420（`MacToolsWindow.cs:20-28`）、Windows 960×720 / min 800×560（windows-p2 §18.1）、iOS sheet、Android 90% 屏高。tokens 允许域里没有 `tools.*`，是否新增 `tools.window.*` 还是并入 `shell.*`？
8. **菜单项省略号。** macOS「工具」（`MacMainWindow.cs:895`），iOS/Android/Windows「工具…」（`iOSToolsMenu`/`iOSContextMenus.cs:63`、`AndroidNativeRootView.cs:554`、windows-p2 §18.1）。正文按 macOS 惯例保留差异，未统一。
9. **Windows 工具窗口不能最大化、不进任务栏**（windows-p2 §18.5）是已登记 `@win` 差异还是待改？影响用户把 EXIF 表拉宽阅读。
10. **照片统计的字段与文件覆盖。** 默认通配符只有 `*.HIF,*.JPG`，不含 RAW（`PhotoStatsViewModel.cs:20`）；CSV 不写文件夹/路径，同名文件无法区分（`PhotoStatsService.cs:73-82`）。是否为有意设计？
11. **Android 工具 sheet 打开期间的键盘抑制未验证**：BottomSheetDialog 是否拦下硬件键盘（`AndroidNativeRootView` 的键盘处理在 Activity 层），正文 TOOLS-01 要求不派发照片快捷键，需实测。
