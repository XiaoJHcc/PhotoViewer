# PhotoViewer.Windows — WinUI 3 原生壳

Windows 头工程使用 WinUI 3、D3D11 与 DXGI，负责原生界面及平台能力接线；共享工程不依赖 UI 框架。

本文只维护模块职责、工程约束与操作入口。设计规则见 [Design/](../Design/README.md)，架构见 [Design/architecture.md](../Design/architecture.md)；任务过程、验收结果与待办见 [Plans/](../Plans/native-rebuild-windows-p2.md)。

## 代码入口

| 文件/目录 | 职责 |
|---|---|
| `Program.cs`、`App.xaml*`、`Native/WindowsNativeApplication.cs` | 原生启动、应用资源、调度器与平台门面注入；设置用 `FileSettingsStore`（schemaVersion 2，原子替换；验收/压测走隔离目录），本地存储引用统一 `LocalFileRef`/`LocalFolderRef`。 |
| `Native/WindowsMainWindow.cs`、`WindowsTitleBar.cs`、`WindowsInput.cs`、`WindowsKeyDispatch.cs` | 主窗口与桌面标题栏（悬停显隐、窄宽度常驻回退与布局让位、caption 拖动区、跨度、背景分段与主图区段背板接线、自绘窗口按钮）、外部打开（拖入经 `WindowsOpenPaths` 投递共享打开通道，选择器直接进共享文件源）、命令与输入分发（`WindowsInput` 做虚拟键/滚轮映射，`WindowsKeyDispatch` 判定消费与重复设闸）。 |
| `Native/WindowsOpenPaths.cs`、`WindowsSingleInstance.cs` | 命令行、文件激活与二次启动路径 → `LocalFileRef`/`LocalFolderRef`；`AppInstance` 单实例把二次激活转发到主实例的 `OpenRequestChannel`；转发等待有界，超时只记诊断并退出二次进程，不无限挂起或另开重复主窗口。隔离验收/压测不注册单实例键，避免与用户实例互相转发。 |
| `Native/WindowsLayout.cs`、`WindowsWrapPanel.cs` | 消费共享几何与设计令牌，补充 Windows 专属布局。 |
| `Native/WindowsPhotoView*.cs`、`WindowsNavigator.cs`、`WindowsDisplayColor.cs`、`WindowsDisplayTransform*.cs` | D3D 照片表面、共享视口、标题栏亚克力与导航图。DisplayConfig 按窗口所在显示器探测实际 Advanced Color 模式，开启时由系统管理 scRGB，传统 SDR 按关联显示器 ICC 转换；矩阵/TRC 用 skcms、其余 profile 用 WCS 建宽域 3D LUT，主图在合成后转换。窗口移动与低频轮询更新输出而不重解照片，LUT/中间表面计入 GPU 占用。 |
| `Native/WindowsFilterBar.cs`、`WindowsFilmstripView.cs`、`WindowsThumbnailCard.cs`、`WindowsControlBar.cs`、`WindowsControlStyles.cs` | 筛选、胶片栏、卡片、EXIF 与评分/命令栏、评分冲突警示三角，以及主屏小型控件的四态外观。 |
| `Native/WindowsRatingSourcesFlyout.cs` | RATING-04 原生 `Flyout` 来源浮窗：几何、方向与长名中间省略取共享 `RatingSourcesLayout`，复用共享整组写入；主窗口接入卡片/相似卡片/控制栏锚点与关闭联动。 |
| `Native/WindowsAnalysisPanel.cs`、`WindowsSimilarityPanel.cs`、`WindowsDiagnostic*.cs`、`WindowsShakeFieldView.cs` | 分析/相似面板与共用诊断瓦片。 |
| `Native/WindowsSettings*.cs`、`WindowsHotkeyRecorder.cs`、`WindowsNativeSettingsPresenter.cs` | 原生设置窗口、工作副本与快捷键录制；内存警告文字取 `color.settings.warning` 柔和暗金色，错误仍用系统语义色。 |
| `Native/WindowsTools*.cs`、`WindowsExifToolPage.cs`、`WindowsStatsToolPage.cs`、`WindowsDinoToolPage.cs`、`WindowsNativeToolsPresenter.cs` | 原生工具窗口及 EXIF、统计、分版诊断页面（AI：DINO；标准：CV）。控制栏 EXIF 整组入口走共享 `OpenExifToolsWindow` → `TryPresentExif`，已打开在其他分类时显式 `SelectPage(Exif)`；主图菜单入口只前置保留当前页（TOOLBAR-06）。 |
| `Native/WindowsWindowChrome.cs`、`WindowsUiServices.cs`、`WindowsImageAdapter.cs` | 窗口外壳、调度/选择器/资源门面、XAML 位图适配。 |
| `Core/` | 展示编码与解码（`WindowsPresentationDecoder`：HEIF → 共享 `PhotoViewer.Core.Image.Heif.FastHeifDecoder`，回落 `LibHeifDecoder`；RAW → `WindowsRawDecoder`，其全幅 BGRA 开发源由 `WindowsRawSourceCache` 最多保留一份 3 秒，容量封顶 `min(256MiB, 应用预算/32)`；存储身份及长度/mtime 核对，固定条带闸门串行同文件开发，Caution/恢复观察期不保留，临时 BGRA 源与精缩产物另走准入）、`LibHeifDecoder`（**产物恒为显示朝向**，`OutputPreRotated` 为 true，`ThumbnailService`/`BitmapLoader` 不再二次旋转；**小尺寸请求**——胶片栏列表档经 `ThumbnailService` 传来的 120/400——优先解容器自带 `thmb` 项并缩到与主图路径相同的尺寸与方向；DINO 的 560 短边仍走主图；缩略图出口 `Bgra32Srgb`、主图走 `DecodePresentationAsync` 直接产出线性 RGBA16F 保留广色域。色彩/位深口径本身在共享源 [PhotoViewer.Heif/](../PhotoViewer.Heif/AGENTS.md)：ICC 优先、其次 `nclx`，BT.2020 原色与 BT.709/伽马曲线走同一套 skcms，8 位源走 RGB24、10/12 位源走 `InterleavedRgb48LE` 半浮点码值不先量化，PQ/HLG 拒绝并记原因；本头只保留入口、`thmb`/快速路径策略与产物口径，不再复制一套标签判定与码值归一化）、`WindowsRawDecoder`（WIC RAW 全分辨率解码：按相机 `DefaultCropOrigin/Size` 裁剪、方向交给渲染层、内嵌整幅 JPEG 兜底；**色彩按 WIC 帧级→容器级上下文如实打标签**——有效 ICC 原样携带、只有 EXIF `ColorSpace` 时按共享规则（2 → Adobe RGB）、都没有才显式 sRGB；`ConvertTo(32bppBGRA)` 只换内存布局不做色彩管理，色域交 `ColorEngine`；产物恒 8 位 `Bgra32` 不虚标 F16）、`WindowsMemoryBudgetProvider`（物理内存预算 + 进程私有提交 + 系统低内存通知；另以 `GlobalMemoryStatusEx` 提供系统总量/可用量，供设置页系统内存展示与用户上限超限判定；缓存张数按工作副本的内存上限估算，警告只附简短「（当前设置超过 xx 上限）」，不以应用预算冒充系统内存）、`WindowsPerformanceBudget`（设置页默认值：缓存内存与张数按物理内存、后台解码线程按核数推导，物理内存/核数/线程上限沿用 `DefaultPerformanceBudget`）、`WindowsDisplayColor` + `WindowsDisplayTransform`（按目标显示器 DisplayConfig 实际 `ActiveColorMode` 探测并动态刷新，v1 只认 `Flags & 2`、HDR/WCG 不可区分时白电平按 1 不猜；SDR 从显示器 DC 取 ICC，matrix/TRC 走 `skcms`、复杂 profile 走 WCS；系统管理开启时不重复应用显示器 ICC，HDR 的 SDR 白电平以 `SdrWhiteScale` 单独 pass；GPU 最终 pass 与 XAML 位图（`WindowsImageAdapter`）共用同一输出转换、无双重转换，界面亚克力遮罩与 XAML 接缝同配方同值）；评分写回不在本目录实现，统一走共享 `RatingService` + `XmpRatingStoreFactory`。 |
| `Native/*Acceptance.cs`（含 `WindowsColorAcceptance.cs`）、`Native/NativeStartupLog.cs` | 隔离验收、部署检查与启动诊断。`WindowsColorAcceptance.cs` 是 `--pv-native-color` 的实现：JPEG 四源 proxy/full 的 GPU 输出、HEIF 快速 vs 强制 libheif 代理（F16 保负值/>1）、WIC RAW 实际色彩声明及源分辨率代理的同步/异步 Full 无副本复用、native WCS 黑灰白、skcms matrix/TRC 65³、GPU P3/sRGB/ACM 旁路/HDR SDR 白 ×2 模拟；真屏 ACM/HDR 未切换时该项记 BOUNDARY（不计失败也不折算 PASS）。 |
| `Native/WindowsPerfProbe.cs`、`WindowsRawProbe.cs`、`WindowsUploadStats.cs`、`WindowsHeifThumbProbe.cs` | 分段性能探针（HIF/ARW/JPG 各 6 张，首张预热，写文本报告）；RAW 另以一次 WIC 开发分别生成代理与 Full，记录分段耗时和像素产物字节账，并与产品解码器逐项核对元数据及有效像素字节（不计行距填充）。另测冷代理→紧接 Full 的开发次数与墙钟。主图上传统计区分同步/异步、缓存命中与真实上传，分别记 staging 创建/映射、目标纹理创建、拷贝、mip、后台墙钟与 Render/Present；`WindowsPresentationSignal` 让压测等待目标代际正式图的 Present 返回，独立记录像素就绪，返回不代表物理扫描完成；HEIF 缩略图项与主图缩放有逐文件对比探针。 |
| `PhotoViewer.Windows.csproj`、`Msix/AppxManifest.template.xml`、`PortableSingleFile.targets`、`publish-win-x64-singlefile.ps1` | 平台依赖、按 edition 生成 MSIX 清单与资源、便携单文件和 CRT 打包、双形态发布验收。 |

