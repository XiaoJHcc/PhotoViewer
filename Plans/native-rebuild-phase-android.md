# Android 原生壳执行清单（Views + GLES）

> 2026-10-18 立项，隶属 [native-rebuild-master-plan-1.md](native-rebuild-master-plan-1.md) Phase 5（"Android Views + GLSurfaceView P3 探测 + SAF 迁移"）。前置：macOS 原生壳已验收、iPad 原生壳 I1–I5 程序化完成、Windows 原生壳主屏/设置/工具程序化完成。
>
> **作者要求（2026-10-18）**：
> 1. 安卓以**手机**为主（平板少见、作者无测试机），**对标 iOS 版**；首屏对标 Avalonia 版或 iOS 版皆可（两者结构一致）。
> 2. 设置是**底部浮起半屏**（参考 iOS），选项尽量找可对标 iOS 的控件。
> 3. **无论 ROM 与系统版本，视觉设计与空间操作一致对标**。
> 4. 能用原生最好；原生不可靠再议。
>
> **进度**：**A1a–A5 已完成（2026-10-18）**——共享几何源迁移、`InputCommandMap`、Mac/iOS 回归门，Android 原生壳基座，主图（EGL/GLES + 8 位 P3/sRGB + 代理↔Full）、主屏布局（文件栏/胶片栏/控制栏/导航图/insets）、SAF 存储、输入（硬件键盘/滚轮/长按菜单/轻扫/返回）、半屏设置（五分类、关闭回写）以及 P3 功能（分析栏/相似聚类/工具页 EXIF+DINO/增强预览）均已落地并在 API 35 arm64 模拟器自验。**A6 未开始**（程序化验收 harness/矩阵/真机门/文档收口）。评审修订见 §7，执行记录见 [native-rebuild-android-handoff.md](native-rebuild-android-handoff.md)。
>
> **模拟器自验基线（2026-10-18，1080×2400 / density 2.625 / fontScale 1.0）**：原生壳默认启动、旧壳 `--legacy-ui` 可回退；应用内文件夹经 `@files/...` 启动参数加载；八方向 6 的 GPU 朝向、Fit/100% 热替换（代理 2706×1804 → Full 6000×4000）、导航图（长边 150 点 + 可见框 + 百分比）、卡片星级 XMP 写回（不改主图）、硬件键盘 Right/F、设置五分类打开/关闭回写（ShowRating 关闭后评分分组与卡片星级即时消失）均已目视/日志核对。**A5**：分析栏开合/细节定格绿框/诊断瓦片、相似聚类 toggle → 空态 → 真实“提取全部” → 100% 分数卡片与柱状条、工具页 EXIF 详情（分组/列/格式切换）与 DINO 诊断（5 瓦片 + 刚体文本）、增强预览像素变化均已目视/日志核对。**程序化 harness 与真机门未做**，不能记作通过。
>
> **警告基线（2026-10-18 重建）**：Android Debug **20 警告 / 0 错误**（低于 A1b 基线 21；剩余均为既有 `AndroidExternalOpenBridge`/`AndroidHeifDecoder`/`AndroidSettingsStorage` 的 CS86xx/CA1422，A2–A5 新增警告已清零）。

## 0. 范围

| # | 内容 | 说明 |
|---|---|---|
| 1 | 原生启动与平台接线 | 无 Avalonia 启动、展示解码器/编码策略、SAF 存储引用、文件选择器、外部打开 |
| 2 | 主图 | `SurfaceView` + EGL/GLES 照片层、P3 探测与 sRGB 回退、缩放/平移/双击、代理↔Full 热替换 |
| 3 | 主屏布局 | 文件栏（筛选条 + 胶片栏）、控制栏、手机纵向/横向、刘海与手势条 insets |
| 4 | 输入 | 长按菜单、硬件键盘、手势、拖入、返回手势 |
| 5 | 设置 | 半屏 Bottom Sheet，五分类与 iOS/Windows 同口径，关闭回写 |
| 6 | P3 功能 | 分析栏、相似聚类、工具页（EXIF 详情 / DINO 诊断）、增强预览 |
| 7 | 验收与文档 | 模拟器程序化矩阵、真机门（待设备）、ui-contract Android 列、AGENTS/handoff 同步 |

不在范围：Avalonia 旧壳删除（Phase 6）；照片数据统计（Windows 专属）；性能门（作者裁定"以后再做"）；平板专项适配（沿用两向布局，不单独调优）；Release 默认通道切换（作者决定）。

## 1. 已定决策

### 1.1 技术路线：Views + Material 3，不用 Compose

- **不用 Compose**。Compose 的编译器与运行时是 Kotlin，从 C# 调用只有"Kotlin/Gradle 子模块 → AAR → binding 库"或 interop 桥两条路，都会在本仓引入第二条工具链并制造"ReactiveUI ↔ Compose State"双真源。另外三个壳（AppKit / UIKit / WinUI 3）全是命令式控件树，`Views` 与它们同构，可逐控件对照移植。
- **不用混合**（`ComposeView`）：没有只能用 Compose 表达的界面，混用只增加真源。

### 1.2 控件三档：ROM 差异只存在于第一档

| 档 | 来源 | ROM 是否影响 | 本壳使用范围 |
|---|---|---|---|
| 一 | 平台系统控件 `android.widget.*` | **是**（读系统主题属性，MIUI/EMUI 会重新着色） | **禁用** |
| 二 | Material Components（`com.google.android.material.*`，随 APK 打包） | 仅当继承系统主题派生属性时受影响 | 设置页开关/滑条/分段/下拉；`RecyclerView` 容器 |
| 三 | 完全自绘（`View` + `Canvas`） | 否 | 胶片栏卡片、控制栏、星级、抖动徽标、导航图、分析栏瓦片 |

