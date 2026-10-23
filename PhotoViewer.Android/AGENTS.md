# PhotoViewer.Android — Android 原生壳

**当前阶段：A1a–A5 已完成（2026-10-18）**。Debug 与 Release 均只启动原生壳，无旧壳入口。主图（EGL14/GLES + 8 位 P3/sRGB）、主屏布局（文件栏/胶片栏/控制栏/导航图/insets）、SAF 存储、硬件键盘/滚轮/长按菜单/返回、半屏设置（五分类 + 关闭回写）已落地。**A6 程序化验收已完成**：八模式（五组基线 + `faults` / `layout` / `input`）在 API 35 模拟器上全 PASS，矩阵覆盖 fontScale 1.0/1.3 × density 420/525 × 竖/横屏；真机门、组合模式（`all`）与启动期偶发 SIGSEGV 未关闭。**真机（25128PNA1C / API 36）已验 P3 表面=True**；列布局/筛选菜单/长按菜单项已按作者反馈修复。

## 启动与主屏（A1b–A5）

| 文件 | 职责 |
|---|---|
| AndroidApp.cs | 普通 `Android.App.Application`，不初始化任何 UI 框架。 |
| AndroidBootstrapActivity.cs | 唯一 `MainLauncher` 与 `ACTION_VIEW`/`ACTION_SEND`/`ACTION_SEND_MULTIPLE`（`image/*`）入口；原样转发 Action/Data/ClipData/Extras 到 `AndroidNativeActivity` 并 `Finish()`。 |
| AndroidNativeActivity.cs | 原生壳 Activity（`AppCompatActivity` + `@style/Theme.PhotoViewer.Native`）；挂 `AndroidNativeShell` 根视图，转发 SAF 结果、硬件键盘与返回键；`ConfigurationChanges` 覆盖键盘/键盘隐藏/导航/触摸/方向/尺寸等，外设插拔与软键盘显隐不重建 Activity、不丢设置或工具 sheet。 |
| Native/AndroidNativeShell.cs | 原生启动顺序：色域探测 → `AndroidUiDispatcher` → ReactiveUI → 门面（FilePicker/AppResources/Heif/展示解码器/预算/MemoryGovernor/StorageAccess/Xmp/Settings/设置展示器/DINO-NNAPI）→ `MainViewModel()` → `PhotoLoadSession` → 根视图；HEIF 门面注入前先 `FastHeifDecoder.Configure`（`libpvhevc.so` + 移动端上下文/并发上限），MemoryGovernor 注入后置 `BitmapLoader.AutomaticCacheLimit`（缓存容量按实测预算推导）与 `ImageBufferPool.MaxIdleBytes = min(512MB, 预算/8)`。外部打开（含 `pv-open` 与多个 Intent Uri）合并为一次打开请求，`pv-settings` 与程序化入口（`pv-check` / `pv-stress` / `pv-perf-probe`）也在这里收口。日志出现 `[Native] Android 原生壳就绪`。 |
| Native/AndroidNativeRootView.cs | 原生主屏：按共享 `MainScreenLayout`/`AndroidMainScreenLayout` 拼合文件栏（筛选条 + 胶片栏）、主图、控制栏（溢出时沿主轴滚动并保留偏移）；筛选条在窄屏同样由可滚动宿主承载，控件宽度不压缩；接线会话、栏位显隐（SharedPreferences `PhotoViewer.Native.ShowFilm/ShowControls` 跨会话恢复）、导航图、长按菜单、评分/Full 失败/会话一次性/打开失败（STATE-02）提示条（文案取共享 `StatusMessages`，与「计算中」药丸同时可见时上移到药丸上方）；行布局按共享 `SimilarityPanelGeometry.RowWidth` 给相似面板让宽并计入列表态停靠区；加载提示绑 `PhotoLoadSession.IsLoadingVisible`（300ms 延迟），增强药丸绑 `IsEnhancingVisible`（500ms 门槛），物理视口上报 `PhotoLoadSession`，EXIF 对焦点到达后同步共享视口。 |
| Native/AndroidMainScreenLayout.cs | 主屏分区纯函数：共享分区数学 + 系统栏/刘海 insets；栏位背景铺到屏幕边缘、内容收在安全区。 |
| Native/AndroidPhotoView.cs / AndroidPhotoRenderer.cs | `SurfaceView` + 自有 EGL14/GLES 主图：P3/sRGB 表面协商、RGBA8 纹理/mipmap、八方向 UV、`PhotoViewport` 手势（放大态平移、双指捏合、双击/Fit、适应态横滑）；滚轮按精确/离散分流（触控板平移、离散滚轮累积到一格才派发命令），物理 100% 触发 Full。切图与热替换按 `PhotoLoadSession.Generation` 区分（缓存命中切图不经过空图，同机型连拍几何相同）：代际变化走切图语义，同一实例 + 同代际的会话通知只重绘不重传纹理；无图时清除纹理宽度。**纹理缓存**：当前帧 + 最近 2 帧按 `DecodedImage` 实例身份缓存（弱引用，不延长像素租约），另有 1 张 ≤32MB 的同尺寸空纹理供下次上传复用（驱动在新建纹理时可能停顿）；命中不重传、`up_hits` 记 0 ms。纹理字节含 mip 链（×4/3）上报为 `AndroidPhotoView` 的 footprint，并通过 `AndroidMemoryBudgetProvider.ExternalFootprint` 加进预算源（GPU 内存不在 RssAnon）；压力 Caution 起在 UI 线程丢掉非当前纹理与空纹理（`Trim` 由探测线程调用，只投递）。 |
| Native/AndroidFilterBar.cs | 自绘下拉 ×3 + 计数 + 相似聚类 toggle（窄屏紧凑宽度 84/76dp）；原生 PopupMenu 写回共享筛选 VM。 |
| Native/AndroidFilmstripView.cs / AndroidThumbnailCard.cs / AndroidBitmapConverter.cs | RecyclerView 胶片栏 + 自绘卡片（96×144 / 80 方形 / 星级 / 抖动徽标 / 第二行）、350ms 三次缓出定位/用户接管/减少动态效果、可见范围上报、星星写回不切图。 |
| Native/AndroidControlBar.cs | 共享 `ControlBarLayout` 的 EXIF/评分/命令三分组、换行/居中；命令显隐/顺序来自共享设置（含分析栏/增强）；评分快捷命令为实心星内叠数字（0 为清零叉），数字不拦截点击；`ExploreByTouchHelper` 暴露虚拟节点、名称、禁用态与焦点顺序。 |
| Native/AndroidControlBarScrollHost.cs | 控制栏滚动宿主（TOOLBAR-08）：文档超出可用主轴时拖动/惯性滚动，普通重建不回到起点，偏移按新几何夹取；未溢出时行为与原先一致。 |
| Native/AndroidNavigationOverlay.cs / AndroidNavigationDecoder.cs | 缩放指示器（长边 150dp + 可见框 + 百分比，宿主不透明度 0.6；白框可拖动定位，IMAGE-06）；缩略图取当前展示产物（含增强预览）抽样转正，不二次解码文件、不回读 GPU。 |
| Native/AndroidNativeSettingsPresenter.cs | 半屏 Material 3 BottomSheet：五分类（文件/预览/控制/EXIF/AI）、共享 `NativeSettingsSession` 工作副本、关闭/下滑/返回统一回写；三张可排序表用 `ItemTouchHelper` + 行尾把手（轻点把手是上移/下移菜单），顶部显示源设置的持久化失败状态。文件页「缓存与预加载」按 `ShowsCacheLimits` 决定是否显示「缓存内存上限 / 缓存数量上限」（Android 自动口径下隐藏，两项提示行保留）。控制页按 `AndroidInputDevices` 能力显示主/次绑定：有外设时提供录制（`SettingsBottomSheetDialog` 统一截获键盘/滚轮/鼠标按键，录制态左右键不穿透）与清空，纯触控整块隐藏绑定文本、冲突与录制/清空控件，读屏也不朗读绑定；`Esc` 取消、`Delete` 清空、写入共享 `HotkeyItem` 并重算冲突，录制时鼠标按键不穿透到下层控件。缩放预设每行只有一个居中数值框 + 固定 `%`，失焦/空白/Done/导航/关闭统一校验提交，保留输入框实例并原地重排；`＋` 新增 100% 项、`−` 删除项。 |
| Native/AndroidInputDevices.cs | `InputManager` 设备增删监听：真实字母键盘（排除虚拟设备与非字母键）或鼠标/触控板任一连接即视为具备键鼠录制能力；只按系统连接状态判断，供设置页动态增删控件。 |
| Native/AndroidAnalysisPanel.cs / AndroidAnalysisHost | 分析栏：共享 `AnalysisPresenter` 宿主，细节预览/直方图/DINO-CV 瓦片自绘；细节绿框按“触控按下立即点亮、滑动切换/空白清除、抬起或取消清除、真实鼠标/触控板悬停、失活/隐藏/换图清除”工作，双击定位与绿框互不影响；诊断瓦片只在未超过触控容差的完整点按时落准星；可见时主图升 Full。 |
| Native/AndroidSimilarityPanel.cs | 相似聚类面板：复用卡片 + 分数条，三态/动作区/真实“提取全部”，行布局从胶片栏右侧让位；向主屏提供列表态停靠区自然宽度，让 1–2 项列表不被动作区挤窄。 |
| Native/AndroidToolsPresenter.cs | 工具 BottomSheet：与设置页同款容器尺寸、把手/标题栏与分组卡片导航；根页只有 EXIF 详情（分组/格式切换）与 DINO 诊断（5 瓦片 + 刚体拟合），导航状态写共享 `ToolsViewModel`；显示/关闭同步 `IsPresented`，关闭即 `Deactivate()` 取消在途 DINO。 |
| Native/AndroidInputMapping.cs | `KeyEvent`/修饰键 → 共享 `GestureKey`/`GestureModifiers`；Android 语义另写，不复用苹果修饰键表。设置页录制复用同一映射生成共享 `AppGesture`，不维护第二套键码。 |
| Native/AndroidUiTheme.cs | 固定深色、显式圆角/字重/主色；uicons 描边与实心图标字体从 Assets 加载（缺失各自回退并只记录一次），设计令牌的图标码位转字形。 |
| Core/AndroidPresentationDecoder.cs / AndroidPresentationEncodingPolicy.cs / AndroidDisplayCapabilities.cs | 展示解码：**RAW 走 `AndroidRawDecoder`（LibRaw 全分辨率开发）**，不落 `BitmapFactory`（系统只会给内嵌预览，违反代理精度规则），失败返回 null 由会话如实报错；HEIF 先走共享 `FastHeifDecoder`（自解析容器 + `libpvhevc.so` 并行解 HEVC 瓦片；方向取容器 `irot`/`imir`，按 ISO/IEC 23008-12 忽略 HEIF 的 EXIF Orientation 项），其余格式与快速路径回落走 `BitmapFactory` → libheif；代理用 `InSampleSize` + 精确缩放，方向只写入 `DecodedImage.Orientation` 交顶点阶段，缩略图路径返回已转正的 sRGB8。同时实现共享 `IQuickPreviewDecoder`：RAW 首次解码空窗期用内嵌预览顶替（STATE-01），RAW 缩略图也只解内嵌预览、绝不全分辨率开发。编码协商恒 Rgba8、色域探测（P3 表面可选）。 |
| Core/AndroidRawDecoder.cs / AndroidBitmapPixels.cs | RAW 解码（`libpvraw.so` = LibRaw 0.22 薄 C ABI，见 `NativeSrc/pvraw`）：全分辨率或 half_size 开发，产物**未旋转**的 RGBA8（A=255），sRGB 或 Display P3（LibRaw `output_color` 1/7，DCI-P3 D65 原色 + sRGB 曲线），方向映射 flip→EXIF（0→1,3→3,5→8,6→6…），`SourceWidth/Height` 为全分辨率裁剪尺寸（`adjust_to_raw_inset_crop`，A7C II 7008x4672 / A6700 6192x4128）。曝光＝LibRaw 直方图 1% 白点、提升上限 2.5×（`no_auto_bright=1` + `bright`），与相机 JPEG/ImageIO 同族且逐字节等同 LibRaw mem image 路径。并发：后台（非 P0）RAW 开发串行、总并发 ≤2，P0 不与后台排队；后台另按 `MemoryGovernor.TryAcquire(EstimatePeakBytes − 产物字节)` 申请工作内存，被拒即跳过（代理返回 null 不算错误）。`EstimatePeakBytes` 由实测 RSS 拟合（2.2 B/原始像素 + 11 B/输出像素）。`AndroidBitmapPixels` 是 Bitmap→RGBA8 拷贝与「RGBA8 直缩（Mitchell，省掉 Bgra32 往返四次整图拷贝）」的共用助手。 |
| Core/AndroidLibHeifDecoder.cs / AndroidHeifDecoder.cs | `HeifLoader` 注入的 HEIF 解码器（胶片缩略图与兜底链）：目标长边 ≤512 先解容器自带的 `thmb` 项，再走 `FastHeifDecoder`，最后 `BitmapFactory` → libheif；契约不变——产物**未旋转**（`OutputPreRotated` 为 false），方向对齐由 `ThumbnailService` 按容器方向完成，DINO 的 560 短边请求仍解整幅主图。`AndroidHeifDecoder` 是其中的系统 `BitmapFactory` 一段。 |
| Core/AndroidStorageRefs.cs / AndroidFilePicker.cs / AndroidStorageAccessManager.cs | SAF `content://` 与本地 `IFileRef`/`IFolderRef`；`ACTION_OPEN_DOCUMENT_TREE`/`ACTION_OPEN_DOCUMENT` + 持久授权。 |
| Core/AndroidNativeWindowPresenter.cs | 设置走原生 BottomSheet；工具页交 `AndroidToolsPresenter`。 |
| Core/AndroidUiDispatcher.cs / AndroidAppResourceProvider.cs / AndroidXmpWriter.cs / AndroidSettingsStorage.cs / AndroidPerformanceBudget.cs / AndroidMemoryBudgetProvider.cs | 既有平台能力；`AndroidMemoryBudgetProvider` 预算按 `MemTotal` 查 AOSP MemoryLimiter 前台可见档，小米 HyperOS/MIUI 的 Sentinel PSS 红线（`persist.sys.stability.pss_highest_line`，越线即被强杀，不经 lmkd）更低时取其 90%，footprint = `RssAnon + VmSwap` + 主图挂的 `ExternalFootprint`（GL 纹理），可用量再受系统 `availMem − threshold` 收窄（Android 14+ 前台收不到 RUNNING_* trim 回调，周期探测是唯一压力源）；`AndroidPerformanceBudget` 的内存上限取该预算、原生预载默认 6 核及以上 2 路否则 1 路。设置 JSON 损坏时另存带时间戳的 SharedPreferences 备份键再以默认值启动（SETTINGS-14）。 |

