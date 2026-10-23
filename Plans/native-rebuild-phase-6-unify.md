# Phase 6C · 四端设计统一清单

目的：把 2026-10-18 的 103 条设计裁定与 34 项令牌对账落成可执行的代码清单。规范侧（`Design/spec/`、`tokens.json`、`platforms.md`、`status.md`）已同步；本文件只列**代码改动**，按平台归口、按优先级排序。

来源：
- 问题：[`questions.md`](native-rebuild-phase-6-rulings/questions.md)
- 裁定：[G1](native-rebuild-phase-6-rulings/rulings-G1.md) · [G2](native-rebuild-phase-6-rulings/rulings-G2.md) · [G3](native-rebuild-phase-6-rulings/rulings-G3.md) · [G4](native-rebuild-phase-6-rulings/rulings-G4.md) · [G5](native-rebuild-phase-6-rulings/rulings-G5.md) · [G6](native-rebuild-phase-6-rulings/rulings-G6.md) · [复核改判](native-rebuild-phase-6-rulings/rulings-overrides.md)
- 规则与编号：[`Design/README.md`](../Design/README.md)；平台登记：[`platforms.md`](../Design/platforms.md)；状态：[`status.md`](../Design/status.md)

方法：分类取 **多数**（Avalonia 原版 + 四端实现）、**更优**（按 L0 原则）、**原版**（兜底）；只有输入方式或平台能力确有优劣差别时才登记为 `ID@平台`，其余一律同标准。桌面与移动可分开执行，但不得把"某端现状"当成标准。每段内按 **功能/行为缺陷 → 视觉令牌** 排序；不同平台可并行。

## 共享层

