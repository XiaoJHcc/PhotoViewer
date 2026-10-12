# Phase 0 执行清单 — Core 平台中立化(纯重构,零行为变化)

> 2026-10-19 立项,隶属 [native-rebuild-master-plan-1.md](native-rebuild-master-plan-1.md) §6 Phase 0。
> 分支:`refactor/native-rebuild`。执行方式:mimo coder subagent 按里程碑实施,kimi 审查验收后提交。
>
> **铁律**:
> 1. **零行为变化** —— 不改任何用户可感知行为;不做顺手优化;bug 留记录不修(另开清单)。
> 2. 每个里程碑:`dotnet build PhotoViewer.Desktop` 零错误、无新增警告(基线见 §1)→ 审查 → 单独提交。
> 3. 注释规范:函数 XML 注释 + 中文;不改 UI 字符串。
> 4. 碰到计划外耦合点 → 记录在本文 §5"发现清单",不擅自扩大范围。

---

## 1. 验收门(Phase 0 出口标准)

- [x] `PhotoViewer/Core/` 全目录 grep `Avalonia` 零命中(代码层面;残留均为注释文本)—— M4 达成;
- [x] ViewModels 全目录无 `Dispatcher.UIThread`、无 `Avalonia.Controls.Window` 直接构造、无 `Avalonia.Media` 笔刷、无 `Avalonia.Input`(快捷键)—— M1/M2 达成;唯一豁免:`AnalysisItem.Overlay` 持有 Avalonia `Control` 实例(抖动瓦片),归原生壳阶段处理(§5 已记录);
- [x] `PhotoViewer.Desktop` 构建零错误、无新增警告 —— 每个里程碑均过;Android/Training.sln/Tools 同步验证通过;
- [ ] **人工/跨平台验收(交回用户)**:Mac 构建 `PhotoViewer.Mac` + `PhotoViewer.iOS`(M3/M4 盲改区);四平台人工冒烟(打开文件夹、翻图、缩放、评分、设置页、相似聚类、分析栏);DINO 渲染缓存抽样对比(verify_onnx_parity 口径)。
- [x] Windows 冒烟(2026-10-19 已完成):CLI 外部打开 JPG(城市图,竖图 EXIF 旋转正确)+ HIF(水乡图,LibHeif 链路)均正常显示,胶片条/EXIF/评分栏渲染正常。
- [ ] 物理拆分 `PhotoViewer.Core.csproj`(编译器强制隔离)**不在本阶段** —— 推迟到 Phase 2 首个原生壳开工时(Mac 上验证四头工程)。

## 2. 基线

- 构建命令:`dotnet build PhotoViewer.Desktop/PhotoViewer.Desktop.csproj -v minimal`
- 基线(2026-10-19 记录):EXIT=0,**代码警告 0 条**;仅 NU1903 包警告(SQLitePCLRaw.lib.e_sqlite3 2.1.11 已知漏洞,跨项目重复共 8 行,与本次重构无关,另行升级)。门禁:里程碑结束后不得出现任何新的 CS/AVLN 等代码警告。

## 3. 里程碑

### M1 — UI 调度抽象 `IUiDispatcher`
**范围**:消灭 Core/ 与 ViewModels/ 中全部 `Dispatcher.UIThread` 与 `AvaloniaScheduler` 直引用。
**设计**:
- 新增 `PhotoViewer/Core/Platform/UiDispatcher.cs`:
  ```csharp
  public enum UiPriority { Normal, Background }
  public interface IUiDispatcher {
      bool CheckAccess();
      void Post(Action action, UiPriority priority = UiPriority.Normal);
      Task InvokeAsync(Action action, UiPriority priority = UiPriority.Normal);
      Task<T> InvokeAsync<T>(Func<T> func, UiPriority priority = UiPriority.Normal);
  }
  public static class UiDispatcher { // 静态门面,启动注入
      public static void Initialize(IUiDispatcher impl);
      public static bool CheckAccess();
      public static void Post(...); public static Task InvokeAsync(...); public static Task<T> InvokeAsync<T>(...);
  }
  ```
- UI 层实现 `AvaloniaUiDispatcher`(放 `PhotoViewer/Core/Platform/` 之外 —— 它引用 Avalonia,放 `PhotoViewer/` 根部或 `PhotoViewer/Platform/`),在 `App.axaml.cs` 启动时 `UiDispatcher.Initialize(new AvaloniaUiDispatcher())`。
- `DispatcherPriority.Background` → `UiPriority.Background`(AnalysisViewModel 等用法保持语义)。
- `AvaloniaScheduler.Current`(`SettingsViewModel.BitmapCache.cs:77` 等处)→ `RxApp.MainThreadScheduler`(ReactiveUI 自带,跨平台,`UseReactiveUI()` 自动接线)。
- **不动** Views/ 与 Controls/ 里的 Dispatcher(UI 层随便用)。

