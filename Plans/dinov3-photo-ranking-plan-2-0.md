# DINOv3 照片美学评分 — 二期计划(可视化 PoC 先行)

> 二期核心:把"纸面可视化"搬进 PhotoViewer 工具窗口,在真实照片上目测聚类 / CV 网格 / DINO 注意力的质量,作为后续数据工程与训练开工前的最后一道决策门。同时补齐"全文件夹批量特征提取"——没有这一步,相似聚类列表查不到未看过的照片。

---

## 0. 需求锚点(原样沿用一期 §0,不改一字)

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
  - **0→1 星**:孤立照片:清晰度、有效性(不是乱拍的),多张照片:其中较好的一半。
  - **1→2 星**:开始考虑美学氛围,或在多张 1 星中选优
  - **2→3 星**:雷同内容选优(多张同拍摄点、同主体对象、同角度中最好的一张,也有孤立的、但拥有好氛围、或极具代表性的好照片)
  - **3→4 星**:同题材不同视角选优,同时要求美学价值较高
  - **4→5 星**:全局精品
- **隐含污染**:极相似的照片(像素级差异,甚至连清晰度都完全一致)在人工判断中有可能是随机选其一晋级的,这部分对比是无意义噪声。

### 0.3 典型痛点与场景举例(模型必须有能力应对)

| # | 场景 | 模型必须做到 |
|---|---|---|
| 1 | **同山头不同焦距/构图** — 24mm vs 35mm,偏左 vs 偏右 | 判断"留白呼吸"加分 vs "囊括杂乱树枝"减分 |
| 2 | **同题材长焦梯田** — 风格一致,只是田埂曲线 / 小房子位置不同 | 视觉极度相似中识别决定性细节 |
| 3 | **沿山脊移动** — 前景变了远山没变 | 识别"同题材"可比性,既在训练时高权重也在推理时仔细辨别 |
| 4 | **静物连拍** — 两张几乎一样,人工随机选 | 识别为不可比,排除出训练对,推理时不强行二选一 |
| 5 | **孤立瞬间抓拍** — 氛围/光线极佳但无相似可比 | 不再依赖对比,直接判断美学,避免漏氛围片。需要模型能够理解优秀美学的潜质,如果这张照片有 5 星潜质,则至少给到 3 星。 |
| 6 | **主体 vs 背景的题材化权衡** — 人像要主体锐+背景虚,风光要全清+接受留白,有些题材可实可虚 | 根据题材语义切换权衡,不是统一规则 |
| 7 | **夜景抖动判断** — CMOS 防抖随机失败,部分清晰部分拖影 | 通过 CV 在全图网格采样计算数据,结合 ViT 注意力,重点识别建筑边缘 / 点光源,避开车流/反光等天然变化区,判断照片各处是否该实、该虚 |

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
  - 每次查询/推理强制 `WHERE model_id = <当前模型>`,**不做跨模型自动 fallback**(例如"L 没算过就用 S 顶上") — 跨模型 embedding 空间不可比,混用 = 隐蔽错误
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

> ⚠️ **§0.7 第一条的作用域澄清**:
> "DINO 热力图定位关键点截局部 patch" 剔除的是**"把热力图当美学依据"的训练/推理路径** — 模型不得以某格注意力权重高就抬高该格对美学得分的贡献。
> 二期做的"热力图可视化工具"是**给人看的调试面板**,不喂给任何模型。PCA-RGB / CLS-attention / 参考点 cosine 三张图都只在 UI 里渲染、不入库、不参与评分公式。只要这条边界守住,§0.7 和 §0.5 陷阱 4 都没被破坏。

---

## 1. 当前基建盘点

二期开工时,以下能力已就位:

### 1.1 ONNX 推理链路(CLS + patch 双输出)

- [Training/onnx/export_dinov3_onnx.py](../onnx/export_dinov3_onnx.py) — 双输出 wrapper,`cls_embedding` + `patch_tokens`,用 `h[:, -num_patch_tokens:, :]` 切片自动跳过 register token,S/B/L 通用。
- [Training/onnx/verify_onnx_parity.py](../onnx/verify_onnx_parity.py) — PyTorch vs ONNX 两路 cosine ≥ 0.999 校验。
- [Core/AI/DinoFeatureExtractor.cs](../PhotoViewer/Core/AI/DinoFeatureExtractor.cs) — ONNX Runtime CPU EP 平台门面,会话建好后强制 `EnsureDualOutputSchema` 早失败。
- [Core/AI/DinoModelResources.cs](../PhotoViewer/Core/AI/DinoModelResources.cs) — 模型常量集中处,`PatchSize` / `PatchGrid` / `PatchTokenCount` 就位。

