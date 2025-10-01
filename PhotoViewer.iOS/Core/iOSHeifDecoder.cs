using System;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Media.Imaging;
using Avalonia.Platform.Storage;
using Foundation;
using PhotoViewer.Core;

namespace PhotoViewer.iOS.Core;

[Preserve(AllMembers = true)]
public sealed class iOSHeifDecoder : IHeifDecoder
{
    public bool IsSupported => OperatingSystem.IsIOS();

    // 替换：真正异步 + 路径不可用时用内存数据回退
    public async Task<Bitmap?> LoadBitmapAsync(IStorageFile file)
    {
        var (path, data) = await PreparePathOrDataAsync(file);
        return DecodeWithImageIO(path, null, data);
    }

    public async Task<Bitmap?> LoadThumbnailAsync(IStorageFile file, int maxSize)
    {
        var (path, data) = await PreparePathOrDataAsync(file);
        return DecodeWithImageIO(path, maxSize, data);
    }

    // 新增：获取可用路径或内存字节
    private static async Task<(string? path, byte[]? data)> PreparePathOrDataAsync(IStorageFile file)
    {
        try
        {
            var path = file.Path?.LocalPath;
            if (!string.IsNullOrEmpty(path) && File.Exists(path))
                return (path, null);

            // 路径不可用 -> 内存读取
            await using var stream = await file.OpenReadAsync();
            using var ms = new MemoryStream();
            await stream.CopyToAsync(ms);
            return (null, ms.ToArray());
        }
        catch
        {
            return (null, null);
        }
    }

    // 修改：支持 path 或 memory data
    private static Bitmap? DecodeWithImageIO(string? path, int? maxSize, byte[]? fileBytes = null)
    {
        if (string.IsNullOrEmpty(path) && (fileBytes == null || fileBytes.Length == 0))
            return null;

        IntPtr url = IntPtr.Zero;
        IntPtr dataRef = IntPtr.Zero;
        IntPtr src = IntPtr.Zero;
        IntPtr img = IntPtr.Zero;

        try
        {
            if (!string.IsNullOrEmpty(path))
            {
                var pathUtf8 = Encoding.UTF8.GetBytes(path);
                url = CFURLCreateFromFileSystemRepresentation(IntPtr.Zero, pathUtf8, (nint)pathUtf8.Length, false);
                if (url == IntPtr.Zero) return null;

                src = CGImageSourceCreateWithURL(url, IntPtr.Zero);
                if (src == IntPtr.Zero) return null;
            }
            else
            {
                // 内存方式
                dataRef = CFDataCreate(IntPtr.Zero, fileBytes!, (nint)fileBytes!.Length);
                if (dataRef == IntPtr.Zero) return null;
                src = CGImageSourceCreateWithData(dataRef, IntPtr.Zero);
                if (src == IntPtr.Zero) return null;
            }

            int chosenIndex = 0;
            if (maxSize.HasValue)
            {
                var best = FindBestThumbnailIndex(src, maxSize.Value);
                if (best.HasValue) chosenIndex = best.Value;
            }

            img = CGImageSourceCreateImageAtIndex(src, chosenIndex, IntPtr.Zero);
            if (img == IntPtr.Zero) return null;

            var w = (int)CGImageGetWidth(img);
            var h = (int)CGImageGetHeight(img);
            if (w <= 0 || h <= 0) return null;

            // 读取 EXIF Orientation（1..8），影响旋转/镜像与目标尺寸
            int orientation = GetOrientationAtIndex(src, chosenIndex);
            bool swapWH = OrientationSwapsWH(orientation);

            // 按应用旋转后的自然宽高决定目标尺寸
            int baseW = swapWH ? h : w;
            int baseH = swapWH ? w : h;

            int targetW = baseW, targetH = baseH;
            if (maxSize.HasValue)
            {
                var m = maxSize.Value;
                var scale = Math.Min((double)m / baseW, (double)m / baseH);
                if (scale < 1.0)
                {
                    targetW = Math.Max(1, (int)Math.Round(baseW * scale));
                    targetH = Math.Max(1, (int)Math.Round(baseH * scale));
                }
            }

            var bytesPerRow = targetW * 4;
            var buffer = new byte[targetH * bytesPerRow];

            IntPtr colorSpace = IntPtr.Zero;
            IntPtr ctx = IntPtr.Zero;

            unsafe
            {
                fixed (byte* p = buffer)
                {
                    colorSpace = CGColorSpaceCreateDeviceRGB();
                    if (colorSpace == IntPtr.Zero) return null;

                    const int bitsPerComponent = 8;
                    const uint kCGBitmapByteOrder32Little = 2u << 12; // 0x2000
                    const uint kCGImageAlphaPremultipliedFirst = 2u;
                    uint bitmapInfo = kCGBitmapByteOrder32Little | kCGImageAlphaPremultipliedFirst; // BGRA premultiplied

                    ctx = CGBitmapContextCreate((IntPtr)p, (nuint)targetW, (nuint)targetH, (nuint)bitsPerComponent,
                        (nuint)bytesPerRow, colorSpace, bitmapInfo);
                    if (ctx == IntPtr.Zero)
                    {
                        CFRelease(colorSpace);
                        return null;
                    }

                    // 应用 EXIF 方向变换
                    CGContextSaveGState(ctx);
                    ApplyOrientationTransform(ctx, orientation, targetW, targetH);

                    // 90/270 度旋转后绘制矩形的宽高需要互换
                    var drawW = (swapWH ? targetH : targetW);
                    var drawH = (swapWH ? targetW : targetH);
                    var rect = new CGRect(0, 0, drawW, drawH);
                    CGContextDrawImage(ctx, rect, img);
                    CGContextRestoreGState(ctx);

                    // 释放与原生上下文/色彩空间的关联，避免 buffer 未固定情况下被访问
                    CFRelease(ctx);
                    CFRelease(colorSpace);
                    ctx = IntPtr.Zero;
                    colorSpace = IntPtr.Zero;
                }
            }

            return CreateBitmapFromBgraData(buffer, targetW, targetH);
        }
        catch (Exception ex)
        {
            Console.WriteLine($"iOSHeifDecoder failed: {ex.Message}");
            return null;
        }
        finally
        {
            if (img != IntPtr.Zero) CFRelease(img);
            if (src != IntPtr.Zero) CFRelease(src);
            if (dataRef != IntPtr.Zero) CFRelease(dataRef);
            if (url != IntPtr.Zero) CFRelease(url);
        }
    }

