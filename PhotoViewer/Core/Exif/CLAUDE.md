# Core/Exif — 元数据读写

> 模块内手册。评分回写到 RAW 的端到端流程(键盘 1~5 → XmpWriter → 筛选刷新)见根 `CLAUDE.md` §5 关键流程。

`namespace PhotoViewer.Core`(Exif 文件沿用根命名空间)

| File | 模块 | Responsibility |
|---|---|---|
| [ExifLoader.cs](ExifLoader.cs) | 元数据读取 | EXIF/XMP 顶层读取编排；子任务委派给 `ExifMetadataGrouper` / `SonyMakernoteParser`。 |
| [ExifModels.cs](ExifModels.cs) | 元数据模型 | `ExifData` / `MetadataGroup` / `MetadataTag` POCO。 |
| [ExifMetadataGrouper.cs](ExifMetadataGrouper.cs) | 分组与翻译 | 展开 MetadataExtractor 目录为按目录分组的可读 tag 列表，应用 `ExifChinese`/`ExifToolTags`/`ExifToolValues` 翻译。 |
| [ExifChinese.cs](ExifChinese.cs) | 元数据汉化 | Chinese tag-name overrides; generated baseline in `ExifChinese.Generated.cs`. |
| [ExifToolTags.cs](ExifToolTags.cs) | 标签库 | English tag-name overrides + brand override tables. Generated baseline in `ExifToolTags.Generated.cs`. |
| [ExifToolValues.cs](ExifToolValues.cs) | 取值翻译 | Enum-style EXIF value translations; generated baseline in `*.Generated.cs`. |
| [XmpWriter.cs](XmpWriter.cs) | 标星评分写入 | XMP rating writes — in-place edit + sidecar fallback for RAW. |
| [Sony/SonyMakernoteParser.cs](Sony/SonyMakernoteParser.cs) | Sony MakerNote 解析 | 对焦点位置/对焦框尺寸、LensSpec BCD 解码、加密 tag 调度。 |
| [Sony/SonyCipherTags.cs](Sony/SonyCipherTags.cs) | Sony 加密 tag 解码 | Decrypt Sony 0x94xx / 0x9050 MakerNote blocks. **Generated table** is in `*.Generated.cs` — do not edit by hand; regenerate via `Tools/generate-sony-cipher-tags.py`. |

> **Generated files**: anything ending in `.Generated.cs` is overwritten by `Tools/*.py`. Manual fixes belong in the non-generated companion file's override table. See [DEV.md §五](../../../DEV.md) for the regeneration workflow.