| # | 设计 ID | 改什么 | 入口 file:line | 来源 Q |
|---|---|---|---|---|
| S1 | FILTER-05 | 当前照片被筛掉后，上一张/下一张以重算前索引落到最近前/后项；新增共享 `MoveCurrent` 供三端改调 | `PhotoViewer/ViewModels/Main/File/ThumbnailListViewModel.cs:355`、`PhotoViewer/ViewModels/Main/ControlViewModel.cs:278` | C01 |
| S2 | FILTER-04 | 映射隐藏伴侣到代表卡片后不再提前 `return`，计数通知必发 | `PhotoViewer/ViewModels/Main/File/ThumbnailListViewModel.cs:298` | C02 |
| S3 | FILTER-02 | 名称排序改 `OrdinalIgnoreCase`，四端同序 | `PhotoViewer/ViewModels/Main/File/ThumbnailListViewModel.cs:325` | C03 |
| S4 | FILTER-04 | 计数包含 FILM-06 暂留项，列表与文本同帧一致 | `PhotoViewer/ViewModels/Main/File/ThumbnailListViewModel.cs:273`、`PhotoViewer/ViewModels/Main/File/FilterBarViewModel.cs:89` | C04 |
| S5 | OPEN-08 | `LoadFolderAsync` 加打开代际；每个 await 后校验，旧枚举与属性结果不写入 | `PhotoViewer/ViewModels/Main/FolderViewModel.cs:263` | C19 |
| S6 | OPEN-04 | 筛选为空时回退到枚举首项所属合并组的代表文件 | `PhotoViewer/ViewModels/Main/FolderViewModel.cs:311` | C20 |
| S7 | OPEN-08 | 同目录重开：以当前照片为 preferred 重载，不清缩略图缓存 | `PhotoViewer/ViewModels/Main/FolderViewModel.cs:144` | D29 |
| S8 | OPEN-03 | 枚举被拒/失败捕获异常，回退单图模式 | `PhotoViewer/ViewModels/Main/FolderViewModel.cs:263` | B37 |
| S9 | INPUT-05 | 命令元数据加 `Repeatable`（仅上一张/下一张/预设缩放），四端重复入口按此设闸 | `PhotoViewer.Shared/InputCommandMap.cs` | C06 |
| S10 | IMAGE-02 | 共享视口作为唯一判定源：Fit 对焦优先、切图跟随、`SetFocus` 可被壳调用 | `PhotoViewer.Shared/PhotoViewport.cs:142` | B01 |
| S11 | IMAGE-03 / IMAGE-10 | `PhotoLoadSession` 拆分 `FullError` 与 `Error`；有代理时只报"无法载入全分辨率" | `PhotoViewer/Core/Image/PhotoLoadSession.cs:230` | C29 |
| S12 | STATE-01 | 会话提供延迟加载态：切图清旧图，`status.loading.delay`（300ms）后才暴露 loading | `PhotoViewer/Core/Image/PhotoLoadSession.cs:64` | C25、B05 |
| S13 | IMAGE-07 | 增强计算态加 500ms 门槛（`image.busy.delay`），结束即收 | `PhotoViewer/Core/Image/PhotoLoadSession.cs` 增强状态 | B04 |
| S14 | SCOPE-02 | 内存 Warning 自动关闭增强时发一次性原因事件，供 STATE-03 提示条消费 | `PhotoViewer/Core/Image/PhotoLoadSession.cs:313` | D26 |
| S15 | TOOLS-06 | 提供 `Cancel` / `Deactivate`：离开 DINO 页或关闭工具窗即取消在途任务 | `PhotoViewer/ViewModels/Tools/ToolsViewModel.cs:81`、`PhotoViewer/Core/AI/DinoDiagnosticPresenter.cs:104` | C21 |
| S16 | TOOLS-02 | `SyncCurrentFile` 用 `IsExifLoaded` 区分「未加载」与「无 EXIF」，加载中留页 | `PhotoViewer/ViewModels/Tools/ToolsViewModel.cs:91` | C22 |
| S17 | TOOLS-01 | `OpenToolsWindow` 仅在未打开时回首页；已打开只前置并保留当前页 | `PhotoViewer/ViewModels/Main/MainViewModel.cs:143` | B17 |
| S18 | ANALYSIS-07 / ANALYSIS-10 | 无展示图清空直方图占位；失败态独立文案，不停在"计算中" | `PhotoViewer/Core/AI/AnalysisPresenter.cs:108`、`:419` | C23 |
| S19 | SIM-10 | 列表态动作区也有错误承载，错误不再只挂空态栈 | `PhotoViewer/ViewModels/Main/File/SimilarityPanelViewModel.cs` | C09 |
| S20 | SETTINGS-07 | `RemoveScalePreset` 保底一项：剩 1 项时删除无效 | `PhotoViewer/ViewModels/Settings/SettingsViewModel.ImagePreview.cs:39` | C28 |
| S21 | SETTINGS-07 | 预设范围统一 >0 且 ≤1600，非法不写入并恢复原值 | `PhotoViewer/ViewModels/Settings/SettingsViewModel.ImagePreview.cs:92` | B14 |
| S22 | SETTINGS-14 | 读取失败先把原文档改名 `settings.json.corrupt-<时间戳>`，再生效默认值；失败状态可观察 | `PhotoViewer/Core/Settings/SettingsService.cs:107`、`PhotoViewer/ViewModels/Settings/SettingsViewModel.Persistence.cs:60` | C27 |
| S23 | SETTINGS-10 | 提供"已确认"清库入口（不带隐形 3 秒 arm），供有系统确认的壳直调 | `PhotoViewer/ViewModels/Settings/SettingsViewModel.AI.cs:126` | D27 |
| S24 | SETTINGS-09 | 删除 `CameraMake` 死分支（11 项口径不变） | `PhotoViewer/ViewModels/Main/ControlViewModel.cs:258`、`PhotoViewer/Views/Main/ControlView.axaml.cs:219` | D12 |
| S25 | TOOLS-07 | CSV 末列追加「文件夹」，前三列不动 | `PhotoViewer/Core/Tools/PhotoStatsService.cs:73` | D09 |
| S26 | IMAGE-04 | 预设查询改可空：端点与未命中直接不动作，Windows 复用 | `PhotoViewer/ViewModels/Main/ImageViewModel.cs:769` | C26 |

### 共享层进度（10-18 完成）

> 下表的 `file:line` 是当轮入口记录；其中 `PhotoViewer/Views/...`、`ImageViewModel.cs` 等旧壳文件已于 2026-10-19 随 6D 删除，保留只为追溯（等价实现见 `PhotoViewer.Shared/PhotoViewport.cs`、`Core/Exif/ExifDisplayConverters.cs` 等）。

S1–S26 已落地。验证：Mac `--pv-native-smoke` 基线与 layout/input/filmstrip/toolbar/analysis/similarity/tools/settings 九套 PASS；iOS 模拟器、Android Debug 构建通过且无新增警告。已顺带改好四端 `Move` → `ThumbnailList.MoveCurrent`、`ZoomPreset` → `ZoomPresets.Next`（含 W4）。各壳还需接线的新 API：

