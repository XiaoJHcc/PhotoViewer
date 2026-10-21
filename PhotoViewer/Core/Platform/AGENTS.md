# Core/Platform — 平台能力抽象

> 模块内手册。具体平台实现（`LibHeifDecoder` / `MacNativeApplication` 的外部打开 / `AndroidNativeShell.RegisterExternalOpen` 等）在各 head project 的 `Core/` 或 `Native/` 文件夹，启动期通过 `Initialize(...)` 注入。平台矩阵见[架构文档](../../../Design/architecture.md) §4。

`namespace PhotoViewer.Core.Platform`

| 文件 | 模块 | 职责 |
|---|---|---|
| [PerformanceBudget.cs](PerformanceBudget.cs) | 性能预算 | Static facade. Exposes memory cap, CPU cores, native-preload thread limit, and the settings-page defaults (`GetRecommendedCacheMemoryMB` / `GetRecommendedCacheCount` / `GetRecommendedNativePreloadParallelism` / `GetRecommendedCpuPreloadParallelism`, implemented as interface default methods so platforms only override where the measured model differs). |
| [ExternalOpenService.cs](ExternalOpenService.cs) | 外部打开服务 | Pending-queue + dispatch for "Open With" / share-to flows. |
| [StorageAccessManager.cs](StorageAccessManager.cs) | 存储访问门面 | Platform security-scoped access (iOS sandbox, Android SAF)。入参为平台中立 `IStorageItemRef`(Core/Storage)，由 `iOSStorageAccessManager` / `AndroidStorageAccessManager` 原生实现；macOS 的安全作用域书签直接在 `MacStorageRefs` 内处理，桌面端不注入本门面。 |
| [NativeWindowPresenter.cs](NativeWindowPresenter.cs) | 原生窗口展示门面 | `INativeWindowPresenter` 设置/工具窗口展示;启动期由各端 UI 层注入（VM 不直接构造原生窗口）。 |
| [UiDispatcher.cs](UiDispatcher.cs) | UI 线程调度门面 | `IUiDispatcher` + 静态门面,启动期由各端 UI 层注入(Core/ViewModels 不直接引用平台调度器);附带 Rx 流用的 `MainThreadScheduler`。 |
| [StatusMessages.cs](StatusMessages.cs) | 提示条文案 | STATE-03 操作结果提示条文案（`动作：对象（原因）`）：评分写回、全分辨率升级、打开文件/文件夹失败与内存关闭增强；共享层与四端壳一律从这里取文案，壳只负责显示。 |
