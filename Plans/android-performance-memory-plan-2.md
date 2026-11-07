# Android 性能与内存专项方案 2

## 目标与边界

降低 Android 原生壳交互长尾、RAW 重复读取与临时内存峰值，补齐后台降载。保留代理精度、物理 100% Full、方向、色彩及像素租约契约；不以降低画质、推迟 Full 或扩大缓存掩盖停顿。不强制 RAW 首次直接 Full，不重写 EGL 线程模型。启动期原生异常独立调查，未确认根因不得宣称修复。

## 实施与验收

| 工作项 | 实施 | 当前结论 |
|---|---|---|
| 色彩准备与提交 | 实际表面色域捕获后单路后台准备，源实例仍作纹理缓存键；切图、Surface 重建、取消及释放作废旧结果；转换按行带取消，源/目标租约覆盖完整工作 | `perf` 同步/后台对照、GPU、旧请求、重建与租约通过；常规冷 GL 上传仍在 UI 线程 |
| 绘制合帧 | 手势、动画、布局及补帧通过下一动画帧合并，视口/Full 需求仍同步上报；只计真实交换 | 同轮 20 次更新合成 1 次交换；input/zoom 功能回归通过，但真实会话偶发心跳长尾未关闭 |
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
4. Debug 两次色彩专项启动在首图后 exit 255；其中 pid 14003 由系统 exit-info 确认为 EXIT_SELF，前有并发 `System.Xml.XDocument.dll` CIL 映像失败日志。APK 为 archive-DSO 而非 assembly store，本地 payload 完整；相同加载提示也出现在通过轮次，不能将其直接认定为退出原因。Release 前一轮 12 次冷启动均存活，但不替代 Debug 对照。
5. 下一步先给实际会话 doFrame 长尾补 Draw/Swap 与 UI 工作分段，并对 Debug bundle-open 记录加载路径及设备 payload 完整性；未具备证据前不更换 EGL 架构、运行时或库版本。

## 收尾约束

本轮优化按用户授权提交 Git，不推送远端。已清理独立验收应用、`/data/local/tmp/pv-perf2-*` 素材和本轮 `bin/perf2`，保留标准构建产物及验收证据。4GB/6GB 真机、非小米 ROM、高刷全帧预算及上述启动/长尾边界仍未验或未关闭，不把本轮写成全量性能与稳定性完成。
