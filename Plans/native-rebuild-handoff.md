# 原生重构交接文档(handoff)

> **给在 Mac 上接手的 agent**:本文是重构的当前状态真源 + 开工指引。写入规则:每次阶段推进后更新"当前状态"与"验证矩阵"。
> 最后更新:2026-10-18(Windows 线已合并回共享线：头工程改名 `PhotoViewer.Windows`，W-M5 设置窗口、Win11 视觉重构与工具窗口随之进入共享线；iPad 原生壳 I1–I5 已落地并完成模拟器 + 真机程序化验收，I6 文档收尾完成；Windows 原生壳已同步共享 `c0567c6`，`--pv-native-smoke` 24 项通过，设置/工具专项各 7/5 项 PASS)。作者手验与发布通道仍在作者手上。

## 1. 为什么重构(决策语境)

PhotoViewer 正在从 Avalonia 迁往四平台原生壳 + 共享 C# Core。驱动与决策全录于 [native-rebuild-master-plan-1.md](native-rebuild-master-plan-1.md)(总纲,含三大方向决策:全平台原生 / 只上 iOS App Store / Core 先行);色彩根因实证在 [color-managed-native-rendering-plan-1.md](color-managed-native-rendering-plan-1.md)(已被总纲收编为色彩专章)。

一句话:**色彩管理、切图性能、iPad 内存、UI 原生感、App Store 上架五个问题共用一套底层重构来根治**,不打补丁。阅读顺序:master plan → phase-0 清单(含发现清单,很多坑在那里)→ 本文。

## 2. 当前状态

- **Windows 线并入共享线（2026-10-18）**：`native-rebuild-win` 的 W-M5 设置窗口、设置页 Win11 视觉重构、工具窗口/共享诊断瓦片与头工程改名（`PhotoViewer.Desktop` → `PhotoViewer.Windows`）已合并到 `refactor/native-rebuild`；冲突仅根 `AGENTS.md` 与 `Design/ui-contract.md`（保留 iPad 手势/验收更新，同时采用 Windows 新命名与设置/工具条文）。合并后在 Windows 本机复验：Debug 构建 0 错（仅既有 NU1903）、`--pv-native-smoke` **24/24**、`--pv-native-settings` **7/7**、`--pv-native-tools` **5/5** PASS。共享层的 `PhotoLoadSession`/`ImageEnhancer`/`ColorEngine`/`FolderViewModel` 有 iPad 侧改动，**Mac/iPad 需在 Mac 上补一次回归**。后续 Windows 改动仍走 `native-rebuild-win`。
- **Windows 工具窗口落地（2026-10-17，P3 收口）**：按作者口径"参考设置窗口的做法和结构"把 EXIF 详情 / 照片数据统计 / DINO 诊断做成独立 Win11 工具窗口：960×720（min 800×560、不进任务栏）、Mica + `TitleBar`（扳手）+ `NavigationView` 三分类 + `SettingsCard`；EXIF 7 列 + `SelectorBar` 多格式，DINO 直接消费共享 `DinoDiagnosticPresenter`（5 瓦片按 3+2 同宽网格、第六格留空 + 全宽刚体拟合文本），照片统计用工具窗口自身句柄的文件夹/CSV 选择器。入口为主图右键「工具…」与控制栏 EXIF 块点击；导航状态复用共享 `ToolsViewModel`，单例由 `WindowsNativeToolsPresenter` 管理。顺带抽出共享 `WindowsDiagnosticTile`/`WindowsDiagnosticBitmap`/`WindowsShakeFieldView`（分析栏改用矢量叠加，不再 Skia 光栅化）与 `WindowsWindowChrome`（设置/工具共用 DPI+caption）。验证：`--pv-native-tools` **5 项 PASS**、`--pv-native-smoke` **24 项**、`--pv-native-settings` **7 项**无回归；150% 截图 `tools-page-{exif,dino,stats}.png`。真实多格式切换/文件夹与 CSV 选择器/视觉手感待作者手验；详见 [Windows P2-W §18](native-rebuild-windows-p2.md)。

- **Windows W-M5 设置窗口落地 + Win11 视觉重构（2026-10-17）**：按 [native-rebuild-windows-settings.md](native-rebuild-windows-settings.md) 实现。首次落地误判 `NavigationView`/`TitleBar`/`NumberBox`/`TextBox`/`InfoBar` 不可用，改用 ListView/Slider/自绘 Border；复核发现根因是缺根 `App.xaml`/`IXamlMetadataProvider`，补 `XamlControlsResources` 后回到方案原结构，并完成 Mica + `TitleBar` + `NavigationView` + CommunityToolkit `SettingsCard` 视觉重构（详见 [Windows P2-W §17](native-rebuild-windows-p2.md)）。`--pv-native-settings` **7 项 PASS**、`--pv-native-smoke` **24 项无回归**；真实拖拽排序、真实键鼠录制、清库确认、预设增删待作者手验。