## 架构边界

- UI 用 C# 构建；根 `App.xaml` 只引导 `XamlControlsResources`。不得删除或绕过它，Fluent 模板和主题资源依赖此入口；`Application.Resources` 只能在 `InitializeComponent()` 后访问。
- 几何与数值优先来自 `PhotoViewer.Shared` 和 `DesignTokens`；不要把尺寸、颜色或设计规则再复制到本模块文档和实现中。
- 缩放、平移、适应与物理 100% 使用共享 `PhotoViewport`。主图走线性 RGBA16F 交换链，不走 XAML 位图适配。
- 主图交换链是**合成交换链**（`CreateSwapChainForComposition`，挂到 `SwapChainPanel`），两条平台事实约束了渲染路径：只给应用一张可渲染缓冲（对其余缓冲建 RTV 返回 `E_INVALIDARG`，`GetCurrentBackBufferIndex` 恒为 0），因此不要写"每帧轮转后台缓冲"；`SetMaximumFrameLatency` 返回 `DXGI_ERROR_INVALID_CALL`，因此不要试图用帧延迟上限抑制积压。距上一次 `Present` 空闲约 0.3 s 后，下一条碰后台缓冲的命令（`OMSetRenderTargets`/清屏/`Present`）会等合成器归还缓冲（实测 260–510 ms，见 [性能专项 §4](../Plans/windows-performance-memory-plan-1.md)），换图那一次解码等待正好落在这个窗口里。
- `D3D11CreateDevice(null, HARDWARE)` 落在"接了窗口输出的适配器"上：本地显示器接独显时设备就在独显；远程/虚拟显示会话里输出挂在核显，设备随之落在核显，桌面合成与解码因此争用同一份系统内存。启动日志会记一条设备适配器与各适配器输出的拓扑（`photo view: 设备适配器=…`），排查合成/呈现类长尾先看它，不要为此加 UI。
- 主图上传按图分流：当前帧与最近两帧的纹理按 `DecodedImage` 实例身份缓存，同一实例直接复用 GPU 纹理、不重传（缓存只持弱引用，不延长像素租约）；另有**一张**同尺寸空纹理（`_spareTexture`，只收 ≤32MB 的小图）给下一次同步上传复用——每张切图新建一张 21MB 的 mip 链纹理时，约 7%–10% 的 `CreateTexture2D` 会在驱动里停 300–500ms，那一段直接进切图延迟，复用后上传段 p95 从 ~350ms 降到 ~5ms；像素数据 ≥32MB 的图把整幅像素交给后台线程逐行写进系统内存暂存纹理（DirectN 是经典 RCW、D3D11 接口无 proxy/stub，后台线程不得调用任何 D3D/COM 对象），UI 线程只建带 mip 的纹理、拷 mip0、生成 mip 链后整体交换，未交换前上一帧继续上屏，同时最多一次在途拷贝，收尾时已不期望的成果进最近纹理缓存而不上屏。纹理占用登记为 `GpuTextures` 内存客户端（含在途 staging 的映射行距字节），压力 Caution 起丢弃非当前帧的缓存纹理与空纹理（清场在 UI 线程）；持续压力与 Normal 恢复观察期内不重新保留非当前纹理，已过期的在途结果不再创建目标纹理。后台拷贝启动前取得源像素租约，拷贝结束归还；源读取失败不得把未写满的 staging 上屏。
- 切图与热替换按 `PhotoLoadSession.Generation` 区分，不能只比源尺寸与方向：缓存命中切图不经过空图，同机型连拍两张的几何完全相同。代际变化一律走 `BeginImageChange`/`ApplyImage`（适应态重新适应、跟随对焦到新照片），代际相同才可能是代理↔Full 热替换；同一实例 + 同一代际的会话通知（加载态、增强态）不重传纹理。大图后台上传仍保持上一帧，收尾交换时按切图语义应用几何。
- 分析栏实现 `IAnalysisHost`，消费共享 `AnalysisPresenter`，独立上报 DINO/CV 即时计算设置；诊断工具页在 AI 版消费 `DinoDiagnosticPresenter`、标准版消费 `CvDiagnosticPresenter`，分别呈现五张/三张瓦片。不得另建分析编排或恢复旧 UI 适配层。细节裁剪使用 `DisplayCrop`，增强状态归 `PhotoLoadSession`。
- HEIF 快速解码（`HeifContainer` 容器解析、`YuvImageConverter` YUV→展示编码、pvhevc 上下文池与优先级解码闸门、`HeifDecodeStats` 分段耗时统计）位于共享 `PhotoViewer.Core.Image.Heif`；Windows 头只注入原生库与并发参数，不另存一套实现。色彩链以 **ICC 优先**（容器带 ICC 时先取 profile 空间码值，再经 `ColorEngine` 转到目标编码），libheif 回落的产物同样按真实声明（ICC 优先，其次 `nclx`）打标签后才转换，主图回落直接出线性 RGBA16F；**位深不降级**：8 位源走原 RGB24 路径，10/12 位源走 libheif 的 16 位交错平面（`InterleavedRgb48LE`）→ 源空间半浮点码值 → 色彩转换，不先量化到 8 位。PQ/HLG 不做 tone map，如实拒绝并记原因。解码排队按优先级通道（`PhotoViewer.Core.Image.DecodeContext` 的票据定通道）：P0 当前照片越过一切后台请求、可用超出常规上限的 pvhevc 上下文（常规上限仍管后台）；翻页方向前两张预取（Alongside）排普通后台之前；普通后台在 P0 在解时让位（只挡排队，不打断已开跑的图，`PV_HEIF_HOLD_BACKGROUND=0` 关）。两个名额先图像后上下文、上下文全有或全无，故无反向等待死锁；后台起解时瓦片并行度默认收窄到 2 路（`PV_HEIF_BACKGROUND_TILES`，0 = 不收窄），把核让给当前图。这两条环境变量只作压测对照，不进设置页。Windows 的在途图像常规上限同时受 `max(1, 应用预算/2GiB)` 收窄，预算不足 4GiB 时空闲上下文留 2 个；像素池空闲上限为 `min(512MiB, 应用预算/32)`。HEIF 主图另按全瓦片平面与 ICC 中间图估算临时准入，后台拒绝不再转更重的 libheif 回落，P0 仍豁免并记账。
- 设置使用 `NativeSettingsSession` 工作副本，关闭时 `ApplyAsync` 一次提交并 `Dispose`；设置/工具窗口通过平台 presenter 管理生命周期。`FileSettingsStore` 落盘用原子替换，`SettingsRepository` 是唯一写者，失败保留 last good。文件选择器绑定发起窗口自己的 HWND。
- 设置变化统一消费共享 `SettingsViewModel.PresentationChanged`（`SettingsChangeImpact` 位），主窗、控制栏与分析栏按位刷新对应结构，不按属性名清单自行判断，也不在壳里复刻影响映射。未登记属性（缓存预算文本、内存探测与诊断通知、未来新增文本）在共享分流里是 `None`，本地整体忽略——周期通知不得重建按钮、关闭弹层或收起下拉。控制栏另保留 `ControlViewModel` 的业务属性窄订阅（`EnabledExifItems`/`EnabledControls`/`StarOpacity`），用于当前照片 EXIF 与命令按钮可用态；快捷键子项与集合排序等真实子项变化由共享发布影响位，壳不再依赖设置快照回填的广播。
- 导航图直接从展示帧以 Skia 降采样并处理方向，不做 D3D 回读。耗时像素处理在后台，`WriteableBitmap` 必须在 UI 线程创建。
- DirectML 即时推理需串行；后台分析期间持有图像租约，避免热替换后访问已归还的像素缓冲。

