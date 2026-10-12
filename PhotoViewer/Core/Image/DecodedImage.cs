using System;

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
/// 平台中立的已解码图像:托管像素缓冲 + 尺寸 + 行距 + 编码。
/// 纯数据载体,无任何 UI 框架依赖;Dispose 仅释放缓冲引用(确定性回收大对象)。
/// </summary>
public sealed class DecodedImage : IDisposable
{
    /// <summary>
    /// 创建已解码图像。
    /// </summary>
    /// <param name="pixels">像素缓冲,长度必须 ≥ (Height-1)*Stride + 最后一行实际字节数。</param>
    /// <param name="width">像素宽。</param>
    /// <param name="height">像素高。</param>
    /// <param name="stride">每行字节数(含对齐填充)。</param>
    /// <param name="encoding">像素编码。</param>
    public DecodedImage(byte[] pixels, int width, int height, int stride, PixelEncoding encoding)
    {
        if (pixels == null) throw new ArgumentNullException(nameof(pixels));
        if (width <= 0) throw new ArgumentOutOfRangeException(nameof(width));
        if (height <= 0) throw new ArgumentOutOfRangeException(nameof(height));
        if (stride < width * BytesPerPixel(encoding)) throw new ArgumentOutOfRangeException(nameof(stride));

        Pixels = pixels;
        Width = width;
        Height = height;
        Stride = stride;
        Encoding = encoding;
    }

    /// <summary>像素缓冲。</summary>
    public byte[] Pixels { get; private set; }

    /// <summary>像素宽。</summary>
    public int Width { get; }

    /// <summary>像素高。</summary>
    public int Height { get; }

    /// <summary>每行字节数(含对齐填充)。</summary>
    public int Stride { get; }

    /// <summary>像素编码。</summary>
    public PixelEncoding Encoding { get; }

    /// <summary>缓冲字节数。</summary>
    public long ByteCount => Pixels.Length;

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
    /// 释放像素缓冲引用。纯托管对象,无原生资源;幂等。
    /// </summary>
    public void Dispose()
    {
        Pixels = [];
    }
}
