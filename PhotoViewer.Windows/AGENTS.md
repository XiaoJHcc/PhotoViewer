# PhotoViewer.Windows — WinUI 3 原生壳

Windows 头工程使用 WinUI 3、D3D11 与 DXGI，负责原生界面及平台能力接线；共享工程不依赖 UI 框架。

本文只维护模块职责、工程约束与操作入口。设计规则见 [Design/](../Design/README.md)，架构见 [Design/architecture.md](../Design/architecture.md)；任务过程、验收结果与待办见 [Plans/](../Plans/native-rebuild-windows-p2.md)。

## 代码入口

| 文件/目录 | 职责 |
|---|---|
| `Program.cs`、`App.xaml*`、`Native/WindowsNativeApplication.cs` | 原生启动、应用资源、调度器与平台门面注入。 |
| `Native/WindowsMainWindow.cs`、`WindowsTitleBar.cs`、`WindowsInput.cs` | 主窗口、标题栏、外部打开、命令与输入分发。 |
| `Native/WindowsLayout.cs`、`WindowsWrapPanel.cs` | 消费共享几何与设计令牌，补充 Windows 专属布局。 |
| `Native/WindowsPhotoView.cs`、`WindowsNavigator.cs` | D3D 照片表面、共享视口接线与缩放导航图。 |
| `Native/WindowsFilterBar.cs`、`WindowsFilmstripView.cs`、`WindowsThumbnailCard.cs`、`WindowsControlBar.cs`、`WindowsControlStyles.cs` | 筛选、胶片栏、卡片、EXIF 与评分/命令栏，以及主屏小型控件的四态外观。 |
| `Native/WindowsAnalysisPanel.cs`、`WindowsSimilarityPanel.cs`、`WindowsDiagnostic*.cs`、`WindowsShakeFieldView.cs` | 分析/相似面板与共用诊断瓦片。 |
| `Native/WindowsSettings*.cs`、`WindowsHotkeyRecorder.cs`、`WindowsNativeSettingsPresenter.cs` | 原生设置窗口、工作副本与快捷键录制。 |
| `Native/WindowsTools*.cs`、`WindowsExifToolPage.cs`、`WindowsStatsToolPage.cs`、`WindowsDinoToolPage.cs`、`WindowsNativeToolsPresenter.cs` | 原生工具窗口及 EXIF、统计、DINO 页面。 |
| `Native/WindowsWindowChrome.cs`、`WindowsUiServices.cs`、`WindowsImageAdapter.cs` | 窗口外壳、调度/选择器/资源门面、XAML 位图适配。 |
| `Core/` | 展示编码与解码、libheif、XMP 写回。 |
| `Native/*Acceptance.cs`、`Native/NativeStartupLog.cs` | 隔离验收、部署检查与启动诊断。 |
| `PhotoViewer.Windows.csproj`、`PortableSingleFile.targets`、`publish-win-x64-singlefile.ps1` | 平台依赖、单文件资源与 CRT 打包、发布验收。 |

## 架构边界

- UI 用 C# 构建；根 `App.xaml` 只引导 `XamlControlsResources`。不得删除或绕过它，Fluent 模板和主题资源依赖此入口；`Application.Resources` 只能在 `InitializeComponent()` 后访问。
- 几何与数值优先来自 `PhotoViewer.Shared` 和 `DesignTokens`；不要把尺寸、颜色或设计规则再复制到本模块文档和实现中。
- 缩放、平移、适应与物理 100% 使用共享 `PhotoViewport`。主图走线性 RGBA16F 交换链，不走 XAML 位图适配。
- 分析栏实现 `IAnalysisHost`，消费共享 `AnalysisPresenter`；工具 DINO 页消费 `DinoDiagnosticPresenter`。不得另建分析编排或恢复旧 UI 适配层。细节裁剪使用 `DisplayCrop`，增强状态归 `PhotoLoadSession`。
- 设置使用 `NativeSettingsSession` 工作副本；设置/工具窗口通过平台 presenter 管理生命周期。文件选择器绑定发起窗口自己的 HWND。
- 导航图直接从展示帧以 Skia 降采样并处理方向，不做 D3D 回读。耗时像素处理在后台，`WriteableBitmap` 必须在 UI 线程创建。
- DirectML 即时推理需串行；后台分析期间持有图像租约，避免热替换后访问已归还的像素缓冲。

## 构建与发布约束

