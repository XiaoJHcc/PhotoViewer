# Windows 原生主窗口方案(P2-W)

> 2026-10-16 立项。隶属总纲 [native-rebuild-master-plan-1.md](native-rebuild-master-plan-1.md) §6 **Phase 4**,做法平移 macOS P2(见 [native-rebuild-phase-2-ui.md](native-rebuild-phase-2-ui.md) 与 [PhotoViewer.Mac/AGENTS.md](../PhotoViewer.Mac/AGENTS.md))。
> 作者要求:**用 Windows 原生方式把主窗口内容重构成原生,尽可能与旧 Avalonia 设计对标**。设置页、分析栏、聚类、工具页按 macOS 同样口径延期(后续均已落地:设置 §17、分析栏/聚类/增强 §15–§16、工具窗口 §18)。
> 跨平台对账真源:[Design/spec/](../Design/spec/00-principles.md) / [Design/status.md](../Design/status.md)。下文早期双壳与旧壳对拍为历史记录；6D 已退役旧壳，现行待办以 §13 为准。
>
> **2026-10-19 最新单 EXE 修复**：WinAppSDK 1.8 支持非打包自包含单文件，PRI 随包解出；清旧清单修复 `0x80040111`，全内容解包、不 trim/R2R、WinPS 5.1 BOM 保留。四个官方 Release x64 CRT 14.44.35211.0 现已随包，用户不需安装运行库；`PortableSingleFile.targets` 加固定 `resources.pri` 回退名，原来的改名限制已解除，不更改生产 App.xaml 引导。
> `release/PhotoViewer-0.8.0-win-x64.exe` 为 230,753,372 字节。原名及中文/空格改名各自独立目录/空缓存 **32/7/5，共 88 项全 PASS**：真实 HEVC 生产解码与 GPU/缩略图回读、DirectML 推理，并证明 CRT/libheif/ONNX/DirectML 均从解包目录实际加载。无现成干净机环境，因此干净 Windows 全系统验收仍未关闭，不以本机来源检查替代；Win10、相机真实素材、未签名/SmartScreen 仍保留。详见 [收尾记录、许可与 SHA256](native-rebuild-windows-release-closeout.md)。

## 0. W-M0 可行性闸门:结论(2026-10-16 已过)

在 `PhotoViewer.Windows` 上原地验证三项,全部通过:

| 闸门 | 结果 | 证据 |
|---|---|---|
| ① 同一工程加入 WinUI 3 后,旧 Avalonia 壳仍能构建与启动 | ✅ | `dotnet build PhotoViewer.Windows` 0 错误 / 4 条既有 NU1903;`--legacy-ui` 启动窗口标题 `PhotoViewer 图片查看器` |
| ② 自写 `Main` + `Application.Start` 能起原生窗口 | ✅ | 窗口标题 `PhotoViewer`,`PrintWindow` 导出 `bin/Debug/spike-window.png`,深色 Fluent 按钮/下拉/复选框渲染正常 |
| ③ 自包含单文件发布产物能启动 | ✅ | 单文件 exe **239 MB**(`PublishSingleFile` + 压缩 + 自包含 WinAppSDK),`--native` 启动并渲染同一深色界面(`bin/Debug/publish-native.png`);对照现役 Release **166 MB**(0.7.1) |

### 0.1 定下来的构建配置(踩过的坑,不要重走)

`PhotoViewer.Windows.csproj`:

```xml
<TargetFramework>net10.0-windows10.0.19041.0</TargetFramework>
<TargetPlatformMinVersion>10.0.17763.0</TargetPlatformMinVersion>
<RuntimeIdentifier>win-x64</RuntimeIdentifier>                 <!-- 固定 x64:LibHeif.Native 仅 x64,发布脚本亦硬拒其他 RID -->
<UseWinUI>true</UseWinUI>
<WindowsPackageType>None</WindowsPackageType>                   <!-- 非打包直接分发 -->
<EnableMsixTooling>true</EnableMsixTooling>                     <!-- 单文件发布的前置要求 -->
<WindowsAppSDKSelfContained>true</WindowsAppSDKSelfContained>  <!-- 免装 Windows App SDK 运行时,并让 WinUI 的 .pri 随输出就位 -->
<DefineConstants>$(DefineConstants);DISABLE_XAML_GENERATED_MAIN</DefineConstants>  <!-- 入口自写 -->
```

包版本集中在 `Directory.Packages.props`:`Microsoft.WindowsAppSDK 1.8.260921001`。

**WinAppSDK 框架包自带 ONNX Runtime,与本工程撞名(必须显式排除)**:
自包含部署会解包 `Microsoft.WindowsAppRuntime.1.8.msix`(x64 约 42 MB),里面自带一份 Windows ML 运行时
`onnxruntime.dll`(**1.23.x**)+ 自带 `DirectML.dll`。本工程用 `Microsoft.ML.OnnxRuntime.DirectML`(**native 1.24.x**),
两者同名不同字节。实测(压制重复检查后的诊断发布):`onnxruntime.dll` 取到本工程版本、`DirectML.dll` 取到框架版本 ——
**结果靠运气**;一旦 managed 的 `Microsoft.ML.OnnxRuntime 1.24.4` 配上框架的 1.23.x native,推理会在运行期直接失败。
故用 SDK 自带的排除钩子(目标内 `MicrosoftWindowsAppSDKFiles Remove="@(MicrosoftWindowsAppSDKFilesExcluded)"`)显式丢弃框架侧副本:

```xml
<WinAppSdkMsixContentDir>$(MSBuildProjectDirectory)\$(BaseIntermediateOutputPath)$(Configuration)\$(TargetFramework)\$(RuntimeIdentifier)\MsixContent\</WinAppSdkMsixContentDir>
<MicrosoftWindowsAppSDKFilesExcluded Include="$(WinAppSdkMsixContentDir)onnxruntime.dll"/>
<MicrosoftWindowsAppSDKFilesExcluded Include="$(WinAppSdkMsixContentDir)onnxruntime_providers_shared.dll"/>
<MicrosoftWindowsAppSDKFilesExcluded Include="$(WinAppSdkMsixContentDir)DirectML.dll"/>
```

坑:路径**必须**用评估期就有的属性拼。`IntermediateOutputPath` 要到 SDK 的 `Microsoft.Common.targets`(工程体之后导入)
才补上 TFM/RID 段,在工程体里读它得到的是空串(实测 `MsixContent` 落到工程根目录,排除静默失效)。
用 `ExcludeAssets="all"` 排掉 `Microsoft.WindowsAppSDK.ML`/`.AI` 包**无效**:该 MSIX 是框架单体包,内容与包资产无关。

### 0.2 三条硬事实(WinUI 工程,2026-10-17 更正)

1. **`Application.Resources` 在 XAML 引导前不可用**:在没有 `App.xaml` 时直接读/写会抛 `COMException 0x8000FFFF`;把 `XamlControlsResources` 放进根 `App.xaml` 后,由 `App.InitializeComponent()` 在构造期装载,`OnLaunched` 及之后可正常访问。
2. **`XamlControlsResources` 是 Fluent 控件模板的必需项**:没有 `App.xaml` 的纯 C# 工程直接 `new XamlControlsResources()` 会抛 `Cannot find a resource with the given key: AcrylicBackgroundFillColorDefaultBrush`,根因是缺少 XAML 元数据提供器(`IXamlMetadataProvider`)。补根 `App.xaml` 后 `NavigationView`/`TitleBar`/`NumberBox`/`TextBox`/`InfoBar`/`ProgressBar` 全部可用;**不要**照旧结论退回 ListView/Slider/自绘 Border。
3. **深色外观**:根 `App.xaml` 设 `Application.RequestedTheme="Dark"`(Mica 与主题资源取深色),窗口根元素仍设 `ElementTheme.Dark`。主窗口是自绘标题栏;设置窗口用 `MicaBackdrop` + `ExtendsContentIntoTitleBar` + `TitleBar` + `SetTitleBar`,系统 caption 按钮背景设透明(见 §17)。

### 0.3 其它 W-M0 观测

- WinUI 原生启动失败是 stowed exception(`0xC000027B`),**无控制台输出**。因此新增 `Native/NativeStartupLog.cs`(写 `%LOCALAPPDATA%\PhotoViewer\native-startup.log` + 未处理异常钩子);没有它无法区分"起不来"与"起来后崩"。这是常驻诊断,不是临时脚手架。
- 固定 `RuntimeIdentifier` 后 Debug 输出路径变为 `bin/Debug/net10.0-windows10.0.19041.0/win-x64/`;引用旧路径的 `.run/PhotoViewer.Windows.run.xml`(本就停留在 `net9.0-windows`)与脚本需在 W-M1 同步。
- 未显式设尺寸时 WinUI 窗口按屏幕宽度开(实测 1920×1015);W-M1 必须显式 `AppWindow.Resize`。
- Git Bash 里 `dotnet publish` 必须用 `-p:Foo=bar`,写 `/p:Foo=bar` 会被 MSYS 改写成路径导致 `MSB1008`(PowerShell 脚本不受影响)。
- **体积口径**：W-M0 多文件约 563 MB、单文件 239 MB（含 Avalonia）仅为历史记录。当前不 trim、不 R2R，已内置四个官方 x64 CRT，0.8.0 最终 EXE 为 230,753,372 字节（230.8 MB / 220.1 MiB），体积接受由作者决定。
- **单文件复核更正（2026-10-19）**：旧多文件清单缺 `loadFrom` 导致的 `0x80040111` 已修；PRI 固定回退名解除改名限制。最新正式包原名/中文改名各 32/7/5，共 88 项 PASS；干净机门仍待环境。证据见篇首及 [收尾记录](native-rebuild-windows-release-closeout.md)。

## 1. 范围与非目标

**本轮做**
1. `Program` 原生/旧壳分流(Mac 同构),原生模式不创建任何 Avalonia 对象。
2. 原生主窗口:隐藏式标题栏、两向布局、筛选条、胶片条、主图区、控制栏(EXIF/评分/命令)、右键菜单、空首屏提示、外部打开与拖放。
3. 主图渲染:GPU 表面 + EXIF 方向顶点变换 + 缩放/平移/100% + 代理图→全分辨率热替换 + 导航图。
4. 输入:鼠标/键盘/滚轮 → 共享 `AppGesture` → 共享命令表 → 原生壳命令分发。
5. 原生验收 harness(隔离设置/数据库、几何 JSON 对拍、程序化断言)。

**本轮不做**
- 分析栏 / 相似聚类 / 工具页 / 增强预览(P3):入口隐藏、配置保留(`SCOPE-01`)。（后续已落地：分析栏/聚类/增强见 §15–§16，工具窗口见 §18）
- 色彩正确性(ICC 广色域、ACM 探测、Win10 烘焙回退、`DXGI_COLOR_SPACE_RGB_FULL_G10_NONE_P709` 标签验证)→ W-M7;本轮只保证**与旧壳行为一致**。
- Windows RAW **主图**解码(`Sdcb.LibRaw`)为既有缺口，随 W-M7 独立立项；内嵌缩略图、列表与星级写回可用。
- 原生设置窗口 → W-M5;在此之前原生壳不显示设置入口(`SCOPE-01`)。
- Avalonia 退役(总纲 Phase 6)。

