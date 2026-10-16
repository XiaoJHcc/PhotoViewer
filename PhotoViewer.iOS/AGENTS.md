# PhotoViewer.iOS — 原生壳（I1 基座 + I2 主图）与迁移期旧壳

I1 起：Debug 默认 **UIKit 原生壳**（不创建任何 Avalonia 对象），旧壳经启动参数 `--legacy-ui` 或 NSUserDefaults 开关 `PVLegacyUI`（`defaults write cc.xiaojh.PhotoViewer PVLegacyUI -bool YES`）回退；Release 默认旧壳，`--native` 启动原生。最低系统版本 **iOS 17**。执行清单见 [Plans/native-rebuild-phase-ipad.md](../Plans/native-rebuild-phase-ipad.md)；I2 已落地主图（Metal + `UIScrollView` 手势 + 编码协商），I3 主屏布局 / I4 输入 / I5 工具页未落地前，主屏是"主图占满 + 临时浮层（状态文本 + 打开文件夹 / 设置）"。

**启动参数来源**：iOS 不填充托管 `Main(string[] args)`（实测为空数组），一律读 `NSProcessInfo.ProcessInfo.Arguments`（`simctl launch <udid> <bundleId> <args…>` 会传入）。

**Apple 共享源**：`../PhotoViewer.Apple/` 纯源目录由 csproj `<Compile Include="..\PhotoViewer.Apple\**\*.cs"/>` 链接编译（展示解码器、Metal 渲染核心、视口/胶片栏/控制栏/分析栏几何、验收判定、命令符号与手势映射），与 [PhotoViewer.Mac](../PhotoViewer.Mac/AGENTS.md) 共用同一份，见 [其手册](../PhotoViewer.Apple/AGENTS.md)。本壳只保留 UIKit 宿主。改共享源需同时复核 Mac 的 `--pv-native-*` 回归。

**主图链路（I2）**：`PhotoLoadSession` 只管代际/代理↔Full/租约；`iOSPhotoView` 把共享 `MetalPhotoRenderer` 挂成 `CAMetalLayer` 子层（绘制目标恒 RGBA16F 扩展线性 sRGB），视口取共享 `PhotoViewport`；缩放/平移/回弹/惯性由覆盖其上的透明 `UIScrollView` 驱动——内容尺寸 = 适应态显示尺寸，`zoomScale`/`contentOffset` 与视口变换互为逆映射（`iOSPhotoView` 内 `PullViewportFromScroll` / `SyncScrollFromViewport`），滚动/捏合回调同帧重绘。双击在适应与物理 100%（点按位置为锚点）间切换，用 `ZoomToRect` 一次动画完成缩放与偏移（先 `SetZoomScale` 再 `SetContentOffset` 会互相取消，实测缩放停在原值）。**编码协商**：`iOSPresentationEncodingPolicy` 给代理档 RGBA16F、Full 档 8 位 Display P3、压力 ≥ Warning 时代理档也降 8 位 P3；准入估算与纹理记账按实际编码的每像素字节（Full 档 4 字节）。**启动顺序硬要求**：必须先上报有效视口再 `SetFile`（`iOSNativeRootViewController.StartSession` + `PhotoLoadSession.UpdateViewport` 的零尺寸守卫），否则代理目标会被钳成 1 像素、解码出 1×1 的"合格代理"且永不重载。导航缩略图、安全区与栏位布局留待 I3。

## 原生壳（Debug 默认）

