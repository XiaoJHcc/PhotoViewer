# DINOv3 选片排序系统 — 落地执行计划

> 状态:草案 v1 / 2026-05-09
> 上游讨论:[相似度处理与模型推理优化](copilot-chat-conversation-相似度处理与模型推理优化.md)

---

## 0. 需求锚点(执行全程锚定 — 每阶段开工前重读)

> 这一章不是项目背景,是**项目宪法**。技术方案怎么变都行,但只要任何阶段的产物违背了这章的任一条目,就是跑偏了。

### 0.1 用户与拍摄习惯

- 摄影爱好者,SONY A7C2,多拍选优工作流。
- 单次外拍 ~600 张/天,人工筛选 ~5 小时(占用大量精力,这是要替代的工作量)。
- 拍摄方式:同地点多角度、多焦距(24/35/85 等)、多构图反复尝试。
- 已有数据资产:**上万张历史照片 + 完整人工星级标注**(0-5 星)。

### 0.2 真实选片流程(标注数据的来源,不可重新打标)

- **滑动窗口比对**:把同时间段/同场景的照片放一起两两比较,不跨场景跨日期。
- **每轮 Top 50% 晋级**:0→1→2→3→4→5 星共 5 轮。
- 隐含假设:本星级和高于本星级的照片数量大致相等(锦标赛结构)。
- 不同层级的人工关注点(既有数据已混合在一起,拆不开):
  - **0→1 星**:孤立照片：清晰度、有效性(不是乱拍的)，多张照片：其中较好的一半。
  - **1→2 星**:开始考虑美学氛围,或在多张 1 星中选优
  - **2→3 星**:雷同内容选优（多张同拍摄点、同主体对象、同角度中最好的一张，也有孤立的、但拥有好氛围、或极具代表性的好照片）
  - **3→4 星**:同题材不同视角选优，同时要求美学价值较高
  - **4→5 星**:全局精品
- **隐含污染**:极相似的照片（像素级差异，甚至连清晰度都完全一致）在人工判断中有可能是随机选其一晋级的,这部分对比是无意义噪声。

### 0.3 典型痛点与场景举例(模型必须有能力应对)

| # | 场景 | 模型必须做到 |
|---|---|---|
| 1 | **同山头不同焦距/构图** — 24mm vs 35mm,偏左 vs 偏右 | 判断"留白呼吸"加分 vs "囊括杂乱树枝"减分 |
| 2 | **同题材长焦梯田** — 风格一致,只是田埂曲线 / 小房子位置不同 | 视觉极度相似中识别决定性细节 |
| 3 | **沿山脊移动** — 前景变了远山没变 | 识别"同题材"可比性,既在训练时高权重也在推理时仔细辨别 |
| 4 | **静物连拍** — 两张几乎一样,人工随机选 | 识别为不可比,排除出训练对,推理时不强行二选一 |
| 5 | **孤立瞬间抓拍** — 氛围/光线极佳但无相似可比 | 不再依赖对比，直接判断美学，避免漏氛围片。需要模型能够理解优秀美学的潜质，如果这张照片有 5 星潜质，则至少给到 3 星。 |
| 6 | **主体 vs 背景的题材化权衡** — 人像要主体锐+背景虚,风光要全清+接受留白，有些题材可实可虚 | 根据题材语义切换权衡,不是统一规则 |
| 7 | **夜景抖动判断** — CMOS 防抖随机失败,部分清晰部分拖影 | 通过 CV 在全图网格采样计算数据，结合 ViT 注意力，重点识别建筑边缘 / 点光源，避开车流/反光等天然变化区，判断照片各处是否该实、该虚 |

**这 7 类场景是验收基线** — 任何阶段的中间产物或最终模型,必须能在每一类的代表样本上给出合理输出。验证集设计(阶段 C.4)直接对齐这 7 类。

### 0.4 核心目标

**模型唯一目标:学习摄影美学。**

- 既要能在两两/多张对比中选优(高相似场景),也要能对孤立照片直接判断美学价值(避免漏氛围片) — **这是同一种能力的两面,不拆任务**。
- **锦标赛逐层 Top 50% 是数据采集方式,不是模型目标**。它是经过我多年人工验证的稳定标注手段,所以星级标签可信;但模型不需要复刻这个流程,模型应该从星级标签反推审美偏好。
- 推理时是否再套滑动窗口 Top 50% 三轮,只是输出包装 — 想要原始美学分就直接读分,想要按工作流分桶就过一遍窗口。两者并不冲突。

