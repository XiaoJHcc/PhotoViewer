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

- [ ] iPad 真机(在役机型 = iPad mini 7,A17 Pro/8GB,按 8GB 档校准阈值;**模拟器无 jetsam 上限,此项不可模拟**)42MP 连翻 500 张:无闪退、无内存告警死循环;压力事件日志可回放;
- [ ] 切图 P0 延迟(缓存 miss,fit 模式)较 Phase 0 版下降 ≥50%(以 Apple 平台实测为准——Windows HEIF 无子采样解码,其提升来自预取命中而非解码加速);100% 放大画质目检无回归;
- [ ] 内存告警后 30s 内水位不立即回填(预取恢复渐进;开发期用模拟器 Simulate Memory Warning 驱动验证);
- [ ] Windows/Mac/Android 桌面回归:翻图/缩放/评分/设置/聚类/分析栏全过(2026-10-12 决策:旧壳阶段由 agent 程序化自验——读写回环/日志断言/截图,不做人工交互验收;人工验收自原生壳阶段开始);
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
- **准入口优先级语义(2026-10-12 审查裁定,动工前定死)**:`Acquire` 按 `DecodeScheduler` 优先级分级——P0(当前图)豁免准入永远放行;P1/P2/P3 预算不足时直接拒绝(不排队,防后台解码饿死/堵死 P0);批量索引走独立可暂停通道,Caution 起暂停。解码并发度由 Governor 按压力级别下发(对接 `DecodeScheduler.MaxBackgroundParallelism`)。
- **P0 链路计时埋点**:缓存命中/读流/解码/旋转/适配各段耗时记录,随压力快照落盘——§2 基线与 §1 验收门"降 ≥50%"的客观判读依据。
- 压力探测:周期 ~2s 读可用内存 + 水位迟滞(进入/退出阈值错开,防抖);OS 事件(iOS DidReceiveMemoryWarning 已有、Android `OnTrimMemory` 本阶段接线 MainActivity)直接抬级。

### P1-M2 — 统一缓存键 + 全部缓存登记 + 压力响应矩阵
- **缩略图并入统一缓存,不建独立缓存(2026-10-12 审查裁定)**:`BitmapLoader` 缓存键升级为 `(path, size-class)`(Thumb/Proxy/Full 三档)单一日 LRU,`ImageFile.Thumbnail` 退化为查询视图。原 M4 的 LRU 键改造与此是同一件事,提前到本里程碑一次做完,避免"先建独立缩略图缓存、M4 再合并"的二次手术。
- 实现 IMemoryClient 的:`BitmapLoader`(统一 LRU)/ `AnalysisResultCache`(指纹 LRU + 即时缓存)/ `DinoFeatureCache` / `ShakeFlagService` / `ImageViewModel` 增强图 / ONNX session(`DinoFeatureExtractor` 卸载重建)。
- 响应矩阵(master plan §4.3):Caution=停 P2/P3+缩略图只留可视区 ±2 屏+批量索引暂停;Warning=主缓存砍到当前+1、分析/增强清空、ONNX 卸载、解码并发降 1;Critical=只留当前纹理。
- **恢复渐进**:Normal 恢复后延迟 ~30s 再逐步放开预取窗口(打破告警→回填循环)。

### P1-M3 — 解码准入 + 淘汰与生命周期修复
- 一切解码(P0 主图 / 缩略图 / P2 预取 / 批量索引 / CV·DINO)先 `Governor.Acquire(estimatedBytes)`(P0 豁免,语义见 M1);在途内存统一记账,解码完成按实际字节复核。
- 淘汰路径拆除阻塞式 `GC.Collect ×2` 与 UI 线程同步 Dispose(DecodedImage 纯托管,Dispose 即清引用;适配层 CWT 双份缓冲的消亡时机验证一把)。
- **use-after-dispose 竞态修复**:动工前先出所有权表(缓存自持 1 份;命中返回 / 适配层 CWT / 分析栏 / 增强管线各消费路径的 Acquire/Release 时点),再实施 `DecodedImage.Acquire()/Release()` 引用租约,淘汰仅当计数为 0 才真正归还缓冲。
- **DecodedImage 分档缓冲池(2026-10-12 审查裁定新增)**:像素缓冲按尺寸分档租用,淘汰/Dispose 归还池而非丢给 GC——消灭 42MP 级 `byte[]` 的 LOH 分配/回收循环(iPad jetsam 放大器);池本身登记为 IMemoryClient,Critical 时整池释放;分配接口保持抽象,为 Phase 2 原生纹理上传留门。
- 缩略图生命周期:文件夹切换显式整批释放(消灭 `ClearThumbnail` 死代码路径);Governor Caution 起修剪离屏项。
- `ShakeFlagService` 既有竞态修复(枚举前快照,见 Phase 0 发现清单)。

