# Core/Image — 图片解码与文件模型

> 模块内手册。图片加载 pipeline(切图触发 → 解码 → 显示 → 预取)的端到端流程见根 `CLAUDE.md` §5 关键流程。

`namespace PhotoViewer.Core.Image`

| File | 模块 | Responsibility |
|---|---|---|
| [BitmapLoader.cs](BitmapLoader.cs) | 图片加载器 | Decode pipeline + LRU cache + EXIF rotation. |
| [ImageEnhancer.cs](ImageEnhancer.cs) | 主图增强 | 确定性"增强预览"图像处理（v0：全局亮度直方图均衡，按 newY/oldY 等比缩放保色相）。纯算法、不改原文件、不入库；由 `ImageViewModel` 的增强 toggle 在后台调用。plan-3-1 §1.2 产品化落地的算法沙盒（铁约束：模型输入最终算法须非破坏性，此 v0 仅供产品目视）。 |
| [HistogramRenderer.cs](HistogramRenderer.cs) | 直方图渲染 | 把位图渲成 RGB 256 级直方图位图（透明底 + 三通道纯原色叠加填充曲线，重叠取 max → 三色重叠恰为白）。纯算法；分析栏"直方图"瓦片用它从当前主图（原片或增强图）现算，随增强切换实时重算，透明底让 DiagnosticTile 的 #222 透出。字节序沿用 BitmapLoader 约定。 |
| [BitmapPrefetcher.cs](BitmapPrefetcher.cs) | 预加载器 | Background prefetch of N neighbours around the current image. 邻居位图解码完成后,若分析栏可见,顺手为该邻居 `AnalysisDataReader.ComputeFingerprintAsync` + `AnalysisComputer.Compute` 落进 `AnalysisResultCache` — 让前后切图变成纯 UI swap。 |
| [HeifLoader.cs](HeifLoader.cs) | HEIF 解码桥接 | Static facade. `Initialize(IHeifDecoder)` injects platform decoder. |
| [ImageFile.cs](ImageFile.cs) | 文件模型 | Per-file state: path, load status, EXIF cache, thumbnail bitmap, `IsShake : bool?`(由 `ShakeFlagService` 回填,驱动缩略图卡片的"抖"徽标)。 |
| [ImageOrientationInfo.cs](ImageOrientationInfo.cs) | 容器方向元数据 | 统一封装 HEIF `Default Rotation` / EXIF `Orientation` + `ExifImageWidth/Height`，给出"显示朝向旋转角 + 水平镜像 + 传感器原始 W/H"。`ThumbnailService` 据此做方向对齐与 letterbox 几何裁剪，无任何启发式。 |
| [JpegDimensionReader.cs](JpegDimensionReader.cs) | JPEG SOF 解析 | 字节级解析 JPEG SOF marker (FFC0..FFCF) 直接读真实宽高。HEIF 的 Thumbnail Data 字节嗅探与厂商 Preview 都依赖它，避免再用容器索引贴标签出错。 |
| [ThumbnailService.cs](ThumbnailService.cs) | 缩略图服务门面 | `GetAvailableSourcesAsync(file)` 列出来源（EXIF/IFD1 缩略图、厂商 PreviewImage、HEIF 内嵌 JPEG/平台兜底）；`GetThumbnailAsync(file, minShortSide)` 取**显示短边 ≥ target 中最小**的来源解码,随后按 `ImageOrientationInfo` 做方向对齐 + letterbox 几何裁剪。HEIF 字节路径只接 JPEG（用 `JpegDimensionReader` 自读尺寸）,HEVC 字节走平台 `HeifLoader` 兜底（已预旋转）。**不再回退原图全图解码**,所有来源都失败时返回 null 由 UI 显示占位符。 |
| [ThumbnailSource.cs](ThumbnailSource.cs) | 缩略图来源 POCO | `Width`/`Height`（字节本身像素，未旋转）/ `Origin`（`ExifEmbedded` / `MakernotePreview` / `HeifEmbedded`）/ `IsPreRotated`（标记该来源是否已是显示朝向，平台 HEIF 解码器为 true，字节直读路径为 false）。 |
