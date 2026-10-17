# AGENTS.md — PhotoViewer

> Primary briefing for AI coding agents. Loaded automatically at session start.
> Keep concise; update when architecture shifts (rename, split, new module). Do **not** log small bug fixes here.
>
> **文档结构**:本文件仅含项目快照、跨模块流程、平台矩阵、辅助组件、编码/任务/文档规范。模块内的文件清单与职责放在各模块的 `AGENTS.md`(见 §3 模块索引),改文件时会随目录自动加载。
>
> **⚠️ 分支提示**:本文件当前位于 `refactor/native-rebuild` 分支。若你在此分支上工作,先读 [Plans/native-rebuild-handoff.md](Plans/native-rebuild-handoff.md) —— 原生重构的动机、当前状态与下一步全在其中。

---

## 1. Project Snapshot 项目概述

PhotoViewer is a cross-platform photo culling app for photographers, optimized for the **选片 (culling) workflow** — browse JPG/HEIF on the go, sync star ratings back to RAW, fast keyboard-driven UX.

- **Framework**: .NET 10 · MVVM (ReactiveUI)；迁移期旧壳 Avalonia 12，macOS P2 原生壳 AppKit + Metal（Debug 默认原生；Release 通道切原生的时机待作者决定）。**旧壳不再做产品验收，将被原生壳替换**。
- **Platforms**: Windows / macOS / iOS / Android (one shared `PhotoViewer/` project + four head projects)
- **No DI container**: core services are static facades; platform capabilities are injected at startup via `Initialize(...)`.
- **HEIF decoding**: `LibHeifSharp` on Windows/Android, native `ImageIO`/`AVIF` on macOS/iOS.

For end-user feature scope, screenshots, supported cameras, see [README.md](README.md).
For build / run / publish workflow (VS Code Tasks), see [DEV.md](DEV.md).
跨平台 UI 与交互对账草稿见 [Design/ui-contract.md](Design/ui-contract.md)：按稳定 ID 记录已确认要求、旧版表现及平台差异；原生表现与重要约束尚待分类，不把记录的尺寸/参数自动冻结成四平台规格。

---

## 2. Repository Layout 仓库结构

```
PhotoViewer/              # Shared Avalonia project (UI + business logic, no platform code)
PhotoViewer.Desktop/      # Windows head (net10.0-windows)
PhotoViewer.Mac/          # macOS head (net10.0-macos)
PhotoViewer.Apple/        # Apple 共享源（纯源目录，Compile Include 链接进 Mac/iOS head，不新增程序集）
PhotoViewer.iOS/          # iOS / iPadOS head (net10.0-ios)
PhotoViewer.Android/      # Android head (net10.0-android)
Tools/                    # Python: ExifTool 表重生成
Tools/ExifTestTool/       # Standalone CLI for EXIF debugging
Tools/CvDebugTool/        # Standalone CLI for CV v5 抖动诊断（HEIF/JPG → 锐度 PNG + 抖动矢量场 PNG + 文本报告）
Training/                 # AI 训练独立仓库（嵌套 git repo，主库 .gitignore 已忽略，见下方说明）：数据集提取 CLI + DINOv3 ONNX 导出/校验 + 特征可行性探针 + 三期计划 + 数据契约(见 Training/README.md 与 Training/AGENTS.md)
release/                  # Output artifacts (DMG, APK, EXE, IPA)
Directory.Build.props     # Single source of truth for version number
Directory.Packages.props  # Central NuGet version pinning
```

> **`Training/`**：已拆分为独立 git 仓库（PhotoViewer-Training）嵌套在主库工作区下，主库 `.gitignore` 忽略整个目录，主库 `git status`/提交**看不到也不包含** `Training/` 内的任何改动。两侧提交完全分开：改 `Training/` 内文件 → 进 `Training/` 目录在该仓提交；改主库文件 → 在主库提交。一次工作两边都有改动时各提各的（`DatasetBuilder` 以 `ProjectReference` 复用 `PhotoViewer/Core`，提取算法演进常需双仓联动提交）。提交规范与文档导航见 [Training/README.md](Training/README.md)。

---

## 3. Module Index 模块索引