### M2 — 窗口门面 + 快捷键模型 + VM 去笔刷
**范围**:VM 不再构造 `Window`;`AppGesture` 去 Avalonia 依赖;VM 输出语义色枚举。
**设计**:
- 新增 `Core/Platform/NativeWindowPresenter.cs` 门面(照抄 `NativeSettingsPresenter` 模式):`INativeWindowPresenter { void ShowSettings(SettingsViewModel vm); void ShowTools(ToolsViewModel vm); }`(签名按现有调用点定),UI 层实现里搬入现有 `new SettingsWindow/ToolsWindow` 代码,`App.axaml.cs` 注册。
- `AppGesture`:把 Avalonia `Key/KeyModifiers` 字段换成 Core 自有 `GestureKey` 枚举(只覆盖现有用到的键)+ `GestureModifiers` flags;Avalonia 映射表放 UI 层(`SettingsViewModel.Hotkeys.cs` 的转换函数挪到 UI 层新文件)。**持久化格式必须逐字节兼容**(设置 JSON 已存在用户机器上,改序列化 = 丢用户配置)。
- `SettingsViewModel.BitmapCache.cs:229-269` 的 `SolidColorBrush` → 语义枚举(如 `PerfStatus { Normal, Warning, Danger }`),颜色映射挪到 View/converter。

### M3 — 存储抽象 `IFileRef` / `IFolderRef`
**范围**:Core 与 VM 不再引用 `Avalonia.Platform.Storage`。
**设计**:
- 新增 `Core/Storage/IFileRef.cs`:
  ```csharp
  public interface IFileRef { string Name; string FullPath; Task<Stream> OpenReadAsync(); Task<Stream> OpenWriteAsync(); }
  public interface IFolderRef { string Name; string FullPath; IAsyncEnumerable<IStorageItemRef> EnumerateAsync(); }
  ```
  成员集合以现有调用点实际用到的为准(先盘点 `IStorageFile`/`IStorageFolder` 全部用法再定接口,禁止拍脑袋加成员)。
- UI 层适配 `AvaloniaFileRef/AvaloniaFolderRef` 直接委托 `IStorageFile/IStorageFolder`(行为零变化的关键:安全作用域书签、SAF 权限维持现状由 Avalonia 承载)。
- 波及:`ImageFile`、`FolderViewModel`、`ExternalOpenService`、`ExifDetailViewModel`、`ImageViewModel`、`App.axaml.cs` 解析逻辑。

### M4 — 像素货币 `DecodedImage`(最大里程碑,一次做完,单个可编译提交)

**实施定稿(2026-10-19,替代原 a/b/c 拆分与 M5)**:
- `Core/Image/DecodedImage.cs`:`byte[] Pixels` + `Width/Height/Stride` + `PixelEncoding { Bgra32, Rgb24 }`(Bgra32=BGRA8888 premul,与 Avalonia ImmutableBitmap 一致)。**不携带 ExifOrientation**。
- **Core 像素基座 = SkiaSharp 3.119.4-preview.1.1**(与 Avalonia 12.0.2 依赖严格同版,Directory.Packages.props 显式固定)。SkiaSharp 是图形库不是 UI 框架,Core 可自由用 `SKCodec/SKBitmap/SKCanvas` 做解码/缩放/旋转/裁剪。
- `ColorEngine` **Phase 0 不做**(零行为变化下恒等骨架是无消费者的死代码),推迟到 Phase 2 与输出表面一起落地 —— 修订 master plan §5 第 1 项的拆分。
- 旋转保持现状时机(解码后、入缓存前),实现从 RenderTargetBitmap 换成 SkiaSharp CPU 变换(90° 倍数无重采样损失);旋转元数据化(顶点变换)留 Phase 2 原生壳。原 M5 取消。
- 适配层:`PhotoViewer/Platform/DecodedImageAdapter.cs`,`ConditionalWeakTable<DecodedImage, WriteableBitmap>` 保证每个 DecodedImage 至多转换一次;VM 公共属性(`SourceBitmap`/`Thumbnail` 等)类型不变,axaml 零改动。
- LRU 缓存改存 `DecodedImage`;`Dispose` 语义退化为释放 byte[] 引用(纯托管,无原生资源),淘汰/Dispose 调用结构保持不变。
- 波及:BitmapLoader(全链)/ HeifLoader / RawLoader / 四头 7 个解码器实现 / ImageViewModel / AnalysisViewModel / DinoDebugViewModel / Core/AI(CvGridExtractor、DinoFeatureExtractor、AnalysisComputer、AnalysisResultCache、HeatmapBitmapBuilder、FolderFeatureIndexer、DinoFeatureCache)/ ImageFile.Thumbnail(缩略图线含在内)/ HistogramRenderer / ImageEnhancer。
- CV/DINO 输入保持"显示朝向像素"(旋转已先于缓存应用),grid 几何语义不变。

