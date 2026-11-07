# Apple 解码链路优化方案 2

## 目标与边界

在不改变代理精度（视口物理长边 × 2）、Full、F16/Display P3、ICC、方向和 RAW 开发质量的前提下，减少 Mac/iOS 的转换、重复开发与纹理上传。新分支 `perf/apple-decode-pipeline`；每个里程碑验证后独立提交，不上传远端、不发布。任务开始时工作树干净。

## 里程碑

1. **可重复对照探针**：增加独立 Apple 优化探针，分开记录新源解码+转换、已解码表面转换、RAW 新实例与代理→Full 实例复用；保留首次和多次中位数，并核对像素/几何与 footprint。Mac 和 iOS 共用，输入照片只读、输出为隔离验收产物。先提交可用探针与基线。
2. **有证据的实现**：优先最近纹理缓存（当前 + 最近两份，弱引用身份、不持 CPU 租约、压力修剪、总字节记账），验证 A/B 不重传、清空、淘汰、失败保留旧图、GPU 像素与方向。Core Image 转换与 RAW 实例复用只有在实测收益、像素质量和内存均可接受时进入产品；失败候选不留隐形环境开关或替代实现。
3. **双端回归与收尾**：Mac 全套原生验收、RAW 对拍，iOS 模拟器 photo/layout/input/analysis/similarity/tools/gesture/faults，以及 Release 真机可用时的性能探针/连翻。记录每项可验证结果及未完成边界，再提交回归收尾。

## 验证约束

- 构建命令遵循 `.vscode/tasks.json`：Mac Debug `dotnet build PhotoViewer.Mac/PhotoViewer.Mac.csproj -c Debug -r osx-arm64`；iOS Debug `dotnet build PhotoViewer.iOS/PhotoViewer.iOS.csproj -c Debug -r iossimulator-arm64`；真机 Release `dotnet build PhotoViewer.iOS/PhotoViewer.iOS.csproj -c Release -r ios-arm64`。
- 不把同一 CGImage 重绘的热中位数当新图解码；不把不同机器/不同视口或并发构建期间的数字当性能提升。
- 纹理缓存需要按内存压力及时收缩，Trim 不阻塞探测线程，绘制/交换/修剪的生命周期须一致。
- 不自行延后可见胶片栏加载或降低图像质量；优先评估实际内嵌来源利用。不在本轮重写 HEIF/VideoToolbox 后端。

## 首轮执行记录

