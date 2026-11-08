# PhotoViewer.Android — Android 原生壳

Debug 与 Release 均只启动原生壳，无旧壳入口。壳内容：原生主图（`SurfaceView` + EGL14/GLES，8 位 P3/sRGB）、主屏布局（文件栏/胶片栏/控制栏/导航图/insets）、SAF 存储、硬件键盘/滚轮/长按菜单/返回、半屏设置（五分类 + 关闭回写）。实现与验收状态见 [Design/status.md](../Design/status.md)，计划、验收矩阵与色域结论见 [Plans/native-rebuild-phase-android.md](../Plans/native-rebuild-phase-android.md)。

**发行版本**：构建、运行与发布必须显式传 `PhotoViewerEnableDino=false|true`；缺失或非法值直接报错。标准版 `PhotoViewer` 不编译相似面板、DINO 诊断及 ONNX 托管/原生依赖，保留 CV 分析、三瓦片「CV 诊断」与独立「即时计算 CV」；AI 版 `PhotoViewer AI`（显示名空格，APK 包名 `PhotoViewer-AI`）内置固定模型和 DINOv3 License，并提供独立「即时计算 DINO / CV」。下文 DINO、相似聚类及五瓦片诊断均指 AI 版。两版使用同一 ApplicationId `cc.xiaojh.PhotoViewer`，替换安装保留设置与数据库，不提供同机共存或产品内切换。头工程及共享工程的 `bin/<standard|ai>/`、`obj/<standard|ai>/` 隔离；只构建 arm64-v8a，清单从源模板生成中间目录副本并写入版本显示名，不改写仓库模板。发行能力与数据边界见 [EDITION](../Design/spec/editions.md)，开发和发布入口见 [DEV.md](../DEV.md)。

## 启动与主屏

