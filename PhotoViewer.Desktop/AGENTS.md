# PhotoViewer.Desktop — 原生与迁移期旧壳

Windows 头工程。迁移期同时承载两条路径:旧 Avalonia 壳与 **WinUI 3 原生壳**。
方案、里程碑与作者待确认项见 [Plans/native-rebuild-windows-p2.md](../Plans/native-rebuild-windows-p2.md)。

**当前状态**:原生壳已具备**四分区主窗口**(筛选条 / 胶片条 / 主图 / 控制栏),主图走 D3D11 + DXGI 合成交换链,
尺寸、边距与配色按旧 Avalonia 设计对标。设置窗口(W-M5)、分析栏/聚类/工具/增强(P3)尚未原生;
显隐与命令表沿用共享设置。

## 文件清单

| 文件 | 职责 |
|---|---|
| Program.cs | 入口分流(**Debug 默认原生壳**,`--legacy-ui` 退回旧壳;Release 默认旧壳,`--native` 进原生,与 macOS 同一套口径);原生分支**不创建任何 Avalonia 对象**。 |
| Native/WindowsNativeApplication.cs | WinUI `Application`、`Application.Start` + `DispatcherQueueSynchronizationContext`、门面注入顺序、外部打开、窗口生命周期。 |
| Native/WindowsUiServices.cs | `IUiDispatcher`(DispatcherQueue)、同步上下文、`IFilePickerService`(WinRT 选择器 + 窗口句柄绑定)、`IAppResourceProvider`(avares URI → 应用目录文件)。 |
| Native/WindowsLayout.cs | **几何与配色常量唯一来源**:栏宽 116、下拉 90×32、卡片 90×138(缩略图 80)、EXIF 50×40、评分 30×30、命令 40×40、窗口 1200×850(min 560×480)等。harness 反查这里。 |
| Native/WindowsWrapPanel.cs | WinUI 没有 WrapPanel;控制栏三分组"整组换行/换列 + 行内居中"依赖这个自建面板。 |
| Native/WindowsMainWindow.cs | 主窗口:隐藏式标题栏(拖动区避让筛选条)、两向四分区布局、右键菜单、空首屏提示、拖放、键盘/滚轮 → 共享手势表、命令分发、`PhotoLoadSession` 接线。 |
| Native/WindowsFilterBar.cs | 排序方式 / 升降序 / 星级筛选 + 筛选后计数;名称与取值语义与旧壳一致(`FILTER-01`)。 |
| Native/WindowsFilmstripView.cs | 胶片条:`ItemsRepeater` + 卡片池 + 可见区上报 + 350ms 三次缓出定位(`FILM-01/02/04`)。 |
| Native/WindowsThumbnailCard.cs | 卡片:缩略图 / 文件名 / 拍摄时间 / 0-5 星 / 抖动徽标 / 两态描边(`FILM-05`)。 |
| Native/WindowsControlBar.cs | EXIF·评分·命令三个整体分组,整组换行换列居中,评分方向按布局翻转(`TOOLBAR-01`)。 |
| Native/WindowsPhotoView.cs | GPU 照片表面:合成交换链(F16)、EXIF 方向顶点变换、fit/100%/滚轮缩放/拖动平移、视口上报驱动代理与全分辨率。 |
| Native/WindowsInput.cs | Windows 虚拟键/滚轮 → 共享 `AppGesture`;不改持久化字码。 |
| Native/WindowsIcons.cs | 命令 → Segoe Fluent Icons 字形;P3 延期命令返回 null ⇒ 不显示也不消费按键。 |
| Native/WindowsImageAdapter.cs | 共享 `DecodedImage`(8 位缩略图)→ XAML `WriteableBitmap`;主图不走这里。 |
| Core/WindowsPresentationDecoder.cs | `IPresentationImageDecoder`:Skia/LibHeif 取未旋转 8 位 → skcms 转线性 RGBA16F,携带源尺寸与待应用方向。 |
| Core/LibHeifDecoder.cs / Core/WindowsXmpWriter.cs | 旧壳与缩略图路径的 HEIF 解码、XMP 原位字节写。 |

## 构建事实(坑已固化在 csproj,改动前先读注释)

- TFM `net10.0-windows10.0.19041.0`;`RuntimeIdentifier` 固定 `win-x64`(`LibHeif.Native.win-x64` 仅 x64),
  输出目录是 `bin/<cfg>/net10.0-windows10.0.19041.0/win-x64/`,**不是**旧路径 `bin/<cfg>/net10.0-windows/`。
- WinUI 非打包自包含:`UseWinUI` + `WindowsPackageType=None` + `EnableMsixTooling` + `WindowsAppSDKSelfContained`
  + `DISABLE_XAML_GENERATED_MAIN`(入口自写)。
- **UI 全部用 C# 代码构建,不写 `.xaml`**(与 macOS 壳的程序化 AppKit 一致)。
- **WinAppSDK 框架包自带 ONNX Runtime(1.23.x)**,与本工程的 `Microsoft.ML.OnnxRuntime.DirectML`(native 1.24.x)撞名;
  csproj 用 SDK 的 `MicrosoftWindowsAppSDKFilesExcluded` 钩子显式排除框架侧副本,详见 csproj 注释 —— 不要删。
- 版本集中在 `Directory.Packages.props`(`Microsoft.WindowsAppSDK`)。

## WinUI 非 XAML 工程硬约束(实测)

- `Application.Resources` 在构造期不可访问(读/写都抛 `0x8000FFFF`),只能在 `OnLaunched` 之后用。
- **不要**并入 `XamlControlsResources`:非 XAML 工程里构造即抛找不到 `AcrylicBackgroundFillColorDefaultBrush`;
  WinUI 1.8 的控件样式由框架自带。
- 深色外观把 `RequestedTheme = ElementTheme.Dark` 设在**根元素**上;标题栏/系统按钮颜色另需 `AppWindow.TitleBar` 显式设置。
- **`ProgressBar` 不能放进虚拟化卡片**:无论 indeterminate 还是 determinate,进入 `ItemsRepeater` 的项模板都会让布局测量抛
  `E_FAIL(0x80004005)` 整窗崩溃(已二分定位)。卡片加载态改用静态细条。
- `ScrollViewer.ZoomMode` 必须设 `Disabled`,否则虚拟化内容的测量用无限尺寸。
- `Border` / `ScrollViewer` 在 WinUI 3 里是 **sealed**,不能继承;自定义容器只能继承 `Panel`、`ContentControl` 或 `Grid`。
- 在 `namespace PhotoViewer.Desktop.Native` 里写 `Windows.UI.Color` 会被解析成本仓的 `PhotoViewer.Windows`;
  统一 `using Windows.UI;` + 裸 `Color`。
- Git Bash 调 `dotnet publish` 必须用 `-p:Foo=bar`(写 `/p:Foo=bar` 会被 MSYS 改写成路径导致 `MSB1008`);
  PowerShell 脚本传参数组用空格分隔的字符串(逗号数组会被 bash 吞成一个 token)。

## 运行与验收

- 旧壳:`Debug Windows` / `Run Windows`(默认路径,行为与原生重构前一致)。
- 原生壳:`dotnet run --project PhotoViewer.Desktop -c Debug -- --native`(可跟图片路径直接打开)。
- 辅助脚本:`PhotoViewer.Desktop/bin/runcheck.ps1`(启动 + 存活性/窗口 + 抓窗口位图 + 收尾;被 `bin/` 忽略,不进库)。
- 发布:仍走 [DEV.md](../DEV.md) 的 `Publish Windows EXE (Release)`。