- **Windows 同步共享分支（2026-10-17，`native-rebuild-win-sync`）**：Windows 原生壳并入共享基线 `c0567c6`（Windows 壳 `b0caf32` 与共享基线的合并）。分析栏改为实现共享 `IAnalysisHost`、消费 `AnalysisPresenter`/`AnalysisTile`，细节预览用共享 `DisplayCrop`，直方图由 presenter 的 `HistogramRenderer` 生成，Windows 侧不再自持分析数据链与即时计算编排；增强预览下移 `PhotoLoadSession`（跨切图持续、压力 Warning 自动关闭）；主图解码改用共享四参 `DecodeAsync` 并注入 `WindowsPresentationEncodingPolicy`（恒 RGBA16F 线性 scRGB）。保留 Windows 分支对共享层的修复（`PhotoLoadSession` 取消重置、`ImageEnhancer` 元数据保留、`SkiaImageCodec.FromSkBitmap` 源尺寸、`BitmapLoader` 在途取消重试、`ColorEngine.ToScRgb16` 元数据重载）。`--pv-native-smoke` **24 项全 PASS**（150%，新增 NumberPad3→SetRating3、方向 6 `DisplayCrop` 象限、方向 6 增强帧朝向、增强帧诊断重算）。**待办**：W-M7 色彩/RAW、200%/175% DPI、真实跨屏、真实相机八方向素材、设置真实拖拽/录制手感、W-M6 专项 harness 与旧壳几何 JSON（工具页已于本条之后的 2026-10-17 轮次落地，见上）。细节见 [Windows P2-W §16](native-rebuild-windows-p2.md)。

- **Windows 主屏视觉细节修复（2026-10-17 第五轮，`native-rebuild`）**：作者回报五处 UI 细节（续报三轮）。①**缩略图盖掉「已缓存」浅蓝描边**——描边原画在槽位 Border 上，80 宽图片按 Uniform 铺满后左右两边被覆盖；改为独立一层压在图片之上。②**分析栏瓦片内容自带圆角 + 拉满方形**——诊断瓦片与细节预览各套了一层 `CornerRadius=5` 的内容 Border（移植旧壳"外框 6 + InnerClip 5"），150% 下呈"卡里套卡"；改为内容贴外框内沿、圆角只由外框裁切产生；作者二次回报"分析图应是原比例置入"，于是诊断位图不再强制 512×512 方形，改按 `entry.AspectRatio` 生成非方形（长边 512，抖动箭头 cellX/cellY 分开算），瓦片里 `Stretch=Uniform` 居中留边，准星与点击 uv 走同一 `_aspect` 推的 `ContentRect()`。③**分析栏首开偶发"挤成一坨、大小错位"**——原实现"读 `ActualWidth` → 推迟回调 → 回写每个瓦片 `Width/Height`"，那一拍若赶在布局给出宽度之前读到 0，瓦片就退回各自内容尺寸且事后不保证再有 `SizeChanged` 纠正；新增 `WindowsAnalysisTilePanel` 在 Measure/Arrange 里直接排等边长条带，布局回调里不再改子元素尺寸与位图。④**绿框每换一张照片都亮**——`SetImage`/`SetCenter` 结尾无条件推高亮，而换图必重建裁剪图；改为瓦片自持悬停态，只有指针真在预览瓦片里才推，`ResetHover()` 兜住面板收起拿不到 `PointerExited` 的情况（与旧壳"仅高亮态才推"同口径）。验证：0 错 0 警、冒烟 **23 项全 PASS**（含两向"瓦片等边长"断言）、150% 实机开合分析栏 4 次尺寸一致、原比例留边与准星落点逐像素核对、绿框四步（开面板→换图→悬停→离开再换图）符合预期。细节见 [Windows P2-W §15.6](native-rebuild-windows-p2.md)、契约 `ANL-02`/`ANL-03`/`ANL-04` 与 `FILM-05`。

- **Windows 主屏 P3 收尾修复（2026-10-17 第四轮，`native-rebuild-win`）**：作者回报"提取特征后点相似聚类开关卡死"，实机复现为**进程直接消失**（WinUI `0xC000027B`，WER HRESULT `0x80131509`）。根因：旧壳 `AnalysisViewModel` 在原生壳里照常订阅并在展开分析栏时用 `DecodedImageAdapter` 建 **Avalonia 位图**，而原生壳不初始化 Avalonia 渲染接口，`InvalidOperationException` 抛在 WinUI 回调里跨 COM 边界即退进程（空库不崩，因为有数据才会建位图）。修复：`AnalysisViewModel` 在 `UsesNativePresentation` 下完全惰性；同时修掉两处卡死诱因——`PhotoDatabase` 每条连接重复写 `PRAGMA journal_mode=WAL` 导致并发抢库锁、`SimilarityService` 一候选一 `Task.Run` 压满连接池与 UI 队列（改 `Parallel.ForEachAsync` 并有界并发），并顺带剔除相似列表里 100% 相似的伴侣文件。诊断手段：`NativeStartupLog` 增加 `FirstChanceException` 留痕 + 相似面板工厂/分析栏可见性的 COM 边界兜底。验证：0 错 0 警、冒烟 **23 项全 PASS**、345 张（已提取）与 1395 张图库实机压测存活。细节见 [Windows P2-W §15.5](native-rebuild-windows-p2.md)。

