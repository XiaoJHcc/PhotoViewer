# Android 端性能与内存专项 方案 1

> 范围：Android 原生壳。主压力：HEIF 连续切换、最近两张对比切换、解码位图内存管理；其次直方图、增强等辅助计算；收掉 RAW 主图（现状：ARW 主图打不开）；设置页内存项按新口径精简。
> 红线：任何瞬间不显示低于精度要求的产物（代理长边 = 视口物理长边 × 2，100% 及以上用 Full）；只在不可见处省性能。唯一破例：STATE-01 已登记的「RAW 初次解码低分辨率顶替」。
> 共享层已由 [Apple 专项](apple-performance-memory-plan-1.md)、[Windows 专项](windows-performance-memory-plan-1.md) 落地的预取/淘汰/缓冲池/直方图查表/增强复用/ColorEngine 查表快通道，Android 自动继承，只验收不重做。

## 1. 现状（代码核对）

| 项 | 现状 |
|---|---|
| HEIF 主图 | `AndroidPresentationDecoder`：BitmapFactory 先试（Sony 4:2:2 10 位失败）→ libheif 单线程整幅 RGB24 → Skia 精缩 → `ColorEngine` 再转一遍 |
| RAW 主图 | 展示解码器不按扩展名分派，BitmapFactory 解不了 ARW 主图 → 「无法打开该图片」；`RawLoader` 未注入 |
| 内存预算 | 预算 = 系统总内存、footprint 恒 0 → 缓存只按用户上限；Android 14+ 前台收不到 trim 回调 |
| 主图上传 | UI 线程每次切图 `glTexImage2D` + 分块上传 + mipmap，只有当前一张纹理 |
| 预取 | `PrefetchFullNeighbors=false`（早期 13 张夹具 3+3 Full 预取 SIGSEGV 的规避） |

## 2. 决策

1. **HEIF 走共享快速路径**：Windows `Core/Heif` 四个托管文件迁入共享 `PhotoViewer/Core/Image/Heif`（无 x86 依赖，只用 `Vector128`），新增 `Rgba8`（sRGB / Display P3）输出；NDK 构建 `libpvhevc.so`（同一份 pvhevc 封装 + 仅 HEVC 的 LGPL FFmpeg，aarch64 NEON）。并发与空闲上下文按移动端收敛。超出支持面回落现有 BitmapFactory / libheif。
2. **RAW 走 LibRaw**（LGPL-2.1，NDK 构建 `libpvraw.so` 薄封装，直接写 RGBA8），全分辨率、方向交渲染层；初次解码按 STATE-01 登记的低分辨率顶替先上内嵌预览（共享会话的可选能力，只有实现了预览提供接口的平台启用）。
3. **内存预算实测**：footprint = `/proc/self/status` 的 `RssAnon + VmSwap` + GL 纹理记账；预算按 AOSP MemoryLimiter 可见档（按 `MemTotal` 分级），可用量再受系统 `availMem − threshold` 约束；真机跑一次上限探针确认可拿到的内存。Android 改自动缓存口径（`AutomaticCacheLimit`），设置页隐藏缓存上限两项。
4. **主图纹理缓存**：保留当前 + 最近 2 张纹理（按展示产物实例身份），A/B 不重传。
5. **设置提示**按新口径（第一行实测预算、第二行适屏张数）。

## 3. 工作流与验收

| # | 工作流 | 验收 |
|---|---|---|
| W0 | 探针与压测入口 | `pv-perf-probe` / `pv-stress`（顺序 + ab）Intent 参数可跑，报告落 `files/PhotoViewer/`；记录基线 |
| W1 | HEIF 快速路径 | Rgba8 与 Bgra32 逐字节一致（通道交换）；P3 输出与 `ColorEngine.ToDisplayP3` 平均差 ≤ 1/255；Windows 类型检查通过；Android HIF 代理/Full 端到端较基线 ≥ 3× |
| W2 | RAW | ARW 主图全分辨率、方向正确；顶替图期间细节预览/绿框不取它；代理 ≥ 目标长边 |
| W3 | 内存预算 | `memory-events` 显示实测预算与 footprint；500 连翻零超时零闪退；真机上限探针结论入文档 |
| W4 | 纹理缓存 | A/B p95 ≤ 80 ms |
| W5 | 预取 | 连翻 p50 较基线下降；放大态邻居 Full 预取稳定 |
| W6 | 设置 | 设计文档先行；Android 隐藏缓存上限两项；提示按新口径 |
| W7 | 回归 | `pv-check` 五模式 PASS；构建告警不新增 |

