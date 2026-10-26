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
| 导航图 | 自绘 `NSView`（`NavigatorHostView`），白框可拖动定位 | 自绘 `UIView`，白框可拖动定位 | `WindowsNavigator`，白框可拖动定位 | `AndroidNavigationOverlay`，白框可拖动定位 |
| 分析栏 | `MacAnalysisPanel`（`NSView`） | `iOSAnalysisPanel`（`UIView`） | `WindowsAnalysisPanel`（`Grid` + `IAnalysisHost`） | `AndroidAnalysisPanel`（自绘 `View`） |
| 控制栏 | `MacControlBar`（`NSView`） | `iOSControlBar`（`UIView`） | `WindowsControlBar`（`Grid` + `WindowsWrapPanel`） | `AndroidControlBar`（单个自绘 `View`） |
| 相似面板 | `MacSimilarityPanel` + `NSCollectionView` | `iOSSimilarityPanel` + `UICollectionView` | `WindowsSimilarityPanel`（`Grid`） | `AndroidSimilarityPanel`（`LinearLayout` + `RecyclerView`） |
| 主图 / 卡片菜单 | `NSMenu` | `UIContextMenuInteraction` + `UIMenu` | `MenuFlyout` | 自绘 `PopupWindow` |
| 设置窗口 | SwiftUI `NavigationSplitView` + `Form(.grouped)` | UIKit 半屏 `PageSheet` | WinUI `NavigationView` + `SettingsCard` | Material 3 `BottomSheetDialog` |
| 工具窗口 | 设置同款 SwiftUI `NavigationSplitView` 外壳 + AppKit 详情 | 设置同款 `PageSheet` + `UINavigationController`（分组列表导航） | 独立 WinUI 窗口（常驻三分类） | 设置同款 `BottomSheetDialog`（分组列表导航） |
| 状态提示 | 自绘提示条 4 s | 自绘提示条 4 s | 自绘状态提示条，4 s | `TextView` 提示条，4 s |
| 输入 | `NSEvent` + `MacInput` | `UIPress` + `AppleInputMapping` | `WindowsInput`（虚拟键 / 滚轮） | `AndroidInputMapping`（`KeyEvent`） |
| 打开 | `NSOpenPanel` + 根视图拖入 | `UIDocumentPicker` + `UIDropInteraction` | `FileOpenPicker` / `FolderPicker` + `Drop` | SAF `ACTION_OPEN_DOCUMENT_TREE` + `ACTION_VIEW/SEND`（拖入见 OPEN-07@android） |
| EXIF 详情表 | 自绘 `NSView` + `ExifTableGeometry`（7 列） | `UITableView`（7 列几何） | `Grid`（7 列） | 线性行（4 个内容列承载 7 列几何，见 TOOLS-03@android） |

Web 参考壳（[PhotoViewer.Web](../PhotoViewer.Web/AGENTS.md)）只依据本目录实现，所有分区都是 DOM 组件（`PhotoViewer.Web/src/shell/*.js`），业务与几何在 `PhotoViewer.Web/src/core/`（`app.js` 主屏模型、`layout/` 几何）。它不进入四端验收，`status.md` 不设 Web 列；行为由 `?check=all` 页内检查覆盖。主图用 Canvas 2D 按方向码变换绘制，下拉与菜单自绘，选择器为应用内合成文件夹列表。

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
- **FILM-04@web**：浏览器胶片栏为原生 `overflow` 滚动 + 自绘悬浮滚动条，横向列表把鼠标纵向滚轮映射为横向滚动。
- **FILM-05**：卡片内容与命中。
  - macOS：`PhotoViewer.Mac/Native/MacThumbnailItem.cs:12`（`NSCollectionViewItem`）、`:64-99`（几何、星级、徽标）。
  - iOS：`PhotoViewer.iOS/Native/iOSThumbnailCard.cs:20`（几何）、`:101-134`（文件名 / 第二行 / 星级）。
  - Windows：`PhotoViewer.Windows/Native/WindowsThumbnailCard.cs:164`（文件名）、`:183`（相似度条）、`PhotoViewer.Windows/Native/WindowsLayout.cs:40-130`（转接共享 `FilmstripGeometry` 与 `film.*` / `color.*` 令牌）。
  - Android：`PhotoViewer.Android/Native/AndroidThumbnailCard.cs:139`（缩略图圆角）、`:171`（第二行 y）、`:288-307`（卡片菜单）。
  - **FILM-05@ios / @android**：卡片星级 14 点不放大，触控评分由卡片上下文菜单补足（已登记 10-18）。
