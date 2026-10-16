using AppKit;
using Foundation;
using PhotoViewer.Core.Image;
using SkiaSharp;

namespace PhotoViewer.Mac.Native;

/// <summary>原生图标和小图工厂；命令语义映射不改变共享私用区字体字码。</summary>
public static class MacSymbols
{
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
    /// <summary>按业务命令取得 SF Symbols 名称；未定义映射的命令显式抛出，避免生成无图标按钮。</summary>
    public static string? ForCommand(string command) => command switch
    {
        "Open" => "folder", "Previous" => "chevron.left", "Next" => "chevron.right",
        "Exchange" => "arrow.left.arrow.right", "Fit" => "arrow.up.left.and.arrow.down.right",
        "ZoomInPreset" or "ZoomInScale" => "plus", "ZoomOutPreset" or "ZoomOutScale" => "minus",
        "SetRating0" => "xmark", "SetRating1" or "SetRating2" or "SetRating3" or "SetRating4" or "SetRating5" => "star",
        "ToggleAnalysisView" => "square.grid.2x2", "ToggleEnhance" => "wand.and.stars",
        _ => throw new InvalidOperationException($"未定义原生命令图标：{command}"),
    };
    /// <summary>创建原生方形图标按钮，尺寸由调用方指定。</summary>
    public static NSButton Button(string symbol, string tooltip, Action action, double size = 40, bool border = true)
    {
        var button = new NSButton(new CoreGraphics.CGRect(0, 0, size, size))
        {
            Image = Get(symbol), ImagePosition = NSCellImagePosition.ImageOnly,
            BezelStyle = NSBezelStyle.TexturedRounded, Bordered = border, ToolTip = tooltip,
        };
        double pointSize = size <= 14 ? (symbol == "xmark" ? 8 : 10) : size <= 30 ? (symbol == "xmark" ? 12 : 16) : 18;
        button.SymbolConfiguration = NSImageSymbolConfiguration.Create((nfloat)pointSize, (double)NSFontWeight.Regular);
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