## 主题与资源

- `Theme.PhotoViewer.Native` 继承 `Theme.Material3.Dark.NoActionBar`，颜色全部走 `values/colors.xml` 自有常量（`native_background` 等），**不调用 DynamicColors**、不引用 OEM 主题派生属性。bootstrap 与原生 Activity 共用该主题。
- `AndroidManifest.xml` 已移除 `MANAGE_EXTERNAL_STORAGE` / `WRITE_EXTERNAL_STORAGE`；`READ_EXTERNAL_STORAGE` 仅 `maxSdkVersion=32`（保留：API ≤32 上 `file://` 外部打开与 `--pv-open` 本地路径靠它读取）。33+ 走 SAF。未使用的 `Xamarin.AndroidX.Core.SplashScreen` 已于 2026-10-19 移除。
- 控制栏图标字体 `uicons-regular-rounded.ttf` 与实心字形 `uicons-solid-straight.ttf` 作为 `AndroidAsset` 打进 `assets/Fonts/`，与 Web/桌面共用同一字形。
- DINO 模型作为 `AndroidAsset` 打进 APK（约 +83MB），原生资源提供器按 `avares://PhotoViewer/Assets/Models/...` 路径部分读取。

## 构建（本机 2026-10-18）

默认 SDK 10.0.402 **没有** android workload；10.0.201 下已装（36.1.53）。不要改仓库 `global.json`（Apple 头需要 10.0.402 的 27 工作负载）。在任意临时目录放 `{"sdk":{"version":"10.0.201","rollForward":"disable"}}`，从该目录构建：

