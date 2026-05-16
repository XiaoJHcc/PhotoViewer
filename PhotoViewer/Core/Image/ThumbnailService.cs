using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using Avalonia.Platform.Storage;
using MetadataExtractor;
using MetadataExtractor.Formats.Exif;

namespace PhotoViewer.Core.Image;

/// <summary>
/// 缩略图服务门面：对外只暴露两个能力 ——
/// 1) <see cref="GetAvailableSourcesAsync"/>:列出文件可用的缩略图来源（含尺寸,未解码）；
/// 2) <see cref="GetThumbnailAsync"/>:按目标短边挑一个尺寸合适的来源解码,并完成"方向对齐 + letterbox 裁剪"。
/// 设计约束：
/// - 仅从广义 EXIF/容器元数据里列出"已知尺寸的缩略图条目",绝不触发原图全分辨率解码。
/// - 方向与裁剪全部依赖容器/EXIF 元数据声明（HEIF Default Rotation / EXIF Orientation + Sensor W/H）,
///   不做任何启发式的像素亮度检测。
/// - HEIF 路径优先按 Thumbnail Data 字节嗅探,JPEG 字节直接读 SOF 拿真实尺寸；HEVC/未知字节走平台解码器兜底。
/// - 选源规则:显示短边必须 ≥ target,不够大的来源整体舍弃,确保始终是缩图（never 放大）。
/// </summary>
public static class ThumbnailService
{
    /// <summary>
    /// 列出该文件所有可用的缩略图来源（尺寸由容器/EXIF 元数据读取；不做任何图像解码）。
    /// 顺序由具体提取器决定;尺寸未知时 <see cref="ThumbnailSource.Width"/> / <see cref="ThumbnailSource.Height"/> 为 0。
    /// </summary>
    public static async Task<IReadOnlyList<ThumbnailSource>> GetAvailableSourcesAsync(IStorageFile file)
    {
        if (file == null) return Array.Empty<ThumbnailSource>();

        try
        {
            IReadOnlyList<MetadataExtractor.Directory> directories;
            await using (var stream = await file.OpenReadAsync())
            {
                directories = ImageMetadataReader.ReadMetadata(stream);
            }

            bool isHeif = HeifLoader.IsHeifFile(file);
            return isHeif
                ? await EnumerateHeifSourcesAsync(file, directories).ConfigureAwait(false)
                : EnumerateNonHeifSources(file, directories);
        }
        catch (Exception ex)
        {
            Console.WriteLine("Failed to enumerate thumbnail sources (" + file.Name + "): " + ex.Message);
            return Array.Empty<ThumbnailSource>();
        }
    }

    /// <summary>
    /// 按目标短边 <paramref name="targetShortSide"/> 选取并解码一张缩略图:
    /// 显示短边 ≥ target 中最小优先（最省 I/O 就能达到显示质量）,其次是尺寸未知的来源。
    /// 解码完成后,根据容器声明的方向与传感器比例完成"方向对齐 + letterbox 裁剪"。
    /// 任何来源都不会触发原图全分辨率解码；所有来源都失败时返回 null。
    /// </summary>
    public static async Task<Bitmap?> GetThumbnailAsync(IStorageFile file, int targetShortSide)
    {
        if (file == null || targetShortSide <= 0) return null;

        IReadOnlyList<MetadataExtractor.Directory> directories;
        bool isHeif = HeifLoader.IsHeifFile(file);
        try
        {
            await using var probeStream = await file.OpenReadAsync();
            directories = ImageMetadataReader.ReadMetadata(probeStream);
        }
        catch (Exception ex)
        {
            Console.WriteLine("ThumbnailService: failed to read metadata (" + file.Name + "): " + ex.Message);
            return null;
        }

        var orientation = ImageOrientationInfo.FromDirectories(directories, isHeif);
        var sources = isHeif
            ? await EnumerateHeifSourcesAsync(file, directories).ConfigureAwait(false)
            : EnumerateNonHeifSources(file, directories);
        if (sources.Count == 0) return null;

        foreach (var src in OrderByPriority(sources, targetShortSide))
        {
            try
            {
                var raw = await src.LoaderAsync(targetShortSide);
                if (raw == null) continue;

                var aligned = AlignOrientationIfNeeded(raw, src, orientation);
                var cropped = CropLetterboxByMetadata(aligned, orientation);
                return cropped;
            }
            catch (Exception ex)
            {
                Console.WriteLine("ThumbnailService: source " + src.Origin + " failed for " + file.Name + ": " + ex.Message);
            }
        }
        return null;
    }

