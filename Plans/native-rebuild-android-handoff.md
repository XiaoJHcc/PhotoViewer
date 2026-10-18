# Android 原生壳交接（Windows 评审 → Mac 执行）

> 状态真源：本文件 + [native-rebuild-phase-android.md](native-rebuild-phase-android.md)。2026-10-18：Windows 评审结论已并入 `refactor/native-rebuild`，`native-rebuild-android` 分支用完即删；A1 在 Mac 本机继续。
> 来源：自 `refactor/native-rebuild` 的 `12cf71a` 切出，Windows 侧只做计划评审、Android Debug 基线构建与模拟器镜像下载，**没有改共享源、没有开始 A1**。本文件保留评审结论与 Mac 执行记录。

## 1. 当前状态

- **真机反馈修复（2026-10-18，25128PNA1C / API 36）**：P3 表面 True；筛选下拉/列布局卡片居中/相似面板宽度/长按菜单位置均已修，待作者解锁目视。
- **A6 起步（2026-10-18）**：`AndroidNativeChecks` 以 `--es pv-check <mode>` 跑主屏几何/Fit↔100%/Full/分析栏/相似面板/设置/工具页开合；API 35 arm64 五模式 **PASS 14/18/15/16/16**，报告 `files/PhotoViewer/android-checks-<mode>.txt`。`all` 连跑偶发 SIGSEGV（分模式稳定，§7）；API 矩阵、两向、fontScale/DPI、真机门未做。
- **A5 已完成（2026-10-18，本机 API 35 arm64 模拟器）**：分析栏（`AndroidAnalysisHost` + 共享 `AnalysisPresenter`，6 瓦片/细节定格绿框/准星）、相似聚类（窄屏紧凑筛选条 toggle、真实“提取全部”后 100% 分数卡片与柱状条）、工具页（EXIF 详情分组表格/多格式；DINO 5 瓦片 + 刚体拟合）、增强预览均已落地；均为**目视/日志核对**。新增警告清零，重建 20 警告 / 0 错误（低于 A1b 基线 21）。**A6（程序化 harness/矩阵/真机门）未开始**。
- **A2 + A3 + A4 已完成（2026-10-18，本机 API 35 arm64 模拟器）**：主图（`AndroidPresentationDecoder`/`AndroidPresentationEncodingPolicy` + 自有 EGL14/GLES 渲染 + `PhotoViewport` 手势）、主屏（`AndroidMainScreenLayout` 叠加系统 insets、自绘筛选条、RecyclerView 胶片栏/自绘卡片、置中控制栏、导航图）、SAF 存储、硬件键盘/滚轮/长按菜单/返回、半屏设置（五分类 + 工作副本关闭回写）均已落地。**A5 已完成；A6（程序化 harness/矩阵/真机门/文档收口）未开始**。自验：原生壳加载 13 张夹具、方向 6 朝向正确、代理 2706×1804 → Full 6000×4000、导航图/百分比正确、卡片星级 XMP 写回不改主图、键盘 Right/F、设置关闭回写后评分分组与卡片星级即时消失；均为**目视/日志核对，不是程序化验收**。
- **A2-A4 期间修掉两个真问题**：① 3+3 邻居预取同时解 Full 导致 13 张夹具（含 6000×4000）启动后 SIGSEGV；共享 `BitmapLoader.PrefetchFullNeighbors` 默认 true，Android 壳置 false，邻居统一代理档，Mac/iOS/Windows 行为不变。② 导航图 FBO 回读内容错位，改为 BitmapFactory 子采样 + 方向转正（长边 300dp），登记为平台实现差异。③ 主题显式蓝色主色，避免 Material 默认紫色。
- 计划评审已完成，结论：技术路线可执行；原 1 个硬阻塞（A1 的 Mac/iOS 回归门）已于 2026-10-18 在本机执行，评审修订见 §3。
- Windows 基线：`dotnet build PhotoViewer.Android/PhotoViewer.Android.csproj -c Debug` = **0 错误 / 21 警告**（含既有 NU1903、CS86xx、CA1422、XA4301；计划里的「14 条」已过时）。
- Windows 已下载 API 24/28/33/35 的 `google_apis;x86_64` 镜像；**这些镜像不能带到 Apple Silicon Mac**，Mac 已重新下载 `arm64-v8a`（见 §4）。
- **Mac 本机（2026-10-18）**：Apple Silicon / macOS 26.6.2 / Xcode 27.1 / .NET SDK **10.0.402（默认）与 10.0.201** / ios + macos workload 27.0.10722 / JDK 21 Temurin。**android workload 只装在 10.0.201 下**（36.1.53）；默认 10.0.402 下没有，`dotnet build` 会报 NETSDK1147。SDK 已补 `platforms;android-35`、`emulator`、`system-images;android-35;google_apis;arm64-v8a`，并已创建 AVD `pv-api35`（pixel_7）。
- **Android 构建/启动已验证（2026-10-18）**：在临时目录用 `global.json` pin 10.0.201（不改仓库 `global.json`，Apple 头仍用 10.0.402）构建 Android 头 = **0 错误 / 21 警告**；APK 约 362MB（含 86MB DINO 模型）安装到 API 35 arm64 模拟器，原生壳与旧壳均可启动。构建命令见 §4 末尾。
- **A1a 已落地**：7 个几何文件移入 `PhotoViewer.Shared/`（命名空间 `PhotoViewer.Shared`），`InputCommandMap.CommandForGesture` 抽出；Mac/iOS/Android 三个 csproj 已链接共享源。Mac 与 iOS Debug 构建 0 错误。
- **A1a 回归（2026-10-18）**：Mac `--pv-native-smoke` **39 checks PASS**，`input / toolbar / layout / analysis / similarity / tools / filmstrip` 七套专项 PASS；iOS 模拟器（iPad mini A17 Pro）七套全 PASS：photo **10** / gesture **24** / layout **24** / input **29** / analysis **18** / similarity **10** / tools **7**。
- **settings 套件假阴性**：`--pv-native-settings` 在“勾选后重绘”断言失败，原因是本机未授予屏幕录制权限，`screencapture -l` 失败后 SwiftUI 图层的 `cacheDisplay` 导出空白；迁移前基线（`3ce703a` + 仅修最低系统版本）同断言同样失败，且 KimiCU 目视窗口内容正常。**不是 A1a 行为变化**，已登记，待补抓屏权限或修复离屏渲染后复跑。
- **Mac 构建阻塞已修**：workload 27.0.10722 要求 macOS 最低 14.0，Mac head 原写 12.0；已把 `PhotoViewer.Mac.csproj` 与 `Info.plist` 对齐 14.0。workload 升级后 iOS 首次构建需先删 `obj/bin`（否则 `_xamarin_*` 未定义符号）。
- **A1b 基座已完成（2026-10-18）**：`AndroidApp` 改普通 `Application`（不初始化 Avalonia）；`AndroidBootstrapActivity` 接管 launcher 与 `ACTION_VIEW`/`SEND`/`SEND_MULTIPLE` 并分派；`AndroidNativeActivity` + `AndroidNativeShell`（dispatcher/ReactiveUI/门面/`MainViewModel`/占位根视图）；`Theme.PhotoViewer.Native`（Material 3 固定深色，无 DynamicColors）；Material/RecyclerView/Window/Fragment 依赖；`AndroidAppResourceProvider`/`AndroidUiDispatcher`/`AndroidFilePicker`/`AndroidNativeWindowPresenter` 落地（A2–A5 已替换为真实 SAF/设置/工具展示）。
- **A1b 自验**：默认启动 → `[Bootstrap] → native`、`[Native] DINO 模型资源=ok`、`[Native] Avalonia.Application.Current=null`、`AndroidNativeActivity 就绪`；`--legacy-ui` → `[Bootstrap] → legacy`、`[Legacy] 主视图挂载完成（mainView=True）`，Avalonia 主界面正常。首次启动 `Displayed`：native **+940ms** / legacy **+1.09s**。manifest 已移除 MANAGE/WRITE 宽权限。
- **A1b 期间修掉两个真问题**：① Avalonia 12 lifetime 接口 `NotClientImplementable`，旧壳实例化 Avalonia.Android internal `ApplicationLifetime` 并走 `IActivityApplicationLifetime.MainViewFactory`；`AvaloniaActivity` 必须在 `base.OnCreate` 之后写 `Content` 才会 `SetContentView`（否则白屏）。② ONNX Runtime 1.24.4 把 Linux glibc `libonnxruntime.so` 误打进 APK 并盖掉 AAR bionic 版，`libonnxruntime4j_jni.so` 启动即 `UnsatisfiedLinkError`；csproj 的 `_PvRemoveLinuxOnnxFromApk` 已移除 Linux 资产。
- **A4 偏差**：数值设置用 +/- 步进（非滑条）、列表排序用上下按钮（非拖拽）、拖入未接。
- **A5 偏差**：手机分析栏约 58dp 瓦片未做手机专用放大/横滑；相似面板空态动作按钮在占位下方、进度用文本；EXIF 表 4 可见列承载共享 7 列几何、未做水平滚动；DINO 抖动矢量线为简化绘制。

