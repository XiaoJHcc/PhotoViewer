# PhotoViewer.Android — Android 原生壳与迁移期旧壳

**当前阶段：A1b 基座（2026-10-18）**。Debug 默认原生壳，`--legacy-ui` 回退 Avalonia 旧壳；Release 默认旧壳，`--native` 进入原生壳（与 Mac/iOS 同口径）。A2–A6 未开始，主图/主屏/输入/设置/工具仍是后续里程碑。

## 启动链（A1b）

| 文件 | 职责 |
|---|---|
| AndroidApp.cs | 普通 `Android.App.Application`，**不初始化 Avalonia**；不得改回 `AvaloniaAndroidApplication`。 |
| AndroidBootstrapActivity.cs | 唯一 `MainLauncher` 与 `ACTION_VIEW`/`ACTION_SEND`/`ACTION_SEND_MULTIPLE`（`image/*`）入口；读 `pv-flags`（`--legacy-ui` / `--native`）后转发 Action/Data/ClipData/Extras 到目标壳并 `Finish()`。 |
| AndroidNativeActivity.cs | 原生壳 Activity（`AppCompatActivity` + `@style/Theme.PhotoViewer.Native`），不创建 Avalonia；A1b 只挂 `AndroidNativeShell` 的占位根视图，A2/A3 换真实主图与主屏。 |
| Native/AndroidNativeShell.cs | 原生启动顺序：`AndroidUiDispatcher`（主 Looper + `SynchronizationContext`）→ ReactiveUI builder → 平台门面（FilePicker/AppResources/Heif/PerformanceBudget/MemoryGovernor/XmpWriter/SettingsService/NativeWindowPresenter/DINO-NNAPI）→ `MainViewModel(nativePresentation: true)` → 根视图。日志必须出现 `[Native] ... Avalonia.Application.Current=null`。 |
| Native/AndroidNativeRootView.cs | A1b 占位根视图：深色 `#111111` + 原生壳标记；不引用 Avalonia 控件。A3 起由共享 `MainScreenLayout` 替换。 |
| MainActivity.cs | 迁移期 Avalonia 旧壳。`AvaloniaActivity` 子类；在 `base.OnCreate` **前**用 `AppBuilder.Configure<App>().UseAndroid()...SetupWithLifetime(...)` 自建 Avalonia，`base.OnCreate` **后**写 `Content` 触发 `SetContentView`。保留存储权限（≤ API 32）与 `AndroidExternalOpenBridge` 旧路径。 |
| Core/AndroidUiDispatcher.cs | 原生壳主线程调度/同步上下文，不借用 Avalonia 调度器。 |
| Core/AndroidAppResourceProvider.cs | 从 APK `Assets/Models/*.onnx` 读 DINO 模型（csproj `AndroidAsset`）。 |
| Core/AndroidFilePicker.cs | SAF 选择器占位；A2/A3 接入 `ACTION_OPEN_DOCUMENT_TREE`/`ACTION_OPEN_DOCUMENT` 与 `IFileRef`/`IFolderRef`。 |
| Core/AndroidNativeWindowPresenter.cs | 设置/工具页占位（Toast + 日志）；A4/A5 换 BottomSheet 与原生工具页。 |
| Core/Android{Heif,LibHeif}Decoder.cs / AndroidXmpWriter.cs / AndroidSettingsStorage.cs / AndroidPerformanceBudget.cs / AndroidMemoryBudgetProvider.cs | 既有平台能力，继续复用。 |

## 主题与资源

- `Theme.PhotoViewer.Native` 继承 `Theme.Material3.Dark.NoActionBar`，颜色全部走 `values/colors.xml` 自有常量（`native_background` 等），**不调用 DynamicColors**、不引用 OEM 主题派生属性。旧壳 `MyTheme.NoActionBar` 保留。
- `AndroidManifest.xml` 已移除 `MANAGE_EXTERNAL_STORAGE` / `WRITE_EXTERNAL_STORAGE`；`READ_EXTERNAL_STORAGE` 仅 `maxSdkVersion=32`。33+ 走 SAF。
- DINO 模型作为 `AndroidAsset` 打进 APK（约 +83MB），原生资源提供器按 `avares://PhotoViewer/Assets/Models/...` 路径部分读取。

