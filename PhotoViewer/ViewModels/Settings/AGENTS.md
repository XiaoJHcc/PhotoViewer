# ViewModels/Settings — 设置页 VM

> 模块内手册。`SettingsViewModel` 是 partial 类,按分类拆 9 个文件,共享同一份 VM 实例（另有 `MoveCommandParameter` 作为排序命令参数）。四个平台的设置页都是原生实现：macOS SwiftUI（[MacNativeSettingsPresenter.cs](../../../PhotoViewer.Mac/Native/MacNativeSettingsPresenter.cs)）、iOS 半屏浮窗（[iOSNativeSettingsPresenter.cs](../../../PhotoViewer.iOS/Core/iOSNativeSettingsPresenter.cs)）、Windows 设置窗口（[WindowsNativeSettingsPresenter.cs](../../../PhotoViewer.Windows/Native/WindowsNativeSettingsPresenter.cs)）、Android BottomSheet（[AndroidNativeSettingsPresenter.cs](../../../PhotoViewer.Android/Native/AndroidNativeSettingsPresenter.cs)），都共享本 VM。

`namespace PhotoViewer.ViewModels.Settings`

五个分页:**文件 / 预览 / 控制 / EXIF / AI**。每个 partial 持有一类设置项,改动通过 `SettingsService` 持久化到 JSON。

macOS P2 通过 Core/Settings/NativeSettingsSession 建工作副本，`applyRuntimeSettings: false` 隔离缓存/解码/键盘映射副作用，原生窗口关闭后 ApplySnapshot 一次回写；副本 Dispose 释放全局内存通知与保存订阅。Windows W-M5 用同一 `NativeSettingsSession` 与 `WindowsNativeSettingsPresenter`：独立窗口、五分类、关闭一次性回写；WinUI 模板由根 `App.xaml` + `XamlControlsResources` 提供，界面用 `NavigationView` + `SettingsCard` + `NumberBox`/`InfoBar`（见 [PhotoViewer.Windows/AGENTS.md](../../../PhotoViewer.Windows/AGENTS.md)）。预算提示按代理档在 Normal 压力下的展示编码每像素字节（原生 F16=8B）估算适屏张数。UI 实现见 PhotoViewer.Mac/AGENTS.md。

| 文件 | 分类 | 职责 |
|---|---|---|
| [MoveCommandParameter.cs](MoveCommandParameter.cs) | 共用 | 三张可排序表（文件格式 / EXIF 显示项 / 控制栏命令）的排序命令参数 `FromIndex` → `ToIndex`，各端原生拖拽结束后回写共享集合。 |
| [SettingsViewModel.cs](SettingsViewModel.cs) | 主类 | 共享构造、`SettingsService` 注入、各 partial 共用的 `RaisePropertyChanged` 与持久化触发逻辑。 |
| [SettingsViewModel.FileFormats.cs](SettingsViewModel.FileFormats.cs) | 文件 | 启用/禁用各扩展名,文件后缀解析顺序。 |
| [SettingsViewModel.BitmapCache.cs](SettingsViewModel.BitmapCache.cs) | 预览 | 内存上限、解码并发、预取邻居数；四项默认值由平台预算源 `IPerformanceBudget.GetRecommended*` 给出（Windows 见 `PhotoViewer.Windows/Core/WindowsPerformanceBudget.cs`，其余平台用接口默认实现），已保存的设置覆盖默认值。预载默认值按推荐的缓存张数换算：张数达到基准 30 张时即默认的上一张 10 / 下一张 5 / 列表滚动 5，张数更少时按同一比例收敛并受预载上限（张数 ÷ 3）约束，保证「前后预载 + 当前 + 对比」不超过缓存张数。预算提示第一行为 CPU 核数与运行时内存预算，第二行按实际缓存容量（用户上限与推导上限取小；自动口径只取推导上限）与适屏代理尺寸换算可缓存张数，并与"前后预载 + 当前 + 对比"所需张数比较定级，容量随 `MemoryGovernor.Probed`（限流后经 UI 线程）刷新。`ShowsCacheLimits` 为 false（iPad 自动口径）时不显示两项上限，预载上限固定为 10。 |
| [SettingsViewModel.ImagePreview.cs](SettingsViewModel.ImagePreview.cs) | 预览 | 主图区显示选项、letterbox 行为；缩放预设校验（百分比 >0 且 ≤1600，非法输入不写入并恢复原值）与至少保留一项（`CanRemoveScalePreset`，SETTINGS-07）。 |
| [SettingsViewModel.Layout.cs](SettingsViewModel.Layout.cs) | 控制 | 文件栏行/列布局、显示选项。 |
| [SettingsViewModel.Hotkeys.cs](SettingsViewModel.Hotkeys.cs) | 控制 | 快捷键绑定与主/次绑定冲突检测；录制由各端原生录制控件完成（macOS `NSEvent`、Windows `WindowsHotkeyRecorder`、移动端只读展示），共享层只接收结果。 |
| [SettingsViewModel.Rating.cs](SettingsViewModel.Rating.cs) | 控制 | 星级显示与安全原位写回开关；当前没有自动 RAW sidecar 创建/关联（见[架构文档](../../../Design/architecture.md) §5.3）。 |
| [SettingsViewModel.ExifDisplay.cs](SettingsViewModel.ExifDisplay.cs) | EXIF | EXIF 详情页的字段展示开关、汉化偏好。 |
| [SettingsViewModel.AI.cs](SettingsViewModel.AI.cs) | AI | 相似聚类阈值(75%~95% / 默认 85%)、最多数量(1~32 / 默认 8，NumberBox)、**分析诊断"即时计算DINO/CV"开关**(默认关;门控分析栏增强实时重算与未提取回退,iOS 原生设置页同步)、**"清除特征数据库"按钮**(开发者用;有系统确认控件的壳调 `ClearFeatureDatabaseConfirmedAsync` 直接执行，无系统确认控件的壳沿用 3 秒 arm 态 `ClearDatabaseCommand`;均调 `PhotoDatabase.DeleteDatabaseAsync` + `DinoFeatureCache.InvalidateAll` + `ShakeFlagService.InvalidateAll`)。 |
| [SettingsViewModel.Persistence.cs](SettingsViewModel.Persistence.cs) | 共用 | 各 partial 的 JSON 读写、默认值回滚、跨会话状态保留(如 `SimilarityPanelExpanded`)；`PersistenceStatus` 显示读取失败/损坏备份/保存失败，保存成功后清除保存错误（SETTINGS-14）。 |
