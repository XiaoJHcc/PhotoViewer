# DINOv3 照片美学评分 — 二期增补收尾

> 状态:草案 v4.1 / 2026-05-16
> 上游:[dinov3-photo-ranking-plan-1.md](dinov3-photo-ranking-plan-1.md) · [dinov3-photo-ranking-plan-2.md](dinov3-photo-ranking-plan-2.md)
>
> **本文件的任务是收尾一件事**:把 Plan-2 二期里实测不及格的 CV 诊断算法从 v0 升级到 v4(中心采样 + Marziliano 绝对边宽 + 32 网格 + 对数锐度 + **结构张量主方向** + 对角线归一化抖动量级 + 加权刚体拟合 + 离线诊断工具),14 张代表样本的 CV 部分过审后进入阶段 III。
>
> 本文件只对应 Plan-2 **T3(CV 诊断页)** 这一个未结条目。Plan-1 / Plan-2 的其他已落地、已废弃或已被其他产物替代的事项不再在本文件重复审视,统一视作既有条件(见 §0.2)。
>
> **v4.0 修订摘要(2026-05-16 上半场)**:v3 锐度部分(对数映射 / 32 网格 / τ_edge_floor / 纯色判虚)实测过审,锐度三条目在 §3.2 已勾,**v4 锐度路径一行不动**。但抖动主任务实测三类失败:
> 1. **小光斑旋转抖被漏报(主任务漏报)**:v3 `spread = max_bucket - min_bucket` 是各向异性差值定义,城市灯阵旋转抖时各方向同步变宽,差值反而趋零,核心识别场景被切掉 → 改 `drag_width = bucket 中位绝对边宽`,绝对量级直接进颜色编码与刚体拟合
> 2. **建筑/霓虹被误读为抖动**:v3 矢量场只画线、长度限位,无法区分"5 px 拖影"和"50 px 长边",且方向画的是梯度方向(⊥ 视觉拖影方向)→ 矢量场长度固定,**颜色按 drag_width 编码**,方向 +π/2 改画拖影线;`MaxHalfWidth` 提到对角线 0.8% 让超长边能诚实显示为"肌理色"
> 3. **刚体拟合不可靠**:v3 用硬阈值切 spread,边界附近抖动;且每格"线段方向"无极性(0 与 180 不可分),平移分量符号不定 → 改加权最小二乘 + 迭代符号对齐,权重按 `drag_r = drag_width / 对角线` 梯形过渡
>
> 同期修正:抖动矢量场长宽比按图像实际比例铺开(v3 是正方形)。
>
> **v4.1 修订摘要(2026-05-16 下半场,1181 / 1197 对照样本驱动)**:v4.0 落地后用户提供"1181 无抖 vs 1197 竖直 20 px 抖"对照组,实测仍存在两个问题 — 矢量场方向毫无规律 + 颜色全部停在灰/红段无法区分长结构与短拖影。两步连续修正:
> 1. **矢量方向用结构张量主方向**(`θ_st = ½·atan2(2·Sxy, Sxx−Syy)`):之前 `drag_bucket = max_bucket` 在城市强长边场景会被那一条孤立长边拐走;改用每格全块的结构张量主梯度方向,极大改善方向场可读性(1197 上 ↖+← 二桶占 96%,1181 散乱)。同时引入各向异性 `A = (λ1−λ2)/(λ1+λ2)` 作为新的标量 5,替代废弃的 `luma_mean`,在 `BuildShakeField` 中作为掩膜门控(A < 0.2 视为无主方向,不画也不参与刚体)
> 2. **drag_bucket 选 (θ_st + π/2) 同向 bucket**(关键修正):前一版本错选了"与主结构边平行的 gradient 方向" → 测的是结构边自身横向锐度(永远 1-3 px),colour 永远落灰/红段。真正被运动模糊拉成 ramp 的是**垂直于主结构**的那批边(它们的 gradient 沿拖影方向),其 Marziliano 横向宽度 ≈ 拖影长度
> 3. **颜色 / 权重阈值整体下移一档**(1181/1197 实测校准):sweet spot 从"~20-48 px"重新中心化到"~15 px @ 6000×4000"(drag_r ≈ 0.18% D)。原阈值是按"max-bucket 绝对宽 + 误选 bucket"的高估值定的;θ_st+π/2 同向 bucket 给出的实际 drag_width 整体下移近一倍
> 4. **离线诊断工具 `Tools/CvDebugTool/`**:命令行解码 HIF/HEIC/JPG → `CvGridExtractor.ExtractFromLuma` → 输出锐度 PNG / 抖动矢量场 PNG / 文本报告(全图直方图 + Σw / |T| / |ω| / residual)。无需启动 Avalonia,批量回归与阈值校准都靠它
>
> v3 抖动方案(`spread = max - min` / 8-桶 5-边阈值 / 硬阈值刚体拟合 / 矢量场正方形 / 矢量场画梯度方向)整体进墓碑(§4)。v3 锐度方案保留为 v4 的一部分。v4.0 中间态的 `max_bucket = θ_st 同向 bucket` 已在 r1 修正,见 §4。

