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

- [ ] `PhotoViewer/Core/` 全目录 grep `Avalonia` 零命中(命名空间引用与类型引用均无);
- [ ] ViewModels 全目录无 `Dispatcher.UIThread`、无 `Avalonia.Controls.Window` 直接构造、无 `Avalonia.Media` 笔刷、无 `Avalonia.Input`(快捷键);
- [ ] `PhotoViewer.Desktop` 构建零错误、无新增警告;
- [ ] 人工冒烟(交回用户):打开文件夹、翻图、缩放、评分、设置页、相似聚类、分析栏。
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

### M4 — 像素货币 `DecodedImage` + `ColorEngine` 骨架(最大里程碑,拆三个子提交)
**M4a — 契约与解码器出口**:
- 新增 `Core/Image/DecodedImage.cs`:
  ```csharp
  public enum PixelFormat { Bgra32, Rgb24 }           // 匹配现有解码器实际输出
  public enum NamedColorSpace { Srgb, DisplayP3, AdobeRgb, Unknown }
  public sealed class DecodedImage : IDisposable {
      public byte[] Pixels; public int Width, Height, Stride;
      public PixelFormat Format; public NamedColorSpace ColorSpace; // Phase 0 恒为 Unknown/Srgb,不做转换
      public int ExifOrientation;                                    // 1-8,为 M6 预留,M4 阶段恒 1
  }
  ```
- 新增 `Core/Color/ColorEngine.cs` **骨架**:profile 解析/转换的接口与恒等实现(零行为变化;skcms 真实转换属 Phase 1/2,不在此阶段)。
- 平台解码器(`IHeifDecoder`/`IRawDecoder` 四平台实现)出口从 Avalonia `Bitmap` 改为 `DecodedImage`(原生缓冲直接落 `byte[]`,少一次拷贝也不许——拷贝行为先保持,优化留 Phase 1)。
- JPG 解码:Core 内以 **SkiaSharp `SKCodec`** 替代 Avalonia `Bitmap`/`DecodeToWidth`(`Directory.Packages.props` 增加 SkiaSharp 显式引用;输出格式保持 BGRA32 与现状一致)。
- UI 层适配:`DecodedImageAdapter.ToBitmap(DecodedImage)`(写 `WriteableBitmap`),位于 UI 层。

**M4b — 主链路切换**:`BitmapLoader`/`ThumbnailService`/`ImageFile.Thumbnail` 全部改产 `DecodedImage`;LRU 缓存改存 `DecodedImage`(估算逻辑适配新类型);VM 边界(`ImageViewModel.SourceBitmap` 等)经 adapter 转 `WriteableBitmap` 上屏 —— **VM 公共属性类型不变**(仍 `Avalonia.Bitmap`,VM 像素解耦留待原生壳阶段),View/axaml 零改动。

**M4c — AI/分析链路**:`CvGridExtractor`/`DinoFeatureExtractor`/`AnalysisComputer`/`AnalysisResultCache`/`HistogramRenderer`/`ImageEnhancer` 去 Avalonia 位图依赖,改消费 `DecodedImage` 或原始缓冲;AI 设置页/诊断页经 adapter 显示。

### M5 — EXIF 旋转元数据化
- `DecodedImage.ExifOrientation` 落地:解码路径只读取不应用;`DecodedImageAdapter.ToBitmap` 内做旋转(搬现有 `RotateBitmap` 逻辑进 UI 层适配器);RAW 路径"跳过二次旋转"的现状语义必须保持(`BitmapLoader` RAW 分支注释)。
- 完成后 Core 无任何像素级旋转代码。

### M6 — 收口
- grep 验收门全量执行;构建零警告确认;AGENTS.md(根 + 受影响模块)同步更新;提交。

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
