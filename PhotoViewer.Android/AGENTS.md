# PhotoViewer.Android — Android 原生壳

**当前阶段：A1a–A5 已完成（2026-10-18）**。Debug 与 Release 均只启动原生壳，无旧壳入口。主图（EGL14/GLES + 8 位 P3/sRGB）、主屏布局（文件栏/胶片栏/控制栏/导航图/insets）、SAF 存储、硬件键盘/滚轮/长按菜单/返回、半屏设置（五分类 + 关闭回写）已落地。**A6 部分完成**：程序化检查驱动器已落地（五模式 API 35 模拟器 PASS），矩阵/真机门/组合模式稳定性未完成。**真机（25128PNA1C / API 36）已验 P3 表面=True**；列布局/筛选菜单/长按菜单项已按作者反馈修复。

## 启动与主屏（A1b–A5）

| 文件 | 职责 |
|---|---|
| AndroidApp.cs | 普通 `Android.App.Application`，不初始化任何 UI 框架。 |
| AndroidBootstrapActivity.cs | 唯一 `MainLauncher` 与 `ACTION_VIEW`/`ACTION_SEND`/`ACTION_SEND_MULTIPLE`（`image/*`）入口；原样转发 Action/Data/ClipData/Extras 到 `AndroidNativeActivity` 并 `Finish()`。 |
| AndroidNativeActivity.cs | 原生壳 Activity（`AppCompatActivity` + `@style/Theme.PhotoViewer.Native`）；挂 `AndroidNativeShell` 根视图，转发 SAF 结果、硬件键盘与返回键。 |
| Native/AndroidNativeShell.cs | 原生启动顺序：色域探测 → `AndroidUiDispatcher` → ReactiveUI → 门面（FilePicker/AppResources/Heif/展示解码器/预算/MemoryGovernor/StorageAccess/Xmp/Settings/设置展示器/DINO-NNAPI）→ `MainViewModel(nativePresentation: true)` → `PhotoLoadSession` → 根视图；外部打开（含 `pv-open` 与多个 Intent Uri）合并为一次打开请求，启动参数 `pv-settings` 也在这里收口。日志出现 `[Native] Android 原生壳就绪`。 |
| Native/AndroidNativeRootView.cs | 原生主屏：按共享 `MainScreenLayout`/`AndroidMainScreenLayout` 拼合文件栏（筛选条 + 胶片栏）、主图、控制栏（溢出时沿主轴滚动并保留偏移）；接线会话、栏位显隐（SharedPreferences `PhotoViewer.Native.ShowFilm/ShowControls` 跨会话恢复）、导航图、长按菜单、评分/Full 失败/会话一次性提示条；加载提示绑 `PhotoLoadSession.IsLoadingVisible`（300ms 延迟），增强药丸绑 `IsEnhancingVisible`（500ms 门槛），物理视口上报 `PhotoLoadSession`，EXIF 对焦点到达后同步共享视口。 |
| Native/AndroidMainScreenLayout.cs | 主屏分区纯函数：共享分区数学 + 系统栏/刘海 insets；栏位背景铺到屏幕边缘、内容收在安全区。 |
| Native/AndroidPhotoView.cs / AndroidPhotoRenderer.cs | `SurfaceView` + 自有 EGL14/GLES 主图：P3/sRGB 表面协商、RGBA8 纹理/mipmap、八方向 UV、`PhotoViewport` 手势（放大态平移、双指捏合、双击/Fit、适应态横滑）；滚轮按精确/离散分流（触控板平移、离散滚轮累积到一格才派发命令），物理 100% 触发 Full。 |
| Native/AndroidFilterBar.cs | 自绘下拉 ×3 + 计数 + 相似聚类 toggle（窄屏紧凑宽度 84/76dp）；原生 PopupMenu 写回共享筛选 VM。 |
| Native/AndroidFilmstripView.cs / AndroidThumbnailCard.cs / AndroidBitmapConverter.cs | RecyclerView 胶片栏 + 自绘卡片（96×144 / 80 方形 / 星级 / 抖动徽标 / 第二行）、350ms 三次缓出定位/用户接管/减少动态效果、可见范围上报、星星写回不切图。 |
| Native/AndroidControlBar.cs | 共享 `ControlBarLayout` 的 EXIF/评分/命令三分组、换行/居中；命令显隐/顺序来自共享设置（含分析栏/增强）；`ExploreByTouchHelper` 暴露虚拟节点、名称、禁用态与焦点顺序。 |
| Native/AndroidControlBarScrollHost.cs | 控制栏滚动宿主（TOOLBAR-08）：文档超出可用主轴时拖动/惯性滚动，普通重建不回到起点，偏移按新几何夹取；未溢出时行为与原先一致。 |
| Native/AndroidNavigationOverlay.cs / AndroidNavigationDecoder.cs | 缩放指示器（长边 150dp + 可见框 + 百分比，宿主不透明度 0.6，点按/拖动定位）；缩略图取当前展示产物（含增强预览）抽样转正，不二次解码文件、不回读 GPU。 |
| Native/AndroidNativeSettingsPresenter.cs | 半屏 Material 3 BottomSheet：五分类（文件/预览/控制/EXIF/AI）、共享 `NativeSettingsSession` 工作副本、关闭/下滑/返回统一回写；三张可排序表用 `ItemTouchHelper` + 行尾把手（轻点把手是上移/下移菜单），顶部显示源设置的持久化失败状态。 |
| Native/AndroidAnalysisPanel.cs / AndroidAnalysisHost | 分析栏：共享 `AnalysisPresenter` 宿主，细节预览/直方图/DINO-CV 瓦片自绘，细节定格绿框与准星；可见时主图升 Full。 |
| Native/AndroidSimilarityPanel.cs | 相似聚类面板：复用卡片 + 分数条，三态/动作区/真实“提取全部”，行布局从胶片栏右侧让位。 |
| Native/AndroidToolsPresenter.cs | 工具 BottomSheet：EXIF 详情（分组/格式切换）与 DINO 诊断（5 瓦片 + 刚体拟合），导航状态写共享 `ToolsViewModel`；显示/关闭同步 `IsPresented`，关闭即 `Deactivate()` 取消在途 DINO。 |
| Native/AndroidInputMapping.cs | `KeyEvent`/修饰键 → 共享 `GestureKey`/`GestureModifiers`；Android 语义另写，不复用苹果修饰键表。 |
| Native/AndroidUiTheme.cs | 固定深色、显式圆角/字重/主色；uicons 图标字体从 Assets 加载。 |
| Core/AndroidPresentationDecoder.cs / AndroidPresentationEncodingPolicy.cs / AndroidDisplayCapabilities.cs | 展示解码（BitmapFactory + libheif 兜底、P3/sRGB、EXIF 方向、源尺寸）与编码协商（恒 Rgba8）、色域探测。 |
| Core/AndroidStorageRefs.cs / AndroidFilePicker.cs / AndroidStorageAccessManager.cs | SAF `content://` 与本地 `IFileRef`/`IFolderRef`；`ACTION_OPEN_DOCUMENT_TREE`/`ACTION_OPEN_DOCUMENT` + 持久授权。 |
| Core/AndroidNativeWindowPresenter.cs | 设置走原生 BottomSheet；工具页交 `AndroidToolsPresenter`。 |
| Core/AndroidUiDispatcher.cs / AndroidAppResourceProvider.cs / Android{Heif,LibHeif}Decoder.cs / AndroidXmpWriter.cs / AndroidSettingsStorage.cs / AndroidPerformanceBudget.cs / AndroidMemoryBudgetProvider.cs | 既有平台能力，继续复用；设置 JSON 损坏时另存带时间戳的 SharedPreferences 备份键再以默认值启动（SETTINGS-14）。 |

