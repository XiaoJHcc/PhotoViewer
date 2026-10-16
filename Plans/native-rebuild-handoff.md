# 原生重构交接文档(handoff)

> **给在 Mac 上接手的 agent**:本文是重构的当前状态真源 + 开工指引。写入规则:每次阶段推进后更新"当前状态"与"验证矩阵"。
> 最后更新:2026-10-14(P2 AppKit/Metal 已落地，程序化核验通过；作者产品验收待执行)。

## 1. 为什么重构(决策语境)

PhotoViewer 正在从 Avalonia 迁往四平台原生壳 + 共享 C# Core。驱动与决策全录于 [native-rebuild-master-plan-1.md](native-rebuild-master-plan-1.md)(总纲,含三大方向决策:全平台原生 / 只上 iOS App Store / Core 先行);色彩根因实证在 [color-managed-native-rendering-plan-1.md](color-managed-native-rendering-plan-1.md)(已被总纲收编为色彩专章)。

一句话:**色彩管理、切图性能、iPad 内存、UI 原生感、App Store 上架五个问题共用一套底层重构来根治**,不打补丁。阅读顺序:master plan → phase-0 清单(含发现清单,很多坑在那里)→ 本文。

## 2. 当前状态

- **分支**:`refactor/native-rebuild`(远端 github 已推送;`main` 保持可发布,未合并)。
- **Phase 0(Core 平台中立化,零行为变化)已完成且验收门全关**:Core 零 Avalonia 引用;像素货币 `DecodedImage` + SkiaSharp 像素基座;存储/调度/窗口/快捷键/资源五组门面与 Avalonia 适配层齐备;机械门/持久化契约/Mac+iOS 构建/Mac 冒烟/DINO 口径全部通过(细节见 §3 矩阵与 phase-1 §5 发现清单)。里程碑提交 M1→M6 逐笔可查。
- **Phase 0 定向审查(2026-10-12)已通过**:Phase 0 由同一 agent 实现+自审(mimo 超时),本次补独立审查——机械门复核 PASS(Core 零 Avalonia,残留均注释;VM 零 UI 耦合);手势持久化契约 PASS(`GestureKey` 223 名 ↔ Avalonia 12.0.2 `Key` 223 名双向 diff 为空,`GestureModifiers` 名/值与 `KeyModifiers` 一致,字符串枚举序列化下逐字节兼容);Mac/iOS 构建 0 错误(仅 NU1903 基线警告)。审查发现的 Phase 1 计划修订已全部落回 phase-1 清单(准入口优先级语义、分档缓冲池、解码路径平台差异、统一 LRU 键前置 M2、验收门平台口径)。
- **Training 仓**:`DatasetBuilder` 已同步适配(独立提交);`docs/HUMAN.md` 有用户自己的未暂存改动,不要动它。
- **Phase 1 已完成**(M1 Governor 骨架 / M2 统一 LRU+压力矩阵 / M3 准入+租约+缓冲池 / M4 分级代理解码 / **M5 iPad 真机验收 2026-10-13 全项通过**):验收门 ①500 连翻无闪退恒 Normal ✅ ②P0 延迟 iPad -55%/Mac HEIC -51% ✅ ③告警后 30s 恢复观察期真机实证 ✅ ⑤Core 零 Avalonia/零新增警告 ✅;④ Windows 程序化部分 2026-10-13 过(交互项作者手测中)、Android 待办。M5 副产物:**修复 mono 解释器 ref/out P/Invoke 封送间歇崩溃**(iOS 头工程签名全指针化;`BootTrace` 常驻取证);新增压测驱动器 `StressFlipDriver`(启动参数 `--pv-stress` 系,见 phase-1 §5)。详见 [native-rebuild-phase-1.md](native-rebuild-phase-1.md) §1 验收门勾选与 §5 发现清单。
- **Windows 收尾(2026-10-13)**:P1 定向审查(Windows 路径)3 项发现已修——`SkiaImageCodec.ToBgra32` R/B 映射(harness 逐通道断言 PASS)、Desktop `--pv-stress` 接线、P1-M4a 失准注释;Windows 桌面回归程序化部分过关(`--pv-stress` 18/18 零超时、告警注入级联+恢复观察期日志断言、评分 XmpWriter 读写回环 PASS);缩放/设置/聚类/分析栏交互项 2026-10-13 作者裁定手工冒烟。`MainViewModel.CurrentFile` 切图取证日志常驻(phase-1 §5 有一次性异常登记)。
- **Phase 2 实现与程序化自验已完成，待作者产品验收**：作者授权直接执行 P2，M1–M4 AppKit/Metal、F16 色彩/代理、胶片条/筛选/评分、手势/快捷键、原生设置、外部打开已落地；Debug 默认原生，Release 默认旧壳。ColorTestTool 23 项、原生色卡 39 项/RAW 副本 17 项/生成 HEIF 14 项通过（含八种 GPU 与分析方向、物理 100% Full、外部打开队列、设置副本隔离与落盘、CV/DINO/指纹三表索引）。四平台构建零错误、无新增编译警告；Mac NU1903、Android 14 条为既有基线。执行清单 [native-rebuild-phase-2.md](native-rebuild-phase-2.md)，产品验收 [native-rebuild-phase-2-acceptance.md](native-rebuild-phase-2-acceptance.md)。系统预览色彩对拍/跨屏、手感、大图性能门未关闭；RAW 冷图约 1.7–1.9s，小色卡不能替代真实素材 <100ms 指标。分析栏/聚类/工具/增强顺延 P3，入口隐藏且切图不隐式推理。作者已授权先提交，产品验收继续；五张实拍暴露的胶片条布局/滚动/顺序缺陷已修复，五张与十二张多图定向回归通过。
- **P1-M4b 画质/交互修复(2026-10-14,作者实证驱动)**:① 代理 overscan 1.5→**2.0**(1.5× 下"解码降采样+上屏 Mitchell 降采样"双重软化可见;2× 超采样后上屏重采样近乎无损,守画质红线;内存代价由 Governor 准入兜底);② **双击/键盘放大不触发 Full 升级**(根因:`ZoomTo`/`FocusOnImagePoint` 先改 Scale、后落 `Fit=false`,`Scale` 订阅见到 `Fit==true` 直接 return;双指缩放因连续事件第二次起 Fit 已 false 才正常。修复:升级触发改听 `Scale`+`Fit` 双属性)。Mac 真屏冒烟:ARW 代理 4180×2787(=2× 视图物理像素)、双击 100% → `show full 7008×4672` 热替换、放大态切图 Full 自动接续、回 fit 不再全解码、零异常。两修复均在共享层,原生壳直接继承;上屏最后一级滤波(trilinear/mipmap)已写入 phase-2 P2-M2。