对第二档的对冲是**钉死主题**（§1.3），不是改写控件。第一档一律不出现。

### 1.3 主题：固定深色、零系统派生

- `Theme.PhotoViewer.Native` 继承 `Theme.Material3.Dark.NoActionBar`；**不调用 `DynamicColors.applyToActivitiesIfAvailable`**（Material You 动态取色会按壁纸改色）。
- 颜色全部写入自有 `values/colors.xml` 常量：背景 `#111111`、分隔线 `#333333`、主图区 `#000000`（与 Avalonia `MainView.axaml` 一致）。**不引用** `?attr/colorPrimary` / `?attr/colorSurface` 等系统派生属性。
- 圆角、形状、字重全部显式声明，不继承 OEM 主题的 `shapeAppearance`。
- 与旧壳主题并存：`MyTheme.NoActionBar`（AppCompat，Avalonia 用）保留，原生壳不与它共享派生值。

### 1.4 字体：接受中文字形由系统决定

- 拉丁、数字、符号与中文字体**均用系统字体**，接受 MIUI（MiSans）/ ColorOS（OPPO Sans）/ 鸿蒙（HarmonyOS Sans）替换。理由：文件名是任意用户数据，要覆盖全部 CJK 需内嵌完整字库（5–10 MB），而中文界面文案本身很少，收益低于体积代价。
- 字号、行高、字重**不受字体影响**的部分全部显式指定（sp 单位 + 显式 `lineHeight`），保证换字体后布局不漂移。
- 图标沿用 Avalonia 已有的 `uicons` 字体（`PhotoViewer/Assets/Fonts/`），作为 `Assets` 资源打包，与壳无关。
- 登记为平台差异（ui-contract）：中文字形随 ROM 变化，属接受项；若作者要求逐像素一致，另立"内嵌 CJK 子集"事项。

### 1.5 主图：`SurfaceView` + EGL14/GLES 3.x

- **不用 `ImageView`/`BitmapDrawable`**：EXIF 方向、广色域、代理↔Full 热替换都需要 GPU。
- **不用 `GLSurfaceView`**：它的配置接口不暴露 `EGL_EXT_gl_colorspace_display_p3`。改为自有 `SurfaceView` + `EGL14`（`eglCreateWindowSurface` 的属性列表是 P3 的唯一入口）。
- **色域探测**：`Display.getSupportedWideColorGamut()`（API 26+）含 `COLOR_GAMUT_DISPLAY_P3` 且 EGL 协商 P3 成功 → P3 帧缓冲；否则 **8 位 sRGB**（着色器同一份，只换色彩空间标签）。与 master plan §3"Android = 8bit P3（探测失败回退 sRGB）"一致。
- **纹理**：`GL_RGBA8`。Android 不做 F16——代理档同为 8 位 P3，代理↔Full 只换纹理尺寸，无编码跳变风险。
- **EXIF 方向在顶点着色器做 UV 置换**，绝不旋转像素（与 Mac/Windows 同口径）；三线性 + mipmap。
- **100% 口径**与 Mac/iPad 一致：1 源像素对 1 屏幕**物理**像素（`density` 参与换算），不是 1 对 1 dp。
- **视口数学复用共享 `PhotoViewport`**：手势算完写 `ApplyGestureView`，宿主只落结果。双击/Fit 走 `ToggleFit`（对焦点优先）。
- **坐标空间（2026-10-18 评审补充）**：共享几何与 `PhotoViewport` 一律以 **dp 当点**（116dp 文件栏、96×144dp 卡片与 iPad 点值同口径）；Android `View` 坐标是 px，只在 View 边界做 px↔dp。`PhotoViewport.ViewWidth/Height` 传 dp，`BackingScale = DisplayMetrics.Density`，`Scale = 1/density` 时 `PhysicalScale == 1`；触摸坐标先转 dp 再进共享数学，否则 Fit、物理 100% 与卡片几何会整体偏。
- **覆盖层**（导航图、绿框、分析栏、状态提示）放在 `SurfaceView` 之上的普通 `ViewGroup`，不在 GL 里画；`SurfaceView` 用默认 `setZOrderOnTop(false)` + 覆盖层同窗口合成。

### 1.6 共享源：把 Apple 共享源提成"壳共享源"

- 新建 `PhotoViewer.Shared/`（纯源目录，`Compile Include` 链接，不新增程序集），迁入 7 个**零 Apple 依赖**的纯数学文件（已核 `using` 为空）：`PhotoViewport`、`MainScreenLayout`、`FilmstripGeometry`（含 `FilmstripAnimation`）、`ControlBarLayout`、`AnalysisPanelGeometry`、`SimilarityPanelGeometry`、`ExifTableGeometry`。
- `PhotoViewer.Apple/` 改为链接 `PhotoViewer.Shared/`，Mac/iOS 两个 csproj 各加一行；`PhotoViewer.Android/` 同样链接。**迁移门 = Mac 八套 + iOS 七套（photo/gesture/layout/input/analysis/similarity/tools）`--pv-native-*` 回归零行为变化**。
- 拆 `AppleInputMapping.cs`：`CommandForGesture` 移入 `PhotoViewer.Shared/InputCommandMap.cs`；**只放命令查询与通用的位掩码→`GestureModifiers`**。苹果的 `AppleKeyboardMapping`、HID usage 表、`FromRaw`/`FromHidUsage` 留在 Apple 侧，避免 Android 继承 Cmd/Option 语义；Android 另写 `AndroidInputMapping`（`KeyEvent` 的 Ctrl/Alt/Shift/Meta），`GestureKey` 名称复用。
- 留在 Apple 的：`MetalPhotoRenderer`、`ApplePresentationDecoder`、`AppleAcceptanceChecks`、`EnhancementAcceptance`、`AppleSymbolNames`。
- 命名空间随之改为 `PhotoViewer.Shared`（Apple/Android 头各加一个 `global using` 或直接改引用）。