```bash
cd /tmp/pv-sdk201 && dotnet build /path/to/PhotoViewer.Android/PhotoViewer.Android.csproj -c Debug
```

长期建议 `sudo dotnet workload install android` 到 10.0.402。模拟器：`~/Library/Android/sdk/emulator/emulator -avd pv-api35`（Apple Silicon 必须 arm64-v8a 镜像）。

原生库（两个脚本各自交叉编译，产物入库，`NativeLibs/arm64-v8a/`）：

| 脚本 | 产物 | 说明 |
|---|---|---|
| `build-native-android-raw.sh` | `libpvraw.so` + `pvraw.txt` + `pvraw-LibRaw-LICENSE.txt` | LibRaw 0.22.2 静态编入（仅 arm64-v8a），`-O3 -fopenmp -static-openmp -static-libstdc++`、16KB 页对齐、只导出 `pv_raw_*`；LibRaw 双许可 LGPL-2.1/CDDL-1.0。脚本内校验导出符号集合、`NEEDED ⊆ libc/libdl/libm/liblog`、`LOAD` 对齐 0x4000。 |
| `build-native-android-hevc.sh` | `libpvhevc.so` + `pvhevc.txt` + `pvhevc-FFmpeg-LICENSE.txt` | 静态 FFmpeg（仅 HEVC 解码器，LGPL）+ 薄 C 封装，供 `FastHeifDecoder` 并行解瓦片。 |