## 2. 为什么曾在 Windows 暂停

[native-rebuild-phase-android.md](native-rebuild-phase-android.md) §3 A1 的迁移门是「Mac 八套 + iOS 七套 `--pv-native-*` 回归零行为变化」。Windows 没有 Mac/iOS 构建链与模拟器，无法执行；按作者选择暂停，不用「静态看起来没问题」冒充回归通过。

Mac 上可以一次做完 A1 门，这是推荐路径（2026-10-18 已在本机执行）。若没有 Mac，唯一可解锁的替代方案是：Android csproj 直接逐文件链接 `PhotoViewer.Apple/` 下 7 个纯数学源，不改 Apple 两头；但这偏离计划的 `PhotoViewer.Shared/` 结构，需作者书面同意。

## 3. 开工前必须写回计划的修订

| # | 计划位置 | 发现 | 处理 |
|---|---|---|---|
| 1 | §1.11 | 「原生路径不构造任何 Avalonia 对象（不构造 `AndroidApp`）」在 Android 上字面不可实现：清单里的 Application 类由系统在 Activity 之前实例化；当前 [AndroidApp.cs](../PhotoViewer.Android/AndroidApp.cs) 继承 `AvaloniaAndroidApplication<App>`，`OnCreate` 就初始化 Avalonia | 改为普通 `Application`；旧壳 [MainActivity.cs](../PhotoViewer.Android/MainActivity.cs) 改 `AvaloniaActivity`，在 `OnCreate` 前用 `AppBuilder.Configure<App>().UseAndroid().SetupWithLifetime(自定义 ISingleViewApplicationLifetime)` 自建 Avalonia。`AvaloniaMainActivity` 依赖 internal `IAndroidApplication.Lifetime`，普通 Application 下不能用它；`AvaloniaActivity` 只要求 `Avalonia.Application.Current`，可行。必须实测旧壳 `--legacy-ui` 仍启动 |
| 2 | §1.5 / §1.7 | 没写明 Android 视图坐标空间。共享几何（116dp 栏、96×144 卡片）按点算，Android `View` 坐标是 px；混用会让 Fit、物理 100%、卡片几何全部偏 | 明确：共享几何一律以 **dp 当点**；`PhotoViewport.ViewWidth/Height` 用 dp，`BackingScale = DisplayMetrics.Density`，`Scale = 1/density` 时 `PhysicalScale == 1`；仅在 View 边界做 px↔dp，触摸坐标先转 dp |
| 3 | §1.6 | `InputCommandMap` 若把 Apple 的修饰键解码一起搬走，Android 会继承 Cmd/Option 语义 | 共享 `InputCommandMap` 只放 `CommandForGesture`（以及通用的位掩码→`GestureModifiers`）；`AppleKeyboardMapping`、HID usage 表、`FromRaw`/`FromHidUsage` 留在 `AppleInputMapping`；Android 新写 `AndroidInputMapping`，按 `KeyEvent` 的 Ctrl/Alt/Shift/Meta 语义重写，`GestureKey` 名称复用 |
| 4 | §1.11 / Manifest | 只说了新增 bootstrap activity，没列 launcher 与 IntentFilter 迁移 | `AndroidBootstrapActivity` 接管 `MainLauncher=true` 与 `ACTION_VIEW`/`ACTION_SEND`/`ACTION_SEND_MULTIPLE`（`image/*`）；`MainActivity` 去掉 launcher 与外部打开 filter；bootstrap 解析 `--pv-flags` 后决定壳，并把 Intent 转发给对应壳。否则点图标会直接进旧壳，外部打开也绕过 bootstrap |
| 5 | §2 / §1.12 | DINO 模型 86MB 在 `PhotoViewer` 的 Avalonia 资源里；原生壳不加载 Avalonia，`AppResources` 旧路径不可用 | 新增 `AndroidAppResourceProvider`（`AssetManager.Open`）+ csproj `AndroidAsset` 打包 `..\PhotoViewer\Assets\Models\*.onnx`；APK 会多约 86MB。同时补 `AndroidUiDispatcher`、`AndroidFilePicker`、`AndroidNativeWindowPresenter`、`AndroidNativeSettingsPresenter`、`StorageAccessManager`、`OnTrimMemory` 压力上报 |
| 6 | §3 A2/A3 | 验收项 19 要求导航图方向正确，但「缩放指示器」没有明确落在 A2 还是 A3 | 归 A3：`PhotoViewport.NavigationSize/NavigationViewport`，覆盖在 `SurfaceView` 之上的普通 View，长边 150dp、百分比、可见框；点按/拖动导航图定位可作为平台增强 |
| 7 | §1.7 | 没提筛选条行布局左对齐。共享 `MainScreenLayout.FilterBarFrames` 默认给 macOS 交通灯居中 | Android 行布局显式 `alignLeading: true`，与 iPad 同口径，左上不留空带 |
| 8 | §1.10 / A2 | SAF 写回链路没验：现有 [AndroidXmpWriter.cs](../PhotoViewer.Android/Core/AndroidXmpWriter.cs) 已支持 `content://`（`OpenFileDescriptor("rw")`），但需要 `IFileRef.Path` 返回 content URI，且 Intent 要带写授权 | `ACTION_OPEN_DOCUMENT_TREE` 用 `takePersistableUriPermission(Read|Write)`；`ACTION_OPEN_DOCUMENT` 加 `FLAG_GRANT_WRITE_URI_PERMISSION`；SD 卡上若 `"rw"` 失败改 `"rwt"`；外部打开走原生 `IFileRef`，不要调 `AndroidExternalOpenBridge` 的 Avalonia 反射路径 |
| 9 | §3 A6 | 「5 API × DPI 两档 × fontScale 两档 × 两向」= 40 种组合，没写抽样规则 | 建议：5 个 API 只跑纵向启动/几何；API 35 + 24 补横向；fontScale 1.0/1.3 与两档 DPI 只在 API 35 跑；真机只补手感/色彩/SAF/杀后台 |
| 10 | 全篇 | 基线警告写 14 条；当前是 21 警告 / 0 错误；iOS 回归套件实际是 7 套（photo/gesture/layout/input/analysis/similarity/tools），不是 6 套 | 以本次构建日志冻结基线；A1 门按 Mac 八套 + iOS 七套排时间；不要把既有警告算成新增 |
| 11 | §1.12 | Material 依赖版本未定；Material 1.14 可能和 Avalonia 12.0.2 带来的 AndroidX 版本冲突 | 先钉 `Xamarin.Google.Android.Material`（1.14.0.6，冲突则退 1.13.0.6）、`RecyclerView` 1.4.0.6、`Window` 1.5.1.3、`Fragment` 1.9.0；A1 立即构建验证 `Theme.Material3.Dark.NoActionBar` 与 `postSplashScreenTheme` 原生链 |

