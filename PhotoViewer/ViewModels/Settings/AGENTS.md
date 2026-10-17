# ViewModels/Settings — 设置页 VM

> 模块内手册。`SettingsViewModel` 是 partial 类,按分类拆 9 个文件,共享同一份 VM 实例。Views 在 [Views/Settings/](../../Views/Settings/);iOS 走原生设置页([PhotoViewer.iOS/Core/iOSNativeSettingsPresenter.cs](../../../PhotoViewer.iOS/Core/iOSNativeSettingsPresenter.cs)),Windows 走原生设置窗口([PhotoViewer.Desktop/Native/WindowsNativeSettingsPresenter.cs](../../../PhotoViewer.Desktop/Native/WindowsNativeSettingsPresenter.cs)),都共享本 VM。

`namespace PhotoViewer.ViewModels.Settings`

五个分页:**文件 / 预览 / 控制 / EXIF / AI**。每个 partial 持有一类设置项,改动通过 `SettingsService` 持久化到 JSON。

macOS P2 通过 Core/Settings/NativeSettingsSession 建工作副本，`applyRuntimeSettings: false` 隔离缓存/解码/键盘映射副作用，原生窗口关闭后 ApplySnapshot 一次回写；副本 Dispose 释放全局内存通知与保存订阅。Windows W-M5 用同一 `NativeSettingsSession` 与 `WindowsNativeSettingsPresenter`：独立窗口、五分类、关闭一次性回写；本工程无法加载 `NavigationView`/`TitleBar`/`NumberBox`/`TextBox`/`InfoBar`，实际用 ListView 导航、Slider 数值与自绘状态条（见 [PhotoViewer.Desktop/AGENTS.md](../../../PhotoViewer.Desktop/AGENTS.md)）。预算提示读取实际展示编码字节数（原生 F16=8B）。UI 实现见 PhotoViewer.Mac/AGENTS.md。

| File | 分类 | Responsibility |
|---|---|---|
| [SettingsViewModel.cs](SettingsViewModel.cs) | 主类 | 共享构造、`SettingsService` 注入、各 partial 共用的 `RaisePropertyChanged` 与持久化触发逻辑。 |
| [SettingsViewModel.FileFormats.cs](SettingsViewModel.FileFormats.cs) | 文件 | 启用/禁用各扩展名,文件后缀解析顺序。 |
| [SettingsViewModel.BitmapCache.cs](SettingsViewModel.BitmapCache.cs) | 预览 | 内存上限、解码并发、预取邻居数。 |
| [SettingsViewModel.ImagePreview.cs](SettingsViewModel.ImagePreview.cs) | 预览 | 主图区显示选项、letterbox 行为。 |
| [SettingsViewModel.Layout.cs](SettingsViewModel.Layout.cs) | 控制 | 文件栏行/列布局、显示选项。 |
| [SettingsViewModel.Hotkeys.cs](SettingsViewModel.Hotkeys.cs) | 控制 | 快捷键绑定,用 `HotkeyButton` 捕获。 |
| [SettingsViewModel.Rating.cs](SettingsViewModel.Rating.cs) | 控制 | 星级显示与安全原位写回开关；当前没有自动 RAW sidecar 创建/关联（见根 §5.3）。 |
| [SettingsViewModel.ExifDisplay.cs](SettingsViewModel.ExifDisplay.cs) | EXIF | EXIF 详情页的字段展示开关、汉化偏好。 |
| [SettingsViewModel.AI.cs](SettingsViewModel.AI.cs) | AI | 相似聚类阈值(75%~95% / 默认 85%)、最多数量(1~32 / 默认 8,指数滑条)、**分析诊断"即时计算DINO/CV"开关**(默认关;门控分析栏增强实时重算与未提取回退,iOS 原生设置页同步)、**"清除特征数据库"按钮**(开发者用,二次确认后调 `PhotoDatabase.DeleteDatabaseAsync` + `DinoFeatureCache.InvalidateAll` + `ShakeFlagService.InvalidateAll`)。 |
| [SettingsViewModel.Persistence.cs](SettingsViewModel.Persistence.cs) | 共用 | 各 partial 的 JSON 读写、默认值回滚、跨会话状态保留(如 `SimilarityPanelExpanded`)。 |
