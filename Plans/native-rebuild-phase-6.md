# Phase 6 — 原生壳转正、Avalonia 退役与设计基准建立

> 2026-10-18 立项。对应 [总纲](native-rebuild-master-plan-1.md) §6 Phase 6。作者同日确认：发布版 UI 以原生壳为主，Avalonia 旧壳退役；设计基准改由 `Design/` 总设计文档 + 设计令牌 + HTML 样机承担；阶段号不进代码注释，设计 ID 正式化。规则见 [Design/README.md](../Design/README.md)。

## 0. 已定决策（2026-10-18）

1. **退役方式**：一次性删除。6E 发布就绪后删 Avalonia，以 git tag `avalonia-final` 回溯，不保留双壳过渡。
2. **令牌**：`Design/tokens.json` 为唯一数值来源，生成 `PhotoViewer/Core/Design/DesignTokens.g.cs`（共享程序集，四个头工程都引用）；Windows 链接 `PhotoViewer.Shared/`，`WindowsLayout.cs` 的自持常量改为转接共享几何与令牌（已完成）。
3. **优先级**：性能类（总纲 §8 性能门、切图卡顿、RAW 冷图）与 Training 仓 `DatasetBuilder` 去 Avalonia 不阻塞 UI 设计与功能可用性，置后。性能门不再作为切换发布通道的前置（总纲 §6 "未达标前不切换发布通道"按此修订）。
4. **对标强度**：主窗口几何/动效对齐令牌与样机；小控件、设置、工具只对齐功能与操作（Design/README §3）。

## 1. 里程碑

| 里程碑 | 内容 | 前置 | 状态 |
|---|---|---|---|
| 6A 基线冻结 | 旧壳几何/截图入 `Design/baseline/avalonia/`；tag `avalonia-final` | — | ✅ 2026-10-18 |
| 6B 设计体系 | Design/README 规则；tokens 草稿；ui-contract 拆分为 spec/platforms/status；L0–L2 功能清单 | 6A | ✅ 2026-10-18（裁定经作者通过）；**ui-contract 跳转化完成 2026-10-19**（全仓链接改指 `spec/`、README、platforms、status） |
| 6C 功能审查与统一 | 按 L2 清单四端逐条审查，产出缺口表并裁定；按 [统一清单](native-rebuild-phase-6-unify.md) 四端改代码 | 6B 清单 | ✅ 2026-10-19 四端统一完成；Windows 已由作者在 Windows 本机验收并合入主线；剩余作者手验见统一清单 |
| 6D Avalonia 退役 | 见 §3 | 6E-1 | ✅ 2026-10-19（合并 `640d52d`；Mac/iOS/Android 程序化 PASS，Windows 仅 macOS 类型检查 0/0，待作者在 Windows 复核） |
| 6E 发布就绪 | 见 §4 | 6C 阻塞项 | ◐ 6E-1 随 6D 完成（Release 默认原生壳）；6E-2～6E-7 待办 |
| 6F 样机与令牌落地 | HTML 样机、几何导出比对、令牌代码生成与四端接入 | 6B | ◐ 令牌生成器 `Tools/generate-design-tokens.py` 与四端接入完成 10-18；HTML 样机未开始 |
| 6G 台账与文档 | 遗留 backlog、文档矛盾修正、阶段文档归档、阶段号注释清理 | 穿插 | ◐ 2026-10-19：退役后文档收口与 §6 可核实矛盾已修；其余见 §6 保留项 |

> 2026-10-19 执行顺序调整为：6B → 6C/6D（6E-1 随 6D 成立）→ 6F 与 6E 其余 → 6G 收尾。

## 2. 6B 设计体系