### 1.7 布局：手机只走行布局

- 复用共享 `MainScreenLayout` 全量数学，不抄常量。手机纵向 400dp 宽放不下 116dp 文件栏 + 主图，故纵向恒为行布局（文件栏在上、控制栏在下）；横向按 `IsRowLayout` 的 Auto 规则走列布局，此时 116dp 栏可用。
- **不新增**手机专属布局模式；若作者后续认为"胶片栏应在下方"，作为平台差异单独立项。
- **insets**：`WindowCompat.setDecorFitsSystemWindows(window, false)` + `OnApplyWindowInsetsListener` 吃 `systemBars()` 与 `displayCutout()`；栏位背景铺到屏幕边缘、内容收进安全区（与 iPad 同口径）。Android 15（API 35）强制 edge-to-edge，此路径是必做项。
- **刘海**：`displayCutout` 的 left/right 在横屏时计入内容内边距。
- **筛选条行布局左对齐**：共享 `MainScreenLayout.FilterBarFrames` 默认给 macOS 交通灯居中；Android 行布局显式 `alignLeading: true`（与 iPad 同口径，左上不留空带）。

### 1.8 设置：半屏 Bottom Sheet

- `BottomSheetDialogFragment` + `BottomSheetBehavior`：`peekHeight = 屏幕高 × 0.5`、`skipCollapsed(false)`、顶部圆角与拖拽把手由 `bottomSheetStyle` 控制。
- **五个分类与 iOS/Windows 同口径**（文件 / 预览 / 控制 / EXIF / AI），根页为分类列表。
- **子页在同一 sheet 内切换**（`FragmentContainerView` + 返回栈），不用全屏 Activity——半屏沉浸感是作者明确要求。
- **控件对标表**：

| iOS | Android | 档 |
|---|---|---|
| `UISwitch` | `MaterialSwitch` | 二 |
| `UISlider` | `Slider`（`stepSize`） | 二 |
| `UISegmentedControl` | `MaterialButtonToggleGroup` | 二 |
| 数值步进 | `Slider` 或 `TextInputLayout` + `InputType` | 二 |
| 下拉 | `MaterialAutoCompleteTextView`（exposed dropdown） | 二 |
| 拖拽排序 | `ItemTouchHelper` | 二 |
| 分组卡片表单 | `MaterialCardView` + 自绘分组标题/脚注 | 二 + 三 |

- 工作副本沿用共享 `NativeSettingsSession`，关闭一次性回写；实现 `INativeSettingsPresenter`。
- 返回/下滑关闭：`BottomSheetDialogFragment` 的 `OnDismiss` 统一触发 `Apply()`。

### 1.9 输入

- 硬件键盘：`DispatchKeyEvent` → `AndroidInputMapping` → `AppGesture` → 用户主/次绑定（与 iPad 同一张命令表）。**注意** Android 的 `Ctrl`/`Meta` 语义与 Apple 不同，映射表按 Android 习惯重写修饰键解码，但 `GestureKey` 名称复用。
- 主图长按 → `PopupMenu`（与 Mac 右键菜单同序）；卡片长按 → 含"评分 0–5"的 `PopupMenu`。
- 触屏：`ScaleGestureDetector` + `GestureDetector`（双击、长按），单指横向轻扫在适应态翻页（与 iPad 同语义）；放大态只平移。
- 返回：`OnBackPressedCallback`（API 33 预测性返回）；sheet 打开时优先关闭 sheet。
- 拖入：`View.OnDragListener` + `requestDragAndDropPermissions`。
- 减少动态效果：读 `Settings.Global.ANIMATOR_DURATION_SCALE == 0` → 胶片栏直达终点（FILM-03）。

### 1.10 存储：SAF 取代宽权限

- 实现 `IFileRef` / `IFolderRef`（对标 `MacStorageRefs` / `iOSStorageRefs`）：`ACTION_OPEN_DOCUMENT_TREE` 选文件夹，`takePersistableUriPermission` 持久授权，`DocumentsContract` 遍历与读写。
- **移除** `MANAGE_EXTERNAL_STORAGE` / `WRITE_EXTERNAL_STORAGE`；`READ_EXTERNAL_STORAGE` 只在 API ≤ 32 保留（供旧壳与缩略图既有路径）。
- **写回链路（2026-10-18 评审补充）**：`ACTION_OPEN_DOCUMENT_TREE` 用 `takePersistableUriPermission(Read|Write)`；单文件 `ACTION_OPEN_DOCUMENT` 加 `FLAG_GRANT_WRITE_URI_PERMISSION`。现有 `AndroidXmpWriter` 已支持 `content://`（`OpenFileDescriptor("rw")`），但 `IFileRef.Path` 必须返回 content URI；SD 卡上 `"rw"` 失败改 `"rwt"`。外部打开走原生 `IFileRef`，不调 `AndroidExternalOpenBridge` 的 Avalonia 反射路径。
- 外部打开：`AndroidExternalOpenBridge` 改为直发原生存储引用（不再经 Avalonia 反射）。

### 1.11 入口切换