## 2. 选型

| 维度 | 结论 |
|---|---|
| UI 框架 | WinUI 3(Windows App SDK 1.8),非打包自包含 |
| 主图表面 | `SwapChainPanel` + 合成 DXGI 翻转链,`R16G16B16A16_FLOAT`(与 Metal 侧同口径) |
| UI 构建方式 | **C# 代码构建,不写 `.xaml`**(与 Mac 的程序化 AppKit 一致;规避与 Avalonia 的 XAML 编译目标冲突,几何常量可被 harness 直接断言) |
| 头工程 | `PhotoViewer.Windows` 单工程 + 运行期开关(与 `PhotoViewer.Mac` 结构一致) |

**否决项**:WPF(D3D 只能走 D3DImage/HwndHost,拿不到干净的 `DXGI + scRGB 标签` 路径;仅在 W-M0 失败时才考虑);Win32 + Direct2D 全手绘(结构最像 Mac 壳、零打包风险,但下拉框/菜单/滚动条/DPI 全自建,工作量显著更大);半原生(总纲 §3 已否决)。

## 3. 现状事实

- 改造前 `PhotoViewer.Windows/Program.cs` 只有 Avalonia 一条路径;平台实现仅 `Core/LibHeifDecoder.cs` 与 `Core/WindowsXmpWriter.cs`。
- 未注入的共享门面(回落默认):`BitmapLoader.InitializePresentationDecoder`(**Windows 无 `IPresentationImageDecoder`**)、`RawLoader.Initialize`、`NativeSettingsPresenter`、`StorageAccessManager`(Windows 用 `Noop` 正确)。
- `DirectNStandard`、`WicNet` 已在 csproj 与中心版本表里但**代码零引用**(W-M2 主图解码/W-M7 色彩会用到,届时转正)。
- 共享层可直接复用:`MainViewModel(nativePresentation: true)` 本身无 Avalonia 依赖;`ImageViewModel` 在 native 模式构造即 early-return,不产生 `WriteableBitmap`;`PhotoLoadSession`、`BitmapLoader/DecodeScheduler/MemoryGovernor`、`ThumbnailService`、`FolderViewModel/FileViewModel/FilterBarViewModel/ThumbnailListViewModel`、`SettingsViewModel`(全文件无 Avalonia)、`NativeSettingsSession`、`ExternalOpenService`、`Core/Input/GestureModels.cs`、`Core/Color/ColorEngine.cs`、`PhotoDatabase`、`XmpWriter`。
- 仍需原生壳自持:`ControlViewModel`/`AnalysisViewModel`/`ImageViewModel`/`DinoDebugViewModel` 仍有 Avalonia 类型(**命令分发与视口状态一律由原生壳持有**,Mac 同做法)。

## 4. 几何与行为对标表

基准来源:`Design/ui-contract.md`、[native-rebuild-phase-2-ui.md](native-rebuild-phase-2-ui.md) §1.4、`PhotoViewer.Mac/Native/*` 常量、旧壳实测。

| 元素 | 目标值(逻辑单位 = DIP) | 旧壳出处 |
|---|---|---|
| 四分区网格 | `Auto,*,Auto,Auto`(两向同构) | `Views/Main/MainView.axaml:13,59` |
| 栏底色 / 分隔线 / 主图底 | `#111111` / `#333333` 1 px / `#000000` | `MainView.axaml:20-52` |
| 文件栏内容宽(左右布局) | **116** + 1 分隔线 | `FileView.axaml.cs:46` |
| 筛选下拉 | **90×32**,间距 5,整体居中;顶距 34(左右)/10(上下) | `FilterBarView.axaml:13-19,57-61` |
| 上下文件栏总高 | 52(筛选)+ 159(胶片条)+ 1 = **212** | Mac `MacMainWindow.cs:259` |
| 胶片条滚动区 MinHeight / 内边距 | 148 / 左 10 上 5 下 10 | `ThumbnailListView.axaml:25,38` |
| 项距 | 96(横)/ 144(纵) | `ThumbnailListView.axaml.cs:253` |
| 卡片 | 外框 **90×138**(圆角 7、边 2)+ 四周 3 占位 = 96×144 | `Controls/ThumbnailCard.axaml:53-65` |
| 卡片缩略图 | **80×80** 圆角 4 | `ThumbnailCard.axaml:72-78` |
| 卡片第 2 行 / 星 / 抖动徽标 | 80 宽进度条 / 6×(14×14) / **16×16** 右下角 | `ThumbnailCard.axaml:102-165` |
| 控制栏内边距 / 组间距 | 10 / 10;组内 EXIF 4、评分 8、命令 8 | `ControlView.axaml:102-160` |
| EXIF 块 / 评分按钮 / 命令按钮 | **50×40** / **30×30** / **40×40** | `ControlView.axaml:71-86,158-160,16-22` |
| 评分方向 | 横 `0→5`、纵 `5→0` | `ControlView.axaml` Dock 方向 |
| 100% 口径 | 1 源像素 = 1 **物理**像素(`PhysicalScale = 逻辑缩放 × DPI 栅格化比`) | `IMAGE-01` |
| 导航图 | 长边 150、可见框 2 px、百分比 12 号粗体 | `ImageView.axaml:74-107` |
| 主图提示 | 标题 16 号灰 / 副标 11 号 `#888`,`MaxWidth 500` | `ImageView.axaml:54-71` |
| 定位动画 | **350 ms CubicEaseOut**,latest-wins,用户接管即取消 | `FILM-02/03` |
| 标题栏 | 隐藏式:内容延伸至顶边、保留系统按钮、拖动区避让居中筛选条 | `SHELL-02` |

**不得复制的旧壳缺陷**:`ThumbnailListView.axaml.cs:293` 可见区估算纵向分支误用 `offset.X/Width`(`ui-contract.md:113` 已登记);`FILM-03` 用户接管/减少动态效果在旧壳缺失,原生壳须补齐。

**平台差异登记**:栏位显隐在旧壳是会话内内存态(`MainViewModel.IsThumbnailViewVisible/IsControlViewVisible/IsAnalysisViewVisible` 不落设置),Mac 落 `NSUserDefaults`;Windows 取**旧壳口径**,不新增第二份持久化存储。

## 5. 目标文件布局

```
PhotoViewer.Windows/
  Program.cs                     # 分流(--native / --legacy-ui)✅ W-M0
  Native/
    WindowsNativeApplication.cs  # WinUI Application、门面注入、生命周期 ✅ W-M0 骨架
    NativeStartupLog.cs          # 启动期诊断(常驻)✅ W-M0
    WindowsMainWindow.cs         # 窗口/隐藏标题栏/帧布局/菜单/右键菜单/提示/命令分发/外部打开 ⏳
    WindowsUiServices.cs         # IUiDispatcher + SynchronizationContext + Rx 调度;IFilePicker;IAppResourceProvider
    WindowsLayout.cs             # 全部几何常量 + 唯一测量/排布装订点(harness 断言的唯一来源)
    WindowsControlBar.cs         # EXIF·评分·命令三组,按主轴换行换列居中
    WindowsFilmstripView.cs      # 虚拟化列表 + 定位动画 + 可见区上报
    WindowsThumbnailCard.cs      # 卡片(照片/文件名/日期/0-5 星/抖动徽标/两态描边)
    WindowsPhotoView.cs          # D3D11 + DXGI 交换链 + 着色器 + 缩放平移 + 导航图
    WindowsInput.cs / WindowsIcons.cs / WindowsImageAdapter.cs
    Windows*Acceptance.cs        # smoke / layout / filmstrip / input / toolbar
    LegacyLayoutAcceptance.cs    # 旧壳真实几何 JSON(移植自 Mac,供两侧对拍)
  Core/
    WindowsPresentationDecoder.cs  # IPresentationImageDecoder → RgbaF16/LinearSrgb
  app.manifest                   # 补 PerMonitorV2 DPI + Win10/11 supportedOS GUID
  AGENTS.md                      # 新:对标 PhotoViewer.Mac/AGENTS.md
```

## 6. 里程碑

**W-M0 可行性 spike 与选型闸** ✅ 见 §0。

**W-M1 原生壳骨架与四分区几何** — 首屏已跑通(2026-10-16),状态与遗留见 §10。
0. 现状:分流口径已按 macOS 落地 —— **Debug 默认原生壳**(`--legacy-ui` 退出),Release 默认旧壳(`--native` 进原生);`.vscode/tasks.json` 与 `DEV.md` 已按 macOS 拆成原生/旧壳/对拍三组任务。
1. 启动序(对位 `MacNativeApplication.cs:24-60`):`WindowsUiDispatcher`(DispatcherQueue + `SynchronizationContext`)+ `UiDispatcher.Initialize` → Rx builder(`WithMainThreadScheduler`) → `FilePicker` → `AppResources` → `HeifLoader` → `PerformanceBudget` → `MemoryGovernor` → `XmpWriter` → `SettingsService` → `DinoFeatureExtractor(DirectML)` → `new MainViewModel(nativePresentation: true)` → `ExternalOpenService.RegisterHandler`。**`UiDispatcher.Initialize` 必须早于 `MainViewModel` 构造。**
2. 窗口:深色、隐藏式标题栏(`ExtendsContentIntoTitleBar` + `InputNonClientPointerSource.SetRegionRects(Caption, …)` 给拖动区挖掉筛选条)、`AppWindow.TitleBar` 显式深色、初始 1200×850、最小 560×480、PerMonitorV2 DPI。
3. `WindowsLayout` 帧布局 pass:按 `IsRowLayout` 与 `UpdateScreenOrientation(w,h)` 分区,1 px 分隔线,栏位显隐即时归主图;§4 常量集中于此。
4. 筛选条 / 胶片条 / 主图区(fit)/ 控制栏三组 / 空首屏提示 / 右键菜单(设置入口隐藏)。
5. `WindowsIcons`:命令 → Segoe Fluent Icons 码位(打开 `\uE8E5`、上/下张 `\uE76B`/`\uE76C`、适应 `\uE740`、放大/缩小 `\uE8A3`/`\uE71F`、星 `\uE735`/`\uE734`、清星 `\uE711`;实施时逐个核对),无语义匹配者沿用共享 uicons 字体。
6. 外部打开与拖放:CLI 参数 + 窗口 `Drop` 的 `GetStorageItemsAsync` → `ExternalOpenService`。
- **门**:构建 0 错误 0 新增警告;四分区/两向/栏位显隐正确;`--legacy-ui` 零回归;publish 产物仍可启动。

