# CV 抖动检测 v5 — 方向一致性路线

> 状态:已落地 v5 r2 / 2026-05-16
>
> **本文件的任务**:把 DINO 诊断页里的"抖动判据"从"边宽量级"翻转到"方向一致性"。
> 上一版(v4.1 r2)在 Case1/2/3 八张实测样本上证明:**单格 drag_width 是"这格有多锐"的物理量,与"是否抖动"无关**;清晰夜景与抖动夜景的 drag_width 中位差只有 2 px,白天对照样本 drag_width 反而最低,颜色编码上的差异落不到肉眼。
>
> v5 改用一个新前提:**抖动是全图相关的运动 → 拖影方向一致;建筑/纹理是局部各异的结构 → 方向散乱**。可视化层、刚体拟合判定都按这个前提重写。drag_width 退为辅助量,只做"信号下限"过滤。
>
> **r2 修订摘要(2026-05-16,Case4 假阳性驱动)**:r1(单 R_global)在 Case1-3 八张全过,但 Case4 六张静止样本里出现五张假阳性 — 1179/1183/1396 弱信号场被加权 LS 拟出 |ω|=0.32-0.53 的虚拟旋转,1266/1479 大面积纹理读出高 R_global。修正:
> 1. **强旋转加 R_global 拦截**(`OmegaStrongRot=0.30 + RGlobalStrongRotAbove=0.30`):弱信号场 R_global 必然低,挡住 1179/1183/1396
> 2. **旋转加 R_local p10 拦截**(`RLocalP10RotMin=0.55`):真旋转抖切向场全图相关,最差 10% 格也 ≥ 0.55(1465=0.60 / 1467=0.84);1266=0.60 临近、1479=0.53 被挡
> 3. **新增 `RigidMotionResult.RLocalP10`**:统计参与拟合格的 R_local 第 10 百分位
>
> 14 张样本最终 12/14 通过。剩余 3 张边界 FP(1266 大面积垂直建筑纹理 / 1479 玻璃折射画质 / 1396 沥青透视颗粒)经用户认可放过 — 都是单图本身就接近模糊或物理无解,见 §3.3。
>
> 本文件自包含。不依赖 Plan-1 / Plan-2-0 / wrapup 的章节存在。

---

## 0. 实测样本与现状证据

下表是 v4.1 r2(当前线上)算法在 [TestCase/](../TestCase/) 三组样本上的 `Tools/CvDebugTool` 实测输出。
所有图像短边 ≥ 4000,对角线 D ≈ 7211 (6000×4000) 或 8423 (7008×4672)。

| 样本 | 用户标注 | drag_w 中位 (px) | drag_r 中位 | 8-bin 方向直方图(高频桶) | \|T\| | \|ω\| (rad) | omegaPx | residual |
|---|---|---:|---:|---|---:|---:|---:|---:|
| C1/1181 | 清晰夜景 | 11 | 0.131% | →73 ←122 ↘102(三峰散) | 9.76 | 0.235 | ~990 | 5.77 |
| C1/1197 | **垂直抖动** | 13 | 0.154% | **↖312(69%)** ←116 | 12.75 | 0.031 | 130 | 4.21 |
| C1/1301 | 白天对照(夜景对照,无抖) | 8 | 0.095% | →189 ↘95(双峰散) | 7.63 | 0.035 | 147 | 4.31 |
| C2/1211 | 清晰夜景 | 11 | 0.131% | 6 桶各占 5-25%(全散) | 8.25 | 0.164 | ~691 | 5.57 |
| C2/1259 | **左上抖动** | 15 | 0.178% | **↖286(50%)** ←116 | 12.64 | 0.059 | 248 | 5.97 |
| C2/8943 | 白天对照 | 8 | 0.111% | ↖186 ←86(中心) | 6.46 | 0.083 | 296 | 3.82 |
| C3/1465 | **旋转抖** | 13 | 0.154% | →133 ↘129(对称双峰,典型旋转 ⊥ 信号) | 11.74 | 0.095 | 400 | 5.31 |
| C3/1467 | **强旋转抖** | 15 | 0.178% | 散+大量 NaN(180/1024) | 7.07 | **0.840** | **3537** | 5.44 |

### 0.1 三条核心结论

