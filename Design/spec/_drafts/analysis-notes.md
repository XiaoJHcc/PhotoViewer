# analysis 拆分笔记（草稿，非规范）

拆分自 `Design/ui-contract.md` ANALYSIS-01 行、ANL-02–04 行与各平台方案；规则正文见 [../analysis.md](../analysis.md)。

本文件提出的新令牌（待 tokens 合并）：`analysis.dino.patchGrid`（32）、`motion.analysis.fallback.yieldTimeout`（5000ms）。既有 `analysis.*` / `color.analysis.*` 见 `Design/tokens.json`。

## platforms

**控件选型**

| 平台 | 面板容器 | 细节预览 | 诊断瓦片 | 直方图 | 备注 |
|---|---|---|---|---|---|
| macOS 原生 | `MacAnalysisPanel`（AppKit 自绘分区，`#111` 底 + 1pt 分隔线） | `MacDetailPreview`（外框 + 绿色描边 overlay，命中测试整体参与） | `MacDiagnosticTile`（外框 6 / 内层裁切 5 / 1pt #444 / 近邻） | 同一 `MacDiagnosticTile`，`AspectRatio = 1` | 抖动瓦片叠加 `MacShakeFieldView` |
| iPad 原生 | `iOSAnalysisPanel`（UIKit） | UIKit 细节预览（`DisplayCrop` 出图） | `iOSDiagnosticTile`（`ContentMode=Redraw`，装饰层 `Opaque=false`） | 同上 | 手指点按定格绿框 |
| Windows 原生 | `WindowsAnalysisPanel`（实现 `IAnalysisHost`） | `AnalysisPreviewTile` + `DisplayCrop`，自持 hover + `ResetHover()` | `WindowsAnalysisTilePanel` 在 Measure/Arrange 排等边长 | 同一瓦片面板 | 单层裁切（内层圆角 0） |
| Android 原生 | `AndroidAnalysisPanel`（自绘，覆盖在 `SurfaceView` 上） | 自绘细节预览（`DisplayCrop`，代理期留空） | 自绘瓦片 | 自绘 | 手机纵向瓦片约 58dp |
| Avalonia 旧壳（退役） | `Views/Main/AnalysisView.axaml` | `Controls/DetailPreview`（`DetailPreviewHighlightGroup` 互斥高亮） | `Controls/DiagnosticTile` | 同一 `DiagnosticTile` | 双层圆角旧缺陷 |

**入口**

- macOS：主图右键菜单「分析栏」（勾选态）+ `ToggleAnalysisView`（控制栏命令/快捷键）。
- iPad：主图长按菜单 `UIContextMenuInteraction`（与 Mac 右键菜单同序）+ 控制栏命令；筛选条之外没有第二个入口（ui-contract.md:105、117）。
- Windows：主图右键菜单 + 控制栏命令（`WindowsMainWindow` 菜单勾选态）。
- Android：主图长按自绘 `PopupWindow`（`Plans/native-rebuild-phase-android.md:184`）+ 控制栏命令。
- 可见性均为会话状态：`MainViewModel` 字段默认 `false`、无设置项（`PhotoViewer/ViewModels/Main/MainViewModel.cs:317`、`:340`）。

**已登记/需登记的 ID@平台 差异**

