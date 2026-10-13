using System;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading.Tasks;
using Foundation;
using PhotoViewer.Core;
using PhotoViewer.Core.Image;
using PhotoViewer.Core.Storage;

namespace PhotoViewer.iOS.Core;

[Preserve(AllMembers = true)]
public sealed class iOSHeifDecoder : IHeifDecoder
{
    public bool IsSupported => OperatingSystem.IsIOS();

    /// <summary>ImageIO 路径全部预旋转(全尺寸走 CTM、代理走 WithTransform),BitmapLoader 不再二次旋转。</summary>
    public bool OutputPreRotated => true;

    /// <summary>
    /// 解码 HEIF。maxSize = null 全分辨率;非空走编解码器级子采样(thumbnail API,省解码成本与瞬时峰值)。
    /// 路径不可用时回退内存字节源(iOS 沙盒/书签场景)。
    /// </summary>
    public async Task<DecodedImage?> DecodeAsync(IFileRef file, int? maxSize)
    {
        var (path, data) = await PreparePathOrDataAsync(file);
        if (maxSize.HasValue)
            return DecodeSubsampledWithImageIO(path, maxSize.Value, data);
        return DecodeWithImageIO(path, null, data);
    }

    /// <summary>
    /// 代理图子采样解码(P1-M4):CGImageSourceCreateThumbnailAtIndex + MaxPixelSize,FromImageAlways 从原图渲染,
    /// WithTransform=true 输出即显示朝向;支持路径或内存字节源。内存峰值 ≈ 代理尺寸而非全尺寸。
    /// </summary>
    internal static DecodedImage? DecodeSubsampledWithImageIO(string? path, int maxSize, byte[]? fileBytes = null)
    {
        if (maxSize <= 0) return null;
        if (string.IsNullOrEmpty(path) && (fileBytes == null || fileBytes.Length == 0)) return null;

        IntPtr url = IntPtr.Zero;
        IntPtr dataRef = IntPtr.Zero;
        IntPtr src = IntPtr.Zero;
        IntPtr img = IntPtr.Zero;
        IntPtr options = IntPtr.Zero;

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
                dataRef = CFDataCreate(IntPtr.Zero, fileBytes!, (nint)fileBytes!.Length);
                if (dataRef == IntPtr.Zero) return null;
                src = CGImageSourceCreateWithData(dataRef, IntPtr.Zero);
                if (src == IntPtr.Zero) return null;
            }

            options = CreateThumbnailOptions(maxSize);
            if (options == IntPtr.Zero) return null;

            img = CGImageSourceCreateThumbnailAtIndex(src, 0, options);
            if (img == IntPtr.Zero) return null;

            var w = (int)CGImageGetWidth(img);
            var h = (int)CGImageGetHeight(img);
            if (w <= 0 || h <= 0) return null;

            // 输出即显示朝向(WithTransform 已由 ImageIO 应用):1:1 画入 BGRA 缓冲
            var bytesPerRow = w * 4;
            var buffer = DecodedImage.RentPixels(h * bytesPerRow);

            IntPtr colorSpace = IntPtr.Zero;
            IntPtr ctx = IntPtr.Zero;

            unsafe
            {
                fixed (byte* p = buffer)
                {
                    colorSpace = CGColorSpaceCreateDeviceRGB();
                    if (colorSpace == IntPtr.Zero)
                    {
                        ImageBufferPool.Return(buffer);
                        return null;
                    }

                    const int bitsPerComponent = 8;
                    const uint kCGBitmapByteOrder32Little = 2u << 12; // 0x2000
                    const uint kCGImageAlphaPremultipliedFirst = 2u;
                    uint bitmapInfo = kCGBitmapByteOrder32Little | kCGImageAlphaPremultipliedFirst; // BGRA premultiplied

                    ctx = CGBitmapContextCreate((IntPtr)p, (nuint)w, (nuint)h, (nuint)bitsPerComponent,
                        (nuint)bytesPerRow, colorSpace, bitmapInfo);
                    if (ctx == IntPtr.Zero)
                    {
                        CFRelease(colorSpace);
                        ImageBufferPool.Return(buffer);
                        return null;
                    }

                    CGContextDrawImage(ctx, new CGRect(0, 0, w, h), img);

                    CFRelease(ctx);
                    CFRelease(colorSpace);
                    ctx = IntPtr.Zero;
                    colorSpace = IntPtr.Zero;
                }
            }