- 已建分支，任务开始时无未提交文件。
- iPad mini 7 已配对可见，但设备未近期解锁，devicectl 返回 RemotePairingError 1016；先推进 Mac 与模拟器，真机在可访问后复测。
- 历史基线仅作方向依据，见 `apple-performance-memory-plan-1.md`，本轮数据在各里程碑完成后补入。
- Mac 原始 Debug 构建 0 错误 / 8 警告（含命令行 RID 既有提示），base 35 项及 layout/filmstrip/toolbar/input/analysis/similarity/tools/faults/settings 全通过。input 必须空窗口；混合大色卡目录不能用于 base（套件仅登记小色卡名称）。首次素材参数误配已修正后重跑，不修改套件语义。
- 已安装的 iPad mini 模拟器原始应用八套 photo/layout/input/analysis/similarity/tools/gesture/faults 全通过，仅作回归基线，不作真机性能数据。
- 第一个里程碑提交 `29dfc7c`：双端对照探针构建通过，Mac 与 iPad 模拟器均 `SUMMARY PASS`；原图输入含 Sony HIF、P3 大图、无标签 Adobe JPEG、ARW 方向 1/6。
- 最近纹理里程碑：Mac 十套与 iPad 模拟器八套完整回归全通过（总包装超时后，模拟器剩余四套独立补跑通过）。专项在 Mac 与 iPad 模拟器均 15/15 通过，含 F16 扩展色域/8 位 P3/八方向/行距/字节上限/失败保留旧图/LRU/空图/并发修剪/弱引用租约。1600×1200 A/B 的 SetTexture 编码耗时（不含 GPU 完成/呈现）：Mac 强制修剪 1.57ms → 缓存 <0.01ms，模拟器 6.40ms → 0.01ms，不将其表述为完整切图或真机时延。
- HEIF 列表取源专项：真实平台解码器在 Mac 与 iPad 模拟器均 6 文件 × 120/400/560 三档全通过。IMG_8923 的 120 档最终代码三样本中位数 Mac 128.3→11.1ms、模拟器 186.7→9.2ms；早期轮次为 60.3→2.9ms / 113.2→4.3ms，绝对值随机器负载波动，不跨轮相减；400 内嵌不足时回落后逐字节相同，560 恒与旧路径逐字节相同；无缩略项文件保持原像素。实验发现 Sony 120 档无稳定收益且有像素差，最终 `.hif` 保持旧路径（已有 JPEG 内嵌取源也不变），不把无收益候选强行启用。验收工具对 ±1 像素尺寸差也先统一几何再核对像素，不空过内容断言。未修改主图目标或取源。报告 `heif-thumbnail-checks.txt` 与 `ios-thumbnail/heif-thumbnail-checks.txt`。
- Mac 大图 F16 转换：最终产品默认路径与内部关闭加速的位图基线交替顺序，各三次。Release、T=4532：Sony HIF 149.5→82.0ms（约45%），P3 大 HEIC 119.3→82.0ms（约31%）；几何/方向一致，最大线性差 ≤0.000977，P3 超范围分量保留。另两张真实 iPhone HEIC 在 Debug 中为120.3→97.6ms、128.4→121.6ms。只对 Mac、HEIC/HEIF/HIF、F16 且产物≥4MP启用；小图/JPEG/8位没有稳定收益，维持旧路径。报告 `accelerated-mac-release.txt` 与 `accelerated-iphone-mac.txt`。末轮色彩空间空值守卫复验（`final-heif-guard.txt`）为 Sony 153.2→110.0ms（约28%）、大 P3 HEIC 148.7→100.1ms（约33%），同样像素/方向通过；保留各轮数据，不跨轮相减或只取最好一轮。
- RAW 跨档复用：已把“独立开发 proxy/full 各新实例”与“每轮新实例复用 proxy→full”分开测，不再用同实例重复渲染冒充跨档收益。两张 ARW 的收益不稳定，存在更慢样本，原生驻留与自动释放波动显著，因此没有加入产品滤镜常驻缓存，也没有提高 RAW 并发。
- iOS 的 CI 候选只完成模拟器正确性对照（该环境反而更慢），没有用模拟器断言真机 GPU 收益；默认关闭新转换，保留共享纹理缓存与合格内嵌缩略来源优化。真机性能/触摸/连翻仍因设备未解锁待复测。
- 探针口径收尾：旧 `ApplePerfProbe.metalSetTexture` 每轮在计时外清空纹理缓存，单列 `metalCachedSetTexture`，不把缓存命中当作上传提速；隔离入口 `--pv-decode-optimize <目录> --pv-perf-components-only` 已在 Release 实际跑通。小 HEIF（1200×800）产品默认与强制位图基线 F16/P3 逐像素完全相同，证明加速闸门未扩大到小图。
- 最终验收：Mac Debug 十套（base 35 项 + layout/filmstrip/toolbar/input/analysis/similarity/tools/faults/settings）、iPad 模拟器八套全部通过；Mac RAW 对拍 22 项 `SUMMARY PASS`；Mac Release 真实 Sony/iPhone HEIF 原生主图流程 14 项通过。Mac/iOS 的 Debug 与 Release 四类交付构建全部成功；Release 仍有项目既有裁剪/反射告警，没有把构建成功当作真机验收。
- 卫生边界：原始 `Fixtures/jpeg`、`jpeg-large`、`heif`、`raw` 内容 SHA256 前后相同；仅写自有工作副本。保留本轮性能/检查报告，清理本轮临时脚本与仓库内大素材副本；不清理此前已有验收目录/模拟器工作数据。
- 独立 Swift ImageIO 小图对照：IMG_8923.HEIC 有内嵌 HEVC 缩略项，120 档同尺寸 90×120，Always 93.4ms / IfAbsent 3.1ms，sRGB8 mean 差 1.93/255；400 档 IfAbsent 仅 240×320，不满足目标，必须回落原图。IMG_8918.HEIC 无缩略项，两者像素完全相同、约 96–100ms。Sony 已有 JPEG 内嵌快路径不改变。报告 `artifacts/acceptance/apple-decode-optimization/baseline/thumbnail-probe.txt`。

