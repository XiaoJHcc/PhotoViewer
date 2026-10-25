# PhotoViewer.Windows — WinUI 3 原生壳

Windows 头工程使用 WinUI 3、D3D11 与 DXGI，负责原生界面及平台能力接线；共享工程不依赖 UI 框架。

本文只维护模块职责、工程约束与操作入口。设计规则见 [Design/](../Design/README.md)，架构见 [Design/architecture.md](../Design/architecture.md)；任务过程、验收结果与待办见 [Plans/](../Plans/native-rebuild-windows-p2.md)。

## 代码入口

| 文件/目录 | 职责 |
|---|---|
| `Program.cs`、`App.xaml*`、`Native/WindowsNativeApplication.cs` | 原生启动、应用资源、调度器与平台门面注入；设置用 `FileSettingsStore`（schemaVersion 2，原子替换；验收/压测走隔离目录），本地存储引用统一 `LocalFileRef`/`LocalFolderRef`。 |
| `Native/WindowsMainWindow.cs`、`WindowsTitleBar.cs`、`WindowsInput.cs`、`WindowsKeyDispatch.cs` | 主窗口、标题栏、外部打开（拖入经 `WindowsOpenPaths` 投递共享打开通道，选择器直接进共享文件源）、命令与输入分发（`WindowsInput` 做虚拟键/滚轮映射，`WindowsKeyDispatch` 判定消费与重复设闸）。 |
| `Native/WindowsOpenPaths.cs`、`WindowsSingleInstance.cs` | 命令行、文件激活与二次启动路径 → `LocalFileRef`/`LocalFolderRef`；`AppInstance` 单实例把二次激活转发到主实例的 `OpenRequestChannel`。隔离验收/压测不注册单实例键，避免与用户实例互相转发。 |
| `Native/WindowsLayout.cs`、`WindowsWrapPanel.cs` | 消费共享几何与设计令牌，补充 Windows 专属布局。 |
| `Native/WindowsPhotoView.cs`、`WindowsNavigator.cs` | D3D 照片表面、共享视口接线与缩放导航图。 |
| `Native/WindowsFilterBar.cs`、`WindowsFilmstripView.cs`、`WindowsThumbnailCard.cs`、`WindowsControlBar.cs`、`WindowsControlStyles.cs` | 筛选、胶片栏、卡片、EXIF 与评分/命令栏、评分冲突警示三角，以及主屏小型控件的四态外观。 |
| `Native/WindowsAnalysisPanel.cs`、`WindowsSimilarityPanel.cs`、`WindowsDiagnostic*.cs`、`WindowsShakeFieldView.cs` | 分析/相似面板与共用诊断瓦片。 |
| `Native/WindowsSettings*.cs`、`WindowsHotkeyRecorder.cs`、`WindowsNativeSettingsPresenter.cs` | 原生设置窗口、工作副本与快捷键录制。 |
| `Native/WindowsTools*.cs`、`WindowsExifToolPage.cs`、`WindowsStatsToolPage.cs`、`WindowsDinoToolPage.cs`、`WindowsNativeToolsPresenter.cs` | 原生工具窗口及 EXIF、统计、DINO 页面。 |
| `Native/WindowsWindowChrome.cs`、`WindowsUiServices.cs`、`WindowsImageAdapter.cs` | 窗口外壳、调度/选择器/资源门面、XAML 位图适配。 |
| `Core/` | 展示编码与解码（`WindowsPresentationDecoder`：HEIF → 共享 `PhotoViewer.Core.Image.Heif.FastHeifDecoder`，回落 `LibHeifDecoder`；RAW → `WindowsRawDecoder`）、`LibHeifDecoder`（**小尺寸请求**——胶片栏列表档经 `ThumbnailService` 传来的 120/400——优先解容器自带 `thmb` 项并缩到与主图路径相同的尺寸与方向；DINO 的 560 短边仍走主图）、`WindowsRawDecoder`（WIC RAW 全分辨率解码：按相机 `DefaultCropOrigin/Size` 裁剪、方向交给渲染层、内嵌整幅 JPEG 兜底）、`WindowsMemoryBudgetProvider`（物理内存预算 + 进程私有提交 + 系统低内存通知）、`WindowsPerformanceBudget`（设置页默认值：缓存内存与张数按物理内存、后台解码线程按核数推导，物理内存/核数/线程上限沿用 `DefaultPerformanceBudget`）；评分写回不在本目录实现，统一走共享 `RatingService` + `XmpRatingStoreFactory`。 |
| `Native/*Acceptance.cs`、`Native/NativeStartupLog.cs` | 隔离验收、部署检查与启动诊断。 |
| `Native/WindowsPerfProbe.cs`、`WindowsUploadStats.cs`、`WindowsHeifThumbProbe.cs` | 分段性能探针（HIF/ARW/JPG 各 6 张，首张预热，写文本报告）、主图纹理上传的常驻轻量统计（压测汇总行消费）、HEIF 缩略图项与主图缩放两条路径的逐文件对比探针。 |
| `PhotoViewer.Windows.csproj`、`PortableSingleFile.targets`、`publish-win-x64-singlefile.ps1` | 平台依赖、单文件资源与 CRT 打包、发布验收。 |

