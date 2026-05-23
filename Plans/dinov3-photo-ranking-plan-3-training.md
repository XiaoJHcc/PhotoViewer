# DINOv3 照片美学评分 — 三期计划（MLP 训练）

> 状态：草案 v0.1 / 2026-05-23
> 上游：[Plan-2-4](dinov3-photo-ranking-plan-2-4-exif-rating.md)
>
> **本文件的任务**：从已入库的三路原始数据（DINO CLS + patch token + CV grid + EXIF）出发，完成训练对生成、MLP 训练、评估验收，产出可部署的美学评分模型。

---

## 0. 需求锚点

> 这一章不是项目背景，是**项目宪法**。技术方案怎么变都行，但只要任何阶段的产物违背了这章的任一条目，就是跑偏了。

### 0.1 用户与拍摄习惯

- 摄影爱好者，SONY A7C2，多拍选优工作流。
- 单次外拍 ~600 张/天，人工筛选 ~5 小时（占用大量精力，这是要替代的工作量）。
- 拍摄方式：同地点多角度、多焦距（24/35/85 等）、多构图反复尝试。
- 已有数据资产：**上万张历史照片 + 完整人工星级标注**（0-5 星）。

### 0.2 真实选片流程（标注数据的来源，不可重新打标）

- **滑动窗口比对**：把同时间段/同场景的照片放一起两两比较，不跨场景跨日期。
- **每轮 Top 50% 晋级**：0→1→2→3→4→5 星共 5 轮。
- 隐含假设：本星级和高于本星级的照片数量大致相等（锦标赛结构）。
- 不同层级的人工关注点（既有数据已混合在一起，拆不开）：
  - **0→1 星**：孤立照片考虑清晰度、有效性（不是乱拍的），相似的多张照片挑出其中较好的一半
  - **1→2 星**：开始考虑美学氛围，或在多张 1 星中选优
  - **2→3 星**：雷同内容选优（多张同拍摄点、同主体对象、同角度中最好的一张，也有孤立的、但拥有好氛围、或极具代表性的好照片）
  - **3→4 星**：同题材不同视角选优，同时要求美学价值较高
  - **4→5 星**：全局精品
- **隐含污染**：极相似的照片（像素级差异，甚至连清晰度都完全一致）在人工判断中有可能是随机选其一晋级的，这部分对比是无意义噪声，但含量极少。

> 经过二期计划，我们已经通过 CV 提取出像素级差异（例如可能其中一张更清晰），增强极相似照片的可判别性。所以不算污染。

### 0.3 典型痛点与场景举例（模型必须有能力应对）

| # | 场景 | 模型必须做到 |
|---|---|---|
| 1 | **同山头不同焦距/构图** — 24mm vs 35mm，偏左 vs 偏右 | 判断"留白呼吸"加分 vs "囊括杂乱树枝"减分 |
| 2 | **同题材长焦梯田** — 风格内容几乎一致，只是田埂曲线 / 小房子位置不同 | 判断抽象视觉元素在构图中的构成美学，而非题材喜好 |
| 3 | **沿山脊移动** — 前景变了远山没变 | 判断同题材下差异部分的美学优劣 |
| 4 | **静物连拍** — 两张几乎一样，仅像素级差异 | 判断对焦、抖动等技术问题（对于视觉模型无法分辨的像素级信息，已经通过 CV 提取） |
| 5 | **孤立瞬间抓拍** — 氛围/光线极佳但无相似可比 | 模型能力不再依赖对比，直接判断美学。需要模型能够理解优秀美学的潜质，如果这张照片有 5 星潜质，则至少给到 3 星，满足 3 星召回需求，但不能过度预测。 |
| 6 | **主体 vs 背景的题材化权衡** — 人像要主体锐+背景虚，风光要全清+接受留白，有些题材可实可虚 | 根据题材语义切换权衡，不是统一规则 |
| 7 | **夜景抖动判断** — CMOS 防抖失败，部分清晰部分拖影 | 通过 CV 在全图网格采样计算数据，结合 ViT 注意力，重点识别建筑边缘 / 点光源，避开车流/树叶等天然变化区，判断照片各处是否该实、该虚 |