- `ANALYSIS-04@ios`：触控板/鼠标悬停 = hover 绿框；手指点按在"定格/解除"间切换，定格后离开不消失，再点或点主图空白解除；双击定位到裁剪中心（ui-contract.md:119）。
- `ANALYSIS-04@android`：点按细节预览定格、点按主图空白解除，主图画 3dp 绿框（Plans/native-rebuild-phase-android.md:190、:269 §6#40）。
- `ANALYSIS-04@mac`：细节预览双击会清 `FollowsFocus`（Avalonia 保留），见 Plans/native-rebuild-phase-ipad.md:192 第 12 条待收尾项。
- `ANALYSIS-03@android`：手机纵向按共享公式排 6–7 张瓦片（约 58dp），未做手机专用放大/横滑（Plans/native-rebuild-phase-android.md:194、:253 §6#24）。
- `ANALYSIS-02@android`：抖动拖影瓦片的矢量线为简化绘制（Plans/native-rebuild-phase-android.md:194）；iPad 验收断言背景 alpha 0.30 与最近邻（ui-contract.md:155）。
- `ANALYSIS-02@windows`：内层圆角 0（单层裁切）；其余平台实现仍保留外层 6 + 内层 5（tokens.json:856–870）。
- `ANALYSIS-08@mac`：诊断瓦片吞掉自身范围内的左键，只有内容矩形落准星；点在 letterbox/角标不改变准星也不清除（`PhotoViewer.Mac/Native/MacDiagnosticTile.cs:11`、`MacAnalysisPanel.cs:107`）。规范取"瓦片空白等同面板空白 = 清除准星"，因此 macOS 需补齐或登记差异。
- `ANALYSIS-05@ios` / `@android`：分析栏参与真实安全区布局；行布局在主图下方、列布局在右侧；主轴瓦片起点叠加安全区内边距（ui-contract.md:117；`PhotoViewer.Shared/MainScreenLayout.cs` 分区数学）。
- `ANALYSIS-06@ios`：ImageIO 对 Sony ARW 只给内嵌预览，`IsProxy=false`，因此"100% 物理"与细节预览的源分辨率以预览尺寸为基准（Plans/native-rebuild-phase-ipad.md:133、:182 未关闭项 2）。
- `ANALYSIS-05@mac`：顶边拖动区与面板/toggle 不得相交（`--pv-native-similarity` 断言，ui-contract.md:88）。
- `ANALYSIS-09@平台`：执行提供器 macOS=CoreML、Windows=DirectML、Android=NNAPI、iOS=CPU（CoreML EP 在 iOS 原生库不可用），推理路径不同但编排共享 `AnalysisPresenter`（Design/architecture.md:17、Plans/native-rebuild-phase-ipad.md:131）。

**几何实测**

- macOS：行布局 6 张 106 正方形瓦片（padding 10 / gap 8 / cross 126），列布局 70 / cross 90；瓦片顺序与角标、点击落准星、直方图与细节出图、关闭后主图恢复（Plans/native-rebuild-phase-3.md:68）。
- iPad：同一共享 `AnalysisPanelGeometry`，主轴取安全区内容长（ui-contract.md:117）。
- Windows：`WindowsAnalysisTilePanel` 两向断言等边长（ui-contract.md:94）；细节预览用 `DisplayCrop`（ui-contract.md:144）。
- Android：`--es pv-check analysis` 18 项 PASS（Plans/native-rebuild-phase-android.md:198）。

## status

> 拆分后各新 ID 的验收状态沿用 ANALYSIS-01 行（面板整体），待 `status.md` 逐 ID 登记；下表是拆分前来源原文摘录。

- **ANALYSIS-01（整体）**
  - Windows：`--pv-native-smoke`（150%）覆盖细节预览/直方图/即时计算/准星/两向几何/增强帧诊断重算，程序化通过；真实素材与手感待作者手验（ui-contract.md:155，2026-10-17）。
  - macOS 原生：本轮实机程序化验证（两向瓦片几何 / 顺序角标 / 落准星 / 直方图与细节预览出图 / 增强联动）；"提取全部后仍显示未提取"已修（ui-contract.md:155，2026-10-16）。
  - iOS/iPadOS：模拟器两机型 + 真机程序化通过（`--pv-native-analysis-check`：菜单/控制栏入口、两向瓦片几何与顺序、准星、F16 代理与 8 位 P3 Full 直方图/裁剪像素、未提取→真实索引刷新、增强开关、点按定格绿框）；触摸手感待作者真机（ui-contract.md:155，2026-10-17）。
  - Android 原生壳：2026-10-18 模拟器目视（分析栏经菜单开合、中心细节裁剪、点按定格、诊断瓦片落准星；纯色色卡直方图信息少）；`pv-check analysis` 18 PASS，矩阵/真机门未做（Plans/native-rebuild-phase-android.md:198；ui-contract.md:155）。
  - Avalonia 旧壳：作者裁定不做验收（Design/ui-contract.md:141，2026-10-16）。
- **ANALYSIS-02（原 ANL-02）**：Windows 已单层裁切 + 非方形位图按原比例置入，程序化通过（2026-10-17）；Mac/iPad/Android 仍外层 6 + 内层 5（tokens.json:856–870）；旧壳双层圆角为已知缺陷，不作设计。
- **ANALYSIS-03（原 ANL-03）**：Windows 用 `WindowsAnalysisTilePanel` 在 Measure/Arrange 排布并断言两向等边长（`--pv-native-smoke`，2026-10-17）；Mac/iPad 由 `AnalysisPanelGeometry` 几何验收覆盖（2026-10-16 / 2026-10-17）；Android 等边长随 `pv-check analysis`（2026-10-18，待矩阵）。
- **ANALYSIS-04（原 ANL-04）**：Windows 自持 hover + `ResetHover()`，换图/代理→Full/尺寸变化不自动点亮，程序化通过（2026-10-17）；macOS/iPad 有 hover 与双击断言；iOS 另有手指点按定格（2026-10-17 模拟器 + 真机）；Android 点按定格（2026-10-18 目视）。
- **ANALYSIS-05–11（新拆分）**：由 ANALYSIS-01/ANL-02–04 的既有验收覆盖，尚未按 ID 单独登记，`status.md` 需回填。