**产品形态**:人机协作 — 高置信度自动化,低置信度回人工。

**运行约束(本期)**:**准确性优先**。本期只在 4080 桌面端跑得动即可,内存/算力放开用。iPad / 移动端下放放到远期。

**最低成功门槛**:相似组选优与人工一致率 ≥ 60% + ≥3 星召回率 ≥ 80%(人工只需看 Top 12.5%) + 稳定剔除技术废片。

> **5 小时 → 30 分钟**就是商业意义上的成功,不要被"全自动精确打分"的执念带偏。

### 0.5 必须避免的副作用与陷阱(每阶段验收必查)

| # | 副作用 | 触发原因 | 对策(已写入设计) |
|---|---|---|---|
| 1 | **模型退化为输出平均分** | 跨场景标签自相矛盾时硬做绝对回归 | 优先 Pairwise / 相似度内对比;绝对美学头(若启用)需配合局部可比域约束 |
| 2 | **模型记忆特定场景而非美学** | 随机划分 train/test,同次拍摄跨集 | 严格按拍摄日期/事件划分 |
| 3 | **退化为人工规则集合** | 堆"if 相似度 > X 则扣分"硬规则 | 软权重,数据驱动,不堆规则 |
| 4 | **误把热力图当审美关注点** | 依赖 DINO Attention 截局部 patch | 用 CV 网格全画面采样 |
| 5 | **训练被噪声拉平** | 极相似+星级不同的对参与训练 | cosine ≥ 0.98 标 tie 不入训练集 |
| 6 | **学到粗暴场景偏置** | 跨题材/跨时段两两组对 | 训练对必须满足局部可比域(时间近+embedding 近) |
| 7 | **过早追求统一大模型** | 一次性堆 ViT+CV+EXIF+patch+Transformer | 分阶段递进,先单图融合基线 |
| 8 | **全自动幻想牺牲可用性** | 追求模型完全替代人工 | 人机协作,置信度回人工 |
| 9 | **复刻人工锦标赛流程当成模型目标** | 把"逐层 Top 50%"硬编码进损失函数 | 锦标赛是采集标签的方式,模型目标是学美学,推理时再选要不要套窗口 |

### 0.6 已锁定的设计决策(经多轮讨论收敛,后续不再动摇)

- **不**做绝对 0-5 分类回归(跨场景同星级标签不可信);允许"局部可比域内的绝对美学头"作为远期备选,具体形式由 D 阶段实测后定
- 输入三路并行:**DINOv3 [CLS]** + **CV 网格统计** + **EXIF 向量**
- 训练比较只发生在"局部可比域"(时间近 + embedding 近)
- 极相似(cosine ≥ 0.98)标 tie,不参与排序训练
- 数据集按拍摄日期/事件划分 train/val/test
- 推理时滑动窗口 Top 50% 是输出包装的一种,不是模型目标
- 第一版用**轻量邻域聚合**(`[h_i; g_i]` 拼接 + MLP),**不**上 Transformer
- 输出至少包含**美学得分 + 置信度**两路

### 0.7 不在第一版考虑(已剔除路线,不要回头)

- DINO 热力图定位关键点截局部 patch
- 独立 CNN 抖动/虚焦分支(全部并入 CV 网格)
- 上下文 Transformer(列为远期升级)
- 全局 Top 12.5% 单轮筛选
- 任意两两组合训练对(必须有局部可比约束)
- 强拆任务为"技术分/美学分/去重分"(数据已混合不可拆)
- 一次性追求"完全替代人工"

---

## 1. 本期范围与远期路线

**本期目标:把 DINOv3 摸透,作为下游所有训练的输入基建。** 三件事并行推进:

