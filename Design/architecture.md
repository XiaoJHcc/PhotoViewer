# PhotoViewer 架构与跨模块流程

> 从根 `AGENTS.md` 移出的长期说明，按需阅读。入口与文档索引见 [AGENTS.md](../AGENTS.md)。
> 短期重构进度、分支与下一步见 [Plans/native-rebuild-handoff.md](../Plans/native-rebuild-handoff.md)。
> 章节编号沿用原根文档（4–6），以便模块手册中的「架构文档 §4/§5/§6」引用继续有效。

## 4. 平台差异化实现

各头工程的 `Core/` 目录放置平台相关实现，启动时通过 `Program.cs` / `MainActivity` / `AppDelegate` 注入。

| 能力 | Windows | macOS | Android | iOS |
|---|---|---|---|---|
| HEIF 解码器 | `FastHeifDecoder`（自解析容器 + `pvhevc.dll` 并行解 HEVC 瓦片；超出支持面回落 `LibHeifDecoder`） | `MacHeifDecoder` | `AndroidHeifDecoder` | `iOSHeifDecoder`（缩略图与平台 8 位 HEIF 路径；主图走展示解码器） |
| RAW 解码器 | `WindowsRawDecoder`（系统 WIC RAW 引擎，按相机 `DefaultCrop` 裁切；解码器缺失或失败时只接受与有效成像区一致的整幅内嵌 JPEG） | `MacRawDecoder`（复用 ImageIO，供缩略图与旧路径；主图走展示解码器） | — | `iOSRawDecoder`（复用 ImageIO，供缩略图兜底；主图走展示解码器：RAW 由 `ApplePresentationDecoder` 的 `CIRawFilter` 路径全分辨率解码，仅回退 ImageIO 时才是内嵌预览档） |
| 展示解码 / 编码 | `WindowsPresentationDecoder` + `WindowsPresentationEncodingPolicy`（恒 RGBA16F 线性 scRGB；W-M7 前不做 8 位/广色域） | `ApplePresentationDecoder` + `MacPresentationEncodingPolicy`（恒 RGBA16F 扩展线性 sRGB） | `AndroidPresentationDecoder` + `AndroidPresentationEncodingPolicy`（恒 8 位 Rgba8；Display P3 / sRGB 由解码器按表面能力选择） | `ApplePresentationDecoder` + `iOSPresentationEncodingPolicy`（代理 F16 / Full 8 位 Display P3 / 压力 ≥ Warning 代理也降 8 位） |
| 性能预算 | `DefaultPerformanceBudget` | `DefaultPerformanceBudget` | `AndroidPerformanceBudget` | `iOSPerformanceBudget` |
| ONNX 执行提供器 | DirectML EP | CoreML EP | NNAPI EP | **CPU**（CoreML EP 在 iOS 原生库中不受支持，注册失败记日志后回退，不静默） |
| 设置存储 | `FileStorage`（默认） | `MacSettingsStorage` | `AndroidSettingsStorage` | `iOSSettingsStorage` |
| 外部打开桥接 | `Program.Main` 参数解析 + 主窗口拖放 | `Program.Main` + `MacNativeApplication`（Finder / Dock / «打开方式»）+ 根视图拖入 | `AndroidBootstrapActivity` 转发 Intent（`ACTION_VIEW` / `ACTION_SEND` / `ACTION_SEND_MULTIPLE`）+ `AndroidNativeShell.RegisterExternalOpen` | `iOSNativeAppDelegate` / scene + `iOSExternalOpenBridge.PublishNativeFromUrl`（直发原生存储引用），主屏另接 `UIDropInteraction` 拖入 |
| 存储访问 | 无 | macOS 安全作用域书签 | SAF 持久化权限 | iOS 安全作用域书签 |

---

## 5. 跨模块关键流程

> 每个流程跨越多个模块,所以集中在此处而非任一子文档。子文档只描述模块内部行为,跨模块联动以**链接**指回这里。

### 5.1 图片加载流水线

1. **触发**：`MainViewModel.CurrentFile` 变化（用户切换图片）。
2. **分发**：各原生壳在 `CurrentFile` 变化时调用 `PhotoLoadSession.SetFile`，会话递增单调的**代际计数器**并调用 `BitmapLoader.GetBitmapAsync`；若新图片不在缓存中，立即把屏上产物置空（旧图不会停留上屏）。
3. **分级代理解码**(P1-M4b 起):适应态/浏览先解码**代理图**(长边 = 渲染表面物理像素 × overscan 2.0——2× 超采样抗"解码降采样+上屏降采样"双重软化,守画质红线;Apple 走 `CGImageSourceCreateThumbnailAtIndex` 编解码器级子采样,JPEG 走 Skia codec 子采样,Windows HEIF 走 HEVC 瓦片并行解码 + YUV 域按比例展宽的 Mitchell 重采样、RAW 走 WIC 全解码 + Mitchell 精缩)数十~数百 ms 上屏;**缩放超出代理能力 / 分析栏可见(细节预览要 100% 源)** 时后台解码 Full,就绪后**同变换热替换**(几何基准 = 全分辨率逻辑尺寸,EXIF/`ImageOrientationInfo` 推导,`ImageSize` 恒定故无跳变);快速翻页代际闸作废在途 Full。100% 及以上放大永远以 Full 呈现(硬约束)。缓存层两级条目同驻统一 LRU(`(路径, 尺寸档)` 键,Proxy 档命中规则:已有代理长边 ≥ 需求即复用)。**跨档复用**:请求代理时若同路径 Full 已驻留、长边足够且保真度不低于本次需求,直接返回 Full 而不解代理(F16 覆盖 8 位请求;iPad 8 位 Full 不满足 F16 代理请求,不得降保真度)。当前图（P0）代理解码失败时解码器自动回退解码 Full；两档都失败才由加载会话报「无法打开该图片」，屏上已有代理而 Full 失败只给非阻塞提示（IMAGE-03、STATE-02）。
4. **缓存 / 解码**（`BitmapLoader.GetBitmapAsync` 以 **P0** 运行，并由 `DecodeScheduler.TrackP0` 包裹，使后台工作线程在期间暂停）：
   - 先查 **LRU 内存缓存**，命中立即返回。
   - 未命中：进行**在途去重**（同路径请求共享一个解码任务）→ **解码准入 `MemoryGovernor.TryAcquire`**（P1-M3 起；P0 豁免，非 P0 预算不足时直接拒绝）→ **容量保障**（有效上限见 §5.5；先释放缓冲池空闲大缓冲，再按保留排名淘汰）→ 在线程池执行解码本体（`Task.Run`，续体保持在 UI 线程之外）：读取流 → 识别格式（JPG / HEIF）→ 解码 → **应用 EXIF 旋转** → 写入缓存 → 返回。P0-M4 起 Core 全程使用平台中立 `DecodedImage`（SkiaSharp 解码/旋转），由 `IFileRef` 提供流；原生壳直接把 `DecodedImage` 交 GPU 纹理上传，不再有 UI 位图适配层。P1-M3 起返回值携带**调用方租约**（用完调用 `Release`；淘汰只归还缓存自己持有的那份，租约归零后缓冲才归还 `ImageBufferPool`）。
   - **代际闸门**：仅当 `generation == _loadGeneration && file is still CurrentFile` 时才交付上屏；过期结果直接丢弃（图片仍留在缓存中，无副作用）。