| 文件 | 职责 |
|---|---|
| AndroidApp.cs | 普通 `Android.App.Application`，不初始化任何 UI 框架。 |
| AndroidBootstrapActivity.cs | 唯一 `MainLauncher` 与 `ACTION_VIEW`/`ACTION_SEND`/`ACTION_SEND_MULTIPLE`（`image/*`）入口；原样转发 Action/Data/ClipData/Extras 到 `AndroidNativeActivity` 并 `Finish()`。 |
| AndroidNativeActivity.cs | 原生壳 Activity（`AppCompatActivity` + `@style/Theme.PhotoViewer.Native`）；挂 `AndroidNativeShell` 根视图，转发 SAF 结果、硬件键盘与返回键；硬件键盘只把 `Action.Down` 交给共享命令表，`RepeatCount>0` 才是系统重复、首下为 0，`Action.Multiple` 与未命中事件原样交还系统，返回/音量/电源等系统键不参与命令派发；返回键按「长按菜单 → 星级来源浮窗 → 筛选下拉 → 设置/工具 → 退出」顺序消费；命令能否执行只按共享 `InputCommandMap.IsCommandKnown`（`SettingsCatalog.Commands`）判定，图标与可见性只影响控制栏外观；`ConfigurationChanges` 覆盖键盘/键盘隐藏/导航/触摸/方向/尺寸等，外设插拔与软键盘显隐不重建 Activity、不丢设置或工具 sheet。 |
| Native/AndroidNativeShell.cs | 原生启动顺序：色域探测 → `AndroidUiDispatcher` → ReactiveUI → 门面（FilePicker/AppResources/Heif/展示解码器/预算/MemoryGovernor/`AndroidStorageAccessSessionFactory` + `AndroidStorageAccessManager`/共享 `RatingService` + `XmpRatingStoreFactory`/`SettingsRepository` + `AndroidSettingsStorage`/设置展示器/DINO-NNAPI）→ `MainViewModel()`（实例级 `OpenRequestChannel`）→ `PhotoLoadSession` → 根视图；HEIF 门面注入前先 `FastHeifDecoder.Configure`（`libpvhevc.so` + 移动端上下文/并发上限），MemoryGovernor 注入后置 `BitmapLoader.AutomaticCacheLimit`（缓存容量按实测预算推导）与 `ImageBufferPool.MaxIdleBytes = min(512MB, 预算/8)`。外部打开（含 `pv-open` 与多个 Intent Uri）合并为一次打开请求：`file://` 回本地路径快路径，`content://` 在后台线程取真实显示名（DocumentsProvider 投影或通用 `_display_name`），数字 documentId 不再被当成文件名丢给按扩展名判类型的打开链，`pv-settings` 与程序化入口（`pv-check` / `pv-stress` / `pv-perf-probe`）也在这里收口。日志出现 `[Native] Android 原生壳就绪`。 |
| Native/AndroidNativeRootView.cs | 原生主屏：按共享 `MainScreenLayout`/`AndroidMainScreenLayout` 拼合文件栏（筛选条 + 胶片栏）、主图、控制栏（溢出时沿主轴滚动并保留偏移）；筛选条在窄屏同样由可滚动宿主承载，控件宽度不压缩；接线会话、栏位显隐（SharedPreferences `PhotoViewer.Native.ShowFilm/ShowControls` 跨会话恢复）、导航图、长按菜单、评分/Full 失败/会话一次性/打开失败（STATE-02）提示条（文案取共享 `StatusMessages`，与「计算中」药丸同时可见时上移到药丸上方）；行布局按共享 `SimilarityPanelGeometry.RowWidth` 给相似面板让宽并计入列表态停靠区；加载提示绑 `PhotoLoadSession.IsLoadingVisible`（300ms 延迟），增强药丸绑 `IsEnhancingVisible`（500ms 门槛），物理视口上报 `PhotoLoadSession`，EXIF 对焦点到达后同步共享视口。纯视口平移/缩放只更新加载需求、导航框与活动细节框，不触发分析重路由；分析通知由展示内容、EXIF、分析栏显隐与即时计算设置变化驱动；细节框已隐藏时不重复请求根视图重绘。主图长按菜单（MENU-03/MENU-06）每项为「勾选列 → uicons 命令图标 → 文本」，图标取 `icon.menu.*`/`icon.command.toggleEnhance`，尺寸与间距取 `menu.icon.size/gap/fontSize`；图标与勾号都标 `ImportantForAccessibility=No`，读屏只念整行名称（勾选项附「已勾选」）。RATING-04 的宿主接线：卡片三角（胶片栏/相似面板）与控制栏三角都交给 `AndroidRatingSourcesPopup`，锚点取各自 0 号槽位的真实屏幕矩形；切图、列表替换（`FilteredFiles`）、栏位隐藏、横竖布局切换、长按菜单/筛选下拉/工具页打开时收起，其余入口隐藏或移动由浮窗每帧核对锚点时关闭（设置 VM 的派生属性会异步连发，不能据此关闭）。设置刷新走共享 `SettingsViewModel.PresentationChanged` 的影响位（Layout/Navigator/Rating/Exif/Commands/InputBindings/FileProjection/Analysis/Similarity），只刷该位真正涉及的部件，不维护诊断属性排除名单；控制栏按 `EnabledExifItems`/`EnabledControls`/`ShowRating`（改分组，需重新量测）与 `StarOpacity`/`SafeSetRating`（只重绘）分别处理，其余通知不再整条硬刷，文件/选择器返回的 120ms 补帧保持原样；筛选下拉的锚点核对在 `OnLayout` 末尾统一执行。 |
| Native/AndroidMainScreenLayout.cs | 主屏分区纯函数：共享分区数学 + 系统栏/刘海 insets；栏位背景铺到屏幕边缘、内容收在安全区。 |
| Native/AndroidPhotoView.cs / AndroidPhotoRenderer.cs | `SurfaceView` + 自有 EGL14/GLES 主图：P3 passthrough → 标准 P3 → sRGB 表面协商；查询实际 EGL 色彩空间与 GL 帧缓冲编码，仅 sRGB 编码帧缓冲由着色器先线性化，避免二次编码；上传按源标签转换到实际表面，纹理检查按实际目标色域比较期望值、不写死 sRGB；销毁上下文时清空表面状态。RGBA8 纹理/mipmap、八方向 UV、`PhotoViewport` 手势（放大态平移、双指捏合、双击/Fit、适应态横滑）；点击识别前先更新触摸状态，Move/Up 连同批次历史检查位移，超过系统触控容差或加入第二指即取消该轮点击/长按识别；长按平台候选先登记，按住时长从 Down 真正送达起算，最终确认统一延至下一帧输入处理之后；越容差移动/抬起/取消/第二指/新 Down 作废旧候选，确认再核对触摸轮次与活动状态。动画被触摸接管停在当前帧，迟到的旧结束不覆盖；已长按不再触发拖动/翻页/点击，双指退回单指时重置平移基点；滚轮先按修饰键查用户绑定（如 Ctrl+滚轮缩放，命中即执行、不再分流），无修饰时按精确/离散分流（触控板平移、离散滚轮累积到一格才派发命令），物理 100% 触发 Full。切图与热替换按 `PhotoLoadSession.Generation` 区分（缓存命中切图不经过空图，同机型连拍几何相同）：代际变化走切图语义，同一实例 + 同代际的会话通知只重绘不重传纹理；无图时清除纹理宽度。**顶点缓冲**：64B direct buffer、float 视图和顶点数组按 renderer 复用，不逐帧分配；同一 renderer 的 EGL 上下文重建保留 CPU 缓冲，`Dispose` 先释放视图再释放 owner。**像素提交**：紧凑 RGBA8 在同步 `glTexSubImage2D` 期间短时固定现有像素数组，JNI direct-buffer 只建视图、不生成整图副本，调用返回即解除固定；有额外行距时复用 ≤4MB 暂存逐行拷贝有效像素，绝不使用会往返搬运整幅数组的 `ByteBuffer.Put(byte[])`。暂存随 renderer 释放，上传统计分别记中间拷贝字节/耗时与 GL 提交。`up_*` 保留建纹理/存储分配/像素上传/mipmap 四段口径（缓存命中为 0）；`submit_*` 另记完整同步提交墙钟（含等锁、EGL 绑定、实际色域转换、纹理收尾与转换租约归还，不含视图 Draw/Swap）。转换分位数只取真正转换的样本，不以缓存命中稀释；空纹理复用只代表纹理名复用，仍重新分配存储。**纹理缓存**：当前帧 + 最近 2 帧按 `DecodedImage` 实例身份缓存（弱引用，不延长像素租约），另有 1 张 ≤32MB 的同尺寸空纹理供下次上传复用（驱动在新建纹理时可能停顿）；命中不重传、`up_hits` 记 0 ms。纹理字节含 mip 链（×4/3）上报为 `AndroidPhotoView` 的 footprint，并通过 `AndroidMemoryBudgetProvider.ExternalFootprint` 加进预算源（GPU 内存不在 RssAnon）；压力 Caution 起在 UI 线程丢掉非当前纹理与空纹理（`Trim` 由探测线程调用，只投递）。 |
| Native/AndroidFilterBar.cs | 自绘下拉 ×3 + 计数 + 相似聚类 toggle（窄屏紧凑宽度 84/76dp）；原生 PopupMenu 写回共享筛选 VM，星级菜单含「已拒绝」「星级冲突」13 项。布局幂等：`Arrange` 只重算几何，相同几何直接返回，**任何 Arrange 都不收起已打开的下拉**（窄屏一次 `OnLayout` 会先按视口宽、再按文档宽各排一次，中间态不能当失效锚点）；打开态锚点由根视图在一次布局结束时调 `CommitLayout` 用最终几何核对——锚点仍在可视区就跟着挪（`Popup.Update`），控件被隐藏/滚出可视区才收起。滚动宿主上报真实滚动变化时收起下拉。 |
| Native/AndroidFilmstripView.cs / AndroidThumbnailCard.cs / AndroidBitmapConverter.cs | RecyclerView 胶片栏 + 自绘卡片（96×144 / 80 方形 / 星级 / 抖动徽标 / 第二行）、350ms 三次缓出定位/用户接管/减少动态效果、可见范围上报、星星写回不切图。组级星级冲突（RATING-04）时 0 号清零叉换成 `color.star.conflict` 红色警示三角，点击只打开该卡片的来源浮窗，不写 0、不切主图；非冲突时仍是清零叉。绘制、命中与锚点共用同一星级行几何（左 6dp、上 119dp，列布局再叠加水平居中偏移），0–5 槽位经 `ExploreByTouchHelper` 暴露读屏节点（冲突时 0 号名称「星级冲突」，点按与触摸同一条 `ActivateSlot`）。 |
| Native/AndroidControlBar.cs | 共享 `ControlBarLayout` 的 EXIF/评分/命令三分组、换行/居中；命令显隐/顺序来自共享设置（含分析栏/增强）；命令读屏名称为 TOOLBAR-04 的 `名称（主 / 次）`（取共享 `CommandTooltipText`，本端只把绑定格式化成可读键名）；评分快捷命令为实心星内叠数字（0 为清零叉），数字不拦截点击；组级星级冲突时 0 号同几何换成红色警示三角（RATING-04），点击（触摸与读屏虚拟节点同一入口）只打开当前照片的来源浮窗、不写值，读屏名称为「星级冲突」；`TryGetRatingSlotScreenRect` 给出评分槽位的屏幕矩形供浮窗定位；`ExploreByTouchHelper` 暴露虚拟节点、名称、禁用态与焦点顺序。 |
| Native/AndroidRatingSourcesPopup.cs | RATING-04 星级来源浮窗：`PopupWindow` + 自绘内容视图，纵向逐条列出共享 `RatingSourcesViewModel.Items`（文件名、内联格式或 XMP 标签、状态、0–5 星）。宽度、条目单/双行、方向、夹取与文件名中间省略取共享 `RatingSourcesLayout`，本端只提供文本测量与入口/宿主屏幕矩形（另加 1dp 描边）；星排与控制栏评分组同槽位、字形与悬停/按下底色。点任一条目的任一值走共享 `SetRatingAsync` 整组写入，写入成功且刷新后冲突解除才收起，失败保留并按磁盘真值刷新全部条目；读屏经 `ExploreByTouchHelper` 暴露每格星级按钮。定位参照入口 0 号槽位的真实屏幕矩形；每帧核对锚点、宿主矩形与目标冲突态，锚点移动/滚出可视区/入口移除即关闭，外部 dismiss 同步共享 `IsOpen`。 |
| Native/AndroidControlBarScrollHost.cs | 控制栏与筛选条共用的滚动宿主（TOOLBAR-08 / FILTER-07）：文档超出可用主轴时拖动/惯性滚动，普通重建不回到起点，偏移按新几何夹取；未溢出时行为与原先一致。偏移真实变化（拖动/惯性/换向夹取）时发 `ScrollChanged`，筛选条据此收起已失效的下拉锚点。 |
| Native/AndroidNavigationOverlay.cs / AndroidNavigationDecoder.cs | 缩放指示器（长边 150dp + 可见框 + 百分比，宿主不透明度 0.6；白框可拖动定位，IMAGE-06）；缩略图取当前展示产物（含增强预览）抽样转正，不二次解码文件、不回读 GPU。 |
| Native/AndroidNativeSettingsPresenter.cs | 半屏 Material 3 BottomSheet：五分类（文件/预览/控制/EXIF/AI）、共享 `NativeSettingsSession` 工作副本、关闭/下滑/返回统一回写；内层内容高度由 `AndroidSheetViewport` 跟随面板可见区域（半屏与 85% 两档滚动范围一致）；三张可排序表用 `ItemTouchHelper` + 行尾把手（轻点把手是上移/下移菜单）。行按 `settings.android.*` 令牌收口：单行紧凑行（缩放预设/纯触控控制/EXIF）48dp + 上下 2dp；普通设置行、分类行与工具分类行取标准行高 52dp + 上下 12dp，可排序表的多行格式与显示键鼠绑定的控制行取 52dp + 上下 8dp，均随系统大字体与多行内容增高。卡片内容左边 16dp、右 12dp，分类箭头 20dp 居中容器；页面不写说明脚注。文件页「缓存与预加载」按 `ShowsCacheLimits` 决定是否显示「缓存内存上限 / 缓存数量上限」（Android 自动口径下隐藏，两项提示行保留）；两条提示行沿用共享 `SettingsViewModel.PerformanceBudgetInfo`（系统总内存与系统当前可用，带可选超限原因）与 `BitmapCacheCountInfo`（「可缓存 xx 张适屏照片 / xx 张 3300 万像素照片」，按设备最大安全容量估算，不读取隐藏用户上限），按内容换行（不省略、不截断），颜色按 `PerfInfoLevel` 取常规次要文字 / `color.settings.warning` 柔和暗金 / 错误红；共享 VM 因周期探测发出这四项属性变化时只刷提示行，不重建页面、不动设置值，Android 隐藏用户缓存上限因此不告警。滚动预载延时用步进行，范围与步进取共享 `SettingsDocumentCodec` 的 100–2000 ms / 100 ms。控制页按 `AndroidInputDevices` 能力显示主/次绑定：有外设时提供录制（`SettingsBottomSheetDialog` 统一截获键盘/滚轮/鼠标按键，录制态左右键不穿透）与清空，纯触控整块隐藏绑定文本、冲突与录制/清空控件，读屏也不朗读绑定；`Esc` 与系统返回键取消并保留原绑定、`Delete`/`Backspace` 清空、写入共享 `HotkeyItem` 并重算冲突，录制时鼠标按键不穿透到下层控件。缩放预设每行只有一个居中数值框 + 固定 `%`，失焦/空白/Done/导航/关闭统一校验提交，保留输入框实例并原地重排；`＋` 新增 100% 项、`−` 删除项。 |
| Native/AndroidSettingsReorderList.cs | 设置页勾选排序表（SETTINGS-15）：以无界高度测量完整表高、自身不滚动，不能按当前视口截断后续命令；44dp 勾选命中区居中绘制 Material 3 的 18dp 方框，行内左边距只把可见字形对齐到卡片 16dp 内容边，勾选字形与命令图标、图标与命令名的视觉间距同为 `settings.android.icon.gap`（16，图标左边距扣除命中区透明内缩）；单行内容（纯触控控制、EXIF）48dp + 上下 2dp，带扩展名副文本的格式行与显示键鼠绑定的控制行取标准行高 52dp + 上下 8dp；行尾把手轻点开上移/下移菜单、长按拖拽。 |
| Native/AndroidSheetScrollView.cs | 设置与工具半屏共用的内容滚动宿主、初始展示与可见高度同步（SETTINGS-04）：`AndroidSheetPresentation` 在 `Show()` 之前把 `BottomSheetBehavior` 配成屏高一半的收起态（`settings.android.sheet.peekRatio`）、外层面板高度 85%（`settings.android.sheet.heightRatio`），上拖扩大、下滑关闭，避免先展开再闪回；`AndroidSheetViewport` 在每帧绘制前把内层「把手 + 标题 + 滚动区」根布局高度同步为面板当前可见高度（扣除浮层宿主底部内边距），收起、拖动/惯性、展开后重新收起、窗口尺寸与系统栏 insets 变化都跟随，ScrollView 的量测与滚动范围因此等于屏内可见区域，长表最后一项在任一档都能滚到屏内；外层面板高度与拖动几何不变，避免 expandedOffset 跳变或循环布局。内容区按下时禁止祖先拦截并退出嵌套滚动，上下滑只滚内容、到顶继续下拉也不会被 `BottomSheetBehavior` 收下成收起；把手/标题不在该视图内，拖动关闭仍由 sheet 容器承担。不要换回普通 `ScrollView`。 |
| Native/AndroidCommandIconView.cs | 设置页控制栏功能行的命令图标（SETTINGS-01 / TOOLBAR-04）：与控制栏同一语义与字号（18sp uicons 描边；评分 1–5 为实心星叠数字、0 为零清叉，雾天增强取 `icon.command.toggleEnhance`），按字形实测尺寸测量容器（系统大字体不裁字）；PUA 字形只绘制，读屏整行名称不含图标。 |
| Native/AndroidInputDevices.cs | `InputManager` 设备增删监听：真实字母键盘（排除虚拟设备与非字母键）或鼠标/触控板任一连接即视为具备键鼠录制能力；只按系统连接状态判断，供设置页动态增删控件。 |
| Native/AndroidAnalysisPanel.cs / AndroidAnalysisHost | 分析栏：共享 `AnalysisPresenter` 宿主，细节预览/直方图/DINO-CV 瓦片自绘；细节绿框按“触控按下立即点亮、滑动切换/空白清除、抬起或取消清除、真实鼠标/触控板悬停、失活/隐藏/换图清除”工作，双击定位与绿框互不影响；诊断瓦片只在未超过触控容差的完整点按时落准星；可见时主图升 Full。 |
| Native/AndroidSimilarityPanel.cs | 相似聚类面板：复用卡片 + 分数条，三态/动作区/真实“提取全部”，行布局从胶片栏右侧让位；向主屏提供列表态停靠区自然宽度，让 1–2 项列表不被动作区挤窄。 |
| Native/AndroidToolsPresenter.cs | 工具 BottomSheet：与设置页同款容器尺寸、初始半屏收起、把手/标题栏与分组卡片导航（内容共用 `AndroidSheetScrollView` 与可见高度同步，分类行 52dp 标准行高/20dp 箭头）；根页只有 EXIF 详情（分组/格式切换）与 DINO 诊断（5 瓦片 + 刚体拟合），导航状态写共享 `ToolsViewModel`；显示/关闭同步 `IsPresented`，关闭即 `Deactivate()` 取消在途 DINO。控制栏 EXIF 整组入口经 `ShowOrActivateExif`：已展示其他子页时按共享导航态刷新内容直达 EXIF，无可用 EXIF 回退分组列表（TOOLBAR-06 / TOOLS-02）。 |
| Native/AndroidInputMapping.cs | `KeyEvent`/修饰键 → 共享 `GestureKey`/`GestureModifiers`；Android 语义另写，不复用苹果修饰键表。键位按物理键对齐其他平台：Enter 与小键盘 Enter → `Return`，Backspace（`Keycode.Del`）→ `Back`、`ForwardDel` → `Delete`，小键盘 `+/−` → `OemPlus/OemMinus`、`.`/`*`/`/` → `Decimal/Multiply/Divide`，主行数字 `Num0–Num9` → `D0–D9`；系统返回键（`Keycode.Back`）不产生任何手势。设置页录制复用同一映射生成共享 `AppGesture`，不维护第二套键码。 |
| Native/AndroidUiTheme.cs | 固定深色、显式圆角/字重/主色；uicons 描边与实心图标字体从 Assets 加载（缺失各自回退并只记录一次），设计令牌的图标码位转字形。 |
| Core/AndroidPresentationDecoder.cs / AndroidPresentationEncodingPolicy.cs / AndroidDisplayCapabilities.cs | 展示解码：**RAW 走 `AndroidRawDecoder`（LibRaw 全分辨率开发）**，不落 `BitmapFactory`（系统只会给内嵌预览，违反代理精度规则），失败返回 null 由会话如实报错；HEIF 先走共享 `FastHeifDecoder`（自解析容器 + `libpvhevc.so` 并行解 HEVC 瓦片；方向取容器 `irot`/`imir`，按 ISO/IEC 23008-12 忽略 HEIF 的 EXIF Orientation 项），快速路径不可用时**直接调共享 [PhotoViewer.Heif](../PhotoViewer.Heif/AGENTS.md) 的 libheif 出口产出目标色域（sRGB/P3）的 8 位 RGBA**（不先收敛 sRGB8，不重复快速路径；HEIF 不经系统 `BitmapFactory`——真机实测其带 P3 ICC 的输出灰阶最大偏 18，不能当可信色彩出口）；代理按 `InSampleSize` + 精确缩放（非 HEIF 系统链），快速路径把容器方向写入 `DecodedImage.Orientation` 交顶点阶段、libheif 出口已按容器变换摆正，缩略图路径返回已转正的 sRGB8。同时实现共享 `IQuickPreviewDecoder`：RAW 首次解码空窗期用内嵌预览顶替（STATE-01），RAW 缩略图也只解内嵌预览、绝不全分辨率开发。编码协商恒 Rgba8；设备与窗口支持广色域时解码目标稳定为 P3，否则 sRGB。解码入口捕获一次目标贯穿请求，表面销毁/回退不改变缓存的像素解释；上传时按实际表面显式转换。无 ICC 但 EXIF 声明 Adobe RGB 的 JPEG 由共享容器色域链解码，失败不回到忽略声明的系统路径。 |
| Core/AndroidRawDecoder.cs / AndroidBitmapPixels.cs | RAW 解码（`libpvraw.so` = LibRaw 0.22 薄 C ABI，见 `NativeSrc/pvraw`）：全分辨率或 half_size 开发，产物**未旋转**的 RGBA8（A=255），sRGB 或 Display P3（LibRaw `output_color` 1/7，DCI-P3 D65 原色 + sRGB 曲线），方向映射 flip→EXIF（0→1,3→3,5→8,6→6…），`SourceWidth/Height` 为全分辨率裁剪尺寸（`adjust_to_raw_inset_crop`，A7C II 7008x4672 / A6700 6192x4128）。曝光＝LibRaw 直方图 1% 白点、提升上限 2.5×（`no_auto_bright=1` + `bright`），与相机 JPEG/ImageIO 同族且逐字节等同 LibRaw mem image 路径。并发：后台（非 P0）RAW 开发串行、总并发 ≤2，P0 不与后台排队；各优先级按 `MemoryGovernor.TryAcquire(EstimateWorkBytes)` 申请原生工作内存（精缩中间缓冲不从该额抵扣，最终产物由加载器记账），P0 放行但记账、后台被拒即跳过（代理返回 null 不算错误）。`EstimatePeakBytes` 由实测 RSS 拟合（2.2 B/原始像素 + 11 B/输出像素）。`AndroidBitmapPixels` 从位图原始 ARGB_8888 存储读取 RGBA 字节（优先 JNI 锁像素，失败回退 `CopyPixelsToBuffer`），保持行距与预乘 alpha，并按位图实际色域显式规范到捕获目标；禁止用会返回 sRGB 打包值的 `GetPixels` 读取 P3。另提供 RGBA8 直缩（Mitchell，避免 Bgra32 往返）。 |
| Core/AndroidLibHeifDecoder.cs / AndroidHeifDecoder.cs | `HeifLoader` 注入的 HEIF 解码器（胶片缩略图与兜底链，**纯 libheif 出口，无系统解码段**）：目标长边 ≤512 先解容器自带的 `thmb` 项，再走 `FastHeifDecoder`，最后共享 [PhotoViewer.Heif](../PhotoViewer.Heif/AGENTS.md) 的 libheif 出口（`Bgra32Srgb`，色彩按真实 ICC/nclx 打标签后再转，高位深源不先量化，PQ/HLG 如实拒绝并写 `HeifLoader.LastDecodeError`）；小尺寸请求在 libheif 段仍先用容器内嵌 JPEG item（相机小图，仅在容器方向恒等时使用，出口经 `AndroidBitmapPixels` 规范到 sRGB8）。契约：产物一律是**显示朝向**（方向 1）的 sRGB8，`OutputPreRotated` 为 true——快速路径与容器 `thmb` 项的未旋转产物在本链按 `fast.Orientation` 摆正，共享 libheif 本身已按 `irot`/`imir` 摆正，容器内嵌 JPEG item 只在容器方向可读且恒等时使用；`ThumbnailService`/`BitmapLoader` 因此不再二次旋转，也不读 HEIF 内 EXIF Orientation 项。DINO 的 560 短边请求仍解整幅主图。`AndroidHeifDecoder` 现在是二十行的共享 libheif 兼容 wrapper（`FastHeifDecoder.ReadAllBytesAsync` → `LibHeifColorDecoder.Decode(Bgra32Srgb)`），不再调用系统 `BitmapFactory`；两者都没有系统解码段。 |
| Core/AndroidStorageRefs.cs / AndroidRandomAccessSession.cs / AndroidFilePicker.cs / AndroidStorageAccessSessionFactory.cs / AndroidStorageAccessManager.cs | SAF `content://` 与本地 `IFileRef`/`IFolderRef`：文档身份为 `android-document:<authority>:<documentId>`，本地为 `file:<绝对路径>`，显示名不参与身份；嵌套目录保留根 tree Uri 与自身 documentId（子项 Uri 由 `BuildChildDocumentsUriUsingTree(根树, 自身 ID)` 枚举）。**属性新鲜度**：目录枚举的投影只是打开时的快照，`GetBasicPropertiesAsync` 与 `OpenRandomAccessAsync` 每次都重新查询（`RefreshProjection`，大目录属性加载因此与枚举同量级），外部程序改动后 size/mtime/revision 不再陈旧；能力（Document flags）由后台投影补齐（`EnsureBackgroundProjection`），`Name`/`Capabilities` getter 只读缓存、不做同步 binder 调用，UI 线程不被 Provider 卡住；`CreateForContentUriAsync` 供外部打开入口在后台取真实显示名。**随机访问**：经 `OpenFileDescriptor("r"/"rw")`，打开时先探测定位（`FileChannel.Position`），管道类 fd 直接以 `NotSeekable` 拒绝——绝不用 fstat 的 0 或投影冒充文件长度；长度取描述符真实值，仅在其不可得时回退到打开瞬间查到的字节数。托管 Stream 搬运字节，读写模式 Dup 独立读流以支持写后回读；支持定长读写、以 `Os.Ftruncate` 实现长度扩展/回缩（扩展区由系统填零），以及 `FlushAsync`（Force + fd.Sync）。SAF `AtomicWriteAsync` 明确 Unsupported（不做 Delete→Create 假原子），能力按 Document flags 给三态（缺失为 Unknown，无 `SupportsWrite` 的只读文档经共享映射报 `NotSeekable`）；`FindFileAsync`/`CreateFileAsync`/`DeleteAsync` 按投影显示名查找并走 `CreateDocument`/`DeleteDocument`；选择器为实例 pending，单文件结果在后台读完真实属性后才完成（与外部打开共用 `CreateForContentUriAsync`，UI 线程不做显示名 binder 查询），落结果前按等待项身份做代际检查——新请求、取消或 Activity 销毁后旧结果既不复活也不串写新请求；Activity 销毁/重建与新请求都按取消收口；持久授权由 `AndroidStorageAccessManager.Retain` 校验并补取，`AndroidStorageAccessSessionFactory` 在会话创建/附加项时做访问准入；系统管理持久授权，会话结束不撤销仍可复用的授权，读权限不存在时抛 `AccessDenied`。 |
| Core/DebugTestDocumentsProvider.cs / Native/AndroidNativeChecks.Storage.cs / Properties/AndroidManifest.Debug.xml | 仅 Debug 注册的验收 DocumentsProvider 与 `storage` 套件：以 App 私有目录提供 writable/readonly/nonseekable 三棵真实 `content://` 文档树，不承载产品数据。DocumentsProvider 必须 `Exported=true` 并授予 URI 权限，读写由 `MANAGE_DOCUMENTS` 保护；Release 不注册。同文件另有 `DebugTestMediaProvider`（通用 `ContentProvider`，**非** DocumentsProvider，`Exported=false`）：以数字 ID + `_display_name` 提供只读媒体，用来核对只有数字 documentId 的 `content://` 打开必须查真实显示名。覆盖嵌套枚举、缺失元数据、外部改动后属性刷新、写入后新鲜 revision、随机读写、JPEG 内嵌 XMP、ARW 内嵌 TIFF tag 700 写入且不创建 sidecar、已有 sidecar 随整组同步、只读预检零写入、不可定位（打开即 `NotSeekable`）拒绝、访问拒绝故障注入、同 UID `ACTION_VIEW` 与数字 ID 通用 content URI；不代替跨 UID 临时授权、系统授权撤销、DocumentsUI、USB/MTP/云 Provider 实测。 |
| Core/AndroidNativeWindowPresenter.cs | 设置走原生 BottomSheet；工具页交 `AndroidToolsPresenter`。 |
| Core/AndroidUiDispatcher.cs / AndroidAppResourceProvider.cs / AndroidSettingsStorage.cs / AndroidPerformanceBudget.cs / AndroidMemoryBudgetProvider.cs | 既有平台能力；`AndroidMemoryBudgetProvider` 预算按 `MemTotal` 查 AOSP MemoryLimiter 前台可见档，小米 HyperOS/MIUI 的 Sentinel PSS 红线（`persist.sys.stability.pss_highest_line`，越线即被强杀，不经 lmkd）更低时取其 90%，footprint = `RssAnon + VmSwap` + 主图挂的 `ExternalFootprint`（GL 纹理），可用量再受系统 `availMem − threshold` 收窄（Android 14+ 前台收不到 RUNNING_* trim 回调，周期探测是唯一压力源）；`GetSystemMemoryInfo()` 另按共享 `SystemMemoryInfo` 首读 `ActivityManager.MemoryInfo` 的 `TotalMem` / `AvailMem` 作为系统整机读数（可用量取不到时为 -1，总内存取不到为 `Unknown`），它是设置页能力提示的来源，**不是** `GetAvailableBytes()` 的本进程预算余量，两者不可互相顶替，也不参与运行时预算与缓存默认；`AndroidPerformanceBudget` 的内存上限取该预算、原生预载默认 6 核及以上 2 路否则 1 路。`AndroidSettingsStorage` 存 schemaVersion 2 JSON（后台 `Commit` 并检查结果）；损坏/旧版先备份为带时间戳的偏好键（最多 3 份）再以默认值启动，备份失败保持不可写且不覆盖原文（SETTINGS-14）。评分写回统一走共享 `RatingService` + `XmpRatingStoreFactory`，没有 Android 专属 Writer。 |

