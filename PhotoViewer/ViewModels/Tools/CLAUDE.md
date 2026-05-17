# ViewModels/Tools — 工具页 VM

> 模块内手册。工具壳如何挂载到桌面/移动主窗口、在主窗口与工具页之间联动当前文件,见根 `CLAUDE.md` §4 UI 模块对应。

`namespace PhotoViewer.ViewModels.Tools`

| 模块 | ViewModel | View | Notes |
|---|---|---|---|
| **工具窗口首页 / Tools shell** | [ToolsViewModel.cs](ToolsViewModel.cs) | [Views/Tools/ToolsView.axaml](../../Views/Tools/ToolsView.axaml) + [Views/Tools/ToolsWindow.axaml](../../Views/Tools/ToolsWindow.axaml) | Shared tool hub for desktop window / mobile modal. Current tools: EXIF 详情、照片数据统计、DINO 诊断。 |
| **EXIF 详情页** | [ExifDetailViewModel.cs](ExifDetailViewModel.cs) | [Views/Tools/ExifDetailView.axaml](../../Views/Tools/ExifDetailView.axaml) | Tool page hosted inside the shared tools shell. Switches between sibling files of the same shot (RAW / JPG / HEIF) — RAW pinned first, companion files lazy-loaded. |
| **照片数据统计** | [PhotoStatsViewModel.cs](PhotoStatsViewModel.cs) | [Views/Tools/PhotoStatsView.axaml](../../Views/Tools/PhotoStatsView.axaml) | 选择多文件夹 + 通配符筛选，批量递归扫描，读取等效焦距与星级，导出为 CSV。仅 Windows（依赖 `System.IO.Directory`，`IsPhotoStatsAvailable = OperatingSystem.IsWindows()`）。核心服务：[Core/Tools/PhotoStatsService.cs](../../Core/Tools/PhotoStatsService.cs)。 |
| **DINO 诊断页** | [DinoDebugViewModel.cs](DinoDebugViewModel.cs) | [Views/Tools/DinoDebugView.axaml](../../Views/Tools/DinoDebugView.axaml) | 平铺展示锐度热力图 + 抖动矢量场(颜色由 R_local 主导色相 × drag_r 调亮度)+ 加权刚体拟合文本面板(含 \|T\| / \|ω\| / R_global / R_local p10 / 判定标签)+ DINO PCA-RGB + 点击参考点 cosine。**优先读库快路径**(`TryReadCachedAsync` 同时取 patch token + cv_grid 7 标量),命中跳过推理与 CV 计算;诊断页不回写库(回写交给 indexer 主路径)。派生层(锐度/抖动/刚体)从 7 标量现算,UI 看图 ≡ DB 重画图。仅在工具页显示时联动(`SyncCurrentFile`)。 |