1. **A1:Python 推理验证** — 4080 上跑通 + 三尺寸 t-SNE 对比(技术决策依据)
2. **A2:全平台端侧打包跑通** — 用一个最小尺寸模型走通 ONNX 导出 → C# 接入 → 四平台部署的完整链路;同时把"基于 DINO 特征的相似聚类"接到 [SimilarityPanelViewModel](../PhotoViewer/ViewModels/Main/File/SimilarityPanelViewModel.cs),取代当前占位实现 — **这本身就是一种可视化验证:相似聚类靠不靠谱,肉眼一看便知**
3. **A3:CV 网格采样设计** — 不写代码,先设计好"每格输出哪些标量、为什么这些对 AI 选片有用",验收物是设计文档与小样本 PoC

A1/A2/A3 完成后进入 B 阶段(数据工程批量入库)。

```
A1: Python 推理验证(4080)        ─ 技术决策:用哪个尺寸 / DINOv3 vs v2
A2: 端侧打包 + 相似聚类落地(四平台) ─ 技术验证 + 可视化验收 + 替换占位
A3: CV 网格采样设计               ─ 设计文档 + 小样本 PoC,不写生产代码
─────────────────────────────────────────────────
远期(待 A 后规划):
B:  数据工程批入库特征
C:  训练对/训练样本生成
D:  基线模型训练(线性头/MLP)
E:  邻域聚合增强(条件触发)
F:  端侧部署最终模型(若 A2 走通,F 大部分基建已就绪)
```

每个远期阶段的具体参数、模型结构、训练目标,都根据上一阶段的结果重新评估,避免提前设计被推翻。

---

## 阶段 A1:Python 推理验证(4080 桌面)

### A1.1 目的

1. **跑通 DINOv3 本地推理链路**:加载、提特征、可视化。
2. **验证 DINOv3 对你的摄影数据是否敏感**:t-SNE 看相似组能否自然聚簇、跨题材能否分离。
3. **拿到真实性能数据**,为下游全量提取与端侧部署做尺寸决策。

如果它无法把"相似但星级不同"的照片分开,后面所有架构都白搭 — A1 失败立即停止,切 DINOv2 重试或重新评估方案。

### A1.2 模型尺寸选型与性能预估

#### 不同尺寸之间能否直接代换?

**不能直接代换** — 不同尺寸的输出维度不同(S:384 / B:768 / L:1024 / H+:1536),下游 MLP 首层是 `Linear(feature_dim, ...)`,换 backbone 必须重训这一层。

**但代价很小**:
- 架构不变,只是首层宽度变,代码不动
- 数据 pipeline 不动,重新跑一次特征提取入库即可(4080 上 3-25 分钟,特征向量缓存进 `photos.db`,训练时不重跑 ViT)
- MLP 本身参数量小,几分钟训完

实际成本 ≈ 一次提取 + 一次重训 ≈ 半小时。**所以本期不必为"未来能不能换"焦虑** — 先选合适的,后期想升降都行。

唯一需要重新校准的是 **cosine 相似度阈值**(如标 tie 的 0.98)在不同特征空间里的语义可能不同,但这是 C 阶段的事。

#### 性能预估

输入 518×518、FP16、batch 16,4080 上的预估(基于 timm/HuggingFace 类似模型公开基准外推,**实测会有 ±30% 波动**):

| 变体 | 参数量 | 特征维度 | 单张 ms | 600 张 | 万张全量 | 显存 |
|---|---|---|---|---|---|---|
| ViT-S/16 | 21M | 384 | ~5 | ~5s | ~3min | <2GB |
| ViT-B/16 | 86M | 768 | ~10 | ~10s | ~5min | ~4GB |
| **ViT-L/16(4080 主选)** | **300M** | **1024** | **~30** | **~25s** | **~10min** | **~8GB** |
| ViT-H+/16 | 840M | 1536 | ~80 | ~70s | ~25min | ~13-14GB |
| ViT-7B | 6.7B | 4096 | OOM | - | - | >16GB |

#### 选型决策:ViT-L/16 主选(4080)+ ViT-S/16 备选(端侧打包)

本期"准确性优先"且 4080 有 16GB 显存,合理上限是 ViT-L/16。**为什么不上 H+/7B**:
- ViT-7B FP16 ~14GB weights + 激活,4080 装不下(必须放弃)
- ViT-H+ 显存吃紧、迭代慢(单次提取 25min),且 L→H+ 在我们的任务(全局 [CLS] 美学判断)上**边际递减明显** — DINOv3 paper 的 retrieval 基准里 H+ 比 L 通常只有 1-2 个百分点提升
- ViT-L 是 DINOv3 在生产部署里公认的 "sweet spot"