**这 7 类场景是验收基线** — 任何阶段的中间产物或最终模型，必须能在每一类的代表样本上给出合理输出。验证集设计直接对齐这 7 类。

### 0.4 核心目标

**模型唯一目标：学习摄影美学。**

- 既要能在两两/多张对比中选优（高度相似场景），也要能对孤立照片直接判断美学价值（避免漏氛围片） — **这是同一种能力的两面，不拆任务**。
- 相似组内压低相对较差的照片，而非刻意评优；相似判定并非严格阈值聚类，需要按权重压低；保证相似组中最优照片必须最高分。

**产品形态**：人机协作 — 高置信度自动化，低置信度回人工。

**运行约束**：准确性优先。本期只在 4080 桌面端跑得动即可，内存/算力放开用。iPad / 移动端下放放到远期。

**成功预期**：≥3 星召回率 ≥ 80%（数量限制 Top 12.5%），且 ≥3 星中不要包含太多雷同相似照片。

### 0.5 必须避免的副作用与陷阱

| # | 副作用 | 触发原因 | 对策 |
|---|---|---|---|
| 1 | **模型退化为输出平均分** | 跨场景标签自相矛盾时硬做绝对回归 | Pairwise loss + 局部可比域约束；监控 val score 方差 |
| 2 | **模型记忆特定场景而非美学** | 随机划分 train/test，同次拍摄跨集 | 严格按拍摄日期划分 |
| 3 | **退化为人工规则集合** | 堆硬规则 | 软权重，数据驱动，不堆规则 |
| 4 | **学到粗暴场景偏置** | 跨题材/跨时段两两组对 | 训练对必须满足局部可比域（同天 + embedding 近） |
| 5 | **全自动幻想牺牲可用性** | 追求完全替代人工 | 人机协作，置信度回人工 |

### 0.6 设计决策

**训练目标与损失**：
- **不**做绝对 0-5 分类回归（跨场景同星级标签不可信）
- 第一版用**Pairwise margin ranking loss**，相似度作为连续权重（越相似的对越重要）
- 输出**美学得分 + 置信度**两路

**输入架构**：
- 三路并行：**DINOv3 [CLS]** (384d) + **patch token** (32×32×384) + **CV 网格** (32×32×7) + **EXIF** (4d)
- patch token 作为 CV 网格的 per-cell 语义权重，保留全量 32×32 空间信息
- 第一版**不做邻域聚合**，纯单图 → score
- 输入聚合方案 **A/B 两方案并行实验**（见 §2）

**训练数据约束**：
- ~2 万张，锦标赛分布
- 训练对在**局部可比域**内生成：同一天 + CLS cosine ≥ 下限阈值（数据验证后定）
- **不设上限排除**：极相似对（cosine ≥ 0.98）照常参与训练，CV/EXIF 提供技术差异信号
- 相似度越高的训练对权重越大（压制相似劣片是核心目标）
- 数据集按**拍摄日期**划分 train/val/test，同天照片严格在同一 split

**评估**：
- 核心指标：≥3★ 召回率 @ 模型 Top 12.5%（相似组去重后）≥ 80%
- 相似组内最优照片必须得分最高

### 0.7 不做的事

- 绝对 0-5 分类回归 / 强拆"技术分/美学分/去重分"
- DINO 热力图截 patch 喂训练
- 上下文 Transformer / 邻域聚合（远期条件触发）
- 任意跨天两两组对（必须同天 + embedding 近）
- 一次性追求"完全替代人工"

---

## 1. 数据准备（Plan-3 第一步，开发者专用工具）

### 1.1 批量入库工具

`Tools/batch_ingest.py` — 独立 Python 脚本，扫描照片文件夹，批量填充 `photos.db`。

