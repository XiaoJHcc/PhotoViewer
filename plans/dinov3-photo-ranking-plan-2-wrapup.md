# DINOv3 照片美学评分 — 二期增补收尾

> 状态:草案 v3.0 / 2026-05-13
> 上游:[dinov3-photo-ranking-plan-1.md](dinov3-photo-ranking-plan-1.md) · [dinov3-photo-ranking-plan-2.md](dinov3-photo-ranking-plan-2.md)
>
> **本文件的任务是收尾一件事**:把 Plan-2 二期里实测不及格的 CV 诊断算法从 v0 升级到 v3(中心采样 + 边宽量化 + 32 网格 + 对数映射),14 张代表样本的 CV 部分过审后进入阶段 III。
>
> 本文件只对应 Plan-2 **T3(CV 诊断页)** 这一个未结条目。Plan-1 / Plan-2 的其他已落地、已废弃或已被其他产物替代的事项不再在本文件重复审视,统一视作既有条件(见 §0.2)。
>
> **v3.0 修订摘要(2026-05-13)**:实测 v2 在 DINO 诊断页有三个问题 ——
> 1. **锐度分层不够明显**:线性映射在 1-3 px 段被压缩,3 px 已经显示半亮 → 改对数映射,着重强调 1 px 数量级
> 2. **CV 网格 16 与 DINO 32 不对齐**:十字准星 / cosine 参考点 / 锐度坐标三方对应不上 → 网格 16→32
> 3. **ISO 100 纯色天空没被标为最低锐度**:`τ_edge` 自适应 p90 会把任意干净区域拔出"假边",得到错误的短边宽 → 加绝对边强下限,失败块从 NaN 中灰改为虚焦深色(0)
>
> v2 方案(线性 / 16 / 中灰 NaN)整体进墓碑(§4)。

---

## 0. 定位

### 0.1 本次收尾只做什么

一句话:把 [CvGridExtractor](../PhotoViewer/Core/AI/CvGridExtractor.cs) / [CvHeatmap](../PhotoViewer/Core/AI/CvHeatmap.cs) / DINO 诊断页的 CV 三图从 v0 升级到 v2,让 §3 验收通过。

为什么:Plan-2 §5.2 CV 肉眼验收实测三张图里**失焦过于宽容、抖动图缺乏物理意义、金字塔一致性冗余**,继续按骨架往前推会把不达标的 CV 基建带进阶段 IV 训练,触发 Plan-1 §0.5 陷阱 4(把热力图当客观标注)。

**用户严格度(2026-05-13 明确)**:虚焦与抖动按**像素级**量化 —— 普通场景下模糊或位移超过 10 px 即为严重问题,故意虚化背景可超过 100 px,需要保证 **1 px 尺度**的可分辨度。v1 的全格均值 / L0-L1 比 / 结构张量都做不到像素级量化,因此本次改走 v2。

### 0.2 既有条件(**不在本文件讨论范围**)

下列事项已在二期里结束,阶段 III 开工**不依赖它们重验**,本文件不再登记进度:

- **ONNX 打包 + 四平台自检**:Windows / macOS / iOS 已实测跑通 `EnsureDualOutputSchema` + 单张耗时达标。Android 允许延后(阶段 V 端侧部署时随整机重打包一并做)。
- **相似聚类质量(Plan-2 §5.1)**:14 张样本上的同山头 Top10 / 连拍 ≥ 0.95 / 孤立抓拍低分等条目在日常使用中已达到预期效果。
- **DINO 诊断(Plan-2 §5.3)**:PCA-RGB 主体/背景分组、参考点 cosine 语义分块已实测准确。
- **A1 Python 推理验证**:归档为"跳过,不补做"。A2-M2 的真用户相似聚类与 DINO 诊断 PCA-RGB 实质替代了原 t-SNE 的决策价值。
- **A3 设计侧 notebook PoC**(`cv_grid_design.ipynb` / `patch_token_design.ipynb`):归档为"不做"。边宽路线直接落地,PatchHeatmap 已替代 patch notebook。
- **Plan-2 §3.3.2 交互参数暴露**(归一化下拉 / τ 滑条 / spinner):归档为"永久不做"。v2 下 τ 参数天然失效,归一化改成固定像素量纲(见 §1.3),spinner 已由 `IsBusy` 驱动。

### 0.3 本文件不改动什么