## 2.1 过程决策(2026-10-12 与作者对齐)

- **主界面严格几何对拍（2026-10-15，最新决定）**：作者要求主界面布局结构、尺寸、边距与原 Avalonia 一致；背景按原设计或原生深色，边框/圆角先原生，菜单/字体/字重/颜色接近或更优时保留原生，不机械堆砌控件；设置暂不处理。此要求优先于下文早期“近似”约定。本轮校正卡片外框与占位、EXIF/评分主轴边距、筛选计数自然宽度、短横向列表居中和导航图长边 150 的等比几何。旧壳/原生隔离驱动器生成同配置两向三尺寸实际几何；验收结果见 P2 清单，最终视觉仍须作者确认。

- **设计文档与工具栏对拍（2026-10-15，早期记录，几何宽松要求已被上条替代）**：作者明确 UI 文档现阶段是对账草稿，重要约束与原生表现待分类，不能把旧版尺寸/参数冻结。工具栏真屏对拍发现三分组丢失、EXIF 信息层级丢失、竖排评分倒置和固定高度按钮边框；新增 MacControlBar 恢复整体分组换行/换列、居中与信息块，按钮采用可变高度原生边框，同时修复显隐/排序刷新、苹果主次绑定提示及评分快捷命令星数。P3 入口保持延期。