- 新增 `AndroidBootstrapActivity`（launcher，不继承 Avalonia）：读启动开关后分派到 `AndroidNativeActivity`（原生壳）或 `MainActivity`（旧壳）。
- **Manifest / launcher 迁移**：bootstrap 接管 `MainLauncher=true` 与 `ACTION_VIEW` / `ACTION_SEND` / `ACTION_SEND_MULTIPLE`（`image/*`）；`MainActivity` 去掉 launcher 与外部打开 filter；bootstrap 解析 `--pv-flags` 后决定壳并把 Intent 转发给对应壳。否则点图标会直接进旧壳，外部打开也绕过 bootstrap。
- **Application 不能晚于 Activity**：清单里的 Application 类由系统在 Activity 之前实例化，当前 `AndroidApp` 继承 `AvaloniaAndroidApplication<App>`，`OnCreate` 就会初始化 Avalonia。改为普通 `Application`；旧壳 `MainActivity` 改 `AvaloniaActivity`，在 `OnCreate` 前用 `AppBuilder.Configure<App>().UseAndroid().SetupWithLifetime(自定义 ISingleViewApplicationLifetime)` 自建 Avalonia。`AvaloniaMainActivity` 依赖 internal `IAndroidApplication.Lifetime`，普通 Application 下不可用；`AvaloniaActivity` 只要求 `Avalonia.Application.Current`，可行。必须实测 `--legacy-ui` 旧壳仍启动。
- Debug 默认原生；`--legacy-ui` 回退。Android 无托管 `Main(args)`，开关从 Intent extra 读：`adb shell am start -n cc.xiaojh.PhotoViewer/.AndroidBootstrapActivity --es pv-flags "--legacy-ui"`。
- 原生路径**不初始化 Avalonia**（不创建 `Application` / `AppBuilder` / 窗口 / 位图适配器）；“不构造 `AndroidApp`”按上一条改成“普通 `Application` 不触碰 Avalonia”。

### 1.12 依赖

`Directory.Packages.props` 补：`Xamarin.Google.Android.Material`（先钉 1.14.0.6，与 Avalonia 12.0.2 的 AndroidX 冲突则退 1.13.0.6）、`Xamarin.AndroidX.RecyclerView`（1.4.0.6）、`Xamarin.AndroidX.Window`（1.5.1.3，insets 兼容层）、`Xamarin.AndroidX.Fragment`（1.9.0）。A1 立即构建验证 `Theme.Material3.Dark.NoActionBar` 与独立 `postSplashScreenTheme` 链。`minSdk` 维持 24（ONNX Runtime AAR 门槛），P3 探测在 API < 28 上直接回退 sRGB。

- **DINO 模型（2026-10-18 评审补充）**：模型 86MB 在 `PhotoViewer` 的 Avalonia 资源里，原生壳不加载 Avalonia，旧 `AppResources` 路径不可用。新增 `AndroidAppResourceProvider`（`AssetManager.Open`），csproj 用 `AndroidAsset` 打包 `..\PhotoViewer\Assets\Models\*.onnx`；APK 会多约 86MB，安装/冷启动时间要记录。
- **平台门面**：补 `AndroidUiDispatcher`、`AndroidFilePicker`、`AndroidNativeWindowPresenter`、`AndroidNativeSettingsPresenter`、`StorageAccessManager`、`OnTrimMemory` 压力上报。

## 2. 复用边界

| 层 | 复用方式 |
|---|---|
| 共享 VM / 会话 / presenter | `MainViewModel(nativePresentation: true)`、`PhotoLoadSession`、`ThumbnailListViewModel`、`ControlViewModel`、`SimilarityPanelViewModel`、`AnalysisPresenter`/`IAnalysisHost`、`DinoDiagnosticPresenter`、`ToolsViewModel`、`NativeSettingsSession` 直接复用 |
| 共享几何（新建） | `PhotoViewport`、`MainScreenLayout`、`FilmstripGeometry`、`ControlBarLayout`、`AnalysisPanelGeometry`、`SimilarityPanelGeometry`、`ExifTableGeometry` |
| 平台中立 Core | `BitmapLoader`（分级代理解码/准入/租约）、`DecodeScheduler`、`MemoryGovernor`、`PhotoDatabase`、`XmpWriter`、`DinoFeatureExtractor`、`CvGridExtractor`、`SkiaImageCodec` |
| 已有 Android Core | `AndroidHeifDecoder` / `AndroidLibHeifDecoder` / `AndroidMemoryBudgetProvider` / `AndroidPerformanceBudget` / `AndroidSettingsStorage` / `AndroidXmpWriter` 直接保留 |
| 需新写 | `AndroidPresentationDecoder` + `AndroidPresentationEncodingPolicy`、SAF 存储引用、`AndroidInputMapping`、GLES 渲染核心、主屏/胶片栏/卡片/控制栏/分析栏/相似面板/工具页、设置 sheet、验收驱动器 |

## 3. 里程碑

