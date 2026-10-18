# SCOPE 草稿：平台事实、状态与疑问

> 私有草稿，供写 spec 时对照。规则正文见 [scope.md](../scope.md)；本文件保留平台实现入口、验收状态与未决问题，**不进规范**。

## platforms

登记格式：`<ID>@<平台>`。

### SCOPE-01 入口与延期配置

- **通用机制**：能力可见性由共享 VM 的布尔属性决定，平台只按属性建/删入口。照片数据统计是唯一走平台判断的项：`ToolsViewModel.IsPhotoStatsAvailable => OperatingSystem.IsWindows()`（`PhotoViewer/ViewModels/Tools/ToolsViewModel.cs:35`）。
- `@mac`：分析栏、相似聚类、工具窗口、增强预览四项均原生（`PhotoViewer.Mac/Native/MacMainWindow.cs:884-899` 右键菜单、筛选条 toggle、控制栏命令、EXIF 块开工具页）。照片数据统计不建入口（`PhotoViewer.Mac/Native/MacToolsWindow.cs:89`）。阶段里隐藏入口期间只保留配置、不重置用户值。
- `@ios`：入口为主图长按/辅助点按菜单（`PhotoViewer.iOS/Native/iOSContextMenus.cs:53-66`：打开文件夹… / 胶片栏·分析栏·控制栏 inline / 增强 / 工具…·设置…）、筛选条 toggle、控制栏命令、EXIF 信息块。工具页 `PageSheet` + `UINavigationController`，只有 EXIF 详情 / DINO 诊断两项。
- `@win`：入口为主图右键菜单与控制栏；工具窗口为独立 Win11 设置式窗口，**三分类含照片数据统计**（`PhotoViewer.Windows/Native/WindowsToolsWindow.cs:85,261`）。
- `@android`：主图长按 `PopupMenu`（`PhotoViewer.Android/Native/AndroidNativeRootView.cs:546-555`：打开文件夹… / 胶片栏·分析栏·控制栏 / 增强预览 / 工具…·设置…），工具页 BottomSheet 两入口（EXIF 详情 / DINO 诊断），照片数据统计不建入口。
- 延期配置保留的既有先例：P2 期间 macOS 隐藏四个 P3 入口但保留 AI 设置值与相似面板开关（`SettingsViewModel.AnalysisImmediateCompute` 默认 false、`SimilarityPanelExpanded` 跨会话保留）。

### SCOPE-02 隐式计算门控

- 相似聚类：`IsPanelVisible => Settings.SimilarityPanelExpanded && Main.IsThumbnailViewVisible`（`PhotoViewer/ViewModels/Main/File/SimilarityPanelViewModel.cs:50`），`CurrentFile` / 筛选池 / 阈值变化都先判该属性（同文件 `:202,210,217,403`），收起时取消在途 CTS。
- 分析栏：`AnalysisPresenter.Refresh` 在 `!_host.AnalysisVisible || file == null` 时取消在途任务并清空诊断瓦片（`PhotoViewer/Core/AI/AnalysisPresenter.cs:136-142`）；直方图同样守卫 `_host.AnalysisVisible`（`:438-442`）。
- 未提取回退：`FallbackDelayMs = 500`（`AnalysisPresenter.cs:34`），延迟内切图即取消；再让位等主图解码与缩略图空闲（上限 5s）。
- 抖动徽标：`ShakeFlagService.EvaluateAsync` 只读 `cv_grid` + 尺寸，不解码不推理；`MainViewModel` 在 `AllFilesChanged` 与 `RecheckRequested` 时调用（`PhotoViewer/ViewModels/Main/MainViewModel.cs:120-127`）。
- `@mac`：`MacAnalysisHost.ImmediateCompute` 读共享设置；开关变化即重路由（`PhotoViewer.Mac/Native/MacAnalysisPanel.cs:230-233`）。
- `@ios`：`iOSAnalysisHost` 同口径；真机 500 连翻（450 HEIC + 50 ARW）0 条 `DinoFeatureCache` 日志（2026-10-17）。
- `@win`：`WindowsAnalysisPanel` 实现 `IAnalysisHost` 消费共享 presenter，不自建数据链路。
- `@android`：`AndroidAnalysisHost` + `AndroidAnalysisPanel` 同口径（A5）。

### SCOPE-03 入库与现算

- 批量提取：`FolderFeatureIndexer.RunAsync` 按指纹组推进，写库成功后 `AnalysisResultCache.Invalidate(fingerprint)` 并抛 `FeaturesWritten`（`PhotoViewer/Core/AI/FolderFeatureIndexer.cs:65,296-297`）。
- 即时计算产物：`AnalysisResultCache.ImmediateKey(pathKey, enhanced: bool)`，增强变体加 `#enhanced`，随 `BitmapLoader.CacheStatusChanged` 与清库清理。
- 派生层现算：`AnalysisComputer` / `HeatmapBitmapBuilder` / `PatchHeatmap.ComputeRefCosine` / `ShakeClassifier`；阈值常量不入库。
- 版本表达：`DinoModelResources.ModelId = "dinov3_vits16_f32_518_v2"`（`PhotoViewer/Core/AI/DinoModelResources.cs:52`），`cv_grid_spec` 不匹配即缓存未命中。
- `@mac` / `@ios` / `@win` / `@android`：四端均只消费共享 presenter 与缓存，无平台自有入库逻辑。

