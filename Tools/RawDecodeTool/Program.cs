using Avalonia.Platform.Storage;
using PhotoViewer.Core.Image;
using PhotoViewer.Core.Storage;
using PhotoViewer.Mac.Core;
using PhotoViewer.Platform;

// ========================================================
// RAW 解码验证工具
// 用法：dotnet run --project Tools/RawDecodeTool -- <输出目录> <raw文件1> [raw文件2 ...]
// 对每个文件：
//   1) 主线程直调 MacRawDecoder 的 ImageIO 解码（全尺寸）→ <name>.full.png（验证 EXIF 方向已应用）
//   2) 同一解码路径的长边 400 缩略图 → <name>.thumb.png
//   3) ThumbnailService 枚举来源列表 + 取 400 短边缩略图 → <name>.svc.png
// 需要 macOS（ImageIO）；请在仓库根目录运行。
// ========================================================

if (args.Length < 2)
{
    Console.WriteLine("用法: dotnet run --project Tools/RawDecodeTool -- <输出目录> <raw文件...>");
    return 1;
}

var outDir = Path.GetFullPath(args[0]);
Directory.CreateDirectory(outDir);

RawLoader.Initialize(new MacRawDecoder());

var failures = 0;
foreach (var rawPath in args.Skip(1))
{
    var full = Path.GetFullPath(rawPath);
    var name = Path.GetFileNameWithoutExtension(full);
    Console.WriteLine($"=== {name} ===");

    IFileRef file = new AvaloniaFileRef(CreateStorageFile(full));
    Console.WriteLine($"IsRawFile: {RawLoader.IsRawFile(file)}, IsSupported: {RawLoader.IsSupported}");

    // 1) 全尺寸解码
    var sw = System.Diagnostics.Stopwatch.StartNew();
    var bmp = MacHeifDecoder.DecodeWithImageIO(full, null);
    sw.Stop();
    if (bmp == null)
    {
        Console.WriteLine("全尺寸解码失败");
        failures++;
    }
    else
    {
        Console.WriteLine($"全尺寸: {bmp.Width}x{bmp.Height} 编码={bmp.Encoding} 耗时={sw.ElapsedMilliseconds}ms");
        SavePngViaImageIO(bmp, Path.Combine(outDir, $"{name}.full.png"));
        bmp.Dispose();
    }

    // 2) 解码器缩略图
    sw.Restart();
    var thumb = MacHeifDecoder.DecodeWithImageIO(full, 400);
    sw.Stop();
    if (thumb == null)
    {
        Console.WriteLine("缩略图解码失败");
        failures++;
    }
    else
    {
        Console.WriteLine($"缩略图: {thumb.Width}x{thumb.Height} 耗时={sw.ElapsedMilliseconds}ms");
        SavePngViaImageIO(thumb, Path.Combine(outDir, $"{name}.thumb.png"));
        thumb.Dispose();
    }

    // 2.5) iOS 解码器代码路径验证（同一 ImageIO 调用，macOS 上可直接运行）
    var iosThumb = PhotoViewer.iOS.Core.iOSHeifDecoder.DecodeWithImageIO(full, 400);
    if (iosThumb == null)
    {
        Console.WriteLine("iOS 解码路径失败");
        failures++;
    }
    else
    {
        Console.WriteLine($"iOS 路径: {iosThumb.Width}x{iosThumb.Height}");
        iosThumb.Dispose();
    }

    // 3) ThumbnailService 来源枚举 + 服务路径取图
    var sources = await ThumbnailService.GetAvailableSourcesAsync(file);
    foreach (var s in sources)
        Console.WriteLine($"来源: {s.Origin} {s.Width}x{s.Height} preRotated={s.IsPreRotated}");
    var svc = await ThumbnailService.GetThumbnailAsync(file, 400);
    if (svc == null)
    {
        Console.WriteLine("ThumbnailService 取图失败");
        failures++;
    }
    else
    {
        Console.WriteLine($"ThumbnailService: {svc.Width}x{svc.Height}");
        SavePngViaImageIO(svc, Path.Combine(outDir, $"{name}.svc.png"));
        svc.Dispose();
    }
}

Console.WriteLine(failures == 0 ? "全部成功" : $"有 {failures} 项失败");
return failures == 0 ? 0 : 2;

/// <summary>
/// Avalonia 12 的 IStorageFile 不允许用户代码实现，BclStorageFile 为 internal；
/// 测试工具里用反射创建它（仅限本 harness，不动生产代码）。
/// </summary>
static IStorageFile CreateStorageFile(string fullPath)
{
    var type = typeof(IStorageFile).Assembly.GetType("Avalonia.Platform.Storage.FileIO.BclStorageFile")
        ?? throw new InvalidOperationException("找不到 BclStorageFile 类型");
    return (IStorageFile)Activator.CreateInstance(
        type,
        System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Public,
        binder: null,
        args: new object[] { new FileInfo(fullPath) },
        culture: null)!;
}

