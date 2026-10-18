# 主图（IMAGE）拆分草稿：平台事实与待答问题

本文件是 [image.md](../image.md) 的配套记录：只放平台事实、验收状态措辞与未决问题，不进入规范正文。拆分来源：[ui-contract.md](../../ui-contract.md) §3/§4、`Plans/native-rebuild-phase-2-ui.md`、`Plans/native-rebuild-phase-3.md`、`Plans/native-rebuild-phase-ipad.md`、`Plans/native-rebuild-phase-android.md`、`Plans/native-rebuild-windows-p2.md`、`Design/architecture.md` §5.1/§5.6–§5.9，以及各平台原生实现代码。

## platforms

### IMAGE-01 显示与画质

| 平台 | 实现事实 |
|---|---|
| mac | `MetalPhotoView`（AppKit `NSView` + `CAMetalLayer`）+ 共享 `MetalPhotoRenderer`；RGBA16F 扩展线性 sRGB；EXIF 方向在 GPU 作 UV 置换，不旋转像素；代理 ↔ Full 用 `PhotoViewport.ApplyTexture` 只改纹理宽度；导航图由同一纹理 GPU 小目标渲染 → sRGB8 → `NSImage`（`MacMainWindow.cs:729-733`）。 |
| ios | `iOSPhotoView` 把 Metal 层挂成 `CAMetalLayer` 子层，外层是**透明的 `UIScrollView` + 透明缩放内容视图**；`zoomScale`/`contentOffset` 是唯一交互真源，视口由 `DidZoom`/`Scrolled` 反算；程序化缩放一律 `ZoomToRect`/`SetZoomScale`；代理 ↔ Full 只换纹理、完全不碰滚动状态。编码：代理 F16，Full 8 位 Display P3（压力 ≥ Warning 时代理也降 8 位）。 |
| win | `WindowsPhotoView` = `SwapChainPanel` + D3D11 `R16G16B16A16_FLOAT` 翻转链；方向在顶点着色器做 UV 置换；三线性 mipmap；`SetImage` 时按相对平移还原视口（`WindowsPhotoView.cs:406-447`）。 |
| android | `AndroidPhotoView` = `SurfaceView` + 自有 EGL14/GLES，RGBA8 mipmap，P3 协商失败回退 sRGB；共享 `PhotoViewport`，坐标 **dp 当点**（`BackingScale = DisplayMetrics.Density`）。 |
| 口径 | 四端 100% 均为 1 源像素对 1 屏幕物理像素。旧 Avalonia `Scale = 1` 是 1 源像素对 1 逻辑点（Retina 上等于 200% 物理），作者 2026-10-18 确认以原生物理口径为准，旧壳差异按意图差异保留，不再对齐。 |

### IMAGE-02 适应 ⇄ 100% 与切图保持

| 平台 | 实现事实 |
|---|---|
| mac | `MetalPhotoView.ToggleFit(CGPoint?)` → 共享 `PhotoViewport.ToggleFit`；双击在 `MetalPhotoView.cs:243` 传拖拽点；Fit 命令传 `null`（对焦点优先）；无缩放动画（直接落位）。 |
| ios | 同一 `ToggleFit`，结果再表达成滚动视图动画；`iOSPhotoView.cs:564-566` 把 `MinimumZoomScale` 固定为 1（该坐标下 1 = 适应态），所以 iOS 不能缩到比适应更小；小图 / RAW 内嵌预览物理 100% 低于适应时夹回适应（`iOSPhotoView.cs:634`、`iOSNativeRootViewController.cs` Fit 分支）。 |
| win | **自有实现**，不消费共享 `PhotoViewport`：`WindowsPhotoView.ToggleFit`（`WindowsPhotoView.cs:475-483`）已放大→回适应，适应态**只用视口中心**放大到 `1/PhysicalPixelsPerDip`，**没有对焦点优先**；`SetImage` 只按相对平移还原，**没有对焦跟随切图**（`WindowsPhotoView.cs:406-447`）。ui-contract §3/§4 已登记“待 W-M6 对齐”。 |
| android | 共享 `PhotoViewport.ToggleFit(null)`，`AndroidPhotoView.cs:163`；程序化缩放用 `ValueAnimator` 250ms（`AndroidPhotoView.cs:452`）；小图夹回适应（`AndroidPhotoView.cs:158`、`:203`）。 |

