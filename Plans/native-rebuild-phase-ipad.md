# iPad 原生主屏执行清单（UIKit + Metal）

> 2026-10-17 立项，隶属 [native-rebuild-master-plan-1.md](native-rebuild-master-plan-1.md)。前置：macOS 原生壳 P2/P3 已由作者验收（见 [handoff](native-rebuild-handoff.md)）。
> 主计划中 Phase 4/5 分别为 Windows/Android，本清单不占用其编号。
>
> **作者要求（2026-10-17）**：iPad 主屏与原 Avalonia 版本对标（布局结构、尺寸、边距、功能要点、选中/高亮语义）；设置已是原生，不在范围；需要针对触屏优化的地方可酌情使用原生控件。评估阶段的推荐项作者已全部采纳（见 §1）。
>
> **进度（2026-10-17）**：I1–I5 已完成并提交，I6 文档收尾完成；逐里程碑程序化证据见 §7，作者手验按 [iPad 验收清单](native-rebuild-phase-ipad-acceptance.md) 执行（尚未开始）。

## 0. 范围

| # | 内容 | 说明 |
|---|---|---|
| 1 | 原生启动与平台接线 | UIKit 生命周期、ReactiveUI 无 Avalonia 启动、原生存储引用、文档选择器、外部打开 |
| 2 | 主图 | Metal 照片层、编码协商（F16/8bit P3）、缩放/平移/双击、Full 热替换 |
| 3 | 主屏布局 | 文件栏（筛选条 + 胶片栏）、控制栏、两向/自动布局、安全区、旋转与多任务尺寸 |
| 4 | 输入 | 上下文菜单、硬件键盘、触控板、拖入、Fit 下滑动翻页 |
| 5 | P3 功能 | 分析栏、相似聚类、工具页（EXIF 详情 / DINO 诊断）、增强预览 |
| 6 | 验收与文档 | UIKit 程序化验收、iPad 真机门、ui-contract iOS 列、AGENTS/handoff 同步 |

不在范围：设置页（已原生，仅接线）；照片数据统计（Windows 专属）；性能门（大图 <100ms、切图卡顿，作者裁定"以后再做"）；iPhone 紧凑布局（只保证能启动、能用，不做专项适配）；Release 通道切原生（作者决定）。

## 1. 已定决策

1. **像素格式按预算协商**：`IPresentationImageDecoder` 增加编码协商，由 `MemoryGovernor`/预算决策。iPad 默认代理 RGBA16F 扩展线性 sRGB、Full 8bit Display P3；压力 ≥ Warning 时代理也降 8bit P3；Mac 保持全程 F16 不变。Metal 渲染器接受两种纹理格式，着色器与方向变换不变。准入估算与 GPU 记账按实际编码计算。
2. **新增触控交互**（iOS 平台差异，写入 ui-contract）：
   - Fit 状态下主图横向轻扫 = 上一张/下一张，走显式定位（FILM-01 同 Previous/Next）；放大态滑动仍为平移。
   - 胶片栏卡片上下文菜单增加"评分 0–5"（卡片星级几何不变，触控目标例外以菜单补足）。
   - 分析栏细节预览：手指点按定格主图绿框，再点或点空白解除；触控板悬停沿用 hover 语义。
3. **缩放手感用 `UIScrollView` 驱动**：只作手势与物理（回弹/惯性/双指缩放），读取 `zoomScale`/`contentOffset` 换算为 Metal 视口变换；100% 物理像素与 `NeedsFull` 判定沿用共享视口数学。
4. **最低系统版本提到 iOS 17**（I1 内核实 `CAMetalLayer` 色彩空间/EDR 相关 API 的最低版本，若需 18 再上调并记录）。
5. **iPhone**：本轮只做 iPad；iPhone 走同一原生壳但不做紧凑布局专项。
6. **入口切换**：Debug 默认原生，旧壳经启动参数/UserDefaults 开关回退；Release 保持旧壳。原生路径不创建任何 Avalonia 对象。
7. **Apple 共享源**：新建 `PhotoViewer.Apple/`（源文件目录，以 `Compile Include` 链接进 Mac 与 iOS head，不新增程序集），放 Mac/iOS 共用的解码器、Metal 渲染器、几何与动画数学、命令符号映射。Mac 改为引用共享源，以现有八套原生回归证明零行为变化。

## 2. 复用边界

| 层 | 复用方式 |
|---|---|
| 共享 VM / 会话 / presenter | `MainViewModel(nativePresentation: true)`、`PhotoLoadSession`、`ThumbnailListViewModel`、`ControlViewModel`、`SimilarityPanelViewModel`、`AnalysisPresenter`/`IAnalysisHost`、`DinoDiagnosticPresenter`、`ToolsViewModel` 直接复用，不复制编排 |
| Mac 纯逻辑 → `PhotoViewer.Apple/` | `MacFilmstripLayout` 几何、350ms 三次缓出、`MacControlBar` 测量/换行、分析栏瓦片边长、`MetalPhotoView` 视口数学（Fit/Zoom/Clamp/导航图/`NeedsFull`）、`MacInput.FromRaw` 映射、SF Symbols 命令名 |
| Metal | 着色器、RGBA16F/ExtendedLinearSrgb、mipmap、八方向变换、帧读回抽为共享渲染核心；宿主分别为 NSView / UIView（`CAMetalLayer` + `displayScale`） |
| 解码 | `MacPresentationDecoder` 移入共享源（仅 ImageIO/CoreGraphics），加编码协商；iOS 旧 `iOSHeifDecoder` 只服务旧壳与缩略图既有路径 |
| UIKit 重写 | 主屏 VC、胶片栏、卡片、筛选条、控制栏、分析栏瓦片、相似面板、工具页、存储引用、选择器、调度器、验收驱动器 |

## 3. 里程碑

### I1 基座
- 建 `PhotoViewer.Apple/`，迁移 §2 纯逻辑、Metal 核心与展示解码器；Mac 改引用，八套 `--pv-native-*` 回归全 PASS、Mac 构建零新增警告。
- `Main.cs` 按开关选择原生/旧壳 AppDelegate；原生启动顺序照 `MacNativeApplication`：`UiDispatcher`（主队列 SynchronizationContext）→ ReactiveUI builder → 门面注入（FilePicker / AppResources / Heif / Raw / PresentationDecoder / PerformanceBudget / MemoryGovernor / StorageAccess / Xmp / Settings / ONNX EP（CoreML 注册失败回退 CPU））→ `MainViewModel` → 根 VC。保留内存告警直报与 `BootTrace`、`--pv-stress` 接线。
- 原生 `IFileRef`/`IFolderRef`（对标 `MacStorageRefs`）+ 安全作用域书签；`UIDocumentPickerViewController` 选文件夹；外部打开（openURL / scene URLContexts）去掉 Avalonia 反射，直接走 `ExternalOpenService`。
- MinimumOSVersion 上调（§1-4），iOS 模拟器 + 真机构建零错误。
- **状态：已完成** —— `d22403e`（共享源抽取，Mac 八套回归 PASS）+ `0a4ad1a`（原生壳启动/存储/选择器/外部打开，最低 iOS 17）。

