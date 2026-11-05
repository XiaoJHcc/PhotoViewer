using System;
using SkiaSharp;

namespace PhotoViewer.Core.Color;

/// <summary>
/// <see cref="ImageColorSpace"/> / ICC 到 Skia 色彩空间的唯一映射：色彩转换与像素操作都从这里取，
/// 避免解码出口、转换与几何变换各自维护一套口径而把标签丢掉。
/// </summary>
internal static class SkColorSpaces
{
    /// <summary>标准 sRGB；旧壳未标记产物同样按 sRGB 解释。</summary>
    public static readonly SKColorSpace Srgb = SKColorSpace.CreateSrgb();

    /// <summary>线性 sRGB（scRGB）。</summary>
    public static readonly SKColorSpace LinearSrgb = SKColorSpace.CreateSrgbLinear();

    /// <summary>Display P3 原色 + sRGB 传递函数。</summary>
    public static readonly SKColorSpace DisplayP3 = SKColorSpace.CreateRgb(SKColorSpaceTransferFn.Srgb, SKColorSpaceXyz.DisplayP3);

    /// <summary>Adobe RGB (1998)。</summary>
    public static readonly SKColorSpace AdobeRgb = SKColorSpace.CreateRgb(
        new SKColorSpaceTransferFn(563f / 256f, 1, 0, 0, 0, 0, 0), SKColorSpaceXyz.AdobeRgb);

    /// <summary>
    /// 解析色彩标签对应的 Skia 色彩空间。ICC 每次新建，须由 <see cref="Handle"/> 释放；
    /// 其余为常驻共享空间，重复释放无副作用。
    /// </summary>
    /// <param name="colorSpace">色彩标签。</param>
    /// <param name="iccProfile">ICC 标签携带的 profile 字节。</param>
    /// <returns>可释放的颜色空间句柄。</returns>
    public static Handle Resolve(ImageColorSpace colorSpace, ReadOnlyMemory<byte> iccProfile = default)
        => new(colorSpace switch
        {
            ImageColorSpace.Legacy or ImageColorSpace.Srgb => Srgb,
            ImageColorSpace.DisplayP3 => DisplayP3,
            ImageColorSpace.AdobeRgb => AdobeRgb,
            ImageColorSpace.LinearSrgb => LinearSrgb,
            ImageColorSpace.Icc => CreateIcc(iccProfile),
            _ => throw new ArgumentOutOfRangeException(nameof(colorSpace)),
        }, colorSpace == ImageColorSpace.Icc);

    /// <summary>按 ICC profile 字节新建 Skia 色彩空间；profile 缺失或无法解析时显式抛出，不静默按 sRGB 处理。</summary>
    private static SKColorSpace CreateIcc(ReadOnlyMemory<byte> profile)
    {
        if (profile.IsEmpty) throw new InvalidOperationException("ICC 色彩标签缺少 profile 数据");
        return SKColorSpace.CreateIcc(profile.Span) ?? throw new InvalidOperationException("无法解析图像 ICC profile");
    }

    /// <summary>Skia 色彩空间句柄：常驻共享空间不释放，ICC 解析结果由使用方释放。</summary>
    internal readonly struct Handle(SKColorSpace space, bool owned) : IDisposable
    {
        /// <summary>解析出的色彩空间。</summary>
        public SKColorSpace Space { get; } = space;

        /// <summary>释放自建的 ICC 色彩空间。</summary>
        public void Dispose()
        {
            if (owned) Space.Dispose();
        }
    }
}
