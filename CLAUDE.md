# CLAUDE.md — PhotoViewer

> Primary briefing for Claude Code. Loaded automatically at session start.
> Keep concise; update when architecture shifts (rename, split, new module). Do **not** log small bug fixes here.

---

## 1. Project Snapshot 项目概述

PhotoViewer is a cross-platform photo culling app for photographers, optimized for the **选片 (culling) workflow** — browse JPG/HEIF on the go, sync star ratings back to RAW, fast keyboard-driven UX.

- **Framework**: Avalonia 12 · .NET 10 · MVVM (ReactiveUI)
- **Platforms**: Windows / macOS / iOS / Android (one shared `PhotoViewer/` project + four head projects)
- **No DI container**: core services are static facades; platform capabilities are injected at startup via `Initialize(...)`.
- **HEIF decoding**: `LibHeifSharp` on Windows/Android, native `ImageIO`/`AVIF` on macOS/iOS.

For end-user feature scope, screenshots, supported cameras, see [README.md](README.md).
For build / run / publish workflow (VS Code Tasks), see [DEV.md](DEV.md).

---

## 2. Repository Layout 仓库结构

```
PhotoViewer/              # Shared Avalonia project (UI + business logic, no platform code)
PhotoViewer.Desktop/      # Windows head (net10.0-windows)
PhotoViewer.Mac/          # macOS head (net10.0-macos)
PhotoViewer.iOS/          # iOS / iPadOS head (net10.0-ios)
PhotoViewer.Android/      # Android head (net10.0-android)
Tools/                    # Python: ExifTool 表重生成 + DINOv3 ONNX 导出/校验 + CV/patch PoC notebooks
Tools/ExifTestTool/       # Standalone CLI for EXIF debugging
Tools/CvDebugTool/        # Standalone CLI for CV v5 抖动诊断（HEIF/JPG → 锐度 PNG + 抖动矢量场 PNG + 文本报告）
release/                  # Output artifacts (DMG, APK, EXE, IPA)
Directory.Build.props     # Single source of truth for version number
Directory.Packages.props  # Central NuGet version pinning
```

---

## 3. Shared Core 核心通用服务 ([PhotoViewer/Core/](PhotoViewer/Core/))

UI-independent business logic. **Do not reference Avalonia controls from this layer.**

