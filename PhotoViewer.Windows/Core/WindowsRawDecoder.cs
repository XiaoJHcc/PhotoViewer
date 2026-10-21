using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading;
using DirectN;
using PhotoViewer.Core.Color;
using PhotoViewer.Core.Image;
using WicNet;

namespace PhotoViewer.Windows.Core;

/// <summary>
/// Windows 侧 RAW 全分辨率解码入口，走系统 WIC「Microsoft Raw Image Decoder」（Raw Image Extension）。
/// 与 Apple 的 Core Image 路径同契约：产出**未旋转**的 8 位 sRGB 像素 + 源尺寸 + 待应用方向，
/// 由渲染层做顶点变换；色域按 WIC 报告（当前实测 raw 解码器不报 ICC，按 sRGB 解释）。
///
/// 几何：WIC 输出的帧比相机取景画面大（a7C II 上 7028×4688 对 7008×4672），多出来的是传感器边距。
/// 相机在 TIFF 标签 DefaultCropOrigin/DefaultCropSize（0xC61F/0xC620）里声明有效成像区，WIC 元数据
/// 以 <c>/ifd/{ushort=0xC61F}</c> 暴露；实测该原点就是把 WIC 输出对齐到内嵌整幅 JPEG 的精确偏移，
/// 故裁剪规则为「按相机声明的原点与尺寸裁剪，越界或缺失则用整帧」。
///
/// 像素格式：解码器只真正产出 8 位精度（48bpp/64bpp 目标是 8 位值 ×257 的放大，128bpp 浮点亦是 8 位数据），
/// 因此固定请求 32bppBGRA 并直接拷进池化缓冲（一次 CopyPixels 带裁剪矩形，无中间整幅拷贝）。
/// </summary>
internal static class WindowsRawDecoder
{
    /// <summary>TIFF 标签 DefaultCropOrigin（0xC61F）：有效成像区左上角。</summary>
    private const ushort DefaultCropOriginTag = 0xC61F;

    /// <summary>TIFF 标签 DefaultCropSize（0xC620）：有效成像区尺寸。</summary>
    private const ushort DefaultCropSizeTag = 0xC620;

    /// <summary>WIC 照片方向的元数据键（raw 帧同时提供 <c>/ifd/{ushort=274}</c>）。</summary>
    private const string OrientationMetadataKey = "System.Photo.Orientation";

    /// <summary>TIFF Orientation 标签的 WIC 查询路径。</summary>
    private const string OrientationQueryPath = "/ifd/{ushort=274}";

    /// <summary>TIFF 内嵌 JPEG 的偏移/长度标签（JPEGInterchangeFormat / -Length）。</summary>
    private const int JpegOffsetTag = 0x0201;
    private const int JpegLengthTag = 0x0202;

    /// <summary>已安装解码器覆盖的 RAW 扩展名集合（含 '.'，忽略大小写）；null 表示探测失败。</summary>
    private static readonly Lazy<IReadOnlySet<string>?> InstalledRawExtensions =
        new(ProbeInstalledRawExtensions, LazyThreadSafetyMode.ExecutionAndPublication);

    /// <summary>
    /// 判断该扩展名是否已被已安装的 WIC RAW 解码器覆盖（Raw Image Extension 未安装时为 false）。
    /// 首次调用枚举全部已安装解码器并缓存，之后为纯字典查询。
    /// </summary>
    /// <param name="extension">扩展名，可带或不带前导点（如 ".arw"、"arw"）。</param>
    /// <returns>可解则 true。</returns>
    internal static bool IsAvailable(string extension)
    {
        if (string.IsNullOrWhiteSpace(extension)) return false;

        var installed = InstalledRawExtensions.Value;
        if (installed == null) return false;

        var normalized = extension.StartsWith('.') ? extension : "." + extension;
        return installed.Contains(normalized);
    }

