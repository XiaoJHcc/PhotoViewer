# 平台实现（L4）

本文件记录同一设计规则在四端用什么控件、实现入口在哪里、哪些差异已经登记。分层定义见 [README §2](README.md#2-分层)：L4 只回答"怎么做"，不写规则本身，也不写验收状态（见 [status.md](status.md)）。平台代号取 `win` / `mac` / `ios` / `android` / `web`（[README §4](README.md#4-编号)）。

实现入口写成工作区相对路径加类型或成员名（`文件` 的 `符号`），不写行号——主窗口等大文件持续增长，行号很快失效。已登记差异写成 `**ID@平台**：…`，裁定来源见 [rulings](../Plans/native-rebuild-phase-6-rulings/rulings-overrides.md)。未登记的偏离不写在本文件，一律记为 [status.md](status.md) 的缺口。

## 控件选型总览

| 分区 / 功能 | macOS | iOS·iPadOS | Windows | Android |
|---|---|---|---|---|
| 窗口外壳与分区 | AppKit `NSWindow` + `ShellPaneLayout.Partition` 落 frame | `UIViewController` + 安全区 + `ShellPaneLayout` / `PhoneScreenLayout` | WinUI 3 满窗口 `SwapChainPanel` + `ShellPaneLayout.Partition` 落位浮层栏位，`WindowsLayout` 转接令牌 | `Activity` + insets + `ShellPaneLayout` / `PhoneScreenLayout` |
| 胶片栏 | `NSCollectionView` + `MacFilmstripLayout` | `UICollectionView` + `iOSFilmstripLayout` | `ItemsRepeater` + `StackLayout` + `ScrollViewer` | `RecyclerView` + `AndroidFilmstripView` |
| 筛选条 | `NSGlassEffectView` + `NSSegmentedControl` + 单胶囊回退 + 原生 `FilterToggleButton` | 单个玻璃胶囊内筛选 / 更多两段 + `UIMenu` | 原生按钮材质胶囊内三段 `Button` + 单胶囊回退 + `MenuFlyout` + 原生 `ToggleButton` | Material 胶囊容器内筛选 / 更多两段 `MaterialButton` + 自绘 `PopupWindow` |
| 主图 | `MetalPhotoView`（`CAMetalLayer`）+ 共享 `MetalPhotoRenderer` | `UIScrollView` + 透明 `CAMetalLayer` 子层 | `SwapChainPanel` + D3D11 F16 | `TextureView` + `SurfaceTexture` + EGL14/GLES |
| 导航图 | 自绘 `NSView`（`NavigatorHostView`），白框可拖动定位 | 自绘 `UIView`，白框可拖动定位 | `WindowsNavigator`，白框可拖动定位 | `AndroidNavigationOverlay`，白框可拖动定位 |
| 分析栏 | `MacAnalysisPanel`（`NSView`） | `iOSAnalysisPanel`（`UIView`） | `WindowsAnalysisPanel`（`Grid` + `IAnalysisHost`） | `AndroidAnalysisPanel`（自绘 `View`） |
| 控制栏 | `MacControlBar`（`NSView`） | `iOSToolbarView`（`UIView`） | `WindowsControlBar`（`Grid` + `ScrollViewer` + `ToolbarFlowLayout` 文档面板） | `AndroidControlBar`（`FrameLayout`：命令 `MaterialButton` + 自绘 EXIF / 星级子视图） |
| 相似面板 | `MacSimilarityPanel` + `NSCollectionView` | `iOSSimilarityPanel` + `UICollectionView` | `WindowsSimilarityPanel`（`Grid`） | `AndroidSimilarityPanel`（`LinearLayout` + `RecyclerView`） |
| 主图 / 卡片菜单 | `NSMenu` | 主图 `UIContextMenuInteraction` + `UIMenu`；卡片改为星级弹窗 | `MenuFlyout` | 自绘 `PopupWindow` |
| 设置窗口 | SwiftUI `NavigationSplitView` + `Form(.grouped)` | UIKit 半屏 `PageSheet` | WinUI `NavigationView` + `SettingsCard` | Material 3 `BottomSheetDialog` |
| 工具窗口 | 设置同款 SwiftUI `NavigationSplitView` 外壳 + AppKit 详情 | 设置同款 `PageSheet` + `UINavigationController`（分组列表导航） | 独立 WinUI 窗口（常驻三分类） | 设置同款 `BottomSheetDialog`（分组列表导航） |
| 状态提示 | 自绘提示条 4 s | 自绘提示条 4 s | 自绘状态提示条，4 s | `TextView` 提示条，4 s |
| 输入 | `NSEvent` + `MacInput` | `UIPress` + `AppleInputMapping` | `WindowsInput`（虚拟键 / 滚轮） | `AndroidInputMapping`（`KeyEvent`） |
| 打开 | `NSOpenPanel` + 根视图拖入 | `UIDocumentPicker` + `UIDropInteraction` | `FileOpenPicker` / `FolderPicker` + `Drop` | SAF `ACTION_OPEN_DOCUMENT_TREE` + `ACTION_VIEW/SEND`（拖入见 OPEN-07@android） |
| EXIF 详情表 | 自绘 `NSView` + `ExifTableGeometry`（7 列） | `UITableView`（7 列几何） | `Grid`（7 列） | 线性行（4 个内容列承载 7 列几何，见 TOOLS-03@android） |

Web 参考壳（[PhotoViewer.Web](../PhotoViewer.Web/AGENTS.md)）的几何与行为只依据本目录实现，宽形态主屏控件外观以 macOS 原生壳为参照（[README](README.md) 外观对标链），手机形态沿用 iOS 口径；所有分区都是 DOM 组件（`PhotoViewer.Web/src/shell/*.js`），业务与几何在 `PhotoViewer.Web/src/core/`（`app.js` 主屏模型、`layout/` 几何）。它不进入四端验收，`status.md` 不设 Web 列；行为由 `?check=all` 页内检查覆盖。主图用 Canvas 2D 按方向码变换绘制，下拉与菜单自绘，选择器为应用内合成文件夹列表。

## 令牌层平台差异（tokens.json `platforms`）

下表集中呈现 `tokens.json` 已登记的 `platforms` 值。平台值只保留输入方式、文字度量与控件结构造成的差异，其余一律为令牌单值。Android 自绘设置列表另有专用的 `settings.android.*` 令牌域，用法见 [settings](#settings)。

| 令牌 | 平台值 | 说明 |
|---|---|---|
| `filter.toggle.width` | `android` 76 | AI 相似聚类开关行布局的最小自然宽；筛选主体几何取 `MobileFilterLayout` 与手机槽位 |
| `analysis.tile.innerCornerRadius` | `win` 0 | 单层裁切（ANALYSIS-02@windows） |
| `color.text.secondary` | `macos` / `ios` system-secondary-label | Apple 系统次级文字色，与 #AAA 肉眼差 ≤2 阶（G6-34@mac/@ios） |
| `tools.window.default.width` | `win` 960 | 常驻侧栏 240 + EXIF 表格 720（TOOLS-01@win） |
| `tools.window.default.height` | `win` 720 | 与 macOS 同高 |
| `tools.window.min.width` | `win` 800 | 7 列 EXIF 表格最小宽 |
| `tools.window.min.height` | `win` 560 | 侧栏 + 表格的最小高 |

## film

胶片栏共享几何与动画在 `PhotoViewer.Shared/FilmstripGeometry.cs`：`FilmstripGeometry`（外框 `CardWidth`/`CardHeight`、内边距、居中偏移 `CenterOffset`）与 `FilmstripAnimation`（350 ms 三次缓出 `EaseOutCubic`）。卡片内层占位面 90×138 见 `MacThumbnailItem.InitializeView`、`iOSThumbnailCard`、`WindowsLayout.CardWidth` 等（转接 `film.card.*` 令牌）。

- **FILM-01、FILM-02、FILM-03**：定位请求与动画入口。
  - macOS：`PhotoViewer.Mac/Native/MacMainWindow.cs` 的 `Filmstrip`（`NSCollectionView`）、`PhotoViewer.Mac/Native/MacFilmstripView.cs` 的 `MacFilmstripLayout`；用户接管与减少动态效果在同一视图。
  - iOS：`PhotoViewer.iOS/Native/iOSFilmstripView.cs` 的 `iOSFilmstripView`（`UICollectionView` 宿主）、`iOSFilmstripLayout`、`ScrollToCurrent`/`AnimateTo`（350 ms 定位）。
  - Windows：`PhotoViewer.Windows/Native/WindowsFilmstripView.cs` 的 `WindowsFilmstripView`（`Grid` 宿主）、`CreateRepeater`（`ItemsRepeater` + `StackLayout`）、`ScrollToCurrentAnimated`（`LocateAnimationMilliseconds` 与三次缓出）。
  - Android：`PhotoViewer.Android/Native/AndroidFilmstripView.cs` 的 `AndroidFilmstripView`（`RecyclerView`）、`ScrollToCurrent`（定位与缓动）。
- **FILM-04**：方向与滚轮。行/列方向由各宿主切换；普通纵向滚轮在横向列表上滚列表，见各平台滚轮入口（macOS `PhotoViewer.Mac/Native/MacMainWindow.cs` 的 `ScrollWheel`、iOS 的 `UIScrollView` 委托、Windows `PhotoViewer.Windows/Native/WindowsMainWindow.cs`、Android `PhotoViewer.Android/Native/AndroidNativeRootView.cs`）。
  - **FILM-04@mac / @ios**：重新显示胶片栏、切换列表方向时显式定位当前项。
  - **FILM-04@ios**：手机竖屏胶片条左右贯通窗口，两端各留一份 `shell.phone.rowInset`（8）作卡片停靠线，卡片外框到相邻内容/背景只取这一份内边距、不叠加栏位与列表两层（卡片自身另有 `film.card.small.inset` 2 点内缩，视觉间距不是严格 8）；滚动指示器由系统按默认位置绘制。iPad 左右布局的胶片条纵贯窗口上下缘，前导内容留白 = 筛选条主轴长度，首张停在筛选带下方、滚动时从筛选按钮下经过（筛选条整体登记为滚动边缘元素盖住边缘），不按按钮下缘裁切列表。
- **FILM-04@web**：浏览器胶片栏为原生 `overflow` 滚动 + 自绘悬浮滚动条，横向列表把鼠标纵向滚轮映射为横向滚动。
- **FILM-07@android**：宽形态列布局与手机横屏的胶片条同样从入口合体下方滚过；Android 没有可挂到浮层上的公开系统滚动边缘效果，不模拟。
- **FILM-07@mac**：列布局胶片栏同样从筛选组下方滚过，但 AppKit 没有可挂到 `NSScrollView` 浮层上的公开滚动边缘效果，筛选组只作浮层，不以截屏或自绘模糊模拟。
- **FILM-05**：卡片内容与命中。
  - macOS：`PhotoViewer.Mac/Native/MacThumbnailItem.cs` 的 `MacThumbnailItem`（`NSCollectionViewItem`；`InitializeView` 落几何、星级、徽标）。
  - iOS：`PhotoViewer.iOS/Native/iOSThumbnailCard.cs` 的 `iOSThumbnailCard`（两档几何取共享 `PhotoViewer.Shared/FilmCardGeometry.cs`；宽形态固定大档，手机形态由根控制器按胶片栏主轴长度选档）。
  - Windows：`PhotoViewer.Windows/Native/WindowsThumbnailCard.cs`（`_name` 文件名、`_secondLine` 第二行与相似度条），`WindowsLayout` 转接共享 `FilmstripGeometry` 与 `film.*` / `color.*` 令牌。
  - Android：`PhotoViewer.Android/Native/AndroidThumbnailCard.cs` 的 `OnDraw`（圆角缩略图、第二行）与 `ShowCardMenu`（卡片菜单）。。
  - **FILM-05@ios**：大档星排是纯显示视图，另挂只收间接指针（`AllowedTouchTypes = IndirectPointer`）且只在星排矩形内接收的点按识别器，手指落在星排上由整卡选择按钮接管；长按（`motion.rating.longPress`）与辅助点按（`ButtonMaskRequired = Secondary`）打开星级弹窗，长按可不抬手滑到弹窗星排提交（与紧凑态共用 `iOSRatingPopup.BeginExternal` 转发路径）。卡片不再挂 `UIContextMenuInteraction`。
  - **FILM-05@android**：两档复用 `FilmCardGeometry`，小档没有逐星命中。大档普通星槽仅鼠标点按写回，触屏点星行只选卡；警示三角只打开弹窗。长按与辅助点按共用星级弹窗，不切图、不抬升卡片。
- **FILM-06**：评分与筛选连续性由共享 `PhotoViewer/ViewModels/Main/File/ThumbnailListViewModel.cs`（评分回填、暂留与筛选刷新）与各端评分写回路径承担；写回作用于组的全部实际来源（RATING-01/03），来源冲突的指示与浮窗见 [rating](#rating)（RATING-04）；写回失败一律走 [state](#state) 的 4 s 非模态提示条（macOS `PhotoViewer.Mac/Native/MacStatusToast.cs`、iOS `PhotoViewer.iOS/Native/iOSStatusToast.cs`）。
- **追溯**：旧壳定位/动画的冻结几何与截图见 `Design/baseline/avalonia/`（旧视图源码只存在于 tag `avalonia-final` 之前的历史）；现行触发与定位逻辑在共享 `PhotoViewer/ViewModels/Main/File/ThumbnailListViewModel.cs`、`PhotoViewer/ViewModels/Main/ControlViewModel.cs`、`PhotoViewer/ViewModels/Main/FolderViewModel.cs`；iPad 侧在 `PhotoViewer.iOS/Native/iOSNativeRootViewController.cs`、`PhotoViewer.iOS/Native/iOSPhotoView.cs`、`PhotoViewer.iOS/Native/iOSFilmstripView.cs`。

## rating

- **RATING-01、RATING-03**：来源枚举与全来源事务在共享 `PhotoViewer/Core/Exif/RatingService.cs`（按 `PhotoGroup.Id` 串行、预检、固定顺序写、逆序补偿、写后重新枚举来源集合并逐来源回读与结果枚举）；来源级读取状态（缺失 / 已评分 / 已拒绝 / 无效 / 冲突 / 不可读）在 `PhotoViewer/Core/Exif/XmpRatingReader.cs`，组级冲突判定在 `PhotoViewer/Core/Image/PhotoGroupMap.cs` 的 `PhotoGroup.HasRatingConflict`，列表与筛选投影在 `PhotoViewer/ViewModels/Main/File/PhotoListProjection.cs` 的 `MatchesRating`。
- **RATING-02、RATING-06**：评分写入只有一条正常路径：`PhotoViewer/Core/Exif/ContainerRatingStore.cs` 负责预检（以 ReadWrite 打开现有文件、在包偏移定位读取，验证真实可定位写能力且不写不截断）、revision 与整包/区间复核、提交和写后回读，`NoChange` 提交同样复核 revision 与目标值；包内 `xap:Rating` 的结构定位与等长改写由 `PhotoViewer/Core/Exif/XmpRatingSpanLocator.cs` 完成（值替换或按结构插入，近端空白吸收；远端 padding 移位、受控追加、整文件替换不支持）。容器适配器只承担 canonical 包的只读定位：`JpegXmpContainerAdapter`、`HeifXmpContainerAdapter`、`TiffXmpContainerAdapter`、`WebpXmpContainerAdapter`、`XmpFileContainerAdapter`（`<stem>.xmp` 本体与已有 sidecar）。按 RATING-02 拒绝与其它段、项、extent 或 IFD 条目重叠或共享的写入区间；无 canonical XMP 包（含 JPEG 无 APP1）时不新建或插入容器结构，按 `StorageError.Unsupported` 失败；已有包但缺 `xap:Rating` 时只在包结构唯一且空白足够时于包内插入。PNG 写入不支持（含标准 `iTXt`，需要维护容器级 CRC 事务）。来源枚举由 `PhotoGroupMap.RatingSources` 汇总，`XmpRatingStoreFactory` 为每个实际来源选择适配器；没有创建 sidecar 的入口。
  - **RATING-06@mac / @win / @ios**：只做等长与 Rating 附近局部空白吸收的原位写；远端 padding 移位、受控追加与整文件替换不支持。
  - **RATING-06@android**：SAF 目标先核对可定位原位写能力；不可定位句柄、云 Provider 或需要远端移位/扩容的写入在首次修改前失败，不做原位假成功。
- **RATING-04**：全局规则见 [spec/rating.md](spec/rating.md)。共享来源状态与整组写入由 `PhotoViewer/ViewModels/Main/RatingSourcesViewModel.cs` 提供（`SetRatingAsync`，成功且冲突解除才收起）；浮窗宽度、条目单行/双行排版、展开方向、夹取与文件名中间省略由共享 `PhotoViewer.Shared/RatingSourcesLayout.cs` 计算（Web 镜像 `PhotoViewer.Web/src/core/layout/ratingpopover.js`），各端只提供文本测量、参照矩形与原生宿主。下面只记录宿主、材质与字形差异。
  - **RATING-04@mac**：`NSPopover`（`PhotoViewer.Mac/Native/MacRatingSourcesPopover.cs`）提供原生毛玻璃、圆角与箭头，内容区不自绘背景或描边；标签底与分隔线用 `NSBox`，文本用 `NSTextField`（测量另加控件内边距），星按钮复用控制栏 `ToolbarButton`，采用系统圆形悬停，星形以 hierarchicalColor 着色。卡片方向的上下余量按屏幕可见区计算。每个主窗口复用唯一 popover/controller/content，内容尺寸只写 `ContentSize`；实例保留到窗口终结以兼容 AppKit 迟后访问，旧 `DidClose` 不得影响新展示。
  - **RATING-04@ios**：不再有独立来源浮窗，冲突来源以星级弹窗的来源条目显示（RATING-07@ios）；条目排版取共享 `PhotoViewer.Shared/RatingPopupLayout.cs`，状态与整组写入仍由 `RatingSourcesViewModel` 提供；名称、时间与状态宽取字体实测加设计内边距（`popup.ratingSources.*`），不另加经验边距。
  - **RATING-04@mac / @ios**：警示三角取 SF Symbols `exclamationmark.triangle.fill`，语义同 `rating.warning`。
  - **RATING-04@win**：警示三角取 `Design/icons.json` 中 `rating.warning` 对应的共享 Fluent 字形。来源浮窗 `PhotoViewer.Windows/Native/WindowsRatingSourcesFlyout.cs`：原生 `Flyout` 继承默认 Presenter 样式，与右键菜单一样以窗口化弹层承载 `DesktopAcrylicBackdrop` 与透明背景（约束在根内容内时系统材质只剩半透明），外部点击与 Esc 由原生关闭，不自绘箭头或底色。窗口化弹层会按显示器工作区自行夹取或翻转，因此共享算出的位置先夹入「窗口安全区 ∩ 所在显示器工作区」的可见交集（容不下时退回窗口安全区）再交 `ShowAt`，避免主窗口部分越出工作区时发生二次翻转。定位所用外形尺寸须按当前 DPI 对内容及四侧原生边框分别做物理像素取整，再换回 DIP；不能把未取整的边框厚度直接加到内容上，否则贴工作区边缘时一像素溢出也会触发整块翻转。星排复用控制栏按钮规格与 `WindowsControlStyles`。
  - **RATING-04@android**：`PhotoViewer.Android/Native/AndroidRatingPopup.cs` 以根视图内的同一个玻璃面板承载普通评分和冲突来源，复用 `RatingPopupLayout`，按实际字号扩展行带，超高可滚；锚定整卡或胶囊，按 `PopupDirection`、`PlacePopup` 与实际安全区落位。面板与主图菜单、筛选下拉共用 `AndroidGlassPopupLayer`，从屏幕层最终画面取样，按 `popup.glass.android.*` 应用色彩、模糊、折射、表面色与 Plain 高光；全窗透明拦截保证点外关闭不穿透，背景对读屏隐藏，关闭后键盘焦点归还入口；触摸关闭不显示焦点描边。外部点按、返回或 Esc 收起并同步共享状态；入口隐藏、移动或换绑即关闭。
  - **RATING-04@web**：自绘组件 `PhotoViewer.Web/src/shell/ratingsources.js`，警示三角用 `icon.warning`；写入走 `core/app.js` 的整组路径。
- **RATING-07**：共享 `PhotoViewer.Shared/RatingControlGeometry.cs`（形态判定、格子映射、紧凑星簇、方向表与落位）、`RatingGesture.cs`（一次手势至多一次提交的状态机）、`RatingPopupLayout.cs`（弹窗步长与条目排版）；宽形态控制栏的分组尺寸与换行取 `ToolbarFlowLayout.cs`。
  - **RATING-07@android**：`AndroidRatingControl` 保持冻结主屏几何，展开态逐格原生状态层并用 `RatingGesture` 预览、松手提交；紧凑态整胶囊打开弹窗。非冲突时入口延续同一触摸，按共享取消带预览，不落盘，松手提交；冲突条目只接受明确点选。弹窗星级按钮优先使用 Android 建议的 48dp 命中尺寸，空间不足按可用主轴等分；竖排入口的普通弹窗保留竖排星列。真实锚点继续承担触摸、读屏与焦点，玻璃绘制层按按钮配方绘制胶囊；API 33+ 有折射、高光和弹簧，星级只放大与显示跟手高光，不位移不拉伸，API 31/32 无折射与着色器高光，API 24–30 使用正式色层。
  - **RATING-07@ios**：控件 `PhotoViewer.iOS/Native/iOSRatingControl.cs`（两种形态都是交互式 `UIGlassEffect` 胶囊，展开态星排与紧凑星簇 `iOSRatingCluster.cs` 都放在胶囊 `ContentView` 里随按压缩放；紧凑星簇自绘时用上层字形的实心轮廓以 `DestinationOut` 分圈擦除下层，形成半透明羽化的叠压）与星排 `iOSRatingRow.cs`，手势用零时长 `UILongPressGestureRecognizer`（`CancelsTouchesInView = false`，在控制栏滚动容器里拖星不触发滚动），逐格 `UIHoverStyle` 系统正圆高亮，读屏 `Adjustable`；触感由每个星排实例各自持有选择生成器并在该星排复用，提交用系统按发起视图给出的轻冲击生成器。弹窗 `iOSRatingPopup.cs` 是根视图上的 `UIGlassEffect` 面板加全窗透明拦截层，不用 `UIPopoverPresentationController`：系统浮窗展示期间无法把起手触摸延续到弹窗星排。弹窗材质视图的 alpha 恒为 1（改 alpha 会让系统材质失效），出入场以 `Effect` 属性过渡配合内容视图淡入表达；减少动态效果时不缩放，只保留淡入淡出。
- **RATING-05**：星级筛选投影在 `PhotoViewer/ViewModels/Main/File/PhotoListProjection.cs` 的 `MatchesRating`，筛选项在 `PhotoViewer/ViewModels/Main/File/FilterBarViewModel.cs`；训练快照由独立仓 `Training/DatasetBuilder`（`FingerprintGrouper`）只读取实际选用的文件，sidecar 与伴侣不参与取 max，文件内部不是有效 1–5 时不落值。
- Web 参考壳没有真实文件写入；冲突指示、来源浮窗与筛选由 `?check=all` 校验界面语义（合成数据）。

## shell

- **SHELL-01**：分区与显隐。
  - macOS：`PhotoViewer.Mac/Native/MacMainWindow.cs` 的 `MacMainWindow`，宽形态分区取共享 `ShellPaneLayout.Partition`；主图铺满窗口，`MetalPhotoView.SetFitInsets` 分离视图与适应区。
  - iOS：`PhotoViewer.iOS/Native/iOSNativeRootViewController.cs`，共享 `ShellPaneLayout` + 真实安全区。
  - Windows：`PhotoViewer.Windows/Native/WindowsMainWindow.cs` 的 `LayoutShell`，宽形态分区取共享 `ShellPaneLayout.Partition`；`WindowsPhotoView` 铺满窗口，`SetFitInsets` 分离视图与适应区，栏位以绝对矩形浮在主图上。
  - Android：`PhotoViewer.Android/Native/AndroidNativeRootView.cs` + `AndroidMainScreenLayout`；insets 分别读取 `NavigationBars` / `TappableElement` 区分手势条与三键导航，横屏只忽略手势条底部，刘海与三键导航遮挡仍保留。
  - **SHELL-01@mac**：列布局筛选条前导 `shell.filter.leadingInset`（42）让开盖住文件栏的标题栏与交通灯（见 FILTER-07）。
  - **SHELL-01@ios**：iPad 左右布局的内容不避让顶部与底部系统安全区——胶片条纵贯窗口上下缘、卡片可从左上角筛选 / 更多浮动组下方滚过（筛选条整体登记为系统滚动边缘元素），右侧控制栏与分析栏同样纵贯；只有浮动筛选组按自身列位置的系统窗口控件带向下让位，不把这份让位套到胶片条或右侧栏。左列靠主图侧对齐：筛选组、胶片卡片外框与相似聚类开关共用同一条内容末端锚点（`FilmSideGeometry`），列宽由列表避让后的占用与入口占用取较大值。上下布局继续使用顶部安全区；所有横屏主屏均不扣底部手势条，竖屏底部安全区不变。
- **SHELL-02**：悬停标题栏、行布局窄宽度常驻栏与拖动区。常驻回退（筛选挖空与窗口控制器区间相交）、挖空与背景的判定在共享 `PhotoViewer.Shared/TitleBarLayout.cs`，各端只提供筛选可点控件与窗口控制器的实际水平范围。
  - macOS：`PhotoViewer.Mac/Native/MacMainWindow.cs` 窗口 `FullSizeContentView` + 隐藏系统标题文字，交通灯保持系统位置；顶边监视器驱动 `SetTitlebarVisible`，`Layout` 按 Zoom 按钮右缘与筛选组左缘判定是否回退，常驻时给共享分区结果统一加 32 点纵向偏移并排布全宽 `DragZone` / `TitlebarBackdrop`（`NSVisualEffectView`，`Titlebar` 材质、窗口内混合、命中透传）与标题标签（`titleBarFont`，按 Zoom 按钮右缘与中线定位）；`HandleTitlebarDoubleClick` 按 `AppleActionOnDoubleClick` 分派。
  - Windows：`PhotoViewer.Windows/Native/WindowsTitleBar.cs`（标题文字、`AcrylicInAppFillColorDefaultBrush` 背景、四个 46×32 自绘窗口按钮与状态字形）+ `WindowsMainWindow.cs` 的 `ConfigureTitleBar`（`SetBorderAndTitleBar(true, false)`）、`RefreshTitleBarVisibility`（内容区与非客户区指针事件、拖动循环）、`UpdateTitleBarChrome`/`BuildCaptionRects`（筛选组与按钮相交判定、行宿主顶部让位、跨度、背景、筛选挖空与 `InputNonClientPointerSource` caption 登记）。隐藏或全屏时登记一条零面积 caption：清空区域会让 WinAppSDK 退回整幅宽默认拖动带。动态标题同时写 `Window.Title` 与标题栏文字。`icon.ico` 随普通输出复制并随单文件解包，激活后由 `AppWindow.SetIcon` 设置为真实窗口图标，供任务栏悬停浮窗读取。
  - **SHELL-02@win**：自绘最大化按钮不提供系统贴靠布局浮层。窗口按钮悬停/按下底色取 `SubtleFillColorSecondaryBrush` / `SubtleFillColorTertiaryBrush`，Segoe Fluent 字形：最大化 `E922` ↔ 还原 `E923`、全屏 `E740` ↔ 退出全屏 `E73F`、最小化 `E921`、关闭 `E8BB`。标题栏背景在窗口内容（含文件栏、工具区及控件）之上，在标题文字和窗口按钮之下；列布局覆盖文件栏，行布局宽窗的无背景挖空和窄窗的常驻让位保持原规则。`SwapChainPanel` 不进入 WinUI 的 `CompositionVisualSurface` 取样，因此在 GPU 内复制视口顶部所需条带（照片和已合成的栏底）加入窗口内容的可视化取样，再对合成结果做高斯模糊，叠加独立校准的中性灰亮度层和 `AcrylicInAppFillColorDefaultBrush`（深色）的运行时色调层。不读屏、不创建独立窗口；系统合成路径不可用时略过 XAML 前景取样，回退到交换链内模糊并保留标题栏独立亮度。
  - **SHELL-02@mac**：材质为 `NSVisualEffectView`（`Titlebar` 材质、窗口内混合、深色外观），标题文字 `titleBarFont`。

  **SHELL-02 标题栏校准锚点（sRGB）**：取满窗纯色照片上方、没有其他控件或栏位影响的空白带内区域。Windows 只调标题栏的亮度层（`shell.titlebar.acrylic.luminosityTint`、`shell.titlebar.acrylic.luminosityOpacity`），不改 SHELL-12@win 的栏底或按钮配方。Windows 实测取自 SDR 窗口合成画面。

  | 满窗照片 | macOS 实测 | Windows 中性灰目标 | Windows 实测 |
  |---|---|---|---|---|
  | `#000000` | `#303232` | `#323232` | `#333333` |
  | `#808080` | `#4A4C4C` | `#4C4C4C` | `#4C4C4C` |
  | `#FFFFFF` | `#646565` | `#656565` | `#656565` |

  - iOS：根控制器隐藏状态栏（`PrefersStatusBarHidden`），窗口级入口由主图长按菜单承担。
  - Android：`Theme.PhotoViewer.Native` 固定深色、edge-to-edge + insets，无标题栏双击语义；横屏资源主题从启动画面起隐藏状态栏，`AndroidSystemBars` 在 Bootstrap 与原生 Activity 中按方向隐藏/恢复，根视图继续按真实 insets 排布。
  - **SHELL-02@ios / @android**：无窗口装饰与标题栏双击语义，属平台差异。
- **SHELL-03**：全屏与尺寸。macOS `MacMainWindow` 的 `IsFullScreen` / `DidEnterFullScreen` / `SaveFullscreen`，Windows `OverlappedPresenter` 四态，iOS/Android 无应用内全屏入口。
  - **SHELL-03@ios / @android**：不提供应用内全屏入口。
- **SHELL-04**：持久化。四端栏位显隐都写平台偏好存储（键名 `PhotoViewer.Native.ShowFilm` / `ShowControls`），实现手机形态的平台另有手机形态 EXIF 开关（`PhotoViewer.Native.ShowExif`，默认开）；分析栏为会话态；窗口帧 macOS 用 `FrameAutosaveName`，iOS 用 `RestoreWindowPreferences`。
  - **SHELL-04@mac / @ios**：偏好存储承载栏位显隐与窗口帧；Windows 走 `PhotoViewer.Windows/Native/WindowsNativePreferences.cs`、Android 走 `PhotoViewer.Android/Native/AndroidShellPreferences.cs`，键名与 macOS/iOS 相同。
- **SHELL-05**：跨屏与显示变化。macOS `DidChangeBackingProperties`；iPad 旋转/分屏重建滚动基准；Windows `XamlRoot`/DPI 变化；Android `OnSizeChanged`/`SurfaceChanged`。
- **SHELL-06**：窗口级输入边界。macOS 系统交通灯；Windows 系统标题栏与窗口按钮；触屏由长按菜单承担。
- **SHELL-07、SHELL-08、SHELL-09、SHELL-11**：形态判定与手机槽位。共享 `PhotoViewer/Core/Settings/ScreenFormResolver.cs`（判定表）、`PhotoViewer.Shared/PhoneScreenLayout.cs`（竖横屏槽位与避让矩形）、`PhotoViewer.Shared/FilmSideGeometry.cs`（刘海侧列与宽文件栏的内容末端锚点、列宽与列表 X）、`PhotoViewer.Shared/ToolRegionGeometry.cs`（分析与控制工具区的边距、接缝、列宽与居中起点）、`PhotoViewer.Shared/PhotoViewport.cs` 的 `SetFitInsets`（视图矩形与适应矩形分离）；iOS 侧在 `PhotoViewer.iOS/Native/iOSNativeRootViewController.cs` 的手机槽位布局与旋转/分屏响应。
  - **SHELL-07@ios**：尺寸类别取 `UITraitCollection.HorizontalSizeClass` / `VerticalSizeClass`，设备取 `UserInterfaceIdiom == Phone`，与用户布局模式一并交给共享判定函数；横屏的刘海侧按界面方向判定（`LandscapeRight` 为刘海在左），其他方向镜像。iPhone 的布局位置分组只列并勾选「手机」（SETTINGS-08@ios）。
  - **SHELL-07@android**：当前窗口尺寸由 `WindowMetricsCalculator` 读取，两轴尺寸类别由 AndroidX `WindowSizeClass.Compute` 给出，设备类型按 `smallestScreenWidthDp < 600` 判定，再交共享 `ScreenFormResolver`；实现位于 `PhotoViewer.Android/Native/AndroidScreenForm.cs`。横屏底部只忽略手势导航条，三键导航与刘海仍按真实遮挡保留。
- **SHELL-09@ios**：刘海侧几何取共享 `FilmSideGeometry`，控制几何取 `ToolRegionGeometry`，宿主为根控制器；手机横屏与 iPad 左右布局共用同一套规则，刘海在右时整体镜像，入口与上下张登记为滚动边缘元素。规则见 [spec/shell.md](spec/shell.md)（SHELL-09），探针见 SHELL-10@ios。
- **SHELL-10@ios**：贴角带在元素实际位置探测。竖屏顶行按共享 `PhoneScreenLayout.TopRowY` 给出的行位置、以左右两半各一块等大的不可见探针读取该半的左右适应值；竖屏底行在底行位置整行探测左右两侧；横屏刘海侧列取筛选/更多入口合体的实际范围（`FilmEntryBandFrame`：88 宽 × 全高），下巴侧控制栏取控制内容列（`ControlBandFrame`：44 宽 × 全高），`LeadStripWidth` 只作刘海侧列宽。带圆角适应安全区接口按视图自身位置计算，每条带取自身方向的适应值，其他方向交给系统安全区。横屏下缘由专用探针读取 iOS 26 `marginsLayoutRegionWithCornerAdaptation:`：显式 margins 为零、不继承祖先 margins、`InsetsLayoutMarginsFromSafeArea=false`，保留独立圆角适应。
- **SHELL-12@ios**：栏背景用 `UIVisualEffectView` + `UIBlurEffectStyle.SystemMaterialDark`；圆按钮用 `GlassButtonConfiguration`（`iOSGlassButtons.Round`）；合体按钮是单个交互式 `UIGlassEffect` 胶囊内的无底色分段，按钮通过 `PointerStyleProvider` 使用预览容器坐标系内缩 4 点的系统高亮效果（`iOSGlassButtonGroup`）；手机形态的全部玻璃控件（含星级控件胶囊）同在一个铺满舞台、自身不拦截触摸的 `UIGlassContainerEffect` 容器内（`iOSGlassContainer`，`Spacing` 取 `shell.glass.container.spacing`），宽形态控制栏的星级控件与命令圆按钮直接平级放在文档内的另一个同类容器内容区（命令组只保留无交互的逻辑矩形，不夹在玻璃层级之间）；重建保留同名按钮及评分实例，布局按未变换 bounds/center 比对，尺寸未变不写交互几何，EXIF 块都留在容器外；横屏两组按钮用 `UIScrollEdgeElementContainerInteraction` 登记为滚动边缘元素，iPad 左右布局把筛选条整体登记为顶端边缘元素，胶片栏从其下方滚过时由系统出模糊。宽形态上下/左右布局与手机横屏的工具区都用单个 `toolBackground` 宿主承载，显示条件见 SHELL-12。iPhone 横屏与 iPad 左右布局的控制栏都用无衬底 EXIF 变体，无衬底变体自身不铺模糊材质。定制组件限于 RATING-07 控件、EXIF 块、卡片与筛选控件，其余不手调圆角、阴影、模糊半径或着色。
- **SHELL-02@web**：浏览器标签页没有窗口装饰、隐藏标题栏、拖动区与标题栏双击；`?platform=mac` 配置档只用于与 macOS 几何比对。
- **SHELL-03@web**：浏览器视口即窗口，允许小于 `shell.window.min.*`，布局按实际尺寸计算（主图至少 1 点）；不提供应用内全屏入口。
- **SHELL-04@web**：栏位显隐与设置文档写 `localStorage`（键名同四端偏好存储）。
- **SHELL-07@web**：参考壳不读取 User-Agent 或推断设备类型。自动模式把 CSS 视口宽 `<600` 作为 iPad 紧凑分屏的横向紧凑映射，`>=600` 为常规；不模拟垂直紧凑。显式行/列仍为宽形态，手机模式用于规范检验。除该视口映射外，形态与方向仍按 SHELL-07 的同一判定表。
- **SHELL-10@web**：系统安全区只取浏览器公开的 `env(safe-area-inset-*)`；没有逐元素带圆角适应或窗口控件探针，也不伪造这些输入。横屏刘海侧由左右安全区较大的一侧决定，相等时按左侧；演示与检查所需的其他带值只能作为显式几何输入注入，不代表浏览器探测能力。
- **SHELL-12@mac**：文件栏与连续工具区由 AppKit `NSView.backgroundFilters` 窗口内高斯背景模糊（`shell.bar.blur.radius@macos`）与 `color.bar.overlay@macos` × `shell.bar.overlay.opacity@macos` 的独立深灰色层承载；EXIF 底部横排使用无标题深色 `NSBox` 原生分组，右侧竖排使用透明无衬底容器，块内边距只取共享几何，不复刻 iOS 光学效果。按钮的 `NSGlassEffectView` 按 `shell.glass.button.tint.opacity@macos` 染白；macOS 26 的原生按钮玻璃没有按压交互与融合，悬停描边、按下与焦点由玻璃内容区里的原生 `NSButton` 承担。
- **SHELL-12@win**：主图视图铺满窗口，文件栏（含分隔线）与连续工具区使用与标题栏同源的 WinUI 应用内亚克力（`AcrylicInAppFillColorDefaultBrush` 的运行时配方），仅把亮度层不透明度限制在 `shell.bar.acrylic.luminosityOpacityMax` 以内。因 `SwapChainPanel` 不能被 XAML 亚克力取样，由主图交换链按区段绘制（`WindowsPhotoView.SetBackdropRegions`），栏位 XAML 只铺透明命中底；工具区只在有分析栏或实际 EXIF 时出背景并在朝主图一侧画一条 `color.divider`，只剩评分与命令时透明，星级胶囊与命令圆按钮各自得到圆角亚克力衬底（WinUI 原生控件放在亚克力上时的材质层次，替代 macOS 玻璃），其上铺 TOOLBAR-10@win 的按钮填充，保留原生状态。不做玻璃融合与按压缩放（无原生对应）。
- **SHELL-10@android**：API 28 起窗口使用官方 `LayoutInDisplayCutoutMode.ShortEdges`，栏底与主图延伸到刘海短边，交互内容仍按安全区避让；系统安全区读取公开 `WindowInsets` 与 `DisplayCutout`；圆角避让读取 `RoundedCorner` 半径，先只按安全区排出贴角元素，再按元素外形（44 高胶囊与圆按钮取半径 22 的半圆端）在其实际位置求端头圆恰好内切屏幕圆弧时的避让线（`AndroidWindowInsets.CornerInset`），横屏刘海侧列取入口合体离刘海侧窗口缘的实际距离、控制列取一份工具区内边距；读不到时为零，不按设备型号拟合。
- **SHELL-12@android**：主图在所有 API 级别统一由 `TextureView` 承载，取样层级为根视图 → 屏幕层 `LayerBackdropLayout` → 场景层 `LayerBackdropLayout` → 照片层 `LayerBackdropLayout`。6 个栏底与带衬底 EXIF 由 `AndroidPhotoMaterial` 从照片层取样，配方为 vibrancy 1.5 →（API 33+ 栏底 gamma 0.642）→ blur 80dp → 既有色层，栏底在自身边界 Clamp；玻璃控件层在屏幕层内、场景层之后，只从场景层取样；窗口内弹层在屏幕层之外，从包含玻璃按钮的屏幕层最终画面取样。每个材质消费者独立持有 `BackdropEffectScope` 和局部效果层，按视图尺寸录制并复用，离屏估算并入 `AndroidMemoryBudgetProvider.ExternalFootprint`。API 33+ 有完整 G2 形状、折射、高光与弹簧，API 31/32 只有色彩、模糊与表面色，API 24–30 不录制、不取样，使用正式色层。按钮、合体、星级胶囊和 AI 相似开关的真实 Material 锚点继续承担布局、触摸、滚动、读屏与焦点，`AndroidGlassControlLayer` 按真实可见矩形绘制 vibrancy → blur 2dp → lens 12/24dp、G2 胶囊、表面色、默认高光/阴影与按压交互；星级只放大不位移不拉伸。主图菜单、筛选下拉与星级弹窗共用 `AndroidGlassPopupLayer`，按 `popup.glass.android.*` 绘制玻璃面板，点外不穿透、读屏模态与键盘焦点归还；触摸不切换输入模式、不在入口绘制焦点环。产品路径不截屏、不用 `PixelCopy`、不做离屏拷贝模拟模糊；竖屏保留系统状态栏，顶行不得覆盖状态栏图标。
- **SHELL-12@web**：栏位与按钮分别用 CSS 原生 `backdrop-filter` 与半透明色层（栏底深灰、按钮纯白，取 Web 令牌；mac 配置档仍只切换几何），筛选胶囊、相似聚类开关、评分胶囊与命令圆按钮共用同一按钮材质；浏览器没有原生玻璃共享取样、融合或滚动边缘材质，不模拟这些光学效果。几何、背景出现条件与唯一外边界仍按 SHELL-12。

**SHELL-12 校准锚点**：栏底与栏上按钮的当前参数及观测读数。使用独立色层的平台，参数取 `color.bar.overlay` × `shell.bar.overlay.opacity`（栏底）与 `color.toolbar.button.tint` × `shell.glass.button.tint.opacity`（栏上按钮）；读数按纯黑 `#000000` / 50% 灰 `#808080` / 纯白 `#FFFFFF` 三种满窗衬底的次序，每通道 sRGB 8 位。

| 平台 | 参数（栏底色 × 不透明度 ＋ 按钮白层） | 实测栏底 | 实测筛选胶囊 |
|---|---|---|---|
| iOS（基准） | 系统深色模糊 ＋ 未染色的原生玻璃按钮 | `#1E1E1E` / `#404040` / `#535353` | `#3D3D3D` / `#595959` / `#686868` |
| macOS | `#2B2B2B` × 0.80 ＋ 白 0.195 | `#212121` / `#3C3C3C` / `#565656` | `#464646` / `#505050` / `#636363` |
| Web | `#292929` × 0.80 ＋ 白 0.135 | `#1F1F1F` / `#3A3A3A` / `#565656` | `#3F3F3F` / `#565656` / `#6D6D6D` |
| Android | `#262626` × 0.7922 ＋ 白 0.132；API 33+ 照片层 vibrancy 1.5 → gamma 0.642 → blur 80dp，API 31/32 线性回退，API 24–30 不取样 | `#1E1E1E` / 64.3 / `#535353` | `#3C3C3C` / 89.9 / `#6A6A6A` |
| Windows | WinUI 应用内亚克力，亮度层上限 0.80；常态按钮白层 `#1FFFFFFF`，保留 Fluent 状态 | `#252525` / `#3E3E3E` / `#575757` | `#404040` / `#555555` / `#6B6B6B` |

对照基准是 iPad mini（A17 Pro）、iPadOS 27.2.1 的产品截图，为当前锚点而非系统材质常量，随系统版本与显示环境变化；辅助样本为星级胶囊 `#3C3C3C` / `#585858` / `#676767` 与命令圆钮 `#3C3C3C` / `#585858` / `#666666`（`targets-ios27.json`）。

`tokens.json` 仍是参数权威，本表记录当前参数与观测，不是像素合规的硬阈值。Windows 读数取 SDR 下的窗口合成画面；采样口径见 [spec/shell.md](spec/shell.md)（SHELL-12）。上一轮跨平台对照见本机产物 `artifacts/acceptance/sidebar-button-ios27/comparison.png`（不入库）。

## filter

- **FILTER-01**：排序、方向与星级筛选；AI 版另含相似聚类开关。
  - macOS：`PhotoViewer.Mac/Native/MacMainWindow.cs` 按共享 `WideFilterLayout` 排布三段 `NSSegmentedControl` 或单胶囊，由 `NSGlassEffectView` 提供原生玻璃背景，段动作打开对应 `NSMenu`，相似开关使用原生玻璃 `FilterToggleButton`。
  - iOS：宽形态 `PhotoViewer.iOS/Native/iOSFilterControl.cs`（交互式 `UIGlassEffect` 胶囊内「筛选 / 更多」两段，与大档卡片同宽（96）、两段各 48；筛选段弹出 `iOSFilterMenu` 的三分组菜单，更多段挂根控制器注入的主图菜单；AI 版开关关闭/打开分别为玻璃与强调玻璃配置），手机形态为根控制器的筛选圆按钮；两处共用 `iOSFilterMenu`。
  - Windows：`PhotoViewer.Windows/Native/WindowsFilterBar.cs` 的 `WindowsFilterBar`：按共享 `WideFilterLayout` 排布三段或单胶囊；胶囊衬底取原生按钮材质（`ControlFillColorDefaultBrush` + `ControlElevationBorderBrush`），段为透明 Subtle 按钮、状态层按同心圆角内缩 3 点，段间 1 点 `DividerStrokeColorDefaultBrush`；各段经 `Button.Flyout` 打开原生 `MenuFlyout`（排序和方向用 `RadioMenuFlyoutItem`，星级用 `ToggleMenuFlyoutItem`），单胶囊为三个无标题分组；AI 版开关为默认样式 `ToggleButton`（44 高、半圆端）。
  - Android：`PhotoViewer.Android/Native/AndroidFilterBar.cs` 的 `AndroidFilterBar`（宽形态按共享 `MobileFilterLayout` 的「筛选 / 更多」96 合体，手机形态为 44 圆按钮或 88 合体；真实入口保留在 `AndroidPhoneChrome.cs` 的锚点视图中，玻璃材质由 `AndroidGlassControlLayer` 绘制，分组菜单承载在 `AndroidGlassPopupLayer` 的窗口内玻璃面板上）。
  - **FILTER-01@android**：宽形态与 iPad 同为「筛选 / 更多」96 合体（两段各 48、高 44），AI 版开关为可勾选玻璃锚点按钮，列布局 96×44、行布局取文字自然宽；手机竖屏为 44 圆入口，横屏为 88×44 合体，刘海在右时两段按槽位镜像。入口图标取 `icon.filter` / `icon.more`。开关未选中文字取 `color.text.primary`（与其他 Material 按钮一致），选中态取 `color.filter.toggle.checked.*`。
- **FILTER-02、FILTER-03、FILTER-05、FILTER-10**：共享 `PhotoViewer/ViewModels/Main/File/ThumbnailListViewModel.cs`（筛选、排序、当前项映射）；筛选项定义在 `PhotoViewer/ViewModels/Main/File/FilterBarViewModel.cs`，星级投影在 `PhotoViewer/ViewModels/Main/File/PhotoListProjection.cs` 的 `MatchesRating`。9 项取值、并集多选与已拒绝/冲突口径见 [rating](#rating)（RATING-01）与 [spec/filter.md](spec/filter.md)（FILTER-03）。
- **FILTER-04**：Windows 与 macOS 在星级入口内显示 `N张`，左侧为当前条件、右侧为计数；计数使用 `filter.count.fontSize` 与平台次级文字色，菜单不显示计数。iOS、Android 与 Web 在桌面试点阶段不显示计数。业务计数随列表投影同步。
- **FILTER-06、FILTER-09**：开关状态 `PhotoViewer/ViewModels/Settings/SettingsViewModel.cs` 的 `SimilarityPanelExpanded`（经 `IViewStateStore` 跨会话保留，不进入设置文档）；可见性 `SimilarityPanelViewModel.IsPanelVisible`。
- **FILTER-07**：移动端（iOS、Android）宽形态取共享 `PhotoViewer.Shared/MobileFilterLayout.cs`（筛选 / 更多与大档卡片同宽（96）、两段各 48；行布局整组含 AI 版开关按自然宽居中；列布局合体与 AI 版开关都靠主图侧的内容末端锚点，锚点与列宽取共享 `FilmSideGeometry`，开关 96 与卡片列同缘、隔 8 点）；macOS 与 Windows 宽形态取共享 `WideFilterLayout`（行布局三段 1:1:1、每段建议宽 120，列布局固定单胶囊），Web 按 `FILTER-07@web` 保留试点前形态。
  - **FILTER-07@win**：列布局首控件顶部让位取 `filter.margin`（10）：Windows 列布局标题栏不覆盖文件栏，不取 macOS 的 42 点让位；段自然宽按 `dropdown.fontSize` 实测最长选项名加两侧 `dropdown.paddingLeft`，星级段另计当前张数与 `filter.count.spacing`。
  - **FILTER-07@android**：宽形态逐项取 `MobileFilterLayout.Compute`；列布局首控件顶部让位取该列位置的系统栏遮挡加一份 `filter.margin`，整组自然宽超出安全内容区时才由滚动宿主横向承载。手机入口直接取 `PhoneScreenLayout` 的竖横屏槽位，横屏合体常驻并随刘海侧镜像。
- **FILTER-08**：四端与 Web 共用相同的选择语义；保留各端现有菜单排版、组序与系统交互。
  - **FILTER-08@mac**：使用 AppKit `NSMenu` 和 SF Symbols；原生单胶囊下拉只展示实际选项，不把入口标题列作选项。AppKit 无公开的逐段读屏值接口，完整星级条件与张数由分段控件的读屏值表达。标准菜单的点击会结束原生跟踪会话，AppKit 没有公开的项级保持展开属性；继续点选需重新打开，先前条件保留。
  - **FILTER-08@win**：分段和单胶囊都用 WinUI `MenuFlyout`，通过菜单项的 `Icon` 呈现 Fluent 字形。菜单项点选后由系统收起，没有公开的项级保持展开属性；继续点选需重新打开，先前条件保留。状态变化只更新既有菜单项及入口标签，不重建菜单。
  - **FILTER-08@ios**：系统 `UIButton` 上下文菜单与 `UIMenu`，使用 SF Symbols。中尺寸排序项、星级条件、方向在三个内联分组；`KeepsMenuPresented` 与 `UpdateVisibleMenu` 保持菜单展开并提交当前状态。切换已滚动菜单的星级条件时，系统可能把当前项移到可见区域中央，保留该原生行为。筛选与更多入口互斥。
  - **FILTER-08@android**：玻璃锚点入口、窗口内玻璃面板与真实菜单行；全部菜单与星级标记取 `Design/icons.json` 的共享 Fluent 映射（无星级为禁止符号，数字档为对应数字圆）。点选即生效并在原弹层更新勾选，入口失效时关闭；点面板外只关闭且不穿透，关闭后键盘焦点归还入口。更多段弹主图菜单（宽形态不含 EXIF 项）。
- **FILTER-07@web / FILTER-08@web**：桌面按三段合体或空间不足时用单胶囊；`platform=mac` 只覆盖列布局 42 点让位与标题栏几何。手机竖屏筛选与更多为两枚 44 点圆钮、横屏为 88 点合体。浏览器无原生菜单弹层，沿用 MENU-03 的 DOM 菜单组件，用 uicons 语义图标（「无星级」以禁止符号表达，其余用对应文件、相机、磁盘、箭头与数字等字形），原位更新筛选菜单项、保留焦点和滚动。

## image

- **IMAGE-01、IMAGE-03、IMAGE-05、IMAGE-08**：共享视口数学 `PhotoViewer.Shared/PhotoViewport.cs`；加载会话 `PhotoViewer/Core/Image/PhotoLoadSession.cs`；代理 overscan 由各壳下发给 `BitmapLoader`（代理目标长边 = 渲染表面物理像素 × `image.proxy.overscan`，见 `BitmapLoader.ProxyTargetLongEdge`）。
  - macOS：`PhotoViewer.Mac/Native/MetalPhotoView.cs` + `PhotoViewer.Apple/MetalPhotoRenderer.cs`。
  - iOS：`PhotoViewer.iOS/Native/iOSPhotoView.cs`（`UIScrollView` 为交互真源）。
  - Windows：`PhotoViewer.Windows/Native/WindowsPhotoView.cs`（`SwapChainPanel` + D3D11）。
  - Android：`PhotoViewer.Android/Native/AndroidPhotoView.cs`（所有 API 统一 `TextureView` + `SurfaceTexture` + EGL14/GLES）。
  - **IMAGE-01@android**：空首屏副文偏移取 24（行高 24 + 内边距 6）。照片使用 8 位 Display P3（设备与窗口支持广色域时）或 sRGB；EGL 优先 P3 passthrough，标准 P3 与显式 sRGB 表面按实际帧缓冲编码决定是否由着色器线性化，避免重复传递编码。解码目标在会话内稳定，表面回退或重建时按图像真实标签转换上传，不把 P3 码值直接交给 sRGB 表面。缩略图、分析位图与导航图出口统一显式 sRGB；导航图先抽样再转换，不为小图转换整幅照片。
- **IMAGE-02**：`PhotoViewport.ToggleFit` / `ApplyImage`。
  - **IMAGE-04@ios / @android**：触屏最小缩放为适应态，小图/RAW 预览夹回适应。
- **IMAGE-04**：缩放范围 `PhotoViewport.ZoomTo` / `ZoomBy` / `ClampPan`；预设与比例缩放入口为各端主屏的 `ZoomPreset`（`MacMainWindow`、`iOSNativeRootViewController`、`WindowsMainWindow`、`AndroidNativeRootView`）。
- **IMAGE-06**：导航图。macOS `PhotoViewer.Mac/Native/MacMainWindow.cs`（`NavigatorHostView`）+ `MetalPhotoView.CenterOnNormalizedPoint`；iOS `PhotoViewer.iOS/Native/iOSNativeRootViewController.cs` 的 `UpdateNavigationIndicator`/`OnNavigationPan`；Windows `PhotoViewer.Windows/Native/WindowsNavigator.cs`（`PressAt`/`DragTo`）+ `WindowsPhotoView.CenterOnNormalizedPoint`；Android `PhotoViewer.Android/Native/AndroidNavigationOverlay.cs`（`OnTouchEvent`）+ `AndroidPhotoView.CenterOnNormalizedPoint`。
  - 四端白框都可拖动定位（桌面鼠标 / 触屏手指）；拖框只改视口中心，滚轮与捏合仍归主图缩放松手。
  - **IMAGE-06@android**：点按 = 把该点直接移到视口中心（`AndroidNavigationOverlay` 的 Down/Move 同一路径）。
  - Android 导航缩略图取当前展示产物（代理/Full/增强），按显示朝向抽样，不再二次解码原文件（`AndroidNavigationDecoder.CreateFromDisplayAsync`）。
- **IMAGE-07**：增强状态 `PhotoViewer/Core/Image/PhotoLoadSession.cs`；药丸 `image.busy.*`、门槛 `image.busy.delay`（500ms）四端同一：macOS `MacMainWindow` 的 `_busyPill`、iOS `PhotoViewer.iOS/Native/iOSNativeRootViewController.cs` 的 `EnhanceBusyLabel`、Windows `PhotoViewer.Windows/Native/WindowsMainWindow.cs` 的 `_busyPill`、Android `PhotoViewer.Android/Native/AndroidNativeRootView.cs` 的药丸标签。
- **IMAGE-09**：定位入口 macOS `MacMainWindow.FocusDetailPoint`、iOS `iOSNativeRootViewController.FocusDetailPoint`、Windows `WindowsPhotoView.FocusOnImagePoint`、Android `AndroidPhotoView.FocusOnPoint`。
- **IMAGE-10**：空态/加载/失败提示块由各端主图区标签显示（macOS `MacMainWindow.OnImageChanged`、iOS `iOSNativeRootViewController.UpdateHint`、Windows `WindowsMainWindow.UpdateHint`、Android `AndroidNativeRootView` 的提示刷新）。首屏的字号、行盒、间距与三级基准色统一取 STATE-01；原生标签通过富文本或分段布局给「/」着色并留白，Web 使用 CSS。
- **IMAGE-11**：缩放/平移/适应/跟随只存会话；导航图开关与缩放预设走设置（`SettingsViewModel.ImagePreview.cs` 的 `ShowZoomIndicator` 与缩放预设），增强开关不持久化（`PhotoViewer/Core/Image/PhotoLoadSession.cs`）。

## analysis

- **ANALYSIS-01、ANALYSIS-05**：编排 `PhotoViewer/Core/AI/AnalysisPresenter.cs`；几何 `PhotoViewer.Shared/AnalysisPanelGeometry.cs`。
  - macOS：`PhotoViewer.Mac/Native/MacAnalysisPanel.cs`、`MacDetailPreview`、`MacDiagnosticTile`。
  - iOS：`PhotoViewer.iOS/Native/iOSAnalysisPanel.cs`、`iOSDiagnosticTile`。
  - Windows：`PhotoViewer.Windows/Native/WindowsAnalysisPanel.cs`（`Grid` + `IAnalysisHost`）、`WindowsAnalysisTilePanel`。
  - Android：`PhotoViewer.Android/Native/AndroidAnalysisPanel.cs`（自绘 `View`）。
  - **ANALYSIS-05@ios / @android**：分析栏参与真实安全区布局。iOS 手机竖屏的分析栏接在顶行之下：不画顶部分隔线，交叉轴只留一份 `shell.phone.rowInset`（瓦片边长 + 2 × 8），不叠加面板自身的 10 点内边距。iOS 手机横屏与 iPad 左右布局中，分析栏与控制栏同属一个工具区（SHELL-12）；控制栏隐藏时恢复窗口侧内边距。Android 的宽形态与手机竖横屏由同一共享几何映射到原生 View。
- **ANALYSIS-02**：瓦片外观。圆角只由外框一层裁切产生。
  - **ANALYSIS-02@windows**：内层圆角 0，单层裁切。
  - **ANALYSIS-02@android**：抖动拖影矢量线为简化绘制。
- **ANALYSIS-03**：等边长由 `AnalysisPanelGeometry` 或 Windows `WindowsAnalysisTilePanel` 的 Measure/Arrange 给出。
  - **ANALYSIS-03@android**：手机纵向按共享公式排 6–7 张，约 58dp；不为手机另设布局。
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

- **TOOLBAR-01、TOOLBAR-02**：四端控制栏都取共享 `PhotoViewer.Shared/ToolbarFlowLayout.cs`。
  - macOS：`PhotoViewer.Mac/Native/MacControlBar.cs`（`NSView`），分组尺寸、换行/换列与逐组矩形取 `ToolbarFlowLayout.Arrange`，置于 `NSScrollView`。
  - iOS：`PhotoViewer.iOS/Native/iOSToolbarView.cs`（`UIView`），分组尺寸与换行取共享 `ToolbarFlowLayout`（工具区外边距、接缝与交叉轴居中取 `ToolRegionGeometry`），置于 `UIScrollView`，`Arrange` 后写 `ContentSize` 并夹取偏移（`iOSNativeRootViewController.Layout`）。
  - Windows：`PhotoViewer.Windows/Native/WindowsControlBar.cs`（`Grid` → `ScrollViewer` → 文档面板），分组尺寸、换行/换列与逐组矩形取 `ToolbarFlowLayout.Arrange`，主窗口按 `MeasureDocument` 落控制栏主轴长度；`ScrollViewer`（`_scroll`）保存并夹取偏移。
  - Android：`PhotoViewer.Android/Native/AndroidControlBar.cs`（`FrameLayout`：命令为 44dp 圆形 `MaterialButton` 子视图，EXIF 块与星级控件为自绘定制子视图），逐组矩形取 `ToolbarFlowLayout.Arrange`，组内子控件在交叉轴居中，置于 `AndroidControlBarScrollHost`（`AndroidNativeRootView._controlScrollHost`）滚动。
- **TOOLBAR-03**：评分组。自绘控件禁用不换底色、只降内容强调；原生控件可用系统禁用外观但保持可辨、不近似隐藏（与 TOOLBAR-10 同一口径）；评分快捷命令统一画数字（0=叉）。冲突时 0 号换警示三角，字形、浮窗宿主与整组写入见 [rating](#rating)（RATING-04）。
  - **TOOLBAR-03@ios**：评分组就是 RATING-07 的 `iOSRatingControl`（宽形态控制栏、手机竖屏底行与横屏控制栏同一实例类型），不再按 30×30 按钮口径；横屏控制栏里它排在 EXIF 之下的剩余长度上，关闭「显示评分」时控件与槽位一并移除（横屏把长度让给 EXIF 并重新居中，竖屏其余按钮位置不变）。
  - **TOOLBAR-03@mac**：桌面横竖两向评分恒使用 `RatingControlView`（`NSGlassEffectView` Regular 玻璃半圆端胶囊）的 224 点展开态，内容区承载六个 36×44 原生圆形悬停 `NSButton` 槽位，空间不足由整组换列或滚动吸收；系统负责悬停、按下与焦点外观，星形以 hierarchicalColor 着色；几何与星色统一取共享 `RatingControlGeometry` 和 `color.star.*`。
- **TOOLBAR-04**：命令组。桌面 tooltip 与移动端命令读屏名称统一为 `命令名（主 / 次）`，四端 C# 壳共用 `PhotoViewer/Core/Input/CommandTooltipText.cs` 拼接，键名仍由各端输入映射格式化；Web 在 `src/core/input.js` 实现同一文案。INPUT-13 只控制设置中的键鼠配置可见性；移动端不另造长按浮层。
  - 图标语义统一取 `Design/icons.json`：Apple 使用 SF Symbols，Windows / Android 使用共享 Fluent 归一字体；Web 使用 `icon.font` / `icon.fontSolid` 与 `icon.*` 码位，由 `PhotoViewer.Web/tools/subset-icons.py` 生成 woff2 子集。
  - **TOOLBAR-04@mac / @ios**：命令图标与评分星用 SF Symbols（`PhotoViewer.Apple/AppleSymbolNames.cs`），语义取 `Design/icons.json`。评分快捷命令为 `star.fill` 内叠数字（`PhotoViewer.Mac/Native/MacControlBar.cs`、`PhotoViewer.iOS/Native/iOSGlassButtonGroup.cs` 的 `iOSRatingDigitLabel`；iOS 宽形态命令为 `GlassButtonConfiguration` 圆按钮，手机可选命令为 44 点原生按钮，共用交互式玻璃胶囊，Mac 命令为 `NSGlassEffectView` 承载的 `NSBezelStyle.Rounded` + `NSControlBorderShape.Circle` 圆钮，`ShowsBorderOnlyWhileMouseInside` 提供系统悬停边框），0 为 `xmark`。
  - **TOOLBAR-04@win**：命令与评分图标由 `PhotoViewer.Windows/Native/WindowsIcons.cs` 按 `Design/icons.json` 取共享 Fluent 字形；评分快捷命令为 `rating.starFilled` 内叠数字（0=清零叉）。tooltip 绑定经 `WindowsInput.FormatGesture` 显示可读键名（`D2`→`2`、`Oem3`→`` ` ``，并补 Oem1–Oem8 别名），不出现枚举名。
  - **TOOLBAR-04@android**：宽形态用 44dp 圆形 `MaterialButton`；手机可选命令在 Material 胶囊内各自使用 44dp 的独立按钮与原生状态层。`AndroidIcons` 按 `Design/icons.json` 提供共享 Fluent 字形，放在按钮图标槽；读屏名取 `CommandTooltipText`，不设 `TooltipText`，因为 Android 的 tooltip 在触摸长按时也会弹出浮层。
- **TOOLBAR-05**：EXIF 摘要。桌面 tooltip `标签 · 值`；触屏完整值由 TOOLBAR-06 的详情页承担。
  - **TOOLBAR-05@ios**：EXIF 块为 `PhotoViewer.iOS/Native/iOSExifBlock.cs`（`UIBlurEffectStyle.SystemThinMaterialDark` 衬底 + 令牌圆角），几何取共享 `PhotoViewer.Shared/ExifBlockGeometry.cs`；上下布局控制栏与手机竖屏顶行用衬底块，iOS 左右布局控制栏（iPad 宽形态与手机横屏）用 `Bare` 无衬底变体（`VisibleCount` 限定显示的整项数），透明容器最大为固有项宽 52 点（不超过按钮与两侧边距之和 64），与星级及按钮同轴，标签和值在容器内截断、不另加边距，四处同一实例类型。
  - **TOOLBAR-05@android**：EXIF 块为自绘定制组件（`AndroidPhoneChrome.cs`、`AndroidControlBar.cs`）。带衬底变体在 API 31 及以上取与栏底同一照片层的真模糊（同一 vibrancy 1.5 → blur 80dp，不应用栏底 gamma），再叠 `color.toolbar.block.background` × `shell.glass.android.exif.tint.opacity@android`，圆角由本视图画布 `ClipPath` 裁出；API 24–30 不取样，取 `color.toolbar.block.background` 的不透明正式色层。无衬底变体（宽形态左右布局、手机横屏）始终完全透明、不取样。标签 / 值按字体度量成对居中，无衬底变体与 iOS 同为 52 点透明容器。
  - **TOOLBAR-05@mac**：EXIF 块为 `MacControlBar.ExifBlockView`（无标题深色 `NSBox`）；底部横排保留原生分组外框，右侧竖排设 `Transparent=true`，使用无衬底、无块内边距的 52 点透明容器（不超过按钮与两侧边距之和 64）。`ContentViewMargins` 为零，项目几何只取共享 `ExifBlockGeometry`，内容视图与主窗口同用顶部原点坐标。
  - **TOOLBAR-05@web**：横排衬底块、竖排无衬底，与配置档无关；宽形态 EXIF 在控制栏滚动文档内，手机竖屏移到顶行，横屏留在固定控制宿主。
  - **TOOLBAR-05@win**：值文本取 Bold（12 点 SemiBold 在 Windows 上肉眼与正文同重），标签保持 10 点次要色。底部横排为原生卡片材质块（`CardBackgroundFillColorDefaultBrush` + `CardStrokeColorDefaultBrush`、圆角 `toolbar.exif.cornerRadius`），右侧竖排为无衬底 52 点透明容器；项目几何只取共享 `ExifBlockGeometry`。
- **TOOLBAR-06**：四端 EXIF 整组点击都直接选择同一工具单例的 EXIF 详情（已展示其他页也切到 EXIF）；主图菜单入口只前置已展示窗口并保留当前页。业务导航意图统一由 `PhotoViewer/ViewModels/Main/MainViewModel.cs` 收口，各端主窗口负责把控制栏与菜单接到不同入口，工具宿主同步当前选择；无 EXIF 按 TOOLS-02 回退。
- **TOOLBAR-07**：重建。**TOOLBAR-07@ios**：先 `RemoveFromSuperview` 再 `Dispose`；**@win** 按影响范围合并 dispatcher turn，同名命令按钮、评分槽位与 EXIF 项保留实例，绑定变化只刷新提示与读屏名；**@android** `MarkDirty` 后在布局阶段补齐 `Arrange` 并落位子视图；**@mac** 按结构摘要（评分显隐、EXIF 项顺序、命令顺序与悬停绑定、方向）决定是否重建，摘要不变时只就地刷新 EXIF 文本、评分显示值与命令可用态，同名命令按钮与评分槽位保留实例（`MacMainWindow.BuildControls`、`MacControlBar`）。
- **TOOLBAR-08**：溢出。四端都必须有滚动宿主，溢出时可滚、保存并夹取滚动偏移（macOS `NSScrollView`、iOS `UIScrollView`、Windows 控制栏自带 `ScrollViewer`、Android `AndroidControlBarScrollHost`）。滚动只在"单组本身超长"时出现，能换行时必须换行；手机形态始终单列、不换列也不滚动，宽形态保留用户完整命令配置。
  - **TOOLBAR-08@win**：文档面板的主轴可用长度取主窗口给控制栏落的显式尺寸（等于滚动视口），不取 `ScrollViewer` 的无限测量约束，因此列布局按组换列、行布局按组换行；`ScrollableMain` 在能容纳时为 0（smoke 断言 700 宽两向均 ≥2 行且不可滚）。
- **TOOLBAR-09**：无照片/空控制栏。三组为空时不生成子控件，文档外边距按所在区域规则计算；各端由 `ControlBarLayout`、`ToolbarFlowLayout` 或 `WindowsLayout` 计算文档长度。
- **TOOLBAR-10**：视觉与反馈。底色/描边取 `color.toolbar.*`，圆角 `toolbar.cornerRadius`；星形与清零叉字号分别取 `toolbar.rating.glyph.fontSize` / `toolbar.rating.clearGlyph.fontSize`。
  - **TOOLBAR-10@mac / @win / @ios / @android**：原生按钮/开关可用系统悬停、按下、禁用、焦点色与轻量状态动效（L4）；占位、命中区与令牌几何一致，状态切换不引起布局抖动、残留或裁切。自绘控件按 `color.toolbar.*` 规则绘制，禁用只降内容，圆角不与系统 bezel 裁切叠加。
  - **TOOLBAR-10@web**：`PhotoViewer.Web/src/shell/controlbar.css`、`filterbar.css` 按 TOOLBAR-10 的自绘口径对标 macOS：命令圆按钮常态无描边、悬停出描边；命令圆按钮、星级胶囊、筛选胶囊与相似聚类开关同取 TOOLBAR-10 的模糊与半透明白色色层；评分槽悬停 / 按下为槽内内切正圆，筛选单胶囊与各段的状态层为段内同心内缩的胶囊形，两者都叠加 `color.menu.item.hover/pressed`；筛选下拉当前项画勾；开关型命令不在按钮上表达按下态（TOOLBAR-12）。
  - **TOOLBAR-10@win**：命令圆按钮为默认样式 `Button`（44 见方、圆角 22，常态填充取 `color.toolbar.button.tint` × `shell.glass.button.tint.opacity@windows`，保留 Fluent 抬升描边与系统状态外观），筛选胶囊、未选中相似聚类开关与星级胶囊同取常态填充；星级胶囊为 224×44 半圆端，六个槽位为透明 `Button`，悬停/按下在槽内画内切正圆状态层（`SubtleFillColorSecondaryBrush` / `SubtleFillColorTertiaryBrush`，`WindowsControlStyles.ApplyRatingSlot`），无照片时只把星形降到 `color.toolbar.disabled.content.alpha`；来源浮窗星排复用同一槽位状态层。相似聚类开关用默认样式 `ToggleButton`，勾选态为系统强调色。仍由壳自绘的透明方块按钮与相似面板动作按钮继续由 `WindowsControlStyles` 覆盖状态键，禁用底色与描边保持常态。
- **TOOLBAR-11**：焦点与键盘。每个控件的焦点顺序按 TOOLBAR-11 由各端控件宿主给出，Enter/Space 触发与命令同一入口。
- **TOOLBAR-12**：开关型命令的按钮态。四端当前都不在按钮上表达按下态/强调色，状态只在菜单勾选与面板可见性上。

- **TOOLBAR-06@web**：没有工具窗口，控制栏 EXIF 组不可点击、无命中区域。

## similarity

<a id="sim"></a>

- **SIM-01**：入口与开合。共享 `PhotoViewer/ViewModels/Main/File/SimilarityPanelViewModel.cs`，展开态从 `SettingsViewModel.SimilarityPanelExpanded` 读取、经 `IViewStateStore` 持久化（macOS/iOS `AppleViewStateStore`、Windows `WindowsNativeViewStateStore`、Android `AndroidViewStateStore`）；各端面板类 `MacSimilarityPanel`、`iOSSimilarityPanel`、`WindowsSimilarityPanel`、`AndroidSimilarityPanel`（各在对应头工程 `Native/` 下）。
- **SIM-02、SIM-03**：空态与动作。macOS 空态几何常量 `MacSimilarityPanel.CornerRadius` / `EmptySpacing`；动作按钮与进度各端自绘。
  - **SIM-03@android**：动作为 Material 文本按钮 90×44（`color.sim.action.*` / `sim.action.*`）与文本进度，信息等价，点按区不小于 44dp。
- **SIM-04、SIM-05**：卡片复用胶片栏卡片；点选走 `MainViewModel.SetCurrentImageKeepAnchor`，双高亮由各端绑定 `IsCurrent` / `IsCurrentImage`。
- **SIM-06、SIM-08**：计算与结果。阈值/数量 `SettingsViewModel.AI.cs` 的 `SimilarityThreshold*` / `SimilarityMaxResults*`；服务 `PhotoViewer/Core/AI/SimilarityService.cs`。
- **SIM-09**：面板内改星走与胶片栏同一写回路径，锚点不变；筛选落出由共享 `PhotoViewer/ViewModels/Main/File/ThumbnailListViewModel.cs` 处理。
- **SIM-07**：几何。共享 `PhotoViewer.Shared/SimilarityPanelGeometry.cs`；四端一律用 `RowWidth` 夹到可用宽一半，`dockWidth` 参数承载列表态停靠区宽，各端由相似面板的 `DockMeasureWidth`（只在列表态且动作区显示时非零）传入，停靠区内容变化即重排。Windows 面板的 `SetAvailableWidth` 按 `RowWidth(hasItems, itemCount, availableWidth, dockWidth)` 全额落宽（行布局）或 `PanelWidth` + `sim.panel.margin`（列布局），外框再让出 4 点右边距；`MaxWidth` 同步夹到可用宽一半。
- **SIM-10、SIM-11**：错误与输入等价。列表态与空态的动作区都必须承载错误标签，列表态由共享 `ShowActionAreaError` 驱动；macOS `MacSimilarityPanel`、iOS `iOSSimilarityPanel._dockError`、Windows `WindowsSimilarityPanel` 的停靠错误标签、Android `AndroidSimilarityPanel._footerError` 各自绑定。

## menu

- **MENU-01、MENU-02**：主图菜单的项、分组、文案、勾选与可用性由共享 `PhotoViewer/ViewModels/Main/PhotoMenuCatalog.cs` 给出（`Build` 的 `includeOpenFile` 区分触屏，`includeExif` 只在手机形态为 true，在栏位组末尾多一项「EXIF」），各端只把条目映射为原生菜单项、组间分隔与平台图标，执行走同一命令入口。EXIF 项的勾选取共享 `MainViewModel.IsExifViewVisible`，执行 `ToggleExifView`，状态写 `PhotoViewer.Native.ShowExif`（SHELL-04）。
  - macOS：`PhotoViewer.Mac/Native/MacMainWindow.cs` 的 `ContextMenu`（`NSMenu`，`UpdateMenuChecks` 在 `menuWillOpen:` 同步勾选与可用性）；系统菜单栏由同文件 `BuildMenus` 建立，「显示」菜单复用目录中的栏位与预览两组，「操作」菜单按设置的完整命令集合和顺序生成。
  - iOS：`PhotoViewer.iOS/Native/iOSContextMenus.cs` 的 `Photo`（`UIMenu`，组用 `DisplayInline` 分段）。
  - Windows：`PhotoViewer.Windows/Native/WindowsMainWindow.cs` 的 `BuildContextMenu`（`MenuFlyout`，勾选项用 `ToggleMenuFlyoutItem`）。
  - Android：`PhotoViewer.Android/Native/AndroidNativeRootView.cs` 的 `ShowPhotoMenu`（`AndroidGlassPopupLayer` 窗口内玻璃面板）。
- **MENU-03、MENU-04**：弹出与收回。macOS/iOS/Windows 用系统菜单；Android 使用主窗口内玻璃面板并夹取到屏幕内（`ShowPhotoMenu`）。
  - **MENU-03@android**：窗口内玻璃面板，定位与夹取自实现；全窗透明拦截、点外不穿透、读屏模态与焦点归还由共享弹层宿主承担。
  - **MENU-04@ios**：卡片星级弹窗不抬升卡片、不移动列表布局。
- **MENU-05**：卡片入口。卡片菜单按修订后的 MENU-05 退役，改为长按/辅助点按打开星级弹窗（RATING-07）；iOS 与 Android 都已改为卡片长按与辅助点按打开星级弹窗（FILM-05@ios、FILM-05@android），桌面用 tooltip + 星级按钮。
  - **MENU-05@android**：卡片长按不再有 `PopupMenu`，改为打开星级弹窗并把同一次触摸转发给弹窗星排；完整文件名与拍摄时间由弹窗条目的文件名/时间行承担，拍摄时间取 `ShotTimeText.FormatShortShotTime`（`PhotoViewer/Core/Platform/PhotoHintText.cs`），不使用桌面悬停提示的完整格式。
- **MENU-06**：主图菜单与 macOS 系统菜单栏均有前置图标。
  - **MENU-06@mac / @ios**：使用 SF Symbols，共用 `PhotoViewer.Apple/AppleSymbolNames.cs` 的 `Menu` 映射；Mac 独有「打开文件…」用 `doc`、应用菜单「退出 PhotoViewer」用 `power`，系统菜单栏与右键菜单一样显示图标。
  - **MENU-06@win**：`WindowsIcons` 按 `Design/icons.json` 提供共享 Fluent 字形，普通项与勾选项都保留图标和原生勾选态。
  - **MENU-06@android / @web**：自绘菜单在勾选列后绘制语义图标，几何取 `menu.icon.*`。Android 由 `AndroidIcons.ForMenu` 取 `Design/icons.json` 的共享 Fluent 字形；Web 使用 `icon.menu.*` 的 uicons 字形，增强复用 `icon.command.toggleEnhance`。
- **MENU-07**：不可用项由 `PhotoMenuCatalog` 按当前照片给出（无照片时「雾天增强」置灰，工具可打开空态）；macOS 关闭 `NSMenu` 自动启用，显示前同步。
- **MENU-08**：手机形态「更多」按钮的菜单。iOS `PhotoViewer.iOS/Native/iOSNativeRootViewController.cs` 的 `BuildMoreMenu` 用 `UIDeferredMenuElement` 延迟构建，内容直接取共享 `PhotoMenuCatalog`（手机形态 `includeExif: true`，与主图长按菜单同为栏位组末尾的「EXIF」），每次展开重建并按共享状态刷新勾选。Android `AndroidNativeRootView.ShowPhotoMenu` 同样以 `PhotoMenuCatalog(includeExif: true)` 提供内容，更多与主图长按复用同一入口。
- **MENU-01@web**：不实现设置与工具窗口，主图菜单不生成「工具…/设置…」及其分组，其余项、顺序与勾选语义不变（SCOPE-01）。
- **MENU-03@web**：主图菜单与卡片菜单为自绘弹层（MENU-03 自绘外观），夹在窗口内，键盘语义按 MENU-03；悬停提示同样自绘（`tooltip.*`）。
- **MENU-05@web**：触屏（`pointerType=touch`）长按卡片按旧规则弹卡片菜单，待随修订后的 MENU-05 替换为星级弹窗；桌面右键卡片仍弹主图菜单。

## input

- **INPUT-01、INPUT-02**：手势到命令。macOS `PhotoViewer.Mac/Native/MacInput.cs`；iOS `PhotoViewer.Apple/AppleInputMapping.cs`；Windows `PhotoViewer.Windows/Native/WindowsInput.cs`；Android `PhotoViewer.Android/Native/AndroidInputMapping.cs`；共享命令表 `PhotoViewer.Shared/InputCommandMap.cs`。
- **INPUT-03、INPUT-04、INPUT-06**：主图手势。macOS `MetalPhotoView.ScrollWheel`/`MagnifyWithEvent`；iOS `UIScrollView` + 单指轻扫；Android `ScaleGestureDetector` + `GestureDetector`；Windows `WindowsPhotoView` 指针手势。滚轮精度分流四端统一：精确滚动走平移、离散一格一命令（Windows `WindowsInput.FromWheel`、Android `AndroidPhotoView.OnGenericMotionEvent`）。
- **INPUT-05**：按键重复。只对可重复命令重复（`InputCommandMap` 的 Repeatable 标记，四端重复入口统一按此设闸）；macOS/Windows/Android 跟系统重复节奏，iOS 用固定节奏。
  - **INPUT-05@ios**：固定 350 ms / 60 ms 节奏（`iOSNativeRootViewController.KeyRepeatDelay` / `KeyRepeatInterval`），属平台差异。
- **INPUT-07@android**：长按候选由平台手势提前登记，按住时长从本轮 Down 真正送达视图起算；最终确认延到下一帧输入处理之后，并核对触摸轮次与活动状态（`AndroidPhotoView`）。正常静止按住的阈值不变。
- **INPUT-07、INPUT-08**：菜单与录制。macOS 真实 `NSEvent` 录制；Windows `WindowsHotkeyRecorder`；触控优先设备在连接键盘/鼠标/触控板时使用同一录制语义。
- **INPUT-13**：外设能力与快捷键控件可见性。桌面始终完整；触控优先设备按系统报告的连接状态增删键鼠专用控件。
  - **INPUT-13@ios**：键盘/鼠标/触控板连接状态取系统通知；宽形态连接任一即完整显示主/次绑定、录制、清空与冲突，仅直接触控隐藏全部键鼠相关控件但保留模型中的绑定配置；断开最后一个外设取消未完成录制并保留原绑定。Apple 修饰键映射在同一可见条件下显示。手机形态按修订后的 INPUT-13 隐藏控制栏功能自定义与快捷键编辑，既有绑定照常生效（`PhotoViewer.iOS/Core/iOSNativeSettingsPresenter.cs` 待接）。
  - **INPUT-13@android**：连接状态取 `InputManager` 的设备增删；可见性与录制语义同 iOS，动态连接不重置工作副本、不弹软键盘。
  - **INPUT-13@mac / @win**：桌面保持完整快捷键设置；Apple 修饰键映射只在 macOS 且外设可见条件满足时显示（`PhotoViewer.Apple/AppleInputMapping.cs`）。
  - 直接触控的手势入口（按压显示绿框、长按菜单、单指轻扫翻页）按实际事件类型处理；连接鼠标或触控板后仍可使用悬停，不能把“触控平台”视为无悬停能力。具体事件映射见各端入口；键鼠设置可见性独立按 INPUT-13 的外设连接状态决定。
- **INPUT-09**：预设/比例缩放入口同 IMAGE-04（各端 `ZoomPreset`）。
- **INPUT-10**：无照片时按钮禁用不隐藏、打开类命令始终可用；独立打开入口由各端控制栏或菜单提供；主图通过共享 `FolderViewModel.IsStartScreen` 与自身适应区命中，仅在首屏打开选择器。
- **INPUT-11**：Android `OnBackPressedCallback` 先关设置/工具 sheet 与菜单。
- **INPUT-12**：触屏通过卡片长按打开星级弹窗（RATING-07），iOS 与 Android 均使用同一入口处理卡片评分。
- **INPUT-01@web**：浏览器保留 Ctrl/⌘+N，「打开文件」主绑定不可用，次绑定 Ctrl+O 与空态点击、菜单为等价入口。
- **INPUT-04@web**：`WheelEvent` 没有精确/离散标志：`deltaMode` 为行/页，或像素模式下 `wheelDeltaY` 为 120 的整数倍且增量足够大时视为离散，其余为精确；Chromium/Safari 把触控板捏合投递为 ctrl+wheel，未按下物理 Ctrl 时按捏合处理。

## open

- **OPEN-01、OPEN-06、OPEN-07**：入口与外部打开。外部打开、拖入与命令行入口先把目标转成 `IStorageItemRef`，再投递实例级 `OpenRequestChannel`，由 `OpenCoordinator` 单消费者按顺序处理（`PublishAndWaitAsync` 供验收等待本批完成）；一批只取首个可打开项。选择器直接进入同一 `FolderViewModel` 打开入口。
  - macOS：`PhotoViewer.Mac/Native/MacMainWindow.cs`（`NSOpenPanel` / 拖入）、`PhotoViewer.Mac/Native/MacOpenPaths.cs`（命令行、Finder/Dock、URL → `LocalFileRef`/`LocalFolderRef`）、`PhotoViewer.Mac/Native/MacNativeApplication.cs`（消费 `OpenRequestChannel`）。
  - iOS：`PhotoViewer.iOS/Native/iOSNativeRootViewController.cs`（`UIDocumentPicker` / `UIDropInteraction`）、`PhotoViewer.iOS/Core/iOSExternalOpenBridge.cs`（URL → 本地引用 → 共享通道）。
  - Windows：`PhotoViewer.Windows/Native/WindowsMainWindow.cs`（选择器 / 拖入）、`PhotoViewer.Windows/Native/WindowsOpenPaths.cs`（命令行/文件激活）、`PhotoViewer.Windows/Native/WindowsSingleInstance.cs`（二次启动转发到主实例的通道；隔离验收不注册）。
  - Android：`PhotoViewer.Android/Native/AndroidNativeRootView.cs`（SAF）、`PhotoViewer.Android/Native/AndroidNativeShell.cs`（`ACTION_VIEW/SEND` 合并后投递共享通道）。
  - **OPEN-01@ios / @android**：只有文件夹选择器。
  - **OPEN-07@android**：未接 `OnDragListener`，手机无常见拖入来源，不建拖入入口。
- **OPEN-02、OPEN-04、OPEN-05、OPEN-08**：共享 `PhotoViewer/ViewModels/Main/FolderViewModel.cs`（打开、枚举与租约）与 `PhotoViewer/ViewModels/Main/File/ThumbnailListViewModel.cs`（列表重建与元数据回填）。所有显式打开的刷新范围、保留项与失败边界以 OPEN-08 为准，RATING 来源与 sidecar 的重读口径见 RATING-01；新目标回到适应态、当前目标保留视口。
  - 新目标的显式打开前调用 `FitToView()`；当前目标刷新不调用。四端统一由共享 `MainViewModel.RequestFitToView()` + 共享 `PhotoViewport` 实现（见 status.md OPEN-05）。
- **OPEN-03、OPEN-09**：单图模式与派生状态由共享 VM 承担；每次打开由 `FolderLoadSession` 持有存储访问租约（iOS `iOSStorageAccessSessionFactory` / Android `AndroidStorageAccessSessionFactory` / 桌面本地会话）。新快照提交成功时释放被取代会话的租约，当前快照保留到下一次提交或文件源释放；父目录不可读时回退单图。
- **OPEN-01@web / OPEN-07@web**：没有真实文件系统，打开文件/文件夹使用应用内选择器浏览合成文件夹；拖入按文件名匹配合成库，否则打开首个合成文件夹。

## state

- **STATE-01、STATE-02**：空态、加载与解码失败的提示文案由共享 `PhotoViewer/Core/Platform/PhotoHintText.cs` 按会话状态给出，显示入口同 IMAGE-10。切图方案由共享加载会话选定：缓存命中同步换图（保留前一张），四端主图视图以 `PhotoLoadSession.Generation` 判定切图；未命中清图并以 `IsLoadingVisible`（`status.loading.delay` 300ms）提示。程序化覆盖：Mac `--pv-native-filmstrip`、iOS `--pv-native-photo-check`、Android `pv-check main`，Web `?check=state`。
- **STATE-03**：操作结果提示。四端统一为 4 s 非模态提示条：macOS `PhotoViewer.Mac/Native/MacStatusToast.cs`（`MacMainWindow` 订阅 `Session.Notice`）、iOS `PhotoViewer.iOS/Native/iOSStatusToast.cs`（`iOSNativeRootViewController` 调用）、Windows `PhotoViewer.Windows/Native/WindowsMainWindow.cs`、Android `PhotoViewer.Android/Native/AndroidNativeRootView.cs`。评分写回与"部分来源写入失败（补偿未完成）"分列文案（RATING-03）；后者必须按磁盘真值展示每个来源与冲突状态，不把部分成功当整组成功。
- **STATE-04**：模型/推理/数据库不可用由共享 `AnalysisPresenter`、`SimilarityPanelViewModel` 与设置清库路径承担。
- **STATE-05**：`FolderViewModel` 确认当前目录可达性，`PhotoLoadSession` 持有已显示的画面并停止磁盘加载。各端原生主图宿主呈现持续状态和文件夹选择器入口；Web 用合成目录注入不可用状态（`?check=state`）。

## settings

- **SETTINGS-01、SETTINGS-02**：工作副本 `PhotoViewer/Core/Settings/NativeSettingsSession.cs`；分类与行由各端设置宿主实现。
  - macOS：`PhotoViewer.Mac/Native/MacNativeSettingsPresenter.cs` + SwiftUI `PhotoViewer.Mac/Native/SwiftSettings/MacSettingsUI.swift`。
  - iOS：`PhotoViewer.iOS/Core/iOSNativeSettingsPresenter.cs`（`PageSheet`）。
  - Windows：`PhotoViewer.Windows/Native/WindowsSettingsWindow.cs`（`NavigationView` + `SettingsCard`）。
  - Android：`PhotoViewer.Android/Native/AndroidNativeSettingsPresenter.cs`（`BottomSheetDialog`）。
  - 控制栏功能行的命令图标：Windows `WindowsIcons.ForCommand`，macOS/iOS `AppleSymbolNames.ForCommand`，Android `AndroidControlBar.CommandGlyph` 与 `AndroidIcons.ForCommand`，三者都按 `Design/icons.json` 取平台语义图标。键鼠相关控件的显隐按 INPUT-13（见 [input](#input)）。
  - **SETTINGS-01@ios**：控制栏功能沿用系统勾选排序列表，与 EXIF 显示表同为 52 点标准行高；绑定按钮只在有外设时出现，点按即在行内录制，长按或辅助点按出「清空」菜单；录制期间手指轻点任意处取消录制并保留原绑定。
  - **SETTINGS-01@android**：勾选字形→命令图标、命令图标→命令名的视觉间距取 `settings.android.icon.gap`（16）；勾选 44dp 命中区的透明内缩由图标负左边距抵消，命中区不缩小。
  - **SETTINGS-08@ios**：控制页始终显示「边栏布局位置」；iPhone 的 `iOSLayoutModePicker` 只列并勾选「手机」，其他设备保留全部选项以便切回。「控制栏功能」标题下的两张独立原生设置卡片分别承载组合开关与可勾选排序的命令表；手机形态仅淡化不生效项的勾号，不显示绑定编辑和键盘映射。页内切换形态时页面就地重建分组。
  - **SETTINGS-08 / SETTINGS-17@android**：手机设备的模式选择只列「手机」，控制页在「控制栏功能」标题下用两张独立 Material 卡片分别显示组合开关和完整命令表，手机形态淡化不生效项的勾号；上一张/下一张复用 Material 勾选行、不显示排序把手，写共享 `ShowNavigationButtons`，配置与既有绑定保留。
  - **SETTINGS-17@ios**：上一张/下一张复用 `iOSNativeEmbeddedCheckableReorderListView` 的蓝色勾选、文字与 52 点行高，图标取 `chevron.left.chevron.right`，单项关闭排序把手，点行写共享 `ShowNavigationButtons`（`SettingsChangeImpact.Layout`）；关闭后收起组合式上下张，独立上下张按 SETTINGS-08 参与可选命令排序。
- **SETTINGS-03**：平台外观。macOS `NavigationSplitView` + `Form(.grouped)`；Windows Mica + `TitleBar` + `NavigationView`；iOS 半屏 sheet；Android Material 3 `BottomSheetDialog`。
  - **SETTINGS-03@mac / @win / @ios / @android**：各自原生设置外观，明暗统一固定深色。iOS 整个应用固定深色（`Info.plist` `UIUserInterfaceStyle = Dark` + 窗口覆盖），页面不铺不透明底以保留 sheet 半透材质。
- **SETTINGS-04**：入口与关闭。关闭统一走 `NativeSettingsSession.ApplyAsync()` 一次提交并 `Dispose`（幂等）。
  - **SETTINGS-04@android**：设置与工具共用 `PhotoViewer.Android/Native/AndroidSheetScrollView.cs`。`Show()` 之前把浮层配成收起态（可见高度 = 屏高 × `settings.android.sheet.peekRatio`），外层面板高度为屏高 × `settings.android.sheet.heightRatio`，顶部把手/标题上拖扩大、下滑关闭；内容区按下时禁止祖先拦截，内容滚动与到顶下拉都不收起 sheet。内层布局高度每帧同步为面板当前可见高度，收起、拖动、重新收起与窗口尺寸/insets 变化都跟随，外层拖动几何不变。
- **SETTINGS-05**：编辑隔离。工作副本以 `applyRuntimeSettings: false` 创建，缓存/解码/键盘映射副作用都在提交后执行（`SettingsViewModel`）。四端主屏订阅共享 `PresentationChanged(SettingsChangeImpact)`，按影响范围更新；预算/缓存等诊断走精确属性绑定，不触发主屏结构重建。Android 自绘下拉以最终屏幕锚点核对生命周期，相同几何重复布局不关闭；iOS 另以菜单与控制栏内容摘要保护已展示的控件。
- **SETTINGS-06**：缓存张数估算与超限判定在共享 `SettingsViewModel`；系统总内存/可用内存由 `IMemoryBudgetProvider.GetSystemMemoryInfo` 提供（Windows `GlobalMemoryStatusEx`，Android `ActivityManager.MemoryInfo`，Apple 物理总量与 Mach free/inactive 页）。警告色取 `color.settings.warning`。
  - **SETTINGS-06@ios**：缓存容量由运行时实测预算（`phys_footprint + os_proc_available_memory`，启用 `increased-memory-limit`）自动推导，文件页不显示「缓存内存上限 / 缓存数量上限」，两项字段保留、不参与 iOS 运行时；Normal 代理 F16、完整图 8 位，分别估算。
  - **SETTINGS-06@win**：缓存与解码默认值由物理内存与核数推导（`PhotoViewer.Windows/Core/WindowsPerformanceBudget.cs`）：缓存内存上限默认物理内存 ÷ 4（夹在 1024–8192 MB），数量上限按单张 2400 万像素 × 8 字节（线性 RGBA16F）折算并夹在 3–30 张，后台解码线程按核数 ÷ 3 夹在 2–6 且每 GB 缓存内存至少 1 路。运行时预算为物理内存 × 50%，足迹为进程私有提交（`WindowsMemoryBudgetProvider`）。已保存的设置覆盖默认值，不做迁移。
  - **SETTINGS-06@android**：内存预算取前台可见进程的匿名内存上限（`PhotoViewer.Android/Core/AndroidMemoryBudgetProvider.cs`，按 `MemTotal` 查 AOSP MemoryLimiter 可见档：3.2–4.8 GB → 2 GB、4.8–6.8 GB → 4 GB、6.8–9 GB → 5 GB、9–14 GB → 8 GB、14–18 GB → 10 GB，档外按 45% / 62.5% 外推；厂商另设前台红线时取较小者——小米 HyperOS/MIUI 读 `persist.sys.stability.pss_highest_line`，预算取其 90%），占用 = `RssAnon + VmSwap` + GL 纹理字节。缓存容量由该预算自动推导（`BitmapLoader.AutomaticCacheLimit`），文件页不显示两项缓存上限，预载上限按自动口径固定 10；适屏与完整图均按 8 位估算；原生解码线程默认 6 核及以上 2 路、否则 1 路，软件解码线程恒为 1。
- **SETTINGS-10@ios / @android**：AI 页无「清空特征数据库」；移动端长期不提供，配置保留、入口不显示。
- **SETTINGS-12**：数值校验。范围与步进取共享 `SettingsDocumentCodec` 与各设置视图模型（如 `SettingsViewModel.BitmapCache.cs`、`SettingsViewModel.AI.cs`），非法输入不写入。
  - **SETTINGS-12@android**：一般数值用 `+/-` 步进（与 iOS `UIStepper` 同类），步进控件不改变取值范围。缩放比例预设行例外：单一数值输入 + `%`，数字居中，`+`/`−` 只做新增 100% 项与删除项（SETTINGS-07）。
- **SETTINGS-13**：键盘与显示缩放。桌面由系统控件提供 Tab/方向键语义；触屏由各端设置列表承担；动态字体与显示缩放随系统。
- **SETTINGS-14**：四端共用 `SettingsRepository` + `SettingsDocumentCodec`。文件载体用唯一临时文件 + 原子替换，偏好/SharedPreferences 用平台同步提交。
  - **SETTINGS-14@mac / @ios**：生产环境 JSON 存 `NSUserDefaults` 单键（`PhotoViewer.Apple/AppleSettingsStore.cs`）；验收会话改用隔离 `FileSettingsStore`，不读写用户设置。
  - **SETTINGS-14@win**：JSON 存本地设置文件（`FileSettingsStore`），普通运行与隔离验收使用不同目录。
  - **SETTINGS-14@android**：JSON 存 `SharedPreferences`（`PhotoViewer.Android/Core/AndroidSettingsStorage.cs`），后台 `Commit` 并检查返回值；损坏/旧版备份为带时间戳的偏好键。
  - **SETTINGS-14@web**：Web 参考壳只持久化影响主屏的设置项，列表项使用壳内 id，文档结构不与四端设置 JSON 互换。
- **SETTINGS-15**：排序四端统一为拖拽；Android 用 `ItemTouchHelper` + 行尾把手，把手轻点给上移/下移菜单（`PhotoViewer.Android/Native/AndroidSettingsReorderList.cs`）。
  - **SETTINGS-15@android**：48dp 紧凑行只用于单行内容——纯触控控制功能、EXIF 显示与缩放预设（`settings.android.list.row.minHeight` / `settings.android.list.row.paddingY`）；多行格式与显示键鼠绑定的控制功能行取 `settings.android.list.row.standardPaddingY` 与最小行高 `settings.android.row.minHeight`；普通设置行、分类行与工具分类行取 `settings.android.row.minHeight` / `settings.android.row.paddingY` / `settings.android.row.paddingRight`，均按内容增高。勾选字形左缘按 `settings.android.card.paddingX` 与普通标签对齐；设置与工具分类箭头共用 `settings.android.navigation.chevron.width` 视觉容器、居中显示，整行可点。
- **SETTINGS-16**：拉条行。macOS SwiftUI `SliderRow`、iOS `CreateSliderRow` 标题自然宽、拉条占满剩余空间、当前值固定最小宽。
  - **SETTINGS-16@win**：`WindowsSettingsCard.SliderRow` 分成等宽左右两区，标题占左半区，拉条与固定宽当前值列占右半区，跨行、跨分类拉条左右边缘一致；缓存相关拉条不显示内部映射气泡。
  - Android 无拉条（SETTINGS-12@android）。

## tools

- **TOOLS-01、TOOLS-02**：外壳与入口。
  - macOS：`PhotoViewer.Mac/Native/MacToolsWindow.cs`（独立 `NSWindow`，原生侧栏 + 详情；默认 EXIF，无可用 EXIF 时落 DINO）。
  - iOS / iPadOS：`PhotoViewer.iOS/Native/iOSToolsPresenter.cs`（设置同款 `PageSheet` + `UINavigationController` 分组列表导航；主图菜单初次打开显示工具列表，控制栏 EXIF 入口直达 EXIF 详情）。
  - Windows：`PhotoViewer.Windows/Native/WindowsToolsWindow.cs`（`NavigationView` 三分类，无首页）。
  - Android：`PhotoViewer.Android/Native/AndroidToolsPresenter.cs`（设置同款 `BottomSheetDialog` + 分组列表导航；默认半屏收起展示，初次打开显示工具列表，系统返回先回列表再关闭浮层）。
  - **TOOLS-01@win**：工具窗口 960×720、最小 800×560，因常驻侧栏 240 + EXIF 表格 720；普通窗口（进任务栏/Alt-Tab、可最大化）。
  - **TOOLS-01@mac / @ios / @android**：不建「照片数据统计」入口。
- **TOOLS-02@win**：无首页/返回，常驻三分类；分类顺序与其他端一致为 EXIF → DINO → 照片数据统计。
- **TOOLS-02@mac**：窗口外壳与设置共用同一 SwiftUI 侧栏组件与窗口样式（`PhotoViewer.Mac/Native/SwiftSettings/MacToolsUI.swift`），侧栏几何、选中态与工具栏不另行仿制；详情仍是 AppKit 页面。侧栏常驻、不设工具首页；默认 EXIF，无可用 EXIF 时落 DINO。
- **TOOLS-02@ios / @android**：分组列表导航，主图菜单初次打开显示工具列表，不自动进入 DINO；两端控制栏 EXIF 入口都直达 EXIF 详情，已展示其他页也明确导航到 EXIF。无当前照片或 EXIF 详情不可用时退回列表。只读边界、生命周期、7 列 EXIF 与 3+2 瓦片不变；原生表格分隔线/分组材质可按 L4 取平台样式，业务列比例与内容不随之改变，也不新增功能。
- **TOOLS-03、TOOLS-04**：EXIF 详情。macOS 为虚拟化 `NSTableView`（`PhotoViewer.Mac/Native/MacExifDetailView.cs`）：标签行自绘文本，点击时才在该文本上放置可选中文本框，行高按列宽缓存；iOS `UITableView`；Windows 7 列 `Grid`；Android 线性行 4 个内容列。
  - **TOOLS-03@android**：4 个内容列承载 7 列几何，手机与平板同口径。
- **TOOLS-05**：DINO 五瓦片。共享 `PhotoViewer/Core/AI/DinoDiagnosticPresenter.cs`；五瓦片 3+2，第六格留空，刚体拟合文本以整宽块置于网格下方（四端统一）。
- **TOOLS-06**：计算时机。共享 presenter 先读库；离开页面或关闭窗口即取消在途任务（`ToolsViewModel.Deactivate()`，由 macOS `MacToolsWindow`、iOS `iOSToolsPresenter`、Windows `WindowsToolsWindow`、Android `AndroidToolsPresenter` 在离页与关闭时调用）。
- **TOOLS-07**：照片数据统计，仅 Windows（`ToolsViewModel.IsPhotoStatsAvailable`）。
  - **TOOLS-07@win**：平台专属工具，其他端不建入口。
- **TOOLS-01@web**：不实现工具窗口。

## scope

- **SCOPE-01**：可见性由共享 VM 布尔属性决定；照片数据统计 `ToolsViewModel.IsPhotoStatsAvailable => OperatingSystem.IsWindows()`。
  - **SCOPE-01@mac / @ios / @android**：不建照片数据统计入口（同 TOOLS-07@win）。
- **SCOPE-02**：计算门控。相似 `SimilarityPanelViewModel.IsPanelVisible`；分析 `AnalysisPresenter.Refresh`；回退 `AnalysisPresenter.FallbackDelayMs`（500）。
- **SCOPE-03**：入库与现算。批量提取 `PhotoViewer/Core/AI/FolderFeatureIndexer.cs`；即时缓存 `AnalysisResultCache`。
- **SCOPE-04**：能力缺失。**SCOPE-04@win**：RAW 主图/代理走系统 WIC RAW 引擎（Raw Image Extension）全分辨率解码，按相机 `DefaultCrop` 裁切、方向交渲染层；解码器缺失或失败时只接受与有效成像区尺寸一致的整幅内嵌 JPEG，否则如实停在可得尺寸。**SCOPE-04@ios**：RAW 主图/代理经 `CIRawFilter` 全分辨率解码（几何取传感器 `NativeSize`，EXIF 方向交 GPU），只有创建/渲染失败回退 ImageIO 时才可能只得到内嵌预览、`IsProxy=false`，此时如实停在该尺寸；CoreML EP 不可用回退 CPU。**SCOPE-04@android**：RAW 走打包的 LibRaw（`AndroidRawDecoder`）全分辨率开发，半尺寸输出已满足代理目标时用 half_size，输出 sRGB 或 Display P3；首次解码空窗期用内嵌预览顶替（STATE-01），真产物到达即替换，顶替图不冒充全分辨率，细节预览不以它为源；`libpvraw.so` 缺失时 RAW 停用并记一次日志（不回退系统预览冒充主图）。模拟器 P3 协商失败回退 sRGB。
- **SCOPE-05**：文件写操作。评分写回走共享 `RatingService`：组的每个文件内嵌与每个实际存在的 `<stem>.xmp` 都是来源，RAW 也写内嵌（Sony 相机与 Imaging Edge 只读 RAW 本体），sidecar 不存在时不创建（RATING-01/03）；无删除/重命名/移动/导出命令。
- **SCOPE-06**：平台专属。导航图点按/拖动定位（**SCOPE-06@ios / @android**）；Windows 照片统计；Android 设置数值步进见 SETTINGS-12@android。
- **SCOPE-01@web**：设置窗口、工具窗口、真实解码/写回/推理均不实现，相应入口不显示；照片、特征与故障均为合成数据。

