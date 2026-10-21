# Core/Memory — 统一内存管控(MemoryGovernor)

> 模块内手册。压力响应矩阵与跨模块联动(预取节制/缓存收窄/批量索引暂停)见[架构文档](../../../Design/architecture.md) §5 与 [Plans/native-rebuild-phase-1.md](../../../Plans/native-rebuild-phase-1.md)。

`namespace PhotoViewer.Core.Memory`

| 文件 | 职责 |
|---|---|
| [MemoryGovernor.cs](MemoryGovernor.cs) | 统一内存管家(静态门面 + 平台注入预算源):client 登记表、压力状态机(周期探测 `ProbeInterval` 2s + OS 事件双源,进入/退出阈值迟滞 5%、好转逐级退)、解码准入 `TryAcquire`(**P0 豁免放行但记账;非 P0 预算不足直接拒绝不排队**;Warning 拒 P2/P3、Critical 拒一切非 P0)、在途账本(`AdmissionPermit.SetActual` 解码后复核、Dispose 归还)、压力事件快照 JSONL(`SettingsPathHelper` 目录下 `memory-events.jsonl`,含 `init` 启动上限快照/级别迁移/拒批,供闪退回放)、`RecoveryHold`(Normal 恢复后 30s 渐进观察期)、`BudgetBytes`/`FootprintBytes` 与每轮探测后的 `Probed` 事件(缓存据此重算有效容量)。**升级级联**:`SetLevel` 升档时对所有登记 client 同步 `Trim(level)`(契约:不阻塞)。已登记 client:BitmapLoader(统一 LRU)、AnalysisResultCache、DinoFeatureCache、ShakeFlagService、DinoFeatureExtractor(ONNX session)、PhotoLoadSession(增强位图与对比缓存)、ImageBufferPool(像素缓冲池:Caution 起清 ≥1MB 空闲,Critical 清空,释放量达标时后台 GC)。**准入调用点**(P1-M3):BitmapLoader 主图解码、ThumbnailService 缩略图、BitmapPrefetcher 预取(经解码内准入)、FolderFeatureIndexer(DINO 缩略图 P3 + CV 大图 P3)。未初始化时(工具/测试进程)准入全放行、恒 Normal。 |
| [IMemoryClient.cs](IMemoryClient.cs) | 内存客户端契约:`Name` / `FootprintBytes` / `Trim(level)`。实现须线程安全、Trim 不阻塞(探测线程上调用)。 |
| [IMemoryBudgetProvider.cs](IMemoryBudgetProvider.cs) | 平台预算源契约:`GetBudgetBytes()`(本进程被杀前可用总量)+ `GetAvailableBytes()`(距上限余量)+ `GetFootprintBytes()`(当前驻留,供缓存扣除"非缓存占用"),三者必须同口径。iOS = `os_proc_available_memory` + `phys_footprint`(读法在 `PhotoViewer.Apple/AppleMemoryFootprint`);Android = `ActivityManager` 系统水位 + `OnTrimMemory` 事件直报(footprint 未知返回 0);桌面 = `DefaultMemoryBudgetProvider`(Windows)/ `MacMemoryBudgetProvider`(macOS,物理内存 × 50% − phys_footprint)。 |
| [DefaultMemoryBudgetProvider.cs](DefaultMemoryBudgetProvider.cs) | 桌面默认预算(Windows):CLR 可见总量 × 50% 为预算、减托管占用(`GC.GetTotalMemory(false)`)为可用。桌面无硬杀,压力主要服务可观测性与预取节制。 |
| [PerfTrace.cs](PerfTrace.cs) | 解码计时环形缓冲(200 样本):`BitmapLoader` P0 命中/miss 埋点写入;Governor 快照携带 miss p50/mean;验收门"P0 延迟降 ≥50%"的判读源。 |
| [PressureLevel.cs](PressureLevel.cs) | `Normal` / `Caution` / `Warning` / `Critical` 四级。阈值默认 40/25/15%(可用占预算比),公开静态属性可调(后续进设置页高级项)。 |