---

## 0. 定位

### 0.1 本次收尾只做什么

一句话:把 [CvGridExtractor](../PhotoViewer/Core/AI/CvGridExtractor.cs) / [CvHeatmap](../PhotoViewer/Core/AI/CvHeatmap.cs) / [ShakeFieldView](../PhotoViewer/Views/Tools/ShakeFieldView.cs) / DINO 诊断页的 CV 抖动从 v3 升级到 v4(锐度路径不动),让 §3 验收通过。配套离线工具 [Tools/CvDebugTool/](../Tools/CvDebugTool/) 用于阈值校准与样本回归。

为什么:Plan-2 §5.2 CV 肉眼验收实测 v3 锐度过审,但抖动模块在主任务"小光斑旋转抖识别"上漏报、在霓虹/建筑场景误报、刚体拟合不可靠。v3 的根因是 `spread = max - min` 这个差值定义本身不适合主任务 —— 旋转抖让所有方向同步变宽,差值反而消失。继续按 v3 往前推会把不达标的抖动基建带进阶段 IV 训练,触发 Plan-1 §0.5 陷阱 4(把热力图当客观标注)。

**用户严格度(2026-05-13 明确)**:虚焦与抖动按**像素级**量化 —— 普通场景下模糊或位移超过 10 px 即为严重问题,故意虚化背景可超过 100 px,需要保证 **1 px 尺度**的可分辨度。  
**v4 补充(2026-05-16 上半场)**:抖动量级与图像分辨率相关,小图同等手抖产生的拖影像素数也按比例缩短。所有抖动阈值改用**对角线 D = √(W²+H²)** 归一化。  
**v4.1 校准(2026-05-16 下半场)**:在 1181/1197 对照样本上实测发现"θ_st+π/2 同向 bucket 的 drag_width" 整体落点比"max-bucket 绝对宽"低近一倍,sweet spot 重新中心化到 ~15 px @ 6000×4000(drag_r ≈ 0.18% D)。

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

## 1. CV 诊断算法 v0 → v4

### 1.1 v0 / v1 / v2 / v3 / v4.0 中间态失效史

| 路线 | 思路 | 失效点 |
|---|---|---|
| v0(已落地) | `lap × sobel` 失焦 / 低熵掩膜抖动 / 金字塔 CV | 只能分"物体 vs 天空" |
| v1(草案 v1.1,否决) | 结构张量 (θ,α,m) + L0/L1 sobel 比 | 全格扫的稀释效应,无法分 1/10/100 px |
| v2(草案 v2.0,否决) | 中心 128 块 + Marziliano + 线性映射 + 16 网格 + NaN 中灰 | (a) 线性映射 1-3 px 压成半亮 (b) 16 网格与 DINO 32 错位 (c) 自适应 τ_edge 让 ISO 100 干净天空挤出"假边" |
| v3(草案 v3.0,锐度过审 / 抖动否决) | 中心采样 + Marziliano + 32 网格 + 对数锐度 + `spread = max - min` 差值抖动 + 硬阈值刚体拟合 + 矢量场画梯度方向 | **锐度部分实测过审,保留进 v4**。抖动部分三类失败:(a) `spread = max - min` 差值定义在小光斑旋转抖时各方向同步变宽 → 差值趋零 → **主任务漏报** (b) 矢量场只画线、长度限位、画的是梯度方向,无法区分"5 px 拖影"与"50 px 长边",且方向与视觉拖影 ⊥ (c) 硬阈值切刚体拟合 + 0/180 不可分导致 \|T\| 符号不定,文本面板长期误报"疑似平移手抖" |
| v4.0 中间态(2026-05-16 上半场,已修正) | drag_bucket = max-width bucket / 颜色阈值 sweet spot 在 0.33-0.67% D / 矢量场 +π/2 画拖影线 | 1181 / 1197 对照样本实测两类失败:(a) 矢量场方向看似规律但杂乱(原因:max-width bucket 在城市强长边场景被那一条孤立长边拐走,不能反映"主拖影方向") (b) 颜色 99% 落在灰/红段,鲜橙/黄从未出现(原因:颜色阈值是按 v3 max_bucket 的高估值定的,与新方案的实际 drag_width 不匹配) |

