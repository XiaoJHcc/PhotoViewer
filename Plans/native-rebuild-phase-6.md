# Phase 6 — 原生壳转正、Avalonia 退役与设计基准建立

> 2026-10-18 立项。对应 [总纲](native-rebuild-master-plan-1.md) §6 Phase 6。作者同日确认：发布版 UI 以原生壳为主，Avalonia 旧壳退役；设计基准改由 `Design/` 总设计文档 + 设计令牌 + HTML 样机承担；阶段号不进代码注释，设计 ID 正式化。规则见 [Design/README.md](../Design/README.md)。

## 0. 已定决策（2026-10-18）

1. **退役方式**：一次性删除。6E 发布就绪后删 Avalonia，以 git tag `avalonia-final` 回溯，不保留双壳过渡。
2. **令牌**：`Design/tokens.json` 为唯一数值来源，生成 `PhotoViewer/Core/Design/DesignTokens.g.cs`（放共享程序集，共享会话与 Windows 不链接 Shared 也能引用）；Windows 改为链接 `PhotoViewer.Shared/`，替换 `WindowsLayout.cs` 的自持常量。
3. **优先级**：性能类（总纲 §8 性能门、切图卡顿、RAW 冷图）与 Training 仓 `DatasetBuilder` 去 Avalonia 不阻塞 UI 设计与功能可用性，置后。性能门不再作为切换发布通道的前置（总纲 §6 "未达标前不切换发布通道"按此修订）。
4. **对标强度**：主窗口几何/动效对齐令牌与样机；小控件、设置、工具只对齐功能与操作（Design/README §3）。

## 1. 里程碑

| 里程碑 | 内容 | 前置 | 状态 |
|---|---|---|---|
| 6A 基线冻结 | 旧壳几何/截图入 `Design/baseline/avalonia/`；tag `avalonia-final` | — | ✅ 2026-10-18 |
| 6B 设计体系 | Design/README 规则；tokens 草稿；ui-contract 拆分为 spec/platforms/status；L0–L2 功能清单 | 6A | ✅ 2026-10-18（裁定经作者通过；ui-contract 跳转化待做） |
| 6C 功能审查与统一 | 按 L2 清单四端逐条审查，产出缺口表并裁定；按 [统一清单](native-rebuild-phase-6-unify.md) 四端改代码 | 6B 清单 | 裁定经作者通过 10-18，代码未开始 |
| 6D Avalonia 退役 | 见 §3 | 6E-1 | 未开始 |
| 6E 发布就绪 | 见 §4 | 6C 阻塞项 | 未开始 |
| 6F 样机与令牌落地 | HTML 样机、几何导出比对、令牌代码生成与四端接入 | 6B | 未开始 |
| 6G 台账与文档 | 遗留 backlog、文档矛盾修正、阶段文档归档、阶段号注释清理 | 穿插 | 未开始 |

建议执行顺序：6B → 6C 与 6F 并行 → 6E-1（Release 切原生）→ 6D → 6E 其余 → 6G 收尾。

## 2. 6B 设计体系

- [x] `Design/README.md`：分层、规范等级、ID 规则、权威顺序、变更流程。
- [x] `Design/tokens.json` 草稿（2026-10-18）：202 个令牌，每个带已核对的 `file:line` 来源；平台不同值记在 `platforms` 字段；未实现的设计意图（`status.loading.delay`）记 `null` + `todo`。
- [x] `Design/spec/` 草稿（2026-10-18）：L0 原则 9 条、L1 分区、L2 14 个模块共 127 条规则（原 ui-contract 22 个 ID 全部保留，新增 `MENU`/`OPEN`/`STATE` 等）。尚需作者审阅。
- [x] `Design/platforms.md`、`Design/status.md` 草稿（2026-10-18）：按模块锚点组织；状态格只写状态词 + 日期 + 短语，证据链接计划文档。
- [x] ID 迁移：`ANL-02`–`04` → `ANALYSIS-02`–`04`，`ANL-01` 退役。
- [x] 裁定 [questions.md](native-rebuild-phase-6-rulings/questions.md)（2026-10-18）：作者授权按"多数 / 更优 / 原版"综合规则代为决策，桌面与移动可分开；结果回写 spec、tokens、platforms、status，代码改动列入 [统一清单](native-rebuild-phase-6-unify.md)。B30（iPad ARW 全分辨率）作者定暂缓：性能未测，并入性能专项。
- [x] 作者通过裁定（2026-10-18）；裁定依据与五端事实归档到 [phase-6-rulings/](native-rebuild-phase-6-rulings/)，`Design/spec/_drafts/`（模块笔记与备选稿）已删除。
- [ ] ui-contract.md 只保留跳转；根 AGENTS.md、DEV.md 与模块 AGENTS 改指向。