- **Windows 主屏 P3 入口落地（2026-10-17 第三轮，`native-rebuild-win`）**：解除 `SCOPE-01` 对 Windows 主屏的限制——**分析栏**（细节预览/直方图/DINO PCA/Cosine/锐度/抖动、准星与 cosine 重算、双击对准主图、悬停绿框）、**相似聚类**（筛选条开关、相似项分数卡片、空态/提取全部/进度、点选保留锚点；聚类只在面板展开时计算）、**增强预览**（CLHE 增强帧上屏，可切回原片）全部原生落地；控制栏恢复「分析栏/增强预览」两个命令按钮，右键菜单补「分析栏」勾选。`--pv-native-smoke` 扩到 **22 项**（新增相似聚类三态、分析栏六瓦片/即时计算/准星、增强开关），150% 实机连续多轮稳定。已知差异与仍待验见 [Windows P2-W §15](native-rebuild-windows-p2.md)：分析诊断固定读原片（增强图 CV 路径在冒烟中复现崩溃）、抖动瓦片用 Skia 光栅化箭头；该轮因缺 `App.xaml` 误判 `ProgressBar` 不可用而改自绘进度（根因已于 W-M5 修正，控件现可用；主屏仍保留自绘作为视觉定制），工具页当时未原生（设置窗口随后在 W-M5 落地）。

- **Windows 主屏功能对标收口（2026-10-17 第二轮，`native-rebuild-win`）**：把计划 §13.2 里属于主屏的缺口全部落地——**缩放指示器导航图**（150 长边缩略图 + 2 px 白色可见框 + 百分比，Skia 直接缩到导航尺寸，不吃指针）、**FILM-03 用户接管 + 系统减少动态效果**、**FILM-06 星级写回失败窗口内可见**（共享 `MainViewModel.RatingWriteFailed` + 底部 4 s 状态提示）、**外部打开/拖放文件夹**、**最大化/全屏/退出全屏闭环**、卡片 `DisplayName`、第二行完整时间 tooltip、右键菜单"缩略图栏"文案、空筛选结果状态；同时修掉"列表重建清掉排队定位请求导致外部打开非首张停在列表头部"的回归。`--pv-native-smoke` 从 13 项扩到 **19 项**（150% 实机连续多轮稳定）：导航图几何/百分比/设置开关、**自生成八方向 EXIF 素材**（方向 + f/2.8 + 1/250 + ISO 400 + 75mm）在主图交换链四角回读与导航图四角采样逐方向核对、FILM-03 两条分支、外部打开定位、写回失败提示、窗口四态往返。导航图实现与 §6 W-M2 原文（"同一交换链内合成"）有一处偏离，理由见 [Windows P2-W §14.2](native-rebuild-windows-p2.md)。仍待验：200%/175% 缩放、真实跨屏、真实相机素材、手感、W-M6 专项 harness；W-M5 设置窗口随后已落地。

