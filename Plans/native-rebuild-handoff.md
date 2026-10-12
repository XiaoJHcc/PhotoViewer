# 原生重构交接文档(handoff)

> **给在 Mac 上接手的 agent**:本文是重构的当前状态真源 + 开工指引。写入规则:每次阶段推进后更新"当前状态"与"验证矩阵"。
> 最后更新:2026-10-19(Phase 0 完成时)。

## 1. 为什么重构(决策语境)

PhotoViewer 正在从 Avalonia 迁往四平台原生壳 + 共享 C# Core。驱动与决策全录于 [native-rebuild-master-plan-1.md](native-rebuild-master-plan-1.md)(总纲,含三大方向决策:全平台原生 / 只上 iOS App Store / Core 先行);色彩根因实证在 [color-managed-native-rendering-plan-1.md](color-managed-native-rendering-plan-1.md)(已被总纲收编为色彩专章)。

一句话:**色彩管理、切图性能、iPad 内存、UI 原生感、App Store 上架五个问题共用一套底层重构来根治**,不打补丁。阅读顺序:master plan → phase-0 清单(含发现清单,很多坑在那里)→ 本文。

## 2. 当前状态

- **分支**:`refactor/native-rebuild`(远端 github 已推送;`main` 保持可发布,未合并)。
- **Phase 0(Core 平台中立化,零行为变化)已完成**:Core 零 Avalonia 引用;像素货币 `DecodedImage` + SkiaSharp 像素基座;存储/调度/窗口/快捷键/资源五组门面与 Avalonia 适配层齐备。里程碑提交 M1→M6 逐笔可查。
- **Training 仓**:`DatasetBuilder` 已同步适配(独立提交);`docs/HUMAN.md` 有用户自己的未暂存改动,不要动它。
- **下一步 = Phase 1**(统一内存架构 MemoryGovernor + 分级代理解码):执行清单 [native-rebuild-phase-1.md](native-rebuild-phase-1.md),验收门需 iPad 真机。

## 3. 验证矩阵

| 范围 | 状态 | 说明 |
|---|---|---|
| Windows Desktop 构建 + 冒烟 | ✅ 2026-10-19 | CLI 开 JPG(竖图旋转正确)+ HIF(LibHeif),翻图/胶片条/EXIF/评分渲染正常 |
| Android 构建 | ✅ | 真机冒烟未做 |
| Training.sln / Tools 构建 | ✅ | CvDebugTool / ExifTestTool / RawDecodeTool(Mac 专用,需在 Mac 构建复核) |
| **macOS 构建** | ❌ 待做 | M3/M4 对头工程是盲改:`dotnet build PhotoViewer.Mac` 是第一件事 |
| **iOS 构建 + iPad 冒烟** | ❌ 待做 | 同上;`Debug iOS` 走真机 |
| DINO 渲染口径 | ⚠️ 理论等价 | `RenderInputImage` 改 Skia Linear 采样;跑 `Training/onnx/verify_onnx_parity.py` + 抽样对比渲染缓存(`--dump-render`)确认分布未漂移 |
| 四平台人工冒烟 | ❌ 待做 | 翻图/缩放/评分写回/设置页/聚类/分析栏逐项过 |

## 4. Mac 开工步骤(接手第一天)

1. `git fetch && git checkout refactor/native-rebuild`,通读 §1 的三份文档。
2. `dotnet build PhotoViewer.Mac` + `dotnet build PhotoViewer.iOS` —— 修到头工程(M3/M4 盲改区)编译错;错处大概率是签名/类型小毛边,对照 Desktop 的同名文件改。
3. VS Code Task `Debug Mac` 跑起来人工冒烟(对照 §3 矩阵勾项);`Debug iOS` 上 iPad。
4. 跑 DINO 口径验证(§3 最后一项)。
5. 全部通过后:Phase 1 按 [native-rebuild-phase-1.md](native-rebuild-phase-1.md) 里程碑开工。

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