**职责**：
1. 遍历目标文件夹（递归），对每个 JPG/HEIF/ARW 文件：
   - 计算 fingerprint（SHA1(filename_noext + DateTimeOriginal + SubSecTimeOriginal)，与 C# `PhotoFingerprint` 一致）
   - 读取 EXIF：focal_length / aperture / shutter_speed / crop_factor
   - 读取 XMP rating（0-5）
   - 写入 `photos` 表（`INSERT OR IGNORE` 身份 + EXIF + rating）
2. 对缺少 DINO 特征的行，调用 ONNX 推理补齐 CLS + patch → 写入 `photo_features` / `photo_patches`
3. 对缺少 CV grid 的行，调用 CV 提取 → 写入 `photos.cv_grid`

**与 app 内 indexer 的关系**：功能重叠但场景不同。app indexer 是用户交互触发、单文件夹、C# 实现；batch_ingest 是开发者离线批量、多文件夹、Python 实现（可用 GPU 加速 DINO 推理）。两者共享同一个 `photos.db` schema，互不冲突。

**输出**：一个填满的 `photos.db`，包含 ~2 万张照片的完整五元组 `(fingerprint, cls_vector, patch_tokens, cv_grid, rating)` + EXIF 字段。

### 1.2 数据验证与统计

`Tools/data_audit.py` — 入库完成后跑一次，输出：

| 统计项 | 用途 |
|---|---|
| 星级分布直方图 | 确认锦标赛结构 |
| CLS cosine 全库直方图 + 分位数 | 校准可比域阈值（当前假设 ≥0.5，需验证） |
| 同天内 cosine 分布 vs 跨天 cosine 分布 | 验证"同天+embedding 近"是否有效分离可比对 |
| 每天照片数分布 | 评估 train/val/test 按天划分的粒度 |
| 缺失字段统计 | 确保覆盖率 ≥ 95% |

**关键决策点**：cosine 阈值。如果全库直方图显示 0.5 处有自然断层（同题材 vs 跨题材），则采用；否则按分位数调整。

### 1.3 训练对生成

`Tools/build_training_pairs.py` — 从 `photos.db` 生成训练对文件。

**算法**：
```
对每一天 D:
  取该天所有有 rating 的照片集合 S_D
  对 S_D 中每对 (a, b) 其中 rating(a) > rating(b):
    cos = cosine(cls_a, cls_b)
    if cos >= COMPARABLE_THRESHOLD (待定，~0.5):
      生成训练对 (a, b, margin = rating(a) - rating(b), cosine = cos)
    else:
      跳过（跨题材不可比）
```

**不设上限排除**：cosine 极高（≥ 0.98）的对照常入训练集。这些对的星级差异来自 CV 可检测的技术差异（对焦精度、抖动程度）或 EXIF 差异（ISO 高低），是模型必须学会的判别能力。

**相似度权重**：每对附带 `cosine` 字段，训练时作为 loss 权重 — 越相似的对越重要（压制相似劣片是核心目标）。

**输出格式**：`train_pairs.parquet` / `val_pairs.parquet` / `test_pairs.parquet`
- 列：`fp_a, fp_b, rating_a, rating_b, cosine, margin, day`

**数据集划分**：按拍摄日期排序，前 70% 天数 → train，中 15% → val，后 15% → test。同一天的所有照片严格在同一个 split 内。

### 1.4 预期数据量估算

~2 万张，假设平均每天 100 张，共 ~200 天：
- train: ~140 天 × 100 张 = ~14000 张
- val: ~30 天 × 100 张 = ~3000 张
- test: ~30 天 × 100 张 = ~3000 张

每天 100 张中，可比对数量取决于 cosine 阈值。假设平均每张有 ~5 个可比邻居，星级差 ≥1 的对约占 50%：
- 每天可比训练对 ≈ 100 × 5 × 50% / 2 = ~125 对
- train 总对数 ≈ 140 × 125 = ~17500 对

这个量级对 MLP 训练足够（参数量 < 100K，不需要百万级数据）。

---

## 2. 模型架构

### 2.1 方案 A — 统计聚合（轻量，~423 维输入）