### A1 基座 + 共享源拆分
- 新建 `PhotoViewer.Shared/`，迁入 §1.6 的 7 个几何文件；`PhotoViewer.Apple/` 与三个 head 改链接方式；Mac 八套 + iOS 七套 `--pv-native-*` 回归零行为变化（**前置门**）。
- 拆 `AppleInputMapping`，抽 `InputCommandMap`（只放 `CommandForGesture`）。
- `AndroidBootstrapActivity` + `AndroidNativeActivity` + `AndroidNativeShell`：启动序对齐 `iOSNativeShell`（主线程 `SynchronizationContext` → ReactiveUI → 门面注入 → `MainViewModel` → 根视图）；`--legacy-ui` 回退；旧壳回归可启动。
- 按 §1.11 改普通 `Application` + `AvaloniaActivity` 旧壳；bootstrap 接管 launcher / 外部打开 filter。
- `Theme.PhotoViewer.Native` 落地（§1.3），`AndroidNativeActivity` 上用。
- 补 §1.12 依赖与 `AndroidAppResourceProvider`/门面，构建零错误零新增警告。
- **状态**：**A1a + A1b 已完成（2026-10-18）**。
  - A1a：Mac base **39 checks** + `input/toolbar/layout/analysis/similarity/tools/filmstrip` 七套 PASS，iOS 模拟器七套全 PASS（photo 10 / gesture 24 / layout 24 / input 29 / analysis 18 / similarity 10 / tools 7）；`--pv-native-settings` 为抓屏权限假阴性（迁移前基线同，窗口目视正常，§6#10）。
  - A1b：普通 `Application` + `AndroidBootstrapActivity` + `AndroidNativeActivity`/`AndroidNativeShell` + `Theme.PhotoViewer.Native` + `AndroidAppResourceProvider`/`AndroidUiDispatcher`/门面占位落地；旧壳 `MainActivity : AvaloniaActivity` 自建 Avalonia。Debug 构建 **0 错误 / 21 警告**（与基线一致）；API 35 arm64 模拟器默认启动 `[Native] Avalonia.Application.Current=null`、`DINO 模型资源=ok`，`--legacy-ui` 旧壳 `MainView=True` 正常；首次启动 native `+940ms` / legacy `+1.09s`。ONNX Linux glibc `.so` 误打包已修（§6#11）。A2 未开始。

### A2 主图
- `AndroidPresentationDecoder`（`BitmapFactory` + libheif 兜底，产出 8 位 sRGB 或 Display P3）+ `AndroidPresentationEncodingPolicy`（恒 Rgba8）；`BitmapLoader.InitializePresentationDecoder` 注入。EXIF 方向只写 `DecodedImage.Orientation`，顶点阶段置换 UV；本地路径与 SAF `content://` 都支持。
- `AndroidPhotoRenderer`（EGL14 + GLES 3）：EGL 表面 P3 协商（`EGL_EXT_gl_colorspace_display_p3`，失败回退默认 sRGB）、GL_RGBA8 纹理、mipmap/三线性、八方向 UV、视口矩形绘制、分块纹理上传、纹理字节记账。
- `AndroidPhotoView`（`SurfaceView` 宿主 + 自有 EGL）：`PhotoViewport` 以 dp 为点、`BackingScale=density`；单指放大态平移、双指捏合锚定焦点、双击/Fit 命令切换、误触阈值与适应态横滑翻页；物理 100% 触发 Full；代理↔Full 只换纹理；小型素材物理 100% 低于适应态时夹回适应（iOS 同口径）。
- `AndroidNavigationDecoder`：导航图改为按长边 300dp 的 BitmapFactory 子采样 + 方向转正，替代 GPU 离屏回读（理由见 §6#18）。
- **自验**：方向 6 照片按显示朝向铺满；`color-p3-large` 6000×4000 代理→Full 上屏、Fit→100% 导航图与百分比正确；**程序化 harness 未落地**。
- **状态**：**已完成（2026-10-18，模拟器目视/日志）**；八方向全量、色卡线性值、双击精确物理 100%、代理→Full 变换保持的自动断言随 A6 补。

### A3 主屏布局
- `AndroidMainScreenLayout` 在共享 `MainScreenLayout.Partition` 上叠加真实系统栏/刘海 insets：栏位背景铺到屏幕边缘、内容收在安全区、主图满幅；行/列/自动只看窗口方向，隐藏栏位把空间还给主图；筛选条行布局 `alignLeading: true`。
- 胶片栏 `RecyclerView` + 自绘卡片（96×144 外框 / 90×138 占位 / 80 方形 / 星级 / 抖动徽标 / 第二行；当前项蓝框、Full 缓存细边、占位/加载态）；350ms 三次缓出定位、用户触摸接管、减少动态效果直达；可见范围上报共享缩略图调度器；点选不居中。
- 筛选条：自绘 90×32 下拉 ×3 + 计数 + 相似聚类 toggle 位置（toggle 在 A5 前隐藏）；点按弹原生 `PopupMenu` 写回共享 VM。
- 控制栏：共享 `ControlBarLayout` 的分组/换行/居中；EXIF 块、评分、命令三组自绘；命令显隐/顺序来自共享设置；A5 前隐藏「分析栏」命令。
- 导航图：普通 View 覆盖在 `SurfaceView` 之上，长边 150dp、可见框与百分比，点按/拖动定位。
- **状态**：**已完成（2026-10-18，模拟器目视/日志）**；两向、栏位显隐、卡片几何与 350ms 中间帧的自动断言随 A6 补。

### A4 输入 + 设置
- `AndroidInputMapping`：`KeyEvent`/修饰键 → 共享 `GestureKey`/`GestureModifiers`，查共享命令表；Activity `DispatchKeyEvent` 在设置 sheet/文本输入期间让位；DPAD Right/F 已实测触发 Next/Fit。
- 离散鼠标滚轮按用户绑定执行命令；主图长按菜单（打开文件夹/栏位显隐/增强/工具/设置，勾选态现读共享状态）；卡片长按评分菜单；适应态单指横滑翻页；返回键先关设置 sheet 再结束 Activity。
- 半屏设置：Material 3 `BottomSheetDialog` + 共享 `NativeSettingsSession` 工作副本；五分类（文件/预览/控制/EXIF/AI）与 iOS 同口径；开关、数值步进、布局模式选择、文件格式/快捷键/EXIF 列表勾选 + 上下排序、缩放预设增删改、相似阈值/数量；关闭/下滑/返回统一 `Apply` 一次回写，主屏即时刷新。
- **偏差**：数值控件用 +/- 步进而非滑条（功能等价、点击精度更高）；列表排序暂用上下按钮而非拖拽（`ItemTouchHelper` 待补）；拖入（`OnDragListener`）未接。
- **状态**：**已完成（2026-10-18，模拟器目视/日志）**；真实键鼠手感、fontScale 1.3、拖入与真机返回手势待 A6/作者。

