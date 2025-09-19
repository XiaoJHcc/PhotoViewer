using System;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using Avalonia.Media.Imaging;
using Avalonia.Platform.Storage;
using Avalonia;
using Avalonia.Platform;
using PhotoViewer.Core;
using WicNet;
using DirectN;

namespace PhotoViewer.Desktop.Core;

public sealed class WindowsHeifDecoder : IHeifDecoder
{
#if !WINDOWS
    public bool IsSupported => false;
    private readonly IHeifDecoder _fallback = new LibHeifDecoder();
#else
    private static readonly Guid HeifContainerGuid = new("E1E62521-6787-405B-8D77-260D8D729909");
    private readonly Lazy<bool> _isSupported;
    private readonly IHeifDecoder _fallback;

    public WindowsHeifDecoder()
    {
        _fallback = new LibHeifDecoder();
        _isSupported = new Lazy<bool>(DetectSupport, isThreadSafe: true);
    }

    public bool IsSupported => _isSupported.Value;
#endif

    public async Task<Bitmap?> LoadBitmapAsync(IStorageFile file)
    {
#if !WINDOWS
        Console.WriteLine("[WindowsHeifDecoder] Non-Windows platform. Falling back to LibHeifDecoder.");
        return await _fallback.LoadBitmapAsync(file);
#else
        if (!IsSupported)
        {
            Console.WriteLine("[WindowsHeifDecoder] WIC HEIF not supported. Falling back to LibHeifDecoder.");
            return await _fallback.LoadBitmapAsync(file);
        }

        try
        {
            return await Task.Run(async () =>
            {
                await using var src = await file.OpenReadAsync();
                using var mem = new MemoryStream();
                await src.CopyToAsync(mem);
                mem.Position = 0;

                using var decoder = WicBitmapDecoder.Load(mem);
                if (decoder.FrameCount == 0)
                {
                    Console.WriteLine("[WindowsHeifDecoder] No frames in file. Fallback.");
                    return await _fallback.LoadBitmapAsync(file);
                }

                using var frame = decoder.GetFrame(0);
                ApplyOrientation(decoder, frame);
                StandardizePixelFormat(frame);

                return CreateAvaloniaBitmapFromWic(frame);
            });
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[WindowsHeifDecoder] Decode failed: {ex.Message}. Falling back.");
            try
            {
                return await _fallback.LoadBitmapAsync(file);
            }
            catch (Exception fb)
            {
                Console.WriteLine($"[WindowsHeifDecoder] Fallback also failed: {fb.Message}");
                return null;
            }
        }
#endif
    }