- **Windows 四分区 + 对标纠偏（2026-10-16 首屏 / 2026-10-17 纠偏，`native-rebuild-win`）**：WinUI 3 + D3D11 四分区垂直切片，并在 150% 缩放下与旧壳同尺寸逐项对拍。本轮修掉五类根因:自备 `app.manifest` 漏 DPI 声明导致进程 DPI-unaware（整套 UI 被系统位图拉伸）、文件选择器扩展名模式被补点成非法形式致「打开文件」静默失效、文件栏 `Border.Width` 含边框致卡片被压窄 + 卡片重复叠外边距致项距 150、缩略图未限幅撑出 80×80 槽位、控制栏 EXIF/评分组间距与底色失配；并按要求把标题栏改成**自绘四按钮 + 50 DIP 热区自动显隐**（覆盖计划 §9 待确认项 1 的「保留系统按钮」推荐值）。`--pv-native-smoke` 现覆盖 3 尺寸 × 2 方向的自动显隐 caption 契约（隐藏态零拖动区、显示态不压按钮），150% DPI 实机通过；200% DPI、实际跨屏、系统色彩、鼠标手感与八方向仍待验。已完成 / 待完成总表见 [Windows P2-W §13](native-rebuild-windows-p2.md#13-已完成--待完成清单)，逐项对拍见 §12。

- **分支**:`refactor/native-rebuild` = Mac/iPad 共享集成线(远端已推送;`main` 保持可发布,未合并);`native-rebuild-win` = Windows 专用线(自 W-M5 起),定期从共享线快进/合并,**Windows 改动不再直接提到共享线**;`native-rebuild-win-sync` 为一次性同步分支,已并入 win 后退役。
- **Phase 0(Core 平台中立化,零行为变化)已完成且验收门全关**:Core 零 Avalonia 引用;像素货币 `DecodedImage` + SkiaSharp 像素基座;存储/调度/窗口/快捷键/资源五组门面与 Avalonia 适配层齐备;机械门/持久化契约/Mac+iOS 构建/Mac 冒烟/DINO 口径全部通过(细节见 §3 矩阵与 phase-1 §5 发现清单)。里程碑提交 M1→M6 逐笔可查。
- **Phase 0 定向审查(2026-10-12)已通过**:Phase 0 由同一 agent 实现+自审(mimo 超时),本次补独立审查——机械门复核 PASS(Core 零 Avalonia,残留均注释;VM 零 UI 耦合);手势持久化契约 PASS(`GestureKey` 223 名 ↔ Avalonia 12.0.2 `Key` 223 名双向 diff 为空,`GestureModifiers` 名/值与 `KeyModifiers` 一致,字符串枚举序列化下逐字节兼容);Mac/iOS 构建 0 错误(仅 NU1903 基线警告)。审查发现的 Phase 1 计划修订已全部落回 phase-1 清单(准入口优先级语义、分档缓冲池、解码路径平台差异、统一 LRU 键前置 M2、验收门平台口径)。
- **Training 仓**:`DatasetBuilder` 已同步适配(独立提交);`docs/HUMAN.md` 有用户自己的未暂存改动,不要动它。
- **Phase 1 已完成**(M1 Governor 骨架 / M2 统一 LRU+压力矩阵 / M3 准入+租约+缓冲池 / M4 分级代理解码 / **M5 iPad 真机验收 2026-10-13 全项通过**):验收门 ①500 连翻无闪退恒 Normal ✅ ②P0 延迟 iPad -55%/Mac HEIC -51% ✅ ③告警后 30s 恢复观察期真机实证 ✅ ⑤Core 零 Avalonia/零新增警告 ✅;④ Windows 程序化部分 2026-10-13 过(交互项作者手测中)、Android 待办。M5 副产物:**修复 mono 解释器 ref/out P/Invoke 封送间歇崩溃**(iOS 头工程签名全指针化;`BootTrace` 常驻取证);新增压测驱动器 `StressFlipDriver`(启动参数 `--pv-stress` 系,见 phase-1 §5)。详见 [native-rebuild-phase-1.md](native-rebuild-phase-1.md) §1 验收门勾选与 §5 发现清单。
- **Windows 收尾(2026-10-13)**:P1 定向审查(Windows 路径)3 项发现已修——`SkiaImageCodec.ToBgra32` R/B 映射(harness 逐通道断言 PASS)、Desktop `--pv-stress` 接线、P1-M4a 失准注释;Windows 桌面回归程序化部分过关(`--pv-stress` 18/18 零超时、告警注入级联+恢复观察期日志断言、评分 XmpWriter 读写回环 PASS);缩放/设置/聚类/分析栏交互项 2026-10-13 作者裁定手工冒烟。`MainViewModel.CurrentFile` 切图取证日志常驻(phase-1 §5 有一次性异常登记)。
- **Phase 2 实现与程序化自验已完成，作者产品验收 2026-10-16 通过（性能门除外）**：作者授权直接执行 P2，M1–M4 AppKit/Metal、F16 色彩/代理、胶片条/筛选/评分、手势/快捷键、原生设置、外部打开已落地；Debug 默认原生，Release 默认旧壳。ColorTestTool 23 项、原生色卡 39 项/RAW 副本 17 项/生成 HEIF 14 项通过（含八种 GPU 与分析方向、物理 100% Full、外部打开队列、设置副本隔离与落盘、CV/DINO/指纹三表索引）。四平台构建零错误、无新增编译警告；Mac NU1903、Android 14 条为既有基线。执行清单 [native-rebuild-phase-2.md](native-rebuild-phase-2.md)，产品验收 [native-rebuild-phase-2-acceptance.md](native-rebuild-phase-2-acceptance.md)。系统预览色彩对拍/跨屏与手感已于 2026-10-16 由作者验收通过；大图性能门仍未关闭，RAW 冷图约 1.7–1.9s，小色卡不能替代真实素材 <100ms 指标。分析栏/聚类/工具/增强顺延 P3，入口隐藏且切图不隐式推理。作者已授权先提交，产品验收继续；五张实拍暴露的胶片条布局/滚动/顺序缺陷已修复，五张与十二张多图定向回归通过。
- **P1-M4b 画质/交互修复(2026-10-14,作者实证驱动)**:① 代理 overscan 1.5→**2.0**(1.5× 下"解码降采样+上屏 Mitchell 降采样"双重软化可见;2× 超采样后上屏重采样近乎无损,守画质红线;内存代价由 Governor 准入兜底);② **双击/键盘放大不触发 Full 升级**(根因:`ZoomTo`/`FocusOnImagePoint` 先改 Scale、后落 `Fit=false`,`Scale` 订阅见到 `Fit==true` 直接 return;双指缩放因连续事件第二次起 Fit 已 false 才正常。修复:升级触发改听 `Scale`+`Fit` 双属性)。Mac 真屏冒烟:ARW 代理 4180×2787(=2× 视图物理像素)、双击 100% → `show full 7008×4672` 热替换、放大态切图 Full 自动接续、回 fit 不再全解码、零异常。两修复均在共享层,原生壳直接继承;上屏最后一级滤波(trilinear/mipmap)已写入 phase-2 P2-M2。

## 2.1 过程决策(2026-10-12 与作者对齐)

- **主界面严格几何对拍（2026-10-15，最新决定）**：作者要求主界面布局结构、尺寸、边距与原 Avalonia 一致；背景按原设计或原生深色，边框/圆角先原生，菜单/字体/字重/颜色接近或更优时保留原生，不机械堆砌控件；设置暂不处理。此要求优先于下文早期“近似”约定。本轮校正卡片外框与占位、EXIF/评分主轴边距、筛选计数自然宽度、短横向列表居中和导航图长边 150 的等比几何。旧壳/原生隔离驱动器生成同配置两向三尺寸实际几何；验收结果见 P2 清单，最终视觉仍须作者确认。

- **设计文档与工具栏对拍（2026-10-15，早期记录，几何宽松要求已被上条替代）**：作者明确 UI 文档现阶段是对账草稿，重要约束与原生表现待分类，不能把旧版尺寸/参数冻结。工具栏真屏对拍发现三分组丢失、EXIF 信息层级丢失、竖排评分倒置和固定高度按钮边框；新增 MacControlBar 恢复整体分组换行/换列、居中与信息块，按钮采用可变高度原生边框，同时修复显隐/排序刷新、苹果主次绑定提示及评分快捷命令星数。P3 入口保持延期。

- **跨平台设计对账（2026-10-14）**：新增 [Design/ui-contract.md](../Design/ui-contract.md) 作为设计真源，使用稳定 ID 记录共同交互、平台差异、缺口及各平台验收状态。对照 Avalonia 纠正 P2 返修中“所有 CurrentFile/列表刷新都居中”的误实现：点选/交换/排序/筛选不居中，显式翻页/打开用 350ms 三次缓出，新请求覆盖并允许用户接管。Mac 显隐恢复/换向定位登记为平台差异；其他平台未做实机验证的项目保持待验，卡片日期/完整时间入口与加载提示缺口已登记。
  - 本轮 12 张色卡与五组实拍 HIF/ARW 定向实跑均 `PASS filmstrip`，两向/升降序及实际筛选菜单回归通过，报告含动画中间帧与终点。Debug Mac 构建零错误、无新增警告（仅既有 NU1903）；触控板/滚动/动画手感与开启系统"减少动态效果"的显式导航已于 2026-10-16 由作者验收通过。

- **Phase 3（macOS 原生缺失功能补全）已完成并经作者复验**：作者 2026-10-16 要求把剩余顺延功能按原 Avalonia 设计重构到 Mac 原生（布局/尺寸/要点/高亮尽量一致；下拉框、右键菜单等小型控件保留原生；工具窗口可仿设置窗口方式）。执行清单 [native-rebuild-phase-3.md](native-rebuild-phase-3.md)。已落地：① 共享层抽出平台中立的 `Core/AI/AnalysisPresenter` + `AnalysisTile` + `IAnalysisHost` 与 `Core/AI/DinoDiagnosticPresenter`，旧壳 `AnalysisViewModel` / `DinoDebugViewModel` 退化为位图适配器（编排单一来源）；② 增强预览下移到 `PhotoLoadSession`（开关跨切图持续生效、压力 Warning 自动关闭）；③ `ImageOrientationMapping` + `DisplayCrop` 统一方向映射与显示朝向 1:1 细节裁剪，`SkiaImageCodec.Crop` 支持 F16；④ `HistogramRenderer` 直接支持 F16 线性表面；⑤ 原生分析栏（`MacAnalysisPanel` / `MacDiagnosticTile` / `MacDetailPreview` / `MacShakeFieldView`）与 `ToggleAnalysisView`、`ToggleEnhance` 命令、右键菜单入口；⑥ 原生相似聚类面板与筛选条 toggle；⑦ 原生工具窗口（EXIF 详情 / DINO 诊断）。
  作者复验（2026-10-16）：色彩 / 画质 / 手感通过；相似面板选中高亮、"提取全部"后瓦片仍显示未提取、toggle 闪烁与点不动等上一轮反馈已修。复验又发现两个缺陷并已修（`Plans/native-rebuild-phase-3.md` §3.2）：① **关闭窗口报"意外退出"**——`MacSettingsNative` 的函数指针要等设置窗口首次展示才赋值，从未打开设置时 `WillTerminate → CloseWindow()` 调用 null 跳地址 0；现在每个导出入口先 `EnsureLoaded()`，且从未展示过时 `CloseWindow()` 直接返回；② **标题栏双击不能最大化**——隐藏式标题栏不自带系统行为，新增窗口级监视器分支按系统偏好 `AppleActionOnDoubleClick` 缩放/最小化/不动作。作者另外判定"切图卡顿（预载/缓存）属共享层问题，本轮不修"。同轮裁定：**Avalonia 旧壳不做产品验收**（它将被原生壳替换），验收只在各平台原生壳上进行；macOS 原生壳的色彩、画质、手感、减少动态效果开启态与真实鼠标拖放（设置页拖拽排序 / 主窗口拖入文件）全部通过。同轮指示：**性能相关（大图性能门、帧率/内存基线复测、RAW 冷图剖面、切图卡顿）一律"以后再做"**。收尾补齐 ui-contract 里 macOS 最后两个"待核验"格子：FILM-05 延迟加载与失败形态（`--pv-native-filmstrip`）、FILM-06 完整真实改星交互与筛选连续性（`--pv-native-toolbar`）。
- **iPad 原生主屏已立项（2026-10-17）**：UIKit + Metal，主屏与 Avalonia 对标，触屏处用原生控件优化；作者已采纳评估推荐（代理 F16 / Full 8bit P3 按预算协商、UIScrollView 驱动缩放、Fit 下滑动翻页等、iOS 17 起、本轮只做 iPad、Mac/iOS 共用 `PhotoViewer.Apple/` 源）。执行清单 [native-rebuild-phase-ipad.md](native-rebuild-phase-ipad.md)。
- **iPad 原生壳 I1–I5 已落地（2026-10-17，HEAD e9ee179）**：`d22403e` + `0a4ad1a` I1 基座（共享源抽取、原生壳启动/存储引用/文档选择器/外部打开，最低 iOS 17）、`41abd7b` + `2245d23` I2 主图（编码协商 + Metal + `UIScrollView`；真机门 500 连翻零超时）、`e1f7cef` ONNX 静态链接修复 + SCOPE-01（切图不隐式推理）、`6f36d8b` I3 主屏布局、`9f20582` I4 输入、`e9ee179` I5 P3 功能。模拟器 iPad mini（A17 Pro）与 iPad Pro 13（M5）两机型六套检查全 PASS（photo 10 / layout 24 / input 27 / analysis 15 / similarity 10 / tools 7）；真机 iPad mini 7（iPadOS 26）跑通 photo / input / analysis / similarity / tools，200 连翻与 500 连翻均零超时。**下一步**：作者按 [iPad 验收清单](native-rebuild-phase-ipad-acceptance.md) 手验（触摸手感、旋转与多任务、键鼠、拖入、色彩），并就 ARW 内嵌预览、CoreML EP、Release 默认通道作决定；分析栏可见压测 p95 2.6s 按"性能以后再做"处理。逐里程碑证据见执行清单 §7。
- **P2 UI 返修（2026-10-14）**：作者指出胶片条自动居中缺失、控制栏右侧空带、设置浅色/四周白边及排序/快捷键结构草率。本轮恢复主轴居中（首尾受边界约束），主窗口滚动条统一 overlay、控制文档匹配裁剪宽度；设置固定 DarkAqua，采用 NSSplitViewController Sidebar、SourceList/SF Symbols 与语义色分组表单，NSTableView 分列勾选/名称/主次快捷键，原生 pasteboard writer 与行间插入。五组实拍/12 张色卡的两向居中回归通过，设置五分类/真实快捷键事件/勾选/排序绑定/窄宽窗/关闭落盘通过，无新增编译警告；系统窗口截图取得。真实鼠标拖放因系统事件注入权限受阻，程序化从未通过、不能记作已通过；该项已于 2026-10-16 由作者手工实操验收通过（设置页列表拖拽排序、主窗口拖入文件）。详见 P2 验收清单新增设置回归段。

- **P2 UI 追加约束(2026-10-14)**：作者要求主窗口保留隐藏式标题栏，明确设计为方形的元素保持方形；下拉框、按钮等通用控件保持原位置与大体形状并接近原生风格。具体几何表、Apple SF Symbols 建议与字体图标保留边界已写入 [P2 UI 方案](native-rebuild-phase-2-ui.md) §1.4–1.5；上一版示意的矩形缩略图区是示意样式错误，不是解码问题，作者允许在原生实现时修正。**设置页**按作者要求参考已有 iOS 原生设置，直接采用 macOS 原生设置风格，功能一致、不追求 Avalonia 视觉一致；原网页设置布局不作为实现规格，以方案 §4 与原生真屏验收为准。

- **人工验收推迟到原生壳阶段**:Phase 0/1 在 Avalonia 旧壳内,UI 形态是过渡载体、仅为产品参考,**不要求严格复刻**;此阶段回归项(评分写回/指纹 DB/AI 索引/分析栏)由 agent 程序化自验(读写回环、日志断言、截图),不做人工交互验收。原生壳阶段(Phase 2 起)功能项对齐即可,允许趁机做 UI/手感优化——plan-1 §7"交互规格以现有行为为准逐条复刻"的要求就此放宽。
- **iPad 验证策略**:开发期用 iOS 模拟器(功能 + Simulate Memory Warning 驱动 MemoryGovernor 响应矩阵);**真机只在关键环节**:P1-M5 验收门(jetsam 压测——模拟器无内存上限,验不了)、以及各 Phase 的原生壳真屏验收。在役机型 = iPad mini 7(A17 Pro,8GB,主流配置而非低配,压力阈值校准按 8GB 档)。

## 3. 验证矩阵

| 范围 | 状态 | 说明 |
|---|---|---|
| Windows Desktop 构建 + 冒烟 | ✅ 2026-10-19 | CLI 开 JPG(竖图旋转正确)+ HIF(LibHeif),翻图/胶片条/EXIF/评分渲染正常 |
| **Windows 原生壳同步共享分支** | ✅ 程序化 2026-10-17 | 合并 `c0567c6`:分析栏/增强/编码接共享架构，Windows 侧不再自持分析数据链；`--pv-native-smoke` **24 项全 PASS**（150%）；W-M5 设置窗口 `--pv-native-settings` **7 项 PASS**（Mica/NavigationView/TitleBar/NumberBox/InfoBar、五分类/开关+数值框+滑条/重排/录制状态/关闭回写）；工具窗口 `--pv-native-tools` **5 项 PASS**（三分类/EXIF 7 列/DINO 5 瓦片/统计 CSV/关闭重开）。W-M7/200%·175% DPI/真实跨屏·相机素材/设置与工具真实拖拽、选择器、视觉手感/W-M6 专项 harness 未关闭 |
| **Windows 桌面回归(P1 门④ Windows)** | ✅ 程序化部分 2026-10-13 | `--pv-stress` 18/18 零超时(冷启 p50≈3.5s = libheif 全解码口径)+ 告警注入级联/恢复观察期日志断言 + 评分 XmpWriter 读写回环 PASS;缩放/设置/聚类/分析栏交互项作者手测中 |
| Android 构建 | ✅ | 真机冒烟未做;P1 桌面回归与 `--pv-stress` 接线待办 |
| Training.sln / Tools 构建 | ✅ | CvDebugTool / ExifTestTool / RawDecodeTool(Mac 专用,需在 Mac 构建复核) |
| **macOS 构建** | ✅ 2026-10-12 | 0 错误,仅 NU1903 基线警告 |
| **P2 四平台构建** | ✅ 2026-10-14 | Windows/iOS 0 警告；Mac 仅 NU1903，Android 14 条既有警告；全部零错误 |
| **P2 原生实跑** | ✅ 程序化 2026-10-14 | AppKit/Metal 真实启动、色卡/八方向、RAW Full7008×4672、设置实际落盘、评级/CV/DINO/指纹三表回环；输出 release/native-p2-acceptance，使用独立设置/数据库与自有图片副本 |
| **P2 产品/色彩/性能** | ✅ 色彩/画质/手感/拖放 2026-10-16 作者验收通过；性能门未关 | 色彩跨屏对拍、画质、手感（含触控板）、真实鼠标拖放由作者实跑通过；大图性能门（RAW 冷图 1.7–1.9s <100ms）与真实素材矩阵仍未关闭。发布通道（dmg 默认是否切原生）待作者决定，属主计划 Phase 6 |
| **P3 作者复验** | ✅ 2026-10-16 通过（含两处修复） | 作者实跑：色彩/画质/手感/真实鼠标拖放通过；退出崩溃与标题栏双击两缺陷已修并复验（八套原生回归 PASS、四平台 0 错误、真实 AppleEvent 退出两种路径无崩溃报告、"双击标题栏"真机双向切换）；系统"减弱动态效果"开启态作者实测通过。未修（作者判定共享层另立项）：切图卡顿；未关闭：大图性能门 |
| **FILM-05 / FILM-06 程序化补齐** | ✅ 2026-10-16 | ui-contract 里 macOS 最后两个"待核验"格子补成断言：filmstrip 套件加 FILM-05（自有素材上验身份/几何不等解码、缩略图与日期异步补齐、滚动不等解码、不可解码文件仍可选主图且无伪造图像）；toolbar 套件加 FILM-06（卡片星级真实点击 → 文件读回一致 + 主图身份不变 + 点星不选中父卡片，改星落出筛选仍暂留 / 切走后移除 / 计数一致）。八套回归 PASS，Mac 构建 0 错误 |
| **旧壳（Avalonia）验收** | ⛔ 不再验收 | 作者裁定旧壳将被原生壳替换、不做产品验收；各平台验收在各自原生壳落地后单独建立（macOS 已完成；iPad 程序化已完成、作者手验待做；Windows 原生主屏程序化已完成、设置/工具/色彩未做；Android 原生待迁移） |
| **macOS 冒烟(首轮)** | ✅ 2026-10-12 | CLI 开 ARW:MacRawDecoder 链路主图/胶片条/EXIF/评分栏渲染正常;交互逐项(翻图/缩放/评分写回/设置)待人工 |
| **iOS 构建** | ✅ 2026-10-13 | 模拟器+真机(ios-arm64 Release)均 0 错误 |
| **iPad mini 7 真机验收(I2 主图：编码协商 + Metal 主图 + UIScrollView 手势)** | ✅ 2026-10-17 | 提交 41abd7b，设备 E71A131A（解锁后一次通过）。`--pv-native-photo-check` **PASS 10 checks**（代理 4532×3021 `RgbaF16` → 物理 100% 热替换 `Rgba8/DisplayP3` 且变换保持 scale=0.5 pan=(0,0)、代理↔Full 中心线性 Δ=0.0001、八方向 GPU 角点、四色卡线性值）；`--pv-stress-count=500`（450 HEIC+50 ARW）**500/500 零超时零闪退恒 Normal**（p50 716ms/p95 775ms/max 799ms、gc 峰 1620MB）；`--pv-stress-warn=100` → 代理降 8 位 P3（`enc=Rgba8`）+ 30s 恢复观察期实证；无新 .ips。**未关闭**：iOS ImageIO 读 ARW 只给内嵌预览（1.7MP），见 [phase-ipad §6](native-rebuild-phase-ipad.md) |
| **iPad ONNX / 相似度隐式推理修复** | ✅ 2026-10-17 | iOS head 的 MSBuild 目标提前展开 `onnxruntime.xcframework`、按 `Kind=Static + ForceLoad` 静态链接、导出 `_OrtGetApiBase`（含 Release strip 的 `mtouch-symbols.list`），`iOSNativeShell` 用 `DllImportResolver` 映射到主程序。产物无 `Frameworks/onnxruntime.framework`（切片是 ar 静态库），`nm`/`dyld_info` 可见导出符号。模拟器与真机 `--pv-dino-check` 均 **PASS cls=384**（CoreML EP 在两版 iOS 原生库中不受支持，日志记录后回退 CPU：sim 440.7ms / 真机 629.0ms）。`SimilarityPanelViewModel` 改为只在面板展开且文件栏可见时推理；真机 500 连翻复测 **500/500 零超时、全 Normal、0 条 DinoFeatureCache**，p50 **323.2ms** / p95 **418.5ms**（基线 716/775ms）。Mac 四套回归 PASS，四平台构建零错误 |
| **iPad 主屏布局(I3)** | ✅ 程序化 2026-10-17 | 提交 `6f36d8b`：两台模拟器 `layout` **PASS 24** + `photo` **10**，覆盖两向/自动布局、真实安全区、栏位显隐释放空间、筛选条/控制栏/卡片几何、FILM-01–06 交互；Mac 回归 PASS。旋转、Split View/Stage Manager 与触摸手感待作者手验 |
| **iPad 输入(I4)** | ✅ 程序化 2026-10-17 | 提交 `9f20582`：模拟器与真机 `--pv-native-input-check` 均 **PASS 27**（两类上下文菜单、轻扫翻页与放大态不翻页、HID 映射 47 键、INPUT-01、滚轮识别范围、拖入、设置关闭回写传播）；修复设置下拉关闭不回写。真实键盘/鼠标/触控板手感待作者 |
| **iPad P3 功能(I5)** | ✅ 程序化 2026-10-17 | 提交 `e9ee179`：两台模拟器 photo/layout/input/analysis/similarity/tools = **10/24/27/15/10/7** 全 PASS；真机 `analysis` **15** / `similarity` **10** / `tools` **7**；真机 200 连翻（分析栏关）**200/200 零超时**、p50 311.1ms / p95 385.3ms、gc 峰值 996MB；`--pv-analysis-open` + 50 连翻 p50 447ms / p95 2660ms（1 次超时），全程 Normal。触摸手感待作者 |
| **iPad I6 文档与验收收尾** | ✅ 2026-10-17 | ui-contract iOS 列补齐、根/模块 AGENTS 与 handoff/DEV 同步、新增 [iPad 验收清单](native-rebuild-phase-ipad-acceptance.md)；逐里程碑状态见 [执行清单 §7](native-rebuild-phase-ipad.md)。模拟器与 Mac Debug 构建 0 错误；作者手验未执行 |
| **iPad mini 7 真机验收(P1-M5)** | ✅ 2026-10-13 | 500 连翻(450 HEIC+50 ARW)p50 356.7ms 恒 Normal 零闪退;告警注入+30s 观察期实证;`--pv-stress-full` 对照组复现 jetsam(A/B 成立);修复 mono interp ref/out P/Invoke 间歇崩溃后 26/26 启动零崩溃。缩放热替换手感、外部打开选中行为留人工 |
| DINO 渲染口径 | ✅ 2026-10-12 | A/B 实证(headless 不可行,用真窗口 GPU 路径):zoneplate 摩尔纹形态两侧一致(同为 Linear/无 mipmap 族),真实内容 meanDiff=0.24/maxDiff=15(GPU↔CPU 舍入级)。**意外发现:macOS 上 `RenderTargetBitmap.CopyPixels` 实为 RGBA 序**,旧管线 Mac 端 DINO 输入 R/B 互换(Windows 缓存正常);新 Skia 路径统一 BGRA,顺带根治 |
| 四平台人工冒烟 | ⛔ 旧壳不验收 | 作者裁定：Avalonia 旧壳不做产品验收（将被原生壳替换）；人工验收只在各平台原生壳进行。macOS 原生壳 2026-10-16 通过；iPad 原生壳作者手验**待执行**（[清单](native-rebuild-phase-ipad-acceptance.md)） |

## 4. Mac 开工步骤(接手第一天)

1. `git fetch && git checkout refactor/native-rebuild`,通读 §1 的三份文档。
2. ~~`dotnet build PhotoViewer.Mac` + `dotnet build PhotoViewer.iOS`~~ ✅ 2026-10-12:两头工程均 0 错误(仅 NU1903 基线警告),M3/M4 盲改区无编译毛边。
3. 冒烟:Mac 首轮已过(见 §3);交互逐项(翻图/缩放/评分写回/设置页/聚类/分析栏)人工过;`Debug iOS` 上 iPad 真机。
4. 跑 DINO 口径验证(§3):`Training/onnx/verify_onnx_parity.py` + `--dump-render` 抽样对比渲染缓存。
5. 全部通过后:Phase 1 按 [native-rebuild-phase-1.md](native-rebuild-phase-1.md) 里程碑开工(**先读其 §5 发现清单**——2026-10-12 审查裁定已修订 M1-M4 多处:准入口优先级语义、统一 LRU 键前置 M2、分档缓冲池、解码路径平台差异)。

## 5. 工作纪律(与主仓 AGENTS.md §8/§9/§10 一致,此处强调重构特有条目)

- 每里程碑可审阅、自验与文档同步；作者已授权先提交 P2 实现与胶片条修复，产品验收继续进行。既有基线警告据实记录，禁止压警告冒充通过。
- 重构期禁止顺手优化无关代码;发现的既有问题记入对应阶段清单的"发现清单"表,不随手修(Phase 0 就是这么记的)。
- **画质红线**:不接受视觉可见的降分辨率;Phase 1 分级解码的"100% 放大永远全分辨率"是硬约束。
- 持久化契约(设置 JSON 键值、指纹、DB schema、DINO 模型输入口径)冻结,改动必须显式立项。
- macOS/iOS 提交前必须真机/真屏过验收门;"编译过"不算完成。

## 6. 已知坑(来自 Phase 0 发现清单,摘要)

- mimo 子代理 provider 曾整日超时,Phase 0 全部工作由 kimi 直接执行;若你的环境恢复,可自行选择是否启用子代理。
- `ShakeFlagService` 有既有并发竞态(文件夹加载期集合改写),Phase 1 M3 修复。
- `AndroidHeifDecoder` 通道序依赖历史口径(行为不变保留),Phase 2 色彩管线统一。
- 适配层 CWT 双份缓冲(当前图 DecodedImage + WriteableBitmap 并存),Phase 1 记账、Phase 2 消除。
- `BitmapLoader` 淘汰路径的阻塞 `GC.Collect ×2` 保留到 Phase 1 M3 拆。
- Windows 侧构建命令:`dotnet build PhotoViewer.Windows/PhotoViewer.Windows.csproj`;Mac 侧一切走 VS Code Tasks(DEV.md)。
