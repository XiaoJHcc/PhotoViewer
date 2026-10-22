# G2 裁定：menu / input / open

范围：B06 B07 B08 B09 B10 B28 B29 B37 C05 C06 C07 C08 C19 C20 D06 D28 D29 D30。

方法：按 `Design/README.md` §5 权威顺序（作者决定 > spec > tokens > 样机 > 基线 > 实现）与 §3 规范等级裁定；分类 U（行为/信息差异，必须统一）／P（依赖原生控件、平台惯例或输入形态，无优劣）／X（共享代码缺陷或规范与实现矛盾，改代码或改规范）。Avalonia 旧壳是原版参考但已退役，只作追溯，不作规范来源。

结论：**U 2 · P 1 · X 15 · 需作者 0**。

| Q | 类别(U/P/X) | Avalonia | mac | win | iOS | android | 裁定 | 依据(多数/更优/原版) | 后续(谁改什么) | 需作者 |
|---|---|---|---|---|---|---|---|---|---|---|
| B06 | U | 菜单无增强 | 菜单无增强 | 菜单无增强 | 有增强（勾选） | 有增强预览（勾选） | 四端统一收录「增强」勾选项；mac/win 补在栏位组之后 | 更优（MENU-01 已收录；三个视图开关同构，触屏依赖它） | mac/win 菜单各加一项，读同一 `IsEnhanced`/`ToggleEnhanced`；mac 系统「显示」菜单补分析栏与增强 | 否 |
| B07 | X | 缩略图栏 | 胶片栏 | 缩略图栏 | 胶片栏 | 胶片栏 | 统一「胶片栏」，Windows 改回 | 多数 3:2＋MENU-06 明禁别名 | win：`WindowsMainWindow.cs:357` 改文案 | 否 |
| B08 | X | 工具（无…） | 工具（缺…） | 工具… | 工具… | 增强预览 | mac 补省略号；Android 改「增强」 | 多数（win/iOS 正确） | mac：`:895` 改「工具…」；android：`:552` 改「增强」 | 否 |
| B09 | U | 无卡片菜单 | 无卡片菜单 | 无卡片菜单 | 单行标题＋秒＋时区 | 两行可点＋分钟精度 | Android 标题行不可点、时间补秒与时区；两行外观可保留为原生形态 | 更优（iOS 已按 MENU-05；可点无动作属缺陷） | android：`AndroidThumbnailCard.cs:293-294` 禁用标题行并改 `yyyy-MM-dd HH:mm:ss zzz`；`platforms.md` 登记 MENU-05@android 两行标题 | 否 |
| B10 | X | 空态点按开选择器 | 空态左键开选择器 | 同 mac | 先解细节定格 | 先清细节预览 | 保留「先解定格、后空态开选择器」优先级并写入 INPUT-10 | 更优（避免误开选择器；iOS/Android 已实做） | `input.md` INPUT-10 补一句；无代码改动 | 否 |
| B28 | P | 移动端文案「打开文件夹」 | 文件＋文件夹 | 文件＋文件夹 | 仅文件夹 | 仅文件夹 | 触屏无「打开文件…」；文案固定「打开文件夹…」 | 原版＋平台（触屏只有目录选择器） | 保持 OPEN-01@ios/@android 登记；原生壳文案已正确，无代码改动 | 否 |
| B29 | X | 投递顺序取首个 | 路径顺序取首个 | 先文件后目录 | 投递顺序取首个 | 逐项各开一次（后项覆盖） | 统一按投递顺序取首个可打开项，其余忽略 | 多数 3:2＋OPEN-01 明定 | win：`OpenPathsAsync` 改为按入参顺序取首个；android：`HandleIntent` 合并为单个请求（不再逐项 Publish） | 否 |
| B37 | X | 仅父目录 null 才单图 | 父目录恒非空，枚举失败未兜底 | 本地路径恒有父目录 | 同 mac | 无树 Uri 时进单图 | 触发改为「父目录为空**或枚举被拒/失败**」；失败进单图模式 | 更优（沙箱、单文件授权、一次性 URL 的真实形态） | `open.md:35` 改触发；`FolderViewModel` 捕获枚举异常回退单图；Mac/iOS 依赖此路径 | 否 |
| C05 | X | 滚轮全部走命令 | 已分精确/离散 | 未分，全走命令 | 离散识别器分流 | 未分，逐事件派发 | 精确滚动平移、离散一格一命令；Windows/Android 补齐 | 更优＋INPUT-04（Mac/iOS 已实现） | win：`OnPhotoWheel` 加精度判据并平移；android：按 source/增量连续性分流并累积到一格才派发 | 否 |
| C06 | X | 系统重复，全部命令 | 系统重复，全部命令 | 系统重复，全部命令 | 固定计时器，全部命令 | Multiple，全部命令 | 仅上一张/下一张/预设缩放可重复，其余按下只执行一次 | 规范 INPUT-05（四端同向偏离） | 命令元数据加 `Repeatable`；四端重复入口加闸（mac `IsARepeat`、win `WasKeyDown`、android `Multiple`、iOS 不启动计时器） | 否 |
| C07 | X | 无卡片菜单 | — | — | 长按抬手不点选 | 长按后抬手仍点选 | 长按弹卡片菜单后消费抬手，不切图、不改星 | 更优（MENU-05 必须条款） | android：`AndroidThumbnailCard` Up 分支加长按守卫并清标志 | 否 |
| C08 | X | 无增强项 | 无增强项 | 无增强项 | 无照片仍可切换增强 | 无照片仍可切换增强 | 无照片时「增强」置灰；「工具…」照常开空态 | 规范 MENU-07 | iOS 禁 action；android 行禁用；mac/win 随 B06 一并做启用态 | 否 |
| C19 | X | 共享 VM 无代际 | 有打开闸门，共享 VM 无代际 | 无代际 | 无代际 | 无代际 | 共享 VM 加打开代际；旧枚举与属性结果不写列表 | 规范 OPEN-08 | `FolderViewModel.LoadFolderAsync` 每段 await 后校验代际，过期即弃 | 否 |
| C20 | X | 空筛选回退枚举首项 | 同共享 VM | 同 | 同 | 同 | 回退改为「枚举首项所属合并组的代表文件」 | 更优（OPEN-04 首图与列表身份一致条款） | `FolderViewModel.cs:311` 改代表选择；`open.md:43` 同步改表述 | 否 |
| D06 | X | 菜单无快捷键列 | 无；仅系统「设置…」⌘, | 无 | 无 | 无 | MENU-06 快捷键列由「应当」降为「可以」 | 原版/多数（5/5 未做；原生菜单无统一列，mac 设 KeyEquivalent 会形成第二派发路径） | `menu.md:67` 改等级；无代码改动 | 否 |
| D28 | X | 目标优先，列表不含它 | 同共享 VM | 同 | 同 | 同 | 维持目标优先：不放宽筛选、不加「不在筛选结果」标记 | 规范 OPEN-04 已定 | 无代码改动；`open.md` 补「不标记」一句 | 否 |
| D29 | X | 同目录重开＝重枚举 | 同 | 同 | 同 | 同 | 保留「视为刷新」，但重开须保持当前照片 | 规范 OPEN-08 | `OpenFolderAsync` 同目录时以当前文件为 preferred 重载，并免清缩略图缓存 | 否 |
| D30 | X | 仅设 FolderName，无标记 | 无标题/无标记 | 无标题/无标记 | 无标题/无标记 | 无标题/无标记 | 不新增「单图模式」标记；以 1 张卡片＋文件栏标题（＝文件名）承担 | 原版/多数（5/5 无标记；新增状态会引入规范外 UI） | 不新增 UI；文件栏标题在四端均未实现，归 OPEN-02 缺口（另一条），不并入本条 | 否 |