## iPad 真机复验与追加优化（2026-11-07）

设备为 iPad mini 7 / A17 Pro，Release `ios-arm64`，实测应用预算约 6144 MiB；全部输入为自有夹具/沙盒副本。证据集中在 `artifacts/acceptance/ipad-performance-current/`。

### 实现与测量口径

- 复现了旧记录“500 张目录没有图片”：目录实际有 500 项且格式启用，但 `iOSStorageAccessSessionFactory` 把沙盒路径误判为外部路径，拒绝缺少系统 URL 的本地引用。沙盒根现由 Documents 父目录推导，词法归一 `/var` 与 `/private/var`，根本身/子项按目录边界比较；未知根仍拒绝，外部原 URL 的 Start/Stop 契约不变。真机 input 覆盖 Documents/Library/tmp/bundle、别名、相似前缀与 `..` 逃逸。
- 首次有效视口前不向展示会话发文件；压测也移到首轮有效布局后启动，避免零视口下直接解 Full 或重复解码。
- iOS 启用 HEIF/HIF 的 ≥4MP F16 Core Image 转换，JPEG/小图/8 位出口不变。仅打开 CI 的第一候选虽降低中位数，但混合 RAW 的整体 p95 升到 915.5ms，不作为最终实现；最终 iOS 在 RAW 开发在途时回到位图转换，慢图 P0 超过预热门槛时先等它结束，再重新给 600ms 稳定停留窗口，且无代理产物不做 Full 预热。
- 压测新增进程 footprint、缓存、池空闲、纹理的逐翻采样及 `SampledPeak`，不以 GC 堆冒充完整进程占用；取样在切图计时外。汇总附视口/倍率/代理目标。延迟为展示会话通知完成（iOS 同步上传并提交绘制）的口径，不是屏幕实际 present 时间。
- 对比采用列布局：主图 895×744 点、倍率 2、代理目标 3580；分析栏开启后主图 782×744、目标 3128，100 张均直接 Full。`warmguard-sequential` 在设置验收改变布局后出现 116 张源尺寸产物，不与基线比较；最终按 `--pv-layout-mode col` 固定视口重测。

### 同条件结果

500 张 = 334 HIF + 116 HEIC + 50 ARW；前后均零超时、500/500 F16 代理。

| 指标 | 优化前（修复存储后） | 最终固定列布局 |
|---|---:|---:|
| 全部 p50 / p95 | 182.3 / 714.5 ms | 114.0 / 730.2 ms |
| HIF p50 / p95 | 213.8 / 381.2 ms | 97.0 / 364.2 ms |
| HEIC p50 / p95 | 131.8 / 281.2 ms | 79.8 / 297.8 ms |
| ARW p50 / p95 | 714.5 / 848.2 ms | 730.2 / 950.6 ms |
| 总耗时 | 111.4 s | 97.8 s |
| 进程 footprint 逐翻采样最高值 | 3975 MiB | 3650 MiB |
| 压力样本 | Normal 488 / Caution 12 | Normal 500 |

普通浏览中位数约下降 37%，总耗时约下降 12%，但 RAW 和部分尾延迟没有改善，不能表述成全场景更快。

