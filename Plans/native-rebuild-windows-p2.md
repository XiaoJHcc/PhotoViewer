# Windows 原生主窗口方案(P2-W)

> 2026-10-16 立项。隶属总纲 [native-rebuild-master-plan-1.md](native-rebuild-master-plan-1.md) §6 **Phase 4**,做法平移 macOS P2(见 [native-rebuild-phase-2-ui.md](native-rebuild-phase-2-ui.md) 与 [PhotoViewer.Mac/AGENTS.md](../PhotoViewer.Mac/AGENTS.md))。
> 作者要求:**用 Windows 原生方式把主窗口内容重构成原生,尽可能与旧 Avalonia 设计对标**。设置页、分析栏、聚类、工具页按 macOS 同样口径延期。
> 跨平台对账真源:[Design/ui-contract.md](../Design/ui-contract.md)(Windows 列在 W-M6 补齐)。

## 0. W-M0 可行性闸门:结论(2026-10-16 已过)

在 `PhotoViewer.Desktop` 上原地验证三项,全部通过:

| 闸门 | 结果 | 证据 |
|---|---|---|
| ① 同一工程加入 WinUI 3 后,旧 Avalonia 壳仍能构建与启动 | ✅ | `dotnet build PhotoViewer.Desktop` 0 错误 / 4 条既有 NU1903;`--legacy-ui` 启动窗口标题 `PhotoViewer 图片查看器` |
| ② 自写 `Main` + `Application.Start` 能起原生窗口 | ✅ | 窗口标题 `PhotoViewer`,`PrintWindow` 导出 `bin/Debug/spike-window.png`,深色 Fluent 按钮/下拉/复选框渲染正常 |
| ③ 自包含单文件发布产物能启动 | ✅ | 单文件 exe **239 MB**(`PublishSingleFile` + 压缩 + 自包含 WinAppSDK),`--native` 启动并渲染同一深色界面(`bin/Debug/publish-native.png`);对照现役 Release **166 MB**(0.7.1) |

### 0.1 定下来的构建配置(踩过的坑,不要重走)

`PhotoViewer.Desktop.csproj`:

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

### 0.2 三条硬事实(非 XAML 工程必须先知道)

1. **`Application.Resources` 在构造期完全不可用**:读(get_Resources)或写(set_Resources)都抛 `COMException 0x8000FFFF`。只能在 `OnLaunched` 之后访问。
2. **`XamlControlsResources` 不需要,且构造即崩**:非 XAML 工程里 `new XamlControlsResources()` 抛
   `Cannot find a resource with the given key: AcrylicBackgroundFillColorDefaultBrush`。
   WinUI 1.8 的控件样式由框架自带 —— 不并入任何字典,`Button`/`ComboBox`/`CheckBox` 已渲染为标准 Fluent 外观(见 spike 截图)。**不要照抄 XAML 模板里的 App.xaml 写法。**
3. **深色外观按元素设置**:`Application.RequestedTheme` 对已建元素树不生效(实测控件仍为浅色);把根元素 `RequestedTheme = ElementTheme.Dark` 才生效(实测深色控件)。窗口标题栏/系统按钮颜色另需 `AppWindow.TitleBar` 显式设置(隐藏式标题栏在 W-M1 处理)。

### 0.3 其它 W-M0 观测

- WinUI 原生启动失败是 stowed exception(`0xC000027B`),**无控制台输出**。因此新增 `Native/NativeStartupLog.cs`(写 `%LOCALAPPDATA%\PhotoViewer\native-startup.log` + 未处理异常钩子);没有它无法区分"起不来"与"起来后崩"。这是常驻诊断,不是临时脚手架。
- 固定 `RuntimeIdentifier` 后 Debug 输出路径变为 `bin/Debug/net10.0-windows10.0.19041.0/win-x64/`;引用旧路径的 `.run/PhotoViewer.Desktop.run.xml`(本就停留在 `net9.0-windows`)与脚本需在 W-M1 同步。
- 未显式设尺寸时 WinUI 窗口按屏幕宽度开(实测 1920×1015);W-M1 必须显式 `AppWindow.Resize`。
- Git Bash 里 `dotnet publish` 必须用 `-p:Foo=bar`,写 `/p:Foo=bar` 会被 MSYS 改写成路径导致 `MSB1008`(PowerShell 脚本不受影响)。
- **体积**:自包含发布目录(未单文件化)约 **563 MB** —— 含 .NET 运行时、WinAppSDK 解包内容(101 MB)、ONNX/DirectML、Avalonia 全系;单文件压缩后 **239 MB**(脚本另删 `*.pdb`,实测这些 pdb 约 111 MB)。对照现役 0.7.1 Release 单文件 166 MB(+44%)。体积增量来自 WinAppSDK 自包含,后续可按需裁剪未用组件(Widgets/WebView2 投影等);是否接受由作者定夺。

## 1. 范围与非目标

**本轮做**
1. `Program` 原生/旧壳分流(Mac 同构),原生模式不创建任何 Avalonia 对象。
2. 原生主窗口:隐藏式标题栏、两向布局、筛选条、胶片条、主图区、控制栏(EXIF/评分/命令)、右键菜单、空首屏提示、外部打开与拖放。
3. 主图渲染:GPU 表面 + EXIF 方向顶点变换 + 缩放/平移/100% + 代理图→全分辨率热替换 + 导航图。
4. 输入:鼠标/键盘/滚轮 → 共享 `AppGesture` → 共享命令表 → 原生壳命令分发。
5. 原生验收 harness(隔离设置/数据库、几何 JSON 对拍、程序化断言)。