切图保持：旧 Avalonia `ImageViewModel.OnBitmapChanged`（`ImageViewModel.cs:830`）与原生 `PhotoViewport.ApplyImage`（`PhotoViewport.cs:142`）同一语义——适应态重新适应；跟随对焦时居中到新照片对焦点；否则按相对平移（等价于保持同一归一化中心）。

### IMAGE-03 代理先行与 Full 升级

| 平台 | 实现事实 |
|---|---|
| 共用 | `PhotoLoadSession`（`PhotoLoadSession.cs`）管代际 / 代理 ↔ Full / 增强 / 租约；代理目标长边 = `max(视口物理宽, 高) × 2`（`:190`）；Full 需求由 `PhotoViewport.NeedsFull`（物理 ≥ 100% 或超纹理覆盖）+ “分析栏可见”给出（`MacMainWindow.cs:716`、`iOSNativeRootViewController.cs:1016-1019`、`WindowsMainWindow.cs:848`、`AndroidNativeRootView.cs:439`）。 |
| ios | 宿主必须先上报有效视口再 `SetFile`，否则代理目标会被钳成 1 像素（`PhotoLoadSession.cs:185-190` 的 `< 1` 守卫）。 |
| win | 视口上报按 `(宽, 高, NeedsFull)` 去重（`WindowsPhotoView.cs:668-670`）。 |
| android | Surface 重建后重传当前图并 `ApplyTexture`（`AndroidPhotoView.cs:267-279`）。 |

### IMAGE-04 缩放档位与百分比

- 预设档默认 12.5 / 25 / 33.333 / 50 / 75 / 100 / 200 / 400（`ViewModels/Settings/SettingsViewModel.ImagePreview.cs:21-30`）。
- mac / ios / android：按预设值走 `ZoomToPhysical`（物理倍率），无更远档时不动作（`MacMainWindow.cs:574-580`、`iOSNativeRootViewController.cs:634-642`、`AndroidNativeRootView.cs:649`）。
- win：`ZoomPreset` 取 `ZoomRatio`（= `PhysicalScale`）后 `ZoomBy(target/current)`（`WindowsMainWindow.cs:983-1002`）；没有更远档时 `FirstOrDefault(..., presets[^1])` 会退回端点档，低端可能反向放大一次（见 questions 6）。
- 百分比小数位：mac / ios / win 用 `0.##`（两位），Android 用 `0.#`（一位，`AndroidNativeRootView.cs:390`）。
- 缩放上下限：共享 `PhotoViewport` 与 Windows 均为 0.01–16（`PhotoViewport.cs:283`、`WindowsPhotoView.cs:92-95`）；iOS 下限为适应态、上限物理 16；Android 手势同样夹在适应之上（`AndroidPhotoView.cs:577`）。

### IMAGE-05 平移与边界

- mac：`MetalPhotoView.SyncViewport` → `PhotoViewport.ResizeView`：按 backing 倍率换算（跨屏保持物理倍率），平移以视口中心为基准，尺寸变化后中心图像点不变，最后只做边界钳制；适应态 `Pan` 恒为 0。
- ios：布局/旋转按“同一图像点留在视口中心 + 同一相对缩放”重建滚动基准；`contentSize = 缩放内容视图 × zoomScale`，偏移按 `(contentInset)` 夹取。
- win：`OnSizeChanged`/`OnCompositionScaleChanged` 统一进 `UpdateSurface`（`WindowsPhotoView.cs:557-565`），重建缓冲与适应缩放。
- android：手势结果写 `ApplyGestureView`，钳制与导航几何全在共享层；捏合低于适应时直接 `FitToView()`（`AndroidPhotoView.cs:577`）。

### IMAGE-06 导航图

