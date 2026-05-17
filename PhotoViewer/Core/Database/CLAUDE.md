# Core/Database — 照片缓存数据库

> 模块内手册。批量索引/单图懒加载/清库的端到端流程在根 `CLAUDE.md` §5 关键流程。

`namespace PhotoViewer.Core.Database`

| File | 模块 | Responsibility |
|---|---|---|
| [PhotoDatabase.cs](PhotoDatabase.cs) | 缓存数据库门面 | SQLite (`photos.db`) 静态门面,与 `SettingsService` 共用数据目录。三表 schema(Plan-2-3):**`photos`** 身份字段 + `cv_grid` BLOB + `cv_grid_spec` TEXT(覆盖式,版本 bump 即作废)+ `cv_computed_at` + `cv_image_width` / `cv_image_height`(CV 实际解码尺寸,供抖动徽标服务算 diagonal,无需重新解码)+ `rating`(预留);**`photo_features`** DINO CLS 纵表 `(fingerprint, model_id)` 主键 + `cls_vector` BLOB;**`photo_patches`** DINO patch token 纵表同主键 + `patch_tokens` BLOB(1024×384 float)。读写 API:`WriteIndexedAsync`(indexer 主路径,单事务三表,可携带尺寸)、`WriteFeatureAsync` / `ReadFeatureAsync` / `WritePatchesAsync` / `ReadPatchesAsync` / `WriteCvGridAsync` / `ReadCvGridAsync`(返回 `CvGridRecord` 含尺寸)/ `EvaluateMissingPartsAsync` / `DeleteDatabaseAsync`(供 AI 设置页"清除特征数据库"按钮)。启动时检测旧 schema(`feature_vector` / `heatmap` 列残留,或缺 `cv_image_width/height` 列)自动删库重建。 |
| [PhotoFingerprint.cs](PhotoFingerprint.cs) | 指纹计算 | 三字段规范化 SHA1：`filename_noext` + `DateTimeOriginal`(秒, UTC ISO-8601) + `SubSecTimeOriginal`(3 位毫秒)。同一次曝光的 RAW/HIF/JPG 字节级字段一致 → 同指纹；高速连拍由 SubSec 毫秒区分；文件名编号循环由日期区隔。验证工具见 `ExifTestTool fp <folder>`。 |