| API | 用途 | 壳侧接线 |
|---|---|---|
| `PhotoLoadSession.IsLoadingVisible` | STATE-01 300ms 延迟加载提示 | M/I/A3/W：替换各自"立即显示/不显示"的加载文案 |
| `PhotoLoadSession.FullError` | IMAGE-03 有代理时的非阻塞提示 | M4/I3/W6：走提示条，不覆盖主图错误标题 |
| `PhotoLoadSession.IsEnhancingVisible` | IMAGE-07 500ms 药丸门槛 | M3/I4/A21/W5 |
| `PhotoLoadSession.Notice` | SCOPE-02 内存自动关闭增强提示 | 四端提示条订阅 |
| `InputCommandMap.IsRepeatable` | INPUT-05 按住重复设闸 | W3/A8；Mac、iOS 键盘重复入口同补 |
| `ToolsViewModel.IsPresented` / `Deactivate()` | TOOLS-01 已开留页、TOOLS-06 关闭取消 | Mac 已写 `IsPresented`，`ShowTools` 需改调 `_main.OpenToolsWindow()`（M5）；I6/I9、A12、W14 写入标志并在关闭时 `Deactivate()` |
| `SimilarityPanelViewModel.ShowActionAreaError` | SIM-10 列表态错误 | Mac 补一项；I10/A15/W15 |
| `SettingsViewModel.CanRemoveScalePreset` / `IsValidScalePresetPercent` / `PersistenceStatus` / `ClearFeatureDatabaseConfirmedAsync()` | SETTINGS-07/14/10 | M9/W16/I12/I14/A17/A19；原生设置是工作副本，`PersistenceStatus` 要从源 VM 取 |

遗留：S8 纯文件夹枚举失败时共享层没有可见错误状态，现只落 `Console.Error` 并保持空态；需要可见提示时另立条目（STATE-02）。

## macOS

**进度（10-18 完成）**：M1–M11 全部完成并已合入 `refactor/native-rebuild` 集成分支；另补 INPUT-05（按键重复只放行 Repeatable 命令）、STATE-01（加载提示绑 300ms 延迟态）、SCOPE-02（内存自动关闭增强的提示条）、TOOLS-06（关闭工具窗即取消 DINO）、SIM-10（列表态错误标签）、STATE-03（新提示条组件 `MacStatusToast`）。验证：2026-10-18 `--pv-native-smoke` 基线 PASS 39，layout / input / filmstrip / toolbar / analysis / similarity / tools / settings 八套 PASS。

| # | 设计 ID | 改什么 | 入口 file:line | 来源 Q |
|---|---|---|---|---|
| M1 | MENU-01 / MENU-06 | 主图菜单栏位组后补「增强」勾选；系统「显示」菜单补分析栏与增强；「工具」改为「工具…」 | `PhotoViewer.Mac/Native/MacMainWindow.cs:885`、`:866`、`:895` | B06、B08、C08 |
| M2 | STATE-03 | 评分写回失败由模态 `NSAlert` 改为 4 s 非模态提示条，星级保持原值 | `PhotoViewer.Mac/Native/MacMainWindow.cs:852` | B16 |
| M3 | IMAGE-07 | 补「计算中…」药丸并接共享 500ms 门槛（现无药丸） | `PhotoViewer.Mac/Native/MacMainWindow.cs:700` | B04 |
| M4 | IMAGE-03 | 有代理时不覆盖「无法打开该图片」，只显示 `FullError` 提示 | `PhotoViewer.Mac/Native/MacMainWindow.cs:700` | C29 |
| M5 | TOOLS-01 | 从主窗口打开工具窗时复位到列表首页；已打开只前置 | `PhotoViewer.Mac/Native/MacToolsWindow.cs:100`、`MacMainWindow.cs:777` | B17 |
| M6 | TOOLS-05 | 刚体拟合文本改为网格下方整宽块，不再占第三列瓦片格 | `PhotoViewer.Mac/Native/MacDinoDiagnosticView.cs:248` | B21 |
| M7 | ANALYSIS-08 | 瓦片内 letterbox/角标点击等同面板空白：清准星、保留 cosine 参考点 | `PhotoViewer.Mac/Native/MacDiagnosticTile.cs:266`、`MacAnalysisPanel.cs:107` | C24 |
| M8 | TOOLBAR-08 | 补滚动偏移保持的验收断言（重建不回到起点，换向夹取） | `PhotoViewer.Mac/Native/MacControlBar.cs:91`、`MacMainWindow.cs:498` | C14 |
| M9 | SETTINGS-14 / SETTINGS-10 | 设置页顶部加一行失败/备份状态；清库按钮直调共享"已确认"入口，去掉隐形 arm | `PhotoViewer.Mac/Native/MacNativeSettingsPresenter.cs:15`、`:376` | C27、D27 |
| M10 | TOOLBAR-04 | 命令提示补当前绑定（现只有 tooltip） | `PhotoViewer.Mac/Native/MacControlBar.cs:99` | B26 |
| M11 | FILM-05 | 清零叉改为显式 `color.star.clear` #666666，不再取系统次级色 | `PhotoViewer.Mac/Native/MacThumbnailItem.cs:99` | G6-31 |

