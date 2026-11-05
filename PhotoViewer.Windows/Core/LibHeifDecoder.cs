using System;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using LibHeifSharp;
using PhotoViewer.Core;
using PhotoViewer.Core.Color;
using PhotoViewer.Core.Image;
using PhotoViewer.Core.Storage;
using PhotoViewer.Core.Image.Heif;
using SkiaSharp;

namespace PhotoViewer.Windows.Core;

/// <summary>
/// HEIF 解码器(缩略图兜底与分析输入):优先走 <see cref="FastHeifDecoder"/>,
/// 容器或色彩超出支持面时回落 libheif。
/// </summary>
/// <remarks>
/// 对外契约保持不变:产物是**显示朝向**的 sRGB8(<see cref="PixelEncoding.Bgra32"/>),
/// 因此 <see cref="OutputPreRotated"/> 仍是 false —— <see cref="ThumbnailService"/> 的方向对齐启发式据此判断,
/// 换成 true 会让它跳过对齐并改变已有产物的横竖。
/// 小尺寸请求(胶片栏)优先解容器自带的 <c>thmb</c> 缩略图项,输出尺寸与方向不变。
///
/// 色彩口径:libheif 回落的像素是**源色彩空间内的编码值**(它只做 YCbCr 反矩阵,不做色域映射),
/// 所以这里先按文件的真实声明打标签(ICC 优先,其次 <c>nclx</c>),再经共享
/// <see cref="ColorEngine"/> 转到 sRGB8;不再像过去那样把广色域码值直接当 sRGB 送出。
/// 同一套标签也供 <see cref="DecodePresentationAsync"/> 转到线性 RGBA16F,让主图回落后仍保留广色域。
/// PQ/HLG 不做 tone map,显式拒绝并记录原因(不伪造 SDR)。
///
/// 位深契约:8 位源保持原路径(交错 RGB24 → BGRA32);10/12 位源请求 libheif 的 16 位交错平面
/// (<c>InterleavedRgb48LE</c>),直接转成"源空间编码值"的半浮点缓冲再进色彩转换 —— 不先量化到 8 位。
/// 16 位平面的码值刻度按句柄声明的解码位深归一化,实测上界超出该范围时按实测值抬位深(见
/// <see cref="ConvertRgb48ToCodeValues"/>)。
/// </remarks>
public sealed class LibHeifDecoder : IHeifDecoder
{
    /// <summary>
    /// 走容器 <c>thmb</c> 缩略图项的请求上限(目标长边,像素)。
    /// </summary>
    /// <remarks>
    /// 胶片栏请求经 <c>ThumbnailService</c> → <c>HeifLoader.DecodeAsync(file, target)</c> 传进来的是**目标短边**
    /// (列表档 120,工具探针 120/400),DINO 特征请求是 560 并必须保持"整幅主图"语义。
    /// 上限取 512:既覆盖列表档(120/400),也把 560 挡在外面——不管调用方按短边还是长边读这个值都不会误入缩略图项。
    /// 命中阈值时输出尺寸仍按主图画布折算,与主图路径逐像素一致(见 <see cref="FastHeifDecoder.TryDecodeContainerThumbnail"/>)。
    /// </remarks>
    private const int ThumbnailItemMaxLongEdge = 512;

    /// <summary>是否优先用容器缩略图项承接小尺寸请求(压测/探针需要参照物时关掉)。</summary>
    private readonly bool _preferContainerThumbnail;

    /// <summary>构造解码器。</summary>
    /// <param name="preferContainerThumbnail">小尺寸请求是否优先用容器 <c>thmb</c> 项(默认是)。</param>
    public LibHeifDecoder(bool preferContainerThumbnail = true)
    {
        _preferContainerThumbnail = preferContainerThumbnail;
    }

    public bool IsSupported => true;