- **DINOv3 侧所有代码**:ONNX 模型、[DinoFeatureExtractor](../PhotoViewer/Core/AI/DinoFeatureExtractor.cs) / [DinoFeatureCache](../PhotoViewer/Core/AI/DinoFeatureCache.cs) / [FolderFeatureIndexer](../PhotoViewer/Core/AI/FolderFeatureIndexer.cs) / [SimilarityService](../PhotoViewer/Core/AI/SimilarityService.cs) / [PatchHeatmap](../PhotoViewer/Core/AI/PatchHeatmap.cs),一行不动
- **数据库 schema**:`photos` 表维持单列过渡形态(`feature_vector` + `feature_model`)。`photo_features` / `photo_patches` 纵表 ALTER 放到阶段 III 开工日执行
- **CV 结果持久化**:CV 一期本来就不入库,换算法 = 换 `CvGridResult.CurrentVersion` 字符串,零迁移零回滚

---

## 1. CV 诊断算法 v0 → v3

### 1.1 v0 / v1 / v2 失效史

| 路线 | 思路 | 失效点 |
|---|---|---|
| v0(已落地) | `lap × sobel` 失焦 / 低熵掩膜抖动 / 金字塔 CV | 只能分"物体 vs 天空" |
| v1(草案 v1.1,否决) | 结构张量 (θ,α,m) + L0/L1 sobel 比 | 全格扫的稀释效应,无法分 1/10/100 px |
| v2(草案 v2.0,否决) | 中心 128 块 + Marziliano + 线性映射 + 16 网格 + NaN 中灰 | (a) 线性映射 1-3 px 压成半亮 (b) 16 网格与 DINO 32 错位 (c) 自适应 τ_edge 会让 ISO 100 干净天空挤出"假边",反而显示为锐(NaN 中灰也让用户难以一眼判定) |

### 1.2 v3 路线:中心采样 + 边宽量化 + 32 网格 + 对数映射

思路:**放弃让每格代表全格**,只在每格中心取 128×128 固定块,在块内用 Marziliano 边宽算法给出"这些边里最锐的 20% 平均几 px 宽"。采样本身的代表性误差用"边数不足或边强度过低 → NaN → 锐度图判为虚焦色(0)"接住 —— 干净纯色块在用户视角下本就没有"锐"可言,语义对齐。

**粒度**(v3 改动):
- 网格 **32×32**,与 DINO patch 网格对齐 → 十字准星、cosine 参考点、锐度坐标同一坐标系
- 每格中心采样块尺寸 P **自适应**:`P = clamp(短边 / 32, 64, 192)`
  - 6000×4000 短边 4000 → P 锁到 128(算力上限)
  - 4000×3000 短边 3000 → P ≈ 93
  - 2400×1600 短边 1600 → P = 64(下限)
  - 短边 < 64 直接全 NaN(诊断用图基本不会遇到)
- CV 输入**用原始分辨率**:走 `BitmapLoader.GetBitmapAsync`(已加 LRU 缓存,与 ImageView 复用),不下采样、不缩到固定短边
- DINO 路径不变,仍走 `ThumbnailService` 的 560 短边
- 仍不走金字塔下采样

**可测量程**:
- 边宽:1 px ~ P/3 ≈ 40 px,**对数映射**强调 1 px 数量级
- 方向位移:0 ~ P/3 ≈ 40 px

### 1.3 v3 标量集合(每格 6 个,1 层,无金字塔)

字节布局:`32×32 × 6 × 1 = 6144 float`(v2 1536)。版本号 `cv_grid_v2_centersample` → `cv_grid_v3_32grid`。

| # | 标量 | 单位 | 意义 | 计算 |
|---|---|---|---|---|
| 0 | `edge_count` | 整数 | 块内强梯度像素数(NaN 判据) | 一次扫描计数 |
| 1 | `edge_width_p20` | px | 最锐 20% 边的平均跨像素宽度 | Marziliano |
| 2 | `edge_width_median` | px | 所有强边宽度中位数 | Marziliano |
| 3 | `shake_spread` | px | 方向 8-bin 边宽 max-min | 方向分桶边宽谱 |
| 4 | `shake_direction` | rad(`[0,π)`) | 边宽最大桶的中心方向 | 同上 |
| 5 | `luma_mean` | [0,255] | 采样块亮度均值(曝光调试,保留) | 一次扫描累加 |

