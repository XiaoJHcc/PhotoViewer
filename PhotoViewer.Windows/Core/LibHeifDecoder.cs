using System;
using System.Threading;
using System.Threading.Tasks;
using LibHeifSharp;
using PhotoViewer.Core;
using PhotoViewer.Core.Image;
using PhotoViewer.Core.Storage;
using PhotoViewer.Core.Image.Heif;

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
        return await HeifLoader.RunCpuDecodeAsync(() => Task.Run(() => DecodeWithLibHeif(bytes, maxSize)))
            .ConfigureAwait(false);
    }

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
    /// <returns>解码产物;失败返回 null。</returns>
    private static DecodedImage? DecodeWithLibHeif(byte[] data, int? maxSize)
    {
        try
        {
            using var context = new HeifContext(data);
            using var imageHandle = context.GetPrimaryImageHandle();
            if (imageHandle == null) return null;

            using var image = imageHandle.Decode(HeifColorspace.Rgb, HeifChroma.InterleavedRgb24);
            if (image == null) return null;

            var full = ConvertHeifImageToBitmap(image);
            if (full == null || !maxSize.HasValue) return full;

            var scaled = SkiaImageCodec.ScaleToLongEdge(full, maxSize.Value);
            if (scaled == null) return full; // null = 不需缩放(源已小于目标)或缩放失败,用全图保证正确性
            full.Release();
            return scaled;
        }
        catch (Exception ex)
        {
            Console.WriteLine("Failed to decode HEIF image: " + ex.Message);
            return null;
        }
    }

    /// <summary>
    /// 将 HeifImage 转换为已解码图像
    /// </summary>
    private static DecodedImage? ConvertHeifImageToBitmap(HeifImage heifImage)
    {
        try
        {
            var reportedWidth = (int)heifImage.Width;
            var reportedHeight = (int)heifImage.Height;
            if (reportedWidth <= 0 || reportedHeight <= 0) return null;

            var plane = heifImage.GetPlane(HeifChannel.Interleaved);
            if (plane == null) return null;

            var stride = (int)plane.Stride;
            if (stride <= 0) return null;

            const int bytesPerPixel = 3;
            
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

            if (stride < bytesPerPixel)
            {
                Console.WriteLine($"Stride too small: {stride}, needs at least {bytesPerPixel} bytes per pixel");
                return null;
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

            return CreateBitmapFromBgraData(bgraData, actualWidth, actualHeight);
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
    private static DecodedImage? CreateBitmapFromBgraData(byte[] bgraData, int width, int height)
    {
        try
        {
            var expected = (long)width * height * 4;
            if (bgraData.Length < expected) return null;
            // 缓冲为池化租用(调用方经 RentPixels 取得),随图像租约归零归还池
            return DecodedImage.CreatePooled(bgraData, width, height, width * 4, PixelEncoding.Bgra32);
        }
        catch (Exception ex)
        {
            Console.WriteLine("Failed to create image from BGRA data: " + ex.Message);
            return null;
        }
    }
}