| 目录 | 模块手册 | 一句话职责 |
|---|---|---|
| [PhotoViewer/Core/AI/](PhotoViewer/Core/AI/) | [Core/AI/AGENTS.md](PhotoViewer/Core/AI/AGENTS.md) | DINOv3 特征提取、CV v5 抖动诊断、相似聚类、批量索引、分析栏与 DINO 诊断的平台中立 presenter |
| [PhotoViewer/Core/Database/](PhotoViewer/Core/Database/) | [Core/Database/AGENTS.md](PhotoViewer/Core/Database/AGENTS.md) | `photos.db` SQLite 缓存门面 + 指纹计算 |
| [PhotoViewer/Core/Image/](PhotoViewer/Core/Image/) | [Core/Image/AGENTS.md](PhotoViewer/Core/Image/AGENTS.md) | 图片解码、LRU 缓存、HEIF 桥接、缩略图服务、文件模型 |
| [PhotoViewer/Core/Color/](PhotoViewer/Core/Color/) | [Core/Color/AGENTS.md](PhotoViewer/Core/Color/AGENTS.md) | 色彩标签、线性 scRGB/F16 与 sRGB8 转换、分析方向归一化 |
| [PhotoViewer/Core/Memory/](PhotoViewer/Core/Memory/) | [Core/Memory/AGENTS.md](PhotoViewer/Core/Memory/AGENTS.md) | 统一内存管控:MemoryGovernor(压力状态机/解码准入/快照日志)、预算源契约、PerfTrace 解码计时 |
| [PhotoViewer/Core/Exif/](PhotoViewer/Core/Exif/) | [Core/Exif/AGENTS.md](PhotoViewer/Core/Exif/AGENTS.md) | EXIF/XMP 读写、汉化标签库、Sony MakerNote 解析 |
| [PhotoViewer/Core/Input/](PhotoViewer/Core/Input/) | [Core/Input/AGENTS.md](PhotoViewer/Core/Input/AGENTS.md) | 平台中立手势模型(键/鼠标/修饰键、苹果键盘映射) |
| [PhotoViewer/Core/Platform/](PhotoViewer/Core/Platform/) | [Core/Platform/AGENTS.md](PhotoViewer/Core/Platform/AGENTS.md) | 性能预算、外部打开服务、存储访问门面(平台能力抽象) |
| [PhotoViewer/Core/Settings/](PhotoViewer/Core/Settings/) | [Core/Settings/AGENTS.md](PhotoViewer/Core/Settings/AGENTS.md) | JSON 设置持久化 + 原生设置展示器接口 |
| [PhotoViewer/Core/Storage/](PhotoViewer/Core/Storage/) | [Core/Storage/AGENTS.md](PhotoViewer/Core/Storage/AGENTS.md) | 平台中立存储引用(IFileRef/IFolderRef)+ 文件选择器门面 |
| [PhotoViewer/Core/Tools/](PhotoViewer/Core/Tools/) | [Core/Tools/AGENTS.md](PhotoViewer/Core/Tools/AGENTS.md) | 照片数据统计服务(Windows 限定) |
| [PhotoViewer/ViewModels/Main/](PhotoViewer/ViewModels/Main/) | [ViewModels/Main/AGENTS.md](PhotoViewer/ViewModels/Main/AGENTS.md) | 主窗口 shell + 文件源/文件栏/图片/控制/分析 VM |
| [PhotoViewer/ViewModels/Tools/](PhotoViewer/ViewModels/Tools/) | [ViewModels/Tools/AGENTS.md](PhotoViewer/ViewModels/Tools/AGENTS.md) | 工具壳 + EXIF 详情、照片统计、DINO 诊断 |
| [PhotoViewer/ViewModels/Settings/](PhotoViewer/ViewModels/Settings/) | [ViewModels/Settings/AGENTS.md](PhotoViewer/ViewModels/Settings/AGENTS.md) | 设置页 VM(9 个 partial,共享 iOS 原生设置页) |
| [Training/](Training/) | [Training/AGENTS.md](Training/AGENTS.md) | AI 训练一等模块:数据集提取 CLI、特征可行性探针、ONNX 导出/校验、三期计划、数据契约 |
| [PhotoViewer.Mac/](PhotoViewer.Mac/) | [PhotoViewer.Mac/AGENTS.md](PhotoViewer.Mac/AGENTS.md) | macOS 原生壳、Metal 照片层与平台接线；迁移期保留旧壳（旧壳不参与产品验收） |
| [PhotoViewer.Apple/](PhotoViewer.Apple/) | [PhotoViewer.Apple/AGENTS.md](PhotoViewer.Apple/AGENTS.md) | Apple 平台共享源：展示解码器、Metal 渲染核心、视口/胶片栏/控制栏/分析栏几何、主屏分区几何、命令符号与手势映射（供 Mac 与 iOS 原生壳链接） |
| [PhotoViewer.iOS/](PhotoViewer.iOS/) | [PhotoViewer.iOS/AGENTS.md](PhotoViewer.iOS/AGENTS.md) | iOS 原生壳（UIKit 生命周期、原生存储引用/文档选择器/外部打开、主图 Metal 链路、主屏布局与胶片栏/筛选条/控制栏）；迁移期保留 Avalonia 旧壳 |

> Core 层规则:UI-independent business logic,**不引用 Avalonia 控件**。

---

## 4. Platform Heads 平台差异化实现

Each head project's `Core/` folder contains platform-specific implementations injected during startup (`Program.cs` / `MainActivity` / `AppDelegate`).

| Capability | Windows | macOS | Android | iOS |
|---|---|---|---|---|
| HEIF decoder | `LibHeifDecoder` | `MacHeifDecoder` | `AndroidHeifDecoder` | `iOSHeifDecoder` |
| RAW decoder | — | `MacRawDecoder`（复用 ImageIO，系统 RAW 引擎） | — | `iOSRawDecoder`（复用 ImageIO，系统 RAW 引擎） |
| Performance budget | `DefaultPerformanceBudget` | `DefaultPerformanceBudget` | `AndroidPerformanceBudget` | `iOSPerformanceBudget` |
| ONNX Execution Provider | DirectML EP | CoreML EP | NNAPI EP | CoreML EP |
| Settings storage | `FileStorage` (default) | `MacSettingsStorage` | `AndroidSettingsStorage` | `iOSSettingsStorage` |
| External open bridge | `Program.Main` arg parsing | `Program.Main` + `MacExternalOpenBridge` | `AndroidExternalOpenBridge` | 旧壳 `AppDelegate` + `iOSExternalOpenBridge`（Avalonia 存储项反射）；原生壳 `iOSNativeAppDelegate`/scene + `iOSExternalOpenBridge.PublishNativeFromUrl`（直发原生存储引用，不加载 Avalonia） |
| Storage access | n/a | macOS security-scoped bookmark | SAF persisted permissions | iOS security-scoped bookmark |

---

## 5. Key Workflows 跨模块关键流程