## 构建与发布约束

- TFM 为 `net10.0-windows10.0.19041.0`，RID 固定 `win-x64`；libheif 原生依赖仅提供该架构。构建、restore 与运行必须显式传 `PhotoViewerEdition=standard|ai`，头工程与共享工程的输出/还原资产按 `standard/ai` 隔离，普通输出位于 `bin/<edition>/<配置>/<TFM>/win-x64/`。标准版不编译 DINO/相似实现或引用 ONNX/MathNet，保留 CV；应用 ID 分别为 `cc.xiaojh.PhotoViewer` / `cc.xiaojh.PhotoViewer.AI`，主发布形态为两个独立签名 MSIX，AUMID、单实例键、激活目标和私有状态各自独立。通用设置与可复用分析数据走显式产品族共享区；显示名为 `PhotoViewer` / `PhotoViewer AI`（便携 EXE 与程序集名仍用 `PhotoViewer-AI`）。
- HEIF 快速解码依赖 `NativeLibs/win-x64/pvhevc.dll`：静态链接「仅 HEVC 解码器」LGPL FFmpeg 的最小库，薄 C 封装在 `NativeSrc/pvhevc/`，用 MSYS2 CLANG64 工具链经 `build-native-windows-hevc.ps1` 构建，二进制与许可说明一并入库。csproj 把它和应用本地内容一起带进输出与单文件包；共享解码器不硬编码路径，Windows 头在 `Native/WindowsNativeApplication.InitializeFacades` 用 `FastHeifOptions.LoadLibrary` 注入 `AppContext.BaseDirectory` 下的 `pvhevc.dll`（缺失时交回默认探测）；`--pv-native-portable` 会核对它确实从解包目录加载。
- 非打包自包含配置、手写入口及 DPI 声明由 csproj / `app.manifest` 维护；保留 `PerMonitorV2`，避免系统位图缩放。
- WinAppSDK 自带的 ONNX/DirectML 与项目包同名；保留 `MicrosoftWindowsAppSDKFilesExcluded` 原生排除规则，并通过 `Microsoft.WindowsAppSDK.ML` 的 `ExcludeAssets=all` 排除 SDK 自带托管包装，避免标准版误带或 AI 版混用。ML 子包版本须与 SDK 元包依赖保持一致，统一维护在 `Directory.Packages.props`。
- AI 版模型由 Windows 头工程显式嵌入，资源名称由 `LogicalName` 固定，不随发布程序集名变化；随包携带 DINOv3 License，发布必须存在固定模型并通过字节数/哈希扫描。标准版不打包模型或许可证。
- 通过 `DisableTransitiveFrameworkReferences=true` 阻止 System.Reactive 的传递 WindowsDesktop 框架引用；只使用平台注入的 `SynchronizationContextScheduler`，不得反向使用 Rx 的 Forms/WPF 调度器。发布扫描拒绝重新引入 WPF/WinForms 运行时。
- MSIX 使用 `Msix/AppxManifest.template.xml` 在 edition 隔离的中间目录生成实际清单；`Identity.Name` 取统一包 ID，Application Id、文件关联名、显示名和 PFN/AUMID 随产品分离。`Tools/check-windows-package.py` 同时扫描实际 MSIX 与便携 EXE，覆盖错 ID、错架构、错资源、签名和分版资产正反门禁；真实双包安装/更新/卸载必须在 Windows 验收。
- 单文件发布必须全内容解包，且重建清单，不能复用多文件清单；不启用 trim/R2R。`PortableSingleFile.targets` 同时打入固定 `resources.pri` 回退名，以支持 EXE 改名，并过滤 PRI payload 展开的开发文件和 `NativeLibs/` 冗余副本；解码 DLL 与许可仅保留运行时根路径。
- 发布脚本从 Visual Studio 官方 Release x64 Redist 目录取得 CRT，或接受 `-VCRedistDirectory`；禁止系统目录和 `debug_nonredist` 来源，遵守微软再分发许可并原样携带。用户端不运行运行库安装器。
- PowerShell 发布脚本保留 UTF-8 BOM，兼容 Windows PowerShell 5.1。Git Bash 调用 MSBuild 属性使用 `-p:`，不用会被 MSYS 改写的 `/p:`。

