# ViewModels/Main — 主窗口 VM

> 模块内手册。VM 间联动(MainViewModel 怎么组合子 VM、文件源事件如何驱动文件栏)见[架构文档](../../../Design/architecture.md)；共享几何与命令表见 [PhotoViewer.Shared](../../../PhotoViewer.Shared/AGENTS.md)。

`namespace PhotoViewer.ViewModels.Main`

共享 VM 只做业务状态，不持有任何 UI 框架类型：主图加载由 `Core/Image/PhotoLoadSession` 编排，视口缩放/平移由各端原生层持有；分析栏编排在 `Core/AI/AnalysisPresenter`，四个原生壳共用。原 Avalonia 壳的 `ImageViewModel`、`AnalysisViewModel`、`Views/Main/*.axaml` 已随 6D 退役删除，`UsesNativePresentation` / `nativePresentation` 开关也不存在。显式打开（文件/文件夹/外部打开/拖入）由 `MainViewModel.RequestFitToView()` 触发 `FitToViewRequested`，平台壳订阅后把主图切回适应态（OPEN-05）。控制栏命令统一为 `ReactiveCommand<Unit, Unit>`（打开/上一张/下一张/交换/评分 0–5/分析栏），不再有视口命令与 `PointerContext`。跨模块链路见[架构文档](../../../Design/architecture.md) §5.6–§5.9。

| 模块 | ViewModel | 职责 |
|---|---|---|
| **主窗口 / Main shell** | [MainViewModel.cs](MainViewModel.cs) | 组合文件源、文件栏、控制栏、设置与工具 VM；维护 `CurrentFile` / `LastFile` / 锚点高亮（`IsCurrent` 与 `IsCurrentImage` 双身份，`SetCurrentImageKeepAnchor` 供相似聚类保留锚点）；`RatingWriteFailed` 事件供各端提示条（FILM-06），`FitToViewRequested` 供显式打开回适应态（OPEN-05）。`OpenToolsWindow` 仅在 `Tools.IsPresented == false` 时回工具首页，已打开只前置并保留当前页（TOOLS-01）。 |
| **文件源** | [FolderViewModel.cs](FolderViewModel.cs) | 仅负责打开文件/文件夹与维护 `AllFiles`；通过 `AllFilesChanged` / `ScrollToCurrentRequested` / `PriorityThumbnailRequested` 事件通知文件栏。打开文件夹且未指定目标时，首张取筛选后列表首项（同名合并下即代表文件，如 HIF 而非隐藏的 ARW），筛选为空时回退枚举首项所属合并组的代表文件（OPEN-04）。打开带代际护栏（旧代际枚举/补齐结果不写入）与同目录重开保留缩略图缓存（OPEN-08）；枚举被拒/失败时回退单图模式（OPEN-03）。 |
| **文件栏-容器** | [File/FileViewModel.cs](File/FileViewModel.cs) | 组合筛选条、主缩略图列表与相似聚类面板；`IsRowLayout` / `IsSimilarityPanelOpen` 直通共享状态，供各端原生文件栏决定分区与让位。 |
| **文件栏-筛选** | [File/FilterBarViewModel.cs](File/FilterBarViewModel.cs) | 排序方式 / 方向 / 星级筛选 + 计数 + 相似聚类 toggle。变化通过 `FilterChanged` / `SortChanged` / `SimilarityPanelToggled` 事件通知下游；`IsSimilarityPanelOpen` 直通 `SettingsModel.SimilarityPanelExpanded`（跨会话保留）；`FilteredCount` 由 `BindFilteredCountProvider` 注入并可与列表同帧刷新（FILTER-04）。 |
| **文件栏-主缩略图列表** | [File/ThumbnailListViewModel.cs](File/ThumbnailListViewModel.cs) | 维护 `FilteredFiles`、缩略图加载（投递 `DecodeScheduler`：可见/当前 → P1，普通排队 → P2）、可见区域与定位请求；位图预取由 `BitmapPrefetcher` 调度，并在当前图变更时向 `BitmapLoader` 上报淘汰保护集（当前 ± 预取窗口 + 最近浏览 M 张）。翻页经共享 `MoveCurrent`（FILTER-05：当前照片被筛掉时按重算前索引定位最近邻项）；名称排序 `OrdinalIgnoreCase`（FILTER-02），评分筛选暂留与计数同帧（FILM-06 / FILTER-04）。卡片绘制由各端原生卡片承担。 |
| **文件栏-相似聚类列表** | [File/SimilarityPanelViewModel.cs](File/SimilarityPanelViewModel.cs) | 基于 DINOv3 CLS 的真实相似聚类(`Core/AI/SimilarityService`)。**触发门控(SCOPE-01)**:仅当面板展开且文件栏可见(`IsSimilarityPanelOpen && IsThumbnailViewVisible`)时,才随 `CurrentFile` / `FilteredFiles` / 阈值变化重算;收起即取消在途计算,切图不隐式推理;重新展开时 `OnPanelOpened` 按当前图重算并统计覆盖度。三态 UI(Empty / Partial / Full)由 `ShowFullPlaceholder` / `ShowUnindexedPlaceholder` / `IsStateIndexing` 等布尔量给出，"提取全部/补齐全部"按钮原地换进度条;`FolderFeatureIndexer` 一轮扫描同时落 DINO CLS+patch+CV 三类原始数据,`EvaluateMissingPartsAsync` 三路齐备才算已提取。点击相似项通过 `SetCurrentImageKeepAnchor` 切主图但保留原锚点。分数 75%~100% → 柱状条的映射由各端卡片按同一口径实现。提取错误 `IndexError`/`HasIndexError` 在列表态与空态共用;`ShowActionAreaError` 供列表态动作区绑定(SIM-10)。 |
| **控制栏** | [ControlViewModel.cs](ControlViewModel.cs) | 控制栏 EXIF 摘要项与命令集合；按钮按 `Settings.Hotkeys` 的主/次绑定与显隐顺序生成，命令统一为 `ReactiveCommand<Unit, Unit>`（`OnOpen` / `OnPrevious` / `OnNext` / `OnExchange` / `OnSetRating0..5` / `OnToggleAnalysisView`），EXIF 文本由 `Core/Exif/ExifDisplayConverters` 格式化。 |