5. **显示**：原生主图宿主（`MetalPhotoView` / `iOSPhotoView` / `WindowsPhotoView` / `AndroidPhotoView`）上传纹理并按共享 `PhotoViewport` 的几何呈现；逻辑坐标系恒取全分辨率 `ImageSize`，与纹理实际像素解耦（P1-M4b 起）。
6. **统一调度**（静态类 `DecodeScheduler`）：P0 当前图 / P1 可见缩略图与当前缩略图 / P2 邻居位图与排队缩略图 / P3 分析预热；同键去重并支持优先级提升；后台工作线程（并发度 = `Settings.NativePreloadParallelism`，Warning 压力下降为 1）在 P0 在途时暂停取新任务。标记为**可沿 P0 并行**的项（翻页方向上的前两张预取）不进这条队列，而走独立通道立即开跑（并发上限 `MaxAlongsideParallelism`）：留在后台池里会被同一池中的缩略图解码挡在队尾（实测晚 160 ms 以上），连翻时永远赶不上下一张。
   - **预取**：`ThumbnailListViewModel` 通知 `BitmapPrefetcher`（最新请求优先——新请求会取消上一轮的 CTS），后者按保留排名顺序（翻页方向优先：方向距离 d 计 d、反方向 d 计 2d）把邻居作为 P2 工作项提交；**预取档位跟随实际需求**——适应态只取代理，放大/分析栏可见时（`BitmapLoader.PrefetchFullWanted`）±1 邻居取 Full；方向上的 +1/+2 邻居以"可沿 P0 并行"提交且不让位高优先级（RAW 只提交 +1：Core Image 的 RAW 渲染是 GPU 串行瓶颈，多路并行不涨吞吐）；**已开跑的沿 P0 并行项不随轮次切换取消**——它正是即将上屏的那一张，取消会把"快好了"的解码丢掉、让主图从零解第二遍；**分析栏预热**随同项顺带执行；Caution 压力或恢复观察期内投递即空跑。当前图停留 ≥600ms、适应态且分析栏不可见、压力 Normal 时另以 P2 预热当前图 Full（不上屏），放大/开分析栏时直接命中。
   - **淘汰排名**：`ThumbnailListViewModel` 通过 `BitmapLoader.SetRetentionRanking` 上报**有序排名**（① 当前图 ② 上一张浏览过的图 ③ 翻页方向 +1 ④ 反方向 −1 ⑤ 第二张最近浏览 ⑥ 其余邻居按方向距离 ⑦ 其余最近浏览）；所有淘汰路径（容量保障 / Cleanup / 压力 Trim）统一先淘汰未入排名的 LRU，再按排名从最不重要到最重要，同排名内先 Full 再 Proxy（当前图与 ② 同排名内保持 LRU），当前图不因容量被淘汰。**预取不得挤掉比自己更重要的条目**：P2/P3 只允许淘汰排名比目标差或未入排名的条目，腾不出就跳过（不解码、不刷错误日志）。淘汰只归还缓存持有的租约，仍被借用的缓冲继续存活（P1-M3 租约模型）。

### 5.2 外部文件打开

1. **入口**：
   - **Windows**：`Program.Main(args)` 解析命令行参数 → `PublishExternalOpenArgs`；主窗口另接文件/文件夹拖放。
   - **macOS**：`Program.Main(args)` + `MacNativeApplication` 的 `OpenFile / OpenFiles / OpenUrls` 捕获 Finder / Dock /「打开方式」事件；根视图另接拖入。
   - **Android**：`AndroidBootstrapActivity` 捕获 `ACTION_VIEW` / `ACTION_SEND` / `ACTION_SEND_MULTIPLE` 并原样转发 Intent，`AndroidNativeShell.RegisterExternalOpen` 把 URI 转为原生存储引用。
   - **iOS**：`iOSNativeAppDelegate` / scene → `iOSExternalOpenBridge.PublishNativeFromUrl`，主屏 `UIDropInteraction` 把"文件"里的文件/文件夹 URL 也交同一条路径。
2. **桥接**：所有平台统一进入 `ExternalOpenService.PublishFile / PublishFiles`。若 UI 尚未就绪，请求先进入待处理队列。
3. **处理**：各原生壳的启动流程注册同一 `ExternalOpenService` 处理器并直接使用原生存储引用（macOS `MacStorageRefs`、iOS `iOSStorageRefs`、Android `AndroidStorageRefs`、Windows 本地路径）。
   - 文件夹 → `FolderViewModel.OpenFolderAsync`。
   - 文件 → `FolderViewModel.OpenImageAsync`——先尝试进入父文件夹；在 Apple/Android 上父目录访问被拒时，回退到**单图模式**，至少让用户看到该文件。

### 5.3 评分与元数据同步

1. **动作**：键盘 `1`–`5` 或点击星级。
2. **逻辑**：`MainViewModel.SetRatingAsync`。
3. **写入**：先更新内存中的 `ImageFile`，再调用 `XmpWriter.WriteRatingAsync`：
   - **原位更新**：逐字节修改 JPG/HEIF 的 XMP 块。
   - **RAW**:本仓实际仍对已有 XMP Rating 做原位更新；独立 `.xmp` 文件的字节写服务可用，但未实现自动创建/关联 RAW 附属文件（P2 核验纠正此前文档误写，扩展能力须单独立项）。
4. **刷新**：调用 `FolderViewModel.RefreshFilters`，让基于评分的筛选即时更新。
5. **数据库副本**(Plan-2-4):成功写文件后顺手 `PhotoDatabase.UpdateRatingAsync(fp, rating)`。产品行为仍以文件为准，数据库评分只是给 Plan-3 训练流水线取快照用;指纹未入库时静默跳过,不主动占位。

