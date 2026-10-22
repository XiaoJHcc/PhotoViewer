using PhotoViewer.Core.Design;
using UIKit;

namespace PhotoViewer.iOS.Native;

/// <summary>
/// 设计令牌颜色（0xAARRGGBB）到 UIKit 颜色的转换；
/// 令牌由 <see cref="DesignTokens"/> 生成，壳内不另存一份色值。
/// </summary>
internal static class iOSColors
{
    /// <summary>
    /// 按令牌自身的不透明度创建颜色。
    /// </summary>
    /// <param name="argb">0xAARRGGBB 颜色令牌。</param>
    /// <returns>UIKit 颜色。</returns>
    internal static UIColor FromToken(uint argb) => FromToken(argb, ((argb >> 24) & 0xFF) / 255.0);

    /// <summary>
    /// 用指定不透明度覆盖令牌的 alpha（用于登记为独立令牌的不透明度，如提示条 0.94）。
    /// </summary>
    /// <param name="argb">0xAARRGGBB 颜色令牌。</param>
    /// <param name="alpha">0–1 不透明度。</param>
    /// <returns>UIKit 颜色。</returns>
    internal static UIColor FromToken(uint argb, double alpha) => UIColor.FromRGBA(
        (int)((argb >> 16) & 0xFF),
        (int)((argb >> 8) & 0xFF),
        (int)(argb & 0xFF),
        (int)Math.Round(Math.Clamp(alpha, 0, 1) * 255));
}