### M6 — 收口
- grep 验收门全量执行;构建零警告确认;AGENTS.md(根 + 受影响模块)同步更新;提交。
- **遗留 coupling(原生壳阶段处理,见 §5)**:`AnalysisItem.Overlay`(Control 实例)/ `PointerContext`(Avalonia.Vector)/ `ImageViewModel` 的 `Avalonia.Rect` / VM 公共 Bitmap 属性(适配层出口)。

## 4. 提交纪律

每里程碑一个 commit,信息格式:`P0-M<n>: <摘要>`。计划外发现记入 §5。

## 5. 发现清单(执行中填写)

| 日期 | 发现 | 处置 |
|---|---|---|
| 2026-10-19 | **ReactiveUI 23.x 已删除静态 `RxApp` 类**,`RxSchedulers.MainThreadScheduler` 默认 `DefaultScheduler`(非 UI 线程),不能直接替换 `AvaloniaScheduler.Instance` | `IUiDispatcher` 增加 `MainThreadScheduler`(由 Avalonia 侧返回 `AvaloniaScheduler.Instance`),VM 统一走 `UiDispatcher.MainThreadScheduler`,语义零变化 |
| 2026-10-19 | mimo coder subagent 连续 3 次 provider 超时,M1 由 kimi 直接执行 | 后续里程碑继续优先尝试 mimo;provider 恢复情况逐次观察 |
| 2026-10-19 | `AnalysisItem.Overlay` 持有 Avalonia `Control`(抖动瓦片 ShakeFieldView 实例)— VM 直接持有控件实例 | 不在 M2 范围,保留 `using Avalonia.Controls`;归 M4 诊断瓦片改造时处理 |
| 2026-10-19 | `ControlViewModel.PointerContext` 使用 `Avalonia.Vector`;`ImageViewModel` 用 `Avalonia.Rect` | 几何类型耦合,Phase 0 不处理(原生壳阶段替换为自有向量/矩形类型) |
| 2026-10-19 | M3 起 macOS/iOS 头工程改动无法在 Windows 构建验证(盲改签名+路径提取) | 记入 §1 人工验收项:Mac 上 `dotnet build PhotoViewer.Mac` + `PhotoViewer.iOS` 验证 |
| 2026-10-19 | `AndroidHeifDecoder` 的 ARGB→BGRA 字节序依赖"Android 端 WriteableBitmap 报告 Bgra8888 实为 RGBA 序"的既有口径,M4 按原样搬运(行为不变) | 真实通道序由 Phase 2 色彩管线统一定义;届时全平台核对 |
| 2026-10-19 | 适配层(CWT)下当前显示图存在 `DecodedImage` + `WriteableBitmap` 双份缓冲(仅当前图/可见缩略图),预取项保持单份 | Phase 1 MemoryGovernor 统一记账;Phase 2 原生表面消除双份 |
| 2026-10-19 | DINO 输入缩放(`RenderInputImage`)改用 Skia Linear 采样,口径对齐原 Avalonia DrawImage 默认;DatasetBuilder 已同步(Training 仓单独提交) | 下次提取前跑 `verify_onnx_parity.py` + 抽样对比渲染缓存确认分布未漂移 |
| 2026-10-19 | BitmapLoader 淘汰路径的阻塞式 `GC.Collect ×2` 与 UI 线程 Dispose 在 Phase 0 原样保留(零行为变化) | Phase 1 内存架构(MemoryGovernor)统一拆除 |
| 2026-10-19 | 冒烟观察:`ShakeFlagService group failed: Collection was modified`(文件夹加载期 `_allFiles` 被改写与后台评估并发) | **既有竞态**,与 Phase 0 无关(未改该链路);Phase 1 做快照枚举修复 |
| 2026-10-19 | Training 仓提交时误卷入用户已 staged 的 `docs/HUMAN.md`,已 soft-reset 拆分重提;HUMAN.md 现处于未暂存修改状态(内容未动) | 以后提交 Training 仓先 `git status` 检查既有暂存区 |