## Windows

**进度（2026-10-19 完成并合入）**：W1–W19 全部改完，原先在 `native-rebuild-win`（提交 e695a8b）做的类型检查（0 错误），已由作者在 Windows 本机验收后合入 `refactor/native-rebuild`（随 6C 收口）。W1 链接 `PhotoViewer.Shared`、视口改用共享 `PhotoViewport` 后，下列数值随令牌变化：

- 导航图：圆角 8→6、可见框描边 2→1、宿主 alpha→0.6
- 胶片带高 148→159
- 当前项描边 `#1E90FF`→`#0A84FF`；星色 `#FFD700`→`#FFD214`
- 筛选 toggle 高 32→27
- EXIF 单元格内边距 8/6→10/8；EXIF 值文字 `#FFFFFF`→`#EEEEEE`
- 相似面板列布局宽 106→110
- 提示条字色 `#808080`→`#FFFFFF`，宽取 min(500, 可用宽)

| # | 设计 ID | 改什么 | 入口 file:line | 来源 Q |
|---|---|---|---|---|
| W1 | 架构 | **链接 `PhotoViewer.Shared`，用共享几何/视口替换 `WindowsLayout` 的重复常量**（一项，子项如下）： | `PhotoViewer.Windows/PhotoViewer.Windows.csproj`、`PhotoViewer.Windows/Native/WindowsLayout.cs:14-590` | G6 汇总、C10、C11 |
| | | - 视口改共享 `PhotoViewport`：`ToggleFit` 对焦优先、`SetImage` 跟随对焦 | `PhotoViewer.Windows/Native/WindowsPhotoView.cs:475`、`:406` | B01 |
| | | - 导航图几何（圆角/frame/alpha）取 `nav.*`、`color.nav.*` | `WindowsLayout.cs:165`、`:168`、`:180`、`WindowsNavigator.cs:59` | G6-07~09 |
| | | - 卡片、星级、当前项描边取 `film.*`、`color.star.*`、`color.film.card.*` | `WindowsLayout.cs:414`、`:420`、`:429`、`:432`、`:435` | G6-27~31 |
| | | - 筛选 toggle 高取 `filter.toggle.height`（27）与四色 | `WindowsLayout.cs:25`、`:385`、`:394`、`:397`、`:400` | G6-01、G6-23~26 |
| | | - EXIF 表内边距取 `exiftable.cell.padding*`（10/8）并复核 7 列最小宽 | `WindowsLayout.cs:556`、`:559`、`WindowsExifToolPage.cs:442` | G6-21、G6-22 |
| | | - 相似面板行布局改共享 `RowWidth`，列宽/MaxWidth 都夹到可用宽一半 | `WindowsSimilarityPanel.cs:118`、`WindowsMainWindow.cs:337` | C10 |
| | | - 控制栏/分区几何改消费 `ControlBarLayout` / `MainScreenLayout`，删除并行常量 | `WindowsLayout.cs:216-252`、`WindowsControlBar.cs` | C11、G6 汇总 |
| W2 | MENU-06 / MENU-01 | 栏位项文案改回「胶片栏」；菜单补「增强」勾选与无照片禁用 | `PhotoViewer.Windows/Native/WindowsMainWindow.cs:357`、`:342` | B07、B06、C08 |
| W3 | INPUT-04 / INPUT-05 | 滚轮按精度分流（精确平移、离散一格一命令）；按键重复按 `Repeatable` 设闸 | `PhotoViewer.Windows/Native/WindowsInput.cs:125`、`WindowsMainWindow.cs:878`、`:897` | C05、C06 |
| W4 | IMAGE-04 | 预设到端点不再回退端点档，未命中直接返回 | `PhotoViewer.Windows/Native/WindowsMainWindow.cs:983` | C26 |
| W5 | IMAGE-07 | 补「计算中…」药丸并接共享 500ms 门槛 | `PhotoViewer.Windows/Native/WindowsMainWindow.cs:852` | B04 |
| W6 | IMAGE-03 | 有代理时不覆盖主图错误标题，只报 `FullError` | `PhotoViewer.Windows/Native/WindowsMainWindow.cs:857` | C29 |
| W7 | OPEN-01 | 多目标投递改为按入参顺序取首个可打开项，其余忽略 | `PhotoViewer.Windows/Native/WindowsMainWindow.cs:297` | B29 |
| W8 | SHELL-04 | 文件栏/控制栏显隐写平台偏好（`PhotoViewer.Native.ShowFilm/ShowControls`） | `PhotoViewer.Windows/Native/WindowsMainWindow.cs:597` | B13 |
| W9 | TOOLBAR-08 | 控制栏包 `ScrollViewer`，保存并夹取滚动偏移 | `PhotoViewer.Windows/Native/WindowsMainWindow.cs:537` | C11、C14 |
| W10 | TOOLBAR-03 | 无照片改系统禁用态（去 `Opacity=0.1`）；评分快捷命令画数字（0=叉） | `PhotoViewer.Windows/Native/WindowsControlBar.cs:319`、`:254` | B25 |
| W11 | TOOLS-01 | 工具窗改普通窗口：进任务栏/Alt-Tab、可最大化（设置窗保持不变） | `PhotoViewer.Windows/Native/WindowsToolsWindow.cs:136`、`WindowsWindowChrome.cs:25` | B22 |
| W12 | TOOLS-02 | 分类顺序改 EXIF → DINO → 照片数据统计，页面索引不动 | `PhotoViewer.Windows/Native/WindowsToolsWindow.cs:18` | B18 |
| W13 | TOOLS-03 | Tag ID 列开文本选择，四类文本一致可复制 | `PhotoViewer.Windows/Native/WindowsExifToolPage.cs:385` | B19 |
| W14 | TOOLS-06 | 关闭工具窗即取消在途 DINO 任务 | `PhotoViewer.Windows/Native/WindowsToolsWindow.cs:328` | C21 |
| W15 | SIM-10 | 列表态动作区补错误标签 | `PhotoViewer.Windows/Native/WindowsSimilarityPanel.cs:81` | C09 |
| W16 | SETTINGS-14 / SETTINGS-10 / SETTINGS-07 | 设置页顶部失败状态行；清库按钮直调共享"已确认"入口（去隐形 arm）；预设删到剩 1 项时禁用删除 | `PhotoViewer.Windows/Native/WindowsSettingsPages.Ai.cs:115`、`WindowsSettingsPages.Preview.cs:114` | C27、D27、C28 |
| W17 | SHELL-06 | 自绘窗口按钮补悬停提示与可点击语义 | `PhotoViewer.Windows/Native/WindowsTitleBar.cs:157` | C16 |
| W18 | TOOLBAR-04 | 命令提示含主/次绑定（沿用 `AppGesture.ToDisplayString()`） | `PhotoViewer.Windows/Native/WindowsControlBar.cs:290` | B26 |
| W19 | STATE-03 | 提示条文字改 `color.status.toast.foreground` #FFFFFF（现 #808080 不达 AA），宽取 min(500, 可用宽) | `PhotoViewer.Windows/Native/WindowsLayout.cs:459`、`:145` | G6-20、G6-17 |