    /// <summary>
    /// 全分辨率解码 RAW：按相机声明的有效成像区裁剪，产出未旋转的 <see cref="PixelEncoding.Bgra32"/> 像素。
    /// 缓冲直接租自 <see cref="ImageBufferPool"/>，由 <see cref="DecodedImage"/> 持有并在最后一份租约释放时归还。
    /// </summary>
    /// <param name="path">RAW 文件路径。</param>
    /// <param name="cancellationToken">取消令牌；解码前与解码后各检查一次。</param>
    /// <param name="orientation">WIC 报告的 EXIF 方向（1-8）；元数据缺失时为 null，由调用方回退自有 EXIF。</param>
    /// <returns>解码产物；解码器缺失或失败返回 null。</returns>
    internal static unsafe DecodedImage? DecodeFull(string path, CancellationToken cancellationToken, out int? orientation)
    {
        orientation = null;
        cancellationToken.ThrowIfCancellationRequested();

        try
        {
            using var decoder = WicBitmapDecoder.Load(path, null, FileAccess.Read, WICDecodeOptions.WICDecodeMetadataCacheOnDemand);
            using var frame = decoder.GetFrame(0);
            int frameWidth = frame.Width, frameHeight = frame.Height;
            if (frameWidth <= 0 || frameHeight <= 0)
            {
                Console.WriteLine($"[WindowsRawDecoder] 帧尺寸无效：{path} {frameWidth}×{frameHeight}");
                return null;
            }

            orientation = ReadOrientation(frame);
            var crop = ReadCropRect(frame, frameWidth, frameHeight);

            // 8 位是这家解码器的精度上限（16 位目标只是 8 位值放大），32bppBGRA 最省一次转换。
            if (!frame.ConvertTo(WicPixelFormat.GUID_WICPixelFormat32bppBGRA, WICBitmapDitherType.WICBitmapDitherTypeNone,
                    null, 0, WICBitmapPaletteType.WICBitmapPaletteTypeCustom))
            {
                Console.WriteLine($"[WindowsRawDecoder] 像素格式转换失败：{path}");
                return null;
            }

            cancellationToken.ThrowIfCancellationRequested();

            int stride = checked(crop.Width * 4);
            var buffer = DecodedImage.RentPixels(checked(stride * crop.Height));
            try
            {
                fixed (byte* target = buffer)
                {
                    frame.CopyPixels(crop.X, crop.Y, crop.Width, crop.Height, (uint)(stride * crop.Height), (IntPtr)target, stride);
                }

                cancellationToken.ThrowIfCancellationRequested();

                // 解码器的 raw 帧不报 ICC（实测 0 个色彩上下文），按 sRGB 解释由 ImageColorSpace.Srgb 声明。
                return DecodedImage.CreatePooled(buffer, crop.Width, crop.Height, stride, PixelEncoding.Bgra32,
                    ImageColorSpace.Srgb, crop.Width, crop.Height, orientation ?? 1);
            }
            catch
            {
                ImageBufferPool.Return(buffer);
                throw;
            }
        }
        catch (OperationCanceledException)
        {
            // 取消不是解码失败，交给调用方区分。
            throw;
        }
        catch (Exception exception)
        {
            Console.WriteLine($"[WindowsRawDecoder] RAW 解码失败：{path} {exception.GetType().Name} 0x{exception.HResult:X8} {exception.Message}");
            return null;
        }
    }

    /// <summary>
    /// 便宜的尺寸探测（打开解码器 + 读首帧 + 裁剪元数据，约数毫秒），不解码像素。
    /// </summary>
    /// <param name="path">RAW 文件路径。</param>
    /// <returns>解码后会得到的像素尺寸；失败返回 null。</returns>
    internal static (int Width, int Height)? TryGetSize(string path)
    {
        try
        {
            using var decoder = WicBitmapDecoder.Load(path, null, FileAccess.Read, WICDecodeOptions.WICDecodeMetadataCacheOnDemand);
            using var frame = decoder.GetFrame(0);
            var crop = ReadCropRect(frame, frame.Width, frame.Height);
            return crop.Width > 0 && crop.Height > 0 ? (crop.Width, crop.Height) : null;
        }
        catch (Exception exception)
        {
            Console.WriteLine($"[WindowsRawDecoder] 尺寸探测失败：{path} {exception.GetType().Name} 0x{exception.HResult:X8} {exception.Message}");
            return null;
        }
    }