### 1.2 v4 路线:结构张量主方向 + 绝对边宽 + 对角线归一化 + 颜色编码 + 加权刚体

**核心翻转**(对比 v3):抖动量级不再是"该格内方向间的差值",而是"该格主方向的**绝对**像素边宽"。城市灯阵旋转抖时每格只有一条主拖影方向,该方向的边宽就是拖影长度 → 即使所有格的"差值"都趋零,只要绝对值在 sweet spot 就能被识别。

**v4.1 关键修正**(对比 v4.0):每格的"主方向"不再用 max-width bucket 估计,改用**结构张量主梯度方向 θ_st**;且 `drag_width` 取的是 **(θ_st + π/2) 同向 bucket 的中位边宽**而非 θ_st 同向 bucket。

物理直觉:运动模糊把"垂直于拖影方向的边"拉成 ramp,这些边的 gradient 沿拖影方向 → 它们落在 (θ_st + π/2) bucket 里(因为 θ_st 本身就是块内主梯度方向 = 主结构边的法线)。读取这批边的 Marziliano 横向宽度,得到的就是拖影像素长度。读 θ_st 同向 bucket 等价于读"主结构边自身的横向锐度",永远 1-3 px,与拖影无关。

抖动算法的三个分层:
1. **每格度量**(`drag_width`,`drag_direction`,`anisotropy`):在块上累结构张量 (Sxx,Syy,Sxy),给 `θ_st = ½·atan2(2·Sxy, Sxx−Syy)` 与 `A = √((Sxx−Syy)² + (2·Sxy)²) / (Sxx+Syy)`;`drag_bucket` = 离 (θ_st + π/2) 最近的有效 bucket;`drag_width` = 该桶中位绝对边宽;`drag_direction` = θ_st + π/2(即拖影线方向,直接用张量值,不取 bucket 中心避免 ±π/16 量化抖)
2. **可视化层**(矢量场):线段长度固定 = `cellHalf × 0.85`,**颜色按 drag_r = drag_width / 对角线 D 分段**,亮度峰值落在 sweet spot,两端低亮,让"需警觉"的格最醒目;**A < 0.20 的格视为无主方向,不画**
3. **全图判读层**(刚体拟合):加权最小二乘 + 迭代符号对齐,权重按 drag_r 梯形过渡(同样有 A 掩膜),0/180 不可分通过迭代翻转 v_i 符号收敛

**粒度(v3 沿用,不变)**:
- 网格 **32×32**,与 DINO patch 网格对齐
- 每格中心采样块尺寸 P **自适应**:`P = clamp(短边 / 32, 64, 192)`
- CV 输入用原始分辨率,走 `BitmapLoader.GetBitmapAsync` 复用 LRU 缓存

**对角线归一化(v4 新增)**:`D = sqrt(W² + H²)`。所有抖动阈值与颜色编码都按 D 归一化,小图同等手抖按比例对齐。6000×4000 时 D ≈ 7211,15 px 拖影对应 drag_r ≈ 0.21% D。

**单边步进上限(v4 改)**:`MaxHalfWidth = round(0.008 × D)`,6000×4000 时 ≈ 58 px,单边最大 0.8% D → 总宽天花板 1.6% D > 颜色编码上限 0.30% D 的 5 倍,**"肌理色"段不会被算法天花板截断,长建筑边能诚实显示**。

**bucket 边数阈值(v4 改)**:`MinEdgesPerBucket: 5 → 3`,让稀疏拖影方向(图 3 小光斑旋转抖)能进入中位数统计。

### 1.3 v4 标量集合(每格 6 个,1 层,无金字塔)

字节布局:`32×32 × 6 × 1 = 6144 float`(v3 同长度)。版本号 `cv_grid_v3_32grid` → `cv_grid_v4_structtensor`。

| # | 标量 | 单位 | 意义 | 计算 |
|---|---|---|---|---|
| 0 | `edge_count` | 整数 | 块内强梯度像素数(NaN 判据,沿用 v3) | 一次扫描计数 |
| 1 | `edge_width_p20` | px | 最锐 20% 边的平均跨像素宽度(锐度路径,沿用 v3) | Marziliano |
| 2 | `edge_width_median` | px | 所有强边宽度中位数(调试用) | Marziliano |
| 3 | `drag_width` | px | **(θ_st + π/2) 同向 bucket 的中位绝对边宽**(v4.1 改,v4.0 是 max-width bucket) | 结构张量主方向 + 8-bin 边宽谱 |
| 4 | `drag_direction` | rad(`[0,π)`) | **拖影线方向 = θ_st + π/2**(直接用结构张量值) | 结构张量主方向 + π/2 |
| 5 | `anisotropy` | [0,1] | **结构张量各向异性 A=(λ1−λ2)/(λ1+λ2)**(v4.1 替换原 `luma_mean`) | 结构张量特征值差 |