```
输入层:
  patch_tokens: (1024, 384)     ← 32×32 展平
  cv_grid:      (1024, 7)       ← 32×32 × 7 标量
  cls:          (384,)
  exif:         (4,)            ← log(focal×crop), log(aperture×crop), log(shutter), crop

Per-cell 权重:
  w_i = softmax(patch_i · cls)  ← (1024,) 主体相关性

统计聚合 (每个 CV 标量 × 5 统计量 = 35d):
  对 7 个 CV 标量各算:
    weighted_mean   = Σ(w_i × cv_i) / Σw_i
    weighted_std    = sqrt(Σ(w_i × (cv_i - mean)²) / Σw_i)
    weighted_p10    = 加权 10% 分位数
    weighted_p90    = 加权 90% 分位数
    unweighted_min  = min(cv_i)   ← 最差格（抖动/虚焦信号）

MLP 输入: concat(cls[384], cv_stats[35], exif[4]) = 423d
MLP:       423 → 256 → 128 → 1 (score)
           + dropout 0.3 between layers
```

**优势**：输入维度低，MLP 参数少（~120K），2 万张数据不容易过拟合。
**风险**：统计聚合可能丢失空间分布模式（如"左半清右半糊"）。

### 2.2 方案 B — 全量 flatten（信息无损，~7556 维输入）

```
输入层:
  patch_tokens: (1024, 384)
  cv_grid:      (1024, 7)
  cls:          (384,)
  exif:         (4,)

Per-cell 融合:
  cell_i = concat(cv_i[7], patch_i · cls[1])  ← 8d per cell
  spatial_map = flatten(cell[1024×8]) = 8192d

MLP 输入: concat(cls[384], spatial_map[8192], exif[4]) = 8580d
MLP:       8580 → 512 → 256 → 128 → 1 (score)
           + dropout 0.5 between layers (防过拟合)
           + weight decay 1e-3
```

**优势**：空间信息完整保留，MLP 可以学到"左半清右半糊"等模式。
**风险**：参数量 ~4.5M，2 万张 / ~17K 对可能不够撑；需要更强的正则化。

### 2.3 方案选择策略

1. **先跑方案 A**（快速迭代，几分钟训完）
2. 如果 A 在 val 上达标（≥3★ 召回 ≥ 80%）→ 直接用 A
3. 如果 A 不达标，分析失败案例：
   - 若失败集中在"局部抖动/虚焦"（空间信息丢失）→ 试方案 B
   - 若失败集中在"跨题材混淆"（语义不够）→ 问题不在聚合方式，需调损失/数据
4. 方案 B 若过拟合严重 → 降维折中（如 per-cell 8d → 1d attention pool）

### 2.4 置信度输出

两个方案共享同一个置信度设计：

```
MLP 最后一层输出 2d: (score, log_confidence)
confidence = sigmoid(log_confidence) ∈ (0, 1)
```

置信度的监督信号：训练对的 cosine 越高（越相似）、margin 越大（星级差越大）→ 该对的置信度标签越高。具体：
```
conf_target = cosine × (margin / 5)
```

低置信度的照片在产品端回人工复核（§0.4 人机协作）。

---

## 3. 训练 Pipeline

### 3.1 损失函数

**Similarity-Weighted Pairwise Margin Ranking Loss**：

```python
def pairwise_loss(score_a, score_b, margin, cosine):
    # score_a 应该 > score_b（a 星级更高）
    # margin 越大，要求的分差越大
    target_gap = base_margin + margin * scale_factor
    raw_loss = max(0, target_gap - (score_a - score_b))
    # 相似度越高的对越重要（压制相似劣片是核心目标）
    weight = cosine_weight_floor + (1 - cosine_weight_floor) * cosine
    return raw_loss * weight
```

超参数：
- `base_margin = 0.1`（最小分差）
- `scale_factor = 0.2`（每差一星多要求 0.2 分差）
- `cosine_weight_floor = 0.3`（cosine=0 时的最低权重，保证低相似对仍有梯度）

