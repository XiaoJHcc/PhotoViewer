# ViewModels/Settings — 设置页 VM

> 模块内手册。`SettingsViewModel` 是 partial 类,按分类拆 9 个文件,共享同一份 VM 实例。Views 在 [Views/Settings/](../../Views/Settings/);iOS 走原生设置页([PhotoViewer.iOS/Core/iOSNativeSettingsPresenter.cs](../../../PhotoViewer.iOS/Core/iOSNativeSettingsPresenter.cs)),共享本 VM。

`namespace PhotoViewer.ViewModels.Settings`

五个分页:**文件 / 预览 / 控制 / EXIF / AI**。每个 partial 持有一类设置项,改动通过 `SettingsService` 持久化到 JSON。

| File | 分类 | Responsibility |
|---|---|---|
| [SettingsViewModel.cs](SettingsViewModel.cs) | 主类 | 共享构造、`SettingsService` 注入、各 partial 共用的 `RaisePropertyChanged` 与持久化触发逻辑。 |
| [SettingsViewModel.FileFormats.cs](SettingsViewModel.FileFormats.cs) | 文件 | 启用/禁用各扩展名,文件后缀解析顺序。 |
| [SettingsViewModel.BitmapCache.cs](SettingsViewModel.BitmapCache.cs) | 预览 | 内存上限、解码并发、预取邻居数。 |
| [SettingsViewModel.ImagePreview.cs](SettingsViewModel.ImagePreview.cs) | 预览 | 主图区显示选项、letterbox 行为。 |
| [SettingsViewModel.Layout.cs](SettingsViewModel.Layout.cs) | 控制 | 文件栏行/列布局、显示选项。 |
| [SettingsViewModel.Hotkeys.cs](SettingsViewModel.Hotkeys.cs) | 控制 | 快捷键绑定,用 `HotkeyButton` 捕获。 |
| [SettingsViewModel.Rating.cs](SettingsViewModel.Rating.cs) | 控制 | 星级写回 RAW 的 in-place / sidecar 策略,XMP 行为开关。 |
| [SettingsViewModel.ExifDisplay.cs](SettingsViewModel.ExifDisplay.cs) | EXIF | EXIF 详情页的字段展示开关、汉化偏好。 |
| [SettingsViewModel.AI.cs](SettingsViewModel.AI.cs) | AI | 相似聚类阈值(75%~95% / 默认 85%)、最多数量(1~32 / 默认 8,指数滑条)、**"清除特征数据库"按钮**(开发者用,二次确认后调 `PhotoDatabase.DeleteDatabaseAsync` + `DinoFeatureCache.InvalidateAll` + `ShakeFlagService.InvalidateAll`)。 |
| [SettingsViewModel.Persistence.cs](SettingsViewModel.Persistence.cs) | 共用 | 各 partial 的 JSON 读写、默认值回滚、跨会话状态保留(如 `SimilarityPanelExpanded`)。 |
