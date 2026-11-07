using System;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;
using Android.OS;
using PhotoViewer.Core.Color;
using PhotoViewer.Core.Image;

namespace PhotoViewer.Android.Native;

internal static partial class AndroidNativeChecks
{
    private static async Task RunPerformanceChecksAsync()
    {
        await WaitForScreenAsync();
        await EnsureGesturePhotoAsync();
        await RunStepAsync("后台色彩准备", CheckBackgroundPresentationAsync);
        await RunStepAsync("纹理字节预算", CheckTextureByteBudgetAsync);
        await RunStepAsync("绘制合帧", CheckFrameCoalescingAsync);
        await RunBackgroundPrefetchChecksAsync();
        await RunRawReadLeaseChecksAsync(_activity!);
        await RunStepAsync("RAW 实际会话物理100%", CheckRawFullResolutionAsync);
    }

    private static async Task CheckBackgroundPresentationAsync()
    {
        var photo = _root!.Photo;
        int generation = int.MinValue + 10000;
        bool surfaceP3 = photo.SurfaceIsP3;
        using var source = await Task.Run(() => CreateZoomPattern(6000, 4000, 6000, 4000, 1));
        using var p3 = await Task.Run(() => ColorEngine.ToDisplayEncoding(source, PixelEncoding.Rgba8, ImageColorSpace.DisplayP3));
        try
        {
            photo.SetImageForChecks(null, generation);
            photo.RecreateSurfaceForChecks(false);
            long synchronousStart = Stopwatch.GetTimestamp();
            photo.SetImageForChecks(p3, ++generation);
            double synchronousMs = Stopwatch.GetElapsedTime(synchronousStart).TotalMilliseconds;
            var synchronous = AndroidUploadStats.LatestForChecks;
            photo.SetImageForChecks(null, ++generation);
            photo.RecreateSurfaceForChecks(false);
            await Task.Delay(100);

            bool ticking = true;
            double maxGap = 0;
            long previous = Stopwatch.GetTimestamp();
            var handler = new Handler(Looper.MainLooper!);
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
            int mainThread = System.Environment.CurrentManagedThreadId;
            try
            {
                long start = Stopwatch.GetTimestamp();
                photo.PresentImage(p3, ++generation);
                double enqueueMs = Stopwatch.GetElapsedTime(start).TotalMilliseconds;
                await WaitAsync(() => !photo.PreparationPending && ReferenceEquals(photo.PresentedImage, p3), "后台转换上屏", 20000);
                await Task.Delay(80);
                var uploaded = AndroidUploadStats.LatestForChecks;
                var pixels = PhotoRendererForChecks(photo).CheckTexturePixelsForChecks(p3);
                Check("后台准备 与同步路径GPU像素一致", pixels.Matches, pixels.Details);
                Check("后台准备 色域转换不在主线程", photo.LastPreparationThread != mainThread && photo.LastPreparationMs > 0,
                    $"main={mainThread} worker={photo.LastPreparationThread} prepareMs={photo.LastPreparationMs:F2}");
                Check("后台准备 UI提交不再包含整图转换", uploaded is { Converted: false, ConvertMs: 0, CacheHit: false }, DescribeUpload(uploaded));
                Check("后台准备 入队无整图同步等待", enqueueMs < 50,
                    $"enqueueMs={enqueueMs:F2} synchronousMs={synchronousMs:F2} syncConvert={synchronous?.ConvertMs:F2} prepareMs={photo.LastPreparationMs:F2}");
                Check("后台准备 UI心跳无250ms阻塞", maxGap < 250, $"gapMs={maxGap:F2} submitMs={uploaded?.SubmitMs:F2}");
            }
            finally { ticking = false; }

            using var replacement = CreateColorQuadImage(ImageColorSpace.Srgb)!;
            photo.SetImageForChecks(null, ++generation);
            photo.RecreateSurfaceForChecks(false);
            photo.PresentImage(p3, ++generation);
            photo.PresentImage(replacement, ++generation);
            await WaitAsync(() => !photo.PreparationPending, "旧切图转换实际退出");
            Check("后台准备 旧切图结果不覆盖新图", ReferenceEquals(photo.PresentedImage, replacement)
                && PhotoRendererForChecks(photo).CheckTexturePixelsForChecks(replacement).Matches,
                $"pending={photo.PreparationPending} presented={photo.PresentedImage?.Width}");

            photo.PresentImage(p3, ++generation);
            photo.RecreateSurfaceForChecks(true);
            await WaitAsync(() => !photo.PreparationPending, "重建后旧准备取消");
            photo.PresentImage(p3, ++generation);
            await WaitAsync(() => !photo.PreparationPending && ReferenceEquals(photo.PresentedImage, p3), "重建后按真实表面呈现");
            Check("后台准备 表面重建不复用旧色域产物", PhotoRendererForChecks(photo).CheckTexturePixelsForChecks(p3).Matches,
                $"p3={photo.SurfaceIsP3} error={photo.PreparationError}");

            var leased = CreateColorQuadImage(ImageColorSpace.DisplayP3)!;
            photo.SetImageForChecks(null, ++generation);
            photo.RecreateSurfaceForChecks(false);
            photo.PresentImage(leased, ++generation);
            leased.Release();
            await WaitAsync(() => !photo.PreparationPending && ReferenceEquals(photo.PresentedImage, leased), "调用者归还后仍安全上屏");
            Check("后台准备 独立持有源租约", leased.Pixels.Length > 0
                && PhotoRendererForChecks(photo).CheckTexturePixelsForChecks(leased).Matches, $"bytes={leased.ByteCount}");
            photo.SetImageForChecks(null, ++generation);
            Check("后台准备 清空归还全部展示租约", leased.Pixels.Length == 0, $"bytes={leased.ByteCount}");
        }
        finally
        {
            photo.SetImageForChecks(null, ++generation);
            photo.RecreateSurfaceForChecks(surfaceP3);
            photo.SetImageForChecks(_session!.Image, _session.Generation);
        }
    }

