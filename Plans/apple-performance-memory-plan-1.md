# Apple 端性能与内存专项（Mac / iPad）方案 1

> 范围：macOS 与 iPadOS 原生壳。主压力：HEIF 连续切换、最近两张对比切换、解码位图的内存管理；其次直方图、增强等辅助计算；并收掉 iPad RAW 全分辨率（`SCOPE-04@ios`）。
> 红线：任何瞬间不显示低于精度要求的产物（代理长边 = 视口长边物理像素 × 2，100% 及以上用全分辨率）；只在不可见处省性能。编码保真度不降（iPad 代理仍 F16，Full 仍 8 位 Display P3，Mac 恒 F16）。

## 1. 实测基线（2026-10-21，`--pv-perf-probe`）

真机 iPad mini 7（A17 Pro，iPadOS 26，Release `ios-arm64`，解释器模式）与 Mac（Mac17,3，Debug）。T = 4532（iPad mini 横屏长边 2266 × 2）。

| 项（中位数 ms） | iPad HEIC 7008×4672 10bit | iPad JPG 6000×4000 | iPad ARW | Mac HEIC | Mac ARW |
|---|---|---|---|---|---|
| 代理缩解码 `thumbAlways@T` | 181 | 156 | 9（内嵌 1616×1080） | 58 | 805 |
| 代理画进 F16 / 8 位 P3 | 110 / 28 | 30 / 7 | — | 2 / 1 | 2 / 1 |
| Full 解码 + 画进 8 位 P3 | 129 + 185 | 42 + 22 | — | 73 + 3 | 2 + 704（惰性） |
| `CIRawFilter` 全幅 / 缩到 T（8 位） | — | — | 481（冷 4013）/ 282 | — | 210 / 133 |
| Metal 上传 + mipmap（代理） | 19 | 19 | — | 13 | 19 |
| 直方图 F16 代理 / 8 位 Full | **5668 / 1252** | 3415 / 915 | — | 1037 / 102 | 1050 / 103 |
| 增强 CLHE F16 代理 / 8 位 Full | **760 / 1781** | 722 / 1329 | — | 80 / 187 | 80 / 190 |

- **内存上限**：`com.apple.developer.kernel.increased-memory-limit` 在免费 Apple ID 下可签进 Profile（Xcode 自动预配已验证），iPad mini 7 的 `phys_footprint + os_proc_available_memory` 由 **3376 MB → 6144 MB**。footprint 含 GPU 纹理与 IOSurface（WWDC22 10106），故该和即 jetsam 上限，可直接作预算。
- **解释器**：Release 仍 `UseInterpreter=true`（全部程序集解释），托管热循环（直方图/增强/CV/EXIF）比 Mac 慢约 10×；分析栏可见时一次切图触发 3 次整幅直方图（切图清空、代理、Full），未取消，旧任务继续占 CPU——是"分析栏可见 p95 2660ms"的主因。
- **缓冲池**：2 幂分档使 33MP 8 位（131 MB）落 256 MB 档、24MP 8 位落 128 MB 档；每档最多留 4 个空闲缓冲且只在 Critical 清空，空闲缓冲计入 footprint。
- **预取**：`WaitForHighPriorityIdleAsync` 在 P0 在途时让位，连续翻页时 P0 几乎一直在途 → 下一张来不及预取，连翻基本全是 miss（压测 p50 ≈ 单张解码）。±1 邻居在适应态也取 Full（iPad 33MP 一张 314 ms + 256 MB 档）。
- **RAW**：Mac `thumbAlways` 走 ImageIO 全量渲染再缩（805 ms），`CIRawFilter.ScaleFactor` 缩渲染 133 ms；iPad ImageIO 只给内嵌预览，`CIRawFilter` 可出 7008×4672。

## 2. 决策

1. **iPad 内存上限 entitlement**：`PhotoViewer.iOS/Entitlements.plist` 与 `Tools/RefreshIosProfile` 同步声明 `increased-memory-limit`；预算以运行时实测为准（best-effort，未生效时同一逻辑自动按小预算运行）。
2. **iOS 热路径 AOT**：保留解释器作反射回退，`MtouchInterpreter` 只解释 ReactiveUI/DI 类反射程序集，共享 `PhotoViewer`、头工程与 CoreLib 等 AOT。
3. **缓存容量由实测预算推导**（iPad 自动、不再暴露缓存内存/数量设置；Mac 保留用户上限但再受推导上限约束）：
   `缓存上限 = 预算 × (1 − Caution 退出阈值) − 非缓存占用`，非缓存占用 = 实测 footprint − (缓存 + 池空闲)。缓存满时 P0 按淘汰序腾位，预取（P2）**不得挤掉保护集**，腾不出位置即跳过。