> 每个流程跨越多个模块,所以集中在此处而非任一子文档。子文档只描述模块内部行为,跨模块联动以**链接**指回这里。

### 5.1 Image Loading Pipeline 图片加载流水线

1. **Trigger**: `MainViewModel.CurrentFile` changes (user switches image).
2. **Dispatch**: `ImageViewModel` observes the change, bumps a monotonic **generation counter** and calls `LoadImageAsync`; if the new image is not in cache, `SourceBitmap = null` immediately (old image never stays on screen).
3. **分级代理解码**(P1-M4b 起):fit/浏览先解码**代理图**(长边 = 渲染表面物理像素 × overscan 2.0——2× 超采样抗"解码降采样+上屏降采样"双重软化,守画质红线;Apple 走 `CGImageSourceCreateThumbnailAtIndex` 编解码器级子采样,JPEG 走 Skia codec 子采样,Windows libheif 全解码+Mitchell 精缩)数十~数百 ms 上屏;**缩放超出代理能力 / 分析栏可见(细节预览要 100% 源)** 时后台解码 Full,就绪后**同变换热替换**(几何基准 = 全分辨率逻辑尺寸,EXIF/`ImageOrientationInfo` 推导,`ImageSize` 恒定故无跳变);快速翻页代际闸作废在途 Full。100% 及以上放大永远以 Full 呈现(硬约束)。缓存层两级条目同驻统一 LRU(`(路径, 尺寸档)` 键,Proxy 档命中规则:已有代理长边 ≥ 需求即复用)。P0 失败时代理自动回退 Full。
4. **Cache / decode** (`BitmapLoader.GetBitmapAsync`, runs as **P0** — wrapped in `DecodeScheduler.TrackP0` so background workers pause while it is in flight):
   - Check **LRU memory cache** — hit returns immediately.
   - Miss: **in-flight dedup** (same-path requests share one decode task) → **解码准入 `MemoryGovernor.TryAcquire`**(P1-M3 起;P0 豁免、非 P0 预算不足直接拒绝)→ decode body on the thread pool (`Task.Run`, continuations stay off the UI thread): read stream → format detect (JPG / HEIF) → decode → **apply EXIF rotation** → cache → return。P0-M4 起 Core 全程使用平台中立 `DecodedImage`(SkiaSharp 解码/旋转),`IFileRef` 供流;UI 层经 `Platform/DecodedImageAdapter` 转 `WriteableBitmap`。P1-M3 起返回携带**调用方租约**(用完 `Release`;淘汰只归还缓存自己那份,租约归零缓冲才归还 `ImageBufferPool`)。
   - **Generation gate**: `SourceBitmap` is assigned only if `generation == _loadGeneration && file is still CurrentFile`; stale results are discarded (image stays in cache, harmless)。
