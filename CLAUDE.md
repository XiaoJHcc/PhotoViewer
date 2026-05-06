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
ExifTestTool/             # Standalone CLI for EXIF debugging
Tools/                    # Python scripts that regenerate ExifTool-derived tables
release/                  # Output artifacts (DMG, APK, EXE, IPA)
Directory.Build.props     # Single source of truth for version number
Directory.Packages.props  # Central NuGet version pinning
```

---

## 3. Shared Core 核心通用服务 ([PhotoViewer/Core/](PhotoViewer/Core/))

UI-independent business logic. **Do not reference Avalonia controls from this layer.**

| File | 模块 | Responsibility |
|---|---|---|
| [BitmapLoader.cs](PhotoViewer/Core/BitmapLoader.cs) | 图片加载器 | Decode pipeline + LRU cache + EXIF rotation + thumbnails. |
| [BitmapPrefetcher.cs](PhotoViewer/Core/BitmapPrefetcher.cs) | 预加载器 | Background prefetch of N neighbours around the current image. |
| [HeifLoader.cs](PhotoViewer/Core/HeifLoader.cs) | HEIF 解码桥接 | Static facade. `Initialize(IHeifDecoder)` injects platform decoder. |
| [PerformanceBudget.cs](PhotoViewer/Core/PerformanceBudget.cs) | 性能预算 | Static facade. Exposes memory cap, CPU cores, native-preload thread limit. (formerly `MemoryBudget`) |
| [ImageFile.cs](PhotoViewer/Core/ImageFile.cs) | 文件模型 | Per-file state: path, load status, cache key. |
| [ExternalOpenService.cs](PhotoViewer/Core/ExternalOpenService.cs) | 外部打开服务 | Pending-queue + dispatch for "Open With" / share-to flows. |
| [StorageAccessManager.cs](PhotoViewer/Core/StorageAccessManager.cs) | 存储访问门面 | Platform security-scoped access (iOS/macOS sandbox, Android SAF). Long-term retention + transient scopes. |
| [Settings/SettingsService.cs](PhotoViewer/Core/Settings/SettingsService.cs) | 设置服务 | JSON-serialised config persistence (source-gen via `SettingsJsonContext`). |
| [Exif/ExifLoader.cs](PhotoViewer/Core/Exif/ExifLoader.cs) | 元数据读取 | EXIF/XMP 顶层读取编排；子任务委派给 `ExifMetadataGrouper` / `SonyMakernoteParser` / `ExifOrientation`。 |
| [Exif/ExifModels.cs](PhotoViewer/Core/Exif/ExifModels.cs) | 元数据模型 | `ExifData` / `MetadataGroup` / `MetadataTag` POCO。 |
| [Exif/ExifMetadataGrouper.cs](PhotoViewer/Core/Exif/ExifMetadataGrouper.cs) | 分组与翻译 | 展开 MetadataExtractor 目录为按目录分组的可读 tag 列表，应用 `ExifChinese`/`ExifToolTags`/`ExifToolValues` 翻译。 |
| [Exif/ExifOrientation.cs](PhotoViewer/Core/Exif/ExifOrientation.cs) | 方向计算 | 基于 EXIF Orientation 值的旋转角度与水平翻转判断。 |
| [Exif/Sony/SonyMakernoteParser.cs](PhotoViewer/Core/Exif/Sony/SonyMakernoteParser.cs) | Sony MakerNote 解析 | 对焦点位置/对焦框尺寸、LensSpec BCD 解码、加密 tag 调度。 |
| [Exif/Sony/SonyCipherTags.cs](PhotoViewer/Core/Exif/Sony/SonyCipherTags.cs) | Sony 加密 tag 解码 | Decrypt Sony 0x94xx / 0x9050 MakerNote blocks. **Generated table** is in `*.Generated.cs` — do not edit by hand; regenerate via `Tools/generate-sony-cipher-tags.py`. |
| [Thumbnails/ThumbnailService.cs](PhotoViewer/Core/Thumbnails/ThumbnailService.cs) | 缩略图服务门面 | 对外仅两个 API：`GetAvailableSourcesAsync(file)` 列出来源（EXIF/IFD1 缩略图、厂商 PreviewImage、HEIF 内嵌、全图回退）；`GetThumbnailAsync(file, minShortSide)` 取不低于该短边的来源并解码到目标尺寸。HEIF 容器分派给 `HeifLoader`。 |
| [Thumbnails/ThumbnailSource.cs](PhotoViewer/Core/Thumbnails/ThumbnailSource.cs) | 缩略图来源 POCO | `Width`/`Height`/`Origin`（`ExifEmbedded` / `MakernotePreview` / `HeifEmbedded` / `FullImage`）。 |
| [Exif/ExifChinese.cs](PhotoViewer/Core/Exif/ExifChinese.cs) | 元数据汉化 | Chinese tag-name overrides; generated baseline in `ExifChinese.Generated.cs`. |
| [Exif/ExifToolTags.cs](PhotoViewer/Core/Exif/ExifToolTags.cs) | 标签库 | English tag-name overrides + brand override tables. Generated baseline in `ExifToolTags.Generated.cs`. |
| [Exif/ExifToolValues.cs](PhotoViewer/Core/Exif/ExifToolValues.cs) | 取值翻译 | Enum-style EXIF value translations; generated baseline in `*.Generated.cs`. |
| [Exif/XmpWriter.cs](PhotoViewer/Core/Exif/XmpWriter.cs) | 标星评分写入 | XMP rating writes — in-place edit + sidecar fallback for RAW. |

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
[MainViewModel.cs](PhotoViewer/ViewModels/MainViewModel.cs) is the root and composes all sub-VMs.

### 5.1 Module map 模块对应

| 模块 | ViewModel | View | Notes |
|---|---|---|---|
| **主窗口 / Main shell** | `MainViewModel` | Desktop: [Windows/MainWindowForWindows.axaml](PhotoViewer/Windows/MainWindowForWindows.axaml), [Windows/MainWindowForMac.axaml](PhotoViewer/Windows/MainWindowForMac.axaml) · Mobile: [Windows/SingleView.axaml](PhotoViewer/Windows/SingleView.axaml) hosting [Views/Main/MainView.axaml](PhotoViewer/Views/Main/MainView.axaml) | Layout switch (grid/list), fullscreen, child-VM wiring. |
| **文件源** | [FolderViewModel.cs](PhotoViewer/ViewModels/FolderViewModel.cs) | (logic only) | 仅负责打开文件/文件夹与维护 `AllFiles`；通过 `AllFilesChanged` / `ScrollToCurrentRequested` / `PriorityThumbnailRequested` 事件通知文件栏。 |
| **文件栏（File）容器** | [ViewModels/File/FileViewModel.cs](PhotoViewer/ViewModels/File/FileViewModel.cs) | [Views/Main/File/FileView.axaml](PhotoViewer/Views/Main/File/FileView.axaml) | 三分区容器：筛选条 + 主缩略图列表 + 相似聚类面板。竖向（左/右挂载）与横向（顶部挂载）共用同一份 Grid 模板。 |
| **筛选/排序条** | [ViewModels/File/FilterBarViewModel.cs](PhotoViewer/ViewModels/File/FilterBarViewModel.cs) | [Views/Main/File/FilterBarView.axaml](PhotoViewer/Views/Main/File/FilterBarView.axaml) | 排序方式 / 方向 / 星级筛选 + 计数。变化通过 `FilterChanged` / `SortChanged` 事件通知缩略图列表。`StackPanel` 名为 `FilterBarPanel`，平台标题栏代码靠它定位筛选条边界。 |
| **主缩略图列表** | [ViewModels/File/ThumbnailListViewModel.cs](PhotoViewer/ViewModels/File/ThumbnailListViewModel.cs) | [Views/Main/File/ThumbnailListView.axaml](PhotoViewer/Views/Main/File/ThumbnailListView.axaml) | 维护 `FilteredFiles`、缩略图加载队列、可见区域滚动+动画；位图预取由内置的 `BitmapPrefetcher` 调度。 |
| **相似聚类面板** | [ViewModels/File/SimilarityPanelViewModel.cs](PhotoViewer/ViewModels/File/SimilarityPanelViewModel.cs) | [Views/Main/File/SimilarityListView.axaml](PhotoViewer/Views/Main/File/SimilarityListView.axaml) | 阶段 3 占位空壳；阶段 3 接入 `SimilarityService` 后填充。 |
| **主要图片显示** | [ImageViewModel.cs](PhotoViewer/ViewModels/ImageViewModel.cs) | [Views/Main/ImageView.axaml](PhotoViewer/Views/Main/ImageView.axaml) | Main canvas. Single-image display, zoom/pan gestures, load state. |
| **控制栏** | [ControlViewModel.cs](PhotoViewer/ViewModels/ControlViewModel.cs) | [Views/Main/ControlView.axaml](PhotoViewer/Views/Main/ControlView.axaml) | Toolbar buttons (open, display options, fullscreen). |
| **细节栏** | [DetailViewModel.cs](PhotoViewer/ViewModels/DetailViewModel.cs) | [Views/Main/DetailView.axaml](PhotoViewer/Views/Main/DetailView.axaml) | Sidebar previews — center / four-corner crops via the `DetailPreview` control; subscribes to `ExifData` to inject a Sony "对焦点" (focus-point) preview when available. Does not render the full EXIF table. |
| **EXIF 详情页** | [ExifDetailViewModel.cs](PhotoViewer/ViewModels/ExifDetailViewModel.cs) | [Views/Tools/ExifDetailView.axaml](PhotoViewer/Views/Tools/ExifDetailView.axaml) + [Views/Tools/ExifDetailWindow.axaml](PhotoViewer/Views/Tools/ExifDetailWindow.axaml) | Standalone EXIF inspector. Switches between sibling files of the same shot (RAW / JPG / HEIF) — RAW pinned first, companion files lazy-loaded. Instantiated on demand by `MainViewModel` (independent of `DetailViewModel`). |
| **设置页** | `SettingsViewModel` (partial across 8 files: `.BitmapCache`, `.ExifDisplay`, `.FileFormats`, `.Hotkeys`, `.ImagePreview`, `.Layout`, `.Persistence`, `.Rating`) | [Views/Settings/](PhotoViewer/Views/Settings/) | Each partial owns one settings category. Add new categories by following the same partial-class pattern. |

### 5.2 Helpers 辅助组件

- [Controls/](PhotoViewer/Controls/): `DetailPreview` (sidebar overview), `SortableList` (drag-reorder list for settings), `HotkeyButton` (hotkey capture), `CheckableMenuHeader`, `DeferredNumericTextBox`, `OverlayGlyphText`.
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