### 5.4 AI 特征提取与持久化（DINO/CV 索引）

> 三类原始数据(DINO CLS / DINO patch token / CV grid 7 标量)同源同时入库。派生层(锐度热力图 / 抖动矢量场 / 刚体拟合 / PCA-RGB / 参考点 cosine)全部现算,阈值常量从不入库。详见 [Plans/dinov3-photo-ranking-plan-2-3-persistence.md](../Plans/dinov3-photo-ranking-plan-2-3-persistence.md)。

**指纹与数据库结构**：
- 指纹 = SHA1(`filename_noext` + `DateTimeOriginal` + `SubSecTimeOriginal`),同次曝光的 RAW/HIF/JPG 共享同一指纹(见 [PhotoFingerprint](../PhotoViewer/Core/Database/PhotoFingerprint.cs))。
- 三表:`photos`(身份 + `rating` + EXIF 拍摄参数 4 列 + `cv_grid` BLOB + `cv_grid_spec` 覆盖式)、`photo_features`(CLS 纵表 `(fingerprint, model_id)`)、`photo_patches`(patch token 纵表同主键)。`model_id` 与 `cv_grid_spec` 不匹配视为缓存未命中，改算法 = 提升版本字符串即整库失效。
- EXIF 列(Plan-2-4):`focal_length` / `aperture` / `shutter_speed` 存原始值;`crop_factor = EquivFocalLength / FocalLength`,缺失为 NULL。等效焦距/光圈不入库,训练脚本自行换算。

**批量提取**(用户点"提取全部 / 补齐全部"按钮):
1. **入口**:[SimilarityPanelViewModel.StartIndexingCommand](../PhotoViewer/ViewModels/Main/File/SimilarityPanelViewModel.cs) → `FolderFeatureIndexer.RunAsync`。
2. **聚合**:`GroupByFingerprintAsync` 把 RAW+HIF/JPG 合并为指纹组,代表文件按解码代价升序(HEIF→JPG→其他→RAW)。
3. **缺失评估**:每组先 `EvaluateMissingPartsAsync(fingerprint, modelId, cvSpec)` 拿 `(needCls, needPatch, needCv)` 三元组,全齐备即跳过。
4. **解码两路**:DINO 走 `ThumbnailService` 560 短边、CV 走 `BitmapLoader` 原始分辨率(共享 LRU);桌面端 `ProcessorCount/2` 并发,移动端单线程。
5. **推理与提取**:`_inferSemaphore` 闸内一次 `DinoFeatureExtractor.ExtractDualAsync(includePatches: needPatch)` 同时拿 CLS 与 patch;原图喂 `CvGridExtractor.ExtractAsync` 出 7 标量(含 `block_contrast`)。
6. **写库**:单事务 `PhotoDatabase.WriteIndexedAsync`,任一 blob 为 null 则该项不更新(支持按需补齐);顺手携带 `ExifSnapshot`(代表文件 EXIF 已在聚合阶段加载),写入 EXIF 4 列 + rating(任一字段为 null 走 COALESCE 保留旧值)。CLS 同步进 `DinoFeatureCache._memoryCache`。
7. **进度**:按指纹组推进而非按文件;失败组跳过不中断整批,本期不可取消。

**单图懒加载**(相似聚类切图时锚点没入库):
- `DinoFeatureCache.GetOrComputeAsync` → 进程缓存 → 数据库 → 解码缩略图 → ONNX 推理 → 后台 `WriteFeatureAsync`(只写 CLS,不写 patch);`Lazy` 闸门保证同指纹并发只算一次。
- **触发条件(SCOPE-01)**:`SimilarityPanelViewModel` 只在面板展开且文件栏可见时随 `CurrentFile` / 筛选池 / 阈值变化重算;收起即取消在途计算,切图不再隐式推理。面板重新展开时按当前图重算一次。

**诊断页读库快路径**(DINO 诊断工具页):
- `TryReadCachedAsync` 同时取 patch token 与 cv_grid 7 标量;命中即跳过推理与 CV 计算，不回写库（回写归索引器主路径）。
- 派生层(`BuildSharpness` / `BuildShakeField` / `FitRigidMotion` / `ComputePcaRgb` / `ComputeRefCosine`)从原始数据现算 — 改阈值常量立刻见效,不需要重提整库。
- 对角线长度仍由 CV 大图的 `PixelSize` 推出，所以即使缓存命中也要 `BitmapLoader` 解码（LRU 通常已被主视图填充）。

**抖动徽标回填**:
- `ShakeFlagService.EvaluateAsync` 由 `MainViewModel` 在 `FolderVM.AllFilesChanged` 时调用、由 `SimilarityPanelViewModel` 在批量索引完成后调用 — 完全不解码、不推理,只读 `cv_grid` + 尺寸算判定 → 回填 `ImageFile.IsShake` → 缩略图卡片渲染徽标。

