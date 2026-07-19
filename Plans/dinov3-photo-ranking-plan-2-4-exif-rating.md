# EXIF 与 Rating 入库收尾 — 二期最终基建

> 状态：✅ 已落地 / 2026-05-24
> 上游：[Plan-2-3 persistence](dinov3-photo-ranking-plan-2-3-persistence.md)
>
> **本文件的任务**：在 `photos` 表预留 EXIF 拍摄参数列 + 在现有代码路径上顺手写入 rating 与 EXIF，让阶段 III（MLP 训练）的批量工具能一条 SQL 取齐全部训练输入。
>
> 本文件**极轻量**：不做批量补扫工具（那是 Plan-3 第一步），不做向量化算法设计（那是训练 pipeline 内部的事），不改产品行为（rating 仍以文件为准）。

---

## 0. 设计决策

### 0.1 Rating：DB 是副本，文件是主

产品行为不变：用户看到的星级始终从 XMP/EXIF 文件实读。`photos.rating` 只是训练用的快照副本，在以下时机顺手同步：
- 用户通过 app 写星级时（`MainViewModel.SetRatingAsync` 成功后）
- indexer 扫描时如果 `ImageFile.Rating` 已有值

不做"从 DB 读 rating 显示给用户"的路径。

### 0.2 EXIF：存原始值，不存等效值

存 `focal_length`（实际焦距 mm）、`aperture`（实际光圈 f-number）、`shutter_speed`（秒）、`crop_factor`（CMOS 倍率）。等效焦距 = `focal_length × crop_factor`，等效光圈 = `aperture × crop_factor`，由消费方（训练脚本）自行计算。

`crop_factor` 来源：`ExifData.EquivFocalLength / ExifData.FocalLength`；若 EquivFocalLength 缺失则写 NULL（训练脚本按 1.0 兜底或跳过）。

### 0.3 不做批量补扫

现有照片库的 EXIF/rating 批量入库由 Plan-3 的开发者专用工具负责。Plan-2-4 只保证"新跑 indexer 的照片"能顺手带上这些字段。

---

## 1. Schema 变更

### 1.1 `photos` 表新增列

```sql
ALTER TABLE photos ADD COLUMN focal_length   REAL;    -- 实际焦距 mm
ALTER TABLE photos ADD COLUMN aperture       REAL;    -- 实际光圈 f-number
ALTER TABLE photos ADD COLUMN shutter_speed  REAL;    -- 快门速度 秒
ALTER TABLE photos ADD COLUMN crop_factor    REAL;    -- CMOS 倍率
```

`rating` 列已存在（Plan-2-3 §1.1），本期不加列，只开始写入。

### 1.2 迁移策略

沿用 Plan-2-3 §5.1 的"检测旧 schema 直接重建"模式。`PhotoDatabase.Initialize()` 启动时检测：

```
IF photos 表缺少 focal_length 列:
    删除 photos.db（以及 -wal / -shm）
    按新 schema 全新创建
```

判定信号选 `focal_length` 列不存在 — 这是本期新增的列，旧库必然缺失。与 Plan-2-3 的 `feature_vector` 检测互不冲突（两者都触发重建，先命中哪个都行）。

软件尚未对外发布，重建无兼容负担。

---

## 2. 写入路径

### 2.1 Rating 写入（顺手同步）

**触发点**：[MainViewModel.SetRatingAsync](../PhotoViewer/ViewModels/Main/MainViewModel.cs) 在 `XmpWriter.WriteRatingAsync` 成功后，顺手调用 `PhotoDatabase.UpdateRatingAsync(fingerprint, rating)`。

```csharp
// PhotoDatabase 新增
public static Task UpdateRatingAsync(string fingerprint, int rating);
```

实现：`UPDATE photos SET rating = $r, updated_at = $t WHERE fingerprint = $fp`。若该 fingerprint 不存在（用户还没跑过 indexer），静默跳过（不 INSERT 空行）。

### 2.2 EXIF 写入（indexer 顺手带）

**触发点**：[FolderFeatureIndexer.RunAsync](../PhotoViewer/Core/AI/FolderFeatureIndexer.cs) 在处理每个指纹组时，代表文件的 `ExifData` 已经被 `ExifLoader` 解析过（`ImageFile.ExifData` 缓存）。在调用 `WriteIndexedAsync` 时把 EXIF 字段一并传入。

**数据来源**（均来自 [ExifData](../PhotoViewer/Core/Exif/ExifModels.cs)）：