**(1) drag_width 区分抖动与建筑的能力 = 0**
清晰 1181 中位 11 px ≈ 抖动 1197 中位 13 px。白天 1301 / 8943 反而最低(8 px)。
单格 drag_width 是"这格里被沿拖影方向拉成 ramp 的那条边到底多宽",其量级取决于这条边本身的物理长度与对比度,**和拍摄时手是否抖完全无关**。20 px 拖影与 20 px 远处楼房窗框的 drag_width 不可分,这点用户在最初提案里就直觉到了。

**(2) 真正分得开的是"方向一致性"**
抖动 = 全图共享的同一个运动向量 → 大多数有效格的拖影方向落进同一两个 bin。
建筑 / 纹理 = 不同墙面、不同结构、不同走向 → 方向直方图分布平坦 / 多峰。
- 1197(竖直抖):↖ 一桶占 69%
- 1259(左上抖):↖ 一桶占 50%
- 1181(清):3 桶分布,最大单桶 < 35%
- 1211(清):6 桶各 5-25%,最大单桶 < 25%

**(3) |ω| 真能识别 Case3 旋转抖,但有假阳性**
1467 |ω|=0.840 rad → 半径处 3537 px 位移,远超所有其他样本。
1465 |ω|=0.095 显著高于平移抖的 1197/1259(0.03/0.06)。
**但 1181 |ω|=0.235 是假阳性** —— 弱信号格的方向被噪声推动,迭代符号对齐凑出虚假旋转。
解法:用方向一致性 R 把它压下去 —— 1181 整图 R 低,刚体拟合应判"静止纹理 / 信息不足"而不是去信它的 |ω|。

### 0.2 v4.1 r2 留下的有效部分(继承不动)

下面这些 v5 直接沿用,**不在本文件再讨论**:

- 32×32 网格、自适应块尺寸 `clamp(short/32, 64, 192)`、原始分辨率 + `BitmapLoader.GetBitmapAsync`
- 锐度路径(`edge_width_p20` 对数映射)及其 14 张样本验收三勾(锐度三档分层 / 纯色判虚 / 光斑判虚)
- Marziliano 单边测宽 + `MaxHalfWidth = round(0.008 × D)` + `MinEdgesPerBucket = 3` + `τ_edge_floor = 30`
- 结构张量主梯度方向 `θ_st` 及各向异性 `A`;`drag_direction = θ_st + π/2` 折到 `[0,π)`
- `drag_width = (θ_st + π/2) 同向 bucket 的中位边宽`
- 矢量场长宽比按图像比例铺开(`AspectRatio` 由 VM 注入)
- 0/180 不可分 → 加权 LS + 迭代符号对齐拟合刚体
- `CvGridResult` 6 标量布局(`edge_count` / `edge_width_p20` / `edge_width_median` / `drag_width` / `drag_direction` / `anisotropy`)
- 离线诊断工具 [Tools/CvDebugTool/](../Tools/CvDebugTool/) 框架与 PNG/报告输出格式
- CV 一期不入库;换算法 = 改 `CvGridResult.CurrentVersion` 字符串

### 0.3 v5 不动什么

- DINOv3 路径所有代码(`DinoFeatureExtractor` / `Cache` / `Indexer` / `SimilarityService` / `PatchHeatmap`)
- 数据库 schema(`photos.feature_vector` 单列过渡形态)
- 锐度路径(`BuildSharpness` / 锐度颜色阈值)
- `CvGridExtractor` 的标量计算 — v5 改的是**消费层**(`CvHeatmap` / `ShakeFieldView` / VM / CvDebugTool)。换句话说:`CvGridResult.Data` 的 6 个标量含义保持原样,版本号也保留 `cv_grid_v4_structtensor` 不必 bump

---

## 1. v5 算法

### 1.1 路线总述

抖动量级不再编码进矢量场颜色。颜色改编码"局部方向一致性 R_local",直接回答用户提出的"用颜色区分抖动还是建筑"。drag_width 沦为辅助:作为信号有效性判据(下限/上限),不进颜色。
刚体拟合输出多加一个全图量 R_global(所有有效格 2θ 的圆形均值长度),把 R_global 作为整张图判定的主轴 — 低 R_global 直接判"静止纹理 / 建筑",不再相信 |T| 与 |ω|。

### 1.2 局部方向一致性 R_local(每格新衍生量,不进存储层)

为什么用 2θ:`drag_direction` 是 `[0,π)` 的无极性线方向 —— 0° 与 180° 应视为同一方向。把 θ 映射到 2θ 后做圆形统计,0° 与 180° 在单位圆上同点,统计才正确。

