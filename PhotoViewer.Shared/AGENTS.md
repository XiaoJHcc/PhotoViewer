# PhotoViewer.Shared — 跨壳共享源（几何 / 命令表）

`PhotoViewer.Shared/` 是**纯源文件目录**（不是独立 csproj/程序集）。`PhotoViewer.Mac`、`PhotoViewer.iOS`、`PhotoViewer.Android`、`PhotoViewer.Windows` 四个头都以 `<Compile Include="..\PhotoViewer.Shared\**\*.cs"/>` 链接编译，并以 `<Using Include="PhotoViewer.Shared"/>` 引入命名空间。

**硬规则**：本目录只允许纯 C#（`double` 数学 / 记录 / 静态表），不得出现 AppKit、UIKit、Android 或 Windows 平台类型，也不得引入任何 UI 框架依赖。平台事件解包、原生视图、坐标换算留在各 head。

**坐标空间**：共享几何一律以「点」为单位。Apple 头直接用逻辑点；Android 头**把 dp 当点**（`View` 的 px 只在边界换算，`PhotoViewport.ViewWidth/Height` 传 dp、`BackingScale = DisplayMetrics.Density`）。100% 口径仍是 1 源像素对 1 屏幕物理像素。

**迁移门**：几何文件只做 namespace/using 级迁移。改动后必须跑 Mac 八套 `--pv-native-*` + iOS 七套 `--pv-native-*/-check` 零行为变化；Android 链入后还要过 Android 模拟器/真机门。

| 文件 | 职责 |
|---|---|
| PhotoViewport.cs | 视口纯数学（纯 double）：适应缩放（`FitScale`）、锚点缩放与对焦点居中、平移钳制、导航图尺寸/可见范围（`NavigationSize`/`NavigationViewport`）、`NeedsFull`（物理 100% 或代理不足）；`CenterOnNormalizedPoint(u,v)` 保持当前缩放、把归一化图像点移到视口中心（导航图白框拖动，IMAGE-06）；`ApplyGestureView(scale, panX, panY)` 供宿主手势（iOS `UIScrollView` 的 zoomScale/contentOffset）直接写入并统一钳制；`ApplyTexture(textureWidth)` 供**同一张照片的代理↔Full 热替换**只更新纹理宽度、不碰缩放/平移；`ApplyImage(...)` 供**切图**沿用原 Avalonia `OnBitmapChanged` 语义（适应态重新适应 / 跟随对焦时保持倍率并居中到新照片对焦点 / 否则保持倍率与同一归一化中心点，几何基准恒为全分辨率 `SourceWidth/Height`）；`ToggleFit(anchor?)` 是控制栏 Fit 命令与双击的共同语义（已放大 → 回适应；适应态无锚点且照片有对焦点 → 以对焦点为中心到物理 100% 并开启跟随；否则以锚点/视口中心放大；物理 100% 不高于适应倍率的小图保持适应）；`CenterUV` 与 `IsViewCenteredOnFocusPoint`（两轴 5% UV）在每次缩放/平移后重算 `FollowsFocus`。**100% 口径**：原生视口 = 1 源像素对 1 屏幕物理像素（`ZoomToPhysical`/`ToggleFit` 用 `1 / BackingScale`）；原 Avalonia `Scale = 1` 是 1 源像素对 1 逻辑点（Retina 上等于 200% 物理像素），差异登记在 [IMAGE-02](../Design/spec/image.md)。 |
| MainScreenLayout.cs | 主屏分区几何：文件栏内容宽 116、筛选条五个控件位置（90×32 / 90×27 / 间距 5 / 外边距 10；行布局默认横向居中给 macOS 交通灯让位，`FilterBarFrames(alignLeading: true)` 时从内容前导边 + 10 起排，iPad/Android 用此模式；列布局 116 栏内纵排）、文件栏与控制栏主轴长度、两向分区矩形（文件栏/分隔线/主图/分析栏/控制栏，任一处长度为 0 即隐藏并把空间让给主图）。平台差异（macOS 隐藏标题栏让位、iOS/Android 真实安全区/无交通灯）留在各自 head。 |
| FilmstripGeometry.cs | 胶片栏几何（96×144 卡片、10/5/10 内边距、横向首项居中、文档长度、可见项范围、FILM-02 居中偏移）与 `FilmstripAnimation`（350ms 三次缓出曲线、进度与 1 点阈值判定）。 |
| ControlBarLayout.cs | 控制栏几何：分组类型（EXIF/评分/命令）的子控件尺寸与内边距、分组主轴/交叉轴长度、整组换行/换列、行内居中、文档尺寸与子控件偏移。宿主只落 frame。 |
| AnalysisPanelGeometry.cs | 分析栏瓦片几何：边长 `floor((可用主轴长度 − 20 − 8×(n−1)) / n)`、交叉轴 = 边长 + 20、槽位起点。Mac / iPad 共用；Android 分析栏按同一份数学。 |
| SimilarityPanelGeometry.cs | 相似聚类面板几何：MinWidth 106 / MinHeight 154、外边距 4、列表内边距 8、列布局面板宽（卡片 96 + 内边距 + 描边）与行布局「从胶片栏右侧切出」的宽度规则（`RowWidth(hasItems, itemCount, paneWidth, dockWidth = 0)`：卡片数 × 96 + `sim.list.padding` + 2 × 描边 + 停靠区宽，封顶可用宽度一半）。 |
| ExifTableGeometry.cs | EXIF 详情页 7 列行几何：Tag ID 60 点、其余 2:2:3、列间 1 点分隔线、单元格内边距 10/8、值列最多 3 行；给出卡片宽度下的列宽与可用文本宽。 |
| InputCommandMap.cs | 平台中立的「手势 → 用户命令」查询（`CommandForGesture`）与命令元数据 `IsRepeatable`（INPUT-05：仅上一张/下一张/预设缩放可按住重复）。Apple 的 `AppleInputMapping` 与 Android 的 `AndroidInputMapping` 各自把原生事件解包成共享 `AppGesture`，再共用这一张用户主/次绑定表。 |

> 2026-10-18 A1a：以上 7 个几何文件从 `PhotoViewer.Apple/` 迁入本目录，命名空间改 `PhotoViewer.Shared`；除 namespace 外内容未改。执行记录见 [Android 交接](../Plans/native-rebuild-android-handoff.md) 与 [执行清单 §7](../Plans/native-rebuild-phase-android.md)。
