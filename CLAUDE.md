# CLAUDE.md — PhotoViewer

> Primary briefing for Claude Code. Loaded automatically at session start.
> Keep concise; update when architecture shifts (rename, split, new module). Do **not** log small bug fixes here.
>
> **文档结构**:本文件仅含项目快照、跨模块流程、平台矩阵、辅助组件、编码/任务/文档规范。模块内的文件清单与职责放在各模块的 `CLAUDE.md`(见 §3 模块索引),改文件时会随目录自动加载。

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

## 3. Module Index 模块索引

| 目录 | 模块手册 | 一句话职责 |
|---|---|---|
| [PhotoViewer/Core/AI/](PhotoViewer/Core/AI/) | [Core/AI/CLAUDE.md](PhotoViewer/Core/AI/CLAUDE.md) | DINOv3 特征提取、CV v5 抖动诊断、相似聚类、批量索引 |
| [PhotoViewer/Core/Database/](PhotoViewer/Core/Database/) | [Core/Database/CLAUDE.md](PhotoViewer/Core/Database/CLAUDE.md) | `photos.db` SQLite 缓存门面 + 指纹计算 |
| [PhotoViewer/Core/Image/](PhotoViewer/Core/Image/) | [Core/Image/CLAUDE.md](PhotoViewer/Core/Image/CLAUDE.md) | 图片解码、LRU 缓存、HEIF 桥接、缩略图服务、文件模型 |
| [PhotoViewer/Core/Exif/](PhotoViewer/Core/Exif/) | [Core/Exif/CLAUDE.md](PhotoViewer/Core/Exif/CLAUDE.md) | EXIF/XMP 读写、汉化标签库、Sony MakerNote 解析 |
| [PhotoViewer/Core/Platform/](PhotoViewer/Core/Platform/) | [Core/Platform/CLAUDE.md](PhotoViewer/Core/Platform/CLAUDE.md) | 性能预算、外部打开服务、存储访问门面(平台能力抽象) |
| [PhotoViewer/Core/Settings/](PhotoViewer/Core/Settings/) | [Core/Settings/CLAUDE.md](PhotoViewer/Core/Settings/CLAUDE.md) | JSON 设置持久化 + 原生设置展示器接口 |
| [PhotoViewer/Core/Tools/](PhotoViewer/Core/Tools/) | [Core/Tools/CLAUDE.md](PhotoViewer/Core/Tools/CLAUDE.md) | 照片数据统计服务(Windows 限定) |
| [PhotoViewer/ViewModels/Main/](PhotoViewer/ViewModels/Main/) | [ViewModels/Main/CLAUDE.md](PhotoViewer/ViewModels/Main/CLAUDE.md) | 主窗口 shell + 文件源/文件栏/图片/控制/分析 VM |
| [PhotoViewer/ViewModels/Tools/](PhotoViewer/ViewModels/Tools/) | [ViewModels/Tools/CLAUDE.md](PhotoViewer/ViewModels/Tools/CLAUDE.md) | 工具壳 + EXIF 详情、照片统计、DINO 诊断 |
| [PhotoViewer/ViewModels/Settings/](PhotoViewer/ViewModels/Settings/) | [ViewModels/Settings/CLAUDE.md](PhotoViewer/ViewModels/Settings/CLAUDE.md) | 设置页 VM(9 个 partial,共享 iOS 原生设置页) |

> Core 层规则:UI-independent business logic,**不引用 Avalonia 控件**。

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

## 5. Key Workflows 跨模块关键流程

> 每个流程跨越多个模块,所以集中在此处而非任一子文档。子文档只描述模块内部行为,跨模块联动以**链接**指回这里。

### 5.1 Image Loading Pipeline 图片加载流水线

1. **Trigger**: `MainViewModel.CurrentFile` changes (user switches image).
2. **Dispatch**: `ImageViewModel` observes the change and calls `LoadImageAsync`.
3. **Cache / decode** (`BitmapLoader.GetBitmapAsync`):
   - Check **LRU memory cache** — hit returns immediately.
   - Miss: read stream → format detect (JPG / HEIF) → decode → **apply EXIF rotation** → cache → return.
4. **Display**: `ImageView` re-binds to `Bitmap`.
5. **Prefetch**: `ImageViewModel` notifies `BitmapPrefetcher`, which queues low-priority decodes for N neighbours.

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
   - **Sidecar**: for RAW, find or create a same-name `.xmp` file.
4. **Refresh**: `FolderViewModel.RefreshFilters` so rating-based filters update live.

### 5.4 AI 特征提取与持久化 DINO/CV Indexing

> 三类原始数据(DINO CLS / DINO patch token / CV grid 7 标量)同源同时入库。派生层(锐度热力图 / 抖动矢量场 / 刚体拟合 / PCA-RGB / 参考点 cosine)全部现算,阈值常量从不入库。详见 [Plans/dinov3-photo-ranking-plan-2-3-persistence.md](Plans/dinov3-photo-ranking-plan-2-3-persistence.md)。

**指纹与 schema**:
- 指纹 = SHA1(`filename_noext` + `DateTimeOriginal` + `SubSecTimeOriginal`),同次曝光的 RAW/HIF/JPG 共享同一指纹(见 [PhotoFingerprint](PhotoViewer/Core/Database/PhotoFingerprint.cs))。
- 三表:`photos`(身份 + `cv_grid` BLOB + `cv_grid_spec` 覆盖式)、`photo_features`(CLS 纵表 `(fingerprint, model_id)`)、`photo_patches`(patch token 纵表同主键)。`model_id` 与 `cv_grid_spec` 不匹配视为 cache miss,改算法 = bump 字符串即整库失效。