## 主图提交与后台生命周期

- 生产主图经 `AndroidPhotoView.Presentation.cs` 的 `PresentImage` 提交：同色域与纹理缓存命中保持即时路径；跨色域转换在单路线程池准备，按实际 EGL 表面捕获目标。切图和 Surface 重建使旧请求失效，取消传入 `ColorEngine` 行带，旧结果不得提交。`SetImageForChecks` 与 `RecreateSurfaceForChecks` 仅供同步对照验收，不接产品入口。
- 请求源、正在展示的源及在途转换各持独立像素租约；替换、取消和释放分别归还，避免会话回收后 Surface 重建读取空像素。GPU 缓存仍只按原源实例弱引用标识，准备产物提交后即释放。准备失败经共享 `StatusMessages.PresentationFailed` 提示，不回落到忽略色彩的上传。
- 手势、动画与布局的绘制请求经下一动画帧合并，视口和 Full 需求仍同步上报；布局补帧也进入同一通道。`RenderedFrames` 只计成功交换，不把无表面或 EGL 失败的调用算上屏。
- 最近纹理和空纹理合计另受 `clamp(应用预算 / 12, 16MiB, 384MiB)` 字节限制，预算未知取 64MiB；当前纹理不受此淘汰限制。最近张数仍至多 2，压力修剪规则不变。字节限制是估算预算，不是驱动真实分配上界。
- `AndroidRawReadLease` 在同一 `IFileRef` 对象的活跃解码/内嵌预览之间共享文件字节，最后消费者释放即撤登记，无常驻文件缓存。取消只退出自己的等待，最后消费者撤销才中止读取；文件字节和原生工作缓冲均进入准入账，P0 放行但记账，后台拒绝即跳过。不缓存 RAW 开发源、不强制代理直接 Full。
- Activity `OnStop` 暂停预取并取消该活跃代际，`OnStart` 恢复并重排邻居与当前 Full 预热；`OnPause` 不暂停，避免系统选择器/浮层导致误切换。`UiHidden` 不伪报内存 Warning；真实内存压力回调和周期探测仍负责修剪。
- `pv-check perf` 使用隔离设置，覆盖同步/后台色彩准备、GPU 像素、旧请求/表面重建/租约、绘制合帧、纹理字节预算、后台预取与 RAW 共享读取；真实 ARW 放 `files/pv-check-raw/`。`pv-check background` 可单跑预取活跃闸，真实 HOME→返回的生命周期另由 ADB 操作核对。

