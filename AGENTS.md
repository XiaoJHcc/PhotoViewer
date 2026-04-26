# AGENTS.md — PhotoViewer

> **文档说明**
> 本文档专为 AI Copilot 编写，用于快速理解项目结构与核心逻辑。

## 1. 技术栈与环境

- **框架**: Avalonia 11.3.12, .NET 9
- **模式**: MVVM (ReactiveUI)
- **平台**:
  - `PhotoViewer.Desktop`: Windows (net9.0-windows)
  - `PhotoViewer.Mac`: macOS (net9.0-macos)
  - `PhotoViewer.Android`: Android (net9.0-android)
  - `PhotoViewer.iOS`: iOS (net9.0-ios)
- **依赖**:
  - 图片解码: `LibHeifSharp` (Windows), `ImageIO` (macOS/iOS/Android Native)
  - 依赖注入: 不使用 DI 容器，核心服务为静态类，平台能力通过初始化时注入。

## 2. 项目结构 (Project Structure)

### 2.1 核心通用服务 (`PhotoViewer/Core`)

核心业务逻辑全部位于共享项目的 `Core` 文件夹下，**不依赖 UI 控件**。

| 源文件 | 说明 | 关键职责 |
|---|---|---|
| **BitmapLoader** | 图片加载器 | 图片解码、LRU 缓存管理、EXIF 旋转修正、缩略图生成。 |
| **BitmapPrefetcher** | 预加载器 | 监听当前浏览图片，后台预加载前后邻居图片进入缓存。 |
| **ExifLoader** | 元数据读取 | 读取 EXIF/XMP 信息，快速读取缩略图流。集成 `ExifSonyCipherTags` 解码 Sony 加密 MakerNote。 |
| **ExifSonyCipherTags** | Sony 加密 tag 解码 | 解密 Sony 0x94xx/0x9050 等加密 MakerNote 数据块，按 ExifTool 字段定义解析为可读值。由 `Tools/generate-sony-cipher-tags.py` 从 ExifTool Sony.pm 自动生成字段映射。 |
| **ExifChinese / ExifToolTags** | 元数据汉化与标签库 | 位于 `PhotoViewer/Core/Exif/`，通过 `Tools/*.py` 脚本从 ExifTool 数据中生成的跨平台翻译映射和标签支持，用于翻译各类 Exif 属性为中文。 |
| **HeifLoader** | HEIF 解码桥接 | 静态外观类。通过 `Initialize` 注入平台特定的 `IHeifDecoder` 实现。 |
| **MemoryBudget** | 内存预算 | 静态外观类。管理内存上限，通过 `Initialize` 注入平台特定的 `IMemoryBudget`。 |
| **XmpWriter** | 评分写入 | 修改 XMP 星级评分。支持无损修改（In-place）及备份策略。 |
| **ImageFile** | 文件模型 | 代表磁盘上的一个图片文件。包含路径、加载状态、缓存 Key。 |
| **ExternalOpenService** | 外部打开服务 | 处理“右键打开方式”或“分享到”进入应用的逻辑。 |
| **Settings/** | 设置服务 | `SettingsService` 负责配置的读写与持久化。 |

### 2.2 平台差异化实现 (Platform Core)

各平台项目 (`.Desktop`, `.Android`, etc.) 的 `Core` 文件夹包含具体实现，并在程序启动 (`Program.cs` / `Activity` / `AppDelegate`) 时注入。

| 接口/功能 | Windows Impl | macOS Impl | Android Impl | iOS Impl |
|---|---|---|---|---|
| **HEIF 解码** | `LibHeifDecoder` | `MacHeifDecoder` | `AndroidHeifDecoder` | `iOSHeifDecoder` |
| **内存管理** | `DefaultMemoryBudget` | `DefaultMemoryBudget` | `AndroidMemoryBudget` | `iOSMemoryBudget` |
| **配置存储** | `FileStorage` (默认) | `MacSettingsStorage` | `AndroidSettingsStorage` | `iOSSettingsStorage` |
| **外部打开** | `Program.Main` 参数解析 | `Program.Main` + `MacExternalOpenBridge` | `AndroidExternalOpenBridge` | `AppDelegate` + `iOSExternalOpenBridge` |

### 2.3 用户界面逻辑 (ViewModels & Views)

UI 逻辑位于 `PhotoViewer/ViewModels`，视图位于 `PhotoViewer/Views`。
**MainViewModel** 是根节点，负责组装各子模块。

| 模块 | ViewModel (主要逻辑) | View (界面布局) | 说明                                          |
|---|---|---|---------------------------------------------|
| **主窗口** | `MainViewModel` | `Windows/MainWindow*.axaml` | 负责整体布局（网格/列表模式切换）、全屏状态、子 VM 初始化。            |
| **文件管理** | `FolderViewModel` | N/A (逻辑控制) | 管理当前文件夹路径、`AllFiles` 列表、过滤逻辑、排序逻辑。          |
| **图片浏览** | `ImageViewModel` | `Views/Main/ImageView` | **主视图**。负责单张大图显示、缩放平移手势、加载状态。               |
| **缩略图栏** | `FolderViewModel` (共享) | `Views/Main/ThumbnailView` | **最左侧或顶部边栏**。显示文件带状列表，处理滚动同步与选中高亮。          |
| **顶部/控制栏** | `ControlViewModel` | `Views/Main/ControlView` | **最右侧或底部边栏**。工具栏按钮（打开、显示设置、全屏）。             |
| **详细/侧边栏** | `DetailViewModel` <br/> `ExifDetailViewModel` | `Views/Main/DetailView` | **右侧或底部边栏**。显示直方图、EXIF 信息、概览小图。包含更专门的 Exif 参数解析ViewModel。 |
| **设置页** | `SettingsViewModel` | `Views/Settings/*` | 设置界面。Partial Class 把不同分类（快捷键、格式、布局）拆分到不同文件。 |

### 2.4 辅助组件 (Helpers)

- **Controls/**:
  - `DetailPreview`: 侧边栏使用的图片概览控件。
  - `SortableList`: 支持拖拽排序的列表（用于设置页）。
  - `HotkeyButton`: 快捷键录制按钮。
- **Converters/**: 值转换器（如 `ExifConverters` 格式化光圈快门文本）。

## 3. 关键流程速览 (Workflows)

### 3.1 图片加载流水线 (Image Loading Pipeline)

1. **触发**: 用户切换图片 (`MainViewModel.CurrentFile` 变更)。
2. **调度**: `ImageViewModel` 监听到变更，调用 `LoadImageAsync`。
3. **缓存/解码**:
   - 调用 `BitmapLoader.GetBitmapAsync`。
   - 检查 **LRU 内存缓存**。命中则直接返回。
   - 未命中则读取文件流 -> 识别格式 (JPG/HEIF) -> 解码 -> **应用 EXIF 旋转**。
   - 存入 LRU 缓存并返回。
4. **显示**: `ImageView` 绑定 `Bitmap` 属性刷新界面。
5. **预加载**:
   - 如果启用了预加载，`ImageViewModel` 加载完成后通知 `BitmapPrefetcher`。
   - `BitmapPrefetcher` 依序请求前后 N 张图片的解码任务（低优先级）。

### 3.2 外部文件打开 (External Open Flow)

1. **入口**:
   - **Windows**: `Program.Main(args)` 捕获命令行参数 -> `PublishExternalOpenArgs`.
   - **macOS**: `Program.Main(args)` 安装 `MacExternalOpenBridge`，由 `OpenFile/OpenFiles/OpenUrls` 接收 Finder / Dock / “打开方式” 事件.
   - **Android**: `MainActivity` 捕获 `Intent` -> `AndroidExternalOpenBridge` 解析 Uri.
   - **iOS**: `AppDelegate` 捕获 `openURL` 回调 -> `iOSExternalOpenBridge` 解析文件 URL.
2. **桥接**: 各平台统一调用 `ExternalOpenService.PublishFile(...)` / `PublishFiles(...)` 投递请求；若 UI 尚未完成初始化，请求会先进入挂起队列。
3. **响应**:
   - `App.axaml.cs` 在 `OnFrameworkInitializationCompleted()` 中注册处理器，并通过 `StorageProvider` 将 `Uri` 解析为 `IStorageFile` / `IStorageFolder`。
   - 文件夹请求：调用 `FolderViewModel.OpenFolderAsync(...)`。
   - 文件请求：调用 `FolderViewModel.OpenImageAsync(...)`。
   - `OpenImageAsync(...)` 会优先尝试进入父文件夹；若 Apple / Android 等平台拿不到父目录权限，则自动回退为“单图模式”，保证至少能打开当前图片。

### 3.3 评分与元数据同步 (Rating Sync)

1. **动作**: 用户按键盘 `1-5` 或点击星星。
2. **逻辑**: `MainViewModel.SetRatingAsync`。
3. **写入**:
   - Update 内存中 `ImageFile` 的状态。
   - 调用 `XmpWriter.WriteRatingAsync`。
   - **原地编辑**: 尝试直接修改文件/XMP 头部字节。
   - **Sidecar**: 如果是 RAW 文件，查找或创建同名 `.xmp` 文件写入。
4. **刷新**: 写入完成后，触发过滤列表刷新 (`FolderVM.RefreshFilters`)，确保过滤条件实时生效。

> **保持更新**
> 本文档的内容如果有根本性变化，即现有实现被大幅度重构、文件废弃、功能新增等，导致文档与现有代码语义冲突或重要描述缺失的，请更新此文档 AGENT.md 以保持信息可靠。
> 通常的 BUG 修复和小幅度优化内容则无需新增条目，保持该文档简要精确，只保留重要信息。