## 证据（按 Q）

**B06**
- Avalonia：`PhotoViewer/Views/Main/ImageView.axaml:123-148` 自绘菜单只有打开、栏位、工具、设置，无增强。
- mac：`PhotoViewer.Mac/Native/MacMainWindow.cs:885-899`（四组之外无「预览」组）；系统「显示」菜单 `:866-873` 缺分析栏与增强。
- win：`PhotoViewer.Windows/Native/WindowsMainWindow.cs:342-384`，`menu.Items` 只有打开/三栏位/工具/设置。
- iOS：`PhotoViewer.iOS/Native/iOSContextMenus.cs:60`；Android：`PhotoViewer.Android/Native/AndroidNativeRootView.cs:552`。
- 规范：`Design/spec/menu.md:18`（预览组含增强）；`Design/ui-contract.md:105` 已把增强列入 iOS 菜单却称「与 Mac 同序」，为旧文档矛盾。
- 说明：Avalonia 原版与 mac/win 同缺，故本条不按多数（3:2 反对），而按规范已收录＋三个视图开关同构、触屏已依赖该入口裁定为统一。

**B07**
- Avalonia `PhotoViewer/Views/Main/ImageView.axaml:130`「缩略图栏」；Windows `PhotoViewer.Windows/Native/WindowsMainWindow.cs:357`；`Plans/native-rebuild-windows-p2.md:397` 记录为有意改回。
- mac `MacMainWindow.cs:891`、iOS `iOSContextMenus.cs:56`、Android `AndroidNativeRootView.cs:548` 均为「胶片栏」。
- 规范 `Design/spec/menu.md:65` 明确不使用「缩略图栏」；分区名见 `Design/spec/01-regions.md:24`。