## 主题与资源

- `Theme.PhotoViewer.Native` 继承 `Theme.Material3.Dark.NoActionBar`，颜色全部走 `values/colors.xml` 自有常量（`native_background` 等），**不调用 DynamicColors**、不引用 OEM 主题派生属性。bootstrap 与原生 Activity 共用该主题。
- `AndroidManifest.xml` 已移除 `MANAGE_EXTERNAL_STORAGE` / `WRITE_EXTERNAL_STORAGE`；`READ_EXTERNAL_STORAGE` 仅 `maxSdkVersion=32`（保留：API ≤32 上 `file://` 外部打开与 `--pv-open` 本地路径靠它读取）。33+ 走 SAF。不依赖 `Xamarin.AndroidX.Core.SplashScreen`（未使用）。
- 控制栏图标字体 `uicons-regular-rounded.ttf` 与实心字形 `uicons-solid-straight.ttf` 作为 `AndroidAsset` 打进 `assets/Fonts/`，与 Web/桌面共用同一字形。
- AI 版的 DINO 模型与 `DINOv3-LICENSE.md` 作为 `AndroidAsset` 打进 APK（模型约 +83MB），原生资源提供器按 `avares://PhotoViewer/Assets/Models/...` 路径部分读取；标准版不包含这些资产。