## WinUI 注意事项

- 布局回调不改子元素尺寸或创建位图；瓦片尺寸交给 `Measure/Arrange`，尺寸变化后的裁剪等副作用异步处理。
- 主屏小型控件（命令按钮、评分星、相似聚类开关、动作按钮）的常态/悬停/按下/禁用统一走 `WindowsControlStyles`：按元素覆盖模板状态键，禁用不换底色、只降内容不透明度；只设 `Background` 不够，WinUI 模板会在禁用/悬停时回落到主题资源。
- 评分冲突（RATING-04）：组级冲突时卡片与控制栏的 0 号槽位换成警示三角（`WindowsIcons.WarningGlyph` + `WindowsLayout.StarConflict`），按钮几何与命中区不变；点击只打开来源浮窗，不写值、不切主图。控制栏订阅当前文件的 `Rating`/`HasRatingConflict`/`RatingSources`，后台来源读取完成后三角自动刷新。`WindowsRatingSourcesFlyout` 使用继承默认样式的原生 `Flyout`，与右键菜单一样保持窗口化弹层（`ShouldConstrainToRootBounds = false`）并用 `DesktopAcrylicBackdrop` + 透明背景——系统背景材质只作用于窗口化弹层，约束在根内容内时只剩半透明而无模糊；窗口化弹层会按显示器工作区自行夹取或翻转，主体位置由共享 `RatingSourcesLayout.Place` 按入口矩形算好，再夹进「窗口安全区 ∩ 所在显示器工作区的可见交集」（交集容得下浮窗才用，否则退回窗口安全区；工作区由 `WindowsWorkArea` 换算到根坐标，查不到时同样退回）后交 `ShowAt`；外形尺寸按当前 `XamlRoot.RasterizationScale` 分别取整内容与四侧边框，再换回 DIP（1 DIP 边框在 150% 下每侧实际为 2px），不可用未取整的描边估值贴工作区边——不先夹或低估一像素，主体都可能被系统二次翻转、整体平移一个自身尺寸（不自绘箭头或额外覆盖底色），共享 `RatingSourcesViewModel`，胶片栏、相似卡片和控制栏共用；展示期间核对真实锚点，切图、滚动、重排、隐藏/回收及菜单/下拉打开时关闭，外部点击与 Esc 由原生轻量弹层处理并同步共享 `IsOpen`。平台实现状态只在 `Design/status.md` 登记。
- 主窗口快捷键挂在根元素 `PreviewKeyDown`（`AddHandler(..., handledEventsToo: true)`），不依赖冒泡焦点路径：未映射/未绑定的按键放行，焦点在文本编辑控件内时整体放行；设置/工具窗口各自独立处理键盘。命令能否执行由共享 `InputCommandMap.IsCommandKnown` 判定，`WindowsIcons` 只负责显示。
- 控制栏换行由 `WindowsWrapPanel.MainLimit` 驱动：主轴上限取滚动视口，能整组换行时不得以滚动代替；单组超长才由 `ScrollViewer` 兜底。
- 导航图白框可拖动：`WindowsNavigator.PressAt/DragTo` 换算归一化点，交 `WindowsPhotoView.CenterOnNormalizedPoint`；滚轮仍冒泡给主图缩放。
- 分析栏细节预览的指针命中统一在 `WindowsAnalysisPanel` 面板层完成：起点在细节预览的触控由该瓦片显式 `CapturePointer`，滑出瓦片或窗口仍能收到 Up；鼠标与可悬停笔悬停点亮，触控按下点亮、滑动切换、滑出清除、抬起/取消/捕获丢失清除；起点在诊断瓦片或面板空白的触控不会滑入点亮，避免抢诊断点按。按指针 id 维护唯一活动目标（按下 > 悬停 > 键盘固定），旧指针结束不清新目标，换图/隐藏面板/窗口失活立即清除。细节瓦片可 Tab 聚焦，Enter/空格切换绿框、Esc 清除，并暴露名称与“绿框已显示”状态；诊断准星独立。
- 缩放比例预设每项只有一个居中的单值文本框（不用步进器）：Enter、失焦、切换分类与关闭设置都提交；>1600、≤0、非数字不写入，用页内 `InfoBar` 提示并恢复原值；合法提交调用 `ApplyScalePreset` 排序并复用行（不重建、不夺焦），落盘归关闭设置时的一次提交。
- `Border` / `ScrollViewer` 是 sealed；自定义容器使用 `Panel`、`Grid` 等。`Border.Width` 包含边框，选中描边用不占布局的叠加层。
- 虚拟化滚动区禁用 `ScrollViewer.ZoomMode`，避免无限尺寸测量。
- `ItemsRepeater` 整体替换 `ItemsSource` 不回收已实现元素，且 `Children` 在 WinUI 3 投影中不可访问；胶片栏换数据源时连同 `ItemsRepeater` 与新的 `StackLayout` 一起重建，解绑旧卡片并还原主轴偏移。
- 主窗口标题栏（SHELL-02）由 `WindowsTitleBar` 自绘，默认是浮在内容之上的悬停覆盖层；行布局筛选组与窗口按钮相交时，`_rowHost.Margin.Top` 让出 `shell.titlebar.height`，标题栏改为文件栏上方全宽亚克力常驻栏，热区外、离开窗口与失焦均不收起，宽度恢复后释放高度。`ExtendsContentIntoTitleBar` 保持 `false`，普通窗口态 `SetBorderAndTitleBar(hasBorder: true, hasTitleBar: false)` 保留系统边框、缩放边缘与圆角并去掉系统标题栏，只有全屏态才是 `(false, false)` + 最大化；全屏不保留常驻布局行。悬停形态在指针进入顶部 `shell.titlebar.hotzone.height` 才显示，移出热区、离开窗口或失焦即收起，不播过渡动画。
- 显隐判定必须同时吃 XAML 指针事件与 `InputNonClientPointerSource` 的非客户区事件（`PointerEntered/PointerExited/PointerMoved`、`EnteredMoveSize/ExitedMoveSize`）：指针进入 caption 区域后变非客户区，XAML 会报 `PointerExited`，只按 XAML 判会把标题栏抖掉；从非客户区离开或拖动结束时用 `GetCursorPos` + `ScreenToClient` 复核真实光标位置再决定是否收起。
- 拖动区用 `SetRegionRects(NonClientRegionKind.Caption, …)` 登记为系统 caption（物理像素），只在标题栏可见且非全屏时存在，排除窗口按钮（取真实布局位置，布局未跑完按 4 × 46 兜底）与行布局悬停形态筛选条的可点控件（`_filterBar.GetInteractiveBounds`）；隐藏与全屏态登记一条**零面积** caption —— 清空区域会让 WinAppSDK 退回系统默认的整幅宽 caption 带（`WM_NCHITTEST` 又变成 `HTCAPTION`，顶部重新可拖动）。拖动、双击、贴靠、缩放全部交给系统，不手动发 `HTCAPTION`、不自绘拖动、不建附属窗口。跨度：列布局从文件栏右缘（含相似面板扩宽与分隔线）到窗口右缘，行布局与文件栏隐藏时整宽；行布局悬停形态无背景并挖空筛选控件，窄宽度常驻栏铺全宽亚克力且不挖空。
- 标题栏背景分两处画（SHELL-02）：WinUI 3 的 `SwapChainPanel` 不支持 `AcrylicBrush`/`CompositionBackdropBrush` 背板取样（XAML 亚克力在交换链上只看到透明，按 `FallbackColor` 出纯灰），主图视口上的区段必须由 `WindowsPhotoView.SetTitleBarBackdrop` 在交换链内绘制，XAML 亚克力只铺主图以外的区段（最多两段，均 `IsHitTestVisible=false`）。交换链区段逐步对应 WinUI `AcrylicBrush` 效果图（以 microsoft-ui-xaml 源码为准，并用色条实测核对过原生输出）：复制区段（外扩 3σ）→ 4 倍降采样并转 sRGB → 可分离高斯（σ = `shell.titlebar.backdrop.blurAmount` × 物理倍率，与原生 BlurAmount 30 DIP 实测一致）→ 亮度混合（保留背板色相/饱和度，亮度取 TintColor，不透明度 `TintLuminosityOpacity`）→ 颜色混合（不透明度 `TintColor.A × TintOpacity`）→ 叠 `Microsoft.UI.Xaml/Assets/NoiseAsset_256x256_PNG.png` 2%（1 纹素 = 1 DIP 平铺，资源缺失只记诊断不叠噪点），全部在 sRGB 编码值上做，写不透明线性值。配方（`TintColor`/`TintOpacity`/`TintLuminosityOpacity`/`FallbackColor`）从 `AcrylicInAppFillColorDefaultBrush` 运行时读取，不得硬编码颜色；亮度层缺失时退化为 `FallbackColor` 纯色带并在日志里说明。接缝（`TitleBarBackdropEdges`）：主图视口边不在窗口边上时，模糊越界处按 `FallbackColor` 补（与 XAML 亚克力取不到交换链时一致），窗口边钳边缘像素；与 XAML 亚克力段相接的一侧，交换链区段在着色器里、XAML 段用 `WindowsTitleBar` 的渐变覆盖层，各在 `WindowsLayout.TitleBarBackdropSeamWidth`（2σ）内按 smoothstep 渐变到 `AcrylicBackdropRecipe.SeamColor()`（背板全为 `FallbackColor` 时的结果），两侧在交界处同值；只做单侧渐变不够，XAML 段贴边处会被下方面板内容（如分析栏缩略图）染色。区段只在标题栏可见且有背景时开启（全屏悬停也算可见），并随尺寸、DPI、栏位显隐、相似面板、主图视图尺寸重算；行布局悬停覆盖层不画背板，窄宽度常驻栏不与主图重叠、只铺 XAML 亚克力。背板纹理按区段与逻辑源区尺寸建/复用，字节（含噪点纹理）计入 `GpuTextures` 内存客户端，收尾随设备一起释放。不要改用窗口化 `Popup` + `SystemBackdrop` 承载标题栏：它确实能模糊主图，但弹层是压在主窗口上的独立窗口，盖住 DWM 窗口描边与右上圆角、拖动/最大化动画不跟随、窗口出屏时被夹在显示器内，caption 拖动还要靠改弹层窗口样式穿透。
- 窗口按钮固定为全屏、最小化、最大化、关闭：`shell.titlebar.button.width` × `shell.titlebar.height`、无圆角、紧贴窗口顶边与右缘、彼此无间距，字形取 `WindowsIcons` 的 Fluent 字形与 `shell.titlebar.button.glyphSize`；悬停/按下底色走 `SubtleFillColorSecondaryBrush`/`SubtleFillColorTertiaryBrush`（覆盖 `ButtonBackground*` 与描边、字形键，避免回落主题默认值），关闭按钮用 `color.titlebar.close.hover/pressed` 且字形转白。字形、提示与读屏名随窗口态切换：最大化 ↔ 还原、全屏 ↔ 退出全屏，presenter 状态变化与全屏切换都要同步。
- 窗口标题由 `WindowsMainWindow.UpdateWindowTitle` 统一写 `Window.Title` 与标题栏文字（同一个字符串）：`文件夹名 - 文件名 - <产品名>`，无当前文件只有产品名（由 `WindowsProductInfo.DisplayName` 给出 `PhotoViewer` / `PhotoViewer AI`），文件夹段与文件段同名时只出现一次，产品名固定保留在末段；订阅 `FolderVM.FolderName` 与 `MainViewModel.CurrentFile`。标题文字用 WinUI `CaptionTextBlockStyle`、单行尾部截断、不参与命中，可用宽不足时隐藏。
- 全屏标志与 presenter 状态保持一致（系统还原时同步回窗口态）；退出全屏先落标志再动 presenter，避免 `Changed` 递归重设边框，并恢复同一系统边框（`hasTitleBar` 仍为 `false`）。
- 任务栏缩略浮窗图标取窗口图标而不是 EXE 资源：csproj 把 `PhotoViewer/Assets/icon.ico` 以 `Link=icon.ico`、`ExcludeFromSingleFile=false` 复制到输出与单文件解包目录，激活后 `AppWindow.SetIcon(AppContext.BaseDirectory/icon.ico)`；缺失只记 `NativeStartupLog`，不阻塞启动。
- 主图菜单每一项都有语义前置图标（`MenuFlyoutItem.Icon` + `FontIcon`，字形表在 `WindowsIcons`）：打开文件=文档、打开文件夹=文件夹、胶片栏=照片、分析栏=方格网、控制栏=滑杆、雾天增强=对比度、工具=扳手、设置=齿轮。WinUI 1.8 的 `ToggleMenuFlyoutItem` 派生自 `MenuFlyoutItem`，沿用同一 `Icon` 与模板的 `CheckedWithIcon`/`UncheckedWithIcon` 状态，勾选项保留系统勾选列；不要删勾选、不要把图标拼进 `Text`。
- 控制栏按钮的悬停提示与读屏名称同源，统一走共享 `CommandTooltipText.Format`（`名称（主 / 次）`，空槽省略、主次相同只出现一次），键名仍由 `WindowsInput.FormatGesture` 给；未设置的槽传 `null`，不要出现"未设置"或用点号分隔命令名与绑定。
- 全屏使用 `OverlappedPresenter` 的边框与最大化组合；独立窗口在 `Activate()` 前按目标 DPI 定尺寸，选择器绑定该窗口句柄。
- 设置/工具共用 `WindowsWindowChrome` 和 `WindowsSettingsCard`。卡片使用 WinUI 主题资源；整块内容使用垂直对齐和 Stretch，需要撑满的控件放在 `Content`。设置拉条行按 SETTINGS-16 分成等宽左右两区，左区为标题，右区为拉条与固定宽数值列，跨分类的拉条边缘一致；缓存相关拉条不显示内部映射气泡，滚动预载延时直接使用共享模型的毫秒范围与步进。
- `NavigationView` 内容岛通过主题键去掉额外边框/圆角；不要使用不存在的 `PaneBackground` 属性。
- 可排序列表直接提供 `UIElement` 行，重排后回写共享集合；不要在 `ContainerContentChanging` 中替换行内容。
- `PhotoViewer.Windows` 命名空间会遮蔽 `Windows.*`；使用文件级 using 或 `global::Windows` 消歧。`DispatcherQueueTimer` 也可能需要完整限定名。
- WinUI 的跨 COM 异常不一定触发 `Application.UnhandledException`；诊断先看 `%LOCALAPPDATA%\PhotoViewer\native-startup.log`，验收模式使用隔离日志。