## 架构边界

- UI 用 C# 构建；根 `App.xaml` 只引导 `XamlControlsResources`。不得删除或绕过它，Fluent 模板和主题资源依赖此入口；`Application.Resources` 只能在 `InitializeComponent()` 后访问。
- 几何与数值优先来自 `PhotoViewer.Shared` 和 `DesignTokens`；不要把尺寸、颜色或设计规则再复制到本模块文档和实现中。
- 缩放、平移、适应与物理 100% 使用共享 `PhotoViewport`。主图走线性 RGBA16F 交换链，不走 XAML 位图适配。
- 主图交换链是**合成交换链**（`CreateSwapChainForComposition`，挂到 `SwapChainPanel`），两条平台事实约束了渲染路径：只给应用一张可渲染缓冲（对其余缓冲建 RTV 返回 `E_INVALIDARG`，`GetCurrentBackBufferIndex` 恒为 0），因此不要写"每帧轮转后台缓冲"；`SetMaximumFrameLatency` 返回 `DXGI_ERROR_INVALID_CALL`，因此不要试图用帧延迟上限抑制积压。距上一次 `Present` 空闲约 0.3 s 后，下一条碰后台缓冲的命令（`OMSetRenderTargets`/清屏/`Present`）会等合成器归还缓冲（实测 260–510 ms，见 [性能专项 §4](../Plans/windows-performance-memory-plan-1.md)），换图那一次解码等待正好落在这个窗口里。
- `D3D11CreateDevice(null, HARDWARE)` 落在"接了窗口输出的适配器"上：本地显示器接独显时设备就在独显；远程/虚拟显示会话里输出挂在核显，设备随之落在核显，桌面合成与解码因此争用同一份系统内存。启动日志会记一条设备适配器与各适配器输出的拓扑（`photo view: 设备适配器=…`），排查合成/呈现类长尾先看它，不要为此加 UI。
- 主图上传按图分流：当前帧与最近两帧的纹理按 `DecodedImage` 实例身份缓存，同一实例直接复用 GPU 纹理、不重传（缓存只持弱引用，不延长像素租约）；另有**一张**同尺寸空纹理（`_spareTexture`，只收 ≤32MB 的小图）给下一次同步上传复用——每张切图新建一张 21MB 的 mip 链纹理时，约 7%–10% 的 `CreateTexture2D` 会在驱动里停 300–500ms，那一段直接进切图延迟，复用后上传段 p95 从 ~350ms 降到 ~5ms；像素数据 ≥32MB 的图把整幅像素交给后台线程逐行写进系统内存暂存纹理（DirectN 是经典 RCW、D3D11 接口无 proxy/stub，后台线程不得调用任何 D3D/COM 对象），UI 线程只建带 mip 的纹理、拷 mip0、生成 mip 链后整体交换，未交换前上一帧继续上屏，同时最多一次在途拷贝，收尾时已不期望的成果进最近纹理缓存而不上屏。纹理占用登记为 `GpuTextures` 内存客户端，压力 Caution 起丢弃非当前帧的缓存纹理与空纹理（清场在 UI 线程）。
- 切图与热替换按 `PhotoLoadSession.Generation` 区分，不能只比源尺寸与方向：缓存命中切图不经过空图，同机型连拍两张的几何完全相同。代际变化一律走 `BeginImageChange`/`ApplyImage`（适应态重新适应、跟随对焦到新照片），代际相同才可能是代理↔Full 热替换；同一实例 + 同一代际的会话通知（加载态、增强态）不重传纹理。大图后台上传仍保持上一帧，收尾交换时按切图语义应用几何。
- 分析栏实现 `IAnalysisHost`，消费共享 `AnalysisPresenter`；工具 DINO 页消费 `DinoDiagnosticPresenter`。不得另建分析编排或恢复旧 UI 适配层。细节裁剪使用 `DisplayCrop`，增强状态归 `PhotoLoadSession`。
- HEIF 快速解码（`HeifContainer` 容器解析、`YuvImageConverter` YUV→展示编码、pvhevc 上下文池与优先级解码闸门、`HeifDecodeStats` 分段耗时统计）位于共享 `PhotoViewer.Core.Image.Heif`；Windows 头只注入原生库与并发参数，不另存一套实现。解码排队按优先级通道（`PhotoViewer.Core.Image.DecodeContext` 的票据定通道）：P0 当前照片越过一切后台请求、可用超出常规上限的 pvhevc 上下文（常规上限仍管后台）；翻页方向前两张预取（Alongside）排普通后台之前；普通后台在 P0 在解时让位（只挡排队，不打断已开跑的图，`PV_HEIF_HOLD_BACKGROUND=0` 关）。两个名额先图像后上下文、上下文全有或全无，故无反向等待死锁；后台起解时瓦片并行度默认收窄到 2 路（`PV_HEIF_BACKGROUND_TILES`，0 = 不收窄），把核让给当前图。这两条环境变量只作压测对照，不进设置页。
- 设置使用 `NativeSettingsSession` 工作副本，关闭时 `ApplyAsync` 一次提交并 `Dispose`；设置/工具窗口通过平台 presenter 管理生命周期。`FileSettingsStore` 落盘用原子替换，`SettingsRepository` 是唯一写者，失败保留 last good。文件选择器绑定发起窗口自己的 HWND。
- 导航图直接从展示帧以 Skia 降采样并处理方向，不做 D3D 回读。耗时像素处理在后台，`WriteableBitmap` 必须在 UI 线程创建。
- DirectML 即时推理需串行；后台分析期间持有图像租约，避免热替换后访问已归还的像素缓冲。