## 构建

默认 SDK 10.0.402 **没有** android workload；10.0.201 下已装（36.1.53）。不要改仓库 `global.json`（Apple 头需要 10.0.402 的 27 工作负载）。在任意临时目录放 `{"sdk":{"version":"10.0.201","rollForward":"disable"}}`，从该目录构建（标准版示例如下，AI 版改传 `true`）：

```bash
cd /tmp/pv-sdk201 && dotnet build /path/to/PhotoViewer.Android/PhotoViewer.Android.csproj -c Debug -p:PhotoViewerEnableDino=false
```

长期建议 `sudo dotnet workload install android` 到 10.0.402。模拟器：`~/Library/Android/sdk/emulator/emulator -avd pv-api35`（Apple Silicon 必须 arm64-v8a 镜像）。

发布统一用 `publish-android-apk.sh --edition standard|ai|both`（Windows 为同名 `.ps1 -Edition …`），脚本自动选择具备 android workload 的 SDK，只构建 arm64-v8a，并调用 `Tools/check-android-package.py` 扫描实际 APK 的身份、显示名、版本、ABI、模型/License 与托管/原生推理组件；`--local` 产物写 `artifacts/scratch/android-local-publish/`，正式产物写 `release/packages/`，报告写 `artifacts/acceptance/android-editions/`。扫描器用 `python3 Tools/check-android-package.py --self-test` 做正反判定自检。

原生库（两个脚本各自交叉编译，产物入库，`NativeLibs/arm64-v8a/`）：

| 脚本 | 产物 | 说明 |
|---|---|---|
| `build-native-android-raw.sh` | `libpvraw.so` + `pvraw.txt` + `pvraw-LibRaw-LICENSE.txt` | LibRaw 0.22.2 静态编入（仅 arm64-v8a），`-O3 -fopenmp -static-openmp -static-libstdc++`、16KB 页对齐、只导出 `pv_raw_*`；LibRaw 双许可 LGPL-2.1/CDDL-1.0。脚本内校验导出符号集合、`NEEDED ⊆ libc/libdl/libm/liblog`、`LOAD` 对齐 0x4000。 |
| `build-native-android-hevc.sh` | `libpvhevc.so` + `pvhevc.txt` + `pvhevc-FFmpeg-LICENSE.txt` | 静态 FFmpeg（仅 HEVC 解码器，LGPL）+ 薄 C 封装，供 `FastHeifDecoder` 并行解瓦片。 |

