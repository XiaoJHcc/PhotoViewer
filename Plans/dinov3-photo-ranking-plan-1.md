# DINOv3 选片排序系统 — 落地执行计划

> 状态:草案 v1.4 / 2026-05-10
> **封板注记(2026-07-19)**:本文件为一/二期历史档案,考古专用。文中"锁定/新增"的设计(16×16 网格、3 层金字塔、5 标量、patch 压缩等)多处已被二期演进推翻,现行基建状态以根 `CLAUDE.md` §5.4 + [plan-2-1 wrapup §0.2 与 §4 墓碑](dinov3-photo-ranking-plan-2-1-wrapup.md) 为准;三期基准见 [Training/plans/dinov3-photo-ranking-plan-3-0-charter.md](../Training/plans/dinov3-photo-ranking-plan-3-0-charter.md)。
> 上游讨论:[相似度处理与模型推理优化](copilot-chat-conversation-相似度处理与模型推理优化.md)
> v1.4 变更(执行视角澄清,解除三处隐含假设):
> - **CV 网格 + patch token 是全平台推理时的美学评分输入,不是 dev-only 离线批**;每张照片首次展示前必须算,结果进 cache。原 §3 对接清单里只有 Python 工具,补 C# 运行时 `CvGridExtractor`
> - **CV 分两期落地**:一期 C# 纯托管实现框架 + 可直接实现的标量(Laplacian 方差 / Sobel 幅度均值 / 梯度方向熵 / 平均亮度 / 亮度标准差);二期按需补 FFT 高频能量占比等需要额外依赖的标量。Python `cv_grid_extract.py` 降级为 "桌面 4080 批处理加速路径",与 C# 实现共享 schema 可互操作
> - **Patch token ONNX 导出提前到 A2-M1**:wrapper 双输出(CLS + patch),零包体积成本。M1 的相似聚类仍只消费 CLS,patch 端口闲置到 B 阶段;避免 B 阶段重新部署四平台 ONNX 的折腾
> - **调度策略(进入文件夹后台预跑 / 移动端 WiFi/充电开关)暂不规划**,待 B 阶段实测提取耗时后再定
> v1.3 变更:
> - **多模型并存**:CLS 从 `photos` 单列搬到 `photo_features` 纵表,`photo_patches` 改为 `(fingerprint, model_id)` 联合主键;同一指纹可同时存 S/B/L 多个模型特征,读/写强制 `WHERE model_id=...`,不做跨模型 fallback
> - **网格统一锁 16×16 零插值**:CV 网格放弃 8/12 候选,与 patch 下采样目标统一到 16×16(32→16 或 64→16 均为整数倍 avg pool),每格 ~375×375 原图像素,融合一一对应
> - 历史 `photos.feature_vector` / `feature_model` 列在 A3 冻结时一次性迁移到 `photo_features`,老列标 deprecated(不立即 DROP,保留数据)
> v1.2 变更:
> - §0.6 三路输入扩为 **[CLS] + patch token + CV 网格 + EXIF**,patch token 作为 CV 网格的语义加权来源;两者都入库、分表存储
> - A1 新增 **ViT-L @ 518 vs @ 1024** 对照实验,对齐场景 2 的细节判别
> - A3 重写:从"纯 CV 网格设计"扩为"CV 网格 + patch token 融合设计",新增 A3.4 patch token 存储与压缩设计(INT8 + 空间下采样 16×16)
> - A3.3 删除"长边 2000 下采样",改为**相机原生分辨率 + 3 层金字塔 + 跨尺度聚合**(2000 下采样对高频敏感标量是有害的低通滤波)
> - 新建独立表 `photo_patches`(~96KB/张,万张 ~1GB);`photos` 加 `cv_grid` / `cv_grid_spec` 列
> - B 阶段入口明确:A3 冻结后一次 ALTER,`DinoFeatureExtractor` / `DinoFeatureCache` 扩展 patch 输出
> - v1.1 变更:AI 代码路径统一到 `PhotoViewer/Core/AI/`,老 `PhotoViewer/Core/Similarity/` 占位实现完全废弃;A2 拆成 M1(打包运行)→ M2(UI 接入)两个先后里程碑
> v1.2 变更:
> - §0.6 三路输入扩为 **[CLS] + patch token + CV 网格 + EXIF**,patch token 作为 CV 网格的语义加权来源;两者都入库、分表存储
> - A1 新增 **ViT-L @ 518 vs @ 1024** 对照实验,对齐场景 2 的细节判别
> - A3 重写:从"纯 CV 网格设计"扩为"CV 网格 + patch token 融合设计",新增 A3.4 patch token 存储与压缩设计(INT8 + 空间下采样 16×16)
> - A3.3 删除"长边 2000 下采样",改为**相机原生分辨率 + 3 层金字塔 + 跨尺度聚合**(2000 下采样对高频敏感标量是有害的低通滤波)
> - 新建独立表 `photo_patches`(~96KB/张,万张 ~1GB);`photos` 加 `cv_grid` / `cv_grid_spec` 列
> - B 阶段入口明确:A3 冻结后一次 ALTER,`DinoFeatureExtractor` / `DinoFeatureCache` 扩展 patch 输出
> - v1.1 变更:AI 代码路径统一到 `PhotoViewer/Core/AI/`,老 `PhotoViewer/Core/Similarity/` 占位实现完全废弃;A2 拆成 M1(打包运行)→ M2(UI 接入)两个先后里程碑

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
- 输入三路并行:**DINOv3 [CLS] + patch token** + **CV 网格统计** + **EXIF 向量**
  - **[CLS]** 做全局语义/相似度检索/跨图对比
  - **patch token** 做每格"是什么"的语义先验,作为 CV 网格的**加权来源**(主体格重、背景格轻),让"该位置该锐还是该虚"这类题材化判断(场景 6)有数据依据而不是靠硬规则
  - 两者在提取阶段一次前向搞定,**都入库、分表存储**(CLS 落在 `photo_features` 纵表,patch 落在 `photo_patches` 独立表)
- **多模型并存的存储纪律**:
  - CLS/patch 的存储键都是 `(fingerprint, model_id)`;同一指纹可同时存 S/B/L 等多个模型特征,互不覆盖
  - 每次查询/推理强制 `WHERE model_id = <当前模型>`,**不做跨模型自动 fallback**(例如"L 没算过就用 S 顶上")— 跨模型 embedding 空间不可比,混用 = 隐蔽错误
  - App 在任一时刻只用一个 `model_id`;4080 dev 端批量跑 L,端侧跑 S,两套特征物理可共存于同库但读时严格隔离
- CV 网格(纯 opencv 物理量,与模型无关)保留在 `photos.cv_grid` 列,不按模型分叉
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

1. **A1:Python 推理验证** — 4080 上跑通 + 三尺寸 × 双分辨率(518/1024)t-SNE 对比(技术决策依据)
2. **A2:全平台端侧打包跑通** — 用一个最小尺寸模型走通 ONNX 导出 → C# 接入 → 四平台部署的完整链路;全部落在新目录 [PhotoViewer/Core/AI/](../PhotoViewer/Core/AI/),老的 [PhotoViewer/Core/Similarity/](../PhotoViewer/Core/Similarity/) 占位实现整体废弃,不参考、不兼容
3. **A3:CV 网格 + DINO patch token 融合设计 + CV 一期 C# 骨架** — 设计 PoC(notebook)冻结融合策略;同时落地 CV 一期 C# 纯托管运行时(Laplacian 方差 / Sobel 幅度均值 / 梯度方向熵 / 平均亮度 / 亮度标准差)作为全平台美学评分输入基建。CV 二期(FFT 高频占比等需额外依赖的标量)推到 B 阶段