**v3 边强度下限(新增,关键修复)**:
- `τ_edge_floor = 30.0`(对应 luma 0-255 标度下 ~12% 局部对比度的 Sobel 幅值,经验值)
- 自适应 `τ_edge = max(块内 mag p90, τ_edge_floor)`
- 若整块 mag 的 p90 < `τ_edge_floor` 即"块内根本没有真边",`edge_count = 0` → 该格 NaN
- 这解决了"ISO 100 干净天空被挤出假边"的问题

**Marziliano 单边测宽流程**(同 v2,但 `τ_edge` 多了 floor):
1. 用 Sobel 挑幅值 `mag > τ_edge` 且是**单方向局部极大**的像素作边种子
2. 沿梯度方向正负两侧各步进,直到亮度**单调性打破**或跨过 **8 px**,记录左右平台位置
3. 边宽 = 总跨度;左右任一方向撞到块边界 → 该边丢弃

### 1.4 v3 三张诊断图

布局仍是 3 张 32×32 + 文本面板:

#### 1.4.1 `Sharpness` — 边宽对数热力图

```
edge_count < N_min OR p90 < τ_edge_floor:    map = 0   (虚焦深色,与"严重虚"同色)
else:                                         w = edge_width_p20
                                              t = (log(w_vis) − log(w)) / (log(w_vis) − log(w_sharp))
                                              map = clamp(t, 0, 1)
```

- `w_sharp = 1.5 px`(全亮)→ `w_vis = 10 px`(全暗)
- **对数映射**:1.5/2/3/5/10 px 在锐度图上的 t 值约为 1.00/0.74/0.49/0.27/0.00,1-3 px 分层比线性方案明显
- v3 弃用 v2 的"NaN 中灰",纯色块直接给 0(深蓝)。**用户视角下"没有锐内容" = "虚",语义统一**
- 调色板:viridis 不变

#### 1.4.2 `MotionBlur` — 刚体方向矢量场

同 v2,仅网格 16→32,每格画线段。绘制范围不变(spread / 10 顶到格半宽,绿色浓度同步)。

#### 1.4.3 `RigidMotion` — 全图刚体拟合数值文本

同 v2,输入样本数从 256 → 1024,但筛选条件不变。

### 1.5 参数默认值(硬编码,不开交互)

| 参数 | 默认值 | 用途 |
|---|---|---|
| `P`(采样块尺寸) | clamp(短边/Grid, 64, 192) | 每格中心采样;自适应到原始分辨率 |
| `Grid` | 32 | 网格边长(与 DINO patch 对齐) |
| CV 解码 | `BitmapLoader.GetBitmapAsync` | 原始分辨率,不下采样;共享 LRU 缓存 |
| `w_sharp` / `w_vis` | 1.5 px / 10 px | 锐度图归一化两端(对数尺度) |
| `τ_edge_floor` | 30.0 | 块内 mag p90 下限(luma 0-255 标度) |
| `τ_edge` | max(p90, τ_edge_floor) | 边种子阈值 |
| `plateau_τ` | τ_edge / 4 | 边宽步进停止 |
| `max_half_width` | 8 px | 单侧最大步进距离 |
| `N_min_edges` | 80 | 块内最小边数,不足判 NaN |
| `bucket_min_edges` | 5 | 方向桶最小边数 |
| 刚体拟合 spread 下限 | 2 px | 参与拟合的格阈值 |

### 1.6 对 DINO 诊断页的连带要求

CV 路径走 **`BitmapLoader.GetBitmapAsync`** 拿原始分辨率位图(已有 LRU 缓存,与 ImageView 共享,二次访问命中即返回)。DINO 路径仍走 `ThumbnailService` 的 560 短边,两条解耦。VM 不可 Dispose CV 大图 —— 它属于 BitmapLoader 的缓存。
格中心像素坐标 = `((x + 0.5) * W / 32, (y + 0.5) * H / 32)`,块边界按 `P = clamp(min(W,H)/32, 64, 192)` 推出,裁剪到图像内;
块短边 < 64 px → 该格 NaN(深色)。

---

## 2. 远期问题预判(出问题再做)

本期**不实现**,每条带触发条件,只在条件满足时开工。

