# 原生重构交接文档(handoff)

> **给在 Mac 上接手的 agent**:本文是重构的当前状态真源 + 开工指引。写入规则:每次阶段推进后更新"当前状态"与"验证矩阵"。
> 最后更新:2026-10-12(Phase 0 定向审查通过、Phase 1 清单修订落定时)。

## 1. 为什么重构(决策语境)

PhotoViewer 正在从 Avalonia 迁往四平台原生壳 + 共享 C# Core。驱动与决策全录于 [native-rebuild-master-plan-1.md](native-rebuild-master-plan-1.md)(总纲,含三大方向决策:全平台原生 / 只上 iOS App Store / Core 先行);色彩根因实证在 [color-managed-native-rendering-plan-1.md](color-managed-native-rendering-plan-1.md)(已被总纲收编为色彩专章)。

一句话:**色彩管理、切图性能、iPad 内存、UI 原生感、App Store 上架五个问题共用一套底层重构来根治**,不打补丁。阅读顺序:master plan → phase-0 清单(含发现清单,很多坑在那里)→ 本文。

## 2. 当前状态

- **分支**:`refactor/native-rebuild`(远端 github 已推送;`main` 保持可发布,未合并)。
- **Phase 0(Core 平台中立化,零行为变化)已完成且验收门全关**:Core 零 Avalonia 引用;像素货币 `DecodedImage` + SkiaSharp 像素基座;存储/调度/窗口/快捷键/资源五组门面与 Avalonia 适配层齐备;机械门/持久化契约/Mac+iOS 构建/Mac 冒烟/DINO 口径全部通过(细节见 §3 矩阵与 phase-1 §5 发现清单)。里程碑提交 M1→M6 逐笔可查。
- **Phase 0 定向审查(2026-10-12)已通过**:Phase 0 由同一 agent 实现+自审(mimo 超时),本次补独立审查——机械门复核 PASS(Core 零 Avalonia,残留均注释;VM 零 UI 耦合);手势持久化契约 PASS(`GestureKey` 223 名 ↔ Avalonia 12.0.2 `Key` 223 名双向 diff 为空,`GestureModifiers` 名/值与 `KeyModifiers` 一致,字符串枚举序列化下逐字节兼容);Mac/iOS 构建 0 错误(仅 NU1903 基线警告)。审查发现的 Phase 1 计划修订已全部落回 phase-1 清单(准入口优先级语义、分档缓冲池、解码路径平台差异、统一 LRU 键前置 M2、验收门平台口径)。
- **Training 仓**:`DatasetBuilder` 已同步适配(独立提交);`docs/HUMAN.md` 有用户自己的未暂存改动,不要动它。
- **下一步 = P1-M3**(解码准入接线 + 淘汰/生命周期修复 + DecodedImage 分档缓冲池 + ShakeFlagService 竞态):执行清单 [native-rebuild-phase-1.md](native-rebuild-phase-1.md)(**先读其 §5 发现清单**——2026-10-12 审查裁定已修订 M1-M4 多处)。
- **P1-M1 已完成**(commit `P1-M1`):`Core/Memory/` 新模块——MemoryGovernor + 三平台预算源 + `PerfTrace` 埋点;自测 24 项全过;Mac 冒烟正常。
- **P1-M2 已完成**(commit `P1-M2`):统一 LRU(路径+尺寸档三档分区,缩略图迁入并设 2000 张/256MB 上限)+ 全部缓存登记为内存客户端 + 压力级联修剪 + 行为响应矩阵(预取停/并发降/索引暂停/恢复观察期);自测 31 项全过;三平台构建零警告;Mac 冒烟正常。

## 2.1 过程决策(2026-10-12 与作者对齐)