## 构建（本机 2026-10-18）

默认 SDK 10.0.402 **没有** android workload；10.0.201 下已装（36.1.53）。不要改仓库 `global.json`（Apple 头需要 10.0.402 的 27 工作负载）。在任意临时目录放 `{"sdk":{"version":"10.0.201","rollForward":"disable"}}`，从该目录构建：

```bash
cd /tmp/pv-sdk201 && dotnet build /path/to/PhotoViewer.Android/PhotoViewer.Android.csproj -c Debug
```

长期建议 `sudo dotnet workload install android` 到 10.0.402。模拟器：`~/Library/Android/sdk/emulator/emulator -avd pv-api35`（Apple Silicon 必须 arm64-v8a 镜像）。

## 已验证（A1b 自验）

- Android Debug 构建 **0 错误 / 21 警告**（与既有基线一致；ONNX `libonnxruntime.so` 重复打包的 `XA4301` 已随 Linux 资产排除消失）。
- 默认启动 → `[Bootstrap] → native 壳`、`[Native] DINO 模型资源=ok`、`[Native] Avalonia.Application.Current=null`、`[Native] AndroidNativeActivity 就绪（未初始化 Avalonia）`；`am start -n cc.xiaojh.PhotoViewer/.AndroidBootstrapActivity`（稳定 Activity 名，见 `[Activity(Name=...)]`）；首次启动 `Displayed +940ms`。
- `adb shell am start -n cc.xiaojh.PhotoViewer/.AndroidBootstrapActivity --es pv-flags "--legacy-ui"` → `[Bootstrap] → legacy 壳`、`[Legacy] 主视图挂载完成（mainView=True）`，Avalonia 主界面正常；首次启动 `Displayed +1s094ms`。

## 发现清单（A1b）

| # | 现象 | 处理 |
|---|---|---|
| 1 | Avalonia 12 把 `ISingleViewApplicationLifetime` / `IApplicationLifetime` 标为 `NotClientImplementable`，手写 lifetime 编译失败 | 旧壳实例化 `Avalonia.Android.ApplicationLifetime`（internal，与 `AvaloniaAndroidApplication` 同一类型）并走 `IActivityApplicationLifetime.MainViewFactory`；待上游公开 API 后移除反射 |
| 2 | `AvaloniaActivity` 的默认 `InitializeAvaloniaView` 只创建 `AvaloniaView`，不调用 `SetContentView`；在 `base.OnCreate` 前写 `Content` 会得到白屏 | 旧壳在 `base.OnCreate` **之后**写 `Content`，触发 setter 的 `SetContentView` 分支 |
| 3 | APK 同时打进 Linux glibc 的 `libonnxruntime.so`（依赖 `libdl.so.2`）与 AAR 的 Android bionic 版本，前者胜出，`libonnxruntime4j_jni.so` 启动即 `UnsatisfiedLinkError` | **已修**：csproj `_PvRemoveLinuxOnnxFromApk` 在 `_BuildApkEmbed` 前从 `FrameworkNativeLibrary` 移除 `runtimes/linux-arm64/native/*.so`；APK 现用 AAR 的 bionic 版本（md5 5316513a…），启动无 `libdl.so.2` 错误 |
| 4 | `AndroidAsset` 实际打进 `assets/Models/...`，而共享 URI 是 `avares://PhotoViewer/Assets/Models/...` | `AndroidAppResourceProvider` 去掉开头 `Assets/`；原生壳启动日志打印 `DINO 模型资源=ok` 验证 |
| 5 | SAF 选择器 / 设置 / 工具页当前是占位 | A2/A3/A4/A5 按计划替换；占位有日志与 Toast，不冒充已实现 |
