# OPEN 草稿：平台事实与待决问题

## platforms

### OPEN-01 入口与统一流程

- **macOS**：AppKit `NSMenu`「文件 → 打开文件…/打开文件夹…」与主图右键菜单入口；`NSOpenPanel`（`PhotoViewer.Mac/Native/MacMainWindow.cs:866`、`:889-890`）。外部打开经 `MacExternalOpenBridge`（`OpenFile`/`OpenFiles`/`OpenUrls`）投递 `ExternalOpenService`（`PhotoViewer.Mac/Native/MacNativeApplication.cs:95-107`）。命令行参数同桥（`:99-100`）。空首屏点击优先文件选择器（`PhotoViewer.Mac/Native/MacMainWindow.cs:827-828`）。
- **iOS/iPadOS**：只有文件夹选择器（`UIDocumentPickerViewController`）；「打开」命令与菜单项都打开文件夹（`PhotoViewer.iOS/Native/iOSNativeRootViewController.cs:600`、`:779-795`；`PhotoViewer/ViewModels/Main/FolderViewModel.cs:81-99`）。外部打开经 `iOSExternalOpenBridge.PublishNativeFromUrl(s)`，启动参数、scene URL、拖入共用（`PhotoViewer.iOS/Native/iOSNativeShell.cs:277`、`PhotoViewer.iOS/Native/iOSNativeAppDelegate.cs:36`、`:45`、`PhotoViewer.iOS/Native/iOSSceneBridge.cs:70`）。
- **Windows**：`MenuFlyout`「打开文件…/打开文件夹…」（`PhotoViewer.Windows/Native/WindowsMainWindow.cs:345-355`）；WinRT `FileOpenPicker`/`FolderPicker`；命令行与窗口 `Drop` 统一走 `OpenPathsAsync`（`:297-315`、`:917-940`）。同一批路径**优先文件**，没有文件才取首个目录（`:301-314`）。
- **Android**：SAF `ACTION_OPEN_DOCUMENT_TREE` 选文件夹（`PhotoViewer.Android/Native/AndroidNativeRootView.cs:546`、`:164`）；长按菜单入口；外部打开 `ACTION_VIEW`/`SEND`/`SEND_MULTIPLE` 由 bootstrap 接管后投递（`PhotoViewer.Android/Native/AndroidNativeShell.cs:157-169`、`:178-216`）。**拖入未接**（`Plans/native-rebuild-phase-android.md:186`、发现 #21 `:250`）。
- 入口形态差异属 `OPEN-01@ios`（无文件选择器）、`OPEN-01@android`（无文件选择器、无拖入）；规则本身四端相同。

### OPEN-02 打开文件夹与枚举

- 共享 `FolderViewModel.LoadFolderAsync` 三段式：指定目标先上屏 → 枚举 → 后台属性/星级（`PhotoViewer/ViewModels/Main/FolderViewModel.cs:263-349`）；格式与隐藏文件判定 `IsImageFile`（`:467-480`）。
- 同名合并与代表文件由 `ThumbnailListViewModel.ApplyFilter` 按设置里的格式顺序取首项（`PhotoViewer/ViewModels/Main/File/ThumbnailListViewModel.cs:203-261`）。
- 打开新文件夹时 `BitmapLoader.ClearThumbnailClass()`（`FolderViewModel.cs:149-151`）。
- 列表容器：mac `NSCollectionView`、iOS `UICollectionView`、Windows `ItemsRepeater`、Android `RecyclerView`；均虚拟化，枚举规模不影响首图上屏。
- 空文件夹/无权限不再有专用文案，落到 `STATE-01`。

### OPEN-03 打开文件与单图模式