### 1.2 特征缓存与相似聚类

- [Core/Database/PhotoDatabase.cs](../PhotoViewer/Core/Database/PhotoDatabase.cs) + [PhotoFingerprint.cs](../PhotoViewer/Core/Database/PhotoFingerprint.cs) — SQLite + 三字段指纹,同次曝光 RAW/JPG/HEIF 共享指纹。
- [Core/AI/DinoFeatureCache.cs](../PhotoViewer/Core/AI/DinoFeatureCache.cs) — 指纹索引 + Lazy 闸门,同指纹并发只跑一次。
- [Core/AI/SimilarityService.cs](../PhotoViewer/Core/AI/SimilarityService.cs) — cosine 聚类,接入 [SimilarityPanelViewModel](../PhotoViewer/ViewModels/Main/File/SimilarityPanelViewModel.cs)。

### 1.3 CV 一期标量提取器

- [Core/AI/CvGridResult.cs](../PhotoViewer/Core/AI/CvGridResult.cs) — POCO,3840 float(16×16 × 5 标量 × 3 层金字塔),小端 BLOB Encode/Decode。
- [Core/AI/CvGridExtractor.cs](../PhotoViewer/Core/AI/CvGridExtractor.cs) — 纯托管 per-cell 扫描,同时累加 Laplacian 方差 / Sobel 幅度 / 方向 8-bin 熵 / 亮度均值/标准差;`Parallel.For` 跨格并行;`Downsample2x` 零插值。
- 目前仅提供 `ExtractAsync` 同步接口,未接缓存、未接 UI 调度。

### 1.4 已知边界

- ONNX 模型文件尚未真实导出到 [PhotoViewer/Assets/Models/](../PhotoViewer/Assets/Models/);相似聚类面板在真机跑起来前必须先完成这一步(见 §3.1)。
- `DinoFeatureCache` 目前只为**当前查看的照片**算特征;未看过的照片没进库 → 相似聚类列表查不到它们。二期解决这个问题(见 §3.2)。
- patch token 虽然 ONNX 端口已就绪,但 C# 侧尚未消费。二期的 DINO 诊断工具是第一个消费方。

---

## 2. 二期目标与交付物

**目标**:在真实照片上目测核心决策,同时把相似聚类做成真正可用的产品功能。

**交付物**:

| 交付 | 动作 | 产出 |
|---|---|---|
| **T1. ONNX 真机自检** | 导出 vits16 模型,四平台 Debug 启动跑通 | 相似聚类面板能给出真结果 |
| **T2. 全文件夹批量特征提取(手动触发)** | 相似聚类列表 toggle 展开后,按钮触发全文件夹批量提特征 | 默认零干扰,需要时一键补齐聚类数据 |
| **T3. CV 诊断工具页** | ToolsView 新增入口,15 原始 + 3 诊断热力图平铺展示 | 目测 §0.3 场景 7 夜景抖动/虚焦能否识别;拍 FFT 去留 |
| **T4. DINO 诊断工具页** | 与 T3 共享工具页,展示 patch PCA-RGB + 点击 cosine | 目测 DINOv3 语义分区质量;判断 S/16 对本数据是否够用 |
| **T5. 相似聚类面板显示控制** | 面板默认隐藏 + FilterBar toggle 展开 + 按数据完整度切换三态 UI | 选片用户零打扰,想用相似聚类的用户一键展开 |

**不在二期做**:
- 任何训练
- `photo_features` / `photo_patches` 多模型纵表 schema(等 T3 拍完 FFT 再一次性 ALTER)
- 美学评分公式本身
- patch token 的长期存储(T4 工具页只在内存里算,不入库)

---

## 3. 功能细则

### 3.1 T1. ONNX 真机自检(前置)

后续功能都需要真模型。此步骤只发生一次,四平台各跑一遍即闭环。