    public async Task<Bitmap?> LoadThumbnailAsync(IStorageFile file, int maxSize)
    {
#if !WINDOWS
        return await _fallback.LoadThumbnailAsync(file, maxSize);
#else
        if (maxSize <= 0)
            return await LoadBitmapAsync(file);

        if (!IsSupported)
        {
            Console.WriteLine("[WindowsHeifDecoder] WIC HEIF not supported (thumbnail). Falling back.");
            return await _fallback.LoadThumbnailAsync(file, maxSize);
        }

        try
        {
            return await Task.Run(async () =>
            {
                await using var src = await file.OpenReadAsync();
                using var mem = new MemoryStream();
                await src.CopyToAsync(mem);
                mem.Position = 0;

                using var decoder = WicBitmapDecoder.Load(mem);
                if (decoder.FrameCount == 0)
                {
                    Console.WriteLine("[WindowsHeifDecoder] No frames for thumbnail. Fallback.");
                    return await _fallback.LoadThumbnailAsync(file, maxSize);
                }

                // Collect frames
                var frames = new List<WicBitmapSource>();
                for (int i = 0; i < decoder.FrameCount; i++)
                    frames.Add(decoder.GetFrame(i));

                var main = frames[0];
                var mainLongest = Math.Max((int)main.Width, (int)main.Height);

                var candidates = frames
                    .Skip(1)
                    .Select(f => new
                    {
                        Frame = f,
                        Longest = Math.Max((int)f.Width, (int)f.Height),
                        Width = f.Width,
                        Height = f.Height
                    })
                    .Where(c => c.Width < main.Width || c.Height < main.Height)
                    .ToList();

                WicBitmapSource chosenFrame = main;

                var overOrEqual = candidates
                    .Where(c => c.Longest >= maxSize)
                    .OrderBy(c => c.Longest)
                    .FirstOrDefault();

                if (overOrEqual != null)
                {
                    chosenFrame = overOrEqual.Frame;
                }
                else
                {
                    var under = candidates
                        .Where(c => c.Longest < maxSize)
                        .OrderByDescending(c => c.Longest)
                        .FirstOrDefault();
                    if (under != null)
                        chosenFrame = under.Frame;
                }

                ApplyOrientation(decoder, chosenFrame);
                StandardizePixelFormat(chosenFrame);

                // Scale if still larger than maxSize
                var chosenLongest = Math.Max((int)chosenFrame.Width, (int)chosenFrame.Height);
                if (chosenLongest > maxSize)
                {
                    if (chosenFrame.Width >= chosenFrame.Height)
                        chosenFrame.Scale(maxSize, null, WICBitmapInterpolationMode.WICBitmapInterpolationModeHighQualityCubic);
                    else
                        chosenFrame.Scale(null, maxSize, WICBitmapInterpolationMode.WICBitmapInterpolationModeHighQualityCubic);
                }

                return CreateAvaloniaBitmapFromWic(chosenFrame);
            });
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[WindowsHeifDecoder] Thumbnail decode failed: {ex.Message}. Fallback.");
            try
            {
                return await _fallback.LoadThumbnailAsync(file, maxSize);
            }
            catch (Exception fb)
            {
                Console.WriteLine($"[WindowsHeifDecoder] Thumbnail fallback failed: {fb.Message}");
                return null;
            }
        }
#endif
    }

#if WINDOWS
    private bool DetectSupport()
    {
        if (!OperatingSystem.IsWindows())
            return false;

        try
        {
            var decoders = WicImagingComponent.AllComponents.OfType<WicDecoder>().ToList();

            bool extMatch = decoders.Any(d =>
            {
                try
                {
                    return d.FileExtensionsList.Any(e =>
                        e.Equals(".heic", StringComparison.OrdinalIgnoreCase) ||
                        e.Equals(".heif", StringComparison.OrdinalIgnoreCase));
                }
                catch
                {
                    return false;
                }
            });

            if (extMatch)
                return true;

            bool containerMatch = decoders.Any(d =>
            {
                try { return d.ContainerFormat == HeifContainerGuid; }
                catch { return false; }
            });

            if (!containerMatch)
                Console.WriteLine("[WindowsHeifDecoder] No HEIF WIC decoder found.");

            return containerMatch;
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[WindowsHeifDecoder] DetectSupport failed: {ex.Message}");
            return false;
        }
    }

    private static void StandardizePixelFormat(WicBitmapSource bmp)
    {
        try
        {
            // Avalonia 期望 BGRA 预乘 (Bgra8888 + Premul)
            bmp.ConvertTo(WicPixelFormat.GUID_WICPixelFormat32bppPBGRA);
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[WindowsHeifDecoder] ConvertTo 32bppPBGRA failed (ignored): {ex.Message}");
        }
    }