### I2 主图
- 编码协商落地（§1-1）：解码器、`BitmapLoader` 准入估算、`PhotoLoadSession`、GPU 记账、Governor 降级。
- iOS Metal 视图 + `UIScrollView` 手势驱动；双击 Fit ↔ 对焦点 100% 动画；导航图；跨屏/旋转刷新物理尺寸。
- **真机门（先于 I3）**：iPad mini 7 500 连翻（HEIC+ARW）无 jetsam、恒 Normal 或可恢复；告警注入降级生效；物理 100% Full 热替换无跳变；P3 色卡对拍（ColorTestTool / 原生色卡）。
- **状态：已完成** —— `41abd7b`（编码协商 + Metal 主图 + 手势）；真机门 `2245d23`：`--pv-native-photo-check` PASS 10、500 连翻零超时恒 Normal、告警降级生效。ONNX/SCOPE-01 修复为 `e1f7cef`。

### I3 主屏布局
- 根布局两向/自动（只看窗口方向），文件栏 116 内容宽、卡片 90×138+3 占位、80 方形、列表边距 10/10/5/10，与 Avalonia 对拍；读真实 `safeAreaInsets`，背景铺满边缘。
- 胶片栏 `UICollectionView` 按 FILM-01–06；减少动态效果读 `UIAccessibility.IsReduceMotionEnabled`。
- 筛选条 `UIButton`+`UIMenu` 下拉、计数、相似聚类 toggle；控制栏三分组换行/换列与居中，评分方向、显隐/排序、主次绑定提示。
- 旋转、Split View、Stage Manager 任意尺寸下几何正确。
- **状态：已完成** —— `6f36d8b`；两台模拟器 `layout` 24 + `photo` 10，Mac 回归 PASS。旋转/多任务任意尺寸待作者手验。

### I4 输入
- 主图 `UIContextMenuInteraction`（打开文件夹、栏位显隐、设置；长按/辅助点按触发）；分析栏/工具/增强预览属 I5，不生成死项。卡片上下文菜单含评分 0–5。
- 硬件键盘：`pressesBegan`/`UIKeyCommand` → `AppGesture`（HID usage 映射表，持久化字段不变）；输入控件获焦不误触（INPUT-01）。
- 触控板滚动/悬停、`UIDropInteraction` 从"文件"拖入、Fit 下横向轻扫翻页。
- 原生设置接线（`iOSNativeSettingsPresenter` 从根 VC 弹出），关闭回写后主屏即时刷新。
- **状态：已完成** —— `9f20582`；模拟器 + 真机 `input` 27（含设置关闭回写修复、卡片菜单评分、轻扫翻页）。

### I5 P3 功能
- 分析栏（几何同旧壳 `AnalysisView`，可见时主图升 Full）、细节预览点按定格；相似聚类面板（SIM-01）；增强预览（`PhotoLoadSession`）。
- 工具页：sheet / `UINavigationController` 承载 EXIF 详情与 DINO 诊断，瓦片与分析栏共用 UIKit 诊断瓦片。
- **状态：已完成** —— `e9ee179`；两台模拟器六套检查全 PASS，真机 `analysis` 15 / `similarity` 10 / `tools` 7。

### I6 验收与文档
- UIKit 验收驱动器 `--pv-native-*`（模拟器跑几何/布局/胶片栏/工具栏/分析/相似；devicectl 传参真机跑内存与 Full 热替换），只写自有测试副本与独立设置/数据库。
- 作者真机验收：色彩、画质、触摸手感（缩放/惯性/翻页/胶片栏）、键盘与触控板、拖入、旋转与多任务。
- 文档：ui-contract iOS 列与新增 iOS 差异条目；新建 `PhotoViewer.iOS/AGENTS.md`、`PhotoViewer.Apple/` 文件表；[Design/architecture.md](../Design/architecture.md) §4/§5（新增 iOS 原生链路小节）、DEV.md 任务、handoff 状态与验证矩阵。
- **状态：程序化部分完成（I2–I5 各阶段随里程碑落地）；作者手验待执行**，清单见 [iPad 验收清单](native-rebuild-phase-ipad-acceptance.md)，逐项状态见 §7。

## 4. 验收门

1. 四平台构建零错误、无新增警告；Mac 八套原生回归 PASS（共享源迁移零行为变化）。
2. iPad 真机 500 连翻无 jetsam，告警降级与 30s 恢复观察期日志可查。
3. 100% 放大永远 Full、热替换无跳变；色卡对拍通过。
4. 主屏几何两向与 Avalonia 对拍通过；FILM/FILTER/TOOLBAR/SIM/ANALYSIS 契约 iOS 列为"实机程序化通过"或"作者手验通过"。
5. 作者真机手感/色彩验收通过；未通过项不以模拟器或 Mac 结果代替。

## 5. 风险

| 风险 | 应对 |
|---|---|
| F16 代理 + 8bit Full 混合在热替换时色彩跳变 | 同一显示色彩空间输出，I2 真机对拍代理↔Full 切换帧 |
| `UIScrollView` 与 Metal 视口同步出现一帧延迟/抖动 | 在 `scrollViewDidZoom`/`DidScroll` 同步提交帧；不达标退回自写识别器 |
| mono 解释器 P/Invoke 封送问题（P1-M5 曾出现） | 新增原生调用沿用全指针化签名，`BootTrace` 常驻 |
| 共享源迁移扰动 Mac 已验收行为 | I1 先迁移再接 iOS，Mac 回归作为合并门 |
| 免费账号 Profile 7 天过期打断真机验证 | `Refresh iOS Profile` task |

## 6. 发现清单

