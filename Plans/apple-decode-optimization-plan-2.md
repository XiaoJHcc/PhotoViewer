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
- 独立 Swift ImageIO 小图对照：IMG_8923.HEIC 有内嵌 HEVC 缩略项，120 档同尺寸 90×120，Always 93.4ms / IfAbsent 3.1ms，sRGB8 mean 差 1.93/255；400 档 IfAbsent 仅 240×320，不满足目标，必须回落原图。IMG_8918.HEIC 无缩略项，两者像素完全相同、约 96–100ms。Sony 已有 JPEG 内嵌快路径不改变。报告 `artifacts/acceptance/apple-decode-optimization/baseline/thumbnail-probe.txt`。