两者都缺 `.so` 时对应路径自动回退（RAW 停用并记一次日志 / HEIF 回落共享 libheif），不阻塞构建。

## 程序化检查

```bash
# 夹具：adb push 到 /data/local/tmp，再 run-as 复制到应用内部 files/pv-fixtures
adb push <本地夹具目录> /data/local/tmp/pv-fixtures
adb shell run-as cc.xiaojh.PhotoViewer cp -r /data/local/tmp/pv-fixtures files/pv-fixtures

# 启动检查（@files/ 解析到内部 files 目录，避开 /sdcard/Android/data 直读限制）
adb shell am start -n cc.xiaojh.PhotoViewer/.AndroidBootstrapActivity \
  --es pv-open @files/pv-fixtures --es pv-check main

# 报告：files/PhotoViewer/android-checks-<mode>-<standard|ai>.txt
adb exec-out run-as cc.xiaojh.PhotoViewer cat files/PhotoViewer/android-checks-main-standard.txt
```

入口统一是 `--es pv-check <mode>`：报告写 App 内部 `files/PhotoViewer/android-checks-<mode>-<standard|ai>.txt`，首行是 `PASS n / FAIL m`，其后逐条 `PASS/FAIL 名称 — 实际值`。每次运行前 `am force-stop`，避免旧进程/旧报告冒充。检查期间根视图置 `KeepScreenOn` 防休眠（表面重建与回读要屏幕保持点亮），结束无论成败都复位；设备需已解锁且屏幕可见。

**基线模式**（覆盖范围）：

- `main`：主屏分区、安全区、筛选/卡片/控制栏几何、Fit↔100% 与 Full；STATE-01 缓存命中切图（运行时生成大图与小于代理目标的小图两组同尺寸 JPEG，切回时无空图快照、无加载提示、新图同步上屏）。
- `analysis`：分析栏开合与瓦片几何；运行时生成确定性 Sony 对焦 JPEG，覆盖对焦点/中心两张细节瓦片，验证按下立即点亮、两瓦片切换/回切、滑到空白/诊断清除、抬起与取消清除、真实鼠标/触控板悬停、触屏不合成悬停、触控期间悬停不抢占、双击定位独立于高亮；`owner` pointer 多指规则与旧 PointerUp/Cancel 不清新目标；诊断起手/空白起手不得滑入细节自动点亮；诊断点按落准星且滑动释放不误点；隐藏分析栏清除活动目标。
- `settings`：设置 sheet 与工作副本；`AndroidInputDevices` 能力注入（键盘/指针/纯触控），读取真实行视图断言完整绑定控件、纯触控零绑定文字与读屏不泄露；录制/取消（`Esc` 与系统返回键均保留原绑定）/清空/冲突、录制时鼠标左右键被吞、断开最后外设取消录制、连接变化保留草稿且不弹软键盘、虚拟/非字母键盘排除；倍率行单一居中值 + `%`、失焦统一提交、1601/NaN/0 恢复、输入框实例保持、草稿随增删保留、原地重排与至少保留一项。体验项向真实对话框窗口派发触摸：初始 state/top 为半屏收起（外层面板高度 85%、内层布局只占可见半屏）、收起态 ScrollView 高度等于可见区域且真实上滑能在文件页/控制页/EXIF/缩放预设页滚到末尾（末项完整可见）、内容区上滑只滚动内容且 sheet 不动、内容已到顶下滑不收起、标题上拖扩大到 85%、展开后重新收起仍可达底部、标题栏拖下关闭；按真实行视图断言单行紧凑行（纯触控控制/EXIF/缩放预设）48dp、标准行（分类/多行格式/显示键鼠绑定的控制行）52dp、分类箭头 20dp 居中与右留白、勾选字形左缘 16dp 且命中区 44dp、勾选→图标→命令名视觉间距 16dp、命令图标 18sp 与评分叠数字语义（读屏忽略 PUA）、外设绑定与多行格式增高不截断，以及预览/控制/EXIF 页面的说明脚注已删除；文件页两条内存提示行按内容换行（长文本量测为多行）、确认文案为「系统内存 / 当前可用」与「可缓存 xx 张适屏照片 / xx 张 3300 万像素照片」、语义级别与配色按 `PerfInfoLevel` 接线、自动口径不告警（`ShowsCacheLimits=false` 时两项均为常规级）且不含 CPU 核数/预载需求/勾号。工具页断言同一初始半屏收起。
- `similarity` / `tools`：面板与工具 sheet 开合；`tools` 另核对工具收起态内层布局等于可见区域、EXIF 长页滚到末尾时末项完整可见。

**独立模式**（精确模式名匹配，不并入 `all`；自带素材、每步独立 try/catch，失败记 FAIL 后继续，例外与超时都写进报告）：

