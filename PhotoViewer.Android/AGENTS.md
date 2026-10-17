# PhotoViewer.Android — Android 原生壳与迁移期旧壳

**当前阶段：A2 + A3 + A4 已完成（2026-10-18）**。Debug 默认原生壳，`--legacy-ui` 回退 Avalonia 旧壳；Release 默认旧壳，`--native` 进入原生壳（与 Mac/iOS 同口径）。主图（EGL14/GLES + 8 位 P3/sRGB）、主屏布局（文件栏/胶片栏/控制栏/导航图/insets）、SAF 存储、硬件键盘/滚轮/长按菜单/返回、半屏设置（五分类 + 关闭回写）已落地。**A5（分析栏/相似聚类/工具页）与 A6（程序化 harness/矩阵/文档收口）未开始**。

## 启动与主屏（A1b–A4）

| 文件 | 职责 |
|---|---|
| AndroidApp.cs | 普通 `Android.App.Application`，**不初始化 Avalonia**；不得改回 `AvaloniaAndroidApplication`。 |
| AndroidBootstrapActivity.cs | 唯一 `MainLauncher` 与 `ACTION_VIEW`/`ACTION_SEND`/`ACTION_SEND_MULTIPLE`（`image/*`）入口；读 `pv-flags`（`--legacy-ui` / `--native`）后转发 Action/Data/ClipData/Extras 到目标壳并 `Finish()`。 |
| AndroidNativeActivity.cs | 原生壳 Activity（`AppCompatActivity` + `@style/Theme.PhotoViewer.Native`），不创建 Avalonia；挂 `AndroidNativeShell` 根视图，转发 SAF 结果、硬件键盘与返回键。 |
| Native/AndroidNativeShell.cs | 原生启动顺序：色域探测 → `AndroidUiDispatcher` → ReactiveUI → 门面（FilePicker/AppResources/Heif/展示解码器/预算/MemoryGovernor/StorageAccess/Xmp/Settings/设置展示器/DINO-NNAPI）→ `MainViewModel(nativePresentation: true)` → `PhotoLoadSession` → 根视图；外部打开与 `pv-open`/`pv-settings` 启动参数也在这里收口。日志必须出现 `[Native] ... Avalonia.Application.Current=null`。 |
| Native/AndroidNativeRootView.cs | 原生主屏：按共享 `MainScreenLayout`/`AndroidMainScreenLayout` 拼合文件栏（筛选条 + 胶片栏）、主图、控制栏；接线会话、栏位显隐、导航图、长按菜单、评分失败提示；物理视口上报 `PhotoLoadSession`。 |
| Native/AndroidMainScreenLayout.cs | 主屏分区纯函数：共享分区数学 + 系统栏/刘海 insets；栏位背景铺到屏幕边缘、内容收在安全区。 |
| Native/AndroidPhotoView.cs / AndroidPhotoRenderer.cs | `SurfaceView` + 自有 EGL14/GLES 主图：P3/sRGB 表面协商、RGBA8 纹理/mipmap、八方向 UV、`PhotoViewport` 手势（放大态平移、双指捏合、双击/Fit、适应态横滑）、物理 100% 触发 Full。 |
| Native/AndroidFilterBar.cs | 自绘 90×32 下拉 ×3 + 计数 + 相似聚类 toggle（A5 前隐藏）；原生 PopupMenu 写回共享筛选 VM。 |
| Native/AndroidFilmstripView.cs / AndroidThumbnailCard.cs / AndroidBitmapConverter.cs | RecyclerView 胶片栏 + 自绘卡片（96×144 / 80 方形 / 星级 / 抖动徽标 / 第二行）、350ms 三次缓出定位/用户接管/减少动态效果、可见范围上报、星星写回不切图。 |
| Native/AndroidControlBar.cs | 共享 `ControlBarLayout` 的 EXIF/评分/命令三分组、换行/居中；命令显隐/顺序来自共享设置；A5 前隐藏分析栏命令。 |
| Native/AndroidNavigationOverlay.cs / AndroidNavigationDecoder.cs | 缩放指示器（长边 150dp + 可见框 + 百分比，点按/拖动定位）；缩略图用 BitmapFactory 子采样 + 方向转正（不是 GPU 回读）。 |
| Native/AndroidNativeSettingsPresenter.cs | 半屏 Material 3 BottomSheet：五分类（文件/预览/控制/EXIF/AI）、共享 `NativeSettingsSession` 工作副本、关闭/下滑/返回统一回写。 |
| Native/AndroidInputMapping.cs | `KeyEvent`/修饰键 → 共享 `GestureKey`/`GestureModifiers`；Android 语义另写，不复用苹果修饰键表。 |
| Native/AndroidUiTheme.cs | 固定深色、显式圆角/字重/主色；uicons 图标字体从 Assets 加载。 |
| Core/AndroidPresentationDecoder.cs / AndroidPresentationEncodingPolicy.cs / AndroidDisplayCapabilities.cs | 展示解码（BitmapFactory + libheif 兜底、P3/sRGB、EXIF 方向、源尺寸）与编码协商（恒 Rgba8）、色域探测。 |
| Core/AndroidStorageRefs.cs / AndroidFilePicker.cs / AndroidStorageAccessManager.cs | SAF `content://` 与本地 `IFileRef`/`IFolderRef`；`ACTION_OPEN_DOCUMENT_TREE`/`ACTION_OPEN_DOCUMENT` + 持久授权。 |
| Core/AndroidNativeWindowPresenter.cs | 设置走原生 BottomSheet；工具页仍是 A5 占位（Toast + 日志）。 |
| MainActivity.cs | 迁移期 Avalonia 旧壳。`AvaloniaActivity` 子类；在 `base.OnCreate` **前**自建 Avalonia，`base.OnCreate` **后**写 `Content` 触发 `SetContentView`。保留存储权限（≤ API 32）与 `AndroidExternalOpenBridge` 旧路径。 |
| Core/AndroidUiDispatcher.cs / AndroidAppResourceProvider.cs / Android{Heif,LibHeif}Decoder.cs / AndroidXmpWriter.cs / AndroidSettingsStorage.cs / AndroidPerformanceBudget.cs / AndroidMemoryBudgetProvider.cs | 既有平台能力，继续复用。 |

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

