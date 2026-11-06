using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using PhotoViewer.Core.Color;
using PhotoViewer.Core.Image;
using PhotoViewer.Core.Storage;
using PhotoViewer.Windows.Core;
using SkiaSharp;

namespace PhotoViewer.Windows.Native;

internal sealed partial class WindowsMainWindow
{
    internal async Task RunColorChecksAsync()
    {
        var checks = new List<string>();
        Directory.CreateDirectory(SmokeOutputDirectory);
        string report = Path.Combine(SmokeOutputDirectory, "color-checks.json");
        File.WriteAllText(report, "[\"RUNNING\"]");
        try
        {
            await WaitForSmokeAsync(() => _photo.PhysicalViewport.Width > 1 && _displayColor?.IsReady == true, "显示器色彩探测");
            checks.Add("PASS actual display query: " + _displayColor!.Status);
            _photo.SetDisplayTransformForChecks(WindowsDisplayTransform.SystemManaged);
            var decoder = new WindowsPresentationDecoder();
            var directory = Path.Combine(SmokeOutputDirectory, "color-fixtures");
            Directory.CreateDirectory(directory);
            foreach (string name in new[] { "srgb", "p3", "adobe", "untagged-adobe" })
            {
                string original = Path.GetFullPath(Path.Combine("Fixtures", "jpeg", "color-" + name + ".jpg"));
                string folder = Path.Combine(directory, name);
                Directory.CreateDirectory(folder);
                string path = Path.Combine(folder, "color-" + name + ".jpg");
                File.Copy(original, path, true);
                var file = new LocalFileRef(path);
                foreach (int? edge in new int?[] { 600, null })
                {
                    using var image = await decoder.DecodeAsync(file, edge, PixelEncoding.RgbaF16, CancellationToken.None)
                        ?? throw new InvalidOperationException("JPEG 色卡解码失败: " + name);
                    var red = Pixel(image, 1.0 / 6, 0.175);
                    var expected = name switch
                    {
                        "p3" => (1.224745f, -0.042058f, -0.019642f),
                        "adobe" or "untagged-adobe" => (1.398283f, 0f, 0f),
                        _ => (1f, 0f, 0f),
                    };
                    Near(red, expected, 0.018f, name + " decoded red");
                    _photo.SetImage(image, name.GetHashCode());
                    _photo.FitToView();
                    RequireSmoke(_photo.TryReadDisplayPixel(1.0 / 6, 0.175, out var gpu), "无法读取 GPU 色块");
                    Near(gpu, red, 0.008f, name + " GPU preserves extended linear color");
                    checks.Add($"PASS JPEG {name} {(edge is null ? "Full" : "Proxy")}: F16 red={red}; GPU={gpu}");
                }
                using var thumbnail = await decoder.DecodeThumbnailAsync(file, 120, CancellationToken.None)
                    ?? throw new InvalidOperationException("JPEG 缩略图失败: " + name);
                RequireSmoke(thumbnail.Encoding == PixelEncoding.Bgra32 && thumbnail.ColorSpace == ImageColorSpace.Srgb,
                    "缩略图不是显式 sRGB: " + name);
            }

            var p3Profile = ReadProfile(Path.Combine("Fixtures", "jpeg", "color-p3.jpg"));
            var srgbProfile = ReadProfile(Path.Combine("Fixtures", "jpeg", "color-srgb.jpg"));
            var p3 = await Task.Run(() => WindowsDisplayTransform.FromProfile(p3Profile, "acceptance P3 monitor"));
            var srgb = await Task.Run(() => WindowsDisplayTransform.FromProfile(srgbProfile, "acceptance sRGB monitor"));
            Near(p3.MapLinear(1, 0, 0), (0.822593f, 0.0332f, 0.017085f), 0.009f, "sRGB red on P3 monitor");
            Near(p3.MapLinear(1.224745f, -0.042058f, -0.019642f), (1, 0, 0), 0.009f, "P3 red must not clip to sRGB first");
            Near(srgb.MapLinear(0.25f, 0.25f, 0.25f), (0.25f, 0.25f, 0.25f), 0.005f, "sRGB monitor gray");
            float last = -1;
            for (int i = 0; i <= 255; i++)
            {
                float value = WindowsDisplayTransform.Decode(i / 255f);
                var gray = srgb.MapLinear(value, value, value);
                RequireSmoke(gray.R + 0.0001 >= last, "显示器灰阶 LUT 非单调");
                Near(gray, (value, value, value), 0.006f, "16-bit ICC LUT gray ramp");
                last = gray.R;
            }
            checks.Add("PASS skcms matrix/TRC 65-cube LUT: sRGB/P3 monitor reference, extended red and 256-step neutral monotonic ramp");
            var wcs = await Task.Run(() => WindowsDisplayTransform.FromProfile(srgbProfile, "acceptance WCS", useWcs: true));
            Near(wcs.MapLinear(0.25f, 0.25f, 0.25f), (0.25f, 0.25f, 0.25f), 0.006f, "WCS 16-bit source/target range");
            Near(wcs.MapLinear(0, 0, 0), (0, 0, 0), 0.003f, "WCS black");
            Near(wcs.MapLinear(1, 1, 1), (1, 1, 1), 0.006f, "WCS white");
            checks.Add("PASS WCS fallback: native 16-bit COLOR stride/range and black/midgray/white reference");
            foreach (string name in new[] { "srgb", "p3" })
            {
                var file = new LocalFileRef(Path.GetFullPath(Path.Combine("Fixtures", "heif", "color-" + name + ".heic")));
                using var fast = await decoder.DecodeAsync(file, 600, PixelEncoding.RgbaF16, CancellationToken.None)
                    ?? throw new InvalidOperationException("HEIF 主图解码失败");
                using var fallback = await LibHeifDecoder.DecodePresentationAsync(file, 600, CancellationToken.None)
                    ?? throw new InvalidOperationException("HEIF 强制回落失败: " + HeifLoader.LastDecodeError);
                foreach (var uv in new[] { (1.0 / 6, 0.175), (0.5, 0.175), (5.0 / 6, 0.175), (0.5, 0.8) })
                    Near(Pixel(fast, uv.Item1, uv.Item2), Pixel(fallback, uv.Item1, uv.Item2), 0.025f, "HEIF fast/fallback " + name);
                RequireSmoke(fallback.ColorSpace == ImageColorSpace.LinearSrgb && fallback.Encoding == PixelEncoding.RgbaF16,
                    "HEIF 回落丢失浮点工作空间");
                _photo.SetImage(fast, name.GetHashCode());
                _photo.FitToView();
                RequireSmoke(_photo.TryReadDisplayPixel(0.5, 0.175, out var gpu), "HEIF 上屏失败");
                Near(gpu, Pixel(fast, 0.5, 0.175), 0.008f, "HEIF actual GPU");
                checks.Add("PASS HEIF " + name + ": real fast/fallback F16 color agreement and GPU upload");
            }
            string rawPath = Path.GetFullPath(Path.Combine("Fixtures", "raw", "A7C01489.ARW"));
            RequireSmoke(File.Exists(rawPath), "RAW 验收夹具缺失");
            if (WindowsRawDecoder.IsAvailable(".arw"))
            {
                var declaration = WindowsRawDecoder.TryGetSourceColor(rawPath)
                    ?? throw new InvalidOperationException("WIC RAW 色彩上下文读取失败");
                using var raw = await Task.Run(() => WindowsRawDecoder.DecodeFull(rawPath, CancellationToken.None, out _))
                    ?? throw new InvalidOperationException("WIC RAW 实际解码失败");
                RequireSmoke(raw.ColorSpace == declaration.ColorSpace && raw.IccProfile.Length == declaration.IccLength
                    && raw.Encoding == PixelEncoding.Bgra32, "WIC RAW 标签与实际声明不一致或虚标精度");
                checks.Add($"PASS WIC RAW source declaration and actual Bgra32 decode: {declaration.ColorSpace}, ICC={declaration.IccLength}B");
                await CheckRawFullResolutionCacheAsync(rawPath, checks);
            }
            else checks.Add("SKIP WIC RAW source declaration: RAW codec not installed");

            using var patch = SolidLinear(1.224745f, -0.042058f, -0.019642f);
            _photo.SetImage(patch, int.MaxValue);
            _photo.FitToView();
            foreach (var transform in new[] { p3, srgb, WindowsDisplayTransform.SystemManaged, WindowsDisplayTransform.ForSystemManaged(2), p3 })
            {
                _photo.SetDisplayTransformForChecks(transform);
                RequireSmoke(_photo.TryReadDisplayPixel(0.5, 0.5, out var gpu), "显示器转换 GPU 回读失败");
                Near(gpu, transform.MapLinear(1.224745f, -0.042058f, -0.019642f), 0.008f, "GPU monitor transform");
                RequireSmoke(_photo.CurrentTextureSize == (320, 200), "换显示器不应重解源图");
            }
            checks.Add("PASS GPU output switching: P3 -> sRGB -> Advanced Color bypass -> P3; same source texture, no double ICC conversion");

            _photo.SetDisplayTransformForChecks(WindowsDisplayColor.Current);
            string scenario = Path.Combine(directory, "p3", "color-p3.jpg");
            await OpenPathsAsync([scenario]);
            await WaitForSmokeAsync(() => _session.Image != null && _main.CurrentFile?.Name == "color-p3.jpg", "真实文件打开上屏");
            await WaitForUploadAsync("色彩主图上传");
            var originalFrame = _session.Image;
            _photo.UpdateDisplayColor();
            RequireSmoke(ReferenceEquals(originalFrame, _session.Image), "输出更新改动了共享源缓存");
            checks.Add("PASS real open/session/upload and display refresh: source instance retained");
            checks.Add("BOUNDARY physical display gamut/calibration, HDR/ACM toggle, Win10 and real cross-monitor comparison require matching hardware; GPU readback is not a colorimeter");
            Environment.ExitCode = 0;
        }
        catch (Exception ex)
        {
            checks.Add("FAIL " + ex);
            NativeStartupLog.Fail("color acceptance", ex);
            Environment.ExitCode = 1;
        }
        finally
        {
            File.WriteAllText(report, JsonSerializer.Serialize(checks, new JsonSerializerOptions { WriteIndented = true }));
            Close();
        }
    }