**动作**:
1. 4080 桌面跑 [Training/onnx/export_dinov3_onnx.py](../onnx/export_dinov3_onnx.py) → `PhotoViewer/Assets/Models/dinov3_vits16.onnx`。
2. 跑 [Training/onnx/verify_onnx_parity.py](../onnx/verify_onnx_parity.py) 确认 cls_min / patch_min ≥ 0.999。
3. Windows / macOS / Android / iOS 各 Debug 启动一次,加载模型 → 跑一张图 → 读日志确认 `EnsureDualOutputSchema` 不抛、推理有输出。

**验收**:现有相似聚类面板 Windows Debug 下跑出真实 cosine 分数(不是占位)。

### 3.2 T2. 全文件夹批量特征提取(手动触发)

**问题**:现状只有"用户切换到某张 → 算它一张"。进入新文件夹时,相似聚类列表对未看过的照片无感知;要看到完整聚类,用户得把所有照片逐张滑一遍,不现实。

**解决**:保持默认零干扰 — **不自动跑、也不默认显示聚类列表**。用户在 FilterBar 上的"相似聚类" toggle 展开聚类面板后,面板根据数据库中当前文件夹的覆盖度呈现三态:

| 状态 | 判定 | UI 呈现 |
|---|---|---|
| **A. 全空** | 当前文件夹**无任何**照片已在数据库中算过 CLS | 面板中央一个大"提取全部"按钮,点击开始跑 |
| **B. 部分** | 已有 ≥1 张,但仍有未算过的 | 上半部分显示已入库子集的聚类结果,底部贴一条"还有 N 张未计算 [补齐全部]"按钮 |
| **C. 全齐** | 当前文件夹每张都已在库 | 正常显示全量聚类结果,不出提取按钮 |

**点击提取/补齐后**:按钮**原地变成进度条**(按钮占位不动,文字变"提取中 X/Y",宽度按进度填充),完成后按钮消失并自动刷新聚类列表。

**三态判定的硬性实现约束**:
- 判断"已算过"的条件是 `photos` 表中存在一条 `(fingerprint, model_id = 当前 Id)` 且 `feature_vector NOT NULL` 的记录。跨 model_id 不算。
- "当前文件夹"指 `FolderViewModel.AllFiles` 里的所有 **可计算指纹**的文件(RAW/JPG/HEIF 均按三字段指纹算,无法计算指纹的跳过,不计入分母)。
- 状态判定只在 toggle 展开时做一次 + 提取完成后刷新一次,不做 FileSystemWatcher。

**调度与资源**:
- **不可中断**暂不做;提取过程中切换文件夹时,任务继续后台跑完(避免用户误操作毁掉进度)。但用户再次展开聚类 toggle 时,看到的仍是**新文件夹**的三态。
- 单张失败跳过,累计失败率不中断整批。无 EXIF 指纹的照片直接跳过、不算在分母里。
- 并发:桌面 `Environment.ProcessorCount / 2` 并行解码 + 单线程 ONNX 推理,与现有 `DinoFeatureCache` 的单 Lazy 闸门兼容。移动端保留默认 1 并发,本期不专门调。

**入库 schema(本期锁定)**:
- CLS 继续写现有 `photos.feature_vector` / `feature_model` 单列 — **不做纵表 ALTER**。纵表留到阶段 III 一次性做。
- patch token 本期**不入库**,工具页按需内存跑。
- CV 网格本期**不入库**,由 T3 目测决策后在阶段 III 统一 ALTER。
- 因此 T2 不带 schema 迁移,只跑业务逻辑。

**产品设计已锁定清单**(替代原 §3.2 空白区):
- 触发时机 — **手动**,用户点 FilterBar toggle 展开聚类面板后,再点"提取全部"/"补齐全部"按钮。
- 进度呈现 — **按钮原地变进度条**,不占额外 UI 位。
- 优先级 — 按 `AllFiles` 现有顺序,不做可见优先。
- 可中断性 — 本期**不支持取消**,切文件夹任务继续跑完。
- 资源约束 — 桌面半核并行解码 + 单线程推理;移动端维持单线程。
- 多模型 — 只跑 `DinoModelResources.ModelId`,单模型。
- 失败处理 — 单张跳过,整批不中断。
- 刷新策略 — 提取完成后自动刷新聚类;不做主动失效。

