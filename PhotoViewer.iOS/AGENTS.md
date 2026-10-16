# PhotoViewer.iOS — 原生壳（I1 基座）与迁移期旧壳

I1 起：Debug 默认 **UIKit 原生壳**（不创建任何 Avalonia 对象），旧壳经启动参数 `--legacy-ui` 或 NSUserDefaults 开关 `PVLegacyUI`（`defaults write cc.xiaojh.PhotoViewer PVLegacyUI -bool YES`）回退；Release 默认旧壳，`--native` 启动原生。最低系统版本 **iOS 17**。执行清单见 [Plans/native-rebuild-phase-ipad.md](../Plans/native-rebuild-phase-ipad.md)；后续里程碑（I2 主图 / I3 主屏布局 / I5 工具页）未落地前，原生壳只到"占位主屏 + 文件源可用"。

**启动参数来源**：iOS 不填充托管 `Main(string[] args)`（实测为空数组），一律读 `NSProcessInfo.ProcessInfo.Arguments`（`simctl launch <udid> <bundleId> <args…>` 会传入）。

**Apple 共享源**：`../PhotoViewer.Apple/` 纯源目录由 csproj `<Compile Include="..\PhotoViewer.Apple\**\*.cs"/>` 链接编译（展示解码器、Metal 渲染核心、视口/胶片栏/控制栏/分析栏几何、命令符号与手势映射），与 [PhotoViewer.Mac](../PhotoViewer.Mac/AGENTS.md) 共用同一份，见 [其手册](../PhotoViewer.Apple/AGENTS.md)。本壳只保留 UIKit 宿主。改共享源需同时复核 Mac 的 `--pv-native-*` 回归。

## 原生壳（Debug 默认）

| 文件/目录 | 职责 |
|---|---|
| Main.cs | 入口：挂 `BootTrace`、读 `NSProcessInfo` 参数、按开关选壳委托类型。未选中的壳委托类型不会被加载，故原生路径不创建 Avalonia 对象。 |
| Native/iOSShellSwitch.cs | 壳选择：`--legacy-ui` / `--native` > NSUserDefaults `PVLegacyUI` > 编译期默认（Debug 原生 / Release 旧壳）。 |
| Native/iOSNativeAppDelegate.cs | 原生 `UIApplicationDelegate`：接线门面与共享 VM（窗口交给 scene）；`openURL` 两个重载直接投递原生外部打开；`GetConfiguration` 声明原生 scene 委托。同文件的 `iOSNativeSceneDelegate` 处理 scene 连接 / URLContexts。 |
| Native/iOSNativeShell.cs | 原生启动顺序（对齐 `MacNativeApplication`）：`UiDispatcher`（主队列 SynchronizationContext）→ ReactiveUI builder（无 Avalonia）→ 门面（FilePicker / AppResources / Heif / Raw / `BitmapLoader.InitializePresentationDecoder(ApplePresentationDecoder)` / PerformanceBudget / MemoryGovernor / StorageAccess / Xmp / NativeSettingsPresenter / Settings 存储 / CoreML EP）→ `new MainViewModel(nativePresentation: true)` → 根控制器；并接线外部打开处理器、内存告警直报（`MemoryGovernor.ReportOsEvent` + 缓存规模记录 + `MemoryWarningEvent` 广播）、退后台/退出前设置落盘、`--pv-stress` 与验收专用 `--pv-open <路径>`。 |
| Native/iOSNativeRootViewController.cs | **I1 占位主屏**（脚手架，I2/I3 替换）：深色底 + 中心状态文本（文件夹张数 / 当前文件名）+「打开文件夹」「设置」按钮；只读共享 VM 状态，不做主图渲染。 |
| Native/iOSUiServices.cs | `iOSUiDispatcher`（主线程调度 + `SynchronizationContext`）、`iOSFilePicker`（`UIDocumentPickerViewController`；文件夹选择并保留安全作用域，单文件按扩展名换算 UTType）、`iOSAppResources`（从 app bundle 读 DINO 模型）、`iOSWindowPresenter`（设置页走原生浮窗；工具页 I5 未实现，调用即抛 `NotSupportedException`）。 |
| Native/iOSSceneBridge.cs | 两个壳共用的 scene 接线：系统会按持久化 scene session 恢复委托类型，故无论哪个壳的委托收到连接，都按"当前活动壳"接窗口（原生根控制器或 Avalonia 单视图），并按活动壳选择 URLContexts 的投递口径。 |
| Core/iOSStorageRefs.cs | 原生 `IFileRef`/`IFolderRef`（对标 `MacStorageRefs`），按真实路径读写；安全作用域由 `iOSStorageAccessManager` 保留。 |
| Core/iOSViewControllerLocator.cs | 前台根控制器 / 展示链顶部定位，设置页与文档选择器共用。 |
| Core/iOSExternalOpenBridge.cs | 外部打开两条发布路径共用一份 URL→路径解析与作用域保留：旧壳 `PublishFromUrl`（Avalonia `IOSStorageItem` 反射，行为未变），原生壳 `PublishNativeFromUrl(s)`（不加载 Avalonia，直接发布原生存储引用）。 |
| Core/BootTrace.cs | 启动里程碑/异常取证（`Documents/PhotoViewer/ios-crash.log`）；`MarkAvaloniaAssemblies(阶段)` 记录该阶段已加载的 Avalonia 程序集。 |

**"无 Avalonia 对象"取证**：iOS 的 Xamarin 注册器在 `Main` 之前就已加载 Avalonia 程序集（`BootTrace.MarkAvaloniaAssemblies("Main enter(before UIApplication.Main)")` 即已列出），因此"程序集已加载"不等于"创建了 Avalonia 对象"。原生路径不引用、不初始化 Avalonia，也不创建 `Application`/窗口/位图适配器；判断依据是日志里的阶段对比与原生壳自身日志。

**DINO 模型打包**：旧壳读 avares 资源（嵌在 `PhotoViewer.dll`），原生壳不加载 Avalonia，故 csproj 另有 `BundleResource` 把 `PhotoViewer/Assets/Models/*.onnx` 拷进 app bundle（与 Mac 同口径，模型约 86 MB，包内因此出现两份副本）。**已知环境问题**：iOS 模拟器上 ONNX 原生库初始化失败（`Microsoft.ML.OnnxRuntime.NativeMethods` 类型初始化异常），真机 P1-M5 验收正常；I2 需复核。

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

- 原生：`xcrun simctl launch --console <udid> cc.xiaojh.PhotoViewer [--pv-open <路径>]`，截图 `xcrun simctl io <udid> screenshot`，日志 `Documents/PhotoViewer/ios-crash.log`（BootTrace marks）+ 控制台。
- 旧壳：同命令加 `--legacy-ui`；**切换壳不重装**时会出现上一次运行的 scene session 被恢复，两个壳的 scene 委托都能按活动壳接线（见 `iOSSceneBridge`，实测两向均正常）。