| 平台 | 控件与差异 |
|---|---|
| mac | `MacMainWindow` 内 `_navigationHost`（`NSView`）+ `_navigationImage` + `_navigationFrame` + `_zoomLabel`；宿主 12 边距、内边距 8、圆角 6、白框 1；百分比 `PhysicalScale × 100`；**只展示、不接受点按**（`MacMainWindow.cs:717-738`）。 |
| ios | 同一套自绘；位置额外让出真实安全区（`rightInset` / `bottomInset`），缩略图由 GPU 小目标渲染 → PNG；**点按/拖动导航图**把归一化点居中（`iOSNativeRootViewController.cs:531-581`）。 |
| win | `WindowsNavigator`：边距 12、内边距 8、圆角 **8**、白框 **2 px**、底色 `#92` 透明度；`IsHitTestVisible = false`（不吃指针）；缩略图由会话 RGBA16F 帧经 Skia 缩到导航尺寸（`WindowsNavigator.cs:23,45-87`；`WindowsLayout.cs:156-177`）。 |
| android | 覆盖在 `SurfaceView` 之上的普通 `View`（`AndroidNavigationOverlay`）：圆角 6、内边距 8、底色 `#C8222222`（≈78%）；触屏点按/拖动定位；缩略图不经 GPU 回读，改用 `AndroidNavigationDecoder`（BitmapFactory 子采样 + 方向转正，长边 300dp），与计划中“GPU 小目标渲染”有偏离（`Plans/native-rebuild-phase-android.md:247` 发现 #18）。 |
| 共用 | 显示条件四端一致：`ShowZoomIndicator && !IsFit && Image != null`；几何来自共享 `PhotoViewport.NavigationSize` / `NavigationViewport`（`PhotoViewport.cs:84-107`）。 |

### IMAGE-07 增强预览

- 状态归 `PhotoLoadSession`（`IsEnhanced` / `ToggleEnhanced()` / `IsEnhancing` / `EnhancedDisplay`），四端同一逻辑；算法只作用于 8 位 sRGB 口径，产物回转源展示编码（`PhotoLoadSession.cs:86-173`）。
- 计算中反馈：iOS `UILabel` 药丸（96×28、圆角 11、距主图区底 24，`iOSNativeRootViewController.cs:222-232`）与 Android TextView（96×28、距底 20 + 底部安全区，`AndroidNativeRootView.cs:170-176`）；**Mac 与 Windows 没有该提示**（`MacMainWindow.cs` / `WindowsMainWindow.cs` 无对应控件）。
- 内存压力 Warning 自动关闭：`PhotoLoadSession.Trim`（`:314-324`）。
- 入口：控制栏命令 `ToggleEnhance`（默认 `E`）四端都有；主图菜单勾选项见 iOS / Android（`iOSNativeRootViewController.cs:686`、`AndroidNativeRootView.cs:552`），Mac / Windows 当前只有控制栏与快捷键。

### IMAGE-08 方向

- 方向映射权威表 `PhotoViewer/Core/Image/ImageOrientationMapping.cs`；`DisplayCrop` 与 `ColorEngine.ToAnalysisSrgb8` 都从该表取，避免两处漂移。
- mac / ios / win：GPU 方向变换（UV 置换）；android：GLES 顶点 UV；旧壳 `SkiaImageCodec.ApplyOrientation` 为 CPU 旋转。
- 导航图长宽在方向 ≥5 时换轴（`PhotoViewport.ImageWidth/ImageHeight`）。

### IMAGE-09 定位到指定图像点

- 四端均已接线：Mac `FocusOnPoint(u, v, 1)`（`MacMainWindow.cs:774`）、iOS 同（`iOSNativeRootViewController.cs:979`）、Windows `FocusOnImagePoint(u, v)` 默认物理 1.0（`WindowsMainWindow.cs:168`、`WindowsPhotoView.cs:207`）、Android `FocusOnPoint(u, v, 1)`（`AndroidNativeRootView.cs:137`）。
- Android 额外对小图夹回适应（`AndroidPhotoView.cs:203`），并按发现 #40 处理绿框与双击位置。

### IMAGE-10 主图区状态