- **跨平台设计对账（2026-10-14）**：新增 [Design/ui-contract.md](../Design/ui-contract.md) 作为设计真源，使用稳定 ID 记录共同交互、平台差异、缺口及各平台验收状态。对照 Avalonia 纠正 P2 返修中“所有 CurrentFile/列表刷新都居中”的误实现：点选/交换/排序/筛选不居中，显式翻页/打开用 350ms 三次缓出，新请求覆盖并允许用户接管。Mac 显隐恢复/换向定位登记为平台差异；其他平台未做实机验证的项目保持待验，卡片日期/完整时间入口与加载提示缺口已登记。
  - 本轮 12 张色卡与五组实拍 HIF/ARW 定向实跑均 `PASS filmstrip`，两向/升降序及实际筛选菜单回归通过，报告含动画中间帧与终点。Debug Mac 构建零错误、无新增警告（仅既有 NU1903）；滚动条真实拖动、开启减少动态效果与动画手感仍待作者手验。

- **Phase 3（macOS 原生缺失功能补全）进行中**：作者 2026-10-16 要求把剩余顺延功能按原 Avalonia 设计重构到 Mac 原生（布局/尺寸/要点/高亮尽量一致；下拉框、右键菜单等小型控件保留原生；工具窗口可仿设置窗口方式）。执行清单 [native-rebuild-phase-3.md](native-rebuild-phase-3.md)。已落地：① 共享层抽出平台中立的 `Core/AI/AnalysisPresenter` + `AnalysisTile` + `IAnalysisHost` 与 `Core/AI/DinoDiagnosticPresenter`，旧壳 `AnalysisViewModel` / `DinoDebugViewModel` 退化为位图适配器（编排单一来源）；② 增强预览下移到 `PhotoLoadSession`（开关跨切图持续生效、压力 Warning 自动关闭）；③ `ImageOrientationMapping` + `DisplayCrop` 统一方向映射与显示朝向 1:1 细节裁剪，`SkiaImageCodec.Crop` 支持 F16；④ `HistogramRenderer` 直接支持 F16 线性表面；⑤ 原生分析栏（`MacAnalysisPanel` / `MacDiagnosticTile` / `MacDetailPreview` / `MacShakeFieldView`）与 `ToggleAnalysisView`、`ToggleEnhance` 命令、右键菜单入口；⑥ 原生相似聚类面板与筛选条 toggle；⑦ 原生工具窗口（EXIF 详情 / DINO 诊断）。验收套件 `--pv-native-analysis` 已加入 runner。
- **P2 UI 返修（2026-10-14）**：作者指出胶片条自动居中缺失、控制栏右侧空带、设置浅色/四周白边及排序/快捷键结构草率。本轮恢复主轴居中（首尾受边界约束），主窗口滚动条统一 overlay、控制文档匹配裁剪宽度；设置固定 DarkAqua，采用 NSSplitViewController Sidebar、SourceList/SF Symbols 与语义色分组表单，NSTableView 分列勾选/名称/主次快捷键，原生 pasteboard writer 与行间插入。五组实拍/12 张色卡的两向居中回归通过，设置五分类/真实快捷键事件/勾选/排序绑定/窄宽窗/关闭落盘通过，无新增编译警告；系统窗口截图取得。真实鼠标拖放因系统事件注入权限受阻仍需作者手验，不能记作已通过。详见 P2 验收清单新增设置回归段。

- **P2 UI 追加约束(2026-10-14)**：作者要求主窗口保留隐藏式标题栏，明确设计为方形的元素保持方形；下拉框、按钮等通用控件保持原位置与大体形状并接近原生风格。具体几何表、Apple SF Symbols 建议与字体图标保留边界已写入 [P2 UI 方案](native-rebuild-phase-2-ui.md) §1.4–1.5；上一版示意的矩形缩略图区是示意样式错误，不是解码问题，作者允许在原生实现时修正。**设置页**按作者要求参考已有 iOS 原生设置，直接采用 macOS 原生设置风格，功能一致、不追求 Avalonia 视觉一致；原网页设置布局不作为实现规格，以方案 §4 与原生真屏验收为准。