- TFM 为 `net10.0-windows10.0.19041.0`，RID 固定 `win-x64`；libheif 原生依赖仅提供该架构。普通输出位于 `bin/<配置>/<TFM>/win-x64/`。
- 非打包自包含配置、手写入口及 DPI 声明由 csproj / `app.manifest` 维护；保留 `PerMonitorV2`，避免系统位图缩放。
- WinAppSDK 自带的 ONNX/DirectML 与项目包同名；保留 `MicrosoftWindowsAppSDKFilesExcluded` 规则，避免托管/原生版本混用。依赖版本集中在 `Directory.Packages.props`。
- DINO 模型由 Windows 头工程显式嵌入，不依赖松散文件；资源名称由 `LogicalName` 固定，不随发布程序集名变化。
- 单文件发布必须全内容解包，且重建清单，不能复用多文件清单；不启用 trim/R2R。`PortableSingleFile.targets` 同时打入固定 `resources.pri` 回退名，以支持 EXE 改名。
- 发布脚本从 Visual Studio 官方 Release x64 Redist 目录取得 CRT，或接受 `-VCRedistDirectory`；禁止系统目录和 `debug_nonredist` 来源，遵守微软再分发许可并原样携带。用户端不运行运行库安装器。
- PowerShell 发布脚本保留 UTF-8 BOM，兼容 Windows PowerShell 5.1。Git Bash 调用 MSBuild 属性使用 `-p:`，不用会被 MSYS 改写的 `/p:`。

## WinUI 注意事项

- 布局回调不改子元素尺寸或创建位图；瓦片尺寸交给 `Measure/Arrange`，尺寸变化后的裁剪等副作用异步处理。
- 主屏小型控件（命令按钮、评分星、相似聚类开关、动作按钮）的常态/悬停/按下/禁用统一走 `WindowsControlStyles`：按元素覆盖模板状态键，禁用不换底色、只降内容不透明度；只设 `Background` 不够，WinUI 模板会在禁用/悬停时回落到主题资源。
- 控制栏换行由 `WindowsWrapPanel.MainLimit` 驱动：主轴上限取滚动视口，能整组换行时不得以滚动代替；单组超长才由 `ScrollViewer` 兜底。
- 导航图白框可拖动：`WindowsNavigator.PressAt/DragTo` 换算归一化点，交 `WindowsPhotoView.CenterOnNormalizedPoint`；滚轮仍冒泡给主图缩放。
- `Border` / `ScrollViewer` 是 sealed；自定义容器使用 `Panel`、`Grid` 等。`Border.Width` 包含边框，选中描边用不占布局的叠加层。
- 虚拟化滚动区禁用 `ScrollViewer.ZoomMode`，避免无限尺寸测量。
- 自绘标题栏覆盖层背景保持 `null`；按钮悬停色通过主题资源键覆盖，避免覆盖层吞掉照片输入。
- 全屏使用 `OverlappedPresenter` 的边框与最大化组合；独立窗口在 `Activate()` 前按目标 DPI 定尺寸，选择器绑定该窗口句柄。
- 设置/工具共用 `WindowsWindowChrome` 和 `WindowsSettingsCard`。卡片使用 WinUI 主题资源；整块内容使用垂直对齐和 Stretch，需要撑满的控件放在 `Content`。
- `NavigationView` 内容岛通过主题键去掉额外边框/圆角；不要使用不存在的 `PaneBackground` 属性。
- 可排序列表直接提供 `UIElement` 行，重排后回写共享集合；不要在 `ContainerContentChanging` 中替换行内容。
- `PhotoViewer.Windows` 命名空间会遮蔽 `Windows.*`；使用文件级 using 或 `global::Windows` 消歧。`DispatcherQueueTimer` 也可能需要完整限定名。
- WinUI 的跨 COM 异常不一定触发 `Application.UnhandledException`；诊断先看 `%LOCALAPPDATA%\PhotoViewer\native-startup.log`，验收模式使用隔离日志。

## 运行与验收

构建、运行和发布统一使用 [DEV.md](../DEV.md) 的 VS Code Tasks。直接运行可追加图片或文件夹路径。

| 参数 | 用途 |
|---|---|
| `--pv-native-smoke` | 主窗口、解码/渲染、模型与交互冒烟；使用隔离设置、数据库和自有素材。 |
| `--pv-native-portable` | 与 smoke 合用，核对实际已加载的 CRT/libheif/ONNX/DirectML 均来自应用解包目录。 |
| `--pv-native-settings`、`--pv-native-tools` | 设置/工具专项；追加 `-hold` 保持窗口供目视。 |

普通验收报告位于工作目录的 `release/native-win-acceptance/`，失败退出码为 1。发布脚本对原名/改名 EXE 在独立目录与空缓存下验收，并在 `release/rebuild-check/windows-singlefile/` 保存绑定产物 SHA256 的 `deployment.json`。

截图与窗口几何测量工具自身必须启用 Per-Monitor-V2；仅移动光标不能代替真实指针事件。自动化结果不能替代真实硬件、色彩或干净系统验收，具体状态只在 `Plans/` 与 `Design/status.md` 记录。
