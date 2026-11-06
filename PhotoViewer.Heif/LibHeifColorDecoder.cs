using System;
using System.Runtime.InteropServices;
using System.Threading;
using LibHeifSharp;
using PhotoViewer.Core.Color;
using PhotoViewer.Core.Image;
using SkiaSharp;

namespace PhotoViewer.Core.Image.Heif;

/// <summary>libheif 软件解码的产物口径(显示朝向,方向恒为 1)。</summary>
internal enum LibHeifOutput
{
    /// <summary>BGRA32 + sRGB 标签:缩略图/分析口径(与 <see cref="IHeifDecoder"/> 契约一致)。</summary>
    Bgra32Srgb,

    /// <summary>线性 RGBA16F + LinearSrgb:Windows 主图线性表面。</summary>
    RgbaF16ScRgb,

    /// <summary>RGBA8 + sRGB 标签:Android 8 位展示档(sRGB 表面)。</summary>
    Rgba8Srgb,

    /// <summary>RGBA8 + Display P3 标签:Android 8 位展示档(P3 表面)。</summary>
    Rgba8DisplayP3,
}

/// <summary>容器色彩声明能否交给"只认 ICC 的系统解码器"处理。</summary>
internal enum LibHeifColorVerdict
{
    /// <summary>声明在 SDR 支持面内(或带 ICC/无声明),系统解码不会把 HDR 当 SDR。</summary>
    Supported,

    /// <summary>容器解析不受支持:无法判定,调用方不得据此假定 SDR。</summary>
    UnsupportedContainer,

    /// <summary>声明是 HDR(PQ/HLG)或超出 SDR 支持面:不得按 SDR 输出。</summary>
    Rejected,
}

/// <summary>
/// libheif 软件解码的共享色彩出口:Windows 主图回落与 Android 主图/缩略图回落共用同一份实现。
/// </summary>
/// <remarks>
/// 色彩:libheif 回落的像素是**源色彩空间内的编码值**(它只做 YCbCr 反矩阵,不做色域映射),
/// 所以先按文件真实声明打标签(ICC 优先,其次 <c>nclx</c>),再经共享 <see cref="ColorEngine"/> 转到目标口径;
/// 共享标签词汇表达不了的组合(BT.2020 原色、BT.709 / 伽马曲线)用同一套 skcms 机制换算,不自造矩阵。
/// PQ/HLG 不做 tone map,显式拒绝并记录原因(<see cref="HeifLoader.SetLastDecodeError"/>),绝不按 SDR 输出。
///
/// 位深契约:8 位源走交错 RGB24 → BGRA32;10/12 位源请求 libheif 的 16 位交错平面
/// (<c>InterleavedRgb48LE</c>),直接转成"源空间编码值"的半浮点缓冲再进色彩转换,不先量化到 8 位。
/// 归一化除数取平面自身声明的码值范围,不看像素内容(见 <see cref="ConvertRgb48ToCodeValues"/>)。
///
/// 几何:libheif 默认应用容器 <c>irot</c>/<c>imir</c>,产物已是显示朝向,故
/// <see cref="DecodedImage.Orientation"/> 恒为 1;全幅几何取解码后的真实画布尺寸
/// (<see cref="DecodedImage.SourceWidth"/>/<see cref="DecodedImage.SourceHeight"/>),代理缩放后保持不变。
/// 不按 EXIF 猜 HEIF 方向。
/// </remarks>
internal static class LibHeifColorDecoder
{
    /// <summary>
    /// 解码 HEIF 主图并转成目标口径。
    /// </summary>
    /// <param name="data">完整文件字节。</param>
    /// <param name="maxLongEdge">目标长边;null 表示全分辨率(libheif 无子采样能力,代理按全解码 + Mitchell 精缩)。</param>
    /// <param name="output">产物口径。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <param name="reason">返回 null 时的原因(成功时为 null)。</param>
    /// <returns>显示朝向的解码产物;色彩不能如实解释或解码失败返回 null(原因同时写入 <see cref="HeifLoader.LastDecodeError"/>)。</returns>
    internal static DecodedImage? Decode(byte[] data, int? maxLongEdge, LibHeifOutput output,
        CancellationToken cancellationToken, out string? reason)
        => Decode(data, maxLongEdge, output, forcedBitDepth: null, cancellationToken, out reason);