- `edition`：对当前实际构建核对 DINO/相似类型是否存在、筛选条末项、分析栏瓦片、诊断入口与实际出图、设置页版本差异及 DINO/CV 独立开关接线；标准版不残留 AI 类型或入口，AI 版反向确认能力完整。
- `roundtrip`：另传 `--es pv-roundtrip prepare|standard|ai`，依次执行 AI → 标准 → AI 覆盖安装往返。三阶段共用隔离设置档 `edition-roundtrip` 与 App 内部隔离库 `files/PhotoViewer/edition-roundtrip/photos.db`，真实生成 CLS/patch/CV，逐项核对设置哨兵、相似面板展开态、特征 SHA-256、model_id、CV spec/尺寸；标准版忽略展开态但不清值，通用设置修改可写，最终 AI 版全部恢复。报告为 `android-checks-roundtrip-<standard|ai>.txt`，按阶段追加，不用旧 PASS 冒充本轮。
- `faults`：无法在日常浏览中自然触发的失败与边界项——IMAGE-08 八方向（运行时生成 320×200 + EXIF 0x0112 = 1..8 的 8 张 JPEG，断言解码方向、视口方向与 5–8 的逻辑宽高互换）、SCOPE-04 RAW 全分辨率（`files/pv-check-raw/*.ARW`，源 ≥6000×4000，放大到物理 100% 后展示尺寸 = 源尺寸）、TOOLBAR-03 外部存储评分写回（`GetExternalFilesDir` 下 800×600 JPEG，XMP 字节回读 = 4）、RATING-04 冲突与来源浮窗（内嵌 2 + 同名 sidecar 4 的 JPEG：卡片/控制栏三角、卡片三角打开浮窗且不写值不切图、锚点/宽度/方向与两来源条目内容；点浮窗内任一值后两来源磁盘真值收敛且浮窗关闭；控制栏 0 号打开当前照片组并向上展开；只读冲突素材下写入失败保留浮窗且评分不变）、IMAGE-03 Full 失败（反射换 `BitmapLoader._presentationDecoder` 为「仅全分辨率失败」替身，断言保留代理 + 提示条含「全分辨率」）、STATE-02/OPEN-03（`ParentFailingFileRef` + `FailingFolderRef`：文案含目录名、回退单图、列表只剩 1 项）、STATE-03（只读副本 444/555，星级不变 + 提示条含文件名）、SETTINGS-14（隔离档 `checks` 写入损坏 JSON，断言 `.corrupt-` 备份键存在、共享 `SettingsViewModel.PersistenceStatus` 诊断状态含「损坏」）、SIM-10（反射给 `SimilarItems`/`UnindexedCount`/`IndexError` 赋值，断言列表态停靠区错误标签）、ANALYSIS-10（AI 版注入坏 DINO 模型，只断言 DINO 两瓦片失败、CV 独立成功；标准版验证三瓦片 CV 诊断可现算且不写库）、SCOPE-02（`session.Trim(Warning)` 关闭增强 + 一次性提示）、OPEN-06（`ACTION_VIEW` 外部打开落到同一 `StorageId`）、SCOPE-03（现算/增强产物不入库不写 XMP，显式提取三路入库，清库清空库表且诊断瓦片回「未提取」、抖动徽标消失）、STATE-04（数据库不可用注入：诊断瓦片如实「未提取」、不停留计算中、无伪造徽标且不崩溃，收尾恢复可用库）。主流程另断言 MENU-06（雾天增强文案、逐项前置图标按 `icon.menu.*` 语义与顺序绘制、勾选随状态、无照片置灰且工具仍可用）；`input` 模式按 TOOLBAR-04 逐条核对控制栏虚拟节点的读屏名称是 `名称（主 / 次）`。素材只写 App 内部目录与 `GetExternalFilesDir`；RAW 夹具先 `adb push` 再 `run-as cp` 到 `files/pv-check-raw/`。
- `layout`：记录当前 `fontScale`/density/窗口尺寸/方向/布局模式，然后在竖屏、横屏（`RequestedOrientation` 切向，Activity 不重建）、回竖屏三态各核对一遍：分区非负且在屏内、相邻分区边界相接（列布局控制栏背景从分隔线之后起排）、筛选条在文件栏内容区、控制栏三分组 + 虚拟节点矩形（整条文档超出可视主轴时按 TOOLBAR-08 只要求交叉轴在界内，主轴允许滚动）、首卡 96×144（行）/116×144（列）。字体档与密度不自己改：外部用 `adb shell settings put system font_scale` / `wm density` 改档后重启再跑。
- `input`：经主图 `OnTouchEvent` 派发真实 `MotionEvent`，覆盖放大态拖动（分析栏显/隐均不触发分析重路由）、历史批次越界回原点、多指取消长按、Cancel 后静止长按恢复、长按不平移/翻页/点按、真实双击 Fit↔100%；INPUT-04 合成滚动事件（触控板源 0.4 → 只平移且不派发滚轮命令；鼠标源 1.0 → 恰好一次命令；非整格增量不累积）分流、INPUT-03 带修饰键的滚轮先走用户绑定（Ctrl + 触控板精确事件 → 执行缩放且不平移）、INPUT-05 经 `Activity.DispatchKeyEvent` 构造带 `RepeatCount` 的真实 `KeyEvent`：首下 `Down+0` 翻页、`Down+1` 重复继续翻页、`Numpad3` 的 `Down+2` 只消费不评分、`Action.Multiple` 不冒充长按；键位别名（`Enter`/`NumpadEnter`→`Return`、`Del`→`Back`、`ForwardDel`→`Delete`、小键盘 `+ − . * /`）与系统返回键不产生绑定、主行 `Num3` 的评分绑定；TOOLBAR-11 控制栏虚拟节点（名称非空、评分按行 0→5 / 列 5→0、命令名与 `Commands` 对齐、矩形非零）与硬件键盘命令（`F` 切适应、`DpadRight` 翻页，同走 Activity 入口）。
- `storage`：真实 `content://` 检查，经仅 Debug 注册的 `DebugTestDocumentsProvider` 覆盖文档身份、嵌套目录、缺失 size/mtime、外部改动后属性刷新、写入后新鲜 revision（属性与下一个会话都给新尺寸）、可定位/管道读取、随机读写、JPEG 内嵌 XMP、ARW 无 tag 700 拒绝内嵌写入且不建 sidecar、ARW tag 700 写内嵌且不创建 sidecar、已有 sidecar 随整组统一写入、sidecar 删除后不再作为来源、只读预检零写入（能力 `Unsupported` 且在打开写入会话前拒绝）、不可定位打开即 `NotSeekable`、原子替换拒绝，以及 Provider 拒绝访问故障注入；`DebugTestMediaProvider` 提供数字 documentId 的通用 content URI，经真实 `ACTION_VIEW` 断言打开用 `_display_name` 而不是 Uri 末段；文件选择器回调（`BeginFileRequestForChecks` 注册等待项后直接投递 Activity 结果，不启动 DocumentsUI）覆盖后台显示名准备、新请求取代旧请求时旧结果不串写、取消/销毁后迟到回调不复活。`ACTION_VIEW` 只验证同 UID 打开与真实读取、不伪造持久授权；不覆盖跨 UID 临时授权、系统授权撤销、DocumentsUI、USB/MTP/云 Provider。报告为 `android-checks-storage-<standard|ai>.txt`。
- `filter`：筛选条三个自绘下拉的存活与锚点语义，全部走真实 UI 入口（点按派发给控件自身命中链、选项点弹出窗里真实行视图）。每个下拉打开后跨三个 2s 周期（> 4.5s）核对仍是同一个 `PopupWindow` 实例，重复 `RequestLayout`、重复相同 `Arrange` 都不收起；无关设置变更（如缩放指示器）不收起；行选择写回共享筛选 VM 且下拉关闭；栏位隐藏、旋转分别收起失效锚点；竖屏行布局上按 `root` 的真实调用序列跑「视口宽 → 文档宽」两次 `Arrange`（中间态不收起），窄屏（外部 `wm density 525` → 329dp）再用真实拖动断言筛选条可滚、滚动偏移跨三次重建保留且下拉仍开、再次滚动收起。非窄屏档只记录几何并跳过滚动步（报告写「非窄屏跳过」），窄屏档必须实跑。
- `color`：主图色彩管理回归，**需广色域真机**（模拟器协商回退 sRGB，不算 P3 验收）。HEIF 最终回落另有回归：强制共享 libheif 回落（跳过快速路径）在设备目标色域 / Display P3 / sRGB 三个出口上逐平坦色块对照主图解码入口，`AndroidHeifDecoder` 兼容入口（现为共享 libheif wrapper，不走 `BitmapFactory`）必须出 `Bgra32Srgb` 的 sRGB8 且色块与共享出口一致，把夹具 `nclx` 改成 PQ/HLG 后真实解码入口必须返回 null 并记 HDR 原因，`irot` 非 0 时回落产物必须是显示朝向（方向 1）、源几何同步互换且色卡四角按逆时针 90° 轮转（改字节的临时夹具写在 App 内部 `files/pv-color-fixtures-generated`，产品路径不使用）。断言全部读真实渲染结果（重建过的 EGL 窗口表面 + 默认帧缓冲回读）或真实解码产物：可用的 P3 passthrough（`0x3490`）优先路径字节直通、强制标准 P3（`0x3363`）时按 sRGB 编码帧缓冲的着色器不得重复编码（128 灰回读 188 为错、128 为对）、强制 sRGB（`0x3089`）表面的显式转换、sRGB 源在 P3 表面必须转换、P3↔sRGB 重复重建与预乘数据保持、`Bitmap` 原始 P3 存储直读、导航图 sRGB 出口与方向，以及真实夹具对照共享 `SkiaImageCodec` 参考解码。上传计时回归另核转换与同色域直传、跨色域源回切不重转；6000×4000 P3 全幅图在强制 sRGB 表面与 P3 重建表面上分别记录同步提交/转换/GL 分段，并在计时外做 GPU 像素对照，不将提交墙钟冒充端到端呈现时间。`AndroidNativeChecks.ColorTransform` 另将这张全幅图的分带转换与单次串行 `SKPixmap.ReadPixels` 按所有有效像素逐行逐字节比较，预热后分别取 3 次转换中位值，参考与产物租约均在检查内归还。夹具取 `Fixtures/jpeg/color-{srgb,p3,adobe,untagged-adobe}.jpg`、`Fixtures/heif/color-{srgb,p3}.heic`（有 `color-gray.heic` 则另核中性保持）与 `Fixtures/jpeg-large/color-p3-large.jpg`；`adb push` 到 `/data/local/tmp` 后 `run-as cc.xiaojh.PhotoViewer cp` 进应用内部 `files/pv-color-fixtures/`。以 `--ez pv-srgb true` 或 `--ez pv-p3 false` 强制 sRGB 作对照，默认按设备与窗口广色域能力走 P3。
- `zoom`：26MP 合成图（含方向 6）及真实 HIF 的 2496 代理→6192 Full 升级，计时同步纹理提交、动画间断与真实加载会话 UI 心跳；回读 GPU 的首尾、原分块边界两侧与行中像素，逐通道对照源字节，另验额外行距与纹理缓存；顶点缓冲检查覆盖八方向实际绘制、非对称四象限 GPU 回读、缩放平移更新及同一 renderer 两轮 EGL 重建后缓冲身份与分配次数保持。真实 HIF 副本放 `files/pv-check-zoom/` 或 `GetExternalFilesDir(null)/pv-check-zoom/`，缺失如实 FAIL。`zoom` / `input` / `perf` / `background` 使用隔离设置档，并将逐条报告写入 `PhotoViewerChecks` Logcat 标签，正式签名非 debuggable 包也可读取；`input` 另覆盖积压 Down、Down 后真实阻塞主线程再送积压 Move/Up、候选过期后恢复正常长按，以及动画中途触摸接管。像素回读、日志与合成素材仅检查模式执行，不进入普通浏览路径。

