using AppKit;
using CoreGraphics;
using PhotoViewer.Core.Color;

namespace PhotoViewer.Mac.Native;

/// <summary>原生控件缓存绘制与实际 Metal 帧导出，仅用于布局/方向验收，不冒充系统桌面截屏。</summary>
public static class NativeSnapshots
{
    /// <summary>保存原生设置控件实际绘制结果。</summary>
    public static void SaveView(NSView view, string path)
    {
        using var bitmap = view.BitmapImageRepForCachingDisplayInRect(view.Bounds) ?? throw new InvalidOperationException("原生截图缓冲创建失败");
        view.CacheDisplay(view.Bounds, bitmap);
        using var data = bitmap.RepresentationUsingTypeProperties(NSBitmapImageFileType.Png) ?? throw new InvalidOperationException("原生截图编码失败");
        File.WriteAllBytes(path, data.ToArray());
    }
    /// <summary>主窗口控件绘制与同一 Metal 渲染器的 GPU 帧合成导出，保持真实控件及图片几何。</summary>
    public static void SaveMain(MacMainWindow window, string path)
    {
        var root = window.Window.ContentView ?? throw new InvalidOperationException("原生内容视图缺失");
        using var bitmap = root.BitmapImageRepForCachingDisplayInRect(root.Bounds) ?? throw new InvalidOperationException("原生截图缓冲创建失败");
        root.CacheDisplay(root.Bounds, bitmap);
        using var frame = window.Photo.CaptureLinearFrame();
        using var srgb = ColorEngine.ToSrgb8(frame);
        using var photo = MacSymbols.CreateThumbnail(srgb);
        using var context = NSGraphicsContext.FromBitmap(bitmap);
        var previous = NSGraphicsContext.CurrentContext;
        context.SaveGraphicsState();
        try
        {
            NSGraphicsContext.CurrentContext = context;
            var rectangle = window.Photo.ConvertRectToView(window.Photo.Bounds, root);
            photo.Draw(new CGRect(rectangle.X, root.Bounds.Height - rectangle.Y - rectangle.Height, rectangle.Width, rectangle.Height));
        }
        finally { context.RestoreGraphicsState(); NSGraphicsContext.CurrentContext = previous; }
        using var data = bitmap.RepresentationUsingTypeProperties(NSBitmapImageFileType.Png) ?? throw new InvalidOperationException("原生截图编码失败");
        File.WriteAllBytes(path, data.ToArray());
    }
}