| 日期 | 发现 | 处理 |
|---|---|---|
| 2026-10-17 | 旧壳 iOS 无真实安全区处理（固定 100px 出血 + 关闭自动内边距） | 原生壳读真实 insets，旧壳不修 |
| 2026-10-17 | 共享层无按平台/预算选择像素编码的机制，`MetalPhotoView` 写死 F16 | I2 编码协商 |
| 2026-10-17 | iOS 外部打开依赖反射 `Avalonia.iOS.Storage.IOSStorageItem` | I1 原生存储引用替换 |
| 2026-10-17 | iOS 26 SDK 下应用委托窗口路径已废弃：系统按 UIScene 连接 scene，且**持久化 scene session（含委托类型）**跨启动恢复 —— 换壳重启时上一次运行的 scene 委托会收到连接 | I1 已处理：两个壳的 scene 委托共用 `iOSSceneBridge`，按"当前活动壳"接窗口（原生根控制器 / Avalonia 单视图），两向切换实测正常 |
| 2026-10-17 | 托管 `Main(string[] args)` 在 iOS 上为空数组 | 启动参数一律读 `NSProcessInfo.ProcessInfo.Arguments`（`simctl launch` 会传入） |
| 2026-10-17 | ONNX Runtime 原生库在 iOS 模拟器上类型初始化失败（真机 P1-M5 正常） | 已由下方 2026-10-17 "ONNX 推理在 iOS 上模拟器与真机都失败" 行处理（静态链接 + `DllImportResolver`）；CoreML EP 仍不受支持，推理走 CPU |
| 2026-10-17 | 原生壳不加载 Avalonia，无法用 avares 资源读 DINO 模型 | 模型另作 BundleResource 拷入 app bundle（与 Mac 同口径；包内出现 avares + bundle 两份，约 86 MB） |
| 2026-10-17 | **I2 编码协商落地**：Core 新增 `PixelEncoding.Rgba8` + `IPresentationEncodingPolicy`（平台注入）+ `IDisplayLoadSignal`；`IPresentationImageDecoder` 改为按请求编码解码；编码**不进缓存键**（同档至多一条，长边足够且保真度不低于需求即复用，否则原地替换），准入与 GPU 记账按实际编码字节 | 见[架构文档](../Design/architecture.md) §5.7；Mac 九套 `--pv-native-*` 复跑全 PASS |
| 2026-10-17 | **8 位 P3 立方体的原色边界**：Adobe RGB 红原色在扩展线性 sRGB 下需 1.3985（F16 档实测 1.387），8 位 Display P3 装不下、只能钳到 P3 红原色（≈1.2145，由共享 P3→scRGB 矩阵与立方体上界解析给出）。sRGB/P3 原色不受影响 | 属 8 位 P3 编码的既有边界，不做隐藏处理；`AppleAcceptanceChecks.ColorExpectation` 按编码取不同下界并在报告里记录实测值 |
| 2026-10-17 | **视口未知时不得改写代理目标**：`PhotoLoadSession.UpdateViewport` 原会把 0 尺寸视口钳成 1 像素目标，解码出 1×1 的"合格代理"（`IsProxy` 判据是"小于源尺寸"，1×1 满足）且因 `IsProxy` 为假永不重载 → 主图恒为一片平均色 | 已修：`UpdateViewport` 忽略尺寸 < 1 的上报；iOS 根控制器改为"先上报视口再 `SetFile`"（`StartSession`） |
| 2026-10-17 | **UIScrollView 程序化缩放会被偏移动画取消**：双击放大时先 `SetZoomScale(animated:)` 再 `SetContentOffset(animated:)`，实测缩放停在原值只动偏移（偏移 setter 中断在途缩放动画） | 改为 `ZoomToRect`（缩放+偏移一次动画），缩回适应只需 `SetZoomScale(1, animated:)` |
| 2026-10-17 | **iOS 启动参数可能在根控制器上屏前就选中首图**（`--pv-open` 在壳初始化期投递），若在 `ViewWillAppear` 才订阅 `CurrentFile` 会漏掉该次变化 → 永不加载 | 根控制器改为**构造期**订阅（会话/VM/文件夹），布局完成后再启动会话 |
| 2026-10-17 | **`--pv-native-photo-check` 需要"比 2× 视口更大"的素材**才能出现真实代理档：1200×800 夹具在 iPad 上 2× 目标 ≈ 4532，解码器不放大 → 直接是满尺寸产物（`IsProxy` 为假），代理/F16 路径根本没被走到 | 夹具加 `color-p3-large.jpg`（6000×4000 Display P3 色卡，缺一即 FAIL），代理档与 Full 档在此素材上才成立。生成法：按 `Tools/generate-native-color-fixtures.py` 的色块布局放大 5 倍（PIL，`icc_profile=` 读 `/System/Library/ColorSync/Profiles/Display P3.icc`、quality=100/subsampling=0） |
| 2026-10-17 | **I3 共享几何抽取**：`MacMainWindow.Layout()` 的两向分区（文件栏/分隔线/主图/分析栏/控制栏，含"隐藏即让空间给主图"）与筛选条五个控件几何是纯尺寸数学，抽到共享 `MainScreenLayout`（并给 `FilmstripGeometry` 加 `CenterOffset`，Mac 定位改为共用） | Mac 复跑 `--pv-native-smoke/layout/filmstrip/toolbar` 全 PASS，零行为变化；见[架构文档](../Design/architecture.md) §5.7 与 [PhotoViewer.Apple/AGENTS.md](../PhotoViewer.Apple/AGENTS.md) |
| 2026-10-17 | **iPadOS 26 拒绝程序化改方向**：`RequestGeometryUpdate(UIWindowSceneGeometryPreferencesIOS)` 返回 `The current windowing mode does not allow for programmatic changes to interface orientation.` | 两向布局改用显式 `--pv-layout-mode row\|col` + 与真实尺寸/安全区一致的布局数学核对（`--pv-native-layout-check`），并新增 `--pv-layout-mode` 供两向截图；真机旋转与手感留给作者手验 |
| 2026-10-17 | **`CADisplayLink` 不 retain 自己的 target**：`CADisplayLink.Create(Action)` 的回调可能在动画途中被回收，350ms 定位动画静默停住（现象：同一流程偶发"一次动一次不动"） | 胶片栏自持 `DisplayLinkTarget`（`[Export("handleDisplayLink:")]`）改用选择器入口；验收断言中间帧与终点，动画停住即 FAIL |
| 2026-10-17 | **`UICollectionView.NumberOfItemsInSection` 在 `reloadData` 之后、下一次布局之前返回过期条目数（实测 0）**：自定义布局据此算文档长度会把文档压成视口大小，紧接着的偏移约束（列表变短只做边界限制）会把刚发起的显式定位清零，上一张/下一张看起来没反应 | 布局条目数改由业务筛选列表提供（`iOSFilmstripLayout.CountProvider`），数据源与布局同源 |
| 2026-10-17 | **取消定位的版本号必须在切图的同步通知里捕获**：`UiDispatcher.Post` 是异步投递，若在投递块内部才捕获版本号，切图后立刻发起的显式定位会被同一轮"取消此刻之前的定位"作废（Mac 同步执行，故没有这个问题） | `iOSFilmstripView.CancelScrollUpToNow` 由 `CurrentFile` 的同步通知调用：版本捕获与投递分离，此后新登记的请求胜出 |
| 2026-10-17 | **`color-p3-large.jpg` 不能放进 `release/native-p2-fixtures/`**：Mac 回归枚举该目录并对每个 `color-*` 调 `AppleAcceptanceChecks.ColorExpectation`，未登记名称直接抛 `ArgumentOutOfRangeException`（I2 的大色卡因此不在该目录） | 大色卡独立目录 `release/native-p2-large-fixtures/`；生成器改为 `--large-output <目录>` 显式指定 |
| 2026-10-17 | `Info.plist` 沿用隐藏状态栏 → 实测 `SafeAreaInsets.Top = 0`，栏位内容贴屏幕顶边 | 布局按真实 insets 计算，将来改为显示状态栏会自动让位；已登记为 iOS 平台差异（[ui-contract](../Design/ui-contract.md)）。**2026-10-17 补**：`UIViewControllerBasedStatusBarAppearance=true` 会忽略 `UIStatusBarHidden`，根控制器必须显式 `PrefersStatusBarHidden() => true`，否则状态栏漂在贴顶内容上（缺陷④） |
| 2026-10-17 | 卡片星级按钮 14×14（Mac 对拍几何）低于系统 44 点触控建议 | 几何保持不变，评分入口由 I4 的卡片上下文菜单补足（§1-2 已定决策）；I4 之前记为 iOS 触控目标缺口 |
| 2026-10-17 | **真机门（I2）已在解锁后跑通**：`--pv-native-photo-check` PASS 10 checks；500 连翻（450 HEIC + 50 ARW）500/500 零超时、零闪退、全程 Normal（p50 716.4ms / p95 775.3ms / max 798.5ms，gc 峰值 1620MB）；`--pv-stress-warn=100` 注入后 seq101–102 记 `enc=Rgba8` + level `Warning`，随后 Caution→Normal、`hold=true` 持续 50 张（≈31s）；`systemCrashLogs` 中无本次时段的 .ips（唯一 JetsamEvent 为 00:18，早于本轮） | 首次尝试因设备锁屏（`kAMDMobileImageMounterDeviceLocked`）阻塞，解锁后一次通过；命令与素材要求见 [PhotoViewer.iOS/AGENTS.md](../PhotoViewer.iOS/AGENTS.md) "验收线索"，本轮结果见 [handoff §3](native-rebuild-handoff.md) |
| 2026-10-17 | **ONNX 推理在 iOS 上模拟器与真机都失败**（推翻此前"真机 P1-M5 正常"的记录）：500 连翻产生 500 条 `[DinoFeatureCache] extract failed … TypeInitialization_Type, Microsoft.ML.OnnxRuntime.NativeMethods`。根因不是 NuGet 资产选择：包自带的 `buildTransitive/net9.0-ios18.0` 目标在 `net10.0-ios` 上**会**把 `onnxruntime.xcframework.zip` 加进 `NativeReference`，但 .NET 10 iOS SDK 的构建图没有把 `_ExpandNativeReferences` 排在 `_ComputeLinkNativeExecutableInputs` 之前，压缩包从未解析成切片；即便解析，`ResolveNativeReferences` 归类出的 `Kind=Framework` 也不会带 `-force_load`，而 managed 侧没有静态引用，归档成员会被丢弃 | **已修（2026-10-17）**：iOS head 的 `_PvExpandOnnxRuntimeNativeReference` 提前展开、把切片改回 `Kind=Static + ForceLoad`；`_OrtGetApiBase` / `_OrtSessionOptionsAppendExecutionProvider_CoreML` 写入 `_ProcessedReferenceNativeSymbol`（Release strip 只保留这份导出列表）；`iOSNativeShell` 注册 `DllImportResolver` 把 `onnxruntime` 映射到主程序。该 xcframework 内是 ar 静态库，因此产物**没有** `Frameworks/onnxruntime.framework`，符号静态链接进主执行文件（Debug 模拟器 37.0MB / Release 真机 24.1MB，`nm` 均为 `T _OrtGetApiBase`）。证据：sim `--pv-dino-check photo-check/color-srgb.jpg` → `PASS cls=384 ep=CPU ms=440.7`；真机同命令 → `PASS cls=384 ep=CPU ms=629.0`；`--pv-native-photo-check` 仍 PASS 10 checks、`--pv-native-layout-check` PASS 24 checks |
| 2026-10-17 | **CoreML EP 在 iOS 原生库中不可用**：`AppendExecutionProvider_CoreML()` 抛 `The CoreML Execution Provider is not supported in this build`；1.24.4 与 1.25.1 的 iOS 静态库都含 `CoreML EP is not supported on this platform.` 与 `_OrtSessionOptionsAppendExecutionProvider_CoreML` 符号，但实现被编译掉 | 不阻断推理：`DinoFeatureExtractor` 按既有路径记录日志并回退 CPU（与 Mac 的 EP 失败回退一致，不静默）；`--pv-dino-check` 明确打印 `ep=CPU`。iOS 上暂无可用的 GPU EP，需等上游包带 CoreML 构建或另立 GPU 方案 |
| 2026-10-17 | **iOS Release 的 `_NativeStripFiles` 会剥掉额外导出的符号**：先用 `-Wl,-exported_symbol` 链接时，中间可执行文件有 `_OrtGetApiBase`，但 `.app` 内的可执行文件在 strip 后只剩 `mtouch-symbols.list` 里的符号，`dlsym` 再次失败 | 改为把两个 ONNX 入口写入 `_ProcessedReferenceNativeSymbol`（即 `mtouch-symbols.list`），Release 产物 `dyld_info -exports` 可见 `_OrtGetApiBase` / `_OrtSessionOptionsAppendExecutionProvider_CoreML` |
| 2026-10-17 | **iOS 的 ImageIO 读 Sony ARW 返回内嵌预览而非传感器分辨率**：真机 `--pv-open stress-0451.arw` 实测 `req=4532 → out=1616x1080 props=1616x1080`（Mac 同一文件为 `7008x4672`，代理 3952x2635）。产物 `Width == SourceWidth` ⇒ `IsProxy=false`，于是共享的 `NeedsFull` 判据（要求 `IsProxy`）对 RAW 永不触发 Full 升级，主图恒为 1.7MP 预览 | I2 不修，报作者裁定（属 RAW 支持口径，非 I2 引入；旧壳 iOS `iOSRawDecoder` 也走同一 ImageIO 机制）。影响：iPad 原生壳看 ARW 只有嵌入式预览、且共享视口的"100% 物理"几何以预览尺寸为基准 |
| 2026-10-17 | **共享 VM 在每次切图都尝试 DINO 提取**：`SimilarityPanelViewModel.RecomputeAsync` 由 `CurrentFile` 驱动，与面板是否展开无关，故原生 iOS 壳每次切图都会解码缩略图 + 算指纹 + 走一次（失败的）推理；500 连翻即 500 条 extract failed 日志。真机 p50 716–738ms 高于 P1-M5 旧壳同素材集的 356.7ms（另一因素是 I2 代理档为 F16，每张解码字节是旧壳 8 位路径的两倍） | **已修（2026-10-17，SCOPE-01）**：`SimilarityPanelViewModel` 只在 `IsSimilarityPanelOpen && IsThumbnailViewVisible` 时随 `CurrentFile` / 筛选池 / 阈值重算；收起即取消在途 CTS，重新展开时 `OnPanelOpened` 按当前图重算。`ShakeFlagService` 仍只读库、`AnalysisPresenter` 仍只在分析栏可见时工作。真机 500 连翻复测的 p50/p95 与"零 DinoFeatureCache extract"证据见下方 2026-10-17 复测行 |
| 2026-10-17 | `PhotoLoadSession.LoadAsync` 里 `if (_needsFull && IsProxy) UpdateViewport(_proxyLongEdge / 2.0, 0, true)` 在零尺寸守卫落地后已成空操作（`heightPixels = 0` 被忽略） | **已删（2026-10-17）**：确认该调用唯一的第二实参为 0、必被 `UpdateViewport` 的 `< 1` 守卫丢弃；宿主在 `Replace` 之后立即用真实尺寸再上报，Full 升级仍走主路径 |
| 2026-10-17 | **ONNX + 相似度触发修复后真机复测**：Release ios-arm64 安装到 iPad mini 7，`--pv-dino-check photo-check/color-srgb.jpg` → `PASS cls=384 ep=CPU ms=629.0`；`--pv-stress=stress --pv-stress-count=500`（450 HEIC + 50 ARW）**500/500 零超时、500 条全 Normal、0 条 DinoFeatureCache 日志**，p50 **323.2ms** / p95 **418.5ms** / max 543.5ms（elapsed 157.5s） | 与 I2 基线 716.4/775.3ms 相比大幅下降（除隐式推理外，Release 编码路径未变；代理仍 F16，450/500 条 `proxy=true`）。模拟器侧 photo/layout/dino 三套 PASS；Mac 四套回归 PASS，`git status release/` 干净（评级已还原） |
| 2026-10-17 | **设置页关闭回写代理在 `ViewDidLoad` 拿不到 presentation controller**：`iOSNativeSettingsNavigationController` 原在 `ViewDidLoad` 里设 `PresentationController.Delegate = _dismissDelegate`；实测此时 `PresentationController` 仍为 null（sheet 在展示开始时才装配），系统下拉关闭不会回写设置，只有关闭按钮回写 | **已修（I4）**：改在 `ViewWillAppear` 挂接；`--pv-native-input-check` 断言真实弹出的设置页 `PresentationController.Delegate` 为关闭回写代理，并经该代理应用工作副本后核对主屏传播 |
| 2026-10-17 | **devicectl 拷入的夹具目录属 root（0755），App 无目录写权限**：评分写回的安全写路径会在同目录新建 `<file>.xmp_backup`，真机 `--pv-native-input-check layout-fixtures` 因此 `UnauthorizedAccess_IODenied_Path` 失败（模拟器容器目录属 App，现象不出现） | 输入验收与 Mac 各套件同口径：先把 `*.jpg` 复制到 App 自建 `Documents/input-fixtures` 工作副本再运行（目录属 App UID），评级仍只写自有副本。真机复跑 PASS 27 checks |
| 2026-10-17 | **iOS 没有读取系统键盘重复速率的公开接口**：`UIPress` 不提供 repeat 标志，`UIKey` 也不暴露系统键盘偏好 | 按住键的重复由本壳 `NSTimer` 以固定节奏（首次 350ms、随后 60ms）驱动；系统若重复派发同一按键被去重。速率与 Mac（跟随系统偏好）不同，记为 iOS 平台差异；`UIPress` 无法合成，重复节奏未做程序化断言 |
| 2026-10-17 | **真实鼠标滚轮与触控板滚动无法在模拟器/检查里合成**：UIPan 的 `AllowedScrollTypesMask` 只影响原生事件源 | 滚轮识别器限定离散滚动（`Minimum/MaximumNumberOfTouches = 0`）并按用户鼠标绑定查命令表；程序化覆盖映射与识别器配置，滚轮方向与"绑定存在时不同时平移"（`RequireGestureRecognizerToFail`）留作者真机手验 |
| 2026-10-17 | **I5 P3 功能落地与验收**：分析栏（`iOSAnalysisHost` + 共享 `AnalysisPresenter`，UIKit 瓦片/细节预览/矢量场）、相似聚类（复用胶片栏卡片与布局，行让位/列扩宽）、工具页（PageSheet + `UINavigationController`：EXIF 7 列 + DINO 5 瓦片，共用共享 presenter）、增强预览（`PhotoLoadSession`）；入口为主图菜单、控制栏命令、EXIF 信息块与筛选条 toggle。共享层新增 `SimilarityPanelGeometry`/`ExifTableGeometry`（Mac 同步改用，回归全 PASS） | 模拟器 iPad mini/iPad Pro 两机型六套检查全 PASS（photo 10 / layout 24 / input 27 / analysis 15 / similarity 10 / tools 7）；真机 iPad mini 7（Release ios-arm64）analysis 15 / similarity 10 / tools 7，索引 1 张 1.2–1.7s（CPU，进程冷启动首轮 24.8s）；真机 200 连翻（分析栏关）**200/200 零超时**、p50 311.1ms / p95 385.3ms、gc 峰值 996MB；`--pv-analysis-open` + 50 连翻（每张 Full）p50 447ms/p95 2660ms、gc 峰值 1593MB，全程 Normal |
| 2026-10-17 | **UIKit 自绘瓦片首次以 1×1 内容被拉伸，装饰层以不透明黑盖住图像**：`UIView` 默认 `ContentMode=ScaleToFill`，尺寸变化只缩放首次绘制的内容；`ChromeView` 未设透明，按不透明层合成成黑块（现象：分析栏/DINO 瓦片全黑，仅药丸文字可见） | 瓦片与矢量场/装饰层设 `ContentMode=Redraw`，装饰层 `Opaque=false` + 透明背景；细节裁剪与热力图随即正常显示，`--pv-native-analysis-check` 增加最近邻与背景 alpha 0.30 断言 |
| 2026-10-17 | **相似聚类行布局在相似项从无到有后仍停在 106 点宽**：面板宽度由主屏布局按卡片数切出，但 `SimilarItems` 变化不触发布局，右侧停靠动作区把列表挤成零宽（真机/模拟器真实交互可见“有列表但空白”） | 根控制器订阅 `SimilarityPanelViewModel.PropertyChanged`（`SimilarItems`/`HasItems`）后重排；相似检查新增“项目出现后面板宽度按卡片数重算”断言 |
| 2026-10-17 | **共享 EXIF 详情 VM 可能先于同名合并创建**：打开文件夹时当前项可能落在同名组的非代表项（隐藏伴侣）上，`Tools.ExifDetail` 因此在伴侣关系建立前构建，工具页只显示单一格式 | 属共享层时序（Mac 同源）；iOS 工具验收在筛选完成后切到带伴侣的代表项（真实用户点选列表卡片即发生）复核多格式切换，产品修复留待共享层专项 |
| 2026-10-17 | **I4 输入落地与验收**：主图/卡片上下文菜单（SF Symbols + 原生勾选态，不含 I5 死项）、适应态轻扫翻页（单指、24 点阈值、走共享 Previous/Next）、HID usage 映射表（47 键）与合成派发、INPUT-01 守卫、拖入走 `ExternalOpenService`、设置关闭回写后六类设置传播 | 模拟器 `--pv-native-input-check layout-fixtures` **PASS 27 checks**；真机（iPad mini 7 / iPadOS 26，Release ios-arm64）`--pv-native-input-check photo-check` **PASS 27 checks**；同轮模拟器 photo **10** / layout **24** checks PASS，Mac `--pv-native-input`/`--pv-native-toolbar`/`--pv-native-settings` 与 base smoke PASS。模拟器上另以真实 UI 事件目视：主图长按、卡片长按、适应态左滑、放大态左滑不翻页 |
| 2026-10-17 | **作者真机缺陷①：主图只有双击有效，捏合与拖动无反应**：透明 `UIScrollView` 没有缩放内容视图，delegate 未实现 `ViewForZoomingInScrollView`；UIKit 因此不响应捏合（`zoomScale` 恒 1），`contentSize` 固定为适应尺寸而无平移。双击有效只是因为它在直接改视口 | **已修**：`iOSPhotoView` 增加透明缩放内容视图并作为 `ViewForZoomingInScrollView` 返回；`SyncScrollFromViewport` 设内容 frame/zoom/contentOffset，`DidZoom` 重算居中内边距；双击 `ZoomToRect` 与 Full 热替换映射不变。证据：`--pv-native-input-check` **PASS 28**（新增"捏合/平移驱动"断言：缩放内容视图尺寸、zoomScale 2 → 视口跟随、contentOffset → 平移跟随、回适应归 1）；模拟器真实 Option+拖拽捏合放大、放大后单指拖动平移且不翻页、适应态横滑翻页、双击仍切换 |
| 2026-10-17 | **作者真机缺陷②：长按菜单把照片抬升/缩小到一边**：系统默认用交互视图（整个主图分区）作预览 | **已修**：主图 `UIContextMenuInteractionDelegate` 返回触点处 1 点透明 `UITargetedPreview`（高亮/消失同源），菜单锚在手指、照片不动；卡片菜单保留系统抬升（不移动布局）。证据：模拟器真实长按截图，照片区与长按前逐像素一致，菜单出现在手指处 |
| 2026-10-17 | **作者真机缺陷③：设置改 EXIF/命令显隐后控制栏多组重叠**：`iOSControlBar.ClearGroups` 只 `Dispose` 分组而未 `RemoveFromSuperview`；`UIView.Dispose` 不把原生视图移出父视图，旧分组仍留在视图树上叠在新分组下 | **已修**：先 `RemoveFromSuperview` 再释放。证据：模拟器真实设置页连续改命令显隐与 EXIF 项（4→6）后控制栏重建为期望数量、无重叠；`--pv-native-input-check` **PASS 28** 新增三轮"子视图数 == 分组数、分组/组内控件零重叠"断言，layout check 的分组/子项数量改为按共享设置推导（不再硬编码 4/6/7） |
| 2026-10-17 | **作者真机缺陷④：文件栏左上留 macOS 交通灯空带 + 状态栏与贴顶内容重叠**：共享 `FilterBarFrames` 行布局横向居中是给 Mac 交通灯让位；iPad 没有该装饰。另外 `UIViewControllerBasedStatusBarAppearance=true` 时 `Info.plist` 的 `UIStatusBarHidden` 被忽略，根控制器未声明隐藏 → 状态栏漂在文件栏上 | **已修**：`FilterBarFrames` 新增 `alignLeading`（默认 false，Mac 不变），iPad 行布局从安全区前导边 + 10 起排；根控制器显式 `PrefersStatusBarHidden() => true`。证据：layout **PASS 24**（行布局断言首控件 X = 文件栏内容前导 + 10、筛选条贴安全区前导边；列布局保持 116 栏内居中），模拟器截图无左上空白与状态栏遮挡；Mac base/layout/similarity/analysis/toolbar 全 PASS |
| 2026-10-17 | **作者真机缺陷⑤（复核结论）：增强预览"没有效果"**：I5 验收只断言 `EnhancedDisplay`，没有比较上屏像素，无法区分"增强未生效"与"高对比素材差异小"；同时控制栏重叠缺陷会让点击落在旧子视图上 | **处理**：新增共享 `EnhancementAcceptance`（确定性低对比度 JPEG + GPU 帧平均/最大绝对差），iOS `--pv-native-analysis-check` 与 Mac `--pv-native-analysis` 都要求平均差 ≥6、最大差 ≥30 且关闭后完全还原；iOS 真实菜单点击实测照片区平均差 **17.7**（增强前后截图）。主图/控制栏链路本身无产品缺陷，控制栏重叠已由缺陷③修复 |