## 4. Mac 环境准备（2026-10-18 实测）

- **架构**：`PhotoViewer.Mac.csproj` 只声明 `<RuntimeIdentifiers>osx-arm64</RuntimeIdentifiers>`。本机是 Apple Silicon，无需改；Intel Mac 需先加 `osx-x64` 并验证。
- **本机已具备**：Xcode 27.1（`/Applications/Xcode.app`）、.NET SDK 10.0.402、`ios`/`macos` workload 27.0.10722、JDK 21 Temurin、Android SDK 目录与 licenses。
- **本机缺**：`android` workload、Android emulator、system image。按下面步骤补齐：

```bash
# 1) Android workload（dotnet 在 /usr/local/share/dotnet，需要管理员）
sudo dotnet workload install android
dotnet workload list          # 应出现 android

# 2) SDK 组件（Apple Silicon 必须 arm64-v8a；x86_64 镜像不可用）
SDK="$HOME/Library/Android/sdk"
"$SDK/cmdline-tools/latest/bin/sdkmanager" --licenses
"$SDK/cmdline-tools/latest/bin/sdkmanager" \
  "platforms;android-35" "emulator" \
  "system-images;android-35;google_apis;arm64-v8a"

# 3) AVD（先建 API 35 日常用；A6 再补 24/28/33/36）
"$SDK/cmdline-tools/latest/bin/avdmanager" create avd \
  -n pv-api35 -k "system-images;android-35;google_apis;arm64-v8a" -d pixel_7

# 4) 启动与确认
"$SDK/emulator/emulator" -avd pv-api35 &
"$SDK/platform-tools/adb" devices
```