- 父目录试图：`file.GetParentAsync()`，返回 null → `OpenSingleImageAsync`（`PhotoViewer/ViewModels/Main/FolderViewModel.cs:204-256`）。单图模式下 `FolderName = file.Name`，`_allFiles` 只含一张（`:240-256`）。
- 平台父目录可得性：macOS 安全作用域书签、iOS security-scoped URL、Android SAF `takePersistableUriPermission`；单文件分享/临时 URL 通常拿不到父目录 → 单图模式。Windows 本地路径恒有父目录。
- 已在当前目录中的文件：按路径/名称在同一文件夹内查找并只切换选中（`FolderViewModel.cs:214-231`）；缺失时插到列表头再选中。
- 目标不是受支持格式：`OpenImageAsync` 直接返回（`FolderViewModel.cs:121-127`）。

### OPEN-04 首图与目标选择

- 无目标：`FilteredFiles.FirstOrDefault() ?? _allFiles.FirstOrDefault()`（`PhotoViewer/ViewModels/Main/FolderViewModel.cs:306-317`）。前者含同名合并代表；后者仅在筛选为空时使用枚举首张（可能是被合并隐藏的伴侣）。
- 指定目标：`LoadFolderAsync(folder, preferredFile)` 阶段 1 直接把目标设为当前（`:276-285`）；枚举阶段跳过同一文件（`:294`）。
- 目标不满足星级筛选时仍为当前照片，但不在列表内：`ApplyFilter` 只把同名伴侣映射回代表，不把当前项塞进筛选结果（`ThumbnailListViewModel.cs:293-307`）。

### OPEN-05 打开后的定位

- `OpenFolderAsync`/`OpenImageAsync` 默认 `scrollToCurrent: true`，末尾调用 `ScrollToCurrent()`（`PhotoViewer/ViewModels/Main/FolderViewModel.cs:121-158`）；`LoadNewImageFolder` 显式 false（`:164-167`）。
- mac：`ScrollToCurrent` 带请求版本、布局后按共享 `FilmstripGeometry.CenterOffset` 动画（`PhotoViewer.Mac/Native/MacMainWindow.cs:798-816`）。
- iOS：`CancelScrollUpToNow` + 350ms 三次缓出（`PhotoViewer.iOS/Native/iOSNativeRootViewController.cs:800-830`）。
- Windows：收敛式定位（`LayoutUpdated` 补做、extent 覆盖目标才销账），外部打开非首张由 `--pv-native-smoke` 断言（`Plans/native-rebuild-windows-p2.md:293`）。
- Android：共享 `FilmstripAnimation` + `ValueAnimator`，触摸接管（`Plans/native-rebuild-phase-android.md:146-148`）。
- 缩放：mac/iOS 在选择器/外部路径前调用 `FitToView()`（`MacMainWindow.cs:827-828`、`:840-841`；`iOSNativeRootViewController.cs:785`）；Windows/Android 打开流程不重置视口（`WindowsMainWindow.cs:297-315`、`AndroidNativeShell.cs:157-169`）。见问题 2。

### OPEN-06 外部打开

- 共享队列：`ExternalOpenService` 在 UI 未就绪时排队（`Design/architecture.md:42-52`）；四端处理器分别见 mac `:206`、iOS `iOSNativeShell.cs:177`、Windows `WindowsNativeApplication`、Android `AndroidNativeShell.cs:157`。
- mac 串行化：`_openGate` 保证 Finder/命令行/拖入逐项处理、最后目标胜出（`MacMainWindow.cs:834-848`）。
- iOS：`HandleExternalOpenAsync` 先文件夹后文件（`iOSNativeShell.cs:228-250`）；Android 同（`AndroidNativeShell.cs:157-169`）；Windows 先文件后文件夹（`:301-314`）。
- 不支持类型：iOS 逐项 `continue` 后静默返回（`iOSNativeShell.cs:232-245`）；共享 `OpenExternalFilesAsync` 取首个图片，否则返回（`FolderViewModel.cs:106-114`）。

### OPEN-07 拖入

