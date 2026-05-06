# PhotoViewer 重构与新功能方案

> 本文件是一次性的实施计划，完成后可删除。
> 范围已收敛：缩略图服务前置化 → 文件管理工具 → 相似聚类 UI；配套最小化 MVVM 清理。

---

## 总体顺序

1. **阶段 1：缩略图服务抽离与扩充**（基础设施，最前置）
2. **阶段 2：缩略图栏 UI 改造**（为聚类腾出右竖排位置，顺便完成 MVVM 拆分）
3. **阶段 3：相似聚类 UI + 服务**
4. **阶段 4：文件管理工具**（拷卡 + 分文件夹，最小可用）

阶段 2 放在阶段 3 之前，因为聚类面板要挂在改造后的容器里。阶段 4 放最后，因为它是独立入口的模态工具，不影响主界面结构。

---

## 阶段 1：`ThumbnailService` 抽离

### 动机
- [ThumbnailExtractor.cs](PhotoViewer/Core/ThumbnailExtractor.cs) 把 120px 宽写死在 `TargetThumbnailWidth`；聚类等下游需要更大尺寸的缩略图（pHash / 结构比对）做不到
- 解码编排散在 [ImageFile.LoadThumbnailAsync](PhotoViewer/Core/ImageFile.cs) 与 `ThumbnailExtractor` 两层之间，缺一个明确的对外门面
- 本阶段只做服务化抽离 + 尺寸参数化，**不动** [FolderViewModel.cs](PhotoViewer/ViewModels/FolderViewModel.cs) 的队列/消费者逻辑（队列搬迁留给阶段 2 的 `ThumbnailListViewModel`）

### 对外接口（仅两个）

新建 [PhotoViewer/Core/Thumbnails/ThumbnailService.cs](PhotoViewer/Core/Thumbnails/ThumbnailService.cs) 静态门面，对外只暴露：

```csharp
/// <summary>列出该文件可用的缩略图来源（含尺寸）。结果按短边升序，便于上层挑选。</summary>
Task<IReadOnlyList<ThumbnailSource>> GetAvailableSourcesAsync(IStorageFile file);

/// <summary>取一个不低于 minShortSide 的缩略图，并缩放到该短边尺寸。
/// 找不到合适来源时回退到原图子采样解码。</summary>
Task<Bitmap?> GetThumbnailAsync(IStorageFile file, int minShortSide);
```

新增 [PhotoViewer/Core/Thumbnails/ThumbnailSource.cs](PhotoViewer/Core/Thumbnails/ThumbnailSource.cs)：
```csharp
public enum ThumbnailOrigin { ExifEmbedded, MakernotePreview, HeifEmbedded, FullImage }
public sealed record ThumbnailSource(int Width, int Height, ThumbnailOrigin Origin);
```

> 故意不引入 `ThumbnailSize` 枚举：尺寸不分桶、不取整，调用方直接给目标短边像素值；`ThumbnailService` 内部先在可用来源里挑最小且 ≥ `minShortSide` 的，再 `DecodeToWidth` 缩到目标。

### `ThumbnailExtractor` 改造
- 拆为两个职责：
  - `EnumerateSourcesAsync(IStorageFile)`：扫描 EXIF / IFD1 / 厂商 preview 目录，返回 `IReadOnlyList<ThumbnailSource>`（不解码，仅读取尺寸 tag），追加一项 `FullImage`（尺寸来源 EXIF 主图 tag，取不到时填 0 表示未知）
  - `LoadFromSourceAsync(IStorageFile, ThumbnailSource, int targetShortSide)`：按来源类型取字节并 `DecodeToWidth` 到目标
- 删除写死的 `TargetThumbnailWidth` 常量
- `IsValidImageData` 保留为内部工具
- HEIF 路径：[HeifLoader.LoadHeifThumbnailAsync](PhotoViewer/Core/HeifLoader.cs) 接 `int targetShortSide` 参数；HEIF 内嵌缩略图作为一种 `ThumbnailOrigin.HeifEmbedded` 来源参与枚举

### `ImageFile` 改造（最小变动）
- `LoadThumbnailAsync` 改为薄包装：`return await ThumbnailService.GetThumbnailAsync(File, 120);`
- 缓存字段不动；`Thumbnail` 属性语义不变（继续是 ~120px 的列表用缩略图）
- 不在本阶段引入"按尺寸分桶缓存"——下游聚类用到时再加，避免无消费者的过度设计

