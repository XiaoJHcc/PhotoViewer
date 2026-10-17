# PhotoViewer.iOS — 原生壳（I1 基座 + I2 主图 + I3 主屏布局）与迁移期旧壳

I1 起：Debug 默认 **UIKit 原生壳**（不创建任何 Avalonia 对象），旧壳经启动参数 `--legacy-ui` 或 NSUserDefaults 开关 `PVLegacyUI`（`defaults write cc.xiaojh.PhotoViewer PVLegacyUI -bool YES`）回退；Release 默认旧壳，`--native` 启动原生。最低系统版本 **iOS 17**。执行清单见 [Plans/native-rebuild-phase-ipad.md](../Plans/native-rebuild-phase-ipad.md)；I1 基座、I2 主图（Metal + `UIScrollView` 手势 + 编码协商）、I3 主屏布局（文件栏/胶片栏/筛选条/控制栏、安全区、两向与自动布局）已落地，输入（I4）与工具页（I5）未落地：控制栏不生成对应入口（SCOPE-01），分析栏在共享布局数学里保留槽位但交叉轴为 0。

**启动参数来源**：iOS 不填充托管 `Main(string[] args)`（实测为空数组），一律读 `NSProcessInfo.ProcessInfo.Arguments`（`simctl launch <udid> <bundleId> <args…>` 会传入）。

**Apple 共享源**：`../PhotoViewer.Apple/` 纯源目录由 csproj `<Compile Include="..\PhotoViewer.Apple\**\*.cs"/>` 链接编译（展示解码器、Metal 渲染核心、视口/胶片栏/控制栏/分析栏几何、验收判定、命令符号与手势映射），与 [PhotoViewer.Mac](../PhotoViewer.Mac/AGENTS.md) 共用同一份，见 [其手册](../PhotoViewer.Apple/AGENTS.md)。本壳只保留 UIKit 宿主。改共享源需同时复核 Mac 的 `--pv-native-*` 回归。

**I2 真机实测限制**：iOS 的 ImageIO 读 Sony ARW 返回**内嵌预览**（真机实测 `req=4532 → out=1616x1080 props=1616x1080`；Mac 同文件为 `7008x4672`），产物 `Width == SourceWidth` 使 `IsProxy=false`，故 RAW 在原生壳恒为预览档、`NeedsFull` 不会升级 Full；属平台/RAW 支持口径问题，待作者裁定（旧壳 `iOSRawDecoder` 同机制）。

**主图链路（I2）**：`PhotoLoadSession` 只管代际/代理↔Full/租约；`iOSPhotoView` 把共享 `MetalPhotoRenderer` 挂成 `CAMetalLayer` 子层（绘制目标恒 RGBA16F 扩展线性 sRGB），视口取共享 `PhotoViewport`；缩放/平移/回弹/惯性由覆盖其上的透明 `UIScrollView` 驱动——内容尺寸 = 适应态显示尺寸，`zoomScale`/`contentOffset` 与视口变换互为逆映射（`iOSPhotoView` 内 `PullViewportFromScroll` / `SyncScrollFromViewport`），滚动/捏合回调同帧重绘。双击在适应与物理 100%（点按位置为锚点）间切换，用 `ZoomToRect` 一次动画完成缩放与偏移（先 `SetZoomScale` 再 `SetContentOffset` 会互相取消，实测缩放停在原值）。**编码协商**：`iOSPresentationEncodingPolicy` 给代理档 RGBA16F、Full 档 8 位 Display P3、压力 ≥ Warning 时代理档也降 8 位 P3；准入估算与纹理记账按实际编码的每像素字节（Full 档 4 字节）。**启动顺序硬要求**：必须先上报有效视口再 `SetFile`（`iOSNativeRootViewController.StartSession` + `PhotoLoadSession.UpdateViewport` 的零尺寸守卫），否则代理目标会被钳成 1 像素、解码出 1×1 的"合格代理"且永不重载。