## iOS·iPadOS

**进度（10-18 完成）**：I1–I19 全部完成并已合入 `refactor/native-rebuild` 集成分支；另补 INPUT-05（按键重复只放行 Repeatable 命令）、STATE-01（加载提示绑 300ms 延迟态）、SCOPE-02（内存自动关闭增强的提示条）。验证：2026-10-18 iPad mini (A17 Pro) 模拟器 photo PASS 10、layout PASS 21、input PASS 31、analysis PASS 18、similarity PASS 10、tools PASS 8、gesture PASS 22，均 0 FAIL。

| # | 设计 ID | 改什么 | 入口 file:line | 来源 Q |
|---|---|---|---|---|
| I1 | MENU-07 | 无照片时「增强」action 置灰；「工具…」仍开空态 | `PhotoViewer.iOS/Native/iOSNativeRootViewController.cs:683` | C08 |
| I2 | STATE-03 | 评分写回失败补 4 s 非模态提示条，不再只写 `Console.Error` | `PhotoViewer.iOS/Native/iOSNativeRootViewController.cs:647` | B16 |
| I3 | IMAGE-03 | 有代理时只显示 `FullError`，不覆盖「无法打开该图片」 | `PhotoViewer.iOS/Native/iOSNativeRootViewController.cs:1025` | C29 |
| I4 | IMAGE-07 | 药丸接共享 500ms 门槛（现计算态立即显示） | `PhotoViewer.iOS/Native/iOSNativeRootViewController.cs:223` | B04 |
| I5 | TOOLBAR-08 | 控制栏 `Arrange` 后写 `ContentSize`，保存并夹取滚动偏移 | `PhotoViewer.iOS/Native/iOSNativeRootViewController.cs:501` | C11、C14 |
| I6 | TOOLS-01 | 从主窗口打开工具页时复位到列表；已打开只保留当前页 | `PhotoViewer.iOS/Native/iOSToolsPresenter.cs:48` | B17 |
| I7 | TOOLS-03 | EXIF 标签文本改可选择复制（长按选择） | `PhotoViewer.iOS/Native/iOSExifDetailView.cs:370` | B19 |
| I8 | TOOLS-05 | 刚体拟合文本改为网格下方整宽块 | `PhotoViewer.iOS/Native/iOSDinoDiagnosticView.cs:253` | B21 |
| I9 | TOOLS-06 | 关闭/隐藏工具页即取消在途 DINO 任务 | `PhotoViewer.iOS/Native/iOSToolsPresenter.cs:65` | C21 |
| I10 | SIM-10 | 列表态动作区补错误标签 | `PhotoViewer.iOS/Native/iOSSimilarityPanel.cs:126` | C09 |
| I11 | SETTINGS-01 | 快捷键行副标题显示主/次绑定与冲突（只读，不补录制） | `PhotoViewer.iOS/Core/iOSNativeSettingsPresenter.cs:1469` | B12 |
| I12 | SETTINGS-07 | 缩放预设补 >0 且 ≤1600 校验 | `PhotoViewer.iOS/Core/iOSNativeSettingsPresenter.cs:1348` | B14 |
| I13 | SETTINGS-03 | 设置展示控制器设 `OverrideUserInterfaceStyle = Dark`（与工具页一致） | `PhotoViewer.iOS/Core/iOSNativeSettingsPresenter.cs:96` | D07 |
| I14 | SETTINGS-14 / SETTINGS-07 | 设置页顶部失败状态行；预设删到剩 1 项时禁用删除 | `PhotoViewer.iOS/Core/iOSNativeSettingsPresenter.cs:17`、`:1280` | C27、C28 |
| I15 | TOOLBAR-04 | 命令按钮提示补主/次绑定（现只有命令名） | `PhotoViewer.iOS/Native/iOSControlBar.cs:107` | B26 |
| I16 | FILTER-01（视觉） | 筛选开关选中/未选中四色改令牌值（深蓝底、#AECBEE 字、#AAA 未选中） | `PhotoViewer.iOS/Native/iOSFilterBar.cs:85`、`:114` | G6-23~26 |
| I17 | IMAGE-06（视觉） | 导航图宿主 alpha 0.78→0.6 | `PhotoViewer.iOS/Native/iOSNativeRootViewController.cs:204` | G6-09 |
| I18 | SHELL-01（视觉） | 栏位底色 #121212→#111111（根视图与控制栏） | `PhotoViewer.iOS/Native/iOSNativeRootViewController.cs:180`、`iOSControlBar.cs:44` | G6-33 |
| I19 | TOOLBAR-10（视觉） | 控制栏块底统一 `color.toolbar.block.background` #222222 | `PhotoViewer.iOS/Native/iOSControlBar.cs:246` | G6-32 |