| # | 问题 | 触发 | 预案 |
|---|---|---|---|
| R1 | 高 ISO 噪点把 Marziliano 边数撑起来,给假锐读数 | §3 验收中场景 7 ISO 3200+ 夜景 Sharpness 图在纯噪点区偏亮 | 边种子加"梯度方向稳定性"过滤(3×3 邻域 θ 方差 < 阈值)或 3×3 中值预处理 |
| R2 | 1 px 精度不够(实际锐图 p20 常在 1.5~2 px) | Sharpness 无法区分"锐 vs 极锐"时 | 边宽步进升级到亚像素(抛物线拟合 mag) |
| R3 | 自然纹理(头发 / 草 / 叶)被测成"均匀宽边"误判为虚 | 阶段 IV 训练发现误当"虚焦信号" | 由 DINO patch 语义加权兜底,不在 CV 层加规则 |
| R4 | 刚体拟合把栏杆 / 屋顶 / 白线误读成手抖 | §3 验收中白天建筑场景 |T|/|ω| 被拉高 | 提高 spread 下限(2→4 px);加"全图 direction 一致性检验"(方差过小时视为静止纹理) |
| R5 | 手抖旋转中心偏离画面中心 | 实测旋转抖中心不在画面中心 | 刚体拟合把 c 作为第三未知量(2 参数 LS → 3 参数);或 RANSAC |
| R6 | 采样块运气差(采到天空/墙)让整图 NaN 比例过高 | §3 验收中出现整张图灰片 | "多采几点取最清晰"的升级:每格在中心 ±P/4 四角额外采 4 个 P/2 小块,取边数最多那个 |
| R7 | 2560 短边解码 I/O 太慢 | 诊断页切图肉眼可感顿挫 | 独立 CV 解码通道做内存缓存;或直接使 JPEG/HEIF 内嵌 2048 级 preview |
| R8 | FFT 高频能量占比 | 废弃,见墓碑 §4 #FFT | 边宽路线已直接给 px,正交冗余度高 |
| R9 | 结构张量 (θ,α,m) | 废弃,见墓碑 §4 #structure | 稀释效应无解 |

---

## 3. 验收 checklist

规则:全部打钩才开工阶段 III。

### 3.1 代码落地

- [ ] [CvGridResult.cs](../PhotoViewer/Core/AI/CvGridResult.cs):`CurrentVersion = "cv_grid_v3_32grid"`,`GridSize = 32`,`ScalarNames` 同 v2,`DataLength = 32*32*6 = 6144`
- [ ] [CvGridExtractor.cs](../PhotoViewer/Core/AI/CvGridExtractor.cs):`τ_edge` 加 `τ_edge_floor` 下限;p90 < floor 整块判 NaN;`Parallel.For` 上限 `Grid * Grid = 1024`
- [ ] [CvHeatmap.cs](../PhotoViewer/Core/AI/CvHeatmap.cs):`BuildSharpness` 改对数映射,NaN → 0(深色)
- [ ] [DinoDebugViewModel.cs](../PhotoViewer/ViewModels/Tools/DinoDebugViewModel.cs):CV 解码改走 `BitmapLoader.GetBitmapAsync` 原始分辨率,VM 不再 Dispose CV 位图
- [ ] [ShakeFieldView.cs](../PhotoViewer/Views/Tools/ShakeFieldView.cs):格数从 16 → 32(用 `CvGridResult.GridSize`)
- [ ] [DinoDebugView.axaml](../PhotoViewer/Views/Tools/DinoDebugView.axaml):标签更新"16×16"→"32×32"
- [ ] Windows Debug 跑通,切图无崩溃、编译零 warning

### 3.2 14 张代表样本 CV 目测(Plan-1 §0.3 七类各 2 张)

- [ ] **锐度 · 1 px/3 px/10 px 分层**:人像锐焦 p20 应 ≤ 2 px(满亮),轻度失焦 p20 应在 3-6 px(中灰),严重失焦 p20 应 ≥ 10 px(暗或 NaN 灰);三档在 Sharpness 图上肉眼可分辨
- [ ] **锐度 · 纯色区 NaN**:夜景天空 / 纯墙采样块在图上为中灰,不误报为"锐"
- [ ] **锐度 · 光斑判虚**:夜景 bokeh 球内部边宽实测 ≥ 5 px,显示偏暗
- [ ] **抖动 · 矢量场方向与拖影目视一致**:夜景点光源拖影方向 `shake_direction` 与目视拖影 ±15° 一致
- [ ] **抖动 · 栏杆 / 屋顶 / 白线不误报**:白天建筑 `shake_spread` 大多 < 2 px,或 `residual_rms > 6 px`,文本标"静止纹理"而非"手抖"
- [ ] **抖动 · 手抖刚体拟合可读数**:手抖样本 `|T|` 或 `|ω|` 在 3-15 px / rad 区间,`residual_rms < |T|/2`
- [ ] **抖动 · 车流 / 反光不误报**:夜景车流矢量场杂乱,`residual_rms` 大,文本标"混乱场景"

