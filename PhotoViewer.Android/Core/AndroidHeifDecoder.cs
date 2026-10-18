using System;
using System.IO;
using System.Threading.Tasks;
using PhotoViewer.Core;
using PhotoViewer.Core.Image;
using PhotoViewer.Core.Storage;
using Android.Graphics;
using Android.OS;
using Java.Nio;
using Android.Runtime;
using Buffer = System.Buffer;
using Bitmap = Android.Graphics.Bitmap;

namespace PhotoViewer.Android.Core;

public sealed class AndroidHeifDecoder : IHeifDecoder
{
    public bool IsSupported => OperatingSystem.IsAndroid() && (int)Build.VERSION.SdkInt >= 28;

    /// <summary>BitmapFactory 不应用 EXIF 方向,BitmapLoader 负责旋转。</summary>
    public bool OutputPreRotated => false;

    /// <summary>
    /// 解码 HEIF。maxSize = null 全分辨率;非空时代理(内嵌缩略图够大用之,否则 InSampleSize 采样主图 + 精缩放)。
    /// </summary>
    public async Task<DecodedImage?> DecodeAsync(IFileRef file, int? maxSize)
    {
        if (!IsSupported || file is null) return null;
        if (!maxSize.HasValue) return await LoadFullAsync(file);
        return await LoadScaledAsync(file, maxSize.Value);
    }

    private async Task<DecodedImage?> LoadFullAsync(IFileRef file)
    {
        if (!IsSupported || file is null) return null;

        try
        {
            using var baseStream = await file.OpenReadAsync();
            using var stream = await EnsureSeekableAsync(baseStream);

            stream.Position = 0;
            var options = new BitmapFactory.Options
            {
                InPreferredConfig = Bitmap.Config.Argb8888,
                InDither = true
            };

            using var androidBmp = BitmapFactory.DecodeStream(stream, null, options);
            if (androidBmp == null)
            {
                Console.WriteLine("AndroidHeifDecoder: Decode full image returned null.");
                return null;
            }

            return ToDecodedImage(androidBmp);
        }
        catch (Exception ex)
        {
            Console.WriteLine($"AndroidHeifDecoder failed (LoadBitmapAsync): {ex.Message}");
            return null;
        }
    }

    private async Task<DecodedImage?> LoadScaledAsync(IFileRef file, int maxSize)
    {
        if (maxSize <= 0) return null;

        try
        {
            using var baseStream = await file.OpenReadAsync();
            using var stream = await EnsureSeekableAsync(baseStream);

            // 1) 尝试内嵌缩略图(仅在够大时;小了交给主图采样,避免放大糊图)
            Bitmap? embedded = null;
            try
            {
                stream.Position = 0;
                embedded = TryGetEmbeddedThumbnail(stream);
            }
            catch (Exception ex)
            {
                Console.WriteLine($"AndroidHeifDecoder: Exif thumbnail failed: {ex.Message}");
            }

            if (embedded != null)
            {
                try
                {
                    var lw = Math.Max(embedded.Width, embedded.Height);
                    if (lw >= maxSize)
                    {
                        using var chosen = (lw > maxSize)
                            ? ScaleBitmap(embedded, maxSize) // 控制输出尺寸不浪费内存
                            : embedded.Copy(Bitmap.Config.Argb8888, false);

                        if (!ReferenceEquals(chosen, embedded))
                            embedded.Dispose();

                        return ToDecodedImage(chosen);
                    }
                    embedded.Dispose();
                }
                catch (Exception ex)
                {
                    // 继续走主图缩放方案
                    Console.WriteLine($"AndroidHeifDecoder: Using embedded thumbnail failed: {ex.Message}");
                    embedded?.Dispose();
                }
            }

            // 2) 内嵌缩略图不可用或不合适，按 maxSize 解码主图
            stream.Position = 0;
            var bounds = new BitmapFactory.Options { InJustDecodeBounds = true };
            BitmapFactory.DecodeStream(stream, null, bounds);
            if (bounds.OutWidth <= 0 || bounds.OutHeight <= 0)
            {
                Console.WriteLine("AndroidHeifDecoder: Failed to read image bounds.");
                return null;
            }

            var sample = ComputeInSampleSize(bounds.OutWidth, bounds.OutHeight, maxSize);
            stream.Position = 0;

            var decode = new BitmapFactory.Options
            {
                InPreferredConfig = Bitmap.Config.Argb8888,
                InDither = true,
                InSampleSize = sample
            };

            using var decoded = BitmapFactory.DecodeStream(stream, null, decode);
            if (decoded == null)
            {
                Console.WriteLine("AndroidHeifDecoder: Decode sampled image returned null.");
                return null;
            }

            using var scaled = ScaleBitmap(decoded, maxSize);
            return ToDecodedImage(scaled);
        }
        catch (Exception ex)
        {
            Console.WriteLine($"AndroidHeifDecoder failed (LoadThumbnailAsync): {ex.Message}");
            return null;
        }
    }

    // ---- helpers ----

    private static async Task<Stream> EnsureSeekableAsync(Stream s)
    {
        if (s.CanSeek) { s.Position = 0; return s; }
        var ms = new MemoryStream();
        await s.CopyToAsync(ms).ConfigureAwait(false);
        ms.Position = 0;
        return ms;
    }

