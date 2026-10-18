# PhotoViewer.Android — Android 原生壳与迁移期旧壳

**当前阶段：A1a–A5 已完成（2026-10-18）**。Debug 默认原生壳，`--legacy-ui` 回退 Avalonia 旧壳；Release 默认旧壳，`--native` 进入原生壳（与 Mac/iOS 同口径）。主图（EGL14/GLES + 8 位 P3/sRGB）、主屏布局（文件栏/胶片栏/控制栏/导航图/insets）、SAF 存储、硬件键盘/滚轮/长按菜单/返回、半屏设置（五分类 + 关闭回写）已落地。**A6 部分完成**：程序化检查驱动器已落地（五模式 API 35 模拟器 PASS），矩阵/真机门/组合模式稳定性未完成。**真机（25128PNA1C / API 36）已验 P3 表面=True**；列布局/筛选菜单/长按菜单项已按作者反馈修复。

## 启动与主屏（A1b–A5）

| 文件 | 职责 |
|---|---|
| AndroidApp.cs | 普通 `Android.App.Application`，**不初始化 Avalonia**；不得改回 `AvaloniaAndroidApplication`。 |
| AndroidBootstrapActivity.cs | 唯一 `MainLauncher` 与 `ACTION_VIEW`/`ACTION_SEND`/`ACTION_SEND_MULTIPLE`（`image/*`）入口；读 `pv-flags`（`--legacy-ui` / `--native`）后转发 Action/Data/ClipData/Extras 到目标壳并 `Finish()`。 |
| AndroidNativeActivity.cs | 原生壳 Activity（`AppCompatActivity` + `@style/Theme.PhotoViewer.Native`），不创建 Avalonia；挂 `AndroidNativeShell` 根视图，转发 SAF 结果、硬件键盘与返回键。 |
| Native/AndroidNativeShell.cs | 原生启动顺序：色域探测 → `AndroidUiDispatcher` → ReactiveUI → 门面（FilePicker/AppResources/Heif/展示解码器/预算/MemoryGovernor/StorageAccess/Xmp/Settings/设置展示器/DINO-NNAPI）→ `MainViewModel(nativePresentation: true)` → `PhotoLoadSession` → 根视图；外部打开与 `pv-open`/`pv-settings` 启动参数也在这里收口。日志必须出现 `[Native] ... Avalonia.Application.Current=null`。 |
| Native/AndroidNativeRootView.cs | 原生主屏：按共享 `MainScreenLayout`/`AndroidMainScreenLayout` 拼合文件栏（筛选条 + 胶片栏）、主图、控制栏；接线会话、栏位显隐、导航图、长按菜单、评分失败提示；物理视口上报 `PhotoLoadSession`。 |
| Native/AndroidMainScreenLayout.cs | 主屏分区纯函数：共享分区数学 + 系统栏/刘海 insets；栏位背景铺到屏幕边缘、内容收在安全区。 |
| Native/AndroidPhotoView.cs / AndroidPhotoRenderer.cs | `SurfaceView` + 自有 EGL14/GLES 主图：P3/sRGB 表面协商、RGBA8 纹理/mipmap、八方向 UV、`PhotoViewport` 手势（放大态平移、双指捏合、双击/Fit、适应态横滑）、物理 100% 触发 Full。 |
| Native/AndroidFilterBar.cs | 自绘下拉 ×3 + 计数 + 相似聚类 toggle（窄屏紧凑宽度 84/76dp）；原生 PopupMenu 写回共享筛选 VM。 |
| Native/AndroidFilmstripView.cs / AndroidThumbnailCard.cs / AndroidBitmapConverter.cs | RecyclerView 胶片栏 + 自绘卡片（96×144 / 80 方形 / 星级 / 抖动徽标 / 第二行）、350ms 三次缓出定位/用户接管/减少动态效果、可见范围上报、星星写回不切图。 |
| Native/AndroidControlBar.cs | 共享 `ControlBarLayout` 的 EXIF/评分/命令三分组、换行/居中；命令显隐/顺序来自共享设置（含分析栏/增强）。 |
| Native/AndroidNavigationOverlay.cs / AndroidNavigationDecoder.cs | 缩放指示器（长边 150dp + 可见框 + 百分比，点按/拖动定位）；缩略图用 BitmapFactory 子采样 + 方向转正（不是 GPU 回读）。 |
| Native/AndroidNativeSettingsPresenter.cs | 半屏 Material 3 BottomSheet：五分类（文件/预览/控制/EXIF/AI）、共享 `NativeSettingsSession` 工作副本、关闭/下滑/返回统一回写。 |
| Native/AndroidAnalysisPanel.cs / AndroidAnalysisHost | 分析栏：共享 `AnalysisPresenter` 宿主，细节预览/直方图/DINO-CV 瓦片自绘，细节定格绿框与准星；可见时主图升 Full。 |
| Native/AndroidSimilarityPanel.cs | 相似聚类面板：复用卡片 + 分数条，三态/动作区/真实“提取全部”，行布局从胶片栏右侧让位。 |
| Native/AndroidToolsPresenter.cs | 工具 BottomSheet：EXIF 详情（分组/格式切换）与 DINO 诊断（5 瓦片 + 刚体拟合），导航状态写共享 `ToolsViewModel`。 |
| Native/AndroidInputMapping.cs | `KeyEvent`/修饰键 → 共享 `GestureKey`/`GestureModifiers`；Android 语义另写，不复用苹果修饰键表。 |
| Native/AndroidUiTheme.cs | 固定深色、显式圆角/字重/主色；uicons 图标字体从 Assets 加载。 |
| Core/AndroidPresentationDecoder.cs / AndroidPresentationEncodingPolicy.cs / AndroidDisplayCapabilities.cs | 展示解码（BitmapFactory + libheif 兜底、P3/sRGB、EXIF 方向、源尺寸）与编码协商（恒 Rgba8）、色域探测。 |
| Core/AndroidStorageRefs.cs / AndroidFilePicker.cs / AndroidStorageAccessManager.cs | SAF `content://` 与本地 `IFileRef`/`IFolderRef`；`ACTION_OPEN_DOCUMENT_TREE`/`ACTION_OPEN_DOCUMENT` + 持久授权。 |
| Core/AndroidNativeWindowPresenter.cs | 设置走原生 BottomSheet；工具页交 `AndroidToolsPresenter`。 |
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

