# PhotoViewer.Shared — 跨壳共享源（几何 / 命令表）

`PhotoViewer.Shared/` 是**纯源文件目录**（不是独立 csproj/程序集）。`PhotoViewer.Mac`、`PhotoViewer.iOS`、`PhotoViewer.Android`、`PhotoViewer.Windows` 四个头都以 `<Compile Include="..\PhotoViewer.Shared\**\*.cs"/>` 链接编译，并以 `<Using Include="PhotoViewer.Shared"/>` 引入命名空间。

**硬规则**：本目录只允许纯 C#（`double` 数学 / 记录 / 静态表），不得出现 AppKit、UIKit、Android 或 Windows 平台类型，也不得引入任何 UI 框架依赖。平台事件解包、原生视图、坐标换算留在各 head。

**坐标空间**：共享几何一律以「点」为单位。Apple 头直接用逻辑点；Android 头**把 dp 当点**（`View` 的 px 只在边界换算，`PhotoViewport.ViewWidth/Height` 传 dp、`BackingScale = DisplayMetrics.Density`）。100% 口径仍是 1 源像素对 1 屏幕物理像素。

**迁移门**：改动共享几何后必须复跑 Mac / iOS / Android 的原生验收套件；Windows 至少通过 `Tools/WindowsTypeCheck` 的类型检查与 Windows 原生冒烟。