    // 根据 EXIF Orientation 判断是否交换宽高（90/270 度）
    private static bool OrientationSwapsWH(int orientation)
        => orientation == 5 || orientation == 6 || orientation == 7 || orientation == 8;

    // 对 CGContext 应用 EXIF Orientation 变换（1..8）
    private static void ApplyOrientationTransform(IntPtr ctx, int orientation, int canvasW, int canvasH)
    {
        const double PI = Math.PI;
        switch (orientation)
        {
            case 2: // 镜像水平
                CGContextTranslateCTM(ctx, canvasW, 0);
                CGContextScaleCTM(ctx, -1, 1);
                break;
            case 3: // 旋转 180
                CGContextTranslateCTM(ctx, canvasW, canvasH);
                CGContextRotateCTM(ctx, PI);
                break;
            case 4: // 镜像垂直
                CGContextTranslateCTM(ctx, 0, canvasH);
                CGContextScaleCTM(ctx, 1, -1);
                break;
            case 5: // 镜像水平 + 旋转 90 CW（transpose）
                // 先把坐标原点移到左上角（以宽为位移），再顺时针 90°，再镜像水平
                CGContextTranslateCTM(ctx, 0, canvasW);
                CGContextRotateCTM(ctx, -PI / 2);
                CGContextTranslateCTM(ctx, canvasW, 0);
                CGContextScaleCTM(ctx, -1, 1);
                break;
            case 6: // 旋转 90 CW
                // 注意顺时针应使用负角度，并按宽（canvasH 对应原图宽）在 Y 方向平移
                CGContextTranslateCTM(ctx, 0, canvasH);
                CGContextRotateCTM(ctx, -PI / 2);
                break;
            case 7: // 镜像水平 + 旋转 90 CCW（transverse）
                // 先沿 X 方向按高（canvasW 对应原图高）平移，再逆时针 90°，再镜像水平
                CGContextTranslateCTM(ctx, canvasW, 0);
                CGContextRotateCTM(ctx, PI / 2);
                CGContextTranslateCTM(ctx, canvasH, 0);
                CGContextScaleCTM(ctx, -1, 1);
                break;
            case 8: // 旋转 90 CCW
                // 逆时针用正角度，并按高（canvasW 对应原图高）在 X 方向平移
                CGContextTranslateCTM(ctx, canvasW, 0);
                CGContextRotateCTM(ctx, PI / 2);
                break;
            case 1:
            default:
                // 不变换
                break;
        }
    }

    // 读取指定 index 的 Orientation（没有则返回 1）
    private static int GetOrientationAtIndex(IntPtr src, int index)
    {
        IntPtr dict = IntPtr.Zero;
        try
        {
            dict = CGImageSourceCopyPropertiesAtIndex(src, (nint)index, IntPtr.Zero);
            if (dict == IntPtr.Zero) return 1;
            return GetIntProperty(dict, "Orientation", 1);
        }
        finally
        {
            if (dict != IntPtr.Zero) CFRelease(dict);
        }
    }

