# Core/Settings — 设置持久化

> 模块内手册。设置页 VM(9 个 partial + `MoveCommandParameter`)在 [ViewModels/Settings/](../../ViewModels/Settings/)，四个平台的设置页均为原生实现（macOS SwiftUI / iOS 半屏 / Windows 设置窗口 / Android BottomSheet），共享同一份 VM。

`namespace PhotoViewer.Core.Settings`

设置文档只有一个版本：`schemaVersion = 2`（`SettingsDocumentCodec.SchemaVersion`）。旧版或缺失版本不迁移，备份成功后才以默认值运行；文档是唯一的持久化契约，功能代码只消费不可变 `AppSettings` 快照。

`SettingsRepository` 是设置文档的唯一写者：读取时区分缺失/正常/已恢复/不可读/版本过高/旧版本；提交时先归一化，再后台写存储，**保存成功后才发布**为 `Current`，失败保留 last good 并记录 `LastSaveError`。普通 IO 失败、未来版本或备份失败进入不可写状态（`CanCommit = false`），绝不覆盖原文件。没有自动保存，也不做节流合并。

| 文件 | 模块 | 职责 |
|---|---|---|
| [AppSettings.cs](AppSettings.cs) | 不可变设置快照 | `sealed record`，所有集合为 `ImmutableArray`，修改通过 `with` 生成新快照；`ContentEquals` 按字段内容比较，作为“是否需要写盘”的判据。字段只含行为设置，不含面板展开态等视图状态。 |
| [SettingsCatalog.cs](SettingsCatalog.cs) | 目录与派生元数据 | 文件格式、EXIF 字段与命令目录，以及它们的默认表；命令名、提示、图标、扩展名按 id 派生，历史展示字段不进入设置文档。 |
| [SettingsDefaults.cs](SettingsDefaults.cs) | 首次运行默认值 | 按 `IPerformanceBudget` 推导缓存内存/张数与解码并发；预载份数按推荐张数换算（30 张基准为 10 / 5 / 5，更少时同比例收敛并受张数 ÷ 3 约束）；自动口径（iPad / Android）的预载上限固定 10。 |
| [SettingsDocumentCodec.cs](SettingsDocumentCodec.cs) | 文档编解码与归一化 | `schemaVersion = 2` 的读取/序列化；缺字段取默认值，未知字段与未知 id 忽略，三张表按 id 合并并保留用户顺序，数值按范围夹取。版本缺失或 `< 2` 判旧版、`> 2` 判未来版本，两者都不做迁移。 |
| [SettingsDocumentDto.cs](SettingsDocumentDto.cs) / [SettingsDocumentJsonContext.cs](SettingsDocumentJsonContext.cs) | 文档 DTO 与源生成上下文 | 可空字段只表示 JSON 中缺失；序列化走源生成，避免反射。 |
| [SettingsRepository.cs](SettingsRepository.cs) | 唯一写者 | 初始化读取与提交写入均经写锁串行；损坏/旧版先经 `ISettingsStore.BackupCorruptAsync` 备份再以默认值启动，备份最多 3 份；备份失败、读取 IO 失败或版本过高时进入不可写状态，原文保持不动。提交 = 归一化 → `SaveAsync` → 成功后发布 `Current`；未修改时不写盘。`StateChanged` 在完成操作的线程同步触发，调用方负责回到 UI 线程。 |
| [SettingsSession.cs](SettingsSession.cs) | 编辑会话 | 持有独立 draft；`CommitAsync` 一次性提交，首次成功后重复调用直接返回 true、不再写盘。 |
| [NativeSettingsSession.cs](NativeSettingsSession.cs) | 原生设置会话 | 从 live `SettingsViewModel` 建立工作副本；编辑期只改 `WorkingSettings`，不写盘；关闭时 `ApplyAsync` 一次提交并刷新源设置持久化状态，`Dispose` 释放副本。四个原生设置页共用。 |
| [ISettingsStore.cs](ISettingsStore.cs) | 存储契约 | 只负责原始 JSON 文本的读取/保存/损坏备份；解析、版本分类与备份时机归 `SettingsRepository`。 |
| [FileSettingsStore.cs](FileSettingsStore.cs) | 本地文件载体 | 写入走 `LocalFileRef.AtomicWriteAsync`（唯一临时名 + 原子替换）；损坏备份名为 `settings.json.corrupt-<时间戳>-<随机后缀>`，按创建时间只保留最近 3 份。 |
| [InMemorySettingsStore.cs](InMemorySettingsStore.cs) / [InMemorySettingsDocumentStore.cs](InMemorySettingsDocumentStore.cs) | 内存载体 | 隔离验收、工具与不需要落盘的场景；前者同时实现 `IViewStateStore`。 |
| [IViewStateStore.cs](IViewStateStore.cs) | 轻量视图状态 | 跨会话保留但不属于设置文档的布尔状态（当前为 `SimilarityPanelExpanded`）；Apple 用独立偏好键、Android 用壳偏好、Windows 用状态文件，缺失键与显式 false 区分。 |
| [NativeSettingsPresenter.cs](NativeSettingsPresenter.cs) | 原生设置展示器 | `INativeSettingsPresenter` 接口 + `TryPresent` 静态入口；macOS / iOS / Windows / Android 各实现原生弹窗或窗口，未注入时返回 false（不再有 Avalonia 模态回退）。 |
| [LayoutMode.cs](LayoutMode.cs) | 布局模式枚举 | `Auto` / `Row` / `Col`，对应设置页三选一与主屏分区。 |
| [SettingsPathHelper.cs](SettingsPathHelper.cs) | 默认数据目录 | Windows `%LOCALAPPDATA%`、Apple 沙盒 `Documents`、macOS `SpecialFolder.ApplicationData`、其他 XDG/`~/.config`；`photos.db` 与设置共用该基准目录。 |