**为什么不退到 B**:B→L 在细粒度判别任务上还有可观提升(尤其"田埂曲线/边缘树枝"这种决定性细节);4080 跑 L 完全有富余,没必要省。

**A2 端侧打包另用 ViT-S/16**:端侧用 ViT-L 太重(尤其移动端),打通流程用 S 即可,等 D 阶段训练完线性头再回头评估端侧用哪个尺寸。**S 不参与 4080 训练决策,只用于 A2 把链路跑通**。

**License 备选**:`facebook/dinov2-large` / `facebook/dinov2-small`(Apache 2.0)架构兼容,`MODEL_ID` 一行替换。

#### 阶段 A1 实操:三尺寸对比验证

阶段 A1 同时跑 ViT-S / ViT-B / ViT-L 各一份(在同一份验证样本上提特征做 t-SNE),目的:
1. 看 S → B → L 的聚类质量是否真的递增
2. 如果 L 比 B 没有明显提升 → 落地用 B,留 L 作为远期升级
3. 如果连 S 都已经聚得很好 → A2 端侧也直接走 S,远期不必蒸馏

t-SNE 是廉价的(几十张样本),三个尺寸各跑一遍总耗时 < 30 分钟,用真实数据替代纸面推测,避免选大了浪费、选小了重做。

### A1.3 环境准备

```bash
# 在项目外建独立 Python 环境(避免污染 .NET 工程)
mkdir -p D:/AI/photo-ranking
cd D:/AI/photo-ranking
python -m venv .venv
.venv/Scripts/activate

# 安装最小依赖
pip install torch torchvision --index-url https://download.pytorch.org/whl/cu121
pip install transformers pillow numpy scikit-learn umap-learn matplotlib
```

DINOv3 需要 `transformers >= 4.45`(2025-08 后)。

### A1.4 加载模型

```python
from transformers import AutoModel, AutoImageProcessor

# 主选
MODEL_ID = "facebook/dinov3-vitl16-pretrain-lvd1689m"
# 对照组(同时跑做 t-SNE 对比)
# MODEL_ID = "facebook/dinov3-vitb16-pretrain-lvd1689m"
# MODEL_ID = "facebook/dinov3-vits16-pretrain-lvd1689m"
# License 备选
# MODEL_ID = "facebook/dinov2-large"

processor = AutoImageProcessor.from_pretrained(MODEL_ID)
model = AutoModel.from_pretrained(MODEL_ID, torch_dtype=torch.float16).eval().cuda()
```

### A1.5 提特征 + t-SNE 可视化(关键验收点)

从你的库里挑 **5 组近重复组**(每组 5-8 张同机位/连拍照片)+ **20 张跨题材孤立照片**,共约 60 张。

```python
# 伪代码
feats = []
for img_path in samples:
    img = Image.open(img_path).convert("RGB")
    inputs = processor(images=img, return_tensors="pt").to("cuda", dtype=torch.float16)
    with torch.no_grad():
        out = model(**inputs)
    cls = out.last_hidden_state[:, 0, :].float().cpu().numpy()  # [1, 1024] (L) / [1, 768] (B) / [1, 384] (S)
    feats.append(cls)

# t-SNE 降维 + 染色按"组别"
from sklearn.manifold import TSNE
emb = TSNE(n_components=2, perplexity=10).fit_transform(np.vstack(feats))
plt.scatter(emb[:, 0], emb[:, 1], c=group_ids, cmap="tab20")
```

### A1.6 验收标准

- ✅ **同组照片**在 2D 平面上**自然聚成簇**(目测一眼可分)
- ✅ **跨题材孤立照片**与各组明显分离
- ✅ 同组内,星级越高的照片**不需要**自动靠某一边(这是后续线性头学习的事,不是基座模型的责任)
- ✅ 实测 ViT-L/16 单张 ≤ 60ms 且显存稳定 < 12GB(留余量),否则评估降级到 B
- ✅ S → B → L 三尺寸的 t-SNE 对比图归档,作为 A2 端侧选型与 D 阶段训练选型的依据