- mac：`RootView.RegisterForDraggedTypes(["public.file-url"])` + `PerformDragOperation` 读本地 URL（`PhotoViewer.Mac/Native/MacMainWindow.cs:951-959`），`OpenDroppedPaths` 汇入 `OpenPathsAsync`。作者真实鼠标拖放验收通过（`Plans/native-rebuild-phase-3.md:77`）。
- iOS：根视图 `UIDropInteraction`，`CanHandleSession` 接收 file URL，落地经 `iOSExternalOpenBridge.PublishNativeFromUrls`（`iOSNativeRootViewController.cs:1084-1106`、`:1081`、`:153`）。
- Windows：根元素 `AllowDrop = true` + `DragOver/Drop` → `OpenPathsAsync`（`WindowsMainWindow.cs:194-208`、`:917-940`）。
- Android：**没有** `OnDragListener`/`DragEvent`（全目录 grep 无命中）；计划登记为未接（`Plans/native-rebuild-phase-android.md:186`、`:250`）。

### OPEN-08 并发、中断与重入

- mac 有 `_openGate` 串行化打开（`MacMainWindow.cs:834-848`）；其余平台无同等闸门。
- `LoadFolderAsync` 无代际/取消令牌：直接清空共享 `_allFiles` 并异步补齐属性/星级（`FolderViewModel.cs:263-349`）；两个文件夹交替打开时结果可能交错。规则要求的“后发起胜出”尚未实现。
- `ImageViewModel` 的加载有代际闸（`PhotoViewer/ViewModels/Main/ImageViewModel.cs` 的 `_loadGeneration`），但那是主图解码，不保护目录枚举。
- 取消选择器：各平台选择器返回 null 即返回，不改状态（`MacMainWindow.cs:827-828`、`iOSNativeRootViewController.cs:783-785`、`WindowsMainWindow.cs:967-971`、`FolderViewModel.cs:81-99`）。

### OPEN-09 打开后的派生状态

- 控制栏 EXIF：`ControlViewModel` 订阅 `CurrentFile` 与 EXIF 到达（`PhotoViewer/ViewModels/Main/ControlViewModel.cs:152-172`）。
- 分析栏：`AnalysisPresenter.Refresh` 由宿主 `HostChanged`/入库信号驱动，可见才工作（`PhotoViewer/Core/AI/AnalysisPresenter.cs:124-154`）。
- 相似面板：`RecomputeAsync` 只在 `SimilarityPanelExpanded && IsThumbnailViewVisible` 时随 `CurrentFile` 重算（`PhotoViewer/ViewModels/Main/File/SimilarityPanelViewModel.cs:47`）。
- 抖动徽标：`ShakeFlagService.EvaluateAsync` 由 `AllFilesChanged` 触发，只读库（`PhotoViewer/ViewModels/Main/MainViewModel.cs:120-126`）。
- 工具页：`MainViewModel` 订阅 `CurrentFile`/EXIF 调 `Tools.SyncCurrentFile`（`:113-118`）。

## status

> 仅记录已有证据；OPEN 没有独立的四端验收套件，以下是从共享路径与其他套件间接得到的结论。

