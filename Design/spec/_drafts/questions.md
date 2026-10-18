# 待裁定问题汇总

来源：`Design/spec/_drafts/*-notes.md` 的 `## questions`（13 份）。已按类型去重；每条只保留一行，IDs 与证据行号放在条目内。已解决项标 `（已解决）`。

## A. ID 冲突 / 编号

- **A01（已解决）**：`state.md` 已收窄为 STATE-only；原混入的 SHELL / OPEN 小节不再存在，`open.md` 是 OPEN 唯一出处。
- **A02（已解决）**：ANALYSIS 编号按 `ANL-02/03/04 → ANALYSIS-02/03/04`、`ANL-01` 退役执行；`tokens.json` 的 7 处 `ANL-02/04` 已改为 `ANALYSIS-02/04`。
- **A03（已解决）**：`state.md`/`tokens.json` 中 `image.hint.*`、`status.toast.*` 的连字符写法已统一为点分 lowerCamelCase。
- **A04**：`Design/README.md:57` 与 `Design/ui-contract.md:92-95` 仍写 `ANL-01–04 → ANALYSIS-02–05`，与当前 `ANALYSIS-02–04` 映射不一致；规范侧已按新映射执行，总则与旧契约待修订。
- **A05（已解决）**：`ANALYSIS-05` 之后的新 ID 是否因 A04 的旧映射再顺移一位？当前按 `ANALYSIS-05` 保留（`Design/spec/analysis.md:55`）。
- **A06**：`OPEN-01` 曾在 shell/state/open 三处含义不同（`Design/spec/_drafts/open-notes.md:90`）；现已由 `open.md` 独占，若其他文档再引用 OPEN 前缀需回链。

## B. 四端行为不一致需裁定

