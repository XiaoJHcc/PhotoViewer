# ViewModels/Settings — 设置页 VM

> 模块内手册。`SettingsViewModel` 是 partial 类,按分类拆 10 个文件,共享同一份 VM 实例（另有 `MoveCommandParameter` 作为排序命令参数）。四个平台的设置页都是原生实现：macOS SwiftUI（[MacNativeSettingsPresenter.cs](../../../PhotoViewer.Mac/Native/MacNativeSettingsPresenter.cs)）、iOS 半屏浮窗（[iOSNativeSettingsPresenter.cs](../../../PhotoViewer.iOS/Core/iOSNativeSettingsPresenter.cs)）、Windows 设置窗口（[WindowsNativeSettingsPresenter.cs](../../../PhotoViewer.Windows/Native/WindowsNativeSettingsPresenter.cs)）、Android BottomSheet（[AndroidNativeSettingsPresenter.cs](../../../PhotoViewer.Android/Native/AndroidNativeSettingsPresenter.cs)），都共享本 VM。

`namespace PhotoViewer.ViewModels.Settings`

五个分页:**文件 / 预览 / 控制 / EXIF / AI**。每个 partial 持有一类设置项；live 构造的编辑经单一保存循环归一化后提交 `SettingsRepository`（后台保存、成功才发布，失败保留 last good），draft 构造只更新 `SettingsSession.Draft`（不写盘），隔离构造不读文件。没有自动保存或节流合并。

`NativeSettingsSession` 由 live 设置建立 `SettingsSession` 工作副本，编辑期不写盘，关闭时 `ApplyAsync` 提交一次；副本 Dispose 释放 `WorkingSettings`。Windows W-M5 用同一 `NativeSettingsSession` 与 `WindowsNativeSettingsPresenter`：独立窗口、五分类、关闭一次性回写；WinUI 模板由根 `App.xaml` + `XamlControlsResources` 提供，界面用 `NavigationView` + `SettingsCard` + `NumberBox`/`InfoBar`（见 [PhotoViewer.Windows/AGENTS.md](../../../PhotoViewer.Windows/AGENTS.md)）。设备能力提示两行由共享 [DeviceCacheCapability.cs](../../Core/Image/DeviceCacheCapability.cs) 算出（系统总内存 / 当前可用，以及两档可缓存张数——可编辑平台按工作副本的用户内存上限、自动平台按设备最大安全容量），用户内存变化立即重算张数，缓存数量/线程/预载不影响；超限只抬级并在第一行追加简短警告。