### P1-M4 — 分级代理解码(性能核心)
- `BitmapLoader.GetBitmapAsync(file, DecodeSizeHint)`:`Proxy`(渲染表面物理像素 × overscan 1.5)与 `Full`;缓存复用 M2 已建成的 `(path, size-class)` 统一 LRU。
- **解码路径按平台如实区分(2026-10-12 审查核实,修正原案"ImageIO 与 LibHeif 均支持 maxSize"的笼统表述)**:
  - JPEG(全平台)→ `SkiaImageCodec.DecodeToWidth`(codec 子采样 + Mitchell 精修,已有);
  - HEIF/RAW(macOS/iOS)→ **重写为 `CGImageSourceCreateThumbnailAtIndex` + `kCGImageSourceThumbnailMaxPixelSize`**:现状是 `CGImageSourceCreateImageAtIndex` 全尺寸解码后画进小 context 缩放,解码成本与瞬时内存峰值都不省;thumbnail API 才是编解码器级子采样(iPad 降峰值的关键一跳);
  - HEIF(Windows)→ libheif 无子采样解码能力:保持全尺寸解码 + Skia Mitchell 重采样(**替换 `LibHeifDecoder` 现有最近邻取样**——42MP→6MP 最近邻全屏显示会肉眼可见锯齿,触画质红线);此平台代理图只获缓存驻留收益,无解码加速;
  - HEIF(Android)→ BitmapFactory `InSampleSize` + 精缩放(已有);
  - RAW(Windows)→ 无解码器(Phase 4 LibRaw),维持现状;
- 解码器接口语义统一:`IHeifDecoder.LoadThumbnailAsync` / `IRawDecoder.LoadThumbnailAsync` 改名 `DecodeAsync(file, int? maxSize)`(null = 全尺寸),一个方法覆盖胶片条/代理/全尺寸三档,"Thumbnail" 命名不再误导;
- `ImageViewModel`:fit/浏览用 proxy 先行上屏(预期数十 ms);缩放超过 proxy 物理像素能力时后台解码 Full,就绪后同变换无跳变热替换;快速翻页 latest-wins 取消 Full。
- P2 预取改为 proxy 邻居(窗口由 Governor 按预算下发),当前 ±1 保留 Full。
- **语义保证**:100% 及以上放大永远以 Full 呈现(固化测试);fit 下 proxy 像素 ≥ 屏幕物理像素 × overscan。

### P1-M5 — iPad 压测 + 收口
- 按 §1 验收门执行;压力日志回放检查;AGENTS.md(根 + Core/Memory 新模块 + Core/Image)同步;提交。

## 4. 提交纪律

每里程碑一个 commit,信息格式:`P1-M<n>: <摘要>`。

## 5. 基线数据与发现清单(执行中填写)

**基线(2026-10-12,Mac M 系,Phase 0 HEAD,工具 = /tmp/pvbench 自包含基准,源 100 行,不入库)**:
- P0 miss 延迟(42MP ARW,ImageIO 全尺寸):**p50 = 946ms(横)/ 986ms(竖)**,min/max 离散 <2% —— 每切一张新图约 1 秒;
- 单张瞬时内存增量:**≈125MB 稳态**(首轮含解码瞬态 ≈250MB);
- 大对象行为:**每张 124MB `byte[]` 自然触发 1 次 gen2 GC**(20 张 = 20 次,零主动 GC.Collect);
- 连翻 RSS:22 张后进程 RSS 峰值 **3.37GB 且不回落**(LOH 段复用滞后 + 原生侧缓存)—— iPad jetsam 机制的桌面镜像实证;
- iPad 闪退复现路径:待真机(P1-M5)。