如果同组聚类都做不到 → 切换 ViT-B/S 或 DINOv2 重测;再不行就要重新评估方案。

### A1.7 配套小工具(留在仓库)

在 `Tools/` 下新增 `Tools/dinov3_feature_probe.py`,接受 `--folder` 与 `--model-id` 参数,输出:
- 每张图的特征向量(`.npy`)
- t-SNE 可视化 PNG
- 单张推理耗时统计(均值 / P95)

---

## 阶段 A2:端侧打包 + 相似聚类落地(四平台)

### A2.1 目的

A2 不追求最终模型质量,只验证**完整工程链路是否打通**,并顺手把"基于 DINO 特征的相似聚类"接到现有 UI,**用肉眼可见的聚类质量做 A1 验证的双重保险**。

三件事:
1. **DINOv3 ONNX 导出与四平台运行验证** — Windows / macOS / Android / iOS 各自启动后能加载模型并提特征
2. **替换 [SimilarityService](../PhotoViewer/Core/Similarity/SimilarityService.cs) 占位** — 把当前的"按拍摄时间差模拟分数"换成"基于 [CLS] 特征的余弦相似度",[SimilarityPanelViewModel](../PhotoViewer/ViewModels/Main/File/SimilarityPanelViewModel.cs) 自然显示真实相似聚类
3. **可视化验收 A1 结论** — 在你的真实照片库里,看相似面板里聚到一起的照片是否真的相似;t-SNE 是统计图,这里是终端用户视角的检验

### A2.2 模型选择:用 ViT-S/16

A2 端侧用 **ViT-S/16**(`facebook/dinov3-vits16-pretrain-lvd1689m`):
- 移动端必须考虑算力 / 包体积 / 内存
- A2 目标是打通链路,不是最终性能
- ViT-S 特征质量在 A1 已经验证(同组聚类做得到就够用)
- D 阶段训练完线性头后再回头评估端侧到底用 S 还是 B,A2 不下决策

模型文件目标体积:S/16 FP16 ONNX ≈ 45MB。Android/iOS 可接受。

### A2.3 ONNX 导出

```python
# Tools/export_dinov3_onnx.py
import torch
from transformers import AutoModel

MODEL_ID = "facebook/dinov3-vits16-pretrain-lvd1689m"
model = AutoModel.from_pretrained(MODEL_ID).eval()

# 仅取 [CLS] token,简化导出图
class DinoFeatureWrapper(torch.nn.Module):
    def __init__(self, m): super().__init__(); self.m = m
    def forward(self, x):
        return self.m(pixel_values=x).last_hidden_state[:, 0, :]  # [B, 384]

dummy = torch.randn(1, 3, 518, 518)
torch.onnx.export(
    DinoFeatureWrapper(model), dummy,
    "PhotoViewer/Resources/Models/dinov3_vits16.onnx",
    opset_version=17,
    input_names=["pixel_values"],
    output_names=["cls_embedding"],
    dynamic_axes={"pixel_values": {0: "batch"}}
)
```

**验收**:用 onnxruntime 加载后,与 PyTorch 输出 cosine 相似度 ≥ 0.999(逐张对比 100 张)。

### A2.4 C# 接入(Avalonia)

```
PhotoViewer/Core/Similarity/
├── SimilarityService.cs        ← 改造:替换占位
└── DinoFeatureExtractor.cs     ← 新增:封装 ONNX Runtime 调用
```

**关键设计**:
- `DinoFeatureExtractor` 是平台门面,内部按平台选 EP(Windows DirectML / macOS+iOS CoreML / Android NNAPI / 兜底 CPU)
- 特征提取**只在需要时按需触发**,提取后写入 [PhotoDatabase](../PhotoViewer/Core/Database/PhotoDatabase.cs)`.feature_vector`(已经预留好的列),后续命中缓存
- `SimilarityService.FindSimilarAsync` 改用 `feature_vector` 算 cosine,不再用拍摄时间差(时间差仅作为 tiebreaker 保留)

NuGet:
- `Microsoft.ML.OnnxRuntime`(桌面)
- `Microsoft.ML.OnnxRuntime.Managed` + 平台 EP NuGet(移动)

