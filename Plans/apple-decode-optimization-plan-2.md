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

## 执行状态

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