    /// <summary>产物已是显示朝向(本类型内部完成旋转),但契约值保持不变。</summary>
    public bool OutputPreRotated => false;

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
    /// 解码 HEIF。maxSize = null 全分辨率;非空时代理(libheif 回落无子采样能力,
    /// 仍按全解码 + Skia Mitchell 精缩,画质红线禁止最近邻)。
    /// </summary>
    public async Task<DecodedImage?> DecodeAsync(IFileRef file, int? maxSize)
    {
        var bytes = await FastHeifDecoder.ReadAllBytesAsync(file).ConfigureAwait(false);

        // 胶片栏缩略图直接解容器自带的小图:相机写进文件的 thmb 项本身就是它渲染的缩略图(约 5 ms),
        // 解主图要 33MP、6 块瓦片并行,十几张缩略图足以把连翻的当前图挤成 1 s 级的长尾。
        if (_preferContainerThumbnail && maxSize is int thumbnailLimit && thumbnailLimit <= ThumbnailItemMaxLongEdge)
        {
            string? thumbnailReason = null;
            var thumbnail = await Task.Run(
                () => FastHeifDecoder.TryDecodeContainerThumbnail(
                    bytes, thumbnailLimit, PixelEncoding.Bgra32, CancellationToken.None, out thumbnailReason))
                .ConfigureAwait(false);
            if (thumbnail is not null)
            {
                return ToDisplayOriented(thumbnail);
            }

            if (thumbnailReason is not null)
            {
                Console.WriteLine($"[LibHeifDecoder] 缩略图项不可用({file.Name}):{thumbnailReason}");
            }
        }

        string? reason = null;
        var fast = await Task.Run(
            () => FastHeifDecoder.TryDecode(bytes, maxSize, PixelEncoding.Bgra32, CancellationToken.None, out reason))
            .ConfigureAwait(false);
        if (fast is not null)
        {
            return ToDisplayOriented(fast);
        }

        Console.WriteLine($"[LibHeifDecoder] 回落 libheif({file.Name}):{reason ?? "未知原因"}");
        return await HeifLoader.RunCpuDecodeAsync(() => Task.Run(() => DecodeWithLibHeif(bytes, maxSize, false)))
            .ConfigureAwait(false);
    }

    /// <summary>
    /// libheif 主图回落:产物为**显示朝向**的线性 RGBA16F(保留广色域),供 Windows 主图渲染使用。
    /// </summary>
    /// <param name="file">文件引用。</param>
    /// <param name="maxLongEdge">代理长边上限;null 表示全分辨率。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>显示朝向的线性 F16;色彩不能如实解释或解码失败返回 null(原因写入 <see cref="HeifLoader.LastDecodeError"/>)。</returns>
    internal static async Task<DecodedImage?> DecodePresentationAsync(IFileRef file, int? maxLongEdge, CancellationToken cancellationToken)
    {
        var bytes = await FastHeifDecoder.ReadAllBytesAsync(file).ConfigureAwait(false);
        cancellationToken.ThrowIfCancellationRequested();
        return await HeifLoader.RunCpuDecodeAsync(
            () => Task.Run(() => DecodeWithLibHeif(bytes, maxLongEdge, true), cancellationToken)).ConfigureAwait(false);
    }

    /// <summary>
    /// 验证工具专用入口:强制按给定位深走 48 位通道解码,用于在只有 8 位夹具的环境里核对
    /// 16 位平面的字节序与码值归一化(<c>Tools/HeifFastDecodeTest</c> 的端到端用例)。生产路径不经过这里。
    /// </summary>
    /// <param name="data">完整文件字节。</param>
    /// <param name="maxLongEdge">目标长边;null 表示全分辨率。</param>
    /// <param name="forcedBitDepth">强制位深(例如对 8 位夹具传 10,libheif 会给 10 位码值)。</param>
    /// <returns>显示朝向的线性 RGBA16F;失败返回 null。</returns>
    internal static DecodedImage? DecodePresentationForTest(byte[] data, int? maxLongEdge, int forcedBitDepth)
        => DecodeWithLibHeif(data, maxLongEdge, true, forcedBitDepth);

    /// <summary>把未旋转的快速路径产物按容器方向摆到显示朝向(旋转/镜像在缩小后的小图上完成)。</summary>
    /// <param name="source">未旋转的 sRGB8 产物。</param>
    /// <returns>显示朝向的图像;调用方持有其租约。</returns>
    private static DecodedImage ToDisplayOriented(DecodedImage source)
    {
        var (rotationCw, mirror) = ImageOrientationMapping.ToDisplay(source.Orientation);
        if (rotationCw == 0 && !mirror) return source;

        DecodedImage? rotated = null;
        try
        {
            rotated = SkiaImageCodec.ApplyOrientation(source, rotationCw, mirror);
            return rotated ?? source;
        }
        finally
        {
            if (rotated is not null) source.Release();
        }
    }

