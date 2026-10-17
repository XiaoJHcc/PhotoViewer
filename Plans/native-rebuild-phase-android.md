# Android 原生壳执行清单（Views + GLES）

> 2026-10-18 立项，隶属 [native-rebuild-master-plan-1.md](native-rebuild-master-plan-1.md) Phase 5（"Android Views + GLSurfaceView P3 探测 + SAF 迁移"）。前置：macOS 原生壳已验收、iPad 原生壳 I1–I5 程序化完成、Windows 原生壳主屏/设置/工具程序化完成。
>
> **作者要求（2026-10-18）**：
> 1. 安卓以**手机**为主（平板少见、作者无测试机），**对标 iOS 版**；首屏对标 Avalonia 版或 iOS 版皆可（两者结构一致）。
> 2. 设置是**底部浮起半屏**（参考 iOS），选项尽量找可对标 iOS 的控件。
> 3. **无论 ROM 与系统版本，视觉设计与空间操作一致对标**。
> 4. 能用原生最好；原生不可靠再议。
>
> **进度**：仅本清单，尚未开工。

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
- **覆盖层**（导航图、绿框、分析栏、状态提示）放在 `SurfaceView` 之上的普通 `ViewGroup`，不在 GL 里画；`SurfaceView` 用默认 `setZOrderOnTop(false)` + 覆盖层同窗口合成。

### 1.6 共享源：把 Apple 共享源提成"壳共享源"

- 新建 `PhotoViewer.Shared/`（纯源目录，`Compile Include` 链接，不新增程序集），迁入 7 个**零 Apple 依赖**的纯数学文件（已核 `using` 为空）：`PhotoViewport`、`MainScreenLayout`、`FilmstripGeometry`（含 `FilmstripAnimation`）、`ControlBarLayout`、`AnalysisPanelGeometry`、`SimilarityPanelGeometry`、`ExifTableGeometry`。
- `PhotoViewer.Apple/` 改为链接 `PhotoViewer.Shared/`，Mac/iOS 两个 csproj 各加一行；`PhotoViewer.Android/` 同样链接。**迁移门 = Mac 八套 + iOS 六套 `--pv-native-*` 回归零行为变化**。
- 拆 `AppleInputMapping.cs`：`CommandForGesture` 与修饰键解码进 `PhotoViewer.Shared/InputCommandMap.cs`；HID usage → `GestureKey` 表留在 Apple；Android 新写 `AndroidInputMapping`（`KeyEvent` → 同一张 `AppGesture` 表）。
- 留在 Apple 的：`MetalPhotoRenderer`、`ApplePresentationDecoder`、`AppleAcceptanceChecks`、`EnhancementAcceptance`、`AppleSymbolNames`。
- 命名空间随之改为 `PhotoViewer.Shared`（Apple/Android 头各加一个 `global using` 或直接改引用）。

### 1.7 布局：手机只走行布局

- 复用共享 `MainScreenLayout` 全量数学，不抄常量。手机纵向 400dp 宽放不下 116dp 文件栏 + 主图，故纵向恒为行布局（文件栏在上、控制栏在下）；横向按 `IsRowLayout` 的 Auto 规则走列布局，此时 116dp 栏可用。
- **不新增**手机专属布局模式；若作者后续认为"胶片栏应在下方"，作为平台差异单独立项。
- **insets**：`WindowCompat.setDecorFitsSystemWindows(window, false)` + `OnApplyWindowInsetsListener` 吃 `systemBars()` 与 `displayCutout()`；栏位背景铺到屏幕边缘、内容收进安全区（与 iPad 同口径）。Android 15（API 35）强制 edge-to-edge，此路径是必做项。
- 刘海：`displayCutout` 的 left/right 在横屏时计入内容内边距。

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
- 外部打开：`AndroidExternalOpenBridge` 改为直发原生存储引用（不再经 Avalonia 反射）。

### 1.11 入口切换

