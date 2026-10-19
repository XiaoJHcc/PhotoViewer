# 平台实现（L4）

本文件记录同一设计规则在四端用什么控件、实现入口在哪里、哪些差异已经登记。分层定义见 [README §2](README.md#2-分层)：L4 只回答"怎么做"，不写规则本身，也不写验收状态（见 [status.md](status.md)）。平台代号取 `win` / `mac` / `ios` / `android` / `web`（[README §4](README.md#4-编号)）。

实现入口均为工作区相对路径；行号指向类声明或关键分支，便于跳转。已登记差异写成 `**ID@平台**：…（已登记 10-18）`，来源为 [rulings-G1](../Plans/native-rebuild-phase-6-rulings/rulings-G1.md)…[rulings-G6](../Plans/native-rebuild-phase-6-rulings/rulings-G6.md) 与 [rulings-overrides](../Plans/native-rebuild-phase-6-rulings/rulings-overrides.md)。未登记的偏离不再写在本文件，一律进 [status.md](status.md) 的缺口列与 [Phase 6 统一清单](../Plans/native-rebuild-phase-6-unify.md)。

## 控件选型总览

| 分区 / 功能 | macOS | iOS·iPadOS | Windows | Android |
|---|---|---|---|---|
| 窗口外壳与分区 | AppKit `NSWindow` + `MainScreenLayout.Partition` 落 frame | `UIViewController` + 安全区 + `MainScreenLayout` | WinUI 3 五分区（XAML Grid），`WindowsLayout` 转接共享几何与令牌 | `Activity` + insets + `AndroidMainScreenLayout` |
| 胶片栏 | `NSCollectionView` + `MacFilmstripLayout` | `UICollectionView` + `iOSFilmstripLayout` | `ItemsRepeater` + `StackLayout` + `ScrollViewer` | `RecyclerView` + `AndroidFilmstripView` |
| 筛选条 | `NSPopUpButton`×3 + 自绘 `FilterToggleButton` | `UIButton` + `UIMenu` | `ComboBox`×3 + `ToggleButton` | 自绘 `View` + 自绘 `PopupWindow` |
| 主图 | `MetalPhotoView`（`CAMetalLayer`）+ 共享 `MetalPhotoRenderer` | `UIScrollView` + 透明 `CAMetalLayer` 子层 | `SwapChainPanel` + D3D11 F16 | `SurfaceView` + EGL14/GLES |
| 导航图 | 自绘 `NSView`，只展示 | 自绘 `UIView`，点按/拖动定位 | `WindowsNavigator`，不吃指针 | `AndroidNavigationOverlay`，点按/拖动定位 |
| 分析栏 | `MacAnalysisPanel`（`NSView`） | `iOSAnalysisPanel`（`UIView`） | `WindowsAnalysisPanel`（`Grid` + `IAnalysisHost`） | `AndroidAnalysisPanel`（自绘 `View`） |
| 控制栏 | `MacControlBar`（`NSView`） | `iOSControlBar`（`UIView`） | `WindowsControlBar`（`Grid` + `WindowsWrapPanel`） | `AndroidControlBar`（单个自绘 `View`） |
| 相似面板 | `MacSimilarityPanel` + `NSCollectionView` | `iOSSimilarityPanel` + `UICollectionView` | `WindowsSimilarityPanel`（`Grid`） | `AndroidSimilarityPanel`（`LinearLayout` + `RecyclerView`） |
| 主图 / 卡片菜单 | `NSMenu` | `UIContextMenuInteraction` + `UIMenu` | `MenuFlyout` | 自绘 `PopupWindow` |
| 设置窗口 | SwiftUI `NavigationSplitView` + `Form(.grouped)` | UIKit 半屏 `PageSheet` | WinUI `NavigationView` + `SettingsCard` | Material 3 `BottomSheetDialog` |
| 工具窗口 | 独立 AppKit `NSWindow`（列表 + 子页） | `PageSheet` + `UINavigationController` | 独立 WinUI 窗口（常驻三分类） | `BottomSheetDialog`（root / exif / dino 三态） |
| 状态提示 | 自绘提示条 4 s | 自绘提示条 4 s | 自绘状态提示条，4 s | `TextView` 提示条，4 s |
| 输入 | `NSEvent` + `MacInput` | `UIPress` + `AppleInputMapping` | `WindowsInput`（虚拟键 / 滚轮） | `AndroidInputMapping`（`KeyEvent`） |
| 打开 | `NSOpenPanel` + 根视图拖入 | `UIDocumentPicker` + `UIDropInteraction` | `FileOpenPicker` / `FolderPicker` + `Drop` | SAF `ACTION_OPEN_DOCUMENT_TREE` + `ACTION_VIEW/SEND`（拖入见 OPEN-07@android） |
| EXIF 详情表 | 自绘 `NSView` + `ExifTableGeometry`（7 列） | `UITableView`（7 列几何） | `Grid`（7 列） | 线性行（4 个内容列承载 7 列几何，见 TOOLS-03@android） |

> Windows 列记录 `native-rebuild-win` 分支（已于 2026-10-18 合入 `refactor/native-rebuild`，作者在 Windows 本机验收）的接线：头工程链接 `PhotoViewer.Shared`、主图视口使用共享 `PhotoViewport`，`WindowsLayout` 从「并行常量」改为转接共享几何与令牌。

## 令牌层平台差异（tokens.json `platforms`）

下表只是把 `tokens.json` 已登记的 `platforms` 值集中呈现。2026-10-18 令牌对账后只保留输入方式、文字度量与控件结构造成的平台值；**其余 30 项原先的平台值已统一为令牌单值**（逐项来源与缺口见 [Phase 6 统一清单](../Plans/native-rebuild-phase-6-unify.md)），不再按平台保留。

| 令牌 | 平台值 | 说明 |
|---|---|---|
| `filter.toggle.width` | `android` 76 | 仅 <600 宽手机用紧凑宽（FILTER-01@android） |
| `image.hint.detail.gap` | `android` 24 | 行高 24 + 内边距 6，视觉等效 28（IMAGE-01@android） |
| `analysis.tile.innerCornerRadius` | `win` 0 | 单层裁切（ANALYSIS-02@windows） |
| `color.text.secondary` | `macos` / `ios` system-secondary-label | Apple 系统次级文字色，与 #AAA 肉眼差 ≤2 阶（G6-34@mac/@ios） |
| `tools.window.default.width` | `win` 960 | 常驻侧栏 240 + EXIF 表格 720（TOOLS-01@win） |
| `tools.window.default.height` | `win` 720 | 与 macOS 同高 |
| `tools.window.min.width` | `win` 800 | 7 列 EXIF 表格最小宽 |
| `tools.window.min.height` | `win` 560 | 侧栏 + 表格的最小高 |

## film

胶片栏共享几何与动画在 `PhotoViewer.Shared/FilmstripGeometry.cs`：外框 `:10`/`:12`，内边距 `:16-20`，居中偏移 `:84-85`，350 ms 三次缓出 `:89-119`。卡片内层占位面 90×138 见 `PhotoViewer.Mac/Native/MacThumbnailItem.cs:64`、`PhotoViewer.iOS/Native/iOSThumbnailCard.cs:20`、`PhotoViewer.Windows/Native/WindowsLayout.cs:62-68`（转接 `film.card.*` 令牌）。

- **FILM-01、FILM-02、FILM-03**：定位请求与动画入口。
  - macOS：`PhotoViewer.Mac/Native/MacMainWindow.cs:39`（`NSCollectionView`）、`:162`（创建）、`PhotoViewer.Mac/Native/MacFilmstripView.cs:9`（布局）；用户接管与减少动态效果在同一视图。
  - iOS：`PhotoViewer.iOS/Native/iOSFilmstripView.cs:93`（`UICollectionView` 宿主）、`:97`/`:133`（集合与布局）、`:295`（350 ms 定位）。
  - Windows：`PhotoViewer.Windows/Native/WindowsFilmstripView.cs:21`（`Grid` 宿主）、`:37`/`:68`（`ItemsRepeater` + `StackLayout`）、`:24`/`:413`（350 ms 与三次缓出）。
  - Android：`PhotoViewer.Android/Native/AndroidFilmstripView.cs:20`（`RecyclerView`）、`:231`/`:240`（定位与缓动）。
- **FILM-04**：方向与滚轮。行/列方向由各宿主切换；普通纵向滚轮在横向列表上滚列表，见各平台滚轮入口（macOS `PhotoViewer.Mac/Native/MacMainWindow.cs` 的 `ScrollWheel`、iOS 的 `UIScrollView` 委托、Windows `PhotoViewer.Windows/Native/WindowsMainWindow.cs`、Android `PhotoViewer.Android/Native/AndroidNativeRootView.cs`）。
  - **FILM-04@mac / @ios**：重新显示胶片栏、切换列表方向时显式定位当前项（已登记 10-18）。
- **FILM-05**：卡片内容与命中。
  - macOS：`PhotoViewer.Mac/Native/MacThumbnailItem.cs:12`（`NSCollectionViewItem`）、`:64-99`（几何、星级、徽标）。
  - iOS：`PhotoViewer.iOS/Native/iOSThumbnailCard.cs:20`（几何）、`:101-134`（文件名 / 第二行 / 星级）。
  - Windows：`PhotoViewer.Windows/Native/WindowsThumbnailCard.cs:164`（文件名）、`:183`（相似度条）、`PhotoViewer.Windows/Native/WindowsLayout.cs:40-130`（转接共享 `FilmstripGeometry` 与 `film.*` / `color.*` 令牌）。
  - Android：`PhotoViewer.Android/Native/AndroidThumbnailCard.cs:139`（缩略图圆角）、`:171`（第二行 y）、`:288-307`（卡片菜单）。
  - **FILM-05@ios / @android**：卡片星级 14 点不放大，触控评分由卡片上下文菜单补足（已登记 10-18）。
- **FILM-06**：评分与筛选连续性由共享 `PhotoViewer/ViewModels/Main/File/ThumbnailListViewModel.cs` 与各端评分写回路径承担；写回失败一律走 [state](#state) 的 4 s 非模态提示条（macOS `PhotoViewer.Mac/Native/MacMainWindow.cs:581`、iOS `PhotoViewer.iOS/Native/iOSNativeRootViewController.cs:686`）。
- **追溯**：旧壳定位/动画的冻结几何与截图见 `Design/baseline/avalonia/`（旧视图源码只存在于 tag `avalonia-final` 之前的历史）；现行触发与定位逻辑在共享 `PhotoViewer/ViewModels/Main/File/ThumbnailListViewModel.cs`、`PhotoViewer/ViewModels/Main/ControlViewModel.cs`、`PhotoViewer/ViewModels/Main/FolderViewModel.cs`；iPad 侧在 `PhotoViewer.iOS/Native/iOSNativeRootViewController.cs`、`PhotoViewer.iOS/Native/iOSPhotoView.cs`、`PhotoViewer.iOS/Native/iOSFilmstripView.cs`。

## shell

- **SHELL-01**：分区与显隐。
  - macOS：`PhotoViewer.Mac/Native/MacMainWindow.cs:17`（窗口）、共享 `PhotoViewer.Shared/MainScreenLayout.cs:142-166`（分区）。
  - iOS：`PhotoViewer.iOS/Native/iOSNativeRootViewController.cs:27`、`PhotoViewer.Shared/MainScreenLayout.cs` + 真实安全区。
  - Windows：`PhotoViewer.Windows/Native/WindowsMainWindow.cs:574-628`（XAML Grid 五分区；栏宽与胶片带高经 `WindowsLayout` 取共享 `MainScreenLayout` / `FilmBandHeight`）。
  - Android：`PhotoViewer.Android/Native/AndroidNativeRootView.cs` + `AndroidMainScreenLayout`。
  - **SHELL-01@mac**：列布局筛选条前导 34 让交通灯（已登记 10-18，见 FILTER-07）。
- **SHELL-02**：隐藏式标题栏与拖动区。
  - macOS：`PhotoViewer.Mac/Native/MacMainWindow.cs:111-115`（`FullSizeContentView`）、`:173-191`（顶部监视器）、`:223-246`（双击）、`:500-508`（拖动区）。
  - Windows：`PhotoViewer.Windows/Native/WindowsTitleBar.cs:11-18`（自绘按钮与热区）、`:35-71`（caption 区域）、`PhotoViewer.Windows/Native/WindowsMainWindow.cs:390+`（显示/隐藏）；拖动与双击委托系统 caption（`NonClientRegionKind.Caption`）。
  - iOS：根控制器隐藏状态栏（`PrefersStatusBarHidden`），窗口级入口由主图长按菜单承担。
  - Android：`Theme.PhotoViewer.Native` 固定深色、edge-to-edge + insets，无标题栏双击语义。
  - **SHELL-02@ios / @android**：无窗口装饰与标题栏双击语义，属平台差异（已登记 10-18）。
- **SHELL-03**：全屏与尺寸。macOS `PhotoViewer.Mac/Native/MacMainWindow.cs:189-190`/`:208-219`，Windows `OverlappedPresenter` 四态，iOS/Android 无应用内全屏入口。
  - **SHELL-03@ios / @android**：不提供应用内全屏入口（已登记 10-18）。
- **SHELL-04**：持久化。四端栏位显隐都写平台偏好存储（键名 `PhotoViewer.Native.ShowFilm` / `ShowControls`），分析栏为会话态；窗口帧 macOS 用 `FrameAutosaveName`，iOS 用 `RestoreWindowPreferences`。
  - **SHELL-04@mac / @ios**：偏好存储承载栏位显隐与窗口帧（已登记 10-18）；Windows 走 `PhotoViewer.Windows/Native/WindowsNativePreferences.cs:13`、Android 走 `PhotoViewer.Android/Native/AndroidShellPreferences.cs:14`，键名与 macOS/iOS 相同。
- **SHELL-05**：跨屏与显示变化。macOS `DidChangeBackingProperties`；iPad 旋转/分屏重建滚动基准；Windows `XamlRoot`/DPI 变化；Android `OnSizeChanged`/`SurfaceChanged`。
- **SHELL-06**：窗口级无障碍。macOS 系统交通灯；Windows 自绘按钮 `PhotoViewer.Windows/Native/WindowsTitleBar.cs:157` 起补 `AutomationProperties.Name`；触屏由长按菜单承担。

## filter

- **FILTER-01**：三个下拉 + 计数 + toggle。
  - macOS：`PhotoViewer.Mac/Native/MacMainWindow.cs:150-160`（`NSPopUpButton`×3 + 自绘 toggle）、`:260+`（`NSMenu.PopUpMenu` 锚点）。
  - iOS：`PhotoViewer.iOS/Native/iOSFilterBar.cs:16`（`UIView`）、`:43-79`（`UIButton` + `UIMenu`）、`:82-88`（toggle）。
  - Windows：`PhotoViewer.Windows/Native/WindowsFilterBar.cs:19`（`Grid`）、`:34-67`（`ComboBox`×3）、`:126-133`（`ToggleButton`）。
  - Android：`PhotoViewer.Android/Native/AndroidFilterBar.cs:20`（自绘 `View`）、`:148-255`（自绘 `PopupWindow`）。
  - **FILTER-01@android**：<600 宽手机 toggle 76、下拉 84，计数用紧凑文案（已登记 10-18）。
- **FILTER-02、FILTER-03、FILTER-05、FILTER-10**：共享 `PhotoViewer/ViewModels/Main/File/ThumbnailListViewModel.cs:270-339`（筛选、排序、当前项映射）。
- **FILTER-04**：计数文案 macOS `PhotoViewer.Mac/Native/MacMainWindow.cs:665`、iOS `PhotoViewer.iOS/Native/iOSFilterBar.cs:71`、Windows `PhotoViewer.Windows/Native/WindowsFilterBar.cs:102`、Android `PhotoViewer.Android/Native/AndroidFilterBar.cs:89-93`。
- **FILTER-06、FILTER-09**：开关状态 `PhotoViewer/Core/Settings/SettingsModel.cs:28`；可见性 `PhotoViewer/ViewModels/Main/File/SimilarityPanelViewModel.cs:50`。
- **FILTER-07**：共享 `PhotoViewer.Shared/MainScreenLayout.cs:29-39`、`:66-69`。
  - **FILTER-07@win**：筛选条自带 `StackPanel` + 外边距 10，不消费共享 `FilterBarFrames`；几何值与令牌一致（已登记 10-18）。
  - **FILTER-07@ios / @android**：行布局 `alignLeading`，从安全区前导边 + 10 起排（已登记 10-18）。
- **FILTER-08**：菜单交互。macOS/iOS 用系统菜单；Windows 原生 `ComboBox`；Android 自绘菜单（行高约 40）。
  - **FILTER-08@ios / @android**：触控下拉可点区 90×32，低于 44 点建议（已登记 10-18）。

## image

- **IMAGE-01、IMAGE-03、IMAGE-05、IMAGE-08**：共享视口数学 `PhotoViewer.Shared/PhotoViewport.cs:7`；加载会话 `PhotoViewer/Core/Image/PhotoLoadSession.cs`；代理 overscan 由各壳下发给 `BitmapLoader`（代理目标长边 = 渲染表面物理像素 × `image.proxy.overscan`，见 `PhotoViewer/Core/Image/BitmapLoader.cs:342`）。
  - macOS：`PhotoViewer.Mac/Native/MetalPhotoView.cs` + `PhotoViewer.Apple/MetalPhotoRenderer.cs`。
  - iOS：`PhotoViewer.iOS/Native/iOSPhotoView.cs`（`UIScrollView` 为交互真源）。
  - Windows：`PhotoViewer.Windows/Native/WindowsPhotoView.cs`（`SwapChainPanel` + D3D11）。
  - Android：`PhotoViewer.Android/Native/AndroidPhotoView.cs`（`SurfaceView` + EGL14/GLES）。
  - **IMAGE-01@android**：空首屏副文偏移取 24（行高 24 + 内边距 6），已登记 10-18。
- **IMAGE-02**：`PhotoViewport.ToggleFit` / `ApplyImage`。
  - **IMAGE-04@ios / @android**：触屏最小缩放为适应态，小图/RAW 预览夹回适应（已登记 10-18）。
- **IMAGE-04**：缩放范围 `PhotoViewer.Shared/PhotoViewport.cs:250`/`:283`；预设与比例缩放入口 macOS `PhotoViewer.Mac/Native/MacMainWindow.cs:553`、iOS `PhotoViewer.iOS/Native/iOSNativeRootViewController.cs:605`、Windows `PhotoViewer.Windows/Native/WindowsMainWindow.cs:273-276`、Android `PhotoViewer.Android/Native/AndroidNativeRootView.cs:623-624`。
  - **IMAGE-04@mac**：导航图只展示，不接受点按（已登记 10-18）。
- **IMAGE-06**：导航图。macOS `PhotoViewer.Mac/Native/MacMainWindow.cs:717-738`；iOS `PhotoViewer.iOS/Native/iOSNativeRootViewController.cs:531-581`；Windows `PhotoViewer.Windows/Native/WindowsNavigator.cs:23`；Android `PhotoViewer.Android/Native/AndroidNavigationOverlay.cs:15`。
  - **IMAGE-06@ios / @android**：导航图点按/拖动定位（已登记 10-18）；**IMAGE-06@win**：不吃指针（已登记 10-18）。
  - Android 导航缩略图取当前展示产物（代理/Full/增强），按显示朝向抽样，不再二次解码原文件（`PhotoViewer.Android/Native/AndroidNavigationDecoder.cs:12` 的 `CreateFromDisplayAsync`）。
- **IMAGE-07**：增强状态 `PhotoViewer/Core/Image/PhotoLoadSession.cs`；药丸 `image.busy.*`、门槛 `image.busy.delay`（500ms）四端同一：macOS `PhotoViewer.Mac/Native/MacMainWindow.cs:749`、iOS `PhotoViewer.iOS/Native/iOSNativeRootViewController.cs` 的 `EnhanceBusyLabel`、Windows `PhotoViewer.Windows/Native/WindowsMainWindow.cs` 的 `_busyPill`、Android `PhotoViewer.Android/Native/AndroidNativeRootView.cs` 的药丸标签。
- **IMAGE-09**：定位入口 macOS `PhotoViewer.Mac/Native/MacMainWindow.cs:774`、iOS `PhotoViewer.iOS/Native/iOSNativeRootViewController.cs:979`、Windows `PhotoViewer.Windows/Native/WindowsPhotoView.cs:207`、Android `PhotoViewer.Android/Native/AndroidNativeRootView.cs:137`。
- **IMAGE-10**：空态/失败文案 macOS `PhotoViewer.Mac/Native/MacMainWindow.cs:135`/`:700-703`、iOS `PhotoViewer.iOS/Native/iOSNativeRootViewController.cs:1025-1035`、Windows `PhotoViewer.Windows/Native/WindowsMainWindow.cs:858`、Android `PhotoViewer.Android/Native/AndroidNativeRootView.cs:865-885`。
- **IMAGE-11**：缩放/平移/适应/跟随只存会话；导航图开关与缩放预设走设置（`PhotoViewer/ViewModels/Settings/SettingsViewModel.ImagePreview.cs:10-30`），增强开关不持久化（`PhotoViewer/Core/Image/PhotoLoadSession.cs`）。

## analysis

- **ANALYSIS-01、ANALYSIS-05**：编排 `PhotoViewer/Core/AI/AnalysisPresenter.cs`；几何 `PhotoViewer.Shared/AnalysisPanelGeometry.cs:11-22`。
  - macOS：`PhotoViewer.Mac/Native/MacAnalysisPanel.cs:20`、`MacDetailPreview`、`MacDiagnosticTile`。
  - iOS：`PhotoViewer.iOS/Native/iOSAnalysisPanel.cs:22`、`iOSDiagnosticTile`。
  - Windows：`PhotoViewer.Windows/Native/WindowsAnalysisPanel.cs:25`（`Grid` + `IAnalysisHost`）、`WindowsAnalysisTilePanel`。
  - Android：`PhotoViewer.Android/Native/AndroidAnalysisPanel.cs:67`（自绘 `View`）。
  - **ANALYSIS-05@ios / @android**：分析栏参与真实安全区布局（已登记 10-18）。
- **ANALYSIS-02**：瓦片外观。圆角只由外框一层裁切产生。
  - **ANALYSIS-02@windows**：内层圆角 0，单层裁切（已登记 10-18）。
  - **ANALYSIS-02@android**：抖动拖影矢量线为简化绘制（已登记 10-18）。
- **ANALYSIS-03**：等边长由 `AnalysisPanelGeometry` 或 Windows `WindowsAnalysisTilePanel` 的 Measure/Arrange 给出。
  - **ANALYSIS-03@android**：手机纵向按共享公式排 6–7 张，约 58dp；不为手机另设布局（已登记 10-18）。
- **ANALYSIS-04**：绿框联动。macOS/iOS/Windows hover 或点按；Android 点按定格。
  - **ANALYSIS-04@ios / @android**：点按定格、再点或点主图空白解除（已登记 10-18）。
- **ANALYSIS-06**：细节裁剪各端 `DisplayCrop`；代理期留空。
- **ANALYSIS-07**：直方图由当前上屏图现算，入口在同一瓦片宿主。
- **ANALYSIS-08**：准星与 cosine 参考点。四端点按内容矩形落准星；letterbox/角标等同面板空白（macOS 在 `PhotoViewer.Mac/Native/MacDiagnosticTile.cs` 与 `MacAnalysisPanel.cs` 的空白命中里清准星、保留 cosine 参考点）。
- **ANALYSIS-09**：推理提供器 macOS=CoreML、Windows=DirectML、Android=NNAPI、iOS=CPU；编排共享 `AnalysisPresenter`。
- **ANALYSIS-10、ANALYSIS-11**：状态与无障碍由各端瓦片宿主承担，未专项实现。

## toolbar

- **TOOLBAR-01、TOOLBAR-02**：共享 `PhotoViewer.Shared/ControlBarLayout.cs:21-44`。
  - macOS：`PhotoViewer.Mac/Native/MacControlBar.cs:11`（`NSView`），置于 `NSScrollView`。
  - iOS：`PhotoViewer.iOS/Native/iOSControlBar.cs:15`（`UIView`），置于 `UIScrollView`，`Arrange` 后写 `ContentSize` 并夹取偏移（`iOSNativeRootViewController.cs:538`）。
  - Windows：`PhotoViewer.Windows/Native/WindowsControlBar.cs:18`（`Grid` + `WindowsWrapPanel`），控制栏自带 `ScrollViewer`（`:126`）保存并夹取偏移。
  - Android：`PhotoViewer.Android/Native/AndroidControlBar.cs:17`（单个自绘 `View`），置于 `AndroidControlBarScrollHost`（`AndroidNativeRootView.cs:134`）滚动，无障碍由 `ExploreByTouchHelper` 虚拟节点承载（`AndroidControlBar.cs:446`）。
- **TOOLBAR-03**：评分组。禁用统一用系统禁用态（降强调、不隐藏）；评分快捷命令统一画数字（0=叉）。
  - **TOOLBAR-03@ios**：评分按钮 30×30 低于 44 点，接受为平台差异，不做长按补偿（已登记 10-18）。
- **TOOLBAR-04**：命令组。桌面 tooltip = 命令名 + 实际主/次绑定；移动端不另造长按浮层，读屏名称含命令名与绑定。
- **TOOLBAR-05**：EXIF 摘要。桌面 tooltip `标签 · 值`；触屏完整值由 TOOLBAR-06 的详情页承担，读屏名称含标签与值。
- **TOOLBAR-06**：EXIF 整组命中打开工具。macOS `PhotoViewer.Mac/Native/MacControlBar.cs:44`/`:217-221`；iOS `PhotoViewer.iOS/Native/iOSControlBar.cs:67-71`；Windows `PhotoViewer.Windows/Native/WindowsControlBar.cs:62-66`；Android `PhotoViewer.Android/Native/AndroidControlBar.cs:290-292`（已接线，待实机核对）。
- **TOOLBAR-07**：重建。**TOOLBAR-07@ios**：先 `RemoveFromSuperview` 再 `Dispose`（已登记 10-18）；**@win** 按影响范围合并 dispatcher turn；**@android** `MarkDirty` + `OnDraw` 补齐 `Arrange`。
- **TOOLBAR-08**：溢出。四端都必须有滚动宿主，溢出时可滚、保存并夹取滚动偏移（macOS `NSScrollView`、iOS `UIScrollView`、Windows 控制栏自带 `ScrollViewer`、Android `AndroidControlBarScrollHost`）。
- **TOOLBAR-09**：无照片/空控制栏。三组为空时只保留四周 `toolbar.padding`；各端由 `ControlBarLayout` 或 `WindowsLayout` 计算文档长度。
- **TOOLBAR-10**：视觉与反馈。底色/描边取 `color.toolbar.*`，圆角 `toolbar.cornerRadius`；星形与清零叉字号分别取 `toolbar.rating.glyph.fontSize` / `toolbar.rating.clearGlyph.fontSize`。
- **TOOLBAR-11**：无障碍。每个控件暴露名称、禁用态与焦点顺序；Android 由 `ExploreByTouchHelper` 虚拟节点给出（`PhotoViewer.Android/Native/AndroidControlBar.cs:446`）。
- **TOOLBAR-12**：开关型命令的按钮态。四端当前都不在按钮上表达按下态/强调色，状态只在菜单勾选与面板可见性上。

## similarity

<a id="sim"></a>

- **SIM-01**：入口与开合。共享 `PhotoViewer/ViewModels/Main/File/SimilarityPanelViewModel.cs:50`；各端面板类 `PhotoViewer.Mac/Native/MacSimilarityPanel.cs:22`、`PhotoViewer.iOS/Native/iOSSimilarityPanel.cs:23`、`PhotoViewer.Windows/Native/WindowsSimilarityPanel.cs:21`、`PhotoViewer.Android/Native/AndroidSimilarityPanel.cs:22`。
- **SIM-02、SIM-03**：空态与动作。macOS 空态常量 `PhotoViewer.Mac/Native/MacSimilarityPanel.cs:33`/`:35`；动作按钮与进度各端自绘。
  - **SIM-03@android**：Material 文本动作（64/整宽）与文本进度，信息等价，点按区不小于 44dp（已登记 10-18）。
- **SIM-04、SIM-05**：卡片复用胶片栏卡片；点选走 `MainViewModel.SetCurrentImageKeepAnchor`，双高亮由各端绑定 `IsCurrent` / `IsCurrentImage`。
- **SIM-06、SIM-08**：计算与结果。阈值/数量 `PhotoViewer/ViewModels/Settings/SettingsViewModel.AI.cs:17-26`；服务 `PhotoViewer/Core/AI/SimilarityService.cs`。
- **SIM-09**：面板内改星走与胶片栏同一写回路径，锚点不变；筛选落出由共享 `PhotoViewer/ViewModels/Main/File/ThumbnailListViewModel.cs` 处理。
- **SIM-07**：几何。共享 `PhotoViewer.Shared/SimilarityPanelGeometry.cs:12-46`；四端一律用 `RowWidth` 夹到可用宽一半。Windows 面板的 `SetAvailableWidth` 按 `RowWidth(hasItems, itemCount, availableWidth)` 落宽，列宽 110、`MaxWidth` 同步夹到可用宽一半。
- **SIM-10、SIM-11**：错误与无障碍。列表态与空态的动作区都必须承载错误标签，列表态由共享 `ShowActionAreaError` 驱动；macOS `MacSimilarityPanel`、iOS `iOSSimilarityPanel.cs:126`、Windows `WindowsSimilarityPanel` 的停靠错误标签、Android `AndroidSimilarityPanel.cs:96` 各自绑定。

## menu

- **MENU-01、MENU-02**：主图菜单。
  - macOS：`PhotoViewer.Mac/Native/MacMainWindow.cs:885-899`（`NSMenu`）、`:612`/`:883`（勾选）。
  - iOS：`PhotoViewer.iOS/Native/iOSContextMenus.cs:48-68`（`UIMenu`）。
  - Windows：`PhotoViewer.Windows/Native/WindowsMainWindow.cs:342-384`（`MenuFlyout`）。
  - Android：`PhotoViewer.Android/Native/AndroidNativeRootView.cs:478-583`（自绘 `PopupWindow`）。
- **MENU-03、MENU-04**：弹出与收回。macOS/iOS/Windows 用系统菜单；Android 自绘并夹取到屏幕内（`PhotoViewer.Android/Native/AndroidNativeRootView.cs:566-568`）。
  - **MENU-03@android**：自绘弹层，定位与夹取自实现（已登记 10-18）。
  - **MENU-04@ios**：卡片菜单保留系统抬升，不移动列表布局（已登记 10-18）。
- **MENU-05**：卡片菜单。iOS `PhotoViewer.iOS/Native/iOSContextMenus.cs:77-93`；Android `PhotoViewer.Android/Native/AndroidThumbnailCard.cs:288-307`；桌面无卡片菜单，用 tooltip + 星级按钮。
  - **MENU-05@android**：标题与时间可拆成两行原生外观；标题行不可点击，时间补秒与时区（已登记 10-18）。
- **MENU-07**：不可用项与并发。各端菜单项集合不随照片/列表内容增删；无照片时「增强」置灰、工具打开空态（macOS `MacMainWindow.cs:933`、iOS `iOSContextMenus.cs:40`、Windows `WindowsMainWindow.cs:418`、Android `AndroidNativeRootView.cs:607`）。

## input

- **INPUT-01、INPUT-02**：手势到命令。macOS `PhotoViewer.Mac/Native/MacInput.cs:11`；iOS `PhotoViewer.Apple/AppleInputMapping.cs:11`；Windows `PhotoViewer.Windows/Native/WindowsInput.cs:14`；Android `PhotoViewer.Android/Native/AndroidInputMapping.cs:11`；共享命令表 `PhotoViewer.Shared/InputCommandMap.cs`。
- **INPUT-03、INPUT-04、INPUT-06**：主图手势。macOS `MetalPhotoView.ScrollWheel`/`MagnifyWithEvent`；iOS `UIScrollView` + 单指轻扫；Android `ScaleGestureDetector` + `GestureDetector`；Windows `WindowsPhotoView` 指针手势。滚轮精度分流四端统一：精确滚动走平移、离散一格一命令（Windows `WindowsInput.cs:125` 的 `FromWheel`、Android `AndroidPhotoView.cs:422` 的 `OnGenericMotionEvent`）。
- **INPUT-05**：按键重复。只对可重复命令重复（`InputCommandMap` 的 Repeatable 标记，四端重复入口统一按此设闸）；macOS/Windows/Android 跟系统重复节奏，iOS 用固定节奏。
  - **INPUT-05@ios**：固定 350 ms / 60 ms 节奏（`PhotoViewer.iOS/Native/iOSNativeRootViewController.cs:30`/`:32`），属平台差异（已登记 10-18）。
- **INPUT-07、INPUT-08**：菜单与录制。macOS 真实 `NSEvent` 录制；Windows `WindowsHotkeyRecorder`。
  - **INPUT-08@ios / @android**：无录制入口（iOS 只读、Android 未实现）（已登记 10-18）。
- **INPUT-09**：预设/比例缩放入口。macOS `PhotoViewer.Mac/Native/MacMainWindow.cs:553`；iOS `PhotoViewer.iOS/Native/iOSNativeRootViewController.cs:605`；Windows `PhotoViewer.Windows/Native/WindowsMainWindow.cs:273-276`；Android `PhotoViewer.Android/Native/AndroidNativeRootView.cs:623-624`。
- **INPUT-10**：无照片时按钮禁用不隐藏、打开类命令始终可用；入口由各端控制栏与空态命中层实现。
- **INPUT-11**：Android `OnBackPressedCallback` 先关设置/工具 sheet 与菜单。
- **INPUT-12**：触屏等价入口。卡片评分由卡片菜单补足（iOS `PhotoViewer.iOS/Native/iOSContextMenus.cs`、Android `PhotoViewer.Android/Native/AndroidThumbnailCard.cs:288-307`）；焦点顺序无专项实现。

## open

- **OPEN-01、OPEN-06、OPEN-07**：入口与外部打开。
  - macOS：`PhotoViewer.Mac/Native/MacMainWindow.cs:866`/`:889-890`（`NSOpenPanel`）、`:951-959`（拖入）、`PhotoViewer.Mac/Native/MacNativeApplication.cs:95-107`（外部打开）。
  - iOS：`PhotoViewer.iOS/Native/iOSNativeRootViewController.cs:779-795`（`UIDocumentPicker`）、`:1084-1106`（`UIDropInteraction`）。
  - Windows：`PhotoViewer.Windows/Native/WindowsMainWindow.cs:345-355`（选择器）、`:194-208`（拖入）。
  - Android：`PhotoViewer.Android/Native/AndroidNativeRootView.cs:164`（SAF）、`PhotoViewer.Android/Native/AndroidNativeShell.cs:157-169`（`ACTION_VIEW/SEND`）。
  - **OPEN-01@ios / @android**：只有文件夹选择器（已登记 10-18）。
  - **OPEN-07@android**：未接 `OnDragListener`，手机无常见拖入来源，不建拖入入口（已登记 10-18）。
- **OPEN-02、OPEN-04、OPEN-05、OPEN-08**：共享 `PhotoViewer/ViewModels/Main/FolderViewModel.cs:263-349` 与 `PhotoViewer/ViewModels/Main/File/ThumbnailListViewModel.cs:203-261`。
  - 显式打开前调用 `FitToView()` 回到适应态（macOS/iOS 已实现）；Windows/Android 未重置视口为缺口（见 status.md）。
- **OPEN-03、OPEN-09**：单图模式与派生状态由共享 VM 承担；平台父目录可得性见各端安全作用域 / SAF 授权。

## state

- **STATE-01、STATE-02**：提示与空态。macOS `PhotoViewer.Mac/Native/MacMainWindow.cs:135`/`:744`；iOS `PhotoViewer.iOS/Native/iOSNativeRootViewController.cs:1101`；Windows `PhotoViewer.Windows/Native/WindowsMainWindow.cs:858`；Android `PhotoViewer.Android/Native/AndroidNativeRootView.cs:933`。加载提示延迟统一取 `status.loading.delay`（300ms），由共享加载会话的 `IsLoadingVisible` 提供，macOS/iOS/Android 已绑定。
- **STATE-03**：操作结果提示。四端统一为 4 s 非模态提示条：macOS `PhotoViewer.Mac/Native/MacStatusToast.cs`（`MacMainWindow.cs:581` 评分失败、`:755` `FullError`、`:212` 订阅 `Session.Notice`）、iOS `PhotoViewer.iOS/Native/iOSStatusToast.cs`（`iOSNativeRootViewController.cs:686`/`:1076`）、Windows `PhotoViewer.Windows/Native/WindowsMainWindow.cs:798-812`、Android `PhotoViewer.Android/Native/AndroidNativeRootView.cs:682-693`。
- **STATE-04**：模型/推理/数据库不可用由共享 `AnalysisPresenter`、`SimilarityPanelViewModel` 与设置清库路径承担。

## settings

- **SETTINGS-01、SETTINGS-02**：工作副本 `PhotoViewer/Core/Settings/NativeSettingsSession.cs`；分类与行由各端设置宿主实现。
  - macOS：`PhotoViewer.Mac/Native/MacNativeSettingsPresenter.cs:15`、SwiftUI `PhotoViewer.Mac/Native/SwiftSettings/MacSettingsUI.swift`。
  - iOS：`PhotoViewer.iOS/Core/iOSNativeSettingsPresenter.cs:17`（`PageSheet`）。
  - Windows：`PhotoViewer.Windows/Native/WindowsSettingsWindow.cs:15`（`NavigationView` + `SettingsCard`）。
  - Android：`PhotoViewer.Android/Native/AndroidNativeSettingsPresenter.cs:25`（`BottomSheetDialog`）。
  - **SETTINGS-01@ios / @android**：无录制，行只读显示主/次绑定与冲突（已登记 10-18）。
- **SETTINGS-03**：平台外观。macOS `NavigationSplitView` + `Form(.grouped)`；Windows Mica + `TitleBar` + `NavigationView`；iOS 半屏 sheet；Android Material 3 `BottomSheetDialog`。四端设置页固定深色（iOS 设 `OverrideUserInterfaceStyle = Dark`）。
  - **SETTINGS-03@mac / @win / @ios / @android**：各自原生设置外观，明暗统一固定深色（已登记 10-18）。
- **SETTINGS-04**：入口与关闭。四端入口都在主图菜单末项；关闭统一走 `NativeSettingsSession.Apply()`（幂等）。
- **SETTINGS-05**：编辑隔离。工作副本以 `applyRuntimeSettings: false` 创建，缓存/解码/键盘映射副作用都在提交后执行（`PhotoViewer/ViewModels/Settings/SettingsViewModel.cs:40`）。
- **SETTINGS-06、SETTINGS-07、SETTINGS-08、SETTINGS-09、SETTINGS-10、SETTINGS-11、SETTINGS-12、SETTINGS-14、SETTINGS-15**：控件与持久化。
  - **SETTINGS-12@android**：数值用 `+/-` 步进（与 iOS `UIStepper` 同类）；步进控件不改变取值范围（已登记 10-18）。
  - **SETTINGS-10@ios / @android**：AI 页无「清空特征数据库」；移动端长期不提供，配置保留、入口不显示（已登记 10-18）。
  - **SETTINGS-11@ios / @android**：无录制控件（已登记 10-18）。
  - **SETTINGS-14@ios / @android**：JSON 分别存 `NSUserDefaults` 与 `SharedPreferences`，字段不变（已登记 10-18）。
  - 排序四端统一为拖拽（Android 用 `ItemTouchHelper` + 行尾把手，把手轻点给上移/下移菜单，`PhotoViewer.Android/Native/AndroidSettingsReorderList.cs`）。
- **SETTINGS-12**：数值校验。范围夹取在各设置视图模型（如 `PhotoViewer/ViewModels/Settings/SettingsViewModel.BitmapCache.cs`、`SettingsViewModel.AI.cs`），非法输入不写入。
- **SETTINGS-13**：键盘与无障碍。桌面由系统控件提供 Tab/方向键语义；触屏由各端设置列表承担，四端均无专项断言。

## tools

- **TOOLS-01、TOOLS-02**：外壳与入口。
  - macOS：`PhotoViewer.Mac/Native/MacToolsWindow.cs:16`（独立 `NSWindow`，列表 + 子页）。
  - iOS：`PhotoViewer.iOS/Native/iOSToolsPresenter.cs`（`PageSheet` + `UINavigationController`）。
  - Windows：`PhotoViewer.Windows/Native/WindowsToolsWindow.cs:16`（`NavigationView` 三分类，无首页）。
  - Android：`PhotoViewer.Android/Native/AndroidToolsPresenter.cs:24`（`BottomSheetDialog`，root / exif / dino）。
  - **TOOLS-01@win**：工具窗口 960×720、最小 800×560，因常驻侧栏 240 + EXIF 表格 720；普通窗口（进任务栏/Alt-Tab、可最大化）（已登记 10-18）。
  - **TOOLS-01@mac / @ios / @android**：不建「照片数据统计」入口（已登记 10-18）。
- **TOOLS-02@win**：无首页/返回，常驻三分类；分类顺序与其他端一致为 EXIF → DINO → 照片数据统计（已登记 10-18）。
- **TOOLS-03、TOOLS-04**：EXIF 详情。macOS 自绘 `NSView` + `ExifTableGeometry`；iOS `UITableView`；Windows 7 列 `Grid`；Android 线性行 4 个内容列。
  - **TOOLS-03@android**：4 个内容列承载 7 列几何，手机与平板同口径（已登记 10-18）。
- **TOOLS-05**：DINO 五瓦片。共享 `PhotoViewer/Core/AI/DinoDiagnosticPresenter.cs`；五瓦片 3+2，第六格留空，刚体拟合文本以整宽块置于网格下方（四端统一）。
- **TOOLS-06**：计算时机。共享 presenter 先读库；离开页面或关闭窗口即取消在途任务（`ToolsViewModel.Deactivate()`，macOS `MacToolsWindow`、iOS `iOSToolsPresenter.cs:65`、Windows `WindowsToolsWindow.cs:328`、Android `AndroidToolsPresenter.cs:171`）。
- **TOOLS-07**：照片数据统计，仅 Windows（`PhotoViewer/ViewModels/Tools/ToolsViewModel.cs:35`）。
  - **TOOLS-07@win**：平台专属工具，其他端不建入口（已登记 10-18）。

## scope

- **SCOPE-01**：可见性由共享 VM 布尔属性决定；照片数据统计 `ToolsViewModel.IsPhotoStatsAvailable => OperatingSystem.IsWindows()`（`PhotoViewer/ViewModels/Tools/ToolsViewModel.cs:35`）。
  - **SCOPE-01@mac / @ios / @android**：不建照片数据统计入口（已登记 10-18，同 TOOLS-07@win）。
- **SCOPE-02**：计算门控。相似 `PhotoViewer/ViewModels/Main/File/SimilarityPanelViewModel.cs:50`；分析 `PhotoViewer/Core/AI/AnalysisPresenter.cs:136-142`；回退 `FallbackDelayMs = 500`（`PhotoViewer/Core/AI/AnalysisPresenter.cs:34`）。
- **SCOPE-03**：入库与现算。批量提取 `PhotoViewer/Core/AI/FolderFeatureIndexer.cs`；即时缓存 `AnalysisResultCache`。
- **SCOPE-04**：能力缺失。**SCOPE-04@ios**：ImageIO 对 Sony ARW 只给内嵌预览、`IsProxy=false`，CoreML EP 不可用回退 CPU（已登记 10-18；`CIRAWFilter` 全分辨率解码暂缓，并入性能专项）。**SCOPE-04@android**：模拟器 P3 协商失败回退 sRGB（已登记 10-18）。
- **SCOPE-05**：文件写操作。评分写回 `XmpWriter.WriteRatingAsync`；无删除/重命名/移动/导出命令。
- **SCOPE-06**：平台专属。导航图点按/拖动定位（**SCOPE-06@ios / @android**，已登记 10-18）；Windows 照片统计；Android 设置数值步进见 SETTINGS-12@android。