    /// <summary>
    /// libheif 全解码(libheif 默认已应用 irot/imir,产物为显示朝向);代理按长边精缩。
    /// </summary>
    /// <param name="data">完整文件字节。</param>
    /// <param name="maxSize">目标长边;null 表示全分辨率。</param>
    /// <param name="linearF16">true 产出线性 RGBA16F(主图),false 产出 sRGB8(缩略图/分析)。</param>
    /// <param name="forceBitDepth">验证工具用:强制按指定位深走 48 位通道(生产路径传 null,按句柄声明)。</param>
    /// <returns>解码产物;色彩不能如实解释或失败返回 null。</returns>
    private static DecodedImage? DecodeWithLibHeif(byte[] data, int? maxSize, bool linearF16, int? forceBitDepth = null)
    {
        try
        {
            using var context = new HeifContext(data);
            using var imageHandle = context.GetPrimaryImageHandle();
            if (imageHandle == null) return null;

            var tag = ResolveColorTag(imageHandle);
            if (tag.Refusal is not null)
            {
                HeifLoader.SetLastDecodeError(tag.Refusal);
                Console.WriteLine("[LibHeifDecoder] 色彩超出支持面:" + tag.Refusal);
                return null;
            }

            var bitDepth = forceBitDepth ?? imageHandle.BitDepth;
            var wide = bitDepth > 8;
            using var image = imageHandle.Decode(
                HeifColorspace.Rgb,
                wide ? HeifChroma.InterleavedRgb48LE : HeifChroma.InterleavedRgb24);
            if (image == null) return null;

            // 中间图带上真实标签:能对上共享标签词汇时交给 ColorEngine,否则只作为码值容器。
            var codedSpace = tag.NeedsSkcms ? ImageColorSpace.Legacy : tag.ColorSpace;
            string? wideReason = null;
            DecodedImage? coded = wide
                ? ConvertRgb48ToCodeValues(image, bitDepth, codedSpace, tag.IccProfile, out wideReason)
                : ConvertHeifImageToBgra(image, codedSpace, tag.IccProfile);
            if (coded is null)
            {
                if (wide && wideReason is not null)
                {
                    HeifLoader.SetLastDecodeError("高位深 libheif 解码失败:" + wideReason);
                    Console.WriteLine("[LibHeifDecoder] 高位深解码失败:" + wideReason);
                }
                return null;
            }

            DecodedImage? full;
            try
            {
                full = ConvertToTarget(coded, tag, linearF16);
            }
            finally
            {
                coded.Release();
            }
            if (full == null) return null;

            if (maxSize is not int limit) return full;
            var scaled = SkiaImageCodec.ScaleToLongEdge(full, limit);
            if (scaled == null) return full; // null = 不需缩放(源已小于目标)或缩放失败,用全图保证正确性
            full.Release();
            return scaled;
        }
        catch (Exception ex)
        {
            HeifLoader.SetLastDecodeError("libheif 解码失败:" + ex.Message);
            Console.WriteLine("Failed to decode HEIF image: " + ex.Message);
            return null;
        }
    }

    /// <summary>
    /// 读文件的色彩声明并决定怎么如实解释:ICC 优先(HEIF 规定),其次 <c>nclx</c> 三元组。
    /// </summary>
    /// <remarks>
    /// 共享 <see cref="ColorEngine"/> 的标签词汇只有 Legacy/sRGB/Display P3/Adobe RGB/线性/ICC,
    /// 既没有 BT.2020 原色也没有 BT.709 曲线,所以这几类源用 <see cref="ConvertNclx"/> 走同一套 skcms
    /// 机制换算(skcms 的传递函数与原色由 Skia 给出,不自造矩阵);能对上标签的常见组合仍交
    /// <see cref="ColorEngine"/>,避免重复实现。
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
    /// 把"源色彩空间码值"的中间图按真实标签解释,转到目标编码(线性 RGBA16F 或 sRGB8)。
    /// </summary>
    /// <remarks>
    /// 中间图有两种位宽:8 位源为 BGRA32(沿用原路径),10/12 位源为 RGBA 半浮点码值
    /// (由 <see cref="ConvertRgb48ToCodeValues"/> 从 48 位平面直接得到,不经过 8 位量化)。
    /// 标签能对上共享色彩标签词汇时走 <see cref="ColorEngine"/>,否则(如 BT.2020 原色、BT.709 曲线)
    /// 走同一套 skcms 机制。中间图的租约归调用方,本方法不释放它。
    /// </remarks>
    /// <param name="coded">源空间码值的中间图。</param>
    /// <param name="tag">色彩标签。</param>
    /// <param name="linearF16">true 产出线性 RGBA16F。</param>
    /// <returns>目标产物;失败返回 null。</returns>
    private static DecodedImage? ConvertToTarget(DecodedImage coded, HeifColorTag tag, bool linearF16)
    {
        if (tag.NeedsSkcms) return ConvertWithSkcms(coded, tag, linearF16);

        // 码值已是 sRGB 且目标就是 sRGB8:直接复用(借一份租约给结果)。
        if (tag.ColorSpace == ImageColorSpace.Srgb && !linearF16)
        {
            if (!coded.TryAcquire()) return null;
            return coded;
        }

        return linearF16
            ? ColorEngine.ToScRgb16(coded, coded.Width, coded.Height, 1)
            : ColorEngine.ToSrgb8(coded);
    }

