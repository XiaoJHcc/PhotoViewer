# 数据契约 Data Contract

> 描述训练数据集库的物理位置、schema、与产品库的对齐关系、版本化规则。字段以 [DatasetDatabase.cs](../DatasetBuilder/DatasetDatabase.cs) 的实际建表语句为准,本文档随其同步更新,不重复维护第二份 schema 定义。

## 位置

数据集库(SQLite 文件)与原始照片本体都在**仓外**,不入库、不受仓库清理影响,便携:

- 默认落盘位置:`D:\PhotoDB\dataset\photos_dataset.db`。
- 每次 `DatasetBuilder` 运行的清单(见 [DatasetBuilder/manifest.sample.json](../DatasetBuilder/manifest.sample.json))可以指定任意 `dbPath`——按拍摄批次拆分成多个独立库,或合并写入同一个库,由使用者决定。
- 覆盖率报告落在 `<dbPath>.coverage.md`,与库文件同目录。

## 与产品 `photos.db` 的对齐关系

数据集库由 [DatasetDatabase](../DatasetBuilder/DatasetDatabase.cs) 门面管理,是与产品 `PhotoDatabase`(见 [PhotoViewer/Core/Database/CLAUDE.md](../../PhotoViewer/Core/Database/CLAUDE.md))**完全解耦的独立库**,但刻意让核心表名/列名与产品对齐,方便 Python 侧沿用同一套读法。区别:

1. **路径由清单指定**,不落 AppData——入库后不再触碰原始照片文件。
2. `photos` 表在产品列之外**加了训练专用列**:`is_retouched`(精修命中)/ `source_rel_path`(相对路径)/ `event_label`(事件标签)/ `subject_label`(题材标签)/ `formats`(该指纹合一的格式集合)。
3. `photo_features` 每个指纹可以存**两行**——原片 CLS(`model_id`)+ 增强 CLS(`model_id` 带 `+clhe.. ycc..` 后缀),支撑探针做"仅原片 / 仅增强 / 多视图"对比,产品库只有一行。
4. **迁移只做加列(additive)**——数据集是精选产物,绝不会像产品库那样在 schema 不匹配时删库重建;新增列走 `ALTER TABLE ADD COLUMN`,历史数据保留。
5. `dataset_meta` 键值表记录模型 id / 增强参数 / CV 版本 / 预处理分辨率等,保证同一批数据可复现追溯。

## 三表 schema

### `photos`(身份 + EXIF/rating + CV grid + 训练来源标签)

| 列 | 类型 | 说明 |
|---|---|---|
| `fingerprint` | TEXT PK | 同次曝光的 RAW/HIF/JPG 共享同一指纹 |
| `filename_noext` / `capture_time` / `capture_subsec` | TEXT | 指纹身份三要素 |
| `rating` | INTEGER | 星级(COALESCE 写入,不会被空值覆盖抹掉) |
| `focal_length` / `aperture` / `shutter_speed` / `crop_factor` | REAL | EXIF 快照,原始值(等效焦距/光圈不入库,训练脚本自行换算) |
| `cv_grid` | BLOB | CV 网格 7 标量(含 `block_contrast`),编码方式见 `CvGridResult.Encode()` |
| `cv_grid_spec` | TEXT | CV 算法版本串,与当前 `CvGridResult.CurrentVersion` 不匹配即视为该项缺失需要重算 |
| `cv_computed_at` / `cv_image_width` / `cv_image_height` | TEXT/INTEGER | CV 计算时间戳与原图分辨率(diagonal 等派生量靠这俩推) |
| `is_retouched` | INTEGER | 精修命中(1/0);清单未提供精修清单时全 NULL(未知,不占位) |
| `source_rel_path` / `event_label` / `subject_label` / `formats` | TEXT | 来自入库清单的来源标签,每次入库覆盖刷新 |
| `updated_at` | TEXT | 最近写入时间(UTC ISO) |

### `photo_features`(CLS 纵表,`(fingerprint, model_id)` 联合主键)

| 列 | 类型 | 说明 |
|---|---|---|
| `fingerprint` | TEXT | 外键 → `photos.fingerprint`,级联删除 |
| `model_id` | TEXT | 原片 CLS 用 `DinoModelResources.ModelId`;增强 CLS 用其后缀变体(`+clheX.Xycc X.X`) |
| `cls_vector` | BLOB | 384 维 float32,L2 归一化,`float[]` 原始字节序编码 |
| `computed_at` | TEXT | 计算时间(UTC ISO) |

### `photo_patches`(patch token 纵表,同主键结构)

| 列 | 类型 | 说明 |
|---|---|---|
| `fingerprint` / `model_id` | TEXT | 同上;patch 只存原片一行,不存增强路 |
| `patch_tokens` | BLOB | 1024×384 float32,token-major 排布(`PatchTokenCount × FeatureDim`) |
| `computed_at` | TEXT | 计算时间 |

### `dataset_meta`(键值契约表)

`(key, value, updated_at)`,`INSERT ... ON CONFLICT DO UPDATE` 覆盖式写入。当前写入的键(见 [Program.cs](../DatasetBuilder/Program.cs) `RunPipelineAsync`):`dino_model_id` / `enhanced_model_id` / `clip_factor` / `saturation_scale` / `color_model` / `cv_spec` / `dino_input_size` / `enhance_resolution`。

## 版本化 = 改算法即整库失效

`model_id`(DINO)与 `cv_grid_spec`(CV)是**唯一**的缓存有效性判据:

- 改 DINO 预处理/模型权重 → bump `DinoModelResources.ModelId` → 旧行的 `model_id` 不匹配,`EvaluateMissingAsync` 判定缺失,重新提取写入新行(旧行不删,历史可追溯)。
- 改增强算法参数(对比度裁剪、饱和度缩放、色彩模型)→ 增强 `model_id` 后缀自动随 `ImageEnhancer.ClipFactor` / `SaturationScale` 变化 → 同样触发按需重算,不会用旧算法产物静默污染新实验。
- 改 CV 网格算法 → bump `CvGridResult.CurrentVersion` → `cv_grid_spec` 不匹配同理触发重算。

**齐备度(GATE)判定分母是可解码组数**,不是全部指纹组——仅 RAW 且当前无解码器的组只写身份/EXIF/来源标签,不提特征,属合法缺失,不计入失败(见 [CoverageReport.cs](../DatasetBuilder/CoverageReport.cs))。

## 消费方

Python 探针([../probes/](../probes/))只通过上述表结构契约读取数据,不依赖任何 C# 代码;只要表名/列名/版本化规则不破坏性变更,探针脚本不需要跟着改。