- 新增 `AndroidBootstrapActivity`（launcher，不继承 Avalonia）：读启动开关后分派到 `AndroidNativeActivity`（原生壳）或 `MainActivity`（`AvaloniaMainActivity`，旧壳）。
- Debug 默认原生；`--legacy-ui` 回退。Android 无托管 `Main(args)`，开关从 Intent extra 读：`adb shell am start -n cc.xiaojh.PhotoViewer/.AndroidBootstrapActivity --es pv-flags "--legacy-ui"`。
- 原生路径不创建任何 Avalonia 对象（不构造 `AndroidApp`）。

### 1.12 依赖

`Directory.Packages.props` 补：`Xamarin.Google.Android.Material`、`Xamarin.AndroidX.RecyclerView`、`Xamarin.AndroidX.Window`（insets 兼容层）、`Xamarin.AndroidX.Fragment`。`minSdk` 维持 24（ONNX Runtime AAR 门槛），P3 探测在 API < 28 上直接回退 sRGB。

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
- 新建 `PhotoViewer.Shared/`，迁入 §1.6 的 7 个几何文件；`PhotoViewer.Apple/` 与三个 head 改链接方式；Mac 八套 + iOS 六套 `--pv-native-*` 回归零行为变化（**前置门**）。
- 拆 `AppleInputMapping`，抽 `InputCommandMap`。
- `AndroidBootstrapActivity` + `AndroidNativeActivity` + `AndroidNativeShell`：启动序对齐 `iOSNativeShell`（主线程 `SynchronizationContext` → ReactiveUI → 门面注入 → `MainViewModel` → 根视图）；`--legacy-ui` 回退；旧壳回归可启动。
- `Theme.PhotoViewer.Native` 落地（§1.3），`AndroidNativeActivity` 上用。
- 补 §1.12 依赖，构建零错误零新增警告。
- **状态**：未开始。

### A2 主图
- `AndroidPresentationDecoder`（`BitmapFactory` + libheif，产出 8 位 sRGB 或 Display P3）+ `AndroidPresentationEncodingPolicy`；`BitmapLoader` 注入。
- `AndroidPhotoRenderer`（EGL14 + GLES 3）：P3 探测、纹理上传与 mipmap、八方向 UV、帧提交、纹理字节记账。
- `AndroidPhotoView`（`SurfaceView` 宿主）+ 手势 → `PhotoViewport`；物理 100% 触发 Full；代理↔Full 只换纹理。
- **程序化门**（模拟器）：八方向角点、色卡、代理→Full 变换保持、双击精确 100%、Fit 双向、切图保持倍率/中心。
- **状态**：未开始。

### A3 主屏布局
- 根布局按 `MainScreenLayout` 落 frame；行/列/自动；insets 与刘海；栏位显隐释放空间。
- 胶片栏 `RecyclerView` + **自绘卡片**（`FilmstripGeometry`：96×144 外框 / 90×138 占位 / 80 方形 / 星级 / 抖动徽标 / 第二行）；350ms 三次缓出与用户接管。
- 筛选条（三个下拉 + 计数 + 相似聚类 toggle）、控制栏三分组换行居中。
- **状态**：未开始。

### A4 输入 + 设置
- `AndroidInputMapping`（`KeyEvent` → `GestureKey`）、长按菜单、卡片评分菜单、轻扫翻页、拖入、返回手势、减少动态效果。
- 半屏设置 sheet（§1.8），五分类全量接线，关闭回写后主屏即时刷新。
- **状态**：未开始。

### A5 P3 功能
- 分析栏（`IAnalysisHost` 宿主视图 + 共享 presenter）、相似聚类面板、增强预览（`PhotoLoadSession`）。
- 工具页：`BottomSheetDialogFragment` 内的 EXIF 详情 / DINO 诊断（复用 `DinoDiagnosticPresenter` 与共享诊断瓦片几何）。
- **状态**：未开始。

### A6 验收与文档
- 模拟器验收驱动器：`--pv-native-{photo,layout,input,analysis,similarity,tools}-check` 同级，结果写 `Documents/PhotoViewer/android-checks.txt` 并 `adb pull`；隔离自有设置/数据库/素材。
- 模拟器矩阵：API 24 / 28 / 33 / 35 / 36 × DPI 两档 × fontScale 1.0 / 1.3 × 两向。
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
| 6 | Android 构建有 14 条既有基线警告（phase-2 已登记） | 保持基线，不压警告 |