- 最终解码探针（`final-decode/decode-optimization.txt`，T=4532，每臂 3 次交替）：Sony HIF 位图基线 239.1ms → 实际默认路径 119.7ms；大 P3 HEIC 107.9ms → 82.0ms。几何/方向一致，最大线性差分别 0.000488 / 0.000977，广色域超范围分量保留，`SUMMARY PASS`。不跨轮相减，也不把纯转换时间当整图时延。
- 最近两张 100 次回切：最终 p50 / p95 = 48.1 / 51.1ms，去掉前两次冷帧后 p95 = 49.0ms，零超时，全部 Normal。
- 分析栏开启 100 张 Full：基线 p50 / p95 = 631.5 / 1231.4ms，最终 764.6 / 1214.1ms；最终零超时、全部 Normal、footprint 采样最高 3680 MiB。本轮没有给 8 位 Full 开新转换路径，不能声称分析栏速度已优化收口。
- 告警注入 500 连翻：零超时，Warning 时出现 8 位 P3 代理；Warning→Caution→Normal，进程占用从告警快照 3035 MiB 降到 1147 MiB。第一段恢复观察期被后续 Caution 中断，回 Normal 后重新计观察期；第二段采样覆盖约 29s，随后恢复正常预取，符合 30s 门槛与采样间隔。最终回到 Normal。

### 回归与未关闭边界

- 交付构建：Mac Debug、iOS Debug 模拟器与 iOS Release 真机均 0 错误；告警分别 8 / 27 / 182，Release 与本轮基线的 182 项一致。CoreContractCheck 为 1050 PASS / 1 SKIP / 0 FAIL。
- Mac 的 base/layout/filmstrip/toolbar/input/analysis/similarity/tools/faults/settings/texture-cache 十一套均通过。首轮隔离工作目录漏拷大图/toolbar 夹具，补齐完整 Fixtures 副本后重跑，不修改断言。
- iPad 真机 photo/input/analysis/similarity/tools/gesture/faults/texture/thumbnail 九套通过，含 GPU 颜色/方向/增强读回、真实 RAW 7008×4672 与内存告警关闭增强。分析检查首轮在低对比度 Full 等待超时，增加失败状态取证并禁止验收会话自动锁屏后，最终原断言通过；未放松编码/像素/方向断言。
- **布局套件未通过，保留阻塞**：12 张在大窗口下滚动位移不足，改用 24 张独立文件名副本后，布局/滚动/评分浮窗部分通过；最后 `VerifyDisplaySnapshotAsync` 在 `UICollectionView.CellForItem(oldPath)` 遇到 `ObjectDisposedException: Foundation.NSIndexPath`。本轮未修改该检查或胶片栏的索引路径生命周期代码，也未将整套记为通过；需独立排查后复验。
- 巡检未新增 PhotoViewer 或 Jetsam 报告；取文件服务新增 5 份 `dtfileserviced` 系统报告，已记录文件名，不把它们混作 App 崩溃，也未排查该系统服务问题。
- 原始 JPEG/大色卡/HEIF/RAW 的 17 个夹具 SHA256 保持不变；只清理本轮创建的临时脚本、Mac 工作副本与重复构建日志，保留报告。没有提交、上传或发布。

## iPad 收尾修复与性能平衡（2026-11-07）

范围为上述剩余三项：索引路径生命周期、RAW 长尾、Full 分析链路。证据在 `artifacts/acceptance/ipad-followup-20261107-031512/`；保留上一轮未提交改动，没有提交或发布。

### 修复与取舍