**W-M2 主图渲染(对等 `MetalPhotoView`)**
- `WindowsPresentationDecoder : IPresentationImageDecoder`(`BytesPerPixel=8`):复用 `SkiaImageCodec`(含 `DecodeToLongEdge` 子采样)与 `LibHeifDecoder` 取 8 bit → `ColorEngine`(必要时补 `ToScRgb16`)转 **RgbaF16 + LinearSrgb**,携带 `SourceWidth/Height` 与**未应用** `Orientation`。
- 渲染器:`SwapChainPanel` + 合成翻转链(R16G16B16A16_FLOAT),D3D11 上传(扁平缓冲 + `Stride`)、**EXIF 方向做 UV 置换而非旋转像素**、三线性 + mipmap;拒绝非 F16 输入(硬契约,不做隐式降级)。
- 交互:fit / 双击 100%(@光标)/ 滚轮缩放 / 拖动平移 + 边缘约束 / 预设档;`NeedsFull`(`PhysicalScale ≥ 1 || 超纹理能力`)→ `Session.UpdateViewport(物理宽, 物理高, NeedsFull)`,热替换同变换、无跳变。
- 导航图在同一交换链内合成。
- **租约语义**:`PhotoLoadSession.Changed` 回调内同步拷出/上传,不得跨下一次 `Changed` 持引用。
- **门**:代理长边 ≈ 2× 视口物理像素;100% 请求 Full 且 `PhysicalScale == 1`;切图/换向/换屏几何与方向正确(`IMAGE-01`)。

**W-M3 胶片条与筛选交互(FILM-01…06)**
- 虚拟化列表 + 可见区上报(`QueueThumbnailLoad`/`ReportVisibleRange`)+ `SequenceEqual` 判等避免无谓重建。
- 项内推送:当前项蓝描边、`IsInCache` 淡蓝内描边、抖动徽标、加载态、评分金/灰;换缩略图前 `TryAcquire/Release`。
- 定位:点选/换序/筛选/改星**不居中**;翻页/显式打开 350 ms 三次缓出居中、首尾受边界约束、latest-wins、用户接管取消;减少动态效果直接定位。
- 评分写回走共享 `SetRatingAsync`,失败在窗口内可见(FILM-06);无筛选结果给简短空状态。
- **门**:`WindowsFilmstripAcceptance` 覆盖触发条件表、动画中间帧/终点/末次请求、用户接管、两向滚动、卡片两态与徽标;写回只用自有副本素材。

**W-M4 输入与快捷键(INPUT-01)**
- `WindowsInput`:键/指针/滚轮 → `AppGesture` → `SettingsViewModel.GetCommandByGesture` → `WindowsMainWindow.Execute(string)` switch;`WindowsIcons.ForCommand == null` 的命令不被消费(P3 命令按构造不可达)。
- 文本框/录制控件获焦时暂停全局照片快捷键;`FormatGesture` 输出 Windows 显示文本。
- **门**:`WindowsInputAcceptance` 覆盖映射、不抛异常、空首屏点击开文件、右键菜单、方向键被消费。

**W-M5 原生设置窗口(SETTINGS-01/02)** ✅ 2026-10-17(方案 [native-rebuild-windows-settings.md](native-rebuild-windows-settings.md);落地记录见 §17)。
- `WindowsNativeSettingsPresenter : INativeSettingsPresenter` + `NativeSettingsSession` 工作副本 + 关闭回写,不改 JSON 字段名/默认值。
- 独立 960×720 窗口(Mica + `TitleBar` + `NavigationView` 240 + 五页 `SettingsCard`、不进任务栏);控件与尺寸/行模型见 §17。
- 五分类分组表单;控制列表行 = 显隐 + 名称 + 主/次快捷键 + 拖动重排(同一行);录制与冲突高亮;AI 页全部可用(P3 已落地);清库二次确认。
- **门**:`--pv-native-settings` **7 项 PASS**;主冒烟 24 项无回归;真实拖拽/录制/清库/预设增删已由作者 2026-10-18 手验。

**W-M6 验收 harness 与文档同步**（专项/几何导出未做）
- 已有 `--pv-native-smoke/settings/tools`；计划的 `--pv-native-layout/filmstrip/input/toolbar` 专项尚未实现。隔离设置/数据库落 `release/native-win-acceptance/`，**不写用户照片、不动用户设置**。
- 几何对拍：按 6F 导出与样机同格式的 Windows 原生几何，覆盖三尺寸 × 两方向。旧壳与 `--legacy-ui --pv-legacy-layout` 已随 6D 退役，原旧壳 JSON 对拍任务失效，不再建设。
- 帧导出:`Windows.Graphics.Capture` 或 `PrintWindow`(权限受限时如实记录,不冒充通过)。
- 文档:本文（§12–§18 即 Windows 验收记录）、`Design/status.md` 的 Windows 列(**编译通过≠验收通过**)、[架构文档](../Design/architecture.md) §4 平台矩阵 + 根 `AGENTS.md` 模块索引、新建 `PhotoViewer.Windows/AGENTS.md`、`Plans/native-rebuild-handoff.md`、`DEV.md`（旧壳 `Debug Windows (Legacy UI)` 任务已随 6D 删除）。

**W-M7(独立立项)**:色彩正确性(ICC/nclx、ACM 探测、Win10 显示器 ICC 烘焙、跨屏重标签)+ Windows `LibRawDecoder`(`Sdcb.LibRaw`)。

**W-M8 工具窗口(P3 收口)** ✅ 2026-10-17 — 方案、实现与验收见 §18;`--pv-native-tools` 5 项 PASS,主冒烟/设置回归无变化。

## 7. 实现约定

1. 原生模式下 `ImageViewModel` 是"植物人":不得读其 `SourceBitmap/Scale/Translate/ViewSize/HintText`;视口状态与提示文案由原生壳持有。
2. **命令表只有一份**:`SettingsViewModel.Hotkeys`(`HotkeyItem.Command` 字符串进设置 JSON);原生壳不得自建命令枚举。
3. **布局常量单一来源**:全部几何常量放 `WindowsLayout`,harness 反查常量本身,防止断言与实现一起漂移。
4. 设置/热键批量变化走一次 dispatcher turn 内合并重建。
5. `UiDispatcher.Post/InvokeAsync` 是唯一跨线程入口。
6. 不引入无谓 fallback:输入与像素格式不匹配时直接拒绝,不静默降级。

## 8. 风险与缓解

| 风险 | 等级 | 缓解 |
|---|---|---|
| WinUI 与 Avalonia 同工程构建/运行干扰 | 低(W-M0 已证可行) | 保留 `--legacy-ui` 回归门 |
| 单文件发布形态变化(exe 旁挂文件) | 中 | W-M0 §0.3 已记录实际形态;若变化由作者定分发形态 |
| 合成交换链 scRGB 标签与 ACM 交互不明 | 中 | 本轮不承诺色彩正确性,W-M7 单独取证 |
| `win-arm64` 声明了却不可用(`LibHeif.Native.win-x64` 仅 x64) | 低 | 维持现状,发布脚本已硬拒非 x64 |
| 虚拟化列表频繁 data reset 丢项(Mac 踩过复用池被 GC) | 中 | `SequenceEqual` 判等 + 增量刷新 + 卡片强引用 |
| 双壳并存维护成本 | 中 | 冻结旧壳新功能;原生壳逐里程碑接管 |

## 9. 作者待确认项(默认值先按推荐执行,可随时改)

1. 隐藏式标题栏保留**系统**最小/最大/关闭按钮(推荐)还是复刻旧壳 36×32 自绘按钮?
2. 设置窗口延到 W-M5、期间隐藏设置入口(推荐)是否接受?
3. 原生窗口初始 1200×850 / 最小 560×480(与 Mac 一致,便于跨平台对拍)是否接受?
4. 栏位显隐取旧壳口径(会话内内存态,推荐)还是 Mac 口径(落盘)?
5. Debug 输出路径因固定 RID 变为 `…/win-x64/`(发布形态不变),是否接受?

---

## 10. W-M1 首屏进展(2026-10-16)

**已跑通**:原生壳启动序(门面注入 + `MainViewModel(nativePresentation:true)` + 外部打开)与**四分区主窗口**
(筛选条 / 胶片条 / 主图 / 控制栏),默认左右布局,实机 1200×850 截图见 `PhotoViewer.Windows/bin/Debug/first-screen.png`。