- **B01 IMAGE-02@win**：Windows `ToggleFit` 只用视口中心，无对焦点优先；`SetImage` 也无对焦跟随，与共享 `PhotoViewport.ToggleFit`/`ApplyImage` 不一致。`PhotoViewer.Windows/Native/WindowsPhotoView.cs:475-483`、`:406-447`。
- **B02 IMAGE-04**：桌面可缩到适应倍率以下，iOS/Android 触屏最小档为适应态；是否统一方向未定。`PhotoViewer.Shared/PhotoViewport.cs:283`、`PhotoViewer.iOS/Native/iOSPhotoView.cs:564-566`、`PhotoViewer.Android/Native/AndroidPhotoView.cs:577`。
- **B03 IMAGE-06@android**：Android 导航图缩略图用 `AndroidNavigationDecoder` 重新解码，不经 GPU 小目标渲染，因此增强预览时导航图不体现增强。`PhotoViewer.Android/Native/AndroidNativeRootView.cs:905-929`。
- **B04 IMAGE-07**：只有 iOS/Android 有「计算中…」药丸，macOS/Windows 无对应控件；是只在慢平台显示还是四端统一。`PhotoViewer.iOS/Native/iOSNativeRootViewController.cs:222-232`、`PhotoViewer.Android/Native/AndroidNativeRootView.cs:170-176`。
- **B05 IMAGE-10@android**：Android 有当前文件且图未上屏时立即显示「正在载入…」，macOS/iOS/Windows 留空或只有错误详情。`PhotoViewer.Android/Native/AndroidNativeRootView.cs:877-881`。
- **B06 MENU-01@mac / @win**：macOS 与 Windows 主图菜单都没有「增强」项，iOS/Android 有。`PhotoViewer.Mac/Native/MacMainWindow.cs:885-899`、`PhotoViewer.Windows/Native/WindowsMainWindow.cs:370-382`、`PhotoViewer.iOS/Native/iOSContextMenus.cs:60`。
- **B07 MENU-06@win**：Windows 把「胶片栏」写作「缩略图栏」（有意改回旧壳文案）。`PhotoViewer.Windows/Native/WindowsMainWindow.cs:357`、`Plans/native-rebuild-windows-p2.md:397`。
- **B08 MENU-06@mac / @android**：macOS「工具」缺省略号；Android 用「增强预览」而规范为「增强」。`PhotoViewer.Mac/Native/MacMainWindow.cs:895`、`PhotoViewer.Android/Native/AndroidNativeRootView.cs:552`。
- **B09 MENU-05@android**：卡片菜单标题是两条普通可点菜单行，时间格式 `yyyy-MM-dd HH:mm`；iOS 为单行 `yyyy-MM-dd HH:mm:ss zzz`。`PhotoViewer.Android/Native/AndroidThumbnailCard.cs:293-294`、`PhotoViewer.iOS/Native/iOSContextMenus.cs:88-90`。
- **B10 INPUT-03 / INPUT-10**：iOS 主图点按在细节预览定格时先解除定格，不是每次都打开文件选择器；规则需为这一优先级留位置。`PhotoViewer.iOS/Native/iOSNativeRootViewController.cs:660-676`。
- **B11 SHELL-01@android**：手机纵向恒为行布局；设置里选「左右」时是否允许列布局未说明。`Plans/native-rebuild-phase-android.md:86`、`PhotoViewer.Android/Native/AndroidNativeRootView.cs:267`。
- **B12 SETTINGS-01@ios / @android**：移动端快捷键行只有勾选 + 名称（Android 另有上下按钮），既不显示主/次绑定也无录制。`PhotoViewer.iOS/Core/iOSNativeSettingsPresenter.cs:1469`、`PhotoViewer.Android/Native/AndroidNativeSettingsPresenter.cs:319-323`。
- **B13 SHELL-04 / SETTINGS-14**：栏位显隐只有 macOS/iOS 写平台偏好，Windows/Android 不持久化；相似面板展开态却进设置 JSON。`PhotoViewer.Mac/Native/MacMainWindow.cs:616-617`、`PhotoViewer.iOS/Native/iOSNativeRootViewController.cs:1042-1052`、`PhotoViewer/Core/Settings/SettingsModel.cs:28`。
- **B14 SETTINGS-07**：预设上限只有 macOS 校验 1600%；iOS 只校验 `>0`，Windows/Android 未设上限。`PhotoViewer.Mac/Native/MacNativeSettingsPresenter.cs:368`、`PhotoViewer.iOS/Core/iOSNativeSettingsPresenter.cs:1350-1365`。
- **B15 SETTINGS-10@ios / @android**：AI 页只有阈值/数量/即时计算，没有「清空特征数据库」；移动端永久不提供还是补开发者入口。`PhotoViewer.iOS/Core/iOSNativeSettingsPresenter.cs:1537-1571`、`PhotoViewer.Android/Native/AndroidNativeSettingsPresenter.cs:350-365`。
- **B16 STATE-03**：评分写回失败 macOS 用模态 `NSAlert`，iOS 只写 `Console.Error`，Windows/Android 用 4 s 提示条。`PhotoViewer.Mac/Native/MacMainWindow.cs:852-856`、`PhotoViewer.iOS/Native/iOSNativeRootViewController.cs:650`。
- **B17 TOOLS-01**：再次打开工具窗口的落点不一致：macOS 停在上次子页，iOS 每次 `BackToList()`，Android 先 `ShowList()`，Windows 恢复上次分类。`PhotoViewer.Mac/Native/MacMainWindow.cs:777`、`PhotoViewer.iOS/Native/iOSToolsPresenter.cs:193-196`、`PhotoViewer.Android/Native/AndroidNativeRootView.cs:610`。
- **B18 TOOLS-02@win**：Windows 分类顺序为 EXIF → 照片数据统计 → DINO，其他端为 EXIF → DINO →（统计）。`Plans/native-rebuild-windows-p2.md` §18.1。
- **B19 TOOLS-03**：EXIF 文本可选中复制只有 macOS 实现，iOS/Windows/Android 为不可选标签；规范按旧壳意图写成应当。`PhotoViewer.Mac/Native/MacExifDetailView.cs:497-502`、`PhotoViewer.iOS/Native/iOSExifDetailView.cs:370`。
- **B20 TOOLS-03@android**：Android 用 4 个内容列承载 7 列几何，没有分隔线列；是否接受为长期平台表现（平板亦然）。`PhotoViewer.Android/Native/AndroidToolsPresenter.cs:273-293`。
- **B21 TOOLS-05@win**：Windows DINO 为 3+2 等宽、第六格留空、刚体文本全宽；其他端为三列两行、第二行第三格放刚体文本。`Plans/native-rebuild-windows-p2.md` §18.3.2。
- **B22 TOOLS-01@win**：Windows 工具窗口不进任务栏、不可最大化（影响 EXIF 表拉宽阅读）；是登记为 `@win` 差异还是待改。`Plans/native-rebuild-windows-p2.md` §18.5。
- **B23 SIM-03@android**：动作按钮为 40×28 纯文本、进度仅文本，无 90×28 按钮外观与不定态进度条。`PhotoViewer.Android/Native/AndroidSimilarityPanel.cs:33-34`、`Plans/native-rebuild-phase-android.md:254`。
- **B24 SIM-02@android**：空态动作按钮在占位文字下方（其他端整体居中）；是否登记为平台差异。`Plans/native-rebuild-phase-android.md:254`。
- **B25 TOOLBAR-03 / TOOLBAR-04**：禁用态有 `Opacity=0.1`、`Enabled=false`、外观不变三套语言；评分快捷命令 macOS/iOS/Android 画数字、Windows 画星。`PhotoViewer.Windows/Native/WindowsControlBar.cs:319`、`:254-267`、`PhotoViewer.Mac/Native/MacControlBar.cs:61`。
- **B26 TOOLBAR-04 / TOOLBAR-05**：iOS 只把完整 EXIF 值与绑定放进 `AccessibilityLabel`，Android 连无障碍名称都没有；是否需要就地长按提示。`PhotoViewer.iOS/Native/iOSControlBar.cs:107`、`:248`、`PhotoViewer.Android/Native/AndroidControlBar.cs:228-272`。
- **B27 TOOLBAR-03@ios**：评分按钮 30×30 低于 44 点建议，控制栏没有卡片菜单式的补偿入口；是否登记差异。`PhotoViewer.iOS/Native/iOSControlBar.cs:83`。
- **B28 OPEN-01@ios / @android**：移动端只有文件夹选择器，菜单/空态文案仍写「打开」；是否把「打开文件」标为桌面专属。`PhotoViewer/ViewModels/Main/FolderViewModel.cs:81-99`。
- **B29 OPEN-04**：多项外部打开取舍顺序不同：Windows 先文件后目录，iOS/Android 先目录后文件，macOS 按路径顺序。`PhotoViewer.Windows/Native/WindowsMainWindow.cs:301-314`、`PhotoViewer.iOS/Native/iOSNativeShell.cs:236-245`。
- **B30 SCOPE-04@ios**：ImageIO 对 Sony ARW 只给内嵌预览，`IsProxy=false`，物理 100% 与细节预览以预览尺寸为基准；是否改 `CIRAWFilter`。`Plans/native-rebuild-phase-ipad.md:133`、`:182`。
- **B31 ANALYSIS-03@android**：手机纵向 6–7 张瓦片约 58dp，细节预览「可用但不适合多点操作」；是否接受为平台差异。`Plans/native-rebuild-phase-android.md:194`、`:253`。
- **B32 ANALYSIS-04@mac**：细节预览双击会清 `FollowsFocus`（Avalonia 保留），是否登记为 `ANALYSIS-04@mac`。`Plans/native-rebuild-phase-ipad.md:192`。
- **B33 FILTER-07@ios**：实现为 `alignLeading: true`，`Design/ui-contract.md:82` 仍写「行布局横向居中」；规范已按前导对齐表述，旧契约待改。`PhotoViewer.iOS/Native/iOSFilterBar.cs:97-99`、`Plans/native-rebuild-phase-ipad.md:149`。
- **B34 FILTER-07@win**：Windows 筛选条自带 `StackPanel` + 外边距 10，不消费共享 `FilterBarFrames`；列布局也没有额外 34 顶距。`PhotoViewer.Windows/Native/WindowsFilterBar.cs:64`。
- **B35 SCOPE-06@android**：设置数值用 `+/-`、列表用上下按钮排序、拖入未接；哪些算合法平台差异、哪些是缺口未定。`PhotoViewer.Android/Native/AndroidNativeSettingsPresenter.cs:270-273`、`:354-360`、`Plans/native-rebuild-phase-android.md:250`。
- **B36 FILTER-08@ios**：下拉控件可点区 90×32，低于系统 44 点建议，尚未登记为 filter 差异。`PhotoViewer.iOS/Native/iOSFilterBar.cs:43-79`。
- **B37 OPEN-03**：父目录不可访问时的单图模式判定未逐端核对；规范需平台确认后再写死触发条件。`PhotoViewer/ViewModels/Main/FolderViewModel.cs:204-256`。