A2 分两个先后里程碑:**M1 打包运行跑通**(ONNX 导出 + 四平台 C# 接入 + 最小命令行自检),**M2 UI 接入**(把基于 DINO 特征的相似聚类接到 [SimilarityPanelViewModel](../PhotoViewer/ViewModels/Main/File/SimilarityPanelViewModel.cs))。**M1 不动 UI,M2 才上 UI** — 这样 M1 的打包与运行问题暴露在最小 surface 上,不被 UI 问题干扰。

A1/A2/A3 完成后进入 B 阶段(数据工程批量入库:CLS + patch token + CV 网格三路一次提取)。

```
A1: Python 推理验证(4080)              ─ 技术决策:用哪个尺寸 / DINOv3 vs v2 / 518 vs 1024
A2-M1: ONNX 导出 + 四平台 C# 运行自检   ─ 打通打包与运行链路;ONNX 双输出(CLS+patch),M1 只消费 CLS
A2-M2: 相似聚类接 UI(替换老占位)      ─ 用户视角验收 + 废弃 Core/Similarity/
A3: CV 网格 + patch token 融合设计     ─ 设计文档 + 小样本 PoC + CV 一期 C# 运行时骨架,不写评分逻辑
─────────────────────────────────────────────────
远期(待 A 后规划):
B:  数据工程批入库三路特征(CLS + patch + CV 网格),按 A3 冻结 schema 一次 ALTER;CV 二期补齐需要额外依赖的标量
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

#### 阶段 A1 实操:三尺寸 + 双分辨率对比验证

阶段 A1 跑两组对比,结果合并决策:

**组 1 — 三尺寸 @ 518**:ViT-S / ViT-B / ViT-L 各一份在同一份验证样本上提 [CLS] 特征做 t-SNE。目的:
1. 看 S → B → L 的聚类质量是否真的递增
2. 如果 L 比 B 没有明显提升 → 落地用 B,留 L 作为远期升级
3. 如果连 S 都已经聚得很好 → A2 端侧也直接走 S,远期不必蒸馏

**组 2 — ViT-L @ 518 vs @ 1024**(针对场景 2 的细节判别):
- ViT-S/16 @ 518 的 patch 覆盖 ≈ 原图 185×185px(按长边 6000 反推),田埂曲线/边缘树枝这类 ≤185px 决定性细节会被 patch 粒度吃掉
- @ 1024 时 patch 覆盖 ≈ 原图 94×94px,细节判别力翻倍,代价是 token 数从 ~1025 涨到 ~4097,显存/时延同步翻倍
- 验证方式:在 §0.3 场景 2 代表样本(同题材长焦梯田、田埂曲线细节差异)上对比两种输入下的 cosine 相似度区分度 — 若 518 已经能把"人眼可分的两张"分开,则 518 够用;若区分度在 1024 下明显更大,则 B 阶段批入库要用 1024(只增显存,不增存储,因为 [CLS] 维度不变)
- DINOv3 支持 ≠ 518 的输入尺寸(patch 大小固定 16,token 数随输入边长平方变化)

两组实验都是廉价的(几十张样本,4080 上每尺寸 <5 分钟),用真实数据替代纸面推测,避免选大了浪费、选小了重做。

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
- ✅ 实测 ViT-L/16 @ 518 单张 ≤ 60ms 且显存稳定 < 12GB(留余量),否则评估降级到 B
- ✅ S → B → L 三尺寸 @ 518 的 t-SNE 对比图归档,作为 A2 端侧选型与 D 阶段训练选型的依据
- ✅ ViT-L @ 518 vs @ 1024 在场景 2 代表样本上的 cosine 区分度对比图归档,**决策 B 阶段批入库采用的输入尺寸**
- ✅ 若 1024 确实更优,实测 ViT-L @ 1024 单张 ≤ 200ms 且显存 ≤ 14GB,否则回退 518 + 上 B 或 L

如果同组聚类都做不到 → 切换 ViT-B/S 或 DINOv2 重测;再不行就要重新评估方案。

### A1.7 配套小工具(留在仓库)

在 `Tools/` 下新增 `Tools/dinov3_feature_probe.py`,接受 `--folder` / `--model-id` / `--input-size`(默认 518,可传 1024 做高分辨率对比)参数,输出:
- 每张图的特征向量(`.npy`)
- t-SNE 可视化 PNG
- 单张推理耗时统计(均值 / P95)
- 若一次跑多 `--model-id` / `--input-size`,汇总 CSV 存到 `Tools/probe_runs/` 供跨实验对比

---

## 阶段 A2:端侧打包 + 相似聚类落地(四平台)

> **总基调**:所有 AI 相关代码落到新目录 [PhotoViewer/Core/AI/](../PhotoViewer/Core/AI/)。老 [PhotoViewer/Core/Similarity/](../PhotoViewer/Core/Similarity/) 目录及 `SimilarityService` 占位实现**整体废弃**,M2 完成后删除,不做兼容、不做迁移、不参考其 API 形状。
>
> A2 分两个先后里程碑,**M1 先行、M2 后接**:
> - **M1 — 打包与运行跑通**:ONNX 导出 + C# 特征提取器 + 四平台启动后能加载模型并对一张测试图产出 [CLS] 特征向量。**不动 UI**。
> - **M2 — UI 接入相似聚类**:用 M1 的特征提取器,新建 `Core/AI/` 下的相似聚类服务,接到 [SimilarityPanelViewModel](../PhotoViewer/ViewModels/Main/File/SimilarityPanelViewModel.cs);同步删除老 `Core/Similarity/`。

### A2.1 目的

A2 不追求最终模型质量,只验证**完整工程链路是否打通**,并顺手把"基于 DINO 特征的相似聚类"作为用户视角验收手段接到现有 UI。

分里程碑的理由:
1. 打包与运行问题(ONNX 算子兼容、EP 选型、包体积、签名、native lib)要暴露在最小 surface 上,不被 UI 状态机、事件时序问题盖过
2. UI 接入是一个独立的"用肉眼判断聚类质量"的验证层,M1 有绿灯后才动 — 提前动 UI 会浪费调试精力

### A2.2 模型选择:用 ViT-S/16

A2 端侧用 **ViT-S/16**(`facebook/dinov3-vits16-pretrain-lvd1689m`):
- 移动端必须考虑算力 / 包体积 / 内存
- A2 目标是打通链路,不是最终性能
- ViT-S 特征质量在 A1 已经验证(同组聚类做得到就够用)
- D 阶段训练完线性头后再回头评估端侧到底用 S 还是 B,A2 不下决策

模型文件目标体积:S/16 FP16 ONNX ≈ 45MB。Android/iOS 可接受。

### A2.3 新目录规划:`PhotoViewer/Core/AI/`

```
PhotoViewer/Core/AI/
├── DinoFeatureExtractor.cs     ← M1 新增:ONNX Runtime 平台门面,输入 ImageFile / 解码后 RGB 张量,输出 [CLS] 向量
├── DinoModelResources.cs       ← M1 新增:模型文件路径 + 版本号 + 输入规格常量(518/normalize 参数)
├── DinoFeatureCache.cs         ← M2 新增:读写 PhotoDatabase.feature_vector 的薄封装,按 PhotoFingerprint 命中
└── SimilarityService.cs        ← M2 新增:基于 feature_vector 的 cosine 相似度聚类(取代老占位)
```

命名空间:`PhotoViewer.Core.AI`。

**设计约束**:
- `Core/AI/` 内部不 using `Core/Similarity/` 任何类型 — 老目录是已废弃隔离区
- `DinoFeatureExtractor` 是静态/单例门面,**M1 统一走 CPU EP**(最小化变量);CoreML(macOS/iOS)与 NNAPI(Android)由 `Microsoft.ML.OnnxRuntime` 1.25+ 基础包内置,M2 或 B 阶段按测时结果启用;Windows DirectML 是独立包 `Microsoft.ML.OnnxRuntime.DirectML`,留到 M2 后再考虑
- 特征提取按需触发,提取后写入 [PhotoDatabase](../PhotoViewer/Core/Database/PhotoDatabase.cs) `.feature_vector` 列(已预留);后续命中缓存不再重算
- 同次曝光的 RAW/JPG/HEIF 三联基于 [PhotoFingerprint](../PhotoViewer/Core/Database/PhotoFingerprint.cs) 共享特征向量,只算一次
- 像素输入管线复用 Avalonia `WriteableBitmap.CopyPixels`(桌面/移动同路径),**不引 SkiaSharp**,避免增加移动端包体

模型文件放 [PhotoViewer/Assets/Models/dinov3_vits16.onnx](../PhotoViewer/Assets/Models/),通过项目已有的 `<AvaloniaResource Include="Assets/**"/>` 打包,运行时用 `AssetLoader.Open(uri)` 读流 → ORT `InferenceSession(byte[])`。

NuGet(1.25.1,在 [Directory.Packages.props](../Directory.Packages.props) 集中管理):
- `Microsoft.ML.OnnxRuntime`(唯一包,覆盖 Windows / macOS / iOS / Android)
- `Microsoft.ML.OnnxRuntime.Managed` 不单独引,`Microsoft.ML.OnnxRuntime` 已自动带入

### A2.4 M1 — 打包与运行跑通(不动 UI)

#### M1.1 ONNX 导出(CLS + patch 双输出)

```python
# Training/onnx/export_dinov3_onnx.py
import torch
from transformers import AutoModel

MODEL_ID = "facebook/dinov3-vits16-pretrain-lvd1689m"
INPUT_SIZE = 518  # 与 A1 结论同步
model = AutoModel.from_pretrained(MODEL_ID).eval()

# 双输出:CLS 供 M2 相似聚类,patch 供 B 阶段美学评分;权重不变,零包体积成本
class DinoFeatureWrapper(torch.nn.Module):
    def __init__(self, m): super().__init__(); self.m = m
    def forward(self, x):
        h = self.m(pixel_values=x).last_hidden_state  # [B, 1+N*N, D]
        cls   = h[:, 0, :]                            # [B, D]
        patch = h[:, 1:, :]                           # [B, N*N, D]
        return cls, patch

dummy = torch.randn(1, 3, INPUT_SIZE, INPUT_SIZE)
torch.onnx.export(
    DinoFeatureWrapper(model), dummy,
    "PhotoViewer/Assets/Models/dinov3_vits16.onnx",
    opset_version=17,
    input_names=["pixel_values"],
    output_names=["cls_embedding", "patch_tokens"],
    dynamic_axes={"pixel_values": {0: "batch"}}
)
```

**M1 期间 C# 只消费 `cls_embedding`**,`patch_tokens` 端口保留闲置,等 B 阶段的 `PatchFeatureExtractor` 接入。这样 B 阶段不需要重新打包四平台 ONNX,只改 C# 消费端。

**Python 侧验收**:用 onnxruntime 加载后,两个输出都与 PyTorch 对齐,CLS cosine ≥ 0.999、patch 逐 token mean cosine ≥ 0.999(逐张对比 100 张)。对应 `Training/onnx/verify_onnx_parity.py`。

#### M1.2 C# 特征提取器

只实现 [Core/AI/DinoFeatureExtractor.cs](../PhotoViewer/Core/AI/DinoFeatureExtractor.cs) + [Core/AI/DinoModelResources.cs](../PhotoViewer/Core/AI/DinoModelResources.cs)。接口最小化:

```csharp
namespace PhotoViewer.Core.AI;

public static class DinoFeatureExtractor
{
    /// <summary>从图片文件提取 [CLS] 特征向量(同步解码 + 异步推理)。</summary>
    public static Task<float[]> ExtractAsync(ImageFile file, CancellationToken ct = default);

    /// <summary>从解码后的 RGB 张量提特征(供批量场景复用,避免重复解码)。</summary>
    public static Task<float[]> ExtractFromRgbAsync(ReadOnlyMemory<byte> rgb, int width, int height, CancellationToken ct = default);
}
```

**M1 不做**:
- 不写数据库缓存逻辑(M2 才做)
- 不写相似度算法(M2 才做)
- 不接 UI(M2 才做)
- 不做批量 API(远期 B 阶段做)

#### M1.3 四平台启动自检

在 [Platform/](../PhotoViewer/Core/Platform/) 新增一个极简的启动自检钩子:应用启动后,在后台跑一次"内置测试图 → ExtractAsync → 记录单张耗时与向量长度"。结果只写日志(`Console.WriteLine`),不显示在 UI。

| 平台 | 验证项 | 启动方式 |
|---|---|---|
| Windows | CPU EP,单张提取 ≤ 400ms,日志见 384 维向量 | VS Code Task `Debug Windows` |
| macOS | CPU EP,单张提取 ≤ 400ms | VS Code Task `Debug Mac` |
| Android | CPU EP,单张提取 ≤ 800ms,APK 增量 < 60MB | `Debug Android` |
| iOS | CPU EP,单张提取 ≤ 800ms | `Debug iOS` |

**M1 验收门**:四平台日志都能打出合理耗时与向量 — 才进入 M2。自检钩子在 M2 接入真实缓存后即删除。

### A2.5 M2 — 相似聚类接 UI 并废弃老 `Core/Similarity/`

#### M2.1 新建 `Core/AI/SimilarityService.cs`

与老占位**无任何 API 兼容负担**,直接按新场景设计:

```csharp
namespace PhotoViewer.Core.AI;

public sealed record SimilarityItem(ImageFile File, double Score);

public static class SimilarityService
{
    /// <summary>基于 DINO [CLS] cosine 相似度找相似项,命中 PhotoDatabase 缓存则跳过 ONNX 推理。</summary>
    public static Task<IReadOnlyList<SimilarityItem>> FindSimilarAsync(
        ImageFile current,
        IReadOnlyList<ImageFile> pool,
        double threshold = 0.75,
        CancellationToken ct = default);
}
```

- 先读 [PhotoDatabase](../PhotoViewer/Core/Database/PhotoDatabase.cs) `.feature_vector`,miss 才走 `DinoFeatureExtractor.ExtractAsync`
- 命名空间与类型名允许与老 `PhotoViewer.Core.Similarity.SimilarityItem` 撞名 — 因为老命名空间整体删除,不共存
- 时间差只保留作为 tiebreaker(同 score 时按拍摄时间近的优先),不作为相似度主因

#### M2.2 改动 `SimilarityPanelViewModel`

- [SimilarityPanelViewModel.cs](../PhotoViewer/ViewModels/Main/File/SimilarityPanelViewModel.cs) 的 `using PhotoViewer.Core.Similarity;` 改为 `using PhotoViewer.Core.AI;`
- 仅改 `using`,其余事件订阅 / 抑制位 / UI 调度逻辑不动
- [SimilarityListView.axaml](../PhotoViewer/Views/Main/File/SimilarityListView.axaml) 的 DataTemplate 绑定路径 `{Binding File}` `{Binding Score}` 保持一致,依赖新 `SimilarityItem` 的形状与老的一致(这是我们特意保持的迁移兼容点)

#### M2.3 删除老 `Core/Similarity/`

完成 M2.1/M2.2 且跑通后,**立即删除**:
- [PhotoViewer/Core/Similarity/SimilarityService.cs](../PhotoViewer/Core/Similarity/SimilarityService.cs)
- [PhotoViewer/Core/Similarity/](../PhotoViewer/Core/Similarity/) 整个目录

不留 `[Obsolete]` 标记,不留 type forwarder,不保留兼容路径。依 §8 规范"无开放式回退、无静默回退"。

#### M2.4 功能验收

- 打开任意文件夹,选中一张照片,相似面板显示基于 DINO 特征的真实聚类
- 肉眼判断:同机位/同题材/连拍聚到一起,跨题材不会乱入
- 同次曝光的 RAW/JPG/HEIF 三联基于 [PhotoFingerprint](../PhotoViewer/Core/Database/PhotoFingerprint.cs) 共享特征向量,日志可见只算一次
- 切换文件夹 / 筛选变化时,相似面板能正确重算(沿用老事件时序,不回归)

**M2 不要做**:
- 排序头 / 美学打分(那是 D 阶段的事)
- CV 网格特征接入(A3 设计完才做)
- EXIF 向量化(D 阶段才用)
- 性能极致优化(打通即可,批量优化留给 B)

### A2.6 配套工具

- [Training/onnx/export_dinov3_onnx.py](../onnx/export_dinov3_onnx.py) — M1.1 的导出脚本
- [Training/onnx/verify_onnx_parity.py](../onnx/verify_onnx_parity.py) — M1.1 的 PyTorch vs ONNX 对齐校验
- 平台部署沿用 [DEV.md](../DEV.md) 的 VS Code Tasks,不引入新 build 系统

---

## 阶段 A3:CV 网格 + DINO patch token 融合设计 + CV 一期 C# 骨架

### A3.1 目的

A3 两条主线并行推进:

1. **设计侧(notebook PoC)**:
   - CV 网格物理量 — 每格输出哪些标量,怎么在相机原生 6000/7000 长边下稳定提取、跨分辨率可比
   - DINO patch token 融合 — patch 每格的语义先验作为 CV 网格的加权来源,让"该位置该锐还是该虚"(场景 6)有数据依据
2. **工程侧(C# 一期骨架)**:
   - CV + patch 是全平台推理时的美学评分输入,每张照片首次展示前必须算、结果进 cache。落地纯托管 C# 运行时([Core/AI/CvGridExtractor.cs](../PhotoViewer/Core/AI/CvGridExtractor.cs))
   - 一期实现可直接算的 5 个标量(Laplacian 方差 / Sobel 幅度均值 / 梯度方向熵 / 平均亮度 / 亮度标准差),二期(FFT 高频占比等)推到 B 阶段按需补
   - **一期不写"什么时候触发提取"的调度策略**,只提供 `ExtractAsync(bitmap, model_id)` 同步接口;调度(后台预跑、WiFi/充电开关)待 B 阶段实测耗时后再定

盲目实现"每格算 Laplacian 方差"或"直接拍 patch token"得到的都是垃圾向量。需要先想清楚:

1. **每个格子要让 AI 学到什么具体能力?**(对应 §0.3 的 7 类场景)
2. **每个格子需要哪些物理标量,这些标量在不同题材下是否仍有判别意义?**
3. **网格密度如何选?**(已锁 16×16,见 A3.2)
4. **如何归一化?**(不同分辨率/曝光下的同一物理量必须可比)
5. **patch token 怎么降维?**(1025×384 直存 ≈ 1.5MB/张,万张量级 15GB,必须压缩)

A3 输出:**设计文档 + 小样本 PoC(notebook) + C# 一期骨架**(能跑但未接入 UI 调度)。PoC 冻结后才进入 B 阶段批量入库。

### A3.2 必须回答的设计问题(逐条对齐 §0.3 场景)

| 场景 | CV 网格物理量 | DINO patch token 语义先验 |
|---|---|---|
| **场景 6:主体清/背景虚的题材化判断** | 每格清晰度图 — 客观告诉 AI "这格实际有多锐" | 主体/背景软掩码(patch 与 [CLS] 的相似度 + 聚类) — 告诉 AI "这格该不该锐";题材语义(人像 vs 风光 vs 静物) — 决定全局权衡策略 |
| **场景 7:夜景旋转抖动** | 梯度方向熵 + 高频各向异性 — 拖影让点光源边缘沿一个方向拉长 | patch token 相邻格的语义一致性 — 连续建筑边缘该有的"各向同性锐边"语义定位,排除"车流/反光"这类天然各向异性区 |
| **场景 1:留白 vs 杂乱树枝** | 边缘密度图 + 局部对比度图 | patch token 判别"留白区语义连续" vs "树枝区语义碎片化",区分"该低密度的留白"和"该高密度的主体" |
| **场景 2:田埂曲线决定性细节** | CV 网格无能为力(尺度太小) | patch token 高分辨率(A1 若选 1024 输入,patch ≈ 94×94px)能捕捉田埂/建筑边缘的语义差异 |
| **场景 4:静物连拍** | 网格几乎一致 → 辅助 tie 证据 | patch token cosine ≥ 0.99 的高置信度 tie 判据(比 [CLS] 更细粒度) |
| **场景 5:孤立瞬间抓拍** | 无对比样本,CV 网格仅提供"全图物理质量" | patch token 提供全图语义多样性度量,判"氛围片" |

**融合方式(融合不是拼接,是加权)**:
- 从 patch token 计算每格的"主体权重" `w_ij ∈ [0,1]`(例如 softmax(patch · [CLS]) 归一化)
- CV 网格每格标量乘以 `w_ij` 得加权版本,和原始版本一并入库 — 下游 MLP 自己学"什么时候用加权版"
- **不在 A3 设计里硬编码"主体清晰度权重 > 背景"** — 那是硬规则,违反 §0.5 副作用 3

注意:**CV 网格不直接做语义判断**(那是 patch token 的工作),它提供"该位置的物理质量"客观指标;**patch token 也不直接输出清晰度**(那是 CV 网格的工作)。两者在同一网格下对齐、融合、共同喂入 D 阶段 MLP。

**网格对齐策略(锁定 16×16,零插值)**:
- patch 与 CV 网格统一锁到 **16×16**,融合时一一对应、零插值损失
- patch 原生 grid 到 16×16 的下采样均为**整数倍 2D avg pool**:
  - ViT-S/16 @ 518 → 32×32 patch,2×2 avg pool → 16×16
  - ViT-S/16 @ 1024 → 64×64 patch,4×4 avg pool → 16×16
  - ViT-B/L 同理
- CV 网格 16×16,对应原图长边 6000 时每格 ~375×375 px(~140k 样本/格),Laplacian/方向熵的样本数充足
- **不再保留 8×8 / 12×12 候选** — 选 16 的理由是同时满足"对齐整除 + 样本量足够";下游若需要粗粒度,可 runtime 对 16×16 再做 2×2 avg pool 得 8×8,免二次存储

### A3.3 候选标量与设计权衡

**一期标量(A3 C# 骨架实现,纯托管,无 native 依赖)**:

| 标量 | 物理意义 | 解决场景 | C# 实现路径 |
|---|---|---|---|
| **Laplacian 方差** | 二阶导数能量,清晰度黄金指标 | 6, 7 | 3×3 卷积 + 方差 |
| **Sobel 梯度幅度均值** | 一阶导数,边缘密度 | 1, 7 | 两个 3×3 卷积 + `sqrt(gx²+gy²)` 均值 |
| **梯度方向直方图熵** | 边缘各向异性(低熵=方向集中=可能拖影) | **7(关键)** | Sobel 结果 → `atan2` → 8-bin 直方图 → 香农熵 |
| **平均亮度** | 曝光分布 | 1 | 累加取均值 |
| **亮度标准差** | 局部对比度,留白区低 | 1 | 累加方差 |

一期 5 个标量全是**纯标量加减乘除**,`System.Numerics.Vector<T>` SIMD 化,`Parallel.ForEach` 跨格并行;桌面预期 ~100ms/张(16×16 格 × 3 尺度金字塔),移动端 ~500ms。

**二期标量(B 阶段按需补,可能引入额外依赖)**:

| 标量 | 物理意义 | 依赖 | 取舍 |
|---|---|---|---|
| **FFT 高频能量占比** | FFT 后 cutoff 以上能量 / 总能量 | 需 FftSharp 或手写 2D FFT | 与 Laplacian 重复度高,A3 PoC 若证明 Laplacian 够用则永久砍掉 |

**决策规则**:A3 PoC notebook 用 numpy(含 FFT)跑全量 6 个标量可视化,如果 FFT 高频占比在七类场景热力图上看不出比 Laplacian 方差更强的信号 → 永久砍掉,二期也不做;否则 B 阶段评估是否引入 FftSharp(~300KB、MIT,可接受)。

**网格密度**:锁定 **16×16**(决策见 A3.2 网格对齐)。不再对比 8/12,PoC 只探索"每格算哪些标量"与"金字塔跨尺度聚合怎么写"。

#### 分辨率策略:**不做"长边 2000 下采样"**

原先的"长边 2000 等效分辨率"是有害的:
- Sobel/Laplacian/方向熵都是高频敏感量,双线性下采样本身就是低通滤波,2000 已经把场景 7 点光源拖影(1-2px 级现象)的判别力砍掉大半
- 在 2000 下算 12×12 的一个格子 ≈ 166×166px,在 6000 原图里对应 500×500px — 两个尺度下"同一张照片"算出的锐度分布是两个分布,谈不上"等效分辨率"
- 相机原生 6000-7000 长边是数据事实,提前下采样既丢精度又引入采样误差

**改为:原图提取 + 多尺度金字塔 + 跨尺度聚合**:
- 保留**相机原生分辨率**计算(6000-7000 长边,不预缩)
- 同时按 1/2、1/4 下采样各再算一遍 — 金字塔 3 层
- 每格在每个尺度独立算所有标量,最后按格聚合:
  - 绝对值标量(亮度、对比度):取原图尺度
  - 高频敏感标量(Laplacian、方向熵、Sobel):**三层分位数组合**(p50 / p90 / max ratio)
- 跨尺度聚合才能区分"全图粗糊" vs "只是噪点级微糊"、"真实主体纹理" vs "压缩噪声":单尺度做不到
- 代价:4080 CPU 上 opencv/numpy 在 6000 原图上 ≤0.5s/张,不是瓶颈(B 阶段万张 ~1.5 小时可接受)

#### 跨图归一化策略(独立于单图分辨率)

- 每个标量的尺度差异巨大(Laplacian 方差能从 10 跳到 10000)
- 在**数据集层面**做 z-score 或 percentile 归一化,而不是单图归一化(单图归一化会丢绝对差异:全图清晰 vs 全图糊片)
- 归一化参数(均值/标准差、分位点)在 B 阶段从全库统计后冻结,推理期直接套用

### A3.4 DINO patch token 存储与压缩设计

本小节独立于 CV 网格,但与 A3.2 的融合策略共用网格对齐。

#### 存储代价基线(必须压缩)

ViT-S/16 @ 518 → 32×32 + 1 = 1025 token × 384 维 × f32 = **1.54 MB/张**,万张 ≈ 15 GB
ViT-S/16 @ 1024 → 64×64 + 1 = 4097 token × 384 维 × f32 = **6.3 MB/张**,万张 ≈ 63 GB
ViT-B/16 @ 518 → 1025 × 768 × f32 = 3.08 MB/张,万张 ≈ 31 GB
ViT-L/16 @ 518 → 1025 × 1024 × f32 = 4.1 MB/张,万张 ≈ 41 GB

SQLite BLOB 直撑 15-60GB 不是不能跑,但每 SELECT 一行就拖一个 MB 级 blob,聚类/训练 IO 难看。**必须先压缩再入库**。

#### 候选压缩方案(PoC 决定)

| 方案 | 压缩比 | 质量损失评估 | 实现难度 |
|---|---|---|---|
| **INT8 量化**(per-token min-max 或全库统一 scale) | 4× | cosine 保留度 ≥ 0.999(业界共识) | 低 |
| **空间下采样到 16×16**(2D avg pool,再 INT8) | 16× | 足够做"每格语义先验",丢掉亚格精度 | 低 |
| **PCA 降维 384→64**(全库拟合) | 6× | cosine 保留度 ≥ 0.98(需 PoC 验证) | 中(需要全库遍历拟合 PCA 基) |
| **空间下采样 + PCA**(组合) | 96× | 可能过激,PoC 验证 | 中 |

**初始选型建议**(A3.4 PoC 冻结):
- **CLS 一路**:全精度 f32,维度随 model_id(S=384 / B=768 / L=1024),写入 `photo_features` 纵表
- **patch token 一路**:原生 patch grid(32 或 64)**整数倍 2D avg pool 下采样到 16×16**(零插值),再 **INT8 量化** → 16×16×feature_dim×1B,S=96 KB/张,写入 `photo_patches` 纵表
- PCA 不在第一版用(多加一个全库统计环节,复杂度不匹配 A3 的"设计"定位)
- 网格密度 16 是锁定值,见 A3.2 网格对齐策略

#### 存储结构(CLS + patch 均按 `(fingerprint, model_id)` 纵表)

原 `photos.feature_vector` + `feature_model` 单列设计**无法同时存多个模型**(A1 对比 S/B/L 时需要频繁清表重跑,D 阶段训练想比对模型迁移效果也做不到)。改为双纵表:

```sql
-- CLS 特征:原 photos.feature_vector / feature_model / feature_computed_at 迁移到此
CREATE TABLE photo_features (
  fingerprint    TEXT NOT NULL,
  model_id       TEXT NOT NULL,       -- 'dinov3_vits16_f32_518_v1' / 'dinov3_vitl16_f32_1024_v1' ...
  vector         BLOB NOT NULL,       -- CLS f32,维度由 model_id 隐含(S=384 / B=768 / L=1024)
  feature_dim    INTEGER NOT NULL,    -- 显式存一份便于查询
  input_size     INTEGER NOT NULL,    -- 518 / 1024
  computed_at    TEXT NOT NULL,
  PRIMARY KEY (fingerprint, model_id)
);

-- Patch 特征:独立表避免单行 MB 级 blob 拖主表
CREATE TABLE photo_patches (
  fingerprint    TEXT NOT NULL,
  model_id       TEXT NOT NULL,       -- 与 photo_features.model_id 一一对应
  grid_size      INTEGER NOT NULL,    -- 固定 16(锁定,零插值融合)
  feature_dim    INTEGER NOT NULL,    -- 384 / 768 / 1024,随 model_id
  quantization   TEXT    NOT NULL,    -- 'int8' / 'int8_per_token' / 'f16'
  scale          BLOB,                -- INT8 反量化 scale
  zero_point     BLOB,                -- INT8 反量化 zero-point(可选)
  patches        BLOB    NOT NULL,    -- 16 × 16 × feature_dim 的 INT8 张量,行主序
  input_size     INTEGER NOT NULL,    -- 518 / 1024
  computed_at    TEXT    NOT NULL,
  PRIMARY KEY (fingerprint, model_id)
);
```

**读写纪律(必须写入代码注释 + 复查清单)**:
- 每次 `SELECT` 必须带 `WHERE fingerprint=? AND model_id=?`,**禁止**按 `fingerprint` 单键查找(会返回多行)
- 每次 `INSERT OR REPLACE` 必须带完整 PK
- **不做跨模型 fallback** — 即便某照片在 L 表里没命中,也不要回退读 S。相似度/聚类要求 embedding 空间一致,跨模型 cosine 是垃圾
- 切换 `model_id` 走 app 级开关,链路整体切换(对应的 [DinoModelResources](../PhotoViewer/Core/AI/DinoModelResources.cs) 与 cache key 同步)

**存储代价(16×16 INT8,按模型并存时相加)**:

| 模型 | 单张 patch | 单张 CLS | 万张(patch+CLS) |
|---|---|---|---|
| ViT-S/16 | 96 KB | 1.5 KB | ~980 MB |
| ViT-B/16 | 192 KB | 3 KB | ~1.95 GB |
| ViT-L/16 | 256 KB | 4 KB | ~2.6 GB |

万张 S+L 并存 ≈ 3.6 GB,十万张 S+L ≈ 36 GB。淘汰某模型 → `DELETE FROM photo_patches WHERE model_id=?` + `photo_features` 同理,一键清。

#### CV 网格入库(与模型无关,保留在主表)

CV 网格是纯 opencv 物理量,与 DINO 模型无关 → **不按 model_id 分叉**,直接加列到 `photos`:

```sql
ALTER TABLE photos ADD COLUMN cv_grid BLOB;         -- 16×16×K × f32,K=保留标量数(PoC 后 ≈ 3-6)
ALTER TABLE photos ADD COLUMN cv_grid_spec TEXT;    -- JSON:{"grid":16, "scalars":["laplacian_p90","sobel_mean",...], "version":"v1"}
```

CV 网格 16×16 × 6 标量 × f32 ≈ 6 KB/张,足够小。

#### 迁移节奏(A3 冻结时一次 ALTER)

A2-M2 阶段**不改 schema**,继续用现 `photos.feature_vector` / `feature_model` 单列跑相似聚类(只有一个模型,单列够用)。
A3 冻结时一次性 ALTER 到位:
1. `CREATE TABLE photo_features` + `photo_patches`
2. `INSERT INTO photo_features SELECT fingerprint, feature_model, feature_vector, ... FROM photos WHERE feature_vector IS NOT NULL` — 把现有 CLS 迁过去
3. `photos.feature_vector` / `feature_model` / `feature_computed_at` 三列**标 deprecated 但不 DROP**(数据保留,新写入停走)
4. `ALTER TABLE photos ADD COLUMN cv_grid BLOB` / `cv_grid_spec TEXT`

迁移后 B 阶段批量入库一次写三张(`photo_features` + `photo_patches` + `photos.cv_grid`)。

### A3.5 PoC 实操

**输入**:从你的库挑 30-50 张代表样本,按 §0.3 七类场景各取数张,标好"哪些区域应该锐 / 应该虚 / 应该不予考虑"。

**输出**:
- **CV 侧**:一个 Jupyter notebook,可视化每张图在 8×8 / 12×12 / 16×16 三种密度下的各项标量热力图;金字塔 3 层独立出图 + 跨尺度聚合结果图
- **patch token 侧**:
  - 每张图的 [CLS] · patch cosine 热力图(主体软掩码)
  - patch token K-means(K=2-4)的"语义区域"可视化
  - INT8 量化前后的 cosine 保留度统计(PoC 判据)
- **融合侧**:CV 清晰度图 × patch 主体权重 → 加权清晰度图,与纯 CV 清晰度图并排对比
- 主观评估表:每个标量在每类场景下是否真的"看得出来该锐还是该虚"

**决策依据**:
- 哪些 CV 标量在所有场景下都低判别力 → 砍掉
- 哪种 CV 网格密度与 patch token 下采样目标对齐后的可视化最贴近人眼直觉 → 保留
- INT8 量化的 cosine 保留度 ≥ 0.999 → 采用;否则退回 f16(2× 压缩,已是最轻量的损失压缩)
- 哪些场景仍无解(例如纯抓拍人像) → 记入"当前基建能力边界",由 D 阶段 MLP 或远期阶段兜底

### A3.6 验收标准

**设计侧**:
- A3 设计文档完成,明确:
  - CV 网格:密度 **16×16** × 一期 **5** 标量 × 金字塔 3 层聚合 = **3840** 维 f32(~15 KB/张)
  - patch token:下采样目标网格 **16×16** × feature_dim(S=384 / B=768 / L=1024)× INT8 per-token
  - 归一化:CV 网格 DB 存 raw,跨图 z-score/percentile 参数在 B 阶段 `cv_norm_params` 表冻结;`cv_grid_spec.norm_version` 标记 PoC v0 → 全库 v1
- PoC notebook 在 §0.3 七类场景代表样本上,**至少 5 类**的 CV 热力图 + patch 软掩码 + 融合图与人眼直觉一致
- INT8 量化方案通过 cosine 保留度 ≥ 0.999 PoC
- FFT 高频占比标量的去留在 PoC 中明确(留 → B 阶段引入 FftSharp / 砍 → 永久不实现)
- 与 A1 对接点明确:patch token 用 A1 决策的输入尺寸(518 或 1024)
- 与 B 对接点明确:`photo_features` + `photo_patches` + `photos.cv_grid` 的 schema 冻结,B 开工前 ALTER TABLE 一次到位

**工程侧**:
- [Core/AI/CvGridExtractor.cs](../PhotoViewer/Core/AI/CvGridExtractor.cs) 实现一期 5 标量,`ExtractAsync` 可调用、可单测、可在 4 平台启动时至少跑一张不崩
- [Core/AI/CvGridCache.cs](../PhotoViewer/Core/AI/CvGridCache.cs) 读写 `photos.cv_grid` BLOB(A3 阶段可先用临时列或 JSON 路径,schema ALTER 可推到 B 入口)
- C# 与 notebook numpy 版同位置数值相对误差 ≤ 1%
- 桌面 / 移动端各跑一张真机耗时达标(桌面 ≤ 200ms,移动端 ≤ 1s)

A3 完成后,B 阶段才能开始**真正意义上的批量入库**(否则现在就批一遍,后面发现网格设计要改还要重来)。

### A3.7 配套工具

- `Training/notebooks/cv_grid_design.ipynb` — A3.5 的 CV 网格设计 PoC notebook(用 numpy 跑全量 6 标量)
- `Tools/patch_token_design.ipynb` — A3.5 的 patch token 可视化 + 量化 PoC notebook
- `Tools/cv_grid_extract.py` — **桌面 4080 批处理加速路径**,与 C# 运行时共享 schema,互操作可读(B 阶段按需使用)
- `Tools/patch_token_extract.py` — 同上,桌面 patch token 提取 + 下采样 + 量化(B 阶段按需使用)

### A3.8 CV 一期 C# 运行时骨架(本期实现)

**目的**:CV 网格是全平台推理时的美学评分输入,每张照片首次展示前必须算,结果进 cache。一期落地"能跑"的骨架,不做调度。

#### 文件落点

| 文件 | 职责 |
|---|---|
| [Core/AI/CvGridExtractor.cs](../PhotoViewer/Core/AI/CvGridExtractor.cs) | 静态门面 + 核心提取逻辑。纯托管,无 native 依赖 |
| [Core/AI/CvGridResult.cs](../PhotoViewer/Core/AI/CvGridResult.cs) | POCO:`{GridSize=16, Scalars=["laplacian_var","sobel_mean","grad_dir_entropy","luma_mean","luma_std"], Pyramid=3, Data=f32[16*16*5*3], Version="cv_grid_v0_5scalar"}` |
| [Core/AI/CvGridCache.cs](../PhotoViewer/Core/AI/CvGridCache.cs) | `photos.cv_grid` BLOB 读写 + LRU 内存 cache 接入(复用 [DinoFeatureCache](../PhotoViewer/Core/AI/DinoFeatureCache.cs) 的模式) |

#### 接口

```csharp
// CvGridExtractor.cs
public static class CvGridExtractor
{
    /// <summary>从已解码 bitmap 提取 16×16 网格 × 5 标量 × 3 尺度金字塔。</summary>
    /// <param name="bitmap">Avalonia 位图,已应用 EXIF rotation</param>
    /// <returns>一期固定 16×16×5×3 = 3840 个 f32,约 15 KB/张</returns>
    public static async Task<CvGridResult> ExtractAsync(
        Bitmap bitmap,
        CancellationToken cancellationToken = default);
}
```

#### 实现约束

- **纯托管**:禁止引入 OpenCvSharp / Emgu.CV / SkiaSharp 等 native 依赖(打包体积 + 跨平台麻烦);所有算子 3×3 卷积 / 直方图 / 累加均用 `Span<T>` 或 `System.Numerics.Vector<T>` SIMD 手写
- **性能预算**:
  - 桌面(Windows/Mac):单张 ≤ 200ms(金字塔 3 层 + 16×16×5 标量)
  - 移动端(iOS/Android):单张 ≤ 1s
  - 超预算则降级 — 一期允许降金字塔层数(3→1)或跳过高频敏感标量(方向熵),但 schema `Version` 字段必须同步改以便 cache 命中区分
- **尺度归一化**:提取时**只存 raw 值**,不做跨图归一化;全库统计参数留到 B 阶段 `cv_norm_params` 表(A3.3 分辨率策略末段)
- **cache 粒度**:`photos.cv_grid` 按 `fingerprint` 存(与模型无关),同一指纹的 RAW/JPG/HEIF 共享一条记录

#### 一期**不做**的事

- **UI 调度**:不在进入文件夹时后台预跑,不接 [BitmapPrefetcher](../PhotoViewer/Core/Image/BitmapPrefetcher.cs) 的队列;仅提供被动接口 `ExtractAsync`,由 D/E 阶段真正用到的地方再接触发点
- **移动端 WiFi/充电开关**:同上,留到 B 阶段再加
- **二期标量**:FFT 高频能量占比等,按 A3.3 规则评估后推到 B
- **批量提取工具**:`Tools/cv_grid_extract.py` 作为桌面加速路径存在,但不是 A3 本期交付物(B 阶段按需)

#### 验收

- [Core/AI/CvGridExtractor.cs](../PhotoViewer/Core/AI/CvGridExtractor.cs) 单测:同一张图多次调用结果一致、5 标量值域合理(Laplacian 方差 ≥ 0 等)
- 在 A3.5 PoC 样本 30 张上跑,产出 C# 版热力图与 notebook numpy 版热力图**同位置数值相对误差 ≤ 1%**(算法一致性校验)
- 桌面 + 移动端各跑一张真机耗时记录,写入验收报告

---

## 远期阶段大纲(待 A 后逐段重新规划)

### 阶段 B:数据工程基建

复用现有基建([PhotoDatabase](../PhotoViewer/Core/Database/PhotoDatabase.cs) + [PhotoFingerprint](../PhotoViewer/Core/Database/PhotoFingerprint.cs) 已写好)。

**入口工作**(A3 冻结后):
1. 按 A3.4 冻结的 schema 执行 ALTER:
   - `CREATE TABLE photo_features(...)` + `CREATE TABLE photo_patches(...)`
   - `INSERT INTO photo_features SELECT fingerprint, feature_model AS model_id, feature_vector AS vector, ... FROM photos WHERE feature_vector IS NOT NULL` — 历史 CLS 迁入纵表
   - `ALTER TABLE photos ADD COLUMN cv_grid BLOB` / `cv_grid_spec TEXT`
   - `photos.feature_vector` / `feature_model` / `feature_computed_at` 三列**标 deprecated 不 DROP**,新写入停走
2. 新增 `PatchFeatureExtractor.cs`:消费 A2-M1 已经就绪的 `patch_tokens` ONNX 端口(**不需要改模型、不需要重部署四平台**),做 2D avg pool 到 16×16 + INT8 per-token 量化
3. 重构 [DinoFeatureCache](../PhotoViewer/Core/AI/DinoFeatureCache.cs):读写切到 `photo_features` 纵表,所有 SQL 强制带 `(fingerprint, model_id)`;新增 `PatchFeatureCache` 读写 `photo_patches`,`CvGridCache` 从 A3 的 JSON 路径 / 临时列切到正式 `photos.cv_grid` 列
4. 若 A3.5 PoC 证明 FFT 高频占比有用 → 评估引入 FftSharp,`CvGridExtractor` 升到二期(新 `Version` 区分 cache);否则永久砍掉
5. CV / patch **提取调度策略**(进入文件夹后台预跑、移动端 WiFi/充电开关)本阶段落地,基于真机实测耗时定具体策略;桌面 Python 批处理工具(`cv_grid_extract.py` / `patch_token_extract.py`)作为 dev 加速路径,可选

然后批量给万张照片提三路特征入库(CLS、patch token、CV 网格),统一命中 [PhotoFingerprint](../PhotoViewer/Core/Database/PhotoFingerprint.cs) — 同次曝光共享。EXIF 向量化复用 `SonyMakernoteParser` 已解析的对焦点。

**待 A 完成后定**:确定模型尺寸与输入分辨率(由 A1 决定:L vs B,518 vs 1024)、批大小、CV 网格归一化参数(由 A3.5 PoC + B 阶段全库统计决定)、patch token 量化参数(由 A3.4 PoC 决定)、调度策略具体参数(由 B 阶段真机耗时定)。

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
| DINOv3 license 门槛 / 分发条款 | 中 | A1 / A2 打包发布前 | 本期仅本地训练 + 端侧本地推理,**不触发**分发条款;若远期要把 `.onnx` 打进 APK/IPA 对外分发,先 review [DINOv3 License](https://ai.meta.com/resources/models-and-libraries/dinov3-license/) 的 derivative works 条款,不行则整体回退 DINOv2-Large(Apache 2.0,架构兼容,L 维度同为 1024) |
| ViT 特征对你的数据不够敏感 | **高** | A1 | t-SNE 验证就能发现,A1 失败立即停止 |
| ViT @ 518 吃不掉场景 2 的决定性细节 | **中** | A1 | A1.5 强制加 @ 1024 对照;若 1024 更优,B 阶段改走 1024(只加显存不改 schema) |
| 4080 上 ViT-L 显存/速度超预期 | 低 | A1 | 降回 ViT-B(特征维度 1024→768,MLP 首层重训,成本 ~30 分钟) |
| ONNX 算子在 CoreML/NNAPI 上不兼容 | **中** | A2 | 退回 CPU EP(慢但能用);或换 ConvNeXt-Small 变体(算子兼容性好) |
| 移动端 APK/IPA 包体积膨胀 | 中 | A2 | 量化(动态 INT8 通常无质量损失);极端可下载分发模型 |
| CV 网格设计错位,AI 学不到 | **中** | A3 | A3 不冻结设计,B 不开工(用 PoC 强制对齐) |
| patch token 存储爆炸 | **中** | B | A3.4 强制"下采样 16×16 + INT8",万张 ~1GB 可控;若 PoC 显示 INT8 损失过大,退 f16(2× 压缩)或 PCA |
| 多模型并存存储膨胀(S+L 共存) | 中 | B | 万张 S+L ≈ 3.6GB 可接受,十万张 ≈ 36GB 开始吃盘;提供 `DELETE FROM photo_patches WHERE model_id=?` 一键淘汰通道,用完即弃 |
| 跨模型 embedding 被隐式混用 | **高** | B 及之后 | code review 强制所有 `photo_features` / `photo_patches` SELECT 带 `(fingerprint, model_id)` 联合键,禁止跨模型 fallback;加 CI 正则检查"WHERE fingerprint" 必须后接 "AND model_id" |
| patch token cosine 在量化后失真 | 中 | A3/B | A3.5 PoC 必查"INT8 前后 cosine 保留度 ≥ 0.999",不达标换方案 |
| CV 多尺度在 6000 原图上过慢 | 低 | B | numpy/opencv 在 4080 CPU 上 ≤0.5s/张,万张 ~1.5h 可接受;真慢再上 CUDA |
| C# 纯托管 CV 在移动端超预算 | 中 | A3 | `System.Numerics.Vector<T>` SIMD + `Parallel.ForEach` 优先;不达标先降金字塔 3→1 层,再评估引入 SkiaSharp 的 imaging 算子 |
| 每张照片 CV+patch 首次展示耗时过长 | 中 | B | A3 一期只提供 `ExtractAsync` 接口不接调度;B 阶段按真机耗时定"后台预跑 / 可见优先 / 移动端 WiFi-充电 gate"策略,避免阻塞 UI |
| 训练对噪声把模型拉平 | 高 | C-D | 严格 tie 过滤 + 软权重 + 局部可比域(具体阈值待 C 阶段定) |
| 按日期划分后训练集太小 | 中 | C | 数据增强(色彩抖动 + 轻微裁剪) |
| 模型记住场景而非美学 | **高** | 所有 | 按日期划分是唯一防线,严格执行 |
| HEIF/RAW 格式 CV 计算异常 | 中 | B | 统一解码到 8bit RGB 再算,复用 LibHeif 链路 |

---

## 3. 与现有架构的对接清单

| 文件/模块 | 改动类型 | 阶段 |
|---|---|---|
| [PhotoDatabase.cs](../PhotoViewer/Core/Database/PhotoDatabase.cs) | A3 冻结后一次 ALTER:`CREATE TABLE photo_features` + `photo_patches`;`photos` 加 `cv_grid` / `cv_grid_spec`;历史 `feature_vector` 迁入 `photo_features` 后标 deprecated(保留数据不 DROP) | A3→B |
| [PhotoFingerprint.cs](../PhotoViewer/Core/Database/PhotoFingerprint.cs) | 不动 — 已经够用 | - |
| [PhotoViewer/Core/Similarity/](../PhotoViewer/Core/Similarity/) | **整体废弃并删除** — 含 `SimilarityService.cs` 占位 | **A2-M2** |
| `PhotoViewer/Core/AI/DinoFeatureExtractor.cs` | **M1 新增**:ONNX Runtime 平台门面,一次前向拿 CLS + patch(patch 端口闲置);**B 扩展**:新增 `ExtractPatchAsync` 消费 patch 端口 | **A2-M1** → B |
| `PhotoViewer/Core/AI/DinoModelResources.cs` | **M1 新增**:模型路径/输入规格/`model_id` 常量;**B 更新**:若 A1 选 1024 需同步 | **A2-M1** → B |
| `PhotoViewer/Core/AI/DinoFeatureCache.cs` | **M2 新增**:用 `photos.feature_vector` 单列;**B 重构**:切到 `photo_features` 纵表,所有 SQL 强制 `(fingerprint, model_id)` 联合键,移除跨模型 fallback | **A2-M2** → B |
| `PhotoViewer/Core/AI/SimilarityService.cs` | **M2 新增**:cosine 相似聚类,取代老占位 | **A2-M2** |
| `PhotoViewer/Core/AI/CvGridExtractor.cs` | **A3 新增**:C# 纯托管一期 5 标量提取器(Laplacian 方差 / Sobel 幅度均值 / 梯度方向熵 / 平均亮度 / 亮度标准差) | **A3** |
| `PhotoViewer/Core/AI/CvGridResult.cs` | **A3 新增**:CV 网格 POCO | **A3** |
| `PhotoViewer/Core/AI/CvGridCache.cs` | **A3 新增**:`photos.cv_grid` BLOB 读写薄封装(A3 阶段可先用 JSON 路径,schema ALTER 可推到 B 入口) | **A3** → B |
| `PhotoViewer/Core/AI/PatchFeatureExtractor.cs` | **B 新增**:消费 `patch_tokens` ONNX 端口 + 2D avg pool 到 16×16 + INT8 per-token 量化 | B |
| `PhotoViewer/Core/AI/PatchFeatureCache.cs` | **B 新增**:`photo_patches` 读写薄封装,PK `(fingerprint, model_id)` | B |
| [SimilarityPanelViewModel.cs](../PhotoViewer/ViewModels/Main/File/SimilarityPanelViewModel.cs) | **M2 改动**:`using` 切换到 `PhotoViewer.Core.AI`;其余逻辑不动 | **A2-M2** |
| `PhotoViewer/Assets/Models/dinov3_vits16.onnx` | **M1 新增**:S/16 端侧模型,**双输出**(CLS + patch);patch 端口 M1 闲置、B 消费 | **A2-M1** |
| `Tools/dinov3_feature_probe.py` | **A1 新增**:三尺寸 + 双分辨率 t-SNE 验证 | **A1** |
| `Training/onnx/export_dinov3_onnx.py` | **M1 新增**:CLS + patch 双输出 wrapper 导出 | **A2-M1** |
| `Training/onnx/verify_onnx_parity.py` | **M1 新增**:PyTorch vs ONNX 对齐校验(CLS + patch 两路) | **A2-M1** |
| `Training/notebooks/cv_grid_design.ipynb` | **A3 新增**:CV 网格设计 PoC notebook(numpy 跑全量 6 标量,评估 FFT 去留) | **A3** |
| `Tools/patch_token_design.ipynb` | **A3 新增**:patch token 可视化 + 量化 PoC | **A3** |
| `Tools/cv_grid_extract.py` | **B 可选新增**:桌面 4080 批处理加速路径,与 C# `CvGridExtractor` 共享 schema | B(按需) |
| `Tools/patch_token_extract.py` | **B 新增**:桌面 4080 patch 批量提取 + 量化,共享 schema | B |
| `Tools/dinov3_batch_extract.py` | B 阶段新增:统一批处理入口,一次前向写 `photo_features` + `photo_patches` + `photos.cv_grid` | B |
| `Tools/migrate_features_to_longtable.py` | **A3 冻结时新增**:历史 `photos.feature_vector` 迁移到 `photo_features` | A3→B |
| `Tools/build_training_pairs.py` | 远期新增 | C |
| `Tools/train_ranking_head.py` | 远期新增 | D |

---

## 4. 远期升级(超出本计划骨架)

- Contextual Transformer(2-4 层小型注意力)替代邻域聚合
- 在线学习:用户手改星级 → 端侧轻量微调
- 多尺度 patch token 利用(DINOv3 dense feature 比 v2 强很多)
- iPad / 移动端下放(量化 + CoreML/NNAPI)
- 跨用户审美迁移