**矩阵维度**：fontScale、density、竖屏/横屏及回竖屏分别验证；当前结果与未覆盖边界见 [Design/status.md](../Design/status.md) 及对应计划。验收读取用的访问器（`StatusTextForChecks`、`StatusVisibleForChecks`、`ResetStatusForChecks`、`ControlBar.NodesForChecks`、`ControlBar.RatingConflictForChecks`、`ControlBar.TryGetRatingSlotScreenRect`、`ThumbnailCard.ShowsRatingConflict`/`StarSlotRectPx`/`ActivateSlot`、`RatingSourcesPopup`（`DirectionForChecks`/`AnchorRectForChecks`/`SlotRectPx`/`ContentViewForChecks`/`WidthPxForChecks`）、`SimilarityPane.DockError*ForChecks`、`FilterScrollableForChecks`、`FilterScrollOffsetForChecks`、`AnalysisHostForChecks`、`FilterBar`（`IsDropdownOpen`/`DropdownInstanceForChecks`/`DropdownKindForChecks`/`DropdownAnchorForChecks`/`DropdownContentViewForChecks`）、`FilterScrollHostForChecks`、`AndroidFilePicker.BeginFileRequestForChecks`/`FileRequestCode`、`AndroidSettingsSheetView`（`FileInfoLabelsForChecks`/`LevelColorForChecks`））都是 `internal` 且注释标「验收用」，产品路径不引用；`faults` 另用反射改 `BitmapLoader` 私有静态解码器字段与 `SimilarityPanelViewModel` 私有 setter，退出前一律还原。

### 启动与 UI 长尾排障

- Debug 保持 `EmbedAssembliesIntoApk=true` 并保留 Debug 符号。AI 版 Debug 显式使用 `AndroidUseAssemblyStore=true`：运行时 36.1.53 的逐程序集 DSO 加载路径会在映射完成前把共享 `data_size` 从 ELF 长度改为载荷长度，并发懒加载可能按缩短长度再次映射并越界读取 ELF 节表；竞争失败分支随后会把污染长度传给 `munmap`，已实测一次调用解除约 1.74GiB 范围并覆盖大量无关原生库的可执行映射。assembly store 不走这条映射路径。标准版在缺少 DINO 程序集的 assembly store 布局下会于 ReactiveUI/后台类型初始化阶段持续等待，因此 Debug 与 Release 均显式 `AndroidUseAssemblyStore=false` 走逐程序集 DSO；该差异须通过两版冷启动与原生回归守护，不得互相照搬。
- `AndroidApp` 将进程域与 Android 未处理异常写到 `PhotoViewerCrash`，只记录、不标记已处理。原生 SIGSEGV / SIGABRT / `EXIT_SELF 255` 分别保留当次 Logcat 和 `dumpsys activity exit-info <应用ID>`，不能因时间接近就合并根因；没有托管钩子日志也不单独证明故障属于原生。
- Android 启动时将 `YuvImageConverter.MaxDegreeOfParallelism` 配为 `max(1, CPU 核数 - 1)`，并将 `ConversionTaskScheduler` 注入同等并发上限的进程级 `ConcurrentExclusiveSchedulerPair.ConcurrentScheduler`。多幅 YUV 转换共用执行槽，单图可用完整转换预算，不按图像上限静态均分；图像上限另不超过 `max(1, CPU 核数 - 1)`。这是 CPU 并发策略，不改变像素算法、Full 请求时机或精度；未配置的平台保持原行为。
- `zoom` 的真实 HIF 放大段开启 `AndroidFrameStats` 固定容量内存采样，平时关闭。报告区分 DrawFrame 等锁/MakeCurrent/GL 命令/Swap、根视图布局/会话/视口、调度回调与 Looper 分发，按最大心跳间隙的时间区间匹配样本，另记该间隙的 GC 增量与 UI 线程 CPU 时间；各类存在嵌套，不跨类相加。消息日志仅检查期间安装，结束清除；它自身有 JNI/字符串开销。上传墙钟不包含 Draw/Swap，不能单凭上传快就排除渲染阻塞。`zoom` 另按所有有效像素逐行对照有界并行与串行 Full，核对色彩、几何与方向不变。
- 独立模式的 `[NativeCheck] 独立模式开始/<步骤>` 日志用于区分启动与检查内失败；当前复现、验证范围与历史尚未归因的异常见 [Android 性能与内存专项方案 2](../Plans/android-performance-memory-plan-2.md)。

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

## 工程约束与排障要点

- APK 打包前从 `FrameworkNativeLibrary` 移除 `runtimes/linux-arm64/native/*.so`（csproj `_PvRemoveLinuxOnnxFromApk`），只保留 AAR 的 bionic `libonnxruntime.so`。
- `AndroidAsset` 落在 `assets/…`；资源提供器解析 `avares://PhotoViewer/Assets/...` 前去掉 `Assets/` 前缀。
- 邻居 Full 预取须跟随实际需求并受预算准入（适应态只取代理，放大或分析栏可见才取 ±1 Full）；无约束地并发解 Full 会原生崩溃。
- 导航图缩略图取当前展示产物抽样，不做 GPU 回读，也不单独走 `BitmapFactory`（HIF 也可出图）。
- 模拟器（ANGLE）EGL P3 协商总回退 sRGB；P3 结论只能在广色域真机上得出。
- `EGL_GL_COLORSPACE_DISPLAY_P3_EXT` 是 `0x3363`（`0x30BF` 会 `EGL_BAD_ATTRIBUTE`）；窗口须 `android:colorMode="wideColorGamut"` + `Window.ColorMode`；P3 表面上着色器是否先线性化按帧缓冲实际编码决定，避免重复编码。
- `Bitmap.GetPixels` 返回 sRGB 打包值；读 P3 像素须 JNI 原始存储直读并按实际色域规范。
- `IMenu.Add(title)` 的 itemId 恒为 0；菜单项必须显式唯一 id 并共用单一点击处理。
- `FileContent` 已含 `safe.Top`，列布局筛选条只传 `FilterMargin`，不再叠加 `leadingInset`。
- 卡片项宽行布局 96、列布局 116；相似面板列布局交叉轴用 `PanelWidth(106)`。
- 主图长按菜单与筛选下拉是屏幕坐标下的自绘 `PopupWindow`（夹取在屏内，超高可滚），不用 `PopupMenu` 或相对 `_photo` 的坐标。
- `SurfaceView` 尺寸变化会先交一帧旧尺寸缓冲：`OnSizeChanged` 保留 `CenterUV` 并补帧重绘。
- 控制栏必须在 `OnDraw` 内补齐 `Arrange`（dirty/空分组时只 `Rebuild` 会画出空容器）。
- `ApplyFilter` 须把被同名合并隐藏的 `CurrentFile` 映射回组代表。
- HIF 缩略图：优先容器 JPEG/`thmb` 项；不同缩略项存储方向可能不同，按「原始横竖 vs 显示横竖」决定是否旋转；元数据尚未给出方向时从文件头 `irot` 兜底；`OutputPreRotated` 必须如实。
- `KeyEvent` 主行数字 `Num0–Num9` 须映射到 `D0–D9`（`Enum.TryParse("Num3")` 失败）。
- 窄屏筛选条由可滚动宿主承载，控件宽度不压缩。
- 非整格滚轮增量按精确滚动平移，不累积成命令。