**设计意图**：cosine=0.95 的对权重 ≈ 0.97，cosine=0.5 的对权重 ≈ 0.65。极相似对（连拍/同机位）的技术差异判别成为最强训练信号，同时跨构图对比仍有贡献。

**置信度辅助损失**（权重 0.1）：
```python
conf_loss = MSE(predicted_confidence, conf_target)
```

**总损失**：
```python
total_loss = pairwise_loss + 0.1 * conf_loss
```

### 3.2 训练配置

| 参数 | 方案 A | 方案 B |
|---|---|---|
| Optimizer | AdamW | AdamW |
| Learning rate | 1e-3 | 5e-4 |
| Weight decay | 1e-4 | 1e-3 |
| Batch size | 256 pairs | 128 pairs |
| Epochs | 100 | 200 |
| Dropout | 0.3 | 0.5 |
| LR scheduler | CosineAnnealing | CosineAnnealing |
| Early stopping | val loss 10 epochs no improve | val loss 15 epochs no improve |

### 3.3 数据加载

```python
class PairDataset(Dataset):
    def __init__(self, pairs_parquet, db_path):
        self.pairs = pd.read_parquet(pairs_parquet)
        self.db = sqlite3.connect(db_path)
    
    def __getitem__(self, idx):
        row = self.pairs.iloc[idx]
        feat_a = self.load_features(row.fp_a)  # (cls, cv_grid, patch, exif)
        feat_b = self.load_features(row.fp_b)
        return feat_a, feat_b, row.margin, row.cosine
```

特征全部从 DB 读取（已入库），**不做实时 DINO/CV 推理**。训练循环只跑 MLP 前向 + 反向，4080 上预计 < 1 分钟/epoch（方案 A）。

### 3.4 训练脚本

`Tools/train_ranking_head.py`：
- 参数：`--db`, `--pairs-dir`, `--variant` (A/B), `--output-dir`
- 输出：`best_model.pt` + `training_log.json` + `config.yaml`
- 支持断点续训（`--resume`）

---

## 4. 评估

### 4.1 核心指标

**指标 1：≥3★ 召回率 @ Top 12.5%（去重后）**

操作定义：
1. 对 test set 所有照片跑模型得 score
2. 在每个相似组（同天 + cosine ≥ COMPARABLE_THRESHOLD）内，只保留 score 最高的一张（去重）
3. 去重后取 score Top 12.5% 的照片集合 P
4. 召回率 = |P ∩ {rating ≥ 3}| / |{rating ≥ 3}|

**目标：≥ 80%**

**指标 2：相似组压制率**

操作定义：
1. 对 test set 中每个相似组（≥2 张，cosine ≥ COMPARABLE_THRESHOLD）
2. 组内最高星级照片的 score 应为组内最高（或 Top-2）
3. 压制率 = 组内最高星级照片 score 排名为 Top-1 的比例

**目标：≥ 60%**

**指标 3：技术废片剔除率**

操作定义：
1. 0★ 照片中，score 落在全局 Bottom 50% 的比例
2. 目标：≥ 90%（0★ 照片绝大多数应该得低分）

### 4.2 七类场景抽检

从 test set 中按 §0.3 七类场景各取 2-3 个代表案例，人工检查模型输出是否合理：

| 场景 | 检查项 |
|---|---|
| 1 同山头不同焦距 | 构图更好的得分更高 |
| 2 同题材长焦梯田 | 构图美学更好的得分更高（或极近时 tie） |
| 3 沿山脊移动 | 可比对内有区分度 |
| 4 静物连拍 | 技术更好的（更锐 / ISO 更低）得分更高 |
| 5 孤立抓拍 | 氛围佳的不被压低（score 不低于同天中位） |
| 6 主体/背景权衡 | 人像主体锐+背景虚得高分；风光全清得高分 |
| 7 夜景抖动 | 抖动照片 score 明显低于同组清晰照片 |

### 4.3 失败分析工具