## 已验证（A2–A5 自验，2026-10-18，API 35 arm64 模拟器）

- Android Debug 构建 **0 错误 / 20 警告**（`-t:Rebuild`；低于 A1b 基线 21；剩余均为既有 `AndroidExternalOpenBridge`/`AndroidHeifDecoder`/`AndroidSettingsStorage` 的 CS86xx/CA1422，A2–A5 新增警告已清零）。
- 默认启动 → `[Bootstrap] → native 壳`、`[Native] DINO 模型资源=ok`、`[Native] Avalonia.Application.Current=null`、`EGL 就绪`；`--legacy-ui` 旧壳仍可启动。
- 夹具：`adb push` 到 `/data/local/tmp`，再 `run-as cc.xiaojh.PhotoViewer cp` 到 `files/pv-fixtures`；启动 `--es pv-open @files/pv-fixtures`（`@files/` 解析到内部 files 目录，避免 `/sdcard/Android/data` 直读限制）。
- 13 张夹具：筛选条/12+ 卡片/控制栏/主图分区与安全区正确；方向 6 显示朝向正确；`color-p3-large`（6000×4000）Fit→100% 时代理 2706×1804 → Full 6000×4000，导航图与百分比正确。
- 卡片星级点击 → `[Rating] SetRating(..., 3)` + XMP 写回 OK，主图身份不变；设置关闭 ShowRating 后控制栏评分分组与卡片星级即时消失（关闭回写传播）。
- 硬件键盘 Right → Next、F → Fit；设置 sheet 打开时键盘让位；返回键先关 sheet 再结束 Activity。
- 3+3 邻居预取在 13 张夹具下曾 SIGSEGV；`BitmapLoader.PrefetchFullNeighbors=false`（Android）后稳定，Mac/iOS/Windows 行为不变。
- 分析栏：范围 + 细节定格绿框 + 诊断瓦片点按；相似聚类：toggle → 空态 → 真实“提取全部” → 100% 分数卡片/柱状条；工具页：EXIF 详情分组/列/格式切换、DINO 5 瓦片 + 刚体文本；增强预览像素变亮（均为目视/日志，不是程序化通过）。

## 程序化检查（A6 起步）

```bash
# 安装并准备夹具后（应用内部 files/pv-fixtures）：
adb shell am start -n cc.xiaojh.PhotoViewer/.AndroidBootstrapActivity \
  --es pv-open @files/pv-fixtures --es pv-check main
# 报告：files/PhotoViewer/android-checks-<mode>.txt
adb exec-out run-as cc.xiaojh.PhotoViewer cat files/PhotoViewer/android-checks-main.txt
```

- 模式：`main` / `analysis` / `similarity` / `settings` / `tools`；2026-10-18 API 35 arm64 分别 **PASS 14 / 18 / 15 / 16 / 16**。
- `all`（五组连跑）在该模拟器上偶发 SIGSEGV；分模式稳定，暂不作为验收口径。检查已按行/列布局分别核对分区与卡片宽度。

## 发现清单（A1b–A5）