**主屏布局（I3）**：分区几何取共享 `MainScreenLayout`（与 Mac 原生壳同一份数学），安全区口径在 `iOSMainScreenLayout` 叠加——栏位**背景**铺满屏幕边缘、栏位**内容**（筛选条/胶片栏/控制栏）收在真实 `SafeAreaInsets` 内、主图满幅；`Info.plist` 沿用 I1/I2 的隐藏状态栏，故实测 `safe.Top = 0`（如将来显示状态栏，布局按真实 insets 自动让位）。两向只看窗口尺寸（行=上下、列=左右、自动=窗口方向），隐藏栏位把空间让给主图，分析栏在数学里留槽位但 I5 前交叉轴恒为 0。**胶片栏**是 `UICollectionView` + 自定义 `UICollectionViewLayout`（几何全部来自共享 `FilmstripGeometry`，含 FILM-02 的居中偏移），卡片 `iOSThumbnailCard` 按 FILM-05 绘制（当前项蓝框取 `IsCurrent`、Full 缓存细边、80 方形照片区、文件名尾部截断、第二行拍摄时间、6 个星级、抖动徽标、占位/加载状态）；点选走独立覆盖按钮（星级按钮命中优先，点星不切图），显式定位用 `CADisplayLink` 驱动的 350ms 三次缓出、用户触摸/拖动即接管、减少动态效果直接到终点、重新显示与换向定位当前项。**筛选条**是 `UIButton`+`UIMenu` 下拉（`ShowsMenuAsPrimaryAction`）+ 计数 + 相似聚类 toggle（**本阶段隐藏**，面板属 I5，按 SCOPE-01 不显示不可用入口）。**控制栏**复用共享 `ControlBarLayout` 的分组/换行/居中，命令显隐与顺序来自共享设置，I5 未实现的分析栏/增强预览不生成按钮。临时保留一条**最小设置入口**（主图区右上角齿轮，I4 的长按上下文菜单落地后删除）与主图区**空态提示**（点击打开文件夹，与 Mac 空首屏同义）；I1/I2 的状态浮层已移除。

## 原生壳（Debug 默认）