| ID | Windows | macOS 原生 | iOS/iPadOS | Android |
|---|---|---|---|---|
| OPEN-01 | 文件/文件夹选择器 150% 实机验证，扩展名模式修好后可弹出并返回（`Plans/native-rebuild-windows-p2.md:285`、`:337`）；真实手感待手验 | 文件/文件夹选择器与 Finder/命令行打开在原生壳可用；真实鼠标拖放作者 2026-10-16 手工验收通过（`Plans/native-rebuild-phase-3.md:77`） | 文件夹选择器随 I1 落地；`--pv-native-input-check` 模拟器 + 真机 PASS 27/28，覆盖 openURL 与拖入（`Plans/native-rebuild-phase-ipad.md:145`、`:172`）；触摸手感待作者 | SAF 文件夹选择器已接（`Plans/native-rebuild-phase-android.md:123-125`）；模拟器目视/日志，无专项断言；拖入未接 |
| OPEN-02 | 外部打开文件夹走文件夹模式；枚举与首图由共享 VM 承担（`Design/ui-contract.md:145`） | 原生壳文件夹模式程序化验证（八套回归） | 两机型 `layout` 24 checks 覆盖文件夹加载后的列表/首图；真机 `input` 覆盖 | 模拟器 `@files/...` 启动加载文件夹并目视；`pv-check main` PASS 14（`Plans/native-rebuild-phase-android.md:198`） |
| OPEN-03 | 外部打开文件走父目录；权限模型无沙箱，单图模式少见 | Finder/命令行文件打开经父目录；单图回退随共享 VM | 分享/拖入单文件可落到单图模式（共享 VM）；未专项断言 | SAF 单文件/临时授权可落到单图模式；未专项断言 |
| OPEN-04 | 外部打开非首张由 `--pv-native-smoke` 断言偏移离开列表头部（`Design/ui-contract.md:145`） | 首图/目标选择由共享 VM；`--pv-native-smoke` 覆盖外部打开非首张 | `layout` 24 checks 覆盖点选与首图；真机同 | 模拟器目视首图；无断言 |
| OPEN-05 | 外部打开非首张定位程序化通过；真实滚动条拖动与手感待作者（`Design/ui-contract.md:145`） | 定位触发与动画作者 2026-10-16 手验通过；重新显示/换向定位为已登记增强 | `layout`/`filmstrip` 覆盖显式定位；轻扫翻页走共享 Previous/Next（`Design/ui-contract.md:145`） | `FilmstripAnimation` 已实现；中间帧断言待 A6（`Plans/native-rebuild-phase-android.md:146`） |
| OPEN-06 | 命令行/文件关联非首张定位由 `--pv-native-smoke` 断言；真实跨屏/手感待验 | Finder「打开方式」、Dock、命令行统一 `ExternalOpenService`；作者真实拖放通过 | `--pv-native-input-check` 覆盖 openURL 与拖入（模拟器 + 真机，2026-10-17） | bootstrap 接管 `ACTION_VIEW/SEND/SEND_MULTIPLE`（A1b），模拟器日志；真机门待做 |
| OPEN-07 | 窗口 `Drop` 已接；程序化不可合成，真实鼠标拖放待作者手验（`Plans/native-rebuild-windows-p2.md:368`） | **作者 2026-10-16 手工验收通过**（主窗口拖入文件/目录） | `--pv-native-input-check` 以同一落地入口验证拖入（拖放会话本身不可合成）；真机 PASS 28 | **缺口**：未接 `OnDragListener`（A4 偏差，未关闭） |
| OPEN-08 | 无专项；打开串行/重入未验证 | `_openGate` 串行化；交替打开未专项验证 | 未专项 | 未专项 |
| OPEN-09 | 分析/相似/工具联动由 `--pv-native-smoke` 24 项间接覆盖（2026-10-17） | 八套回归间接覆盖 | 六套检查间接覆盖（analysis 15 / similarity 10 / tools 7） | 五模式检查间接覆盖（PASS 14/18/15/16/16，2026-10-18） |

## questions

