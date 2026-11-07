# Android 性能与内存专项方案 2

## 目标与边界

降低 Android 原生壳交互长尾、RAW 重复读取与临时内存峰值，补齐后台降载。保留代理精度、物理 100% Full、方向、色彩及像素租约契约；不以降低画质、推迟 Full 或扩大缓存掩盖停顿。不强制 RAW 首次直接 Full，不重写 EGL 线程模型。启动期原生异常独立调查，未确认根因不得宣称修复。

## 实施与验收

| 工作项 | 实施 | 当前结论 |
|---|---|---|
| 色彩准备与提交 | 实际表面色域捕获后单路后台准备，源实例仍作纹理缓存键；切图、Surface 重建、取消及释放作废旧结果；转换按行带取消，源/目标租约覆盖完整工作 | `perf` 同步/后台对照、GPU、旧请求、重建与租约通过；常规冷 GL 上传仍在 UI 线程 |
| 绘制合帧 | 手势、动画、布局及补帧通过下一动画帧合并，视口/Full 需求仍同步上报；只计真实交换 | 同轮 20 次更新合成 1 次交换；已定位 HIF 转换导致的 UI 调度饥饿并用共享 CPU 预算修复，20 轮真实 Full 交互与全像素对照 43/0；历史异常与设备覆盖边界另列 |
| RAW 读取与记账 | 活跃加载按文件引用身份共享池化字节，最后消费者撤登记；取消隔离、失败可重试；文件字节及前台 LibRaw 工作内存记账，精缩中间缓冲不漏账 | 真实 ARW 预览/代理共读一次；共享/独立读取两次 Full 全像素与元数据一致；实际会话物理 100% 为真实 Full |
| 后台降载 | OnStop 暂停预取、取消活跃代际，OnStart 恢复并重排；UiHidden 不伪报内存 Warning | 共享契约和真机活跃闸通过；HOME→返回同 PID 实测暂停/恢复、P3 EGL 重建 |
| 纹理字节预算 | 最近两帧及空纹理另受 `clamp(预算/12, 16MiB, 384MiB)` 合计上限，未知预算取 64MiB；当前纹理不降级 | 小预算真实 GPU 淘汰/最近对比命中/压力修剪及像素通过；不是低内存真机验收 |
| 源生命周期防护 | ColorEngine 真转换入口先持源租约，已释放源不进入原生；取消回收目标缓冲 | 既有工具新增释放/预取消/在途取消检查，全部逐字节对照通过；不冒充所有历史崩溃根因已修 |
| 启动可靠性 | 核对自有原生 ABI、Skia 偏移、工具链及真实退出记录 | 仍未关闭：历史 Skia 构造期 SIGSEGV、本轮 RenderThread SIGSEGV 与 Debug exit 255 不强行合并 |

## 实测与证据

环境：Windows / .NET SDK 10.0.201 / Android workload 36.1.53；小米 13（2211133C / Android 13 / API 33）。起点 `17ed13f`，工作区原干净。设备验收使用独立应用 ID `cc.xiaojh.PhotoViewer.perf2`，不覆盖正式应用或写用户照片。证据位于 `artifacts/acceptance/android-perf-2/`。

### 最终代码构建与回归

- 标准 Debug / Release 与独立 Debug / Release AOT 均构建成功，0 错误；完整重编最多 16 条既有警告（内存探针 API 版本及原生库重复），无新增警告。四次串行构建首次总命令超过 300 秒而中断，标准两种已完成；隔离两种随后单独重新构建成功，不以中断产物验收。
- CoreContractCheck：1132 PASS / 1 平台 SKIP / 0 FAIL；ColorTestTool：68 PASS；ColorEngineLutCheck：70 项逐字节比较通过，含释放源/取消回收；HEIF e2e / smoke 通过。
- 真机 Debug：`perf` 72/0、`color` 119/0、`zoom` 41/0、`input` 27/0、`main` 22/0。色彩启动有一次无报告退出，重启同包后通过，不能把它写成启动全稳。
- 真机 Release AOT：`perf` 72/0、`color` 119/0、`input` 27/0、`main` 22/0。`zoom` 首轮 40/1，唯一失败是实际会话 UI 心跳 330.7ms（当次上传 35.4ms，Looper doFrame 322ms）；同包未改代码的两次独立重跑 41/0，心跳 151.6/127.2ms。保留 `zoom-release-final.txt` 失败记录与 `-2/-3` 复跑，未放宽 250ms 门槛，具体长尾来源未定位。

