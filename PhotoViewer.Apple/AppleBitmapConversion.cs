using CoreGraphics;
using Foundation;

namespace PhotoViewer.Apple;

/// <summary>
/// Apple 位图转换的共享实现：8 位 Display P3（RGBA8 预乘）按行带并行绘制，探针与展示解码器共用同一份。
/// </summary>
internal static class AppleBitmapConversion
{
    /// <summary>每条行带向源图上下各多取的行数，补齐转换所需的邻域。</summary>
    internal const int Halo = 8;

    /// <summary>
    /// 按行带并行绘制到整图缓冲：源 CGImage 与缓冲须由调用方保持到本方法返回；目标缓冲整图只 pin 一次。
    /// 每带自有 NSAutoreleasePool 与 CGBitmapContext，失败原因带回调用方决定回退。
    /// </summary>
    internal static unsafe void DrawParallel(CGImage image, byte[] buffer, int width, int height, int bands,
        CancellationToken cancellationToken = default)
    {
        int stride = checked(width * 4);
        if (buffer.LongLength < (long)stride * height) throw new InvalidOperationException("像素缓冲不足");
        bands = Math.Clamp(bands, 1, Math.Max(1, height));
        var bandStarts = new int[bands];
        var bandHeights = new int[bands];
        for (int index = 0; index < bands; index++)
        {
            bandStarts[index] = (int)((long)height * index / bands);
            bandHeights[index] = (int)((long)height * (index + 1) / bands) - bandStarts[index];
        }

        var failures = new Exception?[bands];
        fixed (byte* pixels = buffer)
        {
            IntPtr basePointer = (IntPtr)pixels;
            Parallel.For(0, bands, new ParallelOptions
            {
                MaxDegreeOfParallelism = bands,
                CancellationToken = cancellationToken,
            }, index =>
            {
                using var pool = new NSAutoreleasePool();
                try
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    int start = bandStarts[index], bandHeight = bandHeights[index];
                    int bandEnd = start + bandHeight;
                    int cropStart = Math.Max(0, start - Halo);
                    int cropEnd = Math.Min(height, bandEnd + Halo);
                    int cropHeight = cropEnd - cropStart;
                    // 位图内存行 0 即源图顶行，裁切同用顶左原点；halo 只扩源邻域，band 外由 context 裁掉。
                    using var crop = image.WithImageInRect(new CGRect(0, cropStart, width, cropHeight));
                    if (crop == null)
                        throw new InvalidOperationException($"CGImage 行带裁切失败 y={cropStart} h={cropHeight}");
                    using var space = CGColorSpace.CreateWithName(CGColorSpaceNames.DisplayP3)
                        ?? throw new InvalidOperationException("系统不支持 Display P3");
                    using var context = new CGBitmapContext(IntPtr.Add(basePointer, start * stride),
                        width, bandHeight, 8, stride, space, CGImageAlphaInfo.PremultipliedLast);
                    context.SetBlendMode(CGBlendMode.Copy);
                    context.DrawImage(new CGRect(0, bandEnd - cropEnd, width, cropHeight), crop);
                }
                catch (OperationCanceledException)
                {
                    // 取消不是转换失败；Parallel 结束后统一抛给调用方。
                }
                catch (Exception exception)
                {
                    failures[index] = exception;
                }
            });
        }

        cancellationToken.ThrowIfCancellationRequested();
        if (failures.FirstOrDefault(failure => failure != null) is { } failed)
            throw new InvalidOperationException($"并行位图转换失败：{failed.Message}", failed);
    }
}