**分析栏派生缓存**(切图无卡顿):
- `AnalysisResultCache` 按指纹 LRU 缓存"读库 + 派生层现算"的全部产物(4 张诊断位图 + ShakeField + 判定文字 + patch tokens),容量 32 项 ≈ 50 MB。
- 编排逻辑集中在平台中立的 `Core/AI/AnalysisPresenter`(macOS / iPad / Windows / Android 原生分析栏共用同一份,各自只做位图格式适配;原 Avalonia 壳的 `AnalysisViewModel` 已随退役删除)切图 → 全部工作推迟到 UI 线程的后台优先级(绝不阻塞主图加载);快路径:EXIF 已加载 + 缓存命中 → 同步切换；慢路径：异步读库 + 派生层现算 + 落缓存，旧图保留到新数据就绪再切换。后台读取展示图前先取租约，避免宿主释放导致释放后使用。
- `BitmapPrefetcher` 预取邻居位图后,若分析栏可见,顺手对该邻居走相同未命中流程预热缓存 → 用户前后切图通常已命中。
- PCA SVD 是切图卡顿主因（几十 ms），下沉进缓存后变成纯位图引用切换。
- 用户点击诊断瓦片重算 cosine 时产生的位图归展示器所有，切图或还原中心时显式释放；缓存拥有的位图由展示器只引用，不调用 Dispose。
- **增强预览例外**(需开启设置"分析诊断 / 即时计算DINO/CV",默认关):展示图已是增强图时分析栏不再只读库 —— 直方图(`HistogramRenderer`,新增瓦片,直接支持 F16 线性表面)与细节预览随主图自动跟随增强图;4 张 DINO/CV 诊断瓦片改对增强图**实时重算**(`AnalysisPresenter.ComputeDiagnosticsAsync` 跑 `CvGridExtractor` + `DinoFeatureExtractor` → 复用 `AnalysisComputer.Compute`),重算期间占位"计算中…",产物落 `AnalysisResultCache` 即时缓存(路径 + `#enhanced` 键,不入库),再次切到该图增强态直接命中;关闭增强(或关闭即时计算)即回落到数据库只读路径。直方图位图始终归展示器所有，按竞态闸随主图重算。
- **未提取回退**(需开启同一设置):库/缓存均无该照片提取结果时（含快路径命中空条目）,不再显示"未提取",改走 `AnalysisPresenter` 的未提取回退对当前主图位图立即计算(与增强联动共用 `ComputeDiagnosticsAsync`)——先延迟 500ms 再等主图解码与缩略图通道空闲,期间占位"计算中…";快速翻页在延迟内即被取消,零推理开销,主图浏览性能优先。产物落即时缓存(文件路径键,不入库),翻回已算过的照片直接切换。未开启设置时未提取照片保持"未提取"占位。
- **即时计算缓存**:`AnalysisResultCache` 内按文件路径键的独立存储(增强变体加 `#enhanced` 后缀),与指纹缓存同条目形状、同「不主动 Dispose」语义;不走 LRU — 生命周期跟随主图缓存:`BitmapLoader.CacheStatusChanged` 报某路径失效(淘汰/清空/内存告警精简)时同步移除该路径含增强变体的即时项;"清除特征数据库"(`InvalidateAll`)也一并清空。
- **入库后失效与重路由**:`FolderFeatureIndexer` 每写成功一组特征就 `AnalysisResultCache.Invalidate(fingerprint)` 并抛出 `FeaturesWritten`(静态,库内容变化是全局事实);分析栏展示器订阅它(以及 `ShakeFlagService.RecheckRequested`)后对当前照片重新路由 —— 否则入库前算出的空条目（「未提取」）会一直贴在诊断瓦片上,直到 LRU 淘汰才恢复。`Refresh` 自身也归位到 UI 线程再触碰瓦片与宿主视图。

**清除入口**(开发者用):
- AI 设置页"清除特征数据库"按钮 → 二次确认 → `PhotoDatabase.DeleteDatabaseAsync` 删 `photos.db`/`-wal`/`-shm` 重建空库 → `DinoFeatureCache.InvalidateAll` + `ShakeFlagService.InvalidateAll` + `AnalysisResultCache.InvalidateAll` 清进程缓存,徽标与分析栏派生数据即时失效。
- 启动时检测旧 schema(残留 `feature_vector` / `heatmap` 列,或缺 `cv_image_width/height`,或缺 `focal_length`)自动删库重建,无需手动清理。

### 5.5 内存压力响应(P1-M2 起)

1. **感知**:`MemoryGovernor` 周期探测(2s)+ OS 事件直报(iOS UIKit 告警与 `DispatchSource.MemoryPressure`（Warn → Warning、Critical → Critical）、Android `OnTrimMemory`),状态机带迟滞(进入 40/25/15%、退出 +5%、好转逐级退)。**预算源按平台实测**:iOS 预算 = `phys_footprint + os_proc_available_memory`（含 Metal 纹理与 IOSurface，即 jetsam 上限）、回退物理内存 × 50%；macOS 预算 = 物理内存 × 50%、可用 = 预算 − `phys_footprint`；桌面其余与 Android 沿用各自口径。`IMemoryBudgetProvider.GetFootprintBytes` 让缓存把"非缓存占用"从预算里扣掉（Android 系统水位口径无 per-app footprint，返回 0 表示未知）。启动即写一条 `init` 快照（含 budgetMB / availMB / footprintMB），供真机确认上限。
2. **级联修剪**:级别上升时 Governor 对全部登记客户端同步 `Trim(level)`——统一 LRU(BitmapLoader,Thumb/Proxy/Full 三档分区)、AnalysisResultCache、DinoFeatureCache、ShakeFlagService、ONNX session、增强位图、ImageBufferPool(Caution 起清 ≥1MB 空闲,Critical 清空,释放量达标时后台 GC)。BitmapLoader 的 Warning/Critical 按**保留排名**修剪（Warning 主分区只留排名 ①②③、Critical 只留 ①，缩略图 Caution 起只留排名内）。各级动作矩阵见总方案 §4.3。
3. **行为响应**:`BitmapPrefetcher` Caution 起取消在途轮次且新投递空跑;Warning 起 `DecodeScheduler.MaxBackgroundParallelism` 降为 1;`FolderFeatureIndexer` 逐组轮询暂停;**编码降级**(I2 起,iOS):Warning 起代理档由 RGBA16F 降为 8 位 Display P3,每张驻留字节减半(逐张记录 `stress-flip.jsonl` 的 `enc` 字段;已缓存的 F16 代理在压力下仍可直接复用,不为此重解码)。**解码准入**(P1-M3 起):一切解码(主图/缩略图/预取/索引/CV·DINO)先 `MemoryGovernor.TryAcquire` 拿在途许可,P0 豁免、其余预算不足直接拒绝不排队;解码完成按实际字节复核。**缓存容量由实测预算推导**（每轮探测后重算，缓存在静态字段）：`有效主分区上限 = clamp(用户上限, 256MB, 预算 × (1 − (Caution 进入阈值 + 5)/100) − 非缓存占用)`，非缓存占用 = max(0, 实测 footprint − 主分区缓存 − 缩略图缓存 − 池空闲字节)；iPad 壳置 `BitmapLoader.AutomaticCacheLimit`（忽略用户缓存内存/数量上限，张数视为 400），桌面保留用户上限但再受推导上限约束。腾容量时先 `ImageBufferPool.ReleaseIdle` 丢弃空闲大缓冲，再按排名淘汰缓存条目；缓冲池把"因超帽丢弃"的字节累计到 256MB 时在线程池触发一次 gen2（大数组要等 major GC 才归还系统）。回 Normal 后过 `RecoveryHold`(30s)观察期才渐进恢复预取(打破告警→回填循环)。
4. **观测**:级别迁移、拒批与启动上限落 `memory-events.jsonl`(settings 同目录;iOS 真机为沙盒 `Documents/PhotoViewer/`,devicectl copy 可取回),含各客户端占用、footprint 与近期解码未命中统计。