模型文件放 `PhotoViewer/Resources/Models/dinov3_vits16.onnx`,作为 Content + CopyToOutputDirectory。

### A2.5 四平台验收

| 平台 | 验证项 | 启动方式 |
|---|---|---|
| Windows | 单张提取 ≤ 100ms,DirectML EP 启用 | VS Code Task `Debug Windows` |
| macOS | 单张提取 ≤ 100ms,CoreML EP 启用 | VS Code Task `Debug Mac` |
| Android | 单张提取 ≤ 300ms,NNAPI EP 启用,APK 增量 < 60MB | `Debug Android` |
| iOS | 单张提取 ≤ 300ms,CoreML EP 启用 | `Debug iOS` |

**功能验收**:
- 打开任意一个文件夹,选中一张照片,相似面板能显示真实相似聚类(肉眼判断:同机位/同题材/连拍能聚到一起,跨题材不会乱入)
- 同次曝光的 RAW/JPG/HEIF 三联,基于 [PhotoFingerprint](../PhotoViewer/Core/Database/PhotoFingerprint.cs) 共享特征向量,只算一次

**A2 不要做**:
- 排序头 / 美学打分(那是 D 阶段的事)
- CV 网格特征接入(A3 设计完才做)
- EXIF 向量化(D 阶段才用)
- 性能极致优化(打通即可)

### A2.6 配套工具

- `Tools/export_dinov3_onnx.py` — A2.3 的导出脚本
- `Tools/verify_onnx_parity.py` — 100 张 PyTorch vs ONNX 对齐校验
- 平台部署沿用 [DEV.md](../DEV.md) 的 VS Code Tasks,不引入新 build 系统

---

## 阶段 A3:CV 网格采样设计(只设计,不写生产代码)

### A3.1 目的

A3 是为 D 阶段做基建的**设计任务**。盲目实现"每格算 Laplacian 方差"得到的是垃圾向量 — 维度大、信息冗余、AI 学不到东西。需要先想清楚:

1. **每个格子要让 AI 学到什么具体能力?**(对应 §0.3 的 7 类场景)
2. **每个格子需要哪些标量,这些标量在不同题材下是否仍有判别意义?**
3. **网格密度如何选?**(过粗丢主体细节,过细噪声大 + 维度爆炸)
4. **如何归一化?**(不同分辨率/曝光下的同一物理量必须可比)

A3 输出:**设计文档 + 小样本 PoC**,不接入主项目。设计冻结后才进入 D 阶段实现。

### A3.2 必须回答的设计问题(逐条对齐 §0.3 场景)

| 场景 | CV 网格需要让 AI 看到什么 |
|---|---|
| **场景 6:主体清/背景虚的题材化判断** | 需要"清晰度图" — 每格清晰度,AI 才能结合 ViT 语义判断"该格该清还是该虚" |
| **场景 7:夜景旋转抖动** | 需要"高频锐度图 + 边缘各向异性" — 拖影会让点光源边缘沿一个方向被拉长,各向同性的高频(纹理)和各向异性的高频(拖影)必须可区分 |
| **场景 1:留白 vs 杂乱树枝** | 需要"边缘密度图 + 局部对比度图" — 留白区域低密度 = 加分,杂乱区域高密度但无主体 = 减分,语义判断仍由 ViT 给 |
| **场景 4 / 静物连拍** | CV 网格本身不解决可比性,但若两张连拍 CV 网格几乎一致,可作为 tie 检测的辅助证据 |

注意:**CV 网格不直接做语义判断**(那是 ViT 的工作),它提供"该位置的物理质量"客观指标,让 D 阶段的 MLP 有能力交叉验证 ViT 的语义。

### A3.3 候选标量与设计权衡

每格候选标量(具体保留哪几个由 PoC 决定):

| 标量 | 物理意义 | 解决场景 |
|---|---|---|
| **Laplacian 方差** | 二阶导数能量,清晰度黄金指标 | 6, 7 |
| **Sobel 梯度幅度均值** | 一阶导数,边缘密度 | 1, 7 |
| **梯度方向直方图熵** | 边缘各向异性(低熵=方向集中=可能拖影) | **7(关键)** |
| **高频能量占比** | FFT 后 cutoff 以上能量 / 总能量 | 6, 7(与 Laplacian 重复度高,可能二选一) |
| **平均亮度** | 曝光分布 | 1 |
| **亮度标准差** | 局部对比度,留白区低 | 1 |