### SCOPE-04 能力缺失

- `@ios`：ImageIO 对 Sony ARW 只给内嵌预览（真机 `req=4532 → out=1616x1080`，Mac 同文件 7008×4672），`IsProxy=false` 使 `NeedsFull` 永不升级 Full；后果是物理 100% 不高于适应倍率，双击/Fit 夹回适应态（`PhotoViewer.Shared/PhotoViewport.cs` 的 `ToggleFit` + iOS 滚动视图最小缩放）。待作者裁定是否改 `CIRAWFilter`。
- `@ios`：CoreML EP 在 iOS 原生库不可用，`DinoFeatureExtractor` 记录日志后回退 CPU，`--pv-dino-check` 打印 `ep=CPU`。
- `@android`：模拟器无 P3（EGL 协商失败回退 sRGB），`AndroidPresentationEncodingPolicy` 恒 Rgba8。
- 缺模型：`DinoFeatureExtractor.cs:94` 抛 `FileNotFoundException`（含"运行 export 脚本"文案）。
- 推理失败后的界面：分析栏 `ComputeDiagnosticsAsync` 捕获后回落占位"未提取"（`AnalysisPresenter.cs:375-379`）；未提取回退失败同样落"未提取"（`:424-425`）；工具页 DINO 失败显示 `失败: <message>`（`PhotoViewer/Core/AI/DinoDiagnosticPresenter.cs:264-266`）。

### SCOPE-05 文件写操作

- 星级写回：`XmpWriter.WriteRatingAsync`，安全模式下同目录生成 `<file>.xmp_backup`（iPad 真机验收因夹具目录属 root 触发写失败而暴露）。
- 命令表无删除 / 重命名 / 移动 / 导出：`SettingsViewModel.BuildDefaultHotkeys()`（`PhotoViewer/ViewModels/Settings/SettingsViewModel.Hotkeys.cs:37-93`）14 条命令，全部为浏览、缩放、评分、栏位与增强。
- 统计导出：`PhotoStatsViewModel.ExportAsync(outputPath)` 只写 CSV。

### SCOPE-06 平台专属

- 照片数据统计：Windows 专属，`IsPhotoStatsAvailable` 硬编码平台判断；mac/iPad/Android 不建入口。
- 触屏专属：导航图点按/拖动定位（iOS `iOSNativeRootViewController.cs:203,570`；Android A3 同）；Mac 导航图只展示。
- Android 实现偏差：设置数值用 `+/-` 步进、列表排序用上下按钮（拖拽 `ItemTouchHelper` 待补）、拖入 `OnDragListener` 未接（`Plans/native-rebuild-phase-android.md` A4）。
- iPhone 紧凑布局不在范围内，同一原生壳可启动、不做专用适配（`Plans/native-rebuild-phase-ipad.md` §0）。

## status

按 ui-contract §4 与各计划原文摘录，带日期；只作迁移期参考，最终以 status.md 为准。

- **SCOPE-01**
  - macOS：P3（2026-10-16）四项入口原生可用；照片数据统计不建入口；作者真实照片端到端复验通过（除退出崩溃已修）。
  - iOS/iPadOS：I5（2026-10-17）四类入口全部解禁；模拟器两机型 + 真机 `analysis` 15 / `similarity` 10 / `tools` 7 PASS；逐项手感待作者。
  - Windows：分析栏/相似聚类/增强预览原生可用；工具窗口 2026-10-17（`--pv-native-tools` 5 项 PASS）；设置窗口 W-M5 2026-10-17（`--pv-native-settings` 7 项 PASS）；主屏 `--pv-native-smoke` 24 项 PASS（150%）；200%/175% DPI、真实跨屏、视觉与手感待验。
  - Android：A5 已完成（2026-10-18 模拟器目视/日志）；A6 `pv-check` 五模式 PASS 14/18/15/16/16（2026-10-18）；矩阵与真机门未做。
- **SCOPE-02**
  - macOS：`--pv-native-analysis` 断言分析栏隐藏时不出图、入库后自行刷新（2026-10-16）。
  - iOS/iPadOS：真机 500 连翻 0 条 `DinoFeatureCache` 日志，p50 323.2ms（2026-10-17）；200 连翻（分析栏关）p50 311.1ms。
  - Windows：`--pv-native-smoke` 覆盖即时计算与未提取回退（150%）。
  - Android：A5 落地，门控专项断言待 A6。
- **SCOPE-03**
  - macOS：`--pv-native-analysis` 覆盖"未提取 → 真实索引 → 瓦片自行出图且未切图"（2026-10-16 修复后）。
  - iOS/iPadOS：`--pv-native-analysis-check` 覆盖未提取 → 真实索引刷新（2026-10-17，模拟器 + 真机）。
  - Windows：共享 presenter，随 `--pv-native-smoke` 覆盖。
  - Android：`pv-check analysis` 18 项 PASS（2026-10-18 模拟器），入库路径未专项断言。
