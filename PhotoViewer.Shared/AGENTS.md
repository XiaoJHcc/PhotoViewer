# PhotoViewer.Shared — 跨壳共享源（几何 / 命令表）

`PhotoViewer.Shared/` 是**纯源文件目录**（不是独立 csproj/程序集）。`PhotoViewer.Mac`、`PhotoViewer.iOS`、`PhotoViewer.Android`、`PhotoViewer.Windows` 四个头都以 `<Compile Include="..\PhotoViewer.Shared\**\*.cs"/>` 链接编译，并以 `<Using Include="PhotoViewer.Shared"/>` 引入命名空间。

**硬规则**：本目录只允许纯 C#（`double` 数学 / 记录 / 静态表），不得出现 AppKit、UIKit、Android 或 Windows 平台类型，也不得引入任何 UI 框架依赖。平台事件解包、原生视图、坐标换算留在各 head。

**坐标空间**：共享几何一律以「点」为单位。Apple 头直接用逻辑点；Android 头**把 dp 当点**（`View` 的 px 只在边界换算，`PhotoViewport.ViewWidth/Height` 传 dp、`BackingScale = DisplayMetrics.Density`）。100% 口径仍是 1 源像素对 1 屏幕物理像素。

**迁移门**：改动共享几何后必须复跑 Mac / iOS / Android 的原生验收套件；Windows 至少通过 `Tools/WindowsTypeCheck` 的类型检查与 Windows 原生冒烟。

