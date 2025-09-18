using System;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Media.Imaging;
using Avalonia.Platform.Storage;
using PhotoViewer.Core;

namespace PhotoViewer.Mac.Core;

public sealed class MacHeifDecoder : IHeifDecoder
{
    public bool IsSupported => OperatingSystem.IsMacOS();

    public Task<Bitmap?> LoadBitmapAsync(IStorageFile file)
        => Task.Run(() => DecodeWithImageIO(file.Path.LocalPath, null));

    public Task<Bitmap?> LoadThumbnailAsync(IStorageFile file, int maxSize)
        => Task.Run(() => DecodeWithImageIO(file.Path.LocalPath, maxSize));

    private static Bitmap? DecodeWithImageIO(string path, int? maxSize)
    {
        if (string.IsNullOrEmpty(path)) return null;

        IntPtr url = IntPtr.Zero;
        IntPtr src = IntPtr.Zero;
        IntPtr img = IntPtr.Zero;

        try
        {
            var pathUtf8 = Encoding.UTF8.GetBytes(path);
            url = CFURLCreateFromFileSystemRepresentation(IntPtr.Zero, pathUtf8, (nint)pathUtf8.Length, false);
            if (url == IntPtr.Zero) return null;

            src = CGImageSourceCreateWithURL(url, IntPtr.Zero);
            if (src == IntPtr.Zero) return null;

            img = CGImageSourceCreateImageAtIndex(src, 0, IntPtr.Zero);
            if (img == IntPtr.Zero) return null;

            var w = (int)CGImageGetWidth(img);
            var h = (int)CGImageGetHeight(img);
            if (w <= 0 || h <= 0) return null;

            int targetW = w, targetH = h;
            if (maxSize.HasValue)
            {
                var m = maxSize.Value;
                var scale = Math.Min((double)m / w, (double)m / h);
                if (scale < 1.0)
                {
                    targetW = Math.Max(1, (int)Math.Round(w * scale));
                    targetH = Math.Max(1, (int)Math.Round(h * scale));
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

                    var rect = new CGRect(0, 0, targetW, targetH);
                    CGContextDrawImage(ctx, rect, img);

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
            Console.WriteLine($"MacHeifDecoder failed: {ex.Message}");
            return null;
        }
        finally
        {
            if (img != IntPtr.Zero) CFRelease(img);
            if (src != IntPtr.Zero) CFRelease(src);
            if (url != IntPtr.Zero) CFRelease(url);
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

    [DllImport("/System/Library/Frameworks/ImageIO.framework/ImageIO")]
    private static extern IntPtr CGImageSourceCreateWithURL(IntPtr url, IntPtr options);

    [DllImport("/System/Library/Frameworks/ImageIO.framework/ImageIO")]
    private static extern IntPtr CGImageSourceCreateImageAtIndex(IntPtr isrc, nint index, IntPtr options);

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