- **人工验收推迟到原生壳阶段**:Phase 0/1 在 Avalonia 旧壳内,UI 形态是过渡载体、仅为产品参考,**不要求严格复刻**;此阶段回归项(评分写回/指纹 DB/AI 索引/分析栏)由 agent 程序化自验(读写回环、日志断言、截图),不做人工交互验收。原生壳阶段(Phase 2 起)功能项对齐即可,允许趁机做 UI/手感优化——plan-1 §7"交互规格以现有行为为准逐条复刻"的要求就此放宽。
- **iPad 验证策略**:开发期用 iOS 模拟器(功能 + Simulate Memory Warning 驱动 MemoryGovernor 响应矩阵);**真机只在关键环节**:P1-M5 验收门(jetsam 压测——模拟器无内存上限,验不了)、以及各 Phase 的原生壳真屏验收。在役机型 = iPad mini 7(A17 Pro,8GB,主流配置而非低配,压力阈值校准按 8GB 档)。

## 3. 验证矩阵

| 范围 | 状态 | 说明 |
|---|---|---|
| Windows Desktop 构建 + 冒烟 | ✅ 2026-10-19 | CLI 开 JPG(竖图旋转正确)+ HIF(LibHeif),翻图/胶片条/EXIF/评分渲染正常 |
| Android 构建 | ✅ | 真机冒烟未做 |
| Training.sln / Tools 构建 | ✅ | CvDebugTool / ExifTestTool / RawDecodeTool(Mac 专用,需在 Mac 构建复核) |
| **macOS 构建** | ✅ 2026-10-12 | 0 错误,仅 NU1903 基线警告 |
| **macOS 冒烟(首轮)** | ✅ 2026-10-12 | CLI 开 ARW:MacRawDecoder 链路主图/胶片条/EXIF/评分栏渲染正常;交互逐项(翻图/缩放/评分写回/设置)待人工 |
| **iOS 构建** | ✅ 2026-10-12 | 模拟器目标 0 错误;真机构建(`Debug iOS`)+ iPad 冒烟待做 |
| DINO 渲染口径 | ✅ 2026-10-12 | A/B 实证(headless 不可行,用真窗口 GPU 路径):zoneplate 摩尔纹形态两侧一致(同为 Linear/无 mipmap 族),真实内容 meanDiff=0.24/maxDiff=15(GPU↔CPU 舍入级)。**意外发现:macOS 上 `RenderTargetBitmap.CopyPixels` 实为 RGBA 序**,旧管线 Mac 端 DINO 输入 R/B 互换(Windows 缓存正常);新 Skia 路径统一 BGRA,顺带根治 |
| 四平台人工冒烟 | ⏸ 按 §2.1 决策推迟 | 旧壳阶段 agent 程序化自验;人工验收自原生壳阶段开始 |

## 4. Mac 开工步骤(接手第一天)

1. `git fetch && git checkout refactor/native-rebuild`,通读 §1 的三份文档。
2. ~~`dotnet build PhotoViewer.Mac` + `dotnet build PhotoViewer.iOS`~~ ✅ 2026-10-12:两头工程均 0 错误(仅 NU1903 基线警告),M3/M4 盲改区无编译毛边。
3. 冒烟:Mac 首轮已过(见 §3);交互逐项(翻图/缩放/评分写回/设置页/聚类/分析栏)人工过;`Debug iOS` 上 iPad 真机。
4. 跑 DINO 口径验证(§3):`Training/onnx/verify_onnx_parity.py` + `--dump-render` 抽样对比渲染缓存。
5. 全部通过后:Phase 1 按 [native-rebuild-phase-1.md](native-rebuild-phase-1.md) 里程碑开工(**先读其 §5 发现清单**——2026-10-12 审查裁定已修订 M1-M4 多处:准入口优先级语义、统一 LRU 键前置 M2、分档缓冲池、解码路径平台差异)。

## 5. 工作纪律(与主仓 AGENTS.md §8/§9/§10 一致,此处强调重构特有条目)

- 每里程碑一个 commit(`P<n>-M<m>: ...`);编译零警告才能提交;AGENTS.md(根+模块)同任务同步。
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