| 文件/目录 | 职责 |
|---|---|
| Main.cs | 入口：挂 `BootTrace`、读 `NSProcessInfo` 参数、按开关选壳委托类型。未选中的壳委托类型不会被加载，故原生路径不创建 Avalonia 对象。 |
| Native/iOSShellSwitch.cs | 壳选择：`--legacy-ui` / `--native` > NSUserDefaults `PVLegacyUI` > 编译期默认（Debug 原生 / Release 旧壳）。 |
| Native/iOSNativeAppDelegate.cs | 原生 `UIApplicationDelegate`：接线门面与共享 VM（窗口交给 scene）；`openURL` 两个重载直接投递原生外部打开；`GetConfiguration` 声明原生 scene 委托。同文件的 `iOSNativeSceneDelegate` 处理 scene 连接 / URLContexts。 |
| Native/iOSNativeShell.cs | 原生启动顺序（对齐 `MacNativeApplication`）：`UiDispatcher`（主队列 SynchronizationContext）→ ReactiveUI builder（无 Avalonia）→ 门面（FilePicker / AppResources / Heif / Raw / `BitmapLoader.InitializePresentationDecoder(ApplePresentationDecoder, iOSPresentationEncodingPolicy)` / PerformanceBudget / MemoryGovernor / StorageAccess / Xmp / NativeSettingsPresenter / Settings 存储 / ONNX 静态库 DllImport resolver / CoreML EP）→ `new MainViewModel(nativePresentation: true)` → `PhotoLoadSession` → 根控制器；并接线外部打开处理器、内存告警直报（`MemoryGovernor.ReportOsEvent` + 缓存规模记录 + `MemoryWarningEvent` 广播）、退后台/退出前设置落盘、压测（信号源 = 原生 `PhotoLoadSession`）与验收专用 `--pv-open <路径>` / `--pv-layout-mode row|col` / `--pv-native-photo-check <目录>` / `--pv-native-layout-check <目录>` / `--pv-dino-check <文件>`。带任一 `--pv-*` 参数即视为验收会话，不读写用户窗口偏好。 |
| Native/iOSNativeRootViewController.cs | I3 原生主屏：按共享几何与真实安全区拼合文件栏/主图/控制栏，两向与自动布局、栏位显隐、旋转与任意窗口尺寸都重算；**构造期**订阅共享 VM 与展示会话（启动参数可能在根控制器上屏前就选中首图）、把会话产物直接交 `iOSPhotoView` 上传、上报物理视口与细节需求；主图区保留空态/错误提示与临时设置齿轮。 |
| Native/iOSMainScreenLayout.cs | 主屏分区计算（纯函数）：在共享 `MainScreenLayout.Partition` 之上叠加安全区口径，产出文件栏背景/内容区、分隔线、主图、分析栏槽位、控制栏背景/内容区八个矩形；行布局把上下让位从主轴扣除并把栏位背景补到屏幕边缘，列布局把左右让位计入文件栏长度与背景延伸。 |
| Native/iOSPhotoView.cs | Metal 主图宿主 + `UIScrollView` 手势驱动（见"主图链路"）；暴露 `CaptureLinearFrame`、视口状态、`ZoomBy/ZoomToPhysical` 与对焦点 API 供共享命令与验收使用。 |
| Native/iOSFilmstripView.cs | 胶片栏：`UICollectionViewLayout` 单轴几何（条目数取业务筛选列表，见下方发现）、`CADisplayLink` 驱动的 350ms 三次缓出定位（可中断、用户拖动接管、减少动态效果直达终点）、可见范围上报共享缩略图调度器；`ReduceMotionProvider` 为验收注入减少动态效果的测试缝。 |
| Native/iOSThumbnailCard.cs | 卡片（[FILM-05]）：96×144 外框 / 90×138 占位区 / 80 方形照片区 / 文件名（尾部截断）/ 第二行拍摄时间 / 0–5 星级 / 抖动徽标 / 占位与加载态；`iOSSymbols.CreateThumbnail` 把共享缩略图管线的 BGRA8 产物编码成 PNG 交 UIImage。 |
| Native/iOSFilterBar.cs | 筛选条（[FILTER-01]）：排序 / 升降序 / 星级筛选三个 `UIButton` + 下拉 `UIMenu`、计数文本（自然宽度参与布局）、相似聚类 toggle（隐藏但接线到 `IsSimilarityPanelOpen`）。 |
| Native/iOSControlBar.cs | 控制栏（[TOOLBAR-01]）：EXIF 信息块 / 评分 / 命令三分组，几何与换行居中取共享 `ControlBarLayout`，评分方向随布局方向，I5 未落地的命令不生成按钮。 |
| Native/iOSSymbols.cs | SF Symbols → `UIImage`（系统图标对象只在本层创建）与缩略图 PNG 解码。 |
| Native/iOSNativePhotoCheck.cs | **验收专用（测试钩子）** `--pv-native-photo-check <目录>`：核对代理先行与 Full 热替换（编码、变换不变、中心像素无色偏）、八方向 GPU 角点、分析输入与 GPU 朝向一致、四张色卡线性值、编码策略与准入字节；报告写 `Documents/PhotoViewer/native-photo-checks.txt`，打印 `[NativeSmoke] PASS photo (n checks)` / FAIL。素材要求 `orientation-1..8.jpg`、`color-{srgb,p3,adobe,untagged-adobe}.jpg`、`color-p3-large.jpg`（长边 ≥ 4000 的 Display P3 色卡，用于制造真实代理档；缺一即 FAIL）；需与 `--pv-open <同一目录>` 同时使用才会载入素材。 |
| Native/iOSNativeLayoutCheck.cs | **验收专用（测试钩子）** `--pv-native-layout-check <目录>`：核对两向/自动分区与安全区、栏位显隐释放空间、筛选条与控制栏几何、卡片 96×144/80 方形几何，以及 FILM-01–06 交互（点选不居中、显式定位的中间帧与终点、端点不环绕、用户接管、减少动态效果直达、点星写回自有副本且不切主图、改星后筛选暂留与切走后移除）；报告写 `Documents/PhotoViewer/native-layout-checks.txt`，打印 `[NativeSmoke] PASS layout (n checks)` / FAIL。素材要求目录内 ≥12 张自有夹具，且目录必须位于本 App 沙盒内（不写用户照片 / TestCase）。 |
| Native/iOSDinoCheck.cs | **验收专用（测试钩子）** `--pv-dino-check <文件>`：解码一次、按 DINO 输入契约转 sRGB8、执行一次 `DinoFeatureExtractor.ExtractAsync`，打印 CLS 维度、实际生效的 EP（CoreML / CPU）与耗时；报告写 `Documents/PhotoViewer/dino-check.txt`，打印 `[DinoCheck] PASS …` / FAIL。用于核验 onnxruntime 静态链接与 CoreML EP 注册；产品路径不调用。 |
| Core/iOSPresentationEncodingPolicy.cs | iPad 编码协商：代理档 RGBA16F / Full 档 8 位 Display P3 / 压力 ≥ Warning 时代理也降 8 位 P3。 |
| Native/iOSUiServices.cs | `iOSUiDispatcher`（主线程调度 + `SynchronizationContext`）、`iOSFilePicker`（`UIDocumentPickerViewController`；文件夹选择并保留安全作用域，单文件按扩展名换算 UTType）、`iOSAppResources`（从 app bundle 读 DINO 模型）、`iOSWindowPresenter`（设置页走原生浮窗；工具页 I5 未实现，调用即抛 `NotSupportedException`）。 |
| Native/iOSSceneBridge.cs | 两个壳共用的 scene 接线：系统会按持久化 scene session 恢复委托类型，故无论哪个壳的委托收到连接，都按"当前活动壳"接窗口（原生根控制器或 Avalonia 单视图），并按活动壳选择 URLContexts 的投递口径。 |
| Core/iOSStorageRefs.cs | 原生 `IFileRef`/`IFolderRef`（对标 `MacStorageRefs`），按真实路径读写；安全作用域由 `iOSStorageAccessManager` 保留。 |
| Core/iOSViewControllerLocator.cs | 前台根控制器 / 展示链顶部定位，设置页与文档选择器共用。 |
| Core/iOSExternalOpenBridge.cs | 外部打开两条发布路径共用一份 URL→路径解析与作用域保留：旧壳 `PublishFromUrl`（Avalonia `IOSStorageItem` 反射，行为未变），原生壳 `PublishNativeFromUrl(s)`（不加载 Avalonia，直接发布原生存储引用）。 |
| Core/BootTrace.cs | 启动里程碑/异常取证（`Documents/PhotoViewer/ios-crash.log`）；`MarkAvaloniaAssemblies(阶段)` 记录该阶段已加载的 Avalonia 程序集。 |

