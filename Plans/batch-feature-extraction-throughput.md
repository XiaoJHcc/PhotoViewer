# 批量特征提取吞吐重构

## 目标与边界

用户主动提取时吞吐优先，四个原生平台共用一套有界流水线。保持 UI、评分和当前图请求响应；不按 Caution / Warning 等待 Normal，不改指纹、模型、CV 规格、数据库 schema、全分辨率分析与色彩方向口径。标准版仍不包含 DINO/批量索引；Web 仅保留合成任务状态，不模拟设备资源。

初始实现阶段按用户约束只做静态核对。用户随后授权空闲时验收：构建、自动化与真实提取均使用隔离目录和自有夹具，压力仅通过验收进程内的预算源模拟，不修改系统设置、用户照片、数据库或偏好。已有滚轮修复保留；旧提取补丁保存在 stash，不整体恢复。

## 实现结构

- 显式分析上下文：传递独立资源意图、CPU 并发调度器、取消令牌及准入拒绝原因；不冒充 P0，不改变机会型预取规则。
- 内存准入：显式分析按预算、可用余量和在途预留 CAS 准入，不因 Caution/Warning 整批停住；保留严重压力安全边界。根工作集许可覆盖解码、sRGB 转向和 CV 亮度临时数组，持有到计算完成。已在途分析释放许可后唤醒等待者；无可释放工作时回收非当前缓存和闲置池后有限重试，再明确失败。
- 独立分析解码：优先借源分辨率缓存，未命中直接复用注入的平台展示解码器，不写浏览 LRU；所有编码统一转显示朝向 sRGB8。临时图、内存许可与取消路径成对归还。
- 有界流水线：缺失评估与指纹组任务 → 缩略图准备/DINO 串行推理、Full 准备/CV 并行计算 → 单路写库。准备队列、结果队列和在途组数均有上界；成功部分立即持久化，同组失败格式可换伴侣；每组只推进一次进度。
- CPU：CV、分析色彩转换和 HEIF 转换消费同一全核有界调度器，避免每张照片独立展开整机并行。原生后端保留自身串行/安全限制，不增加并行推理会话。
- 推理：交互请求优先于尚未开始的批量请求；会话创建、输入准备、推理与释放位于同一保护区；压力修剪不在 UI 同步等待推理。
- 会话：VM 持有文件快照、独立访问租约和取消源，换目录不取消，宿主销毁取消。Android 停止态、iOS 进入后台取消当前批次并保留成功部分；回前台不隐式重启。桌面失焦不取消。
- 状态：进度合并投递，统计带目录/特征代际，提取完成等待最新计数提交再退出进行态，保持原锚点。错误用既有动作区承载，不加按钮、设置或说明文字。

## 检查与验收

静态核对覆盖 API 与调用点、标准版编译排除、四端编码/方向、取消/Channel 结束/资源所有权、等待唤醒、UI 调度与 diff 卫生。运行验收使用 DEV 的构建和原生冒烟入口，另运行现有 Core/色彩工具。

完整验收目标（本机已覆盖的范围见下表，未覆盖项仍保留）：

1. Caution 启动及中途进入 Caution 均持续推进；严重资源不足有界结束，不无限停留。
2. 冷库与部分缺失库的 DINO CLS/patch/CV 落库完整、重试只补缺失；同组格式失败回退与真实失败计数。
3. 大目录运行内存不随文件数增长，CPU/GPU/解码实际吞吐与阶段耗时可辨识；UI、评分、滚动、当前图响应。
4. 与旧算法输入和结果逐项对照；标准/AI 两版边界。
5. Android 停止/恢复、iOS 后台/前台、换目录和关闭窗口的租约与已完成持久化。
6. Windows/macOS/iOS/Android 各自标准构建与原生验收，不以一端结果代替其它端。

## 实现与验收结果