## 构建与发布约束

- TFM 为 `net10.0-windows10.0.19041.0`，RID 固定 `win-x64`；libheif 原生依赖仅提供该架构。普通输出位于 `bin/<配置>/<TFM>/win-x64/`。
- HEIF 快速解码依赖 `NativeLibs/win-x64/pvhevc.dll`：静态链接「仅 HEVC 解码器」LGPL FFmpeg 的最小库，薄 C 封装在 `NativeSrc/pvhevc/`，用 MSYS2 CLANG64 工具链经 `build-native-windows-hevc.ps1` 构建，二进制与许可说明一并入库。csproj 把它和应用本地内容一起带进输出与单文件包；共享解码器不硬编码路径，Windows 头在 `Native/WindowsNativeApplication.InitializeFacades` 用 `FastHeifOptions.LoadLibrary` 注入 `AppContext.BaseDirectory` 下的 `pvhevc.dll`（缺失时交回默认探测）；`--pv-native-portable` 会核对它确实从解包目录加载。
- 非打包自包含配置、手写入口及 DPI 声明由 csproj / `app.manifest` 维护；保留 `PerMonitorV2`，避免系统位图缩放。
- WinAppSDK 自带的 ONNX/DirectML 与项目包同名；保留 `MicrosoftWindowsAppSDKFilesExcluded` 规则，避免托管/原生版本混用。依赖版本集中在 `Directory.Packages.props`。
- DINO 模型由 Windows 头工程显式嵌入，不依赖松散文件；资源名称由 `LogicalName` 固定，不随发布程序集名变化。
- 单文件发布必须全内容解包，且重建清单，不能复用多文件清单；不启用 trim/R2R。`PortableSingleFile.targets` 同时打入固定 `resources.pri` 回退名，以支持 EXE 改名。
- 发布脚本从 Visual Studio 官方 Release x64 Redist 目录取得 CRT，或接受 `-VCRedistDirectory`；禁止系统目录和 `debug_nonredist` 来源，遵守微软再分发许可并原样携带。用户端不运行运行库安装器。
- PowerShell 发布脚本保留 UTF-8 BOM，兼容 Windows PowerShell 5.1。Git Bash 调用 MSBuild 属性使用 `-p:`，不用会被 MSYS 改写的 `/p:`。

## WinUI 注意事项

