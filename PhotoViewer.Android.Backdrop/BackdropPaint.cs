using Gfx = Android.Graphics;

namespace PhotoViewer.Android.Backdrop;

/// <summary>绘制器共用的平台画笔工具。</summary>
internal static class BackdropPaint
{
    /// <summary>
    /// 把上游 <c>BlendMode</c> 对应的 Porter-Duff 模式设到画笔上；模式变化时才新建
    /// <see cref="Gfx.PorterDuffXfermode"/>，<see cref="Gfx.PorterDuff.Mode.SrcOver"/> 用 <c>null</c>
    /// （默认混合，不分配）。
    /// </summary>
    /// <param name="paint">目标画笔。</param>
    /// <param name="mode">混合模式。</param>
    /// <param name="cachedMode">绘制器缓存的当前模式。</param>
    /// <param name="xfermode">绘制器缓存的 xfermode。</param>
    public static void ApplyBlendMode(
        Gfx.Paint paint,
        Gfx.PorterDuff.Mode mode,
        ref Gfx.PorterDuff.Mode? cachedMode,
        ref Gfx.PorterDuffXfermode? xfermode)
    {
        if (Equals(cachedMode, mode)) return;

        cachedMode = mode;
        xfermode?.Dispose();
        xfermode = Equals(mode, Gfx.PorterDuff.Mode.SrcOver)
            ? null
            : new Gfx.PorterDuffXfermode(mode);
        paint.SetXfermode(xfermode);
    }
}