- **Android SDK 路径**：csproj 已回退 `$(HOME)/Library/Android/sdk`；若用其他路径，设 `AndroidSdkDirectory` 或 `ANDROID_HOME`。
- **模拟器镜像**：Apple Silicon 必须 `arm64-v8a`；API 24/28/33/35/36 的 `google_apis;arm64-v8a` 均可用（已核对仓库列表）。Windows 已下的 `x86_64` 不要拷。
- **AVD 数量**：至少 API 35 一台日常用；A6 再补 24/28/33/36。DPI/fontScale 用 `wm density` / `settings put system font_scale` 调，不必为每档建 AVD。
- **构建命令（默认 10.0.402 缺 android workload 时）**：不要改仓库 `global.json`（Apple 头需要 10.0.402 的 27 工作负载）。在任意临时目录放 `{"sdk":{"version":"10.0.201","rollForward":"disable"}}`，从该目录用绝对路径构建：
  `cd /tmp/pv-sdk201 && dotnet build /path/to/PhotoViewer.Android/PhotoViewer.Android.csproj -c Debug`。
  长期仍建议 `sudo dotnet workload install android` 到 10.0.402，使默认 `dotnet build` 一致。
- **AVD 启动**：`~/Library/Android/sdk/emulator/emulator -avd pv-api35`；无窗口验收可加 `-no-window -no-audio -no-boot-anim -gpu swiftshader_indirect`。
- **素材**：`/release/` 在 [.gitignore](../.gitignore) 里，**新 clone 不会有**。本机已有 `release/native-p2-fixtures`（含 `orientation-1..8.jpg`、四张色卡）、`release/native-p2-large-fixtures/color-p3-large.jpg`、`release/native-p2-raw/A7C01489.ARW`、`release/native-p2-toolbar-fixtures`。A1 的 iOS 回归需要 `focus-src.ARW`（由 `release/native-p2-raw/A7C01489.ARW` 改名）。缺素材时不要让回归静默跳过。