    private static async Task CheckRawFullResolutionCacheAsync(string path, List<string> checks)
    {
        var file = new LocalFileRef(path);
        var key = file.StorageId.Value;
        BitmapLoader.RemoveCached(key, ImageSizeClass.Proxy);
        BitmapLoader.RemoveCached(key, ImageSizeClass.Full);
        try
        {
            using var proxy = await BitmapLoader.GetBitmapAsync(file, DecodeSizeHint.ProxyTo(7680),
                DecodePriority.P0_Current, CancellationToken.None)
                ?? throw new InvalidOperationException("RAW 源分辨率代理未解码");
            RequireSmoke(proxy.Width == proxy.SourceWidth && proxy.Height == proxy.SourceHeight,
                "RAW 验收请求未取得源分辨率像素");
            using var synchronous = BitmapLoader.TryAcquireCached(file, DecodeSizeHint.Full);
            RequireSmoke(ReferenceEquals(proxy, synchronous), "Full 同步缓存查询未复用源分辨率代理");
            var started = System.Diagnostics.Stopwatch.GetTimestamp();
            using var full = await BitmapLoader.GetBitmapAsync(file, DecodeSizeHint.Full,
                DecodePriority.P0_Current, CancellationToken.None);
            var elapsed = System.Diagnostics.Stopwatch.GetElapsedTime(started).TotalMilliseconds;
            RequireSmoke(ReferenceEquals(proxy, full), "Full 请求重复开发了已有源分辨率 RAW");
            RequireSmoke(!BitmapLoader.IsInCache(key, ImageSizeClass.Full), "复用时不应额外创建 Full 缓存条目");
            checks.Add($"PASS WIC RAW source-resolution proxy reused by synchronous/async Full request: "
                + $"same instance, {proxy.Width}x{proxy.Height}, {proxy.Encoding}, no extra cache entry, request={elapsed:F2}ms");
        }
        finally
        {
            BitmapLoader.RemoveCached(key, ImageSizeClass.Proxy);
            BitmapLoader.RemoveCached(key, ImageSizeClass.Full);
        }
    }

