# Phase 1 执行清单 — 统一内存架构 + 分级代理解码

> 2026-10-19 立项,隶属 [native-rebuild-master-plan-1.md](native-rebuild-master-plan-1.md) §6 Phase 1。前置:Phase 0 已完成([native-rebuild-phase-0.md](native-rebuild-phase-0.md) 验收门与发现清单先读)。
> 平台:全部代码在共享层,Windows/Mac 均可开发;**验收门需要 iPad 真机**(内存痛点主场)。
>
> **铁律**(沿用 Phase 0):
> 1. 每里程碑:Desktop 构建零错误、无新增警告 → 审查 → 单独提交。
> 2. 画质红线:fit 显示与 100% 放大不接受视觉可见降分辨率(§3 M4 的分级语义论证见其处)。
> 3. 函数 XML 中文注释;不改 UI 字符串(设置页新增项除外);计划外发现记入 §5。
> 4. 本阶段**允许行为变化**(性能/内存语义),但需逐条列出;不改评分/指纹/DB schema 等持久化契约。

---

## 1. 验收门(Phase 1 出口标准)

- [ ] iPad(尽量低配在役机型)42MP 连翻 500 张:无闪退、无内存告警死循环;压力事件日志可回放;
- [ ] 切图 P0 延迟(缓存 miss,fit 模式)较 Phase 0 版下降 ≥50%;100% 放大画质目检无回归;
- [ ] 内存告警后 30s 内水位不立即回填(预取恢复渐进);
- [ ] Windows/Mac/Android 桌面回归:翻图/缩放/评分/设置/聚类/分析栏全过;
- [ ] Core 新增代码零 Avalonia 引用;构建零错误零新增警告。

## 2. 基线测量(开工先做,半天)

在 Phase 0 版上记录:42MP 连翻的 P0 miss 耗时分布、`BitmapLoader` 峰值内存、iPad 闪退复现路径(若有)、切图 frame time。写入本文 §5,作为验收对照。

## 3. 里程碑

### P1-M1 — MemoryGovernor 骨架
- 新增 `PhotoViewer/Core/Memory/`:
  - `IMemoryBudgetProvider`(平台注入):返回"应用可用内存预算 MB"与"当前可用 MB";iOS 走 `os_proc_available_memory` P/Invoke(缺失回退现机型表 `iOSPerformanceBudget`),Android 走 `ActivityManager.GetMemoryInfo`,桌面走物理内存比例;
  - `IMemoryClient { string Name; long FootprintBytes { get; } void Trim(PressureLevel level); }`;
  - `PressureLevel { Normal, Caution, Warning, Critical }`;
  - `MemoryGovernor` 静态门面:client 登记表、压力状态机(探测 + 事件双源)、`Acquire(long bytes)` 解码准入口、压力事件快照日志(写本地文件,可回放)。
- 压力探测:周期 ~2s 读可用内存 + 水位迟滞(进入/退出阈值错开,防抖);OS 事件(iOS DidReceiveMemoryWarning 已有、Android `OnTrimMemory` 本阶段接线 MainActivity)直接抬级。

### P1-M2 — 全部缓存登记 + 压力响应矩阵
- 实现 IMemoryClient 的:`BitmapLoader`(主缓存)/ 缩略图(`ImageFile.Thumbnail` 集合,经 FolderViewModel 登记)/ `AnalysisResultCache`(指纹 LRU + 即时缓存)/ `DinoFeatureCache` / `ShakeFlagService` / `ImageViewModel` 增强图 / ONNX session(`DinoFeatureExtractor` 卸载重建)。
- 响应矩阵(master plan §4.3):Caution=停 P2/P3+缩略图只留可视区 ±2 屏+批量索引暂停;Warning=主缓存砍到当前+1、分析/增强清空、ONNX 卸载、解码并发降 1;Critical=只留当前纹理。
- **恢复渐进**:Normal 恢复后延迟 ~30s 再逐步放开预取窗口(打破告警→回填循环)。

### P1-M3 — 解码准入 + 淘汰与生命周期修复
- 一切解码(P0 主图 / 缩略图 / P2 预取 / 批量索引 / CV·DINO)先 `Governor.Acquire(estimatedBytes)`;在途内存统一记账,解码完成按实际字节复核。
- 淘汰路径拆除阻塞式 `GC.Collect ×2` 与 UI 线程同步 Dispose(DecodedImage 纯托管,Dispose 即清引用;适配层 CWT 双份缓冲的消亡时机验证一把)。
- **use-after-dispose 竞态修复**:`GetBitmapAsync` 命中返回与并发淘汰之间加引用租约(`DecodedImage.Acquire()/Release()`,淘汰仅当计数为 0 才真正释放)。
- 缩略图生命周期:文件夹切换显式整批释放(消灭 `ClearThumbnail` 死代码路径);Governor Caution 起修剪离屏项。
- `ShakeFlagService` 既有竞态修复(枚举前快照,见 Phase 0 发现清单)。

### P1-M4 — 分级代理解码(性能核心)
- `BitmapLoader.GetBitmapAsync(file, DecodeSizeHint)`:`Proxy`(渲染表面物理像素 × overscan 1.5)与 `Full`;LRU 键 =(path, size-class)。
- 解码路径:JPEG → `SkiaImageCodec.DecodeToWidth`(已有);HEIF/RAW → 平台解码器 `maxSize` 参数(`IHeifDecoder.LoadThumbnailAsync` 语义对齐为"代理解码",Mac/iOS ImageIO 与 LibHeif 均支持);RAW Windows 无解码器(Phase 2 LibRaw)。
- `ImageViewModel`:fit/浏览用 proxy 先行上屏(预期数十 ms);缩放超过 proxy 物理像素能力时后台解码 Full,就绪后同变换无跳变热替换;快速翻页 latest-wins 取消 Full。
- P2 预取改为 proxy 邻居(窗口由 Governor 按预算下发),当前 ±1 保留 Full。
- **语义保证**:100% 及以上放大永远以 Full 呈现(固化测试);fit 下 proxy 像素 ≥ 屏幕物理像素 × overscan。

### P1-M5 — iPad 压测 + 收口
- 按 §1 验收门执行;压力日志回放检查;AGENTS.md(根 + Core/Memory 新模块 + Core/Image)同步;提交。

## 4. 提交纪律

每里程碑一个 commit,信息格式:`P1-M<n>: <摘要>`。

## 5. 基线数据与发现清单(执行中填写)

| 日期 | 发现 | 处置 |
|---|---|---|
| | | |
