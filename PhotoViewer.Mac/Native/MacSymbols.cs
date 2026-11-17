using AppKit;
using Foundation;
using PhotoViewer.Core.Image;
using SkiaSharp;

namespace PhotoViewer.Mac.Native;

/// <summary>原生图标和小图工厂；命令语义映射在共享 <see cref="PhotoViewer.Apple.AppleSymbolNames"/>，不改变共享私用区字体字码。</summary>
public static class MacSymbols
{
    /// <summary>RATING-04：组级星级冲突的警示三角（语义同 <c>icon.warning</c>）。</summary>
    public const string RatingConflict = "exclamationmark.triangle.fill";
    private static readonly Dictionary<string, NSImage> Symbols = new(StringComparer.Ordinal);
    /// <summary>取得稳定的系统符号，缺失时显式报告映射问题。</summary>
    public static NSImage Get(string name)
    {
        if (Symbols.TryGetValue(name, out var cached)) return cached;
        var image = NSImage.GetSystemSymbol(name, name) ?? throw new InvalidOperationException($"系统符号不可用：{name}");
        image.Template = true;
        Symbols.Add(name, image);
        return image;
    }
    /// <summary>取得带层级着色的系统符号，用于需要明确设计色的原生按钮或图像视图。</summary>
    public static NSImage Get(string name, NSColor color)
        => Get(name).GetImage(NSImageSymbolConfiguration.Create(color))
            ?? throw new InvalidOperationException($"系统符号着色失败：{name}");

    /// <summary>创建原生方形图标按钮，尺寸与符号字号由调用点按用途传入设计令牌。</summary>
    /// <param name="symbol">SF Symbol 名称。</param>
    /// <param name="tooltip">悬停提示与读屏名称来源。</param>
    /// <param name="action">点击动作。</param>
    /// <param name="size">按钮边长。</param>
    /// <param name="glyphSize">符号字号。</param>
    /// <param name="border">是否使用系统边框。</param>
    public static NSButton Button(string symbol, string tooltip, Action action, double size, double glyphSize, bool border = true)
    {
        var button = new NSButton(new CoreGraphics.CGRect(0, 0, size, size))
        {
            Image = Get(symbol), ImagePosition = NSCellImagePosition.ImageOnly,
            BezelStyle = NSBezelStyle.TexturedRounded, Bordered = border, ToolTip = tooltip,
        };
        button.SymbolConfiguration = NSImageSymbolConfiguration.Create((nfloat)glyphSize, (double)NSFontWeight.Regular);
        button.Activated += (_, _) => action();
        return button;
    }
    /// <summary>把小图编码成带 sRGB 标签的 PNG，再交 NSImage 管理独立的像素生命周期。</summary>
    public static unsafe NSImage CreateThumbnail(DecodedImage source)
    {
        if (source.Encoding != PixelEncoding.Bgra32) throw new InvalidOperationException("胶片栏需要 BGRA8 缩略图");
        using var space = SKColorSpace.CreateSrgb();
        fixed (byte* pixels = source.Pixels)
        {
            using var pixmap = new SKPixmap(new SKImageInfo(source.Width, source.Height, SKColorType.Bgra8888, SKAlphaType.Premul, space), (IntPtr)pixels, source.Stride);
            using var encoded = pixmap.Encode(SKEncodedImageFormat.Png, 100) ?? throw new InvalidOperationException("缩略图编码失败");
            using var data = NSData.FromArray(encoded.ToArray());
            return new NSImage(data);
        }
    }
}