| 平台 | 空态 / 失败 / 加载 |
|---|---|
| mac | 空态「点击打开文件 或 拖拽图片到此处」；`Session.Error` 非空时标题改「无法打开该图片」，详情为错误文本（`MacMainWindow.cs:135,700-703`）。 |
| ios | 空态「点击打开文件夹」；错误同上（`iOSNativeRootViewController.cs:1025-1035`）。 |
| win | 空态「点击打开文件 或 拖拽图片到此处」（`WindowsMainWindow.cs:858`）。 |
| android | 空态「PhotoViewer / 点按选择文件夹，或长按打开菜单」；有当前文件而图未上屏时显示「正在载入…」+ 文件名；错误文案「无法显示照片」（`AndroidNativeRootView.cs:865-885`）。 |

提示几何：标题 16、详情 11、最大宽 500、详情相对标题下移 28（Android 行高 24，无该 28 偏移）——见 tokens `image.hint.*`。

### IMAGE-11 会话作用域

- 增强开关：`PhotoLoadSession._enhanceRequested` 初值 false，跨切图保持，不写设置 → 重启不保留。
- 导航图开关（`ShowZoomIndicator`）与缩放预设写设置；缩放 / 平移 / 适应态 / 跟随对焦均不写设置。
- 恢复窗口偏好只含栏位显隐（`iOSNativeRootViewController.cs:1037-1045` 等），不含缩放。

## status

> 措辞与日期照抄 [ui-contract.md](../../ui-contract.md) §3/§4 与各阶段计划；新 ID 无独立验收记录，只有能力被既有条目覆盖的说明。