    /// <summary>
    /// RAW 解码不可用（扩展缺失或失败）时的兜底：取出相机写在文件里的整幅内嵌 JPEG，用 Skia 解成未旋转 Bgra32。
    /// 只接受**尺寸等于相机有效成像区**的内嵌图，避免把 1:1 以外的小预览冒充全分辨率；
    /// 尺寸期望先取 EXIF DefaultCropSize（不依赖 RAW 解码器在位），退回 WIC 帧裁剪尺寸。
    /// </summary>
    /// <param name="path">RAW 文件路径。</param>
    /// <returns>与全分辨率几何一致的内嵌预览；没有或不符则 null。</returns>
    internal static DecodedImage? DecodeEmbeddedFullPreview(string path)
    {
        try
        {
            // 一次读元数据同时拿到内嵌 JPEG 与相机声明的有效成像区，避免把 38 MB 的 RAW 解析两遍。
            var (jpeg, exifCrop) = ReadEmbeddedJpeg(path);

            // 尺寸期望先取 EXIF DefaultCropSize（不依赖 RAW 解码器在位），再退回 WIC 帧尺寸。
            var candidate = exifCrop ?? TryGetSize(path);
            if (candidate is not { } expected || expected.Width <= 0 || expected.Height <= 0) return null;
            if (jpeg == null) return null;

            using var stream = new MemoryStream(jpeg, writable: false);
            var decoded = SkiaImageCodec.Decode(stream);
            if (decoded == null) return null;

            if (decoded.Width == expected.Width && decoded.Height == expected.Height)
            {
                return decoded;
            }

            Console.WriteLine($"[WindowsRawDecoder] 内嵌预览尺寸 {decoded.Width}×{decoded.Height} 与有效成像区 {expected.Width}×{expected.Height} 不符，按不可用处理：{path}");
            decoded.Release();
            return null;
        }
        catch (Exception exception)
        {
            Console.WriteLine($"[WindowsRawDecoder] 内嵌预览读取失败：{path} {exception.GetType().Name} 0x{exception.HResult:X8} {exception.Message}");
            return null;
        }
    }