    // 在缩略图中选择最优索引
    private static int? FindBestThumbnailIndex(IntPtr src, int maxSize)
    {
        try
        {
            int count = (int)CGImageSourceGetCount(src);
            int? bestGreaterIdx = null;
            int bestGreaterLong = int.MaxValue;

            int? bestElseIdx = null;
            int bestElseLong = -1;

            for (int i = 0; i < count; i++)
            {
                IntPtr dict = IntPtr.Zero;
                try
                {
                    dict = CGImageSourceCopyPropertiesAtIndex(src, (nint)i, IntPtr.Zero);
                    if (dict == IntPtr.Zero) continue;

                    bool isThumb = GetBoolProperty(dict, "IsThumbnail", false);
                    if (!isThumb) continue;

                    int pw = GetIntProperty(dict, "PixelWidth", 0);
                    int ph = GetIntProperty(dict, "PixelHeight", 0);
                    if (pw <= 0 || ph <= 0) continue;

                    int longEdge = Math.Max(pw, ph);

                    if (longEdge > maxSize)
                    {
                        if (longEdge < bestGreaterLong)
                        {
                            bestGreaterLong = longEdge;
                            bestGreaterIdx = i;
                        }
                    }
                    else
                    {
                        if (longEdge > bestElseLong)
                        {
                            bestElseLong = longEdge;
                            bestElseIdx = i;
                        }
                    }
                }
                finally
                {
                    if (dict != IntPtr.Zero) CFRelease(dict);
                }
            }

            if (bestGreaterIdx.HasValue) return bestGreaterIdx.Value;
            if (bestElseIdx.HasValue) return bestElseIdx.Value;
            return null;
        }
        catch
        {
            return null;
        }
    }

    // 读取 int 属性
    private static int GetIntProperty(IntPtr dict, string key, int defaultValue)
    {
        IntPtr keyStr = IntPtr.Zero;
        try
        {
            keyStr = CFStringCreateWithCString(IntPtr.Zero, key, kCFStringEncodingUTF8);
            if (keyStr == IntPtr.Zero) return defaultValue;

            var val = CFDictionaryGetValue(dict, keyStr);
            if (val == IntPtr.Zero) return defaultValue;

            if (CFNumberGetValue(val, /*kCFNumberSInt32Type*/ 3, out int v))
                return v;

            return defaultValue;
        }
        finally
        {
            if (keyStr != IntPtr.Zero) CFRelease(keyStr);
        }
    }

    // 读取 bool 属性
    private static bool GetBoolProperty(IntPtr dict, string key, bool defaultValue)
    {
        IntPtr keyStr = IntPtr.Zero;
        try
        {
            keyStr = CFStringCreateWithCString(IntPtr.Zero, key, kCFStringEncodingUTF8);
            if (keyStr == IntPtr.Zero) return defaultValue;

            var val = CFDictionaryGetValue(dict, keyStr);
            if (val == IntPtr.Zero) return defaultValue;

            return CFBooleanGetValue(val);
        }
        finally
        {
            if (keyStr != IntPtr.Zero) CFRelease(keyStr);
        }
    }

    private static Bitmap? CreateBitmapFromBgraData(byte[] bgraData, int width, int height)
    {
        try
        {
            var expected = width * height * 4;
            if (bgraData.Length < expected) return null;

            var bmp = new WriteableBitmap(new PixelSize(width, height), new Vector(96, 96), Avalonia.Platform.PixelFormat.Bgra8888);
            using (var locked = bmp.Lock())
            {
                var destStride = locked.RowBytes;
                var srcStride = width * 4;

                unsafe
                {
                    var destPtr = (byte*)locked.Address;
                    for (int y = 0; y < height; y++)
                    {
                        var srcOffset = y * srcStride;
                        var destOffset = y * destStride;
                        var copy = Math.Min(srcStride, destStride);
                        if (srcOffset + copy > bgraData.Length) break;

                        fixed (byte* srcPtr = &bgraData[srcOffset])
                        {
                            Buffer.MemoryCopy(srcPtr, destPtr + destOffset, copy, copy);
                        }
                    }
                }
            }
            return bmp;
        }
        catch (Exception ex)
        {
            Console.WriteLine($"CreateBitmapFromBgraData failed: {ex.Message}");
            return null;
        }
    }

    // ==== Native interop (CoreFoundation / ImageIO / CoreGraphics) ====

    [DllImport("/System/Library/Frameworks/CoreFoundation.framework/CoreFoundation")]
    private static extern IntPtr CFURLCreateFromFileSystemRepresentation(IntPtr allocator, byte[] buffer, nint bufLen, bool isDirectory);

    [DllImport("/System/Library/Frameworks/CoreFoundation.framework/CoreFoundation")]
    private static extern void CFRelease(IntPtr cfTypeRef);

