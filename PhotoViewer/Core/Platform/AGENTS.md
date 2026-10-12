# Core/Platform — 平台能力抽象

> 模块内手册。具体平台实现(`LibHeifDecoder` / `MacExternalOpenBridge` 等)在各 head project 的 `Core/` 文件夹,启动期通过 `Initialize(...)` 注入。平台矩阵见根 `AGENTS.md` §4。

`namespace PhotoViewer.Core.Platform`

| File | 模块 | Responsibility |
|---|---|---|
| [PerformanceBudget.cs](PerformanceBudget.cs) | 性能预算 | Static facade. Exposes memory cap, CPU cores, native-preload thread limit. |
| [ExternalOpenService.cs](ExternalOpenService.cs) | 外部打开服务 | Pending-queue + dispatch for "Open With" / share-to flows. |
| [StorageAccessManager.cs](StorageAccessManager.cs) | 存储访问门面 | Platform security-scoped access (iOS/macOS sandbox, Android SAF)。入参为平台中立 `IStorageItemRef`(Core/Storage),iOS 实现经 `AvaloniaFileRef.Inner` 解包。 |
| [NativeWindowPresenter.cs](NativeWindowPresenter.cs) | 原生窗口展示门面 | `INativeWindowPresenter` 设置/工具窗口展示;启动期由 UI 层注入(P0-M2 起 VM 不再直接构造 `Window`)。 |
| [UiDispatcher.cs](UiDispatcher.cs) | UI 线程调度门面 | `IUiDispatcher` + 静态门面,启动期由 UI 层注入(P0-M1 起,Core/ViewModels 不再直接引用 `Dispatcher.UIThread`);附带 Rx 流用的 `MainThreadScheduler`。 |