两者都缺 `.so` 时对应路径自动回退（RAW 停用并记一次日志 / HEIF 回落系统与 libheif），不阻塞构建。

## 已验证（A2–A5 自验，2026-10-18，API 35 arm64 模拟器）

- Android Debug/Release 构建 **0 错误 / 9 警告**（剩余为既有 `AndroidHeifDecoder` 的 CS8603/CS8604×5、CA1422×2 与 XA4301×2，未新增）。
- 默认启动 → `[Bootstrap] → native 壳`、`[Native] Android 原生壳就绪`、`[Native] DINO 模型资源=ok`、`EGL 就绪`。
- 夹具：`adb push` 到 `/data/local/tmp`，再 `run-as cc.xiaojh.PhotoViewer cp` 到 `files/pv-fixtures`；启动 `--es pv-open @files/pv-fixtures`（`@files/` 解析到内部 files 目录，避免 `/sdcard/Android/data` 直读限制）。
- 13 张夹具：筛选条/12+ 卡片/控制栏/主图分区与安全区正确；方向 6 显示朝向正确；`color-p3-large`（6000×4000）Fit→100% 时代理 2706×1804 → Full 6000×4000，导航图与百分比正确。
- 卡片星级点击 → `[Rating] SetRating(..., 3)` + XMP 写回 OK，主图身份不变；设置关闭 ShowRating 后控制栏评分分组与卡片星级即时消失（关闭回写传播）。
- 硬件键盘 Right → Next、F → Fit；设置 sheet 打开时键盘让位；返回键先关 sheet 再结束 Activity。
- `PrefetchFullNeighbors` 为 true（只在放大或分析栏可见时预取 ±1 Full，受预算准入约束）；26MP HIF 夹具上 `pv-check analysis` 的「100% 触发 Full」受全幅解码约 1s 与检查固定 700ms 等待窗影响偶发失败，重跑即过。性能与内存实测见 [Android 性能专项](../Plans/android-performance-memory-plan-1.md)。
- 分析栏：范围 + 细节按压/悬停绿框 + 诊断瓦片点按；相似聚类：toggle → 空态 → 真实“提取全部” → 100% 分数卡片/柱状条；工具页：EXIF 详情分组/列/格式切换、DINO 5 瓦片 + 刚体文本；增强预览像素变亮（均为目视/日志，不是程序化通过）。

## 程序化检查（A6）

```bash
# 安装并准备夹具后（应用内部 files/pv-fixtures）：
adb shell am start -n cc.xiaojh.PhotoViewer/.AndroidBootstrapActivity \
  --es pv-open @files/pv-fixtures --es pv-check main
# 报告：files/PhotoViewer/android-checks-<mode>.txt
adb exec-out run-as cc.xiaojh.PhotoViewer cat files/PhotoViewer/android-checks-main.txt
```

入口统一是 `--es pv-check <mode>`：报告写 App 内部 `files/PhotoViewer/android-checks-<mode>.txt`，首行是 `PASS n / FAIL m`，其后逐条 `PASS/FAIL 名称 — 实际值`。每次运行前 `am force-stop`，避免旧进程/旧报告冒充。

**基线模式**（API 35 arm64 模拟器基线与覆盖范围）：

- `main`：主屏分区、安全区、筛选/卡片/控制栏几何、Fit↔100% 与 Full；STATE-01 缓存命中切图（运行时生成大图与小于代理目标的小图两组同尺寸 JPEG，切回时无空图快照、无加载提示、新图同步上屏）。
- `analysis`：分析栏开合与瓦片几何；运行时生成确定性 Sony 对焦 JPEG，覆盖对焦点/中心两张细节瓦片，验证按下立即点亮、两瓦片切换/回切、滑到空白/诊断清除、抬起与取消清除、真实鼠标/触控板悬停、触屏不合成悬停、触控期间悬停不抢占、双击定位独立于高亮；`owner` pointer 多指规则与旧 PointerUp/Cancel 不清新目标；诊断起手/空白起手不得滑入细节自动点亮；诊断点按落准星且滑动释放不误点；隐藏分析栏清除活动目标。
- `settings`：设置 sheet 与工作副本；`AndroidInputDevices` 能力注入（键盘/指针/纯触控），读取真实行视图断言完整绑定控件、纯触控零绑定文字与读屏不泄露；录制/取消/清空/冲突、录制时鼠标左右键被吞、断开最后外设取消录制、连接变化保留草稿且不弹软键盘、虚拟/非字母键盘排除；倍率行单一居中值 + `%`、失焦统一提交、1601/NaN/0 恢复、输入框实例保持、草稿随增删保留、原地重排与至少保留一项。
- `similarity` / `tools`：面板与工具 sheet 开合。