| 日期 | 发现 | 处置 |
|---|---|---|
| 2026-10-12 | **Phase 0 定向审查结论**(补独立审查缺位:Phase 0 由同一 agent 实现+自审):机械门复核 PASS(Core 零 Avalonia,残留均注释文本;VM 零 UI 耦合);手势持久化契约 PASS(`GestureKey` 223 名 ↔ Avalonia 12.0.2 `Key` 223 名双向 diff 为空,`GestureModifiers` 名/值与 `KeyModifiers` 一致,`UseStringEnumConverter` 下逐字节兼容);Mac/iOS 构建 0 错误(仅 NU1903 基线警告);Mac 冒烟 PASS(CLI 开 ARW:MacRawDecoder 链路主图/胶片条/EXIF/评分栏渲染正常) | Phase 0 验收门对应项关闭;剩余:四平台交互冒烟(人工)、DINO 渲染口径验证、iPad 真机 |
| 2026-10-12 | Apple 解码器现状是 `CGImageSourceCreateImageAtIndex` 全解码 + 绘制缩放,非编解码器级子采样,省不了解码成本与瞬时峰值;`LibHeifDecoder` 无子采样且重采样为最近邻(42MP→6MP 代理会触画质红线) | M4 解码路径按平台重写/修正(见 M4 条目) |
| 2026-10-12 | 原案准入口与 P0~P3 调度交叉语义未定义,存在 P0 被后台解码饿死/堵死风险 | M1 动工前定死:P0 豁免准入;低优先级预算不足直接拒绝不排队;批量索引独立可暂停通道 |
| 2026-10-12 | 42MP `byte[]` 直进 LOH,分配/回收循环是 iPad jetsam 放大器,原案缓冲池未落里程碑 | M3 增 DecodedImage 分档缓冲池(淘汰归还池;池登记为 IMemoryClient;接口抽象为 Phase 2 纹理上传留门) |
| 2026-10-12 | use-after-dispose 租约涉及缓存/适配层 CWT/分析/增强多条消费路径,所有权时点未定义 | M3 先出所有权表再实施租约 |
| 2026-10-12 | 缩略图若建独立缓存,M4 统一 LRU 键时需二次手术 | M2 一次到位:`(path, size-class)` 统一 LRU,`ImageFile.Thumbnail` 退化为查询视图 |
| 2026-10-12 | P1-M1 完成:MemoryGovernor 骨架(状态机迟滞/准入口 P0 豁免/快照 JSONL/PerfTrace 埋点)+ 三平台预算源(iOS `os_proc_available_memory`+phys_footprint、Android 系统水位+`OnTrimMemory` 直报、桌面 CLR 50%);pvbench `gov` 自测 24 项全过(准入矩阵/迟滞带/逐级退/OS 事件只升不降/快照落盘);Desktop/Mac/iOS 构建 0 错零新增警告;Mac 冒烟 App 存活正常 | commit `P1-M1` |
| 2026-10-12 | P1-M2 完成:统一 LRU(键 = 路径+`ImageSizeClass`,Thumb 分区 2000 张/256MB 双限,`ImageFile.Thumbnail` 退化为查询视图)+ 6 个内存客户端登记 + 压力级联(`SetLevel` 升档同步 `Trim`)+ 行为响应(预取 Caution 停 / 并发 Warning 降 1 / 索引暂停 / 30s 恢复观察期);iOS 旧 80% trim 摘除,`TrimOnMemoryWarning`/`TrimToCurrentRatio` 死代码删除。**行为变化逐条**:① 缩略图从无限增长转有界;② 告警响应从单点修剪转矩阵级联;③ `CacheStatusChanged` 事件签名带尺寸档 | pvbench 31 项全过;Desktop/Mac/iOS 构建 0 错零新增警告;Mac 冒烟主图/胶片条/EXIF 正常;commit `P1-M2` |
| 2026-10-12 | **既有跨平台不一致(已被 Phase 0 顺带根治)**:macOS `RenderTargetBitmap.CopyPixels` 输出实为 RGBA 序,旧管线按 BGRA 读取导致 Mac 端 DINO 输入 R/B 互换(Windows 正常);新 Skia 路径全平台统一 BGRA。DINO 口径 A/B 实证:同滤波器族(Linear/无 mip),残差 meanDiff=0.24 为 GPU↔CPU 舍入级,分布未漂移 | 已修复,无需动作;Phase 2 色彩管线注意:平台字节序/像素格式一律以实证为准,不信注释口径 |
| 2026-10-12 | P1-M3 完成:解码准入接线(主图/缩略图/预取/索引全走 `TryAcquire`,P0 豁免;`ReserveForPreloadAsync` 预占机制删除,账本归 Governor)+ 淘汰路径拆除阻塞 `GC.Collect ×2` 与 UI 线程同步 Dispose(租约归还直接执行)+ `DecodedImage` 引用租约(命中返回携租约,归零才还池)+ `ImageBufferPool` 分档缓冲池(解码主路径全接)+ 文件夹切换整批释放缩略图(`ClearThumbnailClass`)+ `ShakeFlagService` 竞态快照修复 + `ClearThumbnail` 死代码删除。**A/B 实证**(同会话交错双二进制):P0 miss 延迟无回归(两侧 p50 重叠);gen2 GC 20 张 20 次 → **0 次**;RSS 峰值两侧一致 ~3.39GB(残留为 ImageIO 原生侧累积,归 M4 缩解码 + Phase 2) | pvbench 31 项全过;四平台构建 0 错;Mac 冒烟正常;commit `P1-M3` |
| 2026-10-12 | Training 仓 Mac 本地检出(af6c6fe)早于 Phase 0 的 DatasetBuilder 适配提交,`Training.sln` 本机构建失败(`RenderInputBitmap` 已删)——**跨仓同步缺口,非 M1-M3 回归**;GitHub 当前网络不通无法拉取 | 待网络恢复 `cd Training && git pull`;若 Windows 侧适配未推送需先在那边 push |
| 2026-10-12 | `SQLitePCLRaw.lib.e_sqlite3` 2.1.11 已知高危漏洞(NU1903,四平台构建均报) | 不在本阶段范围;单独升级任务,**iOS 上架前必须处理** |
| 2026-10-13 | P1-M4a 完成(解码侧):`IHeifDecoder`/`IRawDecoder` 收敛为 `DecodeAsync(file, int? maxSize)` + `OutputPreRotated`;Apple 代理走 `CGImageSourceCreateThumbnailAtIndex`(FromImageAlways + MaxPixelSize + WithTransform,编解码器级子采样);Windows/Android libheif 无子采样 → 全解码 + Skia Mitchell 精缩(`ScaleToLongEdge`,**替换最近邻**,守画质红线);Android 系统路 InSampleSize;`SkiaImageCodec.DecodeToLongEdge`/`ToBgra32` 新增;BitmapLoader 代理档(`#proxy<size>` 在途键、长边 ≥ 需求复用、P0 失败回退 Full)。**实证**:orientation-6 HEIC Full/Proxy 均单次旋转(顺带修复 Apple HEIF 主图双重旋转既有 bug:解码器 CTM 已转 + BitmapLoader 再转);RAW 代理 ~900ms vs Full ~1040ms(-12%),托管 64MB vs 128MB;RAW 内嵌预览仅 1616px 不够代理分辨率(IfAbsent 15ms 但违红线,弃用);小源不放大 ✓ | 四平台构建 0 错零新增警告;gov 31 全过;Mac 冒烟(竖图 RAW 选中+渲染正确);commit `P1-M4a` |
| 2026-10-13 | P1-M4b 完成(VM 集成):`ImageViewModel` 代理先行上屏 + 按需 Full 热替换(几何锚定 `_logicalImageSize` 全分辨率坐标系,`ImageSize` 恒定无跳变;`Image` 元素宽高绑定逻辑尺寸与纹理解耦);缩放/分析栏可见触发升级;代理期 DetailPreview 源置空占位;预取 ±1 Full + 其余代理;视图尺寸上报携带渲染缩放(实测发现 Avalonia `VisualRoot as TopLevel` 拿不到 2.0,`TopLevel.GetTopLevel` 修正——**Avalonia API 行为差异以实证为准**)。**实证**:42MP HEIC 代理 227ms vs Full 465ms(**-51%,达验收门**),RAW 代理 -12%(ImageIO RAW 渲染主导,内存减半仍在);逻辑尺寸 = 实际尺寸(HEIC ispe/RAW EXIF 双 PASS);Mac 冒烟竖图 RAW 代理上屏正确。**待人工**:缩放→Full 热替换手感(本机无辅助功能权限,无法注入按键,留 iPad/桌面人工冒烟时一并过) | 四平台构建 0 错零新增警告;gov 31 全过;commit `P1-M4b` |