    private static (float R, float G, float B) Pixel(DecodedImage image, double u, double v)
    {
        int x = Math.Clamp((int)(image.Width * u), 0, image.Width - 1), y = Math.Clamp((int)(image.Height * v), 0, image.Height - 1);
        var values = MemoryMarshal.Cast<byte, Half>(image.Pixels.AsSpan(y * image.Stride + x * 8, 8));
        return ((float)values[0], (float)values[1], (float)values[2]);
    }

    private static void Near((float R, float G, float B) actual, (float R, float G, float B) expected, float tolerance, string message)
    {
        RequireSmoke(Math.Abs(actual.R - expected.R) <= tolerance && Math.Abs(actual.G - expected.G) <= tolerance
            && Math.Abs(actual.B - expected.B) <= tolerance, $"{message}: actual={actual}; expected={expected}; tolerance={tolerance}");
    }

    private static byte[] ReadProfile(string path)
    {
        using var stream = File.OpenRead(path);
        return JpegColorMetadata.Read(stream).IccProfile
            ?? throw new InvalidOperationException("ICC 夹具缺 profile");
    }

    private static string ClassifyManagedColor(float r, float g, float b, bool linear)
    {
        (string Name, float R, float G, float B)[] palette = [("红", 1, 0, 0), ("绿", 0, 1, 0), ("蓝", 0, 0, 1), ("黄", 1, 1, 0)];
        string best = string.Empty;
        double distance = double.MaxValue;
        foreach (var entry in palette)
        {
            var expected = linear ? WindowsDisplayColor.Current.MapLinear(entry.R, entry.G, entry.B)
                : WindowsDisplayColor.Current.MapToDevice(entry.R, entry.G, entry.B);
            double d = Math.Pow(r - Math.Clamp(expected.R, 0, 1), 2) + Math.Pow(g - Math.Clamp(expected.G, 0, 1), 2)
                + Math.Pow(b - Math.Clamp(expected.B, 0, 1), 2);
            if (d < distance) { distance = d; best = entry.Name; }
        }
        return best;
    }

    private static DecodedImage SolidLinear(float r, float g, float b)
    {
        var pixels = new byte[320 * 200 * 8];
        var halves = MemoryMarshal.Cast<byte, Half>(pixels.AsSpan());
        for (int at = 0; at < halves.Length; at += 4)
        {
            halves[at] = (Half)r; halves[at + 1] = (Half)g; halves[at + 2] = (Half)b; halves[at + 3] = (Half)1;
        }
        return new DecodedImage(pixels, 320, 200, 320 * 8, PixelEncoding.RgbaF16, ImageColorSpace.LinearSrgb);
    }
}