已落地并与旧壳对标的几何:文件栏 116(+1 分隔线)、筛选下拉 90×32 与间距 5、卡片 90×138(缩略图 80、四周 3 占位)、
EXIF 块 50×40、评分 30×30(纵向自下而上 0→5)、命令 40×40、栏底 `#111111` 与 1px `#333333` 分隔线。
命令分发**复用共享 `ControlViewModel`**(Open/Previous/Next/Exchange/SetRating 与旧壳同一份实现),
只有视口相关命令(Fit/Zoom*）由壳实现;滚轮与键盘统一走共享手势表,P3 命令既不显示也不消费按键。

**本轮新增的 Windows 侧硬约束**(详见 [PhotoViewer.Windows/AGENTS.md](../PhotoViewer.Windows/AGENTS.md)):
`ProgressBar` 的旧 `E_FAIL` 结论已由 W-M5 根因纠正(缺 `App.xaml`,控件现可用);相似聚类进度仍保留自绘两条 `Border` 作视觉定制;
虚拟化 `ScrollViewer` 必须 `ZoomMode=Disabled`;`Border`/`ScrollViewer` 在 WinUI 3 是 sealed;
`Windows.UI.Color` 会被本仓 `PhotoViewer.Windows` 命名空间遮蔽。

**遗留(下一步)**
1. **主图缩放/偏移**:布局前 1×1 占位视口已阻止上报；定向回归进一步修正无 EXIF 尺寸时丢失原始像素尺寸的问题（见 §11）。真实照片、八方向和跨屏仍需 W-M6/W-M7 验收。
2. **上下布局**:已完成两向三尺寸往返 resize 和标题栏区域的定向实机程序化验证；完整几何对拍、滚动及真实输入仍待 W-M6。
3. 常规原生壳继续使用用户既有设置；`--pv-native-smoke` 已接入隔离设置/数据库/日志及自有照片，其他 W-M6 专项入口尚未实现。
4. **Debug 默认已是原生壳**(`--legacy-ui` 退回旧壳),`.vscode/tasks.json` 拆成 `Debug/Run Windows`(原生)、`Debug/Run Windows (Legacy)`(旧壳)与 `Compare Windows (Native + Legacy)`(对拍),`DEV.md` §三.1 已同步。
5. 导航图(W 右下角小图)、EXIF 真实素材对拍(仓库内 JPEG 无 EXIF,EXIF 块显示 `--`)待补。

## 11. 首轮底层纠偏与定向回归（2026-10-16）

- **保持 WinUI 3 + D3D11 路线**。D3D11 flip-model 仍支持同一 API buffer 对象的自动轮转，不套用 D3D12 的逐帧索引管理；RTV 明确来自 buffer 0，resize 前解除 OM 引用，关闭时脱开 SwapChainPanel 并 ClearState 后释放。
- **调度契约**：Normal → DispatcherQueue Normal，Background → Low；UI 线程 Background InvokeAsync 也须排队，不能就地执行。SynchronizationContext 续体保持 Normal。
- **源尺寸**：codec 缩解码及内存 Mitchell 精缩保留真实原始像素尺寸，Windows 展示解码使用该尺寸而非 EXIF；缺少 EXIF 的 3600×2400 JPEG 不再被误判为代理已是 Full。
- **标题栏**：DIP 边界转换为物理像素，监听 XamlRoot 与筛选条布局更新；上下布局仅避让实际控件组合，避免用拉伸网格宽度清空自定义区域、退回系统默认拖动区；胶片条隐藏时恢复全宽拖动。
- **模型**：Windows 头工程以 .NET 清单资源提供模型，支持工作目录变化及单文件发布；迁移期与旧壳 AvaloniaResource 各有一份同源模型，原始模型 86,582,598 字节，这项体积代价随 Native/Legacy 工程和资源归属拆分再消除。
- **验收入口**：`Check Windows Native` Task；仅 `--native --pv-native-smoke` 激活，配置自有设置/数据库/日志，忽略外部图片参数并生成自有棋盘 JPEG；输出 `release/native-win-acceptance/checks.json`，失败退出码 1。覆盖真实队列顺序、线程池回 UI、资源 SHA256/DirectML 双输出推理、两向 1200×850 / 830×650 / 700×480 加恢复原尺寸、标题栏区域、100% Full 升级和关闭释放。
- **已验证边界**：当前设备 scale=1 的 13 项定向检查通过，模型 SHA256 与源文件独立核对一致；不是 W-M6 完整验收。150%/200% DPI、实际跨屏、真实鼠标手感、八方向和系统色彩仍待验。新增共享尺寸元数据回归，ColorTestTool 共 27 项通过（原色彩口径保持）。

## 12. 对标纠偏与标题栏自动显隐（2026-10-17）

作者要求:原生 UI 继续充分对标旧壳(Avalonia),修掉未对齐 / 边距缺失 / 内容出框,并补**标题栏自动显隐**与**打开文件不可用**。
本轮方法:在 150% 系统缩放下用同一支 DPI 感知的抓图脚本把两壳窗口设成同一物理尺寸截图,配合自带 harness 与 UIA 矩形实测定位差异,逐项修;不再靠肉眼判读。

### 12.1 根因清单（按影响排序）

| 现象 | 根因 | 证据 |
|---|---|---|
| 整套 UI 与旧壳对不齐、发虚 | `app.manifest` 自备清单**覆盖了 Windows App SDK 默认清单但没有 DPI 声明** → 进程 DPI-unaware（`GetProcessDpiAwareness=0`、`GetDpiForWindow=96`），150% 屏上被系统按位图拉伸 | 同机旧壳窗口 `awareness=2 / dpi=144`，原生 `awareness=0 / dpi=96`；补 `PerMonitorV2` 后两边同为 144 |
| 打开文件不可用（点了没反应） | `WindowsFilePicker.ResolveExtensions` 把共享层的 glob（`*.jpg`）补点成 `.*.jpg`，WinRT `FileTypeFilter.Add` 抛 `ArgumentException`；调用处 `_ = …OpenFilePickerAsync()` 丢弃任务 → 静默无响应 | 日志 `打开文件选择器失败: System.ArgumentException`，栈指到 `FileTypeFilter.Add` |
| 胶片条卡片窄 4 DIP、项距 150、缩略图出框 | ① `_filePaneFrame.Width=116` 在 WinUI 里**含 1 px 右边框** → 内容只剩 115，胶片条可用宽 95 < 卡片 96 被压窄；② 卡片同时设 `Width/Height=96/144` **又叠 `Margin=3`** → 项距变 150；③ 缩略图 `Image` 未限幅，按位图自然尺寸铺开（实测 83×58 DIP）撑出 80×80 槽位 | 同尺寸实测:卡片 85.6 / 项距 149.3，旧壳 87.3 / 143.4 |
| 控制栏 EXIF 块无间距、整组连成长条，评分组左对齐 | ① EXIF 缺旧壳的 `Spacing=4` + 每块 4/4 外边距；② 评分组把旧壳加在**星条**上的 8/8 留白加成**组框外边距**，组内星条也未居中；③ EXIF 分组边框比旧壳多一层 `#222` 底色，把 12 的块间留白填成一片 | 实测 EXIF 组 160（旧壳 204）、评分组 180（旧壳 196）、块间距 0（旧壳 12） |
| 标题栏不会自动显隐 | 沿用系统标题栏与系统最小/最大/关闭按钮，系统按钮无法隐藏 | 旧壳 `MainWindowForWindows.axaml.cs:140-176`：指针 Y ≤ 50 显示、移开/失活隐藏，自绘 36×32 四按钮 |

### 12.2 修复

1. **DPI**:`app.manifest` 补 `dpiAware=true/pm` + `dpiAwareness=PerMonitorV2` + `longPathAware`（Windows App SDK 默认清单就是这几项，自备清单必须自己声明；旧壳 Avalonia 自行设置 DPI 感知，不冲突）。
2. **文件选择器**:扩展名模式统一规范化成 `.jpg` 形式（`*.jpg` / `.jpg`；裸 `jpg` 不再接受），非法模式显式报错；主窗口空首屏、右键菜单、`Open` 命令都改走 `OpenFilePicker()`，失败写 `NativeStartupLog`，不再静默。`Debug Windows` 已在 150% DPI 实机验证选择器可弹出。
3. **文件栏宽度**:当时新增过 `WindowsLayout.RailFrameWidth = RailWidth + DividerThickness`（WinUI 的 `Border.Width` 含边框）；§16 同步后文件栏宽度交给内容计算，该常量已删除，新增带边框占位仍须按"内容宽 + BorderThickness"计算。卡片改为整格 96×144 且**不再叠 Margin**（旧壳的 3 占位已经在 96×144 里）。
4. **缩略图限幅**:卡片照片容器定为 80×80，`Image` 与井、抖动徽标共用同一坐标系，Uniform 留边 12/12。
5. **控制栏间距**:EXIF 块按旧壳 `WebMarginConverter '0,4|4,0'` 设横/竖两向 4/4 外边距 + `Spacing=4`；星条的 8/8 留白移回星条自身；EXIF 分组去掉多余底色；三个分组边框不再有外边距；EXIF/评分/命令组内控件改回居中。
6. **筛选条**:`SetOrientation` 不再做方向补偿（旧壳两个方向都是 `Margin=10`，计数文本无额外外边距），下拉框显式 90×32。
7. **标题栏自动显隐**:新增 `Native/WindowsTitleBar.cs` —— `OverlappedPresenter.SetBorderAndTitleBar(hasBorder:true, hasTitleBar:false)` 去掉系统标题栏与系统按钮，改自绘「标题文本 + 最小化/最大化/全屏/关闭」四个 36×32 按钮；指针进 50 DIP 热区显示、移开或窗口失活隐藏；**拖动区只在标题栏可见时**登记为系统 caption（隐藏即清空，顶部不可拖动），按钮区域始终排除在拖动区外。窗口标题同时对齐旧壳 `PhotoViewer 图片查看器`。
   - 该项**覆盖 §9 待确认项 1** 的推荐值（原推荐保留系统按钮）：作者 2026-10-17 明确要求自动显隐，系统按钮无法隐藏，故改为自绘。代价是失去系统最大化按钮的贴靠布局（Snap Layouts），与旧壳一致。
8. **验收 harness**:`CheckCaptionRegion` 改为核对新契约 —— 隐藏态必须**零** caption 区域、显示态区域不越界且不压住标题栏按钮、隐藏胶片条时覆盖到按钮左侧；`--pv-native-smoke` 在 3 尺寸 × 2 方向上继续跑该项。
9. **胶片条定位**:`--native <图片路径>` 打开非首张时胶片条停在列表头部（旧壳会定位到目标）。实测根因有两个:① `ScrollToCurrentRequested` 与布局时序竞争 —— 请求到达时 `Viewport/Extent` 还是 0，紧随其后的一次 `ReloadItems`（`ItemsSource` 归零再赋值）又把刚滚到的偏移清回 0；② WinUI 虚拟化列表的 `Extent` 是**逐轮增长**的，早期算出的目标会被夹短（实测第 1 轮 extent 只到 2337 / 需要 2592），按夹取结果销账就会停在半路。改为**收敛式定位**:请求记账后由 `LayoutUpdated` 补做，只有 extent 盖住目标项、且偏移真正落到目标才销账（最多 4 次动画 + 失败留日志）。同时把换向时的 `ChangeView(0,0)`（甩回顶部）改成按当前项重新定位 —— 旧壳口径是"换向定位当前项 / 普通 resize 不抢位置"，而项距从 96 变 144 后旧偏移本来也无意义。
10. **空首屏左右键分工**:空首屏命中层原来对任意鼠标键都打开文件选择器，右键会同时弹选择器和右键菜单。改为与旧壳 `ImageView` 同口径 —— 鼠标只认左键（右键让位给右键菜单），触摸/笔直接打开。实机验证：空首屏右键只出菜单、左键出选择器；已载图后右键菜单照常。

### 12.3 对齐结果（150% 缩放、两壳同尺寸实测）

| 项 | 旧壳 | 原生壳 |
|---|---|---|
| DPI 感知 / 窗口 DPI | PerMonitorV2 / 144 | PerMonitorV2 / 144 |
| 左右布局文件栏 | 116 + 1 px 分隔线 | 116 + 1 px 分隔线 |
| 卡片 / 项距 | 90×138 / 96×144 | 90×138 / 96×144 |
| 控制栏竖排组高（EXIF / 评分 / 命令） | 204 / 196 / 280（6 按钮） | 204 / 196 / 160（4 按钮，P3 两项不显示） |
| 控制栏横排组宽（EXIF / 评分 / 命令） | 244 / 196 / 280 | 244 / 196 / 160 |
| 主图 fit 位置（左右布局，竖图 2:3） | x 502–1344 px，中心 615.3 DIP | x 502–1343 px，中心 615.0 DIP |
| 上下布局文件栏总高 | 52（筛选）+ 159（胶片条）= 211 | 211 |
| 标题栏按钮 | 36×32，首按钮左 1009.3 DIP | 35.6×30.3，首按钮左 1010.7 DIP |
| 标题文本左起 | 129.3 DIP | 129.1 DIP |
| 外部打开非首张的列表定位 | 当前项贴近列表底部（偏移 1739 DIP） | 当前项贴近列表底部（偏移 1763 DIP） |

实机交互（真实鼠标输入）已验证:热区悬停显示 / 移开隐藏、按钮最大化与还原、caption 拖动区可拖动窗口、窗口失活隐藏、空首屏与右键菜单可弹出文件选择器并成功返回。

### 12.4 已知且有意的差异（不是未对齐）

- P3 入口按 `SCOPE-01` 不显示:筛选条的「相似聚类」开关、控制栏的「分析栏展开/收纳」「增强预览」两个命令。因此原生控制栏命令组比旧壳短 120 DIP，**上下布局下旧壳的控制栏会折成两行、原生保持一行**，这是同一套换行规则作用在不同按钮数上的结果，不是布局差异。
- 图标集按平台取（`WindowsIcons` 的 Segoe Fluent Icons ↔ 旧壳 uicons 私用区），与 macOS 用 SF Symbols 同一策略。

### 12.5 本轮遗留

本轮新增但没关掉的验证项已并入 [§13 状态清单](#13-已完成--待完成清单)：200% DPI、真实跨屏、八方向真实素材、系统色彩与鼠标手感。

## 13. 已完成 / 待完成清单

> 口径:状态只写"证据到哪一步"。**编译通过 ≠ 验收通过**;程序化 harness 通过 ≠ 真实设备/手感验收。
> ✅ 完成(有证据) · 🟡 部分(已实现,验收项未关) · ⬜ 未开始 · ⏸ 有意延期。

### 13.1 已完成

| # | 项 | 状态 | 证据 |
|---|---|---|---|
| 1 | W-M0 可行性闸门(同工程 WinUI 3、自写 `Main`、非打包自包含、单文件发布可启动) | ✅ | §0;`--legacy-ui` 零回归 |
| 2 | 原生/旧壳分流(Debug 默认原生,`--legacy-ui` 退出;Release 反向) | ✅ | VS Code Tasks + 实机多次运行 |
| 3 | 四分区主窗口(筛选 / 胶片 / 主图 / 控制)+ 两向布局 + 独立栏位显隐 + 深色外观 | ✅ | §12.3 两壳同尺寸对拍;harness 3 尺寸 × 2 方向 |
| 4 | **DPI 感知**(自备清单声明 PerMonitorV2,修掉整窗位图拉伸) | ✅ | 两壳 `awareness=2 / dpi=144`;harness 全程 `scale=1.5` |
| 5 | 几何逐项对齐(栏宽 116+1、卡片 90×138/项距 96·144、缩略图限幅 80×80、控制栏 EXIF 204/评分 196、筛选条 90×32 与 10 边距) | ✅ | §12.3 表 |
| 6 | 标题栏自绘四按钮 + 50 DIP 热区自动显隐 + 仅可见时可拖动 | ✅ | 真实鼠标输入:悬停显示/移开隐藏/最大化/还原/caption 拖动/失活隐藏;harness caption 契约 8 组 |
| 7 | 文件选择器(扩展名模式规范化 + 失败留痕,修掉"打开文件不可用") | ✅ | 实机弹出并返回;日志无静默吞异常 |
| 8 | 空首屏左右键分工(右键只出菜单、左键开文件) | ✅ | 实机三场景(空态右键/空态左键/载图后右键) |
| 9 | 主图 GPU 表面:D3D11 交换链 F16、EXIF 方向顶点变换、fit/100%/滚轮/拖动、代理→Full 热替换、resize 同步 | ✅ 定向 | harness:无 EXIF 3600×2400 源尺寸、`PhysicalScale=1.0`、视口与交换链尺寸一致;展示解码经 `WindowsPresentationDecoder`(四参签名,拒绝非 `RgbaF16`)+ `WindowsPresentationEncodingPolicy`(恒 F16) |
| 10 | 胶片条虚拟化(ItemsRepeater + 卡片池 + 可见区上报 + 350ms 三次缓出定位 + 收敛式定位) | ✅ 定向 | 卡片几何/项距/打开定位/换向重定位已对拍;用户接管与"减少动态效果"见 §14(harness 覆盖) |
| 11 | 输入:键盘/滚轮 → 共享手势表 → 共享命令表 | 🟡 | NumberPad3→SetRating3 映射已过；`INPUT-01` 整体仍待核验，不直接认定缺功能（设置窗口隔离见 #20） |
| 12 | 命令分发复用共享 `ControlViewModel`(Open/Previous/Next/Exchange/SetRating 与旧壳同源) | ✅ | 单测式对拍 + 实机 |
| 13 | 定向验收 harness(`--pv-native-smoke`,隔离设置/数据库/日志 + 自有素材) | ✅ 定向 | **24 项全过(150%)**；专项 harness 与 6F 几何导出仍缺，见 13.2 |
| 14 | **缩放指示器(导航图)**:长边 150 缩略图 + 2 px 白色可见框 + 百分比,显示条件与旧壳一致、不可交互 | ✅ | §14;harness 断言 fit 隐藏/非 fit 显示、像素尺寸、白框在缩略图内、百分比与 `PhysicalScale` 同口径、设置关闭即隐藏;缩略帧取 `PhotoLoadSession.Image`(含增强态),`SKImage` pin 覆盖 `DrawImage` 全程,栅格化比例变化时重建 |
| 15 | **FILM-03**:点选/滚轮/拖动滚动条/触摸接管撤销动画与排队请求;系统"减少动态效果"直接到终点 | ✅ 定向 | §14;harness 断言动画中接管后位置不再推进、减少动态效果两次请求终点一致;真实手感待手测 |
| 16 | **FILM-06**:星级写回失败在窗口内可见(底部状态提示),不呈现虚假成功 | ✅ 定向 | §14;共享 `MainViewModel.RatingWriteFailed` + 只读素材触发,harness 断言提示可见且星色未变 |
| 17 | 窗口态闭环:自绘按钮的最大化/还原/全屏/退出全屏与 presenter 状态一致 | ✅ 定向 | §14;harness 四态往返；真实跨屏待验，贴靠布局为有意差异 |
| 18 | 八方向 + 带 EXIF 自生成素材验证方向链与控制栏取值 | ✅ 定向 | GPU/导航图四角回读、方向 6 `DisplayCrop`/增强帧朝向已过；真实相机素材仍待验，见 13.2 |
| 19 | **原生设置窗口(W-M5)**:Mica + TitleBar + NavigationView + SettingsCard/五分类/排序/录制/清库/关闭回写 | ✅ | §17;settings 7 项 PASS；真实拖拽/录制/清库/预设增删已由作者 2026-10-18 手验 |
| 20 | INPUT-01:设置窗口独立于主窗口,录制态吞键 | ✅ 定向 | 主窗口 `_root.KeyDown` 收不到设置窗口按键;录制态 `e.Handled` + 失活取消 |
| 21 | **原生工具窗口(P3 收口)**:Mica + TitleBar + NavigationView/7 列 EXIF/DINO 5 瓦片/CSV 导出 | ✅ | §18;tools 5 项 PASS；多格式/文件夹与 CSV 选择器/视觉手感已由作者 2026-10-18 手验 |

### 13.2 剩余事项（2026-10-19 分类）

| 分类 | 剩余事项 | 边界 |
|---|---|---|
| 发布风险 | 未签名/SmartScreen；干净 Windows 全系统、Win10 未验 | 原名/改名共 88 项与模块来源检查不替代干净机；最低声明 Win10 1809 与实测 Win11 不矛盾，仅发布 x64 原生产物 |
| 作者决策 | 最终单文件体积是否接受 | 230,753,372 字节（230.8 MB / 220.1 MiB），已内置 CRT；工程修复通过，干净机门仍待环境 |
| 硬件/素材验收 | 175%/200% DPI、真实跨屏、相机八方向与 EXIF、增强 + 分析长稳 | 150% 与自生成八方向已覆盖；无设备的触控板分流不阻塞 |
| W-M6 验收欠账 | 专项 harness 与 Windows 同格式几何导出未做 | 旧壳已退役，不再建设旧壳 JSON 对拍；按 6F 样机导出并比较 |
| 状态核验/小缺口 | status Windows 现仅 1 格待核验（SCOPE-04）与 6 格代码核对；`INPUT-01` 待判定 | 不是缺功能；`FILTER-08` / `TOOLBAR-11` / `ANALYSIS-11` / `SETTINGS-13` 的读屏类要求已于 2026-10-21 删除，不再挂账 |

> 设置真实拖拽/键鼠录制/清库确认/预设增删，工具真实多格式/文件夹与 CSV 选择器/视觉手感，均已由作者 2026-10-18 手验，不再挂起。模型重复打包已随 6D 消除。早期 #4/#6/#7 主屏缺口已关闭，#14 单文件工程阻塞也已关闭。

### 13.3 独立延期与非目标

- **W-M7 色彩/RAW 原本独立立项**：ICC/广色域/ACM/Win10 回退与 RAW 主图解码未做；RAW 内嵌缩略图、列表与星级写回可用，不是完全不支持 RAW。
- **性能门以后再做，不计发布前置**：原生 `--pv-stress` 接线、p50/p95 基线与性能优化置后。
- P3 主屏、设置与工具已落地；自绘标题栏失去系统最大化按钮的贴靠布局为有意差异。

### 13.4 建议推进顺序

先由作者确认体积与发布风险，再按条件补设备/素材验收及 W-M6 专项、6F 样机几何导出；W-M7 与性能另立项，不重新挂已通过的设置/工具手验。

## 14. 主屏功能对标收口(2026-10-17 第二轮)

作者要求"完善主屏窗口的全部功能对标"。本轮把 §13.2 里属于主屏的功能缺口全部落地,并把"对拍"从人工截图推进到**可回归的定向验收**。

### 14.1 新增/修复

| 项 | 实现 | 契约 |
|---|---|---|
| **导航图(缩放指示器)** | 新增 `WindowsNavigator`:右下角 12 边距、圆角 8、内边距 8、`#9222` 底色;缩略图长边 150(按显示朝向比例)、白色 2 px 可见框、12 号粗体百分比;仅"设置开启 && 非适应态 && 有图"显示,不吃指针(与旧壳一致,旧壳导航图同样不可交互)。缩略图由会话线性 RGBA16F 帧经 Skia 绘到导航尺寸并转 sRGB8(只处理导航像素),EXIF 方向在绘制矩阵里应用;白框位置按 `显示比例 + 平移` 每帧重算。 | 旧壳 `ImageView.axaml:74-107`;`IMAGE-01` |
| **FILM-03 用户接管** | 滚轮 / 指针按下 / 按住拖动 / 触摸操纵开始都调 `CancelLocate()`:撤销排队请求 + 停止动画,并把当前偏移以无动画请求写回,压掉已在途的一帧 `ChangeView`。 | `FILM-03` |
| **减少动态效果** | 每次定位请求读取 `UISettings.AnimationsEnabled`;关闭动画时同一触发条件直接 `ChangeView(..., disableAnimation: true)` 到终点。 | `FILM-03` |
| **FILM-06 写回失败可见** | 共享 `MainViewModel.SetRatingAsync` 在写盘失败(或异常且未写成功)时触发 `RatingWriteFailed`;原生壳在主图区底部显示 4 s 状态提示(卡片星与快捷键评分共用同一路径),写失败不更新星色,因此不会出现"假成功"。 | `FILM-06` |
| **外部打开文件夹 / 定位回归** | `OpenPathsAsync` 先按文件处理,没有文件时把首个目录按文件夹打开(命令行与拖放共用);同时修正一个回归:列表重建曾清掉排队中的定位请求,导致"外部打开非首张"停在列表头部——现在重建只停动画、保留排队请求,接管/隐藏才撤销。 | `FILM-01/02/03` |
| **窗口态** | 最大化/还原/全屏/退出全屏经自绘四按钮走通,退出全屏回到有边框的还原态而不是最大化;状态与 `OverlappedPresenter` 一致。 | `SHELL-02` |
| **细节对齐** | 卡片文件名改用 `DisplayName`、第二行补"完整时间"tooltip(旧壳 `SecondLineToolTip`);右键菜单栏位项改回旧壳文案"缩略图栏";空筛选结果在主图无匹配时给出"无匹配照片"短状态。 | `FILM-05`、`FILTER-01` |

### 14.2 与计划的一处实现偏离

§6 W-M2 原写"导航图在同一交换链内合成"。实际落地改为:**缩略图走 Skia 采样 + 原生控件绘制白框与百分比**。理由:D3D 内合成导航图需要为百分比文本自建 DirectWrite 字形图集与圆角底板,而导航图只有 150 逻辑点的缩略帧,Skia 直接从会话帧采样到导航尺寸的成本与照片分辨率无关(macOS 侧同样是"GPU 小帧 → sRGB8 → 原生图像控件"的形状)。主图交换链保持纯照片表面,不掺 UI。

### 14.3 新增验收(harness 从 13 项扩到 19 项,全部 150% 实机跑通,连续多轮稳定)

- `PASS navigator`:fit 隐藏 / 缩放后显示;像素尺寸 = 导航逻辑尺寸 × 栅格化比;白框在缩略图内、缩小后变大;百分比与 `PhysicalScale` 同口径;关闭设置即隐藏。
- `PASS EXIF/orientation fixtures`:自生成 8 张方向素材(400×300 四象限标记 + 最小 EXIF APP1:方向、f/2.8、1/250、ISO 400、75mm)。逐方向断言:方向进入解码产物、源尺寸不变、导航逻辑尺寸按显示朝向换轴;并且**从主图交换链回读四个象限的线性像素**、以及**从导航图缩略图采样四个象限**,都与预期朝向一致。EXIF 块显示真实取值。
- `PASS filmstrip FILM-03`:动画中接管后偏移不再推进;减少动态效果两次请求终点一致。
- `PASS FILM-01/03`:外部打开非首张定位到目标(偏移 > 100),点选不居中。
- `PASS FILM-06`:只读素材写星失败 → 底部提示可见、文案含"无法写入星级"、星色未变。
- `PASS window states`:最大化/还原/全屏/退出全屏四态往返与 presenter 状态一致。

### 14.4 仍待验(不因本轮通过而关闭)

当轮待验项现按 §13 收口：175%/200% DPI、真实跨屏/相机八方向与 EXIF、增强 + 分析长稳；设置拖拽/录制已由作者 10-18 手验。W-M6 专项与 6F 样机几何导出未做，旧壳 JSON 对拍已失效。



## 15. 主屏 P3 入口落地(2026-10-17 第三轮)

作者要求"主屏还有分析栏和相似聚类等一大堆功能,做完再说",本轮解除 `SCOPE-01` 对 Windows 主屏的限制:

### 15.1 新增

| 项 | 实现 | 契约 |
|---|---|---|
| **相似聚类** | 筛选条新增 90×32 "相似聚类"开关(写回 `Settings.SimilarityPanelExpanded`,跨会话保留);文件栏按旧壳三分区加第二列:`WindowsSimilarityPanel` 带相似项卡片(分数百分比 + 75%~100% 柱条)、空态("尚未提取特征"/"无相似照片")、"提取全部/补齐全部"按钮与进度、错误文案。点选走 `SelectItemCommand` 切主图但保留主列表锚点。 | 旧壳 `SimilarityListView`;`FILM-01` |
| **聚类计算接回** | `SimilarityPanelViewModel.RecomputeAsync` 的原生短路改为"面板展开才计算"(SCOPE-01:切图不隐式推理),面板展开时立即按锚点算一次 + 评估未提取张数。 | `SCOPE-01` |
| **分析栏** | 主窗口第五分区(主图与控制栏之间,两向同构):`WindowsAnalysisPanel` 承载细节预览(中心 + 索尼对焦点,源像素 1:1 方形裁剪)、直方图、DINO PCA / Cosine / 锐度 / 抖动矢量场瓦片、准星、瓦片点击重算 cosine、双击预览对准主图、悬停联动主图绿框。数据链路复用 `AnalysisDataReader/AnalysisComputer/AnalysisResultCache/HeatmapBitmapBuilder/PatchHeatmap/ShakeClassifier`;视图不可见即不读库。(**§16 同步后**:改为实现 `IAnalysisHost` + 共享 `AnalysisPresenter`,Windows 不再自持数据链,细节预览走共享 `DisplayCrop`。) | 旧壳 `AnalysisView`+`AnalysisViewModel`;`IMAGE-01` |
| **增强预览** | 控制栏"增强预览"按钮 + `E`:对会话帧走 `ColorEngine` F16→sRGB8 → `ImageEnhancer.Enhance`(CLHE 保色度)→ 再转线性 F16 上屏;关闭回原片。模式跨切图持续,切图/代理→Full 时按 token 重算。(**§16 同步后**:增强态归 `PhotoLoadSession`,窗口不再自持增强位图。) | 旧壳 `ImageViewModel.ToggleEnhanceAsync` |
| **入口与视觉** | `WindowsIcons` 补 `ToggleAnalysisView`(E9D9)/`ToggleEnhance`(E945);右键菜单补"分析栏"勾选项并在弹出前同步三栏状态;控制栏命令组恢复两枚按钮(旧壳 §12.4 的 120 DIP 差异随之消失)。 | `SHELL-01`、`TOOLBAR-01` |

### 15.2 与旧壳的实现差异(已登记)

1. ~~**诊断瓦片始终基于原片**~~ **已由 §16 同步解决**:上一轮因增强图 CV 路径在冒烟中复现崩溃,临时改为诊断固定读原片(增强只影响主图、细节预览与直方图)。同步共享架构后,`AnalysisPresenter` 直接对增强帧实时重算(与旧壳/Mac/iPad 同一份),冒烟新增"增强态诊断重算"覆盖;此处保留差异历史,不再作为当前实现。
2. **抖动瓦片**用 Skia 把 `ShakeField` 光栅化成 32×8 px 箭头位图(旧壳是自绘控件),颜色/方向/边宽口径相同。
3. **细节预览不因代理档隐藏**:旧壳在代理期显示空占位,原生壳直接裁当前会话帧;分析栏可见会驱动 Full 解码,几帧后自动变成全分辨率裁剪。
4. `ProgressBar` 的旧 `XamlParseException` 已定位为缺根 `App.xaml`/`XamlControlsResources`(W-M5 根因);控件现可用,相似聚类进度仍用两条 `Border` 自绘作视觉定制。

### 15.3 新增验收(harness 19 → 22 项,§15.5 后 23 项,§16 同步后 24 项,150% 实机连续多轮稳定)

- `PASS similarity panel`:开关展开→空态占位→"提取全部"跑完 12 张 480×320 素材→列表出分数卡片→点选切主图且不重排→关闭收起。
- `PASS analysis panel`:可见即出细节预览与直方图;`AnalysisImmediateCompute` 下对未提取照片现算 4 张诊断瓦片;瓦片点击把 cosine 参考点改到 `Cosine 8,8` 并重算;上下/左右两向瓦片尺寸均按主轴均分。
- `PASS enhancement`:CLHE 增强帧上屏、关闭恢复原片、增强产物全部释放。
- §16 同步后新增/加强(总数 24):
  - `PASS input`:NumberPad3→SetRating3(新增检查项;默认次级评分热键依赖这张映射)。
  - `PASS EXIF/orientation`:方向 6 的 `DisplayCrop` 显示朝向象限、增强帧保持 GPU 显示朝向(既有检查内断言)。
  - `PASS enhancement`:增强态诊断瓦片对增强帧重算(既有检查内断言)。

### 15.4 仍待验

增强态 + 分析栏同时开启的**长时间**稳定性(同步前冒烟曾因增强图 CV 路径硬崩溃,15.2 #1 曾临时规避;§16 后共享 presenter 对增强帧重算已过冒烟,长时间交互仍待手测);工具窗口已原生(§18);200%/跨屏/手感与 W-M7 色彩/RAW 不变。

### 15.5 收尾缺陷修复(作者回报"提取后切相似聚类卡死")

作者在 345 张已提取素材的实机上点"相似聚类"开关后进程消失。复现与定位过程、以及顺带发现的两处结构性缺陷:

**根因(硬崩溃)**:旧壳 `AnalysisViewModel` 在原生壳里照常构造并订阅 `IsAnalysisViewVisible` / `CurrentFile` / `ImageVM.SourceBitmap`。
展开分析栏时它走 `DecodedImageAdapter.ToBitmap` 建 Avalonia 位图,而原生壳不初始化 Avalonia 渲染接口 →
`InvalidOperationException: Unable to locate 'Avalonia.Platform.IPlatformRenderInterface'` 抛在 WinUI 回调里,
跨 COM 边界变成 stowed exception(`0xC000027B`,WER 签名 combase `0x80131509`)直接退进程。
**只在库里有提取结果时崩**:空库时 `SetSource` 无数据可建位图,所以冒烟与空库实机都过。
修复(当时):`AnalysisViewModel` 在 `UsesNativePresentation` 下构造器直接跳过全部订阅,`SetSource` / `RefreshHistogram` / `OnTileClicked` 早退;
原生分析栏由 `WindowsAnalysisPanel` 独占。**§16 同步后**旧壳 VM 进一步退化为共享 presenter 的薄适配器(原生展示时 `AvaloniaAnalysisHost.AnalysisVisible` 恒 false),
`WindowsAnalysisPanel` 实现 `IAnalysisHost` 并消费同一 `AnalysisPresenter`;当时"构造期跳过订阅"的止血保留为历史。诊断手段固化为 `NativeStartupLog` 的 `FirstChanceException` 留痕
(这类崩溃不触发 `Application.UnhandledException`,日志里的 `FIRSTCHANCE` 是唯一线索),并给相似面板工厂 / 分析栏可见性切换加了
COM 边界兜底(记录并降级,不再整窗消失)。

**顺带修掉的两处卡死诱因**:

1. `PhotoDatabase.OpenConnectionInternal` 每条连接都执行 `PRAGMA journal_mode=WAL`(库级持久设置)。并发开几十上百连接时互相抢库锁,
   在数 GB 的 `photos.db` 上表现为面板/索引长时间无响应。现在只在 `Initialize` / `DeleteDatabaseAsync` 的单连接上设一次。
2. `SimilarityService.FindSimilarAsync` 原实现"一个候选一个 `Task.Run`":345 张能跑,上千张时会同时压满 SQLite 连接池与线程池,
   并把成百上千个 EXIF 完成回调塞进 UI 队列。现在改 `Parallel.ForEachAsync`(并发度 `ProcessorCount/2` 夹 2~8),
   并按指纹剔除同次曝光的伴侣文件(此前面板会出现 100% 相似的自己,锚点 ARW 的 RAW/HIF 对)。`SimilarityPanelViewModel` 的
   未提取统计改成单飞 + 面板收起先取消在途计算,避免展开/收起反复触发全量 EXIF 扫描。

**验证**:`dotnet build`(0 错 0 警)、`--native --pv-native-smoke` **24 项全 PASS**(§16 同步后复跑;本节当时为 23 项,连跑 2 次)、150% 实机 345 张(已全量提取)与
1395 张图库上"提取 → 相似聚类开关 ×3 → 翻页 → 分析栏开关 ×2"全部存活,分析栏正确显示 DB 派生的 PCA / Cosine / 锐度 / 抖动瓦片与
真实索尼对焦点。Expanded 状态、`LayoutMode` 与热键表在测试后恢复为作者原值。

### 15.6 主屏视觉细节修复(作者回报三处:缩略图描边 / 瓦片圆角 / 分析栏首开错位)

1. **"已缓存"浅蓝描边被缩略图盖掉**:`WindowsThumbnailCard` 把 1 px 描边画在 80×80 槽位 Border 上,而 `Image` 按 Uniform 铺满 80 宽,
   左右两条边直接被图片覆盖(实机只剩上下两段可见)。改为把描边拆成独立一层 `_photoFrame` 压在图片之上,槽位只留底色与圆角;
   描边颜色语义仍由 `IsInCache` 决定(`WindowsLayout.CachedBorder`)。旧壳 `ThumbnailCard.axaml` 同样是"描边在 Border 上、图片在其内",
   待旧壳退役前一并处理,本轮只修原生壳。
2. **分析栏瓦片内容自带圆角 + 拉满方形瓦片**:诊断瓦片与细节预览原来各套一层 `CornerRadius=5` 的内容 Border(移植旧壳 `InnerClip` 口径),
   外框 6 + 内层 5 在 150% 下呈现"卡里再套一张圆角卡"的观感。现在删掉内层圆角(`WindowsLayout.AnalysisTileInnerCornerRadius` 一并删除),
   内容直接贴到外框内沿,圆角只由外框 `CornerRadius=6` 裁切产生。同轮作者二次回报"分析图应是原比例置入,不是填满":
   诊断位图原来是**强制 512×512 方形**再 `Stretch=Fill` 进方形瓦片,照片比例信息被吃掉;现在
   `ToSourceUpscaled(image, aspect)` / `BuildShakeBitmap(field, aspect)` 直接按 `entry.AspectRatio` 生成非方形位图
   (长边 `WindowsLayout.AnalysisDiagnosticLongEdge` = 512,另一向按比例收;抖动箭头用 cellX/cellY 分开算,箭头不被压成正方),
   瓦片侧 `Image.Stretch=Uniform` 自动居中留边。准星与点击 uv 换算仍走 `ContentRect()`(同一 `_aspect` 推同一个矩形),
   实机点选与准星落点逐像素一致;直方图瓦片保持 1:1(旧壳同样是 `AspectRatio="1"`)。
3. **分析栏首开偶发"挤成一坨、大小错位"**:原实现靠"读 `ActualWidth` → `UiDispatcher.Post` 推迟 → 回写每个瓦片的 `Width/Height`",
   只要推迟的那一拍赶在布局给出宽度之前读到 0,瓦片就退回各自内容尺寸(标签药丸大小不一),且事后不保证再有 `SizeChanged` 纠正;
   内层内容 Border 又在 `SizeChanged`(布局回调)里改自己的尺寸,进一步触发布局环。新增 `WindowsAnalysisTilePanel`
   (`Panel` 子类)接管测量与排列:主轴均分、另一向钳制、条带居中,边长完全由布局系统给;边长变化只经 `SideChanged` 异步回调
   去重建细节预览裁剪图,布局回调里不再碰位图与子元素尺寸。
4. **主图绿框在每次换照片时都亮**:`AnalysisPreviewTile` 原来在 `SetImage` / `SetCenter` 结尾无条件 `PushHighlight()`,
   而换图、代理→Full、瓦片尺寸变化都会重建裁剪图,于是绿框每张照片必现。现在瓦片自持 `_isHovered`:只有 `PointerEntered`
   置位后的推送才生效,`PointerExited` 置位归零并清框;重建裁剪图只更新数据、不点亮。面板收起拿不到 `PointerExited` 的场合由
   `ResetHover()` 丢弃悬停态(否则重新展开会把旧绿框补画回来)。与旧壳同口径:`DetailPreview` 也是"高亮态才推 `SetDetailHighlight`",
   `ImageViewModel` 在图片尺寸变化时先 `SetDetailHighlight(null)`。

**验证**:`dotnet build`(0 错 0 警)、`--native --pv-native-smoke` **24 项全 PASS**(§16 同步后复跑;含新增的两向"瓦片等边长"硬断言)、
150% 实机 `TestCase/CV` 进程内连续开合分析栏 4 次,每次 7 张瓦片等边长、外框裁切圆角;首开(进程内第一次展开)与后续开合结果一致。
原比例置入逐像素核对:4:3 素材的瓦片内容带上下留边 23/20 px、居中,点选 (1400,700) 后准星落在 (1399.5,700.5),三张瓦片同一 uv 对齐。
绿框实机四步:开面板(指针在主图)→ 无框;点缩略图换照片 → 无框;指针进入「中心」预览 → 框出现在照片中心对应位置;
离开预览再换照片 → 无框。实机缩略图卡片确认"已缓存"描边四边完整可见。

## 16. 同步共享分支(2026-10-17)

Windows 壳已并入共享基线 `c0567c6`。同步不改变产品范围,只把 §15 的 Windows P3 入口对齐到共享的 analysis/enhancement/orientation/encoding 架构:

- **分析栏**:`WindowsAnalysisPanel` 实现共享 `IAnalysisHost`,消费 `Core/AI/AnalysisPresenter` + `AnalysisTile`;只读库快路径、增强联动重算、未提取回退、直方图(`HistogramRenderer`)与 cosine 全在 presenter,细节预览改共享 `DisplayCrop`。Windows 面板不再直接调用 `AnalysisDataReader`/`AnalysisComputer`/`AnalysisResultCache`,也不再自建即时计算编排;`WindowsAnalysisTilePanel` 只负责瓦片几何。旧 Avalonia `AnalysisViewModel` 退化为薄适配器,原生展示时 `AnalysisVisible` 恒 false。
- **增强预览**:状态从 `WindowsMainWindow` 下移到 `PhotoLoadSession`(`ToggleEnhanced`/`EnhancedDisplay`;切图/代理→Full 重算,压力 Warning 自动关闭);窗口不再持有 `_enhancedDisplay`/`_enhancedSrgb8`/`_isEnhanced`,换图恒 `_photo.SetImage(_session.Image)`。
- **主图编码**:新增 `Core/WindowsPresentationEncodingPolicy`(恒 RGBA16F 线性 scRGB),`WindowsNativeApplication` 注入 `WindowsPresentationDecoder` + policy;`DecodeAsync` 改为共享四参签名(带 `PixelEncoding`),非 `RgbaF16` 显式拒绝,`BytesPerPixel` 删除,`BitmapLoader.PresentationBytesPerPixel` 由 policy 计算。
- **共享修复保留**:Windows 分支对共享层的修复随合并进入——`PhotoLoadSession` 取消重置、`ImageEnhancer` 元数据保留(增强 F16 方向,冒烟在方向 6 用增强帧四角回读覆盖)、`SkiaImageCodec.FromSkBitmap` 源尺寸、`BitmapLoader` 在途取消重试、`ColorEngine.ToScRgb16` 元数据重载;不另开 Windows 分叉。
- **验收**:`--pv-native-smoke` **24 项全 PASS**(150%)。新增覆盖 NumberPad3→SetRating3、方向 6 的 `DisplayCrop` 象限、增强帧保持显示朝向、增强帧诊断重算;§15.2 #1 的"诊断固定读原片"规避已移除。
- **待办更新（10-19）**：现行分类见 §13；W-M7 色彩/RAW 独立延期，DPI/跨屏/相机素材与长稳待条件，W-M6 专项/6F 样机几何导出未做，不再建设旧壳对拍；设置/工具已实现并由作者手验。

## 17. W-M5 设置窗口落地与视觉重构(2026-10-17)

按 [native-rebuild-windows-settings.md](native-rebuild-windows-settings.md) 实现。W-M5 首次落地时误判了一批 WinUI 控件不可用，改用 ListView/Slider/自绘 Border；同日复核发现根因是**缺少根 `App.xaml` / `IXamlMetadataProvider`**，补 `XamlControlsResources` 后已回到方案原结构并完成 Mica/Fluent 视觉重构。

### 17.1 根因(不是控件限制)

| 旧报告 | 实际根因 | 现状 |
|---|---|---|
| `NavigationView` 首次布局缺 `TabViewScrollButtonBackground` | 无 `XamlControlsResources`/XAML 元数据提供器，Fluent 模板未装载 | 已用 `NavigationView` |
| `TitleBar` 同上 | 同上 | 已用 `TitleBar` + 透明 caption |
| `NumberBox` 同上 | 同上 | 数值项已恢复 `NumberBox` |
| `TextBox`/`RichEditBox` `E_FAIL` | 同上 | `TextBox` 只由 `NumberBox` 内部使用，无独立文本页 |
| `InfoBar` 缺 TabView 资源 | 同上 | 清库状态已恢复 `InfoBar` |
| `ProgressBar` `E_FAIL` | 同上 | 控件可用；主屏仍保留自绘进度作为视觉定制 |

修复：根 `App.xaml` 合并 `XamlControlsResources` 并设 `RequestedTheme="Dark"`；`App.xaml.cs` 承接原 `NativeApplication` 的 `OnLaunched`/外部打开/窗口生命周期。页面仍全部 C# 构建，`App.xaml` 只做资源引导。

### 17.2 实现要点

- **窗口**：960×720（min 720×560），`MicaBackdrop` + `TitleBar`（齿轮图标 + 设置，caption 按钮透明），系统标准边框；不进任务栏，单例；主窗口关闭先关设置以回写。
- **尺寸**：激活**前**用主窗口 `GetDpiForWindow` 定物理尺寸；激活后再 `Resize` 不刷新内容岛（实测 `ActualWidth` 停在旧值）。960 DIP 外层对应 945 DIP 内容。
- **导航**：`NavigationView` 五类（`OpenPaneLength=240`，无汉堡/返回），选中态用 WinUI 默认 accent 药丸 + 左缘指示条；覆盖 `NavigationViewContentGridBorderThickness/CornerRadius=0`、`NavigationViewContentBackground=透明`，Mica 平铺内容区。
- **卡片**：`CommunityToolkit.WinUI.Controls.SettingsControls` 的 `SettingsCard`；主题材质/描边/圆角/文字层级由 WinUI 提供，整块列表卡 `Padding=4`。
- **列表**：`ListView.ItemsSource` 直接放 `UIElement` 行（`ContainerContentChanging` 替换 `Content` 不显示）；`ReorderMode=Enabled`；拖后按行顺序逐项 `Move` 回写共享集合。
- **数值**：线程/预载/最多数量/缩放预设用 `NumberBox`（140 宽，Inline spin）；缓存/延时/阈值保留 `Slider`，数值放在 Content 顶部右对齐。
- **录制**：窗口根 `PreviewKeyDown`/`KeyDown`/滚轮/中键；Esc 取消、Delete 清空、冲突色取 `SystemFillColorCautionBrush`；窗口失活取消；主窗口快捷键因独立窗口天然不受影响。
- **清库**：`ContentDialog` 确认 + 共享两段确认命令 + `InfoBar` 状态。
- **入口**：右键菜单「设置…」→ `MainViewModel.OpenSettingWindow` → `NativeWindowPresenter` → presenter；只此一条路。
- **图标**：`WindowsIcons.FluentFontFamily` 用 `Segoe Fluent Icons,Segoe MDL2 Assets` 回退链，五个分类字形。

### 17.3 验收

- `--pv-native-settings` **7 项 PASS**：深色/Mica、`NavigationView`+`TitleBar`+`NumberBox`+`InfoBar` 已加载、五分类/960 外层 945 内容、开关+`NumberBox`+滑条回写、UI 行重排回写、录制开始取消/手势显示、关闭回写。
- `--pv-native-smoke` **24 项全 PASS**（无回归）。
- 截图：`release/native-win-acceptance/settings-page{0..4}.png`（DPI-aware `PrintWindow`，1440×1080）。
- **作者 2026-10-18 手验通过**：真实拖拽排序、键鼠录制、清库确认、预设增删与真实手感；未覆盖的 DPI/文本缩放矩阵按 §13 补验。

### 17.4 与方案的偏差(已修正)

- ~~`NavigationView` → `ListView`；`TitleBar` → 系统 caption；`NumberBox` → `Slider`；`InfoBar` → `Border`~~：已恢复原生控件。
- 窗口尺寸在激活前定，而非 `Loaded` 后。
- Mica 与标题栏为 2026-10-17 视觉重构新增，属于方案 D4/D7 的修订。
- 排序整行拖 + 行尾把手视觉、无前进/后退、无 Apple 映射段——与方案一致。

## 18. 工具窗口落地(2026-10-17,P3 收口)

作者口径:**参考设置窗口的做法和结构,用原生控件复刻工具页功能**。本轮把 Windows 最后一个 P3 缺口(EXIF 详情 / 照片数据统计 / DINO 诊断)做成独立 Win11 工具窗口。

### 18.1 结构与复用

- **独立窗口** `WindowsToolsWindow`:960×720(min 800×560、不进任务栏、可缩放不可最大化),Mica + WinUI `TitleBar`(扳手 `E90F`)+ `NavigationView` 左栏三分类 + `SettingsCard` 页面;窗口壳与设置窗口共用 `WindowsWindowChrome`(深色 caption/DPI 当前置尺寸/居中),页面容器与行原语共用 `WindowsSettingsCard`。
- **导航**复用共享 `ToolsViewModel`:分类切换走 `OpenExifDetail` / `OpenPhotoStats` / `OpenDinoDebug` 写回 `CurrentTool`,共享 `SyncCurrentFile` 的联动条件不变;默认 EXIF(无 EXIF 时回落照片统计),已打开再次请求时恢复上次分类;单例由 `WindowsNativeToolsPresenter` 管理,主窗口关闭一并释放。
- **EXIF 详情** `WindowsExifToolPage`:不带头后缀文件名 + `SelectorBar` 多格式切换 + 7 列分组表格(ID 60 / 名称 2 / 中文名 2 / 值 3,值最多 3 行);订阅 `ExifDetailViewModel` 的格式/分组/加载状态,合并到一次 dispatcher turn 重建。
- **照片数据统计** `WindowsStatsToolPage`:文件夹列表(UIElement 行 + 选中移除)、通配符 `TextBox`、`FileSavePicker` 导出 CSV、取消/进度/状态;文件夹与保存选择器都绑定**工具窗口自身**句柄,避免对话框落在主窗口后面。业务仍全部在 `PhotoStatsViewModel`/`PhotoStatsService`。
- **DINO 诊断** `WindowsDinoToolPage`:直接消费共享 `DinoDiagnosticPresenter`,不经过旧壳 `DinoDebugViewModel`;3 列 DINO patch 瓦片(缩略图/PCA/Cosine)+ 2 列 CV 瓦片(锐度/抖动拖影)+ 全宽刚体拟合文本;点击回传 presenter 重算 cosine 参考点。
- **入口**:主图右键「工具…」+ 控制栏 EXIF 信息块整组 `Tapped`,两者共用 `MainViewModel.OpenToolsWindow` → `NativeWindowPresenter` → `WindowsNativeToolsPresenter`。

### 18.2 抽出的共享控件

| 文件 | 内容 |
|---|---|
| `Native/WindowsDiagnosticTile.cs` | 自 `WindowsAnalysisPanel` 的私有瓦片抽出:正方形 + `ContentHost` 按 `AspectRatio` letterbox + 压暗背景/主图/矢量叠加/准星/药丸标签/占位/底部说明;Measure/Arrange 直接给出几何,不在 `SizeChanged` 里回写子元素尺寸。分析栏与 DINO 页共用。 |
| `Native/WindowsDiagnosticBitmap.cs` | 诊断网格按纵横比最近邻放大到长边 512;两处共用。 |
| `Native/WindowsShakeFieldView.cs` | 32×32 抖动矢量线,颜色统一走 `CvHeatmap.ColorForShake`;分析栏不再用 Skia 光栅化箭头位图。 |
| `Native/WindowsWindowChrome.cs` | 设置/工具窗口共用的深色 caption、任务栏隐藏、DPI 尺寸与居中。 |

### 18.3 与旧壳/macOS 的有意差异

1. **Windows 用设置页结构,不是"列表 + 返回"两态**:三分类常驻左栏,EXIF/统计/DINO 直接是页;macOS/iPad 保留工具列表页。这是"参考设置窗口"的直接结果,也是 Win11 工具的常见形态。
2. **DINO 五张瓦片按 3+2 同宽网格排列,第六格留空;刚体拟合面板占满下方整行**:首版试过 CV 行 2 列 + 面板占第三格,实机(作者 2026-10-17 截图反馈)暴露出两排瓦片宽度不同、无说明文字的缩略图因总高较矮被垂直居中而比同排其它瓦片上沿低约 20px。现两排都用三列、五张瓦片同宽,瓦片统一 `VerticalAlignment=Top` 保证两排正方形上沿齐平;第六格空位保留,刚体拟合文本以全宽卡片放在第二排瓦片之后。
3. **工具窗口最小宽 800**(设置 720):EXIF 7 列表格在 720 下会把 `Compression` 一类单词折断;800 下名称列约 105 DIP,配合 `WrapWholeWords` 只按词换行。
4. **EXIF 表格取 WinUI 主题色**,不再维护旧壳 `#222/#3a3a3a` 第二套灰度;ID 列用 `ControlFillColorDefaultBrush`,分隔线用 `DividerStrokeColorDefaultBrush`。

### 18.4 验收

- `--pv-native-tools`(隔离设置/数据库/日志、自建带 EXIF 的 400×300 素材)**5 项 PASS**,输出 `release/native-win-acceptance/tools-checks.json`:
  1. 深色/Mica/NavigationView + TitleBar/三分类/960 DIP/不进任务栏;
  2. EXIF 6 组 / 25 标签 + 单格式标签(文件名去后缀);
  3. DINO 5 瓦片出图、3+2 同宽网格(206 DIP)且两排上沿齐平、状态 = 文件名、点击瓦片把 cosine 参考点设为 8,8;
  4. 照片统计文件夹行/通配符写回/CSV 导出/移除;
  5. 关闭重开:新窗口、默认 EXIF、共享统计状态保留。
- 主冒烟 `--pv-native-smoke` **24 项无回归**(分析栏抽瓦片后含增强态诊断、两向等边长仍 PASS);`--pv-native-settings` **7 项无回归**(设置窗口改用共享 `WindowsWindowChrome`/页容器后)。
- 150% 实机截图:`release/native-win-acceptance/tools-page-{exif,dino,stats}.png`;最小 800×560 的 EXIF 排版另行目视。
- 主图右键「工具…」与控制栏 EXIF 块点击均实机验证可打开工具窗口。
- **作者 2026-10-18 手验通过**：真实多格式(RAW+JPG/HEIF)切换、文件夹/CSV 选择器与视觉手感；未覆盖的 DPI/文本缩放矩阵按 §13 补验。

### 18.5 验收边界与后续更新

- `SelectorBar` 程序化只覆盖单格式、选择器程序化只覆盖 VM 与列表/导出路径；真实多格式与系统选择器已由作者 10-18 手验补齐，不再挂起。
- 工具窗口已于 10-18 改为普通窗口，进任务栏/Alt+Tab 且可最大化；仍随主窗口关闭。§18.1/18.4 为初轮记录。
- UIA 的 `TransformPattern.Resize` 在本窗口上会导致窗口关闭(疑似 WinUI/UIA 组合问题),抓图用 `SetWindowPos`;这是验收工具的限制,不是产品行为。

### 18.6 作者截图反馈修正(2026-10-17)

作者用真实 `A7C01482.HIF` 截图反馈 DINO 页两处问题:

1. **两排瓦片大小不同**:首版 CV 行用 2 列,第二排瓦片明显比第一排的三列瓦片宽。
2. **高度不对齐**:第一排缩略图瓦片没有底部说明文字,总高较矮,在 Grid 行里被垂直居中,正方形上沿比 PCA/Cosine 低约 20px。

修正:

- CV 行也改三列,五张瓦片按 **3+2** 排列,第六格留空;刚体拟合面板仍全宽放在第二排下方。
- 五张瓦片统一 `VerticalAlignment=Top`,两排正方形上沿齐平;说明文字长短只影响瓦片底部,不再影响正方形起点。
- `--pv-native-tools` 的 DINO 检查新增布局断言:五张瓦片宽度差 ≤1 DIP、两排各自上沿差 ≤1 DIP;当前 150% 下五张均 206 DIP 宽。
- 重新截图:`tools-page-dino.png`(默认窗高)、`tools-page-dino-full.png`(加高窗口显示完整刚体拟合面板)。