    private static Task CheckTextureByteBudgetAsync()
    {
        var photo = _root!.Photo;
        var renderer = PhotoRendererForChecks(photo);
        long previousLimit = renderer.RecentTextureByteLimit;
        int generation = int.MinValue + 11000;
        using var first = CreateSolidColorImage(512, 512, 0);
        using var second = CreateSolidColorImage(512, 512, 1);
        using var third = CreateSolidColorImage(512, 512, 2);
        try
        {
            renderer.TrimCachedTextures();
            renderer.RecentTextureByteLimit = 2L << 20;
            photo.SetImageForChecks(first, generation++);
            photo.SetImageForChecks(second, generation++);
            photo.SetImageForChecks(third, generation++);
            Check("纹理预算 小预算只保留可容纳的最近帧", renderer.RecentTextureCountForChecks == 1,
                $"recent={renderer.RecentTextureCountForChecks} bytes={renderer.TextureBytes} cap={renderer.RecentTextureByteLimit}");
            Check("纹理预算 当前图像素不降级", renderer.CheckTexturePixelsForChecks(third).Matches, $"texture={renderer.TextureWidth}x{renderer.TextureHeight}");
            photo.SetImageForChecks(second, generation++);
            Check("纹理预算 最近对比仍命中", AndroidUploadStats.LatestForChecks is { CacheHit: true }, DescribeUpload(AndroidUploadStats.LatestForChecks));
            photo.SetImageForChecks(first, generation++);
            Check("纹理预算 被淘汰旧帧重新上传且像素正确", AndroidUploadStats.LatestForChecks is { CacheHit: false }
                && renderer.CheckTexturePixelsForChecks(first).Matches, DescribeUpload(AndroidUploadStats.LatestForChecks));
            renderer.TrimCachedTextures();
            Check("纹理预算 压力修剪保留当前", renderer.RecentTextureCountForChecks == 0 && renderer.CheckTexturePixelsForChecks(first).Matches,
                $"recent={renderer.RecentTextureCountForChecks}");
        }
        finally
        {
            renderer.RecentTextureByteLimit = previousLimit;
            photo.SetImageForChecks(_session!.Image, _session.Generation);
        }
        return Task.CompletedTask;
    }

    private static async Task CheckFrameCoalescingAsync()
    {
        var photo = _root!.Photo;
        photo.SetImageForChecks(_session!.Image, _session.Generation);
        await Task.Delay(100);
        int frames = photo.RenderedFrames;
        for (int index = 0; index < 20; index++) photo.FitToView();
        int immediate = photo.RenderedFrames - frames;
        await Task.Delay(100);
        int rendered = photo.RenderedFrames - frames;
        Check("绘制合帧 同一轮20次更新不立即交换", immediate == 0, $"immediate={immediate}");
        Check("绘制合帧 下一帧仅绘制一次", rendered == 1, $"frames={rendered}");
    }
}
