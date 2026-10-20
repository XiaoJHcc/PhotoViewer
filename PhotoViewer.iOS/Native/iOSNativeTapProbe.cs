using CoreGraphics;
using UIKit;

namespace PhotoViewer.iOS.Native;

internal static class iOSNativeTapProbe
{
    internal static void Tap(iOSDiagnosticTile tile, double u, double v)
    {
        for (var parent = tile.Superview; parent != null; parent = parent.Superview)
            if (parent is UIScrollView scroll)
            {
                scroll.ScrollRectToVisible(tile.ConvertRectToView(new CGRect(0, 0, tile.Bounds.Width, tile.Bounds.Width), scroll), false);
                scroll.LayoutIfNeeded();
                break;
            }
        tile.LayoutIfNeeded();
        var rect = tile.ImageContentRect;
        var local = new CGPoint(rect.X + rect.Width * u, rect.Y + rect.Height * v);
        var window = tile.Window ?? throw new InvalidOperationException("瓦片未上屏");
        var point = tile.ConvertPointToView(local, window);
        var hit = window.HitTest(point, null);
        if (!ReferenceEquals(hit, tile)) throw new InvalidOperationException($"UIKit瓦片命中失败：{hit?.GetType().Name ?? "null"}，local={local} frame={tile.Frame}");
        tile.TapAt(local);
    }
}