- **索引路径生命周期已关闭**：真机证实同一 item/section 的 `NSIndexPath.FromItemSection` 返回同一个托管包装；布局里的 `using` 会清掉调用方仍持有的句柄。iOS 与 Mac 生产/验收调用点均移除这类显式释放，不加 retain 或全局缓存。验收新增重入布局前后句柄不变检查，iPad layout 首次和最终重复运行均通过，Mac filmstrip/layout 等全套通过；原有显示快照、滚动、评分断言未放松。
- **RAW 以提前量而非更多并发解决**：加入 iOS 原生创建/渲染单路准入，P0 优先于旁路与普通后台，等待票面可提升、取消不泄漏名额，自动释放池不跨 await。窗口 6 能把 RAW p95 降到 448ms，但混合 HEIF 中位回退较大；最终取窗口 4，在用户配置的方向预载范围内只选最近一张 RAW，常规 +1/+2 非 RAW 仍可并行，其他平台默认邻居规则保持。
- **Full 需求不预取无用代理**：方向 +2 与提前 RAW 原来仍按代理解，翻到后又要求 Full；`ShouldPrefetchFull` 统一让 ±1 和全部 alongside 候选按当前 Full 需求取 Full，普通远邻仍为代理。新增 31 条 Core 纯规划契约与原生尺寸档检查。
- **Full P3 采用通过逐像素校验的 CPU 行带绘制**：真实原尺寸 HIF 是 10bpc/32bpp/sRGB。CGImage 桥接 CI 和 CIImage 直读均约 1s，比串行约 460ms 更慢，未入产品；原尺寸 Thumbnail 没有收益，也已撤去生产候选。4 带初版虽快，但裁切边缘最大差 6/255；补上下各 8 行邻域、按原尺寸裁回目标后，98224128 个 RGB 通道与串行逐值一致。生产仅为 iOS ≥4MP、10bpc/32bpp/sRGB HEIF/HIF Full 8 位出口启用；前台/直接调用最多 4 带，预取最多 2 带，其余源格式和 Mac 不变。异常回退串行，取消不回退；全部 worker 完成后才归还 pin/缓冲。

### 同条件实测

本轮基线使用上一轮已测试的 Release 二进制；主图固定列布局 895×744 点、倍率 2、代理目标 3580，分析栏开启后为 782×744 / 3128。500 张仍是 334 HIF + 116 HEIC + 50 ARW。以下保留多轮范围，不只取最好值：

| 指标 | 本轮修复前基线 | 最终候选与复测 |
|---|---:|---:|
| 混合 500 张整体 p50 | 114.7 ms | 130.8–147.0 ms |
| 混合 500 张整体 p95 | 715.6 ms | 464.7–517.6 ms |
| RAW p50 | 715.6 ms | 448.1–482.1 ms |
| RAW p95 | 1047.6 ms | 747.8–832.2 ms |
| 混合总耗时 | 96.7 s | 89.1–96.4 s |
| 混合 footprint 采样最高 | 3698 MiB | 3585–3782 MiB |
| 分析栏 100 张 Full p50 | 781.3 ms | 281.4–282.3 ms |
| 分析栏 100 张 Full p95 | 1339.8 ms | 798.0–1164.8 ms |
| 分析栏总耗时 | 58.5 s | 35.3–48.4 s |
| 分析栏 footprint 采样最高 | 3607 MiB | 3727–3780 MiB |

- RAW 提前预取使部分 HEIF 回到 CPU，混合素材的整体中位数增加约 16–32ms；换来 RAW 与整体长尾下降。不能表述成普通浏览所有指标都更快。固定列布局的全部混合复测均 500/500 F16、零超时、全 Normal；本轮不以 GC 堆替代 footprint，也不将采样最高值声称为瞬时峰值。
- 真实 Full 同进程三臂各 3 次：最终默认路径 HIF 468.6→285.4ms，几何、方向、色彩标签一致，98224128 个 RGB 通道 mean/max 均为 0；独立 P2 实际默认两带调用同样 mean/max=0，明确标 quality-only、不计入测速。JPEG、8 位 P3 HEIC、小图仍走旧路径。报告 `full-product-probe/decode-optimization.txt`。
- Full 端到端两轮均零超时、100/100 8 位 Full；一轮 Normal 100，另一轮 Normal 96 / Caution 4，未进入 Warning。约增加 120–173 MiB 的 Full 采样最高占用，不能宣称所有场景都更省内存。
- 最终 A/B 100 翻 p50/p95=47.8/50.9ms，去掉两次冷帧后 p95=49.2ms，全 Normal。
- 最终告警注入 500 翻零超时，出现 8 位 P3 降级，Warning→Caution→Normal；两段完整恢复观察期采样覆盖约 29.7/29.4s，后续 Caution 会重新计观察期，结束为 Normal。未将注入的 Warning 冒充真实 OS 内存不足。