**代码落点**:

| 文件 | 角色 |
|---|---|
| `PhotoViewer/Core/AI/FolderFeatureIndexer.cs` **新增** | 批量任务调度:`RunAsync(IReadOnlyList<ImageFile>)` / `Progress` (Completed / Total / Failed) 事件 / `IsRunning` |
| [Core/AI/DinoFeatureCache.cs](../PhotoViewer/Core/AI/DinoFeatureCache.cs) | 暴露 `TryGetCachedVector(fingerprint)`(同步查 DB 不触发计算),给三态判定用 |
| [ViewModels/Main/File/SimilarityPanelViewModel.cs](../PhotoViewer/ViewModels/Main/File/SimilarityPanelViewModel.cs) | 持有三态 `SimilarityPanelState { Empty / Partial / Full / Indexing }`,订阅 `FolderFeatureIndexer.Progress`,展开时执行首次判定 |
| [ViewModels/Main/File/FilterBarViewModel.cs](../PhotoViewer/ViewModels/Main/File/FilterBarViewModel.cs) | 新增 `IsSimilarityPanelOpen` 绑定 → 通过新事件 `SimilarityPanelToggled` 告知 `FileView` 布局切换 |
| [ViewModels/Main/File/FileViewModel.cs](../PhotoViewer/ViewModels/Main/File/FileViewModel.cs) | 根据 `IsSimilarityPanelOpen` 控制 `SimilarityListView` 可见性,默认隐藏 |
| [Core/Settings/SettingsModel.cs](../PhotoViewer/Core/Settings/SettingsModel.cs) | 新增持久化字段 `SimilarityPanelExpanded`(默认 `false`) |

**硬约束(不动摇)**:
- 所有写数据库的 SQL 必须带 `WHERE fingerprint = ? AND model_id = ?`。
- 入库失败单张跳过,不因一张坏图阻塞整批。
- toggle 开关状态持久化到 `SettingsModel.SimilarityPanelExpanded`,跨会话保留。

### 3.3 T3. CV 诊断工具页

ToolsView 新增入口"DINO 诊断",对当前 `ImageFile` 实时跑一次完整管线并平铺显示诊断图。**结果不入库,UI 销毁即丢。**

#### 3.3.1 数据构成(15 + 3 张热力图)

**原始证据(15 张单通道 16×16)**:5 标量 × 3 层金字塔,顺序与 `CvGridResult.ScalarNames` 对齐。

**诊断图(3 张单通道 16×16,从原始证据推导)**:

| 诊断图 | 推导 | 目测回答 |
|---|---|---|
| 失焦图 | `1 - norm(laplacian_var_L0)` | 哪些格子边缘能量低 → 虚焦或焦外 |
| 抖动图 | `(1 - norm(grad_dir_entropy_L0)) × step(norm(sobel_mean_L0) ≥ τ)` | 熵低且有边 → 方向集中 → 拖影嫌疑 |
| 跨尺度一致性 | `norm(laplacian_var_L0) - norm(laplacian_var_L2)` | 原图糊但 1/4 清 → 高频噪声;都糊 → 真失焦 |

**归一化两档**:

| 模式 | 定义 | 适合 |
|---|---|---|
| `PerPlane`(默认) | 每张 16×16 独立 min-max 归一化 | 看单张图内部相对差异 |
| `PerScalarPyramid` | 同一标量 3 层共享 min-max | 看跨尺度变化 |

具体默认值和 τ(抖动图中边缘阈值)等实物调整。

#### 3.3.2 UI 布局

按工具窗口简单平铺,单通道灰度、左上角 label。

```
┌─ DINO 诊断 ─────────────────────────────────────────┐
│ [参数条]  归一化: per-plane ▾   抖动 τ: ━━●━━  0.15 │
├─ 当前照片预览(512×384)+ 基本信息 ─────────────────┤
├─ CV 诊断图(3 张,128×128 最近邻放大)──────────────┤
│  [失焦]  [抖动]  [一致性]                            │
├─ CV 原始证据(5×3 网格,每图 96×96)─────────────────┤
│  L0:  lap_var  sobel  ent  luma  std                 │
│  L1:  同上                                           │
│  L2:  同上                                           │
├─ DINO(≥2 张)─────────────────────────────────────┤
│  [PCA-RGB 32×32]   [点击 cosine 16×16]               │
└──────────────────────────────────────────────────────┘
```