**批量提取**(用户点"提取全部 / 补齐全部"按钮):
1. **入口**:[SimilarityPanelViewModel.StartIndexingCommand](PhotoViewer/ViewModels/Main/File/SimilarityPanelViewModel.cs) → `FolderFeatureIndexer.RunAsync`。
2. **聚合**:`GroupByFingerprintAsync` 把 RAW+HIF/JPG 合并为指纹组,代表文件按解码代价升序(HEIF→JPG→其他→RAW)。
3. **缺失评估**:每组先 `EvaluateMissingPartsAsync(fingerprint, modelId, cvSpec)` 拿 `(needCls, needPatch, needCv)` 三元组,全齐备即跳过。
4. **解码两路**:DINO 走 `ThumbnailService` 560 短边、CV 走 `BitmapLoader` 原始分辨率(共享 LRU);桌面端 `ProcessorCount/2` 并发,移动端单线程。
5. **推理与提取**:`_inferSemaphore` 闸内一次 `DinoFeatureExtractor.ExtractDualAsync(includePatches: needPatch)` 同时拿 CLS 与 patch;原图喂 `CvGridExtractor.ExtractAsync` 出 7 标量(含 `block_contrast`)。
6. **写库**:单事务 `PhotoDatabase.WriteIndexedAsync`,任一 blob 为 null 则该项不更新(支持按需补齐)。CLS 同步进 `DinoFeatureCache._memoryCache`。
7. **进度**:按指纹组推进而非按文件;失败组跳过不中断整批,本期不可取消。

**单图懒加载**(相似聚类切图时锚点没入库):
- `DinoFeatureCache.GetOrComputeAsync` → 进程缓存 → DB → 解码缩略图 → ONNX 推理 → 后台 `WriteFeatureAsync`(只写 CLS,不写 patch);`Lazy` 闸门保证同指纹并发只算一次。

**诊断页读库快路径**(DINO 诊断工具页):
- `TryReadCachedAsync` 同时取 patch token 与 cv_grid 7 标量;命中即跳过推理与 CV 计算,不回写库(回写归 indexer 主路径)。
- 派生层(`BuildSharpness` / `BuildShakeField` / `FitRigidMotion` / `ComputePcaRgb` / `ComputeRefCosine`)从原始数据现算 — 改阈值常量立刻见效,不需要重提整库。
- diagonal 仍由 CV 大图 `PixelSize` 推出,所以缓存命中也要 `BitmapLoader` 解码(LRU 通常已被主视图填充)。

**抖动徽标回填**:
- `ShakeFlagService.EvaluateAsync` 由 `MainViewModel` 在 `FolderVM.AllFilesChanged` 时调用、由 `SimilarityPanelViewModel` 在批量索引完成后调用 — 完全不解码、不推理,只读 `cv_grid` + 尺寸算判定 → 回填 `ImageFile.IsShake` → 缩略图卡片渲染徽标。

**清除入口**(开发者用):
- AI 设置页"清除特征数据库"按钮 → 二次确认 → `PhotoDatabase.DeleteDatabaseAsync` 删 `photos.db`/`-wal`/`-shm` 重建空库 → `DinoFeatureCache.InvalidateAll` + `ShakeFlagService.InvalidateAll` 清进程缓存,徽标即时消失。
- 启动时检测旧 schema(残留 `feature_vector` / `heatmap` 列)自动删库重建,无需手动清理。

---

## 6. UI Helpers 共享控件 / 转换器 / 行为

- [Controls/](PhotoViewer/Controls/):
  - `ThumbnailCard` — 主缩略图列表与相似聚类列表共用的 90×138 卡片:缩略图 + 文件名 + 自定义第二行 + 6 星级 + 抖动徽标。徽标在 `File.IsShake == true` 时叠在 80×80 缩略图区右下角。
  - `DetailPreview` — 细节预览/分析栏共用,圆角灰边方框 + 左上药丸标签 + hover/double-tap 联动主图绿框。
  - `DiagnosticTile` — DINO 诊断页与分析栏共用的诊断瓦片,letterbox + AspectRatio + Crosshair + CornerLabel + PlaceholderText,视觉与 DetailPreview 像素级对齐;`SyncSquareLayout` 必须从外尺寸扣 `BorderThickness` 再算 letterbox,否则 1px 边框会被内容覆盖。
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
- 任务收尾前,**必须**回看本次改动是否让任一 `CLAUDE.md`(根或模块)的描述失准 — 文件清单变化、职责变化、跨模块流程改路径、阈值/字段命名调整等。失准必须在**同一任务**内修文档,否则任务**未完成**,与"编译通过"同级。
- 判据:行为表 / 跨模块流程 / 关键字段命名过期 → 必更新;只是改实现细节、小修 bug、加日志 → 不动。

**Use legitimate means only**:
- If repeated attempts still fail, stop retrying indefinitely — surface the blocker in the conversation.
- **Never** resort to cheating tactics (e.g. suppressing errors, returning mock data in place of real work, etc.).

## 10. Documentation Standards 文档规范

**Comments**:
- Prefix every function with an XML comment (`/// <summary>...</summary>`) describing purpose, parameters, and return value. Add inline comments for non-trivial logic. **Write comments in Chinese.**

**Docs**:
- 根 `CLAUDE.md` 与各模块 `CLAUDE.md` 边界:**模块内文件清单与职责** → 子文档;**跨模块流程、平台矩阵、共享 UI 辅助、规范** → 根。子文档点到外部类只用链接,不复述行为,避免两边漂移。
- 文档同步是任务收尾的硬要求,见 §9 "Sync docs in the same task"。

**Tidiness**:
- Do not litter the code with redundant comments for minor fixes — a concise summary in the conversation is enough. In particular, **never add explanatory/note-like text to UI strings**. Keep code comments systematic and the UI visually clean.