5. **Display**: `ImageView` re-binds to `Bitmap`(由适配层转换;主图元素尺寸绑定 `ImageSize` 逻辑坐标系,与纹理实际像素解耦,P1-M4b 起)。
6. **Unified scheduling** (`DecodeScheduler`, static): P0 current image / P1 visible+current thumbnails / P2 neighbour bitmaps + queued thumbnails / P3 analysis prewarm; same-key dedup with priority promotion; background workers (parallelism = `Settings.NativePreloadParallelism`,Warning 压力降为 1) pause while P0 is in flight。
   - **Prefetch**: `ThumbnailListViewModel` notifies `BitmapPrefetcher` (latest-wins — a new request cancels the old round's CTS), which submits each neighbour as a P2 work item:**±1 邻居取 Full**(变焦连续),**其余取代理档**(P1-M4b 起;省内存),**分析栏预热**同项顺带;Caution 压力或恢复观察期内投递即空跑。
   - **Eviction protection**: `ThumbnailListViewModel` reports `BitmapLoader.SetProtectedPaths` (current ± preload window + recently viewed M paths); every eviction path (EnsureCapacity / Cleanup / 压力 Trim) evicts non-protected LRU first and touches protected paths only as a last resort。淘汰只归还缓存持有的租约,仍被借用的缓冲继续存活(P1-M3 租约模型)。

### 5.2 External Open Flow 外部文件打开

1. **Entry**:
   - **Windows**: `Program.Main(args)` parses CLI args → `PublishExternalOpenArgs`.
   - **macOS**: `Program.Main(args)` installs `MacExternalOpenBridge`; `OpenFile / OpenFiles / OpenUrls` capture Finder / Dock / "Open With".
   - **Android**: `MainActivity` captures `Intent` → `AndroidExternalOpenBridge` parses URI.
   - **iOS**: `AppDelegate` `openURL` → `iOSExternalOpenBridge` parses file URL.
2. **Bridge**: every platform funnels through `ExternalOpenService.PublishFile / PublishFiles`. If UI isn't ready, the request lands in a pending queue.
3. **Handle**: `App.axaml.cs` `OnFrameworkInitializationCompleted` registers the handler; `StorageProvider` resolves the URI to `IStorageFile` / `IStorageFolder`.
   - Folder → `FolderViewModel.OpenFolderAsync`.
   - File → `FolderViewModel.OpenImageAsync` — tries to enter the parent folder first; on Apple/Android where parent access is denied, falls back to **single-image mode** so the user can at least see the file.

### 5.3 Rating Sync 评分与元数据同步

1. **Action**: keyboard `1`–`5` or click a star.
2. **Logic**: `MainViewModel.SetRatingAsync`.
3. **Write**: update in-memory `ImageFile`, then `XmpWriter.WriteRatingAsync`:
   - **In-place**: patch the JPG/HEIF XMP block byte-by-byte.
   - **RAW**:本仓实际仍对已有 XMP Rating 做原位更新；独立 `.xmp` 文件的字节写服务可用，但未实现自动创建/关联 RAW sidecar（P2 核验纠正此前文档误写，扩展能力须单独立项）。
4. **Refresh**: `FolderViewModel.RefreshFilters` so rating-based filters update live.
5. **DB 副本**(Plan-2-4):成功写文件后顺手 `PhotoDatabase.UpdateRatingAsync(fp, rating)`。产品行为仍以文件为准,DB rating 只是给 Plan-3 训练 pipeline 取快照用;指纹未入库时静默跳过,不主动占位。

### 5.4 AI 特征提取与持久化 DINO/CV Indexing

> 三类原始数据(DINO CLS / DINO patch token / CV grid 7 标量)同源同时入库。派生层(锐度热力图 / 抖动矢量场 / 刚体拟合 / PCA-RGB / 参考点 cosine)全部现算,阈值常量从不入库。详见 [Plans/dinov3-photo-ranking-plan-2-3-persistence.md](Plans/dinov3-photo-ranking-plan-2-3-persistence.md)。

**指纹与 schema**:
- 指纹 = SHA1(`filename_noext` + `DateTimeOriginal` + `SubSecTimeOriginal`),同次曝光的 RAW/HIF/JPG 共享同一指纹(见 [PhotoFingerprint](PhotoViewer/Core/Database/PhotoFingerprint.cs))。
- 三表:`photos`(身份 + `rating` + EXIF 拍摄参数 4 列 + `cv_grid` BLOB + `cv_grid_spec` 覆盖式)、`photo_features`(CLS 纵表 `(fingerprint, model_id)`)、`photo_patches`(patch token 纵表同主键)。`model_id` 与 `cv_grid_spec` 不匹配视为 cache miss,改算法 = bump 字符串即整库失效。
- EXIF 列(Plan-2-4):`focal_length` / `aperture` / `shutter_speed` 存原始值;`crop_factor = EquivFocalLength / FocalLength`,缺失为 NULL。等效焦距/光圈不入库,训练脚本自行换算。

**批量提取**(用户点"提取全部 / 补齐全部"按钮):
1. **入口**:[SimilarityPanelViewModel.StartIndexingCommand](PhotoViewer/ViewModels/Main/File/SimilarityPanelViewModel.cs) → `FolderFeatureIndexer.RunAsync`。
2. **聚合**:`GroupByFingerprintAsync` 把 RAW+HIF/JPG 合并为指纹组,代表文件按解码代价升序(HEIF→JPG→其他→RAW)。
3. **缺失评估**:每组先 `EvaluateMissingPartsAsync(fingerprint, modelId, cvSpec)` 拿 `(needCls, needPatch, needCv)` 三元组,全齐备即跳过。
4. **解码两路**:DINO 走 `ThumbnailService` 560 短边、CV 走 `BitmapLoader` 原始分辨率(共享 LRU);桌面端 `ProcessorCount/2` 并发,移动端单线程。
5. **推理与提取**:`_inferSemaphore` 闸内一次 `DinoFeatureExtractor.ExtractDualAsync(includePatches: needPatch)` 同时拿 CLS 与 patch;原图喂 `CvGridExtractor.ExtractAsync` 出 7 标量(含 `block_contrast`)。
6. **写库**:单事务 `PhotoDatabase.WriteIndexedAsync`,任一 blob 为 null 则该项不更新(支持按需补齐);顺手携带 `ExifSnapshot`(代表文件 EXIF 已在聚合阶段加载),写入 EXIF 4 列 + rating(任一字段为 null 走 COALESCE 保留旧值)。CLS 同步进 `DinoFeatureCache._memoryCache`。
7. **进度**:按指纹组推进而非按文件;失败组跳过不中断整批,本期不可取消。

**单图懒加载**(相似聚类切图时锚点没入库):
- `DinoFeatureCache.GetOrComputeAsync` → 进程缓存 → DB → 解码缩略图 → ONNX 推理 → 后台 `WriteFeatureAsync`(只写 CLS,不写 patch);`Lazy` 闸门保证同指纹并发只算一次。
- **触发条件(SCOPE-01)**:`SimilarityPanelViewModel` 只在面板展开且文件栏可见时随 `CurrentFile` / 筛选池 / 阈值变化重算;收起即取消在途计算,切图不再隐式推理。面板重新展开时按当前图重算一次。

**诊断页读库快路径**(DINO 诊断工具页):
- `TryReadCachedAsync` 同时取 patch token 与 cv_grid 7 标量;命中即跳过推理与 CV 计算,不回写库(回写归 indexer 主路径)。
- 派生层(`BuildSharpness` / `BuildShakeField` / `FitRigidMotion` / `ComputePcaRgb` / `ComputeRefCosine`)从原始数据现算 — 改阈值常量立刻见效,不需要重提整库。
- diagonal 仍由 CV 大图 `PixelSize` 推出,所以缓存命中也要 `BitmapLoader` 解码(LRU 通常已被主视图填充)。

**抖动徽标回填**:
- `ShakeFlagService.EvaluateAsync` 由 `MainViewModel` 在 `FolderVM.AllFilesChanged` 时调用、由 `SimilarityPanelViewModel` 在批量索引完成后调用 — 完全不解码、不推理,只读 `cv_grid` + 尺寸算判定 → 回填 `ImageFile.IsShake` → 缩略图卡片渲染徽标。

**分析栏派生缓存**(切图无卡顿):
- `AnalysisResultCache` 按指纹 LRU 缓存"读库 + 派生层现算"的全部产物(4 张诊断位图 + ShakeField + 判定文字 + patch tokens),容量 32 项 ≈ 50 MB。
- 编排逻辑集中在平台中立的 `Core/AI/AnalysisPresenter`(旧壳 `AnalysisViewModel` 与 macOS 原生分析栏共用同一份,各自只做位图格式适配)切图 → 全部工作推迟到 `DispatcherPriority.Background`(绝不阻塞主图加载);快路径:EXIF 已加载 + 缓存命中 → 同步 swap;慢路径:异步读库 + 派生层现算 + 落 cache,旧图保留到新数据就绪再 swap。后台读取展示图前先取租约,避免宿主释放导致 use-after-release。
- `BitmapPrefetcher` 预取邻居位图后,若分析栏可见,顺手对该邻居走相同 miss 流程预热 cache → 用户前后切图通常已命中。
- PCA SVD 是切图卡顿主因(几十 ms),sink 进 cache 后变成纯位图引用切换。
- 用户点击诊断瓦片重算 cosine 时产生的位图归 presenter 所有,切图或还原中心时显式释放;cache 拥有的位图 presenter 只引用,不 Dispose。
- **增强预览例外**(需开启设置"分析诊断 / 即时计算DINO/CV",默认关):展示图已是增强图时分析栏不再只读库 —— 直方图(`HistogramRenderer`,新增瓦片,直接支持 F16 线性表面)与细节预览随主图自动跟随增强图;4 张 DINO/CV 诊断瓦片改对增强图**实时重算**(`AnalysisPresenter.ComputeDiagnosticsAsync` 跑 `CvGridExtractor` + `DinoFeatureExtractor` → 复用 `AnalysisComputer.Compute`),重算期间占位"计算中…",产物落 `AnalysisResultCache` 即时缓存(路径 + `#enhanced` 键,不入库),再次切到该图增强态直接命中;关闭增强(或关闭即时计算)即回落 DB 只读路径。直方图位图始终 presenter 所有、按竞态闸随主图重算。
- **未提取回退**(需开启同一设置):库/cache 均无该照片提取结果时(含快路径命中空 entry),不再显示"未提取",改走 `AnalysisPresenter` 的未提取回退对当前主图位图立即计算(与增强联动共用 `ComputeDiagnosticsAsync`)——先延迟 500ms 再等主图解码与缩略图通道空闲,期间占位"计算中…";快速翻页在延迟内即被取消,零推理开销,主图浏览性能优先。产物落即时缓存(文件路径键,不入库),翻回已算过的照片直接 swap。未开启设置时未提取照片保持"未提取"占位。
- **即时计算缓存**:`AnalysisResultCache` 内按文件路径键的独立存储(增强变体加 `#enhanced` 后缀),与指纹缓存同 Entry 形状、同"不主动 Dispose"语义;不走 LRU — 生命周期跟随主图缓存:`BitmapLoader.CacheStatusChanged` 报某路径失效(淘汰/清空/内存告警精简)时同步移除该路径含增强变体的即时项;"清除特征数据库"(`InvalidateAll`)也一并清空。
- **入库后失效与重路由**:`FolderFeatureIndexer` 每写成功一组特征就 `AnalysisResultCache.Invalidate(fingerprint)` 并抛出 `FeaturesWritten`(静态,库内容变化是全局事实);分析栏 presenter 订阅它(以及 `ShakeFlagService.RecheckRequested`)后对当前照片重新路由 —— 否则入库前算出的空 entry("未提取")会一直贴在诊断瓦片上,直到 LRU 淘汰才恢复。`Refresh` 自身也归位到 UI 线程再触碰瓦片与宿主视图。

**清除入口**(开发者用):
- AI 设置页"清除特征数据库"按钮 → 二次确认 → `PhotoDatabase.DeleteDatabaseAsync` 删 `photos.db`/`-wal`/`-shm` 重建空库 → `DinoFeatureCache.InvalidateAll` + `ShakeFlagService.InvalidateAll` + `AnalysisResultCache.InvalidateAll` 清进程缓存,徽标与分析栏派生数据即时失效。
- 启动时检测旧 schema(残留 `feature_vector` / `heatmap` 列,或缺 `cv_image_width/height`,或缺 `focal_length`)自动删库重建,无需手动清理。

### 5.5 内存压力响应(P1-M2 起)

1. **感知**:`MemoryGovernor` 周期探测(2s)+ OS 事件直报(iOS `DidReceiveMemoryWarning`、Android `OnTrimMemory`),状态机带迟滞(进入 40/25/15%、退出 +5%、好转逐级退)。
2. **级联修剪**:级别上升时 Governor 对全部登记 client 同步 `Trim(level)`——统一 LRU(BitmapLoader,Thumb/Proxy/Full 三档分区)、AnalysisResultCache、DinoFeatureCache、ShakeFlagService、ONNX session、增强位图、ImageBufferPool(Critical 整池清空)。各级动作矩阵见 master plan §4.3。
3. **行为响应**:`BitmapPrefetcher` Caution 起取消在途轮次且新投递空跑;Warning 起 `DecodeScheduler.MaxBackgroundParallelism` 降为 1;`FolderFeatureIndexer` 逐组轮询暂停;**编码降级**(I2 起,iOS):Warning 起代理档由 RGBA16F 降为 8 位 Display P3,每张驻留字节减半(逐张记录 `stress-flip.jsonl` 的 `enc` 字段;已缓存的 F16 代理在压力下仍可直接复用,不为此重解码)。**解码准入**(P1-M3 起):一切解码(主图/缩略图/预取/索引/CV·DINO)先 `MemoryGovernor.TryAcquire` 拿在途许可,P0 豁免、其余预算不足直接拒绝不排队;解码完成按实际字节复核。回 Normal 后过 `RecoveryHold`(30s)观察期才渐进恢复预取(打破告警→回填循环)。
4. **观测**:级别迁移与拒批落 `memory-events.jsonl`(settings 同目录;iOS 真机为沙盒 `Documents/PhotoViewer/`,devicectl copy 可取回),含各 client footprint 与近期解码 miss 统计。

---

### 5.6 macOS P2 原生看图链路

1. Debug 默认 AppKit，Release 默认旧壳；`--native`/`--legacy-ui` 明确切换。原生启动不创建 Avalonia 应用/窗口，NSApplication 生命周期与外部打开进入共享门面。
2. `MainViewModel(nativePresentation: true)` 复用文件源、筛选、评分、设置；旧 `ImageViewModel` 不订阅加载、不创建 WriteableBitmap。原生加载编排使用共享 `PhotoLoadSession`，视口缩放/平移由 `MetalPhotoView` 管理。
3. BitmapLoader 启动期注入 `IPresentationImageDecoder` + `IPresentationEncodingPolicy`；Mac ImageIO 统一 JPEG/HEIF/RAW 主图与代理，产物为带源尺寸/待应用 EXIF 方向的 **RGBA16F LinearSrgb**（Mac 策略恒取 F16；I2 起编码按尺寸档/压力协商，见 §5.7）。代理同样保留广色域；2× 物理视口代理与 Full 使用统一 LRU、准入、租约、预取，准入估算与 GPU 记账按实际编码每像素字节。编码不进缓存键（同一 (路径,尺寸档) 至多一条：长边足够且保真度不低于需求即复用，否则原地替换）。内存按池化实际分配与 GPU 客户端分别记账。
4. Metal 用 ExtendedLinearSrgb 标签、RGBA16F 绘制目标、mipmap 三线性采样和 GPU 方向变换呈现（主图纹理接受 F16 线性 scRGB 与 8 位 P3 两种编码：后者用 `RGBA8Unorm_sRGB` 纹理，采样得线性 P3 再由片段着色器 3×3 矩阵换算到扩展线性 sRGB）；物理 100%/代理能力不足触发 Full，热替换沿用原生变换。导航图复用同纹理的小目标渲染。缺少内嵌预览的用户图片走原生小图子采样，避免全尺寸兜底。渲染核心（设备/管线/层/纹理上传/mipmap/方向变换/帧读回）与视口数学在共享 [PhotoViewer.Apple/](PhotoViewer.Apple/AGENTS.md)，Mac 头以 Compile Include 链接；宿主 `MetalPhotoView` 只保留 AppKit 事件与图层挂载（同一共享源将来供 iOS 原生壳复用）。
5. 主窗口布局结构、尺寸和边距按 Avalonia 主界面对拍，边框/圆角及适合的字体/菜单保留原生（本轮不改设置设计）；隐藏式标题栏、方形缩略图区/图标按钮与原有标识语义，顶边拖动区双击按系统偏好 `AppleActionOnDoubleClick` 缩放/最小化/不动作（[SHELL-02](Design/ui-contract.md)；隐藏标题栏不自带该行为）；胶片栏定位条件、350ms 缓出及用户接管按 [FILM-01–04](Design/ui-contract.md) 执行，点选/交换/列表刷新不主动居中；跨屏刷新物理尺寸，F 优先定位对焦点；控制栏保留 EXIF/评分/命令三个整体分组，按空间换行/换列并居中，超长组可滚动；全屏/窗口/显隐状态持久化。设置固定深色外观，由 SwiftUI（macOS 26）承载系统分栏侧栏与分组表单，侧栏选中态用系统原生高亮（跟随强调色），显隐/排序与主次快捷键同一行，工作副本关闭回写。**P3 起分析栏、相似聚类、工具窗口与增强预览已原生可用**：分析栏几何按旧壳 `AnalysisView` 同口径（正方形瓦片边长 = `floor((可用主轴长度 − 20 − 8×(n−1)) / n)`，行布局在主图下方、列布局在其右侧，交叉轴 = 边长 + 20），编排复用 `Core/AI/AnalysisPresenter`；相似聚类面板由筛选条原生 toggle（行布局 90×27、列布局紧随计数 5 点，与旧壳筛选条 5 点间距/居中一致）控制，面板复用 DINO CLS 相似度与卡片视觉（列布局扩宽文件栏、行布局从胶片栏右侧切出，MinWidth 106）；工具窗口为独立 AppKit 窗口（EXIF 详情 / DINO 诊断，照片数据统计仍为 Windows 专属不显示入口）；增强预览下移到 `PhotoLoadSession`（开关跨切图持续生效、压力 Warning 自动关闭）。分析栏可见时主图升 Full（细节预览需全分辨率源）；共享索引读取 F16 时先转显示朝向 sRGB8，保持 CV/DINO 契约。
6. 测试设置、数据库及测试照片使用自有目录；原生控件+GPU 帧导出不能替代系统预览/跨屏人工色彩验收（macOS 跨屏色彩与手感已由作者 2026-10-16 验收通过；旧壳不做验收）。验收清单见 [Plans/native-rebuild-phase-2-acceptance.md](Plans/native-rebuild-phase-2-acceptance.md)。

### 5.7 iOS 原生看图链路（iPad 主屏 I2–I3）

1. Debug 默认 UIKit 原生壳（旧壳 `--legacy-ui` 回退），启动不创建任何 Avalonia 对象；`MainViewModel(nativePresentation: true)` + 共享 `PhotoLoadSession`，主屏由 `iOSNativeRootViewController` 承载，主图由 `iOSPhotoView`（共享 `MetalPhotoRenderer` + `PhotoViewport`）渲染。
2. **编码协商（I2 已定决策）**：`IPresentationEncodingPolicy` 由平台注入 —— macOS 恒 RGBA16F 扩展线性 sRGB；iPad 代理档 RGBA16F、Full 档 8 位 Display P3，内存压力 ≥ Warning 时代理档也降 8 位 P3（每张驻留字节减半）。两次解码都从同一 ImageIO 源转换，渲染器统一换算到扩展线性 sRGB 后绘制，故 F16 代理 ↔ 8 位 P3 Full 热替换无可面色偏（P3 立方体装不下的原色——如 Adobe RGB 红原色——只能钳到 P3 边界，这是 8 位 P3 编码的既有边界）。Core 不含任何 `OperatingSystem.IsIOS()` 分支。
3. **手势**：透明 `UIScrollView` 只作手势与物理驱动（内容尺寸 = 适应态显示尺寸），`zoomScale`/`contentOffset` 与 `PhotoViewport` 变换互为逆映射并在同一回调内重绘；双击在适应与物理 100%（点按位置锚点）间动画切换，`minimumZoomScale` = 适应，`maximumZoomScale` = 物理 16 倍（与共享视口上限同口径）。
4. **`NeedsFull` 与热替换**：宿主把物理视口（bounds × displayScale）与 `NeedsFull` 上报 `PhotoLoadSession.UpdateViewport`，代理目标 = 2× 物理视口；物理 100% 触发 Full，热替换沿用当前视口变换（含 `PhotoViewport.ApplyGestureView` 的手势态）。**视口未成形（尺寸 < 1）时禁止改写代理目标**，否则目标被钳成 1 像素会解码出 1×1 的"合格代理"并永不重载。
5. **主屏布局（I3）**：分区几何与 Mac 原生壳共用共享 [MainScreenLayout](PhotoViewer.Apple/AGENTS.md)（文件栏内容宽 116、筛选控件 90×32、toggle 90×27、间距 5、外边距 10、两向分区与"隐藏即让空间给主图"），iOS 侧在 `iOSMainScreenLayout` 叠加真实 `SafeAreaInsets`：栏位背景铺到屏幕边缘、栏位内容收在安全区内、主图满幅。两向只看窗口尺寸，隐藏栏位释放空间，分析栏留槽位但 I5 前交叉轴为 0。胶片栏为 `UICollectionView` + 共享 `FilmstripGeometry` 布局（96×144 卡片、FILM-01–06 语义、350ms 三次缓出定位、用户接管、减少动态效果直达终点、重新显示与换向定位当前项）；筛选条为 `UIButton`+`UIMenu`；控制栏复用共享 `ControlBarLayout`（分组显隐/顺序来自共享设置，未实现的 I5 命令不生成按钮）。临时保留主图区空态提示（点击打开文件夹）与最小设置入口，I4 上下文菜单落地后删除。
6. **验收**：`--pv-native-photo-check <目录>`（代理先行/编码/热替换变换/八方向 GPU 角点/色卡线性值）、`--pv-native-layout-check <目录>`（两向与自动布局、安全区、栏位显隐、筛选条/控制栏几何、卡片几何、FILM-01–06 交互）与 `--pv-dino-check <文件>`（onnxruntime 静态链接 + 一次 DINO 推理的 CLS/EP/耗时），报告写 `Documents/PhotoViewer/native-{photo,layout}-checks.txt` 与 `dino-check.txt`；`--pv-layout-mode row|col` 供两向截图（iPadOS 不允许程序化改方向，见执行清单发现清单）。Mac 与 iOS 共用 [PhotoViewer.Apple/AppleAcceptanceChecks.cs](PhotoViewer.Apple/AGENTS.md)。素材与真机步骤见 [Plans/native-rebuild-phase-ipad.md](Plans/native-rebuild-phase-ipad.md) 与 [PhotoViewer.iOS/AGENTS.md](PhotoViewer.iOS/AGENTS.md)。
7. **DINO / ONNX Runtime（I2 后补）**：`onnxruntime.xcframework` 的 iOS 切片是 ar 静态库，iOS head 的 `_PvExpandOnnxRuntimeNativeReference` 在原生链接前排好 `NativeReference` 展开、把切片按 `Kind=Static + ForceLoad` 链接，并把 `_OrtGetApiBase` / CoreML 入口写进 `mtouch-symbols.list`（Release strip 会保留）；`iOSNativeShell` 用 `DllImportResolver` 把 `onnxruntime` 映射到主程序。CoreML EP 在 `Microsoft.ML.OnnxRuntime` 1.24.4/1.25.1 的 iOS 原生库中均返回 not supported → 推理走 CPU（注册失败会记录日志并按既有逻辑回退，不静默）。相似聚类只在面板展开且文件栏可见时触发（§5.4）。

## 6. UI Helpers 共享控件 / 转换器 / 行为

- [Controls/](PhotoViewer/Controls/):
  - `ThumbnailCard` — 主缩略图列表与相似聚类列表共用的 90×138 卡片:缩略图 + 文件名 + 自定义第二行 + 6 星级 + 抖动徽标。徽标在 `File.IsShake == true` 时叠在 80×80 缩略图区右下角。第二行支持柱状进度条覆盖(`SecondLineProgress` 非 null 时启用,文本叠在条上),相似聚类用于把分数 75%~100% 映射成柱状。
  - `DetailPreview` — 细节预览/分析栏共用,圆角灰边方框 + 左上药丸标签 + hover/double-tap 联动主图绿框。
  - `DiagnosticTile` — DINO 诊断页与分析栏共用的诊断瓦片,letterbox + AspectRatio + Crosshair + CornerLabel + PlaceholderText。嵌套圆角结构与 DetailPreview 像素级对齐(外 Border CornerRadius=6 + 内层 InnerClip CornerRadius=5 裁切);`SyncSquareLayout` 必须从外尺寸扣 `BorderThickness` 再算 letterbox,否则 1px 边框会被内容覆盖。
  - `SortableList` (drag-reorder for settings), `HotkeyButton` (hotkey capture), `CheckableMenuHeader`, `DeferredNumericTextBox`, `OverlayGlyphText`。
- [Converters/](PhotoViewer/Converters/): `ExifConverters` (aperture / shutter / ISO formatters), `KeyGestureToStringConverter`, `LayoutConverters`。
- [Behaviors/](PhotoViewer/Behaviors/): `HorizontalScrollWheelBehavior` (mouse-wheel → horizontal scroll for filmstrip)。

---

## 7. Build / Run 快速参考

Always go through the VS Code Tasks defined in [DEV.md](DEV.md) — they wrap signing, trimming, install, and Logcat/console attach. Do **not** invoke `dotnet publish` directly for distribution builds.

Most-used Tasks:
- `Debug Windows` / `Debug Mac` / `Debug Android` / `Debug iOS` / `Debug iOS Simulator` — build + launch on target.
- `Run Mac` / `Run Windows` / `Install Android` / `Install iOS` — re-launch last build without rebuilding.
- `Publish Windows EXE / Mac DMG / Android APK / iOS IPA (Release)` — distribution artifacts → `release/`.
- `Clean Build Caches (All Platforms)` — when caches go stale (SDK upgrade, weird link errors).

Version bumps: edit [Directory.Build.props](Directory.Build.props) only.

**iOS free-account profile renewal**: 免费 Apple ID 的 Provisioning Profile 每 7 天过期，`Debug iOS` 会报 `找不到任何可用预配配置文件`。执行 `Refresh iOS Profile` task 调占位工程 [Tools/RefreshIosProfile/](Tools/RefreshIosProfile/) 走 `xcodebuild -allowProvisioningUpdates` 续期，然后重跑 `Debug iOS` 即可。Team ID 存放在 gitignored 的 `Tools/RefreshIosProfile/Local.xcconfig`（从 `Local.xcconfig.sample` 复制而来），pbxproj 不含个人信息。

---

## 8. Coding Standards 编码规范

**Reuse first**:
- Prefer reusing existing functions and components over duplicating code. New features and components should be designed with reasonable reuse/extension hooks.
- Keep code readable and maintainable. Split oversized files — 300–1000 lines per file is the target range.

**Pick the right solution, not the smallest diff**:
- Large-scale refactors are allowed when warranted; audit every call site carefully. **Do not** pile up small ad-hoc patches just to minimise the diff.
- Fallbacks are allowed **only** when forced by platform constraints or untrusted user input. Otherwise, **no open-ended fallbacks and no silent fallbacks** — the final code must keep exactly one reliable path, and any obsolete branches or dead code must be removed in the same edit.

## 9. Task Standards 任务规范

**End-to-end verification**:
- All new code must be syntactically correct and **compile cleanly — no errors, no warnings**.
- All bug fixes must be **installed on a real device and launched without crashing**, proving the bug is fixed and no regression has been introduced.

**Own the task to completion**:
- Every task must go through **at least one full end-to-end run**. The task is not done until the app starts successfully.

**Sync docs in the same task**:
- 任务收尾前,**必须**回看本次改动是否让任一 `AGENTS.md`(根或模块)的描述失准 — 文件清单变化、职责变化、跨模块流程改路径、阈值/字段命名调整等。失准必须在**同一任务**内修文档,否则任务**未完成**,与"编译通过"同级。
- 判据:行为表 / 跨模块流程 / 关键字段命名过期 → 必更新;只是改实现细节、小修 bug、加日志 → 不动。
- UI 移植或交互变更必须核对 [Design/ui-contract.md](Design/ui-contract.md) 的契约 ID；更新行为及平台验收状态，未通过的手感/设备项不得以编译或其他平台结果代替。

**Use legitimate means only**:
- If repeated attempts still fail, stop retrying indefinitely — surface the blocker in the conversation.
- **Never** resort to cheating tactics (e.g. suppressing errors, returning mock data in place of real work, etc.).

## 10. Documentation Standards 文档规范

**Comments**:
- Prefix every function with an XML comment (`/// <summary>...</summary>`) describing purpose, parameters, and return value. Add inline comments for non-trivial logic. **Write comments in Chinese.**

**Docs**:
- 根 `AGENTS.md` 与各模块 `AGENTS.md` 边界:**模块内文件清单与职责** → 子文档;**跨模块流程、平台矩阵、共享 UI 辅助、规范** → 根。子文档点到外部类只用链接,不复述行为,避免两边漂移。
- 文档同步是任务收尾的硬要求,见 §9 "Sync docs in the same task"。

**Tidiness**:
- Do not litter the code with redundant comments for minor fixes — a concise summary in the conversation is enough. In particular, **never add explanatory/note-like text to UI strings**. Keep code comments systematic and the UI visually clean.
