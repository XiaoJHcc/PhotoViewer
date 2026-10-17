# Core/Database — 照片缓存数据库

> 模块内手册。批量索引/单图懒加载/清库的端到端流程在根 `AGENTS.md` §5 关键流程。

`namespace PhotoViewer.Core.Database`

`PhotoDatabase.Initialize(databasePath)` 可在首次使用前指定独立测试路径；生产默认仍为标准数据目录，初始化后禁止更换路径，schema/指纹口径不变。

| File | 模块 | Responsibility |
|---|---|---|
| [PhotoDatabase.cs](PhotoDatabase.cs) | 缓存数据库门面 | SQLite (`photos.db`) 静态门面,与 `SettingsService` 共用数据目录。三表 schema(Plan-2-3 + Plan-2-4):**`photos`** 身份字段 + `rating` + EXIF 拍摄参数 4 列(`focal_length` / `aperture` / `shutter_speed` / `crop_factor`,Plan-2-4 训练快照,等效焦距由 crop_factor 现算)+ `cv_grid` BLOB + `cv_grid_spec` TEXT(覆盖式,版本 bump 即作废)+ `cv_computed_at` + `cv_image_width` / `cv_image_height`(CV 实际解码尺寸,供抖动徽标服务算 diagonal,无需重新解码);**`photo_features`** DINO CLS 纵表 `(fingerprint, model_id)` 主键 + `cls_vector` BLOB;**`photo_patches`** DINO patch token 纵表同主键 + `patch_tokens` BLOB(1024×384 float)。读写 API:`WriteIndexedAsync`(indexer 主路径,单事务三表,可携带尺寸 + `ExifSnapshot`)、`UpdateRatingAsync`(用户打星顺手同步副本,指纹未入库静默跳过)、`WriteFeatureAsync` / `ReadFeatureAsync` / `WritePatchesAsync` / `ReadPatchesAsync` / `WriteCvGridAsync` / `ReadCvGridAsync`(返回 `CvGridRecord` 含尺寸)/ `EvaluateMissingPartsAsync` / `DeleteDatabaseAsync`(供 AI 设置页"清除特征数据库"按钮)。启动时检测旧 schema(`feature_vector` / `heatmap` 列残留,或缺 `cv_image_width/height`,或缺 `focal_length`)自动删库重建。 |
| [PhotoFingerprint.cs](PhotoFingerprint.cs) | 指纹计算 | 三字段规范化 SHA1：`filename_noext` + `DateTimeOriginal`(秒, UTC ISO-8601) + `SubSecTimeOriginal`(3 位毫秒)。同一次曝光的 RAW/HIF/JPG 字节级字段一致 → 同指纹；高速连拍由 SubSec 毫秒区分；文件名编号循环由日期区隔。验证工具见 `ExifTestTool fp <folder>`。 |

**连接纪律**:`journal_mode=WAL` 是库级持久设置,只在 `Initialize`/`DeleteDatabaseAsync` 的单连接上写一次;每条连接只设连接级
`PRAGMA synchronous=NORMAL; foreign_keys=ON;`。曾经每次开连接都写 `journal_mode`,并发开几十上百连接时互相抢库锁
(`SQLITE_BUSY` 重试),大图库里表现为面板/索引长时间卡死。另外 `FindSimilarAsync` 这类批量读也受连接池上限(默认 100)约束,
调用方必须自带并发度上限,不要一张 Task 一个候选。
