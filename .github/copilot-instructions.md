# PhotoViewer 项目速览（按目录/文件）

## 技术栈与平台
- UI：Avalonia 11.3.2（编译绑定）
- 运行时：.NET 9（net9.0）
- 架构：MVVM（Avalonia.ReactiveUI）
- 关键依赖：MetadataExtractor（EXIF）、XmpCore（XMP/星级）、Microsoft.Extensions.DependencyInjection
- 平台：Windows / macOS / iOS(iPadOS) / Android

## 重点目录导航
- `PhotoViewer/Core`：跨平台核心逻辑（缓存、EXIF、HEIF、XMP、内存预算）
- `PhotoViewer/ViewModels`：业务状态与交互逻辑（选片/预取/设置）
- `PhotoViewer.Mac` / `PhotoViewer.Desktop` / `PhotoViewer.Android` / `PhotoViewer.iOS`：平台入口与能力适配
- `PhotoViewer/Windows`：桌面端窗口与入口视图（Windows/Mac/SingleView）
- `PhotoViewer/Views`：UI 视图（Main/Settings）
- `PhotoViewer/Controls` / `PhotoViewer/Converters` / `PhotoViewer/Behaviors`：可复用 UI 组件与交互

## Core（核心文件，较详细）
- `PhotoViewer/Core/BitmapLoader.cs`
  - `GetBitmapAsync`：带缓存与 EXIF 旋转的主加载入口。
  - `PreloadBitmapAsync` / `PreloadBitmapsSequentiallyAsync`：预取接口。
  - `CacheStatusChanged`：缓存状态变化事件（驱动 UI 标记）。
  - `EstimateDecodedSizeAsync` / `EnsureCapacityAsync` / `ReserveForPreloadAsync`：解码内存估算与预留。
  - `CleanupCache` / `ClearCache` / `RemoveFromCache`：LRU 清理与手动清理。
  - `TrimOnMemoryWarning` / `TrimToCurrentRatio`：系统内存告警收缩策略。
  - `LoadBitmapWithExifRotationAsync` / `ApplyExifRotation` / `RotateBitmap`：旋转处理。
  - `IgnoreAlpha` / `ConvertToDesiredFormat`：可选去 Alpha（转 Rgb24）。

- `PhotoViewer/Core/BitmapPrefetcher.cs`
  - `PrefetchAroundCurrent`：当前图前后预取（前/后数量由设置控制）。
  - `PrefetchVisibleCenter`：滚动停止后按中心附近预取。
  - `RunQueuedAsync`：统一串行协调 + 并行度控制，避免抢占主图加载。

- `PhotoViewer/Core/ExifLoader.cs`
  - `LoadExifDataAsync`：完整 EXIF/XMP 读取（含 Rating）。
  - `LoadRatingOnlyAsync`：仅加载星级（快速筛选）。
  - `TryLoadExifThumbnailAsync` / `GenerateThumbnailFromImageAsync`：缩略图提取与生成。
  - `TryGetDimensionsAsync`：快速读尺寸用于内存估算。
  - `GetRotationAngle` / `NeedsHorizontalFlip`：方向计算。

- `PhotoViewer/Core/HeifLoader.cs`
  - `IHeifDecoder`：平台解码接口。
  - `Initialize`：平台启动时注入具体解码器。
  - `IsHeifFile` / `LoadHeifBitmapAsync` / `LoadHeifThumbnailAsync`：HEIF 识别与加载。

- `PhotoViewer/Core/ImageFile.cs`
  - 模型：文件元数据、缩略图、EXIF 状态、缓存状态。
  - `LoadThumbnailAsync`：缩略图加载（优先 HEIF/EXIF）。
  - `LoadExifDataAsync` / `LoadRatingOnlyAsync` / `ForceReloadExifDataAsync`：EXIF 管理。
  - `UpdateCacheStatus` / `ClearThumbnail` / `ClearExifData`：状态维护。

- `PhotoViewer/Core/MemoryBudget.cs`
  - `IMemoryBudget` / `MemoryBudget.Initialize`：平台注入内存预算。
  - `DefaultMemoryBudget`：基于 GC 信息的默认实现。

- `PhotoViewer/Core/XmpWriter.cs`
  - `WriteRatingAsync`：写入 XMP 星级（可启安全模式）。
  - 平台分支：`WriteRatingAndroidAsync` / `WriteRatingDesktopAsync`（Android 额外校验/恢复）。
  - 关键逻辑：定位 XMP Rating 字节并原位修改，带备份验证。

## ViewModels（核心文件，较详细）
- `PhotoViewer/ViewModels/MainViewModel.cs`
  - 组合：`FolderVM` / `ImageVM` / `ControlVM` / `Settings`。
  - `CurrentFile`：当前图切换与预取触发。
  - `UpdateLayoutFromSettings` / `UpdateScreenOrientation`：布局联动。
  - `OpenSettingWindow` / `OpenSettingModal`：设置窗口/模态。
  - `SetRatingAsync`：星级写入 + 隐藏同名文件同步 + EXIF 刷新。

- `PhotoViewer/ViewModels/ImageViewModel.cs`
  - `LoadImageAsync` / `ClearImage`：主图加载与清空。
  - 缩放/平移状态：`Scale` / `Translate` / `Fit` / `FitScale`。
  - `UpdateZoomIndicator` / `UpdateZoomViewportFrame`：缩放指示器计算。

- `PhotoViewer/ViewModels/FolderViewModel.cs`
  - 文件集与筛选：`AllFiles` / `FilteredFiles`，排序/筛选联动。
  - `OpenFilePickerAsync` / `LoadNewImageFolder`：加载文件夹或文件入口（桌面/移动分支）。
  - 缩略图队列：并发控制 + 可见区域触发加载。
  - `PreloadNearbyFiles`：与 `BitmapPrefetcher` 协作。
  - 缓存状态监听：`BitmapLoader.CacheStatusChanged`。

- `PhotoViewer/ViewModels/SettingsViewModel.*`
  - 分模块设置：布局/热键/EXIF/格式/评分/缓存等。

## 平台与多平台适配（简要）
- `PhotoViewer.Mac`：macOS 入口与平台实现（含 `Core/MacHeifDecoder.cs`）。
- `PhotoViewer.Desktop`：桌面端启动与生命周期管理。
- `PhotoViewer.Android` / `PhotoViewer.iOS`：移动端入口、权限与平台能力。
- `PhotoViewer/Windows`：桌面端窗口与主入口视图（含 Windows/Mac/SingleView）。

## UI/控件/转换器（简要）
- `PhotoViewer/Views/Main`：主界面（浏览、缩略图、控制面板）。
- `PhotoViewer/Views/Main/DetailView.axaml`：细节预览栏，显示当前图片的多处局部。
- `PhotoViewer/Views/Settings`：设置界面模块化页面。
- `PhotoViewer/Controls`：可复用控件（快捷键按钮、可排序列表、细节预览组件）。
- `PhotoViewer/Controls/DetailPreview.axaml`：正方形细节预览组件（标签 + 局部裁切）。
- `PhotoViewer/Controls/CheckableMenuHeader.axaml`：菜单项头部（左文右图标）的可复用控件。
- `PhotoViewer/Converters`：键位/布局/EXIF 显示等转换器。
- `PhotoViewer/Behaviors`：交互行为（如滚轮横向滚动）。

**如果涉及新增文件，或者对现有文件进行较大改动，需要在此文件(.github/copilot-instructions.md)中进行更新，但避免冗长。**