### 最终验收

- iPad 真机 photo/layout/input/analysis/similarity/tools/gesture/faults/texture/thumbnail/raw-scheduling **11 套全通过**；布局显示快照重复检查同样通过。此前 layout 的 `NSIndexPath` 阻塞已关闭。
- Mac base/layout/filmstrip/toolbar/input/analysis/similarity/tools/faults/settings/texture-cache **11 套全通过**，覆盖相同共享包装的句柄回归。
- CoreContractCheck **1081 PASS / 1 SKIP / 0 FAIL**（新增 31 条预取规划断言），原生 RAW 调度 **8 组通过**。
- iOS Release、iOS Debug 模拟器、Mac Debug 均 0 错误，既有告警数为 182 / 27 / 8，未新增告警。没有把构建通过替代真实图像和 GPU 验收。
- 原始 JPEG/大色卡/HEIF/RAW/toolbar 共 20 个夹具 SHA256 不变；巡检无新增 PhotoViewer/Jetsam 报告。新增系统 SFA/取文件服务诊断只记录元数据，不混作 App 崩溃，也未扩大范围排查系统服务。
- 仅清理本轮临时脚本、基线 App 备份与 Mac 工作副本，保留本轮比较报告；已更新长期模块约束与 FILM-06 程序化验收边界，未声明作者手验或跨机型验收。

## 远端更新整合

已在提交优化前将 main 从 `fd5ae65` 快进到远端 `3afa455`（Windows 性能与内存专项方案 2）。先保存完整补丁、未跟踪源码副本与临时 stash，再恢复本地改动；没有文本冲突，重叠路径为 `Design/status.md`、`PhotoViewer/Core/Image/AGENTS.md`、`PhotoViewer/StressFlipDriver.cs`、`Tools/CoreContractCheck/Program.cs`。非重叠本地文件与新增源码均逐字节核对保留。

行为整合保留两侧职责：远端的准入 CAS、主分区预算扣除缩略图/空闲池、持续压力下缓冲不回填，以及本地 RAW 单路/提前窗口、Full 需求预取、并行 P3 转换；压测同时保留远端 completion/pixelsReady/submitAfterReady/循环次数语义与本地 footprint/cache/pool/texture 采样，Core 自检同时注册内存准入与预取规划检查。模块文档没有用单侧版本覆盖；仅移除了一条仍写旧试验窗口 6 的过期注释，产品值保持 4。

整合后的交付物验证：CoreContractCheck 为 **1131 PASS / 1 SKIP / 0 FAIL**，远端内存准入与本地预取规划均执行；Mac 与 iPad 真机各 **11 套全通过**，三类 Apple 构建均 0 错误，既有告警仍为 8 / 27 / 182。Windows C# 类型检查与 win-x64 HEIF 工具交叉编译通过；该工具的 smoke/e2e 依赖 Windows 原生库，macOS 上标准运行被平台目标拒绝，未冒充 Windows 运行验收，也未修改目标框架绕过限制。

合并后同视口实测：500 连翻 p50/p95=114.9/597.5ms，零超时、500/500 F16、全 Normal，footprint 采样最高 3550MiB；100 张分析栏 Full 为 341.3/999.5ms，零超时、全 Normal；100 次 A/B p95=48.4ms。告警注入 500 翻零超时，8 位 P3 降级、Warning→Caution→Normal 与约 29.4s 采样跨度的恢复观察期通过。日志实际包含远端 `completion=pixels-ready` 和像素就绪计时，同时保留本地内存采样；24 张自有夹具按 count=30 循环测试也通过，确认超过列表长度时回到首张。Full 产品路径 4 带与 P2 两带仍与串行基线逐像素一致。报告位于 `artifacts/acceptance/apple-remote-integration/`；此前性能数字保留为各自构建的历史结果，不与远端新计时口径跨轮相减。