**网格密度候选**:8×8 / 12×12 / 16×16。需要 PoC 确认在你的真实照片上哪个最合适。

**归一化策略**:
- 必须在**等效分辨率**(比如长边 2000px)下算,避免相机原生像素数差异引入偏置
- 每个标量的尺度差异巨大(Laplacian 方差能从 10 跳到 10000),需要在数据集层面做 z-score 或 percentile 归一化,而不是单图归一化(单图归一化会丢绝对差异:全图清晰 vs 全图糊片)

### A3.4 PoC 实操

**输入**:从你的库挑 30-50 张代表样本,按 §0.3 七类场景各取数张,标好"哪些区域应该锐 / 应该虚 / 应该不予考虑"。

**输出**:
- 一个 Jupyter notebook,可视化每张图在 8×8 / 12×12 / 16×16 三种密度下的各项标量热力图
- 主观评估表:每个标量在每类场景下是否真的"看得出来该锐还是该虚"

**决策依据**:
- 哪些标量在所有场景下都低判别力 → 砍掉
- 哪种网格密度的可视化最贴近人眼直觉 → 保留
- 哪些场景仍无解(例如纯抓拍人像) → 记入"CV 网格能力边界",由 ViT 兜底

### A3.5 验收标准

- A3 设计文档完成,明确"D 阶段 CV 特征向量 = 网格密度 N×N × 每格 K 个标量 = M 维",有具体数字
- PoC notebook 在 §0.3 七类场景代表样本上,**至少 5 类**可视化热力图与人眼直觉一致
- 与 A1 对接点明确:CV 网格特征作为输入向量的一部分,与 DINOv3 [CLS] 拼接后送入 D 阶段 MLP

A3 完成后,B 阶段才能开始**真正意义上的批量入库**(否则现在就批一遍,后面发现网格设计要改还要重来)。

### A3.6 配套工具

- `Tools/cv_grid_design.ipynb` — A3.4 的设计 PoC notebook
- `Tools/cv_grid_extract.py` — 设计冻结后的最终提取实现(B 阶段使用)

---

## 远期阶段大纲(待 A 后逐段重新规划)

### 阶段 B:数据工程基建

复用现有基建([PhotoDatabase](../PhotoViewer/Core/Database/PhotoDatabase.cs) + [PhotoFingerprint](../PhotoViewer/Core/Database/PhotoFingerprint.cs) 已写好,`feature_vector` BLOB 列已预留)。批量给万张照片提特征入库,同步算 A3 设计冻结的 CV 网格统计。EXIF 向量化复用 `SonyMakernoteParser` 已解析的对焦点。

**待 A 完成后定**:确定模型尺寸(由 A1 决定)、批大小、是否需要 ALTER TABLE 加 `cv_grid` 列(由 A3 决定向量长度)。

### 阶段 C:训练对/训练样本生成

构造训练样本时必须满足"局部可比域"约束(时间近 + embedding 相似度 ≥ 0.5),极相似(≥ 0.98)标 tie 不入训练集。数据集按拍摄日期/事件划分 train/val/test,绝不随机划分。

**待 B 完成后定**:具体相似度阈值、时间窗口长度、软权重函数 — 都要在拿到真实特征分布之后才能定。

### 阶段 D:线性头/MLP 训练

输入三路并行(DINOv3 特征 + CV 网格 + EXIF),冻结 backbone 只训上层 MLP。损失函数与输出头形式(纯 Pairwise / Pairwise+绝对美学双头 / 仅绝对美学)**根据 A-C 阶段实际特征质量与训练对分布重新评估**。

**待 C 完成后定**:单头 vs 双头、损失加权、是否需要邻域聚合增强(阶段 E)。

### 阶段 E:邻域聚合增强(条件触发)

仅当 D 基线指标不达标或孤立佳作召回明显偏低时启用。轻量 `[h_i; g_i]` 拼接 + MLP,不上 Transformer。

### 阶段 F:端侧导出与集成

