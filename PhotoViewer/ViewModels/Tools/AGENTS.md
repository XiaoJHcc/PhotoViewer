# ViewModels/Tools — 工具页 VM

> 模块内手册。工具壳如何挂载到桌面/移动主窗口、在主窗口与工具页之间联动当前文件,见[架构文档](../../../Design/architecture.md)。视图全部在四个原生壳内实现，共享工程不再有 `Views/Tools/*.axaml`。

`namespace PhotoViewer.ViewModels.Tools`

| 模块 | ViewModel | 职责 |
|---|---|---|
| **工具窗口首页 / Tools shell** | [ToolsViewModel.cs](ToolsViewModel.cs) | 共享工具中心：EXIF 详情、照片数据统计（Windows 专属）、DINO 诊断。持有 DINO 诊断的平台中立 presenter(`DinoPresenter`)，四个原生工具窗口共用同一实例，避免重复推理。`ShowList`/`Deactivate` 取消在途 DINO 任务(TOOLS-06);`IsPresented` 由壳在显示/关闭时写入,主窗口据此只在未展示时回首页(TOOLS-01);`SyncCurrentFile` 用 `ImageFile.IsExifLoaded` 区分 EXIF 加载中与确实无 EXIF(TOOLS-02)。**Windows 原生工具窗口**([WindowsToolsWindow](../../../PhotoViewer.Windows/Native/WindowsToolsWindow.cs)) 用 Mica + `TitleBar` + `NavigationView` 三分类直接承载各工具 VM,不存在"列表 + 返回"两态;它仍通过 `OpenExifDetail` / `OpenPhotoStats` / `OpenDinoDebug` 写回 `CurrentTool`,让 `SyncCurrentFile` 的联动条件保持同一份。 |
| **EXIF 详情页** | [ExifDetailViewModel.cs](ExifDetailViewModel.cs) | 工具页内容 VM：在同一张照片的伴侣文件（RAW / JPG / HEIF）间切换 — RAW 固定在前，伴侣文件惰性加载。纯平台中立（无 UI 位图），macOS/iPad/Windows/Android 原生工具窗口直接复用；表格几何取共享 `ExifTableGeometry`。 |
| **照片数据统计** | [PhotoStatsViewModel.cs](PhotoStatsViewModel.cs) | 选择多文件夹 + 通配符筛选，批量递归扫描，读取等效焦距与星级，导出为 CSV。仅 Windows（依赖 `System.IO.Directory`，`IsPhotoStatsAvailable = OperatingSystem.IsWindows()`）。核心服务：[Core/Tools/PhotoStatsService.cs](../../Core/Tools/PhotoStatsService.cs)。macOS/iPad/Android 原生工具窗口不显示该入口；Windows 原生页用工具窗口自有的文件夹/CSV 保存选择器。 |
| **DINO 诊断页** | [DinoDebugViewModel.cs](DinoDebugViewModel.cs) | **中性转发层**:数据来源与编排在 `Core/AI/DinoDiagnosticPresenter`(读库快路径 + CV/DINO 两路推理 → 锐度图 / 抖动矢量场 / 加权刚体拟合文本 / PCA-RGB / 点击参考点 cosine)。本 VM 只把 presenter 的状态与操作（`IsBusy` / `Status` / `AspectRatio` / `ShakeField` / `RigidMotionText` / `RefGridX,Y` / `SetSource` / `OnTileClicked` / `ClearCrosshair`）暴露给工具壳，不持有位图、不做格式适配。仅在工具页显示时联动(`SyncCurrentFile`);诊断页不回写库。 |