## C. 规则与代码矛盾 / 疑似缺陷

- **C01 FILTER-05**：当前照片被筛选排除后，「下一张」从筛选列表第一张开始、「上一张」无动作，与"从原索引落到最近前/后项"冲突。`PhotoViewer/ViewModels/Main/File/ThumbnailListViewModel.cs:355-368`、`PhotoViewer/ViewModels/Main/ControlViewModel.cs:278-300`。
- **C02 FILTER-04**：`ApplyFilter` 把隐藏伴侣映射为代表卡片时提前 `return`，跳过末尾 `RaiseFilteredCountChanged()`，计数可能停在旧值。`PhotoViewer/ViewModels/Main/File/ThumbnailListViewModel.cs:298-309`。
- **C03 FILTER-02**：名称排序用当前区域文化字符串比较，不同系统语言下顺序可能不同，与"四端结果一致"冲突。`PhotoViewer/ViewModels/Main/File/ThumbnailListViewModel.cs:325`。
- **C04 FILTER-04**：计数取 `FilteredFiles.Count`，但改星落出筛选的当前照片被强制暂留，计数会比筛选条件多 1。`PhotoViewer/ViewModels/Main/File/ThumbnailListViewModel.cs:273`、`:309`。
- **C05 INPUT-04@win / @android**：`FromWheel` 与 Android 滚轮路径都不分精确/离散，触控板两指滚动会执行上一张/下一张。`PhotoViewer.Windows/Native/WindowsInput.cs:125-130`、`PhotoViewer.Android/Native/AndroidNativeRootView.cs:467-475`。
- **C06 INPUT-05**：macOS/Windows/Android 对所有按住键都重复，iOS 也按固定节奏重复；规则只允许可连续命令重复。`PhotoViewer.iOS/Native/iOSNativeRootViewController.cs:735-757`、`PhotoViewer.Android/AndroidNativeActivity.cs:72-83`。
- **C07 MENU-05@android**：卡片 `Up` 分支无长按守卫，长按弹菜单后抬手仍会走星级/整卡点选，与"不切主图"冲突。`PhotoViewer.Android/Native/AndroidThumbnailCard.cs:264-285`。
- **C08 MENU-07**：无照片时「增强」/「工具…」仍可直接执行，规则要求增强置灰、工具显示空态。`PhotoViewer.Android/Native/AndroidNativeRootView.cs:552-555`、`PhotoViewer.iOS/Native/iOSNativeRootViewController.cs:683-686`。
- **C09 SIM-10**：四端错误标签只挂在空态栈；「补齐全部」只在列表态可用，失败时错误不可见。`PhotoViewer.Mac/Native/MacSimilarityPanel.cs:124`、`PhotoViewer.Windows/Native/WindowsSimilarityPanel.cs:81`。
- **C10 SIM-07@win**：Windows 面板是文件栏 `Auto` 列，只有 `MinWidth 106`，未调用共享 `RowWidth` 的"可用宽一半"夹取。`PhotoViewer.Windows/Native/WindowsSimilarityPanel.cs:118`、`PhotoViewer.Windows/Native/WindowsMainWindow.cs:337`。
- **C11 TOOLBAR-08@win / @android**：Windows 无滚动宿主、Android 自绘视图被分区矩形约束；命令多或窗口小时超出部分不可达。`PhotoViewer.Windows/Native/WindowsMainWindow.cs:537-543`、`PhotoViewer.Android/Native/AndroidNativeRootView.cs:343-344`。
- **C12 TOOLBAR-11@android**：整栏一个 `View`，无子节点、无 `ContentDescription`、无 `Focusable`，TOOLBAR-11 的可读名称与焦点顺序无法成立。`PhotoViewer.Android/Native/AndroidControlBar.cs:194-304`。
- **C13 TOOLBAR-06@android**：`ui-contract.md` §4 仍写"EXIF 块打开工具页待 A5"，代码已接线 `ShowTools`，状态口径矛盾。`PhotoViewer.Android/Native/AndroidControlBar.cs:290-292`、`Design/ui-contract.md:156`。
- **C14 TOOLBAR-08**：四端都没有显式保存/恢复控制栏滚动偏移，规则却要求"普通重建不主动回到起点"。`PhotoViewer.Mac/Native/MacControlBar.cs:91-111`。
- **C15 SHELL-02@win**：自绘标题栏只有按钮与 tooltip，未见双击按系统偏好的处理。`PhotoViewer.Windows/Native/WindowsTitleBar.cs`、`PhotoViewer.Windows/Native/WindowsMainWindow.cs:390+`。
- **C16 SHELL-06@win**：自绘窗口按钮只有 `ToolTipService`，没有 `AutomationProperties.Name`。`PhotoViewer.Windows/Native/WindowsTitleBar.cs:157`。
- **C17 SHELL-02@win**：caption 边界在布局未完成时用常量兜底；该兜底是否算合规实现待判。`PhotoViewer.Windows/Native/WindowsTitleBar.cs:88-103`。
- **C18 OPEN-05（已裁定 10-18：显式打开回适应态，Windows/Android 为缺口）**：macOS/iOS 显式打开前调用 `FitToView()`，Windows/Android 不重置视口；`Design/spec/image.md:28` 又把"外部打开"列为保持倍率的切图入口。`PhotoViewer.Mac/Native/MacMainWindow.cs:827-828`、`PhotoViewer.iOS/Native/iOSNativeRootViewController.cs:785`。
- **C19 OPEN-08**：`FolderViewModel.LoadFolderAsync` 无代际/取消令牌，连续打开两个文件夹时旧枚举仍会写入共享集合，"后发起胜出"未实现。`PhotoViewer/ViewModels/Main/FolderViewModel.cs:263-349`。
- **C20 OPEN-04**：筛选为空时首图回退枚举首项，可能取到被同名合并隐藏的伴侣，导致首图与卡片身份不一致。`PhotoViewer/ViewModels/Main/FolderViewModel.cs:311`。
- **C21 TOOLS-06**：离开 DINO 页或关闭工具窗口不取消在途任务，只有下次 `SetSource`/`Dispose` 才取消，与"不可见即取消"冲突。`PhotoViewer/ViewModels/Tools/ToolsViewModel.cs:81-85`、`PhotoViewer/Core/AI/DinoDiagnosticPresenter.cs:106-112`。
- **C22 TOOLS-02 / TOOLS-03**：切图瞬间 EXIF 尚未加载完时 `ExifDetail` 被置空并把用户踢回首页，未区分「未加载」与「确实无 EXIF」。`PhotoViewer/ViewModels/Tools/ToolsViewModel.cs:91-107`。
- **C23 ANALYSIS-10**：无展示图或计算失败时回落到「未提取」；直方图占位固定「计算中」，面板隐藏/无图时可能长期显示。`PhotoViewer/Core/AI/AnalysisPresenter.cs:108`、`:419`、`:424-425`。
- **C24 ANALYSIS-08@mac**：诊断瓦片吞掉自身范围内的左键，点在 letterbox/角标不改变准星也不清除；规范要求瓦片空白等同面板空白。`PhotoViewer.Mac/Native/MacDiagnosticTile.cs:11`、`PhotoViewer.Mac/Native/MacAnalysisPanel.cs:107`。
- **C25 STATE-01**：加载提示延迟只有设计意图，四端都没有延迟参数或状态。`PhotoViewer/Core/Image/PhotoLoadSession.cs`、`Design/spec/_drafts/input-notes.md:48`。
- **C26 IMAGE-04@win**：预设到端点时退回 `presets[0]`/`presets[^1]`，低于最小预设时按"缩小"可能反而放大到 12.5%。`PhotoViewer.Windows/Native/WindowsMainWindow.cs:983-1002`。
- **C27 SETTINGS-14**：保存失败只写控制台；JSON 损坏时按默认值启动并在 400 ms 后写回，等于覆盖用户原配置。`PhotoViewer/ViewModels/Settings/SettingsViewModel.Persistence.cs:60-72`、`PhotoViewer/Core/Settings/SettingsService.cs:40-57`。
- **C28 SETTINGS-07**：允许删到空预设；重启读取时因 `Count > 0` 才覆盖默认，空列表又变回 8 个默认挡。`PhotoViewer/ViewModels/Settings/SettingsViewModel.Persistence.cs:161-164`、`PhotoViewer/ViewModels/Main/ImageViewModel.cs:780-800`。
- **C29 IMAGE-03 / IMAGE-10**：Full 请求失败时 `PhotoLoadSession` 设 `Error`，但屏幕上有可用代理，Mac 仍显示「无法打开该图片」。`PhotoViewer/Core/Image/PhotoLoadSession.cs:230-234`、`PhotoViewer.Mac/Native/MacMainWindow.cs:700-703`。
- **C32 IMAGE-01**：源尺寸元数据缺失时以代理尺寸作几何基准、Full 到达后纠正一次，会产生一次位置/倍率跳变；是否允许。`PhotoViewer/ViewModels/Main/ImageViewModel.cs:89-91`、`:117-121`。