- **人工验收推迟到原生壳阶段**:Phase 0/1 在 Avalonia 旧壳内,UI 形态是过渡载体、仅为产品参考,**不要求严格复刻**;此阶段回归项(评分写回/指纹 DB/AI 索引/分析栏)由 agent 程序化自验(读写回环、日志断言、截图),不做人工交互验收。原生壳阶段(Phase 2 起)功能项对齐即可,允许趁机做 UI/手感优化——plan-1 §7"交互规格以现有行为为准逐条复刻"的要求就此放宽。
- **iPad 验证策略**:开发期用 iOS 模拟器(功能 + Simulate Memory Warning 驱动 MemoryGovernor 响应矩阵);**真机只在关键环节**:P1-M5 验收门(jetsam 压测——模拟器无内存上限,验不了)、以及各 Phase 的原生壳真屏验收。在役机型 = iPad mini 7(A17 Pro,8GB,主流配置而非低配,压力阈值校准按 8GB 档)。

## 3. 验证矩阵

| 范围 | 状态 | 说明 |
|---|---|---|
| Windows Desktop 构建 + 冒烟 | ✅ 2026-10-19 | CLI 开 JPG(竖图旋转正确)+ HIF(LibHeif),翻图/胶片条/EXIF/评分渲染正常 |
| **Windows 桌面回归(P1 门④ Windows)** | ✅ 程序化部分 2026-10-13 | `--pv-stress` 18/18 零超时(冷启 p50≈3.5s = libheif 全解码口径)+ 告警注入级联/恢复观察期日志断言 + 评分 XmpWriter 读写回环 PASS;缩放/设置/聚类/分析栏交互项作者手测中 |
| Android 构建 | ✅ | 真机冒烟未做;P1 桌面回归与 `--pv-stress` 接线待办 |
| Training.sln / Tools 构建 | ✅ | CvDebugTool / ExifTestTool / RawDecodeTool(Mac 专用,需在 Mac 构建复核) |
| **macOS 构建** | ✅ 2026-10-12 | 0 错误,仅 NU1903 基线警告 |
| **P2 四平台构建** | ✅ 2026-10-14 | Windows/iOS 0 警告；Mac 仅 NU1903，Android 14 条既有警告；全部零错误 |
| **P2 原生实跑** | ✅ 程序化 2026-10-14 | AppKit/Metal 真实启动、色卡/八方向、RAW Full7008×4672、设置实际落盘、评级/CV/DINO/指纹三表回环；输出 release/native-p2-acceptance，使用独立设置/数据库与自有图片副本 |
| **P2 产品/色彩/性能** | ⏸ 待作者验收与真实素材测量 | 按 P2 验收清单操作，发布通道在此之前不切换 |
| **macOS 冒烟(首轮)** | ✅ 2026-10-12 | CLI 开 ARW:MacRawDecoder 链路主图/胶片条/EXIF/评分栏渲染正常;交互逐项(翻图/缩放/评分写回/设置)待人工 |
| **iOS 构建** | ✅ 2026-10-13 | 模拟器+真机(ios-arm64 Release)均 0 错误 |
| **iPad mini 7 真机验收(P1-M5)** | ✅ 2026-10-13 | 500 连翻(450 HEIC+50 ARW)p50 356.7ms 恒 Normal 零闪退;告警注入+30s 观察期实证;`--pv-stress-full` 对照组复现 jetsam(A/B 成立);修复 mono interp ref/out P/Invoke 间歇崩溃后 26/26 启动零崩溃。缩放热替换手感、外部打开选中行为留人工 |
| DINO 渲染口径 | ✅ 2026-10-12 | A/B 实证(headless 不可行,用真窗口 GPU 路径):zoneplate 摩尔纹形态两侧一致(同为 Linear/无 mipmap 族),真实内容 meanDiff=0.24/maxDiff=15(GPU↔CPU 舍入级)。**意外发现:macOS 上 `RenderTargetBitmap.CopyPixels` 实为 RGBA 序**,旧管线 Mac 端 DINO 输入 R/B 互换(Windows 缓存正常);新 Skia 路径统一 BGRA,顺带根治 |
| 四平台人工冒烟 | ⏸ 按 §2.1 决策推迟 | 旧壳阶段 agent 程序化自验;人工验收自原生壳阶段开始 |

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
- Windows 侧构建命令:`dotnet build PhotoViewer.Desktop/PhotoViewer.Desktop.csproj`;Mac 侧一切走 VS Code Tasks(DEV.md)。