**独立模式**（精确模式名匹配，不并入 `all`；自带素材、每步独立 try/catch，失败记 FAIL 后继续，例外与超时都写进报告）：

- `faults`：此前只能手验/代码核对的失败与边界项——IMAGE-08 八方向（运行时生成 320×200 + EXIF 0x0112 = 1..8 的 8 张 JPEG，断言解码方向、视口方向与 5–8 的逻辑宽高互换）、SCOPE-04 RAW 全分辨率（`files/pv-check-raw/*.ARW`，源 ≥6000×4000，放大到物理 100% 后展示尺寸 = 源尺寸）、TOOLBAR-03 外部存储评分写回（`GetExternalFilesDir` 下 800×600 JPEG，XMP 字节回读 = 4）、IMAGE-03 Full 失败（反射换 `BitmapLoader._presentationDecoder` 为「仅全分辨率失败」替身，断言保留代理 + 提示条含「全分辨率」）、STATE-02/OPEN-03（`ParentFailingFileRef` + `FailingFolderRef`：文案含目录名、回退单图、列表只剩 1 项）、STATE-03（只读副本 444/555，星级不变 + 提示条含文件名）、SETTINGS-14（隔离档 `checks` 写入损坏 JSON，断言 `.corrupt-` 备份键存在、状态行含「损坏」）、SIM-10（反射给 `SimilarItems`/`UnindexedCount`/`IndexError` 赋值，断言列表态停靠区错误标签）、ANALYSIS-10（覆盖 DINO 模型字节并卸载会话，断言 4 张诊断瓦片全部「分析失败」且无残留图）、SCOPE-02（`session.Trim(Warning)` 关闭增强 + 一次性提示）、OPEN-06（`ACTION_VIEW` 外部打开落到同一 `PathKey`）。素材只写 App 内部目录与 `GetExternalFilesDir`；RAW 夹具先 `adb push` 再 `run-as cp` 到 `files/pv-check-raw/`。
- `layout`：记录当前 `fontScale`/density/窗口尺寸/方向/布局模式，然后在竖屏、横屏（`RequestedOrientation` 切向，Activity 不重建）、回竖屏三态各核对一遍：分区非负且在屏内、相邻分区边界相接（列布局控制栏背景从分隔线之后起排）、筛选条在文件栏内容区、控制栏三分组 + 虚拟节点矩形（整条文档超出可视主轴时按 TOOLBAR-08 只要求交叉轴在界内，主轴允许滚动）、首卡 96×144（行）/116×144（列）。字体档与密度不自己改：外部用 `adb shell settings put system font_scale` / `wm density` 改档后重启再跑。
- `input`：INPUT-04 合成滚动事件（触控板源 0.4 → 只平移且不派发滚轮命令；鼠标源 1.0 → 恰好一次命令）分流、INPUT-05 按键重复（`DpadRight` 重复翻页；`Numpad3` 重复只执行一次、非重复写入 3 星；主行 `Num3` 的评分绑定）、TOOLBAR-11 控制栏虚拟节点（名称非空、评分按行 0→5 / 列 5→0、命令名与 `Commands` 对齐、矩形非零）与硬件键盘命令（`F` 切适应、`DpadRight` 翻页）。

**已跑矩阵**（API 35 arm64 模拟器，2026-10-23）：fontScale 1.0 / 1.3 × density 420 / 525（411dp / 329dp 宽）× 竖屏 / 横屏 / 回竖屏。报告计数：`faults` 20/0、`layout` 18/0（420 两档字体）、`layout` 18/0（525 档，筛选条按窄屏横向滚动）、`input` 8/0、`main` 18/0、`analysis` 47/0、`similarity` 19/0、`settings` 51/0、`tools` 20/0。验收读取用的访问器（`StatusTextForChecks`、`StatusVisibleForChecks`、`ResetStatusForChecks`、`ControlBar.NodesForChecks`、`SimilarityPane.DockError*ForChecks`、`FilterScrollableForChecks`、`FilterScrollOffsetForChecks`）都是 `internal` 且注释标「验收用」，产品路径不引用；`faults` 另用反射改 `BitmapLoader` 私有静态解码器字段与 `SimilarityPanelViewModel` 私有 setter，退出前一律还原。

`all`（五组连跑）与个别独立模式的**启动期偶发 SIGSEGV 未关闭**：实测约每 8 次启动有 1 次在「刚打开文件夹、图片刚上屏」的 200 ms 内以 signal 11 退出（无托管异常、无 tombstone）。独立模式已加 `[NativeCheck] 独立模式开始/<步骤>` 日志，崩溃可定位到步骤之前还是步骤之内；分模式（尤其 `faults` / `layout` / `input`）仍优于 `all`，但不作为「必过」验收口径。

### 压测与性能探针

入口在 `AndroidNativeShell`：Intent extras 映射到共享 `StressFlipDriver` / `AndroidPerfProbe`，报告与 pv-check 同目录（内部 `files/PhotoViewer/`）。素材只读，不写回照片文件夹。