## D. 待作者决策

- **D01 SIM-04**：柱状条下限固定 75%；阈值可调到 75% 时所有柱长为 0，阈值 95% 时最多到 80%。柱条下限是否跟随阈值或改为绝对百分比。`PhotoViewer/Core/AI/SimilarityService.cs`、`PhotoViewer/ViewModels/Settings/SettingsViewModel.AI.cs:17`。
- **D02 SIM-03**：批量提取明确"本期不可中断"，无取消入口；是否新增取消/暂停（与 PRIN-04 的关系）。`PhotoViewer/ViewModels/Main/File/SimilarityPanelViewModel.cs:369-372`。
- **D03 SIM-05**：从相似面板跳到某张后关闭再打开，锚点重置为当前照片；是否要记住面板浏览起点。`PhotoViewer/ViewModels/Main/File/SimilarityPanelViewModel.cs:271-276`。
- **D04 SIM-01**：文件栏隐藏时 toggle 仍可点亮，恢复文件栏后突然展开；是否禁用/置灰。`PhotoViewer.Mac/Native/MacMainWindow.cs:437`。
- **D06 MENU-06**：四端上下文菜单都不显示快捷键列，macOS 只有「设置…」标 `⌘,`；"应当显示绑定"是否降为"可以"。`PhotoViewer.Mac/Native/MacMainWindow.cs:863`。
- **D07 SETTINGS-03 / PRIN-09**：macOS 强制 DarkAqua、Windows 固定深色 + Mica，iOS/Android 跟随系统；设置页是否允许浅色。`PhotoViewer.Mac/Native/SwiftSettings/MacSettingsUI.swift:304`。
- **D08 TOOLS-01**：工具窗口尺寸没有令牌域（macOS 880×720/min 560×420，Windows 960×720/min 800×560）；新增 `tools.window.*` 还是并入 `shell.*`。`PhotoViewer.Mac/Native/MacToolsWindow.cs:20-28`、`Plans/native-rebuild-windows-p2.md` §18.1。
- **D09 TOOLS-07**：照片统计默认通配符只有 `*.HIF,*.JPG`（不含 RAW），CSV 不写路径，同名文件无法区分。`PhotoViewer/ViewModels/Tools/PhotoStatsViewModel.cs:20`、`PhotoViewer/Core/Tools/PhotoStatsService.cs:73-82`。
- **D10 TOOLBAR-12**：四端都没有开关型命令按钮的按下态/强调色，状态只在菜单勾选；是否要统一表达。`PhotoViewer.Mac/Native/MacControlBar.cs:73-79`、`PhotoViewer.Windows/Native/WindowsControlBar.cs:273-286`。
- **D12 SETTINGS-09**：EXIF 11 项里没有 `CameraMake`，但 `FormatExifValue` 支持它；是漏项还是有意只暴露型号。`PhotoViewer/ViewModels/Settings/SettingsViewModel.ExifDisplay.cs:37-48`、`PhotoViewer/ViewModels/Main/ControlViewModel.cs:258`。
- **D13 SHELL-03**：560×480 是 100% 文本缩放值；Windows 125/150/200% 文本缩放、macOS 大字体、Android fontScale 1.3 下是否仍按逻辑点。`Design/spec/shell.md:37`。
- **D14 SHELL-06**：布局切换、全屏切换本就没有过渡动画，"减少动态效果直接到位"是否只是声明性条款。`Design/spec/shell.md:68`。
- **D15 SHELL-06**：全屏失败、恢复窗口帧失败四端都没有错误路径；新增要求还是删除。`Design/spec/shell.md:70`。
- **D16 SHELL-05**：观片环境固定深色，但设置/工具窗口用 Mica 会随系统主题；边界是"主窗口固定、设置/工具跟随"还是全部固定。`Design/spec/shell.md:62`。
- **D17 SHELL-02**：`AppleActionOnDoubleClick` 的 `Zoom` 在部分 macOS 版本是"缩放"而非"最大化"，文案是否改为"缩放/最大化"。`PhotoViewer.Mac/Native/MacMainWindow.cs:223-246`。
- **D18 SHELL-02**：iPad 隐藏状态栏依赖根控制器；若未来显示状态栏，规范需允许安全区自动让位。`PhotoViewer.iOS/Native/iOSNativeRootViewController.cs`。
- **D19 FILTER-09**：排序键/方向/星级筛选每次启动回默认；是否把"选片进度不丢失"延伸到筛选条件。`PhotoViewer/ViewModels/Main/File/FilterBarViewModel.cs:51-70`、`PhotoViewer/Core/Settings/SettingsModel.cs:9-47`。
- **D20 FILTER-02**：名称与大小都取代表文件，隐藏伴侣不参与排序、大小不累加；"文件大小"对 RAW+JPG 组只反映代表格式。`PhotoViewer/ViewModels/Main/File/ThumbnailListViewModel.cs:325-334`。
- **D21 FILTER-03**：同名合并关闭时「星级冲突」项始终存在且恒为空；是否隐藏/禁用。`PhotoViewer/ViewModels/Main/File/FilterBarViewModel.cs:20-25`。
- **D22 FILTER-03**：`ShowRating` 关闭后星级筛选仍可用，用户可在看不到星级时按星级筛选；FILTER-03 以"应当"保留。`PhotoViewer/ViewModels/Settings/SettingsViewModel.Rating.cs:12`。
- **D23 FILTER-04**：macOS 计数宽度与旧壳基线有约 3.5 点差异；是已登记视觉差异还是待修。`Plans/native-rebuild-phase-3.md:73`。
- **D24 SCOPE-01**：照片数据统计是平台专属（可以）还是待补齐缺口（应当）。`PhotoViewer/ViewModels/Tools/ToolsViewModel.cs:35`。
- **D25 SCOPE-01**："延期功能"分支目前没有活跃实例；保留为前瞻约定还是收敛为"平台能力差异 + 配置保留"。`Design/spec/scope.md:9-13`。
- **D26 SCOPE-02 / SCOPE-04**：增强被内存压力自动关闭时无提示；是否补可见提示。`PhotoViewer/Core/Image/PhotoLoadSession.cs:313-321`。
- **D27 SETTINGS-10**：清库实际是三层确认（共享 3 秒 arm + 系统确认），arm 态对用户不可见；规定一次系统确认还是保留两段。`PhotoViewer/ViewModels/Settings/SettingsViewModel.AI.cs:115-140`、`PhotoViewer.Windows/Native/WindowsSettingsPages.Ai.cs:87-124`。
- **D28 OPEN-04**：打开的目标不满足当前星级筛选时主图显示目标但列表没有它；是有意的"目标优先"还是应临时放宽/标记。`PhotoViewer/ViewModels/Main/File/ThumbnailListViewModel.cs:293-307`。
- **D29 OPEN-08**：同一文件夹重复打开没有复用快捷路径，会重新枚举并清缩略图缓存；"视为刷新"是否是期望行为。`PhotoViewer/ViewModels/Main/FolderViewModel.cs:144-158`。
- **D30 OPEN-03**：单图模式只把 `FolderName` 设为文件名，界面没有"单图模式"标记；是否需要显式状态。`PhotoViewer/ViewModels/Main/FolderViewModel.cs:245`。
- **D32 ANALYSIS-01 / IMAGE-03**：分析栏可见即要求 Full，开启分析 + 50 连翻 p50 447 ms / p95 2660 ms；性能代价是否接受。`Plans/native-rebuild-phase-ipad.md:186`、`PhotoViewer/ViewModels/Main/ImageViewModel.cs:64`。
- **D36 SCOPE-04**：iOS CoreML EP 不可用、Android P3 回退 sRGB；是否按"能力缺失如实呈现"长期接受。`Design/spec/scope.md:47-51`。