---

### 5.6 macOS P2 原生看图链路

1. Debug 与 Release 都从 `Program.cs` 直接进入 AppKit 原生启动，不再有壳分流（历史 `--native` / `--legacy-ui` 参数被无害忽略）。NSApplication 生命周期与外部打开进入共享门面。
2. `MainViewModel()` 复用文件源、筛选、评分、设置；原生加载编排使用共享 `PhotoLoadSession`，视口缩放/平移由 `MetalPhotoView` 管理。
3. BitmapLoader 启动期注入 `IPresentationImageDecoder` + `IPresentationEncodingPolicy`；Mac ImageIO 统一 JPEG/HEIF/RAW 主图与代理，产物为带源尺寸/待应用 EXIF 方向的 **RGBA16F LinearSrgb**（Mac 策略恒取 F16；I2 起编码按尺寸档/压力协商，见 §5.7）。代理同样保留广色域；2× 物理视口代理与 Full 使用统一 LRU、准入、租约、预取，准入估算与 GPU 记账按实际编码每像素字节。编码不进缓存键（同一 (路径,尺寸档) 至多一条：长边足够且保真度不低于需求即复用，否则原地替换）。内存按池化实际分配与 GPU 客户端分别记账。
4. Metal 用 ExtendedLinearSrgb 标签、RGBA16F 绘制目标、mipmap 三线性采样和 GPU 方向变换呈现（主图纹理接受 F16 线性 scRGB 与 8 位 P3 两种编码：后者用 `RGBA8Unorm_sRGB` 纹理，采样得线性 P3 再由片段着色器 3×3 矩阵换算到扩展线性 sRGB）；物理 100%/代理能力不足触发 Full，热替换沿用原生变换。导航图复用同纹理的小目标渲染。缺少内嵌预览的用户图片走原生小图子采样，避免全尺寸兜底。渲染核心（设备/管线/层/纹理上传/mipmap/方向变换/帧读回）在 [PhotoViewer.Apple/](../PhotoViewer.Apple/AGENTS.md)，视口数学在跨壳 [PhotoViewer.Shared/](../PhotoViewer.Shared/AGENTS.md)，Mac/iOS/Android 头以 Compile Include 链接；宿主 `MetalPhotoView` 只保留 AppKit 事件与图层挂载。
5. **共享源分层（2026-10-18 A1a）**：`PhotoViewport`、`MainScreenLayout`、`FilmstripGeometry`、`ControlBarLayout`、`AnalysisPanelGeometry`、`SimilarityPanelGeometry`、`ExifTableGeometry` 与 `InputCommandMap` 已从 Apple 共享源提为 `PhotoViewer.Shared/`（纯 C#，三头共链）；`PhotoViewer.Apple/` 只保留展示解码、Metal、Apple 验收与 Apple 输入解码。几何文件迁移只改 namespace/using，Mac 与 iOS 回归零行为变化。
6. 主窗口布局结构、尺寸和边距按原 Avalonia 主界面对拍（冻结几何与截图见 [baseline/avalonia/](baseline/avalonia/README.md)），边框/圆角及适合的字体/菜单保留原生（本轮不改设置设计）；隐藏式标题栏、方形缩略图区/图标按钮与原有标识语义，顶边拖动区双击按系统偏好 `AppleActionOnDoubleClick` 缩放/最小化/不动作（[SHELL-02](spec/shell.md)；隐藏标题栏不自带该行为）；胶片栏定位条件、350ms 缓出及用户接管按 [FILM-01–04](spec/film.md) 执行，点选/交换/列表刷新不主动居中；跨屏刷新物理尺寸，F 优先定位对焦点；控制栏保留 EXIF/评分/命令三个整体分组，按空间换行/换列并居中，超长组可滚动；全屏/窗口/显隐状态持久化。设置固定深色外观，由 SwiftUI（macOS 26）承载系统分栏侧栏与分组表单，侧栏选中态用系统原生高亮（跟随强调色），显隐/排序与主次快捷键同一行，工作副本关闭回写。**P3 起分析栏、相似聚类、工具窗口与增强预览已原生可用**：分析栏几何按原 Avalonia `AnalysisView` 同口径（正方形瓦片边长 = `floor((可用主轴长度 − 20 − 8×(n−1)) / n)`，行布局在主图下方、列布局在其右侧，交叉轴 = 边长 + 20），编排复用 `Core/AI/AnalysisPresenter`；相似聚类面板由筛选条原生开关（行布局 90×27、列布局紧随计数 5 点，与原 Avalonia 筛选条 5 点间距/居中一致）控制，面板复用 DINO CLS 相似度与卡片视觉（列布局扩宽文件栏、行布局从胶片栏右侧切出，MinWidth 106）；工具窗口为独立 AppKit 窗口（EXIF 详情 / DINO 诊断，照片数据统计仍为 Windows 专属不显示入口）；增强预览下移到 `PhotoLoadSession`（开关跨切图持续生效、压力 Warning 自动关闭）。分析栏可见时主图升 Full（细节预览需全分辨率源）；共享索引读取 F16 时先转显示朝向 sRGB8，保持 CV/DINO 契约。
7. 测试设置、数据库及测试照片使用自有目录；原生控件+GPU 帧导出不能替代系统预览/跨屏人工色彩验收（macOS 跨屏色彩与手感已由作者 2026-10-16 验收通过；旧壳不做验收）。验收清单见 [Plans/native-rebuild-phase-2-acceptance.md](../Plans/native-rebuild-phase-2-acceptance.md)。

### 5.7 iOS 原生看图链路（iPad 主屏 I1–I5）