**0/180 不可分声明**:`drag_direction` 是无极性线方向,不是矢量方向。`[0, π)` 区间即代表"一条无极性线段的角度"。刚体拟合时用迭代符号对齐处理(见 §1.4.3)。

**τ_edge 处理(v3 沿用)**:`τ_edge_floor = 30.0`(luma 0-255 标度),自适应 `τ_edge = max(块内 mag p90, τ_edge_floor)`,块内 p90 < floor → `edge_count = 0` → 该格 NaN。

**Marziliano 单边测宽(v3 沿用,仅 MaxHalfWidth 上限改)**:
1. 用 Sobel 挑幅值 `mag > τ_edge` 且**单方向局部极大**的像素作边种子
2. 沿梯度方向正负两侧各步进,直到亮度**单调性打破**或跨过 **MaxHalfWidth** px,记录左右平台位置
3. 边宽 = 总跨度;左右任一方向撞到块边界 → 该边丢弃

**结构张量主方向(v4.1 新增)**:在块上累 Sobel 平方与互积:`Sxx = Σsx²`,`Syy = Σsy²`,`Sxy = Σsx·sy`。两特征值 `λ1,2 = (Sxx+Syy)/2 ± √(((Sxx−Syy)/2)² + Sxy²)`,主梯度方向 `θ_st = ½·atan2(2·Sxy, Sxx−Syy)`(折到 `[0, π)`),各向异性 `A = (λ1−λ2)/(λ1+λ2)`。块内整体能量统计,比单条边方向更稳健 —— 一两个孤立长边带不偏。

### 1.4 v4 三张诊断图

布局仍是 3 张 32×32 + 文本面板。

#### 1.4.1 `Sharpness` — 边宽对数热力图(v3 沿用,**不动**)

```
edge_count < N_min OR p90 < τ_edge_floor:    map = 0   (虚焦深色,与"严重虚"同色)
else:                                         w = edge_width_p20
                                              t = (log(w_vis) − log(w)) / (log(w_vis) − log(w_sharp))
                                              map = clamp(t, 0, 1)
```

- `w_sharp = 1.5 px`(全亮)→ `w_vis = 10 px`(全暗)
- 调色板:viridis 不变

#### 1.4.2 `MotionBlur` — 拖影线矢量场(v4 全改)

**线段长度固定**,不再表示拖影长度。颜色按 `drag_r = drag_width / D` 分段编码,亮度峰值落在 sweet spot。

绘制规则:
- 每格线段长度统一 = `cellHalf × 0.85`
- 线段方向 = `drag_direction`(已 +π/2,画的是拖影线方向)
- 颜色:按下表 drag_r 段映射(亮度峰值落在 sweet spot,两端低亮)
- drag_r < 0.033% D 不画
- **anisotropy < 0.20 不画**(v4.1 新增,无主方向的格直接跳过)
- **矢量场长宽比按图像比例铺开**(v3 是正方形,v4 修正)

**颜色编码表(v4.1 r1 校准,1181/1197 实测下移一档)**(对角线 D = √(W²+H²) 归一化,6000×4000 时 D ≈ 7211):

| drag_r 区间 | 6000×4000 实际 | 区间含义 | 颜色趋势 |
|---|---|---|---|
| < 0.033% D | < 2.4 px | 无信号 / 测不准 | 不画 |
| 0.033% — 0.06% D | 2.4 — 4.3 px | 微抖 | 暗灰 → 暗红(亮度低) |
| 0.06% — 0.10% D | 4.3 — 7.2 px | **需警觉**(sweet spot 起点) | 暗红 → 鲜红(亮度上升) |
| 0.10% — 0.18% D | 7.2 — 13 px | 可疑(sweet spot 主区) | 鲜红 → 鲜橙(**亮度峰值**) |
| 0.18% — 0.30% D | 13 — 22 px | 存疑(偏向肌理) | 鲜橙 → 暗黄(亮度回落) |
| > 0.30% D | > 22 px | 肌理 / 建筑长边 | 灰黄(亮度低) |

> 历史档位(v4.0,2026-05-16 上半场,实测全部落灰/红):0.125% / 0.33% / 0.67% / 1.25% D。已废弃,见 §4 r1 行。

#### 1.4.3 `RigidMotion` — 加权刚体拟合 + 迭代符号对齐(v4 全改)

输入:所有 `Mask = true` 且 `drag_width` 有效的格,**不做硬阈值切**。

