using System;
using System.Threading;
using System.Threading.Tasks;
using Android.Graphics;
using PhotoViewer.Android.Core;
using PhotoViewer.Core.Image;
using PhotoViewer.Core.Storage;
using AndroidColorSpace = Android.Graphics.ColorSpace;

namespace PhotoViewer.Android.Native;

/// <summary>
/// 缩放指示器（导航图）的缩略图解码（A3）：用 BitmapFactory 按目标长边子采样后转正 EXIF 方向，
/// 避免为一张 150 点指示图走 GPU 离屏回读；只读取文件流，不改动主图纹理与视口。
/// </summary>
internal static class AndroidNavigationDecoder
{
    /// <summary>解码导航图（显示朝向），失败返回 null。</summary>
    /// <param name="file">当前照片文件引用。</param>
    /// <param name="orientation">EXIF 方向。</param>
    /// <param name="longEdgePx">目标长边（物理像素）。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>位图，调用方负责释放。</returns>
    internal static Task<Bitmap?> DecodeAsync(IFileRef file, int orientation, int longEdgePx, CancellationToken cancellationToken)
        => Task.Run<Bitmap?>(() =>
        {
            try
            {
                var bounds = new BitmapFactory.Options { InJustDecodeBounds = true };
                using (var stream = file.OpenReadAsync().GetAwaiter().GetResult())
                    BitmapFactory.DecodeStream(stream, null, bounds);
                if (bounds.OutWidth <= 0 || bounds.OutHeight <= 0) return null;
                int sample = 1;
                while (Math.Max(bounds.OutWidth, bounds.OutHeight) / (sample * 2) >= longEdgePx) sample *= 2;
                var options = new BitmapFactory.Options
                {
                    InPreferredConfig = Bitmap.Config.Argb8888,
                    InSampleSize = sample,
                };
                if (OperatingSystem.IsAndroidVersionAtLeast(26))
                    options.InPreferredColorSpace = AndroidColorSpace.Get(
                        (AndroidDisplayCapabilities.SurfaceIsP3 ? AndroidColorSpace.Named.DisplayP3 : AndroidColorSpace.Named.Srgb)!)!;
                using var stream2 = file.OpenReadAsync().GetAwaiter().GetResult();
                using var decoded = BitmapFactory.DecodeStream(stream2, null, options);
                if (decoded == null) return null;
                cancellationToken.ThrowIfCancellationRequested();
                using var scaled = ScaleToLongEdge(decoded, longEdgePx);
                return RotateForDisplay(scaled ?? decoded, orientation);
            }
            catch (System.OperationCanceledException) { throw; }
            catch (Exception exception)
            {
                Console.WriteLine($"[Native] 导航图解码失败（{file.Name}）：{exception.Message}");
                return null;
            }
        }, cancellationToken);

    /// <summary>按目标长边等比缩小；无需缩放时返回 null。</summary>
    private static Bitmap? ScaleToLongEdge(Bitmap bitmap, int longEdge)
    {
        int current = Math.Max(bitmap.Width, bitmap.Height);
        if (current <= longEdge) return null;
        double ratio = (double)longEdge / current;
        int width = Math.Max(1, (int)Math.Round(bitmap.Width * ratio));
        int height = Math.Max(1, (int)Math.Round(bitmap.Height * ratio));
        return Bitmap.CreateScaledBitmap(bitmap, width, height, true);
    }

    /// <summary>按共享方向表（先镜像后顺时针旋转）把解码位图转成显示朝向。</summary>
    private static Bitmap RotateForDisplay(Bitmap source, int orientation)
    {
        var (rotation, mirror) = ImageOrientationMapping.ToDisplay(orientation);
        if (rotation == 0 && !mirror) return source.Copy(Bitmap.Config.Argb8888!, false) ?? source;
        int width = source.Width, height = source.Height;
        bool swap = rotation is 90 or 270;
        int targetWidth = swap ? height : width, targetHeight = swap ? width : height;
        var target = Bitmap.CreateBitmap(targetWidth, targetHeight, Bitmap.Config.Argb8888!);
        using var canvas = new Canvas(target);
        using var paint = new Paint(PaintFlags.FilterBitmap) { FilterBitmap = true };
        canvas.Save();
        // 先镜像（水平），再绕源图中心顺时针旋转，最后把结果平移到目标画布。
        var matrix = new Matrix();
        if (mirror) matrix.PreScale(-1, 1);
        matrix.PostRotate(rotation, 0, 0);
        float translateX = rotation switch
        {
            90 => targetWidth,
            180 => targetWidth,
            _ => 0,
        };
        float translateY = rotation switch
        {
            90 => 0,
            180 => targetHeight,
            270 => targetHeight,
            _ => 0,
        };
        if (mirror && rotation == 0) translateX += targetWidth;
        matrix.PostTranslate(translateX, translateY);
        canvas.DrawBitmap(source, matrix, paint);
        canvas.Restore();
        return target;
    }
}