    /// <summary>
    /// 选源优先级:字节短边 ≥ target 中最小优先（解码代价最低且质量达标）,其次是尺寸未知的（由平台解码器挑选）。
    /// 不满足 ≥ target 的来源直接舍弃,避免放大或质量退化。
    /// 字节短边在旋转前后保持不变,可以直接用 ShortSide 选源。
    /// </summary>
    private static IEnumerable<ThumbnailSource> OrderByPriority(IReadOnlyList<ThumbnailSource> sources, int target)
    {
        var qualified = sources.Where(s => s.ShortSide >= target).OrderBy(s => s.ShortSide);
        var unknown = sources.Where(s => s.ShortSide == 0);
        return qualified.Concat(unknown);
    }

    // ============================================================
    // HEIF 容器:从 Thumbnail Data 目录读取字节范围,逐条嗅探。
    // JPEG 字节用 JpegDimensionReader 直接读 SOF 拿真实尺寸；
    // HEVC/未知字节不单独注册,改由平台解码器兜底。
    // ============================================================

    private static async Task<IReadOnlyList<ThumbnailSource>> EnumerateHeifSourcesAsync(
        IStorageFile file,
        IReadOnlyList<MetadataExtractor.Directory> directories)
    {
        var result = new List<ThumbnailSource>();

        foreach (var dir in directories)
        {
            if (!IsHeicThumbnailData(dir)) continue;
            int offset = TryReadInt(dir, 1);
            int length = TryReadInt(dir, 2);
            if (offset <= 0 || length <= 100) continue;

            int capturedOffset = offset;
            int capturedLength = length;
            int w = 0, h = 0;

            // 嗅探前 64KB,判断是否 JPEG;是的话顺手 SOF 解析出真实宽高
            try
            {
                await using var probe = await file.OpenReadAsync();
                probe.Seek(capturedOffset, SeekOrigin.Begin);
                int probeLen = Math.Min(capturedLength, 64 * 1024);
                var headBuf = new byte[probeLen];
                int got = 0;
                while (got < probeLen)
                {
                    int n = await probe.ReadAsync(headBuf, got, probeLen - got).ConfigureAwait(false);
                    if (n <= 0) break;
                    got += n;
                }
                if (got >= 4 && headBuf[0] == 0xFF && headBuf[1] == 0xD8)
                {
                    var dims = JpegDimensionReader.TryReadDimensions(headBuf.AsSpan(0, got));
                    w = dims.width;
                    h = dims.height;
                }
            }
            catch
            {
                // 嗅探失败就走平台回退,这条略过
            }

            if (w > 0 && h > 0)
            {
                result.Add(new ThumbnailSource(
                    width: w, height: h, origin: ThumbnailOrigin.HeifEmbedded,
                    isPreRotated: false,
                    loaderAsync: target => ReadRangeAndDecodeAsync(file, capturedOffset, capturedLength, target)));
            }
        }

        // 平台解码器兜底:Default Rotation 已被平台解码器内部应用,标记 IsPreRotated=true
        result.Add(new ThumbnailSource(
            width: 0, height: 0, origin: ThumbnailOrigin.HeifEmbedded,
            isPreRotated: true,
            loaderAsync: target => HeifLoader.LoadHeifThumbnailAsync(file, target)));

        return result;
    }

    /// <summary>
    /// 按 (offset, length) 从文件里截取一段字节,嗅探为合法 JPEG 后按目标短边解码。
    /// </summary>
    private static async Task<Bitmap?> ReadRangeAndDecodeAsync(IStorageFile file, int offset, int length, int targetShortSide)
    {
        try
        {
            await using var s = await file.OpenReadAsync();
            s.Seek(offset, SeekOrigin.Begin);
            var buffer = new byte[length];
            int read = 0;
            while (read < length)
            {
                int n = await s.ReadAsync(buffer, read, length - read).ConfigureAwait(false);
                if (n <= 0) break;
                read += n;
            }
            if (read != length) return null;
            if (!IsJpegBytes(buffer)) return null;
            return DecodeJpegToShortSide(buffer, targetShortSide);
        }
        catch (Exception ex)
        {
            Console.WriteLine("ThumbnailService: read range failed (" + file.Name + "): " + ex.Message);
            return null;
        }
    }