### A5 P3 功能
- 分析栏：`AndroidAnalysisHost` 实现共享 `IAnalysisHost`，`AndroidAnalysisPanel` 消费 `AnalysisPresenter`（只读库快路径 / 增强联动 / 未提取回退全部复用），按 `AnalysisPanelGeometry` 自绘中心/对焦细节预览、直方图与 4 张 DINO-CV 瓦片；细节裁剪走 `DisplayCrop`（代理期留空）、诊断瓦片点按落准星、细节点按定格并在主图画 3dp 绿框、点主图空白解除；分析栏可见时视口上报 `NeedsFull`。
- 相似聚类：筛选条 toggle（行布局在窄屏用紧凑宽度，保证不越界）→ `AndroidSimilarityPanel`（RecyclerView + 复用卡片，第二行分数 % + 75% 起柱状条；空态/未提取/动作区/进度读共享 VM；“提取全部”真实触发批量索引，完成后卡片出分数）；行布局从胶片栏右侧切出 `SimilarityPanelGeometry.RowWidth` 宽度，列布局接在文件栏后。
- 工具页：`AndroidToolsPresenter` + `BottomSheetDialog`；列表页只有 EXIF 详情 / DINO 诊断（照片数据统计不建入口）；EXIF 页按 `ExifTableGeometry` 的 60dp ID 列 + 2:2:3 文本列渲染分组表格，支持同名多格式切换；DINO 页只消费共享 `DinoDiagnosticPresenter`（5 瓦片 3+2 + 刚体拟合文本 + 点按准星）。入口：主图长按菜单「工具…」与控制栏 EXIF 信息块。
- 增强预览：`PhotoLoadSession.ToggleEnhanced` 已可用，主图与导航图随增强图替换；`IsEnhancing` 显示“计算中…”。
- **偏差**：分析栏在手机上按共享几何排 6–7 个瓦片（约 58dp），未做手机专用放大/横滑；相似面板空态动作按钮位于占位下方（列表态停靠尾部）；工具页 EXIF 表用 4 个可见列承载共享 7 列几何，手机宽度下不做水平滚动；DINO 抖动瓦片矢量线为简化绘制。
- **状态**：**已完成（2026-10-18，模拟器目视/日志）**；A5 程序化 harness（分析栏两向、细节/直方图像素、相似让位、DINO 5 瓦片）随 A6 补。

### A6 验收与文档
- 模拟器验收驱动器：`--pv-native-{photo,layout,input,analysis,similarity,tools}-check` 同级，结果写 `Documents/PhotoViewer/android-checks.txt` 并 `adb pull`；隔离自有设置/数据库/素材。
- 模拟器矩阵按抽样跑（2026-10-18 评审补充，避免 40 种组合全跑）：API 24 / 28 / 33 / 35 / 36 只跑纵向启动/几何；API 35 + 24 补横向；fontScale 1.0 / 1.3 与两档 DPI 只在 API 35 跑；真机只补手感/色彩/SAF/杀后台。
- 真机门：在作者安卓手机上执行，见 [验收清单 §6–§8](native-rebuild-phase-android-acceptance.md)；多 ROM / 多系统版本覆盖需借机，未覆盖机型保持未关闭。
- 文档：新建 `PhotoViewer.Android/AGENTS.md`；根 `AGENTS.md` 项目结构/模块索引、[架构文档](../Design/architecture.md) §4/§5（新增 Android 原生链路小节）；`PhotoViewer.Shared/` 文件表；ui-contract Android 列；DEV.md 任务；handoff 状态与验证矩阵。
- **状态**：未开始。

## 4. 验收门

1. 四个 head 构建零错误、无新增警告；共享源迁移后 Mac/iOS 全部原生回归 PASS（零行为变化）。
2. 模拟器矩阵（5 个 API 级 × 两向）全部启动、无崩溃、几何正确；fontScale 1.3 下布局不破。
3. 100% 放大永远 Full、热替换无跳变；八方向与色卡程序化通过；P3 与 sRGB 回退两条路径都验证到。
4. FILM / FILTER / TOOLBAR / SIM / ANALYSIS / SETTINGS 契约 Android 列填到"模拟器程序化通过"或"真机手验通过"；未通过项不得以其他平台结果代替。
5. 真机门（作者设备到位后）：字体替换、厂商杀后台、真实 P3 面板、手势手感、SAF 与第三方文件管理器协作。

## 5. 风险

| 风险 | 应对 |
|---|---|
| ROM 修改主题派生色导致 Material 组件变色 | 钉死主题整包（§1.3）；A1 即落地；模拟器无法覆盖，靠真机门兜底 |
| 厂商省电策略杀进程，外部打开回来丢状态 | 保存/恢复 `savedInstanceState`；启动路径短；登记为已知限制 |
| `SurfaceView` 与覆盖层合成错位/黑边 | A2 先用最小覆盖层验证；必要时改 `TextureView`（接受性能代价） |
| EGL P3 探测在部分 GPU 上假成功 | 探测结果与实际 `getSupportedWideColorGamut()` 交叉校验；色卡对拍兜底 |
| 只有一台真机，ROM/系统版本覆盖不足 | 模拟器矩阵承担几何与 API 跨度；作者手机承担手感/色彩/存储/杀后台；小米（MiSans）、OPPO（OPPO Sans）、华为（HarmonyOS Sans）字体与新旧系统版本在借机时逐台补，未覆盖项保持未关闭 |
| `RecyclerView` 复用与自绘卡片状态竞态 | 卡片 `onDraw` 只读不可变快照，状态由 VM 推送；A3 加滚动专项 |
| 共享源迁移触碰 Apple 两壳 | A1 独立提交，回归门未过不进入 A2 |