| Extra | 作用 |
|---|---|
| `pv-stress=<文件夹>`（`@files/` 解析同 `pv-open`） | 顺序翻页压测，写 `stress-flip.jsonl` |
| `pv-stress-count=N` | 翻页上限；缺省翻完整个文件夹 |
| `pv-stress-mode=ab` | 最近两张对比切换（缺省 sequential） |
| `pv-perf-probe-target=N`（`--ei`） | 探针代理长边；缺省为屏幕物理长边 × 2，实际主图代理长边见压测 `up_last` |
| `pv-memory-probe` | 内存上限探针：前台按 128MB 逐步分配并逐页写入，每步把占用与系统水位写入 `memory-ceiling.txt`，到 14GB 或分配失败停止（会挤掉后台应用，只在验收时用） |
| `pv-memory-probe-report` | 把系统记录的最近 5 次进程退出原因写入 `exit-reasons.txt` 后结束进程 |
| `pv-settings-page=<files\|preview\|control\|exif\|ai>` | 与 `pv-settings` 合用，设置 sheet 直接落到指定分类页（真机禁用输入注入时核对页面内容） |
| `pv-perf-probe=<文件夹>` | 分段计时探针，独占一轮，写 `perf-probe.txt` 后结束进程 |

```bash
# 探针：HIF/ARW/JPG 各取前 6 张（首张预热），代理长边 = 屏幕物理长边 × 2
adb shell am start -n cc.xiaojh.PhotoViewer/.AndroidBootstrapActivity \
  --es pv-perf-probe @files/pv-perf
adb exec-out run-as cc.xiaojh.PhotoViewer cat files/PhotoViewer/perf-probe.txt

# 压测：顺序 60 翻 / 对比 60 翻
adb shell am start -n cc.xiaojh.PhotoViewer/.AndroidBootstrapActivity \
  --es pv-stress @files/pv-stress-hif --ei pv-stress-count 60 --es pv-stress-mode ab
adb exec-out run-as cc.xiaojh.PhotoViewer cat files/PhotoViewer/stress-flip.jsonl
```

- 压测切到隔离设置档 `cc.xiaojh.PhotoViewer.Settings.stress`，每轮从默认值起跑且不覆盖用户设置；`done` 行 `extra` 含 GL 上传统计（`up_*`）、`/proc/self/status` 的 `rssAnonMB`/`swapMB`/`peakRssMB` 与 GC 次数。
- 压测与探针结束都结束进程（对齐 Windows 的 `Environment.Exit`），下一轮从干净进程起跑。
- 探针解码走壳注入的展示解码器实例（换 HEIF/RAW 解码器后无需改探针），GL 上传无 EGL 上下文时不测；解码返回 null 记作 `null result`，不吞成 0 ms。
- Release 包非 debuggable，`run-as` 不可用：模拟器 `adb root` 后 `adb pull /data/data/cc.xiaojh.PhotoViewer/files/PhotoViewer/` 取报告。真机（不可 root）验收要可调试的 Release：复制 `Properties/AndroidManifest.xml` 加 `android:debuggable="true"`，以相对 csproj 的路径传 `/p:AndroidManifest=…`（绝对路径会报 XA1018），签名参数同 `publish-android-apk.sh`。
- 真机素材推到 `/sdcard/Android/data/cc.xiaojh.PhotoViewer/files/<目录>`，以 `@external/<目录>` 打开；`adb push` 建的目录是 `shell:ext_data_rw 2770`，应用无权遍历，推完须 `adb shell chmod -R 777 <目录>`。
- 小米 HyperOS 禁止 `input` 注入与 `svc power stayon`，触控类验收只能靠程序化检查与 `screencap`。

## 发现清单（A1b–A5）