4. **缓冲池**：每八度 8 个子档（浪费 ≤ 12.5%），空闲池按字节封顶并在 Caution 起清空 + 后台 GC，空闲字节计入缓存账。
5. **预取跟随实际需求档**：适应态只预取代理；放大态/分析栏可见时 ±1 取 Full。按最近翻页方向排序，方向上的下一张以 P1 与 P0 并发；当前图停留 ≥ 600 ms 且 Normal 时后台预解 Full（不上屏，只入缓存），放大时立即命中。
6. **最近对比**：淘汰序 = 非保护 LRU → 保护窗内按距离由远到近 → 最近浏览（旧→新），最近 2 张最后淘汰；增强图与直方图按源产物各留最近 2 份，对比切换不重算。
7. **RAW 走 `CIRawFilter`**（Mac/iPad 共用 `PhotoViewer.Apple`）：代理 `ScaleFactor = 目标/原生长边`、Full `ScaleFactor = 1`，几何取 `NativeSize`；创建/渲染失败才回退 ImageIO（iPad 即内嵌预览，`SCOPE-04@ios` 降为回退说明）。Mac 切换前须与 ImageIO 全幅输出做色彩对拍。
8. **直方图精确提速**：F16 用 65536 项 Half→sRGB 级查表（与逐像素公式逐值相同），8 位分行并行后整数合并；同一源只算一次、过期即取消。
9. **设置**：iPad 隐藏「缓存内存上限 / 缓存数量上限」；预算提示第一行显示实测上限，第二行改为"可容纳多少张适屏预览 / 原图"并按是否容得下预载窗口 + 对比 2 张定级，旧"调小限值"说明删除。先改 `Design/spec/settings.md` 与 `Design/platforms.md`，再落四端共享 VM 与 iOS 设置页。

## 3. 工作流与验收

| # | 工作流 | 验收 |
|---|---|---|
| W1 | iOS AOT | 真机 Release 启动、`--pv-native-photo-check` PASS；探针直方图/CLHE 中位数较基线 ≥ 5× 提速 |
| W2 | 缓冲池 + 直方图 | 直方图输出与旧实现逐字节一致（F16/8 位多素材）；池档浪费 ≤ 12.5%；Mac analysis 套件 PASS |
| W3 | RAW `CIRawFilter` | Mac 全幅色彩与 ImageIO 平均差 ≤ 1/255、无方向错误（含改写 EXIF 方向的副本）；iPad ARW 出 7008×4672 Full、代理 ≥ 目标长边；双端 photo/gesture 套件 PASS |
| W4 | 预算/缓存/预取/对比 | 真机 500 连翻零超时零闪退；连翻 p50 较基线下降；A/B 对比切换 p95 ≤ 80 ms；`memory-events` 显示上限 ≈ 6 GB 且全程 Normal |
| W5 | 设置 | 设计文档先行；iPad 设置页无两项缓存上限；提示文案按新公式；Mac/iOS 设置相关套件 PASS |
| W6 | 回归 | Mac 八套 `--pv-native-*`、iOS 七套 `--pv-native-*-check` 全 PASS；构建告警不新增 |

## 4. 进度与结论

全部工作流已落地并验收。

| # | 结论 |
|---|---|
| W1 | Release `MtouchInterpreter=-all`（全部 AOT，解释器只作反射回退）。真机：CLHE 33MP 1781→191 ms、8 位直方图 1252→105 ms、F16 代理 CLHE 760→80 ms；500 连翻零超时零闪退 |
| W2 | 池每八度 8 子档（33MP 8 位 256→128 MB 档）、空闲按字节封顶、Caution 起清空；直方图 F16 查表 + 分区并行，与旧实现逐字节一致，Mac F16 812→40 ms；分析栏按源实例去重、取消过期、留最近 2 份 |
| W3 | `CIRawFilter` 全分辨率（Mac/iPad），Mac 对拍 Full 8 位平均差 0.063、F16 0.043、方向 6/8 正确；iPad ARW 出 7008×4672。冷渲染约 500 ms 为原生尺寸解马赛克固定成本，所有不降精度的选项（缓存中间结果、优先级、软件渲染、方向）均无提速，草稿模式禁用 |
| W4 | 预算实测（iPad 6144 MB）、缓存按预算推导、保留排名淘汰、预取不挤更重要条目、适应态邻居只取代理 + 当前图 600 ms 后预热 Full、F16 Full 跨档复用、增强/直方图对比复用。连翻不命中的三处根因：驱动器按枚举序翻页；方向邻居排在缩略图后面开跑晚半轮；翻到时取消快完成的预取。修法：驱动器按展示列表翻、沿 P0 并行独立通道（2 路，HEVC 实测 2 路吞吐 ≈2×）、方向前两张一起预取（RAW 只 +1，GPU 串行）、已开跑的不随轮次取消 |
| W5 | iPad 隐藏缓存上限两项；提示第一行为实测预算、第二行为"可缓存约 N 张适屏照片（预载与对比需 R 张）"；删去"调小限值"说明 |
| W6 | Mac 十套 + RAW 对拍、iOS 模拟器七套 + faults 全 PASS；构建 0 错误，告警不新增 |

真机 500 连翻（iPad mini 7，450 HEIC + 50 ARW，最终构建）：

| | HEIC p50 / p95 | ARW p50 / p95 | 总耗时 | 压力 / gc 峰值 |
|---|---|---|---|---|
| 基线 | 364 / 382 ms | 80 / 86 ms（内嵌预览 1616×1080，不满足代理精度） | 167 s | Normal / 1.2 GB |
| 最终 | **182 / 248 ms** | 781 / 798 ms（7008×4672） | **122 s** | Normal / 3.3 GB，零超时，代理 500/500 F16 |

HEIC 稳态 ≈ 单张代理解码 290 ms / 2 路 + 显示 65 ms，已到当前红线下的上限。

## 5. 候选（未做，需决策）

- 快速连翻时把胶片栏缩略图延后约 200 ms：一轮 500 连翻中缩略图解码约 142 s 线程工作量，与主图争 CPU 与 HEVC 解码器，预计 HEIC 再降 10–15%；属胶片栏可见行为，需设计裁定。
- RAW 滤镜实例复用（同图重复解码 273 vs 681 ms，用于升 Full 与 A/B 回切）：需先测常驻实例的 footprint。