    private static bool IsHeicThumbnailData(MetadataExtractor.Directory d) =>
        d.Name.IndexOf("Thumbnail Data", StringComparison.OrdinalIgnoreCase) >= 0;

    private static int TryReadInt(MetadataExtractor.Directory dir, int tag)
    {
        try { return dir.GetInt32(tag); }
        catch { return 0; }
    }

    // ============================================================
    // JPEG / TIFF / ARW 等非 HEIF 容器:枚举 EXIF/IFD1 与厂商 Preview/Thumbnail 目录。
    // 尺寸全部从字节本身读取（SOF 解析）,不再用元数据贴标签。
    // ============================================================

    private static IReadOnlyList<ThumbnailSource> EnumerateNonHeifSources(IStorageFile file, IReadOnlyList<MetadataExtractor.Directory> directories)
    {
        var result = new List<ThumbnailSource>();

        // 1) 标准 EXIF/IFD1 缩略图条目（tag 256/257 = Width/Height）
        var exifThumbDir = directories.OfType<ExifThumbnailDirectory>().FirstOrDefault();
        if (exifThumbDir != null)
        {
            int w = TryReadInt(exifThumbDir, ExifDirectoryBase.TagImageWidth);
            int h = TryReadInt(exifThumbDir, ExifDirectoryBase.TagImageHeight);
            var capturedDir = exifThumbDir;
            result.Add(new ThumbnailSource(
                width: w, height: h, origin: ThumbnailOrigin.ExifEmbedded,
                isPreRotated: false,
                loaderAsync: target => ReadEmbeddedFromExifThumbnailDirectoryAsync(file, capturedDir, target)));
        }

        // 2) 厂商 MakerNote / Preview / Thumbnail 目录（如 Sony PreviewImage）
        foreach (var dir in directories)
        {
            if (ReferenceEquals(dir, exifThumbDir)) continue;
            var lname = dir.GetType().Name.ToLowerInvariant();
            if (!(lname.Contains("preview") || lname.Contains("thumbnail"))) continue;
            AppendMakernoteSources(dir, result);
        }

        return result;
    }

    /// <summary>
    /// 把厂商 Preview/Thumbnail 目录里"看起来是合法 JPEG 字节"的 tag 列为来源；
    /// 用 JpegDimensionReader 直接从字节里读真实尺寸,不再用元数据贴标签。
    /// </summary>
    private static void AppendMakernoteSources(MetadataExtractor.Directory dir, List<ThumbnailSource> sink)
    {
        foreach (var tag in dir.Tags)
        {
            try
            {
                var data = dir.GetByteArray(tag.Type);
                if (data == null || data.Length <= 100 || !IsJpegBytes(data)) continue;

                var dims = JpegDimensionReader.TryReadDimensions(data);
                if (dims.width <= 0 || dims.height <= 0) continue;

                var capturedData = data;
                sink.Add(new ThumbnailSource(
                    width: dims.width, height: dims.height, origin: ThumbnailOrigin.MakernotePreview,
                    isPreRotated: false,
                    loaderAsync: target => Task.FromResult(DecodeJpegToShortSide(capturedData, target))));
            }
            catch
            {
                // 当前 tag 不是字节数组,跳过
            }
        }
    }