- **SCOPE-04**
  - iOS：ARW 内嵌预览列为未关闭项（待作者裁定 `CIRAWFilter`）；CoreML EP 不可用（上游限制，CPU 推理）；8 位 P3 原色边界已登记。
  - Android：模拟器 P3 回退 sRGB；双击物理 100%、色卡线性值断言待 A6。
  - Windows：色彩正确性属 W-M7 独立立项；当前恒 F16 线性 scRGB。
- **SCOPE-05**：无独立验收项；评分写回成功/失败路径由 FILM-06 各端套件覆盖。
- **SCOPE-06**：Mac/iPad/Android 工具页均只有两项入口（2026-10-17/18 程序化或目视）；照片统计仅 Windows `--pv-native-tools` 覆盖。

## questions

1. **照片数据统计的等级**：`ToolsViewModel.IsPhotoStatsAvailable` 用平台判断（`PhotoViewer/ViewModels/Tools/ToolsViewModel.cs:35`），`MacToolsWindow.cs:89` 注释写"仅 Windows 可用"。spec 里我按「平台专属、可以」写；如果它是"待补齐缺口"，等级应改为「应当」并进 status 缺口，请作者裁定。
2. **SCOPE-01 的"延期"分支目前没有活跃实例**：四端四项 P3 入口都已解禁，唯一被隐藏的是照片统计（且是平台判断）。这条规则是保留为前瞻约定，还是收敛为"平台能力差异 + 配置保留"？两者写法差别在是否还要强调"阶段隐藏"。
3. **推理失败与"未提取"不可区分**：`AnalysisPresenter.cs:375-379` 与 `:424-425` 在推理/计算失败后回落"未提取"占位，用户无法区分"还没提取"与"算不出来"（缺模型、EXE 资源缺失）。这与 SCOPE-04"缺模型不长期停在计算中、不冒充成功"张力明显；是否需要独立失败占位（由 [state](../state.md) 收口）？
4. **增强被内存压力自动关闭无提示**：`PhotoLoadSession.Trim` 在压力 ≥ Warning 时静默关闭增强并释放增强图（`PhotoViewer/Core/Image/PhotoLoadSession.cs:313-321`）。这是系统触发的"用户没要求的状态变更"；是否需要一条可见提示（与 SCOPE-02/04 的"状态变更可见"一致）？
5. **增强入口的跨端一致性**：macOS 主图右键菜单没有「增强」（`PhotoViewer.Mac/Native/MacMainWindow.cs:884-899`，只有命令/控制栏入口），iPad 菜单有「增强」（`iOSContextMenus.cs:60`），Android 也有（`AndroidNativeRootView.cs:552`）。SCOPE-01 要求"同一会话内三处可见性一致"，同一功能在四端菜单结构上也应一致；以哪端为准？（可能属 MENU，但缺项本质是功能入口可见性。）
6. **Mac 菜单与 iPad 菜单结构差异**：Mac 有「打开文件…」+「打开文件夹…」两项且无 inline 分组，iPad/Android 只有「打开文件夹…」并把三个栏位开关并成一组（`iOSContextMenus.cs:53-66` vs `MacMainWindow.cs:888-896`）。这是平台惯例（桌面可选文件/文件夹）还是应统一？OPEN/MENU 需要同一结论。
7. **Android 偏差算差异还是缺口**：设置数值 `+/-` 步进、列表上下按钮排序、拖入未接（`Plans/native-rebuild-phase-android.md` A4 明确写"偏差"）。按 README §3，偏离「必须」项即缺口，偏离「应当」项须登记 `@android`；目前没有登记条目，SCOPE/SETTINGS 谁负责登记？
8. **清库的"二次确认"其实是三层**：共享 VM 自带"3 秒内再次点击才执行"的 arm 态（`PhotoViewer/ViewModels/Settings/SettingsViewModel.AI.cs:115-140`），但各原生壳外面还有系统确认（Windows `ContentDialog` 后连打两次 `ClearDatabaseCommand`，`PhotoViewer.Windows/Native/WindowsSettingsPages.Ai.cs:87-124`；macOS Swift 侧 `confirmationDialog`，`PhotoViewer.Mac/Native/SwiftSettings/MacSettingsUI.swift:1092`，并在 AI 页暴露"即时计算DINO/CV"开关 `:1076`）。结果是 arm 态对用户不可见、两层确认语义重复。spec 应规定"一次系统确认"还是保留两段确认？（属 SETTINGS；SCOPE-03 只认"清库必须清缓存"。）
9. **单图模式的平台事实缺口**：父目录不可访问时回退单图模式（`FolderViewModel.OpenImageWithFolderFallbackAsync`），但四端原生壳是否都实现了"父目录访问被拒"的判定未逐一核对；spec 的 OPEN 条目需平台确认后再写死触发条件。