## Android

**进度（10-18 完成）**：A1–A27 全部完成并已合入主线（`refactor/native-rebuild`）。A18 用 `RecyclerView` + `ItemTouchHelper`，把手轻点给上移/下移菜单；A20 接 `ExploreByTouchHelper`；A9 栏位显隐存 `SharedPreferences`（`PhotoViewer.Native.ShowFilm` / `ShowControls`）；A2 导航图取展示产物抽样，不再二次解码文件。验证：2026-10-18 模拟器 `pv-api35` `pv-check` main PASS 14、analysis PASS 18、similarity PASS 15、settings PASS 16、tools PASS 16，均 0 FAIL；真实拖拽、触控板等需作者手验（见文末「待作者手验」）。

| # | 设计 ID | 改什么 | 入口 file:line | 来源 Q |
|---|---|---|---|---|
| A1 | IMAGE-02 | 接共享视口对焦点：`SetFocus` 在切图与 Fit 时调用 | `PhotoViewer.Android/Native/AndroidPhotoView.cs:234`、`AndroidNativeRootView.cs:137` | B01 |
| A2 | IMAGE-06 | 导航缩略图改取当前展示产物（含增强），弃 `AndroidNavigationDecoder` 二次解码 | `PhotoViewer.Android/Native/AndroidNativeRootView.cs:905` | B03 |
| A3 | IMAGE-10 | 去掉"有文件即显示正在载入"，改共享 300ms 延迟态 | `PhotoViewer.Android/Native/AndroidNativeRootView.cs:877` | B05 |
| A4 | MENU-06 / MENU-07 | 「增强预览」改「增强」；无照片时增强与工具项禁用/空态 | `PhotoViewer.Android/Native/AndroidNativeRootView.cs:552` | B08、C08 |
| A5 | MENU-05 | 卡片菜单标题行不可点；时间改 `yyyy-MM-dd HH:mm:ss zzz` | `PhotoViewer.Android/Native/AndroidThumbnailCard.cs:293` | B09 |
| A6 | MENU-05 | 长按弹菜单后消费抬手，不切主图、不改星 | `PhotoViewer.Android/Native/AndroidThumbnailCard.cs:264` | C07 |
| A7 | OPEN-01 | `HandleIntent` 合并为单个打开请求，不再逐项 Publish | `PhotoViewer.Android/Native/AndroidNativeShell.cs:216` | B29 |
| A8 | INPUT-04 / INPUT-05 | 滚轮按 source/增量连续性分流并累积到一格；`Multiple` 按键按 `Repeatable` 设闸 | `PhotoViewer.Android/Native/AndroidPhotoView.cs:413`、`AndroidNativeRootView.cs:467`、`AndroidNativeActivity.cs:72` | C05、C06 |
| A9 | SHELL-04 | 文件栏/控制栏显隐写平台偏好，跨会话恢复 | `PhotoViewer.Android/Native/AndroidNativeRootView.cs:269` | B13 |
| A10 | TOOLBAR-08 | 控制栏加沿主轴滚动与偏移保存（不再被分区矩形裁切） | `PhotoViewer.Android/Native/AndroidNativeRootView.cs:343` | C11、C14 |
| A11 | TOOLBAR-03 | 无照片补评分禁用态；评分快捷命令画数字（0=叉），去掉星形 | `PhotoViewer.Android/Native/AndroidControlBar.cs:241` | B25 |
| A12 | TOOLS-01 | 关闭工具 sheet 即取消在途 DINO 任务（打开落点已按首页） | `PhotoViewer.Android/Native/AndroidToolsPresenter.cs:171` | C21 |
| A13 | TOOLS-03 | EXIF 标签 `SetTextIsSelectable(true)` | `PhotoViewer.Android/Native/AndroidToolsPresenter.cs:278` | B19 |
| A14 | SIM-02 | 空态补 `sim.empty.spacing`（8）与 `sim.empty.margin`（4） | `PhotoViewer.Android/Native/AndroidSimilarityPanel.cs:84` | B24 |
| A15 | SIM-10 | 列表态动作区补错误标签 | `PhotoViewer.Android/Native/AndroidSimilarityPanel.cs:96` | C09 |
| A16 | SETTINGS-01 | 快捷键行副标题显示主/次绑定与冲突（只读） | `PhotoViewer.Android/Native/AndroidNativeSettingsPresenter.cs:319` | B12 |
| A17 | SETTINGS-07 | 缩放预设补 >0 且 ≤1600 校验 | `PhotoViewer.Android/Native/AndroidNativeSettingsPresenter.cs:427` | B14 |
| A18 | SETTINGS-15 | 控制列表改 `ItemTouchHelper` 拖拽 + 行尾把手，删上下按钮 | `PhotoViewer.Android/Native/AndroidNativeSettingsPresenter.cs:319` | B35 |
| A19 | SETTINGS-14 / SETTINGS-07 | 设置页顶部失败状态行；预设删到剩 1 项时禁用删除 | `PhotoViewer.Android/Native/AndroidNativeSettingsPresenter.cs:25`、`:408` | C27、C28 |
| A20 | TOOLBAR-11 | 控制栏焦点顺序与键盘触发 | `PhotoViewer.Android/Native/AndroidControlBar.cs:194` | C12、B26 |
| A21 | IMAGE-07（视觉） | 药丸：圆角 11dp、距底 24 且不重复扣底部安全区；接 500ms 门槛 | `PhotoViewer.Android/Native/AndroidNativeRootView.cs:168`、`:367` | G6-05、G6-06、B04 |
| A22 | SIM-03（视觉） | 动作按钮改 90×28：底 #2A2A2A、边 #555555、字 #CCCCCC；文本进度保留 | `PhotoViewer.Android/Native/AndroidSimilarityPanel.cs:77` | G6-11~14 |
| A23 | STATE-03（视觉） | 提示条：字号 12、圆角 6、最大宽 min(500, 宽−40)、底 #222、alpha 0.94、字 #FFFFFF | `PhotoViewer.Android/Native/AndroidNativeRootView.cs:181` | G6-15~20 |
| A24 | FILTER-01（视觉） | 开关选中底 #1F3A5F / 边 #3A6FA8 / 字 #AECBEE，未选中字 #AAAAAA | `PhotoViewer.Android/Native/AndroidFilterBar.cs:114` | G6-23~26 |
| A25 | FILM-05 / TOOLBAR-10（视觉） | 卡片与 EXIF/评分块底改 #222222；星亮 #FFD214、星灰 #808080、清零叉 #666666 | `PhotoViewer.Android/Native/AndroidUiTheme.cs:24`、`:34`、`:36`、`AndroidControlBar.cs:248` | G6-27、G6-29~32 |
| A26 | SHELL-01（视觉） | 栏位底 #1A1A1A→#111111 | `PhotoViewer.Android/Native/AndroidUiTheme.cs:20` | G6-33 |
| A27 | IMAGE-06（视觉） | 导航图宿主 alpha 0.784→0.6 | `PhotoViewer.Android/Native/AndroidNavigationOverlay.cs:39` | G6-09 |