- [x] `Design/README.md`：分层、规范等级、ID 规则、权威顺序、变更流程。
- [x] `Design/tokens.json` 草稿（2026-10-18）：202 个令牌，每个带已核对的 `file:line` 来源；平台不同值记在 `platforms` 字段；未实现的设计意图（`status.loading.delay`）记 `null` + `todo`。
- [x] `Design/spec/` 草稿（2026-10-18）：L0 原则 9 条、L1 分区、L2 14 个模块共 127 条规则（原 ui-contract 22 个 ID 全部保留，新增 `MENU`/`OPEN`/`STATE` 等）。尚需作者审阅。
- [x] `Design/platforms.md`、`Design/status.md` 草稿（2026-10-18）：按模块锚点组织；状态格只写状态词 + 日期 + 短语，证据链接计划文档。
- [x] ID 迁移：`ANL-02`–`04` → `ANALYSIS-02`–`04`，`ANL-01` 退役。
- [x] 裁定 [questions.md](native-rebuild-phase-6-rulings/questions.md)（2026-10-18）：作者授权按"多数 / 更优 / 原版"综合规则代为决策，桌面与移动可分开；结果回写 spec、tokens、platforms、status，代码改动列入 [统一清单](native-rebuild-phase-6-unify.md)。B30（iPad ARW 全分辨率）作者定暂缓：性能未测，并入性能专项。
- [x] 作者通过裁定（2026-10-18）；裁定依据与五端事实归档到 [phase-6-rulings/](native-rebuild-phase-6-rulings/)，`Design/spec/_drafts/`（模块笔记与备选稿）已删除。
- [x] ui-contract.md 只保留跳转；根 AGENTS.md、DEV.md 与模块 AGENTS 改指向（2026-10-19 完成；代码注释中无 `ui-contract.md` 引用，无需改代码）。

已裁定（2026-10-18）：显式打开（文件/文件夹/外部打开/拖入）主图回适应态（OPEN-05）；切图保持倍率只限批内（IMAGE-02）。

## 3. 6D Avalonia 退役

依据 2026-10-18 盘点（30 个 `.axaml`、53 个共享 `.cs` 编译期依赖 Avalonia；`Core/` 已零依赖）。**以下 1–7 全部完成（2026-10-19，合并 `640d52d`）。**

1. ✅ **VM 去 Avalonia 类型**：`ControlViewModel.PointerContext` 与视口命令删除；`ImageViewModel`、`AnalysisViewModel`/`AnalysisItem` 删除；`MainViewModel` 去掉 `UsesNativePresentation`/`nativePresentation`/`ImageVM`/`AnalysisVM` 与旧壳模态状态，新增 `FitToViewRequested`/`RequestFitToView()`（OPEN-05）；`DinoDebugViewModel` 只剩对 `DinoDiagnosticPresenter` 的中性转发；`ToolsViewModel` 仍持有它，但各壳不再 `is` 判型。
2. ✅ **头工程**：`--legacy-ui`/`--native`/`PVLegacyUI`/`pv-flags` 分流与 `#if DEBUG` 默认值全部删除；Mac `RunLegacy`/`LegacyLayoutAcceptance`/`MacExternalOpenBridge`、iOS 旧 `AppDelegate`/`PhotoViewerSceneDelegate`/旧外部打开路径/`Linker.xml` Avalonia 项、Android `MainActivity`/旧主题、Windows `RunLegacy` 已删。历史 `--native` 参数传入被无害忽略。
3. ✅ **共享工程**：`Views/`、`Controls/`、`Converters/`、`Behaviors/`、`Platform/Avalonia*`、`DecodedImageAdapter`、`Windows/`、`App.axaml*`、`ViewLocator.cs` 已删；剩余工程未改名（仍为 `PhotoViewer/`，`Views*` 三个模块 AGENTS 一并删除）；EXIF 格式化器移到 `Core/Exif/ExifDisplayConverters.cs`，排序参数在 `ViewModels/Settings/MoveCommandParameter.cs`。
4. ✅ **包与资源**：`Directory.Packages.props` 删除 Avalonia 全系、`ReactiveUI.Avalonia`、`Xaml.Behaviors.Interactions.Custom`、`Avalonia.Browser`；共享工程直接引用 `ReactiveUI 23.2.1`；DINO 模型改由各头 bundle/asset/嵌入资源打包（`PhotoViewer.dll` 不再内嵌，iOS/Windows 重复打包消除）；`uicons-solid-straight.ttf` 删除，`uicons-regular-rounded.ttf` 保留（Android 使用）。**保留项**：`Directory.Packages.props` 仍保留 `Avalonia` / `Avalonia.Desktop` 两个版本条目，仅供独立仓 `Training/DatasetBuilder` 使用（去 Avalonia 置后）；`avares://` 资源 URI 约定保留，作为各壳 `IAppResourceProvider` 的路径约定（不再指向 Avalonia 资源系统）。
5. ✅ **工具**：`Tools/RawDecodeTool` 改用 `LocalStorageRefs`；ColorTest/CvDebug/ExifTest 只依赖 Core。
6. ✅ **工程配置**：`.vscode/tasks.json` 删除 Legacy/Compare 任务、Release 任务去 `--native`（`Debug iOS (Native)` 并入 `Debug iOS`）；`.vscode/extensions.json` 删 Avalonia 扩展；`.run/` 修 `net9.0` 路径；DEV.md、README、根与模块 AGENTS 改写。
7. ✅ **验收门**：Mac Debug/Release 0 错误；Mac 九套 harness PASS（smoke 39）；iOS 模拟器七套（photo 10 / layout 21 / input 31 / analysis 18 / similarity 9+NOTE / tools 8 / gesture 22）；Android 五模式 main 14 / analysis 18 / similarity 15 / settings 16 / tools 16；Windows **仅 macOS 类型检查 0/0，需作者在 Windows 本机复核**；各平台产物内无 `Avalonia*`。