## 6. 发现清单

| # | 现象 | 处理 |
|---|---|---|
| 1 | `AndroidManifest.xml` 当前申请 `MANAGE_EXTERNAL_STORAGE`，Google Play 会拒审（本仓 Android 直发，不影响分发，但 SAF 迁移后应移除） | A1 移除 |
| 2 | `AndroidHeifDecoder` 通道序依赖历史口径（phase-0 已登记），行为不变保留，Phase 2 色彩管线统一时一并核对 | A2 复核 |
| 3 | `application android:largeHeap="true"` 在原生壳下是否仍需要，需按 `MemoryGovernor` 实测复核 | A2 复核 |
| 4 | 旧壳 `MainActivity` 继承 `AvaloniaMainActivity`，原生分派必须走独立 bootstrap activity，不能在同 activity 内切 | 已在 §1.11 定案 |
| 5 | 现有 `values-v31/styles.xml` 的 `postSplashScreenTheme` 指向 `MyTheme.Main`，原生主题需另一条 splash 链 | A1 处理 |
| 6 | Android 构建基线不是 phase-2 记录的 14 条：Windows 2026-10-18 实测 **21 警告 / 0 错误**（NU1903、CS86xx、CA1422、XA4301），Mac 侧本机为 9 警告 | 以最新构建日志冻结基线，不把既有警告算新增 |
| 7 | Mac/iOS workload 27.0.10722 把 macOS 最低部署版本抬到 **14.0**，Mac head 原写 12.0，`dotnet build` 直接报 `SupportedOSPlatformVersion ... lower than the minimum value '14.0'`；`Info.plist` 的 `LSMinimumSystemVersion` 也需同步 | 2026-10-18 已把 Mac csproj 与 Info.plist 对齐 14.0（设置窗口 SwiftUI 本就以 macOS 26 为目标） |
| 8 | workload 升级后首次构建 iOS 会因旧 `obj/bin` 残留报 `Undefined symbols for architecture arm64: _xamarin_*` | 删除 `PhotoViewer.iOS/obj`、`bin` 后重建即通过；升级工具链后先做一次干净构建 |
| 9 | 本机 macOS 环境缺 Android 工具链：默认 SDK 10.0.402 无 `android` workload，SDK 只有 platform 36 / build-tools 36 / platform-tools，**无 emulator 与 system-images** | 2026-10-18 已补 platform 35 / emulator / arm64-v8a 镜像与 AVD `pv-api35`；**10.0.201 下已有 android workload 36.1.53**，用临时 `global.json` pin 10.0.201 构建 Android 头 0 错误 / 21 警告并成功启动旧壳；10.0.402 仍需 `sudo dotnet workload install android` |
| 10 | `--pv-native-settings` 的“勾选后重绘”断言依赖 `screencapture -l`：本机未授予屏幕录制权限时回退到 `cacheDisplay`，SwiftUI 图层导出为空白，断言必失败；迁移前基线同样失败，KimiCU 目视窗口内容正常 | 登记为假阴性；授予权限或改用可捕获图层树的离屏渲染后复跑。A1a 结论不据此改判 |