**"无 Avalonia 对象"取证**：iOS 的 Xamarin 注册器在 `Main` 之前就已加载 Avalonia 程序集（`BootTrace.MarkAvaloniaAssemblies("Main enter(before UIApplication.Main)")` 即已列出），因此"程序集已加载"不等于"创建了 Avalonia 对象"。原生路径不引用、不初始化 Avalonia，也不创建 `Application`/窗口/位图适配器；判断依据是日志里的阶段对比与原生壳自身日志。

**DINO 模型与 ONNX Runtime 打包**：旧壳读 avares 资源（嵌在 `PhotoViewer.dll`），原生壳不加载 Avalonia，故 csproj 另有 `BundleResource` 把 `PhotoViewer/Assets/Models/*.onnx` 拷进 app bundle（与 Mac 同口径，模型约 86 MB，包内因此出现两份副本）。**ONNX 原生库（2026-10-17 已修）**：`Microsoft.ML.OnnxRuntime` 的 iOS 资产是 `runtimes/ios/native/onnxruntime.xcframework.zip`，包自带的 `buildTransitive/net9.0-ios18.0` 目标在 `net10.0-ios` 上**确实会生效**并加为 `NativeReference(Kind=Static)`，但 .NET 10 iOS SDK 的构建图没有把 `_ExpandNativeReferences` 排在 `_ComputeLinkNativeExecutableInputs` 之前——压缩包从未解析成设备/模拟器切片，主执行文件里没有 onnxruntime 符号。csproj 的 `_PvExpandOnnxRuntimeNativeReference` 显式提前展开，并把解析后的静态切片改回 `Kind=Static + ForceLoad`（`ResolveNativeReferences` 会把 framework 切片归类成 `Kind=Framework`，原生链接只加 `-framework`、不会 `-force_load`，托管侧没有静态引用，归档成员会被全部丢弃）。xcframework 内是 **ar 静态库**，所以 app bundle 里没有 `Frameworks/onnxruntime.framework`，符号静态链接进主执行文件；`_OrtGetApiBase` / `_OrtSessionOptionsAppendExecutionProvider_CoreML` 写入 `_ProcessedReferenceNativeSymbol`（Release 的 `_NativeStripFiles` 只保留 `mtouch-symbols.list` 里的符号），`iOSNativeShell` 再把托管侧 `DllImport("onnxruntime")` 经 `NativeLibrary.SetDllImportResolver` 映射到主程序。**CoreML EP**：1.24.4 与 1.25.1 的 iOS 原生库都返回 `CoreML EP is not supported on this platform.`，`ConfigureSession` 注册失败后按既有逻辑记录日志并回退 CPU（与 Mac 的 EP 失败回退同一路径），因此 iOS 推理可用但走 CPU。真机/模拟器核验入口：`--pv-dino-check <文件>`（该图解码→转 sRGB8→`DinoFeatureExtractor.ExtractAsync` 一次，打印 CLS 维度、EP 与耗时到 `Documents/PhotoViewer/dino-check.txt`；产品路径不调用）。

## 旧壳（迁移期保留，不做产品验收）