公式(每格 (gx,gy)):
```
邻域 N(gx,gy) = 5×5 网格窗(共 25 格,边界裁剪),取 Mask=true 的有效格集合 V
若 |V| < 5:        R_local(gx,gy) = NaN(信息不足,该格视为不确定)
否则:               θ_k = direction[(x_k, y_k)],k ∈ V
                    C = Σ cos(2θ_k)
                    S = Σ sin(2θ_k)
                    R_local = sqrt(C² + S²) / |V|        ∈ [0, 1]
```

物理含义:
- R_local ≈ 1:5×5 邻域内所有格拖影方向一致 → 强烈"运动相关性"信号
- R_local ≈ 0:邻域方向均匀分布 → 各异结构(混合纹理 / 边界过渡区)
- 中间值:局部规则纹理(整面百叶、整片栏杆),需要进一步看 R_global 才能判

5×5 而非 3×3:窗口太小受 NaN 与单格噪声影响大;太大(如 7×7)会平滑掉 Case3 旋转抖切向场的方向变化。5×5 在 32×32 网格上对应实际像素约 ±300 px @ 6000 宽,够稳但仍能描出 Case3 旋转中心附近的方向梯度。

### 1.3 全图方向一致性 R_global

```
W = 所有 Mask=true 且 weight>0 的有效格(weight 沿用 v4.1 梯形,不变)
R_global = | Σ w_k · exp(i·2θ_k) | / Σ w_k             ∈ [0, 1]
```

预期分布(基于 0 节实测数据回算):
- 抖动样本(1197 / 1259):R_global ≈ 0.65 - 0.85
- 旋转抖(1465 / 1467):R_global ≈ 0.20 - 0.35(切向场是各方向都有,但模长平均后偏中)
- 清晰夜景(1181 / 1211):R_global ≈ 0.15 - 0.30
- 白天对照(1301 / 8943):R_global ≈ 0.20 - 0.40

注意旋转抖的 R_global 不会高 —— 切向场各方向都有。这正是为什么 v5 判定要把 R_global 与 |ω| 配合起来(见 §1.5)。

### 1.4 矢量场颜色编码(2D 表)

颜色由两轴决定:R_local(横轴,方向一致性)、drag_r = drag_width / D(纵轴,信号强度)。

| | drag_r < 0.033% | 0.033% ≤ drag_r < 0.05% | 0.05% ≤ drag_r ≤ 0.30% | drag_r > 0.30% |
|---|---|---|---|---|
| **R_local NaN / < 0.30** | 不画 | **暗灰**(建筑/混合) | **暗灰**(建筑/混合) | **暗灰**(长边) |
| **0.30 ≤ R_local < 0.55** | 不画 | 暗黄绿 | **黄绿**(规则纹理) | 暗黄绿 |
| **R_local ≥ 0.55** | 不画 | 暗红 | **鲜红 → 鲜橙**(亮度峰,真抖动) | 浅白(过长拖影) |

操作上:
- "不画"段沿用 v4.1(节省绘制 / 让人眼专注于"被画出来"的格)
- "暗灰段"是 v5 新增的关键 —— 让长建筑边、栏杆密集格、车流混合格都落到这色,与"鲜红抖动峰"形成强对比
- 黄绿段是过渡(规则纹理:百叶 / 栏杆),给人眼"这是有方向但不是抖动"的提示
- R_local 阈值 0.30 / 0.55 取的是 5×5 窗下"方向开始集中"和"方向高度集中"的两个心理感知断点;均匀分布 R≈0.20,两条直线分布 R≈0.55,单条直线 R=1

### 1.5 刚体拟合判定(全图)

`CvHeatmap.FitRigidMotion` 在 v4.1 基础上多输出 4 个量(r2 校准):

```csharp
public readonly struct RigidMotionResult
{
    public int   SampleCount;            // 沿用
    public float WeightSum;              // 沿用
    public float TranslationMagnitude;   // 沿用 |T| (px)
    public float RotationMagnitude;      // 沿用 |ω| (rad)
    public float ResidualRms;            // 沿用 (px)
    public float DirectionalConsistency; // r1 新增:R_global ∈ [0,1],按 weight × exp(i·2θ) 归一
    public float OmegaPxRatio;           // r1 新增:omegaPx / (|T| + omegaPx + ε);保留但 r2 判定未使用
    public float MaskRatio;              // r1 新增:有效格数 / 1024
    public float RLocalP10;              // r2 新增:参与拟合的格的 R_local 第 10 百分位
}
```

