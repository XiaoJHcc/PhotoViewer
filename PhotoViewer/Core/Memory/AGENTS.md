# Core/Memory — 统一内存管控(MemoryGovernor)

> 模块内手册。压力响应矩阵与跨模块联动(预取节制/缓存收窄/批量索引暂停)见根 `AGENTS.md` §5 与 [Plans/native-rebuild-phase-1.md](../../../Plans/native-rebuild-phase-1.md)。

`namespace PhotoViewer.Core.Memory`

| File | Responsibility |
|---|---|
| [MemoryGovernor.cs](MemoryGovernor.cs) | 统一内存管家(静态门面 + 平台注入预算源):client 登记表、压力状态机(周期探测 `ProbeInterval` 2s + OS 事件双源,进入/退出阈值迟滞 5%、好转逐级退)、解码准入 `TryAcquire`(**P0 豁免放行但记账;非 P0 预算不足直接拒绝不排队**;Warning 拒 P2/P3、Critical 拒一切非 P0)、在途账本(`AdmissionPermit.SetActual` 解码后复核、Dispose 归还)、压力事件快照 JSONL(`SettingsPathHelper` 目录下 `memory-events.jsonl`,级别迁移与拒批落盘供闪退回放)、`RecoveryHold`(Normal 恢复后 30s 渐进观察期)。**升级级联**:`SetLevel` 升档时对所有登记 client 同步 `Trim(level)`(契约:不阻塞)。已登记 client:BitmapLoader(统一 LRU)、AnalysisResultCache、DinoFeatureCache、ShakeFlagService、DinoFeatureExtractor(ONNX session)、ImageViewModel(增强位图)、ImageBufferPool(像素缓冲池)。**准入调用点**(P1-M3):BitmapLoader 主图解码、ThumbnailService 缩略图、BitmapPrefetcher 预取(经解码内准入)、FolderFeatureIndexer(DINO 缩略图 P3 + CV 大图 P3)。未初始化时(工具/测试进程)准入全放行、恒 Normal。 |
| [IMemoryClient.cs](IMemoryClient.cs) | 内存客户端契约:`Name` / `FootprintBytes` / `Trim(level)`。实现须线程安全、Trim 不阻塞(探测线程上调用)。 |
| [IMemoryBudgetProvider.cs](IMemoryBudgetProvider.cs) | 平台预算源契约:`GetBudgetBytes()`(本进程被杀前可用总量)+ `GetAvailableBytes()`(距上限余量),两者必须同口径。iOS = `os_proc_available_memory` + phys_footprint(task_info);Android = `ActivityManager` 系统水位 + `OnTrimMemory` 事件直报;桌面 = `DefaultMemoryBudgetProvider`。 |
| [DefaultMemoryBudgetProvider.cs](DefaultMemoryBudgetProvider.cs) | 桌面(Win/Mac)默认预算:CLR 可见总量 × 50% 为预算、减托管占用为可用。桌面无硬杀,压力主要服务可观测性与预取节制。 |
| [PerfTrace.cs](PerfTrace.cs) | 解码计时环形缓冲(200 样本):`BitmapLoader` P0 命中/miss 埋点写入;Governor 快照携带 miss p50/mean;验收门"P0 延迟降 ≥50%"的判读源。 |
| [PressureLevel.cs](PressureLevel.cs) | `Normal` / `Caution` / `Warning` / `Critical` 四级。阈值默认 40/25/15%(可用占预算比),公开静态属性可调(后续进设置页高级项)。 |