已裁定（2026-10-18）：显式打开（文件/文件夹/外部打开/拖入）主图回适应态（OPEN-05）；切图保持倍率只限批内（IMAGE-02）。

## 3. 6D Avalonia 退役

依据 2026-10-18 盘点（30 个 `.axaml`、53 个共享 `.cs` 编译期依赖 Avalonia；`Core/` 已零依赖）。

1. **VM 去 Avalonia 类型**：`ControlViewModel.PointerContext` 的 `Avalonia.Vector`；`ImageViewModel`、`AnalysisViewModel`/`AnalysisItem`、`DinoDebugViewModel` 是旧壳显示适配器，删除或改为共享类型。`MainViewModel.cs:104-105` 原生模式仍构造 `ImageViewModel`/`AnalysisViewModel`（后者 `new ShakeFieldView()` 并建第二份 `AnalysisPresenter`），`ToolsViewModel` 无条件建 `DinoDebugViewModel`；各壳工具页改用共享工具页契约，不再 `is DinoDebugViewModel` 判型。
2. **头工程**：删 `--legacy-ui`/`--native`/`PVLegacyUI`/`pv-flags` 分流与 `#if DEBUG` 默认值；删 Mac `RunLegacy`/`LegacyLayoutAcceptance`/`MacExternalOpenBridge`，iOS `AppDelegate`(Avalonia)/`PhotoViewerSceneDelegate`、`iOSExternalOpenBridge` 旧路径、`iOSSceneBridge` 的 `Application.Current`、`Linker.xml` Avalonia 项，Android `MainActivity`/`AndroidExternalOpenBridge`/旧主题与 `AndroidNativeShell.cs:98` 日志，Windows `RunLegacy`。
3. **共享工程**：删 `Views/`、`Controls/`、`Converters/`、`Behaviors/`、`Platform/Avalonia*`、`DecodedImageAdapter`、`Windows/`、`App.axaml*`、`ViewLocator.cs`；`Settings*.cs` 的无用 `using PhotoViewer.Controls`；`PhotoViewer.csproj` 失效的 `Compile Update` 项。评估是否把剩余工程改名为 `PhotoViewer.Core`。
4. **包与资源**：`Directory.Packages.props` 删 Avalonia 全系、`ReactiveUI.Avalonia`（改直接引用 `ReactiveUI`，核对 `RxAppBuilder`）、`Xaml.Behaviors.Interactions.Custom`、未用的 `Avalonia.Browser`；`AvaloniaResource` 改为各头的 bundle/asset 链接，消除 iOS/Windows 重复打包的约 86MB 模型；`uicons-solid-straight.ttf` 删除，`uicons-regular-rounded.ttf` Android 仍在用须保留；`avares://` 资源 URI 约定改为平台中立形式（各壳 `IAppResourceProvider` 同步）。
5. **工具**：`Tools/RawDecodeTool` 改用 `LocalStorageRefs`；ColorTest/CvDebug/ExifTest 只依赖 Core，随引用自动去 Avalonia。
6. **工程配置**：`.vscode/tasks.json` 删 Legacy/Compare 任务，Release 任务去 `--native`；`.vscode/extensions.json` 删 Avalonia 扩展；`.run/` 修旧 `net9.0` 路径；DEV.md、README、根与模块 AGENTS 改写；`PhotoViewer/Views*` 三个模块 AGENTS 删除。
7. **验收门**：四平台 Debug/Release 构建 0 错误；Mac 八套、iOS 七套、Windows 三套、Android 五模式程序化检查 PASS；Release 包内无 `Avalonia*.dll`。

旧壳专属待办随删除关闭：已缓存描边被覆盖、分析瓦片双层圆角、纵向可见范围估算、`--pv-legacy-layout` 采集、双份 `DecodedImage`+`WriteableBitmap` 缓冲。

## 4. 6E 发布就绪

