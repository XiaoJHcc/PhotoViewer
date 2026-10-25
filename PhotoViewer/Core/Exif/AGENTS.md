# Core/Exif — 元数据读写

> 模块内手册。评分回写(键盘 1~5 → `RatingService` → 内嵌容器或 sidecar → 筛选刷新)的端到端流程见 [架构文档](../../../Design/architecture.md) §5.3。

`namespace PhotoViewer.Core`(Exif 文件沿用根命名空间)

| 文件 | 模块 | 职责 |
|---|---|---|
| [ExifLoader.cs](ExifLoader.cs) | 元数据读取 | EXIF/XMP 顶层读取编排；子任务委派给 `ExifMetadataGrouper` / `SonyMakernoteParser`。星级读取统一走 `XmpRatingReader` 与 `RatingSourceReader`：完整 EXIF 用已读目录（XMP 异常只丢星级，不影响其余 EXIF），仅星级用 `LoadRatingSourcesAsync`；内嵌与已存在的 sidecar 是两个独立来源，内嵌永不被 sidecar 遮蔽。可选 `RatingSidecarIndex` 避免逐文件整目录查找。 |
| [XmpRatingReader.cs](XmpRatingReader.cs) | 星级读取语义 | XMP `xap:Rating` 的唯一解析口径（`Missing` / `Rated` / `Rejected` / `Invalid` / `Conflict`，多份内嵌 XMP 不一致判 Conflict）；`XmpDirectory.XmpMeta == null` 映射 `Invalid` 而不是 Missing。`ReadAsync` 只读内嵌，`ReadSidecarAsync` 只读已存在的 `<stem>.xmp`。`ProbeEmbeddedAsync` 供写入预检重新枚举内嵌目录，返回目录数、extended 声明与空元数据。`NumericValue` 为 Rejected=-1。 |
| [RatingSourceReader.cs](RatingSourceReader.cs) | 评分来源枚举 | `RatingSourceState`（来源 + 载体文件 + 快照）与 `RatingSidecarIndex`（一次枚举建立 `<stem>.xmp` 索引；枚举完成前由会话返回 null）；`ReadAsync` 返回内嵌 + 已存在 sidecar 的全部来源，读取失败明确为 `Invalid` 并带原因，不静默归零。 |
| [XmpRatingSpanLocator.cs](XmpRatingSpanLocator.cs) | 评分区间定位与等长改写 | 结构化扫描 UTF-8 XMP：按命名空间 URI（而非前缀）识别属性式/元素式 `xap:Rating`，元素式必须属于 xap 命名空间；`TryCreateEdit` 生成保持整包长度的最小改写区间（等长直接改值、变短补空白、变长只用 Rating 附近可证明合法的局部空白，属性分隔空格至少保留一个；不再用包尾 padding 远端兜底），`XmpRatingEdit` 同时携带原字节与替换字节。`TryCreateInsertion` 在已有 canonical 包缺 Rating 时按结构插入（唯一 `rdf:Description` 属性或 `rdf:RDF` 结束前的自包含描述），只吸收紧邻插入点的局部空白。多个非定位 Rating、CDATA、DOCTYPE/实体、空元素、局部空白不足等明确拒绝；重复 Rating 也一律拒绝。 |
| [ExifModels.cs](ExifModels.cs) | 元数据模型 | `ExifData` / `MetadataGroup` / `MetadataTag` POCO；`ExifData` 另带内嵌评分状态、完整快照与全部来源列表。 |
| [ExifMetadataGrouper.cs](ExifMetadataGrouper.cs) | 分组与翻译 | 展开 MetadataExtractor 目录为按目录分组的可读 tag 列表，应用 `ExifChinese`/`ExifToolTags`/`ExifToolValues` 翻译。 |
| [ExifChinese.cs](ExifChinese.cs) | 元数据汉化 | Chinese tag-name overrides; generated baseline in `ExifChinese.Generated.cs`. |
| [ExifToolTags.cs](ExifToolTags.cs) | 标签库 | English tag-name overrides + brand override tables. Generated baseline in `ExifToolTags.Generated.cs`. |
| [ExifToolValues.cs](ExifToolValues.cs) | 取值翻译 | Enum-style EXIF value translations; generated baseline in `*.Generated.cs`. |
| [ExifDisplayConverters.cs](ExifDisplayConverters.cs) | 摘要格式 | `ExifDisplayFormat`：控制栏 EXIF 摘要取值格式（TOOLBAR-05：光圈/快门/焦距/曝光补偿/拍摄时间），缺失为 `--`。 |
| [RatingService.cs](RatingService.cs) | 评分写入事务 | 实例级服务（`MainViewModel.Rating`），按 `PhotoGroup.Id` 串行；核心允许 `-1`（Rejected，UI 不暴露）。写入目标是组内**全部实际来源**：每个文件的内嵌 XMP + 已存在的同名 `<stem>.xmp`（共享 sidecar 按存储身份去重，只写/补偿一次）。预检先重新读取内嵌 XMP 目录（不依赖内存），多份/扩展 XMP 一律 `Unsupported`；再逐来源 `PrepareAsync`，任一不支持则在首次修改前整组失败。写入时 sidecar 逐个文件直接核查当前磁盘（忽略列表索引），避免陈旧/混合索引漏写。预检全过后按“代表内嵌 → 其余内嵌 → 已有 sidecar”提交；提交返回带 `Failure` 的回执同样纳入逆序补偿。同值来源 NoChange 且不写盘；结果分 `Applied` / `NoChange` / `Superseded` / `RolledBack` / `Partial` / `Canceled`，`Partial` 逐来源重读磁盘真值。 |
| [XmpRatingStore.cs](XmpRatingStore.cs) | 评分存储契约 | `IXmpRatingStore`（`ReadAsync` / `PrepareAsync` / `CommitAsync` / `RestoreAsync`，另有 `WriteAsync` 扩展做预检+提交）、`IXmpContainerAdapter`。`XmpRatingPreparation` 携带预检 revision、包偏移、最小改写区间的原字节/替换字节与是否插入；`XmpRatingWriteReceipt` 携带同一区间字节，`Failure` 非空表示已尝试自愈但需事务层继续补偿。 |
| [XmpRatingStoreFactory.cs](XmpRatingStoreFactory.cs) | 存储选择 | 每个文件的内嵌来源：JPEG / HEIF/HEIC/HIF/AVIF / WebP / TIFF/DNG / RAW（ARW、CR2、NEF、ORF、RW2、SRW 走 TIFF，CR3 走 HEIF）。**PNG 明确不支持写入**（iTXt 长度/CRC 事务未实现），读取仍可用旧适配器。`CreateSidecar` 只包装**已存在**的 `.xmp`；没有创建 sidecar 的入口。其余格式明确 `Unsupported`。 |
| [ContainerRatingStore.cs](ContainerRatingStore.cs) | 来源存储 | 只读写适配器认定的 canonical XMP 包。预检先检查随机访问写入能力；只有严格解析为 Rated/Rejected 且等于目标值才 NoChange（不可读、裸实体、重复 Rating 一律失败）。提交前始终比较预检 size/mtime/version；安全模式再逐字节比较**整个原始 XMP 包**。复核与实际写在同一 RW 会话、Write 之前完成（safeMode 整包，非 safeMode 至少编辑区间），并再次核对会话 revision。正常写入只覆盖 locator 给出的最小等长局部区间，写后回读语义并核对区间外字节未变；Write/Flush/写后 Read/校验失败都会尝试恢复本来源，恢复不成则返回带 `Failure` 的回执供事务层逆序补偿。补偿也按区间恢复并确认外部未改。无 canonical XMP、空 sidecar、非局部空白等预检 `Unsupported`。 |
| [XmpFileContainerAdapter.cs](XmpFileContainerAdapter.cs) | 独立 `.xmp` 来源 | 整个文件就是一个包（偏移 0）；不存在或为空返回 null，绝不创建。 |
| [XmpContainerIO.cs](XmpContainerIO.cs) | 容器读写原语 | 定长原位写（safeMode 下先比对原字节）、变长整文件重写（走 `AtomicWriteAsync`）、按范围精确读；写后复核 revision，外部修改报 `ChangedExternally`。 |
| [XmpPacketEditor.cs](XmpPacketEditor.cs) | XMP 包编辑（旧路径） | 用 XmpCore 读取并改写 `xap:Rating`。评分事务已改用 `XmpRatingSpanLocator` 的等长区间改写；此编辑器只服务于容器适配器的历史写入入口，不属于正常写入路径。 |
| [JpegXmpContainerAdapter.cs](JpegXmpContainerAdapter.cs) | JPEG 容器适配 | 只认标准 APP1 `http://ns.adobe.com/xap/1.0/\0`；EXIF/Photoshop/extended XMP 不触碰。为评分事务定位 canonical 包（偏移 + 长度），事务按同偏移原位覆盖；多个 canonical APP1 明确 `Unsupported`。类上保留的历史 `WriteRatingAsync` 不属于评分事务路径。 |
| [HeifXmpContainerAdapter.cs](HeifXmpContainerAdapter.cs) | HEIF/HEIC/HIF/AVIF/CR3 容器适配 | 只认 `meta/iinf` 中 `mime` + XML/RDF 的项，经 `iloc` 单 extent 定位 `mdat`；无 XMP mime 项、多 extent、`idat`、长度不定或无法证明范围时 `Unsupported`，不新增项。为评分事务定位 canonical 包，事务等长原位覆盖。 |
| [TiffXmpContainerAdapter.cs](TiffXmpContainerAdapter.cs) | TIFF 家族容器适配（含 ARW） | 只按结构定位 IFD0 的 tag 700；无 tag 700 明确 `Unsupported`（新增条目需重建 IFD0），不扫文本。为评分事务定位 canonical 包，事务等长原位覆盖。类上保留的历史追加/恢复入口不属于评分事务路径。 |
| [PngXmpContainerAdapter.cs](PngXmpContainerAdapter.cs) | PNG 容器适配（只读/历史） | 只认未压缩 iTXt keyword `XML:com.adobe.xmp`；逐 chunk 校验边界与 CRC。**评分事务明确不支持 PNG 写入**（iTXt 长度/CRC 事务未实现，裸写文本会写坏文件）；适配器的历史写入入口不属于事务路径。tEXt/zTXt 中的 XMP 不视为 canonical。 |
| [WebpXmpContainerAdapter.cs](WebpXmpContainerAdapter.cs) | WebP 容器适配 | 只认 RIFF little-endian 的 `XMP ` chunk；为评分事务定位 canonical 包。 |
| [Sony/SonyMakernoteParser.cs](Sony/SonyMakernoteParser.cs) | Sony MakerNote 解析 | 对焦点位置/对焦框尺寸、LensSpec BCD 解码、加密 tag 调度（含 0x940F 加速度计姿态）。 |
| [Sony/SonyCipherTags.cs](Sony/SonyCipherTags.cs) | Sony 加密 tag 解码 | Decrypt Sony 0x94xx / 0x9050 MakerNote blocks；`DecodeAccelerometer940F` 只负责显示格式化，数值真源在 `SonyAttitudeDecoder`。**Generated table** is in `*.Generated.cs` — do not edit by hand; regenerate via `Tools/generate-sony-cipher-tags.py`. |
| [Sony/SonyAttitudeDecoder.cs](Sony/SonyAttitudeDecoder.cs) | Sony 姿态数值 API（public） | 0x940F 静照加速度计数值解码唯一真源：三轴恒导出（m/s²，本帧 \|raw\| 归一）；俯仰/横滚仅已校准机型（A7C2 恒等 / a6700 body=(-rawY,-rawZ,-rawX)），未知机型不算角度。显示层与训练侧导出（DatasetBuilder `--dump-accel`）共用，禁止分叉。`DecryptRaw` 供新机型标定诊断（ExifTestTool `accel` 子命令）。 |

> **Generated files**: anything ending in `.Generated.cs` is overwritten by `Tools/*.py`. Manual fixes belong in the non-generated companion file's override table. See [DEV.md §五](../../../DEV.md) for the regeneration workflow.