    /// <summary>
    /// 枚举已安装的 WIC 解码器，取 RAW 容器（Microsoft Raw Image Decoder / DNG Decoder）覆盖的扩展名集合。
    /// </summary>
    /// <returns>扩展名集合（含前导点）；枚举失败返回 null。</returns>
    private static IReadOnlySet<string>? ProbeInstalledRawExtensions()
    {
        try
        {
            var extensions = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var decoder in WicDecoder.AllComponents.OfType<WicDecoder>())
            {
                if (decoder.ContainerFormat != WicCodec.GUID_ContainerFormatRaw &&
                    decoder.ContainerFormat != WicCodec.GUID_ContainerFormatAdng)
                {
                    continue;
                }

                foreach (var extension in decoder.FileExtensionsList)
                {
                    if (string.IsNullOrWhiteSpace(extension)) continue;
                    extensions.Add(extension.StartsWith('.') ? extension : "." + extension);
                }
            }

            return extensions.Count > 0 ? extensions : null;
        }
        catch (Exception exception)
        {
            Console.WriteLine($"[WindowsRawDecoder] WIC 解码器枚举失败：{exception.GetType().Name} 0x{exception.HResult:X8} {exception.Message}");
            return null;
        }
    }

    /// <summary>
    /// 按相机声明的有效成像区计算裁剪矩形；缺元数据或越界时退回整帧。
    /// </summary>
    /// <param name="frame">WIC 首帧。</param>
    /// <param name="frameWidth">帧宽。</param>
    /// <param name="frameHeight">帧高。</param>
    /// <returns>裁剪矩形（帧坐标系，未旋转）。</returns>
    private static (int X, int Y, int Width, int Height) ReadCropRect(WicBitmapSource frame, int frameWidth, int frameHeight)
    {
        var full = (0, 0, frameWidth, frameHeight);
        try
        {
            var size = TryReadPair(frame, DefaultCropSizeTag);
            if (size is not { } s || s.First <= 0 || s.Second <= 0) return full;
            if (s.First > frameWidth || s.Second > frameHeight) return full;

            var origin = TryReadPair(frame, DefaultCropOriginTag) ?? (0, 0);
            int x = Math.Clamp(origin.First, 0, frameWidth - s.First);
            int y = Math.Clamp(origin.Second, 0, frameHeight - s.Second);
            return (x, y, s.First, s.Second);
        }
        catch (Exception exception)
        {
            Console.WriteLine($"[WindowsRawDecoder] 裁剪元数据读取失败，改用整帧：{exception.GetType().Name} {exception.Message}");
            return full;
        }
    }

    /// <summary>读取给定 TIFF 标签的二维整数元数据（WIC 以 VT_UI4|VT_VECTOR 暴露）。</summary>
    /// <param name="frame">WIC 首帧。</param>
    /// <param name="tag">TIFF 标签号。</param>
    /// <returns>两个整数；缺失或不合法返回 null。</returns>
    private static (int First, int Second)? TryReadPair(WicBitmapSource frame, ushort tag)
    {
        using var reader = frame.GetMetadataReader();
        var key = $"/ifd/{{ushort={tag.ToString(CultureInfo.InvariantCulture)}}}";
        if (!reader.TryGetMetadataByName(key, out object? value, out _)) return null;
        return ToPair(value);
    }

    /// <summary>读取 EXIF 方向；WIC 用 <c>System.Photo.Orientation</c> 与 TIFF 274 暴露同一数值。</summary>
    /// <param name="frame">WIC 首帧。</param>
    /// <returns>1-8 的方向；缺失或不合法返回 null。</returns>
    private static int? ReadOrientation(WicBitmapSource frame)
    {
        try
        {
            using var reader = frame.GetMetadataReader();
            foreach (var key in new[] { OrientationMetadataKey, OrientationQueryPath })
            {
                if (!reader.TryGetMetadataByName(key, out object? value, out _)) continue;
                if (ToInt(value) is int orientation && orientation is >= 1 and <= 8) return orientation;
            }
        }
        catch (Exception exception)
        {
            Console.WriteLine($"[WindowsRawDecoder] 方向元数据读取失败：{exception.GetType().Name} {exception.Message}");
        }

        return null;
    }

    /// <summary>把元数据值（数组/标量/字符串）转成两个整数。</summary>
    /// <param name="value">元数据值。</param>
    /// <returns>两个整数；不合法返回 null。</returns>
    private static (int First, int Second)? ToPair(object? value)
    {
        switch (value)
        {
            case Array array when array.Length >= 2:
            {
                if (ToInt(array.GetValue(0)) is int first && ToInt(array.GetValue(1)) is int second) return (first, second);
                return null;
            }
            case string text:
            {
                var parts = text.Split([' ', ','], StringSplitOptions.RemoveEmptyEntries);
                if (parts.Length >= 2 &&
                    int.TryParse(parts[0], NumberStyles.Integer, CultureInfo.InvariantCulture, out int first) &&
                    int.TryParse(parts[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out int second))
                {
                    return (first, second);
                }

                return null;
            }
            default:
                return null;
        }
    }

    /// <summary>把元数据标量（ushort/uint/int/long）转成 int。</summary>
    /// <param name="value">元数据值。</param>
    /// <returns>整数值；不可转换返回 null。</returns>
    private static int? ToInt(object? value) => value switch
    {
        null => null,
        Array array when array.Length >= 1 => ToInt(array.GetValue(0)),
        ushort v => v,
        short v => v,
        uint v when v <= int.MaxValue => (int)v,
        int v => v,
        long v when v is >= int.MinValue and <= int.MaxValue => (int)v,
        string text when int.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out int parsed) => parsed,
        _ => null,
    };

    /// <summary>
    /// 一次读完兜底所需的两项信息：相机声明的有效成像区（DefaultCropSize，0xC620）与文件里最大的那段内嵌 JPEG。
    /// 内嵌 JPEG 由相机整幅写出（Sony 为 IFD2 的 JpgFromRaw），偏移/长度取 TIFF 的 JPEGInterchangeFormat/-Length。
    /// </summary>
    /// <param name="path">RAW 文件路径。</param>
    /// <returns>JPEG 字节（找不到为 null）与有效成像区尺寸（无标签为 null）。</returns>
    private static (byte[]? Jpeg, (int Width, int Height)? CropSize) ReadEmbeddedJpeg(string path)
    {
        var candidates = new List<(long Offset, long Length)>();
        (int Width, int Height)? cropSize = null;

        foreach (var directory in MetadataExtractor.ImageMetadataReader.ReadMetadata(path))
        {
            long offset = -1, length = -1;
            foreach (var tag in directory.Tags)
            {
                if (tag.Type == DefaultCropSizeTag && cropSize == null)
                {
                    if (ToPair(tag.Description) is { } pair && pair.First > 0 && pair.Second > 0)
                    {
                        cropSize = (pair.First, pair.Second);
                    }
                }
                else if (tag.Type == JpegOffsetTag)
                {
                    offset = ToLong(directory, tag.Type);
                }
                else if (tag.Type == JpegLengthTag)
                {
                    length = ToLong(directory, tag.Type);
                }
            }

            if (offset > 0 && length > 0) candidates.Add((offset, length));
        }

        if (candidates.Count == 0) return (null, cropSize);

        // 同一文件里通常还有 1:1 小预览与 IFD1 缩略图，取最长的那段即相机的整幅 JPEG。
        var best = candidates.OrderByDescending(c => c.Length).First();
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        if (best.Offset + best.Length > stream.Length) return (null, cropSize);

        stream.Position = best.Offset;
        var buffer = new byte[best.Length];
        stream.ReadExactly(buffer);

        // JPEG SOI 校验：偏移来自不可信元数据，读到的不是 JPEG 就放弃。
        return buffer.Length > 2 && buffer[0] == 0xFF && buffer[1] == 0xD8 ? (buffer, cropSize) : (null, cropSize);
    }

    /// <summary>读取标签的原始整数值（优先结构化取值，退回描述文本的前导整数）。</summary>
    /// <param name="directory">元数据目录。</param>
    /// <param name="tagType">标签号。</param>
    /// <returns>整数值；不可用返回 -1。</returns>
    private static long ToLong(MetadataExtractor.Directory directory, int tagType)
    {
        try
        {
            var raw = directory.GetObject(tagType);
            if (raw != null) return Convert.ToInt64(raw, CultureInfo.InvariantCulture);
        }
        catch
        {
            // 结构化取值不可用时落到描述文本解析。
        }

        string? description = directory.GetDescription(tagType);
        if (string.IsNullOrEmpty(description)) return -1;

        int end = 0;
        while (end < description.Length && (char.IsDigit(description[end]) || (end == 0 && description[end] == '-'))) end++;
        return end > 0 && long.TryParse(description.AsSpan(0, end), NumberStyles.Integer, CultureInfo.InvariantCulture, out long parsed)
            ? parsed
            : -1;
    }
}