旧壳专属待办随删除关闭：已缓存描边被覆盖、分析瓦片双层圆角、纵向可见范围估算、`--pv-legacy-layout` 采集、双份 `DecodedImage`+`WriteableBitmap` 缓冲。**保留项**：`Training/DatasetBuilder` 去 Avalonia（独立仓，置后，须先跑 `verify_onnx_parity.py`）；`avares://` URI 约定；`Directory.Packages.props` 为 Training 保留的 `Avalonia` / `Avalonia.Desktop` 版本。

## 4. 6E 发布就绪

| # | 事项 | 平台 | 备注 |
|---|---|---|---|
| 6E-1 | ✅ **已完成 2026-10-19**：Release 默认进原生壳（随 6D 删除分流后自然成立） | 全部 | 发布脚本与 tasks 已同步；四平台 Release 都直接进原生壳 |
| 6E-2 | 原生单文件发布：缺 `.pri` 致 `Application.Start` 抛 `0x80040111`；体积 239MB 是否接受 | Windows | P2-W §1；多文件发布可用，单文件未打通 |
| 6E-3 | Developer ID 签名 + 公证（现 adhoc） | macOS | 待作者决定是否立项 |
| 6E-4 | App Store 合规冲刺：权限用途字符串、隐私清单、ATS、TestFlight、审核材料；批量评分写回进度与失败汇总 | iOS | 总纲 §4.5 |
| 6E-5 | `SQLitePCLRaw.lib.e_sqlite3` 2.1.11 高危（NU1903） | 全部 | iOS 上架前必须处理 |
| 6E-6 | Release 签名材料、真机冒烟 | Android | `signing.json` 本机配置 |
| 6E-7 | `ValidateXcodeVersion=false` 在 workload 支持 Xcode 27.1 后移除 | Mac/iOS | 非阻塞 |

## 5. 遗留事项（6C 审查前的已知项）

完整清单由 2026-10-18 文档盘点产生，6G 合并为单一 backlog。2026-10-19 按 `Design/status.md` 复核后：

- **Windows**：`INPUT-01` 文本框获焦不暂停全局快捷键（状态仍为待核验）；W-M7 色彩（ICC/ACM/跨屏）与 RAW 解码（LibRaw）缺失。~~`IMAGE-02` Fit 无对焦点优先与对焦跟随切图~~ → **已由 6C W1 解决**（共享 `PhotoViewport.ToggleFit` 对焦优先，status.md Windows IMAGE-02 程序化通过 10-18，F 键对焦优先已作者手验）。
- **Android**：~~列表上下按钮排序~~ → **已改为拖拽**（`ItemTouchHelper` + 行尾把手，status.md SETTINGS-15 代码核对 10-18）；数值用 +/- 与拖入未接已登记为平台差异（SETTINGS-12@android / OPEN-07@android）；真机 P3 表面抬亮（现回退 sRGB）；`pv-check all` 偶发 SIGSEGV；API/方向/字体缩放/DPI 矩阵与真机门未跑。
- **macOS**：~~细节预览双击会清 `FollowsFocus`~~ → **已按规格统一**：`ANALYSIS-04` 规定"双击即结束跟随（显式定位优先），四端一致"，status.md macOS 程序化通过 10-16。
- **共享**：RAW 自动 sidecar 未实现。

作者手验待办（不阻塞开发，阻塞"作者手验通过"状态）：iPad 清单 39 项；Windows 200%/175% DPI、真实跨屏、八方向真实素材、W-M7 色彩/RAW（设置拖拽/录制与工具多格式/选择器已于 2026-10-18 由作者验收）；Android 验收清单全部；统一清单中 macOS/iOS 余项。

待作者决策：iOS 纯 AOT（`UseInterpreter`）、Android 手机分析栏放大与 EXIF 横滑、iPhone 紧凑布局、Android 平板。（Windows 工具窗口进任务栏/Alt+Tab 已于 2026-10-18 落地为普通窗口，见 `platforms.md` TOOLS-01@win。）

