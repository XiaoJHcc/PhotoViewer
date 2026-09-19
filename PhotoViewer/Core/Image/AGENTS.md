# Core/Image — 图片解码与文件模型

> 模块内手册。图片加载 pipeline(切图触发 → 解码 → 显示 → 预取)的端到端流程见根 `AGENTS.md` §5 关键流程。

`namespace PhotoViewer.Core.Image`

| File | 模块 | Responsibility |
|---|---|---|
| [BitmapLoader.cs](BitmapLoader.cs) | 图片加载器 | Decode pipeline + LRU cache + EXIF rotation. 在途去重(同路径请求共享一个解码任务),解码本体在线程池执行;淘汰走距离保护——`SetProtectedPaths` 上报的保护项在所有淘汰路径(EnsureCapacity/Cleanup/LruRemoveToSize)中排到最后。 |
| [ImageEnhancer.cs](ImageEnhancer.cs) | 主图增强 | 确定性"信息归一化"增强预览：全局**限对比直方图均衡（CLHE）**——亮度直方图按 `ClipFactor` 裁剪封顶 + 均匀回填（防断层 / 防大面积同色尖峰独吞输出范围）→ cdfMin 归一化 LUT → **保色度重建** `ch' = Y' + s·(ch − Y)`（YCbCr 恒定色度，`s = SaturationScale`）。该式结果亮度恒等于 `LUT[Y]` → 亮暗关系严格单调、色相不变；`s=1` 保**绝对**彩度，暗部彩度不随亮度塌缩发灰（取代旧的 `(newY+ε)/(oldY+ε)` 等比增益——那只保**相对**饱和度，暗部彩度仍随亮度趋零而发灰，且加性重建无除法故 ε 取消）。极端亮/暗处绝对彩度越界时逐像素把 s 收到"恰好不越界"，物理最小去饱和、无硬钳位。手感参数 `ClipFactor`（保守↔激进，默认 2.0）+ `SaturationScale`（默认 1.0）。纯算法、不改原文件、不入库；由 `ImageViewModel` 的增强 toggle 在后台调用，当前仅作产品目视。设计意图 / 选型理由见 plan-3-1 §1.2。 |
| [HistogramRenderer.cs](HistogramRenderer.cs) | 直方图渲染 | 把位图渲成 RGB 256 级直方图位图（透明底 + 三通道纯原色叠加填充曲线，重叠取 max → 三色重叠恰为白）。纯算法；分析栏"直方图"瓦片用它从当前主图（原片或增强图）现算，随增强切换实时重算，透明底让 DiagnosticTile 的 #222 透出。字节序沿用 BitmapLoader 约定。 |
| [BitmapPrefetcher.cs](BitmapPrefetcher.cs) | 预加载器 | Background prefetch of N neighbours around the current image. latest-wins(新请求取消旧轮 CTS);每张邻居位图的"预留内存 + 解码 + 分析栏预热"合并为一个 P2 工作项投递 `DecodeScheduler`(构造时把 `Settings.NativePreloadParallelism` 同步为调度器并发度)。邻居位图解码完成后,若分析栏可见,顺手为该邻居 `AnalysisDataReader.ComputeFingerprintAsync` + `AnalysisComputer.Compute` 落进 `AnalysisResultCache` — 让前后切图变成纯 UI swap。 |
| [DecodeScheduler.cs](DecodeScheduler.cs) | 统一优先级调度器 | 静态类。P0 当前照片(立即独立执行 + 在途标志,`TrackP0` 供主图解码路径上报)/ P1 可见缩略图 / P2 预载 / P3 杂项;同 key 去重 + 高优先级请求可提升队列中项的优先级;后台 worker 按 `MaxBackgroundParallelism` 并发,P0 在途时暂停取新工作(已在跑的不打断)。收编缩略图加载与位图预取两条通道。 |
| [HeifLoader.cs](HeifLoader.cs) | HEIF 解码桥接 | Static facade. `Initialize(IHeifDecoder)` injects platform decoder. |
| [RawLoader.cs](RawLoader.cs) | RAW 解码桥接 | Static facade. `Initialize(IRawDecoder)` injects platform decoder（macOS `MacRawDecoder` / iOS `iOSRawDecoder`，均复用 ImageIO 系统 RAW 引擎；扩展名单与设置页「RAW」组一致）。平台解码器内部已应用 EXIF 方向，`BitmapLoader` 对该路径跳过二次旋转。 |
| [ImageFile.cs](ImageFile.cs) | 文件模型 | Per-file state: path, load status, EXIF cache, thumbnail bitmap, `IsShake : bool?`(由 `ShakeFlagService` 回填,驱动缩略图卡片的"抖"徽标)。 |
| [ImageOrientationInfo.cs](ImageOrientationInfo.cs) | 容器方向元数据 | 统一封装 HEIF `Default Rotation` / EXIF `Orientation` + `ExifImageWidth/Height`，给出"显示朝向旋转角 + 水平镜像 + 传感器原始 W/H"。`ThumbnailService` 据此做方向对齐与 letterbox 几何裁剪，无任何启发式。 |
| [JpegDimensionReader.cs](JpegDimensionReader.cs) | JPEG SOF 解析 | 字节级解析 JPEG SOF marker (FFC0..FFCF) 直接读真实宽高。HEIF 的 Thumbnail Data 字节嗅探与厂商 Preview 都依赖它，避免再用容器索引贴标签出错。 |
| [ThumbnailService.cs](ThumbnailService.cs) | 缩略图服务门面 | `GetAvailableSourcesAsync(file)` 列出来源（EXIF/IFD1 缩略图、厂商 PreviewImage、HEIF 内嵌 JPEG/平台兜底、RAW 平台解码器兜底）；`GetThumbnailAsync(file, minShortSide)` 取**显示短边 ≥ target 中最小**的来源解码,随后按 `ImageOrientationInfo` 做方向对齐 + letterbox 几何裁剪。HEIF 字节路径只接 JPEG（用 `JpegDimensionReader` 自读尺寸）,HEVC 字节走平台 `HeifLoader` 兜底（已预旋转）。RAW 文件在内嵌预览全部失败时追加 `RawLoader` 全尺寸渲染兜底（尺寸未知排最后，已预旋转）；除此之外**不回退原图全图解码**,所有来源都失败时返回 null 由 UI 显示占位符。 |
| [ThumbnailSource.cs](ThumbnailSource.cs) | 缩略图来源 POCO | `Width`/`Height`（字节本身像素，未旋转）/ `Origin`（`ExifEmbedded` / `MakernotePreview` / `HeifEmbedded`）/ `IsPreRotated`（标记该来源是否已是显示朝向，平台 HEIF 解码器为 true，字节直读路径为 false）。 |