| ID | Windows | macOS | iOS/iPadOS | Android |
|---|---|---|---|---|
| IMAGE-01 | `--pv-native-smoke`（150%）覆盖 3600×2400 代理→Full 与物理 100%、导航图、EXIF 方向 1–8（GPU 四角回读 + 导航图四角，方向 6 另验 `DisplayCrop`）程序化通过（2026-10-17）；真实相机八方向素材、200%/175% DPI、跨屏与色彩/RAW 待验 | 色彩门已关闭（作者 2026-10-16：色彩通过、画质通过）；性能门（大图 <100ms）未关闭，作者裁定“以后再做” | 模拟器两机型 + 真机程序化通过（2026-10-17，`--pv-native-photo-check` 10 checks + `--pv-native-gesture-check` 24 checks：代理先行、物理 100% 换 Full、变换保持、中心色差在阈值内、八方向 GPU 角点、色卡线性值）；P3/Adobe RGB 目视与跨屏色彩待作者手验 | Android 原生壳 2026-10-18 模拟器目视：方向 6 朝向、6000×4000 代理→Full、Fit→100% 与导航图正确；P3 在模拟器为 sRGB 回退；八方向/色卡/双击物理 100% 断言待 A6 |
| IMAGE-02 | `--pv-native-smoke` 覆盖 Fit/100% 与代理→Full；**Fit 目前只用视口中心、无对焦点优先**（2026-10-17 登记待 W-M6 对齐） | Mac `--pv-native-smoke`(39) / `layout` / `input` / `analysis` PASS；Fit 与双击走同一共享语义，跟随对焦按 5% UV 重估 | 2026-10-17 模拟器 + 真机 `--pv-native-gesture-check` 24 checks（冷启动首图双击精确物理 100%、Fit 双向、放大态切图保持倍率/归一化中心、真实 Sony EXIF 对焦跟随、代理→Full 不跳）；作者手验双击/Fit/切图手感待复测；ARW 内嵌预览使“100%”仅 1.09–1.2 倍 | Android 原生壳 2026-10-18 模拟器目视 Fit→100% 往返；程序化断言待 A6 |
| IMAGE-03 | 由 IMAGE-01 的 smoke 覆盖（代理→Full、视口去重）；未按本 ID 单独立项 | 由 IMAGE-01/02 的 smoke 覆盖；代理档实测见 P2 | 由 `photo`/`gesture` 套件覆盖（代理先行、Full 热替换、视口 <1 守卫）；未按本 ID 单独立项 | 2026-10-18 模拟器目视 2706×1804 代理→6000×4000 Full；断言待 A6 |
| IMAGE-04 | smoke 断言百分比与 `PhysicalScale` 同口径、预设档位；150% 实机对拍（2026-10-17） | 代码核对（`PhysicalScale × 100`，`0.##`）；未见专项断言 | gesture 检查断言导航百分比与物理倍率；缩放预设逐档未专项 | 2026-10-18 模拟器目视 Fit→100%；预设档与百分比断言待 A6 |
| IMAGE-05 | smoke 覆盖 100% 与三尺寸布局；平移边界未单独立项 | `layout` 覆盖尺寸变化与视口包含关系 | gesture 断言 `contentSize` / 偏移合法、Full 热替换与布局变化不移动视图（2026-10-17） | 模拟器目视；程序化断言待 A6 |
| IMAGE-06 | smoke 断言 fit 隐藏 / 非 fit 显示、缩略图像素尺寸、白框在缩略图内、百分比与 `PhysicalScale` 同口径、设置关闭即隐藏（2026-10-17）；真实鼠标手感不适用（不吃指针） | `--pv-native-layout` 断言长边 150、比例、代理→Full 后导航几何不跳、实际帧长边与比例（代码核对 + 程序化） | gesture 断言适应态隐藏、放大态尺寸/白框/百分比、平移后白框随动、拖动导航图定位（2026-10-17）；真实拖拽已目视复核 | 2026-10-18 模拟器目视长边 150、白框与百分比、点按/拖动定位；断言待 A6 |
| IMAGE-07 | smoke 覆盖增强帧上屏、关闭恢复、压力关闭；**无“计算中”提示** | `--pv-native-analysis` 覆盖增强开关与还原；**无“计算中”提示** | `--pv-native-analysis-check` GOP 帧差断言（平均差 ≥6、最大差 ≥30，关闭后完全还原）+ “计算中”提示出现/收起（2026-10-17）；作者真机目视平均差 17.7 | 2026-10-18 模拟器目视增强像素变化与“计算中…”；断言待 A6 |
| IMAGE-08 | smoke 自生成方向 1–8 素材，GPU 表面四角回读 + 导航图四角逐方向核对（2026-10-17） | `--pv-native-analysis` 同口径（方向 6 另验 `DisplayCrop`） | `photo` 八方向 GPU 角点；`analysis` 断言 EXIF 6/8 与 8 位 P3 Full 增强前后方向/源尺寸/导航帧四角一致（2026-10-17） | 模拟器仅目视方向 6；八方向断言待 A6 |
| IMAGE-09 | 细节预览双击由分析栏套件覆盖；主图定位未单独立项 | `--pv-native-analysis` 覆盖双击定位 | `--pv-native-analysis-check` 覆盖双击定位到裁剪中心 | 模拟器目视细节点按定格 + 双击定位（2026-10-18）；断言待 A6 |
| IMAGE-10 | smoke 覆盖空态/失败形态；提示文案逐条待手验 | 空态与失败提示已实机核对（`--pv-native-input` 断言空首屏文案） | 空态/错误提示随 `input` 套件断言；未单独列项 | 2026-10-18 模拟器目视空态、「正在载入…」与错误文案；断言待 A6 |
| IMAGE-11 | 代码核对（会话态，无持久化） | 代码核对 | 代码核对（`RestoreWindowPreferences` 只恢复栏位显隐） | 代码核对 |

## questions