## 主题与资源

- `Theme.PhotoViewer.Native` 继承 `Theme.Material3.Dark.NoActionBar`，颜色全部走 `values/colors.xml` 自有常量（`native_background` 等），**不调用 DynamicColors**、不引用 OEM 主题派生属性。bootstrap 与原生 Activity 共用该主题。
- `AndroidManifest.xml` 已移除 `MANAGE_EXTERNAL_STORAGE` / `WRITE_EXTERNAL_STORAGE`；`READ_EXTERNAL_STORAGE` 仅 `maxSdkVersion=32`。33+ 走 SAF。
- DINO 模型作为 `AndroidAsset` 打进 APK（约 +83MB），原生资源提供器按 `avares://PhotoViewer/Assets/Models/...` 路径部分读取。

## 构建（本机 2026-10-18）

默认 SDK 10.0.402 **没有** android workload；10.0.201 下已装（36.1.53）。不要改仓库 `global.json`（Apple 头需要 10.0.402 的 27 工作负载）。在任意临时目录放 `{"sdk":{"version":"10.0.201","rollForward":"disable"}}`，从该目录构建：

```bash
cd /tmp/pv-sdk201 && dotnet build /path/to/PhotoViewer.Android/PhotoViewer.Android.csproj -c Debug
```

长期建议 `sudo dotnet workload install android` 到 10.0.402。模拟器：`~/Library/Android/sdk/emulator/emulator -avd pv-api35`（Apple Silicon 必须 arm64-v8a 镜像）。

