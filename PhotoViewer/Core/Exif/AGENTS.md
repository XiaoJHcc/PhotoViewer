# Core/Exif — 元数据读写

> 模块内手册。评分回写(键盘 1~5 → `RatingService` → 内嵌容器或 sidecar → 筛选刷新)的端到端流程见 [架构文档](../../../Design/architecture.md) §5.3。

`namespace PhotoViewer.Core`(Exif 文件沿用根命名空间)

| 文件 | 模块 | 职责 |
|---|---|---|
| [ExifLoader.cs](ExifLoader.cs) | 元数据读取 | EXIF/XMP 顶层读取编排；子任务委派给 `ExifMetadataGrouper` / `SonyMakernoteParser`。星级读取统一走 `XmpRatingReader`：完整 EXIF 用 `ReadDirectories`（XMP 异常只丢星级，不影响其余 EXIF），仅星级用 `ReadAsync`（专有 RAW 先读标准 sidecar，不存在再读内嵌），并接受取消令牌。 |
| [XmpRatingReader.cs](XmpRatingReader.cs) | 星级读取语义 | XMP `xap:Rating` 的唯一解析口径（`Missing` / `Rated` / `Rejected` / `Invalid` / `Conflict`，多份内嵌 XMP 不一致判 Conflict）；提供 packet、MetadataExtractor 目录与文件三条入口，`ReadAsync` 对专有 RAW 优先 `<stem>.xmp` sidecar。 |
| [ExifModels.cs](ExifModels.cs) | 元数据模型 | `ExifData` / `MetadataGroup` / `MetadataTag` POCO。 |
| [ExifMetadataGrouper.cs](ExifMetadataGrouper.cs) | 分组与翻译 | 展开 MetadataExtractor 目录为按目录分组的可读 tag 列表，应用 `ExifChinese`/`ExifToolTags`/`ExifToolValues` 翻译。 |
| [ExifChinese.cs](ExifChinese.cs) | 元数据汉化 | Chinese tag-name overrides; generated baseline in `ExifChinese.Generated.cs`. |
| [ExifToolTags.cs](ExifToolTags.cs) | 标签库 | English tag-name overrides + brand override tables. Generated baseline in `ExifToolTags.Generated.cs`. |
| [ExifToolValues.cs](ExifToolValues.cs) | 取值翻译 | Enum-style EXIF value translations; generated baseline in `*.Generated.cs`. |
| [ExifDisplayConverters.cs](ExifDisplayConverters.cs) | 摘要格式 | `ExifDisplayFormat`：控制栏 EXIF 摘要取值格式（TOOLBAR-05：光圈/快门/焦距/曝光补偿/拍摄时间），缺失为 `--`。 |
| [RatingService.cs](RatingService.cs) | 评分写入事务 | 实例级服务（`MainViewModel.Rating`），按 `PhotoGroup.Id` 串行；代表文件先写、组内其余按顺序写，每个文件经 `WriteAsync` 写入并由适配器做写后回读语义校验；任一失败按逆序补偿已拿到回执的文件。结果分 `Applied` / `Superseded` / `RolledBack` / `Partial` / `Canceled`：`Partial` 明示补偿未全部成功、组内文件可能不一致，上层逐文件重读真实状态；只保证“全部成功或按回执回滚”，不宣称跨文件原子。取消只在首次物理修改前生效。 |
| [XmpRatingStore.cs](XmpRatingStore.cs) | 评分存储契约 | `IXmpRatingStore`（`ReadAsync` / `WriteAsync` / `RestoreAsync`）、`IXmpContainerAdapter`、`XmpRatingWriteReceipt`（原始包、写后 revision、原文件长度、是否新建目标）。 |
| [XmpRatingStoreFactory.cs](XmpRatingStoreFactory.cs) | 存储选择 | 专有 RAW（`.arw` `.cr2` `.cr3` `.nef` `.raf` `.orf` `.rw2` `.srw`）走 sidecar；`.xmp` 按独立 sidecar；JPEG/HEIF/HIF/AVIF/PNG/WebP/TIFF/DNG 走内嵌适配器；其余格式明确 `Unsupported`，绝不静默假成功。 |
| [ContainerRatingStore.cs](ContainerRatingStore.cs) | 内嵌容器存储 | 只读写适配器认定的 canonical XMP 包，不猜测容器布局；适配器异常代表没有可交给事务层补偿的改动。 |
| [SidecarRatingStore.cs](SidecarRatingStore.cs) | 专有 RAW / 独立 `.xmp` 存储 | sidecar 名为 `<stem>.xmp`；存在 sidecar 时它是唯一权威（读取不再回看内嵌）。创建时以内嵌 canonical 包为模板重新序列化以保留其它属性，内嵌包不可解析或声明扩展包则 `Unsupported`，不静默丢属性。新建/空目标用随机访问在偏移 0 写入（SAF 没有原子替换）；已有非空、变长写入必须 `AtomicReplace`，平台不支持则在修改前失败；等长可原位写。写后回读校验，失败按 revision + 字节自愈或删除新建目标。 |
| [XmpContainerIO.cs](XmpContainerIO.cs) | 容器读写原语 | 定长原位写（safeMode 下先比对原字节）、变长整文件重写（走 `AtomicWriteAsync`）、按范围精确读；写后复核 revision，外部修改报 `ChangedExternally`。 |
| [XmpPacketEditor.cs](XmpPacketEditor.cs) | XMP 包编辑 | 用现有 XmpCore 读取并改写 `xap:Rating`，保留其余属性与命名空间，不手写 XML 文本。 |
| [JpegXmpContainerAdapter.cs](JpegXmpContainerAdapter.cs) | JPEG 容器适配 | 只认标准 APP1 `http://ns.adobe.com/xap/1.0/\0`；EXIF/Photoshop/extended XMP 不触碰。等长原位写，变长整文件原子重写；多个 canonical APP1 明确 `Unsupported`。 |
| [HeifXmpContainerAdapter.cs](HeifXmpContainerAdapter.cs) | HEIF/HEIC/HIF/AVIF 容器适配 | 只认 `meta/iinf` 中 `mime` + XML/RDF 的项，经 `iloc` 单 extent 定位 `mdat`；无 XMP mime 项、多 extent、`idat`、长度不定或无法证明范围时 `Unsupported`，不新增项。变长时追加新 `mdat` 并改写 `iloc`（需原子替换）。 |
| [TiffXmpContainerAdapter.cs](TiffXmpContainerAdapter.cs) | TIFF 家族容器适配 | 只按结构定位 IFD0 的 tag 700；无 tag 700 明确 `Unsupported`（新增条目需重建 IFD0），不扫文本。等长原位写；变长追加新包并原位改 tag 700 的 count/offset，恢复时按回执移除追加区。 |
| [PngXmpContainerAdapter.cs](PngXmpContainerAdapter.cs) | PNG 容器适配 | 只认未压缩 iTXt keyword `XML:com.adobe.xmp`；逐 chunk 校验边界与 CRC，写入重建长度/CRC 并保留语言与翻译字段。等长原位写，扩容/新增走原子替换；tEXt/zTXt 中的 XMP 不视为 canonical。 |
| [WebpXmpContainerAdapter.cs](WebpXmpContainerAdapter.cs) | WebP 容器适配 | 只认 RIFF little-endian 的 `XMP ` chunk；写入维护 RIFF 长度与 VP8X XMP 标志位。等长原位写，其余情况走原子替换。 |
| [Sony/SonyMakernoteParser.cs](Sony/SonyMakernoteParser.cs) | Sony MakerNote 解析 | 对焦点位置/对焦框尺寸、LensSpec BCD 解码、加密 tag 调度（含 0x940F 加速度计姿态）。 |
| [Sony/SonyCipherTags.cs](Sony/SonyCipherTags.cs) | Sony 加密 tag 解码 | Decrypt Sony 0x94xx / 0x9050 MakerNote blocks；`DecodeAccelerometer940F` 只负责显示格式化，数值真源在 `SonyAttitudeDecoder`。**Generated table** is in `*.Generated.cs` — do not edit by hand; regenerate via `Tools/generate-sony-cipher-tags.py`. |
| [Sony/SonyAttitudeDecoder.cs](Sony/SonyAttitudeDecoder.cs) | Sony 姿态数值 API（public） | 0x940F 静照加速度计数值解码唯一真源：三轴恒导出（m/s²，本帧 \|raw\| 归一）；俯仰/横滚仅已校准机型（A7C2 恒等 / a6700 body=(-rawY,-rawZ,-rawX)），未知机型不算角度。显示层与训练侧导出（DatasetBuilder `--dump-accel`）共用，禁止分叉。`DecryptRaw` 供新机型标定诊断（ExifTestTool `accel` 子命令）。 |

> **Generated files**: anything ending in `.Generated.cs` is overwritten by `Tools/*.py`. Manual fixes belong in the non-generated companion file's override table. See [DEV.md §五](../../../DEV.md) for the regeneration workflow.