| 2026-10-17 | **作者真机缺陷⑥：放大后照片被拽到左上角**。根因是 `iOSPhotoView.SyncScrollFromViewport` 每次 `SetImage` 都对**带缩放变换的缩放内容视图写 `Frame`**（UIKit 对此语义未定义），`contentSize` 崩掉后偏移被钳向 (0,0)；增强开关、升 Full、布局回调都走这条路径。另一处是"先 `SetZoomScale` 再 `SetContentOffset`"互相取消 | **已重做为 UIKit 标准照片缩放模式**：滚动视图是唯一交互真源，缩放内容视图只在 `zoomScale=1` 时写基准尺寸，视口由 `DidZoom`/`Scrolled` 反算；程序化缩放一律 `ZoomToRect`/`SetZoomScale` 并带锚点修正；**代理↔Full 热替换只换纹理**（`PhotoViewport.ApplyTexture`），切图才回适应态；布局/旋转保持中心图像点与相对缩放。证据：`--pv-native-gesture-check`（模拟器 15 / 真机 15）按 UIKit 捏合序列驱动并断言手指下图像点保持（±1 点）、`contentSize`/偏移合规、Full 热替换/布局/增强开关不动视图；模拟器真实 Option+拖拽捏合（33.44% → 50.88% → 79.7% → 121.25%）与单指拖动截图复核，视图始终停在捏合点附近（对照修复前同操作跳到左上角） |
| 2026-10-17 | **作者真机缺陷⑦：iPad 缺右下角缩放导航指示器**（Mac 有：minimap + 白框 + 百分比） | **已补齐**：`iOSNativeRootViewController` 用 GPU 小目标渲染出缩略图（长边 150、按显示朝向比例），叠白色 1 点可见框（共享 `NavigationViewport`）与物理百分比；非适应态且设置开启时显示在主图区右下角（安全区内、避让控制栏/分析栏），小图只在展示图引用变化时重出。**触屏增强**：点按/拖动导航图把该位置移到视口中心（Mac 只展示）。证据：`--pv-native-gesture-check` 断言适应态隐藏、放大态尺寸/可见框/百分比、平移后可见框随动、拖动定位到 (.6,.55)；模拟器真实拖动导航图把视图移到左下区域（121.25% 不变） |
| 2026-10-17 | **作者真机缺陷⑧：增强预览切换很慢（以为没生效）+ 竖拍照片开启后变横向**。方向问题的**共享链路复核未见缺陷**：`EnhanceForDisplay` 的 sRGB8→CLHE→F16 全程保留 `SourceWidth/SourceHeight/Orientation`（离线链路验证与新增运行时断言均一致），EXIF 6/8 夹具与 8 位 P3 Full 档增强前后元数据、导航帧四角排列不变；真机上"变横向"可由缺陷⑥的滚动状态崩塌（照片被拽走/裁成横向视口）解释 | **方向**：`--pv-native-analysis-check` 增加 EXIF 6/8 与 4000×3000 EXIF 6（8 位 P3 Full）增强方向断言，Mac `--pv-native-analysis` 同口径；`--pv-native-gesture-check` 断言增强开关不改滚动状态。**速度**：拆分为"转 sRGB8 副本 → **就地**并行 CLHE（`ImageEnhancer.EnhanceInPlace`，直方图分区统计 + 逐行并行，输出逐字节等同旧串行实现）→ 回源展示编码（8 位 Full 回 8 位 Display P3，不再扩成 F16）"，省一次整图拷贝与 24MP 的 96MB F16 缓冲。真机 Release 实测 24MP Full **1.82 s**（转 238 / 均衡 1378 / 回 P3 201 ms）、13.7MP F16 代理 **0.92 s**（96 / 764 / 55 ms）；同一算法在 Mac（JIT）为 260 / 85 ms。**剩余瓶颈是 csproj 的 `UseInterpreter=true`（混合 AOT+解释器，Release 也开）**——托管热循环按解释执行，比 JIT 慢约一个数量级，非算法问题；是否为本机开启纯 AOT 需作者决定（计划 §1-6 的通道决策同源）。计算期间主图显示 `计算中…` 反馈（`PhotoLoadSession.IsEnhancing`），`--pv-native-gesture-check` 断言提示出现与收起 |
| 2026-10-17 | **验收方式与触摸实际用法脱节**：旧 `--pv-native-input-check` 的"捏合"只写 `SetZoomScale`，绕过了 UIKit 捏合时同时调整 `contentOffset` 的真实序列，因此真实捏合跳左上角而检查仍 PASS | 新增 `--pv-native-gesture-check`（见上），并把 input 检查的"放大态"从"物理 100%"改为相对放大（小夹具的物理 100% 在适应态之下，旧断言已不成立）。**结论：触摸类行为一律以 UIScrollView 真实回调序列 + 屏幕锚点断言为准，不再只断言视口 API** |
| 2026-10-17 | **作者手验①：首图双击"100%"只放大一点点**。复现证据：真机以文件夹启动时首图是 Sony ARW，iOS ImageIO 只给 **1616×1080** 内嵌预览，适应态已 **117%** 物理（竖屏模拟器 92%）⇒ 物理 100% 只剩 1.09–1.2 倍可放；同分辨率 JPG（24MP）冷启动双击是 **3.17–4.03 倍**（模拟器 3160×2107 适应 24.8% → 100%；真机 3780×2520 适应 31.5% → 100%）。属已登记的 RAW 预览限制（§7 未关闭项 2），不是缩放状态缺陷；另补两处护栏：首图上屏早于布局时滚动基准标脏并在下一次 `LayoutSubviews` 补建（旧实现直接返回，`MaximumZoomScale` 停在 1 ⇒ 双击/捏合几乎不动），物理 100% 不高于适应倍率时 iOS 夹回适应态（不把照片缩到比适应更小，避免视口与滚动状态分叉） | `--pv-native-gesture-check` 新增冷启动首图断言：大色卡启动要求 `zoomScale == 1/(BackingScale×FitScale)`；预览尺寸首图（适应 ≥ 100%）要求双击保持适应不跳动。模拟器 + 真机各 **PASS 24**；真机两种启动方式（单图 `--pv-open …jpg` / 文件夹，首图 ARW）都跑通，后者报告行记录 "1616×1080 适应 117%…无放大余量" |
| 2026-10-17 | **作者手验②：控制栏 Fit 命令/热键"没反应"**。旧 iOS `case "Fit"` 映射成无条件 `_photo.FitToView()`，所以在适应态按下什么都不发生；原 Avalonia 语义是 `ImageViewModel.ToggleFit()`：适应态 → 物理 100%（**优先 Sony 对焦点**，无对焦点取视口中心），已放大 → 回适应（双击则是 `ToggleFit(tapPoint)`）。另发现程序化改视口后不触发 `ViewportChanged`：滚动视图随后派生出的状态可能"看起来没变"（Pull 判定无变化），导航指示器百分比/可见性停在旧值 | 改为 `_photo.ToggleFit()` → 共享 `PhotoViewport.ToggleFit(null)`，再由 `SyncScrollFromViewport(animated: true)` 表达成滚动视图动画；`SyncScrollFromViewport` 末尾显式 `ViewportChanged`（覆盖 Fit/预设/双击/细节定位）。证据：gesture 检查"Fit 命令：适应 ⇄ 物理 100% 双向切换"；模拟器真实点击 Fit 按钮两次（100% → 适应 → 100%，截图 `01-cold-start/02-double-tap-100/03-fit-button/04-fit-button-again-100`） |
| 2026-10-17 | **作者手验③：放大态切图被重置回适应态**。`iOSPhotoView.SetImage` 在身份变化时强制 `FitToView` + `ResetScrollForImage`，丢掉倍率与位置；原 Avalonia `OnBitmapChanged` 是"适应态重新适应 / 对焦点模式居中到新照片对焦点 / 否则保持同一归一化中心"，Mac 共享 `ApplyImage` 早已同口径 | iOS 改为 `BeginImageChange` + `ApplyImage` + 按视口状态重建滚动基准（几何基准恒为全分辨率 `SourceWidth/Height`）；`PhotoViewport` 增加 `CenterUV` / `IsViewCenteredOnFocusPoint`（5% UV）并在每次缩放/平移后重估 `FollowsFocus`（与原 Avalonia 每次 Move/Zoom 重估一致，Mac 同步受益）。证据：gesture 检查"放大态切图 100% 中心保持""半倍率切图代理落地+升 Full 不移动视口""对焦跟随切图（真实 EXIF 0.214/0.728 → 0.742/0.257）"；模拟器真实交互：缩放到左上角 100% 后按 Next，下一张保持 100% 与同一角（导航图白框位置逐像素一致，`05-corner-100` / `06-next-keeps-corner`） |
| 2026-10-17 | **手势检查自身的两个口径问题**：(a) 冷启动首图在布局未定型时量到的是过渡几何（控制栏要等 EXIF 组就绪才定型，主图分区先高后矮），与用户看到的画面不符；(b) `SelectFileAsync` 用"产物引用变化"等待切图，命中 LRU 时同一 `DecodedImage` 会被复用 ⇒ 永远等不到 | (a) 冷启动断言先等当前照片 EXIF 就绪 + 主图分区尺寸连续 500ms 不变；(b) 改用 `PhotoLoadSession.Subscribe` 按文件路径确认上屏，再留一轮 UI 线程排空后确认产物仍在。夹具同时在 App 自建 `Documents/gesture-fixtures-work` 内生成 3 张同分辨率色卡副本与 2 张改写 MakerNote 0x2027 的 ARW 副本（输入目录 / TestCase / 用户照片只读） |