| 文件 | 分类 | 职责 |
|---|---|---|
| [MoveCommandParameter.cs](MoveCommandParameter.cs) | 共用 | 三张可排序表（文件格式 / EXIF 显示项 / 控制栏命令）的排序命令参数 `FromIndex` → `ToIndex`，各端原生拖拽结束后回写共享集合。 |
| [SettingsViewModel.cs](SettingsViewModel.cs) | 主类 | live / draft / 隔离构造，仓库 `StateChanged` 订阅与 `Repository` / `Session` 暴露，各 partial 共用的属性通知与编辑分发。 |
| [SettingsViewModel.Presentation.cs](SettingsViewModel.Presentation.cs) | 共用 | 原生展示刷新契约：`SettingsChangeImpact` 旗标、`PresentationChanged` 事件、`GetPresentationImpact` 白名单映射、`ApplySnapshot` / 排序批次合并与 Dispose 收口（见下文「展示刷新通道」）。 |
| [SettingsViewModel.FileFormats.cs](SettingsViewModel.FileFormats.cs) | 文件 | 启用/禁用各扩展名,文件后缀解析顺序。 |
| [SettingsViewModel.BitmapCache.cs](SettingsViewModel.BitmapCache.cs) | 预览 | 内存上限、解码并发、预取邻居数；四项默认值由平台预算源 `IPerformanceBudget.GetRecommended*` 给出（Windows 见 `PhotoViewer.Windows/Core/WindowsPerformanceBudget.cs`，Android 见 `PhotoViewer.Android/Core/AndroidPerformanceBudget.cs`，其余平台用接口默认实现），已保存的设置覆盖默认值；缓存张数按 1–400、内存按 512–32768 MB 在输入与文档归一化两处使用同一范围。滚动预载延时为 100–2000 ms、步进 100 ms（默认 1000 ms），工作副本输入经 `SettingsDocumentCodec.NormalizeVisibleCenterDelayMs` 按最近刻度归一（正中间向上取整），四端拉条/步进都直接用实际毫秒值。预载默认值按推荐的缓存张数换算：张数达到基准 30 张时即默认的上一张 10 / 下一张 5 / 列表滚动 5，张数更少时按同一比例收敛并受预载上限（张数 ÷ 3）约束，保证「前后预载 + 当前 + 对比」不超过缓存张数。**设备能力提示**：`PerformanceBudgetInfo` 为第一行（系统物理内存总量 / 当前可用，取自 `MemoryGovernor.GetSystemMemoryInfo()`，未知显式写"未知"，超限时在同一行追加简短警告），`BitmapCacheCountInfo` 为第二行（「可缓存 xx 张适屏照片 / xx 张 3300 万像素照片」，未知项写「适屏照片：未知」「3300 万像素照片：未知」）。两行都由 `DeviceCacheCapability` 算出：可编辑缓存上限的平台容量直接取工作副本的用户内存上限（不乘安全比例、不按系统可用/预算/数量上限截断），自动管理平台取 `DeviceSafeCapacity`（预算 × (1 − (Caution + 5)/100)，默认 55%）；两档各按 Normal 压力字节数（`ProxyBytesPerPixel` / `NormalFullBytesPerPixel`）换算，不扣 footprint、不套 256MB 下限、不按 400 条截断、不受缓存数量/线程/预载影响；用户内存变化立即重算张数，读数随 `MemoryGovernor.Probed` 限流 500 ms 后经 UI 线程刷新且只发属性通知。**超限警告**只在 `ShowsCacheLimits` 为 true 时判定，阈值一律取设备最大安全容量：超过系统总内存或平台应用内存预算为 Error，超过系统当前可用内存或设备最大安全容量为 Warning，文案固定为「（当前设置超过系统总内存/应用内存/系统可用内存/设备缓存上限）」不带数字，等于阈值不告警、未知读数不误判；`PerformanceBudgetInfoLevel` 抬级而张数仍反映所选内存，`BitmapCacheCountInfoLevel` 恒为常规（接口保留供四端接线）。`ShowsCacheLimits` 为 false（iPad / Android 自动口径）时不显示两项上限、不告警，张数按设备最大安全容量。|
| [SettingsViewModel.ImagePreview.cs](SettingsViewModel.ImagePreview.cs) | 预览 | 主图区显示选项、letterbox 行为；缩放预设校验（百分比 >0 且 ≤1600，非法输入不写入并恢复原值）与至少保留一项（`CanRemoveScalePreset`，SETTINGS-07）。 |
| [SettingsViewModel.Layout.cs](SettingsViewModel.Layout.cs) | 控制 | 文件栏行/列布局、显示选项。 |
| [SettingsViewModel.Hotkeys.cs](SettingsViewModel.Hotkeys.cs) | 控制 | 快捷键绑定与主/次绑定冲突检测；录制由各端原生录制控件完成（macOS `NSEvent`、Windows `WindowsHotkeyRecorder`、移动端只读展示），共享层只接收结果。 |
| [SettingsViewModel.Rating.cs](SettingsViewModel.Rating.cs) | 控制 | 星级显示与安全写回开关；评分写入由共享 `RatingService`（存储选择由 `XmpRatingStoreFactory`）统一处理：组内各文件内嵌 XMP 与每个已存在的 `<stem>.xmp` 都是来源，统一写入、不创建 sidecar（见[架构文档](../../../Design/architecture.md) §5.3、[RATING](../../../Design/spec/rating.md)）。 |
| [SettingsViewModel.ExifDisplay.cs](SettingsViewModel.ExifDisplay.cs) | EXIF | EXIF 详情页的字段展示开关、汉化偏好。 |
| [SettingsViewModel.AI.cs](SettingsViewModel.AI.cs) | AI | 相似聚类阈值(75%~95% / 默认 85%)、最多数量(1~32 / 默认 8，NumberBox)、**分析诊断"即时计算DINO/CV"开关**(默认关;门控分析栏增强实时重算与未提取回退,iOS 原生设置页同步)、**"清除特征数据库"按钮**(开发者用;有系统确认控件的壳调 `ClearFeatureDatabaseConfirmedAsync` 直接执行，无系统确认控件的壳沿用 3 秒 arm 态 `ClearDatabaseCommand`;均调 `PhotoDatabase.DeleteDatabaseAsync` + `DinoFeatureCache.InvalidateAll` + `ShakeFlagService.InvalidateAll`)。 |
| [SettingsViewModel.Persistence.cs](SettingsViewModel.Persistence.cs) | 共用 | `CreateSnapshot` / `ApplySnapshot` 使用不可变 `AppSettings`；live 编辑经单一保存循环提交 `SettingsRepository`（归一化→后台保存→成功才回填、失败保留 last good，无节流合并），draft 编辑只同步 `SettingsSession.Draft`；`PersistenceStatus` 来自仓库读取/保存错误（SETTINGS-14），仅作诊断与验收断言，四端设置页不呈现；`SimilarityPanelExpanded` 走 `IViewStateStore`，不进入设置文档。快捷键只持久化命令 id、顺序、显隐与主/次绑定，展示元数据由 `SettingsCatalog` 派生。 |