| # | 现象 | 处理 |
|---|---|---|
| 3 | APK 同时打进 Linux glibc 的 `libonnxruntime.so`（依赖 `libdl.so.2`）与 AAR 的 Android bionic 版本，前者胜出，`libonnxruntime4j_jni.so` 启动即 `UnsatisfiedLinkError` | **已修**：csproj `_PvRemoveLinuxOnnxFromApk` 在 `_BuildApkEmbed` 前从 `FrameworkNativeLibrary` 移除 `runtimes/linux-arm64/native/*.so`；APK 现用 AAR 的 bionic 版本（md5 5316513a…），启动无 `libdl.so.2` 错误 |
| 4 | `AndroidAsset` 实际打进 `assets/Models/...`，而共享 URI 是 `avares://PhotoViewer/Assets/Models/...` | `AndroidAppResourceProvider` 去掉开头 `Assets/`；原生壳启动日志打印 `DINO 模型资源=ok` 验证 |
| 5 | SAF 选择器/设置页/工具页已全部替换 | A5 工具页已落地；无占位入口 |
| 6 | 3+3 邻居预取同时解 Full 时 13 张夹具（含 6000×4000）启动约 0.5s SIGSEGV | 预取档位改为跟随实际需求（适应态只取代理，放大或分析栏可见时只取 ±1 邻居 Full）并加预算准入后，Android 重新启用 `PrefetchFullNeighbors`（true）；`pv-check analysis/main` 各 3 次 + 26MP HIF 夹具 analysis 2 次全 PASS、无 SIGSEGV。Mac/iOS/Windows 不受影响 |
| 7 | 导航图 FBO `glReadPixels` 回读内容错位/通道异常 | 改为不依赖 GPU 回读：当前取展示产物（代理/Full/增强）抽样转正生成导航图，长边仍为 150dp 显示 |
| 8 | 模拟器为 ANGLE，EGL P3 协商回退 sRGB；真实 P3 面板未验 | 解码器按实际表面色彩空间选择 P3/sRGB；真机门补 P3 正向与色卡 |
| 9 | 数值设置用 +/- 步进、列表排序曾用上下按钮、拖入未接 | 三张表已改 `ItemTouchHelper` 拖拽 + 行尾把手，轻点把手提供上移/下移菜单（SETTINGS-13/15）；`OnDragListener` 拖入仍待补 |
| 10 | 设置五分类页面的列表项较多（文件格式/EXIF/快捷键），需滚动；无搜索 | 与 iOS 设置同口径（滚动表单）；搜索非本轮范围 |
| 11 | 手机 411dp 放不下 3×90dp 筛选控件 + 计数 + 90dp 相似 toggle | 共享 `FilterBarFrames` 增加可选宽度（默认 90 不变）；Android 窄屏 84/76dp + “13 张”紧凑计数 |
| 12 | 手机分析栏瓦片约 58dp；相似空态按钮在占位下方；EXIF 表只显示 4 列；DINO 抖动线为简化绘制 | A5 偏差，登记待作者裁定/A6 视觉收口 |
| 13 | `pv-check all` 连跑五组检查偶发 SIGSEGV；分模式也有约 1/8 的启动在「刚开文件夹、图片刚上屏」200 ms 内 signal 11 退出（无托管异常、无 tombstone）。2026-10-19 重装后首跑 analysis 偶发 1 项「回到适应态 fit=False」，随后连跑 3 次 18/18 | 分模式（尤其 `faults`/`layout`/`input`）为验收口径，但不按「必过」要求；独立模式已加步骤日志，便于把崩溃定位到步骤边界；崩溃根因待查（与检查代码无关，发生在 `[NativeCheck] 独立模式开始` 之后、首步之前） |
| 14 | 检查矩阵已覆盖 API 35 arm64 / fontScale 1.0·1.3 / density 420·525（411dp·329dp 宽）/ 竖屏·横屏·回竖屏；真机矩阵未跑 | 矩阵结果见「程序化检查」段；真机门仍待做 |
| 15 | 真机（Xiaomi 25128PNA1C / API 36）P3 表面始终 False | 补 `android:colorMode="wideColorGamut"` + `Window.ColorMode`；关键修正是 `EGL_GL_COLORSPACE_DISPLAY_P3_EXT` 常量应为 **0x3363**（此前误用 0x30BF → EGL_BAD_ATTRIBUTE）；真机日志已 `P3表面=True` |
| 16 | 筛选下拉点击不切换 | `IMenu.Add(title)` 的 itemId 恒为 0；改为 `Add(0, index, index, title)`，卡片评分菜单同样改唯一 id 并合并单一 `MenuItemClick`（原先每个评分项各挂一个 handler，点击会串写） |
| 17 | 列布局筛选条顶部有大段“mac 红绿灯”空白 | `FileContent` 已含 `safe.Top`，`leadingInset` 又加一次 `safe.Top`；列布局只传 `FilterMargin` |
| 18 | 列布局缩略卡未居中、相似面板过宽；卡片内容偏左 | 卡片项宽行布局 96、列布局 116 并在 `OnBind` 同步；相似面板列布局交叉轴改用 `PanelWidth(106)` 而非 `ListCross(154)` |
| 19 | 主图长按菜单飞到屏幕角落/屏幕外 | 原先用 `_photo.Left/Top`（相对 photoPane，非根视图）且 PopupMenu 开到锚点上方；改为自绘 `PopupWindow`，屏幕坐标夹取、超高度可滚，返回键先关菜单 |
| 20 | 自绘长按菜单无分隔线、首行顶距小、底部空 | 行高 44dp、内容上下 4dp 对称 padding、项间 1px `Divider`，文字 `CenterVertical`；菜单仍夹在屏幕内 |
| 21 | 筛选下拉位置无规律 | 改为自绘 `PopupWindow`，按被点下拉框 frame 精确落在框下方 2dp；下方放不下翻到框上方，返回键先关 |
| 22 | 缩略图卡片图片仍偏左 | 图片/第二行从 5..85 改 8..88（80dp 在 96dp 内容内居中），星级 6 颗整体居中，日期中心 48dp |
| 23 | P3 测试图只有 Adobe 略灰，P3/sRGB 看起来一样 | P3 表面已 True；但测试图是同一组 RGB 分别带 Adobe/P3/sRGB ICC，正确管理下 P3 与 sRGB 应不同。当前 `BitmapFactory` 的 ICC→P3 转换仍可疑，下一步用共享 `ColorEngine`/Skia skcms 接管；真机锁屏暂未取到解码色彩日志 |
| 24 | 缩略图图片不偏左但偏上 | 图片/第二行改 8..88，标题基线 103、日期 115、星级 126，使上边距与左右 8dp 一致 |
| 25 | 开关胶片栏/控制栏后主图严重偏移 | 两因：SurfaceView 新缓冲晚一帧（旧尺寸帧残留）+ 视口不保留中心。`OnSizeChanged` 保留 `CenterUV` 并 Post/PostDelayed 重绘；SurfaceChanged 也补两帧 |
| 26 | 分析栏细节绿框随机乱飞、小图也出框、双击位置怪 | 按 iOS 语义重做：按下/真实悬停点亮瓦片中心（CenterU/V，不取手指落点）、滑动切换或清除、抬起/取消/失活/切图/关闭分析栏清除；双击定位到瓦片中心；绿框大小 = 对焦框或 1:1 裁剪像素×视口 Scale；适应态已大于物理 100% 的小图不画绿框；`FocusOnPoint` 小图夹回适应 |
| 27 | 长按菜单每个项之间都有分隔线 | 按 iOS 分组：打开文件夹｜胶片栏/分析栏/控制栏｜增强预览｜工具/设置；顺序也改为 胶片/分析/控制 |
| 28 | 初次打开文件夹控制栏只剩容器 | 空分组兜底重建 + 文件/CurrentFile 补 MarkDirty + 文件夹 UI 更新 120ms 后再重建；SAF 选择器返回时 Activity `OnResume` 再强制刷新控制栏与主图一帧 |
| 29 | 主图色彩整体发白，任何格式 | 不是 ICC：真机 EGL P3 表面把主图整体抬亮（同一 JPEG 强制 sRGB 表面后正常，缩略图也正常）。默认改为 sRGB 表面/解码（`ForceSrgb`），广色域保留 `--ez pv-p3 true` 诊断；待确认 P3 EGL/合成传递函数后再默认开启 |
| 30 | 控制栏偶现“只剩容器” | 真正根因：`OnDraw` 在 dirty/空分组时只 `Rebuild`，新分组 `Rect` 还是 0，绘制循环全部 `continue`。现改为在 `OnDraw` 内调用 `Arrange`（重建并算出矩形）再绘制；配合 SAF 返回刷新 |
| 31 | 胶片栏图片被拉成 80 方形 | 卡片绘制改为按位图比例等比装入 80×80 并居中，非方形预览不再拉伸 |
| 32 | HIF 导航指示器无缩略图 | 导航图改取当前展示产物（HIF 主图由 libheif 解出），不再单独走 BitmapFactory；真实 HIF `A7C05520.HIF` 已实测导航图出图 |
| 33 | 同名 HIF/ARW 优先级 | 真实 `same.hif + same.arw` 实测：卡片 `same.hif(RAW)`、主图 HIF；`same.jpg + same.hif` 实测代表与主图均为 JPG。小组代表仍按 `SelectedFormats` 顺序 |
| 34 | 同名合并后主图可能是被隐藏文件 | 文件夹打开时 CurrentFile 可能先指向 ARW/HIF，卡片却显示代表；`ApplyFilter` 末尾把隐藏的 CurrentFile 映射回其代表，实测 JPG/HIF/ARW 主图与卡片一致 |
| 35 | HIF 缩略图/指示器等待久 | Sony HIF 的 `thmb` item 用 libheif thumbnail 插件解不了；但容器里有独立的 JPEG item（实测 160×120）。新增 `HeifJpegThumbnailExtractor` 解析 iinf/iloc 提取 JPEG item，胶片/导航优先用它，跳过 7008×4672 全图；小尺寸请求现由共享快速路径直接解容器 `thmb` 项（≤512 长边），不再保留 512 代理缓存。主图 Full 走 `pvhevc` 全图解码，首张小图不等全图 |
| 38 | HIF 同一文件缩略图横竖不一致 | 同一 HIF 内不同 thumbnail item 方向不一致：160×120 是传感器横向，373×560 已是显示竖图；统一按容器 270° 转会把后者转横。改为先比较“原始横竖”和“显示横竖”，相反才转。同进程先开 A670 文件夹再开 A7C09124，首卡已与主图同向 |
| 37 | HIF 首张缩略图方向偶发错 | 首张走优先加载时 MetadataExtractor 可能还没给出 Default Rotation；新增 `HeifOrientationReader` 直接从文件头 irot 读角度，元数据为 0 时兜底。用 `/sdcard/PhotoViewerTest/12760425/A7C09124.HIF`（irot=1→270°）验证首卡与主图同向 |
| 36 | HIF 胶片缩略图横竖错乱 | JPEG 缩略图 item 存储方向与主图不同。`ThumbnailService` 之前把 HEIF 平台源硬编码 `isPreRotated:true`；改为 `HeifLoader.OutputPreRotated`，Android libheif/JPEG item 路径按容器 Default Rotation 再做方向对齐。用改 `irot=90°` 的 HIF 验证主图与卡片同向 |

