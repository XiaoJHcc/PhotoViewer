# Core/Platform — 平台能力抽象

> 模块内手册。具体平台实现（`LibHeifDecoder` / `MacNativeApplication` / `AndroidNativeShell` 等）在各 head project 的 `Core/` 或 `Native/` 文件夹，启动期通过 `Initialize(...)` 注入。外部打开统一走实例级 `OpenRequestChannel` + `OpenCoordinator`（`PhotoViewer/ViewModels/Main/OpenCoordinator.cs`），不使用静态打开服务。存储访问租约在 `Core/Storage` 的 `IStorageAccessSessionFactory`，由各端注入。平台矩阵见[架构文档](../../../Design/architecture.md) §4。

`namespace PhotoViewer.Core.Platform`

| 文件 | 模块 | 职责 |
|---|---|---|
| [PerformanceBudget.cs](PerformanceBudget.cs) | 性能预算 | Static facade. Exposes memory cap, CPU cores, native-preload thread limit, and the settings-page defaults (`GetRecommendedCacheMemoryMB` / `GetRecommendedCacheCount` / `GetRecommendedNativePreloadParallelism` / `GetRecommendedCpuPreloadParallelism`, implemented as interface default methods so platforms only override where the measured model differs). |
| [OpenRequestChannel.cs](OpenRequestChannel.cs) | 外部打开请求通道 | 实例级队列，不持有静态状态：`Publish` 对同目标、未带完成源的 pending 请求去重合并，`RunAsync` 只允许一个消费者并按投递顺序处理，handler 异常交给统一出口后继续下一条，`Complete` 排空后退出。界面就绪前的请求在通道里排队，不丢失；验收可用协调器的 `PublishAndWaitAsync` 等待本批完成（OPEN-01/OPEN-06）。 |
| [NativeWindowPresenter.cs](NativeWindowPresenter.cs) | 原生窗口展示门面 | `INativeWindowPresenter` 设置/工具窗口展示;启动期由各端 UI 层注入（VM 不直接构造原生窗口）。 |
| [UiDispatcher.cs](UiDispatcher.cs) | UI 线程调度门面 | `IUiDispatcher` + 静态门面,启动期由各端 UI 层注入(Core/ViewModels 不直接引用平台调度器);附带 Rx 流用的 `MainThreadScheduler`。 |
| [StatusMessages.cs](StatusMessages.cs) / [UserFacingException.cs](UserFacingException.cs) | 提示条文案 | STATE-03 操作结果提示条文案（`动作：对象（原因）`）：评分写回、全分辨率升级、打开文件/文件夹失败、单图刷新访问边界与内存关闭雾天增强；共享层与四端壳一律从这里取文案，壳只负责显示。`ReasonFor` 把存储、权限、内容损坏、XMP 解析等异常归类为中文原因，未知异常不直接暴露类型名或内部消息，完整异常仍写日志；只有显式 `UserFacingException` 才保留调用方给出的用户可读原因。 |