## 已验证（A2–A4 自验，2026-10-18，API 35 arm64 模拟器）

- Android Debug 构建 **0 错误**；A2–A4 引入的可空性/平台版本分析警告（CS86xx/CA1416/CS0618 等）尚未清理，**不得**按“与基线一致”记通过；提交前按构建日志逐项清理。
- 默认启动 → `[Bootstrap] → native 壳`、`[Native] DINO 模型资源=ok`、`[Native] Avalonia.Application.Current=null`、`EGL 就绪`；`--legacy-ui` 旧壳仍可启动。
- 夹具：`adb push` 到 `/data/local/tmp`，再 `run-as cc.xiaojh.PhotoViewer cp` 到 `files/pv-fixtures`；启动 `--es pv-open @files/pv-fixtures`（`@files/` 解析到内部 files 目录，避免 `/sdcard/Android/data` 直读限制）。
- 13 张夹具：筛选条/12+ 卡片/控制栏/主图分区与安全区正确；方向 6 显示朝向正确；`color-p3-large`（6000×4000）Fit→100% 时代理 2706×1804 → Full 6000×4000，导航图与百分比正确。
- 卡片星级点击 → `[Rating] SetRating(..., 3)` + XMP 写回 OK，主图身份不变；设置关闭 ShowRating 后控制栏评分分组与卡片星级即时消失（关闭回写传播）。
- 硬件键盘 Right → Next、F → Fit；设置 sheet 打开时键盘让位；返回键先关 sheet 再结束 Activity。
- 3+3 邻居预取在 13 张夹具下曾 SIGSEGV；`BitmapLoader.PrefetchFullNeighbors=false`（Android）后稳定，Mac/iOS/Windows 行为不变。

## 发现清单（A1b–A4）

| # | 现象 | 处理 |
|---|---|---|
| 1 | Avalonia 12 把 `ISingleViewApplicationLifetime` / `IApplicationLifetime` 标为 `NotClientImplementable`，手写 lifetime 编译失败 | 旧壳实例化 `Avalonia.Android.ApplicationLifetime`（internal，与 `AvaloniaAndroidApplication` 同一类型）并走 `IActivityApplicationLifetime.MainViewFactory`；待上游公开 API 后移除反射 |
| 2 | `AvaloniaActivity` 的默认 `InitializeAvaloniaView` 只创建 `AvaloniaView`，不调用 `SetContentView`；在 `base.OnCreate` 前写 `Content` 会得到白屏 | 旧壳在 `base.OnCreate` **之后**写 `Content`，触发 setter 的 `SetContentView` 分支 |
| 3 | APK 同时打进 Linux glibc 的 `libonnxruntime.so`（依赖 `libdl.so.2`）与 AAR 的 Android bionic 版本，前者胜出，`libonnxruntime4j_jni.so` 启动即 `UnsatisfiedLinkError` | **已修**：csproj `_PvRemoveLinuxOnnxFromApk` 在 `_BuildApkEmbed` 前从 `FrameworkNativeLibrary` 移除 `runtimes/linux-arm64/native/*.so`；APK 现用 AAR 的 bionic 版本（md5 5316513a…），启动无 `libdl.so.2` 错误 |
| 4 | `AndroidAsset` 实际打进 `assets/Models/...`，而共享 URI 是 `avares://PhotoViewer/Assets/Models/...` | `AndroidAppResourceProvider` 去掉开头 `Assets/`；原生壳启动日志打印 `DINO 模型资源=ok` 验证 |
| 5 | SAF 选择器 / 设置页 A2–A4 已替换；工具页仍占位 | 工具页 A5 处理；占位有日志与 Toast，不冒充已实现 |
| 6 | 3+3 邻居预取同时解 Full 时 13 张夹具（含 6000×4000）启动约 0.5s SIGSEGV | 共享 `BitmapLoader.PrefetchFullNeighbors` 默认 true；Android 壳置 false，邻居统一代理档（Full 只在当前图放大时按需）。Mac/iOS/Windows 行为不变 |
| 7 | 导航图 FBO `glReadPixels` 回读内容错位/通道异常 | 改 `AndroidNavigationDecoder`（BitmapFactory 子采样 + 方向转正，长边 300dp），导航图 150dp 显示不变；登记为 Android 实现差异 |
| 8 | 模拟器为 ANGLE，EGL P3 协商回退 sRGB；真实 P3 面板未验 | 解码器按实际表面色彩空间选择 P3/sRGB；真机门补 P3 正向与色卡 |
| 9 | 数值设置用 +/- 步进、列表排序用上下按钮、拖入未接 | A4 偏差；`ItemTouchHelper` 拖拽排序与 `OnDragListener` 拖入待补，或作者裁定接受按钮排序 |
| 10 | 设置五分类页面的列表项较多（文件格式/EXIF/快捷键），需滚动；无搜索 | 与 iOS 设置同口径（滚动表单）；搜索非本轮范围 |