置后：性能门与切图卡顿、iPad ARW 全分辨率解码（`CIRAWFilter`，作者 10-18 定暂缓，现登记为 `SCOPE-04@ios` 平台限制）、iPad 分析栏可见翻页 p95、CoreML EP、Training `DatasetBuilder --dump-render` 去 Avalonia（牵涉 DINO 输入口径，须先跑 `verify_onnx_parity.py` 对照）。

## 6. 6G 文档矛盾（2026-10-19 复核）

| # | 矛盾 | 处置 |
|---|---|---|
| 1 | handoff 与 Android 验收清单仍写 Android "未开工" | ✅ 已修：handoff §2 改为"已开工并落地 A1b–A5，A6 程序化 PASS、矩阵/真机门待补"；Android 验收清单首段改为"待作者手验"进展说明 |
| 2 | 验证矩阵第一行日期 2026-10-19 | ✅ 已核：该行是 2026-10-19 Windows CLI 冒烟记录，日期正确；同表新增 6D 退役验证行 |
| 3 | iPad input 套件计数 27/28/29 不一致 | ✅ 已修：iOS AGENTS 补一条"退役后复核（2026-10-19）"统一给出当前计数（input 31），旧段落保留为历史轮次记录 |
| 4 | `native-rebuild-windows-p2.md` 指向不存在的 `phase-4-windows-acceptance.md` | ✅ 已修：文档清单改指本文件的 `native-rebuild-windows-p2.md` 与 `Design/spec/` |
| 5 | Windows 设置方案 §13 待确认项已按推荐落地 | ✅ 已核：自绘四按钮 + 50 DIP 热区、工具窗口进任务栏/可最大化、设置/工具窗口 960×720 均按推荐落地（见 `PhotoViewer.Windows/AGENTS.md`）；方案 §13 保留为历史决策记录 |
| 6 | 总纲 §4.1 编码口径与 Phase 5 `GLSurfaceView` 未回写 | ✅ 已修：§4.1 像素产物表按现行策略（mac F16 / iOS 代理 F16 + Full 8 位 P3 / Win 恒 F16 / Android 8 位）回写；Phase 5 标题改 `SurfaceView` + 自有 EGL14 |
| 7 | Phase 0/2 验收勾选未回写 | ⏳ **待核实**：Phase 0/2 为历史阶段文档，勾选状态是否需按最终结果回填未定；本轮不动其正文（改动会篡改历史验收记录） |
| 8 | ui-contract SHELL-01/TOOLBAR-01 的 Android 格子过期 | ✅ 随 6B 关闭：`ui-contract.md` 已跳转化，Android 现行状态以 `Design/status.md` 为准（SHELL-01 / TOOLBAR-01 为代码核对 10-18） |
| 9 | architecture §4 Android 展示解码为空 | ✅ 已修：补 `AndroidPresentationDecoder` + `AndroidPresentationEncodingPolicy`（恒 8 位 Rgba8） |
| 10 | DEV.md 称 Windows 原生单文件可用 | ✅ 已修：DEV.md 发布章节加"单文件缺 `.pri`、`0x80040111`、多文件可用"的已知限制 |
| 11 | iPad/Android 发现清单表格被空行截断 | ✅ 复核未见空行截断（iPad `native-rebuild-phase-ipad.md` §6、Android AGENTS 发现清单连续）；若后续再出现，按表格连续性修正 |

## 7. 发现清单

| # | 日期 | 发现 | 处置 |
|---|---|---|---|
| 1 | 2026-10-18 | 旧壳 Mac 主图已无法加载："展示编码策略未注入"。`41abd7b`（2026-10-17）把 `BitmapLoader.PresentationEncoding` 改为强制策略，旧壳路径未注入；`--pv-legacy-layout` 因此超时。旧 Release 包（默认旧壳）若基于当前代码构建同样受影响 | 不修：旧壳不再验收且已删除；基线沿用 2026-10-16 采集（旧壳视图代码此后未改）。**已随 6D 关闭（2026-10-19）**：旧壳与分流参数一并删除，Release 默认原生壳，不再存在"用当前代码发布默认旧壳"的路径 |
| 2 | 2026-10-18 | `avalonia-final` 之前的提交在当前工具链（Xcode 27.1 / macOS workload 27，最低 14.0）下无法直接构建 | 基线只能保存产物，不能依赖重新构建 |
