# ViewModels/Tools — 工具页 VM

> 模块内手册。工具壳如何挂载到桌面/移动主窗口、在主窗口与工具页之间联动当前文件,见[架构文档](../../../Design/architecture.md)。

`namespace PhotoViewer.ViewModels.Tools`

| 模块 | ViewModel | View | Notes |
|---|---|---|---|
| **工具窗口首页 / Tools shell** | [ToolsViewModel.cs](ToolsViewModel.cs) | [Views/Tools/ToolsView.axaml](../../Views/Tools/ToolsView.axaml) + [Views/Tools/ToolsWindow.axaml](../../Views/Tools/ToolsWindow.axaml) | Shared tool hub for desktop window / mobile modal. Current tools: EXIF 详情、照片数据统计、DINO 诊断。P3 起同时持有 DINO 诊断的平台中立 presenter(`DinoPresenter`),旧壳 VM 与 macOS/iPad/Windows 原生工具窗口共用同一实例,避免重复推理。`ShowList`/`Deactivate` 取消在途 DINO 任务(TOOLS-06);`IsPresented` 由壳在显示/关闭时写入,主窗口据此只在未展示时回首页(TOOLS-01);`SyncCurrentFile` 用 `ImageFile.IsExifLoaded` 区分 EXIF 加载中与确实无 EXIF(TOOLS-02)。**Windows 原生工具窗口**([WindowsToolsWindow](../../../PhotoViewer.Windows/Native/WindowsToolsWindow.cs)) 用 Mica + `TitleBar` + `NavigationView` 三分类直接承载各工具 VM,不存在共享 `ToolsView` 的"列表 + 返回"两态;它仍通过 `OpenExifDetail` / `OpenPhotoStats` / `OpenDinoDebug` 写回 `CurrentTool`,让 `SyncCurrentFile` 的联动条件保持同一份。 |
| **EXIF 详情页** | [ExifDetailViewModel.cs](ExifDetailViewModel.cs) | [Views/Tools/ExifDetailView.axaml](../../Views/Tools/ExifDetailView.axaml) | Tool page hosted inside the shared tools shell. Switches between sibling files of the same shot (RAW / JPG / HEIF) — RAW pinned first, companion files lazy-loaded. 纯平台中立(无 Avalonia 位图),macOS/iPad/Windows 原生工具窗口直接复用。 |
| **照片数据统计** | [PhotoStatsViewModel.cs](PhotoStatsViewModel.cs) | [Views/Tools/PhotoStatsView.axaml](../../Views/Tools/PhotoStatsView.axaml) | 选择多文件夹 + 通配符筛选，批量递归扫描，读取等效焦距与星级，导出为 CSV。仅 Windows（依赖 `System.IO.Directory`，`IsPhotoStatsAvailable = OperatingSystem.IsWindows()`）。核心服务：[Core/Tools/PhotoStatsService.cs](../../Core/Tools/PhotoStatsService.cs)。macOS/iPad 原生工具窗口不显示该入口；Windows 原生页用工具窗口自有的文件夹/CSV 保存选择器。 |
| **DINO 诊断页** | [DinoDebugViewModel.cs](DinoDebugViewModel.cs) | [Views/Tools/DinoDebugView.axaml](../../Views/Tools/DinoDebugView.axaml) | **薄适配器**:数据来源与编排在 `Core/AI/DinoDiagnosticPresenter`(读库快路径 + CV/DINO 两路推理 → 锐度图 / 抖动矢量场 / 加权刚体拟合文本 / PCA-RGB / 点击参考点 cosine)。本 VM 只把 presenter 的 `DecodedImage` 适配成 Avalonia 位图属性。仅在工具页显示时联动(`SyncCurrentFile`);诊断页不回写库。**Windows 原生页不再经过本 VM**,直接消费 `Tools.DinoPresenter` 并渲染 `AnalysisTile`;本 VM 仅服务旧 Avalonia 壳。 |