**步骤**:
1. **权重计算**(梯形,平滑过渡,r1 校准):
   ```
   w(drag_r):
     drag_r < 0.033% D       →  w = 0
     0.033 ≤ drag_r < 0.05%  →  w 线性 0 → 1
     0.05 ≤ drag_r ≤ 0.20%   →  w = 1            (核心置信区,对应 ≈4-15 px @ 6000)
     0.20 < drag_r ≤ 0.40%   →  w 线性 1 → 0
     drag_r > 0.40%          →  w = 0
     anisotropy < 0.20       →  w = 0            (v4.1 新增,无主方向直接剔)
   ```
2. **初始向量**:每格 `v_i = +drag_width_i · (cos(drag_dir_i), sin(drag_dir_i))`(统一取正方向)
3. **迭代符号对齐**(最多 3 轮):
   - 用 v_i 做加权最小二乘求 `T, ω`(标准 normal equation 加权)
   - 对每格预测 `v_pred = T + ω × r_i`
   - 若 `v_pred · v_i < 0` → 翻转 `v_i` 符号
   - 重拟合;无符号翻转时提前收敛
4. **输出**:`|T|`、`|ω|`、`residual_rms`、`Σw`(有效权重和)

**判定文本**(研发判断用,不考虑用户友好性):
- `Σw < 20` → "信息不足"
- `residual_rms > 0.6 × hypot(|T|, |ω|·R)` → "混乱场景"(R = 图像半对角线,把 ω 换算到边缘像素位移量级)
- 其他 → 输出 `|T|` / `|ω|` / `residual_rms` 数值

`|T|` 方向因 0/180 不可分而最多有 180° 翻转,但 `|T|` 幅值与 `|ω|` 数值可靠。文本面板照常显示,**不在 UI 中标注此模糊性**(§4 决策:UI 只做研发判断)。

### 1.5 参数默认值(硬编码,不开交互)

| 参数 | 默认值 | 用途 |
|---|---|---|
| `P`(采样块尺寸) | clamp(短边/Grid, 64, 192) | 每格中心采样;自适应到原始分辨率 |
| `Grid` | 32 | 网格边长(与 DINO patch 对齐) |
| `D`(对角线) | √(W²+H²) | 所有抖动阈值归一化基准 |
| CV 解码 | `BitmapLoader.GetBitmapAsync` | 原始分辨率,不下采样;共享 LRU 缓存 |
| `w_sharp` / `w_vis` | 1.5 px / 10 px | 锐度图归一化两端(对数尺度,v3 沿用) |
| `τ_edge_floor` | 30.0 | 块内 mag p90 下限(luma 0-255 标度,v3 沿用) |
| `τ_edge` | max(p90, τ_edge_floor) | 边种子阈值 |
| `plateau_τ` | τ_edge / 4 | 边宽步进停止 |
| `MaxHalfWidth` | round(0.008 × D) | **单侧最大步进距离(v4 改,原硬编码 8 px)** |
| `N_min_edges` | 80 | 块内最小边数,不足判 NaN |
| `MinEdgesPerBucket` | **3**(v4 改,原 5) | 方向桶最小边数 |
| `BucketCount` | 8 | 方向桶数 |
| `AnisotropyMin` | **0.20**(v4.1 新增) | A 低于此判"无主方向",不画也不参与刚体 |
| 锐度颜色区间 | 1.5 px / 10 px | 见 §1.4.1 |
| 抖动颜色编码 | 6 段相对值(r1 校准) | 见 §1.4.2 表 |
| 刚体拟合权重 | 梯形 0.033%/0.05%/0.20%/0.40% D(r1 校准) | 见 §1.4.3 |
| 刚体迭代上限 | 3 轮 | 符号对齐 |

### 1.6 对 DINO 诊断页的连带要求

CV 路径走 **`BitmapLoader.GetBitmapAsync`** 拿原始分辨率位图(v3 沿用)。DINO 路径仍走 `ThumbnailService` 的 560 短边,两条解耦。VM 不可 Dispose CV 大图 —— 它属于 BitmapLoader 的缓存。
格中心像素坐标 = `((x + 0.5) * W / 32, (y + 0.5) * H / 32)`,块边界按 `P = clamp(min(W,H)/32, 64, 192)` 推出,裁剪到图像内;块短边 < 64 px → 该格 NaN(深色)。

**矢量场长宽比**(v4 新增):`ShakeFieldView.AspectRatio` 必须由 `DinoDebugViewModel` 传入实际图像 `Width / Height`,axaml 中绑定该属性,**不再使用默认 1.0**。

### 1.7 离线诊断工具(v4.1 新增)

