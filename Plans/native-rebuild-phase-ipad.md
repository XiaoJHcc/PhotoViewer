# iPad 原生主屏执行清单（UIKit + Metal）

> 2026-10-17 立项，隶属 [native-rebuild-master-plan-1.md](native-rebuild-master-plan-1.md)。前置：macOS 原生壳 P2/P3 已由作者验收（见 [handoff](native-rebuild-handoff.md)）。
> 主计划中 Phase 4/5 分别为 Windows/Android，本清单不占用其编号。
>
> **作者要求（2026-10-17）**：iPad 主屏与原 Avalonia 版本对标（布局结构、尺寸、边距、功能要点、选中/高亮语义）；设置已是原生，不在范围；需要针对触屏优化的地方可酌情使用原生控件。评估阶段的推荐项作者已全部采纳（见 §1）。

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
- `Main.cs` 按开关选择原生/旧壳 AppDelegate；原生启动顺序照 `MacNativeApplication`：`UiDispatcher`（主队列 SynchronizationContext）→ ReactiveUI builder → 门面注入（FilePicker / AppResources / Heif / Raw / PresentationDecoder / PerformanceBudget / MemoryGovernor / StorageAccess / Xmp / Settings / CoreML EP）→ `MainViewModel` → 根 VC。保留内存告警直报与 `BootTrace`、`--pv-stress` 接线。
- 原生 `IFileRef`/`IFolderRef`（对标 `MacStorageRefs`）+ 安全作用域书签；`UIDocumentPickerViewController` 选文件夹；外部打开（openURL / scene URLContexts）去掉 Avalonia 反射，直接走 `ExternalOpenService`。
- MinimumOSVersion 上调（§1-4），iOS 模拟器 + 真机构建零错误。

### I2 主图
- 编码协商落地（§1-1）：解码器、`BitmapLoader` 准入估算、`PhotoLoadSession`、GPU 记账、Governor 降级。
- iOS Metal 视图 + `UIScrollView` 手势驱动；双击 Fit ↔ 对焦点 100% 动画；导航图；跨屏/旋转刷新物理尺寸。
- **真机门（先于 I3）**：iPad mini 7 500 连翻（HEIC+ARW）无 jetsam、恒 Normal 或可恢复；告警注入降级生效；物理 100% Full 热替换无跳变；P3 色卡对拍（ColorTestTool / 原生色卡）。

### I3 主屏布局
- 根布局两向/自动（只看窗口方向），文件栏 116 内容宽、卡片 90×138+3 占位、80 方形、列表边距 10/10/5/10，与 Avalonia 对拍；读真实 `safeAreaInsets`，背景铺满边缘。
- 胶片栏 `UICollectionView` 按 FILM-01–06；减少动态效果读 `UIAccessibility.IsReduceMotionEnabled`。
- 筛选条 `UIButton`+`UIMenu` 下拉、计数、相似聚类 toggle；控制栏三分组换行/换列与居中，评分方向、显隐/排序、主次绑定提示。
- 旋转、Split View、Stage Manager 任意尺寸下几何正确。

### I4 输入
- 主图 `UIContextMenuInteraction`（打开、栏位显隐、分析栏、工具、设置；长按/辅助点按触发）；卡片上下文菜单含评分 0–5。
- 硬件键盘：`pressesBegan`/`UIKeyCommand` → `AppGesture`（HID usage 映射表，持久化字段不变）；输入控件获焦不误触（INPUT-01）。
- 触控板滚动/悬停、`UIDropInteraction` 从"文件"拖入、Fit 下横向轻扫翻页。
- 原生设置接线（`iOSNativeSettingsPresenter` 从根 VC 弹出），关闭回写后主屏即时刷新。

### I5 P3 功能
- 分析栏（几何同旧壳 `AnalysisView`，可见时主图升 Full）、细节预览点按定格；相似聚类面板（SIM-01）；增强预览（`PhotoLoadSession`）。
- 工具页：sheet / `UINavigationController` 承载 EXIF 详情与 DINO 诊断，瓦片与分析栏共用 UIKit 诊断瓦片。

### I6 验收与文档
- UIKit 验收驱动器 `--pv-native-*`（模拟器跑几何/布局/胶片栏/工具栏/分析/相似；devicectl 传参真机跑内存与 Full 热替换），只写自有测试副本与独立设置/数据库。
- 作者真机验收：色彩、画质、触摸手感（缩放/惯性/翻页/胶片栏）、键盘与触控板、拖入、旋转与多任务。
- 文档：ui-contract iOS 列与新增 iOS 差异条目；新建 `PhotoViewer.iOS/AGENTS.md`、`PhotoViewer.Apple/` 文件表；根 AGENTS.md §4/§5（新增 iOS 原生链路小节）、DEV.md 任务、handoff 状态与验证矩阵。

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
| 2026-10-17 | ONNX Runtime 原生库在 iOS 模拟器上类型初始化失败（真机 P1-M5 正常） | I2 复核；不影响 I1 占位主屏 |
| 2026-10-17 | 原生壳不加载 Avalonia，无法用 avares 资源读 DINO 模型 | 模型另作 BundleResource 拷入 app bundle（与 Mac 同口径；包内出现 avares + bundle 两份，约 86 MB） |
