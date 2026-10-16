using System;
using System.Threading;
using PhotoViewer.Core.Color;

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

    /// <summary>RGBA 半精度浮点、预乘 alpha，8 字节/像素；线性 scRGB 由色彩标签声明。</summary>
    RgbaF16,

    /// <summary>RGBA8888、预乘 alpha，4 字节/像素；通道顺序为 R,G,B,A(8 位展示编码，色彩空间由 <see cref="ImageColorSpace"/> 声明)。</summary>
    Rgba8,
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
    public DecodedImage(byte[] pixels, int width, int height, int stride, PixelEncoding encoding,
        ImageColorSpace colorSpace = ImageColorSpace.Legacy, int sourceWidth = 0, int sourceHeight = 0, int orientation = 1, byte[]? iccProfile = null)
        : this(pixels, width, height, stride, encoding, false, colorSpace, sourceWidth, sourceHeight, orientation, iccProfile)
    {
    }

    /// <summary>验证像素与元数据并建立创建者租约，源尺寸为零时使用当前尺寸。</summary>
    private DecodedImage(byte[] pixels, int width, int height, int stride, PixelEncoding encoding, bool pooled,
        ImageColorSpace colorSpace, int sourceWidth, int sourceHeight, int orientation, byte[]? iccProfile)
    {
        if (pixels == null) throw new ArgumentNullException(nameof(pixels));
        if (width <= 0) throw new ArgumentOutOfRangeException(nameof(width));
        if (height <= 0) throw new ArgumentOutOfRangeException(nameof(height));
        if (stride < checked(width * BytesPerPixel(encoding))) throw new ArgumentOutOfRangeException(nameof(stride));
        if (pixels.LongLength < checked((long)(height - 1) * stride + (long)width * BytesPerPixel(encoding)))
            throw new ArgumentException("像素缓冲不足", nameof(pixels));
        if (orientation is < 1 or > 8) throw new ArgumentOutOfRangeException(nameof(orientation));

        _pixels = pixels;
        Width = width;
        Height = height;
        Stride = stride;
        Encoding = encoding;
        _pooled = pooled;
        ColorSpace = colorSpace;
        SourceWidth = sourceWidth > 0 ? sourceWidth : width;
        SourceHeight = sourceHeight > 0 ? sourceHeight : height;
        Orientation = orientation;
        IccProfile = iccProfile == null ? ReadOnlyMemory<byte>.Empty : new ReadOnlyMemory<byte>((byte[])iccProfile.Clone());
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

    /// <summary>像素色彩空间；与编码正交。</summary>
    public ImageColorSpace ColorSpace { get; }
    /// <summary>未旋转的全分辨率源宽度，代理与 Full 共用几何基准。</summary>
    public int SourceWidth { get; }
    /// <summary>未旋转的全分辨率源高度。</summary>
    public int SourceHeight { get; }
    /// <summary>仍待渲染器应用的 EXIF 方向，已旋转产物为 1。</summary>
    public int Orientation { get; }
    /// <summary>源 ICC 的不可变副本，仅 Icc 色彩标签使用。</summary>
    public ReadOnlyMemory<byte> IccProfile { get; }

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
        PixelEncoding.RgbaF16 => 8,
        PixelEncoding.Rgba8 => 4,
        _ => throw new ArgumentOutOfRangeException(nameof(encoding)),
    };

    /// <summary>
    /// 展示编码的保真度序：8 位编码在颜色上可被 F16 线性表面无损覆盖（F16 允许超一与负分量），反之不成立。
    /// 缓存复用与在途去重据此判断"已有产物是否足以满足本次请求"。
    /// </summary>
    /// <param name="encoding">像素编码。</param>
    /// <returns>保真度序号，越大越通用。</returns>
    public static int PresentationFidelity(PixelEncoding encoding) => encoding switch
    {
        PixelEncoding.RgbaF16 => 2,
        PixelEncoding.Rgba8 => 1,
        _ => throw new ArgumentOutOfRangeException(nameof(encoding)),
    };

    /// <summary>
    /// 从 <see cref="ImageBufferPool"/> 租用像素缓冲(2 幂分档,长度 ≥ minBytes)。
    /// 调用方必须经 <see cref="CreatePooled"/> 交给 DecodedImage,或自行 <see cref="ImageBufferPool.Return"/>。
    /// </summary>
    public static byte[] RentPixels(int minBytes) => ImageBufferPool.Rent(minBytes);

    /// <summary>
    /// 用池化缓冲创建图像;缓冲归图像所有,最后一份租约释放时归还池。
    /// </summary>
    public static DecodedImage CreatePooled(byte[] rentedPixels, int width, int height, int stride, PixelEncoding encoding,
        ImageColorSpace colorSpace = ImageColorSpace.Legacy, int sourceWidth = 0, int sourceHeight = 0, int orientation = 1, byte[]? iccProfile = null)
        => new(rentedPixels, width, height, stride, encoding, true, colorSpace, sourceWidth, sourceHeight, orientation, iccProfile);

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
