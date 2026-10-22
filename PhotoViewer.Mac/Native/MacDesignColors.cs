using AppKit;
using PhotoViewer.Core.Design;

namespace PhotoViewer.Mac.Native;

/// <summary>把 <see cref="DesignTokens"/> 的 0xAARRGGBB 颜色令牌转成 AppKit 颜色，避免各壳重复手抄色值。</summary>
internal static class MacDesignColors
{
    /// <summary>按设计令牌的 0xAARRGGBB 布局创建 sRGB 颜色。</summary>
    /// <param name="argb">令牌颜色值。</param>
    /// <returns>对应的 AppKit 颜色。</returns>
    internal static NSColor Argb(uint argb)
        => NSColor.FromRgba(
            (byte)(argb >> 16),
            (byte)(argb >> 8),
            (byte)argb,
            (byte)(argb >> 24));

    /// <summary>按设计令牌的 RGB 与独立透明度令牌创建颜色。</summary>
    /// <param name="argb">令牌颜色值（只取 RGB）。</param>
    /// <param name="alpha">透明度（0~1）。</param>
    /// <returns>对应的 AppKit 颜色。</returns>
    internal static NSColor Argb(uint argb, double alpha)
        => NSColor.FromRgba(
            (byte)(argb >> 16),
            (byte)(argb >> 8),
            (byte)argb,
            (byte)Math.Round(Math.Clamp(alpha, 0, 1) * byte.MaxValue));
}
