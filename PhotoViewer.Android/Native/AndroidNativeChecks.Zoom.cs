using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using Android.OS;
using PhotoViewer.Android.Core;
using PhotoViewer.Core.Color;
using PhotoViewer.Core.Image;
using IOPath = System.IO.Path;

namespace PhotoViewer.Android.Native;

internal static partial class AndroidNativeChecks
{
    private static async Task RunZoomChecksAsync()
    {
        await WaitForScreenAsync();
        await EnsureGesturePhotoAsync();
        await ZoomForGestureAsync("探针宿主 Full 就绪");
        await Task.Delay(300);
        for (int round = 1; round <= 2; round++)
        {
            int current = round;
            await RunStepAsync($"26MP 冷纹理 {round}", async () =>
            {
                using var full = await Task.Run(() => CreateZoomPattern(6192, 4128, 6192, 4128, current));
                using var proxy = await Task.Run(() => CreateZoomPattern(2496, 1664, 6192, 4128, current));
                await CheckZoomUploadAsync($"pattern-{current}", proxy, full, current);
            });
        }
        await RunStepAsync("行距填充纹理", () =>
        {
            using var image = CreateZoomPattern(1024, 1500, 1024, 1500, 3, rowPadding: 16);
            var photo = _root!.Photo;
            var renderer = (AndroidPhotoRenderer)typeof(AndroidPhotoView)
                .GetField("_renderer", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(photo)!;
            AndroidUploadStats.Reset();
            photo.SetImage(image, int.MinValue + 50);
            try
            {
                var pixels = renderer.CheckTexturePixelsForChecks(image);
                Check("带行距填充纹理逐行上传像素一致", pixels.Matches, pixels.Details);
                var copied = AndroidUploadStats.LatestForChecks;
                Check("行距填充不搬运额外尾部字节", copied is { CopyBytes: 6144000, Chunks: 2 },
                    $"copied={copied?.CopyBytes} chunks={copied?.Chunks} stride={image.Stride} array={image.ByteCount}");
            }
            finally { photo.SetImage(null, int.MinValue + 51); }
            return Task.CompletedTask;
        });
        await RunStepAsync("26MP HIF", async () =>
        {
            string[] folders =
            [
                IOPath.Combine(_activity!.FilesDir!.AbsolutePath, "pv-check-zoom"),
                IOPath.Combine(_activity.GetExternalFilesDir(null)!.AbsolutePath, "pv-check-zoom"),
            ];
            string? path = folders.Where(Directory.Exists)
                .SelectMany(folder => Directory.EnumerateFiles(folder))
                .FirstOrDefault(file => file.EndsWith(".hif", StringComparison.OrdinalIgnoreCase));
            if (path == null) throw new FileNotFoundException("需要将 26MP HIF 副本放在 files/pv-check-zoom 或 external/pv-check-zoom");
            using var ticket = DecodeContext.Enter(DecodePriority.P0_Current);
            var decoder = new AndroidPresentationDecoder();
            var file = new AndroidFileRef(path);
            var watch = Stopwatch.StartNew();
            using var proxy = await decoder.DecodeAsync(file, 2496, PixelEncoding.Rgba8, CancellationToken.None)
                ?? throw new InvalidOperationException("HIF proxy=null");
            double proxyMs = watch.Elapsed.TotalMilliseconds;
            watch.Restart();
            using var full = await decoder.DecodeAsync(file, null, PixelEncoding.Rgba8, CancellationToken.None)
                ?? throw new InvalidOperationException("HIF full=null");
            double fullMs = watch.Elapsed.TotalMilliseconds;
            Check("HIF 26MP 全分辨率解码", full.Width == full.SourceWidth && full.Height == full.SourceHeight
                && full.Width >= 6000 && full.Height >= 4000,
                $"{file.Name} proxy={proxy.Width}x{proxy.Height} full={full.Width}x{full.Height}"
                + $" source={full.SourceWidth}x{full.SourceHeight} orientation={full.Orientation}"
                + $" encoding={full.Encoding} color={full.ColorSpace} decodeMs={proxyMs:F1}/{fullMs:F1}");
            await CheckZoomUploadAsync("HIF", proxy, full, 3);
            await CheckLiveHifZoomAsync(path);
        });
    }

    private static async Task CheckLiveHifZoomAsync(string path)
    {
        var photo = _root!.Photo;
        var main = _main!;
        var session = _session!;
        photo.SetImage(session.Image, session.Generation);
        photo.FitToView();
        await main.FolderVM.OpenFolderAsync(new AndroidFolderRef(IOPath.GetDirectoryName(path)!));
        await WaitAsync(() => main.CurrentFile?.Name == IOPath.GetFileName(path)
            && session.Image is { SourceWidth: >= 6000 }, "真实 HIF 会话代理上屏", 20000);
        bool startedProxy = session.IsProxy;
        AndroidUploadStats.Reset();
        var handler = new Handler(Looper.MainLooper!);
        bool ticking = true;
        long previous = Stopwatch.GetTimestamp();
        double maxGap = 0;
        Action tick = null!;
        tick = () =>
        {
            if (!ticking) return;
            long now = Stopwatch.GetTimestamp();
            maxGap = Math.Max(maxGap, Stopwatch.GetElapsedTime(previous, now).TotalMilliseconds);
            previous = now;
            handler.PostDelayed(tick, 16);
        };
        handler.PostDelayed(tick, 16);
        var clock = Stopwatch.StartNew();
        try
        {
            var (consumed, _) = await DoubleTapAsync(photo.Width / 2f, photo.Height / 2f);
            await WaitAsync(() => !session.IsProxy && Math.Abs(photo.PhysicalScale - 1) < .001,
                "真实 HIF 双击与 Full 完成", 20000);
            await Task.Delay(80);
            var upload = AndroidUploadStats.LatestForChecks;
            Check("HIF 实际加载会话双击从代理到Full", consumed && startedProxy
                && session.Image is { Width: >= 6000, Height: >= 4000 } && photo.Viewport.TextureWidth == session.Image.Width,
                $"startedProxy={startedProxy} proxy={session.IsProxy} physical={photo.PhysicalScale:P2}"
                + $" image={session.Image?.Width}x{session.Image?.Height} elapsedMs={clock.Elapsed.TotalMilliseconds:F1}");
            Check("HIF 实际会话UI心跳无250ms阻塞", maxGap < 250,
                $"maxGapMs={maxGap:F1} uploadMs={upload?.TotalMs:F1} copyMs={upload?.CopyMs:F1}");
        }
        finally
        {
            ticking = false;
            photo.FitToView();
        }
    }

    private static DecodedImage CreateZoomPattern(int width, int height, int sourceWidth, int sourceHeight, int seed, int rowPadding = 0)
    {
        int stride = checked(width * 4 + rowPadding);
        var pixels = DecodedImage.RentPixels(checked(stride * height));
        for (int y = 0; y < height; y++)
        for (int x = 0; x < width; x++)
        {
            int offset = y * stride + x * 4;
            pixels[offset] = (byte)(x * 13 + y * 7 + seed);
            pixels[offset + 1] = (byte)(x * 3 + y * 11 + seed);
            pixels[offset + 2] = (byte)(x * 17 + y * 5 + seed);
            pixels[offset + 3] = 255;
        }
        return DecodedImage.CreatePooled(pixels, width, height, stride, PixelEncoding.Rgba8,
            ImageColorSpace.Srgb, sourceWidth, sourceHeight, orientation: seed == 2 ? 6 : 1);
    }

    private static async Task CheckZoomUploadAsync(string label, DecodedImage proxy, DecodedImage full, int generation)
    {
        var photo = _root!.Photo;
        var renderer = (AndroidPhotoRenderer)typeof(AndroidPhotoView)
            .GetField("_renderer", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(photo)!;
        int imageGeneration = int.MinValue + generation;
        photo.SetImage(proxy, imageGeneration);
        photo.FitToView();
        Check($"{label} 代理上屏", photo.HasImage && photo.Viewport.TextureWidth == proxy.Width,
            $"texture={photo.Viewport.TextureWidth} proxy={proxy.Width} fit={photo.PhysicalScale:P2}");
        await Task.Delay(400);
        AndroidUploadStats.Reset();
        bool upgraded = false;
        double threshold = (double)proxy.Width / full.SourceWidth;
        double upgradeScale = 0, uploadMs = 0, gapMs = 0;
        AndroidUploadStats.Entry? upload = null;
        long lastFrame = 0;
        void Frame()
        {
            long now = Stopwatch.GetTimestamp();
            if (lastFrame != 0)
                gapMs = Math.Max(gapMs, Stopwatch.GetElapsedTime(lastFrame, now).TotalMilliseconds);
            lastFrame = now;
            if (upgraded || !photo.NeedsFull) return;
            upgraded = true;
            upgradeScale = photo.PhysicalScale;
            var clock = Stopwatch.StartNew();
            photo.SetImage(full, imageGeneration);
            uploadMs = clock.Elapsed.TotalMilliseconds;
            upload = AndroidUploadStats.LatestForChecks;
        }
        photo.ViewportChanged += Frame;
        try
        {
            var (consumed, immediate) = await DoubleTapAsync(photo.Width / 2f, photo.Height / 2f);
            await Task.Delay(500);
            Check($"{label} 双击达到物理100%且升Full", consumed && upgraded
                && Math.Abs(photo.PhysicalScale - 1) < .001 && photo.Viewport.TextureWidth == full.Width,
                $"scale={photo.PhysicalScale:P2} immediate={immediate:P2} trigger={upgradeScale:P2}"
                + $" coverage={threshold:P2} texture={photo.Viewport.TextureWidth}x{full.Height}");
            Check($"{label} Full上传无秒级主线程停顿", upload.HasValue && uploadMs < 250,
                $"uploadMs={uploadMs:F1} create={upload?.CreateMs:F1} alloc={upload?.AllocMs:F1}"
                + $" copyUpload={upload?.UpdateMs:F1} copy={upload?.CopyMs:F1} mips={upload?.MipsMs:F1} arrayBytes={full.ByteCount}");
            Check($"{label} 紧凑像素缓冲零中间拷贝", upload is { CopyBytes: 0, Chunks: 1 },
                $"copied={upload?.CopyBytes} calls={upload?.Chunks} payload={(long)full.Width * full.Height * 4}");
            Check($"{label} 动画无250ms以上帧间断", gapMs < 250,
                $"maxGapMs={gapMs:F1} uploadMs={uploadMs:F1}");
            var pixels = renderer.CheckTexturePixelsForChecks(full);
            Check($"{label} GPU像素含分块边界逐字节一致", pixels.Matches, pixels.Details);
            AndroidUploadStats.Reset();
            photo.SetImage(proxy, imageGeneration);
            photo.SetImage(full, imageGeneration);
            Check($"{label} 代理Full回切命中纹理缓存", AndroidUploadStats.LatestForChecks is { TotalMs: 0 },
                AndroidUploadStats.Summary());
        }
        finally
        {
            photo.ViewportChanged -= Frame;
            photo.SetImage(null, imageGeneration + 10);
        }
    }
}