| 文件/目录 | 职责 |
|---|---|
| AppDelegate.cs | Avalonia 旧壳委托：`CustomizeAppBuilder` 注入平台能力、内存告警、`--pv-stress` 接线、`openURL` 保留作用域。行为未变。 |
| PhotoViewerSceneDelegate.cs | 旧壳 scene 委托，窗口与 URLContexts 均交给 `iOSSceneBridge`（与原生壳共用，避免切换壳后无窗口）。 |
| Core/iOSHeifDecoder.cs / iOSRawDecoder.cs | 旧壳与既有缩略图路径的 ImageIO 解码（8 位口径）。 |
| Core/iOSPerformanceBudget.cs / iOSMemoryBudgetProvider.cs | 机型表预算 / `os_proc_available_memory` 实测预算（P/Invoke 全部指针化）。 |
| Core/iOSNativeSettingsPresenter.cs | 原生设置半屏浮窗（Debug/Release 两壳共用）。 |
| Core/iOSSettingsStorage.cs / iOSXmpWriter.cs / iOSStorageAccessManager.cs | 设置存 NSUserDefaults、XMP 原位写入、安全作用域保留。 |

## 验收线索

- 原生：`xcrun simctl launch --console <udid> cc.xiaojh.PhotoViewer [--native] [--pv-open <路径>] [--pv-layout-mode row|col] [--pv-native-photo-check <目录>] [--pv-native-layout-check <目录>] [--pv-dino-check <文件>]`，截图 `xcrun simctl io <udid> screenshot`，日志 `Documents/PhotoViewer/ios-crash.log`（BootTrace marks）+ 控制台；验收报告 `Documents/PhotoViewer/native-photo-checks.txt`、`native-layout-checks.txt` 与 `dino-check.txt`。
- 布局验收素材：`release/native-p2-fixtures/*.jpg`（12 张）与 `release/native-p2-large-fixtures/color-p3-large.jpg` 拷进 app 容器 `Documents/layout-fixtures`（照片检查用 `photo-fixtures`，含大色卡）。夹具只放自有素材，评分写回只发生在容器副本内。**两向验收不依赖系统旋转**：iPadOS 拒绝 `requestGeometryUpdate` 改方向（见执行清单发现清单），故用显式 `--pv-layout-mode row|col` + 与真实尺寸/安全区一致的布局数学核对；真机旋转由作者手验。
- 真机（2026-10-17 实测可用）：`xcrun devicectl device install app --device <设备> <app>`；`xcrun devicectl device process launch --device <设备> --console --terminate-existing cc.xiaojh.PhotoViewer --native <参数…>`（`--console` 会等进程退出，脚本里用后台进程 + 轮询报告文件，读完后 kill 包装进程即可，杀它会顺带终止 app）；素材 `xcrun devicectl device copy to --device <设备> --domain-type appDataContainer --domain-identifier cc.xiaojh.PhotoViewer --source <本地> --destination Documents/<子目录>`（目录内容直接落到目标目录，5.8GB/500 文件约 15s），日志 `… copy from … --source Documents/PhotoViewer/<文件> --destination <本地>` 取回（`stress-flip.jsonl` / `memory-events.jsonl` / `ios-crash.log`）。设备需解锁才能挂载 DDI（锁屏报 `kAMDMobileImageMounterDeviceLocked`）；`devicectl device info lockState` 先确认，`systemCrashLogs` 域可查 .ips（本机挂载点写法见 handoff §3 行）。
- I2 真机基线（2026-10-17，iPad mini 7 / iPadOS 26 / Release ios-arm64 41abd7b）：`--pv-native-photo-check` **PASS 10 checks**；`--pv-stress=stress --pv-stress-count=500`（450 HEIC + 50 ARW）**500/500 零超时**、全程 Normal、p50 716.4ms / p95 775.3ms / max 798.5ms、gc 峰值 1620MB；`--pv-stress-warn=100` 注入后 seq101–102 记 `enc=Rgba8` + `Warning`，随后 Caution→Normal 且 `hold=true` 50 张（≈31s）；期间无新 .ips。
- ONNX / 相似度触发修复复测（2026-10-17，Release ios-arm64）：模拟器 `--pv-dino-check photo-check/color-srgb.jpg` → `PASS cls=384 ep=CPU ms=440.7`；真机同命令 → `PASS cls=384 ep=CPU ms=629.0`（CoreML EP 在这两版 iOS 原生库中均不受支持，按既有路径回退 CPU 并打印）；模拟器 `--pv-native-photo-check` **PASS 10 checks**、`--pv-native-layout-check` **PASS 24 checks**；真机 `--pv-stress=stress --pv-stress-count=500` **500/500 零超时、全程 Normal**，p50 **323.2ms** / p95 **418.5ms** / max 543.5ms（elapsed 157.5s），控制台 **0 条 `[DinoFeatureCache]`**（面板收起时切图不再触发推理）。
- 旧壳：同命令加 `--legacy-ui`；**切换壳不重装**时会出现上一次运行的 scene session 被恢复，两个壳的 scene 委托都能按活动壳接线（见 `iOSSceneBridge`，实测两向均正常）。