工具:[Tools/CvDebugTool/](../Tools/CvDebugTool/) — 命令行可执行,引用 `PhotoViewer.csproj` 的 `Core/AI` 层。

**用途**:不启动 Avalonia UI,直接对单文件 / 多文件批量跑 CV v4 → 输出可视化 PNG + 文本统计,用于:
- 阈值校准(本次 v4.0 → v4.1 的颜色 / 权重区间都是用它在 1181/1197 对照样本上调出来的)
- 样本回归(改动算法后批量过 14 张验收图,看哪些条目断了)
- 第三方分发(把 PNG / 报告甩给 reviewer,无需对方安装 Avalonia)

**调用**:`dotnet run -c Release --project Tools/CvDebugTool -- <file1> [file2] ...`

**输出**(同目录,与输入同名):
- `<name>_sharpness.png` — 锐度热力图(viridis,32×32 × 16 px)
- `<name>_shake.png` — 抖动矢量场(2048 宽,暗化原图叠底 + 矢量线段)
- `<name>_report.txt` — 全图统计:有效格数 / 各向异性格数 / `drag_width` 与 `drag_r` 的 p10/p50/p90、8 方向直方图、`Σw` / `|T|` / `|ω|` / `residual_rms`

**实现要点**:
- HEIF/HIF/HEIC/AVIF 走 LibHeifSharp(与桌面端一致的 "stride 检测竖拍" 逻辑);JPG/PNG 走 ImageSharp
- RGB → Rec.709 luma → `CvGridExtractor.ExtractFromLuma(luma, W, H)` 新公开入口(跳过 Avalonia `RenderTargetBitmap` 路径)
- 颜色 / 权重 / 阈值常量与 `CvHeatmap` / `ShakeFieldView` **保持同步**,改一处必同步另一处
- 工具不入 `PhotoViewer.sln`,与 `Tools/ExifTestTool/` 同模式

---

## 2. 远期问题预判(出问题再做)

本期**不实现**,每条带触发条件,只在条件满足时开工。

| # | 问题 | 触发 | 预案 |
|---|---|---|---|
| R1 | 高 ISO 噪点把 Marziliano 边数撑起来,给假锐读数 | §3 验收中场景 7 ISO 3200+ 夜景 Sharpness 图在纯噪点区偏亮 | 边种子加"梯度方向稳定性"过滤(3×3 邻域 θ 方差 < 阈值)或 3×3 中值预处理 |
| R2 | 1 px 精度不够(实际锐图 p20 常在 1.5~2 px) | Sharpness 无法区分"锐 vs 极锐"时 | 边宽步进升级到亚像素(抛物线拟合 mag) |
| R3 | 自然纹理(头发 / 草 / 叶)被测成"均匀宽边"误判为虚 | 阶段 IV 训练发现误当"虚焦信号" | 由 DINO patch 语义加权兜底,不在 CV 层加规则 |
| R4 | 刚体拟合仍把栏杆 / 屋顶 / 白线误读成手抖 | v4 验收后 §3.2 "霓虹/建筑长边不被误报"未通过 | 加"全图 direction 一致性检验"(方差过小时视为静止纹理);或把 0.20%-0.40% D 段权重压得更陡 |
| R5 | 手抖旋转中心偏离画面中心 | 实测旋转抖中心不在画面中心 | 刚体拟合把 c 作为第三未知量(2 参数 LS → 3 参数);或 RANSAC |
| R6 | 采样块运气差(采到天空/墙)让整图 NaN 比例过高 | §3 验收中出现整张图灰片 | "多采几点取最清晰"的升级:每格在中心 ±P/4 四角额外采 4 个 P/2 小块,取边数最多那个 |
| R7 | 2560 短边解码 I/O 太慢 | 诊断页切图肉眼可感顿挫 | 独立 CV 解码通道做内存缓存;或直接使 JPEG/HEIF 内嵌 2048 级 preview |
| R8 | FFT 高频能量占比 | 废弃,见墓碑 §4 #FFT | 边宽路线已直接给 px,正交冗余度高 |
| R9 | 结构张量 (θ,α,m) | 废弃,见墓碑 §4 #structure | 稀释效应无解 |

---

## 3. 验收 checklist

规则:全部打钩才开工阶段 III。

### 3.1 代码落地