## 已暂缓

| Q | 事项 | 处置 |
|---|---|---|
| B30 | `SCOPE-04@ios`：iOS ImageIO 对 Sony ARW 只给内嵌预览（1616×1080），物理 100% 与细节预览以预览尺寸为基准 | 作者 2026-10-18 定暂缓：`CIRAWFilter` 全分辨率解码性能未测，并入后续性能专项；现按平台限制登记 |

## 待作者手验

程序化 harness 覆盖不到的项，按平台汇总（2026-10-21 已将三端常规 UI 回填为「作者手验通过」）：

- macOS：常规 UI 已通过；失败注入（打开/评分/Full/损坏设置/相似/分析）已由 `--pv-native-faults` 机测通过。仍缺：外设断开取消录制。
- iOS·iPadOS：常规 UI 已通过；失败注入已由 `--pv-native-faults-check` 机测通过。仍缺：真实外接键鼠/触控板热插拔与录制；P3/Adobe RGB 与系统预览对拍；外接显示；外设断开后的控件恢复。
- Android：基础操作、色彩与全部可机测专项已通过（2026-10-23）：八方向、RAW 全分辨率、失败态（Full/打开/评分/DINO/相似/损坏设置）、外部打开与外部存储写回、旋转、fontScale 1.0/1.3 × density 420/525、滚轮分流、按键重复、控制栏焦点。仍缺：真实外接键鼠/触控板手感与断连、SAF 持久授权真机重启、第三方文件管理器打开、ROM 字体/厂商省电/多系统版本、手感/高刷新率；SCOPE-03/SCOPE-06 为策略项，STATE-04 的数据库不可用分支未注入。
- Windows：常规 UI、175%/200% DPI 与真实跨屏已通过。W-M7 只剩色彩（ICC/ACM/广色域标签），RAW 主图已由性能专项落地（WIC 系统 RAW 引擎全分辨率）。按作者 2026-10-22 裁定不再挂账：真实相机八方向素材（自生成素材 + 代码核对即足）、增强 + 分析长时间稳定性、桌面端外设热插拔与触控板分流、Windows 程序化失败注入、W-M6 专项 harness 与几何导出（旧壳对拍已废，几何以 Web 参考壳 + Mac 比对为准）。

遗留（本次未处理，需另立条目）：

- ~~S8：文件夹枚举失败时共享层没有可见错误状态~~ → 2026-10-19 已解决：STATE-02 补「打开失败走提示条、文案由共享层给出」，`MainViewModel.OpenFailed` / `ReportOpenFailure`，四端订阅显示。
- ~~macOS 选择器/外部打开失败走模态 `ShowError`~~ → 2026-10-19 已改为提示条，`ShowError` 删除（自动验收仍抛出）。
- 余项：Android `AndroidFilePicker` 的 SAF 启动失败仍只记日志（服务层无 VM 引用，边缘路径）。

## 验收

每个条目完成后回填 [`status.md`](../Design/status.md) 对应格子（`程序化通过 <日期>` 或保留 `缺口`），并运行该平台在 [`DEV.md`](../DEV.md) 列出的 harness：macOS `--pv-native-smoke|layout|input|analysis|similarity|toolbar|faults`；Windows **Check Windows Native** 与 `--pv-native-settings|tools`；iOS `--pv-native-photo|layout|input|analysis|similarity|tools-check|faults-check`；Android `pv-check main|analysis|similarity|settings|tools`。