**本轮不做**
- 分析栏 / 相似聚类 / 工具页 / 增强预览(P3):入口隐藏、配置保留(`SCOPE-01`)。
- 色彩正确性(ICC 广色域、ACM 探测、Win10 烘焙回退、`DXGI_COLOR_SPACE_RGB_FULL_G10_NONE_P709` 标签验证)→ W-M7;本轮只保证**与旧壳行为一致**。
- Windows RAW 解码(`Sdcb.LibRaw`):既有缺口(Windows 现在完全没有 RAW 解码器),随 W-M7 或单独立项。
- 原生设置窗口 → W-M5;在此之前原生壳不显示设置入口(`SCOPE-01`)。
- Avalonia 退役(总纲 Phase 6)。

## 2. 选型

| 维度 | 结论 |
|---|---|
| UI 框架 | WinUI 3(Windows App SDK 1.8),非打包自包含 |
| 主图表面 | `SwapChainPanel` + 合成 DXGI 翻转链,`R16G16B16A16_FLOAT`(与 Metal 侧同口径) |
| UI 构建方式 | **C# 代码构建,不写 `.xaml`**(与 Mac 的程序化 AppKit 一致;规避与 Avalonia 的 XAML 编译目标冲突,几何常量可被 harness 直接断言) |
| 头工程 | `PhotoViewer.Desktop` 单工程 + 运行期开关(与 `PhotoViewer.Mac` 结构一致) |

**否决项**:WPF(D3D 只能走 D3DImage/HwndHost,拿不到干净的 `DXGI + scRGB 标签` 路径;仅在 W-M0 失败时才考虑);Win32 + Direct2D 全手绘(结构最像 Mac 壳、零打包风险,但下拉框/菜单/滚动条/DPI/无障碍全自建,工作量显著更大);半原生(总纲 §3 已否决)。

## 3. 现状事实

- 改造前 `PhotoViewer.Desktop/Program.cs` 只有 Avalonia 一条路径;平台实现仅 `Core/LibHeifDecoder.cs` 与 `Core/WindowsXmpWriter.cs`。
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
PhotoViewer.Desktop/
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

**W-M5 原生设置窗口(SETTINGS-01/02)**
- `WindowsNativeSettingsPresenter : INativeSettingsPresenter` + `NativeSettingsSession` 工作副本 + 关闭回写,不改 JSON 字段名/默认值。
- 五分类分组表单;控制列表行 = 显隐 + 名称 + 主/次快捷键 + 拖动重排(同一行);录制与冲突高亮;AI 页 P3 项禁用且保留原值;清库二次确认。
- **门**:五分类渲染、动作往返、关闭回写重开恢复、隔离设置、窄宽不溢出。

**W-M6 验收 harness 与文档同步**
- CLI(与 Mac 同形):`--pv-native-smoke` + `--pv-native-layout` / `--pv-native-filmstrip` / `--pv-native-input` / `--pv-native-toolbar`,以及 `--legacy-ui --pv-legacy-layout`;隔离设置/数据库落 `release/native-win-acceptance/`,**不写用户照片、不动用户设置**。
- 几何对拍:3 尺寸(1200×850 / 830×650 / 700×480)× 2 方向,旧壳 JSON ↔ 原生实测 `Exact = 0.01`。
- 帧导出:`Windows.Graphics.Capture` 或 `PrintWindow`(权限受限时如实记录,不冒充通过)。
- 文档:本文、`Plans/native-rebuild-phase-4-windows-acceptance.md`、`Design/ui-contract.md` Windows 列(**编译通过≠验收通过**)、根 `AGENTS.md` §4 平台矩阵 + 模块索引、新建 `PhotoViewer.Desktop/AGENTS.md`、`Plans/native-rebuild-handoff.md`、`DEV.md` 增 `Debug Windows (Legacy UI)`。

**W-M7(独立立项)**:色彩正确性(ICC/nclx、ACM 探测、Win10 显示器 ICC 烘焙、跨屏重标签)+ Windows `LibRawDecoder`(`Sdcb.LibRaw`)。

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
(筛选条 / 胶片条 / 主图 / 控制栏),默认左右布局,实机 1200×850 截图见 `PhotoViewer.Desktop/bin/Debug/first-screen.png`。

已落地并与旧壳对标的几何:文件栏 116(+1 分隔线)、筛选下拉 90×32 与间距 5、卡片 90×138(缩略图 80、四周 3 占位)、
EXIF 块 50×40、评分 30×30(纵向自下而上 0→5)、命令 40×40、栏底 `#111111` 与 1px `#333333` 分隔线。
命令分发**复用共享 `ControlViewModel`**(Open/Previous/Next/Exchange/SetRating 与旧壳同一份实现),
只有视口相关命令(Fit/Zoom*）由壳实现;滚轮与键盘统一走共享手势表,P3 命令既不显示也不消费按键。

**本轮新增的 Windows 侧硬约束**(详见 [PhotoViewer.Desktop/AGENTS.md](../PhotoViewer.Desktop/AGENTS.md)):
`ProgressBar` 不能进虚拟化卡片(会 `E_FAIL` 整窗崩溃,已二分定位,加载态改静态细条);
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