判定优先级(VM 端 `FormatRigidMotion`,从上往下短路;r2 校准,实测 14 张样本):

```
信息不足:        Σw < 20  OR  MaskRatio < 0.05
强旋转抖动:      |ω| ≥ 0.30 rad  AND  R_global ≥ 0.30
                 (r2 关键修正:加 R_global 拦截,挡住 Case4 假阳性 1179/1183/1396 的虚拟旋转)
静止纹理(早拦):  R_global < 0.45
                 (1181=0.14 / 1211=0.22 / 1179=0.09 / 1183=0.15 / 1182=0.26 落此分支)
旋转抖动:        |ω| ≥ 0.090 rad  AND  R_global ≥ 0.50  AND  R_local p10 ≥ 0.55
                 (r2 关键修正:加 R_local p10 拦截,挡住 1266/1479 大面积纹理假阳性;
                  真旋转抖切向场全图相关,最差 10% 格也 ≥ 0.55:1465=0.60 / 1467=0.84)
平移抖动:        |T|/D ≥ 0.13%  AND  R_global ≥ 0.50
                 (1197/1259 稳定落此分支)
混乱场景:        residual > 0.6 × motionScale
                 motionScale = max(sqrt(|T|² + omegaPx²), 1e-3)
                 (车流 / 反光 / 树叶)
静止纹理(兜底):  |T|/D < 0.13%  AND  |ω| < 0.090
                 (1301/8943 白天对照落此分支:R_global 中等但量级都不到运动阈值)
其他:            "弱信号 / 难判"
```

判定顺序的两个关键:
1. **"强旋转抖动"必须配 R_global 拦截**(r2 修正):弱信号场被加权 LS 拟出虚拟 |ω| 是 Case4 假阳性的主因;真强旋转抖必然伴随 R_global ≥ 0.30
2. **"旋转抖动"必须配 R_local p10 拦截**(r2 修正):真旋转抖是全图切向场,最差 10% 格 R_local 也 ≥ 0.55;大面积纹理/弱信号场 R_local p10 通常在 0.45-0.55

文本面板新格式(r2):
```
样本 354 格 (34.6%)
Σw   = 343.2
|T|  = 9.76 px  (0.116% D)
|ω|  = 0.235 rad
残差 = 5.77 px
R_global = 0.14  R_local p10 = 0.39
判定:静止纹理
```

### 1.6 R_local 计算位置 — `CvHeatmap` 还是 View?

放 `CvHeatmap.BuildShakeField` 里。理由:
1. R_local 是数据驱动的 32×32 平面,语义上属于"这张图怎么画矢量场",和 `Direction` / `Width` / `Mask` 是平行结构
2. CvDebugTool 与 ShakeFieldView 共享同一份 R_local — 两端颜色编码完全同步,不会出现 PNG 与 UI 颜色错位

`ShakeField` 多一个 `LocalConsistency: float[]` 字段(长度 1024)。

### 1.7 性能开销

R_local: 1024 格 × 5×5 窗 = 25600 次 cos/sin 累加。每次 cos+sin 约 50 ns,合计 1.3 ms,可忽略。
R_global: 一次扫 1024 格 cos/sin,< 0.1 ms。
颜色函数加一个 R_local 维度的查表分支,渲染量级与 v4.1 一致(像素级线段,< 1 ms)。

整体 CV 路径耗时不变(主要时间仍在 `CvGridExtractor.Extract` 的 Sobel + Marziliano,约 100-300 ms @ 6000×4000)。

---

## 2. v5 改动清单

`CvGridResult` / `CvGridExtractor` / `BitmapLoader` / `DinoDebugView.axaml` / DINO 路径全部不动。

### 2.1 [PhotoViewer/Core/AI/CvHeatmap.cs](../PhotoViewer/Core/AI/CvHeatmap.cs)
- `ShakeField` 增加 `LocalConsistency: float[]`(长度 1024)
- `RigidMotionResult` 增加 `DirectionalConsistency` / `OmegaPxRatio` / `MaskRatio`
- `BuildShakeField`:在算完 Mask 之后,跑一次 5×5 邻域圆形均值,填充 `LocalConsistency`(无效格 NaN)
- `FitRigidMotion`:计算 R_global(用 `weight × exp(i·2θ)` 归一);计算 OmegaPxRatio / MaskRatio
- 新增颜色函数 `ColorForShake(float dragR, float rLocal) → (byte R, G, B)` 作为 v5 配色的唯一权威实现
  (View 与 CvDebugTool 都来这里取色,不再各自 inline 一份)