| 39 | 主行数字键 1–5 在 Android 上不触发评分快捷键（默认绑定 `D1`–`D5`）：`AndroidInputMapping.MapKey` 的数字兜底只把 1 字符名补成 `D<n>`，而键码枚举是 `Num0`–`Num9`，`Enum.TryParse<GestureKey>("Num3")` 解析失败返回 null → `HandleHardwareKey(KEYCODE_3)` 返回 false。小键盘 `Numpad3` 经 `NumPad` 前缀改写可正常触发（次绑定） | **已修**：`Num0`–`Num9` 映射为 `D0`–`D9`；`pv-check input`「INPUT-05 主行数字键映射」PASS |
| 40 | 窄屏（density 525 → 329dp 宽）下筛选条溢出：自然最小宽约 389dp（3×84 + 计数 + 76 toggle + 间距），相似聚类 toggle 落在 313..389，超出 329dp 内容区约 60dp 被屏幕裁掉；行布局无横向滚动 | **已修**：筛选条复用可滚动宿主（TOOLBAR-08 同一实现），控件宽度不压缩，窄屏横向滚动；density 525 的 `pv-check layout` 18/18 PASS |
| 41 | `INPUT-04` 的「离散事件累积到一格才派发」分支实际不可达：非整数轴增量一律先被判为精确滚动（`IsPreciseScroll`），两次 0.5 只平移不派发命令；与 Windows「增量小于一格即精确」同口径 | **已按实际口径修正**：`Design/spec/input.md` 改为「高分辨率（非整格）增量按精确滚动平移，不累积成命令；整格按格计数」；`pv-check input` 改为断言非整格平移 + 整格一次命令，PASS |