## 7. 验收状态（I6 收尾，2026-10-17）

> 本节只记录**程序化证据**；作者手验项一律未关闭，步骤见 [iPad 验收清单](native-rebuild-phase-ipad-acceptance.md)。"模拟器"= iPad mini（A17 Pro）与 iPad Pro 13（M5）两机型；"真机"= iPad mini 7 / iPadOS 26 / Release ios-arm64。

| 里程碑 | 提交 | 已验证（程序化） | 未关闭 |
|---|---|---|---|
| I1 基座 | `d22403e` + `0a4ad1a` | Mac 八套 `--pv-native-*` 回归 PASS、零新增警告；iOS 模拟器/真机构建零错误；原生壳启动不创建 Avalonia 对象；原生存储引用 / 文档选择器 / 外部打开接线 | 无 UI 手验项 |
| I2 主图 | `41abd7b` + `2245d23` | 模拟器 `photo` 10；真机 `photo` 10、500 连翻 **500/500 零超时恒 Normal**、告警注入降级 + 30s 恢复观察期；F16 代理 ↔ 8 位 P3 Full 热替换变换保持、中心线性色差 ≈0.0001 | 色彩/P3 目视与跨屏；ARW 内嵌预览（待作者裁定） |
| ONNX / SCOPE-01 | `e1f7cef` | 模拟器与真机 `--pv-dino-check` **PASS cls=384 ep=CPU**；真机 500 连翻 0 条 `DinoFeatureCache` 日志，p50 323.2ms / p95 418.5ms | CoreML EP 在 iOS 原生库不可用（上游限制，CPU 推理） |
| I3 主屏布局 | `6f36d8b` | 两台模拟器 `layout` 24 + `photo` 10：两向/自动布局、真实安全区、栏位显隐释放空间、筛选条/控制栏/卡片几何、FILM-01–06 交互 | 旋转、Split View/Stage Manager、触摸手感 |
| I4 输入 | `9f20582` + 2026-10-17 缺陷修复 | 模拟器 `input` **28**、真机 `input` **28**（新增捏合/平移驱动、控制栏重建幂等）：两类上下文菜单、轻扫翻页、HID 映射 47 键、INPUT-01、滚轮识别范围、拖入、设置关闭回写传播 | 真实键盘/鼠标/触控板手感、滚轮方向 |
| I5 P3 功能 | `e9ee179` | 两台模拟器六套 **10 / 24 / 27 / 15 / 10 / 7** 全 PASS；真机 `analysis` 15、`similarity` 10、`tools` 7；真机 200 连翻 p50 311.1ms / p95 385.3ms、gc 峰值 996MB | 分析栏可见压测（p95 2.66s）；触摸手感（长按菜单、点按定格） |
| 作者四项缺陷修复（⑥–⑧） | 2026-10-17 工作区（未提交） | 模拟器：`photo` 10 / **`gesture` 15** / `layout` 21 + 3 NOTE / `input` 28 / `analysis` 18 / `similarity` 9 + 1 NOTE / `tools` 5 + 2 NOTE 全 PASS；真机（iPad mini 7 / iPadOS 26 / Release ios-arm64）：`photo` 10、**`gesture` 15**（指下图像点 ±1 点、Full 热替换/增强不改滚动状态、导航图几何、增强 Full 1.82 s / 代理 0.92 s）、`analysis` 18（含增强方向）；Mac：base 39 + layout/analysis/input 全 PASS（analysis 新增增强方向断言）。模拟器真实交互：Option+拖拽捏合（33%→121%）不再跳左上角、单指拖动、双击锚点、导航图出现/拖动定位、增强开关画面不动；详见 §6 缺陷⑥–⑧与"验收方式"行 | 触摸手感、真机目视、`UseInterpreter` 是否调整 |
| 作者四项缺陷修复 | 2026-10-17 工作区（未提交） | 模拟器 iPad mini：`photo` 10 / `layout` 24 / `input` 28 / `analysis` 15 / `similarity` 10 / `tools` PASS；Mac base 39 + layout/similarity/analysis/toolbar 全 PASS；模拟器真实交互：Option+拖拽捏合放大、放大态拖动平移、适应态横滑翻页、长按照片区不动、设置页真实改命令/EXIF 后控制栏重建、菜单真实点增强后照片区平均差 17.7。详见 §6 缺陷①–⑤ | 触摸手感、真机复测由作者执行 |
| 作者手验反馈·主图缩放手感（双击 / Fit / 切图） | 2026-10-17 工作区（未提交） | 模拟器 iPad mini（A17 Pro）：`gesture` **24**、`photo` 10、`layout` 24、`input` 29、`analysis` 18、`similarity` 10、`tools` 7 全 PASS；真机（iPad mini 7 / iPadOS 26 / Release ios-arm64）`--native --pv-open <大色卡> --pv-native-gesture-check` **PASS 24**，另一轮以文件夹启动（首图 ARW）同样 **PASS 24** 并记录"1616×1080 适应 117%，无放大余量"；Mac base 39 + layout/input/analysis 全 PASS。模拟器真实交互截图（`/tmp/pvshots3`）：冷启动适应 → 双击 100% → Fit 回适应 → Fit 再 100% → 缩放到左上角 100% → Next 后新照片保持同一 100% 与同一角（导航图白框位置不变） | 作者真机复测手感；RAW 全分辨率解码裁定（首图"100% 只放大一点点"的根因） |