1. **Windows 的 Fit 与切图保持是否按必须对待？** `WindowsPhotoView.ToggleFit`（`PhotoViewer.Windows/Native/WindowsPhotoView.cs:475-483`）只用视口中心，`SetImage` 只按相对平移还原（`:406-447`），没有共享 `PhotoViewport.ToggleFit`/`ApplyImage` 的对焦点优先与对焦跟随；ui-contract §3 与 `Plans/native-rebuild-phase-ipad.md:192` 都记为“待 W-M6 对齐”。若 IMAGE-02 是必须，Windows 当前是缺口而不是平台差异。
2. **桌面可以缩到比适应更小吗？** 共享 `PhotoViewport` 只把缩放夹在 0.01–16（`PhotoViewer.Shared/PhotoViewport.cs:283`），Mac/Windows 的 Ctrl+滚轮与 12.5% 预设因此可达低于适应的倍率；iOS 把 `MinimumZoomScale` 定为 1（`PhotoViewer.iOS/Native/iOSPhotoView.cs:564-566`），Android 捏合低于适应直接 `FitToView()`（`PhotoViewer.Android/Native/AndroidPhotoView.cs:577`）。IMAGE-04 暂按“触屏夹在适应、桌面允许”写；需作者确认统一方向。
3. **增强“计算中”提示是否四端必须？** 只有 iOS（`iOSNativeRootViewController.cs:222-232`）与 Android（`AndroidNativeRootView.cs:170-176`）有药丸；Mac/Windows 无对应控件（`MacMainWindow.cs`、`WindowsMainWindow.cs` 全文无 `IsEnhancing` 消费）。Mac 实测 24MP 约 0.26 s、iOS 真机 1.8 s，是否只在慢平台显示还是统一显示？
4. **代理已在屏时 Full 升级失败要不要报错？** `PhotoLoadSession.LoadAsync` 在 Full 请求返回 null 或抛异常时设 `Error`（`PhotoViewer/Core/Image/PhotoLoadSession.cs:230-234,257-263`），而 Mac 只要 `Session.Error` 非空就显示「无法打开该图片」（`MacMainWindow.cs:700-703`）——此时屏幕上其实有可用代理。IMAGE-03/10 写的是“保持代理、错误只在无图时显示”，与当前实现不一致，需确认是修实现还是改规则。
5. **导航图缩略图的来源与增强一致性。** Mac/iOS 用当前展示产物做 GPU 小目标渲染，Windows 用会话帧 Skia 采样，Android 重新解码文件缩略图（`AndroidNavigationDecoder`，`AndroidNativeRootView.cs:905-929`），因此 Android 的导航图在开启增强预览时不体现增强；计划 §6 发现 #18 也登记了与“GPU 小目标渲染”的偏离。IMAGE-06 暂写“可以体现（不要求）”，需确认是否允许长期分叉。
6. **Windows 预设档在端点附近的行为。** `WindowsMainWindow.cs:983-1002` 在找不到更远档时退回 `presets[0]`/`presets[^1]`：低于最小预设时按“缩小”可能反而放大到 12.5%。Mac/iOS/Android 无更远档时不动作。是否按“不动作”统一？
7. **主图程序化缩放是否遵循减少动态效果？** 只有胶片栏接入（Mac `MacFilmstripView.cs:93`、iOS `iOSFilmstripView.cs:295`、Windows `WindowsFilmstripView.cs:259`）；Android 主图固定 250ms `ValueAnimator`（`AndroidPhotoView.cs:452`），iOS 走 `ZoomToRect` 系统动画。IMAGE-04 把“直接到终点”写成应当，需确认是否升为必须并补齐四端。
8. **源尺寸元数据缺失时的“一次纠正”是否可接受？** 旧壳在 EXIF 尺寸不可得时以代理尺寸作几何基准，Full 到达后纠正（`PhotoViewer/ViewModels/Main/ImageViewModel.cs:89-91,117-121`），会产生一次位置/倍率跳变；原生 `PhotoLoadSession` 依赖解码产物的 `SourceWidth/Height`，通常不触发。IMAGE-01 允许“纠正一次”，需确认是否改为“必须等到源尺寸可用再上屏”。
9. **切图瞬间旧图是否保留？** 原生 `PhotoLoadSession.SetFile` 立即 `Replace(null)`（`PhotoLoadSession.cs:64-80`），主图短暂空白；旧壳仅在新图未命中缓存时清屏（`ImageViewModel.cs:252-254`），命中缓存会短暂保留上一张。IMAGE-10 采用原生语义（不显示旧图冒充），旧壳差异按退役处理。
10. **Android 空态显示「正在载入…」而 Mac/Windows/iOS 留空。** `AndroidNativeRootView.cs:877-881` 在“有当前文件、图未上屏”时立即显示加载提示，另外三端为空白。加载提示的延迟策略归 state.md，需确认主图是否统一给即时或延迟提示。