| 11 | `Microsoft.ML.OnnxRuntime` 1.24.4 在 net10.0-android 下把 `runtimes/linux-arm64/native/*.so` 放进 `FrameworkNativeLibrary`，APK 的 `lib/arm64-v8a/libonnxruntime.so` 因而是 glibc 版（依赖 `libdl.so.2`），AAR 的 bionic 版被忽略，`libonnxruntime4j_jni.so` 启动即 `UnsatisfiedLinkError` | **已修**：csproj `_PvRemoveLinuxOnnxFromApk` 在 `_BuildApkEmbed` 前移除 `runtimes/linux-arm64/native/*.so`；APK 现为 AAR 的 bionic 版（md5 5316513a…），启动无该错误 |
| 12 | Avalonia 12 的 `ISingleViewApplicationLifetime`/`IApplicationLifetime` 标为 `NotClientImplementable`，手写 lifetime 无法编译 | 旧壳实例化 `Avalonia.Android.ApplicationLifetime`（internal）并走 `IActivityApplicationLifetime.MainViewFactory`；待上游公开 API 后移除反射 |
| 13 | `AvaloniaActivity.InitializeAvaloniaView` 只创建 `AvaloniaView`，不调用 `SetContentView`；在 `base.OnCreate` 前写 `Content` 会白屏 | 旧壳在 `base.OnCreate` 后写 `Content`，触发 setter 的 `SetContentView` 分支；已实测主界面正常 |
| 14 | `AndroidAsset` 打进 APK 的实际路径是 `assets/Models/...`，共享 URI 是 `avares://PhotoViewer/Assets/Models/...` | `AndroidAppResourceProvider` 去掉开头 `Assets/`；原生壳启动日志 `DINO 模型资源=ok` 验证 |
| 15 | SAF 选择器、设置 BottomSheet、工具页当前是占位（返回 null / Toast） | A2/A3/A4 已替换 SAF 与设置；工具页仍占位，A5 处理 |
| 16 | 模拟器（API 35 arm64，ANGLE）`P3表面=False`，EGL P3 协商走 sRGB 回退；真实 P3 面板未验 | A2 代码探测 `Window.IsWideColorGamut` + EGL 扩展双条件；真机门补 P3 正向与色卡 |
| 17 | 3+3 邻居预取同时解 Full 时，13 张夹具（含 6000×4000）启动后约 0.5s 进程 SIGSEGV；1/1 或全代理稳定 | **已修**：共享 `BitmapLoader.PrefetchFullNeighbors` 默认 true，Android 壳置 false，邻居预取统一代理档；Full 只在当前图放大时按需解码。Mac/iOS/Windows 行为不变 |
| 18 | 导航图最初用 FBO `glReadPixels` 回读：缩略图内容错位/通道异常，且多一次 GPU 同步 | 改为 `AndroidNavigationDecoder`（BitmapFactory 子采样 + 方向转正，长边 300dp）；导航图仍是 150dp 显示，点按/拖动定位不变。与计划"GPU 小目标渲染"有偏离，登记为 Android 实现差异 |
| 19 | Material 默认紫色主色与 iOS 系统蓝不一致 | 主题与 BottomSheet 显式 `colorPrimary/Secondary=#0A84FF`；开关/复选框跟随；不使用 DynamicColors |
| 20 | 安装 APK 可能清空 App 私有目录（增量安装/数据清理），夹具与设置会丢 | 验收夹具每次用 `run-as` 重新拷入 `files/pv-fixtures`；程序化 harness 需自建隔离目录（A6） |
| 21 | 设置列表排序暂用上下按钮，拖拽排序未接；拖入未接 | 记为 A4 偏差；A6 前补 `ItemTouchHelper` 与 `OnDragListener`，或作者裁定接受按钮排序 |
| 22 | 主屏上下文菜单暂未含「分析栏」（A5 前该命令隐藏），「工具…」仍为占位 | **已解决（A5）**：菜单补分析栏勾选项，控制栏命令解禁，工具页真实入口 |
| 23 | 手机宽度 411dp 放不下 3×90dp 筛选控件 + 计数 + 90dp 相似 toggle | 共享 `FilterBarFrames` 增加可选 `controlWidth/toggleWidth`（默认 90 不变）；Android 行布局窄屏用 84/76dp 与“13 张”紧凑计数，其他平台行为不变 |
| 24 | 分析栏在手机上瓦片仅约 58dp，细节预览可用但不适合多点操作 | 沿用共享几何与 iPhone/iPad 同构；手机专用放大/横滑未做，登记为 A5 偏差，待作者裁定 |
| 25 | 相似面板空态动作按钮位于占位下方、列表态停靠在尾部；无进度条（用文本进度） | 功能等价；进度条与拖拽排序一样留 A6/作者裁定 |
| 26 | 工具页 EXIF 表只显示 ID/名称/中文名/值四列，未做 7 列水平滚动；格式切换按钮为自绘胶囊 | 功能对齐（分组/格式切换/值 3 行截断），视觉差异登记 |

## 7. Windows 评审修订写回（2026-10-18）

> 来源：[native-rebuild-android-handoff.md](native-rebuild-android-handoff.md) §3（Windows 侧只做计划评审与基线构建）。以下 11 条已写回正文；与正文早期文字冲突时，以本节为准。

| # | 结论 | 落点 |
|---|---|---|
| 1 | Android 的 Application 由系统在 Activity 前实例化，「原生路径不构造 `AndroidApp`」字面不可实现。改普通 `Application`；旧壳 `MainActivity` 改 `AvaloniaActivity` 并在 `OnCreate` 前自建 Avalonia；必须实测 `--legacy-ui` 仍启动 | §1.11、A1 |
| 2 | 共享几何一律 **dp 当点**；`PhotoViewport` 用 dp，`BackingScale = Density`，只在 View 边界做 px↔dp | §1.5、A2 |
| 3 | `InputCommandMap` 只放 `CommandForGesture`；Apple 的修饰键解码/HID 表留在 `AppleInputMapping`；Android 另写 `AndroidInputMapping` | §1.6、A1/A4 |
| 4 | `AndroidBootstrapActivity` 接管 launcher 与 `ACTION_VIEW`/`SEND`/`SEND_MULTIPLE`；`MainActivity` 去掉 launcher 与外部打开 filter | §1.11、A1 |
| 5 | DINO 模型走 `AndroidAppResourceProvider` + `AndroidAsset`（约 +86MB）；补 `AndroidUiDispatcher`/`AndroidFilePicker`/`AndroidNativeWindowPresenter`/`AndroidNativeSettingsPresenter`/`StorageAccessManager`/`OnTrimMemory` | §1.12、A1 |
| 6 | 缩放指示器归 A3：`NavigationSize`/`NavigationViewport`，覆盖在 `SurfaceView` 上的普通 View，长边 150dp；点按/拖动定位可作平台增强 | A3 |
| 7 | 行布局筛选条显式 `alignLeading: true`，与 iPad 同口径 | §1.7、A3 |
| 8 | SAF 写回：`takePersistableUriPermission(Read\|Write)`、`FLAG_GRANT_WRITE_URI_PERMISSION`、SD 卡 `"rw"` 失败改 `"rwt"`；`IFileRef.Path` 返回 content URI | §1.10、A2 |
| 9 | A6 矩阵抽样：5 个 API 只跑纵向；API 35 + 24 补横向；fontScale 与 DPI 只在 API 35；真机补手感/色彩/SAF/杀后台 | A6 |
| 10 | 基线冻结为「Android 21 警告 / 0 错误」「iOS 7 套回归」，不再沿用 14 条/6 套的旧数字 | §6、A1/A6 |
| 11 | Material 版本先钉 Material 1.14.0.6（冲突退 1.13.0.6）、RecyclerView 1.4.0.6、Window 1.5.1.3、Fragment 1.9.0；A1 立即构建验证主题与 splash 链 | §1.12、A1 |