**交互**:
- 切换照片自动重跑,UI 显示 spinner
- 归一化下拉切换实时重绘,不重跑推理
- 抖动 τ 滑条只重渲抖动图
- 点击 PCA-RGB 某格 → cosine 图显示该格对其余 255 格的相似度

**不做**:半透明 overlay、多照片并排、热力图导出 PNG。

#### 3.3.3 代码落点

| 文件 | 角色 |
|---|---|
| `PhotoViewer/Core/AI/CvHeatmap.cs` **新增** | 基于 `CvGridResult` 推 3 张诊断图 + 归一化工具,纯函数无状态 |
| `PhotoViewer/Core/AI/PatchHeatmap.cs` **新增** | 消费 `patch_tokens`,提供 `ComputePcaRgb` / `ComputeRefCosine`,纯函数 |
| `PhotoViewer/ViewModels/Tools/DinoDebugViewModel.cs` **新增** | 工具页 VM,持有当前 `CvGridResult` / patch 张量 |
| `PhotoViewer/Views/Tools/DinoDebugView.axaml` **新增** | 平铺布局,`ItemsControl` + `Image` 渲染 `WriteableBitmap` |
| [PhotoViewer/Views/Tools/ToolsView.axaml](../PhotoViewer/Views/Tools/ToolsView.axaml) | 首屏加 "DINO 诊断" 按钮 |
| [PhotoViewer/ViewModels/Tools/ToolsViewModel.cs](../PhotoViewer/ViewModels/Tools/ToolsViewModel.cs) | 持有 `DinoDebug` VM,`SyncCurrentFile` 联动 |
| [PhotoViewer/Core/AI/DinoFeatureExtractor.cs](../PhotoViewer/Core/AI/DinoFeatureExtractor.cs) | 扩展 `ExtractDualAsync` 返回 `(cls, patch)` 两路;原 `ExtractAsync` 内部调用它再丢 patch |

### 3.4 T4. DINO 诊断(与 T3 合并为同一工具页)

两张必做热力图:

| 图 | 通道 | 来源 | 要改 ONNX? |
|---|---|---|---|
| PCA-RGB | 3 | patch_tokens 32×32×D PCA → 3 通道,avg pool 到 16×16 | 否 |
| 点击参考点 cosine | 1 | patch_tokens 归一化后与用户点击格的 dot product | 否 |

**可选(二期尾声评估)**:
- CLS-attention rollout(1 通道):需改 ONNX wrapper 增加每层 attention 输出,重新导出并四平台重打包。二期尾声看 PCA-RGB 效果决定是否值得做。

**PCA 实现选型(未定)**:`MathNet.Numerics` vs 手写 SIMD。实现前定。

### 3.5 T5. 相似聚类面板显示控制(新)

原定"加时间差 / 分数直方图 / 调试开关"三项增强 — **本期忽略**,留待后续按实际使用反馈再定。本期 T5 只做显示控制:

**交互**:
- FilterBar 新增"相似聚类"toggle 按钮
  - **竖排布局**(左/右挂载):按钮位于 FilterBar **底部**
  - **横排布局**(顶部挂载):按钮位于 FilterBar **最右端**
  - 形式:**toggle 按钮**(按下/弹起态可视),非普通 push
- 默认**关闭**(面板不显示);状态持久化到 [SettingsModel](../PhotoViewer/Core/Settings/SettingsModel.cs) 的新字段 `SimilarityPanelExpanded`,跨会话保留
- 展开时聚类面板滑入(或直接替换占位),背景色 `#222`,带圆角边框,与缩略图列表形成明确分区

**三态面板内容(由 §3.2 的状态驱动)**:
- **Empty**:面板中央渲染一个大"提取全部"按钮
- **Partial**:上半显示已入库子集聚类,底部一条"还有 N 张未计算 [补齐全部]"
- **Full**:完整聚类列表
- **Indexing**:提取/补齐按钮**原地变进度条**,显示 `提取中 X/Y`

**代码落点**:

| 文件 | 角色 |
|---|---|
| [Views/Main/File/FilterBarView.axaml](../PhotoViewer/Views/Main/File/FilterBarView.axaml) | 新增 toggle 按钮,位置按 `IsVertical` 走 StackPanel 尾或右 |
| [ViewModels/Main/File/FilterBarViewModel.cs](../PhotoViewer/ViewModels/Main/File/FilterBarViewModel.cs) | `IsSimilarityPanelOpen` 属性 + `SimilarityPanelToggled` 事件,初值读 `SettingsModel.SimilarityPanelExpanded` |
| [Views/Main/File/SimilarityListView.axaml](../PhotoViewer/Views/Main/File/SimilarityListView.axaml) | 容器改 `#222` 背景 + 圆角边框;三态子模板 |
| [ViewModels/Main/File/SimilarityPanelViewModel.cs](../PhotoViewer/ViewModels/Main/File/SimilarityPanelViewModel.cs) | 状态机 + 提取按钮命令 + 订阅 `FolderFeatureIndexer.Progress` |
| [Views/Main/File/FileView.axaml](../PhotoViewer/Views/Main/File/FileView.axaml) | 根据 `IsSimilarityPanelOpen` 折叠/展开聚类列表槽位 |

**不做**:
- 分数直方图 / 时间差标签 / 调试开关(移出二期)
- 关闭 toggle 时的动画细节(仅 Collapsed/Visible 切换即可)

---

## 4. 自动化验收(可视化覆盖不了的硬门槛)

下列条目不做 UI,用脚本跑完、数字过门槛即可。目测不能代替数值验收。

| # | 门槛 | 实现 | 何时跑 |
|---|---|---|---|
| 1 | ONNX vs PyTorch cosine ≥ 0.999 | [Training/onnx/verify_onnx_parity.py](../onnx/verify_onnx_parity.py) | 每次导出 ONNX |
| 2 | 四平台真机启动 + `EnsureDualOutputSchema` 不抛 | 人工跑 4 平台 Debug 看日志 | T1 验收 |
| 3 | INT8 量化保持率 cos_min ≥ 0.999 | `Tools/verify_patch_quantization.py` **新增** | T2 中 patch 入库前(若产品决策要入) |
| 4 | CV 网格跨平台一致性 | `Tools/verify_cv_grid_parity.py` **新增** | `CvGridExtractor` 算法调整时 |

前 2 项 T1 前置必须跑。3/4 按需触发。

---

## 5. 验收标准

拿至少 **§0.3 七类场景各 2 张代表样本**(共 14 张)在二期功能上逐张过一遍,同时 Checklist 全部 yes:

### 5.1 相似聚类(T2 + T5)

**显示控制(T5)**:
- [ ] FilterBar toggle 默认关闭,首次启动聚类面板不可见
- [ ] toggle 展开/关闭状态跨会话保留(`SettingsModel.SimilarityPanelExpanded`)
- [ ] 竖排布局下 toggle 位于 FilterBar 底部;横排布局下位于最右端
- [ ] 聚类面板背景 `#222` + 圆角边框,目视上与缩略图列表明显分区
- [ ] Empty 态中央提取按钮点击后**原地变进度条**,完成后按钮消失 + 聚类列表自动刷新
- [ ] Partial 态上半显聚类子集 + 底部"还有 N 张未计算"补齐按钮,点击同样原地变进度条

**聚类质量(沿用原 §5.1 验收)**:
- [ ] 场景 1 同山头不同焦距的兄弟照片落在 Top 10
- [ ] 场景 4 连拍极相似分数 ≥ 0.95
- [ ] 场景 5 孤立抓拍 Top 1 分数明显低于场景 1/2(CLS 确实识别"没兄弟")
- [ ] 场景 3 沿山脊兄弟照片分数在 0.55~0.80 中段,未误入 ≥ 0.9 同场景桶

### 5.2 CV 诊断(T3)

- [ ] 场景 7 夜景抖动在"抖动图"上肉眼可见拖影区域亮、稳定区暗
- [ ] 场景 7 失焦区在"失焦图"上亮,焦内主体暗
- [ ] 场景 2 长焦梯田在"一致性图"上整体暗(原图 1/4 都清)
- [ ] 15 张原始证据图 `PerPlane` 模式下每张都有可辨识结构(不是一片糊)