## 运行与验收

构建、运行和发布统一使用 [DEV.md](../DEV.md) 的 VS Code Tasks。直接运行可追加图片或文件夹路径。

| 参数 | 用途 |
|---|---|
| `--pv-native-smoke` | 主窗口、解码/渲染、模型、评分冲突指示、交互冒烟；另含悬停与窄宽度常驻标题栏（显隐、跨度与背景、32 点布局让位、失焦保持、caption 拖动区、窗口按钮与窗口态字形）、动态窗口标题、窗口图标、菜单图标与命令提示格式、设置展示影响分流（跨两个 2 秒内存探测周期与显式诊断通知后来源浮窗锚点/开放态、评分按钮与命令按钮身份、筛选条下拉展开态均不变，真实评分显隐与快捷键子项变化仍按影响位刷新）；AI 版另用进程内预算源覆盖 Caution 起跑、提取中进入 Warning，核对真实三路落库、6000×4000 CV 尺寸、合并进度、UI dispatcher 心跳与不灌入浏览 LRU，压力日志写 `memory-events-batch.jsonl`，不改系统内存；使用隔离设置、数据库和自有素材。 |
| `--pv-native-portable` | 与 smoke 合用，只针对单文件发布解包目录：真实使用的 libheif/pvhevc（AI 版另含 ORT/DirectML）必须已加载且来自解包目录；四份分发 CRT 显式从解包目录加载并核对来源，缺件不允许系统副本顶替。普通 Debug 输出不附带 CRT，不作为 portable 验收目标。 |
| `--pv-native-settings`、`--pv-native-tools` | 设置/工具专项；追加 `-hold` 保持窗口供目视。 |
| `--pv-native-edition` | 当前实际版本的能力、筛选条/分析栏/诊断页构成、独立 DINO/CV 门控与 CV 只读/不写库专项；默认输出到 `artifacts/acceptance/native-win-editions/<edition>/`，报告 `edition-checks-<edition>.json`。 |
| `--pv-native-activation-check` | 二次激活专项（OPEN-06）：本进程持单实例键并拉起第二个进程，核对激活转发、主实例定位目标、二次进程以 0 退出且不开第二个窗口；设置/数据库仍走隔离目录。 |
| `--pv-native-color`（Task **Check Windows Color**） | 色彩专项：JPEG 四源 proxy/full 经 GPU、HEIF 快速路径 vs 强制 libheif 600 代理（F16 保负值与 >1）、WIC RAW 实际色彩声明、native WCS 黑/灰/白、skcms matrix/TRC 65³、GPU P3/sRGB/ACM 旁路/HDR SDR 白 ×2 模拟、源实例保持；真屏 ACM/HDR 未切换时该项记 BOUNDARY。报告 `color-checks.json`。 |
| `--pv-native-rating` | 独立评分来源浮窗专项（也包含在 smoke）：真实冲突入口、逐侧 DPI 取整与原生 Presenter 尺寸一致、四边/双轴夹取、复位、诊断通知不重建、失败保留及整组写入。报告 `rating-checks.json`。 |
| `--pv-native-upload` | 独立上传/提交显示专项（也包含在 smoke）：后台帧提交前保留原纹理、最新代际接力、过期代际不发完成通知、GPU 像素对照、staging 字节记账与持续 Caution 不回填。报告 `upload-checks.json`。 |
| `--pv-stress=<文件夹>`（`--pv-stress-count=N`、`--pv-stress-mode=ab`、`--pv-stress-native-threads=N`） | 顺序按展示列表循环连翻，`ab` 在相邻两张间切换；计时终点 `present-return`，另记 `pixelsReadyMs`。支持 `--pv-stress-window=宽x高`（物理窗口）、`--pv-stress-full`、`--pv-stress-budget-mb=N`（仅模拟应用预算，≥1024）。并发覆盖仍只用于隔离压测。 |
| `--pv-perf-probe=<文件夹>` | 分段性能探针：HIF/ARW/JPG 各 6 张（首张预热），报告写隔离目录后退出，不进交互。 |
| `--pv-heif-thumb-probe=<文件夹或文件>`（可重复） | HEIF 缩略图项对比探针：逐文件对比"容器 `thmb` 项"与"主图缩放"在同一请求下的尺寸、方向与平均绝对差，并校验 DINO 560 档仍走主图；报告 `heif-thumb-probe.txt`，失败退出码为 1。 |

普通验收、压测与探针默认报告位于工作目录的 `artifacts/acceptance/native-win-acceptance/`；所有隔离入口可用 `--pv-acceptance-output=<目录>` 分开保存设置、数据库、启动日志和 `memory-events.jsonl`（压测日志为 `stress-flip.jsonl`，探针为 `perf-probe.txt`），验收失败退出码为 1。发布脚本 `-Edition standard|ai|both [-Form msix|portable|both] [-Local]` 扫描实际 MSIX 与便携 EXE；便携形态另对原名/中文空格路径改名 EXE 在独立空缓存下验收。报告与绑定产物 SHA-256 的 `deployment.json` 位于 `artifacts/acceptance/windows-editions/<轮次>/<edition>/`。`-Local` 只输出到 `artifacts/scratch/windows-local-publish/`，不覆盖正式归档；双 MSIX 安装、同时运行、更新与卸载需在 Windows 真实环境验收。

截图与窗口几何测量工具自身必须启用 Per-Monitor-V2；仅移动光标不能代替真实指针事件。自动化结果不能替代真实硬件、色彩或干净系统验收，具体状态只在 `Plans/` 与 `Design/status.md` 记录。
