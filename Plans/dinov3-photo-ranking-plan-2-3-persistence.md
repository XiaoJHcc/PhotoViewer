# CV 与 DINO 写库收尾 — 二期持久化

> 状态:草案 v1 / 2026-05-17
> 上游:[Plan-1](dinov3-photo-ranking-plan-1.md) · [Plan-2-0](dinov3-photo-ranking-plan-2-0.md) · [Plan-2-1 wrapup](dinov3-photo-ranking-plan-2-1-wrapup.md) · [Plan-2-2 shake v5](dinov3-photo-ranking-plan-2-2-shake-v5.md)
>
> **本文件的任务**:把现在还没入库的两类数据(CV grid 6 标量 / DINO patch token 1024×384)写进 `photos.db`,同时把 DINO CLS 从 `photos` 单列形态迁到 `photo_features` 纵表,让阶段 III(MLP 训练)可以直接 `JOIN` 三类原始数据 + rating 取训练对。
>
> 本文件**只做持久化收尾**:不再讨论 CV 算法本身(v5 r3 已 12/14 通过 + 用户认可放过 1266/1479,见 Plan-2-2 §3.2),也不再讨论 DINO 模型结构(CLS+patch 双输出 schema 已就绪,见 [DinoFeatureExtractor.cs:55](../PhotoViewer/Core/AI/DinoFeatureExtractor.cs#L55))。
>
> Plan-2-1 §3.3 的"阶段 III 前哨"三条未勾条目由本文件正式接续。

---

## 0. 设计决策(2026-05-17 用户拍板)

下面四条是本期施工的前提,出问题再回头。

### 0.1 CV 是覆盖关系,不并存

每个发布版本只配一套 CV 算法 + 一个 MLP。CV 升级 = 整库失效 = 用户重新提取。表现形式:`photos.cv_grid_spec` 字符串与 [CvGridResult.cs:15](../PhotoViewer/Core/AI/CvGridResult.cs#L15) `CurrentVersion` 不一致直接当 cache miss,与 [DinoFeatureCache.cs:180](../PhotoViewer/Core/AI/DinoFeatureCache.cs#L180) 现有的 `FeatureModel != ModelId` 校验一一对应。

不引入 `cv_grids` 纵表 / 多版本并存。决议依据:本地选片场景单库通常 1000 张量级,重算成本几分钟可接受;为"并行评估两个 CV 版本"的不存在用户需求多养一套 schema 不划算。

### 0.2 DINO 是并存关系,纵表

DINO 模型可能并存多套(vits16 / vitb16 / 未来更换),每套都对应一个 MLP。表设计:`(fingerprint, model_id)` 复合主键,所有读取强制 `WHERE model_id = ?` 过滤,跨模型混用直接被拦。CLS 与 patch 各自一张纵表。

### 0.3 patch token 本期入库

1024×384 float = 1.5 MB / 张 × 1000 张 = 1.5 GB,单库可接受。理由:DINO 推理是 100-300 ms/张的重操作,既然 indexer 已经跑了一遍 ONNX,顺手 dump patch 比阶段 III 训练循环里每张图重推一遍便宜得多。

### 0.4 派生量全部现算

锐度热力图、抖动矢量场、刚体拟合、参考点 cosine、PCA-RGB — 全部不入库,从原始数据现算。这些都跟阈值常量耦合(Plan-2-2 §1.5 / §1.8 还在迭代窗口),入库等于把阈值冻进数据库,改一次失效一次。

但 v5 r3 的 `block_contrast`(luma p98-p2)是个例外:它是块级**纯像素统计量**,跟阈值无关 — 升标量进 `CvGridResult` 第 7 标量(`cv_grid_v4_structtensor` → `cv_grid_v5_contrast`),让 indexer 写库的数据等于诊断页现算的数据,**UI 看图 ≡ DB 重画图**,阶段 III MLP 训练吃到的 g_i 也是完整的 7 标量。

`photos.heatmap` 预留列(从未启用)本期一并删除,清理 schema。

---

## 1. Schema 终态

### 1.1 `photos` 主表(瘦身后)

```sql
CREATE TABLE photos (
    fingerprint     TEXT PRIMARY KEY,
    filename_noext  TEXT NOT NULL,
    capture_time    TEXT,
    capture_subsec  TEXT,
    rating          INTEGER,                 -- 预留,本期仍以文件实读为准
    cv_grid         BLOB,                    -- 6144 float (32×32×6) 小端
    cv_grid_spec    TEXT,                    -- = CvGridResult.CurrentVersion
    cv_computed_at  TEXT,                    -- ISO-8601 UTC
    updated_at      TEXT NOT NULL
);
CREATE INDEX idx_photos_capture_time ON photos(capture_time);
CREATE INDEX idx_photos_filename     ON photos(filename_noext);
```

变化:
- **删除** `feature_vector` / `feature_model` / `feature_computed_at`(迁到 `photo_features` 纵表)
- **删除** `heatmap`(预留未启用,派生量决议 §0.4)
- **新增** `cv_grid` / `cv_grid_spec` / `cv_computed_at`(覆盖式 §0.1)

### 1.2 `photo_features` — DINO CLS 纵表

```sql
CREATE TABLE photo_features (
    fingerprint   TEXT NOT NULL,
    model_id      TEXT NOT NULL,
    cls_vector    BLOB NOT NULL,             -- 384 float 小端
    computed_at   TEXT NOT NULL,
    PRIMARY KEY (fingerprint, model_id),
    FOREIGN KEY (fingerprint) REFERENCES photos(fingerprint) ON DELETE CASCADE
);
CREATE INDEX idx_features_model ON photo_features(model_id);
```

`model_id` 来源:[DinoModelResources.cs:52](../PhotoViewer/Core/AI/DinoModelResources.cs#L52),当前 `dinov3_vits16_f32_518_v1`。

### 1.3 `photo_patches` — DINO patch token 纵表(本期新增)

```sql
CREATE TABLE photo_patches (
    fingerprint   TEXT NOT NULL,
    model_id      TEXT NOT NULL,
    patch_tokens  BLOB NOT NULL,             -- 1024×384 float 小端 = 1572864 bytes
    computed_at   TEXT NOT NULL,
    PRIMARY KEY (fingerprint, model_id),
    FOREIGN KEY (fingerprint) REFERENCES photos(fingerprint) ON DELETE CASCADE
);
CREATE INDEX idx_patches_model ON photo_patches(model_id);
```

存储布局:与 [DinoFeatureExtractor.RunInferenceDual](../PhotoViewer/Core/AI/DinoFeatureExtractor.cs#L111) 输出顺序一致(token-major,每个 token 384 维连续)。

### 1.4 三类数据写入条件汇总

| 数据 | 写入入口 | 何时失效 | 大小/张 |
|---|---|---|---|
| `photos.cv_grid` | `FolderFeatureIndexer` 一轮扫描 / DINO 诊断页现算后顺手写 | `cv_grid_spec` 不匹配 | 24 KB |
| `photo_features.cls_vector` | 同上 | `model_id` 不匹配 | 1.5 KB |
| `photo_patches.patch_tokens` | 同上(只在 indexer 调用 `ExtractDualAsync(includePatches: true)` 时产出) | `model_id` 不匹配 | 1.5 MB |

---

## 2. PhotoDatabase 改造

[PhotoDatabase.cs](../PhotoViewer/Core/Database/PhotoDatabase.cs) 需要做这几件事。

### 2.1 EnsureSchema 重写

- 主表 `photos` 重建为 §1.1 终态(下方"迁移路径"决定是 `ALTER` 还是重建)
- 新建 `photo_features` / `photo_patches` 两张纵表

### 2.2 删除的 API

- `WriteFeatureVectorAsync(input, fp, vector, modelId)` → 取代为 `WriteFeatureAsync`(写纵表)
- `PhotoCacheRecord.FeatureVector` / `FeatureModel` / `FeatureComputedAt` / `Heatmap` 字段移除

### 2.3 新增的 API

```csharp
// CV grid 单列覆盖
public static Task WriteCvGridAsync(PhotoFingerprintInput input, string fp, byte[] gridBlob, string spec);
public static Task<(byte[] Blob, string Spec)?> ReadCvGridAsync(string fp);

// DINO 纵表
public static Task WriteFeatureAsync(PhotoFingerprintInput input, string fp, string modelId, byte[] clsBlob);
public static Task<byte[]?> ReadFeatureAsync(string fp, string modelId);

public static Task WritePatchesAsync(string fp, string modelId, byte[] patchBlob);
public static Task<byte[]?> ReadPatchesAsync(string fp, string modelId);

// 一次写入(供 indexer):事务内同时落 photos / photo_features / photo_patches / cv_grid
public static Task WriteIndexedAsync(
    PhotoFingerprintInput input,
    string fp,
    string modelId,
    byte[] clsBlob,
    byte[] patchBlob,
    byte[] cvGridBlob,
    string cvSpec);
```

`WriteIndexedAsync` 是 indexer 主路径,事务保证三表同步;DINO 诊断页或 `DinoFeatureCache` 单步触发时仍用单表 API。

### 2.4 身份字段 Upsert 复用

DINO 纵表与 patch 纵表插入前都需要确保 `photos` 主行存在(外键)。复用现有 `UpsertIdentityAsync` 模式,在 `WriteIndexedAsync` / `WriteFeatureAsync` 内部先 `INSERT OR IGNORE` 一行 photos。

---

## 3. FolderFeatureIndexer 合并改造

[FolderFeatureIndexer.cs](../PhotoViewer/Core/AI/FolderFeatureIndexer.cs) 现在只跑 DINO CLS,改造后**一轮扫描完成 DINO CLS + patch + CV**。

### 3.1 解码两路

DINO 走 560 短边的 `ThumbnailService`,CV 走原始分辨率的 `BitmapLoader`(Plan-2-1 §1.6 锁定)。**同一文件在同一组任务里完成两路解码**,不共用 bitmap。

### 3.2 推理与提取顺序

每个指纹组(代表文件)按以下顺序:
1. `ThumbnailService.GetThumbnailAsync(file, 560)` → DINO 输入
2. `_inferSemaphore` 闸内调用 `DinoFeatureExtractor.ExtractDualAsync(thumb, includePatches: true)` → 一次推理同时拿 CLS 与 patch(本期开关默认开)
3. `BitmapLoader.GetBitmapAsync(file)` → CV 输入(原始分辨率,共享 LRU)
4. 调用 `CvGridExtractor.ExtractAsync(fullBitmap)` → `CvGridResult` → `Encode()`
5. `PhotoDatabase.WriteIndexedAsync(...)` 一次性事务写入

### 3.3 跳过判定升级

`IsAlreadyIndexedAsync` 现在只看 `feature_vector`,改为**三路同时齐备才算已入库**:
```
photo_features.cls_vector(model_id 命中)
∧ photo_patches.patch_tokens(model_id 命中)
∧ photos.cv_grid + cv_grid_spec(spec 命中)
```
任意一路缺失/版本不匹配 → 该组进入处理;按需补齐(已存在的不重写)。
新增私有方法 `EvaluateMissingPartsAsync(fp) → (needCls, needPatch, needCv)`,在第 2 / 3 步前用于跳过对应解码与计算。

### 3.4 并发模型不变

桌面端 `ProcessorCount/2` 解码并发 / 移动端单线程;ONNX 推理仍走 `_inferSemaphore` 单线程闸。CV 计算是纯托管 CPU 密集,与解码同信号量足够,不再加额外限流。

### 3.5 进度仍按指纹组

`IndexProgress` 结构与事件保持不变(对消费方 `SimilarityPanelViewModel` 透明)。一组完成 = 该组三类数据全部齐备,中途任意一步失败仍按"该组失败"统计。

---

## 4. 消费方改造

### 4.1 [DinoFeatureCache.cs](../PhotoViewer/Core/AI/DinoFeatureCache.cs)

- `ReadFromDatabaseAsync` 改读 `photo_features`(`ReadFeatureAsync(fp, modelId)`)
- `ComputeAndStoreAsync` 写库改 `WriteFeatureAsync`(只写 CLS,patch 由 indexer 路径独占)
- 进程缓存 `_memoryCache` 不动;`PutMemoryCache` 接口不动
- **不**在 cache miss 路径上拉 patch:DINO 诊断页与 indexer 都不依赖 cache 的 patch 路径

### 4.2 [SimilarityPanelViewModel.cs](../PhotoViewer/ViewModels/Main/File/SimilarityPanelViewModel.cs)

`EvaluateUnindexedAsync` 第 261 行的判定改为调用 §3.3 新加的 `EvaluateMissingPartsAsync`,任一缺失即计为"未提取"。文案不变(仍是"提取全部 / 补齐全部")。

### 4.3 DINO 诊断页 [DinoDebugViewModel.cs](../PhotoViewer/ViewModels/Tools/DinoDebugViewModel.cs)

切图时:
- **patch token 走读库快路径**:`ReadPatchesAsync(fp, modelId)` 命中则解码 BLOB,跳过 DINO 推理直接喂 PCA-RGB / 参考点 cosine(patch token 是 DINO 路径里最贵的产出,1.5 MB)
- **CV 也走读库快路径**:`ReadCvGridAsync(fp)` + spec 一致 → 解码 7 标量(含 `block_contrast`)直接喂 `CvHeatmap`。v5 起 contrast 已是 result 第 7 标量,UI 看图与 indexer 写库时算的图字节级等价 — 改算法只需 bump `CvGridResult.CurrentVersion`,阈值常量从不入库
- **未命中现算 + 不回写**:现算后**不**回写库 — 回写交给 indexer 主路径,避免诊断页弹回写造成事务交错
- **图像尺寸源**:diagonal 仍要 CV 大图的 PixelSize 来算(归一化与 indexer 写库时一致),所以 cache 命中也要 `BitmapLoader.GetBitmapAsync` 解码 — 但跳过了 100-300 ms 的 CV 计算,且 LRU 通常已被主视图填充

### 4.4 [Tools/CvDebugTool/](../Tools/CvDebugTool/)

不动。命令行工具的语义就是"对单张照片现算并输出 PNG/报告",不需要触碰 photos.db。

### 4.5 AI 设置页:清除特征数据库按钮(开发者用)

在 [SettingsViewModel.AI.cs](../PhotoViewer/ViewModels/Settings/SettingsViewModel.AI.cs) 与 [AiSettingsView.axaml](../PhotoViewer/Views/Settings/AiSettingsView.axaml) 末尾添加一项:

- 按钮文案:"清除特征数据库"
- 副作用:删除 `photos.db`(以及 `-wal` / `-shm` 旁路文件)→ `PhotoDatabase.Initialize()` 重建空库 → `DinoFeatureCache.InvalidateAll()` 清空进程内存缓存 → 触发 `SimilarityPanelViewModel.EvaluateUnindexedAsync` 刷新面板(UnindexedCount 恢复为文件总数)
- 二次确认 dialog:"确定清除所有 DINO 与 CV 特征?该操作不可撤销"
- 不需要重启应用;按钮就绪即生效

PhotoDatabase 侧新增:

```csharp
public static async Task DeleteDatabaseAsync();
// 实现:_initialized 置 false → 锁内删 3 个文件 → 再次 Initialize()
```

这条按钮在正式发布前可考虑下线;现阶段作为开发者调试入口长期保留(开关位置直接放 AI 分页底部,不必藏在隐藏路径)。

---

## 5. 迁移路径

### 5.1 检测到旧 schema 直接重建

软件尚未对外发布,v1 库只存在于开发者本地测试设备,不需要保留任何旧数据。`PhotoDatabase.Initialize()` 启动检测:

```
IF photos.db 存在 AND (photos has column feature_vector OR photos has column heatmap):
    删除 photos.db(以及 -wal / -shm 旁路文件)
    按 §1 schema 全新创建
```

判定信号选 `feature_vector` 或 `heatmap` 任一存在 — 这两列在新 schema 里都不存在,作为"旧库"指纹足够稳健。

### 5.2 用户感知

- 测试设备上的所有提取数据全部清空,下次打开文件夹后相似聚类面板回到 Empty 状态,需要重新点"提取全部"
- 无对外发布版本的兼容负担

### 5.3 不写迁移脚本

Plan-2-1 §3.3 提到的 `Tools/migrate_features_to_longtable.py` 与早期讨论过的在线 ALTER 路径都不再需要。该条目原地归档。

---

## 6. 验收 checklist

### 6.1 代码落地

- [ ] [PhotoDatabase.cs](../PhotoViewer/Core/Database/PhotoDatabase.cs):§1.1 / §1.2 / §1.3 三表 schema 落地,启动时检测到旧 schema 自动删库重建(§5.1)
- [ ] [PhotoDatabase.cs](../PhotoViewer/Core/Database/PhotoDatabase.cs):§2.3 五个新 API 实现,`WriteIndexedAsync` 走单事务
- [ ] [PhotoDatabase.cs](../PhotoViewer/Core/Database/PhotoDatabase.cs):`DeleteDatabaseAsync()` 实现 — 关闭连接 → 删 `photos.db` / `-wal` / `-shm` → 清空内存缓存 → 重新 `Initialize()`(§4.5 联动)
- [ ] [DinoFeatureCache.cs](../PhotoViewer/Core/AI/DinoFeatureCache.cs):读写改纵表 API,行为对外不变;新增 `InvalidateAll()` 清空 `_memoryCache` 与 `_inflight`,供数据库重置时调用
- [ ] [FolderFeatureIndexer.cs](../PhotoViewer/Core/AI/FolderFeatureIndexer.cs):§3.2 五步顺序合并实现,`EvaluateMissingPartsAsync` 用于跳过判定与按需补齐
- [ ] [SimilarityPanelViewModel.cs](../PhotoViewer/ViewModels/Main/File/SimilarityPanelViewModel.cs):未提取统计改为三路齐备判定
- [ ] [DinoDebugViewModel.cs](../PhotoViewer/ViewModels/Tools/DinoDebugViewModel.cs):优先读库,未命中现算不回写
- [ ] AI 设置页"清除特征数据库"按钮接通 `PhotoDatabase.DeleteDatabaseAsync` + 刷新相似聚类面板状态(§4.5)
- [ ] 编译零 warning;Windows Debug 跑通切图无崩溃
- [ ] macOS / iOS 各跑一次"打开新文件夹 → 提取全部 → 切图浏览"全链路无异常

### 6.2 行为验证(手动 / 14 张样本)

- [ ] 干净库首次启动 → schema 三表创建成功
- [ ] 启动时检测到旧 schema(`feature_vector` / `heatmap` 列存在)→ 自动删库重建为新 schema
- [ ] 文件夹包含同次曝光 RAW+HIF 组 → indexer 进度按组推进,组内只跑一次推理 / 一次 CV
- [ ] 提取完成后:DINO 诊断页 PCA-RGB / 参考点 cosine 与 CV 锐度图 / 抖动矢量场 与 indexer 之前完全一致(无回归)
- [ ] 改动 [CvGridResult.cs:15](../PhotoViewer/Core/AI/CvGridResult.cs#L15) `CurrentVersion` 字符串后重启 → 相似聚类面板 Full 退化为 Partial,提示需补齐 CV(DINO 部分仍 Full)
- [ ] 改动 [DinoModelResources.cs:52](../PhotoViewer/Core/AI/DinoModelResources.cs#L52) `ModelId` 后重启 → 三路均退化为 Partial(model_id 不匹配)
- [ ] AI 设置页点击"清除特征数据库"→ 二次确认 → 数据库文件消失并重建为空 → 相似聚类面板退回 Empty 状态;无需重启应用

### 6.3 阶段 III 接口冒烟(可推迟到阶段 III 开工日)

- [ ] 写一条 SQL 验证 `JOIN`:能在一次查询里取到 `(fingerprint, cls_vector, patch_tokens, cv_grid, rating)` 五元组(rating 为空允许),作为 MLP 训练 dataloader 的最小 SQL
- [ ] 1000 张库 `JOIN` 性能 < 5 秒(走主键索引,该是 O(N))

---

## 7. 远期问题预判(出问题再做)

| # | 问题 | 触发 | 预案 |
|---|---|---|---|
| P1 | patch token 1.5 MB / 张让数据库膨胀超用户预期 | 某次反馈"千张库占 2 GB,单文件夹 200 张就吃掉手机一半空间" | 改存 fp16(0.75 MB)或 int8 量化(0.4 MB);schema 加 `dtype` 列 |
| P2 | DINO 多模型并存场景下,旧模型条目不会自动清理 | `photo_features` / `photo_patches` 行数比 `photos` 大很多 | 设置页加"清理非当前模型"按钮 |
| P3 | CV 升级后所有用户都要重提一遍,体感成本 | 下一次 CV bump 撞到大库用户 | 升级时提供"后台逐步重算"模式(不 block 浏览),不阻塞 UI |
| P4 | rating 列仍以文件实读为准,与库存的元数据脱节 | 用户跨设备同步评分混乱 | 把 rating 写入并以库为准,文件读作为初始填充源 |
| P5 | 1000 张以上扫描时 `EvaluateMissingPartsAsync` 慢 | 切文件夹后面板长时间没反应 | 改为单次 `SELECT IN(...)` 批量查询 + 内存哈希比对,而不是 per-fp |

---

## 8. Go / No-Go

**go**:§6.1 + §6.2 全部打钩,旧库自动迁移路径双向(干净库 / v1 库)验证通过 → 进入阶段 III(MLP 训练数据准备)。
**no-go**:§5.1 在线 ALTER 在任一真实用户库上失败,或迁移后 DINO 诊断页 / 相似聚类有可见回归 → 回 §2 / §3 修。

阶段 III / IV / V / VI / VII 的路径由 [Plan-1 §B/C/D/E/F](dinov3-photo-ranking-plan-1.md) 锁定,本文件不重复。