**v3 锐度部分已落地(保留)**:
- [x] [CvGridResult.cs](../PhotoViewer/Core/AI/CvGridResult.cs):v3 落地的 `GridSize = 32` / 32×32 网格 / 自适应块尺寸保留
- [x] [CvGridExtractor.cs](../PhotoViewer/Core/AI/CvGridExtractor.cs):v3 落地的 `τ_edge_floor = 30f` 保留
- [x] [CvHeatmap.cs](../PhotoViewer/Core/AI/CvHeatmap.cs):v3 落地的 `BuildSharpness` 对数映射保留
- [x] [DinoDebugViewModel.cs](../PhotoViewer/ViewModels/Tools/DinoDebugViewModel.cs):v3 落地的 CV 走 `BitmapLoader.GetBitmapAsync` 原始分辨率保留
- [x] [DinoDebugView.axaml](../PhotoViewer/Views/Tools/DinoDebugView.axaml):v3 落地的"32×32"标签保留

**v4 抖动部分(v4.0 + v4.1 已落地)**:
- [x] [CvGridResult.cs](../PhotoViewer/Core/AI/CvGridResult.cs):`CurrentVersion = "cv_grid_v4_structtensor"`,`ScalarNames[3] = "drag_width"` / `ScalarNames[4] = "drag_direction"` / `ScalarNames[5] = "anisotropy"`(v4.1 替换 `luma_mean`),`DataLength = 32*32*6 = 6144`(不变)
- [x] [CvGridExtractor.cs](../PhotoViewer/Core/AI/CvGridExtractor.cs):`MaxHalfWidth` 改 `round(0.008 × D)`;`MinEdgesPerBucket: 5 → 3`;Sobel 累加时同步累结构张量 (Sxx,Syy,Sxy);`drag_bucket` = 离 `(θ_st + π/2)` 最近的有效 bucket;`drag_width` = 该桶中位绝对边宽;`drag_direction = θ_st + π/2`;标量 5 写 `anisotropy`;新增 `ExtractFromLuma(luma, W, H, ct)` 公开入口供工具调用
- [x] [CvHeatmap.cs](../PhotoViewer/Core/AI/CvHeatmap.cs):`ShakeField` 含 `Direction`/`Width`/`Mask`/`Diagonal`;`AnisotropyMin = 0.20f` 在 `BuildShakeField` 中作为掩膜门控;`FitRigidMotion` 加权 LS + 迭代符号对齐;阈值常量按 r1 校准(`DragRMinDisplay 0.033%` / `DragRWeightRampEnd 0.05%` / `DragRWeightFalloffStart 0.20%` / `DragRMaxValid 0.40%`)
- [x] [ShakeFieldView.cs](../PhotoViewer/Views/Tools/ShakeFieldView.cs):线段长度固定 = `cellHalf × 0.85`;颜色按 `drag_r` 6 段编码(r1 校准:B1=0.06% / B2=0.10% / B3=0.18% / B4=0.30%);长宽比按 `AspectRatio` 实际生效;接收 `Diagonal` 用于归一化
- [x] [DinoDebugView.axaml](../PhotoViewer/Views/Tools/DinoDebugView.axaml):`ShakeFieldView` 绑定 `AspectRatio`(图像 W/H);标签更新 "抖动矢量场 (drag_width / direction)"
- [x] [DinoDebugViewModel.cs](../PhotoViewer/ViewModels/Tools/DinoDebugViewModel.cs):把 `CvDecodedBitmap.PixelSize.Width / Height` 传给 `ShakeFieldView.AspectRatio`,`FormatRigidMotion` 改优先级链(信息不足 > 混乱场景 > 旋转 > 平移 > 静止纹理)
- [x] [Tools/CvDebugTool/](../Tools/CvDebugTool/)(v4.1 新增):命令行 HIF/HEIC/JPG → CV → PNG + 文本报告
- [x] Windows Debug 跑通,切图无崩溃、编译零 warning

### 3.2 14 张代表样本 CV 目测(Plan-1 §0.3 七类各 2 张)

**v3 锐度三条已过审(保留打钩)**:
- [x] **锐度 · 1 px/3 px/10 px 分层**:人像锐焦 p20 应 ≤ 2 px(满亮),轻度失焦 p20 应在 3-6 px(中灰),严重失焦 p20 应 ≥ 10 px(暗);三档在 Sharpness 图上肉眼可分辨
- [x] **锐度 · 纯色区判虚**:夜景天空 / 纯墙采样块 `p90 < τ_edge_floor` → NaN → 锐度图显示为 0(深色,与"严重虚"同色),不误报为"锐"
- [x] **锐度 · 光斑判虚**:夜景 bokeh 球内部边宽实测 ≥ 5 px,显示偏暗