**B08**
- mac `MacMainWindow.cs:895`「工具」；Avalonia `ImageView.axaml:143` 同样无省略号（旧壳，不再作为依据）。
- Android `AndroidNativeRootView.cs:552`「增强预览」；iOS `iOSContextMenus.cs:60` 与 Windows `WindowsMainWindow.cs:377` 正确。
- 规范 `Design/spec/menu.md:65-66`（固定文案、带省略号）。

**B09**
- iOS `PhotoViewer.iOS/Native/iOSContextMenus.cs:88-90`：单行 `文件名 · yyyy-MM-dd HH:mm:ss zzz`，评分放子菜单。
- Android `PhotoViewer.Android/Native/AndroidThumbnailCard.cs:293-294`：两个普通菜单项，时间 `yyyy-MM-dd HH:mm`；`:299-303` 只处理 `id>=2`，标题行可点但无动作。
- 规范 `Design/spec/menu.md:56`（一行标题、时间格式、标题不可点击）。

**B10**
- iOS `PhotoViewer.iOS/Native/iOSNativeRootViewController.cs:667-674`：`_detailVisible || !_detailHighlight.Hidden` 先清定格，再判空态开文件夹。
- Android `PhotoViewer.Android/Native/AndroidNativeRootView.cs:455-462` 同序。
- 规范已有一半：`Design/spec/input.md:50`（INPUT-03 行内注明先解定格），但 `:115`（INPUT-10）只写空态点按开选择器。

**B28**
- 共享分支 `PhotoViewer/ViewModels/Main/FolderViewModel.cs:81-99`：iOS/Android 选文件夹，桌面选文件。
- Avalonia 文案已按平台切换 `PhotoViewer/ViewModels/Main/ImageViewModel.cs:230-231`；原生菜单文案 iOS `iOSContextMenus.cs:53`、Android `AndroidNativeRootView.cs:546` 均为「打开文件夹…」；空态提示 iOS `:1026`、Android `:884` 同为文件夹语义。
- 规范 `Design/spec/open.md:13-14`、`Design/spec/menu.md:13`；`Design/platforms.md:243` 已登记 OPEN-01@ios/@android。