## 展示刷新通道

`PresentationChanged`（`Action<SettingsChangeImpact>`）只服务原生壳的展示结构刷新；`INotifyPropertyChanged` / ReactiveUI `Changed` 继续承担文本绑定与既有业务依赖，两者并行、互不替代。原生壳不得再因任意 `PropertyChanged` 重建结构，只按旗标位刷新对应区域。

- `SettingsChangeImpact`（`SettingsViewModel.Presentation.cs`）为 `[Flags]`：`Layout` / `Navigator` / `Rating` / `Exif` / `Commands` / `InputBindings` / `FileProjection` / `Analysis` / `Similarity`，`All` 为并集；未列出的属性返回 `None`，null / 空属性名（“全部属性已变”）返回 `All`。
- `GetPresentationImpact(propertyName)` 是正向白名单：`LayoutMode`→Layout，`ShowZoomIndicator`→Navigator，`ShowRating`→Rating，`EnabledExifItems`→Exif，`Hotkeys` 与四项 Apple 键盘映射→Commands|InputBindings，`SelectedFormats` / `SameNameAsOnePhoto`→FileProjection，`AnalysisImmediateCompute`→Analysis，`SimilarityPanelExpanded` / `SimilarityThreshold` / `SimilarityMaxResults`→Similarity。诊断与文本属性（`BitmapCacheCountInfo[Level]`、`PerformanceBudgetInfo[Level]`）、缓存预算、缩放预设均为 `None`，新增属性默认不触发展示刷新。
- 订阅 `Changed` 统一转换发布；热键集合增删、整体替换与排序、子项 `Command` / `IsDisplay` / 主次绑定显式发布 Commands|InputBindings（替换时旧集合退订、挂接幂等），冲突标记与展示文本等派生通知不发布。`ResetHotkeys` 内容按值一致时直接返回，保持集合与子项 identity；清空/重建时按跟踪集合补齐退订，`Dispose` 一并清理集合与子项订阅。`SelectedFormats` 内容不变时不换列表；`EnabledExifItems` 仍换新实例列表供消费者使用，但展示通道按 id/显示名顺序摘要去重。因此相同快照回填、仅缓存预算变更也不会误发 FileProjection/Exif。
- `ApplySnapshot` 与 `MoveHotkey` / `MoveFileFormat` / `MoveExifDisplay` 使用批次：最外层结束只发一次合并旗标（异常退出也发布已应用部分，允许嵌套）；Dispose 清空订阅、在途批次与事件，之后不再发布；构造初值不发布。
- 周期内存探测（`MemoryGovernor.Probed` → `BitmapCacheCountInfo`）只走属性通知，零展示事件。契约由 `Tools/CoreContractCheck` 的 `SettingsPresentationChecks` 锁定。