**未关闭项（需作者决定或手验）**：

1. **作者手验**：触摸手感（双指捏合/惯性/轻扫翻页/胶片栏定位）、旋转与 Split View/Stage Manager、硬件键盘/鼠标/触控板、文件拖入与"用…打开"、色彩与 P3 目视。捏合/平移/长按菜单/控制栏重建已在模拟器上以真实系统事件（Option+拖拽捏合、单指拖动、真实点击与设置页操作）验证，但真机手势手感仍需作者确认；程序化结果不替代这些项。
2. **ARW 内嵌预览**：iOS ImageIO 对 Sony ARW 只返回约 1.7MP 内嵌预览（Mac 同文件为 7008×4672），`IsProxy=false` 使 `NeedsFull` 永不升级 Full。待作者裁定：改用 `CIRAWFilter` 全分辨率解码，或保持现状并记为平台限制。
3. **CoreML EP 不可用**：`Microsoft.ML.OnnxRuntime` 1.24.4 / 1.25.1 的 iOS 原生库均不支持 CoreML EP，DINO 走 CPU（注册失败记日志后回退）。需等上游包或另立 GPU 方案。
4. **DINO 模型包内重复**：原生壳不加载 Avalonia，模型另作 `BundleResource` 拷入 app bundle，与 avares 资源各一份（约 86MB），旧壳退役后消除。
5. **8 位 P3 编码边界**：超出 P3 立方体的原色（如 Adobe RGB 红原色）在 Full 档被钳到 P3 边界；属当前编码的既有边界，非 I2 回归。
6. **分析栏可见压测**：`--pv-analysis-open` + 50 连翻（每张升 Full）p50 447ms / p95 **2660ms** / max 4002ms、1 次超时；gc 峰值 1593MB，全程 Normal。是否优化留给性能专项。
7. **Release 通道默认仍旧壳**：`Debug iOS` 构建 Release 配置，默认进入 Avalonia 旧壳；原生壳用 `Debug iOS (Native)` 或显式 `--native`。切默认通道属主计划 Phase 6，待作者决定。
8. **iPhone 紧凑布局不在本轮范围**：同一原生壳可启动，不做专项适配。
9. **设置页逐条契约**：I4 只验证"关闭回写后主屏传播"；设置页视觉/输入体验逐条验收待作者手验。
10. ~~**共享 EXIF 伴侣时序**~~ **已修（2026-10-18）**：根因同"首图落在同名组非代表项"——`FolderViewModel.LoadFolderAsync` 无指定目标时取枚举顺序首张，同名 HIF+ARW 时常是被合并隐藏的 ARW（iOS 上 ARW 只有 1616×1080 内嵌预览，作者因此看到"首张双击 100% 只放大一点点"）。改为取同步重建后的筛选列表首项（即代表文件，按格式顺序 HIF 优先），筛选为空时回退枚举首张。Mac 实跑 HIF+ARW 同名目录 `CurrentFile => A7C01489.HIF`；Mac base/filmstrip/similarity PASS。
11. **100% 口径已与作者确认（2026-10-18）**：100% = 照片源像素与屏幕物理像素点对点（IMAGE-02），旧 Avalonia 逻辑点口径的差异按意图差异保留，不再对齐。
12. **待收尾统一清理的差异/遗留**：Mac 细节预览双击会清 `FollowsFocus`（Avalonia 保留）；Windows `ToggleFit` 无对焦点优先、无对焦跟随切图（W-M6）；iOS `UseInterpreter=true` 致增强等热循环慢（真机 24MP Full 约 1.7s，是否改纯 AOT 待定）；"增强后竖拍变横"未复现，待作者提供原图；iOS RAW 全分辨率（`CIRAWFilter`）待定（见第 2 条）。