## 5. A1 执行顺序（建议拆两笔提交）

1. **直接在本机 `refactor/native-rebuild` 工作**：原 `native-rebuild-android` 分支的内容（本文件）已并入，分支已删除；不再另开 Android 分支。
2. **A1a 共享源迁移（2026-10-18 已完成）**：7 个几何文件移入 `PhotoViewer.Shared/`，命名空间改 `PhotoViewer.Shared`；Mac/iOS/Android 三个 csproj 加 `Compile Include="..\PhotoViewer.Shared\**\*.cs"`；Mac/iOS/Android 加 `<Using Include="PhotoViewer.Shared" />`。`InputCommandMap.CommandForGesture` 抽出，Apple 侧保留修饰键/HID 解码。用 `git diff --color-moved` + 文件哈希确认只有 namespace/using 变化。
3. **A1a 回归门**：Mac 构建 + 八套 `--pv-native-*`；iOS 构建 + 七套 `--pv-*check`（模拟器即可；真机项留给作者）。零行为变化后提交，例如 `A1a: 共享几何源迁移 + InputCommandMap（Mac/iOS 回归通过）`。
4. **A1b Android 基座**：普通 `Application` + 旧壳 `AvaloniaActivity` 自建（§3#1）、`AndroidBootstrapActivity`/`AndroidNativeActivity`/`AndroidNativeShell`、`Theme.PhotoViewer.Native` 与独立 splash 链、manifest/launcher/IntentFilter 迁移、Material/RecyclerView/Window/Fragment 依赖、`AndroidAppResourceProvider`、`AndroidUiDispatcher`/`FilePicker`/`WindowPresenter`/`SettingsPresenter` 接线。
5. **A1b 自验**：Android Debug 构建零错误零新增警告；`--legacy-ui` 旧壳可启动；原生壳可启动且日志证明未初始化 Avalonia；主题深色、无 DynamicColors；记录首次启动耗时。提交 `A1b: Android 原生壳基座（bootstrap/主题/门面）`。
6. 再进 A2。A1 门未过不得进 A2。