### 3.3 阶段 III 前哨(开工日前就绪即可,不阻塞 §3.1 / §3.2)

- [ ] `Tools/migrate_features_to_longtable.py` 编写 + dry-run 通过
- [ ] `photo_features` / `photo_patches` / `photos.cv_grid` / `photos.cv_grid_spec` ALTER 脚本就绪
- [ ] 迁移后 `photos.feature_vector` / `feature_model` / `feature_computed_at` 三列保留数据但停写(代码路径切到纵表)

---

## 4. 废弃方案墓碑

永不再讨论。未来评审撞到其中一条,直接指回本节。

| 方案 | 废弃理由 |
|---|---|
| `grad_dir_entropy` v0 标量 | 只保留方向集中度 1 bit,丢方向 + 强度 |
| v0 抖动图二值掩膜(低熵 + 有边缘) | 建筑栏杆误报率高 |
| v0 金字塔一致性图 | 噪声放大 / 信号冗余 |
| v1 结构张量 (θ,α,m) + L0/L1 sobel 比 (#structure) | 全图扫 16 格稀释效应:1 px/10 px/100 px 在格均值里被折成两档,无法满足用户"像素级严格度"要求 |
| v1 金字塔 3 层方案 | 边宽是绝对 px,跨尺度冗余 |
| v2 锐度线性映射 (#linear) | 1-3 px 段被压缩成半亮,无法在视觉上拉开"略虚 vs 微虚",改对数映射 |
| v2 16 网格 | 与 DINO 32 网格无法共用十字准星 / cosine 参考点 / 锐度坐标 |
| v2 自适应 τ_edge(无下限) | 干净区(ISO 100 天空)被挤出"假边",反而显示为锐;v3 加 τ_edge_floor 兜底 |
| v2 NaN 中灰 | 用户视角"没有锐内容" = "虚",中灰反而比"严重虚"显得不那么糟,语义错位;v3 NaN → 0(深色) |
| FFT 高频能量占比(FftSharp / 手写 2D FFT) (#FFT) | Marziliano 边宽直接给 px,正交冗余 |
| DINO 热力图截 patch 喂训练 | Plan-1 §0.5 陷阱 4 |
| 独立 CNN 抖动 / 虚焦分支 | 全部并入 CV 网格,避免多分支协调成本 |
| 上下文 Transformer(v1) | 第一版 `[h_i; g_i]` 拼接 + MLP,Transformer 远期 |
| 全局 Top 12.5% 单轮筛选 | 违背锦标赛选片结构 |
| 任意两两组合训练对 | 必须有局部可比域约束 |
| 强拆"技术分 / 美学分 / 去重分" | 数据已混合不可拆 |
| 绝对 0-5 分类回归 | 跨场景同星级标签不可信 |
| 长边 2000 下采样做 CV | 双线性下采样损害高频;v3 走 BitmapLoader 原始分辨率,块尺寸自适应 |
| v3 之前的"CV 固定短边解码"(2560 / 4096) | CV 必须用原始分辨率,任何缩放都会损害 1 px 精度;v3 走 BitmapLoader |
| `photos.feature_vector` 单列作为最终 schema | 过渡形态;阶段 III 开工日迁 `photo_features` 纵表 |
| 跨模型 CLS / patch fallback(L 没算用 S 顶) | 空间不可比;所有 SQL 强制 `WHERE model_id = ?` |
| `Tools/dinov3_feature_probe.py` | A1 正式归档"跳过不补做" |
| `Tools/cv_grid_design.ipynb` / `patch_token_design.ipynb` | v2 直接落地,PatchHeatmap 已替代 |
| Plan-2 §3.3.2 交互参数暴露 | v2 下 τ 天然失效;归一化改成固定量纲(`w_sharp`/`w_vis`) |
| 老 `Core/Similarity/` 占位实现 | A2-M2 完成时已删除 |

---

## 5. Go / No-Go

**go**:§3.1 + §3.2 + §3.3 全部打钩 → 开工阶段 III(Plan-1 §B 入口)。
**no-go**:任何一条未钩 → 回 §1 或 §2 预案。

阶段 III / IV / V / VI / VII 的路径已由 Plan-1 §B/C/D/E/F 锁定,本文件不重复。