- 共享实现和四端接线已落盘。首次构建发现 `GroupTicket(FingerprintGroup, MissingParts)` 的可访问性错误，已修正后重新构建；本轮未修改无关告警。
- 首次需要 DINO 时在投递分析图前准备模型会话，避免 CV 工作集挤占首次初始化预算。预留仍是估算，不是原生 codec / ONNX 峰值上界；模型资源字节读取早于会话构建准入，真实峰值仍需取证。
- 每条路径成功立即落库并使派生缓存失效，取消排空已成功结果；进度按已收尾组计数，中断可小于总数。CPU-only native Run 使用与 CV 相同调度器对的独占通道，GPU 后端可与 CV 重叠；会话许可只覆盖构建，不把常驻会话计作长期在途预留。

| 检查 | 本机结果 |
|---|---|
| Windows Debug 标准 / AI 构建 | 均通过，0 错误；保留 WindowsBase 引用冲突告警 |
| Android Debug 标准 / AI 构建 | 均通过，0 错误；未安装或操作设备，保留原生库重复打包与 API 级别告警 |
| Windows AI 原生冒烟 | 41 PASS / 0 FAIL，真实 DirectML 模型推理；最终报告 `artifacts/acceptance/batch-throughput/windows-ai-full-resolution/checks.json` |
| Windows 标准版原生冒烟 | 39 PASS / 0 FAIL；报告 `artifacts/acceptance/batch-throughput/baseline-standard/checks.json` |
| Core 契约自检 AI | 1228 PASS / 1 SKIP / 0 FAIL |
| Core 契约自检标准版 | 1212 PASS / 1 SKIP / 0 FAIL；两版唯一跳过项均为 macOS 文件系统拼写身份 |
| 色彩通用转换对照 | `Tools/ColorEngineLutCheck general` 全部逐字节一致，涵盖广色域、行距、alpha、已释放源与取消 |
| macOS 标准 Debug / AI Release 构建 | 均通过，0 错误；保留既有裁剪与原生绑定告警 |
| iOS 标准模拟器 Debug / AI 真机 Release 构建 | 均通过，0 错误；保留既有裁剪告警 |
| macOS AI Release 批量定向验收 | Apple M5 / 10 核，CoreML，8/8 组通过，整批 7.28s；报告 `artifacts/acceptance/batch-throughput/mac-release-current.txt` |
| iPad AI Release 批量定向验收 | iPad mini A17 Pro / 6 核，CPU，两轮 8/8 组通过，整批 4.82s / 4.86s；报告 `artifacts/acceptance/batch-throughput/ipad-current-1.txt`、`ipad-current-2.txt` |
| iPad 原生相似面板 | 10 PASS / 0 FAIL；真实「提取全部」按钮、三路结果驱动的列表、双高亮与布局均通过；报告 `artifacts/acceptance/batch-throughput/ipad-native-similarity-checks.txt` |

### 提取定向场景

1. Caution 起跑：3 张 1200×800 自有 JPEG，经真实 VM 的「提取全部」完成 3/3，无错误；未提取数归零，每个指纹的 CLS/patch/CV 真查库齐备，空态只显示一处占位和按钮。最终一轮流水线 195ms，UI 心跳最大间隔 138ms。
2. 中途 Warning：8 份仓库 6000×4000 P3 夹具副本，新文件名保证新指纹。第 1 组完成时进入 Warning，仍完成 8/8，无失败；每组进度恰好一次、三路数据齐备，另逐组断言库内 CV 尺寸仍为 6000×4000。最终一轮流水线 1828ms，UI 心跳最大间隔 145ms。
3. 两轮定向检查中，停用套件的机会型预取以隔离缓存来源；排除当前展示照片后，待提取文件的浏览 Full 条目均为 0→0，无批量全图灌入。收尾恢复原预取状态和真实预算源。
4. Core 新增回归覆盖显式分析在 Caution/Warning 获批、普通后台在 Warning 仍拒、Critical 安全拒绝、可用量 0 拒绝、拒因嵌套/并发隔离、取消/应用代际、CPU 并发与独占通道互斥。
5. Apple 共用探针对同一 6000×4000 P3 夹具做 8 份新文件名副本，从 Caution 起跑、首组后进入 Warning，逐组核对 CLS/patch/CV、CV 尺寸非零与进度序列，并记录真实 `phys_footprint` 和在途预留。Mac 峰值预留 1088MB，iPad 两轮峰值 917/909MB；临时夹具均收尾删除，iOS 使用独立验收数据库。