### `FolderViewModel` 改造
- **不动**队列与消费者（阶段 2 一并搬到 `ThumbnailListViewModel`）
- 队列内部调用从 `ThumbnailExtractor.TryLoadEmbeddedAsync + GenerateFromImageAsync` 改为 `ThumbnailService.GetThumbnailAsync(file, 120)` 一行

### 验收
- 编译无警告；缩略图栏视觉与滚动行为零变化
- 用 ExifTestTool 或临时调用验证 `GetAvailableSourcesAsync` 至少能枚举出 EXIF 内嵌缩略图与 FullImage 两条
- `GetThumbnailAsync(file, 480)` 对带 Sony preview 的文件返回 preview 来源（不是回退到全图解码）

---

## 阶段 2：文件栏 UI 改造 + MVVM 清理

> **命名**：这一“三合一”工作区（筛选条 + 主缩略图列表 + 相似聚类列表）统称**文件栏**（`File`），不再用 `FileBar` / `Thumbnail` 字眼。容器对应 `FileViewModel` / `FileView`，目录命名 `ViewModels/File/` 与 `Views/Main/File/`。

### 最终布局

文件栏位于左/右侧（竖向滚动）时：

```
┌────────────────────────────┐
│   筛选/排序工具栏（头部）     │   ← Auto 高度
├──────────────┬─────────────┤
│              │             │
│  主缩略图列表 │  相似聚类列表 │   ← * 高度，上下滚动
│  （现有）     │  （新增）    │
│              │             │
└──────────────┴─────────────┘
```

文件栏位于顶部（横向滚动）时：沿用同一份“上 + 左 + 右”的三分区模板，无需切换方向 —— 当前 `ThumbnailView` 在顶部布局下已是横滚 `ScrollViewer + ItemsControl`，直接搬入 `FileView` 即可工作。筛选条仍居最上方（Auto），主列表与聚类列表并排横滚（* 列宽）。**两种朝向共用同一份 axaml，无需 if/switch、无需方向分支。**

### VM 拆分

| 新 ViewModel | 职责 |
|---|---|
| [PhotoViewer/ViewModels/File/FileViewModel.cs](PhotoViewer/ViewModels/File/FileViewModel.cs) | 容器 VM，仅组合三个子 VM（`FilterBar`、`ThumbnailList`、`SimilarityPanel`），本身几乎无逻辑 |
| [PhotoViewer/ViewModels/File/FilterBarViewModel.cs](PhotoViewer/ViewModels/File/FilterBarViewModel.cs) | 吸走现 `FolderViewModel` 的 `SortMode` / `SortOrder` / `RatingFilters` / `SelectedRatingFilter` / `FilteredCount` / `FolderName` |
| [PhotoViewer/ViewModels/File/ThumbnailListViewModel.cs](PhotoViewer/ViewModels/File/ThumbnailListViewModel.cs) | 吸走 `FilteredFiles` / `ApplyFilter` / `ApplySort` / `ScrollToCurrentRequested` / `ReportVisibleRange` / `SelectImageCommand` |
| `FolderViewModel`（保留） | 只负责文件来源：`OpenFilePickerAsync` / `OpenFolderAsync` / `OpenImageAsync` / `LoadFolderAsync` / `_allFiles` / `LoadAllExifRatingsAsync` / `IsImageFile` |

拆分原则：**`FolderViewModel` 是“文件源”，`ThumbnailListViewModel` 是“展示列表”，`FilterBarViewModel` 是“筛选控件”**。筛选变化由 `FilterBarViewModel` 发出事件，`ThumbnailListViewModel` 订阅后重算 `FilteredFiles`。

`MainViewModel` 新增 `FileViewModel FileVM { get; }`，旧 `FolderVM` 保留（供 `MainViewModel.SetRatingAsync` 等仍需访问 `AllFiles`）。

### View 拆分

