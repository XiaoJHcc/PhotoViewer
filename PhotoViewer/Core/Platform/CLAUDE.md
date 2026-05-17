# Core/Platform — 平台能力抽象

> 模块内手册。具体平台实现(`LibHeifDecoder` / `MacExternalOpenBridge` 等)在各 head project 的 `Core/` 文件夹,启动期通过 `Initialize(...)` 注入。平台矩阵见根 `CLAUDE.md` §4。

`namespace PhotoViewer.Core.Platform`

| File | 模块 | Responsibility |
|---|---|---|
| [PerformanceBudget.cs](PerformanceBudget.cs) | 性能预算 | Static facade. Exposes memory cap, CPU cores, native-preload thread limit. |
| [ExternalOpenService.cs](ExternalOpenService.cs) | 外部打开服务 | Pending-queue + dispatch for "Open With" / share-to flows. |
| [StorageAccessManager.cs](StorageAccessManager.cs) | 存储访问门面 | Platform security-scoped access (iOS/macOS sandbox, Android SAF). Long-term retention + transient scopes. |