    private static void ApplyOrientation(WicBitmapDecoder decoder, WicBitmapSource frame)
    {
        int orientation = 1;

        // 1) 容器 Metadata Query Reader
        var decQuery = decoder.GetMetadataQueryReader();
        if (decQuery != null)
        {
            if (TryReadOrientation(decQuery, out var o))
                orientation = o;
        }

        // 2) 帧（有些格式可能把拍摄方向放在帧级）
        if (orientation == 1)
        {
            var frameQuery = frame.GetMetadataReader();
            if (frameQuery != null && TryReadOrientation(frameQuery, out var of))
                orientation = of;
        }

        if (orientation == 1) return;

        try
        {
            switch (orientation)
            {
                case 2:
                    frame.FlipRotate(WICBitmapTransformOptions.WICBitmapTransformFlipHorizontal);
                    break;
                case 3:
                    frame.FlipRotate(WICBitmapTransformOptions.WICBitmapTransformRotate180);
                    break;
                case 4:
                    frame.FlipRotate(WICBitmapTransformOptions.WICBitmapTransformFlipVertical);
                    break;
                case 5:
                    frame.FlipRotate(WICBitmapTransformOptions.WICBitmapTransformRotate270 | WICBitmapTransformOptions.WICBitmapTransformFlipHorizontal);
                    break;
                case 6:
                    frame.FlipRotate(WICBitmapTransformOptions.WICBitmapTransformRotate90);
                    break;
                case 7:
                    frame.FlipRotate(WICBitmapTransformOptions.WICBitmapTransformRotate90 | WICBitmapTransformOptions.WICBitmapTransformFlipHorizontal);
                    break;
                case 8:
                    frame.FlipRotate(WICBitmapTransformOptions.WICBitmapTransformRotate270);
                    break;
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[WindowsHeifDecoder] ApplyOrientation failed (ignored): {ex.Message}");
        }
    }

    private static bool TryReadOrientation(WicMetadataQueryReader reader, out int orientation)
    {
        orientation = 1;
        try
        {
            if (reader.TryGetMetadataByName("/app1/ifd/{ushort=274}", out var val, out _) ||
                reader.TryGetMetadataByName("/ifd/{ushort=274}", out val, out _))
            {
                if (val is ushort us) { orientation = us; return true; }
                if (val is short s) { orientation = s; return true; }
                if (val is int i) { orientation = i; return true; }
                if (val is string str && int.TryParse(str, out var parsed)) { orientation = parsed; return true; }
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[WindowsHeifDecoder] Orientation read failed (ignored): {ex.Message}");
        }
        return orientation != 1;
    }

    private static Bitmap CreateAvaloniaBitmapFromWic(WicBitmapSource source)
    {
        int width = (int)source.Width;
        int height = (int)source.Height;
        if (width <= 0 || height <= 0)
            throw new InvalidOperationException("Invalid image size.");

        // 目标统一为 32bpp PBGRA（之前已 ConvertTo），计算 stride
        int srcStride = checked(width * 4);
        int bufferSize = checked(srcStride * height);

        // 准备 Avalonia WriteableBitmap（内部可能行对齐与我们计算的 stride 不同）
        var wb = new WriteableBitmap(
            new PixelSize(width, height),
            new Vector(96, 96),
            PixelFormat.Bgra8888,
            AlphaFormat.Premul);

        // 申请托管缓冲并固定（也可用 ArrayPool<byte>，此处为清晰保持简单）
        var managed = new byte[bufferSize];
        var handle = GCHandle.Alloc(managed, GCHandleType.Pinned);
        try
        {
            nint ptr = handle.AddrOfPinnedObject();
            // WicNet 的签名：CopyPixels(uint bufferSize, nint buffer, int? stride = null)
            source.CopyPixels((uint)bufferSize, ptr, srcStride);

            using var fb = wb.Lock(); // IWritableBitmapLock
            int destStride = fb.RowBytes; // Avalonia 实际行字节数
            nint destBase = fb.Address;

            unsafe
            {
                byte* srcBase = (byte*)ptr;
                byte* dstBase = (byte*)destBase;

                if (destStride == srcStride)
                {
                    Buffer.MemoryCopy(srcBase, dstBase, bufferSize, bufferSize);
                }
                else
                {
                    // 行对齐不同则逐行复制
                    int copyWidth = Math.Min(srcStride, destStride);
                    for (int y = 0; y < height; y++)
                    {
                        byte* s = srcBase + y * srcStride;
                        byte* d = dstBase + y * destStride;
                        Buffer.MemoryCopy(s, d, copyWidth, copyWidth);
                    }
                }
            }
        }
        finally
        {
            handle.Free();
        }

        return wb;
    }
#endif
}