            return CreateBitmapFromBgraData(buffer, w, h);
        }
        catch (Exception ex)
        {
            Console.WriteLine($"ImageIO subsampled decode failed: {ex.Message}");
            return null;
        }
        finally
        {
            if (options != IntPtr.Zero) CFRelease(options);
            if (img != IntPtr.Zero) CFRelease(img);
            if (src != IntPtr.Zero) CFRelease(src);
            if (dataRef != IntPtr.Zero) CFRelease(dataRef);
            if (url != IntPtr.Zero) CFRelease(url);
        }
    }

    /// <summary>构建 thumbnail API 选项:FromImageAlways=true + MaxPixelSize + WithTransform=true。</summary>
    private static IntPtr CreateThumbnailOptions(int maxSize)
    {
        IntPtr num = IntPtr.Zero;
        try
        {
            IntPtr kFromImageAlways = GetImageIOConstant("kCGImageSourceCreateThumbnailFromImageAlways");
            IntPtr kMaxPixelSize = GetImageIOConstant("kCGImageSourceThumbnailMaxPixelSize");
            IntPtr kWithTransform = GetImageIOConstant("kCGImageSourceCreateThumbnailWithTransform");
            IntPtr cfTrue = GetCFConstant("kCFBooleanTrue");
            if (kFromImageAlways == IntPtr.Zero || kMaxPixelSize == IntPtr.Zero || kWithTransform == IntPtr.Zero || cfTrue == IntPtr.Zero)
                return IntPtr.Zero;

            // P/Invoke 全部显式指针化:mono 解释器对 ref/out 封送存在已证实的间歇崩溃风险(2026-10-13 真机 SIGSEGV)
            unsafe
            {
                num = CFNumberCreate(IntPtr.Zero, /*kCFNumberSInt32Type*/ 3, (IntPtr)(&maxSize));
            }
            if (num == IntPtr.Zero) return IntPtr.Zero;

            // 立即消费的字典:NULL 回调(不 retain)在本次调用内安全
            var keys = new[] { kFromImageAlways, kMaxPixelSize, kWithTransform };
            var values = new[] { cfTrue, num, cfTrue };
            return CFDictionaryCreate(IntPtr.Zero, keys, values, 3, IntPtr.Zero, IntPtr.Zero);
        }
        finally
        {
            if (num != IntPtr.Zero) CFRelease(num);
        }
    }

    private static IntPtr GetImageIOConstant(string name)
        => GetConstant("/System/Library/Frameworks/ImageIO.framework/ImageIO", name);

    private static IntPtr GetCFConstant(string name)
        => GetConstant("/System/Library/Frameworks/CoreFoundation.framework/CoreFoundation", name);

    /// <summary>读框架导出的 CFStringRef/CFBooleanRef 常量(数据符号,需二次解引用)。</summary>
    private static IntPtr GetConstant(string libPath, string name)
    {
        try
        {
            var lib = NativeLibrary.Load(libPath);
            var addr = NativeLibrary.GetExport(lib, name);
            return Marshal.ReadIntPtr(addr);
        }
        catch (Exception ex)
        {
            Console.WriteLine($"GetConstant({name}) failed: {ex.Message}");
            return IntPtr.Zero;
        }
    }

    // 新增：获取可用路径或内存字节
    internal static async Task<(string? path, byte[]? data)> PreparePathOrDataAsync(IFileRef file)
    {
        try
        {
            var path = file.PathKey;
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
    internal static DecodedImage? DecodeWithImageIO(string? path, int? maxSize, byte[]? fileBytes = null)
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
            var buffer = DecodedImage.RentPixels(targetH * bytesPerRow);   // 缓冲池租用(P1-M3)

            IntPtr colorSpace = IntPtr.Zero;
            IntPtr ctx = IntPtr.Zero;

            unsafe
            {
                fixed (byte* p = buffer)
                {
                    colorSpace = CGColorSpaceCreateDeviceRGB();
                    if (colorSpace == IntPtr.Zero)
                    {
                        ImageBufferPool.Return(buffer);
                        return null;
                    }

                    const int bitsPerComponent = 8;
                    const uint kCGBitmapByteOrder32Little = 2u << 12; // 0x2000
                    const uint kCGImageAlphaPremultipliedFirst = 2u;
                    uint bitmapInfo = kCGBitmapByteOrder32Little | kCGImageAlphaPremultipliedFirst; // BGRA premultiplied

                    ctx = CGBitmapContextCreate((IntPtr)p, (nuint)targetW, (nuint)targetH, (nuint)bitsPerComponent,
                        (nuint)bytesPerRow, colorSpace, bitmapInfo);
                    if (ctx == IntPtr.Zero)
                    {
                        CFRelease(colorSpace);
                        ImageBufferPool.Return(buffer);
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
            Console.WriteLine($"ImageIO decode failed: {ex.Message}");
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

            // P/Invoke 全部显式指针化:mono 解释器对 ref/out 封送存在已证实的间歇崩溃风险(2026-10-13 真机 SIGSEGV)
            unsafe
            {
                int v;
                if (CFNumberGetValue(val, /*kCFNumberSInt32Type*/ 3, (IntPtr)(&v)))
                    return v;
            }

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

    private static DecodedImage? CreateBitmapFromBgraData(byte[] bgraData, int width, int height)
    {
        try
        {
            var expected = (long)width * height * 4;
            if (bgraData.Length < expected) return null;
            // 缓冲为池化租用(调用方经 RentPixels 取得),随图像租约归零归还池
            return DecodedImage.CreatePooled(bgraData, width, height, width * 4, PixelEncoding.Bgra32);
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
    private static extern bool CFNumberGetValue(IntPtr number, int theType, IntPtr valuePtr);

    [DllImport("/System/Library/Frameworks/CoreFoundation.framework/CoreFoundation")]
    private static extern bool CFBooleanGetValue(IntPtr boolean);

    [DllImport("/System/Library/Frameworks/ImageIO.framework/ImageIO")]
    private static extern IntPtr CGImageSourceCreateWithURL(IntPtr url, IntPtr options);

    [DllImport("/System/Library/Frameworks/ImageIO.framework/ImageIO")]
    private static extern IntPtr CGImageSourceCreateImageAtIndex(IntPtr isrc, nint index, IntPtr options);

    // 子采样缩略图(P1-M4)
    [DllImport("/System/Library/Frameworks/ImageIO.framework/ImageIO")]
    private static extern IntPtr CGImageSourceCreateThumbnailAtIndex(IntPtr isrc, nint index, IntPtr options);

    [DllImport("/System/Library/Frameworks/CoreFoundation.framework/CoreFoundation")]
    private static extern IntPtr CFNumberCreate(IntPtr allocator, int theType, IntPtr valuePtr);

    [DllImport("/System/Library/Frameworks/CoreFoundation.framework/CoreFoundation")]
    private static extern IntPtr CFDictionaryCreate(IntPtr allocator, IntPtr[] keys, IntPtr[] values, nint numValues, IntPtr keyCallBacks, IntPtr valueCallBacks);

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