**B29**
- mac `PhotoViewer.Mac/Native/MacMainWindow.cs:834-848`：按入参顺序逐项判目录/文件，首个命中即 `break`。
- Avalonia `PhotoViewer/App.axaml.cs:136-179`：同样按 `request.Items` 顺序取首个可打开项。
- iOS `PhotoViewer.iOS/Native/iOSNativeShell.cs:228-250`：按项顺序，目录或图片首个命中即返回（问题文本称「先目录后文件」与代码不符）。
- Windows `PhotoViewer.Windows/Native/WindowsMainWindow.cs:297-315`：先收集全部文件，有文件即 `OpenExternalFilesAsync`，否则取首个目录。
- Android `PhotoViewer.Android/Native/AndroidNativeShell.cs:216` 每个 item 单独 `PublishStorageItem`；`:153-169` 每个请求各自打开，实际是后项覆盖而非取首个。
- 规范 `Design/spec/open.md:19`。

**B37**
- 共享 `PhotoViewer/ViewModels/Main/FolderViewModel.cs:204-233` 仅在 `GetParentAsync()` 返回 null 时进 `OpenSingleImageAsync`（`:240-256`）；`:263-349` 枚举没有 try/catch。
- Mac `PhotoViewer.Mac/Core/MacStorageRefs.cs:27` 由路径推父目录，永不为 null，注释却写「枚举失败由既有单图回退处理」；iOS `PhotoViewer.iOS/Core/iOSStorageRefs.cs:40` 同构。
- Android `PhotoViewer.Android/Core/AndroidStorageRefs.cs:124-136`：无本地路径且无树 Uri 时返回 null（ACTION_VIEW/SEND 单文件即此路径）。
- Windows `PhotoViewer/Core/Storage/LocalStorageRefs.cs:50-54`：本地路径恒有父目录，单图模式基本不可达。
- 规范 `Design/spec/open.md:35`。

**C05**
- macOS 正确：`PhotoViewer.Mac/Native/MetalPhotoView.cs:227-237` 用 `HasPreciseScrollingDeltas` 分流，精确滚动平移。
- iOS 正确：`PhotoViewer.iOS/Native/iOSPhotoView.cs:167-176` 用 `AllowedScrollTypesMask = Discrete` 限定离散滚轮；`:436-449` 每个增量派发。
- Windows：`PhotoViewer.Windows/Native/WindowsMainWindow.cs:897-915` 只取 `MouseWheelDelta`，`PhotoViewer.Windows/Native/WindowsInput.cs:125-132` 也只有增量方向。
- Android：`PhotoViewer.Android/Native/AndroidPhotoView.cs:413-425` 任一 `Axis.Vscroll` 值即派发一次；`AndroidNativeRootView.cs:467-475` 直接查绑定。
- Avalonia 旧壳：`PhotoViewer/Views/Main/ImageView.axaml.cs:328-339` 所有滚轮都走命令。
- 规范 `Design/spec/input.md:62-68`。

**C06**
- iOS `PhotoViewer.iOS/Native/iOSNativeRootViewController.cs:303-323` 对任意执行成功的键启动计时器，`:735-757` 固定 350/60 ms 重复，不判命令是否可连续。
- Android `PhotoViewer.Android/AndroidNativeActivity.cs:72-83` 把 `KeyEventActions.Down` 与 `Multiple` 一并处理。
- mac `PhotoViewer.Mac/Native/MacMainWindow.cs:582-590` + `MacInput.cs:29-30`：直接用系统重复事件，未使用 `NSEvent.IsARepeat`。
- Windows `PhotoViewer.Windows/Native/WindowsMainWindow.cs:878-895`：未使用 `KeyRoutedEventArgs.KeyStatus.WasKeyDown`。
- Avalonia 旧壳 `PhotoViewer/Views/Main/ControlView.axaml.cs:76-97` 同样无重复过滤。
- 规范 `Design/spec/input.md:70-76`（可重复命令清单）。

**C07**
- `PhotoViewer.Android/Native/AndroidThumbnailCard.cs:264-286`：`ActionMasked == Up` 分支先判星级、否则 `_select?.Invoke`，无长按守卫；长按入口在 `:337`。
- 规范 `Design/spec/menu.md:59`（长按不得在抬起时被点选消费）。

