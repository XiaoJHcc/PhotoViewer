using System;
using System.Threading;

namespace PhotoViewer.Core.Image;

/// <summary>
/// 解码后像素的编码格式。
/// </summary>
public enum PixelEncoding
{
    /// <summary>BGRA8888,预乘 alpha,4 字节/像素(与 Avalonia ImmutableBitmap 内存布局一致)。</summary>
    Bgra32,

    /// <summary>RGB888,3 字节/像素(libheif 等无 alpha 通道来源)。</summary>
    Rgb24,
}

/// <summary>
/// 平台中立的已解码图像:托管像素缓冲 + 尺寸 + 行距 + 编码;纯数据载体,无任何 UI 框架依赖。
///
/// P1-M3 起为**引用租约 + 缓冲池**模型:
/// - 创建时携带 1 份租约(通常归缓存);消费方经 <see cref="TryAcquire"/> 借用、<see cref="Release"/> 归还;
/// - 最后一份租约释放时,池化缓冲归还 <see cref="ImageBufferPool"/> 而非丢给 GC;
/// - 租约归零后 <see cref="Pixels"/> 返回空缓冲。读取纪律:先 TryAcquire 成功再使用,用完 Release。
///   缓存命中路径在返回前已完成 Acquire;淘汰方只释放缓存自己那份,不会释放仍在被借用的缓冲。
/// </summary>
public sealed class DecodedImage : IDisposable
{
    private byte[] _pixels;
    private int _leases = 1;
    private readonly bool _pooled;

    /// <summary>
    /// 创建已解码图像(非池化缓冲;缓冲丢给 GC)。解码主路径请改用 <see cref="CreatePooled"/>。
    /// </summary>
    /// <param name="pixels">像素缓冲,长度必须 ≥ (Height-1)*Stride + 最后一行实际字节数。</param>
    /// <param name="width">像素宽。</param>
    /// <param name="height">像素高。</param>
    /// <param name="stride">每行字节数(含对齐填充)。</param>
    /// <param name="encoding">像素编码。</param>
    public DecodedImage(byte[] pixels, int width, int height, int stride, PixelEncoding encoding)
        : this(pixels, width, height, stride, encoding, pooled: false)
    {
    }

    private DecodedImage(byte[] pixels, int width, int height, int stride, PixelEncoding encoding, bool pooled)
    {
        if (pixels == null) throw new ArgumentNullException(nameof(pixels));
        if (width <= 0) throw new ArgumentOutOfRangeException(nameof(width));
        if (height <= 0) throw new ArgumentOutOfRangeException(nameof(height));
        if (stride < width * BytesPerPixel(encoding)) throw new ArgumentOutOfRangeException(nameof(stride));

        _pixels = pixels;
        Width = width;
        Height = height;
        Stride = stride;
        Encoding = encoding;
        _pooled = pooled;
    }

    /// <summary>像素缓冲。租约归零后返回空数组。</summary>
    public byte[] Pixels => _pixels;

    /// <summary>像素宽。</summary>
    public int Width { get; }

    /// <summary>像素高。</summary>
    public int Height { get; }

    /// <summary>每行字节数(含对齐填充)。</summary>
    public int Stride { get; }

    /// <summary>像素编码。</summary>
    public PixelEncoding Encoding { get; }

    /// <summary>缓冲字节数(池化缓冲为分档对齐后的实际长度,即真实分配量)。</summary>
    public long ByteCount => _pixels.Length;

    /// <summary>
    /// 每种编码的每像素字节数。
    /// </summary>
    /// <param name="encoding">像素编码。</param>
    /// <returns>每像素字节数。</returns>
    public static int BytesPerPixel(PixelEncoding encoding) => encoding switch
    {
        PixelEncoding.Bgra32 => 4,
        PixelEncoding.Rgb24 => 3,
        _ => 4,
    };

    /// <summary>
    /// 从 <see cref="ImageBufferPool"/> 租用像素缓冲(2 幂分档,长度 ≥ minBytes)。
    /// 调用方必须经 <see cref="CreatePooled"/> 交给 DecodedImage,或自行 <see cref="ImageBufferPool.Return"/>。
    /// </summary>
    public static byte[] RentPixels(int minBytes) => ImageBufferPool.Rent(minBytes);

    /// <summary>
    /// 用池化缓冲创建图像;缓冲归图像所有,最后一份租约释放时归还池。
    /// </summary>
    public static DecodedImage CreatePooled(byte[] rentedPixels, int width, int height, int stride, PixelEncoding encoding)
        => new(rentedPixels, width, height, stride, encoding, pooled: true);

    /// <summary>
    /// 借用一份租约(CAS 原子);已彻底释放(缓冲已归还/丢弃)返回 false,调用方按 miss 处理。
    /// </summary>
    public bool TryAcquire()
    {
        while (true)
        {
            int c = Volatile.Read(ref _leases);
            if (c <= 0) return false;
            if (Interlocked.CompareExchange(ref _leases, c + 1, c) == c) return true;
        }
    }

    /// <summary>
    /// 释放一份租约;最后一份释放时归还池化缓冲并清空像素引用。
    /// 纪律:每份成功的 TryAcquire 恰好配对一次 Release(创建者那份由缓存/持有方释放)。
    /// </summary>
    public void Release()
    {
        int n = Interlocked.Decrement(ref _leases);
        if (n != 0) return;
        if (_pooled)
        {
            ImageBufferPool.Return(_pixels);
        }
        _pixels = [];
    }

    /// <summary>
    /// 释放本持有者的一份租约(等价 <see cref="Release"/>;幂等性由调用方纪律保证)。
    /// </summary>
    public void Dispose() => Release();
}