### Apple 调度复核

- 生产调度未改。Mac 的 CoreML 仅接管 758 个节点中的 398 个，其余仍可能使用 CPU，但实测不能据此认定互斥更优：强制 CoreML native Run 与 CV 走同一独占调度器后，Debug 整批由原方案 7.54–7.66s 变为 7.86s，DINO/CV 单组耗时均因等待增加；保留 GPU 后端与 CV 重叠。
- Mac CoreML intra-op 从 10 线程裁到 5 线程为 7.59s，没有超出原方案波动范围的收益，未采纳。iPad 的 CPU 推理继续与 CV 互斥，避免 native intra-op 与 CV 双重占满 6 核。
- 两组 Mac Debug 基线写库合计仅 25/43ms，Release 为 35ms；writer 不是瓶颈。Mac 冷批次约 4.1s 用于首次 CoreML 会话构建，稳态主要受单路 DINO 推理限制；双缩略图解码头、单全图解码头、容量 1 的准备队列与有界背压未表现出需要扩队列的空等证据。
- 当前结论只覆盖上述 JPEG/P3 负载与两种候选，不宣称对所有素材和后端达到理论最优。首次 CoreML 构建成本和混合 RAW/HEIF 调度是后续独立优化对象，不在未取得收益时改动共享策略。

压力只在测试进程包装预算源，不实际占满整机内存，不代表真实低内存硬件结果；夹具构图简单，耗时仅为本机套件读数，不代表用户整库吞吐或提速倍数。Apple 冷批次包含真实首次会话构建。报告和压力日志保留，临时夹具已删除。

### 尚未覆盖

- macOS / iOS：已覆盖构建、真实 JPEG/P3 批量提取与压力推进；尚未覆盖真实混合 RAW/HEIF 大目录、长时间系统低内存及 iOS 退后台/回前台后的部分落库，不提升其它设计验收状态。Mac `--pv-native-similarity` 的真实提取完成，但既有行布局 2 项列表宽度断言失败（170 vs 200），属独立 UI 问题，未混入吞吐结论。
- Android：仅完成两版构建与共享 Core 回归；设备上的提取、退后台与恢复仍待验。
- Windows：本轮覆盖 JPEG/P3 大图与原有 HEIF 解码冒烟，尚未覆盖真实混合 RAW/HEIF 大目录、长时间真实系统低内存、批次中断后的部分落库，以及 CPU-only 原生模型吞吐；不能用 8 组夹具替代原 74 张用户目录验收。

复现命令：按 DEV 的 `dotnet build PhotoViewer.Windows -c Debug -p:PhotoViewerEnableDino=true|false`、`dotnet run --project PhotoViewer.Windows --no-build -c Debug -p:PhotoViewerEnableDino=... -- --pv-native-smoke --pv-acceptance-output=<独立目录>`；Core 用 `dotnet run --project Tools/CoreContractCheck -c Release -p:PhotoViewerEnableDino=true|false`；色彩用 `dotnet run --project Tools/ColorEngineLutCheck -c Release -p:PhotoViewerEnableDino=true -- general`。Mac 用 `dotnet build PhotoViewer.Mac -c Release -r osx-arm64 -p:PhotoViewerEnableDino=true` 后向 AI 版可执行文件传 `--pv-native-smoke --pv-native-batch Fixtures/jpeg-large/color-p3-large.jpg`；iPad 安装 AI Release 并拷入自有 JPEG 后传 `--pv-native-batch-check batch-input/color-p3-large.jpg`，从 `Documents/PhotoViewer/native-batch-throughput.txt` 取回报告。