## 6. A2–A6 交接要点

- A2：`AndroidPresentationDecoder`/`EncodingPolicy` 走 8 位 sRGB 或 Display P3；纹理 `GL_RGBA8`；八方向 UV；代理↔Full 只换纹理。模拟器只验 sRGB 回退 + EGL P3 协商代码，P3 正向留真机门。
- A3：几何全部 dp；导航图与筛选条左对齐按 §3#6/#7；`RecyclerView` 卡片状态只读不可变快照。
- A4：设置 BottomSheet 半屏，五分类；`OnDismiss` 一次性回写；返回手势先关 sheet；fontScale 1.3 要实测控制栏不截断。
- A5：分析栏/相似/工具页复用共享 presenter；DINO 模型按 §3#5；工具页只有 EXIF/DINO，无照片数据统计。
- A6：验收驱动器写 App 外部文件目录（`GetExternalFilesDir`），`adb pull`；矩阵按 §3#9 抽样；`PhotoViewer.Android/AGENTS.md`、根 `AGENTS.md`、`Design/architecture.md`、`ui-contract` Android 列、`DEV.md`、handoff 同步。
- 提交：每里程碑一笔，**分支 `refactor/native-rebuild`**；原 `native-rebuild-android` 已并入并删除，不再单独维护 Android 分支。

## 7. 未关闭 / 风险

- **A1a**：已完成（Mac 七套专项 + base 39，iOS 七套全 PASS；settings 为抓屏权限假阴性）。
- **A1b**：已完成（原生/旧壳均可启动，0 错误 / 21 警告）。
- **A2-A5**：功能已落地并在 API 35 模拟器目视/日志自验；**A6 部分完成**（五模式程序化 PASS，矩阵/真机未做）。未关闭：程序化 harness、两向/栏位显隐/卡片几何/八方向/双击物理 100% 断言、fontScale 1.3、拖拽排序、拖入、真实 P3 面板、真机手感/SAF/杀后台。
- **Android 警告**：A2–A5 新增警告已清零；重建 **20 警告 / 0 错误**（低于 A1b 基线 21，剩余为既有 `AndroidExternalOpenBridge`/`AndroidHeifDecoder`/`AndroidSettingsStorage` 的 CS86xx/CA1422）。
- **Android workload**：默认 10.0.402 仍缺；当前用 10.0.201 的 workload（§4 构建命令）。要让默认 `dotnet build` 一致，仍需 `sudo dotnet workload install android`。
- **DINO**：模型资产与 ONNX bionic 库已修到可加载；A5 工具页已跑真实 DINO 诊断（模拟器 CPU，5 瓦片 + 刚体文本），NNAPI EP 在模拟器不可用并按既有路径回退 CPU。
- **Mac 工具链**：workload 27.0.10722 要求 macOS 14.0，已把 Mac head 与 Info.plist 对齐；升级 Apple workload 后先删 iOS `obj/bin` 再构建。
- P3 正向路径在模拟器上大概率不可得，真机验收清单已列。
- iOS 回归素材不在 git；本机已有色卡/ARW，`focus-src.ARW` 需由 `release/native-p2-raw/A7C01489.ARW` 改名。
- `PhotoViewer.Android` 当前 Debug APK 约 362MB（含 86MB DINO 模型）；冷启动 native +940ms / legacy +1.09s，Release/AOT 与真实设备待测。
- ARW 在 Android 的 libheif/系统解码链路未验；验收清单第 40 项要求记录是否只有内嵌预览。
