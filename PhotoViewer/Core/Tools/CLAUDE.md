# Core/Tools — 辅助工具服务

> 模块内手册。对应的 UI 在 [ViewModels/Tools/](../../ViewModels/Tools/) 工具页。

`namespace PhotoViewer.Core.Tools`

| File | 模块 | Responsibility |
|---|---|---|
| [PhotoStatsService.cs](PhotoStatsService.cs) | 照片数据统计服务 | 递归扫描多文件夹，读取等效焦距与星级，导出 CSV。仅 Windows 启用（`OperatingSystem.IsWindows()`）。 |