| # | 现象 | 处理 |
|---|---|---|
| 1 | Avalonia 12 把 `ISingleViewApplicationLifetime` / `IApplicationLifetime` 标为 `NotClientImplementable`，手写 lifetime 编译失败 | 旧壳实例化 `Avalonia.Android.ApplicationLifetime`（internal，与 `AvaloniaAndroidApplication` 同一类型）并走 `IActivityApplicationLifetime.MainViewFactory`；待上游公开 API 后移除反射 |
| 2 | `AvaloniaActivity` 的默认 `InitializeAvaloniaView` 只创建 `AvaloniaView`，不调用 `SetContentView`；在 `base.OnCreate` 前写 `Content` 会得到白屏 | 旧壳在 `base.OnCreate` **之后**写 `Content`，触发 setter 的 `SetContentView` 分支 |
| 3 | APK 同时打进 Linux glibc 的 `libonnxruntime.so`（依赖 `libdl.so.2`）与 AAR 的 Android bionic 版本，前者胜出，`libonnxruntime4j_jni.so` 启动即 `UnsatisfiedLinkError` | **已修**：csproj `_PvRemoveLinuxOnnxFromApk` 在 `_BuildApkEmbed` 前从 `FrameworkNativeLibrary` 移除 `runtimes/linux-arm64/native/*.so`；APK 现用 AAR 的 bionic 版本（md5 5316513a…），启动无 `libdl.so.2` 错误 |
| 4 | `AndroidAsset` 实际打进 `assets/Models/...`，而共享 URI 是 `avares://PhotoViewer/Assets/Models/...` | `AndroidAppResourceProvider` 去掉开头 `Assets/`；原生壳启动日志打印 `DINO 模型资源=ok` 验证 |
| 5 | SAF 选择器/设置页/工具页已全部替换 | A5 工具页已落地；无占位入口 |
| 6 | 3+3 邻居预取同时解 Full 时 13 张夹具（含 6000×4000）启动约 0.5s SIGSEGV | 共享 `BitmapLoader.PrefetchFullNeighbors` 默认 true；Android 壳置 false，邻居统一代理档（Full 只在当前图放大时按需）。Mac/iOS/Windows 行为不变 |
| 7 | 导航图 FBO `glReadPixels` 回读内容错位/通道异常 | 改 `AndroidNavigationDecoder`（BitmapFactory 子采样 + 方向转正，长边 300dp），导航图 150dp 显示不变；登记为 Android 实现差异 |
| 8 | 模拟器为 ANGLE，EGL P3 协商回退 sRGB；真实 P3 面板未验 | 解码器按实际表面色彩空间选择 P3/sRGB；真机门补 P3 正向与色卡 |
| 9 | 数值设置用 +/- 步进、列表排序用上下按钮、拖入未接 | A4 偏差；`ItemTouchHelper` 拖拽排序与 `OnDragListener` 拖入待补，或作者裁定接受按钮排序 |
| 10 | 设置五分类页面的列表项较多（文件格式/EXIF/快捷键），需滚动；无搜索 | 与 iOS 设置同口径（滚动表单）；搜索非本轮范围 |
| 11 | 手机 411dp 放不下 3×90dp 筛选控件 + 计数 + 90dp 相似 toggle | 共享 `FilterBarFrames` 增加可选宽度（默认 90 不变）；Android 窄屏 84/76dp + “13 张”紧凑计数 |
| 12 | 手机分析栏瓦片约 58dp；相似空态按钮在占位下方；EXIF 表只显示 4 列；DINO 抖动线为简化绘制 | A5 偏差，登记待作者裁定/A6 视觉收口 |
| 13 | `pv-check all` 连跑五组检查偶发 SIGSEGV；分模式均 PASS | 分模式作为验收口径；矩阵/真机复测 |
| 14 | 检查矩阵只覆盖 API 35 / 竖屏 / fontScale 1.0 / density 2.625 | A6 矩阵未跑；真机门未做 |
| 15 | 真机（Xiaomi 25128PNA1C / API 36）P3 表面始终 False | 补 `android:colorMode="wideColorGamut"` + `Window.ColorMode`；关键修正是 `EGL_GL_COLORSPACE_DISPLAY_P3_EXT` 常量应为 **0x3363**（此前误用 0x30BF → EGL_BAD_ATTRIBUTE）；真机日志已 `P3表面=True` |
| 16 | 筛选下拉点击不切换 | `IMenu.Add(title)` 的 itemId 恒为 0；改为 `Add(0, index, index, title)`，卡片评分菜单同样改唯一 id 并合并单一 `MenuItemClick`（原先每个评分项各挂一个 handler，点击会串写） |
| 17 | 列布局筛选条顶部有大段“mac 红绿灯”空白 | `FileContent` 已含 `safe.Top`，`leadingInset` 又加一次 `safe.Top`；列布局只传 `FilterMargin` |
| 18 | 列布局缩略卡未居中、相似面板过宽；卡片内容偏左 | 卡片项宽行布局 96、列布局 116 并在 `OnBind` 同步；相似面板列布局交叉轴改用 `PanelWidth(106)` 而非 `ListCross(154)` |
| 19 | 主图长按菜单飞到屏幕角落/屏幕外 | 原先用 `_photo.Left/Top`（相对 photoPane，非根视图）且 PopupMenu 开到锚点上方；改为自绘 `PopupWindow`，屏幕坐标夹取、超高度可滚，返回键先关菜单 |