| DB 列 | ExifData 字段 | 转换 |
|---|---|---|
| `focal_length` | `FocalLength` | `Rational.ToDouble()` |
| `aperture` | `Aperture` | `Rational.ToDouble()` |
| `shutter_speed` | `ExposureTime` | `Rational.ToDouble()`（秒） |
| `crop_factor` | `EquivFocalLength / FocalLength` | 两者都有值时算商；否则 NULL |
| `rating` | `ImageFile.Rating` | 直接取（0~5，0 表示未评） |

### 2.3 WriteIndexedAsync 扩展

现有签名已有 `PhotoFingerprintInput input`，其中包含身份字段。新增一个可选参数结构体传递 EXIF + rating：

```csharp
public readonly struct ExifSnapshot
{
    public double? FocalLength { get; init; }
    public double? Aperture { get; init; }
    public double? ShutterSpeed { get; init; }
    public double? CropFactor { get; init; }
    public int? Rating { get; init; }
}

public static Task WriteIndexedAsync(
    PhotoFingerprintInput input,
    string fingerprint,
    string modelId,
    byte[]? clsBlob,
    byte[]? patchBlob,
    byte[]? cvGridBlob,
    string? cvSpec,
    int? cvImageWidth,
    int? cvImageHeight,
    ExifSnapshot? exif = null);  // 本期新增
```

事务内 `INSERT OR REPLACE` 时，若 `exif` 非 null，同时写入 5 个列；否则保持原值不覆盖（`COALESCE` 模式）。

---

## 3. 不做的事

| 事项 | 归属 |
|---|---|
| 批量补扫现有照片库的 EXIF/rating | Plan-3 第一步（开发者专用工具） |
| EXIF 向量化（log 归一化、拼向量） | Plan-3 训练 pipeline |
| 跨图归一化参数（z-score / percentile） | Plan-3 训练 pipeline |
| 从 DB 读 rating 显示给用户 | 不做（产品以文件为准） |
| Sony AF 对焦点坐标入库 | 远期评估（MakerNote 覆盖率不确定） |
| `ExifSnapshot` 的 C# 向量化方法 | Phase F（推理端部署时移植 Python 逻辑） |

---

## 4. 验收 checklist

### 4.1 代码落地

- [x] `photos` 表 schema 含 `focal_length` / `aperture` / `shutter_speed` / `crop_factor` 四列
- [x] 启动时检测旧 schema（缺 `focal_length` 列）自动删库重建
- [x] `PhotoDatabase.UpdateRatingAsync` 实现
- [x] `ExifSnapshot` 结构体 + `WriteIndexedAsync` 扩展参数
- [x] `FolderFeatureIndexer` 在写库时传入 `ExifSnapshot`（从 `ImageFile.ExifData` 取值）
- [x] `MainViewModel.SetRatingAsync` 成功后调用 `UpdateRatingAsync`
- [x] 编译零 warning；macOS Debug 跑通

### 4.2 行为验证

- [ ] 新文件夹跑"提取全部"后，`SELECT focal_length, aperture, shutter_speed, crop_factor, rating FROM photos LIMIT 5` 有非 NULL 值
- [ ] 手动给一张照片打 3 星 → `photos.rating` 同步更新为 3
- [ ] 缺少 EquivFocalLength 的照片（如手动镜头）→ `crop_factor` 为 NULL，其余字段正常
- [x] 旧库（无 `focal_length` 列）启动后自动重建，相似聚类面板退回 Empty 状态

### 4.3 Plan-3 接口冒烟

- [ ] 一条 SQL 能取齐训练五元组 + EXIF：
  ```sql
  SELECT p.fingerprint, p.rating, p.focal_length, p.aperture,
         p.shutter_speed, p.crop_factor, p.cv_grid,
         f.cls_vector, pt.patch_tokens
  FROM photos p
  JOIN photo_features f ON p.fingerprint = f.fingerprint
  JOIN photo_patches pt ON p.fingerprint = pt.fingerprint
  WHERE f.model_id = 'dinov3_vits16_f32_518_v1'
    AND pt.model_id = 'dinov3_vits16_f32_518_v1'
    AND p.rating IS NOT NULL
  ```
- [ ] 上述查询在 1000 行库上 < 3 秒

---

## 5. Go / No-Go

**go**：§4.1 + §4.2 全部打钩 → 二期基建正式收尾，进入 Plan-3（MLP 训练数据准备）。

§4.3 可推迟到 Plan-3 开工日验证（依赖批量工具先把数据灌进去）。