| 新 View | 对应 VM |
|---|---|
| [PhotoViewer/Views/Main/File/FileView.axaml](PhotoViewer/Views/Main/File/FileView.axaml) | `FileViewModel` — 三分区容器（Grid：筛选条 / 主列表 / 聚类列表），竖向与横向布局共用 |
| [PhotoViewer/Views/Main/File/FilterBarView.axaml](PhotoViewer/Views/Main/File/FilterBarView.axaml) | `FilterBarViewModel` — 从 [ThumbnailView.axaml](PhotoViewer/Views/Main/ThumbnailView.axaml) 第 88–147 行的 `FilterBarPanel` 迁出 |
| [PhotoViewer/Views/Main/File/ThumbnailListView.axaml](PhotoViewer/Views/Main/File/ThumbnailListView.axaml) | `ThumbnailListViewModel` — 从 [ThumbnailView.axaml](PhotoViewer/Views/Main/ThumbnailView.axaml) 第 150–302 行的 `ScrollViewer + ItemsControl` 迁出 |
| [PhotoViewer/Views/Main/File/SimilarityListView.axaml](PhotoViewer/Views/Main/File/SimilarityListView.axaml) | `SimilarityPanelViewModel` — 空壳先，阶段 3 填充 |

老 `ThumbnailView.axaml` 删除；[MainView.axaml](PhotoViewer/Views/Main/MainView.axaml) 中所有挂载缩略图栏的位置（上 / 左 / 右）`views:ThumbnailView` 全部替换为 `views:FileView`，`DataContext` 改为 `FileVM`。

### 验收
- 所有原交互（选图、滚动、筛选、排序、星级点击）行为不变
- 右竖排先显示空面板（后续填聚类）
- 文件行数目标：`FolderViewModel` ≤ 400 行，`ThumbnailListViewModel` ≤ 300 行

---

## 阶段 3：相似聚类

### Core 服务

| 文件 | 职责 |
|---|---|
| [PhotoViewer/Core/Similarity/SimilarityService.cs](PhotoViewer/Core/Similarity/SimilarityService.cs) | 对外接口：`Task<IReadOnlyList<ImageFile>> FindSimilarAsync(ImageFile current, IReadOnlyList<ImageFile> pool)` |
| [PhotoViewer/Core/Similarity/BurstDetector.cs](PhotoViewer/Core/Similarity/BurstDetector.cs) | v1 检测器：`|PhotoDate 差| < 2s` ∧ 同相机/镜头 |
| （预留） `HdrBracketDetector.cs` / `PerceptualHashDetector.cs` | 后续阶段填充；接口统一 |

策略：
- v1 先只做连拍检测，依赖已有 EXIF 数据（不阻塞阶段 3 发布）
- 检测结果内存缓存（key = `ImageFile`，值 = 相似文件列表），`FolderViewModel` 切换文件夹时清空
- 不引入 DB；pHash 延后做，先让 UI 跑通

### ViewModel

[PhotoViewer/ViewModels/File/SimilarityPanelViewModel.cs](PhotoViewer/ViewModels/File/SimilarityPanelViewModel.cs):
- 订阅 `Main.CurrentFile` 变化 → 异步调 `SimilarityService.FindSimilarAsync`
- 暴露 `IReadOnlyList<ImageFile> SimilarItems`、`bool IsEmpty`（无相似时显示占位文案）
- 点击相似项 → `Main.CurrentFile = item`

### View

[PhotoViewer/Views/Main/File/SimilarityListView.axaml](PhotoViewer/Views/Main/File/SimilarityListView.axaml):
- 竖排 `ItemsControl` + `VirtualizingStackPanel`
- 每个 item 缩略图尺寸小于主列表（`ThumbnailSize.Small` 即可，约 80×80）
- 缩略图加载走 `ThumbnailService.Enqueue(file, ThumbnailSize.Small, priority: true)`
- 空状态显示"无相似照片"

### 验收
- 打开包含连拍的文件夹，切到连拍组任一张时，右竖排列出同组其他张
- 点击切换后右竖排刷新为新的相似组
- 不影响主列表滚动性能

---

## 阶段 4：文件管理工具（最小可用）

### 定位
一个独立的"文件管理"模态入口，内部两个固定工具：**拷卡**（从外部目录复制到 App 管理目录）、**分文件夹**（按规则把当前文件夹内的文件移到子目录）。

不做任意多选 + 任意移动；只做规则化流程，降低 UI 与跨平台权限复杂度。

### Core 服务

