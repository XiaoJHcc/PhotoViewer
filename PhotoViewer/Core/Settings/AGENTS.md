# Core/Settings — 设置持久化

> 模块内手册。设置页 VM(9 个 partial)在 [ViewModels/Settings/](../../ViewModels/Settings/),Views 在 [Views/Settings/](../../Views/Settings/);iOS 走原生设置页([PhotoViewer.iOS/Core/iOSNativeSettingsPresenter.cs](../../../PhotoViewer.iOS/Core/iOSNativeSettingsPresenter.cs)),共享同一份 VM。

`namespace PhotoViewer.Core.Settings`

| File | 模块 | Responsibility |
|---|---|---|
| [SettingsService.cs](SettingsService.cs) | 设置服务 | JSON-serialised config persistence (source-gen via `SettingsJsonContext`). |
| [SettingsModel.cs](SettingsModel.cs) | 设置模型 | 序列化 POCO，定义所有持久化字段及默认值。 |
| [NativeSettingsPresenter.cs](NativeSettingsPresenter.cs) | 原生设置展示器 | `INativeSettingsPresenter` 接口 + `TryPresent` 静态调用入口；平台层实现原生弹窗，移动端回退到 Avalonia 模态。 |