    private static int ComputeInSampleSize(int w, int h, int maxSize)
    {
        var longSide = Math.Max(w, h);
        if (longSide <= 0) return 1;
        var sample = longSide / Math.Max(1, maxSize);
        return Math.Max(1, sample);
    }

    private static Bitmap ScaleBitmap(Bitmap src, int maxSize)
    {
        var w = src.Width;
        var h = src.Height;
        var longSide = Math.Max(w, h);
        if (longSide <= maxSize) return src.Copy(Bitmap.Config.Argb8888, false);

        double scale = (double)maxSize / longSide;
        int tw = Math.Max(1, (int)Math.Round(w * scale));
        int th = Math.Max(1, (int)Math.Round(h * scale));
        var dst = Bitmap.CreateScaledBitmap(src, tw, th, true);
        return dst;
    }

    private static DecodedImage? ToDecodedImage(Bitmap androidBmp)
    {
        try
        {
            var w = androidBmp.Width;
            var h = androidBmp.Height;
            if (w <= 0 || h <= 0) return null;

            var pixels = new int[w * h];
            using (var ib = IntBuffer.Allocate(pixels.Length))
            {
                androidBmp.CopyPixelsToBuffer(ib);
                ib.Rewind();
                ib.Get(pixels);
            }

            // 字节布局沿用 Android 位图通道顺序的既有口径，不做改动；缓冲池租用
            var bgra = DecodedImage.RentPixels(pixels.Length * 4);
            for (int i = 0, j = 0; i < pixels.Length; i++, j += 4)
            {
                int p = pixels[i];
                byte a = (byte)((p >> 24) & 0xFF);
                byte b = (byte)((p >> 16) & 0xFF);
                byte g = (byte)((p >> 8) & 0xFF);
                byte r = (byte)(p & 0xFF);

                bgra[j + 0] = b;
                bgra[j + 1] = g;
                bgra[j + 2] = r;
                bgra[j + 3] = a;
            }

            return DecodedImage.CreatePooled(bgra, w, h, w * 4, PixelEncoding.Bgra32);
        }
        catch (Exception ex)
        {
            Console.WriteLine($"AndroidHeifDecoder: Convert to DecodedImage failed: {ex.Message}");
            return null;
        }
        finally
        {
            androidBmp.Dispose();
        }
    }

    // 尝试通过 ExifInterface 获取内嵌缩略图（反射，避免强依赖 AndroidX 包）
    private static Bitmap? TryGetEmbeddedThumbnail(Stream streamSeekable)
    {
        try
        {
            // 将可寻址流复制为字节数组，再用 Java.IO.ByteArrayInputStream 包装
            using var ms = new MemoryStream();
            if (streamSeekable.CanSeek) streamSeekable.Position = 0;
            streamSeekable.CopyTo(ms);
            var data = ms.ToArray();
            using var jis = new Java.IO.ByteArrayInputStream(data);

            // 反射加载两种可能的类型（AndroidX 或旧 Support）
            var t = Type.GetType("AndroidX.ExifInterface.Media.ExifInterface, Xamarin.AndroidX.ExifInterface")
                    ?? Type.GetType("Android.Support.ExifInterface.ExifInterface, Xamarin.Android.Support");
            if (t == null) return null;

            // 优先使用 InputStream 构造函数
            var ctor = t.GetConstructor(new[] { typeof(Java.IO.InputStream) });
            if (ctor == null) return null;

            using var exif = (Java.Lang.Object)ctor.Invoke(new object[] { jis });

            // 1) 方法 GetThumbnailBitmap()
            Bitmap? bmp = null;
            var mGetThumbBitmap = t.GetMethod("GetThumbnailBitmap", Type.EmptyTypes);
            if (mGetThumbBitmap != null)
            {
                var r = mGetThumbBitmap.Invoke(exif, Array.Empty<object>());
                if (r is Bitmap b1) bmp = b1;
                else if (r is Java.Lang.Object j1 && j1.Handle != IntPtr.Zero) bmp = j1.JavaCast<Bitmap>();
            }

            // 2) 属性 ThumbnailBitmap
            if (bmp == null)
            {
                var pThumbBitmap = t.GetProperty("ThumbnailBitmap");
                if (pThumbBitmap != null)
                {
                    var r = pThumbBitmap.GetValue(exif);
                    if (r is Bitmap b2) bmp = b2;
                    else if (r is Java.Lang.Object j2 && j2.Handle != IntPtr.Zero) bmp = j2.JavaCast<Bitmap>();
                }
            }

            // 3) 方法 GetThumbnail() => byte[]，再手动 Decode
            if (bmp == null)
            {
                var mGetThumbBytes = t.GetMethod("GetThumbnail", Type.EmptyTypes);
                if (mGetThumbBytes != null)
                {
                    var r = mGetThumbBytes.Invoke(exif, Array.Empty<object>());
                    if (r is byte[] bytes && bytes.Length > 0)
                    {
                        bmp = BitmapFactory.DecodeByteArray(bytes, 0, bytes.Length);
                    }
                }
            }

            return bmp;
        }
        catch
        {
            // 上层记录日志
            return null;
        }
    }
}