## questions

1. **编号冲突**：`Design/README.md:57` 的前缀登记写"原 `ANL-01`–`04` 并入为 `ANALYSIS-02`–`05`"，本次按任务要求采用 `ANL-02/03/04 → ANALYSIS-02/03/04`、`ANL-01` 退役。需要统一 README 或本文件。
2. **内层圆角令牌与 ANALYSIS-02 冲突**：`Design/tokens.json:856–870` 保留 `analysis.tile.innerCornerRadius`（5），并注明 macOS/iOS/Android 继续用双层圆角、Windows 为 0；ANALYSIS-02 要求单层裁切。是改实现还是改令牌，需作者裁定。
3. **Android 手机瓦片尺寸**：`Plans/native-rebuild-phase-android.md:194`、`:253 §6#24` 记录手机纵向 6–7 张瓦片仅约 58dp、细节预览"可用但不适合多点操作"，未做手机专用放大或横滑。按现规范算合规（同公式缩小），但可读性存疑，需决定是否作为平台差异接受或新增窄屏规则。
4. **失败状态与"未提取"混用**：`PhotoViewer/Core/AI/AnalysisPresenter.cs:419`、`:424–425` 在无展示图或计算失败时回落到"未提取"；`:108` 直方图占位固定"计算中"，面板隐藏/无图时（`:442–445` 只清源）可能长期显示"计算中"。规范已写"失败应可区分"，实现需补文案/状态。
5. **可见性不持久化、准星跨图保留**：`MainViewModel.cs:317`、`:340` 无设置字段，可见性每次启动隐藏（README §1 对分析栏只写"命令与菜单开关"）；但代码里文件栏/控制栏的显隐同样没有持久化字段，与 `Design/spec/01-regions.md` 的"状态持久化"表述不一致，需 shell 规范澄清。另 `AnalysisPresenter.cs:161–184`、`:306–334` 切图保留准星归一化位置但把 cosine 参考点重置为中心，请确认这两条是否为有意语义。
6. **Mac 细节预览双击清对焦跟随**：`Plans/native-rebuild-phase-ipad.md:192` 第 12 条把"Mac 细节预览双击会清 `FollowsFocus`（Avalonia 保留）"列为待收尾差异；ANALYSIS-04 未写跟随语义，需确认是否登记为 `ANALYSIS-04@mac`。
7. **分析栏可见即升 Full 的代价**：`Plans/native-rebuild-phase-ipad.md:186` 记录分析栏开启 + 50 连翻 p50 447ms / p95 2660ms、1 次超时；`PhotoViewer/ViewModels/Main/ImageViewModel.cs:64` 无条件要求 Full。ANALYSIS-06 把"可见即以 Full 呈现"定为必须，性能专项是否接受这一代价需作者确认。
8. **未验证**："分析栏可见时滚动滚轮不翻页/不缩放主图"在四个原生壳中都没有专项断言（各分析面板无滚轮处理），仅按事件路径推断，需实机核对。空白点击清准星四处均有实现（`MacAnalysisPanel.cs:107`、`WindowsAnalysisPanel.cs:66–69`、`iOSAnalysisPanel.cs:76`、`AndroidAnalysisPanel.cs:404`），但 iPad/Android 的命中区域（是否整块面板）未见断言。
9. **细节预览代理期留空**：旧壳与四个原生壳均按 `IsProxy ? null` 留空（`MacAnalysisPanel.cs:198`、`iOSAnalysisPanel.cs:216`、`Plans/native-rebuild-phase-android.md:190`），与 ANALYSIS-06 一致；但 iPad 验收文案写作"F16 代理与 8 位 P3 Full 的直方图/裁剪"（ui-contract.md:91、:155），容易读成代理期也出裁剪图，需确认该断言实际覆盖的是直方图（代理）还是细节裁剪（Full）。