- **FILM-06**：评分与筛选连续性由共享 `PhotoViewer/ViewModels/Main/File/ThumbnailListViewModel.cs`（评分回填、暂留与筛选刷新）与各端评分写回路径承担；写回作用于组的全部实际来源（RATING-01/03），来源冲突的指示与浮窗见 [rating](#rating)（RATING-04）；写回失败一律走 [state](#state) 的 4 s 非模态提示条（macOS `PhotoViewer.Mac/Native/MacStatusToast.cs`、iOS `PhotoViewer.iOS/Native/iOSStatusToast.cs`）。
- **追溯**：旧壳定位/动画的冻结几何与截图见 `Design/baseline/avalonia/`（旧视图源码只存在于 tag `avalonia-final` 之前的历史）；现行触发与定位逻辑在共享 `PhotoViewer/ViewModels/Main/File/ThumbnailListViewModel.cs`、`PhotoViewer/ViewModels/Main/ControlViewModel.cs`、`PhotoViewer/ViewModels/Main/FolderViewModel.cs`；iPad 侧在 `PhotoViewer.iOS/Native/iOSNativeRootViewController.cs`、`PhotoViewer.iOS/Native/iOSPhotoView.cs`、`PhotoViewer.iOS/Native/iOSFilmstripView.cs`。

## rating

- **RATING-01、RATING-03**：来源枚举与全来源事务在共享 `PhotoViewer/Core/Exif/RatingService.cs`（按 `PhotoGroup.Id` 串行、预检、固定顺序写、逆序补偿、写后重新枚举来源集合并逐来源回读与结果枚举）；来源级读取状态（缺失 / 已评分 / 已拒绝 / 无效 / 冲突 / 不可读）在 `PhotoViewer/Core/Exif/XmpRatingReader.cs`，组级冲突判定在 `PhotoViewer/Core/Image/PhotoGroupMap.cs` 的 `PhotoGroup.HasRatingConflict`，列表与筛选投影在 `PhotoViewer/ViewModels/Main/File/PhotoListProjection.cs` 的 `MatchesRating`。
- **RATING-02、RATING-06**：评分写入只有一条正常路径：`PhotoViewer/Core/Exif/ContainerRatingStore.cs` 负责预检（以 ReadWrite 打开现有文件、在包偏移定位读取，验证真实可定位写能力且不写不截断）、revision 与整包/区间复核、提交和写后回读，`NoChange` 提交同样复核 revision 与目标值；包内 `xap:Rating` 的结构定位与等长改写由 `PhotoViewer/Core/Exif/XmpRatingSpanLocator.cs` 完成（值替换或按结构插入，近端空白吸收；远端 padding 移位、受控追加、整文件替换不支持）。容器适配器只承担 canonical 包的只读定位：`JpegXmpContainerAdapter`、`HeifXmpContainerAdapter`、`TiffXmpContainerAdapter`、`WebpXmpContainerAdapter`、`XmpFileContainerAdapter`（`<stem>.xmp` 本体与已有 sidecar）。按 RATING-02 拒绝与其它段、项、extent 或 IFD 条目重叠或共享的写入区间；无 canonical XMP 包（含 JPEG 无 APP1）时不新建或插入容器结构，按 `StorageError.Unsupported` 失败；已有包但缺 `xap:Rating` 时只在包结构唯一且空白足够时于包内插入。PNG 写入不支持（含标准 `iTXt`，需要维护容器级 CRC 事务）。来源枚举由 `PhotoGroupMap.RatingSources` 汇总，`XmpRatingStoreFactory` 为每个实际来源选择适配器；没有创建 sidecar 的入口。
  - **RATING-06@mac / @win / @ios**：只做等长与 Rating 附近局部空白吸收的原位写；远端 padding 移位、受控追加与整文件替换不支持。
  - **RATING-06@android**：SAF 目标先核对可定位原位写能力；不可定位句柄、云 Provider 或需要远端移位/扩容的写入在首次修改前失败，不做原位假成功。
- **RATING-04**：冲突三角落在控制栏评分组与胶片栏卡片的 0 号槽位，三角只作指示、不写值；来源条目由共享 `PhotoViewer/ViewModels/Main/RatingSourcesViewModel.cs` 汇总组内全部来源，打开期间跟随刷新。浮窗的宽度、展开方向、条目结构与关闭条件以 [spec/rating.md](spec/rating.md) RATING-04 为准；本节只记录弹层宿主与字形，各端实现与验收状态见 [status.md](status.md#rating)。
  - **RATING-04@mac**：`NSPopover` 的原生毛玻璃与圆角是唯一外观，内容区不自绘背景、描边或圆角；来源标签底与条目间分隔线用标准 `NSBox`，文件名/来源/状态用 `NSTextField`，星按钮复用控制栏评分按钮的槽位、字形与悬停/按下反馈。条目几何、间距与字号取 `popup.ratingSources.*`；卡片入口默认向下、下方不足且上方可容纳时向上，锚点移动/隐藏/滚动/列表替换时关闭。关闭中的旧实例保存在 `_retired` 并强引用到窗口终结（AppKit 迟后读取 bounds 的真实安全限制，不视为已解决；边界与挂账归 Plans，入口说明见 [PhotoViewer.Mac/AGENTS.md](../PhotoViewer.Mac/AGENTS.md)）。
  - **RATING-04@mac / @ios**：警示三角取 SF Symbols `exclamationmark.triangle.fill`，语义同 `icon.warning`。
  - **RATING-04@win**：警示三角取 Segoe Fluent Icons Warning `E7BA`；**@android / @web** 直接用 `icon.warning`（U+FE33）。
- **RATING-05**：数字/以上筛选投影在 `PhotoViewer/ViewModels/Main/File/PhotoListProjection.cs` 的 `MatchesRating`，筛选项在 `PhotoViewer/ViewModels/Main/File/FilterBarViewModel.cs`；训练快照由独立仓 `Training/DatasetBuilder`（`FingerprintGrouper`）只读取实际选用的文件，sidecar 与伴侣不参与取 max，文件内部不是有效 1–5 时不落值。
- Web 参考壳没有真实文件写入；冲突指示与 13 项筛选由 `?check=all` 校验界面语义（合成数据）。

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
- **SHELL-06**：窗口级输入边界。macOS 系统交通灯；Windows 自绘按钮可点击、带悬停提示（`PhotoViewer.Windows/Native/WindowsTitleBar.cs:157`）；触屏由长按菜单承担。
- **SHELL-02@web**：浏览器标签页没有窗口装饰、隐藏标题栏、拖动区与标题栏双击；`?platform=mac` 配置档只用于与 macOS 几何比对。
- **SHELL-03@web**：浏览器视口即窗口，允许小于 `shell.window.min.*`，布局按实际尺寸计算（主图至少 1 点）；不提供应用内全屏入口。
- **SHELL-04@web**：栏位显隐与设置文档写 `localStorage`（键名同四端偏好存储）。

## filter

- **FILTER-01**：三个下拉 + 计数 + toggle。
  - macOS：`PhotoViewer.Mac/Native/MacMainWindow.cs:150-160`（`NSPopUpButton`×3 + 自绘 toggle）、`:260+`（`NSMenu.PopUpMenu` 锚点）。
  - iOS：`PhotoViewer.iOS/Native/iOSFilterBar.cs:16`（`UIView`）、`:43-79`（`UIButton` + `UIMenu`）、`:82-88`（toggle）。
  - Windows：`PhotoViewer.Windows/Native/WindowsFilterBar.cs:19`（`Grid`）、`:34-67`（`ComboBox`×3）、`:126-133`（`ToggleButton`）。
  - Android：`PhotoViewer.Android/Native/AndroidFilterBar.cs:20`（自绘 `View`）、`:148-255`（自绘 `PopupWindow`）。
  - **FILTER-01@android**：<600 宽手机 toggle 76、下拉 84，计数用紧凑文案（已登记 10-18）；窄于紧凑宽度所需时筛选条横向滚动，控件宽度不压缩。
- **FILTER-02、FILTER-03、FILTER-05、FILTER-10**：共享 `PhotoViewer/ViewModels/Main/File/ThumbnailListViewModel.cs`（筛选、排序、当前项映射）；筛选项定义在 `PhotoViewer/ViewModels/Main/File/FilterBarViewModel.cs`，星级投影在 `PhotoViewer/ViewModels/Main/File/PhotoListProjection.cs` 的 `MatchesRating`。13 项取值与已拒绝/冲突口径见 [rating](#rating)（RATING-01）与 [spec/filter.md](spec/filter.md)（FILTER-03）。
- **FILTER-04**：计数文案 macOS `PhotoViewer.Mac/Native/MacMainWindow.cs:665`、iOS `PhotoViewer.iOS/Native/iOSFilterBar.cs:71`、Windows `PhotoViewer.Windows/Native/WindowsFilterBar.cs:102`、Android `PhotoViewer.Android/Native/AndroidFilterBar.cs:89-93`。
- **FILTER-06、FILTER-09**：开关状态 `PhotoViewer/ViewModels/Settings/SettingsViewModel.cs` 的 `SimilarityPanelExpanded`（经 `IViewStateStore` 跨会话保留，不进入设置文档）；可见性 `PhotoViewer/ViewModels/Main/File/SimilarityPanelViewModel.cs:50`。
- **FILTER-07**：共享 `PhotoViewer.Shared/MainScreenLayout.cs:29-39`、`:66-69`。
  - **FILTER-07@win**：筛选条自带 `StackPanel` + 外边距 10，不消费共享 `FilterBarFrames`；几何值与令牌一致（已登记 10-18）。
  - **FILTER-07@android**：手机行布局 `alignLeading`，从安全区前导边 + 10 起排（已登记 10-18）；iPad 与四端一致，五项整组在安全内容区内居中，不设前导对齐差异。窄于紧凑宽度所需时筛选条横向滚动，控件宽度不压缩。
- **FILTER-08**：菜单交互。macOS/iOS 用系统菜单；Windows 原生 `ComboBox`；Android 自绘菜单（行高约 40）。
  - **FILTER-08@ios / @android**：触控下拉可点区 90×32，低于 44 点建议（已登记 10-18）。
- **FILTER-07@web / FILTER-08@web**：无交通灯，列布局首控件从 `filter.margin` 起排；浏览器无原生下拉弹层，三个下拉按 FILTER-08 自绘外观（旧壳 Avalonia Fluent 深色），菜单与 MENU-03 共用同一自绘弹层组件。

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
- **IMAGE-06**：导航图。macOS `PhotoViewer.Mac/Native/MacMainWindow.cs`（`NavigatorHostView`）+ `MetalPhotoView.CenterOnNormalizedPoint`；iOS `PhotoViewer.iOS/Native/iOSNativeRootViewController.cs` 的 `UpdateNavigationIndicator`/`OnNavigationPan`；Windows `PhotoViewer.Windows/Native/WindowsNavigator.cs`（`PressAt`/`DragTo`）+ `WindowsPhotoView.CenterOnNormalizedPoint`；Android `PhotoViewer.Android/Native/AndroidNavigationOverlay.cs`（`OnTouchEvent`）+ `AndroidPhotoView.CenterOnNormalizedPoint`。
  - 四端白框都可拖动定位（桌面鼠标 / 触屏手指）；拖框只改视口中心，滚轮与捏合仍归主图缩放松手。
  - **IMAGE-06@android**：点按 = 把该点直接移到视口中心（`AndroidNavigationOverlay` 的 Down/Move 同一路径，已登记 10-18）。
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
- **ANALYSIS-04**：绿框联动。桌面用指针悬停，直接触控用按下；抬起/取消清除，滑入另一瓦片切换活动目标，四端与 Web 共用一个活动目标状态机，平台只提供事件来源。
  - 触控平台没有指针悬停，事件来源为按下/拖动/抬起/取消；不另设触控专属的固定选择状态。
  - **ANALYSIS-04@web**：`PointerEvent` 的 `pointerType` 分流；`mouse` 走悬停，`touch`/`pen` 走按下/拖动/抬起/取消，旧指针的抬起事件按指针 id 忽略，不能清除新目标。
- **ANALYSIS-06**：细节裁剪各端 `DisplayCrop`；代理期留空。
- **ANALYSIS-07**：直方图由当前上屏图现算，入口在同一瓦片宿主。
- **ANALYSIS-08**：准星与 cosine 参考点。四端点按内容矩形落准星；letterbox/角标等同面板空白（macOS 在 `PhotoViewer.Mac/Native/MacDiagnosticTile.cs` 与 `MacAnalysisPanel.cs` 的空白命中里清准星、保留 cosine 参考点）。
  - 直方图不显示准星（Windows `SyncCrosshair` 只同步四张诊断瓦片；Android 只对 `Diagnostic` 画准星），四端一致；直方图点按吞掉事件、不改准星。
- **ANALYSIS-09**：推理提供器 macOS=CoreML、Windows=DirectML、Android=NNAPI、iOS=CPU；编排共享 `AnalysisPresenter`。
- **ANALYSIS-10、ANALYSIS-11**：状态与输入由各端瓦片宿主承担。

## toolbar

- **TOOLBAR-01、TOOLBAR-02**：共享 `PhotoViewer.Shared/ControlBarLayout.cs:21-44`。
  - macOS：`PhotoViewer.Mac/Native/MacControlBar.cs:11`（`NSView`），置于 `NSScrollView`。
  - iOS：`PhotoViewer.iOS/Native/iOSControlBar.cs:15`（`UIView`），置于 `UIScrollView`，`Arrange` 后写 `ContentSize` 并夹取偏移（`iOSNativeRootViewController.cs:538`）。
  - Windows：`PhotoViewer.Windows/Native/WindowsControlBar.cs:18`（`Grid` + `WindowsWrapPanel`），控制栏自带 `ScrollViewer`（`:126`）保存并夹取偏移。
  - Android：`PhotoViewer.Android/Native/AndroidControlBar.cs:17`（单个自绘 `View`），置于 `AndroidControlBarScrollHost`（`AndroidNativeRootView.cs:134`）滚动。
- **TOOLBAR-03**：评分组。自绘控件禁用不换底色、只降内容强调；原生控件可用系统禁用外观但保持可辨、不近似隐藏（与 TOOLBAR-10 同一口径）；评分快捷命令统一画数字（0=叉）。冲突时 0 号换警示三角，字形、浮窗宿主与整组写入见 [rating](#rating)（RATING-04）。
  - **TOOLBAR-03@ios**：评分按钮 30×30 低于 44 点，接受为平台差异，不做长按补偿（已登记 10-18）。
- **TOOLBAR-04**：命令组。桌面 tooltip = 命令名 + 实际主/次绑定；INPUT-13 只控制设置中的键鼠配置可见性；移动端不另造长按浮层。
  - 图标基准为 `icon.font`（uicons Regular Rounded）/ `icon.fontSolid`（实心星）与 `icon.*` 码位；Android 与 Web 直接使用该字体（Android `AndroidUiTheme.IconFontAsset`；Web 由 `PhotoViewer.Web/tools/subset-icons.py` 按令牌码位生成 woff2 子集）。
  - **TOOLBAR-04@mac / @ios**：命令图标与评分星用 SF Symbols（`PhotoViewer.Apple/AppleSymbolNames.cs`），语义同 `icon.command.*`。评分快捷命令为 `star.fill` 内叠数字（`PhotoViewer.Mac/Native/MacControlBar.cs`、`PhotoViewer.iOS/Native/iOSControlBar.cs` 的 `RatingDigitLabel`），0 为 `xmark`。
  - **TOOLBAR-04@win**：命令图标用 Segoe Fluent Icons，评分快捷命令为实心星 FavoriteStarFill `E735` 内叠数字（0=清零叉）；显式映射在 `PhotoViewer.Windows/Native/WindowsIcons.cs`：打开=Folder `E8B7`、缩放适应=BackToWindow `E73F`（双对角箭头）、放大/缩小=Add `E710` / Remove `E738`、增强=Contrast `E7A1`。tooltip 绑定经 `WindowsInput.FormatGesture` 显示可读键名（`D2`→`2`、`Oem3`→`` ` ``，并补 Oem1–Oem8 别名），不再出现枚举名。
- **TOOLBAR-05**：EXIF 摘要。桌面 tooltip `标签 · 值`；触屏完整值由 TOOLBAR-06 的详情页承担。
  - **TOOLBAR-05@win**：值文本取 Bold（12 点 SemiBold 在 Windows 上肉眼与正文同重），标签保持 10 点次要色。
- **TOOLBAR-06**：EXIF 整组命中打开工具。macOS `PhotoViewer.Mac/Native/MacControlBar.cs:44`/`:217-221`；iOS `PhotoViewer.iOS/Native/iOSControlBar.cs:67-71`；Windows `PhotoViewer.Windows/Native/WindowsControlBar.cs:62-66`；Android `PhotoViewer.Android/Native/AndroidControlBar.cs:290-292`（已接线，待实机核对）。
- **TOOLBAR-07**：重建。**TOOLBAR-07@ios**：先 `RemoveFromSuperview` 再 `Dispose`（已登记 10-18）；**@win** 按影响范围合并 dispatcher turn；**@android** `MarkDirty` + `OnDraw` 补齐 `Arrange`。
- **TOOLBAR-08**：溢出。四端都必须有滚动宿主，溢出时可滚、保存并夹取滚动偏移（macOS `NSScrollView`、iOS `UIScrollView`、Windows 控制栏自带 `ScrollViewer`、Android `AndroidControlBarScrollHost`）。滚动只在"单组本身超长"时出现，能换行时必须换行。
  - **TOOLBAR-08@win**：`WindowsWrapPanel.MainLimit` 由 `ScrollViewer` 视口主轴驱动，滚动宿主测量内容时不再拿到无限主轴，因此列布局按组换列、行布局按组换行；`ScrollableMain` 在能容纳时为 0（smoke 断言 700 宽两向均 ≥2 行且不可滚）。
- **TOOLBAR-09**：无照片/空控制栏。三组为空时只保留四周 `toolbar.padding`；各端由 `ControlBarLayout` 或 `WindowsLayout` 计算文档长度。
- **TOOLBAR-10**：视觉与反馈。底色/描边取 `color.toolbar.*`，圆角 `toolbar.cornerRadius`；星形与清零叉字号分别取 `toolbar.rating.glyph.fontSize` / `toolbar.rating.clearGlyph.fontSize`。
  - **TOOLBAR-10@mac / @win / @ios / @android**：原生按钮/开关可用系统悬停、按下、禁用、焦点色与轻量状态动效（L4）；占位、命中区与令牌几何一致，状态切换不引起布局抖动、残留或裁切。自绘控件按 `color.toolbar.*` 规则绘制，禁用只降内容，圆角不与系统 bezel 裁切叠加。
  - **TOOLBAR-10@win**：控件状态外观集中在 `PhotoViewer.Windows/Native/WindowsControlStyles.cs`：在 WinUI 模板上按元素覆盖 Button/ToggleButton 的状态主题键，禁用底色与描边保持常态、只把内容降到 `color.toolbar.disabled.content.alpha`（0.42），悬停/按下取 `color.toolbar.command.*` / `color.toolbar.rating.*`；相似聚类开关补齐 `Checked/PointerOver/Pressed/Disabled` 前景，悬停不发黑。筛选下拉去掉自定义描边/圆角/字号，回到 WinUI 原生外观，只保留 90×32 占位。
- **TOOLBAR-11**：焦点与键盘。每个控件的焦点顺序按 TOOLBAR-11 由各端控件宿主给出，Enter/Space 触发与命令同一入口。
- **TOOLBAR-12**：开关型命令的按钮态。四端当前都不在按钮上表达按下态/强调色，状态只在菜单勾选与面板可见性上。

- **TOOLBAR-06@web**：没有工具窗口，控制栏 EXIF 组不可点击、无命中区域。

## similarity

<a id="sim"></a>

- **SIM-01**：入口与开合。共享 `PhotoViewer/ViewModels/Main/File/SimilarityPanelViewModel.cs:50`，展开态从 `SettingsViewModel.SimilarityPanelExpanded` 读取、经 `IViewStateStore` 持久化（macOS/iOS `AppleViewStateStore`、Windows `WindowsNativeViewStateStore`、Android `AndroidViewStateStore`）；各端面板类 `PhotoViewer.Mac/Native/MacSimilarityPanel.cs:22`、`PhotoViewer.iOS/Native/iOSSimilarityPanel.cs:23`、`PhotoViewer.Windows/Native/WindowsSimilarityPanel.cs:21`、`PhotoViewer.Android/Native/AndroidSimilarityPanel.cs:22`。
- **SIM-02、SIM-03**：空态与动作。macOS 空态常量 `PhotoViewer.Mac/Native/MacSimilarityPanel.cs:33`/`:35`；动作按钮与进度各端自绘。
  - **SIM-03@android**：Material 文本动作（64/整宽）与文本进度，信息等价，点按区不小于 44dp（已登记 10-18）。
- **SIM-04、SIM-05**：卡片复用胶片栏卡片；点选走 `MainViewModel.SetCurrentImageKeepAnchor`，双高亮由各端绑定 `IsCurrent` / `IsCurrentImage`。
- **SIM-06、SIM-08**：计算与结果。阈值/数量 `PhotoViewer/ViewModels/Settings/SettingsViewModel.AI.cs:17-26`；服务 `PhotoViewer/Core/AI/SimilarityService.cs`。
- **SIM-09**：面板内改星走与胶片栏同一写回路径，锚点不变；筛选落出由共享 `PhotoViewer/ViewModels/Main/File/ThumbnailListViewModel.cs` 处理。
- **SIM-07**：几何。共享 `PhotoViewer.Shared/SimilarityPanelGeometry.cs`；四端一律用 `RowWidth` 夹到可用宽一半，`dockWidth` 参数承载列表态停靠区宽，各端由相似面板的 `DockMeasureWidth`（只在列表态且动作区显示时非零）传入，停靠区内容变化即重排。Windows 面板的 `SetAvailableWidth` 按 `RowWidth(hasItems, itemCount, availableWidth, dockWidth)` 全额落宽（行布局）或 `PanelWidth`（列布局），外框再让出 4 点右边距；`MaxWidth` 同步夹到可用宽一半。
- **SIM-10、SIM-11**：错误与输入等价。列表态与空态的动作区都必须承载错误标签，列表态由共享 `ShowActionAreaError` 驱动；macOS `MacSimilarityPanel`、iOS `iOSSimilarityPanel.cs:126`、Windows `WindowsSimilarityPanel` 的停靠错误标签、Android `AndroidSimilarityPanel.cs:96` 各自绑定。

## menu

- **MENU-01、MENU-02**：主图菜单。
  - macOS：`PhotoViewer.Mac/Native/MacMainWindow.cs`（`ContextMenu` 创建 `NSMenu`，`UpdateMenuChecks` 同步勾选与可用性）。系统菜单栏由同文件 `BuildMenus` 建立：应用菜单「设置… / 分割线 / 退出 PhotoViewer」、文件菜单「打开文件… / 打开文件夹…」、显示菜单「胶片栏 / 分析栏 / 控制栏 / 分割线 / 雾天增强」、工具菜单「EXIF 详情… / DINO 诊断…」与操作菜单；操作菜单按设置的完整命令集合和顺序生成，不受控制栏显隐影响。显示菜单只含这三个栏位开关、一个分割线与雾天增强，不放布局模式、全屏、缩放或其他项；工具菜单直达同一个工具窗口的目标分类，全部命令与右键菜单、控制栏按钮走同一执行入口。
  - iOS：`PhotoViewer.iOS/Native/iOSContextMenus.cs:49-69`（`UIMenu`）。
  - Windows：`PhotoViewer.Windows/Native/WindowsMainWindow.cs:342-384`（`MenuFlyout`）。
  - Android：`PhotoViewer.Android/Native/AndroidNativeRootView.cs:478-583`（自绘 `PopupWindow`）。
- **MENU-03、MENU-04**：弹出与收回。macOS/iOS/Windows 用系统菜单；Android 自绘并夹取到屏幕内（`PhotoViewer.Android/Native/AndroidNativeRootView.cs:566-568`）。
  - **MENU-03@android**：自绘弹层，定位与夹取自实现（已登记 10-18）。
  - **MENU-04@ios**：卡片菜单保留系统抬升，不移动列表布局（已登记 10-18）。
- **MENU-05**：卡片菜单。iOS `PhotoViewer.iOS/Native/iOSContextMenus.cs:77-93`；Android `PhotoViewer.Android/Native/AndroidThumbnailCard.cs:288-307`；桌面无卡片菜单，用 tooltip + 星级按钮。
  - **MENU-05@android**：标题与时间可拆成两行原生外观；标题行不可点击，时间补秒与时区（已登记 10-18）。
- **MENU-06@mac / @ios**：主图菜单（macOS 右键、iOS 长按）与 macOS 系统菜单栏都使用 SF Symbols，共用 `PhotoViewer.Apple/AppleSymbolNames.cs` 的 `Menu` 映射；Mac 独有「打开文件…」用 `doc`、应用菜单「退出 PhotoViewer」用 `power`，系统菜单栏与右键菜单一样显示图标。
- **MENU-07**：不可用项与并发。各端菜单项与 macOS 显示菜单的集合都不随照片/列表内容增删；无照片时「雾天增强」置灰、工具打开空态（macOS `MacMainWindow.cs` 的 `AddCheck`、iOS `iOSContextMenus.cs:40`、Windows `WindowsMainWindow.cs:418`、Android `AndroidNativeRootView.cs:607`）。
- **MENU-01@web**：不实现设置与工具窗口，主图菜单不生成「工具…/设置…」及其分组，其余项、顺序与勾选语义不变（SCOPE-01）。
- **MENU-03@web**：主图菜单与卡片菜单为自绘弹层（MENU-03 自绘外观），夹在窗口内，键盘语义按 MENU-03；悬停提示同样自绘（`tooltip.*`）。
- **MENU-05@web**：触屏（`pointerType=touch`）长按卡片弹卡片菜单；桌面右键卡片仍弹主图菜单。

## input

- **INPUT-01、INPUT-02**：手势到命令。macOS `PhotoViewer.Mac/Native/MacInput.cs:11`；iOS `PhotoViewer.Apple/AppleInputMapping.cs:11`；Windows `PhotoViewer.Windows/Native/WindowsInput.cs:14`；Android `PhotoViewer.Android/Native/AndroidInputMapping.cs:11`；共享命令表 `PhotoViewer.Shared/InputCommandMap.cs`。
- **INPUT-03、INPUT-04、INPUT-06**：主图手势。macOS `MetalPhotoView.ScrollWheel`/`MagnifyWithEvent`；iOS `UIScrollView` + 单指轻扫；Android `ScaleGestureDetector` + `GestureDetector`；Windows `WindowsPhotoView` 指针手势。滚轮精度分流四端统一：精确滚动走平移、离散一格一命令（Windows `WindowsInput.cs:125` 的 `FromWheel`、Android `AndroidPhotoView.cs:422` 的 `OnGenericMotionEvent`）。
- **INPUT-05**：按键重复。只对可重复命令重复（`InputCommandMap` 的 Repeatable 标记，四端重复入口统一按此设闸）；macOS/Windows/Android 跟系统重复节奏，iOS 用固定节奏。
  - **INPUT-05@ios**：固定 350 ms / 60 ms 节奏（`PhotoViewer.iOS/Native/iOSNativeRootViewController.cs:30`/`:32`），属平台差异（已登记 10-18）。
- **INPUT-07、INPUT-08**：菜单与录制。macOS 真实 `NSEvent` 录制；Windows `WindowsHotkeyRecorder`；触控优先设备在连接键盘/鼠标/触控板时使用同一录制语义。
- **INPUT-13**：外设能力与快捷键控件可见性。桌面始终完整；触控优先设备按系统报告的连接状态增删键鼠专用控件。
  - **INPUT-13@ios**：键盘/鼠标/触控板连接状态取系统通知；连接任一完整显示主/次绑定、录制、清空与冲突，仅直接触控隐藏全部键鼠相关控件但保留模型中的绑定配置；断开最后一个外设取消未完成录制并保留原绑定。Apple 修饰键映射在同一可见条件下显示。
  - **INPUT-13@android**：连接状态取 `InputManager` 的设备增删；可见性与录制语义同 iOS，动态连接不重置工作副本、不弹软键盘。
  - **INPUT-13@mac / @win**：桌面保持完整快捷键设置；Apple 修饰键映射只在 macOS 且外设可见条件满足时显示（`PhotoViewer.Apple/AppleInputMapping.cs`）。
  - 直接触控的手势入口（按压显示绿框、长按菜单、单指轻扫翻页）按实际事件类型处理；连接鼠标或触控板后仍可使用悬停，不能把“触控平台”视为无悬停能力。具体事件映射见各端入口；键鼠设置可见性独立按 INPUT-13 的外设连接状态决定。
- **INPUT-09**：预设/比例缩放入口。macOS `PhotoViewer.Mac/Native/MacMainWindow.cs:553`；iOS `PhotoViewer.iOS/Native/iOSNativeRootViewController.cs:605`；Windows `PhotoViewer.Windows/Native/WindowsMainWindow.cs:273-276`；Android `PhotoViewer.Android/Native/AndroidNativeRootView.cs:623-624`。
- **INPUT-10**：无照片时按钮禁用不隐藏、打开类命令始终可用；入口由各端控制栏与空态命中层实现。
- **INPUT-11**：Android `OnBackPressedCallback` 先关设置/工具 sheet 与菜单。
- **INPUT-12**：触屏等价入口。卡片评分由卡片菜单补足（iOS `PhotoViewer.iOS/Native/iOSContextMenus.cs`、Android `PhotoViewer.Android/Native/AndroidThumbnailCard.cs:288-307`）；焦点顺序无专项实现。
- **INPUT-01@web**：浏览器保留 Ctrl/⌘+N，「打开文件」主绑定不可用，次绑定 Ctrl+O 与空态点击、菜单为等价入口。
- **INPUT-04@web**：`WheelEvent` 没有精确/离散标志：`deltaMode` 为行/页，或像素模式下 `wheelDeltaY` 为 120 的整数倍且增量足够大时视为离散，其余为精确；Chromium/Safari 把触控板捏合投递为 ctrl+wheel，未按下物理 Ctrl 时按捏合处理。

## open

- **OPEN-01、OPEN-06、OPEN-07**：入口与外部打开。外部打开、拖入与命令行入口先把目标转成 `IStorageItemRef`，再投递实例级 `OpenRequestChannel`，由 `OpenCoordinator` 单消费者按顺序处理（`PublishAndWaitAsync` 供验收等待本批完成）；一批只取首个可打开项。选择器直接进入同一 `FolderViewModel` 打开入口。
  - macOS：`PhotoViewer.Mac/Native/MacMainWindow.cs`（`NSOpenPanel` / 拖入）、`PhotoViewer.Mac/Native/MacOpenPaths.cs`（命令行、Finder/Dock、URL → `LocalFileRef`/`LocalFolderRef`）、`PhotoViewer.Mac/Native/MacNativeApplication.cs`（消费 `OpenRequestChannel`）。
  - iOS：`PhotoViewer.iOS/Native/iOSNativeRootViewController.cs`（`UIDocumentPicker` / `UIDropInteraction`）、`PhotoViewer.iOS/Core/iOSExternalOpenBridge.cs`（URL → 本地引用 → 共享通道）。
  - Windows：`PhotoViewer.Windows/Native/WindowsMainWindow.cs`（选择器 / 拖入）、`PhotoViewer.Windows/Native/WindowsOpenPaths.cs`（命令行/文件激活）、`PhotoViewer.Windows/Native/WindowsSingleInstance.cs`（二次启动转发到主实例的通道；隔离验收不注册）。
  - Android：`PhotoViewer.Android/Native/AndroidNativeRootView.cs`（SAF）、`PhotoViewer.Android/Native/AndroidNativeShell.cs`（`ACTION_VIEW/SEND` 合并后投递共享通道）。
  - **OPEN-01@ios / @android**：只有文件夹选择器（已登记 10-18）。
  - **OPEN-07@android**：未接 `OnDragListener`，手机无常见拖入来源，不建拖入入口（已登记 10-18）。
- **OPEN-02、OPEN-04、OPEN-05、OPEN-08**：共享 `PhotoViewer/ViewModels/Main/FolderViewModel.cs`（打开、枚举与租约）与 `PhotoViewer/ViewModels/Main/File/ThumbnailListViewModel.cs`（列表重建与元数据回填）。所有显式打开的刷新范围、保留项与失败边界以 OPEN-08 为准，RATING 来源与 sidecar 的重读口径见 RATING-01；新目标回到适应态、当前目标保留视口。
  - 新目标的显式打开前调用 `FitToView()`；当前目标刷新不调用。四端统一由共享 `MainViewModel.RequestFitToView()` + 共享 `PhotoViewport` 实现（见 status.md OPEN-05）。
- **OPEN-03、OPEN-09**：单图模式与派生状态由共享 VM 承担；每次打开由 `FolderLoadSession` 持有存储访问租约（iOS `iOSStorageAccessSessionFactory` / Android `AndroidStorageAccessSessionFactory` / 桌面本地会话）。新快照提交成功时释放被取代会话的租约，当前快照保留到下一次提交或文件源释放；父目录不可读时回退单图。
- **OPEN-01@web / OPEN-07@web**：没有真实文件系统，打开文件/文件夹使用应用内选择器浏览合成文件夹；拖入按文件名匹配合成库，否则打开首个合成文件夹。

## state

- **STATE-01、STATE-02**：提示与空态。macOS `PhotoViewer.Mac/Native/MacMainWindow.cs:135`/`:744`；iOS `PhotoViewer.iOS/Native/iOSNativeRootViewController.cs:1101`；Windows `PhotoViewer.Windows/Native/WindowsMainWindow.cs:858`；Android `PhotoViewer.Android/Native/AndroidNativeRootView.cs:933`。切图方案由共享加载会话选定：缓存命中同步换图（保留前一张），四端主图视图以 `PhotoLoadSession.Generation` 判定切图；未命中清图并以 `IsLoadingVisible`（`status.loading.delay` 300ms）提示。程序化覆盖：Mac `--pv-native-filmstrip`、iOS `--pv-native-photo-check`、Android `pv-check main`，Web `?check=state`。
- **STATE-03**：操作结果提示。四端统一为 4 s 非模态提示条：macOS `PhotoViewer.Mac/Native/MacStatusToast.cs`（`MacMainWindow` 订阅 `Session.Notice`）、iOS `PhotoViewer.iOS/Native/iOSStatusToast.cs`（`iOSNativeRootViewController` 调用）、Windows `PhotoViewer.Windows/Native/WindowsMainWindow.cs`、Android `PhotoViewer.Android/Native/AndroidNativeRootView.cs`。评分写回与"部分来源写入失败（补偿未完成）"分列文案（RATING-03）；后者必须按磁盘真值展示每个来源与冲突状态，不把部分成功当整组成功。
- **STATE-04**：模型/推理/数据库不可用由共享 `AnalysisPresenter`、`SimilarityPanelViewModel` 与设置清库路径承担。

## settings

- **SETTINGS-01、SETTINGS-02**：工作副本 `PhotoViewer/Core/Settings/NativeSettingsSession.cs`；分类与行由各端设置宿主实现。
  - macOS：`PhotoViewer.Mac/Native/MacNativeSettingsPresenter.cs:15`、SwiftUI `PhotoViewer.Mac/Native/SwiftSettings/MacSettingsUI.swift`。
  - iOS：`PhotoViewer.iOS/Core/iOSNativeSettingsPresenter.cs:17`（`PageSheet`）。
  - Windows：`PhotoViewer.Windows/Native/WindowsSettingsWindow.cs:15`（`NavigationView` + `SettingsCard`）。
  - Android：`PhotoViewer.Android/Native/AndroidNativeSettingsPresenter.cs:25`（`BottomSheetDialog`）。
  - iOS / Android：行结构随 INPUT-13 外设能力变化；连接硬件键盘/鼠标/触控板任一显示完整绑定、录制、清空与冲突，仅直接触控隐藏绑定文本、录制、清空、冲突与键盘映射等全部键鼠相关控件，保留命令显隐、排序与模型中的绑定配置。
  - **SETTINGS-01@ios**：控制栏功能沿用系统勾选排序列表，与 macOS 同一行结构（勾选 + 命令名 + 主/次绑定按钮 + 系统排序把手）；绑定按钮只在有外设时出现，点按即在行内录制，长按或辅助点按出「清空」菜单。录制期间手指轻点任意处取消录制并保留原绑定。
- **SETTINGS-03**：平台外观。macOS `NavigationSplitView` + `Form(.grouped)`；Windows Mica + `TitleBar` + `NavigationView`；iOS 半屏 sheet；Android Material 3 `BottomSheetDialog`。四端设置页与工具页固定深色；iOS 整个应用固定深色（`Info.plist` `UIUserInterfaceStyle = Dark` + 窗口覆盖），不随系统浅/深切换，页面不铺不透明底以保留 sheet 半透材质。
  - **SETTINGS-03@mac / @win / @ios / @android**：各自原生设置外观，明暗统一固定深色（已登记 10-18）。
- **SETTINGS-04**：入口与关闭。四端入口都在主图菜单末项；关闭统一走 `NativeSettingsSession.ApplyAsync()` 一次提交并 `Dispose`（幂等）。
- **SETTINGS-05**：编辑隔离。工作副本以 `applyRuntimeSettings: false` 创建，缓存/解码/键盘映射副作用都在提交后执行（`PhotoViewer/ViewModels/Settings/SettingsViewModel.cs:40`）。
- **SETTINGS-06、SETTINGS-07、SETTINGS-08、SETTINGS-09、SETTINGS-10、SETTINGS-11、SETTINGS-12、SETTINGS-14、SETTINGS-15**：控件与持久化。
  - **SETTINGS-12@android**：一般数值用 `+/-` 步进（与 iOS `UIStepper` 同类），步进控件不改变取值范围（已登记 10-18）。缩放比例预设行例外：单一数值输入 + `%`，数字居中，`+`/`−` 只做新增 100% 项与删除项，不是步进（SETTINGS-07）。
  - **SETTINGS-06@ios**：缓存容量由运行时实测预算（`phys_footprint + os_proc_available_memory`，启用 `increased-memory-limit`）自动推导，文件页「缓存与预加载」组不显示「缓存内存上限 / 缓存数量上限」；设置文档中的两项字段保留、不参与 iPad 运行时。
  - **SETTINGS-06@win**：缓存与解码的默认值由物理内存与核数推导（`PhotoViewer.Windows/Core/WindowsPerformanceBudget.cs`）：缓存内存上限默认物理内存 ÷ 4（夹在 1024–8192 MB），数量上限按单张 2400 万像素 × 每像素 8 字节（线性 RGBA16F）折算并夹在 3–30 张，后台解码线程按 核数 ÷ 3 夹在 2–6 且每 GB 缓存内存至少 1 路。提示行第一行的内存上限取运行时预算（物理内存 × 50%）、足迹为进程私有提交（`WindowsMemoryBudgetProvider`）；第二行按实测有效缓存容量换算，容量随每轮内存探测刷新。已保存的设置覆盖默认值，不做迁移。CPU 解码线程沿用跨平台默认口径。
  - **SETTINGS-06@android**：内存预算取前台可见进程的匿名内存上限（`PhotoViewer.Android/Core/AndroidMemoryBudgetProvider.cs`，按 `MemTotal` 查 AOSP MemoryLimiter 可见档：3.2–4.8 GB → 2 GB、4.8–6.8 GB → 4 GB、6.8–9 GB → 5 GB、9–14 GB → 8 GB、14–18 GB → 10 GB，档外按 45% / 62.5% 外推；厂商另设前台红线时取较小者——小米 HyperOS/MIUI 的 Sentinel 读 `persist.sys.stability.pss_highest_line`，预算取其 90%，小米 17 Ultra 实测超过 6 GB PSS 即被强杀），占用 = `RssAnon + VmSwap` + GL 纹理字节。缓存容量由该预算自动推导（`BitmapLoader.AutomaticCacheLimit`），文件页「缓存与预加载」组不显示「缓存内存上限 / 缓存数量上限」，两项用户字段保留；预载上限按自动口径固定 10。提示第一行为预算（与缓存推导同口径，不是设备总内存），第二行按有效缓存容量与 8 位代理尺寸换算；原生解码线程默认 6 核及以上 2 路、否则 1 路，软件解码线程恒为 1。
  - **SETTINGS-10@ios / @android**：AI 页无「清空特征数据库」；移动端长期不提供，配置保留、入口不显示（已登记 10-18）。
  - iOS / Android：绑定文本、录制、清空与冲突按 INPUT-13 外设能力显示；仅直接触控全部隐藏，断开最后一个外设时取消未完成录制并保留原绑定，不按平台固定为只读。
  - **SETTINGS-14**：四端共用 `SettingsRepository` + `SettingsDocumentCodec`（schemaVersion 2）；旧/缺失版本不迁移、不写字段级迁移补丁，先备份再默认值；普通 IO、未来版本或备份失败进入不可写且不覆盖原文；保存成功才发布，提交不做延迟合并。文件载体（Windows、macOS/iOS 验收）用原子替换，偏好/SharedPreferences 用平台同步提交；损坏备份最多保留 3 份。
  - **SETTINGS-14@mac / @ios**：生产环境 JSON 存 `NSUserDefaults` 单键（`PhotoViewer.Apple/AppleSettingsStore.cs`）；macOS / iOS 验收会话改用隔离 `FileSettingsStore`，不读写用户设置。
  - **SETTINGS-14@win**：JSON 存本地设置文件（`FileSettingsStore`，唯一临时文件 + 原子替换），普通运行与隔离验收使用不同目录。
  - **SETTINGS-14@android**：JSON 存 `SharedPreferences`（`PhotoViewer.Android/Core/AndroidSettingsStorage.cs`），后台 `Commit` 并检查返回值；损坏/旧版备份为带时间戳的偏好键，最多 3 份。
  - 排序四端统一为拖拽（Android 用 `ItemTouchHelper` + 行尾把手，把手轻点给上移/下移菜单，`PhotoViewer.Android/Native/AndroidSettingsReorderList.cs`）。
- **SETTINGS-12**：数值校验。范围夹取在各设置视图模型（如 `PhotoViewer/ViewModels/Settings/SettingsViewModel.BitmapCache.cs`、`SettingsViewModel.AI.cs`），非法输入不写入。
- **SETTINGS-13**：键盘与显示缩放。桌面由系统控件提供 Tab/方向键语义；触屏由各端设置列表承担；动态字体与显示缩放随系统，四端均无专项断言。
- **SETTINGS-14@web**：Web 参考壳只持久化影响主屏的设置项，列表项使用壳内 id，文档结构不与四端设置 JSON 互换。

## tools

- **TOOLS-01、TOOLS-02**：外壳与入口。
  - macOS：`PhotoViewer.Mac/Native/MacToolsWindow.cs:16`（独立 `NSWindow`，原生侧栏 + 详情；默认 EXIF，无可用 EXIF 时落 DINO）。
  - iOS / iPadOS：`PhotoViewer.iOS/Native/iOSToolsPresenter.cs`（设置同款 `PageSheet` + `UINavigationController` 分组列表导航；初次打开显示工具列表，不自动进入 DINO）。
  - Windows：`PhotoViewer.Windows/Native/WindowsToolsWindow.cs:16`（`NavigationView` 三分类，无首页）。
  - Android：`PhotoViewer.Android/Native/AndroidToolsPresenter.cs:24`（设置同款 `BottomSheetDialog` + 分组列表导航；初次打开显示工具列表，系统返回先回列表再关闭浮层）。
  - **TOOLS-01@win**：工具窗口 960×720、最小 800×560，因常驻侧栏 240 + EXIF 表格 720；普通窗口（进任务栏/Alt-Tab、可最大化）（已登记 10-18）。
  - **TOOLS-01@mac / @ios / @android**：不建「照片数据统计」入口（已登记 10-18）。
- **TOOLS-02@win**：无首页/返回，常驻三分类；分类顺序与其他端一致为 EXIF → DINO → 照片数据统计（已登记 10-18）。
- **TOOLS-02@mac**：窗口外壳与设置共用同一 SwiftUI 侧栏组件与窗口样式（`PhotoViewer.Mac/Native/SwiftSettings/MacToolsUI.swift`），侧栏几何、选中态与工具栏不另行仿制；详情仍是 AppKit 页面。侧栏常驻、不设工具首页；默认 EXIF，无可用 EXIF 时落 DINO。
- **TOOLS-02@ios / @android**：分组列表导航，初次打开显示工具列表，不自动进入 DINO；无当前照片或 EXIF 详情不可用时退回列表。只读边界、生命周期、7 列 EXIF 与 3+2 瓦片不变；原生表格分隔线/分组材质可按 L4 取平台样式，业务列比例与内容不随之改变，也不新增功能。
- **TOOLS-03、TOOLS-04**：EXIF 详情。macOS 为虚拟化 `NSTableView`（`PhotoViewer.Mac/Native/MacExifDetailView.cs`）：标签行自绘文本，点击时才在该文本上放置可选中文本框，行高按列宽缓存；iOS `UITableView`；Windows 7 列 `Grid`；Android 线性行 4 个内容列。
  - **TOOLS-03@android**：4 个内容列承载 7 列几何，手机与平板同口径（已登记 10-18）。
- **TOOLS-05**：DINO 五瓦片。共享 `PhotoViewer/Core/AI/DinoDiagnosticPresenter.cs`；五瓦片 3+2，第六格留空，刚体拟合文本以整宽块置于网格下方（四端统一）。
- **TOOLS-06**：计算时机。共享 presenter 先读库；离开页面或关闭窗口即取消在途任务（`ToolsViewModel.Deactivate()`，macOS `MacToolsWindow`、iOS `iOSToolsPresenter.cs:65`、Windows `WindowsToolsWindow.cs:328`、Android `AndroidToolsPresenter.cs:171`）。
- **TOOLS-07**：照片数据统计，仅 Windows（`PhotoViewer/ViewModels/Tools/ToolsViewModel.cs:35`）。
  - **TOOLS-07@win**：平台专属工具，其他端不建入口（已登记 10-18）。
- **TOOLS-01@web**：不实现工具窗口。

## scope

- **SCOPE-01**：可见性由共享 VM 布尔属性决定；照片数据统计 `ToolsViewModel.IsPhotoStatsAvailable => OperatingSystem.IsWindows()`（`PhotoViewer/ViewModels/Tools/ToolsViewModel.cs:35`）。
  - **SCOPE-01@mac / @ios / @android**：不建照片数据统计入口（已登记 10-18，同 TOOLS-07@win）。
- **SCOPE-02**：计算门控。相似 `PhotoViewer/ViewModels/Main/File/SimilarityPanelViewModel.cs:50`；分析 `PhotoViewer/Core/AI/AnalysisPresenter.cs:136-142`；回退 `FallbackDelayMs = 500`（`PhotoViewer/Core/AI/AnalysisPresenter.cs:34`）。
- **SCOPE-03**：入库与现算。批量提取 `PhotoViewer/Core/AI/FolderFeatureIndexer.cs`；即时缓存 `AnalysisResultCache`。
- **SCOPE-04**：能力缺失。**SCOPE-04@win**：RAW 主图/代理走系统 WIC RAW 引擎（Raw Image Extension）全分辨率解码，按相机 `DefaultCrop` 裁切、方向交渲染层；解码器缺失或失败时只接受与有效成像区尺寸一致的整幅内嵌 JPEG，否则如实停在可得尺寸。**SCOPE-04@ios**：RAW 主图/代理经 `CIRawFilter` 全分辨率解码（几何取传感器 `NativeSize`，EXIF 方向交 GPU），只有创建/渲染失败回退 ImageIO 时才可能只得到内嵌预览、`IsProxy=false`，此时如实停在该尺寸；CoreML EP 不可用回退 CPU（已登记 10-18）。**SCOPE-04@android**：RAW 走打包的 LibRaw（`AndroidRawDecoder`）全分辨率开发，半尺寸输出已满足代理目标时用 half_size，输出 sRGB 或 Display P3；首次解码空窗期用内嵌预览顶替（STATE-01），真产物到达即替换，顶替图不冒充全分辨率，细节预览不以它为源；`libpvraw.so` 缺失时 RAW 停用并记一次日志（不回退系统预览冒充主图）。模拟器 P3 协商失败回退 sRGB（已登记 10-18）。
- **SCOPE-05**：文件写操作。评分写回走共享 `RatingService`：组的每个文件内嵌与每个实际存在的 `<stem>.xmp` 都是来源，RAW 也写内嵌（Sony 相机与 Imaging Edge 只读 RAW 本体），sidecar 不存在时不创建（RATING-01/03）；无删除/重命名/移动/导出命令。
- **SCOPE-06**：平台专属。导航图点按/拖动定位（**SCOPE-06@ios / @android**，已登记 10-18）；Windows 照片统计；Android 设置数值步进见 SETTINGS-12@android。
- **SCOPE-01@web**：设置窗口、工具窗口、真实解码/写回/推理均不实现，相应入口不显示；照片、特征与故障均为合成数据。