## 4. 进度与结论

### 基线（arm64 API 35 模拟器，4 核 / 2.5 GB，Release；`pv-perf-probe` 代理目标 T=4800，主图实际代理 2496）

| 项（中位数 ms） | HIF（A6700 26MP） | ARW | JPG（33MP） |
|---|---|---|---|
| 代理 T / Full | 922 / 692 | 29 / 27（只有 1616×1080 内嵌预览，不满足精度） | 318 / 343 |
| 缩略图 120 | 2.4 | 1.7 | 4.2 |
| 直方图 代理 / Full | 15.5 / 24.4 | — | 16.5 / 36.9 |
| 增强（代理） | 66 | — | 106 |

压测 60 张 HIF：顺序 p50 / p95 / max = 1210 / 2257 / 2846 ms，零超时；A/B p50 / p95 = 64 / 107 ms，每次切换都重传纹理（`up_hits=0`）。

### 原生库

- `libpvhevc.so`（1.3 MB，FFmpeg 8.1.3 仅 HEVC，aarch64 NEON，16 KB 页对齐）：模拟器上 10 位 4:2:2 与 8 位 4:2:0 解码 Y 平面与 Mac FFmpeg 逐字节一致。
- `libpvraw.so`（1.8 MB，LibRaw 0.22.2 + OpenMP 静态链接）：A7C II 7008×4672 / A6700 6192×4128，按 ARW `DefaultCrop` 裁到相机取景；默认 AHD 端到端约 0.7 / 0.53 s，峰值 RSS 约 0.5 / 0.4 GB；与 LibRaw 自身内存图输出逐字节一致。
- HEIF 快速解码已迁入共享 `PhotoViewer/Core/Image/Heif/`，新增 Rgba8 sRGB/P3 输出；Bgra32/RgbaF16 与迁移前逐字节一致（Windows 行为不变）。

### 工作流状态（同一模拟器；数字受并行构建干扰，真机待复测）

| # | 状态 | 结论 |
|---|---|---|
| W0 | 完成 | `pv-perf-probe`（`--ei pv-perf-probe-target` 可指定代理长边）与 `pv-stress`（顺序 / ab）可跑；压测用隔离设置档，素材目录零写入。 |
| W1 | 部分（≥3× 未达成） | Rgba8+sRGB 与 Bgra32 逐字节一致（通道交换），Rgba8+P3 与 skcms 平均差 0.14–0.27 级；迁移前后 Bgra32/RgbaF16 96/96 逐字节一致，Windows 类型检查 0 错误。HIF 代理 922 → 416 ms、Full 692 → 323 ms（模拟器约 2.2×；真机 17 Ultra 26MP 代理 458 ms，基线未在真机测，3× 未核验）；连翻零回落；方向与卡片一致，色差 < 0.2/255。胶片栏 HIF 改解容器 `thmb` 项，删去 512 代理缓存。 |
| W2 | 完成（模拟器） | ARW 主图 LibRaw 全分辨率（6192×4128 / 7008×4672），竖拍方向正确，100% 为全分辨率细节；色调沿用 LibRaw 白点规则、提亮上限 2.5×，与 macOS ImageIO 渲染 p50 差 −8…+2 级；代理在半尺寸长边够用时走 half_size（T=2496 代理 424 ms，Full 603 ms）。顶替预览 SetFile 后 78–112 ms 上屏，真图 0.6–1.3 s 后替换；细节预览以 `IsProxy` 为闸不取顶替图。后台 RAW 串行、按工作内存准入，被拒即跳过。30 张 ARW 顺序 p50/p95 690/745 ms、ab 61/84 ms，零超时零崩溃。 |
| W3 | 完成 | 模拟器预算 1112 MB（低于最低档 45%）；真机见下节：17 Ultra 受 Sentinel 红线约束取 5529 MB、小米 13 取 8192 MB，两台 500 连翻零超时零闪退、全程 Normal。 |
| W4 | 完成 | 当前 + 最近 2 帧纹理按实例弱引用缓存，另留 1 张同尺寸空纹理；mip 链计入 footprint 并汇入预算源。A/B 60 翻 `up_hits=58`，剔除冷帧 p95 48–67 ms；Caution 修剪非当前纹理（198 → 45 MB）。 |
| W5 | 完成 | HIF 顺序 60 翻 p50/p95 1210/2257 → 446–544/611–741 ms；`PrefetchFullNeighbors` 恢复为 true，`pv-check analysis`/`main` 多轮无崩溃。 |
| W6 | 完成 | `SETTINGS-06@android` 入 `Design/platforms.md`；文件页隐藏两项缓存上限，提示「设备性能: CPU 4 核 / 内存 1112 MB」「✓ 可缓存约 28 张适屏照片（预载与对比需 6 张）」。 |
| W7 | 完成 | Android Release 0 错误、告警不新增；`pv-check` 五模式 PASS；Mac Debug 0 错误，Windows 类型检查 0 错误。 |