**AI/** — DINOv3 特征 + CV 网格 + 相似聚类（`namespace PhotoViewer.Core.AI`）

> AI 是目前主要开发任务，当前阶段聚焦 CV 诊断 v5（抖动方向一致性路线），见 [Plans/dinov3-photo-ranking-plan-2-2-shake-v5.md](Plans/dinov3-photo-ranking-plan-2-2-shake-v5.md)；上游：[Plans/dinov3-photo-ranking-plan-1.md](Plans/dinov3-photo-ranking-plan-1.md) · [Plans/dinov3-photo-ranking-plan-2-0.md](Plans/dinov3-photo-ranking-plan-2-0.md) · [Plans/dinov3-photo-ranking-plan-2-1-wrapup.md](Plans/dinov3-photo-ranking-plan-2-1-wrapup.md)（含 v1 标量/诊断图公式、14 张样本验收 checklist、废弃方案墓碑）。

| File | 模块 | Responsibility |
|---|---|---|
| [AI/DinoModelResources.cs](PhotoViewer/Core/AI/DinoModelResources.cs) | 模型资源常量 | ONNX 资源 URI、输入规格（518/ImageNet mean-std）、I/O 端口名、`PatchSize`/`PatchGrid`/`PatchTokenCount`、`ModelId`（当前 `dinov3_vits16_f32_518_v1`）。改动此处需同步两个 Python 工具。 |
| [AI/DinoFeatureExtractor.cs](PhotoViewer/Core/AI/DinoFeatureExtractor.cs) | DINOv3 推理门面 | ONNX Runtime CPU EP 静态门面；延迟建 session + `EnsureDualOutputSchema` 早失败；`ExtractAsync` 只取 L2 归一化的 CLS 向量；`ExtractDualAsync(..., includePatches)` 同时返回 1024×384 patch token，供诊断工具页消费。 |
| [AI/DinoFeatureCache.cs](PhotoViewer/Core/AI/DinoFeatureCache.cs) | CLS 向量缓存 | 指纹索引 + 进程内存缓存 + `Lazy` 闸门（同指纹并发只推一次）。`GetOrComputeAsync` miss 后台写库；`TryReadAsync` 只读缓存不触发推理（`SimilarityService` 的候选池走这条）。写库列是 `photos.feature_vector` + `feature_model`（单列方案，纵表 schema 留到远期）。 |
| [AI/FolderFeatureIndexer.cs](PhotoViewer/Core/AI/FolderFeatureIndexer.cs) | 全文件夹批量索引 | 实例化调度器：桌面端 `ProcessorCount/2` 并行解码 + 单线程 ONNX 推理；移动端单线程。跳过已入库指纹，单张失败不中断整批，本期**不可取消**。完成后 `PutMemoryCache` 同步到进程缓存。 |
| [AI/SimilarityService.cs](PhotoViewer/Core/AI/SimilarityService.cs) | 相似聚类 | 基于 DINOv3 [CLS] cosine 的相似聚类（默认阈值 0.75，上限 64 项，拍摄时间差作 tiebreaker）。锚点必算、池内只读缓存 — 避免一次切图触发成百上千次推理。 |
| [AI/CvGridResult.cs](PhotoViewer/Core/AI/CvGridResult.cs) | CV 网格结果 POCO | 32×32 格 × 6 标量 × 1 层（无金字塔）= 6144 float；标量 `edge_count` / `edge_width_p20` / `edge_width_median` / `drag_width` / `drag_direction` / `anisotropy`；`Version`（当前 `cv_grid_v4_structtensor`）+ 小端 BLOB Encode/Decode。块尺寸自适应 `clamp(短边/32, 64, 192)`。 |
| [AI/CvGridExtractor.cs](PhotoViewer/Core/AI/CvGridExtractor.cs) | CV v5 标量提取 | 纯托管，无 native 依赖；每格 Sobel + 块级累结构张量 (Sxx,Syy,Sxy) → `θ_st` / `anisotropy`；边种子按自适应 τ_edge + NMS，Marziliano 单边步进算边宽；`drag_bucket` = 离 (θ_st+π/2) 最近的有效 bucket（测拖影线方向）；`MaxHalfWidth` 按对角线 0.8% 自适应；同时累 luma 64-bin 直方图算 `block_contrast = p98-p2`（v5 r3 软门控用，**只算不存**）；`Parallel.For` 跨格并行。`ExtractAsync(Bitmap)` 返回 CvGridResult，`ExtractWithContrastAsync(Bitmap)` 同时返回 contrast 平面，`ExtractFromLuma(luma,w,h)` 给 CvDebugTool 用。**不接调度、不入库**。 |
| [AI/CvHeatmap.cs](PhotoViewer/Core/AI/CvHeatmap.cs) | CV 诊断图 + 判定（纯函数） | `BuildSharpness` 边宽对数热力图 ×（可选）`ContrastFactor`；`BuildShakeField` 输出 32×32 `Direction` / `Width` / `Mask` / `LocalConsistency`（5×5 邻域 2θ 圆形均值）/ `Contrast` + 图像对角线 D，对比度软门控让低对比格不进 mask；`FitRigidMotion` 加权 LS + 迭代符号对齐，weight 乘 c_factor，输出 `\|T\|` / `\|ω\|` / `R_global`（全图 2θ 一致性）/ `R_local p10`（切向场判据）/ `MaskRatio`；`ColorForShake(drag_r, R_local, c_factor)` 是 View/CvDebugTool 共用的唯一权威配色函数；判定阈值常量集中在文件顶部，改一处必同步 14 张样本回归。 |
| [AI/PatchHeatmap.cs](PhotoViewer/Core/AI/PatchHeatmap.cs) | DINO patch 诊断图（纯函数） | 消费 1024×384 patch token：`ComputePcaRgb` 经济型 SVD 取前 3 主成分 → 32×32×3；`ComputeRefCosine` 参考点 cosine 映射到 [0,1]。PCA 用 `MathNet.Numerics`。 |
| [AI/HeatmapBitmapBuilder.cs](PhotoViewer/Core/AI/HeatmapBitmapBuilder.cs) | 诊断图渲染 | 把 [0,1] 平面或 RGB 数组渲成 `WriteableBitmap`（viridis / grayscale / raw RGB）；输出 1:1 原尺寸，放大交给 XAML 端 `BitmapInterpolationMode=None`。 |

**Database/** — 照片缓存数据库（`namespace PhotoViewer.Core.Database`）

| File | 模块 | Responsibility |
|---|---|---|
| [Database/PhotoDatabase.cs](PhotoViewer/Core/Database/PhotoDatabase.cs) | 缓存数据库门面 | SQLite (`photos.db`) 静态门面，与 `SettingsService` 共用数据目录。`photos` 表列：`fingerprint`(PK) / `filename_noext` / `capture_time` / `capture_subsec` / `rating`(预留) / `feature_vector` / `feature_model` / `feature_computed_at` / `heatmap`(预留) / `updated_at`。当前 **DINOv3 CLS 向量已启用**（`feature_vector` + `feature_model`，由 `DinoFeatureCache` / `FolderFeatureIndexer` 写入）；`rating` 仍以文件实读为准，`heatmap` / `photo_features` 纵表 / `photo_patches` / `cv_grid` 列**尚未启用**（Plan-1 §A3 冻结后一次性 ALTER）。 |
| [Database/PhotoFingerprint.cs](PhotoViewer/Core/Database/PhotoFingerprint.cs) | 指纹计算 | 三字段规范化 SHA1：`filename_noext` + `DateTimeOriginal`(秒, UTC ISO-8601) + `SubSecTimeOriginal`(3 位毫秒)。同一次曝光的 RAW/HIF/JPG 字节级字段一致 → 同指纹；高速连拍由 SubSec 毫秒区分；文件名编号循环由日期区隔。验证工具见 `ExifTestTool fp <folder>`。 |

**Image/** — 图片解码与文件模型（`namespace PhotoViewer.Core.Image`）

| File | 模块 | Responsibility |
|---|---|---|
| [Image/BitmapLoader.cs](PhotoViewer/Core/Image/BitmapLoader.cs) | 图片加载器 | Decode pipeline + LRU cache + EXIF rotation. |
| [Image/BitmapPrefetcher.cs](PhotoViewer/Core/Image/BitmapPrefetcher.cs) | 预加载器 | Background prefetch of N neighbours around the current image. |
| [Image/HeifLoader.cs](PhotoViewer/Core/Image/HeifLoader.cs) | HEIF 解码桥接 | Static facade. `Initialize(IHeifDecoder)` injects platform decoder. |
| [Image/ImageFile.cs](PhotoViewer/Core/Image/ImageFile.cs) | 文件模型 | Per-file state: path, load status, EXIF cache, thumbnail bitmap. |
| [Image/ImageOrientationInfo.cs](PhotoViewer/Core/Image/ImageOrientationInfo.cs) | 容器方向元数据 | 统一封装 HEIF `Default Rotation` / EXIF `Orientation` + `ExifImageWidth/Height`，给出"显示朝向旋转角 + 水平镜像 + 传感器原始 W/H"。`ThumbnailService` 据此做方向对齐与 letterbox 几何裁剪，无任何启发式。 |
| [Image/JpegDimensionReader.cs](PhotoViewer/Core/Image/JpegDimensionReader.cs) | JPEG SOF 解析 | 字节级解析 JPEG SOF marker (FFC0..FFCF) 直接读真实宽高。HEIF 的 Thumbnail Data 字节嗅探与厂商 Preview 都依赖它，避免再用容器索引贴标签出错。 |
| [Image/ThumbnailService.cs](PhotoViewer/Core/Image/ThumbnailService.cs) | 缩略图服务门面 | `GetAvailableSourcesAsync(file)` 列出来源（EXIF/IFD1 缩略图、厂商 PreviewImage、HEIF 内嵌 JPEG/平台兜底）；`GetThumbnailAsync(file, minShortSide)` 取**显示短边 ≥ target 中最小**的来源解码,随后按 `ImageOrientationInfo` 做方向对齐 + letterbox 几何裁剪。HEIF 字节路径只接 JPEG（用 `JpegDimensionReader` 自读尺寸）,HEVC 字节走平台 `HeifLoader` 兜底（已预旋转）。**不再回退原图全图解码**,所有来源都失败时返回 null 由 UI 显示占位符。 |
| [Image/ThumbnailSource.cs](PhotoViewer/Core/Image/ThumbnailSource.cs) | 缩略图来源 POCO | `Width`/`Height`（字节本身像素，未旋转）/ `Origin`（`ExifEmbedded` / `MakernotePreview` / `HeifEmbedded`）/ `IsPreRotated`（标记该来源是否已是显示朝向，平台 HEIF 解码器为 true，字节直读路径为 false）。 |

**Platform/** — 平台能力抽象（`namespace PhotoViewer.Core.Platform`）

| File | 模块 | Responsibility |
|---|---|---|
| [Platform/PerformanceBudget.cs](PhotoViewer/Core/Platform/PerformanceBudget.cs) | 性能预算 | Static facade. Exposes memory cap, CPU cores, native-preload thread limit. |
| [Platform/ExternalOpenService.cs](PhotoViewer/Core/Platform/ExternalOpenService.cs) | 外部打开服务 | Pending-queue + dispatch for "Open With" / share-to flows. |
| [Platform/StorageAccessManager.cs](PhotoViewer/Core/Platform/StorageAccessManager.cs) | 存储访问门面 | Platform security-scoped access (iOS/macOS sandbox, Android SAF). Long-term retention + transient scopes. |

**Settings/** — 设置持久化（`namespace PhotoViewer.Core.Settings`）

| File | 模块 | Responsibility |
|---|---|---|
| [Settings/SettingsService.cs](PhotoViewer/Core/Settings/SettingsService.cs) | 设置服务 | JSON-serialised config persistence (source-gen via `SettingsJsonContext`). |
| [Settings/SettingsModel.cs](PhotoViewer/Core/Settings/SettingsModel.cs) | 设置模型 | 序列化 POCO，定义所有持久化字段及默认值。 |
| [Settings/NativeSettingsPresenter.cs](PhotoViewer/Core/Settings/NativeSettingsPresenter.cs) | 原生设置展示器 | `INativeSettingsPresenter` 接口 + `TryPresent` 静态调用入口；平台层实现原生弹窗，移动端回退到 Avalonia 模态。 |

**Exif/** — 元数据读写（`namespace PhotoViewer.Core`，Exif 文件沿用根命名空间）

| File | 模块 | Responsibility |
|---|---|---|
| [Exif/ExifLoader.cs](PhotoViewer/Core/Exif/ExifLoader.cs) | 元数据读取 | EXIF/XMP 顶层读取编排；子任务委派给 `ExifMetadataGrouper` / `SonyMakernoteParser`。 |
| [Exif/ExifModels.cs](PhotoViewer/Core/Exif/ExifModels.cs) | 元数据模型 | `ExifData` / `MetadataGroup` / `MetadataTag` POCO。 |
| [Exif/ExifMetadataGrouper.cs](PhotoViewer/Core/Exif/ExifMetadataGrouper.cs) | 分组与翻译 | 展开 MetadataExtractor 目录为按目录分组的可读 tag 列表，应用 `ExifChinese`/`ExifToolTags`/`ExifToolValues` 翻译。 |
| [Exif/ExifChinese.cs](PhotoViewer/Core/Exif/ExifChinese.cs) | 元数据汉化 | Chinese tag-name overrides; generated baseline in `ExifChinese.Generated.cs`. |
| [Exif/ExifToolTags.cs](PhotoViewer/Core/Exif/ExifToolTags.cs) | 标签库 | English tag-name overrides + brand override tables. Generated baseline in `ExifToolTags.Generated.cs`. |
| [Exif/ExifToolValues.cs](PhotoViewer/Core/Exif/ExifToolValues.cs) | 取值翻译 | Enum-style EXIF value translations; generated baseline in `*.Generated.cs`. |
| [Exif/XmpWriter.cs](PhotoViewer/Core/Exif/XmpWriter.cs) | 标星评分写入 | XMP rating writes — in-place edit + sidecar fallback for RAW. |
| [Exif/Sony/SonyMakernoteParser.cs](PhotoViewer/Core/Exif/Sony/SonyMakernoteParser.cs) | Sony MakerNote 解析 | 对焦点位置/对焦框尺寸、LensSpec BCD 解码、加密 tag 调度。 |
| [Exif/Sony/SonyCipherTags.cs](PhotoViewer/Core/Exif/Sony/SonyCipherTags.cs) | Sony 加密 tag 解码 | Decrypt Sony 0x94xx / 0x9050 MakerNote blocks. **Generated table** is in `*.Generated.cs` — do not edit by hand; regenerate via `Tools/generate-sony-cipher-tags.py`. |

**Tools/** — 辅助工具（`namespace PhotoViewer.Core.Tools`）

| File | 模块 | Responsibility |
|---|---|---|
| [Tools/PhotoStatsService.cs](PhotoViewer/Core/Tools/PhotoStatsService.cs) | 照片数据统计服务 | 递归扫描多文件夹，读取等效焦距与星级，导出 CSV。仅 Windows 启用（`OperatingSystem.IsWindows()`）。 |

> **Generated files**: anything ending in `.Generated.cs` is overwritten by `Tools/*.py`. Manual fixes belong in the non-generated companion file's override table. See [DEV.md §五](DEV.md) for the regeneration workflow.

---

## 4. Platform Heads 平台差异化实现

Each head project's `Core/` folder contains platform-specific implementations injected during startup (`Program.cs` / `MainActivity` / `AppDelegate`).

| Capability | Windows | macOS | Android | iOS |
|---|---|---|---|---|
| HEIF decoder | `LibHeifDecoder` | `MacHeifDecoder` | `AndroidHeifDecoder` | `iOSHeifDecoder` |
| Performance budget | `DefaultPerformanceBudget` | `DefaultPerformanceBudget` | `AndroidPerformanceBudget` | `iOSPerformanceBudget` |
| Settings storage | `FileStorage` (default) | `MacSettingsStorage` | `AndroidSettingsStorage` | `iOSSettingsStorage` |
| External open bridge | `Program.Main` arg parsing | `Program.Main` + `MacExternalOpenBridge` | `AndroidExternalOpenBridge` | `AppDelegate` + `iOSExternalOpenBridge` |
| Storage access | n/a | macOS security-scoped bookmark | SAF persisted permissions | iOS security-scoped bookmark |

---

## 5. UI Layer 用户界面逻辑

ViewModels in [PhotoViewer/ViewModels/](PhotoViewer/ViewModels/), Views in [PhotoViewer/Views/](PhotoViewer/Views/).
[MainViewModel.cs](PhotoViewer/ViewModels/Main/MainViewModel.cs) is the root and composes all sub-VMs.

### 5.1 Module map 模块对应

| 模块 | ViewModel | View | Notes |
|---|---|---|---|
| **主窗口 / Main shell** | `MainViewModel` | Desktop: [Windows/MainWindowForWindows.axaml](PhotoViewer/Windows/MainWindowForWindows.axaml), [Windows/MainWindowForMac.axaml](PhotoViewer/Windows/MainWindowForMac.axaml) · Mobile: [Windows/SingleView.axaml](PhotoViewer/Windows/SingleView.axaml) hosting [Views/Main/MainView.axaml](PhotoViewer/Views/Main/MainView.axaml) | Layout switch (grid/list), fullscreen, child-VM wiring. |
| **文件源** | [ViewModels/Main/FolderViewModel.cs](PhotoViewer/ViewModels/Main/FolderViewModel.cs) | (logic only) | 仅负责打开文件/文件夹与维护 `AllFiles`；通过 `AllFilesChanged` / `ScrollToCurrentRequested` / `PriorityThumbnailRequested` 事件通知文件栏。 |
| **文件栏-容器** | [ViewModels/Main/File/FileViewModel.cs](PhotoViewer/ViewModels/Main/File/FileViewModel.cs) | [Views/Main/File/FileView.axaml](PhotoViewer/Views/Main/File/FileView.axaml) | 三分区容器：筛选条 + 主缩略图列表 + 相似聚类面板。单层 `Grid` 按 `IsRowLayout` 切换列定义（行布局:两列 *，筛选条跨两列；列布局:主列 116px + 聚类列 Auto，筛选条仅占主列）。聚类面板 `IsVisible` 绑定 `IsSimilarityPanelOpen`，默认折叠。 |
| **文件栏-筛选** | [ViewModels/Main/File/FilterBarViewModel.cs](PhotoViewer/ViewModels/Main/File/FilterBarViewModel.cs) | [Views/Main/File/FilterBarView.axaml](PhotoViewer/Views/Main/File/FilterBarView.axaml) | 排序方式 / 方向 / 星级筛选 + 计数 + 相似聚类 toggle。变化通过 `FilterChanged` / `SortChanged` / `SimilarityPanelToggled` 事件通知下游。`IsSimilarityPanelOpen` 直通 `SettingsModel.SimilarityPanelExpanded`（跨会话保留）。`StackPanel` 名为 `FilterBarPanel`，平台标题栏代码靠它定位筛选条边界。 |
| **文件栏-主缩略图列表** | [ViewModels/Main/File/ThumbnailListViewModel.cs](PhotoViewer/ViewModels/Main/File/ThumbnailListViewModel.cs) | [Views/Main/File/ThumbnailListView.axaml](PhotoViewer/Views/Main/File/ThumbnailListView.axaml) | 维护 `FilteredFiles`、缩略图加载队列、可见区域滚动+动画；位图预取由内置的 `BitmapPrefetcher` 调度。卡片渲染已抽到共享 `ThumbnailCard` 控件（与相似聚类列表复用）。 |
| **文件栏-相似聚类列表** | [ViewModels/Main/File/SimilarityPanelViewModel.cs](PhotoViewer/ViewModels/Main/File/SimilarityPanelViewModel.cs) | [Views/Main/File/SimilarityListView.axaml](PhotoViewer/Views/Main/File/SimilarityListView.axaml) | 基于 DINOv3 CLS 的真实相似聚类（`Core/AI/SimilarityService`）。三态 UI（Empty / Partial / Full）+ "提取全部/补齐全部" 按钮原地变进度条；`FolderFeatureIndexer` 驱动批量索引。面板展开时 `OnPanelOpened` 触发覆盖度评估；点击相似项通过 `SetCurrentImageKeepAnchor` 切主图但保留原锚点。 |
| **主要图片显示** | [ViewModels/Main/ImageViewModel.cs](PhotoViewer/ViewModels/Main/ImageViewModel.cs) | [Views/Main/ImageView.axaml](PhotoViewer/Views/Main/ImageView.axaml) | Main canvas. Single-image display, zoom/pan gestures, load state. |
| **控制栏** | [ViewModels/Main/ControlViewModel.cs](PhotoViewer/ViewModels/Main/ControlViewModel.cs) | [Views/Main/ControlView.axaml](PhotoViewer/Views/Main/ControlView.axaml) | Toolbar buttons (open, display options, fullscreen). |
| **细节栏** | [ViewModels/Main/DetailViewModel.cs](PhotoViewer/ViewModels/Main/DetailViewModel.cs) | [Views/Main/DetailView.axaml](PhotoViewer/Views/Main/DetailView.axaml) | Sidebar previews — center / four-corner crops via the `DetailPreview` control; subscribes to `ExifData` to inject a Sony "对焦点" (focus-point) preview when available. Does not render the full EXIF table. |
| **工具窗口首页 / Tools shell** | [ViewModels/Tools/ToolsViewModel.cs](PhotoViewer/ViewModels/Tools/ToolsViewModel.cs) | [Views/Tools/ToolsView.axaml](PhotoViewer/Views/Tools/ToolsView.axaml) + [Views/Tools/ToolsWindow.axaml](PhotoViewer/Views/Tools/ToolsWindow.axaml) | Shared tool hub for desktop window / mobile modal. Current tools: EXIF 详情、照片数据统计、DINO 诊断。 |
| **EXIF 详情页** | [ViewModels/Tools/ExifDetailViewModel.cs](PhotoViewer/ViewModels/Tools/ExifDetailViewModel.cs) | [Views/Tools/ExifDetailView.axaml](PhotoViewer/Views/Tools/ExifDetailView.axaml) | Tool page hosted inside the shared tools shell. Switches between sibling files of the same shot (RAW / JPG / HEIF) — RAW pinned first, companion files lazy-loaded. |
| **照片数据统计** | [ViewModels/Tools/PhotoStatsViewModel.cs](PhotoViewer/ViewModels/Tools/PhotoStatsViewModel.cs) | [Views/Tools/PhotoStatsView.axaml](PhotoViewer/Views/Tools/PhotoStatsView.axaml) | 选择多文件夹 + 通配符筛选，批量递归扫描，读取等效焦距与星级，导出为 CSV。仅 Windows（依赖 `System.IO.Directory`，`IsPhotoStatsAvailable = OperatingSystem.IsWindows()`）。核心服务：[Core/Tools/PhotoStatsService.cs](PhotoViewer/Core/Tools/PhotoStatsService.cs)。 |
| **DINO 诊断页** | [ViewModels/Tools/DinoDebugViewModel.cs](PhotoViewer/ViewModels/Tools/DinoDebugViewModel.cs) | [Views/Tools/DinoDebugView.axaml](PhotoViewer/Views/Tools/DinoDebugView.axaml) | 对当前图片现算一次 CV 网格 + DINO 双输出，平铺展示锐度热力图 + 抖动矢量场（颜色由 R_local 主导色相 × drag_r 调亮度）+ 加权刚体拟合文本面板（含 \|T\| / \|ω\| / R_global / R_local p10 / 判定标签）+ DINO PCA-RGB + 点击参考点 cosine。结果不入库，切图即重算；仅在工具页显示时联动（`SyncCurrentFile`）。 |
| **设置页** | `SettingsViewModel` (partial across 8 files: `.BitmapCache`, `.ExifDisplay`, `.FileFormats`, `.Hotkeys`, `.ImagePreview`, `.Layout`, `.Persistence`, `.Rating`) | [Views/Settings/](PhotoViewer/Views/Settings/) | Each partial owns one settings category. Add new categories by following the same partial-class pattern. |

### 5.2 Helpers 辅助组件

- [Controls/](PhotoViewer/Controls/): `ThumbnailCard` (主缩略图列表与相似聚类列表共用的 90×138 卡片:缩略图 + 文件名 + 自定义第二行 + 6 星级), `DetailPreview` (sidebar overview), `SortableList` (drag-reorder list for settings), `HotkeyButton` (hotkey capture), `CheckableMenuHeader`, `DeferredNumericTextBox`, `OverlayGlyphText`.
- [Converters/](PhotoViewer/Converters/): `ExifConverters` (aperture / shutter / ISO formatters), `KeyGestureToStringConverter`, `LayoutConverters`.
- [Behaviors/](PhotoViewer/Behaviors/): `HorizontalScrollWheelBehavior` (mouse-wheel → horizontal scroll for filmstrip).

---

## 6. Key Workflows 关键流程

### 6.1 Image Loading Pipeline 图片加载流水线

1. **Trigger**: `MainViewModel.CurrentFile` changes (user switches image).
2. **Dispatch**: `ImageViewModel` observes the change and calls `LoadImageAsync`.
3. **Cache / decode** (`BitmapLoader.GetBitmapAsync`):
   - Check **LRU memory cache** — hit returns immediately.
   - Miss: read stream → format detect (JPG / HEIF) → decode → **apply EXIF rotation** → cache → return.
4. **Display**: `ImageView` re-binds to `Bitmap`.
5. **Prefetch**: `ImageViewModel` notifies `BitmapPrefetcher`, which queues low-priority decodes for N neighbours.

### 6.2 External Open Flow 外部文件打开

1. **Entry**:
   - **Windows**: `Program.Main(args)` parses CLI args → `PublishExternalOpenArgs`.
   - **macOS**: `Program.Main(args)` installs `MacExternalOpenBridge`; `OpenFile / OpenFiles / OpenUrls` capture Finder / Dock / "Open With".
   - **Android**: `MainActivity` captures `Intent` → `AndroidExternalOpenBridge` parses URI.
   - **iOS**: `AppDelegate` `openURL` → `iOSExternalOpenBridge` parses file URL.
2. **Bridge**: every platform funnels through `ExternalOpenService.PublishFile / PublishFiles`. If UI isn't ready, the request lands in a pending queue.
3. **Handle**: `App.axaml.cs` `OnFrameworkInitializationCompleted` registers the handler; `StorageProvider` resolves the URI to `IStorageFile` / `IStorageFolder`.
   - Folder → `FolderViewModel.OpenFolderAsync`.
   - File → `FolderViewModel.OpenImageAsync` — tries to enter the parent folder first; on Apple/Android where parent access is denied, falls back to **single-image mode** so the user can at least see the file.

### 6.3 Rating Sync 评分与元数据同步

1. **Action**: keyboard `1`–`5` or click a star.
2. **Logic**: `MainViewModel.SetRatingAsync`.
3. **Write**: update in-memory `ImageFile`, then `XmpWriter.WriteRatingAsync`:
   - **In-place**: patch the JPG/HEIF XMP block byte-by-byte.
   - **Sidecar**: for RAW, find or create a same-name `.xmp` file.
4. **Refresh**: `FolderViewModel.RefreshFilters` so rating-based filters update live.

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

**Use legitimate means only**:
- If repeated attempts still fail, stop retrying indefinitely — surface the blocker in the conversation.
- **Never** resort to cheating tactics (e.g. suppressing errors, returning mock data in place of real work, etc.).

## 10. Documentation Standards 文档规范

**Comments**:
- Prefix every function with an XML comment (`/// <summary>...</summary>`) describing purpose, parameters, and return value. Add inline comments for non-trivial logic. **Write comments in Chinese.**

**Docs**:
- Proactively read and maintain this `CLAUDE.md`. When a large-scale change or refactor makes existing docs or comments semantically inconsistent with the new code, update those docs/comments. For small changes, do not add new entries.

**Tidiness**:
- Do not litter the code with redundant comments for minor fixes — a concise summary in the conversation is enough. In particular, **never add explanatory/note-like text to UI strings**. Keep code comments systematic and the UI visually clean.