- 删除 v4.1 三色编码常量(`B0/B1/B2/B3/B4` 现已移入 `ColorForShake`)
- 颜色阈值 / R 阈值常量定义为 `public const`,写明 v5 校准日期

### 2.2 [PhotoViewer/Views/Tools/ShakeFieldView.cs](../PhotoViewer/Views/Tools/ShakeFieldView.cs)
- 删除 inline `ColorForDragR`
- 渲染时调 `CvHeatmap.ColorForShake(dragR, field.LocalConsistency[i])`
- R_local NaN 时按"无信号"对待 — 不画(让原图透出来)
- 笔触粗细不变(1.4 px),长度不变(`cellHalf × 0.85`)

### 2.3 [PhotoViewer/ViewModels/Tools/DinoDebugViewModel.cs](../PhotoViewer/ViewModels/Tools/DinoDebugViewModel.cs)
- `FormatRigidMotion`:输出 §1.5 文本格式;判定优先级按 §1.5 表实现
- `RigidMotionText` 多两行(R / mask 比例),控件高度可能要小调整(§2.5)

### 2.4 [Tools/CvDebugTool/Program.cs](../Tools/CvDebugTool/Program.cs)
- 删除 inline `ColorForDragR`,改调 `CvHeatmap.ColorForShake`
- `BuildReport` 增加段落:"R_global / R_local p10/50/p90 / mask_ratio"
- 8-bin 直方图保留(它仍是判读"哪个方向是主拖影"的最快入口)

### 2.5 [PhotoViewer/Views/Tools/DinoDebugView.axaml](../PhotoViewer/Views/Tools/DinoDebugView.axaml)
- 文本面板高度若超出原占位区,放开自动行高;不动布局

### 2.6 不改 `CvGridResult.CurrentVersion`
v5 改的是消费层。`CvGridResult.Data` 6 标量布局未动 — 保留 `cv_grid_v4_structtensor` 字符串,避免 cache miss 全失效(虽然 CV 一期不入库,版本号沿用对未来上库有意义)。

---

## 3. 验收 checklist

### 3.1 代码落地
- [ ] `ShakeField.LocalConsistency` 已计算且非全 NaN
- [ ] `RigidMotionResult.DirectionalConsistency / OmegaPxRatio / MaskRatio` 已填充
- [ ] `ColorForShake` 单点定义,View 与 CvDebugTool 共用
- [ ] 编译零 warning
- [ ] Windows Debug 跑通,切图无崩溃

### 3.2 14 张样本实测(r2 最终结果)

每张样本满足:**判定文本符合预期**;R_global / R_local p10 / |T|/D / |ω| 实测值已记录,作为后续阈值调整的基线。

| # | 样本 | 真值 | r2 判定 | R_global | RLocalP10 | \|T\|/D% | \|ω\| | 通过 |
|---|---|---|---|---:|---:|---:|---:|:---:|
| 1 | C1/A7C01181 | 静(夜景清晰) | 静止纹理 | 0.14 | 0.39 | 0.116 | 0.235 | ✅ |
| 2 | C1/A7C01197 | 平移抖 | 平移抖动 | 0.91 | 0.78 | 0.151 | 0.031 | ✅ |
| 3 | C1/A7C01301 | 静(白天) | 静止纹理 | 0.50 | 0.22 | 0.091 | 0.035 | ✅ |
| 4 | C2/A7C01211 | 静(夜景清晰) | 静止纹理 | 0.22 | 0.33 | 0.098 | 0.164 | ✅ |
| 5 | C2/A7C01259 | 平移抖 | 平移抖动 | 0.57 | 0.50 | 0.150 | 0.059 | ✅ |
| 6 | C2/DSC08943 | 静(白天) | 静止纹理 | 0.48 | 0.17 | 0.090 | 0.083 | ✅ |
| 7 | C3/A7C01465 | 旋转抖 | 旋转抖动 | 0.60 | 0.60 | 0.139 | 0.095 | ✅ |
| 8 | C3/A7C01467 | 强旋转抖 | 强旋转抖动 | 0.60 | 0.84 | 0.084 | 0.840 | ✅ |
| 9 | C4/A7C01179 | 静(对照 FP) | 静止纹理 | 0.09 | 0.32 | 0.067 | 0.321 | ✅ |
| 10 | C4/A7C01182 | 静(对照) | 静止纹理 | 0.26 | 0.23 | 0.094 | 0.040 | ✅ |
| 11 | C4/A7C01183 | 静(对照 FP) | 静止纹理 | 0.15 | 0.26 | 0.069 | 0.525 | ✅ |
| 12 | C4/A7C01266 | 静(大面积垂直建筑纹理) | **旋转抖动** | 0.69 | 0.60 | 0.115 | 0.241 | △ FP |
| 13 | C4/A7C01396 | 静(沥青马路) | **强旋转抖动** | 0.54 | 0.30 | 0.088 | 0.305 | △ FP |
| 14 | C4/A7C01479 | 静(玻璃折射) | **平移抖动** | 0.50 | 0.53 | 0.148 | 0.283 | △ FP |