- 布局回调不改子元素尺寸或创建位图；瓦片尺寸交给 `Measure/Arrange`，尺寸变化后的裁剪等副作用异步处理。
- 主屏小型控件（命令按钮、评分星、相似聚类开关、动作按钮）的常态/悬停/按下/禁用统一走 `WindowsControlStyles`：按元素覆盖模板状态键，禁用不换底色、只降内容不透明度；只设 `Background` 不够，WinUI 模板会在禁用/悬停时回落到主题资源。
- 评分冲突（RATING-04）：组级冲突时卡片与控制栏的 0 号槽位换成警示三角（`WindowsIcons.WarningGlyph` + `WindowsLayout.StarConflict`），按钮几何与命中区不变；三角只作指示，点击不写值、不切主图。控制栏订阅当前文件的 `Rating`/`HasRatingConflict`/`RatingSources`，后台来源读取完成后三角自动刷新。来源浮窗尚未移植到 Windows（macOS 已实现，设计约束见 [RATING-04](../Design/spec/rating.md) 与 [platforms](../Design/platforms.md#rating)），点击三角不打开任何界面。
- 主窗口快捷键挂在根元素 `PreviewKeyDown`（`AddHandler(..., handledEventsToo: true)`），不依赖冒泡焦点路径：未映射/未绑定的按键放行，焦点在文本编辑控件内时整体放行；设置/工具窗口各自独立处理键盘。命令能否执行由共享 `InputCommandMap.IsCommandKnown` 判定，`WindowsIcons` 只负责显示。
- 控制栏换行由 `WindowsWrapPanel.MainLimit` 驱动：主轴上限取滚动视口，能整组换行时不得以滚动代替；单组超长才由 `ScrollViewer` 兜底。
- 导航图白框可拖动：`WindowsNavigator.PressAt/DragTo` 换算归一化点，交 `WindowsPhotoView.CenterOnNormalizedPoint`；滚轮仍冒泡给主图缩放。
- 分析栏细节预览的指针命中统一在 `WindowsAnalysisPanel` 面板层完成：起点在细节预览的触控由该瓦片显式 `CapturePointer`，滑出瓦片或窗口仍能收到 Up；鼠标与可悬停笔悬停点亮，触控按下点亮、滑动切换、滑出清除、抬起/取消/捕获丢失清除；起点在诊断瓦片或面板空白的触控不会滑入点亮，避免抢诊断点按。按指针 id 维护唯一活动目标（按下 > 悬停 > 键盘固定），旧指针结束不清新目标，换图/隐藏面板/窗口失活立即清除。细节瓦片可 Tab 聚焦，Enter/空格切换绿框、Esc 清除，并暴露名称与“绿框已显示”状态；诊断准星独立。
- 缩放比例预设每项只有一个居中的单值文本框（不用步进器）：Enter、失焦、切换分类与关闭设置都提交；>1600、≤0、非数字不写入，用页内 `InfoBar` 提示并恢复原值；合法提交调用 `ApplyScalePreset` 排序并复用行（不重建、不夺焦），落盘归关闭设置时的一次提交。
- `Border` / `ScrollViewer` 是 sealed；自定义容器使用 `Panel`、`Grid` 等。`Border.Width` 包含边框，选中描边用不占布局的叠加层。
- 虚拟化滚动区禁用 `ScrollViewer.ZoomMode`，避免无限尺寸测量。
- `ItemsRepeater` 整体替换 `ItemsSource` 不回收已实现元素，且 `Children` 在 WinUI 3 投影中不可访问；胶片栏换数据源时连同 `ItemsRepeater` 与新的 `StackLayout` 一起重建，解绑旧卡片并还原主轴偏移。
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
| `--pv-native-smoke` | 主窗口、解码/渲染、模型、评分冲突指示、交互冒烟；使用隔离设置、数据库和自有素材。 |
| `--pv-native-portable` | 与 smoke 合用，核对实际已加载的 CRT/libheif/ONNX/DirectML 均来自应用解包目录。 |
| `--pv-native-settings`、`--pv-native-tools` | 设置/工具专项；追加 `-hold` 保持窗口供目视。 |
| `--pv-stress=<文件夹>`（`--pv-stress-count=N`、`--pv-stress-mode=ab`、`--pv-stress-native-threads=N`） | 压测翻页：顺序模式按展示列表连翻，`ab` 模式在相邻两张之间来回切（测"最近两张对比"）。`--pv-stress-native-threads` 覆盖本轮的后台预取并发（隔离设置每轮重置，用于并行度对照实验）。与验收入口同样使用隔离设置与数据库。 |
| `--pv-perf-probe=<文件夹>` | 分段性能探针：HIF/ARW/JPG 各 6 张（首张预热），报告写隔离目录后退出，不进交互。 |
| `--pv-heif-thumb-probe=<文件夹或文件>`（可重复） | HEIF 缩略图项对比探针：逐文件对比"容器 `thmb` 项"与"主图缩放"在同一请求下的尺寸、方向与平均绝对差，并校验 DINO 560 档仍走主图；报告 `heif-thumb-probe.txt`，失败退出码为 1。 |

普通验收、压测与探针的报告位于工作目录的 `release/native-win-acceptance/`（压测日志为 `stress-flip.jsonl`，探针为 `perf-probe.txt`），验收失败退出码为 1。发布脚本对原名/改名 EXE 在独立目录与空缓存下验收，并在 `release/rebuild-check/windows-singlefile/` 保存绑定产物 SHA256 的 `deployment.json`。

截图与窗口几何测量工具自身必须启用 Per-Monitor-V2；仅移动光标不能代替真实指针事件。自动化结果不能替代真实硬件、色彩或干净系统验收，具体状态只在 `Plans/` 与 `Design/status.md` 记录。