### 24MP P3→sRGB 强制回退对照

同轮、同 6000×4000 图，Release 同步准备/提交 148.80ms（转换 124.39ms）；后台路径 UI 入队 0.08ms、后台转换 99.83ms、UI 同步提交 23.48ms，心跳最大间断 66.46ms。GPU 有效像素采样与正确目标色域逐通道一致。该数据是单轮强制回退对照，不是日常发生频率、p95、扫描完成时间或宣称端到端仅 23ms；GL 上传仍可能超过 16.7/8.3ms。

### RAW、纹理与持续翻页

- 44,339,200B 的真实 A7C01489.ARW，内嵌预览与代理开发共读一次，池化文件分配 46,137,344B，结束活跃登记 0。Full 7008×4672，共享读取与独立读取两次开发按 4672 行×28032B 全像素精确相同；方向与 Display P3/Rgba8 一致。未消除适应代理→Full 的两次开发，也未缓存开发源。
- 非寻址流维持托管数组上限；允许临时流→池缓冲复制，临时流容量增长及最终缓冲共存阶段分别准入。真机覆盖 300KB 受控流，不宣称实际云/管道 Provider 或极值验证。
- GPU 2MiB 最近缓存预算对照只保留可容纳的一帧，当前图不降级，最近对比仍命中，较旧帧重传且像素正确；大内存机仍最多最近两帧。
- Release 同一组 6 张 26/33MP HIF 扩成 60 个独立文件，顺序 60 翻与 A/B 60 翻均零超时完成（`stress-*-release-log.txt`）。生产 Release 不可 run-as，未提取私有 JSONL，故不编造该轮 p50/p95。Debug A/B 60 翻 0 超时、58 次纹理命中，数字不与 Release 历史基线混比。Debug 顺序在 140 秒采集窗口只完成 45 翻，随后为下一轮主动 force-stop，保留截断报告，不计为 60 翻通过。此轮不是 500 张长期上限验收。

## 可靠性发现与未关闭项

1. 历史 `sk_pixmap_read_pixels` 空指针机制可由已释放源送入原生触发，ColorEngine 与主图现分别持源租约并拒绝已释放输入；历史具体调用者无完整托管栈，不能据此宣布该起崩溃根因已关闭。
2. 历史另一起 Skia 动态库 constructor 在 `__system_property_get` 处 SIGSEGV，自有 RAW/HEVC ABI 未发现错误；此调用不经过 AndroidMemoryBudgetProvider（后者走 getprop 子进程）。未升级依赖或用初始化预热掩盖问题。
3. 本轮首次 Debug 验收在 RenderThread 出现 signal 11、无完整 tombstone；非增量重装后流程通过，不能证明与 incremental install 有因果。
4. 首轮 Debug 两次色彩专项启动在首图后 exit 255；pid 14003 的留存 Zygote 日志为 clean exit 255，当次 exit-info 未留存，不能用后续已经轮转的记录代替。前有并发 `System.Xml.XDocument.dll` CIL 映像失败日志，但相同提示也出现在通过轮次。续修已即时保存另一次 EXIT_SELF 255 的系统记录；该退出的具体调用者仍未取得原生栈。
5. 定向处置与新验收见下节：已补分段与系统调度轨迹、修复独立程序集映射路径及 HIF 转换 CPU 饥饿；不据此把上述不同历史异常强行归为同因。

