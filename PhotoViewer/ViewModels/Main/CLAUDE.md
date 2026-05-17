# ViewModels/Main — 主窗口 VM

> 模块内手册。VM 间联动(MainViewModel 怎么组合子 VM、文件源事件如何驱动文件栏)写在根 `CLAUDE.md` §4 UI 模块对应。Helpers / Controls 在根 §6。

`namespace PhotoViewer.ViewModels.Main`

| 模块 | ViewModel | View | Notes |
|---|---|---|---|
| **主窗口 / Main shell** | [MainViewModel.cs](MainViewModel.cs) | Desktop: [Windows/MainWindowForWindows.axaml](../../Windows/MainWindowForWindows.axaml), [Windows/MainWindowForMac.axaml](../../Windows/MainWindowForMac.axaml) · Mobile: [Windows/SingleView.axaml](../../Windows/SingleView.axaml) hosting [Views/Main/MainView.axaml](../../Views/Main/MainView.axaml) | Layout switch (grid/list), fullscreen, child-VM wiring. |
| **文件源** | [FolderViewModel.cs](FolderViewModel.cs) | (logic only) | 仅负责打开文件/文件夹与维护 `AllFiles`；通过 `AllFilesChanged` / `ScrollToCurrentRequested` / `PriorityThumbnailRequested` 事件通知文件栏。 |
| **文件栏-容器** | [File/FileViewModel.cs](File/FileViewModel.cs) | [Views/Main/File/FileView.axaml](../../Views/Main/File/FileView.axaml) | 三分区容器：筛选条 + 主缩略图列表 + 相似聚类面板。单层 `Grid` 按 `IsRowLayout` 切换列定义（行布局:两列 *，筛选条跨两列；列布局:主列 116px + 聚类列 Auto，筛选条仅占主列）。聚类面板 `IsVisible` 绑定 `IsSimilarityPanelOpen`，默认折叠。 |
| **文件栏-筛选** | [File/FilterBarViewModel.cs](File/FilterBarViewModel.cs) | [Views/Main/File/FilterBarView.axaml](../../Views/Main/File/FilterBarView.axaml) | 排序方式 / 方向 / 星级筛选 + 计数 + 相似聚类 toggle。变化通过 `FilterChanged` / `SortChanged` / `SimilarityPanelToggled` 事件通知下游。`IsSimilarityPanelOpen` 直通 `SettingsModel.SimilarityPanelExpanded`（跨会话保留）。`StackPanel` 名为 `FilterBarPanel`，平台标题栏代码靠它定位筛选条边界。 |
| **文件栏-主缩略图列表** | [File/ThumbnailListViewModel.cs](File/ThumbnailListViewModel.cs) | [Views/Main/File/ThumbnailListView.axaml](../../Views/Main/File/ThumbnailListView.axaml) | 维护 `FilteredFiles`、缩略图加载队列、可见区域滚动+动画；位图预取由内置的 `BitmapPrefetcher` 调度。卡片渲染已抽到共享 `ThumbnailCard` 控件（与相似聚类列表复用）。 |
| **文件栏-相似聚类列表** | [File/SimilarityPanelViewModel.cs](File/SimilarityPanelViewModel.cs) | [Views/Main/File/SimilarityListView.axaml](../../Views/Main/File/SimilarityListView.axaml) | 基于 DINOv3 CLS 的真实相似聚类(`Core/AI/SimilarityService`)。三态 UI(Empty / Partial / Full)+ "提取全部/补齐全部" 按钮原地变进度条;`FolderFeatureIndexer` 一轮扫描同时落 DINO CLS+patch+CV 三类原始数据,`EvaluateMissingPartsAsync` 三路齐备才算已提取。面板展开时 `OnPanelOpened` 触发覆盖度评估;点击相似项通过 `SetCurrentImageKeepAnchor` 切主图但保留原锚点。 |
| **主要图片显示** | [ImageViewModel.cs](ImageViewModel.cs) | [Views/Main/ImageView.axaml](../../Views/Main/ImageView.axaml) | Main canvas. Single-image display, zoom/pan gestures, load state. |
| **控制栏** | [ControlViewModel.cs](ControlViewModel.cs) | [Views/Main/ControlView.axaml](../../Views/Main/ControlView.axaml) | Toolbar buttons (open, display options, fullscreen). |
| **细节栏(旧,已无菜单入口)** | [DetailViewModel.cs](DetailViewModel.cs) | [Views/Main/DetailView.axaml](../../Views/Main/DetailView.axaml) | 旧实现:5 张 DetailPreview(中心/四角)+ Sony 对焦点动态项。`IsDetailViewVisible` 仍在 `MainViewModel`,但右键菜单和 `ToggleDetailView` 快捷键已重定向到分析栏。保留是为了出问题时一行改回 (`ControlViewModel.OnToggleDetailView`)。下个清理 PR 整体删除。 |
| **分析栏** | [AnalysisViewModel.cs](AnalysisViewModel.cs) | [Views/Main/AnalysisView.axaml](../../Views/Main/AnalysisView.axaml) | 常驻侧栏,合并细节预览(对焦点/中心,DetailPreview 复用,hover/double-tap 联动主图绿框)+ 4 张 DINO/CV 诊断图(`DINO PCA` / `Cosine x,y` / `锐度` / `抖动拖影`,DiagnosticTile + ShakeFieldView 复用)。**只读库**:每次切图 `AnalysisDataReader.TryReadAsync` 拉指纹 → patch + cv_grid → 派生层现算 4 张图与判定文字 — **不解码、不推理、不重算 CV**,缓存缺失时角标改为判定文字或"中心"等占位,主图区显示"未提取"。`diagonal` 从 `cv_image_width/height` 读,无需重新解码。下半 4 张瓦片共享准星 + cosine 参考点(点击瓦片 → `OnTileClicked` 重算 cosine 项);上半 2 张 DetailPreview 自己吃点击事件,不参与准星。可见性 false 时取消后台 token,避免泄漏。 |