    /// <summary>
    /// 共享标签词汇表达不了的 <c>nclx</c> 源(BT.2020 原色、BT.709 / 伽马 2.2 / 伽马 2.8 曲线):
    /// 用与 <see cref="ColorEngine"/> 同一套 skcms 机制(经 Skia 色彩空间)转到 sRGB8 或线性 F16。
    /// 原色/传递函数取自 Skia 的预设(<c>SKColorSpaceXyz</c>/<c>SKColorSpaceTransferFn</c>),
    /// 不在这里自造矩阵或曲线。
    /// </summary>
    /// <param name="coded">源空间码值的中间图(BGRA32 或 RGBA 半浮点)。</param>
    /// <param name="tag">色彩标签(含 nclx 代码)。</param>
    /// <param name="linearF16">true 产出线性 RGBA16F,否则 sRGB8。</param>
    /// <returns>目标产物;失败返回 null。</returns>
    private static unsafe DecodedImage? ConvertWithSkcms(DecodedImage coded, HeifColorTag tag, bool linearF16)
    {
        var wide = coded.Encoding == PixelEncoding.RgbaF16;
        var targetEncoding = linearF16 ? PixelEncoding.RgbaF16 : PixelEncoding.Bgra32;
        var stride = checked(coded.Width * (linearF16 ? 8 : 4));
        var buffer = DecodedImage.RentPixels(checked(stride * coded.Height));
        try
        {
            fixed (byte* sourcePixels = coded.Pixels)
            fixed (byte* targetPixels = buffer)
            {
                using var sourceSpace = SKColorSpace.CreateRgb(TransferFnOf(tag.Transfer), XyzOf(tag.Primaries));
                using var targetSpace = linearF16 ? SKColorSpace.CreateSrgbLinear() : SKColorSpace.CreateSrgb();
                var sourceInfo = new SKImageInfo(
                    coded.Width,
                    coded.Height,
                    wide ? SKColorType.RgbaF16 : SKColorType.Bgra8888,
                    wide ? SKAlphaType.Premul : SKAlphaType.Opaque,
                    sourceSpace);
                var targetInfo = new SKImageInfo(
                    coded.Width,
                    coded.Height,
                    linearF16 ? SKColorType.RgbaF16 : SKColorType.Bgra8888,
                    SKAlphaType.Premul,
                    targetSpace);
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
                linearF16 ? ImageColorSpace.LinearSrgb : ImageColorSpace.Srgb,
                coded.Width,
                coded.Height,
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
    private static unsafe DecodedImage? ConvertRgb48ToCodeValues(
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
                $"[LibHeifDecoder] 读不到 16 位平面的码值范围,退回句柄声明位深 {codeBits} 归一化(容器声明 {bitDepth})");
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
            Console.WriteLine($"[LibHeifDecoder] 读取平面码值范围失败: {ex.GetType().Name} {ex.Message}");
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
    /// 验证工具专用入口:把一块(可自建的)48 位交错平面按给定声明位深转成码值图,
    /// 用于核对归一化是否只按平面自身位深范围、不依赖白点。生产路径不经过这里。
    /// </summary>
    /// <param name="image">48 位交错(libheif <see cref="HeifChroma.InterleavedRgb48LE"/>)图像。</param>
    /// <param name="declaredBitDepth">模拟"句柄声明位深"(故意给错时用于证明不依赖它)。</param>
    /// <returns>RGBA 半浮点码值图;失败返回 null。</returns>
    internal static DecodedImage? ConvertRgb48ToCodeValuesForTest(HeifImage image, int declaredBitDepth)
        => ConvertRgb48ToCodeValues(image, declaredBitDepth, ImageColorSpace.Srgb, null, out _);

    /// <summary>
    /// 把 libheif 的 RGB24 交错平面拷成 BGRA32,并把真实色彩标签写到图像上(只换通道序,不做色域换算)。
    /// </summary>
    /// <param name="heifImage">libheif 解码结果。</param>
    /// <param name="colorSpace">中间图标签(能对上共享词汇时即真实标签,否则 Legacy)。</param>
    /// <param name="iccProfile">ICC 字节(<see cref="ImageColorSpace.Icc"/> 时给出)。</param>
    /// <returns>未旋转的 BGRA32 码值图;失败返回 null。</returns>
    private static DecodedImage? ConvertHeifImageToBgra(HeifImage heifImage, ImageColorSpace colorSpace, byte[]? iccProfile)
    {
        try
        {
            var reportedWidth = (int)heifImage.Width;
            var reportedHeight = (int)heifImage.Height;
            if (reportedWidth <= 0 || reportedHeight <= 0) return null;

            var plane = heifImage.GetPlane(HeifChannel.Interleaved);
            if (plane == null) return null;

            var stride = (int)plane.Stride;
            const int bytesPerPixel = 3;
            if (stride < bytesPerPixel) return null;

            // 计算实际的图像尺寸
            var pixelsPerRow = stride / bytesPerPixel;
            var actualWidth = pixelsPerRow;
            var actualHeight = reportedHeight;

            // 对于竖拍照片，如果stride表明每行像素数少于报告的宽度，
            // 说明图像实际上是竖拍的，需要交换宽高
            if (pixelsPerRow < reportedWidth && pixelsPerRow == reportedHeight)
            {
                actualWidth = reportedHeight;
                actualHeight = reportedWidth;
            }
            else if (pixelsPerRow == reportedWidth)
            {
                actualWidth = reportedWidth;
                actualHeight = reportedHeight;
            }
            else
            {
                Console.WriteLine("Unusual dimensions: reported=" + reportedWidth + "x" + reportedHeight + ", stride=" + stride + ", pixels per row=" + pixelsPerRow);
            }

            // 使用实际尺寸创建BGRA数据(缓冲池租用,P1-M3)
            var bgraData = DecodedImage.RentPixels(actualWidth * actualHeight * 4);

            unsafe
            {
                var sourcePtr = (byte*)plane.Scan0;
                if (sourcePtr == null)
                {
                    ImageBufferPool.Return(bgraData);
                    return null;
                }

                for (int y = 0; y < actualHeight; y++)
                {
                    var sourceRowPtr = sourcePtr + (y * stride);
                    var destRowOffset = y * actualWidth * 4;

                    for (int x = 0; x < actualWidth; x++)
                    {
                        var srcPixelOffset = x * bytesPerPixel;
                        var destPixelOffset = destRowOffset + (x * 4);

                        if (srcPixelOffset + 2 >= stride) break;
                        if (destPixelOffset + 3 >= bgraData.Length) break;

                        bgraData[destPixelOffset] = sourceRowPtr[srcPixelOffset + 2];       // B
                        bgraData[destPixelOffset + 1] = sourceRowPtr[srcPixelOffset + 1];   // G
                        bgraData[destPixelOffset + 2] = sourceRowPtr[srcPixelOffset];       // R
                        bgraData[destPixelOffset + 3] = 255;    // A
                    }
                }
            }

            return CreateBitmapFromBgraData(bgraData, actualWidth, actualHeight, colorSpace, iccProfile);
        }
        catch (Exception ex)
        {
            Console.WriteLine("Failed to convert HEIF image to bitmap: " + ex.Message);
            return null;
        }
    }

    /// <summary>
    /// 从 BGRA 数据创建已解码图像
    /// </summary>
    /// <param name="bgraData">池化租用的 BGRA 缓冲。</param>
    /// <param name="width">像素宽。</param>
    /// <param name="height">像素高。</param>
    /// <param name="colorSpace">色彩标签。</param>
    /// <param name="iccProfile">ICC 字节。</param>
    /// <returns>已解码图像;失败返回 null。</returns>
    private static DecodedImage? CreateBitmapFromBgraData(
        byte[] bgraData,
        int width,
        int height,
        ImageColorSpace colorSpace,
        byte[]? iccProfile)
    {
        try
        {
            var expected = (long)width * height * 4;
            if (bgraData.Length < expected) return null;
            // 缓冲为池化租用(调用方经 RentPixels 取得),随图像租约归零归还池
            return DecodedImage.CreatePooled(
                bgraData,
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
        catch (Exception ex)
        {
            Console.WriteLine("Failed to create image from BGRA data: " + ex.Message);
            return null;
        }
    }
}