    /// <summary>
    /// 从 ExifThumbnailDirectory 读取嵌入缩略图字节:先按常见字节 tag,再按 (Offset 0x0201, Length 0x0202) 偏移读取。
    /// 始终只解码嵌入字节;任何情况都不会触发原图解码。
    /// </summary>
    private static async Task<Bitmap?> ReadEmbeddedFromExifThumbnailDirectoryAsync(IStorageFile file, ExifThumbnailDirectory dir, int targetShortSide)
    {
        int[] thumbnailTags = { 0x0201, 0x0202, 0x0103 };
        foreach (var tag in thumbnailTags)
        {
            if (!dir.HasTagName(tag)) continue;
            try
            {
                var data = dir.GetByteArray(tag);
                if (data?.Length > 100 && IsJpegBytes(data))
                {
                    var thumb = DecodeJpegToShortSide(data, targetShortSide);
                    if (thumb != null) return thumb;
                }
            }
            catch
            {
                // 当前 tag 不是字节数组,继续
            }
        }

        if (dir.HasTagName(0x0201) && dir.HasTagName(0x0202))
        {
            try
            {
                var offset = dir.GetInt32(0x0201);
                var length = dir.GetInt32(0x0202);
                if (offset > 0 && length > 100)
                {
                    var jpeg = await ReadJpegAroundAsync(file, offset, length);
                    if (jpeg != null) return DecodeJpegToShortSide(jpeg, targetShortSide);
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine("Failed to extract thumbnail by offset (" + file.Name + "): " + ex.Message);
            }
        }

        return null;
    }

    /// <summary>
    /// 从 <paramref name="hintOffset"/> 附近扫首个 JPEG SOI（FF D8 FF）,再截取 <paramref name="length"/> 字节返回。
    /// 动机:IFD1 的 ThumbnailOffset 口径因相机/软件而异（TIFF-相对 vs 文件-绝对）,
    /// 还常有 firmware quirk 偏移几字节（实测 Sony ILCE-6100 偏 +6）。
    /// </summary>
    private static async Task<byte[]?> ReadJpegAroundAsync(IStorageFile file, int hintOffset, int length)
    {
        const int windowPad = 64;
        try
        {
            await using var s = await file.OpenReadAsync();
            long start = Math.Max(0, (long)hintOffset - windowPad);
            int want = length + windowPad * 2;
            s.Seek(start, SeekOrigin.Begin);
            var window = new byte[want];
            int read = await s.ReadAsync(window, 0, want);
            if (read < 100) return null;

            int soi = FindSoiPrefix(window, read);
            if (soi < 0) return null;

            int take = Math.Min(length, read - soi);
            if (take < 100) return null;

            var jpeg = new byte[take];
            Array.Copy(window, soi, jpeg, 0, take);
            return jpeg;
        }
        catch (Exception ex)
        {
            Console.WriteLine("ThumbnailService: scan SOI failed (" + file.Name + "): " + ex.Message);
            return null;
        }
    }

    private static int FindSoiPrefix(byte[] buffer, int count)
    {
        int limit = Math.Min(count, buffer.Length) - 3;
        for (int i = 0; i <= limit; i++)
        {
            if (buffer[i] == 0xFF && buffer[i + 1] == 0xD8 && buffer[i + 2] == 0xFF) return i;
        }
        return -1;
    }

    /// <summary>
    /// 把 JPEG 字节解码为目标短边 <paramref name="targetShortSide"/> 附近的位图。
    /// Avalonia 只提供 <c>DecodeToWidth</c>,我们用 SOF 解出来的真实宽高换算成等效"短边 = target"的目标宽度,
    /// 这样横竖图都能保证解码后短边 ≈ target,避免按宽度直接传 target 在横图上缩过头。
    /// 若字节本身短边已经 ≤ target（不需要缩小）,1:1 解码避免插值损失。
    /// </summary>
    private static Bitmap? DecodeJpegToShortSide(byte[]? data, int targetShortSide)
    {
        if (data == null || data.Length < 100 || !IsJpegBytes(data)) return null;
        try
        {
            int decodeWidth;
            var dims = JpegDimensionReader.TryReadDimensions(data);
            if (dims.width > 0 && dims.height > 0)
            {
                int byteShort = Math.Min(dims.width, dims.height);
                double ratio = (double)targetShortSide / byteShort;
                if (ratio >= 1.0) ratio = 1.0; // 不放大
                decodeWidth = Math.Max(1, (int)Math.Round(dims.width * ratio));
            }
            else
            {
                decodeWidth = targetShortSide;
            }
            using var ms = new MemoryStream(data);
            return Bitmap.DecodeToWidth(ms, decodeWidth);
        }
        catch (Exception ex)
        {
            Console.WriteLine("Failed to decode thumbnail bytes: " + ex.Message);
            return null;
        }
    }

    /// <summary>
    /// 判断字节是否为 JPEG（FF D8 起始）。
    /// </summary>
    private static bool IsJpegBytes(byte[] data)
    {
        if (data == null || data.Length < 3) return false;
        return data[0] == 0xFF && data[1] == 0xD8;
    }

    // ============================================================
    // 方向对齐 + letterbox 裁剪：纯几何变换,只依赖容器元数据声明。
    // ============================================================

    /// <summary>
    /// 若来源声明 IsPreRotated=true（如 HEIF 平台解码器）,bitmap 已是显示朝向,直接返回。
    /// 否则按 orientation 中的 (RotationCw, MirrorHorizontal) 把传感器朝向位图旋转/镜像到显示朝向。
    /// </summary>
    private static Bitmap AlignOrientationIfNeeded(Bitmap raw, ThumbnailSource src, ImageOrientationInfo orientation)
    {
        if (src.IsPreRotated) return raw;
        if (orientation.RotationDegreesCw == 0 && !orientation.MirrorHorizontal) return raw;

        try
        {
            var rotated = ApplyOrientation(raw, orientation.RotationDegreesCw, orientation.MirrorHorizontal);
            if (rotated == null) return raw;
            if (!ReferenceEquals(rotated, raw)) raw.Dispose();
            return rotated;
        }
        catch (Exception ex)
        {
            Console.WriteLine("ThumbnailService: orientation align failed: " + ex.Message);
            return raw;
        }
    }

    /// <summary>
    /// 用容器声明的显示纵横比作目标,从位图中央居中裁出最大内接矩形,去掉 letterbox 黑边。
    /// 比例已相符（容差 1%）时跳过裁剪。容器未声明传感器尺寸时跳过裁剪。
    /// </summary>
    private static Bitmap CropLetterboxByMetadata(Bitmap aligned, ImageOrientationInfo orientation)
    {
        double displayAspect = orientation.DisplayAspect;
        if (displayAspect <= 0) return aligned;

        int bw = aligned.PixelSize.Width;
        int bh = aligned.PixelSize.Height;
        if (bw <= 0 || bh <= 0) return aligned;

        double curAspect = (double)bw / bh;
        double diff = Math.Abs(curAspect - displayAspect) / displayAspect;
        if (diff < 0.01) return aligned;

        int targetW, targetH;
        if (curAspect > displayAspect)
        {
            // 当前更宽 → 左右有黑边 → 按目标比例裁宽
            targetH = bh;
            targetW = (int)Math.Round(bh * displayAspect);
        }
        else
        {
            // 当前更窄 → 上下有黑边 → 按目标比例裁高
            targetW = bw;
            targetH = (int)Math.Round(bw / displayAspect);
        }
        targetW = Math.Clamp(targetW, 1, bw);
        targetH = Math.Clamp(targetH, 1, bh);

        int offX = (bw - targetW) / 2;
        int offY = (bh - targetH) / 2;
        var sourceRect = new PixelRect(offX, offY, targetW, targetH);

        try
        {
            var format = aligned is WriteableBitmap wb && wb.Format.HasValue
                ? wb.Format.Value
                : PixelFormats.Bgra8888;
            var alphaFormat = aligned.AlphaFormat ?? AlphaFormat.Premul;
            var target = new WriteableBitmap(new PixelSize(targetW, targetH), aligned.Dpi, format, alphaFormat);
            using (var locked = target.Lock())
            {
                aligned.CopyPixels(sourceRect, locked.Address, locked.RowBytes * targetH, locked.RowBytes);
            }
            aligned.Dispose();
            return target;
        }
        catch (Exception ex)
        {
            Console.WriteLine("ThumbnailService: letterbox crop failed: " + ex.Message);
            return aligned;
        }
    }

    /// <summary>
    /// 把传感器朝向的位图按 (rotationCw, mirror) 变换到显示朝向,返回新位图。
    /// 变换顺序:平移到源中心 → （水平镜像）→ （顺时针旋转）→ 平移到目标中心。
    /// </summary>
    private static Bitmap? ApplyOrientation(Bitmap source, int rotationCw, bool mirror)
    {
        int sw = source.PixelSize.Width;
        int sh = source.PixelSize.Height;
        if (sw <= 0 || sh <= 0) return null;

        bool swap = rotationCw == 90 || rotationCw == 270;
        int dw = swap ? sh : sw;
        int dh = swap ? sw : sh;

        var rt = new RenderTargetBitmap(new PixelSize(dw, dh), source.Dpi);
        using var ctx = rt.CreateDrawingContext();

        var transform = Matrix.CreateTranslation(-sw / 2.0, -sh / 2.0);
        if (mirror)
        {
            transform *= Matrix.CreateScale(-1, 1);
        }
        if (rotationCw != 0)
        {
            transform *= Matrix.CreateRotation(Math.PI * rotationCw / 180.0);
        }
        transform *= Matrix.CreateTranslation(dw / 2.0, dh / 2.0);

        using (ctx.PushTransform(transform))
        {
            ctx.DrawImage(source, new Rect(0, 0, sw, sh));
        }
        return rt;
    }
}