    // 新增：CFString / CFDictionary / CFNumber / CFBoolean
    [DllImport("/System/Library/Frameworks/CoreFoundation.framework/CoreFoundation")]
    private static extern IntPtr CFStringCreateWithCString(IntPtr alloc, string str, uint encoding);
    private const uint kCFStringEncodingUTF8 = 0x08000100;

    [DllImport("/System/Library/Frameworks/CoreFoundation.framework/CoreFoundation")]
    private static extern IntPtr CFDictionaryGetValue(IntPtr theDict, IntPtr key);

    [DllImport("/System/Library/Frameworks/CoreFoundation.framework/CoreFoundation")]
    private static extern bool CFNumberGetValue(IntPtr number, int theType, out int value);

    [DllImport("/System/Library/Frameworks/CoreFoundation.framework/CoreFoundation")]
    private static extern bool CFBooleanGetValue(IntPtr boolean);

    [DllImport("/System/Library/Frameworks/ImageIO.framework/ImageIO")]
    private static extern IntPtr CGImageSourceCreateWithURL(IntPtr url, IntPtr options);

    [DllImport("/System/Library/Frameworks/ImageIO.framework/ImageIO")]
    private static extern IntPtr CGImageSourceCreateImageAtIndex(IntPtr isrc, nint index, IntPtr options);

    // 新增：CFDataCreate / CGImageSourceCreateWithData
    [DllImport("/System/Library/Frameworks/CoreFoundation.framework/CoreFoundation")]
    private static extern IntPtr CFDataCreate(IntPtr allocator, byte[] bytes, nint length);

    [DllImport("/System/Library/Frameworks/ImageIO.framework/ImageIO")]
    private static extern IntPtr CGImageSourceCreateWithData(IntPtr data, IntPtr options);

    // 新增：读取属性与计数
    [DllImport("/System/Library/Frameworks/ImageIO.framework/ImageIO")]
    private static extern IntPtr CGImageSourceCopyPropertiesAtIndex(IntPtr isrc, nint index, IntPtr options);

    [DllImport("/System/Library/Frameworks/ImageIO.framework/ImageIO")]
    private static extern nint CGImageSourceGetCount(IntPtr isrc);

    [DllImport("/System/Library/Frameworks/CoreGraphics.framework/CoreGraphics")]
    private static extern nint CGImageGetWidth(IntPtr image);

    [DllImport("/System/Library/Frameworks/CoreGraphics.framework/CoreGraphics")]
    private static extern nint CGImageGetHeight(IntPtr image);

    [DllImport("/System/Library/Frameworks/CoreGraphics.framework/CoreGraphics")]
    private static extern IntPtr CGColorSpaceCreateDeviceRGB();

    [DllImport("/System/Library/Frameworks/CoreGraphics.framework/CoreGraphics")]
    private static extern IntPtr CGBitmapContextCreate(
        IntPtr data,
        nuint width,
        nuint height,
        nuint bitsPerComponent,
        nuint bytesPerRow,
        IntPtr colorSpace,
        uint bitmapInfo);

    [DllImport("/System/Library/Frameworks/CoreGraphics.framework/CoreGraphics")]
    private static extern void CGContextDrawImage(IntPtr c, CGRect rect, IntPtr image);

    // 新增：CTM 变换
    [DllImport("/System/Library/Frameworks/CoreGraphics.framework/CoreGraphics")]
    private static extern void CGContextSaveGState(IntPtr c);

    [DllImport("/System/Library/Frameworks/CoreGraphics.framework/CoreGraphics")]
    private static extern void CGContextRestoreGState(IntPtr c);

    [DllImport("/System/Library/Frameworks/CoreGraphics.framework/CoreGraphics")]
    private static extern void CGContextTranslateCTM(IntPtr c, double tx, double ty);

    [DllImport("/System/Library/Frameworks/CoreGraphics.framework/CoreGraphics")]
    private static extern void CGContextRotateCTM(IntPtr c, double angle);

    [DllImport("/System/Library/Frameworks/CoreGraphics.framework/CoreGraphics")]
    private static extern void CGContextScaleCTM(IntPtr c, double sx, double sy);

    // 基础结构体
    [StructLayout(LayoutKind.Sequential)]
    private struct CGPoint
    {
        public double X;
        public double Y;
        public CGPoint(double x, double y) { X = x; Y = y; }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct CGSize
    {
        public double Width;
        public double Height;
        public CGSize(double w, double h) { Width = w; Height = h; }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct CGRect
    {
        public CGPoint Origin;
        public CGSize Size;
        public CGRect(double x, double y, double w, double h)
        {
            Origin = new CGPoint(x, y);
            Size = new CGSize(w, h);
        }
    }
}