1. **壳与启动**：Debug 与 Release 都只启动 UIKit 原生壳（历史 `--native` / `--legacy-ui` 参数不参与壳选择）。`iOSNativeShell` 的启动顺序与 macOS 对齐：主线程调度 → ReactiveUI → 平台门面（展示解码器/编码策略/HEIF/RAW/预算/Governor/存储/设置/ONNX）→ `MainViewModel()` → `PhotoLoadSession` → 根控制器。启动参数读 `NSProcessInfo`（iOS 不填充托管 `Main(args)`）。文件清单见 [PhotoViewer.iOS/AGENTS.md](../PhotoViewer.iOS/AGENTS.md)。
2. **主图**：`PhotoLoadSession` 只管代际/代理↔Full/租约、增强态；`iOSPhotoView` 把共享 `MetalPhotoRenderer` 挂成 `CAMetalLayer` 子层，视口取共享 `PhotoViewport`。**缩放/平移是 UIKit 标准照片缩放模式，滚动视图是唯一交互真源**：透明 `UIScrollView` 内的透明缩放内容视图只在 `zoomScale=1` 时写适应态显示尺寸，视口变换在每个 `DidZoom`/`Scrolled` 回调里由 `zoomScale`/`contentOffset`/`contentInset` 反算，居中由等量内边距给出；程序化缩放（双击、键盘、细节预览定位）一律用 `ZoomToRect`/`SetZoomScale` 表达，不先写视口再回写滚动视图。**代理↔Full 热替换只换纹理、完全不碰滚动状态**（同一张照片 ImageSize 恒定），只有切图（身份/逻辑尺寸/方向变化）才回适应态；布局/旋转按"同一图像点留在中心 + 同一相对缩放"重建基准。旧实现对带缩放变换的缩放内容视图写 `Frame`，`contentSize` 崩掉、偏移被钳向 (0,0)——真机"放大后照片被拽到左上角"即此，2026-10-17 已移除。宿主先上报有效视口再 `SetFile`（零尺寸守卫，避免解出 1×1 的"合格代理"）；物理 100% 触发 Full。**导航图（缩放指示器）**在主图区右下角（安全区内、避让控制栏/分析栏），非适应态显示：GPU 小目标渲染缩略图（长边 150）+ 白色可见框 + 物理百分比；四端白框都可拖动定位（IMAGE-06）。
3. **编码协商（I2）**：`IPresentationEncodingPolicy` 平台注入——macOS 恒 RGBA16F 扩展线性 sRGB；iPad 代理档 F16、Full 档 8 位 Display P3、压力 ≥ Warning 时代理也降 8 位。渲染器统一换算到扩展线性 sRGB 后绘制，F16 代理 ↔ 8 位 P3 Full 热替换无可面色偏；8 位 P3 装不下的原色（如 Adobe RGB 红原色）钳到 P3 边界，属该编码的既有边界。Core 不含任何 `OperatingSystem.IsIOS()` 分支。
4. **主屏布局**：分区几何与 Mac 共用跨壳 [MainScreenLayout](../PhotoViewer.Shared/AGENTS.md)（文件栏内容宽 116、筛选控件 90×32、开关 90×27、间距 5、外边距 10、隐藏即让位），`iOSMainScreenLayout` 叠加真实 `SafeAreaInsets`：栏位背景铺到屏幕边缘、内容收在安全区、主图满幅（根控制器显式 `PrefersStatusBarHidden`，故 `safe.Top = 0`）。**行布局筛选条在安全内容区内整组居中**（`FilterBarFrames` 默认居中）；列布局保持 116 栏内居中。两向只看窗口尺寸；胶片栏为 `UICollectionView` + 共享 `FilmstripGeometry`（FILM-01–06、350ms 三次缓出、用户接管、减少动态效果直达终点、重新显示/换向定位当前项），筛选条为 `UIButton`+`UIMenu`，控制栏复用 `ControlBarLayout`，分析栏按 `AnalysisPanelGeometry` 真正参与分区。
5. **输入**：主图与卡片各挂原生 `UIContextMenuInteraction`（主图菜单与 Mac 右键菜单同序；卡片菜单标题含完整文件名+拍摄时间与"评分 0–5"）；**主图菜单返回触点处 1 点透明定向预览，菜单锚在手指、照片不抬升不缩小**（卡片保留系统默认抬升）；硬件键盘 `UIKey.KeyCode`（HID usage）→ 共享 `AppleInputMapping` → `AppGesture` → 用户主/次绑定，固定节奏重复（350ms/60ms），INPUT-01 守卫；离散滚轮走用户鼠标绑定；适应态单指横向轻扫 = 上一张/下一张（走共享 Previous/Next 并显式定位，放大态仍是平移）；`UIDropInteraction` 拖入走共享 `ExternalOpenService`；设置页关闭回写后主屏即时刷新（控制栏重建先 `RemoveFromSuperview` 再释放旧分组，旧子视图不会叠在新分组上）。
6. **P3 功能（I5，与 Mac 共用展示器）**：分析栏（`iOSAnalysisHost` + `AnalysisPresenter`，细节预览按实际指针悬停或直接触控按压显示绿框、抬手即清除）、相似聚类（复用胶片栏卡片/布局，行让位、列扩宽）、工具页（浮层 + 导航控制器：EXIF 详情 7 列 / DINO 诊断）、增强预览（`PhotoLoadSession`）。入口为主图长按菜单、控制栏命令、筛选条开关与 EXIF 信息块。增强预览的验收比较**增强前后 GPU 帧像素**（共享 `EnhancementAcceptance`：确定性低对比度素材，平均差 ≥6、最大差 ≥30，关闭后还原），并断言 EXIF 6/8 与 8 位 P3 Full 档增强前后方向/源尺寸/导航帧四角排列不变。增强实现为"转 sRGB8 副本 → 就地并行 CLHE → 回源展示编码"（8 位 Full 回 8 位 P3，F16 表面回 F16），计算期间主图显示 `计算中…`；真机 Release（解释器模式）24MP 1.8 s / 13.7MP 代理 0.9 s，Mac 同一算法 0.26 / 0.09 s。
7. **限制与验收**：iOS 的 RAW 主图/代理由共享 `ApplePresentationDecoder` 的 `CIRawFilter` 路径全分辨率解码（几何取传感器原生尺寸）；只有该路径不可用而回退 ImageIO 时才只得到内嵌预览（约 1.7MP），此时如实停在该尺寸；CoreML EP 在 iOS 原生库不受支持，DINO 走 CPU；ONNX 模型由 `BundleResource` 直接从 `PhotoViewer/Assets/Models/*.onnx` 拷入 app bundle（共享程序集不再内嵌模型，各头各自打包）。`--pv-native-{photo,gesture,layout,input,analysis,similarity,tools}-check` 与 `--pv-dino-check` 报告写 `Documents/PhotoViewer/`；iPadOS 26 拒绝程序化改方向，两向用 `--pv-layout-mode` 核对。程序化证据见 [执行清单 §7](../Plans/native-rebuild-phase-ipad.md) 与 [PhotoViewer.iOS/AGENTS.md](../PhotoViewer.iOS/AGENTS.md)，作者手验清单见 [iPad 验收](../Plans/native-rebuild-phase-ipad-acceptance.md)。