`Tools/eval_analysis.py`：
- 输出 test set 中"模型判错"的案例列表（模型 Top-1 ≠ 人工最高星级的组）
- 按失败原因分类：空间信息丢失 / 语义混淆 / 数据噪声（tie 未过滤干净）
- 指导方案 A→B 切换决策

---

## 5. 部署（ONNX 导出）

训练完成且指标达标后：

### 5.1 导出

```python
# Tools/export_ranking_head.py
torch.onnx.export(
    model,
    dummy_input,  # (cls, cv_stats_or_spatial, exif)
    "PhotoViewer/Assets/Models/ranking_head_v1.onnx",
    opset_version=17,
    input_names=["cls", "cv_input", "exif"],
    output_names=["score", "confidence"]
)
```

### 5.2 C# 推理集成

在 `PhotoViewer/Core/AI/` 新增：
- `RankingHead.cs`：加载 `ranking_head_v1.onnx`，输入 CLS + CV + EXIF → 输出 (score, confidence)
- `AestheticScoreService.cs`：编排完整推理流程（读缓存特征 → 聚合 → MLP → 输出）
- EXIF 向量化逻辑（从 Python 训练 pipeline 移植，log 归一化等）

### 5.3 产品集成

- 相似聚类面板内，每张照片旁显示 score（或 score 排名）
- 低置信度照片标记"需人工确认"
- 推理时相似组内去重：只高亮 Top-1，其余灰显

---

## 6. 实施顺序

```
M1 — 数据准备（§1）:
  1. batch_ingest.py 批量入库 ~2 万张
  2. data_audit.py 统计验证 + 校准 cosine 阈值
  3. build_training_pairs.py 生成训练对
  预计：1-2 天

M2 — 方案 A 训练（§2.1 + §3）:
  1. 实现 train_ranking_head.py（方案 A）
  2. 训练 + 调参
  3. eval_analysis.py 评估
  预计：1-2 天

M3 — 评估与决策（§4）:
  - 达标 → 进 M5
  - 不达标 + 空间信息丢失 → 进 M4
  - 不达标 + 其他原因 → 调数据/损失，回 M2

M4 — 方案 B 训练（§2.2，条件触发）:
  1. 实现方案 B 变体
  2. 训练 + 正则化调参
  3. 对比评估
  预计：1-2 天

M5 — 部署（§5）:
  1. ONNX 导出
  2. C# 推理集成
  3. 端到端验收
  预计：2-3 天
```

总预计：5-10 天（取决于是否需要方案 B + 调参迭代轮数）。

---

## 7. 风险与应对

| 风险 | 等级 | 应对 |
|---|---|---|
| 可比域阈值选错，训练对质量差 | **高** | data_audit 先验证；阈值作为超参数可调 |
| 方案 A 统计聚合丢失关键空间信号 | 中 | 失败分析后切方案 B |
| 方案 B 过拟合（2 万张撑不住 4.5M 参数） | 中 | 强正则化 + 数据增强（pair 内交换顺序 = 2×） |
| 相似度权重过高导致模型只学技术差异忽略美学 | 中 | 调 cosine_weight_floor；观察低相似对的 loss 贡献 |
| 跨天的"同题材"被切断（如连续两天拍同一座山） | 低 | 训练对只在同天内生成，但评估时允许跨天相似组 |
| 孤立佳作（场景 5）得分偏低 | 中 | 无可比对 → 不参与 pairwise 训练 → score 由 CLS 语义决定；若系统性偏低，考虑加绝对头 |
| EXIF 缺失（手动镜头无电子触点） | 低 | EXIF 向量缺失值填 0；MLP 学到忽略 |
| 模型退化为输出平均分（§0.5 陷阱 1） | **高** | 监控 val score 方差；方差趋零 → 停训排查 |

---

## 8. 远期升级（超出本期）

- 绝对美学头（局部可比域内 ordinal regression）
- 邻域聚合 `[h_i; g_i]`（Plan-1 Phase E）
- 上下文 Transformer（远期）
- 在线学习（用户改星级 → 端侧微调）
- 移动端部署（量化 + CoreML/NNAPI）