| 文件 | 职责 |
|---|---|
| PhotoViewport.cs | 视口纯数学（纯 double）：适应缩放（`FitScale`）、锚点缩放与对焦点居中、平移钳制、导航图尺寸/可见范围（`NavigationSize`/`NavigationViewport`）、`NeedsFull`（物理 100% 或代理不足）；`CenterOnNormalizedPoint(u,v)` 保持当前缩放、把归一化图像点移到视口中心（导航图白框拖动，IMAGE-06）；`ApplyGestureView(scale, panX, panY)` 供宿主手势（iOS `UIScrollView` 的 zoomScale/contentOffset）直接写入并统一钳制；`ApplyTexture(textureWidth)` 供**同一张照片的代理↔Full 热替换**只更新纹理宽度、不碰缩放/平移；`ApplyImage(...)` 供**切图**沿用原 Avalonia `OnBitmapChanged` 语义（适应态重新适应 / 跟随对焦时保持倍率并居中到新照片对焦点 / 否则保持倍率与同一归一化中心点，几何基准恒为全分辨率 `SourceWidth/Height`）；`ToggleFit(anchor?)` 是控制栏 Fit 命令与双击的共同语义（已放大 → 回适应；适应态无锚点且照片有对焦点 → 以对焦点为中心到物理 100% 并开启跟随；否则以锚点/视口中心放大；物理 100% 不高于适应倍率的小图保持适应）；`CenterUV` 与 `IsViewCenteredOnFocusPoint`（两轴 5% UV）在每次缩放/平移后重算 `FollowsFocus`。**100% 口径**：原生视口 = 1 源像素对 1 屏幕物理像素（`ZoomToPhysical`/`ToggleFit` 用 `1 / BackingScale`）；原 Avalonia `Scale = 1` 是 1 源像素对 1 逻辑点（Retina 上等于 200% 物理像素），差异登记在 [IMAGE-02](../Design/spec/image.md)。**适应区与视图区分离**：`SetFitInsets(EdgeInsets)` 记录四边避让内边距（不自动改缩放/平移，由调用方随后重新适应或钳制），`FitScale`/`FitToView`/`ClampPan`/视口中心语义（`CenterUV`、`ZoomBy`/`ZoomToPhysical` 无锚点、`ToggleFit` 无锚点、切图相对平移、`NavigationViewport`）一律取适应区（`FitWidth`/`FitHeight`）与适应区中心；照片自身仍可绘制满视图，放大后的平移极限让照片边缘正好落在适应区边缘；四边内边距为零时适应区即整个视图，全部公式与引入适应区之前逐位等价。 |
| MainScreenLayout.cs | 主屏分区基础常量与 Windows 旧几何：文件栏内容宽 116（无遮挡基准）、筛选条控件几何（三下拉 90×32 / 90×27、间距 5、外边距 10）、文件栏与控制栏主轴长度、两向分区矩形。四端原生壳的分区都使用 `ShellPaneLayout`、筛选使用 `WideFilterLayout` / `MobileFilterLayout`；旧筛选条几何 `FilterBarFrames` / `FilterBarMainLength` 已无壳消费，只剩共享契约覆盖。平台安全区与标题栏让位留在各 head。 |
| ShellPaneLayout.cs | SHELL-01 / SHELL-11 宽形态浮层分区：`SafeInsets` 按方向归一（横屏不为底部手势条保留空间），`Partition` 在 `MainScreenLayout.Partition` 之上产出栏位背景（铺窗口缘）、栏位内容（收布局安全区，列布局可显式给胶片条起点与宽度）、分隔线、主图、分析栏与工具区背景/控制内容八个矩形；分析栏与控制栏相邻时消除内部 1 点分隔线、背景取连续并集。iOS / macOS / Android / Windows 宽形态与各自验收共用这一份，手机形态仍走 `PhoneScreenLayout`。 |
| FilmstripGeometry.cs | 胶片栏几何（96×144 卡片、10/5/10 内边距、横向首项居中、文档长度、可见项范围、FILM-02 居中偏移）与 `FilmstripAnimation`（350ms 三次缓出曲线、进度与 1 点阈值判定）。 |
| FilmCardGeometry.cs | FILM-05 胶片卡片两档几何：大档 96×144（逐位等于 `FilmstripGeometry`）、小档 64×76 的尺寸、内外边距、带厚、步长、选档、可见容量、文档尺寸与卡片矩形；可见范围与居中偏移复用 `FilmstripGeometry`。 |
| ControlBarLayout.cs | 分组类型 `ControlBarGroupKind`（EXIF/评分/命令，`ToolbarFlowLayout` 复用）与旧控制栏几何（30×30 评分、40×40 命令口径）；四端控制栏都已改用 `ToolbarFlowLayout`，旧几何只剩共享契约覆盖。 |
| RatingSourcesLayout.cs | RATING-04 来源浮窗几何：星排槽位、条目与浮窗尺寸、单行/双行排版、按入口类型与可用空间解析方向、落位夹取与文件名中间省略；各端只提供文本测量与参照矩形，Mac 在调用处加原生文本控件内边距。 |
| RatingControlGeometry.cs | RATING-07 星级控件几何：形态判定、控件矩形、六格槽位与主轴坐标→值映射、紧凑星簇分布、弹窗方向与夹取、`ExternalInBand` 跨入口取值带。 |
| RatingGesture.cs | RATING-07 星级手势状态机：起手预览、沿星排移动、抬手提交、取消回滚，一次手势最多提交一次；支持延迟预览、强制提交与 0 号格改开来源弹窗。 |
| RatingPopupLayout.cs | RATING-07 星级弹窗排版：星排步长收窄（下限 28 / 上限 44）、单条目横排与竖排、来源条目排版、面板尺寸、条目起点与分隔线位置；全部矩形相对条目左上角。 |
| ToolbarFlowLayout.cs | TOOLBAR-01 宽形态控制栏流式排布：命令圆按钮 / 星级控件 / EXIF 分组尺寸由内容显式给出（EXIF 组取自 `ExifBlockGeometry`，无衬底纵排使用固有项宽且与按钮同轴），`Arrange` 一次输出文档宽高与每个分组在文档坐标中的矩形（整组换行、行内居中、交叉轴拉伸到行高、前导内边距可省略）；Mac / iOS / Android / Windows 控制栏共用。 |
| FilmSideGeometry.cs | SHELL-09 / FILM-07 / FILTER-07 左侧栏的末端几何：按刘海侧安全区、每档列表交叉轴内边距与胶片带厚算出内容末端锚点、栏宽与列表 X（`Measure`），把入口组或上下张按同一条末端对齐（`AlignTrailing`）；`PhoneScreenLayout`、iOS 宽形态筛选与根控制器共用。 |
| ToolRegionGeometry.cs | SHELL-09/SHELL-12 工具区几何：分析栏与控制栏相邻时的共同外边距、接缝间距、独立/相邻控制列宽与居中起点；`PhoneScreenLayout` 与 `ToolbarFlowLayout` 共用。 |
| ExifBlockGeometry.cs | TOOLBAR-05 EXIF 块几何：每项 52×36、项距 2、内边距 6、横排块高 44、圆角 12；固有尺寸与逐项矩形（槽位不足时等量收窄且不为负、长于固有长度不拉伸），以及内边距为零、按整项裁剪的无衬底变体。 |
| WideFilterLayout.cs | FILTER-07 桌面宽形态筛选控件几何（三合体或单胶囊）：形态判定、行布局整组居中（含相似聚类开关与 8 点间距、空间不足从外边距起排）、列布局固定卡片列宽并把余量均分三段、开关下方同宽、控件主轴长度与段矩形。 |
| MobileFilterLayout.cs | FILTER-07 移动端宽形态（iPadOS / Android）「筛选 / 更多」两段合体几何：合体与大档卡片同宽（96，两段各 48），行布局整组含相似聚类开关按自然宽居中，列布局合体与开关靠主图侧取 `FilmSideGeometry` 内容末端、开关在下方隔 8 同宽；给出控件主轴长度。 |
| TitleBarLayout.cs | SHELL-02 桌面标题栏判定：行布局筛选挖空与窗口控制器区间相交（且非全屏、文件栏可见）时改为常驻回退；挖空与背景是否绘制。Mac / Windows 共用，标题最小宽与材质留在各端。 |
| AnalysisPanelGeometry.cs | 分析栏瓦片几何：边长、主轴槽位起点与独立面板交叉轴；Mac / iPad 共用，Android 同一份数学。共同工具区取 `ToolRegionGeometry`，iOS 手机竖屏交叉轴取 `TileSize` + 2 × `RowInset`。 |
| SimilarityPanelGeometry.cs | 相似聚类面板几何：MinWidth 106 / MinHeight 154、外边距 4、列表内边距 8、列布局面板宽（卡片 96 + 内边距 + 描边）与行布局「从胶片栏右侧切出」的宽度规则（`RowWidth(hasItems, itemCount, paneWidth, dockWidth = 0)`：卡片数 × 96 + `sim.list.padding` + 2 × 描边 + 停靠区宽，封顶可用宽度一半）。 |
| ExifTableGeometry.cs | EXIF 详情页 7 列行几何：Tag ID 60 点、其余 2:2:3、列间 1 点分隔线、单元格内边距 10/8、值列最多 3 行；给出卡片宽度下的列宽与可用文本宽。 |
| InputCommandMap.cs | 平台中立的「手势 → 用户命令」查询（`CommandForGesture`）、命令是否登记（`IsCommandKnown`：唯一来源 `SettingsCatalog.Commands`，各平台输入派发的闸门；SF Symbols 等图标表只负责 UI，不得充当命令白名单）与命令元数据 `IsRepeatable`（INPUT-05：仅上一张/下一张/预设缩放可按住重复）。Apple 的 `AppleInputMapping` 与 Android 的 `AndroidInputMapping` 各自把原生事件解包成共享 `AppGesture`，再共用这一张用户主/次绑定表。 |
| IconCatalog.g.cs | 由 `Design/icons.json` 生成的语义 ID、SF Symbols 名称与 Fluent / Material 码位及填充状态；只含纯 C# 静态表，不创建平台图标对象。上游版本、字体子集和三端对比由 `Tools/generate-icons.py` 维护。 |
| ScreenForm.cs | SHELL-07 主屏形态判定：`ScreenFormResolver.Resolve` 输入两轴尺寸类别、窗口宽高、设备类型、用户布局模式与平台能力，输出宽 / 手机竖屏 / 手机横屏；判定表见 spec。 |
| PhoneScreenLayout.cs | SHELL-08–SHELL-12 手机槽位几何：`PhoneScreenLayout.Compute` 输入方向归一后的系统安全区、贴角带探测值、栏位显隐、EXIF 项数、是否显示评分及生效的可选命令数和按钮边长，输出竖横屏槽位、栏背景、胶片条两端停靠留白、主图避让与 `ExifItems`；关闭评分时评分槽与组间距一并释放，刘海在右整体镜像。横屏刘海侧列的列宽、内容末端锚点与列表 X 取 `FilmSideGeometry`，入口合体常驻、上下张仅胶片栏与显示开关同时满足时出现；`TopRowY` 给出竖屏顶行位置，`LeadStripWidth` 只作刘海侧列宽、`ControlStripWidth` 只作控制区域宽，宿主在入口合体（88 × 全高）与控制内容列（按 `ControlContentWidth` × 全高）的实际范围上取圆角探测值。 |
