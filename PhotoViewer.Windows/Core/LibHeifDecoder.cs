using System;
using System.Threading;
using System.Threading.Tasks;
using PhotoViewer.Core;
using PhotoViewer.Core.Color;
using PhotoViewer.Core.Image;
using PhotoViewer.Core.Image.Heif;
using PhotoViewer.Core.Storage;
using SkiaSharp;

namespace PhotoViewer.Windows.Core;

/// <summary>
/// HEIF 解码器(缩略图兜底与分析输入):优先走 <see cref="FastHeifDecoder"/>,
/// 容器或色彩超出支持面时回落 libheif。
/// </summary>
/// <remarks>
/// 对外契约:产物是**显示朝向**的 sRGB8(<see cref="PixelEncoding.Bgra32"/>,方向 1),
/// 因此 <see cref="OutputPreRotated"/> 为 true——<see cref="ThumbnailService"/> 与 <c>BitmapLoader</c>
/// 不再按容器/EXIF 方向二次旋转。快速路径与容器 <c>thmb</c> 项的未旋转产物由 <see cref="ToDisplayOriented"/>
/// 摆正,libheif 回落本身已按容器 <c>irot</c>/<c>imir</c> 摆正。
/// 小尺寸请求(胶片栏)优先解容器自带的 <c>thmb</c> 缩略图项,输出尺寸与方向不变。
///
/// libheif 回落的色彩与位深口径在共享 <see cref="LibHeifColorDecoder"/>
/// (ICC 优先、其次 <c>nclx</c>,PQ/HLG 拒绝,10/12 位源走 48 位平面不先量化);这里只保留 Windows 侧的
/// 入口与产物口径(缩略图 sRGB8、主图线性 RGBA16F)。
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

    /// <summary>产物一律已按容器 <c>irot</c>/<c>imir</c> 摆到显示朝向,方向恒为 1,调用方不得再旋转。</summary>
    public bool OutputPreRotated => true;

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
        return await HeifLoader.RunCpuDecodeAsync(() => Task.Run(() =>
        {
            var decoded = LibHeifColorDecoder.Decode(bytes, maxSize, LibHeifOutput.Bgra32Srgb,
                CancellationToken.None, out _);
            return decoded;
        })).ConfigureAwait(false);
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
            () => Task.Run(() => LibHeifColorDecoder.Decode(bytes, maxLongEdge, LibHeifOutput.RgbaF16ScRgb,
                cancellationToken, out _), cancellationToken)).ConfigureAwait(false);
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
        => LibHeifColorDecoder.Decode(data, maxLongEdge, LibHeifOutput.RgbaF16ScRgb, forcedBitDepth,
            CancellationToken.None, out _);

    /// <summary>
    /// 验证工具专用入口:把一块(可自建的)48 位交错平面按给定声明位深转成码值图,
    /// 用于核对归一化是否只按平面自身位深范围、不依赖白点。生产路径不经过这里。
    /// </summary>
    /// <param name="image">48 位交错(libheif <see cref="LibHeifSharp.HeifChroma.InterleavedRgb48LE"/>)图像。</param>
    /// <param name="declaredBitDepth">模拟"句柄声明位深"(故意给错时用于证明不依赖它)。</param>
    /// <returns>RGBA 半浮点码值图;失败返回 null。</returns>
    internal static DecodedImage? ConvertRgb48ToCodeValuesForTest(LibHeifSharp.HeifImage image, int declaredBitDepth)
        => LibHeifColorDecoder.ConvertRgb48ToCodeValues(image, declaredBitDepth, ImageColorSpace.Srgb, null, out _);

    /// <summary>nclx 原色代码 → Skia 基色矩阵(共享实现,验证工具按同一份对照)。</summary>
    /// <param name="primaries">nclx 原色代码。</param>
    /// <returns>Skia 基色矩阵。</returns>
    internal static SKColorSpaceXyz XyzOf(int primaries) => LibHeifColorDecoder.XyzOf(primaries);

    /// <summary>nclx 传输特性代码 → Skia 传递函数(共享实现,验证工具按同一份对照)。</summary>
    /// <param name="transfer">nclx 传输特性代码。</param>
    /// <returns>Skia 传递函数。</returns>
    internal static SKColorSpaceTransferFn TransferFnOf(int transfer) => LibHeifColorDecoder.TransferFnOf(transfer);

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
}