A2 已经把链路打通,F 主要是把 D 阶段训出的最终模型(backbone + 线性头)导出 ONNX 替换 A2 的占位模型,接入完整的"美学打分 + 置信度"输出。本期不规划。

---

## 2. 关键风险与应对(全期)

| 风险 | 等级 | 触发阶段 | 应对 |
|---|---|---|---|
| DINOv3 license 不可接受 | 中 | A1 | 切 DINOv2-Large(架构不变,Apache 2.0,特征维度同为 1024) |
| ViT 特征对你的数据不够敏感 | **高** | A1 | t-SNE 验证就能发现,A1 失败立即停止 |
| 4080 上 ViT-L 显存/速度超预期 | 低 | A1 | 降回 ViT-B(特征维度 1024→768,MLP 首层重训,成本 ~30 分钟) |
| ONNX 算子在 CoreML/NNAPI 上不兼容 | **中** | A2 | 退回 CPU EP(慢但能用);或换 ConvNeXt-Small 变体(算子兼容性好) |
| 移动端 APK/IPA 包体积膨胀 | 中 | A2 | 量化(动态 INT8 通常无质量损失);极端可下载分发模型 |
| CV 网格设计错位,AI 学不到 | **中** | A3 | A3 不冻结设计,B 不开工(用 PoC 强制对齐) |
| 训练对噪声把模型拉平 | 高 | C-D | 严格 tie 过滤 + 软权重 + 局部可比域(具体阈值待 C 阶段定) |
| 按日期划分后训练集太小 | 中 | C | 数据增强(色彩抖动 + 轻微裁剪) |
| 模型记住场景而非美学 | **高** | 所有 | 按日期划分是唯一防线,严格执行 |
| HEIF/RAW 格式 CV 计算异常 | 中 | B | 统一解码到 8bit RGB 再算,复用 LibHeif 链路 |

---

## 3. 与现有架构的对接清单

| 文件/模块 | 改动类型 | 阶段 |
|---|---|---|
| [PhotoDatabase.cs](../PhotoViewer/Core/Database/PhotoDatabase.cs) | 远期可能加 `cv_grid` 列(向量长度由 A3 定) | B |
| [PhotoFingerprint.cs](../PhotoViewer/Core/Database/PhotoFingerprint.cs) | 不动 — 已经够用 | - |
| [SimilarityService.cs](../PhotoViewer/Core/Similarity/SimilarityService.cs) | **本期改造**:替换占位为基于 [CLS] 的余弦相似 | **A2** |
| [SimilarityPanelViewModel.cs](../PhotoViewer/ViewModels/Main/File/SimilarityPanelViewModel.cs) | **本期填充**:接入真实相似聚类 | **A2** |
| `PhotoViewer/Core/Similarity/DinoFeatureExtractor.cs` | **本期新增**:ONNX Runtime 平台门面 | **A2** |
| `PhotoViewer/Resources/Models/dinov3_vits16.onnx` | **本期新增**:S/16 端侧模型 | **A2** |
| `Tools/dinov3_feature_probe.py` | **本期新增**:A1 的三尺寸 t-SNE 验证 | **A1** |
| `Tools/export_dinov3_onnx.py` | **本期新增**:A2 的 ONNX 导出 | **A2** |
| `Tools/verify_onnx_parity.py` | **本期新增**:PyTorch vs ONNX 对齐校验 | **A2** |
| `Tools/cv_grid_design.ipynb` | **本期新增**:A3 的设计 PoC | **A3** |
| `Tools/cv_grid_extract.py` | 设计冻结后新增(B 阶段使用) | A3→B |
| `Tools/dinov3_batch_extract.py` | 远期新增 | B |
| `Tools/build_training_pairs.py` | 远期新增 | C |
| `Tools/train_ranking_head.py` | 远期新增 | D |

---

## 4. 远期升级(超出本计划骨架)

- Contextual Transformer(2-4 层小型注意力)替代邻域聚合
- 在线学习:用户手改星级 → 端侧轻量微调
- 多尺度 patch token 利用(DINOv3 dense feature 比 v2 强很多)
- iPad / 移动端下放(量化 + CoreML/NNAPI)
- 跨用户审美迁移