### 真机验收（Release AOT，可调试 + 正式签名）

| | 小米 17 Ultra | 小米 13 |
|---|---|---|
| 系统 / SoC / 内存 | Android 16 HyperOS 3 / SM8850 / 15 GB | Android 13 MIUI 14 / SM8550 / 11 GB |
| 预算（启动快照） | 5529 MB（AOSP 档 10240，Sentinel 红线 6144 × 90%） | 8192 MB（无 Sentinel 红线属性） |
| HIF 代理 / Full（真实视口代理，中位 ms） | 458 / 375（26MP）、624 / 523（33MP） | 651 / 502（26MP） |
| ARW 代理 / Full | 746 / 893（26MP）、887 / 1179（33MP） | 967 / 1354（26MP） |
| HIF 顺序 60 p50 / p95 | 404–534 / 599–910 | 470 / 705 |
| HIF A/B 60 p50 / p95 | 16 / 38（`up_hits=58`） | 16 / 37（`up_hits=58`） |
| RAW 顺序 30 / A/B 30 p50 / p95 | 838 / 1224；16 / 43 | 753 / 1140；16 / 921（冷帧，`up_hits=27`） |
| HIF 500 连翻 p50 / p95 | 591 / 805，零超时，全程 Normal，swap 66 MB | 618 / 797，零超时，全程 Normal |
| `pv-check` 五模式 | 全 PASS | 全 PASS（修复设置线程问题后） |

- 方向、顶替预览先上屏再替换、100% 全分辨率细节均截图核对；设置文件页隐藏缓存上限两项，小米 13 提示行「设备性能: CPU 8 核 / 内存 8192 MB」「✓ 可缓存约 254 张适屏照片（预载与对比需 17 张）」。
- 零 SIGSEGV / OOM / lmkd 查杀。小米 13 连续压测后一度 Thermal SEVERE，探针中位数慢约 12%。
- **内存上限**：17 Ultra 前台逐步分配到 PSS 14.8 GB 后被 HyperOS `MiuiSentinelMemoryManager` 以 `used too many pss resource … threshold:6291456kb` 强杀（越线约 60 s 后执行，非 lmkd），阈值即系统属性 `persist.sys.stability.pss_highest_line`。预算改为 `min(AOSP 可见档, 红线 × 90%)` 后，同一 500 连翻的 swap 从 2.5 GB 降到 66 MB、托管堆峰值从约 4.9 GB 降到约 2.6 GB；后半程 p50 上升来自素材后 300 张为 33MP（前 100 张 26MP），与内存无关。
- **设置读取跨线程**：真机上 `LoadSettingsAsync` 在后台线程回填设置，原生控件订阅者抛 `CalledFromWrongThreadException`，被报成「读取设置失败」，部分回填后的节流保存曾把小米 13 的快捷键从 16 项截断为 1 项（控制栏命令分组随之消失）。回填改为切回 UI 线程，两台真机复测无报错、设置完整。
- **P3 表面**：小米 13 上 `pv-p3` 诊断模式照片区亮度 +72%（HIF）/ +114%（ARW），与 17 Ultra 的 #29 一致，默认 sRGB 维持不变。

### 遗留候选

- RAW 适应态先解代理、放大再解一次 Full（共两次开发）；可考虑 RAW 代理直接取全图，需改缓存口径。
- Windows 细节预览未以 `IsProxy` 为闸（`WindowsAnalysisPanel.cs`），若 Windows 启用顶替须先补。