**v4 抖动验收(v4.1 校准后重新打开)**:
- [ ] **抖动 · 主任务:小光斑旋转抖可读**(对应实测图 3 高楼俯拍城市夜景):每盏灯都有可识别拖影,矢量场应出现**沿圆周切向**的线段密集分布,颜色集中在 sweet spot(暗红→鲜红→鲜橙,drag_r ≈ 0.06%-0.30% D);文本判定 `|ω| > 0.02 rad` 且 `residual_rms < 0.6 × hypot(|T|, |ω|·R)`
- [ ] **抖动 · 霓虹 / 建筑长边不被误报为抖动**(对应实测图 1 霓虹灯):矢量场中**长方向 / 整齐方向**的格颜色为**灰黄**(drag_r > 0.30% D),刚体拟合权重接近 0;`Σw` 远小于"信息不足"阈值 20 → 文本"信息不足"(或 `residual_rms` 极小但 `Σw < 20`)
- [ ] **抖动 · 信息过少场景诚实输出"信息不足"**(对应实测图 2 大光圈纵深街道):矢量场零散少量短线段,刚体拟合 `Σw < 20` → 文本输出"信息不足",不再误判"疑似平移手抖"
- [x] **抖动 · 矢量场拖影线方向与目视一致**(v4.1 r1 通过):结构张量主方向给出整张图能量分布的统计方向,实测 1197(竖直抖动)矢量场 ↖+← 二桶占 96%,1181(无抖)方向散乱;**用户 2026-05-16 反馈"方向非常合理,字符画美感"**
- [ ] **抖动 · 矢量场长宽比与图像一致**:横构图照片矢量场区域应为横向(非正方形)
- [ ] **抖动 · 颜色编码 sweet spot 醒目**(v4.1 r1 校准后):≈7-22 px @ 6000×4000 区间的格在矢量场中**明显比两端醒目**(亮度峰值);< 2.4 px 或 > 22 px 的格在视觉上不抢眼;**1197 实测中位 drag_r=0.154%(鲜红→鲜橙过渡)与 1181 中位 0.131%(鲜红区)已有明显颜色差异**
- [ ] **抖动 · 车流 / 反光不误报**:夜景车流矢量场杂乱方向,加权后 `residual_rms` 大 → 文本"混乱场景"

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
| v3 `spread = max_bucket − min_bucket` 差值定义 (#dragspread) | 主任务漏报:小光斑旋转抖时各方向同步变宽,差值反而趋零;改用 `drag_width = max_bucket 中位绝对边宽` |
| v3 `MinEdgesPerBucket = 5` 阈值 | 稀疏拖影方向(图 3 小光斑)被切掉无法进中位数;v4 改 3 |
| v3 `MaxHalfWidth = 8` 硬编码 | 单边步进 8 px 让总宽天花板 16 px 远低于"肌理色"段 90 px;v4 改 `round(0.008 × D)`,让超长建筑边能诚实显示 |
| v3 矢量场画梯度方向 | 与视觉拖影方向 ⊥ 难读;v4 改画 `梯度方向 + π/2`(拖影线方向) |
| v3 矢量场长度表示 spread | 长短线段堆叠难读、且 spread 量级表达冗余于颜色;v4 长度固定 = `cellHalf × 0.85`,**信息全靠颜色** |
| v3 矢量场正方形 | 与图像长宽比无关,扭曲视觉对位;v4 按 `AspectRatio` 实际比例铺开 |
| v3 硬阈值切刚体拟合 | spread < 2 px 直接丢弃产生边界抖动,且阈值与图像分辨率无关;v4 改加权梯形过渡 + 对角线归一化 |
| v3 刚体拟合不处理 0/180 不可分 | 平移分量符号不定,文本面板长期误报"疑似平移手抖";v4 加迭代符号对齐 |
| 抖动量级用绝对像素阈值 | 不同分辨率下同等手抖产生像素位移不同;v4 一律按对角线 D 归一化 |
| v4.0 `drag_bucket = max-width bucket`(2026-05-16 上半场) | 城市强长边场景会被那一条孤立长边拐走,矢量场方向看起来杂乱;v4.1 改用结构张量主方向 `θ_st`,块内整体能量统计 |
| v4.0 `drag_bucket = θ_st 同向 bucket`(中间过渡态) | 实际测的是主结构边自身横向锐度(永远 1-3 px),颜色永远停在灰/红段无法区分长结构与短拖影;v4.1 改用 `θ_st + π/2` 同向 bucket,测的是被拖影拉成 ramp 的那批边 |
| v4.0 颜色阈值 sweet spot 0.33-0.67% D(2026-05-16 上半场) | 按 max-width bucket 高估值定的,与新方案实际 drag_width 不匹配,实测 99% 落灰/红段;v4.1 r1 校准下移到 0.10-0.18% D |
| v4 标量 5 `luma_mean` | 累 lumaSum 浪费一次完整块扫描,且无人消费;v4.1 复用槽位写 `anisotropy`,bump 版本号无库迁 |
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