| 文件 | 职责 |
|---|---|
| PhotoViewport.cs | 视口纯数学（纯 double）：适应缩放（`FitScale`）、锚点缩放与对焦点居中、平移钳制、导航图尺寸/可见范围（`NavigationSize`/`NavigationViewport`）、`NeedsFull`（物理 100% 或代理不足）；`CenterOnNormalizedPoint(u,v)` 保持当前缩放、把归一化图像点移到视口中心（导航图白框拖动，IMAGE-06）；`ApplyGestureView(scale, panX, panY)` 供宿主手势（iOS `UIScrollView` 的 zoomScale/contentOffset）直接写入并统一钳制；`ApplyTexture(textureWidth)` 供**同一张照片的代理↔Full 热替换**只更新纹理宽度、不碰缩放/平移；`ApplyImage(...)` 供**切图**沿用原 Avalonia `OnBitmapChanged` 语义（适应态重新适应 / 跟随对焦时保持倍率并居中到新照片对焦点 / 否则保持倍率与同一归一化中心点，几何基准恒为全分辨率 `SourceWidth/Height`）；`ToggleFit(anchor?)` 是控制栏 Fit 命令与双击的共同语义（已放大 → 回适应；适应态无锚点且照片有对焦点 → 以对焦点为中心到物理 100% 并开启跟随；否则以锚点/视口中心放大；物理 100% 不高于适应倍率的小图保持适应）；`CenterUV` 与 `IsViewCenteredOnFocusPoint`（两轴 5% UV）在每次缩放/平移后重算 `FollowsFocus`。**100% 口径**：原生视口 = 1 源像素对 1 屏幕物理像素（`ZoomToPhysical`/`ToggleFit` 用 `1 / BackingScale`）；原 Avalonia `Scale = 1` 是 1 源像素对 1 逻辑点（Retina 上等于 200% 物理像素），差异登记在 [IMAGE-02](../Design/spec/image.md)。**适应区与视图区分离**：`SetFitInsets(EdgeInsets)` 记录四边避让内边距（不自动改缩放/平移，由调用方随后重新适应或钳制），`FitScale`/`FitToView`/`ClampPan`/视口中心语义（`CenterUV`、`ZoomBy`/`ZoomToPhysical` 无锚点、`ToggleFit` 无锚点、切图相对平移、`NavigationViewport`）一律取适应区（`FitWidth`/`FitHeight`）与适应区中心；照片自身仍可绘制满视图，放大后的平移极限让照片边缘正好落在适应区边缘；四边内边距为零时适应区即整个视图，全部公式与引入适应区之前逐位等价。 |
| MainScreenLayout.cs | 主屏分区几何：文件栏内容宽 116、筛选条控件位置（90×32 / 90×27 / 间距 5 / 外边距 10；行布局默认在安全内容区内整组横向居中；Android 与 iPad 都使用默认居中，只有 Android 自然宽度溢出时才用 `FilterBarFrames(alignLeading: true)` 从内容前导边 + 10 起排并滚动；列布局 116 栏内纵排）、文件栏与控制栏主轴长度、两向分区矩形。`includeSimilarityToggle=false` 时按四项居中且列布局不预留 toggle 高度，供标准版使用；默认 true 保持既有五项目径。平台差异（macOS 隐藏标题栏让位、iOS/Android 真实安全区/无交通灯）留在各自 head。 |
| FilmstripGeometry.cs | 胶片栏几何（96×144 卡片、10/5/10 内边距、横向首项居中、文档长度、可见项范围、FILM-02 居中偏移）与 `FilmstripAnimation`（350ms 三次缓出曲线、进度与 1 点阈值判定）。 |
| FilmCardGeometry.cs | FILM-05 胶片卡片两档：大档 96×144（逐位等于 `FilmstripGeometry`，Mac / Windows / Android 继续用旧类型）、小档 64×76（主轴内边距 6、交叉轴 8）的尺寸、主轴/交叉轴内边距、带厚（大档 159/116、小档 92/80）、步长、选档（扣掉大档主轴内边距后放得下 5 张大档才用大档）、可见容量（整条主轴长度 ÷ 步长）与文档尺寸/卡片矩形；可见范围与居中偏移复用 `FilmstripGeometry.VisibleRange`/`CenterOffset`。 |
| ControlBarLayout.cs | 控制栏几何：分组类型（EXIF/评分/命令）的子控件尺寸与内边距、分组主轴/交叉轴长度、整组换行/换列、行内居中、文档尺寸与子控件偏移。宿主只落 frame。 |
| RatingSourcesLayout.cs | RATING-04 来源浮窗几何：六格星排槽位、条目宽 / 内容宽 / 浮窗宽、条目单行或双行（文件名放不下时独占首行）与内容高、按入口类型（卡片 / 控制栏）与可用空间解析方向（卡片向下不足则向上、横排控制栏向上、竖排控制栏向左）、按入口矩形与宿主矩形放置并夹取、文件名中间省略。各端只提供文本测量与参照矩形；Mac 在调用处加原生文本控件内边距。iOS 星级弹窗改用 `RatingPopupLayout`，只复用本类的信息行高与中间省略。 |
| RatingControlGeometry.cs | RATING-07 星级控件几何：展开/紧凑形态判定（槽位主轴 ≥ 固有长度 224）、控件在槽位内的矩形、六格星排槽位与「主轴坐标 → 值」反向映射（横排 0 在左、竖排 5 在上）、紧凑星簇的均匀分布（`CompactStep`：首末条目中心在胶囊两端半圆圆心，步长封顶展开态格宽，胶囊短时允许叠压；`CompactOffset` 冲突三角占最前一格）、弹窗方向表（控件/卡片入口 × 竖横屏与宽形态行列布局）与按边界夹取的落位；`ExternalInBand(x, y, anchor, panel, row, horizontalRow)` 判定起点在弹窗外（紧凑胶囊、卡片长按）的手势是否仍可预览：交叉轴取入口与弹窗范围的并集两侧外扩取消阈值，主轴还必须落在星排主轴范围两侧各外扩半格之内，越出即不产生预览。 |
| RatingGesture.cs | RATING-07 星级手势状态机：起手预览、沿星排移动、抬手提交、取消回滚，一次手势最多提交一次；支持延迟预览（紧凑态/卡片长按越过点按阈值才预览）、强制提交（冲突同值也写）与 0 号格改开来源弹窗。 |
| RatingPopupLayout.cs | RATING-07 星级弹窗排版：星排步长按可用空间收窄（下限 28 / 上限 44）、非冲突单条目横排与竖排、冲突来源条目按 RATING-04 口径排文件名与来源信息、面板尺寸、条目起点与分隔线位置。全部矩形相对条目左上角。 |
| ToolbarFlowLayout.cs | TOOLBAR-01 宽形态控制栏流式排布：命令圆按钮 / 星级控件 / EXIF 分组尺寸由内容显式给出（EXIF 组取自 `ExifBlockGeometry`），整组换行、行内居中、行长与文档尺寸；旧 `ControlBarLayout` 继续供 Mac / Windows / Android 使用。 |
| ExifBlockGeometry.cs | TOOLBAR-05 EXIF 块几何：每项 52×36、项距 2、内边距 6、横排块高取玻璃按钮边长 44、圆角 12；整块固有尺寸（横排 2P + n·52 + (n−1)·2，竖排 64 宽）与逐项矩形（槽位挤压时各项等量收窄且不为负，长于固有长度时不拉伸）。 |
| WideFilterLayout.cs | FILTER-07 宽形态筛选控件几何：三合体（排序/方向/星级三段）与单胶囊的形态判定、行布局整组在内容区内居中（含相似聚类开关与 8 点间距、空间不足从外边距起排）、列布局控件固定卡片列宽并把余量均分三段、开关在下方同宽、控件主轴长度；三段矩形相对控件局部坐标，单胶囊时为空。 |
| TitleBarLayout.cs | SHELL-02 桌面标题栏判定：行布局筛选挖空与窗口控制器区间相交（且非全屏、文件栏可见）时改为常驻回退；挖空与背景是否绘制。Mac / Windows 共用，标题最小宽与材质留在各端。 |
| AnalysisPanelGeometry.cs | 分析栏瓦片几何：边长 `floor((可用主轴长度 − 20 − 8×(n−1)) / n)`、交叉轴 = 边长 + 20、槽位起点。Mac / iPad 共用；Android 分析栏按同一份数学。 |
| SimilarityPanelGeometry.cs | 相似聚类面板几何：MinWidth 106 / MinHeight 154、外边距 4、列表内边距 8、列布局面板宽（卡片 96 + 内边距 + 描边）与行布局「从胶片栏右侧切出」的宽度规则（`RowWidth(hasItems, itemCount, paneWidth, dockWidth = 0)`：卡片数 × 96 + `sim.list.padding` + 2 × 描边 + 停靠区宽，封顶可用宽度一半）。 |
| ExifTableGeometry.cs | EXIF 详情页 7 列行几何：Tag ID 60 点、其余 2:2:3、列间 1 点分隔线、单元格内边距 10/8、值列最多 3 行；给出卡片宽度下的列宽与可用文本宽。 |
| InputCommandMap.cs | 平台中立的「手势 → 用户命令」查询（`CommandForGesture`）、命令是否登记（`IsCommandKnown`：唯一来源 `SettingsCatalog.Commands`，各平台输入派发的闸门；SF Symbols 等图标表只负责 UI，不得充当命令白名单）与命令元数据 `IsRepeatable`（INPUT-05：仅上一张/下一张/预设缩放可按住重复）。Apple 的 `AppleInputMapping` 与 Android 的 `AndroidInputMapping` 各自把原生事件解包成共享 `AppGesture`，再共用这一张用户主/次绑定表。 |
| ScreenForm.cs | 主屏形态判定（SHELL-01）：`ScreenForm` 为宽 / 手机竖屏 / 手机横屏；`ScreenFormResolver.Resolve` 输入两轴尺寸类别、窗口宽高、是否手机设备、用户布局模式与平台能力，平台未实现手机形态时恒宽形态，手机设备恒手机形态，`Phone` 模式直接手机形态，`Auto` 在任一轴紧凑时进入手机形态，其余为宽形态；手机形态按 `width > height` 取横屏。 |
| PhoneScreenLayout.cs | 手机形态槽位几何（SHELL-01）：`EdgeInsets`、`PhoneLayoutInput`、`PhoneLayout` 与 `PhoneScreenLayout.Compute`。输入分三层：系统安全区 `Safe`、竖屏顶行 / 横屏刘海侧按钮列的贴角带 `LeadBand`、按设备竖屏口径给出的底行带 `ControlBand`（横屏沿用竖屏值，控制列不随旋转移动）。竖屏给出横贯整窗宽的胶片条、顶行（筛选 / EXIF / 更多）、分析栏、底行（上下张 / 星级 / 缩放增强）与顶/底栏背景；横屏给出刘海侧按钮列（筛选更多合体、纵贯整窗高的胶片条、上下张，两组合体按钮可伸入刘海安全区且右对齐同一列边）、相对侧控制列（缩放增强 + 星级，几何不随上下张开关变化）、EXIF 列与分析栏，并在刘海在右时整体水平镜像。`FilmstripPadding` 给出胶片条滚动两端的内容留白（卡片停靠线），首末卡片间距由留白而非裁剪实现。`PhotoAvoidance` 按栏缘口径给出主图避让（栏缘 = 元素内缘 + `RowInset`，与是否绘制栏背景无关；Home 指示条不被主图避让）。空槽位宽高为 0。 |