/// <summary>
/// 用 ImageIO（CGImageDestination）把 Bgra32 像素缓冲落盘 PNG。
/// 在 pin 期间同步完成 CGImage 创建与写出，保证像素指针有效。
/// </summary>
static unsafe void SavePngViaImageIO(DecodedImage img, string outPath)
{
    int w = img.Width, h = img.Height;
    var rowBytes = (nuint)img.Stride;

    // Bgra32（内存 B,G,R,A = LE 32 位 ARGB）：byteOrder32Little + premultipliedFirst
    uint bitmapInfo = (2u << 12) | 2u;

    IntPtr provider = IntPtr.Zero, cs = IntPtr.Zero, cgImg = IntPtr.Zero, url = IntPtr.Zero, dest = IntPtr.Zero;
    fixed (byte* pixelsPtr = img.Pixels)
    {
        try
        {
            provider = CGDataProviderCreateWithData(IntPtr.Zero, (IntPtr)pixelsPtr, (nint)(img.Stride * (long)h), IntPtr.Zero);
            cs = CGColorSpaceCreateDeviceRGB();
            cgImg = CGImageCreate((nuint)w, (nuint)h, 8, 32, rowBytes, cs, bitmapInfo, provider,
                IntPtr.Zero, false, 0 /* kCGRenderingIntentDefault */);
            if (cgImg == IntPtr.Zero) throw new InvalidOperationException("CGImageCreate failed");

            var pathBytes = System.Text.Encoding.UTF8.GetBytes(outPath);
            url = CFURLCreateFromFileSystemRepresentation(IntPtr.Zero, pathBytes, pathBytes.Length, false);
            var typeUtf8 = System.Text.Encoding.UTF8.GetBytes("public.png\0");
            var type = CFStringCreateWithCString(IntPtr.Zero, typeUtf8, 0x08000100);
            dest = CGImageDestinationCreateWithURL(url, type, 1, IntPtr.Zero);
            if (dest == IntPtr.Zero) throw new InvalidOperationException("CGImageDestinationCreateWithURL failed");
            CGImageDestinationAddImage(dest, cgImg, IntPtr.Zero);
            if (!CGImageDestinationFinalize(dest)) throw new InvalidOperationException("CGImageDestinationFinalize failed");
            if (type != IntPtr.Zero) CFRelease(type);
        }
        finally
        {
            if (dest != IntPtr.Zero) CFRelease(dest);
            if (url != IntPtr.Zero) CFRelease(url);
            if (cgImg != IntPtr.Zero) CFRelease(cgImg);
            if (cs != IntPtr.Zero) CFRelease(cs);
            if (provider != IntPtr.Zero) CFRelease(provider);
        }
    }
}

[System.Runtime.InteropServices.DllImport("/System/Library/Frameworks/CoreFoundation.framework/CoreFoundation")]
static extern IntPtr CFURLCreateFromFileSystemRepresentation(IntPtr a, byte[] b, nint len, bool isDir);
[System.Runtime.InteropServices.DllImport("/System/Library/Frameworks/CoreFoundation.framework/CoreFoundation")]
static extern IntPtr CFStringCreateWithCString(IntPtr a, byte[] b, uint enc);
[System.Runtime.InteropServices.DllImport("/System/Library/Frameworks/CoreFoundation.framework/CoreFoundation")]
static extern void CFRelease(IntPtr r);
[System.Runtime.InteropServices.DllImport("/System/Library/Frameworks/CoreGraphics.framework/CoreGraphics")]
static extern IntPtr CGColorSpaceCreateDeviceRGB();
[System.Runtime.InteropServices.DllImport("/System/Library/Frameworks/CoreGraphics.framework/CoreGraphics")]
static extern IntPtr CGDataProviderCreateWithData(IntPtr info, IntPtr data, nint size, IntPtr releaseCallback);
[System.Runtime.InteropServices.DllImport("/System/Library/Frameworks/CoreGraphics.framework/CoreGraphics")]
static extern IntPtr CGImageCreate(nuint w, nuint h, nuint bpc, nuint bpp, nuint rowBytes, IntPtr cs,
    uint bitmapInfo, IntPtr provider, IntPtr decode, bool shouldInterpolate, nint intent);
[System.Runtime.InteropServices.DllImport("/System/Library/Frameworks/ImageIO.framework/ImageIO")]
static extern IntPtr CGImageDestinationCreateWithURL(IntPtr url, IntPtr type, nint count, IntPtr options);
[System.Runtime.InteropServices.DllImport("/System/Library/Frameworks/ImageIO.framework/ImageIO")]
static extern void CGImageDestinationAddImage(IntPtr dest, IntPtr img, IntPtr props);
[System.Runtime.InteropServices.DllImport("/System/Library/Frameworks/ImageIO.framework/ImageIO")]
static extern bool CGImageDestinationFinalize(IntPtr dest);