8. **手势与切图语义（2026-10-17 作者手验反馈）**：双击与 Fit 命令统一由共享 `PhotoViewport.ToggleFit` 决定——双击以触点为锚点到物理 100%（放大态回适应）；Fit 命令（按钮/热键）在适应态**优先以照片 Sony 对焦点为中心**放大到 100%（无对焦点取视口中心），已放大回适应。放大态切图沿用 `PhotoViewport.ApplyImage`（与旧 Avalonia `OnBitmapChanged`、Mac 同一份）：保持图像像素→屏幕点的倍率，跟随对焦时居中到新照片对焦点，否则保持同一归一化中心点；代理↔Full 仍只换纹理。**100% 口径**：原生壳是 1 源像素对 1 屏幕物理像素（Mac 已验收口径）；原 Avalonia 壳 `Scale=1` 是 1 源像素对 1 逻辑点（Retina 上 200% 物理），该差异登记在 [IMAGE-02](spec/image.md)。iOS 的滚动视图最小缩放是适应态，物理 100% 不高于适应倍率时（小图、RAW 回退 ImageIO 时的内嵌预览）双击/Fit 夹回适应态；因此回退路径下的 ARW 在 iPad 上"100%"只剩约 1.09–1.2 倍可放，属上一条 RAW 回退限制的口径后果，不是缩放状态缺陷。程序化覆盖：iOS `--pv-native-gesture-check`（24 项检查，含冷启动首图精确 100%、Fit 双向、切图保持倍率/中心、对焦跟随、代理→Full 不跳）+ Mac `--pv-native-smoke/layout/input/analysis`。

### 5.8 Windows P2 原生看图链路

1. **壳与启动**：Debug 与 Release 都只进 WinUI 3 原生壳（历史 `--native` / `--legacy-ui` 参数被忽略）。启动序：`WindowsUiDispatcher` + `SynchronizationContext` → ReactiveUI → 门面（展示解码器/编码策略/HEIF/预算/Governor/存储/设置/ONNX）→ `MainViewModel()` → `WindowsNativeSettingsPresenter` + `WindowsNativeToolsPresenter` + `NativeWindowPresenter` → `WindowsMainWindow` → 外部打开注册。文件清单见 [PhotoViewer.Windows/AGENTS.md](../PhotoViewer.Windows/AGENTS.md)。
2. **主图**：`WindowsPhotoView` = `SwapChainPanel` + D3D11 合成翻转链 `R16G16B16A16_FLOAT`；EXIF 方向在顶点着色器做 UV 置换（绝不旋转像素），三线性 mipmap；`PhotoLoadSession` 管代际/代理↔Full/租约；100% 口径 = 1 源像素对 1 物理像素；导航图由会话帧经 Skia 缩到导航尺寸，白框/百分比由原生控件绘制。
3. **增强与分析**：增强预览已下移到 `PhotoLoadSession`（跨切图持续、压力 Warning 自动关闭）；`WindowsAnalysisPanel` 实现 `IAnalysisHost` 并消费共享 `AnalysisPresenter`，细节预览用 `DisplayCrop` 从容器朝向裁出显示朝向 1:1 方块；直方图/诊断瓦片/未提取回退与 Mac/iPad 同一份编排，不再保留 Windows 专属分析缓存或即时计算实现。
4. **布局**：五分区（筛选 / 胶片条+相似聚类 / 主图 / 分析栏 / 控制栏），自绘标题栏 50 DIP 热区自动显隐，caption 区域按实际按钮边界避让；文件栏内容宽 116 + 1 分隔线、卡片 90×138/项距 96·144、控制栏三组整组换行居中。
5. **设置窗口（W-M5，2026-10-17；同日 Win11 视觉重构）**：独立 960×720 窗口（min 720×560、不进任务栏），Win11 设置页结构：Mica 背景 + WinUI `TitleBar`（齿轮图标、透明 caption 按钮）+ `NavigationView` 左栏（240）+ 五页 `SettingsCard` 卡片（文件/预览/控制/EXIF/AI），右键菜单「设置…」入口；共享 `NativeSettingsSession` 工作副本，关闭一次性回写。应用级 `App.xaml` 只合并 `XamlControlsResources` 并固定深色主题，页面仍全部 C# 构建。此前记录的「本工程无法加载 `NavigationView`/`TitleBar`/`NumberBox`/`TextBox`/`InfoBar`」是缺 XAML 元数据提供器（无 `App.xaml`）导致的假象，**不是控件限制**；详见 [Windows P2-W §17](../Plans/native-rebuild-windows-p2.md) 与 [设置窗口方案](../Plans/native-rebuild-windows-settings.md)。
6. **工具窗口（2026-10-17，P3 收口）**：独立 960×720 窗口（min 800×560、不进任务栏），与设置窗口同一 Win11 结构：Mica + `TitleBar`（扳手图标）+ `NavigationView` 三分类（EXIF 详情 / 照片数据统计 / DINO 诊断）+ `SettingsCard` 卡片。EXIF 详情按旧壳 7 列表格渲染并支持多格式切换；照片数据统计提供文件夹列表、通配符与 CSV 导出（WinRT 选择器绑定工具窗口句柄）；DINO 诊断直接消费共享 `DinoDiagnosticPresenter`，5 张瓦片按 3+2 同宽网格排列（第六格留空）并配刚体拟合文本，数据/推理不在壳内自建。入口为主图右键「工具…」与控制栏 EXIF 信息块点击；共享 `ToolsViewModel` 导航状态与 `NativeWindowPresenter` 单例，主窗口关闭一并释放；照片数据统计仍为 Windows 专属，macOS/iPad 不建入口。
7. **编码与色彩**：`WindowsPresentationEncodingPolicy` 本阶段恒 RGBA16F 线性 scRGB。解码分三条:HEIF 主图/缩略图走 `FastHeifDecoder`(自解析容器 + `pvhevc.dll` 并行解 HEVC 瓦片,`YuvImageConverter` 一次融合完成色度上采样/矩阵/缩放/线性化;容器结构、位深、色度或传输特性超出支持面回落 `LibHeifDecoder`),方向取容器 `irot`/`imir` 并忽略 HEIF 的 EXIF Orientation 项;RAW 走系统 WIC RAW 引擎(`WindowsRawDecoder`,按相机 `DefaultCrop` 裁切,解码器缺失或失败时只接受与有效成像区一致的整幅内嵌 JPEG,不拿小预览冒充全分辨率),方向取 WIC/EXIF 方向码;其余格式走 Skia。方向一律由渲染层顶点变换应用(回落 libheif 时像素已被其摆正,方向固定 1)。ICC/ACM/广色域标签仍未做。
8. **验收**：`--pv-native-smoke` 隔离设置/数据库/日志并生成自有素材；24 项覆盖 150% 下三尺寸×两向、caption、代理→Full、八方向（主图/导航图/`DisplayCrop`）、FILM-03/06、外部打开定位、相似聚类、分析栏（含增强态重算）、窗口四态与关闭释放；`--pv-native-settings` 7 项覆盖设置窗口深色/Mica、NavigationView+TitleBar+NumberBox+InfoBar 已加载、五分类/尺寸、开关+数值框+滑条、重排、录制状态与关闭回写；`--pv-native-tools` 5 项覆盖工具窗口深色/Mica/三分类、EXIF 分组/标签、DINO 5 瓦片与余弦参考点、照片统计文件夹/通配符/CSV 导出与单例重开。200% DPI、真实跨屏、真实相机八方向素材、系统色彩、真实拖拽/录制/文件选择器手感仍待验；程序化通过不等于完整 W-M6 验收。