    /// <summary>
    /// 同上,另可强制解码位深(验证用:在只有 8 位夹具的环境里核对 16 位平面的字节序与码值归一化)。
    /// </summary>
    internal static DecodedImage? Decode(byte[] data, int? maxLongEdge, LibHeifOutput output, int? forcedBitDepth,
        CancellationToken cancellationToken, out string? reason)
    {
        reason = null;
        cancellationToken.ThrowIfCancellationRequested();
        try
        {
            using var context = new HeifContext(data);
            using var imageHandle = context.GetPrimaryImageHandle();
            if (imageHandle == null)
            {
                reason = "HEIF 文件中没有可解码的主图像";
                HeifLoader.SetLastDecodeError(reason);
                return null;
            }

            var tag = ResolveColorTag(imageHandle);
            if (tag.Refusal is not null)
            {
                reason = "色彩超出支持面:" + tag.Refusal;
                HeifLoader.SetLastDecodeError(reason);
                Console.WriteLine("[LibHeifColorDecoder] " + reason);
                return null;
            }

            var bitDepth = forcedBitDepth ?? imageHandle.BitDepth;
            var wide = bitDepth > 8;
            using var image = imageHandle.Decode(
                HeifColorspace.Rgb,
                wide ? HeifChroma.InterleavedRgb48LE : HeifChroma.InterleavedRgb24);
            if (image == null)
            {
                reason = "libheif 解码返回空图";
                HeifLoader.SetLastDecodeError(reason);
                return null;
            }

            cancellationToken.ThrowIfCancellationRequested();

            // 中间图带上真实标签:能对上共享标签词汇时交给 ColorEngine,否则只作为码值容器。
            var codedSpace = tag.NeedsSkcms ? ImageColorSpace.Legacy : tag.ColorSpace;
            string? wideReason = null;
            DecodedImage? coded = wide
                ? ConvertRgb48ToCodeValues(image, bitDepth, codedSpace, tag.IccProfile, out wideReason)
                : ConvertHeifImageToBgra(image, codedSpace, tag.IccProfile);
            if (coded is null)
            {
                reason = wide ? "高位深 libheif 解码失败:" + (wideReason ?? "未知原因") : "libheif 像素转换失败";
                HeifLoader.SetLastDecodeError(reason);
                Console.WriteLine("[LibHeifColorDecoder] " + reason);
                return null;
            }

            DecodedImage? full;
            try
            {
                full = ConvertToTarget(coded, tag, output);
            }
            finally
            {
                coded.Release();
            }

            if (full is null)
            {
                reason = "色彩转换失败";
                HeifLoader.SetLastDecodeError(reason);
                Console.WriteLine("[LibHeifColorDecoder] " + reason);
                return null;
            }

            // 取消/缩放失败都不能把 full 的租约留在无人持有的产物上:成功返回时把所有权交给调用方,
            // 其余所有路径(含取消 throw)由 finally 释放。
            try
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (maxLongEdge is not int limit)
                {
                    var result = full;
                    full = null;
                    return result;
                }

                var scaled = SkiaImageCodec.ScaleToLongEdge(full, limit);
                if (scaled == null)
                {
                    // null = 不需缩放(源已小于目标)或缩放失败,用全图保证正确性
                    var result = full;
                    full = null;
                    return result;
                }

                try
                {
                    cancellationToken.ThrowIfCancellationRequested();
                }
                catch
                {
                    scaled.Release();
                    throw;
                }

                full.Release();
                full = null;
                return scaled;
            }
            finally
            {
                full?.Release();
            }
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            reason = "libheif 解码失败:" + ex.Message;
            HeifLoader.SetLastDecodeError(reason);
            Console.WriteLine("[LibHeifColorDecoder] " + reason);
            return null;
        }
    }

    /// <summary>
    /// 只读容器色彩声明,判定能否交给"只按 ICC 做色彩管理"的系统解码器(PQ/HLG 会被当 SDR 吞掉)。
    /// 容器解析器读不了的结构再用 libheif 句柄读一次(只读属性,不解码);两条路都判不了才返回
    /// <see cref="LibHeifColorVerdict.UnsupportedContainer"/>,调用方不得据此假定 SDR。
    /// </summary>
    internal static LibHeifColorVerdict InspectColor(byte[] data, out string? reason)
    {
        reason = null;
        var container = HeifContainer.TryParse(data, out var parseReason);
        if (container is not null)
        {
            var color = container.Color;
            if (color.HasNclx && color.Transfer is 16 or 18)
                return JudgeSdr(color.Transfer, out reason);
            if (color.HasIccProfile || !color.HasNclx) return LibHeifColorVerdict.Supported;
            return JudgeSdr(color.Transfer, out reason);
        }

        // 容器解析器不认这个结构:用 libheif 句柄读色彩声明(能读属性即可,不必解得了该编码)。
        var handleVerdict = InspectColorWithHandle(data, out var handleReason);
        if (handleVerdict != LibHeifColorVerdict.UnsupportedContainer)
        {
            reason = handleReason;
            return handleVerdict;
        }

        reason = (parseReason ?? "容器不受支持") + "；" + (handleReason ?? "libheif 句柄也读不到色彩声明");
        return LibHeifColorVerdict.UnsupportedContainer;
    }

    /// <summary>用 libheif 主图句柄读色彩声明(不解码像素);读不到返回 <see cref="LibHeifColorVerdict.UnsupportedContainer"/>。</summary>
    private static LibHeifColorVerdict InspectColorWithHandle(byte[] data, out string? reason)
    {
        reason = null;
        try
        {
            using var context = new HeifContext(data);
            using var handle = context.GetPrimaryImageHandle();
            if (handle == null)
            {
                reason = "libheif 没有主图句柄";
                return LibHeifColorVerdict.UnsupportedContainer;
            }

            return JudgeFromTag(handle, out reason);
        }
        catch (Exception ex)
        {
            reason = "libheif 读取句柄色彩声明失败（" + ex.GetType().Name + ": " + ex.Message + "）";
            return LibHeifColorVerdict.UnsupportedContainer;
        }
    }

    /// <summary>把句柄的色彩标签折算成色彩支持面判定。</summary>
    private static LibHeifColorVerdict JudgeFromTag(HeifImageHandle handle, out string? reason)
    {
        var tag = ResolveColorTag(handle);
        reason = tag.Refusal is null ? null : "色彩超出支持面:" + tag.Refusal;
        return tag.Refusal is null ? LibHeifColorVerdict.Supported : LibHeifColorVerdict.Rejected;
    }

    /// <summary>传输特性代码 → 色彩支持面判定。</summary>
    private static LibHeifColorVerdict JudgeSdr(int transfer, out string? reason)
    {
        reason = null;
        if (transfer is 16 or 18)
        {
            reason = "传输特性 " + transfer + "(PQ/HLG)是 HDR,系统解码会当成 SDR,已拒绝";
            return LibHeifColorVerdict.Rejected;
        }
        if (!IsSdrTransfer(transfer))
        {
            reason = "传输特性 " + transfer + " 不受支持,拒绝按 SDR 输出";
            return LibHeifColorVerdict.Rejected;
        }
        return LibHeifColorVerdict.Supported;
    }

    /// <summary>libheif 句柄声明到"可解释的色彩标签"的判定结果。</summary>
    /// <param name="ColorSpace">像素应按哪个共享色彩标签解释。</param>
    /// <param name="IccProfile">ICC 字节(<see cref="ImageColorSpace.Icc"/> 时非空)。</param>
    /// <param name="NeedsSkcms">标签词汇表达不了(如 BT.2020 原色、BT.709 曲线):用 skcms 直接换算。</param>
    /// <param name="Primaries">nclx 原色代码(仅 <paramref name="NeedsSkcms"/> 时使用)。</param>
    /// <param name="Transfer">nclx 传输特性代码(仅 <paramref name="NeedsSkcms"/> 时使用)。</param>
    /// <param name="Refusal">不能如实解释时的原因(为空表示可解释)。</param>
    private readonly record struct HeifColorTag(
        ImageColorSpace ColorSpace,
        byte[]? IccProfile,
        bool NeedsSkcms,
        int Primaries,
        int Transfer,
        string? Refusal)
    {
        /// <summary>构造一条拒绝原因。</summary>
        /// <param name="reason">原因文本。</param>
        /// <returns>带原因的标签。</returns>
        public static HeifColorTag Refuse(string reason)
            => new(ImageColorSpace.Legacy, null, false, 0, 0, reason);
    }

    /// <summary>
    /// 读文件的色彩声明并决定怎么如实解释:ICC 优先(HEIF 规定),其次 <c>nclx</c> 三元组。
    /// </summary>
    /// <remarks>
    /// 共享 <see cref="ColorEngine"/> 的标签词汇只有 Legacy/sRGB/Display P3/Adobe RGB/线性/ICC,
    /// 既没有 BT.2020 原色也没有 BT.709 曲线,所以这几类源用 skcms(Skia 给出的原色与传递函数)
    /// 换算,不自造矩阵;能对上标签的常见组合仍交 <see cref="ColorEngine"/>。
    /// </remarks>
    /// <param name="handle">libheif 主图句柄。</param>
    /// <returns>标签或拒绝原因。</returns>
    private static HeifColorTag ResolveColorTag(HeifImageHandle handle)
    {
        byte[]? icc;
        try
        {
            icc = handle.IccColorProfile?.GetIccProfileBytes();
        }
        catch (Exception ex)
        {
            return HeifColorTag.Refuse("读取 ICC 失败:" + ex.Message);
        }

        HeifNclxColorProfile? nclx;
        try
        {
            nclx = handle.NclxColorProfile;
        }
        catch (Exception ex)
        {
            return HeifColorTag.Refuse("读取 nclx 失败:" + ex.Message);
        }

        var transfer = nclx == null ? 2 : (int)nclx.TransferCharacteristics;
        if (transfer is 16 or 18)
        {
            return HeifColorTag.Refuse(
                "传输特性 " + transfer + "(PQ/HLG)是 HDR,不做 tone map,拒绝按 SDR 输出");
        }
        if (icc is { Length: > 0 }) return new HeifColorTag(ImageColorSpace.Icc, icc, false, 0, 0, null);
        if (nclx is null) return new HeifColorTag(ImageColorSpace.Srgb, null, false, 0, 0, null);
        var primaries = (int)nclx.ColorPrimaries;

        if (!IsSdrTransfer(transfer))
        {
            return HeifColorTag.Refuse("传输特性 " + transfer + " 不受支持");
        }

        // 传递曲线是 sRGB 时可以直接落到已有标签上;BT.709 曲线(1/6/14/15)没有对应标签,交 skcms。
        var srgbCurve = transfer is 2 or 13;
        return primaries switch
        {
            // 1 = BT.709、2 = 未指定:原色等同 sRGB。
            1 or 2 => srgbCurve
                ? new HeifColorTag(ImageColorSpace.Srgb, null, false, primaries, transfer, null)
                : new HeifColorTag(ImageColorSpace.Legacy, null, true, primaries, transfer, null),
            12 => srgbCurve
                ? new HeifColorTag(ImageColorSpace.DisplayP3, null, false, primaries, transfer, null)
                : new HeifColorTag(ImageColorSpace.Legacy, null, true, primaries, transfer, null),
            9 => new HeifColorTag(ImageColorSpace.Legacy, null, true, primaries, transfer, null),
            _ => HeifColorTag.Refuse("原色 " + primaries + " 不受支持"),
        };
    }

    /// <summary>是否为本站点支持的 SDR 传递特性(1/6/14/15 = BT.709 曲线族,2/13 = sRGB,4 = 伽马 2.2,5 = 伽马 2.8)。</summary>
    /// <param name="transfer">nclx 传输特性代码。</param>
    /// <returns>支持返回 true。</returns>
    private static bool IsSdrTransfer(int transfer) => transfer is 1 or 2 or 4 or 5 or 6 or 13 or 14 or 15;

    /// <summary>
    /// 把"源色彩空间码值"的中间图按真实标签解释,转到目标口径。
    /// </summary>
    /// <remarks>
    /// 中间图有两种位宽:8 位源为 BGRA32,10/12 位源为 RGBA 半浮点码值
    /// (由 <see cref="ConvertRgb48ToCodeValues"/> 从 48 位平面直接得到,不经过 8 位量化)。
    /// 中间图的租约归调用方,本方法不释放它。
    /// </remarks>
    /// <param name="coded">源空间码值的中间图。</param>
    /// <param name="tag">色彩标签。</param>
    /// <param name="output">目标口径。</param>
    /// <returns>目标产物;失败返回 null。</returns>
    private static DecodedImage? ConvertToTarget(DecodedImage coded, HeifColorTag tag, LibHeifOutput output)
    {
        if (tag.NeedsSkcms) return ConvertWithSkcms(coded, tag, output);

        // 码值已是 sRGB8 BGRA 且目标就是 sRGB8:直接复用(借一份租约给结果)。
        // 高位深源这里是 RGBA16F(标签也可能是 sRGB),不能零拷贝 —— 否则会把 F16 当 BGRA32 交出去。
        if (output == LibHeifOutput.Bgra32Srgb && coded.Encoding == PixelEncoding.Bgra32
            && tag.ColorSpace == ImageColorSpace.Srgb)
        {
            if (!coded.TryAcquire()) return null;
            return coded;
        }

        var (encoding, colorSpace) = TargetOf(output);
        return ColorEngine.ToDisplayEncoding(coded, encoding, colorSpace);
    }

    /// <summary>目标口径 → (展示编码, 色彩标签)。</summary>
    private static (PixelEncoding Encoding, ImageColorSpace ColorSpace) TargetOf(LibHeifOutput output) => output switch
    {
        LibHeifOutput.Bgra32Srgb => (PixelEncoding.Bgra32, ImageColorSpace.Srgb),
        LibHeifOutput.RgbaF16ScRgb => (PixelEncoding.RgbaF16, ImageColorSpace.LinearSrgb),
        LibHeifOutput.Rgba8Srgb => (PixelEncoding.Rgba8, ImageColorSpace.Srgb),
        LibHeifOutput.Rgba8DisplayP3 => (PixelEncoding.Rgba8, ImageColorSpace.DisplayP3),
        _ => throw new ArgumentOutOfRangeException(nameof(output)),
    };

    /// <summary>目标口径对应的 skcms 色彩空间(调用方负责释放)。</summary>
    private static SKColorSpace CreateTargetSpace(LibHeifOutput output) => output switch
    {
        LibHeifOutput.Bgra32Srgb or LibHeifOutput.Rgba8Srgb => SKColorSpace.CreateSrgb(),
        LibHeifOutput.RgbaF16ScRgb => SKColorSpace.CreateSrgbLinear(),
        LibHeifOutput.Rgba8DisplayP3 => SKColorSpace.CreateRgb(SKColorSpaceTransferFn.Srgb, SKColorSpaceXyz.DisplayP3),
        _ => throw new ArgumentOutOfRangeException(nameof(output)),
    };

    /// <summary>
    /// 共享标签词汇表达不了的 <c>nclx</c> 源(BT.2020 原色、BT.709 / 伽马 2.2 / 伽马 2.8 曲线):
    /// 用与 <see cref="ColorEngine"/> 同一套 skcms 机制(经 Skia 色彩空间)转到目标口径。
    /// 原色/传递函数取自 Skia 的预设(<c>SKColorSpaceXyz</c>/<c>SKColorSpaceTransferFn</c>),
    /// 不在这里自造矩阵或曲线。
    /// </summary>
    /// <param name="coded">源空间码值的中间图(BGRA32 或 RGBA 半浮点)。</param>
    /// <param name="tag">色彩标签(含 nclx 代码)。</param>
    /// <param name="output">目标口径。</param>
    /// <returns>目标产物;失败返回 null。</returns>
    private static unsafe DecodedImage? ConvertWithSkcms(DecodedImage coded, HeifColorTag tag, LibHeifOutput output)
    {
        var wide = coded.Encoding == PixelEncoding.RgbaF16;
        var (targetEncoding, targetColorSpace) = TargetOf(output);
        var targetBytes = targetEncoding == PixelEncoding.RgbaF16 ? 8 : 4;
        var targetType = targetEncoding switch
        {
            PixelEncoding.RgbaF16 => SKColorType.RgbaF16,
            PixelEncoding.Bgra32 => SKColorType.Bgra8888,
            _ => SKColorType.Rgba8888,
        };
        var stride = checked(coded.Width * targetBytes);
        var buffer = DecodedImage.RentPixels(checked(stride * coded.Height));
        try
        {
            fixed (byte* sourcePixels = coded.Pixels)
            fixed (byte* targetPixels = buffer)
            {
                using var sourceSpace = SKColorSpace.CreateRgb(TransferFnOf(tag.Transfer), XyzOf(tag.Primaries));
                using var destinationSpace = CreateTargetSpace(output);
                var sourceInfo = new SKImageInfo(
                    coded.Width,
                    coded.Height,
                    wide ? SKColorType.RgbaF16 : SKColorType.Bgra8888,
                    wide ? SKAlphaType.Premul : SKAlphaType.Opaque,
                    sourceSpace);
                var targetInfo = new SKImageInfo(
                    coded.Width,
                    coded.Height,
                    targetType,
                    SKAlphaType.Premul,
                    destinationSpace);
                using var pixmap = new SKPixmap(sourceInfo, (IntPtr)sourcePixels, coded.Stride);
                if (!pixmap.ReadPixels(targetInfo, (IntPtr)targetPixels, stride))
                {
                    ImageBufferPool.Return(buffer);
                    return null;
                }
            }

            return DecodedImage.CreatePooled(
                buffer,
                coded.Width,
                coded.Height,
                stride,
                targetEncoding,
                targetColorSpace,
                coded.SourceWidth,
                coded.SourceHeight,
                1);
        }
        catch
        {
            ImageBufferPool.Return(buffer);
            throw;
        }
    }

    /// <summary>nclx 原色代码 → Skia 的基色矩阵(BT.2020 与 Display P3 有独立预设,其余按 sRGB)。</summary>
    /// <param name="primaries">nclx 原色代码。</param>
    /// <returns>Skia 基色矩阵。</returns>
    internal static SKColorSpaceXyz XyzOf(int primaries) => primaries switch
    {
        9 => SKColorSpaceXyz.Rec2020,
        12 => SKColorSpaceXyz.DisplayP3,
        _ => SKColorSpaceXyz.Srgb,
    };

    /// <summary>
    /// nclx 传输特性代码 → Skia 的传递函数(解码方向)。1/6/14/15 用 BT.709/2020 曲线族预设,
    /// 4 = 伽马 2.2、5 = 伽马 2.8 用纯幂参数(a=1, 其余为 0,指数取 2.2/2.8),其余按 sRGB。
    /// </summary>
    /// <param name="transfer">nclx 传输特性代码。</param>
    /// <returns>Skia 传递函数。</returns>
    internal static SKColorSpaceTransferFn TransferFnOf(int transfer) => transfer switch
    {
        1 or 6 or 14 or 15 => SKColorSpaceTransferFn.Rec2020,
        4 => new SKColorSpaceTransferFn { G = 2.2f, A = 1f },
        5 => new SKColorSpaceTransferFn { G = 2.8f, A = 1f },
        _ => SKColorSpaceTransferFn.Srgb,
    };

    /// <summary>
    /// 把 libheif 的 16 位交错 RGB 平面(InterleavedRgb48LE,每像素 6 字节)转成"源空间编码值"的
    /// RGBA 半浮点缓冲:每个通道按**该平面自身声明的码值范围**归一化到 [0,1],不经过 8 位量化。
    /// </summary>
    /// <remarks>
    /// 归一化除数取自 libheif 的 <c>heif_image_get_bits_per_pixel_range</c>(见 <see cref="TryGetPlaneBitDepthRange"/>),
    /// **不看像素内容**:color 正确性不能靠图像亮度反推位深——全暗的 12 位图最大值可能只有几十,
    /// 按最大值归一化会把它整体提亮,而带白点的图又恰好"看起来对",这种启发式必然时对时错。
    /// 实测 libheif 会把 8 位源按 48 位通道输出时提升到 10 位码值(平面范围 10),所以既不能用容器声明位深
    /// 当除数,也不能假设 16 位满量程。
    /// </remarks>
    /// <param name="heifImage">libheif 解码结果(交错 16 位 RGB,小端)。</param>
    /// <param name="bitDepth">句柄声明的解码位深(仅在读不到平面范围时作为元数据回退)。</param>
    /// <param name="colorSpace">中间图标签(能对上共享词汇时即真实标签,否则 Legacy)。</param>
    /// <param name="iccProfile">ICC 字节(<see cref="ImageColorSpace.Icc"/> 时给出)。</param>
    /// <param name="reason">失败原因。</param>
    /// <returns>RGBA 半浮点码值图;失败返回 null。</returns>
    internal static unsafe DecodedImage? ConvertRgb48ToCodeValues(
        HeifImage heifImage,
        int bitDepth,
        ImageColorSpace colorSpace,
        byte[]? iccProfile,
        out string? reason)
    {
        reason = null;
        var width = (int)heifImage.Width;
        var height = (int)heifImage.Height;
        if (width <= 0 || height <= 0)
        {
            reason = "尺寸非法";
            return null;
        }

        var plane = heifImage.GetPlane(HeifChannel.Interleaved);
        if (plane == null)
        {
            reason = "缺少交错平面";
            return null;
        }

        var sourceStride = (int)plane.Stride;
        const int bytesPerPixel = 6;
        if (sourceStride < checked(width * bytesPerPixel))
        {
            reason = "行跨距小于宽 × 6 字节";
            return null;
        }

        var sourcePtr = (byte*)plane.Scan0;
        if (sourcePtr == null)
        {
            reason = "平面指针为空";
            return null;
        }

        // 归一化除数只取平面自身声明的码值范围(库元数据),不扫像素、不看白点:
        // libheif 对 8 位源按 48 位通道输出时会提升到 10 位码值,容器声明位深也不等于该范围。
        int codeBits;
        if (TryGetPlaneBitDepthRange(heifImage) is int planeBits)
        {
            codeBits = planeBits;
        }
        else
        {
            // 读不到平面范围时退回句柄声明的位深(仍是元数据,不是按亮度猜);libheif 提升位深时可能偏亮,
            // 因此这里明确留痕,便于发现 binding/libheif 变化。
            codeBits = bitDepth is >= 1 and <= 16 ? bitDepth : 16;
            Console.WriteLine(
                $"[LibHeifColorDecoder] 读不到 16 位平面的码值范围,退回句柄声明位深 {codeBits} 归一化(容器声明 {bitDepth})");
        }

        var scaleCode = (1 << codeBits) - 1;
        var inverse = 1f / scaleCode;
        var stride = checked(width * 8);
        var buffer = DecodedImage.RentPixels(checked(stride * height));
        try
        {
            fixed (byte* targetPixels = buffer)
            {
                for (var y = 0; y < height; y++)
                {
                    var source = (ushort*)(sourcePtr + ((long)y * sourceStride));
                    var target = (ushort*)(targetPixels + ((long)y * stride));
                    for (var x = 0; x < width; x++, source += 3, target += 4)
                    {
                        target[0] = BitConverter.HalfToUInt16Bits((Half)(source[0] * inverse));
                        target[1] = BitConverter.HalfToUInt16Bits((Half)(source[1] * inverse));
                        target[2] = BitConverter.HalfToUInt16Bits((Half)(source[2] * inverse));
                        target[3] = 0x3C00; // 1.0h:不透明
                    }
                }
            }

            return DecodedImage.CreatePooled(
                buffer,
                width,
                height,
                stride,
                PixelEncoding.RgbaF16,
                colorSpace,
                width,
                height,
                1,
                iccProfile);
        }
        catch
        {
            ImageBufferPool.Return(buffer);
            throw;
        }
    }

    /// <summary>
    /// 读 libheif 为交错平面报的码值位深范围(即 <c>heif_image_get_bits_per_pixel_range</c>);读不到返回 null。
    /// </summary>
    /// <remarks>
    /// LibHeifSharp 3.2 的公开面没有平面位深:<see cref="HeifPlaneData"/> 只有 Width/Height/Stride/Channel/Scan0,
    /// 而 <see cref="HeifImageHandle.BitDepth"/> 是容器声明(实测 8 位源按 48 位通道解码后平面范围是 10)。
    /// binding 内部已有该原生函数的 P/Invoke 包装但不可见,这里反射取内部 <c>SafeHeifImage</c> 句柄,再调用
    /// **同一个**打包的 <c>libheif</c>(与 binding 同名 DllImport),不新增依赖;任何环节缺失都返回 null。
    /// </remarks>
    /// <param name="image">libheif 解码结果。</param>
    /// <returns>码值位深(1-16);不可得返回 null。</returns>
    private static int? TryGetPlaneBitDepthRange(HeifImage image)
    {
        try
        {
            if (SafeHeifImageProperty?.GetValue(image) is SafeHandle handle && !handle.IsInvalid && !handle.IsClosed)
            {
                var range = heif_image_get_bits_per_pixel_range(handle.DangerousGetHandle(), InterleavedPlaneChannel);
                return range is >= 1 and <= 16 ? range : null;
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[LibHeifColorDecoder] 读取平面码值范围失败: {ex.GetType().Name} {ex.Message}");
        }

        return null;
    }

    /// <summary>LibHeifSharp 内部的 <c>HeifImage.SafeHeifImage</c> 属性(首次使用时反射解析并缓存;缺失为 null)。</summary>
    private static readonly System.Reflection.PropertyInfo? SafeHeifImageProperty = typeof(HeifImage).GetProperty(
        "SafeHeifImage",
        System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic);

    /// <summary>libheif 的交错平面通道号(<c>heif_channel_interleaved</c>)。</summary>
    private const int InterleavedPlaneChannel = 10;

    /// <summary>libheif:某平面存放码值的位深范围(不是 bits per pixel;交错 48 位平面的 bits per pixel 是 48)。</summary>
    [DllImport("libheif")]
    private static extern int heif_image_get_bits_per_pixel_range(IntPtr image, int channel);

    /// <summary>
    /// 把 libheif 的 RGB24 交错平面拷成 BGRA32,并把真实色彩标签写到图像上(只换通道序,不做色域换算)。
    /// </summary>
    /// <remarks>
    /// 尺寸只取 <see cref="HeifImage.Width"/>/<see cref="HeifImage.Height"/>,行距取平面真实 <c>Stride</c>,
    /// 逐行只拷 <c>Width × 3</c> 有效字节——不使用"Stride / 3 就是行像素数"的启发式
    /// (对齐填充会让它偏大,竖拍图更会据此交换宽高、把几何整体搞错)。
    /// </remarks>
    /// <param name="heifImage">libheif 解码结果。</param>
    /// <param name="colorSpace">中间图标签(能对上共享词汇时即真实标签,否则 Legacy)。</param>
    /// <param name="iccProfile">ICC 字节(<see cref="ImageColorSpace.Icc"/> 时给出)。</param>
    /// <returns>显示朝向的 BGRA32 码值图;失败返回 null。</returns>
    private static DecodedImage? ConvertHeifImageToBgra(HeifImage heifImage, ImageColorSpace colorSpace, byte[]? iccProfile)
    {
        try
        {
            var width = (int)heifImage.Width;
            var height = (int)heifImage.Height;
            if (width <= 0 || height <= 0) return null;

            var plane = heifImage.GetPlane(HeifChannel.Interleaved);
            if (plane == null) return null;

            var stride = (int)plane.Stride;
            const int bytesPerPixel = 3;
            var rowBytes = checked(width * bytesPerPixel);
            if (stride < rowBytes) return null;

            var buffer = DecodedImage.RentPixels(checked(width * height * 4));
            try
            {
                unsafe
                {
                    var sourcePtr = (byte*)plane.Scan0;
                    if (sourcePtr == null)
                    {
                        ImageBufferPool.Return(buffer);
                        return null;
                    }

                    for (var y = 0; y < height; y++)
                    {
                        var source = sourcePtr + ((long)y * stride);
                        var destination = y * width * 4;
                        for (var x = 0; x < width; x++, source += bytesPerPixel)
                        {
                            buffer[destination] = source[2];         // B
                            buffer[destination + 1] = source[1];     // G
                            buffer[destination + 2] = source[0];     // R
                            buffer[destination + 3] = 255;           // A
                            destination += 4;
                        }
                    }
                }

                return DecodedImage.CreatePooled(
                    buffer,
                    width,
                    height,
                    width * 4,
                    PixelEncoding.Bgra32,
                    colorSpace,
                    width,
                    height,
                    1,
                    iccProfile);
            }
            catch
            {
                ImageBufferPool.Return(buffer);
                throw;
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine("[LibHeifColorDecoder] RGB24 转换失败: " + ex.Message);
            return null;
        }
    }
}