### 5.3 DINO 诊断(T4)

- [ ] 场景 6 人像 PCA-RGB 主体区与背景区颜色分组明显
- [ ] 场景 2 长焦梯田 PCA-RGB 的梯田纹理带分层
- [ ] 点击主体区参考点,cosine 图在相似语义格子(皮肤/衣服等)显著亮

### 5.4 FFT 去留决策(硬产出)

用 T3 CV 诊断页在场景 7 样本上目测:
- 若"抖动图"已能把拖影拖干净 → **FFT 永久砍掉**
- 若拖影依然漏检且 notebook 里 FFT 明显更准 → **FFT 补做**,引入 FftSharp(只加到 `CvGridExtractor`,写新 `Version`)

决策占位:`(未决,二期完成时更新)`。

### 5.5 全文件夹索引(T2)

- [ ] 万张级文件夹展开 toggle 后点"提取全部",进度条在按钮原地跑到 100%
- [ ] 提取中切换文件夹:旧任务继续后台跑完(本期不可中断);新文件夹展开 toggle 时显示该文件夹自身的三态
- [ ] 完成后相似聚类列表显示的候选数 ≥ 文件夹总数的 95%(剩 5% 是无 EXIF 指纹等边界)
- [ ] 单张失败不阻塞整批
- [ ] Partial 态下"补齐全部"按钮点击后只跑未算过的指纹,不重复跑已入库的

---

## 6. 后续计划

### 6.1 阶段 III. 数据工程与特征分布审计

二期 T2 的批量索引完成后,全库特征分布才算拿到手。阶段 III 做:
- 对全库 CLS cosine 做直方图 + 分位数统计,反推**训练对生成的相似度阈值**(当前拍的 "≥ 0.5 入可比域 / ≥ 0.98 标 tie" 需要数据验证)
- 按拍摄日期/事件切分 train/val/test,保证同次拍摄不跨集
- 如果二期 §5.4 决定保留 FFT,阶段 III 开工前补一次 `CvGridExtractor` 升级 + 历史数据重算

### 6.2 阶段 IV. 基线模型训练

输入三路并行(DINOv3 特征 + CV 网格 + EXIF 向量),**冻结 backbone 只训上层 MLP**。

**待拍事项**(必须有阶段 III 的数据才能定):
- 单头 vs 双头(纯 Pairwise / Pairwise + 绝对美学)
- 损失加权
- 是否引入邻域聚合(`[h_i; g_i]` 拼接 + MLP)

**硬约束(不再动摇)**:
- 不上 Transformer
- patch token 只作为 CV 网格的**加权来源**(主体重 / 背景轻),不直接当美学依据
- 按日期划分,绝不随机划分

### 6.3 阶段 V. 端侧部署最终模型

阶段 IV 训完,导出 ONNX 替换当前 `dinov3_vits16.onnx`。二期已打通导出 → 四平台接入的链路,这一步主要是**换权重**。

**触发条件**:阶段 IV 指标过 §0.4 最低门槛(相似组选优一致率 ≥ 60%、≥3 星召回率 ≥ 80%)。

---

## 7. 二期验证对后续计划的价值

二期每个 Checklist 条目都是对后续阶段的"前置绿灯":

| 二期验收 | 放行哪一阶段 |
|---|---|
| T2 全库批量提取跑通 | 阶段 III 全库特征分布审计的唯一前提 |
| §5.1 相似聚类合理 + 显示控制可用 | 阶段 III 训练对生成的可信度基础 + 用户自助数据覆盖度 |
| §5.2 CV 诊断合理 | CV 网格作为阶段 IV 训练输入的资格认证 |
| §5.3 DINO PCA-RGB 有区分度 | 阶段 IV 选型通过关 — 若 S/16 看不出分区,必须回到模型选型重新评估 B/L |
| §5.4 FFT 决策 | 阶段 III schema 冻结前的最后一道参数决策 |
| T1 四平台自检 | 阶段 V 端侧部署的链路就位 |

**最关键的反向信号**:§5.3 若 PCA-RGB 在 7 类样本上完全看不出语义分组 → 意味着 DINOv3 S/16 对本数据太弱 → 后续所有训练都无救 → 必须回退到模型选型阶段重评 B/L。这是二期守着的一道回退信号。

