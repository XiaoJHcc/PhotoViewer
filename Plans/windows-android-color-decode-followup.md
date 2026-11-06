# Windows / Android 色彩与解码明确项收尾

## 范围与边界

本轮按里程碑实现并分别提交，每项必须在实际交付代码上通过构建、相关工具和可用真机检查后才提交。保留现有代理精度、方向、色彩与租约契约，不以降画质或延迟 Full 升级换速度。提交主模型落款使用 `GPT-6.1 Sol`。

## 里程碑

| 项 | 实施内容 | 验收 | 状态 |
|---|---|---|---|
| 安卓 HEIF 回落色彩 | Windows 已验证的 libheif 色彩转换提取为可选共享源；安卓最终回落保留 ICC/nclx 与高位深语义，按目标输出；PQ/HLG 明确拒绝；统一显示朝向契约 | Windows 色彩专项；ColorTestTool；HEIF e2e/smoke；Android Debug/Release 构建；真机 color（强制回落与拒绝）及主流程 | 完成 |
| HEIF 文件读取 | 去掉常见可寻址流 `MemoryStream + ToArray` 的重复整文件分配；传递取消；主图回落复用已读字节，保持非寻址兼容 | 扩充现有工具的读取边界检查；HEIF e2e/smoke；真机色彩与实际 HIF 解码；同口径探针对照 | 完成 |
| 安卓顶点缓冲 | 复用 renderer 的 direct 顶点缓冲和视图；消除每帧托管数组与 direct 分配，生命周期随 renderer 释放 | Android 构建；真机 color/zoom/input，覆盖表面重建、方向、纹理缓存和真实 Full 升级 | 待实施 |

## 下一步验证项（本轮不贸然实施）

- Windows RAW：代理先全开发再缩小、Full 未命中时再次开发；先比较 Full 直接复用与保留开发结果的内存、首屏和缩放延迟，再决定缓存策略。
- Windows 冷帧合成长尾：本地直连显示与远程/虚拟显示分开复测，不把呈现等待当解码耗时。
- 安卓绘制合帧、独立渲染/上传线程与共享 EGL 上下文：需单独设计线程、表面生命周期与背压，先保留同步提交契约。
- JPEG 多次 SAF 打开、HEIF 按 extent 随机读取、硬件 HEVC：需先补分段计时与格式覆盖，避免扩大支持面或改变解码画质。
- P3 目标落到 sRGB 表面时的全图转换计时与后台化，先明确发生频率与帧时。
- 真实 HDR/ACM 开关、跨显示器与校准精度，以及非小米 ROM 的 P3 表面矩阵。

## 环境与基线

- 主机 Windows，.NET SDK 10.0.201，Android workload 36.1.53；已连接 arm64 小米 13（2211133C）。
- 工作区起始干净，基线提交 `c550f62`。
- 本轮前审查已通过 Windows 色彩 16 项（另 1 项硬件边界）、共享色彩 68 项及 HEIF e2e/smoke。
- Windows 同机 33MP 探针中位：HIF Full 240.1ms、5120 代理 274.9ms；RAW Full 1575.6ms、5120 代理 2007.0ms。探针每类 6 张，首张预热，不作为切图 p95。

## 验收记录

### HEIF 回落色彩

- 共享 `PhotoViewer.Heif` 仅链接 Windows/Android/验证工具，无新增 NuGet 依赖，Apple 不受影响。修复 RGB24 行距被当图宽、高位深中间图被当 BGRA32 返回，以及取消/缩放异常路径的产物租约释放。
- 真机新增强制回落对照发现，旧 `BitmapFactory` 段在 sRGB 与 P3 ICC HEIF 的灰阶上都可偏 18 级；不放宽容差，安卓 HEIF 展示回落统一到共享 libheif，兼容入口也不再调用系统 HEIF 解码。共享引擎不能解释的编码/色彩如实失败；系统专属 AVIF 等支持面仍需另备素材验证，不能宣称全格式等价。
- Windows/Android 的 `IHeifDecoder` 统一返回已转正 sRGB8 并声明 `OutputPreRotated=true`；真实入口覆盖 irot=1/2/3，`ThumbnailService` 的 180° 色卡检查通过，不再双转。
- Windows Release 构建 0 警告/0 错误；色彩 16 PASS + 1 硬件边界，原生 smoke 35 PASS。共享色彩 68 PASS；Core 契约 914 PASS / 1 平台 SKIP / 0 FAIL；HEIF e2e/smoke 通过，含真实 ICC/nclx、强制高位深三出口、12 位暗平面、租约和取消。
- Android 标准 Release 与独立包名验收包均构建成功；删除旧系统段后剩余警告为既有内存探针平台版本与原生库重复项。小米 13/API 33 的独立 Release AOT 包色彩 105/105、主流程 22/22；Debug 轮次还覆盖缩放 30/30、输入 27/27。正式应用未覆盖安装，用户数据未动。


### HEIF 文件读取

- 稳定长度的可寻址流直接填充最终数组；非寻址流、部分读取、长度增长/缩短保留真实字节，超出数组可容纳范围明确报错；打开与读取传递取消，异常与取消均释放流。
- Windows 主图与 Android 主图/展示缩略图在快速路径回落时复用本次字节，不重新打开文件。Android 真机 PQ/HLG 的完整入口回落检查均 `opens=1`，色彩检查增至 107/107。
- `HeifFastDecodeTest read` 全通过；8MiB 文件读取实测分配 8,388,808B（有效载荷 8,388,608B），稳定长度路径只剩一份文件级数组，而非原来的两份。HEIF e2e/smoke、Windows Release、Android Release AOT 验收包通过。
- Windows 同机同目录/同参数探针：HIF Full 184.4ms、5120 代理 214.9ms、列表缩略图 7.2ms。相对审查轮次 240.1/274.9/14.2ms 有下降，但 CPU/驱动与系统负载存在跑间波动，不把这组单轮差异全归因于读取优化；确定收益是少一份文件数组与回落零重复打开。