| 文件 | 职责 |
|---|---|
| [PhotoViewer/Core/FileOps/FileCopyService.cs](PhotoViewer/Core/FileOps/FileCopyService.cs) | 基础文件 I/O：`CopyAsync(IStorageFile src, IStorageFolder dstFolder, CancellationToken, IProgress<FileOpProgress>)`；`MoveAsync` 同签名 |
| [PhotoViewer/Core/FileOps/CameraImportService.cs](PhotoViewer/Core/FileOps/CameraImportService.cs) | 拷卡：输入源文件夹 + 目标文件夹；枚举图片后调 `FileCopyService.CopyAsync`；支持"跳过已存在"/"覆盖"策略 |
| [PhotoViewer/Core/FileOps/FileOrganizerService.cs](PhotoViewer/Core/FileOps/FileOrganizerService.cs) | 分文件夹：输入规则（v1 只有"星级阈值"）+ 文件集合；生成移动计划 → 逐个 `MoveAsync` |
| [PhotoViewer/Core/FileOps/FileOpProgress.cs](PhotoViewer/Core/FileOps/FileOpProgress.cs) | POCO：`Total`、`Completed`、`CurrentFileName`、`FailedItems` |

iOS 收益：默认目标目录指向 App Documents（`ApplicationData.Current.LocalFolder` 等效路径），拷贝后后续浏览无需 security-scoped bookmark。但**阶段 4 不触碰** `StorageAccessManager`，保持最小改动。

### ViewModel

| 文件 | 职责 |
|---|---|
| [PhotoViewer/ViewModels/Tools/FileManagerViewModel.cs](PhotoViewer/ViewModels/Tools/FileManagerViewModel.cs) | 壳 VM，切换子工具；`SelectedTool` ∈ `{CameraImport, Organize}` |
| [PhotoViewer/ViewModels/Tools/CameraImportViewModel.cs](PhotoViewer/ViewModels/Tools/CameraImportViewModel.cs) | 三步向导：源 / 目标 / 进度 |
| [PhotoViewer/ViewModels/Tools/OrganizeToolViewModel.cs](PhotoViewer/ViewModels/Tools/OrganizeToolViewModel.cs) | 三步向导：规则（星级阈值、目标子目录名）/ 预览（将移动的文件列表）/ 进度 |

### View

| 文件 | 说明 |
|---|---|
| [PhotoViewer/Views/Tools/FileManagerView.axaml](PhotoViewer/Views/Tools/FileManagerView.axaml) | 壳 + Tab 切换两个子工具 |
| [PhotoViewer/Views/Tools/FileManagerWindow.axaml](PhotoViewer/Views/Tools/FileManagerWindow.axaml) | 桌面端窗口容器（参考现有 [ExifDetailWindow.axaml](PhotoViewer/Views/Tools/ExifDetailWindow.axaml)） |
| [PhotoViewer/Views/Tools/CameraImportView.axaml](PhotoViewer/Views/Tools/CameraImportView.axaml) | 子工具视图 |
| [PhotoViewer/Views/Tools/OrganizeToolView.axaml](PhotoViewer/Views/Tools/OrganizeToolView.axaml) | 子工具视图 |

### 入口

- 桌面端：[ControlView.axaml](PhotoViewer/Views/Main/ControlView.axaml) 工具栏新增"文件管理"按钮 → `MainViewModel.OpenFileManagerWindow(parent)`
- 移动端：模态展示，复用 `MainViewModel.ShowModal` 机制（类似现有 EXIF 详情模态）

### 验收
- 拷卡：选外部文件夹 → 选 App 内目标文件夹 → 进度条 → 完成后主界面可直接浏览目标文件夹
- 分文件夹：设阈值"≥4 星" → 目标子目录名"Selected" → 预览列表正确 → 执行后文件确实移动
- 失败项在进度结束时列出，不中断整体流程
- iOS 拷卡到 Documents 后，后续打开该目录无需 security-scoped bookmark 重新授权

---

## 不在本计划内（显式延后）

- 多文件夹合并浏览 / 文件目录树（分区1）
- 本机星级 DB（SQLite + 跨设备合并）
- 感知哈希（pHash）/ HDR 包围检测
- 任意多选 + 任意移动 / 重命名 / 删除
- `StorageAccessManager` 路径简化

这些待本计划全部落地且稳定后另起一份计划。

---

## 跨阶段约束

- 每阶段独立可编译、可发布、可回滚
- 每阶段结束后在真机至少跑一次主流程（按 [CLAUDE.md §9](CLAUDE.md)）
- 所有新公开方法带中文 XML 注释（按 [CLAUDE.md §10](CLAUDE.md)）
- 生成的阶段产物更新到 [CLAUDE.md §3 / §5.1](CLAUDE.md) 对应表格