**最不能跳过的产物**:§5.4 FFT 决策。不决策就不进阶段 III,否则 schema 要 ALTER 两次。

---

## 8. 风险与边界

| 风险 | 等级 | 应对 |
|---|---|---|
| 全文件夹批量提取挤占 UI 线程,打开文件夹卡顿 | 中 | 手动触发后才跑,默认不打扰;桌面半核并行,单线程推理避开 UI 线程 |
| 万张级文件夹批量时间过长,用户等不及 | 中 | 按钮原地进度条显示 X/Y;本期不可中断,切走后台继续跑 |
| 用户切文件夹后以为进度丢了 | 中 | Indexing 状态在后台跑完,但 toggle 展开时只显示当前文件夹三态 — 文档 / UI 提示要明确这点 |
| 工具页每次切换照片重跑推理,目测体验卡顿 | 中 | 默认命中 `DinoFeatureCache`;CV 实时算(桌面 ~200ms 可接受);UI 加 spinner |
| 归一化策略选错导致热力图全糊 | 中 | UI 下拉两档切换,用户自己试 |
| DINO patch 张量常驻内存膨胀 | 低 | 切换照片立即释放旧张量,不做 LRU |
| patch PCA 在超小样本(单张 1024 格)上退化 | 低 | 阶段 III 全库 PCA 再优化 |
| 工具页结果被误当美学评分依据 | **高** | 代码注释标"仅调试可视化";§0.7 作用域澄清已写明 |
| 做完却没拍 §5.4 FFT 决策 | 高 | §5.4 占位"(未决)",每次 review 必查;不决策不进阶段 III |
| 跨模型 embedding 被隐式混用 | 高 | 所有 SQL 强制 `(fingerprint, model_id)` 联合键;禁止跨模型 fallback |

---

## 9. 实施顺序(建议)

**M0 — 前置**(§3.1):
1. 导出 ONNX + 跑 parity
2. 四平台 Debug 启动自检

**M1 — 相似聚类面板显示控制 + 全文件夹批量特征提取**(§3.2 + §3.5):
1. `SettingsModel.SimilarityPanelExpanded` 持久化字段 + FilterBar toggle(竖/横布局两种位置)
2. `SimilarityListView` 背景 `#222` + 圆角边框
3. `FolderFeatureIndexer` + 三态 `SimilarityPanelViewModel` + 按钮原地进度条
4. 万张级文件夹实测(打开 → 展开 toggle → 点提取 → 进度跑完 → 聚类刷新)

**M2 — CV 诊断页骨架**(§3.3):
1. `CvHeatmap` 纯函数 + 归一化两档
2. `DinoDebugViewModel` + `DinoDebugView`(先只放 CV 15 + 3 图)
3. 接入 ToolsView 导航
4. 5 张样本调默认值

**M3 — DINO 诊断页补全**(§3.4):
1. `DinoFeatureExtractor.ExtractDualAsync` 扩展
2. `PatchHeatmap`:`ComputePcaRgb` + `ComputeRefCosine`
3. DinoDebugView 加 DINO 区块 + 点击交互

**M4 — 样本复核 §5.1 显示控制 + 聚类质量**

**M5 — 14 张样本逐项过 §5 Checklist,拍 FFT 决策归档**

**M6(条件触发)— CLS-attention rollout**:若 M3 做完 PCA-RGB + cosine 仍不够区分,改 ONNX wrapper 加 attention 输出,重新导出 + 四平台重打包。

---

## 10. 未明确事项(等用户拍板后补充)

- [ ] **PCA 实现**:`MathNet.Numerics` vs 手写 SIMD,M3 开工前定
- [ ] **单元测试工程**:目前解决方案没有测试工程;`CvHeatmap` / `PatchHeatmap` 是纯函数适合首次引入 xUnit。是否在二期内引入一个 `PhotoViewer.Tests` 工程?
- [ ] **样本集归档位置**:14 张 §0.3 七类代表样本是否入 repo?(建议不入,私人作品;绝对路径列表放 `plans/dinov3-phase2-samples.md` 本地)
- [ ] **热力图渲染性能兜底**:若 `WriteableBitmap` 频繁重建导致 UI 卡顿,降级为预生成一张大拼图 `Bitmap`。默认走前者,实测再说。

