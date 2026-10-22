# Core/Settings — 设置持久化

> 模块内手册。设置页 VM(9 个 partial + `MoveCommandParameter`)在 [ViewModels/Settings/](../../ViewModels/Settings/)，四个平台的设置页均为原生实现（macOS SwiftUI / iOS 半屏 / Windows 设置窗口 / Android BottomSheet），共享同一份 VM。

`namespace PhotoViewer.Core.Settings`

`NativeSettingsSession.cs`：四端原生设置共用的快照-工作副本-关闭回写机制，编辑期临时存储消费副本保存请求，不更改运行时缓存/解码/键盘映射；源 VM 回写后按原 JSON 契约持久化。Dispose 结束副本订阅。平台展示及跨模块流程见[架构文档](../../../Design/architecture.md) §5.6–§5.9。

| 文件 | 模块 | 职责 |
|---|---|---|
| [SettingsService.cs](SettingsService.cs) | 设置服务 | JSON-serialised config persistence (source-gen via `SettingsJsonContext`). 读取损坏文档先由 `ISettingsStorage.BackupCorruptAsync` 备份（文件改名 `settings.json.corrupt-<yyyyMMdd-HHmmss>`；Apple 偏好/Android SharedPreferences 写带时间戳的备份键）再以默认值启动；`LastLoadError`/`LastSaveError` 暴露最近一次失败（SETTINGS-14）。 |
| [SettingsModel.cs](SettingsModel.cs) | 设置模型 | 序列化 POCO，定义所有持久化字段及默认值。 |
| [NativeSettingsPresenter.cs](NativeSettingsPresenter.cs) | 原生设置展示器 | `INativeSettingsPresenter` 接口 + `TryPresent` 静态调用入口；macOS / iOS / Windows / Android 各实现原生弹窗或窗口，未注入时返回 false（不再有 Avalonia 模态回退）。 |