## 启动退出与 UI 长尾续修

证据单独写入 `artifacts/acceptance/android-reliability/`，仍用小米 13，独立应用 ID `cc.xiaojh.PhotoViewer.reliability`。不覆盖正式安装、不写用户照片、不更换运行时或 SkiaSharp。

### 启动基线与定位

- 同一个 Debug APK：默认增量安装 12 轮色彩冷启动有 4 次退出（1 次 EXIT_SELF 255、2 次 SIGSEGV、1 次 Mono 断言 SIGABRT）；`--no-incremental` 重装后 12 轮仍有 2 次 SIGSEGV。通过轮次均为 119/0。增量安装不是必要条件。
- 非增量第 4、11 轮均抓到 `libmonodroid.so+0x721ac`，符号为 `EmbeddedAssemblies::open_from_bundles+3972`。反汇编对应读取 ELF 的 `e_shoff + section[10].sh_size`，不是图像解码或本应用主动退出路径。
- [Android 36.1.53 源码](https://github.com/dotnet/android/blob/36.1.53/src/native/mono/monodroid/embedded-assemblies.cc#L208-L254)：`map_runtime_file` 用共享 `file.data_size` 映射完整 ELF，随后先把该字段改为载荷长度、再发布 `file.data`；另一线程已经看到空指针却尚未取映射长度时，会使用缩短后的长度再次映射。[载荷解析](https://github.com/dotnet/android/blob/36.1.53/src/native/mono/monodroid/embedded-assemblies.hh#L297-L319) 未校验节表是否在映射内。
- 包内 XDocument wrapper 长 `0x81f0`，载荷长 `0x3f30`，节表大小读取位置 `0x81d0`；若按载荷长度重映射，读取位置明确越界。设备 PC、实际 ELF 布局及上游源码三者吻合，确认独立程序集 DSO 首次并发映射存在长度竞态。
- 修复使用 SDK 支持的 `AndroidUseAssemblyStore=true`（Debug 已自包含，符号仍保留），不预加载特定程序集、不串行化应用解码。APK 已核对不再含逐 DLL wrapper，使用 `libassembly-store.so`。
- EXIT_SELF 255 尚未取得原生调用栈，不能把上述段错误机制直接认定为它的具体调用者；新增 `PhotoViewerCrash` 只记录未处理异常、不吞异常。历史 Skia constructor 与 RenderThread 异常也不能仅凭同处启动窗口归为同因。

### UI 长尾定位与修复

- 修复前 Release 10 轮中 1 轮 255.8ms 超门槛，另外一次系统轨迹采集复现 336.3ms（`release-timing-zoom-*`、`release-trace1-*`）。该缺口内没有 Draw/Swap/应用回调执行样本，不能归咎于当轮 31.7ms 上传。
- 系统轨迹主线程 pid 29753：`1374435.821717` 睡眠，`1374435.837740` 被唤醒，却直到 `1374436.157769` 才上 CPU，约 320ms 是就绪后等待调度；与心跳的「16ms 睡眠 + 320ms 等待」吻合。8 核该窗口共 2560 CPUms，`.NET TP Worker` 占 2420.7ms；主线程 nice 4、主要工作线程 nice -10。不是以「CPU 高」泛猜，主导缺口是可运行线程的调度饥饿，不是 Swap 或 GC 阻塞。
- 首个保守方案把扣除一核后的预算按图像并发上限均分（本机每图 3 路），20 轮均 43/0、心跳最大 92.5ms，但 Full 解码中位数 786.2→1128.0ms，真实 Full 到达 1108.45→1448.8ms；不以该明显延迟代价收尾（证据 `release-bounded-final-*`）。
- 改用共享并发预算：Android 将 `MaxDegreeOfParallelism` 和进程级 `ConversionTaskScheduler` 上限都置为 `max(1, 核数 - 1)`，多幅转换共用执行槽，单图可用完整预算；共享层只接收可选调度器，未配置平台不变。单线程 Full 也服从注入调度器。像素算法、Full 请求时机与精度不变，不提高 UI 优先级。仍须同时验证心跳、Full 到达与多幅并发的总执行上限。
- `AndroidFrameStats` 默认关闭，`zoom` 真实 HIF 段才采样 Draw/Swap、根回调、调度与 Looper 区间，并附最大间隙的 GC / UI CPU。新增诊断的首版因在分发中提前释放打印器触发 Java 代理异常，已改成清日志后延至下一消息释放；失败日志保留，不把该诊断故障计作产品修复结果。
- `zoom` 在真实交互计时之后追加有界并行 Full 与串行 Full 全有效像素逐行对照（4128 行 × 24768B），同时核对色彩、方向与源几何，临时串行配置无论成败都还原。

### 续修验收结果

- Debug assembly store：30 轮同口径色彩冷启动全部 119/0，0 次退出、0 条真实程序集 CIL 错误（`debug-store-color-summary.json` 与逐轮日志）。最终完整代码再跑 5 轮色彩冷启动仍全部 119/0，无 CIL / 崩溃钩子记录；这是修复后的观测范围，不是全部历史退出签名均已归因。
- 共享预算 Release 20 轮均 43/0，原 250ms 门槛不变，全部保留真实 Full、GPU 像素及并行/串行全有效像素对照。每轮最大心跳间隙的中位数 / 最大值从 127.65 / 255.8ms 降为 62.0 / 93.1ms（前 10 轮 / 后 20 轮，不是所有帧的 p50/p95）；Full 解码中位数 786.2→805.05ms，真实 Full 到达中位数 1108.45→1121.25ms，消除了保守 3 路方案额外约 340ms 的延迟（`release-global-final-*`）。同机同素材前后分组，不宣称统计显著或零代价。
- 最终包系统轨迹复核：主线程睡眠唤醒后的最大就绪等待为 6.547ms，修复前为 320.029ms（`release-trace-global-runqueue-analysis.txt` / `release-trace1-runqueue-analysis.txt`）。保留两份压缩原始轨迹 `*-atrace.bin`，可用 `python artifacts/acceptance/android-reliability/analyze_runqueue.py release-trace1` 或 `release-trace-global` 重算，不依赖已清理的展开文本。
- 最终标准 Debug / Release 和隔离 Debug / Release AOT 均构建成功，0 错误、最多 16 条既有警告。最终两种配置的 `color` 119/0、`zoom` 43/0、`perf` 72/0、`input` 27/0、`main` 22/0，报告分别为 `debug-exact-final-*`、`release-exact-final-*`，Release 的最终 `zoom` 在 `release-trace-global-*`。
- CoreContractCheck：1132 PASS / 1 平台 SKIP / 0 FAIL；HEIF `selftest` / `e2e` / `smoke` 全部通过。新增调度自检覆盖 10 种直通/重采样编码组合与串行逐字节一致、4 幅并发共用上限 2（实测 queued=16 / peak=2）、度 1 必须经过注入调度器且无死锁；超时结束独立工具进程，避免未完成转换仍使用已释放的合成平面。
- 独立应用 `cc.xiaojh.PhotoViewer.reliability` 已卸载，`/data/local/tmp/pv-reliability-lldb`、本轮 `bin/reliability` 和 scratch 工具已清理，恢复原正式应用。保留标准构建产物、验收报告和两份压缩轨迹，没有覆盖正式安装或用户设置。

## 收尾约束

首次优化已按用户授权提交 `ab358ed`，本续修按用户授权提交 Git，不推送远端。4GB/6GB 真机、非小米 ROM、高刷全帧预算与历史 EXIT_SELF / Skia constructor / RenderThread 的独立归因仍未关闭；不把当前样本下的修复写成所有启动崩溃或所有 UI 长尾均已根治。