## 已验证（A2–A5 自验，2026-10-18，API 35 arm64 模拟器）

- Android Debug/Release 构建 **0 错误 / 9 警告**（剩余为既有 `AndroidHeifDecoder` 的 CS8603/CS8604×5、CA1422×2 与 XA4301×2，未新增）。
- 默认启动 → `[Bootstrap] → native 壳`、`[Native] Android 原生壳就绪`、`[Native] DINO 模型资源=ok`、`EGL 就绪`。
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
| 3 | APK 同时打进 Linux glibc 的 `libonnxruntime.so`（依赖 `libdl.so.2`）与 AAR 的 Android bionic 版本，前者胜出，`libonnxruntime4j_jni.so` 启动即 `UnsatisfiedLinkError` | **已修**：csproj `_PvRemoveLinuxOnnxFromApk` 在 `_BuildApkEmbed` 前从 `FrameworkNativeLibrary` 移除 `runtimes/linux-arm64/native/*.so`；APK 现用 AAR 的 bionic 版本（md5 5316513a…），启动无 `libdl.so.2` 错误 |
| 4 | `AndroidAsset` 实际打进 `assets/Models/...`，而共享 URI 是 `avares://PhotoViewer/Assets/Models/...` | `AndroidAppResourceProvider` 去掉开头 `Assets/`；原生壳启动日志打印 `DINO 模型资源=ok` 验证 |
| 5 | SAF 选择器/设置页/工具页已全部替换 | A5 工具页已落地；无占位入口 |
| 6 | 3+3 邻居预取同时解 Full 时 13 张夹具（含 6000×4000）启动约 0.5s SIGSEGV | 共享 `BitmapLoader.PrefetchFullNeighbors` 默认 true；Android 壳置 false，邻居统一代理档（Full 只在当前图放大时按需）。Mac/iOS/Windows 行为不变 |
| 7 | 导航图 FBO `glReadPixels` 回读内容错位/通道异常 | 改为不依赖 GPU 回读：当前取展示产物（代理/Full/增强）抽样转正生成导航图，长边仍为 150dp 显示 |
| 8 | 模拟器为 ANGLE，EGL P3 协商回退 sRGB；真实 P3 面板未验 | 解码器按实际表面色彩空间选择 P3/sRGB；真机门补 P3 正向与色卡 |
| 9 | 数值设置用 +/- 步进、列表排序曾用上下按钮、拖入未接 | 三张表已改 `ItemTouchHelper` 拖拽 + 行尾把手，轻点把手提供上移/下移菜单（SETTINGS-13/15）；`OnDragListener` 拖入仍待补 |
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
| 20 | 自绘长按菜单无分隔线、首行顶距小、底部空 | 行高 44dp、内容上下 4dp 对称 padding、项间 1px `Divider`，文字 `CenterVertical`；菜单仍夹在屏幕内 |
| 21 | 筛选下拉位置无规律 | 改为自绘 `PopupWindow`，按被点下拉框 frame 精确落在框下方 2dp；下方放不下翻到框上方，返回键先关 |
| 22 | 缩略图卡片图片仍偏左 | 图片/第二行从 5..85 改 8..88（80dp 在 96dp 内容内居中），星级 6 颗整体居中，日期中心 48dp |
| 23 | P3 测试图只有 Adobe 略灰，P3/sRGB 看起来一样 | P3 表面已 True；但测试图是同一组 RGB 分别带 Adobe/P3/sRGB ICC，正确管理下 P3 与 sRGB 应不同。当前 `BitmapFactory` 的 ICC→P3 转换仍可疑，下一步用共享 `ColorEngine`/Skia skcms 接管；真机锁屏暂未取到解码色彩日志 |
| 24 | 缩略图图片不偏左但偏上 | 图片/第二行改 8..88，标题基线 103、日期 115、星级 126，使上边距与左右 8dp 一致 |
| 25 | 开关胶片栏/控制栏后主图严重偏移 | 两因：SurfaceView 新缓冲晚一帧（旧尺寸帧残留）+ 视口不保留中心。`OnSizeChanged` 保留 `CenterUV` 并 Post/PostDelayed 重绘；SurfaceChanged 也补两帧 |
| 26 | 分析栏细节绿框随机乱飞、小图也出框、双击位置怪 | 按 iOS 语义重做：点按瓦片只切换定格（中心恒为瓦片自身 CenterU/V，不取手指落点）；双击才定位到瓦片中心；绿框大小 = 对焦框或 1:1 裁剪像素×视口 Scale；切图/关闭分析栏清除；适应态已大于物理 100% 的小图不画绿框；`FocusOnPoint` 小图夹回适应 |
| 27 | 长按菜单每个项之间都有分隔线 | 按 iOS 分组：打开文件夹｜胶片栏/分析栏/控制栏｜增强预览｜工具/设置；顺序也改为 胶片/分析/控制 |
| 28 | 初次打开文件夹控制栏只剩容器 | 空分组兜底重建 + 文件/CurrentFile 补 MarkDirty + 文件夹 UI 更新 120ms 后再重建；SAF 选择器返回时 Activity `OnResume` 再强制刷新控制栏与主图一帧 |
| 29 | 主图色彩整体发白，任何格式 | 不是 ICC：真机 EGL P3 表面把主图整体抬亮（同一 JPEG 强制 sRGB 表面后正常，缩略图也正常）。默认改为 sRGB 表面/解码（`ForceSrgb`），广色域保留 `--ez pv-p3 true` 诊断；待确认 P3 EGL/合成传递函数后再默认开启 |
| 30 | 控制栏偶现“只剩容器” | 真正根因：`OnDraw` 在 dirty/空分组时只 `Rebuild`，新分组 `Rect` 还是 0，绘制循环全部 `continue`。现改为在 `OnDraw` 内调用 `Arrange`（重建并算出矩形）再绘制；配合 SAF 返回刷新 |
| 31 | 胶片栏图片被拉成 80 方形 | 卡片绘制改为按位图比例等比装入 80×80 并居中，非方形预览不再拉伸 |
| 32 | HIF 导航指示器无缩略图 | 导航图改取当前展示产物（HIF 主图由 libheif 解出），不再单独走 BitmapFactory；真实 HIF `A7C05520.HIF` 已实测导航图出图 |
| 33 | 同名 HIF/ARW 优先级 | 真实 `same.hif + same.arw` 实测：卡片 `same.hif(RAW)`、主图 HIF；`same.jpg + same.hif` 实测代表与主图均为 JPG。小组代表仍按 `SelectedFormats` 顺序 |
| 34 | 同名合并后主图可能是被隐藏文件 | 文件夹打开时 CurrentFile 可能先指向 ARW/HIF，卡片却显示代表；`ApplyFilter` 末尾把隐藏的 CurrentFile 映射回其代表，实测 JPG/HIF/ARW 主图与卡片一致 |
| 35 | HIF 缩略图/指示器等待久 | Sony HIF 的 `thmb` item 用 libheif thumbnail 插件解不了；但容器里有独立的 JPEG item（实测 160×120）。新增 `HeifJpegThumbnailExtractor` 解析 iinf/iloc 提取 JPEG item，胶片/导航优先用它，跳过 7008×4672 全图；随后 512 代理缓存复用。主图 Full 仍需一次全图 libheif（另一条路径），首张小图已不再等全图 |
| 38 | HIF 同一文件缩略图横竖不一致 | 同一 HIF 内不同 thumbnail item 方向不一致：160×120 是传感器横向，373×560 已是显示竖图；统一按容器 270° 转会把后者转横。改为先比较“原始横竖”和“显示横竖”，相反才转。同进程先开 A670 文件夹再开 A7C09124，首卡已与主图同向 |
| 37 | HIF 首张缩略图方向偶发错 | 首张走优先加载时 MetadataExtractor 可能还没给出 Default Rotation；新增 `HeifOrientationReader` 直接从文件头 irot 读角度，元数据为 0 时兜底。用 `/sdcard/PhotoViewerTest/12760425/A7C09124.HIF`（irot=1→270°）验证首卡与主图同向 |
| 36 | HIF 胶片缩略图横竖错乱 | JPEG 缩略图 item 存储方向与主图不同。`ThumbnailService` 之前把 HEIF 平台源硬编码 `isPreRotated:true`；改为 `HeifLoader.OutputPreRotated`，Android libheif/JPEG item 路径按容器 Default Rotation 再做方向对齐。用改 `irot=90°` 的 HIF 验证主图与卡片同向 |
