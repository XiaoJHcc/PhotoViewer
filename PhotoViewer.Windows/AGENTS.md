# PhotoViewer.Windows — WinUI 3 原生壳

Windows 头工程:WinUI 3 原生壳。
方案、里程碑与作者待确认项见 [Plans/native-rebuild-windows-p2.md](../Plans/native-rebuild-windows-p2.md)。

**当前状态**:原生壳已具备**五分区主窗口**(筛选 / 胶片条+相似聚类 / 主图 / 分析栏 / 控制栏),主图走 D3D11 + DXGI 合成交换链,
尺寸、边距与配色沿用既有设计口径(2026-10-17 在 150% 缩放下逐项实测,见 [Windows P2-W §12](../Plans/native-rebuild-windows-p2.md))。
标题栏为**自绘四按钮 + 50 DIP 热区自动显隐**(不再使用系统标题栏)。主屏功能对标已完成:缩放指示器(导航图)、
FILM-03 用户接管与"减少动态效果"、FILM-06 星级写回失败窗口内可见、外部打开/拖放文件夹、最大化/全屏/还原闭环、
空筛选结果状态都在原生壳内实现并被 `--pv-native-smoke` 覆盖(见 [Windows P2-W §14](../Plans/native-rebuild-windows-p2.md))。
P3 主屏入口(分析栏、相似聚类、增强预览)已落地(§15),并已**同步到共享分支 `c0567c6`**:分析栏实现 `IAnalysisHost`、消费共享 `AnalysisPresenter`/`AnalysisTile`,细节预览用共享 `DisplayCrop`,Windows 侧不再自持分析数据链与即时计算编排;增强态归 `PhotoLoadSession`;主图解码注入 `WindowsPresentationDecoder` + `WindowsPresentationEncodingPolicy`(恒 RGBA16F 线性 scRGB)(§16)。**设计统一(Phase 6C)**:头工程链接 `PhotoViewer.Shared` 源,`WindowsLayout` 的重复常量改引共享几何/`DesignTokens`;主图缩放/平移/100% 与切图对焦改由共享 `PhotoViewport` 驱动;筛选条位置消费 `MainScreenLayout.FilterBarFrames`;控制栏消费 `ControlBarLayout` 度量并有溢出滚动;菜单/输入/提示条/工具窗/设置页已按 W2–W19 接线(详见 `Plans/native-rebuild-phase-6-unify.md`)。**设置窗口(W-M5)已原生并完成 Win11 视觉重构**:独立窗口 + Mica + WinUI `TitleBar` + `NavigationView` 分类 + 五页 `SettingsCard` 卡片,右键菜单「设置…」入口,`--pv-native-settings` 7 项定向验收通过(§17);**工具窗口已原生**(2026-10-17):Mica + `TitleBar` + `NavigationView` 三分类(EXIF 详情/照片数据统计/DINO 诊断)+ `SettingsCard`,入口为主图右键「工具…」与控制栏 EXIF 信息块,`--pv-native-tools` 5 项定向验收通过(§18);**色彩与 RAW(W-M7)尚未原生**;显隐与命令表沿用共享设置。
已完成 / 待完成总表见 [Windows P2-W §13](../Plans/native-rebuild-windows-p2.md#13-已完成--待完成清单)。

## 文件清单

| 文件 | 职责 |
|---|---|
| Program.cs | 入口:直接调用 `WindowsNativeApplication.Run`;Debug 与 Release 都只进原生壳,命令行路径由原生启动流程打开。 |
| App.xaml / App.xaml.cs | WinUI `Application` + 应用级 `XamlControlsResources`(Fluent 控件模板/主题资源唯一入口);`OnLaunched` 装配门面、`MainViewModel`、主窗口与外部打开。XAML 只承载资源,页面仍全 C# 构建。 |
| Native/WindowsNativeApplication.cs | 原生入口:`Application.Start` + `DispatcherQueueSynchronizationContext`、窗口句柄状态、门面注入顺序(含 `WindowsPresentationDecoder` + `WindowsPresentationEncodingPolicy`)。Application 对象已移到 App.xaml.cs。 |
| Native/WindowsUiServices.cs | `IUiDispatcher`(Normal → Normal、Background → Low，UI 线程后台调用也入队)、同步上下文、`IFilePickerService`(WinRT 选择器 + 窗口句柄绑定 + 过滤器模式规范化)、`IAppResourceProvider`(avares URI → Windows 头工程清单资源，支持单文件发布)。 |
| Native/WindowsLayout.cs | **几何与配色常量唯一来源**：数值优先引用共享 `PhotoViewer.Shared` 几何（`MainScreenLayout`/`FilmstripGeometry`/`ControlBarLayout`/`SimilarityPanelGeometry`/`ExifTableGeometry`/`AnalysisPanelGeometry`）与 `DesignTokens`，Windows 专属口径（标题栏、设置/工具窗口）留在本文件。含栏宽 116 + 1 分隔线、下拉 90×32、toggle 90×27、胶片带宽 159、卡片 90×138(项距 96/144)、EXIF 50×40、评分 30×30、命令 40×40、标题栏 48/热区 50/按钮 36×32、窗口 1200×850(min 560×480)、设置窗口 960×720(min 720×560)/导航 240/标题栏 48/页宽 800/卡片 4 圆角、工具窗口 960×720(min 800×560)/EXIF 7 列 ID 60/单元格 10×8/值最多 3 行/瓦片间距 8 等。harness 反查这里。 |
| Native/WindowsWrapPanel.cs | WinUI 没有 WrapPanel;控制栏三分组"整组换行/换列 + 行内居中"依赖这个自建面板。 |
| Native/WindowsTitleBar.cs | 自绘标题栏:标题文本 + 最小化/最大化/全屏/关闭四按钮，指针进热区显示、移开或窗口失活隐藏；覆盖层背景为 null 只让按钮吃指针，拖动区由主窗口登记为系统 caption。 |
| Native/WindowsMainWindow.cs | 主窗口:去掉系统标题栏与系统按钮(`OverlappedPresenter.SetBorderAndTitleBar(true, false)`)、两向五分区布局、标题栏显隐与物理像素拖动区登记(caption 边界取实际按钮布局)、右键菜单(胶片栏/分析栏/控制栏/增强/工具…/设置…)、空首屏与 300ms 延迟加载提示、窗口内状态提示(`FullError` 与会话 `Notice` 走同一提示条)、拖放/外部打开(按入参取首个可打开项,文件夹也进文件夹模式)、键盘/滚轮 → 共享手势表(按键按 `IsRepeatable` 设闸、精确滚动平移)、命令分发、`PhotoLoadSession` 接线与栏位显隐平台偏好(`PhotoViewer.Native.ShowFilm/ShowControls`)。 |
| Native/WindowsSimilarityPanel.cs | 相似聚类面板:相似项列表(分数百分比 + 75%~100% 柱条)、空态/未提取占位、"提取全部/补齐全部"按钮原地换进度条(自绘两条 Border,见硬约束);行布局宽度取共享 `SimilarityPanelGeometry.RowWidth` 并夹到可用宽一半,列布局固定 110。列表态动作区也显示提取错误(SIM-10)。点选走 `SimilarityPanelViewModel.SelectItemCommand` 切主图但保留主列表锚点。 |
| Native/WindowsAnalysisPanel.cs | 分析栏宿主:实现共享 `IAnalysisHost`,把可见性/当前图/展示帧/即时计算上报给 `Core/AI/AnalysisPresenter`(与 Mac/iPad 同一份);只读库快路径、增强态重算、未提取回退、直方图与准星都在 presenter,本类只做 Windows 瓦片渲染、布局、准星与 hover 联动。细节预览用共享 `DisplayCrop` 从展示帧裁显示朝向 1:1 方块;悬停绿框只在指针停在细节预览瓦片里时推送(重建裁剪图、换图都不点亮,面板收起时丢弃悬停态)。**不再直接调用** `AnalysisDataReader`/`AnalysisComputer`/`AnalysisResultCache`,也不再自建即时计算编排。诊断瓦片改用共享 `WindowsDiagnosticTile`(与工具窗口 DINO 页同一实现),抖动瓦片用 `WindowsShakeFieldView` 矢量叠加而不是 Skia 光栅化;内容矩形按 presenter 的 `AspectRatio` 铺开,圆角只来自外框裁切。 |
| Native/WindowsAnalysisTilePanel.cs | 分析栏瓦片容器:Measure/Arrange 把子项直接排成等边长正方形条带(主轴均分、另一向钳制、条带居中),换向即 `InvalidateMeasure`;边长变化后 `SideChanged` 异步通知细节预览重裁。 |
| Native/WindowsNavigator.cs | 缩放指示器(导航图):右下角缩略图 + 白色可见框 + 百分比;缩略图由会话线性 RGBA16F 帧用 Skia 直接缩到导航尺寸并转 sRGB8(只处理导航像素),既有显示条件(设置开启且非适应态),不可交互;圆角 6、描边 1、底色 60% 均由 `nav.*`/`color.nav.*` 令牌给出。 |
| Native/WindowsNativeAcceptance.cs | 主窗口的定向验收 partial；显式 `--pv-native-smoke` 隔离设置/数据库/日志(每轮清空自有库)并创建自有棋盘/方向素材，验证真实队列顺序、内嵌模型 SHA256 与 DirectML 双输出、两向三尺寸往返 resize、标题栏自动显隐下的 caption 契约、无 EXIF 源尺寸、物理 100% Full 热替换、导航图、**NumberPad3→SetRating3 映射**、八方向 EXIF(主图交换链四角回读 + 导航图四角采样 + 方向 6 `DisplayCrop` 象限与增强帧朝向)与控制栏取值、FILM-03 接管/减少动态效果、FILM-06 写回失败提示、外部打开非首张定位、**相似聚类三态与提取全部**、**分析栏细节预览/直方图/四张诊断瓦片/即时计算/准星重算**（含两向"瓦片等边长"断言）、**增强预览开关与增强帧诊断重算**、**分析栏已提取(DB 派生)路径**、最大化/全屏/还原与关闭释放(共 24 项)。输出 `release/native-win-acceptance/checks.json`，失败退出码 1；不代表完整 W-M6 或跨屏色彩验收。 |
| Native/WindowsNativeSettingsPresenter.cs | `INativeSettingsPresenter`：设置工作副本（`NativeSettingsSession`）、单例窗口、关闭回写；只由共享 `NativeWindowPresenter` 调用。 |
| Native/WindowsSettingsWindow.cs | 设置窗口壳：960×720（min 720×560）、Mica 背景 + WinUI `TitleBar`（齿轮图标、透明 caption 按钮）+ 顶部持久化失败状态行（读源设置的 `PersistenceStatus`，SETTINGS-14）+ `NavigationView` 分类（240）+ 五页 `SettingsCard` 内容、关闭事件；DPI 尺寸/居中/深色 caption 走共享 `WindowsWindowChrome`。`--pv-native-settings-hold` 保持窗口供人工目视。 |
| Native/WindowsSettingsCard.cs | 设置窗口与工具窗口共用的 Win11 行原语：滚动页容器（页宽 800/边距 36）、页面/分组标题、`CommunityToolkit.WinUI.Controls.SettingsCard` 卡片、开关/滑块行、提示、分隔线、把手、图标按钮；颜色/字号/圆角全部取 WinUI 主题资源（`CardBackgroundFillColorDefaultBrush`、`TextFillColorPrimaryBrush`、`ControlCornerRadius` 等），不再维护第二套灰度。 |
| Native/WindowsSettingsList.cs | 可排序列表：`ListView`（`ReorderMode=Enabled`）+ **UIElement 行**（本工程容器内容注入不生效）；拖后把行顺序回写共享集合。 |
| Native/WindowsHotkeyRecorder.cs | 快捷键录制：窗口根捕获键/滚轮/中键，Esc 取消、Delete 清空、冲突重算；录制态吞键。 |
| Native/WindowsSettingsPages.cs / .File / .Preview / .Control / .Exif / .Ai | 五页内容与共享 VM 订阅：格式勾选/重排、同名合并、缓存/预载（NumberBox + Slider）、缩放预设（NumberBox）、布局与快捷键表、EXIF 显示项、AI 阈值/最多数量（NumberBox）/即时计算/清库（InfoBar）。 |
| Native/WindowsSettingsAcceptance.cs | 显式 `--pv-native-settings`：隔离设置下验证深色/Mica、`NavigationView` + `TitleBar` + `NumberBox` + `InfoBar` 已加载、五分类、尺寸、开关/数值框/滑条回写、列表重排回写、录制开始/取消、手势显示、关闭回写（7 项），输出 `settings-checks.json`。 |
| Native/WindowsWindowChrome.cs | 独立工具类窗口的公共外壳：深色 caption（透明按钮）、任务栏/Alt-Tab 可见性（工具窗口开启、设置窗口关闭）、可缩放/最大化开关、按主窗口 DPI 在激活前定物理尺寸并居中。设置窗口与工具窗口共用，避免两份 DPI/标题栏代码漂移。 |
| Native/WindowsNativeToolsPresenter.cs | 工具窗口展示器：单例、懒创建、关闭释放；显示/关闭时写共享 `ToolsViewModel.IsPresented`（TOOLS-01），只由共享 `NativeWindowPresenter` 调用，不持有工具数据。 |
| Native/WindowsToolsWindow.cs | 工具窗口壳：960×720（min 800×560）、Mica + `TitleBar`（扳手图标）+ `NavigationView` 三分类（EXIF 详情/DINO 诊断/照片数据统计）+ 页面宿主；按普通窗口处理（进任务栏/Alt-Tab、可最大化）。导航状态复用共享 `ToolsViewModel`（默认 EXIF，无 EXIF 时可回落照片统计），已打开再次请求时恢复上次分类；关闭即 `Deactivate()` 取消在途 DINO，主窗口关闭一并释放。 |
| Native/WindowsExifToolPage.cs | EXIF 详情页：文件名 + `SelectorBar` 多格式切换 + 按元数据分组渲染 7 列表格（ID 60、名称/中文名 2:2、值 3、值最多 3 行）；订阅共享 `ExifDetailViewModel`，异步加载/格式切换合并重建。 |
| Native/WindowsStatsToolPage.cs | 照片数据统计页：文件夹列表（UIElement 行 + 选择移除）、通配符 `TextBox`、`FileSavePicker` 导出 CSV、取消/进度/状态；文件夹与保存选择器绑定工具窗口句柄。 |
| Native/WindowsDinoToolPage.cs | DINO 诊断页：直接消费共享 `DinoDiagnosticPresenter`，五张瓦片按 **3+2 同宽网格**排列（缩略图/PCA/Cosine + 锐度/抖动拖影，第六格留空），全部 `VerticalAlignment=Top` 保证两排正方形上沿齐平；刚体拟合文本面板占满下方整行。位图按引用缓存，点击回传 presenter 重算 cosine 参考点。 |
| Native/WindowsDiagnosticTile.cs | 分析栏与 DINO 诊断页共用的诊断瓦片（自 `WindowsAnalysisPanel` 提取）：Measure/Arrange 给出正方形，内容按 `AspectRatio` 在内部 `ContentHost` 里 letterbox；支持压暗背景图、矢量叠加层、准星、药丸标签、占位与底部说明；点击只在内容矩形内换算 uv。 |
| Native/WindowsDiagnosticBitmap.cs | 诊断网格位图工具：按纵横比把 8 位派生图铺开并最近邻放大到长边 512，再交 XAML；分析栏与 DINO 页共用。 |
| Native/WindowsShakeFieldView.cs | 抖动矢量场原生控件：32×32 掩膜/方向/边宽/一致性画短线段，颜色统一走 `CvHeatmap.ColorForShake`；分析栏与 DINO 页共用。 |
| Native/WindowsToolsDialogs.cs | 工具窗口自有文件夹/CSV 保存选择器；WinRT 选择器绑定工具窗口自身句柄。 |
| Native/WindowsToolsAcceptance.cs | 显式 `--pv-native-tools`：隔离设置/数据库/日志 + 自有带 EXIF 素材，验证工具窗口深色/Mica/三分类顺序(EXIF→DINO→统计)/进任务栏且可最大化/尺寸、EXIF 分组/标签/单格式、DINO 5 瓦片与余弦参考点、照片统计文件夹/通配符/CSV 导出/移除、关闭重开（5 项），输出 `tools-checks.json`；`--pv-native-tools-hold` 保持窗口供截图与人工目视。 |
| Native/WindowsFilterBar.cs | 排序方式 / 升降序 / 星级筛选 + 筛选后计数;名称与取值语义沿用既有契约(`FILTER-01`)。位置直接消费共享 `MainScreenLayout.FilterBarFrames`(行布局居中、列布局 116 栏内纵排),toggle 90×27 与四色取令牌。 |
| Native/WindowsFilmstripView.cs | 胶片条:`ItemsRepeater` + 卡片池 + 可见区上报 + 350ms 三次缓出定位(`FILM-01/02/04`);用户接管(点选/滚轮/拖动滚动条/触摸)撤销在途动画与排队请求,系统"减少动态效果"直接到终点(`FILM-03`);空筛选结果显示简短状态;滚动区最小高取 `film.band.height`(159)。 |
| Native/WindowsThumbnailCard.cs | 卡片:缩略图 / 文件名(DisplayName) / 拍摄时间(含完整时间 tooltip) / 0-5 星 / 抖动徽标 / 两态描边(`FILM-05`)。"已缓存"描边单独一层压在缩略图**之上**:80 宽的图片按 Uniform 铺满槽位,画在槽位里的描边左右两条边会被图片盖掉。当前项描边走 `_surfaceFrame` **叠加层**(2 DIP 画在 90×138 内边缘,不占布局);纵向坐标对齐 macOS:照片 5..85、文件名 89、第二行 103、星级 116、底部留 8。几何/星色/描边全部取 `film.*`、`color.star.*`。 |
| Native/WindowsControlBar.cs | EXIF·评分·命令三个整体分组,分组尺寸/间距取共享 `ControlBarLayout`,整组换行换列居中,评分方向按布局翻转(`TOOLBAR-01`);包 `ScrollViewer` 保存并夹取偏移(`TOOLBAR-08`);无照片走系统禁用态、评分快捷命令画数字(0=叉)、命令 tooltip/读屏名称含主次绑定。EXIF 信息块整组可点,与主图右键「工具…」共用 `MainViewModel.OpenToolsWindow`。 |
| Native/WindowsPhotoView.cs | GPU 照片表面:合成交换链(F16)、EXIF 方向顶点变换;缩放/平移/适应/100%/导航图数学全部委托共享 `PhotoViewport`(100% = 1 源像素对 1 物理像素,`ToggleFit` 对焦优先,切图按共享语义重新适应/跟随对焦/保持归一化中心);视口上报驱动代理与全分辨率。 |
| Native/WindowsInput.cs | Windows 虚拟键/滚轮 → 共享 `AppGesture`;不改持久化字码。 |
| Native/WindowsIcons.cs | 命令 → Segoe Fluent Icons 字形（字体族用 `Segoe Fluent Icons,Segoe MDL2 Assets` 回退链）；设置分类五个字形、工具分类三个字形（EXIF `E946` / 统计 `E9D2` / DINO `E99A`）与工具窗口标题扳手 `E90F`；未知命令(设置漂移)返回 null ⇒ 不显示也不消费按键,并写一次启动日志;P3 主屏入口命令已登记。 |
| Native/WindowsImageAdapter.cs | 共享 `DecodedImage`(8 位缩略图)→ XAML `WriteableBitmap`;主图不走这里。 |
| Core/WindowsPresentationDecoder.cs | `IPresentationImageDecoder`:`DecodeAsync(IFileRef, int? maxLongEdge, PixelEncoding, CancellationToken)`;Skia/LibHeif 取未旋转 8 位 → skcms 转线性 RGBA16F，源尺寸取解码器真实原始像素而非 EXIF，携带待应用方向；非 `RgbaF16` 显式拒绝，`BytesPerPixel` 已删除(由 policy 决定)。 |
| Core/WindowsPresentationEncodingPolicy.cs | `IPresentationEncodingPolicy`:本阶段恒返回 RGBA16F 线性 scRGB(所有尺寸档/压力);`BitmapLoader.PresentationBytesPerPixel` 与解码准入估算据此计算,W-M7 再按 ICC/广色域扩展。 |
| Core/LibHeifDecoder.cs / Core/WindowsXmpWriter.cs | 缩略图路径的 HEIF 解码、XMP 原位字节写。 |

## 构建事实(坑已固化在 csproj,改动前先读注释)

- TFM `net10.0-windows10.0.19041.0`;`RuntimeIdentifier` 固定 `win-x64`(`LibHeif.Native.win-x64` 仅 x64),
  输出目录是 `bin/<cfg>/net10.0-windows10.0.19041.0/win-x64/`,**不是**旧路径 `bin/<cfg>/net10.0-windows/`。
- WinUI 非打包自包含:`UseWinUI` + `WindowsPackageType=None` + `EnableMsixTooling` + `WindowsAppSDKSelfContained`
  + `DISABLE_XAML_GENERATED_MAIN`(入口自写)。
- **`app.manifest` 必须自己声明 DPI 感知**:本工程自备清单会**替换** Windows App SDK 的默认清单,漏掉
  `dpiAwareness=PerMonitorV2` 时进程是 DPI-unaware(`GetProcessDpiAwareness=0`),在 150% 屏上整套 UI 被系统
  按位图拉伸 —— 表现为控件发虚、边距与窗口对不齐。
- **UI 全部用 C# 代码构建,不写页面 `.xaml`**;根 `App.xaml` 只承载应用级 `XamlControlsResources` 引导,不承载任何页面控件布局(与 macOS 壳的程序化 AppKit 一致)。
- **WinAppSDK 框架包自带 ONNX Runtime(1.23.x)**,与本工程的 `Microsoft.ML.OnnxRuntime.DirectML`(native 1.24.x)撞名;
  csproj 用 SDK 的 `MicrosoftWindowsAppSDKFilesExcluded` 钩子显式排除框架侧副本,详见 csproj 注释 —— 不要删。
- 版本集中在 `Directory.Packages.props`(`Microsoft.WindowsAppSDK`、`CommunityToolkit.WinUI.Controls.SettingsControls`)。
- DINO 模型在 Windows 头工程显式作为 `EmbeddedResource` 编入，原生启动不读取松散文件，也不依赖共享工程的资源打包。

## WinUI 工程硬约束(实测;UI 仍全 C# 构建)

- **`App.xaml` 是 Fluent 控件模板的硬依赖**:`App.xaml` 作为 `ApplicationDefinition` 合并 `XamlControlsResources`,并设 `Application.RequestedTheme="Dark"`。没有它时 WinUI 只加载基础模板,`NavigationView` / `TitleBar` / `NumberBox` / `TextBox` / `RichEditBox` / `InfoBar` / `ProgressBar` 首次布局会缺 `TabViewScrollButtonBackground` / `TabViewButtonBackground` 或 `AcrylicBackgroundFillColorDefaultBrush`。**不要删除或绕过 App.xaml**,也不要退回 ListView / Slider / 自绘 Border 的替代实现;页面内容仍全部在 C# 中构建。
- `Application.Resources` 只有在 `App.InitializeComponent()` 之后可用;XAML 引导之前直接读/写会抛 `0x8000FFFF`。
- 深色外观:`App.xaml` 设 `Application.RequestedTheme="Dark"`(Mica 与主题资源取深色),窗口根元素再设 `ElementTheme.Dark`。主窗口仍为自绘标题栏;设置窗口用 `MicaBackdrop` + `ExtendsContentIntoTitleBar` + `TitleBar` + `SetTitleBar`,caption 按钮背景设透明与 Mica 融合。
- **`OverlappedPresenter` 没有全屏 API**:全屏 = `SetBorderAndTitleBar(false, false)` + `Maximize()`,退出时 `SetBorderAndTitleBar(true, false)` + `Restore()`。
- **自绘标题栏的按钮悬停色要改写主题键**:WinUI 的 `Button` 模板用 `ButtonBackground*` 画刷覆盖 `Background`,只设 `Background` 会被默认亮色顶掉;按元素写 `button.Resources["ButtonBackgroundPointerOver"]` 等键才生效。覆盖层根元素背景必须为 **null** —— 设成透明画刷会吃掉顶部 48 DIP 的全部指针,筛选条与照片手势全被吞掉。
- **`ProgressBar` 在 App.xaml 后可用**:旧结论来自早期缺 `App.xaml`/`XamlControlsResources` 的工程状态;主屏进度条仍保留自绘 Border(卡片内布局/视觉定制),设置页未使用 `ProgressBar`。
- **设置窗口原生控件已验证**:`NavigationView`、`TitleBar`、`NumberBox`、`InfoBar`、`TextBox`、`ContentDialog`、`ListView`、`ComboBox`、`Slider`、`ToggleSwitch`、`CheckBox`、`Button`、`FontIcon` 均可用;没有独立文本输入页,`TextBox` 只出现在 `NumberBox` 内部。
- **设置卡片**:用 `CommunityToolkit.WinUI.Controls.SettingsControls` 的 `SettingsCard`;整块列表/滑块行必须 `ContentAlignment=Vertical` + `HorizontalContentAlignment=Stretch`(默认 Right 会按自然宽度右对齐,列表缩到右侧);列表卡 `Padding=4`。颜色/字号/圆角全部取 WinUI 主题资源,不再新增灰度。
- **`NavigationView` 内容岛**:WinUI 默认给内容区 1px 圆角边框;设置页/工具页要平铺 Mica 时覆盖 `NavigationViewContentGridBorderThickness=0`、`NavigationViewContentGridCornerRadius=0`、`NavigationViewContentBackground=透明`。WinUI 1.8 的 `NavigationView` **没有 `PaneBackground` 属性**(编译错误);左栏背景走主题资源。
- **工具窗口复用设置页控件**:EXIF 格式切换用 `SelectorBar`（`Items` 放 `SelectorBarItem`，`Text` 显示扩展名、`Tag` 挂 `FormatEntry`），页面容器/卡片继续用 `WindowsSettingsCard`；工具窗口与设置窗口共用 `WindowsWindowChrome` 的 DPI/深色 caption 逻辑。**工具窗口最小宽 800**：720 下 EXIF 7 列表格会把英文单词折成两截；单元格左右内边距 8、名称/中文名用 `WrapWholeWords`，值列最多 3 行。
- **诊断瓦片**：`WindowsDiagnosticTile` 继承 `Panel`，在 Measure/Arrange 里把正方形与 `ContentHost` 的内容矩形直接给出（不在 `SizeChanged` 里回写子元素尺寸）；诊断网格用 `WindowsDiagnosticBitmap` 长边 512 最近邻放大，抖动矢量用 `WindowsShakeFieldView` 矢量线，不再走 Skia 光栅化。工具页与主窗口分析栏共用这一套，改瓦片时两边一起验收。
- **工具窗口的 WinRT 选择器**必须绑定**工具窗口自身**句柄（`WindowsToolsDialogs` 在窗口构造后读 `WindowNative.GetWindowHandle`），否则文件夹/CSV 保存对话框会相对主窗口弹出。
- **`SettingsCard` 的 `Header` 不撑满**:需要右对齐的数值/滑块放进 `Content`(垂直布局)再右对齐,不要把需要撑满的 Grid 放进 `Header`。
- **`NumberBox`**:`SpinButtonPlacementMode=Inline`,宽度 140(`WindowsLayout.SettingsNumberBoxWidth`),否则 `33.333` 一类值会被内联按钮挤掉;缩放预设按 1 位小数显示。
- **`TitleBar` 与 caption**:`ExtendsContentIntoTitleBar=true` + `Window.SetTitleBar(titleBar)`,`AppWindow.TitleBar` 背景/按钮背景设透明;`PreferredHeightOption` 仍抛 `0x8007139F`,不设置。
- **内容岛尺寸**:`AppWindow.Resize` 在窗口激活后再调用不会让 XAML 内容重新布局(`ActualWidth` 停在旧值);设置窗口在 `Activate()` **前**用主窗口 DPI(`GetDpiForWindow(主窗口句柄)`)定物理尺寸,首帧即按 960 DIP 布局。
- **ListView 自定义行**:`ContainerContentChanging` 里替换 `ItemContainer.Content` 不显示(`Handled=true` 也一样);可行做法是 `ItemsSource` 直接放 `UIElement` 行,事件里只改容器 `Padding/MinHeight/对齐`。拖拽重排后按行顺序回写共享集合。
- `ScrollViewer.ZoomMode` 必须设 `Disabled`,否则虚拟化内容的测量用无限尺寸。
- `Border` / `ScrollViewer` 在 WinUI 3 里是 **sealed**,不能继承;自定义容器只能继承 `Panel`、`ContentControl` 或 `Grid`
  (导航图因此是 `Grid` + 内部 `Border`,不是 `Border` 子类)。
- **`Border.Width` 含 `BorderThickness`**:把内容宽直接写在外框上会让内容少 1~2 DIP(文件栏内容因此从 116 变 115,胶片条卡片被压窄)。当前文件栏宽度交给内容计算,不再写死外框宽;新增带边框占位时按"内容宽 + BorderThickness"算,不要把内容宽当外框宽。**缩略图卡片已踩过**:90×138 内层占位面直接在 `Border` 上画 2 DIP 当前项描边时,内容被压成 86×134,且描边透明的那一圈露出栏位底色,未选中卡片看起来比选中卡片小一圈(2026-10-18 作者目视发现)。现把当前项描边放到 `_surfaceFrame` 叠加层(不占布局),90×138 整块可见;纵向坐标同时对齐 macOS(照片 5、文件名 89、第二行 103、星级 116)。
- 本工程根命名空间 `PhotoViewer.Windows` 会遮蔽 `Windows.*`(共享工程里另有同名的 `Windows` 命名空间,分属两个程序集):
  在 `namespace PhotoViewer.Windows.Native` 里写 `Windows.UI.Color` 时 `Windows` 会先绑定到本工程命名空间,`.UI` 不存在而编译失败。
  统一在文件顶部(命名空间声明之前)`using Windows.UI;` + 裸 `Color`(`Colors` 类反而取不到,用 `Color.FromArgb`)。
- `DispatcherQueueTimer` 在同时 `using Windows.System;` 的文件里会二义,显式写 `Microsoft.UI.Dispatching.DispatcherQueueTimer`。
- **布局回调里不要改子元素尺寸**:`SizeChanged` 里直接改尺寸会触发布局环(Windows fail-fast)。分析栏瓦片的边长因此
  由 `WindowsAnalysisTilePanel` 的 Measure/Arrange 直接给出(布局系统给多少就用多少,子项不再有自己的 `Width/Height`),
  换向或窗口变形只 `InvalidateMeasure`。**禁止**回到"读 `ActualWidth` → `UiDispatcher.Post` 推迟 → 回写子项尺寸"的老写法:
  那一拍只要赶在布局之前读到 0,瓦片就退回各自内容尺寸(表现为"挤成一坨、大小错位",偶发),而且事后可能不再有
  `SizeChanged` 来纠正。边长变化后的副作用(细节预览重裁)用面板的 `SideChanged` 异步回调,不在布局回调里碰位图。
- `WriteableBitmap` **只能在 UI 线程创建**(后台线程会 `RPC_E_WRONG_THREAD`);Skia 降采样/直方图渲染放后台,回来再建位图。
- 分析栏现算的 CV/DINO 推理用 `SemaphoreSlim` 串行(两个并发 DirectML 推理会崩进程),分析图在现算期间持 `DecodedImage` 租约,
  避免主图热替换把像素缓冲归还进池。
- **分析栏不走旧壳适配层**:共享层已不再包含分析栏显示适配器；原生壳由 `WindowsAnalysisPanel` 实现 `IAnalysisHost`、直接消费共享 `AnalysisPresenter`，分析位图只在原生适配器里转换。**不要**把共享展示适配器重新引入原生壳:一旦调用到旧 UI 位图接口，WinUI 回调会抛 `InvalidOperationException`，
  跨 COM 边界变成 `0xC000027B` 直接退进程(WER 签名:combase `0x80131509`)。诊断要点:这类崩溃**不会**触发
  `Application.UnhandledException`,`native-startup.log` 里的 `FIRSTCHANCE` 行是唯一线索(已装钩子,限流 200 条)。
  实机复现顺序:先提取特征 → 再展开分析栏;空库不崩,是因为 `SetSource` 没有数据可建位图。
- 导航图缩略图**不走 D3D 回读**:直接把会话的线性 RGBA16F 帧包成 `SKImage`,用 Skia 绘到导航尺寸的 sRGB8 `SKBitmap`
  (只处理导航像素,成本与照片分辨率无关),再由 `WindowsImageAdapter.ToSource(width, height, span)` 交 XAML。
  EXIF 方向在绘制矩阵里应用,口径与顶点着色器一致。文字、圆角与可见框由原生控件绘制。
- Git Bash 调 `dotnet publish` 必须用 `-p:Foo=bar`(写 `/p:Foo=bar` 会被 MSYS 改写成路径导致 `MSB1008`);
  PowerShell 脚本传参数组用空格分隔的字符串(逗号数组会被 bash 吞成一个 token)。

## 运行与验收

- 运行:`dotnet run --project PhotoViewer.Windows -c Debug --`(可跟图片路径或文件夹路径直接打开);Debug 与 Release 都是原生壳。
- 主屏 P3 入口:右键菜单"分析栏/缩略图栏/控制栏"勾选、筛选条"相似聚类"开关、控制栏"分析栏/增强预览"按钮(快捷键 Ctrl+Oem… 见设置;增强默认 `E`);工具窗口入口为主图右键「工具…」与控制栏 EXIF 信息块点击。
- 定向验收:`Check Windows Native` Task（或从仓库根运行 `dotnet run --project PhotoViewer.Windows -c Debug -- --pv-native-smoke`）；当前设备 150% 缩放(PerMonitorV2)已过，**24 项**覆盖两向三尺寸 resize、caption 契约、物理 100% 升级、导航图、NumberPad3→SetRating3、八方向 EXIF(主图四角回读 + 导航图四角 + 方向 6 `DisplayCrop`/增强帧朝向)、FILM-03、FILM-06、外部打开定位、相似聚类(空态→提取全部→列表→点选)、分析栏(预览/直方图/即时计算/准星 cosine/两向 + 已提取 DB 派生路径)、增强预览(含增强帧诊断重算)、窗口四态与文件选择器；200%/175% DPI、真实跨屏、真实相机八方向素材、系统色彩、增强态 + 分析栏同时开启的长时间稳定性与真实鼠标手感仍待验；W-M7 色彩/RAW 与 W-M6 专项 harness 仍未做。
- 设置窗口验收:`--pv-native-settings`(隔离设置/数据库,7 项:深色/Mica、NavigationView+TitleBar+NumberBox+InfoBar 已加载、五分类/尺寸、开关+数值框+滑条回写、列表重排、录制开始取消、手势显示、关闭回写,输出 `settings-checks.json`);`--pv-native-settings-hold` 保持窗口供截图与人工目视。**作者 2026-10-18 手验通过**(真实拖拽排序、键鼠录制、清库确认、预设增删);触控板本机无设备未验。
- 工具窗口验收:`--pv-native-tools`(隔离设置/数据库/日志 + 自有带 EXIF 素材,5 项:深色/Mica/NavigationView+TitleBar/三分类/960 DIP、EXIF 6 组/25 标签、DINO 5 瓦片与余弦参考点、照片统计文件夹/通配符/CSV 导出/移除、关闭重开,输出 `tools-checks.json`);`--pv-native-tools-hold` 保持窗口供截图与人工目视。**作者 2026-10-18 手验通过**(真实多格式切换、文件夹/CSV 选择器、视觉手感);截图 `release/native-win-acceptance/tools-page-{exif,dino,stats}.png`(150%)。
- 截图验收方法:窗口设成**指定物理尺寸**截图后按 1.5 比 DIP 换算。抓图脚本必须先把**自身**设成 Per-Monitor-V2 感知,否则 Win32 会把所有矩形按 96/dpi 虚拟化,测量数据不可用。
- 辅助脚本(均在 `bin/`,被忽略不进库):`runcheck.ps1`(启动 + 存活性/窗口 + 抓窗口位图 + 收尾)、`compare/wincheck.ps1`(按指定物理尺寸抓窗口/客户区位图)、`compare/profile.py` / `bands.py`(按像素剖面核对几何)。
  `wincheck.ps1` 的 `-HoverY`(直接 `SetCursorPos`)只能移动光标,**不会**触发 WinUI 的 `PointerMoved` —— 要验标题栏自动显隐必须用真实输入注入(本轮用 KimiCU 的 click/drag)。
- 原生壳单文件发布尚未打通(2026-10-17 Debug 复核:产物缺 `PhotoViewer.Windows.pri`,`Application.Start` 抛 `0x80040111`;多文件发布 + `--pv-native-settings` 通过)。Debug 与 Release 现在都进原生壳,依赖单文件发布前仍需单独修 PRI/运行时打包。
- 发布:仍走 [DEV.md](../DEV.md) 的 `Publish Windows EXE (Release)`。