### 5.9 Android 原生壳（A1b–A5，2026-10-18）

1. **壳分派**：`AndroidApp` 是普通 `Android.App.Application`，不涉及任何 UI 框架。唯一 launcher 是 `AndroidBootstrapActivity`（同时接管 `ACTION_VIEW` / `ACTION_SEND` / `ACTION_SEND_MULTIPLE`），把原始 Intent 转发到 `AndroidNativeActivity` 后自身立即结束。目标 Activity 不带 launcher/filter，避免点图标绕过入口；历史 `pv-flags` 分流参数已不存在，传入时被忽略。
2. **原生壳**：`AndroidNativeActivity : AppCompatActivity` + 固定深色 `Theme.PhotoViewer.Native`（Material 3，颜色走自有 `colors.xml`，不调用 DynamicColors）。`AndroidNativeShell` 的启动序对齐 iOS：色域探测 → `AndroidUiDispatcher`（主 Looper + `SynchronizationContext`）→ ReactiveUI builder → 平台门面（FilePicker/AppResources/Heif/展示解码器/PerformanceBudget/MemoryGovernor/StorageAccess/XmpWriter/SettingsService/设置与工具展示器/DINO-NNAPI）→ `MainViewModel()` → `PhotoLoadSession` → `AndroidNativeRootView`。主图是自有 `SurfaceView` + EGL14/GLES（P3 协商失败回退 sRGB、八方向 UV、RGBA8 mipmap），视口数学复用共享 `PhotoViewport`；主屏按共享 `MainScreenLayout` 叠加系统 insets；筛选条/胶片栏/控制栏/分析栏/相似面板/导航图全部原生自绘或 RecyclerView，设置/工具为 Material 3 BottomSheet。
3. **存储权限**：`READ_EXTERNAL_STORAGE` 仅保留 API ≤ 32，33+ 全部交给 SAF；`MANAGE_EXTERNAL_STORAGE` / `WRITE_EXTERNAL_STORAGE` 已从清单移除。
4. **资源与依赖**：DINO 模型以 `AndroidAsset` 打进 APK（`assets/Models/`，约 +83MB；共享工程不再内嵌模型），`AndroidAppResourceProvider` 把 `avares://PhotoViewer/Assets/...` 映射到 AssetManager 根。`Microsoft.ML.OnnxRuntime` 1.24.4 在 net10.0-android 下误把 `runtimes/linux-arm64/native/*.so` 放进 `FrameworkNativeLibrary`；csproj 的 `_PvRemoveLinuxOnnxFromApk` 在 APK 打包前移除 Linux glibc 版本，保留 AAR 的 Android bionic `libonnxruntime.so`。
5. **构建与验证**：本机 10.0.201 有 android workload，默认 10.0.402 没有；用临时 `global.json` pin 10.0.201 构建（Apple 头仍用 10.0.402），Debug **0 错误 / 20 警告**（低于 A1b 基线 21）。API 35 arm64 模拟器启动日志 `[Bootstrap] → native`、`[Native] Android 原生壳就绪`、`DINO 模型资源=ok`。A2–A5 已落地；A6 程序化检查 `--es pv-check <main|analysis|similarity|settings|tools>` 五模式 PASS 14/18/15/16/16，矩阵/真机门未做。细节见 [Android 交接](../Plans/native-rebuild-android-handoff.md) 与 [执行清单](../Plans/native-rebuild-phase-android.md)。

## 6. 共享 UI 原语的去向

原 Avalonia 壳退役后，共享工程不再包含任何视图、控件、转换器或行为（`Views/`、`Controls/`、`Converters/`、`Behaviors/`、`Platform/Avalonia*`、`App.axaml*`、`ViewLocator.cs` 已随 6D 删除）。当前的等价物按层分散：

- **几何 / 数学**：`PhotoViewer.Shared/`（`PhotoViewport`、`MainScreenLayout`、`FilmstripGeometry`、`ControlBarLayout`、`AnalysisPanelGeometry`、`SimilarityPanelGeometry`、`ExifTableGeometry`、`InputCommandMap`），四端共链；数值令牌在 `PhotoViewer/Core/Design/DesignTokens.g.cs`（由 `Design/tokens.json` 生成）。
- **卡片 / 细节预览 / 诊断瓦片**：各端原生实现（`MacThumbnailItem`、`iOSThumbnailCard`、`WindowsThumbnailCard`、`AndroidThumbnailCard`；`MacDetailPreview` / `iOSDetailPreview` / `WindowsDiagnosticTile`；`MacDiagnosticTile` / `iOSDiagnosticTile` / `WindowsDiagnosticTile`），共享的只有 `AnalysisTile` 数据契约与 `AnalysisPresenter` 编排。
- **显示格式化**：`PhotoViewer/Core/Exif/ExifDisplayConverters.cs`（EXIF 光圈/快门/ISO 文本，原 UI 转换器随旧壳删除后保留同一套文本口径）；快捷键绑定模型在 `PhotoViewer/Core/Input/GestureModels.cs`，设置列表排序参数在 `PhotoViewer/ViewModels/Settings/MoveCommandParameter.cs`，不再有 UI 转换器。
- **输入行为**：命令表与手势映射在 `PhotoViewer.Shared/InputCommandMap.cs` + 各端输入适配（`MacInput` / `AppleInputMapping` / `WindowsInput` / `AndroidInputMapping`）；原 `HorizontalScrollWheelBehavior` 由各端原生滚动事件直接实现。

---