| 文件/目录 | 职责 |
|---|---|
| Main.cs | 入口：挂 `BootTrace`、读 `NSProcessInfo` 参数、按开关选壳委托类型。未选中的壳委托类型不会被加载，故原生路径不创建 Avalonia 对象。 |
| Native/iOSShellSwitch.cs | 壳选择：`--legacy-ui` / `--native` > NSUserDefaults `PVLegacyUI` > 编译期默认（Debug 原生 / Release 旧壳）。 |
| Native/iOSNativeAppDelegate.cs | 原生 `UIApplicationDelegate`：接线门面与共享 VM（窗口交给 scene）；`openURL` 两个重载直接投递原生外部打开；`GetConfiguration` 声明原生 scene 委托。同文件的 `iOSNativeSceneDelegate` 处理 scene 连接 / URLContexts。 |
| Native/iOSNativeShell.cs | 原生启动顺序（对齐 `MacNativeApplication`）：`UiDispatcher`（主队列 SynchronizationContext）→ ReactiveUI builder（无 Avalonia）→ 门面（FilePicker / AppResources / Heif / Raw / `BitmapLoader.InitializePresentationDecoder(ApplePresentationDecoder, iOSPresentationEncodingPolicy)` / PerformanceBudget / MemoryGovernor / StorageAccess / Xmp / NativeSettingsPresenter / Settings 存储 / CoreML EP）→ `new MainViewModel(nativePresentation: true)` → `PhotoLoadSession` → 根控制器；并接线外部打开处理器、内存告警直报（`MemoryGovernor.ReportOsEvent` + 缓存规模记录 + `MemoryWarningEvent` 广播）、退后台/退出前设置落盘、压测（信号源 = 原生 `PhotoLoadSession`）与验收专用 `--pv-open <路径>` / `--pv-native-photo-check <目录>`。 |
| Native/iOSNativeRootViewController.cs | I2 原生主屏：主图占满屏幕（安全区与栏位布局待 I3），临时浮层保留状态文本与「打开文件夹 / 设置」；**构造期**订阅共享 VM 与展示会话（启动参数可能在根控制器上屏前就选中首图）、把会话产物直接交 `iOSPhotoView` 上传、上报物理视口与细节需求。 |
| Native/iOSPhotoView.cs | Metal 主图宿主 + `UIScrollView` 手势驱动（见上"主图链路"）；暴露 `CaptureLinearFrame`、视口状态与对焦点 API 供验收与后续里程碑（I4 对焦定位）。 |
| Native/iOSNativePhotoCheck.cs | **验收专用（测试钩子）** `--pv-native-photo-check <目录>`：核对代理先行与 Full 热替换（编码、变换不变、中心像素无色偏）、八方向 GPU 角点、分析输入与 GPU 朝向一致、四张色卡线性值、编码策略与准入字节；报告写 `Documents/PhotoViewer/native-photo-checks.txt`，打印 `[NativeSmoke] PASS photo (n checks)` / FAIL。素材要求 `orientation-1..8.jpg`、`color-{srgb,p3,adobe,untagged-adobe}.jpg`、`color-p3-large.jpg`（长边 ≥ 4000 的 Display P3 色卡，用于制造真实代理档；缺一即 FAIL）。 |
| Core/iOSPresentationEncodingPolicy.cs | iPad 编码协商：代理档 RGBA16F / Full 档 8 位 Display P3 / 压力 ≥ Warning 时代理也降 8 位 P3。 |
| Native/iOSUiServices.cs | `iOSUiDispatcher`（主线程调度 + `SynchronizationContext`）、`iOSFilePicker`（`UIDocumentPickerViewController`；文件夹选择并保留安全作用域，单文件按扩展名换算 UTType）、`iOSAppResources`（从 app bundle 读 DINO 模型）、`iOSWindowPresenter`（设置页走原生浮窗；工具页 I5 未实现，调用即抛 `NotSupportedException`）。 |
| Native/iOSSceneBridge.cs | 两个壳共用的 scene 接线：系统会按持久化 scene session 恢复委托类型，故无论哪个壳的委托收到连接，都按"当前活动壳"接窗口（原生根控制器或 Avalonia 单视图），并按活动壳选择 URLContexts 的投递口径。 |
| Core/iOSStorageRefs.cs | 原生 `IFileRef`/`IFolderRef`（对标 `MacStorageRefs`），按真实路径读写；安全作用域由 `iOSStorageAccessManager` 保留。 |
| Core/iOSViewControllerLocator.cs | 前台根控制器 / 展示链顶部定位，设置页与文档选择器共用。 |
| Core/iOSExternalOpenBridge.cs | 外部打开两条发布路径共用一份 URL→路径解析与作用域保留：旧壳 `PublishFromUrl`（Avalonia `IOSStorageItem` 反射，行为未变），原生壳 `PublishNativeFromUrl(s)`（不加载 Avalonia，直接发布原生存储引用）。 |
| Core/BootTrace.cs | 启动里程碑/异常取证（`Documents/PhotoViewer/ios-crash.log`）；`MarkAvaloniaAssemblies(阶段)` 记录该阶段已加载的 Avalonia 程序集。 |

**"无 Avalonia 对象"取证**：iOS 的 Xamarin 注册器在 `Main` 之前就已加载 Avalonia 程序集（`BootTrace.MarkAvaloniaAssemblies("Main enter(before UIApplication.Main)")` 即已列出），因此"程序集已加载"不等于"创建了 Avalonia 对象"。原生路径不引用、不初始化 Avalonia，也不创建 `Application`/窗口/位图适配器；判断依据是日志里的阶段对比与原生壳自身日志。

**DINO 模型打包**：旧壳读 avares 资源（嵌在 `PhotoViewer.dll`），原生壳不加载 Avalonia，故 csproj 另有 `BundleResource` 把 `PhotoViewer/Assets/Models/*.onnx` 拷进 app bundle（与 Mac 同口径，模型约 86 MB，包内因此出现两份副本）。**已知环境问题（I2 复核结论）**：iOS **模拟器**上 ONNX 原生库初始化失败（`Microsoft.ML.OnnxRuntime.NativeMethods` 类型初始化异常），只在真的走到推理时出现（如缩略图抖动徽标评估触发的 `DinoFeatureCache` 提取），真机 P1-M5 验收正常；I2 真机复核见 [执行清单 §6](../Plans/native-rebuild-phase-ipad.md)。

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

- 原生：`xcrun simctl launch --console <udid> cc.xiaojh.PhotoViewer [--native] [--pv-open <路径>] [--pv-native-photo-check <目录>]`，截图 `xcrun simctl io <udid> screenshot`，日志 `Documents/PhotoViewer/ios-crash.log`（BootTrace marks）+ 控制台；验收报告 `Documents/PhotoViewer/native-photo-checks.txt`。
- 真机：`xcrun devicectl device install app --device <设备> <app>` + `xcrun devicectl device process launch --device <设备> cc.xiaojh.PhotoViewer -- <参数…>`（设备需解锁才能挂载 DDI/安装）；素材经 `xcrun devicectl device copy to --device <设备> --source <本地> --destination <容器 Documents 路径>` 拷入，压测/内存日志用 `copy from` 取回（`stress-flip.jsonl` / `memory-events.jsonl` / `ios-crash.log`）。
- 旧壳：同命令加 `--legacy-ui`；**切换壳不重装**时会出现上一次运行的 scene session 被恢复，两个壳的 scene 委托都能按活动壳接线（见 `iOSSceneBridge`，实测两向均正常）。