**C08**
- Android `PhotoViewer.Android/Native/AndroidNativeRootView.cs:552-555`：增强与工具项无条件可用。
- iOS `PhotoViewer.iOS/Native/iOSNativeRootViewController.cs:683-686` 构建菜单时不带可用性。
- `PhotoViewer/Core/Image/PhotoLoadSession.cs:86-105`：无文件时 `ToggleEnhanced` 只改标志，无可见结果。
- 规范 `Design/spec/menu.md:73`。

**C19**
- `PhotoViewer/ViewModels/Main/FolderViewModel.cs:263-349`：清空 `_allFiles`、枚举、阶段 3 后台任务都没有代际或取消令牌；连续打开时两批结果可交错写入。
- mac 的 `_openGate` 只串行化平台入口（`MacMainWindow.cs:834-848`），不保护共享 VM。
- 规范 `Design/spec/open.md:81`。

**C20**
- `PhotoViewer/ViewModels/Main/FolderViewModel.cs:311`：`FilteredFiles.FirstOrDefault() ?? _allFiles.FirstOrDefault()`；后者是枚举首项，可能是被同名合并隐藏的伴侣。
- 代表文件按设置格式顺序选出 `PhotoViewer/ViewModels/Main/File/ThumbnailListViewModel.cs:203-261`；当前项映射只覆盖「代表仍在筛选结果内」的情况 `:293-307`。
- 规范 `Design/spec/open.md:43`（回退枚举首项）与 `:47`（首图与列表身份一致）自相矛盾。

**D06**
- 四端上下文菜单均无快捷键列；mac 仅系统菜单「设置…」带 `,`（`PhotoViewer.Mac/Native/MacMainWindow.cs:863`）。Avalonia 自绘菜单同样没有。
- 规范 `Design/spec/menu.md:67` 现为「应当」。

**D28**
- 指定目标在阶段 1 直接设为当前（`PhotoViewer/ViewModels/Main/FolderViewModel.cs:276-285`）；`ThumbnailListViewModel.cs:293-307` 只把同名伴侣映射回代表，不把目标塞进筛选结果。
- 规范 `Design/spec/open.md:44`（目标优先、不为它放宽条件）与 `:47`（目标不在筛选结果时没有卡片高亮）。

**D29**
- `PhotoViewer/ViewModels/Main/FolderViewModel.cs:144-158`：每次打开都 `ClearThumbnailClass()` 后 `LoadFolderAsync`，无同目录复用；`:263-349` 无 preferred 时首图落回筛选首项，当前照片丢失。
- 规范 `Design/spec/open.md:83`（视为刷新，尽量保持当前照片）。

**D30**
- 单图模式 `PhotoViewer/ViewModels/Main/FolderViewModel.cs:240-256`：`FolderName = file.Name`，列表仅一项，无状态标记。
- 四端文件栏均只显示计数，未渲染 `FolderName`：`PhotoViewer.Mac/Native/MacMainWindow.cs:665`、`PhotoViewer.Windows/Native/WindowsFilterBar.cs:102`、`PhotoViewer.iOS/Native/iOSFilterBar.cs:71`、`PhotoViewer.Android/Native/AndroidFilterBar.cs:71`。
- 规范 `Design/spec/open.md:35-36`。

## 随裁定需要同步的登记（供后续改文件时一并做）

- `platforms.md:217-220`：`MENU-01@mac/@win`（无增强）随 B06 代码补齐后删除；`MENU-06@mac/@android` 随 B08 删除；`MENU-05@android` 改为已登记差异（两行标题外观；不可点＋全时间由 B09 代码落实）。
- `platforms.md:226`：`INPUT-04@win/@android` 不再按「平台差异」保留，改为缺口（C05 修代码）。
- `platforms.md:228`：保留 `INPUT-05@ios` 的固定节奏差异，但重复范围按 C06 统一。
- `platforms.md:247`：`OPEN-08` 由「未登记，待裁定」改为缺口（C19 修共享 VM）。
- `menu.md:25` 引用的 `menu-01@ios / @android`、`menu-03@android`、`menu-04@ios`、`menu-05@ios / @android` 在 `platforms.md` 并不存在；本次只需补 `MENU-05@android`，其余仍待 menu 模块补登。