| # | 事项 | 平台 | 备注 |
|---|---|---|---|
| 6E-1 | Release 默认进原生壳（随 6D 删除分流后自然成立） | 全部 | 发布脚本与 tasks 同步 |
| 6E-2 | 原生单文件发布：缺 `.pri` 致 `Application.Start` 抛 `0x80040111`；体积 239MB 是否接受 | Windows | P2-W §1 |
| 6E-3 | Developer ID 签名 + 公证（现 adhoc） | macOS | 待作者决定是否立项 |
| 6E-4 | App Store 合规冲刺：权限用途字符串、隐私清单、ATS、TestFlight、审核材料；批量评分写回进度与失败汇总 | iOS | 总纲 §4.5 |
| 6E-5 | `SQLitePCLRaw.lib.e_sqlite3` 2.1.11 高危（NU1903） | 全部 | iOS 上架前必须处理 |
| 6E-6 | Release 签名材料、真机冒烟 | Android | `signing.json` 本机配置 |
| 6E-7 | `ValidateXcodeVersion=false` 在 workload 支持 Xcode 27.1 后移除 | Mac/iOS | 非阻塞 |

## 5. 遗留事项（6C 审查前的已知项）

完整清单由 2026-10-18 文档盘点产生，6G 合并为单一 backlog。阻塞功能可用性的：

- **Windows**：`INPUT-01` 文本框获焦不暂停全局快捷键；`IMAGE-02` Fit 无对焦点优先与对焦跟随切图；W-M7 色彩（ICC/ACM/跨屏）与 RAW 解码（LibRaw）缺失。
- **Android**：设置数值用 +/-、列表上下按钮排序、拖入未接；真机 P3 表面抬亮（现回退 sRGB）；`pv-check all` 偶发 SIGSEGV；API/方向/字体缩放/DPI 矩阵与真机门未跑。
- **macOS**：细节预览双击会清 `FollowsFocus`（与原设计不同）。
- **共享**：RAW 自动 sidecar 未实现。

作者手验待办（不阻塞开发，阻塞"作者手验通过"状态）：iPad 清单 39 项；Windows 设置拖拽/录制、工具多格式与选择器、200%/175% DPI、跨屏、八方向真实素材；Android 验收清单全部。

待作者决策：iOS 纯 AOT（`UseInterpreter`）、Android 手机分析栏放大与 EXIF 横滑、Windows 工具窗口是否进 Alt+Tab、iPhone 紧凑布局、Android 平板。

置后：性能门与切图卡顿、iPad ARW 全分辨率解码（`CIRAWFilter`，作者 10-18 定暂缓，现登记为 `SCOPE-04@ios` 平台限制）、iPad 分析栏可见翻页 p95、CoreML EP、Training `DatasetBuilder --dump-render` 去 Avalonia（牵涉 DINO 输入口径，须先跑 `verify_onnx_parity.py` 对照）。

## 6. 6G 文档矛盾（已知，待修）

handoff 与 Android 验收清单仍写 Android "未开工"；验证矩阵第一行日期 2026-10-19；iPad input 套件计数 27/28/29 不一致；`native-rebuild-windows-p2.md` 指向不存在的 `phase-4-windows-acceptance.md`；Windows 设置方案 §13 待确认项已按推荐落地；总纲 §4.1 编码口径与 Phase 5 `GLSurfaceView` 未回写；Phase 0/2 验收勾选未回写；ui-contract SHELL-01/TOOLBAR-01 的 Android 格子过期；architecture §4 Android 展示解码为空；DEV.md 称 Windows 原生单文件可用；iPad/Android 发现清单表格被空行截断。

## 7. 发现清单

| # | 日期 | 发现 | 处置 |
|---|---|---|---|
| 1 | 2026-10-18 | 旧壳 Mac 主图已无法加载："展示编码策略未注入"。`41abd7b`（2026-10-17）把 `BitmapLoader.PresentationEncoding` 改为强制策略，旧壳路径未注入；`--pv-legacy-layout` 因此超时。旧 Release 包（默认旧壳）若基于当前代码构建同样受影响 | 不修：旧壳不再验收且将删除；基线沿用 2026-10-16 采集（旧壳视图代码此后未改）。在 6D 完成前**不得**用当前代码发布默认旧壳的 Release |
| 2 | 2026-10-18 | `avalonia-final` 之前的提交在当前工具链（Xcode 27.1 / macOS workload 27，最低 14.0）下无法直接构建 | 基线只能保存产物，不能依赖重新构建 |