1. **OPEN 编号冲突（必须先裁决）**：`shell.md:119` 与 `state.md:152` 各自也写了 `OPEN-01`，且含义不同（shell 是“打开文件与文件夹”，state 是“加载提示与延迟”体系里的相邻条目；本文件 `OPEN-01` 是入口与统一流程）。三个文件都覆盖全前缀，说明拆分口径未统一。建议以本文件为 OPEN 唯一出处，删除 `shell.md`/`state.md` 的 OPEN 小节，或把它们改为指向 `open.md`。
2. **模块范围与文件分工**：`Design/spec/` 下已有按前缀拆分的 `filter.md`、`image.md`、`toolbar.md`、`analysis.md`、`similarity.md`、`menu.md`、`input.md`、`settings.md`、`tools.md`、`scope.md`，另有覆盖全前缀的 `shell.md`/`state.md`，且它们互相引用 `shell.md`/`state.md`/`open.md`。本文件只写 `OPEN`，未重复其他前缀的 ID。另：`Design/README.md:57` 写“原 `ANL-01`–`04` 并入为 `ANALYSIS-02`–`05`”，与任务说明“`ANL-02`–`04` → `ANALYSIS-02`–`04`、`ANL-01` 退役”不一致；`Design/ui-contract.md:92-95` 仍是四条旧 ID。
3. **显式打开是否回到适应态**：mac 选择器/外部打开前调用 `FitToView()`（`PhotoViewer.Mac/Native/MacMainWindow.cs:827-828`、`:840-841`），iOS 选择器同（`PhotoViewer.iOS/Native/iOSNativeRootViewController.cs:785`）；Windows 与 Android 打开流程不重置视口（`PhotoViewer.Windows/Native/WindowsMainWindow.cs:297-315`、`PhotoViewer.Android/Native/AndroidNativeShell.cs:157-169`）。同时 `Design/spec/image.md:28` 把“外部打开”列为**保持倍率**的切图入口。需要二选一：打开回适应（改 image.md）还是打开也保持（改 mac/iOS 实现）。
4. **多项外部打开的取舍顺序**：Windows 优先文件、其次目录（`WindowsMainWindow.cs:301-314`）；iOS 先目录后文件（`PhotoViewer.iOS/Native/iOSNativeShell.cs:236-245`）；Android 先目录（`AndroidNativeShell.cs:157-169`）；mac 按路径顺序逐项尝试（`MacMainWindow.cs:835-844`）。OPEN-01 目前写“按投递顺序取首个可打开项”，需确认是否强制统一。
5. **`FolderViewModel.LoadFolderAsync` 没有代际/取消**（`PhotoViewer/ViewModels/Main/FolderViewModel.cs:263-349`）：连续打开两个文件夹时，先发起的枚举与后台属性/星级任务仍会写入共享集合，OPEN-08 的“后发起胜出”目前只在主图解码层有代际闸。是补实现还是降低规则等级？
6. **打开的目标不满足当前星级筛选**：主图显示目标，但列表没有它（`PhotoViewer/ViewModels/Main/File/ThumbnailListViewModel.cs:293-307` 只做同名伴侣映射）。这是有意的“目标优先”，还是应临时放宽筛选/给“不在筛选结果中”标记？
7. **空筛选时首图回退枚举首项**可能取到被同名合并隐藏的伴侣（`FolderViewModel.cs:311`）；该分支是否应改为“忽略筛选取代表文件”，避免首图与卡片身份不一致？
8. **Android 拖入缺口**：`PhotoViewer.Android/Native/` 无 `OnDragListener`/`DragEvent`，计划登记为 A4 偏差（`Plans/native-rebuild-phase-android.md:186`、`:250`）。OPEN-07 是四端必须项还是允许 `OPEN-07@android` 暂缺？
9. **移动端“打开文件”不存在**：`FolderViewModel.OpenFilePickerAsync` 在 Android/iOS 只选文件夹（`PhotoViewer/ViewModels/Main/FolderViewModel.cs:81-99`），但菜单/空态文案仍是“打开”。OPEN-01 是否应把“打开文件”标为桌面专属，移动端文案改为“打开文件夹”？
10. **同一目标重复打开**：`OpenFolderAsync` 没有“同文件夹直接复用”的快捷路径，选择器再次选同一目录会重新枚举并清缩略图缓存（`FolderViewModel.cs:144-158`）；打开文件才有同目录复用（`:214-231`）。OPEN-08 写“视为刷新”，需确认是否是期望行为。
11. **单图模式的“文件夹名”**：`FolderName = file.Name`（`FolderViewModel.cs:245`），文件栏没有“单图模式”标记；用户无法从界面判断为什么没有上一张/下一张。是否需要显式状态（STATE 或 FILM）？