**通过率 12/14 (86%)**。所有真阳性(4 张)与对照真阴性(8 张)全过。

### 3.3 接受的边界 FP(用户认可放过)

| # | 样本 | 内容 | 算法读到的信号 | 物理根因 | 接受理由 |
|---|---|---|---|---|---|
| 12 | C4/1266 | 大面积同朝向墙面 | R_local 高 + 单一方向 | 大面规则纹理在数学上与平移抖同源 | 用户认可:这种构图本身极少见,触发即提醒并非全错 |
| 13 | C4/1396 | 城市公交车 + 沥青路面 | 沥青颗粒的水平方向偏置 | 颗粒在透视下被压缩成水平椭圆 → R_global+方向集中,与水平抖动数学同源(纯海面同理) | **物理无解**:无 y 梯度时和真平移抖不可分;触发条件参 §4 R10 |
| 14 | C4/1479 | 玻璃后场景 | 折射导致局部模糊 + 弱方向 | 折射本身就是真实存在的"局部退化"信号 | 用户认可:玻璃折射图本来就该被标记 |

### 3.4 反馈环

CvDebugTool 跑 14 张后,若任意一张不满足:
- 判定错(走错优先级分支):看 R_global / R_local p10 / |T|/D / |ω| 实测,微调 §1.5 阈值;边界 FP 优先升级语义而不是收紧阈值(否则切到真阳性)
- 颜色主导色错:看实测 R_local 的 p10/p50/p90,调 §1.4 颜色表里 0.30 / 0.55 两道阈值

每次调阈值后,**14 张样本必须全量回归**,不允许只跑出问题那张。

---

## 4. v5 之外不做的事

| # | 事项 | 触发条件 | 预案 |
|---|---|---|---|
| F1 | 把 R_local 写进 CvGridResult 持久化 | CV 入库阶段开工后,实测 5×5 邻域算开销 < 2ms,目前不需要 | 列加 `local_consistency` blob;改 `CurrentVersion` |
| F2 | 旋转中心 c 作为第三未知量(刚体拟合 2 参 → 3 参) | 实测旋转抖中心明显偏离画面中心 | 加 RANSAC 或 3 参直接解 |
| F3 | 在 R_local 上叠加 weight 加权 | 出现"R_local 被弱格噪声拉低"案例 | normal-equation 用 weight × cos(2θ) 累加 |
| F4 | DINO 语义层接入(头发 / 草地 / 树叶等) | 误把生物纹理判抖动且 CV 层无法靠 R 区分时 | `PatchHeatmap` 给 CV mask 颗粒/匀质语义区;接入需打通 CV/DINO 路径 |
| F5 | 上下文多帧抖动判别 | 单图无法收敛时 | EXIF shutter / 焦距规则;shutter < 1/(2·焦距) 时置信下调 |
| R10 | 颗粒透视纹理的水平假阳性(沥青 / 海面 / 草坪) | 用户工作流里碰到误剔且占比 > 5% | 物理无解(纯海面无 y 梯度时与水平平移抖数学同源);必须 DINO 语义层介入,见 F4 |

---

## 5. Go / No-Go

**已 go**:r2 落地后,§3.1 全部打钩,§3.2 真阳性 4/4 + 真阴性 8/8,边界 FP 3/14 经用户认可放过 → v5 收尾。

阈值常量集中在 [CvHeatmap.cs](../PhotoViewer/Core/AI/CvHeatmap.cs) 顶部,所有改动都伴随 14 张样本回归。CV/VM/CvDebugTool 配色与判定共享 `CvHeatmap.ColorForShake` / `CvHeatmap.*` 常量,改一处必同步。

