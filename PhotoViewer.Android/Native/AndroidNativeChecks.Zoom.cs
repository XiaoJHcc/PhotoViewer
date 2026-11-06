using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using Android.OS;
using Android.Views;
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
        await RunStepAsync("同尺寸上传与空纹理复用", CheckReusedTextureAsync);
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
        await RunStepAsync("主图顶点缓冲复用", CheckVertexBufferReuseAsync);
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

    /// <summary>同尺寸不同实例连续上传：核对空纹理复用命中点、尺寸不符与 EGL 重建，并按 GPU 回读核对像素。</summary>
    private static async Task CheckReusedTextureAsync()
    {
        var photo = _root!.Photo;
        var session = _session!;
        await WaitAsync(() => photo.SurfaceReadyForChecks, "主图 EGL 表面就绪", 20000);
        var renderer = PhotoRendererForChecks(photo);
        const int size = 256;
        bool surfaceP3 = photo.SurfaceIsP3;
        var images = new DecodedImage?[6];
        var entries = new AndroidUploadStats.Entry?[6];
        int generation = int.MinValue + 320;
        try
        {
            photo.SetImage(null, ++generation);
            photo.RecreateSurfaceForChecks(surfaceP3);
            renderer = PhotoRendererForChecks(photo);

            bool spareBeforeFourth = false;
            AndroidUploadStats.Reset();
            for (int index = 0; index < 4; index++)
            {
                if (index == 3) spareBeforeFourth = renderer.HasSpareTextureForChecks;
                images[index] = CreateSolidColorImage(size, size, index);
                photo.SetImage(images[index], ++generation);
                entries[index] = AndroidUploadStats.LatestForChecks;
            }
            bool freshFirstFour = entries[0] is { ReusedTexture: false, CacheHit: false }
                && entries[1] is { ReusedTexture: false, CacheHit: false }
                && entries[2] is { ReusedTexture: false, CacheHit: false }
                && entries[3] is { ReusedTexture: false, CacheHit: false };
            Check("上传复用 前四次同尺寸均新建纹理", freshFirstFour && !spareBeforeFourth
                && renderer.HasSpareTextureForChecks && renderer.RecentTextureCountForChecks == 2,
                $"spareBeforeFourth={spareBeforeFourth} spare={renderer.HasSpareTextureForChecks}"
                + $" recent={renderer.RecentTextureCountForChecks} 第四张={DescribeUpload(entries[3])}");

            images[4] = CreateSolidColorImage(320, 240, 4);
            photo.SetImage(images[4], ++generation);
            entries[4] = AndroidUploadStats.LatestForChecks;
            Check("上传复用 尺寸变化不命中空纹理",
                entries[4] is { ReusedTexture: false, CacheHit: false } && renderer.HasSpareTextureForChecks,
                DescribeUpload(entries[4]));

            images[5] = CreateSolidColorImage(size, size, 5);
            photo.SetImage(images[5], ++generation);
            entries[5] = AndroidUploadStats.LatestForChecks;
            var pixels = renderer.CheckTexturePixelsForChecks(images[5]!);
            Check("上传复用 第五张同尺寸取到空纹理",
                entries[5] is { ReusedTexture: true, CacheHit: false, Converted: false, CreateMs: 0 },
                DescribeUpload(entries[5]));
            Check("上传复用 复用纹理装载新一帧像素",
                pixels.Matches && renderer.TextureWidth == size && renderer.TextureHeight == size,
                $"{pixels.Details} texture={renderer.TextureWidth}x{renderer.TextureHeight}");

            AndroidUploadStats.Reset();
            photo.RecreateSurfaceForChecks(surfaceP3);
            var rebuilt = AndroidUploadStats.LatestForChecks;
            Check("上传复用 EGL 重建后不误报命中",
                rebuilt is { ReusedTexture: false, CacheHit: false, Converted: false },
                DescribeUpload(rebuilt));
        }
        finally
        {
            photo.SetImage(null, generation + 10);
            foreach (var image in images) image?.Dispose();
            photo.RecreateSurfaceForChecks(surfaceP3);
            photo.SetImage(session.Image, session.Generation);
            photo.FitToView();
        }
    }

    /// <summary>纯色合成图（RGBA8，标签取当前表面色域）。</summary>
    private static DecodedImage CreateSolidColorImage(int width, int height, int index)
    {
        int stride = checked(width * 4);
        var pixels = DecodedImage.RentPixels(checked(stride * height));
        byte red = (byte)(40 + index * 35), green = (byte)(200 - index * 25), blue = (byte)(80 + index * 20);
        for (int offset = 0; offset < pixels.Length; offset += 4)
        {
            pixels[offset] = red;
            pixels[offset + 1] = green;
            pixels[offset + 2] = blue;
            pixels[offset + 3] = 255;
        }
        var colorSpace = _root!.Photo.SurfaceIsP3 ? ImageColorSpace.DisplayP3 : ImageColorSpace.Srgb;
        return DecodedImage.CreatePooled(pixels, width, height, stride, PixelEncoding.Rgba8,
            colorSpace, width, height);
    }

    private static async Task CheckVertexBufferReuseAsync()
    {
        const int generation = int.MinValue + 200;
        var photo = _root!.Photo;
        var session = _session!;
        await WaitAsync(() => photo.SurfaceReadyForChecks, "主图 EGL 表面就绪", 20000);
        var renderer = PhotoRendererForChecks(photo);
        bool surfaceP3 = photo.SurfaceIsP3;
        var images = new DecodedImage?[8];
        bool shared = true, uvMatched = true, quadValid = true, rebuilt = true;
        int allocations = -1;
        object? buffer = null, view = null;
        string drawn = string.Empty;
        try
        {
            for (int orientation = 1; orientation <= 8; orientation++)
            {
                var image = CreateOrientationPattern(orientation, 640, 480);
                images[orientation - 1] = image;
                photo.SetImage(image, generation + orientation);
                photo.FitToView();
                photo.Draw();
                var state = renderer.VertexStateForChecks();
                if (buffer == null)
                {
                    buffer = state.Buffer;
                    view = state.Floats;
                    allocations = state.Allocations;
                }
                shared &= buffer != null && ReferenceEquals(buffer, state.Buffer)
                    && ReferenceEquals(view, state.Floats) && state.Allocations == allocations;
                uvMatched &= MatchesOrientationUvs(state.Values, orientation);
                quadValid &= IsCenteredQuad(state.Values);
                drawn = $"orientation={orientation} allocations={state.Allocations} floats={state.Values.Length}";
            }
            Check("主图顶点缓冲 8 方向绘制零重分配", shared && allocations == 1 && uvMatched && quadValid,
                $"allocations={allocations} sameBuffer={shared} uv={uvMatched} quad={quadValid} {drawn}");

            bool pixelMatched = true;
            string pixels = string.Empty;
            foreach (var (u, v) in PixelSamplePoints)
            {
                var (sourceU, sourceV) = DisplayToSourceUv(8, (float)u, (float)v);
                var expected = PatternQuadrant(sourceU, sourceV);
                var actual = photo.ReadPixelForChecks(u, v);
                bool matched = Math.Abs(actual.R - expected.R) <= FixtureTolerance
                    && Math.Abs(actual.G - expected.G) <= FixtureTolerance
                    && Math.Abs(actual.B - expected.B) <= FixtureTolerance;
                pixelMatched &= matched;
                pixels += $" ({u:0.##},{v:0.##})→({actual.R},{actual.G},{actual.B})"
                    + $" 期望({expected.R},{expected.G},{expected.B})";
            }
            Check("主图顶点缓冲 方向8像素与源象限一致", pixelMatched, pixels.Trim());

            photo.FitToView();
            photo.Draw();
            var fitState = renderer.VertexStateForChecks();
            if (fitState.Values.Length != 16)
                throw new InvalidOperationException($"顶点缓冲读取失败：floats={fitState.Values.Length}");
            var fit = fitState.Values;
            double fitWidth = fit[4] - fit[0];
            double fitCenterX = (fit[0] + fit[4]) / 2.0;
            double fitCenterY = (fit[1] + fit[13]) / 2.0;
            photo.Viewport.ApplyGestureView(photo.Viewport.FitScale * 2, 40, -30);
            photo.Draw();
            var zoomState = renderer.VertexStateForChecks();
            if (zoomState.Values.Length != 16)
                throw new InvalidOperationException($"顶点缓冲读取失败：floats={zoomState.Values.Length}");
            var zoomed = zoomState.Values;
            double zoomedWidth = zoomed[4] - zoomed[0];
            double zoomedCenterX = (zoomed[0] + zoomed[4]) / 2.0;
            double zoomedCenterY = (zoomed[1] + zoomed[13]) / 2.0;
            double panX = photo.Viewport.PanX, panY = photo.Viewport.PanY;
            bool scaled = fitWidth > 0 && Math.Abs(zoomedWidth / fitWidth - 2) < .02;
            bool panned = (Math.Abs(panX) > 1e-6 || Math.Abs(panY) > 1e-6)
                && (Math.Abs(panX) < 1e-6 || (zoomedCenterX - fitCenterX) * panX > 0)
                && (Math.Abs(panY) < 1e-6 || (zoomedCenterY - fitCenterY) * panY < 0);
            bool uvKept = SameUvs(zoomed, fit);
            shared &= ReferenceEquals(buffer, zoomState.Buffer) && ReferenceEquals(view, zoomState.Floats)
                && zoomState.Allocations == allocations;
            Check("主图顶点缓冲 缩放平移更新顶点", shared && scaled && panned && uvKept,
                $"allocations={zoomState.Allocations} scale={(fitWidth > 0 ? zoomedWidth / fitWidth : 0):0.###}"
                + $" pan=({panX:0.#},{panY:0.#})"
                + $" shiftCenter=({zoomedCenterX - fitCenterX:0.####},{zoomedCenterY - fitCenterY:0.####})"
                + $" uv={uvKept} sameBuffer={shared}");

            photo.FitToView();
            var surfaceView = (SurfaceView)typeof(AndroidPhotoView)
                .GetField("_surfaceView", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(photo)!;
            float[]? expectedValues = null;
            for (int round = 1; round <= 2; round++)
            {
                renderer.Create(surfaceView.Holder!, photo.Density);
                renderer.Resize(surfaceView.Width, surfaceView.Height);
                renderer.SetImage(images[7]);
                renderer.DrawFrame(photo.Viewport, photo.Density);
                var rebuildState = renderer.VertexStateForChecks();
                rebuilt &= rebuildState.Buffer != null && ReferenceEquals(buffer, rebuildState.Buffer)
                    && ReferenceEquals(view, rebuildState.Floats) && rebuildState.Allocations == allocations
                    && MatchesOrientationUvs(rebuildState.Values, 8) && IsCenteredQuad(rebuildState.Values);
                if (expectedValues == null) expectedValues = rebuildState.Values;
                else rebuilt &= SameValues(rebuildState.Values, expectedValues);
            }
            Check("主图顶点缓冲 表面重建后复用", rebuilt,
                $"allocations={renderer.VertexStateForChecks().Allocations} sameBuffer={rebuilt}"
                + $" rounds=2 floats={expectedValues?.Length ?? 0}");
        }
        finally
        {
            photo.FitToView();
            photo.SetImage(null, generation + 100);
            foreach (var image in images) image?.Dispose();
            try
            {
                photo.RecreateSurfaceForChecks(surfaceP3);
            }
            finally
            {
                photo.SetImage(session.Image, session.Generation);
            }
        }
    }

    private static AndroidPhotoRenderer PhotoRendererForChecks(AndroidPhotoView photo)
        => (AndroidPhotoRenderer)typeof(AndroidPhotoView)
            .GetField("_renderer", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(photo)!;

    /// <summary>方向 8 的取样点（显示朝向归一化坐标，避开象限边界）。</summary>
    private static readonly (double U, double V)[] PixelSamplePoints =
    [
        (0.22, 0.18),
        (0.78, 0.14),
        (0.16, 0.80),
        (0.84, 0.76),
    ];

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
        var colorSpace = _root!.Photo.SurfaceIsP3 ? ImageColorSpace.DisplayP3 : ImageColorSpace.Srgb;
        return DecodedImage.CreatePooled(pixels, width, height, stride, PixelEncoding.Rgba8,
            colorSpace, sourceWidth, sourceHeight, orientation: seed == 2 ? 6 : 1);
    }

    private static DecodedImage CreateOrientationPattern(int orientation, int width, int height)
    {
        int stride = checked(width * 4);
        var pixels = DecodedImage.RentPixels(checked(stride * height));
        for (int y = 0; y < height; y++)
        for (int x = 0; x < width; x++)
        {
            var (r, g, b) = PatternQuadrant((x + .5f) / width, (y + .5f) / height);
            int offset = y * stride + x * 4;
            pixels[offset] = r;
            pixels[offset + 1] = g;
            pixels[offset + 2] = b;
            pixels[offset + 3] = 255;
        }
        var colorSpace = _root!.Photo.SurfaceIsP3 ? ImageColorSpace.DisplayP3 : ImageColorSpace.Srgb;
        return DecodedImage.CreatePooled(pixels, width, height, stride, PixelEncoding.Rgba8,
            colorSpace, width, height, orientation: orientation);
    }

    private static (byte R, byte G, byte B) PatternQuadrant(float u, float v) => (u, v) switch
    {
        (< .5f, < .5f) => (200, 40, 40),
        (>= .5f, < .5f) => (40, 180, 60),
        (< .5f, _) => (48, 64, 200),
        (_, _) => (216, 196, 56),
    };

    private static (float U, float V) DisplayToSourceUv(int orientation, float u, float v)
    {
        var corners = OrientationCornerUvs(orientation);
        float topU = (1 - u) * corners[0].U + u * corners[1].U;
        float topV = (1 - u) * corners[0].V + u * corners[1].V;
        float bottomU = (1 - u) * corners[2].U + u * corners[3].U;
        float bottomV = (1 - u) * corners[2].V + u * corners[3].V;
        return (topU + v * (bottomU - topU), topV + v * (bottomV - topV));
    }

    /// <summary>EXIF 方向 1–8 四角（显示左上 / 右上 / 左下 / 右下）的源纹理 UV，按 EXIF 定义独立写出。</summary>
    private static (float U, float V)[] OrientationCornerUvs(int orientation) => orientation switch
    {
        2 => [(1f, 0f), (0f, 0f), (1f, 1f), (0f, 1f)],
        3 => [(1f, 1f), (0f, 1f), (1f, 0f), (0f, 0f)],
        4 => [(0f, 1f), (1f, 1f), (0f, 0f), (1f, 0f)],
        5 => [(0f, 0f), (0f, 1f), (1f, 0f), (1f, 1f)],
        6 => [(0f, 1f), (0f, 0f), (1f, 1f), (1f, 0f)],
        7 => [(1f, 1f), (1f, 0f), (0f, 1f), (0f, 0f)],
        8 => [(1f, 0f), (1f, 1f), (0f, 0f), (0f, 1f)],
        _ => [(0f, 0f), (1f, 0f), (0f, 1f), (1f, 1f)],
    };

    private static bool MatchesOrientationUvs(float[] values, int orientation)
    {
        if (values.Length != 16) return false;
        var expected = OrientationCornerUvs(orientation);
        for (int corner = 0; corner < 4; corner++)
            if (Math.Abs(values[corner * 4 + 2] - expected[corner].U) > 1e-6f
                || Math.Abs(values[corner * 4 + 3] - expected[corner].V) > 1e-6f) return false;
        return true;
    }

    private static bool IsCenteredQuad(float[] values)
    {
        if (values.Length != 16) return false;
        if (values[4] <= values[0] || values[1] <= values[9]) return false;
        if (values[4] != values[12] || values[0] != values[8]) return false;
        if (values[1] != values[5] || values[9] != values[13]) return false;
        return Math.Abs((values[0] + values[4]) / 2) < 1e-4f && Math.Abs((values[1] + values[13]) / 2) < 1e-4f;
    }

    private static bool SameUvs(float[] first, float[] second)
    {
        if (first.Length != 16 || second.Length != 16) return false;
        for (int corner = 0; corner < 4; corner++)
            if (Math.Abs(first[corner * 4 + 2] - second[corner * 4 + 2]) > 1e-6f
                || Math.Abs(first[corner * 4 + 3] - second[corner * 4 + 3]) > 1e-6f) return false;
        return true;
    }

    private static bool SameValues(float[] first, float[] second)
    {
        if (first.Length != 16 || second.Length != 16) return false;
        for (int index = 0; index < 16; index++)
            if (Math.Abs(first[index] - second[index]) > 1e-6f) return false;
        return true;
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
            Check($"{label} Full上传提交墙钟覆盖分段", upload is { } submitted && SubmitCoversSegments(submitted),
                DescribeUpload(upload));
            Check($"{label} Full上传同表面源零转换", upload is { Converted: false },
                DescribeUpload(upload));
            Check($"{label} 紧凑像素缓冲零中间拷贝", upload is { CopyBytes: 0, Chunks: 1 },
                $"copied={upload?.CopyBytes} calls={upload?.Chunks} payload={(long)full.Width * full.Height * 4}");
            Check($"{label} 动画无250ms以上帧间断", gapMs < 250,
                $"maxGapMs={gapMs:F1} uploadMs={uploadMs:F1}");
            var pixels = renderer.CheckTexturePixelsForChecks(full);
            Check($"{label} GPU像素含分块边界逐字节一致", pixels.Matches, pixels.Details);
            AndroidUploadStats.Reset();
            photo.SetImage(proxy, imageGeneration);
            photo.SetImage(full, imageGeneration);
            Check($"{label} 代理Full回切命中纹理缓存",
                AndroidUploadStats.LatestForChecks is { TotalMs: 0, CacheHit: true, SubmitMs: >= 0 },
                AndroidUploadStats.Summary());
        }
        finally
        {
            photo.ViewportChanged -= Frame;
            photo.SetImage(null, imageGeneration + 10);
        }
    }
}
